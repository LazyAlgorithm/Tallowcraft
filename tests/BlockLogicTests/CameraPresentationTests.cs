using System;
using System.Numerics;
using Tallowcraft;
using WorldGeneration;

internal static class CameraPresentationTests
{
    public static void Run(Action<bool, string> check)
    {
        float small = CameraPresentationMath.Blend(1f / 144f, 48f);
        float large = CameraPresentationMath.Blend(1f / 60f, 48f);
        check(small > 0f && small < large && large < 1f,
            "Camera turn interpolation uses frame delta, not fixed frame steps");

        // Two half frames have the same response as one full frame.
        float half = CameraPresentationMath.Blend(1f / 120f, 48f);
        float full = CameraPresentationMath.Blend(1f / 60f, 48f);
        check(MathF.Abs((1f - half) * (1f - half) - (1f - full)) < .00001f,
            "Camera damping is independent of render FPS");

        Vector3 origin = Vector3.Zero;
        Vector3 near = new(.12f, 0f, 0f);
        Vector3 followed = CameraPresentationMath.FollowPosition(
            origin, near, 1f / 144f, 75f);
        check(followed.X > 0f && followed.X < near.X,
            "Small between-tick positional changes are smoothed");

        Vector3 teleported = new(40f, 5f, -30f);
        check(CameraPresentationMath.FollowPosition(
                followed, teleported, 1f / 144f, 75f) == teleported,
            "Camera snaps to teleport rather than flying across the world");

        Quaternion initial = Quaternion.Identity;
        Quaternion target = Quaternion.CreateFromYawPitchRoll(1f, .25f, 0f);
        Quaternion intermediate = CameraPresentationMath.FollowRotation(
            initial, target, 1f / 120f, 48f);
        check(Quaternion.Dot(intermediate, initial) < 1f &&
              Quaternion.Dot(intermediate, target) > Quaternion.Dot(initial, target) &&
              MathF.Abs(intermediate.LengthSquared() - 1f) < .0001f,
            "Look interpolation advances toward the latest authoritative tick");

        check(AnimalAnimationRules.DeathDuration < 1.0f &&
              AnimalAnimationRules.DeathDuration > .5f,
            "Animal death animation finishes rapidly without being instantaneous");
        Console.WriteLine("PASS: frame-rate-independent camera follow and faster death animation");
    }
}
