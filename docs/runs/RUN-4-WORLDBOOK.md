# Run 4, the worldbook: six pages, one world

**Status: a vision for the founder, 23 November 2026. Nothing is built.** It makes one world of the six Run 4 pages and twelve world-builders' ideas. **(his)** marks what he asked for; all else is **beyond what was asked**, provisional until he plays it. **K** needs a kernel, rules or contract round first: a second wave. `G/` is `pipeline/landscape/generator/`, `X/` is `pipeline/landscape/export/`, `N/` is `game/scripts/native/`, `SPC` is `N/SmallPlayerController.cs`. Packets, lanes, waves and decisions are the brief's (`docs/runs/RUN-4.md`); where this page differs, the brief wins.

## 1. The place

You are one centimetre tall, and the room is your whole country. The floor is a wide plain. The chair is a mountain shaped like a chair. The desk is a high table of rock, with a small world on top and a dark hall beneath. The walls are cliffs as tall as the sky. Water starts where the cliffs weep and runs to the lowest place. On top of the walls a desert goes on for ever; walk too far and it brings you home. One beam of sun comes through the window and walks across the floor. At night the room's lamp is the moon. Small people and animals live here, and they know the way to water.

## 2. The layers of the land

One rock column runs through it all: the same coloured beds cross chair, desk and wall at the same heights. Today a bed's tint is a hash (`game/shaders/painterly_land.gdshader:611-620`).

1. **The underlands.** Slots: gaps behind a cabinet or between cushions, a few bodies wide, cool and ferny. Pits: an open bin or tote is a crater with a grove inside. Halls: a pillared dark under a desk or table. The scan gives boxes, not cavities (`docs/ROOM-TO-LANDSCAPE.md:15`), so hollows are invented by kind and scored on the stand-in's test voids (`pipeline/landscape/corpus/fixtures.py:23-26,49,54`). A hall's roof is a `platform` shell part, which keeps the land outdoors (`N/Look/LandscapeLook.cs:134-135`). Halls wait on a proof packet (WORLD-10).
2. **The dust country.** In still corners pale silt lies deep, keeps footprints and grows the oldest woods.
3. **The plain (his).** Steppe, savannah, grass, lakes or moor by the floor's colour. The family's line between doors is swept bare: the giants' road.
4. **Scree and fans.** Fines on top, boulders at the foot; a wet green toe under each notch.
5. **The landforms (his).** Each object in its true shape under a hard cap bed: mesa, butte or hoodoo. Soft things are capless, rilled domes.
6. **The spring line.** Rain sinks through the desert, meets one tight bed and weeps out at one height round every cliff. Streams start there. Green means water and a vine.
7. **Summit worlds.** Each top is a sky island with one plant of its own. What rests on the furniture is the prize.
8. **The great cliffs (his).** Walls; wall things are buttresses, farmed in tiers.
9. **The rim desert (his).** Dunes all lie one way: the only compass. Past each window lies a low desert at the sill's height, a tongue of dunes between two scarps: stand on the sill and you look out of the window.

## 3. What each room feature becomes

Kinds: `contracts/room-manifest.schema.json:538-547,708-717`; `pipeline/roomscan/src/roomscan/inventory/vocabulary.py:26-71`.

| Room feature | Becomes | The player there |
|---|---|---|
| Floor (his) | Plain, savannah, steppe, lake or moor | Follows trails to water |
| Wall (his) | Great cliff | Climbs its terraces to the rim |
| Shelf, cabinet (his) | Buttress: stair-farms, a seep line | Takes the folk's cut steps |
| Chair (his) | Seat meadow under a vined back wall | The first climb; a camp |
| Desk, table (his) | Capped mesa over a dark hall | Scales it; explores beneath |
| Couch, bean bag | Rilled ridge or dome; slots between cushions | Braces up a seam (K) |
| Box, tote, bin | Mesa; an open one is a pit | Climbs down to a grove |
| Mug, jar, paint can | Crater tarn, tinted by the vessel | Swims on a mesa top |
| Potted plant | Walled high garden, the oldest wood | Walks the canopy road |
| Window | A notch with crystal shards on its sill, open to a low desert: the Sun Gate | Looks out of the window; follows the beam |
| Garage door, vent | Weather gate; breathing cave | Starts here; hears rain coming |
| Door, pet door | Pale slab over a body-high pass | Turned back by mist |
| Ceiling lamp, screen | The Lamp-moon; a cold curtain of light | Steers home at night |
| Gap, corner, dust | Slot canyon; deep soil, cobweb nets, dust-herds | A hidden road; a safe landing |
| Rug, stain, crack, drain, cord: taped only (K) | Prairie, tar lake, rift river, swallow-hole, causeway | Each its own place |

