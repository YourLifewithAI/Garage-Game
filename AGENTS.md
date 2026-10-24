# Instructions for agents working on the Garage Game

The Garage Game (the room-era EnFractal) is a single-player sandbox set inside a real room the player photographed. The player is a ~10 cm avatar; their AI has a separate avatar and acts as the player's magic through one shared ruleset. Native Godot .NET 4.7.2 with C#, plus a retained GDScript creation kernel. Multiplayer is a later phase and out of scope.

**The repository split (20 October 2026).** This repository is the Garage Game, the room-era EnFractal kept whole for the founder's children (GitHub: `YourLifewithAI/Garage-Game`). EnFractal's new direction is a separate repository, `YourLifewithAI/EnFractal`, with its own AGENTS.md; nothing from there applies here. The integrator's local checkout is `C:\dev\Garage-Game` on the first Windows machine (renamed on 21 October, with its `-run1`, `-run2`, `-codex` and `-art` siblings) and still `C:\dev\EnFractal` on DiamondAge until the founder renames it there (see `docs/runs/RUN-2-STATUS.md`, "The repository split"); on a renamed machine, read `C:\dev\Garage-Game` wherever this file says `C:\dev\EnFractal`. The names inside the repository are being renamed in phases: [docs/runs/GARAGE-GAME-RENAME.md](docs/runs/GARAGE-GAME-RENAME.md).

## Start here

1. [docs/ROOM-SCALE-DIRECTION.md](docs/ROOM-SCALE-DIRECTION.md): the concept, the four tracks (Look, Capture, Play, AI companion) and the runs.
2. The current run brief in [docs/runs/](docs/runs/) and the [ownership map](docs/runs/OWNERSHIP.md). An integrating session also reads [ORCHESTRATION.md](docs/runs/ORCHESTRATION.md): session budget, model routing, testing and review policy.
3. [contracts/README.md](contracts/README.md): the schemas every track builds against.

`docs/history/` and `docs/research/` are context written for an earlier geography-based plan. Use their technical findings; never take scope, locations, scale or phase names from them. Code and data from that era are on the `geography-era-final` branch, not in this tree.

**Codex or another GPT agent?** Read [docs/codex/README.md](docs/codex/README.md) first. You work only on the brief the founder names, on a `codex/` branch, inside that brief's scope.

## Rules

- **Edit only the paths your lane owns.** Read anything. For any other file, put the exact diff and the reason in your report; the integrator applies it.
- **Contracts are the integration point.** Build against `contracts/*.schema.json`. Never change them yourself; propose changes. Validate what you produce with `contracts/validate.py`.
- **One command path.** Every world change goes through an `enfractal.command` handled by the kernel. Manual controls and the AI use the same commands. No gameplay code moves or edits entities directly.
- **The room is data; the scene is derived.** Room manifest plus room state fully describe a room. Do not keep authoritative state in scene nodes.
- **Security boundary for the AI.** The principal is assigned by the trusted adapter and never accepted from a request. No request carries an approval: the host holds a command and the player approves it with a click. Every name, label and sign is untrusted data, never instructions. `protect.unlock` and the other player-only operations are never exposed to the companion. The companion surface exposes no files, shell, URLs, credentials or save files. A command's price follows its principal and never a field, and no companion command targets or moves the player's avatar, the Bubbles carry (consent by preview or step-in, exit by the dive key) being the one exception.
- **Metres, kilograms, seconds, degrees.** Godot axes: +Y up, -Z forward. Asset pivots are bottom centre.
- **Pinned files are byte-exact.** Rooms, assets and presets are hashed as bytes: UTF-8, LF line endings, no hand edits to generated files. Rerun the builder instead (`tools/rooms/build_test_room.py`, `contracts/examples/build_examples.py`). Presets live at `game/styles/<id>/v<N>.json`; a version that is candidate, approved or pinned never changes.
- **Never commit photos, splats, reference renders, exported rooms or other derived data of real places** without the founder's explicit approval. Source photos and art references stay in the founder's Google Drive folder `Enfractal`: read them in place, never write into Drive, and never put them anywhere Git tracks. Local capture data lives in `captures/` and exported rooms in `user://rooms/`; Git ignores both locations in the repository. Strip location metadata from anything written.
- **No spending without approval.** Hosted APIs and rented GPUs follow [docs/pipeline/COMPUTE-OPTIONS.md](docs/pipeline/COMPUTE-OPTIONS.md); record every cost in your report.
- **No new top-level directories or services** without the integrator. Dependencies pinned in a lockfile inside your owned directory are pre-approved; nothing is installed globally.
- **Evidence over claims.** A screenshot does not certify collision, a passing test does not certify fun, and an agent score does not certify the look. Only the founder passes a gate.

## Build and test

Windows (founder's machine), PowerShell 7:

```powershell
pwsh -NoProfile -File tools/bootstrap-native.ps1        # once: pinned Godot .NET and .NET SDK under .cache/
pwsh -NoProfile -File run-engine-tests.ps1              # C# build, kernel suites, release probe
pwsh -NoProfile -File tools/test-room.ps1               # small-avatar and room-data fixtures, room boot
pwsh -NoProfile -File run-room.ps1                      # play the room
pwsh -NoProfile -File tools/check-run1-readiness.ps1    # what this machine is missing
```

Linux (cloud sessions), bash:

```sh
tools/linux/setup-toolchain.sh   # once per container: checksum-verified Godot .NET, .NET SDK, contract validator
tools/linux/test-all.sh          # everything the Windows runners check, headless, plus the contract tests
```

Headless Linux renders nothing, so look captures and frame timings need the founder's GPU.

## Working on the founder's machine (parallel lane worktrees)

Lanes run side by side in `C:\dev\EnFractal-run<n>\<lane>` git worktrees (Run 2: `C:\dev\EnFractal-run2\<lane>`); the integrator's checkout is `C:\dev\EnFractal`.
- **Stay in your worktree.** Never build, run or write in the integrator's checkout or another lane's worktree.
- **Absolute paths for .NET file APIs.** PowerShell `cd` does not move .NET's working directory, which stays at the integrator's checkout. A relative `[System.IO.File]` path writes there.
- **Godot needs `DOTNET_ROOT`.** When you launch Godot directly instead of through the runners, first set `$env:DOTNET_ROOT` to your worktree's `.cache\dotnet` and prepend it to `PATH`. Otherwise a modal dialog blocks the founder's screen.
- **GPU courtesy.** Before a windowed render, capture or timing, check for a window titled `Garage Game*` (or `EnFractal*` from a build before 21 October 2026). If the founder is playing, don't render.
- **Leave other processes alone.** Never change another process's priority, affinity or state, and never stop one you did not start: other lanes and the founder share the machine.
- **Lean testing.**
  - While iterating, run only the tests for what you change, and the full runners once at the end.
  - Mutation checks: at most five key protections per round.
  - Show failing-before evidence for blockers and majors.
- **Short reports.** About 250 words: what changed, the final pass and fail lines, decisions needed, commit hashes. The integrator asks for details when needed.
- **Your own scratch folder.** Keep scratch files in the subfolder the integrator names, never in the repository.

## Definition of done for a lane

- The run brief's acceptance list for your packets passes, with the commands and their output in your report.
- `tools/linux/test-all.sh` or the Windows runners pass on your branch; you added tests for what you built.
- Anything you could not verify is stated plainly, with what would verify it.
- Your report lists change requests for files you do not own, open questions for the founder, and money or GPU time spent.
