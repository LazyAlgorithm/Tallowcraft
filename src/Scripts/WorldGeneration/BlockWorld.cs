using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Tallow;

namespace WorldGeneration;

/// <summary>
/// Locally streams chunk meshes. Only the server edits voxels; the scene's
/// NetworkIdentity replicates a bounded, atomic edit snapshot to every peer.
/// Generated chunk GameObjects are deliberately local/transient, never network objects.
/// </summary>
public class BlockWorld : Script
{
    public GameObject Player;
    public GameObject ViewCamera;

    public int Seed = 42;
    // Conservative default streaming radius; near chunks take priority before
    // distant scenery. These remain adjustable in the BlockWorld Inspector.
    public int RenderRadius = 6;
    public int SideRadius = 4;
    public int BehindRadius = 2;
    public int EditableRadius = 2;
    public int KeepMargin = 1;
    public int WorkerCount = 3;
    public int UploadsPerFrame = 3;
    public int VerticalRadius = 1;
    // Drawing a distant mountain must not require distant chunk meshes to
    // render again in the cascaded sun-shadow passes.
    public bool LimitChunkShadows = true;
    public int ShadowRadius = 5;

    // Diagnostic only: synchronous mesh and collider entry-point timings.
    // Native Jolt cooking deferred to physics frames is NOT measured here.
    public bool ProfileChunkColliders = true;
    public int ProfileReportEvery = 32;
    public bool ProfileStreaming = true;
    public int StreamReportEvery = 64;
    private int _streamUploads;
    private double _streamLatencyTotal;
    private double _streamLatencyMax;
    private readonly int[] _profileCounts = new int[4];
    private readonly double[] _profileMilliseconds = new double[4];
    private readonly double[] _profileMaximum = new double[4];
    private int _profileEvents;

    // 0: mesh upload, 1: MeshCollider add, 2: refresh, 3: remove.
    private void RecordChunkProfile(int operation, long start)
    {
        if (!ProfileChunkColliders || start == 0) return;
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _profileCounts[operation]++;
        _profileMilliseconds[operation] += elapsed;
        _profileMaximum[operation] = Math.Max(_profileMaximum[operation], elapsed);
        if (++_profileEvents < Math.Max(1, ProfileReportEvery)) return;

        string[] names = { "SetMesh", "ColliderAdd", "ColliderRefresh", "ColliderRemove" };
        Console.WriteLine("[Tallowcraft chunk profile] synchronous call timings (deferred Jolt work excluded):");
        for (int i = 0; i < 4; i++)
        {
            int count = _profileCounts[i];
            if (count > 0)
                Console.WriteLine($"  {names[i]} count={count} avg_ms={_profileMilliseconds[i] / count:F3} max_ms={_profileMaximum[i]:F3} total_ms={_profileMilliseconds[i]:F3}");
        }
        Array.Clear(_profileCounts);
        Array.Clear(_profileMilliseconds);
        Array.Clear(_profileMaximum);
        _profileEvents = 0;
    }

    private long ProfileStart() => ProfileChunkColliders ? Stopwatch.GetTimestamp() : 0;

    private void RecordStreamLatency(ChunkJob job)
    {
        if (!ProfileStreaming) return;
        double elapsed = Stopwatch.GetElapsedTime(job.EnqueuedAt).TotalMilliseconds;
        _streamLatencyTotal += elapsed;
        _streamLatencyMax = Math.Max(_streamLatencyMax, elapsed);
        if (++_streamUploads < Math.Max(1, StreamReportEvery)) return;
        Console.WriteLine(
            $"[Tallowcraft stream profile] uploads={_streamUploads} " +
            $"queue_to_upload_avg_ms={_streamLatencyTotal / _streamUploads:F1} " +
            $"max_ms={_streamLatencyMax:F1}");
        _streamUploads = 0;
        _streamLatencyTotal = 0;
        _streamLatencyMax = 0;
    }

    // Empty on newly connected clients until the authoritative snapshot arrives.
    // Includes world seed; the two cannot disagree while joining.
    [Sync] public string EditState = "";

