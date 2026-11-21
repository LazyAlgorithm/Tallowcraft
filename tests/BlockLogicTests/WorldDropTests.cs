using System;
using System.Numerics;
using WorldGeneration;

internal static class WorldDropTests
{
    public static void Run(Action<bool, string> check)
    {
        for (int type = 1; type <= BlockInventory.MaxType; type++)
        {
            if (BlockDropMesh.IsCube(type))
            {
                BlockDropMesh cube = BlockDropMesh.ForType(type);
                check(cube.Positions.Length == 24 &&
                    cube.Normals.Length == 24 &&
                    cube.Uvs.Length == 24 &&
                    cube.Triangles.Length == 36,
                    "Each building block drops as a six-faced miniature cube");
                for (int face = 0; face < 6; face++)
                {
                    int tile = BlockTextureAtlas.TileFor(
                        BlockState.Create(type), face);
                    check(cube.Uvs[face*4] ==
                        BlockTextureAtlas.FaceUV(tile, face, 0f, 0f) &&
                        cube.Uvs[face*4+2] ==
                        BlockTextureAtlas.FaceUV(tile, face, 1f, 1f),
                        "Mini cubes reuse exactly the placed block's face textures");
                }
                foreach (Vector3 v in cube.Positions)
                    check(MathF.Abs(v.X) <= BlockDropMesh.Size*.5f &&
                        MathF.Abs(v.Y) <= BlockDropMesh.Size*.5f &&
                        MathF.Abs(v.Z) <= BlockDropMesh.Size*.5f,
                        "Dropped cubes are much smaller than terrain blocks");
                check(DroppedItemMesh.ForType(type).Triangles.Length == 0,
                    "Block items do not create a billboard sprite");
            }
            else if (type is BlockInventory.Wool or BlockInventory.RawPork)
            {
                // Animal resources use tiny, locally colored prefab cubes,
                // not atlas block faces or ore/ingot billboard UV tiles.
                check(!BlockDropMesh.IsCube(type) &&
                      !DroppedItemMesh.IsBillboard(type) &&
                      BlockDropMesh.ForType(type).Triangles.Length == 0 &&
                      DroppedItemMesh.ForType(type).Triangles.Length == 0,
                    "Wool and raw pork are rendered by the WorldDrop cosmetic cube path");
            }
            else
            {
                check(DroppedItemMesh.IsBillboard(type),
                    "Ore and ingot resources use camera-facing sprites");
                DroppedItemMesh sprite = DroppedItemMesh.ForType(type);
                check(sprite.Positions.Length == 4 &&
                    sprite.Normals.Length == 4 &&
                    sprite.Uvs.Length == 4 &&
                    sprite.Triangles.Length == 6,
                    "Coal, iron ore and ingots have a single billboard quad");
                int col = (type-1)%5, row = (type-1)/5;
                foreach (Vector2 uv in sprite.Uvs)
                    check(uv.X > col/5f && uv.X < (col+1)/5f &&
                        uv.Y > row/2f && uv.Y < (row+1)/2f,
                        "Billboard UVs remain inside the correct resource icon");
                check(BlockDropMesh.ForType(type).Triangles.Length == 0,
                    "Ore and ingots are not rendered as solid mini cubes");
            }
        }

        check(DroppedItemMesh.ForType(0).Triangles.Length == 0 &&
            DroppedItemMesh.ForType(12).Triangles.Length == 0 &&
            BlockDropMesh.ForType(0).Triangles.Length == 0 &&
            !BlockDropMesh.IsCube(BlockInventory.IronIngot),
            "Unknown types and ingots never become fake voxel cubes");
        check(DroppedItemMesh.InPickupRange(
                new Vector3(0,0,0),new Vector3(1,0,0)) &&
            !DroppedItemMesh.InPickupRange(
                new Vector3(0,0,0),new Vector3(1.7f,0,0)) &&
            !DroppedItemMesh.InPickupRange(
                new Vector3(0,0,0),new Vector3(float.NaN,0,0)),
            "Server pickup distance still rejects remote positions");

        Console.WriteLine("PASS: textured mini block cubes, ore/ingot billboards, " +
            "atlas UV mapping and server pickup limits");
    }
}
