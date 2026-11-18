using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace WorldGeneration;

/// <summary>Deterministic block terrain; half-cell greedy surfaces for slabs, stairs and walls.</summary>
public static class ChunkGenerator
{
    public const int Size = 16;
    public const int MinChunkY = -2;
    public const int MaxChunkY = 2;
    public const int MinWorldY = MinChunkY * Size;
    public const int MaxWorldY = (MaxChunkY + 1) * Size - 1;
    public static bool InWorld(int y) => y >= MinWorldY && y <= MaxWorldY;
    private const int HalfCellDetail = 2;

    /// <summary>
    /// Use a one-metre grid for ordinary cube terrain (8x fewer cells per
    /// volume), and fall back to half-metre occupancy near partial blocks.
    /// The neighbor halo is important: a slab in an adjacent chunk can
    /// expose half a face of our border cube.
    /// </summary>
    public static int MeshingDetail(ChunkKey key, ushort[] blocks,
        IReadOnlyDictionary<BlockPosition, ushort> edits)
    {
        foreach (ushort packed in blocks)
            if (packed != 0 && new BlockState(packed).Shape != BlockShape.Cube)
                return HalfCellDetail;

        int y0 = key.Y * Size - 1;
        int y1 = (key.Y + 1) * Size;
        int x0 = key.X * Size - 1;
        int z0 = key.Z * Size - 1;
        int x1 = (key.X + 1) * Size;
        int z1 = (key.Z + 1) * Size;
        foreach (var pair in edits)
        {
            BlockPosition p = pair.Key;
            if (p.Y >= y0 && p.Y <= y1 &&
                p.X >= x0 && p.X <= x1 &&
                p.Z >= z0 && p.Z <= z1 &&
                pair.Value != 0 &&
                new BlockState(pair.Value).Shape != BlockShape.Cube)
                return HalfCellDetail;
        }

        return 1;
    }

    public static int Index(int x, int y, int z) => (y * Size + z) * Size + x;

    public static int Height(int worldX, int worldZ, int seed)
    {
        float height = 8f
            + 3f * MathF.Sin(((long)worldX + seed) * 0.11f)
            + 3f * MathF.Cos(((long)worldZ - seed) * 0.09f);
        return Math.Clamp((int)MathF.Round(height), 1, Size - 1);
    }

    public static ushort BaseBlock(int worldX, int y, int worldZ, int seed)
    {
        if (!InWorld(y)) return 0;
        int height = Height(worldX, worldZ, seed);
        return y >= height ? (ushort)0
            : y == height - 1 ? (ushort)1
            : y >= height - 4 ? (ushort)2 : (ushort)3;
    }

    public static ushort GetBlock(int worldX, int y, int worldZ, int seed,
        IReadOnlyDictionary<BlockPosition, ushort> edits)
    {
        if (!InWorld(y)) return 0;
        return edits.TryGetValue(new BlockPosition(worldX, y, worldZ), out ushort value)
            ? value : BaseBlock(worldX, y, worldZ, seed);
    }

    public static ushort[] CreateBlocks(ChunkKey key, int seed,
        IReadOnlyDictionary<BlockPosition, ushort> edits)
    {
        var blocks = new ushort[Size * Size * Size];
        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
            {
                int worldX = key.X * Size + x;
                int worldZ = key.Z * Size + z;
                int height = Height(worldX, worldZ, seed);
                for (int y = 0; y < Size; y++)
                {
                    int worldY = key.Y * Size + y;
                    if (worldY < height)
                        blocks[Index(x, y, z)] = BaseBlock(worldX, worldY, worldZ, seed);
                }
            }

        // Copy-on-write edit dictionaries are immutable for worker lifetimes.
        foreach (var pair in edits)
        {
            BlockPosition p = pair.Key;
            if (FloorDiv(p.Y, Size) != key.Y ||
                FloorDiv(p.X, Size) != key.X || FloorDiv(p.Z, Size) != key.Z)
                continue;
            blocks[Index(p.X - key.X * Size, p.Y - key.Y * Size,
                p.Z - key.Z * Size)] = pair.Value;
        }
        return blocks;
    }

    public static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : (value + 1) / divisor - 1;