    private readonly BlockEdits _edits = new();
    private readonly Dictionary<ChunkKey, LiveChunk> _loaded = new();
    private readonly Dictionary<ChunkKey, ChunkJob> _jobs = new();
    private readonly Dictionary<ChunkKey, int> _versions = new();
    private readonly Dictionary<ulong, ChunkKey> _chunkObjects = new();
    private readonly HashSet<ChunkKey> _colliders = new();
    private readonly HashSet<ChunkKey> _shadowCasters = new();
    private readonly List<ChunkKey> _scratch = new();
    // Reused candidate buffer. Find priorities cheaply first, then only
    // evaluate costly biome/tree/structure existence for slots we can queue.
    private readonly List<(ChunkKey Key, long Distance)> _candidates = new();
    private readonly HashSet<ChunkKey> _candidateKeys = new();
    private readonly List<ChunkKey> _playerCenters = new();
    private readonly List<float> _playerHeights = new();
    // Y=0 air edits expose the negative-Y layer from the surface.
    private readonly HashSet<ChunkKey> _exposedBelow = new();
    private float _viewerWorldY;
    private readonly CancellationTokenSource _stop = new();

    private Vector2 _lastForward = Vector2.UnitY; // 2D Y represents world Z.
    private string _appliedSnapshot = "";
    private string _rejectedSnapshot = "";
    private int _publishedSeed;

    public override void Start()
    {
        if (Scene.IsServer)
        {
            _publishedSeed = Seed;
            EditState = _edits.Serialize(Seed);
        }
    }

    // Rendering, chunk jobs and synchronized snapshots are local per machine,
    // including clients that do not execute a server-owned NetworkIdentity's
    // networked Update callback. Never run streaming during prediction replay.
    public override void LocalUpdate(float deltaTime)
    {
        // Never generate a client's base terrain before getting the server's
        // seed AND modified blocks in one authoritative snapshot.
        if (Scene.IsServer)
        {
            if (Seed != _publishedSeed || EditState.Length == 0)
            {
                _publishedSeed = Seed;
                EditState = _edits.Serialize(Seed);
                MarkAllDirty();
            }
        }
        else
        {
            if (EditState.Length == 0)
                return;
            ApplyIncomingSnapshot();
            if (_appliedSnapshot.Length == 0)
                return;
        }

        GameObject anchor = Viewer();
        GameObject view = ViewCamera.IsValid ? ViewCamera : Scene.Find("Main Camera") ?? anchor;
        Vector3 position = anchor.IsValid ? anchor.Transform.Position : Vector3.Zero;
        Vector3 forward = view.IsValid ? view.Transform.Forward : Vector3.UnitZ;
        Vector2 flat = new(forward.X, forward.Z);

        if (flat.LengthSquared() > 0.0001f)
            _lastForward = Vector2.Normalize(flat);

        int cy = Math.Clamp(ChunkGenerator.FloorDiv((int)MathF.Floor(position.Y), ChunkGenerator.Size),
            ChunkGenerator.MinChunkY, ChunkGenerator.MaxChunkY);
        _viewerWorldY = position.Y;
        int cx = ChunkGenerator.FloorDiv((int)MathF.Floor(position.X), ChunkGenerator.Size);
        int cz = ChunkGenerator.FloorDiv((int)MathF.Floor(position.Z), ChunkGenerator.Size);

        GatherCollisionCenters(cx, cy, cz);
        CancelObsoleteJobs(cx, cy, cz);
        UnloadDistant(cx, cy, cz);
        UploadFinished(cx, cy, cz);
        UpdateColliders(cx, cy, cz);
        MaintainBlockCache(cx, cy, cz);
        StartNearestJobs(cx, cy, cz);
    }

    private GameObject Viewer()
    {
        // A client's camera must follow its OWN player, not an arbitrary scene object.
        if (Scene.IsNetworked &&
            Network.PlayerObject(Scene, Scene.LocalClientId) is { } local &&
            local.IsValid)
            return local;

        return Player.IsValid ? Player : ViewCamera.IsValid ? ViewCamera :
            Scene.Find("Main Camera") ?? GameObject;
    }

