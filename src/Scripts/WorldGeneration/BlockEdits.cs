using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WorldGeneration;

/// <summary>
/// Server-owned sparse, copy-on-write block states, synchronized as a bounded,
/// versioned snapshot. Shapes/facings are sent as one packed ushort per edit.
/// </summary>
public sealed class BlockEdits
{
    public const int MaxChanges = 512;
    public const int MaxEncodedLength = 20000;
    public const int MaxWorldCoordinate = 1_000_000;

    private Dictionary<BlockPosition, ushort> _changes = new();
    public IReadOnlyDictionary<BlockPosition, ushort> Snapshot => _changes;
    public int Count => _changes.Count;

    public ushort Get(int x, int y, int z, int seed) =>
        _changes.TryGetValue(new BlockPosition(x, y, z), out ushort value)
            ? value : ChunkGenerator.BaseBlock(x, y, z, seed);

    public bool Set(BlockPosition position, ushort value, int seed)
    {
        if (!ChunkGenerator.InWorld(position.Y) ||
            Math.Abs((long)position.Y) > MaxWorldCoordinate ||
            Math.Abs((long)position.X) > MaxWorldCoordinate ||
            Math.Abs((long)position.Z) > MaxWorldCoordinate ||
            !BlockState.IsValid(value))
            return false;

        ushort original = ChunkGenerator.BaseBlock(position.X, position.Y, position.Z, seed);
        if (Get(position.X, position.Y, position.Z, seed) == value)
            return false;
        if (value != original && !_changes.ContainsKey(position) && _changes.Count >= MaxChanges)
            return false;

        var next = new Dictionary<BlockPosition, ushort>(_changes);
        if (value == original)
            next.Remove(position);
        else
            next[position] = value;
        _changes = next;
        return true;
    }

    public string Serialize(int seed)
    {
        var builder = new StringBuilder("2|");
        builder.Append(seed.ToString(CultureInfo.InvariantCulture)).Append('|');
        var positions = new List<BlockPosition>(_changes.Keys);
        positions.Sort((a, b) =>
        {
            int x = a.X.CompareTo(b.X);
            if (x != 0) return x;
            int z = a.Z.CompareTo(b.Z);
            return z != 0 ? z : a.Y.CompareTo(b.Y);
        });
        foreach (BlockPosition p in positions)
        {
            builder.Append(p.X.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(p.Y.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(p.Z.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(_changes[p].ToString(CultureInfo.InvariantCulture)).Append(';');
        }
        return builder.ToString();
    }

    public bool TryLoad(string encoded, out int seed, out List<BlockPosition> changed)
    {
        seed = 0;
        changed = new List<BlockPosition>();
        if (string.IsNullOrEmpty(encoded) || encoded.Length > MaxEncodedLength)
            return false;

        string[] header = encoded.Split('|', 3);
        if (header.Length != 3 || (header[0] != "2" && header[0] != "1") ||
            !int.TryParse(header[1], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int parsedSeed))
            return false;

        bool legacy = header[0] == "1";
        var next = new Dictionary<BlockPosition, ushort>();
        foreach (string row in header[2].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] values = row.Split(',');
            if (values.Length != 4 ||
                !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
                !int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ||
                !int.TryParse(values[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int z) ||
                !ushort.TryParse(values[3], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out ushort packed))
                return false;

            if (legacy)
            {
                if (packed > 1) return false;
                packed = packed == 0 ? (ushort)0 : (ushort)3;
            }
            if (!ChunkGenerator.InWorld(y) ||
                Math.Abs((long)y) > MaxWorldCoordinate ||
                Math.Abs((long)x) > MaxWorldCoordinate ||
                Math.Abs((long)z) > MaxWorldCoordinate ||
                !BlockState.IsValid(packed))
                return false;
            if (!next.TryAdd(new BlockPosition(x, y, z), packed) ||
                next.Count > MaxChanges)
                return false;
        }

        foreach (var pair in _changes)
            if (!next.TryGetValue(pair.Key, out ushort value) || value != pair.Value)
                changed.Add(pair.Key);
        foreach (var pair in next)
            if (!_changes.ContainsKey(pair.Key))
                changed.Add(pair.Key);

        _changes = next;
        seed = parsedSeed;
        return true;
    }
}
