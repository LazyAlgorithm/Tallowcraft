using System;
using System.Collections.Generic;
using System.Numerics;
using WorldGeneration;

internal static class ForgeTests
{
    public static void Run(Action<bool, string> check)
    {
        var blocks = new Dictionary<BlockPosition, ushort>();
        var location = new BlockPosition(6, 10, -4);
        Vector3 eye = new(6.5f, 10.5f, -1.5f);
        ushort forge = BlockState.Create(5).Packed;
        ushort stone = BlockState.Create(3).Packed;

        check(!ForgeCrafting.NearForge(eye,
                p => blocks.TryGetValue(p, out ushort value) ? value : stone),
            "A player without a nearby forge cannot smelt");
        blocks[location] = forge;
        check(ForgeCrafting.NearForge(eye,
                p => blocks.TryGetValue(p, out ushort value) ? value : stone),
            "A placed forge within 3.5 metres unlocks smelting");
        check(!ForgeCrafting.NearForge(new Vector3(6.5f, 10.5f, .25f),
                p => blocks.TryGetValue(p, out ushort value) ? value : stone),
            "A distant forge cannot be used by spoofing a client request");
        check(!ForgeCrafting.NearForge(new Vector3(float.NaN, 0, 0),
                p => blocks.TryGetValue(p, out ushort value) ? value : stone),
            "Nonfinite player position cannot match a forge");
        blocks[location] = stone;
        check(!ForgeCrafting.NearForge(eye,
                p => blocks.TryGetValue(p, out ushort value) ? value : stone),
            "Mining the forge immediately revokes workstation access");

        var materials = new BlockInventory();
        check(materials.TryCollect(8, 5) &&
            materials.TryCollect(7, 4) &&
            !ForgeCrafting.CanSmelt(materials, 5) &&
            !ForgeCrafting.TrySmelt(materials, 5) &&
            materials.Count(8) == 5 && materials.Count(7) == 4,
            "Five-unit smelting fails atomically when coal is insufficient");
        check(materials.TryCollect(7) &&
            ForgeCrafting.TrySmelt(materials, 5) &&
            materials.Count(7) == 0 && materials.Count(8) == 0 &&
            materials.Count(BlockInventory.IronIngot) == 5,
            "Five iron ore and five coal become exactly five ingots");
        check(!ForgeCrafting.TrySmelt(materials, -1) &&
            !ForgeCrafting.TrySmelt(materials, 0) &&
            !ForgeCrafting.TrySmelt(materials, 2) &&
            !ForgeCrafting.TrySmelt(materials, 999),
            "Invalid forge recipe counts do not change inventory");
        string snapshot = materials.Serialize();
        var copied = new BlockInventory();
        check(copied.TryLoad(snapshot) && copied.Serialize() == snapshot &&
            copied.Count(10) == 5,
            "Ten-slot inventory replicates smelted ingots deterministically");

        var legacyItems = new BlockInventory();
        check(legacyItems.TryLoad("1|32,64,64,32,8,64,3,4,5") &&
            legacyItems.Count(7) == 3 && legacyItems.Count(8) == 4 &&
            legacyItems.Count(9) == 5 && legacyItems.Count(10) == 0 &&
            legacyItems.Serialize().StartsWith("3|", StringComparison.Ordinal),
            "Original nine-item save migrates to the ten-item format");
        var overflow = new BlockInventory();
        check(overflow.TryCollect(10, 999) && overflow.TryCollect(8) &&
            overflow.TryCollect(7) &&
            !ForgeCrafting.TrySmelt(overflow, 1) &&
            overflow.Count(7) == 1 && overflow.Count(8) == 1 &&
            overflow.Count(10) == 999,
            "A full ingot stack cannot consume ore or coal");
        check(overflow.TrySpend(10) && ForgeCrafting.TrySmelt(overflow, 1) &&
            overflow.Count(10) == 999,
            "Smelting uses exact remaining ingot capacity");

        var equipment = new PlayerTools();
        check(equipment.TryLoad("1|80,60,50|2") &&
            equipment.Durability(MiningTool.Axe) == 60 &&
            equipment.Durability(MiningTool.IronAxe) == 0 &&
            equipment.Equipped == MiningTool.Axe &&
            equipment.Serialize().StartsWith("2|", StringComparison.Ordinal),
            "Original stone-tool save migrates without changing equipment");
        check(!equipment.TryEquipSlot(4) && !equipment.TryEquipSlot(99),
            "Quick equip rejects invalid slots");

        var mining = new BlockInventory();
        check(mining.TryCollect(10, 6) && mining.TryCollect(6, 6) &&
            equipment.TryCraft(mining, MiningTool.IronPickaxe) &&
            equipment.TryCraft(mining, MiningTool.IronAxe) &&
            equipment.TryCraft(mining, MiningTool.IronShovel) &&
            mining.Count(10) == 0 && mining.Count(6) == 0,
            "Iron pickaxe, axe and shovel require smelted ingots and planks");
        check(equipment.Durability(MiningTool.IronPickaxe) == 250 &&
            equipment.Durability(MiningTool.IronAxe) == 220 &&
            equipment.Durability(MiningTool.IronShovel) == 190,
            "Iron tool durability is higher than equivalent stone tools");
        check(equipment.TryEquipSlot(1) &&
            equipment.Equipped == MiningTool.IronPickaxe &&
            BlockMining.Seconds(BlockState.Create(8).Packed,
                MiningTool.IronPickaxe) <
            BlockMining.Seconds(BlockState.Create(8).Packed,
                MiningTool.Pickaxe),
            "Hotkey 7 prefers the iron pickaxe and mines ore faster");
        check(equipment.TryEquipSlot(2) &&
            equipment.Equipped == MiningTool.IronAxe &&
            BlockMining.Seconds(BlockState.Create(4).Packed,
                MiningTool.IronAxe) <
            BlockMining.Seconds(BlockState.Create(4).Packed,
                MiningTool.Axe),
            "Hotkey 8 prefers the faster iron axe");
        check(equipment.TryEquipSlot(3) &&
            equipment.Equipped == MiningTool.IronShovel,
            "Hotkey 9 prefers the crafted iron shovel");

        var joined = new PlayerTools();
        check(joined.TryLoad(equipment.Serialize()) &&
            joined.Serialize() == equipment.Serialize(),
            "Six-slot equipment saves and replicates losslessly");
        string preserved = joined.Serialize();
        foreach (string invalid in new[]
        {
            "2|0,0,0,0,0|0", "2|0,0,0,0,0,0,0|0",
            "2|80,72,64,251,0,0|0",
            "2|80,72,64,0,0,0|4",
            "2|80,72,64,0,0,0|7"
        })
            check(!joined.TryLoad(invalid) && joined.Serialize() == preserved,
                "Forged iron equipment data is atomically rejected");
        check(equipment.TryEquipSlot(0) &&
            equipment.Equipped == MiningTool.Hand,
            "Hotkey 0 always switches back to hands");

        Console.WriteLine(
            "PASS: forge range, validated batch smelting, " +
            "iron tools and legacy inventory migration");
    }
}
