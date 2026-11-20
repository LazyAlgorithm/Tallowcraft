# Tallowcraft

A voxel world built in [Tallow](https://tallowengine.com/) to exercise the C# gameplay, Jolt physics, ECS and networking APIs.

## Running

Requires the current Tallow **feature/csharp-gameplay** engine branch (including the raycast-normal and MeshCollider.Refresh physics update). The local engine path in project.tallow is machine-specific; change it if you clone this project elsewhere.

Open project.tallow with Tallow and press Play. The Main scene starts a local **Host** network session on UDP port **7777**. It spawns the local player from src/Prefabs/Player.tprefab at one of four Spawn Points. Start a second instance as a client with the engine's --connect 127.0.0.1 option, or select Client in the Network Session component.

### Controls

- WASD / mouse: move / look
- Space: jump
- Shift: sprint
- Ctrl: crouch
- Left mouse: place the selected block
- Hold right mouse: mine the targeted block (stone and ores take longer than dirt and leaves). A pixelated, progressively spreading **ten-stage crack overlay** appears on the actual cube/slab/stair/half-wall faces. It resets immediately on release, target changes or breaking.
- **E (Interact):** open the nearest placed chest within 4.5m directly into the Storage UI. E never places blocks; use **left mouse** (or controller North) to place. E does nothing if no chest is nearby.
- 1–6: select the first six physical hotbar slots (not fixed material types); scroll the mouse wheel for all nine slots
- 7, 8, 9: equip a crafted pickaxe, axe or shovel; 0: switch back to hands
- Mouse wheel: cycle selected block
- Q: cycle cube, slab, stair and half-wall shapes
- F: rotate the next placed block in 90° increments
- Ctrl while placing: place an upper slab or upside-down stair
- Left-click a lower slab's top (or an upper slab's bottom) with the same slab type selected to combine both halves into one full block
- The new **.tui nine-slot HUD** displays the generated icon for each material, the live quantity, selected slot, building shape/rotation, and equipped tool durability. The C# UI creates one local-only UIDocument per player; all gameplay state stays on the server.
- Tab (or gamepad West): open/close the **36-slot inventory** (9 hotbar + 27 backpack), crafting, forge, and storage pages. Stand near a placed chest, then click the Storage tab to open its 27-slot storage; click a source stack then a destination to transfer it. Toggle Move half to transfer half a stack. Escape (or gamepad East) also closes it. While it is open, gameplay input—including mouse look—is disabled instead of merely freezing the camera, so closing the screen does not snap to an accumulated mouse angle. The on-screen Close button works too.
- Craft stone tools in the Crafting page with stone and planks: pickaxe (3 stone + 2 planks), axe (2 stone + 2 planks), or shovel (1 stone + 2 planks). Click Equip to select one, or Use hands to unequip. Recraft a damaged tool to repair it.
- **Forge progression:** place a Forge block (hotbar slot 5), stand within 3.5 m of it, press Tab, then select **Forge page**. Smelt 1 iron ore + 1 coal into 1 iron ingot, or smelt batches of five. Use the ingots and planks to craft an iron pickaxe (3 ingots), axe (2), or shovel (1); each costs 2 planks. Iron tools mine their specialty blocks faster and last longer. The server checks proximity to the actual forge for every smelt/iron-craft request.
- Use **7/8/9** for pickaxe/axe/shovel; those keys automatically select the iron version if you own it, otherwise the stone version. Use **0** for bare hands. Iron ingots are inventory items, not placeable voxel blocks.
- Pickaxes mine stone, ores and forges faster; axes mine logs and planks faster; shovels mine dirt and grass faster. Each successfully mined block consumes one durability point. A broken tool automatically unequips. Iron tools have higher durability and a 5.5× effective-mining speed multiplier versus stone's 3×.
- Mining a block now spawns a **small floating item entity** with gravity. The server checks actual player distance before collecting it into an inventory stack. Nearby identical drops merge (up to 64), drops expire after five minutes, and at most 256 may exist per scene. If spawning fails at the limit, the server credits the item directly rather than losing it. Coal, iron ore and leaves can be moved into any hotbar slot and placed if their voxel types support placement. Stack limit is 64 per slot; click a stack and then a destination to move or swap, or enable Move half to split a stack in half.
- `BlockEditor.CreativeMode` is **enabled by default** to preserve unrestricted test-building. Disable it on the player prefab's BlockEditor component for survival building: the player starts with a small supply of each of the six hotbar materials, and successful placements consume one material. Empty slots cannot place blocks; mining replenishes stock. Mined items use physical server-owned drop entities.
- Red wireframe shows the voxel that right-click will remove; green wireframe previews the chosen block shape/orientation on left-click. Orange indicates blocked placement. Previews are client-local and gameplay edits remain server-authoritative.

