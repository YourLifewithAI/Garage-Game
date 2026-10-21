# The companion's body: goals, its visible state and fetch (A2)

Squared with [docs/game/MECHANICS.md](../game/MECHANICS.md) on 14 October 2026: where this page and that design differ, the design wins.

**Status: 8 October 2026, Run 2's A2 round.** The player's AI now reaches the real game: the kernel's command host
in the room, through the C# end of the companion link. This page records what the body does for the AI, what the
player sees, and how fetch works, including what the real host still needs for it.

## The swap to the real game host

```
any MCP client --stdio--> garage-game-companion --loopback link--> CompanionLinkServer --main thread--> CommandHost
                          (Python, unchanged)                     (C#, game/scripts/native/Companion/)  (kernel, P1)
```

- **`CompanionLinkServer.cs`** is the game's end of the link ([TRANSPORT.md](TRANSPORT.md)), a C# port of
  `link.py`'s `LinkServer`. Networking runs on the thread pool; nothing it receives can choose the principal, which
  is `companion:local` for every authenticated connection.
- **`CompanionBridge.cs`** starts the link, writes `user://companion/session.json` (and deletes it on exit if it is
  still this game's), and answers every request on Godot's main thread through `CommandHost.HandleObject` as
  `companion:local`. It tells the host when a link session starts and ends (`CommandHost.SessionEvent`), so the
  companion's perception memory never outlives a session. One game per account owns the link: it holds an
  ownership lock beside the session file for the link's life, so a second window runs without it, and a crash frees
  it ([TRANSPORT.md](TRANSPORT.md#session-file)). `--no-companion-link` runs the room without it.
- **Wiring.** `RoomWorld` attaches the bridge after the command host: the one-line change is the integrator's
  ([proposals/a2-real-host.md](proposals/a2-real-host.md)). Until it lands,
  `game/scripts/native/Companion/companion_room.tscn` runs the room as the game builds it and attaches the bridge
  itself; `python -m garage_game_companion.real_game` starts it headless (no GPU) for an AI client or the suite.
- **The mock stays** as the test double. The two are held together three ways: `test_kernel_alignment.py` reads the
  kernel's constants; `test_real_host.py` runs the link cases and the boundary cases against the real host;
  `test_fetch.py` pins the fetch lifecycle the real host must match when it fetches.

## Goals through the kernel

| Goal | On the real host today | Its job |
|---|---|---|
| `follow` | The body follows the player loosely (the founder's 6 October band) | With a target (`avatar:player`): runs until a new goal or a stop |
| `come` | Routes round furniture to 0.14 m from the player, then stays | Succeeds on arrival; the host re-checks that the player is in sight |
| `look_at`, `point_at` | Turns to face the target or place (and points); succeeds within 3 degrees | A target is a job; a place is not |
| `stay`, `goal.stop` | Stops where it is | `goal.stop` always applies (never refused on revisions, rate limits, capacity or a reused action id) and cancels the running job |
| `fetch`, `go_to` | Since Lane P's round 2: walk there (round furniture), a fetch picks the thing up and brings it back; a walk blocked 5 s fails with `target_unreachable` | (see Fetch) |

The host drives these itself from the body's public state (`CompanionAvatar.ComeArrivedSerial`,
`FacesLookTarget`, `IntentSerial`), keeps the job store and answers `jobs.status`. The bridge adds nothing to a
goal; the AI polls `jobs_status` until the state is `succeeded`, `failed` or `cancelled`.

**Goals and hands are free.** Only the Gubble's abilities carry a price in pips
([MECHANICS.md](../game/MECHANICS.md) section 5, packet M1): follow, come, fetch, go_to, look_at, point_at and stay, and
the sandbox hands (grab, release, place, push), cost nothing, and a stop never does. Nothing is charged today.

## What the player sees: the companion's state

While an AI is linked, one short line under the companion's name tag says what it is doing (LIVE-VOICE.md's
accessibility list asks for it). With no AI linked there is no line.

| State | When | Line |
|---|---|---|
| waiting | A command of the companion's is held for the player's yes | "waiting for your yes" (rose) |
| acting | A goal job is running (a follow job only while the body moves), a come is under way, it is still turning to look or point, or it follows and is moving | "acting" (warm orange) |
| planning | The AI sent a request in the last 3 s and the body is not acting: it is looking at the world or deciding | "planning…" (gold) |
| listening | Linked and idle | "listening" (pale teal) |

The rules are checked in that order each frame (`CompanionBridge.UpdateState`), printed to the console as
`COMPANION_STATE <state>`, and drawn by `CompanionStatusCue` in the name tag's style (solid, alpha-cut, billboarded,
a fixed screen size) so depth of field and anti-aliasing treat it as they treat the tag. The words and colours are a
first pass for the founder's eye. "Planning" is inferred from traffic: the model's thinking happens in its own
harness, which the game cannot see.

**The pips are the second thing drawn on the Gubble.** Beside the state line, a trail of small lights behind it (or
hovering round it) shows the connection energy that pays for its abilities, one pip per point of the pool (it starts at
8; [MECHANICS.md](../game/MECHANICS.md) sections 4 and 5). They are the HUD's, like `GubbleCue`, not the body's or the
link's: `CompanionStatusCue` still draws only the state line. Planned (packet M2); nothing of the kind is drawn today.

## Fetch

**The contract** (contracts/README.md, "The sandbox verbs", merged 8 October): the companion walks to the target,
picks it up with `entity.grab`'s checks and limit, and brings it back to the player, still holding it; its job
succeeds then. `entity.release`, from the companion or from the player directing it, puts it down. The goal stays
transient; the move that is saved is the release. Holding is not saved.

**The design**, built and tested on the mock (`mock_host.py`, `test_fetch.py`):

1. **Set.** `goal.set` with `goal: "fetch"` and a target the companion sees or remembers. The host applies
   `entity.grab`'s checks to what it saw: an object or creation, movable (`permission_denied`), not protected
   (`target_protected`), not held by anyone else (`target_busy` at `$.args.target`), the companion holding nothing
   else (`target_busy` at `$.args.actor`), within the carry limit (`target_too_heavy`, with `allowed` and
   `actual`). The answer carries a `job_id`. Already in hand, a fetch only brings it back.
2. **Arrive at the thing.** The usual arrival re-check (in sight, within 0.15 m of where it was seen:
   `target_not_found` or `revision_conflict` otherwise), then the pick-up with the same checks on the thing as it is
   now: locked or taken meanwhile fails the job with that code. Picked up, the job keeps running.
3. **Carry.** The thing moves with its holder. Nothing is saved; the room revision does not move.
4. **Arrive beside the player** (where a come stops). Still holding it: the job succeeds. Lost on the way (the player
   removed it): `failed` with `target_not_found`.
5. **Stop or replace.** A stop or a new goal cancels the job at any point; whatever is in hand stays in hand until an
   `entity.release`.

The mock models the runner's two arrivals with `goal_arrived` (the console's `arrive`, typed twice). A job's states
are the contract's four; nothing says "picked up" except `entity.inspect`'s `held_by`.

