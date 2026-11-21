using System;
using WorldGeneration;

internal static class CreativeInventoryTests
{
    public static void Run(Action<bool, string> check)
    {
        check(!BlockInventory.IsCreativeBlock(0) &&
              !BlockInventory.IsCreativeBlock(BlockInventory.IronIngot) &&
              !BlockInventory.IsCreativeBlock(BlockInventory.MaxType + 1),
            "Creative catalog excludes air, iron ingots and invalid types");

        int listed = 0;
        for (int type = 1; type <= BlockInventory.MaxType; type++)
        {
            if (!BlockInventory.IsCreativeBlock(type)) continue;
            listed++;
            var catalog = new BlockInventory();
            check(catalog.TryGrantCreativeBlock(type, 1) &&
                  catalog.Slot(0) == new ItemStack(type, 1),
                "Every catalog entry must create the chosen block stack");
        }
        check(listed == 18, "Creative catalog contains all 18 placeable block types");

        var inventory = new BlockInventory();
        inventory.FillStarter();
        check(inventory.TryGrantCreativeBlock(BlockInventory.Glass, 64) &&
              inventory.Count(BlockInventory.Glass) == 64 &&
              inventory.Slot(6) == new ItemStack(BlockInventory.Glass, 64),
            "Creative full stack fills the first empty physical slot");
        check(inventory.TryGrantCreativeBlock(BlockInventory.Glass, 1) &&
              inventory.Count(BlockInventory.Glass) == 65 &&
              inventory.Slot(7) == new ItemStack(BlockInventory.Glass, 1),
            "Single-block grants merge or use a second physical stack");
        check(!inventory.TryGrantCreativeBlock(BlockInventory.IronIngot, 1) &&
              !inventory.TryGrantCreativeBlock(BlockInventory.Glass, 0) &&
              !inventory.TryGrantCreativeBlock(BlockInventory.Glass, 2) &&
              !inventory.TryGrantCreativeBlock(BlockInventory.Glass, 65),
            "Only 1 or 64 placeable blocks can be granted");
        check(inventory.Reserve(BlockInventory.Glass) == 0,
            "Creative grants never enter hidden legacy reserve");
        string encoded = inventory.Serialize();
        var restored = new BlockInventory();
        check(restored.TryLoad(encoded) &&
              restored.Count(BlockInventory.Glass) == 65 &&
              restored.Slot(6) == new ItemStack(BlockInventory.Glass, 64),
            "Creative items persist in the standard v5 save format");

        var full = new BlockInventory();
        for (int type = 1; type <= BlockInventory.MaxType; type++)
            if (BlockInventory.IsCreativeBlock(type))
                check(full.TryCollect(type, 128),
                    "Fill two physical stacks of each creative type");
        for (int i = 0; i < BlockInventory.SlotCount; i++)
            check(!full.Slot(i).IsEmpty,
                "All 36 physical inventory slots are occupied");
        string fullSnapshot = full.Serialize();
        check(!full.TryGrantCreativeBlock(BlockInventory.Glass, 1) &&
              !full.TryGrantCreativeBlock(BlockInventory.Slate, 64) &&
              full.Serialize() == fullSnapshot,
            "Full inventory rejects grant atomically without hidden overflow");

        check(full.TryRemoveFromSlot(0, 1) &&
              full.TryGrantCreativeBlock(1, 1) &&
              full.Slot(0) == new ItemStack(1, 64),
            "A matching partially empty stack can accept creative items");

        var capped = new BlockInventory();
        check(capped.TryCollect(BlockInventory.Slate, BlockInventory.MaxPerType) &&
              !capped.TryGrantCreativeBlock(BlockInventory.Slate, 1) &&
              capped.Count(BlockInventory.Slate) == BlockInventory.MaxPerType,
            "Creative grant preserves the 999-item per-type limit");

        Console.WriteLine("PASS: creative catalog validation, grants, capacity and persistence");
    }
}
