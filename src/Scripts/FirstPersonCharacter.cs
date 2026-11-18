using System.Numerics;
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
    public string CameraName = "Main Camera";

    private GameObject? _camera;
    private float _eyeNow;

    private bool ControlsCamera =>
        !Scene.IsNetworked || GameObject.OwnerClientId == Scene.LocalClientId;

    public override void Start()
    {
        _eyeNow = EyeHeight;
        if (ControlsCamera)
        {
            Cursor.Locked = true;
            _camera = Scene.Find(CameraName);
        }
    }

    public override void Update(TickInput input, float deltaTime)
    {
        // Remote players are driven by replicated state, not local empty input.
        if (Scene.IsNetworked && !Scene.IsServer && !ControlsCamera)
            return;

        if (GetCharacterController() is { } controller)
        {
            float speed = input.Crouch ? CrouchSpeed : input.Sprint ? SprintSpeed : WalkSpeed;
            controller.SimpleMove(input.MoveDirection * speed, deltaTime,
                input.Pressed(InputButton.Jump) ? JumpSpeed : 0f);
            controller.SetHeight(input.Crouch ? CrouchHeight : StandHeight, StandHeight);

            float target = EyeHeight - (StandHeight - controller.Height);
            _eyeNow = Mathf.Lerp(_eyeNow - controller.CrouchLift, target,
                Mathf.Clamp01(deltaTime * 12f));
        }

        Transform.Rotation = RotationMath.EulerDegrees(new Vector3(0f, input.Yaw, 0f));

        if (!ControlsCamera)
            return;

        // Ownership can arrive after Start on a joining client.
        if (_camera is not { IsValid: true })
        {
            _camera = Scene.Find(CameraName);
            Cursor.Locked = true;
        }

        if (_camera is { } camera && camera.IsValid)
        {
            camera.Transform.Position = Transform.Position + new Vector3(0f, _eyeNow, 0f);
            camera.Transform.Rotation = input.LookRotation;
        }
    }
}
