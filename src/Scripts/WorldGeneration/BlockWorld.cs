using System;
using System.Collections.Generic;
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

        int cx = ChunkGenerator.FloorDiv((int)MathF.Floor(position.X), ChunkGenerator.Size);
        int cz = ChunkGenerator.FloorDiv((int)MathF.Floor(position.Z), ChunkGenerator.Size);

        GatherCollisionCenters(cx, cz);
        UnloadDistant(cx, cz);
        UploadFinished(cx, cz);
        UpdateColliders(cx, cz);
        MaintainBlockCache(cx, cz);
        StartNearestJobs(cx, cz);
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

    private void GatherCollisionCenters(int cx, int cz)
    {
        _playerCenters.Clear();

        if (Scene.IsServer && Scene.IsNetworked)
        {
            AddPlayerCenter(Network.PlayerObject(Scene, 0));
            foreach (int clientId in Network.ClientIds(Scene))
                AddPlayerCenter(Network.PlayerObject(Scene, clientId));
        }

        if (_playerCenters.Count == 0 || !Scene.IsServer)
            _playerCenters.Add(new ChunkKey(cx, cz));
    }

    private void AddPlayerCenter(GameObject? player)
    {
        if (player is not { } p || !p.IsValid)
            return;

        Vector3 position = p.Transform.Position;
        _playerCenters.Add(new ChunkKey(
            ChunkGenerator.FloorDiv((int)MathF.Floor(position.X), ChunkGenerator.Size),
            ChunkGenerator.FloorDiv((int)MathF.Floor(position.Z), ChunkGenerator.Size)));
    }

    private static bool Near(ChunkKey key, int cx, int cz, int radius)
    {
        long dx = (long)key.X - cx, dz = (long)key.Z - cz;
        return dx * dx + dz * dz <= (long)radius * radius;
    }

    private int CollisionRadius() =>
        Math.Clamp(EditableRadius, 0, Math.Clamp(RenderRadius, 1, 16));

    private bool ShouldStream(ChunkKey key, int cx, int cz, int extra = 0)
    {
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

    private bool ShouldKeep(ChunkKey key, int cx, int cz, int margin = 0)
    {
        if (ShouldStream(key, cx, cz, margin))
            return true;

        // Dedicated/host server must simulate collision around every client,
        // even when that player is far outside the host's viewing direction.
        if (Scene.IsServer)
            foreach (ChunkKey center in _playerCenters)
                if (Near(key, center.X, center.Z, CollisionRadius() + margin))
                    return true;

        return false;
    }

    private bool ShouldHaveCollider(ChunkKey key, int cx, int cz, int margin = 0)
    {
        if (Near(key, cx, cz, CollisionRadius() + margin))
            return true;
        if (Scene.IsServer)
            foreach (ChunkKey center in _playerCenters)
                if (Near(key, center.X, center.Z, CollisionRadius() + margin))
                    return true;
        return false;
    }

    private void UnloadDistant(int cx, int cz)
    {
        _scratch.Clear();
        foreach (var pair in _loaded)
        {
            if (ShouldKeep(pair.Key, cx, cz, Math.Clamp(KeepMargin, 0, 3)))
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

    private void UploadFinished(int cx, int cz)
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
                !ShouldKeep(pair.Key, cx, cz))
                continue;

            ChunkMesh data = job.Task.Result;

            if (_loaded.TryGetValue(pair.Key, out LiveChunk? live))
            {
                if (!live.Object.IsValid)
                    continue;

                live.Renderer.SetMesh(data.Vertices, data.Triangles, data.Normals);
                if (_colliders.Contains(pair.Key) &&
                    live.Object.GetComponent<MeshCollider>() is { } collider &&
                    collider.IsValid)
                    collider.Refresh();

                live.Object.Entity.Set(new ChunkInfo
                {
                    X = pair.Key.X, Z = pair.Key.Z,
                    FaceCount = data.Triangles.Length / 6
                });
                live.Blocks = ShouldHaveCollider(pair.Key, cx, cz) ? data.Blocks : null;
                live.Dirty = false;
                continue;
            }

            GameObject obj = Scene.CreateGameObject($"Chunk {pair.Key.X}, {pair.Key.Z}");
            obj.Transient = true;
            obj.Transform.Position = new Vector3(
                pair.Key.X * ChunkGenerator.Size, 0, pair.Key.Z * ChunkGenerator.Size);

            MeshRenderer renderer = obj.AddComponent<MeshRenderer>();
            renderer.FlatShading = true;
            renderer.SetMesh(data.Vertices, data.Triangles, data.Normals);

            bool collision = ShouldHaveCollider(pair.Key, cx, cz);
            if (collision)
            {
                obj.AddComponent<MeshCollider>();
                _colliders.Add(pair.Key);
            }

            obj.Entity.Set(new ChunkInfo
            {
                X = pair.Key.X, Z = pair.Key.Z,
                FaceCount = data.Triangles.Length / 6
            });
            _chunkObjects[obj.Id] = pair.Key;
            _loaded.Add(pair.Key,
                new LiveChunk(obj, renderer, collision ? data.Blocks : null));
        }

        foreach (ChunkKey key in _scratch)
            _jobs.Remove(key);
    }

    private void UpdateColliders(int cx, int cz)
    {
        foreach (var pair in _loaded)
        {
            ChunkKey key = pair.Key;
            GameObject obj = pair.Value.Object;
            if (!obj.IsValid)
                continue;

            bool has = _colliders.Contains(key);
            if (!has && ShouldHaveCollider(key, cx, cz))
            {
                obj.AddComponent<MeshCollider>();
                _colliders.Add(key);
            }
            else if (has && !ShouldHaveCollider(key, cx, cz, 1))
            {
                obj.RemoveComponent<MeshCollider>();
                _colliders.Remove(key);
            }
        }
    }

    private void MaintainBlockCache(int cx, int cz)
    {
        var snapshot = _edits.Snapshot;
        foreach (var pair in _loaded)
        {
            if (ShouldHaveCollider(pair.Key, cx, cz))
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
        int z = ChunkGenerator.FloorDiv(position.Z, size);
        int localX = position.X - x * size;
        int localZ = position.Z - z * size;

        MarkDirty(new ChunkKey(x, z));
        if (localX == 0) MarkDirty(new ChunkKey(x - 1, z));
        if (localX == size - 1) MarkDirty(new ChunkKey(x + 1, z));
        if (localZ == 0) MarkDirty(new ChunkKey(x, z - 1));
        if (localZ == size - 1) MarkDirty(new ChunkKey(x, z + 1));
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

    private void StartNearestJobs(int cx, int cz)
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
                if (!ShouldKeep(key, cx, cz) || _jobs.ContainsKey(key))
                    return;
                if (_loaded.TryGetValue(key, out LiveChunk? live) && !live.Dirty)
                    return;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearest = key;
                }
            }

            // Prioritize camera-near chunks, in the existing directional shape.
            for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                ChunkKey key = new(cx + dx, cz + dz);
                if (ShouldStream(key, cx, cz))
                    Consider(key, (long)dx * dx + (long)dz * dz);
            }

            // Server-only: also cover each remote player's collision neighborhood.
            if (Scene.IsServer)
                foreach (ChunkKey center in _playerCenters)
                    for (int dz = -nearRadius; dz <= nearRadius; dz++)
                    for (int dx = -nearRadius; dx <= nearRadius; dx++)
                        if ((long)dx * dx + (long)dz * dz <= (long)nearRadius * nearRadius)
                            Consider(new ChunkKey(center.X + dx, center.Z + dz),
                                (long)dx * dx + (long)dz * dz);

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
    /// Server only. Uses Tallow's native Jolt surface normal to select the
    /// interior voxel (break) or neighboring empty voxel (place).
    /// </summary>
    public bool TryEditFromRay(Vector3 origin, Vector3 direction, float reach, bool breaking, GameObject actingPlayer)
    {
        if (!Scene.IsServer || !float.IsFinite(reach) ||
            reach <= 0 || !float.IsFinite(origin.X) || !float.IsFinite(origin.Y) ||
            !float.IsFinite(origin.Z) || !float.IsFinite(direction.X) ||
            !float.IsFinite(direction.Y) || !float.IsFinite(direction.Z) ||
            direction.LengthSquared() < 0.0001f)
            return false;

        direction = Vector3.Normalize(direction);
        // RaycastAll allows skipping the shooter's own capsule while still
        // honoring other obstacles (another player or a wall) before the voxel.
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
            hit.HitObject is not { } objectHit ||
            !_chunkObjects.ContainsKey(objectHit.Id))
            return false;

        // The contact point lies exactly on a face: offset before Floor().
        Vector3 sample = hit.Point + normal * (breaking ? -0.01f : 0.01f);
        var position = new BlockPosition(
            (int)MathF.Floor(sample.X),
            (int)MathF.Floor(sample.Y),
            (int)MathF.Floor(sample.Z));

        if (position.Y < 0 || position.Y >= ChunkGenerator.Size)
            return false;

        byte current = _edits.Get(position.X, position.Y, position.Z, Seed);
        if (breaking ? current == 0 : current != 0)
            return false;

        if (!breaking)
        {
            // Do not place a block inside another physics body, including a player.
            Vector3 center = new(position.X + 0.5f, position.Y + 0.5f, position.Z + 0.5f);
            if (Scene.CheckBox(center, new Vector3(0.9f), Quaternion.Identity))
                return false;
        }

        byte replacement = breaking ? (byte)0 : (byte)1;
        if (!_edits.Set(position, replacement, Seed))
            return false;

        MarkBlockDirty(position);
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
