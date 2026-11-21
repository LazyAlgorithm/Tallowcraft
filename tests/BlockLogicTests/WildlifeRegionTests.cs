using System;
using System.Numerics;
using WorldGeneration;

internal static class WildlifeRegionTests
{
    public static void Run(Action<bool, string> check)
    {
        Vector3 host = new(0, 10, 0);
        Vector3 close = new(18, 20, 16);
        Vector3 distant = new(100, 11, -110);
        check(WildlifeRegionRules.Share(host, close) &&
              !WildlifeRegionRules.Share(host, distant),
            "Nearby players share a wildlife navigation region; distant players do not");
        check(WildlifeRegionRules.DistanceXZ(host, close) ==
              WildlifeRegionRules.DistanceXZ(close, host),
            "Wildlife regions ignore height and are symmetric in X/Z");
        check(!WildlifeRegionRules.Recenter(host, new Vector3(15, 10, 10), 0) &&
              !WildlifeRegionRules.Recenter(host, distant, 8f) &&
              WildlifeRegionRules.Recenter(host, distant, 0f),
            "Moving a region waits for sufficient player displacement and cooldown");

        check(WildlifeRegionRules.IntersectsEdit(host,
                  new Vector3(22f, 6f, 5f), new Vector3(26f, 10f, 7f)) &&
              !WildlifeRegionRules.IntersectsEdit(host,
                  new Vector3(37f, 6f, 0f), new Vector3(40f, 10f, 2f)) &&
              !WildlifeRegionRules.IntersectsEdit(host,
                  new Vector3(0f, -25f, 0f), new Vector3(1f, -20f, 1f)),
            "Rebuild only navigation regions touching edits in X/Z and Y");

        var candidates = new[] {
            (Id: 0, Position: host),
            (Id: 2, Position: close),
            (Id: 7, Position: distant),
            (Id: 9, Position: new Vector3(180, 10, 110)),
            (Id: 11, Position: new Vector3(-220, 10, -110))
        };
        int regions = 0;
        var accepted = new Vector3[WildlifeRegionRules.MaxRegions];
        foreach (var candidate in candidates)
        {
            bool shared = false;
            for (int i = 0; i < regions; i++)
                shared |= WildlifeRegionRules.Share(accepted[i], candidate.Position);
            if (!shared && regions < accepted.Length)
                accepted[regions++] = candidate.Position;
        }
        check(regions == 4 &&
              WildlifeRegionRules.Share(accepted[0], host) &&
              WildlifeRegionRules.Share(accepted[1], distant),
            "Region planner groups nearby peers while capping remote habitat count");

        Console.WriteLine("PASS: per-player wildlife grouping, capped regions and terrain edit overlap");
    }
}
