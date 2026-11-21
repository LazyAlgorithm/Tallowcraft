using System;

namespace WorldGeneration;

/// <summary>
/// Small seeded shrub clusters and abandoned stone foundations. Features are
/// sampled from WORLD coordinates, not random chunk order, so server, clients,
/// chunk buffers, and neighbor mesh queries agree at every chunk seam.
/// </summary>
public static class WorldDecorations
{
    public const ushort Leaves = 9;
    public const ushort Stone = 3;
    public const ushort Planks = 6;

    private const int BushCell = 6;
    private const int RuinCell = 56;
    private const int RuinRadius = 6;
    private const int SpawnClear = 112;

    private readonly record struct Bush(int X, int Z, int GroundY);
    private readonly record struct Ruin(int X, int Z, int GroundY, WorldBiomes.Biome Biome, bool Extended)
    {
        public int TopY => GroundY + (Extended ? 6 : 3);
    }

    private static uint Hash(int x, int z, int seed, uint salt)
    {
        unchecked
        {
            uint h = (uint)seed ^ salt;
            h = (h ^ ((uint)x * 0x9E3779B9u)) * 0x85EBCA6Bu;
            h = (h ^ (h >> 16)) * 0xC2B2AE35u;
            h = (h ^ ((uint)z * 0x165667B1u)) * 0x27D4EB2Fu;
            h = (h ^ (h >> 15)) * 0x7FEB352Du;
            return h ^ (h >> 16);
        }
    }

    private static bool BeyondSpawn(int x, int z, int minRadius) =>
        (long)x * x + (long)z * z >= (long)minRadius * minRadius;

    private static bool TryRuinCell(int cellX, int cellZ, int seed, out Ruin ruin)
    {
        ruin = default;
        uint hash = Hash(cellX, cellZ, seed, 0x51A47B3u);
        if ((hash & 1023u) >= 370u)
            return false;

        // Every ruin remains inside its 56x56 anchor cell by at least 8m.
        // This allows O(1) world-block queries without checking other cells.
        int x = cellX * RuinCell + 8 + (int)((hash >> 10) % 40u);
        int z = cellZ * RuinCell + 8 + (int)((hash >> 18) % 40u);
        if (!BeyondSpawn(x, z, SpawnClear))
            return false;

        WorldBiomes.Biome biome = WorldBiomes.At(x, z, seed);
        if (biome != WorldBiomes.Biome.Meadow &&
            biome != WorldBiomes.Biome.Drylands &&
            biome != WorldBiomes.Biome.Highlands)
            return false;

        int ground = ChunkGenerator.Height(x, z, seed);
        if (ground < 3 || ground > ChunkGenerator.MaxWorldY - 4)
            return false;

        // Level or gently sloped ground. Do not bury half the walls or leave
        // half the foundation floating over a steep hillside.
        for (int dz = -3; dz <= 3; dz++)
        for (int dx = -3; dx <= 3; dx++)
            if (Math.Abs(ChunkGenerator.Height(x + dx, z + dz, seed) - ground) > 1)
                return false;
        // The south-facing entrance must not be obstructed by a neighboring
        // terrain step even though the rest of the foundation is level enough.
        if (ChunkGenerator.Height(x, z - 3, seed) > ground)
            return false;

        // Keep existing foundations on slopes. Build the second chamber
        // only where it can share a walkable entrance with the courtyard.
        bool extended = ground <= ChunkGenerator.MaxWorldY - 6;
        for (int dz = 4; extended && dz <= RuinRadius; dz++)
        for (int dx = -3; dx <= 3; dx++)
            if (Math.Abs(ChunkGenerator.Height(x + dx, z + dz, seed) - ground) > 1)
            {
                extended = false;
                break;
            }
        ruin = new Ruin(x, z, ground, biome, extended);
        return true;
    }

    private static bool TryBushCell(int cellX, int cellZ, int seed, out Bush bush)
    {
        bush = default;
        uint hash = Hash(cellX, cellZ, seed, 0xB057E55u);
        int x = cellX * BushCell + 1 + (int)((hash >> 10) & 3u);
        int z = cellZ * BushCell + 1 + (int)((hash >> 14) & 3u);
        if (!BeyondSpawn(x, z, 64))
            return false;

        WorldBiomes.Biome biome = WorldBiomes.At(x, z, seed);
        uint chance = biome switch
        {
            WorldBiomes.Biome.Woodland => 210u,
            WorldBiomes.Biome.Meadow => 85u,
            _ => 0u
        };
        if ((hash & 1023u) >= chance)
            return false;

        int ground = ChunkGenerator.Height(x, z, seed);
        if (ground < 2 || ground > ChunkGenerator.MaxWorldY - 2)
            return false;

        // Only bush near gentle ground, and never in or next to a ruin.
        if (Math.Abs(ChunkGenerator.Height(x - 1, z, seed) - ground) > 1 ||
            Math.Abs(ChunkGenerator.Height(x + 1, z, seed) - ground) > 1 ||
            Math.Abs(ChunkGenerator.Height(x, z - 1, seed) - ground) > 1 ||
            Math.Abs(ChunkGenerator.Height(x, z + 1, seed) - ground) > 1)
            return false;
        if (NearRuin(x, z, seed, RuinRadius + 2))
            return false;

        bush = new Bush(x, z, ground);
        return true;
    }

