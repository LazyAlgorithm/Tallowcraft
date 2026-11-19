using System;
using WorldGeneration;

internal static class MiningTests
{
    public static void Run(Action<bool, string> check)
    {
        ushort dirt = BlockState.Create(2).Packed;
        ushort stone = BlockState.Create(3).Packed;
        ushort logStair = BlockState.Create(4, BlockShape.Stair, 2).Packed;
        ushort iron = BlockState.Create(8).Packed;
        ushort leaves = BlockState.Create(9).Packed;

        check(BlockMining.Seconds(dirt) < BlockMining.Seconds(stone) &&
            BlockMining.Seconds(leaves) < BlockMining.Seconds(logStair) &&
            BlockMining.Seconds(iron) > BlockMining.Seconds(stone),
            "Mining hardness must vary by material");
        check(BlockMining.Seconds(logStair) == BlockMining.Seconds(
            BlockState.Create(4).Packed),
            "Partial/oriented blocks share their material mining hardness");
        check(BlockMining.Seconds(stone, MiningTool.Pickaxe) <
            BlockMining.Seconds(stone) &&
            BlockMining.Seconds(logStair, MiningTool.Axe) <
            BlockMining.Seconds(logStair) &&
            BlockMining.Seconds(dirt, MiningTool.Shovel) <
            BlockMining.Seconds(dirt),
            "Correct equipment types can accelerate mining when added");
        check(BlockMining.Seconds(stone, MiningTool.Axe) ==
            BlockMining.Seconds(stone),
            "Incorrect tool does not accelerate a material");
        check(BlockMining.Percent(0f, stone) == 0 &&
            BlockMining.Percent(BlockMining.Seconds(stone) / 2f, stone) == 50 &&
            BlockMining.Percent(BlockMining.Seconds(stone), stone) == 100 &&
            BlockMining.Percent(500f, stone) == 100,
            "Mining progress is bounded and reaches completion");
        check(BlockMining.Seconds(0) == 0f &&
            BlockMining.Percent(500f, 0) == 0 &&
            BlockMining.Percent(float.NaN, stone) == 0,
            "Air and invalid progress cannot be mined");
        Console.WriteLine("PASS: server mining hardness, progress and future tool multipliers");
    }
}
