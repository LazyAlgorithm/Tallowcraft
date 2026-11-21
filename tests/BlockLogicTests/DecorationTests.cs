using System;
using System.Collections.Generic;
using WorldGeneration;

internal static class DecorationTests
{
    public static void Run(Action<bool, string> check)
    {
        const int seed = 42;
        var empty = new Dictionary<BlockPosition, ushort>();
        var ruins = new List<BlockPosition>();
        var landmarks = new Dictionary<WorldBiomes.Biome, BlockPosition>();
        var bushes = new List<BlockPosition>();
        bool horizontalRuinSeam = false;
        bool verticalRuinSeam = false;
        BlockPosition nearestRuin = default;
        long nearestDistanceSquared = long.MaxValue;

        // Enumerate feature CELLS, not every voxel, to keep tests fast.
        for (int cz = -24; cz <= 24; cz++)
        for (int cx = -24; cx <= 24; cx++)
        {
            if (!WorldDecorations.TryRuinCellRoot(cx, cz, seed,
                out BlockPosition root))
                continue;
            ruins.Add(root);
            long distanceSquared = (long)root.X * root.X +
                (long)root.Z * root.Z;
            if (distanceSquared < nearestDistanceSquared)
            {
                nearestDistanceSquared = distanceSquared;
                nearestRuin = root;
            }
            check(WorldDecorations.TryRuinAt(root.X, root.Z, seed, out int y)
                && y == root.Y,
                "Ruin root must be discoverable from absolute coordinates");
            check(WorldDecorations.StructureAt(root.X, root.Y - 1,
                root.Z, seed) == WorldDecorations.Planks,
                "Ruin floor center uses planks");
            check(WorldDecorations.StructureAt(root.X - 3, root.Y,
                root.Z - 3, seed) == WorldDecorations.Stone,
                "Ruin corner is a stone pillar");
            check(WorldDecorations.StructureAt(root.X, root.Y,
                root.Z - 3, seed) == 0 &&
                ChunkGenerator.Height(root.X, root.Z - 3, seed) <= root.Y,
                "Ruin entrance is not blocked by stone or terrain");
            check((long)root.X * root.X + (long)root.Z * root.Z >=
                112L * 112L, "Ruins must stay away from the protected spawn");

            if (WorldDecorations.StructureAt(root.X, root.Y - 1, root.Z + 5, seed) != 0)
            {
                var biome = WorldBiomes.At(root.X, root.Z, seed);
                var (dx, peak, material) = biome switch
                {
                    WorldBiomes.Biome.Meadow => (3, 4, BlockInventory.DarkTimber),
                    WorldBiomes.Biome.Drylands => (0, 5, BlockInventory.StoneBricks),
                    _ => (3, 6, BlockInventory.Cobblestone)
                };
                check(WorldDecorations.StructureAt(root.X + dx, root.Y + peak,
                    root.Z + 6, seed) == material, "Landmark silhouette matches biome");
                for (int dz = -3; dz <= 5; dz++)
                for (int offset = -1; offset <= 1; offset++)
                for (int dy = 0; dy <= 2; dy++)
                    check(ChunkGenerator.BaseBlock(root.X + offset, root.Y + dy, root.Z + dz, seed) == 0,
                        "Standing players have a continuous three-wide passage into the rear chamber");
                if (!landmarks.TryGetValue(biome, out var previous) ||
                    distanceSquared < (long)previous.X * previous.X + (long)previous.Z * previous.Z)
                    landmarks[biome] = root;
            }
            int lx = root.X - ChunkGenerator.FloorDiv(root.X, 16) * 16;
            int lz = root.Z - ChunkGenerator.FloorDiv(root.Z, 16) * 16;
            horizontalRuinSeam |= lx < 3 || lx >= 13 || lz < 3 || lz >= 13;
            verticalRuinSeam |= ChunkGenerator.FloorDiv(root.Y, 16) !=
                ChunkGenerator.FloorDiv(root.Y + 2, 16);
        }
        check(landmarks.Count == 3, "Seed contains all three explorable landmark variants");
        foreach (var (biome, root) in landmarks)
            Console.WriteLine($"Seed {seed} {biome} landmark at {root.X}, {root.Y}, {root.Z}");
        var cover = new int[3];
        for (int z = -36; z <= 36; z++)
        for (int x = -36; x <= 36; x++)
        {
            int kind = WorldDecorations.GroundCoverAt(x, z, seed);
            check(kind == WorldDecorations.GroundCoverAt(x, z, seed), "Ground cover repeats across visits");
            cover[kind]++;
        }
        check(cover[0] > cover[1] && cover[1] > cover[2] && cover[2] > 0,
            "Meadows have scattered grass and occasional daisies");
        check(ruins.Count > 8,
            "Seeded worlds should generate discoverable ruined buildings");
        check(horizontalRuinSeam && verticalRuinSeam,
            "Ruin test set must exercise horizontal and vertical seams");

        for (int cz = -56; cz <= 56; cz++)
        for (int cx = -56; cx <= 56; cx++)
            if (WorldDecorations.TryBushCellRoot(cx, cz, seed,
                out BlockPosition root))
            {
                bushes.Add(root);
                check(WorldDecorations.TryBushAt(root.X, root.Z, seed,
                    out int y) && y == root.Y,
                    "Shrub root is independent of chunk generation order");
                check(WorldDecorations.BushAt(root.X, root.Y,
                    root.Z, seed) == WorldDecorations.Leaves,
                    "Shrubs use the existing opaque leaf tile");
                check(WorldBiomes.At(root.X, root.Z, seed) is
                    WorldBiomes.Biome.Meadow or WorldBiomes.Biome.Woodland,
                    "Shrubs only grow in leafy biomes");
            }
        check(bushes.Count > 20,
            "Seeded shrub fields should contain vegetation");

        // Direct queries and generated chunk arrays must agree over every
        // visible part of sampled ruin walls and bushes, at negative or
        // positive X/Z coordinates and across chunk-layer boundaries.
        var chunks = new Dictionary<ChunkKey, ushort[]>();
        void Compare(int x, int y, int z)
        {
            if (!ChunkGenerator.InWorld(y)) return;
            ChunkKey key = new(
                ChunkGenerator.FloorDiv(x, 16),
                ChunkGenerator.FloorDiv(y, 16),
                ChunkGenerator.FloorDiv(z, 16));
            if (!chunks.TryGetValue(key, out ushort[]? blocks))
            {
                blocks = ChunkGenerator.CreateBlocks(key, seed, empty);
                chunks.Add(key, blocks);
            }
            int lx = x - key.X * 16;
            int ly = y - key.Y * 16;
            int lz = z - key.Z * 16;
            check(blocks[ChunkGenerator.Index(lx, ly, lz)] ==
                ChunkGenerator.BaseBlock(x, y, z, seed),
                "Decorated chunk must match direct world-space sample");
            if (key.Y > 0 && blocks[ChunkGenerator.Index(lx, ly, lz)] != 0)
                check(ChunkGenerator.HasPotentialBlocks(key, seed, empty),
                    "Upper chunks containing buildings or shrubs must stream");
        }

        for (int i = 0; i < Math.Min(10, ruins.Count); i++)
        {
            var root = ruins[i];
            for (int dz = -3; dz <= 6; dz++)
            for (int dx = -3; dx <= 3; dx++)
            for (int dy = -1; dy <= 6; dy++)
                Compare(root.X + dx, root.Y + dy, root.Z + dz);
        }
        for (int i = 0; i < Math.Min(24, bushes.Count); i++)
        {
            var root = bushes[i];
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = 0; dy <= 1; dy++)
                Compare(root.X + dx, root.Y + dy, root.Z + dz);
        }

