using System.Threading.Tasks;

namespace WorldGeneration;

/// <summary>A background mesh generated against one immutable edit snapshot.</summary>
public sealed class ChunkJob
{
    public readonly Task<ChunkMesh> Task;
    public readonly int Version;

    public ChunkJob(Task<ChunkMesh> task, int version)
    {
        Task = task;
        Version = version;
    }
}
