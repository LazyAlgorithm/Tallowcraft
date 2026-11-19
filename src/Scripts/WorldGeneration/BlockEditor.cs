using System;
using System.Numerics;
using Tallow;

namespace WorldGeneration;

/// <summary>
/// Server-authoritative placement, mining and crafting. Block edits and all
/// inventory mutations are performed on the server; clients receive [Sync]
/// fields and may only request a recipe through an owner-restricted RPC.
/// </summary>
public class BlockEditor : Script
{
    public float Reach = 6f;
    public float EyeHeight = 0.7f;
    public float EditCooldown = 0.15f;

    [Sync] public int PlaceType = 3;
    [Sync] public int PlaceShape = 0;
    [Sync] public int FacingOffset = 0;
    [Sync] public bool CreativeMode = true;
    [Sync] public string InventoryState = "";
    [Sync] public bool InventoryOpen = false;
    // Quantized to ten-percent steps to avoid flooding player snapshots.
    [Sync] public int MiningPercent = 0;
    [Sync] public int MiningType = 0;

    private static readonly string[] BlockNames =
    {
        "Grass", "Dirt", "Stone", "Log", "Forge", "Planks",
        "Coal", "Iron", "Leaves"
    };
    private static readonly string[] ShapeNames =
        { "Cube", "Slab", "Stair", "Half wall" };

    private BlockWorld? _world;
    private float _cooldown;
    private float _craftCooldown;
    private readonly BlockInventory _inventory = new();
    private readonly BlockInventory _displayInventory = new();
    private string _displaySnapshot = "";
    private bool _miningActive;
    private BlockPosition _miningPosition;
    private ushort _miningState;
    private float _miningElapsed;
    private bool _cursorInitialized;
    private bool _cursorMenuOpen;

    private bool IsLocal =>
        !Scene.IsNetworked || GameObject.OwnerClientId == Scene.LocalClientId;

    public override void Start()
    {
        if (!Scene.IsServer)
            return;
        _inventory.FillStarter();
        InventoryState = _inventory.Serialize();
    }

    public override void LocalUpdate(float deltaTime)
    {
        if (!IsLocal)
            return;

        // InventoryOpen itself is replicated, so UI clicks cannot authorize
        // gameplay. Only the owning peer needs an unlocked pointer.
        if (!_cursorInitialized || _cursorMenuOpen != InventoryOpen)
        {
            Cursor.Locked = !InventoryOpen;
            _cursorMenuOpen = InventoryOpen;
            _cursorInitialized = true;
        }
    }

    /// <summary>Clients read a fully validated server inventory snapshot.</summary>
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

    private void ResetMining()
    {
        _miningActive = false;
        _miningElapsed = 0f;
        if (MiningPercent != 0) MiningPercent = 0;
        if (MiningType != 0) MiningType = 0;
    }