    public static ChunkMesh GenerateMesh(ChunkKey key, int seed,
        IReadOnlyDictionary<BlockPosition, ushort> edits, CancellationToken token)
    {
        ushort[] blocks = CreateBlocks(key, seed, edits);
        int detail = MeshingDetail(key, blocks, edits);
        // Mixed chunks use a sparse half-cell path instead of scanning
        // every 0.5m cell in the entire 16m chunk.
        if (detail == HalfCellDetail)
            return HybridChunkMesher.Generate(key, seed, blocks, edits, token);
        int grid = Size * detail;
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();
        var mask = new int[grid * grid];

        ushort StateAt(int x, int y, int z)
        {
            int worldY = key.Y * Size + FloorDiv(y, detail);
            if (!InWorld(worldY)) return 0;
            int blockX = FloorDiv(x, detail);
            int blockZ = FloorDiv(z, detail);
            int blockY = y / detail;
            if ((uint)blockX < Size && (uint)blockY < Size && (uint)blockZ < Size)
                return blocks[Index(blockX, blockY, blockZ)];
            return GetBlock(key.X * Size + blockX, worldY,
                key.Z * Size + blockZ, seed, edits);
        }

        bool Occupied(int x, int y, int z)
        {
            int worldY = key.Y * Size + FloorDiv(y, detail);
            if (!InWorld(worldY)) return false;
            var state = new BlockState(StateAt(x, y, z));
            if (state.IsAir) return false;
            if (detail == 1)
                return true; // All nearby visible shapes are whole cubes.
            int innerX = x - FloorDiv(x, detail) * detail;
            int innerZ = z - FloorDiv(z, detail) * detail;
            return state.ContainsMicro(innerX, y - FloorDiv(y, detail) * detail, innerZ);
        }

        for (int faceId = 0; faceId < ChunkFace.All.Length; faceId++)
        {
            ChunkFace face = ChunkFace.All[faceId];
            Vector3 stepU = face.B - face.A;
            Vector3 stepV = face.D - face.A;
            Vector3 axis = Vector3.Abs(face.Normal);

            for (int slice = 0; slice < grid; slice++)
            {
                token.ThrowIfCancellationRequested();
                for (int v = 0; v < grid; v++)
                    for (int u = 0; u < grid; u++)
                    {
                        Vector3 cell = axis * slice + stepU * u + stepV * v;
                        int x = (int)cell.X, y = (int)cell.Y, z = (int)cell.Z;
                        mask[v * grid + u] =
                            Occupied(x, y, z) &&
                            !Occupied(x + face.DX, y + face.DY, z + face.DZ)
                            ? BlockTextureAtlas.TileFor(new BlockState(StateAt(x, y, z)), faceId) + 1
                            : 0;
                    }

                for (int v = 0; v < grid; v++)
                    for (int u = 0; u < grid;)
                    {
                        int kind = mask[v * grid + u];
                        if (kind == 0) { u++; continue; }

                        // Keep atlas UVs within a single one-metre block. Crossing
                        // atlas islands for merged quads would stretch or bleed tiles.
                        int maxWidth = Math.Min(grid - u, detail - (u % detail));
                        int maxHeight = Math.Min(grid - v, detail - (v % detail));
                        int width = 1;
                        while (width < maxWidth && mask[v * grid + u + width] == kind)
                            width++;

                        int height = 1;
                        while (height < maxHeight)
                        {
                            bool good = true;
                            for (int k = 0; k < width; k++)
                                if (mask[(v + height) * grid + u + k] != kind)
                                { good = false; break; }
                            if (!good) break;
                            height++;
                        }

                        Vector3 cellOrigin = axis * slice + stepU * u + stepV * v;
                        Vector3 a = (cellOrigin + face.A) / detail;
                        Vector3 b = a + stepU * (width / (float)detail);
                        Vector3 c = b + stepV * (height / (float)detail);
                        Vector3 d = a + stepV * (height / (float)detail);
                        int start = vertices.Count;
                        vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
                        for (int i = 0; i < 4; i++) normals.Add(face.Normal);

                        int tile = kind - 1;
                        float u0 = (u % detail) / (float)detail;
                        float u1 = (u % detail + width) / (float)detail;
                        float v0 = (v % detail) / (float)detail;
                        float v1 = (v % detail + height) / (float)detail;
                        uvs.Add(BlockTextureAtlas.FaceUV(tile, faceId, u0, v0));
                        uvs.Add(BlockTextureAtlas.FaceUV(tile, faceId, u1, v0));
                        uvs.Add(BlockTextureAtlas.FaceUV(tile, faceId, u1, v1));
                        uvs.Add(BlockTextureAtlas.FaceUV(tile, faceId, u0, v1));
                        triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
                        triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 3);

                        for (int yy = 0; yy < height; yy++)
                            for (int xx = 0; xx < width; xx++)
                                mask[(v + yy) * grid + u + xx] = 0;
                        u += width;
                    }
            }
        }

        return new ChunkMesh(vertices.ToArray(), triangles.ToArray(),
            normals.ToArray(), uvs.ToArray(), blocks);
    }
}