        var first = ruins[0];
        var floor = new BlockPosition(first.X, first.Y - 1, first.Z);
        var edits = new BlockEdits();
        check(edits.Get(floor.X, floor.Y, floor.Z, seed) ==
            WorldDecorations.Planks,
            "Naturally generated ruin floor is breakable");
        check(edits.Set(floor, 0, seed),
            "Player can remove naturally generated ruin blocks");
        check(edits.Get(floor.X, floor.Y, floor.Z, seed) == 0,
            "Player edits override structure generation");

        var replica = new BlockEdits();
        check(replica.TryLoad(edits.Serialize(seed), out int loadedSeed,
            out List<BlockPosition> changed) && loadedSeed == seed &&
            changed.Count == 1 &&
            replica.Get(floor.X, floor.Y, floor.Z, seed) == 0,
            "Late joiners receive edits to natural structures");

        check(WorldDecorations.StructureAt(0, 7, 0, seed) == 0 &&
            WorldDecorations.BushAt(0, 7, 0, seed) == 0,
            "Decorations must not obstruct the initial spawn");

        Console.WriteLine(
            $"PASS: shrub and ruin world-generation tests (ruins={ruins.Count}, bushes={bushes.Count}, nearestRuin={nearestRuin.X},{nearestRuin.Y},{nearestRuin.Z})");
    }
}
