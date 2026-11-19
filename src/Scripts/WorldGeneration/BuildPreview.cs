using System;
using System.Numerics;
using Tallow;

namespace WorldGeneration;

/// <summary>
/// Local-only wire previews using the same chunk ray query as server edits.
/// Red highlights the block to remove; green draws the actual half-cell
/// occupancy of the selected cube/slab/stair/half-wall at the placement spot.
/// The preview has no colliders, does not spawn network objects, and never
/// changes the world. Actual placement is still validated by the server.
/// </summary>
public class BuildPreview : Script
{
    public float Reach = 6f;
    public float EyeHeight = 0.7f;

    private BlockWorld? _world;
    private BlockEditor? _editor;
    private GameObject? _camera;
    private bool _targeted;
    private bool _canPlace;
    private BlockPosition _remove;
    private BlockPosition _place;
    private BlockState _selected;
    private BlockState _placeState;

    private bool IsLocal =>
        !Scene.IsNetworked || GameObject.OwnerClientId == Scene.LocalClientId;

    public override void LocalUpdate(float deltaTime)
    {
        _targeted = false;
        if (!IsLocal)
            return;

        _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        _editor ??= GameObject.GetComponent<BlockEditor>();
        if (_camera is not { IsValid: true })
            _camera = Scene.Find("Main Camera");
        if (_world == null || _editor == null || _editor.InventoryOpen ||
            _camera is not { IsValid: true } camera)
            return;

        var look = camera.Transform.Forward;
        Vector3 origin = Transform.Position + Vector3.UnitY * EyeHeight;
        _targeted = _world.TryQueryFromRay(origin, look,
            Math.Clamp(Reach, 1f, 8f), GameObject, out _remove,
            out BlockPosition adjacent, out Vector3 hitNormal);
        if (!_targeted)
            return;

        int facing = (BlockState.FacingTowardPlayer(look) +
            _editor.FacingOffset) & 3;
        // Only the presentation samples a raw key. Gameplay itself uses
        // TickInput.Crouch on the server, including for networked clients.
        bool upper = Input.GetKey(KeyCode.LeftControl) ||
            Input.GetKey(KeyCode.RightControl);
        _selected = BlockState.Create(Math.Clamp(_editor.PlaceType, 1, 6),
            (BlockShape)Math.Clamp(_editor.PlaceShape, 0, 3), facing, upper);
        bool planned = _world.TryPlanPlacement(_remove, adjacent,
            hitNormal, _selected, out _place, out ushort replacement);
        _placeState = planned ? new BlockState(replacement) : _selected;
        _canPlace = planned && _editor.CanPlaceSelected &&
            _world.CanPlaceAt(_place, replacement);

        DrawShape(_remove, new BlockState(_world.BlockAt(_remove)),
            new Color(1f, .25f, .18f, 1f));
        DrawShape(_place, _placeState,
            _canPlace ? new Color(.25f, 1f, .35f, 1f) :
                new Color(1f, .62f, .16f, 1f));
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

    public override void OnGUI()
    {
        if (!IsLocal || !_targeted)
            return;
        string blockName = _selected.Type switch
        {
            1 => "Grass", 2 => "Dirt", 3 => "Stone",
            4 => "Log", 5 => "Forge", 6 => "Planks",
            _ => "Block"
        };
        string shapeName = _placeState.Shape.ToString();
        bool stacking = _selected.Shape == BlockShape.Slab &&
            _placeState.Shape == BlockShape.Cube;
        GUI.Box(12, 12, 380, 54, new Color(.035f, .045f, .055f, .75f));
        GUI.Label(22, 18,
            $"RMB REMOVE  {_remove.X}, {_remove.Y}, {_remove.Z}");
        GUI.Label(22, 40, _canPlace
            ? stacking
                ? $"LMB STACK SLAB → FULL  {blockName}  {_place.X}, {_place.Y}, {_place.Z}"
                : $"LMB PLACE  {blockName} {shapeName}  {_place.X}, {_place.Y}, {_place.Z}"
            : !_editor.CanPlaceSelected
                ? "LMB PLACE  not enough materials"
                : "LMB PLACE  blocked");
    }
}
