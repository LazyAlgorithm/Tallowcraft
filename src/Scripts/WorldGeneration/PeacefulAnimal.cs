using System;
using System.Collections.Generic;
using System.Numerics;
using Tallow;

namespace WorldGeneration;

/// <summary>
/// Server-owned health and feeding state, with client-local voxel-model
/// animations. Only visual children are posed: the root CharacterController,
/// NavigationAgent and networked Transform keep their original physics paths.
/// </summary>
public sealed class PeacefulAnimal : Script
{
    public string Species = "Sheep";
    public int MaxHealth = 40;
    [Sync] public int Health = 40;
    [Sync] public int HitSequence;
    [Sync] public int LastDamage;
    [Sync] public bool Dying;
    [Sync] public bool Eating;

    private enum VisualKind : byte { Body, Face, Leg, Tail }

    private readonly record struct VisualPart(
        GameObject Object, MeshRenderer Mesh, Color RestColor,
        Vector3 RestPosition, Quaternion RestRotation, VisualKind Kind,
        bool CanFlash, bool Chews);

    private readonly List<VisualPart> _parts = new();
    private int _seenHitSequence;
    private float _flashSeconds;
    private float _deathSeconds = -1f;
    private float _deathVisualSeconds;
    private float _walkPhase;
    private float _walkBlend;
    private float _grazeBlend;
    private float _clock;
    private float _grazeCooldown;
    private float _grazeTimeLeft;
    private float _idleSeconds;
    private int _grazeCycle;
    private Vector3 _lastPosition;
    private bool _showingFlash;
    private bool _initialized;

