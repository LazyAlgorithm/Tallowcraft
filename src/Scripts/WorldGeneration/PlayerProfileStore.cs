using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WorldGeneration;

/// <summary>
/// Persistent client bearer identity for prototype multiplayer. This is
/// deliberately not an authenticated account: servers must replace it with
/// a verified account ID before allowing public untrusted sessions.
/// </summary>
public static class PlayerIdentity
{
    public static bool Valid(string? token) =>
        token != null && Guid.TryParseExact(token, "N", out _) &&
        token == token.ToLowerInvariant();

    /// <summary>Host and client use different local slots so a two-instance
    /// localhost test never accidentally claims the same server inventory.
    /// Multiple client instances can set Tallowcraft's profile slot separately.</summary>
    public static string GetOrCreate(string playerDataRoot, string slot = "client")
    {
        if (string.IsNullOrEmpty(slot) || slot.Length > 24)
            throw new ArgumentException("Invalid local player slot.");
        foreach (char ch in slot)
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '-' && ch != '_')
                throw new ArgumentException("Invalid local player slot.");

        string folder = Path.Combine(playerDataRoot, "tallowcraft");
        string path = Path.Combine(folder, "player-id-" + slot + ".txt");
        if (File.Exists(path))
        {
            string saved = File.ReadAllText(path).Trim();
            if (!Valid(saved))
                throw new InvalidDataException(
                    "Player identity is corrupt; do not silently replace it.");
            return saved;
        }

        Directory.CreateDirectory(folder);
        string proposed = Guid.NewGuid().ToString("N");
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            byte[] bytes = Encoding.ASCII.GetBytes(proposed);
            stream.Write(bytes);
            try { stream.Flush(flushToDisk: true); }
            catch (PlatformNotSupportedException) { stream.Flush(); }
            return proposed;
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another local instance may have created an identity concurrently.
            string existing = File.ReadAllText(path).Trim();
            if (Valid(existing))
                return existing;
            throw new InvalidDataException("Concurrent player identity is corrupt.");
        }
    }
}

/// <summary>Only one live network player may claim a saved profile in a process.
/// Prevents two connections with the same bearer ID from cloning equipment.</summary>
public static class PlayerProfileClaims
{
    private static readonly HashSet<string> Active = new(StringComparer.Ordinal);
    public static bool TryClaim(string worldId, string token)
    {
        if (!PlayerIdentity.Valid(worldId) || !PlayerIdentity.Valid(token))
            return false;
        lock (Active)
            return Active.Add(worldId + ":" + token);
    }

    public static void Release(string worldId, string token)
    {
        lock (Active)
            Active.Remove(worldId + ":" + token);
    }
}

public enum ProfileLoadResult
{
    New,
    Loaded,
    Recovered,
    Corrupt
}

/// <summary>
/// One atomic server-owned profile: materials, tool durability and hotbar.
/// Saved beside the world but keyed by its world identity. Two bounded,
/// checksummed generations allow recovery after incomplete/corrupted writes.
/// </summary>
public static class PlayerProfileStore
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static string FileFor(string folder, string token)
    {
        if (!PlayerIdentity.Valid(token))
            throw new ArgumentException("Invalid player identifier.");
        return Path.Combine(folder, token + ".profile");
    }

    private static string Encode(string worldId, BlockInventory inventory,
        PlayerTools tools, int selectedType, int selectedShape, int facing)
    {
        if (!PlayerIdentity.Valid(worldId) ||
            selectedType is < 1 or > 6 ||
            selectedShape is < 0 or > 3 ||
            facing is < 0 or > 3)
            throw new ArgumentException("Invalid saved player values.");

        string payload = string.Join(";", "1", worldId,
            inventory.Serialize(), tools.Serialize(),
            selectedType.ToString(Invariant),
            selectedShape.ToString(Invariant), facing.ToString(Invariant));
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant() + "\n" + payload + "\n";
    }

    private static bool TryDecode(string file, string worldId,
        out BlockInventory inventory, out PlayerTools tools,
        out int selectedType, out int selectedShape, out int facing)
    {
        inventory = new BlockInventory();
        tools = new PlayerTools();
        selectedType = 3;
        selectedShape = 0;
        facing = 0;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > 512)
                return false;
            string[] lines = File.ReadAllLines(file);
            if (lines.Length != 2 || lines[0].Length != 64)
                return false;
            string digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(lines[1])))
                .ToLowerInvariant();
            if (!string.Equals(digest, lines[0], StringComparison.Ordinal))
                return false;
            string[] fields = lines[1].Split(';');
            if (fields.Length != 7 || fields[0] != "1" ||
                fields[1] != worldId ||
                !inventory.TryLoad(fields[2]) ||
                !tools.TryLoad(fields[3]) ||
                !int.TryParse(fields[4], NumberStyles.None,
                    Invariant, out int hotbar) ||
                !int.TryParse(fields[5], NumberStyles.None,
                    Invariant, out int shape) ||
                !int.TryParse(fields[6], NumberStyles.None,
                    Invariant, out int rotation) ||
                hotbar is < 1 or > 6 || shape is < 0 or > 3 ||
                rotation is < 0 or > 3)
                return false;
            selectedType = hotbar;
            selectedShape = shape;
            facing = rotation;
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }

    public static ProfileLoadResult Load(string folder, string worldId,
        string token, out BlockInventory inventory, out PlayerTools tools,
        out int selectedType, out int selectedShape, out int facing)
    {
        if (!PlayerIdentity.Valid(worldId))
            throw new ArgumentException("Invalid world identity.");
        string path = FileFor(folder, token);
        inventory = new BlockInventory();
        tools = new PlayerTools();
        selectedType = 3;
        selectedShape = 0;
        facing = 0;
        string backup = path + ".bak";

        if (!File.Exists(path) && !File.Exists(backup))
        {
            inventory.FillStarter();
            return ProfileLoadResult.New;
        }
        if (TryDecode(path, worldId, out inventory, out tools,
                out selectedType, out selectedShape, out facing))
            return ProfileLoadResult.Loaded;

        if (!TryDecode(backup, worldId, out inventory, out tools,
                out selectedType, out selectedShape, out facing))
            return ProfileLoadResult.Corrupt;

        // Only repair the current file after validating the backup. Do not
        // rotate the backup: we have one trusted copy remaining.
        try
        {
            string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.Copy(backup, pending);
            File.Move(pending, path, overwrite: true);
        }
        catch (IOException) { /* recovered profile stays in memory */ }
        catch (UnauthorizedAccessException) { /* storage read-only */ }
        return ProfileLoadResult.Recovered;
    }

    public static void Save(string folder, string worldId, string token,
        BlockInventory inventory, PlayerTools tools,
        int selectedType, int selectedShape, int facing)
    {
        string path = FileFor(folder, token);
        string encoded = Encode(worldId, inventory, tools,
            selectedType, selectedShape, facing);
        Directory.CreateDirectory(folder);
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(encoded);
                stream.Write(bytes);
                try { stream.Flush(flushToDisk: true); }
                catch (PlatformNotSupportedException) { stream.Flush(); }
            }
            if (File.Exists(path))
                File.Move(path, path + ".bak", overwrite: true);
            File.Move(pending, path);
        }
        finally
        {
            if (File.Exists(pending))
                File.Delete(pending);
        }
    }
}
