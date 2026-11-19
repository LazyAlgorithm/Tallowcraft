using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace WorldGeneration;

/// <summary>
/// Server-owned sparse edits in persistent 16^3 sections. Only edited sections
/// change on a write; immutable snapshots remain safe on mesh worker threads.
/// Per-section revisions advance even when an edit restores the base block.
/// </summary>
public sealed class BlockEdits
{
    public const int MaxWorldCoordinate = 1_000_000;
    public const int LegacyMaxEncodedLength = 20000;
    private ImmutableDictionary<ChunkKey, ImmutableDictionary<BlockPosition, ushort>>
        _sections = ImmutableDictionary<ChunkKey, ImmutableDictionary<BlockPosition, ushort>>.Empty;
    private ImmutableDictionary<ChunkKey, int> _revisions =
        ImmutableDictionary<ChunkKey, int>.Empty;
    private SectionedEditSnapshot _snapshot;
    private int _count;

    public BlockEdits()
    {
        _snapshot = new SectionedEditSnapshot(_sections, 0);
    }

    public SectionedEditSnapshot Snapshot => _snapshot;
    public int Count => _count;
    public IEnumerable<ChunkKey> Keys => _revisions.Keys;
    public int Revision(ChunkKey key) => _revisions.TryGetValue(key, out int v) ? v : 0;

    public ushort Get(int x, int y, int z, int seed) =>
        _snapshot.TryGetValue(new BlockPosition(x, y, z), out ushort value)
            ? value : ChunkGenerator.BaseBlock(x, y, z, seed);

    public static bool ValidPosition(BlockPosition p) =>
        ChunkGenerator.InWorld(p.Y) &&
        Math.Abs((long)p.X) <= MaxWorldCoordinate &&
        Math.Abs((long)p.Y) <= MaxWorldCoordinate &&
        Math.Abs((long)p.Z) <= MaxWorldCoordinate;

    public bool Set(BlockPosition p, ushort value, int seed)
    {
        if (!ValidPosition(p) || !BlockState.IsValid(value) ||
            Get(p.X, p.Y, p.Z, seed) == value)
            return false;

        ChunkKey key = SectionedEditSnapshot.SectionOf(p);
        var old = _snapshot.InSection(key);
        var section = _sections.TryGetValue(key, out var current)
            ? current : ImmutableDictionary<BlockPosition, ushort>.Empty;
        ushort original = ChunkGenerator.BaseBlock(p.X, p.Y, p.Z, seed);
        section = value == original ? section.Remove(p) : section.SetItem(p, value);

        _count += section.Count - old.Count;
        _sections = section.Count == 0 ? _sections.Remove(key) :
            _sections.SetItem(key, section);
        _revisions = _revisions.SetItem(key, checked(Revision(key) + 1));
        _snapshot = new SectionedEditSnapshot(_sections, _count);
        return true;
    }

    public IReadOnlyDictionary<BlockPosition, ushort> InSection(ChunkKey key) =>
        _snapshot.InSection(key);

    /// <summary>
    /// Atomically replace one complete section from a validated remote
    /// baseline, or when restoring saved data.
    /// </summary>
    public List<BlockPosition> ReplaceSection(ChunkKey key,
        IReadOnlyDictionary<BlockPosition, ushort> entries, int revision,
        int seed)
    {
        if (revision < 0 || key.Y < ChunkGenerator.MinChunkY ||
            key.Y > ChunkGenerator.MaxChunkY || entries.Count > 4096)
            throw new ArgumentException("Invalid section revision or size.");

        var newSection = ImmutableDictionary.CreateBuilder<BlockPosition, ushort>();
        foreach (var item in entries)
        {
            if (SectionedEditSnapshot.SectionOf(item.Key) != key ||
                !ValidPosition(item.Key) || !BlockState.IsValid(item.Value) ||
                item.Value == ChunkGenerator.BaseBlock(
                    item.Key.X, item.Key.Y, item.Key.Z, seed))
                throw new ArgumentException("Invalid noncanonical section edit.");
            newSection.Add(item.Key, item.Value);
        }

        IReadOnlyDictionary<BlockPosition, ushort> old = InSection(key);
        var dirty = new List<BlockPosition>();
        foreach (var item in old)
            if (!newSection.TryGetValue(item.Key, out ushort val) ||
                val != item.Value)
                dirty.Add(item.Key);
        foreach (var item in newSection)
            if (!old.ContainsKey(item.Key))
                dirty.Add(item.Key);

        _count += newSection.Count - old.Count;
        _sections = newSection.Count == 0 ? _sections.Remove(key) :
            _sections.SetItem(key, newSection.ToImmutable());
        _revisions = _revisions.SetItem(key, revision);
        _snapshot = new SectionedEditSnapshot(_sections, _count);
        return dirty;
    }

