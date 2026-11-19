using System;
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
        Console.WriteLine("PASS: near-first surface chunk scheduling and underground section visibility");
    }
}
