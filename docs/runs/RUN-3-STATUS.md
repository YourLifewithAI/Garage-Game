# Run 3 status

The handoff page for [RUN-3.md](RUN-3.md), the story run. An integrating session reads [AGENTS.md](../../AGENTS.md), [ORCHESTRATION.md](ORCHESTRATION.md), [OWNERSHIP.md](OWNERSHIP.md), RUN-3.md and this page, in that order. Run 2's record is [RUN-2-STATUS.md](RUN-2-STATUS.md).

## The planning session (19 November, a Linux cloud container)

- **What it made:** RUN-3.md (the brief, with the founder's answers of the same day in its section 7), `tools/linux/capture-hud.sh` (HUD pictures from a cloud container by software OpenGL), and this page. Branch `claude/sweet-newton-y33w6t`, draft [pull request #21](https://github.com/YourLifewithAI/Garage-Game/pull/21). No game code changed and no suite was run.
- **Why the run moves to the laptop** (the founder's word): the container has four cores and no GPU. Software OpenGL draws the test room's HUD in 69 to 83 s and the 10x island in 9 minutes, in the Compatibility renderer, which is not the look. The real Forward+ renderer starts on a software Vulkan driver but had written no frame after five minutes. Look, sound, windowed captures, frame times and the Windows runners all need the founder's machine.
- **Left in the container, unused and never committed:** the software Vulkan driver unpacked in `.cache/linux/lavapipe`.

## The first laptop session (18 November, DiamondAge)

- **Step 0, done.** `tools/check-run1-readiness.ps1`: every lane ready, nothing to bootstrap. The checkout is `C:\dev\Garage-Game`.
- **The branch.** `run3/integration`, cut from the handoff (`f58f429`) with `run2/integration` merged in (`d7b4170`): LEAD.md, the two debriefs and the companion's `GARAGE_GAME_HOST` switch had never reached main. Not pushed yet.
- **Step 1, done.** The "before" sets are in [docs/look/reviews/run3/](../look/reviews/run3/README.md) (`8c20e90`): the island cameras by day, at dusk and at night, with frame times. Shown to the founder as three contact sheets.
  - Over the provisional budget: the spawn view (18.1 ms p95, worst frame 65 to 67 ms) and the far Fractal view (19.6 ms p95).
  - The summit camera could not place the player, so there is no picture from the top yet; `-Sweep` fails with the island cameras. Both are in L-perf's packet.
- **Step 2, done.** The opening commit is `496ad80`: OWNERSHIP.md's Run 3 section (with a narrow grant for L-perf), AGENTS.md's worktree line and LEAD.md's note.
- **Step 3, batch 1,** each lane in `C:\dev\Garage-Game-run3\<lane>` on `run3/<lane>`, with `.cache\dotnet`, `.cache\godot` and `.cache\contracts-venv` linked to the integrator's. The routing log in ORCHESTRATION.md has each run's tokens and outcome.

  | Worktree | Packet | Model | State |
  |---|---|---|---|
  | `sound` | The buses, one director, 28 sounds made in code | Sonnet, reviewed by a fresh Opus | **Merged** (`1ce6be8`), both runners green on the merge |
  | `look-perf` | L-perf | Opus | **Merged** (`a7b6cf5`), both runners green. Partial: no frame time won |
  | `contracts` | The story pack contract (on the integrator's behalf) | Opus, reviewed twice by a fresh Opus | **Not merged.** See "The story contract" below |

- **Sound, as merged.** The director attaches in `RoomWorld._Ready` after the command host; `project.godot` names the bus layout; `native_audio.tscn` is in both runner lists (392 checks). A headless run starts no audio. `capture-look.ps1` passes `--audio-driver Dummy`, so captures are silent; one single-view capture with sound attached ran clean (the hamlet 7.9 / 11.0 ms, as before).
  - **Open:** a windowed game quit while sounds play can still print Godot's "ObjectDB instances were leaked at exit" (a race in the audio server; the headless switch hides it from the tests and does not remove it).
  - **Cost:** attaching takes 18 to 22 ms of main thread once, at room start, on the 10x island.
  - **Change requests not yet applied:** the expertise chime to the UI bus (`RoomHud.Camp.cs`, the player's initialiser near line 172, `Bus = "UI"`) and the glow chimes to Effects (`LookDirector.GlowGrowth.cs`, the `AudioStreamPlayer3D` near line 91). The settings screen needs sliders over `AudioSettings.Get/Set/Mute/Reset/Save` (Lane U).
  - **Not built:** sounds for the Gubble's actions, doors and the Bubble; muffling when the eye is under water; music.
  - **Not heard by anyone.** The founder has three listening files (ambience, feet and water, small things); the lane checked the sounds by numbers only.
- **L-perf, as merged.** The intro's set-up waits two frames behind the black cover: the freeze frame on the 10x island went from 846 to 1,064 ms to about 728 ms. The summit camera stands on the highest ground the player can stand on. `FrameProbe --split` times each child of the world. The numbers are in [docs/look/reviews/run3/README.md](../look/reviews/run3/README.md).
  - **Still over the provisional budget:** the spawn (18.2 ms p95) and the far Fractal view (19.3).
  - **L-perf round 2 comes before L-air.** Its measured targets:
    - the land's geometry is drawn in the depth pre-pass (2.7 ms) and again in the opaque pass (4.3 ms) at the spawn;
    - small plants drawn to 30 m instead of 40 put both views inside the budget (16.6 and 16.0 ms p95) but change the picture visibly: render the pair for the founder to judge;
    - what is left of the freeze: a HUD CanvasLayer (about 100 ms, Lane U's), the first 87 pipelines (230 to 300 ms), the bubble's warm-up (66 ms), the campfires (50 ms), a 62 ms terrain-collision tile build (`Room/TerrainCollision.cs`, in the grant), and the command host's physics step (25 to 45 ms every few seconds, the kernel's);
    - the apple-picking hitch was not reproduced; `-Sweep` still fails with the island cameras;
    - check whether sound's start adds to the first view's worst frame (86 ms with the hamlet first, against 65 to 67 ms at the spawn before sound).
- **The summit has nothing to see.** The true summit view is a wide, flat, pale tabletop, with none of the island below in the frame. Nobody has checked that this spot is the island's `x_summit`. The ending rests on this place: Q3 checks the route and the spot, and the look and the plant kit make it worth reaching.
- **A crash under load, not ours to chase yet.** The room runner's character steps crashed at exit three times in the `contracts` worktree (a .NET finalizer crash, "Leaked unsafe reference", GLTF) while four suites shared the machine. The same steps pass alone, and the integrator's five room-runner passes never showed it. Stagger the lanes' final runner passes.
- **Not started:** Q1 (waits for the contract) and L-air (waits for L-perf round 2).
- **Both Windows runners on the base: green.**
  - The room runner passed on `8c20e90`.
  - The engine runner first failed one check there, and stopped before its later suites: the play HUD's "noon is the brightest" reads the real date, and on 18 November the late-afternoon step reads 0.96 against noon's 0.89. Main fails the same way on this date. Fixed in the test (`1cb4ded`); the engine runner then passed on `1cb4ded` with no failing check (companion 868 tests, playtest reader 98).
  - The three lanes were told to merge `1cb4ded` before their final runner pass.
  - **For L-air:** the day's light keys sit at fixed hours and peak after noon, so they do not follow the day's length. In winter the brightest light comes under two hours before sunset.

### The story contract

**State at 22:35 on 18 November: not merged.** `run3/contracts` holds round 1 (`9414dc6`) and the first fix round (`0decccc`), which merged cleanly into the integration tree and passed both runners there before the review's blockers kept it out. A short third round was running for the re-review's findings:
- a step whose condition needs a topic the step never opens;
- digits in titles, names and lines (and Unicode digits);
- the save's room against the pack's `room_id`;
- quest facts against progress;
- `fractals_spent` against the island's budget;
- a `"` inside quoted text;
- where the two summit effects may sit.

If a newer commit is on the branch, its body holds the lane's report; if not, that list is what is left.

- **What it is** (`contracts/island-story.schema.json`, `check_story` and its companions in `contracts/validate.py`, `game/stories/example/v1.json`): a pack at `game/stories/<id>/v<N>.json` names its island (`room_id`) and its rules pack. Chapters hold quests, quests hold steps; a step has a journal line, one condition from a closed list of nine, an optional lead and beats. Rewards are a keepsake or a lead to a Fractal the island already has. Quest state is room state's `extensions.x_story`; the journal has three quest kinds.
- **Round 1 passed its own tests and was still broken.** The reviewer wrote packs that validated and could never be finished, and showed that the Gubble's own Fire could satisfy "a campfire lit". The fix rounds closed both blockers.
- **The integrator's decisions, all provisional:**
  - no condition is met by a companion's own-initiative act: a fact counts when the player did it or directed it;
  - an islander's character is one id or an ordered cast list, and the validator proves every islander gets a distinct character whatever the player wears;
  - a ninth condition, a Fractal found;
  - a closed list of three quest effects (an islander comes to the camp, the overview opens, the second knack slot);
  - no digits in a pack's text;
  - a child's recording of a line is found by pack, islander and line id in the player's user folder, never shipped and never committed;
  - the old-style id `enfractal.island_story` is not accepted.
- **Open for Q1, its first task.** The kernel cannot yet attribute two conditions. The expertise store (`team.schema`, `expertise.discoveries`) is a team-wide list with no principal on an entry, and `x_play_camp` records that a campfire is lit, not who lit it. Recommended: the ledger counts both from the command host's handled commands while the step is current, and expertise the team held when the story started counts when its step becomes current.
- **Also for Q1:** `CommandHostRoomState.cs` (near lines 110 to 125) rebuilds a save's extensions from scratch, so the host must write `x_story` back; add the three quest kinds to `CommandHostJournal.cs`'s `JournalKinds` and restore exact equality in `test_contracts.py`; a save naming a pack the game does not have keeps its story block untouched and inert; which pack a room plays is bound by `room_id`.
- **For the family, before T1 writes a word:** the player wears one of the four characters, so three islanders are left for the four friends' needs (Grow, Bubbles, Glow, Fire). The cast list carries either answer; who carries the fourth need is a story decision.

## The next session, in order

0. Read this page. `run3/integration` is pushed; the lane branches are local only.
1. **The story contract:** finish what "The story contract" above says is left, then merge it: the contract tests (`python -m unittest discover -s contracts/tests` from the repository's root, with `.cache\contracts-venv`), `validate.py` over the shipped rooms, presets, rules and stories, and both Windows runners on the merge.
2. **Q1, the quest ledger** (Opus), on the merged contract, starting with the attribution question above.
3. **L-perf round 2** (Opus, the GPU lane), then **L-air**. Show the founder the 30 m and 40 m pair first.
4. **Sound's small follow-ups** above, by the integrator or a short Sonnet round.
5. **The pull request for batch 1,** from a snapshot branch as RUN-2-STATUS.md describes. The Linux suite has not been run on any of this session's work, and a pull request to main needs it green on the exact commit: run `tools/linux/test-all.sh` in a cloud session first.
6. **Show the founder the island with sound** (`run-room.ps1`), and the before and after pictures once L-air has something to show.
7. **The founder's sharing question** (LEAD.md, 18 November): a design answer, as its own piece of work.

Worktrees `contracts`, `sound` and `look-perf` under `C:\dev\Garage-Game-run3` all stay; nothing was deleted.

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
- The story's first draft: the founder and the children say who each islander is and what it wants, by voice once V2 exists, in words to the founder before that. With it, who carries the fourth friend's need (above).
- From the first laptop session (18 November):
  - whether the first sounds feel gentle (three listening files), and where music comes from;
  - the frame budget, now with numbers: two views are over 16.7 ms, and drawing small plants to 30 m instead of 40 would bring both inside it at a visible cost;
  - how dark the night should be: away from the fire the land is nearly black, and the hamlet has no lit windows;
  - whether the day's light should follow the day's length (it peaks after noon at fixed hours);
  - the sharing question the founder raised (LEAD.md): how others copy the game and make it their own with their own AI, and one safe place to share what they make.
