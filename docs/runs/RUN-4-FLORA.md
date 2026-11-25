# Run 4, flora: trees, grasses, bushes and vines

**Status: a design for the founder, 23 November 2026. Nothing is built.** `G/` is `pipeline/landscape/generator/`, `X/` is `pipeline/landscape/export/`, `N/` is `game/scripts/native/`. **(beyond)** is beyond what was asked: provisional, vetted in play. **K** waits for a kernel round. Where a lane, an After, a wave or a decision number here differs from `docs/runs/RUN-4.md`, the brief wins.

Plants turn room-shaped rock into land, and they are how the land speaks. Trees grow by botanists' rules and climb by kind. Ten grasses ring water in order. Vines hang where the cliffs weep. Each high top keeps a plant of its own. Flowers keep hours, ferns fold at a touch, and ash flowers after fire.

## What the founder asked

- "Trees should look like actual trees (not photorealistic, but artistically faithful procedurally generated renditions of trees)."
- "multiple kinds of trees, up to ten different kinds of grasses each with their own niches, bushes with fruits and nuts".
- "the chairs would be landscape that's covered in vines that they have to scale"; "forests scattered throughout, and meadows and paths worn by animals".
- "any objects on or against the walls become parts of the great cliffs"; "a sandy desert extends forever".
- "be expansive, be creative, do research, and go beyond what I initially propose, but keep the themes intact."

## The design

**Scale.** The world stays ten times the room; plants stay body-sized (`G/scale.py:59-61`). New flora grows only on magnified rooms, as Fractals do (`G/generate.py:136`): the suites play a true-size room (`tools/test-room.ps1:93-106`).

**The maker.** A maker under `G/flora/` replaces the kit's cone and five balls (`pipeline/landscape/harness/kit.py:84-90`). Each species sheet names a branching model. A tree collides as trunk, pole and up to eight caps (`contracts/room-manifest.schema.json:922-926`), so the model is also the climb: a pine is a ladder, an oak a hall under low limbs. Meshes are pinned bytes with three authored levels: branch growth magnifies rounding between machines, the exporter's clustering would wreck limbs (`X/copies.py:199-209`), and its tree fit needs a flat trunk top (`X/trees.py:227-246`). Leaves are solid clumps (the land shader is opaque, `game/shaders/painterly_land.gdshader:15`); each carries its twig point, so winter bares the branching. The leaf style is one option of the maker, so a change of mind is a rerun, not a rewrite. Pinned meshes are `.json`: the fixture key hashes only `.py` and `.json` (`tools/landscape-fixture.ps1:21`). **He walks among made trees early:** FLORA-20 plants the maker's trees in today's noise groves on magnified rooms, so wave 2's first basin has real trees, and one oak stands there both ways, clumps beside cards.

**Planting by cause.** `G/habitat.py` (new) replaces the noise groves (`G/generate.py:1385-1388`). It reads the world page's maps: soil, wetness, the spring line, wind, sky-view and trail traffic.

- *Water's rings.* Out from each pool: rush, sedge, wet meadow, dry meadow. Inside them, trampled mud and a cropped flowery lawn: a safe camp and a firebreak (`docs/world/TRAITS.md:161`).
- *Slope.* Tall grass in damp hollows, heath on thin tops, fern on scree, turf on roads. Trails tunnel through tall grass.
- *Wind.* Windward faces mossy, the lee dry thorn, rim trees flagged downwind: a compass.
- *Depth.* At a hollow's mouth: grass, fern, moss, then fungi. In cliff shade moss stands three bodies high.
- *Age.* The oldest wood stands in the deepest dust soil, or in the room's own potted plant (`pipeline/roomscan/src/roomscan/inventory/vocabulary.py:65`; a knoll today, `G/land.py:24`). Its crowns stand level, one Grow bridge apart (`N/Kernel/CommandHostGrowKinds.cs:94-96`): the canopy road.
- *Animals.* Oak saplings with nuts line trails where a hoarder forgot them. On savannah, wedge mounds stand edge-on to north: rows turn about +Y (`N/Room/PlantCopiesView.cs:527-536`) and the room holds north (`X/exporter.py:415`).

