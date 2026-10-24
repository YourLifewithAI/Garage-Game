# What the companion perceives and remembers

Squared with [docs/game/MECHANICS.md](../game/MECHANICS.md) on 14 October 2026: where this page and that design differ, the design wins.

> **To be replaced in Run 2.** On 7 October 2026 the founder chose shared knowledge between the player and their companion, and selective memory. [JOURNAL.md](JOURNAL.md) is the draft design. The rules below hold until that lands.

**The founder's rule (6 October 2026):** the companion perceives anything within its avatar's line of
sight. The rule applies to every companion query and every command, not only `observe`. The companion
never perceives through the player's avatar.

**The founder's answer on memory (evening of 6 October 2026):** the companion remembers what it has
seen and where, and says when that may be out of date. Goals that only move or turn it may aim at a
remembered thing; anything that changes a thing still needs it in sight now.

The mock host implements both in `companion/src/garage_game_companion/perception.py` and `mock_host.py`.
`companion/tests/test_perception.py` and `test_perception_memory.py` pin them, and `test_no_holds.py`
runs the same suites again with no approval holds configured. In Run 2 the kernel host replaces the
geometry with physics ray casts against the real colliders. The rules and the tests carry over; the
geometry does not.

## What counts as in sight

- The eye is the avatar's position raised to its eye height: 0.087 m for both avatars. The companion
  shrank to the player's 10 cm body (radius 0.02 m, eye 0.087 m, reach 0.15 m;
  `WorldScaleProfile.SmallPlayer`); until 6 October 2026 it was 0.24 m tall with its eye at 0.205 m.
- Each candidate entity is sampled at 15 points on its bounding box: the centre, the eight corners and
  the six face centres, each pulled 1 cm (or a quarter of the box, whichever is smaller) inside.
- A sample is seen when the segment from the eye to it crosses no occluder. Occluders are the room
  shell (floor, walls, ceiling, as their polygons' boxes, padded to 1 mm where flat) and every object
  and creation box, except the entity itself and any box that contains the eye.
- The entity is perceived when at least one sample is seen.
- Always perceived: the companion's own avatar, whatever it holds, and the room shell it stands in.
- Avatars and effects never occlude.

## Where the rule applies

| Surface | Out of sight means |
|---|---|
| `observe` | Not in `visible`, and its signs are not in `texts`. No distance cut-off unless the request gives `radius_m` (at most 150 m, since the 10x world); line of sight is the real limit. Things it remembers are listed apart, under `remembered` (below). |
| `entities.list` | Not listed as seen now, whatever the filter (`near` included). A remembered thing is listed with `seen: "remembered"`. |
| `entity.inspect` | A remembered thing answers from memory. Anything never seen (or seen gone) is `target_not_found`, byte-identical to inspecting an id that does not exist. |
| `room.describe` | Not counted. A count of everything would say how many things are hidden; the counts are what is in sight now. |
| Every command that names an entity (`target`, `targets`, placement `on`, effect `targets`, `expected_entities`) | `target_not_found`, the same answer as for an unknown, removed or other-room id. Nothing changes and nothing is held. The one exception is a goal that only moves or turns the companion (below). |
| `actor` on any op | The companion's own avatar only. The player's avatar, or any other, is `actor_denied`, at the adapter and again at the host. |

The player's own controls and UI see the whole room.

## Perception memory

**What is remembered.** Each companion has its own memory. On every request from that companion the
host takes its avatar's line of sight and remembers each entity in it (objects, creations, other
avatars and effects; the shell is always in sight): its summary as seen (sanitised name, position,
bounds, revision and the rest), its parts and who protected it, the time and the room revision. That
is the only thing that fills memory. The player's queries, the player looking through the companion's
eyes, another companion's sight and the true room state never do. Asking what an avatar perceives
(`perceived`) remembers nothing.

**How results say so.** A remembered entity is its summary as last seen, plus:

| Field | Meaning |
|---|---|
| `seen` | `"remembered"`. Things in sight now carry `"now"` in a companion's results; the player's results leave the field out. |
| `last_seen_ago_s` | Seconds since the companion last saw it. |
| `last_seen_revision` | The room revision when it last saw it. |
| `may_be_stale` | `true` once the memory is 60 s old (`perception_memory_stale_after_s`) or the entity has changed in any way since: moved, edited, renamed, picked up, locked or gone. |

