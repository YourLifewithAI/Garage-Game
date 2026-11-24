# Run 4, villages: settlements the land explains, and folk living their days

**Status: a design for the founder, 23 November 2026, reworked for his charter and the worldbook (`docs/runs/RUN-4-WORLDBOOK.md`). Nothing is built.** `G/` is `pipeline/landscape/generator/`, `X/` is `pipeline/landscape/export/`, `N/` is `game/scripts/native/`. **(beyond)** marks what goes beyond what was asked: provisional, vetted in play. Where a lane, an After, a wave or a decision number here differs from `docs/runs/RUN-4.md`, the brief wins.

Every village shows why it is there: a spring, a ford, a gap, the place the window's sunbeam lands. The folk build from what lies underfoot, take trades that fit a body one centimetre tall, and keep a day, a week and a year. The generator writes villages, ways and animal signs into the room's data. Folk and animals are scenery, placed only by that data, the clock and the rain: no entity, no command, no save field.

## What the founder asked

- "little villages tucked about in different parts of the landscape with NPC's moving about living their lives."
- "paths worn by animals, things of that nature, to kind of make the whole area, the world, feel lived in."

## The design

**Today.** One hamlet a land, three cottages at most, nobody living there (`G/life.py:275-326`, `G/generate.py:368-401`), sited on shelter, water and flat ground (`G/life.py:323`). A site no road reaches regenerates the land, about 100 s (`G/REPORT.md:275-277`). The jetty's road and shore goals (`G/generate.py:488-502`) go with the sea; doors still face the nearest water (`G/generate.py:373-374`), else a well.

**A reason for every village (beyond).** One winner for each reason the land offers, as a closed token:

- **Wet point:** the largest, where water pools.
- **Spring line:** in the lee, at a scree fan's wet toe; three wheel-mills.
- **Ford:** along the long road at a stream.
- **Gap:** a gateway in the pass between two landforms.
- **Dry point:** a ring on a knoll in marsh.
- **Sun stone:** where the window's beam dwells; hour stones along its track.
- **Ledge:** stair-farms on a wall thing's terraces; a granary on the driest.
- **Summit:** one summer steading on a desk-mesa.
- **Rim:** a watchtower with fog nets, atop the way up.
- **Dust bank:** homes dug into a still corner's silt.

Five to eight live in a room; a reason the land lacks is skipped. An unreached site stays a ruin, rotten rungs below it; nothing regenerates. Sites favour the lee on the world page's wind map. The nearest keeps today's crate and walked doors (`G/generate.py:565-624`), and its smoke shows from the start, for the world page's `first_hour` check. Building on an object's landform, barred today (`G/generate.py:382-384`), opens for tops and terraces.

**Built from what is underfoot (beyond).** Six habits, read from the world page's countries and grounds and adding none: reed houses on stilts in lake country; stone and turf on steppe and moor; timber on the grass plain, where the woods stand; woven round houses on tall stems in savannah; mud domes on rim sand; dug-outs in dust. Walls take the nearest landform's tint (`G/life.py:155`): the desk-mesa's village is the desk's colour. All stay body-sized (`G/scale.py:54-56`). The unused tower (`docs/look/CHARM.md:22-23`) is the rim post.

**Ways.** Roads are graded and proved by the walker's one flood (`G/scale.py:73-80`); none is written out today (`G/generate.py:437-495`). The worldbook gives the trail sum to the world page; this page takes its ranks. The busiest trails become roads, the family's line between doors the long road, and villages stand at crossings. A child's rule: at a fork, take the joined branch, to water or a pass. The folk's pitch is a climb's middle act: cut steps and ladders from scree to the bench where a trade ends.

**Signs before sightings (beyond).** Animals come first as generator data (`docs/world/TRAITS.md:148-153`): prints, dens, nests, whitewash under each proved ledge, mud nests under wall shelves, a hoarder's caches on trails, a salt lick, cairns at forks leaning to water, a silver night road to the fruit bushes. Each token names its creature; enough of one mark a watching place.

