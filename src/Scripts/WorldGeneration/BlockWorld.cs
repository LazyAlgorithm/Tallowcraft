using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Tallow;

namespace WorldGeneration;

public class BlockWorld : Script
{
    public GameObject Player;

    // Optional: use the actual camera's look direction.
    // Useful when the player body and camera rotate separately.
    public GameObject ViewCamera;

    public int Seed = 42;

    // How far chunks are generated in each direction.
    public int RenderRadius = 8;
    public int SideRadius = 4;
    public int BehindRadius = 2;

    // Full circle near the player for collision and block data.
    public int EditableRadius = 2;

    // Extra retention distance to reduce chunk popping.
    public int KeepMargin = 1;

    public int WorkerCount = 2;
    public int UploadsPerFrame = 1;

    private readonly Dictionary<ChunkKey, LiveChunk> _loaded = new();
    private readonly Dictionary<ChunkKey, Task<ChunkMesh>> _jobs = new();
    private readonly HashSet<ChunkKey> _colliders = new();
    private readonly List<ChunkKey> _scratch = new();
    private readonly CancellationTokenSource _stop = new();

    // Remember the last horizontal viewing direction.
    // Looking straight up/down won't suddenly change streaming.
    private Vector2 _lastForward = Vector2.UnitY;

    public override void Update(TickInput input, float deltaTime)
    {
        // The world follows the player position.
        GameObject anchor = Player.IsValid
            ? Player
            : ViewCamera.IsValid ? ViewCamera : GameObject;

        // The camera controls the directional loading.
        GameObject view = ViewCamera.IsValid
            ? ViewCamera
            : anchor;

        Vector3 position = anchor.Transform.Position;

        Vector3 cameraForward = view.Transform.Forward;
        Vector2 horizontalForward = new(
            cameraForward.X,
            cameraForward.Z
        );

        if (horizontalForward.LengthSquared() > 0.0001f)
            _lastForward = Vector2.Normalize(horizontalForward);

        int size = ChunkGenerator.Size;

        int cx = (int)MathF.Floor(position.X / size);
        int cz = (int)MathF.Floor(position.Z / size);

        UnloadDistant(cx, cz);
        UploadFinished(cx, cz);
        UpdateColliders(cx, cz);
        MaintainBlockCache(cx, cz);
        StartNearestJobs(cx, cz);
    }

    // True when a chunk is within a circular radius.
    private static bool Near(
        ChunkKey key,
        int cx,
        int cz,
        int radius)
    {
        long dx = (long)key.X - cx;
        long dz = (long)key.Z - cz;

        return dx * dx + dz * dz <= (long)radius * radius;
    }

    // Direction-aware chunk streaming.
    //
    // Forward: long distance
    // Sides:   medium distance
    // Behind:  short distance
    //
    // The range changes smoothly based on the angle
    // instead of using a hard-edged viewing cone.
    private bool ShouldStream(
        ChunkKey key,
        int cx,
        int cz,
        int extra = 0)
    {
        int forwardRange = Math.Clamp(RenderRadius, 1, 16);
        int sideRange = Math.Clamp(SideRadius, 1, forwardRange);
        int backRange = Math.Clamp(BehindRadius, 0, sideRange);
        int nearRange = Math.Clamp(EditableRadius, 0, forwardRange);

        float dx = key.X - cx;
        float dz = key.Z - cz;

        float distance = MathF.Sqrt(dx * dx + dz * dz);

        if (distance <= nearRange + extra)
            return true;

        if (distance < 0.0001f)
            return true;

        // +1 = directly ahead
        //  0 = directly to the side
        // -1 = directly behind
        float facing =
            (dx * _lastForward.X + dz * _lastForward.Y)
            / distance;

        float allowedRadius;

        if (facing >= 0)
        {
            // Gradually extend from side to forward range.
            allowedRadius = sideRange
                + (forwardRange - sideRange) * facing;
        }
        else
        {
            // Gradually shrink from side to behind range.
            allowedRadius = sideRange
                + (sideRange - backRange) * facing;
        }

        // Always cover the editable/collision area.
        allowedRadius = MathF.Max(allowedRadius, nearRange);

        return distance <= allowedRadius + extra;
    }

    // Unload meshes that are well outside the viewing area.
    private void UnloadDistant(int cx, int cz)
    {
        int margin = Math.Clamp(KeepMargin, 0, 3);

        _scratch.Clear();

        foreach (var pair in _loaded)
        {
            if (ShouldStream(pair.Key, cx, cz, margin))
                continue;

            GameObject obj = pair.Value.Object;

            if (obj.IsValid)
            {
                pair.Value.Renderer.ClearMesh();
                obj.Destroy();
            }

            _colliders.Remove(pair.Key);
            _scratch.Add(pair.Key);
        }

        foreach (ChunkKey key in _scratch)
            _loaded.Remove(key);
    }

