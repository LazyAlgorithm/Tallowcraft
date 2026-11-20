using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using WorldGeneration;

/// <summary>
/// v6 saves re-use unchanged immutable section objects and commit only a
/// small manifest, preserving v5-and-earlier load and pointer rollback.
/// </summary>
internal static class IncrementalWorldSaveTests
{
    private static int CountObjects(string root, string kind)
    {
        string directory = Path.Combine(root, "objects", kind);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.bin",
                SearchOption.AllDirectories).Count() : 0;
    }

    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(),
            "tallowcraft-v6-" + Guid.NewGuid().ToString("N"));
        try
        {
            string worldId = WorldEditSave.NewWorldId();
            const int seed = 42;
            var edits = new BlockEdits();
            ushort stone = BlockState.Create(3).Packed;
            // Distinct 16x16x16 sections create an expensive original v5
            // save but only one new section blob on a one-block edit.
            for (int i = 0; i < 24; i++)
                check(edits.Set(new BlockPosition(i * 16, 45, 1),
                    stone, seed), "Create deterministic edited sections");
            var chestPosition = new BlockPosition(400, 45, 1);
            check(edits.Set(chestPosition,
                    BlockState.Create(BlockInventory.Chest).Packed, seed),
                "A real chest block is stored in the edited world");
            var chests = new WorldChestStorage();
            var owner = new BlockInventory();
            check(owner.TryCollect(6, 12) &&
                chests.TryTransfer(chestPosition, owner,
                    0, 3, fromPlayer: true, split: false),
                "A chest world object contains its stored item stacks");
            var drops = new List<SavedWorldDrop>();

            WorldEditSave.Save(root, worldId, seed, edits.Snapshot,
                edits.Revisions, chests.Snapshot, drops);
            string initial = File.ReadAllText(
                Path.Combine(root, "current.txt")).Trim();
            string initialManifest = Path.Combine(root, "generations",
                initial, "manifest.txt");
            check(File.ReadAllLines(initialManifest)[0].StartsWith("6|",
                    StringComparison.Ordinal) &&
                Directory.GetFiles(Path.GetDirectoryName(initialManifest)!)
                    .Select(Path.GetFileName)
                    .SequenceEqual(new[] { "manifest.txt" }),
                "v6 generations store only the manifest, never full copies of sections");
            int sections = CountObjects(root, "sections");
            int chestBlobs = CountObjects(root, "chests");
            int dropBlobs = CountObjects(root, "drops");
            check(sections > 0 && sections <= 25 &&
                chestBlobs == 1 && dropBlobs == 1,
                "The object store deduplicates immutable sections, chests and drops");

            WorldEditSave.Save(root, worldId, seed, edits.Snapshot,
                edits.Revisions, chests.Snapshot, drops);
            string repeated = File.ReadAllText(
                Path.Combine(root, "current.txt")).Trim();
            check(repeated != initial &&
                CountObjects(root, "sections") == sections &&
                CountObjects(root, "chests") == chestBlobs &&
                CountObjects(root, "drops") == dropBlobs &&
                File.ReadAllText(Path.Combine(root, "previous.txt")).Trim()
                    == initial,
                "A world checkpoint with no changes writes zero data blobs");

            var firstChanged = new BlockPosition(0, 45, 1);
            check(edits.Set(firstChanged, 0, seed),
                "Modify exactly one previously stored section");
            WorldEditSave.Save(root, worldId, seed, edits.Snapshot,
                edits.Revisions, chests.Snapshot, drops);
            check(CountObjects(root, "sections") == sections + 1 &&
                CountObjects(root, "chests") == chestBlobs &&
                CountObjects(root, "drops") == dropBlobs,
                "One changed section writes exactly one new section blob");

            drops.Add(new SavedWorldDrop(3, 2,
                new Vector3(-2f, 17f, 1f), 25f));
            WorldEditSave.Save(root, worldId, seed, edits.Snapshot,
                edits.Revisions, chests.Snapshot, drops);
            check(CountObjects(root, "sections") == sections + 1 &&
                CountObjects(root, "chests") == chestBlobs &&
                CountObjects(root, "drops") == dropBlobs + 1,
                "Moving or collecting drops does not rewrite voxel data");

            var filled = new BlockInventory();
            check(filled.TryCollect(7, 5) &&
                chests.TryTransfer(chestPosition, filled, 0, 4,
                    fromPlayer: true, split: false),
                "Update chest contents without voxel edits");
            WorldEditSave.Save(root, worldId, seed, edits.Snapshot,
                edits.Revisions, chests.Snapshot, drops);
            check(CountObjects(root, "sections") == sections + 1 &&
                CountObjects(root, "chests") == chestBlobs + 1 &&
                CountObjects(root, "drops") == dropBlobs + 1,
                "One chest update writes a new chest blob but no section blobs");

            int objectsBeforeGc = CountObjects(root, "sections") +
                CountObjects(root, "chests") + CountObjects(root, "drops");
            string unfinished = Path.Combine(root, "generations",
                "not-committed.tmp");
            Directory.CreateDirectory(unfinished);
            check(WorldSaveV6.PruneGarbage(root) == 0,
                "Orphan blob cleanup refuses to run during an incomplete save");
            Directory.Delete(unfinished, recursive: true);
            int removed = WorldSaveV6.PruneGarbage(root);
            int objectsAfterGc = CountObjects(root, "sections") +
                CountObjects(root, "chests") + CountObjects(root, "drops");
            check(removed > 0 && objectsAfterGc == objectsBeforeGc - removed,
                "Unreferenced historical blobs are pruned without rewriting live data");

            check(WorldEditSave.TryLoad(root, out string restoredId,
                    out int restoredSeed, out BlockEdits restored,
                    out WorldChestStorage restoredChests,
                    out IReadOnlyList<SavedWorldDrop> restoredDrops,
                    out bool recovered) &&
                !recovered && restoredId == worldId &&
                restoredSeed == seed &&
                restored.Get(firstChanged.X,firstChanged.Y,firstChanged.Z,
                    seed) == 0 &&
                restoredChests.At(chestPosition).Slot(4).Quantity == 5 &&
                restoredDrops.SequenceEqual(drops),
                "Incremental world saves reload every section, chest and drop accurately");

            // Only the changed section blob differs between these two
            // generations, so corruption of the new blob must still leave
            // an independent, valid previous generation for rollback.
            var secondChanged = new BlockPosition(16, 45, 1);
            // Use a distinct nonempty payload: two empty sections would
            // intentionally share one blob, invalidating both generations
            // if that shared object were then deliberately corrupted.
            check(edits.Set(secondChanged, BlockState.Create(4).Packed, seed),
                "Prepare a unique changed section blob to test rollback");
            WorldEditSave.Save(root, worldId, seed, edits.Snapshot,
                edits.Revisions, chests.Snapshot, drops);
            string damagedGen = File.ReadAllText(
                Path.Combine(root, "current.txt")).Trim();
            string[] manifest = File.ReadAllLines(Path.Combine(root,
                "generations", damagedGen, "manifest.txt"));
            string[] entry = manifest.Skip(1).Select(l => l.Split(','))
                .First(l => l[0] == "1" && l[1] == "2" && l[2] == "0");
            string blobPath = WorldSaveV6.BlobPath(root, "sections", entry[4]);
            File.WriteAllBytes(blobPath, new byte[] { 1, 2, 3 });
            check(WorldEditSave.TryLoad(root, out _, out _,
                    out BlockEdits backup, out _, out _, out bool rolledBack) &&
                rolledBack &&
                backup.Get(secondChanged.X, secondChanged.Y,
                    secondChanged.Z, seed) == stone &&
                backup.Get(firstChanged.X, firstChanged.Y,
                    firstChanged.Z, seed) == 0,
                "A corrupted new blob rolls back to the valid prior manifest");

            bool invalidPathRejected = false;
            try { WorldSaveV6.BlobPath(root, "sections", "../../escape"); }
            catch (InvalidDataException) { invalidPathRejected = true; }
            check(invalidPathRejected,
                "Saved content checksums cannot traverse outside the object store");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("PASS: v6 incremental world saves, zero rewrites " +
            "on unchanged sections, category isolation and corruption rollback");
    }
}