    private void GatherCollisionCenters(int cx, int cy, int cz)
    {
        _playerCenters.Clear();
        _playerHeights.Clear();

        if (Scene.IsServer && Scene.IsNetworked)
        {
            AddPlayerCenter(Network.PlayerObject(Scene, 0));
            foreach (int clientId in Network.ClientIds(Scene))
                AddPlayerCenter(Network.PlayerObject(Scene, clientId));
        }

        if (_playerCenters.Count == 0 || !Scene.IsServer)
        {
            _playerCenters.Add(new ChunkKey(cx, cy, cz));
            _playerHeights.Add(_viewerWorldY);
        }
    }

    private void AddPlayerCenter(GameObject? player)
    {
        if (player is not { } p || !p.IsValid)
            return;

        Vector3 position = p.Transform.Position;
        _playerHeights.Add(position.Y);
        _playerCenters.Add(new ChunkKey(
            ChunkGenerator.FloorDiv((int)MathF.Floor(position.X), ChunkGenerator.Size),
            Math.Clamp(ChunkGenerator.FloorDiv((int)MathF.Floor(position.Y), ChunkGenerator.Size),
                ChunkGenerator.MinChunkY, ChunkGenerator.MaxChunkY),
            ChunkGenerator.FloorDiv((int)MathF.Floor(position.Z), ChunkGenerator.Size)));
    }

    private static bool Near(ChunkKey key, int cx, int cy, int cz, int radius)
    {
        long dx = (long)key.X - cx, dz = (long)key.Z - cz;
        return Math.Abs((long)key.Y - cy) <= Math.Clamp(radius, 0, 2) &&
            dx * dx + dz * dz <= (long)radius * radius;
    }

    private bool HiddenForViewer(ChunkKey key) =>
        ChunkScheduling.HiddenBelowSurface(
            key, _viewerWorldY, _exposedBelow.Contains(key));

    private bool HiddenForPlayer(ChunkKey key, float height) =>
        ChunkScheduling.HiddenBelowSurface(
            key, height, _exposedBelow.Contains(key));

    /// <summary>Track surface shafts on edit changes, not every streaming frame.</summary>
    private void RebuildSurfaceOpenings()
    {
        _exposedBelow.Clear();
        foreach (var edit in _edits.Snapshot)
            if (edit.Key.Y == 0 && edit.Value == 0)
                _exposedBelow.Add(new ChunkKey(
                    ChunkGenerator.FloorDiv(edit.Key.X, ChunkGenerator.Size),
                    -1,
                    ChunkGenerator.FloorDiv(edit.Key.Z, ChunkGenerator.Size)));
    }

    private int CollisionRadius() =>
        Math.Clamp(EditableRadius, 0, Math.Clamp(RenderRadius, 1, 16));

    private bool ShouldStream(ChunkKey key, int cx, int cy, int cz, int extra = 0)
    {
        if (key.Y < ChunkGenerator.MinChunkY || key.Y > ChunkGenerator.MaxChunkY ||
            Math.Abs((long)key.Y - cy) > Math.Clamp(VerticalRadius, 0, 2) + extra ||
            HiddenForViewer(key))
            return false;
        int forwardRange = Math.Clamp(RenderRadius, 1, 16);
        int sideRange = Math.Clamp(SideRadius, 1, forwardRange);
        int backRange = Math.Clamp(BehindRadius, 0, sideRange);
        int nearRange = CollisionRadius();

        float dx = key.X - cx, dz = key.Z - cz;
        float distance = MathF.Sqrt(dx * dx + dz * dz);
        if (distance <= nearRange + extra || distance < 0.0001f)
            return true;

        float facing = (dx * _lastForward.X + dz * _lastForward.Y) / distance;
        float allowed = facing >= 0
            ? sideRange + (forwardRange - sideRange) * facing
            : sideRange + (sideRange - backRange) * facing;

        return distance <= MathF.Max(allowed, nearRange) + extra;
    }

