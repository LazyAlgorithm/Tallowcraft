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
        CheckCanonicalFaces(before, check);
        int cells = DistantTerrainMesher.CellsPerAxis;
        // Each terrain column is a flat-topped square, and exposed step
        // faces can add geometry without requiring a full voxel mesh.
        check(before.Vertices.Length >= cells * cells * 4 &&
            before.Vertices.Length % 4 == 0 &&
            before.Triangles.Length == before.Vertices.Length / 4 * 6,
            "Coarse geometry should be a compact batched quad mesh");
        check(before.Blocks.Length == 0,
            "LOD meshes must not store authoritative chunk voxel buffers");
        foreach (ChunkMesh mesh in new[] { before, neighbor })
            for (int i = 0; i < mesh.Vertices.Length; i += 4)
                if (mesh.Normals[i] == Vector3.UnitY ||
                    mesh.Normals[i] == -Vector3.UnitY)
                    check(mesh.Vertices[i].Y == mesh.Vertices[i + 1].Y &&
                        mesh.Vertices[i].Y == mesh.Vertices[i + 2].Y &&
                        mesh.Vertices[i].Y == mesh.Vertices[i + 3].Y,
                        "Horizontal distant block faces must be flat, never sloped");
        check(before.Vertices[0].Y == before.Vertices[1].Y &&
            before.Vertices[0].Y == before.Vertices[2].Y,
            "Distant terrain must not interpolate a slope between samples");

        // First coarse cell (-32..-31 in each axis) samples (-31,-31).
        var edited = new BlockPosition(-31, 42, -31);
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

        // One deterministic 8m flora cell should produce only one compact
        // tree proxy, and mining its root should remove that distant tree.
        bool foundTree = false;
        for (int cellZ = 10; cellZ < 28 && !foundTree; cellZ++)
        for (int cellX = 10; cellX < 28 && !foundTree; cellX++)
        {
            if (!WorldFlora.TryTreeRootInCell(cellX, cellZ, seed,
                out int treeX, out int treeZ, out int groundY,
                out int trunkHeight, out bool pine))
                continue;
            check(WorldFlora.TryTreeAt(treeX, treeZ, seed,
                    out int sameGround, out int sameHeight, out bool samePine) &&
                sameGround == groundY && sameHeight == trunkHeight &&
                samePine == pine,
                "Far tree roots must match full-detail WorldFlora generation");

            var treeTile = TerrainTileKey.At(
                ChunkGenerator.FloorDiv(treeX, ChunkGenerator.Size),
                ChunkGenerator.FloorDiv(treeZ, ChunkGenerator.Size), 3);
            var treeEdits = new BlockEdits();
            ChunkMesh trees = DistantTerrainMesher.Generate(treeTile, seed,
                treeEdits.Snapshot, CancellationToken.None);
            ChunkMesh terrainOnly = DistantTerrainMesher.Generate(treeTile, seed,
                treeEdits.Snapshot, CancellationToken.None, includeTrees: false);
            check(trees.DistantTreeCount > 0 &&
                trees.Vertices.Length > terrainOnly.Vertices.Length &&
                terrainOnly.DistantTreeCount == 0,
                "Distant LOD meshes must batch procedural trees with terrain");
            check(treeEdits.Set(new BlockPosition(treeX, groundY, treeZ), 0, seed),
                "Mining a deterministic natural tree trunk must be a valid edit");
            ChunkMesh mined = DistantTerrainMesher.Generate(treeTile, seed,
                treeEdits.Snapshot, CancellationToken.None);
            check(mined.DistantTreeCount == trees.DistantTreeCount - 1,
                "Mining a tree root should invalidate its distant proxy");
            ChunkMesh repeatedTrees = DistantTerrainMesher.Generate(treeTile, seed,
                treeEdits.Snapshot, CancellationToken.None);
            check(mined.Vertices.SequenceEqual(repeatedTrees.Vertices) &&
                mined.Triangles.SequenceEqual(repeatedTrees.Triangles),
                "Seeded distant trees must generate deterministically");
            foundTree = true;
        }
        check(foundTree, "Regression region should contain a seeded tree");

        using var stop = new CancellationTokenSource();
        stop.Cancel();
        bool cancelled = false;
        try
        {
            DistantTerrainMesher.Generate(left, seed, blocks.Snapshot, stop.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "Distant terrain jobs must obey cancellation tokens");
        Console.WriteLine("PASS: signed quadtree coverage, stepped block terrain, tree LOD, edits, and cancellation");
    }

    /// <summary>
    /// Far tiles must use the exact ChunkFace A/B/C/D vertex order: the
    /// atlas's per-face orientation compensation is based on this order.
    /// A winding-only check would miss 90/180-degree rotated stone textures.
    /// </summary>
    private static void CheckCanonicalFaces(ChunkMesh mesh,
        Action<bool, string> check)
    {
        var uvCorners = new[]
        {
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(1, 1), new Vector2(0, 1)
        };
        for (int i = 0; i < mesh.Vertices.Length; i += 4)
        {
            Vector3 min = mesh.Vertices[i], max = mesh.Vertices[i];
            for (int j = 1; j < 4; j++)
            {
                min = Vector3.Min(min, mesh.Vertices[i + j]);
                max = Vector3.Max(max, mesh.Vertices[i + j]);
            }
            Vector3 normal = mesh.Normals[i];
            int face;
            Vector3[] positions;
            if (normal == Vector3.UnitZ)
            {
                face = 0;
                positions = new[] {
                    new Vector3(min.X, min.Y, min.Z),
                    new Vector3(max.X, min.Y, min.Z),
                    new Vector3(max.X, max.Y, min.Z),
                    new Vector3(min.X, max.Y, min.Z)
                };
            }
            else if (normal == -Vector3.UnitZ)
            {
                face = 1;
                positions = new[] {
                    new Vector3(min.X, min.Y, min.Z),
                    new Vector3(min.X, max.Y, min.Z),
                    new Vector3(max.X, max.Y, min.Z),
                    new Vector3(max.X, min.Y, min.Z)
                };
            }
            else if (normal == Vector3.UnitX)
            {
                face = 2;
                positions = new[] {
                    new Vector3(min.X, min.Y, min.Z),
                    new Vector3(min.X, max.Y, min.Z),
                    new Vector3(min.X, max.Y, max.Z),
                    new Vector3(min.X, min.Y, max.Z)
                };
            }
            else if (normal == -Vector3.UnitX)
            {
                face = 3;
                positions = new[] {
                    new Vector3(min.X, min.Y, min.Z),
                    new Vector3(min.X, min.Y, max.Z),
                    new Vector3(min.X, max.Y, max.Z),
                    new Vector3(min.X, max.Y, min.Z)
                };
            }
            else if (normal == Vector3.UnitY)
            {
                face = 4;
                positions = new[] {
                    new Vector3(min.X, min.Y, min.Z),
                    new Vector3(min.X, min.Y, max.Z),
                    new Vector3(max.X, min.Y, max.Z),
                    new Vector3(max.X, min.Y, min.Z)
                };
            }
            else if (normal == -Vector3.UnitY)
            {
                face = 5;
                positions = new[] {
                    new Vector3(min.X, min.Y, min.Z),
                    new Vector3(max.X, min.Y, min.Z),
                    new Vector3(max.X, min.Y, max.Z),
                    new Vector3(min.X, min.Y, max.Z)
                };
            }
            else
                throw new Exception("Non-axis-aligned distant terrain normal");

            Vector2 zero = BlockTextureAtlas.FaceUV(0, face, 0, 0);
            for (int j = 0; j < 4; j++)
            {
                check(mesh.Vertices[i + j] == positions[j],
                    "LOD faces must match full-block canonical vertex order");
                Vector2 expectedDelta = BlockTextureAtlas.FaceUV(
                    0, face, uvCorners[j].X, uvCorners[j].Y) - zero;
                check(Vector2.Distance(mesh.Uvs[i + j] - mesh.Uvs[i],
                    expectedDelta) < 0.0001f,
                    "LOD face UVs must keep the same rotation as nearby voxels");
            }
        }
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
