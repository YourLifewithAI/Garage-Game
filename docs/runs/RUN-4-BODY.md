# Run 4, the body: one centimetre tall

**Status: a design for the founder, 23 November 2026, reworked to his charter. Nothing is built.** `SPC` is `game/scripts/native/SmallPlayerController.cs`, `N/` is `game/scripts/native/`, `K/` is `N/Kernel/`, `G/` is `pipeline/landscape/generator/`, `v5` is `game/rules/storybook_wild/v5.json`. **K** needs a kernel, rules or contract round: a second wave. Where a lane, an After, a wave or a decision number here differs from `docs/runs/RUN-4.md`, the brief wins.

The player already is 1 cm tall against the room: the land is the room ten times over, round a 10 cm body. That stays. What changes is how a body lives at that size, where gravity is weak and air, grip and water's skin are strong. It falls slowly, steers, catches a face, lands soft on moss, runs down scree, walks on still water and leaves a summit under a seed. Up is still earned. The numbers move into a rules pack, so saves and tests work until one switch.

## What the founder asked

- "I'd also like the player character to be effectively 1cm high relative to the surrounding environment. Let's rewrite the physics to make it more faithful to this actual miniaturization"; and "movement improvements".
- Later: chairs and desks "that they have to scale"; a desert that "brings them back if they wander off long enough as the ocean water does now".
- His charter: "go beyond what I initially propose, but keep the themes intact."

## The design

**The scale stays.** Both garage lands are already ten times the room (`docs/runs/RUN-2-STATUS.md:586`). A 1 cm capsule breaks the profile's 5 cm floor (`game/native/WorldScaleProfile.cs:26`) and the 2 cm navigation cells (`N/Navigation/RoomNavigation.cs:48`). Filmed 5.3 times slowed, today's gravity (SPC:59) is a 1 cm creature's own, and an ant's fall limit comes out near 3.4 m/s.

**Numbers become data.** Rules pack v6 gains a `traits.body` block; `traits` is closed (`contracts/island-rules.schema.json:662`), so it is a contract round. The kernel hands it over beside the knacks (`K/CommandHostModeRules.cs:70-73`); with no pack the body keeps today's numbers. `--rules-version=6` plays v6 while suites stay on v5 (`N/RoomWorld.cs:27`); saves pin no version (`K/CommandHostModeRules.cs:88-97`). Capsule, step and floor angle stay: the generator's walker copies them (`G/checks.py:31-33`).

**Air.** A drag curve replaces the clamp (SPC:1189). The landing's cost stays one function (`N/SmallPlayerController.Exertion.cs:51`), its slope from the pack (`K/PlayerVitals.cs:73-77`). The weather's wind (`K/NatureState.cs:487-488`) drifts a body in the air only (SPC:1185-1186). It turns a full circle every 16 minutes today (`K/NatureState.cs:46`); R4-5 gives it the room's home bearing, so a faller drifts the way the dunes and the trees point.
- *Steer and catch.* In a long fall the body steers three times harder (SPC:28, 1187). A push into a face grabs it in the air (SPC:152), free at any speed today (SPC:1723); it will cost bar by the speed stopped, through the climb's budget (SPC:320; `K/CommandHostModes.cs:228-239`).
- *Soft ground.* Moss, dust heaps and cobweb nets cost no heart, as deep water costs none (`K/PlayerVitals.cs:168-170`; `docs/world/TRAITS.md:103`). Dry moss and nets bounce, lower than the leap.
- *The chimney ride (K): Fire with Bubbles.* In a marked cleft over a lit firepit (`K/CommandHostCamps.cs:294`), a led bubble passes the 1.2 m lift (`K/Bubble/BubbleLaws.cs:40`; `K/Bubble/CommandHostBubbleLead.cs:191`) to the generator's shelf. A pop is still a fall (`docs/game/MECHANICS.md:318`); the catch saves it.