**Tops.** The eight highest, loneliest tops each keep a summit plant found nowhere else in the room, a journal page each, with `seeds: false` so sprouts never carry one off (`N/Kernel/CommandHostGrowSprouts.cs:30-31`). Pitchers stand taller than the body. Seed-clocks grow only on tops and the rim; the drift down is the body page's (K). A builder flagged a held-seed glide in a shipped backyard-scale game, so the seed-clock waits on the brief's likeness check; the fallback is a spinner tree, whose winged seeds turn as they fall, as a maple's keys do.

**Vines.** Green means water and a way up. Vine lines hang below the spring line and on wet, shaded faces over 55 degrees (`G/reach.py:7-8`). A line is leafy tufts, each a plant copy rooted on the face; fuel `shrub`, so sprouts never sow it. The climbing body already asks the ivy view (`N/SmallPlayerController.cs:1768-1769`); it will ask the copies view what grows at a point. A vine row costs the pack's `ivy_climb_factor` (`game/rules/storybook_wild/v5.json:103`); a moss row is the body page's soft landing (`docs/world/TRAITS.md:103`). The host still spends the bar (`N/Kernel/CommandHostModes.cs:128`). No command, no save field, nothing new for the companion.

**Cliffs and desert.** The spring line is a hanging garden round every cliff. Wall shelves are tiers, a crop on each. One ancient tree hangs from the highest proved face: an object, so it never burns. On the rim, dune grass and thorn thin to nothing; curled grey plants uncurl and flower in rain.

**Plants that answer.** Look only. A shipped table gives each known species a habit, set per set as the season's is (`N/Room/PlantCopiesView.cs:426`).

