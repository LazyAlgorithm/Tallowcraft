namespace WorldGeneration;

public readonly record struct ChunkFace(
    int DX,
    int DY,
    int DZ,
    Vector3 Normal,
    Vector3 A,
    Vector3 B,
    Vector3 C,
    Vector3 D)
{
    public static readonly ChunkFace[] All =
    {
        // Front (+Z)
        new(0, 0, 1, Vector3.UnitZ,
            new(0,0,1), new(1,0,1),
            new(1,1,1), new(0,1,1)),

        // Back (-Z)
        new(0, 0, -1, -Vector3.UnitZ,
            new(0,0,0), new(0,1,0),
            new(1,1,0), new(1,0,0)),

        // Right (+X)
        new(1, 0, 0, Vector3.UnitX,
            new(1,0,0), new(1,1,0),
            new(1,1,1), new(1,0,1)),

        // Left (-X)
        new(-1, 0, 0, -Vector3.UnitX,
            new(0,0,0), new(0,0,1),
            new(0,1,1), new(0,1,0)),

        // Top (+Y)
        new(0, 1, 0, Vector3.UnitY,
            new(0,1,0), new(0,1,1),
            new(1,1,1), new(1,1,0)),

        // Bottom (-Y)
        new(0, -1, 0, -Vector3.UnitY,
            new(0,0,0), new(1,0,0),
            new(1,0,1), new(0,0,1))
    };
}
