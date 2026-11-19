using System;
using System.Globalization;

namespace WorldGeneration;

/// <summary>
/// Server-owned equipment, separate from voxel material stacks. Each tool
/// occupies one slot, has finite durability, and can be repaired by crafting
/// another copy using the same recipe. The client receives a compact validated
/// snapshot; only the server crafts, equips and spends durability.
/// </summary>
public sealed class PlayerTools
{
    private readonly int[] _durability = new int[7];
    public MiningTool Equipped { get; private set; } = MiningTool.Hand;

    public static int MaxDurability(MiningTool tool) => tool switch
    {
        MiningTool.Pickaxe => 80,
        MiningTool.Axe => 72,
        MiningTool.Shovel => 64,
        MiningTool.IronPickaxe => 250,
        MiningTool.IronAxe => 220,
        MiningTool.IronShovel => 190,
        _ => 0
    };

    public static string Name(MiningTool tool) => tool switch
    {
        MiningTool.Pickaxe => "Stone pickaxe",
        MiningTool.Axe => "Stone axe",
        MiningTool.Shovel => "Stone shovel",
        MiningTool.IronPickaxe => "Iron pickaxe",
        MiningTool.IronAxe => "Iron axe",
        MiningTool.IronShovel => "Iron shovel",
        _ => "Hand"
    };

    /// <summary>
    /// Stone tools use stone + planks, while iron tools require smelted
    /// iron ingots + planks. Iron ingots are inventory items, not voxels.
    /// </summary>
    public static bool Recipe(MiningTool tool,
        out int inputA, out int countA, out int inputB, out int countB)
    {
        inputA = tool is >= MiningTool.IronPickaxe and <= MiningTool.IronShovel
            ? BlockInventory.IronIngot : 3; // stone
        inputB = 6; // planks
        countB = 2;
        countA = tool switch
        {
            MiningTool.Pickaxe or MiningTool.IronPickaxe => 3,
            MiningTool.Axe or MiningTool.IronAxe => 2,
            MiningTool.Shovel or MiningTool.IronShovel => 1,
            _ => 0
        };
        return countA > 0;
    }

    public int Durability(MiningTool tool) =>
        tool is >= MiningTool.Pickaxe and <= MiningTool.IronShovel
            ? _durability[(int)tool] : 0;

    public bool CanCraft(BlockInventory materials, MiningTool tool)
    {
        if (!Recipe(tool, out int a, out int countA,
                out int b, out int countB) ||
            Durability(tool) == MaxDurability(tool))
            return false;
        return materials.CanSpend(a, countA) &&
            materials.CanSpend(b, countB);
    }

    public bool TryCraft(BlockInventory materials, MiningTool tool)
    {
        if (!CanCraft(materials, tool))
            return false;

        // One server tick owns this transaction. The two costs are
        // prevalidated, so there is no possible partial spend.
        Recipe(tool, out int a, out int countA, out int b, out int countB);
        if (!materials.TrySpend(a, countA))
            return false;
        if (!materials.TrySpend(b, countB))
        {
            // Defensive rollback if the recipe system changes later.
            materials.TryCollect(a, countA);
            return false;
        }

        _durability[(int)tool] = MaxDurability(tool);
        return true;
    }

    public bool TryEquip(MiningTool tool)
    {
        if (tool != MiningTool.Hand && Durability(tool) == 0)
            return false;
        if (tool is < MiningTool.Hand or > MiningTool.IronShovel)
            return false;
        Equipped = tool;
        return true;
    }

    /// <summary>Use the stronger iron version on quick-equip hotkeys
    /// 7/8/9 when available, falling back to the crafted stone version.</summary>
    public bool TryEquipSlot(int slot)
    {
        if (slot == 0)
            return TryEquip(MiningTool.Hand);
        if (slot is < 1 or > 3)
            return false;
        MiningTool upgraded = (MiningTool)(slot + 3);
        MiningTool basic = (MiningTool)slot;
        return Durability(upgraded) > 0 ? TryEquip(upgraded) :
            Durability(basic) > 0 && TryEquip(basic);
    }

    /// <summary>One successful block break consumes one point of tool life.</summary>
    public bool OnBlockMined()
    {
        if (Equipped == MiningTool.Hand)
            return false;
        int type = (int)Equipped;
        if (_durability[type] <= 0)
        {
            Equipped = MiningTool.Hand;
            return true;
        }
        _durability[type]--;
        if (_durability[type] == 0)
            Equipped = MiningTool.Hand;
        return true;
    }

    public string Serialize() =>
        "2|" + string.Join(",", _durability[1..]) +
        "|" + ((int)Equipped).ToString(CultureInfo.InvariantCulture);

    /// <summary>Malformed or forged network data changes nothing.</summary>
    public bool TryLoad(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded) || encoded.Length > 50)
            return false;
        string[] sections = encoded.Split('|');
        if (sections.Length != 3 ||
            (sections[0] != "1" && sections[0] != "2"))
            return false;
        bool legacy = sections[0] == "1";
        string[] fields = sections[1].Split(',');
        if (fields.Length != (legacy ? 3 : 6))
            return false;

        int[] next = new int[7];
        for (int i = 1; i <= fields.Length; i++)
        {
            if (!int.TryParse(fields[i - 1], NumberStyles.None,
                    CultureInfo.InvariantCulture, out int value) ||
                value < 0 || value > MaxDurability((MiningTool)i))
                return false;
            next[i] = value;
        }

        if (!int.TryParse(sections[2], NumberStyles.None,
                CultureInfo.InvariantCulture, out int selected) ||
            selected < 0 || selected > (legacy ? 3 : 6) ||
            (selected != 0 && next[selected] == 0))
            return false;

        Array.Copy(next, _durability, _durability.Length);
        Equipped = (MiningTool)selected;
        return true;
    }
}