- *Hours.* Six clock flowers open in turn, most toward evening; each village keeps a small clock. The hour is the world's (`N/Look/WorldTime.cs:127`).
- *Sun.* Sun-followers face the sun and open only in direct sun (the look page's shade map): along the Sun Gate's beam.
- *Touch.* Shy ferns fold as the player nears; never for the Gubble (`N/Look/Fauna/PondLife.cs:44-45`).
- *Rain.* Moss swells, seed-clocks close, the rim blooms. The look already reads the kernel's weather (`N/Look/NatureLook.cs:155`), a six-minute cycle (`N/Kernel/NatureState.cs:39-44`).
- *Fire.* Ash flowers rise where a cell's ash is fading (`N/Look/NatureLook.cs:111-124`).

**Grass and the budget.** Ten kinds are ten sets, so ids, fire and sprouts need nothing new (`N/Kernel/CommandHostGrowSprouts.cs:51-67`); the look bakes a kind map at load, as it bakes the canopy (`N/Look/GrassLook.cs:257-294`). A room holds 64 sets (`contracts/room-manifest.schema.json:805-808`); this page spends 54. The look page's ledger lines are flora's ceiling. Flora's caps: triangles per level, copies no more than today, twelve sets to an 8 m tile.

**In the game.** Bushes stay walk-through (`contracts/room-manifest.schema.json:984-1008`). Food follows species through the rules pack (`game/rules/storybook_wild/v5.json:179-185`); rules v6 gains a `nut` kind. Root bridges carry trails over streams, walked by the footbridge's deck rule (`G/checks.py:148-153`). The beach sand (`G/generate.py:1240`) and coast conifers (`G/generate.py:1382`) go with the sea.

**Second wave (K).** Burnt land heals in stages; today a burnt plant is gone for good (`N/Kernel/NatureState.cs:181-183`). Grow drops a root ladder from a lip, and a vine's tip leans toward the caster's Glow; today ivy only grows up from its foot (`N/Kernel/CommandHostGrowIvy.cs:13-17`). In the world page's halls the light picks what grows: growth needs light over 0.12 (`N/Kernel/NatureState.cs:70`).

## Packets

Generator packets run in series with the world's, the villages' and the body page's gates packet: each edit rekeys both fixtures (`tools/landscape-fixture.ps1:15-35`). FLORA-9 to FLORA-11 need no generator: they play on today's flowers and ferns (`G/generate.py:1448-1454`), in wave 2, once the look's three budget packets have measured the frame. The brief's table gives each packet's lane, After and wave: the generator's life packets go to its lane H. FLORA-1 is split: the maker and one oak there, the other species in FLORA-21, the early planting in FLORA-20.

| Id | Title | Lane | Paths | After | GPU | Founder's eye | Model | Done when |
|---|---|---|---|---|---|---|---|---|
| FLORA-1 | The maker; one oak, two ways | F | `G/flora/**`, pinned `.json`, `-text` | none | no | at the first basin | Opus | The maker and one species sheet, the oak, with solid clumps and with cut-out cards; same bytes twice; triangle caps hold; at most eight caps a tree; proved by bytes and counts, no picture |
| FLORA-2 | Plants through the exporter | Integrator | `X/copies.py`, `X/trees.py` | FLORA-1 | no | no | Opus | Kit bytes kept; a maker tree keeps levels and caps; a barked bush is a plant (`X/trees.py:13-16`) |
| FLORA-3 | Planting by cause | C | `G/habitat.py`, `G/generate.py`, `G/checks.py` | FLORA-1; the world's maps | no | at regeneration | Opus | Deterministic; species in niche; rings in order; caps hold; no top opened early; true-size bytes unchanged |
| FLORA-4 | Seeps, tiers, the rim | C | `G/generate.py`, `G/checks.py` | FLORA-3; the world's rim | no | at regeneration | Opus | Every seep and ledge planted; nothing on a way up or past the tile edge |
| FLORA-5 | Vine lines | C | `G/vines.py`, `G/reach.py`, `G/checks.py` | FLORA-3; the world's ledges | no | at regeneration | Opus | The reach flood proves a line to every top and the rim; no top opens early |
| FLORA-6 | What grows here | P | `N/Room/PlantCopiesView.cs`, `N/SmallPlayerController.cs` | none | no | no | Opus | A quarter of rock's price on a vine, full off it or burnt; moss answers the landing |
| FLORA-7 | Foliage, ten grasses, sun-followers | L, `Room/**` grant | `game/shaders/**`, `N/Look/**`, `N/Room/PlantCopiesView.cs` | the look's grass packet and shade map; FLORA-2, FLORA-3 | yes | yes | Sonnet | Plants sway; winter bares broadleaves; sun-followers open in the beam; no line where blades end |
| FLORA-8 | Fruit and nuts | Integrator; P | rules v6, `N/Kernel/CommandHostFood*.cs`, `N/Room/FoodView.cs` | FLORA-1 | no | no | Opus | A nut is picked, pocketed, eaten; kit species still bear; the companion refused as today |
| FLORA-9 | Flowers keep hours (beyond) | L, the grant | `N/Look/PlantHabits.cs` (new), `game/shaders/painterly_land.gdshader`, `N/Room/PlantCopiesView.cs` | FLORA-6; LOOK-9; LOOK-23 | yes | yes | Sonnet | At pinned instants today's daisies shut at night; unknown species unchanged; at most +0.1 ms |
| FLORA-10 | Touch and rain (beyond) | L | as FLORA-9 | FLORA-9 | yes | yes | Sonnet | Today's ferns fold near a moving player, never the Gubble; a rain habit opens in rain |
| FLORA-11 | Ash flowers (beyond) | L | `N/Look/NatureLook.cs` | none | yes | yes | Sonnet | A burnt patch flowers as its ash fades; one draw; kernel suites unchanged |
| FLORA-12 | Lawns, tunnels, hoards, mounds (beyond) | C | `G/habitat.py`, `G/checks.py` | FLORA-3; the villages' trails | no | at regeneration | Opus | Each pool has its lawn; saplings only on trails; mounds one yaw; a clock meadow by the start |
| FLORA-13 | Wind, moss, belts of light (beyond) | C | `G/habitat.py`, `G/flora/**` | FLORA-3; the world's maps | no | at regeneration | Opus | Flagged trees point downwind; no grass under a third sky-view; tall moss only in wet shade |
| FLORA-14 | Summit plants, seed-clocks (beyond) | C | `G/flora/**`, `G/habitat.py` | FLORA-3; the world's tops; the likeness check | no | at a basin | Opus | No summit form twice in a room or off its top; seed-clocks, or the spinner tree, only on tops and rim |
| FLORA-15 | Old wood, canopy road (beyond) | C | `G/habitat.py`, `G/checks.py` | FLORA-3; the world's soil | no | at regeneration | Opus | Five veteran caps chained 0.4 to 1.2 m apart, level within 2 cm; never the only way |
| FLORA-16 | Ancient tree, root bridges (beyond) | C; integrator | `G/flora/**`, `G/generate.py`, `G/checks.py` | FLORA-4; the villages' trails | no | at regeneration | Opus | One hanging tree, an object, on the highest proved face; each root bridge walked |
| FLORA-17 | K: fire's second act (beyond) | P | `N/Kernel/NatureState.cs`, `N/Kernel/CommandHostNature.cs` | changes decision 67 (`docs/game/MECHANICS.md:276`) | no | yes | Opus | By one saved burn time: grass, then bramble and birch; pine where one burned; oak from its root |
| FLORA-18 | K: root ladders, light-led vines (beyond) | P; C | `N/Kernel/CommandHostGrowIvy.cs`, `G/vines.py` | FLORA-5 | no | yes | Opus | Grow at a lip's bud drops a ladder; a tip leans to a Glow; every top proved without either |
| FLORA-19 | K: light gardens, rug prairies (beyond) | P; C | `N/Kernel/CommandHostGrowSprouts.cs`, `G/habitat.py` | the world's halls and floor marks | no | yes | Opus | Under a roof a sprout's kind follows its light; a taped rug is prairie |
| FLORA-20 | The maker's trees in today's groves | H | `G/generate.py`, `G/flora/**` (read) | FLORA-2 | no | at the first basin | Opus | On magnified rooms today's groves plant the maker's trees as their sheets land; one grove by the spawn holds the oak both ways; kit trees stay at true size; true-size bytes unchanged; copies no more than today |
| FLORA-21 | The species sheets | F | `G/flora/**` | FLORA-1 | no | at a basin | Opus | Six more trees, four veterans, two flagged forms and five bushes, each a pinned `.json` mesh in three levels; same bytes twice; triangle caps hold; clumps unless he picks cards, and then a rerun |

## Provisional values

- Triangles by level: trees 1,500, 300, 60; bushes 300, 80, 20.
- Trees: oak, birch, willow, apple, thorn, pine, spruce; veterans of oak, willow, pine, thorn; flagged pine, thorn. Bushes: hazel, bramble, currant, bilberry, gorse.
- Grasses: meadow, path turf, fescue, tussock, wood grass, sedge, rush, oat-grass, savannah grass, dune grass.
- Sets, 54 of 64: trees 13, bushes 5, grasses 10, clock flowers 6, fern, moss and fungi 3, vines 2, summit plants 8, stones 2, and a crop, seed-clock, mound, sapling and rim bloom.
- Clock flowers open at 05, 09, 12, 16, 18 and 20, four hours each. A shy fern folds within 0.4 m; the rim blooms until 90 s after rain.
- Copies under the stand-in's 333,186; vine rows under 40,000; rim plants end at the tile edge, about 6 m past the lip (the brief's rim table).
- Nuts heal one quarter; one hazel or oak in three bears; every hoarder's sapling bears.

