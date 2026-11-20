using System;
using System.Globalization;

namespace WorldGeneration;

/// <summary>One concrete item stack. Type 10 is an ingot; other valid
/// types are placeable blocks. Empty slots have both fields zero.</summary>
public readonly record struct ItemStack(int Type, int Quantity)
{
    public bool IsEmpty => Type == 0;
}

/// <summary>
/// Server-owned 9-slot hotbar + 27-slot backpack. New items stack to 64.
/// Old v1/v2 profiles allowed up to 999 of each type: migration retains
/// anything that doesn't fit in a separate per-type reserve, making all old
/// resources spendable instead of silently dropping them.
/// </summary>
public sealed class BlockInventory
{
    public const int MaxType = 15;
    public const int Cobblestone = 12;
    public const int StoneBricks = 13;
    public const int MossyStone = 14;
    public const int DarkTimber = 15;
    public const int Chest = 11;
    public const int IronIngot = 10;
    public const int MaxPerType = 999;
    public const int StackLimit = 64;
    public const int HotbarSlots = 9;
    public const int SlotCount = 36;

    private readonly ItemStack[] _slots = new ItemStack[SlotCount];
    private readonly int[] _reserve = new int[MaxType + 1];

    public ItemStack Slot(int slot) =>
        slot is >= 0 and < SlotCount ? _slots[slot] : default;

    public int Reserve(int type) =>
        type is >= 1 and <= MaxType ? _reserve[type] : 0;

    public int Count(int type)
    {
        if (type is < 1 or > MaxType) return 0;
        int count = _reserve[type];
        foreach (ItemStack stack in _slots)
            if (stack.Type == type) count += stack.Quantity;
        return count;
    }

    public void Clear()
    {
        Array.Clear(_slots);
        Array.Clear(_reserve);
    }

    /// <summary>Keep the six familiar starter materials in hotbar slots.</summary>
    public void FillStarter()
    {
        Clear();
        _slots[0] = new(1, 32);
        _slots[1] = new(2, 64);
        _slots[2] = new(3, 64);
        _slots[3] = new(4, 32);
        _slots[4] = new(5, 8);
        _slots[5] = new(6, 64);
    }

    public bool CanSpend(int type, int quantity = 1) =>
        type is >= 1 and <= MaxType &&
        quantity > 0 && quantity <= Count(type);

    private void AddUnchecked(int type, int quantity)
    {
        for (int slot = 0; slot < SlotCount && quantity > 0; slot++)
        {
            if (_slots[slot].Type != type) continue;
            int space = StackLimit - _slots[slot].Quantity;
            int take = Math.Min(space, quantity);
            if (take > 0)
            {
                _slots[slot] = new(type, _slots[slot].Quantity + take);
                quantity -= take;
            }
        }
        for (int slot = 0; slot < SlotCount && quantity > 0; slot++)
        {
            if (!_slots[slot].IsEmpty) continue;
            int take = Math.Min(StackLimit, quantity);
            _slots[slot] = new(type, take);
            quantity -= take;
        }
        // This preserves *all* previously saved items, including profiles
        // with ten separate stacks of 999 from the earlier counter system.
        _reserve[type] += quantity;
    }

    /// <summary>Placing from the selected hotbar always consumes that
    /// *physical* stack, never some unrelated backpack stack.</summary>
    public bool TrySpendSlot(int slot, int quantity = 1)
    {
        if (slot is < 0 or >= HotbarSlots || quantity <= 0 ||
            _slots[slot].IsEmpty || quantity > _slots[slot].Quantity)
            return false;
        ItemStack stack = _slots[slot];
        int remain = stack.Quantity - quantity;
        _slots[slot] = remain == 0 ? default : new(stack.Type, remain);
        return true;
    }

    public bool CanInsertIntoSlot(int slot, ItemStack item)
    {
        if (slot is < 0 or >= SlotCount ||
            item.Type is < 1 or > MaxType ||
            item.Quantity is < 1 or > StackLimit ||
            item.Quantity > MaxPerType - Count(item.Type))
            return false;
        ItemStack current = _slots[slot];
        return current.IsEmpty || (current.Type == item.Type &&
            current.Quantity <= StackLimit - item.Quantity);
    }

