namespace WorldGeneration;

using Tallow;

public sealed class LiveChunk
{
    public GameObject Object;
    public MeshRenderer Renderer;
    public byte[]? Blocks;

    public LiveChunk(
        GameObject obj,
        MeshRenderer renderer,
        byte[]? blocks)
    {
        Object = obj;
        Renderer = renderer;
        Blocks = blocks;
    }
}
