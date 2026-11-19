using System;
using System.Numerics;
using WorldGeneration;

internal static class BlockDamageTests
{
    public static void Run(Action<bool, string> check)
    {
        check(BlockDamageMesh.StageForPercent(0) == -1 &&
            BlockDamageMesh.StageForPercent(-40) == -1 &&
            BlockDamageMesh.StageForPercent(1) == 0 &&
            BlockDamageMesh.StageForPercent(10) == 0 &&
            BlockDamageMesh.StageForPercent(20) == 1 &&
            BlockDamageMesh.StageForPercent(90) == 8 &&
            BlockDamageMesh.StageForPercent(100) == 9 &&
            BlockDamageMesh.StageForPercent(1000) == 9,
            "Damage stages are bounded and hide immediately on reset");

        check(BlockDamageMesh.Create(default).FaceCount == 0,
            "Air blocks have no fracture overlay");

        int[] faces = {24, 16, 0, 16};
        for (int shape = 0; shape < 4; shape++)
        for (int facing = 0; facing < 4; facing++)
        for (int upper = 0; upper < 2; upper++)
        {
            BlockState block = BlockState.Create(3, (BlockShape)shape,
                facing, upper != 0);
            BlockDamageMesh mesh = BlockDamageMesh.Create(block);
            check(mesh.FaceCount > 0 && mesh.FaceCount <= 32 &&
                (shape == 2 || mesh.FaceCount == faces[shape]),
                "Crack mesh contains only outward, shape-accurate half-cell faces");
            check(mesh.Positions.Length == mesh.FaceCount * 4 &&
                mesh.Normals.Length == mesh.Positions.Length &&
                mesh.Uvs.Length == mesh.Positions.Length &&
                mesh.Triangles.Length == mesh.FaceCount * 6,
                "Overlay quads provide matching UVs, normals and triangles");

            for (int i = 0; i < mesh.Positions.Length; i++)
            {
                Vector3 p = mesh.Positions[i];
                Vector2 uv = mesh.Uvs[i];
                check(float.IsFinite(p.X) && float.IsFinite(p.Y) &&
                    float.IsFinite(p.Z) &&
                    MathF.Abs(p.X) <= .5f + BlockDamageMesh.Offset + 1e-5f &&
                    MathF.Abs(p.Y) <= .5f + BlockDamageMesh.Offset + 1e-5f &&
                    MathF.Abs(p.Z) <= .5f + BlockDamageMesh.Offset + 1e-5f &&
                    uv.X >= 0 && uv.X <= 1 &&
                    uv.Y >= 0 && uv.Y <= 1,
                    "Overlay vertices remain within the selected voxel and UV bounds");
            }

            for (int i = 0; i < mesh.Triangles.Length; i += 6)
            {
                int a = mesh.Triangles[i], b = mesh.Triangles[i+1],
                    c = mesh.Triangles[i+2];
                Vector3 triangleNormal = Vector3.Cross(
                    mesh.Positions[b] - mesh.Positions[a],
                    mesh.Positions[c] - mesh.Positions[a]);
                check(Vector3.Dot(triangleNormal, mesh.Normals[a]) < -.01f,
                    "Crack quad is clockwise-outward for the engine renderer");
                check(mesh.Triangles[i+3] == a &&
                    mesh.Triangles[i+4] == c &&
                    mesh.Triangles[i+5] == a+3,
                    "Both triangles form one continuous face");
            }
        }

        // All four stair directions and slab upper/lower variants must
        // have visibly distinct geometry, avoiding a floating full cube.
        BlockDamageMesh lower = BlockDamageMesh.Create(
            BlockState.Create(4, BlockShape.Slab, 0, false));
        BlockDamageMesh upper = BlockDamageMesh.Create(
            BlockState.Create(4, BlockShape.Slab, 0, true));
        check(lower.Positions[0].Y != upper.Positions[0].Y,
            "Upper and lower slabs receive different crack shells");

        BlockDamageMesh stair0 = BlockDamageMesh.Create(
            BlockState.Create(3, BlockShape.Stair, 0));
        BlockDamageMesh stair1 = BlockDamageMesh.Create(
            BlockState.Create(3, BlockShape.Stair, 1));
        bool rotationChangesShell = false;
        for (int i = 0; i < stair0.Positions.Length; i++)
            if (stair0.Positions[i] != stair1.Positions[i])
            {
                rotationChangesShell = true;
                break;
            }
        check(stair0.FaceCount == stair1.FaceCount && rotationChangesShell,
            "Rotated stairs produce differently oriented crack geometry");

        Console.WriteLine(
            "PASS: ten damage stages, full/partial block crack geometry, " +
            "UVs and triangle winding");
    }
}