**The data.** `x_landscape_ways`, beside its neighbours (`X/exporter.py:420-430`): villages (reason, habit, tint, homes with door and charm points, spots, beacon order), ways as 3D lines with kind and rank, signs, a calendar. Closed tokens, no free text (`X/tests/test_export.py:219`); magnified rooms only (`G/generate.py:136-137`); read as the intro reads its source, hash-checked, bounded, failing closed (`N/Look/Intro/IntroSource.cs:56-58`).

**The folk's day.** Trades at the body's size (beyond): dew-roller, water-carrier, herder, scent-road carrier, rope-maker, terrace farmer, miller, lamplighter, watch. `FolkDay` builds each day once: legs along the ways. A villager's place is a pure function of the block, the world's moment (`N/Look/WorldTime.cs:127,148`) and the rain, which the look reads from the host (`N/Look/NatureLook.cs:155`). It holds no scene types, so tests pin it and the menu's title map can show the folk. In play it takes the moment from LOOK-9, so the preview clock's hour and date move the folk with the sun. The figure walks toward its place and never jumps. Evening is the busy shift (provisional).

- **Dawn (beyond).** Dew-rollers push beads from field to cistern. Ledge folk step off the cliff and sink under seed fluff down a proved line; at dusk a hoist basket lifts them home. The folk's drift shares the seed glide's likeness check (VILLAGE-11); its fallback is the spiderlings' own way, a strand of silk let out on the breeze.
- **Rain (beyond).** It rains every six minutes (`N/Kernel/NatureState.cs:39-44`), so nobody stops: each walks on under a leaf held overhead, and one under a real leaf shuffles over for the player. A leaf held over a small figure in rain has well-known kin in film, so VILLAGE-8 waits on the brief's likeness check. The fallback keeps to what small animals do: folk walk the dry lines under overhangs and drip-lines, and wait out the worst under the nearest real leaf.
- **Manners.** A wave within 0.6 m, a look up at the body page's call, a wait at a blocked way; never for the Gubble (`N/Look/Fauna/PondLife.cs:44-45`).

**The week and the year (beyond).** The game day turns at dawn (`N/Look/WorldTime.cs:124-125,162`), so an evening is one day. Each day the market stands in a different village, its stall frames dressed and lit; two days a week it is the lamp fair, under the room's ceiling lamp (the stand-in's record has one, `pipeline/landscape/corpus/generate.py:218`; the generator ignores it). Herders and grazers summer on the summit and winter at the spring line; twice a year the drove fills the trail. Four times a year two villages string a new rope beside an old bridge. On the world page's rare beam days folk gather at the sun stone.

**Light and sound that lead home (beyond).** At lamps-on (`N/Look/LookDirector.cs:487`) the rim post lights, then each village in sight of the last: a wave that draws the walls in the dark. Glows fade together today and draw to 60 m (`N/Look/Charm/CharmLook.cs:33-35`); village lights must reach the room's diagonal. A mill row gets one positional loop, as the fire has (`N/Audio/AudioDirector.cs:164`), made in code (`N/Audio/AudioDirector.cs:19-20`).

**Animals as scenery (beyond).** One draw each, as the fish: grazers; dust-herds, lint creatures that roll downwind along cliff feet, heap in still corners at dusk and cling behind a walker; a dusk roost wheeling over the largest reed bed, showing where water lies; moths asleep on vine bark, circling lamps at night; shrimp in summit pools. They flee by the body page's hush table (fixed numbers today, `N/Look/Fauna/Shoal.cs:73-76`). Dust creatures in a house's corners also have kin in film, so VILLAGE-16 waits on the likeness check. Ours are weather before they are animals: pale, eyeless tumblers the home wind rolls, as tumbleweed and real dust clumps move, never dark things that scurry from the light. They roll with the kernel's wind once R4-5 gives it the home bearing.

