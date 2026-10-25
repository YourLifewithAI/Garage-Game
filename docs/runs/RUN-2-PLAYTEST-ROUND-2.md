# Run 2: the second recorded play's round (queued 17 October, to start 20 October)

**Read with** [playtests/2026-10-17-landscape_garage_nominal.md](playtests/2026-10-17-landscape_garage_nominal.md) (the debrief this round answers; its "Change" list of ten is this brief's input), [MECHANICS.md](../game/MECHANICS.md) decisions 81 to 89 (and the ones they replace: 43, 60, 62, 73, 77), [RUN-2-PLAYTEST-ROUND-1.md](RUN-2-PLAYTEST-ROUND-1.md) (the shape this brief follows), [OWNERSHIP.md](OWNERSHIP.md), [ORCHESTRATION.md](ORCHESTRATION.md), [RUN-2-STATUS.md](RUN-2-STATUS.md) and [AGENTS.md](../../AGENTS.md).

**Queued, not started.** The founder's weekly allowance was at 4% when this was written (it resets on Tuesday 20 October); they asked for the changes to be written up and queued. Nothing here is built. The next integrating session starts at "Questions for the founder", then P0.

## What the founder found (17 October, DiamondAge, "EnFractal Landscape" at `bb9f47f`)

The debrief has the words, the numbers and the causes; in short:

- **No in-game menu:** full screen with no easy way out; Esc should open save, load, settings and the main menu.
- **Fire:** it hurt them, which is right; X putting it out is not; 6 pips is too much and the preview-then-hold is too slow.
- **The camp:** sitting changes nothing on screen, nothing says the fire can be used, and the camp should be where the player's own things are managed. One key for the whole world.
- **Hearts:** healing by themselves is lame (one of the kids); food and resting at camp should heal. The campfire should hurt a player standing in it.
- **Heights:** a bubble's slow drift down takes the risk away; out of a bubble should be a fall, with a wall to grab and the bar deciding the rest; the bubble's time should be a ring of light counting down.
- **The message line was never seen.** Found in the record: the reader's silent refusals were the lead's previews (a recorder bug), and the freeze after the island appears has grown to 941.6 ms.

## Questions for the founder (Tuesday, before P1 and P2 start)

Each has the integrator's proposal. **All answered on 17 October (evening), MECHANICS.md decisions 90 to 97:** 1 E; 2 yes, a quarter a second; 3 apples half a heart and berries a quarter (not the proposal's one and a half), eaten on the spot, on certain trees and bushes; 4 yes, no healing while closed; 5 yes, 1 pip; 7 no, **8 seconds**; 8 yes, but only for a pop the Gubble causes by its own action, never the time limit or the player's own cancel. 6 (decision 97): the first Esc cancels what the player is doing, the next opens the menu; with nothing going on, one Esc opens it.

