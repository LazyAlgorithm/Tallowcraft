using System;
using WorldGeneration;

internal static class InventoryTests
{
    public static void Run(Action<bool, string> check)
    {
        var inventory = new BlockInventory();
        for (int type = 1; type <= BlockInventory.MaxType; type++)
            check(inventory.Count(type) == 0,
                "A new inventory must not contain phantom materials");

        inventory.FillStarter();
        check(inventory.Count(1) == 32 &&
            inventory.Count(2) == 64 &&
            inventory.Count(3) == 64 &&
            inventory.Count(4) == 32 &&
            inventory.Count(5) == 8 &&
            inventory.Count(6) == 64,
            "Starter materials must support initial building without mining");
        for (int type = 7; type <= BlockInventory.MaxType; type++)
            check(inventory.Count(type) == 0,
                "Ore and leaf resources should be collected in the world");

        string snapshot = inventory.Serialize();
        var replica = new BlockInventory();
        check(replica.TryLoad(snapshot),
            "Inventory replication payload must deserialize");
        for (int type = 1; type <= BlockInventory.MaxType; type++)
            check(replica.Count(type) == inventory.Count(type),
                "Inventory roundtrip must preserve all material quantities");
        check(replica.Serialize() == snapshot,
            "Inventory snapshots must serialize deterministically");

        check(inventory.TrySpend(3), "Stone placement consumes one item");
        check(inventory.Count(3) == 63,
            "Successful placement should reduce selected resource by one");
        check(!inventory.TrySpend(3, 64) && inventory.Count(3) == 63,
            "Out-of-stock placement cannot consume a partial amount");
        check(inventory.TryCollect(3) && inventory.Count(3) == 64,
            "Mining stone returns one stone");
        check(inventory.TryCollect(7, 3) && inventory.Count(7) == 3,
            "Coal ore can be collected even though it is not a starter hotbar slot");
        check(inventory.TryCollect(8, 2) && inventory.Count(8) == 2,
            "Iron ore enters the resource inventory");
        check(inventory.TryCollect(9, 1) && inventory.Count(9) == 1,
            "Tree leaves enter the resource inventory");

        BlockState sidewaysStair = BlockState.Create(4, BlockShape.Stair, 2);
        check(inventory.TryCollect(sidewaysStair.Type) &&
            inventory.Count(4) == 33,
            "Mining oriented partial shapes returns their material identity");

        check(!inventory.TryCollect(0) &&
            !inventory.TryCollect(BlockInventory.MaxType + 1) &&
            !inventory.TrySpend(0) &&
            !inventory.TrySpend(BlockInventory.MaxType + 1) &&
            !inventory.TryCollect(3, -1) &&
            !inventory.TrySpend(3, 0),
            "Invalid block types and quantities cannot change inventory");

        var full = new BlockInventory();
        check(full.TryCollect(3, BlockInventory.MaxPerType),
            "One material may fill to the inventory limit");
        check(!full.TryCollect(3) &&
            full.Count(3) == BlockInventory.MaxPerType,
            "Inventory overflow must be rejected without wrapping");
        check(full.TrySpend(3, BlockInventory.MaxPerType) &&
            full.Count(3) == 0,
            "Full stacks can be spent atomically");
        check(!full.TrySpend(3), "Empty stacks cannot be spent");

        string preserved = replica.Serialize();
        foreach (string invalid in new[]
        {
            "", "1|", "2|32,64,64,32,8,64,0,0,0",
            "1|32,64,64,32,8,64,0,0",
            "1|32,64,64,32,8,64,0,0,0,0",
            "1|32,64,64,32,8,64,0,0,-1",
            "1|32,64,64,32,8,64,0,0,1000",
            "1|32,64,64,32,8,64,0,0,nan",
            new string('1', 128)
        })
        {
            check(!replica.TryLoad(invalid),
                "Malformed network inventory snapshots must be rejected");
            check(replica.Serialize() == preserved,
                "Invalid inventory messages cannot mutate existing item counts");
        }

        // Exhaust stock, then harvest again; tests the intended survival
        // placement and mining accounting independently of engine physics.
        var survival = new BlockInventory();
        survival.FillStarter();
        check(survival.TrySpend(5, 8),
            "All starter forges can be placed");
        check(!survival.CanSpend(5),
            "Further forge placement is blocked when inventory is empty");
        check(survival.TryCollect(5) && survival.TrySpend(5),
            "A mined forge can be placed again without duplication");

        // Recipes live in pure game logic. The server, not an RPC payload,
        // chooses ingredient costs, outputs and quantities.
        var crafting = new BlockInventory();
        crafting.FillStarter();
        check(crafting.CanCraft(1) && crafting.TryCraft(1) &&
            crafting.Count(4) == 31 && crafting.Count(6) == 68,
            "One log becomes four planks and charges exactly one log");
        string craftedState = crafting.Serialize();
        var joinedClient = new BlockInventory();
        check(joinedClient.TryLoad(craftedState) &&
            joinedClient.Count(4) == 31 && joinedClient.Count(6) == 68,
            "Late-join inventory replication includes authoritative crafts");
        check(!crafting.TryCraft(0) && !crafting.TryCraft(999) &&
            crafting.Serialize() == craftedState,
            "Invalid or forged recipe IDs cannot change material counts");

        var shortage = new BlockInventory();
        check(shortage.TryCollect(2, 2) && !shortage.TryCraft(2) &&
            shortage.Count(2) == 2 && shortage.Count(1) == 0,
            "Missing secondary ingredient must not consume primary ingredient");
        check(shortage.TryCollect(9) && shortage.TryCraft(2) &&
            shortage.Count(2) == 0 && shortage.Count(9) == 0 &&
            shortage.Count(1) == 2,
            "Grass recipe consumes two dirt and one leaves for two grass");

        var forge = new BlockInventory();
        check(forge.TryCollect(3, 8) && forge.TryCollect(8, 2) &&
            forge.TryCraft(3) && forge.Count(5) == 1 &&
            forge.Count(3) == 0 && forge.Count(8) == 0,
            "Forge recipe consumes stone and iron atomically");

        var saturated = new BlockInventory();
        check(saturated.TryCollect(4) &&
            saturated.TryCollect(6, BlockInventory.MaxPerType),
            "Craft overflow case prepares full output stack");
        string unchanged = saturated.Serialize();
        check(!saturated.CanCraft(1) && !saturated.TryCraft(1) &&
            saturated.Serialize() == unchanged,
            "Full output inventory must not consume logs or overflow");
        check(saturated.TrySpend(6, 4) && saturated.TryCraft(1) &&
            saturated.Count(6) == BlockInventory.MaxPerType &&
            saturated.Count(4) == 0,
            "Craft accepts exact remaining output capacity");

        Console.WriteLine(
            "PASS: validated inventory snapshots, atomic server crafting, mining rewards and placement costs");
    }
}
