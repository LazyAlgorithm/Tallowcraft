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
    public int RenderRadius = 8;
    public int SideRadius = 4;
    public int BehindRadius = 2;
    public int EditableRadius = 2;
    public int KeepMargin = 1;
    public int WorkerCount = 2;
    public int UploadsPerFrame = 1;
    public int VerticalRadius = 1;

    // Diagnostic only: synchronous mesh and collider entry-point timings.
    // Native Jolt cooking deferred to physics frames is NOT measured here.
    public bool ProfileChunkColliders = true;
    public int ProfileReportEvery = 32;
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

    // Empty on newly connected clients until the authoritative snapshot arrives.
    // Includes world seed; the two cannot disagree while joining.
    [Sync] public string EditState = "";

    private readonly BlockEdits _edits = new();
    private readonly Dictionary<ChunkKey, LiveChunk> _loaded = new();
    private readonly Dictionary<ChunkKey, ChunkJob> _jobs = new();
    private readonly Dictionary<ChunkKey, int> _versions = new();
    private readonly Dictionary<ulong, ChunkKey> _chunkObjects = new();
    private readonly HashSet<ChunkKey> _colliders = new();
    private readonly List<ChunkKey> _scratch = new();
    private readonly List<ChunkKey> _playerCenters = new();
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
        int cx = ChunkGenerator.FloorDiv((int)MathF.Floor(position.X), ChunkGenerator.Size);
        int cz = ChunkGenerator.FloorDiv((int)MathF.Floor(position.Z), ChunkGenerator.Size);

        GatherCollisionCenters(cx, cy, cz);
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

        if (Scene.IsServer && Scene.IsNetworked)
        {
            AddPlayerCenter(Network.PlayerObject(Scene, 0));
            foreach (int clientId in Network.ClientIds(Scene))
                AddPlayerCenter(Network.PlayerObject(Scene, clientId));
        }

        if (_playerCenters.Count == 0 || !Scene.IsServer)
            _playerCenters.Add(new ChunkKey(cx, cy, cz));
    }

    private void AddPlayerCenter(GameObject? player)
    {
        if (player is not { } p || !p.IsValid)
            return;

        Vector3 position = p.Transform.Position;
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

    private int CollisionRadius() =>
        Math.Clamp(EditableRadius, 0, Math.Clamp(RenderRadius, 1, 16));

    private bool ShouldStream(ChunkKey key, int cx, int cy, int cz, int extra = 0)
    {
        if (key.Y < ChunkGenerator.MinChunkY || key.Y > ChunkGenerator.MaxChunkY ||
            Math.Abs((long)key.Y - cy) > Math.Clamp(VerticalRadius, 0, 2) + extra)
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
            foreach (ChunkKey center in _playerCenters)
                if (Near(key, center.X, center.Y, center.Z, CollisionRadius() + margin))
                    return true;

        return false;
    }

    private bool ShouldHaveCollider(ChunkKey key, int cx, int cy, int cz, int margin = 0)
    {
        if (Near(key, cx, cy, cz, CollisionRadius() + margin))
            return true;
        if (Scene.IsServer)
            foreach (ChunkKey center in _playerCenters)
                if (Near(key, center.X, center.Y, center.Z, CollisionRadius() + margin))
                    return true;
        return false;
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
        _appliedSnapshot = EditState;
    }

    private void StartNearestJobs(int cx, int cy, int cz)
    {
        int radius = Math.Clamp(RenderRadius, 1, 16);
        int workers = Math.Clamp(WorkerCount, 1, 4);
        int nearRadius = CollisionRadius();

        while (_jobs.Count < workers)
        {
            ChunkKey? nearest = null;
            long bestDistance = long.MaxValue;

            void Consider(ChunkKey key, long distance)
            {
                if (!ShouldKeep(key, cx, cy, cz) || _jobs.ContainsKey(key))
                    return;
                if (_loaded.TryGetValue(key, out LiveChunk? live) && !live.Dirty)
                    return;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearest = key;
                }
            }

            // Prioritize chunks at the viewer's height, then nearby vertical
            // layers; keep horizontal directional streaming unchanged.
            int vertical = Math.Clamp(VerticalRadius, 0, 2);
            for (int dy = -vertical; dy <= vertical; dy++)
            {
                int layer = cy + dy;
                if (layer < ChunkGenerator.MinChunkY || layer > ChunkGenerator.MaxChunkY)
                    continue;
                for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    ChunkKey key = new(cx + dx, layer, cz + dz);
                    if (ShouldStream(key, cx, cy, cz))
                        Consider(key, (long)dx * dx + (long)dz * dz +
                            (long)dy * dy * 4);
                }
            }

            // A headless/server world also maintains collision neighborhoods
            // centered around remote players' true 3D chunk coordinates.
            if (Scene.IsServer)
                foreach (ChunkKey center in _playerCenters)
                    for (int dy = -vertical; dy <= vertical; dy++)
                    for (int dz = -nearRadius; dz <= nearRadius; dz++)
                    for (int dx = -nearRadius; dx <= nearRadius; dx++)
                    {
                        int layer = center.Y + dy;
                        if (layer < ChunkGenerator.MinChunkY ||
                            layer > ChunkGenerator.MaxChunkY ||
                            (long)dx * dx + (long)dz * dz > (long)nearRadius * nearRadius)
                            continue;
                        Consider(new ChunkKey(center.X + dx, layer, center.Z + dz),
                            (long)dx * dx + (long)dz * dz + (long)dy * dy * 4);
                    }

            if (nearest is not { } next)
                break;

            var snapshot = _edits.Snapshot;
            int seed = Seed;
            int version = VersionFor(next);
            CancellationToken token = _stop.Token;
            Task<ChunkMesh> task = Task.Run(
                () => ChunkGenerator.GenerateMesh(next, seed, snapshot, token), token);
            _jobs.Add(next, new ChunkJob(task, version));
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
    public float SafeSpawnHeight(float x, float z, float standingHeight, float radius = 0.5f)
    {
        int minX = (int)MathF.Floor(x - radius);
        int maxX = (int)MathF.Floor(x + radius);
        int minZ = (int)MathF.Floor(z - radius);
        int maxZ = (int)MathF.Floor(z + radius);
        float highest = ChunkGenerator.MinWorldY;
        for (int bx = minX; bx <= maxX; bx++)
        for (int bz = minZ; bz <= maxZ; bz++)
        for (int y = ChunkGenerator.MinWorldY; y <= ChunkGenerator.MaxWorldY; y++)
        {
            BlockState block = new(BlockAt(new BlockPosition(bx, y, bz)));
            if (block.IsAir)
                continue;
            bool upper = false;
            bool lower = false;
            for (int sx = 0; sx < 2; sx++)
            for (int sz = 0; sz < 2; sz++)
            {
                upper |= block.ContainsMicro(sx, 1, sz);
                lower |= block.ContainsMicro(sx, 0, sz);
            }
            if (upper)
                highest = MathF.Max(highest, y + 1f);
            else if (lower)
                highest = MathF.Max(highest, y + 0.5f);
        }
        return highest + MathF.Max(standingHeight, 0.1f) * 0.5f + 0.15f;
    }

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
        EditState = _edits.Serialize(Seed);
        return true;
    }

    public override void OnDestroy()
    {
        _stop.Cancel();
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
        _chunkObjects.Clear();
        _versions.Clear();
    }
}
