using System;
using System.Numerics;

namespace WorldGeneration;

/// <summary>Pure pose math shared by the animals' cosmetic animation and tests.
/// These values never alter Jolt physics or the server's navigation.</summary>
public static class AnimalAnimationRules
{
    public const float DeathDuration = 1.55f;
    public const float WalkingThreshold = 0.15f;
    public const float GrazeIdleSeconds = 0.8f;

    public static float Approach(float from, float to, float dt, float rate) =>
        Math.Clamp(from + Math.Clamp(to - from, -MathF.Max(0f, dt) * rate,
            MathF.Max(0f, dt) * rate), 0f, 1f);

    public static float Ease(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Opposing diagonal legs take alternating steps. Hooves use
    /// their matching leg's coordinates so the two always move together.</summary>
    public static Vector3 LegOffset(Vector3 rest, float phase, float walking)
    {
        float blend = Math.Clamp(walking, 0f, 1f);
        if (blend == 0f) return Vector3.Zero;
        float offset = rest.X * rest.Z >= 0f ? MathF.PI : 0f;
        float stride = MathF.Sin(phase + offset);
        return new Vector3(0f, MathF.Max(0f, stride) * 0.105f * blend,
            stride * 0.16f * blend);
    }

    /// <summary>Pitch the entire face around the neck during grazing, with
    /// a small periodic chewing nod. Positive X rotation lowers the muzzle.</summary>
    public static float GrazePitch(float grazing, float time) =>
        Math.Clamp(grazing, 0f, 1f) *
        (0.88f + MathF.Sin(time * 7.2f) * 0.055f);

    public static float DeathFraction(float seconds) =>
        Ease(seconds / DeathDuration);

    public static float DeathRoll(float seconds) =>
        -1.24f * DeathFraction(seconds);

    public static float DeathSink(float seconds) =>
        0.15f * DeathFraction(seconds);
}