## 4. Sky, light, weather and time

- **The Sun Gate.** When the cliffs shade the floor, the window's beam is one pool of gold crossing the plain at the real hour. Flowers open as it comes. A few real days a year it touches a standing stone. The generator reads windows (`G/generate.py:86-88`) and the room holds north (`X/exporter.py:415`). The played room's own shell lists no openings and its lights are one sun (`X/exporter.py:402-413`); its `x_landscape_source` does list the windows (`X/source.py:73-84`). The sun is low, so the notch must open onto low ground: the low desert.
- **The Lamp-moon.** The room's lamp hangs where it hangs: a still moon that wakes at lamps-on and points home. One star stands over each landform, in its colour. Glow only, in a new sky block: the export forbids a `ceiling_lamp` light (`X/tests/test_export.py:595`).
- **The cloud sea.** At dawn mist fills the basin, drawn by height, not volume. Mesas are islands.
- **Weather with a home.** Wind comes through the door bay; today it only turns, a full circle every 16 minutes (`N/Kernel/NatureState.cs:46,487-488`). The brief's R4-5 gives the kernel's wind that home, so fire, a falling body and the dunes agree. Windward faces are mossy, the lee dry; trees lean downwind. Vents whistle before rain.
- **Rain at true size.** Few drops, each a third of the body, bursting in crowns. Dawn dew hangs in glass beads.
- **Living light.** Blue sparks round a night swimmer; fireflies; the halls' star roof.
- **Time.** The real clock stays (VILLAGES decision 3; the brief's D9 asks about it). Frost lies where no sun came. Evenings get the most life. A player who plays only after dark still meets the day: the preview clock opens the world at any hour and date, and a quick day runs dawn to dusk in two minutes, the beam walking the floor as they watch. Nothing of it is saved.

## 5. Life

**Plants** belong by water, soil, shade and wind. Trees (his) follow the botanists' branching models, so kinds read apart far off and climb differently: a pine is a ladder, an oak a hall. Moss stands three bodies high in cliff shade, springy to land on. A flower clock, weighted to dusk, tells the hour. Old-wood crowns stand one Grow bridge apart (1.2 m, `docs/game/MECHANICS.md:342`): the canopy road. The rim blooms for a minute and a half after each rain. At a hollow's mouth plants tell depth: grass, fern, moss, fungi.

**Animals** come as signs first (`docs/world/TRAITS.md:153`): generator data, no entity, no command. Trails (his) join like streams toward water or a pass; at a fork, take the joined branch. Each pool has a cropped lawn round it. Whitewash marks each ledge worth reaching. At night a silver ribbon runs from cliff foot to fruit bushes. Compass mounds stand edge-on to north. Oak saplings line trails where a hoarder forgot its nuts. Then scenery, one draw each, as the pond fish (`N/Look/Fauna/PondLife.cs:19`): dust-herds rolling downwind, a dusk roost over the reeds, pool shrimp that wake after rain. Run, and the meadow falls silent; stand still, and it returns.

**People** (his) live where the land gives a reason: spring line, ford, gap, dry knoll, the Sun Gate's stone. They build from what is underfoot. Trades fit the size: dew-rollers, herders, scent-road carriers, lamplighters. Ledge folk drift down at dawn under seed fluff and ride a hoist home. The market moves by weekday, the herds by season. At lamps-on the villages light in a wave. All from data, clock and rain.

## 6. Moving through it

Here air and water's skin are strong, and gravity is weak.

- **Up is earned (his).** A climb has three acts: the animals' trail to the scree, the folk's cut steps, the wild pitch. In a slot the body braces between two faces at half price, no refill (K). Scree slides back going up and runs free going down.
- **Down is the reward (K).** Seed-clocks grow only on tops and the rim. Pick one, step off, sink at walking pace on the wind. Warm air holds a drifter level, never above its launch, so no gate opens. A held-seed glide is like a shipped backyard-scale game's, so the brief's likeness check comes first; the fallback is a winged seed that spins as it falls, as a maple key does.
- **Falling well.** Push toward a face to catch it (`SPC:152`). Moss, dust heaps and cobweb nets under the worst pitches cost no heart.
- **Water (K).** Still water holds a slow walker; a run or a hop breaks through. Rain falls two and a half minutes in every six, and no child is dropped by weather: rain shuts the water to a new crossing, and the skin holds under a walker already out until they reach ground. The look knows still from flowing (`N/Look/LandscapeLook.cs:73-74`); the body's water does not (`N/Room/RoomWater.cs:22,39`). BODY-5 re-proves the gates.
- **The call.** One key; the cliffs answer, later the farther they stand.

**The abilities** read the land (K). Glow wakes the star roof, rings itself with a garden in the dark and draws a vine's tip toward it. Grow drops root ladders from a hard-won lip and ripens seed-clocks. Fire makes wall camps: stars on the cliff. Fire with Bubbles is the chimney ride up a cleft, past today's 1.2 m lift (`N/Kernel/Bubble/BubbleLaws.cs:40`). A Bubble under water is an air bell.

**The Gubble** is the spotter: on a climb it floats to the next ledge the bar can reach and lights it. That is the game's own follow, never an AI command. The companion gains no new surface; whatever moves the player is fired by the player's own key.

## 7. The first hour

1. The title page shows this room as land, at this hour. New game: the eye falls from a grown-up's height to 1 cm (the Dive).
2. Wake on the door's pale slab, the whole room ahead: every top, one thread of smoke. The first rest at its campfire teaches Fire.
3. A trail at the feet, then a boulder one held leap high and the first cairn, which pencils what is in sight onto the map.
4. The trail joins a wider one and reaches water. A Fractal lies on a pale bed.
5. Into the chair's shade: dusk at midday, and Glow.
6. The first climb, in three acts, to the seat's camp. At its cairn, the Squint: the plants sink, the mountain is plainly a chair, and the land grows back.
7. A seed-clock, or the spinner tree if the likeness check says so: the first drift down (K).
8. Lamps-on: the villages light in a wave, the Lamp-moon wakes, and one shelf fire stays dark for next time.

A `first_hour` check in `G/checks.py` fails a land that cannot offer this.

## 8. Adopted and set aside

**Adopted, by carrying page.**

- **WORLD:** hollows; caprock and bed heights; spring line; scree fans; wind, exposure and wetness maps; dust soil and the giants' road; summed trails; three-act climbs; cairns; summit worlds and tarns; waking pools; door-slab start and pass; Sun Gate track and stone; nets and heaps; wall camps; `first_hour` check; sky block of openings and lamps; taped `floor_features` (K).
- **BODY:** seed drift, water's skin, chimney brace, chimney ride, air bell, leaf ferries (K); scree pace; wall catch; soft landings; the call; the hush; the spotter.
- **FLORA:** branching models; summit plants; flower clock; hanging gardens, tier crops; moss forest; canopy road; flag trees; hoarder's saplings; pool lawns; compass mounds; rim bloom; belts of light; seed-clocks; root bridges; root ladders, light-led vines, light gardens, staged regrowth after fire (K).
- **VILLAGES:** site reasons; building habits; trades; drift and hoist; dew harvest; market, season moves, a bridge's mending day; beacon wave; signs and tracker's tokens; dust-herds, roost, moths, shrimp.
- **LOOK:** beam and motes; Lamp-moon and plan stars; cloud sea; strata table, damp band; true-size rain, dew; living lights; star roof, dark under a roof; dunes, footprints mirrored at the wrap; echo, vent whistle; frost by shade; the Dive; the Squint.
- **MENU:** the Dive; title map alive at this hour; chalk plan lines; cairn survey (outline at once, detail earned); field-sketch world cards; hand lens; journal pages for signs and summit plants.

**Set aside.**

- **A fungus that throws the body:** its landing moves with the hour, so no gate can be proved.
- **Rising from the floor on a vent's air:** it skips the climb he asked for.
- **Rain knocking a climber off:** a fall the child did not choose.
- **Walkable winter ice:** a season would open a gate.
- **Folk that answer kernel things** (bowers, the player's bridge): folk read data, clock and rain only.
- **Invented lost things; inferred cords, joints, stains:** a false room.
- **A living giant snail; lichen ladders:** no moving collider, no material per object.
- **Volumetric mist; a real light per lamp:** the frame budget.

## 9. Names

Placeholders, for the family to replace: Sun Gate, Lamp-moon, star roof, spring line, giants' road, seed-clock, dust-herd, the Squint, the Dive. Gubble and Fractal are the game's own. No word a player reads is written here.

## Not checked

Nothing was built, run or drawn. I read the six pages and every line cited here; not the builders' other citations, any frame cost or the real garage's rooms. Unproved: a hall's roof against the walker, the navigation bake and the camera; whether a window faces the sun's path; the gates with seed drift and water's skin on. A builder flagged a seed glide in a shipped backyard-scale game: the brief's likeness check comes first, and it also covers leaf umbrellas and dust-herds. The low desert, the one wind, the preview clock and the rain rule were added in the edit after review; none is built or proved.
