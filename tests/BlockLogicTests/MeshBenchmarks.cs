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
        var cases = new (string Name, int Density, int Iterations)[]
        {
            ("cube", 0, iterations),
            ("mixed-shapes", 1, iterations),
            ("dense-partials", 2, 8),
            ("all-partials", 3, 4),
            ("underground-caves", 4, 8),
            ("biome-upper-layer", 5, 8)
        };

        foreach (var test in cases)
        {
            // Warm the runtime/JIT and all code paths before timing.
            for (int i = 0; i < 4; i++)
                Generate(i, test.Density, seed);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int count = test.Iterations;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            long vertices = 0;
            long triangles = 0;
            for (int i = 0; i < count; i++)
            {
                ChunkMesh mesh = Generate(i, test.Density, seed);
                vertices += mesh.Vertices.Length;
                triangles += mesh.Triangles.Length / 3;
            }
            watch.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            double seconds = Math.Max(watch.Elapsed.TotalSeconds, 1e-9);
            Console.WriteLine(
                $"{test.Name},{count},{watch.Elapsed.TotalMilliseconds / count:F3}," +
                $"{count / seconds:F1},{vertices / count},{triangles / count}," +
                $"{allocated / count}");
        }
    }

    private static ChunkMesh Generate(int index, int density, int seed)
    {
        // Include negative chunks to exercise world-coordinate floor division.
        var key = density switch
        {
            4 => new ChunkKey((index % 8) - 4, -1, (index / 8) - 2),
            5 => new ChunkKey((index % 8) + 12, 1, (index / 8) + 12),
            _ => new ChunkKey((index % 8) - 4, (index / 8) - 2)
        };
        var edits = new Dictionary<BlockPosition, ushort>();
        if (density == 1)
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
        else if (density >= 2)
        {
            // 8-high edited volume. Density 2 changes about a quarter of
            // 2,048 voxels; density 3 changes all 2,048 into partial shapes.
            int x0 = key.X * ChunkGenerator.Size;
            int z0 = key.Z * ChunkGenerator.Size;
            for (int y = 8; y < 16; y++)
            for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x++)
            {
                if (density == 2 && ((x * 7 + z * 11 + y * 3 + index) & 3) != 0)
                    continue;
                var shape = (BlockShape)(1 + (x + z + y + index) % 3);
                bool upper = ((x + y) & 1) != 0;
                edits[new BlockPosition(x0 + x, y, z0 + z)] =
                    BlockState.Create(1 + ((x + z) % 6), shape,
                        (x + z + index) % 4, upper).Packed;
            }
        }
        return ChunkGenerator.GenerateMesh(key, seed, edits, CancellationToken.None);
    }
}
