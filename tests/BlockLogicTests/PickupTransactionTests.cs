using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using WorldGeneration;

internal static class PickupTransactionTests
{
    public static void Run(Action<bool, string> check)
    {
        const int seed = 42;
        // Exercise every crash window: after the journal write, after only
        // the world write, after only the profile write, and after both.
        for (int stage = 0; stage < 4; stage++)
        {
            string root = Path.Combine(Path.GetTempPath(),
                "tallowcraft-pickup-" + Guid.NewGuid().ToString("N"));
            try
            {
                string worldId = WorldEditSave.NewWorldId();
                string token = Guid.NewGuid().ToString("N");
                string profileFolder = Path.Combine(root, "players", worldId);
                var edits = new BlockEdits();
                var chests = new WorldChestStorage();
                // Y=-1 is solid stone in the protected surface layer.
                BlockPosition mined = new(8, -1, -5);
                check(edits.Set(mined, 0, seed),
                    "Pickup recovery test includes the already-mined voxel");
                var before = new BlockInventory();
                before.FillStarter();
                var after = new BlockInventory();
                check(after.TryLoad(before.Serialize()) &&
                    after.TryCollect(7, 3),
                    "Pickup is prevalidated in a copied inventory");
                var tools = new PlayerTools();
                var collected = new SavedWorldDrop(7, 3,
                    new Vector3(8.5f, 12.1f, -4.5f), 8f);
                var untouched = new SavedWorldDrop(8, 2,
                    new Vector3(-3f, 2f, 7f), 15f);
                SavedWorldDrop[] original = { collected, untouched };
                SavedWorldDrop[] remaining = { untouched };

                // The existing world save is checkpointed before preparing
                // the receipt, including its voxel edits and active drops.
                WorldEditSave.Save(root, worldId, seed, edits.Snapshot,
                    edits.Revisions, chests.Snapshot, original);
                PlayerProfileStore.Save(profileFolder, worldId, token,
                    before, tools, 3, 0, 0, 2);

                var receipt = new PickupReceipt(worldId, token, after, tools,
                    3, 0, 0, 2, remaining);
                PickupTransactionStore.Prepare(root, receipt);
                check(PickupTransactionStore.HasPending(root) &&
                    PickupTransactionStore.TryRead(root, worldId,
                        out PickupReceipt? verified) &&
                    verified != null &&
                    verified.Inventory.Serialize() == after.Serialize() &&
                    verified.Drops.SequenceEqual(remaining),
                    "Durable pickup journal validates the complete after-state");
                bool overwriteRejected = false;
                try { PickupTransactionStore.Prepare(root, receipt); }
                catch (InvalidOperationException) { overwriteRejected = true; }
                check(overwriteRejected,
                    "A pending pickup cannot be overwritten by another pickup");

                if (stage == 1 || stage == 3)
                    WorldEditSave.Save(root, worldId, seed, edits.Snapshot,
                        edits.Revisions, chests.Snapshot, remaining);
                if (stage == 2 || stage == 3)
                    PlayerProfileStore.Save(profileFolder, worldId, token,
                        after, tools, 3, 0, 0, 2);

                check(PickupTransactionStore.Recover(root, worldId, seed,
                        edits.Snapshot, edits.Revisions, chests.Snapshot,
                        out IReadOnlyList<SavedWorldDrop> recoveredDrops) &&
                    recoveredDrops.SequenceEqual(remaining) &&
                    !PickupTransactionStore.HasPending(root),
                    "All pickup crash windows are replayed and finalized");

                check(WorldEditSave.TryLoad(root, out _, out _,
                        out BlockEdits savedEdits, out _,
                        out IReadOnlyList<SavedWorldDrop> savedDrops,
                        out bool recoveredWorld) &&
                    !recoveredWorld &&
                    savedDrops.SequenceEqual(remaining) &&
                    savedEdits.Get(mined.X, mined.Y, mined.Z, seed) == 0,
                    "Recovered world contains the removed pickup once, without losing edits");

                check(PlayerProfileStore.Load(profileFolder, worldId, token,
                        out BlockInventory savedInventory, out _, out _, out _,
                        out _, out _) == ProfileLoadResult.Loaded &&
                    savedInventory.Serialize() == after.Serialize() &&
                    !PickupTransactionStore.Recover(root, worldId, seed,
                        edits.Snapshot, edits.Revisions, chests.Snapshot,
                        out _),
                    "Recovered player credits the item once and does not replay again");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        string corrupt = Path.Combine(Path.GetTempPath(),
            "tallowcraft-pickup-bad-" + Guid.NewGuid().ToString("N"));
        try
        {
            string world = WorldEditSave.NewWorldId();
            string player = Guid.NewGuid().ToString("N");
            var inventory = new BlockInventory();
            inventory.FillStarter();
            var receipt = new PickupReceipt(world, player, inventory,
                new PlayerTools(), 3, 0, 0, 2,
                Array.Empty<SavedWorldDrop>());
            PickupTransactionStore.Prepare(corrupt, receipt);
            File.AppendAllText(Path.Combine(corrupt, "pending-pickup.txn"),
                "tampering");
            bool invalidRejected = false;
            try { PickupTransactionStore.TryRead(corrupt, world, out _); }
            catch (InvalidDataException) { invalidRejected = true; }
            check(invalidRejected && PickupTransactionStore.HasPending(corrupt),
                "Corrupted durable pickup evidence is rejected, not discarded");
        }
        finally
        {
            if (Directory.Exists(corrupt))
                Directory.Delete(corrupt, recursive: true);
        }

        Console.WriteLine("PASS: redo-logged pickups, four crash windows, " +
            "single-apply recovery and corrupted journal rejection");
    }
}