**Drawing.** One MultiMesh of a code-built figure posed in its shader, and one of what folk carry (leaf, bead, load, fluff, lantern), as the fish are drawn (`N/Look/Fauna/PondLife.cs:18-19`). No physics body: land collision is built round every `CharacterBody3D` (`N/Room/TerrainCollision.cs:19`). No route search: 10 to 36 ms each (`N/Navigation/RoomNavigation.cs:977-978`). All share the look page's 0.8 ms (`docs/runs/RUN-4-LOOK.md:50`).

**Rules kept.** Folk and animals change nothing and read no kernel thing but the weather. Cliffs must be terrain, not shell walls, or the charm switches off (`N/Look/Charm/CharmLook.cs:207`, `N/Look/LandscapeLook.cs:134-135`). Every name is a placeholder; the family writes the words.

**Seams.** World: trail sum, beam track, wind map, cobweb nets. Flora: tier crops, flower-clock beds, cache saplings, root bridges. Look: dew beads, Lamp-moon. Body: hush, call. Menu: sign pages, the title map.

**Later.** An islander takes the home each village keeps empty, a place (`contracts/island-story.schema.json:165-183`). The hoist becomes the player's ride by a kernel command.

## Packets

Generator packets run in series with the world page's: each edit rekeys both fixtures (`tools/landscape-fixture.ps1:15-35`). VILLAGE-1 to 4 play on today's land. VILLAGE-8 on are **beyond what was asked**. The brief's table gives each packet's lane, After and wave: the generator's halves go to its lane H, and folk, fauna and charm to its lane N. VILLAGE-7 is split: the kit's frame and two habits there, four more in VILLAGE-19. VILLAGE-8, 11 and 16 wait on the likeness check.

| Id | Title | Lane | Paths | After | GPU | Founder's eye | Model | Done when |
|---|---|---|---|---|---|---|---|---|
| VILLAGE-1 | The ways block | Integrator; L | `pipeline/landscape/harness/package.py`, `X/exporter.py`, `contracts/validate.py`, `N/Look/Folk/WaysSource.cs` | none | no | no | Opus | An example validates; hostile data fails closed; other rooms keep their bytes |
| VILLAGE-2 | Today's hamlet out | C | `G/ways.py`, `G/generate.py` | VILLAGE-1 | no | no | Opus | The stand-in exports doors, a well, proved roads; 1x byte-identical |
| VILLAGE-3 | The folk's day | L | `N/Look/Folk/FolkDay.cs` | VILLAGE-1 | no | no | Opus | A pinned instant gives the same places anywhere; no scene types; no jump |
| VILLAGE-4 | Folk drawn | L; integrator wires `N/RoomWorld.cs:208` | `N/Look/Folk/FolkLook.cs`, `game/shaders/painterly_folk.gdshader` | VILLAGE-2, 3 | yes | yes | Opus | One draw; no body; a wave, never at the Gubble; within 0.3 ms |
| VILLAGE-5 | Sites by reason | C | `G/villages.py`, `G/generate.py`, `G/checks.py` | the world's land | no | later | Opus | Five living villages of four reasons on the stand-in, each tested; doors walked; no regeneration |
| VILLAGE-6 | Roads, the folk's pitch | C | `G/ways.py`, `G/life.py` | VILLAGE-5; the world's trails | no | later | Opus | Road ends walked; a fork's joined branch ends at water or a pass |
| VILLAGE-7 | Kit: the frame, two habits | C; L | `G/village_kit.py`, `G/checks.py:34`, `N/Look/Charm/**` | VILLAGE-5 | yes | yes | Opus | The kit's frame and the stand-in's two habits, one a session; within 512 objects (`X/exporter.py:83`); charm reads the block's points, not kit tokens (`N/Look/Charm/Hamlet.cs:97-108`) |
| VILLAGE-8 | Leaf umbrellas | L | `N/Look/Folk/**` | VILLAGE-4 | yes | yes | Sonnet | A second draw; in rain every figure holds a leaf |
| VILLAGE-9 | Beacon wave | L | `N/Look/Charm/**` | VILLAGE-5 | yes | yes | Sonnet | Villages light in order; seen from the rim; nothing casts light |
| VILLAGE-10 | Market, fair, mending | C; L | `G/villages.py`, `N/Look/Folk/FolkDay.cs`, `N/Look/Charm/**` | VILLAGE-7 | yes | yes | Sonnet | A pinned week visits every village; the old rope stays up |
| VILLAGE-11 | Dew, drift, hoist | C; L | `G/ways.py`, `N/Look/Folk/**` | VILLAGE-6, 8 | yes | yes | Opus | Drift lines clear land by 0.3 m; nobody airborne after mid-morning |
| VILLAGE-12 | Grazers, the drove | L | `N/Look/Fauna/Herds.cs`, `N/Look/Folk/FolkDay.cs` | VILLAGE-6 | yes | yes | Sonnet | One draw; pinned days give summer on top, winter below |
| VILLAGE-13 | Sun stone | C; L | `G/villages.py`, `N/Look/Folk/FolkDay.cs` | the world's beam track | no | later | Opus | Stones on the stand-in's track; folk gather on a pinned beam day |
| VILLAGE-14 | Stair-farms | C | `G/village_kit.py`, `G/villages.py` | VILLAGE-7; FLORA-4 | no | later | Opus | Tiers walled; a granary on the driest ledge; steps on the proved rim way |
| VILLAGE-15 | Signs | C | `G/signs.py`, `G/ways.py` | VILLAGE-6 | no | no | Opus | Every sign on reachable ground; tokens only |
| VILLAGE-16 | Dust-herds | L | `N/Look/Fauna/DustHerds.cs` | VILLAGE-4 | yes | yes | Sonnet | One draw; they roll with the wind and heap at dusk |
| VILLAGE-17 | Roost, moths, shrimp | L | `N/Look/Fauna/**` | VILLAGE-15; the world's pools | yes | yes | Sonnet | Moved in the shader; the flock starts at lamps-on |
| VILLAGE-18 | The mills' sound | Sound; L | `N/Audio/**`, `N/Look/Charm/**` | VILLAGE-7 | no | yes | Sonnet | Wheels turn in the shader; one loop a row; no file loaded |
| VILLAGE-19 | Kit: four more habits | C; L | as VILLAGE-7 | VILLAGE-7 | yes | yes | Opus | The other four habits, one a session, each merged alone; still within 160 objects |

