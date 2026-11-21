# Tallowcraft

A voxel world built in [Tallow](https://tallowengine.com/) to exercise the C# gameplay, Jolt physics, ECS and networking APIs.

## Multiplayer wildlife regions and richer world generation (20 November)

This is a **creative-building and exploration** update; it does not add survival meters or other survival mechanics.

**Wildlife across multiplayer areas.** The authoritative host now maintains up to **four independent 48 m navigation regions**, sharing one when players stand within 34 m of one another. Each active region can maintain up to **10 roaming sheep/pigs**, subject to a global upper bound of 40 animals. The regions follow their respective player areas, prevent duplicate herds at their overlaps, and disappear when nobody needs them. Dying animals finish their animation and spawn existing wool/raw-pork drops even when the region unloads. Newly spawned animals use per-region navigation queries and native CharacterController collision/gravity/steps, rather than binding to Tallow's first navigation surface. The game uses local steering with reachable/random points and navmesh raycasts; **this is not full A* path following**. World voxel edits now publish server-side bounds, debounced and rebuilt only on overlapping regional navmeshes after terrain geometry is ready.

**New exploration features.** The existing four biome types are joined by **Wetlands and Badlands**. Wetlands favor low green ground, bushes, grass and pigs; Badlands use existing sandstone/slate building materials as surface strata. New deeper cave fields create larger chambers farther from spawn. Winding world-space rivers and occasional lake basins carve editable ground into the terrain, with a **single batched transparent pixel-art water surface per near chunk**. Water is **decorative and noncolliding** for now: no swimming, current, buoyancy or fluid placement; the distant LOD currently shows carved basins without the water sheet. Independent seeded **stone circles and timber lookouts** provide more points of interest, using the same absolute-world queries as chunk creation, edits, mesh halos and saved-world regeneration.

The established area around spawn retains its previous river-free ground, and existing **player block edits/save records are unchanged**. However, the procedural generator itself has evolved: in unedited territory outside the protected spawn area, existing worlds can display changed natural terrain/vegetation/caves after this update. Back up worlds before testing if preserving the exact old unedited landscape matters. Future generator-version pinning would be needed to fully preserve old terrain outside saved edits.

To test, run the standalone `BlockLogicTests` project after pulling `main`. Then use Linux Play mode to compare the old spawn area, travel into new biomes, inspect water seams and new landmarks, and open a multiplayer session with the host and guest separated by at least 100 m to exercise independent wildlife regions. Build a wall near animals, watch navigation update, kill an animal while a player moves out of range, and verify loot is still collectible. Performance, shoreline visuals, large-cave shapes and multiplayer nav movement still require Play-mode verification; these code changes were committed without a local engine/player build.

## Roaming sheep and pigs, fleeing and animal loot (20 November)

Sheep and pigs now populate a **moving wildlife habitat** around the currently explored area instead of being confined to the original spawn point. The server waits for nearby terrain/physics meshes to load, then bakes one bounded 48 m navigation surface, periodically moving it when the exploration focus gets more than 24 m away (with a 12-second throttle). A maximum of 10 nearby animals are active at a time. New spawns are spread out, avoid the player's immediate position, and respect the world's deterministic biome sampling: **Meadows** contain sheep and pigs, **Woodlands** lean toward pigs, **Highlands** favor sheep, and **Drylands** have neither. Herd-cell spacing avoids duplicates and defeated animals have a short respawn cooldown. Existing grazing and walk animations remain.

When a player shoots an animal but does not kill it, the server stops its wander tree and asks its navigation agent to **run away from the shooter's position** for up to 4.5 seconds, then restarts its grazing behavior. The frightened state replicates to other clients and quickens the visual gait. On death the animal completes its existing fall animation, then spawns **1–3 wool** (sheep) or **1–3 raw pork** (pig) as real physical, server-owned world drops. These persist through the existing world-drop save and player inventory pickup transaction flow. Their new pixel icons use nearest-neighbor filtering and no texture compression. They are **resources, not placeable blocks**. Crafting with wool and cooking/eating pork will come with the later survival milestone.

Animal loot extends the inventory from item types 1–19 to **1–21** (20 wool, 21 raw pork), keeping every existing building block ID unchanged. Older 19-resource v5 player inventories still load; the two additional item counts start empty. Existing mining, block placement, creative catalog, save files and world-drop records remain compatible. Gameplay mutation (hit, flee, spawn, death, drop, pickup) is server-authoritative. Pure .NET tests check biome rules, flee directions, item validity, quantities and save migration.

**Scope/verification:** The first pass still maintains **one active bounded navigation region centered around the host/exploration focus**, not independent navmesh streaming for multiple widely separated players. The region follows exploration, but this is not world-persistent wildlife simulation. The code, icon assets and tests are committed, but a Linux Play-mode build, two-client multiplayer test, route accuracy over one-block steps and actual loot collection have not been executed here.

## Creative inventory catalog (20 November)

Open the inventory with **Tab**, then choose the **Creative** tab (visible only when the server has Creative Mode enabled for your player). The grid includes all **18 placeable block types** available in the current game: grass, dirt, stone, log, forge, planks, coal, iron ore, leaves, chest, cobblestone, stone bricks, mossy stone, dark timber, glass, red bricks, sandstone and slate. Iron ingots are a crafting resource, not a block, so they are not included.

Click an illustrated block to add **64 items** to your normal inventory. The quantity toggle can switch to **1 item per click**. The card shows how many you own; the server reports whether the grant succeeded. Items stack normally, prefer compatible stacks, and fill empty hotbar/backpack slots. Move blocks from your backpack to the hotbar with the existing slot controls. Creative placement still doesn't consume materials.

The catalog is not a client-side inventory editor: each request goes through an owner-checked, server-authoritative RPC. Survival mode, an open chest, invalid types/quantities, and insufficient physical slot capacity all prevent creative grants. The existing maximum of 999 items per type applies; no grants spill into hidden legacy reserves. Changes sync to clients and save in the existing v5 player profile. Pure .NET inventory tests cover all catalog IDs, stack additions, full inventories, caps and save round-trips.

**Runtime check needed:** verify the catalog looks correct in the engine's .tui editor, images render sharply, clicks update multiplayer inventories, hotbar movement works, and the catalog hides outside Creative Mode. This was committed without a local Tallow player build.

## Camera feel and faster animal deaths (20 November)

The local first-person camera no longer receives position and view-rotation updates solely from `FirstPersonCharacter.Update(TickInput)`, which can run at a different rate from rendering. Predicted player motion and gun aim still use the original network tick. The presentation camera now follows that simulated pose in `LocalLateUpdate` every rendered frame, with frame-rate-independent interpolation and snap handling for spawn/teleport changes. Default camera tuning fields on `FirstPersonCharacter`: `CameraTurnResponse = 48`, `CameraPositionResponse = 75`. Higher response follows the simulated pose more tightly; lower response hides tick steps more aggressively but increases visible latency. This is a game-side smoothing experiment, not a claim that engine-level frame pacing or input sampling was fixed.

Peaceful animal death rolls now complete in about **0.72 seconds**, with the existing short additional despawn hold (~0.18 seconds). Gameplay health, server authority, and navmesh behavior are unchanged. The isolated test project includes camera damping, teleport, rotation, and death-timing checks.

**Test in Linux Play mode:** rotate the mouse slowly and quickly at stable FPS, observe 60 vs 120+ FPS camera feel and aim agreement with the crosshair/guns, move over one-block steps, crouch, teleport to spawn, open/close menus, and compare two network clients. If turning still feels jerky, investigate native mouse sampling or engine tick pacing rather than adding more filtering.

## Peaceful animal animation (prototype)

Sheep and pigs now animate their voxel-model pieces locally as they move: alternate diagonal legs and hooves, subtle body bobbing and tail motion. When the server detects an idle animal, it occasionally sets a synchronized eating state; the muzzle dips toward the grass and chews for a few seconds, then returns upright. The existing behavior tree keeps its navigation/wait loop, with longer idle pauses for grazing. Lethal gun damage stops navigation, briefly flashes the animal, and rolls the model onto its side before replicated despawn. The root CharacterController is never posed, so art doesn't affect collisions or pathfinding. Sheep wool tufts have been reduced in size.

These animations are code-driven in `PeacefulAnimal.cs` with pure math in `AnimalAnimationRules.cs`. They are unverified in a live editor build; multiplayer motion smoothing and the falling pose should be checked in Play mode.

## Peaceful mob gunplay (prototype)

Two test guns can hurt peaceful mobs in Creative mode. Press **Z** for the Spark pistol (18 damage) or **X** for the Pulse repeater (9 damage); fire with **left mouse button**. Shoot the first unobstructed animal in your crosshair. Sheep have 40 HP; pigs have 48 HP. Server-only raycasts apply damage, update the hit-feedback HUD, briefly flash the animal red, and replicate its death. The bounded habitat refills freed slots. Animal CharacterControllers and the habitat NavMesh have 1.05-metre step settings to allow climbing one full voxel block.

Animal prefabs now include additional eyes, ears, snouts, wool, hooves, and tails. Model and hit feedback are still a first pass; verify collision, navmesh baking, and multiplayer in a real Tallow Play-mode session before treating this feature as finished.

## Peaceful animals (prototype)

The main scene includes a small, server-controlled sheep/pig habitat using Tallow's built-in NavigationAgent, BehaviorTreeRunner and CharacterController. A bounded navmesh is created once the immediate terrain meshes exist, and animals use a shared wander/wait behavior tree. This is a first Play-mode integration pass, not a world-streamed mob system; movement/spawn/network behavior still need runtime verification. No engine changes are required.

## Additional building blocks (prototype)

Four new full-cube materials are available in crafting and storage: **glass**, **red bricks**, **sandstone**, and **slate**. Recipes respectively use 2 stone + 1 coal, 4 dirt + 1 coal, 4 stone, and 4 stone + 1 coal (four blocks per craft). The old 4x4 pixel-art atlas is untouched. New masonry is drawn from a separate nearest-filtered 2x2 companion atlas; glass uses a separate genuinely alpha-blended 32px tile, with its own locally streamed collision mesh. Legacy inventory v3/v4 saves are readable; new saves are v5. This feature requires Linux/Windows play-mode testing for rendering, collisions and multiplayer.

## Running

Requires the current Tallow **feature/csharp-gameplay** engine branch (including the raycast-normal and MeshCollider.Refresh physics update). The local engine path in project.tallow is machine-specific; change it if you clone this project elsewhere.

Open `project.tallow` and press Play. **Play solo** opens your local world. For multiplayer, sign into Steam and select **Host on Steam**. Select **Invite Steam friends** in the lobby to send an invitation. Friends can also select **Join a Steam friend** from the title screen or **Join Game** in Steam. Both players should run Tallowcraft before accepting an invitation when using Spacewar (App ID 480). The lobby remains accessible from the pause menu during play.

To package Steam support, place the Steamworks runtime for your platform in `Libraries/` before building. Use `libsteam_api.so` on Linux or `steam_api64.dll` on Windows. Tallow copies it next to the executable during export.

World settings are available on the main and Escape menus. View distance ranges from 4 to 64 chunks ahead, with shorter side and rear ranges. The experimental **Quadtree terrain LOD** toggle switches between the new coarse-distance terrain and the original full-chunk renderer (capped at 20 chunks). The **LOD quality** setting scales the near-to-distant transition boundaries. Chunk threads can use Auto or a fixed count up to the detected maximum. Changes apply immediately and are saved for the local player (including compatibility with older display-settings files). Larger distances can use considerably more memory and CPU. **64 chunks is an experimental cap, not a validated frame-rate guarantee.**

### November 20 follow-up: compass and stair corners

The compass now updates from the local camera on every local UI frame, with 3° marker increments and 15° cardinal-label ticks. The decorative left/right arrows were removed. This is a bounded .tui presentation workaround rather than continuously interpolated CSS positioning; labels only update when they change.

Automatic inner/outer stair corners are derived from adjacent stairs at mesh-generation time. Continuous same-facing stair rows are protected from unintended cuts when a perpendicular stair touches their side. Both mesh generation and placement/mining previews use the same derived occupancy. The world save, multiplayer edits, and item types still store ordinary stair states without persistent corner metadata. These changes include pure C# regression tests but still require a Linux play-mode rendering check.

### November 20 additions: safe return, compass and building materials

**Return to Spawn** now reserves the destination's actual 16³ terrain chunks (including Jolt collision) on the server and preloads the same destination on the owning remote client. Movement, look, mining and item actions are locked while the destination is prepared. A remote client acknowledges readiness only after its chunks and colliders are available; the server then teleports authoritatively. The attempt cancels after a bounded timeout instead of teleporting into a missing chunk. The HUD shows a loading banner during this process. Test this on host and remote client before relying on it for long-distance travel.

The top-of-screen 180-degree compass uses local saved **Home, Building site, Mine and Landmark** waypoint positions. Each has a generated, transparent pixel-art icon. Saved locations update dynamically with camera yaw; unsaved/behind-you markers are hidden. Waypoint saves and their security boundaries remain unchanged.

Four new craftable building blocks (IDs 12–15) use previously unused tiles in the approved 4×4 atlas. Tiles 0–11 were preserved byte-for-byte at the pixel level. All four support existing cube, slab, stair and half-wall shapes, inventory storage and mining. Recipes: **Cobblestone** (2 stone → 2), **Stone bricks** (4 stone → 4), **Mossy stone** (3 cobblestone + 1 leaves → 4), **Dark timber** (2 logs → 4). Existing player-profile inventory strings with 11 reserve fields are accepted and migrated in memory. All materials are craftable rather than newly generated underground.

### Experimental distant-terrain LOD

Tallowcraft now selects a signed X/Z quadtree of 16-metre chunk columns. LOD0 keeps the existing full 16³ voxel geometry, edits, collider rules, underground sections, and authoritative multiplayer state. Outside the near LOD0 footprint, LOD1–3 generate approximately 32m, 64m, and 128m square tiles with **flat, stepped block faces** and exposed vertical cliffs (sampling at 2m, 4m and 8m), directly from deterministic terrain data; they do **not** create and throw away voxel meshes. The same seeded WorldFlora roots generate simplified low-poly oak and pine canopies and trunks, batched into each terrain tile's existing atlas mesh with no separate tree draw calls. Coarse tiles have no physics colliders or replicated GameObjects. Their edges include skirts, and obsolete representations remain until requested replacements are uploaded.

**Current limitations:** Coarse blocks and tree canopies are deliberately stylized approximations rather than exact voxel geometry. Small vegetation, modified individual leaves/branches, overhangs, partial block shapes and underground/cave openings are not fully reproduced at distance. Mining a seeded tree trunk root suppresses the distant tree proxy. Distant tiles incorporate edits already in the local edit snapshot, but remote clients currently request validated edit sections only near their player; edits far outside that request radius may be missing from client-side LODs until they approach and synchronize. Test hosting and late joining carefully before relying on distant edited structures. This feature does not change Tallow Engine's voxel destruction system.

### Validation and benchmarks

The C#-only regression suite includes signed-coordinate quadtree coverage, stable selections, no overlapping LOD leaves, flat block-face geometry, deterministic tree roots and batched tree deletion after mining, edit-driven invalidation and task cancellation. Run it with:

```bash
dotnet run --project tests/BlockLogicTests/BlockLogicTests.csproj -c Release
dotnet run --project tests/BlockLogicTests/BlockLogicTests.csproj -c Release -- --lod-bench
```

The second command prints CPU selection and eight-tile meshing samples at 20, 32, 48 and 64 chunks; it is **not** a gameplay FPS benchmark. In Tallow play mode, enable `BlockWorld.ProfileTerrainFrames` and `ProfileStreaming`, then capture `[Tallowcraft frame profile]` and `[Tallowcraft LOD]` log lines for each distance, both with LOD enabled and disabled. Log output includes average frame time and estimated FPS, maximum sampled frame time, CPU-mesh geometry, batched distant tree counts, estimated CPU array bytes, process RSS if available, job counts and queue-to-upload latency. Draw calls and actual GPU allocation are not exposed by these measurements. Compare identical camera positions, seeds, worlds and settings, and test at least one host plus remote client.

The current repository has **not** been verified by a Linux/Windows in-editor run or measured FPS trial. Review local test output and renderer transitions before claiming a performance gain.

**Peaceful exploration:** Pause with Escape and select **Waypoints and coordinates** to mark up to four personal locations: Home, Building site, Mine and Landmark. Track one location to see its distance and compass direction (N/S/E/W) alongside your XYZ coordinates during play. Markers are checksum-verified, saved atomically in local player data, and separated by world and player identity. They are not server-controlled teleport destinations and are not synchronized between devices. The menu also offers **Return to spawn** with confirmation: the server validates ownership, resolves a safe ground height at the player's original spawn location, and teleports only that player without changing their inventory. This is optional convenience travel, not a health, hunger or death system.

### Controls

- WASD / mouse: move / look
- Space: jump
- Shift: sprint
- Ctrl: crouch
- Left mouse: place the selected block
- **Middle mouse: pick block** under the crosshair. In both modes, an existing matching hotbar stack is selected; a matching backpack stack is swapped into the active hotbar slot. Survival mode never generates materials. In Creative mode, an absent material can be added as one item in a free slot without deleting existing stacks. The selection copies the shape, facing and upper or lower half, including beams. Rotate a copied block with F; crouch while placing to invert its upper/lower half. Switching hotbar slots restores normal relative placement rotation.
- Hold right mouse: mine the targeted block (stone and ores take longer than dirt and leaves). A pixelated, progressively spreading **ten-stage crack overlay** appears on the actual cube/slab/stair/half-wall faces. It resets immediately on release, target changes or breaking.
- **E (Interact):** open the nearest placed chest within 4.5m directly into the Storage UI. E never places blocks; use **left mouse** (or controller North) to place. E does nothing if no chest is nearby.
- 1–6: select the first six physical hotbar slots (not fixed material types); scroll the mouse wheel for all nine slots
- 7, 8, 9: equip a crafted pickaxe, axe or shovel; 0: switch back to hands
- Mouse wheel: cycle selected block
- Q cycles through cube, slab, stair, half-wall, corner post and horizontal beam
- F: rotate the next placed block in 90° increments
- Aim at the upper or lower half of a side face to place a slab or stair at that height. Clicking a top face selects a lower piece, and clicking underneath selects an upper piece. Ctrl flips the previewed half and raises beams. Press Q to resume aim-based placement after copying a block.
- Left-click a lower slab's top (or an upper slab's bottom) with the same slab type selected to combine both halves into one full block
- The new **.tui nine-slot HUD** displays the generated icon for each material, the live quantity, selected slot, building shape/rotation, and equipped tool durability. The C# UI creates one local-only UIDocument per player; all gameplay state stays on the server.
- Escape: open the main/pause menu. **Waypoints and coordinates** lets you mark, track and clear four saved landmarks; the HUD shows XYZ and the tracked landmark's compass direction and distance. **Return to spawn** is available from the pause menu with confirmation, and does not consume or drop items.
- Tab (or gamepad West): open/close the **36-slot inventory** (9 hotbar + 27 backpack), crafting and forge pages. Aim at a placed chest within 4.5m and press E to open its 27-slot inventory; click a source stack then a destination to transfer it. Toggle Move half to transfer half a stack. Escape (or gamepad East) also closes it. While it is open, gameplay input—including mouse look—is disabled instead of merely freezing the camera, so closing the screen does not snap to an accumulated mouse angle. The on-screen Close button works too.
- Craft stone tools in the Crafting page with stone and planks: pickaxe (3 stone + 2 planks), axe (2 stone + 2 planks), or shovel (1 stone + 2 planks). Click Equip to select one, or Use hands to unequip. Recraft a damaged tool to repair it.
- **Forge progression:** place a Forge block (hotbar slot 5), stand within 3.5 m of it, press Tab, then select **Forge page**. Smelt 1 iron ore + 1 coal into 1 iron ingot, or smelt batches of five. Use the ingots and planks to craft an iron pickaxe (3 ingots), axe (2), or shovel (1); each costs 2 planks. Iron tools mine their specialty blocks faster and last longer. The server checks proximity to the actual forge for every smelt/iron-craft request.
- Use **7/8/9** for pickaxe/axe/shovel; those keys automatically select the iron version if you own it, otherwise the stone version. Use **0** for bare hands. Iron ingots are inventory items, not placeable voxel blocks.
- Pickaxes mine stone, ores and forges faster; axes mine logs and planks faster; shovels mine dirt and grass faster. Each successfully mined block consumes one durability point. A broken tool automatically unequips. Iron tools have higher durability and a 5.5× effective-mining speed multiplier versus stone's 3×.
- Mining a block now spawns a **small floating item entity** with gravity. The server checks actual player distance before collecting it into an inventory stack. Nearby identical drops merge (up to 64), drops expire after five minutes, and at most 256 may exist per scene. If spawning fails at the limit, the server credits the item directly rather than losing it. Coal, iron ore and leaves can be moved into any hotbar slot and placed if their voxel types support placement. Stack limit is 64 per slot; click a stack and then a destination to move or swap, or enable Move half to split a stack in half.
- `BlockEditor.CreativeMode` is **enabled by default** to preserve unrestricted test-building. Disable it on the player prefab's BlockEditor component for survival building: the player starts with a small supply of each of the six hotbar materials, and successful placements consume one material. Empty slots cannot place blocks; mining replenishes stock. Mined items use physical server-owned drop entities.
- The red outline marks the mining target. A translucent mint preview shows where left-click will place your selected shape. Amber means placement is blocked.

