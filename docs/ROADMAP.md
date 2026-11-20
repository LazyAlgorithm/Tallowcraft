# Tallowcraft Development Roadmap

Tallowcraft is the gameplay and integration testbed for Tallow's `feature/csharp-gameplay` engine branch. All Tallowcraft implementation commits go directly to **main**; do not create feature branches. A merged or committed source change is **not** the same as a successful Linux/Windows build or in-editor test.

## Progress update — 19 November 2026

The milestones below track shipped repository code separately from engine
Play-mode verification. Since the early roadmap entries, the game has gained
server-persisted edited sections and chest contents (world-save v4), player
profiles, a 36-slot inventory, physical pickups, tools, iron smelting and a
.TUI interface. Chest withdrawals now accept the available capacity of
matching player stacks instead of failing an otherwise valid partial merge.
Regression coverage was added, but this change has not been compiled or
Play-tested on Linux or Windows in this session.

**Current implementation:** physical item pickups now use a durable, checksummed
redo journal. A pre-pickup world checkpoint is committed if terrain/chests are
dirty. The journal records complete post-pickup drop and player-profile states
before updating either file. On restart, an interrupted transaction replays
both states and is removed only after both writes succeed. Corrupt receipts
halt loading instead of being discarded.

**Next implementation priority:** benchmark the synchronous pickup checkpoint
path, validate host/client crash recovery in Play mode, and extend transactional
consistency to chest transfers and the direct-credit mining fallback. Full
public multiplayer account authentication is still out of scope. **Still blocked on engine verification:** character
animation/occlusion, collision accuracy for partial blocks, and sustained
high-radius performance.

## Foundation — existing prototype

- [x] Deterministic 16×16×16 voxel chunks, directional streaming, Jolt chunk colliders
- [x] Greedy-meshed full and partial block faces, committed pixel-art PNG texture atlas
- [x] Block states: grass/dirt/stone/log/forge/planks; horizontal facing; cube/slab/stair/half wall
- [x] Authoritative break/place requests and bounded v2 edit-state replication for late joiners
- [x] Corrected side-face grass UV orientation (tested visually in Tallow)
- [ ] Verify all block-shape collision, orientation, seam and client-join combinations

