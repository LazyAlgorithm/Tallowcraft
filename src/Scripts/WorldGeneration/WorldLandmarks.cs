using System;

namespace WorldGeneration;

/// <summary>
/// Additional seeded explorable locations: stone circles and timber
/// observation decks. O(1) direct queries and chunk decoration use the
/// same shape function, so seams and edits remain deterministic.
/// </summary>
public static class WorldLandmarks
{
    public const int CellSize = 112;
    public const int Radius = 4;
    public enum Kind { StoneCircle, Lookout }

    public readonly record struct Landmark(
        int X, int Y, int Z, Kind Style)
    {
        public int TopY => Y + (Style == Kind.Lookout ? 7 : 5);
    }

    public static bool TryRoot(int cellX, int cellZ, int seed, out Landmark site)
    {
        site = default;
        uint h = WildlifeRules.Hash(seed, cellX, cellZ, 0x1A7E);
        if (h % 100u >= 48u) return false;
        int x = cellX * CellSize + 16 + (int)((h >> 8) % 80u);
        int z = cellZ * CellSize + 16 + (int)((h >> 16) % 80u);
        if ((long)x * x + (long)z * z < 144L * 144L ||
            WorldWater.HasSurface(x, z, seed) ||
            WorldDecorations.NearRuin(x, z, seed, 9))
            return false;
        WorldBiomes.Biome biome = WorldBiomes.At(x, z, seed);
        if (biome is WorldBiomes.Biome.Drylands or WorldBiomes.Biome.Wetlands)
            return false;
        int ground = ChunkGenerator.Height(x, z, seed);
        if (ground < 4 || ground > 34) return false;
        for (int dz = -Radius; dz <= Radius; dz += 2)
        for (int dx = -Radius; dx <= Radius; dx += 2)
        {
            int sampled = ChunkGenerator.Height(x + dx, z + dz, seed);
            if (Math.Abs(sampled - ground) > 1 ||
                WorldWater.HasSurface(x + dx, z + dz, seed))
                return false;
        }
        Kind style = biome is WorldBiomes.Biome.Woodland or
            WorldBiomes.Biome.Meadow ? Kind.Lookout : Kind.StoneCircle;
        site = new Landmark(x, ground, z, style);
        return true;
    }

    /// <summary>Blocks in the landmark's footprint; null leaves natural
    /// terrain intact, zero clears the interior of the structure.</summary>
    public static ushort? Shape(Landmark site, int x, int y, int z)
    {
        int dx = x - site.X, dz = z - site.Z;
        int ax = Math.Abs(dx), az = Math.Abs(dz), height = y - site.Y;
        if (ax > Radius || az > Radius || height < -1 ||
            height > site.TopY - site.Y) return null;
        if (site.Style == Kind.StoneCircle)
        {
            if (height == -1 && dx * dx + dz * dz <= 12)
                return BlockInventory.Slate;
            bool pillar = (ax == 3 && (az == 0 || az == 2)) ||
                (az == 3 && (ax == 0 || ax == 2));
            if (pillar && height >= 0 && height <= (ax == az ? 4 : 3))
                return height == 3 ? BlockInventory.MossyStone :
                    BlockInventory.StoneBricks;
            if (height == 0 && dx == 0 && dz == 0)
                return BlockInventory.Slate;
            return null;
        }

        bool corner = ax == 3 && az == 3;
        if (height == -1 && ax <= 3 && az <= 3)
            return BlockInventory.DarkTimber;
        if (corner && height <= 6 && height >= 0)
            return BlockInventory.DarkTimber;
        if (height == 5 && ax <= 3 && az <= 3)
            return (ushort)6;
        if (height == 6 && (ax == 3 || az == 3))
            return BlockInventory.DarkTimber;
        if (height == 7 && ax <= 1 && az <= 1)
            return (ushort)6;
        return null;
    }

    public static bool TryNear(int x, int z, int seed, out Landmark site)
    {
        site = default;
        int cx = ChunkGenerator.FloorDiv(x, CellSize);
        int cz = ChunkGenerator.FloorDiv(z, CellSize);
        // Hot path: this query runs for every world-space voxel. Reject
        // cells outside the footprint BEFORE slope/biome/water checks in
        // TryRoot, otherwise even ordinary air becomes expensive to query.
        uint hash = WildlifeRules.Hash(seed, cx, cz, 0x1A7E);
        if (hash % 100u >= 48u) return false;
        int rootX = cx * CellSize + 16 + (int)((hash >> 8) % 80u);
        int rootZ = cz * CellSize + 16 + (int)((hash >> 16) % 80u);
        if (Math.Abs(x - rootX) > Radius ||
            Math.Abs(z - rootZ) > Radius ||
            !TryRoot(cx, cz, seed, out Landmark root))
            return false;
        site = root;
        return true;
    }

    public static ushort? Override(int x, int y, int z, int seed) =>
        y >= 0 && TryNear(x, z, seed, out Landmark site)
            ? Shape(site, x, y, z) : null;

    public static void Nearby(ChunkKey key, int seed, Action<Landmark> apply)
    {
        int x0 = key.X * ChunkGenerator.Size - Radius;
        int z0 = key.Z * ChunkGenerator.Size - Radius;
        int x1 = (key.X + 1) * ChunkGenerator.Size - 1 + Radius;
        int z1 = (key.Z + 1) * ChunkGenerator.Size - 1 + Radius;
        for (int cz = ChunkGenerator.FloorDiv(z0, CellSize);
            cz <= ChunkGenerator.FloorDiv(z1, CellSize); cz++)
        for (int cx = ChunkGenerator.FloorDiv(x0, CellSize);
            cx <= ChunkGenerator.FloorDiv(x1, CellSize); cx++)
            if (TryRoot(cx, cz, seed, out Landmark site))
                apply(site);
    }

    public static bool HasBlocksInChunk(ChunkKey key, int seed)
    {
        bool found = false;
        int minY = key.Y * ChunkGenerator.Size;
        int maxY = minY + ChunkGenerator.Size - 1;
        Nearby(key, seed, site =>
        {
            if (site.Y - 1 <= maxY && site.TopY >= minY &&
                site.X - Radius < (key.X + 1) * ChunkGenerator.Size &&
                site.X + Radius >= key.X * ChunkGenerator.Size &&
                site.Z - Radius < (key.Z + 1) * ChunkGenerator.Size &&
                site.Z + Radius >= key.Z * ChunkGenerator.Size)
                found = true;
        });
        return found;
    }

    public static void DecorateChunk(ChunkKey key, int seed, ushort[] blocks)
    {
        int x0 = key.X * ChunkGenerator.Size;
        int y0 = key.Y * ChunkGenerator.Size;
        int z0 = key.Z * ChunkGenerator.Size;
        Nearby(key, seed, site =>
        {
            for (int y = site.Y - 1; y <= site.TopY; y++)
            for (int z = site.Z - Radius; z <= site.Z + Radius; z++)
            for (int x = site.X - Radius; x <= site.X + Radius; x++)
            {
                int lx = x - x0, ly = y - y0, lz = z - z0;
                if ((uint)lx >= ChunkGenerator.Size ||
                    (uint)ly >= ChunkGenerator.Size ||
                    (uint)lz >= ChunkGenerator.Size)
                    continue;
                if (Shape(site, x, y, z) is ushort type)
                    blocks[ChunkGenerator.Index(lx, ly, lz)] = type;
            }
        });
    }
}