Blocks can only be edited within 6 metres, and placement is refused when another physics body occupies the destination.

## Architecture

- **Streaming:** directional visible chunks around the local player's camera, plus physical chunks around **every player on the server**.
- **Mesh:** 16×16×16 blocks, 0.5-metre occupancy for partial shapes, deterministic terrain, and greedy merging of compatible half-cell faces within each block. Atlas UV seams deliberately prevent merging textured quads across entire blocks; this is an explicit performance tradeoff pending a tiling shader. Cube-only chunks now take a one-metre meshing fast path (8× fewer occupancy cells); chunks with slabs/stairs/half-walls or neighboring partial blocks use the original half-metre path to preserve edge geometry. Neighboring chunk faces consult edits across boundaries.
- **Character:** The block explorer has separate head, torso, upper and lower limb meshes. Its procedural pose follows idling, walking, sprinting, crouching, jumping, mining and test-weapon recoil. Your own character casts an animated shadow in first person. Other players see the character and equipped item.
- **Building UX:** on-screen local-player hotbar driven by authoritative network `TickInput` (`Custom1..8`, Ability1 and Ability2). The project includes an `Input.tinput` map for number keys and mouse wheel. Mouse-middle pick-block is a local input event carried by an owner-authorized RPC; the server raycasts against authoritative voxels within the normal interaction reach, selects/moves a real stack (or safely grants a single Creative item), and syncs exact block shape/absolute facing/upper-half to the preview. Pick feedback is visible on the navigation HUD. Copied absolute orientation is session-local (not yet persisted as a player-profile mode), while the existing selected shape/facing continue to save normally.
- **Breaking overlay:** `BuildPreview` draws one local-only, collider-free `MeshRenderer` around only the mined block, with `BlockDamageMesh` generating outward partial-shape faces using the same 2×2×2 occupancy as the world mesher. Its cutout material uses `Shaders/block_break_damage.tsgraph` (editable Shader Graph), a generated `.tshader` and a 160×16 lossless crack-stage atlas. Server-authoritative, quantized `MiningPercent` and synced mining target select the crack stage. A target change rebuilds only this tiny overlay mesh, not a terrain chunk. No extra networked crack objects or engine changes.
- **Inventory and tools:** authoritative per-player 9-slot hotbar + 27-slot backpack `BlockInventory` with 64-item physical stacks of nine voxel types and iron ingots, and `PlayerTools` with three stone and three iron tools. Validated/versioned `[Sync] InventoryState` and `[Sync] ToolState` replicate quantity, equipped tool and durability. Server-only RPCs validate crafting/equip commands. Tool durability is spent only on a successful server-authoritative block break. The server persists inventory, equipped tool/durability and selected block/shape/facing per world and per persistent player token. Current inventory tracks materials rather than individual oriented or partial shapes.
- **Block states:** packed 16-bit type + facing + shape + upper flag. Types: grass, dirt, stone, wood, forge, planks. Shapes include cube, slab, stair, half-wall, corner post and horizontal beam. Rotations affect stairs, half-walls, and the forge's front texture.
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

