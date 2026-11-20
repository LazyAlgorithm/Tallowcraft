using System;
using System.Collections.Generic;
using System.Numerics;

namespace WorldGeneration;

/// <summary>Origin is in signed 16m chunk coordinates, aligned to 2^Level.</summary>
public readonly record struct TerrainTileKey(int X, int Z, int Level)
{
    public int Span => 1 << Level;
    public int WorldSize => Span * ChunkGenerator.Size;
    public bool Contains(int x, int z) =>
        x >= X && x < X + Span && z >= Z && z < Z + Span;

    public static TerrainTileKey At(int x, int z, int level)
    {
        int span = 1 << level;
        return new TerrainTileKey(
            ChunkGenerator.FloorDiv(x, span) * span,
            ChunkGenerator.FloorDiv(z, span) * span, level);
    }
}

public readonly record struct TerrainColumn(int X, int Z);

/// <summary>A nonoverlapping X/Z partition; full columns retain vertical 16³ sections.</summary>
public sealed class TerrainSelection
{
    public readonly HashSet<TerrainColumn> Full = new();
    public readonly HashSet<TerrainTileKey> Tiles = new();

    public bool Covers(int x, int z) =>
        Full.Contains(new TerrainColumn(x, z)) ||
        ContainsTile(x, z);

    public bool ContainsTile(int x, int z)
    {
        foreach (TerrainTileKey tile in Tiles)
            if (tile.Contains(x, z))
                return true;
        return false;
    }
}

/// <summary>
/// A forest of persistent-coordinate 8x8-chunk quadtree roots. Selection runs
/// only when the streaming footprint/settings change, never every frame.
/// Nodes select a representation; authoritative voxel chunks remain separate.
/// </summary>
public static class TerrainQuadtree
{
    public const int MaxLevel = 3;
    public const int DefaultFullRadius = 8;
    public const float Hysteresis = 1.5f;

    public static TerrainSelection Select(int cx, int cz, Vector2 direction,
        int forwardRadius, int sideRadius, int behindRadius, float quality,
        IReadOnlySet<TerrainTileKey>? previousTiles = null)
    {
        var result = new TerrainSelection();
        forwardRadius = Math.Clamp(forwardRadius, 1, WorldDisplaySettings.MaxDistance);
        sideRadius = Math.Clamp(sideRadius, 1, forwardRadius);
        behindRadius = Math.Clamp(behindRadius, 0, sideRadius);
        quality = Math.Clamp(quality, 0.65f, 1.6f);
        int fullRadius = Math.Clamp((int)MathF.Round(DefaultFullRadius * quality),
            2, forwardRadius);
        if (direction.LengthSquared() < .0001f)
            direction = Vector2.UnitY;
        else
            direction = Vector2.Normalize(direction);

        bool InView(int x, int z)
        {
            long dx = (long)x - cx, dz = (long)z - cz;
            float distance = MathF.Sqrt((float)(dx * dx + dz * dz));
            if (distance <= fullRadius || distance < .0001f)
                return true;
            float facing = (dx * direction.X + dz * direction.Y) / distance;
            float allowed = facing >= 0f
                ? sideRadius + (forwardRadius - sideRadius) * facing
                : sideRadius + (sideRadius - behindRadius) * facing;
            return distance <= MathF.Max(allowed, fullRadius);
        }

        bool AnyInView(int x, int z, int span)
        {
            for (int dz = 0; dz < span; dz++)
            for (int dx = 0; dx < span; dx++)
                if (InView(x + dx, z + dz))
                    return true;
            return false;
        }

        void Visit(int x, int z, int level)
        {
            int span = 1 << level;
            if (!AnyInView(x, z, span))
                return;
            if (level == 0)
            {
                result.Full.Add(new TerrainColumn(x, z));
                return;
            }

            // Distance to the nearest chunk center inside this node, not to
            // the node origin. This keeps nearby subtrees full-resolution.
            long dx = cx < x ? (long)x - cx :
                cx >= x + span ? (long)cx - (x + span - 1L) : 0L;
            long dz = cz < z ? (long)z - cz :
                cz >= z + span ? (long)cz - (z + span - 1L) : 0L;
            float nearest = MathF.Sqrt((float)(dx * dx + dz * dz));
            float splitAt = level switch
            {
                1 => 8f * quality,
                2 => 16f * quality,
                _ => 32f * quality
            };
            var key = new TerrainTileKey(x, z, level);
            // Once a tile is selected, require greater movement to split it.
            // Once split, require greater movement away before re-merging.
            splitAt += previousTiles?.Contains(key) == true
                ? -Hysteresis : Hysteresis;

            if (nearest < splitAt)
            {
                int half = span / 2;
                Visit(x, z, level - 1);
                Visit(x + half, z, level - 1);
                Visit(x, z + half, level - 1);
                Visit(x + half, z + half, level - 1);
            }
            else
                result.Tiles.Add(key);
        }

        int root = 1 << MaxLevel;
        int startX = ChunkGenerator.FloorDiv(cx - forwardRadius - root, root) * root;
        int endX = cx + forwardRadius + root;
        int startZ = ChunkGenerator.FloorDiv(cz - forwardRadius - root, root) * root;
        int endZ = cz + forwardRadius + root;
        for (int z = startZ; z <= endZ; z += root)
        for (int x = startX; x <= endX; x += root)
            Visit(x, z, MaxLevel);
        return result;
    }

    /// <summary>Dirty both the owning tile and any coarser cached ancestors.</summary>
    public static IEnumerable<TerrainTileKey> Ancestors(int worldX, int worldZ)
    {
        int cx = ChunkGenerator.FloorDiv(worldX, ChunkGenerator.Size);
        int cz = ChunkGenerator.FloorDiv(worldZ, ChunkGenerator.Size);
        for (int level = 1; level <= MaxLevel; level++)
            yield return TerrainTileKey.At(cx, cz, level);
    }
}
