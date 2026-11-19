using System;
using System.Collections.Generic;
using System.Numerics;

namespace WorldGeneration;

/// <summary>
/// Generates a tiny visual-only outer shell for the exact targeted block.
/// It respects the same 2x2x2 occupancy as the voxel mesher, including
/// stairs, slabs, half walls and orientation, without adding any collider.
/// UVs are normalized over each *whole* face so every part of a block has
/// one continuous crack pattern, rather than eight unrelated crack sprites.
/// </summary>
public sealed class BlockDamageMesh
{
    public const float Offset = .006f;

    public Vector3[] Positions { get; private init; } = [];
    public Vector3[] Normals { get; private init; } = [];
    public Vector2[] Uvs { get; private init; } = [];
    public int[] Triangles { get; private init; } = [];
    public int FaceCount => Triangles.Length / 6;

    /// <summary>MiningPercent is replicated in 10% steps by the server.
    /// A negative percentage hides the overlay. MiningActive decides when
    /// to display it, so 0% is the first crack stage and 90% is the tenth.</summary>
    public static int StageForPercent(int percent) =>
        percent < 0 ? -1 : Math.Clamp(percent / 10, 0, 9);

    public static BlockDamageMesh Create(BlockState block)
    {
        if (!BlockState.IsValid(block.Packed) || block.IsAir)
            return new BlockDamageMesh();

        var vertices = new List<Vector3>(128);
        var normals = new List<Vector3>(128);
        var uvs = new List<Vector2>(128);
        var indices = new List<int>(192);

        bool Solid(int x, int y, int z) =>
            (uint)x < 2 && (uint)y < 2 && (uint)z < 2 &&
            block.ContainsMicro(x,y,z);

        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d,
            Vector3 outward, float u0, float v0, float u1, float v1)
        {
            int start = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            vertices.Add(d);
            for (int i = 0; i < 4; i++) normals.Add(outward);
            uvs.Add(new Vector2(u0,v0));
            uvs.Add(new Vector2(u0,v1));
            uvs.Add(new Vector2(u1,v1));
            uvs.Add(new Vector2(u1,v0));
            // Clockwise outward as expected by Tallow's left-handed mesh.
            indices.Add(start);
            indices.Add(start + 1);
            indices.Add(start + 2);
            indices.Add(start);
            indices.Add(start + 2);
            indices.Add(start + 3);
        }

        for (int y = 0; y < 2; y++)
        for (int z = 0; z < 2; z++)
        for (int x = 0; x < 2; x++)
        {
            if (!Solid(x,y,z))
                continue;
            float x0 = -.5f + x * .5f, x1 = x0 + .5f;
            float y0 = -.5f + y * .5f, y1 = y0 + .5f;
            float z0 = -.5f + z * .5f, z1 = z0 + .5f;
            float ux0 = x * .5f, ux1 = (x+1)*.5f;
            float uy0 = y * .5f, uy1 = (y+1)*.5f;
            float uz0 = z * .5f, uz1 = (z+1)*.5f;
            if (!Solid(x+1,y,z))
                Face(new(x1+Offset,y0,z1), new(x1+Offset,y1,z1),
                    new(x1+Offset,y1,z0), new(x1+Offset,y0,z0),
                    Vector3.UnitX, 1-uz1,uy0,1-uz0,uy1);
            if (!Solid(x-1,y,z))
                Face(new(x0-Offset,y0,z0), new(x0-Offset,y1,z0),
                    new(x0-Offset,y1,z1), new(x0-Offset,y0,z1),
                    -Vector3.UnitX, uz0,uy0,uz1,uy1);
            if (!Solid(x,y+1,z))
                Face(new(x0,y1+Offset,z1), new(x0,y1+Offset,z0),
                    new(x1,y1+Offset,z0), new(x1,y1+Offset,z1),
                    Vector3.UnitY, ux0,1-uz1,ux1,1-uz0);
            if (!Solid(x,y-1,z))
                Face(new(x0,y0-Offset,z0), new(x0,y0-Offset,z1),
                    new(x1,y0-Offset,z1), new(x1,y0-Offset,z0),
                    -Vector3.UnitY, ux0,uz0,ux1,uz1);
            if (!Solid(x,y,z+1))
                Face(new(x0,y0,z1+Offset), new(x0,y1,z1+Offset),
                    new(x1,y1,z1+Offset), new(x1,y0,z1+Offset),
                    Vector3.UnitZ, ux0,uy0,ux1,uy1);
            if (!Solid(x,y,z-1))
                Face(new(x1,y0,z0-Offset), new(x1,y1,z0-Offset),
                    new(x0,y1,z0-Offset), new(x0,y0,z0-Offset),
                    -Vector3.UnitZ, 1-ux1,uy0,1-ux0,uy1);
        }

        return new BlockDamageMesh
        {
            Positions = [.. vertices],
            Normals = [.. normals],
            Uvs = [.. uvs],
            Triangles = [.. indices]
        };
    }
}
