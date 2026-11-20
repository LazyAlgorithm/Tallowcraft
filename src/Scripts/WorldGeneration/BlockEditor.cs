using System;
using System.Collections.Generic;
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
    [Sync] public bool ChestOpen = false;
    [Sync] public int ChestX = 0;
    [Sync] public int ChestY = 0;
    [Sync] public int ChestZ = 0;
    [Sync] public string ChestState = "";
    // Networked feedback makes a rejected chest request/transfer visible
    // instead of silently leaving the storage UI empty.
    [Sync] public string ChestStatus = "";
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
        "Coal", "Iron", "Leaves", "Iron ingots", "Chest"
    };
    private static readonly string[] ShapeNames =
        { "Cube", "Slab", "Stair", "Half wall" };
    private static readonly string[] ItemShort =
        { "GRS", "DRT", "STN", "LOG", "FRG", "PLK", "COL", "ORE", "LEF", "ING", "CHT" };
    private static readonly Color[] ItemColors =
    {
        new(.33f,.66f,.20f), new(.48f,.30f,.19f), new(.50f,.53f,.54f),
        new(.54f,.35f,.21f), new(.57f,.49f,.34f), new(.77f,.61f,.36f),
        new(.25f,.27f,.30f), new(.65f,.53f,.45f), new(.25f,.58f,.23f),
        new(.75f,.73f,.69f), new(.61f,.38f,.19f)
    };

    private BlockWorld? _world;
    private float _cooldown;
    private float _craftCooldown;
    private float _chunkRequestCooldown;
    private float _forgeCheckClock;
    private bool _nearForge;
    private GameObject? _uiObject;
    private TallowcraftUI? _ui;
    private ChestContents _displayChest = new();
    private string _displayChestSnapshot = "";
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
    private bool _cursorGameMenuOpen;
    private readonly InventoryMenuState _menu = new();
    private string _profileToken = "";
    private string _profileWorldId = "";
    private string _profileDirectory = "";
    private float _identityRetryClock;
    private float _profileSaveClock;
    private bool _profileDirty;
    private bool _localIdentityFailed;
    private bool _mainMenu = true;
    private bool _paused;
    private bool _settingsOpen;
    private Vector2 _serverSpawnXZ;
    private float _returnToSpawnCooldown;

    private bool IsLocal =>
        !Scene.IsNetworked || GameObject.OwnerClientId == Scene.LocalClientId;

    /// <summary>Client-side menu focus changes immediately, without waiting
    /// for replicated server state. The server still authorizes all actions.</summary>
    public bool IsInventoryVisible => IsLocal ? _menu.Open : InventoryOpen;
    internal bool UiMainMenu => _mainMenu;
    internal bool UiPaused => _paused;
    internal bool UiSettingsOpen => _settingsOpen;
    public bool UiGameMenuVisible => IsLocal && (_mainMenu || _paused);

    internal void UiStartGame() { _mainMenu = false; _settingsOpen = false; }
    internal void UiResumeGame() { _paused = false; _settingsOpen = false; }
    internal void UiToggleSettings() => _settingsOpen = !_settingsOpen;
    internal void UiQuitGame() => Application.Quit();
    internal void UiReturnToSpawn() => Rpc(nameof(RequestReturnToSpawn));

    /// <summary>Voluntary, server-authorized return to the original spawn
    /// point. No health, death or inventory loss is involved. Clients cannot
    /// provide arbitrary destination coordinates or bypass the cooldown.</summary>
    [Rpc(RpcTarget.Server)]
    private void RequestReturnToSpawn()
    {
        if (!Scene.IsServer || !ProfileReady ||
            _returnToSpawnCooldown > 0f ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId))
            return;
        _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        if (_world == null || _world.PersistenceLocked ||
            _world.WorldIdentity.Length == 0)
            return;
        var controller = GetCharacterController();
        if (controller == null || !controller.HasCharacter)
            return;
        Vector3 destination = new(_serverSpawnXZ.X, 0f, _serverSpawnXZ.Y);
        destination.Y = _world.SafeSpawnHeight(destination.X,
            destination.Z, 2f);
        if (!WaypointBook.ValidPosition(destination) ||
            !controller.Teleport(destination))
            return;
        Transform.Position = destination;
        _returnToSpawnCooldown = 10f;
    }

    public override void Start()
    {
        if (!Scene.IsServer)
            return;
        _serverSpawnXZ = new Vector2(Transform.Position.X, Transform.Position.Z);
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
        if (_world == null || _world.WorldIdentity.Length == 0 ||
            _world.PersistenceLocked)
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
        if (!Scene.IsServer || !ProfileReady || !_profileDirty ||
            _world?.PersistenceLocked == true)
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
        if (_world?.PersistenceLocked == true)
        {
            if (ProfileReady)
            {
                // Do not overwrite the redo journal's promised profile with
                // unsaved mutations after a disk failure. Restart to recover.
                PlayerProfileClaims.Release(_profileWorldId, _profileToken);
                ProfileReady = false;
                ProfileError = true;
                _profileDirty = false;
                Console.WriteLine(
                    "Tallowcraft profile locked after pickup persistence error.");
            }
            return;
        }
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

        if (_uiObject is not { IsValid: true })
        {
            // Per-client visual UI. It is not a replicated player component,
            // and no server/world data is stored inside the .tui document.
            GameObject view = Scene.CreateGameObject("Tallowcraft HUD (local)");
            view.Transient = true;
            TallowcraftUI controller = view.AddComponent<TallowcraftUI>();
            controller.Bind(this);
            UIDocument document = view.AddComponent<UIDocument>();
            // Uniform fit for desktop resolutions, without stretching
            // either the pixel-art icons or the click targets.
            var documentSettings = new NativeComponent(view, "Tallow.UIDocument");
            documentSettings.Set("referenceHeight", 900);
            documentSettings.Set("referenceWidth", 1200);
            document.Document = "UI/tallowcraft.tui";
            _uiObject = view;
            _ui = controller;
        }

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
        if (!_mainMenu && !_menu.Open &&
            (Input.GetKeyDown(KeyCode.Escape) || InputActions.Pressed("Menu/Cancel")))
        {
            _paused = !_paused;
            _settingsOpen = false;
        }

        bool menuPressed = !_mainMenu && !_paused && (Input.GetKeyDown(KeyCode.Tab) ||
            InputActions.Pressed("Menu/Inventory") ||
            (_menu.Open && InputActions.Pressed("Menu/Cancel")));
        bool menuHeld = !_mainMenu && !_paused && (Input.GetKey(KeyCode.Tab) ||
            InputActions.Held("Menu/Inventory") ||
            (_menu.Open && InputActions.Held("Menu/Cancel")));
        if (_menu.Update(InventoryOpen, menuPressed, menuHeld,
                deltaTime, out bool requestedOpen))
            Rpc(nameof(SetInventoryOpen), requestedOpen);

        bool open = _menu.Open;
        if (open)
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
        if (!open && !_mainMenu && !_paused)
        {
            // E is interaction-only: the Player/Use action no longer
            // binds this key. Interacting requests the nearby chest on the
            // server, which atomically opens both the chest and menu when
            // a real placed chest is in range. No client voxel claim.
            if (!InventoryOpen && ProfileReady &&
                InputActions.Pressed("Player/Interact"))
                Rpc(nameof(InteractNearbyChest));

            // Server owns durability/equipment; hotkeys send only the tool id.
            // 1-6 continue selecting building blocks in the networked hotbar.
            int tool = Input.GetKeyDown(KeyCode.Alpha7) ? 1 :
                Input.GetKeyDown(KeyCode.Alpha8) ? 2 :
                Input.GetKeyDown(KeyCode.Alpha9) ? 3 :
                Input.GetKeyDown(KeyCode.Alpha0) ? 0 : -1;
            if (tool >= 0)
                Rpc(nameof(EquipToolQuick), tool);
        }
        _ui?.Refresh();

        if (!_cursorInitialized || _cursorMenuOpen != open ||
            _cursorGameMenuOpen != UiGameMenuVisible)
        {
            // Set the input map before unlocking/locking the mouse. The engine
            // clears relative mouse motion when changing cursor-lock mode.
            InputActions.SetMapEnabled("Player", !open && !_mainMenu && !_paused);
            Cursor.Locked = !open && !_mainMenu && !_paused;
            _cursorMenuOpen = open;
            _cursorGameMenuOpen = UiGameMenuVisible;
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
        if (_uiObject is { IsValid: true } ui)
            ui.Destroy();
        _uiObject = null;
        _ui = null;
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

    // UI Document events are received on a separate local-only code-behind
    // object. These accessors expose read-only replicated snapshots and only
    // forward actions to the existing server-authoritative RPC endpoints.
    internal BlockInventory UiInventory => DisplayInventory();
    internal PlayerTools UiTools => DisplayTools();
    internal ChestContents UiChest => DisplayChest();
    internal bool UiNearForge => _nearForge;
    internal void UiMoveStack(int from, int to, bool split) =>
        RequestItemMove(from, to, split);
    internal void UiSetHotbar(int slot) =>
        Rpc(nameof(SelectInventoryHotbar), slot);
    internal void UiCraftRecipe(int recipe) =>
        Rpc(nameof(CraftRecipe), recipe);
    internal void UiCraftTool(int tool) =>
        Rpc(nameof(CraftTool), tool);
    internal void UiEquipTool(int tool) =>
        Rpc(nameof(EquipTool), tool);
    internal void UiSmelt(int quantity) =>
        Rpc(nameof(SmeltIron), quantity);
    internal void UiUnpack(int type) =>
        Rpc(nameof(RecoverLegacyMaterials), type);
    internal void UiOpenChest() =>
        Rpc(nameof(OpenNearbyChest));
    internal void UiCloseChest() =>
        Rpc(nameof(CloseChest));
    internal void UiCloseMenu() => CloseMenuLocally();

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
            return held.Type is >= 1 and <= BlockInventory.Chest &&
                held.Type != BlockInventory.IronIngot &&
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

    private void RequestItemMove(int from,int to,bool split)
    {
        if (from<0 || to<0 || from==to) return;
        if (from<36 && to<36)
            Rpc(nameof(MoveInventoryStack),from,to,split);
        else if (ChestOpen)
            Rpc(nameof(MoveChestStack),from,to,split);
    }

    private bool ServerChestInReach()
    {
        if (!Scene.IsServer || !ChestOpen) return false;
        Vector3 eye=Transform.Position+Vector3.UnitY*EyeHeight;
        return Vector3.DistanceSquared(eye,
            new Vector3(ChestX+.5f,ChestY+.5f,ChestZ+.5f))<=4.5f*4.5f;
    }

    private void RefreshChest()
    {
        if (!ChestOpen) return;
        _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        if (_world==null || !ServerChestInReach() ||
            !_world.TryReadChest(new BlockPosition(ChestX,ChestY,ChestZ),
                out ChestContents contents))
        {
            ChestOpen = false;
            ChestState = "";
            ChestStatus = "Chest closed: out of range or no longer placed.";
            return;
        }
        string current=contents.Serialize();
        if (current!=ChestState) ChestState=current;
    }

    /// <summary>Common server-side check for both E interaction and the
    /// Storage tab. Only placed chest voxels in range may open. No client
    /// location, block position, or item contents are trusted.</summary>
    private bool TryOpenChestNearPlayer()
    {
        if (!Scene.IsServer || !ProfileReady)
            return false;
        _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        BlockPosition? found = _world?.NearestChest(
            Transform.Position + Vector3.UnitY * EyeHeight);
        if (found is not { } pos || _world == null ||
            !_world.TryReadChest(pos, out ChestContents contents))
        {
            ChestOpen = false;
            ChestState = "";
            ChestStatus = "No placed chest within 4.5m. Move closer and try again.";
            return false;
        }
        ChestX = pos.X;
        ChestY = pos.Y;
        ChestZ = pos.Z;
        ChestOpen = true;
        ChestState = contents.Serialize();
        ChestStatus = "Chest ready. Select a stack, then an empty storage slot.";
        return true;
    }

    /// <summary>World interaction opened by E, not the block placement
    /// action. The menu and chest open in the same server transaction,
    /// avoiding the tab menu's InventoryOpen acknowledgment race.</summary>
    [Rpc(RpcTarget.Server)]
    private void InteractNearbyChest()
    {
        if (!Scene.IsServer || !ProfileReady || InventoryOpen ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId))
            return;
        if (!TryOpenChestNearPlayer())
            return;
        InventoryOpen = true;
        ResetMining();
    }

    [Rpc(RpcTarget.Server)]
    private void OpenNearbyChest()
    {
        if (!Scene.IsServer ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId))
            return;
        if (!ProfileReady || !InventoryOpen)
        {
            ChestStatus = "Inventory is not ready; open it before using storage.";
            return;
        }
        TryOpenChestNearPlayer();
    }

    [Rpc(RpcTarget.Server)]
    private void CloseChest()
    {
        if (!Scene.IsServer ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId))
            return;
        ChestOpen = false;
        ChestState = "";
        ChestStatus = "";
    }

    [Rpc(RpcTarget.Server)]
    private void MoveChestStack(int from,int to,bool split)
    {
        if (!Scene.IsServer ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId))
            return;
        if (!ProfileReady || !InventoryOpen || !ChestOpen)
        {
            ChestStatus = "Open a nearby chest before transferring items.";
            return;
        }
        if (!ServerChestInReach())
        {
            ChestOpen = false;
            ChestState = "";
            ChestStatus = "Too far from the chest; move closer and reopen it.";
            return;
        }
        if (from is < 0 or >= 63 || to is < 0 or >= 63 ||
            from == to)
        {
            ChestStatus = "Invalid source or destination slot.";
            return;
        }
        _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        if (_world == null)
        {
            ChestStatus = "World storage is not ready.";
            return;
        }
        BlockPosition pos = new(ChestX, ChestY, ChestZ);
        bool changed, playerChanged = false;
        BlockInventory after = new();
        if (from < 36 && to >= 36)
        {
            changed = _world.TryCommitChestTransfer(pos, _inventory,
                from, to - 36, true, split, _profileToken, _tools,
                PlaceType, PlaceShape, FacingOffset, SelectedHotbar,
                out after);
            playerChanged = changed;
        }
        else if (from >= 36 && to < 36)
        {
            changed = _world.TryCommitChestTransfer(pos, _inventory,
                to, from - 36, false, split, _profileToken, _tools,
                PlaceType, PlaceShape, FacingOffset, SelectedHotbar,
                out after);
            playerChanged = changed;
        }
        else if (from >= 36 && to >= 36)
            changed = _world.TryRearrangeChest(pos, from - 36,
                to - 36, split);
        else
        {
            ChestStatus = "Select one storage slot and one player slot.";
            return;
        }
        if (!changed)
        {
            ChestStatus = "Transfer failed: slot full, incompatible or empty.";
            RefreshChest();
            return;
        }
        if (playerChanged)
        {
            if (!_inventory.TryLoad(after.Serialize()))
                throw new InvalidOperationException(
                    "Durably transferred chest items but live inventory rejected.");
            InventoryState = _inventory.Serialize();
            _profileDirty = false;
            _profileSaveClock = 0f;
        }
        ChestStatus = from < 36 && to >= 36 ? "Items stored." :
            from >= 36 && to < 36 ? "Items withdrawn." :
            "Chest items rearranged.";
        RefreshChest();
    }

    private ChestContents DisplayChest()
    {
        if (ChestState!=_displayChestSnapshot &&
            ChestContents.TryLoad(ChestState,out ChestContents next))
        {
            _displayChest=next;
            _displayChestSnapshot=ChestState;
        }
        return _displayChest;
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
        if (!open)
        {
            ChestOpen = false;
            ChestState = "";
            ChestStatus = "";
        }
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
        if ((recipeId is >= 1 and <= 3 || recipeId == 6) &&
            _inventory.TryCraft(recipeId))
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
    /// <summary>Atomically persist a physical pickup's receiving profile
    /// and its removal from the ground-drop snapshot before changing memory.
    /// This is distinct from the direct-credit fallback when no drop spawned.</summary>
    internal bool TryReceivePersistedDrop(WorldDrop drop)
    {
        if (!Scene.IsServer || !ProfileReady || !drop.GameObject.IsValid ||
            _world?.PersistenceLocked == true ||
            drop.ItemType is < 1 or > BlockInventory.MaxType ||
            drop.Quantity is < 1 or > BlockInventory.StackLimit)
            return false;

        var after = new BlockInventory();
        if (!after.TryLoad(_inventory.Serialize()) ||
            !after.TryCollect(drop.ItemType, drop.Quantity))
            return false;

        _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        if (_world == null ||
            !_world.TryCommitDropPickup(drop, _profileToken, after, _tools,
                PlaceType, PlaceShape, FacingOffset, SelectedHotbar))
            return false;

        // The durable profile has already been saved. Replacing the live
        // inventory here cannot fail: it was validated before journal creation.
        if (!_inventory.TryLoad(after.Serialize()))
            throw new InvalidOperationException(
                "Pickup profile committed but in-memory inventory rejected.");
        InventoryState = _inventory.Serialize();
        _profileDirty = false;
        _profileSaveClock = 0f;
        return true;
    }

    /// <summary>The 256-drop cap / prefab spawn failure cannot simply
    /// credit an unsaved inventory after the voxel has been mined. Stage
    /// the reward and commit the mined block and player inventory as one
    /// journaled transaction, including the current tool durability.</summary>
    private bool TryReceiveMinedFallback(int type, BlockPosition position,
        ushort blockBefore, SectionedEditSnapshot beforeEdit,
        IReadOnlyDictionary<ChunkKey, int> beforeRevisions)
    {
        if (!Scene.IsServer || !ProfileReady ||
            type is < 1 or > BlockInventory.MaxType)
            return false;
        var after = new BlockInventory();
        if (!after.TryLoad(_inventory.Serialize()) ||
            !after.TryCollect(type, 1))
            return false;
        _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        if (_world == null ||
            !_world.TryCommitMiningCredit(position, blockBefore,
                beforeEdit, beforeRevisions, _profileToken,
                after, _tools, PlaceType, PlaceShape, FacingOffset,
                SelectedHotbar))
            return false;

        if (!_inventory.TryLoad(after.Serialize()))
            throw new InvalidOperationException(
                "Mined reward committed but live inventory rejected.");
        InventoryState = _inventory.Serialize();
        _profileDirty = false;
        _profileSaveClock = 0f;
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
        _returnToSpawnCooldown = MathF.Max(0f, _returnToSpawnCooldown - deltaTime);
        _craftCooldown = MathF.Max(0f, _craftCooldown - deltaTime);
        _chunkRequestCooldown = MathF.Max(0f, _chunkRequestCooldown - deltaTime);

        // The owner's local menu RPC toggles this instead of TickInput.Reload.
        // That lets the local machine suppress look input before the server
        // has acknowledged the change, with no double Tab toggle.
        if (InventoryOpen)
        {
            ResetMining();
            RefreshChest();
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
        PlaceType = heldType is >= 1 and <= BlockInventory.Chest ?
            heldType : 0;
        if (PlaceType == BlockInventory.Chest) PlaceShape = 0;
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
            (PlaceType is < 1 or > BlockInventory.Chest ||
             (!CreativeMode &&
              _inventory.Slot(SelectedHotbar).Quantity == 0)))
            return;

        if (_world.TryEditFromRay(origin, direction, Math.Clamp(Reach, 1f, 8f),
            breaking, GameObject, selected.Packed,
            out ushort previous, out _, out BlockPosition editedPosition,
            out SectionedEditSnapshot beforeEdit,
            out IReadOnlyDictionary<ChunkKey, int> beforeRevisions))
        {
            if (breaking)
            {
                // Record tool wear before journaling so an interrupted
                // direct-credit reward also recovers the correct durability.
                string toolsBefore = _tools.Serialize();
                bool toolsChanged = _tools.OnBlockMined();
                if (toolsChanged)
                    ToolState = _tools.Serialize();

                int material = new BlockState(previous).Type;
                Vector3 droppedAt = new(
                    editedPosition.X + .5f,
                    editedPosition.Y + .55f,
                    editedPosition.Z + .5f);
                if (WorldDrop.TrySpawn(Scene, material, droppedAt))
                {
                    if (toolsChanged) _profileDirty = true;
                }
                else if (!TryReceiveMinedFallback(material, editedPosition,
                    previous, beforeEdit, beforeRevisions))
                {
                    // No item must be lost if a pickup is impossible (such
                    // as a full 999-item cap), or before the receipt exists.
                    // A partial disk transaction instead locks the world and
                    // is safely replayed after restart.
                    if (!_world.PersistenceLocked)
                    {
                        if (_world.TryUndoFailedMining(editedPosition, previous))
                        {
                            if (!_tools.TryLoad(toolsBefore))
                                throw new InvalidOperationException(
                                    "Could not revert tool wear after failed reward.");
                            ToolState = _tools.Serialize();
                        }
                        else
                            _world.HaltAfterRewardFailure();
                    }
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

    // The original immediate-mode GUI has been replaced by UI/tallowcraft.tui.
    // All state modifications still go through these server-validated RPCs.
}
