using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace WorldGeneration;

/// <summary>A server-side snapshot of a physically spawned item drop.
/// Remaining lifetime is recovered from Age; time spent offline is paused.</summary>
public readonly record struct SavedWorldDrop(
    int ItemType, int Quantity, Vector3 Position, float Age);

/// <summary>Bounded, deterministic v1 drop file for atomic world-save v5.
/// No engine objects or runtime physics handles are serialized.</summary>
public static class WorldDropSaveCodec
{
    public const int MaxDrops = 256;
    public const float MaxAgeSeconds = 300f;
    private const int EntryBytes = 18; // type, count, X/Y/Z, age
    public const int MaxBytes = 8 + MaxDrops * EntryBytes;
    private const float MaxAbsCoordinate = 1_000_000f;

    public static bool IsValid(SavedWorldDrop drop) =>
        drop.ItemType is >= 1 and <= BlockInventory.MaxType &&
        drop.Quantity is >= 1 and <= BlockInventory.StackLimit &&
        float.IsFinite(drop.Position.X) &&
        float.IsFinite(drop.Position.Y) &&
        float.IsFinite(drop.Position.Z) &&
        MathF.Abs(drop.Position.X) <= MaxAbsCoordinate &&
        MathF.Abs(drop.Position.Y) <= MaxAbsCoordinate &&
        MathF.Abs(drop.Position.Z) <= MaxAbsCoordinate &&
        float.IsFinite(drop.Age) &&
        drop.Age >= 0f && drop.Age < MaxAgeSeconds;

    public static byte[] Encode(IReadOnlyList<SavedWorldDrop> drops)
    {
        if (drops.Count > MaxDrops)
            throw new InvalidDataException("Too many world drops.");
        var ordered = new List<SavedWorldDrop>(drops);
        foreach (SavedWorldDrop drop in ordered)
            if (!IsValid(drop))
                throw new InvalidDataException("Invalid world drop snapshot.");
        // Sort every field for repeatable checksums, regardless of spawn order.
        ordered.Sort((a,b) =>
        {
            int cmp = a.Position.X.CompareTo(b.Position.X);
            if (cmp != 0) return cmp;
            cmp = a.Position.Y.CompareTo(b.Position.Y);
            if (cmp != 0) return cmp;
            cmp = a.Position.Z.CompareTo(b.Position.Z);
            if (cmp != 0) return cmp;
            cmp = a.ItemType.CompareTo(b.ItemType);
            if (cmp != 0) return cmp;
            cmp = a.Quantity.CompareTo(b.Quantity);
            if (cmp != 0) return cmp;
            return a.Age.CompareTo(b.Age);
        });

        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
        {
            writer.Write(new byte[] { (byte)'T', (byte)'D',
                (byte)'R', (byte)'1' });
            writer.Write(ordered.Count);
            foreach (SavedWorldDrop drop in ordered)
            {
                writer.Write((byte)drop.ItemType);
                writer.Write((byte)drop.Quantity);
                writer.Write(drop.Position.X);
                writer.Write(drop.Position.Y);
                writer.Write(drop.Position.Z);
                writer.Write(drop.Age);
            }
        }
        return output.ToArray();
    }

    public static bool TryDecode(byte[] bytes,
        out IReadOnlyList<SavedWorldDrop> drops)
    {
        drops = Array.Empty<SavedWorldDrop>();
        if (bytes.Length is < 8 or > MaxBytes) return false;
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(input);
            if (reader.ReadByte() != 'T' || reader.ReadByte() != 'D' ||
                reader.ReadByte() != 'R' || reader.ReadByte() != '1')
                return false;
            int count = reader.ReadInt32();
            if (count < 0 || count > MaxDrops ||
                bytes.Length != 8 + count * EntryBytes)
                return false;
            var parsed = new SavedWorldDrop[count];
            for (int i = 0; i < count; i++)
            {
                var drop = new SavedWorldDrop(reader.ReadByte(),
                    reader.ReadByte(), new Vector3(
                        reader.ReadSingle(), reader.ReadSingle(),
                        reader.ReadSingle()), reader.ReadSingle());
                if (!IsValid(drop)) return false;
                parsed[i] = drop;
            }
            drops = parsed;
            return true;
        }
        catch (IOException) { return false; }
    }
}
