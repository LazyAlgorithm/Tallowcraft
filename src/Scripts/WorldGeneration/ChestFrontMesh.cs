using System.Numerics;

namespace WorldGeneration;

/// <summary>
/// Very cheap decoration for placed storage blocks. Their collision and
/// terrain mesh stay ordinary cubes; this facing-specific front cover supplies
/// the lock/lid art without changing the existing packed block atlas.
/// </summary>
public static class ChestFrontMesh
{
    public static void Build(int facing, out Vector3[] positions,
        out Vector3[] normals, out Vector2[] uvs, out int[] triangles,
        out Vector3 offset)
    {
        Vector3 normal = (facing & 3) switch
        {
            0 => Vector3.UnitZ,
            1 => Vector3.UnitX,
            2 => -Vector3.UnitZ,
            _ => -Vector3.UnitX
        };
        Vector3 right = new(normal.Z,0f,-normal.X);
        Vector3 up = Vector3.UnitY;
        const float size = .5f;
        offset = normal * .504f;
        positions =
        [
            -right*size-up*size,
            -right*size+up*size,
             right*size+up*size,
             right*size-up*size
        ];
        normals = [normal,normal,normal,normal];
        uvs =
        [
            new Vector2(0f,1f),new Vector2(0f,0f),
            new Vector2(1f,0f),new Vector2(1f,1f)
        ];
        triangles = [0,2,1,0,3,2];
    }
}
