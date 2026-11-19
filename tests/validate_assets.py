"""Validate Tallowcraft's committed rigged GLB and player asset wiring.

Pure Python 3 standard library; does not require Tallow or a graphics card.
It tests asset structure, not actual rendering, animation quality or networking.
"""
from __future__ import annotations

import json
from pathlib import Path
import struct
import zlib

ROOT = Path(__file__).resolve().parents[1]
MODEL = ROOT / "src/Models/QuaterniusBaseCharacter.glb"
LIBRARY = ROOT / "src/Animations/QuaterniusUniversalLibrary.glb"
PREFAB = ROOT / "src/Prefabs/Player.tprefab"
ANIMATIONS = ROOT / "src/Animations"
INPUT = ROOT / "src/Input.tinput"

COMPONENTS = {5120: ("b", 1), 5121: ("B", 1), 5122: ("h", 2),
              5123: ("H", 2), 5125: ("I", 4), 5126: ("f", 4)}
ARITY = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


def check(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def glb(path: Path = MODEL) -> tuple[dict, bytes]:
    raw = path.read_bytes()
    check(len(raw) >= 28, "GLB header is incomplete")
    magic, version, length = struct.unpack_from("<III", raw, 0)
    check(magic == 0x46546C67 and version == 2 and length == len(raw),
          "Invalid GLB 2.0 header or byte length")
    json_size, json_magic = struct.unpack_from("<II", raw, 12)
    check(json_magic == 0x4E4F534A, "Missing JSON chunk")
    offset = 20 + json_size
    check(offset + 8 <= len(raw), "No BIN chunk header")
    bin_size, bin_magic = struct.unpack_from("<II", raw, offset)
    check(bin_magic == 0x004E4942, "Missing BIN chunk")
    check(offset + 8 + bin_size == len(raw), "Incorrect GLB chunk sizes")
    document = json.loads(raw[20:offset].decode("utf-8"))
    binary = raw[offset + 8:]
    check(binary[:document["buffers"][0]["byteLength"]], "Empty mesh buffer")
    return document, binary


def accessor(doc: dict, buf: bytes, index: int) -> list[tuple]:
    obj = doc["accessors"][index]
    fmt, size = COMPONENTS[obj["componentType"]]
    arity = ARITY[obj["type"]]
    view = doc["bufferViews"][obj["bufferView"]]
    stride = view.get("byteStride", arity * size)
    pos = view.get("byteOffset", 0) + obj.get("byteOffset", 0)
    values = []
    for _ in range(obj["count"]):
        check(pos + size * arity <= len(buf), "Accessor exceeds binary buffer")
        values.append(struct.unpack_from("<" + fmt * arity, buf, pos))
        pos += stride
    return values


def verify() -> None:
    doc, buf = glb()
    library, _ = glb(LIBRARY)
    check(doc.get("skins") and library.get("skins"), "Quaternius humanoid skeleton absent")
    model_bones = {doc["nodes"][joint].get("name")
                   for skin in doc["skins"] for joint in skin["joints"]}
    library_bones = {library["nodes"][joint].get("name")
                     for skin in library["skins"] for joint in skin["joints"]}
    joint_names = sorted(model_bones - {None})
    check(len(joint_names) >= 30, "Expected a real multi-joint skinned humanoid")
    check(len((model_bones & library_bones) - {None}) >= 24,
          "Quaternius model and library have incompatible rigs")
    vertices = 0
    triangles = 0
    skinned = 0
    for mesh in doc["meshes"]:
        for primitive in mesh["primitives"]:
            attributes = primitive["attributes"]
            count = doc["accessors"][attributes["POSITION"]]["count"]
            vertices += count
            if {"JOINTS_0", "WEIGHTS_0"} <= attributes.keys():
                skinned += 1
                joints = accessor(doc, buf, attributes["JOINTS_0"])
                weights = accessor(doc, buf, attributes["WEIGHTS_0"])
                check(len(joints) == len(weights) == count,
                      "Quaternius skin attributes have mismatched lengths")
            triangles += doc["accessors"][primitive["indices"]]["count"] // 3
    check(skinned > 0, "The imported Quaternius mesh must be skinned")

    metadata = json.loads((ROOT / "src/Animations/QuaterniusImport.json").read_text())
    selected = metadata["selected"]
    available = {a["name"] for a in library["animations"]}
    check(set(selected) == {"Idle", "Walk", "Run", "Jump"},
          "Missing movement clip role")
    check(all(clip in available for clip in selected.values()),
          "Referenced movement clip is missing from Quaternius library")
    clip_source = (ROOT / "src/Scripts/QuaterniusClips.cs").read_text()
    for name, clip in selected.items():
        check(f'public const string {name} = {json.dumps(clip)};' in clip_source,
              "Generated C# clip mapping does not match imported clips")

    prefab = PREFAB.read_text()
    check('field model "Models/QuaterniusBaseCharacter.glb"' in prefab,
          "Player prefab does not reference the CC0 Quaternius character")
    check('field clipLibrary "Animations/QuaterniusUniversalLibrary.glb"' in prefab,
          "Skinned character is missing the CC0 Universal Animation Library")
    check("component Tallow.SkinnedMeshRenderer enabled 1" in prefab,
          "Player is missing skinned model renderer")
    check("component Tallow.Animator enabled 1" not in prefab,
          "Old procedural Animator would conflict with Quaternius imported clips")
    check("component Tallow.CharacterController enabled 1" in prefab,
          "Player must keep invisible Jolt collision capsule")
    check("builtin:capsule" not in prefab,
          "Visible capsule must not be drawn in the game")
    check('field scriptType "Tallowcraft.CharacterAnimation"' in prefab,
          "Character playback script is missing")
    animation = (ROOT / "src/Scripts/CharacterAnimation.cs").read_text()
    check("GetComponentInChildren<SkinnedMeshRenderer>()" in animation
          and "_rig.Clip = clip" in animation,
          "Character script does not play native Quaternius clips")
    character = (ROOT / "src/Scripts/FirstPersonCharacter.cs").read_text()
    check("renderer.ShadowOnly = shadowOnly" in character
          and "renderer.CastShadows = true;" in character,
          "Local first-person body should be hidden while keeping its shadow")

    atlas_path = ROOT / "src/Textures/block_atlas.png"
    atlas_bytes = atlas_path.read_bytes()
    check(atlas_bytes[:8] == bytes.fromhex("89504e470d0a1a0a"),
          "Atlas must be a PNG file")
    check(struct.unpack_from(">II", atlas_bytes, 16) == (144, 144),
          "Expected a 4x4 atlas of 32x32 tiles with 2px gutters (144x144)")
    check(atlas_bytes[24:26] == bytes([8, 2]),
          "Expected an 8-bit lossless RGB pixel-art PNG")
    atlas_source = (ROOT / "src/Scripts/WorldGeneration/BlockTextureAtlas.cs").read_text()
    check("public const int TileSize = 32;" in atlas_source
          and "public const int Gutter = 2;" in atlas_source
          and "public const int Columns = 4;" in atlas_source
          and "public const int Rows = 4;" in atlas_source
          and "0.5f + u * (TileSize - 1)" in atlas_source,
          "Atlas UV grid does not match the PNG")
    atlas_settings = (ROOT / "src/Textures/block_atlas.png.import").read_text()
    for key, expected in {
        "filter": "nearest", "wrap": "clamp", "mipmaps": "0",
        "compression": "0", "fullResolution": "1"
    }.items():
        check(f"{key} = {expected}" in atlas_settings,
              f"Atlas needs {key} = {expected} in its .png.import")
    project_settings = (ROOT / "project.tallow").read_text()
    check("Textures/block_atlas.png" in project_settings,
          "Project must exclude the atlas from GPU compression in packaged builds")
    world_source = (ROOT / "src/Scripts/WorldGeneration/BlockWorld.cs").read_text()
    check("renderer.Texture = BlockTextureAtlas.Path;" in world_source,
          "Chunk renderer is not using the committed texture atlas")

    world_source = (ROOT / "src/Scripts/WorldGeneration/BlockWorld.cs").read_text()
    preview_source = (ROOT / "src/Scripts/WorldGeneration/BuildPreview.cs").read_text()
    check("TryQueryFromRay(" in world_source
          and "TryQueryFromRay(" in preview_source,
          "Preview and gameplay must share the same targeting query")
    check("DrawShape(" in preview_source
          and "state.ContainsMicro(" in preview_source
          and "DebugDraw.Box(" in preview_source,
          "Partial-block placement preview is missing")
    check('field scriptType "WorldGeneration.BuildPreview"' in prefab,
          "Player prefab does not attach BuildPreview")

    actions = INPUT.read_text()
    check("action Fire button\nbind mouse 1" in actions,
          "Right mouse must remove blocks")
    check("action Use button\nbind mouse 0" in actions,
          "Left mouse must place blocks")
    for i in range(1, 7):
        check("action Custom" + str(i) + " button" in actions
              and "bind key Alpha" + str(i) in actions,
              "Hotbar number key binding is missing: " + str(i))
    check("bind wheel up" in actions and "bind wheel down" in actions,
          "Hotbar mouse wheel actions are missing")
    check("action Reload button\nbind key Tab" in actions,
          "Inventory gamepad action should retain the Tab binding")
    check("action Inventory button\nbind key Tab\nbind pad West" in actions,
          "Menu must accept Tab/West while Player actions are disabled")
    menu_script = (ROOT / "src/Scripts/WorldGeneration/BlockEditor.cs").read_text()
    check('InputActions.SetMapEnabled("Player", !open)' in menu_script
          and 'InputActions.Pressed("Menu/Inventory")' in menu_script
          and "private void SetInventoryOpen(bool open)" in menu_script,
          "Menu focus must disable look input and use the server-authorized state RPC")
    check('[Sync] public bool ProfileReady = false' in menu_script
          and 'private void BindPlayerProfile(string token)' in menu_script
          and 'PlayerProfileStore.Load(' in menu_script
          and 'PlayerProfileStore.Save(' in menu_script
          and 'if (!ProfileReady)' in menu_script
          and 'GameObject.OwnerClientId <= 0' in menu_script,
          "Player items must wait for an owner-authorized server profile load")
    world_source = (ROOT / "src/Scripts/WorldGeneration/BlockWorld.cs").read_text()
    check("public string WorldIdentity" in world_source
          and "public string PlayerProfilesFolder" in world_source,
          "Player profiles need the server's persistent world identity")
    forge_src = (ROOT / "src/Scripts/WorldGeneration/ForgeCrafting.cs").read_text()
    editor_src = (ROOT / "src/Scripts/WorldGeneration/BlockEditor.cs").read_text()
    inventory_src = (ROOT / "src/Scripts/WorldGeneration/BlockInventory.cs").read_text()
    tools_src = (ROOT / "src/Scripts/WorldGeneration/PlayerTools.cs").read_text()
    check("public const int MaxType = 11" in inventory_src
          and 'encoded.StartsWith("1|"' in inventory_src
          and 'encoded.StartsWith("2|"' in inventory_src
          and 'encoded.StartsWith("3|"' in inventory_src
          and 'encoded.StartsWith("4|"' in inventory_src
          and 'sections[0] != "1" && sections[0] != "2"' in tools_src,
          "Old saved inventories and tools must migrate without resetting items")
    check("private void SmeltIron(int count)" in editor_src
          and "private bool ServerNearForge()" in editor_src
          and "ForgeCrafting.TrySmelt(_inventory, count)" in editor_src
          and "!ServerNearForge()" in editor_src
          and "recipeId is >= 1 and <= 3" in editor_src
          and "BlockInventory.IronIngot" in forge_src,
          "Iron smelting must be server-validated and locked to a real forge")
    check("if (_tools.TryEquipSlot(kind))" in editor_src
          and "MiningTool.IronPickaxe" in tools_src,
          "Quick equip should choose crafted iron tools when available")
    # Crack atlas is cut out directly by the standard renderer. Its ten
    # 16px stages are selected by UVs so the custom Shader Graph is optional.
    damage_png = (ROOT / "src/Textures/block_cracks.png").read_bytes()
    check(damage_png[:8] == bytes.fromhex("89504e470d0a1a0a") and
          struct.unpack_from(">II", damage_png, 16) == (160, 16) and
          damage_png[24:26] == bytes([8, 6]),
          "Fracture atlas must contain ten 16x16 RGBA stages")
    damage_import = (ROOT / "src/Textures/block_cracks.png.import").read_text()
    check(all(s in damage_import for s in
              ("filter = nearest", "wrap = clamp", "mipmaps = 0",
               "compression = 0", "fullResolution = 1")),
          "Mining crack atlas must remain sharp and lossless")

    shader_graph = (ROOT / "src/Shaders/block_break_damage.tsgraph").read_text()
    shader = (ROOT / "src/Shaders/block_break_damage.tshader").read_text()
    crack_material = (ROOT / "src/Materials/block_break_damage.tmat").read_text()
    check("node 1 shader.output" in shader_graph
          and 'node 2 shader.prop.float' in shader_graph
          and "node 3 shader.custom.float" in shader_graph
          and "link 2 0 3 1" in shader_graph
          and "link 3 0 1 1" in shader_graph
          and "property float damage" in shader
          and "texture2D(mainTexture" in shader
          and 'shader "Shaders/block_break_damage.tshader"' not in crack_material
          and 'texture "Textures/block_cracks.png"' in crack_material
          and "doubleSided 1" in crack_material
          and "blend 1" in crack_material and "unlit 1" in crack_material,
          "Breaking overlay must work with the standard cutout material")
    damage_mesh = (ROOT / "src/Scripts/WorldGeneration/BlockDamageMesh.cs").read_text()
    preview = (ROOT / "src/Scripts/WorldGeneration/BuildPreview.cs").read_text()
    editor_script = (ROOT / "src/Scripts/WorldGeneration/BlockEditor.cs").read_text()
    check("block.ContainsMicro(x,y,z)" in damage_mesh
          and "new BlockDamageMesh" in damage_mesh
          and "_damageRenderer.SetMesh(" in preview
          and 'BlockDamageMesh.Create(' in preview
          and 'CrackUV(stage,' in damage_mesh
          and "BackFaceCulling = false" in preview
          and 'public const float Offset = .018f' in damage_mesh
          and "overlay.Destroy()" in preview
          and "_editor.MiningActive" in preview
          and "[Sync] public int MiningX" in editor_script
          and "[Sync] public bool MiningActive" in editor_script,
          "Block breaking cracks must respect target/shape and clean up locally")
    icons = (ROOT / "src/Textures/inventory_icons.png").read_bytes()
    check(icons[:8] == bytes.fromhex("89504e470d0a1a0a") and
          struct.unpack_from(">II", icons, 16) == (160, 64) and
          icons[24] == 8 and icons[25] == 3,
          "Inventory sprite atlas must provide ten crisp 32px icons")
    icons_import = (ROOT / "src/Textures/inventory_icons.png.import").read_text()
    check(all(value in icons_import for value in
              ("filter = nearest", "wrap = clamp", "mipmaps = 0",
               "compression = 0", "fullResolution = 1")),
          "Inventory artwork must stay uncompressed and pixel-sharp")
    check("public const int SlotCount = 36" in inventory_src
          and "public const int StackLimit = 64" in inventory_src
          and 'public bool TrySplit(int from, int to)' in inventory_src
          and 'private void MoveInventoryStack(int from, int to, bool split)' in editor_src
          and 'internal void UiMoveStack(int from, int to, bool split)' in editor_src
          and 'TrySpendSlot(SelectedHotbar)' in editor_src,
          "Server-owned inventory must use physical stacks and validated UI transfers")
    # The in-game inventory/hotbar now use a local UIDocument instead of
    # manually positioned C# GUI widgets. The UI must show the exact
    # generated atlas icons, not fallback colored rectangles.
    import xml.etree.ElementTree as ET
    ui_file = ROOT / "src/UI/tallowcraft.tui"
    ui_css = ROOT / "src/UI/tallowcraft.tcss"
    ui = ET.parse(ui_file).getroot()
    ui_style = ui_css.read_text()
    ui_source = (ROOT / "src/Scripts/WorldGeneration/TallowcraftUI.cs").read_text()
    icon_files = ["grass", "dirt", "stone", "log", "forge", "planks",
                  "coal", "iron_ore", "leaves", "iron_ingot"]
    check(ui.tag == "panel" and ui.attrib.get("id") == "screen" and
          len(list(ui.iter("image"))) >= 792 and
          len([x for x in ui.iter("button")
               if x.attrib.get("onclick") == "OnSlot"]) == 72,
          "Full .tui HUD, player inventory and chest must have icon slots")
    ids = [x.attrib["id"] for x in ui.iter() if "id" in x.attrib]
    check(len(ids) == len(set(ids)) and
          all(x in ids for x in ("overlay","hud","page_craft",
              "page_forge","page_chest","chest-stack","notice")),
          "The new UI must have unique, usable element IDs")
    for icon in icon_files:
        png = ROOT / ("src/Textures/UI/" + icon + ".png")
        data = png.read_bytes()
        check(data[:8] == bytes.fromhex("89504e470d0a1a0a") and
              struct.unpack_from(">II",data,16)==(32,32),
              "Item icon must be a cropped 32x32 PNG: "+icon)
        opts = png.with_suffix(".png.import").read_text()
        check(all(x in opts for x in ("filter = nearest","mipmaps = 0",
              "compression = 0","fullResolution = 1")),
              "Item icon must use lossless nearest filtering: "+icon)
        check(any(x.attrib.get("src") == "Textures/UI/"+icon+".png"
                  for x in ui.iter("image")),
              "TUI must show generated item icon: "+icon)
    # A PNG can have a correct signature and IHDR but a corrupt IDAT.
    # The previous planks image passed the header checks yet rendered as a
    # white square. Validate the image's CRCs and decompressed scanlines.
    plank_png = (ROOT / "src/Textures/UI/planks.png").read_bytes()
    check(plank_png[24:26] == bytes([8, 6]),
          "Planks icon must be an RGBA8 PNG with transparent background")
    chunk_offset, compressed, reached_end = 8, bytearray(), False
    while chunk_offset + 12 <= len(plank_png):
        length = struct.unpack_from(">I", plank_png, chunk_offset)[0]
        chunk_offset += 4
        typ = plank_png[chunk_offset:chunk_offset + 4]
        chunk_offset += 4
        check(length <= len(plank_png) - chunk_offset - 4,
              "Planks icon has a truncated PNG chunk")
        payload = plank_png[chunk_offset:chunk_offset + length]
        chunk_offset += length
        crc = struct.unpack_from(">I", plank_png, chunk_offset)[0]
        chunk_offset += 4
        check(zlib.crc32(typ + payload) & 0xffffffff == crc,
              "Planks icon has a corrupt PNG chunk CRC")
        if typ == b"IDAT":
            compressed.extend(payload)
        if typ == b"IEND":
            reached_end = True
            break
    check(reached_end and bool(compressed) and
          chunk_offset == len(plank_png),
          "Planks icon is missing image data or its closing PNG chunk")
    try:
        scanlines = zlib.decompress(compressed)
    except zlib.error:
        scanlines = b""
    check(len(scanlines) == 32 * (1 + 32 * 4) and
          len(set(scanlines)) > 16,
          "Planks icon pixel data is unreadable or blank")

    # Only reorder the visual grid. The persistent server inventory must
    # still own indices 0..8 as the hotbar and 9..35 as the backpack.
    check("slot = row == 3 ? col : 9 + row*9 + col;" in ui_source
          and 'slot < BlockInventory.HotbarSlots' in ui_source
          and 'Visible("p3_"+i+"_selected"' in ui_source
          and "HOTBAR  /  SLOTS 1–9" in ui_file.read_text()
          and all(ui.find(".//*[@id='p3_" + str(i) + "']") is not None
                  for i in range(9)),
          "The bottom row below the divider must be the real hotbar")
    check(".menu-content { flex-direction: row; gap: 18; height: 538 }"
              in ui_style
          and ".player-column { width: 610; height: 538;"
              in ui_style
          and ".detail-column { width: 422; height: 538;"
              in ui_style
          and ".detail { gap: 9; width: 100% }" in ui_style
          and "height: 100%" not in ui_style.split(".player-column")[1].split("}")[0],
          "Inventory and crafting panels must fit within the outer frame")
    check(any(x.attrib.get("src") == "Textures/chest_item.png"
              for x in ui.iter("image")) and
          "onclick=\"OnSlot\"" in ui_file.read_text() and
          "public void OnSlot(string id)" in ui_source and
          "public void OnTab(string id)" in ui_source and
          "private void DrawInventory()" not in editor_src and
          "public override void OnGUI()" not in editor_src and
          "TallowcraftUI controller = view.AddComponent<TallowcraftUI>()"
              in editor_src and
          "UI/tallowcraft.tui" in editor_src and
          "#overlay" in ui_style and ".slot-icon" in ui_style,
          "Editor .tui must be mounted locally and replace the old C# GUI")
    profile_src = (ROOT / "src/Scripts/WorldGeneration/PlayerProfileStore.cs").read_text()
    check('fields[0] == "1"' in profile_src
          and 'fields[0] == "2"' in profile_src
          and 'selectedSlot' in profile_src,
          "Player saves must migrate and retain selected physical hotbar slots")
    drop_prefab = (ROOT / "src/Prefabs/WorldDrop.tprefab").read_text()
    drop_script = (ROOT / "src/Scripts/WorldGeneration/WorldDrop.cs").read_text()
    drop_mesh = (ROOT / "src/Scripts/WorldGeneration/DroppedItemMesh.cs").read_text()
    drop_mat = (ROOT / "src/Materials/item_drop.tmat").read_text()
    check("component Tallow.NetworkIdentity enabled 1" in drop_prefab
          and "component Tallow.Rigidbody enabled 1" in drop_prefab
          and "component Tallow.BoxCollider enabled 1" in drop_prefab
          and 'field scriptType "WorldGeneration.WorldDrop"' in drop_prefab
          and "component Tallow.MeshRenderer enabled 1" not in drop_prefab
          and 'texture "Textures/inventory_icons.png"' in drop_mat
          and "doubleSided 1" in drop_mat and "blend 1" in drop_mat,
          "Pickup prefab must keep Jolt physics separate from local visual children")
    check("Scene.Destroy(" not in preview
          and "scene.Destroy(" not in drop_script
          and "Scene.Destroy(" not in drop_script
          and "overlay.Destroy();" in preview
          and "item.Destroy();" in drop_script
          and "GameObject.Destroy();" in drop_script
          and "_editor is not { } editor" in preview,
          "Use supported GameObject.Destroy() and guard the nullable editor")
    check('public const int MaxActive = 256' in drop_script
          and 'public const float DespawnAfter = 300f' in drop_script
          and 'private bool TryPickup(GameObject? gameObject)' in drop_script
          and 'inventory.TryReceiveWorldDrop(ItemType, Quantity)' in drop_script
          and 'public static bool TrySpawn(Scene scene' in drop_script
          and 'if (!WorldDrop.TrySpawn(Scene, material, droppedAt))' in editor_src
          and 'Scene.Despawn(GameObject)' in drop_script
          and 'DroppedItemMesh.ForType(ItemType)' in drop_script
          and 'BlockDropMesh.ForType(ItemType)' in drop_script
          and 'Scene.CreateGameObject("Local pickup visual")' in drop_script
          and 'Matrix4x4.CreateWorld(' in drop_script
          and 'Vector3.DistanceSquared' in drop_mesh,
          "Server mining drops must replicate, merge, expire and render safely")
    block_drop = (ROOT / "src/Scripts/WorldGeneration/BlockDropMesh.cs").read_text()
    cube_material = (ROOT / "src/Materials/block_drop.tmat").read_text()
    check('public const float Size = .28f' in block_drop
          and 'BlockTextureAtlas.TileFor(block, faceId)' in block_drop
          and 'BlockTextureAtlas.FaceUV(tile, faceId' in block_drop
          and 'BlockDropMesh.IsCube(ItemType)' in drop_script
          and 'DroppedItemMesh.IsBillboard(ItemType)' in drop_script
          and 'public static bool IsBillboard(int type)' in drop_mesh
          and 'texture "Textures/block_atlas.png"' in cube_material,
          "Placeable blocks must drop as tiny cubes; ores and ingots as billboards")
    chest_tex = (ROOT / "src/Textures/chest_front.png").read_bytes()
    chest_icon = (ROOT / "src/Textures/chest_item.png").read_bytes()
    for name, raw in (("chest front",chest_tex),("chest item",chest_icon)):
        check(raw[:8] == bytes.fromhex("89504e470d0a1a0a") and
              struct.unpack_from(">II",raw,16)==(32,32) and
              raw[24:26]==bytes([8,6]),
              name+" must be crisp, 32x32, lossless RGBA")
    for name in ("chest_front","chest_item"):
        opts = (ROOT / ("src/Textures/"+name+".png.import")).read_text()
        check(all(value in opts for value in (
              "filter = nearest","mipmaps = 0","compression = 0",
              "fullResolution = 1")),name+" texture should remain pixel-sharp")
    check("component Tallow.CSharpBehaviour" in
          (ROOT / "src/Prefabs/WorldDrop.tprefab").read_text()
          and 'Materials/chest_item.tmat' in
          (ROOT / "src/Scripts/WorldGeneration/WorldDrop.cs").read_text()
          and 'Materials/chest_front.tmat' in
          (ROOT / "src/Scripts/WorldGeneration/BlockWorld.cs").read_text(),
          "Chest texture and dropped item art should be wired into the renderer")
    chest_code = (ROOT / "src/Scripts/WorldGeneration/WorldChestStorage.cs").read_text()
    world_code = (ROOT / "src/Scripts/WorldGeneration/BlockWorld.cs").read_text()
    save_code = (ROOT / "src/Scripts/WorldGeneration/WorldEditSave.cs").read_text()
    check('public const int SlotCount = 27' in chest_code
          and 'public bool TryTransfer(BlockPosition position' in chest_code
          and 'ChestSaveCodec.Encode(chests)' in save_code
          and 'ChestSaveCodec.TryDecode(bytes' in save_code
          and 'WorldEditSave.Save(folder, id, seed, snapshot, revisions, chests)' in world_code
          and '_chests.IsEmpty(target)' in world_code
          and 'TryReadChest(pos' in editor_src
          and 'private void MoveChestStack(int from,int to,bool split)' in editor_src
          and 'private void OpenNearbyChest()' in editor_src,
          "Chest transfers must be server-owned and saved atomically with terrain")
    print(f"PASS: {len(joint_names)} joints, {vertices} skinned vertices, "
          f"{triangles} triangles, 4 animation clips, player prefab and hotbar input")


if __name__ == "__main__":
    verify()
