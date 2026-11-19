using System;
using System.Numerics;

namespace WorldGeneration;

/// <summary>
/// Workstation-only iron processing. The client may display an availability
/// hint, but every forge action must independently check this condition on
/// the authoritative server using the server's own block data and position.
/// </summary>
public static class ForgeCrafting
{
    public const float Reach = 3.5f;
    public const int IronOre = 8;
    public const int Coal = 7;
    public const int IronIngot = BlockInventory.IronIngot;

    /// <summary>Checks a small volume around the player for a real placed
    /// forge. Does not trust client-supplied block positions or forge flags.</summary>
    public static bool NearForge(Vector3 eye,
        Func<BlockPosition, ushort> blockAt)
    {
        if (!float.IsFinite(eye.X) || !float.IsFinite(eye.Y) ||
            !float.IsFinite(eye.Z))
            return false;

        int minX = (int)MathF.Floor(eye.X - Reach);
        int maxX = (int)MathF.Floor(eye.X + Reach);
        int minY = (int)MathF.Floor(eye.Y - Reach);
        int maxY = (int)MathF.Floor(eye.Y + Reach);
        int minZ = (int)MathF.Floor(eye.Z - Reach);
        int maxZ = (int)MathF.Floor(eye.Z + Reach);
        float maxSq = Reach * Reach;

        for (int y = minY; y <= maxY; y++)
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            Vector3 center = new(x + .5f, y + .5f, z + .5f);
            if (Vector3.DistanceSquared(eye, center) > maxSq)
                continue;
            if (new BlockState(blockAt(new BlockPosition(x,y,z))).Type == 5)
                return true;
        }
        return false;
    }

    /// <summary>Forge-only recipe IDs. 1 ore + 1 coal produces one iron
    /// ingot; a five-unit batch is an atomic convenience.</summary>
    public static bool CanSmelt(BlockInventory materials, int count) =>
        (count == 1 || count == 5) &&
        materials.CanCraft(count == 1 ? 4 : 5);

    public static bool TrySmelt(BlockInventory materials, int count) =>
        CanSmelt(materials, count) &&
        materials.TryCraft(count == 1 ? 4 : 5);
}
