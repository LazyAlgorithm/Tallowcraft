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
- Left mouse: break the targeted block
- E (Use): place a block on the targeted face

Blocks can only be edited within 6 metres, and placement is refused when another physics body occupies the destination.

## Architecture

- **Streaming:** directional visible chunks around the local player's camera, plus physical chunks around **every player on the server**.
- **Mesh:** 16×16×16 chunks with greedy face merging and deterministic terrain generation. Neighboring chunk faces consult edits across boundaries.
- **Physics:** nearby static Jolt MeshColliders. Editing rebuilds the affected chunk's mesh and calls MeshCollider.Refresh(); chunk-edge edits rebuild the affected neighbor as well.
- **Network authority:** only server-side player TickInput can edit blocks. The player is a networked prefab; procedural chunk GameObjects remain transient and local to each machine.
- **Sync and joining:** the shared BlockWorld has a NetworkIdentity with Relevance=Everyone; its [Sync] EditState carries the world seed and the server's sparse edit snapshot. Clients wait for this snapshot before generating and reject invalid payloads. Worker jobs use immutable edit snapshots; stale completed jobs are discarded.
- **WorldGeneration:** a separate C# assembly definition with one primary type per source file.

## Prototype limitations

**Edits survive chunk unload/reload and late joining, but are not yet saved to disk or across server restarts.** The edit snapshot is deliberately limited to **512 differing blocks** (20,000-character upper bound). It sends the entire current edit state whenever a block changes; use reliable per-chunk snapshots and deltas before scaling to large or persistent multiplayer worlds.

Only block values 0 (air) and 1 (solid) are supported, the vertical world currently spans Y=0..15, and chunk collision/rendering on a dedicated server has not yet been separated into headless-only physics chunks. Moving/removing a block requires its chunk collider to have been generated first.

### Testing checklist

1. Host locally: walk, jump, break/replace top and side faces.
2. Edit a block at a chunk seam (X/Z multiple of 16); verify both chunks update without cracks.
3. Walk away and return; verify the edit remains.
4. Connect a second client; verify the terrain matches the host and both players see edits.
5. Join late after the host has edited blocks; verify the late join receives the modified world.
6. Stand far apart with two players; verify server collision remains around both.
7. Turn rapidly while jobs are active; verify stale meshes never overwrite newer edits.
8. Stress editing and monitor network bandwidth. A complete multiplayer/restart/CI soak is still needed.

The repo contains gameplay sources and a Tallow scene, not the engine itself.