    private bool ShouldKeep(ChunkKey key, int cx, int cy, int cz, int margin = 0)
    {
        if (ShouldStream(key, cx, cy, cz, margin))
            return true;

        // Dedicated/host server must simulate collision around every client,
        // even when that player is far outside the host's viewing direction.
        if (Scene.IsServer)
            for (int i = 0; i < _playerCenters.Count; i++)
            {
                ChunkKey center = _playerCenters[i];
                if (!HiddenForPlayer(key, _playerHeights[i]) &&
                    Near(key, center.X, center.Y, center.Z, CollisionRadius() + margin))
                    return true;
            }

        return false;
    }

    private bool ShouldCastChunkShadow(ChunkKey key, int cx, int cy, int cz)
    {
        if (!LimitChunkShadows)
            return true;
        int radius = Math.Clamp(ShadowRadius, 0, Math.Clamp(RenderRadius, 1, 16));
        long dx = (long)key.X - cx, dz = (long)key.Z - cz;
        return Math.Abs((long)key.Y - cy) <= 1 &&
            dx * dx + dz * dz <= (long)radius * radius;
    }

    private bool ShouldHaveCollider(ChunkKey key, int cx, int cy, int cz, int margin = 0)
    {
        if (!HiddenForViewer(key) &&
            Near(key, cx, cy, cz, CollisionRadius() + margin))
            return true;
        if (Scene.IsServer)
            for (int i = 0; i < _playerCenters.Count; i++)
            {
                ChunkKey center = _playerCenters[i];
                if (!HiddenForPlayer(key, _playerHeights[i]) &&
                    Near(key, center.X, center.Y, center.Z, CollisionRadius() + margin))
                    return true;
            }
        return false;
    }

    private void CancelObsoleteJobs(int cx, int cy, int cz)
    {
        foreach (var pair in _jobs)
        {
            if (pair.Value.Task.IsCompleted ||
                (pair.Value.Version == VersionFor(pair.Key) &&
                 ShouldKeep(pair.Key, cx, cy, cz)))
                continue;
            pair.Value.Cancellation.Cancel();
        }
    }

    private void UnloadDistant(int cx, int cy, int cz)
    {
        _scratch.Clear();
        foreach (var pair in _loaded)
        {
            if (ShouldKeep(pair.Key, cx, cy, cz, Math.Clamp(KeepMargin, 0, 3)))
                continue;

            if (pair.Value.Object.IsValid)
            {
                pair.Value.Renderer.ClearMesh();
                pair.Value.Object.Destroy();
            }

            _chunkObjects.Remove(pair.Value.Object.Id);
            _colliders.Remove(pair.Key);
            _shadowCasters.Remove(pair.Key);
            _scratch.Add(pair.Key);
        }

        foreach (ChunkKey key in _scratch)
            _loaded.Remove(key);
    }

