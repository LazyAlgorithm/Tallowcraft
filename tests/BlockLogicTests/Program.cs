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
            TestMeshing();
            TestChunkCoordinates();
            VerticalChunkTests.Run(Check);
            GeologyTests.Run(Check);
            BiomeTests.Run(Check);
            FloraTests.Run(Check);
            DecorationTests.Run(Check);
            ChunkSchedulingTests.Run(Check);
            InventoryTests.Run(Check);
            InventoryMenuTests.Run(Check);
            SectionEditTests.Run(Check);
            MiningTests.Run(Check);
            ToolTests.Run(Check);
            ProfileTests.Run(Check);
            HybridMeshTests.Run(Check);
            if (Array.Exists(Environment.GetCommandLineArgs(), value => value == "--bench"))
                MeshBenchmarks.Run();
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

    private static void TestChunkCoordinates()
    {
        // Floor division must map the -1/-16/-17 seams to the correct chunks.
        Check(ChunkGenerator.FloorDiv(-1, 16) == -1, "Negative -1 chunk seam");
        Check(ChunkGenerator.FloorDiv(-16, 16) == -1, "Negative -16 chunk seam");
        Check(ChunkGenerator.FloorDiv(-17, 16) == -2, "Negative -17 chunk seam");
        Check(ChunkGenerator.FloorDiv(0, 16) == 0, "Zero chunk boundary");
        Check(ChunkGenerator.FloorDiv(15, 16) == 0, "Positive chunk edge");
        Check(ChunkGenerator.FloorDiv(16, 16) == 1, "Positive next chunk");

        const int seed = 42;
        var edits = new Dictionary<BlockPosition, ushort>();
        // The same edited block must appear only in its owning chunk, even
        // across negative coordinate boundaries.
        var negative = new ChunkKey(-1, -1);
        var neighbor = new ChunkKey(0, -1);
        var target = new BlockPosition(-1, 12, -16);
        ushort state = BlockState.Create(4).Packed;
        edits[target] = state;
        ushort[] left = ChunkGenerator.CreateBlocks(negative, seed, edits);
        ushort[] right = ChunkGenerator.CreateBlocks(neighbor, seed, edits);
        Check(left[ChunkGenerator.Index(15, 12, 0)] == state,
            "Edit at x=-1 must be in chunk x=-1");
        Check(right[ChunkGenerator.Index(0, 12, 0)] != state,
            "Edit at x=-1 must not leak into chunk x=0");

        // Neighboring partial edits require detailed border occlusion in
        // either direction, including negative X/Z coordinates.
        edits.Clear();
        edits[new BlockPosition(-1, 12, -16)] =
            BlockState.Create(6, BlockShape.Slab).Packed;
        right = ChunkGenerator.CreateBlocks(neighbor, seed, edits);
        Check(ChunkGenerator.MeshingDetail(neighbor, right, edits) == 2,
            "Negative-X neighboring slab must trigger detailed edge meshing");
        edits.Clear();
        edits[new BlockPosition(0, 12, -16)] =
            BlockState.Create(6, BlockShape.Stair).Packed;
        left = ChunkGenerator.CreateBlocks(negative, seed, edits);
        Check(ChunkGenerator.MeshingDetail(negative, left, edits) == 2,
            "Positive-X neighboring stair must trigger detailed edge meshing");
    }

    private static void TestMeshing()
    {
        const int seed = 42;
        ChunkKey home = new(0, 0);
        var edits = new Dictionary<BlockPosition, ushort>();
        ushort[] blocks = ChunkGenerator.CreateBlocks(home, seed, edits);
        Check(ChunkGenerator.MeshingDetail(home, blocks, edits) == 1,
            "Unedited cube terrain should use full-block fast path");
        ChunkMesh whole = ChunkGenerator.GenerateMesh(home, seed, edits,
            System.Threading.CancellationToken.None);
        Check(whole.Vertices.Length > 0 && whole.Vertices.Length == whole.Uvs.Length,
            "Cube terrain mesh must contain UVs");
        Check(whole.Triangles.Length > 0 && whole.Triangles.Length % 3 == 0,
            "Cube terrain needs triangle indices");

        edits[new BlockPosition(7, 12, 7)] = BlockState.Create(6, BlockShape.Stair).Packed;
        blocks = ChunkGenerator.CreateBlocks(home, seed, edits);
        Check(ChunkGenerator.MeshingDetail(home, blocks, edits) == 2,
            "Partial shape in the chunk must use half-block meshing");
        ChunkMesh detailed = ChunkGenerator.GenerateMesh(home, seed, edits,
            System.Threading.CancellationToken.None);
        Check(detailed.Vertices.Length > 0 && detailed.Triangles.Length % 3 == 0,
            "Partial terrain mesh must be valid");

        edits.Clear();
        edits[new BlockPosition(16, 9, 7)] =
            BlockState.Create(6, BlockShape.Slab).Packed;
        blocks = ChunkGenerator.CreateBlocks(home, seed, edits);
        Check(ChunkGenerator.MeshingDetail(home, blocks, edits) == 2,
            "Adjacent chunk's partial block requires detailed edge occlusion");

        edits.Clear();
        edits[new BlockPosition(33, 9, 7)] =
            BlockState.Create(6, BlockShape.Slab).Packed;
        Check(ChunkGenerator.MeshingDetail(home, blocks, edits) == 1,
            "Far-away partial blocks must not slow nearby cube chunks");
    }
}
