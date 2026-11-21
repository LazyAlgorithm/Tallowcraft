using System;

namespace WorldGeneration;

/// <summary>
/// Side-effect-free weapon and mob health rules. The server owns the actual
/// physics query, health fields and object lifetime; these checks are shared
/// with the isolated block-logic test project.
/// </summary>
public static class AnimalCombatRules
{
    public const int SparkPistolDamage = 18;
    public const int PulseRepeaterDamage = 9;
    public const float SparkPistolRange = 48f;
    public const float PulseRepeaterRange = 38f;
    public const float StairClimbHeight = 1.05f;

    public static int Damage(int weapon) => weapon switch
    {
        1 => SparkPistolDamage,
        2 => PulseRepeaterDamage,
        _ => 0
    };

    public static float Range(int weapon) => weapon switch
    {
        1 => SparkPistolRange,
        2 => PulseRepeaterRange,
        _ => 0f
    };

    /// <summary>
    /// Reject invalid damage and prevent a dead mob taking repeated hits.
    /// Return true only if this hit actually changed the mob's health.
    /// </summary>
    public static bool TryApply(int currentHealth, int damage,
        out int remainingHealth)
    {
        remainingHealth = currentHealth;
        if (currentHealth <= 0 || damage <= 0 || damage > 100)
            return false;
        remainingHealth = Math.Max(0, currentHealth - damage);
        return true;
    }
}
