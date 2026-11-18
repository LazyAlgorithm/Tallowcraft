using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace WorldGeneration;

/// <summary>Deterministic terrain and greedy meshing, independent of Tallow's scene thread.</summary>
public static class ChunkGenerator
{
    public const int Size = 16;

    public static int Index(int x, int y, int z) => (y * Size + z) * Size + x;

    public static int Height(int worldX, int worldZ, int seed)
    {
        float height = 8f
            + 3f * MathF.Sin(((long)worldX + seed) * 0.11f)
            + 3f * MathF.Cos(((long)worldZ - seed) * 0.09f);
        return Math.Clamp((int)MathF.Round(height), 1, Size - 1);
    }

    public static byte BaseBlock(int worldX, int y, int worldZ, int seed)
    {
        if (y < 0 || y >= Size)
            return 0;
        return y < Height(worldX, worldZ, seed) ? (byte)1 : (byte)0;
    }

    public static byte GetBlock(int worldX, int y, int worldZ, int seed,
        IReadOnlyDictionary<BlockPosition, byte> edits)
    {
        if (y < 0 || y >= Size)
            return 0;
        return edits.TryGetValue(new BlockPosition(worldX, y, worldZ), out byte value)
            ? value : BaseBlock(worldX, y, worldZ, seed);
    }

    public static byte[] CreateBlocks(ChunkKey key, int seed,
        IReadOnlyDictionary<BlockPosition, byte> edits)
    {
        byte[] blocks = new byte[Size * Size * Size];
        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
        {
            int worldX = key.X * Size + x;
            int worldZ = key.Z * Size + z;
            int height = Height(worldX, worldZ, seed);
            for (int y = 0; y < height; y++)
                blocks[Index(x, y, z)] = 1;
        }

        // The worker reads a copy-on-write snapshot. No other thread changes it.
        foreach (var pair in edits)
        {
            BlockPosition p = pair.Key;
            if (p.Y < 0 || p.Y >= Size ||
                FloorDiv(p.X, Size) != key.X || FloorDiv(p.Z, Size) != key.Z)
                continue;
            blocks[Index(p.X - key.X * Size, p.Y, p.Z - key.Z * Size)] = pair.Value;
        }
        return blocks;
    }

    public static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : (value + 1) / divisor - 1;

    public static ChunkMesh GenerateMesh(ChunkKey key, int seed,
        IReadOnlyDictionary<BlockPosition, byte> edits, CancellationToken token)
    {
        byte[] blocks = CreateBlocks(key, seed, edits);

        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var triangles = new List<int>();
        bool[] mask = new bool[Size * Size];

        bool IsSolid(int x, int y, int z)
        {
            if (y < 0)
                return true; // No underside for the world's bottom layer.
            if (y >= Size)
                return false;
            if ((uint)x < Size && (uint)z < Size)
                return blocks[Index(x, y, z)] != 0;

            // At chunk boundaries consult edits in neighboring chunks too.
            return GetBlock(key.X * Size + x, y, key.Z * Size + z, seed, edits) != 0;
        }

        foreach (ChunkFace face in ChunkFace.All)
        {
            Vector3 stepU = face.B - face.A;
            Vector3 stepV = face.D - face.A;
            Vector3 axis = Vector3.Abs(face.Normal);

            for (int slice = 0; slice < Size; slice++)
            {
                token.ThrowIfCancellationRequested();

                for (int v = 0; v < Size; v++)
                for (int u = 0; u < Size; u++)
                {
                    Vector3 cell = axis * slice + stepU * u + stepV * v;
                    int x = (int)cell.X, y = (int)cell.Y, z = (int)cell.Z;
                    mask[v * Size + u] =
                        IsSolid(x, y, z) &&
                        !IsSolid(x + face.DX, y + face.DY, z + face.DZ);
                }

                for (int v = 0; v < Size; v++)
                for (int u = 0; u < Size;)
                {
                    int index = v * Size + u;
                    if (!mask[index]) { u++; continue; }

                    int width = 1;
                    while (u + width < Size && mask[v * Size + u + width])
                        width++;

                    int height = 1;
                    while (v + height < Size)
                    {
                        bool canExtend = true;
                        for (int k = 0; k < width; k++)
                        {
                            if (!mask[(v + height) * Size + u + k])
                            {
                                canExtend = false;
                                break;
                            }
                        }
                        if (!canExtend) break;
                        height++;
                    }

                    Vector3 cellOrigin = axis * slice + stepU * u + stepV * v;
                    Vector3 a = cellOrigin + face.A;
                    Vector3 b = a + stepU * width;
                    Vector3 c = b + stepV * height;
                    Vector3 d = a + stepV * height;

                    int start = vertices.Count;
                    vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
                    normals.Add(face.Normal); normals.Add(face.Normal);
                    normals.Add(face.Normal); normals.Add(face.Normal);
                    triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
                    triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 3);

                    for (int yy = 0; yy < height; yy++)
                    for (int xx = 0; xx < width; xx++)
                        mask[(v + yy) * Size + u + xx] = false;
                    u += width;
                }
            }
        }

        return new ChunkMesh(
            vertices.ToArray(), triangles.ToArray(), normals.ToArray(), blocks);
    }
}
