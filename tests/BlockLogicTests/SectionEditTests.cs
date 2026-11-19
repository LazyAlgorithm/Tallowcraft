using System;
using System.Collections.Generic;
using System.IO;
using WorldGeneration;

internal static class SectionEditTests
{
    public static void Run(Action<bool, string> check)
    {
        const int seed = 42;
        ushort stair = BlockState.Create(6, BlockShape.Stair, 2).Packed;
        var edits = new BlockEdits();

        var negative = new BlockPosition(-1, 16, -17);
        check(SectionedEditSnapshot.SectionOf(negative) == new ChunkKey(-1, 1, -2) &&
            EditSectionCodec.Position(new ChunkKey(-1,1,-2),
                EditSectionCodec.Index(negative)) == negative,
            "Signed chunk coordinates and local voxel offsets roundtrip");
        check(edits.Set(negative, stair, seed),
            "A negative chunk accepts a partial-block modification");
        var before = edits.Snapshot;
        var beforePart = before.InSection(new ChunkKey(-1,1,-2));

        // More than the former global 512 limit, with deterministic positions.
        for (int z = 0; z < 100; z++)
        for (int x = 0; x < 1000; x++)
            check(edits.Set(new BlockPosition(x, 32, z), stair, seed),
                "A valid edit is not rejected by a global 512-block cap");
        check(edits.Count == 100_001,
            "The section store holds 100k+ edits at once");
        check(before.Count == 1 &&
            before.TryGetValue(negative, out ushort original) &&
            original == stair &&
            beforePart.Count == 1,
            "An immutable worker snapshot is unaffected by later writes");
        check(edits.InSection(new ChunkKey(0,2,0)).Count == 256,
            "Only a chunk's 16x16x16 positions are indexed together");

        ChunkKey testKey = new(0, 2, 0);
        IReadOnlyDictionary<BlockPosition, ushort> section = edits.InSection(testKey);
        byte[] bytes = EditSectionCodec.Encode(section);
        string hash = EditSectionCodec.Hash(bytes);
        Dictionary<BlockPosition, ushort> decoded = new();
        check(EditSectionCodec.Pages(bytes.Length) == 4 &&
            EditSectionCodec.TryDecode(testKey, seed, bytes, out decoded) &&
            decoded.Count == 256 &&
            decoded[new BlockPosition(0,32,0)] == stair,
            "Section state has a 4-byte packed format and decodes correctly");

        var receiving = new EditSectionTransfer(testKey);
        Dictionary<BlockPosition, ushort>? full = null;
        for (int page = 0; page < 4; page++)
        {
            string data = EditSectionCodec.Page(bytes, page);
            check(data.Length <= 352, "Packets stay well below the 4000-byte RPC cap");
            int status = receiving.Receive(edits.Revision(testKey),
                page, 4, hash, data, seed, out var chunk);
            check(status == (page == 3 ? 2 : 1),
                "Paginated sections become visible only after all pages arrive");
            if (chunk != null) full = chunk;
        }
        check(full != null && full.Count == 256 &&
            receiving.CompletedBytes != null,
            "All four pages verify to one atomic section");

        var outOfOrder = new EditSectionTransfer(testKey);
        check(outOfOrder.Receive(1, 1, 4, hash,
            EditSectionCodec.Page(bytes, 1), seed, out _) == 0 &&
            outOfOrder.NextPage == 0,
            "Out-of-order pages are rejected");
        check(outOfOrder.Receive(1, 0, 4, new string('0',64),
            EditSectionCodec.Page(bytes, 0), seed, out _) == 1 &&
            outOfOrder.Receive(1, 1, 4, hash,
                EditSectionCodec.Page(bytes,1), seed, out _) == 0,
            "A changed checksum during transfer resets the baseline");

        string folder = Path.Combine(Path.GetTempPath(),
            "tallowcraft-test-" + Guid.NewGuid().ToString("N"));
        string worldId = WorldEditSave.NewWorldId();
        try
        {
            WorldEditSave.Save(folder, worldId, seed,
                edits.Snapshot, edits.Revisions);
            BlockEdits restored = new();
            check(WorldEditSave.HasSave(folder) &&
                WorldEditSave.TryLoad(folder, out string restoredId,
                    out int restoredSeed, out restored) &&
                restoredId == worldId && restoredSeed == seed &&
                restored.Count == edits.Count &&
                restored.Get(negative.X,negative.Y,negative.Z,seed) == stair &&
                restored.Revision(testKey) == edits.Revision(testKey),
                "An atomic disk save restores 100k edits, revisions, orientation and seed");

            string pointer = File.ReadAllText(Path.Combine(folder, "current.txt"));
            string abandoned = Path.Combine(folder, "generations", "abandoned.tmp");
            Directory.CreateDirectory(abandoned);
            File.WriteAllText(Path.Combine(abandoned, "manifest.txt"), "incomplete");
            check(WorldEditSave.TryLoad(folder, out _, out _, out var safe) &&
                safe.Count == edits.Count,
                "Interrupted staged saves do not replace the last committed generation");

            var cached = new EditSectionTransfer(testKey);
            string cache = Path.Combine(folder, "cache");
            WorldEditCache.Write(cache, worldId, testKey, edits.Revision(testKey), bytes);
            check(WorldEditCache.TryRead(cache, worldId, testKey, seed,
                    out int knownRevision, out string knownHash, out var known) &&
                knownRevision == edits.Revision(testKey) &&
                knownHash == hash && known.Count == 256,
                "Previously downloaded chunks survive reconnect in a verified local cache");
            cached.CacheRevision = knownRevision;
            cached.CacheHash = knownHash;
            cached.Cached = known;
            check(cached.Receive(knownRevision, 0, -1, hash, "", seed,
                    out var confirmed) == 2 && confirmed!.Count == 256,
                "A server revision/hash match avoids downloading cached chunk data");

            check(restored.Set(new BlockPosition(0,32,0), 0, seed) &&
                restored.Revision(testKey) == edits.Revision(testKey) + 1,
                "Every live edit advances only its own section revision");
            var replica = new BlockEdits();
            replica.ReplaceSection(testKey, decoded, edits.Revision(testKey), seed);
            check(!replica.ApplyDelta(testKey, edits.Revision(testKey) + 2,
                    new BlockPosition(0,32,0), 0, seed) &&
                replica.ApplyDelta(testKey, edits.Revision(testKey) + 1,
                    new BlockPosition(0,32,0), 0, seed) &&
                !replica.ApplyDelta(testKey, edits.Revision(testKey) + 1,
                    new BlockPosition(0,32,0), 0, seed),
                "Live changes enforce contiguous revisions and reject replayed deltas");

            // Make a new valid generation then corrupt it: never treat that
            // as an empty world or silently discard the previous edits.
            WorldEditSave.Save(folder, worldId, seed,
                restored.Snapshot, restored.Revisions);
            string newPointer = File.ReadAllText(Path.Combine(folder, "current.txt"));
            check(newPointer != pointer &&
                WorldEditSave.TryLoad(folder, out _, out _, out var newest) &&
                newest.Get(0,32,0,seed) == 0,
                "The pointer switches only after a complete generation commits");
            File.WriteAllText(Path.Combine(folder, "current.txt"), "invalid!");
            check(!WorldEditSave.TryLoad(folder, out _, out _, out _),
                "Corrupted world pointers are refused, not silently reset");
            File.WriteAllText(Path.Combine(folder, "current.txt"), newPointer);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }

        Console.WriteLine("PASS: 100k edited blocks, immutable section snapshots, " +
            "bounded pages, cache reuse, revisions and crash-safe save recovery");
    }
}
