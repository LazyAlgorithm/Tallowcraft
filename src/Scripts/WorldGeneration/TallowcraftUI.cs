using System;
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
        "coal", "iron_ore", "leaves", "iron_ingot", "chest"
    };

    private BlockEditor? _owner;
    private string _inventorySnapshot = "";
    private string _chestSnapshot = "";
    private string _toolSnapshot = "";
    private bool _readyBefore;
    private bool _menuBefore;
    private bool _chestBefore;
    private string _chestMessageBefore = "";
    // The Storage tab may be clicked before the server acknowledges
    // SetInventoryOpen. Defer the chest RPC until that acknowledgment.
    private bool _chestOpenRequested;
    private int _selectedBefore = -1;
    private int _source = -1;
    private bool _moveHalf;
    private string _page = "craft";
    private float _refreshClock;
    private bool _initialized;
    // Avoid hundreds of native Find/Visible calls whenever one stack count
    // changes. Each slot initializes once, then switches only its old/new art.
    private readonly bool[] _slotDrawn = new bool[72];
    private readonly int[] _lastType = new int[72];
    private readonly int[] _lastQuantity = new int[72];

    public void Bind(BlockEditor owner) => _owner = owner;

    private UIDocument Document => GetComponent<UIDocument>();

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
            _chestOpenRequested ?
                "Opening nearby chest..." :
                "No chest open. Stand within 4.5m and click Find nearby chest.");
        Visible("open-chest", !editor.ChestOpen);
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
        Visible("page_craft", _page == "craft");
        Visible("page_forge", _page == "forge");
        Visible("page_chest", _page == "chest");
        if (_owner is not { } editor) return;
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
            $"{heldName}   /   {(BlockShape)editor.PlaceShape}   /   {editor.FacingOffset*90} deg");
        var tool = editor.UiTools.Equipped;
        Text("hud-tool", tool == MiningTool.Hand ? "Hands" :
            $"{PlayerTools.Name(tool)}   {editor.UiTools.Durability(tool)}/" +
                PlayerTools.MaxDurability(tool));
        Text("menu-hint", _page == "chest" ?
            "Pick a source and destination. Empty a chest before mining." :
            "Click source then destination. Toggle Move half to split.");
        Text("inventory-total",
            $"36 slots / {editor.UiInventory.Count(10)} iron ingots");
    }

    public void Refresh()
    {
        if (_owner is not { } editor || !Document.IsValid ||
            Document.Find("overlay") is null)
            return;

        bool open = editor.IsInventoryVisible;
        if (!_initialized || open != _menuBefore)
        {
            Visible("overlay", open);
            Visible("hud", !open);
            Visible("crosshair", !open);
            if (!open)
            {
                _source = -1;
                _chestOpenRequested = false;
            }
            _menuBefore = open;
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
        // The menu opens locally before InventoryOpen is replicated.
        // Waiting for the server's acknowledgment avoids dropping the
        // only OpenNearbyChest RPC on high-latency connections.
        if (open && _page == "chest" && _chestOpenRequested &&
            editor.InventoryOpen && editor.ProfileReady)
        {
            _chestOpenRequested = false;
            // Always reissue on entering Storage. The previous CloseChest
            // RPC may still be in flight, while the old replicated
            // ChestOpen value momentarily remains true on this client.
            editor.UiOpenChest();
        }
        // E opens the chest and inventory together on the server. When
        // that open state replicates, switch straight to the Storage page.
        // This also works when the player opens a chest from the Storage
        // tab, with no local/persisted inventory slot changes.
        if (open && editor.ChestOpen && !_chestBefore)
        {
            _page = "chest";
            _source = -1;
            _chestOpenRequested = false;
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

        // Text labels and forge proximity are inexpensive; item-image
        // visibility is refreshed only after network snapshots change.
        _refreshClock -= Time.DeltaTime;
        if (_refreshClock <= 0)
        {
            _refreshClock = 0.2f;
            RefreshPage();
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

    public void OnTab(string id)
    {
        if (_owner is not { } editor) return;
        string next = id switch
        {
            "tab_forge" => "forge",
            "tab_chest" => "chest",
            _ => "craft"
        };
        if (_page == "chest" && next != "chest")
            editor.UiCloseChest();
        _page = next;
        _source = -1;
        _chestOpenRequested = _page == "chest";
        RefreshPage();
        UpdateChest();
        UpdateSelection();
    }

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

    public void OnOpenChest()
    {
        if (_owner is not { } editor || !editor.IsInventoryVisible)
            return;
        _chestOpenRequested = true;
        UpdateChest();
    }

    public void OnClose() => _owner?.UiCloseMenu();
}
