using System;
using System.Numerics;
using WorldGeneration;

internal static class AnimalAnimationTests
{
    public static void Run(Action<bool, string> check)
    {
        Vector3 near = new(-0.39f, -0.48f, 0.32f);
        Vector3 diagonal = new(0.39f, -0.48f, 0.32f);
        check(AnimalAnimationRules.LegOffset(near, 0.5f, 0f) == Vector3.Zero,
            "Standing mobs do not swing their legs");
        Vector3 front = AnimalAnimationRules.LegOffset(near, 1.0f, 1f);
        Vector3 opposite = AnimalAnimationRules.LegOffset(diagonal, 1.0f, 1f);
        check(front.Z * opposite.Z < 0f,
            "Opposite leg diagonals alternate their forward/back swing");
        check(front == AnimalAnimationRules.LegOffset(
                new Vector3(near.X, -0.71f, near.Z), 1.0f, 1f),
            "Hooves follow their matching leg exactly");
        check(AnimalAnimationRules.GrazePitch(0f, 2f) == 0f &&
              AnimalAnimationRules.GrazePitch(1f, 2f) > .8f,
            "Grazing lowers the head while standing keeps it upright");
        check(AnimalAnimationRules.DeathFraction(-2f) == 0f &&
              AnimalAnimationRules.DeathFraction(AnimalAnimationRules.DeathDuration) == 1f,
            "Death animation clamps its timing at both ends");
        check(AnimalAnimationRules.DeathRoll(AnimalAnimationRules.DeathDuration) < -1.2f &&
              AnimalAnimationRules.DeathSink(AnimalAnimationRules.DeathDuration) > .1f,
            "A dead animal visibly tips onto its side and settles");
        check(AnimalAnimationRules.Approach(.2f, 1f, .1f, 2f) > .2f &&
              AnimalAnimationRules.Approach(.8f, 0f, .1f, 2f) < .8f,
            "Movement and eating transitions blend in both directions");
        Console.WriteLine("PASS: peaceful animal walking, grazing, and death pose rules");
    }
}
