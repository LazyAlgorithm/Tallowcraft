using System;
using System.Numerics;
using Tallow;
using WorldGeneration;

namespace Tallowcraft;

/// <summary>Articulated block character driven by movement and capsule state.</summary>
public class CharacterAnimation : Script
{
    [Sync] public float CapsuleHeight = 2f;
    [Sync] public bool Airborne;

    private GameObject? _rig;
    private GameObject _torso, _head;
    private readonly GameObject[] _arms = new GameObject[2];
    private readonly GameObject[] _elbows = new GameObject[2];
    private readonly GameObject[] _legs = new GameObject[2];
    private readonly GameObject[] _knees = new GameObject[2];
    private readonly MeshRenderer[] _renderers = new MeshRenderer[10];
    private int _partCount;
    private bool? _shadowOnly;
    private Vector3 _lastPosition;
    private float _speed, _stride, _idle, _crouch, _jump, _mining;
    private BlockEditor? _editor;
    private GameObject? _equipment;
    private string _itemMesh = "";
    private int _lastShot;
    private float _shotPose;

    public override void Update(TickInput input, float deltaTime)
    {
        if (!Scene.IsServer || GetCharacterController() is not { HasCharacter: true } controller)
            return;
        CapsuleHeight = controller.Height;
        Airborne = !controller.IsGrounded &&
            (controller.Velocity.Y > .55f || controller.AirTime > .12f);
    }

    private GameObject Part(GameObject parent, string name, Vector3 position)
    {
        GameObject part = Scene.CreateGameObject(name);
        part.Transient = true;
        part.SetParent(parent, false);
        part.Transform.LocalPosition = position;
        var renderer = part.AddComponent<MeshRenderer>();
        renderer.Mesh = $"Models/BlockCharacter/{name}.glb";
        _renderers[_partCount++] = renderer;
        return part;
    }

