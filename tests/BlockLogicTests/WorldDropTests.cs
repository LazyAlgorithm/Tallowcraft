using System;
using System.Numerics;
using WorldGeneration;

internal static class WorldDropTests
{
    public static void Run(Action<bool,string> check)
    {
        for (int type = 1; type <= 10; type++)
        {
            DroppedItemMesh sprite = DroppedItemMesh.ForType(type);
            check(sprite.Positions.Length == 8 &&
                sprite.Normals.Length == 8 &&
                sprite.Uvs.Length == 8 &&
                sprite.Triangles.Length == 12,
                "Every material has exactly two crossed pickup sprite quads");

            int column = (type - 1) % 5, row = (type - 1) / 5;
            for (int vertex = 0; vertex < sprite.Uvs.Length; vertex++)
            {
                Vector2 uv = sprite.Uvs[vertex];
                check(uv.X > column / 5f && uv.X < (column+1) / 5f &&
                    uv.Y > row / 2f && uv.Y < (row+1) / 2f,
                    "Sprite atlas UVs cannot bleed into adjacent item icons");
            }
            for (int vertex = 0; vertex < sprite.Positions.Length; vertex++)
            {
                Vector3 position = sprite.Positions[vertex];
                check(MathF.Abs(position.X) <= .2f &&
                    MathF.Abs(position.Y) <= .2f &&
                    MathF.Abs(position.Z) <= .2f,
                    "Item sprites remain inside a small physical pickup");
            }
            foreach (int index in sprite.Triangles)
                check(index is >= 0 and < 8,
                    "Crossed sprite indices always reference valid vertices");
        }

        check(DroppedItemMesh.ForType(0).Triangles.Length == 0 &&
            DroppedItemMesh.ForType(11).Triangles.Length == 0,
            "Invalid pickup types have no display geometry");
        check(DroppedItemMesh.InPickupRange(
                new Vector3(0,0,0),new Vector3(1,0,0)) &&
            !DroppedItemMesh.InPickupRange(
                new Vector3(0,0,0),new Vector3(1.7f,0,0)) &&
            !DroppedItemMesh.InPickupRange(
                new Vector3(0,0,0),new Vector3(float.NaN,0,0)),
            "Server pickup distance rejects remote and invalid positions");

        Console.WriteLine("PASS: item drop atlas UVs, crossed sprites " +
            "and server pickup distance limits");
    }
}
