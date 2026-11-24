# Run 4, the world: the room at the player's scale, walled by great cliffs

**Status: a design for the founder, 23 November 2026, reworked to his charter and the worldbook (`docs/runs/RUN-4-WORLDBOOK.md`). Nothing is built.** **(his)** is what he asked. **(beyond)** is beyond what was asked: provisional, to vet in play. **K** waits on a kernel, rules or contract round. `G/` is `pipeline/landscape/generator/`, `X/` is `pipeline/landscape/export/`, `N/` is `game/scripts/native/`, `SPC` is `N/SmallPlayerController.cs`. Where a lane, an After, a wave or a decision number here differs from `docs/runs/RUN-4.md`, the brief wins.

The map is the room. Every object stands where it stands, ten times over, and keeps its shape as rock under a hard cap. The walls are great cliffs that weep along one line, where every stream starts. A window is a notch where sand and sun come in. On top, sand runs on without end, and a walker who goes far enough walks back in from the other side.

## What the founder asked

- "maybe the floors are vast plains of grass, or lakes or savannahs depending on the colors."
- "the rocky cliffs that clearly look like a chair but are still also obviously landscape. Desks would be high mesas", with floors "following the basic physics of geology."
- "great cliffs with water only where the land puts it. And be sure any objects on or against the walls become parts of the great cliffs. If someone gets to the top that's where a sandy desert extends forever (but brings them back if they wander off long enough as the ocean water does now)"

## The design

**Blocks (his), caprock (beyond).** Today each box is melted (`G/land.py:261-271`), and land is shaped at true size, then magnified (`G/generate.py:181-190`): nothing under 30 cm survives. A grammar (`G/parts.py`, new) turns each kind into at most 12 blocks inside its true box, replayed as rules on the base and detail grids (`G/detail.py:11-21`). Flat tops are hard cap beds: mesa, butte or hoodoo by width. Soft kinds (`G/land.py:26`) are capless, rilled domes.

**One rock column (beyond).** Beds lie at the same heights room-wide. A course is one band's climb as the gates give it; every fifth seam is dark, one full bar (`G/gates.py:17-20`). The gates were sized for a metre of climb a metre of bar, and the bar climbs 1.5 (`G/gates.py:35-39`; `game/rules/storybook_wild/v5.json:102`): BODY-21 mends them first, and a course is then 0.6 m. Benches sit on seams, so a child counts courses to the next ledge. Bench Fractals go first to one pale marker bed (`G/fractals.py:18-21`). LOOK-6 draws the table in place of today's hash (`game/shaders/painterly_land.gdshader:612-620`).

**Hollows (beyond).** The scan gives boxes, not cavities (`docs/ROOM-TO-LANDSCAPE.md:15`); hollows are invented by kind. A true gap of 2 to 10 cm stays open as a slot canyon. A bin or basket (`G/land.py:16`) is a crater. A closed box has one blind crack to its heart. A hall under a desk or table needs a roof. The package allows one (`pipeline/landscape/harness/README.md:80`), but topmost sampling would stand things on it (`:242-244`), so a proof comes first, scored on the stand-in's test voids (`pipeline/landscape/corpus/fixtures.py:23-26,49,54`).

**The rim (his), the Sun Gate (beyond).** `G/rim.py` (new) stands in for the coast (`G/generate.py:248`) on a magnified room made with `--edge rim`. Without the option the land is today's island, so the fixtures and the sea suites keep their sea. Past the wall line the land rises to the wall's height, 27 m on the stand-in; past the lip lies level sand. A thing nearer a wall than a slot is a buttress. A lifted thing nothing holds (`G/from_capture.py:184-185`) gets a buttress by a wall, an inferred block elsewhere. The widest door is a bay with a pale slab. A window is a notch cut from its sill up: sand blows in and lies as a fan beneath, and the sill is the rim way's broadest bench. The generator sweeps the real sun through the notch, by the room's latitude and bearing (`:176-179`), and writes the beam's track and a standing stone at the midwinter beam's deepest reach.

