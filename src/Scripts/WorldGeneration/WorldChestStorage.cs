using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace WorldGeneration;

/// <summary>Immutable 27-slot inventory owned by one chest voxel.</summary>
public sealed class ChestContents
{
    public const int SlotCount = 27;
    private readonly ItemStack[] _slots;

    public ChestContents() => _slots = new ItemStack[SlotCount];
    private ChestContents(ItemStack[] slots) => _slots = slots;

    public ItemStack Slot(int index) =>
        index is >= 0 and < SlotCount ? _slots[index] : default;

    public bool IsEmpty => _slots.All(s => s.IsEmpty);

    public string Serialize()
    {
        string[] fields = new string[SlotCount];
        for (int i = 0; i < SlotCount; i++)
        {
            ItemStack item = _slots[i];
            fields[i] = item.IsEmpty ? "0" : item.Type + ":" + item.Quantity;
        }
        return "1|" + string.Join(",", fields);
    }

    public static bool TryLoad(string? encoded, out ChestContents contents)
    {
        contents = new ChestContents();
        if (string.IsNullOrEmpty(encoded) || encoded.Length > 270 ||
            !encoded.StartsWith("1|", StringComparison.Ordinal))
            return false;
        string[] entries = encoded[2..].Split(',');
        if (entries.Length != SlotCount) return false;
        var slots = new ItemStack[SlotCount];
        for (int i = 0; i < SlotCount; i++)
        {
            if (entries[i] == "0") continue;
            string[] values = entries[i].Split(':');
            if (values.Length != 2 ||
                !int.TryParse(values[0], out int type) ||
                !int.TryParse(values[1], out int count))
                return false;
            slots[i] = new(type,count);
        }
        return TryCreate(slots, out contents);
    }

    public bool TryMove(int from, int to, bool split,
        out ChestContents next)
    {
        next = this;
        if (from is < 0 or >= SlotCount || to is < 0 or >= SlotCount ||
            from == to || _slots[from].IsEmpty) return false;
        ItemStack src = _slots[from];
        ItemStack dest = _slots[to];
        if (!dest.IsEmpty && dest.Type != src.Type)
        {
            if (split) return false;
            var swapped = (ItemStack[])_slots.Clone();
            swapped[to] = src;
            swapped[from] = dest;
            next = new ChestContents(swapped);
            return true;
        }
        if (!TryDeposit(to, src, split,
                out ChestContents deposited, out int moved))
            return false;
        var copy = (ItemStack[])deposited._slots.Clone();
        copy[from] = src.Quantity == moved ? default :
            new(src.Type, src.Quantity-moved);
        next = new ChestContents(copy);
        return true;
    }

    public bool TryDeposit(int chestSlot, ItemStack offered, bool split,
        out ChestContents next, out int moved)
    {
        next = this;
        moved = 0;
        if (chestSlot is < 0 or >= SlotCount ||
            offered.Type is < 1 or > BlockInventory.MaxType ||
            offered.Quantity is < 1 or > BlockInventory.StackLimit)
            return false;
        ItemStack current = _slots[chestSlot];
        if (!current.IsEmpty && current.Type != offered.Type) return false;
        int amount = split ? (offered.Quantity + 1) / 2 : offered.Quantity;
        moved = Math.Min(amount, BlockInventory.StackLimit - current.Quantity);
        if (moved <= 0) return false;
        ItemStack[] copy = (ItemStack[])_slots.Clone();
        copy[chestSlot] = new(offered.Type, current.Quantity + moved);
        next = new ChestContents(copy);
        return true;
    }

    public bool TryWithdraw(int chestSlot, bool split,
        out ChestContents next, out ItemStack taken)
    {
        next = this;
        taken = default;
        ItemStack current = Slot(chestSlot);
        if (current.IsEmpty) return false;
        int amount = split ? (current.Quantity + 1) / 2 : current.Quantity;
        taken = new(current.Type, amount);
        ItemStack[] copy = (ItemStack[])_slots.Clone();
        copy[chestSlot] = current.Quantity == amount ? default :
            new(current.Type, current.Quantity - amount);
        next = new ChestContents(copy);
        return true;
    }

    internal static bool TryCreate(ItemStack[] slots, out ChestContents contents)
    {
        contents = new ChestContents();
        if (slots.Length != SlotCount) return false;
        foreach (ItemStack s in slots)
            if ((s.IsEmpty && s.Quantity != 0) ||
                (!s.IsEmpty && (s.Type is < 1 or > BlockInventory.MaxType ||
                    s.Quantity is < 1 or > BlockInventory.StackLimit)))
                return false;
        contents = new ChestContents((ItemStack[])slots.Clone());
        return true;
    }
}

/// <summary>
/// Copy-on-write sparse chest state. The snapshot handed to the background
/// save worker is immutable. An empty chest needs no entry: its placed block
/// is still present in the voxel edit snapshot.
/// </summary>
public sealed class WorldChestStorage
{
    public const int MaxNonemptyChests = 4096;
    private Dictionary<BlockPosition, ChestContents> _entries = new();
    public IReadOnlyDictionary<BlockPosition, ChestContents> Snapshot => _entries;
    public int Count => _entries.Count;

    public ChestContents At(BlockPosition position) =>
        _entries.TryGetValue(position, out ChestContents? chest) ?
            chest : new ChestContents();

