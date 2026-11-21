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

        check(BlockState.Create(BlockInventory.Glass).Type == BlockInventory.Glass &&
            BlockState.IsValid(BlockState.Create(BlockInventory.Glass).Packed) &&
            BlockState.Create(BlockInventory.Glass, BlockShape.Stair).IsAir,
            "Glass remains a cube-only block until partial transparent meshing is validated");
        check(inventory.TryCollect(7, 4) && inventory.TryCraft(11) &&
            inventory.Count(BlockInventory.Glass) == 4,
            "Craft transparent glass from stone and coal");
        check(inventory.TryCraft(13) &&
            inventory.Count(BlockInventory.Sandstone) == 4,
            "Craft sandstone without changing the original block IDs");
        string saved = inventory.Serialize();
        var restored = new BlockInventory();
        check(restored.TryLoad(saved) && restored.Serialize() == saved,
            "New item types must survive inventory serialization");

        // v4 saves from before new materials carried only eleven reserve
        // fields; they must remain readable without reassigning old types.
        var old = new BlockInventory();
        old.FillStarter();
        string[] segments = old.Serialize().Split('|');
        string legacy = "4|" + segments[1] + "|" +
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
        Console.WriteLine("PASS: building blocks, glass, recipes and legacy save compatibility");
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
        check(WaypointCompass.MarkerSlots == 61 &&
            WaypointCompass.Slots == 13 &&
            WaypointCompass.MarkerDegreesPerSlot == 3f,
            "Marker positions have five times the granularity of cardinal ticks");
        Vector3 origin = new(-100, 4, -120);
        check(WaypointCompass.Slot(origin, -Vector3.UnitZ,
            origin - Vector3.UnitZ * 100) == WaypointCompass.MarkerSlots / 2,
            "Northward waypoint centers in a northward camera");
        check(WaypointCompass.Slot(origin, -Vector3.UnitZ,
            origin + Vector3.UnitX * 100) == 0,
            "Facing -Z, eastward waypoint is screen-left in Tallow's +Z camera convention");
        check(WaypointCompass.Slot(origin, -Vector3.UnitZ,
            origin - Vector3.UnitX * 100) == WaypointCompass.MarkerSlots - 1,
            "Facing -Z, westward waypoint is screen-right in Tallow's +Z camera convention");
        check(WaypointCompass.Slot(origin, -Vector3.UnitZ,
            origin + Vector3.UnitZ * 100) == -1,
            "Waypoints behind the player are excluded from forward ribbon");
        check(WaypointCompass.Slot(origin, Vector3.UnitX,
            origin - Vector3.UnitZ * 100) == WaypointCompass.MarkerSlots - 1,
            "Compass headings rotate relative to the camera");
        check(WaypointCompass.TickLabel(-Vector3.UnitZ, 6) == "N" &&
            WaypointCompass.TickLabel(Vector3.UnitX, 6) == "E",
            "Center tick label matches camera direction");
        // Reproduce the reported regression: after the camera turns
        // right, a stationary waypoint ahead must move left on the ribbon.
        Vector3 fixedWaypoint = origin + Vector3.UnitZ * 100f;
        float turn = MathF.PI / 12f; // 15 degrees = one ribbon slot
        Vector3 cameraTurnRight = Vector3.Transform(Vector3.UnitZ,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, turn));
        Vector3 cameraTurnLeft = Vector3.Transform(Vector3.UnitZ,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, -turn));
        check(WaypointCompass.Slot(origin, Vector3.UnitZ, fixedWaypoint) == 30 &&
            WaypointCompass.Slot(origin, cameraTurnRight, fixedWaypoint) == 25 &&
            WaypointCompass.Slot(origin, cameraTurnLeft, fixedWaypoint) == 35,
            "Stationary waypoint moves opposite to right/left camera turns");
        check(WaypointCompass.TickLabel(Vector3.UnitZ, 0) == "W" &&
            WaypointCompass.TickLabel(Vector3.UnitZ, 6) == "S" &&
            WaypointCompass.TickLabel(Vector3.UnitZ, 12) == "E",
            "Cardinal labels use the same left/right axis as waypoint icons");
        // Tallow camera.Transform.Forward is local +Z rotated by yaw.
        // Test every quarter-turn using that axis convention, not a
        // fixed player heading or the replicated body rotation.
        for (int quarter = 0; quarter < 4; quarter++)
        {
            float yaw = quarter * MathF.PI / 2f;
            Vector3 forward = Vector3.Transform(Vector3.UnitZ,
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
            Vector3 left = new(-forward.Z, 0f, forward.X);
            Vector3 right = -left;
            check(WaypointCompass.Slot(origin, forward,
                    origin + forward * 75f) == WaypointCompass.MarkerSlots / 2,
                "Waypoints ahead stay in the center as the camera turns");
            check(WaypointCompass.Slot(origin, forward,
                    origin + left * 75f) == 0 &&
                WaypointCompass.Slot(origin, forward,
                    origin + right * 75f) == WaypointCompass.MarkerSlots - 1,
                "Compass left/right markers align with camera yaw");
            check(WaypointCompass.Slot(origin, forward,
                    origin - forward * 75f) == -1,
                "Behind-camera markers are excluded at all orientations");
        }
        Console.WriteLine("PASS: signed camera-relative waypoint compass bearings");
    }
}