Blocks can only be edited within 6 metres, and placement is refused when another physics body occupies the destination.

## Architecture

- **Streaming:** directional visible chunks around the local player's camera, plus physical chunks around **every player on the server**.
- **Mesh:** 16×16×16 blocks, 0.5-metre occupancy for partial shapes, deterministic terrain, and greedy merging of compatible half-cell faces within each block. Atlas UV seams deliberately prevent merging textured quads across entire blocks; this is an explicit performance tradeoff pending a tiling shader. Cube-only chunks now take a one-metre meshing fast path (8× fewer occupancy cells); chunks with slabs/stairs/half-walls or neighboring partial blocks use the original half-metre path to preserve edge geometry. Neighboring chunk faces consult edits across boundaries.
- **Character:** a real **Quaternius Universal Base Characters** CC0 skinned humanoid (free Standard *Superhero Male* model) plus the CC0 **Universal Animation Library**. Idle/Walk/Sprint/Jump are native library clips retargeted by joint name using `SkinnedMeshRenderer.clipLibrary`, not the old procedural .tanim motions. The Jolt capsule remains an invisible collision proxy and the model retains first-person shadow-only visibility. Sources and licensing: `src/Models/QUATERNIUS_LICENSE.md`.
- **Building UX:** on-screen local-player hotbar driven by authoritative network `TickInput` (`Custom1..8`, Ability1 and Ability2). The project includes an `Input.tinput` map for number keys and mouse wheel.
- **Breaking overlay:** `BuildPreview` draws one local-only, collider-free `MeshRenderer` around only the mined block, with `BlockDamageMesh` generating outward partial-shape faces using the same 2×2×2 occupancy as the world mesher. Its cutout material uses `Shaders/block_break_damage.tsgraph` (editable Shader Graph), a generated `.tshader` and a 160×16 lossless crack-stage atlas. Server-authoritative, quantized `MiningPercent` and synced mining target select the crack stage. A target change rebuilds only this tiny overlay mesh, not a terrain chunk. No extra networked crack objects or engine changes.
- **Inventory and tools:** authoritative per-player 9-slot hotbar + 27-slot backpack `BlockInventory` with 64-item physical stacks of nine voxel types and iron ingots, and `PlayerTools` with three stone and three iron tools. Validated/versioned `[Sync] InventoryState` and `[Sync] ToolState` replicate quantity, equipped tool and durability. Server-only RPCs validate crafting/equip commands. Tool durability is spent only on a successful server-authoritative block break. The server persists inventory, equipped tool/durability and selected block/shape/facing per world and per persistent player token. Current inventory tracks materials rather than individual oriented or partial shapes.
- **Block states:** packed 16-bit type + facing + shape + upper flag. Types: grass, dirt, stone, wood, forge, planks. Shapes: cube, slab, stair, half-wall. Rotations affect stairs, half-walls, and the forge's front texture.
- **Storage chests:** craft a chest from 8 planks, put it into a hotbar slot, and place it like any block. It has a dedicated lid/lock texture overlay and a separate dropped-item icon. Chest contents are 27 physical item stacks, shared by players, saved with the **same atomic world generation** as voxel edits, and recovered together on corruption. Empty the chest before mining it to prevent loss or duplication. The server validates proximity, selected chest, and both slot indices for every transfer.
- **Item artwork:** `src/Textures/inventory_icons.png` is a new 5×2 sprite sheet of ten transparent 32×32 pixel-art icons including iron ingots, with nearest-neighbor sampling and compression/mips disabled. The atlas is used on world item-drop sprites through `Materials/item_drop.tmat`. The inventory, hotbar, chest, forge, and crafting screens now use `src/UI/tallowcraft.tui` and `tallowcraft.tcss`, which support real images. The generated sprite atlas has been split into 10 individual uncompressed 32x32 icons under `src/Textures/UI`; the existing `chest_item.png` provides the eleventh icon. The code-behind is `TallowcraftUI.cs`.
- **Textures:** the approved 3×3 block artwork is now in `src/Textures/block_atlas.png`: a lossless, indexed-color 108×108 PNG with nine 32×32 pixel-art tiles and 2px extruded borders. UVs point at texel centers; chunky source pixels are preserved by nearest-neighbor sampling. `src/Textures/block_atlas.png.import` configures `filter = nearest`, `wrap = clamp`, `mipmaps = 0`, `compression = 0`, and `fullResolution = 1`. Tallowcraft's build config also explicitly excludes this atlas from BC1/BC3 compression. Greedy chunk meshes share the atlas.
- **Physics:** nearby static Jolt MeshColliders. Editing rebuilds the affected chunk's mesh and calls MeshCollider.Refresh(); chunk-edge edits rebuild the affected neighbor as well. Placement checks the occupied half-cells rather than one oversized full-block overlap volume, and merges a matching slab into a full block when clicked on its exposed horizontal face.
- **Network authority:** only server-side player TickInput can edit blocks. The player is a networked prefab; procedural chunk GameObjects remain transient and local to each machine.
- **Sync and joining:** the shared BlockWorld has a NetworkIdentity with Relevance=Everyone. Its [Sync] EditState carries only world identity and seed; clients request compact chunk baselines, then receive live per-block deltas. Workers use immutable section snapshots and discard stale jobs. A client-owned player RPC binds a stable local identity token to a server-owned saved profile; gameplay waits until loading completes.
- **WorldGeneration:** a separate C# assembly definition with one primary type per source file.

