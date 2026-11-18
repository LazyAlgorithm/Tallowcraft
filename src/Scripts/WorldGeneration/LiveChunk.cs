using Tallow;

namespace WorldGeneration;

/// <summary>One locally generated chunk; never a replicated GameObject.</summary>
public sealed class LiveChunk
{
    public GameObject Object;
    public MeshRenderer Renderer;
    public byte[]? Blocks;
    public bool Dirty;

    public LiveChunk(GameObject obj, MeshRenderer renderer, byte[]? blocks)
    {
        Object = obj;
        Renderer = renderer;
        Blocks = blocks;
    }
}
