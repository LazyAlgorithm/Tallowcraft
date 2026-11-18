using System;
using System.Numerics;
using Tallow;

namespace WorldGeneration;

/// <summary>
/// Server-authoritative block editing. Hotbar controls travel in the network
/// TickInput as Custom1..8, Q (shape) and F (rotate), never local raw Input.
/// OnGUI displays only for the locally controlled player.
/// </summary>
public class BlockEditor : Script
{
    public float Reach = 6f;
    public float EyeHeight = 0.7f;
    public float EditCooldown = 0.15f;

    [Sync] public int PlaceType = 3;  // 1 grass, 2 dirt, 3 stone, 4 log, 5 forge, 6 planks
    [Sync] public int PlaceShape = 0; // 0 cube, 1 slab, 2 stair, 3 half wall
    [Sync] public int FacingOffset = 0;

    private static readonly string[] BlockNames =
        { "Grass", "Dirt", "Stone", "Log", "Forge", "Planks" };
    private static readonly string[] ShapeNames =
        { "Cube", "Slab", "Stair", "Half wall" };

    private BlockWorld? _world;
    private float _cooldown;

    public override void Update(TickInput input, float deltaTime)
    {
        // Only the server commits selection and edits. All selection input
        // arrives via Tallow's network transport and is checked here.
        if (!Scene.IsServer)
            return;

        for (int i = 0; i < 6; i++)
            if (input.Pressed((InputButton)((int)InputButton.Custom1 + i)))
                PlaceType = i + 1;
        if (input.Pressed(InputButton.Custom7))
            PlaceType = PlaceType % 6 + 1;
        if (input.Pressed(InputButton.Custom8))
            PlaceType = (PlaceType + 4) % 6 + 1;
        if (input.Pressed(InputButton.Ability1))
            PlaceShape = (PlaceShape + 1) % 4;
        if (input.Pressed(InputButton.Ability2))
            FacingOffset = (FacingOffset + 1) % 4;

        // Validate even when fields were modified externally.
        PlaceType = Math.Clamp(PlaceType, 1, 6);
        PlaceShape = Math.Clamp(PlaceShape, 0, 3);
        FacingOffset &= 3;

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
        int facing = (BlockState.FacingTowardPlayer(direction) + FacingOffset) & 3;
        BlockState selected = BlockState.Create(PlaceType,
            (BlockShape)PlaceShape, facing, input.Crouch);

        if (_world.TryEditFromRay(origin, direction, Math.Clamp(Reach, 1f, 8f),
            breaking, GameObject, selected.Packed))
            _cooldown = MathF.Max(0.05f, EditCooldown);
    }

    public override void OnGUI()
    {
        if (Scene.IsNetworked && GameObject.OwnerClientId != Scene.LocalClientId)
            return;

        int selected = Math.Clamp(PlaceType, 1, BlockNames.Length);
        int shape = Math.Clamp(PlaceShape, 0, ShapeNames.Length - 1);
        const float width = 63f, gap = 5f;
        float total = 6 * width + 5 * gap;
        float left = MathF.Max(8f, (GUI.Width - total) * 0.5f);
        float top = MathF.Max(20f, GUI.Height - 91f);

        // Visual-only overlay; all game edits still originate in TickInput.
        GUI.Box(left - 10, top - 37, total + 20, 119, new Color(0.03f, 0.04f, 0.06f, 0.72f));
        GUI.Label(left + 8, top - 31, "TALLOWCRAFT        LMB PLACE   RMB REMOVE");
        for (int slot = 0; slot < 6; slot++)
        {
            float x = left + slot * (width + gap);
            bool active = selected == slot + 1;
            GUI.Box(x, top, width, 45, active
                ? new Color(.33f, .45f, .22f, .95f)
                : new Color(.13f, .15f, .18f, .95f));
            GUI.Label(x + 6, top + 4, $"{slot + 1} {BlockNames[slot]}");
            if (active) GUI.Label(x + 25, top + 23, "SELECT");
        }
        GUI.Label(left + 4, top + 49,
            $"{BlockNames[selected - 1]}  •  {ShapeNames[shape]}  •  rotation {FacingOffset * 90}°");
        GUI.Label(left + 4, top + 66, "1-6 select   Wheel cycle   Q shape   F rotate   Ctrl upper");
        GUI.Label(GUI.Width * .5f - 4f, GUI.Height * .5f - 8f, "+");
    }
}
