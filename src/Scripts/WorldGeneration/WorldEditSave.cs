using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WorldGeneration;

/// <summary>
/// Crash-safe committed generations of section files, under a dedicated
/// server-controlled save folder. A new generation is fully written before
/// the tiny 'current' pointer changes; an incomplete save is never loaded.
/// This code is platform-neutral (works with browser virtual filesystems).
/// </summary>
public static class WorldEditSave
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public static bool HasSave(string root) =>
        File.Exists(Path.Combine(root, "current.txt"));

    public static string NewWorldId() => Guid.NewGuid().ToString("N");

    private static string Filename(ChunkKey key) =>
        $"s_{key.X}_{key.Y}_{key.Z}.bin";

    private static void DurableWrite(string path, byte[] data)
    {
        using var stream = new FileStream(path, FileMode.CreateNew,
            FileAccess.Write, FileShare.None);
        stream.Write(data);
        // The web virtual filesystem may not expose a physical fsync.
        try { stream.Flush(flushToDisk: true); }
        catch (PlatformNotSupportedException) { stream.Flush(); }
    }

    public static void Save(string root, string worldId, int seed,
        SectionedEditSnapshot edits, IReadOnlyDictionary<ChunkKey, int> revisions)
    {
        if (!Guid.TryParseExact(worldId, "N", out _))
            throw new ArgumentException("Invalid world identity.");
        Directory.CreateDirectory(root);
        string generation = NewWorldId();
        string staging = Path.Combine(root, "generations", generation + ".tmp");
        string target = Path.Combine(root, "generations", generation);
        Directory.CreateDirectory(staging);
        try
        {
            var keys = new List<ChunkKey>(revisions.Keys);
            keys.Sort((a,b) => a.X != b.X ? a.X.CompareTo(b.X) :
                a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Z.CompareTo(b.Z));
            var manifest = new StringBuilder();
            manifest.Append("3|").Append(worldId).Append('|')
                .Append(seed.ToString(Invariant)).Append('|')
                .Append(keys.Count.ToString(Invariant)).Append('\n');
            foreach (ChunkKey key in keys)
            {
                int revision = revisions[key];
                if (revision <= 0)
                    throw new InvalidDataException("Revision must be positive.");
                byte[] bytes = EditSectionCodec.Encode(edits.InSection(key));
                string hash = EditSectionCodec.Hash(bytes);
                DurableWrite(Path.Combine(staging, Filename(key)), bytes);
                manifest.Append(key.X.ToString(Invariant)).Append(',')
                    .Append(key.Y.ToString(Invariant)).Append(',')
                    .Append(key.Z.ToString(Invariant)).Append(',')
                    .Append(revision.ToString(Invariant)).Append(',')
                    .Append(hash).Append('\n');
            }

            DurableWrite(Path.Combine(staging, "manifest.txt"),
                Encoding.UTF8.GetBytes(manifest.ToString()));
            Directory.Move(staging, target);
            string pointer = Path.Combine(root, "current.txt");
            string previous = File.Exists(pointer)
                ? File.ReadAllText(pointer).Trim() : "";
            string pending = Path.Combine(root, generation + ".pointer.tmp");
            DurableWrite(pending, Encoding.ASCII.GetBytes(generation));
            File.Move(pending, pointer, overwrite: true);

            // Keep the previous committed generation as one recovery backup.
            // Remove older complete generations only AFTER the new pointer is
            // committed. Never delete an unfinished staging directory.
            string generations = Path.Combine(root, "generations");
            foreach (string dir in Directory.EnumerateDirectories(generations))
            {
                string name = Path.GetFileName(dir);
                if (name == generation || name == previous ||
                    name.EndsWith(".tmp", StringComparison.Ordinal))
                    continue;
                try { Directory.Delete(dir, recursive: true); }
                catch (IOException) { /* cleanup is best effort */ }
                catch (UnauthorizedAccessException) { /* keep backup */ }
            }
        }
        catch
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    public static bool TryLoad(string root, out string worldId,
        out int seed, out BlockEdits edits)
    {
        worldId = "";
        seed = 0;
        edits = new BlockEdits();
        try
        {
            string pointer = File.ReadAllText(Path.Combine(root, "current.txt")).Trim();
            if (!Guid.TryParseExact(pointer, "N", out _))
                return false;
            string folder = Path.Combine(root, "generations", pointer);
            string[] lines = File.ReadAllLines(Path.Combine(folder, "manifest.txt"));
            if (lines.Length < 1)
                return false;
            string[] header = lines[0].Split('|');
            if (header.Length != 4 || header[0] != "3" ||
                !Guid.TryParseExact(header[1], "N", out _) ||
                !int.TryParse(header[2], NumberStyles.Integer,
                    Invariant, out int parsedSeed) ||
                !int.TryParse(header[3], NumberStyles.None,
                    Invariant, out int count) ||
                count < 0 || count > 2_000_000 || lines.Length != count + 1)
                return false;

            var restored = new BlockEdits();
            var seen = new HashSet<ChunkKey>();
            for (int i = 1; i < lines.Length; i++)
            {
                string[] fields = lines[i].Split(',');
                if (fields.Length != 5 ||
                    !int.TryParse(fields[0], NumberStyles.Integer,
                        Invariant, out int x) ||
                    !int.TryParse(fields[1], NumberStyles.Integer,
                        Invariant, out int y) ||
                    !int.TryParse(fields[2], NumberStyles.Integer,
                        Invariant, out int z) ||
                    !int.TryParse(fields[3], NumberStyles.None,
                        Invariant, out int revision) ||
                    revision <= 0 || fields[4].Length != 64)
                    return false;
                ChunkKey key = new(x,y,z);
                if (!seen.Add(key))
                    return false;
                string path = Path.Combine(folder, Filename(key));
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length > EditSectionCodec.MaxBytes ||
                    !string.Equals(EditSectionCodec.Hash(bytes), fields[4],
                        StringComparison.Ordinal))
                    return false;
                if (!EditSectionCodec.TryDecode(key, parsedSeed, bytes,
                        out var section))
                    return false;
                restored.ReplaceSection(key, section, revision, parsedSeed);
            }

            worldId = header[1];
            seed = parsedSeed;
            edits = restored;
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }
}

