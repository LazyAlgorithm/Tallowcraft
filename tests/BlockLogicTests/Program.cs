using System;
using System.Numerics;
using WorldGeneration;

static class Program
{
    private static int _assertions;

    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception(message);
    }

    public static int Main()
    {
        try
        {
            TestShapes();
            TestStacking();
            TestReplacements();
            TestFacing();
            Console.WriteLine($"PASS: {_assertions} block-state and placement assertions");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    private static int Count(BlockState state)
    {
        int n = 0;
        for (int y = 0; y < 2; y++)
        for (int z = 0; z < 2; z++)
        for (int x = 0; x < 2; x++)
            if (state.ContainsMicro(x, y, z)) n++;
        return n;
    }

    private static void TestShapes()
    {
        for (int type = 1; type <= 6; type++)
        for (int facing = 0; facing < 4; facing++)
        {
            var cube = BlockState.Create(type, BlockShape.Cube, facing);
            var lower = BlockState.Create(type, BlockShape.Slab, facing);
            var upper = BlockState.Create(type, BlockShape.Slab, facing, true);
            var stair = BlockState.Create(type, BlockShape.Stair, facing);
            var upsideDown = BlockState.Create(type, BlockShape.Stair, facing, true);
            var wall = BlockState.Create(type, BlockShape.HalfWall, facing);
            Check(Count(cube) == 8, "Cube must fill eight half-cells");
            Check(Count(lower) == 4 && Count(upper) == 4, "Each slab occupies one half");
            Check(Count(stair) == 6 && Count(upsideDown) == 6, "Stair is six subcells");
            Check(Count(wall) == 4, "Half wall is four subcells");
            Check(BlockState.IsValid(stair.Packed) && BlockState.IsValid(upper.Packed),
                "Valid packed state rejected");
            for (int y = 0; y < 2; y++)
            for (int z = 0; z < 2; z++)
            for (int x = 0; x < 2; x++)
            {
                bool a = lower.ContainsMicro(x, y, z);
                bool b = upper.ContainsMicro(x, y, z);
                Check(a != b, "Slab halves must not overlap and must cover a cube");
            }
        }
        Check(!BlockState.IsValid(0xE001), "Reserved bits must be rejected");
        Check(Count(default) == 0, "Air should be empty");
    }

    private static void TestStacking()
    {
        BlockPosition hit = new(0, 4, 0), adjacent = new(0, 5, 0);
        for (int type = 1; type <= 6; type++)
        {
            BlockState lower = BlockState.Create(type, BlockShape.Slab);
            BlockState upper = BlockState.Create(type, BlockShape.Slab, 0, true);
            Check(BlockPlacement.TryPlan(hit, adjacent, Vector3.UnitY,
                lower, lower, out var target, out var result) &&
                target == hit && new BlockState(result).Shape == BlockShape.Cube &&
                new BlockState(result).Type == type,
                "Lower slab + top click should complete cube");
            Check(BlockPlacement.TryPlan(hit, new(0, 3, 0), -Vector3.UnitY,
                upper, lower, out target, out result) &&
                target == hit && new BlockState(result).Shape == BlockShape.Cube,
                "Upper slab + bottom click should complete cube");
            Check(BlockPlacement.TryPlan(hit, adjacent, Vector3.UnitX,
                lower, lower, out target, out result) &&
                target == adjacent && result == lower.Packed,
                "Side click must use adjacent voxel instead of merging");
            var other = BlockState.Create(type == 6 ? 1 : type + 1, BlockShape.Slab);
            Check(BlockPlacement.TryPlan(hit, adjacent, Vector3.UnitY,
                lower, other, out target, out result) &&
                target == adjacent && result == other.Packed,
                "Different materials must not merge");
        }
    }

    private static void TestReplacements()
    {
        BlockState slab = BlockState.Create(3, BlockShape.Slab);
        BlockState cube = BlockState.Create(3);
        BlockState foreign = BlockState.Create(2);
        Check(BlockPlacement.CanReplace(0, slab.Packed), "Air may be occupied");
        Check(BlockPlacement.CanReplace(slab.Packed, cube.Packed), "Same-material slab merge allowed");
        Check(!BlockPlacement.CanReplace(cube.Packed, slab.Packed), "Cube cannot be replaced by slab");
        Check(!BlockPlacement.CanReplace(slab.Packed, foreign.Packed), "Different material merge denied");
        Check(!BlockPlacement.CanReplace(0, 0), "No-op air replacement denied");
        Check(!BlockPlacement.CanReplace(0, 0x8001), "Reserved bits denied");
        int newCells = 0;
        for (int y = 0; y < 2; y++)
        for (int z = 0; z < 2; z++)
        for (int x = 0; x < 2; x++)
        {
            if (BlockPlacement.AddsMicro(slab.Packed, cube.Packed, x, y, z))
            {
                Check(y == 1, "Merging lower slab should only add upper cells");
                newCells++;
            }
        }
        Check(newCells == 4, "Slab merge adds four upper subcells");
    }

    private static void TestFacing()
    {
        Check(BlockState.FacingTowardPlayer(Vector3.UnitZ) == 2, "Front-facing rotation");
        Check(BlockState.FacingTowardPlayer(-Vector3.UnitZ) == 0, "Back-facing rotation");
        Check(BlockState.FacingTowardPlayer(Vector3.UnitX) == 3, "Right-facing rotation");
        Check(BlockState.FacingTowardPlayer(-Vector3.UnitX) == 1, "Left-facing rotation");
    }
}