## Prototype limitations

**Inventory now persists across reconnects and restarts:** server-owned per-world profiles save all 36 material stacks, legacy reserve amounts, the equipped tool and remaining durability, plus selected hotbar index, shape and rotation. Saves occur every 30 seconds after changes and on player despawn, using a checksummed primary file and previous backup. Missing profiles receive starter supplies only once; corrupted profiles are not silently reset. Player identity uses a random persistent token, not a verified account login—this is prototype identity, **not suitable for untrusted public multiplayer authentication**. On one machine, host and guest use separate identity slots; set `TALLOWCRAFT_PROFILE_SLOT` to a different alphanumeric identifier when running multiple clients from the same PC. Mining now produces physical, collectible drops; collecting past the 999-item material limit leaves the drop in the world until it expires.

**World edits now persist to disk**, with a versioned server-owned 16×16×16 section format, atomic save generations, 30-second autosaving and a final save on scene shutdown. If the newest saved generation is corrupted, the game attempts recovery from the previous validated save; unrecoverable files are not silently overwritten. There is no global 512-modified-block cap. A client receives the seed and world identity, requests edited sections in bounded pages as needed, validates revision + SHA-256, and receives individual block deltas after the baseline. Cached sections can be reused on reconnect when the server confirms a matching revision and hash. The current C# RPC bridge only broadcasts Everyone-target RPCs, so page replies carry the intended client ID and other clients discard them; **true per-recipient transport filtering still needs a game-message API exposed in Tallow C#**. Test full host/client transfer and browser disk behavior in-engine before treating this as production verified.