**Down is the reward (K).** Flora's seed-clocks stand on tops and the rim. Taking a seed is a player-only command, as `inventory.use` is (`K/CommandHostInventory.cs:25-26`); the kernel opens the drift as a creation sets a fall limit (SPC:820-836, 1202). The body sinks at walking pace, steers, and rides the wind. The seed fills the hands, so it never climbs (SPC:359, 1673) or reaches the pockets or the Gubble. Warm air over the sunlit patch holds a drifter level, never above its launch. A clock grows only where nothing in its fan is banded above its own top. **Likeness first:** a builder flagged a held-seed glide in a shipped backyard-scale game, so BODY-14 and FLORA-14 wait on the brief's likeness check. The fallback is another real flier: a winged seed that spins as it falls, as a maple key does, slower still and steered by leaning, so the summit's plant is a spinner tree.

**Leaping.** A tap is today's free hop (SPC:32). Holding carries the rise to three body heights, paid through the climb's budget, so no gate loosens.

**Grip.** Rock at 0.20 m/s, not 0.12 (SPC:151). Vines and the folk's cut steps climb at walking pace through one price seam (SPC:1768-1769; FLORA-6). Hanging costs nothing, as today (v5:102). *The brace (K):* in a slot, a second face close behind halves the price, beside `ivy_climb_factor` (v5:103); the bar refills only on standable ground (SPC:1166, 1175), so a chimney prices exactly.

**Ground.** Nothing tells the body scree from rock or moss from grass: the exporter folds them together (`pipeline/landscape/export/materials.py:5-12`), one role a collider (`N/Room/RoomBuilder.cs:99,143`). A footing read gives firm, loose or soft; unknown is firm. Its one source is the world page's footing in `x_landscape_places` (a bounded grid, with nets and dust heaps as points; WORLD-5), and moss through FLORA-6's plant copies. Up loose ground the scramble (SPC:166, 1704) is slow and dear (`docs/world/TRAITS.md:93`). Down it, past 25 degrees, the body slides: faster than the run, free, steered. Reach leaves drops out (`G/reach.py:17-19`); BODY-5 adds them.

**Water.** The sea leaves the ten-times world; a room with none already runs (SPC:243-244). Lakes keep swimming, diving and the boat (`K/Bubble/BubbleLaws.cs:78-81`). B goes to the start (SPC:1266-1296).
- *The skin (K).* Still water holds a walker; a run, a hard landing or a stream breaks through to the swim (SPC:1511-1525). Rain falls 150 s in every 360 (`K/NatureState.cs:39-44`), and the worldbook bars a fall the child did not choose. So rain never drops a walker: it shuts still water to a new crossing, and the skin holds under a walker already out until they reach ground. The look's rain warning comes 30 s ahead (LOOK-12). The look tells still from flowing (`N/Look/LandscapeLook.cs:73-74`); the body's water has one role (`N/Room/RoomWater.cs:22,39`). A resin bead shoots the body across and kills the skin behind it.
- *The air bell (K).* A Bubble cast under water is a bell (`docs/game/MECHANICS.md:259`): inside, the dive's drain (v5:101) stops and the bar refills.
- *Leaf ferries (K).* A leaf is a deck the kernel moves down a stream or downwind on a lake; the body already rides a deck (`N/SmallPlayerController.Bubble.cs:132-142`).

**The edge.** With no sea the bounds hold the body's sides and head (SPC:680, 714-728, 1806). A rim record takes the sea's place in the wrap and the far net (SPC:654-674, 1057): a walker far out and still going out is put at the opposite point, heading the same way. Its shape is the brief's rim table (`docs/runs/RUN-4.md`, section 4); BODY-4 builds to that table on a test rim, before any exporter writes it. Past a window's notch the sand lies at the sill, the world page's low desert; a wrap from there lands on the sand at the opposite point, at that sand's own height, and blown sand hides the change. The look's sand repeats every 12.8 m and the wrap moves whole periods (`docs/runs/RUN-4-LOOK.md:28,55`): the same dune lies underfoot before and after. Flat slabs first; later a slab of the dune function travels under the walker, and on `Wrapped` (SPC:271, 673) the look redraws their prints ahead. The rim is untrusted data and fails closed.

**Senses.** *The call:* one key sends eight rays; each cliff answers after its true delay. Nothing in the world changes, so no command; sounds are made in code (`N/Audio/AudioDirector.cs:19-20`). *The hush:* the body publishes a noise ring by pace (SPC:1122-1155); scenery inside bolts, then creeps back, as fish do at a fixed range today (`N/Look/Fauna/Shoal.cs:73-76`). Never for the Gubble (`N/Look/Fauna/PondLife.cs:44-45`).