## Provisional values

- Villages: five to eight, cap 9; one to five buildings; 8 m apart; at most 160 of 512 objects.
- Folk: about 60, cap 96 (fish: 72, `N/Look/Fauna/Shoal.cs:59`); stroll 0.2 m/s (the player walks 0.32); drift 0.35 m/s down, the body page's one drift speed.
- Figure: 0.10 m, about 300 triangles, six variants, drawn to 30 m; rays for the nearest 8, four a second.
- Evening shift to 21:30; beacon wave 1.5 s a hop.
- Village lights glow and cast none: an exported lantern is a real light as wide as the island (`docs/look/CHARM.md:44`).
- Market by game day modulo 7; droves at season days 105 and 285; mending on each season's first three days.
- 24 grazers, 40 dust-herds, 120 roosting, 48 moths.

## Decisions for the founder

The brief takes all three as provisional (its D6 and D8) and holds no packet for them; its D9 asks about the world's day. Rare days (the drove, the mending, the beam) are seen by the preview clock's date (R4-6).

1. **Folk are scenery now:** they walk, work and wave; no talk, no give, nothing saved, nothing told to the AI. Changes "We do not need to populate actual avatars" (`docs/ROOM-TO-LANDSCAPE.md:58`). Recommended: yes. Taken; judged at VILLAGE-4.
2. **A small people built in code:** no download, not the family's drawn characters. Recommended: yes, judged at VILLAGE-4; the fallback is rigs for the nearest eight (`N/Characters/Gaits/CharacterGaits.cs:18`). Taken.
3. **Keep the real clock:** the week and the year ride on it, and a faster day starts saves fresh (`N/Look/WorldTime.cs:10-13`). Recommended: keep. Taken for now. An evening-only player sees night all winter, so the brief adds the preview clock and asks him about an evening arc set at New game (its D9); `FolkDay` takes whichever clock the world has.

