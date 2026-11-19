# Run 3 status

The handoff page for [RUN-3.md](RUN-3.md), the story run. An integrating session reads [AGENTS.md](../../AGENTS.md), [ORCHESTRATION.md](ORCHESTRATION.md), [OWNERSHIP.md](OWNERSHIP.md), RUN-3.md and this page, in that order. Run 2's record is [RUN-2-STATUS.md](RUN-2-STATUS.md).

## The planning session (19 November, a Linux cloud container)

- **What it made:** RUN-3.md (the brief, with the founder's answers of the same day in its section 7), `tools/linux/capture-hud.sh` (HUD pictures from a cloud container by software OpenGL), and this page. Branch `claude/sweet-newton-y33w6t`, draft [pull request #21](https://github.com/YourLifewithAI/Garage-Game/pull/21). No game code changed and no suite was run.
- **Why the run moves to the laptop** (the founder's word): the container has four cores and no GPU. Software OpenGL draws the test room's HUD in 69 to 83 s and the 10x island in 9 minutes, in the Compatibility renderer, which is not the look. The real Forward+ renderer starts on a software Vulkan driver but had written no frame after five minutes. Look, sound, windowed captures, frame times and the Windows runners all need the founder's machine.
- **Left in the container, unused and never committed:** the software Vulkan driver unpacked in `.cache/linux/lavapipe`.

## For the session on the laptop, in order

0. **Start.** Fetch and check out `claude/sweet-newton-y33w6t`. Read the pages above. Run `tools/check-run1-readiness.ps1` and the bootstrap if the machine needs it. On a machine not yet renamed, read `C:\dev\EnFractal` for `C:\dev\Garage-Game` (AGENTS.md). Check for a `Garage Game*` window before any render.
1. **The "before" pictures.** One full capture set of the 10x garage island as it is today (`tools/look/capture-look.ps1 -Room landscape10` with the island cameras), within ORCHESTRATION.md's capture budget. No look picture has been committed since 8 October, so this is the baseline every look packet is judged against. Show the founder.
2. **Open the run.** One integrator commit: OWNERSHIP.md rows for the new paths (`contracts/island-story.schema.json` and `game/stories/**` to the integrator; the quest kernel under `game/scripts/native/Kernel/**` to Lane Q; `game/scripts/native/Audio/**`, `game/assets/audio/**` and `tools/audio/**` to the Sound lane; `game/scripts/native/Voice/**` to Lane V; `game/assets/ui/**` to Lane U), and the lane worktrees under `C:\dev\Garage-Game-run3\<lane>`.
3. **Dispatch, two or three lanes at a time, one GPU lane at a time.** Routing as ORCHESTRATION.md's table: Opus for the kernel, contracts, characters and look; Sonnet for bounded work; a fresh Opus reviewer when a lane finishes a chunk.

   | Batch | Lanes | The founder sees or hears |
   |---|---|---|
   | 1 | The story pack contract and Q1 (the quest ledger). Sound: the bus, the hooks, ambience and effects made in code. L-perf, then L-air | The island before and after light and air; the island with sound |
   | 2 | The plant kit: trees, rocks, grass and flowers. K9 bendy limbs and the bodies' physics. U1 the theme, fonts and icons | Trees and rocks; a character sheet in motion; the HUD in its new dress |
   | 3 | Q2 islanders and T1 the tale. K10 faces, K11 gestures, the opening (O1, O2). U2 to U4 once the founder has marked up the controls table. V1 to V3 voice | The opening and the first night, played; an islander spoken to |
   | 4 | L-water, L-ground. Q3 the summit and the ending. U5. Review rounds and their fixes. A Windows build | The story start to end |

4. **After batch 4:** the children play, with the recorder on, and `/playtest` debriefs each play (the founder's ruling: the children's playtests follow the whole update).

Every merge runs both Windows runners on the exact tree before the integration branch moves, as in Run 2. Pull requests to main at each batch, as RUN-2-STATUS.md's "The next session", item 0, describes; never push to main directly.

## Waiting on the founder

- The controls table in RUN-3.md section 7: mark it up before U2 starts.
- The frame budget, the gamepad and where music comes from (RUN-3.md section 7, items 3 to 5): the run uses the provisional answers written there until the founder says otherwise.
- Whether the family's subscription may carry a child's live conversation with a character: a question for Anthropic. Voice's first step needs no live model, so nothing waits on it.
- The story's first draft: the founder and the children say who each islander is and what it wants, by voice once V2 exists, in words to the founder before that.