    // Upload completed chunks through Tallow's renderer.
    // This runs on the main thread.
    private void UploadFinished(int cx, int cz)
    {
        int collisionRadius = Math.Clamp(
            EditableRadius,
            0,
            Math.Clamp(RenderRadius, 1, 16)
        );

        int limit = Math.Clamp(UploadsPerFrame, 1, 4);
        int processed = 0;

        _scratch.Clear();

        foreach (var pair in _jobs)
        {
            if (processed >= limit)
                break;

            if (!pair.Value.IsCompleted)
                continue;

            processed++;
            _scratch.Add(pair.Key);

            if (pair.Value.IsFaulted)
            {
                Console.WriteLine(
                    $"Chunk {pair.Key} failed: {pair.Value.Exception}"
                );
                continue;
            }

            if (!pair.Value.IsCompletedSuccessfully)
                continue;

            // Player may have moved or turned while
            // this chunk was being generated.
            if (!ShouldStream(pair.Key, cx, cz))
                continue;

            if (_loaded.ContainsKey(pair.Key))
                continue;

            ChunkMesh data = pair.Value.Result;

            GameObject obj = Scene.CreateGameObject(
                $"Chunk {pair.Key.X}, {pair.Key.Z}"
            );

            obj.Transient = true;

            obj.Transform.Position = new Vector3(
                pair.Key.X * ChunkGenerator.Size,
                0,
                pair.Key.Z * ChunkGenerator.Size
            );

            MeshRenderer renderer = obj.AddComponent<MeshRenderer>();

            renderer.FlatShading = true;

            renderer.SetMesh(
                data.Vertices,
                data.Triangles,
                data.Normals
            );

            // Generate collision only around the player.
            if (Near(pair.Key, cx, cz, collisionRadius))
            {
                obj.AddComponent<MeshCollider>();
                _colliders.Add(pair.Key);
            }

            // ECS chunk metadata.
            obj.Entity.Set(new ChunkInfo
            {
                X = pair.Key.X,
                Z = pair.Key.Z,
                FaceCount = data.Triangles.Length / 6
            });

            // Keep actual block data only nearby.
            byte[]? blocks = Near(
                pair.Key, cx, cz, collisionRadius
            ) ? data.Blocks : null;

            _loaded.Add(
                pair.Key,
                new LiveChunk(obj, renderer, blocks)
            );
        }

        foreach (ChunkKey key in _scratch)
            _jobs.Remove(key);
    }

    // Add and remove nearby static collision.
    private void UpdateColliders(int cx, int cz)
    {
        int radius = Math.Clamp(
            EditableRadius,
            0,
            Math.Clamp(RenderRadius, 1, 16)
        );

        foreach (var pair in _loaded)
        {
            ChunkKey key = pair.Key;
            GameObject obj = pair.Value.Object;

            if (!obj.IsValid)
                continue;

            bool hasCollider = _colliders.Contains(key);

            if (!hasCollider && Near(key, cx, cz, radius))
            {
                obj.AddComponent<MeshCollider>();
                _colliders.Add(key);
            }
            else if (
                hasCollider &&
                !Near(key, cx, cz, radius + 1))
            {
                obj.RemoveComponent<MeshCollider>();
                _colliders.Remove(key);
            }
        }
    }

    // Keep block arrays around the player for future edits.
    private void MaintainBlockCache(int cx, int cz)
    {
        int radius = Math.Clamp(
            EditableRadius,
            0,
            Math.Clamp(RenderRadius, 1, 16)
        );

        foreach (var pair in _loaded)
        {
            if (Near(pair.Key, cx, cz, radius))
            {
                pair.Value.Blocks ??=
                    ChunkGenerator.CreateBlocks(pair.Key, Seed);
            }
            else
            {
                pair.Value.Blocks = null;
            }
        }
    }

    // Generate the nearest missing chunks first.
    // Only chunks within the directional footprint
    // are eligible for generation.
    private void StartNearestJobs(int cx, int cz)
    {
        int radius = Math.Clamp(RenderRadius, 1, 16);
        int maxJobs = Math.Clamp(WorkerCount, 1, 4);

        while (_jobs.Count < maxJobs)
        {
            ChunkKey? nearest = null;
            long bestDistance = long.MaxValue;

            for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                ChunkKey key = new(cx + dx, cz + dz);

                if (!ShouldStream(key, cx, cz))
                    continue;

                if (_loaded.ContainsKey(key))
                    continue;

                if (_jobs.ContainsKey(key))
                    continue;

                long distance =
                    (long)dx * dx + (long)dz * dz;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearest = key;
                }
            }

            if (nearest is not { } next)
                break;

            int seed = Seed;
            CancellationToken token = _stop.Token;

            Task<ChunkMesh> job = Task.Run(
                () => ChunkGenerator.GenerateMesh(next, seed, token),
                token
            );

            _jobs.Add(next, job);
        }
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
        _colliders.Clear();
        _jobs.Clear();
    }
}
