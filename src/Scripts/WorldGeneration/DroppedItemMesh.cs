using System;
using System.Numerics;

namespace WorldGeneration;

/// <summary>
/// True camera-facing billboard for non-cube resource pickups (coal, iron
/// ore, iron ingots). An actual item sprite, not two intersecting panels.
/// The WorldDrop visual child rotates to face each client's camera locally.
/// </summary>
public sealed class DroppedItemMesh
{
    public const float HalfSize = .17f;
    public Vector3[] Positions { get; private init; } = [];
    public Vector3[] Normals { get; private init; } = [];
    public Vector2[] Uvs { get; private init; } = [];
    public int[] Triangles { get; private init; } = [];

    public static bool IsBillboard(int type) =>
        type is 7 or 8 or BlockInventory.IronIngot;

    public static DroppedItemMesh ForType(int type)
    {
        if (!IsBillboard(type))
            return new DroppedItemMesh();

        int frame = type - 1;
        int col = frame % 5, row = frame / 5;
        // Half-pixel texture atlas inset avoids adjacent icons bleeding.
        float u0 = (col * 32f + .5f) / 160f;
        float u1 = (col * 32f + 31.5f) / 160f;
        float v0 = (row * 32f + .5f) / 64f;
        float v1 = (row * 32f + 31.5f) / 64f;

        return new DroppedItemMesh
        {
            Positions =
            [
                new(-HalfSize, -HalfSize, 0f),
                new(-HalfSize, HalfSize, 0f),
                new(HalfSize, HalfSize, 0f),
                new(HalfSize, -HalfSize, 0f)
            ],
            Normals = [Vector3.UnitZ, Vector3.UnitZ,
                Vector3.UnitZ, Vector3.UnitZ],
            Uvs =
            [
                new(u0,v1), new(u0,v0), new(u1,v0), new(u1,v1)
            ],
            Triangles = [0,1,2,0,2,3]
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
