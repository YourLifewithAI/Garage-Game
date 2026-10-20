# EnFractal

> **This repository is the Garage Game** (renamed on GitHub from `EnFractal` to `YourLifewithAI/Garage-Game` on 19 October 2026): the room-era EnFractal, kept whole so the founder can keep adding to it for their children. EnFractal's new direction (planetoids in the void, primitives, one material, the AI as a teammate) lives in a fresh repository at [github.com/YourLifewithAI/EnFractal](https://github.com/YourLifewithAI/EnFractal). Nothing here was removed by the split.

A sandbox where you and your AI play together inside a real room that you photographed, shrunk to the size of a thumb.

**Direction as of 6 October 2026.** The player photographs a room or house. An AI-driven pipeline guides the capture, reconstructs the space, turns every object into its own game asset, and applies a chosen whimsical style. The player then enters that room as a ~10 cm avatar beside a separate avatar for their own AI. The AI is the player's magic: a spoken or typed wish ("there's a dragon haunting the garage", "build a cozy village along the shelves", "make this a spaceport") becomes a real change in the world through one shared baseline ruleset. Single-player first; multiplayer last.

Read the [room-scale direction](docs/ROOM-SCALE-DIRECTION.md) for the concept, the kernel rules, and the four tracks (Look, Capture, Play, AI companion) that converge in parallel-team integration runs. Read the [capture-to-Godot pipeline design](docs/pipeline/ROOM-CAPTURE-PIPELINE.md) for the MCP server and skill that take photos to a playable room, including the method review of the founder's reconstruction specification.

## Status

The geography era is cleaned out. The repository previously built a real-place world (Barton Creek, then the Pfluger district in Austin) with a 0.30 m player; the map data, terrain runtime, district scenes, PostgreSQL save/travel service and multiplayer experiments were removed on 6 October 2026 per the [cleanup plan](docs/CLEANUP-PLAN.md). Everything removed is on the `geography-era-final` branch. Run 0 is done: the [contracts](contracts/README.md) every track builds against, the [ownership map](docs/runs/OWNERSHIP.md) and the [Run 1 brief](docs/runs/RUN-1.md). The game now loads rooms and style presets as hash-verified data; the placeholder room (`run-room.ps1`) is the first one. Agents start from [AGENTS.md](AGENTS.md).

What carries forward from the earlier work:

- **Native Godot .NET 4.7.2 with C#** and a pinned, checksum-verified toolchain. See [native build](docs/NATIVE-BUILD.md) and [ADR 0001](docs/engine/decisions/0001-native-godot-csharp.md).
- **Small-body controllers**: a metric player controller with stepping, recovery and camera, and a separate deterministic companion body with follow/stay/come/look/point/stop. Both default profiles are already 10 cm.
- **The creation kernel**: data-only creation manifests, one compiler, a host-owned authority with budgets, receipts and protection, a world-state store, and an invention editor retained in kernel fixtures. The player-facing workshop is disabled in the room (see [the editor record](docs/engine/phase3/editor.md)). This becomes the baseline ruleset every wish composes from.
- **Painterly shaders and original art sources** for the style pass.
- **Research** on AI/MCP security, physics and creation runtime, structured world and style, and platform choice.

## Working rules

- Metric units everywhere; 1 world unit = 1 metre. Captured rooms and generated assets are real size.
- The companion and manual controls use the same validated operations. No second physics, no bypass.
- Claims need evidence: a checkpoint records what ran, on what machine, and what remains open.
- Photo sets for test rooms live in the founder's Google Drive folder **Enfractal / Photos for space generation / <room>**, and art references in **Enfractal / Art inspiration**. Both stay in Drive and are read in place; nothing from them is committed. The first test case is the garage; the second is a friend's back yard.

## Where things are

- `docs/ROOM-SCALE-DIRECTION.md` and `docs/pipeline/` are the active plan.
- `docs/engine/` holds the kept engineering records: the platform ADR, the creation-kernel documents under `phase3/`, the persistence rules and the review rubric.
- `docs/research/` holds the research that still applies (physics and creation runtime, AI/MCP security, structured world and style, visual style, platform, costs, product).
- `docs/history/` holds the superseded vision, roadmap, backlog, contract and the last geography-era checkpoints, each with a superseded banner. They are context, not instructions.
