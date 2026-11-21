using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace WorldGeneration;

/// <summary>
/// Builds independent opaque masonry and alpha-blended glass meshes from
/// authoritative voxel states, without modifying the approved original atlas.
/// Nearby physics uses the same generated surfaces on child MeshColliders.
/// </summary>
public static class AdditionalBlockMesher
{
    public static bool IsGlass(int type) => type == BlockInventory.Glass;
    public static bool IsAdditional(int type) =>
        type >= BlockInventory.Glass && type <= BlockInventory.MaxType;

    // Companion 2x2 atlas with 32px art and 2px extruded gutters per tile.
    public static Vector2 FaceUV(int type, int face, float u, float v)
    {
        if (face is 0 or 3) v = 1f - v;
        else if (face is 1 or 2) (u, v) = (v, 1f - u);
        if (type == BlockInventory.Glass) return new Vector2(u, v);
        int tile = type - BlockInventory.RedBricks;
        return new Vector2(((tile & 1) * 36f + 2.5f + u * 31f) / 72f,
            ((tile >> 1) * 36f + 2.5f + v * 31f) / 72f);
    }

    public static void Populate(ChunkMesh output, ChunkKey key, int seed,
        ushort[] blocks, IReadOnlyDictionary<BlockPosition, ushort> edits,
        CancellationToken cancellation)
    {
        var opaque = new LayerBuilder();
        var glass = new LayerBuilder();
        for (int y = 0; y < ChunkGenerator.Size; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int z = 0; z < ChunkGenerator.Size; z++)
            for (int x = 0; x < ChunkGenerator.Size; x++)
            {
                BlockState state = new(blocks[ChunkGenerator.Index(x, y, z)]);
                if (!IsAdditional(state.Type)) continue;
                int wx = key.X * ChunkGenerator.Size + x;
                int wy = key.Y * ChunkGenerator.Size + y;
                int wz = key.Z * ChunkGenerator.Size + z;
                for (int id = 0; id < ChunkFace.All.Length; id++)
                {
                    ChunkFace face = ChunkFace.All[id];
                    BlockState adjacent = new(ChunkGenerator.GetBlock(
                        wx + face.DX, wy + face.DY, wz + face.DZ, seed, edits));
                    // Glass needs to show old/extra opaque faces behind it.
                    // For solid masonry, draw only against air, glass and
                    // partial blocks. This avoids z-fighting at solid seams.
                    bool show = IsGlass(state.Type)
                        ? adjacent.IsAir
                        : adjacent.IsAir || IsGlass(adjacent.Type) ||
                          adjacent.Shape != BlockShape.Cube;
                    if (!show) continue;
                    (IsGlass(state.Type) ? glass : opaque).Emit(
                        new Vector3(x, y, z), face, id, state.Type);
                }
            }
        }
        opaque.CopyTo(output.Additional);
        glass.CopyTo(output.Glass);
    }

    private sealed class LayerBuilder
    {
        private readonly List<Vector3> _vertices = new();
        private readonly List<int> _triangles = new();
        private readonly List<Vector3> _normals = new();
        private readonly List<Vector2> _uvs = new();

        public void Emit(Vector3 position, ChunkFace face, int id, int type)
        {
            int start = _vertices.Count;
            _vertices.Add(position + face.A);
            _vertices.Add(position + face.B);
            _vertices.Add(position + face.C);
            _vertices.Add(position + face.D);
            for (int i = 0; i < 4; i++) _normals.Add(face.Normal);
            _uvs.Add(FaceUV(type, id, 0f, 0f));
            _uvs.Add(FaceUV(type, id, 1f, 0f));
            _uvs.Add(FaceUV(type, id, 1f, 1f));
            _uvs.Add(FaceUV(type, id, 0f, 1f));
            _triangles.Add(start);
            _triangles.Add(start + 1);
            _triangles.Add(start + 2);
            _triangles.Add(start);
            _triangles.Add(start + 2);
            _triangles.Add(start + 3);
        }

        public void CopyTo(ChunkMeshLayer layer)
        {
            layer.Vertices = _vertices.ToArray();
            layer.Triangles = _triangles.ToArray();
            layer.Normals = _normals.ToArray();
            layer.Uvs = _uvs.ToArray();
        }
    }
}