## M1 — Rigged player and movement presentation

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
- [x] Seeded, world-coordinate 3D cave field and coal/iron deposits below ground (C# regression tests committed; Play-mode test pending)
- [x] Dedicated coal/iron ore texture tiles integrated in the 4×4 atlas
- [ ] Mining drops and inventory (ore blocks currently use the existing edit system)
- [x] Seeded Meadow/Woodland/Drylands/Highlands climate classification and smoothly varied surface heights (automated regression tests passed on Nobara)
- [x] Discover naturally elevated chunks above Y=15 using the authoritative world seed, with central spawn terrain preserved
- [x] Deterministic seeded oak-like trees with chunk-independent trunks and leaf canopies (user confirmed Play-mode testing)
- [x] 4x4 pixel atlas preserving original nine tiles with new coal, iron and leafy foliage tiles; nearest filtering, no mipmaps/compression
- [x] Tall tapered pine trees in highlands and woodlands using the existing logs/leaves
- [x] Biome-dependent voxel shrubs and small abandoned stone ruins generated from world seed (Play-mode verification pending)
- [ ] Flowers, thin grass sprites, extra biome textures and inventory drops
- [x] Add negative-coordinate chunk ownership and neighboring partial-shape seam regression tests (runtime confirmation pending)
- [ ] Verify world-space generation across negative coordinates and chunk seams in Play mode
- [x] Prioritize chunk generation by distance and vertical proximity, cancel obsolete worker jobs, and skip sealed underground sections for surface viewers (runtime validation pending)
- [ ] Exact camera-frustum and occlusion culling, far-distance terrain representation and vertical column metadata cache
- [ ] Verify headless server physics streaming separately from client rendering

## M5 — Persistence and scalable multiplayer

- [x] Persist world identity, seed, sparse block edits and chest contents atomically, with previous-generation recovery (world save v4)
- [ ] World creation/settings UI and configurable vertical bounds
- [x] Bounded per-section snapshots, revision/hash validation, on-demand pages and ordered block deltas (no global 512-edit cap)
- [ ] True per-recipient transport filtering (the current C# RPC bridge broadcasts replies with recipient IDs)
- [x] Server-side block-interaction and chest proximity validation, plus late-join section reconstruction
- [ ] Authenticated player identities and chest ownership/permissions for untrusted public servers (current profile token is a prototype bearer identity)
- [ ] Automated multiplayer test matrix (two clients, churn, prediction/replay, reconnect)
- [ ] Dedicated-server bandwidth, CPU and stability profiling

## M6 — Survival/crafting vertical slice

- [x] 36 physical inventory slots (9 hotbar + 27 backpack), 64-stack limits, count validation and saved player profiles
- [x] Server-authoritative placement costs and mining rewards, with optional creative/survival building toggle
- [x] Physical item drops, pickup interactions, stack splitting and .tui crafting/inventory UI
- [x] Crafting recipes, proximity-validated forge smelting, stone/iron tools, and 27-slot chests saved with the world
- [x] Partial chest withdrawals into matching, nearly-full player stacks without losing remainder (pure-C# regression added; runtime verification pending)
- [x] Persist live ground drops with terrain/chest world-save v5, including age/position and corrupt-generation rollback (regression tests added; runtime testing pending)
- [x] Redo-log physical drop pickups across world/player saves; recover both post-pickup states after abrupt crashes (unit tests added; Play-mode verification pending)
- [ ] Batch pickup journal/checkpoint writes without degrading large-world performance; current transactional path saves the world synchronously
- [ ] Extend equivalent crash safety to chest-to-player transfers, direct-credit mining fallback, and other inventory mutations
- [ ] Health/damage, simple wildlife and AI, weather/day–night polish

## Verification and release gates

- **Code review:** validate API names against Tallow's current C# branch and keep assembly definitions/metadata consistent
- **Build:** Linux C# build using the user's Tallow engine branch; Windows follow-up when available
- **Gameplay:** manual Play mode test for movement, editing, placing all shape variants, atlas, chunk seams
- **Networking:** host/client + late join + distant players + saturated edits
- **Performance:** record CPU/physics/meshing and GPU timing before expanding chunk radii

### Known prototype limitations

- The world currently uses signed 16-block vertical sections (Y=-32..47); bounds and world settings are not user-configurable yet.
- Versioned chunk pages and deltas replace the original 512-edit global snapshot, but the RPC bridge still lacks true per-recipient filtering.
- Physical ground-item pickups now have a crash-recoverable redo journal; chests and other inventory operations still use independent profile/world saves. This is not a general multi-player transaction system.
- The .tui hotbar, inventory, crafting, and chest UI exist. Rendering, chest interaction and replicated animation still require hands-on editor/host/client verification.
- Player identity is a local bearer token rather than authenticated multiplayer account ownership.


## Physical item pickup recovery — November 19, 2026

The server now validates a physical drop and copies the receiving inventory
before any disk changes. An existing async world save is completed, and unsaved
terrain/chest edits are checkpointed before an immutable post-pickup drop
snapshot is journaled alongside the complete post-pickup player inventory,
tools, and selection. The receipt is flushed to disk and atomically renamed
before either the world v5 generation or player profile can change. After
both saves succeed, the receipt is removed, the in-memory inventory changes,
and the physical drop despawns. Startup replays any valid pending receipt
before restoring drops or accepting player profile logins.

A corrupt or unrecoverable pending receipt blocks loading instead of silently
resetting inventory. Recovery is repeatable across subsequent restarts, and
new unit tests simulate interruption before either save, after only the world
write, after only the player write, and after both writes but before receipt
deletion. They also test checksum tampering and duplicate pending pickups.

**Important performance limit:** each successful physical pickup forces a
synchronous world-save generation (plus a precheckpoint if world edits are
dirty). This prioritizes correctness over throughput for the current testbed.
Before large-scale play, replace per-pickup full snapshots with compact,
batched, durable transactions. World edits/chest transfers, player identity,
and direct-credit fallbacks have separate consistency requirements.

Run the pure-C# suite on Nobara:

```bash
dotnet run --project tests/BlockLogicTests -c Release
```

Play-mode host/client verification remains required before considering crash
recovery reliable on the live Tallow backend.

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

## M4: first caves and ore pass

`WorldGeology` uses deterministic integer hashing and trilinearly interpolated 3D noise. All sampling uses *world-space block coordinates*, not chunk-local PRNG state, so both peers should generate identical terrain regardless of job order. Caves occur underground (primarily Y=-29 through -5), leaving the surface cover and bottom few layers intact. Coal (type 7) and iron (type 8) appear in remaining underground stone; iron is restricted to deeper layers. They are not yet selectable in the six-slot creative hotbar. The current nine-tile atlas is unchanged: ore types temporarily reuse the dark forge-side and light log-end artwork; dedicated ore textures need approval before being added.

Geology is part of `BaseBlock`, ensuring `CreateBlocks`, `GetBlock`, spawn clearance, edits, server checks and clients all share one source of truth. Edits override geology and are sent through the existing authoritative v2 snapshot format. `GeologyTests` cover repeated deterministic sampling, negative/positive chunk boundaries, ore and cave existence, and edit replication.

Run on Nobara:

```bash
cd ~/TallowProjects/Tallowcraft
git pull --ff-only origin main
dotnet run --project tests/BlockLogicTests -c Release -- --bench
```

Then inspect caves underground in Play mode, mine into coal/iron, cross negative X/Z/Y chunk seams, and test host/client. The cave density and generation cost remain provisional until runtime checks. This first geology implementation does not yet add inventory, mining drops, custom ore textures, or larger vertical world limits.

## M4: biome climate and taller terrain

`WorldBiomes` deterministically derives temperature, moisture, and relief from world-space noise and the synchronized seed. Four first-pass categories (Meadow, Woodland, Drylands and Highlands) share the existing approved nine-tile pixel atlas: Meadow/Woodland use grass, drylands use dirt, and highlands use stone. The original world surface appearance and heights are retained within roughly 40 blocks of the origin, with terrain heights smoothly transitioning between 40 and 120 blocks away. Outside that transition, highland ridges can rise into Y=16..34 and cross vertical 16-block chunk boundaries.

`ChunkGenerator.HasPotentialBlocks` now checks actual heights in upper vertical chunks using the authoritative seed, preventing tall terrain from being incorrectly skipped. The generator caches height and biome per X/Z column when constructing positive-Y chunk data. The world height remains Y=-32..47; the existing underground caves, coal, iron, ore rules, and snapshot format are unchanged. No biome-dependent foliage or new art has been added.

Tests: `BiomeTests` validates original spawn heights, terrain bounds, deterministic sampled heights, multiple biome regions, upper-chunk buffering, and safe spawn clearance. The CPU benchmark now includes `underground-caves` and `biome-upper-layer` rows in addition to the previous cube, mixed, dense-partial and all-partial workloads. These are pure-C# tests; the updated game has not been compiled or Play-tested in this environment.

```bash
cd ~/TallowProjects/Tallowcraft
git pull --ff-only origin main
dotnet run --project tests/BlockLogicTests -c Release -- --bench
```

When validating in Play mode, travel beyond the original spawn zone (at least 120 blocks), inspect cliff/terrain chunk seams, confirm the elevated layer generates collision, and check performance while approaching the new highlands. Watch the existing `[Tallowcraft chunk profile]` timings to spot increased collider costs. Future tuning should profile repeated elevated-chunk discovery checks rather than assuming the new noise is free.

## M4: tree and atlas pass

The approved original nine 32×32 atlas tiles were preserved and reorganized into the first nine slots of a 4×4 (144×144) RGB PNG, keeping the existing 2px extruded gutters. Added 32×32 coal ore (slot 9), iron ore (slot 10) and opaque leaves (slot 11) using pixel-art designs. The original texture import file remains `filter = nearest`, `wrap = clamp`, `mipmaps = 0`, `compression = 0`, `fullResolution = 1`. The texture atlas remapping is strictly by tile index; no Tallow Engine modifications.

`WorldFlora.cs` now generates one jittered tree root per 8×8 world-space cell. Woodland has a higher seeded density, meadows have lower density, and drylands/highlands have no trees. The first 60 blocks around the origin do not contain tree roots, preserving existing spawn points. Trees reuse block type 4 logs, add type 9 opaque leaves, and create a 4–6 block trunk with a layered radius-two canopy. Trees can cross X/Z/Y chunk seams and may overlap, with trunk priority over foliage.

`ChunkGenerator.CreateBlocks` overlays deterministic tree decorations after solid terrain and before player edits. `BaseBlock` uses the same world-space decoration logic for neighbor meshing and authoritative state queries. `HasPotentialBlocks` now considers natural trees in elevated chunks so canopies at Y=16 or Y=32 are streamed. The old cave, ore and biome noise rules remain unchanged, and the existing edit snapshot format serializes block types through 9.

`FloraTests` checks atlas tile mappings and UVs, tree determinism, biome restrictions, spawn protection, X/Z/Y seam consistency, upper chunk streaming and late-join edit replication. `MeshBenchmarks` adds `woodland-trees` and `forest-upper-layer` CPU benchmark cases in addition to the existing six. Regression test and in-editor gameplay validation still required after pulling.

```bash
cd ~/TallowProjects/Tallowcraft
git pull --ff-only origin main
dotnet run --project tests/BlockLogicTests -c Release -- --bench
```

After tests, launch Play mode, head more than 60 blocks from origin into woodland/meadow biomes, check trunk/leaf texture sharpness, seam-free chunk streaming and ability to mine tree blocks, and monitor the existing `[Tallowcraft chunk profile]` logs. This pass intentionally uses opaque collidable leaves; transparent cutouts and leaf decay are future work.

### Benchmark correction

As part of the tree pass, corrected `MeshBenchmarks.Generate` to apply artificial dense partial-shape edits only to density cases 2 and 3. Previous `underground-caves` and `biome-upper-layer` results unintentionally included dense partial-block edits and should not be compared to the new pure natural terrain results. New `woodland-trees` and `forest-upper-layer` tests use no edits. The updated atlas's three new tiles have matching left/right and top/bottom edge pixels to reduce visible texture seams, with 2px extruded gutters still intact.

## M4: shrubs, pines and ruined structures

Following successful user-side verification of the original trees and the 4×4 atlas, `WorldFlora` now adds a second silhouette: tapered pines with 6–8 block trunks in highlands and a subset of woodlands, alongside existing 4–6 block oaks in meadows and woodlands. The current log and opaque leaf tiles remain unchanged, and the original 60-block spawn-safe area still excludes tree roots.

`WorldDecorations` adds small leaf-block shrubs (more common in woodland, less common in meadows), with a 64-block spawn-safe zone, and rare abandoned stone ruins with 7×7 foundations, variable-height stone walls and a doorway. Ruins are kept at least 112 blocks from the initial spawn. Stone and planks reuse existing texture tiles; no atlas changes or Tallow Engine modifications were needed. Structures are intentionally simple, collidable, and mineable voxel constructions. Opaque bushes are collidable for now; flowers and thin grass will need a non-colliding decoration mesh/foliage renderer instead of full voxel cubes.

All roots and construction shapes depend only on deterministic world coordinates and the synced world seed, not chunk load order. Decorations can span positive/negative X/Z and Y chunk boundaries. `ChunkGenerator.CreateBlocks` applies trees and then structures/shrubs before authoritative edits. `BaseBlock` and neighbor queries apply matching precedence, and `HasPotentialBlocks` includes upper-layer decorations so chunks with trees or ruins are not skipped. The existing bounded edit snapshot can remove structure blocks; persistence and large-scale chunk deltas remain future work.

`DecorationTests` checks spawned structure/bush positions, biomes, protected spawn area, ruin door/floor/walls, direct-vs-buffered samples across seams, upper-layer stream discovery, and server-to-client edit snapshots. `FloraTests` now also checks pine placement and heights. The CPU benchmark adds `shrub-patches` and `stone-ruins` natural chunk cases. No .NET or in-engine build was run in the assistant environment; run on the tested Nobara installation:

```bash
cd ~/TallowProjects/Tallowcraft && git pull --ff-only origin main
dotnet run --project tests/BlockLogicTests -c Release -- --bench
```

The decoration test's passing message reports a `nearestRuin=x,y,z` location (for seed 42) to make in-game checking easier. Verify structure materials, doorway clearance, vegetation, pine shape, chunk seams, and collider timing in Tallow Play mode.

## Streaming performance pass — near chunks first

The primary performance issue was an Inspector override in `src/Scenes/Main.tscene`: `RenderRadius = 40` (the code clamped it to 16), making chunk enumeration and background generation unnecessarily broad. The scene now sets `RenderRadius = 6`, `WorkerCount = 4` and `UploadsPerFrame = 3`. Defaults in `BlockWorld` are `RenderRadius = 6`, `WorkerCount = 3`, `UploadsPerFrame = 3`, `SideRadius = 4`, `BehindRadius = 2`, `EditableRadius = 2`, `VerticalRadius = 1`. All remain editable in the Inspector.

`StartNearestJobs` now builds/sorts a cheap distance-priority candidate list before checking expensive `HasPotentialBlocks` (which evaluates heightmaps, tree roots, ruins and other features). It avoids rescanning the entire candidate set per worker, deduplicates local/remote areas, and prioritizes current-height nearby chunks over vertical and distant scenery. Per-job linked tokens allow canceling work that is stale or no longer in the stream range. This does not change voxel dimensions: chunks remain 16×16×16, akin to Minecraft's vertically stacked sections.

When the viewer is on the surface (world Y >= 5), the streamer omits negative-Y sections under unbroken Y=0. It restores those sections if the player descends close to Y=0 or an edit opens a shaft at Y=0; the same rules protect remote players' collider neighborhoods. This is **not** a complete view-frustum/occlusion system; visible higher-altitude chunks still use horizontal directional/distance streaming, and generation of some occluded positive-Y sections remains possible. Exact frustum and column occlusion are later optimizations. The world seed, deterministic terrain content and chunk/edit replication formats were not changed.

The existing `[Tallowcraft chunk profile]` measures synchronous `SetMesh` and collider calls; an additional `[Tallowcraft stream profile]` prints average and maximum queue-to-upload latency every 64 successful uploads. This latency includes generation, queue waiting and frame scheduling; it is not a pure CPU mesh-generation timer. New `ChunkSchedulingTests` cover near-first priority, vertical ordering and sealed/exposed underground behavior, and need to run on Nobara.

```bash
cd ~/TallowProjects/Tallowcraft && git pull --ff-only origin main
dotnet run --project tests/BlockLogicTests -c Release -- --bench
```

In Play mode, compare how quickly ground appears beneath the player, check walking and turning around the edge of the 6-chunk radius, dig from the surface through Y=0 into caves, and verify that the underground loads when exposed. For multiplayer, check both host and client moving independently and editing shafts. Send stream-profile logs to quantify residual queue-to-upload delays. Native Jolt cooking and GPU transfer remain outside these numbers.

## High-radius mountain performance: game-only shadow cutoff

A read-only inspection of Tallow's `feature/csharp-gameplay` renderer confirmed **camera frustum culling is implemented** in `Engine/src/Render/RenderView.bf`. The game scene's camera has `occlusionCulling 0`; this separately controlled feature is *disabled*, and enabling it in an open mountain scene can cost CPU while hiding very little. The renderer also gathers shadow-casting meshes within the sun-shadow range even if they are outside the camera's main-view frustum.

Tallowcraft now limits its **voxel chunk** shadow submissions with `LimitChunkShadows = true` and `ShadowRadius = 5` (horizontal distance in 16-block chunks, one layer vertically). This is recalculated as the player moves and changed only for renderers entering/leaving the shadow ring. Meshes beyond this radius still draw normally when in view, but do not generate costly cascaded shadow renders. This setting can be disabled in the BlockWorld Inspector for A/B comparison. The scene's `shadowDistance` has also been lowered from 120 to 80 world units. No Tallow Engine source or render internals were modified.

This may significantly reduce shadow-pass draw calls when viewing a wide mountain landscape at `RenderRadius=16`, but it does not replace LODs or GPU/CPU frame profiling, nor does it change how many mesh GameObjects exist. `ChunkSchedulingTests` covers the caster-distance boundaries. Try the same viewpoint at radius 16 with `LimitChunkShadows = true` and false, then separately toggle directional-light `Cast Shadows` to distinguish GPU shadow workload from main-thread streaming. Editor/engine frame timings will establish whether vertex processing, draw calls or CPU object updates are the remaining bottleneck.

The `Camera.OcclusionCulling` setting was intentionally left off, since heightmap terrain and exposed mountain ridges are a poor match for the CPU occlusion buffer compared to enclosed caves or structures.

## Sustained FPS investigation — live chunk overhead (wide-radius mountain views)

User reported that framerate starts high when turning and then worsens as generated chunks accumulate, while disabling distant terrain shadows did not help. This indicates that persistent per-frame streaming management and/or the number of live meshes/renderers are stronger candidates than shadow-map submission. Read-only engine inspection confirmed view-frustum culling exists; the game camera's CPU occlusion culling remains off.

Tallowcraft-side optimizations:
- `BlockWorld.LocalUpdate` no longer performs full `UnloadDistant`, `UpdateColliders`, and `MaintainBlockCache` scans every rendered frame. Unload is triggered by viewer chunk/directional footprint changes; collider/cache scans run on player position/visibility transitions, remote-player center movement, or streaming setting changes. Small camera movements below the directional threshold avoid unnecessary work.
- `StartNearestJobs` now builds and sorts a single queue of candidate chunk sections per footprint/edit change, then consumes it across subsequent frames; it does not rebuild the whole radius² queue every time a job finishes. Dirty edits invalidate the queue and stale jobs are correctly re-enqueued after an obsolete task clears.
- Completed zero-triangle meshes no longer create GameObjects, MeshRenderers or colliders; the zero-surface section's version is cached until it exits the stream footprint or becomes dirty. Empty-section cache is also invalidated on world seed change.
- Once the candidate queue is exhausted and no generation jobs remain, the streamer avoids repeated candidate enumeration while stationary.
- Optional `[Tallowcraft stream profile]` reports now include `live_meshes`, `empty_sections` and `colliders` alongside queue-to-upload latency. Profiles are user-controllable as before.
- `ChunkSchedulingTests` now includes a zero-triangle upper-sky-section regression and a positive-surface-section regression.

No Tallow Engine changes were made; the same 16×16×16 block section system, world generation, voxel edit format and rendering/shader backend remain intact. These changes have not been tested in a running Tallow process in this assistant session.

```bash
cd ~/TallowProjects/Tallowcraft && git pull --ff-only origin main
dotnet run --project tests/BlockLogicTests -c Release -- --bench
```

After the automated tests, revisit the same mountain viewpoint at `RenderRadius=16`. Allow chunks to finish loading, rotate and stand still. Compare *steady* FPS as well as initial loading hitches. If steady FPS still falls with higher `live_meshes`, next targets are mesh vertex count, per-chunk draw submissions, actual camera visibility, distant LODs and chunk batching. Shadows have already been deprioritized by the user's A/B result. Test multiplayer and mining edits as well, since chunk state and collider streaming remain authoritative.

## M6: first material inventory and mining rewards

First survival-oriented gameplay pass. `BlockInventory` tracks quantities for types 1–9 (grass, dirt, stone, logs, forge, planks, coal ore, iron ore, leaves), with a maximum of 999 per type, nine-count versioned snapshots and strict atomic validation. The server owns each player's inventory through `BlockEditor`. `[Sync] InventoryState` replicates counts to the owning client's hotbar, without trusting inventory values submitted by clients.

To preserve creative testing, `BlockEditor.CreativeMode` is **true by default**. Turn it off on the player prefab's BlockEditor inspector component for survival building. Initial supplies: grass 32, dirt 64, stone 64, logs 32, forge 8, planks 64, ores/leaves 0. Mine blocks with right mouse to collect one matching material directly; placed blocks cost one selected material in survival mode. Server enforces material availability before an edit and changes quantities only after the actual voxel-edit transaction succeeds. Block edits remain subject to range, physics, and server authority checks. The existing six-slot hotbar retains its controls, and coal/iron/leaves are shown separately as collected resources. The local placement preview reports "not enough materials" for an empty selected slot.

This is direct-to-inventory collection, **not** physically spawned pickups. A mined block at capacity does not yield another item; overflow/drop/spawn systems remain open. Item counts represent block types, not individual rotated/partial states; one successfully edited block costs or awards one material. Snapshot persistence and reconnect restoration have not been implemented. World block-edit replication remains the independent, existing bounded edit snapshot.

`InventoryTests` validates starter supplies, successful spending, exhausted stacks, mining additions, capped resources, invalid types, strict replication payloads and atomic failure behavior. To validate on Nobara:

```bash
cd ~/TallowProjects/Tallowcraft && git pull --ff-only origin main
dotnet run --project tests/BlockLogicTests -c Release -- --bench
```

Then in Play mode, switch the player's BlockEditor `CreativeMode` off, place several blocks and mine them back, collect coal and iron, and verify that the hotbar quantity changes on the host and a connected client. Make sure a failed placement (occupied space, out of range, no resources) does not consume items. None of the C# or game-engine runtime code was compiled or tested by the assistant session.
