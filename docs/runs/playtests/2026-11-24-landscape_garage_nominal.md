# Playtest: landscape_garage_nominal, 24 November 2026 (Challenge, standard; build `2e6dc3aa`; record `20261124-*-landscape_garage_nominal`)

**Read with** [RUN-2-PLAYTEST-LOOP.md](../RUN-2-PLAYTEST-LOOP.md), [RUN-2-STATUS.md](../RUN-2-STATUS.md), the brief the play tested ([RUN-4.md](../RUN-4.md), wave 1's menu lane: the title page, New game and Continue) and [AGENTS.md](../../../AGENTS.md). 12.7 minutes, a clean quit, 0 marks, 0 refusals (0 not shown on screen), worst frame window 561.6 ms at 1:39, 48% of the time within 2 m of the Gubble. The record's id is given without its clock time; the record stays on the machine that played.

## What the founder found (24 November 2026, DiamondAge, the menu lane's build under a trial profile at `2e6dc3aa`)

- **The sound tires the ear.** "They were charming at first, but after listening to them it's genuinely hard to listen to. We'll have to work on a sound system."
- **No sound controls.** "We don't have sound controls such as ambient sounds vs. action sounds vs music in the main menu's settings UI."
- **The player should choose the world's state, and the choice should matter.** "If the player picks Spring, the plants all go into bloom. If the player picks Fall, the leaves are falling and that changes how your feet sound." And: "Their choices become a vibe to play, and also affects what you can find and harvest and interact with the wildlife."
- **The trees are simple geometry.** "They should have more natural forms with visible foliage and individual leaves and branches."
- **The grass is one grass.** "There should be varying types that are visually distinct, they should clump differently, grow to different heights, wave in the wind differently and be used for different things."
- **Tune the sound together.** "Maybe we can set up audio scenarios where we run through specific sounds and sound combinations to make sure things truly sound right?"

No "Keep" was said. The title page, New game and Continue drew no complaint; that is not the same as a pass.

## What the record shows

- **The marks:** none. Neither F9 nor F8 was pressed; every finding above came from the chat afterwards.
- **The refusals:** none. Seven commands, all the player's, all accepted: six `goal.set` and one `effect.start` (66 ms, the slowest).
- **The frames:** mean 13.3 ms. The worst window (561.6 ms at 1:34 to 1:39) is the load-in: the body is standing at the spawn, and the window's collections (`232/23/10`, gen 0/1/2) paused 452.9 ms. Two more long frames follow at the same spot (348.1 ms at 2:04 to 2:09, 178.6 ms after it). 21 of 132 windows had a frame over 33.3 ms. The slowest stretch all the way through is 8:20 to 8:40 (mean 14.9 to 16.6 ms).
- **Where the time went:** standing 81%, walking 6%, no breadcrumb 13% (the title page, before the room). The game was paused from 4:16 to 8:16, and the stills show the Settings panel open for most of that: about four minutes looking for a control that is not there. The whole play stayed in the lowest height band. The companion was offline throughout.
- **About the record:** a clean quit. The reader ignored two fields it does not know (`frames` on frame events, `grab_ms` on still events). Stills worth a second look: any from 4:24 to 8:05 (the Settings panel with its five switches and no volume row).

## The causes (the integrator, from the source)

Paths under `game/scripts/native/` unless they say otherwise; every line checked against the tree on 25 November.

- **The feet.** Steps are counted by distance (`Audio/AudioDirector.cs:398-403`), 2.9 a second walking and 5.6 running, from two samples a ground (`Audio/SoundBank.cs:92-100`) played in strict turn, which measure 0.97 to 1.00 alike on grass, sand and wood. Running on grass, steps are 71% of what the ear gets. The rig already says when each foot lands (`Characters/Gaits/GaitLayer.cs:214-215`) and nothing listens.
- **Hiss.** The one-pole filter (`Audio/Dsp.cs:33-39`) barely cuts at a 22,050 Hz sample rate, so rain, grass, sand and water are close to white noise. Rain is 10 dB over the calm night and returns for 150 s in every 360 s (`Kernel/NatureState.cs:39-44`).
- **Beeps.** The night insects are three bare sine tones between 2,950 and 3,750 Hz on fixed periods (`Audio/SoundBank.cs:182-202`); the birds are sine sweeps and sing only by day; every button press and hover is one unvaried tone.
- **No rest, no place.** Six loops of 6 to 12 s; the soft wind never falls under 0.34 outdoors (`Audio/AudioDirector.cs:326`); every bed is mono and centred with no reverb; only the player's body makes a sound.
- **No controls.** Six buses and a tested volume API exist (`Audio/AudioSettings.cs`), but the game only ever loads them (`Audio/AudioDirector.cs:142`): no screen sets a volume and nothing is saved. The in-play Settings panel holds five switches (`Ui/RoomHud.Settings.cs:41-88`, the switches at `:50-54`); the title page has no settings at all.
- **No choice of season, hour or weather.** The season is colour only (`Look/LandSeason.cs:195-219`) and a testing key steps it (`RoomHud.cs:148-161`); the kernel has no season; the weather is one fixed six-minute loop (`Kernel/NatureState.cs:37-51`). A saved clock (`x_clock`) exists in the contracts (`contracts/room-state.schema.json:346`) with no way to set it.
- **The trees.** The build drew the kit: a post and five balls for a broadleaf, a post and three cones for a conifer (`pipeline/landscape/harness/kit.py:84-92`), with painted leaf dabs that fade out between about 4 and 11 m. The made oak of FLORA-1 was not in this build's land.
- **The grass.** One set of copies, every copy the same code-built tuft turned and scaled, with one wind for all (`Look/GrassLook.cs:38-51`).
- **The load-in hitch.** A 453 ms collection pause in the first seconds of the room; not yet traced to its allocator.

## What the founder decided

- A sound system is wanted, with controls for ambient, action and music.
- The player chooses the season, the day and night, the weather "and more", and the choice changes what blooms, falls, sounds, can be found and harvested, and how the wildlife behaves. The real clock (his answer of 24 November) becomes the default of that choice.
- Trees and grasses are to be natural forms: branches, leaves, distinct kinds with distinct uses.
- The sound is tuned with him, by scenarios he listens to and marks.

## Keep

- The buses, the limiter and the safe levels: nothing in the bank can hurt a child's ear, and the volume API is sound and tested.
- Making sound in code: every one of the 28 sounds can be rebuilt and measured outside the game, so a new recipe can be judged by ear before it is in a build.
- The season as one global the look already reads.

## Change

Written as guesses the next play can check, each with the lane it falls to. The packets that answer each are named from [RUN-4-BUILD-2.packets.json](../RUN-4-BUILD-2.packets.json); the build page says which come first.

1. **A step that follows the feet and is never the same twice will stop being noticed.** Steps from the gait's footfall events, many variants a ground, softer and lower. Lane S with lane P. Packets: SOUND-22 (the bench's measures), SOUND-1c (eight steps a ground, dealt from a bag), SOUND-7 (feet from the gait, on every cover).
2. **Beds made of events, with rests, will bear ten minutes.** Wind that comes and goes, insects and birds as living voices, rain with near and far, a place for each sound. Lane S. Packets: SOUND-1c (first aid: longer loops, the wandering wind, insects as pulses), SOUND-8, SOUND-9, SOUND-10, SOUND-11.
3. **Three sliders on the title page and in play will be found in seconds.** Ambient, action, music (and all), saved. Lane U with lane S. Packets: SOUND-2a, SOUND-2b, SOUND-5 (the plan builds four: Everything, The World, Actions, Music).
4. **A scenario he can mark will get the sound right faster than a play.** The listening room: single sounds, families and real combinations, with a verdict a click each. Lane S with lane U. Packets: SOUND-22 and SOUND-23 (listening sets as files), SOUND-3a, SOUND-3b, SOUND-3d, SOUND-24 (the Listening Room in the game), SOUND-3c, SOUND-4, SOUND-21.
5. **A season he picks will read at a glance and under the feet.** The world's dials: season, hour, weather, each changing what grows, falls, sounds and can be found. Lanes Q, L, S and U. Packets: DIAL-1a, DIAL-1b, DIAL-2, DIAL-21, DIAL-3, SOUND-16a (the next build); DIAL-4 to DIAL-7 (the saved dial, the sky); DIAL-8 and DIAL-18 (what can be found); LEAF-14, LEAF-8, LEAF-9, DIAL-14, DIAL-16 and SOUND-16b to 16d (what blooms, falls, lives and sounds).
6. **A tree with branches and leaves will read as a tree from a body's height.** The made trees in the land, with leaves as cut-out cards near the eye. Lanes F, L and the exporter. Packets: LEAF-1a, LEAF-1b, LEAF-1c, FLORA-2, LEAF-5a, LEAF-5b (the trees, in the land); LEAF-6, LEAF-2a, LEAF-2b, LEAF-13 (the leaves). The plan builds true leaves on twigs rather than cards (D11); the cards oak, FLORA-23, is held as the way back.
7. **Four grasses that differ in height, clump and sway will read as kinds, and each will be for something.** Lanes F, L and Q. Packets: LEAF-3, LEAF-15, LEAF-4a (the next build); LEAF-4b, LEAF-4c, LEAF-12; their uses, LEAF-17. The plan gives the grasses to lanes N and L.
8. **The load-in pause can go.** Find the allocator behind the 453 ms collection. Lane Q. No packet answers it yet.

## Drop

- The strict two-sample footstep, the sine insects and the unvaried button tones.

## Not this round

- Music itself: the bus is kept and a slider shown; what plays on it is a design of its own.
- Reverb by place (cave, cliff foot, open meadow): after the beds are right.

## The reader

- Teach `report.py` the two fields the newer recorder writes (`frames` on frame events, `grab_ms` on still events), so "About this record" stays for real damage.
