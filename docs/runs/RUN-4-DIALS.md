# Run 4, the world's dials: the year, the hour, the sky

**Status: a design for the founder, 24 November 2026, edited from three proposals (a naturalist's, a systems one and one written from a young player's seat) and mended on 25 November after three reviews; the same day it was made one design with `RUN-4-SOUND.md` and `RUN-4-LEAVES.md`, and the three were read whole a second time, seam by seam (rulings 21 to 40 below). Nothing is built.** This page holds what the three share: the one vocabulary table, the four seam files, the build order and the one-editor lists. The other two point here. Code read at `run4/integration`, `dd0a7561`; no file cited by line changed up to `74729278`. `N/` is `game/scripts/native/`, `K/` is `N/Kernel/`, `M/` is `N/Ui/Menu/`, `A/` is `N/Audio/`, `S/` is `game/shaders/`, `T/` is `game/tests/native/`. **(his)** is what he asked; the rest is **beyond what was asked**, provisional until he plays it. Every name is a placeholder, "the Turning" and the named days among them: the family writes every word a player reads. Where this page differs from `docs/runs/RUN-4.md`, the brief wins; what it asks of the brief is listed near the foot.

He asked that the player's choices "become a vibe to play, and also affects what you can find and harvest": pick Fall, and "the leaves are falling and that changes how your feet sound". To choose a season is to visit one day of a real year, and the whole land must keep that day. A season that only recolours is a filter. So this page is one calendar with four readers (look, sound, rules, folk) and one closed vocabulary. A **station** is one of eight spans of the year. The **larder** is what the land offers to pick. **Flicking** is turning a dial back and forth for gain.

## What the founder gets

1. **The dials' first build.** It follows the sound page's first aid (SOUND-1 and 2), which holds the same files. The title page shows four pictures of days, and he taps the gold one. Leaves are coming down under the trees and lie where he walks. His feet are on them: a soft crackle when dry, a hush when wet, nothing louder than the build before. In Settings two rows step the year and the hour, both ways. No command, nothing saved in the game; the pick is kept on this machine.
2. **Then the dial is the game's own.** Saved with each game, shown on Continue, turned at any campfire by a look and a held key. The sky joins it: four skies, each a way to play. The Gubble wears the station. No rules pack is needed, so this is built beside the first build.
3. **Then the larder moves.** Each station offers its own things, and no flicking picks one unit more. It sits behind the rules v6 switch until BODY-7 pins v6 (`docs/runs/RUN-4.md:201`).
4. **Then the year fills in.** Animals as signs and far scenery, the folk's work, a toy a station, an almanac that sketches what was met.
5. **Later:** a day in one sitting; a year that walks; the wind, the moon and the folk's day.
6. **Always:** no price, no wait, nothing lost, no way opened, and the Gubble never turns a dial.

## The dials

| Dial | Stops; the first is the default | At once | Over time | Its face in the room |
|---|---|---|---|---|
| **The year** (his) | `calendar`, the real date; `held` at a station; later `walking` | That day's sun, day length and colours; the ground's cover; who sings | What the land offers to pick (DIAL-8) | Every plant; the fire's stones; the beam, long across the floor in winter, a short pool by the sill in summer |
| **The hour** (his) | `clock`; `held` in a part; later `turning` | Light, lamps, the sound's part; flowers and folk through LOOK-9 | The drawn light reaches the kernel's ground (`K/CommandHostNature.cs:1295-1306`), as the T key's does today: a held night dries slowly and is Glow's hour. The kernel's own day stands (ruling 17; `K/CommandHostGrowKinds.cs:51`) | The window's beam; a stick's shadow at a fire |
| **The sky** (his) | `changeable`, today's bursts; `settled`; `wet_spell`; `dry_spell`; later `wild`, `hard` | No fall for 60 s: the signs come first | Rain's rhythm, so wet ground, fire's spread, campfires and Grow's bonus | The vent's note; smoke at a fire |
| The wind (beyond) | `breeze`, as the kernel blows; `still`; `blowing` | How far grass and boughs travel; how loud the wind is | Nothing: the kernel's wind, fire and every drift stand | The door bay |
| The moon (beyond) | The calendar's phase; `dark`; `half`; `full` | Living lights, moths; never the night's light level (`docs/look/GLOW.md:56`) | Nothing | The Lamp-moon |
| The folk's day (beyond) | The week's own; `market`; `lamp_fair`; `mending`; `drove`; `rest` | Who is on the roads | Nothing | The villages |

The room's own features are each dial's face. Only the fire is also a door, until play proves more. The last three dials come with DIAL-13.

**Comfort** is this machine's, with no command. Thunder: `off` to start, or `far`; `near` and any flash wait for his word. Its row is SOUND-9's, and the volumes are SOUND-2's. A night floor: the picture's darkest night is lifted and the kernel's light is left alone; off to start, since his dark night stands (`docs/look/GLOW.md:56`). No packet builds the night floor yet, so it is not promised: it is asked of the look page.

**Three doors to one dial.**

- *Picture days on the title page* (his "main menu"; first). A strip of cards, each a picture and a mark, no word needed. Each card is a real day; the table below says which. In the first build a card only pins the look. Once the dial is saved, a card chosen while New game's question is up sets the new game's three dials (`M/MenuModel.cs:248-250`). With none, New game stays one click. Continue shows its game's dials and does not ask.
- *Settings* (his). Rows that step each dial, both ways. A row's face shows only what differs today. The year: Today, the four seasons' held days and `freeze_up`. The hour: the clock, `dawn`, `midday`, `dusk`, `night`. The other three turns join when LEAF-8 draws them apart; a part joins when FLORA-9, VILLAGE-3 or SOUND-16 gives it a use. T and Shift+T step the same rows (`N/RoomHud.cs:148-205`), so a row never lies. While a dial is off its first stop the HUD's clock line shows its mark (`N/RoomHud.cs:742-751`). Every row lives in one model, SOUND-2's, and each owner registers its own rows there: the sound's sliders, these rows, the leaves' one switch. No settings page exists before a room loads; SOUND-5 builds one. The first build's rows work there at once, since they only write this machine's pick. The saved rows (DIAL-7) show a kept game's dials there and cannot turn them: a turn is a command, and no host runs before play. For a new game they hold the same choice a card does.
- *The fire's stones* (beyond; a door that needs no reading). Eight flat stones ring every campfire, lit or out. Each is marked by what lies on it: rime, a drip, a petal, a seed head, straw, a nut, a gold leaf, a frost feather. Stand by one, look at it and hold the use key for one second: the year turns to it. Look at the hearth and hold: Today. A lit fire's flame leans to the stone under the eye. On the first rest the stones light one after another, once. In Creative the same eight marks rise round the body on a held key, anywhere. The smoke (the sky) and a stick's shadow (the hour) are read-outs here, not controls.

| Card; its token | Station; part | Sky, once saved | By ear (the SOUND page) | Shown once this is in the build |
|---|---|---|---|---|
| Today; `today` | `calendar`; `clock` | The game's own | The hour's own cast | Always |
| Blossom morning; `blossom_morning` | `high_spring`; `dawn` | `settled` | The dawn chorus (SOUND-16) | Today's look: first build |
| The gold turn; `gold_turn` | `leaf_fall`; `dusk` | `settled` | Dry leaves underfoot (SOUND-16) | Today's look: first build; LEAF-14 adds the falling leaves |
| First frost; `first_frost` | `freeze_up`; `dawn` | `settled` | Frost's glass tick (SOUND-16) | Today's look: first build |
| Snow hush; `snow_hush` | `deep_winter`; `midday` | `wet_spell` | The highs gone (SOUND-16, once DIAL-14 draws snow) | DIAL-14, DIAL-6 |
| Frog night; `frog_night` | `high_spring`; `evening` | `wet_spell` | The frogs (SOUND-16) | SOUND-16, DIAL-6: its soft rain is a saved sky |
| Firefly dusk; `firefly_dusk` | `high_summer`; `dusk` | `settled` | The first cricket (SOUND-16) | LOOK-18 |
| Nut fall; `nut_fall` | `harvest`; `afternoon` | `dry_spell` | Knock and thud (SOUND-19) | FLORA-8, DIAL-8, DIAL-18, SOUND-19 |
| Stone day; `stone_day` | The day and hour the beam touches the standing stone | `settled` | A visit of music as the beam touches the stone (SOUND-14) | WORLD-17, LOOK-14, SOUND-14 |

A card that promises by name and delivers a tint is a filter. So a card stays hidden until its namesake's packets are in the build, the one that draws it and the one that voices it; a test reads each card's owners from the almanac file (DIAL-1). The first build shows four: Today and the three whose look and ear are both in it. The SOUND page's `named_day_<id>` scenarios take each card's station, part and sky from the same file, so a card and its scenario are one day.

