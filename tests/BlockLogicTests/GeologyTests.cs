using System;
using System.Collections.Generic;
using WorldGeneration;

internal static class GeologyTests
{
    public static void Run(Action<bool, string> check)
    {
        const int seed = 42;
        var empty = new Dictionary<BlockPosition, ushort>();
        check(!WorldGeology.IsCave(5, -1, -9, seed),
            "Caves must not break shallow spawn terrain");
        check(!WorldGeology.IsCave(5, ChunkGenerator.MinWorldY, -9, seed),
            "Bedrock-depth floor remains solid");
        check(ChunkGenerator.BaseBlock(5, -1, -9, seed) == 3,
            "Stone covers the surface from Y=-1");
        check(BlockState.IsValid(WorldGeology.CoalOre) &&
            BlockState.IsValid(WorldGeology.IronOre),
            "Ore types are valid packed block states");

        int caves = 0, stone = 0, coal = 0, iron = 0;
        for (int z = -48; z <= 48; z++)
        for (int x = -48; x <= 48; x++)
        for (int y = -24; y <= -8; y += 4)
        {
            ushort block = ChunkGenerator.BaseBlock(x, y, z, seed);
            check(block == 0 || block == 3 ||
                block == WorldGeology.CoalOre || block == WorldGeology.IronOre,
                "Underground block must be air, stone, coal or iron");
            if (block == 0) caves++;
            else if (block == 3) stone++;
            else if (block == WorldGeology.CoalOre) coal++;
            else if (block == WorldGeology.IronOre) iron++;
            check(block == ChunkGenerator.GetBlock(x, y, z, seed, empty),
                "Generator and world block query agree");
            check(block == ChunkGenerator.BaseBlock(x, y, z, seed),
                "World geology is deterministic across repeated calls");
        }
        check(caves > 0 && stone > 0, "Underground caves and stone exist");
        check(coal > 0 && iron > 0, "Both ore types generate underground");

        // The chunk builder and direct block query must agree on both sides
        // of positive and negative X/Z/Y seams, regardless of job order.
        foreach (int x in new[] { -17, -16, -1, 0, 15, 16 })
        foreach (int z in new[] { -17, -16, -1, 0, 15, 16 })
        foreach (int y in new[] { -17, -16, -1, 0, 15, 16 })
        {
            var key = new ChunkKey(
                ChunkGenerator.FloorDiv(x, 16),
                ChunkGenerator.FloorDiv(y, 16),
                ChunkGenerator.FloorDiv(z, 16));
            ushort[] data = ChunkGenerator.CreateBlocks(key, seed, empty);
            int lx = x - key.X * 16, ly = y - key.Y * 16, lz = z - key.Z * 16;
            check(data[ChunkGenerator.Index(lx, ly, lz)] ==
                ChunkGenerator.GetBlock(x, y, z, seed, empty),
                "Chunk seams match direct geology sampling");
        }

        check(WorldGeology.IsCave(11, -20, -13, seed) ==
            WorldGeology.IsCave(11, -20, -13, seed),
            "Cave query is deterministic");
        check(ChunkGenerator.BaseBlock(5, -24, -7, seed) ==
            ChunkGenerator.BaseBlock(5, -24, -7, seed),
            "Ore query is deterministic");

        // Edits must override caves and ore on both server and late joiners.
        var changes = new BlockEdits();
        var position = new BlockPosition(-16, -20, -16);
        ushort original = changes.Get(position.X, position.Y, position.Z, seed);
        ushort replacement = original == 0 ? (ushort)3 : (ushort)0;
        check(changes.Set(position, replacement, seed),
            "Override generated cave/ore block with user edit");
        check(changes.Get(position.X, position.Y, position.Z, seed) == replacement,
            "User edit takes precedence over geological generation");
        var replica = new BlockEdits();
        check(replica.TryLoad(changes.Serialize(seed), out int replicatedSeed,
            out List<BlockPosition> updated) &&
            replicatedSeed == seed && updated.Count == 1 &&
            replica.Get(position.X, position.Y, position.Z, seed) == replacement,
            "Geology edits replicate across network snapshot");

        Console.WriteLine(
            $"PASS: geological seams and deterministic cave/ore tests (caves={caves}, coal={coal}, iron={iron})");
    }
}
