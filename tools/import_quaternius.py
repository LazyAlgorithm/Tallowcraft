#!/usr/bin/env python3
"""Fetch pinned CC0 Quaternius base character and universal animation library.

Run once in GitHub Actions; this does not change the Tallow engine.
"""
import hashlib
import json
from pathlib import Path
import struct
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parents[1]
URLS = [
    ("https://raw.githubusercontent.com/programasweights/avatar/a488723e3a93d73d5c562d88f8a85353c37bc721/public/assets/character.glb",
     ROOT / "src/Models/QuaterniusBaseCharacter.glb", 8_000_000),
    ("https://raw.githubusercontent.com/Seyamalam/blood-league-kickoff/aa02a4e6d8337a0604d2da131bcbbeb1f01badf0/public/assets/vendor/quaternius/universal-animation-library.glb",
     ROOT / "src/Animations/QuaterniusUniversalLibrary.glb", 12_000_000),
]

def get_document(raw):
    if len(raw) < 28 or struct.unpack_from("<III", raw) != (0x46546C67, 2, len(raw)):
        raise RuntimeError("Not a valid GLB 2.0 asset")
    size, kind = struct.unpack_from("<II", raw, 12)
    if kind != 0x4E4F534A:
        raise RuntimeError("No JSON chunk")
    start = 20 + size
    if start + 8 > len(raw):
        raise RuntimeError("Truncated GLB")
    binary_size, binary_kind = struct.unpack_from("<II", raw, start)
    if binary_kind != 0x004E4942 or start + 8 + binary_size > len(raw):
        raise RuntimeError("GLB binary chunk invalid")
    return json.loads(raw[20:start].decode("utf-8"))

def choose(names, role, preferred):
    for candidate in preferred:
        for name in names:
            if name.casefold() == candidate.casefold():
                return name
    disallowed = ("crouch", "back", "side", "left", "right", "rifle", "gun",
                  "sword", "zombie", "swim", "sit", "holding", "combat")
    valid = [n for n in names
             if role in n.casefold()
             and not any(word in n.casefold() for word in disallowed)]
    if valid:
        return sorted(valid, key=lambda n: (len(n), n.casefold()))[0]
    raise RuntimeError("No suitable " + role + " clip; available: " + repr(names))

def main():
    downloads = []
    for url, destination, max_length in URLS:
        with urlopen(Request(url, headers={"User-Agent": "Tallowcraft-CC0-import"}), timeout=90) as response:
            raw = response.read(max_length + 1)
        if len(raw) > max_length or len(raw) < 40_000:
            raise RuntimeError("Unexpected file length")
        doc = get_document(raw)
        downloads.append((url, destination, raw, doc))
    model, library = downloads[0][3], downloads[1][3]
    if not model.get("skins") or not library.get("skins") or not library.get("animations"):
        raise RuntimeError("Sources missing rig or animations")
    def bones(doc):
        return {doc["nodes"][j].get("name")
                for skin in doc["skins"] for j in skin["joints"]}
    common = (bones(model) & bones(library)) - {None}
    if len(common) < 24:
        raise RuntimeError("Rig does not match library (matching joints: " + str(len(common)) + ")")
    clips = [a.get("name", "") for a in library["animations"]]
    mapping = {
        "Idle": choose(clips, "idle", ("Idle", "Idle_Normal", "Idle_Standing")),
        "Walk": choose(clips, "walk", ("Walk", "Walking", "Walk_Forward")),
        "Run": choose(clips, "run", ("Run", "Running", "Sprint", "Jog")),
        "Jump": choose(clips, "jump", ("Jump", "Jump_Start", "Jump_Up")),
    }
    if len(set(mapping.values())) != 4:
        raise RuntimeError("Not four unique movement clips")
    for url, destination, raw, doc in downloads:
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(raw)
        print(str(destination.relative_to(ROOT)), len(raw), hashlib.sha256(raw).hexdigest())
    (ROOT / "src/Models/QuaterniusBaseCharacter.glb.tmeta").write_text(
        "id 817a21f93d1a4af0ab7d2c1f08bb34ce\n")
    (ROOT / "src/Animations/QuaterniusUniversalLibrary.glb.tmeta").write_text(
        "id 3c8edb30de1e4a98b7d12f6d909fd502\n")
    metadata = {
        "license": "CC0-1.0",
        "source_urls": [item[0] for item in downloads],
        "sha256": [hashlib.sha256(item[2]).hexdigest() for item in downloads],
        "matching_joints": len(common),
        "selected": mapping,
        "available": clips,
        "character": "Quaternius Universal Base Characters - Superhero Male",
        "animation_pack": "Quaternius Universal Animation Library Standard in-place",
    }
    (ROOT / "src/Animations/QuaterniusImport.json").write_text(json.dumps(metadata, indent=2) + "\n")
    (ROOT / "src/Models/QUATERNIUS_LICENSE.md").write_text(
        "# Quaternius CC0 player assets\n\n"
        "Character: Quaternius Universal Base Characters, Superhero Male from the free Standard release. "
        "https://quaternius.com/packs/universalbasecharacters.html\n\n"
        "Animation: Quaternius Universal Animation Library (Standard, no root motion). "
        "https://quaternius.com/packs/universalanimationlibrary.html\n\n"
        "Both originally published under CC0 1.0 Universal (https://creativecommons.org/publicdomain/zero/1.0/). "
        "Downloaded from pinned public GitHub community derivatives shown in QuaterniusImport.json. "
        "The model is a Superhero Male, not the premium Regular body variant.\n"
    )
    print("Matching humanoid joints:", len(common))
    print("Chosen movement clips:", mapping)

if __name__ == "__main__":
    main()
