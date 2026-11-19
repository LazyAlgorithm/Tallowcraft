# Saved sections, chunk streaming and client cache (implementation)

Tallowcraft replaces the 512-global-edit prototype with section-backed edits.
This code lives entirely in the game repository, not in Tallow's engine.

## Authoritative state

- `BlockEdits` indexes by signed 16×16×16 `ChunkKey`; persistent immutable
  dictionaries share unchanged sections between concurrent meshing snapshots.
  The global voxel count is not capped at 512. Valid positions and a maximum
  4096 edits per section are still enforced.
- `SectionedEditSnapshot.InSection` and `Around` prevent distant edits
  from being scanned every time a chunk is meshed.
- `EditSectionCodec`: deterministic 4-byte local-coordinate + packed-block
  entries, ordered by local index. An SHA-256 digest covers each whole section.
  64 edits per page (256 raw bytes, 344 Base64 characters).
- Every section has an integer revision (including edits restoring natural
  blocks). Delta clients accept only the exact next revision.

## Server save/recovery

- `WorldEditSave` stores `GamePaths.PlayerData/tallowcraft/worlds/default/`.
- `current.txt` points to a complete committed generation directory.
- Each generation contains a `manifest.txt` (world ID, seed, section
  revisions, checksums) and a binary file per modified section.
- Saves write a completely new generation in a temporary folder, rename it
  into place, then atomically update `current.txt`. Incomplete staged saves
  are never loaded.
- The server loads the last committed generation when starting. A damaged
  committed save causes a load error and disables edits, rather than silently
  overwriting the user's construction with fresh terrain.
- Autosaves run every 30 seconds on an immutable snapshot, and
  `OnDestroy` performs a final save. A crash can lose unsaved edits since
  the last autosave. Completed saves retain the previous committed generation\n  as a recovery backup and prune older complete generations.

## Clients and late joining

1. The server's world object replicates metadata only:
   `3|<seed>|<world GUID>`.
2. A client builds a near-first candidate list. Before meshing, it requests
   a baseline for that section and adjoining face-neighbor sections.
3. The request originates from the *client-owned player* `BlockEditor`
   network identity; the server validates the RPC sender, proximity and rate.
4. The server returns sections in 64-edit pages. A page can never exceed
   the 4000-byte engine RPC argument ceiling.
5. The client assembles pages only within one consistent section revision,
   validates the complete digest and canonical block contents, then atomically
   replaces its local section and allows the pending mesh job.
6. Later authoritative edits send only one position, packed value and
   monotonically increasing per-section revision; gaps cause a baseline
   refresh. Edits are never taken from the claiming client's block data.
7. Completed baselines are cached under
   `GamePaths.PlayerData/tallowcraft/chunk-cache/<world GUID>/`.
   A reconnecting client sends cached revision/checksum. A matching server
   response lets it use the cached bytes without downloading them again.

## Known integration and scaling limits

- Tallow's current C# `[Rpc(RpcTarget.Everyone)]` broadcasts each response
  to every connected peer. Tallowcraft checks the included target client ID
  before applying it, but **other peers still receive the traffic**.
  A C# public reliable `SendToClient` / custom game-message API in Tallow
  is needed for network-level bandwidth filtering by subscriber. This
  migration does *not* claim that per-recipient sends already work.
- Snapshot pages are requested sequentially; heavy fully edited sections
  require many round trips. Future optimizations: multiple in-flight pages,
  compression, prioritized back-pressure and cache eviction.
- Browser IndexedDBFS persistence and actual two-client network tests have
  not been verified. Existing .NET logic and asset tests do not test native
  Beef/Jolt/WebGL networking.
- Save generation writes are atomic at the pointer level, not a substitute
  for backups. Keep external backups for important user worlds.
