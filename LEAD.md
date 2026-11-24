# Garage Game: the lead's backlog

Kept by the Garage Game's lead session (started by Wren, 8 November 2026; a new lead session since 23 November). One ranked list for the whole game: the Godot game in this folder and the Unreal slice in `C:\dev\Garage-Unreal`. [docs/runs/RUN-2-STATUS.md](docs/runs/RUN-2-STATUS.md) stays the detailed record ("The next session" and each round's questions); this file sets the order and points there for detail. Work runs one item at a time, top down.

**Ben's answers (8 November, 11:36, relayed by Wren):** the order as ranked; go on the Unreal slice's stage 3 with its roadmap's defaults; the kids play on DiamondAge (this laptop); Ben himself played both unread plays; yes to merging #20 and closing #17 and #12. Auto mode refused the lead those GitHub writes on the relay; Ben then gave his sign-off in the lead's session that evening, and the lead did them (H2).

**Ben's answers (8 November, 17:59, relayed by Wren):** the Garage Game is a fun project now, behind the dental office and Life with AI. Finish what's in flight (the kids' gate on DiamondAge), then pause: no new work until he says. The kids wanted the Gubble see-through, so it's made see-through before their gate. His five-minute look at the slice may or may not happen first; the gate doesn't wait on it. Yes to pushing the three branches, which he then typed in the lead's session ("Yes, push all three"): `run2/integration` here at `0f9d541`, the slice's `main` at `b5edf01`, and `claude/stage-3-wiring` with its pull request, [Garage-Unreal#7](https://github.com/YourLifewithAI/Garage-Unreal/pull/7).

**Run 3 (19 November):** Ben approved the story run in a planning session ([docs/runs/RUN-3.md](docs/runs/RUN-3.md), section 7) and moved it to this laptop. That lifts the pause for Run 3's work, on the Godot game. [docs/runs/RUN-3-STATUS.md](docs/runs/RUN-3-STATUS.md) is its handoff, on `run3/integration`. Its batches take over the items below that they cover: item 5's freeze is L-perf, item 7's sound is the Sound lane, item 10's opening is O1 and O2, and item 4's first look happens as each batch is shown to Ben. Item 3, the kids' gate on the Unreal slice, has not been played and is still Ben's to call.

**Sharing the game (Ben, 18 November, in the Run 3 session):** "we need to consider how we can make this game transferable and shareable. As in, how can other gamers take this game, copy it, and make it their own using their own AI, and how do we centralize a way for everyone to safely share the experiences they create?" He asked for it once Run 3's first batch is in. It is a design question, not yet a packet: nothing is built for it. On 19 November, on his word, the Run 3 session wrote its answer up as [docs/ideas/SHARING-THE-GAME.md](docs/ideas/SHARING-THE-GAME.md): a licence first (the repository is public and has none), a Windows build that carries the companion, one data-only "experience package" with an export gate and an import gate, and a curated gallery that starts as a GitHub repository. It is a proposal; its nine decisions are Ben's and none is made.

## Where things stand (23 November 2026)

A new lead session keeps this file from 23 November, started by Wren on Ben's word; the lead of 8 November is archived. Run 3 was run on 18 and 19 November by its own orchestration session, which is gone: [RUN-3-STATUS.md](docs/runs/RUN-3-STATUS.md) is its record. The 8 November section below is kept as history.

- **Branches.** `run3/integration` is at `0a47245`, the same on GitHub, and 35 commits ahead of GitHub's `main` (`f7af65f`), which Run 3 has not touched. The three lane branches (`run3/contracts`, `run3/look-perf`, `run3/sound`) are merged, local only, and their worktrees under `C:\dev\Garage-Game-run3` are kept. Draft [pull request #21](https://github.com/YourLifewithAI/Garage-Game/pull/21), the planning branch, is still open; everything in it is inside `run3/integration`.
- **The Unreal slice.** `C:\dev\Garage-Unreal` `main` is at `6ea8d89`: one status commit ahead of GitHub's `8869523`, not pushed. No pull request is open, and nothing has been done there since 8 November.
- **What plays today, and what opens it.** Four desktop shortcuts, each `pwsh -NoProfile -File C:\dev\Garage-Game\run-room.ps1` with its own arguments, started in `C:\dev\Garage-Game`. Every launch builds first and records the play.

  | Shortcut | Arguments | Opens |
  |---|---|---|
  | Garage Game Menu | none | The opening menu: the team, one of four islands (the test room, the garage as a room, the stand-in island, the real garage), the mode and the difficulty |
  | Garage Game My Garage | `-Room landscape_my_garage` | The island made from Ben's own garage, in the mode played last. An older island: it has campfires and no Fractals |
  | Garage Game Landscape | `-Room landscape_garage_nominal -Mode challenge -Difficulty standard -Fullscreen` | The stand-in garage island, Challenge standard, full screen |
  | Garage Game 10x Island | `-Room landscape10` | The stand-in island at ten times the size, the one Run 3 builds on (Fractals, the summit, the look pictures). It was built into the cache on 18 November |

  - All four carry Run 3's sound: ambience and effects made in code, no music.
  - The story has nothing to play yet. Its contract and an example pack are in; the quest ledger (Q1) is not started.
  - The slice plays with `C:\dev\Garage-Unreal\run-room.ps1` and has no shortcut.
  - The desktop's two EnFractal shortcuts belong to the EnFractal repository.
- **A play on 23 November.** Record `20261123-193837-landscape_my_garage`: 2 minutes 19 seconds on the real garage, from the menu, Challenge standard, build `0a47245`, quit cleanly. It is the first play of a build with sound. Not debriefed.
- **Run 3, what is left.** Batch 1 still owes Q1 (the quest ledger), L-perf round 2 and then L-air, sound's two small follow-ups, the Linux suite on the batch, and the batch's pull request to main. Batches 2 to 4 are not started. The order is in RUN-3-STATUS.md, "The next session".
- **Wren's project record** still lists the game as paused (9 November). Run 3 lifted the pause for its own work on 19 November. Updating it is Wren's.

### Waiting on Ben (23 November)

Each with the lead's recommendation. No new build work starts until Ben says.

1. **His play notes** from 23 November: what he saw and heard. The lead then debriefs the record with `/playtest`.
2. **Run 3: go on, or pause again?** Recommended: go on, one lane at a time, Q1 first.
3. **Sound.** Whether the first sounds are gentle enough, and where music comes from. Recommended: no music until the first night plays; then made in code or from CC0 libraries, never bought.
4. **The look's open values.**
   - The frame budget: two views sit at 18 to 19 ms against 16.7, and small plants drawn to 30 m instead of 40 would fix it at a visible cost. Recommended: keep 16.7 ms and judge the 30 m and 40 m pair once L-perf round 2 renders it.
   - How dark the night is. Recommended: a lighter night, with lit windows in the hamlet.
   - Whether the day's light follows the day's length. Recommended: fixed hours, with the peak moved to noon.
5. **The story's words.** Who each islander is and what it wants; who carries the fourth friend's need (three islanders, four needs); and where agents' drafting stops. Recommended: agents write the beats and the machinery, and the family supplies every line a player reads, by voice once V2 exists. Nothing of the tale is shared before Ben's own pass.
6. **The controls table** (RUN-3.md, section 7), marked up before U2. Recommended: as proposed.
7. **Sharing** ([SHARING-THE-GAME.md](docs/ideas/SHARING-THE-GAME.md), nine decisions). Recommended: the licences first (Apache-2.0 for the code, CC BY 4.0 for the art, the children's characters and the family's story kept the family's); the other decisions wait for the story run to end.
8. **The kids' gate on the Unreal slice** (item 3): move, stay, or keep both. Recommended: stay on Godot, where Run 3 builds; the slice parks as it is.
9. **Batch 1's pull request to main,** after a green Linux suite in a cloud session, and closing draft #21 once it exists. Each GitHub write needs Ben's own typed line in the lead's session.
10. **Small ones.**
    - Pushing the slice's one status commit.
    - The weekly making journal (`codex/daily-journal`, still written each week, last on 22 November, local only): keep it and merge it, or stop it. Recommended: keep and merge.
    - H3's finished worktrees, which Ben removes with his own script.
    - The gamepad as a first-class way to play. Recommended: yes, as provisionally built.
    - Whether a child's live talk with a character may run on the family's plan: a question for Anthropic, and nothing waits on it.

### Ben's direction (23 November, typed in the lead's session)

- **Stay on the Godot game for now.** That answers item 8 above: the Unreal slice parks as it is.
- **A large upgrade, Run 4,** with a team of agents:
  - an opening menu that is good to look at;
  - a New game and a Continue, so nothing old comes back unasked;
  - a world that is plainly the room, with floors as plains, lakes or savannah by their colours;
  - the player effectively 1 cm tall against the room, with physics and movement rewritten to match;
  - real-looking trees of several kinds, up to ten grasses each with its niche, bushes with fruit and nuts;
  - little villages with people living their lives;
  - substantial improvements to the graphics and to movement.
- **Run 3's story lanes park** (the quest ledger, the tale, voice) until the new world exists, because a story pack is tied to an island's places. Its look, plant, UI and character lanes fold into Run 4.
- **Where it is.** A design team mapped the code and is writing `docs/runs/RUN-4.md` and six `RUN-4-*.md` design pages; build lanes start from that brief, in `C:\dev\Garage-Game-run4\<lane>`.
- **Ben on what the world should look like (23 November, 20:20, word for word).** This came after the six design pages were begun, so `RUN-4-WORLD.md`, `RUN-4-FLORA.md`, `RUN-4-LOOK.md` and the brief must be brought into line with it:

  > "So when I'm thinking of what I want the Garage Games map to look like, I want it to look like the room, but from the centimeter scale, from the player look being that small. But, you know, the chairs would be landscape that's covered in vines that they have to scale, or the rocky cliffs that clearly look like a chair but are still also obviously landscape. Desks would be high mesas that they have to scale up to get to. I want the floors and everything to have some form of geographical context, you know, following the basic physics of geology. I would want there to be forests scattered throughout, and meadows and paths worn by animals, things of that nature, to kind of make the whole area, the world, feel lived in."

  The lead's reading of it:
  - The map is the room itself at the player's scale: every object stands where it stands in the room, at its true size against a 1 cm body, and keeps its shape. A chair is a chair-shaped cliff or a vine-covered climb; a desk is a high mesa.
  - Each object is plainly landscape as well: rock, soil, vines and plants, not furniture with a texture.
  - The ground obeys simple geology: water runs downhill and pools in low places, cliffs shed scree, soil gathers where it would, and the floor's regions have a reason to be plain, lake or savannah.
  - The world is lived in: forests in places, meadows, and paths worn by animals.
- **Found on the way.** The game's window is titled "Garage-Game (DEBUG)", and the GPU courtesy check looked for `Garage Game*`, so it never saw a game in play. AGENTS.md and the two capture tools now match both.

## Where things stood (8 November 2026)

- **The Godot game.** `main` is at `f7af65f`: pull request #19's code (merged by Ben on 25 October, with both Windows runners and the Linux suite green) plus #20's two docs-only commits, merged on 8 November. Nothing has been built since 25 October.
- **Built and never seen on a screen.** The 24 and 25 October rounds built the Esc menu, the camp screen, the pockets and chest, food, the hotbar, Grow's unlocks (sprouts, replanting, the bridge, ivy) and the repair card. They were tested headless only. Each packet's "first look" list is in the status page.
- **Two plays recorded and never debriefed.** Both are in the game's user folder on this laptop:
  - `20261017-141154-landscape_garage_nominal`: 15 minutes on the stand-in garage, build `bb9f47f`.
  - `20261025-180114-landscape_my_garage`: 11 minutes on the real garage, build `94da603`. This is the newest build, played an hour after #19 merged, and the only play of the hotbar, pockets, food and Grow's unlocks.
- **The Unreal slice** (`C:\dev\Garage-Unreal`, private repository `YourLifewithAI/Garage-Unreal`). Ben started it on 31 October, after the look test in which the kids chose the Unreal views, "to decide whether the game moves". Stages 0 to 2 are merged (pull requests #1 to #6, the last at 22:31 on 31 October). Stage 3 is merged too ([pull request #7](https://github.com/YourLifewithAI/Garage-Unreal/pull/7), `8869523`, on 8 November; item 2), so stage 4, the kids' gate, is next.
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
- **Where it is (8 November, evening).** Built on `claude/stage-3-wiring` in `C:\dev\Garage-Unreal\.cache\stage3`, pushed on Ben's yes as [pull request #7](https://github.com/YourLifewithAI/Garage-Unreal/pull/7) into the slice's `main`. The repository runs no CI; the pass line is in the pull request. The slice's status page has the detail.
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
  - **The Gubble is see-through (8 November, evening),** as the kids asked: the Garage Game's soap bubble ported to the slice's import, and Nanite turned off for its mesh, since Nanite doesn't draw see-through materials. A window still shows the room through its body. The slice's status page has the detail.
- **Left for Ben:**
  - ~~the merge of [#7](https://github.com/YourLifewithAI/Garage-Unreal/pull/7)~~: merged by the lead on Ben's "Go ahead and merge", typed in the lead's session on 8 November, as `8869523`, with the branch kept. `main`'s tree is the tested branch's, so the green run on `d546edf` stands for it;
  - a windowed look if he has time, since nobody has played the slice's keys on a screen yet.

### 3. The kids' gate

- **What:** Ben and the kids play the slice next to the Godot room (the roadmap's stage 4).
- **Done when:** Ben's word (move, stay, or keep both for now) is written in both status pages, and items 4 onward are reordered to match.
- **Ben:** the play itself. Only Ben passes a gate.

### Then, while the Godot game is the one the kids play

**Paused after item 3** (Ben, 8 November): nothing below starts until he says.

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
- **H2.** ~~Merge #20, close #17 and #12~~: done 8 November, on Ben's sign-off typed in the lead's session.
  - [#20](https://github.com/YourLifewithAI/Garage-Game/pull/20): marked ready and merged with a merge commit (`f7af65f`);
  - [#17](https://github.com/YourLifewithAI/Garage-Game/pull/17): closed (the planetoid direction belongs to the EnFractal repository);
  - [#12](https://github.com/YourLifewithAI/Garage-Game/pull/12): closed (superseded by `docs/magic/README.md`).
  - All three branches are kept on GitHub.
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
