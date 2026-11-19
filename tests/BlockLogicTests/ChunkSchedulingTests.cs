using System;
using System.Collections.Generic;
using System.Threading;
using WorldGeneration;

internal static class ChunkSchedulingTests
{
    public static void Run(Action<bool, string> check)
    {
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
