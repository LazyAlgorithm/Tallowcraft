using System;
using System.Numerics;

namespace WorldGeneration;

/// <summary>Pure multiplayer region geometry, independent of Tallow's
/// scene and network handles. Testable without starting a game server.</summary>
public static class WildlifeRegionRules
{
    public const float ShareDistance = 34f;
    public const float RecenterDistance = 21f;
    public const float CullDistance = 34f;
    public const float RegionHalfWidth = 24f;
    public const int MaxRegions = 4;

    public static float DistanceXZ(Vector3 a, Vector3 b) =>
        Vector2.Distance(new Vector2(a.X, a.Z),
            new Vector2(b.X, b.Z));

    public static bool Share(Vector3 a, Vector3 b) =>
        DistanceXZ(a, b) < ShareDistance;

    public static bool Recenter(Vector3 center, Vector3 focus,
        float cooldownSeconds) =>
        cooldownSeconds <= 0f &&
        DistanceXZ(center, focus) > RecenterDistance;

    public static bool IntersectsEdit(Vector3 center,
        Vector3 min, Vector3 max) =>
        max.X >= center.X - RegionHalfWidth &&
        min.X <= center.X + RegionHalfWidth &&
        max.Z >= center.Z - RegionHalfWidth &&
        min.Z <= center.Z + RegionHalfWidth &&
        max.Y >= center.Y - 13f &&
        min.Y <= center.Y + 13f;
}
