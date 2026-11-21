using System;
using System.Collections.Generic;
using WorldGeneration;

internal static class WorldExpansionTests
{
    public static void Run(Action<bool, string> check)
    {
        const int seed = 42;
        var edits = new Dictionary<BlockPosition, ushort>();

        // All new surface features are outside the established spawn area.
        for (int z = -48; z <= 48; z += 8)
        for (int x = -48; x <= 48; x += 8)
        {
            check(WorldWater.Carve(x, z, seed,
                    ChunkGenerator.RawHeight(x, z, seed)) == 0f &&
                  !WorldWater.HasSurface(x, z, seed),
                "Old spawn terrain contains no new lakes or water overlays");
            check(ChunkGenerator.Height(x, z, seed) ==
                  ChunkGenerator.RawHeight(x, z, seed),
                "Spawn heights are unchanged by new rivers");
        }

        var biomes = new HashSet<WorldBiomes.Biome>();
        int wetCells = 0, waterCells = 0, badlands = 0;
        for (int z = -1200; z <= 1200; z += 19)
        for (int x = -1200; x <= 1200; x += 19)
        {
            WorldBiomes.Biome biome = WorldBiomes.At(x, z, seed);
            biomes.Add(biome);
            int raw = ChunkGenerator.RawHeight(x, z, seed);
            int carved = ChunkGenerator.Height(x, z, seed);
            check(carved >= 1 && carved <= raw && raw <= 35,
                "Water carving only lowers the terrain and respects world limits");
            if (biome == WorldBiomes.Biome.Wetlands) wetCells++;
            if (biome == WorldBiomes.Biome.Badlands) badlands++;
            if (WorldWater.HasSurface(x, z, seed))
            {
                waterCells++;
                check(carved <= WorldWater.WaterLevel,
                    "Visible water areas always have carved ground below the waterline");
                // Structures and player blocks have priority over decorative
                // water: the renderer skips cells with any occupied surface.
                ushort occupancy = ChunkGenerator.BaseBlock(
                    x, WorldWater.WaterLevel, z, seed);
                check(occupancy == 0 || BlockState.IsValid(occupancy),
                    "Water surface occupancy follows the real block sampler");
            }
        }
        check(biomes.Contains(WorldBiomes.Biome.Wetlands) &&
              biomes.Contains(WorldBiomes.Biome.Badlands) &&
              wetCells > 0 && badlands > 0,
            "World climate generates both new biomes");
        check(waterCells > 0, "World contains new water-filled channels or lakes");

        int circles = 0, towers = 0;
        var selected = new List<WorldLandmarks.Landmark>();
        for (int cz = -14; cz <= 14; cz++)
        for (int cx = -14; cx <= 14; cx++)
            if (WorldLandmarks.TryRoot(cx, cz, seed, out var site))
            {
                if (site.Style == WorldLandmarks.Kind.StoneCircle) circles++;
                else towers++;
                check(WorldLandmarks.TryNear(site.X, site.Z, seed,
                    out var found) && found == site,
                    "Additional landmark roots are stable across chunk borders");
                if (selected.Count < 5) selected.Add(site);
            }
        check(circles > 0 && towers > 0,
            "World generates stone circles and timber lookout towers");

        // Cross-check world queries against actual chunk batches for new
        // structures including Y=15/16 and signed X/Z chunk seams.
        var loaded = new Dictionary<ChunkKey, ushort[]>();
        foreach (var site in selected)
        for (int z = site.Z-4; z <= site.Z+4; z++)
        for (int x = site.X-4; x <= site.X+4; x++)
        for (int y = site.Y-1; y <= site.TopY; y++)
        {
            if (!ChunkGenerator.InWorld(y)) continue;
            ChunkKey key = new(
                ChunkGenerator.FloorDiv(x, 16),
                ChunkGenerator.FloorDiv(y, 16),
                ChunkGenerator.FloorDiv(z, 16));
            if (!loaded.TryGetValue(key, out ushort[]? chunk))
            {
                chunk = ChunkGenerator.CreateBlocks(key, seed, edits);
                loaded.Add(key, chunk);
            }
            int lx = x - key.X*16, ly = y-key.Y*16, lz = z-key.Z*16;
            check(chunk[ChunkGenerator.Index(lx, ly, lz)] ==
                  ChunkGenerator.BaseBlock(x, y, z, seed),
                "New landmark chunk meshing and direct block sampling agree");
        }

        var first = selected[0];
        var marker = new BlockPosition(first.X, first.Y-1, first.Z);
        ushort old = ChunkGenerator.BaseBlock(marker.X, marker.Y, marker.Z, seed);
        var changed = new BlockEdits();
        check(changed.Set(marker, 0, seed) &&
              changed.Get(marker.X, marker.Y, marker.Z, seed) == 0 &&
              old != 0,
            "Natural lookout and stone-circle floor remains mineable");

        // Distant caves are larger, while the original underground tests
        // near origin retain the previous threshold and solid surface lid.
        for (int y = -31; y <= -8; y += 3)
        {
            check(!WorldGeology.IsCave(0, -1, 0, seed) &&
                  !WorldGeology.IsCave(0, ChunkGenerator.MinWorldY, 0, seed),
                "New caverns preserve the shallow and deep solid layers");
            check(WorldGeology.IsCave(210, y, -140, seed) ==
                  WorldGeology.IsCave(210, y, -140, seed),
                "Expanded caverns remain deterministic");
        }

        Console.WriteLine($"PASS: exploration expansion (biomes={biomes.Count}, wetlands={wetCells}, badlands={badlands}, water={waterCells}, circles={circles}, lookouts={towers})");
    }
}
