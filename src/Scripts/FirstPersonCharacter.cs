using System.Numerics;
using WorldGeneration;
using Tallow;

namespace Tallowcraft;

/// <summary>
/// Network-predicted first-person movement. The camera belongs only to the
/// local player's instance; remote players must never steal it.
/// </summary>
public class FirstPersonCharacter : Script
{
    public float WalkSpeed = 5f;
    public float SprintSpeed = 9f;
    public float CrouchSpeed = 2.5f;
    public float JumpSpeed = 6f;
    public float StandHeight = 2f;
    public float CrouchHeight = 1.2f;
    public float EyeHeight = 0.7f;
    public float CrouchTransitionSeconds = 0.2f;
    public string CameraName = "Main Camera";

    private GameObject? _camera;
    private float _eyeNow;
    private float _eyeStart;
    private float _eyeTarget;
    private float _eyeTransitionTime;
    private bool _spawnAligned;

    private bool ControlsCamera =>
        !Scene.IsNetworked || GameObject.OwnerClientId == Scene.LocalClientId;

    public override void Start()
    {
        _spawnAligned = false;
        _eyeNow = EyeHeight;
        _eyeStart = EyeHeight;
        _eyeTarget = EyeHeight;
        _eyeTransitionTime = CrouchTransitionSeconds;
        if (ControlsCamera)
        {
            Cursor.Locked = false;
            _camera = Scene.Find(CameraName);
        }
    }

    public override void Update(TickInput input, float deltaTime)
    {
        // Remote players are driven by replicated state, not local empty input.
        if (Scene.IsNetworked && !Scene.IsServer && !ControlsCamera)
            return;

        // The replicated inventory menu also blocks movement on the server;
        // unlocking the pointer must never let UI clicks move the player.
        var editor = GameObject.GetComponent<BlockEditor>();
        bool browsingInventory = editor?.IsInventoryVisible == true ||
            editor?.UiGameMenuVisible == true ||
            editor?.ReturnToSpawnLocked == true;
        if (GetCharacterController() is { } controller)
        {
            // Only the authoritative server relocates new player instances.
            // A fixed scene spawn Y can start inside terrain (or high in the
            // air), especially with vertical chunks. The mesh is deterministic
            // and the BlockWorld also checks edits and partial shapes.
            if (Scene.IsServer && !_spawnAligned &&
                Scene.Find("BlockWorld")?.GetComponent<BlockWorld>() is { } world)
            {
                Vector3 position = Transform.Position;
                float safeY = world.SafeSpawnHeight(position.X, position.Z,
                    StandHeight);
                if (System.MathF.Abs(position.Y - safeY) > 0.001f)
                {
                    position.Y = safeY;
                    Transform.Position = position;
                }
                // Update the native Jolt character as well as the transform
                // once physics has created it, so it cannot snap back into
                // the old spawn point during the next simulation step.
                if (controller.HasCharacter)
                    _spawnAligned = controller.Teleport(position);
            }

            float speed = browsingInventory ? 0f :
                input.Crouch ? CrouchSpeed : input.Sprint ? SprintSpeed : WalkSpeed;
            controller.SimpleMove(input.MoveDirection * speed, deltaTime,
                !browsingInventory && input.Pressed(InputButton.Jump) ? JumpSpeed : 0f);
            controller.SetHeight(input.Crouch ? CrouchHeight : StandHeight, StandHeight);

            float target = EyeHeight - (StandHeight - controller.Height);
            // Physics immediately changes the capsule. Match the camera's
            // visible transition to the crouch pose instead
            // of using an exponential lerp that never truly finishes.
            // CrouchLift compensates the native midair capsule shift.
            float lift = controller.CrouchLift;
            _eyeNow -= lift;
            _eyeStart -= lift;
            _eyeTarget -= lift;
            if (System.MathF.Abs(target - _eyeTarget) > 0.0001f)
            {
                _eyeStart = _eyeNow;
                _eyeTarget = target;
                _eyeTransitionTime = 0f;
            }

            float duration = System.MathF.Max(0.001f, CrouchTransitionSeconds);
            _eyeTransitionTime = System.MathF.Min(duration,
                _eyeTransitionTime + deltaTime);
            float t = Mathf.Clamp01(_eyeTransitionTime / duration);
            // Smooth start and finish, including interrupted transitions.
            t = t * t * (3f - 2f * t);
            _eyeNow = Mathf.Lerp(_eyeStart, _eyeTarget, t);
        }

        if (!browsingInventory)
            Transform.Rotation = RotationMath.EulerDegrees(new Vector3(0f, input.Yaw, 0f));

        if (!ControlsCamera)
            return;

        // Ownership can arrive after Start on a joining client.
        if (_camera is not { IsValid: true })
        {
            _camera = Scene.Find(CameraName);
            Cursor.Locked = !browsingInventory;
        }

        if (_camera is { } camera && camera.IsValid)
        {
            camera.Transform.Position = Transform.Position + new Vector3(0f, _eyeNow, 0f);
            if (!browsingInventory)
                camera.Transform.Rotation = input.LookRotation;
        }
    }
}
