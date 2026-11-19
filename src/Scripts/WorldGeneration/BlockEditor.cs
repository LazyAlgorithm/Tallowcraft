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
    [Sync] public int SelectedHotbar = 2;
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
    [Sync] public bool MiningActive = false;
    [Sync] public int MiningX = 0;
    [Sync] public int MiningY = 0;
    [Sync] public int MiningZ = 0;

    private static readonly string[] BlockNames =
    {
        "Grass", "Dirt", "Stone", "Log", "Forge", "Planks",
        "Coal", "Iron", "Leaves", "Iron ingots"
    };
    private static readonly string[] ShapeNames =
        { "Cube", "Slab", "Stair", "Half wall" };
    private static readonly string[] ItemShort =
        { "GRS", "DRT", "STN", "LOG", "FRG", "PLK", "COL", "ORE", "LEF", "ING" };
    private static readonly Color[] ItemColors =
    {
        new(.33f,.66f,.20f), new(.48f,.30f,.19f), new(.50f,.53f,.54f),
        new(.54f,.35f,.21f), new(.57f,.49f,.34f), new(.77f,.61f,.36f),
        new(.25f,.27f,.30f), new(.65f,.53f,.45f), new(.25f,.58f,.23f),
        new(.75f,.73f,.69f)
    };

    private BlockWorld? _world;
    private float _cooldown;
    private float _craftCooldown;
    private float _chunkRequestCooldown;
    private float _forgeCheckClock;
    private bool _nearForge;
    private bool _forgePage;
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
    private int _dragFrom = -1;
    private int _pickedFrom = -1;
    private bool _dragSplit;
    private bool _pickedSplit;

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
                out int type, out int shape, out int facing,
                out int savedSlot);
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

            // Old profiles saved a block type rather than a real hotbar
            // index. Prefer the matching material when upgrading them.
            int matchingSlot = savedInventory.FindHotbarType(type);
            SelectedHotbar = matchingSlot >= 0 ? matchingSlot : savedSlot;
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
                    _inventory, _tools, PlaceType, PlaceShape, FacingOffset,
                    SelectedHotbar);
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
                PlaceShape, FacingOffset, SelectedHotbar);
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
        if (open && _forgePage)
        {
            _forgeCheckClock -= MathF.Max(deltaTime, 0f);
            if (_forgeCheckClock <= 0f)
            {
                _forgeCheckClock = .25f;
                _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
                _nearForge = _world != null &&
                    ForgeCrafting.NearForge(
                        Transform.Position + Vector3.UnitY * EyeHeight,
                        position => _world.BlockAt(position));
            }
        }
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
        if (open)
            HandleInventoryPointer();
        else
        {
            _dragFrom = -1;
            _pickedFrom = -1;
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

    private BlockInventory DisplayInventory()
    {
        if (InventoryState != _displaySnapshot)
        {
            if (_displayInventory.TryLoad(InventoryState))
                _displaySnapshot = InventoryState;
        }
        return _displayInventory;
    }

    /// <summary>Clients read a fully validated server inventory snapshot.</summary>
    public int AvailableCount(int type)
    {
        return DisplayInventory().Count(type);
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

    public bool CanPlaceSelected
    {
        get
        {
            ItemStack held = DisplayInventory().Slot(SelectedHotbar);
            return held.Type is >= 1 and <= 9 &&
                (CreativeMode || held.Quantity > 0);
        }
    }

    [Rpc(RpcTarget.Server)]
    private void MoveInventoryStack(int from, int to, bool split)
    {
        if (!Scene.IsServer || !ProfileReady || !InventoryOpen ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId))
            return;
        // Only the server applies inventory moves. Bad indices, empty stacks,
        // split-swaps and forged requests leave the current data untouched.
        bool changed = split ? _inventory.TrySplit(from, to) :
            _inventory.TryMove(from, to);
        if (changed)
        {
            InventoryState = _inventory.Serialize();
            _profileDirty = true;
        }
    }

    [Rpc(RpcTarget.Server)]
    private void SelectInventoryHotbar(int slot)
    {
        if (!Scene.IsServer || !ProfileReady || !InventoryOpen ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId) ||
            slot is < 0 or >= BlockInventory.HotbarSlots)
            return;
        SelectedHotbar = slot;
        _profileDirty = true;
    }

    [Rpc(RpcTarget.Server)]
    private void RecoverLegacyMaterials(int type)
    {
        if (!Scene.IsServer || !ProfileReady || !InventoryOpen ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId))
            return;
        if (_inventory.RetrieveReserve(type))
        {
            InventoryState = _inventory.Serialize();
            _profileDirty = true;
        }
    }

    private static bool Inside(float x, float y, float left, float top,
        float width, float height) =>
        x >= left && x < left + width && y >= top && y < top + height;

    private static int InventorySlotAt(Vector2 pointer, float left, float top)
    {
        float localX = pointer.X - (left + 19);
        float localY = pointer.Y - (top + 58);
        if (localX < 0 || localY < 0) return -1;
        int col = (int)(localX / 70f);
        int row = (int)(localY / 67f);
        if (col is < 0 or >= 9 || row is < 0 or >= 4 ||
            localX - col * 70f >= 64f || localY - row * 67f >= 60f)
            return -1;
        return row * 9 + col;
    }

    private void HandleInventoryPointer()
    {
        const float width = 680f, height = 682f;
        float left = MathF.Max(8f, (GUI.Width - width) * .5f);
        float top = MathF.Max(8f, (GUI.Height - height) * .5f);
        Vector2 pointer = Input.MousePosition;
        int slot = InventorySlotAt(pointer, left, top);
        bool rightDown = Input.GetMouseButtonDown(MouseButton.Right);
        bool leftDown = Input.GetMouseButtonDown(MouseButton.Left);
        if (slot is >= 0 and < BlockInventory.HotbarSlots &&
            Input.GetMouseButtonDown(MouseButton.Middle))
            Rpc(nameof(SelectInventoryHotbar), slot);
        if (slot >= 0 && (leftDown || rightDown))
        {
            if (_pickedFrom >= 0)
            {
                if (slot != _pickedFrom)
                    Rpc(nameof(MoveInventoryStack), _pickedFrom, slot, _pickedSplit);
                _pickedFrom = -1;
                _dragFrom = -1;
            }
            else
            {
                _dragFrom = slot;
                _dragSplit = rightDown;
            }
        }
        if (_dragFrom >= 0 &&
            (Input.GetMouseButtonUp(_dragSplit ? MouseButton.Right :
                MouseButton.Left)))
        {
            if (slot >= 0 && slot != _dragFrom)
                Rpc(nameof(MoveInventoryStack), _dragFrom, slot, _dragSplit);
            else if (slot == _dragFrom)
            {
                _pickedFrom = _dragFrom;
                _pickedSplit = _dragSplit;
            }
            _dragFrom = -1;
        }
    }

    private void ResetMining()
    {
        _miningActive = false;
        _miningElapsed = 0f;
        if (MiningActive) MiningActive = false;
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
        // Workstation-only recipe IDs may never be crafted through the
        // ordinary inventory endpoint, even with a forged client RPC.
        if (recipeId is >= 1 and <= 3 && _inventory.TryCraft(recipeId))
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
        if (_tools.TryEquipSlot(kind))
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
        MiningTool tool = (MiningTool)kind;
        if (tool is >= MiningTool.IronPickaxe and <= MiningTool.IronShovel &&
            !ServerNearForge())
            return;
        if (_tools.TryCraft(_inventory, tool))
        {
            InventoryState = _inventory.Serialize();
            ToolState = _tools.Serialize();
            _profileDirty = true;
            _craftCooldown = 0.12f;
        }
    }

    private bool ServerNearForge()
    {
        if (!Scene.IsServer)
            return false;
        _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        return _world != null && ForgeCrafting.NearForge(
            Transform.Position + Vector3.UnitY * EyeHeight,
            position => _world.BlockAt(position));
    }

    [Rpc(RpcTarget.Server)]
    private void SmeltIron(int count)
    {
        if (!Scene.IsServer || !ProfileReady || !InventoryOpen ||
            _craftCooldown > 0f ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId) ||
            !ServerNearForge())
            return;
        if (ForgeCrafting.TrySmelt(_inventory, count))
        {
            InventoryState = _inventory.Serialize();
            _profileDirty = true;
            _craftCooldown = .12f;
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

    /// <summary>
    /// Only a live, server-owned drop may call this. Successful collection
    /// updates the authoritative inventory snapshot and persistent profile.
    /// An invalid item, full material cap or uninitialized profile rejects
    /// the pickup without deleting the drop.
    /// </summary>
    internal bool TryReceiveWorldDrop(int type, int quantity)
    {
        if (!Scene.IsServer || !ProfileReady ||
            type is < 1 or > BlockInventory.MaxType ||
            quantity is < 1 or > BlockInventory.StackLimit ||
            !_inventory.TryCollect(type, quantity))
            return false;
        InventoryState = _inventory.Serialize();
        _profileDirty = true;
        return true;
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

        int previousHotbar = SelectedHotbar;
        for (int i = 0; i < 6; i++)
            if (input.Pressed((InputButton)((int)InputButton.Custom1 + i)))
                SelectedHotbar = i;
        if (input.Pressed(InputButton.Custom7))
            SelectedHotbar = (SelectedHotbar + 1) % BlockInventory.HotbarSlots;
        if (input.Pressed(InputButton.Custom8))
            SelectedHotbar = (SelectedHotbar + BlockInventory.HotbarSlots - 1) %
                BlockInventory.HotbarSlots;
        if (input.Pressed(InputButton.Ability1))
            PlaceShape = (PlaceShape + 1) % 4;
        if (input.Pressed(InputButton.Ability2))
            FacingOffset = (FacingOffset + 1) % 4;

        SelectedHotbar = Math.Clamp(SelectedHotbar, 0,
            BlockInventory.HotbarSlots - 1);
        int heldType = _inventory.Slot(SelectedHotbar).Type;
        PlaceType = heldType is >= 1 and <= 9 ? heldType : 0;
        PlaceShape = Math.Clamp(PlaceShape, 0, 3);
        FacingOffset &= 3;
        if (previousHotbar != SelectedHotbar || previousType != PlaceType ||
            previousShape != PlaceShape || previousFacing != FacingOffset)
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
            // The render-only crack overlay may appear only on this exact
            // authoritative block. Looking at a different voxel must never
            // transfer the old damage stage to it.
            MiningX = target.X;
            MiningY = target.Y;
            MiningZ = target.Z;
            MiningActive = true;
            MiningType = new BlockState(state).Type;
            MiningPercent = BlockMining.Percent(_miningElapsed, state,
                _tools.Equipped) / 10 * 10;
            if (_miningElapsed < BlockMining.Seconds(state, _tools.Equipped))
                return;
        }
        else if (placing &&
            (PlaceType is < 1 or > 9 ||
             (!CreativeMode &&
              _inventory.Slot(SelectedHotbar).Quantity == 0)))
            return;

        if (_world.TryEditFromRay(origin, direction, Math.Clamp(Reach, 1f, 8f),
            breaking, GameObject, selected.Packed,
            out ushort previous, out _))
        {
            if (breaking)
            {
                // Minecraft-style collectible: a server-owned networked
                // drop falls from the mined voxel. On spawn failure (e.g.
                // temporary entity cap), award directly instead of losing it.
                int material = new BlockState(previous).Type;
                Vector3 droppedAt = new(
                    _miningPosition.X + .5f,
                    _miningPosition.Y + .55f,
                    _miningPosition.Z + .5f);
                if (!WorldDrop.TrySpawn(Scene, material, droppedAt))
                    TryReceiveWorldDrop(material, 1);
                if (_tools.OnBlockMined())
                {
                    ToolState = _tools.Serialize();
                    _profileDirty = true;
                }
            }
            else if (!CreativeMode && _inventory.TrySpendSlot(SelectedHotbar))
            {
                InventoryState = _inventory.Serialize();
                _profileDirty = true;
            }

            _cooldown = MathF.Max(0.05f, EditCooldown);
        }
        if (breaking)
            ResetMining();
    }

    private void DrawToolRow(float left, float width,
        int i, float y, PlayerTools equipped, bool forge)
    {
        MiningTool tool = (MiningTool)i;
        int life = equipped.Durability(tool);
        int max = PlayerTools.MaxDurability(tool);
        PlayerTools.Recipe(tool, out int a, out int countA,
            out int b, out int countB);
        bool canCraft = AvailableCount(a) >= countA &&
            AvailableCount(b) >= countB && life < max && (!forge || _nearForge);

        GUI.Box(left + 17, y, width - 34, 31,
            new Color(.13f,.16f,.17f,.95f));
        GUI.Label(left + 26, y + 6,
            PlayerTools.Name(tool) + " " + life + "/" + max);
        GUI.Label(left + 266, y + 6,
            countA + (forge ? " ingots + " : " stone + ") +
            countB + " planks");
        if (life > 0 && GUI.Button(left + width - 190, y + 1, 75, 27,
                equipped.Equipped == tool ? "Equipped" : "Equip") &&
            equipped.Equipped != tool)
            Rpc(nameof(EquipTool), i);
        if (GUI.Button(left + width - 107, y + 1, 75, 27,
                life == max ? "Full" :
                canCraft ? life > 0 ? "Repair" : "Craft" : "No items") &&
            canCraft)
            Rpc(nameof(CraftTool), i);
    }

    private void DrawInventory()
    {
        const float width = 680f, height = 682f;
        float left = MathF.Max(8f, (GUI.Width - width) * .5f);
        float top = MathF.Max(8f, (GUI.Height - height) * .5f);
        GUI.Box(left, top, width, height,
            new Color(.035f,.045f,.055f,.97f));
        GUI.Label(left + 18, top + 12,
            _forgePage ? "INVENTORY / FORGE" : "INVENTORY / CRAFTING");
        if (GUI.Button(left + 290, top + 8, 150, 28,
                _forgePage ? "Crafting page" : "Forge page"))
        {
            _forgePage = !_forgePage;
            _forgeCheckClock = 0;
            _pickedFrom = -1;
        }
        if (GUI.Button(left + 450, top + 8, 115, 28, "Use hands"))
            Rpc(nameof(EquipTool), 0);
        if (GUI.Button(left + 594, top + 8, 68, 28, "Close"))
            CloseMenuLocally();

        BlockInventory items = DisplayInventory();
        GUI.Label(left + 19, top + 39,
            "HOTBAR (top row)  /  BACKPACK (three rows)");
        for (int slot = 0; slot < BlockInventory.SlotCount; slot++)
        {
            int row = slot / 9, col = slot % 9;
            float x = left + 19 + col * 70;
            float y = top + 58 + row * 67;
            ItemStack stack = items.Slot(slot);
            bool selected = slot == SelectedHotbar && row == 0;
            bool picked = slot == _pickedFrom || slot == _dragFrom;
            GUI.Box(x, y, 64, 60,
                picked ? new Color(.60f,.43f,.17f,.97f) :
                selected ? new Color(.36f,.52f,.25f,.97f) :
                row == 0 ? new Color(.21f,.26f,.27f,.98f) :
                    new Color(.13f,.16f,.18f,.98f));
            if (row == 0)
                GUI.Label(x + 3, y + 2, (slot + 1).ToString());
            if (!stack.IsEmpty && stack.Type is >= 1 and <= 10)
            {
                GUI.Box(x + 5, y + 21, 12, 16,
                    ItemColors[stack.Type-1]);
                GUI.Label(x + 19, y + 21, ItemShort[stack.Type-1]);
                GUI.Label(x + 7, y + 39, "x" + stack.Quantity);
            }
        }

        GUI.Label(left + 19, top + 330,
            "Drag to move/swap   Right-drag half   Click, then click to move");
        GUI.Label(left + 19, top + 348,
            "Wheel: select 1-9   Middle-click hotbar: select   7/8/9: tools");

        // Legacy profiles may hold more than 36 x 64 items. Show excess,
        // keep it spendable, and allow retrieving it after making room.
        int reserveType = 0, reserveCount = 0;
        for (int i = 1; i <= BlockInventory.MaxType; i++)
        {
            if (items.Reserve(i) == 0) continue;
            reserveType = i;
            reserveCount += items.Reserve(i);
        }
        if (reserveCount > 0)
        {
            GUI.Label(left + 19, top + 367,
                "Legacy reserve: " + reserveCount +
                " items (available to craft; free slots to unpack)");
            if (GUI.Button(left + 510, top + 363, 150, 29,
                    "Unpack " + BlockNames[reserveType-1]))
                Rpc(nameof(RecoverLegacyMaterials), reserveType);
        }

        PlayerTools equipped = DisplayTools();
        if (_forgePage)
        {
            GUI.Label(left + 19, top + 394,
                _nearForge ? "FORGE ACTIVE (within 3.5m)" :
                    "PLACE A FORGE AND STAND WITHIN 3.5m");
            GUI.Label(left + 19, top + 414,
                "1 iron ore + 1 coal = 1 iron ingot");
            bool one = _nearForge && AvailableCount(8) >= 1 &&
                AvailableCount(7) >= 1 && AvailableCount(10) < 999;
            bool five = _nearForge && AvailableCount(8) >= 5 &&
                AvailableCount(7) >= 5 && AvailableCount(10) <= 994;
            if (GUI.Button(left + 19, top + 439, 144, 34,
                    one ? "Smelt 1" : "Need ore / coal") && one)
                Rpc(nameof(SmeltIron), 1);
            if (GUI.Button(left + 174, top + 439, 144, 34,
                    five ? "Smelt 5" : "Need 5 each") && five)
                Rpc(nameof(SmeltIron), 5);
            GUI.Label(left + 19, top + 486, "IRON TOOLS");
            for (int i = 4; i <= 6; i++)
                DrawToolRow(left, width, i,
                    top + 510 + (i - 4) * 42, equipped, true);
        }
        else
        {
            GUI.Label(left + 19, top + 394, "CRAFTING RECIPES");
            var recipes = CraftingRecipes.All;
            for (int i = 0; i < 3; i++)
            {
                CraftingRecipe recipe = recipes[i];
                float y = top + 418 + i * 43;
                GUI.Box(left + 17, y, width - 34, 40,
                    new Color(.13f,.16f,.17f,.95f));
                string ingredients = recipe.CountA + " " +
                    BlockNames[recipe.InputA - 1];
                if (recipe.InputB != 0)
                    ingredients += " + " + recipe.CountB + " " +
                        BlockNames[recipe.InputB - 1];
                GUI.Label(left + 26, y + 5,
                    recipe.Name + "  /  " + ingredients);
                GUI.Label(left + 26, y + 23,
                    "Makes " + recipe.CountOut + " " +
                    BlockNames[recipe.Output - 1]);
                bool available = AvailableCount(recipe.InputA) >= recipe.CountA &&
                    (recipe.InputB == 0 ||
                    AvailableCount(recipe.InputB) >= recipe.CountB);
                if (GUI.Button(left + width - 113, y + 6, 79, 28,
                        available ? "Craft" : "Need items") && available)
                    Rpc(nameof(CraftRecipe), recipe.Id);
            }
            GUI.Label(left + 19, top + 552, "STONE TOOLS");
            for (int i = 1; i <= 3; i++)
                DrawToolRow(left, width, i,
                    top + 572 + (i-1) * 34, equipped, false);
        }
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

        BlockInventory items = DisplayInventory();
        int active = Math.Clamp(SelectedHotbar, 0,
            BlockInventory.HotbarSlots-1);
        int shape = Math.Clamp(PlaceShape, 0, ShapeNames.Length-1);
        const float slotWidth = 59f, gap = 5f;
        float total = BlockInventory.HotbarSlots * slotWidth +
            (BlockInventory.HotbarSlots-1) * gap;
        float left = MathF.Max(8f, (GUI.Width - total) * .5f);
        float top = MathF.Max(20f, GUI.Height - 146f);

        GUI.Box(left - 10, top - 37, total + 20, 166,
            new Color(.03f,.04f,.06f,.76f));
        GUI.Label(left + 8, top - 31,
            "TALLOWCRAFT     LMB PLACE    HOLD RMB MINE");
        for (int slot = 0; slot < BlockInventory.HotbarSlots; slot++)
        {
            float x = left + slot*(slotWidth+gap);
            bool selected = active == slot;
            ItemStack item = items.Slot(slot);
            GUI.Box(x, top, slotWidth, 48, selected ?
                new Color(.37f,.51f,.22f,.97f) :
                new Color(.13f,.15f,.18f,.95f));
            GUI.Label(x+4, top+2, (slot+1).ToString());
            if (!item.IsEmpty && item.Type is >= 1 and <= 10)
            {
                GUI.Box(x+5, top+22, 11, 12, ItemColors[item.Type-1]);
                GUI.Label(x+18, top+21, ItemShort[item.Type-1]);
                GUI.Label(x+7, top+37, "x"+item.Quantity);
            }
        }
        ItemStack held = items.Slot(active);
        string heldName = held.IsEmpty ? "Empty" :
            held.Type <= BlockNames.Length ? BlockNames[held.Type-1] : "Unknown";
        GUI.Label(left + 4, top + 51,
            heldName + "  /  " + ShapeNames[shape] +
            "  /  rotation " + FacingOffset*90 + "°");
        GUI.Label(left + 4, top + 67,
            "1-6 or wheel: hotbar   7 pick   8 axe   9 shovel   0 hands");
        GUI.Label(left + 4, top + 85,
            MiningType is >= 1 and <= 9 ?
                "Mining " + BlockNames[MiningType-1] + ": " + MiningPercent + "%" :
                "Coal " + AvailableCount(7) +
                    "   Iron ore " + AvailableCount(8) +
                    "   Ingots " + AvailableCount(10));
        PlayerTools tools = DisplayTools();
        MiningTool equipped = tools.Equipped;
        GUI.Label(left + 4, top + 103,
            "Tool: " + PlayerTools.Name(equipped) +
            (equipped == MiningTool.Hand ? "" :
                " " + tools.Durability(equipped) + "/" +
                PlayerTools.MaxDurability(equipped)));
        GUI.Label(left + 4, top + 121, CreativeMode ?
            "Creative building ON  /  TAB: inventory" :
            "Survival building ON  /  TAB: inventory");
        GUI.Label(GUI.Width * .5f - 4f, GUI.Height * .5f - 8f, "+");
    }
}
