using System.Numerics;
using System;

namespace WorldGeneration;

public sealed class ChunkMesh
{
    public readonly Vector3[] Vertices;
    public readonly int[] Triangles;
    public readonly Vector3[] Normals;
    public readonly Vector2[] Uvs;
    public readonly ushort[] Blocks;
    public readonly ChunkMeshLayer Additional = new();
    public readonly ChunkMeshLayer Glass = new();
    // Captured by worker task wrapper; never inferred from GPU upload time.
    public double GenerationMilliseconds { get; set; }
    // Number of deterministic low-detail tree proxies batched in this tile.
    public int DistantTreeCount { get; set; }

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

public sealed class ChunkMeshLayer
{
    public Vector3[] Vertices = Array.Empty<Vector3>();
    public int[] Triangles = Array.Empty<int>();
    public Vector3[] Normals = Array.Empty<Vector3>();
    public Vector2[] Uvs = Array.Empty<Vector2>();
}
