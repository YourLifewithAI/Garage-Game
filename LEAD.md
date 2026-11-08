# Garage Game: the lead's backlog

Kept by the Garage Game's lead session (started by Wren, 8 November 2026). One ranked list for the whole game: the Godot game in this folder and the Unreal slice in `C:\dev\Garage-Unreal`. [docs/runs/RUN-2-STATUS.md](docs/runs/RUN-2-STATUS.md) stays the detailed record ("The next session" and each round's questions); this file sets the order and points there for detail. Work runs one item at a time, top down.

**Ben's answers (8 November, 11:36, relayed by Wren):** the order as ranked; go on the Unreal slice's stage 3 with its roadmap's defaults; the kids play on DiamondAge (this laptop); Ben himself played both unread plays; yes to merging #20 and closing #17 and #12. Auto mode refused the lead those GitHub writes, so they're Ben's clicks (H2).

## Where things stand (8 November 2026)

- **The Godot game.** `main` is at `cd2675e` (pull request #19, merged by Ben on 25 October, with both Windows runners and the Linux suite green). The local `run2/integration` is at `94da603`, two docs-only commits behind GitHub's (`99d42e2`). Nothing has been built since 25 October.
- **Built and never seen on a screen.** The 24 and 25 October rounds built the Esc menu, the camp screen, the pockets and chest, food, the hotbar, Grow's unlocks (sprouts, replanting, the bridge, ivy) and the repair card. They were tested headless only. Each packet's "first look" list is in the status page.
- **Two plays recorded and never debriefed.** Both are in the game's user folder on this laptop:
  - `20261017-141154-landscape_garage_nominal`: 15 minutes on the stand-in garage, build `bb9f47f`.
  - `20261025-180114-landscape_my_garage`: 11 minutes on the real garage, build `94da603`. This is the newest build, played an hour after #19 merged, and the only play of the hotbar, pockets, food and Grow's unlocks.
- **The Unreal slice** (`C:\dev\Garage-Unreal`, private repository `YourLifewithAI/Garage-Unreal`). Ben started it on 31 October, after the look test in which the kids chose the Unreal views, "to decide whether the game moves". Stages 0 to 2 are merged (pull requests #1 to #6, the last at 22:31 on 31 October). Stage 3 (wiring, the AI plays, the measurements) and stage 4 (the kids' gate) have not started. Its status page doesn't record stage 2's merge, `docs/runs/OWNERSHIP.md` has four uncommitted lines, and the six questions in its roadmap's section 6 are unanswered.
- **Sound.** The game has two synthesized chimes (Glow and the expertise card) and nothing else. There's no music, no footsteps, no fire, no rain, and no eating sound (the `FoodSound` hook waits for one).

## The order

### 1. Debrief the two unread plays

- **What:** run `/playtest` on both records. The 25 October play gets its own debrief, `docs/runs/playtests/2026-10-25-landscape_my_garage.md`. The 17 October 14:11 play, Ben's second that day, played while the first one's debrief was being written, goes in as an addendum to the existing 17 October debrief. Each gets its row in the index and its Change list.
- **Why:** the 25 October play is the only evidence of how the newest build plays, on the real garage. It feeds item 3's comparison whichever way the gate goes. The game keeps only its newest thirty records.
- **Done when:** both debriefs are in the index and committed on `run2/integration`. Stills and records are never committed.
- **Ben:** one line for each play, if you remember: who played, and anything that stood out. The record stands on its own without it.
- **Size:** about an hour.

### 2. Finish the Unreal slice to its gate (stage 3)

- **What:** in `C:\dev\Garage-Unreal`:
  - record stage 2 in its status page and commit the OWNERSHIP lines;
  - wire the link, room, body and kernel together;
  - run `test_real_host.py`'s 41 tests against Unreal;
  - have the AI, through the unchanged companion server on Ben's plan, bring the Gubble to the player and move the box when asked;
  - take the measurements beside Godot (frame time, link round trip, launch to room, build and test times);
  - have one fresh Opus reviewer that wrote none of it read the result.
- **Why:** this is the open fork in the road. Ben started the slice to answer whether the game moves engines, and it stopped two-thirds built. Items 4 to 10 are worth more or less depending on that answer.
- **Done when:** `tools/test-all.ps1` is green with stage 3, `run-room.ps1` plays the slice, the measurements are in its status page, and the pull request is reviewed. Merging stays Ben's click, as in stages 1 and 2.
- **Ben:** a go-ahead; the roadmap's six questions (the recommendations stand unless you change them); and a read of Lane B's scale verdict (#5) before wiring.

### 3. The kids' gate

- **What:** Ben and the kids play the slice next to the Godot room (the roadmap's stage 4).
- **Done when:** Ben's word (move, stay, or keep both for now) is written in both status pages, and items 4 onward are reordered to match.
- **Ben:** the play itself. Only Ben passes a gate.

### Then, while the Godot game is the one the kids play

If the gate says move, items 4 to 10 become port stages, and the Godot game stays playable as it is.

4. **A first look at what was built blind.** Windowed captures on this laptop's GPU of each packet's first-look list, in order: P2 (Esc and the camp screen), the HUD pass, P-Hotbar, P-Inventory-2 and Round 3 HUD. Fix what's broken, then a short recorded play by Ben, debriefed.
   - **Done when:** the captures are reviewed, the fixes are merged, and both Windows runners are green.
   - **Ben:** a ten-minute play.
5. **The freeze and the camera (L2, L1).** The 941 ms freeze, the sitting camera and the bubble's ring of light, left from the second play's round. These need the GPU, which this laptop has.
   - **Done when:** the recorder's worst window no longer shows the freeze, and Ben has seen the camera.
6. **Round 3's open rulings and the small follow-ups.**
   - **Ben's three rulings:** should the Gubble repair a build the player made without asking? Should F always drop when a unit is armed? Should the H help be shortened instead of scrolled?
   - **The follow-ups** (status page item 10): a pockets-to-hand take; `CastFireAt`'s walk for a long Fireline; a Grow-coloured bridge outline; the generator's gaps in widths the bridge spans; the 10x food counts.
7. **Sound, a first pass.** Footsteps, fire, rain, eating, the expertise chime, and a little music.
   - **Ben:** the feel you want, and whether sounds are made in code or taken from free CC0 libraries (a download needs your yes; nothing is bought).
8. **Playing with the AI as the Gubble, live.** The parked `run2/wishes-live` branch: the password gate, a contract round for its ops, then merge. After that, a session where Claude joins Ben's or the kids' game as the Gubble.
9. **The real garage with firepits.** Installed rooms gain firepits only when regenerated, which starts their island saves fresh.
   - **Ben:** a yes to regenerate, and whether the desktop's Landscape shortcut should open the real garage instead of the stand-in.
10. **The opening scene** ([RUN-2-OPENING.md](docs/runs/RUN-2-OPENING.md), packets O1 to O3). This is a new path, so it comes last.
    - **Ben:** judges it in play. Any likeness of you or your desk needs your approval before it's made.

## Housekeeping (small; mostly Ben's hand)

- **H1.** ~~Fast-forward the local `run2/integration` to GitHub's~~: done 8 November (`99d42e2`).
- **H2.** Ben said yes (8 November). Auto mode refused the lead every GitHub write, so these are Ben's clicks:
  - [#20](https://github.com/YourLifewithAI/Garage-Game/pull/20): mark it ready for review, then merge with a merge commit (docs only, two files, no secrets or real-room data);
  - [#17](https://github.com/YourLifewithAI/Garage-Game/pull/17): close (the planetoid direction belongs to the EnFractal repository);
  - [#12](https://github.com/YourLifewithAI/Garage-Game/pull/12): close (superseded by `docs/magic/README.md`).
  - Keep the branches.
- **H3.** About 80 finished worktrees under `C:\dev\EnFractal-run1`, `-run2` and `-codex`, and their merged branches. Removing them is deletion, so Ben runs the script himself (`.cache\scratch-integrator\remove-finished-worktrees.ps1`). The lead can first bring its list up to date.
- **H4.** Rename leftovers:
  - the worktree folders are still named `EnFractal-*`;
  - AGENTS.md and the docs still say this laptop's checkout is `C:\dev\EnFractal` (phase 5);
  - the journal panel's "ENFRACTAL" heading, `class_name EnfractalWorldState` and the schema titles still carry the old name;
  - the landscape package format's id flip needs Blender, which this laptop has.
- **H5.** Parked branches, untouched unless Ben says:
  - `run2/wishes-live` (item 8);
  - `run2/characters` (the conv_b converter A/B);
  - `run2/scale-experiment`;
  - `codex/gubble-growth` (the same work as #12);
  - `codex/daily-journal`: the weekly "making journal", seven commits up to 1 November, never merged. Should it keep going, and should it merge?

## Done

(Nothing yet.)
