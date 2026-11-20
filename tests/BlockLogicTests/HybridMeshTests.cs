using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using WorldGeneration;

internal static class HybridMeshTests
{
    // Independently count exposed half-cell faces. The produced mesh must
    // have exactly the same surface area, with no missing/duplicated faces.
    public static void Run(Action<bool, string> check)
    {
        const int seed = 42;
        int cases = 0;
        foreach (BlockShape shape in new[] { BlockShape.Slab, BlockShape.Stair, BlockShape.HalfWall })
        for (int facing = 0; facing < 4; facing++)
        for (int upper = 0; upper < (shape == BlockShape.HalfWall ? 1 : 2); upper++)
        {
            Validate(new ChunkKey(0, 0), new Dictionary<BlockPosition, ushort>
            {
                [new(8, 12, 8)] = BlockState.Create(6, shape, facing, upper == 1).Packed
            }, seed, check);
            cases++;
        }

        // Shape on either side of chunk seams, including negative chunks.
        foreach (int x in new[] { -17, -16, -1, 0, 15, 16 })
        {
            var edits = new Dictionary<BlockPosition, ushort>
            {
                [new(x, 12, -1)] = BlockState.Create(6, BlockShape.Stair, 1).Packed,
                [new(x + 1, 12, -1)] = BlockState.Create(3, BlockShape.Slab, 0, true).Packed
            };
            int chunkX = ChunkGenerator.FloorDiv(x, ChunkGenerator.Size);
            foreach (int dx in new[] { 0, 1 })
            {
                Validate(new ChunkKey(chunkX + dx, -1), edits, seed, check);
                cases++;
            }
        }
        // Auto-corner geometry must match independently sampled occupancy,
        // including changed neighbor edit states across positive/negative
        // chunk seams and upside-down stair variants.
        for (int facing = 0; facing < 4; facing++)
        for (int upper = 0; upper < 2; upper++)
        {
            var (fx, fz) = StairCornerResolver.Forward(facing);
            var center = new BlockPosition(-1, 47, -16);
            var adjacent = new BlockPosition(center.X + fx, center.Y,
                center.Z + fz);
            var casesWithCorner = new Dictionary<BlockPosition, ushort>
            {
                [center] = BlockState.Create(6, BlockShape.Stair, facing,
                    upper == 1).Packed,
                [adjacent] = BlockState.Create(6, BlockShape.Stair,
                    (facing + 1) & 3, upper == 1).Packed
            };
            var keys = new HashSet<ChunkKey>
            {
                SectionedEditSnapshot.SectionOf(center),
                SectionedEditSnapshot.SectionOf(adjacent)
            };
            foreach (ChunkKey chunk in keys)
            {
                Validate(chunk, casesWithCorner, seed, check);
                cases++;
            }
        }
        Console.WriteLine($"PASS: {cases} hybrid mesh surface scenarios");
    }

    private static void Validate(ChunkKey key,
        IReadOnlyDictionary<BlockPosition, ushort> edits, int seed,
        Action<bool, string> check)
    {
        ChunkMesh mesh = ChunkGenerator.GenerateMesh(key, seed, edits, CancellationToken.None);
        double actualArea = 0;
        check(mesh.Vertices.Length == mesh.Normals.Length &&
            mesh.Vertices.Length == mesh.Uvs.Length,
            "Hybrid mesh attribute lengths");
        check(mesh.Triangles.Length % 3 == 0, "Hybrid triangles");
        for (int i = 0; i < mesh.Triangles.Length; i += 3)
        {
            int a = mesh.Triangles[i], b = mesh.Triangles[i + 1], c = mesh.Triangles[i + 2];
            check(a >= 0 && b >= 0 && c >= 0 &&
                a < mesh.Vertices.Length && b < mesh.Vertices.Length &&
                c < mesh.Vertices.Length, "Triangle indices in range");
            Vector3 cross = Vector3.Cross(mesh.Vertices[b] - mesh.Vertices[a],
                mesh.Vertices[c] - mesh.Vertices[a]);
            check(Vector3.Dot(cross, mesh.Normals[a]) > 0,
                "Hybrid face winding matches normal");
            actualArea += cross.Length() * 0.5;
        }

        bool Solid(int mx, int my, int mz)
        {
            // Sample the actual neighboring vertical section rather than
            // assuming every chunk starts at world Y=0. This matters for
            // upper, lower and negative-coordinate stair seam tests.
            int worldX = key.X * ChunkGenerator.Size +
                ChunkGenerator.FloorDiv(mx, 2);
            int worldZ = key.Z * ChunkGenerator.Size +
                ChunkGenerator.FloorDiv(mz, 2);
            int by = key.Y * ChunkGenerator.Size +
                ChunkGenerator.FloorDiv(my, 2);
            if (!ChunkGenerator.InWorld(by)) return false;
            ushort packed = ChunkGenerator.GetBlock(worldX, by, worldZ, seed, edits);
            var state = new BlockState(packed);
            if (state.Shape == BlockShape.Stair)
            {
                var (fx, fz) = StairCornerResolver.Forward(state.Facing);
                BlockState front = new(ChunkGenerator.GetBlock(
                    worldX + fx, by, worldZ + fz, seed, edits));
                BlockState behind = new(ChunkGenerator.GetBlock(
                    worldX - fx, by, worldZ - fz, seed, edits));
                state = state.WithCorner(StairCornerResolver.Choose(
                    state, front, behind));
            }
            return state.ContainsMicro(mx - ChunkGenerator.FloorDiv(mx, 2) * 2,
                my % 2, mz - ChunkGenerator.FloorDiv(mz, 2) * 2);
        }

        int exposed = 0;
        for (int my = 0; my < ChunkGenerator.Size * 2; my++)
        for (int mz = 0; mz < ChunkGenerator.Size * 2; mz++)
        for (int mx = 0; mx < ChunkGenerator.Size * 2; mx++)
        {
            if (!Solid(mx, my, mz)) continue;
            foreach (ChunkFace f in ChunkFace.All)
                if (!Solid(mx + f.DX, my + f.DY, mz + f.DZ))
                    exposed++;
        }

        check(Math.Abs(actualArea - exposed * 0.25) < 0.001,
            $"Hybrid surface area {actualArea:F3} vs expected {exposed * 0.25:F3} ({key})");
    }
}
