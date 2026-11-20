using System;

namespace WorldGeneration;

/// <summary>
/// Fast, pure chunk scheduling priority. Near surface/collision chunks are
/// preferred over distant scenery and hidden vertical layers. Sorting by
/// this number requires no biome, flora or meshing work on the main thread.
/// </summary>
public static class ChunkScheduling
{
    /// <summary>
    /// Underground chunks are sealed from a surface viewer by Y=0 stone.
    /// They only become relevant when the viewer descends near the seam or
    /// mining has opened a visible shaft into their horizontal chunk.
    /// Above-ground layers still use normal visibility and occupancy checks.
    /// </summary>
    public static bool HiddenBelowSurface(ChunkKey key,
        float viewerWorldY, bool openedByEdit) =>
        key.Y < 0 && viewerWorldY >= 5f && !openedByEdit;

    /// <summary>Limit expensive cascaded chunk shadow submissions near the viewer.</summary>
    public static bool ShouldCastShadow(ChunkKey key, int cx, int cy, int cz,
        int radius)
    {
        long dx = (long)key.X - cx, dz = (long)key.Z - cz;
        long r = Math.Clamp(radius, 0, WorldDisplaySettings.MaxDistance);
        return Math.Abs((long)key.Y - cy) <= 1 &&
            dx * dx + dz * dz <= r * r;
    }

    /// <summary>
    /// Distant LOD should never monopolize the same worker pool used for
    /// collidable, full-resolution near terrain. Startup builds no far tiles
    /// until the immediate viewer footprint is ready. Single-worker targets
    /// finish the near queue first. Other targets reserve at least 75% of
    /// their workers for full-detail sections.
    /// </summary>
    public static int DistantWorkerLimit(int workers, bool nearReady,
        bool nearQueueExhausted)
    {
        workers = Math.Max(1, workers);
        if (!nearReady) return 0;
        if (workers == 1)
            return nearQueueExhausted ? 1 : 0;
        return Math.Max(1, workers / 4);
    }

    public static long Priority(ChunkKey key, int centerX, int centerY,
        int centerZ, int collisionRadius)
    {
        long dx = (long)key.X - centerX;
        long dz = (long)key.Z - centerZ;
        long dy = (long)key.Y - centerY;
        long horizontal = dx * dx + dz * dz;
        long near = Math.Clamp(collisionRadius, 0, WorldDisplaySettings.MaxDistance);
        // Prefer the ground layer at the player's elevation. The layer just
        // beneath them stays fairly high priority for cave floors and physics.
        long vertical = dy == 0 ? 0 : Math.Abs(dy) *
            (dy < 0 ? 16L : 24L);
        return horizontal * 12 + vertical +
            (horizontal <= near * near ? 0 : 20);
    }
}
