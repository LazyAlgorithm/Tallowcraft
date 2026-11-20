using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace WorldGeneration;

/// <summary>
/// Batches 2x2, 4x4 or 8x8 distant chunk footprints into one 16x16
/// heightfield mesh. It samples the procedural world directly; no 16³ voxel
/// arrays or full-resolution chunk meshes are created and discarded.
/// Nearby caves, stairs, structures and mining still use ChunkGenerator.
/// </summary>
public static class DistantTerrainMesher
{
    public const int CellsPerAxis = 16;
    public const float SkirtDepth = 18f;

    public static ChunkMesh Generate(TerrainTileKey key, int seed,
        IReadOnlyDictionary<BlockPosition, ushort> edits, CancellationToken token)
    {
        if (key.Level is < 1 or > TerrainQuadtree.MaxLevel ||
            TerrainTileKey.At(key.X, key.Z, key.Level) != key)
            throw new ArgumentOutOfRangeException(nameof(key));

        int size = key.WorldSize;
        int step = size / CellsPerAxis;
        int startX = key.X * ChunkGenerator.Size;
        int startZ = key.Z * ChunkGenerator.Size;
        var editedColumns = new HashSet<long>();

        static long ColumnId(int x, int z) => ((long)x << 32) | (uint)z;
        void Remember(BlockPosition position)
        {
            if (position.X >= startX && position.X <= startX + size &&
                position.Z >= startZ && position.Z <= startZ + size)
                editedColumns.Add(ColumnId(position.X, position.Z));
        }

        // Sparse indexed sections avoid scanning an entire world edit history
        // per generated tile. The +1 boundary samples the adjacent edge.
        if (edits is SectionedEditSnapshot sections)
        {
            int maxCX = key.X + key.Span;
            int maxCZ = key.Z + key.Span;
            for (int y = ChunkGenerator.MinChunkY; y <= ChunkGenerator.MaxChunkY; y++)
            for (int z = key.Z; z <= maxCZ; z++)
            for (int x = key.X; x <= maxCX; x++)
                foreach (var pair in sections.InSection(new ChunkKey(x, y, z)))
                    Remember(pair.Key);
        }
        else
            foreach (var pair in edits)
                Remember(pair.Key);

        float Surface(int x, int z)
        {
            int baseHeight = ChunkGenerator.Height(x, z, seed);
            float top = baseHeight;
            if (editedColumns.Contains(ColumnId(x, z)))
            {
                // Honor placed and mined solid blocks in the sampled column.
                // Ignore vegetation so tree crowns do not become giant hills.
                for (int y = ChunkGenerator.MaxWorldY;
                    y >= ChunkGenerator.MinWorldY; y--)
                {
                    ushort packed = ChunkGenerator.GetBlock(x, y, z, seed, edits);
                    BlockState state = new(packed);
                    if (state.IsAir || state.Type is 4 or 9)
                        continue;
                    return y + (state.Shape == BlockShape.Slab && !state.Upper
                        ? 0.5f : 1f);
                }
                return ChunkGenerator.MinWorldY;
            }

            // Recognize the larger seeded stone/plank ruins when the sample
            // intersects them. Small trees and bushes stay near-mesh details.
            for (int y = baseHeight; y < Math.Min(baseHeight + 5,
                ChunkGenerator.MaxWorldY + 1); y++)
                if (WorldDecorations.StructureAt(x, y, z, seed) != 0)
                    top = y + 1f;
            return top;
        }

        var vertices = new List<Vector3>(1280);
        var normals = new List<Vector3>(1280);
        var uvs = new List<Vector2>(1280);
        var triangles = new List<int>(1920);
        var heights = new float[CellsPerAxis + 1, CellsPerAxis + 1];

        for (int z = 0; z <= CellsPerAxis; z++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x <= CellsPerAxis; x++)
                heights[x, z] = Surface(startX + x * step, startZ + z * step);
        }

        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d,
            Vector3 normal, int tile, int face)
        {
            int start = vertices.Count;
            vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
            for (int i = 0; i < 4; i++)
                normals.Add(normal);
            uvs.Add(BlockTextureAtlas.FaceUV(tile, face, 0f, 0f));
            uvs.Add(BlockTextureAtlas.FaceUV(tile, face, 1f, 0f));
            uvs.Add(BlockTextureAtlas.FaceUV(tile, face, 1f, 1f));
            uvs.Add(BlockTextureAtlas.FaceUV(tile, face, 0f, 1f));
            triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
            triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 3);
        }

        // Local-space terrain vertices: the GameObject supplies the signed
        // world-space tile origin. Shared border sample coordinates make
        // adjacent tiles agree at seams even at different LOD resolutions.
        for (int z = 0; z < CellsPerAxis; z++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < CellsPerAxis; x++)
            {
                int px = x * step, pz = z * step;
                int worldX = startX + px + step / 2;
                int worldZ = startZ + pz + step / 2;
                int surfaceY = ChunkGenerator.Height(worldX, worldZ, seed) - 1;
                ushort packed = ChunkGenerator.GetBlock(worldX, surfaceY,
                    worldZ, seed, edits);
                int tile = BlockTextureAtlas.TileFor(new BlockState(
                    packed == 0 ? (ushort)3 : packed), 4);
                Quad(new Vector3(px, heights[x, z], pz),
                    new Vector3(px, heights[x, z + 1], pz + step),
                    new Vector3(px + step, heights[x + 1, z + 1], pz + step),
                    new Vector3(px + step, heights[x + 1, z], pz),
                    Vector3.UnitY, tile, 4);
            }
        }

        // Skirts hide T-junction cracks on mixed-LOD borders and cover the
        // temporary heightfield-to-block transition. No colliders are made.
        for (int i = 0; i < CellsPerAxis; i++)
        {
            token.ThrowIfCancellationRequested();
            float a = i * step, b = (i + 1) * step;
            float w0 = heights[0, i], w1 = heights[0, i + 1];
            Quad(new Vector3(0, w0, a), new Vector3(0, w1, b),
                new Vector3(0, w1 - SkirtDepth, b),
                new Vector3(0, w0 - SkirtDepth, a),
                -Vector3.UnitX, 1, 3);
            float e0 = heights[CellsPerAxis, i],
                e1 = heights[CellsPerAxis, i + 1];
            Quad(new Vector3(size, e1, b), new Vector3(size, e0, a),
                new Vector3(size, e0 - SkirtDepth, a),
                new Vector3(size, e1 - SkirtDepth, b),
                Vector3.UnitX, 1, 2);
            float s0 = heights[i, 0], s1 = heights[i + 1, 0];
            Quad(new Vector3(b, s1, 0), new Vector3(a, s0, 0),
                new Vector3(a, s0 - SkirtDepth, 0),
                new Vector3(b, s1 - SkirtDepth, 0),
                -Vector3.UnitZ, 1, 1);
            float n0 = heights[i, CellsPerAxis],
                n1 = heights[i + 1, CellsPerAxis];
            Quad(new Vector3(a, n0, size), new Vector3(b, n1, size),
                new Vector3(b, n1 - SkirtDepth, size),
                new Vector3(a, n0 - SkirtDepth, size),
                Vector3.UnitZ, 1, 0);
        }

        return new ChunkMesh(vertices.ToArray(), triangles.ToArray(),
            normals.ToArray(), uvs.ToArray(), Array.Empty<ushort>());
    }
}
