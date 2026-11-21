using System;
using System.Numerics;

namespace WorldGeneration;

/// <summary>
/// Minecraft-style miniature cube. Each face samples exactly the same
/// pixel-art tile and orientation as the placed voxel, including grass top,
/// log ends, forge front and the chest's wooden base. This is cosmetic only.
/// </summary>
public sealed class BlockDropMesh
{
    public const float Size = .28f;
    public Vector3[] Positions { get; private init; } = [];
    public Vector3[] Normals { get; private init; } = [];
    public Vector2[] Uvs { get; private init; } = [];
    public int[] Triangles { get; private init; } = [];

    /// <summary>All actual blocks are cubes; coal/iron ore are rendered as
    /// collectible billboards rather than tiny stone cubes.</summary>
    public static bool IsCube(int type) =>
        type is 1 or 2 or 3 or 4 or 5 or 6 or 9 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19;

    public static BlockDropMesh ForType(int type)
    {
        if (!IsCube(type))
            return new BlockDropMesh();

        BlockState block = BlockState.Create(type);
        var positions = new Vector3[24];
        var normals = new Vector3[24];
        var uvs = new Vector2[24];
        var triangles = new int[36];
        for (int faceId = 0; faceId < ChunkFace.All.Length; faceId++)
        {
            ChunkFace face = ChunkFace.All[faceId];
            int start = faceId * 4;
            positions[start] = (face.A - new Vector3(.5f)) * Size;
            positions[start+1] = (face.B - new Vector3(.5f)) * Size;
            positions[start+2] = (face.C - new Vector3(.5f)) * Size;
            positions[start+3] = (face.D - new Vector3(.5f)) * Size;
            for (int i = 0; i < 4; i++)
                normals[start+i] = face.Normal;

            int tile = BlockTextureAtlas.TileFor(block, faceId);
            uvs[start] = type >= BlockInventory.Glass ?
                AdditionalBlockMesher.FaceUV(type, faceId, 0f, 0f) :
                BlockTextureAtlas.FaceUV(tile, faceId, 0f, 0f);
            uvs[start+1] = type >= BlockInventory.Glass ?
                AdditionalBlockMesher.FaceUV(type, faceId, 1f, 0f) :
                BlockTextureAtlas.FaceUV(tile, faceId, 1f, 0f);
            uvs[start+2] = type >= BlockInventory.Glass ?
                AdditionalBlockMesher.FaceUV(type, faceId, 1f, 1f) :
                BlockTextureAtlas.FaceUV(tile, faceId, 1f, 1f);
            uvs[start+3] = type >= BlockInventory.Glass ?
                AdditionalBlockMesher.FaceUV(type, faceId, 0f, 1f) :
                BlockTextureAtlas.FaceUV(tile, faceId, 0f, 1f);

            int index = faceId * 6;
            triangles[index] = start;
            triangles[index+1] = start+1;
            triangles[index+2] = start+2;
            triangles[index+3] = start;
            triangles[index+4] = start+2;
            triangles[index+5] = start+3;
        }
        return new BlockDropMesh
        {
            Positions = positions,
            Normals = normals,
            Uvs = uvs,
            Triangles = triangles
        };
    }
}
