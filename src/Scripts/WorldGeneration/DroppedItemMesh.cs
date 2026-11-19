using System;
using System.Numerics;

namespace WorldGeneration;

/// <summary>
/// Small crossed sprites drawn from the 5x2 item icon atlas. Each tile is
/// 32px, with a tiny inset so nearest filtering never picks adjacent tiles.
/// This is visual-only: pickup authority is handled by server WorldDrop.
/// </summary>
public sealed class DroppedItemMesh
{
    public Vector3[] Positions { get; private init; } = [];
    public Vector3[] Normals { get; private init; } = [];
    public Vector2[] Uvs { get; private init; } = [];
    public int[] Triangles { get; private init; } = [];

    public static DroppedItemMesh ForType(int type)
    {
        if (type is < 1 or > BlockInventory.MaxType)
            return new DroppedItemMesh();
        int frame = type - 1, column = frame % 5, row = frame / 5;
        float u0 = (column + .035f) / 5f;
        float u1 = (column + .965f) / 5f;
        float v0 = (row + .035f) / 2f;
        float v1 = (row + .965f) / 2f;
        const float size = .20f;
        var positions = new Vector3[8];
        var normals = new Vector3[8];
        var uvs = new Vector2[8];
        var triangles = new int[12];

        for (int plane = 0; plane < 2; plane++)
        {
            int i = plane * 4;
            // Two intersecting vertical planes, rotated 45 degrees so a
            // player sees a recognizable item from any horizontal angle.
            float x = plane == 0 ? size : -size;
            float z = plane == 0 ? size : size;
            positions[i] = new(-x, -size, -z);
            positions[i+1] = new(-x, size, -z);
            positions[i+2] = new(x, size, z);
            positions[i+3] = new(x, -size, z);
            for (int k = 0; k < 4; k++) normals[i+k] = Vector3.UnitZ;
            uvs[i] = new(u0, v1);
            uvs[i+1] = new(u0, v0);
            uvs[i+2] = new(u1, v0);
            uvs[i+3] = new(u1, v1);
            int o = plane*6;
            triangles[o] = i; triangles[o+1] = i+1; triangles[o+2] = i+2;
            triangles[o+3] = i; triangles[o+4] = i+2; triangles[o+5] = i+3;
        }
        return new DroppedItemMesh
        {
            Positions = positions, Normals = normals, Uvs = uvs,
            Triangles = triangles
        };
    }

    public static bool InPickupRange(Vector3 drop, Vector3 player,
        float radius = 1.6f)
    {
        return radius > 0f &&
            float.IsFinite(drop.X) && float.IsFinite(drop.Y) &&
            float.IsFinite(drop.Z) && float.IsFinite(player.X) &&
            float.IsFinite(player.Y) && float.IsFinite(player.Z) &&
            Vector3.DistanceSquared(drop, player) <= radius*radius;
    }
}
