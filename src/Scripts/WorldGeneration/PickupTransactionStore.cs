using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WorldGeneration;

/// <summary>
/// Durable redo record for one server-authorized ground-item pickup.
/// While it exists, the entire post-pickup drop list and the receiving
/// player's post-pickup profile are the authoritative recovery target.
/// </summary>
public sealed record PickupReceipt(
    string WorldId, string PlayerToken, BlockInventory Inventory,
    PlayerTools Tools, int SelectedType, int SelectedShape,
    int Facing, int SelectedSlot, IReadOnlyList<SavedWorldDrop> Drops);

public static class PickupTransactionStore
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private const int MaxJournalBytes = 16384;
    private const string JournalName = "pending-pickup.txn";

    private static string JournalPath(string root) =>
        Path.Combine(root, JournalName);

    private static void Validate(PickupReceipt receipt)
    {
        if (!PlayerIdentity.Valid(receipt.WorldId) ||
            !PlayerIdentity.Valid(receipt.PlayerToken) ||
            receipt.SelectedType is < 0 or > BlockInventory.Chest ||
            receipt.SelectedShape is < 0 or > 3 ||
            receipt.Facing is < 0 or > 3 ||
            receipt.SelectedSlot is < 0 or >= BlockInventory.HotbarSlots)
            throw new InvalidDataException("Invalid pickup identity or player selection.");
        // Validate bounded, immutable snapshots before writing any journal.
        var inventory = new BlockInventory();
        var tools = new PlayerTools();
        if (!inventory.TryLoad(receipt.Inventory.Serialize()) ||
            !tools.TryLoad(receipt.Tools.Serialize()))
            throw new InvalidDataException("Invalid pickup profile.");
        WorldDropSaveCodec.Encode(receipt.Drops);
    }

    private static string Encode(PickupReceipt receipt)
    {
        Validate(receipt);
        string payload = string.Join(";", "1", receipt.WorldId,
            receipt.PlayerToken, receipt.Inventory.Serialize(),
            receipt.Tools.Serialize(),
            receipt.SelectedType.ToString(Invariant),
            receipt.SelectedShape.ToString(Invariant),
            receipt.Facing.ToString(Invariant),
            receipt.SelectedSlot.ToString(Invariant),
            Convert.ToBase64String(WorldDropSaveCodec.Encode(receipt.Drops)));
        string hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return hash + "\n" + payload + "\n";
    }

    public static void Prepare(string root, PickupReceipt receipt)
    {
        string encoded = Encode(receipt);
        byte[] bytes = Encoding.UTF8.GetBytes(encoded);
        if (bytes.Length > MaxJournalBytes)
            throw new InvalidDataException("Pickup journal exceeds size limit.");
        Directory.CreateDirectory(root);
        string path = JournalPath(root);
        if (File.Exists(path))
            throw new InvalidOperationException(
                "Previous pickup must recover before accepting another.");
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                try { stream.Flush(flushToDisk: true); }
                catch (PlatformNotSupportedException) { stream.Flush(); }
            }
            File.Move(pending, path); // Never overwrite an uncommitted receipt.
        }
        finally
        {
            if (File.Exists(pending))
                File.Delete(pending);
        }
    }

    /// <summary>Absence is safe; a present but invalid receipt is fatal.
    /// Never silently discard a pickup that may already have committed.</summary>
    public static bool TryRead(string root, string expectedWorldId,
        out PickupReceipt? receipt)
    {
        receipt = null;
        string path = JournalPath(root);
        if (!File.Exists(path))
            return false;
        var info = new FileInfo(path);
        if (info.Length < 70 || info.Length > MaxJournalBytes)
            throw new InvalidDataException("Pickup receipt has invalid size.");
        string[] lines = File.ReadAllLines(path);
        if (lines.Length != 2 || lines[0].Length != 64)
            throw new InvalidDataException("Pickup receipt header is invalid.");
        string actualHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(lines[1]))).ToLowerInvariant();
        if (!string.Equals(actualHash, lines[0], StringComparison.Ordinal))
            throw new InvalidDataException("Pickup receipt checksum mismatch.");
        string[] fields = lines[1].Split(';');
        if (fields.Length != 10 || fields[0] != "1" ||
            fields[1] != expectedWorldId ||
            !PlayerIdentity.Valid(fields[1]) ||
            !PlayerIdentity.Valid(fields[2]) ||
            !int.TryParse(fields[5], NumberStyles.None, Invariant, out int type) ||
            !int.TryParse(fields[6], NumberStyles.None, Invariant, out int shape) ||
            !int.TryParse(fields[7], NumberStyles.None, Invariant, out int facing) ||
            !int.TryParse(fields[8], NumberStyles.None, Invariant, out int slot))
            throw new InvalidDataException("Pickup receipt fields are invalid.");
        var inventory = new BlockInventory();
        var tools = new PlayerTools();
        if (!inventory.TryLoad(fields[3]) || !tools.TryLoad(fields[4]))
            throw new InvalidDataException("Pickup receipt profile is invalid.");
        byte[] dropsData;
        try { dropsData = Convert.FromBase64String(fields[9]); }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Pickup receipt drop data is invalid.", ex);
        }
        if (!WorldDropSaveCodec.TryDecode(dropsData, out var drops))
            throw new InvalidDataException("Pickup receipt drop snapshot is invalid.");
        receipt = new PickupReceipt(fields[1], fields[2], inventory, tools,
            type, shape, facing, slot, drops);
        Validate(receipt);
        return true;
    }

    /// <summary>
    /// Redo a pickup interrupted between world/profile writes. Repeating
    /// recovery is safe: the receipt contains complete replacement snapshots.
    /// Both durable saves finish before the receipt may be deleted.
    /// </summary>
    public static bool Recover(string root, string worldId, int seed,
        SectionedEditSnapshot edits,
        IReadOnlyDictionary<ChunkKey, int> revisions,
        IReadOnlyDictionary<BlockPosition, ChestContents> chests,
        out IReadOnlyList<SavedWorldDrop> drops)
    {
        drops = Array.Empty<SavedWorldDrop>();
        if (!TryRead(root, worldId, out PickupReceipt? receipt))
            return false;
        if (receipt == null)
            throw new InvalidDataException("Missing committed pickup payload.");
        WorldEditSave.Save(root, worldId, seed, edits, revisions,
            chests, receipt.Drops);
        PlayerProfileStore.Save(Path.Combine(root, "players", worldId),
            worldId, receipt.PlayerToken, receipt.Inventory, receipt.Tools,
            receipt.SelectedType, receipt.SelectedShape, receipt.Facing,
            receipt.SelectedSlot);
        Complete(root);
        drops = receipt.Drops;
        return true;
    }

    public static bool HasPending(string root) =>
        File.Exists(JournalPath(root));

    public static void Complete(string root)
    {
        string path = JournalPath(root);
        if (!File.Exists(path))
            throw new InvalidDataException("Committed pickup journal disappeared.");
        File.Delete(path);
    }
}
