# Tallowcraft Development Roadmap

Tallowcraft is the gameplay and integration testbed for Tallow's `feature/csharp-gameplay` engine branch. All Tallowcraft implementation commits go directly to **main**; do not create feature branches. A merged or committed source change is **not** the same as a successful Linux/Windows build or in-editor test.

## Foundation — existing prototype

- [x] Deterministic 16×16×16 voxel chunks, directional streaming, Jolt chunk colliders
- [x] Greedy-meshed full and partial block faces, committed pixel-art PNG texture atlas
- [x] Block states: grass/dirt/stone/log/forge/planks; horizontal facing; cube/slab/stair/half wall
- [x] Authoritative break/place requests and bounded v2 edit-state replication for late joiners
- [x] Corrected side-face grass UV orientation (tested visually in Tallow)
- [ ] Verify all block-shape collision, orientation, seam and client-join combinations

## M1 — Rigged player and movement presentation (next)

**Goal:** replace the *visible* capsule with a conventionally modeled, skinned humanoid, **not** a block-character.

- [x] Replace the procedural character in the player prefab with the CC0 Quaternius Universal Base Characters model (Superhero Male from Standard) and document asset provenance
- [x] Keep the Jolt `CharacterController` capsule **invisible** as the collision proxy
- [x] Add rigged model to the player prefab as a visual child at estimated feet offset (needs Play-mode alignment check)
- [x] Integrate CC0 Quaternius Universal Animation Library Idle/Walk/Sprint/Jump via SkinnedMeshRenderer clipLibrary and remove legacy Animator component from the player (runtime test pending)
- [ ] Confirm actual remote-client model/pose and camera behavior in two-instance Play-mode testing
- [x] Apply owner-only `MeshRenderer.ShadowOnly`, while restoring shadow casting for local and remote player models (runtime test pending)
- [ ] Verify head/body occlusion, crouching and local ownership transfer in Play mode
- [x] Retain smoothed velocity-based clip selection with the actual Quaternius clips close to native playback speed (runtime check pending)
- [ ] Host + client test, walking/sprinting/jumping/crouching; verify no extra collider

**Exit:** no visible capsule, animated rigged player visible to other clients, no regression in movement.

## M2 — Selectable hotbar and building UX

- [x] Display 6 block types with selected slot, block-name, shape and rotation label (immediate-mode prototype HUD; icons later)
- [x] Bind hotbar selection to network-aware input (1–6, mouse wheel via Custom1..8) and validate chosen state on server
- [x] Shape selector: cube, slab, stair, half-wall (Q), upper/lower via crouch held while placing
- [x] Same-material slab stacking, shape-aware collision checks, and the same placement planning for client preview and server (runtime check pending)
- [ ] Verify collision-aware placement beside the player, against rotated stairs and at chunk seams (in-game check pending)
- [ ] Add clickable facing preview, fine-grained subcell replacement and additional shape states
- [x] Swap mouse buttons: left-click place / right-click remove (E still places)
- [x] Integrate approved 3×3 atlas (32px tiles, 2px extruded gutters), texel-center UVs, per-texture point sampling, no mipmaps/compression, full resolution (runtime check pending)
- [x] Crosshair and red remove / green placement outlines, including half-cell shapes (runtime test pending)
- [ ] Add a more polished transparent ghost mesh and block-face-aware partial shape placement; lightweight first-person hand/tool later
- [ ] Host/client: confirm correct type, facing, shape and selection per owner; late join sees same blocks

**Exit:** block selection is usable without editing Inspector fields.

## M3 — Correctness and performance of partial blocks

- [x] Pure C# regression tests for all slab/stair/half-wall microcell occupancies and slab-to-cube placement decisions
- [x] Add pure C# reference-surface tests for slab/stair/half-wall orientations, upper/lower variants and positive/negative chunk seams (execution pending)
- [ ] In-editor physics/collider and raycast regression tests across all orientations
- [x] Support slab stacking on exposed upper/lower faces without allocating an extra voxel (runtime check pending)
- [ ] Improve placement on internal faces of stairs and slabs, including precise hit-height rules
- [ ] Split/merge compatible shapes and occlusion correctly across chunk boundaries
- [x] Fast one-metre meshing path for cube-only chunks with half-metre fallback around partial blocks and chunk boundaries (runtime benchmark pending)
- [x] Implement hybrid per-block face meshing: cube faces stay whole, partial blocks use local half-cell faces, with atlas UVs and identical render/collider mesh (runtime benchmarking pending)
- [ ] Extend hybrid path with shared material batching and cross-block greedy merging after world-tiled atlas UV support
- [ ] Proper per-block repeating texture UVs on merged faces; avoid atlas bleed
- [x] Add repeatable pure C# benchmark for generated chunks/sec, vertices/chunk, triangles/chunk, and managed allocations (run with `dotnet run --project tests/BlockLogicTests -c Release -- --bench`; results pending)
- [x] Add optional BlockWorld synchronous profiling for SetMesh, collider add/refresh/remove (live runtime data pending; does not measure deferred Jolt cooking)
- [ ] Measure deferred Jolt physics cooking, GPU draw calls, runtime memory and player-facing frame times in Tallow
- [ ] Retain Tallow native ECS and physics integration; avoid GameObject-per-block

**Exit:** performance meets or beats the original cube prototype on ordinary terrain.

## M4 — Bigger world and navigation

