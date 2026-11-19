using System;

namespace WorldGeneration;

/// <summary>
/// Shared, deterministic hardness values. Only the server advances breaking
/// progress; a future equipment slot can select a tool without changing the
/// material or networking protocol.
/// </summary>
public enum MiningTool : byte { Hand, Pickaxe, Axe, Shovel }

public static class BlockMining
{
    public static float Seconds(ushort packedState, MiningTool tool = MiningTool.Hand)
    {
        var type = new BlockState(packedState).Type;
        float seconds = type switch
        {
            1 => 0.25f, // grass
            2 => 0.20f, // dirt
            3 => 0.85f, // stone
            4 => 0.60f, // log
            5 => 1.60f, // forge
            6 => 0.40f, // planks
            7 => 1.15f, // coal
            8 => 1.40f, // iron
            9 => 0.12f, // leaves
            _ => 0f
        };

        if (seconds == 0f || tool == MiningTool.Hand)
            return seconds;

        bool effective = tool switch
        {
            MiningTool.Pickaxe => type is 3 or 5 or 7 or 8,
            MiningTool.Axe => type is 4 or 6,
            MiningTool.Shovel => type is 1 or 2,
            _ => false
        };
        return effective ? seconds / 3f : seconds;
    }

    public static int Percent(float elapsed, ushort packedState,
        MiningTool tool = MiningTool.Hand)
    {
        float duration = Seconds(packedState, tool);
        if (duration <= 0f || !float.IsFinite(elapsed))
            return 0;
        return (int)Math.Clamp(elapsed / duration * 100f, 0f, 100f);
    }
}
