using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WorldGeneration;

/// <summary>Crash simulation for the direct-inventory reward used when the
/// server has no room to spawn an additional physical drop.</summary>
internal static class MiningFallbackTransactionTests
{
    public static void Run(Action<bool, string> check)
    {
        const int seed = 42;
        ushort stone = BlockState.Create(3).Packed;

        // Journal exists, only world saved, only profile saved, and both saved
        // but the journal has not yet been removed.
        for (int stage = 0; stage < 4; stage++)
        {
            string root = Path.Combine(Path.GetTempPath(),
                "tallowcraft-mining-fallback-" + Guid.NewGuid().ToString("N"));
            try
            {
                string world = WorldEditSave.NewWorldId();
                string token = Guid.NewGuid().ToString("N");
                string profiles = Path.Combine(root, "players", world);
                BlockPosition mined = new(9, 46, -3);
                BlockPosition unrelated = new(-4, 45, 7);
                var edits = new BlockEdits();
                check(edits.Set(mined, stone, seed) &&
                    edits.Set(unrelated, BlockState.Create(4).Packed, seed),
                    "Fallback transaction starts with the target and an unrelated unsaved edit");

                var beforeInventory = new BlockInventory();
                beforeInventory.FillStarter();
                var beforeTools = new PlayerTools();
                check(beforeTools.TryCraft(beforeInventory, MiningTool.Pickaxe) &&
                    beforeTools.TryEquip(MiningTool.Pickaxe),
                    "Mined reward test includes a nonempty, durable tool");
                var afterInventory = new BlockInventory();
                var afterTools = new PlayerTools();
                check(afterInventory.TryLoad(beforeInventory.Serialize()) &&
                    afterInventory.TryCollect(3, 1) &&
                    afterTools.TryLoad(beforeTools.Serialize()) &&
                    afterTools.OnBlockMined(),
                    "Fallback reward and tool wear are staged without altering original profile");

                var emptyDrops = Array.Empty<SavedWorldDrop>();
                var chest = new WorldChestStorage();
                WorldEditSave.Save(root, world, seed, edits.Snapshot,
                    edits.Revisions, chest.Snapshot, emptyDrops);
                PlayerProfileStore.Save(profiles, world, token,
                    beforeInventory, beforeTools, 3, 0, 0, 2);

                var receipt = new PickupReceipt(world, token,
                    afterInventory, afterTools, 3, 0, 0, 2,
                    emptyDrops, EditedBlock: mined, BlockBefore: stone,
                    BlockAfter: 0);
                PickupTransactionStore.Prepare(root, receipt);
                check(PickupTransactionStore.TryRead(root, world,
                        out PickupReceipt? loadedReceipt) &&
                    loadedReceipt?.EditedBlock == mined &&
                    loadedReceipt.BlockBefore == stone &&
                    loadedReceipt.BlockAfter == 0,
                    "V3 mining receipt preserves the precise voxel edit");

                if (stage == 1 || stage == 3)
                {
                    check(edits.Set(mined, 0, seed),
                        "Simulate server saving mined voxel before profile");
                    WorldEditSave.Save(root, world, seed,
                        edits.Snapshot, edits.Revisions, chest.Snapshot,
                        emptyDrops);
                }
                if (stage == 2 || stage == 3)
                    PlayerProfileStore.Save(profiles, world, token,
                        afterInventory, afterTools, 3, 0, 0, 2);

                check(WorldEditSave.TryLoad(root, out _, out _,
                        out BlockEdits loaded, out WorldChestStorage loadedChest,
                        out IReadOnlyList<SavedWorldDrop> loadedDrops,
                        out _) &&
                    PickupTransactionStore.Recover(root, world, seed,
                        loaded.Snapshot, loaded.Revisions,
                        loadedChest.Snapshot, out var recoveredDrops,
                        out var recoveredChests, out BlockEdits recoveredEdits) &&
                    !PickupTransactionStore.HasPending(root) &&
                    recoveredDrops.Count == 0 &&
                    recoveredChests.Count == 0 &&
                    recoveredEdits.Get(mined.X, mined.Y, mined.Z, seed) == 0 &&
                    recoveredEdits.Get(unrelated.X, unrelated.Y,
                        unrelated.Z, seed) == BlockState.Create(4).Packed,
                    "Interrupted fallback recovers the mined voxel without discarding other edits");

                check(WorldEditSave.TryLoad(root, out _, out _,
                        out BlockEdits persisted, out _, out _, out _) &&
                    persisted.Get(mined.X, mined.Y, mined.Z, seed) == 0 &&
                    PlayerProfileStore.Load(profiles, world, token,
                        out BlockInventory savedInventory,
                        out PlayerTools savedTools, out _, out _, out _,
                        out _) == ProfileLoadResult.Loaded &&
                    savedInventory.Serialize() == afterInventory.Serialize() &&
                    savedTools.Serialize() == afterTools.Serialize() &&
                    !PickupTransactionStore.Recover(root, world, seed,
                        persisted.Snapshot, persisted.Revisions,
                        chest.Snapshot, out _),
                    "Every crash window grants one reward and one tool-use cost");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        // If the server save no longer contains the claimed original block
        // or the result, replay refuses it without crediting the profile.
        string mismatchRoot = Path.Combine(Path.GetTempPath(),
            "tallowcraft-mismatched-mining-" + Guid.NewGuid().ToString("N"));
        try
        {
            string world = WorldEditSave.NewWorldId();
            string token = Guid.NewGuid().ToString("N");
            var edits = new BlockEdits();
            BlockPosition pos = new(7, 44, -2);
            check(edits.Set(pos, BlockState.Create(4).Packed, seed),
                "Mismatched mining journal test creates a different placed voxel");
            var inventory = new BlockInventory();
            inventory.FillStarter();
            var receipt = new PickupReceipt(world, token, inventory,
                new PlayerTools(), 3, 0, 0, 2,
                Array.Empty<SavedWorldDrop>(),
                EditedBlock: pos, BlockBefore: stone, BlockAfter: 0);
            PickupTransactionStore.Prepare(mismatchRoot, receipt);
            bool refused = false;
            try
            {
                PickupTransactionStore.Recover(mismatchRoot, world, seed,
                    edits.Snapshot, edits.Revisions,
                    new WorldChestStorage().Snapshot, out _, out _,
                    out _);
            }
            catch (InvalidDataException) { refused = true; }
            check(refused && PickupTransactionStore.HasPending(mismatchRoot),
                "Mismatched voxel receipt is never replayed or discarded");

            bool invalidRejected = false;
            try
            {
                PickupTransactionStore.Prepare(
                    Path.Combine(mismatchRoot, "invalid"),
                    receipt with { BlockAfter = stone });
            }
            catch (InvalidDataException) { invalidRejected = true; }
            check(invalidRejected,
                "Mining receipt cannot forge arbitrary block placements");
        }
        finally
        {
            if (Directory.Exists(mismatchRoot))
                Directory.Delete(mismatchRoot, recursive: true);
        }
        Console.WriteLine("PASS: direct-credit mining recovery, four crash " +
            "windows, tool durability, voxel mismatches and receipt validation");
    }
}