**Where a turn is kept.** The `view` dials (the hour, later the wind, the moon and the folk's day) change look and sound only: anywhere, any mode. Year and sky: anywhere in Creative. In Challenge they are kept at a campfire, lit or out, within its rest radius of 0.3 m (`K/CommandHostCamps.cs:28`), or by a card before a new game. Away from a fire the row only pins the look and says so, and the Gubble lights the way to the nearest fire. A new game starts at the starting campfire (`K/CommandHostCamps.cs:220-222`), so a first turn is always in reach. Live is not offered yet (`K/CommandHost.cs:169`). As a solo preview it turns as Challenge does; once an island is shared the year and the sky are the island's (`docs/game/MECHANICS.md:174`).

**The Turning** (beyond). The world answers within three seconds, at the feet first.

- *The year.* The land's globals cross-fade whole over 3 s, and the sound's near layers with them: the step, the rustle, the small events. Its far air is made in stretches, so it has changed by 10 s. The plants answer in order: the LEAVES page's `plant_year` runs a front out over the same 3 s, from the feet, or from the fire when the turn is made at its stones (DIAL-10), and each plant does its row's one act a little late. The front is never a drawn line, rim or colour and cannot be paused or held, so two seasons never stand side by side to be played in. With DIAL-20 a ragged gust rides it down the wind, lifting the station's own thing. Last, the nearest plant that bears now shows one windfall landing, on a spot that was already there (DIAL-18).
- *The sky.* A turn brings no fall for 60 s, and those are the signs, staged. Each sign is keyed to the fall, read ahead by `FallIn`, never to the turn. A cloud's shadow comes in from the door bay when a fall is within 60 s. The birds stop within 50 s. The vent's note, the frogs and the Gubble's look come within 30 s. So after a turn they come by 3 s, 10 s and 30 s, then the first drops. The signs are true: if the new sky's first fall is 200 s away, they wait for it.
- *The hour.* The beam swings to its place and the lamps go on or off in their wave.

## The vocabulary every page uses

Closed tokens, and the only vocabulary table of the three pages: the SOUND and LEAVES pages name every season, part, fall, state, sky, cover, ground, place, grass, plant and litter by these. No page invents another; a new one is an edit here. A token is read only inside its group, and a file always names the group. So `held` may be a year mode and a phase, `bare` a cover and a phase, `snow` a fall and a cover. `slot` is a place here; on the LEAVES page it is also the name of a row's key, which is no token. The singers' kinds are the SOUND page's own rows, and the leaf classes, acts, slots and finds the LEAVES page's: closed lists in their files, in no group here.

| Group | Tokens | Rule |
|---|---|---|
| Season | `winter`, `spring`, `summer`, `autumn` | The look's own (`N/Look/LookTuning.cs:203`). His "Fall" is `autumn`. Four seasons stand (decision 40, `docs/game/MECHANICS.md:234`) |
| Station | 1 `deep_winter`, 2 `thaw`, 3 `high_spring`, 4 `flush`, 5 `high_summer`, 6 `harvest`, 7 `leaf_fall`, 8 `freeze_up` | Spans of the same four seasons. Odd: the days the look holds one season whole. Even, the four turns: the days it cross-fades two (`N/Look/LookClock.cs:416-447`). So the name always fits the picture |
| Part | `dawn`, `morning`, `midday`, `afternoon`, `dusk`, `evening`, `night` | From the shown day's sunrise R, sunset S and noon (`LookClock.SunTimes`). Dawn runs from R-0.75 h to R+1 h; morning to noon-1 h; midday to noon+1 h; afternoon to S-1 h; dusk to S+0.75 h; evening to S+3.5 h; night the rest. A held part is its middle. With under 4 h or over 19.75 h of sun the edges cross: a squeezed part keeps 0.25 h, taken from its longer neighbour |
| Fall | `fair`, `showers`, `rain`, `snow` | What comes down from the sky; not his "Fall". The kernel's rain runs from 0 to 1 and is cut where the kernel cuts it: up to 0.02, up to 0.65, above (`K/NatureState.cs:50-51,489`). `snow` is a fall drawn and heard as snow while warmth is under 1 degree Celsius (C); the kernel's numbers do not change. No moment says `snow` until DIAL-14 draws it, and the shown moment says it only while DIAL-14's switch is on: until then a cold fall stays `showers` or `rain`, so no ear hears a snow the eye does not see |
| State | `dew`, `frost`, `mist`, `heat`; later `gale`, `dust_wind` | Derived, never saved; any may stand beside a fall |
| Sky | `changeable`, `settled`, `wet_spell`, `dry_spell`; later `wild`, `hard` | The sky dial's stops. `nature.describe`'s `weather.pattern` keeps its three words (`contracts/game-command.schema.json:5005-5010`) |
| Wind; Thunder | `breeze`, `still`, `blowing`; `off`, `far`, later `near` | A `view` stop; a comfort stop |
| Moon; Folk day | `dark`, `half`, `full`; `market`, `lamp_fair`, `mending`, `drove`, `rest` | Typed in DIAL-13's round, not in CR-5 |
| Cover | `bare`, `dew`, `wet`, `frost`, `snow`, `litter_dry`, `litter_wet` | What lies on the ground, over its kind. The first that holds of snow, frost, litter, wet, dew, bare. Only `leaf_litter` is a litter cover. A `needle_bed` makes the ground `needles` and is no cover: the cover there is the first of the others that holds, and its quiet is its ground kind's. A sodden spot is never drier than `wet` |
| Phase | `bare`, `held`, `bud`, `catkin`, `leaf`, `bloom`, `green`, `fruit`, `turn`, `drop`, `seed` | A plant's state at a station; the LEAVES page draws each. `held` is dead leaves kept on the twig and, on a grass, standing straw |
| Ground | `grass`, `dirt`, `sand`, `stone`, `wood`; new `moss`, `needles`, `shallows` | Today's five (`A/SoundBank.cs:41`) and the SOUND page's three. The room's own material gives it. The ground's word may lay another over it: `needles` on a needle bed, a lane's floor between clumps of grass. Mud is `dirt` on a sodden spot |
| Grass | `path_turf`, `meadow`, `oat_grass`, `tussock`, `fescue`, `wood_grass`, `sedge`, `rush`, `reed`, `reedmace`; later `dune_grass`, `savannah_grass`; and `none` | Each key's row is the LEAVES page's. A new kind is a row there and a token here, in one edit |
| Plant | Trees `oak`, `birch`, `spruce`, `pine`, `apple`, `willow`, `maple`, `beech`, `thorn`, `alder`; bushes `hazel`, `gorse`, `bramble`, `currant`, `bilberry`; the kit's `broadleaf`, `conifer`, `shrub` | Rows as for a grass. The kit's three are also fuel classes (`N/Room/RoomData.Island.cs:34`), which a made tree keeps |
| Litter | `leaf_litter`, `needle_bed`, `none` | What a plant sheds, in the ground's word; its amount is `litter01` |
| Place | `open`, `forest`, `lake`, `cliff_foot`, `slot`, `hall`, `rim_desert`, `under_water`; and `spring_line`, `summit` | Where a body or a plant stands: the SOUND page's eight, and two more for the year by country. SOUND-11 reads all ten. The two new ones come from the ground's word, ring as `open`, and move who sings |
| Day card | `today`, `blossom_morning`, `gold_turn`, `first_frost`, `snow_hush`, `frog_night`, `firefly_dusk`, `nut_fall`, `stone_day` | The cards' ids, in the almanac file and in the SOUND page's `named_day_<id>` scenarios |

**One moment, two fillers.** `DialMoment` (DIAL-1) carries the station and the part, each with how far through it. It carries `warmth_c`, the fall, the kernel's rain and wind, the sky and the view stops. It answers `FallIn(seconds)`: the fall read ahead on the same counter and sky list. So every sign is true, and no vent warns of rain a sky has taken away. It carries `thunder` and four amounts from 0 to 1: `wet01`, `dew01`, `frost01`, `snow01`. It names the states that hold, by the weather table's rules. Its inputs are the day and hour, the room's hemisphere and site, the preset's sun and seasons, the dials, the weather counter and the sky list. It is pure.

- **Shown** is filled from the look's moment, pins included. The look, the sound, the folk and LOOK-9's globals read it, so DIAL-1 goes before LOOK-9.
- **One holder in a room.** `RoomWorld` keeps the room's one shown moment. It is filled once a frame by DIAL-1's pure function, from the look's moment and the host's weather (`N/RoomWorld.cs:38`, `N/Look/LookDirector.cs:51`, `K/CommandHostNature.cs:198`). The holder is the integrator's diff in `N/RoomWorld.cs`, in the same commit as the line that hands `RowsNear` to the word's default, with whichever of DIAL-3 and LEAF-14 merges first. The ear's snapshot and every reader of the look take that one, and none fills its own: so the leaf the eye sees fall and the step the ear hears are of one moment. Outside a room the page fills it: the scenario in the Listening Room, the sky's reader on the title page.
- **Ruled** is filled from the host's clock (`K/CommandHost.cs:195`), `x_clock` and the saved dials. The larder, the receipt and `nature.describe` read it.
- **The pin order.** One slot holds the look's pinned hour and day today, and the last writer wins: the launch's flags, then the T keys (`N/Look/LookDirector.cs:209-213,495-517`). The first build's rows and cards write the same slot. Under it sit the saved dial, then `x_clock`, then the machine's clock. While a pin differs from the saved dial the HUD says the land is only being looked at.

This page owns that air. The LEAVES page owns the spot: `Cover(moment, word)` is handed the ground's word, what grows and lies at a point, from `FloraGround.At`. The SOUND page owns the voice. `litter01` is by place, so the word carries it and the moment does not. The amounts are provisional:

| Amount | What it is | Holds at |
|---|---|---|
| `wet01` | 1 while the fall is not `fair`; then a straight run down to 0 over 75 s | 0.5 |
| `dew01` | 0 in a fall, for 90 s after one, under 0 C, and under `dry_spell`. Else by part: `night` 0.6, `dawn` 1, `morning` 1 running to 0 | 0.3 |
| `frost01` | The look's own frost: the season's share times the hour's (`N/Look/LandSeason.cs:170,180`), and by LOOK-15's shade once it lands. One source, so eye and ear agree; the state `frost` is this amount holding | 0.3 |
| `snow01` | The look's own snow share (`N/Look/LandSeason.cs:170`), and 0 while DIAL-14's constant is off: the look dabs snow on sky-facing rock and trees today (`S/painterly_land.gdshader:349-352,394-396`), and no step creaks on it before a flake falls. From DIAL-14 it holds where the look lays snow: those dabs and the meadow floor. Until then winter grass answers `frost` (`S/grass_blades.gdshaderinc:248-249`) | 0.3 |
| `litter01` | By place, in the ground's word. From LEAF-9, each kind by its own eight numbers. Until then the seam's default: within 0.6 m of a broadleaf tree's copy (found through `RowsNear`, `N/Room/PlantCopies.cs:193`), told by its fuel class since a made oak's species is `oak`, by station 1 to 8: 0.5, 0.3, 0.1, 0, 0, 0.2, 1, 0.8. LEAF-14 draws its litter tint from the same default, so eye and ear cannot differ | 0.15; `litter_wet` while `wet01` or `dew01` holds or the spot is sodden, else `litter_dry` |

- Under today's sky a fall ends at 269 s of the counter and the next starts at 481 s (`K/NatureState.cs:39-44,480-486`). So leaves under a tree are dry for 174 s in each 360 s, and dew and frost form under a sky nobody has turned.
- Warmth is the night's low plus a share of the day's range: `night` 0.1, `dawn` 0, `morning` 0.5, `midday` 0.9, `afternoon` 1, `dusk` 0.6, `evening` 0.3. It is 2 C less in a fall, so a spring evening's soft rain is 6 C and the frogs sing through it.
- The cover is read when a step is due, as the ground is (`A/AudioSource.cs:38-39`), never each frame.
- Under a roof there is no cover. Where the snapshot's `Outdoors` is false today (`A/AudioSource.cs:29-30`), and in a `hall` once SOUND-11 reads places, the caller takes `bare` and does not ask `Cover`.

**Four seam files, landed once.** Each is pure, sits in `N/Seams/` with its test in `T/Seams/`, and lands with DIAL-1. So every lane builds against all four from the first day, and no later packet creates one. A later change to a seam is the integrator's diff, with the packet that needs it.

| File | Whose design | What it answers |
|---|---|---|
| `WorldDials.cs` | This page's | The tokens; `StationOf`, `PartOf`, `Warmth`, the amounts, `Cover`; `DialMoment` |
| `Gust.cs` | The SOUND page's; the LEAVES page draws it | `Gust(x, z, t, wind)`: 0 to 1 at a point. `t` is the weather counter's time, so a pause stops eye and ear together; `wind` is the kernel's (`K/NatureState.cs:364`). Gusts travel down the wind and have troughs: under 0.15 is a lull (provisional). From SOUND-16 and LEAF-4 on, no reader keeps a gust of its own |
| `FloraGround.cs` | The LEAVES page's | `At(x, z, moment)`: the ground's word, with the fields that page lists. The look registers its maps: the kinds with LEAF-3, the litter by kind with LEAF-9, the trace of feet with LEAF-10. With none registered the default answers: `leaf_litter` at `litter01` within 0.6 m of a broadleaf tree, no grass kind, nothing sodden, nothing trodden, nothing mown. The seam also keeps the list of mown places, a centre and a radius each: empty until DIAL-13 registers the villages', and read by LEAF-8's blades |
| `StepLoudness.cs` | The SOUND page's | `StepLoudness(surface, cover, speed)`: the ladder's first level, its settled level and its hush factor, read by the SOUND page's four rules under its ladder. A **surface** is the Ground token and, on `grass`, the Grass token and whether it stands as straw (its phase is `held`). `grass` ground whose word names no kind steps as `meadow`, so the default word and a look with its kinds switched off sound alike |

## The calendar

**The eight stations.** Spans are days of the year under the shipped preset. Its centres are days 15, 105, 196 and 288, and its hold is 0.3: a season is whole within 0.3 of the gap on each side of its centre (`game/styles/storybook_painterly/v2.json:150`). 24 November, day 328, is `freeze_up`. A held station shows its held day. Every row gives five answers: underfoot, to pick, an animal, the folk, a toy. Animals are marked heard (h), a sign (s) or far scenery (f).

| # | Held day; span | Night, day (C) | Underfoot | To pick | Animals | Folk | A toy |
|---|---|---|---|---|---|---|---|
| 1 | 15; 353 to 42 | -7, 1 | Frost on grass, snow's creak where it lies; held oak leaves rattle | Haws, cone seed, hoards | Tracks (s); winter flocks (f) | Smoke from every roof | Follow tracks to a sleeper's door: its breath shows |
| 2 | 60; 43 to 77 | -2, 8 | Squelch and drip | Sap, pollen, thorn leaf | Frogs walk to water (f) and call by day (h); the first bee (f) | Sap pails; mills start | A drip shaken from a bud; stand still and frogs sing |
| 3 | 105; 78 to 132 | 5, 15 | Wet brush, dew | Nectar, young leaves, spruce tips | The dawn chorus (h); the drove goes up (f) | Terraces sown | Petal rafts lie on still water |
| 4 | 150; 133 to 168 | 10, 22 | Swish of tall grass | Currants, grain, honeydew | Froglets leave (f); glow-worms (f) | Hay cut round each village | The tall-grass maze; a pressed trail |
| 5 | 196; 169 to 223 | 15, 28 | Hiss and snap of straw | Bilberries, grain; resin, a find | Birds silent; crickets by night (h) | Rushes cut; lamp fairs | Gorse pods pop at a touch |
| 6 | 242; 224 to 260 | 9, 21 | First crackle; nuts knock | Nuts, apples, brambles, fungi | Hoarders bury (f); gossamer (s) | Nuts and apples home | Nuts thud down and keys spin by themselves; no nut within 0.3 m of a body |
| 7 | 288; 261 to 315 | 3, 13 | Dry litter crackles; wet litter is hushed | Late fungi, haws, apples under leaves | The drove comes down (f); the roost wheels (f) | Leaves cut for thatch | Kick through litter: leaves fly and skitter |
| 8 | 334; 316 to 352 | -3, 5 | Frost: a glass tick, then quiet where trodden | Sweet haws, cone seed; galls, a find | Sleepers go to ground (s) | Doors banked; early lamps | Draw paths in frost |

**Who builds each column.** A cell with no builder is not promised.

- *Underfoot:* DIAL-3 routes each step to its cover and SOUND-16 voices the covers in the same build; then SOUND-7. Levels are the SOUND page's compressed ladder: dry litter is the brightest step, at most 4 dB over soil.
- *To pick:* in this order, on the same files. FLORA-8 brings the nut, as the brief has it. DIAL-8 moves three kinds by station. DIAL-18 adds the rest. LEAF-17, the LEAVES page's, adds grain, down and haws once the grasses are sets.
- *Animals:* the heard ones are SOUND-16's, then SOUND-10's. Crickets chirp 40 times a minute at 10 C, 112 at 20 C, 148 at 25 C. Frogs call while warmth is over 5 C: by day at `thaw`, from dusk into the night at `high_spring` and `flush`. Signs and scenery are DIAL-16's. The drove (the villages page's herd on the move) is VILLAGE-12's, the roost and moths VILLAGE-17's, glow-worms and fireflies LOOK-18's.
- *Folk:* DIAL-13, on VILLAGE-3's `FolkDay`.
- *Toys:* every toy made of leaves, grass or prints is the LEAVES page's, by its toys table (LEAF-4, 8, 9, 10 and 11); spinning keys are a maple's own act there. A nut that thuds down is the food view's windfall seen arriving, DIAL-18's: it lands on a spot the larder already counts, or it does not fall. The sleeper's breath is DIAL-16's. Frogs that sing only for a body standing still are SOUND-10's hush. Each toy's sound is SOUND-19's.

