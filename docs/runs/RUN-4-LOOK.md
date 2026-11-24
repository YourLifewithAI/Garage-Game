# Run 4, the look: light, air, shade, ground, water and the frame budget

**Status: a design for the founder, 23 November 2026, reworked under his charter and the worldbook (`docs/runs/RUN-4-WORLDBOOK.md`). Nothing is built.** `N/` is `game/scripts/native/`, `S/` is `game/shaders/`, `LT/` is `game/tests/native/Look/`, `X/` is `pipeline/landscape/export/`, `T` is `docs/look/reviews/run3/after-perf/timings.json`. Where a lane, an After, a wave or a decision number here differs from `docs/runs/RUN-4.md`, the brief wins.

The room becomes a basin: the floor a plain, the walls great cliffs, the ceiling the sky, sand beyond the rim. No sea fills the distance, so air and shadow must show the size: haze pales the far cliff, shade takes the sky's colour, and the cliffs' shadow crosses the floor through the day. The room's lights become its sky: the window's sun walks the plain, and the lamp is the evening's moon. All of it is drawing from room data: no command, no save field, nothing new for the companion.

## What the founder asked

- "substantial graphical improvements"; "rocky cliffs that clearly look like a chair but are still also obviously landscape."
- "great cliffs with water only where the land puts it. And be sure any objects on or against the walls become parts of the great cliffs. If someone gets to the top that's where a sandy desert extends forever (but brings them back if they wander off long enough as the ocean water does now)"

## The design

