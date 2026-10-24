# Playtest: landscape_garage_nominal, 2026-10-17 (Challenge, standard; build `bb9f47f`; record `20261017-133610-landscape_garage_nominal`)

**Read with** [RUN-2-PLAYTEST-LOOP.md](../RUN-2-PLAYTEST-LOOP.md), [RUN-2-STATUS.md](../RUN-2-STATUS.md), the round it tested ([RUN-2-PLAYTEST-ROUND-1.md](../RUN-2-PLAYTEST-ROUND-1.md), plus Fire's camp, Grow, Fire's behaviours and the 28-Fractal garage of 16 and 17 October), the round it feeds ([RUN-2-PLAYTEST-ROUND-2.md](../RUN-2-PLAYTEST-ROUND-2.md)) and [AGENTS.md](../../../AGENTS.md). 4.7 minutes, a clean quit, no marks, 2 refusals, both shown on screen (the reader counts 34 with 32 silent: wrong, see "The reader"), worst frame window 941.6 ms at 0:18, 95% of the time within 2 m of the Gubble. The companion was offline throughout: every Gubble action came from the founder's keys.

## What the founder found (17 October, early afternoon, DiamondAge, "EnFractal Landscape" at `bb9f47f`)

- **No way out from full screen.** "The game went full screen, which is fine [...] However, I couldn't exit very easily. The esc key is often an in-game menu key where people can choose to save, load, alter settings, or exit to the main menu." (the play ends with Esc pressed six times, then H, then Settings, still `0030-steady.jpg`)
- **The fire hurt, and that is right; X putting it out is not.** "I did notice the fire was hurting me. I don't think being able to press X to turn it off is a good idea [...] It should burn until it burns itself out or until I have snuff."
- **Sitting at the fire changes nothing on screen, and nothing says the fire can be used.** "When I went to sit down next to the fire, the scene should alter in some way to indicate that we're in a different mode. Also, when I approach the fire, there's no indicator that I can interact with the fire. I only know to press Z because that's been made clear." One key for the world: "if I want to pick an apple from a tree or pick up something to put it in my inventory, or open a door, or interact with the campsite, it's much easier if we're only pressing a single key."
- **The camp is the place for the player's own things.** "The campsite is a good location to manage their magic, learn about what expertise they've discovered, check their stats and knacks, to manage their inventory (which we haven't really built yet), and spend their Fractals."
- **Fire costs too much and waits too long.** "Let's drop it down to two pips per cast. Also, being forced to wait for a preview [...] is annoying. Let's remove the wait time. If a player gets hasty and casts Fire near themselves, that's an automatic learning moment."
- **Hearts that heal by themselves are lame** (one of the kids): "[They]'d rather we find items like apples or fish or things like that which we would then use to heal ourselves, or we could heal by sitting down at the campsite."
- **The campfire does not hurt.** "The campfire doesn't hurt the player when the[y] stand over it. It should hurt them."
- **The message line was never seen.** Of the two lines that stood on it for over a minute: "I don't remember seeing these messages."
- **A bubble takes the risk out of heights.** "Falls should hurt, and I think falling out of a bubble should hurt as well. Right now you drift gently down to the ground [...] I'd like to be able to pop out of a bubble and grab a wall, then be forced to rely on my stamina to make the rest of a climb." And the bubble's time: "a ring of light around the bubble that acts as a countdown"; when the ring is gone, it pops.

## What the record shows

- **The play, in order:** H help at 0:41 and 1:05; a rest at the starting campfire at 1:07 and up at 1:11; the Magic panel; Fire at 1:29 previewed ("0 things may be affected") and lit at 1:37 about 0.2 m from where the founder stood; hearts 5 to 4 within four seconds and down to 2.5 by 2:09; a second Fire at 2:02 refused ("Let the current fire finish first", shown); a third lit at 2:23; a rest at 2:33; a run east with the time and season keys pressed (3:06 to 3:32, which is why it turned to night and rain); a Fractal picked up at 3:24 (the first in a real play: `item.pickup` OK); a climb of the rock block toward the glow on its top from 3:36, the bar empty at about 1.2 m, a fall at 3:44 costing 2.25 hearts; a Bubble at 3:50 (pips 9 to 5); the lift up the block refused at 3:54 (it costs 6), then led at 4:00 (pips 7 to 1) and arrived at 4:06; the founder's stop at 4:08 and back on the moss at the block's foot by 4:14; about 30 s standing still, Esc six times, H, Settings, quit.
- **The refusals:** two real ones, both on screen: `target_busy` at 2:02 (still `0011-refusal.jpg`) and `energy_exhausted` at 3:54 (`0023-refusal.jpg`). The other 32 the reader lists are the reticle's lead previews (one every 0.2 to 0.25 s while the aim moved), which showed as the tag "lift it there needs 6" (`0023`, `0025`).
- **The frames:** the worst, 941.6 ms at 0:18, is the first frames after the island appeared (ready at 0:13), with 225 generation-0 collections pausing 310.6 ms in the window; the last play's was 535 to 561 ms, so it has grown (28 Fractals and Fire's camp since). 41 of 54 windows had a frame over 33.3 ms; the mean is 13.4 ms.
- **Where the time went:** standing 63%, walking 13%, running 6%, climbing 4%, carried 9%; all of it in the lowest height band; 95% within 2 m of the Gubble; companion offline.
- **About the record:** a clean quit; the frame events carry `frames` and the stills `grab_ms`, which this reader does not know yet (allowed by the contract).