**Weather at one centimetre.** The player keeps a person's eyes and ears; every drop and flake keeps its true size.

- A raindrop is a third of the body (the worldbook; LOOK-10). A flake is a third of a body across and falls at walking pace. Hoar frost stands knee high, as glass feathers on blade edges. A dew bead is a boulder on a blade tip, and never one bead to catch: a shaken bough sheds its dew as spray, and a walker leaves a dark line through it (LEAF-10's shake and its trace map). Mist is drifting beads.
- Water in the cold is never a sheet. It is a rim of white ice a step wide round open dark water with steam over it, as on a spring-fed lake. So no ice is drawn that a body cannot stand on.
- A leaf heap or a snow drift is drawn only where WORLD-5's footing is already soft: dust heaps, moss, nets. The picture never promises a landing the body does not get. Leaves the wind gathers in the lee of a trunk or a stone are a drift at most knee deep (0.03 m): ground cover, the LEAVES page's, and no heap.
- A worm is ten bodies long and a bee is the body's size. So nothing larger than the body comes within 1 m, and none chases.

**The year by country.** No season is everywhere. Each pocket is look and sound only; no rule reads one.

| Country | Its year | Built by |
|---|---|---|
| The plain and its woods | The whole calendar | This page |
| Waterside, the spring line (`spring_line`) | Green half a station early; mist longest; the frogs | LEAF-8, from its ground map; the frogs SOUND-16's |
| Summit worlds (`summit`) | Half a station late in spring, early in autumn | LEAF-8, from its ground map |
| A slot in all-day shade | Frost stays through the day | LOOK-15 |
| The beam's track | Frost gone along it; flowers open as it comes | LOOK-14, LOOK-15, FLORA-9 |
| By a lit fire | A ring of thaw: no frost or snow within 0.25 m | DIAL-14 |
| A hall under a mesa | No fall, frost, snow or litter; the season is heard on the roof and seen at the door | WORLD-11, LOOK-21, SOUND-11 |
| The rim desert | No leaf fall; bright cold or bright heat; a bloom for 90 s after each rain | LOOK-8, FLORA-10 |

**Plants.** Phase at stations 1 to 8 by first letter, but u for `bud` and o for `bloom`; then what is picked, by station. The home of every phase row is the LEAVES page's tables, and then its files under `game/assets/flora/`. This table is a copy for reading: where they differ, those win. The bushes are not tabled there yet, so their rows here are the draft LEAF-11 and FLORA-21 copy. A kind with no inventory row is a **find**: drawn by its plant's `find` row (the LEAVES page), a sketch in the almanac (DIAL-19), and nothing in a pocket. What is picked from a plant shows in the plant's `slot` at that station, and one test holds picture and larder together (DIAL-8).

| Plant | Stations 1 to 8 | To pick (stations) |
|---|---|---|
| Oak | h h c l g f d h | Acorn 6 to 7; gall 5 to 8, a find |
| Birch | s u c l g t d b | Sap 2; seed 6 to 1 |
| Willow | b c l g g g d b | Pollen 2; down 3, a find; rods 8 to 1 |
| Apple | b u o l g f d b | Apple 6 to 8, on the ground from 7 |
| Thorn | b l o g g f t b | Leaf 2 to 3; haw 6 to 1, sweeter after frost |
| Pine | g g c g g g g g | Cone seed 7 to 2, cones open only when dry; resin 4 to 6, a find |
| Spruce | g g u g g g g g | Tips 3; cone seed 6 to 8 |
| Maple | b u o l g s t b | Keys 6, a find |
| Beech | h u l g g f t h | Mast 6 to 7 |
| Alder | s c l g g g d s | Cone seed 7 to 1 |
| Hazel | c c l g g f t b | Nut 6 to 7; pollen 2 |
| Bramble | h u l o g f t h | Berries 6 |
| Currant; bilberry | b u o f g g t b; b u o g f f t b | Berries 4; 5 to 6 |
| Gorse | g g o g s g g g | A flower in any station; pods crack at 5 |

**Grasses.** The LEAVES page's ten; their phase letters, heights, clumps and sway are its rows. Cool kinds shoot at the thaw, seed at station 4, stand as straw at 5 and are green again at 6 to 7. The warm kind is tan from 8 to 3 and tallest at 5 to 6. Side by side they change the meadow's pattern with the dial. Between the plants of a clump kind the foot is on the lane's own ground, and the lanes of `tussock`, `sedge` and `rush` are sodden: that is the squelch below.

| Grass | Underfoot | To cut or pick (stations) |
|---|---|---|
| `path_turf` | The softest pat | Nothing: the quiet step, a firebreak |
| `meadow` | Swish; a hiss as straw | Grain 4; hay |
| `oat_grass` | A dry rattle while it stands as straw, 6 to 1 | Grain 4 to 5; straw |
| `tussock` | Thump on top, squelch between | Tinder; a dry seat |
| `fescue` | A dry whisper | Summit pasture |
| `wood_grass` | Soft, on litter | Plaits when dry |
| `sedge` | Rasp, squelch | Cord, mats |
| `rush` | A click of stems | Pith for lamps |
| `reed` | A hiss of canes | Thatch 7 to 1; pipes |
| `reedmace` | A slap of straps | Down for tinder 6 to 8 |

Waiting for their countries: `dune_grass` (LOOK-8), whose arcs show the wind, and `savannah_grass` (WORLD-19; h h h g g s g h).

**The day.** Each part's builder is the page named: singers the SOUND page's (SOUND-16, SOUND-10), folk the villages page's, flowers FLORA-9's.

| Part | Air and plants | Animals and folk | Heard |
|---|---|---|---|
| `dawn` | Coldest; dew or frost heaviest; mist | The chorus, 100 minutes long; dew-rollers; ledge folk drift down | The year's fullest sound at station 3, one voice at station 1 |
| `morning` | Dew burns off; flowers open by kind | Bees start; grazers feed; field work | Song thinning |
| `midday` | Sun-followers full round | The lull; grazers lie up; market | The quietest daylight |
| `afternoon` | Warmest; cones open | Winged ants on calm days; mills | Wind and insects |
| `dusk` | The air stills; evening flowers | A second chorus; the roost wheels; fireflies; the lamplighter | Wing-rush; the first cricket |
| `evening` | The Lamp-moon wakes | Moths at lamps; frogs; the beacon wave | Frogs and crickets take the birds' pitch |
| `night` | Dew forms; glowing wood | Worms and snails out; an owl; the watch | Crickets by warmth; near silence at stations 8 to 1 |

**The weather.** A fall is the kernel's; a state is drawn and heard only. Signs come ahead, each keyed to the fall and read from `FallIn`: a cloud's shadow from the door bay, 60 s ahead (asked of LOOK-5); the birds stop, 50 s ahead; the vent's note and the frogs, 30 s ahead (all three SOUND-16's); smoke lying flat at a fire (DIAL-10); the Gubble looking up, 30 s ahead (DIAL-17).

