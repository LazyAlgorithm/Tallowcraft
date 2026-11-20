using System;
using System.Numerics;

namespace WorldGeneration;

/// <summary>Compact, network-serializable voxel identity (not a mesh or GameObject).</summary>
public enum BlockShape : byte { Cube = 0, Slab = 1, Stair = 2, HalfWall = 3 }

// Corner state is derived from neighboring stairs when meshing/colliding;
// authoritative saved edits keep their original, straight stair encoding.
public enum StairCorner : byte
{
    Straight = 0, InnerLeft = 1, InnerRight = 2,
    OuterLeft = 3, OuterRight = 4
}

public readonly record struct BlockState(ushort Packed)
{
    // Bits 0-7: type, 8-9: facing (+Z,+X,-Z,-X),
    // 10-11: shape, 12: upper (slabs and stairs only).
    // 13-15: transient stair corner geometry (0=straight), never required
    // for persisted world data or replication.
    public int Type => Packed & 255;
    public int Facing => (Packed >> 8) & 3;
    public BlockShape Shape => (BlockShape)((Packed >> 10) & 3);
    public bool Upper => (Packed & 0x1000) != 0;
    public StairCorner Corner => Shape == BlockShape.Stair
        ? (StairCorner)((Packed >> 13) & 7) : StairCorner.Straight;

    public BlockState WithCorner(StairCorner corner) =>
        Shape == BlockShape.Stair && (uint)corner <= 4
            ? new BlockState((ushort)((Packed & 0x1FFF) | ((int)corner << 13)))
            : this;
    public bool IsAir => Type == 0;

    public static BlockState Create(int type, BlockShape shape = BlockShape.Cube,
        int facing = 0, bool upper = false)
    {
        if (type < 1 || type > BlockInventory.MaxType ||
            type == BlockInventory.IronIngot || (uint)shape > 3 ||
            (type == BlockInventory.Chest && shape != BlockShape.Cube))
            return default;
        if (shape != BlockShape.Slab && shape != BlockShape.Stair)
            upper = false;
        return new BlockState((ushort)(type | ((facing & 3) << 8) |
            ((int)shape << 10) | (upper ? 0x1000 : 0)));
    }

    public static bool IsValid(ushort packed)
    {
        if (packed == 0) return true;
        if ((packed & 255) is < 1 or > BlockInventory.MaxType)
            return false;
        int shape = (packed >> 10) & 3;
        int corner = (packed >> 13) & 7;
        if ((shape != (int)BlockShape.Stair && corner != 0) ||
            corner > (int)StairCorner.OuterRight)
            return false;
        if ((packed & 255) == BlockInventory.IronIngot)
            return false;
        if ((packed & 255) == BlockInventory.Chest && shape != 0)
            return false;
        return (packed & 0x1000) == 0 || shape == 1 || shape == 2;
    }

    /// <summary>Which of eight half-metre subcells the block occupies.</summary>
    public bool ContainsMicro(int x, int y, int z)
    {
        if (IsAir) return false;
        bool back = Facing switch
        {
            0 => z == 0, // front faces +Z
            1 => x == 0,
            2 => z == 1,
            _ => x == 1,
        };
        bool left = Facing switch
        {
            0 => x == 0,
            1 => z == 1,
            2 => x == 1,
            _ => z == 0
        };
        bool stairStep = Corner switch
        {
            StairCorner.InnerLeft => back || left,
            StairCorner.InnerRight => back || !left,
            StairCorner.OuterLeft => back && left,
            StairCorner.OuterRight => back && !left,
            _ => back
        };
        return Shape switch
        {
            BlockShape.Cube => true,
            BlockShape.Slab => y == (Upper ? 1 : 0),
            BlockShape.Stair => y == (Upper ? 1 : 0) || stairStep,
            BlockShape.HalfWall => back,
            _ => false
        };
    }

    /// <summary>Orient the front of an interactive block toward the player.</summary>
    public static int FacingTowardPlayer(Vector3 look)
    {
        if (MathF.Abs(look.X) > MathF.Abs(look.Z))
            return look.X >= 0f ? 3 : 1;
        return look.Z >= 0f ? 2 : 0;
    }
}

/// <summary>
/// Minecraft-style corner selection from neighboring stair orientation.
/// This is an ephemeral derived mesh/collider shape, not an edited voxel:
/// changing or removing a neighboring stair automatically reverts corners,
/// without extra save records or network packets.
/// </summary>
public static class StairCornerResolver
{
    public static (int X, int Z) Forward(int facing) =>
        (facing & 3) switch
        {
            0 => (0, 1),
            1 => (1, 0),
            2 => (0, -1),
            _ => (-1, 0)
        };

    public static StairCorner Choose(BlockState center,
        BlockState front, BlockState behind)
    {
        if (center.Shape != BlockShape.Stair)
            return StairCorner.Straight;
        // Require identical upper/lower halves and perpendicular facing.
        // Different materials can still join, as in Minecraft.
        bool Compatible(BlockState neighbor) =>
            neighbor.Shape == BlockShape.Stair &&
            neighbor.Upper == center.Upper &&
            ((neighbor.Facing ^ center.Facing) & 1) != 0;
        if (Compatible(front))
            return front.Facing == ((center.Facing + 1) & 3)
                ? StairCorner.OuterLeft : StairCorner.OuterRight;
        if (Compatible(behind))
            return behind.Facing == ((center.Facing + 1) & 3)
                ? StairCorner.InnerLeft : StairCorner.InnerRight;
        return StairCorner.Straight;
    }
}