    private void UploadFinished(int cx, int cy, int cz)
    {
        int processed = 0;
        _scratch.Clear();

        foreach (var pair in _jobs)
        {
            if (processed >= Math.Clamp(UploadsPerFrame, 1, 4))
                break;

            ChunkJob job = pair.Value;
            if (!job.Task.IsCompleted)
                continue;

            processed++;
            _scratch.Add(pair.Key);
            job.Cancellation.Dispose();

            if (job.Task.IsFaulted)
            {
                Console.WriteLine($"Chunk {pair.Key} failed: {job.Task.Exception}");
                continue;
            }

            if (!job.Task.IsCompletedSuccessfully ||
                job.Version != VersionFor(pair.Key) ||
                !ShouldKeep(pair.Key, cx, cy, cz))
                continue;

            ChunkMesh data = job.Task.Result;
            RecordStreamLatency(job);

            if (_loaded.TryGetValue(pair.Key, out LiveChunk? live))
            {
                if (!live.Object.IsValid)
                    continue;

                long meshStart = ProfileStart();
                live.Renderer.SetMesh(data.Vertices, data.Triangles, data.Normals, data.Uvs);
                RecordChunkProfile(0, meshStart);
                if (_colliders.Contains(pair.Key) &&
                    live.Object.GetComponent<MeshCollider>() is { } collider &&
                    collider.IsValid)
                {
                    long refreshStart = ProfileStart();
                    collider.Refresh();
                    RecordChunkProfile(2, refreshStart);
                }

                live.Object.Entity.Set(new ChunkInfo
                {
                    X = pair.Key.X, Y = pair.Key.Y, Z = pair.Key.Z,
                    FaceCount = data.Triangles.Length / 6
                });
                live.Blocks = ShouldHaveCollider(pair.Key, cx, cy, cz) ? data.Blocks : null;
                live.Dirty = false;
                continue;
            }

            GameObject obj = Scene.CreateGameObject($"Chunk {pair.Key.X}, {pair.Key.Y}, {pair.Key.Z}");
            obj.Transient = true;
            obj.Transform.Position = new Vector3(
                pair.Key.X * ChunkGenerator.Size, pair.Key.Y * ChunkGenerator.Size,
                pair.Key.Z * ChunkGenerator.Size);

            MeshRenderer renderer = obj.AddComponent<MeshRenderer>();
            renderer.FlatShading = true;
            renderer.Texture = BlockTextureAtlas.Path;
            bool castChunkShadow = ShouldCastChunkShadow(pair.Key, cx, cy, cz);
            renderer.CastShadows = castChunkShadow;
            if (castChunkShadow)
                _shadowCasters.Add(pair.Key);
            long newMeshStart = ProfileStart();
            renderer.SetMesh(data.Vertices, data.Triangles, data.Normals, data.Uvs);
            RecordChunkProfile(0, newMeshStart);

            bool collision = ShouldHaveCollider(pair.Key, cx, cy, cz);
            if (collision)
            {
                long colliderStart = ProfileStart();
                obj.AddComponent<MeshCollider>();
                RecordChunkProfile(1, colliderStart);
                _colliders.Add(pair.Key);
            }

            obj.Entity.Set(new ChunkInfo
            {
                X = pair.Key.X, Y = pair.Key.Y, Z = pair.Key.Z,
                FaceCount = data.Triangles.Length / 6
            });
            _chunkObjects[obj.Id] = pair.Key;
            _loaded.Add(pair.Key,
                new LiveChunk(obj, renderer, collision ? data.Blocks : null));
        }

        foreach (ChunkKey key in _scratch)
            _jobs.Remove(key);
    }

    private void UpdateColliders(int cx, int cy, int cz)
    {
        foreach (var pair in _loaded)
        {
            ChunkKey key = pair.Key;
            GameObject obj = pair.Value.Object;
            if (!obj.IsValid)
                continue;

            bool shouldCastShadow = ShouldCastChunkShadow(key, cx, cy, cz);
            if (shouldCastShadow != _shadowCasters.Contains(key))
            {
                pair.Value.Renderer.CastShadows = shouldCastShadow;
                if (shouldCastShadow)
                    _shadowCasters.Add(key);
                else
                    _shadowCasters.Remove(key);
            }

            bool has = _colliders.Contains(key);
            if (!has && ShouldHaveCollider(key, cx, cy, cz))
            {
                long colliderStart = ProfileStart();
                obj.AddComponent<MeshCollider>();
                RecordChunkProfile(1, colliderStart);
                _colliders.Add(key);
            }
            else if (has && !ShouldHaveCollider(key, cx, cy, cz, 1))
            {
                long removeStart = ProfileStart();
                obj.RemoveComponent<MeshCollider>();
                RecordChunkProfile(3, removeStart);
                _colliders.Remove(key);
            }
        }
    }

    private void MaintainBlockCache(int cx, int cy, int cz)
    {
        var snapshot = _edits.Snapshot;
        foreach (var pair in _loaded)
        {
            if (ShouldHaveCollider(pair.Key, cx, cy, cz))
                pair.Value.Blocks ??=
                    ChunkGenerator.CreateBlocks(pair.Key, Seed, snapshot);
            else
                pair.Value.Blocks = null;
        }
    }

    private int VersionFor(ChunkKey key) =>
        _versions.TryGetValue(key, out int version) ? version : 0;

    private void MarkDirty(ChunkKey key)
    {
        _versions[key] = VersionFor(key) + 1;
        if (_loaded.TryGetValue(key, out LiveChunk? chunk))
            chunk.Dirty = true;
    }

    private void MarkAllDirty()
    {
        foreach (ChunkKey key in _loaded.Keys)
            MarkDirty(key);
        foreach (ChunkKey key in _jobs.Keys)
            MarkDirty(key);
    }

