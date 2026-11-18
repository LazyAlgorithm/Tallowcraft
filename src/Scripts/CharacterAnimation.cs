using System;
using System.Numerics;
using Tallow;

namespace Tallowcraft;

/// <summary>
/// Animation is local visual presentation; every peer derives its character
/// pose from the same replicated player movement, without networked pose spam.
/// This script lives on the player root; Animator and rig live on a child.
/// </summary>
public class CharacterAnimation : Script
{
    public float WalkThreshold = 0.35f;
    public float RunThreshold = 6.5f;
    public float AirThreshold = 1.8f;

    private Animator _animator;
    private Vector3 _lastPosition;
    private bool _ready;
    private float _airTime;

    public override void Start()
    {
        _animator = GameObject.GetComponentInChildren<Animator>();
        _lastPosition = Transform.Position;
        _ready = false;
    }

    public override void LocalUpdate(float deltaTime)
    {
        if (!_animator.IsValid)
            _animator = GameObject.GetComponentInChildren<Animator>();
        if (!_animator.IsValid || deltaTime <= 0f)
            return;

        Vector3 current = Transform.Position;
        Vector3 displacement = current - _lastPosition;
        _lastPosition = current;
        if (!_ready || displacement.LengthSquared() > 100f)
        {
            _ready = true;
            _airTime = 0f;
            _animator.Play("Idle", 0f);
            return;
        }

        float horizontal = MathF.Sqrt(displacement.X * displacement.X +
            displacement.Z * displacement.Z) / deltaTime;
        float vertical = MathF.Abs(displacement.Y / deltaTime);
        _airTime = vertical > AirThreshold ? _airTime + deltaTime : 0f;

        string clip = _airTime > .12f ? "Jump"
            : horizontal > RunThreshold ? "Run"
            : horizontal > WalkThreshold ? "Walk" : "Idle";

        if (_animator.CurrentClip != clip)
            _animator.Play(clip, clip == "Jump" ? .1f : .17f);

        _animator.Speed = clip switch
        {
            "Walk" => Math.Clamp(horizontal / 2.6f, .65f, 1.65f),
            "Run" => Math.Clamp(horizontal / 6f, .8f, 1.5f),
            _ => 1f
        };
    }
}