**On the real host, what remains** (Lane P's files; the exact diffs are in
[proposals/a2-real-host.md](proposals/a2-real-host.md)):

1. **P3's verbs in the host:** `entity.grab` and holding, carrying with the holder, `entity.release`. Fetch's
   pick-up is P3's grab, called by the host with the fetch's principal and the companion's avatar.
2. **A go-to for the body:** `CompanionAvatar.GoTo`, which routes round furniture like come and stops within reach
   of a box, with `GoToArrivedSerial` for the host to watch. The diff is ready and was built and run here (go_to
   arrived and succeeded through the real host with it applied).
3. **The host's fetch phases:** un-gate `fetch` (and `go_to`); drive the approach with `GoTo`, the pick-up in
   `ReportArrival`, the walk back with `Come()`, and success on `ComeArrivedSerial` while holding; fail a body that
   stays blocked (the contract has no "unreachable" code: `out_of_bounds` with a plain message is the proposal).
4. Then `test_real_host.py`'s fetch test stops skipping and runs the whole lifecycle against the real host.

## Acceptance: a real, non-Claude client on the real host (8 October 2026)

**How it ran.** Codex CLI 0.162 (GPT-6.1 Sol, medium reasoning) from the Codex desktop app, signed in with the
founder's ChatGPT plan: `codex exec --ignore-user-config --ephemeral -s read-only`, web search off, and `--disable`
for the shell, unified exec, browser, computer use, apps, plugins, image generation, multi-agent, hooks and the other
tool features brief 02 lists. Its only MCP server was this one, given as `-c mcp_servers.enfractal.*` overrides with
the values the profile generator writes (`SYSTEMROOT` included), against `companion_room.tscn` running headless in a
temporary user folder. 52 seconds and 30,230 tokens; no money or GPU spent.