## Beyond what was asked (to vet in play)

Built in as provisional; none waits on an answer.

- Villages with reasons and habits; five to eight, not three to five (VILLAGE-5, 7).
- Small-body trades, leaf umbrellas, about 60 folk (VILLAGE-3, 8).
- Market ring, lamp fair, mending days, the drove (VILLAGE-10, 12).
- Dew harvest, morning drift, hoist (VILLAGE-11); sun stone and sundial (13); stair-farms (14); beacon wave, mills (9, 18).
- Signs, dust-herds, roost, moths, shrimp (VILLAGE-15 to 17).

## Research notes

The world-builders' links; this editor searched nothing and opened none.

- Settlement site factors: https://savemyexams.com/o-level/geography/cie/22/revision-notes/1-population-and-settlement/1-5-settlements-and-service-provision/1-5-2-factors-influencing-settlement-site-growth-and-functions
- Elm's sun window: https://www.swissinfo.ch/eng/science/villagers-in-elm-await-spectacular-trail-of-light/2259532
- Ballooning spiderlings: https://pmc.ncbi.nlm.nih.gov/articles/PMC6065530
- Meteora's net and windlass: https://cnewa.org/magazine/meteora-between-heaven-and-earth-30340/
- Fog nets: https://en.wikipedia.org/wiki/Fog_net
- Ledge granaries, cut steps: https://www.nps.gov/meve/learn/historyculture/cliff_dwellings_home.htm https://en.wikipedia.org/wiki/Moki_steps
- Pile dwellings: https://www.britannica.com/topic/Lake-Dwellings
- Transhumance: https://en.wikipedia.org/wiki/Alpine_transhumance
- A yearly grass bridge: https://ich.unesco.org/en/RL/knowledge-skills-and-rituals-related-to-the-annual-renewal-of-the-qeswachaka-bridge-00594
- Mill rows: https://canmore.org.uk/site/69377
- Jays' caches: https://oaks.cnr.berkeley.edu/?p=1760
- Moths at lights: https://pmc.ncbi.nlm.nih.gov/articles/PMC10827719
- Dust bunnies: https://www.wikiwand.com/en/Dust_bunny
- Rock-pool shrimp: https://www.nps.gov/elmo/learn/nature/tinajas.htm
- From memory: market rings; harvest-mouse nests; loess dug-outs; beacon chains; ants herding aphids; eyrie whitewash; swallows' mud nests; snail trails; starling roosts; bark moths; leaf umbrellas.

## What was rejected and why

- **Villagers as kernel citizens; a contract round first:** an avatar is player or companion only (`contracts/room-state.schema.json:448-452`); only scenery reads the block, so it starts as an extension.
- **A needs model; a firepit circle; names from tokens:** a timetable; kernel state; untrusted text.
- **Rigged folk from the start:** each part is its own mesh with modifiers (`docs/characters/RIG-API.md:52-57`).
- **A ray from each figure; a real lamp a village:** the frame budget.
- **Bowers, lost things, folk on the player's bridge:** folk read data, clock and rain; invented clutter is a false room.
- **Mending that takes the bridge down:** a proved road keeps its deck.
- **A living snail; a raft of carriers; riding the hoist now:** a moving collider; a bridge that bears nothing; a kernel command.
- **Cord causeways, outlet caves:** cords are not scanned; taped only.
- **Folk that bolt; work stopped by rain:** the hush is the animals'; rain comes too often.

## Not verified

- Nothing was built, run or rendered.
- The cost of folk and six more draws; two views are already over 16.7 ms (`docs/look/reviews/run3/README.md:25-31`).
- Whether a code-built figure pleases beside the drawn characters.
- The likeness checks on leaf umbrellas, the folk's drift and dust-herds: not run.
- Whether the new land offers each reason; whether the other pages, edited side by side, carry what the seams name.
- Whether today's checks can prove a drift line; the object count; generation time; the weekday under a faster clock record.
- Not read: most of `G/checks.py`, `G/detail.py`, the fish shader, the gait layers, the sound synthesis; the real-garage rooms.
