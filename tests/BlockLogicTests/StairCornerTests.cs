using System;
using System.Numerics;
using WorldGeneration;

/// <summary>
/// Pure regression coverage for context-sensitive stair corners and slab
/// stacking. No Tallow physics runtime is needed for these state tests.
/// </summary>
internal static class StairCornerTests
{
    private static int Count(BlockState block)
    {
        int count = 0;
        for (int y = 0; y < 2; y++)
        for (int z = 0; z < 2; z++)
        for (int x = 0; x < 2; x++)
            if (block.ContainsMicro(x, y, z)) count++;
        return count;
    }

    public static void Run(Action<bool, string> check)
    {
        foreach (int material in new[] { 3, 6, 12, 13, 14, 15 })
        {
            BlockPosition hit = new(-1, 4, -16);
            BlockState lower = BlockState.Create(material, BlockShape.Slab);
            BlockState upper = BlockState.Create(material, BlockShape.Slab,
                0, true);
            check(BlockPlacement.TryPlan(hit,
                    new BlockPosition(hit.X, hit.Y + 1, hit.Z),
                    Vector3.UnitY, lower, upper, out var target,
                    out ushort full) &&
                target == hit && BlockPlacement.CanReplace(lower.Packed, full) &&
                new BlockState(full).Shape == BlockShape.Cube &&
                new BlockState(full).Type == material,
                "Top-clicked lower slabs must merge to cubes with the same material");
            check(BlockPlacement.TryPlan(hit,
                    new BlockPosition(hit.X, hit.Y - 1, hit.Z),
                    -Vector3.UnitY, upper, lower, out target, out full) &&
                target == hit && BlockPlacement.CanReplace(upper.Packed, full),
                "Bottom-clicked upper slabs must merge to cubes");
            for (int x = 0; x < 2; x++)
            for (int y = 0; y < 2; y++)
            for (int z = 0; z < 2; z++)
                check(BlockPlacement.AddsMicro(lower.Packed,
                    BlockState.Create(material).Packed, x, y, z) == (y == 1),
                    "Slab merge checks only newly filled 0.5m cells");
        }

        for (int facing = 0; facing < 4; facing++)
        for (int isUpper = 0; isUpper < 2; isUpper++)
        {
            BlockState stair = BlockState.Create(13, BlockShape.Stair, facing,
                isUpper == 1);
            BlockState right = BlockState.Create(6, BlockShape.Stair,
                (facing + 1) & 3, isUpper == 1);
            BlockState left = BlockState.Create(12, BlockShape.Stair,
                (facing + 3) & 3, isUpper == 1);
            BlockState parallel = BlockState.Create(6, BlockShape.Stair,
                facing, isUpper == 1);
            BlockState oppositeLayer = BlockState.Create(6, BlockShape.Stair,
                (facing + 1) & 3, isUpper == 0);
            BlockState unrelated = BlockState.Create(6, BlockShape.Slab);
            check(StairCornerResolver.Choose(stair, right, default) ==
                    StairCorner.InnerLeft &&
                StairCornerResolver.Choose(stair, left, default) ==
                    StairCorner.InnerRight,
                "Lower-side perpendicular stairs make inner corners");
            check(StairCornerResolver.Choose(stair, default, right) ==
                    StairCorner.OuterLeft &&
                StairCornerResolver.Choose(stair, default, left) ==
                    StairCorner.OuterRight,
                "Raised-side perpendicular stairs make outer corners");
            check(StairCornerResolver.Choose(stair, parallel, default) ==
                    StairCorner.Straight &&
                StairCornerResolver.Choose(stair, oppositeLayer, unrelated) ==
                    StairCorner.Straight,
                "Parallel, different-height and non-stair neighbors never bend stairs");
            check(StairCornerResolver.Choose(stair, right, left) ==
                    StairCorner.OuterRight,
                "Raised-side corner takes precedence when both ends turn");
            // A perpendicular neighbor does not make a corner if a
            // same-facing stair continues along the exposed side.
            check(StairCornerResolver.Choose(stair, default, right,
                    parallel, default) == StairCorner.Straight &&
                StairCornerResolver.Choose(stair, default, left,
                    default, parallel) == StairCorner.Straight,
                "Outer corner must not cut out a continuous stair row");
            check(StairCornerResolver.Choose(stair, right, default,
                    default, parallel) == StairCorner.Straight &&
                StairCornerResolver.Choose(stair, left, default,
                    parallel, default) == StairCorner.Straight,
                "Inner corner must not extend into a continuous stair row");
            check(StairCornerResolver.Choose(stair, right, right,
                    parallel, default) == StairCorner.InnerLeft,
                "Suppressed outer corner may still form an inner corner");
            check(StairCornerResolver.Choose(stair, default, right,
                    oppositeLayer, default) == StairCorner.OuterLeft,
                "Opposite-half side stair does not block a valid corner");
            // A turn must be geometrically continuous across the joining
            // voxel face, not just return the correct Corner enum name.
            // The older front/back inversion put a solid step where a
            // quarter step belonged, leaving visible holes in L turns.
            int stepY = isUpper == 1 ? 0 : 1;
            BlockState outer = stair.WithCorner(
                StairCornerResolver.Choose(stair, default, right));
            BlockState inner = stair.WithCorner(
                StairCornerResolver.Choose(stair, right, default));
            var (dx, dz) = StairCornerResolver.Forward(facing);
            for (int side = 0; side < 2; side++)
            {
                int cxBack = dx != 0 ? (dx > 0 ? 0 : 1) : side;
                int czBack = dz != 0 ? (dz > 0 ? 0 : 1) : side;
                int nxFront = dx != 0 ? (dx > 0 ? 1 : 0) : side;
                int nzFront = dz != 0 ? (dz > 0 ? 1 : 0) : side;
                check(outer.ContainsMicro(cxBack, stepY, czBack) ==
                      right.ContainsMicro(nxFront, stepY, nzFront),
                    "Outer raised step matches perpendicular raised-side neighbor");

                int cxFront = dx != 0 ? (dx > 0 ? 1 : 0) : side;
                int czFront = dz != 0 ? (dz > 0 ? 1 : 0) : side;
                int nxBack = dx != 0 ? (dx > 0 ? 0 : 1) : side;
                int nzBack = dz != 0 ? (dz > 0 ? 0 : 1) : side;
                check(inner.ContainsMicro(cxFront, stepY, czFront) ==
                      right.ContainsMicro(nxBack, stepY, nzBack),
                    "Inner raised step matches perpendicular lower-side neighbor");
            }
            check(Count(stair) == 6, "Straight stair occupies six microcells");

            foreach (StairCorner corner in Enum.GetValues<StairCorner>())
            {
                BlockState shaped = stair.WithCorner(corner);
                int expected = corner switch
                {
                    StairCorner.Straight => 6,
                    StairCorner.InnerLeft or StairCorner.InnerRight => 7,
                    _ => 5
                };
                check(BlockState.IsValid(shaped.Packed) &&
                    shaped.Shape == BlockShape.Stair &&
                    shaped.Type == stair.Type &&
                    shaped.Facing == facing &&
                    shaped.Upper == stair.Upper &&
                    shaped.Corner == corner &&
                    Count(shaped) == expected,
                    "Corner occupancy and packed-state validation match each orientation");
            }

            var (fx, fz) = StairCornerResolver.Forward(facing);
            check(Math.Abs(fx) + Math.Abs(fz) == 1,
                "Every stair forward direction moves exactly one voxel");
        }
        check(!BlockState.IsValid((ushort)(BlockState.Create(3).Packed |
            (1 << 13))), "Non-stair blocks cannot store corner metadata");
        check(!BlockState.IsValid((ushort)(BlockState.Create(3,
            BlockShape.Stair).Packed | (7 << 13))),
            "Invalid corner variants must be rejected");

        Console.WriteLine("PASS: slab completion, all rotated upside-down stair corners and state validity");
    }
}