## Decisions for the founder

1. **Leaves as solid clumps, not cut-out cards.** Every pinned mesh follows it. Recommended: clumps, judged on one oak made both ways; a card shader is the fallback (`game/shaders/painterly_foliage.gdshader`). The brief takes clumps as provisional (its D7) and holds nothing: the oak stands both ways in wave 2's first basin, since no made mesh reaches the game before FLORA-2 and FLORA-20, and FLORA-21 goes on in clumps meanwhile.

Built as provisional, no answer needed: vines as the way up at a quarter of rock's price (`docs/game/MECHANICS.md:343`); the species list; nuts; saves starting fresh once, at the regeneration.

## What was rejected and why

- **From the first proposals:** a grass packet and ledger of flora's own (the look page's); the maker in the harness kit (the integrator's format); flora at true size (the suites' bytes); one grass set with a habitat map (a contract round first); kind in the colour's alpha (`N/Room/PlantCopiesView.cs:536`); long vine mats (rows turn about +Y only).
- **Twelve clock flowers, four ages a species, a plant for every top, browse-line trees.** The 64-set cap.
- **A fungus that throws the body; lichen ladders.** The worldbook's rulings.
- **Pitchers to wade in; dew to drink or roll.** Water parts and commands for what is a look.
- **The canopy road as a gate.** The reach flood does not model crowns (`G/reach.py:19`).
- **Giant fallen leaves; flowers in a rug's pattern.** Invented things make a false room.

