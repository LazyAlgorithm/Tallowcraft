using System;
using System.Collections.Generic;
using Tallow;

namespace WorldGeneration;

/// <summary>
/// Replicated health for peaceful mobs. Firearms are resolved exclusively in
/// BlockEditor's server tick; clients never choose targets or write damage.
/// Short damage flashes are cosmetic and recreated from synced hit counters.
/// </summary>
public sealed class PeacefulAnimal : Script
{
    public string Species = "Sheep";
    public int MaxHealth = 40;
    [Sync] public int Health = 40;
    [Sync] public int HitSequence;
    [Sync] public int LastDamage;
    [Sync] public bool Dying;

    private readonly List<(MeshRenderer Renderer, Color Original)> _skin = new();
    private int _seenHitSequence;
    private float _flashSeconds;
    private float _deathSeconds = -1f;
    private bool _showingFlash;
    private bool _initialized;

    public override void Start()
    {
        if (Scene.IsServer)
        {
            MaxHealth = Math.Clamp(MaxHealth, 1, 200);
            Health = Math.Clamp(Health, 1, MaxHealth);
        }
        // CharacterController handles all bullet physics queries, so no
        // extra collider is placed on the artwork (and no double collisions).
        for (int i = 0; i < GameObject.ChildCount; i++)
        {
            if (GameObject.GetChild(i) is not { IsValid: true } child ||
                child.GetComponent<MeshRenderer>() is not { IsValid: true } mesh)
                continue;
            if (child.Name is "Eye" or "Nostril" or "Hoof")
                continue;
            _skin.Add((mesh, mesh.Color));
        }
        _seenHitSequence = HitSequence;
        _initialized = true;
    }

    /// <summary>Server-side application of an already validated ray hit.</summary>
    public bool TryTakeDamage(int amount, out int remaining)
    {
        remaining = Health;
        if (!Scene.IsServer || Dying ||
            !AnimalCombatRules.TryApply(Health, amount, out remaining))
            return false;

        Health = remaining;
        LastDamage = amount;
        HitSequence = HitSequence == int.MaxValue ? 1 : HitSequence + 1;
        if (Health == 0)
        {
            Dying = true;
            _deathSeconds = .65f;
            NavigationAgent agent = GetComponent<NavigationAgent>();
            if (agent.IsValid) agent.StopMovement();
        }
        return true;
    }

    public override void LocalUpdate(float deltaTime)
    {
        if (!_initialized) return;
        float dt = Math.Clamp(deltaTime, 0f, .1f);
        if (_seenHitSequence != HitSequence)
        {
            _seenHitSequence = HitSequence;
            _flashSeconds = .22f;
        }
        _flashSeconds = MathF.Max(0f, _flashSeconds - dt);
        bool red = Dying || _flashSeconds > 0f;
        if (red != _showingFlash)
        {
            _showingFlash = red;
            foreach (var (mesh, original) in _skin)
                if (mesh.IsValid)
                    mesh.Color = red ? new Color(1f, .43f, .43f, 1f) : original;
        }

        if (!Scene.IsServer || !Dying) return;
        if (_deathSeconds < 0f) _deathSeconds = .65f;
        _deathSeconds -= dt;
        if (_deathSeconds <= 0f && GameObject.IsValid)
            GameObject.Destroy(); // A NetworkIdentity replicates despawn.
    }
}