### Block character and equipment

The explorer, all six mining tools and the two test weapons are built from small cuboids. Each tool and weapon exports as one GLB mesh with one material. The explorer's articulated parts have independent pivots. Run `python3 tools/generate_block_models.py` to rebuild the models.

In Creative mode, **Z** equips the **Spark pistol**, which fires one pulse per left click. **X** equips the **Pulse repeater**, which fires while left click is held. Press the same key again to holster, or press **0** to return to hands. The test weapons play recoil, sound, muzzle flashes and contact sparks. Pulses are cosmetic.

### Texture import requirements

This atlas uses the per-picture import controls from the current Tallow `feature/csharp-gameplay` branch. If a local engine revision predates those controls, the project PNG is still lossless but can look blurry because the older renderer applies smooth/anisotropic filtering. Pull the updated engine branch before testing. In Tallow's texture Inspector, select `Textures/block_atlas.png` to see Nearest, Clamp, no mipmaps, no compressed copy and Keep full resolution. Close and reopen the project or reload the texture if the old atlas remains cached.

### Targeting preview

The translucent mesh follows the server's placement rules. It shows the result of combining two matching slabs, turning a stair corner, rotating a corner post and flipping a beam into the upper half of a block. The preview follows the most recently synchronized selection.

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
prefab load prevents a spawn, a journaled direct-inventory reward is used,
subject to the existing per-type 999-item cap. If the player has no capacity,
the mined block is restored instead of silently consuming its resources.

