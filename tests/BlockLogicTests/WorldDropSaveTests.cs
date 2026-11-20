using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using WorldGeneration;

internal static class WorldDropSaveTests
{
    public static void Run(Action<bool, string> check)
    {
        var first = new SavedWorldDrop(7, 32,
            new Vector3(-17.5f, 6.25f, 28f), 45f);
        var second = new SavedWorldDrop(3, 4,
            new Vector3(18f, 22f, -42.5f), 299.5f);
        SavedWorldDrop[] original = { second, first };
        byte[] bytes = WorldDropSaveCodec.Encode(original);
        byte[] reversed = WorldDropSaveCodec.Encode(new[] { first, second });
        check(bytes.SequenceEqual(reversed) &&
            WorldDropSaveCodec.TryDecode(bytes, out var decoded) &&
            decoded.Count == 2 && decoded.Contains(first) &&
            decoded.Contains(second),
            "Drop codec deterministically roundtrips items, stacks, positions and ages");
        check(!WorldDropSaveCodec.TryDecode(bytes[..^1], out _) &&
            !WorldDropSaveCodec.TryDecode(new byte[9], out _),
            "Drop codec rejects incomplete or incorrectly sized data");
        byte[] invalidStack = (byte[])bytes.Clone();
        invalidStack[9] = 255;
        check(!WorldDropSaveCodec.TryDecode(invalidStack, out _),
            "Corrupt stack sizes never become physical pickups");
        byte[] invalidAge = (byte[])bytes.Clone();
        BitConverter.GetBytes(float.NaN).CopyTo(invalidAge, 8 + 2 + 12);
        check(!WorldDropSaveCodec.TryDecode(invalidAge, out _) &&
            !WorldDropSaveCodec.IsValid(first with { Age = 300f }) &&
            !WorldDropSaveCodec.IsValid(first with {
                Position = new Vector3(float.PositiveInfinity, 0f, 0f)
            }),
            "Expired, nonfinite or tampered drop snapshots are rejected");

        string root = Path.Combine(Path.GetTempPath(),
            "tallowcraft-drop-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            string worldId = WorldEditSave.NewWorldId();
            const int seed = 42;
            var edits = new BlockEdits();
            var chests = new WorldChestStorage();
            BlockPosition position = new(4, 10, -2);
            check(edits.Set(position, BlockState.Create(3).Packed, seed),
                "Drop recovery test has committed terrain edits");

            // Earlier save formats have no world-drop state.
            WorldEditSave.Save(root, worldId, seed, edits.Snapshot,
                edits.Revisions);
            check(WorldEditSave.TryLoad(root, out _, out _, out _,
                    out _, out IReadOnlyList<SavedWorldDrop> legacy, out _) &&
                legacy.Count == 0,
                "v3 world saves load without invented drops");
            WorldEditSave.Save(root, worldId, seed, edits.Snapshot,
                edits.Revisions, chests.Snapshot);
            check(WorldEditSave.TryLoad(root, out _, out _, out _,
                    out _, out IReadOnlyList<SavedWorldDrop> v4, out _) &&
                v4.Count == 0,
                "v4 world saves load without invented drops");

            WorldEditSave.SaveLegacyV5(root, worldId, seed,
                edits.Snapshot, edits.Revisions, chests.Snapshot, original);
            check(WorldEditSave.TryLoad(root, out string restoredId,
                    out int restoredSeed, out BlockEdits world,
                    out WorldChestStorage storage,
                    out IReadOnlyList<SavedWorldDrop> loaded,
                    out bool recovered) &&
                !recovered && restoredId == worldId &&
                restoredSeed == seed &&
                world.Get(position.X, position.Y, position.Z, seed) ==
                    BlockState.Create(3).Packed &&
                storage.Count == 0 && loaded.Count == 2 &&
                loaded.Contains(first) && loaded.Contains(second),
                "v5 worlds restore terrain, chests and drop snapshots together");

            // A healthy current generation must not replay a pickup.
            WorldEditSave.Save(root, worldId, seed, edits.Snapshot,
                edits.Revisions, chests.Snapshot,
                Array.Empty<SavedWorldDrop>());
            check(WorldEditSave.TryLoad(root, out _, out _, out _,
                    out _, out IReadOnlyList<SavedWorldDrop> empty, out _) &&
                empty.Count == 0,
                "A saved pickup stays absent from the current generation");
            string current = File.ReadAllText(
                Path.Combine(root, "current.txt")).Trim();
            string manifest = File.ReadAllLines(Path.Combine(
                root, "generations", current, "manifest.txt"))[0];
            string[] header = manifest.Split('|');
            check(header.Length == 6 && header[0] == "6",
                "The current world save uses incremental manifest v6");
            File.WriteAllBytes(WorldSaveV6.BlobPath(root, "drops",
                header[5]), new byte[] { 1, 2, 3, 4 });
            check(WorldEditSave.TryLoad(root, out _, out _, out _,
                    out _, out IReadOnlyList<SavedWorldDrop> rolledBack,
                    out bool fallback) &&
                fallback && rolledBack.Count == 2 &&
                rolledBack.Contains(first) && rolledBack.Contains(second),
                "Corrupt drop data rolls back the complete saved world generation");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("PASS: bounded drop codec, v3/v4 loading, " +
            "legacy v5 restore, incremental v6 migration and corruption rollback");
    }
}
