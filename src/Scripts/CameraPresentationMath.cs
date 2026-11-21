using System;
using System.Numerics;

namespace Tallowcraft;

/// <summary>
/// Presentation-only damping for the camera. Neither the player controller nor
/// replayable TickInput simulation uses these values.
/// </summary>
public static class CameraPresentationMath
{
    public static float Blend(float deltaTime, float responsiveness)
    {
        float dt = Math.Clamp(deltaTime, 0f, 0.1f);
        return 1f - MathF.Exp(-dt * MathF.Max(0f, responsiveness));
    }

    public static Vector3 FollowPosition(Vector3 current, Vector3 target,
        float deltaTime, float responsiveness, float snapDistance = 2.5f)
    {
        // An authoritative teleport must never ease the view through the
        // intervening world; ordinary walking, stepping and crouching blend.
        if (Vector3.DistanceSquared(current, target) >
            snapDistance * snapDistance)
            return target;

        return Vector3.Lerp(current, target, Blend(deltaTime, responsiveness));
    }

    public static Quaternion FollowRotation(Quaternion current,
        Quaternion target, float deltaTime, float responsiveness) =>
        Quaternion.Normalize(Quaternion.Slerp(
            current, target, Blend(deltaTime, responsiveness)));
}