- [x] Add signed (X,Y,Z) 16³ chunk keys, vertical streaming, Y=-32..47 block edits and cross-layer occlusion (Play-mode verification pending)
- [x] Calculate server-authoritative player spawn clearance from voxel surface, with initial spawn points adjusted (runtime verification pending)
- [ ] Expand vertical world bounds and stream/simulate deeper caves based on world settings
- [ ] Biomes, vegetation, ore layers, caves and simple structures
- [x] Add negative-coordinate chunk ownership and neighboring partial-shape seam regression tests (runtime confirmation pending)
- [ ] Verify world-space generation across negative coordinates and chunk seams in Play mode
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

- The world uses signed 16-block vertical layers (currently Y=-32..47); terrain below Y=0 is initially stone, and positive layers start as air until edited.
- The current edit channel is one 20,000-character / 512-edit snapshot, not scalable chunk deltas.
- Unedited cube-only chunks now use full-block meshing; edited chunks containing partial shapes still use half-cell meshing until the hybrid path is complete.
- The HUD is a text hotbar (no item icons/inventory counts); authoritative selection now uses the project's explicit `Input.tinput` bindings.
- Rigged character integration and its runtime tests are **not done** until verified.


## M3 benchmark procedure

From the Tallowcraft repository root:

```bash
dotnet run --project tests/BlockLogicTests -c Release
dotnet run --project tests/BlockLogicTests -c Release -- --bench
```

The benchmark reports CSV-compatible CPU chunk generation throughput, mesh vertex/triangle counts and managed allocation per chunk for cube-only and edited partial-shape workloads. It warms up the JIT before timing, checks negative-coordinate chunks, and does not claim to measure native MeshCollider reconstruction, GPU rendering or full-game frame rates. Run on the same hardware and .NET runtime when comparing commits. Tests added for negative X chunk seams and partial-block neighbor meshing; engine-level collider/raycast validation remains open.

## Hybrid mesher validation

The mixed-shape benchmark now exercises `HybridChunkMesher` instead of the chunk-wide half-cell sweep. `HybridMeshTests` compares the visible triangle surface area and winding against independently enumerated half-cell occupancy for rotated slabs, stairs, walls and seam cases. These tests have been committed but have **not** been executed in this environment. Run the two benchmark/test commands above on Nobara and compare mixed-shape throughput to the prior 297.6 chunks/sec (3.360 ms/chunk). The untouched cube-only baseline was 2,385.4 chunks/sec (0.419 ms/chunk). Inspect in-game collision and face visibility before treating this as production-verified.

## M3 stress benchmark and collider profiling (Nobara)

Run from the Tallowcraft repository root:

```bash
git pull --ff-only origin main
dotnet run --project tests/BlockLogicTests -c Release -- --bench
```

Benchmarks now include four cases: `cube` (no partial edits), `mixed-shapes` (three sparse edits), `dense-partials` (roughly 512 partial blocks spread over an eight-block-high volume), and `all-partials` (2,048 partial blocks). The last two use fewer samples (8 and 4 chunks respectively) to keep runs manageable. Compare cube and mixed-shapes against the prior Nobara measurements of 0.372 ms/chunk and 0.135 ms/chunk. All results are *CPU mesh generation only*, with no MeshCollider or graphics timing.

For live profiling, build/open Tallowcraft in Tallow, select the object with `WorldGeneration.BlockWorld`, and leave `ProfileChunkColliders` enabled (default: true). This prints `[Tallowcraft chunk profile]` once per 32 instrumented operations by default (`ProfileReportEvery` is configurable). Each report includes count, average, max, and total synchronous milliseconds for `SetMesh`, `ColliderAdd`, `ColliderRefresh`, and `ColliderRemove`. Walk away from the spawn to stream chunks, and place/remove blocks near loaded chunks to trigger collider refreshes. Log output from this running-engine test, not the standalone `dotnet run` test, is needed to assess the collider path.

**Interpretation:** The profiler measures time spent synchronously inside managed-to-native API calls, including any immediate native work. If Jolt builds/updates a collision shape later during the physics step, that deferred time is **not** counted. Neither render-thread GPU work nor full-frame costs are measured here. To claim full physics/rebuild cost, use Tallow's native profiler or instrument the engine physics step separately. These project-side diagnostics do not modify the engine.

## M4 vertical chunk and player spawn validation

The first vertical-stack implementation uses signed `ChunkKey(X,Y,Z)` with 16×16×16 blocks per chunk, and currently permits world block heights from Y=-32 through Y=47 (chunk layers -2 through +2). Vertical streaming defaults to one layer above/below the player's current layer, while nearby server-side remote-player colliders are considered separately. Upper empty chunks without edits are not scheduled; editing them creates the chunk. Chunks at Y=-1 initially contain stone, and Y=1/2 initially contain air. The original heightmap still produces the familiar surface at around Y=1..15; caves, ores, improved heightmaps, and persistence are follow-ups.

The authoritative first-person player aligns its Jolt capsule center above the highest occupied block within the spawn capsule footprint, using `ChunkGenerator.SpawnHeight` and current block edits. The four scene SpawnPoint positions have also been adjusted to initial surface heights; the calculation is still performed at runtime so changes to the seed or edits can affect spawn placement. The character's controller and animation remain otherwise unchanged.

Test on Nobara:

```bash
cd ~/TallowProjects/Tallowcraft
git pull --ff-only origin main
dotnet run --project tests/BlockLogicTests -c Release -- --bench
```

Then open the game in Tallow. Check that the player spawns with feet on terrain (not buried or floating), mine down through Y=0 into a negative-Y chunk, and build above Y=15 into Y=16. At both seams, test stairs/slabs and edited chunk refreshes, then test multiplayer spawn points. The unit tests do not replace in-editor physics, streaming, or host/client verification. A committed code change alone is not a successful engine build.
