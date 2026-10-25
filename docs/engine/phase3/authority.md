# Room creation authority

> **Kernel record, generalized for rooms in Run 1 (P1); ledger, checkpoint and save bounds revised after the Lane P review.** `game/scripts/creation_authority.gd` was built for the retired Barton workshop and is kept. Run 1 replaced its plot rectangle with the room's bounds, its protected garden with locks, its terrain sampler with a physics surface query, its fixture identities with the contract principals, and added contract receipt metadata. The [command host](command-host.md) is its contract front door.

`creation_authority.gd` is the host-owned command boundary for creations and locks in one room. It is a `RefCounted` service. The trusted adapter (the command host, or the invention runtime directly in tests) supplies the principal; no request can set its principal, owner, height, instance identity, artifact, permission grants, approval or resource reservation. This is a local authority boundary, not remote authentication or multiplayer.

## Integration

1. `configure(room, surface_query, save_path)`. `room` is `{room_id, manifest_sha256, bounds: {min_m, max_m}, entities: {"obj:x" or "shell:x": {min_m, max_m}}}`, built by `CommandHost.RoomDictionary(RoomData)`. `surface_query(x, z, from_y)` returns `{ok: true, height_m, entity_id}` for the first support below `from_y` (the runtime ray-casts the room's collision layer, ignoring creations). An existing save blocks every mutation until it has loaded.
2. `load_saved()` validates and recompiles everything; a failed load locks writes without overwriting the file. The file is parsed with the exact strict parser (`creation_json.gd`), so saved numbers read back bit for bit.
3. `snapshot(principal)` returns copies: instances (with fresh artifacts, `active` and `locked`), revisions, roles, consent, capacity, `locks`, `entity_revisions`, `room_pin` and `bounds`.
4. `submit(principal, request, receipt_meta = {})` accepts `place`, `revise`, `remove`, transient `activate`, `lock` / `unlock` with `targets`, and `checkpoint` with an optional `label`. `place`/`revise` take `source`, `x_m`, `z_m`, `yaw_deg`, optional `y_m` (height hint) and `on` (the entity it must stand on). `receipt_meta` is the host's `{fingerprint, op, at_utc, approved_by}`: the fingerprint replaces the internal one so replays are keyed on the contract command, and `approved_by: player:local` lets a held companion command change the player's creation. Receipts without host metadata still record a derived contract op and the commit time.
5. `preflight(...)` runs the same candidate path read-only. `entity_revision(id)` (creations: their revision; objects and shell parts: their play revision, 0 until changed; -1 when not in the room), `is_locked(id)`, `receipt_for(principal, action_id)`, `compacted_receipt(principal, action_id)`, `receipt_count()`, `checkpoints()`, `forget_activation(principal, action_id)` and `room_bounds()` serve the command host and the runtime.

The runtime binds `occupancy_query` (the real player and companion capsules must not end up inside a placed collider) and `activation_query` (a ground device is used from within 2 m; a worn design only by its wearer).

## Permission, placement and protection rules

- `player:local` owns the room. `companion:local` is an editor by default (the player can make it a visitor). An editor places and revises its own creations; the owner may remove anything for moderation; nobody silently rewrites another's source. A companion change to the player's creation needs the player's approval through the command host.
- Role revocation keeps source and reservations but makes the creator's instances inactive at once. Activation needs the activating principal's consent; effects need the creator's current role, a consenting target and an allowed position. Consent starts off and resets to off on every load.
- The full rotated footprint and every wind or proximity radius must fit inside the room's X/Z bounds; a ground creation must also fit under the ceiling.
- Support is sampled at the footprint corners and origin from just above the height hint (or from the ceiling). Samples may differ by at most 5 cm, the creation stands on the highest, and `on` must match the support under the origin.
- **Locks.** `lock` and `unlock` take 1 to 64 objects or creations (shell parts are not lock targets). Only the player can unlock (`unlock_denied` otherwise). Locking or unlocking advances the target's revision and the room revision. A locked creation cannot be revised or removed by anyone. Every locked thing's X/Z footprint is a no-build, no-effect zone: no new footprint or field may overlap it and no force acts on a body inside it, except that a locked creation's own field still works. A reload does not re-check old placements against later locks.
- Each creator can wear one avatar design at a time.

## Capacity and execution bounds

| Resource | Per creator | Whole room |
|---|---:|---:|
| Instances | 8 | 16 |
| Parts | 96 | 192 |
| Behavior nodes | 64 | 128 |
| Wires | 128 | 256 |
| Wind capabilities | 16 | 32 |
| Light capabilities | 16 | 32 |
| Rotor capabilities | 16 | 32 |

Replacement validates the candidate collection **after subtracting the replaced instance**, and removal releases its reservation. All costs come from recompiling source. Limits are deliberately conservative local prototype policy; they are not measured target-laptop performance guarantees.

The shared rolling one-second execution window admits at most 5 evaluations per instance, 10 per actor and 20 globally, with corresponding node-work ceilings of 80/160/320. Field admissions are capped at 8 per actor and 16 globally. The companion using the player's invention charges both, so it cannot multiply the creator's budget. Host monotonic time must not move backward. Expiry never grants catch-up executions.

## Receipts and local persistence

Successful mutations store `(principal|action_id) → {fingerprint, receipt, meta}` in the same atomic save as the state. **The ledger holds 2,048 durable receipts and the last 256 are the player's**: the companion is refused with `receipt_limit` once the ledger holds 1,792, so it can never block the player's lock, placement or moderation. **`checkpoint`** compacts every durable receipt to `[revision, first 16 hex of the fingerprint]` (at most 4,096 kept, the oldest forgotten), keeps at most 16 checkpoint records `{id, revision, label}`, changes no world state, does not move the revision and never fails for a full ledger; a compacted action still replays (`replayed: true, compacted: true`) and still refuses other content. Activations keep session receipts bounded at 1,024 per principal, the oldest forgotten, so an activation never fails for capacity; the runtime calls `forget_activation` when an admitted activation could not fire, so its retry runs instead of replaying a success that never happened. A committed retry with the same fingerprint returns its receipt (with `replayed: true`) before any current permission or revision check, even after revocation or reload; different content is `action_id_conflict`; another principal never receives it. Receipts carry `affected` and `created` so the command host can rebuild the contract result.

Saves (envelope version 3; version 2 saves still load, without compaction) hold the room pin `{room_id, manifest_sha256}`, schema, compiler and style versions, editable sources and host placements, roles, consent, locks, object play revisions and the receipt ledger. Compiled artifacts are never trusted from disk. A pin mismatch, an older version, a forged lock target or receipt, or any structural error fails closed and leaves the file untouched. The candidate is written to a sibling `.pending` file, flushed, then renamed over the committed file; memory changes only after that succeeds. **A save is never written unless the loader would accept it**: `_persist` runs the same structural bounds `load_saved` checks (the JSON value budget, at most 16 instances, 2,048 receipts, 4,096 compacted receipts, 16 checkpoints and 256 locks) before writing, and fails with `save_size` otherwise. The JSON value budget is the byte limit itself (every JSON value takes at least one byte), so a save that fits 4 MiB always passes it; the old fixed budget of 32,768 values refused saves past about 1,930 receipts after they had been written (Lane P review blocker).

Bounds: 64 KiB commands, 4 MiB saves, 2,048 durable receipts per save (256 of them the player's), 4,096 compacted receipts, 256 locks and 1,024 transient activation receipts per principal per session. Anchored patterns use `\A` and `\z` (PCRE's `$` matches before a final newline).

## Verification

`game/tests/creation_authority_smoke.gd` passes **266 checks** on Godot 4.7.2 (and fails itself on any script or engine error, `kernel_test_guard.gd`): host-assigned fields, forged request fields (including approval fields), roles and moderation, consent, room bounds and rotor sweeps, the surface query (missing, uneven, non-finite, on and under a table, `on` mismatch), the ceiling, locks (companion lock, refused companion unlock, locked creations, no-build and no-effect zones, persistence, failed persistence), approvals and receipt metadata, capacity, runtime budgets, corrupt, incompatible, version-1 and forged saves, occupancy and preflight, a save with 2,048 durable receipts written and reloaded, the player's reserve and the checkpoint, bounded activation receipts and version 2 saves. `invention_runtime_smoke.gd` (77 checks) runs the same authority with the real C# bodies, and `native_kernel_command_host.tscn` (195 checks) drives it through `garage_game.command`.
