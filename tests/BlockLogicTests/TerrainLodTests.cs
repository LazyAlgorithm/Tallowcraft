using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using WorldGeneration;

internal static class TerrainLodTests
{
    public static void Run(Action<bool, string> check)
    {
        var northwest = TerrainTileKey.At(-1, -1, 3);
        check(northwest == new TerrainTileKey(-8, -8, 3),
            "Negative tile origins use floor division");
        check(TerrainTileKey.At(-8, -8, 3) == northwest &&
            TerrainTileKey.At(-9, -9, 3) == new TerrainTileKey(-16, -16, 3),
            "Tile alignment handles signed seams");
        check(northwest.Contains(-1, -1) && !northwest.Contains(0, 0),
            "Tile coverage is half-open");

        foreach (int radius in new[] { 20, 32, 48, 64 })
        foreach (var center in new[] { new TerrainColumn(0, 0),
                     new TerrainColumn(-17, -31) })
        {
            int side = Math.Max(3, radius * 3 / 4);
            int behind = Math.Max(2, radius / 2);
            var selection = TerrainQuadtree.Select(center.X, center.Z,
                Vector2.UnitX, radius, side, behind, 1f);
            check(selection.Full.Contains(center),
                "Viewer column must be full resolution");
            check(selection.Tiles.Count > 0,
                "Distant viewing requires coarse tiles");
            foreach (TerrainTileKey tile in selection.Tiles)
                check(tile.Level is >= 1 and <= TerrainQuadtree.MaxLevel &&
                    tile == TerrainTileKey.At(tile.X, tile.Z, tile.Level),
                    "Every tile must have a valid aligned LOD key");

            var coverage = new HashSet<TerrainColumn>(selection.Full);
            foreach (TerrainTileKey tile in selection.Tiles)
                for (int z = tile.Z; z < tile.Z + tile.Span; z++)
                for (int x = tile.X; x < tile.X + tile.Span; x++)
                    check(coverage.Add(new TerrainColumn(x, z)),
                        "Quadtree leaves and LOD0 columns must not overlap");
            // All columns within the allowed forward axis must be owned.
            for (int d = 0; d < radius - 2; d++)
                check(selection.Covers(center.X + d, center.Z),
                    "No holes on the forward viewing axis");
            var stable = TerrainQuadtree.Select(center.X, center.Z,
                Vector2.UnitX, radius, side, behind, 1f, selection.Tiles);
            check(stable.Tiles.SetEquals(selection.Tiles) &&
                stable.Full.SetEquals(selection.Full),
                "An unchanged viewer must not oscillate LOD selection");
        }

        var ancestors = TerrainQuadtree.Ancestors(-1, -1).ToArray();
        check(ancestors.Length == 3 &&
            ancestors[0] == new TerrainTileKey(-2, -2, 1) &&
            ancestors[1] == new TerrainTileKey(-4, -4, 2) &&
            ancestors[2] == new TerrainTileKey(-8, -8, 3),
            "Edits invalidate all ancestors even at negative coordinates");

        const int seed = 42;
        var blocks = new BlockEdits();
        var left = new TerrainTileKey(-2, -2, 1);
        var right = new TerrainTileKey(0, -2, 1);
        ChunkMesh before = DistantTerrainMesher.Generate(left, seed,
            blocks.Snapshot, CancellationToken.None);
        ChunkMesh neighbor = DistantTerrainMesher.Generate(right, seed,
            blocks.Snapshot, CancellationToken.None);
        int cells = DistantTerrainMesher.CellsPerAxis;
        check(before.Vertices.Length == (cells * cells + 4 * cells) * 4 &&
            before.Triangles.Length == (cells * cells + 4 * cells) * 6,
            "Coarse meshes use constant geometry independent of terrain volume");
        check(before.Blocks.Length == 0,
            "LOD meshes must not store authoritative chunk voxel buffers");
        for (int z = 0; z < cells; z++)
        {
            int leftQuad = ((z * cells) + cells - 1) * 4;
            int rightQuad = (z * cells) * 4;
            check(before.Vertices[leftQuad + 3].Y ==
                neighbor.Vertices[rightQuad].Y &&
                before.Vertices[leftQuad + 2].Y ==
                neighbor.Vertices[rightQuad + 1].Y,
                "Equal-LOD tile edges must meet without a height crack");
        }

        var edited = new BlockPosition(-32, 42, -32);
        check(blocks.Set(edited, BlockState.Create(3).Packed, seed),
            "Edit above terrain should be a persistent voxel change");
        ChunkMesh after = DistantTerrainMesher.Generate(left, seed,
            blocks.Snapshot, CancellationToken.None);
        check(after.Vertices[0].Y == 43f &&
            after.Vertices[0].Y != before.Vertices[0].Y,
            "Edited terrain height should invalidate generated tile geometry");
        ChunkMesh repeated = DistantTerrainMesher.Generate(left, seed,
            blocks.Snapshot, CancellationToken.None);
        check(after.Vertices.SequenceEqual(repeated.Vertices) &&
            after.Triangles.SequenceEqual(repeated.Triangles),
            "Same world seed and edit snapshot must generate identical LOD mesh");

        using var stop = new CancellationTokenSource();
        stop.Cancel();
        bool cancelled = false;
        try
        {
            DistantTerrainMesher.Generate(left, seed, blocks.Snapshot, stop.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "Distant terrain jobs must obey cancellation tokens");
        Console.WriteLine("PASS: signed quadtree coverage, LOD stability, seams, edits, and cancellation");
    }

    public static void Benchmark()
    {
        Console.WriteLine("lod_distance,full_columns,distant_tiles,selection_ms,mesh_ms,vertices,triangles");
        foreach (int distance in new[] { 20, 32, 48, 64 })
        {
            var clock = Stopwatch.StartNew();
            var selection = TerrainQuadtree.Select(0, 0, Vector2.UnitX,
                distance, distance * 3 / 4, distance / 2, 1f);
            clock.Stop();
            double selectionMs = clock.Elapsed.TotalMilliseconds;
            long vertices = 0, triangles = 0;
            clock.Restart();
            // Compare a fixed workload to keep the CPU meshing sample cheap.
            foreach (var tile in selection.Tiles.OrderByDescending(t => t.Level)
                .ThenBy(t => t.X).ThenBy(t => t.Z).Take(8))
            {
                ChunkMesh mesh = DistantTerrainMesher.Generate(tile, 42,
                    new BlockEdits().Snapshot, CancellationToken.None);
                vertices += mesh.Vertices.Length;
                triangles += mesh.Triangles.Length / 3;
            }
            clock.Stop();
            Console.WriteLine($"{distance},{selection.Full.Count},{selection.Tiles.Count}," +
                $"{selectionMs:F3},{clock.Elapsed.TotalMilliseconds:F3}," +
                $"{vertices},{triangles}");
        }
    }
}
