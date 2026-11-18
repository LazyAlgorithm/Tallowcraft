# Block shapes, orientation, and texturing — next world milestone

The initial multiplayer editing prototype only supports `0 = air` and `1 = solid cube`.
Do not widen its wire format casually: the next milestone needs a versioned block-state
representation, compatible serialization, updated meshing, and server validation.

## Block state

- Persistent **BlockType ID** selected from a registry, not a mesh or prefab per voxel.
- **Orientation** (four horizontal facings; optionally vertical/24-way for specialized blocks).
- **Shape / variant** (full cube, upper/lower slab, stair facing and upside-down flag, half-wall,
  fence/connection mask, door top/bottom and hinge, etc.).
- Optional interaction data (forge inventory and burning state) as separate sparse block-entity records,
  **not** repeated per-voxel in all chunks.

Model `BlockState` as a compact immutable value, e.g. `ushort TypeId`, `byte Facing`,
`byte Variant`. Its exact serialization must be versioned before introducing it.

## Geometry and collision

- Greedy mesh only adjacent faces with *identical* material, texture tile, orientation and visibility.
  Full faces can be merged; stairs, slabs, walls and other partial faces use cached shape templates.
- Use a material/texture atlas with top/side/bottom tiles and orientation-aware UVs; atlas repeats
  require UV design that does not stretch a tile over greedy quads.
- Generated collision matches each shape: full cubes use simple merged collision,
  slabs use half-height boxes, stairs can use two or more box hulls, fences/walls use narrow shapes.
  Consider separate render and collision meshes, and make sure Tallow's runtime collider-refresh
  handles changes to each one.
- Face occlusion must account for *partial* neighboring faces, not treat every non-air block as an
  opaque full cube. Mesh generation consults neighboring chunk states for seam correctness.

## Placement and networking

- Client sends ordinary networked `TickInput`; **server raycasts and chooses** the target cell,
  block type, facing, and shape variant. Validate reach, interaction permissions, collisions,
  multi-block adjacency and placement rules server-side.
- Looking direction / selected face determines initial facing (forge front points to the player,
  stair facing follows placement yaw, slab upper/lower uses surface placement height).
- Replicate a **versioned per-chunk block-state delta** (type + orientation + variant) and
  authoritative snapshots for late joiners; never sync the generated mesh or collider data.
- Keep edits persistent across chunk unload/reload and server restarts, and serialize multi-block
  state transitions atomically (doors, beds, etc.).
- Add host/client/late-join, rotation and shape-collision regression tests on Windows/Linux.

This is a **design requirement**, not implemented in the first editing PR.
