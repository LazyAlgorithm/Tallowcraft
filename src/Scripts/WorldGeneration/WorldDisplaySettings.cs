using System;

namespace WorldGeneration;

/// <summary>Player-facing streaming presets and their machine limits.</summary>
public static class WorldDisplaySettings
{
    public const int MaxDistance = 64;
    public static readonly int[] Distances = { 4, 6, 9, 12, 16, 20, 32, 48, 64 };
    public static readonly int[] SideDistances = { 3, 4, 6, 9, 12, 16, 24, 36, 48 };
    public static readonly int[] BehindDistances = { 2, 2, 4, 6, 8, 10, 16, 24, 32 };
    public static readonly string[] DistanceNames =
        { "Near", "Balanced", "Far", "Very far", "Ultra", "Maximum", "Extended", "Horizon", "Extreme" };

    // Leave one logical processor for the game and operating system when possible.
    public static int WorkerCount(int selection, int processors)
    {
        int max = Math.Max(1, processors);
        return selection <= 0 ? Math.Max(1, max - 1) : Math.Clamp(selection, 1, max);
    }

    // Auto, 1, 2, 4, ... and the exact machine maximum.
    public static int NextWorkerSelection(int selection, int processors)
    {
        int max = Math.Max(1, processors);
        if (selection <= 0) return 1;
        if (selection >= max) return 0;
        return selection > max / 2 ? max : Math.Min(max, selection * 2);
    }
}
