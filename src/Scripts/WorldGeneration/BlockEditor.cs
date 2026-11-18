using System;
using System.Numerics;
using Tallow;

namespace WorldGeneration;

/// <summary>
/// Server-authoritative mining/placing on the NETWORKED player. Selections
/// are part of server-side player state, never trusted world coordinates.
/// </summary>
public class BlockEditor : Script
{
    public float Reach = 6f;
    public float EyeHeight = 0.7f;
    public float EditCooldown = 0.15f;

    // Also editable in the player prefab Inspector for testing each shape.
    public int PlaceType = 3;  // 1 grass, 2 dirt, 3 stone, 4 wood, 5 forge, 6 planks
    public int PlaceShape = 0; // 0 cube, 1 slab, 2 stair, 3 half wall

    private BlockWorld? _world;
    private float _cooldown;

    public override void Update(TickInput input, float deltaTime)
    {
        if (!Scene.IsServer)
            return;

        // Bind Ability1/Ability2 in the project's network input settings.
        if (input.Pressed(InputButton.Ability1))
            PlaceType = Math.Clamp(PlaceType, 1, 6) % 6 + 1;
        if (input.Pressed(InputButton.Ability2))
            PlaceShape = (Math.Clamp(PlaceShape, 0, 3) + 1) % 4;

        _cooldown = MathF.Max(0f, _cooldown - deltaTime);
        bool breaking = input.Pressed(InputButton.Fire);
        bool placing = input.Pressed(InputButton.Use);
        if ((!breaking && !placing) || _cooldown > 0f)
            return;

        _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        if (_world == null)
            return;

        Vector3 origin = Transform.Position + Vector3.UnitY * EyeHeight;
        Vector3 direction = Vector3.Transform(Vector3.UnitZ, input.LookRotation);
        int facing = BlockState.FacingTowardPlayer(direction);
        BlockState selected = BlockState.Create(Math.Clamp(PlaceType, 1, 6),
            (BlockShape)Math.Clamp(PlaceShape, 0, 3), facing, input.Crouch);

        if (_world.TryEditFromRay(origin, direction, Math.Clamp(Reach, 1f, 8f),
            breaking, GameObject, selected.Packed))
            _cooldown = MathF.Max(0.05f, EditCooldown);
    }
}
