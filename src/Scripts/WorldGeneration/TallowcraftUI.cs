using System;
using System.IO;
using System.Numerics;
using System.Threading;
using Tallow;

namespace WorldGeneration;

/// <summary>
/// Local-only .tui code-behind. The actual inventory, chest, and equipment
/// remain owned by BlockEditor on the server. This object has no network
/// identity and stores no items; it only renders synchronized snapshots and
/// submits the already-validated RPC requests through its owning player.
/// </summary>
public sealed class TallowcraftUI : Script
{
    private static readonly string[] Images =
    {
        "", "grass", "dirt", "stone", "log", "forge", "planks",
        "coal", "iron_ore", "leaves", "iron_ingot", "chest",
        "cobblestone", "stone_bricks", "mossy_stone", "dark_timber",
        "glass", "red_bricks", "sandstone", "slate"
    };

    private BlockEditor? _owner;
    private string _inventorySnapshot = "";
    private string _chestSnapshot = "";
    private string _toolSnapshot = "";
    private bool _readyBefore;
    private bool _menuBefore;
    private bool _gameMenuBefore;
    private bool _chestBefore;
    private string _chestMessageBefore = "";
    private bool _partyOpen;
    private readonly SteamParty _friends = new();
    private bool _inviteOpen;
    private BuildPreview? _preview;
    private BlockWorld? _partyWorld;
    private int _selectedBefore = -1;
    private int _source = -1;
    private bool _moveHalf;
    private string _page = "craft";
    private int _creativeQuantity = BlockInventory.StackLimit;
    private static readonly int[] CreativeTypes =
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9,
        11, 12, 13, 14, 15, 16, 17, 18, 19
    };
    private float _refreshClock;
    private const int VisibleRecipes = 4;
    private static readonly int[] RecipeIds = { 1, 2, 3, 6, 7, 8, 9, 10, 11, 12, 13, 14 };
    private int _recipeScroll;
    private bool _recipeScrollInitialized;
    private bool _initialized;
    private bool _settingsLoaded;
    private WaypointBook _waypoints = new();
    private readonly int[] _compassPrevious = { -1, -1, -1, -1 };
    private readonly string[] _compassTickLabels = new string[WaypointCompass.Slots];
    private bool _compassInitialized;
    private GameObject? _compassCamera;
    private string _waypointWorldId = "";
    private string _waypointToken = "";
    private bool _waypointLoadError;
    private bool _waypointsOpen;
    private bool _respawnConfirm;
    private int _viewChoice = 1;
    private int _heightChoice = 1;
    private bool _shadows = true;
    private bool _lodEnabled = true;
    private int _lodQualityChoice = 1;
    private static readonly float[] LodScales = { 0.75f, 1f, 1.25f };
    // Zero means automatic; positive values are explicit worker counts.
    private int _workerSelection;
    private float _fpsSeconds;
    private int _fpsFrames;
    // Avoid hundreds of native Find/Visible calls whenever one stack count
    // changes. Each slot initializes once, then switches only its old/new art.
    private readonly bool[] _slotDrawn = new bool[72];
    private readonly int[] _lastType = new int[72];
    private readonly int[] _lastQuantity = new int[72];

    public void Bind(BlockEditor owner) => _owner = owner;

    private UIDocument Document => GetComponent<UIDocument>();

    private string SettingsPath => Path.Combine(GamePaths.PlayerData,
        "world-display-settings.txt");

    private void LoadSettings()
    {
        if (_settingsLoaded) return;
        _settingsLoaded = true;
        try
        {
            string[] lines = File.ReadAllLines(SettingsPath);
            if (lines.Length >= 3 &&
                int.TryParse(lines[0], out int view) &&
                int.TryParse(lines[1], out int height) &&
                bool.TryParse(lines[2], out bool shadows))
            {
                _viewChoice = Math.Clamp(view, 0, WorldDisplaySettings.Distances.Length - 1);
                _heightChoice = Math.Clamp(height, 0, 2);
                _shadows = shadows;
                if (lines.Length >= 4 && int.TryParse(lines[3], out int workers))
                    _workerSelection = Math.Clamp(workers, 0, MaxWorkers());
                if ((lines.Length == 6 || lines.Length >= 8) &&
                    bool.TryParse(lines[4], out bool lodEnabled) &&
                    int.TryParse(lines[5], out int quality))
                {
                    _lodEnabled = lodEnabled;
                    _lodQualityChoice = Math.Clamp(quality, 0, LodScales.Length - 1);
                }
                // Preserve the effective settings from older Auto quality saves.
                if (lines.Length == 7 && bool.TryParse(lines[4], out bool automatic) && automatic)
                {
                    if (int.TryParse(lines[5], out int distance))
                        _viewChoice = Math.Clamp(distance, 0, WorldDisplaySettings.Distances.Length - 1);
                    if (bool.TryParse(lines[6], out bool savedShadows))
                        _shadows = savedShadows;
                }
            }
        }
        catch (FileNotFoundException) { }
        catch (Exception e) { Console.WriteLine($"Tallowcraft settings: {e.Message}"); }
        ApplySettings();
    }

    private void ApplySettings()
    {
        int maxWorkers = MaxWorkers();
        int workers = WorldDisplaySettings.WorkerCount(_workerSelection, maxWorkers);
        int distance = _viewChoice;
        bool shadows = _shadows;
        var world = Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        if (world is not null)
        {
            world.RenderRadius = WorldDisplaySettings.Distances[distance];
            world.SideRadius = WorldDisplaySettings.SideDistances[distance];
            world.BehindRadius = WorldDisplaySettings.BehindDistances[distance];
            world.VerticalRadius = _heightChoice;
            world.ChunkShadowsEnabled = shadows;
            world.WorkerCount = workers;
            world.QuadtreeLodEnabled = _lodEnabled;
            world.LodQuality = LodScales[_lodQualityChoice];
            // Uploads run on the render thread, so retain its existing safe cap.
            world.UploadsPerFrame = Math.Clamp((workers + 1) / 2, 1, 4);
            // Keep distant terrain visible through the scene's fog.
            var environment = Scene.Environment;
            environment.FogDensity = .004f * Math.Min(1f,
                6f / (_lodEnabled ? world.RenderRadius : Math.Min(20, world.RenderRadius)));
        }
        ThreadPool.GetMinThreads(out int minWorkers, out int minIo);
        if (workers > minWorkers)
            ThreadPool.SetMinThreads(workers, minIo);
        string[] heights = { "Near", "Balanced", "Far" };
        int shownDistance = _lodEnabled ? WorldDisplaySettings.Distances[distance] :
            Math.Min(20, WorldDisplaySettings.Distances[distance]);
        Text("view-setting", $"View distance: {WorldDisplaySettings.DistanceNames[distance]} ({shownDistance} chunks ahead)");
        Text("lod-setting", "Quadtree terrain LOD: " + (_lodEnabled ? "On" : "Off (full meshes, 20 max)"));
        string[] qualities = { "Performance", "Balanced", "High detail" };
        Text("lod-quality-setting", "LOD quality: " + qualities[_lodQualityChoice]);
        Text("height-setting", "Vertical range: " + heights[_heightChoice]);
        Text("shadow-setting", "Block shadows: " + (shadows ? "On" : "Off"));
        Text("threads-setting", _workerSelection == 0 ?
            $"Chunk threads: Auto ({workers} of {maxWorkers})" :
            $"Chunk threads: {workers} of {maxWorkers}");
    }

    private static int MaxWorkers()
    {
        ThreadPool.GetMaxThreads(out int maxPoolWorkers, out _);
        return Math.Max(1, Math.Min(Environment.ProcessorCount, maxPoolWorkers));
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(GamePaths.PlayerData);
            File.WriteAllLines(SettingsPath, new[] {
                _viewChoice.ToString(), _heightChoice.ToString(),
                _shadows.ToString(), _workerSelection.ToString(),
                _lodEnabled.ToString(), _lodQualityChoice.ToString() });
        }
        catch (Exception e) { Console.WriteLine($"Tallowcraft settings: {e.Message}"); }
    }

    /// <summary>World/player-specific landmarks live only in this machine's
    /// player-data directory. They are never transmitted or used as trusted
    /// teleport targets by the multiplayer server.</summary>
    private void EnsureWaypointsLoaded()
    {
        string worldId = Scene.Find("BlockWorld")?
            .GetComponent<BlockWorld>()?.ClientWorldIdentity ?? "";
        if (!PlayerIdentity.Valid(worldId)) return;
        string token;
        try
        {
            string? slot = Environment.GetEnvironmentVariable(
                "TALLOWCRAFT_PROFILE_SLOT");
            token = PlayerIdentity.GetOrCreate(GamePaths.PlayerData,
                string.IsNullOrEmpty(slot) ?
                    Scene.IsServer ? "host" : "client" : slot);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Tallowcraft navigation identity: {e.Message}");
            return;
        }
        if (_waypointWorldId == worldId && _waypointToken == token)
            return;
        _waypointWorldId = worldId;
        _waypointToken = token;
        _compassInitialized = false;
        _waypointLoadError = false;
        try
        {
            _waypoints = WorldWaypointStore.Load(GamePaths.PlayerData,
                worldId, token);
        }
        catch (Exception e)
        {
            // Corrupt or inaccessible files are never silently overwritten.
            _waypoints = new WaypointBook();
            _waypointLoadError = true;
            Console.WriteLine($"Tallowcraft waypoints unavailable: {e.Message}");
        }
    }

    private void SaveWaypoints()
    {
        if (_waypointLoadError || _waypointWorldId.Length == 0 ||
            _waypointToken.Length == 0)
            return;
        try
        {
            WorldWaypointStore.Save(GamePaths.PlayerData,
                _waypointWorldId, _waypointToken, _waypoints);
        }
        catch (Exception e)
        {
            _waypointLoadError = true;
            Console.WriteLine($"Tallowcraft waypoints cannot be saved: {e.Message}");
        }
    }

    private static bool WaypointSlot(string id, string prefix, out int slot)
    {
        slot = -1;
        return id.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(id.AsSpan(prefix.Length), out slot) &&
            slot >= 0 && slot < WaypointBook.SlotCount;
    }

    /// <summary>
    /// The 13 fixed image slots avoid mutating UI layout at runtime.
    /// Icons appear only for bookmarks actually saved in WaypointBook.
    /// Hide the old bin before showing a new one to avoid duplicate markers.
    /// </summary>
    private void UpdateCompass()
    {
        if (_owner is not { } editor) return;
        if (!_compassInitialized)
        {
            for (int index = 0; index < WaypointBook.SlotCount; index++)
            for (int bin = 0; bin < WaypointCompass.MarkerSlots; bin++)
                Visible($"compass-marker-{index}-{bin}", false);
            Array.Fill(_compassPrevious, -1);
            Array.Fill(_compassTickLabels, "");
            _compassInitialized = true;
        }

        // Read the locally owned camera every frame, never replicated
        // player yaw or a 200ms timer. Cache the handle, not its rotation.
        if (_compassCamera is not { IsValid: true })
            _compassCamera = Scene.Find("Main Camera");
        Vector3 look = _compassCamera is { } validCamera &&
            validCamera.IsValid
                ? validCamera.Transform.Forward : editor.Transform.Forward;
        Vector3 here = editor.Transform.Position;
        // No native UI reads are needed on frames where a label is stable.
        // Only the compass' few changed icons toggle visibility on a turn.
        for (int bin = 0; bin < WaypointCompass.Slots; bin++)
        {
            string label = WaypointCompass.TickLabel(look, bin);
            if (_compassTickLabels[bin] == label) continue;
            Text("compass-tick-" + bin, label);
            _compassTickLabels[bin] = label;
        }

        for (int index = 0; index < WaypointBook.SlotCount; index++)
        {
            Vector3? destination = _waypoints.Position(index);
            int next = destination is Vector3 target
                ? WaypointCompass.Slot(here, look, target) : -1;
            int previous = _compassPrevious[index];
            if (next == previous) continue;
            if (previous >= 0)
                Visible($"compass-marker-{index}-{previous}", false);
            if (next >= 0)
                Visible($"compass-marker-{index}-{next}", true);
            _compassPrevious[index] = next;
        }
    }

    private void UpdateNavigation()
    {
        if (_owner is not { } editor) return;
        Vector3 here = editor.Transform.Position;
        Text("hud-coordinates", $"XYZ {MathF.Floor(here.X):F0}, " +
            $"{MathF.Floor(here.Y):F0}, {MathF.Floor(here.Z):F0}");
        int tracked = _waypoints.Tracked;
        Vector3? goal = _waypoints.Position(tracked);
        Text("hud-waypoint", goal is Vector3 destination ?
            $"{WaypointBook.Names[tracked]}  {MathF.Round(Vector3.Distance(
                here, destination)):F0}m {WaypointBook.Direction(here, destination)}" :
            "Mark places from Pause > Waypoints");

        Text("waypoint-status", _waypointLoadError ?
            "Landmark file needs repair. Check the game log." :
            _waypointWorldId.Length == 0 ?
            "Waiting for the world's identity..." :
            "Personal markers are saved on this device, per world.");
        for (int slot = 0; slot < WaypointBook.SlotCount; slot++)
        {
            Vector3? position = _waypoints.Position(slot);
            Text("waypoint-info-" + slot,
                position is Vector3 p ?
                $"{WaypointBook.Names[slot]}: " +
                $"{MathF.Floor(p.X):F0}, {MathF.Floor(p.Y):F0}, {MathF.Floor(p.Z):F0}" :
                WaypointBook.Names[slot] + ": not marked");
            Text("waypoint-track-" + slot, _waypoints.Tracked == slot ?
                "Untrack" : "Track");
        }
    }

    public void OnParty()
    {
        _partyOpen = !_partyOpen;
        _inviteOpen = false;
    }

    public void OnInvite() => _inviteOpen = !_inviteOpen;
    public void OnFriend(string id)
    {
        if (_inviteOpen && Scene.IsServer && Network.IsConnected(Scene)) _friends.Choose(id, true);
    }
    public void OnPrevious() => _friends.Turn(-1);
    public void OnNext() => _friends.Turn(1);
    public void OnLeave()
    {
        Network.Leave(Scene);
        SceneManager.Load(ExpeditionMenu.Title);
    }

    public void OnPlay()
    {
        Audio.PlayOneShot2D("Audio/tap.wav", .45f);
        _owner?.UiStartGame();
        _waypointsOpen = false;
        _respawnConfirm = false;
    }

    public void OnResume()
    {
        _owner?.UiResumeGame();
        _waypointsOpen = false;
        _respawnConfirm = false;
    }

    public void OnSettings()
    {
        _waypointsOpen = false;
        _respawnConfirm = false;
        _owner?.UiToggleSettings();
    }

    public void OnWaypoints()
    {
        if (_owner is not { UiGameMenuVisible: true } editor) return;
        if (editor.UiSettingsOpen) editor.UiToggleSettings();
        _respawnConfirm = false;
        _waypointsOpen = !_waypointsOpen;
        UpdateNavigation();
    }

    public void OnBackWaypoints() => _waypointsOpen = false;

    public void OnSetWaypoint(string id)
    {
        if (!_waypointsOpen || _waypointLoadError ||
            _waypointWorldId.Length == 0 ||
            _owner is not { } editor ||
            !WaypointSlot(id, "waypoint-set-", out int slot))
            return;
        if (_waypoints.Mark(slot, editor.Transform.Position))
            SaveWaypoints();
        UpdateNavigation();
    }

    public void OnTrackWaypoint(string id)
    {
        if (!_waypointsOpen || _waypointLoadError ||
            !WaypointSlot(id, "waypoint-track-", out int slot))
            return;
        if (_waypoints.Track(_waypoints.Tracked == slot ? -1 : slot))
            SaveWaypoints();
        UpdateNavigation();
    }

    public void OnClearWaypoint(string id)
    {
        if (!_waypointsOpen || _waypointLoadError ||
            !WaypointSlot(id, "waypoint-clear-", out int slot))
            return;
        if (_waypoints.Clear(slot)) SaveWaypoints();
        UpdateNavigation();
    }

    public void OnRespawnPrompt()
    {
        if (_owner is not { UiPaused: true }) return;
        _respawnConfirm = true;
        _waypointsOpen = false;
    }

    public void OnCancelRespawn() => _respawnConfirm = false;

    public void OnConfirmRespawn()
    {
        if (!_respawnConfirm || _owner is not { UiPaused: true } editor)
            return;
        _respawnConfirm = false;
        editor.UiReturnToSpawn();
        editor.UiResumeGame();
    }

    public void OnQuit() => _owner?.UiQuitGame();
    public void OnViewDistance() { _viewChoice = (_viewChoice + 1) % WorldDisplaySettings.Distances.Length; ApplySettings(); SaveSettings(); }
    public void OnTerrainLod() { _lodEnabled = !_lodEnabled; ApplySettings(); SaveSettings(); }
    public void OnLodQuality() { _lodQualityChoice = (_lodQualityChoice + 1) % LodScales.Length; ApplySettings(); SaveSettings(); }
    public void OnWorldHeight() { _heightChoice = (_heightChoice + 1) % 3; ApplySettings(); SaveSettings(); }
    public void OnShadows() { _shadows = !_shadows; ApplySettings(); SaveSettings(); }
    public void OnThreads() { _workerSelection = WorldDisplaySettings.NextWorkerSelection(_workerSelection, MaxWorkers()); ApplySettings(); SaveSettings(); }

    private void Text(string id, string value)
    {
        var element = Document.Find(id);
        if (element is { } field && field.Text != value)
            field.SetText(value);
    }

    private void Visible(string id, bool show)
    {
        var element = Document.Find(id);
        if (element is { } field && field.Visible != show)
            field.Visible = show;
    }

    private static bool ParseSlot(string id, out int slot)
    {
        slot = -1;
        // Visual rows p0..p2 show backpack slots 9..35; p3 is the
        // nine-slot hotbar (inventory slots 0..8), below the divider.
        // c0..c2 are chest slots 36..62; h_0_* is the gameplay HUD.
        // Map only the presentation, never move or migrate saved items.
        if (id.StartsWith("h_", StringComparison.Ordinal))
            return false;
        if (id.Length != 4 || id[2] != '_' ||
            id[1] is < '0' or > '3' || id[3] is < '0' or > '8')
            return false;
        int row = id[1] - '0', col = id[3] - '0';
        if (id[0] == 'p')
            slot = row == 3 ? col : 9 + row*9 + col;
        else if (id[0] == 'c' && row < 3)
            slot = 36+row*9+col;
        return slot >= 0;
    }

    private static string PlayerId(int slot) =>
        slot < BlockInventory.HotbarSlots
            ? $"p3_{slot}"
            : $"p{(slot-BlockInventory.HotbarSlots)/9}_" +
              $"{(slot-BlockInventory.HotbarSlots)%9}";

    private static string ChestId(int slot) =>
        $"c{slot/9}_{slot%9}";

    private void SetItem(string id, ItemStack stack, int index)
    {
        int type = stack.Type is >= 1 and <= BlockInventory.MaxType ?
            stack.Type : 0;
        if (!_slotDrawn[index])
        {
            for (int i = 1; i < Images.Length; i++)
                Visible(id+"_type"+i, i == type);
            _slotDrawn[index] = true;
        }
        else if (type != _lastType[index])
        {
            if (_lastType[index] != 0)
                Visible(id+"_type"+_lastType[index], false);
            if (type != 0)
                Visible(id+"_type"+type, true);
        }
        if (_lastQuantity[index] != stack.Quantity ||
            _lastType[index] != type)
            Text(id+"_count", type != 0 && stack.Quantity > 1 ?
                stack.Quantity.ToString() : "");
        _lastType[index] = type;
        _lastQuantity[index] = stack.Quantity;
    }

    private void UpdateInventory()
    {
        if (_owner is not { } editor) return;
        BlockInventory items = editor.UiInventory;
        for (int i = 0; i < BlockInventory.SlotCount; i++)
            SetItem(PlayerId(i), items.Slot(i), i);
        for (int i = 0; i < BlockInventory.HotbarSlots; i++)
            SetItem("h_0_"+i, items.Slot(i), 36+i);
        int reserveType = 0, reserveCount = 0;
        for (int type = 1; type <= BlockInventory.MaxType; type++)
        {
            int count = items.Reserve(type);
            if (count <= 0) continue;
            reserveCount += count;
            if (reserveType == 0) reserveType = type;
        }
        Text("reserve-info", reserveCount == 0 ?
            "All resources are in slots" :
            $"Legacy reserve: {reserveCount} items");
        Visible("reserve-button", reserveType != 0);
    }

    private void UpdateChest()
    {
        if (_owner is not { } editor) return;
        ChestContents chest = editor.UiChest;
        for (int i = 0; i < ChestContents.SlotCount; i++)
            SetItem(ChestId(i), editor.ChestOpen ? chest.Slot(i) : default,
                45+i);
        Text("chest-status", editor.ChestOpen ?
            $"Chest at {editor.ChestX}, {editor.ChestY}, {editor.ChestZ}" +
                (editor.ChestStatus.Length > 0 ? " - " + editor.ChestStatus : "") :
            editor.ChestStatus.Length > 0 ? editor.ChestStatus :
            "Chest closed.");
        Visible("chest-stack", editor.ChestOpen);
        Visible("chest-actions", editor.ChestOpen);
    }

    private void UpdateToolRows()
    {
        if (_owner is not { } editor) return;
        PlayerTools tools = editor.UiTools;
        for (int i = 1; i <= 6; i++)
        {
            var type = (MiningTool)i;
            int life = tools.Durability(type);
            int max = PlayerTools.MaxDurability(type);
            Text("tool_info_"+i,
                PlayerTools.Name(type) + "  " + life + "/" + max);
            Text("tool_equip_"+i,
                tools.Equipped == type ? "Using" : "Equip");
            Text("tool_make_"+i,
                life >= max ? "Full" : life > 0 ? "Repair" : "Craft");
        }
    }

    private void UpdateSelection()
    {
        for (int i = 0; i < BlockInventory.HotbarSlots; i++)
        {
            bool active = _owner != null && _owner.SelectedHotbar == i;
            Visible("h_0_"+i+"_selected", active);
            Visible("p3_"+i+"_selected", _source == i || active);
        }
        for (int i = 9; i < BlockInventory.SlotCount; i++)
            Visible(PlayerId(i)+"_selected", _source == i);
        for (int i = 0; i < ChestContents.SlotCount; i++)
            Visible(ChestId(i)+"_selected", _source == i+36);
        Text("selection-note", _source < 0 ?
            "Select a stack, then click a destination" :
            $"Selected slot {_source+1}. Click where it should go.");
        Text("half_button", _moveHalf ? "Move half: ON" : "Move half: OFF");
    }

    private void RefreshPage()
    {
        if (_owner is not { } editor) return;
        bool storage = editor.ChestOpen;
        if (_page == "creative" && (!editor.CreativeMode || storage))
            _page = storage ? "chest" : "craft";
        Text("inventory-title", storage ? "Chest inventory" :
            _page == "creative" ? "Creative inventory" : "Inventory");
        Visible("tab_craft", !storage);
        Visible("tab_forge", !storage);
        Visible("tab_creative", !storage && editor.CreativeMode);
        UpdateRecipeScroll();
        Visible("page_craft", !storage && _page == "craft");
        Visible("page_forge", !storage && _page == "forge");
        Visible("page_creative", !storage && editor.CreativeMode && _page == "creative");
        Visible("page_chest", storage && _page == "chest");
        Text("creative-amount-button",
            _creativeQuantity == BlockInventory.StackLimit ?
                "Add 64 per click" : "Add 1 per click");
        Text("creative-status", editor.CreativeStatus.Length > 0 ?
            editor.CreativeStatus :
            "Choose any block below. Click to add it to your inventory.");
        UpdateCreativeCounts(editor.UiInventory);
        Text("forge-status", editor.UiNearForge ?
            "Forge within reach - smelting available" :
            "Place a forge and stand within 3.5m to smelt.");
        Text("smelt-info",
            $"Iron ore: {editor.AvailableCount(8)}  |  Coal: {editor.AvailableCount(7)}  |  Ingots: {editor.AvailableCount(10)}");
        Text("hud-status", editor.CreativeMode ? "CREATIVE BUILD" : "SURVIVAL");
        int slot = Math.Clamp(editor.SelectedHotbar, 0,
            BlockInventory.HotbarSlots-1);
        var held = editor.UiInventory.Slot(slot);
        string heldName = held.Type is >= 1 and <= BlockInventory.MaxType ?
            Images[held.Type].Replace('_',' ') : "Empty";
        Text("hud-held",
            $"{heldName}   /   {(BlockShape)editor.PlaceShape}   /   " +
            (editor.PickedAbsoluteFacing ?
                $"Facing {editor.FacingOffset*90} deg (copied)" :
                $"Rotate +{editor.FacingOffset*90} deg") +
            (editor.PickedUpper ? "   /   Upper" : ""));
        Text("hud-pick-feedback", editor.PickFeedback);
        var tool = editor.UiTools.Equipped;
        Text("hud-tool", editor.TestWeapon != 0 ? editor.TestWeaponName :
            tool == MiningTool.Hand ? "Hands" :
            $"{PlayerTools.Name(tool)}   {editor.UiTools.Durability(tool)}/" +
                PlayerTools.MaxDurability(tool));
        Text("menu-hint", _page == "chest" ?
            "Pick a source and destination. Empty a chest before mining." :
            _page == "creative" ?
            "Choose a block to add. Toggle between 1 and 64 per click." :
            "Click source then destination. Toggle Move half to split.");
        Text("inventory-total",
            $"36 slots / {editor.UiInventory.Count(10)} iron ingots");
    }

    public void Refresh()
    {
        if (_owner is not { } editor || !Document.IsValid ||
            Document.Find("overlay") is null)
            return;

        LoadSettings();
        EnsureWaypointsLoaded();
        if (!editor.UiGameMenuVisible)
        {
            _waypointsOpen = false;
            _partyOpen = false;
            _inviteOpen = false;
            _respawnConfirm = false;
        }
        if (!editor.UiGameMenuVisible)
        {
            _fpsSeconds += MathF.Max(0f, Time.UnscaledDeltaTime);
            _fpsFrames++;
            if (_fpsSeconds >= .5f)
            {
                Text("fps-value", $"FPS {MathF.Round(_fpsFrames / _fpsSeconds)}");
                _fpsSeconds = 0f;
                _fpsFrames = 0;
            }
        }
        Visible("fps-counter", !editor.UiGameMenuVisible && !editor.IsInventoryVisible);
        Visible("navigation-hud", !editor.UiGameMenuVisible && !editor.IsInventoryVisible);
        Visible("compass-ribbon", !editor.UiGameMenuVisible && !editor.IsInventoryVisible);
        Visible("return-loading", editor.ReturnToSpawnLocked);
        if (editor.ReturnToSpawnLocked)
            Text("return-loading-status",
                "Generating destination terrain and colliders...");
        Visible("game-menu", editor.UiGameMenuVisible);
        Visible("inventory-panel", editor.IsInventoryVisible);
        Visible("game-menu-actions", editor.UiGameMenuVisible &&
            !editor.UiSettingsOpen && !_waypointsOpen && !_respawnConfirm && !_partyOpen);
        Visible("world-settings", editor.UiGameMenuVisible &&
            editor.UiSettingsOpen && !_waypointsOpen && !_respawnConfirm);
        Visible("waypoint-panel", editor.UiGameMenuVisible && _waypointsOpen);
        Visible("respawn-confirm", editor.UiGameMenuVisible && _respawnConfirm);
        Visible("respawn-menu-button", editor.UiPaused);
        Visible("play-game", editor.UiMainMenu);
        Visible("resume-game", editor.UiPaused);
        bool lobby = editor.UiMainMenu || _partyOpen;
        Text("game-menu-subtitle", lobby ? "Expedition lobby" : "Expedition paused");
        Visible("party-button", editor.UiPaused);
        Visible("party-back", _partyOpen);
        Visible("party-card", editor.UiGameMenuVisible && lobby &&
            !editor.UiSettingsOpen && !_waypointsOpen && !_respawnConfirm);
        if (editor.UiGameMenuVisible && lobby)
        {
            _partyWorld ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
            Text("party-roster", _partyWorld?.PartyRoster is { Length: > 0 } roster ? roster : Network.InSession(Scene) ? "Joining…" : "Solo expedition");
            bool canInvite = Scene.IsServer && Network.InSession(Scene) && Steam.IsReady;
            Visible("invite-button", canInvite);
            Visible("invite-fields", canInvite && _inviteOpen);
            Text("invite-button", _inviteOpen ? "Close friends list" : "Invite Steam friends");
            if (_inviteOpen && canInvite) _friends.Refresh(Document, true, Time.DeltaTime);

        }

        bool open = editor.IsInventoryVisible;
        if (!_initialized || open != _menuBefore ||
            _gameMenuBefore != editor.UiGameMenuVisible)
        {
            Visible("overlay", open || editor.UiGameMenuVisible);
            Visible("hud", !open && !editor.UiGameMenuVisible);
            Visible("crosshair", !open && !editor.UiGameMenuVisible);
            if (!open)
            {
                _source = -1;
                if (_page == "chest") _page = "craft";
            }
            _menuBefore = open;
            _gameMenuBefore = editor.UiGameMenuVisible;
        }

        if (!_initialized || _readyBefore != editor.ProfileReady)
        {
            Visible("notice-panel", !editor.ProfileReady);
            Text("notice", editor.ProfileError ?
                "Player save error. Check the server logs." :
                "Loading your saved inventory...");
            _readyBefore = editor.ProfileReady;
        }
        if (!editor.ProfileReady)
        {
            _initialized = true;
            return;
        }

        if (!_initialized || _inventorySnapshot != editor.InventoryState)
        {
            _inventorySnapshot = editor.InventoryState;
            UpdateInventory();
            RefreshPage();
        }
        if (open && (editor.ChestOpen != (_page == "chest")))
        {
            _page = editor.ChestOpen ? "chest" : "craft";
            if (editor.ChestOpen) Audio.PlayOneShot2D("Audio/latch.wav", .6f);
            _source = -1;
            RefreshPage();
            UpdateSelection();
        }
        if (!_initialized || _chestSnapshot != editor.ChestState ||
            _chestBefore != editor.ChestOpen ||
            _chestMessageBefore != editor.ChestStatus)
        {
            _chestSnapshot = editor.ChestState;
            _chestBefore = editor.ChestOpen;
            _chestMessageBefore = editor.ChestStatus;
            UpdateChest();
        }
        if (!_initialized || _toolSnapshot != editor.ToolState)
        {
            _toolSnapshot = editor.ToolState;
            UpdateToolRows();
            RefreshPage();
        }
        if (!_initialized || _selectedBefore != editor.SelectedHotbar)
        {
            _selectedBefore = editor.SelectedHotbar;
            UpdateSelection();
            RefreshPage();
        }

        // Compass must reflect the *current* local camera frame. Updating
        // only with slow inventory snapshots made left/right bearings lag.
        if (!editor.UiGameMenuVisible && !editor.IsInventoryVisible)
            UpdateCompass();
        // Tallow's .tui has no native scroll container. The slider above is
        // a bounded scroll window, and the inventory wheel advances rows.
        if (editor.IsInventoryVisible && _page == "craft" &&
            Input.MouseWheel != 0f)
            ScrollRecipes(_recipeScroll +
                (Input.MouseWheel < 0f ? 1 : -1));
        // Item-image visibility is refreshed only after snapshots change.
        _refreshClock -= Time.DeltaTime;
        if (_refreshClock <= 0)
        {
            _refreshClock = 0.2f;
            RefreshPage();
            UpdateNavigation();
            _preview ??= editor.GameObject.GetComponent<BuildPreview>();
            string targetHint = _preview?.TargetHint() ?? "";
            Text("target-hint", targetHint);
            Visible("target-hint", targetHint.Length > 0);
        }
        _initialized = true;
    }

    public void OnSlot(string id)
    {
        if (_owner is not { } editor || !editor.IsInventoryVisible ||
            !ParseSlot(id, out int selected))
            return;
        if (selected >= 36 && !editor.ChestOpen) return;

        var value = selected < 36 ? editor.UiInventory.Slot(selected) :
            editor.UiChest.Slot(selected-36);

        if (_source < 0)
        {
            if (value.IsEmpty) return;
            _source = selected;
            UpdateSelection();
            return;
        }

        if (_source != selected)
            editor.UiMoveStack(_source, selected, _moveHalf);
        _source = -1;
        UpdateSelection();
    }

    /// <summary>Deposit the selected player stack into the first
    /// compatible chest slot. The server still validates the actual chest,
    /// inventory and transfer quantity on every request.</summary>
    public void OnDeposit()
    {
        if (_owner is not { } editor || !editor.IsInventoryVisible)
            return;
        if (!editor.ChestOpen)
        {
            Text("selection-note", "Open a nearby chest first.");
            return;
        }
        if (_source is < 0 or >= BlockInventory.SlotCount)
        {
            Text("selection-note", "Select an item in your inventory to store.");
            return;
        }
        ItemStack offered = editor.UiInventory.Slot(_source);
        if (offered.IsEmpty) return;
        ChestContents chest = editor.UiChest;
        int destination = -1;
        // Prefer adding to an existing matching stack before filling an
        // empty chest slot. The server determines the actual amount moved.
        for (int i = 0; i < ChestContents.SlotCount; i++)
        {
            ItemStack stack = chest.Slot(i);
            if (stack.Type == offered.Type &&
                stack.Quantity < BlockInventory.StackLimit)
            {
                destination = i;
                break;
            }
        }
        if (destination < 0)
            for (int i = 0; i < ChestContents.SlotCount; i++)
                if (chest.Slot(i).IsEmpty)
                {
                    destination = i;
                    break;
                }
        if (destination < 0)
        {
            Text("selection-note", "Chest is full; choose another destination.");
            return;
        }
        editor.UiMoveStack(_source, BlockInventory.SlotCount + destination,
            _moveHalf);
        _source = -1;
        UpdateSelection();
        Text("selection-note", "Store requested - server is confirming...");
    }

    /// <summary>Withdraw the selected chest stack into an available
    /// player slot. Transfer validation and saved contents stay server-owned.</summary>
    public void OnWithdraw()
    {
        if (_owner is not { } editor || !editor.IsInventoryVisible)
            return;
        if (!editor.ChestOpen)
        {
            Text("selection-note", "Open a nearby chest first.");
            return;
        }
        if (_source is < BlockInventory.SlotCount or >= 63)
        {
            Text("selection-note", "Select an item in the chest to take.");
            return;
        }
        ItemStack stored = editor.UiChest.Slot(
            _source - BlockInventory.SlotCount);
        if (stored.IsEmpty) return;
        int quantity = _moveHalf ? (stored.Quantity + 1) / 2 : stored.Quantity;
        ItemStack wanted = new(stored.Type, quantity);
        int destination = -1;
        BlockInventory player = editor.UiInventory;
        for (int i = 0; i < BlockInventory.SlotCount; i++)
        {
            ItemStack occupied = player.Slot(i);
            if (occupied.Type == stored.Type &&
                player.CanInsertIntoSlot(i, wanted))
            {
                destination = i;
                break;
            }
        }
        if (destination < 0)
            for (int i = 0; i < BlockInventory.SlotCount; i++)
                if (player.Slot(i).IsEmpty &&
                    player.CanInsertIntoSlot(i, wanted))
                {
                    destination = i;
                    break;
                }
        if (destination < 0)
        {
            Text("selection-note", "Inventory full; free a slot to withdraw.");
            return;
        }
        editor.UiMoveStack(_source, destination, _moveHalf);
        _source = -1;
        UpdateSelection();
        Text("selection-note", "Withdrawal requested - server is confirming...");
    }

    public void OnHalf()
    {
        _moveHalf = !_moveHalf;
        UpdateSelection();
    }

    public void OnCancel()
    {
        _source = -1;
        UpdateSelection();
    }

    public void OnActive()
    {
        if (_owner is { } editor && _source is >= 0 and < 9)
            editor.UiSetHotbar(_source);
        _source = -1;
        UpdateSelection();
    }

    /// <summary>Purely visual catalog selection; the owning player's
    /// server-validated RPC performs any actual inventory mutation.</summary>
    public void OnCreativeBlock(string id)
    {
        if (_owner is not { } editor || !editor.IsInventoryVisible ||
            !editor.CreativeMode || editor.ChestOpen || _page != "creative" ||
            !id.StartsWith("creative_", StringComparison.Ordinal) ||
            !int.TryParse(id.AsSpan("creative_".Length), out int type) ||
            !BlockInventory.IsCreativeBlock(type))
            return;
        editor.UiGiveCreativeBlock(type, _creativeQuantity);
        Audio.PlayOneShot2D("Audio/tap.wav", .32f);
    }

    public void OnCreativeAmount()
    {
        if (_owner is not { IsInventoryVisible: true, CreativeMode: true } ||
            _page != "creative")
            return;
        _creativeQuantity = _creativeQuantity == BlockInventory.StackLimit ?
            1 : BlockInventory.StackLimit;
        RefreshPage();
    }

    private void UpdateCreativeCounts(BlockInventory inventory)
    {
        foreach (int type in CreativeTypes)
            Text("creative-count-" + type, inventory.Count(type) + " owned");
    }

    public void OnTab(string id)
    {
        if (_owner is not { } editor || editor.ChestOpen) return;
        if (id == "tab_creative")
        {
            if (!editor.CreativeMode) return;
            _page = "creative";
        }
        else
            _page = id == "tab_forge" ? "forge" : "craft";
        _source = -1;
        Audio.PlayOneShot2D("Audio/tap.wav", .32f);
        RefreshPage();
        UpdateChest();
        UpdateSelection();
    }

    private void UpdateRecipeScroll()
    {
        int maxStart = Math.Max(0, RecipeIds.Length - VisibleRecipes);
        _recipeScroll = Math.Clamp(_recipeScroll, 0, maxStart);
        if (!_recipeScrollInitialized)
        {
            _recipeScrollInitialized = true;
            // Hide first so initial UI layout never flashes all 8 recipes.
            foreach (int id in RecipeIds)
                Visible("recipe_" + id, false);
        }
        for (int i = 0; i < RecipeIds.Length; i++)
            Visible("recipe_" + RecipeIds[i],
                i >= _recipeScroll && i < _recipeScroll + VisibleRecipes);
        Text("recipe-scroll-status",
            $"Recipes {_recipeScroll + 1}–{Math.Min(RecipeIds.Length,
                _recipeScroll + VisibleRecipes)} of {RecipeIds.Length}");
        if (Document.Find("recipe-scrollbar") is { } slider &&
            MathF.Abs(slider.Value - _recipeScroll) > 0.01f)
            slider.Value = _recipeScroll;
        Visible("recipe-scroll-up", _recipeScroll > 0);
        Visible("recipe-scroll-down", _recipeScroll < maxStart);
    }

    private void ScrollRecipes(int offset)
    {
        int next = Math.Clamp(offset, 0,
            Math.Max(0, RecipeIds.Length - VisibleRecipes));
        if (next == _recipeScroll && _recipeScrollInitialized) return;
        _recipeScroll = next;
        UpdateRecipeScroll();
    }

    public void OnRecipeScroll(UIElement element) =>
        ScrollRecipes((int)MathF.Round(element.Value));
    public void OnRecipeScrollUp() => ScrollRecipes(_recipeScroll - 1);
    public void OnRecipeScrollDown() => ScrollRecipes(_recipeScroll + 1);

    public void OnRecipe(string id)
    {
        if (_owner is not { } editor || !id.StartsWith("recipe_") ||
            !int.TryParse(id.AsSpan(7), out int recipe))
            return;
        editor.UiCraftRecipe(recipe);
    }

    public void OnSmelt(string id)
    {
        if (_owner is not { } editor || !editor.UiNearForge)
            return;
        if (id == "smelt_1") editor.UiSmelt(1);
        else if (id == "smelt_5") editor.UiSmelt(5);
    }

    public void OnTool(string id)
    {
        if (_owner is { } editor && id.StartsWith("tool_make_") &&
            int.TryParse(id.AsSpan(10), out int tool))
            editor.UiCraftTool(tool);
    }

    public void OnEquip(string id)
    {
        if (_owner is { } editor && id.StartsWith("tool_equip_") &&
            int.TryParse(id.AsSpan(11), out int tool))
            editor.UiEquipTool(tool);
    }

    public void OnHands() => _owner?.UiEquipTool(0);

    public void OnUnpack()
    {
        if (_owner is not { } editor) return;
        var inventory = editor.UiInventory;
        for (int type = 1; type <= BlockInventory.MaxType; type++)
            if (inventory.Reserve(type) > 0)
            {
                editor.UiUnpack(type);
                return;
            }
    }

    public void OnClose() => _owner?.UiCloseMenu();
}
