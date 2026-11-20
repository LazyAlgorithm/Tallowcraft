using System;
using System.Numerics;

namespace WorldGeneration;

/// <summary>
/// Signed compass bearings in the same axis system as WaypointBook:
/// north = -Z, east = +X. Pure math allows headless regression tests.
/// </summary>
public static class WaypointCompass
{
    public const int Slots = 13;
    public const float DegreesPerSlot = 15f;
    public const float HalfWidth = 90f;

    public static float Heading(float x, float z) =>
        (MathF.Atan2(x, -z) * (180f / MathF.PI) + 360f) % 360f;

    public static float SignedDelta(float target, float forward)
    {
        float value = (target - forward + 540f) % 360f - 180f;
        return value;
    }

    /// <summary>Returns -1 when the marker is behind the 180-degree ribbon.</summary>
    public static int Slot(Vector3 viewer, Vector3 direction, Vector3 target)
    {
        float dx = target.X - viewer.X;
        float dz = target.Z - viewer.Z;
        if (!float.IsFinite(dx) || !float.IsFinite(dz) ||
            dx * dx + dz * dz < 0.01f ||
            direction.X * direction.X + direction.Z * direction.Z < 0.0001f)
            return -1;
        float delta = SignedDelta(Heading(dx, dz),
            Heading(direction.X, direction.Z));
        if (MathF.Abs(delta) > HalfWidth + DegreesPerSlot * 0.5f)
            return -1;
        return Math.Clamp((int)MathF.Round(delta / DegreesPerSlot) + Slots / 2,
            0, Slots - 1);
    }

    public static string TickLabel(Vector3 forward, int slot)
    {
        if (slot < 0 || slot >= Slots) return "";
        float bearing = Heading(forward.X, forward.Z) +
            (slot - Slots / 2) * DegreesPerSlot;
        bearing = (bearing % 360f + 360f) % 360f;
        int direction = (int)MathF.Round(bearing / 45f) % 8;
        float deviation = MathF.Abs(SignedDelta(bearing, direction * 45f));
        if (deviation > 7.5f) return "·";
        string[] labels = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        return labels[direction];
    }
}