    /// <summary>True within range of a seeded ruin's center.</summary>
    public static bool NearRuin(int x, int z, int seed, int radius)
    {
        int cellX = ChunkGenerator.FloorDiv(x, RuinCell);
        int cellZ = ChunkGenerator.FloorDiv(z, RuinCell);
        // Candidate origins are inset 8m from cell borders. Callers of
        // NearRuin use radii <= 8m, so only the containing cell is needed.
        uint hash = Hash(cellX, cellZ, seed, 0x51A47B3u);
        if ((hash & 1023u) >= 370u) return false;
        int rootX = cellX * RuinCell + 8 + (int)((hash >> 10) % 40u);
        int rootZ = cellZ * RuinCell + 8 + (int)((hash >> 18) % 40u);
        return Math.Abs(x - rootX) <= radius &&
            Math.Abs(z - rootZ) <= radius &&
            TryRuinCell(cellX, cellZ, seed, out _);
    }

    private static ushort? RuinShape(Ruin ruin, int x, int y, int z, int seed)
    {
        int dx = x - ruin.X, dz = z - ruin.Z;
        int ax = Math.Abs(dx), az = Math.Abs(dz);
        int level = y - ruin.GroundY;
        if (ax > 3 || dz < -3 || dz > (ruin.Extended ? RuinRadius : 3) || level < -1 || y > ruin.TopY)
            return null;
        if (level == -1)
            return ax <= 2 && az <= 2 ? Planks : Stone;

        // The rear chamber and courtyard share one continuous floor.
        if (ruin.Extended && dz >= 3)
        {
            if (dz == 3 && ax <= 1) return 0;
            bool corner = ax == 3 && (dz == 3 || dz == RuinRadius);
            bool rear = dz == RuinRadius;
            switch (ruin.Biome)
            {
                case WorldBiomes.Biome.Meadow:
                    if (corner && level <= 4) return BlockInventory.DarkTimber;
                    if (level == 4 && (rear || ax == 3)) return Planks;
                    if (level == 5 && rear && ax <= 2) return Planks;
                    break;
                case WorldBiomes.Biome.Drylands:
                    if (rear && (ax >= 2 || level >= 4) && level <= 5)
                        return BlockInventory.StoneBricks;
                    if (corner && level <= 2) return Stone;
                    break;
                case WorldBiomes.Biome.Highlands:
                    if (rear && ax >= 2 && level <= (dx > 0 ? 6 : 4))
                        return BlockInventory.Cobblestone;
                    if (ax == 3 && level <= 2) return Stone;
                    break;
            }
            return 0;
        }

        if (ax != 3 && az != 3) return 0;
        if (dz == -3 && ax <= 1 && level < 3) return 0;
        int wallHeight = ax == 3 && az == 3 ? 3 :
            1 + (int)(Hash(x, z, seed, 0x7A1105u) % 4u);
        return level < wallHeight ? Stone : (ushort)0;
    }

    // 0 is bare ground, 1 grass, 2 daisies. Selection is independent of
    // chunk load order. The renderer additionally requires exposed turf.
    public static int GroundCoverAt(int x, int z, int seed)
    {
        uint sample = Hash(x, z, seed, 0x7165Bu) & 255;
        if (sample >= 44) return 0;
        return WorldBiomes.At(x, z, seed) switch
        {
            WorldBiomes.Biome.Meadow => sample < 9 ? 2 : 1,
            WorldBiomes.Biome.Woodland when sample < 24 => 1,
            _ => 0
        };
    }

    private static ushort BushShape(Bush bush, int x, int y, int z)
    {
        int dx = Math.Abs(x - bush.X), dz = Math.Abs(z - bush.Z);
        if (y == bush.GroundY + 1 && dx == 0 && dz == 0)
            return Leaves;
        if (y == bush.GroundY && dx <= 1 && dz <= 1 &&
            dx + dz <= 1)
            return Leaves;
        return 0;
    }

