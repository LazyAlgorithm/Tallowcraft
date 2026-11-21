using System;
using System.Numerics;
using Tallow;

namespace WorldGeneration;

/// <summary>
/// Local placement previews using the same chunk ray query as server edits.
/// A red outline marks the mining target. A translucent mesh shows the
/// resolved placement, including facing, stair corners and slab merges.
/// </summary>
public class BuildPreview : Script
{
    public float Reach = 6f;
    public float EyeHeight = 0.7f;

    private BlockWorld? _world;
    private BlockEditor? _editor;
    private GameObject? _camera;
    private GameObject? _held;
    private int _shownModel;
    private GameObject? _flash;
    private GameObject? _impact;
    private int _seenShots;
    private float _recoil, _pulseAge, _idle;
    private float _swing;
    private bool _swinging;
    private static readonly string[] ToolMeshes = { "",
        "Models/Tools/stone_pick.glb", "Models/Tools/stone_axe.glb", "Models/Tools/stone_spade.glb",
        "Models/Tools/iron_pick.glb", "Models/Tools/iron_axe.glb", "Models/Tools/iron_spade.glb",
        "Models/Tools/spark_pistol.glb", "Models/Tools/pulse_repeater.glb" };

    public static string ItemMesh(MiningTool tool, int weapon) =>
        ToolMeshes[weapon is 1 or 2 ? 6 + weapon : (int)tool];

    private GameObject ShowHeld(GameObject camera, int model)
    {
        if (_held is not { IsValid: true })
        {
            var obj = Scene.CreateGameObject("Held equipment (local)");
            obj.Transient = true;
            obj.SetLayer(30);
            obj.SetParent(camera, false);
            var mesh = obj.AddComponent<MeshRenderer>();
            mesh.CastShadows = false;
            obj.Transform.LocalScale = new Vector3(.46f);
            _held = obj;
            _shownModel = 0;
        }
        var held = _held.Value;
        if (model != _shownModel)
        {
            held.GetComponent<MeshRenderer>().Mesh = ToolMeshes[model];
            held.SetActive(true);
            _shownModel = model;
        }
        return held;
    }

    private GameObject PulseEffect(ref GameObject? effect, string name)
    {
        if (effect is { IsValid: true } ready) return ready;
        var obj = Scene.CreateGameObject(name);
        obj.Transient = true;
        var mesh = obj.AddComponent<MeshRenderer>();
        mesh.Mesh = "builtin:cube";
        mesh.CastShadows = false;
        mesh.Unlit = true;
        effect = obj;
        return obj;
    }

    private void ShowTestWeapon(GameObject camera, BlockEditor editor, float dt)
    {
        bool pistol = editor.TestWeapon == 1;
        int model = 6 + editor.TestWeapon;
        if (model != _shownModel)
        {
            _recoil = 0;
            _pulseAge = 1;
            _seenShots = editor.TestShotCount;
            _swinging = false;
            _swing = 0;
        }
        var held = ShowHeld(camera, model);
        _pulseAge += dt;
        _recoil = MathF.Max(0, _recoil - dt * (pistol ? 4.5f : 8f));
        if (_seenShots != editor.TestShotCount)
        {
            _seenShots = editor.TestShotCount;
            _recoil = 1;
            _pulseAge = 0;
            Color color = pistol ? new Color(1f, .68f, .23f) : new Color(.36f, 1f, .83f);
            var flash = PulseEffect(ref _flash, "Pulse muzzle flash (local)");
            flash.SetParent(held, false);
            flash.SetLayer(30);
            flash.Transform.LocalPosition = new Vector3(0, .12f, pistol ? .56f : .83f);
            flash.Transform.LocalScale = new Vector3(.13f, .13f, .19f);
            flash.GetComponent<MeshRenderer>().Color = color;
            flash.SetActive(true);
            if (_impact is { IsValid: true } old) old.SetActive(false);
            foreach (var hit in Scene.RaycastAll(camera.Transform.Position, camera.Transform.Forward, 30f))
            {
                if (hit.HitObject is not { } target || target.Id == GameObject.Id)
                    continue;
                var impact = PulseEffect(ref _impact, "Pulse contact (local)");
                impact.Transform.Position = hit.Point - camera.Transform.Forward * .045f;
                impact.Transform.LocalScale = new Vector3(.10f);
                impact.GetComponent<MeshRenderer>().Color = color;
                impact.SetActive(true);
                break;
            }
            Audio.PlayOneShot2D(pistol ? "Audio/spark.wav" : "Audio/pulse.wav", .55f);
        }
        if (_pulseAge > .065f && _flash is { IsValid: true } muzzle) muzzle.SetActive(false);
        if (_pulseAge > .15f && _impact is { IsValid: true } mark) mark.SetActive(false);

        float kick = _recoil * _recoil;
        held.Transform.LocalPosition = new Vector3(.29f, -.22f + MathF.Sin(_idle) * .007f, .66f - .09f * kick);
        held.Transform.LocalRotation = Quaternion.CreateFromYawPitchRoll(-.12f,
            -.04f - kick * (pistol ? .36f : .16f), -.08f - .05f * kick);
    }

