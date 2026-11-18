using System;
using System.Numerics;
using Tallow;

namespace Tallowcraft;

/// <summary>
/// Plays the real Quaternius CC0 humanoid animations through Tallow's native
/// SkinnedMeshRenderer and its matched Universal Animation Library GLB.
/// Only visual pose selection is local; the Jolt CharacterController determines
/// actual movement and remains server-authoritative for gameplay.
/// </summary>
public class CharacterAnimation : Script
{
    public float WalkThreshold = 0.65f;
    public float RunThreshold = 6.6f;
    public float AirGrace = 0.12f;
    public float CrouchHeightTolerance = 0.1f;
    public float StandingHeight = 2.0f;

    // The imported clips are native-speed locomotion, not the 0.55x
    // placeholder .tanim loops. Keep rates close to the source animation.
    public float IdlePlayback = 1.0f;
    public float WalkPlayback = 0.95f;
    public float RunPlayback = 0.90f;
    public float JumpPlayback = 0.95f;
    public float CrouchIdlePlayback = 1.0f;
    public float CrouchWalkPlayback = 0.95f;

    private SkinnedMeshRenderer _rig;
    private Vector3 _lastPosition;
    private bool _ready;
    private float _smoothGroundSpeed;
    private string _lastClip = "";

    public override void Start()
    {
        _rig = GameObject.GetComponentInChildren<SkinnedMeshRenderer>();
        _lastPosition = Transform.Position;
        _ready = false;
        _smoothGroundSpeed = 0f;
        _lastClip = "";
    }

    public override void LocalUpdate(float deltaTime)
    {
        if (!_rig.IsValid)
            _rig = GameObject.GetComponentInChildren<SkinnedMeshRenderer>();
        if (!_rig.IsValid || deltaTime <= 0f)
            return;

        Vector3 now = Transform.Position;
        Vector3 movement = now - _lastPosition;
        _lastPosition = now;

        // Ignore first-frame deltas, big network corrections and frame stalls.
        if (!_ready || movement.LengthSquared() > 4f || deltaTime > 0.25f)
        {
            _ready = true;
            _smoothGroundSpeed = 0f;
            SetClip(QuaterniusClips.Idle, IdlePlayback);
            return;
        }

        float dt = Math.Clamp(deltaTime, 0.001f, 0.1f);
        float ground = MathF.Sqrt(movement.X * movement.X +
            movement.Z * movement.Z) / dt;
        float blend = 1f - MathF.Exp(-8f * dt);
        _smoothGroundSpeed += (Math.Clamp(ground, 0f, 15f) - _smoothGroundSpeed) * blend;

        bool running = _lastClip == QuaterniusClips.Run
            ? _smoothGroundSpeed > RunThreshold - 0.8f
            : _smoothGroundSpeed > RunThreshold;
        bool walking = _lastClip == QuaterniusClips.Walk
            ? _smoothGroundSpeed > WalkThreshold * 0.6f
            : _smoothGroundSpeed > WalkThreshold;

        // Use the actual Jolt character state, not changes in world Y.
        // SetHeight moves the character in midair to keep its head stable;
        // measuring Y movement alone falsely interprets that as a jump.
        // Height also reflects a failed attempt to stand beneath a ceiling.
        var controller = GetCharacterController();
        bool crouched = controller is { IsValid: true } activeController &&
            activeController.Height > 0f &&
            activeController.Height < StandingHeight - CrouchHeightTolerance;
        bool airborne = controller is { HasCharacter: true } character &&
            !character.IsGrounded &&
            (character.Velocity.Y > 0.75f || character.AirTime > AirGrace);

        // A real jump/fall overrides crouch animation, including air-crouching.
        // Otherwise choose grounded crouch poses from the capsule's real height.
        string clip = airborne ? QuaterniusClips.Jump
            : crouched ? (walking ? QuaterniusClips.CrouchForward : QuaterniusClips.CrouchIdle)
            : running ? QuaterniusClips.Run
            : walking ? QuaterniusClips.Walk : QuaterniusClips.Idle;
        float speed = clip == QuaterniusClips.Run ? RunPlayback
            : clip == QuaterniusClips.Walk ? WalkPlayback
            : clip == QuaterniusClips.Jump ? JumpPlayback
            : clip == QuaterniusClips.CrouchForward ? CrouchWalkPlayback
            : clip == QuaterniusClips.CrouchIdle ? CrouchIdlePlayback : IdlePlayback;
        SetClip(clip, speed);
    }

    private void SetClip(string clip, float speed)
    {
        // The engine cross-fades imported .glb clips by bone name when the
        // SkinnedMeshRenderer.Clip property changes. Do not restart every frame.
        if (_lastClip != clip)
        {
            _rig.Clip = clip;
            _lastClip = clip;
        }
        if (MathF.Abs(_rig.Speed - speed) > 0.001f)
            _rig.Speed = speed;
    }
}