**The spotter.** Beside a climber the Gubble rests just off the face (`N/CompanionAvatar.cs:948-955`). It will float to the next ledge the bar still reaches and mark it, dim when none is, and lean toward the rim on the sand. The game's own follow: no AI command, no new companion surface.

**Seams.** The manifest gains a typed `world_scale`; the screen counts in the room's centimetres. World: bounds past the wrap and over the rim (`pipeline/landscape/export/sea.py:65-69`), slots, scree, heaps, nets, streams. Flora: seed-clocks, moss, vines. Look: the sand seam (LOOK-8), fall camera, echo, hum, prints. Characters: the tumble's threshold (`N/Characters/Tumble/TumbleTiers.cs:32`).

## Packets

BODY-4, BODY-11 and BODY-12 can start now. **BODY-8 to BODY-20 are beyond what was asked.** Only BODY-6 needs the GPU and the founder's eye. Lanes follow the paths (pack and contracts: integrator; `G/`: C; `N/Ui/`: U; `N/Audio/`: Sound). The brief names who holds the body's files and the kernel for the run: lane P the body, lane Q the kernel (`docs/runs/OWNERSHIP.md:104,108`). The brief's table gives each packet's lane, After and wave. BODY-21 is new: the gates' fix, split out of BODY-5 so it lands before any bed or bench.

| Id | Title | Paths | After | Done when |
|---|---|---|---|---|
| BODY-1 | Rules v6, scale as data | `contracts/**`; `game/rules/storybook_wild/v6.json`; `K/IslandRulesMechanics.cs`; `game/native/BodyMotion.cs` (new) | none | v5 byte-exact; a v6 equal to today passes every suite; a broken block fails `validate.py` |
| BODY-2 | Fall, leap, climb pace | SPC; `game/tests/native/Miniature/**` (new) | BODY-1 | A 27 m fall lands within 8 s; a held leap rises 30 cm at a climb's price |
| BODY-3 | Landing cost, fall wind | `K/**`, `N/CompanionAvatar.cs` | BODY-1 | v5 suites green; v6 hearts match the values below; the Gubble keeps pace |
| BODY-4 | The rim and the wrap | SPC, `N/Room/RoomRim.cs` (new) | none | A body stands on a test rim in the brief's table shape; the wrap lands it opposite, on the sand there, whole sand periods away; a wrap from a notch's low desert lands upright on the high sand; a hostile record fails closed |
| BODY-5 | The gates re-proved | `G/gates.py`, `G/reach.py` | BODY-1; BODY-21; the world's cliffs | Reach reads v6: its leap, drops, slides; no Fractal below its band; true-size bytes unchanged. Rerun after each K packet |
| BODY-6 | Feel round, camera, words | `N/RoomHud.cs`, `N/Ui/**`, v6 | BODY-2, BODY-3 | The founder plays v6 on today's land and marks the numbers; p95 reported |
| BODY-7 | The switch | `N/RoomWorld.cs`, fixtures, suites, `docs/game/MECHANICS.md` | all; the world's regeneration | Rules default to 6; both runners and `-Scale` green |
| BODY-8 | Steer and the catch | `N/SmallPlayerController.Air.cs` (new) | BODY-2 | A faller reaches a face 1 m aside within 6 m; a catch spends bar; an empty bar never holds |
| BODY-9 | Loose ground | `N/Room/RoomFooting.cs` (new), SPC | BODY-2; WORLD-5's footing; the world's scree | Footing read from `x_landscape_places`; test scree is slow and dear up, a free slide down; unknown or hostile footing is firm |
| BODY-10 | Soft landings | `K/CommandHostModes.cs`, `K/PlayerVitals.cs`, SPC | BODY-3, BODY-9 | A 27 m fall onto moss, a heap or a net costs no heart; no bounce passes 0.25 m |
| BODY-11 | The call, the hush | `N/SmallPlayerController.Senses.cs` (new), `N/Ui/PlayerControls.cs`, `N/Audio/**` | none | Eight delays match the ray lengths; no command sent; the ring grows with pace |
| BODY-12 | The spotter | `N/CompanionAvatar.cs` | none | The Gubble rests at the next ledge within the bar and signals when none is; its commands unchanged |
| BODY-13 | Dunes underfoot | `N/Room/RimSand.cs` (new) | BODY-4; LOOK-8 | A walk 200 m out and back never falls through; height and slope match across a wrap |
| BODY-14 | Seed drift (K) | `K/CommandHostDrift.cs` (new), `contracts/**`, v6 | BODY-3; flora's seed-clocks; the likeness check | The companion is refused; a 27 m drift lasts 70 to 90 s, never above its launch; a held seed cannot climb |
| BODY-15 | Drift proved (K) | `G/drift.py` (new), `G/checks.py` | BODY-14 | Each clock's fan written; no Fractal below its band with drift on; a failing clock is not planted |
| BODY-16 | The brace (K) | SPC, v6, `G/reach.py` | the world's slots | A test slot pays half, an open face full; reach prices chimneys the same |
| BODY-17 | Water's skin (K) | `contracts/**`, `pipeline/landscape/export/exporter.py`, `N/SmallPlayerController.Skin.cs` (new) | the world's lakes | A walker crosses a still pond dry; a run, a drop or a stream breaks through; in rain no new crossing holds, and a walker already out is carried to ground |
| BODY-18 | The chimney ride (K) | `K/Bubble/**` | the world's clefts | Over a lit pit the lift reaches its shelf record, no higher; unlit, 1.2 m; a hostile record clamps |
| BODY-19 | The air bell (K) | `K/Bubble/**`, v6 | BODY-3 | Under water a bubble anchors; inside, the drain stops and the bar refills |
| BODY-20 | Leaf ferries (K) | `K/CommandHostFerries.cs` (new), `G/water.py` | the world's streams | A leaf carries a standing body from spring to pool; nobody else is moved |
| BODY-21 | The gates read the bar's 1.5 | `G/gates.py`, `G/reach.py`, `G/tests/test_fractals.py` | none | Failing first: on today's ten-times land a band-2 bar (0.8 m, 1.2 m of climb) tops band 3's gate (1.18 m). After: `gates.cap` carries the pack's `climb_m_per_bar_m`, kept equal to the pack by the test; no band tops the next band's gate; `-Scale` green; true-size bytes unchanged |

