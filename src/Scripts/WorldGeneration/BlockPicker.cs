using System;

namespace WorldGeneration;

/// <summary>
/// Shared inventory rule for Minecraft-style pick-block. Pick selection never
/// mines, places, consumes or duplicates an existing stack. Survival requires
/// a real inventory stack; Creative can create one item only when an actual
/// empty inventory slot is available. The caller still performs the
/// server-authoritative raycast, ownership and proximity checks.
/// </summary>
public static class BlockPicker
{
    /// <summary>Pick a valid world block into the hotbar. Existing matching
    /// hotbar stacks are preferred. A backpack stack can swap with the
    /// currently selected slot. Inventory is unchanged on any failure.</summary>
    public static bool TryPick(BlockInventory inventory, ushort packedState,
        int selectedHotbar, bool creative,
        out int resultingHotbar, out bool inventoryChanged)
    {
        resultingHotbar = selectedHotbar;
        inventoryChanged = false;
        if (selectedHotbar is < 0 or >= BlockInventory.HotbarSlots ||
            !BlockState.IsValid(packedState) || packedState == 0)
            return false;

        int type = new BlockState(packedState).Type;
        if (type <= 0 || type > BlockInventory.Chest ||
            type == BlockInventory.IronIngot)
            return false;

        int hotbar = inventory.FindHotbarType(type);
        if (hotbar >= 0)
        {
            resultingHotbar = hotbar;
            return true;
        }

        // Stage any inventory mutation first. Failed picks cannot shuffle
        // a player's hotbar or discard a stack.
        var staged = new BlockInventory();
        if (!staged.TryLoad(inventory.Serialize()))
            return false;

        for (int slot = BlockInventory.HotbarSlots;
             slot < BlockInventory.SlotCount; slot++)
        {
            if (staged.Slot(slot).Type != type)
                continue;
            if (!staged.TryMove(slot, selectedHotbar))
                return false;
            if (!inventory.TryLoad(staged.Serialize()))
                throw new InvalidOperationException(
                    "A validated pick-block inventory swap failed.");
            resultingHotbar = selectedHotbar;
            inventoryChanged = true;
            return true;
        }

        if (!creative || inventory.Count(type) >= BlockInventory.MaxPerType)
            return false;

        // Creative mode may obtain one block for visual building. Use an
        // empty hotbar slot first; never overwrite or destroy existing items.
        int emptyHotbar = -1;
        for (int slot = 0; slot < BlockInventory.HotbarSlots; slot++)
            if (staged.Slot(slot).IsEmpty)
            {
                emptyHotbar = slot;
                break;
            }

        if (emptyHotbar < 0)
        {
            // If the hotbar is full, move the current material into the
            // backpack without dropping it, then fill its vacated slot.
            int emptyBackpack = -1;
            for (int slot = BlockInventory.HotbarSlots;
                 slot < BlockInventory.SlotCount; slot++)
                if (staged.Slot(slot).IsEmpty)
                {
                    emptyBackpack = slot;
                    break;
                }
            if (emptyBackpack < 0 ||
                !staged.TryMove(selectedHotbar, emptyBackpack))
                return false;
            emptyHotbar = selectedHotbar;
        }

        if (!staged.TryInsertIntoSlot(emptyHotbar, new ItemStack(type, 1)))
            return false;
        if (!inventory.TryLoad(staged.Serialize()))
            throw new InvalidOperationException(
                "A validated creative pick-block inventory grant failed.");
        resultingHotbar = emptyHotbar;
        inventoryChanged = true;
        return true;
    }

    /// <summary>Absolute orientation is maintained after picking an
    /// oriented block, even if the player later turns around. Rotate (F)
    /// increments this absolute facing; crouch temporarily inverts the
    /// picked upper/lower slab or stair.</summary>
    public static BlockState SelectedState(int type, int shape, int rotation,
        bool absoluteFacing, bool pickedUpper, bool crouching, int lookFacing)
    {
        int facing = absoluteFacing
            ? (rotation & 3) : (lookFacing + rotation) & 3;
        bool upper = pickedUpper != crouching;
        return BlockState.Create(type, (BlockShape)shape, facing, upper);
    }
}