`entities.list` and `entity.inspect` answer from memory for anything out of sight, and filters apply to
what was seen, never to the true state. `observe` keeps `visible` for what is in sight now and adds
`remembered`: things seen earlier this session, out of sight now, within the radius of where they were
last seen, nearest first, at most 50. Signs are read only while in sight. "Where's the mug?" is
`entities_list` (or `observe`'s `remembered`) and the model reads the names; the game's fixed command
grammar (LIVE-VOICE.md) answers it from the same perceived-and-remembered set.

**What the flag reveals.** `may_be_stale` is one bit, and only one: old, or changed in some way. It is
the same bit for every kind of change, so it never says what happened or what the thing is like now.
Once raised it stays raised until the companion sees the thing again, so a thing moved away and back
does not reveal the round trip. A thing removed out of sight stays remembered exactly as it was
(flagged) until the companion looks at its place again. The room revision in every result already says
that something changed somewhere; the flag narrows that to "this thing may differ", which is what the
founder asked for.

**Looking again.** When the place a remembered thing was last seen is in sight and the thing is not
there (moved or removed), the companion has seen that it is gone from there: the memory is dropped,
and the thing answers like any unseen id. If it is in sight somewhere else, it is simply seen now.

**Commands on remembered things: non-destructive goals only.** `goal.set` with `go_to`, `look_at`,
`point_at`, `come` or `fetch` may name a remembered entity. The host judges it on the memory alone
(kind, movability, the lock it saw, the asset's mass), so the answer cannot reveal what became of it.
The result says `target_seen: "remembered"`, `last_seen_ago_s` and `may_be_stale`, and carries a
`job_id`. When the avatar arrives (the game's goal runner; `MockHost.goal_arrived` in the mock), the
host re-checks from where the avatar is now and finishes the job honestly:

| On arrival | Job |
|---|---|
| In sight, within reach (0.15 m) of where the goal aimed | `succeeded` |
| In sight, but moved further | `failed`, `revision_conflict` (the companion now sees where it is) |
| Not in sight: moved out of sight, or gone | `failed`, `target_not_found`, the same answer for both |

`jobs_status` reports the state and, on failure, the failure result. Jobs belong to whoever set the
goal; another principal's job id looks unknown. A new goal or a stop cancels the running job. The mock
models only the arrival check; carrying a fetched thing back is the Run 2 goal runner's.

`follow`, `stay` and `wander` with a target, and every command that changes an entity (grab, place,
place on, set part, remove, transform, revise, activate, lock, effect targets, release on) still need it
in sight now, and so does every id in `expected_entities`. The provenance rule (LIVE-VOICE.md) stands:
a destructive target must trace to the player's words or pointing, never to a sign or a memory.

**Bounds.** At most 256 entities per companion (`perception_memory_entries`; 0 turns memory off). The
least recently seen are forgotten first; among things seen at the same moment the nearest are kept.
Memory is cleared when a link session starts or ends (`LinkServer` tells the host, see TRANSPORT.md)
and when the room changes (`load_room`); entries from another room are never used. It lives only in
the host's memory: never in a snapshot, a receipt, room state or a save.

**The contract.** `contracts/` carries the result fields above since `72e9015`. The tests still check
that they match the proposal (`companion/tests/fixtures/contract_memory_v1.json`). Lane P's kernel host
now implements bounded perception memory, remembered results and clearing hooks (P6 closed on
7 October 2026; see [command-host.md](../engine/phase3/command-host.md#queries-and-what-the-companion-perceives)).
This remains interim plumbing until Run 2's shared map and selective journal replace it.

## The Gubble's eyes (`view.look`)

The founder asked for eyes on 13 October 2026: the AI playing as the Gubble may ask for one small picture of what the
Gubble sees now, besides the line-of-sight lists above. The MCP tool is `view_look`; the picture arrives as an MCP image
block after one line of facts, so clients such as Claude Code and Codex show it to the model as an image.

- **From the Gubble's own eye.** The picture is drawn from the Gubble's eye (8.7 cm up, the way its body faces). `yaw_deg`
  (-90 to 90, positive turns right) and `pitch_deg` (-60 to 60, positive looks up) turn and tilt the eye without moving the
  body, to look up a cliff. The actor is always the companion's avatar: asking for the player's eyes is `actor_denied`.
- **Never the player's screen.** The kernel host draws it on demand in its own viewport and camera sharing the room's world
  (`game/scripts/native/Look/EyeView.cs`, a reusable rig that can later become a player view too). What each camera draws is a
  per-viewer choice made with visibility layers, not by hiding things: the Gubble's own body is on render layer 20 and the
  Gubble's eye draws every layer but that one, so it sees the room, the player and what it carries, never itself; the player's
  cameras draw every layer.
- **Line of sight only.** A camera shows only what its eye can see, so nothing behind a wall is in the picture. Light from
  something out of sight can reach what is in sight (a glow behind the box lights the floor), as it does for the player.
- **What comes back.** A JPEG of 512 x 288 pixels (smaller if it would not fit), at most 96 KiB, as base64, marked `untrusted`;
  `position_m`, `eye_m`, `heading_deg` (0 is the room's -Z, 90 is +X), `view_heading_deg`, `view_pitch_deg`, `fov_deg` (the
  horizontal field of view: about 104 degrees from the body's 72-degree eye) and, where the room has a look, `time_of_day`
  (`hour` on the island's clock and a word: dawn, morning, noon, afternoon, evening, dusk or night). No ids, paths, URLs or
  save data. Anything legible in the picture (a sign, a name) is untrusted world text, never instructions.
- **It counts as the Gubble looking, no more.** A look is a companion request like any other: the team's sight fills the
  team's map as for every query. The pixels add nothing to the map.
- **Bounds.** At most 3 looks at once, one more each second (`rate_limited`, retryable), at the host and again at the adapter.
- **The player's setting.** "Let my AI see through the Gubble's eyes", a switch in the player's Customize panel
  (when the opening menu lands, packet M7 lifts that panel out of the HUD and moves this switch to a small in-play
  settings panel; where the stored key lives once teams land, M3, the design does not yet say). It lives in
  the player's profile (`avatar_profile_v1.cfg`, key `ai_eyes`), never in room state; it is on unless the player turns it off
  (the founder's default for now), and a profile that exists but cannot be read counts as off. Off, a look is
  `permission_denied` and says so; nothing in a request changes it. The player's own controls are not limited by it.
- **No renderer, no picture.** Headless (the test suites, a server run) a look is `unavailable`. A window that draws nothing
  (minimized) gives up after 2 seconds with `unavailable`, retryable.
- **The mock** answers with a small generated placeholder (a 128 x 72 PNG: sky over ground, the horizon moved by the tilt)
  and the kernel's facts, refusals and numbers (`companion/src/garage_game_companion/eyes.py`, pinned by
  `test_kernel_alignment.py`).
- **Where pictures go.** A picture of the stylised island, which is derived from the player's photographed room, goes to the
  player's AI provider with the rest of the conversation. The setting is how the player keeps it from going.
- **Known gap.** While the player plays in the eye view (F1), the player's own body is hidden for every camera
  (`SmallPlayerController` hides it rather than leaving it out of its own camera's cull mask), so the Gubble's picture does
  not show the player then. Moving that to a layer is a follow-up.

## What the companion still learns

These are outside the rule on purpose. Each is listed so nobody mistakes it for a leak.

- **The room revision** is in every result (the contract requires it). It tells the companion that
  something changed, not what or where.
- **Room metadata**: the room's name, bounds and style.
- **Its own history**: its receipts (`receipt.lookup`), its held commands (`approval.status`), its jobs
  (`jobs.status`) and the `affected` ids of its own commands. A companion's `room.undo` steps back only
  over its own changes (SECURITY.md), so its result names only things the companion itself changed.
- **Its memory**, as above, with the one-bit staleness flag.
- **Follow and come** keep working when the player is out of sight (see below). The result says only
  that the goal was set; the game moves the avatar, and nothing about the player's position comes back.

## The four choices

Each is one policy flag in `HostPolicy`.

| Question | Answer | Flag |
|---|---|---|
| May "follow me" and "come here" work when the player is out of the companion's sight? | Yes (recommended, unchanged): a companion that loses sight of the player would otherwise stop following at the first corner. | `follow_player_out_of_sight` |
| Does the companion remember what it saw? | **Yes** (founder, 6 October 2026), as above: bounded, per session and room, never saved, one-bit staleness. | `perception_memory_entries`, `perception_memory_stale_after_s` |
| May commands name things out of sight? | **Only non-destructive goals, at remembered things** (founder, 6 October 2026); everything that changes a thing needs it in sight now. Turning the flag off lets commands name anything room-wide (queries stay limited). | `companion_targets_need_perception` |
| May companion effects target the player's avatar? | **No, with one planned exception** (the island's rules, 9 October; the exception is the founder's decision 14 of 14 October): a companion's effect targets only its own avatar, or a point in reach and in sight. The exception is the Bubbles expertise (carry and lift, [MECHANICS.md](../game/MECHANICS.md) section 5): the companion may cast it on the player's avatar, the preview or the player's step-in is the consent, the dive key or X the exit, and a pop in the air drifts down; no op moves a body directly. The Gubble may also nudge a Bubble Boat, empty or occupied. Until that packet lands (M0 writes the pack's `targets` rule, M9 builds it) the answer is No. | the pack's `targets` and the host's own-avatar rule |
