using System;
using System.Numerics;

namespace WorldGeneration;

/// <summary>Compact, network-serializable voxel identity (not a mesh or GameObject).</summary>
public enum BlockShape : byte { Cube = 0, Slab = 1, Stair = 2, HalfWall = 3, Post = 4, Beam = 5 }

// Corner state is derived from neighboring stairs when meshing/colliding;
// authoritative saved edits keep their original, straight stair encoding.
public enum StairCorner : byte
{
    Straight = 0, InnerLeft = 1, InnerRight = 2,
    OuterLeft = 3, OuterRight = 4
}

public readonly record struct BlockState(ushort Packed)
{
    // Extension tags 5 and 6 use previously invalid corner codes on cubes.
    // Existing cube, slab, stair and wall encodings retain their meaning.
    public const int MaxShape = (int)BlockShape.Beam;
    public int Type => Packed & 255;
    public int Facing => (Packed >> 8) & 3;
    public BlockShape Shape => (Packed >> 13) switch
    {
        5 => BlockShape.Post,
        6 => BlockShape.Beam,
        _ => (BlockShape)((Packed >> 10) & 3)
    };
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
            type == BlockInventory.IronIngot || (uint)shape > MaxShape ||
            (type == BlockInventory.Chest && shape != BlockShape.Cube))
            return default;
        if (shape != BlockShape.Slab && shape != BlockShape.Stair && shape != BlockShape.Beam)
            upper = false;
        int shapeBits = shape switch
        {
            BlockShape.Post => 5 << 13,
            BlockShape.Beam => 6 << 13,
            _ => (int)shape << 10
        };
        return new BlockState((ushort)(type | ((facing & 3) << 8) |
            shapeBits | (upper ? 0x1000 : 0)));
    }

    public static bool IsValid(ushort packed)
    {
        if (packed == 0) return true;
        if ((packed & 255) is < 1 or > BlockInventory.MaxType)
            return false;
        int shape = (packed >> 10) & 3;
        int corner = (packed >> 13) & 7;
        if (corner is 5 or 6)
            return shape == 0 && (packed & 255) != BlockInventory.Chest &&
                (packed & 255) != BlockInventory.IronIngot &&
                (corner == 6 || (packed & 0x1000) == 0);
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
            BlockShape.Post => back && left,
            BlockShape.Beam => back && y == (Upper ? 1 : 0),
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

    /// <summary>
    /// A perpendicular stair is not sufficient to form a corner: a straight
    /// stair continuing along the exposed side must keep its normal profile.
    /// Side neighbors are relative to the center's facing (+90° is right).
    /// </summary>
    public static StairCorner Choose(BlockState center,
        BlockState front, BlockState behind, BlockState left, BlockState right)
    {
        if (center.Shape != BlockShape.Stair)
            return StairCorner.Straight;
        bool SameHalf(BlockState state) =>
            state.Shape == BlockShape.Stair &&
            state.Upper == center.Upper;
        bool Perpendicular(BlockState state) =>
            SameHalf(state) &&
            ((state.Facing ^ center.Facing) & 1) != 0;
        bool StraightSide(BlockState state) =>
            SameHalf(state) && state.Facing == center.Facing;

        // Facing points toward the LOWER step (the front). The raised
        // half of a normal stair is BEHIND the facing direction:
        // for facing +Z, ContainsMicro raises z == 0.
        //
        // An OUTER corner is formed by a perpendicular stair beyond the
        // raised half (behind); retain only its matching raised quadrant.
        // An INNER corner is formed on the lower side (front); add the
        // matching quadrant. Swapping these makes an L turn look like a
        // solid block with a notch in the wrong place.
        if (Perpendicular(behind))
        {
            bool turningRight = behind.Facing == ((center.Facing + 1) & 3);
            // Outer-left retains the left half of the raised step. Its left
            // face still joins a straight stair. Only its right face recedes.
            if (!StraightSide(turningRight ? right : left))
                return turningRight ? StairCorner.OuterLeft : StairCorner.OuterRight;
        }
        if (Perpendicular(front))
        {
            bool turningRight = front.Facing == ((center.Facing + 1) & 3);
            // Inner-left fills the left half of the lower step. The right
            // face retains its straight profile and can continue the run.
            if (!StraightSide(turningRight ? left : right))
                return turningRight ? StairCorner.InnerLeft : StairCorner.InnerRight;
        }
        return StairCorner.Straight;
    }

    // Compatibility for isolated/test cases with no side-neighbor context.
    public static StairCorner Choose(BlockState center,
        BlockState front, BlockState behind) =>
        Choose(center, front, behind, default, default);
}
