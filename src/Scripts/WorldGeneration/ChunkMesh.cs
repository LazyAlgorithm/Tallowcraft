using System.Numerics;

namespace WorldGeneration;

public sealed class ChunkMesh
{
    public readonly Vector3[] Vertices;
    public readonly int[] Triangles;
    public readonly Vector3[] Normals;
    public readonly Vector2[] Uvs;
    public readonly ushort[] Blocks;

    public ChunkMesh(Vector3[] vertices, int[] triangles,
        Vector3[] normals, Vector2[] uvs, ushort[] blocks)
    {
        Vertices = vertices;
        Triangles = triangles;
        Normals = normals;
        Uvs = uvs;
        Blocks = blocks;
    }
}