**The low desert (beyond).** A notch cut only through the cliff would face a plateau at lip height, and no low sun could enter. On the stand-in the lip stands 16 m over the sill, so a 30 degree sun needs 28 m of open ground behind the notch, and the margin is 10 m. So the notch runs out through the whole rim, widening 30 degrees a side, and beyond it the desert lies at the sill's height: a tongue of dunes between two scarps, out to the horizon. From the sill a child looks out of the window, and the morning sun comes in low over the sand. The scarps keep the cliffs' face angle, so the sill opens no walk to the high desert: the true lip stays a wild line. A `sun_gate` check sweeps the year and lists each notch's sunlit hours. The stand-in's midwinter noon ray must reach the floor; a room whose windows all face away from the sun's path gets no beam, and the report says so.

**Water (his), the spring line (beyond).** Today one spring rises below the highest ground (`G/water.py:1-4,530`). Now rain sinks through the desert, meets one tight bed and weeps out at one height round the cliffs. A strong seep falls to a plunge pool and starts a stream; a weak one sinks into its fan and wets the toe. A lake stands only where a hollow fills. Streams leave by a swallow-hole at the door, the lowest ground (`G/generate.py:192-196`).

**Ground (his), its maps (beyond).** Scree is graded, fines on top. Soil gathers by flow, which the surface is handed and never reads (`G/life.py:140`). One wind comes in by the widest door; exposure and wetness follow. The kernel's wind turns a full circle every 16 minutes today (`N/Kernel/NatureState.cs:46,487`); R4-5 gives it this home bearing, so grass, fire and a falling body lean the way the trees do. Dust settles in still air, so corners hold the deepest soil. Lines from door to door and door to seat are swept bare: the giants' road. Walks from cover to water sum into a traffic map for VILLAGE-6's trails. The floor's colour, unread today (`pipeline/roomscan/src/roomscan/shell/manifest.py:85-97`), names the country; the maps vary it: a grey floor is steppe on the road, meadow by water, old wood in the dust. Colour shapes the land and the land places the water: a blue floor is dented deeper, and its lakes still stand only in the hollows. The scan gives a floor one colour, so a room is one country until R4-2 reads the floor in patches.

**Ways up (his); summits, acts, cairns, nets (beyond).** Faces keep no standable lip: any lip refills the bar (`G/reach.py:9-11`). `G/ascents.py` (new) sets benches on seams and writes each climb as three acts: the trail to the scree, the folk's cut steps, the wild pitch. The ladders of gates leave the meadow (`G/fractals.py:15,36`) for the landforms' own benches. What rests on a thing (`fixtures.py:22,51-55`) is the prize on its top: a mug or jar is a crater tarn, a laptop a pavement of blocks, a potted plant a walled garden; bare tops hold rain dents. Each top gets a cairn and a list of tops in sight; a net or dust heap lies under each worst pitch. Firepits keep decision 74's even spread (`docs/game/MECHANICS.md:295`), by travel time, within the kernel's 16 (`N/Kernel/CommandHostCamps.cs:82`).

**The start (beyond).** A new game wakes on the door's slab: a rule moves the spawn (`G/generate.py:175`) to the slab point with most tops in sight. A door's foot is a dark mouth one body high; mist turns the walker. A `first_hour` check fails a land that cannot offer the worldbook's first hour inside the pace targets (`docs/game/MECHANICS.md:116`).

**The data.** Cliffs are terrain, never shell walls, so the light stays outdoors (`N/Look/LandscapeLook.cs:134-135`). The exporter writes `x_landscape_rim` for BODY-4, LOOK-24 and LOOK-8, in the shape of the brief's rim table (`docs/runs/RUN-4.md`, section 4), and grows the bounds, which hold the body under the wall's height today (`G/generate.py:1137`, `SPC:718`). `x_landscape_places` and `x_landscape_sky` carry beds, tops, cairns, hollows, seeps, lamps and beam tracks as closed tokens. Places also carries footing, the body's one source for it: a bounded grid of firm, loose (scree, sand, silt) and soft, with nets and dust heaps as points (BODY-9, BODY-10); a lamp cannot be a light hint (`X/tests/test_export.py:595`). The intro's source carries openings (`X/source.py:73-84`) and gains the floor's colour (`:64-66`). No command, save field or companion surface changes.

