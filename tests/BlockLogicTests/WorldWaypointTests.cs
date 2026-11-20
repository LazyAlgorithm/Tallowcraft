using System;
using System.IO;
using System.Numerics;
using WorldGeneration;

/// <summary>Peaceful exploration bookmarks are local, world-scoped, and
/// never provide server-side movement authority.</summary>
internal static class WorldWaypointTests
{
    public static void Run(Action<bool, string> check)
    {
        var book = new WaypointBook();
        check(book.Tracked == -1 && book.Position(0) == null &&
            book.Position(3) == null,
            "A fresh world has no tracked or invented landmarks");
        Vector3 home = new(12.25f, 35.5f, -14.875f);
        Vector3 mine = new(-128.5f, -18f, 207.25f);
        check(book.Mark(0, home) && book.Mark(2, mine) &&
            book.Tracked == 0 && book.Position(2) == mine,
            "Marking a landmark records real three-dimensional coordinates");
        check(book.Track(2) && book.Tracked == 2,
            "Players can select a distinct tracked landmark");
        check(WaypointBook.Direction(Vector3.Zero,
                    new Vector3(10, 0, -10)) == "NE" &&
                WaypointBook.Direction(Vector3.Zero,
                    new Vector3(-8, 0, 12)) == "SW" &&
                WaypointBook.Direction(Vector3.Zero,
                    new Vector3(1, 0, 1)) == "Here",
            "Tracked coordinates provide stable world-axis compass directions");
        check(!book.Mark(-1, home) && !book.Mark(4, home) &&
            !book.Track(1) && !book.Clear(1),
            "Out-of-range and empty landmark slots do not mutate state");
        check(!book.Mark(1, new Vector3(float.NaN, 0, 0)) &&
            !book.Mark(1, new Vector3(0, float.PositiveInfinity, 0)) &&
            !book.Mark(1, new Vector3(11_000_000f, 0, 0)) &&
            !book.Mark(1, new Vector3(0, 0, float.NegativeInfinity)),
            "Nonfinite and out-of-range locations cannot enter a save");

        string root = Path.Combine(Path.GetTempPath(),
            "tallowcraft-waypoints-" + Guid.NewGuid().ToString("N"));
        try
        {
            string worldId = Guid.NewGuid().ToString("N");
            string otherWorldId = Guid.NewGuid().ToString("N");
            string playerId = Guid.NewGuid().ToString("N");
            string otherPlayerId = Guid.NewGuid().ToString("N");
            WorldWaypointStore.Save(root, worldId, playerId, book);
            WaypointBook restored = WorldWaypointStore.Load(root,
                worldId, playerId);
            check(restored.Tracked == 2 && restored.Position(0) == home &&
                restored.Position(2) == mine && restored.Position(1) == null,
                "Saved landmarks roundtrip with exact floating-point coordinates");
            check(WorldWaypointStore.Load(root, otherWorldId,
                    playerId).Position(0) == null &&
                WorldWaypointStore.Load(root, worldId,
                    otherPlayerId).Tracked == -1,
                "Navigation files never leak between worlds or player profiles");

            check(restored.Clear(2) && restored.Tracked == -1 &&
                restored.Track(0) && restored.Tracked == 0,
                "Clearing the selected waypoint removes tracking safely");
            WorldWaypointStore.Save(root, worldId, playerId, restored);
            check(WorldWaypointStore.Load(root, worldId,
                    playerId).Tracked == 0 &&
                WorldWaypointStore.Load(root, worldId,
                    playerId).Position(2) == null,
                "Clearing and changing the tracked waypoint persist atomically");

            string path = Path.Combine(root, "tallowcraft", "waypoints",
                worldId, playerId + ".txt");
            File.AppendAllText(path, "tampered\n");
            bool rejectedCorruption = false;
            try { WorldWaypointStore.Load(root, worldId, playerId); }
            catch (InvalidDataException) { rejectedCorruption = true; }
            check(rejectedCorruption && File.Exists(path),
                "A corrupt landmark file is rejected rather than silently reset");

            bool rejectedIdentity = false;
            try { WorldWaypointStore.Save(root, "../other", playerId, book); }
            catch (ArgumentException) { rejectedIdentity = true; }
            check(rejectedIdentity,
                "World IDs cannot escape their scoped navigation folders");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("PASS: peaceful landmarks, tracking, world/player isolation, " +
            "corruption detection, exact coordinates and safe slot validation");
    }
}