| Kind | When | The kernel, by today's laws | The land answers | Built by |
|---|---|---|---|---|
| `showers` | Rain 0.02 to 0.65 | Wets the ground, so fire spreads less; no campfire dies; no Grow bonus | Snails out, moss swells; a fine hiss, taps on leaves | LOOK-10, SOUND-9 |
| `rain` | Over 0.65 | Grow grows two steps (`K/CommandHostGrow.cs:164`); a lit campfire dies after 30 s (`K/CommandHostCamps.cs:68`); at 0.8 fire stops spreading (`K/NatureState.cs:92`) | Rills run, pools fill; drums on leaves, bells on water | LOOK-10, SOUND-9 |
| `snow` | A fall while warmth is under 1 C | As the fall it is | Flakes; a dusting with no depth and no collider; high sounds gone | DIAL-14, SOUND-16 |
| `dew` | `dew01` holds | None | Beads on blade tips and cobweb nets; steps hushed | LOOK-16, SOUND-16 |
| `frost` | `frost01` holds | None | A walker's prints stay dark; a glass tick | The look today; LOOK-15; SOUND-16 |
| `mist` | `dawn` while dew or frost holds; by day in `wet_spell` between falls | None | The cloud sea; near sounds only | LOOK-16, SOUND-8 |
| `heat` | No fall now or for 90 s; stations 4 to 6; `midday` or `afternoon`; 21 C or more; never in `wet_spell` | None | Pods crack, cool grasses brown; grasshoppers, the lull | SOUND-16; a drawn shimmer is set aside |
| Thunder | A `rain` that starts within 120 s of `heat` | None | A long low roll from the door bay, by the Thunder stop | SOUND-9 voices `far`; `near` and a flash wait for his word |

**The four skies.** Each is a kernel law beside today's, so today's rules pack plays all four, and each gives what no other does. Every sky keeps today's wind law, 0.12 to 0.34 m/s (`K/NatureState.cs:47-48,488`), so no drift leaves today's range.

| Sky | One period of the weather counter | Rain over 0.65 | A way to play |
|---|---|---|---|
| `changeable` | 360 s: today's, to the byte | 91 s in each 360 s | The grower's: Grow's bonus comes round, and camps drown |
| `settled` | 720 s: fair, but for one passing shower that peaks at 0.5 | Never | Walking and watching: camps last; long fair spells for dew, mist and frost; one shower with all its signs |
| `wet_spell` | 360 s: a soft steady fall at 0.4 for 240 s, then 120 s dry | Never | The quiet season: hushed ground for walking up close, fire that barely spreads, snails, frogs and fungi; in the cold, snow beside a living fire |
| `dry_spell` | No fall | Never | The fire-keeper's: fire runs to its circle's edge, litter stays loud, no dew, life gathers at water |

## The rule that keeps a free choice kind and honest

**Kind.**

- Nothing is lost at a turn: pockets, chest, builds, sowings and seedling sizes stay. A plant out of season sleeps and keeps its count. A cast made before a turn can still be undone after it.
- No price, no wait, no cool-down, no lock: a setting is not an ability.
- No hungry station: each keeps at least half of today's spots live, and something that heals near every camp.
- Weather never moves, harms or strands a body; nothing changes under one. No drop, nut, cone or fruit lands within 0.3 m of one. A leaf, a petal, a key or a flake may land on one and ride there until it moves: drawn only, never an item.
- No sky locks its own dial: a campfire that rain put out still turns it.
- Thunder is off to start, and no creature chases under any dial.
- Each game keeps its own dials. Saves sit under the team and the mode (`N/Saves/TeamBoot.cs:108-110`), so two players on one machine never share a season.

**Honest.**

