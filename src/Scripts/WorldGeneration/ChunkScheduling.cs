using System;

namespace WorldGeneration;

/// <summary>
/// Fast, pure chunk scheduling priority. Near surface/collision chunks are
/// preferred over distant scenery and hidden vertical layers. Sorting by
/// this number requires no biome, flora or meshing work on the main thread.
/// </summary>
public static class ChunkScheduling
{
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
