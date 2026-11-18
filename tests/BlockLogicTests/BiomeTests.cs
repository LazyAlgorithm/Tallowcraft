using System;
using System.Collections.Generic;
using WorldGeneration;

internal static class BiomeTests
{
    public static void Run(Action<bool, string> check)
    {
        const int seed = 42;
        var edits = new Dictionary<BlockPosition, ushort>();

        // Keep the already play-tested central spawn area at its original height.
        for (int z = -24; z <= 24; z += 6)
        for (int x = -24; x <= 24; x += 6)
        {
            int oldHeight = Math.Clamp((int)MathF.Round(8f
                + 3f * MathF.Sin(((long)x + seed) * 0.11f)
                + 3f * MathF.Cos(((long)z - seed) * 0.09f)), 1, 15);
            check(ChunkGenerator.Height(x, z, seed) == oldHeight,
                "Spawn neighborhood must preserve prior terrain height");
        }

        int tallest = 0;
        int changed = 0;
        var foundBiomes = new HashSet<WorldBiomes.Biome>();
        for (int z = -512; z <= 512; z += 16)
        for (int x = -512; x <= 512; x += 16)
        {
            int height = ChunkGenerator.Height(x, z, seed);
            check(height >= 1 && height <= 35,
                "Biome terrain stays inside vertical world bounds");
            check(height == ChunkGenerator.Height(x, z, seed),
                "Climate height must be repeatable");
            foundBiomes.Add(WorldBiomes.At(x, z, seed));
            tallest = Math.Max(tallest, height);
            if (WorldBiomes.HeightOffset(x, z, seed) > 0.5f) changed++;
        }
        check(changed > 0 && tallest > 16,
            "Biome relief creates elevated terrain above the old 16m layer");
        check(foundBiomes.Count >= 2,
            "Different biome climates occur across the world");

        // All biome-generated blocks in the chunk array must match direct
        // world-coordinate sampling, including upper layers and negative X/Z.
        for (int cz = -12; cz <= 12; cz += 6)
        for (int cx = -12; cx <= 12; cx += 6)
        for (int cy = 0; cy <= 2; cy++)
        {
            ChunkKey key = new(cx, cy, cz);
            ushort[] blocks = ChunkGenerator.CreateBlocks(key, seed, edits);
            bool anyBlock = false;
            for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x++)
            for (int y = 0; y < 16; y++)
            {
                int wx = cx * 16 + x, wy = cy * 16 + y,
                    wz = cz * 16 + z;
                ushort block = blocks[ChunkGenerator.Index(x, y, z)];
                check(block == ChunkGenerator.BaseBlock(wx, wy, wz, seed),
                    "Buffered biome blocks match direct generated world terrain");
                anyBlock |= block != 0;
            }
            if (anyBlock)
                check(ChunkGenerator.HasPotentialBlocks(key, seed, edits),
                    "Nonempty elevated biome chunks must be eligible to stream");
        }

        float safeY = ChunkGenerator.SpawnHeight(250.5f, -320.5f, seed, edits);
        check(safeY >= ChunkGenerator.Height(250, -321, seed) + 1f,
            "Spawn capsule clears higher terrain too");

        Console.WriteLine($"PASS: biome height, block and streaming checks (highest={tallest}, biomeKinds={foundBiomes.Count})");
    }
}