    private void MarkBlockDirty(BlockPosition position)
    {
        int size = ChunkGenerator.Size;
        int x = ChunkGenerator.FloorDiv(position.X, size);
        int y = ChunkGenerator.FloorDiv(position.Y, size);
        int z = ChunkGenerator.FloorDiv(position.Z, size);
        int localX = position.X - x * size;
        int localY = position.Y - y * size;
        int localZ = position.Z - z * size;

        MarkDirty(new ChunkKey(x, y, z));
        if (localX == 0) MarkDirty(new ChunkKey(x - 1, y, z));
        if (localX == size - 1) MarkDirty(new ChunkKey(x + 1, y, z));
        if (localY == 0) MarkDirty(new ChunkKey(x, y - 1, z));
        if (localY == size - 1) MarkDirty(new ChunkKey(x, y + 1, z));
        if (localZ == 0) MarkDirty(new ChunkKey(x, y, z - 1));
        if (localZ == size - 1) MarkDirty(new ChunkKey(x, y, z + 1));
    }

    private void ApplyIncomingSnapshot()
    {
        if (EditState == _appliedSnapshot || EditState == _rejectedSnapshot)
            return;

        int previousSeed = Seed;
        if (!_edits.TryLoad(EditState, out int newSeed, out List<BlockPosition> changed))
        {
            _rejectedSnapshot = EditState;
            Console.WriteLine("Rejected invalid Tallowcraft world snapshot.");
            return;
        }

        Seed = newSeed;
        if (newSeed != previousSeed)
            MarkAllDirty();
        foreach (BlockPosition position in changed)
            MarkBlockDirty(position);
        RebuildSurfaceOpenings();
        _appliedSnapshot = EditState;
    }

    private void StartNearestJobs(int cx, int cy, int cz)
    {
        int workers = Math.Clamp(WorkerCount, 1, 4);
        if (_jobs.Count >= workers)
            return;

        int radius = Math.Clamp(RenderRadius, 1, 16);
        int nearRadius = CollisionRadius();
        int vertical = Math.Clamp(VerticalRadius, 0, 2);
        _candidates.Clear();
        _candidateKeys.Clear();

        void Consider(ChunkKey key)
        {
            // Do not run expensive HasPotentialBlocks/biome queries while
            // scanning. Most candidates will never be launched this frame.
            if (_jobs.ContainsKey(key))
                return;
            if (_loaded.TryGetValue(key, out LiveChunk? live) && !live.Dirty)
                return;
            if (!_candidateKeys.Add(key))
                return;
            _candidates.Add((key,
                ChunkScheduling.Priority(key, cx, cy, cz, nearRadius)));
        }

        // Visible chunks first. The candidate order is irrelevant: priority
        // is calculated in world/chunk coordinates and sorted below.
        for (int dy = -vertical; dy <= vertical; dy++)
        {
            int layer = cy + dy;
            if (layer < ChunkGenerator.MinChunkY ||
                layer > ChunkGenerator.MaxChunkY)
                continue;
            for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                ChunkKey key = new(cx + dx, layer, cz + dz);
                if (ShouldStream(key, cx, cy, cz))
                    Consider(key);
            }
        }

        // Keep collision around remote players even when outside the local
        // camera, but don't queue their sealed underground layers either.
        if (Scene.IsServer)
            for (int playerIndex = 0; playerIndex < _playerCenters.Count; playerIndex++)
            {
                ChunkKey center = _playerCenters[playerIndex];
                float remoteHeight = _playerHeights[playerIndex];
                for (int dy = -vertical; dy <= vertical; dy++)
                for (int dz = -nearRadius; dz <= nearRadius; dz++)
                for (int dx = -nearRadius; dx <= nearRadius; dx++)
                {
                    int layer = center.Y + dy;
                    if (layer < ChunkGenerator.MinChunkY ||
                        layer > ChunkGenerator.MaxChunkY ||
                        (long)dx * dx + (long)dz * dz >
                            (long)nearRadius * nearRadius)
                        continue;
                    ChunkKey key = new(center.X + dx, layer, center.Z + dz);
                    if (HiddenForPlayer(key, remoteHeight))
                        continue;
                    if (!_jobs.ContainsKey(key) &&
                        (!_loaded.TryGetValue(key, out LiveChunk? live) || live.Dirty) &&
                        _candidateKeys.Add(key))
                        _candidates.Add((key, ChunkScheduling.Priority(
                            key, center.X, center.Y, center.Z, nearRadius)));
                }
            }

