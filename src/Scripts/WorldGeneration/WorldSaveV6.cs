using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace WorldGeneration;

/// <summary>
/// Version 6 worlds store immutable, checksummed section/chest/drop blobs once
/// under objects/. A newly committed generation contains only a manifest;
/// unchanged blobs are referenced rather than copied or rewritten. Current
/// and previous pointers retain transactional rollback semantics.
/// </summary>
public static class WorldSaveV6
{
    private const int MaxSections = 2_000_000;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static int _saveCount;

    private static bool ValidHash(string value)
    {
        if (value.Length != 64) return false;
        foreach (char c in value)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        return true;
    }

    // Objects are grouped by kind; a hash is never used as a path component
    // without validating all 64 lowercase hex digits first.
    internal static string BlobPath(string root, string kind, string hash)
    {
        if (kind != "sections" && kind != "chests" && kind != "drops")
            throw new ArgumentException("Unknown world object kind.");
        if (!ValidHash(hash))
            throw new InvalidDataException("Invalid world object checksum.");
        return Path.Combine(root, "objects", kind, hash[..2], hash + ".bin");
    }

    private static void DurableWrite(string path, byte[] data)
    {
        using var stream = new FileStream(path, FileMode.CreateNew,
            FileAccess.Write, FileShare.None);
        stream.Write(data);
        try { stream.Flush(flushToDisk: true); }
        catch (PlatformNotSupportedException) { stream.Flush(); }
    }

