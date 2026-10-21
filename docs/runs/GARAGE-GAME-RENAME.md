# Renaming EnFractal to the Garage Game inside the repository (planned 21 October)

**Read with** [AGENTS.md](../../AGENTS.md) and [RUN-2-STATUS.md](RUN-2-STATUS.md), "The repository split". **Status: started 21 October 2026 on the first Windows machine at the founder's word ("Can we begin the process of correcting all the names in the repo? DiamondAge isn't working on this right now, and I will update it later.").** Phase 1 is merged (`669d77c`); the founder's choices are below; phases 2, 3 and 4 follow in that order.

The repository is `YourLifewithAI/Garage-Game` since the split, but the code still calls the game EnFractal about 3,000 times in 1,100 files. Those uses are of four kinds with very different risks, so the rename runs in phases, each tested and merged on its own. The name EnFractal now belongs to a different game (`YourLifewithAI/EnFractal`, the planetoids), so nothing here may keep it where a person or the AI reads it.

## The inventory (21 October, `3840526`)

| Kind | Where | Count | What breaks if it changes carelessly |
|---|---|---|---|
| Names people and the AI see | window titles, the menu's title, the companion's instructions and result headers, sandbox messages, the README and AGENTS titles | about 30 | nothing in data; tools that look for a window titled "EnFractal*" stop seeing the game |
| Code identifiers | the C# namespace `EnFractal.*` (385 files, 1,229 uses), `EnFractal.csproj`, `EnFractal.sln`, the assembly name, the companion's package `enfractal_companion` (96 imports) and its commands `enfractal-companion*`, the custom agent file name, `EnFractal.exe` | about 1,800 | AI clients registered with the old command or package names stop connecting until they are registered again; Godot's C# bindings need a clean rebuild |
| The save folder | `config/name="EnFractal"` decides `user://` (`%APPDATA%\Godot\app_userdata\EnFractal`); the companion's link, the playtest reader, the roomscan exporter, the Linux suite and their tests find saves there | 1 setting, about 15 paths | every team, island save, installed room, playtest and the companion's session file is orphaned unless moved |
| Data-format identifiers | 20 schema ids (`enfractal.result` 232 uses, `enfractal.command` 154, `enfractal.room` 52, `enfractal.team`, `enfractal.room_state` and 15 more) in the contracts, every example and fixture, every saved island, team file and installed room, the canonical JSON fixtures | about 800 | every installed island and save stops loading, every byte-pinned file's hash changes, and the companion protocol changes for both hosts at once |

Plus the docs' local paths (`C:\dev\EnFractal`, `C:\dev\EnFractal-run2\...`): right for DiamondAge until its folder is renamed, wrong for the first Windows machine since 21 October.

## The phases

1. **What people and the AI see** (built 21 October, `run2/rename-1`). Window titles "Garage Game" and "Garage Game - <island>"; the menu's title; "Another Garage Game window holds the companion link"; the companion's instructions to the AI, its result headers, its sandbox messages and its command-line help; the README's and AGENTS.md's titles. The tools that refuse to capture while the game is open (`capture-look.ps1`, `capture-play.ps1`, `character_sheet.ps1`, roomscan's `game_window_open`) accept a window titled either "Garage Game..." or "EnFractal...", so a build from before the rename still counts as open. No identifier, path, folder or data format changes.
2. **Code identifiers** (decision A). One sweep: the namespace, the project and solution files and the assembly name (with `.godot/mono` rebuilt), the companion's package and commands, the custom agent file, the exported exe, the generator strings in GLB metadata, the reflection strings in tests. Then the founder's AI clients are registered again with `python -m <package>.profile`.
3. **The save folder** (decision B). Either keep `user://` where it is, or move it once, on the first launch of the renamed build: copy the whole folder, verify every file, leave the old one in place, and point the companion, the playtest reader, roomscan and the Linux suite at the new one in the same commit.
4. **Data-format identifiers** (decision C). Either keep them as file-format names, or rename them with readers that accept both for a release, a migration of installed rooms and saves, regenerated fixtures and pins, and both companion hosts in step.
5. **The docs' local paths,** after DiamondAge's folder is renamed to `C:\dev\Garage-Game`: AGENTS.md's worktree section, the status page's paths, the briefs. History stays as written.

## Decisions (the founder, 21 October)

- **A. The code name: `GarageGame`** (as recommended): the C# namespace, project, solution and assembly `GarageGame`, the Python package `garage_game_companion`, its commands `garage-game-companion*`. Each AI client is registered again once after phase 2.
- **B. The save folder: moved to `Garage-Game`** (the founder, 21 October: "please update everything to Garage-Game. I don't want there being any confusion in the future"). `config/name` becomes `"Garage-Game"`, so `user://` is `%APPDATA%\Godot\app_userdata\Garage-Game`; the first launch of that build copies the whole old `EnFractal` folder there, verifies every file and leaves the old folder untouched; the companion, the playtest reader, roomscan and the Linux suite look in the new folder. Phase 3.
- **C. The data-format ids: renamed with a migration** (against the recommendation to keep them). New ids `garage_game.<kind>` for all twenty kinds; readers accept both for a transition; installed rooms, saves and team files migrate; fixtures, examples and pins are regenerated; both companion hosts move in step. Phase 4, below.

## How each phase lands

Its own branch from `run2/integration`, both Windows runners green on the merged tree, a fast-forward of `run2/integration`, and a line in the status page. DiamondAge pulls when the founder next works there; nothing here needs DiamondAge idle except the merge itself.
