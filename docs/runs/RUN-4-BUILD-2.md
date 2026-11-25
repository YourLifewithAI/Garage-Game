# Run 4: the next build (plan of 25 November 2026)

**Status, 25 November 2026.** Batch A is merged, closed and pushed (`run4/integration` at `c595f407` on GitHub). Batch B (the kernel, body and look lanes) is building. This page orders the build that runs beside it and after it. [RUN-4.md](RUN-4.md) section 3 holds every packet's row, and rulings 20 to 35 there are this plan's; where this page and the brief differ, the brief wins.

The packets are in [RUN-4-BUILD-2.packets.json](RUN-4-BUILD-2.packets.json): 84 in all, 29 of them in the next build (`first_build`). Each carries its page, paths, done-when, note and afters, and a `start`: `now` for the 38 a builder can begin beside batch B (20 of them in the next build), `after-B` for those that wait on a lane or a file of B's. Each is one sitting for a builder who has only the packet. Five are on no design page, and this page is theirs: SOUND-22 and SOUND-23 (the bench), SOUND-24 (the integrator's lines), DIAL-21 (the moment's holder), LEAF-18 (the import proof).

**Who does what** (the founder's word of 25 November): the planning stays with the lead. Every build, review, fix and measuring agent runs on a coding model. Nothing here was built, run or heard.

## What the build must answer

| His word | Met by |
|---|---|
| Sound he can bear, with sliders | SOUND-22, SOUND-1a, SOUND-1c, SOUND-2a, SOUND-2b, SOUND-5 |
| A way to tune sound together, scenario by scenario | Listening sets one and two as files (SOUND-22, SOUND-23); the Listening Room in the game (SOUND-3a, SOUND-3b, SOUND-3d, SOUND-24) |
| Made trees with leaves and branches | LEAF-1a, 1b, 1c, FLORA-2, LEAF-5a, LEAF-5b (trees and limbs); LEAF-6, LEAF-2a, LEAF-2b (leaves) |
| At least four distinct grasses | LEAF-3, LEAF-15, LEAF-4a |
| A first dial he can turn | DIAL-1a, DIAL-2 (the rows); DIAL-1b, DIAL-21, DIAL-3, SOUND-16a (the ear answers the row) |

## Before any queue starts (the lead's steps, not a builder's)

1. **Done.** Three shared worktrees, cut from the integration head, hold today's queues (next section). The plan first asked for one worktree a lane (`sound`, `habitat`, `folk` new, and `menu`, `flora`, `world` brought up to the head); the lead built three instead.
2. **Done.** BODY-11 as dispatched names no audio file: its packet owns the key and the eight delays only, and ships the call silent (ruling 23).
3. **Done.** LOOK-12 is struck (ruling 23). Its three sounds have new homes.
4. **Open.** The likeness review for LEAF-4a (ruling 35): five lines, by a reviewer who builds none of it. LEAF-4a is the only packet of this build that waits on one.
5. After any change to the packets, change both copies byte for byte (this page's file and the plan's own, `.cache/scratch-integrator/run4-design-2/plan/packets.json`, which Git ignores) and run `python -B .cache/scratch-integrator/run4-design-2/plan/check_plan.py` from the repository's root. It reads only. It holds the ids against the pages and the brief, every After, every path that is not new, the order of two lanes on one file, and that no `now` packet edits a file batch B has open. The checker lives only in the integrator's checkout; a session without it checks the same things by hand.

## Today, beside batch B

Batch B holds three lanes (kernel, body, look). Today's packets run in three shared worktrees under `C:\dev\Garage-Game-run4\`, each on its own branch from `run4/integration`, each with one builder who takes its packets top to bottom; a packet whose afters are not met steps aside. A packet built in a shared worktree still writes only its own lane's paths and its grants (ruling 33). No queue today takes the GPU token.

**`tune`** (branch `run4/tune`): the whole C# queue, in the plan's order. One worktree holds it all, so the merges the plan set between its rows fall away: each row builds on the one before, and integration takes the branch as the lead decides. Lean testing: the packet's own test scene while it works, both runners once at its end.

| Order | Owner | Packets | Then |
|---|---|---|---|
| 1 | U | SOUND-2a | The sound rows can register |
| 2 | S | SOUND-1a, SOUND-1c, SOUND-2b | Bearable sound and four sliders in play |
| 3 | U | SOUND-5 | The sound page before play. **Tier 1 is whole** (with the bench's and the generator's first rows) |
| 4 | I (built by the world lane's grant) | DIAL-1a, DIAL-1b | The four seams' first two commits |
| 5 | S | SOUND-3a, SOUND-3b | |
| 6 | U | SOUND-3d | The Listening Room opens from the settings page and by `--listen` |
| 7 | L (built by lane N's grant) | LEAF-3 | |
| 8 | S | DIAL-3, SOUND-16a | Together, never one without the other (ruling 25) |

**`bench`** (branch `run4/bench`), Python only, no Godot, no .NET, nothing installed:

| Order | Owner | Packets | Then |
|---|---|---|---|
| 1 | S | SOUND-22 | The lead hands him listening set one, as files, the same hour |
| 2 | S | SOUND-23 | Listening set two, as files |

**`generator`** (branch `run4/generator`, the generator's batch branch), Python only:

| Order | Owner | Packets | Then |
|---|---|---|---|
| 1 | F | LEAF-1a, LEAF-1b, LEAF-1c | Five trees pinned. `make --preview` gives their outlines as pictures to hand him |
| 2 | F, I (the exporter, on a grant) | FLORA-2 | |
| 3 | H | LEAF-5a | |

The bench and the generator queues run side by side. Integration takes `run4/generator` once, after LEAF-5b, so the fixtures rekey once and the 10x Island's save starts fresh once.

**Listening starts before any build.** Set one needs only SOUND-22: eight scenarios as sound files and one local page with the seven answers. He plays it in a browser; the page saves his answers as one small file; the lead reads it with the bench's tally. The bench's numbers pick what he hears, and his answers pick what ships. No packet waits for a round (ruling 24). Sets and answers never reach Git (ruling 26).

## When batch B merges

B's lanes merge one by one. What each one releases:

| Lane in | Releases |
|---|---|
| kernel | Nothing of this build. DIAL-5a and DIAL-4 (the build after) queue behind LOOK-25 and MENU-16 |
| body | SOUND-24's call line. LEAF-6, once the look lane is in too |
| look | DIAL-21, DIAL-2, the rest of SOUND-24, LEAF-15, LEAF-6, LEAF-2a, then LEAF-2b and LEAF-4a. LEAF-5b too: it holds no file of B's, but `generate.py` and `checks.py` are one-editor files and its `-Scale` run is heavy |

With all of B in, three lanes build at once and one holds the GPU token. The owners are named; which worktree builds each is the lead's call at dispatch.

| Round | Owner | Packets | Token |
|---|---|---|---|
| 1 | P | LEAF-6 | |
| 1 | L | LEAF-15, LEAF-2a | |
| 1 | H, in `generator` | LEAF-5b, then the generator batch merges | |
| 2 | L | LEAF-2b | Three pairs, on and off |
| 2 | U | DIAL-2 | |
| 2 | I | DIAL-21 (once DIAL-3, `tune`'s last row, is in), SOUND-24 | |
| 3 | L | LEAF-4a | Pairs, and one still of the grasses |
| 3 | `tune` | Whatever of its queue is left; LEAF-3 must be in before LEAF-4a starts, and DIAL-3 before DIAL-21 | |

## What moves or drops in the batch already planned

- **LOOK-12 is dropped** (done). Its three sounds have new homes (RUN-4.md, ruling 23).
- **BODY-11 is trimmed** (done as dispatched): key and delays only; no sound, no audio file. SOUND-24 joins it to the call's sounds.
- **Keep LOOK-9, LOOK-1, LOOK-2, LOOK-22, LOOK-23.** The rows lean on LOOK-9. LOOK-2, 22 and 23 pay for the leaves and the grass: the frame is over its budget today.
- **Let LOOK-26 step behind LEAF-6** if it is the last thing holding the look lane. It pays for nothing this build shows, and it holds the copies view. LEAF-6's After then loses LOOK-26.
- **The GPU token goes to LEAF-2b and LEAF-4a first.** LOOK-3's timing and R4-10's pair wait behind them: they are measures, and he sees neither.
- **Keep MENU-16, LOOK-25, FLORA-6, BODY-4, BODY-12 and R4-10** as they are.
- **Cut from this build, to the next:** picture days on the title page (DIAL-15, which the dials page had in its first build), the singers and the cold (SOUND-16b to 16d), music's first visit (SOUND-17), the kit redrawn (LEAF-13), falling leaves and litter drawn (LEAF-14), the saved dial (DIAL-4 to DIAL-7). At leaf fall in this build he hears the dry leaves and sees no litter yet. LEAF-14 is the next build's first packet for that reason.

## If he sits early (D10)

| Tier | Whole when | He gets |
|---|---|---|
| 1 | `tune`'s rows 1 to 3, the bench's SOUND-22 and the generator's LEAF-1a to 1c (9 packets) | First aid's sound, the sliders in play and before play, listening set one, the five trees' outlines as pictures |
| 2 | The rest of today's three queues (11 packets) | The Listening Room in the game with round one, listening set two. The seams, the grass map and the dry-leaf steps are in, and wait for tier 3 to be seen and heard in a room: until DIAL-21 a room sounds as first aid's bank under the year's rules, with no litter |
| 3 | The rounds after B (9 packets) | Made trees on the 10x Island, leaves near the eye, the grasses, the year and hour rows, dry leaves underfoot |

All five of his words are met only at tier 3.

## What he will see and hear at his next sitting that he did not before

1. The game opens quieter. Steps come in eight variants a ground and sit under the world, not over it. No beeping insects, no tick on hover, no loop short enough to learn.
2. In the pause panel, four sliders: Everything, The World, Actions, and Music, which says "not yet". Each plays its own kind when he lets go.
3. The same page before play, from one small link on the title page.
4. A "Which sounds" row sets the old sound beside the new, and a "Calm ears" switch softens the top.
5. Two listening sets as files, each with a page that takes seven answers. In the game, the Listening Room: a first card, then round one, at most 12 items in 300 s.
6. On the 10x Island, five kinds of made tree: oak, apple, birch, spruce and pine. About a quarter as many trees, up to 1.5 m tall, young ones among them, apple trees by each camp.
7. Limbs, forks and elbows he can stand on, with seats under 0.45 m that a new climber reaches.
8. In every room, his garage's island too: leaves with outlines on twigs, near the eye, moving in the wind.
9. At least four grasses he can tell at a glance, each a sign of its ground: rush and sedge by water, tall oat grass in hollows, fine fescue on dry tops, short turf at camps.
10. A year row and an hour row in the settings, which T and Shift+T also step. At leaf fall his feet crackle on dry leaves under the broadleaves, go soft for 90 s after rain, and tick on frost at a freeze-up dawn.

Batch B adds its own: the call and its echoes, the Gubble as spotter, the rim and the wrap, a day in two minutes.

## Not verified

- Nothing was built, run, measured or heard for this plan. The bench's numbers are the read folder's, made on the old bank only.
- The plan was checked on 25 November against the pages, the brief and the tree at `c595f407` (`check_plan.py`; its table of who writes which path is the checker's own reading of each packet). It was run again on the brief as edited with this page, and holds. What batch B's lanes will still edit is read from their rows and their branches as they stood that morning.
- The three shared worktrees and their branches are the lead's; this page records them as the lead reported them and did not open them.
- Whether the frame can pay for leaves and grass is LEAF-2b's and LEAF-4a's pairs to say. Each ships the richest setting that passes, down to its floor.
- LOOK-9's wet and dew rules were read on `run4/look`, not on integration; they may still change before the lane merges.
- Whether the engine keeps a second texture set and the colour's alpha on import is LEAF-18's to prove, in the build after.