- **One larder.** A turn changes what kind a spot offers, never how many, and never how much it heals. Which plants bear, and where, stays today's hash of the plant's id (`K/CommandHostFoodLayout.cs:93-113`). No turn touches the picked records or their regrowth clock (`K/CommandHostFood.cs:94-98`). A calendar row may hide a share of a plant's spots, or rename a spot to a kind that heals the same. Turn away and back a hundred times: not one unit more.
- **No dial opens a way.** A way is a climb or a crossing the generator has proved: a gate, in the brief's word. Colliders, the footing grid and the body's numbers are byte-equal across every station, part, sky and view stop. Snow, ice, frost and litter are drawn and heard, never stood on. A sky may shut what rain shuts (BODY-17's still water); none opens a way that is shut at the first stops. Every way is proved with each dial at its least helpful stop.
- **The step is heard, and that is all.** He said the leaves change how the feet sound, and so how one meets the wildlife. The noise ring that singers and scenery read (BODY-11) is scaled by the step's own factor, from the SOUND page's `StepLoudness(surface, cover, speed)`: 2 on `litter_dry`, 0.7 on `litter_wet` and `snow`, 0.5 on `moss` and `needles` (SOUND-10). Standing still in a tall stand of `oat_grass` or `reed` brings the singers back in half the time: the LEAVES page's hide. Dry leaves are the season for hearing; wet leaves are the season for walking up close. It moves no collider and no gate.
- **The companion never turns a dial.** The Gubble reads the dials, knows what bears now, and may suggest a station; the player's hand turns it.
- **Absent is today, to the byte.** A game nobody has turned saves what it saves now.
- **One calendar.** No reader keeps a station, a part or a weather rule of its own.

## State, command and the contract rounds

**The saved dials.** The island save's team block keeps `dials` beside `camp` and `clock`. The room state shows them as `x_dials`, new and typed, beside `x_clock` (`contracts/room-state.schema.json:349`):

```
dials:  { "year": {"mode": "calendar"} | {"mode": "held", "station": "<station>"},
          "view": {"hour": "clock" | "<part>"} }
nature: "sky": [ {"token": "<sky>", "from_s": <weather counter>}, ... ]   1 to 4, newest last
```

- Every key is optional and closed. At all first stops no record is written. A bad record is a notice and the defaults, never a failed load, as the clock's is (`K/CommandHostCamps.cs:917-920`).
- The sky has one home, the nature block. On export `x_dials.sky` is its newest token; on load the nature block is the truth.
- `x_clock` is not touched: its five keys are all required and no other is allowed (`N/Look/WorldTime.cs:22,82-85`).
- `walking`, `turning`, the wind, the moon and the folk's day are not in CR-5. Each key joins in the round that builds it.

**How a sky runs.** The phase is always the weather counter modulo the sky's period, 360 s or 720 s. `from_s` is only a mask: no fall for 60 s after it, then a ceiling that rises from 0 to 1 over 45 s. So a turn can remove rain and never bring it nearer.

**`world.set_dials`:** player-only. Its args are any of `year`, `sky` and `view`, tokens only. A preview records nothing.

- It joins `player_only_ops` beside `world.set_physics` and `world.set_mode` (`K/CommandHost.cs:158`): refused from the companion with `permission_denied`, never held for a click (`:663-667`), no tool on its surface. It has no price to follow a principal (`contracts/README.md:305`).
- A `year` or `sky` turn is durable. It commits through `CommitNature` (`K/CommandHostNature.cs:951`): the nature block, the team block and the receipt in one write, one room revision. The receipt keeps the dials before and after, the ruled station and the weather counter, and replays (`contracts/README.md:103,107`). So the op joins `NATURE_OPS` (`game/scripts/creation_authority.gd:56`); without that a save with a turn in it does not load (`:1237`).
- A turn records an empty undo step. `room.undo` walks back through it to the casts before it and leaves the dial standing (`K/NatureState.cs:539-552`). An undo that would take back turns alone is `invalid_args`. Turns in a row share one step, so they never push a cast out of the eight steps kept (`K/NatureState.cs:388`).
- A `view` turn alone moves no revision and keeps a transient receipt. The team block is written with the next save.
- When the ledger is full the HUD compacts it and tries once more, as the hand keys do (`K/CommandHostSandbox.cs:588-598`).
- `BootDials` is a kernel seam beside `BootMode` (`N/Saves/CommandHostTeams.cs:85`). It sets a fresh game's dials as the first save's starting record: no command, no revision, no receipt. It withdraws no standing offer of an earlier save (`K/CommandHostNature.cs:1709-1717`). On a game that exists the save's dials win, with a notice, as its difficulty does (`N/Saves/TeamBoot.cs:46`).
- `nature.describe` gains `dials`: station, part, the derived fall (so a snow fall is told as `snow`), the sky, and the kinds live now.

**What keeps a replay the same.**

1. Weather stays a pure function of the saved counter, the saved sky list and the kernel's laws (`K/NatureState.cs:474-491`).
2. No dial moves the weather counter: fire's wait and the spread gate count on it (`K/NatureState.cs:897-899`).
3. The ruled station is a pure function of the host's clock, `x_clock` with its hemisphere, the machine's zone, the saved year and eight spans. The spans are constants in DIAL-1's file, held equal to the shipped preset's by a test. Under `calendar` it turns at the look's midnight. A station that turns by itself is derived, as a seedling's size is: a refresh, no revision.
4. A resting cell replays the weather it missed, through the sky list. The whole-cycle skip (`K/NatureState.cs:784,822-846`) takes each sky's period and never crosses an entry. A fifth turn first settles the cells resting since before the second entry, as far as its `from_s`, then drops the oldest.

**CR-5, one commit, the integrator's,** after DIAL-5's measure:

- `contracts/room-state.schema.json`: `x_dials`; the nature block's `sky`.
- `contracts/game-command.schema.json`: the op, `player_only_ops` at `:5921`, `nature.describe`'s `dials`.
- The README, examples, `validate.py` and tests. In the same commit the mock (`companion/**`, no Run 4 lane) answers `dials` at first stops and `unsupported_capability` to the op; it has `world.set_mode` (`companion/src/garage_game_companion/mock_host.py:2919`).

**CR-6, with DIAL-8, after CR-2:** `contracts/island-rules.schema.json` gains an optional `food.calendar` by `food` (`:1614`): rows of species, station, a kind or none, and the live share of spots.

## Packets

No packet edits collision, footing, the body's constants, `x_clock`, a pinned pack or a pinned preset. **One editor at a time, in this order, for all three pages.** The SOUND and LEAVES pages repeat the lines that are theirs; if a list there differs, this one stands. These are the files more than one lane or page touches. Inside one lane its one agent keeps the queue, by each packet's After. A packet that is not ready when its turn comes (DIAL-14, which waits for the ledger, is the likely one) steps aside and takes the next free turn: the order may slip, but no two packets ever hold one file at once.

- `K/NatureState.cs`, `K/NatureStore.cs`, `K/CommandHostNature.cs`: R4-5, LOOK-25, DIAL-5, DIAL-6.
- `A/AudioDirector.cs`: SOUND-1, SOUND-2's line, DIAL-3, SOUND-16, SOUND-17, SOUND-18's first commit, SOUND-4. `A/SoundBank.cs`, SOUND-1's `A/SoundSet.cs` and `T/Audio/**`: SOUND-1, DIAL-3, SOUND-16, then down the SOUND page's table. `game/assets/audio/ecology/v1.json`: SOUND-16, SOUND-10.
- The settings model (`N/Ui/SettingsModel.cs`), `N/Ui/RoomHud.Settings.cs` and their test `T/Kernel/PlayHudTest.Settings.cs`: SOUND-2, DIAL-2, DIAL-7. A later row (the leaves' switch, music's visits, thunder, the output) registers from its owner's own file and opens neither. `N/Ui/RoomHud.Camp.cs`: SOUND-2, DIAL-10.
- `M/TitleScreen.cs`, `M/MenuBoot.cs` and the menu's tests under `T/Menu/**`: MENU-8, MENU-18, SOUND-5, DIAL-15, SOUND-3's door, SOUND-13's two lines, DIAL-7. `M/SettingsPage.cs`: SOUND-5, SOUND-3's foot line, DIAL-7. `M/MenuModel.cs`: DIAL-15, DIAL-7.
- `N/Look/LookDirector.cs`: LOOK-9, DIAL-6, DIAL-11. `N/Look/YearAir.cs`, `S/year_air.gdshader`: DIAL-14, DIAL-20.
- `S/painterly_land.gdshader`, all lane L's: LEAF-14, DIAL-14, LEAF-7, LEAF-4, LEAF-8, LEAF-9. LOOK-6 takes its turn between two of them, never beside one.
- The larder's files (`K/CommandHostFood*.cs`, `K/IslandRules.cs`, `N/Room/FoodView.cs`, the food rows of rules v6): FLORA-8, DIAL-8, DIAL-18, LEAF-17.
- `N/Seams/**`, `T/Seams/**`: DIAL-1 lands all four files. After that, each by the integrator's diff: a field the ground's word still lacks (LEAF-9, LEAF-10), DIAL-14 (the one constant that lets a moment say `snow`, in `WorldDials.cs`), DIAL-12 (`WorldDials.cs`), DIAL-13 (the wind stop in `Gust.cs`; the villages' places as mown ground in `FloraGround.cs`), and the SOUND page's tunings of the ladder.
- `N/Playtest/PlaytestRecorder.cs`: SOUND-1's mark, then DIAL-6's line. `run-room.ps1` and `N/RoomWorld.cs` are the integrator's own: one diff a packet, as each merges.

| Id | Title | Lane; after; needs | Owns | Fails before, passes after |
|---|---|---|---|---|
| DIAL-1 | The vocabulary, the moment, the seams, the almanac file | I; none, and before LOOK-9; headless | New `N/Seams/WorldDials.cs`, `Gust.cs`, `FloraGround.cs`, `StepLoudness.cs`; `T/Seams/**`; `game/assets/almanac/v1.json`; their runner lines | The tokens as constants. `StationOf`, `PartOf`, `Warmth`, the amounts, `Cover`, and `DialMoment` shown and ruled: pure, no scene, every input in the signature. Eight pinned dates give eight stations in both hemispheres; the spans equal the preset's holds and fades. Seven parts at three day lengths, and at a far-north midwinter and midsummer. One 360 s cycle at `leaf_fall` under `changeable` is `litter_dry` for at least a third of it; `freeze_up` at `dawn` is `frost`. No moment says `snow`, and no cover is `snow`, while the one constant DIAL-14 turns is off. The three sister seams are as the table of four has them. `Gust` gives the same value twice, stays within 0 to 1, moves down the wind and has lulls. `FloraGround.At` gives the default word from a hand-made list of trees, and a registered map's word in its place; its list of mown places starts empty, and a place registered in a test makes the word say `mown` there. `Cover` takes the word as an argument and reads no map. `StepLoudness` holds the SOUND page's ladder, column for column, and its four rules of reading: a test steps `dew` on `meadow`, `litter_dry` at the first step and the eleventh, and `grass` with no kind. The almanac file holds this page's station rows and cards as tokens, each with its form and its owner packet, and each kind to pick with the `slot` token that shows it; a blank owner fails. `WorldTime.cs` is not edited. Today none of this exists |
| DIAL-2 | The world's rows | U; DIAL-1, SOUND-2; eye | SOUND-2's `N/Ui/SettingsModel.cs`; `N/Ui/RoomHud.Settings.cs`; `N/RoomHud.cs:148-205` and its clock line; `T/Kernel/PlayHudTest.Settings.cs` (the HUD's test in the kernel's folder: a grant) | The Settings bullet above: two rows in SOUND-2's model, so every place it draws shows them. They pin the look through `SetClockAsync` (`N/Look/LookDirector.cs:504`; `SetClock` builds a grade on the main thread). The pick is two tokens in the profile's own `[dials]` section (`N/RoomHud.cs:22-23`), read as untrusted; a launch flag wins for its launch. Saves are byte-equal. As with the T key today, the pinned light still reaches the kernel's ground. Today: five switches (`PlayHudTest.Settings.cs:36-42`) and two keys |
| DIAL-3 | The moment in the ear | S; DIAL-1, SOUND-1, with SOUND-16; ear | `A/AudioSource.cs`, `A/AudioDirector.cs`, `A/SoundBank.cs`, SOUND-1's `A/SoundSet.cs`, `T/Audio/**`. By diff, the integrator's, in `N/RoomWorld.cs`: the holder of the shown moment and the line that hands `RowsNear` to the word's default, with whichever of DIAL-3 and LEAF-14 merges first | The snapshot carries the room's one shown moment, read from `RoomWorld`'s holder and never filled here, and the seam's gust at the listener. When a step is due it reads the ground's word (`FloraGround.At`), then the cover. The two `winter` tests (`A/AudioDirector.cs:322,419`) become rules of the sound set that read station, part and warmth; SOUND-16 then moves who sings into its ecology file. A step is routed to its cover, and DIAL-3 writes no recipe: the covers' voices are SOUND-16's, back to back with it, and no build ships one without the other. Frost ticks only on a spot the feet's own trail of the last 64 prints does not hold, until LEAF-10's trace map says where is trodden. Under a roof it asks for no cover. At a pinned `high_summer` `midday` in `fair` weather the cue log equals that of SOUND-1's bank `v1`. Today leaves sound as grass (`A/AudioSource.cs:187`) |
| DIAL-14 | Snow in the air and on the ground; the ring of thaw | L; DIAL-1, LEAF-14; pair, eye | New `N/Look/YearAir.cs`, `S/year_air.gdshader`, `T/Look/YearAir*`; a few ground lines of `S/painterly_land.gdshader`; one line of `N/Look/NatureLook.cs` (ruling 14) | Look only, from the shown moment. The falling leaves and the litter tint are LEAF-14's, by this page's 0.6 m rule; this packet adds the cold. It turns DIAL-1's one constant, by the integrator's diff, so a moment may say `snow` and SOUND-16's snow voices wake. In a `snow` fall, at most 48 flakes a third of a body across, and the rain is not drawn (`N/Look/NatureLook.cs:153-157`). A dusting on the meadow floor, where `Cover` then answers `snow`. No frost or snow within 0.25 m of the nearest lit campfire. At most two draws and 0.1 ms a frame, by a pair, once the ledger has room (ruling 5). `--look-year-air=off` adds no node. Today winter grass is frosted straw and no flake falls (`N/Look/LandSeason.cs:168-170`) |
| DIAL-15 | Picture days on the title page | U; DIAL-2, SOUND-5; eye | `M/TitleScreen.cs`, `M/MenuModel.cs`, `T/Menu/**` | A strip of the four first-build cards, each a picture drawn in code with a mark and no word. A tap writes DIAL-2's pick; the page's own sky shows it (`TitleSky.Read`) and the room opens on it. It fits the three test sizes; New game stays one click. A card whose owner is absent is not shown. Today the title page offers no day |
| DIAL-4 | CR-5 | I; DIAL-5's measure; headless | CR-5's files | An example with `x_dials` and a `world.set_dials` validates; a broken one fails; every file valid before stays valid; no tool on the surface; both hosts answer `unsupported_capability` until DIAL-6 |
| DIAL-5 | The weather takes a sky | Q; its measure first, then DIAL-4; a measure | `K/NatureState.cs`, `K/NatureStore.cs`, `T/Kernel/NatureTest.*`, `T/Kernel/NatureStepProbe.cs`. By the integrator's diff, in this commit, if the kernel's pinned weather line moves: the alignment test (`companion/tests/test_kernel_alignment.py:449-451`). The mock's `weather_at` (`companion/src/garage_game_companion/mock_nature.py:277`) stays today's law: the mock never takes a sky | First, on today's code: the probe times 16,000 resting cells (`K/NatureState.cs:375`) waking after two cycles away. The number fixes the list's length in CR-5. Then the four skies as laws beside today's, with no pack edit. With no `sky` every nature suite and save is byte-equal. A cell rested across a change equals one kept awake. A block with `sky` loads (refused today as "its fields", `K/NatureStore.cs:473-475`). Turning at each rain's end for an hour of counter leaves at most 915 s over 0.65. `CatchUp` and `CatchUpCell` under `settled` cost at most twice today's |
| DIAL-6 | `world.set_dials` | Q; DIAL-4, DIAL-5; headless | New `K/CommandHostDials.cs`, `T/Kernel/NatureTest.Dials.cs`; `K/CommandHost.cs`, `K/CommandHostRoomState.cs`, `K/CommandHostCamps.cs`, `K/CommandHostNature.cs`, `N/Saves/CommandHostTeams.cs`, `N/Saves/TeamBoot.cs`. By the integrator's diff: `NATURE_OPS` with the GDScript suite's line; `N/RoomWorld.cs`; `N/Look/LookDirector.cs`; one line of `N/Playtest/PlaytestRecorder.cs` | Each bullet of the command above, with a test. Save and reload answer a turn; before the `NATURE_OPS` diff the reload fails `save_receipt_invalid`. Cast, turn, `room.undo`: the cast is undone and the dial stands. In Challenge year and sky are refused away from a campfire and taken at one that is out. The counter does not move. `TeamBoot` reads `--dial-year=`, `--dial-sky=` and `--dial-hour=` and calls `BootDials`; a bad value is a notice and the default. Save, load, export and rebuild keep the dials; at first stops no record is saved. The saved hour sits under the pin (`N/Look/LookDirector.cs:552-553`). The play record says the dials at launch and at each turn (its kinds are open, `contracts/README.md:385`) |
| DIAL-7 | The saved doors | U, I; DIAL-6, DIAL-15, SOUND-5; eye | `M/MenuModel.cs`, `M/MenuBoot.cs`, `M/MenuIslands.cs`, `M/TitleScreen.cs`, SOUND-5's `M/SettingsPage.cs`, `T/Menu/**`; DIAL-2's rows in the settings model and `N/Ui/RoomHud.Settings.cs`, with their test; `run-room.ps1`, by the integrator | A card now sets a new game's three dials. Continue shows its game's (`GameWords`) and the page's sky reads them. Three saved rows, year, hour and sky, sit under a caption that says they are this game's (the panel's says this computer's, `N/Ui/RoomHud.Settings.cs:47`). On SOUND-5's page before play they show the chosen game's dials and do not turn them; for a new game they hold a card's choice. DIAL-6's three flags pass the menu's three argument builders (`TestBootChoices`) and gain launcher switches; `-Hour` and `-Date` stay R4-6's look pins. DIAL-2's rows now send the command and the profile pick is retired. Away from a campfire in Challenge a year or sky row only pins the look, and says so |
| DIAL-10 | The fire's stones | L, U; DIAL-6, DIAL-9; GPU, eye, likeness | New `N/Look/DialStones.cs`; `N/Look/CampfireLook.cs`; `N/Ui/RoomHud.Camp*.cs`; a held key in `N/Ui/PlayerControls.cs` | The stones bullet above. Eight flat stones a camp, one draw, at most 2 cm high, no collider, never carved and never turned. The look and hold works standing, lit or out: sitting needs a lit fire and opens the camp's screen (`K/CommandHostCamps.cs:541-549`, `N/Ui/RoomHud.Camp.cs:52-70`). Letting go early does nothing. The smoke reads the sky. The turn is the plain 3 s cross-fade. Once LEAF-8 is in, a turn at the stones hands the fire's place to `plant_year` as the front's centre; lane L's one agent wires that line with whichever of the two lands second, and until then the front runs from the feet. At most 0.1 ms a frame on the main thread |
| DIAL-17 | The Gubble's part | L, P; DIAL-6, BODY-12; eye, likeness | New `N/Look/GubbleWear.cs`; a few lines of `N/CompanionAvatar.cs` (ruling 14) | The game's own follow, as the spotter is: no command. A thing of the shown station clings to the Gubble, so a child reads the dial off it. It looks up 30 s before a fall. In Challenge, when a year or sky row is touched away from a fire, it lights the way to the nearest one. Its commands and its surface are unchanged |
| DIAL-8 | The calendar's mechanism | I, Q; DIAL-6, BODY-1, FLORA-8; headless | CR-6, its first commit; `food.calendar` in `game/rules/storybook_wild/v6.json` (lane I's); `K/IslandRules.cs`, `K/CommandHostFoodLayout.cs`, `K/CommandHostFood.cs`; new `T/Kernel/FoodTest.Calendar.cs`, `T/Seams/LarderAndLeaf*` | Proved on three kinds: apple, berries, and the nut the brief's FLORA-8 brings, which goes first on these files. A hundred turns pick no unit more. A thinned station hides spots and a turn back shows them. The loader refuses two things: a spot renamed to a kind that heals otherwise; a station with under half of today's spots live. It reads no flora file. Picture and larder are held together by one test, the only such check on any page. Where `food.calendar` makes a kind live on a species at a station, that species' flora row shows the kind's `slot` there, as the almanac file names it. And a row whose `slot` shows `fruit` at a station has a live kind or a find there. A species that either file does not hold yet is skipped. With no calendar, today's bearing. A station turning at a pinned midnight refreshes the food with no revision. `nature.describe` names the kinds live now |
| DIAL-18 | New things to pick | P, U, I; DIAL-8, FLORA-21; eye | `N/Room/FoodView.cs` (five looks today, `:43`); the inventory's words; rows in v6 | The table's other kinds, beyond the grain, down and haws LEAF-17 takes after it: each an inventory kind with a placeholder word, a drawn unit and a HUD line, each standing in for a kind that heals the same. A find gets no row. At a turn the nearest live windfall is seen to land on its own spot. At `harvest` one lands now and then on the tree's own time: never within 0.3 m of a body, never after a shake. Uses wait for rules v7: wave 6's round, which the LEAVES page marks K |
| DIAL-9 | No dial opens a way | P; DIAL-6; headless | New `T/Miniature/DialGates*` | Boot a room and walk every station, part, sky and view stop: the world layer's colliders, the footing grid and the body's constants hash equal. A made-up dial that adds a collider fails. The generator knows no dial, so its share is a clause asked of BODY-15 and BODY-17 |
| DIAL-16 | The year's animals | N; DIAL-1; GPU, eye | New `N/Look/Fauna/YearLife.cs`, `T/Look/Fauna/**` | Signs first: tracks, a sleeper's breath, gossamer, a hoard. Then scenery in one draw, moved in the shader as the shoal is: frogs walking to water, froglets leaving, hoarders burying, the first bee. Each by station and part from the almanac file. Scenery reads the hush as the singers do, from two seams and no audio file: the brief's noise ring times `StepLoudness`'s factor. Inside that reach it goes still or slips away, and comes back as the singers do; until BODY-11's ring is in its seam, scenery reads no hush. Nothing larger than the body within 1 m; none chases; none has a collider. Today the look has fish, pond life and one hamlet (`N/Look/Fauna/`, `N/Look/Charm/`) |
| DIAL-19 | The almanac's pages | U; DIAL-6, MENU-14; eye | `N/Ui/JournalPages.cs` | A page a station in the journal. It gains a sketch each time something of that station is met. No slots, no counts, no timetable; names only from a shipped table. What was met is kept as MENU-14 keeps a find; a new journal kind, if one is needed, joins a contract round |
| DIAL-20 | The gust at a turn | L; DIAL-10, DIAL-14, LEAF-8; pair, eye, likeness | `N/Look/YearAir.cs`, `S/year_air.gdshader` | A ragged gust on the ground, riding LEAF-8's front down the kernel's wind and lifting the station's own thing: leaves, petals, flakes, seed fluff. No dome, wall, rim or colour; it cannot be paused or held. Ships only on the integrator's ruling |
| DIAL-11 | A day a sitting | L; LOOK-9, DIAL-6, its round; eye | `N/Look/LookGlobals.cs` by turns with LOOK-9; `N/Look/LookDirector.cs` | `turning`: seven parts of 371 s on the saved weather counter, a day in about 43 minutes. 371 shares no factor with a sky's 360 s, so rain does not fall at the same sun every day. A pause stops it and a reload resumes it; the kernel's day is unmoved |
| DIAL-12 | The year walks | Q, I; DIAL-8, its round; headless | `N/Seams/WorldDials.cs`, `K/CommandHostDials.cs` | `walking`: one station on at the first rest of a real day's play, never while the game is closed, so no station passes unseen |
| DIAL-13 | Wind, moon, the folk's year | L, N; LOOK-11, VILLAGE-3, LEAF-7, SOUND-8, its round; eye | By turns: LOOK-11's `N/Look/NightSky.cs`, VILLAGE-3's `N/Look/Folk/FolkDay.cs` | The wind stop scales `Gust` (the SOUND page's seam, which the LEAVES page draws) and the wind's sound only; fire and drift do not move. Moon and folk's day reach LOOK-9's globals. Each station's folk work is a prop and a pose at each village, one draw. It registers the villages' places with `N/Seams/FloraGround.cs` as mown ground, by the integrator's diff. LEAF-8's blades read that list and stand as stubble there from `flush` on: the hay. Either may land first, since an empty list mows nothing. Under a held station the droves and mending come round by game day (season days today, `docs/runs/RUN-4-VILLAGES.md:92`) |

