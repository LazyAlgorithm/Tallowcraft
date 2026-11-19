using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace WorldGeneration;

/// <summary>
/// Canonical, compact format shared by disk saves, client cache and network
/// pages. Each entry is 2 bytes of local voxel index and 2 bytes of packed
/// block state; only deviations from procedural terrain are stored.
/// </summary>
public static class EditSectionCodec
{
    public const int PageEdits = 64;
    public const int MaxSectionEdits = 4096;
    public const int MaxBytes = MaxSectionEdits * 4;

    public static ushort Index(BlockPosition p) =>
        (ushort)(((p.Y & 15) << 8) | ((p.Z & 15) << 4) | (p.X & 15));

    public static BlockPosition Position(ChunkKey key, int index) =>
        new(key.X * 16 + (index & 15),
            key.Y * 16 + ((index >> 8) & 15),
            key.Z * 16 + ((index >> 4) & 15));

    public static byte[] Encode(IReadOnlyDictionary<BlockPosition, ushort> section)
    {
        if (section.Count > MaxSectionEdits)
            throw new ArgumentException("A section cannot have more than 4096 edits.");
        var ordered = new List<(ushort Local, ushort Value)>(section.Count);
        foreach (var edit in section)
            ordered.Add((Index(edit.Key), edit.Value));
        ordered.Sort((a,b) => a.Local.CompareTo(b.Local));
        byte[] bytes = new byte[ordered.Count * 4];
        for (int i = 0; i < ordered.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 4), ordered[i].Local);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 4 + 2), ordered[i].Value);
        }
        return bytes;
    }

    public static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static int Pages(int byteCount) =>
        Math.Max(1, (byteCount + PageEdits * 4 - 1) / (PageEdits * 4));

    public static string Page(byte[] bytes, int page) =>
        page < 0 || page >= Pages(bytes.Length)
            ? throw new ArgumentOutOfRangeException(nameof(page))
            : Convert.ToBase64String(bytes, page * PageEdits * 4,
                Math.Min(PageEdits * 4, bytes.Length - page * PageEdits * 4));

    public static bool TryPage(string encoded, out byte[] bytes)
    {
        bytes = [];
        if (encoded.Length > 352)
            return false;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { return false; }
        return bytes.Length <= PageEdits * 4 && bytes.Length % 4 == 0;
    }

    public static bool TryDecode(ChunkKey key, int seed, byte[] bytes,
        out Dictionary<BlockPosition, ushort> result)
    {
        result = new Dictionary<BlockPosition, ushort>();
        if (bytes.Length > MaxBytes || bytes.Length % 4 != 0 ||
            key.Y < ChunkGenerator.MinChunkY || key.Y > ChunkGenerator.MaxChunkY)
            return false;
        int previous = -1;
        for (int i = 0; i < bytes.Length; i += 4)
        {
            int local = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i));
            ushort state = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 2));
            BlockPosition p = Position(key, local);
            if (local <= previous || local >= 4096 ||
                !BlockEdits.ValidPosition(p) ||
                !BlockState.IsValid(state) ||
                ChunkGenerator.BaseBlock(p.X, p.Y, p.Z, seed) == state)
                return false;
            result.Add(p, state);
            previous = local;
        }
        return true;
    }
}
