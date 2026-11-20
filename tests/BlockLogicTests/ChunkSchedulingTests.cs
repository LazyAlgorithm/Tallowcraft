using System;
using System.Collections.Generic;
using System.Threading;
using WorldGeneration;

internal static class ChunkSchedulingTests
{
    public static void Run(Action<bool, string> check)
    {
        check(WorldDisplaySettings.Distances[^1] == 64 &&
              WorldDisplaySettings.Distances.Contains(20) &&
              WorldDisplaySettings.Distances.Length > 3,
            "View distance preserves 20 chunks and extends to 64 with quadtree LOD");
        check(WorldDisplaySettings.WorkerCount(0, 12) == 11 &&
              WorldDisplaySettings.WorkerCount(0, 1) == 1 &&
              WorldDisplaySettings.WorkerCount(50, 12) == 12,
            "Automatic and explicit chunk worker counts respect processor limits");
        int choice = 0;
        foreach (int expected in new[] { 1, 2, 4, 8, 12, 0 })
        {
            choice = WorldDisplaySettings.NextWorkerSelection(choice, 12);
            check(choice == expected, "Thread options reach the exact detected maximum and Auto");
        }
        check(ChunkScheduling.ShouldCastShadow(new ChunkKey(20, 0, 0),
            0, 0, 0, 20), "The extended shadow radius reaches 20 chunks");
        check(ChunkScheduling.DistantWorkerLimit(1, false, false) == 0 &&
              ChunkScheduling.DistantWorkerLimit(4, false, true) == 0,
            "No distant LOD jobs start until immediate spawn terrain is ready");
        check(ChunkScheduling.DistantWorkerLimit(1, true, false) == 0 &&
              ChunkScheduling.DistantWorkerLimit(1, true, true) == 1,
            "Single-worker builds finish near terrain before distant meshes");
        check(ChunkScheduling.DistantWorkerLimit(2, true, false) == 1 &&
              ChunkScheduling.DistantWorkerLimit(4, true, false) == 1 &&
              ChunkScheduling.DistantWorkerLimit(8, true, false) == 2,
            "Multi-worker builds reserve most workers for full-detail chunks");
        check(ChunkScheduling.DistantWorkerLimit(16, true, false) == 4,
            "Large worker pools keep the far LOD quota bounded to one quarter");
        int collisionRadius = 2;
        ChunkKey center = new(0, 0, 0);
        ChunkKey near = new(1, 0, 0);
        ChunkKey far = new(5, 0, 0);
        ChunkKey below = new(0, -1, 0);
        ChunkKey above = new(0, 1, 0);

        check(ChunkScheduling.Priority(center, 0, 0, 0, collisionRadius) <
              ChunkScheduling.Priority(near, 0, 0, 0, collisionRadius),
              "Current viewer chunk is most urgent");
        check(ChunkScheduling.Priority(near, 0, 0, 0, collisionRadius) <
              ChunkScheduling.Priority(far, 0, 0, 0, collisionRadius),
              "Nearby surface chunks precede distant terrain");
        check(ChunkScheduling.Priority(center, 0, 0, 0, collisionRadius) <
              ChunkScheduling.Priority(below, 0, 0, 0, collisionRadius),
              "Current player layer precedes neighboring vertical layers");
        check(ChunkScheduling.Priority(below, 0, 0, 0, collisionRadius) <
              ChunkScheduling.Priority(above, 0, 0, 0, collisionRadius),
              "Ground layers precede elevated scenery at equal radius");

        check(ChunkScheduling.HiddenBelowSurface(below, 12f, false),
            "Underground layers stay hidden for surface viewers");
        check(!ChunkScheduling.HiddenBelowSurface(below, 12f, true),
            "Mined shafts expose underground chunk sections");
        check(!ChunkScheduling.HiddenBelowSurface(below, 3f, false),
            "Underground chunks load when approaching the Y=0 boundary");
        check(!ChunkScheduling.HiddenBelowSurface(below, -15f, false),
            "Underground players always receive underground chunks");
        check(!ChunkScheduling.HiddenBelowSurface(above, 12f, false),
            "Terrain above ground is never rejected by underground policy");
        check(!ChunkScheduling.HiddenBelowSurface(center, 12f, false),
            "Surface player layer stays visible");
        check(ChunkScheduling.ShouldCastShadow(center, 0, 0, 0, 5),
            "Current ground chunk casts shadows");
        check(ChunkScheduling.ShouldCastShadow(new ChunkKey(3, 1, 4),
            0, 0, 0, 5),
            "Near elevated chunks cast shadows across vertical seams");
        check(!ChunkScheduling.ShouldCastShadow(new ChunkKey(6, 0, 0),
            0, 0, 0, 5),
            "Far chunks do not add cascaded shadow draw calls");
        check(!ChunkScheduling.ShouldCastShadow(new ChunkKey(0, -2, 0),
            0, 0, 0, 5),
            "Distant vertical sections do not cast unnecessary shadows");
        check(ChunkScheduling.ShouldCastShadow(new ChunkKey(5, 0, 0),
            0, 0, 0, 5),
            "Shadow radius includes its final visible ring");

        // At the initial spawn, the high sky section is guaranteed to be
        // completely empty. It should be representable without a transient
        // MeshRenderer or MeshCollider GameObject.
        var emptyEdits = new Dictionary<BlockPosition, ushort>();
        ChunkMesh sky = ChunkGenerator.GenerateMesh(
            new ChunkKey(0, 2, 0), 42, emptyEdits, CancellationToken.None);
        check(sky.Vertices.Length == 0 && sky.Triangles.Length == 0 &&
            sky.Blocks.Length == 16 * 16 * 16,
            "Known empty sky sections have no render/collision surface");

        ChunkMesh surface = ChunkGenerator.GenerateMesh(
            new ChunkKey(0, 0, 0), 42, emptyEdits, CancellationToken.None);
        check(surface.Vertices.Length > 0 && surface.Triangles.Length > 0,
            "Nearby surface terrain still generates visible and collidable faces");

        Console.WriteLine("PASS: near-first surface chunk scheduling and underground section visibility");
    }
}
