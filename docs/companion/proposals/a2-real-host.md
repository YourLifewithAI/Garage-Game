# A2 change requests: the real host's wiring, go_to, and what fetch still needs

Files this lane does not own. Each patch applies on `run2/integration` `0e39374` (`git apply --check`), from the
stored blob so line-ending conversion cannot touch it:

```sh
git show run2/companion:docs/companion/proposals/a2-roomworld-wiring.diff | git apply
git show run2/companion:docs/companion/proposals/a2-go-to.diff | git apply
```

## 1. RoomWorld attaches the companion bridge (the integrator: `RoomWorld.cs`)

[a2-roomworld-wiring.diff](a2-roomworld-wiring.diff): `using EnFractal.Native.Companion;` and
`CompanionBridge.Attach(this);` right after `Kernel.CommandHost.Attach(this);`. (Spelled through the `using`: inside
`RoomWorld`, `Companion` names the avatar property, not the namespace.)

**Why:** the game itself then listens for the player's AI: `run-room.ps1` and the shipped game write the session file
the MCP server reads, and `python -m garage_game_companion.profile` with no arguments connects to the running game.
Until then only `companion_room.tscn` (and `python -m garage_game_companion.real_game`) serve the link.

**Checked with the patch applied** (8 October): the C# build has no warnings; `tools/test-room.ps1`'s room boot
(`room.tscn --quit-after 240`, headless) prints `COMPANION_LINK listening ...` with nothing on stderr and removes its
session file on quit; `companion_room.tscn` then finds that bridge and starts no second link. The boot writes the
session file under the runners' redirected user folder (`.cache/native-test/appdata`), never the player's.
`--no-companion-link` turns it off for a run.

## 2. go_to: a go-to for the body, watched by the host (Lane P: `CompanionAvatar.cs`, `Kernel/CommandHost.cs`, `CommandHostTest.cs`)

[a2-go-to.diff](a2-go-to.diff):
- **`CompanionAvatar.GoTo(Aabb target, float arrivalM)`** (P2, body and movement): a `go_to` intent that routes round
  furniture as come does, toward the nearest point of the box's footprint, and stays once the body's centre is
  within `arrivalM` of it, setting `GoToArrivedSerial`. Blocked is reported as `GoalBlocked`, as for come.
- **`CommandHost`** (P1): `go_to` is no longer `unsupported_capability`; with a target it walks to the target's bounds
  as seen or remembered, with a place to that point, stopping 8 cm from the footprint (`GoToStopM`, within the body's
  reach); `go_to` joins the host-driven goals, and its job finishes through `ReportArrival` when
  `GoToArrivedSerial` matches, with the usual re-check. `fetch` stays refused, now saying it waits for P3.
- **`CommandHostTest`**: the check that pinned go_to as unsupported now checks that it may aim at the remembered
  doorstop (a job, judged on the memory); fetch and coming to a thing still wait.

**Why:** fetch walks to the thing before it picks it up, and the body had no way to walk anywhere but to the player.
go_to is the first half of fetch and is useful by itself ("go to the box").

**Checked with the patch applied:** build clean; `native_kernel_command_host.tscn` 421/421 (one check more than
today); `companion/tests/test_real_host.py`'s `test_go_to_walks_the_companion_to_a_thing` passes against the real
host (the companion walks to the doorstop, ends within 10 cm of its footprint, and comes back); without the patch it
skips. Not checked: a target on top of furniture (the footprint is planar, so the body can stop under a table; the
host's arrival re-check then fails honestly with `target_not_found`, because it cannot see the thing from there).

## 3. Fetch on the real host: what remains (Lane P, after P3)

The design and the mock are in [EMBODIMENT.md](../EMBODIMENT.md#fetch); `companion/tests/test_fetch.py` pins the
lifecycle. With P3's grab and holding in the host, and the go-to above, the host's part is:

1. **Un-gate `fetch`** in `Unsupported`.
2. **Set** (`GoalSet`, `case "fetch"`): apply P3's `entity.grab` checks to what the companion sees or remembers
   (object or creation and movable: `permission_denied`; `target_protected`; held by another: `target_busy` at
   `$.args.target`; the companion holding something else: `target_busy` at `$.args.actor`; over the carry limit:
   `target_too_heavy` with `allowed` and `actual`). Already holding the target: start in the return phase. Then
   `Companion.GoTo(aim, GoToStopM)` and a job in phase "approach".
3. **Pick up** (`_PhysicsProcess`, on `GoToArrivedSerial` for a fetch in "approach"): the arrival re-check that
   `ReportArrival` does today, then P3's grab under the job's principal with `avatar:companion` as the actor. A
   refusal fails the job with it (`job.Result` under the goal's action id). Picked up: phase "return",
   `Companion.Come()`, and the job's `Serial` becomes the come's `IntentSerial` (so `GoalJob.Serial` needs a setter).
   Nothing about the pick-up reaches the AI but `held_by` in `entity.inspect`.
4. **Back** (on `ComeArrivedSerial` matching the job's serial in "return"): still held by the companion, the job
   succeeds; otherwise it fails with `target_not_found`. The companion keeps holding it; `entity.release` puts it down.
5. **Stops and new goals** cancel the job in either phase, as now; holding is untouched.
6. **Optional:** fail a fetch or go_to whose body has reported `GoalBlocked` for 5 s. The contract has no
   "unreachable" code; `out_of_bounds` with "The companion cannot get there from here." is the suggestion, with a
   contract addition (`target_unreachable`) left to the integrator.

Then `test_real_host.py`'s `test_fetch_through_the_real_host` stops skipping and runs the whole lifecycle (fetch the
doorstop, the job succeeds, `held_by` is the companion, `entity.release` puts it down).

## 4. Runners and ownership

- **No runner change is needed.** The companion suite now includes `test_real_host.py`, which starts one headless
  Godot with the build the runners have just made (`run-engine-tests.ps1` builds before the suite;
  `tools/linux/test-all.sh` builds first too and its Godot is found under `.cache/linux/godot`). It gives the game a
  temporary user folder of its own and quits it cleanly. The suite takes about 100 s instead of about 50.
- **Ownership:** everything this round added is under `companion/**`, `game/scripts/native/Companion/**` and
  `docs/companion/**`. `companion_room.tscn` lives with its script; move it to `game/scenes/` if scenes should live
  there, and update `garage_game_companion/real_game.py`'s `SCENE`.
