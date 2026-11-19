using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace WorldGeneration;

/// <summary>
/// Immutable, structurally shared set of edited 16x16x16 sections. Jobs may
/// keep an old snapshot while new edits replace one section only.
/// </summary>
public sealed class SectionedEditSnapshot : IReadOnlyDictionary<BlockPosition, ushort>
{
    internal readonly ImmutableDictionary<ChunkKey, ImmutableDictionary<BlockPosition, ushort>> Sections;
    public int Count { get; }
    public IEnumerable<BlockPosition> Keys
    {
        get { foreach (var entry in this) yield return entry.Key; }
    }
    public IEnumerable<ushort> Values
    {
        get { foreach (var entry in this) yield return entry.Value; }
    }

    internal SectionedEditSnapshot(
        ImmutableDictionary<ChunkKey, ImmutableDictionary<BlockPosition, ushort>> sections,
        int count)
    {
        Sections = sections;
        Count = count;
    }

    public static ChunkKey SectionOf(BlockPosition p) =>
        new(ChunkGenerator.FloorDiv(p.X, ChunkGenerator.Size),
            ChunkGenerator.FloorDiv(p.Y, ChunkGenerator.Size),
            ChunkGenerator.FloorDiv(p.Z, ChunkGenerator.Size));

    public IReadOnlyDictionary<BlockPosition, ushort> InSection(ChunkKey key) =>
        Sections.TryGetValue(key, out var section) ? section :
            ImmutableDictionary<BlockPosition, ushort>.Empty;

    /// <summary>Only the sections around one mesh may affect exposed faces.</summary>
    public IEnumerable<KeyValuePair<BlockPosition, ushort>> Around(ChunkKey key)
    {
        for (int y = key.Y - 1; y <= key.Y + 1; y++)
        for (int z = key.Z - 1; z <= key.Z + 1; z++)
        for (int x = key.X - 1; x <= key.X + 1; x++)
            foreach (var edit in InSection(new ChunkKey(x, y, z)))
                yield return edit;
    }

    public ushort this[BlockPosition key] =>
        TryGetValue(key, out ushort value) ? value :
            throw new KeyNotFoundException();

    public bool ContainsKey(BlockPosition key) => TryGetValue(key, out _);

    public bool TryGetValue(BlockPosition key, out ushort value)
    {
        if (Sections.TryGetValue(SectionOf(key), out var section))
            return section.TryGetValue(key, out value);
        value = 0;
        return false;
    }

    public IEnumerator<KeyValuePair<BlockPosition, ushort>> GetEnumerator()
    {
        foreach (var section in Sections.Values)
            foreach (var entry in section)
                yield return entry;
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
