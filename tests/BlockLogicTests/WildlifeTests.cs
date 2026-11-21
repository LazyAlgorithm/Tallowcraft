using System;
using System.Numerics;
using WorldGeneration;

internal static class WildlifeTests
{
    public static void Run(Action<bool, string> check)
    {
        int sheep = 0, pig = 0, drylands = 0;
        for (int z = -384; z <= 384; z += 16)
        for (int x = -384; x <= 384; x += 16)
        {
            int kind = WildlifeRules.SpeciesAt(x, z, 42);
            check(kind is 0 or WildlifeRules.Sheep or WildlifeRules.Pig,
                "World spawn must choose a supported animal type or none");
            check(kind == WildlifeRules.SpeciesAt(x, z, 42),
                "Biome animal selection is deterministic for each world cell");
            if (kind == WildlifeRules.Sheep) sheep++;
            if (kind == WildlifeRules.Pig) pig++;
            WorldBiomes.Biome biome = WorldBiomes.At(x, z, 42);
            if (biome == WorldBiomes.Biome.Drylands)
            {
                drylands++;
                check(kind == 0,
                    "Grass-feeding animals should avoid drylands");
            }
            if (biome == WorldBiomes.Biome.Highlands)
                check(kind != WildlifeRules.Pig,
                    "Pigs should not be generated on highland stone");
            if (kind != 0)
            {
                check(WildlifeRules.CanSpawnOn(kind, 1, x, z, 42),
                    "An animal can stand on grassy ground in its biome");
                check(!WildlifeRules.CanSpawnOn(kind, 0, x, z, 42) &&
                      !WildlifeRules.CanSpawnOn(kind, 16, x, z, 42),
                    "Animals cannot spawn floating in air or on glass");
            }
        }
        check(sheep > 0 && pig > 0,
            "Meadows and forests generate both sheep and pigs");

        Vector3 animal = new(5, 7, 9);
        Vector3 shooter = new(6, 7, 12);
        Vector3 fleeing = WildlifeRules.FleeTarget(animal, shooter, 42);
        Vector2 towardAttacker = new(shooter.X - animal.X, shooter.Z - animal.Z);
        Vector2 towardEscape = new(fleeing.X - animal.X, fleeing.Z - animal.Z);
        check(Vector2.Dot(towardAttacker, towardEscape) < 0,
            "A wounded animal flees away from its attacker");
        check(MathF.Abs(towardEscape.Length() - WildlifeRules.FleeDistance) < .001f,
            "Flee destination is limited to the intended navmesh radius");
        Vector3 samePlace = WildlifeRules.FleeTarget(animal, animal, 42);
        check(float.IsFinite(samePlace.X) && float.IsFinite(samePlace.Z) &&
              Vector2.Distance(new Vector2(samePlace.X, samePlace.Z),
                  new Vector2(animal.X, animal.Z)) > 5f,
            "Overlapping attacker and animal still produce a safe flee direction");

        check(WildlifeRules.DropType(WildlifeRules.Sheep) == BlockInventory.Wool &&
              WildlifeRules.DropType(WildlifeRules.Pig) == BlockInventory.RawPork &&
              WildlifeRules.DropType(0) == 0,
            "Sheep drop wool; pigs drop raw pork; invalid species drop nothing");
        for (int s = WildlifeRules.Sheep; s <= WildlifeRules.Pig; s++)
        for (int x = -40; x <= 40; x += 7)
        {
            int count = WildlifeRules.DropAmount(s, 42, x, x + 3);
            check(count is >= 1 and <= 3 &&
                  count == WildlifeRules.DropAmount(s, 42, x, x + 3),
                "Wildlife loot quantities are bounded and repeatable");
            int item = WildlifeRules.DropType(s);
            var inventory = new BlockInventory();
            check(inventory.TryCollect(item, count) &&
                  inventory.Count(item) == count &&
                  !BlockInventory.IsPlaceableBlock(item) &&
                  !BlockInventory.IsCreativeBlock(item),
                "Wool and pork are collectible crafting resources, not world blocks");
            var saved = new SavedWorldDrop(item, count, animal, 0);
            byte[] bytes = WorldDropSaveCodec.Encode(new[] { saved });
            check(WorldDropSaveCodec.TryDecode(bytes, out var decoded) &&
                  decoded.Count == 1 && decoded[0].ItemType == item &&
                  decoded[0].Quantity == count,
                "New animal resources survive the existing world drop save format");
        }

        // Updating inventory v5 from 19 to 21 resources must not make old
        // saved inventories unreadable. The extra reserve fields default to 0.
        var previous = new BlockInventory();
        previous.FillStarter();
        string current = previous.Serialize();
        int separator = current.LastIndexOf('|');
        string[] reserve = current[(separator + 1)..].Split(',');
        check(reserve.Length == 21, "New saves reserve two more item IDs");
        string legacy = current[..(separator + 1)] +
            string.Join(',', reserve[..19]);
        var upgraded = new BlockInventory();
        check(upgraded.TryLoad(legacy) &&
              upgraded.Slot(0) == previous.Slot(0) &&
              upgraded.Reserve(BlockInventory.Wool) == 0 &&
              upgraded.Reserve(BlockInventory.RawPork) == 0,
            "Inventories created before wildlife resources load without item loss");
        check(BlockState.Create(BlockInventory.Wool).IsAir &&
              BlockState.Create(BlockInventory.RawPork).IsAir &&
              !BlockState.IsValid((ushort)BlockInventory.Wool) &&
              !BlockState.IsValid((ushort)BlockInventory.RawPork),
            "Wool and pork cannot enter the voxel world as invalid block IDs");

        Console.WriteLine($"PASS: wildlife biome distribution, fleeing, loot and old-save migration (sheep={sheep}, pigs={pig}, drylands={drylands})");
    }
}
