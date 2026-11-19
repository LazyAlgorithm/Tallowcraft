"""Validate Tallowcraft's committed rigged GLB and player asset wiring.

Pure Python 3 standard library; does not require Tallow or a graphics card.
It tests asset structure, not actual rendering, animation quality or networking.
"""
from __future__ import annotations

import json
from pathlib import Path
import struct

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
    check("public const int MaxType = 10" in inventory_src
          and 'serialized.StartsWith("1|"' in inventory_src
          and 'serialized.StartsWith("2|"' in inventory_src
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
    print(f"PASS: {len(joint_names)} joints, {vertices} skinned vertices, "
          f"{triangles} triangles, 4 animation clips, player prefab and hotbar input")


if __name__ == "__main__":
    verify()
