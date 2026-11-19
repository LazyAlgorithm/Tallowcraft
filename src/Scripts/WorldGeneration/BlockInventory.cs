using System;
using System.Globalization;

namespace WorldGeneration;

/// <summary>
/// Small, server-owned player material inventory. Inventory items are block
/// TYPES (not oriented/partial block states): one successful edit awards or
/// spends one item. The synced representation is deliberately bounded and
/// validated before clients use it for the hotbar.
/// </summary>
public sealed class BlockInventory
{
    public const int MaxType = 9;
    public const int MaxPerType = 999;
    private readonly int[] _counts = new int[MaxType + 1];

    public int Count(int type) =>
        type is >= 1 and <= MaxType ? _counts[type] : 0;

    public void Clear() => Array.Clear(_counts);

    /// <summary>Starter building supplies; ores and leaves must be mined.</summary>
    public void FillStarter()
    {
        Clear();
        _counts[1] = 32; // grass
        _counts[2] = 64; // dirt
        _counts[3] = 64; // stone
        _counts[4] = 32; // logs
        _counts[5] = 8;  // forges
        _counts[6] = 64; // planks
    }

    public bool CanSpend(int type, int quantity = 1) =>
        type is >= 1 and <= MaxType &&
        quantity > 0 && quantity <= Count(type);

    public bool TrySpend(int type, int quantity = 1)
    {
        if (!CanSpend(type, quantity))
            return false;
        _counts[type] -= quantity;
        return true;
    }

    public bool TryCollect(int type, int quantity = 1)
    {
        if (type is < 1 or > MaxType || quantity <= 0 ||
            quantity > MaxPerType - _counts[type])
            return false;
        _counts[type] += quantity;
        return true;
    }

    public string Serialize() =>
        "1|" + string.Join(",", _counts[1..]);

    /// <summary>
    /// Parse atomically. Invalid/stale data never partially changes inventory.
    /// </summary>
    public bool TryLoad(string? serialized)
    {
        if (string.IsNullOrEmpty(serialized) || serialized.Length > 60 ||
            !serialized.StartsWith("1|", StringComparison.Ordinal))
            return false;
        string[] parts = serialized[2..].Split(',');
        if (parts.Length != MaxType)
            return false;

        var next = new int[MaxType + 1];
        for (int type = 1; type <= MaxType; type++)
        {
            if (!int.TryParse(parts[type - 1], NumberStyles.None,
                CultureInfo.InvariantCulture, out int count) ||
                count < 0 || count > MaxPerType)
                return false;
            next[type] = count;
        }

        Array.Copy(next, _counts, _counts.Length);
        return true;
    }
}