    /// <summary>Only a contiguous revision may mutate a client's section.</summary>
    public bool ApplyDelta(ChunkKey key, int revision,
        BlockPosition position, ushort value, int seed)
    {
        if (revision <= 0 || Revision(key) != revision - 1 ||
            SectionedEditSnapshot.SectionOf(position) != key ||
            !ValidPosition(position) || !BlockState.IsValid(value))
            return false;
        if (!Set(position, value, seed))
            return false;
        // Set() already incremented the revision by one.
        return true;
    }

    /// <summary>Snapshot for atomic background saves.</summary>
    public IReadOnlyDictionary<ChunkKey, int> Revisions => _revisions;

    /// <summary>Legacy v1/v2 format for migration and pure regression tests.
    /// Not used for network replication or the new disk save.</summary>
    public string Serialize(int seed)
    {
        var builder = new StringBuilder("2|");
        builder.Append(seed.ToString(CultureInfo.InvariantCulture)).Append('|');
        var positions = new List<BlockPosition>(_snapshot.Keys);
        positions.Sort((a,b) => a.X != b.X ? a.X.CompareTo(b.X) :
            a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.Y.CompareTo(b.Y));
        foreach (BlockPosition p in positions)
            builder.Append(p.X.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(p.Y.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(p.Z.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(_snapshot[p].ToString(CultureInfo.InvariantCulture)).Append(';');
        return builder.ToString();
    }

    public bool TryLoad(string encoded, out int seed,
        out List<BlockPosition> changed)
    {
        seed = 0;
        changed = new List<BlockPosition>();
        if (string.IsNullOrEmpty(encoded) || encoded.Length > LegacyMaxEncodedLength)
            return false;
        string[] header = encoded.Split('|', 3);
        if (header.Length != 3 || (header[0] != "2" && header[0] != "1") ||
            !int.TryParse(header[1], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int parsedSeed))
            return false;

        bool legacy = header[0] == "1";
        var next = new Dictionary<ChunkKey, Dictionary<BlockPosition, ushort>>();
        foreach (string row in header[2].Split(';',
            StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = row.Split(',');
            if (fields.Length != 4 ||
                !int.TryParse(fields[0], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int x) ||
                !int.TryParse(fields[1], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int y) ||
                !int.TryParse(fields[2], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int z) ||
                !ushort.TryParse(fields[3], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out ushort packed))
                return false;
            if (legacy)
            {
                if (packed > 1) return false;
                packed = packed == 0 ? (ushort)0 : (ushort)3;
            }
            BlockPosition p = new(x, y, z);
            if (!ValidPosition(p) || !BlockState.IsValid(packed))
                return false;
            ChunkKey key = SectionedEditSnapshot.SectionOf(p);
            if (!next.TryGetValue(key, out var section))
                next.Add(key, section = new Dictionary<BlockPosition, ushort>());
            if (!section.TryAdd(p, packed))
                return false;
        }

        var replacement = new BlockEdits();
        foreach (var section in next)
        {
            // Legacy worlds could contain redundant natural blocks; discard
            // those rather than growing the new sparse world unnecessarily.
            var canonical = new Dictionary<BlockPosition, ushort>();
            foreach (var edit in section.Value)
                if (edit.Value != ChunkGenerator.BaseBlock(
                    edit.Key.X, edit.Key.Y, edit.Key.Z, parsedSeed))
                    canonical.Add(edit.Key, edit.Value);
            replacement.ReplaceSection(section.Key, canonical, 1, parsedSeed);
        }
        foreach (var old in _snapshot)
            if (!replacement.Snapshot.TryGetValue(old.Key, out ushort value) ||
                value != old.Value)
                changed.Add(old.Key);
        foreach (var now in replacement.Snapshot)
            if (!_snapshot.ContainsKey(now.Key))
                changed.Add(now.Key);
        _sections = replacement._sections;
        _revisions = replacement._revisions;
        _snapshot = replacement._snapshot;
        _count = replacement._count;
        seed = parsedSeed;
        return true;
    }
}
