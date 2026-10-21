# Player-started wishes

The game's wish box queues the player's own words. The first wish explains how to start a runner; the game never launches an external program. A separate local runner processes one wish at a time. A later wish waits. **Stop wishes** cancels both the active wish and queued wishes, revokes their pending approvals, and stops the Gubble through its normal command path.

The journal retains up to 64 finished wishes and 16 queued wishes. It stores the player's request, typed command evidence, trusted refusal codes and curated statuses. Model prose never becomes Gubble dialogue. A runner's completion claim cannot grant a wish without a successful game receipt. Some accepted actions plus a refusal produce a partial result. Restarted running wishes become interrupted; they are not automatically executed again. Queued wishes remain local to that room.

## Try the complete path without an AI provider

From the repository, using the companion's existing Python environment:

```powershell
$env:PYTHONPATH = "$PWD\companion\src"
python -m garage_game_companion.wish_runner --write-demo-client "$PWD\.cache\wish-demo.json"
python -m garage_game_companion.wish_runner --client "$PWD\.cache\wish-demo.json"
```

Start the game and submit **Make a light**. The deterministic demo starts one Lantern for 30 seconds. It recognizes `light`, `glow` and `lantern`; other requests are marked unsupported. This checks the queue, authenticated links, command host, journal and Stop without an account or provider call. It is explicitly a demonstration, not an AI.

The runner normally reads `wish-runner.json` beside the game's existing companion `session.json`. `--session` and `--runner-session` override these paths for fixtures. `--once` processes at most one queued wish. Close another MCP client before running wishes: the ordinary companion link intentionally admits only one active AI connection. The separate runner link can only claim/update wishes and cannot send world commands.

## Connect any AI through the JSON-line adapter

Create a local configuration containing an absolute executable path and literal arguments. The command is launched directly, with no shell or wish interpolation:

```json
{
  "version": 1,
  "kind": "jsonl",
  "command": ["C:/absolute/path/to/adapter.exe"],
  "timeout_s": 120,
  "max_tool_calls": 16
}
```

The runner sends an `initialize` line containing protocol `enfractal.wish_adapter`, version `1`, and game tool names with input schemas. Before receiving player intent, the adapter must answer:

```json
{"type":"ready","protocol":"enfractal.wish_adapter","version":1,"exclusive":true,"tools":["observe","effect_start"]}
```

Every listed tool must be in the offered game inventory. `exclusive:true` states that the adapter has configured its model with only this inventory; an adapter is trusted local integration code, not an operating-system sandbox. The runner independently refuses every non-game call. Shell, files, browser actions, journal prose and approval tools are not offered. `creation_place`, `creation_revise` and `creation_activate` use the existing bounded data-only creation compiler and normal host authority. The runner always replaces a creation's displayed name with **Wish creation**; it never accepts scripts, file paths or external APIs as creation behavior.

Next comes `{"type":"wish","wish":{"text":"…","aim_id":"optional:target"}}`. Emit a `tool_call` with exactly `type`, unique `id`, `name` and `arguments`. The runner supplies command action IDs and returns `tool_result`. Do not supply `action_id` or `note`. The existing boolean `preview` field is supported: `preview:true` predicts without committing and never grants a wish. Fire requires an explicit preview call followed by a separately selected commit carrying the returned `preview_id`; the runner never automatically commits a preview. Finish with `{"type":"done","status":"completed"}`, `failed` or `unsupported`. No other prose or result fields are accepted. Observed world prose is removed from tool results; IDs, geometry, state and error codes remain. Player approval happens only inside the game.

One invocation handles one wish, at most 32 tool calls and 600 seconds; defaults are 32 calls and 120 seconds. Protocol frames are bounded. The runner monitors Stop while the adapter is silent and terminates the process it started. Integration adapters must keep any provider subprocess in their own lifetime rather than detach background work.

## Optional Claude Code CLI path

There is a concrete `"kind":"claude-cli"` adapter. Its `command` must contain only the absolute native Claude executable. It uses fixed restrictions and relays model decisions through the same game adapter; Claude itself gets no native tools. Each turn verifies the CLI's initialization inventory and fails closed if tools, MCP servers or plugins remain. Tests use synthetic CLI events only; live inference has not been tested.

This path uses `--bare`, empty `--tools`, deny-all tools, strict MCP configuration, no session persistence and a one-turn cap. These options and the initialization event are documented in the [official CLI reference](https://code.claude.com/docs/en/cli-reference) and [programmatic-use guide](https://code.claude.com/docs/en/headless), checked 11 October 2026. **Bare mode uses API/provider authentication, not a Claude subscription login.** Authentication stays with the unmodified CLI; EnFractal never reads credentials. Provider usage begins only when the player explicitly runs this configured runner.

```json
{
  "version": 1,
  "kind": "claude-cli",
  "command": ["C:/absolute/path/to/claude.exe"],
  "timeout_s": 120,
  "max_tool_calls": 8
}
```

Subscription-based Claude and Codex CLI adapters are not verified here. Use the vendor-neutral protocol when a client can enforce and advertise a game-only inventory; do not replace these restrictions with a coding CLI's unrestricted toolset. Provider account eligibility and terms remain separate from the game's child-appropriate presentation.