    private static bool TryNearbyRuin(int x, int z, int seed, out Ruin ruin)
    {
        ruin = default;
        if (!BeyondSpawn(x, z, SpawnClear - RuinRadius * 2)) return false;
        int cellX = ChunkGenerator.FloorDiv(x, RuinCell);
        int cellZ = ChunkGenerator.FloorDiv(z, RuinCell);
        uint hash = Hash(cellX, cellZ, seed, 0x51A47B3u);
        if ((hash & 1023u) >= 370u) return false;
        int rootX = cellX * RuinCell + 8 + (int)((hash >> 10) % 40u);
        int rootZ = cellZ * RuinCell + 8 + (int)((hash >> 18) % 40u);
        return Math.Abs(x - rootX) <= 3 && z >= rootZ - 3 && z <= rootZ + RuinRadius &&
            TryRuinCell(cellX, cellZ, seed, out ruin);
    }

    // A zero override clears terrain intruding into the walkable interior.
    // Null leaves the terrain and vegetation at this position untouched.
    internal static ushort? StructureOverrideAt(int x, int y, int z, int seed)
    {
        if (y < 0 || y > ChunkGenerator.MaxWorldY) return null;
        if (TryNearbyRuin(x, z, seed, out Ruin ruin))
            return RuinShape(ruin, x, y, z, seed);
        return WorldLandmarks.Override(x, y, z, seed);
    }

    public static ushort StructureAt(int x, int y, int z, int seed) =>
        StructureOverrideAt(x, y, z, seed) ?? 0;

    internal static int SurfaceWithRuin(int x, int z, int seed, int terrainHeight)
    {
        if (TryNearbyRuin(x, z, seed, out Ruin ruin))
            for (int y = ruin.TopY; y >= ruin.GroundY - 1; y--)
                if (RuinShape(ruin, x, y, z, seed) is > 0)
                    return y + 1;
        return terrainHeight;
    }

    /// <summary>A shrub leaf block, or 0, below tree and ruin precedence.</summary>
    public static ushort BushAt(int x, int y, int z, int seed)
    {
        if (y < 2 || y > ChunkGenerator.MaxWorldY ||
            !BeyondSpawn(x, z, 62))
            return 0;
        int cellX = ChunkGenerator.FloorDiv(x, BushCell);
        int cellZ = ChunkGenerator.FloorDiv(z, BushCell);
        uint hash = Hash(cellX, cellZ, seed, 0xB057E55u);
        if ((hash & 1023u) >= 210u)
            return 0;
        int rootX = cellX * BushCell + 1 + (int)((hash >> 10) & 3u);
        int rootZ = cellZ * BushCell + 1 + (int)((hash >> 14) & 3u);
        if (Math.Abs(x - rootX) > 1 || Math.Abs(z - rootZ) > 1)
            return 0;
        return TryBushCell(cellX, cellZ, seed, out Bush bush)
            ? BushShape(bush, x, y, z) : (ushort)0;
    }

    /// <summary>Locate a deterministic ruin in a world-space feature cell.</summary>
    public static bool TryRuinCellRoot(int cellX, int cellZ, int seed,
        out BlockPosition root)
    {
        if (TryRuinCell(cellX, cellZ, seed, out Ruin ruin))
        {
            root = new BlockPosition(ruin.X, ruin.GroundY, ruin.Z);
            return true;
        }
        root = default;
        return false;
    }

    /// <summary>Locate a deterministic shrub in a world-space feature cell.</summary>
    public static bool TryBushCellRoot(int cellX, int cellZ, int seed,
        out BlockPosition root)
    {
        if (TryBushCell(cellX, cellZ, seed, out Bush bush))
        {
            root = new BlockPosition(bush.X, bush.GroundY, bush.Z);
            return true;
        }
        root = default;
        return false;
    }

    public static bool TryRuinAt(int x, int z, int seed, out int groundY)
    {
        int cellX = ChunkGenerator.FloorDiv(x, RuinCell);
        int cellZ = ChunkGenerator.FloorDiv(z, RuinCell);
        if (TryRuinCell(cellX, cellZ, seed, out Ruin ruin) &&
            ruin.X == x && ruin.Z == z)
        {
            groundY = ruin.GroundY;
            return true;
        }
        groundY = 0;
        return false;
    }

    public static bool TryBushAt(int x, int z, int seed, out int groundY)
    {
        int cellX = ChunkGenerator.FloorDiv(x, BushCell);
        int cellZ = ChunkGenerator.FloorDiv(z, BushCell);
        if (TryBushCell(cellX, cellZ, seed, out Bush bush) &&
            bush.X == x && bush.Z == z)
        {
            groundY = bush.GroundY;
            return true;
        }
        groundY = 0;
        return false;
    }

