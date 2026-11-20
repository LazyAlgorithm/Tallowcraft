using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace WorldGeneration;

/// <summary>
/// Local half-cell surfaces for partial blocks, with ordinary cubes kept on
/// whole-block faces. A stair no longer makes every block in the chunk use
/// an expensive 32-cells-wide occupancy sweep.
/// </summary>
public static class HybridChunkMesher
{
    public static ChunkMesh Generate(ChunkKey key, int seed, ushort[] blocks,
        IReadOnlyDictionary<BlockPosition, ushort> edits, CancellationToken token)
    {
        var vertices = new List<Vector3>(2048);
        var normals = new List<Vector3>(2048);
        var uvs = new List<Vector2>(2048);
        var triangles = new List<int>(3072);

        BlockState RawAt(int x, int y, int z)
        {
            int worldY = key.Y * ChunkGenerator.Size + y;
            if (!ChunkGenerator.InWorld(worldY))
                return default;
            if ((uint)x < ChunkGenerator.Size && (uint)y < ChunkGenerator.Size &&
                (uint)z < ChunkGenerator.Size)
                return new BlockState(blocks[ChunkGenerator.Index(x, y, z)]);
            return new BlockState(ChunkGenerator.GetBlock(
                key.X * ChunkGenerator.Size + x, worldY,
                key.Z * ChunkGenerator.Size + z, seed, edits));
        }

        // Detailed meshing probes the same stair half-cells repeatedly.
        // Memoize derived corners per job, not globally or per frame.
        var cornerCache = new Dictionary<(int X, int Y, int Z), BlockState>();

        BlockState At(int x, int y, int z)
        {
            BlockState raw = RawAt(x, y, z);
            if (raw.Shape != BlockShape.Stair) return raw;
            if (cornerCache.TryGetValue((x, y, z), out BlockState resolved))
                return resolved;
            var (fx, fz) = StairCornerResolver.Forward(raw.Facing);
            var (lx, lz) = StairCornerResolver.Forward((raw.Facing + 3) & 3);
            var (rx, rz) = StairCornerResolver.Forward((raw.Facing + 1) & 3);
            // Guarding each corner against straight runs prevents cut-outs.
            StairCorner corner = StairCornerResolver.Choose(raw,
                RawAt(x + fx, y, z + fz),
                RawAt(x - fx, y, z - fz),
                RawAt(x + lx, y, z + lz),
                RawAt(x + rx, y, z + rz));
            resolved = raw.WithCorner(corner);
            cornerCache.Add((x, y, z), resolved);
            return resolved;
        }

        bool Occupied(int bx, int by, int bz, int mx, int my, int mz)
        {
            // Rebase half-cell coordinates onto the proper neighbor block.
            // In particular, negative X/Z chunks must use floor division.
            while (mx < 0) { bx--; mx += 2; }
            while (my < 0) { by--; my += 2; }
            while (mz < 0) { bz--; mz += 2; }
            while (mx >= 2) { bx++; mx -= 2; }
            while (my >= 2) { by++; my -= 2; }
            while (mz >= 2) { bz++; mz -= 2; }
            return At(bx, by, bz).ContainsMicro(mx, my, mz);
        }

        void Quad(int bx, int by, int bz, int faceId, float planeInset,
            int u, int v, int width, int height, int tile)
        {
            ChunkFace face = ChunkFace.All[faceId];
            Vector3 across = face.B - face.A;
            Vector3 up = face.D - face.A;
            Vector3 a = new Vector3(bx, by, bz) + face.A -
                face.Normal * planeInset + across * (u * 0.5f) + up * (v * 0.5f);
            Vector3 b = a + across * (width * 0.5f);
            Vector3 c = b + up * (height * 0.5f);
            Vector3 d = a + up * (height * 0.5f);
            int start = vertices.Count;
            vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
            for (int k = 0; k < 4; k++) normals.Add(face.Normal);
            uvs.Add(BlockTextureAtlas.FaceUV(tile, faceId, u * 0.5f, v * 0.5f));
            uvs.Add(BlockTextureAtlas.FaceUV(tile, faceId, (u + width) * 0.5f, v * 0.5f));
            uvs.Add(BlockTextureAtlas.FaceUV(tile, faceId, (u + width) * 0.5f,
                (v + height) * 0.5f));
            uvs.Add(BlockTextureAtlas.FaceUV(tile, faceId, u * 0.5f,
                (v + height) * 0.5f));
            triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
            triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 3);
        }