## Provisional values

- Terminal fall 4 m/s (today 6); landing 8 health per m/s past the safe speed (today 53.6). At Standard 30 cm is free, 0.5 m a quarter heart, 1 m half, 4 m one; Gentle at most half, Brave one and a half.
- Leap 0.30 m held. Rock 0.20 m/s, vines and cut steps 0.32; brace half price.
- Catch: 0.1 m of bar per m/s stopped. Bounce 0.25 m. Slide 1.2 m/s; loose ground uphill 1.5 times the bar.
- Drift sinks 0.35 m/s: 77 s from the stand-in's 27 m rim (`pipeline/landscape/corpus/fixtures.py:43`). Skin holds a walk; in rain it holds only a walker already out. Bead 1.3 m/s for 2 s.
- Ride 2 m/s, 16 m at most. Bell 20 s and ferry 0.25 m/s, as the boat (`BubbleLaws.cs:32,46`). Hush ring: walk 0.4 m, run 1.5 m, a landing 2.5 m.
- Wrap 1.5 m past the sand seam (SPC:260); bounds 3 m over the rim. Fall camera: after 0.6 s, twice the arm, 15 degrees down.

## Decisions for the founder

1. **"1 cm" is today's ten-times world and 10 cm body.** Recommended: yes. The brief takes it as provisional (its D2) and holds nothing for it. He played the ten-times land on 23 November and still asked for 1 cm, so the number is not the answer: the fall, the air and the land must make it felt. If the first basin still reads small to him, this is the first thing to ask.

Provisional, for him to mark in play: a fall of at most one heart at Standard, softening "Falls should hurt" (17 October); the leap and climb paces; the screen's centimetres; the next section.

## Beyond what was asked (to vet in play)

