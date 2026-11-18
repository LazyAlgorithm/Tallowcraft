namespace WorldGeneration;

/// <summary>Signed three-dimensional chunk coordinates.</summary>
public readonly record struct ChunkKey(int X, int Y, int Z)
{
    // Existing test/benchmark call sites default to the original surface layer.
    public ChunkKey(int x, int z) : this(x, 0, z) { }
}
