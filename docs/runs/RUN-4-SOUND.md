# Run 4, sound: a world that rests the ear and answers the dial

**Status: a design for the founder, 24 November 2026, edited from two proposals and mended on 25 November after three critiques. Nothing is built or heard.** Code read at `run4/integration`, `a5548c57`; the dials page reads `dd0a7561`, and between the two only the title page's file and the menu's tests changed. `N/` is `game/scripts/native/`, `A/` is `N/Audio/`, `M/` is `N/Ui/Menu/`, `K/` is `N/Kernel/`, `T/` is `game/tests/native/`, `D/` is `game/assets/audio/`. **(his)** is what he asked; the rest is **beyond what was asked**, provisional until he hears it in play. Every token here (station, part, fall, state, sky, cover, ground, place, grass, plant, day card) is in `RUN-4-DIALS.md`'s one vocabulary table; the rows behind the grass and plant keys are `RUN-4-LEAVES.md`'s. The three pages were made one design on 25 November, and read whole a second time, seam by seam, the same day (the dials page's rulings 21 to 40): the four seam files, the build order and the one-editor lists stand on the dials page, and this page repeats only its own lines of them. Names are placeholders: the family writes every word a player reads. Lanes and needs are the brief's (`docs/runs/RUN-4.md:48-50`). Where this page differs from the brief, the brief wins.

**Words.** A *bed* is a sound that lies under the others (wind, far rain). A *bus* is a named channel of the mix with its own volume. To *duck* is to turn one bus down while another speaks. The *limiter* is the last guard on the loudest bus. A *reverb* is the ring a space adds. A *one-pole* filter cuts 6 dB an octave, a *two-pole* 12. A *pool* is the variants of one sound; a *bag* deals them so none comes twice running. A *cue* is one decision to play a sound. An *emitter* is a point in the land a sound plays from. The *worker* is a background thread. *dBFS* counts down from the loudest a file can hold. *LUFS* is loudness as broadcasters measure it; a step of 1 LU is 1 dB. *A-weighted* means weighed as the ear hears. *True peak* is the highest point of the wave; *rms* is the average level; *crest* is peak over rms. The *lead* is the session that leads the build: it cannot hear, so it judges by numbers.

The sounds were "charming at first" and then "genuinely hard to listen to": the fault is structure, not timbre. The cure is another machine, in which every sound has a cause, a place and a rest. But less of what tired him is only half an answer. The dial he asked for arrives in the same builds, so the ear must answer Spring and Fall as the eye does, from the first build that has a dial. He vets by playing: no sound waits on his approval, and the old voice is always one switch away.

## What the founder gets

1. **The very next build** (SOUND-1, 2; build one of the dials page's order, with the first leaves): tonight's sounds made bearable, with new small life: a far call at night, a drip, an echo for the call. In Settings, in play, four sliders: Everything, the World (his "ambient"), Actions, Music. The Music row says "not yet" for one build. F9 names the sounds that were playing.
2. **The build that brings the dial** (build two: SOUND-16 with DIAL-3; then SOUND-5 and 17): the same rows before play, his "main menu's settings UI". Pick `leaf_fall`, and his feet crackle on dry leaves and go quiet on wet ones. Pick `high_spring` at `dawn`, and birds sing in phrases. Frost ticks, frogs start before rain, crickets slow with the cold. Snow creaks and hushes the land once the eye has snow (DIAL-14). Music pays its first visit when rain ends.
3. **Then, family by family,** each the default the day its numbers pass. Feet on every ground and grass; the body's own moves; rain as drops; singers that bolt and return; places that answer; toys that answer a touch.
4. **Later:** the room ringing through its land; the Dive and the Squint by ear; music's five voices; the live voice.
5. **Always:** made in code. No recording ships, and nothing is fetched without his yes. A Listening Room (beyond) is there when he wants to tune by ear; nothing waits for him to use it.

## Why tonight's sound tired him

Measured by the round's readers on a port of tonight's 28 recipes, equal to the engine's bank sample for sample.

1. **Feet.** Two near-identical samples a ground, in strict turn (`A/SoundBank.cs:92-100`, `A/AudioDirector.cs:410`), spaced by distance (`:32-33`). That is 2.9 steps a second walking and 5.6 running, the run 2 dB louder (`:403`). In a night run on grass, steps are 71% of the A-weighted energy.
2. **Hiss.** Only one-pole filters, at 22,050 Hz (`A/Dsp.cs:11,33-48`). Rain holds 80% of its energy above 2 kHz, its level flat within 0.6 dB, 10 dB over the calm night (`A/SoundBank.cs:157-179`).
3. **Beeps.** Insects are three bare tones near 3 kHz on fixed periods (`A/SoundBank.cs:182-202`), where the ear is most tender. Birds are sine sweeps there (`:239-271`). Every hover and press is a pure tone (`:88-89`, `A/AudioDirector.cs:294-295`).
4. **Loops** of 6 to 12 s (`A/SoundBank.cs:119,138,159,184,207`). The ear finds a noise loop up to 20 s long and keeps it for weeks. Rain came round 90 times in nine minutes.
5. **No rest.** Outdoors the soft wind never falls under 0.34 of its full level (`A/AudioDirector.cs:326`). The one occasional sound is a bird by day (`:416-420`), and the build was in its night.
6. **One place.** Every bed and step is mono, the same in both ears (`A/AudioDirector.cs:146,158,204`). No bus has a reverb. "Outdoors" is one value a room (`A/AudioSource.cs:90`).
7. **Levels.** The whole sits near -34 dBFS rms, so the machine is turned up. A step's peak then stands 18 to 19 dB over the bed's rms.
8. **No control.** Six buses and a squared curve exist (`A/AudioSettings.cs:20-24,57`), and nothing outside `A/` calls `Set` or `Save` (`:36,72`). Two chimes are on no bus (`N/Ui/RoomHud.Camp.cs:171-176`, `N/Look/LookDirector.GlowGrowth.cs:91`). The one control is a chime slider on the Magic page, saved by a command (`N/Ui/RoomHud.MagicGlow.cs:42-51`).
9. **A tiny cast.** Only the player's own acts sound (`A/AudioDirector.cs:235`). The gait's true footfalls go unheard (`N/Characters/Gaits/GaitLayer.cs:76,214-215`).
10. **Kept:** the limiter (`A/AudioBuses.cs:26`); the director's shape, plain values in and decisions out (`A/AudioSource.cs:19-48`); sound made in code; ground read from data (`:162-190`); the log of what was played (`A/AudioDirector.cs:94`).

## What the world sounds like

**The palette at one centimetre.** The player keeps a person's ears; every source keeps its true size.

- **The ground speaks more than the foot.** A step is the cover giving way, never a shoe.
- **Rain is things.** Most drops are small ticks; one in thirty is the great drop the eye sees, a third of the body across, as the worldbook has it (`docs/runs/RUN-4-WORLDBOOK.md:51`). On a leaf overhead a drop is a drum, on water a bell, on soil a thud, on `litter_dry` a snap. Only far rain is a hiss. A drop sounds as water on a thing, never a whistle and a blast, and rain never harms.
- **Wind belongs to tops.** One gust function, shared with the eye, reaches each stand of plants: a gust is heard crossing the land where it is seen crossing. Needles sing, broad leaves patter, rushes click, reeds hiss. A `held` oak rattles; a `bare` twig whistles low and its limbs knock. Winter has a wind of its own.
- **Huge and low.** *The air floor:* the room's own stillness, low brown noise (noise weighted to the bass), 12 LU under the meadow. *Thunder,* off to start: a long low roll from the door bay, never a clap.
- **A place colours a sound.** A cliff foot answers once, after twice its distance over 343 m/s. A slot flutters. A hall under a mesa makes rain a drum on its roof. A forest eats the highs and keeps its wind overhead. A lake at night carries far voices. The rim desert is the quietest place. Under water the world is dull and one's own bubbles are near.

**The room rings through (beyond).** The brief's theme is a land that is plainly the room. Its sound should be too.

- **Its own stillness.** The air floor carries two or three faint notes at the room's own lengths, worked out from the outline the room record holds. Each room rests differently.
- **Each landform keeps a faint answer of what it is made of.** The table has one row a material role. The manifest already holds the role, and the feet already read it (`A/AudioSource.cs:162-190`). Wood is a hollow thud, metal a long ring, glass a clear high note, fabric a swallowed pat, stone and plaster a dead knock. A landing, a knock or the call wakes it, at least 18 dB under the act. Roles only: nothing is inferred about the real thing.
- **The Lamp-moon's breath.** At night, a soft breath on one low note and its next two partials, so a small speaker still gives the pitch. The pitch is data, 100 to 130 Hz to start, and claims nothing about a real fitting. It comes a few seconds at a time, brighter and nearer under the lamp, never louder, and is off under calm ears: home by ear.
- **The Dive by ear.** As the eye falls, no pitch moves. The room's stillness gives way to the land: one far shimmer becomes single callers, a hiss becomes separate leaves.
- **The Squint by ear.** The land hushes, the thing's own ring is heard once, and the land returns.
- The home wind comes through the door bay, as the worldbook has it (`docs/runs/RUN-4-WORLDBOOK.md:50`). Thunder rolls in the same way.

**Beds built of events.** No bed is a loop. A layer is a pool of small events over far air, with a rate: base x part x station x warmth x fall x gust x place x hush. Each factor is a few keys of data, so a dial moves numbers of the same recipes. A state is a factor too: in `mist` the far air and the far voices fall away and only near sounds are left, as the dials page's weather table has it (SOUND-8). **A lull has a cause.** The gust function has troughs: when one crosses the player's stand, the leaves fall still. Singers rest by their own habits, out of step. For a few seconds only the air floor and one small thing are left. Nothing dips on a clock.

**Voices with habits.** Each singer is one creature: a perch on a real plant or stone of this land, a few syllables drawn once, and habits. Thirty that each sing four seconds a minute, not three that never rest. A land keeps its singers across loads: each is seeded from the room's hash and its perch.

| Kind (placeholder) | When and where | Habit; how it bolts |
|---|---|---|
| `high_percher` | first at `dawn`, fullest at `high_spring`; the tallest crowns | the chorus fills in below it as the sun climbs; a wing-whirr |
| `meadow_trill`, `two_note` | by day; grass heads, bushes | answers a neighbour, never over it; `two_note` is the voice left when `high_summer` falls silent; a whirr off the heads |
| `roost`, `night_hoo` | `dusk` in reeds; cold `night` on ledges | a chatter that stops all at once; low notes a minute or more apart |
| `pond_frog` | `thaw` by day over 5 C; `high_spring` and `flush` from `dusk` to `night` over 5 C; pond edges | the first voice before rain; a plop |
| `cricket` | the first at `dusk`, then `evening` and `night`; `flush` to the first `frost` | 40 chirps a minute at 10 C, 112 at 20 C, silent under 5 C; a scuttle in litter |
| `grasshopper`, `bee` | in `heat`; by day at flowers, from `thaw` on | a dry rasp in the midday lull; a wing note going by, never chased |

- Before a fall the birds stop and the frogs start: 50 s and 30 s ahead, read from `FallIn`. The dials page keeps the one ladder of signs, and the vent's note is on it at 30 s.
- **The cold and the dark.** The dial's default is the real date: `freeze_up` now, then `deep_winter`, and an evening-only player meets night all winter. So a winter night gets a cast of equal weight. Wind in `held` leaves and `bare` twigs; reed plumes hissing dry. Frost ticking as it forms; ice pinging at a pool's edge. A roost settling far off; `night_hoo` on its ledge. The folk's evening: a door, a two-note whistle, a far mill, one bell a village as the beacon wave crosses. The fire as the night's centre. And the Gubble's small contented note, now and then, in a quiet dark.
- **The hush, ruled.** Singers inside the hush's reach stop. The reach is BODY-11's ring by pace, times the step's own factor below: 0.5 on moss, 2 on dry leaves. The ring is 0.4 m walking, 1.5 m running and 2.5 m at a landing (`docs/runs/RUN-4-BODY.md:79`). So his chain holds: the season changes the step, the step changes who stays, and where to walk is a choice at every station. A body standing still in a tall stand of `oat_grass` or `reed` gets its singers back in half the time: the leaves page's hide. It is scenery only. Singers, their bolts and lane N's scenery animals read it; no rule, gate, command or creature's fate does. The ring any rule reads stays the BODY page's, by pace alone. Never for the Gubble. On a shared island, later, a hush is heard only by the body that caused it.
- **A bolt is a sound (beyond).** A singer chased off leaves by its own exit, at its place: a plop, a whirr, a scuttle. A run plays the meadow instead of emptying it. The farthest singer is back after 2 to 3 s of stillness, the nearest by 8 to 12 s. A walk hushes only what is within reach.
- **A book of voices (beyond, later).** A singer heard and then found is sketched into the journal. It waits for MENU-14's pages and has no packet here.

**Footfalls.** A step is the gait's own event (which foot, how fast, wet or dry: `N/Characters/Gaits/GaitEvents.cs:5-21`) on a surface, under the dials' cover. A **surface** is the Ground token and, on `grass`, the Grass token and whether it stands as straw (its phase is `held`).

| Ground kind | `bare` | Under a cover |
|---|---|---|
| grass | a soft pat and brushing blades; each grass kind has its word, the dials page's Underfoot column: swish, whisper, thump, rasp, click, rattle, a hiss of canes, a slap of straps | `dew`, `wet`: 4 dB down. `frost`: one glass tick on an untrodden spot, then quiet. `snow`: a creak. `litter_dry`: a bright crackle. `litter_wet`: a soft press |
| dirt; sand; stone; wood | a low pat; a soft grind; a small click; two knocks | `wet` dirt is mud: a suck, then a pop |
| new: moss; needles; shallows | almost nothing; a dry hush; a burst, then bubbles | |

- **One ladder, compressed.** Level against bare `dirt`, then the hush's factor. It is `StepLoudness`, column for column, read by the four rules under it:

| | `moss`, `needles` | `path_turf` | `snow` | `litter_wet` | `dirt` | `meadow` | straw: a grass at `held` | `litter_dry` |
|---|---|---|---|---|---|---|---|---|
| Level, dB | -6 | -3 | -2 | -2 | 0 | +2 | +3 | +4 on the first steps, +2 after ten |
| Hush factor | 0.5 | 0.5 | 0.7 | 0.7 | 1 | 1 | 1.3 | 2 |

- **How the ladder is read,** all provisional:
  1. A cover with a column wins over the surface: `snow`, `litter_wet`, `litter_dry`.
  2. Else the surface's column. A ground with none (`sand`, `stone`, `wood`, `shallows`) is 0 dB and 1. A grass kind with none, and `grass` ground whose word names no kind, is as `meadow`.
  3. `dew` and `wet` take 4 dB off the surface's level and leave 0.7 of its factor, so mud is -4 dB and 0.7. `frost` and `bare` leave both alone: frost's tick is a sound of its own, laid over the step.
  4. The function returns a first level, a settled level and the factor. The two levels differ only on `litter_dry`. The feet count ten steps from when they came onto the cover or last stood still. `speed` adds the sound set's run step, 2 dB today (`A/AudioDirector.cs:403`), and nothing else: the ring's size by pace is BODY-11's.
- Nature spaces these far wider: dry litter measures 17 dB over soil, wet ground 6 to 8 dB under dry. At that spacing a walk on dry leaves would be over half of what the ear gets, and feet were his first complaint. So **the feet row of the check wins under every cover.** Litter's loudness lives in its brightness (above 5 kHz), in the first steps into it and after a pause, and in the depth of the litter. One step in thirty on deep litter snaps a twig. The season speaks through the hush more than through the level.
- Variety comes from the cause (force, wetness, depth of litter). Timing stays the gait's, and pitch moves 3% at most.
- **Running blurs.** Past four footfalls a second, every second or third is whole and the rest are light.
- **Trodden frost.** Where the feet have been comes from the leaves page's trace map once LEAF-10 lands: the `trodden` of the ground's word. Until then the feet keep their own trail of the last 64 prints, which DIAL-3 builds as it routes the step. The press map is another thing: it only parts blades.
- **Grass kinds.** The kind underfoot is the `grass` of the ground's word (`FloraGround.At`; the look's kinds from LEAF-3). `D/feet/v1.json` holds one row a kind; a kind with no row sounds as meadow, and so does `grass` ground whose word names no kind (the seam's default, before the look registers its map). Between the plants of a clump kind the word says lane, and the step is the lane's own ground: wet `moss` between tussocks, mud between sedges and rushes, `path_turf` where the lane's floor is `grass`.

**The body's own (beyond).** The run rebuilds how the body moves; each move gets a voice.

- Stems brush the body by kind and speed, where the press map says blades part. Oat-grass stands to the head and over it, and reeds at two to three bodies, by the leaves page's rows: tall grass is heard all round.
- A grip has a sound, and grit ticks down the face below: a climber hears the height.
- The catch; a landing by cover, near silent on moss; scree that runs with the body.
- Strokes. Under water the world's buses are low-passed near 800 Hz, with one's own bubbles.
- Wind in the ears grows with height above the plants and with falling speed. A top sounds high, and a fall is heard before it is felt.

**Hands, fire, water, voices, magic.**

- The Gubble's real commands sound where the Gubble is (silent today). Only a committed command sounds: never a preview, a replay or one held for a click. At most four a second, and never over the player's own.
- **One row a power (beyond).** Grow is a creak that climbs with the vine. Fire catching is a soft whump, then crackle. A Bubble has its own note by its size, lower as it swells. In the air bell the world is heard from inside a jar. All are in tune with the chimes, on degrees 1, 2 and 5.
- **A Fractal sings (beyond),** faintly, within 0.5 m: a child can find one by ear.
- A brook is a bank of bubbles: flow is their rate, depth their pitch. The Gubble speaks in soft bubble notes. The folk have no words: work, a two-note whistle, a far murmur. A mill is knock and splash at its wheel's own phase.

**A touch answers by ear (beyond).** The dials page gives each station a toy and the leaves page gives touch. Each is one kit event at the thing's place.

- `deep_winter`: a sleeper's slow breath within two bodies. The snow hush, the highs gone from the whole land, is weather and not a toy: SOUND-16's.
- `thaw`: a drip that plinks, its note by what it lands on; none lands within 0.3 m of a body. `high_spring`: bees at blossom; the chorus from a top.
- `high_summer`: a gorse pod pops at a touch. `harvest`: nuts knock in the boughs and thud down, each the voice of a windfall DIAL-18 lands.
- `leaf_fall`: a leaf lands with a tick; a drift in the lee bursts in a rush under running feet; lying leaves skitter in gusts.
- `freeze_up`: frost's glass ticks under a drawing foot.
- A shaken bough lets fall the station's own: a patter of dew, a soft dump of snow, spinning keys. A reedmace head bursts with a puff.
- **Each named day has a signature,** one a card of the dials page's table, and the check asks for it: `blossom_morning`, the dawn chorus (bees join once the sun is up); `gold_turn`, dry leaves underfoot; `first_frost`, glass; `snow_hush`, the missing highs; `frog_night`, the frogs; `firefly_dusk`, the first cricket; `nut_fall`, the knock and thud; `stone_day`, a visit of music as the beam touches the stone. That table names the packet that voices each.
- **The Turning by ear,** at the Turning's own times. From 0.3 s the new cover's step and rustle spread along the ground with the plants' front: from the feet, or from the fire when the turn is made at its stones (DIAL-10). The front is never a drawn ring or line, and the ear draws none. By 3 s the near layers have crossed with the land and the nearest tree's one act sounds. Within 5 s the new station's first voice sings, once. By 10 s the far air has changed, a stretch at a time.

**The interface.** Wood and paper, and no sound on hover. A press is a wooden tap in six variants. The chimes stay fixed, since recognition is their job.

**Music, a visitor.** No track and no loop. One tonic (home note); the sun moves a ladder of five modes (scales, bright to dark) one note at a time. A visit has a cause the player can see: the sun clears the wall, rain ends, lamps-on, the beam touching the standing stone on its day. After it, at least two minutes of world. Five voices, each made of something: string, tine (a struck metal tongue), bell, pipe, wind harp. The dawn chorus is left alone: it is the music. **The switch chooses which causes count,** not how often: the day's turns and the weather (the default); the day's turns only; any cause; never.

## The system

**A core that decides, a sink that plays.** `A/Core/` holds no engine type: recipes, pools, scheduler, cue log, offline mix, measures. A cue holds a time, sound, variant, level, pitch, bus, priority, and a point or "flat". The director and scheduler only append cues. The engine's sink plays them. The offline sink sums the same buffers faster than real time, headless, as `--preview=` runs today (`T/Audio/AudioTest.cs:41`). No scheduling decision passes through `MathF.Exp`, `Pow` or `Sin`: integers, tables and the four operations only, so a cue log is the same on every machine.

**The voice model.** A recipe is a pure function of a seed and its cause in numbers: force, wetness, size, material, speed. Three models carry nearly all: the shaker (many small collisions: leaves, gravel, far rain), the modal knock (a struck thing) and the contoured tone (a bubble, a syllable). A pool holds 8 variants of what is heard more than once a second, else 4, or 1 where recognition is the job. A bag bars the last half of the pool. A seed retires after forty plays.

**Where sounds are made.**

- *Ahead,* on the worker at start: a kit at 44,100 Hz, about 150 s of samples, 13 MB, 2 to 4 s of one core. One-shots first, so the feet are never last.
- *Just in time,* same worker: the next far air in two unlike channels, and a singer's next phrase. Far air comes in stretches of 2 to 4 s, at most 0.7 MB each at 44,100 Hz, chained by a cross-fade. A longer stretch would stall a frame, because wrapping a stream copies its bytes on the main thread. A late stretch holds the present one, never silence.
- *Live,* built last: one generator stream fed from the main thread, never managed code on the audio thread. Only what must follow a control within 0.1 s: air rushing in a fall, a dune's hum.

**The scheduler.** Timing laws by layer: scatter (gaps spread well past 20% of their mean, where the ear loses the beat), phrase and rest, turns, a clock for crickets. Each layer has its own seeded stream. At most eight cues a second; a cue may be a cluster, so rain keeps its true counts. When a dial turns, layers change by the Turning's own times.

**Emitters.** Never one a plant. At load the worker bakes a stand map: a 2 m grid of leaf mass by sounding kind and by how its leaves stand at the station (in leaf, `held` or `bare`, read from the leaves page's phase rows through its one reader, `N/Look/Flora/FloraTable.cs`). Plant emitters sit on the strongest cells within 12 m. Water, fire, mills and folk register a point or a line; two fires both sound (one today, `A/AudioDirector.cs:354-361`). Lane N registers what it draws that sings, the roost for one, as it registers the wheels. The ears sit on the body's head, turned as the camera is.

**The surface under the foot.** One answer, a surface and a cover, from six readings:

1. the room's ground kind: today's ray and tags (`A/AudioSource.cs:130-190`), with a new row for `moss`;
2. WORLD-5's footing grid; until it lands, the ground kinds alone;
3. the ground's word: `FloraGround.At`, the leaves page's seam, landed with DIAL-1. Where it lays a ground, that stands over reading 1: `needles` on a needle bed, a lane's floor between clumps. Its grass, straw and lane finish the surface. Until the look registers its maps the seam's default answers, so the ear never waits for the eye;
4. DIAL-1's `Cover`, handed that word: its litter and its sodden spots come from there. Under a roof no cover is asked for: the step is `bare`;
5. where was trodden: the word's `trodden`, from LEAF-10's trace map, and the feet's own trail until then;
6. the body's water depth: `shallows` while the feet are in water.

`StepLoudness(surface, cover, speed)` returns the ladder's two numbers. It sits in `N/Seams/` beside the noise ring, pure, the integrator's (`docs/runs/RUN-4.md:256`), and lands with DIAL-1 like the other seam files. A change to its numbers is this lane's diff, sent to the integrator.

**The mix and its buses.** Master (the fold to one channel, soft highs, the limiter last) holds Land, Voice, Music and Interface. Land (pause fade, the voices' duck, the low-pass under water) holds Ambience and Near. Near (one reverb and echo, set by place) holds Creatures and Actions. A test pins every name and send.

- **Loud:** 6 to 8 LU of range on speakers, 10 to 12 on headphones.
- **What gives way:** beds to music, music to near events, those to the player's own acts, all to the Gubble's voice. By habit first: rain comes and the singers stop. Then by gain: a voice cue drops Land 6 dB and Music 9 dB in 150 ms.
- **No startle:** nothing the player did not cause rises over 14 dB inside 50 ms.
- **Set aside:** easing a long unbroken family down by itself. It moves levels unasked and would blur every mark about level. It returns if a ten-minute mix shows the need.

**The settings** (placeholder words). One model with no room in it, drawn in the in-play panel, the Esc menu and the page before play. It takes rows as data (slider, stepper, switch), and each owner registers its own rows from its own file: this lane's from `A/AudioSettings.cs`, the dials page's two (DIAL-2), the leaves page's one switch (LEAF-2). So a later row (music's visits, thunder, the output) opens no file of lane U's. The dials' saved rows (DIAL-7) join under their own caption; before play they show a kept game's dials and do not turn them, since a turn is a command. `user://audio.cfg` version 2 is read as untrusted, and an old file stays valid.

| Row | Moves today (six buses, `A/AudioBuses.cs:23`) | Moves after SOUND-11's tree | File key |
|---|---|---|---|
| Everything | Master | Master | `Master` |
| The World | Ambience | Ambience and Creatures together, never Land, which also carries Actions | `Ambience`; `Creatures`, new |
| Actions | Effects | Actions | `Effects` |
| Music | Music; says "not yet" until SOUND-17 | Music | `Music` |
| More: Menus | UI | Interface | `UI` |
| More: Voices; Creatures | shown when the bus first holds a sound (SOUND-12; SOUND-11) | Voice; Creatures | `Voice`; `Creatures` |

- Each row has a mute, shows no decibels and plays its own kind once when let go. While the rows are open the World's bed comes back at half its level: a paused game is silent today (`A/AudioDirector.cs:336`).
- The Magic page's chime slider stays, as a trim under Actions; retiring it is a contract round.

| Switch | Stops, the default first | Packet |
|---|---|---|
| Calm ears | off; on (highs down 4 dB above 2.5 kHz, range narrowed, no Lamp-moon's breath) | SOUND-2 |
| Which sounds | the best that passes; first aid's; tonight's | SOUND-2, while more than one bank exists; the off switch for every new voice |
| Music visits | turns and weather; turns only; any cause; never | SOUND-17 |
| Thunder | off; far | SOUND-9 |
| Output | speakers; headphones | SOUND-11 |
| One ear | off; on | SOUND-11 |
| Quiet when the window is not in front | on; off | SOUND-11 |

One line, once, under Output: headphones at about 60% of the machine's volume.

**The seam to the dials.** The sound reads DIAL-1's `DialMoment` and the listener's own state. It calls no weather function of its own. Two things this page asked for are the dials page's now, and both land with DIAL-1:

- `FallIn(seconds)`, on the moment: the fall read ahead, on the same pure function and the same sky, so no vent warns of rain a sky has taken away.
- `Gust(x, z, t, wind)`: one pure function in `N/Seams/` beside the dune function, the integrator's, timed by the weather counter so a pause stops eye and ear together. The leaves page's gust map is this function drawn; the sound's stands sample it; its troughs are the lulls. From SOUND-16 on the ear keeps no gust of its own; first aid's bank `v1` keeps its seeded curve until then.

In a room the moment is the one `RoomWorld` holds, the dials page's one holder, which the look reads too; DIAL-3 reads it into the snapshot and fills none of its own. The scenario fills it in the Listening Room. On the title page it comes from the reader the page's sky uses: the day a card pinned (DIAL-15), then the chosen game's dials (DIAL-7), or the real clock with neither. The moment says `snow` only once DIAL-14 draws snow, so no snow is heard that is not seen. Who sings is rows by station, part, fall, warmth and place in `D/ecology/v1.json`.

This page keeps no token list of its own. Ground kinds and places are the Ground and Place groups of the dials page's table. Its ten places are this page's eight and two more, `spring_line` and `summit`. Those two ring as `open` and move who sings, as the dials page's year by country has it: the frogs half a station early at the spring line, spring half a station late on a summit.

## The tired-ear check

One set of measures, in the game's own code, defined one by one in `docs/audio/MEASURES.md` (SOUND-1 lands it). The readers' Python port needs numpy, so it stays out of the runners: it is the one-time witness, and its numbers for tonight's bank become a fixture under `T/Audio/`.

- A **share** is a share of A-weighted energy. **Alike** is the correlation, 0 to 1, of two sounds' spectra in bands a third of an octave wide. **Matches itself** is the correlation of a level's shape with itself at a lag. **Over the bed** is a sound's peak over the bed's rms. A sound shorter than 0.4 s takes its loudness from its A-weighted rms.
- A row is **in force** from the packet named. A build's default sounds, and a round, hold only what passes the rows in force for them. First aid's bank is still loops in one channel, and that is allowed until SOUND-8.
- Each threshold is provisional and follows what he marks in play.

| Row | It fails a sound or a scene when | In force from |
|---|---|---|
| Feet | a pool has under 8 variants (4 if heard under once a second); two are over 0.95 alike (tonight grass 0.97, sand 0.99, wood 1.00; dirt and stone differ more, at 0.76 and 0.86); feet are over 25% of a walking scene or 35% of a running one, under any cover; a step peaks over 12 dB above the bed | SOUND-1 |
| Hiss | a far bed's spectrum falls less than 12 dB an octave between 2 and 8 kHz; it has over 15% above 2 kHz; its 50 ms levels spread outside 6 to 14 dB | SOUND-8 |
| Beeps | a sustained or frequent sound has over 30% in 2 to 5 kHz (insects 100%), or over 35% in one band a sixth of an octave wide; any voice at all has over 50% in one such band above 1 kHz (tonight's birds 61 to 77%, the press 93%; chimes and music excepted, since recognition is their job); a voice's gaps spread under 35% of their mean | SOUND-1 for beds and menus; SOUND-16 for singers |
| Loops | a bed repeats in under 20 s; its level matches itself above 0.3 at a lag of 0.2 to 30 s (0.99 at 4 s tonight); a scene's does at any lag up to 120 s, which is a lull on a clock | length: SOUND-1; the rest: SOUND-8 |
| Rest | a layer sounds over a quarter of the time; a ten-minute mix spends under 8% of its time 8 LU or more under its median; over 8 cues start in a second | SOUND-8 |
| One place | a bed's two sides correlate outside 0.2 to 0.7, or lose over 3 dB summed to one channel | SOUND-8 |
| Levels | weather is over 6 LU above calm; a sitting is outside -24 LUFS by 2 LU, or its range outside 6 to 12 LU; a true peak passes -3 dBFS | weather: SOUND-1; the rest: SOUND-11 |
| Small speaker | a scene fails a row above with everything under 250 Hz removed; a lull then holds no event at all | SOUND-8 |
| **Cast** (beyond) | ten simulated minutes hold under 8 families by day or 6 by night (tonight at night: wind, insects, his feet); under 12 by day once singers have habits | SOUND-16; 12 by day: SOUND-10 |
| **Stations** (beyond) | a station by part has under three families that can sound; two neighbouring stations give the same cast at `dawn` or at `night`; a named day whose card is shown lacks its signature | SOUND-16; a named day: from the packet the dials page's card table names for its voice |
| **Answer** (beyond) | under half the player's loud acts (a run, a landing, the call, a splash) are answered within 3 s by a bolt, an echo, a ring or a hush | SOUND-10 |
| **Warm night** (beyond) | ten minutes of `night` pass with no warm near voice: a fire, the Gubble or a village | SOUND-12 |

The first eight rows fail a world for too much. The last four fail it for too little: wind alone at the right loudness is not a pass.

## The Listening Room (beyond)

He said "We'll have to work on a sound system". This is a bench for doing that work together, by ear, when he wants to. **It is never a gate:** no packet's After and no default waits on a round. His first channel is play, with F9.

**Opening it.** `--listen`, read first in `MenuBoot._Ready`, before the old saves are brought (`M/MenuBoot.cs:93`), so no save is touched even on a first launch. Also `run-room.ps1 -Listen`, a shortcut, and one small line at the foot of the settings page. It is not on the title page's own row: that is a child's front page. No land, no kernel, no save, no microphone. It plays at the shipped default levels, noted in its record, never at `audio.cfg`'s.

**Why it is the real thing.** Director, mix and limiter are the game's own, which already run on any hand-filled source with a seed (`A/AudioDirector.cs:127-133`). Only the world is scripted. Two things make it true:

- The room owns a small 3D node with a listener that follows the scenario's listener. By reading, a placed voice (`A/AudioDirector.cs:152,164`) is silent in a window with none, and no menu page has one.
- The room waits until every sound is made, then steps the director in fixed ticks of 1/60 s. With SOUND-1's seams, a scenario and seed then give one log, heard or headless. Today they do not: the playback's random start draws from the shared stream only when sound is on (`A/AudioDirector.cs:348,368`).

**The first card, 20 s a sitting** (placeholder words). "What are you listening on?": laptop speakers, headphones, other speakers. One key. The answer shows on every card and is kept as a class, never a device's name, which can carry a person's. Then three yes or no: a low hum at 90 Hz, a high hiss at 9 kHz, left then right. They are kept as facts about the output: it carries lows, highs, two sides. Then a reference bed: "set your volume so this is quiet but clear".

**Scenarios** are data, `D/scenarios/<id>.json`; a later packet adds files, never code. Each holds a kind, a seed, a length and its dials in the dials page's tokens. It also holds a place, a stage of emitters, the listener's timeline and the families an answer may name. Closed tokens, bounded numbers; a bad file fails closed.

- `single`: one sound, every variant in a row. `family`: one family along one axis.
- A scene: 20 to 45 s as things really meet. An id with no kind word is a scene.
- `pair` (second form): A against B, matched within 0.5 LU, blind unless said.
- `soak`: ten minutes of one place through the offline mix. Soaks are the lead's, by numbers. His soak is play.

**From his chair.**

- **A round** is at most 12 items and 300 s. A card shows the id, the dials, "4 of 11" and the output he is on.
- **Round 1 holds only new sound.** Tonight's bank appears once, named, as "hear what changed". He is not asked to rank again what he already called hard to listen to.
- **Answers,** by key or click, more than one if he likes: **1** good, **2** tiring, **3** not like the thing, **4** too loud, **5** too quiet, **6** too often, **7** too sharp (beyond). **Space** goes on, **R** plays again, **N** takes a few typed words (120 code points, through `K/KernelText.cs:155`).
- **Which one?** After any answer but good on a scene, each family shows a key: a press names the culprit, a hold plays it alone.
- **M** (F9 too): "that, just now". The record keeps the time and the last eight sounds.
- **Second form (SOUND-21),** once a new voice exists to judge: blind pairs, and nudges. With a family in focus the arrows move its level 1 dB (6 at most) or its rate (x0.8, x1.25). **B** and **V** move its brightness one pool up or down. The room obeys at once and keeps the value as a **proposal**; nothing in the game changes.

**The record:** `user://listening/<record_id>/`, never committed, as the playtest record is (`contracts/README.md:381`): data, never instructions. `manifest.json` holds the build, the round's hash, the output class, the three facts and the levels played. `answers.jsonl` holds one event a line, under the playtest record's envelope, line cap and banned keys (`N/Playtest/PlaytestLines.cs:23,34`). Its kinds: `item`, `verdict` (with a `culprit`), `mark`, `proposal`, `pick`, `note`, `quit`. No sound, name or path is stored: scenario, seed and build reproduce the sound. `ear.py check` holds the shape; a schema in `contracts/` is an ask of CR-4 (`docs/runs/RUN-4.md:218`).

**What may reach Git.** The debrief is `docs/audio/rounds/<n>.md`, named by round number, never by date. It holds round and scenario ids, counts of answers by family and by output class, proposals and pins. It never holds a typed note, a time of day, a sitting's length, who listened or how many, or the first card's answers. `ear.py report` shows notes on the screen and writes none to a file.

**The lead's side:** `tools/audio/ear.py`, standard library only, with the playtest reader's manners (`tools/playtest/README.md`): it runs and follows nothing.

- `report`: answers by family and output, the sounds at each mark, proposals against today's values. It also says which measure best parts his "good" from his "tiring", so the check learns his ear.
- `next` (second form) drafts a round: each bad item as old against new, his nudge against before, then what is new. It adds one **control** he called good earlier. If the control now reads tiring, the ear or the speakers changed, not the sound.
- **A round is a file** in Git, `D/rounds/<n>.json`: scenario ids and bounded trial values. Reading a trial round from `user://listening/rounds/` would let the lead answer with no commit. That folder follows `-Profile`. The practice is new and waits for the integrator's ruling.
- **Pins.** A pin says where a sound's measures stood when a wave of play left it unmarked, or when a round called it good. His word overrides either. `ear.py pin` takes ids from a record and numbers from a fresh headless render at HEAD, never from a file a player's machine wrote. It writes `D/pins.json` as canonical JSON: ids and numbers, never his words. A pin is a measure, not bytes, since maths may differ in the last bit between machines (`docs/audio/README.md:28`). Cue counts are exact; loudness holds within 1 LU, shares within 5 points, crest within 1.5 dB. A drifted pin is a line in the report, naming the measure. It fails a suite only once its voice has stood for a wave.

**The first scenarios.** "From" is the SOUND packet that ships it. A scenario is data: one shipped before SOUND-3 (lane S builds SOUND-16 and 17 first) is read by its packet's own headless test, and the room plays it once the room exists.

| Id | Sets and asks | From |
|---|---|---|
| `single_step_grass`, `single_rain_bed`, `single_night_insects`, `family_feet_by_kind` | each alone; every ground | 3 |
| `night_walk_run_stand`, `rain_comes_and_goes`, `menu_sweep` | `evening`, `fair`: stop at 18 s; `changeable`: the rise, the peak, the end; presses across buttons | 3 |
| `hear_what_changed` | tonight's bank, named, then first aid's: feet, rain, the night walk | 3 |
| `settings_rows` | each row's own sound | 2 |
| `freeze_up_evening_walk`, `deep_winter_night_fire` | the default build's own hours: held leaves, a far call, frost ticking; a fire at 1.5 m as the night's centre | 16 |
| `family_feet_by_cover`, `frost_dawn_walk` | grass under all seven covers; `freeze_up`, `dawn`, `frost` | 16 |
| `walk_dry_leaves_light_rain_dusk_then_stop` (his: Fall underfoot) | `leaf_fall`, `dusk`, `showers`: `litter_dry` going `litter_wet`, crickets, then stillness | 16 |
| `meadow_dawn_spring`, `meadow_dawn_autumn` (his: Spring against Fall) | `high_spring`, then `leaf_fall`, at `dawn`: two scenes a round sets side by side, not blind | 16 |
| `family_singers`, `frog_night_before_rain` | each singer; `high_spring`, `evening`, 8 C: frogs start 30 s ahead | 16 |
| `rain_ends_first_visit`, `lamps_on_visit` | `changeable`: the fall ends, one phrase, then two minutes of world; the part turns to `evening` | 17 |
| `title_this_hour` | this land at this hour, no feet | 13 |
| `climb_fall_catch_land`, `swim_under_and_up` | grit, the rush, the catch, a landing on moss; the world from under water | 18 |
| `family_drops_by_surface`, `under_a_mesa_in_rain` | leaf, water, soil, litter; a hall's roof | 9, 11 |
| `run_then_stand_meadow_morning` | `high_spring`, `morning`: the bolts, the hush, the wave back | 10 |
| `cliff_foot_call`, `rim_desert_wind`, `forest_midday`, `lake_night` | each face's answer; the quietest place; highs eaten; far voices carried | 11 |
| `fire_by_lake_night`, `village_evening` | `high_summer`, `night`: a fire at 1.5 m, lapping, crickets; mill, hammer, whistle, one bell | 12 |
| `named_day_<id>`, one a card but `today` (eight); `turning_at_the_fire` | each takes its card's station, part and sky from the dials page's almanac file and asks for its signature; the Turning by ear | each from the packet that voices it: six from 16, `named_day_nut_fall` from 19, `named_day_stone_day` from 14. The Turning: 19 |
| `knock_each_role`, `dive_by_ear`, `squint_by_ear` | one landform a material role; the two views | 20 |
| `pair_rate_22k_44k` | one recipe at both rates | 21 |
| `soak_night_meadow`, `soak_long_walk`, `soak_music_sun_clears_wall` | ten minutes each, offline | 4, 4, 14 |

No scenario stages a creature listening to drops on a cover it holds: see Likeness.

## The budget

| Line | Budget |
|---|---|
| Main thread | 0.10 ms mean, 0.25 ms worst: at most 6 new voices and 2 ray casts a frame, and no new memory asked for. Today a step builds its id's string (`A/SoundBank.cs:43`) and its ray's answer (`A/AudioSource.cs:142`). A test counts allocated bytes over 1,000 updates |
| Worker | the first step's pool within 150 ms of the room, the kit within 4 s |
| Mixer; memory | 40 voices, 16 of them placed; a full family drops its new cue, never the body's own. 24 MB of samples (2.4 MB today), with the managed copy dropped once a stream is wrapped: it is kept today (`A/AudioDirector.cs:57,203`), which would make it 48 MB |
| An hour's ear | lulls at least 8% of the time; music at most 10% at the default; no family past a quarter of the energy |

## Packets

Lane S owns `A/**`, `D/**`, `tools/audio/**`, `T/Audio/**` and `docs/audio/**` (`docs/runs/OWNERSHIP.md:105`). A line in another lane's file goes by diff. A lane in brackets lands the part named. No packet touches the kernel, a contract schema, a save or the companion's surface. The two seam files this page designs, `StepLoudness` and `Gust`, are the integrator's and land with DIAL-1, beside the dials' and the leaves' own; this lane reads them and sends a diff to change one. **ear** is his listen at the wave's one sitting (`docs/runs/RUN-4.md:50,219`): a packet merges on its rows in force and never waits for it. Ids 1 to 15 keep their meaning from the first draft and 16 to 21 are new, so the table is in build order.

| Id | Title | Lane; after; needs | Owns | Fails before, passes after |
|---|---|---|---|---|
| SOUND-1 | First aid, and the seams | S (the recorder's line by diff); none; headless, ear | `A/SoundBank.cs`, `A/Dsp.cs`, `A/AudioDirector.cs`, `T/Audio/**`; new `A/SoundSet.cs`, `A/SoundBankTonight.cs`, `A/Measure.cs`, `docs/audio/MEASURES.md`. By diff: the mark in `N/Playtest/PlaytestRecorder.cs:267-281` | Three commits; the lists are below this table. Today: 28 fixed recipes, rules in the director's code, no measure, a mark that names no sound |
| SOUND-2 | Sound rows in Settings | U (S: the settings file); SOUND-1's first commit; eye, ear | U: `N/Ui/RoomHud.Settings.cs`, `N/Ui/RoomHud.Camp.cs`, new `N/Ui/SettingsModel.cs`, `T/Kernel/PlayHudTest.Settings.cs` (a grant). S: `A/AudioSettings.cs`, `A/AudioBuses.cs`, and the line of `Attach` that reads which sounds (`A/AudioDirector.cs:120`). By diff: one line in `N/Look/LookDirector.GlowGrowth.cs:91` | The panel (`:41-88`) gains a Sound section: four sliders, "more", mutes, a reset, calm ears, which sounds. A change of which sounds takes hold when a room next loads. The model has no room in it and takes rows as data, each registered by its owner from the owner's own file; a test registers a row from outside lane U's files and sees it drawn. `audio.cfg` version 2 is written when a slider is let go (never today), through a settable path, so tests stay off the machine's file; an old file loads. A let-go row plays its kind (`Audition`); a moved row is heard while paused (`WorldPreview01`). A row whose bus holds no sound says "not yet", by a test. Both chimes sit on Actions. The panel's sections stay on the screen at the menu tests' three sizes (`T/Menu/MenuTest.Title.cs:260`). Today: five switches (`PlayHudTest.Settings.cs:36-42`) |
| SOUND-16 | The dial's first voices | S; SOUND-1, with DIAL-3; headless, ear | `A/SoundBank.cs`, `A/AudioDirector.cs`, new `D/ecology/v1.json`, `T/Audio/**` | On today's director. **Underfoot:** `litter_dry`, `litter_wet`, `frost`, `snow`, `dew` and `wet` as pools of eight at the levels `StepLoudness` gives; the feet row holds under each. DIAL-3 routes a step to its cover and writes no recipe. **Singers:** the three birds remade as noisy syllables in phrases with rests; `pond_frog`; `cricket` by warmth; `night_hoo`; `grasshopper`. **The cold and dark:** a rattle of held leaves and a twig's creak on the seam's gusts, frost's tick, a far roost. Before the stand map, the rattle sounds while a broadleaf stands within 12 m of the listener (found through the same `RowsNear` the word's default reads), at the stations its ecology row names. **Snow:** while the fall is `snow` no rain bed plays and the wind's bed is a dull variant with nothing above 2 kHz: the hush. It and the creak sleep until DIAL-14 lets a moment say `snow`. **The signs:** the birds stop 50 s before a fall; the frogs start and a vent hoots 30 s before, all from `FallIn`; with no such reading, no sign. A test reads the dials page's almanac file: each animal it marks heard at a station has a row in the ecology file that can sound then. The Cast and Stations rows pass. The six named days this packet voices each sound their signature in their scenario: the three DIAL-15 shows in this build (`blossom_morning`, `gold_turn`, `first_frost`), and `frog_night`, `firefly_dusk` and `snow_hush` ahead of their cards, the last asleep until a moment may say `snow`. Ships the scenarios marked 16, as data its own headless test reads. Today: three sine birds, one insect loop, leaves that sound as grass (`A/AudioSource.cs:187`) |
| SOUND-5 | The sound page before play | U; SOUND-2; eye | New `M/SettingsPage.cs`; `M/MenuTheme.cs`, `M/TitleScreen.cs`, `M/MenuBoot.cs`, `T/Menu/**` | A paper slider, stepper and switch (none exist, `M/MenuTheme.cs:113-172`), drawing the same model. Reached from the title's small row (`M/TitleScreen.cs:692`), Esc back. It fits the three test sizes. A let-go row plays its kind with no room. A value set here is what the room loads (`A/AudioDirector.cs:142`). DIAL-2's rows and LEAF-2's switch show here at once, through the model; DIAL-7's join later. DIAL-15's cards follow it on the title page |
| SOUND-17 | A first visit | S; SOUND-16; ear, likeness | New `A/Music/**`, `D/music/v0.json`; one line in `A/AudioDirector.cs` | Two causes: a fall ends (weather), and the part turns to `evening`, lamps-on (a day's turn). Each brings one phrase of 5 to 9 notes on one voice, a struck tine, in the part's mode, never twice the same. Then at least two minutes of world, and at most one visit in ten minutes. It plays on Music, so that row loses "not yet", and the visits switch ships with its four stops. `never` is silence. Today the Music bus is empty (`A/AudioBuses.cs:8`) |
| SOUND-3 | The Listening Room, first form | S (U, I: the door); SOUND-1; headless | New `A/Listening/**`, `D/scenarios/**`, `D/rounds/**`, `tools/audio/ear.py` and tests. By diff: `--listen` in `M/MenuBoot.cs`, `-Listen` in `run-room.ps1`, the settings page's foot line (which waits for SOUND-5's page) | Opens with no room. The saves folder and `audio.cfg` are byte-equal after a round, on a first launch too. Singles, families and scenes run on today's director through a scenario source and SOUND-1's seams. A test finds a listener in the viewport. A round and seed give one log, with sound on and off. Every key writes its line; `ear.py check` names each broken example; `ear.py report` writes no note. No pairs, no nudges. Ships the scenarios marked 3 |
| SOUND-13 | The interface; the title page's hour | S (U: new `M/MenuSound.cs`); SOUND-3, 5, 16; ear | `D/interface.json`; U: `M/MenuSound.cs`, and one line each in `M/TitleScreen.cs` and `M/MenuBoot.cs` that starts it, in the one-editor order | Wood and paper, no hover. The title page plays `title_this_hour` through the scenario source after loading the settings, with its own listener. Its moment comes from the reader its sky uses. It plays whichever sounds are the default, and grows as they do. Today the page is silent |
| SOUND-4 | The ear's numbers | S (I: the skill, runner lines); SOUND-3; headless, a timing | New `A/Core/**`, `D/pins.json`; `A/AudioDirector.cs:13`. By diff: `.claude/skills/listen/`, the runners, a `.gitattributes` line for `D/**/*.json` | The cue log; the offline mix; the full check, each row applied from its packet; soaks offline. A scenario and seed give one cue log, byte for byte, on Windows and Linux. A test finds no `Exp`, `Pow` or `Sin` in the scheduler. `ear.py pin` as above; a second pin is byte-equal. Timed and written down: the kit's cost, a 7 MB wrap against 0.5 MB ones, allocated bytes over 1,000 updates |
| SOUND-6 | The kit and the sinks | S (I: `-Sound` on the launcher); SOUND-4; a timing | `A/Core/**`, new `A/Kit/**`, `D/materials/v1.json` | Three models at 44,100 Hz; pools, bags, retiring seeds; capped voices; the managed copy dropped. `--sound=` takes `v0`, `v1`, `v2` or `best`, the default: each family's newest voice that passes its rows. No family waits on a round. The launcher passes no free argument today (`run-room.ps1:25`) |
| SOUND-7 | Feet | S (P by diff); SOUND-6, DIAL-3, LEAF-3; ear | New `A/Feet/**`, `D/feet/v1.json` | Steps follow the gait's footfalls through its hook, where nothing listens today (`N/Characters/Gaits/CharacterGaits.cs:28-34`); distance is the fallback. Every ground kind under every cover; a row a grass kind, with its lane's voice; the six readings with their fallbacks. The feet row passes under every cover, in his dry-leaves scene too. Levels and hush factors are read from `StepLoudness`, landed with DIAL-1 and tested in `T/Seams/`; a change to the ladder goes to the integrator as a diff |
| SOUND-18 | The body's own | S (P by diff: a grip, a catch, a slide as events); SOUND-16, its first commit beside BODY-2 and BODY-8; ear | New `A/Body/**`, `D/body/v1.json`; one line in `A/AudioDirector.cs`; one low-pass in `A/AudioBuses.cs` | First commit, on today's director, to meet BODY-2 and BODY-8: the fall's rush as four stretches cross-faded by speed, and a landing by cover. After SOUND-6: brush by grass kind, grips and grit, the catch, scree, strokes, the world under water. Ships its two scenarios. Today a fall and a climb are silent |
| SOUND-8 | Beds of events | S; SOUND-6; ear | `A/Core/**`, `D/layers/**`, new `A/Places/StandMap.cs` | The air floor; the stand map baked at load; wind by plant kind and phase on the one `Gust`, which landed with DIAL-1 and is only read here; lulls only from its troughs and the singers' own rests; far air in two channels in short stretches; in `mist` the far air and far voices drop, by data, and near sounds stay. The hiss, loop, rest, place and small-speaker rows pass on a ten-minute offline mix. No wrap passes 0.25 ms |
| SOUND-9 | Rain as drops; thunder | S; SOUND-8; ear, likeness | `D/layers/**`, `A/Core/**` | True counts in clusters; a surface a drop; the warning moved onto the kit; thunder `off` and `far`. A drop is water on a thing, never a whistle and a blast. The drum on a held leaf waits on VILLAGE-8's ruling |
| SOUND-10 | Voices with habits; the hush; the bolt | S (N registers what sings); SOUND-7, 8, BODY-11; ear, likeness | New `A/Singers/**`; `D/ecology/v1.json` | Perches read from the land; singers seeded from the room's hash and perch, the same on every load. The hush's reach is the ring times the step's factor. A body still in `oat_grass` or `reed` gets its singers back in half the time. Each kind bolts with its sound at its place. In `run_then_stand_meadow_morning` the farthest is back within 3 s and the nearest by 12 s. The beeps, rest and Answer rows pass |
| SOUND-11 | Emitters, places, the mix | S; SOUND-8; ear | New `A/Places/**`; `A/AudioBuses.cs`, `A/AudioSettings.cs`, `D/default_bus_layout.tres` | Two fires; the place reading for the dials page's ten places (rays until WORLD-5's grid lands; `spring_line` and `summit` from the ground's word); ears on the body; the bus tree pinned; `Creatures` joins `audio.cfg`; the duck; output, one ear, quiet when not in front. The level rows pass. The call's answers become placed cues |
| SOUND-12 | Hands, fire, water, voices, magic | S (N: the wheels); SOUND-11, VILLAGE-7; ear, likeness | `D/materials/**`, new `A/Hands/**` | The companion's committed commands sound at the Gubble, under the limits above (today's filter, `A/AudioDirector.cs:235`, widened in the principal only). One row a power. A Fractal's song within 0.5 m. The Gubble's contented note. Brooks from the land's water (WORLD-12's beds; until then, the pools a room holds). A mill at its wheel's phase. The Warm night row passes |
| SOUND-19 | A touch answers; the Turning | S (L, N, P register their toys); SOUND-6, LEAF-10, DIAL-10; ear, likeness | New `A/Touch/**`, `D/touch/v1.json` | Each toy is one kit event at the thing's place. A nut's thud waits for DIAL-18's windfall, and is silent until then. Each named day's signature sounds in its scenario once the packet that voices it is in: the dials page's card table names it, SOUND-16 for six of the eight, this packet for `nut_fall`, and SOUND-14 for `stone_day`, which waits for it. The Turning keeps its times, and its front starts where the dials page says: at the feet, or at the fire for a turn made at its stones. Nothing saved, nothing for the companion |
| SOUND-20 | The room rings through | S (L by diff: the two views' moments); SOUND-11, LOOK-13, LOOK-20; ear, likeness | New `A/Room/**`, `D/room_modes/v1.json` | The air floor's notes from the room's outline; one row a material role; a landing, a knock and the call wake a landform's answer; the Lamp-moon's breath; the Dive and the Squint by ear. A room with no roles sounds as today |
| SOUND-14 | Music the visitor | S; SOUND-11, 17; ear, likeness | `A/Music/**`, `D/music/**` | Five voices, every cause, the ladder. The hour's music line holds in an offline soak |
| SOUND-15 | The live voice | S; SOUND-11; a timing, ear, likeness | New `A/Live/**` | A generator stream fed from the main thread: the fall's rush, a dune's hum under a slide. Skips are counted through a ten-minute soak while a fire burns, when a save holds the main thread 31 to 73 ms (`docs/runs/RUN-4.md:314`). Past a few skips the voice falls back to kit stretches |
| SOUND-21 | The Listening Room, second form | S; SOUND-4, 6; no | `A/Listening/**`, `tools/audio/ear.py` | Blind pairs matched within 0.5 LU (single sounds from samples, scenes from the offline mix, both sides by one path); nudges as proposals; `ear.py next` with its control |

**SOUND-1's three commits.**

1. **The seams,** with tonight's sound unchanged; every later packet leans on them and edits no line of the director for them.
   - A `SoundSet` is the recipes and the rules: strides, the strict turn or a bag, the run's 2 dB, the wind's law, the insects' level, the hover tick. It is an argument of `Create` (`A/AudioDirector.cs:127`), in place of the static bank (`:143,181,200`). `v0` is tonight's set whole. A test holds a `v0` session's ids at seed 7 to the list taken at `a5548c57`.
   - Samples are cached once a set and id, shared by every director (an instance field today, `:57`). One-shots are made before loops (`:181`).
   - `Reset(seed)`. A stream of its own for playback starts, so a run with sound on and off logs the same ids and times.
   - `Audition(busKey)`, usable with no room. `WorldPreview01`, honoured at `:336`. `FamilyTrimDb` and `Solo` by id prefix. Button watching optional for each director (`:256,276`).
   - `Call(delays)`: a soft call, then one answer a delay, each later one duller and 12 to 18 dB down. BODY-11 calls it with its eight delays; a test calls it until then.
2. **The measures.** `A/Measure.cs`: A-weighted rms, loudness with the short-sound rule, band shares, alike, self-match, one sound's render. `docs/audio/MEASURES.md` defines each. A fixture holds `v0`'s numbers with tolerances. In one test `v0` fails each row in force and `v1` passes.
3. **Bank `v1`, first aid.**
   - Loops of 23 to 41 s sharing no factor. The loop bound moves to 20 to 45 s (`T/Audio/AudioTest.cs:320`); the bank stays under the test's 400 s (`:336`).
   - Eight steps a ground from a bag, through a two-pole filter. A grass step has at most 35% above 2 kHz (57 to 65% today). Past four footfalls a second, every second step is light. The feet row's numbers hold.
   - Rain at most 6 LU over calm, and for now 35% above 2 kHz; the Hiss row's 15% waits for SOUND-8.
   - Insects as noisy pulses, at most a quarter of the time.
   - The wind wanders round 0.4 of its full level, on a slow curve drawn from the seed, never the wall clock. Its troughs come at uneven times and stop at 0.15, which keeps the two real-room checks (`T/Audio/AudioTest.cs:256,279`).
   - No hover tick. The press becomes a soft knock under 1 kHz. Beds come up 4 dB, so the build is not simply quieter.
   - **New life:** a far low call at night, 60 to 180 s apart; now and then a drip or a tick.
   - F9's mark carries the last eight sounds from the director's log.

**The order.**

- **Lane S,** one agent: SOUND-1; SOUND-2's part; DIAL-3 and SOUND-16 back to back; SOUND-17; SOUND-3; SOUND-4; SOUND-6; then down the table.
- **Lane U:** SOUND-2, then DIAL-2 on the same panel and test, registering its rows in the same model; SOUND-5; DIAL-15's cards; later DIAL-7.
- **One editor at a time,** as the dials page lists it for all three pages; these are this page's lines of it. `A/AudioDirector.cs` (SOUND-1, SOUND-2's line, DIAL-3, SOUND-16, 17, 18's first commit, 4); `A/AudioBuses.cs` (SOUND-2, 18, 11); `N/Ui/RoomHud.Settings.cs` and the model (SOUND-2, DIAL-2, DIAL-7); `N/Ui/RoomHud.Camp.cs` (SOUND-2, DIAL-10); `M/TitleScreen.cs` and `M/MenuBoot.cs` (MENU-8, MENU-18, SOUND-5, DIAL-15, SOUND-3's door, SOUND-13, DIAL-7); `M/SettingsPage.cs` (SOUND-5, SOUND-3's foot line, DIAL-7); the play recorder (SOUND-1's mark, DIAL-6's line).
- **No build ships a dial whose ear is stand-ins:** the build that first carries DIAL-3 carries SOUND-16.

## What becomes of LOOK-12, VILLAGE-18 and MENU-8

All three were planned on tonight's bank (`docs/runs/RUN-4.md:75,80,163`).

- **LOOK-12** retires, and none of its sounds leaves a wave empty. The call's echo is SOUND-1's `Call`, in wave 1 as the brief promises (`docs/runs/RUN-4.md:241`); BODY-11 keeps the key and the delays, and SOUND-11 makes the answers placed. The rain's warning is SOUND-16's. The dune's hum is SOUND-15's.
- **VILLAGE-18** keeps its wheels in the shader (lane N). Its "one loop a row" becomes SOUND-12's mechanism: wheel and knock share one phase.
- **MENU-8** keeps its light. Its sound becomes SOUND-13's, which waits on SOUND-3, 5 and 16 and not on the whole system.

## Asks

**Of the brief.**

- Ruling 6 (`docs/runs/RUN-4.md:196`): the call's sounds are SOUND-1's. BODY-11's row lists `N/Audio/**` (`docs/runs/RUN-4-BODY.md:61`); it calls `Call` and edits no audio file.
- Ruling 14 (`:204`) gains the files in "One editor at a time" above.
- Section 3: LOOK-12's row goes. SOUND-1 and 2 stand in wave 1, and SOUND-16 wherever DIAL-3 does. SOUND-5, 17, 3 and 13 follow in wave 2.
- A grant: lane U on `T/Kernel/PlayHudTest.Settings.cs`, which lies under the kernel's tests (`docs/runs/OWNERSHIP.md:53`).
- The integrator lands the seam files with DIAL-1, since a seam is a contract change (`:18`). Also `.claude/skills/listen/`, the runner lines for new suites, the `.gitattributes` line, and `-Listen` and `-Sound` on the launcher.

**Of the dials page: settled on 25 November.** Its section "Where the three pages disagreed" holds the rulings.

- `DialMoment` carries `FallIn(seconds)`. `Gust` and `StepLoudness` land with DIAL-1, beside its own seam and the leaves page's.
- DIAL-3 follows SOUND-1 and writes no recipe. DIAL-2 follows SOUND-2 and registers its rows in the model.
- Thunder: this page voices `off` and `far`. `near` has no sound until his word, and no packet on any page draws the flash.
- Frogs: by its warmth rule a `thaw` evening is 1 C, under the frogs' 5 C. So frogs call at `thaw` by day, and `frog_night_before_rain` is at `high_spring`.
- The warning is SOUND-16's, on the dials page's one ladder of signs: birds 50 s ahead, frogs and the vent 30 s. The game plays this page's compressed ladder: dry litter is the brightest step, and the loudest only at first.
- Ground kinds, places and the cards' ids are tokens in its table. Its two new places are read here. Each named-day scenario takes its card's dials, and blossom morning's signature is the dawn chorus.
- No snow is heard before DIAL-14 draws it.
- From the second reading: the room's shown moment has one holder, on `RoomWorld`, and DIAL-3 reads it. The ladder has four rules of reading, so `dew`, `wet`, `frost`, a grass with no kind and the first steps on dry leaves each have one answer. Under a roof no cover is asked for. `mist` hushes the far air (SOUND-8). A named day's signature is owed from the packet that voices it, and a card waits for that packet too.

**Of the leaves page: settled on 25 November.**

- Its gust map is `Gust` drawn.
- The ground's word, `FloraGround.At`, carries where was trodden, from LEAF-10's trace map. Its press map only parts blades, and this page reads it for the body's brush alone.
- Each grass kind is a token. The lists of ten agree now: reed and reedmace are in, and savannah and dune grass wait for their countries.
- Needles: a `needle_bed` is ground, `needles`, and no cover, all year where the word says it lies; rain still wets it and frost still lies on it.
- Its hide: the hush lifts in half the time in `oat_grass` or `reed` (SOUND-10). One cue for a shaken bough (SOUND-19). A lane between clumps sounds as its own ground (SOUND-7), and a `grass` lane as `path_turf`.
- From the second reading: its heights stand here (oat-grass to the head and over, reeds at two to three bodies). What bursts under running feet is a drift in the lee, never a heap. A drip's note is by what it lands on, and none lands on a body.

## Recorded sound

Not wanted in the game. Made in code, nothing is licensed or like anyone's recording.

**One request, which holds nothing and is in no packet's needs.** His yes, by name, to fetch the US National Park Service sound gallery once, into a folder Git ignores, for measuring only: singers' syllables, a fire's crackle rate. Its page says public domain, lists 71 clips, asks for credit and states no size. The numbers would be ours; no file ships. On a yes, `docs/audio/README.md:3` is amended, since it tells the public nothing is downloaded, and the credit line goes where the numbers are kept. **The default is no fetch, and the game is good without it.**

## Provisional, for him to hear

- Every number in first aid's list, in the check and in the budget; -24 LUFS and both ranges.
- The ladder's levels and hush factors, and its four rules: 4 dB and 0.7 on dew and wet ground, ten steps to settle on dry leaves. The bolt's return times, and half of them in a hide; 0.5 m for a Fractal's song.
- The signs' 50 s and 30 s before a fall; nothing above 2 kHz in the wind under snow.
- Pool sizes; forty plays a seed; eight cues a second; a quarter of the time; lulls 8% of an hour.
- The cast's 8, 12 and 6; three families a station by part; half the acts answered.
- The first visit's 5 to 9 notes and two minutes after; the Lamp-moon's pitch; 18 dB under for a landform's ring.
- The duck; twelve items a round; the seventh key; the pins' tolerances.

## Likeness

One line an idea. No shipped work is named. A reviewer's five lines on music, rain and the hush are with the integrator.

- **Music that comes seldom, and a switch for it.** Alike: two shipped games leave long silences between pieces and let the player set how often music plays. Ours: no pieces; each visit answers a seen cause, and the switch chooses which causes count. Fallback from nature: the wind harp, which plays only when the wind does.
- **Music that follows the hour.** Alike: a town-life series plays a set piece each hour; an island-walking game's music follows hour and season. Ours: one tonic and five modes, moved a note at a time by the sun. Fallback: who calls when.
- **Rain as things.** Alike: one film's creature delights in drops drumming on a cover held overhead; another gives insects raindrops like bombs. Ours: a drop sounds as what it strikes; rain never harms; nobody performs delight. Fallback: rain heard from under a real leaf.
- **A hush round a loud walker; a step that changes who stays.** Alike: noise rings by pace and surface in stealth games; a survival game whose birds and insects answer players. Ours: the body's own ring, a wave back from the far edge; only scenery answers, and no rule reads it. Fallback: crickets stop at a footfall.
- **Beds of scattered events.** Alike: every audio tool's practice. Ours: this land's own state places every sound. Fallback: each caller keeps its own band and hour.
- **Things that sound as what they are.** Alike: an island-walking game in which each thing seen has a sound. Ours: a landform answers by the material role its room record holds, only when struck. Fallback: the same knocks, unpitched.
- **A beacon by ear.** Alike: sound beacons in audio-only games and wayfinding tools. Ours: a breath that follows the lamp overhead. Fallback: the Lamp-moon's light alone, and a village bell at lamps-on.
- **Small by ear at the Dive.** Alike: tales of people made small often lower the big world's pitch. Ours: no pitch moves; a far shimmer becomes single callers. Fallback: kneeling in a meadow.
- **The world muffled under water.** Alike: common practice. Ours: one's own bubbles, pitched by their size. Fallback: it is how water sounds.
- **A found thing that sings.** Alike: collectibles that hum are common. Ours: heard only within a few bodies, in the chimes' own notes. Fallback: no song; the Gubble turns toward it.
- **Things that answer a touch.** Alike: leaf heaps, pods and shaken trees in many family games. Ours: the station's own matter, where this land's wind left it; none hides a prize. Fallback: leaves that only skitter in gusts.
- **A book of voices.** Alike: collecting creatures in a book is common to many games, and to field guides. Ours: singers on this land's own plants, found by standing still. Fallback: a naturalist's list.
- **Bubble-talk.** Alike: a town-life series' babble, a syllable a letter. Ours: notes sized as bubbles are, with no letters behind them. Fallback: a brook's plinks.
- **Wordless folk.** Alike: invented babble in life games. Ours: no speech at all. Fallback: birds' calls.
- **A page that plays a game's sounds.** Alike: a list and a play key, an old tool. Ours: seeded scenes on the real director, answers, a record. Fallback: rendered files and a tally sheet; a tool needs none from nature.

## Research notes

- A listener tells looping noise from fresh noise up to a cycle of 20 s (source not opened by the researcher). https://econtent.hogrefe.com/doi/10.1027/1618-3169.51.4.240
- Noise heard again unannounced is learned fast and known weeks later. https://pure.qub.ac.uk/en/publications/rapid-formation-of-robust-auditory-memories-insights-from-noise/
- Timing scatter of 20% of the gap hides a repeating sound; 2% and 10% do not. Real walkers stray by about 2%. https://www.frontiersin.org/journals/neuroscience/articles/10.3389/fnins.2016.00009/full
- Games aim near -23 to -24 LUFS, within 2 LU, over a half-hour of play. https://designingsound.org/2013/02/loudness-in-game-audio/
- A cricket's rate follows the air's warmth; the rule fits one kind best. https://en.wikipedia.org/wiki/Dolbear%27s_law
- A bubble rings at about 3.26 over its radius in metres. https://en.wikipedia.org/wiki/Minnaert_resonance
- Steps on dry litter, soil and wet litter: the dials page's note.

## Not verified

- Nothing was built, run, heard or timed. The editor opened the code lines cited and the readers' table of tonight's measures, and recomputed none of it. The research's sources were not opened.
- **The second reading of 25 November was of the three pages' text.** The ladder's rule for `dew` and `wet` takes its 4 dB from this page's own footfall table and its 0.7 from wet litter's factor; nobody has heard either. Whether `speed` belongs in `StepLoudness` at all, now that it adds only the run step, is DIAL-1's to say when it writes the test.
- **No likeness check was run by the editor.** A reviewer searched general subjects for the nearest kin of music the visitor, rain as things and the hush. The integrator rules before a packet marked likeness is dispatched.
- **Whether he asked for a listening room** in words not given to this page: it is marked beyond, and nothing leans on it.
- **Two engine facts are from memory, not a run.** That a placed voice is silent with no listener: a windowed 60 s day scene under the title page, with and without one, settles it. That wrapping a stream copies its bytes on the main thread: SOUND-4's timing settles it.
- **Every cost is arithmetic** until SOUND-4 times the kit. Placed voices need a windowed run.
- **Whether the offline mix matches the engine's output:** one recording of the machine's own output bounds it.
- **How SOUND-16's recipes sound.** Nobody has heard them. The round's prototype of a litter step measured inside the check's bounds, in memory only, but with 54% of its energy in 2 to 5 kHz; litter's crackle must sit above 5 kHz.
- **The feet's share on dry leaves** at nature's spacing is arithmetic on the ladder with the bed unchanged.
- **Which bodies raise footfalls:** the gait is installed for each character (`N/Characters/CharacterMotion.cs:23`); the Gubble and the folk were not traced.
- **What he listened on, and how loud:** nobody knows; the room's first card would say. **Whether 22,050 Hz is itself harsh:** `pair_rate_22k_44k`.
- **Whether a child finds the silence after a run a puzzle or a let-down,** and whether the Lamp-moon's breath comforts or unsettles: only the kids settle it, in play.