    /// <summary>Exact-slot insertion without spilling into the legacy
    /// reserve. Used for atomic chest withdrawals.</summary>
    public bool TryInsertIntoSlot(int slot, ItemStack item)
    {
        if (!CanInsertIntoSlot(slot, item)) return false;
        ItemStack current = _slots[slot];
        _slots[slot] = new(item.Type, current.Quantity + item.Quantity);
        return true;
    }

    public bool TryRemoveFromSlot(int slot, int quantity)
    {
        if (slot is < 0 or >= SlotCount || quantity <= 0 ||
            _slots[slot].IsEmpty || quantity > _slots[slot].Quantity)
            return false;
        ItemStack old = _slots[slot];
        int remain = old.Quantity - quantity;
        _slots[slot] = remain > 0 ? new(old.Type, remain) : default;
        return true;
    }

    /// <summary>Harvest, crafting output and future pickups use this API.</summary>
    public bool TryCollect(int type, int quantity = 1)
    {
        if (type is < 1 or > MaxType || quantity <= 0 ||
            quantity > MaxPerType - Count(type)) return false;
        AddUnchecked(type, quantity);
        return true;
    }

    public bool TrySpend(int type, int quantity = 1)
    {
        if (!CanSpend(type, quantity)) return false;
        int reserved = Math.Min(_reserve[type], quantity);
        _reserve[type] -= reserved;
        quantity -= reserved;

        // Spend from backpack before hotbar so selected building blocks stay
        // visible until every other matching stack has been exhausted.
        for (int i = SlotCount - 1; i >= 0 && quantity > 0; i--)
        {
            if (_slots[i].Type != type) continue;
            int take = Math.Min(_slots[i].Quantity, quantity);
            int remaining = _slots[i].Quantity - take;
            _slots[i] = remaining == 0 ? default : new(type, remaining);
            quantity -= take;
        }
        return quantity == 0;
    }

    /// <summary>
    /// Move all or part of a physical stack, merging with a compatible stack.
    /// 0 means all. Swaps are only allowed for full-stack moves.
    /// </summary>
    public bool TryMove(int from, int to, int quantity = 0)
    {
        if (from is < 0 or >= SlotCount || to is < 0 or >= SlotCount ||
            from == to || _slots[from].IsEmpty || quantity < 0)
            return false;

        ItemStack source = _slots[from], destination = _slots[to];
        int moving = quantity == 0 ? source.Quantity : Math.Min(quantity, source.Quantity);
        if (destination.IsEmpty)
        {
            _slots[to] = new(source.Type, moving);
            _slots[from] = source.Quantity == moving ? default :
                new(source.Type, source.Quantity - moving);
            return true;
        }
        if (source.Type == destination.Type)
        {
            int count = Math.Min(moving, StackLimit - destination.Quantity);
            if (count <= 0) return false;
            _slots[to] = new(source.Type, destination.Quantity + count);
            _slots[from] = source.Quantity == count ? default :
                new(source.Type, source.Quantity - count);
            return true;
        }
        if (moving != source.Quantity) return false;
        _slots[from] = destination;
        _slots[to] = source;
        return true;
    }

    /// <summary>Right-drag moves half the source stack (rounded up).</summary>
    public bool TrySplit(int from, int to) =>
        from is >= 0 and < SlotCount && !_slots[from].IsEmpty &&
        TryMove(from, to, (_slots[from].Quantity + 1) / 2);

    /// <summary>Recover legacy excess into real slots as room becomes available.</summary>
    public bool RetrieveReserve(int type)
    {
        if (type is < 1 or > MaxType || _reserve[type] == 0) return false;
        int previous = _reserve[type];
        _reserve[type] = 0;
        AddUnchecked(type, previous);
        return _reserve[type] < previous;
    }

    public int FindHotbarType(int type)
    {
        for (int i = 0; i < HotbarSlots; i++)
            if (_slots[i].Type == type) return i;
        return -1;
    }

    public bool CanCraft(int recipeId)
    {
        CraftingRecipe? recipe = CraftingRecipes.Find(recipeId);
        if (recipe == null) return false;
        int requiredA = recipe.CountA +
            (recipe.InputB == recipe.InputA ? recipe.CountB : 0);
        if (!CanSpend(recipe.InputA, requiredA) ||
            (recipe.InputB != 0 && recipe.InputB != recipe.InputA &&
             !CanSpend(recipe.InputB, recipe.CountB)))
            return false;

        int outputBase = Count(recipe.Output);
        if (recipe.Output == recipe.InputA) outputBase -= recipe.CountA;
        if (recipe.Output == recipe.InputB) outputBase -= recipe.CountB;
        return outputBase >= 0 && recipe.CountOut <= MaxPerType - outputBase;
    }

