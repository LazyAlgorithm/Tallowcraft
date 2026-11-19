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
- The server loads the latest valid generation when starting. The previous
  committed generation is explicitly recorded in `previous.txt`. If the
  latest manifest, section file or pointer is corrupted, the verified previous
  generation is restored and the current pointer is repaired when writable.
  A world with neither valid generation refuses to load rather than silently
  resetting the user's construction.
- Autosaves run every 30 seconds on an immutable snapshot, and
  `OnDestroy` performs a final save. A crash can lose unsaved edits since
  the last autosave. Completed saves retain the previous committed generation
  as a recovery backup and prune older complete generations.

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


## Saved players (prototype identity)

- The host uses `GamePaths.PlayerData/tallowcraft/player-id-host.txt`.
  Each remote client uses `player-id-client.txt`. These random identifiers
  survive process restarts. Set `TALLOWCRAFT_PROFILE_SLOT` to an alphanumeric
  slot name (e.g. `client2`) to run two client instances from one machine.
- The client sends its token in an owner-checked server RPC, without publishing
  it in `[Sync]`. The server loads
  `worlds/default/players/<world GUID>/<token>.profile`, validates the
  checksum and all ten counts (including iron ingots), tool durability, equipment and selected
  block state, and then enables the player's gameplay.
- Reconnecting with the same token loads the previously saved profile.
  An active-claim guard rejects two simultaneous players with the same token.
  A different saved world ID receives a separate inventory.
- Changes are autosaved every 30 seconds (when dirty), and saved again when
  the player object is destroyed. The previous `.profile.bak` is retained.
  A damaged newest file falls back to the validated backup; if neither file
  is usable, no fresh starter inventory is granted and gameplay stays locked.
- The bearer token is **not account authentication**. It is unguessable when
  generated but may be copied or intercepted on an unencrypted connection.
  Public servers require a stable authenticated account identity supplied by
  a verified login/transport, plus real authorization. Local host/client
  testing and trusted invite-only sessions are the intended initial targets.
- World and profile saves are separate transactions; simultaneous world
  edits and item costs are not yet crash-atomic as a unit.

## Smelting and iron upgrades

- Forge recipe: 1 iron ore (type 8) + 1 coal (type 7) -> 1 iron ingot
  (inventory-only type 10); atomic batches of five are also supported.
- Iron tools: pickaxe = 3 ingots + 2 planks, axe = 2 + 2,
  shovel = 1 + 2. The six equipment slots persist independently with their
  remaining durability; quick equip 7/8/9 selects the iron version if owned.
- The Forge page can be viewed anywhere, but the server allows smelting and
  crafting iron tools only within 3.5 metres of a real Forge (type 5) block
  recorded in the authoritative terrain/edits. The server validates
  ownership, open inventory, cooldown, real proximity, item costs, and tool
  identifiers. It never accepts the client's claimed forge location or prices.
- The item snapshot format now uses v2 (10 materials) and the equipment
  snapshot uses v2 (6 tools). Both readers accept v1 saves and promote them
  to v2 on the next save. The world voxel atlas and block IDs stay the same:
  ingots cannot accidentally become a placeable voxel.