## What ships first

One order for the three pages. A build ships what is ready; nothing in it waits for another page's packet unless an After says so.

1. **Build one, first aid and first leaves.** SOUND-1 and SOUND-2 (S, U). LEAF-15, LEAF-6 and LEAF-2 (L, P): leaves near the eye in every installed room. Beside them, seen by nobody: DIAL-1 with the four seam files (I), DIAL-5's measure (Q) and LEAF-1's five trees in the maker (F), which need nothing of today's rooms.
2. **Build two, the dials' first build.** DIAL-3 with SOUND-16 (S). DIAL-2, SOUND-5, then DIAL-15 (U). LEAF-13 and LEAF-14 (L): the kit dressed and the year's first forms, with LEAF-3 headless between their pairs. Then SOUND-17's first visit, and DIAL-14 when the ledger has room. No contract, no kernel edit, nothing in a save.
3. **The saved dial,** beside build two in lanes I and Q: DIAL-4, 5 and 6; then DIAL-9, 7, 10 and 17. Beside them: LEAF-4 once LOOK-2 lands; FLORA-2 and LEAF-5 in the next generator batch, behind LEAF-1; the Listening Room (SOUND-3) and the ear's numbers (SOUND-4).
4. **The larder:** FLORA-8, DIAL-8, DIAL-18, then LEAF-17. Then the year fills in: LEAF-8, 9 and 10 on every plant; DIAL-16, 19 and 20; the SOUND page's families down its table.

