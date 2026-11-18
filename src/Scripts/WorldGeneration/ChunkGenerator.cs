namespace WorldGeneration;

public static class ChunkGenerator
{
    public const int Size = 16;

    public static int Index(int x, int y, int z)
    {
        return (y * Size + z) * Size + x;
    }

    // Deterministic terrain generation.
    public static int Height(int worldX, int worldZ, int seed)
    {
        float height = 8f
            + 3f * MathF.Sin((worldX + seed) * 0.11f)
            + 3f * MathF.Cos((worldZ - seed) * 0.09f);

        return Math.Clamp(
            (int)MathF.Round(height),
            1,
            Size - 1
        );
    }

    public static byte[] CreateBlocks(ChunkKey key, int seed)
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

        return blocks;
    }

    // Greedy meshing: merge adjacent visible faces
    // into larger rectangular quads.
    public static ChunkMesh GenerateMesh(
        ChunkKey key,
        int seed,
        CancellationToken token)
    {
        byte[] blocks = CreateBlocks(key, seed);

        List<Vector3> vertices = new();
        List<Vector3> normals = new();
        List<int> triangles = new();

        bool[] mask = new bool[Size * Size];

        bool IsSolid(int x, int y, int z)
        {
            // Hide the underside of the world.
            if (y < 0)
                return true;

            if (y >= Size)
                return false;

            // Inside this chunk.
            if ((uint)x < Size && (uint)z < Size)
                return blocks[Index(x, y, z)] != 0;

            // Across a chunk boundary, sample the
            // same deterministic terrain generator.
            int worldX = key.X * Size + x;
            int worldZ = key.Z * Size + z;

            return y < Height(worldX, worldZ, seed);
        }

        foreach (ChunkFace face in ChunkFace.All)
        {
            // B - A and D - A are the two positive
            // grid directions along the face.
            Vector3 stepU = face.B - face.A;
            Vector3 stepV = face.D - face.A;

            // Absolute normal selects the slice axis.
            Vector3 axis = Vector3.Abs(face.Normal);

            for (int slice = 0; slice < Size; slice++)
            {
                token.ThrowIfCancellationRequested();

                // STEP A: Find visible faces on this slice.
                for (int v = 0; v < Size; v++)
                for (int u = 0; u < Size; u++)
                {
                    Vector3 cell =
                        axis * slice +
                        stepU * u +
                        stepV * v;

                    int x = (int)cell.X;
                    int y = (int)cell.Y;
                    int z = (int)cell.Z;

                    bool visible =
                        IsSolid(x, y, z) &&
                        !IsSolid(
                            x + face.DX,
                            y + face.DY,
                            z + face.DZ
                        );

                    mask[v * Size + u] = visible;
                }

                // STEP B: Merge adjacent visible faces.
                for (int v = 0; v < Size; v++)
                {
                    for (int u = 0; u < Size;)
                    {
                        int index = v * Size + u;

                        if (!mask[index])
                        {
                            u++;
                            continue;
                        }

                        // Grow the rectangle horizontally.
                        int width = 1;

                        while (
                            u + width < Size &&
                            mask[v * Size + u + width])
                        {
                            width++;
                        }

                        // Grow it vertically while every
                        // cell across its width is visible.
                        int height = 1;

                        while (v + height < Size)
                        {
                            bool canExtend = true;

                            for (int k = 0; k < width; k++)
                            {
                                if (!mask[
                                    (v + height) * Size + u + k])
                                {
                                    canExtend = false;
                                    break;
                                }
                            }

                            if (!canExtend)
                                break;

                            height++;
                        }

                        // STEP C: Emit one quad for the
                        // entire rectangle.
                        Vector3 cellOrigin =
                            axis * slice +
                            stepU * u +
                            stepV * v;

                        Vector3 a = cellOrigin + face.A;
                        Vector3 b = a + stepU * width;
                        Vector3 c = b + stepV * height;
                        Vector3 d = a + stepV * height;

                        int start = vertices.Count;

                        vertices.Add(a);
                        vertices.Add(b);
                        vertices.Add(c);
                        vertices.Add(d);

                        // Explicit outward-facing normals.
                        normals.Add(face.Normal);
                        normals.Add(face.Normal);
                        normals.Add(face.Normal);
                        normals.Add(face.Normal);

                        triangles.Add(start);
                        triangles.Add(start + 1);
                        triangles.Add(start + 2);

                        triangles.Add(start);
                        triangles.Add(start + 2);
                        triangles.Add(start + 3);

                        // Mark merged faces as consumed.
                        for (int yy = 0; yy < height; yy++)
                        for (int xx = 0; xx < width; xx++)
                        {
                            mask[(v + yy) * Size + u + xx] = false;
                        }

                        u += width;
                    }
                }
            }
        }

        return new ChunkMesh(
            vertices.ToArray(),
            triangles.ToArray(),
            normals.ToArray(),
            blocks
        );
    }
}
