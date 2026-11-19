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

    // Keep unrestricted building for existing test worlds. Uncheck in the
    // player's BlockEditor Inspector to enable material-limited survival.
    [Sync] public bool CreativeMode = true;
    [Sync] public string InventoryState = "";

    private static readonly string[] BlockNames =
        { "Grass", "Dirt", "Stone", "Log", "Forge", "Planks" };
    private static readonly string[] ShapeNames =
        { "Cube", "Slab", "Stair", "Half wall" };

    private BlockWorld? _world;
    private float _cooldown;
    private readonly BlockInventory _inventory = new();
    private readonly BlockInventory _displayInventory = new();
    private string _displaySnapshot = "";

    public override void Start()
    {
        if (!Scene.IsServer) return;
        _inventory.FillStarter();
        InventoryState = _inventory.Serialize();
    }

    /// <summary>Client HUD/preview reads only the replicated, validated data.</summary>
    public int AvailableCount(int type)
    {
        if (InventoryState != _displaySnapshot)
        {
            _displayInventory.TryLoad(InventoryState);
            _displaySnapshot = InventoryState;
        }
        return _displayInventory.Count(type);
    }

    public bool CanPlaceSelected => CreativeMode || AvailableCount(PlaceType) > 0;

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

        // The server validates quantity BEFORE the edit, then charges only
        // after the world confirms the voxel was actually changed.
        if (placing && !CreativeMode && !_inventory.CanSpend(PlaceType))
            return;

        if (_world.TryEditFromRay(origin, direction, Math.Clamp(Reach, 1f, 8f),
            breaking, GameObject, selected.Packed,
            out ushort previous, out _))
        {
            if (breaking)
            {
                // First pass: collect directly rather than spawning hundreds
                // of networked physics pickups in destructible terrain.
                if (_inventory.TryCollect(new BlockState(previous).Type))
                    InventoryState = _inventory.Serialize();
            }
            else if (!CreativeMode && _inventory.TrySpend(PlaceType))
                InventoryState = _inventory.Serialize();

            _cooldown = MathF.Max(0.05f, EditCooldown);
        }
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
        float top = MathF.Max(20f, GUI.Height - 126f);

        // Visual-only overlay; all game edits still originate in TickInput.
        GUI.Box(left - 10, top - 37, total + 20, 151, new Color(0.03f, 0.04f, 0.06f, 0.72f));
        GUI.Label(left + 8, top - 31, "TALLOWCRAFT        LMB PLACE   RMB REMOVE");
        for (int slot = 0; slot < 6; slot++)
        {
            float x = left + slot * (width + gap);
            bool active = selected == slot + 1;
            GUI.Box(x, top, width, 45, active
                ? new Color(.33f, .45f, .22f, .95f)
                : new Color(.13f, .15f, .18f, .95f));
            GUI.Label(x + 6, top + 4, $"{slot + 1} {BlockNames[slot]}");
            GUI.Label(x + 6, top + 23, CreativeMode
                ? "FREE" : $"x{AvailableCount(slot + 1)}");
        }
        GUI.Label(left + 4, top + 49,
            $"{BlockNames[selected - 1]}  •  {ShapeNames[shape]}  •  rotation {FacingOffset * 90}°");
        GUI.Label(left + 4, top + 66, "1-6 select   Wheel cycle   Q shape   F rotate   Ctrl upper");
        GUI.Label(left + 4, top + 85,
            $"Collected: Coal {AvailableCount(7)}   Iron {AvailableCount(8)}   Leaves {AvailableCount(9)}");
        GUI.Label(left + 4, top + 103, CreativeMode
            ? "Creative building ON  •  disable CreativeMode for survival inventory"
            : "Survival building ON  •  mining adds one item; placing spends one");
        GUI.Label(GUI.Width * .5f - 4f, GUI.Height * .5f - 8f, "+");
    }
}