    public bool IsEmpty(BlockPosition position) =>
        !_entries.TryGetValue(position, out ChestContents? chest) || chest.IsEmpty;

    private bool Replace(BlockPosition pos, ChestContents contents)
    {
        if (!_entries.ContainsKey(pos) && !contents.IsEmpty &&
            _entries.Count >= MaxNonemptyChests) return false;
        var copy = new Dictionary<BlockPosition, ChestContents>(_entries);
        if (contents.IsEmpty) copy.Remove(pos);
        else copy[pos] = contents;
        _entries = copy;
        return true;
    }

    public bool TryTransfer(BlockPosition position, BlockInventory player,
        int playerSlot, int chestSlot, bool fromPlayer, bool split)
    {
        if (playerSlot is < 0 or >= BlockInventory.SlotCount ||
            chestSlot is < 0 or >= ChestContents.SlotCount)
            return false;
        ChestContents current = At(position);
        if (fromPlayer)
        {
            ItemStack offered = player.Slot(playerSlot);
            if (!current.TryDeposit(chestSlot, offered, split,
                    out ChestContents after, out int moved) ||
                (!Snapshot.ContainsKey(position) && !after.IsEmpty &&
                    Count >= MaxNonemptyChests))
                return false;
            if (!player.TryRemoveFromSlot(playerSlot, moved)) return false;
            return Replace(position, after);
        }

        if (!current.TryWithdraw(chestSlot, split,
                out ChestContents withdrawal, out ItemStack received) ||
            !player.CanInsertIntoSlot(playerSlot, received))
            return false;
        if (!player.TryInsertIntoSlot(playerSlot, received)) return false;
        return Replace(position, withdrawal);
    }

    public bool TryRearrange(BlockPosition position, int from, int to,
        bool split)
    {
        ChestContents old = At(position);
        return old.TryMove(from,to,split,out ChestContents next) &&
            Replace(position,next);
    }

    public bool Remove(BlockPosition position)
    {
        if (!_entries.ContainsKey(position)) return true;
        var copy = new Dictionary<BlockPosition, ChestContents>(_entries);
        copy.Remove(position);
        _entries = copy;
        return true;
    }

    public void Restore(IReadOnlyDictionary<BlockPosition, ChestContents> contents)
    {
        if (contents.Count > MaxNonemptyChests)
            throw new InvalidDataException("Too many saved chests.");
        _entries = new Dictionary<BlockPosition, ChestContents>(contents);
    }
}

/// <summary>Bounded, deterministic binary encoding for chest world saves.</summary>
public static class ChestSaveCodec
{
    public const int MaxBytes =
        8 + WorldChestStorage.MaxNonemptyChests * (12 + ChestContents.SlotCount * 2);

    public static byte[] Encode(IReadOnlyDictionary<BlockPosition, ChestContents> data)
    {
        if (data.Count > WorldChestStorage.MaxNonemptyChests)
            throw new InvalidDataException("Too many chests.");
        using var output = new MemoryStream();
        using (var w = new BinaryWriter(output, System.Text.Encoding.UTF8, true))
        {
            w.Write(new byte[] { (byte)'T', (byte)'C', (byte)'H', (byte)'1' });
            w.Write(data.Count);
            var positions = new List<BlockPosition>(data.Keys);
            positions.Sort((a,b) => a.X != b.X ? a.X.CompareTo(b.X) :
                a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Z.CompareTo(b.Z));
            foreach (BlockPosition p in positions)
            {
                ChestContents chest = data[p];
                if (chest.IsEmpty)
                    throw new InvalidDataException("Empty chests must be omitted.");
                w.Write(p.X); w.Write(p.Y); w.Write(p.Z);
                for (int i = 0; i < ChestContents.SlotCount; i++)
                {
                    ItemStack item = chest.Slot(i);
                    w.Write((byte)item.Type);
                    w.Write((byte)item.Quantity);
                }
            }
        }
        return output.ToArray();
    }

    public static bool TryDecode(byte[] bytes,
        out Dictionary<BlockPosition, ChestContents> chests)
    {
        chests = new();
        if (bytes.Length is < 8 or > MaxBytes) return false;
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var r = new BinaryReader(input);
            if (r.ReadByte() != 'T' || r.ReadByte() != 'C' ||
                r.ReadByte() != 'H' || r.ReadByte() != '1')
                return false;
            int count = r.ReadInt32();
            if (count < 0 || count > WorldChestStorage.MaxNonemptyChests ||
                bytes.Length != 8 + count * (12 + ChestContents.SlotCount * 2))
                return false;
            var parsed = new Dictionary<BlockPosition, ChestContents>();
            for (int c = 0; c < count; c++)
            {
                var position = new BlockPosition(r.ReadInt32(),
                    r.ReadInt32(),r.ReadInt32());
                var slots = new ItemStack[ChestContents.SlotCount];
                for (int i = 0; i < slots.Length; i++)
                    slots[i] = new(r.ReadByte(),r.ReadByte());
                if (!ChestContents.TryCreate(slots, out ChestContents chest) ||
                    chest.IsEmpty || !parsed.TryAdd(position, chest))
                    return false;
            }
            chests = parsed;
            return true;
        }
        catch (EndOfStreamException) { return false; }
        catch (IOException) { return false; }
    }
}
