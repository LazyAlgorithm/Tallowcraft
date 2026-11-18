using System;

namespace WorldGeneration;

/// <summary>
/// World-space temperature, moisture and relief. Biome colors currently reuse
/// approved atlas tiles; no new art or block IDs are introduced.
/// </summary>
public static class WorldBiomes
{
    public enum Biome { Meadow, Woodland, Drylands, Highlands }

    private static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);
    private static float Smooth(float t) => t * t * (3f - 2f * t);

    // Gentle blend with the existing spawn terrain; preserves original
    // heightmap in the central 40m for familiar spawn placement.
    public static float DevelopmentBlend(int x, int z)
    {
        float distance = MathF.Sqrt((float)((long)x * x + (long)z * z));
        return Smooth(Clamp01((distance - 40f) / 80f));
    }

    public static float Temperature(int x, int z, int seed) =>
        WorldGeology.ClimateNoise(x * 0.0065f, z * 0.0065f, seed, 0x7E1A0u);

    public static float Moisture(int x, int z, int seed) =>
        WorldGeology.ClimateNoise(x * 0.0085f, z * 0.0085f, seed, 0xA1157u);

    public static float Relief(int x, int z, int seed) =>
        WorldGeology.ClimateNoise(x * 0.0175f, z * 0.0175f, seed, 0xB1055u);

    public static Biome At(int x, int z, int seed)
    {
        float temperature = Temperature(x, z, seed);
        float moisture = Moisture(x, z, seed);
        float relief = Relief(x, z, seed);
        if (relief > 0.64f) return Biome.Highlands;
        if (moisture < 0.35f && temperature > 0.48f) return Biome.Drylands;
        if (moisture > 0.57f) return Biome.Woodland;
        return Biome.Meadow;
    }

    /// <summary>
    /// Smooth relief, not a stepped biome border. Surface elevations can
    /// cross Y=16; highland material is currently stone, drylands dirt.
    /// </summary>
    public static float HeightOffset(int x, int z, int seed)
    {
        float relief = Relief(x, z, seed);
        float mountain = Smooth(Clamp01((relief - 0.46f) / 0.28f));
        float valley = Smooth(Clamp01((0.40f - relief) / 0.25f));
        float detail = WorldGeology.ClimateNoise(x * 0.044f,
            z * 0.044f, seed, 0xD37A11u);
        return DevelopmentBlend(x, z) *
            (mountain * (11f + detail * 10f) - valley * 3.5f);
    }
}