Supported block types are 0 (air), 1 (grass), 2 (dirt), 3 (stone), 4 (wood), 5 (forge), 6 (planks), 7 (coal), 8 (iron) and 9 (leaves). The vertical world currently spans Y=-32..47, and chunk collision/rendering on a dedicated server has not yet been separated into headless-only physics chunks. Half-cell meshing is more expensive than the previous full-cube greedy mesher; profiling is required before increasing render radius. Moving/removing a block requires its chunk collider to have been generated first.

### Rigged-character caveats

The first-person player's **own** rigged body is rendered as shadow-only on their machine: it stays out of the camera view while still casting its full animated shadow on the ground. Remote clients continue rendering the character body and shadow normally. The imported Quaternius clips play close to their authored speed while movement-driven animation switching remains smoothed. Whether these animations look right in motion and shadow still needs an in-game test. Ownership and local visibility are checked in `FirstPersonCharacter.LocalUpdate` without disabling the player or its collider. This needs a host/client runtime check, especially when ownership arrives after spawning.

The new player art is Quaternius's CC0 humanoid with **65 matching skeleton joints** to the Universal Animation Library, not a block-built character. The model and animation files are versioned in the repository; the original procedural Adventurer and .tanim clips are retained as unused fallback assets. Character and animation provenance is documented in `src/Animations/QuaterniusImport.json`. This is a **first integration pass**: verify skinning, animation ordering, network interpolation, camera/head occlusion, crouch, and feet placement in Play mode. No passing Tallow build has been recorded for this commit. A local-player-specific head visibility mask is still needed if the model occludes the first-person camera.

### Texture import requirements

This atlas uses the per-picture import controls from the current Tallow `feature/csharp-gameplay` branch. If a local engine revision predates those controls, the project PNG is still lossless but can look blurry because the older renderer applies smooth/anisotropic filtering. Pull the updated engine branch before testing. In Tallow's texture Inspector, select `Textures/block_atlas.png` to see Nearest, Clamp, no mipmaps, no compressed copy and Keep full resolution. Close and reopen the project or reload the texture if the old atlas remains cached.

### Targeting preview

The red target outline and shape-specific green placement outline use the same ray-to-block targeting and placement-planning methods as the authoritative server. Same-material slabs can stack into a full block; the preview also changes to the resulting cube. Some partial blocks show internal half-cell grid lines in the initial wireframe; these are previews only, not additional colliders. Actual blocks and collision remain unchanged. In a multiplayer client, selection previews are based on the latest replicated selection state and may briefly lag a fast hotbar key press. Verify the outlines against server placement on vertical faces, chunk seams and partial shapes.

### Testing checklist

1. Host locally: check the capsule is invisible and the skinned character appears to clients; walk, jump, sprint, crouch and verify the camera is unobstructed; then break/replace top and side faces.
2. Edit a block at a chunk seam (X/Z multiple of 16); verify both chunks update without cracks.
3. Walk away and return; verify the edit remains.
4. Connect a second client; verify the terrain matches the host and both players see edits.
5. Join late after the host has edited blocks; verify the late join receives the modified world.
6. Stand far apart with two players; verify server collision remains around both.
7. Turn rapidly while jobs are active; verify stale meshes never overwrite newer edits.
8. Place each type, rotate a forge and each stair/half-wall direction, place lower and upper slabs, and ensure collision matches the visible geometry.
9. Edit partial shapes at chunk seams and verify no ghost faces or collision gaps.
10. Stress editing and monitor network bandwidth. A complete multiplayer/restart/CI soak is still needed.

The repo contains gameplay sources and a Tallow scene, not the engine itself.

### Ground item drops

Every mined voxel now spawns a tiny networked, textured item drop with a
Jolt Rigidbody and Debris collider. Dropped items fall and rest on terrain
without blocking player movement or the mining raycast. The server alone
determines each material/quantity and accepts a pickup only within 1.6 m of
the player's real position after a 0.5 s delay. Identical nearby drops
merge to stacks of 64; drops despawn after five minutes, and a bounded
256-entity cap prevents unlimited networked rigid bodies. If the cap or
prefab load prevents a spawn, resources are credited directly rather than
lost, subject to the existing per-type 999-item cap.

