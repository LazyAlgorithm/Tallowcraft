"""Validate Tallowcraft's committed rigged GLB and player asset wiring.

Pure Python 3 standard library; does not require Tallow or a graphics card.
It tests asset structure, not actual rendering, animation quality or networking.
"""
from __future__ import annotations

import json
from pathlib import Path
import struct

ROOT = Path(__file__).resolve().parents[1]
MODEL = ROOT / "src/Models/Adventurer.glb"
PREFAB = ROOT / "src/Prefabs/Player.tprefab"
ANIMATIONS = ROOT / "src/Animations"
INPUT = ROOT / "src/Input.tinput"

COMPONENTS = {5120: ("b", 1), 5121: ("B", 1), 5122: ("h", 2),
              5123: ("H", 2), 5125: ("I", 4), 5126: ("f", 4)}
ARITY = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


def check(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def glb() -> tuple[dict, bytes]:
    raw = MODEL.read_bytes()
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
    check(doc["asset"]["version"] == "2.0", "Not glTF 2.0")
    check(len(doc["skins"]) == 1, "Expected a single humanoid skin")
    skin = doc["skins"][0]
    joint_names = [doc["nodes"][index]["name"] for index in skin["joints"]]
    check(len(joint_names) == 11 and len(set(joint_names)) == 11,
          "Expected eleven distinct named joints")
    check({"Hips", "Chest", "Head", "UpperArm.L", "LowerArm.L",
           "UpperArm.R", "LowerArm.R", "UpperLeg.L", "LowerLeg.L",
           "UpperLeg.R", "LowerLeg.R"} == set(joint_names),
          "Skeleton no longer matches the animation clips")
    check(len(accessor(doc, buf, skin["inverseBindMatrices"])) == len(joint_names),
          "Inverse bind matrix count does not match skeleton")

    vertices = 0
    triangles = 0
    for primitive in doc["meshes"][0]["primitives"]:
        attrs = primitive["attributes"]
        check({"POSITION", "NORMAL", "JOINTS_0", "WEIGHTS_0"} <= attrs.keys(),
              "Missing required skinned mesh attributes")
        positions = accessor(doc, buf, attrs["POSITION"])
        normals = accessor(doc, buf, attrs["NORMAL"])
        joints = accessor(doc, buf, attrs["JOINTS_0"])
        weights = accessor(doc, buf, attrs["WEIGHTS_0"])
        check(len(positions) == len(normals) == len(joints) == len(weights),
              "Skin attribute lengths disagree")
        check(all(abs(sum(w) - 1) < 0.001 for w in weights),
              "Skin weights are not normalized")
        check(all(0 <= j < len(joint_names) for v in joints for j in v),
              "Skin references a missing joint")
        indices = [x[0] for x in accessor(doc, buf, primitive["indices"])]
        check(len(indices) % 3 == 0 and all(0 <= x < len(positions) for x in indices),
              "Invalid triangle indices")
        vertices += len(positions)
        triangles += len(indices) // 3

    for stem, name in [("idle", "Idle"), ("walk", "Walk"), ("run", "Run"), ("jump", "Jump")]:
        lines = (ANIMATIONS / (stem + ".tanim")).read_text().splitlines()
        check(lines and lines[0].startswith("clip " + name + " "),
              name + " clip name mismatch")
        tracks = [line[6:] for line in lines if line.startswith("track ")]
        check(bool(tracks) and set(tracks) <= set(joint_names),
              name + " animation has unmatched or missing bones")

    prefab = PREFAB.read_text()
    check('field model "Models/Adventurer.glb"' in prefab, "Prefab model path mismatch")
    check("component Tallow.SkinnedMeshRenderer enabled 1" in prefab,
          "No skinned mesh renderer in player prefab")
    check("component Tallow.MeshRenderer enabled 1" in prefab
          and "field shadowOnly 0" in prefab,
          "Player visual needs its own local shadow-only renderer")
    character = (ROOT / "src/Scripts/FirstPersonCharacter.cs").read_text()
    check("UpdateLocalModelVisibility()" in character
          and "public override void LocalUpdate" in character
          and "renderer.ShadowOnly = shadowOnly" in character
          and "bool shadowOnly = ControlsCamera" in character,
          "Owner-only first-person model hiding is missing")
    check("component Tallow.Animator enabled 1" in prefab,
          "Player is missing an Animator")
    check("component Tallow.CharacterController enabled 1" in prefab,
          "Player is missing Jolt collision controller")
    check("builtin:capsule" not in prefab,
          "Visible placeholder capsule has reappeared")
    check('field scriptType "Tallowcraft.CharacterAnimation"' in prefab,
          "Character animation script is not attached")

    actions = INPUT.read_text()
    for i in range(1, 7):
        check("action Custom" + str(i) + " button" in actions
              and "bind key Alpha" + str(i) in actions,
              "Hotbar number key binding is missing: " + str(i))
    check("bind wheel up" in actions and "bind wheel down" in actions,
          "Hotbar mouse wheel actions are missing")
    print(f"PASS: {len(joint_names)} joints, {vertices} skinned vertices, "
          f"{triangles} triangles, 4 animation clips, player prefab and hotbar input")


if __name__ == "__main__":
    verify()
