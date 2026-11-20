using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace WorldGeneration;

/// <summary>
/// Private, client-local navigation bookmarks. They never authorize voxel
/// edits, server teleports or network state; the server's world identity
/// simply namespaces a player's landmarks between different worlds.
/// </summary>
public sealed class WaypointBook
{
    public const int SlotCount = 4;
    public static readonly string[] Names =
        { "Home", "Building site", "Mine", "Landmark" };

    private readonly Vector3?[] _positions = new Vector3?[SlotCount];
    public int Tracked { get; private set; } = -1;

    public Vector3? Position(int slot) =>
        slot >= 0 && slot < SlotCount ? _positions[slot] : null;

    public static bool ValidPosition(Vector3 position) =>
        float.IsFinite(position.X) && float.IsFinite(position.Y) &&
        float.IsFinite(position.Z) &&
        MathF.Abs(position.X) <= 10_000_000f &&
        MathF.Abs(position.Y) <= 10_000_000f &&
        MathF.Abs(position.Z) <= 10_000_000f;

    /// <summary>World-axis directions use negative Z as north, and
    /// deliberately ignore the local camera's yaw so markers remain
    /// stable while the player turns.</summary>
    public static string Direction(Vector3 from, Vector3 to)
    {
        float dx = to.X - from.X, dz = to.Z - from.Z;
        string northSouth = dz < -3f ? "N" : dz > 3f ? "S" : "";
        string eastWest = dx > 3f ? "E" : dx < -3f ? "W" : "";
        string cardinal = northSouth + eastWest;
        return cardinal.Length == 0 ? "Here" : cardinal;
    }

    public bool Mark(int slot, Vector3 position)
    {
        if (slot < 0 || slot >= SlotCount || !ValidPosition(position))
            return false;
        _positions[slot] = position;
        if (Tracked == -1) Tracked = slot;
        return true;
    }

    public bool Clear(int slot)
    {
        if (slot < 0 || slot >= SlotCount || _positions[slot] == null)
            return false;
        _positions[slot] = null;
        if (Tracked == slot) Tracked = -1;
        return true;
    }

    public bool Track(int slot)
    {
        if (slot == -1)
        {
            Tracked = -1;
            return true;
        }
        if (slot < 0 || slot >= SlotCount || _positions[slot] == null)
            return false;
        Tracked = slot;
        return true;
    }
}

public static class WorldWaypointStore
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private const string Version = "TCW1";
    private const int MaxFileBytes = 2048;

    private static string PathFor(string root, string worldId,
        string playerToken)
    {
        if (!PlayerIdentity.Valid(worldId) ||
            !PlayerIdentity.Valid(playerToken))
            throw new ArgumentException("Invalid waypoint world or player identity.");
        return Path.Combine(root, "tallowcraft", "waypoints",
            worldId, playerToken + ".txt");
    }

    private static string EncodePosition(Vector3? position) =>
        position is Vector3 p
            ? string.Join(",", p.X.ToString("R", Invariant),
                p.Y.ToString("R", Invariant), p.Z.ToString("R", Invariant))
            : "-";

    private static bool TryDecodePosition(string text, out Vector3? position)
    {
        position = null;
        if (text == "-") return true;
        string[] parts = text.Split(',');
        if (parts.Length != 3 ||
            !float.TryParse(parts[0], NumberStyles.Float, Invariant,
                out float x) ||
            !float.TryParse(parts[1], NumberStyles.Float, Invariant,
                out float y) ||
            !float.TryParse(parts[2], NumberStyles.Float, Invariant,
                out float z))
            return false;
        Vector3 decoded = new(x, y, z);
        if (!WaypointBook.ValidPosition(decoded)) return false;
        position = decoded;
        return true;
    }

    private static string Checksum(string body) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    public static void Save(string root, string worldId,
        string playerToken, WaypointBook book)
    {
        string path = PathFor(root, worldId, playerToken);
        var lines = new string[4 + WaypointBook.SlotCount];
        lines[0] = Version;
        lines[1] = worldId;
        lines[2] = playerToken;
        if (book.Tracked < -1 || book.Tracked >= WaypointBook.SlotCount ||
            (book.Tracked >= 0 && book.Position(book.Tracked) == null))
            throw new InvalidDataException("Invalid tracked landmark.");
        lines[3] = book.Tracked.ToString(Invariant);
        for (int slot = 0; slot < WaypointBook.SlotCount; slot++)
        {
            Vector3? position = book.Position(slot);
            if (position is Vector3 p && !WaypointBook.ValidPosition(p))
                throw new InvalidDataException("Invalid landmark position.");
            lines[slot + 4] = EncodePosition(position);
        }
        string body = string.Join("\n", lines);
        string content = body + "\n" + Checksum(body) + "\n";
        if (Encoding.UTF8.GetByteCount(content) > MaxFileBytes)
            throw new InvalidDataException("Landmark save is too large.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                byte[] data = Encoding.UTF8.GetBytes(content);
                stream.Write(data);
                try { stream.Flush(flushToDisk: true); }
                catch (PlatformNotSupportedException) { stream.Flush(); }
            }
            File.Move(pending, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(pending)) File.Delete(pending);
        }
    }

    public static WaypointBook Load(string root, string worldId,
        string playerToken)
    {
        string path = PathFor(root, worldId, playerToken);
        if (!File.Exists(path)) return new WaypointBook();
        if (new FileInfo(path).Length > MaxFileBytes)
            throw new InvalidDataException("Landmark save is oversized.");
        string[] lines = File.ReadAllLines(path);
        if (lines.Length != 5 + WaypointBook.SlotCount ||
            lines[0] != Version || lines[1] != worldId ||
            lines[2] != playerToken ||
            !int.TryParse(lines[3], NumberStyles.AllowLeadingSign,
                Invariant, out int tracked) ||
            tracked < -1 || tracked >= WaypointBook.SlotCount)
            throw new InvalidDataException("Invalid landmark save header.");
        string body = string.Join("\n", lines, 0, lines.Length - 1);
        if (!string.Equals(Checksum(body), lines[^1],
                StringComparison.Ordinal))
            throw new InvalidDataException("Landmark save checksum mismatch.");
        var book = new WaypointBook();
        for (int slot = 0; slot < WaypointBook.SlotCount; slot++)
        {
            if (!TryDecodePosition(lines[slot + 4],
                    out Vector3? position))
                throw new InvalidDataException("Invalid saved landmark coordinate.");
            if (position is Vector3 p && !book.Mark(slot, p))
                throw new InvalidDataException("Invalid saved landmark.");
        }
        if (!book.Track(tracked))
            throw new InvalidDataException("Tracked landmark is missing.");
        return book;
    }
}