    [Rpc(RpcTarget.Server)]
    private void CloseInventory()
    {
        if (!Scene.IsServer ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId))
            return;
        InventoryOpen = false;
    }

    [Rpc(RpcTarget.Server)]
    private void CraftRecipe(int recipeId)
    {
        if (!Scene.IsServer || !InventoryOpen || _craftCooldown > 0f ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId))
            return;
        // No recipe contents or claimed resources are accepted over RPC.
        if (_inventory.TryCraft(recipeId))
        {
            InventoryState = _inventory.Serialize();
            _craftCooldown = 0.12f;
        }
    }

    public override void Update(TickInput input, float deltaTime)
    {
        if (!Scene.IsServer)
            return;

        _cooldown = MathF.Max(0f, _cooldown - deltaTime);
        _craftCooldown = MathF.Max(0f, _craftCooldown - deltaTime);

        if (input.Pressed(InputButton.Reload))
        {
            InventoryOpen = !InventoryOpen;
            ResetMining();
        }
        if (InventoryOpen)
        {
            ResetMining();
            return;
        }

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

        PlaceType = Math.Clamp(PlaceType, 1, 6);
        PlaceShape = Math.Clamp(PlaceShape, 0, 3);
        FacingOffset &= 3;

        bool breaking = input.Held(InputButton.Fire);
        bool placing = !breaking && input.Pressed(InputButton.Use);
        if (!breaking)
            ResetMining();
        if ((!breaking && !placing) || _cooldown > 0f)
            return;

        _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        if (_world == null)
        {
            ResetMining();
            return;
        }

        Vector3 origin = Transform.Position + Vector3.UnitY * EyeHeight;
        Vector3 direction = Vector3.Transform(Vector3.UnitZ, input.LookRotation);
        int facing = (BlockState.FacingTowardPlayer(direction) + FacingOffset) & 3;
        BlockState selected = BlockState.Create(PlaceType,
            (BlockShape)PlaceShape, facing, input.Crouch);

        if (breaking)
        {
            // Re-query the same authoritative voxel during the hold; aiming at
            // a different block, releasing RMB, or a world edit resets progress.
            if (!_world.TryQueryFromRay(origin, direction,
                    Math.Clamp(Reach, 1f, 8f), GameObject,
                    out BlockPosition target, out _) )
            {
                ResetMining();
                return;
            }
            ushort state = _world.BlockAt(target);
            if (state == 0 || !BlockState.IsValid(state))
            {
                ResetMining();
                return;
            }
            if (!_miningActive || _miningPosition != target || _miningState != state)
            {
                _miningActive = true;
                _miningPosition = target;
                _miningState = state;
                _miningElapsed = 0f;
            }

            _miningElapsed += MathF.Max(0f, deltaTime);
            MiningType = new BlockState(state).Type;
            MiningPercent = BlockMining.Percent(_miningElapsed, state) / 10 * 10;
            if (_miningElapsed < BlockMining.Seconds(state))
                return;
        }
        else if (!CreativeMode && !_inventory.CanSpend(PlaceType))
            return;

        if (_world.TryEditFromRay(origin, direction, Math.Clamp(Reach, 1f, 8f),
            breaking, GameObject, selected.Packed,
            out ushort previous, out _))
        {
            if (breaking)
            {
                // Mining adds directly to the same inventory backing the HUD.
                if (_inventory.TryCollect(new BlockState(previous).Type))
                    InventoryState = _inventory.Serialize();
            }
            else if (!CreativeMode && _inventory.TrySpend(PlaceType))
                InventoryState = _inventory.Serialize();

            _cooldown = MathF.Max(0.05f, EditCooldown);
        }
        if (breaking)
            ResetMining();
    }

    private void DrawInventory()
    {
        const float width = 528f, height = 438f;
        float left = MathF.Max(8f, (GUI.Width - width) * 0.5f);
        float top = MathF.Max(8f, (GUI.Height - height) * 0.5f);
        GUI.Box(left, top, width, height,
            new Color(.035f, .045f, .055f, .96f));
        GUI.Label(left + 18, top + 12, "INVENTORY  /  CRAFTING");
        GUI.Label(left + 252, top + 12, CreativeMode
            ? "CREATIVE BUILDING" : "SURVIVAL BUILDING");
        if (GUI.Button(left + width - 81, top + 8, 65, 28, "Close"))
            Rpc(nameof(CloseInventory));

        for (int i = 0; i < BlockNames.Length; i++)
        {
            int column = i % 3, row = i / 3;
            float x = left + 17 + column * 171;
            float y = top + 50 + row * 62;
            GUI.Box(x, y, 158, 54, new Color(.13f, .16f, .17f, .96f));
            GUI.Label(x + 10, y + 7, BlockNames[i]);
            GUI.Label(x + 10, y + 28, $"x{AvailableCount(i + 1)} / 999");
        }

        GUI.Label(left + 18, top + 244, "RECIPES  (crafting always spends materials)");
        var recipes = CraftingRecipes.All;
        for (int i = 0; i < recipes.Count; i++)
        {
            CraftingRecipe recipe = recipes[i];
            float y = top + 269 + i * 48;
            GUI.Box(left + 17, y, width - 34, 43,
                new Color(.13f, .16f, .17f, .95f));
            string ingredients = $"{recipe.CountA} {BlockNames[recipe.InputA - 1]}";
            if (recipe.InputB != 0)
                ingredients += $" + {recipe.CountB} {BlockNames[recipe.InputB - 1]}";
            GUI.Label(left + 26, y + 5,
                $"{recipe.Name}: {ingredients}");
            GUI.Label(left + 26, y + 24,
                $"Makes {recipe.CountOut} {BlockNames[recipe.Output - 1]}");
            bool available = AvailableCount(recipe.InputA) >= recipe.CountA &&
                (recipe.InputB == 0 || AvailableCount(recipe.InputB) >= recipe.CountB);
            if (GUI.Button(left + width - 112, y + 7, 78, 29,
                    available ? "Craft" : "Need items") && available)
                Rpc(nameof(CraftRecipe), recipe.Id);
        }
        GUI.Label(left + 18, top + 416,
            "TAB closes  •  Mine with RMB  •  Click Craft to request from server");
    }

    public override void OnGUI()
    {
        if (!IsLocal)
            return;

        if (InventoryOpen)
        {
            DrawInventory();
            return;
        }

        int selected = Math.Clamp(PlaceType, 1, 6);
        int shape = Math.Clamp(PlaceShape, 0, ShapeNames.Length - 1);
        const float width = 63f, gap = 5f;
        float total = 6 * width + 5 * gap;
        float left = MathF.Max(8f, (GUI.Width - total) * 0.5f);
        float top = MathF.Max(20f, GUI.Height - 126f);

        GUI.Box(left - 10, top - 37, total + 20, 151,
            new Color(0.03f, 0.04f, 0.06f, 0.72f));
        GUI.Label(left + 8, top - 31,
            "TALLOWCRAFT        LMB PLACE   HOLD RMB TO MINE");
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
        GUI.Label(left + 4, top + 66,
            "1-6 select   Wheel cycle   Q shape   F rotate   TAB inventory");
        GUI.Label(left + 4, top + 85,
            MiningType is >= 1 and <= 9
                ? $"Mining {BlockNames[MiningType - 1]}: {MiningPercent}%"
                : $"Coal {AvailableCount(7)}   Iron {AvailableCount(8)}   Leaves {AvailableCount(9)}");
        GUI.Label(left + 4, top + 103, CreativeMode
            ? "Creative building ON  •  crafting still uses your inventory"
            : "Survival building ON  •  mining collects materials");
        GUI.Label(GUI.Width * .5f - 4f, GUI.Height * .5f - 8f, "+");
    }
}