    public bool TryCraft(int recipeId)
    {
        if (!CanCraft(recipeId)) return false;
        CraftingRecipe recipe = CraftingRecipes.Find(recipeId)!;
        if (!TrySpend(recipe.InputA, recipe.CountA)) return false;
        if (recipe.InputB != 0 && !TrySpend(recipe.InputB, recipe.CountB))
        {
            TryCollect(recipe.InputA, recipe.CountA);
            return false;
        }
        return TryCollect(recipe.Output, recipe.CountOut);
    }

    /// <summary>
    /// v4 lists 36 real stacks and per-type reserves. Legacy saves with
    /// only 11 reserve fields are padded with zeros for the four new types.
    /// The payload stays under 700 characters, well inside the sync bridge.
    /// </summary>
    public string Serialize()
    {
        var slots = new string[SlotCount];
        for (int i = 0; i < SlotCount; i++)
            slots[i] = _slots[i].IsEmpty ? "0" :
                _slots[i].Type.ToString(CultureInfo.InvariantCulture) + ":" +
                _slots[i].Quantity.ToString(CultureInfo.InvariantCulture);
        return "4|" + string.Join(',', slots) + "|" +
            string.Join(',', _reserve[1..]);
    }

    public bool TryLoad(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded) || encoded.Length > 900) return false;
        if (encoded.StartsWith("1|", StringComparison.Ordinal) ||
            encoded.StartsWith("2|", StringComparison.Ordinal))
        {
            bool old = encoded[0] == '1';
            string[] counts = encoded[2..].Split(',');
            if (counts.Length != (old ? 9 : 10)) return false;
            var parsed = new int[MaxType + 1];
            for (int type = 1; type <= counts.Length; type++)
                if (!int.TryParse(counts[type - 1], NumberStyles.None,
                    CultureInfo.InvariantCulture, out parsed[type]) ||
                    parsed[type] is < 0 or > MaxPerType)
                    return false;
            var staged = new BlockInventory();
            for (int type = 1; type <= MaxType; type++)
                if (parsed[type] != 0)
                    staged.AddUnchecked(type, parsed[type]);
            Array.Copy(staged._slots, _slots, SlotCount);
            Array.Copy(staged._reserve, _reserve, MaxType + 1);
            return true;
        }
        bool legacySlots = encoded.StartsWith("3|", StringComparison.Ordinal);
        if (!legacySlots &&
            !encoded.StartsWith("4|", StringComparison.Ordinal)) return false;
        string[] fields = encoded.Split('|');
        if (fields.Length != 3) return false;
        string[] items = fields[1].Split(',');
        string[] reserve = fields[2].Split(',');
        if (items.Length != SlotCount ||
            reserve.Length != (legacySlots ? 10 : MaxType) &&
            !( !legacySlots && reserve.Length == 11))
            return false;

        var next = new ItemStack[SlotCount];
        var extras = new int[MaxType + 1];
        var totals = new int[MaxType + 1];
        for (int i = 0; i < SlotCount; i++)
        {
            if (items[i] == "0") continue;
            string[] pair = items[i].Split(':');
            if (pair.Length != 2 ||
                !int.TryParse(pair[0], NumberStyles.None,
                    CultureInfo.InvariantCulture, out int type) ||
                !int.TryParse(pair[1], NumberStyles.None,
                    CultureInfo.InvariantCulture, out int amount) ||
                type is < 1 or > MaxType || amount is < 1 or > StackLimit)
                return false;
            next[i] = new(type, amount);
            totals[type] += amount;
        }
        for (int type = 1; type <= reserve.Length; type++)
        {
            if (!int.TryParse(reserve[type - 1], NumberStyles.None,
                CultureInfo.InvariantCulture, out extras[type]) ||
                extras[type] is < 0 or > MaxPerType ||
                totals[type] + extras[type] > MaxPerType)
                return false;
        }
        Array.Copy(next, _slots, SlotCount);
        Array.Copy(extras, _reserve, MaxType + 1);
        return true;
    }
}