## The causes (the integrator, from the source)

- **The preview's count leaves the player out.** `RoomHud.FireWheel.cs` `WheelFireWords` says "{affected} things may be affected" from the host's `affected_ids` (`CommandHostNature.cs`), the burnable things the team knows; the player inside the outline is not one. Both fires hurt the founder, and a fire that hurts the player teaches no Fire expertise (`CommandHostFireKinds.MarkFireHarm`), so neither counted toward Fire's.
- **Hearts heal anywhere.** `VitalsRules.HealQuarterEveryS` (2.5 s) after `HealAfterUnharmedS` (10 s) unharmed, twice as fast at a camp (`CampLaws.CampHeartsFactor`, decision 62).
- **Fire's price and wait are data and HUD.** `game/rules/storybook_wild/v5.json` prices Fire at 6 ordered (half as the Gubble's idea); decision 77's tap-to-preview, hold-to-light is `RoomHud.FireWheel.cs`.
- **The bubble drifts down by decision.** Decisions 43 and 60: a bubble that pops in the air deflates into a slow drift, so a carry never ends in a fall. The lead at 4:00 arrived at its spot beside the block; the stop at 4:08 ended it there and the founder drifted to the foot.
- **The message line is easy to miss.** The notice line sits at the bottom edge under "H keys" and keeps its last words until another notice replaces them (`RoomHud` `_noticeText`): "You sit down by the campfire and rest." stood from 2:33 past 3:43, and "The Gubble needs a moment." from 3:54 to the quit. The founder saw neither.

## What the founder decided

The founder's answers of 17 October, folded into [MECHANICS.md](../../game/MECHANICS.md) as decisions 81 to 89: Esc is the in-game menu (81); fire burns out or is Snuffed and X no longer puts it out (82); Fire costs 2 pips and lights at once (83); hearts heal only by resting at a camp and by eating found food (84); a campfire hurts a player standing in it (85); one interaction key, with a prompt at what it works on (86); sitting at a camp changes the scene and opens the camp, the place for magic, expertise, stats, knacks, inventory and Fractals (87); falling out of a bubble is a fall, with a wall to grab (88); a bubble's time is a shrinking ring of light (89). The details still to settle are the questions in [RUN-2-PLAYTEST-ROUND-2.md](../RUN-2-PLAYTEST-ROUND-2.md).

## Keep

- **Fire hurting in Challenge.** The founder noticed it and wants it.
- **The walk-in Fractal pickup.** It worked the first time in a real play.
- **The refusals' words.** Both real refusals were on screen, in the Gubble's bubble and the line.
- **Fall damage from a climb** (2.25 hearts from about 0.6 m): the founder wants falls to hurt.

## Change

Written as guesses the next play can check; the packets are in [RUN-2-PLAYTEST-ROUND-2.md](../RUN-2-PLAYTEST-ROUND-2.md).

1. **The founder will leave and come back through Esc** without hunting: a menu with resume, save, load, settings and the main menu. HUD (P2).
2. **Fire will feel cheap and quick enough to use as a tool:** 2 pips, lit on the click, burning until it burns out or Snuff. Pack (P0), kernel and HUD (P1, P2).
3. **A player will look for food and the camp when hurt,** once hearts stop healing by themselves. Pack and kernel (P0, P1); food needs a design first (D1).
4. **Standing in a campfire will hurt as any fire does.** Kernel (P1).
5. **One key and a prompt will be enough to use the world**, starting with the camp, a Fractal and a carried thing. HUD (P2).
6. **Sitting at camp will read as a different mode** and be where the player spends Fractals, chooses knacks and sees expertise. Look (L1) and HUD (P2).
7. **Heights will matter again:** out of a bubble is a fall, a wall can be grabbed, the bar decides the rest. Body and kernel (P1).
8. **A bubble's ring of light will say when it pops.** Look (L1).
9. **What the game says will be seen:** notices near where the player looks, gone after a few seconds. HUD (P2).
10. **The first seconds will not freeze** (941.6 ms after the island appears). Lane L's hitch work, carried from round 1 (change requests 2 and 3).

## Drop

- **X putting out fire** (decision 82), and decision 43's and 60's slow drift down (decision 88).
- **Self-healing hearts** outside a camp (decision 84).

## Not this round

- **Inventory** (the founder: "which we haven't really built yet"): food is eaten where it is found until it exists (D1's proposal).
- **The companion playing live**: offline in this play, as in the last.
- **The time and season keys** were used to look around; nothing found in them.

## The reader

- **Refused previews are counted as refusals.** `CommandHost.RaiseHandled` (`CommandHost.cs` near line 424) takes `preview` from the result, and a refused result carries none, so the recorder writes a refused lead preview as a refused command. The fix is in the host's event (take the flag from the command), with the reader unchanged; P0 has it.
- **The worst frame's split exceeds the frame:** process 1148.5 ms and physics 1678.7 ms inside a 941.6 ms frame; the recorder's `longest_process_ms` and `longest_physics_ms` cannot both be that frame's parts. A recorder or reader finding for a Sonnet round.
- **`frames` and `grab_ms`** are newer recorder fields the reader should learn to print.
