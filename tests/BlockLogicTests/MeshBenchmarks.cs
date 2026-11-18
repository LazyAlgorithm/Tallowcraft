using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using WorldGeneration;

internal static class MeshBenchmarks
{
    // Run with: dotnet run --project tests/BlockLogicTests -c Release -- --bench
    // This tests pure C# generation only. Native MeshCollider uploads and GPU
    // draw calls require a running Tallow scene and are NOT measured here.
    public static void Run()
    {
        Console.WriteLine("Tallowcraft mesh generation benchmark (CPU only)");
        Console.WriteLine("case,chunks,ms_per_chunk,chunks_per_second,vertices_per_chunk,triangles_per_chunk,allocated_bytes_per_chunk");

        const int seed = 42;
        const int iterations = 32;
        var cases = new (string Name, bool Partial)[]
        {
            ("cube", false),
            ("mixed-shapes", true)
        };

        foreach (var test in cases)
        {
            // Warm the runtime/JIT and all code paths before timing.
            for (int i = 0; i < 4; i++)
                Generate(i, test.Partial, seed);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            long vertices = 0;
            long triangles = 0;
            for (int i = 0; i < iterations; i++)
            {
                ChunkMesh mesh = Generate(i, test.Partial, seed);
                vertices += mesh.Vertices.Length;
                triangles += mesh.Triangles.Length / 3;
            }
            watch.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            double seconds = Math.Max(watch.Elapsed.TotalSeconds, 1e-9);
            Console.WriteLine(
                $"{test.Name},{iterations},{watch.Elapsed.TotalMilliseconds / iterations:F3}," +
                $"{iterations / seconds:F1},{vertices / iterations},{triangles / iterations}," +
                $"{allocated / iterations}");
        }
    }

    private static ChunkMesh Generate(int index, bool partial, int seed)
    {
        // Include negative chunks to exercise world-coordinate floor division.
        var key = new ChunkKey((index % 8) - 4, (index / 8) - 2);
        var edits = new Dictionary<BlockPosition, ushort>();
        if (partial)
        {
            int x = key.X * ChunkGenerator.Size;
            int z = key.Z * ChunkGenerator.Size;
            // Spread partial-block edits through the chunk without overriding
            // the entire terrain. Every variant must use the detailed mesher.
            edits[new BlockPosition(x + 4, 14, z + 4)] =
                BlockState.Create(6, BlockShape.Stair, index % 4).Packed;
            edits[new BlockPosition(x + 8, 14, z + 8)] =
                BlockState.Create(2, BlockShape.Slab, 0, true).Packed;
            edits[new BlockPosition(x + 12, 14, z + 12)] =
                BlockState.Create(3, BlockShape.HalfWall, index % 4).Packed;
        }
        return ChunkGenerator.GenerateMesh(key, seed, edits, CancellationToken.None);
    }
}