    private void CreateRig()
    {
        var rig = Scene.CreateGameObject("Block explorer");
        rig.Transient = true;
        rig.SetParent(GameObject, false);
        _rig = rig;
        _lastPosition = Transform.Position;
        _partCount = 0;
        _equipment = null;
        _itemMesh = "";
        _shadowOnly = null;
        _torso = Part(rig, "torso", new Vector3(0, .69f, 0));
        _head = Part(_torso, "head", new Vector3(0, .47f, 0));
        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1 : 1;
            _arms[i] = Part(_torso, "upper_arm", new Vector3(side * .3f, .425f, 0));
            _elbows[i] = Part(_arms[i], "forearm", new Vector3(0, -.25f, 0));
            _legs[i] = Part(rig, "thigh", new Vector3(side * .123f, .69f, 0));
            _knees[i] = Part(_legs[i], "shin", new Vector3(0, -.335f, 0));
        }
    }

    private void ShowRemoteEquipment(string meshPath, bool weapon)
    {
        if (meshPath.Length == 0)
        {
            if (_itemMesh.Length != 0 && _equipment is { IsValid: true } equipped)
                equipped.SetActive(false);
            _itemMesh = "";
            return;
        }
        if (_equipment is not { IsValid: true })
        {
            var item = Scene.CreateGameObject("Equipped block model");
            item.Transient = true;
            item.SetParent(_elbows[1], false);
            item.Transform.LocalPosition = new Vector3(0, -.228f, .062f);
            item.Transform.LocalScale = new Vector3(.63f);
            item.AddComponent<MeshRenderer>();
            _equipment = item;
        }
        if (_itemMesh == meshPath) return;
        var equipment = _equipment.Value;
        equipment.GetComponent<MeshRenderer>().Mesh = meshPath;
        equipment.Transform.LocalRotation =
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, weapon ? 1.28f : 1.52f);
        equipment.SetActive(true);
        _itemMesh = meshPath;
    }

    public override void LocalUpdate(float deltaTime)
    {
        if (_rig is not { IsValid: true })
            CreateRig();
        var rig = _rig!.Value;

        bool shadowOnly = !Scene.IsNetworked || GameObject.OwnerClientId == Scene.LocalClientId;
        if (_shadowOnly != shadowOnly)
        {
            foreach (var renderer in _renderers)
            {
                renderer.CastShadows = true;
                renderer.ShadowOnly = shadowOnly;
            }
            _shadowOnly = shadowOnly;
        }

        float dt = Math.Clamp(deltaTime, .001f, .1f);
        float blend = 1f - MathF.Exp(-12f * dt);
        Vector3 position = Transform.Position;
        Vector3 motion = position - _lastPosition;
        _lastPosition = position;
        float speed = motion.LengthSquared() > 4f || deltaTime > .25f ? 0f :
            new Vector2(motion.X, motion.Z).Length() / dt;
        _speed += (Math.Min(speed, 11f) - _speed) * blend;

        var controller = GetCharacterController();
        float height = shadowOnly && controller is { HasCharacter: true } local
            ? local.Height : CapsuleHeight;
        bool jumping = shadowOnly && controller is { HasCharacter: true } character
            ? !character.IsGrounded && (character.Velocity.Y > .55f || character.AirTime > .12f)
            : Airborne;
        _crouch += ((height < 1.85f ? 1f : 0f) - _crouch) * blend;
        _jump += ((jumping ? 1f : 0f) - _jump) * blend;

        float strideWeight = Math.Clamp(_speed / 3f, 0, 1) * (1 - _jump);
        float run = Math.Clamp((_speed - 4f) / 5f, 0, 1);
        _stride = (_stride + dt * (4.5f + Math.Min(_speed, 9f) * 1.45f)) % MathF.Tau;
        _idle = (_idle + dt * 2.8f) % MathF.Tau;
        float bob = MathF.Sin(_idle) * .009f +
            MathF.Abs(MathF.Sin(_stride)) * .04f * strideWeight;
        rig.Transform.LocalPosition = new Vector3(0, -height * .5f + bob, 0);
        rig.Transform.LocalScale = new Vector3(1, 1 - .26f * _crouch, 1);
        _torso.Transform.LocalRotation = Quaternion.CreateFromYawPitchRoll(
            MathF.Sin(_stride) * .09f * strideWeight, .24f * _crouch + .15f * run, 0);
        _head.Transform.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -.2f * _crouch);

        _editor ??= GameObject.GetComponent<BlockEditor>();
        int weapon = _editor?.TestWeapon ?? 0;
        int shots = _editor?.TestShotCount ?? 0;
        _shotPose = shots != _lastShot ? 1 : MathF.Max(0, _shotPose - dt * 7);
        _lastShot = shots;
        ShowRemoteEquipment(shadowOnly ? "" :
            BuildPreview.ItemMesh(_editor?.VisibleTool ?? MiningTool.Hand, weapon), weapon != 0);

        bool mining = _editor?.MiningActive == true;
        _mining = mining ? (_mining + dt * 2.4f) % 1f : 0f;
        float strike = MathF.Sin(_mining * MathF.PI);
        for (int i = 0; i < 2; i++)
        {
            float cycle = i == 0 ? _stride : _stride + MathF.PI;
            float swing = MathF.Sin(cycle) * strideWeight;
            _arms[i].Transform.LocalRotation = Quaternion.CreateFromYawPitchRoll(0,
                -.48f * swing - .6f * _jump - (i == 1 ? 1.15f * strike : 0),
                i == 0 ? -.08f : .08f);
            _elbows[i].Transform.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX,
                -.12f - .4f * run - (i == 1 ? .6f * strike : 0));
            if (i == 1 && weapon != 0)
            {
                _arms[i].Transform.LocalRotation = Quaternion.CreateFromYawPitchRoll(
                    -.13f, -1.02f - .2f * _shotPose, .12f);
                _elbows[i].Transform.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -.29f);
            }
            _legs[i].Transform.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX,
                (.57f + .2f * run) * swing - .47f * _crouch + (i == 0 ? -.5f : .25f) * _jump);
            _knees[i].Transform.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX,
                MathF.Max(0, -swing) * .65f + .72f * _crouch + .47f * _jump);
        }
    }

    public override void OnDestroy()
    {
        if (_rig is { IsValid: true } rig) rig.Destroy();
    }
}
