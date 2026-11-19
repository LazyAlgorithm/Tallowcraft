using System;
using System.IO;
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
    [Sync] public string ToolState = "";
    [Sync] public bool InventoryOpen = false;
    [Sync] public bool ProfileReady = false;
    [Sync] public bool ProfileError = false;
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
    private float _chunkRequestCooldown;
    private readonly BlockInventory _inventory = new();
    private readonly BlockInventory _displayInventory = new();
    private readonly PlayerTools _tools = new();
    private readonly PlayerTools _displayTools = new();
    private string _displayToolSnapshot = "";
    private string _displaySnapshot = "";
    private bool _miningActive;
    private BlockPosition _miningPosition;
    private ushort _miningState;
    private float _miningElapsed;
    private bool _cursorInitialized;
    private bool _cursorMenuOpen;
    private readonly InventoryMenuState _menu = new();
    private string _profileToken = "";
    private string _profileWorldId = "";
    private string _profileDirectory = "";
    private float _identityRetryClock;
    private float _profileSaveClock;
    private bool _profileDirty;
    private bool _localIdentityFailed;

    private bool IsLocal =>
        !Scene.IsNetworked || GameObject.OwnerClientId == Scene.LocalClientId;

    /// <summary>Client-side menu focus changes immediately, without waiting
    /// for replicated server state. The server still authorizes all actions.</summary>
    public bool IsInventoryVisible => IsLocal ? _menu.Open : InventoryOpen;

    public override void Start()
    {
        if (!Scene.IsServer)
            return;
        _inventory.FillStarter();
        InventoryState = _inventory.Serialize();
        ToolState = _tools.Serialize();
        ProfileReady = false;
        ProfileError = false;
    }

    private static string LocalIdentity(string role)
    {
        string? slot = Environment.GetEnvironmentVariable(
            "TALLOWCRAFT_PROFILE_SLOT");
        return PlayerIdentity.GetOrCreate(GamePaths.PlayerData,
            string.IsNullOrEmpty(slot) ? role : slot);
    }

    private bool TryBindProfile(string token)
    {
        if (!Scene.IsServer || ProfileReady || ProfileError ||
            !PlayerIdentity.Valid(token))
            return false;

        _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        if (_world == null || _world.WorldIdentity.Length == 0)
            return false;
        string worldId = _world.WorldIdentity;
        if (!PlayerProfileClaims.TryClaim(worldId, token))
            return false; // Another live player already owns this profile.

        string folder = _world.PlayerProfilesFolder;
        try
        {
            ProfileLoadResult status = PlayerProfileStore.Load(folder, worldId,
                token, out BlockInventory savedInventory, out PlayerTools savedTools,
                out int type, out int shape, out int facing);
            if (status == ProfileLoadResult.Corrupt)
            {
                ProfileError = true;
                Console.WriteLine("Tallowcraft player save is damaged. " +
                    "No valid profile backup found; inventory will not be reset.");
                PlayerProfileClaims.Release(worldId, token);
                return false;
            }
            if (!_inventory.TryLoad(savedInventory.Serialize()) ||
                !_tools.TryLoad(savedTools.Serialize()))
                throw new InvalidDataException("Invalid restored player profile.");

            PlaceType = type;
            PlaceShape = shape;
            FacingOffset = facing;
            InventoryState = _inventory.Serialize();
            ToolState = _tools.Serialize();
            _profileToken = token;
            _profileWorldId = worldId;
            _profileDirectory = folder;
            _profileSaveClock = 0f;
            // Persist starter supplies before enabling their use. If this
            // first write fails, do not grant an unsaved new inventory that
            // could be granted again on the next reconnect.
            if (status == ProfileLoadResult.New)
                PlayerProfileStore.Save(folder, worldId, token,
                    _inventory, _tools, PlaceType, PlaceShape, FacingOffset);
            _profileDirty = false;
            ProfileReady = true;
            if (status == ProfileLoadResult.Recovered)
                Console.WriteLine("Tallowcraft recovered a previous player inventory.");
            return true;
        }
        catch (Exception exception)
        {
            PlayerProfileClaims.Release(worldId, token);
            ProfileError = true;
            Console.WriteLine($"Tallowcraft profile load failed: {exception}");
            return false;
        }
    }

    private void SaveProfile()
    {
        if (!Scene.IsServer || !ProfileReady || !_profileDirty)
            return;
        try
        {
            PlayerProfileStore.Save(_profileDirectory, _profileWorldId,
                _profileToken, _inventory, _tools, PlaceType,
                PlaceShape, FacingOffset);
            _profileDirty = false;
            _profileSaveClock = 0f;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Tallowcraft inventory save failed: {exception}");
        }
    }

    private void TickProfile(float deltaTime)
    {
        _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        if (ProfileReady && _world != null &&
            _world.WorldIdentity.Length > 0 &&
            _profileWorldId != _world.WorldIdentity)
        {
            // A seed change starts a different world with its own inventory.
            SaveProfile();
            PlayerProfileClaims.Release(_profileWorldId, _profileToken);
            string token = _profileToken;
            _profileToken = "";
            ProfileReady = false;
            _profileDirty = false;
            TryBindProfile(token);
        }

        if (!ProfileReady && !ProfileError &&
            GameObject.OwnerClientId == 0)
        {
            // Host player. Clients request their own profile using an owned RPC.
            try { TryBindProfile(LocalIdentity("host")); }
            catch (Exception exception)
            {
                ProfileError = true;
                Console.WriteLine($"Tallowcraft host identity failed: {exception}");
            }
        }
        if (ProfileReady && _profileDirty)
        {
            _profileSaveClock += MathF.Max(0f, deltaTime);
            if (_profileSaveClock >= 30f)
                SaveProfile();
        }
    }

    [Rpc(RpcTarget.Server)]
    private void BindPlayerProfile(string token)
    {
        if (!Scene.IsServer || !Scene.IsNetworked ||
            GameObject.OwnerClientId <= 0 ||
            RpcSender != GameObject.OwnerClientId ||
            !PlayerIdentity.Valid(token))
            return;
        TryBindProfile(token);
    }

    public override void LocalUpdate(float deltaTime)
    {
        if (!IsLocal)
            return;

        if (!Scene.IsServer && Scene.IsNetworked &&
            !ProfileReady && !ProfileError && !_localIdentityFailed)
        {
            _identityRetryClock -= MathF.Max(0f, deltaTime);
            if (_identityRetryClock <= 0f)
            {
                _identityRetryClock = 1f;
                try { Rpc(nameof(BindPlayerProfile), LocalIdentity("client")); }
                catch (Exception exception)
                {
                    _localIdentityFailed = true;
                    Console.WriteLine(
                        $"Tallowcraft local identity unavailable: {exception}");
                }
            }
        }

        // LocalControls.Read accumulates yaw/pitch from the Player/Look action
        // even if a script chooses not to rotate its camera. Merely skipping
        // camera rotation while browsing therefore makes the camera snap when
        // the menu closes. Disable the whole local Player map instead: this
        // stops look, movement, mining and placement at the input source.
        // Menu/Cancel and direct Tab input remain available while it is off.
        bool menuPressed = Input.GetKeyDown(KeyCode.Tab) ||
            InputActions.Pressed("Menu/Inventory") ||
            (_menu.Open && InputActions.Pressed("Menu/Cancel"));
        bool menuHeld = Input.GetKey(KeyCode.Tab) ||
            InputActions.Held("Menu/Inventory") ||
            (_menu.Open && InputActions.Held("Menu/Cancel"));
        if (_menu.Update(InventoryOpen, menuPressed, menuHeld,
                deltaTime, out bool requestedOpen))
            Rpc(nameof(SetInventoryOpen), requestedOpen);

        bool open = _menu.Open;
        if (!open)
        {
            // Server owns durability/equipment; hotkeys send only the tool id.
            // 1-6 continue selecting building blocks in the networked hotbar.
            int tool = Input.GetKeyDown(KeyCode.Alpha7) ? 1 :
                Input.GetKeyDown(KeyCode.Alpha8) ? 2 :
                Input.GetKeyDown(KeyCode.Alpha9) ? 3 :
                Input.GetKeyDown(KeyCode.Alpha0) ? 0 : -1;
            if (tool >= 0)
                Rpc(nameof(EquipToolQuick), tool);
        }
        if (!_cursorInitialized || _cursorMenuOpen != open)
        {
            // Set the input map before unlocking/locking the mouse. The engine
            // clears relative mouse motion when changing cursor-lock mode.
            InputActions.SetMapEnabled("Player", !open);
            Cursor.Locked = !open;
            _cursorMenuOpen = open;
            _cursorInitialized = true;
        }
    }

    public override void OnDestroy()
    {
        if (Scene.IsServer)
        {
            SaveProfile();
            if (_profileToken.Length != 0)
                PlayerProfileClaims.Release(_profileWorldId, _profileToken);
        }
        if (!IsLocal)
            return;
        // A new Play session or reconnect must start with gameplay enabled.
        InputActions.SetMapEnabled("Player", true);
        Cursor.Locked = false;
    }

    private void CloseMenuLocally()
    {
        if (_menu.RequestClose())
            Rpc(nameof(SetInventoryOpen), false);
        // LocalUpdate will immediately re-enable mouse look on this frame's
        // next input poll, after the engine's cursor-lock transition.
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

    /// <summary>Read the synchronized equipment state for the local HUD.</summary>
    private PlayerTools DisplayTools()
    {
        if (ToolState != _displayToolSnapshot)
        {
            _displayTools.TryLoad(ToolState);
            _displayToolSnapshot = ToolState;
        }
        return _displayTools;
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
    private void SetInventoryOpen(bool open)
    {
        if (!Scene.IsServer ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId))
            return;
        InventoryOpen = open;
        ResetMining();
    }

    [Rpc(RpcTarget.Server)]
    private void CraftRecipe(int recipeId)
    {
        if (!Scene.IsServer || !ProfileReady ||
            !InventoryOpen || _craftCooldown > 0f ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId))
            return;
        // No recipe contents or claimed resources are accepted over RPC.
        if (_inventory.TryCraft(recipeId))
        {
            InventoryState = _inventory.Serialize();
            _profileDirty = true;
            _craftCooldown = 0.12f;
        }
    }

    [Rpc(RpcTarget.Server)]
    private void EquipTool(int kind)
    {
        if (!Scene.IsServer || !ProfileReady || !InventoryOpen ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId))
            return;
        if (_tools.TryEquip((MiningTool)kind))
        {
            ToolState = _tools.Serialize();
            _profileDirty = true;
        }
    }

    [Rpc(RpcTarget.Server)]
    private void EquipToolQuick(int kind)
    {
        if (!Scene.IsServer || !ProfileReady || InventoryOpen ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId))
            return;
        if (_tools.TryEquip((MiningTool)kind))
        {
            ToolState = _tools.Serialize();
            _profileDirty = true;
        }
    }

    [Rpc(RpcTarget.Server)]
    private void CraftTool(int kind)
    {
        if (!Scene.IsServer || !ProfileReady ||
            !InventoryOpen || _craftCooldown > 0f ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId))
            return;
        if (_tools.TryCraft(_inventory, (MiningTool)kind))
        {
            InventoryState = _inventory.Serialize();
            ToolState = _tools.Serialize();
            _profileDirty = true;
            _craftCooldown = 0.12f;
        }
    }

    /// <summary>Client-owned requests for one page of a nearby edited section.</summary>
    public void RequestWorldSection(ChunkKey key, int page,
        int cachedRevision, string cachedHash)
    {
        if (!IsLocal || Scene.IsServer || !Scene.IsNetworked ||
            page < 0 || page >= 64)
            return;
        Rpc(nameof(RequestWorldSectionOnServer), key.X, key.Y, key.Z,
            page, cachedRevision, cachedHash);
    }

    [Rpc(RpcTarget.Server)]
    private void RequestWorldSectionOnServer(int x, int y, int z,
        int page, int cachedRevision, string cachedHash)
    {
        if (!Scene.IsServer || !Scene.IsNetworked ||
            RpcSender != GameObject.OwnerClientId ||
            _chunkRequestCooldown > 0f || page < 0 || page >= 64 ||
            cachedHash.Length > 64)
            return;
        _chunkRequestCooldown = 0.01f;
        _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        _world?.SendSectionPage(RpcSender, new ChunkKey(x, y, z),
            page, cachedRevision, cachedHash);
    }

    public override void Update(TickInput input, float deltaTime)
    {
        if (!Scene.IsServer)
            return;

        TickProfile(deltaTime);
        if (!ProfileReady)
            return;

        _cooldown = MathF.Max(0f, _cooldown - deltaTime);
        _craftCooldown = MathF.Max(0f, _craftCooldown - deltaTime);
        _chunkRequestCooldown = MathF.Max(0f, _chunkRequestCooldown - deltaTime);

        // The owner's local menu RPC toggles this instead of TickInput.Reload.
        // That lets the local machine suppress look input before the server
        // has acknowledged the change, with no double Tab toggle.
        if (InventoryOpen)
        {
            ResetMining();
            return;
        }

        int previousType = PlaceType;
        int previousShape = PlaceShape;
        int previousFacing = FacingOffset;

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
        if (previousType != PlaceType || previousShape != PlaceShape ||
            previousFacing != FacingOffset)
            _profileDirty = true;

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
            MiningPercent = BlockMining.Percent(_miningElapsed, state,
                _tools.Equipped) / 10 * 10;
            if (_miningElapsed < BlockMining.Seconds(state, _tools.Equipped))
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
                {
                    InventoryState = _inventory.Serialize();
                    _profileDirty = true;
                }
                if (_tools.OnBlockMined())
                {
                    ToolState = _tools.Serialize();
                    _profileDirty = true;
                }
            }
            else if (!CreativeMode && _inventory.TrySpend(PlaceType))
            {
                InventoryState = _inventory.Serialize();
                _profileDirty = true;
            }

            _cooldown = MathF.Max(0.05f, EditCooldown);
        }
        if (breaking)
            ResetMining();
    }

    private void DrawInventory()
    {
        const float width = 528f, height = 628f;
        float left = MathF.Max(8f, (GUI.Width - width) * 0.5f);
        float top = MathF.Max(8f, (GUI.Height - height) * 0.5f);
        GUI.Box(left, top, width, height,
            new Color(.035f, .045f, .055f, .96f));
        GUI.Label(left + 18, top + 12, "INVENTORY  /  CRAFTING");
        GUI.Label(left + 252, top + 12, CreativeMode
            ? "CREATIVE BUILDING" : "SURVIVAL BUILDING");
        if (GUI.Button(left + width - 81, top + 8, 65, 28, "Close"))
            CloseMenuLocally();

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
        GUI.Label(left + 18, top + 421, "MINING TOOLS");
        PlayerTools equipped = DisplayTools();
        for (int i = 1; i <= 3; i++)
        {
            MiningTool tool = (MiningTool)i;
            float y = top + 442 + (i - 1) * 50;
            int life = equipped.Durability(tool);
            int max = PlayerTools.MaxDurability(tool);
            PlayerTools.Recipe(tool, out int a, out int countA,
                out int b, out int countB);
            bool canCraft = AvailableCount(a) >= countA &&
                AvailableCount(b) >= countB && life < max;

            GUI.Box(left + 17, y, width - 34, 46,
                new Color(.13f, .16f, .17f, .95f));
            GUI.Label(left + 26, y + 4,
                $"{PlayerTools.Name(tool)}  {life}/{max}");
            GUI.Label(left + 26, y + 26,
                $"{countA} Stone + {countB} Planks  •  " +
                (life > 0 ? "craft again to repair" : "craft to unlock"));

            if (life > 0 && GUI.Button(left + width - 199, y + 9, 78, 28,
                    equipped.Equipped == tool ? "Equipped" : "Equip") &&
                equipped.Equipped != tool)
                Rpc(nameof(EquipTool), i);
            if (GUI.Button(left + width - 109, y + 9, 77, 28,
                    life == max ? "Full" :
                    canCraft ? life > 0 ? "Repair" : "Craft" : "No items") &&
                canCraft)
                Rpc(nameof(CraftTool), i);
        }
        if (GUI.Button(left + width - 204, top + 596, 172, 24,
                equipped.Equipped == MiningTool.Hand ? "Using hands" : "Use hands"))
            Rpc(nameof(EquipTool), 0);
        GUI.Label(left + 18, top + 601,
            "TAB closes   •   Tools wear out when mining");
    }

    public override void OnGUI()
    {
        if (!IsLocal)
            return;

        if (!ProfileReady)
        {
            GUI.Label(20f, 20f, ProfileError
                ? "PLAYER SAVE ERROR - check server log and backup files"
                : "Loading your saved inventory...");
            return;
        }

        if (IsInventoryVisible)
        {
            DrawInventory();
            return;
        }

        int selected = Math.Clamp(PlaceType, 1, 6);
        int shape = Math.Clamp(PlaceShape, 0, ShapeNames.Length - 1);
        const float width = 63f, gap = 5f;
        float total = 6 * width + 5 * gap;
        float left = MathF.Max(8f, (GUI.Width - total) * 0.5f);
        float top = MathF.Max(20f, GUI.Height - 148f);

        GUI.Box(left - 10, top - 37, total + 20, 166,
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
            "1-6 blocks   7 pick   8 axe   9 shovel   0 hand   TAB inventory");
        GUI.Label(left + 4, top + 85,
            MiningType is >= 1 and <= 9
                ? $"Mining {BlockNames[MiningType - 1]}: {MiningPercent}%"
                : $"Coal {AvailableCount(7)}   Iron {AvailableCount(8)}   Leaves {AvailableCount(9)}");
        PlayerTools tools = DisplayTools();
        MiningTool equipped = tools.Equipped;
        GUI.Label(left + 4, top + 103,
            $"Tool: {PlayerTools.Name(equipped)}" +
            (equipped == MiningTool.Hand ? "" :
                $"  {tools.Durability(equipped)}/{PlayerTools.MaxDurability(equipped)}"));
        GUI.Label(left + 4, top + 121, CreativeMode
            ? "Creative building ON  •  crafting uses materials"
            : "Survival building ON  •  mining collects materials");
        GUI.Label(GUI.Width * .5f - 4f, GUI.Height * .5f - 8f, "+");
    }
}