**The frame first.**
- The cost is geometry on the GPU: at the spawn 14.0 ms and 14.4 M primitives (`T`; `docs/look/reviews/run3/README.md:59`).
- The 3.5 ms from p50 to p95 is the kernel's CPU work (`docs/look/FRAMES.md:37`).
- A near grass tuft is 576 triangles (`N/Look/GrassLook.cs:38-40`), drawn in every 8 m tile within 8 m (`N/Look/DrawDistances.cs:102-103`). Blades fold away by 12 m (`S/grass_blades.gdshaderinc:190-192`), yet tiles draw to 40 m (`N/Room/PlantCopiesView.cs:444`). The fix: 2 m cells, four bands, nothing past 12 m.
- The hours share one line (dawn's mist, the day's beam, rain, the night's lights), each with an off switch, as the charm has (`N/Look/Charm/CharmLook.cs:137-139`). The line is the most at any one hour, never a sum; rain shares it with the hour it falls in.
- **The budget is not paid yet.** Today's content costs 14.0 ms of GPU at the spawn and 15.5 at the far Fractal view (`T`); its lines below allow 10.3. That is 3.7 to 5.2 ms to find. The one cut measured so far saved about 1 ms and was refused as visible (`docs/look/reviews/run3/README.md:61`). So the cuts come first, in three packets in wave 1 (LOOK-2, 22, 23), each measured, before any wonder that adds cost; and from LOOK-23 on a packet that adds cost merges only while LOOK-1's ledger shows its line has room.

**Air.** Haze follows the room's span. Sun scatter and aerial perspective come on; both are zero today (`N/Look/ViewDistances.cs:105-108`). Shade takes the sky's light (`N/Look/LookDirector.cs:271-272`). Floor haze and dawn's cloud sea are one term, mist by world height; the land's own haze is off today (`N/Look/MaterialLibrary.cs:125-131`). From a top the basin is a white sea, and the body's shadow lies on it ringed in colour.

**Shade.** The sun's shadows reach 60 m (`N/Look/ViewDistances.cs:40`); the stand-in's floor is 60 by 70 m under 27 m cliffs (`pipeline/landscape/corpus/fixtures.py:43`, times ten). Three small maps are marched on a worker over the coarse ground (`N/Room/TerrainTiles.cs:35-43`): sky-view once, sun-shade each degree the sun moves, sun-hours once a day. The land's light step (`S/painterly_land.gdshader:829-831`) dims the sun by sun-shade, and cloud shadow rides along. None feeds the light estimate the nature step reads (`N/Kernel/CommandHostNature.cs:960`).

**The Sun Gate.** The played rooms already list their windows (`X/source.py:73-84`; `N/Look/Intro/IntroSource.cs:134-142`). The march follows the land, so WORLD-2's notch lets a blade of sun onto the shaded floor: a gold patch crossing the plain at the real hour, under a few additive sheets with motes in them. Crystal shards stand along the sill, the pane's remains, and glow with the sun behind them. Beyond the notch lies the world page's low desert, sand at the sill's height out to the horizon, so a low sun has a clear way in and the child on the sill looks out of the window.

**The room's night.** The night's key is a moon fixed outside the brightest window (`N/Look/LookClock.cs:86-89`, `N/Look/LookDirector.cs:360-364`). At lamps-on (`:487`) it comes from the ceiling lamp instead, no stronger than the moon, so "moonlight alone is dark" holds (`docs/look/GLOW.md:56`). The sky paints by direction (`S/painterly_window_sky.gdshader:53`) so the lamp is a glow hung at its place, with one star over each landform in its colour (`IntroSource.cs:143-149`): the plan as a constellation. A screen is a cold curtain on its cliff; light spills under the door; campfires show past their light's 0.8 m (`N/Look/CampfireLook.cs:120`). The lamp needs a new block: a lamp hint becomes a real light (`N/Look/LookDirector.cs:386-394`; `X/tests/test_export.py:595`).

**Ground.** Cliffs stay terrain with the `ground` role (`X/exporter.py:353`); a shell wall flips the look indoors (`N/Look/LandscapeLook.cs:134-135`). Cliff marks are 6 and 14 cm (`:69`): noise on a 27 m wall, each layer tinted by a hash (`S/painterly_land.gdshader:618-620`). A bed table replaces it: great beds at fixed heights, the same on chair, desk and wall; one pale marker; one dark, over a damp band: the spring line. New roles (sand, dry grass, dust silt) are one integrator round (`LT/LookPresetTest.Landscape.cs:37-43`). No photo textures.

**Water and rain.** Water is only what the generator places. Lakes reflect the sky, its clouds and a skyline strip (the land's height by bearing), with a sun path; no screen copy (`S/painterly_water.gdshader:13`). At night still water sparks blue where the body stirred it; a diver wears a silver film of air. Rain today is 160 streaks 4 cm long (`N/Look/NatureLook.cs:19,50`). A real drop is a third of this body: rain becomes few, big globes bursting in crowns and rings, every six minutes (`N/Kernel/NatureState.cs:39-44`). Dawn's dew beads the near grass, and a bright ring follows the body's shadow over it.

**The desert.** The open sea's sibling: one surface following the camera, sand to the horizon; blown sand hides the basin before the wrap (`N/Look/OpenSea.cs:104-108`). One shared function draws the sand underfoot: dunes crested across the home wind (the widest door's), the only compass. The function reads the rim record: the lip's height everywhere but past a notch, where the sand lies at the sill. R4-5 gives the kernel's wind the same bearing, so blown sand and grass never run against the dunes. LOOK-24 comes first: flat sand to the horizon, so the first basin never shows a void. Sand and silt keep the body's last steps; at the wrap (`N/SmallPlayerController.cs:271,669-673`) the look mirrors them through the centre, so the walker meets their own outbound steps and follows them home. Low stones trail grooves and move between visits.

**The Dive and the Squint.** The intro loses its coast and water phases (`N/Look/Intro/IntroTimeline.cs:34`) and opens top-down, not inside the room (`N/Look/Intro/IntroCameraPath.cs:73`). The eye falls to 1 cm; the tilt-shift blur that makes a high view a miniature (`N/Look/LookDirector.cs:930-931`) thins as haze comes in. At a cairn the shader runs backwards for three seconds (`N/Look/Intro/RoomIntro.cs:16-25`): plants sink, the mountain is plainly a chair, chalk on its true edges, and the land grows back.

## Packets

One GPU packet at a time, the game closed (`tools/look/capture-look.ps1:60-62`); one before and one after set each (`docs/runs/ORCHESTRATION.md:73-78`). Real-garage pictures stay in scratch. Lane L, but LOOK-3 is the kernel lane's and LOOK-12 the Sound lane's; LOOK-2 uses the `Room/**` grant (`docs/runs/OWNERSHIP.md:112`). All but LOOK-9 and 12 need the GPU: LOOK-1 for its `-Sweep` and LOOK-3 for its frame timing, with no eye; LOOK-2 and 22 in pairs; LOOK-23 and 24 for a count and a capture; the rest with the founder's eye; LOOK-12 by ear. LOOK-2 is split in three (LOOK-22, LOOK-23), and LOOK-24 is the desert's first step. The brief's table gives each packet's lane, After and wave. Sonnet, Opus on rework (`:35`); Opus first for LOOK-2, 3, 17 and 21. The integrator lands LOOK-6's roles, LOOK-9's globals, LOOK-12's key and LOOK-17's block. LOOK-9 to LOOK-21 are each **beyond what was asked**; LOOK-9 to 12 are small and play on today's land.

| Id | Title | Paths | After | Done when |
|---|---|---|---|---|
| LOOK-1 | Cameras, ledger, budget file | `LT/**`, `tools/look/**` | none | Floor, cliff-foot, mesa, rim and desert cameras derive on a test basin; `-Sweep` runs; the ledger ranks as `T` does; `-Budget` fails `T` and runs at dawn, noon, rain and lamps-on; a headless triangle count runs during play. The sweep is proved on the GPU token (it fails with the island cameras today, `docs/look/reviews/run3/README.md:38`) |
| LOOK-2 | Grass in cells and bands | `N/Look/GrassLook.cs`, `N/Room/PlantCopiesView.cs`, `N/Look/DrawDistances.cs` | LOOK-1 | Grass in 2 m cells and four bands, nothing past 12 m; the saving measured on the ledger, in pairs; plant, fire, Grow and food suites unchanged |
| LOOK-22 | Land levels, pre-pass, occluders | `N/Look/DrawDistances.cs`, `N/Look/**` | LOOK-2 | The finest land level cut to 28 m; the pre-pass and occluders measured and kept only where they pay; pairs; the ledger's land line stated |
| LOOK-23 | Draws: sets a tile | `N/Room/PlantCopiesView.cs`, `LT/**` | LOOK-22 | A test room of 54 sets draws at most twelve sets a tile and 1,500 draws a camera; the ledger gains a draws line; every camera in budget or the miss stated in milliseconds, for the brief's D5 |
| LOOK-3 | The kernel's periodic work | `N/Kernel/CommandHostNature.cs`, `N/Kernel/CommandHost.cs` | none | The nature step spread over ticks (`docs/look/FRAMES.md:44`), results unchanged, proved headless; p95 within 2 ms of p50, timed on the GPU token |
| LOOK-4 | Air | `N/Look/ViewDistances.cs`, `N/Look/LookDirector.cs`, `S/**` | LOOK-23 and its ledger | Behind `--look-air=off`; the light estimate as before; pinned checks move only where named (`LT/LookPresetTest.TenTimes.cs:50,142`, `.Sea.cs:194-198`); no hitch as the hour moves; at most +0.4 ms |
| LOOK-5 | Shade maps, cloud shadow | `N/Look/Basin/**`, `S/**` | LOOK-4 | Headless: a box basin's shadow line within one cell at three sun heights; no scene needed, for the title map too; at most +0.3 ms |
| LOOK-6 | Bed table, sand, silt, floor colours | `N/Look/LandscapeLook.cs`, `S/painterly_land.gdshader`, `pipeline/landscape/harness/common.py` | LOOK-5; the world page's roles | A cliff reads as rock from 60 m and at 10 cm; one bed at one height on wall, chair and desk; roles in one commit; at most +0.5 ms |
| LOOK-7 | Water, without a sea | `S/painterly_water.gdshader`, `N/Look/**` | LOOK-5; the first basin | Lakes show sky, cloud, skyline and sun path; without `x_landscape_sea`, no open sea, mist, foam, wash, sea fish, veil or beach camera (`LT/IslandCameras.cs:141`); sea rooms unchanged; no slower than today's pond |
| LOOK-8 | Open desert | `N/Look/OpenDesert.cs` | LOOK-6; LOOK-24; BODY-4; R4-5 | Dunes to the horizon in every play view, crested across the home wind; the low desert past each notch; the basin hidden at the wrap; no jump underfoot |
| LOOK-9 | The hour as globals; a day in two minutes | `N/Look/LookGlobals.cs`, `N/Look/LookDirector.cs` | none | Hour, day, lamps, rain, dew, home wind and moon phase reach every shader, pinned headless, for flora, the folk and the title map too; today's pins move them all (`N/Look/LookClock.cs:206`); `--look-day-in=<seconds>` runs the look's day that fast, nothing saved (beyond what was asked); the home wind is the kernel's until the rim record gives one |
| LOOK-24 | The sand stub | `N/Look/OpenDesert.cs` | WORLD-5 | Where a room has a rim record and no sea: flat sand in the rim's colour from the tile edge to the horizon, at the sill's height past a notch; no dunes, no blown sand; a hostile record draws none; no slower than the open sea |
| LOOK-10 | True-size rain, wet rock | `N/Look/NatureLook.cs`, `S/**` | LOOK-9 | At most 48 drops, each bursting where it lands; none under cover; no slower than today's rain |
| LOOK-11 | Plan stars, moon phase, far fires | `N/Look/NightSky.cs`, `S/painterly_window_sky.gdshader` | LOOK-9 | At most 48 stars, one draw; the phase right on four dates; a hostile source gives no stars |
| LOOK-12 | Call, rain's warning, dune hum | `N/Audio/**` | none | In code (`N/Audio/AudioDirector.cs:19-20`): cliffs answer one key after twice their distance over 343 m/s; a vent whistles and frogs start 30 s before rain, by the saved weather clock (`N/Kernel/NatureState.cs:474-478`); a dune hums under a slide |
| LOOK-13 | The Dive | `N/Look/Intro/**` | LOOK-1 | No coast or water phase without a sea; no cut into play; skippable throughout; sea rooms unchanged |
| LOOK-14 | The Sun Gate | `N/Look/Basin/**`, `S/**` | LOOK-5 | Headless: the patch within one cell of the notch's shadow at three hours; at most +0.2 ms |
| LOOK-15 | Frost by sun-hours | `N/Look/Basin/**`, `S/painterly_land.gdshader` | LOOK-5 | Frost (`S/painterly_land.gdshader:167-169`) stays in all-day shade and is gone along the beam's track; summer unchanged |
| LOOK-16 | Dawn: cloud sea, dew, halos | `S/**`, `N/Look/DawnLook.cs` | LOOK-2, LOOK-4 | The mist's top a pure function of hour and season; 3 m round the eye clear; at most +0.2 ms |
| LOOK-17 | Lamp-moon, curtain, door's light | `N/Look/NightSky.cs`, `N/Look/LookClock.cs` | LOOK-11; the worldbook's sky block | Night light levels as before (`LT/LookPresetTest.Glow.cs`); no light added; a hostile block fails closed (`N/Look/Intro/IntroSource.cs:56-58`) |
| LOOK-18 | Living lights | `S/**`, `N/Look/Fauna/**` | LOOK-7, LOOK-9 | Sparks only at night, only where stirred; fireflies one draw, none in rain or winter |
| LOOK-19 | Desert life | `N/Look/OpenDesert.cs`, `N/Look/Footprints.cs` | LOOK-8 | Dunes level across a wrap; steps mirrored on `Wrapped`; stones a pure function of the date; nothing saved |
| LOOK-20 | The Squint, chalk plan | `N/Look/Intro/**`, `N/Look/Squint.cs` | LOOK-13; the world's cairns | Three seconds; all put back exactly; no body moves; a still (`N/Look/EyeView.cs:116`) for the menu page |
| LOOK-21 | Dark under a roof, star roof | `N/Look/Basin/**`, `S/**` | LOOK-5; the world's halls | Sky-view gains a roof layer: 0.05 at most in a hall; blue points fill its roof over wet ground a minute after the last light goes out |

## Provisional values

- GPU mean 12.5 ms and 12 M primitives a camera (today 6.3 to 19.2 M). Draws: at most 1,500 a camera (today 207 to 1,257, with 10 sets; flora plans 54), render CPU 2.5 ms.
- Of the lines below, today's content has 10.3 ms (land, grass, trees, shadows, post, water) and the run's new work 2.2 (folk, air and shade, the hours, reserve).
- GPU lines, ms: land and cliffs 3.0; grass 2.4; trees, bushes, rocks 2.0; shadows 1.2; post 1.1; folk 0.8; air and shade 0.7; water 0.6; sky, weather and living lights 0.4; reserve 0.3.
- Grass: 96, 40, 16 and 7 blade pairs to 2, 4, 7 and 12 m.
- The finest land level to 28 m (48 today, `N/Look/DrawDistances.cs:64-69`).
- Haze from 15 m; the far cliff at 45% of the horizon's colour.
- Sky-view at least 0.45 on open ground, 0.12 in a slot.
- Mist tops a third of the cliffs at sunrise. Dew: 400 beads within 2 m.
- Stars at the ceiling's height; the lamp sleeps at 23:00. Steps: the last 400.
- Blown sand is whole 16.5 m past the rim, as the sea's seam (`N/Look/OpenSea.cs:53-62`). The sand repeats every 12.8 m; the wrap moves whole periods.

## Decisions for the founder

The brief merges these: 1, 2 and 5 are its D5 (5's one timing of the real garage is in its D3); 3, 4 and 6 are taken as provisional (its D8), each behind a switch. Nothing here holds a packet.

1. **One budget, 16.7 ms at p95,** split into GPU and kernel CPU; the 8 and 12 ms table retires (`docs/runs/RUN-2-TEN-TIMES.md:55`). Cuts inside capture noise merge unseen (`docs/look/reviews/run3/README.md:61`); the rest come as pairs. Recommended: yes. It blocks nothing: 16.7 ms is Run 3's standing value (`docs/runs/RUN-3.md:134`), so LOOK-1 and LOOK-2 start on it.
2. **Your screen** reads 144 Hz (`docs/look/FRAMES.md:37`). Without G-Sync a 15 ms frame shows at 48 a second. Recommended: tell us; if it is off, turn it on or let the game ask the screen for 60 Hz. A 13.9 ms budget is not offered: it would leave about 9.7 ms of GPU, and the lines above do not fit. If the cuts fall short of 16.7, far small plants thin first, a cut you can see, before any wonder goes. Blocks nothing.
3. **A true sun or a kinder one.** The look follows the real calendar (`game/styles/storybook_painterly/v2.json:53`). At 09:00 in late November a 27 m cliff shades the whole floor. Recommended: the true sun; its shade is the Sun Gate's stage. Taken; an evening player sees it by the preview clock (R4-6) and LOOK-9's quick day, and the brief's D9 asks about the world's day.
4. **Haze nearer than 40 m,** changing 13 October's ruling (`docs/runs/RUN-2-TEN-TIMES.md:66`). Recommended: yes, from a pair. Taken, behind `--look-air=off`.
5. **Standing capture times** (dawn, noon, rain, lamps-on), and one timing of your real garage, numbers only. Recommended: yes. The four times are taken; the real garage's timing waits on the brief's D3.
6. **The desert's reach.** Recommended: as the sea's, about a minute. Taken.

## Beyond what was asked (to vet in play)

LOOK-9 to LOOK-21, and LOOK-6's bed table, marker bed and spring band. All provisional, each with an off switch. Names are placeholders; the family writes every word a player reads.

## What was rejected and why

- **Walls as a sky layer** (proposal 1), and all sea and coast work (1, 2): the walls are real cliffs now and the sea ends.
- **Volumetric fog or mist, a real light per lamp, outdoor GI, screen-space reflections, a mirror pass:** two views are over budget already.
- **Each system switched off on five cameras** (2); **three clock sets around every packet** (3): past the capture budget.
- **Rain that knocks the body down; lake ice:** set aside by the worldbook.
- **Lightning glass, a drying lake, crickets timed by warmth:** the weather is rain and wind only (`N/Kernel/NatureState.cs:478-490`).
- **A darker light estimate under roofs; drinking dew; a roof woken by Glow:** rules work, the worldbook's K.

## Research notes

- Sun: a [rock window](https://www.swissinfo.ch/eng/various/gl-sun-crossed-the-martinsloch-in-elm-this-morning/91093259) lights a village church twice a year; dust shows a [beam](https://britannica.com/science/Tyndall-effect).
- Dawn: [cold air](https://en.wikipedia.org/wiki/Cold-air_pool) pools in basins; a [glory](https://en.wikipedia.org/wiki/Glory_(optical_phenomenon)) rings one's own shadow on mist; [dew](https://atoptics.co.uk/droplets/heilig.htm) rings the head's shadow; [frost](https://www.naturalnavigator.com/?p=14404) outlasts the morning in shade.
- Rain: a 5 mm [drop](https://gpm.nasa.gov/resources/faq/how-fast-do-raindrops-fall) falls at 9 m/s.
- Night: stirred sea [flashes](https://www.britannica.com/science/bioluminescence/The-range-and-variety-of-bioluminescent-organisms); [fireflies](https://www.nps.gov/grsm/learn/nature/fireflies.htm) flash together for a fortnight; [glow-worms](https://teara.govt.nz/en/node/7643) light cave roofs over streams.
- Rim and sound: [dunes](https://thesis.caltech.edu/5891/) boom at 70 to 105 Hz; [stones](https://www.nps.gov/deva/learn/news/racetrack.htm) sail on thin ice; lost walkers [circle](https://www.scientificamerican.com/blog/news-blog/do-people-really-walk-in-circles-2009-08-20/); [caves](https://www.nps.gov/wica/learn/education/what-causes-the-wind-of-wind-cave.htm) blow as pressure falls; [blur](https://pmc.ncbi.nlm.nih.gov/articles/PMC3088122) makes a scene a model.

## Not verified

- Nothing was built, run, rendered or seen. Every saving and cost is arithmetic: no cut of 3.7 to 5.2 ms has been shown, and the 9.7 ms is 13.9 less the same 4.2 ms of kernel and render CPU that 16.7 leaves.
- Draws with 54 sets and 2 m grass cells; whether 1,500 holds.
- The low desert: a second sand level past each notch, in the look and under the body.
- Godot 4.7.2: sky light as the hour moves; SSAO with the pre-pass off; run-time occluders; the intro's shader replayed in play.
- Whether far cliff tiles cast into the shadow splits, a window faces the sun's path, or a wrap rounded to the sand's period fires twice.
- The world page's notches, roles, rim data, cairns, halls and sky block: not built. Dunes need the body's sand to follow the shared function; BODY-4 planned flat slabs.
- The real garage: never timed. In both installed rooms only opening and light-hint kinds were counted: windows in both, no lamp.
- Not re-read from the first draft: `T`, `FRAMES.md`, the grass and copies files, `TerrainTiles.cs`, `LT/`, the tools. Four web searches; other notes are the world-builders' links.