    private static byte[] ReadBlob(string root, string kind,
        string hash, int maxBytes)
    {
        string path = BlobPath(root, kind, hash);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > maxBytes)
            throw new InvalidDataException("Missing or oversized world object.");
        byte[] data = File.ReadAllBytes(path);
        if (data.Length > maxBytes ||
            !string.Equals(EditSectionCodec.Hash(data), hash,
                StringComparison.Ordinal))
            throw new InvalidDataException("World object checksum mismatch.");
        return data;
    }

    private static string StoreBlob(string root, string kind,
        byte[] data, int maxBytes)
    {
        if (data.Length > maxBytes)
            throw new InvalidDataException("World object size exceeds limit.");
        string hash = EditSectionCodec.Hash(data);
        string target = BlobPath(root, kind, hash);
        if (File.Exists(target))
        {
            // Detect incomplete or corrupt preexisting content, including
            // crashes prior to this version's atomic blob installation.
            byte[] existing = ReadBlob(root, kind, hash, maxBytes);
            if (!existing.AsSpan().SequenceEqual(data))
                throw new InvalidDataException("World object content mismatch.");
            return hash;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string pending = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            DurableWrite(pending, data);
            try { File.Move(pending, target); }
            catch (IOException) when (File.Exists(target))
            {
                // Another world save installed this identical immutable blob.
                byte[] existing = ReadBlob(root, kind, hash, maxBytes);
                if (!existing.AsSpan().SequenceEqual(data))
                    throw;
            }
        }
        finally
        {
            if (File.Exists(pending)) File.Delete(pending);
        }
        return hash;
    }

    private static bool TryParseSectionLine(string line,
        out ChunkKey key, out int revision, out string hash)
    {
        key = default;
        revision = 0;
        hash = "";
        string[] fields = line.Split(',');
        if (fields.Length != 5 ||
            !int.TryParse(fields[0], NumberStyles.Integer, Invariant, out int x) ||
            !int.TryParse(fields[1], NumberStyles.Integer, Invariant, out int y) ||
            !int.TryParse(fields[2], NumberStyles.Integer, Invariant, out int z) ||
            !int.TryParse(fields[3], NumberStyles.None, Invariant,
                out revision) ||
            revision <= 0 || !ValidHash(fields[4]))
            return false;
        key = new ChunkKey(x, y, z);
        hash = fields[4];
        return true;
    }

    private static bool TryReadHeader(string[] lines,
        out string worldId, out int seed, out int count,
        out string chestHash, out string dropHash)
    {
        worldId = ""; seed = 0; count = 0;
        chestHash = ""; dropHash = "";
        if (lines.Length == 0) return false;
        string[] h = lines[0].Split('|');
        if (h.Length != 6 || h[0] != "6" ||
            !Guid.TryParseExact(h[1], "N", out _) ||
            !int.TryParse(h[2], NumberStyles.Integer, Invariant, out seed) ||
            !int.TryParse(h[3], NumberStyles.None, Invariant, out count) ||
            count < 0 || count > MaxSections ||
            lines.Length != count + 1 ||
            !ValidHash(h[4]) || !ValidHash(h[5]))
            return false;
        worldId = h[1];
        chestHash = h[4];
        dropHash = h[5];
        return true;
    }

    // Revision identity allows skipping expensive section re-encoding on
    // ordinary drop/chest/profile saves. A section's revision changes on
    // every voxel edit; immutable world snapshots preserve this property.
    private static Dictionary<ChunkKey, (int Revision, string Hash)>
        ReusableSections(string root, string worldId, int seed)
    {
        var result = new Dictionary<ChunkKey, (int, string)>();
        try
        {
            string generation = File.ReadAllText(
                Path.Combine(root, "current.txt")).Trim();
            if (!Guid.TryParseExact(generation, "N", out _))
                return result;
            string manifest = Path.Combine(root, "generations",
                generation, "manifest.txt");
            string[] lines = File.ReadAllLines(manifest);
            if (!TryReadHeader(lines, out string savedId,
                    out int savedSeed, out int count, out _, out _) ||
                savedId != worldId || savedSeed != seed)
                return result;
            for (int i = 1; i <= count; i++)
            {
                if (!TryParseSectionLine(lines[i],
                        out ChunkKey key, out int revision, out string hash) ||
                    !result.TryAdd(key, (revision, hash)))
                    return new();
            }
            return result;
        }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
        catch (ArgumentException) { return new(); }
    }

    public static void Save(string root, string worldId, int seed,
        SectionedEditSnapshot edits,
        IReadOnlyDictionary<ChunkKey, int> revisions,
        IReadOnlyDictionary<BlockPosition, ChestContents> chests,
        IReadOnlyList<SavedWorldDrop> drops)
    {
        if (!Guid.TryParseExact(worldId, "N", out _))
            throw new ArgumentException("Invalid world identity.");
        Directory.CreateDirectory(root);
        Dictionary<ChunkKey, (int Revision, string Hash)> reusable =
            ReusableSections(root, worldId, seed);
        string generation = WorldEditSave.NewWorldId();
        string staging = Path.Combine(root, "generations", generation + ".tmp");
        string target = Path.Combine(root, "generations", generation);
        Directory.CreateDirectory(staging);
        try
        {
            byte[] chestBytes = ChestSaveCodec.Encode(chests);
            byte[] dropBytes = WorldDropSaveCodec.Encode(drops);
            string chestHash = StoreBlob(root, "chests", chestBytes,
                ChestSaveCodec.MaxBytes);
            string dropHash = StoreBlob(root, "drops", dropBytes,
                WorldDropSaveCodec.MaxBytes);
            var keys = new List<ChunkKey>(revisions.Keys);
            keys.Sort((a,b) => a.X != b.X ? a.X.CompareTo(b.X) :
                a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Z.CompareTo(b.Z));
            if (keys.Count > MaxSections)
                throw new InvalidDataException("Too many edited sections.");

            var manifest = new StringBuilder();
            manifest.Append("6|").Append(worldId).Append('|')
                .Append(seed.ToString(Invariant)).Append('|')
                .Append(keys.Count.ToString(Invariant)).Append('|')
                .Append(chestHash).Append('|').Append(dropHash).Append('\n');
            foreach (ChunkKey key in keys)
            {
                int revision = revisions[key];
                if (revision <= 0)
                    throw new InvalidDataException("Revision must be positive.");
                string hash;
                if (reusable.TryGetValue(key, out var prior) &&
                    prior.Revision == revision &&
                    File.Exists(BlobPath(root, "sections", prior.Hash)))
                    hash = prior.Hash;
                else
                {
                    byte[] bytes = EditSectionCodec.Encode(edits.InSection(key));
                    hash = StoreBlob(root, "sections", bytes,
                        EditSectionCodec.MaxBytes);
                }
                manifest.Append(key.X.ToString(Invariant)).Append(',')
                    .Append(key.Y.ToString(Invariant)).Append(',')
                    .Append(key.Z.ToString(Invariant)).Append(',')
                    .Append(revision.ToString(Invariant)).Append(',')
                    .Append(hash).Append('\n');
            }
            DurableWrite(Path.Combine(staging, "manifest.txt"),
                Encoding.UTF8.GetBytes(manifest.ToString()));
            WorldEditSave.PublishGeneration(root, generation, staging, target);
            // Best-effort garbage collection is deliberately infrequent:
            // no full-object-store scan on each item pickup.
            if (Interlocked.Increment(ref _saveCount) % 32 == 0)
                PruneGarbage(root);
        }
        catch
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    /// <summary>Delete immutable blobs that are not referenced by ANY
    /// existing committed generation. Fail closed if any manifest or an
    /// unfinished staging directory makes the live set uncertain. This is
    /// best-effort disk reclamation; a failed scan must never fail a save.</summary>
    internal static int PruneGarbage(string root)
    {
        try
        {
            string generations = Path.Combine(root, "generations");
            if (!Directory.Exists(generations)) return 0;
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (string dir in Directory.EnumerateDirectories(generations))
            {
                string name = Path.GetFileName(dir);
                if (name.EndsWith(".tmp", StringComparison.Ordinal))
                    return 0;
                string manifest = Path.Combine(dir, "manifest.txt");
                if (!File.Exists(manifest)) return 0;
                string[] lines = File.ReadAllLines(manifest);
                if (lines.Length == 0) return 0;
                if (!lines[0].StartsWith("6|", StringComparison.Ordinal))
                {
                    string head = lines[0].Split('|')[0];
                    if (head != "3" && head != "4" && head != "5")
                        return 0;
                    continue;
                }
                if (!TryReadHeader(lines, out _, out _, out int count,
                        out string chestHash, out string dropHash))
                    return 0;
                referenced.Add("chests/" + chestHash);
                referenced.Add("drops/" + dropHash);
                for (int i = 1; i <= count; i++)
                {
                    if (!TryParseSectionLine(lines[i], out _, out _,
                            out string hash))
                        return 0;
                    referenced.Add("sections/" + hash);
                }
            }
            int removed = 0;
            foreach (string kind in new[] { "sections", "chests", "drops" })
            {
                string folder = Path.Combine(root, "objects", kind);
                if (!Directory.Exists(folder)) continue;
                foreach (string file in Directory.EnumerateFiles(folder,
                             "*.bin", SearchOption.AllDirectories))
                {
                    string hash = Path.GetFileNameWithoutExtension(file);
                    if (ValidHash(hash) &&
                        !referenced.Contains(kind + "/" + hash))
                    {
                        try { File.Delete(file); removed++; }
                        catch (IOException) { /* try again next cleanup */ }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
            return removed;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
        catch (ArgumentException) { return 0; }
    }

    public static bool TryLoad(string root, string[] lines,
        out string worldId, out int seed, out BlockEdits edits,
        out WorldChestStorage chests, out IReadOnlyList<SavedWorldDrop> drops)
    {
        worldId = ""; seed = 0; edits = new BlockEdits();
        chests = new WorldChestStorage();
        drops = Array.Empty<SavedWorldDrop>();
        try
        {
            if (!TryReadHeader(lines, out string id, out int parsedSeed,
                    out int count, out string chestHash, out string dropHash))
                return false;
            var restored = new BlockEdits();
            var seen = new HashSet<ChunkKey>();
            for (int i = 1; i <= count; i++)
            {
                if (!TryParseSectionLine(lines[i],
                        out ChunkKey key, out int revision, out string hash) ||
                    !seen.Add(key))
                    return false;
                byte[] bytes = ReadBlob(root, "sections", hash,
                    EditSectionCodec.MaxBytes);
                if (!EditSectionCodec.TryDecode(key, parsedSeed, bytes,
                        out var section))
                    return false;
                restored.ReplaceSection(key, section, revision, parsedSeed);
            }
            byte[] chestBytes = ReadBlob(root, "chests", chestHash,
                ChestSaveCodec.MaxBytes);
            if (!ChestSaveCodec.TryDecode(chestBytes, out var chestEntries))
                return false;
            foreach (BlockPosition position in chestEntries.Keys)
                if (new BlockState(restored.Get(position.X, position.Y,
                        position.Z, parsedSeed)).Type != BlockInventory.Chest)
                    return false;
            var loadedChests = new WorldChestStorage();
            loadedChests.Restore(chestEntries);
            byte[] dropBytes = ReadBlob(root, "drops", dropHash,
                WorldDropSaveCodec.MaxBytes);
            if (!WorldDropSaveCodec.TryDecode(dropBytes, out var loadedDrops))
                return false;

            worldId = id;
            seed = parsedSeed;
            edits = restored;
            chests = loadedChests;
            drops = loadedDrops;
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (InvalidDataException) { return false; }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
