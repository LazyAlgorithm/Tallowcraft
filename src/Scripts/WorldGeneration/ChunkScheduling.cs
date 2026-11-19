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
        long r = Math.Clamp(radius, 0, 16);
        return Math.Abs((long)key.Y - cy) <= 1 &&
            dx * dx + dz * dz <= r * r;
    }

    public static long Priority(ChunkKey key, int centerX, int centerY,
        int centerZ, int collisionRadius)
    {
        long dx = (long)key.X - centerX;
        long dz = (long)key.Z - centerZ;
        long dy = (long)key.Y - centerY;
        long horizontal = dx * dx + dz * dz;
        long near = Math.Clamp(collisionRadius, 0, 16);
        // Prefer the ground layer at the player's elevation. The layer just
        // beneath them stays fairly high priority for cave floors and physics.
        long vertical = dy == 0 ? 0 : Math.Abs(dy) *
            (dy < 0 ? 16L : 24L);
        return horizontal * 12 + vertical +
            (horizontal <= near * near ? 0 : 20);
    }
}
