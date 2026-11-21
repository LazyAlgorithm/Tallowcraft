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
    // Pick-block locks the sampled absolute block facing and slab/stair
    // half while the picked hotbar selection remains active.
    [Sync] public bool PickedAbsoluteFacing = false;
    [Sync] public bool PickedUpper = false;
    [Sync] public string PickFeedback = "";
    [Sync] public bool CreativeMode = true;
    [Sync] public string CreativeStatus = "";
    [Sync] public int TestWeapon;
    [Sync] public int TestShotCount;
    private float _testShotCooldown;

    public string TestWeaponName => TestWeapon == 1 ? "Spark pistol" : "Pulse repeater";
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
        "Coal", "Iron", "Leaves", "Iron ingots", "Chest",
        "Cobblestone", "Stone bricks", "Mossy stone", "Dark timber",
        "Glass", "Red bricks", "Sandstone", "Slate"
    };
    private static readonly string[] ShapeNames =
        { "Cube", "Slab", "Stair", "Half wall", "Post", "Beam" };
    private static readonly string[] ItemShort =
        { "GRS", "DRT", "STN", "LOG", "FRG", "PLK", "COL", "ORE", "LEF", "ING", "CHT", "COB", "BRK", "MOS", "TMB", "GLS", "RED", "SND", "SLT" };
    private static readonly Color[] ItemColors =
    {
        new(.33f,.66f,.20f), new(.48f,.30f,.19f), new(.50f,.53f,.54f),
        new(.54f,.35f,.21f), new(.57f,.49f,.34f), new(.77f,.61f,.36f),
        new(.25f,.27f,.30f), new(.65f,.53f,.45f), new(.25f,.58f,.23f),
        new(.75f,.73f,.69f), new(.61f,.38f,.19f),
        new(.48f,.50f,.53f), new(.58f,.59f,.60f),
        new(.27f,.50f,.31f), new(.49f,.31f,.19f),
        new(.58f,.78f,.85f), new(.64f,.32f,.27f),
        new(.77f,.65f,.47f), new(.30f,.36f,.40f)
    };

    private BlockWorld? _world;
    private float _cooldown;
    private float _craftCooldown;
    private float _pickCooldown;
    private float _pickFeedbackSeconds;
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
    private bool _cursorReturnLockBefore;
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
    [Sync] public bool ReturnToSpawnPending;
    private Vector3 _returnDestination;
    private float _returnSecondsLeft;
    private int _returnSequence;
    private int _reservedReturnOwner = -1;
    private bool _returnClientReady;
    private bool _returnLocalPending;
    private float _returnLocalSeconds;
    private int _returnLocalSequence;
    private bool _returnReadySent;

    /// <summary>Blocks movement, look, mining and tools until both peers
    /// confirm that the destination is generated and collidable.</summary>
    public bool ReturnToSpawnLocked => ReturnToSpawnPending || _returnLocalPending;

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
    internal void UiReturnToSpawn()
    {
        if (_returnLocalPending || ReturnToSpawnPending) return;
        _returnLocalPending = true;
        _returnLocalSeconds = 45f;
        _returnLocalSequence = 0;
        _returnReadySent = false;
        Rpc(nameof(RequestReturnToSpawn));
    }

    /// <summary>Voluntary, server-authorized return to the original spawn
    /// point. No health, death or inventory loss is involved. Clients cannot
    /// provide arbitrary destination coordinates or bypass the cooldown.</summary>
    [Rpc(RpcTarget.Server)]
    private void RequestReturnToSpawn()
    {
        if (!Scene.IsServer) return;
        int owner = GameObject.OwnerClientId;
        if (!ProfileReady || ReturnToSpawnPending ||
            _returnToSpawnCooldown > 0f ||
            (Scene.IsNetworked && RpcSender != owner))
        {
            Rpc(nameof(NotifyReturnFinished), owner, 0, false);
            if (IsLocal) ResetLocalReturn();
            return;
        }

        _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        if (_world == null || _world.PersistenceLocked ||
            _world.WorldIdentity.Length == 0 ||
            GetCharacterController() is not { } controller ||
            !controller.HasCharacter)
        {
            Rpc(nameof(NotifyReturnFinished), owner, 0, false);
            if (IsLocal) ResetLocalReturn();
            return;
        }

        Vector3 destination = new(_serverSpawnXZ.X, 0f, _serverSpawnXZ.Y);
        destination.Y = _world.SafeSpawnHeight(destination.X, destination.Z, 2f);
        if (!WaypointBook.ValidPosition(destination))
        {
            Rpc(nameof(NotifyReturnFinished), owner, 0, false);
            if (IsLocal) ResetLocalReturn();
            return;
        }

        _returnSequence = _returnSequence == int.MaxValue ? 1 : _returnSequence + 1;
        _returnDestination = destination;
        _returnSecondsLeft = 40f;
        ReturnToSpawnPending = true;
        _returnClientReady = !Scene.IsNetworked || owner == 0;
        // This reservation keeps full 16³ chunks and Jolt colliders loaded
        // around the destination, regardless of the host camera's LOD.
        _reservedReturnOwner = owner;
        _world.BeginServerReturnPreload(owner, destination);
        if (_returnClientReady)
        {
            _returnLocalPending = true; // local host also stays input locked
            _returnLocalSeconds = 45f;
            _returnLocalSequence = _returnSequence;
        }
        else
            Rpc(nameof(NotifyReturnStarted), owner, _returnSequence,
                destination.X, destination.Y, destination.Z);
    }

    [Rpc(RpcTarget.Everyone)]
    private void NotifyReturnStarted(int owner, int sequence,
        float x, float y, float z)
    {
        if (Scene.IsServer || Scene.LocalClientId != owner || !IsLocal)
            return;
        Vector3 destination = new(x, y, z);
        if (!WaypointBook.ValidPosition(destination)) return;
        _returnLocalPending = true;
        _returnLocalSeconds = 45f;
        _returnLocalSequence = sequence;
        _returnReadySent = false;
        _returnDestination = destination;
        _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        _world?.BeginLocalReturnPreload(destination);
    }

    [Rpc(RpcTarget.Server)]
    private void ConfirmReturnAreaReady(int sequence)
    {
        if (!Scene.IsServer || !ReturnToSpawnPending ||
            sequence != _returnSequence ||
            RpcSender != GameObject.OwnerClientId)
            return;
        _returnClientReady = true;
    }

    [Rpc(RpcTarget.Everyone)]
    private void NotifyReturnFinished(int owner, int sequence, bool succeeded)
    {
        if (Scene.IsServer || !IsLocal || Scene.LocalClientId != owner ||
            (sequence != 0 && sequence != _returnLocalSequence))
            return;
        ResetLocalReturn();
        if (!succeeded)
            Console.WriteLine("Return to spawn canceled: destination was not ready.");
    }

    private void ResetLocalReturn()
    {
        _returnLocalPending = false;
        _returnReadySent = false;
        _returnLocalSequence = 0;
        _returnLocalSeconds = 0;
        _world?.EndLocalReturnPreload();
        // LocalUpdate updates Player input-map state on its next pass.
        _cursorInitialized = false;
    }

    private void FinishReturn(bool success)
    {
        if (!ReturnToSpawnPending) return;
        ReturnToSpawnPending = false;
        _world?.EndServerReturnPreload(_reservedReturnOwner);
        _reservedReturnOwner = -1;
        if (success) _returnToSpawnCooldown = 10f;
        Rpc(nameof(NotifyReturnFinished), GameObject.OwnerClientId,
            _returnSequence, success);
        if (Scene.IsServer && IsLocal)
            ResetLocalReturn();
    }

    private void TickReturn(float deltaTime)
    {
        if (!ReturnToSpawnPending) return;
        _returnSecondsLeft -= MathF.Max(0f, deltaTime);
        if (_returnSecondsLeft <= 0f)
        {
            FinishReturn(false); // never teleport into unloaded or stale meshes
            return;
        }
        if (!_returnClientReady || _world == null ||
            !_world.ReturnAreaReady(_returnDestination))
            return;
        if (GetCharacterController() is not { } controller ||
            !controller.HasCharacter ||
            !controller.Teleport(_returnDestination))
        {
            FinishReturn(false);
            return;
        }
        Transform.Position = _returnDestination;
        FinishReturn(true);
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

    internal string PartyName { get; private set; } = "";

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
    private void BindPlayerProfile(string token, string displayName)
    {
        if (!Scene.IsServer || !Scene.IsNetworked ||
            GameObject.OwnerClientId <= 0 ||
            RpcSender != GameObject.OwnerClientId ||
            !PlayerIdentity.Valid(token))
            return;
        if (TryBindProfile(token))
            PartyName = new string(displayName.Take(32).Where(c => !char.IsControl(c)).ToArray()).Trim();
    }

    public override void LocalUpdate(float deltaTime)
    {
        if (!IsLocal)
            return;
        if (_returnLocalPending)
        {
            _returnLocalSeconds -= MathF.Max(0f, deltaTime);
            if (_returnLocalSeconds <= 0f)
            {
                Console.WriteLine("Return to spawn: local terrain preload timed out.");
                ResetLocalReturn();
            }
            else if (!Scene.IsServer && _returnLocalSequence != 0 &&
                !_returnReadySent)
            {
                _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
                if (_world != null && _world.ReturnAreaReady(_returnDestination))
                {
                    _returnReadySent = true;
                    Rpc(nameof(ConfirmReturnAreaReady), _returnLocalSequence);
                }
            }
        }

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
                try { Rpc(nameof(BindPlayerProfile), LocalIdentity("client"), Steam.UserName); }
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
        if (!open && !_mainMenu && !_paused && !ReturnToSpawnLocked)
        {
            // Middle-click reads only the local camera direction, not an
            // untrusted client block coordinate. The server raycasts its
            // own authoritative world at the owner's character position.
            if (ProfileReady && Input.GetMouseButtonDown(MouseButton.Middle) &&
                Scene.Find("Main Camera") is { IsValid: true } camera)
            {
                Vector3 direction = camera.Transform.Forward;
                Rpc(nameof(PickBlockAtCrosshair),
                    direction.X, direction.Y, direction.Z);
            }

            if (!InventoryOpen && ProfileReady && InputActions.Pressed("Player/Interact") &&
                Scene.Find("Main Camera") is { IsValid: true } eye)
            {
                Vector3 look = eye.Transform.Forward;
                Rpc(nameof(InteractWithChest), look.X, look.Y, look.Z);
            }

            // Server owns durability/equipment; hotkeys send only the tool id.
            // 1-6 continue selecting building blocks in the networked hotbar.
            int tool = Input.GetKeyDown(KeyCode.Alpha7) ? 1 :
                Input.GetKeyDown(KeyCode.Alpha8) ? 2 :
                Input.GetKeyDown(KeyCode.Alpha9) ? 3 :
                Input.GetKeyDown(KeyCode.Alpha0) ? 0 : -1;
            if (tool >= 0)
                Rpc(nameof(EquipToolQuick), tool);
            else if (CreativeMode && Input.GetKeyDown(KeyCode.Z))
                Rpc(nameof(SelectTestWeapon), 1);
            else if (CreativeMode && Input.GetKeyDown(KeyCode.X))
                Rpc(nameof(SelectTestWeapon), 2);
        }
        _ui?.Refresh();

        if (!_cursorInitialized || _cursorMenuOpen != open ||
            _cursorGameMenuOpen != UiGameMenuVisible ||
            _cursorReturnLockBefore != ReturnToSpawnLocked)
        {
            // Set the input map before unlocking/locking the mouse. The engine
            // clears relative mouse motion when changing cursor-lock mode.
            bool gameplay = !open && !_mainMenu && !_paused &&
                !ReturnToSpawnLocked;
            InputActions.SetMapEnabled("Player", gameplay);
            Cursor.Locked = gameplay;
            _cursorReturnLockBefore = ReturnToSpawnLocked;
            _cursorMenuOpen = open;
            _cursorGameMenuOpen = UiGameMenuVisible;
            _cursorInitialized = true;
        }
    }

    public override void OnDestroy()
    {
        if (Scene.IsServer)
        {
            if (_reservedReturnOwner >= 0)
                _world?.EndServerReturnPreload(_reservedReturnOwner);
            SaveProfile();
            if (_profileToken.Length != 0)
                PlayerProfileClaims.Release(_profileWorldId, _profileToken);
        }
        // The engine may already have destroyed the player identity. Only
        // the local owner created a HUD and changed the shared input map.
        if (!_uiObject.HasValue) return;
        _world?.EndLocalReturnPreload();
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
    public MiningTool VisibleTool => DisplayTools().Equipped;
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
    internal void UiGiveCreativeBlock(int type, int quantity) =>
        Rpc(nameof(GrantCreativeBlock), type, quantity);
    internal void UiCloseMenu() => CloseMenuLocally();

    /// <summary>Server-authoritative middle-click. The packet contains a
    /// look direction only, never a target block position or inventory state.
    /// The server raycasts locally, validates the target and applies only
    /// the pure inventory picker rules. No terrain is modified.</summary>
    [Rpc(RpcTarget.Server)]
    private void PickBlockAtCrosshair(float lookX, float lookY, float lookZ)
    {
        if (!Scene.IsServer || !ProfileReady || InventoryOpen ||
            _pickCooldown > 0f ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId))
            return;
        _pickCooldown = .18f;
        Vector3 look = new(lookX, lookY, lookZ);
        if (!float.IsFinite(lookX) || !float.IsFinite(lookY) ||
            !float.IsFinite(lookZ) || look.LengthSquared() < .25f ||
            look.LengthSquared() > 4f)
            return;
        _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        if (_world == null || _world.PersistenceLocked)
            return;
        Vector3 origin = Transform.Position + Vector3.UnitY * EyeHeight;
        if (!_world.TryQueryFromRay(origin, look,
                Math.Clamp(Reach, 1f, 8f), GameObject,
                out BlockPosition target, out _))
        {
            PickFeedback = "Pick block: aim at a nearby block";
            _pickFeedbackSeconds = 2.5f;
            return;
        }

        ushort packed = _world.BlockAt(target);
        if (!BlockPicker.TryPick(_inventory, packed, SelectedHotbar,
                CreativeMode, out int selected, out bool changed))
        {
            PickFeedback = CreativeMode ?
                "Pick block: no free inventory slot" :
                "Pick block: material not in inventory";
            _pickFeedbackSeconds = 2.5f;
            return;
        }

        BlockState copied = new(packed);
        SelectedHotbar = selected;
        PlaceType = copied.Type;
        PlaceShape = (int)copied.Shape;
        FacingOffset = copied.Facing;
        PickedAbsoluteFacing = true;
        PickedUpper = copied.Upper;
        if (changed)
            InventoryState = _inventory.Serialize();
        _profileDirty = true;
        PickFeedback = $"Copied {BlockNames[copied.Type - 1]} " +
            $"{ShapeNames[(int)copied.Shape]} (facing {copied.Facing * 90} deg)" +
            (copied.Upper ? " upper" : "");
        _pickFeedbackSeconds = 2.5f;
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
            return held.Type is >= 1 and <= BlockInventory.MaxType &&
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

    /// <summary>
    /// Creative catalog clicks are only suggestions. The authoritative server
    /// validates the owning player, creative privileges, item type, quantity,
    /// physical inventory capacity and persistent profile before granting.
    /// A client can never supply the resulting serialized inventory state.
    /// </summary>
    [Rpc(RpcTarget.Server)]
    private void GrantCreativeBlock(int type, int quantity)
    {
        if (!Scene.IsServer || !ProfileReady || !CreativeMode ||
            !InventoryOpen || ChestOpen || ReturnToSpawnPending ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId) ||
            !BlockInventory.IsCreativeBlock(type) ||
            quantity is not (1 or BlockInventory.StackLimit))
            return;

        if (!_inventory.TryGrantCreativeBlock(type, quantity))
        {
            CreativeStatus = "Not enough space, or 999 of this block already owned.";
            return;
        }

        InventoryState = _inventory.Serialize();
        _profileDirty = true;
        CreativeStatus = $"Added {quantity} {BlockNames[type - 1]} to inventory.";
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

    [Rpc(RpcTarget.Server)]
    private void InteractWithChest(float x, float y, float z)
    {
        if (!Scene.IsServer || !ProfileReady || InventoryOpen || ReturnToSpawnPending ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId))
            return;
        Vector3 eye = Transform.Position + Vector3.UnitY * EyeHeight;
        _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        if (_world == null || _world.PersistenceLocked ||
            !_world.TryQueryFromRay(eye, new Vector3(x, y, z), 4.5f, GameObject,
                out BlockPosition pos, out _) ||
            Vector3.DistanceSquared(eye, new Vector3(pos.X+.5f,pos.Y+.5f,pos.Z+.5f)) > 4.5f*4.5f ||
            !_world.TryReadChest(pos, out ChestContents contents))
            return;
        ChestX = pos.X;
        ChestY = pos.Y;
        ChestZ = pos.Z;
        ChestState = contents.Serialize();
        ChestStatus = "Choose a stack and its destination.";
        ChestOpen = true;
        InventoryOpen = true;
        ResetMining();
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
        if (open) CreativeStatus = "";
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
            TestWeapon = 0;
            ToolState = _tools.Serialize();
            _profileDirty = true;
        }
    }

    [Rpc(RpcTarget.Server)]
    private void SelectTestWeapon(int weapon)
    {
        if (!Scene.IsServer || !CreativeMode || !ProfileReady || InventoryOpen ||
            ReturnToSpawnPending || weapon is < 1 or > 2 ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId))
            return;

        TestWeapon = TestWeapon == weapon ? 0 : weapon;
        _testShotCooldown = 0f;
        ResetMining();
    }

    [Rpc(RpcTarget.Server)]
    private void EquipToolQuick(int kind)
    {
        if (!Scene.IsServer || !ProfileReady || InventoryOpen ||
            (Scene.IsNetworked && RpcSender != GameObject.OwnerClientId))
            return;
        TestWeapon = 0;
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

    /// <summary>
    /// Resolve gun impacts from the server's own character position and
    /// replicated look input. The client never supplies a target or damage.
    /// Jolt's raycast includes CharacterControllers, so peaceful mobs need
    /// no duplicate physics bodies or extra shooting hitboxes.
    /// </summary>
    private void FireAtPeacefulAnimals(int weapon, Quaternion lookRotation)
    {
        if (!Scene.IsServer || AnimalCombatRules.Damage(weapon) == 0)
            return;

        Vector3 direction = Vector3.Transform(Vector3.UnitZ, lookRotation);
        Vector3 origin = Transform.Position + Vector3.UnitY * EyeHeight;
        foreach (PhysicsHit hit in Scene.RaycastAll(origin, direction,
                     AnimalCombatRules.Range(weapon)))
        {
            if (hit.HitObject is not { IsValid: true } struck)
                break; // An unknown physics obstruction still stops the shot.

            // The local player's character controller may be encountered
            // first. Ignore only this player and its own visual children.
            GameObject current = struck;
            bool ownObject = false;
            for (int i = 0; i < 16; i++)
            {
                if (current == GameObject) { ownObject = true; break; }
                if (current.Parent is not { IsValid: true } parent) break;
                current = parent;
            }
            if (ownObject) continue;

            // The Jolt character should return the mob root, but walking
            // parents also supports future colliders on detailed model parts.
            current = struck;
            for (int i = 0; i < 16; i++)
            {
                PeacefulAnimal? animal = current.GetComponent<PeacefulAnimal>();
                if (animal != null)
                {
                    int damage = AnimalCombatRules.Damage(weapon);
                    if (animal.TryTakeDamage(damage, out int remaining))
                    {
                        PickFeedback = remaining == 0
                            ? $"{animal.Species} defeated"
                            : $"{animal.Species} -{damage} ({remaining} HP)";
                        _pickFeedbackSeconds = .85f;
                    }
                    return;
                }
                if (current.Parent is not { IsValid: true } parent) break;
                current = parent;
            }

            // Terrain, glass, props and other players obstruct bullets.
            // Do not accidentally damage an animal behind the nearest hit.
            break;
        }
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
        if (ReturnToSpawnPending)
        {
            ResetMining();
            TickReturn(deltaTime);
            return;
        }
        _pickCooldown = MathF.Max(0f, _pickCooldown - deltaTime);
        if (_pickFeedbackSeconds > 0f)
        {
            _pickFeedbackSeconds = MathF.Max(0f,
                _pickFeedbackSeconds - deltaTime);
            if (_pickFeedbackSeconds <= 0f) PickFeedback = "";
        }
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
        {
            PlaceShape = (PlaceShape + 1) % (BlockState.MaxShape + 1);
            // Selecting another shape restores aim-based placement while
            // preserving the current facing at the moment of the change.
            if (PickedAbsoluteFacing)
            {
                var look = Vector3.Transform(Vector3.UnitZ, input.LookRotation);
                FacingOffset = (FacingOffset - BlockState.FacingTowardPlayer(look)) & 3;
                PickedAbsoluteFacing = false;
            }
            PickedUpper = false;
        }
        if (input.Pressed(InputButton.Ability2))
            FacingOffset = (FacingOffset + 1) % 4;

        SelectedHotbar = Math.Clamp(SelectedHotbar, 0,
            BlockInventory.HotbarSlots - 1);
        if (previousHotbar != SelectedHotbar)
        {
            // Manual hotbar changes restore the normal player-relative
            // rotation and crouch-to-place-upper behavior. Do not treat
            // a copied world-axis facing as a relative rotation offset.
            if (PickedAbsoluteFacing) FacingOffset = 0;
            PickedAbsoluteFacing = false;
            PickedUpper = false;
        }
        int heldType = _inventory.Slot(SelectedHotbar).Type;
        PlaceType = heldType is >= 1 and <= BlockInventory.MaxType &&
            heldType != BlockInventory.IronIngot ?
            heldType : 0;
        if (PlaceType == BlockInventory.Chest) PlaceShape = 0;
        PlaceShape = Math.Clamp(PlaceShape, 0, BlockState.MaxShape);
        FacingOffset &= 3;
        if (previousHotbar != SelectedHotbar || previousType != PlaceType ||
            previousShape != PlaceShape || previousFacing != FacingOffset)
            _profileDirty = true;

        if (TestWeapon != 0)
        {
            ResetMining();
            if (CreativeMode)
            {
                _testShotCooldown = MathF.Max(0, _testShotCooldown - deltaTime);
                bool trigger = TestWeapon == 1 ? input.Pressed(InputButton.Use) : input.Held(InputButton.Use);
                if (trigger && _testShotCooldown <= 0)
                {
                    _testShotCooldown = TestWeapon == 1 ? .32f : .16f;
                    TestShotCount = TestShotCount == int.MaxValue ? 1 : TestShotCount + 1;
                    FireAtPeacefulAnimals(TestWeapon, input.LookRotation);
                }
                return;
            }
            TestWeapon = 0;
        }

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
        BlockState selected = BlockPicker.SelectedState(
            PlaceType, PlaceShape, FacingOffset, PickedAbsoluteFacing,
            PickedUpper, input.Crouch,
            BlockState.FacingTowardPlayer(direction));

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
            (PlaceType is < 1 or > BlockInventory.MaxType ||
               PlaceType == BlockInventory.IronIngot ||
             (!CreativeMode &&
              _inventory.Slot(SelectedHotbar).Quantity == 0)))
            return;

        if (_world.TryEditFromRay(origin, direction, Math.Clamp(Reach, 1f, 8f),
            breaking, GameObject, selected.Packed,
            out ushort previous, out _, out BlockPosition editedPosition,
            out SectionedEditSnapshot beforeEdit,
            out IReadOnlyDictionary<ChunkKey, int> beforeRevisions,
            aimSelectsHalf: !PickedAbsoluteFacing))
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
