using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WorldGeneration;

/// <summary>
/// Sparse, copy-on-write voxel overrides. The server owns this dictionary;
/// meshing workers only read published snapshots, which are never mutated.
/// The bounded text snapshot is synchronized by BlockWorld's NetworkIdentity.
/// </summary>
public sealed class BlockEdits
{
    // Prototype transport bound. Replace with chunk snapshots/deltas for large worlds.
    public const int MaxChanges = 512;
    public const int MaxEncodedLength = 20000;
    public const int MaxWorldCoordinate = 1_000_000;

    private Dictionary<BlockPosition, byte> _changes = new();

    public IReadOnlyDictionary<BlockPosition, byte> Snapshot => _changes;
    public int Count => _changes.Count;

    public byte Get(int x, int y, int z, int seed)
    {
        if (_changes.TryGetValue(new BlockPosition(x, y, z), out byte value))
            return value;
        return ChunkGenerator.BaseBlock(x, y, z, seed);
    }

    public bool Set(BlockPosition position, byte value, int seed)
    {
        if (position.Y < 0 || position.Y >= ChunkGenerator.Size ||
            Math.Abs((long)position.X) > MaxWorldCoordinate ||
            Math.Abs((long)position.Z) > MaxWorldCoordinate || value > 1)
            return false;

        byte original = ChunkGenerator.BaseBlock(position.X, position.Y, position.Z, seed);
        if (Get(position.X, position.Y, position.Z, seed) == value)
            return false;

        if (value != original && !_changes.ContainsKey(position) && _changes.Count >= MaxChanges)
            return false;

        var next = new Dictionary<BlockPosition, byte>(_changes);
        if (value == original)
            next.Remove(position);
        else
            next[position] = value;
        _changes = next;
        return true;
    }

    // One string carries the seed and all overrides atomically, for late joiners.
    // Culture-independent and deterministically ordered for stable network state.
    public string Serialize(int seed)
    {
        var builder = new StringBuilder("1|");
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
                   .Append(_changes[p]).Append(';');
        }

        return builder.ToString();
    }

    /// <summary>Accept an entire validated server snapshot, without partial application.</summary>
    public bool TryLoad(string encoded, out int seed, out List<BlockPosition> changed)
    {
        seed = 0;
        changed = new List<BlockPosition>();

        if (string.IsNullOrEmpty(encoded) || encoded.Length > MaxEncodedLength)
            return false;

        string[] header = encoded.Split('|', 3);
        if (header.Length != 3 || header[0] != "1" ||
            !int.TryParse(header[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedSeed))
            return false;

        var next = new Dictionary<BlockPosition, byte>();
        foreach (string row in header[2].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] values = row.Split(',');
            if (values.Length != 4 ||
                !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
                !int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ||
                !int.TryParse(values[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int z) ||
                !byte.TryParse(values[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte block))
                return false;

            if (y < 0 || y >= ChunkGenerator.Size ||
                Math.Abs((long)x) > MaxWorldCoordinate ||
                Math.Abs((long)z) > MaxWorldCoordinate || block > 1)
                return false;

            if (!next.TryAdd(new BlockPosition(x, y, z), block) ||
                next.Count > MaxChanges)
                return false;
        }

        // Include removed overrides as well as added/changed ones.
        foreach (var pair in _changes)
            if (!next.TryGetValue(pair.Key, out byte value) || value != pair.Value)
                changed.Add(pair.Key);

        foreach (var pair in next)
            if (!_changes.ContainsKey(pair.Key))
                changed.Add(pair.Key);

        _changes = next;
        seed = parsedSeed;
        return true;
    }
}
