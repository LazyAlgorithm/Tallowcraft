using System;
using WorldGeneration;

internal static class BlockPickerTests
{
    public static void Run(Action<bool, string> check)
    {
        var inventory = new BlockInventory();
        inventory.FillStarter();
        string before = inventory.Serialize();
        ushort stair = BlockState.Create(3, BlockShape.Stair, 3, true).Packed;

        check(BlockPicker.TryPick(inventory, stair, 0, false,
                out int selected, out bool changed) &&
            selected == 2 && !changed && inventory.Serialize() == before,
            "Middle-click selects an existing hotbar stack without moving items");

        check(inventory.TryMove(2, 9) && inventory.Slot(2).IsEmpty,
            "Prepare a matching stack in the backpack");
        int originalStone = inventory.Count(3);
        int originalGrass = inventory.Count(1);
        check(BlockPicker.TryPick(inventory, stair, 0, false,
                out selected, out changed) &&
            selected == 0 && changed &&
            inventory.Slot(0).Type == 3 &&
            inventory.Slot(9).Type == 1 &&
            inventory.Count(3) == originalStone &&
            inventory.Count(1) == originalGrass,
            "Picking a backpack block swaps its full stack into the active slot without loss");

        string unchanged = inventory.Serialize();
        check(!BlockPicker.TryPick(inventory,
                    BlockState.Create(9).Packed, 0, false,
                    out selected, out changed) &&
            inventory.Serialize() == unchanged && !changed,
            "Survival pick-block never creates an unavailable block");

        check(BlockPicker.TryPick(inventory,
                    BlockState.Create(9).Packed, 0, true,
                    out selected, out changed) &&
            selected == 2 && changed &&
            inventory.Slot(2).Type == 9 &&
            inventory.Slot(2).Quantity == 1,
            "Creative pick-block grants only one item in an empty hotbar slot");

        // Fill the remaining empty hotbar slots, then pick a new block;
        // the selected stack must move to an empty backpack slot instead
        // of being overwritten, even in creative mode.
        check(inventory.TryInsertIntoSlot(6, new ItemStack(7, 1)) &&
            inventory.TryInsertIntoSlot(7, new ItemStack(8, 1)) &&
            inventory.TryInsertIntoSlot(8, new ItemStack(4, 1)),
            "Prepare a full hotbar without modifying any other stack");
        int previousType = inventory.Slot(0).Type;
        int previousCount = inventory.Count(previousType);
        check(BlockPicker.TryPick(inventory,
                BlockState.Create(BlockInventory.Chest).Packed, 0, true,
                out selected, out changed) &&
            selected == 0 && changed &&
            inventory.Slot(0).Type == BlockInventory.Chest &&
            inventory.Count(previousType) == previousCount,
            "Creative pick-block preserves the displaced material when hotbar is full");

        var full = new BlockInventory();
        for (int slot = 0; slot < BlockInventory.SlotCount; slot++)
            check(full.TryInsertIntoSlot(slot, new ItemStack(slot % 9 + 1, 1)),
                "Prepare an occupied inventory slot");
        string fullBefore = full.Serialize();
        check(!BlockPicker.TryPick(full,
                    BlockState.Create(BlockInventory.Chest).Packed,
                    4, true, out _, out _) &&
            full.Serialize() == fullBefore,
            "Even Creative mode never deletes items from a full inventory");

        check(!BlockPicker.TryPick(inventory, 0, 0, true, out _, out _) &&
            !BlockPicker.TryPick(inventory, 0xffff, 0, true, out _, out _) &&
            !BlockPicker.TryPick(inventory, stair, -1, true, out _, out _) &&
            !BlockPicker.TryPick(inventory, stair, 9, true, out _, out _),
            "Air, malformed blocks and invalid active hotbar slots are rejected");

        BlockState copied = BlockPicker.SelectedState(3,
            (int)BlockShape.Stair, 3, true, true, false, 0);
        BlockState turned = BlockPicker.SelectedState(3,
            (int)BlockShape.Stair, 3, true, true, false, 2);
        BlockState crouched = BlockPicker.SelectedState(3,
            (int)BlockShape.Stair, 3, true, true, true, 2);
        check(copied.Facing == 3 && copied.Upper &&
            turned.Facing == 3 && turned.Upper &&
            crouched.Facing == 3 && !crouched.Upper,
            "Copied stair orientation stays absolute while turning; crouch inverts upper/lower");
        BlockState relative = BlockPicker.SelectedState(3,
            (int)BlockShape.HalfWall, 1, false, false, false, 2);
        check(relative.Facing == 3 && !relative.Upper,
            "Ordinary placement still rotates relative to the player's facing");

        Console.WriteLine("PASS: server-authorized pick-block inventory, creative grants, " +
            "shape copying and absolute rotation / upper-half controls");
    }
}
