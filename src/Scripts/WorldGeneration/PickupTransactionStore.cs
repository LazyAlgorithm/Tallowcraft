using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WorldGeneration;

/// <summary>
/// Durable redo record for one server-authorized inventory/world transfer.
/// Includes the complete post-transaction player state and drop list.
/// Chest transfers use a bounded optional changed-chest record, not the
/// full world chest snapshot, so journals stay small as worlds grow.
/// </summary>
public sealed record PickupReceipt(
    string WorldId, string PlayerToken, BlockInventory Inventory,
    PlayerTools Tools, int SelectedType, int SelectedShape,
    int Facing, int SelectedSlot, IReadOnlyList<SavedWorldDrop> Drops,
    BlockPosition? ChestPosition = null, ChestContents? ChestAfter = null,
    BlockPosition? EditedBlock = null, ushort BlockBefore = 0,
    ushort BlockAfter = 0);

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
            receipt.SelectedType is < 0 or > BlockInventory.MaxType ||
            receipt.SelectedShape is < 0 or > BlockState.MaxShape ||
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
        if (receipt.ChestPosition.HasValue != (receipt.ChestAfter != null))
            throw new InvalidDataException("Incomplete chest transaction fields.");
        if (receipt.ChestPosition is BlockPosition position &&
            !BlockEdits.ValidPosition(position))
            throw new InvalidDataException("Invalid journaled chest position.");
        if (receipt.ChestAfter != null &&
            !ChestContents.TryLoad(receipt.ChestAfter.Serialize(), out _))
            throw new InvalidDataException("Invalid journaled chest state.");
        // V3 represents only a mined block turning into air. Other edits
        // must use their own transaction semantics.
        if (receipt.EditedBlock is BlockPosition mined)
        {
            if (receipt.ChestPosition != null ||
                !BlockEdits.ValidPosition(mined) ||
                receipt.BlockBefore == 0 ||
                !BlockState.IsValid(receipt.BlockBefore) ||
                receipt.BlockAfter != 0)
                throw new InvalidDataException("Invalid mining reward receipt.");
        }
        else if (receipt.BlockBefore != 0 || receipt.BlockAfter != 0)
            throw new InvalidDataException("Mining edit is missing its position.");
    }

    private static string Encode(PickupReceipt receipt)
    {
        Validate(receipt);
        var fields = new List<string>
        {
            receipt.EditedBlock.HasValue ? "3" :
                receipt.ChestPosition.HasValue ? "2" : "1",
            receipt.WorldId,
            receipt.PlayerToken,
            receipt.Inventory.Serialize(),
            receipt.Tools.Serialize(),
            receipt.SelectedType.ToString(Invariant),
            receipt.SelectedShape.ToString(Invariant),
            receipt.Facing.ToString(Invariant),
            receipt.SelectedSlot.ToString(Invariant),
            Convert.ToBase64String(WorldDropSaveCodec.Encode(receipt.Drops))
        };
        if (receipt.ChestPosition is BlockPosition position &&
            receipt.ChestAfter is ChestContents chest)
        {
            fields.Add(string.Join(",", position.X.ToString(Invariant),
                position.Y.ToString(Invariant), position.Z.ToString(Invariant)));
            fields.Add(chest.Serialize());
        }
        if (receipt.EditedBlock is BlockPosition mined)
        {
            fields.Add(string.Join(",", mined.X.ToString(Invariant),
                mined.Y.ToString(Invariant), mined.Z.ToString(Invariant)));
            fields.Add(receipt.BlockBefore.ToString(Invariant));
            fields.Add(receipt.BlockAfter.ToString(Invariant));
        }
        string payload = string.Join(";", fields);
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
        bool dropOnly = fields.Length == 10 && fields[0] == "1";
        bool chestTransfer = fields.Length == 12 && fields[0] == "2";
        bool miningReward = fields.Length == 13 && fields[0] == "3";
        if ((!dropOnly && !chestTransfer && !miningReward) ||
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
        BlockPosition? chestPosition = null;
        ChestContents? chestAfter = null;
        if (chestTransfer)
        {
            string[] parts = fields[10].Split(',');
            if (parts.Length != 3 ||
                !int.TryParse(parts[0], NumberStyles.Integer, Invariant,
                    out int x) ||
                !int.TryParse(parts[1], NumberStyles.Integer, Invariant,
                    out int y) ||
                !int.TryParse(parts[2], NumberStyles.Integer, Invariant,
                    out int z) ||
                !ChestContents.TryLoad(fields[11], out ChestContents decoded))
                throw new InvalidDataException("Invalid journaled chest transfer.");
            chestPosition = new BlockPosition(x, y, z);
            chestAfter = decoded;
        }
        BlockPosition? editedBlock = null;
        ushort blockBefore = 0, blockAfter = 0;
        if (miningReward)
        {
            string[] parts = fields[10].Split(',');
            if (parts.Length != 3 ||
                !int.TryParse(parts[0], NumberStyles.Integer, Invariant,
                    out int x) ||
                !int.TryParse(parts[1], NumberStyles.Integer, Invariant,
                    out int y) ||
                !int.TryParse(parts[2], NumberStyles.Integer, Invariant,
                    out int z) ||
                !ushort.TryParse(fields[11], NumberStyles.None,
                    Invariant, out blockBefore) ||
                !ushort.TryParse(fields[12], NumberStyles.None,
                    Invariant, out blockAfter))
                throw new InvalidDataException("Invalid mined block receipt.");
            editedBlock = new BlockPosition(x, y, z);
        }
        receipt = new PickupReceipt(fields[1], fields[2], inventory, tools,
            type, shape, facing, slot, drops, chestPosition, chestAfter,
            editedBlock, blockBefore, blockAfter);
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
        out IReadOnlyList<SavedWorldDrop> drops) =>
        Recover(root, worldId, seed, edits, revisions, chests,
            out drops, out _, out _);

    public static bool Recover(string root, string worldId, int seed,
        SectionedEditSnapshot edits,
        IReadOnlyDictionary<ChunkKey, int> revisions,
        IReadOnlyDictionary<BlockPosition, ChestContents> chests,
        out IReadOnlyList<SavedWorldDrop> drops,
        out IReadOnlyDictionary<BlockPosition, ChestContents> restoredChests) =>
        Recover(root, worldId, seed, edits, revisions, chests,
            out drops, out restoredChests, out _);

    /// <summary>Replay an interrupted physical pickup, chest transfer or
    /// mined-block direct reward. The optional block edit is replayed only
    /// when the loaded save still contains the original block, and remains
    /// unchanged if the committed edit was already saved.</summary>
    public static bool Recover(string root, string worldId, int seed,
        SectionedEditSnapshot edits,
        IReadOnlyDictionary<ChunkKey, int> revisions,
        IReadOnlyDictionary<BlockPosition, ChestContents> chests,
        out IReadOnlyList<SavedWorldDrop> drops,
        out IReadOnlyDictionary<BlockPosition, ChestContents> restoredChests,
        out BlockEdits restoredEdits)
    {
        drops = Array.Empty<SavedWorldDrop>();
        restoredChests = chests;
        restoredEdits = new BlockEdits();
        if (!TryRead(root, worldId, out PickupReceipt? receipt))
            return false;
        if (receipt == null)
            throw new InvalidDataException("Missing committed inventory receipt.");

        foreach (ChunkKey key in revisions.Keys)
            restoredEdits.ReplaceSection(key, edits.InSection(key),
                revisions[key], seed);

        if (receipt.EditedBlock is BlockPosition mined)
        {
            ushort current = restoredEdits.Get(mined.X, mined.Y, mined.Z, seed);
            if (current == receipt.BlockBefore)
            {
                if (!restoredEdits.Set(mined, receipt.BlockAfter, seed))
                    throw new InvalidDataException(
                        "Unable to redo journaled mined voxel.");
            }
            else if (current != receipt.BlockAfter)
                throw new InvalidDataException(
                    "Mined voxel differs from both transaction states.");
        }

        if (receipt.ChestPosition is BlockPosition position &&
            receipt.ChestAfter is ChestContents after)
        {
            if (new BlockState(restoredEdits.Get(position.X, position.Y,
                    position.Z, seed)).Type != BlockInventory.Chest)
                throw new InvalidDataException(
                    "Journaled storage is no longer a chest voxel.");
            var updated = new Dictionary<BlockPosition, ChestContents>(chests);
            if (after.IsEmpty) updated.Remove(position);
            else updated[position] = after;
            if (updated.Count > WorldChestStorage.MaxNonemptyChests)
                throw new InvalidDataException("Journaled chest limit exceeded.");
            restoredChests = updated;
        }

        WorldEditSave.Save(root, worldId, seed,
            restoredEdits.Snapshot, restoredEdits.Revisions,
            restoredChests, receipt.Drops);
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