**Ground drop persistence:** active server-owned drops save with the same
atomic world generation as voxel edits and chest contents. New saves use
incremental world-save v6 (older v3/v4/v5 saves are still readable).
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

**Crash-recoverable chest transfers:** moving items between the player
inventory and an opened chest now uses the same durable redo mechanism.
The server first stages both inventories, then journals the full post-transfer
player profile plus just the affected chest record. Restart recovery applies
that chest record to the saved world, even if the final withdrawal emptied
the chest, and replays the player profile exactly once. Transfers are not
accepted if the journal or either save fails; the existing recovery evidence
is preserved. Moves between slots within a single chest stay world-only.

**Crash-recoverable mining fallback:** if a physical drop cannot spawn,
the server stages a direct reward and tool wear. It checkpoints the immutable
world snapshot captured *before* mining, then records a v3 redo receipt with
the exact removed voxel and post-reward player profile. If the server crashes
after journaling but before both saves finish, recovery replays the block
removal and grants the materials and tool durability exactly once. If the
player cannot receive the reward, or a save fails before a receipt exists,
the block and tool durability are restored in the same tick. A failure after
a receipt exists locks world writes until restart recovery.

**Incremental world saves:** v6 stores only modified voxel sections,
not fully generated terrain. Each edited 16x16x16 section is encoded into
an immutable, SHA-256-addressed blob. A new committed generation writes a
small manifest referencing unchanged section blobs and stores new blobs
only for changed sections/chests/ground drops. Existing world seeds still
regenerate untouched terrain; meshes and colliders are reconstructed
on demand. The usual current/previous generation pointers and checksummed
rollback remain. The first v6 save migrates an older v3–v5 world, without
discarding its previous save. Orphan blobs are cleaned conservatively
after groups of checkpoints, never while unfinished staging exists.

