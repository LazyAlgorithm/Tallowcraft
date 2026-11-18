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
- Right mouse: remove the targeted block
- E (Use): alternate place control
- 1–6: select grass, dirt, stone, log, forge, planks
- Mouse wheel: cycle selected block
- Q: cycle cube, slab, stair and half-wall shapes
- F: rotate the next placed block in 90° increments
- Ctrl while placing: place an upper slab or upside-down stair
- On-screen hotbar displays selection, shape, and rotation. Controls are networked with `Input.tinput`.
- Red wireframe shows the voxel that right-click will remove; green wireframe previews the chosen block shape/orientation on left-click. Orange indicates blocked placement. Previews are client-local and gameplay edits remain server-authoritative.

Blocks can only be edited within 6 metres, and placement is refused when another physics body occupies the destination.

## Architecture

- **Streaming:** directional visible chunks around the local player's camera, plus physical chunks around **every player on the server**.
- **Mesh:** 16×16×16 blocks, 0.5-metre occupancy for partial shapes, deterministic terrain, and greedy merging of compatible half-cell faces within each block. Atlas UV seams deliberately prevent merging textured quads across entire blocks; this is an explicit performance tradeoff pending a tiling shader. Neighboring chunk faces consult edits across boundaries.
- **Character:** the network-spawned player has an invisible Jolt capsule controller, an original smooth low-poly **skinned glTF 2.0 GLB humanoid** in `Models/Adventurer.glb`, an Animator on its visual child, and local pose selection (Idle/Walk/Run/Jump). The same animated presentation is calculated on each client from its locally observed player movement. See `src/Models/ASSET_LICENSE.md`.
- **Building UX:** on-screen local-player hotbar driven by authoritative network `TickInput` (`Custom1..8`, Ability1 and Ability2). The project includes an `Input.tinput` map for number keys and mouse wheel.
- **Block states:** packed 16-bit type + facing + shape + upper flag. Types: grass, dirt, stone, wood, forge, planks. Shapes: cube, slab, stair, half-wall. Rotations affect stairs, half-walls, and the forge's front texture.
- **Textures:** a real committed `src/Textures/block_atlas.png` (54×54 RGBA), with nine hand-designed 16×16 pixel-art tiles and duplicate-color gutters; Tallow imports it as a shared texture, not generated every frame. Greedy meshes include UVs; renderers share one atlas.
- **Physics:** nearby static Jolt MeshColliders. Editing rebuilds the affected chunk's mesh and calls MeshCollider.Refresh(); chunk-edge edits rebuild the affected neighbor as well.
- **Network authority:** only server-side player TickInput can edit blocks. The player is a networked prefab; procedural chunk GameObjects remain transient and local to each machine.
- **Sync and joining:** the shared BlockWorld has a NetworkIdentity with Relevance=Everyone; its [Sync] EditState carries the world seed and the server's sparse, version-2 block-state snapshot. Clients wait for this snapshot before generating and reject invalid payloads. Worker jobs use immutable edit snapshots; stale completed jobs are discarded.
- **WorldGeneration:** a separate C# assembly definition with one primary type per source file.

## Prototype limitations

**Edits survive chunk unload/reload and late joining, but are not yet saved to disk or across server restarts.** The edit snapshot is deliberately limited to **512 differing blocks** (20,000-character upper bound). It sends the entire current edit state whenever a block changes; use reliable per-chunk snapshots and deltas before scaling to large or persistent multiplayer worlds.

Supported block types are 0 (air), 1 (grass), 2 (dirt), 3 (stone), 4 (wood), 5 (forge), and 6 (planks). The vertical world currently spans Y=0..15, and chunk collision/rendering on a dedicated server has not yet been separated into headless-only physics chunks. Half-cell meshing is more expensive than the previous full-cube greedy mesher; profiling is required before increasing render radius. Moving/removing a block requires its chunk collider to have been generated first.

### Rigged-character caveats

The first-person player's **own** rigged body is rendered as shadow-only on their machine: its own rapidly animated shadows are suppressed locally, so it no longer covers the camera or flickers across the terrain. Remote clients still render the entire animated character and its shadows. Animation playback is deliberately slowed to 0.55–0.65× and velocity changes are smoothed to prevent rapid pose flicker. Ownership and local visibility are checked in `FirstPersonCharacter.LocalUpdate` without disabling the player or its collider. This needs a host/client runtime check, especially when ownership arrives after spawning.

The player model is a **real glTF skinned mesh with 11 named skeleton joints and skin weights**, not a Minecraft-style block character. The asset is authored in this repository. Its Idle/Walk/Run/Jump clips are editable `.tanim` files based on Tallow's sample animation tracks. This is a **first integration pass**: verify skinning, animation ordering, network interpolation, camera/head occlusion, crouch, and feet placement in Play mode. No passing Tallow build has been recorded for this commit. A local-player-specific head visibility mask is still needed if the model occludes the first-person camera.

### Targeting preview

The red target outline and shape-specific green placement outline use the same ray-to-block targeting method as the authoritative server. Some partial blocks show internal half-cell grid lines in the initial wireframe; these are previews only, not additional colliders. Actual blocks and collision remain unchanged. In a multiplayer client, selection previews are based on the latest replicated selection state and may briefly lag a fast hotbar key press. Verify the outlines against server placement on vertical faces, chunk seams and partial shapes.

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