Falling is a skill: steer, catch, aim for moss (BODY-8, BODY-10). Scree and dunes are quick ways down (BODY-9). A call tells distance; a hush rewards stillness (BODY-11). The Gubble shows the next ledge in reach (BODY-12). The desert turns you onto your own prints (BODY-13). A summit pays with a drift (BODY-14, BODY-15). Slots, clefts and lake beds become routes (BODY-16, BODY-18, BODY-19). A lake is a floor in fair weather and a gate in rain; streams carry you (BODY-17, BODY-20).

## Research notes

- An ant falls at 1.8 m/s at most and walks away (popular sources). [Illinois](https://van.physics.illinois.edu/ask/listing/2099)
- Canopy ants steer 85 falls in 100 back to their trunk. [Gliding ant](https://en.wikipedia.org/wiki/Gliding_ant)
- A dandelion seed sinks at 0.39 m/s; warm indoor air rises at 0.1 to 0.3. [Nature](https://www.nature.com/articles/s41586-018-0604-2.pdf), [PMC](https://pmc.ncbi.nlm.nih.gov/articles/PMC8294611)
- Jump height hardly depends on size. [PMC](https://pmc.ncbi.nlm.nih.gov/articles/PMC6907395)
- Sticky pads hold small climbers; they fail above gecko size. [PMC](https://www.ncbi.nlm.nih.gov/pmc/articles/PMC4275900/)
- Back-and-foot chimney climbing. [Climbing](https://www.climbing.com/skills/learn-this-conquer-chimneys-with-tips-from-rob-pizem/)
- A water strider's leg holds 15 times its weight. [Nature](https://doi.org/10.1038/432036a)
- Stenus beetles skim at 0.7 m/s on a film they release. [Nature](https://www.nature.com/articles/s41467-024-45997-3.pdf)
- The diving bell spider's bubble works as a gill. [Wikipedia](https://en.wikipedia.org/wiki/Diving_bell_spider)
- Scree running; dry sand rests at 32 to 35 degrees. [Advnture](https://www.advnture.com/features/scree-running), [Britannica](https://www.britannica.com/technology/angle-of-repose-mechanics)
- Walkers with no landmark circle onto their own track. [Scientific American](https://www.scientificamerican.com/blog/news-blog/do-people-really-walk-in-circles-2009-08-20/)

## What was rejected and why

- **A glide from the first minute; a pocketed seed.** One climb would reach every lower ledge, against "have to scale".
- **A 1 m leap.** It crosses the 1.2 m gap that is a gate (`docs/game/MECHANICS.md:342`).
- **Lift from the floor on a vent's air; rain that knocks a body down; walkable ice.** They skip the climb, cause a fall nobody chose, or open a gate by season.
- **A thrower aimed by the sun.** Its landing moves with the hour. A fixed arc could be proved: a later packet.
- **A refill while braced.** A rest would open a gate.
- **A slower world profile; a C# switch; a hard cap.** Hostless suites run on the profile's default (`game/scripts/world_physics_profile.gd:15`); the generator cannot read C# (`G/tests/test_fractals.py:784`); under a cap (`K/VitalsRules.cs:216`) long falls would still play the knockout tumble.

## Not verified

- Nothing was built, run or measured; every number is my arithmetic.
- The gates carry no 1.5 (`G/gates.py:35-39`), though the bar climbs 1.5 m per metre (v5:102): by arithmetic a band-2 bar tops band 3's gate today. BODY-21 mends it; how many of today's ledges move, and whether `-Scale` names a rise, is not checked.
- The gates with drift, skin, brace, bell, ride and ferry on; whether any rim seed-clock passes the fan rule.
- How footing reaches the body: a face lookup or a small grid.
- A sand slab travelling under the walker; the Gubble on it and in a long drift (`N/CompanionAvatar.cs:1626-1639`).
- Whether a Bubble may form under water; whether every reader of the sea accepts none.
- Unread: `G/sites.py`, `G/water.py`, most of `G/climb.py` and of the bubble host.
- A builder flagged a seed glide in a shipped backyard-scale game: the brief's likeness check comes before BODY-14 and FLORA-14. It was not run here.
- A wrap between the low desert and the high sand; the skin's hold in rain against the gates.
- I read every line cited here, not the builders' other citations; six facts were searched today. The real garage was not opened.