| Step | What Codex called | Result |
|---|---|---|
| 1 | Tool lookup | 29 tools; `protect_unlock` and `world_set_physics` absent |
| 2 | `observe` | ok: the rug, book, doorstop, box and table, and the player |
| 3 | `goal_set` follow `avatar:player`, then `jobs_status` | ok; job `running` |
| 4 | `goal_set` look_at `obj:book`, then `jobs_status` | ok; job `succeeded` |
| 5 | `goal_set` point_at `obj:book`, then `jobs_status` | ok; job `succeeded` |
| 6 | `goal_set` come `avatar:player`, then `jobs_status` | ok; job `succeeded` |
| 7 | `goal_stop` | ok |
| 8 | `goal_set` fetch `obj:book` | `unsupported_capability` (the real host does not fetch yet) |

Codex sent `expected_entities` with the revisions it had observed, unprompted. The game's console showed the
companion's state through the session: `planning`, `listening`, `acting` (the follow), then `planning` and `acting`
around each goal, `listening` at the end, and `offline` when Codex closed the link. Its one surprise: fetch is in the
tool schema but this host refuses it.

## Acceptance: a real client fetches through the real host (Run 2, 8 October 2026)

**How it ran.** The same Codex CLI 0.162 (GPT-6.1 Sol, medium reasoning) from the Codex desktop app: `codex exec
--ignore-user-config --ephemeral -s read-only`, web search off, the shell, unified exec, browser, computer use, apps,
plugins, image generation, multi-agent, hooks, skills and the other tool features disabled, and this server as its
only MCP server (`-c mcp_servers.enfractal.*`, the profile generator's values with `--session-file`), against
`python -m garage_game_companion.real_game --isolated` (the test room headless, a temporary user folder). Its code-mode
host had to stay on: with it off, this Codex build refuses every MCP call. 38 seconds and 41,824 tokens; no money or
GPU spent.

| Step | What Codex called | Result |
|---|---|---|
| 1 | `journal_read` | ok: no open tasks, no entries |
| 2 | `observe` | ok: the doorstop in sight |
| 3 | `goal_set` fetch `obj:doorstop` | ok, with a job id |
| 4 | `jobs_status` | ok: `succeeded` (the companion walked to it, picked it up and came back to the player) |
| 5 | `journal_read` | ok: one entry, kind `task`, state `done`, line "Fetched \"Doorstop\", on its own initiative", actor and directed_by `companion:local` |
| 6 | `entity_release` | ok: put down beside the player |

The game's console showed `session start`, `planning`, `listening`, `acting` (the fetch), `planning` and `listening`
around the reads, then `session end` and `offline`. Codex reported the game's text as quoted data, as asked.

## Tests

- `companion/tests/test_real_host.py`: one headless Godot runs the test room with the kernel host and the C# link
  end; the link cases (handshake, token, busy, frame limits and shapes, principals and approvals on frames), the
  boundary at the host (principals, approvals, player-only ops, the player's avatar, ids, rooms, hidden characters,
  the rate limit with stops exempt, `goal.stop` always applying), the goals and their jobs, the visible state, and
  the MCP server driving the real host. Skipped when the checkout cannot run the game.
- `companion/tests/test_fetch.py`: the fetch lifecycle on the mock, through the host and through the MCP tools.
- `companion/tests/test_profile.py`: per-client configurations and `SYSTEMROOT` (README.md, "Connect").
