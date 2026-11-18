# Block-state roadmap and implementation status

## Implemented in the prototype

- Compact versioned `ushort` block state: 8-bit type, 2-bit facing,
  2-bit shape (cube/slab/stair/half-wall), 1-bit upper flag.
- Procedural grass-top, grass-side, dirt, stone, log, forge-front and plank textures
  on one client-local texture atlas. Texture UVs use full blocks, never stretch
  across merges. This is placeholder procedural art, not an imported art pack.
- Half-cell occupancy meshing for slabs, stairs and half-thickness full-height
  walls; the exact same mesh drives physics through MeshCollider.Refresh().
- Server-authoritative selection of placement target and orientation from
  network TickInput; sparse snapshots are version 2 and carry complete block
  states for late joiners. Version-1 snapshots can be read for migration.
- Copy-on-write edit dictionaries and stale chunk-job invalidation remain in use.

## Follow-up required

- Test on actual Tallow C# editor/host/client builds (no build verification yet).
- Add user-facing block selection/hotbar; Ability1 and Ability2 need network
  input bindings, or the PlaceType/PlaceShape Inspector fields can be used.
- Add atlas sampling shader using world-space tiled UVs, so greedy merging can
  cross block boundaries without texture stretching. Current meshing caps
  textured merges at one block, a geometry/performance compromise.
- Add thin fences, connected walls, doors, transparent/cutout vegetation, water,
  and proper block-entity state for machines such as forges.
- Add collision-aware placement occupancy tests for arbitrary partial shapes
  (current full-voxel overlap box is deliberately conservative).
- Persist edits across server restarts; move bounded full-world snapshots
  (512 edits / 20,000 characters) to per-chunk snapshots and deltas.
- Expand beyond one vertical 16-block chunk and optimize physics separately
  from render chunks; test dedicated headless performance.
- Add automated regression tests for all eight half-cells, rotations,
  tile selection, negative-coordinate chunks, chunk seams and packet parsing.