    public override void Start()
    {
        if (Scene.IsServer)
        {
            MaxHealth = Math.Clamp(MaxHealth, 1, 200);
            Health = Math.Clamp(Health, 1, MaxHealth);
            _grazeCooldown = 4f + (float)(GameObject.Id % 6UL);
        }

        // The prefab's separate cubes are cosmetic. CharacterController
        // handles physics/raycasting; do not animate it or add colliders.
        for (int i = 0; i < GameObject.ChildCount; i++)
        {
            if (GameObject.GetChild(i) is not { IsValid: true } child ||
                child.GetComponent<MeshRenderer>() is not { IsValid: true } mesh)
                continue;

            string name = child.Name;
            VisualKind kind = KindFor(name);
            bool flash = !name.Contains("eye", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("nostril", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("hoof", StringComparison.OrdinalIgnoreCase);
            bool chews = name is "Nose" or "Mouth" ||
                name.Contains("nostril", StringComparison.OrdinalIgnoreCase);
            _parts.Add(new VisualPart(child, mesh, mesh.Color,
                child.Transform.LocalPosition, child.Transform.LocalRotation,
                kind, flash, chews));
        }
        _seenHitSequence = HitSequence;
        _lastPosition = Transform.Position;
        _initialized = true;
    }

    private static VisualKind KindFor(string name)
    {
        if (name is "Leg" || name.Contains("hoof",
                StringComparison.OrdinalIgnoreCase))
            return VisualKind.Leg;
        if (name.Contains("tail", StringComparison.OrdinalIgnoreCase))
            return VisualKind.Tail;
        if (name is "Head" or "Nose" or "Mouth" or "Eye" or "Ear" or "Wool fringe" ||
            name.Contains("eye shine", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ear lining", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" ear", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("inner ear", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("cheek", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("eyebrow", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("nostril", StringComparison.OrdinalIgnoreCase))
            return VisualKind.Face;
        return VisualKind.Body;
    }

    /// <summary>Only the server may change replicated health.</summary>
    public bool TryTakeDamage(int amount, out int remaining)
    {
        remaining = Health;
        if (!Scene.IsServer || Dying ||
            !AnimalCombatRules.TryApply(Health, amount, out remaining))
            return false;

        Health = remaining;
        LastDamage = amount;
        HitSequence = HitSequence == int.MaxValue ? 1 : HitSequence + 1;
        Eating = false;
        _grazeTimeLeft = 0f;
        _grazeCooldown = 6f;
        if (Health == 0)
        {
            Dying = true;
            _deathSeconds = AnimalAnimationRules.DeathDuration + 0.18f;
            NavigationAgent agent = GetComponent<NavigationAgent>();
            if (agent.IsValid) agent.StopMovement();
            // The old tree would start another wander during the falling
            // animation. Removing the runner on the server stops future
            // wander commands without touching any engine code.
            GameObject.RemoveComponent<BehaviorTreeRunner>();
        }
        return true;
    }

    private void TickGrazing(float dt, float speed)
    {
        _grazeCooldown = MathF.Max(0f, _grazeCooldown - dt);
        if (speed < AnimalAnimationRules.WalkingThreshold)
            _idleSeconds += dt;
        else
            _idleSeconds = 0f;

        if (Eating)
        {
            _grazeTimeLeft -= dt;
            // Only graze during the behavior tree's idle / wait segment.
            // Cancelling as soon as the agent walks prevents sliding while
            // the head is down and leaves path selection to the tree.
            if (_grazeTimeLeft <= 0f || speed > .35f)
            {
                Eating = false;
                _grazeTimeLeft = 0f;
                _grazeCooldown = 7f + (_grazeCycle++ % 4) * 1.8f;
            }
        }
        else if (_grazeCooldown <= 0f &&
                 _idleSeconds >= AnimalAnimationRules.GrazeIdleSeconds)
        {
            Eating = true;
            _grazeTimeLeft = Species == "Pig" ? 2.6f : 3.1f;
            _idleSeconds = 0f;
        }
    }

    private void Pose(float dt)
    {
        float wantedWalk = Dying || Eating ? 0f :
            _walkBlend; // Movement smoothing already reflects this frame.
        _walkPhase += dt * (6.4f + wantedWalk * 1.1f);
        float bob = MathF.Abs(MathF.Sin(_walkPhase)) * .028f * _walkBlend +
            MathF.Sin(_clock * 1.9f) * .008f * (1f - _grazeBlend);

        float pitch = AnimalAnimationRules.GrazePitch(_grazeBlend, _clock);
        Quaternion nod = Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch);
        Vector3 neck = new(0f, .19f, .34f);
        float death = AnimalAnimationRules.DeathFraction(_deathVisualSeconds);
        Quaternion tumble = Quaternion.CreateFromAxisAngle(
            Vector3.UnitZ, AnimalAnimationRules.DeathRoll(_deathVisualSeconds));

        foreach (VisualPart part in _parts)
        {
            if (!part.Object.IsValid) continue;
            Vector3 pos = part.RestPosition;
            Quaternion rotation = part.RestRotation;
            switch (part.Kind)
            {
                case VisualKind.Leg:
                    pos += AnimalAnimationRules.LegOffset(
                        part.RestPosition, _walkPhase, _walkBlend);
                    pos.X += MathF.Sign(part.RestPosition.X) * .09f * death;
                    break;
                case VisualKind.Face:
                    pos = neck + Vector3.Transform(pos - neck, nod);
                    rotation = Quaternion.Normalize(nod * rotation);
                    pos.Y += bob;
                    if (part.Chews && _grazeBlend > .01f)
                    {
                        pos.Y += MathF.Sin(_clock * 13.5f) * .022f * _grazeBlend;
                        pos.Z += MathF.Sin(_clock * 13.5f) * .012f * _grazeBlend;
                    }
                    break;
                case VisualKind.Tail:
                    pos.X += MathF.Sin(_clock * 3.4f) * .045f * (1f - death);
                    break;
                default:
                    pos.Y += bob;
                    break;
            }

            if (death > 0f)
            {
                // All parts roll about the body center as a single visual
                // rig, then settle slightly into the ground. Physics stays
                // completely upright until server-authoritative despawn.
                pos = Vector3.Transform(pos, tumble);
                pos.Y -= AnimalAnimationRules.DeathSink(_deathVisualSeconds);
                rotation = Quaternion.Normalize(tumble * rotation);
            }
            part.Object.Transform.LocalPosition = pos;
            part.Object.Transform.LocalRotation = rotation;
        }
    }

    public override void LocalUpdate(float deltaTime)
    {
        if (!_initialized || !GameObject.IsValid) return;
        float dt = Math.Clamp(deltaTime, 0f, .1f);
        _clock += dt;

        Vector3 position = Transform.Position;
        float distance = Vector3.Distance(position, _lastPosition);
        _lastPosition = position;
        // Ignore network correction/teleport spikes when choosing animation.
        float speed = dt > 0f && distance < 1.25f ? distance / dt : 0f;

        if (Scene.IsServer && !Dying)
            TickGrazing(dt, speed);

        float targetWalk = !Dying && !Eating &&
            speed >= AnimalAnimationRules.WalkingThreshold ? 1f : 0f;
        _walkBlend = AnimalAnimationRules.Approach(
            _walkBlend, targetWalk, dt, 5.2f);
        _grazeBlend = AnimalAnimationRules.Approach(
            _grazeBlend, !Dying && Eating ? 1f : 0f, dt, 1.9f);

        if (_seenHitSequence != HitSequence)
        {
            _seenHitSequence = HitSequence;
            _flashSeconds = .22f;
        }
        _flashSeconds = MathF.Max(0f, _flashSeconds - dt);
        bool flash = _flashSeconds > 0f;
        if (flash != _showingFlash)
        {
            _showingFlash = flash;
            foreach (VisualPart part in _parts)
            {
                // MeshRenderer is a value-type native component handle. The
                // VisualPart record exposes it as a property, so assign
                // through a local variable instead of mutating that value.
                MeshRenderer mesh = part.Mesh;
                if (part.CanFlash && mesh.IsValid)
                    mesh.Color = flash ?
                        new Color(1f, .43f, .43f, 1f) : part.RestColor;
            }
        }

        if (Dying)
            _deathVisualSeconds = MathF.Min(
                AnimalAnimationRules.DeathDuration, _deathVisualSeconds + dt);
        Pose(dt);

        if (!Scene.IsServer || !Dying) return;
        if (_deathSeconds < 0f)
            _deathSeconds = AnimalAnimationRules.DeathDuration + .18f;
        _deathSeconds -= dt;
        if (_deathSeconds <= 0f && GameObject.IsValid)
            GameObject.Destroy();
    }
}