    private void ShowTool(GameObject camera, MiningTool tool, float dt)
    {
        if (tool == MiningTool.Hand)
        {
            HideTool();
            return;
        }
        var held = ShowHeld(camera, (int)tool);
        // Complete each stroke even when soft terrain breaks in a single tick.
        bool mining = _editor!.MiningActive || (_targeted && InputActions.Held("Player/Fire"));
        if (!_swinging && mining)
        {
            _swinging = true;
            Audio.PlayOneShot2D("Audio/strike.wav", .35f);
        }
        bool axe = tool is MiningTool.Axe or MiningTool.IronAxe;
        bool spade = tool is MiningTool.Shovel or MiningTool.IronShovel;
        if (_swinging)
        {
            _swing += dt * (axe ? 2.1f : spade ? 2.7f : 2.4f);
            if (_swing >= 1f)
            {
                _swing = 0;
                _swinging = false;
            }
        }
        float arc = MathF.Sin(_swing * MathF.PI);
        held.Transform.LocalPosition = new Vector3(.29f - .045f*arc, -.205f - .02f*arc + MathF.Sin(_idle) * .009f, .68f + .065f*arc);
        // The pick point and axe edge are modeled along local +X. Turn
        // them toward the target, then swing the entire grip in camera space.
        float gripYaw = tool switch
        {
            MiningTool.Pickaxe or MiningTool.IronPickaxe => -2.14f,
            MiningTool.Axe or MiningTool.IronAxe => -2.28f,
            _ => -.36f // Keep the shovel's broad face visible.
        };
        var swing = Quaternion.CreateFromYawPitchRoll(
            axe ? -.29f * arc : 0,
            .16f + arc * (spade ? .62f : .86f),
            .32f + arc * (axe ? .35f : .12f));
        held.Transform.LocalRotation = swing * Quaternion.CreateFromAxisAngle(Vector3.UnitY, gripYaw);
    }

    private void HideTool()
    {
        if (_shownModel != 0 && _held is { IsValid: true } held) held.SetActive(false);
        _shownModel = 0;
        if (_flash is { IsValid: true } muzzle) muzzle.SetActive(false);
        if (_impact is { IsValid: true } mark) mark.SetActive(false);
        _swinging = false;
        _swing = 0;
    }

    private bool _targeted;
    private bool _canPlace;
    private BlockPosition _remove;
    private BlockPosition _place;
    private BlockState _selected;
    private BlockState _placeState;

    // One tiny, local-only renderer. Never replicate it, add collision, or
    // rebuild an entire terrain chunk to show cosmetic mining damage.
    private GameObject? _damageObject;
    private MeshRenderer _damageRenderer;
    private BlockPosition _damagePosition;
    private ushort _damageBlock;
    private int _damageStage = -1;
    private bool _damageVisible;

    private bool IsLocal =>
        !Scene.IsNetworked || GameObject.OwnerClientId == Scene.LocalClientId;

    private void HideDamage()
    {
        if (_damageObject is { IsValid: true } overlay && _damageVisible)
            overlay.SetActive(false);
        _damageVisible = false;
        _damageStage = -1;
    }