**What goes.** Sea, reef, beaches and jetty leave a land made with the rim; the sea island stays as a fixture and for rooms made before. Swimming and the bubble boat stay, on lakes; the swim home becomes a walk (`SPC:662-674`, BODY-4). The map shows the plan's outline at once (`N/Ui/JournalMap.cs:87-90`); detail is earned at cairns. Regenerated lands start saves fresh (`LEAD.md:171`).

## Packets

Generator packets run in series with flora's and the villages' (`tools/landscape-fixture.ps1:15-35`). WORLD-1, 2, 5 and 8 come first, so he walks a basin early; WORLD-8 only brings play forward. WORLD-9 to 18 are each beyond what was asked. The brief's table gives each packet's lane, After and wave. WORLD-3 is split in two: water here, scree and country in WORLD-19.

| Id | Title | Lane | Paths | After | GPU | Founder's eye | Model | Done when |
|---|---|---|---|---|---|---|---|---|
| WORLD-1 | Blocks, caps | C | `G/parts.py` (new), `G/land.py` | none | no | WORLD-8 | Opus | Stand-in: 12 floor boxes match their blocks from five sides; unlike families fail; true-size bytes unchanged |
| WORLD-2 | The rim | C | `G/rim.py` (new), `G/generate.py` | WORLD-1 | no | WORLD-8 | Opus | Behind `--edge rim`; without it today's bytes. Lip at 27 m but for openings; wall things joined; each notch open to its low desert; the rim record in the brief's shape; a `rim` check stands beside `sea` (`G/checks.py:2034`); `sun_gate` lists each notch's sunlit hours |
| WORLD-3 | Water where the land puts it | C | `G/water.py` | WORLD-2 | no | WORLD-8 | Opus | A dented floor gives one lake, in the dent; no stream rises on high ground; water leaves by the door's swallow-hole; no sea |
| WORLD-19 | Scree and country | C | `G/ground.py` (new) | WORLD-3 | no | WORLD-8 | Opus | Scree graded, fines on top; the floor's colour names the country and a second colour changes it; a blue floor dents deeper and puts no water by itself |
| WORLD-4 | Ways up | C | `G/ascents.py` (new), `G/fractals.py` | WORLD-12 | no | no | Opus | Every top, sill and lip proved from the spawn, with a way down; no other standable lip |
| WORLD-5 | Export | Integrator | `X/rim.py` (new), `X/exporter.py` | WORLD-2; BODY-4 | no | no | Opus | Bounds pass wrap and lip; the rim record, floor colour, lamps, places and footing out; hostile data refused; a land with a sea keeps its bytes |
| WORLD-6 | The map | U | `N/Ui/JournalMap.cs`, `N/Ui/Minimap.cs` | WORLD-5 | no | yes | Sonnet | The plan's outline under the ink at once; names by kind, from a table |
| WORLD-7 | His garage, regenerated | Integrator | `tools/install-room.ps1` | all generator packets; R4-7; his yes | yes | yes | Integrator | The old room set aside, not deleted (`X/exporter.py:60`); the real garage local, in counts; runners green. Again after wave 6, on his yes |
| WORLD-8 | A first basin | Integrator | `tools/install-room.ps1` (new), `tools/landscape-fixture.ps1` | WORLD-5; VILLAGE-2; FLORA-20; LOOK-24 | no | yes | Integrator | A third fixture, the basin, beside the two sea fixtures; the stand-in basin installed as a room of its own, with its own shortcut; it boots headless; flat sand past the lip, never a void; he walks it and picks the crispness. Installed again at each wave's end |
| WORLD-9 | Slots and pits | C | `G/parts.py`, `G/detail.py` | WORLD-2 | no | WORLD-7 | Opus | Stand-in: the gap behind the couch walked through; the bin a pit with a way out |
| WORLD-10 | Hall proof | C, L | a scratch package | WORLD-8 | yes | no | Opus | A hall under the stand-in desk passes walker, navigation, camera and planting, or names the fault |
| WORLD-11 | Halls | C | `G/hollows.py` (new), `X/exporter.py` | WORLD-10 passing | no | WORLD-7 | Opus | Halls within a quarter of the stand-in's voids; roof points out; the light stays outdoors |
| WORLD-12 | Beds and seeps | C | `G/beds.py` (new), `G/water.py` | WORLD-3; BODY-21 | no | WORLD-7 | Opus | The marker bed on every floor landform; seeps on the tight bed only; streams start at plunge pools |
| WORLD-13 | Ground maps | C | `G/ground.py`, `G/life.py` | WORLD-19 | no | WORLD-7 | Opus | Deepest soil in the stillest corner; a bare lane from the stand-in's door to each seat |
| WORLD-14 | Summit worlds | C | `G/parts.py` | WORLD-3 | no | WORLD-7 | Opus | Stand-in: the jar a tarn on its top; the laptop a block pavement; rain dents; one id a top |
| WORLD-15 | Cairns, nets, camps | C | `G/ascents.py`, `G/firepits.py` | WORLD-4 | no | WORLD-7 | Opus | A cairn and sight list a top; a net under each worst pitch; firepits on the cliffs |
| WORLD-16 | The start | C | `G/generate.py`, `G/checks.py` | WORLD-15; VILLAGE-6 | no | yes | Opus | Spawn on the slab inside the wall line; `first_hour` fails a land without the first trail |
| WORLD-17 | Sun Gate | C | `G/sun.py` (new), `X/exporter.py` | WORLD-5 | no | WORLD-7 | Opus | On a box basin the beam's track lands within one cell of a hand sum |
| WORLD-18 | Floor marks (K) | Integrator, C | `contracts/**`, `G/ground.py` | a contract round | no | yes | Opus | Rug, stain, crack, drain, cord as closed tokens, taped only; none inferred |

