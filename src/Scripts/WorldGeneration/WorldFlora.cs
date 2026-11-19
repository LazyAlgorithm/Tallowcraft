using System;

namespace WorldGeneration;

/// <summary>
/// Seeded oak and pine voxel trees. Anchors live on an 8x8 world grid
/// (one jittered root per cell), so tree shapes never depend on chunk job order.
/// Canopies may overlap across horizontal chunk boundaries; trunks always take
/// precedence over leaves. Trunks reuse type 4; opaque leaves use type 9.
/// </summary>
public static class WorldFlora
{
    public const ushort Log = 4;
    public const ushort Leaves = 9;
    private const int CellSize = 8;
    private const int CanopyRadius = 2;
    private const int SpawnClearRadius = 60;

    private readonly record struct Tree(int X, int Z, int GroundY, int WoodHeight,
        bool IsPine)
    {
        public int CrownY => GroundY + WoodHeight - 1;
    }

    private static uint Hash(int x, int z, int seed)
    {
        unchecked
        {
            uint h = (uint)seed ^ 0x7EEA9A33u;
            h = (h ^ ((uint)x * 0x9E3779B9u)) * 0x85EBCA6Bu;
            h = (h ^ ((uint)z * 0xC2B2AE35u)) * 0x27D4EB2Fu;
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            return h ^ (h >> 15);
        }
    }

    private static bool TryTreeInCell(int cellX, int cellZ, int seed,
        out Tree tree)
    {
        tree = default;
        uint hash = Hash(cellX, cellZ, seed);
        int x = cellX * CellSize + (int)((hash >> 10) & 7);
        int z = cellZ * CellSize + (int)((hash >> 13) & 7);

        // Preserve the already-tested initial spawn and its approach.
        if ((long)x * x + (long)z * z < (long)SpawnClearRadius * SpawnClearRadius)
            return false;

        // Cheap early rejection, then resolve climate only for candidates.
        if ((hash & 1023u) >= 680u)
            return false;
        // Avoid trunks or canopies obstructing a natural ruin. This depends
        // only on the same world seed, not on whether the ruin chunk is loaded.
        if (WorldDecorations.NearRuin(x, z, seed, 5))
            return false;
        WorldBiomes.Biome biome = WorldBiomes.At(x, z, seed);
        uint chance = biome switch
        {
            WorldBiomes.Biome.Woodland => 680u,
            WorldBiomes.Biome.Meadow => 120u,
            WorldBiomes.Biome.Highlands => 240u,
            _ => 0u
        };
        if ((hash & 1023u) >= chance)
            return false;

        int ground = ChunkGenerator.Height(x, z, seed);
        if (ground < 2 || ground > 34)
            return false;

        // Prevent trees from standing unsupported on sharp cliff edges.
        if (Math.Abs(ChunkGenerator.Height(x - 1, z, seed) - ground) > 2 ||
            Math.Abs(ChunkGenerator.Height(x + 1, z, seed) - ground) > 2 ||
            Math.Abs(ChunkGenerator.Height(x, z - 1, seed) - ground) > 2 ||
            Math.Abs(ChunkGenerator.Height(x, z + 1, seed) - ground) > 2)
            return false;

        bool pine = biome == WorldBiomes.Biome.Highlands ||
            (biome == WorldBiomes.Biome.Woodland && ((hash >> 24) & 3u) == 0u);
        tree = new Tree(x, z, ground,
            pine ? 6 + (int)((hash >> 16) % 3u)
                : 4 + (int)((hash >> 16) % 3u), pine);
        return true;
    }

    public static bool TryTreeAt(int worldX, int worldZ, int seed,
        out int groundY, out int trunkHeight) =>
        TryTreeAt(worldX, worldZ, seed,
            out groundY, out trunkHeight, out _);

    public static bool TryTreeAt(int worldX, int worldZ, int seed,
        out int groundY, out int trunkHeight, out bool pine)
    {
        groundY = trunkHeight = 0;
        pine = false;
        int cx = ChunkGenerator.FloorDiv(worldX, CellSize);
        int cz = ChunkGenerator.FloorDiv(worldZ, CellSize);
        if (!TryTreeInCell(cx, cz, seed, out Tree tree) ||
            tree.X != worldX || tree.Z != worldZ)
            return false;
        groundY = tree.GroundY;
        trunkHeight = tree.WoodHeight;
        pine = tree.IsPine;
        return true;
    }

    private static ushort ShapeAt(Tree tree, int x, int y, int z)
    {
        int dx = Math.Abs(x - tree.X), dz = Math.Abs(z - tree.Z);
        if (dx == 0 && dz == 0 &&
            y >= tree.GroundY && y <= tree.CrownY)
            return Log;

        int level = y - tree.CrownY;
        int radius;
        if (tree.IsPine)
        {
            // Layered taper gives pine trees an identifiable narrow profile
            // without adding atlas tiles or relying on transparency.
            radius = level switch
            {
                -5 => 1, -4 => 2, -3 => 1, -2 => 2,
                -1 => 1, 0 => 1, 1 => 0, _ => -1
            };
        }
        else
        {
            radius = level < -2 || level > 1 ? -1
                : level >= 0 ? 1 : CanopyRadius;
        }
        if (radius < 0 || dx > radius || dz > radius)
            return 0;
        if (radius == 2 && dx == 2 && dz == 2)
            return 0;
        return Leaves;
    }

