# Companion command surface (A1, A2)

Players bring their own AI. Any MCP client or agent harness reaches the game through one model-neutral
MCP server, whose tools map one to one onto the command contract in
[contracts/game-command.schema.json](../../contracts/game-command.schema.json). The server talks to the
running game over a loopback link with a per-session token ([TRANSPORT.md](TRANSPORT.md)). The security
boundary and the tests that prove it are in [SECURITY.md](SECURITY.md); what the companion can see and
remember is in [PERCEPTION.md](PERCEPTION.md); its goals, its visible state and fetch are in
[EMBODIMENT.md](EMBODIMENT.md).

```
any MCP client or harness --stdio--> garage-game-companion --loopback link--> game host
   tools/list, tools/call            adapter: contract messages            the kernel's CommandHost in the room (A2),
                                     locked-down process                   behind the C# link end; the mock host
                                                                           (Python) as the test double
```

## Layout

| Path | What it is |
|---|---|
| `companion/pyproject.toml`, `companion/uv.lock` | Pinned environment (Python 3.11 or newer; the founder's machine uses 3.14.2) |
| `companion/requirements.lock.txt` | The same pins with hashes, exported for runners without uv (`uv export --frozen --no-dev --no-emit-project --format requirements-txt -o requirements.lock.txt`) |
| `companion/src/garage_game_companion/contract.py` | Loads `contracts/` and `contracts/validate.py` (with ECMA-262 patterns); derives the tool catalogue |
| `.../server.py` | The MCP server (official Python SDK `mcp==2.2.0`, low-level API) and the adapter |
| `.../profile.py`, `.../clients.json` | The play-only client profile, in the common `mcpServers` shape or in six clients' own formats (`--client`, `--write`); `SYSTEMROOT` on Windows. `clients.json` is the client catalogue (data, from brief 04) |
| `.../real_game.py` | Starts the real game host (the room with the kernel's command host and the companion link) headless, for an AI client or the suite: `python -m garage_game_companion.real_game` |
| `.../link.py` | The loopback link: session file, mutual HMAC handshake, canonical frames |
| `.../canonical.py` | Canonical JSON v1, byte for byte as Lane P defined it: fingerprints, size limits, frames |
| `.../mock_host.py`, `.../mock_game.py` | The mock game host (with perception memory and goal jobs), and a console that runs it as a stand-in game |
| `.../perception.py` | Line of sight and the 10 cm body for the mock host |
| `.../lockdown.py` | Environment scrub and audit-hook sandbox for the server process |
| `.../refusals.py`, `.../textsafety.py` | One mapping from contract violations to error codes; untrusted-text rules, emoji markers in place |
| `companion/schemas/companion-link.schema.json` | The link's session file and frames, proposed for `contracts/` |
| `companion/tests/` | Boundary tests (host, link, MCP surface, perception, perception memory, text rules, no holds, sandbox, profile), the stdio acceptance test, `test_kernel_alignment.py` (it reads the kernel host's constants and fails when the mock's policy drifts from them), `test_fetch.py` (fetch on the mock), `test_glow.py` (Glow, Bubbles and Fireworks on the mock and through MCP; `test_host_alignment.py` runs their shared scenarios on both hosts), `test_real_host.py` (the link and boundary cases, a refused-connection flood, the goals and the visible state against the real game host) and `test_link_ownership.py` (two games sharing one user folder) |
| `game/scripts/native/Companion/` | The game's end (A2): `CompanionLinkServer.cs` (the link), `CompanionBridge.cs` (the link in front of the command host, the session file, the visible state), `CompanionStatusCue.cs` (the state on the avatar), `CompanionRoom.cs` with `companion_room.tscn` (the room with the link, until `RoomWorld` attaches the bridge itself) |
| `companion/tests/fixtures/contract_memory_v1.json` | The perception-memory result fields as proposed; `contracts/` has carried them since `72e9015`, and a test checks the two still match |
| `docs/companion/proposals/` | Changes for files this lane does not own, as patches, and the kernel host's change requests (below) |

## Tools

One tool per op in `$defs/command_op` and `$defs/query_op`, except `$defs/player_only_ops`
(`protect.unlock`), which has no tool and which the game refuses from a companion anyway. The list is
derived from the contract at startup, so a new player-only op disappears from the surface without a
code change.

| Tool | Op | Kind |
|---|---|---|
| `room_describe`, `entities_list`, `entity_inspect`, `capabilities_list`, `observe`, `jobs_status`, `receipt_lookup`, `approval_status`, `journal_read`, `map_find` | the query of the same name | read-only |
| `entity_grab`, `entity_release`, `entity_place`, `entity_push`, `entity_set_part`, `entity_remove`, `entity_transform`, `creation_place`, `creation_revise`, `creation_activate`, `protect_lock`, `goal_set`, `goal_stop`, `effect_start`, `effect_stop`, `style_set`, `room_checkpoint`, `room_undo`, `journal_note` | the command of the same name | command |

29 tools. The contract round added `entity_push`, `journal_note`, `journal_read` and `map_find` (8 October); the hosts
answer the three journal ops as not available yet until the journal's round, and the push until Lane P's verbs.

Tool names use underscores because several vendors' function-calling APIs reject dots. A tool's
arguments are the op's `args` from the contract, flattened, plus for commands the envelope fields the
sender owns: `action_id` (required, except for the two stops, where the adapter mints one),
`expected_revision`, `expected_entities`, `preview`, `note`. The adapter fills `schema`, `version`,
`room_id`, `op` and `query_id`, and fills `actor` with the companion's own avatar where the contract
requires one. Nothing else is accepted: no principal, no approval, no room. Numbers are sent as canonical
JSON v1 writes them (`2.0` is sent as `2`).

When the adapter refuses a call before sending it, it answers with a contract-valid `enfractal.result` of
its own (same error codes as the host). Its `field_path` names the tool argument for unknown or
forbidden fields (`$.principal`) and the contract message otherwise (`$.args.target`), as the host does.

Each result is the game's `enfractal.result`, returned twice in the same call: as `structuredContent`,
and as a text block holding a fixed preamble line and one line of ASCII-escaped JSON. `isError` is true
when `ok` is false. Published input schemas inline every `$ref` and drop `if`/`then`/`not`, which older
clients reject; `--schema-profile minimal` keeps only the most widely supported JSON Schema keywords. The
adapter and the host still validate every message against the full contract.

## Run the tests

```powershell
uv sync --project companion --frozen                       # once: creates companion/.venv
uv run --project companion --locked python -m unittest discover -s companion/tests
```

Expected: `Ran 661 tests ... OK (skipped=7)` in two to three minutes. Skipped: the stand-in for a `contracts/` without
the memory fields (today's contract carries them, so its counterpart runs instead), and the real host's go_to and
fetch, which run once the host walks to things and fetches. `test_real_host.py` starts one headless game; it needs the
pinned Godot .NET and .NET SDK and the C# project built (the runners build it first), and skips with the reason
otherwise.
The canonical JSON test uses the kernel's golden fixture in `game/tests/fixtures/kernel/` when the
checkout has it (or the folder `GARAGE_GAME_KERNEL_FIXTURE_DIR` names), and this lane's byte-identical copy
otherwise. `GARAGE_GAME_CONTRACTS_DIR` points the whole suite, the stdio server included, at another copy of
`contracts/`, to check a proposed change.

## Connect an MCP client or agent harness

The server is a standard MCP stdio server with no vendor extensions. Any client that can launch a stdio
server with a command line can use it. Both the initialize-handshake protocol era (2025-11-25) and
2026-07-28 are served. Registering a server is persistent client configuration, so the founder approves
it on their own machine.

1. Create the environment once, in the checkout the game runs from:

   ```powershell
   uv sync --project companion --frozen
   ```

2. Print the **play-only profile**: the one server a play session needs, in the common `mcpServers`
   JSON shape.

   ```powershell
   companion\.venv\Scripts\python.exe -m garage_game_companion.profile           # the running game
   companion\.venv\Scripts\python.exe -m garage_game_companion.profile --mock    # the built-in mock game
   ```

   ```json
   {
     "mcpServers": {
       "garage-game-companion": {
         "type": "stdio",
         "command": "C:\\dev\\EnFractal\\companion\\.venv\\Scripts\\python.exe",
         "args": ["-m", "garage_game_companion", "--mock"],
         "env": {"SYSTEMROOT": "C:\\Windows"}
       }
     }
   }
   ```

   Give it to the client as its whole MCP configuration for play. **For a particular client**, the profile is written
   in that client's own format: `--client claude-code`, `codex`, `vscode`, `continue`, `hermes` or `openclaw` prints
   it, and `--write <play-folder>` writes all six into that folder (and nowhere else) with a `README.txt` saying where
   each file goes and how to make that client's session play-only. Nothing is copied into a client's own settings:
   registering a server is the player's choice, behind the client's own trust prompt. Only Codex CLI has been run
   with this server; the others' formats come from their documentation (brief 04). **Play with this server only**, in a client
   session that has no shell, file, browser or other MCP tools, so that nothing a sign in the room says
   can reach anything but the game's checked commands. The server needs no keys or tokens; do not pass
   any to it (it clears its environment at startup either way). There is no token to hand over: the
   server reads the game's session file itself.

   **On Windows the profile gives the server `SYSTEMROOT`**, with this machine's value, and nothing else. Some clients
   start MCP servers with an almost empty environment (Codex does). Without `SYSTEMROOT`, Python's asyncio cannot load
   Winsock and the server exits at once (`WinError 10106`, found on 7 October 2026 with Codex CLI 0.162;
   `test_profile.py` shows both). The OpenClaw file sets no environment, because its persisted shape was not
   established; its `README.txt` entry says what to do if the server exits with that error.

3. Check that the client lists 29 tools, one resource (the journal) and no prompts, then ask, for example: "Observe what
   the companion can see, then set a follow goal for it." Expect an `observe` result listing the
   test-room props and a `goal_set` result with `"ok": true` and `"transient": true`.

4. **The real game.** Once `RoomWorld` attaches the companion bridge (the integrator's one-line wiring,
   [proposals/a2-real-host.md](proposals/a2-real-host.md)), every room the game runs listens for the AI, and the
   profile without `--mock` connects to it. Until then, or with no GPU to spare, run the room headless with the link:

   ```powershell
   companion\.venv\Scripts\python.exe -m garage_game_companion.real_game              # the player's own user folder
   companion\.venv\Scripts\python.exe -m garage_game_companion.real_game --isolated   # a temporary one; prints the session file to pass with --session-file
   ```

   Ask the companion to come, look at the book, point at the box, follow you; ask `jobs_status` how each ended. The
   game's console prints the companion's state (`COMPANION_STATE listening`, `planning`, `acting`, `waiting`), which
   a windowed game shows under its name tag.

5. To see held commands as well, run the stand-in game (the mock host) in a second terminal and use the profile
   without `--mock`, which reads the game's session file:

   ```powershell
   companion\.venv\Scripts\python.exe -m garage_game_companion.mock_game
   ```

   With the recommended policy, ask the companion to remove the book. The console prints
   `[held] <request id> entity.remove ...`; type `approve <first characters of the id>` there, as the
   player, then ask the companion to check `approval_status`. Typing `say obj:box <text>` puts a sign in
   the world to try prompt injection. To try perception memory: ask the companion to observe, type
   `walk 1.6 0 0.2` (behind the box), ask where the book is (it answers from memory), type
   `move obj:book -1.7 0 -1.2`, ask again (now `may_be_stale`), ask it to fetch the book, then type
   `walk 0.45 0 0.2` and `arrive`: the fetch fails honestly, and `jobs_status` says why. A fetch that finds its
   thing arrives twice: `arrive` at the thing picks it up, `arrive` again brings it back to you.

Without `--mock` or `--session-file`, the server reads the real game's session file from
`%APPDATA%\Godot\app_userdata\Garage-Game\companion\session.json` (Godot's `user://`). With no game running,
every tool answers `not_ready`.

## Proposals for files this lane does not own

The four Run 1 patches were applied on `run1/integration` (`72e9015` and earlier); they stay as the record.
They are applied from the stored blob so line-ending conversion cannot touch them
(`git show run1/companion:<path> | git apply`).

**Before the Run 2 swap:** [proposals/kernel-host-gaps.md](proposals/kernel-host-gaps.md) records the
mock alignment and the eight host requests. All eight are closed on the real host as of 7 October 2026
(see [the Run 1 status](../runs/RUN-1-STATUS.md#still-open)).

**A2 (8 October 2026):** [proposals/a2-real-host.md](proposals/a2-real-host.md) explains two patches and what fetch
still needs on the real host: `a2-roomworld-wiring.diff` (the integrator: `RoomWorld` attaches the bridge) and
`a2-go-to.diff` (Lane P: a go-to for the body and go_to through the host). Both apply on `0e39374`, build without
warnings and were run here with the patches applied.

| Patch | What it does | Checked by |
|---|---|---|
| `proposals/runners-run1.diff` | Adds this suite to `run-engine-tests.ps1` and `tools/linux/test-all.sh` (with its environment from `tools/linux/setup-toolchain.sh`), and pins line endings for the link schema, the fixture copy and these patches in `.gitattributes`. Unchanged this round | Applies on `0ea09ff` with `git apply --check`, alone and after the three patches below; the bash scripts pass `bash -n` and the PowerShell runner parses. Not run: the runners belong to the integrator |
| `proposals/contracts-run1.diff` | Promotes the link schema; adds `capabilities.list` and `jobs.status` payloads and a display-text checkpoint label; adds the perception-memory fields to `entity_summary` (`seen`, `last_seen_ago_s`, `last_seen_revision`, `may_be_stale`, the last three only on remembered summaries) and `remembered` to `observe`; examples for memory, a goal job and `jobs.status`, and two invalid memory examples. Rebuilt on `0ea09ff` | Applies there with `git apply --check`; 34 contract tests alone, 40 with the text-rules patch (either order), the validator on the test room, its style pins, the garage and all 34 valid messages; this suite against the patched copy (469 tests) |
| `proposals/contracts-text-rules.diff` | Review finding 5, the trailing-newline item and the emoji answer: the invisible characters and plane 14 refused in `display_text`, `long_text` and scalar strings, VS15, VS16 and the joiner let through by the patterns; ECMA-262 patterns, plane 14, unpaired surrogates, misplaced emoji markers and unreadable numbers in `contracts/validate.py`; six contract tests with the shared emoji vectors. Rebuilt on `0ea09ff` | Applies there; 40 contract tests with both contract patches, either order; this suite against the patched copy |
| `proposals/roomdata-text-rules.diff` | The same text rule in the C# room loader (`IsSafeText`, `MisplacedEmojiMarker` with the Extended_Pictographic table), `\z` for its anchored patterns (.NET's `$` also matches before a final newline), and the shared emoji vectors plus a room-name load test in `RoomDataTest.cs`. Rebuilt on `0ea09ff` | Applies there. **Not built or run**: no C# toolchain in this lane's worktree |

## Decisions taken in this lane

- **Python, official SDK, low-level server.** The tool list is generated from the contract, so the SDK's
  decorator API would only get in the way. `mcp==2.2.0` (released 7 September 2026) was chosen over the
  then four-day-old 2.3.0.
- **The adapter validates, the host decides.** The adapter refuses what it can without asking the game,
  so a confused model fails fast, but every rule is enforced again by the host, because anything holding
  the token can bypass the adapter.
- **No protection depends on holding.** Holds are a policy table that may be empty (the founder's
  direction), and every protection is tested with it empty.
- **One error mapping.** Adapter and mock host share `refusals.py`, so the same attack gets the same
  contract error code at either layer, with template messages that never echo requester text.
- **Results are data.** World text only ever appears inside JSON string values, stripped of invisible
  characters; the text block is ASCII-escaped and one line long, so a sign cannot start a new line,
  hide characters or close a tag.
- **A companion undoes only its own changes**, and never anything protected; undoing the player's
  changes is the player's. The founder confirmed this on 6 October 2026.
- **The companion remembers what its own avatar saw** (founder, 6 October 2026): per session and room,
  bounded, never saved, with a one-bit `may_be_stale`; only goals that move or turn it may aim at a
  remembered thing, re-checked on arrival ([PERCEPTION.md](PERCEPTION.md)). (Replaced on 7 October by
  [shared team knowledge and selective game-relevant memory](JOURNAL.md). The per-session perception
  cache remains interim code; the shared map and journal are not implemented yet.)
- **Standard emoji are allowed in world text** (founder, 6 October 2026): VS15, VS16, the joiner and the
  keycap combiner only where an emoji puts them; every other invisible character stays blocked.
- **The companion has the player's 10 cm body** (founder, 6 October 2026): eye 0.087 m, reach 0.15 m.
- **Canonical frames, no lockout** on the link (TRANSPORT.md).
- **The mock follows the kernel host** wherever the kernel fixes a number or a rule that the contract
  allows (6 October 2026, night): its ledger, rate limit and approval limit, and the fingerprint over the
  command exactly as received, so `"preview": false` on the wire is other content. The MCP tools treat
  `preview: false` as their default and never send it, so a model that spells the default out still
  replays rather than conflicts. The adapter keeps its tighter per-kind rate buckets in front of the
  host's limit.
- **The in-game bridge (A2) is a port, not a redesign.** `CompanionLinkServer.cs` follows `link.py`'s `LinkServer`
  case for case, and `test_real_host.py` runs `test_link.py`'s cases against it. Requests are answered on Godot's
  main thread, because the host casts rays and touches the scene.
- **The avatar shows the AI's state** (listening, planning, acting, waiting for the player's yes) only while an AI is
  linked, inferred from the link and the body: the game cannot see the model think. First-pass words and colours.
- **One game per account owns the link**, by an ownership lock held for the link's life, never by what a session
  file says; a second window runs without it, and `--no-companion-link` turns it off. The link's diagnostics are
  counted and summarised, so traffic without the token cannot load the game's main thread (A2 security review).
- **Fetch is built to the merged contract on the mock first** (pick up on arrival, succeed beside the player still
  holding); the real host gets it with Lane P's verbs and the body's go-to ([EMBODIMENT.md](EMBODIMENT.md#fetch)).
- **Per-client configurations are data, not code.** The client catalogue (`clients.json`) names the clients; the
  generator's formats are named by shape, so the package's code still names no vendor.
