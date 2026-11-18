# Tallowcraft Development Roadmap

Tallowcraft is the gameplay and integration testbed for Tallow's `feature/csharp-gameplay` engine branch. All Tallowcraft implementation commits go directly to **main**; do not create feature branches. A merged or committed source change is **not** the same as a successful Linux/Windows build or in-editor test.

## Foundation — existing prototype

- [x] Deterministic 16×16×16 voxel chunks, directional streaming, Jolt chunk colliders
- [x] Greedy-meshed full and partial block faces, procedural pixel-art texture atlas
- [x] Block states: grass/dirt/stone/log/forge/planks; horizontal facing; cube/slab/stair/half wall
- [x] Authoritative break/place requests and bounded v2 edit-state replication for late joiners
- [x] Corrected side-face grass UV orientation (tested visually in Tallow)
- [ ] Verify all block-shape collision, orientation, seam and client-join combinations

## M1 — Rigged player and movement presentation (next)

**Goal:** replace the *visible* capsule with a conventionally modeled, skinned humanoid, **not** a block-character.

- [ ] Import a redistributable, genuinely rigged (bones/skin weights) glTF/GLB model; preserve attribution/license
- [ ] Keep the Jolt `CharacterController` capsule **invisible** as the collision proxy
- [ ] Instantiate the model as a visual child of the player, at sensible scale/feet offset
- [ ] Map Idle/Walk/Run/Jump on Tallow's `Animator` or imported model clips
- [ ] Remote clients see model pose/movement; owning first-person camera is never taken over
- [ ] Prevent the local camera from seeing/clipping into its own head; verify crouching
- [ ] Host + client test, walking/sprinting/jumping/crouching; verify no extra collider

**Exit:** no visible capsule, animated rigged player visible to other clients, no regression in movement.

## M2 — Selectable hotbar and building UX

- [x] Display 6 block types with selected slot, block-name, shape and rotation label (immediate-mode prototype HUD; icons later)
- [x] Bind hotbar selection to network-aware input (1–6, mouse wheel via Custom1..8) and validate chosen state on server
- [x] Shape selector: cube, slab, stair, half-wall (Q), upper/lower via crouch held while placing
- [ ] Facing preview/rotation; prevent placing inside player and occupied subcells
- [ ] Crosshair, outline and reach indicator; lightweight first-person hand/tool later
- [ ] Host/client: confirm correct type, facing, shape and selection per owner; late join sees same blocks

**Exit:** block selection is usable without editing Inspector fields.

## M3 — Correctness and performance of partial blocks

- [ ] Ground truth tests for microcell occupancy, slab/stair/half-wall geometry, collision and raycasts
- [ ] Fix partial-face placement (especially clicking internal slab/stair faces)
- [ ] Split/merge compatible shapes and occlusion correctly across chunk boundaries
- [ ] Hybrid mesh: full-cube greedy quads + compact shape templates; no 2× subdivision for all ordinary blocks
- [ ] Proper per-block repeating texture UVs on merged faces; avoid atlas bleed
- [ ] Benchmark chunks generated/sec, vertices/chunk, MeshCollider rebuild cost, memory and draw calls
- [ ] Retain Tallow native ECS and physics integration; avoid GameObject-per-block

**Exit:** performance meets or beats the original cube prototype on ordinary terrain.

## M4 — Bigger world and navigation

- [ ] Vertical chunk stacks and safe Y coordinates outside 0–15
- [ ] Biomes, vegetation, ore layers, caves and simple structures
- [ ] World-space generation across negative coordinates and chunk seams
- [ ] Chunk-generation priority, cancellation and far-distance representation
- [ ] Verify headless server physics streaming separately from client rendering

## M5 — Persistence and scalable multiplayer

- [ ] Seeded worlds, world settings, and save/load edited chunk states across server restarts
- [ ] Bounded, versioned chunk snapshots and ordered deltas (replace 512-edit whole-world string)
- [ ] Ownership/permissions, block-interaction validation and late-join reconstruction
- [ ] Automated multiplayer test matrix (two clients, churn, prediction/replay, reconnect)
- [ ] Dedicated-server bandwidth, CPU and stability profiling

## M6 — Survival/crafting vertical slice

- [ ] Inventory stacks, hotbar quantities and drop/pickup interactions
- [ ] Crafting recipes, forge with sparse block-entity data, and basic tools
- [ ] Health/damage, simple wildlife and AI, weather/day–night polish

## Verification and release gates

- **Code review:** validate API names against Tallow's current C# branch and keep assembly definitions/metadata consistent
- **Build:** Linux C# build using the user's Tallow engine branch; Windows follow-up when available
- **Gameplay:** manual Play mode test for movement, editing, placing all shape variants, atlas, chunk seams
- **Networking:** host/client + late join + distant players + saturated edits
- **Performance:** record CPU/physics/meshing and GPU timing before expanding chunk radii

### Known prototype limitations

- The current world is only one 16-block vertical layer.
- The current edit channel is one 20,000-character / 512-edit snapshot, not scalable chunk deltas.
- Half-cell greedy meshing is temporary; full cubes need a more efficient hybrid path.
- The HUD is a text hotbar (no item icons/inventory counts); authoritative selection now uses the project's explicit `Input.tinput` bindings.
- Rigged character integration and its runtime tests are **not done** until verified.