    /// <summary>
    /// Get a natural tree block at any world coordinate. Callers apply terrain
    /// and edit precedence separately. Used for cross-chunk neighbor queries.
    /// </summary>
    public static ushort BlockAt(int x, int y, int z, int seed)
    {
        if (y < 2 || y > ChunkGenerator.MaxWorldY ||
            (long)x * x + (long)z * z <
                (long)(SpawnClearRadius - 3) *
                (SpawnClearRadius - 3))
            return 0;

        ushort result = 0;
        for (int cz = ChunkGenerator.FloorDiv(z - CanopyRadius, CellSize);
             cz <= ChunkGenerator.FloorDiv(z + CanopyRadius, CellSize); cz++)
        for (int cx = ChunkGenerator.FloorDiv(x - CanopyRadius, CellSize);
             cx <= ChunkGenerator.FloorDiv(x + CanopyRadius, CellSize); cx++)
            if (TryTreeInCell(cx, cz, seed, out Tree tree))
            {
                ushort type = ShapeAt(tree, x, y, z);
                if (type == Log)
                    return Log;
                if (type == Leaves)
                    result = Leaves;
            }
        return result;
    }

    private static void ForNearbyTrees(ChunkKey key, int seed,
        Action<Tree> apply)
    {
        int x0 = key.X * ChunkGenerator.Size - CanopyRadius;
        int x1 = (key.X + 1) * ChunkGenerator.Size - 1 + CanopyRadius;
        int z0 = key.Z * ChunkGenerator.Size - CanopyRadius;
        int z1 = (key.Z + 1) * ChunkGenerator.Size - 1 + CanopyRadius;
        for (int cz = ChunkGenerator.FloorDiv(z0, CellSize);
             cz <= ChunkGenerator.FloorDiv(z1, CellSize); cz++)
        for (int cx = ChunkGenerator.FloorDiv(x0, CellSize);
             cx <= ChunkGenerator.FloorDiv(x1, CellSize); cx++)
            if (TryTreeInCell(cx, cz, seed, out Tree tree))
                apply(tree);
    }

    /// <summary>Allows the streamer to discover trees crossing Y=16 or Y=32.</summary>
    public static bool HasBlocksInChunk(ChunkKey key, int seed)
    {
        if (key.Y < 0 || key.Y > ChunkGenerator.MaxChunkY)
            return false;
        int y0 = key.Y * ChunkGenerator.Size;
        int y1 = y0 + ChunkGenerator.Size - 1;
        bool found = false;
        ForNearbyTrees(key, seed, tree =>
        {
            if (found || tree.CrownY + 1 < y0 || tree.GroundY > y1)
                return;
            if (tree.X + CanopyRadius < key.X * ChunkGenerator.Size ||
                tree.X - CanopyRadius >= (key.X + 1) * ChunkGenerator.Size ||
                tree.Z + CanopyRadius < key.Z * ChunkGenerator.Size ||
                tree.Z - CanopyRadius >= (key.Z + 1) * ChunkGenerator.Size)
                return;
            found = true;
        });
        return found;
    }

    /// <summary>
    /// Populate trunks and leaf canopies only after the solid terrain has been
    /// generated, and before sparse player edits are applied. Trunk blocks
    /// override leaves at their centers; neither can overwrite natural stone.
    /// </summary>
    public static void DecorateChunk(ChunkKey key, int seed, ushort[] blocks)
    {
        if (key.Y < 0 || key.Y > ChunkGenerator.MaxChunkY)
            return;
        int x0 = key.X * ChunkGenerator.Size;
        int y0 = key.Y * ChunkGenerator.Size;
        int z0 = key.Z * ChunkGenerator.Size;

        void Write(int x, int y, int z, ushort type)
        {
            int lx = x - x0, ly = y - y0, lz = z - z0;
            if ((uint)lx >= ChunkGenerator.Size ||
                (uint)ly >= ChunkGenerator.Size ||
                (uint)lz >= ChunkGenerator.Size)
                return;
            int index = ChunkGenerator.Index(lx, ly, lz);
            if (blocks[index] == 0 || (type == Log && blocks[index] == Leaves))
                blocks[index] = type;
        }

        ForNearbyTrees(key, seed, tree =>
        {
            for (int y = tree.CrownY - (tree.IsPine ? 5 : 2);
                 y <= tree.CrownY + 1; y++)
            for (int z = tree.Z - CanopyRadius; z <= tree.Z + CanopyRadius; z++)
            for (int x = tree.X - CanopyRadius; x <= tree.X + CanopyRadius; x++)
                if (ShapeAt(tree, x, y, z) == Leaves)
                    Write(x, y, z, Leaves);
            for (int y = tree.GroundY; y <= tree.CrownY; y++)
                Write(tree.X, y, tree.Z, Log);
        });
    }
}