**Known limits:** physical pickups, chest transfers and direct-credit
fallback still synchronously write a full world *manifest* and player
profile, which may hitch on worlds with very many edited sections
(the mining fallback also checkpoints the pre-edit manifest). Their
unchanged voxel data is no longer duplicated on disk. Blob checksums are
validated when loading, but sharing immutable blobs means that corruption
of a blob referenced by both generations can affect both rollback copies.
Linux/Windows host/client tests and disk-failure injection have not been
run. Other inventory operations (crafting, smelting, placement spending
and tool wear) and authenticated multiplayer accounts are not covered by
these redo transactions.

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
* Crafting and forge tabs in the backpack, with a separate storage view for each targeted chest (27 slots). Content is synchronized with the server, not stored
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

Aim at a chest within **4.5m** and press **E**. The server opens the first chest under the crosshair and the storage panel shows its coordinates.

To store a stack, click any backpack/hotbar stack and click **Store
selected**. To withdraw, click an occupied chest slot and choose **Take
selected**. You can still transfer into a specific slot by clicking the
source and then the desired destination. Use **Move half** before either
action to split a stack. When the receiving slot is nearly full, taking
from a chest transfers only the amount that fits; any remainder stays
in the chest. The per-material inventory cap still applies. All transfers remain server-authoritative,
share chest state between players, and use the existing persisted world
storage; the UI cannot create or duplicate items.

If nothing moves, read the status line at the top of the chest inventory.
It now reports out-of-range chests, incompatible/full slots and failed
server-authorized transfers instead of silently ignoring them.
