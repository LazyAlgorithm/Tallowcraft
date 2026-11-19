using System;
using WorldGeneration;

internal static class ToolTests
{
    public static void Run(Action<bool, string> check)
    {
        var materials = new BlockInventory();
        var tools = new PlayerTools();

        check(tools.Equipped == MiningTool.Hand &&
            tools.Durability(MiningTool.Pickaxe) == 0 &&
            !tools.TryEquip(MiningTool.Pickaxe),
            "Uncrafted equipment cannot be equipped");

        materials.FillStarter();
        check(tools.CanCraft(materials, MiningTool.Pickaxe) &&
            tools.TryCraft(materials, MiningTool.Pickaxe),
            "Stone and planks can craft the first pickaxe");
        check(materials.Count(3) == 61 && materials.Count(6) == 62,
            "Pickaxe crafting consumes exactly three stone and two planks");
        check(tools.Durability(MiningTool.Pickaxe) == 80 &&
            tools.TryEquip(MiningTool.Pickaxe) &&
            tools.Equipped == MiningTool.Pickaxe,
            "Crafted pickaxe is equippable with full durability");

        ushort stone = BlockState.Create(3).Packed;
        ushort log = BlockState.Create(4).Packed;
        ushort dirt = BlockState.Create(2).Packed;
        check(BlockMining.Seconds(stone, tools.Equipped) <
              BlockMining.Seconds(stone, MiningTool.Hand),
            "Equipped pickaxe speeds up hard stone mining");
        check(BlockMining.Seconds(log, tools.Equipped) ==
              BlockMining.Seconds(log, MiningTool.Hand),
            "Pickaxe does not speed up wood mining");

        string initial = tools.Serialize();
        var joined = new PlayerTools();
        check(joined.TryLoad(initial) &&
            joined.Serialize() == initial && joined.Equipped == MiningTool.Pickaxe,
            "Tool selection and durability replicate in a bounded snapshot");

        for (int i = 0; i < 80; i++)
            check(tools.OnBlockMined(),
                "Every successful break with the pickaxe consumes durability");
        check(tools.Durability(MiningTool.Pickaxe) == 0 &&
            tools.Equipped == MiningTool.Hand && !tools.OnBlockMined(),
            "Broken tools automatically unequip and hands do not wear down");

        check(!tools.TryEquip(MiningTool.Pickaxe) &&
            tools.TryCraft(materials, MiningTool.Pickaxe) &&
            tools.Durability(MiningTool.Pickaxe) == 80,
            "An exhausted tool can be crafted again for the normal material cost");

        check(tools.TryCraft(materials, MiningTool.Axe) &&
            tools.TryCraft(materials, MiningTool.Shovel),
            "Existing materials can craft an axe and a shovel");
        check(tools.TryEquip(MiningTool.Axe) &&
            BlockMining.Seconds(log, tools.Equipped) <
            BlockMining.Seconds(log, MiningTool.Hand),
            "Equipped axe speeds wood mining");
        check(tools.TryEquip(MiningTool.Shovel) &&
            BlockMining.Seconds(dirt, tools.Equipped) <
            BlockMining.Seconds(dirt, MiningTool.Hand),
            "Equipped shovel speeds dirt mining");

        // A tool can be repaired, but crafting at full durability is blocked
        // so the client cannot consume materials and get no improvement.
        string before = materials.Serialize();
        check(!tools.TryCraft(materials, MiningTool.Shovel) &&
            materials.Serialize() == before,
            "Crafting a tool at full durability must consume nothing");
        check(tools.OnBlockMined() && tools.Durability(MiningTool.Shovel) == 63 &&
            tools.TryCraft(materials, MiningTool.Shovel) &&
            tools.Durability(MiningTool.Shovel) == 64,
            "Crafting repairs a damaged tool to full durability");

        check(tools.TryEquip(MiningTool.Hand) &&
            !tools.TryEquip((MiningTool)99) &&
            !tools.TryCraft(materials, MiningTool.Hand) &&
            !tools.TryCraft(materials, (MiningTool)99),
            "Invalid equipment IDs cannot change authoritative state");

        var scarce = new BlockInventory();
        var empty = new PlayerTools();
        check(scarce.TryCollect(3, 3) &&
            !empty.TryCraft(scarce, MiningTool.Pickaxe) &&
            scarce.Count(3) == 3 && empty.Durability(MiningTool.Pickaxe) == 0,
            "Missing planks cannot partially consume a tool recipe");
        check(scarce.TryCollect(6, 2) &&
            empty.TryCraft(scarce, MiningTool.Pickaxe) &&
            scarce.Count(3) == 0 && scarce.Count(6) == 0,
            "Exact ingredient counts craft one tool without negative materials");

        string preserved = joined.Serialize();
        foreach (string invalid in new[]
        {
            "", "2|80,0,0|1", "1|80,0|1", "1|80,0,0,0|1",
            "1|81,0,0|1", "1|80,-1,0|1", "1|0,0,0|1",
            "1|80,0,0|99", "1|NaN,0,0|0", new string('9', 200)
        })
        {
            check(!joined.TryLoad(invalid) && joined.Serialize() == preserved,
                "Invalid network tool states are rejected without partial changes");
        }

        Console.WriteLine(
            "PASS: server-owned tool crafting, repair, equipment, durability and replication");
    }
}
