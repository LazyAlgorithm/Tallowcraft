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
- Hold right mouse: mine the targeted block (stone and ores take longer than dirt and leaves)
- E (Use): alternate place control
- 1–6: select grass, dirt, stone, log, forge, planks
- 7, 8, 9: equip a crafted pickaxe, axe or shovel; 0: switch back to hands
- Mouse wheel: cycle selected block
- Q: cycle cube, slab, stair and half-wall shapes
- F: rotate the next placed block in 90° increments
- Ctrl while placing: place an upper slab or upside-down stair
- Left-click a lower slab's top (or an upper slab's bottom) with the same slab type selected to combine both halves into one full block
- On-screen hotbar displays selection, shape, rotation, and available item quantities. Controls are networked with `Input.tinput`.
- Tab (or gamepad West): open/close the ten-slot materials inventory and crafting screen. Escape (or gamepad East) also closes it. While it is open, gameplay input—including mouse look—is disabled instead of merely freezing the camera, so closing the screen does not snap to an accumulated mouse angle. The on-screen Close button works too.
- Craft stone tools in the Crafting page with stone and planks: pickaxe (3 stone + 2 planks), axe (2 stone + 2 planks), or shovel (1 stone + 2 planks). Click Equip to select one, or Use hands to unequip. Recraft a damaged tool to repair it.
- **Forge progression:** place a Forge block (hotbar slot 5), stand within 3.5 m of it, press Tab, then select **Forge page**. Smelt 1 iron ore + 1 coal into 1 iron ingot, or smelt batches of five. Use the ingots and planks to craft an iron pickaxe (3 ingots), axe (2), or shovel (1); each costs 2 planks. Iron tools mine their specialty blocks faster and last longer. The server checks proximity to the actual forge for every smelt/iron-craft request.
- Use **7/8/9** for pickaxe/axe/shovel; those keys automatically select the iron version if you own it, otherwise the stone version. Use **0** for bare hands. Iron ingots are inventory items, not placeable voxel blocks.
- Pickaxes mine stone, ores and forges faster; axes mine logs and planks faster; shovels mine dirt and grass faster. Each successfully mined block consumes one durability point. A broken tool automatically unequips. Iron tools have higher durability and a 5.5× effective-mining speed multiplier versus stone's 3×.
- Mining a block deposits one material directly into the server-owned player inventory; coal, iron and leaves are collected even though they are not currently selectable hotbar slots.
- `BlockEditor.CreativeMode` is **enabled by default** to preserve unrestricted test-building. Disable it on the player prefab's BlockEditor component for survival building: the player starts with a small supply of each of the six hotbar materials, and successful placements consume one material. Empty slots cannot place blocks; mining replenishes stock. There are no physical pickups yet.
- Red wireframe shows the voxel that right-click will remove; green wireframe previews the chosen block shape/orientation on left-click. Orange indicates blocked placement. Previews are client-local and gameplay edits remain server-authoritative.

Blocks can only be edited within 6 metres, and placement is refused when another physics body occupies the destination.

## Architecture

- **Streaming:** directional visible chunks around the local player's camera, plus physical chunks around **every player on the server**.
- **Mesh:** 16×16×16 blocks, 0.5-metre occupancy for partial shapes, deterministic terrain, and greedy merging of compatible half-cell faces within each block. Atlas UV seams deliberately prevent merging textured quads across entire blocks; this is an explicit performance tradeoff pending a tiling shader. Cube-only chunks now take a one-metre meshing fast path (8× fewer occupancy cells); chunks with slabs/stairs/half-walls or neighboring partial blocks use the original half-metre path to preserve edge geometry. Neighboring chunk faces consult edits across boundaries.
- **Character:** a real **Quaternius Universal Base Characters** CC0 skinned humanoid (free Standard *Superhero Male* model) plus the CC0 **Universal Animation Library**. Idle/Walk/Sprint/Jump are native library clips retargeted by joint name using `SkinnedMeshRenderer.clipLibrary`, not the old procedural .tanim motions. The Jolt capsule remains an invisible collision proxy and the model retains first-person shadow-only visibility. Sources and licensing: `src/Models/QUATERNIUS_LICENSE.md`.
- **Building UX:** on-screen local-player hotbar driven by authoritative network `TickInput` (`Custom1..8`, Ability1 and Ability2). The project includes an `Input.tinput` map for number keys and mouse wheel.
- **Inventory and tools:** authoritative per-player `BlockInventory` with nine voxel material counts plus iron ingots, and `PlayerTools` with three stone and three iron tools. Validated/versioned `[Sync] InventoryState` and `[Sync] ToolState` replicate quantity, equipped tool and durability. Server-only RPCs validate crafting/equip commands. Tool durability is spent only on a successful server-authoritative block break. The server persists inventory, equipped tool/durability and selected block/shape/facing per world and per persistent player token. Current inventory tracks materials rather than individual oriented or partial shapes.
- **Block states:** packed 16-bit type + facing + shape + upper flag. Types: grass, dirt, stone, wood, forge, planks. Shapes: cube, slab, stair, half-wall. Rotations affect stairs, half-walls, and the forge's front texture.
- **Textures:** the approved 3×3 block artwork is now in `src/Textures/block_atlas.png`: a lossless, indexed-color 108×108 PNG with nine 32×32 pixel-art tiles and 2px extruded borders. UVs point at texel centers; chunky source pixels are preserved by nearest-neighbor sampling. `src/Textures/block_atlas.png.import` configures `filter = nearest`, `wrap = clamp`, `mipmaps = 0`, `compression = 0`, and `fullResolution = 1`. Tallowcraft's build config also explicitly excludes this atlas from BC1/BC3 compression. Greedy chunk meshes share the atlas.
- **Physics:** nearby static Jolt MeshColliders. Editing rebuilds the affected chunk's mesh and calls MeshCollider.Refresh(); chunk-edge edits rebuild the affected neighbor as well. Placement checks the occupied half-cells rather than one oversized full-block overlap volume, and merges a matching slab into a full block when clicked on its exposed horizontal face.
- **Network authority:** only server-side player TickInput can edit blocks. The player is a networked prefab; procedural chunk GameObjects remain transient and local to each machine.
- **Sync and joining:** the shared BlockWorld has a NetworkIdentity with Relevance=Everyone. Its [Sync] EditState carries only world identity and seed; clients request compact chunk baselines, then receive live per-block deltas. Workers use immutable section snapshots and discard stale jobs. A client-owned player RPC binds a stable local identity token to a server-owned saved profile; gameplay waits until loading completes.
- **WorldGeneration:** a separate C# assembly definition with one primary type per source file.

## Prototype limitations

**Inventory now persists across reconnects and restarts:** server-owned per-world profiles save all ten material counts, the equipped tool and remaining durability, plus hotbar block/shape/rotation. Saves occur every 30 seconds after changes and on player despawn, using a checksummed primary file and previous backup. Missing profiles receive starter supplies only once; corrupted profiles are not silently reset. Player identity uses a random persistent token, not a verified account login—this is prototype identity, **not suitable for untrusted public multiplayer authentication**. On one machine, host and guest use separate identity slots; set `TALLOWCRAFT_PROFILE_SLOT` to a different alphanumeric identifier when running multiple clients from the same PC. Mining resources still go straight into inventory rather than physical pickups; collecting past the 999-item material limit discards the extra resource for now.

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
