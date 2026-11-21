using System;
using WorldGeneration;

internal static class AnimalCombatTests
{
    public static void Run(Action<bool, string> check)
    {
        check(AnimalCombatRules.Damage(1) == 18 &&
            AnimalCombatRules.Damage(2) == 9 &&
            AnimalCombatRules.Damage(0) == 0 &&
            AnimalCombatRules.Damage(3) == 0,
            "Only the two existing test guns can damage peaceful mobs");
        check(AnimalCombatRules.Range(1) > AnimalCombatRules.Range(2) &&
            AnimalCombatRules.Range(2) > 0,
            "Pistol and repeater use finite, distinct hitscan reach");
        check(AnimalCombatRules.StairClimbHeight >= 1f,
            "Animal controller and navmesh both clear one-metre blocks");

        int health = 40;
        check(AnimalCombatRules.TryApply(health, AnimalCombatRules.Damage(1),
            out health) && health == 22,
            "Spark pistol reduces 40 health to 22");
        check(AnimalCombatRules.TryApply(health, AnimalCombatRules.Damage(2),
            out health) && health == 13,
            "Repeater reduces health independently");
        check(AnimalCombatRules.TryApply(health, 18, out health) && health == 0,
            "Lethal damage clamps health at zero");
        check(!AnimalCombatRules.TryApply(health, 18, out int after) && after == 0,
            "Dead animals cannot be damaged repeatedly");
        check(!AnimalCombatRules.TryApply(40, 0, out after) && after == 40 &&
            !AnimalCombatRules.TryApply(40, -5, out after) && after == 40 &&
            !AnimalCombatRules.TryApply(40, 101, out after) && after == 40,
            "Invalid hit amounts cannot alter mob health");
        Console.WriteLine("PASS: peaceful mob weapon damage, death, hit rejection and step policy");
    }
}
