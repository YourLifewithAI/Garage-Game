# Garage Game: the lead's backlog

Kept by the Garage Game's lead session (started by Wren, 8 November 2026). One ranked list for the whole game: the Godot game in this folder and the Unreal slice in `C:\dev\Garage-Unreal`. [docs/runs/RUN-2-STATUS.md](docs/runs/RUN-2-STATUS.md) stays the detailed record ("The next session" and each round's questions); this file sets the order and points there for detail. Work runs one item at a time, top down.

**Ben's answers (8 November, 11:36, relayed by Wren):** the order as ranked; go on the Unreal slice's stage 3 with its roadmap's defaults; the kids play on DiamondAge (this laptop); Ben himself played both unread plays; yes to merging #20 and closing #17 and #12. Auto mode refused the lead those GitHub writes, so they're Ben's clicks (H2).

## Where things stand (8 November 2026)

- **The Godot game.** `main` is at `cd2675e` (pull request #19, merged by Ben on 25 October, with both Windows runners and the Linux suite green). The local `run2/integration` is at `94da603`, two docs-only commits behind GitHub's (`99d42e2`). Nothing has been built since 25 October.
- **Built and never seen on a screen.** The 24 and 25 October rounds built the Esc menu, the camp screen, the pockets and chest, food, the hotbar, Grow's unlocks (sprouts, replanting, the bridge, ivy) and the repair card. They were tested headless only. Each packet's "first look" list is in the status page.
- **Two plays recorded and never debriefed.** Both are in the game's user folder on this laptop:
  - `20261017-141154-landscape_garage_nominal`: 15 minutes on the stand-in garage, build `bb9f47f`.
  - `20261025-180114-landscape_my_garage`: 11 minutes on the real garage, build `94da603`. This is the newest build, played an hour after #19 merged, and the only play of the hotbar, pockets, food and Grow's unlocks.
- **The Unreal slice** (`C:\dev\Garage-Unreal`, private repository `YourLifewithAI/Garage-Unreal`). Ben started it on 31 October, after the look test in which the kids chose the Unreal views, "to decide whether the game moves". Stages 0 to 2 are merged (pull requests #1 to #6, the last at 22:31 on 31 October). Stage 3 is built, reviewed and green on a local branch (item 2); stage 4, the kids' gate, waits on it.
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
- **Ben:** a go-ahead; the roadmap's six questions (the recommendations stand unless you change them); and a read of Lane B's scale verdict (#5) before wiring. All given on 8 November (the roadmap's answers; real scale stands).
- **Where it is (8 November, evening).** Built on `claude/stage-3-wiring` in `C:\dev\Garage-Unreal\.cache\stage3`, not pushed. The slice's status page has the detail.
  - **Built:** the composition root (the kernel, the room, potato_man, the Gubble and the link in one game), the host's process contract, and the player's E, V and G keys through the one command path.
  - **Tests:** `tools/test-all.ps1` green, and `test_real_host.py`'s 41 pass against Unreal with the file unchanged, plus the lock suite's 6.
  - **Review:** a fresh Opus reviewer found no blockers and two majors, both fixed and rechecked.
  - **The AI played** through the unchanged companion server on Ben's plan, with no API key: the Claude CLI, asked in plain words, called the Gubble and pushed the box. Its first play found a real bug: the slice's kernel stopped a push at the 6 mm rug, where Godot steps over it. Now the box lands where Godot puts it, to the millimetre.
  - **Measured beside Godot:**
    - frame p50 12.0 ms at 1080p, against Godot's 14.0 ms on another scene;
    - link p50 7.6 ms windowed, against Godot's 10 ms;
    - launch to ready 9.0 s, against Godot's 2.0 s.
  - **The upstream launcher patch** is committed here (`f429366`).
  - **Carried forward, not fixed:** the slice's drop and place still differ from Godot, as its push did. The Gubble blocks the player's drop, and the player can place a thing inside their own body. It's a small port for the slice's next round, or for the gate if Ben sees it in play.
- **Left for Ben:**
  - a sign-off to push the branch and open its pull request, then the merge (his click);
  - whether the Gubble's opaque white body (not its see-through bubble) is fine for the gate;
  - a windowed look, since nobody has played the slice's keys on a screen yet.

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
   - **From the AI's play of the Unreal slice (8 November), true of Godot too:**
     - a push's answer doesn't say how far the thing moved, so the AI assumed the full distance;
     - `come` answers before the Gubble arrives, so the AI's first push came back "out of reach" (it learned to `go_to` the box);
     - `SandboxPhysics.cs:24` says a push stops at "both avatars", but the Gubble's layer (4, `CompanionAvatar.cs:337`) is outside the push's masks, so only the player's body stops one. Either the comment or the masks is wrong; that's Ben's call.
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

- **Item 1, the two unread plays debriefed (8 November).**
  - [2026-10-25-landscape_my_garage.md](docs/runs/playtests/2026-10-25-landscape_my_garage.md), plus an addendum to the 17 October debrief and two rows in the index.
  - **What the 25 October play found:**
    - the freeze after the intro is now 1.3 s on the real garage;
    - every apple picked costs a 60 to 73 ms hitch;
    - a steady 40 to 60 ms hitch shows in most windows;
    - two falls cost 3.5 and 2.5 of 5 hearts;
    - a lift ran out of pips mid-air without warning;
    - E says nothing about eating when no pocket slot is chosen;
    - the reader's height bands are wrong on an island.
  - **Kept:** food healed 3.75 hearts, and every refusal was shown on screen.
  - **Ben's questions are in the debrief:** fall damage, how dark the night is, and anything he remembers.
  - These feed items 4 to 6.