    private void ShowDamage(BlockPosition position, ushort block, int percent)
    {
        int stage = BlockDamageMesh.StageForPercent(percent);
        if (stage < 0 || block == 0)
        {
            HideDamage();
            return;
        }

        if (_damageObject is not { IsValid: true })
        {
            _damageObject = Scene.CreateGameObject("Block break cracks (local)");
            _damageRenderer = _damageObject.Value.AddComponent<MeshRenderer>();
            _damageRenderer.CastShadows = false;
            _damageRenderer.Unlit = true;
            // The crack shell is intentionally two-sided: a few partial
            // block faces have opposite winding to the terrain mesh.
            _damageRenderer.BackFaceCulling = false;
            _damageRenderer.Material = "Materials/block_break_damage.tmat";
            _damageRenderer.Texture = "Textures/block_cracks.png";
            _damageObject.Value.SetActive(false);
            _damageVisible = false;
            _damageStage = -1;
        }

        GameObject overlay = _damageObject.Value;
        bool newTarget = !_damageVisible || _damagePosition != position ||
            _damageBlock != block;
        // Update only on target/shape changes or when a 10% damage stage
        // advances. Sampling the atlas with UVs is reliable even if an
        // optional custom Shader Graph is unavailable on this platform.
        if (newTarget || _damageStage != stage)
        {
            BlockDamageMesh mesh = BlockDamageMesh.Create(
                new BlockState(block), stage);
            if (mesh.FaceCount == 0)
            {
                HideDamage();
                return;
            }
            _damageRenderer.SetMesh(mesh.Positions, mesh.Triangles,
                mesh.Normals, mesh.Uvs);
            if (newTarget)
            {
                overlay.Transform.Position = new Vector3(
                    position.X + .5f, position.Y + .5f, position.Z + .5f);
                _damagePosition = position;
                _damageBlock = block;
            }
            _damageStage = stage;
        }
        if (!_damageVisible)
        {
            overlay.SetActive(true);
            _damageVisible = true;
        }
    }

    private GameObject? _ghost;
    private MeshRenderer _ghostRenderer;
    private ushort _ghostState;
    private bool? _ghostAllowed;
    private bool _ghostVisible;

    private void HideGhost()
    {
        if (_ghostVisible && _ghost is { IsValid: true } ghost)
            ghost.SetActive(false);
        _ghostVisible = false;
    }

    private void ShowGhost(BlockPosition target, BlockState state, bool allowed)
    {
        if (_ghost is not { IsValid: true })
        {
            _ghost = Scene.CreateGameObject("Placement preview (local)");
            var obj = _ghost.Value;
            obj.Transient = true;
            _ghostRenderer = _ghost.Value.AddComponent<MeshRenderer>();
            _ghostRenderer.CastShadows = false;
            _ghostState = 0;
            _ghostAllowed = null;
        }
        if (_ghostState != state.Packed)
        {
            var mesh = BlockDamageMesh.Create(state, 0);
            _ghostRenderer.SetMesh(mesh.Positions, mesh.Triangles, mesh.Normals, mesh.Uvs);
            _ghostState = state.Packed;
        }
        if (_ghostAllowed != allowed)
        {
            _ghostRenderer.Material = allowed ? "Materials/place_valid.tmat" : "Materials/place_blocked.tmat";
            _ghostAllowed = allowed;
        }
        var ghost = _ghost.Value;
        ghost.Transform.Position = new Vector3(target.X + .5f, target.Y + .5f, target.Z + .5f);
        if (!_ghostVisible) ghost.SetActive(true);
        _ghostVisible = true;
    }

    public override void OnDestroy()
    {
        if (_damageObject is { IsValid: true } overlay)
            overlay.Destroy();
        if (_ghost is { IsValid: true } ghost) ghost.Destroy();
        _damageObject = null;
        if (_held is { IsValid: true } held) held.Destroy();
        if (_impact is { IsValid: true } mark) mark.Destroy();
    }

