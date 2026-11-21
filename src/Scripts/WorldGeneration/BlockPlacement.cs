using System.Numerics;

namespace WorldGeneration;

/// <summary>
/// Pure rules for deciding where a chosen block goes. Shared by the owner's
/// preview and the authoritative server. Does not access Unity/Tallow objects,
/// physics, the local input device, or any mutable world state.
/// </summary>
public static class BlockPlacement
{
    /// <summary>
    /// On top of a lower slab (or below an upper slab), placing another slab
    /// of the same type fills the missing half and creates a normal full block.
    /// When given a hit height, orient ordinary slabs and stairs to the
    /// clicked half. The selected upper flag then acts as a manual flip.
    /// </summary>
    public static bool TryPlan(BlockPosition hit, BlockPosition adjacent,
        Vector3 hitNormal, BlockState existing, BlockState selected,
        out BlockPosition target, out ushort replacement, float? hitY = null)
    {
        target = adjacent;
        replacement = selected.Packed;
        if (!BlockState.IsValid(selected.Packed) || selected.IsAir)
            return false;

        if (selected.Shape == BlockShape.Slab &&
            existing.Shape == BlockShape.Slab &&
            existing.Type == selected.Type &&
            ((!existing.Upper && hitNormal.Y > 0.5f) ||
             (existing.Upper && hitNormal.Y < -0.5f)))
        {
            target = hit;
            replacement = BlockState.Create(selected.Type).Packed;
        }
        else if (hitY is float y && selected.Shape is BlockShape.Slab or BlockShape.Stair)
        {
            bool upper = hitNormal.Y < -0.5f ||
                (hitNormal.Y <= 0.5f && y - hit.Y > 0.5f);
            replacement = BlockState.Create(selected.Type, selected.Shape,
                selected.Facing, upper != selected.Upper).Packed;
        }

        return true;
    }

    /// <summary>Only empty voxels or same-type slab-to-full-block merges.</summary>
    public static bool CanReplace(ushort existing, ushort replacement)
    {
        if (!BlockState.IsValid(existing) || !BlockState.IsValid(replacement) ||
            replacement == 0)
            return false;
        if (existing == 0)
            return true;
        BlockState oldState = new(existing);
        BlockState newState = new(replacement);
        return oldState.Shape == BlockShape.Slab &&
            newState.Shape == BlockShape.Cube &&
            oldState.Type == newState.Type;
    }

    /// <summary>
    /// Only freshly occupied half-cells need physics overlap queries during
    /// a slab merge; terrain in an already occupied half is not an obstacle.
    /// </summary>
    public static bool AddsMicro(ushort existing, ushort replacement,
        int x, int y, int z) =>
        new BlockState(replacement).ContainsMicro(x, y, z) &&
        !new BlockState(existing).ContainsMicro(x, y, z);
}
