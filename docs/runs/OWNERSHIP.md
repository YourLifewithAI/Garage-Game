# Who owns which files

Squared with [docs/game/MECHANICS.md](../game/MECHANICS.md) on 14 October 2026: where this page and that design differ, the design wins.

Every run is built by parallel agent lanes. A lane **edits only the paths it owns** and may read anything. Shared files belong to the integrator, who merges lanes, keeps contracts and consumers in step, and runs the full suite. This map is what keeps four lanes from editing the same file at once. When a path is not listed, it belongs to the integrator.

## Integrator (shared files)

| Path | Why it is shared |
|---|---|
| `contracts/**` | The agreements between tracks; see [contracts/README.md](../../contracts/README.md) |
| `AGENTS.md`, `CLAUDE.md`, `README.md`, `docs/ROOM-SCALE-DIRECTION.md`, `docs/runs/**`, `docs/CLEANUP-PLAN.md` | Plans and agent rules |
| `game/rules/**` | Island rules packs (`contracts/island-rules.schema.json`), written through contract rounds |
| `game/project.godot`, `game/export_presets.cfg`, `game/EnFractal.csproj`, `game/EnFractal.sln`, `game/global.json` | Project-wide settings |
| `.gitattributes`, `.gitignore` | Line-ending pins and ignore rules |
| `run-engine-tests.ps1`, `run-room.ps1`, `tools/*.ps1`, `tools/native-toolchain.lock.json`, `tools/linux/**` | Toolchain and the test lists every lane runs |
| `game/scripts/native/RoomWorld.cs`, `game/native/**`, `game/scenes/room.tscn`, `game/scenes/native_*.tscn` | Composition root, boot and release probe |
| `game/scripts/native/Seams/**` | Interfaces two lanes share (the stamina ledger between the kernel and the body, M0 of the mechanics round): a change is a contract change |
| `tools/rooms/**`, `game/rooms/test_room/**` | The placeholder room (regenerate with its builder; lanes request changes) |
| `tools/playtest/**` (the playtest reader: `report.py`, its fixture and tests) | The integrator, with R2's Sonnet fix rounds ([RUN-2-PLAYTEST-LOOP.md](RUN-2-PLAYTEST-LOOP.md)); `.claude/skills/playtest/` and `docs/runs/playtests/**` are the integrator's |
| `game/tests/native/RoomDataTest.cs`, `game/tests/native_room_data.tscn` | Contract-consumption fixture |
| `docs/engine/persistence-notes.md`, `docs/engine/quality-gates.md`, `docs/engine/decisions/**` | Cross-track rules |
| `docs/history/**`, `docs/research/**` | Read-only context; nobody edits history |

## Track L: Look

| Path | Packets |
|---|---|
| `docs/look/**` (look bible, review captures, scores) | L1, L7 |
| `game/styles/**` (presets) | L1, L3, L5, L6 |
| `game/shaders/**` | L3 |
| `game/scripts/native/Look/**` (look director, material library, preset reader) | L2, L3, L5 |
| `game/scripts/painterly_assets.gd` | L3; keep its public function names, the creation visuals call them |
| `game/assets/art/painterly/**`, `assets/art_sources/painterly/**`, `tools/art/**` | L4, reference art |
| `tools/look/**` (review cameras, capture harness, mesh treatment) | L4, L7 |
| `game/tests/native/Look/**`, `game/tests/native_look_*.tscn` | L tests |

## Track C: Capture

| Path | Packets |
|---|---|
| `pipeline/roomscan/**` (Python package, MCP server, SKILL.md, tests) | C1 to C7 |
| `docs/pipeline/**` | C design notes and compute costs |
| `captures/` (local photos and intermediate data; **ignored by Git, never committed**) | C1 to C5 |
| Captured room exports go to the player's user data, `user://rooms/<room_id>/` (on Windows under `%APPDATA%\Godot\app_userdata\Garage-Game\rooms\`), never into the repository; Git ignores `game/rooms/*` except `test_room` | C6 |

## Track P: Play

