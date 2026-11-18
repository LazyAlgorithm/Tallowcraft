using System;
using System.Collections.Generic;
using System.Threading;
using WorldGeneration;

internal static class VerticalChunkTests
{
    public static void Run(Action<bool, string> check)
    {
        const int seed = 42;
        const int size = ChunkGenerator.Size;
        var edits = new BlockEdits();

        check(ChunkGenerator.FloorDiv(-1, size) == -1, "Negative Y=-1 chunk");
        check(ChunkGenerator.FloorDiv(-16, size) == -1, "Negative Y=-16 chunk");
        check(ChunkGenerator.FloorDiv(-17, size) == -2, "Negative Y=-17 chunk");
        check(ChunkGenerator.FloorDiv(16, size) == 1, "Positive Y=16 chunk");
        check(ChunkGenerator.InWorld(-32) && ChunkGenerator.InWorld(47),
            "Vertical world endpoints");
        check(!ChunkGenerator.InWorld(-33) && !ChunkGenerator.InWorld(48),
            "Reject out-of-bounds vertical positions");

        var below = new ChunkKey(0, -1, 0);
        var above = new ChunkKey(0, 1, 0);
        check(ChunkGenerator.GetBlock(3, -1, 3, seed, edits.Snapshot) == 3,
            "Underground terrain Y=-1 is stone");
        check(ChunkGenerator.Height(3, 3, seed) < 4,
            "Low terrain location exercises shallow surface dirt");
        check(ChunkGenerator.BaseBlock(3, -1, 3, seed) == 3 &&
            ChunkGenerator.BaseBlock(3, -32, 3, seed) == 3,
            "Low hills must not extend their dirt layer into negative Y");
        check(ChunkGenerator.BaseBlock(3, 2, 3, seed) == 0,
            "Above the low-hill surface remains air");
        check(ChunkGenerator.GetBlock(3, 16, 3, seed, edits.Snapshot) == 0,
            "Above-ground Y=16 is air by default");
        ushort[] belowBlocks = ChunkGenerator.CreateBlocks(below, seed, edits.Snapshot);
        ushort[] aboveBlocks = ChunkGenerator.CreateBlocks(above, seed, edits.Snapshot);
        check(belowBlocks[ChunkGenerator.Index(3, 15, 3)] == 3,
            "Y=-1 stored in layer -1 local 15");
        check(aboveBlocks[ChunkGenerator.Index(3, 0, 3)] == 0,
            "Y=16 stored in layer +1 local 0");

        var high = new BlockPosition(-1, 16, -1);
        var low = new BlockPosition(-1, -1, -1);
        var upperBound = new BlockPosition(-1, 47, -1);
        var lowerBound = new BlockPosition(-1, -32, -1);
        var slab = BlockState.Create(6, BlockShape.Slab).Packed;
        check(edits.Set(high, slab, seed), "Place slab above Y=15");
        check(edits.Set(low, 0, seed), "Dig below Y=0");
        check(edits.Set(upperBound, BlockState.Create(3).Packed, seed),
            "Edit upper vertical world limit");
        check(edits.Set(lowerBound, 0, seed), "Edit lower vertical world limit");
        check(!edits.Set(new BlockPosition(0, -33, 0), 0, seed),
            "Reject edits below world limit");
        check(!edits.Set(new BlockPosition(0, 48, 0), 3, seed),
            "Reject edits above world limit");

        ushort[] layerAbove = ChunkGenerator.CreateBlocks(new ChunkKey(-1, 1, -1),
            seed, edits.Snapshot);
        ushort[] layerBelow = ChunkGenerator.CreateBlocks(new ChunkKey(-1, -1, -1),
            seed, edits.Snapshot);
        check(layerAbove[ChunkGenerator.Index(15, 0, 15)] == slab,
            "Y=16 edited slab belongs to upper chunk");
        check(layerBelow[ChunkGenerator.Index(15, 15, 15)] == 0,
            "Y=-1 dug cell belongs to lower chunk");

        string snapshot = edits.Serialize(seed);
        var restored = new BlockEdits();
        check(restored.TryLoad(snapshot, out int loadedSeed, out var changed),
            "Signed Y snapshot loads");
        check(loadedSeed == seed && changed.Count == 4,
            "Signed Y snapshot captures four edits");
        check(restored.Get(high.X, high.Y, high.Z, seed) == slab &&
            restored.Get(low.X, low.Y, low.Z, seed) == 0,
            "Signed Y edit states survive network snapshot");

        // The +Y chunk must hide the underside of a cube against the
        // neighboring chunk's Y=15 cube, even though the cube is not local.
        var upper = new ChunkKey(0, 1, 0);
        var seams = new Dictionary<BlockPosition, ushort>
        {
            [new BlockPosition(8, 16, 8)] = BlockState.Create(3).Packed
        };
        ChunkMesh exposed = ChunkGenerator.GenerateMesh(upper, seed, seams,
            CancellationToken.None);
        seams[new BlockPosition(8, 15, 8)] = BlockState.Create(3).Packed;
        ChunkMesh hidden = ChunkGenerator.GenerateMesh(upper, seed, seams,
            CancellationToken.None);
        check(exposed.Triangles.Length == hidden.Triangles.Length + 6,
            "A cube below the vertical seam hides one quad above");

        seams[new BlockPosition(8, 15, 8)] =
            BlockState.Create(6, BlockShape.Slab, 0, true).Packed;
        ushort[] topBlocks = ChunkGenerator.CreateBlocks(upper, seed, seams);
        check(ChunkGenerator.MeshingDetail(upper, topBlocks, seams) == 2,
            "Neighboring partial shape across vertical seam enables hybrid path");
        ChunkMesh partial = ChunkGenerator.GenerateMesh(upper, seed, seams,
            CancellationToken.None);
        check(partial.Triangles.Length > 0 && partial.Vertices.Length > 0,
            "Hybrid vertical seam generates a valid mesh");

        var spawnEdits = new Dictionary<BlockPosition, ushort>();
        float ground = ChunkGenerator.SpawnHeight(0f, -4f, seed, spawnEdits);
        check(ground >= ChunkGenerator.Height(0, -4, seed) + 1f,
            "Spawn capsule is above ground");
        spawnEdits[new BlockPosition(3, 30, 3)] =
            BlockState.Create(6, BlockShape.Slab, 0, true).Packed;
        float raised = ChunkGenerator.SpawnHeight(3.5f, 3.5f, seed,
            spawnEdits);
        check(raised >= 32f, "Spawn clears elevated partial-block structures");
        Console.WriteLine("PASS: vertical chunk coordinate, seam, edit and spawn tests");
    }
}
