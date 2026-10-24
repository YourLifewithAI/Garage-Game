# Persistence rules carried forward from the save/travel service

Squared with [docs/game/MECHANICS.md](../game/MECHANICS.md) on 14 October 2026: where this page and that design differ, the design wins.

The PostgreSQL save/travel service was removed in the room-scale cleanup (restorable from the `geography-era-final` tag). Its storage engine does not fit a single-player room game, but the rules it enforced do. File-based room saves must honor them from the start so that a later multiplayer service can adopt the same semantics instead of retrofitting them.

## Rules

- **Every mutation carries a unique action ID and yields a durable receipt.** Repeating an action ID with identical content returns the original receipt. Repeating it with different content is refused. This is already how `creation_authority.gd` and `world_state.gd` behave; room saves must persist those receipts with the state.
- **Writes are atomic and fenced.** A save is written to a temporary file and renamed into place; a reader never sees a partial file. Each save records a store revision and the session that wrote it; a stale session (an older process, a crashed run) cannot overwrite a newer revision.
- **A checkpoint precedes any transfer.** Before leaving a room (and later, before crossing a portal), the source room is saved and that checkpoint is referenced by the transfer. Arrival is acknowledged only after the destination has been materialized; until then the player can be returned to the source checkpoint.
- **Uncertain results are reconciled, not retried blindly.** If a write's outcome is unknown (crash mid-save, interrupted rename), the next start reads the authoritative state and the receipt ledger and resolves the action from them. Old commands are never replayed into a new session.
- **Durable versus transient receipts.** Commands that change saved state (moving, placing, removing, creating, locking, restyling, undo) get durable receipts in room state. Goals, effects and grabs change nothing that is saved; their receipts are transient and live for the session. When a checkpoint is written, durable receipts at or below its revision may be compacted into it, and a lookup reports the action as compacted rather than unknown.
- **Bounded quotas with a reserved recovery path.** Receipts, history and save sizes have caps. A small fixed set of recovery operations (load, return to a safe spawn, restore a checkpoint) must remain available even when ordinary quotas are exhausted.
- **Movement and effect state are never persisted.** Saves hold entities, creations, locks, identities, pins and receipts; a loaded room restores a checked spawn point and resets consent for active effects.
- **The trusted save path is not the AI's path.** The companion adapter never receives save-file access or raw envelopes; all its changes go through the same command path as manual edits.

## Shape of a room save

**Today.** The proposal that stood here for R0 (`user://rooms/<room_id>/state.json`) was not built as drawn (`user://rooms/` holds exported rooms, not saves). The saves are:

```text
user://saves/rooms/<room_id>/<manifest prefix>/inventions.json
                     the room save: schema garage_game.creation-world (enfractal.creation-world before the rename), version 6, written by the command host
                     (<manifest prefix> is the first 16 hex characters of the room manifest's hash)
user://single_player/progression/player_modes_v1.json   the vitals, one record per mode (version 1)
user://single_player/progression/glow_trial_v1.json     the Glow record, kept once per mode today (file version 2)
```

**Design** ([docs/game/MECHANICS.md](../game/MECHANICS.md) sections 2 and 10; packets M0, M1 and M3 of [RUN-2-MECHANICS.md](../runs/RUN-2-MECHANICS.md)): the island's save moves under the team and the mode, beside a team file, the vitals store and one expertise store per team, so two profiles on one island, or one profile in two modes, never share a file. The room save goes from version 6 to 7, the Glow file from 2 to 3 and the vitals store from 1 to 2. The exact layout was fixed in M0's contract round ([contracts/README.md](../../contracts/README.md), "Teams and the save layout"; the documents in `contracts/team.schema.json`) and is built in M3 as a new `game/scripts/native/Saves/**` (that directory does not exist yet). The move is the one step that can lose a save, so it writes the new path, verifies it, then removes the old file, and the old files stay until the next launch. The rules above (atomic writes, fencing, receipts) carry over unchanged.

Size caps and quota numbers are set in R0 from measured room content, not copied from the old service.
