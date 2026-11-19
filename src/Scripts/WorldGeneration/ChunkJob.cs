using System;
using System.Threading;
using System.Threading.Tasks;

namespace WorldGeneration;

/// <summary>
/// Background mesh generated against one immutable edit snapshot. Owns a
/// linked cancellation token so stale/far-away jobs can be stopped before
/// wasting worker time on unseen terrain.
/// </summary>
public sealed class ChunkJob
{
    public readonly Task<ChunkMesh> Task;
    public readonly int Version;
    public readonly CancellationTokenSource Cancellation;
    public readonly long EnqueuedAt;

    public ChunkJob(Task<ChunkMesh> task, int version,
        CancellationTokenSource cancellation, long enqueuedAt)
    {
        Task = task;
        Version = version;
        Cancellation = cancellation;
        EnqueuedAt = enqueuedAt;
    }
}