    private static void NearbyRuins(ChunkKey key, int seed, Action<Ruin> apply)
    {
        int x0 = key.X * ChunkGenerator.Size - RuinRadius;
        int x1 = (key.X + 1) * ChunkGenerator.Size - 1 + RuinRadius;
        int z0 = key.Z * ChunkGenerator.Size - RuinRadius;
        int z1 = (key.Z + 1) * ChunkGenerator.Size - 1 + RuinRadius;
        for (int cz = ChunkGenerator.FloorDiv(z0, RuinCell);
             cz <= ChunkGenerator.FloorDiv(z1, RuinCell); cz++)
        for (int cx = ChunkGenerator.FloorDiv(x0, RuinCell);
             cx <= ChunkGenerator.FloorDiv(x1, RuinCell); cx++)
            if (TryRuinCell(cx, cz, seed, out Ruin ruin))
                apply(ruin);
    }

    private static void NearbyBushes(ChunkKey key, int seed, Action<Bush> apply)
    {
        int x0 = key.X * ChunkGenerator.Size - 1;
        int x1 = (key.X + 1) * ChunkGenerator.Size;
        int z0 = key.Z * ChunkGenerator.Size - 1;
        int z1 = (key.Z + 1) * ChunkGenerator.Size;
        for (int cz = ChunkGenerator.FloorDiv(z0, BushCell);
             cz <= ChunkGenerator.FloorDiv(z1, BushCell); cz++)
        for (int cx = ChunkGenerator.FloorDiv(x0, BushCell);
             cx <= ChunkGenerator.FloorDiv(x1, BushCell); cx++)
            if (TryBushCell(cx, cz, seed, out Bush bush))
                apply(bush);
    }

    /// <summary>Check extra above-ground chunks without requiring edits.</summary>
    public static bool HasBlocksInChunk(ChunkKey key, int seed)
    {
        if (key.Y < 0 || key.Y > ChunkGenerator.MaxChunkY)
            return false;
        int minY = key.Y * ChunkGenerator.Size;
        int maxY = minY + ChunkGenerator.Size - 1;
        bool found = false;
        NearbyRuins(key, seed, ruin =>
        {
            if (!found && ruin.GroundY - 1 <= maxY &&
                ruin.TopY >= minY &&
                ruin.X + RuinRadius >= key.X * ChunkGenerator.Size &&
                ruin.X - RuinRadius < (key.X + 1) * ChunkGenerator.Size &&
                ruin.Z + RuinRadius >= key.Z * ChunkGenerator.Size &&
                ruin.Z - RuinRadius < (key.Z + 1) * ChunkGenerator.Size)
                found = true;
        });
        if (found || WorldLandmarks.HasBlocksInChunk(key, seed)) return true;
        NearbyBushes(key, seed, bush =>
        {
            if (!found && bush.GroundY <= maxY &&
                bush.GroundY + 1 >= minY &&
                bush.X + 1 >= key.X * ChunkGenerator.Size &&
                bush.X - 1 < (key.X + 1) * ChunkGenerator.Size &&
                bush.Z + 1 >= key.Z * ChunkGenerator.Size &&
                bush.Z - 1 < (key.Z + 1) * ChunkGenerator.Size)
                found = true;
        });
        return found;
    }

    /// <summary>Apply decorations before player edits, preserving their edits.</summary>
    public static void DecorateChunk(ChunkKey key, int seed, ushort[] blocks)
    {
        if (key.Y < 0 || key.Y > ChunkGenerator.MaxChunkY)
            return;

        int x0 = key.X * ChunkGenerator.Size;
        int y0 = key.Y * ChunkGenerator.Size;
        int z0 = key.Z * ChunkGenerator.Size;

        void Write(int x, int y, int z, ushort type, bool overwrite)
        {
            int lx = x - x0, ly = y - y0, lz = z - z0;
            if ((uint)lx >= ChunkGenerator.Size ||
                (uint)ly >= ChunkGenerator.Size ||
                (uint)lz >= ChunkGenerator.Size)
                return;
            int index = ChunkGenerator.Index(lx, ly, lz);
            if (overwrite || blocks[index] == 0)
                blocks[index] = type;
        }

        // Tree foliage is lower precedence than ruined construction.
        NearbyRuins(key, seed, ruin =>
        {
            for (int y = ruin.GroundY - 1; y <= ruin.TopY; y++)
            for (int z = ruin.Z - RuinRadius; z <= ruin.Z + RuinRadius; z++)
            for (int x = ruin.X - RuinRadius; x <= ruin.X + RuinRadius; x++)
            {
                if (RuinShape(ruin, x, y, z, seed) is ushort type)
                    Write(x, y, z, type, true);
            }
        });

        WorldLandmarks.DecorateChunk(key, seed, blocks);

        NearbyBushes(key, seed, bush =>
        {
            for (int y = bush.GroundY; y <= bush.GroundY + 1; y++)
            for (int z = bush.Z - 1; z <= bush.Z + 1; z++)
            for (int x = bush.X - 1; x <= bush.X + 1; x++)
            {
                ushort type = BushShape(bush, x, y, z);
                if (type != 0)
                    Write(x, y, z, type, false);
            }
        });
    }
}