**Ground drop persistence:** active server-owned drops now save with the
same atomic world generation as voxel edits and chest contents (world save v5).
Item type, quantity, position and remaining five-minute lifetime restore after
restart. Offline time does not count against the despawn timer, and physics
velocity is reset when an item loads. A corrupt drops file causes the entire
world to roll back to the previous validated generation. A normal quit saves
the current drop state; automatic checkpoints occur every 30 seconds while
drops exist.

**Crash-recoverable physical pickups:** before a ground-item pickup
credits the player or despawns the item, the server writes a checksummed
redo receipt containing the complete *post-pickup* player profile and the
remaining world drops. Unsaved terrain/chest edits are checkpointed first.
After the receipt is durable, both the world save and profile save commit;
only then is the receipt removed and the pickup applied in memory. Startup
finishes any interrupted receipt before restoring drops or loading profiles.
Corrupt pending receipts block world startup instead of silently discarding
items. This prevents a crash between the two pickup saves from duplicating
or losing the collected ground item.

**Known limits:** this correctness-first path writes a full world generation
synchronously for each successful physical pickup, which can cause hitches
on large worlds. It still needs Linux/Windows host/client verification and
write-failure testing. Chest-to-player transfers, direct-credit fallback
rewards when the drop entity cap is hit, and other inventory operations
are not yet covered by this redo journal. User tokens remain prototype
bearer identities, not authenticated public multiplayer accounts.

### Chests and world save v4 (supported by v5)

- Craft **Storage chest** with 8 planks in the Craft tab (recipe 6). Move
  it to the hotbar and place it. Chest blocks are cube-shaped with four
  front-facing orientations; the main terrain uses the existing planks tile
  while an inexpensive local face decoration shows the distinctive
  chest lid/lock texture. Unlike a world point light, these details are
  only rendered for loaded chunks and use no new network entities.
- Stand within **4.5m** of a chest, open Tab > Storage. Click a source
  and destination for a full-stack transfer or swap; turn on Move half
  to split a stack, including transfers within a chest.
  The server rechecks the real chest voxel and player range for every
  interaction. Other players viewing the same chest see updates.
- Chest contents are stored as a sparse, deterministic 27-stack map.
  The world save advances from v3 to v4 and stores a checksummed
  `chests.bin` in the same generation as the terrain section files.
  Loading an older v3 world starts with no stored chests. A corrupt
  new chest file rolls **the entire world generation** back, never
  restoring orphan contents. Chest-only changes trigger autosaving.
  A filled chest cannot be mined until its slots are empty.
- Player inventory v4 adds item type 11 (chest) while transparently
  migrating previous v1-v3 inventory saves, maintaining all item
  quantities and physical slot positions. Iron ingots remain type 10
  inventory-only and cannot be placed as voxel blocks.


### Rendering fix and manual testing

The block-breaking overlay uses a reliable two-sided, unlit cutout material
instead of depending on the experimental custom Shader Graph. The existing
editable `block_break_damage.tsgraph` is retained as a reference; actual
ten-stage selection comes from the crack mesh UVs, expanded 0.018m beyond
the voxel surface to avoid z-fighting and face culling. It only rebuilds on
target or ten-percent damage-stage changes.

Mined grass, dirt, stone, logs, forge, planks, leaves and chests now drop
as spinning 0.28m miniature cubes with the same exact face atlas tiles as
their world blocks. Coal, iron ore and iron ingots remain single
camera-facing 2D sprites. Visuals are client-local children of the original
networked Jolt pickup body; only the visual rotates, never the physics body.

**GitHub Actions workflows have been removed from both repository branches.**
To run the checks locally rather than triggering Actions:

```bash
dotnet run --project tests/BlockLogicTests/BlockLogicTests.csproj
python3 tests/validate_assets.py
```

