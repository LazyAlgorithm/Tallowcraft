using System;
using System.Collections.Generic;
using WorldGeneration;

internal static class FloraTests
{
    public static void Run(Action<bool, string> check)
    {
        const int seed = 42;
        var empty = new Dictionary<BlockPosition, ushort>();
        check(BlockState.IsValid(WorldFlora.Leaves) &&
            BlockState.Create(WorldFlora.Leaves).Packed == WorldFlora.Leaves,
            "Generated leaves must be a valid network block type");
        check(BlockTextureAtlas.Columns == 4 && BlockTextureAtlas.Rows == 4,
            "Expanded atlas must use a four-by-four grid");
        check(BlockTextureAtlas.TileFor(new BlockState(7), 0) == 9 &&
            BlockTextureAtlas.TileFor(new BlockState(8), 0) == 10 &&
            BlockTextureAtlas.TileFor(new BlockState(WorldFlora.Leaves), 0) == 11,
            "Coal, iron and leaf blocks must use their dedicated atlas tiles");
        for (int i = 0; i < 12; i++)
        {
            var uv = BlockTextureAtlas.UV(i, 0.5f, 0.5f);
            check(uv.X > 0f && uv.X < 1f && uv.Y > 0f && uv.Y < 1f,
                "Atlas UVs must lie inside the updated PNG");
        }

        int count = 0;
        BlockPosition first = default;
        bool foundHorizontalSeam = false;
        bool foundVerticalSeam = false;
        var roots = new List<BlockPosition>();

        // Enumerate a seeded world region to catch different biomes, negative
        // coordinates, and canopies that cross 16-block X/Z or Y boundaries.
        for (int z = -192; z <= 192; z++)
        for (int x = -192; x <= 192; x++)
        {
            if (!WorldFlora.TryTreeAt(x, z, seed, out int y, out int trunkHeight))
                continue;
            check((long)x * x + (long)z * z >= 60 * 60,
                "No roots may spawn inside the protected origin area");
            check(trunkHeight >= 4 && trunkHeight <= 6,
                "Tree trunk should be 4-6 blocks tall");
            check(WorldBiomes.At(x, z, seed) is WorldBiomes.Biome.Woodland
                or WorldBiomes.Biome.Meadow,
                "Trees should only appear in meadow and woodland");
            check(ChunkGenerator.BaseBlock(x, y, z, seed) == WorldFlora.Log,
                "Tree trunk must be returned by direct block generator");
            if (count == 0) first = new BlockPosition(x, y, z);
            roots.Add(new BlockPosition(x, y, z));
            count++;
            int crown = y + trunkHeight - 1;
            if (ChunkGenerator.FloorDiv(crown, 16) !=
                ChunkGenerator.FloorDiv(crown + 1, 16) ||
                ChunkGenerator.FloorDiv(y, 16) !=
                ChunkGenerator.FloorDiv(crown + 1, 16))
                foundVerticalSeam = true;
            if (x - ChunkGenerator.FloorDiv(x, 16) * 16 < 2 ||
                x - ChunkGenerator.FloorDiv(x, 16) * 16 >= 14 ||
                z - ChunkGenerator.FloorDiv(z, 16) * 16 < 2 ||
                z - ChunkGenerator.FloorDiv(z, 16) * 16 >= 14)
                foundHorizontalSeam = true;
        }
        check(count > 30, "Seeded terrain should contain a useful tree population");
        check(foundHorizontalSeam, "At least one tree must straddle an X/Z chunk seam");
        check(foundVerticalSeam, "At least one tree must straddle a Y chunk seam");

        // Compare chunk buffering against independently queried world blocks,
        // including overlapping leaves and trunk-priority areas.
        int checkedRoots = 0;
        foreach (BlockPosition root in roots)
        {
            if (++checkedRoots > 16) break;
            var generated = new Dictionary<ChunkKey, ushort[]>();
            for (int z = root.Z - 2; z <= root.Z + 2; z++)
            for (int x = root.X - 2; x <= root.X + 2; x++)
            for (int y = root.Y; y <= root.Y + 7; y++)
            {
                if (!ChunkGenerator.InWorld(y)) continue;
                ChunkKey key = new(
                    ChunkGenerator.FloorDiv(x, 16),
                    ChunkGenerator.FloorDiv(y, 16),
                    ChunkGenerator.FloorDiv(z, 16));
                if (!generated.TryGetValue(key, out ushort[]? blocks))
                {
                    blocks = ChunkGenerator.CreateBlocks(key, seed, empty);
                    generated.Add(key, blocks);
                }
                int lx = x - key.X * 16, ly = y - key.Y * 16,
                    lz = z - key.Z * 16;
                check(blocks[ChunkGenerator.Index(lx, ly, lz)] ==
                    ChunkGenerator.BaseBlock(x, y, z, seed),
                    "Trunk and leaf chunks must match direct world queries");
            }
            foreach (ChunkKey key in generated.Keys)
                if (key.Y > 0)
                    check(ChunkGenerator.HasPotentialBlocks(key, seed, empty),
                        "Elevated tree sections must be eligible for streaming");
        }

        // Existing network edit serialization must preserve leaf removals.
        var edits = new BlockEdits();
        check(edits.Get(first.X, first.Y, first.Z, seed) == WorldFlora.Log,
            "Unedited tree trunk must be solid");
        check(edits.Set(first, 0, seed), "Players can mine a generated log");
        check(edits.Get(first.X, first.Y, first.Z, seed) == 0,
            "Player edit overrides procedural tree trunk");
        var replica = new BlockEdits();
        check(replica.TryLoad(edits.Serialize(seed), out int loadedSeed,
            out List<BlockPosition> changed) && loadedSeed == seed &&
            changed.Count == 1 &&
            replica.Get(first.X, first.Y, first.Z, seed) == 0,
            "Late joiners must receive tree edits without re-growing mined logs");

        Console.WriteLine($"PASS: deterministic tree and atlas tests (treeRoots={count}, seams=XZ/Y)");
    }
}
