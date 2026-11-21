using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
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

    // Read the exposed tread from the triangles that also feed the chunk
    // collider. An outer turn has a quarter-block tread, an inner turn has
    // three quarters, and a straight stair has half, regardless of facing.
    private static float TreadArea(BlockPosition center, bool upper,
        Dictionary<BlockPosition, ushort> edits)
    {
        ChunkKey section = SectionedEditSnapshot.SectionOf(center);
        ChunkMesh mesh = ChunkGenerator.GenerateMesh(section, 42, edits, CancellationToken.None);
        int x = center.X - section.X * ChunkGenerator.Size;
        int z = center.Z - section.Z * ChunkGenerator.Size;
        int plane = center.Y - section.Y * ChunkGenerator.Size + (upper ? 0 : 1);
        Vector3 normal = upper ? -Vector3.UnitY : Vector3.UnitY;
        float area = 0;
        for (int i = 0; i < mesh.Triangles.Length; i += 3)
        {
            int first = mesh.Triangles[i];
            Vector3 a = mesh.Vertices[first];
            if (mesh.Normals[first] != normal || a.Y != plane) continue;
            Vector3 b = mesh.Vertices[mesh.Triangles[i + 1]];
            Vector3 c = mesh.Vertices[mesh.Triangles[i + 2]];
            Vector3 midpoint = (a + b + c) / 3f;
            if (midpoint.X > x && midpoint.X < x + 1 && midpoint.Z > z && midpoint.Z < z + 1)
                area += Vector3.Cross(b - a, c - a).Length() * .5f;
        }
        return area;
    }

    private static void TestConnectedRuns(Action<bool, string> check)
    {
        // Two chunk borders intersect here. Both arms can extend through
        // the border, depending on the rotation and handedness of the turn.
        BlockPosition center = new(-16, 46, 15);
        for (int facing = 0; facing < 4; facing++)
        foreach (bool upper in new[] { false, true })
        foreach (bool outer in new[] { false, true })
        foreach (int turn in new[] { -1, 1 })
        {
            var (fx, fz) = StairCornerResolver.Forward(facing);
            int turnFacing = (facing + turn) & 3;
            var (tx, tz) = StairCornerResolver.Forward(turnFacing);
            int sign = outer ? -1 : 1;
            BlockPosition bend = new(center.X + sign * fx, center.Y, center.Z + sign * fz);
            BlockPosition continuation = new(center.X + sign * tx, center.Y, center.Z + sign * tz);
            ushort straight = BlockState.Create(3, BlockShape.Stair, facing, upper).Packed;
            ushort perpendicular = BlockState.Create(6, BlockShape.Stair, turnFacing, upper).Packed;
            var edits = new Dictionary<BlockPosition, ushort> { [center] = straight, [bend] = perpendicular };
            float cornerArea = outer ? .25f : .75f;
            check(TreadArea(center, upper, edits) == cornerArea, "First arm forms a corner");
            edits[continuation] = straight;
            check(TreadArea(center, upper, edits) == cornerArea, "Connecting the second arm must preserve the corner tread");
            edits.Remove(bend);
            check(TreadArea(center, upper, edits) == .5f, "Removing the turn leaves the straight tread");
            edits[bend] = perpendicular;
            check(TreadArea(center, upper, edits) == cornerArea, "A turn added after its continuation must form the same corner");
        }
    }

    private static void TestPlacementHalf(Action<bool, string> check)
    {
        var hit = new BlockPosition(-16, -4, 15);
        var side = new BlockPosition(-17, -4, 15);
        foreach (var shape in new[] { BlockShape.Slab, BlockShape.Stair })
        {
            var selected = BlockState.Create(3, shape, facing: 3);
            foreach (var (normal, height, upper) in new[]
            {
                (-Vector3.UnitX, .25f, false),
                (-Vector3.UnitX, .5f, false),
                (-Vector3.UnitX, .75f, true),
                (Vector3.UnitY, 1f, false),
                (-Vector3.UnitY, 0f, true)
            })
            foreach (bool flip in new[] { false, true })
            {
                var manual = BlockState.Create(3, shape, selected.Facing, flip);
                var adjacent = normal.Y == 0 ? side : hit with { Y = hit.Y + (int)normal.Y };
                check(BlockPlacement.TryPlan(hit, adjacent, normal, BlockState.Create(6), manual,
                        out var target, out ushort packed, hit.Y + height) &&
                    target == adjacent &&
                    packed == BlockState.Create(3, shape, 3, upper != flip).Packed,
                    "Aim selects the correct half and preserves facing on side, top and bottom faces");
            }

            var copied = BlockState.Create(3, shape, facing: 1, upper: true);
            check(BlockPlacement.TryPlan(hit, side, -Vector3.UnitX, BlockState.Create(6), copied,
                    out _, out ushort copy) && copy == copied.Packed,
                "Copied stairs and slabs retain the selected half and facing");
        }

        var beam = BlockState.Create(3, BlockShape.Beam, facing: 2);
        check(BlockPlacement.TryPlan(hit, side, -Vector3.UnitX, BlockState.Create(6), beam,
                out _, out ushort trim, hit.Y + .75f) && trim == beam.Packed,
            "Beam height remains manually selected");
    }

    public static void Run(Action<bool, string> check)
    {
        TestConnectedRuns(check);
        TestPlacementHalf(check);
        foreach (int material in new[] { 3, 6, 12, 13, 14, 15 })
        {
            BlockPosition hit = new(-1, 4, -16);
            BlockState lower = BlockState.Create(material, BlockShape.Slab);
            BlockState upper = BlockState.Create(material, BlockShape.Slab,
                0, true);
            check(BlockPlacement.TryPlan(hit,
                    new BlockPosition(hit.X, hit.Y + 1, hit.Z),
                    Vector3.UnitY, lower, upper, out var target,
                    out ushort full, hit.Y + .5f) &&
                target == hit && BlockPlacement.CanReplace(lower.Packed, full) &&
                new BlockState(full).Shape == BlockShape.Cube &&
                new BlockState(full).Type == material,
                "Top-clicked lower slabs must merge to cubes with the same material");
            check(BlockPlacement.TryPlan(hit,
                    new BlockPosition(hit.X, hit.Y - 1, hit.Z),
                    -Vector3.UnitY, upper, lower, out target, out full, hit.Y + .5f) &&
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
            // Protect only the side whose face the corner would change.
            check(StairCornerResolver.Choose(stair, default, right,
                    default, parallel) == StairCorner.Straight &&
                StairCornerResolver.Choose(stair, default, left,
                    parallel, default) == StairCorner.Straight,
                "Outer corner must not cut out a continuous stair row");
            check(StairCornerResolver.Choose(stair, right, default,
                    parallel, default) == StairCorner.Straight &&
                StairCornerResolver.Choose(stair, left, default,
                    default, parallel) == StairCorner.Straight,
                "Inner corner must not extend into a continuous stair row");
            check(StairCornerResolver.Choose(stair, right, right,
                    default, parallel) == StairCorner.InnerLeft,
                "Suppressed outer corner may still form an inner corner");
            var otherHalfParallel = BlockState.Create(6, BlockShape.Stair, facing, isUpper == 0);
            check(StairCornerResolver.Choose(stair, default, right,
                    default, otherHalfParallel) == StairCorner.OuterLeft,
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
