using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using WorldGeneration;

internal static class ProfileTests
{
    public static void Run(Action<bool, string> check)
    {
        string temp = Path.Combine(Path.GetTempPath(),
            "tallowcraft-profiles-" + Guid.NewGuid().ToString("N"));
        try
        {
            string host = PlayerIdentity.GetOrCreate(temp, "host");
            string client = PlayerIdentity.GetOrCreate(temp, "client");
            check(PlayerIdentity.Valid(host) && PlayerIdentity.Valid(client) &&
                host != client,
                "Local host and client have independent persistent identities");
            check(PlayerIdentity.GetOrCreate(temp, "host") == host &&
                PlayerIdentity.GetOrCreate(temp, "client") == client,
                "Player tokens survive restarting the process");
            check(!PlayerIdentity.Valid("../traversal") &&
                !PlayerIdentity.Valid(new string('a', 32) + "/") &&
                !PlayerIdentity.Valid(""),
                "Invalid identities cannot become save file paths");

            string world = Guid.NewGuid().ToString("N");
            string dir = Path.Combine(temp, "worlds", "default", "players", world);
            check(PlayerProfileClaims.TryClaim(world, host) &&
                !PlayerProfileClaims.TryClaim(world, host) &&
                PlayerProfileClaims.TryClaim(world, client),
                "Two connected players cannot claim one live player inventory");
            PlayerProfileClaims.Release(world, host);
            check(PlayerProfileClaims.TryClaim(world, host),
                "Disconnect releases a persistent player's live inventory lock");
            PlayerProfileClaims.Release(world, host);
            PlayerProfileClaims.Release(world, client);

            var first = PlayerProfileStore.Load(dir, world, host,
                out BlockInventory material, out PlayerTools equipment,
                out int type, out int shape, out int facing);
            check(first == ProfileLoadResult.New &&
                material.Count(3) == 64 && material.Count(6) == 64 &&
                type == 3 && shape == 0 && facing == 0 &&
                equipment.Equipped == MiningTool.Hand,
                "First-time players receive starter materials exactly once");

            check(material.TrySpend(3, 4) &&
                equipment.TryCraft(material, MiningTool.Pickaxe) &&
                equipment.TryEquip(MiningTool.Pickaxe) &&
                equipment.OnBlockMined(),
                "Inventory and equipped tool mutate before saving");
            PlayerProfileStore.Save(dir, world, host, material, equipment,
                6, 2, 3);
            var loaded = PlayerProfileStore.Load(dir, world, host,
                out BlockInventory restored, out PlayerTools tools,
                out type, out shape, out facing);
            check(loaded == ProfileLoadResult.Loaded &&
                restored.Serialize() == material.Serialize() &&
                tools.Serialize() == equipment.Serialize() &&
                type == 6 && shape == 2 && facing == 3,
                "Reconnecting restores all materials, tool wear and hotbar state");

            check(material.TryCollect(7, 8) &&
                equipment.OnBlockMined() &&
                material.TrySpend(6, 3),
                "Resources and tools can change after first save");
            PlayerProfileStore.Save(dir, world, host, material, equipment,
                4, 1, 0);

            string current = Path.Combine(dir, host + ".profile");
            string previous = current + ".bak";
            check(File.Exists(current) && File.Exists(previous),
                "Player saves retain the previous committed inventory");

            File.WriteAllText(current, "bad-current-profile");
            var recovered = PlayerProfileStore.Load(dir, world, host,
                out BlockInventory rolledBack, out PlayerTools rolledBackTools,
                out type, out shape, out facing);
            check(recovered == ProfileLoadResult.Recovered &&
                rolledBack.Serialize() == restored.Serialize() &&
                rolledBackTools.Serialize() == tools.Serialize() &&
                type == 6 && shape == 2 && facing == 3,
                "Corrupt latest profile rolls back to previous verified equipment");
            check(PlayerProfileStore.Load(dir, world, host, out _, out _,
                    out _, out _, out _) == ProfileLoadResult.Loaded,
                "Rollback repairs the current on-disk profile");

            // Simulate a crash between archiving the current file and writing
            // a new one: the backup remains an authoritative copy.
            File.Move(current, previous, overwrite: true);
            check(PlayerProfileStore.Load(dir, world, host, out _,
                    out _, out _, out _, out _) == ProfileLoadResult.Recovered,
                "Interrupted save uses the backup when current is absent");

            File.WriteAllText(current, "damaged");
            File.WriteAllText(previous, "damaged");
            check(PlayerProfileStore.Load(dir, world, host, out _,
                    out _, out _, out _, out _) == ProfileLoadResult.Corrupt,
                "Both damaged profiles are refused without resetting items");

            string otherWorld = Guid.NewGuid().ToString("N");
            check(PlayerProfileStore.Load(Path.Combine(temp, "players", otherWorld),
                    otherWorld, host, out _, out _, out _, out _, out _) ==
                ProfileLoadResult.New,
                "The same player has independent profiles in different worlds");

            bool invalidSlot = false;
            try { PlayerIdentity.GetOrCreate(temp, "../other"); }
            catch (ArgumentException) { invalidSlot = true; }
            check(invalidSlot, "Profile slot names cannot traverse directories");

            // A correct checksum with invalid material counts must still be
            // rejected, not trusted just because the digest matches.
            string malicious = "1;" + world + ";1|9999,0,0,0,0,0,0,0,0;" +
                "1|0,0,0|0;3;0;0";
            string digest = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(malicious))).ToLowerInvariant();
            File.WriteAllText(current, digest + "\n" + malicious + "\n");
            check(PlayerProfileStore.Load(dir, world, host, out _,
                    out _, out _, out _, out _) == ProfileLoadResult.Corrupt,
                "A checksummed but invalid inventory is rejected atomically");
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }

        Console.WriteLine(
            "PASS: persistent player identities, per-world inventory, " +
            "tool durability and corrupted-save recovery");
    }
}
