using System;

namespace WorldGeneration;

/// <summary>
/// Pure coordinate-based geology. There is no mutable PRNG state, so chunk
/// job order, negative coordinates and clients cannot change world content.
/// </summary>
public static class WorldGeology
{
    public const ushort CoalOre = 7;
    public const ushort IronOre = 8;

    // Integer avalanche hashing avoids platform-dependent random sequences.
    private static uint Hash(int x, int y, int z, int seed, uint salt)
    {
        unchecked
        {
            uint h = (uint)seed ^ salt;
            h ^= (uint)x * 0x9E3779B9u;
            h = (h ^ (h >> 16)) * 0x85EBCA6Bu;
            h ^= (uint)y * 0xC2B2AE35u;
            h = (h ^ (h >> 13)) * 0x27D4EB2Fu;
            h ^= (uint)z * 0x165667B1u;
            h = (h ^ (h >> 16)) * 0x7FEB352Du;
            return h ^ (h >> 15);
        }
    }

    private static float Random01(int x, int y, int z, int seed, uint salt) =>
        (Hash(x, y, z, seed, salt) & 0xFFFFFFu) * (1f / 16777216f);

    private static float Smooth(float t) => t * t * (3f - 2f * t);

    // Continuous world-space noise; no chunk-local sampling coordinates.
    private static float Noise(float x, float y, float z, int seed, uint salt)
    {
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y),
            iz = (int)MathF.Floor(z);
        float tx = Smooth(x - ix), ty = Smooth(y - iy), tz = Smooth(z - iz);
        float low = 0f;
        for (int dz = 0; dz < 2; dz++)
        for (int dy = 0; dy < 2; dy++)
        for (int dx = 0; dx < 2; dx++)
        {
            float wx = dx == 0 ? 1f - tx : tx;
            float wy = dy == 0 ? 1f - ty : ty;
            float wz = dz == 0 ? 1f - tz : tz;
            float value = Random01(ix + dx, iy + dy, iz + dz, seed, salt);
            low += value * wx * wy * wz;
        }
        return low;
    }

    // Shared climate sampling uses the same deterministic hash/noise code
    // as geology, but with separate salts and no dependency on chunk order.
    public static float ClimateNoise(float worldX, float worldZ,
        int seed, uint salt) => Noise(worldX, 0f, worldZ, seed, salt);

    /// <summary>Low-frequency connected cave fields beneath solid cover.</summary>
    public static bool IsCave(int x, int y, int z, int seed)
    {
        // Surface and bottom remain solid so initial player spawns are safe.
        if (y > -5 || y < ChunkGenerator.MinWorldY + 3)
            return false;
        float broad = Noise(x * 0.067f, y * 0.085f, z * 0.067f, seed, 0xCA5E5u);
        float detail = Noise(x * 0.145f, y * 0.14f, z * 0.145f, seed, 0xC0FFEEu);
        if (broad * 0.8f + detail * 0.2f > 0.605f)
            return true;
        // Larger, connected chambers in deeper exploration terrain.
        // Keep the existing underground layout within 128m of origin.
        if ((long)x * x + (long)z * z < 128L * 128L || y > -9)
            return false;
        float cavern = Noise(x * .029f, y * .041f, z * .029f, seed, 0x1A73Fu);
        float rough = Noise(x * .097f, y * .09f, z * .097f, seed, 0xC0A7Eu);
        return cavern * .83f + rough * .17f > .637f;
    }

    /// <summary>Compact local ore pockets in remaining stone; depth-limited.</summary>
    public static ushort OreAt(int x, int y, int z, int seed)
    {
        if (y > -3 || y < ChunkGenerator.MinWorldY + 2)
            return 3;

        // Use separate salted noise channels; iron is rarer and deeper.
        float iron = Noise(x * 0.26f, y * 0.26f, z * 0.26f, seed, 0x1A0B05u);
        if (y <= -12 && iron > 0.755f)
            return IronOre;

        float coal = Noise(x * 0.22f, y * 0.22f, z * 0.22f, seed, 0xC0A1u);
        return coal > 0.705f ? CoalOre : (ushort)3;
    }
}
