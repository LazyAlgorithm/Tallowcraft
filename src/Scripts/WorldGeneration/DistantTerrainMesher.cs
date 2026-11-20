using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace WorldGeneration;

/// <summary>
/// A single batched, collider-free, blocky mesh for each distant quadtree tile.
/// Surface cells are sampled directly from the seed and edit snapshot, without
/// generating full-resolution voxel chunks. Trees use the exact seeded roots
/// from WorldFlora, simplified by LOD and batched into the SAME atlas mesh.
/// </summary>
public static class DistantTerrainMesher
{
    public const int CellsPerAxis = 16;
    public const float SkirtDepth = 18f;

    public static ChunkMesh Generate(TerrainTileKey key, int seed,
        IReadOnlyDictionary<BlockPosition, ushort> edits,
        CancellationToken token, bool includeTrees = true)
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
            if (position.X >= startX && position.X < startX + size &&
                position.Z >= startZ && position.Z < startZ + size)
                editedColumns.Add(ColumnId(position.X, position.Z));
        }

        // Indexed immutable edit sections avoid iterating the entire world's
        // edit history for each distant tile.
        if (edits is SectionedEditSnapshot sections)
        {
            for (int y = ChunkGenerator.MinChunkY; y <= ChunkGenerator.MaxChunkY; y++)
            for (int z = key.Z; z < key.Z + key.Span; z++)
            for (int x = key.X; x < key.X + key.Span; x++)
                foreach (var pair in sections.InSection(new ChunkKey(x, y, z)))
                    Remember(pair.Key);
        }
        else
            foreach (var pair in edits)
                Remember(pair.Key);

        float Surface(int x, int z)
        {
            int baseHeight = ChunkGenerator.Height(x, z, seed);
            if (editedColumns.Contains(ColumnId(x, z)))
            {
                // Height approximates the highest occupied non-foliage voxel
                // in this sampled column. Full LOD0 voxels remain authoritative.
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

            float top = baseHeight;
            // Retain recognizably raised portions of seeded stone ruins.
            for (int y = baseHeight; y < Math.Min(baseHeight + 5,
                ChunkGenerator.MaxWorldY + 1); y++)
                if (WorldDecorations.StructureAt(x, y, z, seed) != 0)
                    top = y + 1f;
            return top;
        }

        var vertices = new List<Vector3>(2048);
        var normals = new List<Vector3>(2048);
        var uvs = new List<Vector2>(2048);
        var triangles = new List<int>(3072);
        var heights = new float[CellsPerAxis, CellsPerAxis];

        // One horizontal, unsmoothed plateau per sampled cell. Unlike the
        // old heightfield, no triangle connects two different elevations.
        // Width changes by LOD (2m/4m/8m), not full-resolution voxel work.
        for (int z = 0; z < CellsPerAxis; z++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < CellsPerAxis; x++)
                heights[x, z] = Surface(startX + x * step + step / 2,
                    startZ + z * step + step / 2);
        }

        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d,
            Vector3 normal, int tile, int face)
        {
            int start = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            vertices.Add(d);
            for (int i = 0; i < 4; i++)
                normals.Add(normal);
            uvs.Add(BlockTextureAtlas.FaceUV(tile, face, 0f, 0f));
            uvs.Add(BlockTextureAtlas.FaceUV(tile, face, 1f, 0f));
            uvs.Add(BlockTextureAtlas.FaceUV(tile, face, 1f, 1f));
            uvs.Add(BlockTextureAtlas.FaceUV(tile, face, 0f, 1f));
            triangles.Add(start);
            triangles.Add(start + 1);
            triangles.Add(start + 2);
            triangles.Add(start);
            triangles.Add(start + 2);
            triangles.Add(start + 3);
        }

        // Vertices MUST use the same A,B,C,D order as ChunkFace.All.
        // BlockTextureAtlas.FaceUV rotates/flips texels per canonical face;
        // merely matching winding rotates far-cliff artwork 90/180 degrees.
        // The corners are rectangular voxel faces, never diagonal slopes.
        void West(float x, float z0, float z1, float top, float bottom, int tile) =>
            Quad(new(x, bottom, z0), new(x, bottom, z1),
                new(x, top, z1), new(x, top, z0),
                -Vector3.UnitX, tile, 3);

        void East(float x, float z0, float z1, float top, float bottom, int tile) =>
            Quad(new(x, bottom, z0), new(x, top, z0),
                new(x, top, z1), new(x, bottom, z1),
                Vector3.UnitX, tile, 2);

        void South(float z, float x0, float x1, float top, float bottom, int tile) =>
            Quad(new(x0, bottom, z), new(x0, top, z),
                new(x1, top, z), new(x1, bottom, z),
                -Vector3.UnitZ, tile, 1);

        void North(float z, float x0, float x1, float top, float bottom, int tile) =>
            Quad(new(x0, bottom, z), new(x1, bottom, z),
                new(x1, top, z), new(x0, top, z),
                Vector3.UnitZ, tile, 0);

        for (int z = 0; z < CellsPerAxis; z++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < CellsPerAxis; x++)
            {
                float h = heights[x, z];
                float x0 = x * step, x1 = x0 + step;
                float z0 = z * step, z1 = z0 + step;
                int worldX = startX + x * step + step / 2;
                int worldZ = startZ + z * step + step / 2;
                int topY = Math.Clamp((int)MathF.Ceiling(h) - 1,
                    ChunkGenerator.MinWorldY, ChunkGenerator.MaxWorldY);
                ushort block = ChunkGenerator.GetBlock(worldX, topY,
                    worldZ, seed, edits);
                BlockState state = new(block == 0 ? (ushort)3 : block);
                int topTile = BlockTextureAtlas.TileFor(state, 4);
                int sideTile = BlockTextureAtlas.TileFor(state, 0);

                Quad(new(x0, h, z0), new(x0, h, z1),
                    new(x1, h, z1), new(x1, h, z0),
                    Vector3.UnitY, topTile, 4);

                // Only emit walls where the adjacent coarse cell is lower.
                // Boundary skirts cover mixed LOD edges and the LOD0 seam.
                if (x == 0) West(x0, z0, z1, h, h - SkirtDepth, sideTile);
                else if (h > heights[x - 1, z])
                    West(x0, z0, z1, h, heights[x - 1, z], sideTile);

                if (x == CellsPerAxis - 1)
                    East(x1, z0, z1, h, h - SkirtDepth, sideTile);
                else if (h > heights[x + 1, z])
                    East(x1, z0, z1, h, heights[x + 1, z], sideTile);

                if (z == 0) South(z0, x0, x1, h, h - SkirtDepth, sideTile);
                else if (h > heights[x, z - 1])
                    South(z0, x0, x1, h, heights[x, z - 1], sideTile);

                if (z == CellsPerAxis - 1)
                    North(z1, x0, x1, h, h - SkirtDepth, sideTile);
                else if (h > heights[x, z + 1])
                    North(z1, x0, x1, h, heights[x, z + 1], sideTile);
            }
        }

        void Box(float centerX, float centerZ, float y0, float y1,
            float width, int tile, bool leaf)
        {
            float x0 = centerX - width / 2f, x1 = centerX + width / 2f;
            float z0 = centerZ - width / 2f, z1 = centerZ + width / 2f;
            West(x0, z0, z1, y1, y0, tile);
            East(x1, z0, z1, y1, y0, tile);
            South(z0, x0, x1, y1, y0, tile);
            North(z1, x0, x1, y1, y0, tile);
            if (!leaf) return;
            Quad(new(x0, y1, z0), new(x0, y1, z1),
                new(x1, y1, z1), new(x1, y1, z0),
                Vector3.UnitY, tile, 4);
            Quad(new(x0, y0, z0), new(x1, y0, z0),
                new(x1, y0, z1), new(x0, y0, z1),
                -Vector3.UnitY, tile, 5);
        }

        int trees = 0;
        if (includeTrees)
        {
            // Each existing 8m vegetation cell can produce exactly one tree.
            // One MeshRenderer/SetMesh submission renders all terrain and tree
            // geometry for this tile. No foliage GameObjects or colliders.
            const int treeCell = 8;
            int minCellX = ChunkGenerator.FloorDiv(startX, treeCell);
            int minCellZ = ChunkGenerator.FloorDiv(startZ, treeCell);
            int maxCellX = ChunkGenerator.FloorDiv(startX + size - 1, treeCell);
            int maxCellZ = ChunkGenerator.FloorDiv(startZ + size - 1, treeCell);
            for (int cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
            {
                token.ThrowIfCancellationRequested();
                for (int cellX = minCellX; cellX <= maxCellX; cellX++)
                {
                    if (!WorldFlora.TryTreeRootInCell(cellX, cellZ, seed,
                        out int worldX, out int worldZ, out int groundY,
                        out int trunkHeight, out bool pine))
                        continue;
                    if (worldX < startX || worldX >= startX + size ||
                        worldZ < startZ || worldZ >= startZ + size)
                        continue;

                    // An edited-away natural trunk or foundation must not
                    // reappear in distant representation after mining.
                    if (edits.TryGetValue(new BlockPosition(worldX, groundY, worldZ),
                        out ushort trunkEdit) && trunkEdit != WorldFlora.Log)
                        continue;
                    if (edits.TryGetValue(new BlockPosition(worldX, groundY - 1, worldZ),
                        out ushort floorEdit) && floorEdit == 0)
                        continue;

                    float cx = worldX - startX + 0.5f;
                    float cz = worldZ - startZ + 0.5f;
                    float top = groundY + trunkHeight;
                    Box(cx, cz, groundY, top, 1f, 4, false);

                    if (!pine)
                        Box(cx, cz, top - 3f, top + 1f, 5f, 11, true);
                    else if (key.Level == 1)
                    {
                        Box(cx, cz, top - 5f, top - 3f, 4.5f, 11, true);
                        Box(cx, cz, top - 3f, top - 1f, 3f, 11, true);
                        Box(cx, cz, top - 1f, top + 1f, 1.5f, 11, true);
                    }
                    else if (key.Level == 2)
                    {
                        Box(cx, cz, top - 5f, top - 2f, 4.5f, 11, true);
                        Box(cx, cz, top - 2f, top + 1f, 2.5f, 11, true);
                    }
                    else
                        Box(cx, cz, top - 4f, top + 1f, 3.5f, 11, true);
                    trees++;
                }
            }
        }

        var mesh = new ChunkMesh(vertices.ToArray(), triangles.ToArray(),
            normals.ToArray(), uvs.ToArray(), Array.Empty<ushort>());
        mesh.DistantTreeCount = trees;
        return mesh;
    }
}
