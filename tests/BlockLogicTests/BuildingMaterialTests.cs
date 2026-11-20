using System;
using System.Linq;
using System.Numerics;
using WorldGeneration;

internal static class BuildingMaterialTests
{
    public static void Run(Action<bool, string> check)
    {
        var inventory = new BlockInventory();
        inventory.FillStarter();
        check(inventory.TryCraft(7) && inventory.Count(BlockInventory.Cobblestone) == 2,
            "Craft cobblestone from stone without changing old item IDs");
        check(inventory.TryCraft(8) && inventory.Count(BlockInventory.StoneBricks) == 4,
            "Craft stone bricks from existing stone resources");
        check(inventory.TryCollect(9) && inventory.TryCollect(3, 2),
            "Prepare leaves and stone for mossy recipe");
        check(inventory.TryCraft(7) && inventory.TryCraft(9) &&
            inventory.Count(BlockInventory.MossyStone) == 4,
            "Craft mossy stone from three cobblestone and one leaves");
        check(inventory.TryCraft(10) && inventory.Count(BlockInventory.DarkTimber) == 4,
            "Craft four dark timber from two logs");

        foreach (int type in new[]
                 {
                     BlockInventory.Cobblestone, BlockInventory.StoneBricks,
                     BlockInventory.MossyStone, BlockInventory.DarkTimber
                 })
        {
            foreach (BlockShape shape in Enum.GetValues<BlockShape>())
            {
                BlockState state = BlockState.Create(type, shape, 3);
                check(BlockState.IsValid(state.Packed) && state.Type == type &&
                    state.Shape == shape, "New materials must support rotated partial blocks");
            }
            check(BlockTextureAtlas.TileFor(BlockState.Create(type), 4) == type,
                "Each new block uses its own approved atlas slot");
        }

        string saved = inventory.Serialize();
        var restored = new BlockInventory();
        check(restored.TryLoad(saved) && restored.Serialize() == saved,
            "New item types must survive inventory serialization");

        // v4 saves from before new materials carried only eleven reserve
        // fields; they must remain readable without reassigning old types.
        var old = new BlockInventory();
        old.FillStarter();
        string[] segments = old.Serialize().Split('|');
        string legacy = segments[0] + "|" + segments[1] + "|" +
            string.Join(",", segments[2].Split(',').Take(11));
        var migrated = new BlockInventory();
        check(migrated.TryLoad(legacy) && migrated.Count(3) == 64 &&
            migrated.Count(BlockInventory.DarkTimber) == 0,
            "Previous eleven-reserve player profiles must still load");

        var creative = new BlockInventory();
        creative.FillStarter();
        check(BlockPicker.TryPick(creative,
                BlockState.Create(BlockInventory.DarkTimber).Packed, 2, true,
                out int slot, out bool changed) && changed &&
            creative.Slot(slot).Type == BlockInventory.DarkTimber,
            "Creative pick-block must support all new materials");
        Console.WriteLine("PASS: four new building blocks, recipes, atlas and legacy save compatibility");
    }
}

internal static class WaypointCompassTests
{
    public static void Run(Action<bool, string> check)
    {
        check(WaypointCompass.Heading(0, -1) == 0 &&
            WaypointCompass.Heading(1, 0) == 90 &&
            WaypointCompass.Heading(0, 1) == 180 &&
            WaypointCompass.Heading(-1, 0) == 270,
            "Compass uses north=-Z east=+X world axes");
        Vector3 origin = new(-100, 4, -120);
        check(WaypointCompass.Slot(origin, -Vector3.UnitZ,
            origin - Vector3.UnitZ * 100) == 6,
            "Northward waypoint centers in a northward camera");
        check(WaypointCompass.Slot(origin, -Vector3.UnitZ,
            origin + Vector3.UnitX * 100) == 12,
            "Eastward waypoint is at right compass edge when facing north");
        check(WaypointCompass.Slot(origin, -Vector3.UnitZ,
            origin - Vector3.UnitX * 100) == 0,
            "Westward waypoint is at left compass edge when facing north");
        check(WaypointCompass.Slot(origin, -Vector3.UnitZ,
            origin + Vector3.UnitZ * 100) == -1,
            "Waypoints behind the player are excluded from forward ribbon");
        check(WaypointCompass.Slot(origin, Vector3.UnitX,
            origin - Vector3.UnitZ * 100) == 0,
            "Compass headings rotate relative to the camera");
        check(WaypointCompass.TickLabel(-Vector3.UnitZ, 6) == "N" &&
            WaypointCompass.TickLabel(Vector3.UnitX, 6) == "E",
            "Center tick label matches camera direction");
        Console.WriteLine("PASS: signed camera-relative waypoint compass bearings");
    }
}
