using System;
using System.Numerics;

namespace WorldGeneration;

/// <summary>
/// Deterministic animal distribution and loot policy. This class has no engine
/// references so world-seed/biome tests can run without booting the player.
/// The habitat owns the runtime actor and navigation lifecycle.
/// </summary>
public static class WildlifeRules
{
    public const int Sheep = 1;
    public const int Pig = 2;
    public const int Wool = 20;
    public const int RawPork = 21;
    public const float FleeSeconds = 4.5f;
    public const float FleeDistance = 9f;
    public const int SpatialCell = 12;

    private static uint Mix(uint value)
    {
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        value *= 0x846ca68bu;
        return value ^ (value >> 16);
    }

    public static uint Hash(int seed, int x, int z, int salt = 0) =>
        Mix(unchecked((uint)seed * 0x9e3779b9u ^
            (uint)x * 0x85ebca6bu ^
            (uint)z * 0xc2b2ae35u ^ (uint)salt * 0x27d4eb2du));

    /// <summary>Use one random-looking type per 12-metre world grid cell.
    /// Drylands are avoided: grass-feeding animals prefer green terrain.</summary>
    public static int SpeciesAt(int x, int z, int seed)
    {
        WorldBiomes.Biome biome = WorldBiomes.At(x, z, seed);
        uint roll = Hash(seed, x / SpatialCell, z / SpatialCell) % 100;
        return biome switch
        {
            WorldBiomes.Biome.Meadow => roll < 67 ? Sheep : Pig,
            WorldBiomes.Biome.Woodland => roll < 34 ? Sheep : Pig,
            WorldBiomes.Biome.Highlands => roll < 92 ? Sheep : 0,
            _ => 0
        };
    }

    public static bool CanSpawnOn(int species, int groundType,
        int x, int z, int seed) =>
        species is Sheep or Pig &&
        SpeciesAt(x, z, seed) == species &&
        groundType is 1 or 2 or 3;

    /// <summary>Pick a safe, deterministic point opposite the attacker,
    /// with a fallback if they occupy exactly the same X/Z location.</summary>
    public static Vector3 FleeTarget(Vector3 animal, Vector3 attacker, int seed)
    {
        Vector2 away = new(animal.X - attacker.X, animal.Z - attacker.Z);
        if (away.LengthSquared() < .01f)
        {
            uint direction = Hash(seed, (int)MathF.Floor(animal.X),
                (int)MathF.Floor(animal.Z));
            float angle = (direction % 6283) / 1000f;
            away = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }
        away = Vector2.Normalize(away);
        return new Vector3(animal.X + away.X * FleeDistance,
            animal.Y, animal.Z + away.Y * FleeDistance);
    }

    public static int DropType(int species) =>
        species switch { Sheep => Wool, Pig => RawPork, _ => 0 };

    /// <summary>One deterministic drop per kill, 1-3 items; repeated
    /// rendering or replay cannot change its amount.</summary>
    public static int DropAmount(int species, int seed, int x, int z) =>
        DropType(species) == 0 ? 0 :
        1 + (int)(Hash(seed, x, z, species) % 3u);
}
