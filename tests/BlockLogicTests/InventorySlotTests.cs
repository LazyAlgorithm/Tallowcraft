using System;
using WorldGeneration;

internal static class InventorySlotTests
{
    public static void Run(Action<bool,string> check)
    {
        var inventory = new BlockInventory();
        inventory.FillStarter();
        check(inventory.Slot(0) == new ItemStack(1,32) &&
            inventory.Slot(1) == new ItemStack(2,64) &&
            inventory.Slot(2) == new ItemStack(3,64) &&
            inventory.Slot(5) == new ItemStack(6,64) &&
            inventory.Slot(9).IsEmpty,
            "Starter resources occupy actual nine-slot hotbar stacks");

        check(inventory.TryMove(0,9) &&
            inventory.Slot(0).IsEmpty &&
            inventory.Slot(9) == new ItemStack(1,32),
            "Full drag moves a stack to the backpack");
        check(inventory.TryMove(1,0) &&
            inventory.Slot(0) == new ItemStack(2,64) &&
            inventory.Slot(1).IsEmpty,
            "A second stack can be moved into the vacated hotbar slot");
        check(inventory.TryMove(9,0) &&
            inventory.Slot(9) == new ItemStack(2,64) &&
            inventory.Slot(0) == new ItemStack(1,32),
            "Dragging onto a different item swaps both physical stacks");

        check(inventory.TrySplit(0,10) &&
            inventory.Slot(0) == new ItemStack(1,16) &&
            inventory.Slot(10) == new ItemStack(1,16),
            "Right-drag splits a stack into two equal physical stacks");
        check(inventory.TrySplit(0,10) &&
            inventory.Slot(0) == new ItemStack(1,8) &&
            inventory.Slot(10) == new ItemStack(1,24),
            "Another half drag merges into an existing compatible stack");
        check(!inventory.TryMove(0,9,3) &&
            inventory.Slot(0) == new ItemStack(1,8) &&
            inventory.Slot(9) == new ItemStack(2,64),
            "Partial moves cannot swap different item types");
        check(!inventory.TryMove(-1,0) &&
            !inventory.TryMove(0,36) &&
            !inventory.TryMove(0,0) &&
            !inventory.TryMove(0,9,-1),
            "Invalid slot requests cannot mutate inventory");

        check(inventory.TrySpendSlot(0,8) && inventory.Slot(0).IsEmpty &&
            inventory.Count(1) == 24,
            "Placing consumes the selected hotbar stack, not another stack");
        check(!inventory.TrySpendSlot(0) &&
            !inventory.TrySpendSlot(9) &&
            !inventory.TrySpendSlot(2,0),
            "Empty, backpack and zero-size placements are rejected");
        check(inventory.TrySpend(1,24) && inventory.Count(1) == 0,
            "Crafting can spend ingredients across separate physical stacks");

        string current = inventory.Serialize();
        var joined = new BlockInventory();
        check(current.StartsWith("4|", StringComparison.Ordinal) &&
            joined.TryLoad(current) &&
            joined.Serialize() == current &&
            joined.Slot(9) == inventory.Slot(9),
            "Slot locations replicate and roundtrip deterministically");

        const string old = "2|999,999,999,999,999,999,999,999,999,999";
        var migration = new BlockInventory();
        check(migration.TryLoad(old),
            "A full old v2 inventory migrates without overflow rejection");
        for (int i = 1; i <= 10; i++)
            check(migration.Count(i) == 999,
                "All 999 legacy items survive the migration");
        int spare = 0;
        for (int type = 1; type <= 10; type++) spare += migration.Reserve(type);
        check(spare > 0,
            "Excess legacy resources remain available in a lossless reserve");
        string saved = migration.Serialize();
        var recovered = new BlockInventory();
        check(recovered.TryLoad(saved) && recovered.Serialize() == saved,
            "The reserve and all 36 physical stacks survive save/reconnect");

        int typeWithReserve = 10;
        check(recovered.Reserve(typeWithReserve) > 0 &&
            recovered.TrySpend(typeWithReserve,10) &&
            recovered.Count(typeWithReserve) == 989,
            "Legacy excess remains spendable in crafting and tool recipes");

        // Free a slot, then manually unpack a previously hidden material.
        for (int slot = 0; slot < BlockInventory.SlotCount; slot++)
        {
            ItemStack value = recovered.Slot(slot);
            if (value.IsEmpty) continue;
            recovered.TrySpend(value.Type,value.Quantity);
            break;
        }
        check(recovered.RetrieveReserve(10) ||
            recovered.Reserve(10) == 0,
            "Overflow can move into free physical slots after consuming items");

        string baseline = joined.Serialize();
        foreach (string invalid in new[]
        {
            "4|0|0", "4|" + new string('0', 300),
            saved.Replace("4|","9|", StringComparison.Ordinal),
            saved.Replace("1:64","1:999", StringComparison.Ordinal),
            current.Replace("4|","4|x", StringComparison.Ordinal)
        })
        {
            check(!joined.TryLoad(invalid) &&
                joined.Serialize() == baseline,
                "Invalid or forged slot snapshots are rejected atomically");
        }

        Console.WriteLine("PASS: 36 physical item slots, split/swap/merge, " +
            "hotbar selection, lossless legacy reserves and replication");
    }
}
