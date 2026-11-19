# Playtest: landscape_my_garage, 2026-10-25 (Challenge, standard; build `94da6038`; record `20261025-180114-landscape_my_garage`)

**Read with** [RUN-2-PLAYTEST-LOOP.md](../RUN-2-PLAYTEST-LOOP.md), [RUN-2-STATUS.md](../RUN-2-STATUS.md) (the cloud session of 24 and 25 October: the rounds this build carried), [RUN-2-PLAYTEST-ROUND-2.md](../RUN-2-PLAYTEST-ROUND-2.md) (L1 and L2, still open) and [AGENTS.md](../../../AGENTS.md).

- **The play:** 11.2 minutes, a clean quit, no marks, 4 refusals, all shown on screen.
- **Frames:** worst window 1337.3 ms at 0:26.
- **The Gubble:** within 2 m for 55% of the time. The companion was offline throughout, so every Gubble action came from the founder's keys.
- **Firsts:** the first recorded play on the real garage, and the first play of the hotbar, the pockets, food, the Esc menu and the camp screen. It was played windowed at 1280×720, an hour after pull request #19 merged.
- **The debrief:** written on 8 November by the Garage Game's lead session, from the record alone.

## What the founder found (25 October, evening, DiamondAge, `run-room.ps1` at `94da603`)

- **No notes.** Asked on 8 November through Wren, the founder said only that he played it himself. The questions under "What the founder decided" stand in for his notes.

## What the record shows

### The play, in order

- **The start.** The intro (the room becoming the island) ran until about 0:35. The founder sat at the starting campfire at 0:57 and read the camp screen's pages (Magic, Expertise, You) for a minute (`0003` to `0007`), then stood up at 1:56. Dusk turned to night by about 2:10, and the rest of the play is at night.
- **Glow and Fire.** Glow at 2:32, a stop at 2:36, then Fire on the click at 2:39, centred about 0.55 m from where he stood with a 1.5 m radius: "You are standing in its circle: step out of the flames!" He lost a quarter of a heart at 2:51, and the fire spread as a ring through the meadow (`0017-steady.jpg`).
- **Panels.** Settings from 3:06 to 3:14, and the Magic panel from 3:30 to 3:54.
- **The first fall.** He climbed scree and a cliff face from 3:56 to about 2.1 m, stood there 20 s, and fell at 4:28 to 0.86 m: hearts 4.75 to 1.25, so **3.5 hearts on a drop of about 1.2 m**.
- **Healing with food.** He picked four windfall apples at 4:35 and ate apples and berries from 4:44 to 4:56, back to 3.25 hearts. He kept picking: 22 units picked (18 from the ground, 4 from plants) and 8 eaten in the play, healing 3.75 hearts in all. Food was the only healing he used away from the camp.
- **Glow on the river.** Glow aimed at the river at 5:44 was refused ("Neither of you can see the water surface"), then cast on land two seconds later.
- **E with nothing chosen.** After picking, he pressed E four times with no pocket slot chosen (6:24 to 6:28: "Nothing here for E: walk up to a lit campfire, a fractal, some food or something to pick up."). Then key 1 chose the apples, and three clicks ate three (6:30).
- **The Gubble's magic.** From 8:21 to 9:26, high on a slope near (−7.7, 2.4, −13.4): a bubble carry at 8:35, a lift at 8:49, Glow, and three Grows. The pips went from 9 to 0 by 9:23, with three "The Gubble needs a moment" refusals (8:53, 9:11, 10:56).
- **The second fall.** A climb to about 3.45 m and a fall at 10:00: hearts 5 to 2.5.
- **The pause menu.** Esc at 9:33 and from 10:32 to 10:46; the meters' numbers were switched on at 10:46.
- **The last bubble.** At 10:54 he chose a bubble and led it upward ("a higher spot lifts it"). The pips went from 6 to 0 at 11:00, "The bubble popped before it got there", and he fell to 2 hearts (`0068-refusal.jpg`: Pips 0/9). He quit at 11:09.

### The refusals

- **Three times `energy_exhausted`** ("The Gubble needs a moment"), at 8:53, 9:11 and 10:56.
- **Once `target_not_found`**, for Glow on water at 5:44.
- **All four were shown on screen.** The pop at 11:00 is a notice that no command claimed: the world's pop, not a refusal.

### The frames

- **The freeze after the island appears.** The worst window, 0:21 to 0:26, is the first frames after `ready` at 0:21: process 1566 ms, physics 1585 ms, and collections 175/19/8 pausing 319.6 ms. It was 941.6 ms on 17 October and 535 to 561 ms before that.
- **A steady hitch.** 104 of 129 windows had a frame over 33.3 ms, and the median window's longest frame is 43 ms; the mean frame is 12.8 ms. From 0:56 to 2:31 the windows' longest frames were 21 to 36 ms. From 2:51 to the end they were 33 to 60 ms, after the fire was lit.
- **Picks stall the frame.** `inventory.take` took 60 to 73 ms per pick and `item.pickup` up to 60 ms; they make the 72 to 94 ms windows at 4:36 to 4:51, 6:11 to 6:26 and 9:42 to 9:47.
- **The collector** ran in 127 of 129 windows, pausing 3 to 24 ms. The median was 936 draw calls.