Also build the game's .NET 11 scripts in your Tallow editor and verify both
the block cracks and the dropped mesh rendering on the actual graphics backend.

### In-game UI editor (.tui)

This game uses Tallow's **UIDocument** for its in-game HUD and panels, not the
immediate-mode `GUI.Box/Button` layout. Open `src/UI/tallowcraft.tui` in
the Tallow UI editor to edit the element tree and `src/UI/tallowcraft.tcss`
to edit spacing, typography, and colors. The local-only `TallowcraftUI`
code-behind reads synchronized inventory/tool/chest snapshots and forwards
all craft, move, smelt, equipment, chest and reserve operations through the
existing owner-authorized `BlockEditor` server RPCs.

* 9-slot icon HUD, selected slot border, item counts, mining/creative status,
  held material, selected shape, and equipped tool durability.
* 36 inventory slots with actual item images: **three backpack rows
  (slots 9–35) first, then the nine-slot hotbar (slots 0–8) below the
  divider**, matching the Minecraft-style inventory order. Selecting a
  source and destination moves an item; **Move half** splits stacks.
  **Set hotbar active** selects a hotbar slot, or use the mouse wheel in
  gameplay. The .tui API exposes
  click events but no native drag-and-drop hook, so click/click replaces
  the former immediate-mode drag gesture.
* Dedicated tabs for crafting, forge smelting/iron tools, and nearby chest
  storage (27 slots). Content is synchronized with the server, not stored
  in the UI. Close with Tab, Escape, controller Cancel or X.
* The planks icon is now a valid 32×32 RGBA pixel-art cube (the original
  PNG had a corrupt image-data stream and displayed as a white square).
  Asset validation now checks its PNG chunk CRCs and decoded scanlines.
* The inventory and crafting columns use explicit heights with compact
  crafting/tool rows so neither panel extends below the main dialogue.
* 10 cropped icon PNGs in `src/Textures/UI`, plus
  `src/Textures/chest_item.png`, using nearest filtering, no compression,
  and no mipmaps. The original gameplay sprite atlas stays unchanged.
* The document is created only for the **local player**, even in multiplayer.
  It is transient and has no NetworkIdentity. Profiles and worlds keep their
  current save format; there is no UI migration of saved data.

GitHub Actions workflows remain removed; validate locally with
`python3 tests/validate_assets.py` and the Tallow editor's .NET 11
gameplay compile. Asset checks do not verify final on-screen layout.

### Chest storage interaction (fixed)

Open **Tab → Storage** while standing within **4.5m** of a placed chest.
The UI now waits for the server's inventory-open acknowledgement before
requesting the chest, avoiding a dropped one-shot RPC if you switch tabs
quickly. The chest panel displays either its coordinates and storage slots,
or the server's actual reason for rejecting the request.

To store a stack, click any backpack/hotbar stack and click **Store
selected**. To withdraw, click an occupied chest slot and choose **Take
selected**. You can still transfer into a specific slot by clicking the
source and then the desired destination. Use **Move half** before either
action to split a stack. When the receiving slot is nearly full, taking
from a chest transfers only the amount that fits; any remainder stays
in the chest. The per-material inventory cap still applies. All transfers remain server-authoritative,
share chest state between players, and use the existing persisted world
storage; the UI cannot create or duplicate items.

If nothing moves, read the status line at the top of the Storage tab.
It now reports out-of-range chests, incompatible/full slots and failed
server-authorized transfers instead of silently ignoring them.

### Chest E-key interaction

Standing within 4.5m of a placed chest, press **E** to open it directly.
The server checks the actual chest block and the player's position, then
opens the inventory menu and Storage page together. E is now bound to
`Player/Interact`, completely separate from `Player/Use` (left mouse to
place). The action doesn't place voxels or transfer inventory automatically.
If no chest exists within range, the menu stays closed. Press Tab, Escape
or X to close storage and return to gameplay.

When opening Storage through Tab, the UI still waits for the server's
inventory-open acknowledgment before requesting a chest, so rapid tab
switching does not silently lose the open request.