| Path | Packets |
|---|---|
| `game/scripts/creation_*.gd`, `game/scripts/invention_*.gd`, `game/scripts/world_state.gd`, `game/scripts/world_physics_profile.gd` | P1 |
| `game/scripts/native/Kernel/**` (C# side of the kernel and the command host; the integrator wires its `CommandHost.Attach(RoomWorld)` entry point into `RoomWorld.cs` at merge) | P1 |
| `game/tests/native/Kernel/**`, `game/tests/native_kernel_*.tscn`, `game/tests/fixtures/kernel/**`, `game/tests/fixtures/creations/**`, `tools/kernel/**` (command-host tests, golden canonical-JSON fixtures and their Python reproducer, the neutral creation fixtures) | P1 |
| `game/native/WorldScaleProfile.cs`, `game/scripts/native/SmallPlayerController.cs`, `game/scripts/native/CompanionAvatar.cs` (body and movement only), `game/scripts/native/Navigation/**`, `game/tests/native/RoomNavigationTest.cs`, `game/tests/native_room_navigation.tscn` | P2 |
| `game/scripts/native/Room/**` (room data and builder) | P6, from Run 2 |
| `game/scripts/native/Sandbox/**` | P3, from Run 2 |
| `game/scripts/native/Saves/**` | P4, from Run 3 |
| `game/scripts/native/RoomHud.cs`, `game/scripts/native/Ui/**` | P5, from Run 3 |
| `game/scripts/native/Playtest/**`, `game/tests/native/Playtest/**`, `game/tests/native_playtest.tscn` (the playtest recorder; the integrator wires its `PlaytestRecorder.Attach(RoomWorld)` into `RoomWorld.cs`) | R1, the playtest loop ([RUN-2-PLAYTEST-LOOP.md](RUN-2-PLAYTEST-LOOP.md)) |
| `game/tests/*.gd` (kernel suites), `game/tests/native/SmallAvatarPhysicsTest.cs`, `game/tests/native_small_avatar.tscn` | P1, P2 |
| `docs/engine/phase3/**` | P1 kernel records |

## Codex (GPT), a contractor

| Path | Notes |
|---|---|
| `codex/<brief>` branches only | Never `main`, a `run<n>/` branch or a lane branch |
| The files in its brief's `scope` block, normally `docs/codex/reports/<brief>.md` | `tools/codex/check_scope.py` checks every branch before the integrator merges it |
| `pipeline/recipes/**` (parametric Blender recipes for stand-ins, their runner and tests; from Run 2, brief 10) | The integrator owns it and Codex briefs build it. Lane C reads and runs the recipes for C5 and requests changes |
| `pipeline/landscape/**` (the room-to-landscape conversion: its synthetic room corpus from brief 14, the shared render harness from brief 16) | The integrator owns it and Codex briefs build it, except the parts below |
| `pipeline/landscape/generator/**` (C5, the landscape generator, continued from the A/B winner `gen_b`), and the harness's rendering side (`harness/worker.py`, `harness/views.py`, `harness/render.py`, its README and reference renders; not the package format: `package.py`, `glb.py`, `common.py`) | Lane C (from the fourth Run 2 session, 8 October, as the revised Run 2 plan assigns it) |
| `pipeline/landscape/export/**` (a landscape package to a game room directory) | Codex brief 18. The mechanics round (M4 in [RUN-2-MECHANICS.md](RUN-2-MECHANICS.md)) needs a bounded whitelist here for the Fractal, route and tutorial fields (`x_fractals`, `x_island_tutorial`), and the generator lane cannot write the manifest: the lane reports the diff and the integrator applies it (Codex, which built this folder, is retired) |
| `docs/ROOM-TO-LANDSCAPE.md` | Integrator-owned design; Codex drafts it in brief 13 |
| `pipeline/characters/**` (drawing-to-character converters; the shared turntable `turntable.py`, which every converter's output is judged through) | The integrator owns it. In brief 20's blind A/B each contender owns one folder (`conv_a/`, `conv_b/`) until the verdict |

The integrator owns `docs/codex/README.md`, `docs/codex/briefs/**` and `tools/codex/**`. Codex works on research, test runs and audits, never on contracts, the kernel, the companion's server or the Look lane's GPU work. See [docs/codex/README.md](../codex/README.md).

## Track A: AI companion

| Path | Packets |
|---|---|
| `companion/**` (model-neutral MCP server for the game command surface, mock host, boundary tests) | A1 |
| `game/scripts/native/Companion/**` (in-game bridge endpoint and goal runner; drives CompanionAvatar through its public API) | A1, A2 |
| `docs/companion/**` | A notes |

## Track K: Characters (from the characters round, 13 October)

| Path | Packets |
|---|---|
| `game/scripts/native/Characters/**` (the rig and library, the gaits, contact, the Gubble's port; one agent per subfolder) | K1, K3, K4, K5 ([RUN-2-CHARACTERS.md](RUN-2-CHARACTERS.md)) |
| `assets/characters/**` (the shipped characters and their `rig.json` sidecars; the models themselves change only by rerunning the converter) | K1; the integrator for new characters |
| `game/tests/native/Characters/**`, `game/tests/native_characters*.tscn` | K tests |
| `docs/characters/**` | K notes |

The bodies' own files stay Lane P's: `SmallPlayerController.cs` and `CompanionAvatar.cs` take the round's hooks from the integrator, and K5 edits `CompanionAvatar.cs` only on the run-specific grant the plan names.

## Rules for crossing a boundary

- **Need a change in a file you do not own?** Write the exact diff and the reason in your lane report. The integrator applies it or hands it to the owning lane. Do not edit it yourself, even for a one-line fix.
- **Need a contract change?** Same: propose it. Contracts change in one integrator commit with their examples, consumers and tests.
- **Dependencies** pinned in a lockfile inside your owned directory (for example `pipeline/roomscan/pyproject.toml` with `uv.lock`, or `companion/pyproject.toml`) are pre-approved. Global installs, new services and paid APIs are not.
- **New tests** go in your owned test paths. Ask the integrator to add them to `run-engine-tests.ps1`, `tools/test-room.ps1` or `tools/linux/test-all.sh`.
- **New directories** under an owned path are yours. A new top-level directory needs the integrator.
- **Generated files** (rooms, examples, `.uid` sidecars Godot creates for new scripts) are committed by whoever owns the generator or the new script.
- **Run-specific grants** in a run brief (for example, the Look lane changing the renderer line in `project.godot`) apply only to that run and only to the lines named.