        void EmitMask(int mask, int x, int y, int z, int faceId, int tile,
            float inset)
        {
            // Greedy-merge the 2x2 visible quadrants within this block's
            // atlas island. A full face becomes a single textured quad.
            for (int v = 0; v < 2; v++)
            for (int u = 0; u < 2; u++)
            {
                int bit = 1 << (v * 2 + u);
                if ((mask & bit) == 0) continue;
                int width = 1;
                while (u + width < 2 && (mask & (bit << width)) != 0)
                    width++;
                int height = 1;
                while (v + height < 2)
                {
                    bool fullRow = true;
                    for (int k = 0; k < width; k++)
                        if ((mask & (1 << ((v + height) * 2 + u + k))) == 0)
                        { fullRow = false; break; }
                    if (!fullRow) break;
                    height++;
                }
                Quad(x, y, z, faceId, inset, u, v, width, height, tile);
                for (int dv = 0; dv < height; dv++)
                for (int du = 0; du < width; du++)
                    mask &= ~(1 << ((v + dv) * 2 + u + du));
            }
        }

        for (int y = 0; y < ChunkGenerator.Size; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int z = 0; z < ChunkGenerator.Size; z++)
            for (int x = 0; x < ChunkGenerator.Size; x++)
            {
                BlockState state = At(x, y, z);
                if (state.IsAir) continue;
                bool cube = state.Shape == BlockShape.Cube;

                for (int faceId = 0; faceId < ChunkFace.All.Length; faceId++)
                {
                    ChunkFace face = ChunkFace.All[faceId];
                    BlockState adjacent = At(x + face.DX, y + face.DY, z + face.DZ);
                    // Adjacent cubes cover the entire outside face.
                    // An ordinary cube against air exposes one whole face,
                    // without checking any individual half-cells.
                    if (cube && adjacent.Shape == BlockShape.Cube && !adjacent.IsAir)
                        continue;
                    int tile = BlockTextureAtlas.TileFor(state, faceId);
                    if (cube && adjacent.IsAir)
                    {
                        Quad(x, y, z, faceId, 0f, 0, 0, 2, 2, tile);
                        continue;
                    }

                    // The outside face tests the far (edge) half-cell;
                    // internal half-height/half-width planes test the near
                    // half-cell against the next cell in the face direction.
                    int outerMask = 0;
                    int innerMask = 0;
                    Vector3 stepU = face.B - face.A;
                    Vector3 stepV = face.D - face.A;
                    int positive = (face.DX + face.DY + face.DZ) > 0 ? 1 : 0;
                    for (int v = 0; v < 2; v++)
                    for (int u = 0; u < 2; u++)
                    {
                        int mx = (int)(stepU.X * u + stepV.X * v);
                        int my = (int)(stepU.Y * u + stepV.Y * v);
                        int mz = (int)(stepU.Z * u + stepV.Z * v);
                        int outerX = face.DX == 0 ? mx : positive;
                        int outerY = face.DY == 0 ? my : positive;
                        int outerZ = face.DZ == 0 ? mz : positive;
                        if (Occupied(x, y, z, outerX, outerY, outerZ) &&
                            !Occupied(x, y, z, outerX + face.DX,
                                outerY + face.DY, outerZ + face.DZ))
                            outerMask |= 1 << (v * 2 + u);

                        if (!cube)
                        {
                            int innerX = face.DX == 0 ? mx : 1 - positive;
                            int innerY = face.DY == 0 ? my : 1 - positive;
                            int innerZ = face.DZ == 0 ? mz : 1 - positive;
                            if (Occupied(x, y, z, innerX, innerY, innerZ) &&
                                !Occupied(x, y, z, innerX + face.DX,
                                    innerY + face.DY, innerZ + face.DZ))
                                innerMask |= 1 << (v * 2 + u);
                        }
                    }
                    if (outerMask != 0)
                        EmitMask(outerMask, x, y, z, faceId, tile, 0f);
                    if (innerMask != 0)
                        EmitMask(innerMask, x, y, z, faceId, tile, 0.5f);
                }
            }
        }

        return new ChunkMesh(vertices.ToArray(), triangles.ToArray(),
            normals.ToArray(), uvs.ToArray(), blocks);
    }
}
