using Tallow;
using System.Collections.Generic;

namespace WorldGeneration;

/// <summary>One locally generated chunk; never a replicated GameObject.</summary>
public sealed class LiveChunk
{
    public GameObject Object;
    public MeshRenderer Renderer;
    public ushort[]? Blocks;
    public bool Dirty;
    public int VertexCount;
    public int TriangleCount;
    public readonly List<GameObject> Decorations = new();

    public void ClearDecorations()
    {
        foreach (GameObject decoration in Decorations)
            if (decoration.IsValid) decoration.Destroy();
        Decorations.Clear();
    }

    public LiveChunk(GameObject obj, MeshRenderer renderer, ushort[]? blocks)
    {
        Object = obj;
        Renderer = renderer;
        Blocks = blocks;
    }
}