## Beyond what was asked (to vet in play)

- First, on today's land: flowers that keep hours, shy ferns, ash flowers.
- Trees climbed by their kind; winter branching; vines on wet rock; seep gardens; the moss forest and its soft landing.
- Pool lawns, grass tunnels, hoarder's saplings, compass mounds, flagged trees, belts of light.
- A plant of its own on each high top; seed-clocks; the oldest wood in the room's potted plant; the canopy road; the ancient tree; root bridges.
- Later (K): fire's second act, root ladders, light-led vines, light gardens, rug prairies.

## Research notes

- Branching models; damaged trees regrow small copies of themselves. https://harvardforest1.fas.harvard.edu/publications/pdfs/Tomlinson_AmScientist_1983.pdf
- Linnaeus's flower clock. https://en.wikipedia.org/wiki/Linnaeus%27s_flower_clock
- Sun-tracking arctic flowers warm basking insects. https://pmc.ncbi.nlm.nih.gov/articles/PMC6798827
- The sensitive plant folds within a second. https://www.snexplores.org/article/mimosa-sensitive-plant-muscles-fold-leaves-fast
- Resurrection plants uncurl within hours of water. https://en.wikipedia.org/wiki/Selaginella_lepidophylla
- Cones sealed until fire; resprouting roots. https://www.uv.es/jgpausas/papers/Lamont-2020-CritRevPlSci_serotiny.pdf
- Water's rings: reed swamp, sedge, meadow. https://en.wikipedia.org/wiki/Hydrosere
- Grasses sort by moisture down a slope. https://core.ac.uk/download/346444066.pdf
- Hanging gardens at desert seeps. https://www.nps.gov/glca/learn/nature/hanginggardens.htm
- Table-mountain summits keep their own plants. https://en.wikipedia.org/wiki/Tepui
- Cliff cedars over 1,000 years old. https://www.americanscientist.org/node/880
- Jays forget cached acorns. https://oaks.cnr.berkeley.edu/?p=1760
- Living root bridges. https://www.nature.com/articles/s41598-019-48652-w
- Unlinked, from the builders or memory: crown shyness, flag trees, fireweed, the grazed ring round water, twining tips, north-set termite mounds, cave-lamp plants.

## Not verified

- Nothing was built, run, rendered or timed. No picture was seen.
- Whether solid clumps read as leaves; whether fire reaches a vine high on a face; whether a tree's cap passes the bridge's lip test (read, `N/Kernel/CommandHostGrowBridge.cs:84-99`; not run).
- How today's kit flowers and ferns fold in the land shader, and the cost; draws per tile with 54 sets.
- Which model each species follows: from memory.
- The other pages' maps, hollows, shade map and trails are unbuilt. Today's planter roots nothing outside the floor outline (`G/generate.py:1232`).
- How a cheap vine line avoids opening tops early.
- Whether today's planter takes a made tree without FLORA-3 (FLORA-20); the likeness check on the seed-clock, not run.
- Not read: `G/detail.py`, `G/sites.py`, `G/climb.py`, the food layout and the shaders past their headers. The real garage's rooms were not opened.