1. **The interaction key:** E (the usual PC key, beside WASD) or Enter? Proposal: **E**, with Z kept for a round as a second key for the camp.
2. **Resting at camp heals** one quarter of a heart a second (five hearts in 20 s)? Proposal: yes.
3. **Food, first cut:** apples on some trees and berries on some bushes, picked with the interaction key and eaten at once until there is an inventory; an apple one heart, berries half; they grow back after a day. Fish when there is a way to catch them. Proposal: yes (this is D1, shown before any food code).
4. **Hearts while away:** today they heal while the game is closed (MECHANICS.md, "While the player is away"). Keep, or drop with self-healing? Proposal: drop; you wake with what you had.
5. **Fire as the Gubble's own idea** costs half, so 1 pip? Proposal: yes.
6. **Esc while something waits** (a fire's preview, a chosen bubble): first Esc calls that off, the next opens the menu? Proposal: yes.
7. **The bubble's time:** a carry lasts 20 s today. Keep it, with the ring showing it? Proposal: keep.
8. **When the Gubble's pop drops the player** and the fall hurts, it is a consequence fact that costs Trust, as its fire does (Challenge and Live; nothing hurts in Creative)? Proposal: yes.

## The packets

Six pieces. **P0** is the integrator's and lands first. **P1** (Opus) and **L1** (Sonnet) run side by side from it. **P2** (Opus) runs after P1 merges, because both touch the camp and the fire wheel. **D1** is a design for the founder and blocks only the food half of P1. **L2** runs whenever the founder is not playing.

### P0: the pack, the decisions and the recorder (the integrator)

- `game/rules/storybook_wild/v5.json` (a draft until the founder approves it; if it has been approved, write v6): Fire `ordered` 2 (`own_idea_factor` stays 0.5, so 1 as the Gubble's idea, question 5); healing outside a camp off, and the camp's resting rate (question 2), as pack values the kernel reads (`VitalsRules.HealQuarterEveryS`, `HealAfterUnharmedS`, `CampLaws.CampHeartsFactor` today).
- The recorder: `CommandHost.RaiseHandled` takes `preview` from the result, and a refused result carries none, so a refused lead preview is recorded as a refused command (the debrief's "The reader"). Take the flag from the command as received; a test with a refused preview.
- MECHANICS.md decisions 81 to 97 are in (17 October).
- The bubble's 8 seconds (decision 95), wherever its 20 s lives today (the carry's `duration_s`).

### D1: food (the integrator, a design for the founder; no code until they say)

The amounts and the places are decided (decision 92: an apple half a heart, berries a quarter, eaten on the spot, on certain trees and bushes). Left for the integrator to settle and show the founder in a line or two: which generator kinds carry them and how many, whether and when they grow back, the prompt's words, and the look. Then its code joins P1 or a packet of its own.

**The integrator's proposal (24 October, a cloud session; every number provisional, for the founder's yes or no):**

- **Where:** apples under one broadleaf tree in three, as windfalls at the trunk's foot (the canopy is out of a 10 cm player's reach), two to four each; berries on one shrub in four and on one heather clump in four, a cluster at the player's height. On the stand-in garage that is about 12 apple trees and 55 berry plants. The kernel picks them from the plant's id, deterministic and the same on every load, so no generator or manifest change is needed for the first cut; a generator field (`x_food`) can replace that later for the founder's real rooms.
- **Picking:** E facing one within reach, "E: eat an apple" or "E: eat berries", eaten on the spot (decision 92): an apple half a heart, berries a quarter. Nothing is carried until there is an inventory. Picking never costs pips and the Gubble cannot pick for the player (nothing targets the player's avatar).
- **Growing back:** an apple a day per tree and a cluster a day per bush, by the island's clock (`x_clock`), so a day away restocks everything; Grow's cast on the plant ripens its food at once. Fire burns the food with the plant. Saved with the island as the plant's picked count and the time of the last pick.
- **The look:** an apple a red sphere 1 cm across with a stalk; berries a cluster of six 0.3 cm dark blue beads on the shrub, read from a band away; a picked spot shows nothing until it grows back. Look's budget as for the Fractal's star: unshaded, no shadow, at most 64 drawn.
- **The founder's answers (25 October, decisions 108 and 111):** windfalls at the trunk and fruit in the canopy with a hop; Creative's food heals nothing but eating keeps its sound; the Gubble does not point food out; eating heals at once, with no wait. Picked food goes to the pockets (decision 98). The packet P-Food builds it after the inventory.
- **Then:** its code is a packet of its own after P2 (the kernel's pick and heal with E, the look, the saved counts), Opus for the kernel and Sonnet for the look.

### P1: the kernel and the body (Opus)

1. **Fire lights on the click** (decision 83): the player's Fire, from the wheel or the Magic panel, is previewed and committed in one action, so the outline flashes as it lights; no hold. The Gubble's own-idea fire and Creative's one ask about builds are unchanged.
2. **Nothing but burning out or Snuff ends a fire** (decision 82): X no longer puts out any fire (the Gubble's, a wild one, a campfire); X still stops the Gubble's moves. The Settings tooltip ("X puts its fire out") and the journal's lines change with it (P2 has the words).
3. **A campfire hurts a player standing in its flames** (decision 85), at fire's quarter a second; the resting spot beside it is safe.
4. **Hearts heal only at camp and by food** (decision 84): no healing elsewhere; resting at a camp heals at question 2's rate; waking at camp with half the hearts stays; question 4 for the time away. Food's code waits for D1.
5. **Out of a bubble is a fall** (decision 88, replacing 43 and 60's drift): diving out, a pop, the Gubble's stop and the timer all leave the player falling from where they are, with fall damage as any fall (decision 24); a climbable face within reach while falling can be grabbed as from a jump, and the bar decides the rest. A fall that hurts the player after the Gubble popped the bubble by its own action is a consequence fact that costs Trust (decision 96, `CommandHostConsequences`); a pop at the time limit or by the player's own cancel never is. Creative: nothing hurts, as today.

Tests for each; mutation checks on at most five (the drift gone, X not putting out fire, the campfire's harm, no healing away from camp, the fall from a pop). Owns `game/scripts/native/Kernel/` (nature, camps, vitals, Bubble), `SmallPlayerController*.cs`, `BodyKnacks.cs` and their tests.

### L1: the look (Sonnet)

1. **Sitting at camp looks like resting** (decision 87): while seated, the camera eases to a low view across the fire, the light warms and the world beyond dims a little; standing undoes it.
2. **The bubble's ring of light** (decision 89): a thin ring of light around the bubble that opens at one end and shrinks with the time left (the effect's own duration, never a timer of the look's); when it closes, the bubble pops. Readable at night and in rain (the debrief's stills `0023` and `0025` are the case).

Owns `game/scripts/native/Look/`.

### P2: the HUD (Opus, after P1)

1. **Esc is the menu** (decision 81): resume, save, load, settings, exit to the main menu; it pauses; while something is going on, the first Esc cancels it and the next opens the menu (decision 97). Freeing the mouse from the camera needs its own answer once Esc is the menu: say how in the report. Save and load through what `Saves/` already offers; the main menu is M7's.
2. **One interaction key with a prompt** (decision 86, question 1): a short prompt beside what the key works on ("E: sit by the fire", "E: stand up"); first the camp, a carried thing, then food and doors as they come. The H help names it first.
3. **The camp screen** (decision 87): sitting opens it; its pages gather what exists: the Magic panel, the expertise found (the journal's list), you (hearts, bar, pips, knacks), spending Fractals (decision 62's camp rules), and an Inventory page that says it is coming. No new rules.
4. **Notices where the eye is:** the debrief's founder never saw the bottom line. A notice shows near the reticle or the Gubble and fades after about four seconds; the bottom line keeps the key hints only.
5. **Fire's words** for the click that lights it (decision 83) and for X (decision 82).

Owns `game/scripts/native/Ui/`, `RoomHud.cs` and its tests.

### L2: the freeze after the island appears (carried, Sonnet or Opus)

Round 1's change requests 2 and 3, now 941.6 ms with 225 generation-0 collections in the first window, and a frame over 33 ms in 41 of 54 windows. Needs the founder's GPU: never while a window titled `EnFractal*` is open.

## Done when

- Each packet's acceptance passes with commands and output in its report; both Windows runners and the contracts green on the merged tree; the Linux suite before the PR to main.
- The founder plays the same slice again (full screen) and runs `/playtest`: the next debrief checks the ten guesses.
