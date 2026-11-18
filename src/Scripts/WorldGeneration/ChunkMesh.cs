namespace WorldGeneration;

using System.Numerics;

public sealed class ChunkMesh
{
    public readonly Vector3[] Vertices;
    public readonly int[] Triangles;
    public readonly Vector3[] Normals;
    public readonly byte[] Blocks;

    public ChunkMesh(
        Vector3[] vertices,
        int[] triangles,
        Vector3[] normals,
        byte[] blocks)
    {
        Vertices = vertices;
        Triangles = triangles;
        Normals = normals;
        Blocks = blocks;
    }
}