        _candidates.Sort((a, b) =>
        {
            int compare = a.Distance.CompareTo(b.Distance);
            if (compare != 0) return compare;
            compare = a.Key.Y.CompareTo(b.Key.Y);
            if (compare != 0) return compare;
            compare = a.Key.X.CompareTo(b.Key.X);
            return compare != 0 ? compare : a.Key.Z.CompareTo(b.Key.Z);
        });

        var snapshot = _edits.Snapshot;
        int seed = Seed;
        foreach (var candidate in _candidates)
        {
            if (_jobs.Count >= workers) break;
            ChunkKey key = candidate.Key;
            if (_jobs.ContainsKey(key))
                continue;
            if (!_loaded.TryGetValue(key, out LiveChunk? live))
            {
                // Expensive check is now restricted to the closest missing
                // candidates, not every chunk in every vertical radius.
                if (!ChunkGenerator.HasPotentialBlocks(key, seed, snapshot))
                    continue;
            }
            else if (!live.Dirty)
                continue;

            int version = VersionFor(key);
            CancellationTokenSource cancellation =
                CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            CancellationToken token = cancellation.Token;
            long enqueuedAt = Stopwatch.GetTimestamp();
            Task<ChunkMesh> task = Task.Run(
                () => ChunkGenerator.GenerateMesh(key, seed, snapshot, token), token);
            _jobs.Add(key, new ChunkJob(task, version, cancellation, enqueuedAt));
        }
    }

    /// <summary>
    /// The shared block targeting query for both preview and server edits.
    /// A client may raycast its local generated collision chunks, but it cannot
    /// change the authoritative edit state. Ignore the player's own capsule.
    /// </summary>
    public bool TryQueryFromRay(Vector3 origin, Vector3 direction, float reach,
        GameObject actingPlayer, out BlockPosition remove, out BlockPosition place) =>
        TryQueryFromRay(origin, direction, reach, actingPlayer,
            out remove, out place, out _);

    public bool TryQueryFromRay(Vector3 origin, Vector3 direction, float reach,
        GameObject actingPlayer, out BlockPosition remove, out BlockPosition place,
        out Vector3 hitNormal)
    {
        remove = default;
        place = default;
        hitNormal = default;
        if (!float.IsFinite(reach) || reach <= 0f ||
            !float.IsFinite(origin.X) || !float.IsFinite(origin.Y) ||
            !float.IsFinite(origin.Z) || !float.IsFinite(direction.X) ||
            !float.IsFinite(direction.Y) || !float.IsFinite(direction.Z) ||
            direction.LengthSquared() < 0.0001f)
            return false;

        direction = Vector3.Normalize(direction);
        PhysicsHit? contact = null;
        foreach (PhysicsHit candidate in Scene.RaycastAll(
            origin, direction, Math.Clamp(reach, 1f, 8f)))
        {
            if (candidate.HitObject is { } target && target.Id == actingPlayer.Id)
                continue;
            contact = candidate;
            break;
        }

        if (contact is not { } hit || hit.Normal is not Vector3 normal ||
            hit.HitObject is not { } objectHit || !_chunkObjects.ContainsKey(objectHit.Id))
            return false;

        // Inside the struck half-cell, then advance a whole block in the
        // normal's direction. This is also the exact targeting rule used by
        // server placement, including stairs and lower/upper slabs.
        Vector3 inside = hit.Point - normal * 0.01f;
        remove = new BlockPosition((int)MathF.Floor(inside.X),
            (int)MathF.Floor(inside.Y), (int)MathF.Floor(inside.Z));
        place = new BlockPosition(remove.X + (int)MathF.Round(normal.X),
            remove.Y + (int)MathF.Round(normal.Y),
            remove.Z + (int)MathF.Round(normal.Z));
        hitNormal = normal;
        return ChunkGenerator.InWorld(remove.Y) &&
            _edits.Get(remove.X, remove.Y, remove.Z, Seed) != 0;
    }

    public ushort BlockAt(BlockPosition position) =>
        _edits.Get(position.X, position.Y, position.Z, Seed);

    /// <summary>
    /// Conservative spawn clearance, using authoritative edited terrain at
    /// the spawn X/Z position. Checks all columns touched by the controller
    /// radius and all occupied half-cells (including slabs and stairs).
    /// The returned Y is the center of a standing capsule, not its feet.
    /// </summary>
    public float SafeSpawnHeight(float x, float z, float standingHeight, float radius = 0.5f) =>
        ChunkGenerator.SpawnHeight(x, z, Seed, _edits.Snapshot, standingHeight, radius);

    /// <summary>Shared, side-effect-free placement rule for preview and server.</summary>
    public bool TryPlanPlacement(BlockPosition hit, BlockPosition adjacent,
        Vector3 hitNormal, BlockState selected,
        out BlockPosition target, out ushort replacement) =>
        BlockPlacement.TryPlan(hit, adjacent, hitNormal,
            new BlockState(BlockAt(hit)), selected, out target, out replacement);

    /// <summary>
    /// Validate a proposed state change against the voxel contents and nearby
    /// physics bodies. Check only each newly-filled half-cell, with a small
    /// gap to avoid false overlaps with terrain in neighboring cells.
    /// </summary>
    public bool CanPlaceAt(BlockPosition position, ushort replacement)
    {
        if (!ChunkGenerator.InWorld(position.Y))
            return false;

        ushort previous = BlockAt(position);
        if (!BlockPlacement.CanReplace(previous, replacement))
            return false;

        for (int y = 0; y < 2; y++)
            for (int z = 0; z < 2; z++)
                for (int x = 0; x < 2; x++)
                {
                    if (!BlockPlacement.AddsMicro(previous, replacement, x, y, z))
                        continue;

                    Vector3 center = new(position.X + (x == 0 ? 0.25f : 0.75f),
                        position.Y + (y == 0 ? 0.25f : 0.75f),
                        position.Z + (z == 0 ? 0.25f : 0.75f));
                    if (Scene.CheckBox(center, new Vector3(0.45f), Quaternion.Identity))
                        return false;
                }
        return true;
    }

    /// <summary>Server-authoritative edit using the same target as preview.</summary>
    public bool TryEditFromRay(Vector3 origin, Vector3 direction, float reach,
        bool breaking, GameObject actingPlayer, ushort placeState)
    {
        if (!Scene.IsServer ||
            (!breaking && (placeState == 0 || !BlockState.IsValid(placeState))) ||
            !TryQueryFromRay(origin, direction, reach, actingPlayer,
                out BlockPosition remove, out BlockPosition adjacent,
                out Vector3 hitNormal))
            return false;

        BlockPosition target;
        ushort replacement;
        if (breaking)
        {
            target = remove;
            replacement = 0;
            if (BlockAt(target) == 0)
                return false;
        }
        else
        {
            if (!TryPlanPlacement(remove, adjacent, hitNormal,
                    new BlockState(placeState), out target, out replacement) ||
                !CanPlaceAt(target, replacement))
                return false;
        }

        if (!_edits.Set(target, replacement, Seed))
            return false;

        MarkBlockDirty(target);
        if (target.Y == 0)
            RebuildSurfaceOpenings();
        EditState = _edits.Serialize(Seed);
        return true;
    }

    public override void OnDestroy()
    {
        _stop.Cancel();
        foreach (ChunkJob job in _jobs.Values)
            job.Cancellation.Cancel();
        foreach (LiveChunk chunk in _loaded.Values)
        {
            if (!chunk.Object.IsValid)
                continue;
            chunk.Renderer.ClearMesh();
            chunk.Object.Destroy();
        }

        _loaded.Clear();
        _jobs.Clear();
        _colliders.Clear();
        _shadowCasters.Clear();
        _chunkObjects.Clear();
        _versions.Clear();
    }
}
