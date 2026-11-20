using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using WorldGeneration;

internal static class ChestTransactionTests
{
    public static void Run(Action<bool, string> check)
    {
        const int seed = 42;
        // A deposit and a withdrawal each cross two independently persisted
        // stores; cover every interruption point in both directions.
        foreach (bool fromPlayer in new[] { true, false })
        for (int stage = 0; stage < 4; stage++)
        {
            string root = Path.Combine(Path.GetTempPath(),
                "tallowcraft-chest-txn-" + Guid.NewGuid().ToString("N"));
            try
            {
                string worldId = WorldEditSave.NewWorldId();
                string token = Guid.NewGuid().ToString("N");
                string playerFolder = Path.Combine(root, "players", worldId);
                BlockPosition pos = new(5, 20, -3);
                var edits = new BlockEdits();
                check(edits.Set(pos, BlockState.Create(
                        BlockInventory.Chest, BlockShape.Cube, 1).Packed, seed),
                    "Chest transaction uses a real placed chest voxel");

                var player = new BlockInventory();
                player.FillStarter();
                var tools = new PlayerTools();
                var chests = new WorldChestStorage();
                var initialFill = new BlockInventory();
                check(initialFill.TryCollect(7, 40) &&
                    chests.TryTransfer(pos, initialFill, 0, 3, true, false),
                    "Chest transaction starts with existing stored resources");

                var untouched = new SavedWorldDrop(8, 2,
                    new Vector3(-1f, 4f, 6f), 25f);
                SavedWorldDrop[] drops = { untouched };
                WorldEditSave.Save(root, worldId, seed, edits.Snapshot,
                    edits.Revisions, chests.Snapshot, drops);
                PlayerProfileStore.Save(playerFolder, worldId, token, player,
                    tools, 3, 0, 0, 2);

                var afterInventory = new BlockInventory();
                check(afterInventory.TryLoad(player.Serialize()),
                    "Chest transaction stages player inventory without mutation");
                var afterChests = new WorldChestStorage();
                afterChests.Restore(chests.Snapshot);

                int slot = fromPlayer ? 0 : 6;
                int chestSlot = fromPlayer ? 1 : 3;
                check(afterChests.TryTransfer(pos, afterInventory, slot,
                        chestSlot, fromPlayer, split: false) &&
                    afterInventory.Serialize() != player.Serialize() &&
                    afterChests.At(pos).Serialize() != chests.At(pos).Serialize(),
                    "Deposit and withdrawal both change a staged pair of inventories");

                int movedType = fromPlayer ? player.Slot(slot).Type :
                    chests.At(pos).Slot(chestSlot).Type;
                static int ChestCount(ChestContents chest, int type) =>
                    Enumerable.Range(0, ChestContents.SlotCount)
                        .Sum(i => chest.Slot(i).Type == type
                            ? chest.Slot(i).Quantity : 0);
                check(player.Count(movedType) +
                        ChestCount(chests.At(pos), movedType) ==
                      afterInventory.Count(movedType) +
                        ChestCount(afterChests.At(pos), movedType),
                    "A staged chest transfer conserves the total number of items");

                var receipt = new PickupReceipt(worldId, token,
                    afterInventory, tools, 3, 0, 0, 2, drops,
                    pos, afterChests.At(pos));
                PickupTransactionStore.Prepare(root, receipt);
                check(PickupTransactionStore.TryRead(root, worldId,
                        out PickupReceipt? checkedReceipt) &&
                    checkedReceipt?.ChestPosition == pos &&
                    checkedReceipt.ChestAfter?.Serialize() ==
                        afterChests.At(pos).Serialize(),
                    "Chest journal v2 roundtrips the changed chest and player state");

                if (stage == 1 || stage == 3)
                    WorldEditSave.Save(root, worldId, seed, edits.Snapshot,
                        edits.Revisions, afterChests.Snapshot, drops);
                if (stage == 2 || stage == 3)
                    PlayerProfileStore.Save(playerFolder, worldId, token,
                        afterInventory, tools, 3, 0, 0, 2);

                check(WorldEditSave.TryLoad(root, out _, out _,
                        out BlockEdits loadedEdits,
                        out WorldChestStorage diskChests,
                        out IReadOnlyList<SavedWorldDrop> diskDrops,
                        out _) &&
                    PickupTransactionStore.Recover(root, worldId, seed,
                        loadedEdits.Snapshot, loadedEdits.Revisions,
                        diskChests.Snapshot,
                        out IReadOnlyList<SavedWorldDrop> finalDrops,
                        out IReadOnlyDictionary<BlockPosition, ChestContents> finalChests) &&
                    !PickupTransactionStore.HasPending(root) &&
                    finalDrops.SequenceEqual(drops) &&
                    (finalChests.TryGetValue(pos, out ChestContents? savedChest)
                        ? savedChest : new ChestContents()).Serialize() ==
                        afterChests.At(pos).Serialize(),
                    "Recovery applies the after-chest record once for all crash windows");

                check(WorldEditSave.TryLoad(root, out _, out _,
                        out BlockEdits restored, out WorldChestStorage stored,
                        out IReadOnlyList<SavedWorldDrop> savedDrops, out _) &&
                    stored.At(pos).Serialize() == afterChests.At(pos).Serialize() &&
                    savedDrops.SequenceEqual(drops) &&
                    new BlockState(restored.Get(pos.X, pos.Y, pos.Z, seed))
                        .Type == BlockInventory.Chest &&
                    PlayerProfileStore.Load(playerFolder, worldId, token,
                        out BlockInventory savedPlayer, out _, out _, out _,
                        out _, out _) == ProfileLoadResult.Loaded &&
                    savedPlayer.Serialize() == afterInventory.Serialize() &&
                    !PickupTransactionStore.Recover(root, worldId, seed,
                        restored.Snapshot, restored.Revisions,
                        stored.Snapshot, out _, out _),
                    "Chest and player persist the exact post-transfer counts without replay duplication");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        // The journal cannot fabricate storage where the saved voxel is
        // absent, even with a correct checksum; fail safely and retain it.
        string orphanRoot = Path.Combine(Path.GetTempPath(),
            "tallowcraft-chest-orphan-" + Guid.NewGuid().ToString("N"));
        try
        {
            string worldId = WorldEditSave.NewWorldId();
            string token = Guid.NewGuid().ToString("N");
            var player = new BlockInventory();
            player.FillStarter();
            var chest = new ChestContents();
            var journal = new PickupReceipt(worldId, token, player,
                new PlayerTools(), 3, 0, 0, 2,
                Array.Empty<SavedWorldDrop>(),
                new BlockPosition(5, 20, -3), chest);
            PickupTransactionStore.Prepare(orphanRoot, journal);
            bool rejected = false;
            var edits = new BlockEdits();
            try
            {
                PickupTransactionStore.Recover(orphanRoot, worldId, seed,
                    edits.Snapshot, edits.Revisions,
                    new WorldChestStorage().Snapshot,
                    out _, out _);
            }
            catch (InvalidDataException) { rejected = true; }
            check(rejected && PickupTransactionStore.HasPending(orphanRoot),
                "Orphan chest transaction is refused without deleting recovery evidence");
        }
        finally
        {
            if (Directory.Exists(orphanRoot))
                Directory.Delete(orphanRoot, recursive: true);
        }
        Console.WriteLine("PASS: redo-logged chest deposits/withdrawals, " +
            "eight crash windows and orphan record rejection");
    }
}
