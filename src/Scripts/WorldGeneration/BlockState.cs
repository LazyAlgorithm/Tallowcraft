using System;
using System.Numerics;

namespace WorldGeneration;

/// <summary>Compact, network-serializable voxel identity (not a mesh or GameObject).</summary>
public enum BlockShape : byte { Cube = 0, Slab = 1, Stair = 2, HalfWall = 3 }

public readonly record struct BlockState(ushort Packed)
{
    // Bits 0-7: type, 8-9: facing (+Z,+X,-Z,-X),
    // 10-11: shape, 12: upper (slabs and stairs only).
    public int Type => Packed & 255;
    public int Facing => (Packed >> 8) & 3;
    public BlockShape Shape => (BlockShape)((Packed >> 10) & 3);
    public bool Upper => (Packed & 0x1000) != 0;
    public bool IsAir => Type == 0;

    public static BlockState Create(int type, BlockShape shape = BlockShape.Cube,
        int facing = 0, bool upper = false)
    {
        if (type < 1 || type > 6 || (uint)shape > 3)
            return default;
        if (shape != BlockShape.Slab && shape != BlockShape.Stair)
            upper = false;
        return new BlockState((ushort)(type | ((facing & 3) << 8) |
            ((int)shape << 10) | (upper ? 0x1000 : 0)));
    }

    public static bool IsValid(ushort packed)
    {
        if (packed == 0) return true;
        if ((packed & 0xE000) != 0 || (packed & 255) is < 1 or > 6)
            return false;
        int shape = (packed >> 10) & 3;
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
        return Shape switch
        {
            BlockShape.Cube => true,
            BlockShape.Slab => y == (Upper ? 1 : 0),
            BlockShape.Stair => y == (Upper ? 1 : 0) || back,
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