/// <summary>Validated, non-authoritative client cache keyed by saved world id.</summary>
public static class WorldEditCache
{
    private static string PathFor(string root, string id, ChunkKey key) =>
        System.IO.Path.Combine(root, id, $"s_{key.X}_{key.Y}_{key.Z}.txt");

    public static bool TryRead(string root, string id, ChunkKey key, int seed,
        out int revision, out string checksum, out Dictionary<BlockPosition, ushort> section)
    {
        revision = -1;
        checksum = "";
        section = new Dictionary<BlockPosition, ushort>();
        if (!Guid.TryParseExact(id, "N", out _))
            return false;
        try
        {
            string[] lines = File.ReadAllLines(PathFor(root, id, key));
            if (lines.Length != 3 || lines[1].Length != 64 ||
                !int.TryParse(lines[0], NumberStyles.None,
                    CultureInfo.InvariantCulture, out int loadedRevision))
                return false;
            byte[] bytes = Convert.FromBase64String(lines[2]);
            if (bytes.Length > EditSectionCodec.MaxBytes ||
                EditSectionCodec.Hash(bytes) != lines[1] ||
                !EditSectionCodec.TryDecode(key, seed, bytes, out var decoded))
                return false;
            revision = loadedRevision;
            checksum = lines[1];
            section = decoded;
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (FormatException) { return false; }
    }

    public static void Write(string root, string id, ChunkKey key, int revision,
        byte[] bytes)
    {
        if (!Guid.TryParseExact(id, "N", out _) || revision < 0 ||
            bytes.Length > EditSectionCodec.MaxBytes)
            throw new ArgumentException("Invalid cache entry.");
        string path = PathFor(root, id, key);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string text = revision.ToString(CultureInfo.InvariantCulture) + "\n" +
            EditSectionCodec.Hash(bytes) + "\n" + Convert.ToBase64String(bytes);
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}