His first minute in build two: a strip of picture days on the title page. He taps the gold one. Leaves are coming down, a tint of them lies under the trees, and they sound like it. Under trees his steps are dry for about half of each six minutes and hushed after rain. Nothing is louder than the build before. In Settings the same day shows on two rows, and T steps them.

What build two lacks, said plainly. There is no weather row until the saved dial. The turns are half-blends of two seasons until LEAF-8, and kit trees (today's balls on posts, dressed by LEAF-13) keep half their leaves in winter. The falling leaves are LEAF-14's first forms until LEAF-8 and LEAF-9. No snow falls or is heard until DIAL-14. The eye shows no gust crossing the land until LEAF-4 and LEAF-7, though the ear already has one. No animal moves.

## Where the proposals disagreed, and the ruling

1. **Stations.** Eight, on the look's own holds and fades (the naturalist proposal), not new day ranges (the systems proposal): no new number, and the name fits the picture.
2. **Parts.** Seven: the T key's sunset and dusk are one `dusk`, and its night splits into `evening` and `night`.
3. **Falls.** The kernel's three words, not five kinds. Grow's bonus and the campfire rule both cut at 0.65, so a drizzle that grows gardens would change them.
4. **A wait until dawn** (the research's pick) is refused: on the real clock the next dawn is tomorrow. One larder already makes flicking pointless.
5. **Leaf heaps and drifts as soft landings** (the player's-seat proposal): a softer landing by season is a body number moved by a dial. They are drawn only on footing that is already soft, and are first to try as real footing once DIAL-9 is green. Ice floors stay set aside by the worldbook (`docs/runs/RUN-4-WORLDBOOK.md:106`).
6. **A mast year, a moth year, a drought** (the naturalist proposal) are set aside: a year of plenty multiplies the larder. **The Gubble's own rain** (the systems proposal, a priced cast) waits for wave 6 and a likeness check.
7. **Day length.** The look-only `turning` day, not `x_clock.day_scale`: D9's answer stands for the kernel's clock, and a changed scale would regrow every seedling at once (`K/CommandHostGrowKinds.cs:297-298`).
8. **Food by hour or weather** and **fuel by season** wait: the kernel knows no held hour, and fire's reach must not move with a dial.
9. **Two seasons side by side,** a ring that holds the new season inside it, is refused for likeness and for cost: the land's season is one set of globals (`N/Look/LandSeason.cs:195-200`). The land cross-fades whole; only the plants take a passing front, for 3 s.

## Where the three pages disagreed, and the ruling

Ruled on 25 November, when the dials, sound and leaves pages were read whole against each other. Each is mended in place on the page named.

1. **One vocabulary.** The sound page kept its own lists of ground kinds and places, and the leaves page owned its keys. All are tokens in this page's table now. The leaves page owns the rows behind the grass, plant and litter keys.
2. **Four seam files, landed with DIAL-1.** `Gust.cs` was new in SOUND-8 and again in LEAF-7, `FloraGround.cs` new in LEAF-3, `StepLoudness.cs` in SOUND-7. One packet creates all four; the rest read them.
3. **The ground's word has one shape and one call,** `FloraGround.At(x, z, moment)`; the sound page had called `KindAt`. Its default, the 0.6 m rule, serves the ear (DIAL-3) and the eye (LEAF-14) from one function.
4. **Trodden is the trace map,** not the press map, which only parts blades. Until LEAF-10 the feet keep their own trail of 64 prints; "one step in four" goes.
5. **A needle bed is ground, not cover:** `needles` under a `bare` cover. **A lane's floor is a Ground token,** and the lanes of `tussock`, `sedge` and `rush` are sodden, so "squelch between" has a builder. Leaf litter in a lane is the litter map's own, so the eye paints what the ear hears.
6. **Phase rows live with the leaves page** and its files. Each page had named the other as the home. This page's Plants table is a copy for reading, and the draft for the bushes.
7. **Gathering in one order:** FLORA-8 (the brief's nut), DIAL-8, DIAL-18, LEAF-17. The leaves page had widened FLORA-8 with kinds picked by station, which needs DIAL-8's calendar, while DIAL-8 waited on FLORA-8: a ring. The widening is LEAF-17.
8. **One check of picture against larder:** DIAL-8's test on the `slot`. The kernel's loader reads no flora file, and LEAF-8 keeps no check of its own.
9. **Finds:** gall, resin, willow down, husks and keys are drawn and sketched, never pocketed, as the leaves page has them. This page had listed three of them to pick.
10. **What may land on a body:** a leaf, a petal, a key or a flake, drawn only. A drop, a nut, a cone or a fruit never lands within 0.3 m. This page had barred the leaf too.
11. **A falling nut is the food view's windfall** (DIAL-18), on a counted spot; the leaves page's acts hold no nut. Spinning keys stay a maple's act.
12. **Signs before a fall are one ladder, keyed to the fall:** the shadow 60 s ahead, the birds 50 s, the vent, the frogs and the Gubble 30 s. This page had the vent both 3 s after a turn and 30 s before a fall.
13. **The Turning by ear:** near layers cross in 3 s with the land, far air by 10 s. It spreads with the plants' front, whose start ruling 27 gives; the sound page's "ring" goes.
14. **No snow by ear before snow by eye:** no moment says `snow` until DIAL-14 draws it. The land-wide hush is SOUND-16's, asleep until then.
15. **Cards:** nine, each with a token and a signature by ear. Blossom morning's is the dawn chorus, since bees fly later. Frog night waits for its saved sky, so the first build shows four.
16. **Hay is cut round villages** (DIAL-13 registers their places, by ruling 28's path), not round camps.
17. **Heaps:** a drift of leaves in the lee is at most knee deep and promises no landing. A deeper heap is drawn only on footing that is already soft.
18. **The year by country** is LEAF-8's, from its ground map, not an ask. The plants' front is never drawn as a line and cannot be held.
19. **The menu:** one model, and each owner registers its rows, so a later row opens no file of lane U's. On the title files the order is SOUND-5, DIAL-15, SOUND-3's door, SOUND-13, DIAL-7. Before play the saved rows show and do not turn.
20. **A raindrop's size** is the worldbook's: the great drop is a third of the body, where the sound page had a fifth.

A second reading the same day, seam by seam, found what the first had left. Each is mended in place on the page named.

21. **One holder of the shown moment in a room.** The ear and the eye were each to fill a moment of their own, and nothing held them to one. `RoomWorld` holds it; DIAL-3 and LEAF-14 read it; the integrator lands the holder with the line that feeds the word's default.
22. **The ladder has four rules of reading.** Its table had no answer for `dew`, `wet` or `frost`, for a ground with no column, or for "the first steps". A cover's column wins; else the surface's; `dew` and `wet` take 4 dB and leave 0.7 of the factor; the function returns a first and a settled level, and `speed` adds the run step only (the SOUND page).
23. **`grass` with no kind named steps as `meadow`:** the seam's default, a look with its kinds off and a kind with no row then sound alike.
24. **A needle bed is ground and no cover.** All three pages gave it a `bare` cover, which rain and frost would break: the cover there is whatever else holds.
25. **No step creaks on snow before a flake falls.** `snow01` is 0 until DIAL-14's constant is on. The amounts table had let today's snow dabs on rock answer `snow` while SOUND-16's creak slept.
26. **Under a roof no cover is asked for.** `Cover` knows no roof; the caller does.
27. **The front starts at the feet,** and at the fire only for a turn made at its stones. All three pages had "from the fire once DIAL-10 lands", which a turn in Settings would break. One setter, wired by lane L with whichever of DIAL-10 and LEAF-8 lands second.
28. **Mown ground is a list on the ground's seam.** DIAL-13 "handed" the villages' places to LEAF-8 by no path, and neither waited for the other. DIAL-13 fills the list and LEAF-8 reads it; empty, it mows nothing.
29. **Dew is never one bead to catch.** This page had a bead that rolls off at a touch; the leaves page bars it, for likeness.
30. **A drift bursts, not a heap.** The sound and leaves pages' toys still burst a heap, against ruling 17.
31. **A drip's note is by what it lands on.** The sound page had it change "when caught", against the 0.3 m rule.
32. **Mist by ear is SOUND-8's.** This page's weather table named it and the sound page had no line.
33. **A named day's voice is owed by the packet that makes it,** and its card waits for that packet too: `nut_fall` for SOUND-19, `stone_day` for SOUND-14. SOUND-19 had owed all eight. A scenario shipped before the Listening Room is data its packet's test reads.
34. **Heights and straw are the leaves page's rows.** The sound page had oat-grass chest high; it stands to the head and over. This page's copy had its rattle at 5 to 7; it is straw from 6 to 1.
35. **The leaves page's file gains what the seams need:** a `head` slot for grain and down, the finds `keys` and `old_cones`, and one lane token a row (`fescue` is `stone`). DIAL-8's test reads the `slot`, as the leaves page already said.
36. **One editor at a time, and a held packet steps aside.** A strict order would have stalled the land shader behind DIAL-14's wait for the ledger. `A/SoundSet.cs`, the menu's tests, SOUND-13's two lines on the title files, DIAL-14's constant and DIAL-13's list join their lines.
37. **LEAF-1 starts on the first day** in lane F, as the leaves page has it; this page had put it in the generator batch.
38. **The leaves' switch keeps its own pick,** in a section of its own, since DIAL-7 retires the dials' first pick. No switch parts the eye from the ear.
39. **Scenery reads the hush from two seams.** The honest rule promised that scenery reads it, and DIAL-16 had no line for it.
40. **Words.** `forest_noon` is `forest_midday`. `slot` is a place here and a key's name on the leaves page. The singers' kinds are the sound page's own rows, in no group of the table.

## Asks of the brief and of the other pages

- **The brief.** Ruling 17 reads "from DIAL-1's shown moment, through LOOK-9's globals", and with D9 gains a line: the real clock is now a dial's first stop. Ruling 14's list gains the files above. The contract rounds (`docs/runs/RUN-4.md:218`) gain CR-5 and CR-6. Lane I gains `game/assets/almanac/**` and the four seam files under `N/Seams/`. FLORA-8 stays as the brief wrote it, and LEAF-17 is new behind it.
- **The SOUND page.** Taken from it: `FallIn(seconds)`; its `Gust` and `StepLoudness` seams; its hush factors and ladder; its ground kinds and places as tokens; thunder as `off` and `far`; the warning as SOUND-16's; frogs by day at `thaw`. Met there on 25 November: the ten places; each card's dials and signature in its named-day scenarios; the signs' ladder; the snow hush; the hide. At the second reading: the ladder's four rules; mist by ear; each named day's voice owed by the packet that makes it; the room's one moment read, not filled.
- **The LEAVES page.** Taken from it: each kind's own litter row wins over this page's default; its ten grasses and their keys; `held` for standing straw; nuts that fall by themselves; LEAF-14 for the first build's leaves; its finds. Met there on 25 November: a front that is never drawn as a line and cannot be held; half a station early at water and late on summits, in LEAF-8, provisional; lane floors as Ground tokens; LEAF-17. At the second reading: `mown` in the ground's word; the front's one setter; `head`, `keys` and `old_cones` in its file; one lane token a row; its switch's own pick.
- **The look page.** A cloud's shadow from the door bay 60 s before a fall (LOOK-5), and the night floor: neither has a packet here.
- **BODY-15, BODY-17.** Drift and crossings proved under the least helpful sky.

## Provisional, for him to feel in play

- In Challenge the year and the sky are kept only at a campfire; Creative turns anywhere. The build takes that.
- A new game's sky is `changeable`, today's bytes, until he has played `settled`; the saved cards already carry `settled`.
- A day in one sitting, for a player who meets only night on the real clock, is built as the hour's later stop, not asked.
- Eight stations, their spans and held days; seven parts and their edges; which stops a row's face shows.
- Every warmth, amount and holding level; 0.6 m to a crown; 75 s of wet ground; 90 s to dew; 21 C for heat.
- The four skies' periods and levels; 60 s of signs after a turn; four entries in the sky list.
- A one-second hold at a stone; 3 s to cross-fade; no drop, nut, cone or fruit landing within 0.3 m; nothing large within 1 m.
- The signs' 60 s, 50 s and 30 s before a fall; a lull under 0.15 of a gust; which lanes are sodden.
- A step on dew or wet ground 4 dB softer, with 0.7 of its hush; 3 m of stubble round a village.
- Half of today's spots at the leanest station; which kinds stand in for which.

## Likeness

- **A year the player holds.** Alike: a life game on the real clock, whose players shift the machine's clock to visit a season; farming games whose seasons change the crops. Ours: an honest dial inside the game; one larder, so a jump never refills; eight stations from field records; the companion can never turn it. What changes: named days stay nature's days, never holidays or events with a reward to miss. There is no timetable of months and hours for a creature, no reward or penalty for a turn, and no place in a difficulty menu. Fallback from nature: the real calendar alone, with the preview clock for looking. Asked of the integrator: a genre's common practice, no hold on DIAL-2, 7 or 8.
- **Picture days and an eight-part year.** Alike: presets in place of sliders are everywhere; old almanacs count small seasons by what frogs and frost do, and some herding peoples count eight. Ours: each card is a real day of this land, shown only when the world can keep it. Fallback: the four seasons' plain names.
- **The fire's stones.** Alike: games where one act at a fixed place turns the season, by a tool or by stones turned to a season's face; a fire that offers morning, noon or night. Ours: every campfire from the first minute, free, with no tool and no power to earn. By rule a turn opens and shuts no way, the opposite of that kin's purpose. What changes: no rod, staff or pedestal; stones marked by what lies on them, never carved, never rotated; no list of hours and no wheel of icons; DIAL-9 is green first. Fallback: a plain ring of fire stones; the year is read from the nearest twig and the smoke, and turned in Settings.
- **The Turning.** Alike: a game whose signature is a sphere with another season inside it, its edge lying on the ground; land that blooms outward from one act. Ours: a passage, not a state. The land cross-fades whole, the plants follow within 3 s and a gust crosses the ground. What changes: never a dome, sphere, wall or rim; no colour for a season; no line is drawn; it cannot be paused or held; each tree answers late by its own stagger. Fallback: how weather arrives, a cloud's shadow and a gust down the wind. The highest risk on this page: DIAL-20 waits for the ruling and DIAL-10 ships without it.
- **A companion that wears the season.** Alike: companions and pets dressed for the weather. Ours: no dress and no command; a thing of the station clings to it, and it is the dial's read-out. Fallback: the land's own signs.
- **A windfall seen at a turn.** Alike: life games hand out a gift when a season changes. Ours: no gift; a fruit is seen to land on a spot already counted, so a turn mints nothing. Fallback: windfalls lie where they lie.
- **The room's openings as the dials' faces.** Alike: instruments that call weather. Ours: the room's own window, vent, door and lamp, read first and turned only if play proves it. Fallback: a wetted finger.
- **The almanac's pages.** Alike: collection books that list what to catch and when. Ours: a field book of what was met, with no slots and no schedule. Fallback: the journal's plain notes.

## Research notes

- Choice helps motivation most when the choices are few: the effect was strongest with two to four choices in a row, and with no reward after. https://scholars.duke.edu/publication/716954
- Steps on dry litter, soil and moist ground: about 6 dB less on moist ground, by the abstract. A larger figure for dry litter is from the paper's body and was not checked, so this page quotes the SOUND page's compressed ladder. https://journals.biologists.com/jeb/article/211/17/2799/17731/Cues-for-acoustic-detection-of-prey-insect

## Not verified

- Nothing was built, run, drawn, heard or timed.
- **The code.** The editor opened every code line cited, at the commit above. Two menu files changed while this was written (`M/MenuBoot.cs`, `M/TitleScreen.cs`), so they are cited by function name. Not opened: the generator, `K/IslandRules.cs`, the sound bank's recipes, the folk's files. LOOK-9's `LookGlobals.cs` is on no run4 branch yet.
- **The research.** The editor opened no research source. Each fact is as the researchers marked it, and several plant dates are from memory, the picks of maple, beech and alder among them.
- **Likeness.** A review of the fire's stones, the Turning and the held year was written on 25 November. The integrator has not ruled.
- **The settling's cost** when a fifth turn drops the oldest sky: DIAL-5's measure, before CR-5 fixes the list's length.
- **Whether an empty undo step fits the walk back** as read (`K/NatureState.cs:539-552`): DIAL-6's first test.
- **Whether the weather counter stands still in a pause** (by reading, yes: `N/Ui/RoomHud.Pause.cs:128-130`). A kernel test settles it; DIAL-11 leans on it.
- **Whether flakes and a dusting fit the frame,** which is over budget today (ruling 5): DIAL-14's pair.
- **The sister pages.** All three were read whole and mended together on 25 November. That pass was of the design's text. For it the editor opened only `N/Room/RoomData.Island.cs:34`, `N/Room/PlantCopies.cs:193`, the two audio lines DIAL-3 names, the worldbook's rain line and the brief's rows for FLORA-8 and its rulings. The second reading was of the text again. For it the editor opened, at `c595f407`, the lines the holder cites (`N/RoomWorld.cs:38`, `N/Look/LookDirector.cs:51`, `K/CommandHostNature.cs:198`), the snapshot's `Outdoors` (`A/AudioSource.cs:29-30`), the profile's path (`N/RoomHud.cs:22-23`), the pause menu's door to the settings panel and the rules contract's inventory kind, which is a token and no closed list. No other line cited on the three pages was opened again, and the branch has moved since each page's commit.
- **Whether `Cover` can tell where the look lays snow.** It is handed the moment and the word, and neither says rock from meadow or which way a face looks. DIAL-14 draws the dusting and says, in its own packet, what the word must carry.
- **The spruce's dry tent.** The leaves page gives a spruce a dry floor in rain, and no field of the ground's word says a spot is sheltered, so the ear would step on wet needles there. Unsettled: one more field of the word, or a tent that is only a sight.
- **Whether a kept game's dials should turn before play.** As ruled they only show there, since a turn is a command. He asked for the main menu's settings: play says whether showing is enough.
- **Whether a child finds the stones with no word:** only the kids settle it.
- **Whether any gate today leans on rain or wind:** DIAL-9's first run says.