### Where the time went

Standing 58%, walking 18%, running 13%, climbing 4%, carried 3% and falling 1%. 55% of the time was within 2 m of the Gubble, and the companion was offline throughout.

### Stills worth another look

- `0017-steady.jpg` (2:51): the fire's ring.
- `0033-refusal.jpg` (5:44): the hotbar at 1280×720, with the reticle's "right-click: glow there" line drawn behind the Berries slot.
- `0046-steady.jpg` (7:51): the orchard at night.
- `0068-refusal.jpg` (11:00): the pop, with the meters' numbers.

### About the record

A clean quit. The frame events carry `frames` and the stills `grab_ms`, which the reader does not know yet (the contract allows it).

## The causes (from the source)

- **The freeze after the island appears.** Not fixed. It is round 1's change requests 2 and 3 (the first step after the load, and the host's periodic work with the collector), carried as **L2** in RUN-2-PLAYTEST-ROUND-2.md, which needs the GPU.
- **A pick stalls the frame.** Picking is a durable room change (`CommandHostFood.cs`: "one durable room revision, as a Fractal's pickup"; decisions 98 and 117). `item.pickup` and a ground `inventory.take` each write the island's revision on the main thread, which took 60 to 73 ms here.
- **Falls cost most of the hearts.** `PlayerVitals.FallDamage` reads the landing speed alone. It is safe up to a 0.30 m drop (`PlayerVitals.SafeFallHeight`) at the pack's low gravity, then grows in a straight line with the landing speed. No difficulty sets a cap: the 15 October answer was "Gentle's four-heart cap only if a single fall knocks a child out". Here one fall cost 3.5 of 5 hearts.
- **E with nothing chosen.** E's order (decision 113) uses food only from a chosen pocket slot. With no slot chosen, E's line names a campfire, a Fractal, food on a plant and things to pick up, but not the food already in the pockets.
- **The bubble popped mid-lift.** The lift took the last six pips at 10:58 to 11:00, and the bubble popped before it arrived, the world's pop (P-Rulings). Nothing on screen warned that the pips would not last. Why it popped short of its spot isn't settled from the record: a carry lasts 8 s (`BubbleLaws.CarryDurationDefaultS`), and the lead began at 10:54.
- **"1 berries".** `InventoryPages.PickedWords` writes the pocket entry's name in lower case after its count. The apples' entry name follows the count ("1 apple"); the berries' entry is always "Berries".
- **Night.** The island clock follows the real clock at four island days a day (x_clock, M6). A play that starts at 18:00 local is at dusk and soon night, and night on this island is dark (`0046`).

## What the founder decided

Nothing yet. The questions for him:

1. **Falls.** Keep the curve (a 1.2 m drop costs 3.5 of 5 hearts), or soften it for the kids (a cap in Gentle, or a higher safe drop)?
2. **Night.** Is the night as dark as you want it for the kids, or should night be lighter?
3. **Anything you remember** from this play: what was fun, and what got in the way.

## Keep

- **Food as healing away from the camp.** 22 units picked, 8 eaten, 3.75 hearts healed. The words were clear ("Crunch! You ate an apple: half a heart healed.").
- **Every refusal shown on screen** (4 of 4).
- **The camp screen** read for a full minute at the first rest.

## Change

Guesses the next play can check:

- **The game hangs about a second and a half right after the intro, and the kids will notice.** L2's freeze, now 1.3 s on the real garage. Lane P with the GPU.
- **Picking an apple shouldn't hitch.** Each pick costs a 60 to 73 ms frame. Write the revision off the frame, or batch picks made close together. Lane P (persistence), Opus.
- **The steady 40 to 60 ms hitch in most windows, which doubled after the fire was lit.** Measure it with the GPU, in L2's scope. Lanes P and L.
- **With food in the pockets and nothing chosen, E should say how to eat** (for example, "1: choose the apples, then E eats"). HUD, Sonnet; within decision 113's order.
- **A lift that the pips or the time won't carry to its spot should warn first, or not start.** Kernel and HUD; the founder's call on which.
- **"1 berries" should read "1 berry cluster"** or "Berries: 1". HUD, Sonnet.
- **The reticle's hint line must not sit behind the hotbar's slots at 1280×720** (`0033`). HUD, Sonnet.
- **Fall damage:** pack values, the founder's answer to question 1.

## Drop

- Nothing.

## Not this round

- **The companion stayed offline,** so the AI never played. That is LEAD.md item 8.
- **No marks.** The founder pressed no mark in his last three plays. A one-line reminder of F9 and F8 at launch would be cheap, but it isn't proposed yet.

## The reader

- **The height bands are wrong on an island.** "By height above the floor" puts 97% of this play in band 1, though the founder stood above 2 m for minutes. The recorder's `height_m` is the height above the ground beneath the body (`PlaytestRecorder.WriteBody`, `Below`), not above the floor, so on a landscape it is always band 1 except in the air. The reader should band the position's height over the island's floor instead. Sonnet; R2's tests are under `tools/playtest/tests`.
- **The worst frame's split still exceeds the frame,** as on 17 October: process 1566.5 ms and physics 1585.3 ms inside a 1337.3 ms frame. It is a recorder or reader finding for a Sonnet round, still open.
