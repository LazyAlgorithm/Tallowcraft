using System;

namespace WorldGeneration;

/// <summary>
/// Deterministic shallow river channels and occasional lake bowls. Surface
/// water is a decorative noncolliding mesh; the terrain below it is actual
/// editable stone/dirt and participates in all collision/LOD paths.
/// No changed terrain in the original 128m development neighbourhood.
/// </summary>
public static class WorldWater
{
    public const int SpawnProtection = 128;
    public const int WaterLevel = 8;
    private const int RiverPeriod = 184;
    private const int LakeCell = 160;

    public static float SpawnBlend(int x, int z)
    {
        float radius = MathF.Sqrt((float)((long)x * x + (long)z * z));
        return Math.Clamp((radius - SpawnProtection) / 28f, 0f, 1f);
    }

    private static float RiverDistance(int x, int z, int seed)
    {
        // Neighbouring channels share a winding, world-space phase. Round
        // relative to the shifted path so negative chunks are symmetric.
        float bend = 19f * MathF.Sin((z + seed) * .016f) +
            9f * MathF.Sin((z - seed * 2) * .041f);
        float shifted = x - bend;
        return MathF.Abs(shifted - MathF.Round(shifted / RiverPeriod) * RiverPeriod);
    }

    private static (float Dist, float Radius) NearestLake(int x, int z, int seed)
    {
        int cx = ChunkGenerator.FloorDiv(x, LakeCell);
        int cz = ChunkGenerator.FloorDiv(z, LakeCell);
        // Candidate centers are inset at least 24m into 160m cells and
        // lakes have at most a 20m radius. They cannot cross a cell edge:
        // sampling 3x3 neighbours did nine hashes per height query for no
        // visible benefit, particularly expensive at 64-chunk far LOD.
        uint hash = WildlifeRules.Hash(seed, cx, cz, 0x1A6E);
        if ((hash & 255u) > 85u)
            return (100000f, 0f);
        int centerX = cx * LakeCell + 24 + (int)((hash >> 8) % 112u);
        int centerZ = cz * LakeCell + 24 + (int)((hash >> 16) % 112u);
        float dx = (float)((long)x - centerX);
        float dz = (float)((long)z - centerZ);
        float radius = 11f + (hash >> 24) % 10u;
        return (MathF.Sqrt(dx * dx + dz * dz), radius);
    }

    /// <summary>How much of the natural heightmap is carved at a column.
    /// Water is rendered at a constant surface only when the resulting
    /// bottom lies below sea level and the uncarved bank is low enough.</summary>
    public static float Carve(int x, int z, int seed, int originalHeight)
    {
        float blend = SpawnBlend(x, z);
        if (blend <= 0f || originalHeight < 4 || originalHeight > 16)
            return 0f;
        float riverDistance = RiverDistance(x, z, seed);
        float channel = Math.Clamp((8f - riverDistance) / 5f, 0f, 1f);
        var lake = NearestLake(x, z, seed);
        float lakeFill = Math.Clamp((lake.Radius - lake.Dist) / 5f, 0f, 1f);
        float depth = MathF.Max(channel * 5f, lakeFill * 6f);
        return blend * depth;
    }

    public static bool HasSurface(int x, int z, int seed)
    {
        if (SpawnBlend(x, z) <= 0f) return false;
        int originalHeight = ChunkGenerator.RawHeight(x, z, seed);
        if (originalHeight > WaterLevel + 3 || originalHeight < 5)
            return false;
        int carved = ChunkGenerator.Height(x, z, seed);
        return carved <= WaterLevel &&
            Carve(x, z, seed, originalHeight) > 1.1f;
    }

    public static float SurfaceY => WaterLevel + 0.14f;
}