## Provisional values

- Slots: true gaps of 2 to 10 cm, floored at least 25 cm wide; narrower gaps join. A lifted thing within 50 cm of a wall is a buttress. Faces 82 degrees; slot walls as steep as the grid gives.
- Marker bed: the highest seam every floor landform reaches. Tight bed: a third of the way up the wall. At most five seeps run; a notch's fan reaches a third of the way to its sill.
- Grid margin 10 m (4.2 today, `G/generate.py:172`); bounds 3 m over the lip; generation under 8 minutes; a quarter more detail blocks at most.
- A top within 90 s, a sill within 5 minutes; the true lip is a wild line.
- Countries: grey, stony steppe; tan, savannah; green, grass plain; blue, lake country (deeper dents and a higher water table; the lakes stand only in the hollows); dark, moor.
- A notch widens 30 degrees a side; the low desert lies at its sill.

## Decisions for the founder

Nothing here blocks a packet but his yes on his own garage (the brief's D3). This page's seven earlier questions are now provisional picks. Two things only he can give:

1. **Wall-hung things.** The scan drops them (`pipeline/roomscan/src/roomscan/inventory/build.py:83`) and knows no wall shelf (`vocabulary.py:68-70`). "Any objects on or against the walls" needs one more read of his photos with wall kinds added: local, no spend, nothing committed. R4-2 teaches the scan on made-up fixtures; R4-7 reads his photos again on his yes (the brief's D3). Until then, wall things are those the scan kept.
2. **Which way the room faces.** One number, given when a capture is converted (`G/from_capture.py:74-76`). It is on record for his garage (`docs/runs/RUN-2-STATUS.md:586`); the Sun Gate keeps true days only if it is true.

## What was rejected and why

- **The sea stays.** His later words.
- **Curtain sheets on faces.** A second surface with open edges (`X/exporter.py:423`).
- **The rim under today's bounds, on the sea's wrap.** The bounds hold the body under the wall's height; the wrap needs a sea (`SPC:664`).
- **Blocks fitted to scan points.** The inventory kept only a count (`build.py:254`).
- **Inferred detail:** door-panel seams, cords, joints, stains, lost things, a shell as a hill. A false room: only what the scan or the tape holds becomes land.
- **A sand ramp from floor to sill, or from sill to the high desert.** The first is a walk-up, against "have to scale"; the second needs 25 m of run in a 10 m margin. The low desert is level sand at the sill, not a ramp.
- **A notch through the cliff only.** It faces a plateau, and the beam never comes in.
- **Tops moved to fit the Grow bridge.** Things stay put; pairs within its 1.2 m (`docs/game/MECHANICS.md:342`) are listed.
- **Every seep a stream.** Water stops the generator's walker (`G/life.py:397`).
- **`x_summit` moved to the rim.** It would undo the 15 October decision (`G/sites.py:11-16`).
- **Waiting on WORLD-10's roof:** a window arch with its lintel; a roofed pass under a door.

## Beyond what was asked (to vet in play)

All provisional. Caprock, the rock column and the spring line (WORLD-1, 12). Slots, pits, box hearts and halls (WORLD-9 to 11). The Sun Gate's fan, sill, low desert, beam track and midwinter stone (WORLD-2, 17). Wind, dust, the giants' road and the traffic map (WORLD-13). Summit worlds and tarns (WORLD-14). Three-act climbs, cairns, nets and wall camps (WORLD-4, 15). The door-slab start, the dark mouth and `first_hour` (WORLD-16). Lamps and beds as data (WORLD-5). Floor marks, taped only (WORLD-18).

## Research notes

- Caprock shields softer rock: mesa, butte, hoodoo. [NPS](https://www.nps.gov/brca/learn/nature/hoodoos.htm)
- Hanging gardens: water sinks to a tight bed and seeps out at the cliff. [NPS](https://www.nps.gov/glca/learn/nature/hanginggardens.htm)
- Slot canyons: cut along cracks by rare floods. [Wikipedia](https://en.wikipedia.org/wiki/Slot_canyon)
- Tepuis: table mountains with plants found on one top only. [Wikipedia](https://en.wikipedia.org/wiki/Tepui)
- Talus sorts itself: fines high, boulders low. [Wikipedia](https://en.wikipedia.org/wiki/Scree)
- Fan toes: groundwater leaves an alluvial fan at its toe. [Woods 2006](https://www2.nrel.colostate.edu/assets/nrel_files/labs/macdonald-lab/pubs/Woods%20et%20al%202006.pdf)
- Climbing dunes: wind through a pass carries sand up and over rock. [Springer](https://rd.springer.com/referenceworkentry/10.1007/978-1-4614-3134-3_51)
- Tinajas: rock pools fill in rain and wake with life. [NPS](https://www.nps.gov/elmo/learn/nature/tinajas.htm)
- At Elm the sun shines through a rock window onto the church twice yearly. [Swissinfo](https://www.swissinfo.ch/eng/various/gl-sun-crossed-the-martinsloch-in-elm-this-morning/91093259)
- At Newgrange the midwinter sunrise lights a floor 18 m in. [Newgrange](https://newgrange.com/stonelight20.htm)
- A door clears the floor by 1 to 2 cm. [CDF](https://www.cdfdistributors.com/blog/post/what-is-the-standard-gap-between-a-door-and-floor-a-contractors-guide)
- Not searched: marker beds, loess, desire paths, cut steps, toposcopes.

## Not verified

- Nothing was built, run or rendered; no picture seen; no frame cost measured (slots add detail blocks, the rim adds ground).
- The walker holds a body inside the floor polygon (`G/checks.py:829-830,855`); proving the rim needs a wider floor.
- A hall's roof; near-vertical slot walls; the gates once benches move; the kernel on sand past the tiles.
- Whether a window faces the sun's path; how long a scan round takes; whether the scan's weights are cached.
- The low desert: its scarps past the tile edge, a wrap from it onto the high desert, and its cost in ground. The 28 m is arithmetic from the stand-in's window (`pipeline/landscape/corpus/generate.py:189-190`, ten times over).
- `G/detail.py`, `G/fractals.py`, `G/routes.py`, `G/checks.py`: read in part. The real garage: kinds counted only (its openings and its lamp).
- Three web searches today (dunes, fan springs, midwinter beams); the other links are the world-builders'.