    public override void LocalUpdate(float deltaTime)
    {
        _targeted = false;
        if (!IsLocal)
        {
            if (_shownModel != 0) HideTool();
            HideGhost();
            HideDamage();
            return;
        }

        _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        _editor ??= GameObject.GetComponent<BlockEditor>();
        if (_camera is not { IsValid: true })
            _camera = Scene.Find("Main Camera");
        if (_world == null || _editor == null || _editor.IsInventoryVisible ||
            _editor.UiGameMenuVisible || _editor.ReturnToSpawnLocked ||
            _camera is not { IsValid: true } camera)
        {
            HideTool();
            HideGhost();
            HideDamage();
            return;
        }

        float dt = Math.Clamp(deltaTime, 0, .1f);
        _idle = (_idle + dt * 3.2f) % MathF.Tau;
        if (_editor.TestWeapon != 0)
        {
            HideGhost();
            HideDamage();
            ShowTestWeapon(camera, _editor, dt);
            return;
        }
        if (_shownModel > 6) HideTool();

        var look = camera.Transform.Forward;
        Vector3 origin = Transform.Position + Vector3.UnitY * EyeHeight;
        _targeted = _world.TryQueryFromRay(origin, look,
            Math.Clamp(Reach, 1f, 8f), GameObject, out _remove,
            out BlockPosition adjacent, out Vector3 hitNormal, out float hitY);
        ShowTool(camera, _editor.UiTools.Equipped, dt);
        if (!_targeted)
        {
            HideGhost();
            HideDamage();
            return;
        }

        bool crouching = InputActions.Held("Player/Crouch");
        _selected = BlockPicker.SelectedState(_editor.PlaceType,
            Math.Clamp(_editor.PlaceShape, 0, BlockState.MaxShape), _editor.FacingOffset,
            _editor.PickedAbsoluteFacing, _editor.PickedUpper, crouching,
            BlockState.FacingTowardPlayer(look));
        bool planned = _world.TryPlanPlacement(_remove, adjacent,
            hitNormal, _selected, out _place, out ushort replacement,
            _editor.PickedAbsoluteFacing ? null : hitY);
        _placeState = planned ? new BlockState(replacement) : _selected;
        _canPlace = planned && _editor.CanPlaceSelected &&
            _world.CanPlaceAt(_place, replacement);

        if (_editor.MiningActive &&
            _editor.MiningX == _remove.X &&
            _editor.MiningY == _remove.Y &&
            _editor.MiningZ == _remove.Z &&
            _editor.MiningType ==
                new BlockState(_world.BlockAt(_remove)).Type)
            ShowDamage(_remove, _world.ResolveVisualShape(
                    _remove, new BlockState(_world.BlockAt(_remove))).Packed,
                _editor.MiningPercent);
        else
            HideDamage();

        DrawShape(_remove, _world.ResolveVisualShape(
                _remove, new BlockState(_world.BlockAt(_remove))),
            new Color(1f, .25f, .18f, 1f));
        if (planned)
            ShowGhost(_place, _world.ResolveVisualShape(_place, _placeState), _canPlace);
        else
            HideGhost();
    }

    private static void DrawOutline(BlockPosition at, Color color)
    {
        DebugDraw.Box(new Vector3(at.X + .5f, at.Y + .5f, at.Z + .5f),
            new Vector3(1.012f), color);
    }

    private static void DrawShape(BlockPosition at, BlockState state, Color color)
    {
        // A cube is one wire box. Non-cubic blocks use their actual eight
        // half-cell occupancy values so the shape/rotation preview agrees
        // with the mesher rather than displaying a misleading full cube.
        if (state.Shape == BlockShape.Cube)
        {
            DrawOutline(at, color);
            return;
        }

        for (int y = 0; y < 2; y++)
            for (int z = 0; z < 2; z++)
                for (int x = 0; x < 2; x++)
                    if (state.ContainsMicro(x, y, z))
                        DebugDraw.Box(
                            new Vector3(at.X + .25f + x * .5f,
                                at.Y + .25f + y * .5f,
                                at.Z + .25f + z * .5f),
                            new Vector3(.502f), color);
    }

    internal string TargetHint()
    {
        if (_editor is not { } editor || _world == null || editor.UiGameMenuVisible ||
            editor.IsInventoryVisible || editor.ReturnToSpawnLocked) return "";
        if (editor.TestWeapon != 0)
            return editor.TestWeapon == 1 ? "LMB   Fire    ·    Z   Holster" : "Hold LMB   Fire    ·    X   Holster";
        if (!_targeted) return "";
        if (new BlockState(_world.BlockAt(_remove)).Type == BlockInventory.Chest)
            return $"E   Open chest   ·   {_remove.X}, {_remove.Y}, {_remove.Z}";
        return _canPlace ? $"LMB   Place {_placeState.Shape}     ·     RMB   Mine" :
            editor.CanPlaceSelected ? "Placement blocked   ·   RMB   Mine" : "No material   ·   RMB   Mine";
    }
}
