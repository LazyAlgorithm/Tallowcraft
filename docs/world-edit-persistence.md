# Scalable edited-world persistence and multiplayer synchronization

Tallowcraft currently keeps a **server-owned** `BlockEdits` dictionary. A
`[Sync] string EditState` on `BlockWorld` sends one complete, versioned
snapshot (seed plus all packed block states). The format is deterministic and
late joiners receive the terrain seed and modifications together.

## Current hard limits

- `BlockEdits.MaxChanges = 512`; the max encoded string is 20,000 characters.
- Each successful block edit serializes and republishes the *entire* world.
- `BlockEdits.Set` copies the global dictionary on every change to keep
  generation-worker snapshots stable.
- A client either receives a valid complete snapshot or leaves its prior
  edit state unchanged.
- **Do not** just lift the 512 limit: replication and whole-world copy costs
  would scale with every block placed, and long strings could exceed native
  network payload limits.

## Next implementation steps (game-only; no Tallow Engine changes)

1. Keep a server-side, immutable **per-section** edit map keyed by
   `ChunkKey(x, y, z)`. Publish only the changed section to workers; reuse
   untouched dictionaries without a global copy. Track one monotonically
   increasing revision per section and a world generation/seed epoch.
2. Persist a versioned world manifest (seed, epoch, format) and section
   records to server-controlled storage. Write to a temporary file, flush,
   then atomically rename. Store only edits differing from generated base.
   Include orientation and shape packed as `ushort`.
3. For replication, use an indexed, bounded per-section snapshot/delta
   protocol over Tallow's existing reliable server/client messaging or
   server-owned network identities. Never place all world edits in one
   `[Sync] string`. Messages include chunk key, epoch, revision and a
   checksum. Send full section state on first interest/late join, then
   ordered, idempotent deltas.
4. A client applies contiguous revisions only. Gaps, bad checksums or a new
   epoch trigger a resync request. A section becomes visible only after it
   has applied the authoritative baseline. Bound queued and in-flight work
   for large settlements.
5. Keep server ray validation, occupancy checks, inventory charging and
   resource awards in one authoritative transaction. Failed writes do not
   charge the player. Persist inventory separately by player identity.
6. Regression-test negative-coordinate section boundaries, reconnects,
   concurrent editors, 100k edits, partial-block states, atomic save recovery,
   message reordering and late join before switching away from `EditState`.

This is a migration design, **not** a claim that the 512-edit limit has already
been removed. Until the replacement is integrated and tested, the existing
limit and format remain intact.
