# Run 4, leaves: leaves, branches and grasses, brought forward

**Status: a design for the founder, 24 November 2026, mended on 25 November after three critiques; it amends `docs/runs/RUN-4-FLORA.md`. Nothing is built, drawn or timed.** Code read at `run4/integration`, `a5548c57`; the two commits since touch the menu and the runners only, and line numbers are that commit's. `G/` is `pipeline/landscape/generator/`, `X/` is `pipeline/landscape/export/`, `N/` is `game/scripts/native/`, `S/` is `game/shaders/`, `LT/` is `game/tests/native/Look/`, `FA/` is `game/assets/flora/`. A body is the engine's 0.10 m avatar; through the camera's 68 degree lens (`N/RoomHud.cs:61`) a pixel covers 1.25 mm for each metre of distance at 1080 rows. Every token here (station, phase, cover, ground, place, grass, plant, litter) is in `RUN-4-DIALS.md`'s one vocabulary table; this page owns the rows behind the grass, plant and litter keys. The three pages were made one design on 25 November, and read whole a second time, seam by seam, the same day (the dials page's rulings 21 to 40): the four seam files, the build order and the one-editor lists stand on the dials page, and this page repeats only its own lines of them. **(his)** is what he asked; the rest is **beyond what was asked** and has an off switch. Names are placeholders: the family writes every word a player reads. Where this page differs from `docs/runs/RUN-4.md`, the brief wins until the integrator rules.

**Words.**

- **Kit:** the stand-in shapes every installed room wears today. **Made tree:** one grown by the plant maker from a species **sheet**, pinned as bytes.
- **Set:** a room's table of one kind of plant copy; a **row** is one copy. **Factor:** a copy's size as a share of its natural size; 0 is burnt.
- **Mass:** a solid lump of leaves. **Lantern:** the far level's one crown shape. **Sprig:** a shoot of leaves hung near the eye.
- **Cap:** a standing place on a tree, pinned in its row; a perch. **Kept mass:** the leaves under a cap.
- **Rank:** a leaf's own order number, 0 to 1. **The hand:** how far the moment is through its station.
- **Pair:** before and after pictures with timings. **Ledger:** LOOK-1's table of what each part of the frame costs.
- **Stand-in:** the made-up test garage, the "10x Island". **Magnified room:** any room built ten times over. **K:** waits for the brief's kernel round. **D7:** its leaf-style decision.
- **Bole:** the trunk below its first limb. **Sward:** an unbroken carpet of grass. **Tussock:** a tuft raised on its own old growth. **Keys:** winged seeds. **`rauh`, `massart`, `troll`:** ways of branching, each named for a botanist.

He said the trees "are only simple geometry" and should have "visible foliage and individual leaves and branches"; grasses should "clump differently, grow to different heights, wave in the wind differently and be used for different things." His picture is the kit drawn exactly: five balls on a post, three cones, one dome (`pipeline/landscape/harness/kit.py:84-92`). It is painted with 3 cm dabs gone by 11 m (`S/painterly_land.gdshader:659-672`), with no gaps and no wind (`:15,444-461`). Grass is one tuft with one fixed wind (`N/Look/GrassLook.cs:38-51,616`). Four things are built, each with a safe default, none a question for him:

1. **Real leaves near the eye, solid masses beyond.** Leaves are opaque. Cut-out cards need a smoothing the game does not run (`S/painterly_foliage.gdshader:2`; `game/project.godot:294`); they wait as the fallback.
2. **Today's trees dressed at once:** limbs, lobes, tiers and stems on the kit, in every installed room, with no perch moved.
3. **Trees fewer and greater:** a quarter as many at the made size, with real limbs, for fewer triangles in all.
4. **Every grass in one draw:** one blade mesh shaped by a table. Ten kinds cost one kind's triangles; their height costs pixels and is held to a budget.

## What the founder gets

1. **Build one, in every room already installed, his garage's island among them (LEAF-2; build one of the dials page's order, beside the sound's first aid):** leaves. Each tree and bush near him wears moving leaves; conifers wear needle tufts. No regeneration, nothing saved, no kernel edit.
2. **Build two (LEAF-13, LEAF-14; the dials' first build, where DIAL-2's rows do the picking and DIAL-3 with SOUND-16 the crackle):** form, and the year's first forms. The balls become lobed masses on forking limbs with sky between, the cones drooping tiers, the domes bushes on stems. Pick `high_spring` and the near trees blossom; pick `leaf_fall` and leaves drop and lie where his feet crackle.
3. **After the grass cut (LOOK-2; LEAF-4):** eight grasses he can tell apart at a glance. Every room holds all eight; a meadow shows the three or four its ground suits.
4. **After the next generator batch, on the "10x Island":** oak, birch, spruce, pine and apple, a quarter as many and up to twice as tall. They have trunks, limbs and seats, and young ones stand among them. That island rebuilds itself when the generator changes (`run-room.ps1:40-45`); its save starts fresh.
5. **Then:** boughs that sway by kind; the year on every plant, turning outward from where he stands; litter his feet hear; grass that parts; a bough that answers; a toy at every station.
6. **Later:** five more trees, made bushes, reedmace, and kinds the kernel knows, to pick and make with. His own garage's land takes made trees when it is regenerated (WORLD-7).

## The trees

### What the eye must see

- **Under it, 0.3 to 3 m:** bark's pattern, a limb's collar, single leaves (8 to 80 pixels) each moving, buds, catkins, fruit.
- **Near, 3 to 8 m:** leaves still leaves (3 to 8 pixels); limbs and sky through gaps.
- **Middle, 8 to 60 m:** the kind, by outline and branching; boughs swaying; colour tree by tree.
- **Far, 60 to 150 m:** a wood's skyline and colour (a 1 m tree is 13 to 5 pixels).

A leaf in true proportion to a 1 m tree is 7 mm, one pixel at 5 m. So the storybook leaf is larger: 1.2 to 4.5 cm on a made tree.

### The chain of levels

| Level | Reach | What is drawn | Triangles |
|---|---|---|---|
| **Hand sprigs** (new) | 0 to 1.5 m; the nearest 12 plants | Each leaf in its true outline: lobes, teeth, a notch, a needle pair | 6 a leaf; 50 a sprig |
| **Sprigs** (new) | To the sprig reach, 8 m at most; bushes 5 m | A shoot: a twiglet, five to seven folded leaves set by the kind's rule, one slot for a bud, catkin, blossom, fruit or cone. Opaque, two-sided, no shadow | 24 a sprig; 240 sprigs a plant at most |
| **Full** | 0 to 18 m (`N/Look/DrawDistances.cs:22,38`) | Every stem (59 on the pinned oak), flared collars, leaf masses; bark relief within 2.5 m | 1,500 a made tree |
| **Middle** | 18 to 60 m | Trunk, thickest limbs, masses joined | 300 |
| **Far** | 60 to 150 m | A three-sided trunk under a lantern | 60 |

- **Sprigs live in the look,** built in code as grass tufts are (`N/Look/GrassLook.cs:324`): no generator, export, contract or save. They hang at points spread by area over the plant's own leaf mass (the mesh its set draws nearest, `N/Room/PlantCopiesView.cs:494`), picked by the copy's seed (`N/Room/PlantCopies.cs:111`). They are children of the copies view, so the intro hides them with it (`N/Look/Intro/RoomIntro.cs:330-334`).
- **Four leaf classes, closed:** `kite` (a folded oval), `lobed`, `strap`, `needle`. A sprig leaf is two triangles, so from 3 to 8 m kinds differ by arrangement, size, colour and motion. The true outline is the hand sprig's.
- **A leaf fits its plant.** A sprig scales with its copy, and no leaf is longer than a twentieth of its plant's height.
- **One pool, one reach.** 9,600 sprigs, nearest plants first, 60 a square metre of mass. The sprig reach is the distance of the pool's last plant, eased over 1 s, 8 m at most: one global. Leaves fold into their buds over its last quarter, as a blade does today (`S/grass_blades.gdshaderinc:190-192`), and a mass draws in only inside it. So no leaf pops, and no tree stands drawn in and leafless. On the stand-in the reach is the whole 8 m at the spawn and about 4 m in the densest wood, at 35 sprigs a tree and 10 a bush.
- **Folding, never fading:** no see-through pixels. A burnt plant wears none; a grown or seedling plant wears sprigs at its factor.
- **A perch is never bare and never moves.** The worldbook bars a season that opens or shuts a way (`docs/runs/RUN-4-WORLDBOOK.md:106`). A kept mass never draws in and never sways: at every station and distance its drawn top is within 1 cm of the cap's top. Every cap wears a mark that plainly bears weight, all year: a flat plate of leaves, a bracket, an old nest, a mistletoe ball.
- **Limbs that hold.** A tree collides as a trunk, a hidden pole and at most eight caps (`G/flora/make.py:355-360`). Up to three caps sit on the elbows of the thickest limbs: seats inside the tree. A limb with no cap is drawn thinner than a body's arm, so nobody jumps for a great limb and passes through it.

### The kit dressed (his)

His garage's island keeps the kit until WORLD-7. So the look redraws the kit's three shapes now, through one mesh seam (LEAF-6). Collision, rows and saves are untouched.

- **`broadleaf`:** the post stays. A limb forks from its top to each ball's centre. Each ball becomes a kept top lobe under its cap and two side lobes, with gaps between.
- **`conifer`:** three tiers of drooping boughs round the post, the tip kept, the trunk seen between tiers.
- **`shrub`:** four to six stems from the ground under three to five small masses, inside the dome's own bounds, so berries sit as today.
- **A leaf a grove.** A copy's grove number (`N/Room/PlantCopies.cs:110`) picks one of its shape's leaf rows: round, lobed or whip for a broadleaf; tuft or comb for a conifer. Woods differ; the kernel still knows one `broadleaf`.
- **The same channels as a made tree.** The look builds these meshes, so each vertex carries its twig point, rank, kept mark and sway weight (FLORA-2's row gives the layout). Sway and the year then reach his garage's island without a regeneration.

### Ten trees

Height by spread is the made tree's, in metres. The planter scales it 0.7 to 1.15, and one tree in four is young, at 0.4 to 0.6. No planted tree tops 1.5 m until the gates model a crown (`G/reach.py:20-22`). The sheets hold every other number (`G/flora/sheet.py:43-61`). The model is `rauh` but for spruce (`massart`) and beech (`troll`, new: every axis levels out, sprays in one plane). **Year** is the phase at stations 1 to 8 in the dials page's letters: b `bare`, h `held`, u `bud`, c `catkin`, l `leaf`, o `bloom`, g `green`, f `fruit`, t `turn`, d `drop`, s `seed`. A find is drawn only (beyond).

| Kind | Height by spread | At a glance; bark | Leaf on the sprig | For the body | A find | Year |
|---|---|---|---|---|---|---|
| `oak` | 1.0 by 1.3 | A wide dome on elbows. Deep furrows | `lobed`, 3.5 cm, six bunched at the tip | A hall: two low wide seats, a broad top | A marble gall, stations 5 to 8 | h h c l g f d h |
| `birch` | 1.3 by 0.6 | A fountain: white stem, black diamonds. Paper bands | `kite`, toothed, 2 cm, five down a hanging whip | One high top in a ring of swaying whips | A round hole in the stem | s u c l g t d b |
| `spruce` | 1.3 by 0.5 | A spire, skirts on the ground. Thin scales | `needle`, single, 1.2 cm, round a hanging comb | Skirt perches a hop apart; a dry tent beneath | Stripped cone cores on the needle bed | g g u g g g g g |
| `pine` | 1.3 by 0.6 | An umbrella on a pole. Orange flakes, dead stubs | `needle`, pairs, 3.5 cm, twelve in a tuft | A ladder: stub steps up a bare pole to one lookout | Resin beads, 4 to 6 | g g c g g g g g |
| `apple` | 0.6 by 0.8 | Low, wide, crooked. Scales | `kite`, oval, 2.5 cm, five on a spur | The first climb: low seats; fruit within a hop | A mistletoe ball, green all winter | b u o l g f d b |
| `willow` | 0.9 by 1.2 | A leaning mop of whips over water. Ridges | `strap`, 4.5 by 0.6 cm, seven, silver beneath | A leaning trunk to walk up; one seat over water | Down tufts, 3 | b c l g g g d b |
| `maple` | 0.8 by 0.8 | A neat ball of even forks. Fine ridges | `lobed`, 3 cm, two opposite pairs | Three seats at one height | Keys in pairs, 6 | b u o l g s t b |
| `beech` | 1.3 by 1.4 | A smooth grey pillar under layered plates | `kite`, wavy, 3 cm, seven in a flat spray; spear buds | Plates: level perches, like stairs round the crown | Husks, 6 | h u l g g f t h |
| `thorn` | 0.5 by 0.6 | A tangle, leaning downwind on the rim. Knots | `lobed`, 1.8 cm, five on a spur by one thorn | No way through: one top | An old nest in the tangle | b l o g g f t b |
| `alder` | 1.1 by 0.5 | A dark narrow cone at the water. Fissures | `kite`, notched, 3 cm, five | A ladder of short limbs | Woody cones all winter | s c l g g g d s |

- **The gate rule.** No made tree stands within reach of a gate route's foot or crest, nor of a bench in a higher band than its own foot. Reach is the crown's half spread plus 1.2 m, a Grow bridge (`N/Kernel/CommandHostGrowKinds.cs:94`). The planter holds the routes before it plants (`G/generate.py:659,857`). Slope alone refuses no tree: a rule by rise would clear every hillside wood. A refused tree becomes a bush, which has no perch. Veterans to 2.6 m wait for FLORA-15.
- **A first climb.** A new bar climbs 0.6 m (`game/rules/storybook_wild/v5.json:99,102`). Young trees and the apple's and oak's low seats, under 0.45 m, keep a tree climb in the first hour.
- **The larder.** Rules v5 make one `broadleaf` in three bear apples (`v5.json:182`). Only `apple` goes out as that species, so no apple hangs from an oak on a stalk three bodies long. An orchard of at least 24 apple trees stands within 8 m of every camp. Nuts wait for FLORA-8, haws for LEAF-17 and the other kinds for DIAL-18: the larder's one queue on the dials page.
- **Beneath each kind its own floor:** wood grass under oak, birch and maple; copper litter under beech; a needle bed under pine; a dry tent under spruce; rush and sedge by willow and alder.

## Ten grasses, sedges, rushes and reeds

Eight now, ten with LEAF-12, twelve with their countries: dune grass (LOOK-8) and savannah grass (WORLD-19), the warm kind (h h h g g s g h). All sizes are in metres. Wind is the kind's rate and its tip's travel as a share of its height: feeling, not measurement. Nothing sways faster than 2.5 Hz. **Now** is a sign the first grass build keeps, because the map is baked from it. **Later** waits for sets and rules v6 (pick) or K (make).

| Kind | Look | Clump; lane floor | Height | Wind | Where; today's rule | Year | Now; later |
|---|---|---|---|---|---|---|---|
| `path_turf` | Fine cropped blades | A sward, no gaps | 0.008 to 0.015: ankle | Still; a gust only, 2 Hz, 6% | Trails, lawns, camps. Kept-clear places, lanes, the fringe of bare patches | g g g g g g g g | Now: a trodden way; the quiet step. Later: a firebreak (K) |
| `meadow` | Flat blades; a tight head on one stem in four | A loose sward over under-turf | Under-turf 0.01 to 0.02; one blade in four 0.025 to 0.035; heads to 0.05: knee | Rolls in waves, 1.3 Hz, 22% | The open plain. The rest | h l g s h g g h | Now: open ground; heads puff at `seed`. Later: grain, hay |
| `oat_grass` | Few tall stems; spikelets on threads | An open stand, 6 to 8 stems a plant; lanes of path turf | 0.09 to 0.13: overhead | Bows, 0.7 Hz, 30%; lies down under a gust, rises over 20 s | The deepest hollows | h l g s s h h h | Now: a hollow; the maze; a hide; the way back. Later: grain, thatch. Find: a woven ball nest, 4 to 6 |
| `tussock` | Fountains of arching leaves under a haze of heads | Tussocks 0.12 to 0.2 across, 0.25 to 0.4 apart; moss lanes | 0.06 to 0.10 | Each fountain breathes, 1 Hz, 18% | Low ground left | h h g g g s h h | Now: dry seats. Later: wet ground; tinder |
| `fescue` | Blue-grey bristle tufts | Tufts 0.03 to 0.05 across, 0.08 to 0.15 apart; stone lanes | 0.015 to 0.03 | Stiff; a gust only, 2.5 Hz, 4% | The highest tenth | h l g s h g g h; its `held` is silver | Now: a high dry top. Later: summit pasture |
| `wood_grass` | Broad pale lax blades | Single plants 0.15 to 0.3 apart; leaf litter | 0.03 to 0.05 | Nods, 0.5 Hz, 25%, each out of step | Under a broadleaf's crown | h l s g g g h h | Now: a broadleaf overhead. Later: old wood; scented when dry; plaits, mats |
| `sedge` | Three-edged stems on a pedestal of old roots | Tussocks 0.15 to 0.25 across, pedestals 0.03 to 0.06 high; mud lanes | 0.08 to 0.12 | Sways from the foot, 0.8 Hz, 15% | 0.5 to 2 m from a reed | h g o g g s h h | Now: water near. Later: thatch, cord, mats |
| `rush` | Smooth dark leafless rods; a brown side tuft | Dense clumps 0.08 to 0.15 across; mud | 0.07 to 0.12 | Rods rock together, 1.6 Hz, 10% | Within 0.5 m of a reed | g g g g o g g g | Now: water within a few steps. Later: pith in fat is the folk's lamp |
| `reed` | Jointed canes, flag leaves, a purple plume greying to silk | A closed stand on a 2.5 cm grid, a hard edge | 0.20 to 0.30: two to three bodies | Long slow waves, 0.4 Hz, 35% | The `reeds` copies, from LEAF-4 | h h l g g o s h | Now: water to the knee; a hide. Later: the chief thatch; pipes; raft bundles (K) |
| `reedmace` | Strap leaves; a brown sausage head | Clumps of 5 to 9 stems, open water between | 0.15 to 0.24 | Stems nod, 0.6 Hz, 12% | Still shallows. LEAF-12 | h h l g o f s h | Later: down for tinder and bedding. A head bursts into drifting seed, seen only |

A hide is a screen to watch animals from: a body standing still in a tall stand brings the singers back sooner. Nothing hunts. A seed that can be held or ridden is BODY-14's, behind its likeness check.

**How they are drawn.** One blade mesh for all: today's tuft and its parts tuft (`N/Look/GrassLook.cs:324`). A tuft takes its kind from a kind map at its own root, as a blade takes the canopy today (`:16-17`). A 16-row table gives the kind's shape, clump, head, wind and year.

- **Two storeys.** The under-turf keeps today's height (`:49`), so the common ground costs today's pixels. The flower copies (2.2 to 3.6 cm, `G/generate.py:1502-1503`) and the parts' own flowers (3 to 5.2 cm, `GrassLook.cs:92`) still show over it. A kind's height stands in its tall share of blades and in the parts tufts (one tuft in three, drawn to 27 m, `:68-70`).
- **Clumps and lanes.** A clump is cell noise at the kind's spacing. A blade rooted outside its plant becomes under-turf in a sward kind. In a clump kind it folds, and the land shader paints the row's lane floor there. A kind folds blades away and never adds one. A lane's floor is a Ground token of the dials page: `grass` (path turf), `moss`, `dirt`, `needles`, `sand`, `stone` or `shallows`. Mud is `dirt` in a sodden lane, and the lanes of `tussock`, `sedge` and `rush` are sodden. A lane the table calls leaf litter is `dirt` under the litter map's own litter: it is painted by station, so the eye sees what the ear hears. A row's lane is one token: `oat_grass` has `grass`, `tussock` `moss`, `fescue` `stone`, `wood_grass`, `sedge` and `rush` `dirt`, `reedmace` `shallows`. The swards and the closed reed stand have no lane: their `clump_m` is 0 apart, and their `lane` key is then never read.
- **Ways through.** Where the canopy's bare field crosses its middle rank (0.47 to 0.53, about 0.2 m wide), a tall kind gives way to path turf. Winding lanes cross every stand: the dials' tall-grass maze.
- **Kept clear.** Only short sward grows within 0.3 m of the start, a camp or a Fractal's place (the manifest's `x_firepits`, `x_fractals`) and within 0.1 m of a flower or herb copy. The same holds within 0.15 m of a broadleaf's trunk, where windfalls lie (`N/Kernel/CommandHostFoodLayout.cs:21-22`).
- **The eye's clearing.** Blades and leaves within 0.06 m of the line from the lens to the body fold away. The lens rides 8 cm up and 32 cm behind (`N/RoomHud.cs:434-440`), so without this a stand would bury it.
- **Bounds.** A blade grown in the vertex step stands outside its tuft's own box, 0.016 m tall, and the engine would drop the tile when the eye looks up. The turf and parts instances take custom bounds to the tallest row's height.
- Past the blades the meadow floor paints each kind's colour, clump and lane by the same function (`S/painterly_land.gdshader:514-528`).

**Which kind grows where, before the generator knows.** The game bakes the kind map at load, in managed arrays, from what a room already holds. The rules run in this order. Each sorts the tufts still unclaimed by its own measure and takes its share from the top, so a share is exact (`GrassLook.cs:277-281`):

1. **Water:** `reed` is the `reeds` copies, set where feet get wet (`G/generate.py:1490`); then `rush`, within 0.5 m of a reed; then `sedge`, 0.5 to 2 m from one.
2. **The kept and the shaded:** `path_turf` takes the kept-clear places, the lanes, and the fringe of each bare patch (the bare field's rank 0.07 to 0.15); then `wood_grass`, under a broadleaf's crown, by its half spread.
3. **By height:** `oat_grass` takes the deepest 6% of hollows (a hollow is a tuft's height below the mean within 1 m); then `tussock` the lowest 10% left; then `fescue` the highest 10% left; `meadow` is the rest.

Under a conifer half the blades fold onto a needle bed.

On the stand-in the shares were 8, 43, 6, 10, 10, 19, 3 and 1 percent (path turf, meadow, oat-grass, tussock, fescue, wood grass, sedge, rush). They are one fixture's facts: printed, never asserted. A quarter as many trees takes wood grass from 19 to about 5. LEAF-12 then plants the ten as sets; only then can the kernel tell them apart.

**The ground's word.** One seam, `N/Seams/FloraGround.cs`, the integrator's, landed with DIAL-1. `At(x, z, moment)` gives, at a point:

- `ground`: the Ground token the look lays over the room's own there (`needles` on a needle bed, a lane's floor between clumps), or none;
- `grass`: the Grass token, or `none`; `straw`, when that grass's phase at the moment's station is `held`; `lane`, when the point lies between the plants of a clump kind;
- `sodden`: the spot is never drier than `wet` (a lane of `tussock`, `sedge` or `rush`);
- `litter` and `litter01`: `leaf_litter`, `needle_bed` or `none`, and how much, 0 to 1;
- `crown`: the Plant token overhead, or none; its phase is its row's, read through `FloraTable`;
- `place`: `spring_line` within 2 m of a reed, `summit` in the highest tenth, else none;
- `trodden`: whether feet have been there since the cover last changed;
- `mown`: whether the point lies in a mown place while the hay is cut, from `flush` on. The seam keeps that list, a centre and a radius each. It is empty until DIAL-13 registers the villages' places, 3 m round each, and the blades read the same list for the hay.

The look registers its maps with the seam at load: the kinds with LEAF-3, the litter by kind with LEAF-9, the trace of feet with LEAF-10. Until a map is registered the seam's default answers, as the dials page gives it, so the ear never waits for the eye. The dials' `Cover` takes the word as an argument; the sound's `StepLoudness` takes its surface. The maps are managed arrays, safe off the main thread. With kinds switched off the word says `meadow` wherever grass stands, so ear and eye agree. **The rows are this page's; their keys are tokens of the dials page's Grass, Plant and Litter groups,** and a new row is an edit there too: the ten grasses and `none`; `leaf_litter`, `needle_bed`; the trees and bushes; the kit's `broadleaf`, `conifer`, `shrub`.

## Every plant's year

**One home, three files, one writer each.** `FA/trees.json` is the maker's (lane F). `FA/kit.json` (the kit's rows and their grove leaves) and `FA/grasses.json` are lane L's. One reader, `N/Look/Flora/FloraTable.cs`. A nested `FA/.gitattributes` holds `*.json -text`, as the maker's own does (`G/flora/.gitattributes`): the root file pins no such path. No row lives in code. These tables, and then the files, are the home of every phase row: the dials page's Plants table is a copy for reading, and where they differ these win. The bushes have no table here yet, so their rows on the dials page are the draft LEAF-11 and FLORA-21 copy. The look keeps no station rule of its own.

```
{ "format": "garage_game.flora_table", "version": 1, "rows": { "<token>": <row> } }   token: a to z, 0 to 9, _; 24 at most
a kit shape (`broadleaf`, `conifer`, `shrub`), which holds these two keys only:
  "of":    "kit"
  "dress": [ one to four tokens of tree or bush rows in the same file ]
a tree or a bush:
  "of":    "tree" | "bush"
  "leaf":  { "class": "kite"|"lobed"|"strap"|"needle", "length_m": 0.005..0.06, "per_sprig": 3..14,
             "set": "tip"|"whip"|"spray"|"spur"|"tuft"|"comb", "under": colour }
  "bark":  { "pattern": "furrow"|"plate"|"paper"|"scale"|"ridge"|"knot"|"smooth" }
  "wind":  { "trunk_hz": 0.1..1, "bough_hz": 0.2..2.5, "leaf_hz": 0.5..2.5 }
a grass:
  "of":    "grass"
  "blade": { "under_m": [lo, hi] 0.005..0.03, "height_m": [lo, hi] 0.005..0.35, "tall_share": 0..1, "width_m": 0.001..0.01,
             "clump_m": [across, apart] 0..0.5, "lane": "grass"|"moss"|"dirt"|"needles"|"sand"|"stone"|"shallows", "lane_sodden": true|false,
             "head": "none"|"tight"|"threads"|"haze"|"tuft"|"plume"|"sausage", "wind_hz": 0.2..2.5, "tip_share": 0..0.4,
             "recover_s": 0..60, "height_by_station": [eight, each 0..1] }
every tree, bush and grass:
  "year":  { "phase": [eight phase tokens], "act": [eight of "none"|"unfurl"|"puff"|"open"|"shower"|"keys"|"rattle"],
             "held": 0..1, "turn_from": "skin"|"core"|"none", "stagger": 0..1,
             "litter": [eight, each 0..1], "litter_kind": "leaf_litter"|"needle_bed"|"none",
             "ramp": [bud, fresh, summer, turn, dead] colours,
             "slot": [eight of "none"|"bud"|"catkin"|"blossom"|"fruit"|"cone"|"keys"|"plume"|"head"] }
  "find":  { "what": "gall"|"stem_hole"|"cone_cores"|"resin"|"mistletoe"|"down"|"keys"|"husks"|"old_nest"|"old_cones"|"ball_nest",
             "stations": [from, to] }                                                optional
```

A colour is three numbers from 0 to 1. No other key is allowed; a wrong token, type or bound fails the file. A bad or missing file is a notice and today's picture (no sprigs, one grass), never a failed load. Bark has no grip here: a grip by kind is a body number, the body page's to ask for. A row's `slot` follows the dials page's To pick column, because one test (DIAL-8's) holds the picture to the larder: a plant shows `fruit` at every station its fruit can be picked, so apples hang into `freeze_up` and haws through `deep_winter`. `head` is a grass's seed head, the slot its grain and down are picked from. Every find in the two tables has its token: the maple's `keys` and the alder's `old_cones` among them.

**What each phase draws.** Between two stations each amount eases by the hand.

| Phase | Near: the sprig | Beyond: the mass, the far lantern |
|---|---|---|
| `bare` | Leaves folded into fat buds; twigs and bark show | A twig haze: the mass at 0.85 of its size in the bark's colour, stroked fine. Never nothing |
| `held` | The `held` share of leaves stays in the dead colour, lowest first, and rattles | The mass at that share, dead colour |
| `bud`, `leaf` | Fat buds in the slot; then leaves unfold small and fresh | The mass grows out from its twig points |
| `catkin`, `bloom` | Catkins or blossom in the slot; a shake puffs pollen or petals | Dabs of that colour |
| `green` | Full leaf | Full mass |
| `fruit`, `seed` | Acorn, haw, cone, keys or plume in the slot; food stays the food view's | Nothing new |
| `turn` | Each leaf turns as the hand passes its rank: from the skin in (maple, beech) or the core out (birch, alder) | Along the kind's own ramp, tree by tree |
| `drop` | A leaf turns, then leaves by rank and falls; so oak, with no `turn` station, still turns | The mass thins toward its twigs |

- **A grass:** `green` is the row's height for the station; `leaf` is short new shoots through old straw; `seed` raises the heads; `held` is the same blades in the dead colour; `bloom` is the side tuft or plume. Height moves with the dial: short at `thaw`, full at `flush`, stubble within 3 m of a village from `flush` on (the dials' hay: the folk cut it; DIAL-13 registers the villages' places with the seam as mown ground, and until then the list is empty and no stubble is cut). A camp keeps only its 0.3 m of short sward. No collider, no gate.
- **The winter wood is a sight, not a loss.** On the real clock the year arrives at `freeze_up`. So the kit's broadleaf rows are `held` through the winter stations, half their leaves kept brown. A bare made birch is a violet-brown haze on a white stem, its buds fat at this body's size.

**When, what, who.** *When* is `plant_year`: a from, a to and a front (a centre and a radius). It is set where the land's season is set (`N/Look/LandSeason.cs:195`) and read by sprigs, masses, blades and litter. A turn of the dial runs outward over 3 s: from the player, or from the fire when the turn is made at its stones, as the dials' Turning goes. The front's centre is one setter on `plant_year`; DIAL-10 calls it with the fire's place, and with no call the centre is the body. As the front passes, each plant does its row's one act: a shower of leaves, a puff of catkins, blossom opening by rank. *What* is the kind's row. *Who* is the hash of the copy's place that the land shader already takes (`S/painterly_land.gdshader:456`), spread by `stagger`, so a wood turns tree by tree. The front is never drawn as a line, a rim or a colour, and it cannot be paused or held: two seasons never stand side by side. *Where* shifts the hand, look only and provisional: a plant at the `spring_line` reads it half a station early, and one on a `summit` half a station late in spring and early in autumn, as the dials page's year by country has it.

**The drop, the litter, the ground's word.** At `drop`, 96 leaves at most are in the air within 6 m (288 for 3 s after a turn), and never fewer than 32: broad leaves flutter, straps tumble, keys spin. A litter map is baked at load as the canopy is: each tree's footprint times its kind's own `litter` share for the station. The dials' `litter01` is the broadleaf default only. Oak's litter is gone by `flush`; pine's and beech's lie all year. The map smears one crown along the rim record's home wind where a room has one, and not at all elsewhere: the kernel's wind turns a full circle (`docs/runs/RUN-4.md:205`). Within 3 m about 400 leaves lie as leaves and skitter. They gather in the lee as a drift at most knee deep (0.03 m, provisional): ground cover, which promises no landing. A heap deeper than that is drawn only where WORLD-5's footing is already soft, as the dials page rules. A `needle_bed` makes the ground `needles` and is no cover, the quietest floor in a wood: creep under the pines or crackle under the oaks.

## How the walker and the wind touch them

- **One wind, one gust.** The wind is the kernel's (`N/Kernel/NatureState.cs:364`). The gust is one pure function, `N/Seams/Gust.cs`, landed with DIAL-1, with the sound page's lulls in it. The look draws it into a small map and the sound's stands sample it, so a gust is seen and heard crossing a meadow together. Wind dies toward the ground: still air under a moving roof.
- **Trees in four layers.** The trunk bends at 0.25 to 0.5 Hz; a bough sways by a weight in the mesh; a sprig has its own phase; a leaf flutters by its rank. Pine and spruce nod as boughs. Oak is stiff and its leaves clatter. Birch flickers first. Willow whips write the gusts. A kept mass and its limb stay still.
- **A walker parts the blades.** First, in LEAF-4: blades lean from the body and the Gubble. Then a press map 6.4 m square, in 10 cm cells, holds which way and how hard each spot was pushed. Turf recovers at once, meadow in 2 s, oat-grass in 20 s; rush never yields and splays at its foot.
- **A trodden place is remembered.** One trace map covers the room in 20 cm cells and keeps a trodden cell until the cover changes: a dark line through dew, a path drawn in frost, tracks in snow, bent hay. It lasts the sitting and is never saved. The ground's word carries it, so frost is quiet where trodden.
- **A bough answers the body.** A landing on a cap (`LastBodyState.Landed`, `N/SmallPlayerController.cs:371,1133`), a run into a trunk or the first grab of a climb shakes that tree for 1.5 s. What falls is weather and plant matter, to watch: dew as spray, a dusting of snow, pollen, petals, leaves, keys. A shake drops nothing that can be picked, eaten, counted or kept, and wakes no creature. Nuts fall on the tree's own time and never follow a shake: a nut's fall is the food view's windfall, DIAL-18's, on a spot the larder already counts.
- **The magic touches a leaf.** A plant whose factor rises (Grow, a sprout, a replanted seedling) unfolds its sprigs from buds and wears the station's bloom for 60 s. Look only: the kernel's own Grow, no new power, nothing saved.
- **A leaf and a body.** A leaf, a petal, a key or a flake may land on the body and ride there until it moves: drawn only, never an item. A nut, a cone, a fruit or a drop never lands within 0.3 m of one. The dials page's kind rule says the same.

**The toys** (beyond). The dials page owes a toy at every station. These are the ones made of leaves, grass or prints, each shipping with its phase.

| Station | The toy | What draws it |
|---|---|---|
| `deep_winter`, `freeze_up` | Tracks in snow; paths drawn in frost | The trace map |
| `thaw`, `high_spring` | A drip shaken from a bud; petal rafts | The shake; petals lying on still water |
| `flush`, `high_summer` | The tall-grass maze; heads that puff; gorse pods that pop | The lanes; `seed` heads on meadow and oat-grass; LEAF-11's gorse |
| `harvest` | Nuts thud down; keys spin | A nut is DIAL-18's windfall seen arriving; keys are the maple's own act |
| `leaf_fall` | Stand where a leaf lands; burst a drift | Lying leaves fly from running feet and gather in the lee of trunks and stones, knee deep at most |

## The budget, and what is cut first

Counted on the stand-in at this commit (fixture `6f38372202897367`: 5,455 trees, 13,311 bushes, 371,783 grass tufts of 533,806 copies), each triangle once, in today's bands. The ledger counts a triangle once a pass (`LT/LookCaptureHarness.cs:548-549`): about 3.5 passes for a tree with its shadows, 2 for a leaf that casts none. Its seven cameras fit 0.64 ms a million primitives, within 1.3 ms. Every figure below sits inside that error, and a pair settles each.

| Triangles, in millions | Today | Leaves and the dressed kit (any room) | Made trees (the stand-in) |
|---|---|---|---|
| Trees, at the spawn | 0.82 | 0.89 at most | 0.61 |
| Trees, in the densest wood | 0.88 | 0.96 at most | 0.78; 0.66 with pine and spruce at 900 |
| Sprigs | None | 0.25 at most | The same |
| Bushes, where they stand thickest | 2,480 within 12 m | 0.07 more at most | The same |
| By the ledger's slope, worst camera | | About 0.7 ms more | About 0.4 ms under the dressed kit |

- **Nearer than 18 m a made wood costs more than the kit:** 0.47 against 0.33 million in the densest wood, where two trees in three are conifers. The saving is in the middle band.
- **Ruling 5 is kept.** The spawn runs 18.2 ms at p95 against 16.7 (`docs/look/reviews/run3/README.md:51`). LEAF-2 and LEAF-13 merge before LOOK-23 only on a pair, at the spawn and in the densest wood, whose p95 is no worse than today's beyond the run-to-run spread. If a pair misses, the cut order runs and the build ships the richest setting that passes. The last step is a floor under any pair's noise: leaves within 3 m, a pool of 1,200. The full setting returns as LOOK-2, 22 and 23 pay. LEAF-4 always follows LOOK-2.
- **The ledger's lines:** trees 2.0 ms for LEAF-2, 13, 14, 7, 8, 9; grass 2.4 ms for LEAF-4, 10, 12.
- Grass keeps its triangles (576 a tuft near). Draws are 1,257 at the spawn today; sprigs add 16 at most (four classes, two meshes, two passes). No see-through pass is added anywhere.

**What is cut first for trees,** in order:

1. The pool: 9,600, then 6,400, then 3,200.
2. Sprigs a square metre: 60 down to 40, each leaf a third larger.
3. The sprig reach: 8 m down to 6 m.
4. Hand sprigs: twelve plants down to four.
5. The full level's reach, 18 m down to 12 m; pine and spruce at 900 triangles.
6. The dressed kit's middle band, back to the room's mesh.
7. Lying leaves, 400 down to 100; leaves in the air, 96 down to 32.

**For grass:** the meadow's tall share, one blade in four down to one in eight; then the tall kinds' shares halved. **Never cut:** the kinds (a table read), the kept masses, and falling leaves below 32, which are his words.

## Packets

The proposal's FLORA-22, FLORA-23 and R4-11 are taken in the brief (`docs/runs/RUN-4.md:317,318,321`), so new packets are LEAF-n. None edits a contract, the kernel, a rules pack, a collision row, `N/Look/NatureLook.cs` or a pinned preset. No lane L packet writes under `N/Room/` but LEAF-12: `Room/**` is lane P's (`docs/runs/RUN-4.md:260`). LEAF-17 is the one exception: it is the larder's, lanes I and Q, in the kernel's food files and the rules v6 draft.

**One editor at a time,** as the dials page lists it for all three pages; these are this page's lines of it. `S/painterly_land.gdshader`, all in lane L's hands: LEAF-14, DIAL-14, LEAF-7, LEAF-4, LEAF-8, LEAF-9, with LOOK-6 between two of them and never beside one. The larder's files: FLORA-8, DIAL-8, DIAL-18, LEAF-17. The four seam files under `N/Seams/` land with DIAL-1; no packet here creates one, and a field the ground's word still lacks goes in by the integrator's diff.

**Switches.** `--look-flora-off=` takes a list of `leaves`, `dress`, `kinds`, `sway`, `year`, `drop`, `touch`; `run-room.ps1 -FloraOff` passes it. One switch row, "Leaves and grasses", is registered by LEAF-2 from its own file in SOUND-2's settings model, where the dials' rows also live. So he flips it in play and before play, and no file of lane U's is opened. If SOUND-2 has not merged, the row waits and the launch flag stands. The pick is this machine's: it is kept in the profile DIAL-2's first pick uses (`N/RoomHud.cs:22-23`), in a `[flora]` section of its own, and stays there when DIAL-7 retires the dials' pick. No switch parts the eye from the ear. With `kinds` off the word says `meadow`; with `year` or `drop` off the word answers from the seam's default and the ground keeps LEAF-14's litter tint, the one the feet crackle on.

| Id | Title | Lane; after; needs | Owns | Fails before, passes after |
|---|---|---|---|---|
| LEAF-15 | The grass files name no shipped work | L; none; none | Comments only: `N/Look/GrassLook.cs:12,15,23,26,46,106,132,139,145`, `S/grass_blades.gdshaderinc:3,8`, `S/grass_stones.gdshader:5` | One commit on today's code, before any other. Each comment says what the code does and names no work. Proof is one search in the report, not a test: a test that lists barred titles would put them in the tree |
| LEAF-6 | Seams on the copies view | P; none; headless | `N/Room/PlantCopiesView.cs`, one commit of about thirty lines | No partial method: one may have a single body, and a partial file would hand lane L the class's private state. Instead: a static mesh seam, asked once for each of a set's meshes (`:135-136`), where null keeps the room's; an event when a tile fills or empties (`:403-455`), with the set, the instance and the band; an event when factors change (`:651-678`), with the ids; a read-only list of sets and their nodes. A test subscribes and counts; the plant suites pass unchanged. FLORA-6 needs a query, not an event, and stays P's own edit |
| LEAF-2 | Leaves near the eye (his) | L; LEAF-6; pairs, eye | New `N/Look/Flora/Leaf*.cs`, `FloraTable.cs`, `FloraGlobals.cs`, `S/flora_sprig.gdshader`, `FA/kit.json`, `FA/.gitattributes`, `LT/Flora/**`, `game/tests/native_look_flora.tscn`. By diff, the integrator's: one line in `N/RoomWorld.cs` after `:208`; a runner line; `-FloraOff` | Headless: the reader takes a hand-written test file to this page's schema and refuses each broken one. Never over 9,600 sprigs, counted as nodes and surfaces. No plant outside the sprig reach holds a sprig and none inside lacks one. A plant at factor 0 wears none; one at 0.5 wears them at half size. The leaf mass is the surface whose material's `material_role` is `foliage` (`N/Look/MaterialLibrary.cs:134`), or whose name ends so with no preset (`:81-85`). A tree set with no row dresses from the kit row of its fuel class. The same picks on every load; with `leaves` off no node is added. It registers the "Leaves and grasses" row, which flips the same switches in play. GPU: sprigs flutter under the present wind; three pairs on and off (under the nearest tree looking up, the spawn, the densest wood); ruling 5's rule. Today no leaf has an outline |
| LEAF-13 | The kit dressed (his) | L; LEAF-2; pairs, eye | New `N/Look/Flora/KitDress*.cs`; the `dress` rows of `FA/kit.json`; `LT/Flora/**`. By diff, the integrator's: one line in `N/RoomWorld.cs` before `:144`, where the room is built | The seam swaps a set only when its prototype is `broadleaf`, `conifer` or `shrub` and its triangle counts are the kit's (528, 120, 96), so a made tree is never touched. Full and middle bands are dressed; the far band keeps the room's mesh. Headless, on every row of a test room: under each cap's centre the dressed top is within 1 cm of the cap's top; bounds within 2 cm of the kit's; triangles at most 1.25 times the kit's at the full level and no more beyond; every vertex under a cap is kept. A grove wears one leaf row. With `dress` off the kit draws as today. GPU: LEAF-2's three pairs again |
| LEAF-14 | The year's first forms (his) | L; LEAF-2, DIAL-1; pair, eye | `N/Look/Flora/**`, `S/flora_*`; the meadow's litter lines of `S/painterly_land.gdshader` | On today's season globals (`N/Look/LandSeason.cs:166-188`), which `-Date` and DIAL-2's rows already pin. Spring: the slot wears blossom on the trees `season_bloom` picks (`S/painterly_land.gdshader:388-391`), so near trees bloom as far ones do. Autumn: leaves leave the near sprigs by rank and drop, 32 to 96 in the air. Ground within 0.6 m of a broadleaf takes a litter tint. The mask is read from the default word of `N/Seams/FloraGround.cs` (DIAL-1's), the same function DIAL-3 steps on, at the same shown moment: the one `RoomWorld` holds, as the dials page has it. So eye and ear cannot differ. The holder and the line that feeds the default are the integrator's diff in `N/RoomWorld.cs`, with whichever of the two merges first. Winter: kit broadleaves keep half their leaves, brown. Headless: the counts; the litter mask equals the seam's default on a made-up room. One pair; at most 0.1 ms. LEAF-8 and LEAF-9 replace each form by kind |
| LEAF-3 | Eight grasses: table, map, the ground's word | L; DIAL-1; headless | New `N/Look/Flora/GrassKinds.cs`, `GroundMap.cs`, `FA/grasses.json`, `LT/Flora/**`; one accessor in `N/Look/GrassLook.cs` (the four ranked fields as arrays, `:264-282`, behaviour unchanged). By diff, the integrator's: one line in `N/RoomWorld.cs` after `:144`, which registers the map with `N/Seams/FloraGround.cs` (landed with DIAL-1, not created here) | This page's rows as one pinned file. The rules run in the stated order on a made-up room built in the test, where the answer is known (a pond's reeds, three trees, a camp, a hollow, a top): each kind where its rule puts it; five kinds, one of them tall, within 15 m of its start; the same twice. Once the map is registered the word gives a point's kind, straw, lane, sodden and place, headless and off the main thread; the seam's default still gives the litter until LEAF-9. Under `-Scale` one report line prints the stand-in's shares and the kinds near its start; nothing there is asserted. Today: one kind (`GrassLook.cs:191`) |
| LEAF-4 | Eight grasses drawn (his) | L; LEAF-3, LOOK-2; pairs, eye, likeness | `N/Look/GrassLook.cs`, `S/grass_blades.gdshaderinc`, `S/grass_parts.gdshader`, the meadow's lines of `S/painterly_land.gdshader` | All of "How they are drawn". Each kind sways by its row under the seam's `Gust` (DIAL-1's). A lane's floor is painted by its Ground token. The `reeds` set is drawn with the reed row, so a bed no longer reads inside out. Blades lean from the body and the Gubble. Headless: triangles submitted equal today's; custom bounds reach 0.30 m; blades standing are at least 0.9 of today's, but where a row's lane floor is not turf; in every sward kind a flower stands above the blades 1 m from the 9 cm eye; with `kinds` off, today's one kind. GPU: pairs at the 9 cm eye in open meadow, at the water's edge, at the spawn; within 0.3 ms of before, or the grass cuts run |
| LEAF-1 | Five trees and their perches | F; none; headless | `G/flora/**` | First commit: the brief's FLORA-22 as written (`docs/runs/RUN-4.md:317`). Then sheet version 2: `stubs` and `collar` on a limb order; `climb.seats`, 0 to 3 caps on limb elbows. An unknown field still fails (`G/flora/sheet.py:74-75`). `oak`, `birch`, `spruce`, `pine`, `apple` pinned once each in clumps; `oak_cards` and the `cards` style stay until D7 closes. Same bytes twice; 1,500, 300, 60 triangles; at most eight caps; oak and apple each hold a cap under 0.45 m; at scale 1.15 none tops 1.5 m. Every cap's mass is kept and wears its mark in all three levels. A test tells the five apart by outline and by perch plan. `make --check` reports 6 plants, 0 stale (`tools/test-room.ps1:83-85`). Today: one species (`sheet.py:14,56-58`) |
| LEAF-16 | Channels and the trees' table | F; LEAF-1; headless | `G/flora/**`; new `FA/trees.json` | Sheet version 3 takes `leaf`, `bark`, `wind`, `year`, `find`. Each leaf vertex of a pinned record carries its twig point, rank, kept mark and sway weight, and `flora.levels` hands them on; a kept vertex has sway 0. `make` writes `FA/trees.json` to this page's schema and `make --check` holds its bytes. One example file passes both the Python check and lane L's reader |
| FLORA-2 | Plants exported, channels and all (kept, amended) | I; LEAF-1; headless | `X/copies.py`, `X/trees.py`, `X/mesh.py`, `X/exporter.py`, `X/tests/**` | First, one headless import of a three-triangle mesh proves the engine keeps two texture-coordinate sets and the colour's alpha through the mesh load (`N/Room/PlantCopiesView.cs:728`); if not, the packet stops and says so. A kit-only room exports the same bytes. The generator marks a made prototype with an `x_flora` key, which the package allows (`pipeline/landscape/harness/common.py:90`). The exporter then takes levels and channels from `flora.levels` and collision from `flora.placed`: not the package's one mesh (`X/exporter.py:134-137`), `lod_meshes` (`X/copies.py:343-349`) or `trees.fit` (`X/trees.py:227-234`). No harness edit. Leaf surface: set 0 is the twig point's x and z, set 1 its y and the rank, a negative rank marking a kept mass. Bark surface: set 0 runs round and along. Alpha is one minus the sway weight, so today's 1 (`X/mesh.py:87-88`) is rigid; until LEAF-16 lands, every vertex goes out so. Kind, fuel class and seeds come from the record; species is the record's own, but `apple` goes out as `broadleaf`. A record of kind `bush` exports as a plant that collides nowhere (bark and leaves make a tree today, `X/trees.py:13-16`), tested on a made-up record |
| LEAF-5 | Fewer and greater (replaces FLORA-20) | H; FLORA-2; headless, eye | `G/generate.py`: the tree loop (`:1425-1456`), `Planter.put` and its kit-only lines (`:1234,1296-1297,1322`), `tree_crown` (`:1222`). `G/checks.py`: `obstacles` (`:749-750`) and one check. By diff, lane C's: the salt in `G/tests/test_generator.py:895` | Magnified rooms only. One made kind a patch, by a noise field 3 m across: birch, oak and apple on the broadleaf's odds, pine and spruce on the conifer's (`:1436-1437`). Spacing 0.42 of the kind's spread: trees fall to about a quarter, and the report prints the count; copies never rise. Ages, orchards, the gate rule and the first climb as "Ten trees" has them, each counted in the report; a camp with fewer than 24 apple trees is named. A made trunk is the walker's obstacle, its radius from `flora.collision`; a test shows one blocking an 11 cm lane. Three tree perches a new bar reaches stand within 10 m of the start, or the check fails. The salt is picked again (`:42-47`). The `CHECKS` line (`G/checks.py:2048`), the gates and `-Scale` pass; true-size bytes unchanged. Today `tree_crown` knows only the kit |
| LEAF-7 | Boughs that move; bark by kind (beyond) | L; LEAF-13, DIAL-1; pair, eye | The vertex step and the bark and leaf roles of `S/painterly_land.gdshader`; `N/Look/Flora/**`. It reads `N/Seams/Gust.cs`, landed with DIAL-1, and creates no seam | Trunk, bough and mass move by the kind's `wind` under the seam's gust: the dressed kit at once, made trees once their channels are out (LEAF-16, FLORA-2). A tile takes its kind's row as it fills (LEAF-6's event), as it takes its season habit today (`N/Room/PlantCopiesView.cs:426`). A mass inside the sprig reach draws in and darkens, measured from the main camera's eye, so a shadow never changes as the player nears. Bark shows its relief within 2.5 m. Headless, on the rule mirrored in C#: a kept vertex moves 0; in a lull no vertex moves over a tenth of its travel. GPU: two frames half a second apart, kit grass, no water: trees and their shadows differ, nothing else. At most 0.2 ms. Off: `sway`, and every tree stands as today |
| LEAF-8 | The year on every plant (his) | L; DIAL-1, LEAF-2, LEAF-4; a year strip, eye | `N/Look/Flora/**`, `S/flora_*`, `S/grass_*`, the leaf lines of the land shader; one line of `N/Look/LandSeason.cs` | `plant_year` follows the room's one shown `DialMoment` as a from, a to and a front. The front's centre is the body, or the place a setter names: DIAL-10 calls it for a turn at the stones, and lane L wires that line with whichever of the two lands second. Pinned headless at the eight stations' pinned days: each kind's phase, height share and act are its table's. A hundred turns in a row add no node and cost the look's step under 2 ms each. At every station and level the drawn top under each cap's centre is within 1 cm of the cap's top. The check of these rows against `food.calendar` is DIAL-8's one test, on the `slot`; this packet keeps none of its own. By place, the hand runs half a station early at the `spring_line` and late on a `summit`, by the ground map's own measures. Grass stands as stubble in the seam's mown places from `flush` on: the list DIAL-13 fills with the villages', empty until then, so nothing is mown and either packet may land first. The front is never drawn and cannot be held. A set with no row draws as today. One camera, eight stills. The dressed kit turns by rank at once, made trees once their channels are out. Off: `year` |
| LEAF-9 | The drop, the litter, the ground's word (his) | L (S reads the word); LEAF-8; pair, eye, ear | `N/Look/Flora/**`; the ground's lines of the land shader | By kind, in place of LEAF-14's forms. In the air 32 to 96 leaves, 288 for 3 s after a turn; 400 lying; two draws. The litter map is a pure function of the room, the tables and the station, with the smear rule above. It is registered with the seam in place of the default, and the word then gives `leaf_litter` or `needle_bed` and its amount by kind, headless. Lying leaves fly from running feet and gather in the lee, as a drift at most knee deep. Nothing but a leaf, a petal, a key or a flake lands within 0.3 m of a body. Off: `drop` |
| LEAF-10 | Touch and the toys (beyond) | L; LEAF-4, LEAF-7, DIAL-1; pair, eye, likeness | `N/Look/Flora/**`, `S/grass_*`, `S/flora_*` | The press map decays by kind. The trace map keeps a trodden cell until the cover changes; it is registered with the seam as the word's `trodden`, and the feet's own trail of prints then retires (the sound page). A shake is read in the physics step, from `Landed` and the body's own slide collisions (`EntityAt`, `N/Room/PlantCopiesView.cs:346`), eight at once at most. It drops nothing that can be picked, eaten, counted or kept, and wakes no creature; dew falls as spray, never one bead; nuts never follow it. A risen factor unfolds its plant. A toy a row, each with its phase. Nothing saved, nothing for the companion, no collider moved. Off: `touch` |
| LEAF-11 | Five more trees; the bush kind | F; LEAF-16; headless | `G/flora/**`, `FA/trees.json` | Willow, maple, beech, thorn, alder pinned; the model `troll`; a kind `bush` (stems from the ground, no collision; refused today, `G/flora/sheet.py:45`) with hazel, gorse, bramble |
| LEAF-12 | Kinds as sets; reedmace | H, L; FLORA-3, LOOK-23, LEAF-4; pairs, eye | H: the grass rows of `G/habitat.py`. L: `N/Look/GrassLook.cs`, `S/grass_*`; `N/Room/PlantCopiesView.cs` under ruling 14 | The planter sows the ten as sets by niche; a tuft's kind is its set's species, and the map serves older rooms. Ten sets must not be ten draws (a set is a MultiMesh a tile today, `N/Room/PlantCopiesView.cs:423,468`): every grass set of a cell goes in one, the kind in the instance's custom data. Proof: on a test room of ten grass sets, grass draws a camera are no more than with one. Reedmace draws as straps and heads, to 40 m |
| LEAF-17 | Grain, down and haws (beyond; what this page first asked of FLORA-8) | I, Q; DIAL-18, LEAF-12; headless, likeness | Three kinds and their rows of `food.calendar` in the rules v6 draft (lane I's); a few lines of `N/Kernel/CommandHostFoodLayout.cs` and `N/Room/FoodView.cs`, last in the larder's queue | By the path apples take, at the dials page's stations: grain from `meadow` at station 4 and from `oat_grass` at 4 to 5; down from `reedmace` at 6 to 8; haws from `thorn` at 6 to 1. A grass bears by its set's species, which LEAF-12 gives. Each kind is a row of the calendar DIAL-8 built, so a hundred turns pick no unit more, and DIAL-8's test of `slot` against calendar passes with these rows in: grain and down show as `head`, haws as `fruit`. Harvest is cut, dried and bundled; no blade becomes a carried plank. With the rows absent, bearing is as before. Today no grass and no thorn bears |

**The planned packets.**

- **FLORA-2:** kept, amended. **FLORA-20:** replaced by LEAF-5; WORLD-8 waits on LEAF-5 in its place.
- **FLORA-21:** split. LEAF-1, 16 and 11 take the ten trees and three bushes; FLORA-21 keeps the veterans, the flagged forms, currant and bilberry. FLORA-3 waits on LEAF-1 and LEAF-11 too.
- **FLORA-22:** LEAF-1's first commit. **FLORA-23** (the cards oak): held, not built, until D7 closes.
- **FLORA-7:** split. Grasses are LEAF-3, 4 and 12; foliage and sway LEAF-2, 13 and 7; winter and the year LEAF-14, 8 and 9. Only the sun-followers keep the id, behind LOOK-5.
- **FLORA-8:** kept as the brief wrote it (fruit and nuts, with CR-2's `nut`), first on the larder's files; DIAL-8's calendar follows it, then DIAL-18. What this page first asked of it is LEAF-17, last in that queue, since kinds picked by station need the calendar: grain from `meadow` and `oat_grass`, down from `reedmace`, haws from `thorn`. LEAF-17 carries the likeness token: harvest is cut, dried and bundled, and no blade becomes a carried plank.
- **LOOK-2:** kept, amended: it carries LEAF-4's kinds through its cells; its "nothing past 12 m" is the turf's, and tall stems keep the parts' 20 m (`N/Look/GrassLook.cs:96`).
- **FLORA-9, 10:** kept; they may take LEAF-6's events and leave the view alone. **FLORA-3, 6:** kept.

**Asks of the brief.**

- **Ruling 5:** the measured-pair rule and its floor. He asked for leaves and grasses by name on 24 November.
- **Ruling 14:** LEAF-6 goes first in the queue for `Room/PlantCopiesView.cs`; LEAF-12 joins it behind LOOK-23.
- **D7:** closes at LEAF-2's sitting, on one switch. If sprigs read as fur, FLORA-23's cards are the fallback.
- **Ownership:** `FA/` is new: `trees.json` to lane F, the rest to lane L. `N/Seams/FloraGround.cs` and `N/Seams/Gust.cs` are the integrator's and land with DIAL-1, as the dials page's table of four seam files has them. The flora's shader globals are registered at run time in one file, as the grass's are (`N/Look/GrassLook.cs:225-240`); the integrator reviews the list.
- **Sets:** three more trees and reedmace make 58 of 64.
- **Beyond this page:** a pinned preset's description (`game/styles/storybook_painterly/v1.json:7`) and a look test's comment (`LT/LookPresetTest.cs:1465`) also name shipped works. No LEAF packet reaches them.

**The sibling pages: settled on 25 November.** The dials page's section "Where the three pages disagreed" holds the rulings.

- **Dials:** `litter01` is the broadleaf default, and a kind's own `litter` row wins. This page's keys are tokens in its one table. Its grass table takes reed and reedmace and marks savannah and dune grass as waiting. Phase rows live here; its Plants table is a copy, and the draft for the bushes. A falling nut is DIAL-18's windfall and never follows a shake. Picking by kind is rules v6; uses wait for v7, which is K. Its litter default reads a copy's fuel class. The year by country and the hay round villages are LEAF-8's. What was asked of FLORA-8 is LEAF-17, behind DIAL-8 and DIAL-18. From the second reading: the eye reads the room's one shown moment, the one the ear reads. Mown ground is a list on the seam, which DIAL-13 fills and LEAF-8 reads. The front starts at the feet, or at the fire for a turn at its stones. The slot gains `head` and the finds `keys` and `old_cones`. Dew is shed as spray, never as a bead. Its copy of the oat-grass row says straw from 6 to 1.
- **Sound:** a `needle_bed` is its quietest wood floor; one cue for a shaken bough; the hush lifts in half the time for a body standing still in `oat_grass` or `reed`; its stands sample `N/Seams/Gust.cs`; a lane sounds as its own ground, and a `grass` lane as `path_turf`; trodden is the trace map's, never the press map's. From the second reading: a `needle_bed` is ground and no cover, so rain still wets it; `grass` with no kind named steps as `meadow`; a drift bursts under running feet, never a heap.
- **Body:** the spotter (BODY-12) lights a cap as it lights a ledge. Still an ask.

**What ships first.** LEAF-15 at once. Then, side by side on today's code: LEAF-6 (P), LEAF-1 (F), and in lane L LEAF-2, LEAF-13 and LEAF-14 in that order, with LEAF-3 headless between pairs. LEAF-4 follows LOOK-2. FLORA-2 and LEAF-5 go in the next generator batch, LEAF-16 with them or after. In the dials page's one order, LEAF-2 is in build one beside the sound's first aid, and LEAF-13 and LEAF-14 in build two with the dials' rows and the ear's first litter. **A first playable is** leaves, the dressed kit, the first forms, eight grasses and five made trees. Later than that: the `troll` model, seven bark reliefs, turning from skin or core, the litter's wind smear, the toys.

## Provisional, for him to feel in play

Five things to look for, and where to stand:

1. **Under the nearest tree, looking up:** leaves, or fur? Do they move?
2. **From the start, facing the nearest wood:** limbs and sky between the masses; conifers in tiers; bushes on stems; one leaf a grove.
3. **The year row in Settings** (or `-Date`): at `leaf_fall` leaves drop and lie where the feet crackle; at `high_spring` the near trees blossom; at `deep_winter` the wood is still a sight.
4. **At the nearest water, walking outward:** rods, then tussocks on pedestals, then meadow. Then walk into the tall stand in the nearest hollow: is the body still in view, and is there a way through?
5. **On the "10x Island":** tell oak, birch, pine and spruce apart at 30 m; climb a young apple; sit in an oak.

The numbers behind them, all provisional:

- Leaf sizes (1.2 to 4.5 cm) and the twentieth rule; 60 sprigs a square metre; the pool; 8 m, 5 m and 1.5 m.
- A quarter as many trees; one in four young; 24 apple trees a camp; every sheet number; the gate rule's 1.2 m.
- Every grass height, clump, rate and share; meadow to the knee and oat-grass overhead; lanes 0.2 m wide; the eye's clearing of 0.06 m.
- Every phase row; the `held` shares; which trees turn from the skin; 32 to 96 leaves in the air, 400 lying; press times; a 1.5 s shake; a turn that crosses in 3 s.
- A drift at most knee deep; half a station early at water and late on a summit; which lanes are sodden; stubble 3 m round a village.

## Likeness

- **Grass taller than a small body, cut and used.** Alike: backyard-scale games and old tales walk under blades, and one game cuts blades into building stock and knocks dew from them. Ours: ten botanical kinds, each a sign of the ground it stands on; the folk's own crafts (thatch, cord, a rush lamp); a year on each kind. No blade becomes a carried plank, and no bead of dew is knocked down to drink. Fallback from nature: the grasses are read, and only the folk harvest them.
- **Blades in clumps under one travelling gust.** Alike: a recent open-world game's grass fields and the talks about them. Ours: outlines from botany (rods, three-edged stems, pedestals, plumes), a tempo and a recovery for each kind, rings round water. The wind never leads the player to a goal, and no one-kind field of pale plumes is the picture. Fallback: drop the cell noise and plant by habit alone (sward, tussock, stand).
- **A tree that lets something fall when the body meets it.** Alike: a village life game's best-known gesture, where what falls also follows the season; an adventure series' charge at a trunk. Ours: only weather and plant matter fall, to watch; no button on a trunk; nothing is a prize. Fallback: only gusts and rain shake a bough.
- **A hide in tall grass; prints that stay.** Alike: stealth games hide a body from hunters; many games keep trails in snow. Ours: nothing hunts, and stillness only brings the singers back; one trace for frost, dew, snow and hay, read from a height. Fallback: a watcher's stillness anywhere; the dew line at dawn alone.
- **Leaves kicked, drifted and landing on one.** Alike: leaf heaps in many family games. Ours: drifts lie where this land's wind leaves them, by kind and station, knee deep at most; none hides a prize and none promises a landing. Fallback: leaves that only skitter in gusts.
- **Magic that makes a plant leaf.** Alike: growth magic in many games and tales. Ours: it draws the kernel's own Grow and adds no power. Fallback: a seedling leafs as it grows, with no flourish.
- **The year on every plant under a dial.** The dials page's line covers it. Here no phase opens or shuts a way.

LEAF-4 and LEAF-10 carry the brief's **likeness** token, and so will each pick or make packet when it is written.

## Research notes

As the research marked them; the editor opened none of these pages.

- Smooth edges on cut-out leaves need a sampling the game does not run. https://docs.godotengine.org/en/stable/tutorials/3d/3d_antialiasing.html
- Branching models. https://harvardforest1.fas.harvard.edu/publications/pdfs/Tomlinson_AmScientist_1983.pdf
- A tree's sway follows its branching; bare crowns sway faster. https://pmc.ncbi.nlm.nih.gov/articles/PMC6597771
- Oak and beech keep dead leaves through winter. https://en.wikipedia.org/wiki/Marcescence
- Autumn colour starts in a maple's outer crown. https://frontiersin.org/articles/10.3389/fpls.2022.1006413/full
- Falling leaves flutter or tumble by their size. https://www.nature.com/articles/40817
- Telling grasses, sedges and rushes apart. https://extension.illinois.edu/blogs/grasses-glance/2023-04-17-telling-apart-grasses-sedges-rushes
- Thatch; a rush pith lamp. https://en.wikipedia.org/wiki/Thatching ; https://en.wikipedia.org/wiki/Rushlight
- A ball nest woven in tall stems. https://en.wikipedia.org/wiki/Eurasian_harvest_mouse
- Unlinked, from memory or inferred: each tree's branching model; oak turning from the skin; alder dropping green; galls; mistletoe on apple; how fast each litter rots.

## Not verified, and open

- No test and no likeness check was run. Every cost is arithmetic on the fixture's tables and the ledger's slope; a pair settles each.
- **The second reading of 25 November was of the three pages' text.** It opened no flora code. Two things it left open are on the dials page's list: whether `Cover` can tell where the look lays snow, and the spruce's dry tent, which no field of the ground's word carries. The bushes still have no table here; their rows stand as the dials page's draft until LEAF-11 and FLORA-21 write them.
- The editor opened the lines cited, at the commit above, and counted the stand-in's tables with Python: the sets, the trees' triangles from two cameras, the pool's reach, the trees near each of 13 camps. Not read: `G/flora/grow.py`, `X/landwork.py`, `N/Room/PlantCollision.cs`, the spotter's code.
- **Whether sprigs on lobed masses read as leaves or as fur:** LEAF-2's first pair.
- **Whether the kit's caps leave room for forking limbs and lobes:** LEAF-13's headless test. If not, a ball keeps its outline and gains only limbs.
- **Whether the engine keeps a second texture-coordinate set and the colour's alpha:** FLORA-2's first step. A custom channel is no fallback: the import keeps none.
- **Whether a made tree's perches open a gate early:** the gate rule is a guard until BODY-5 gives the reach flood crowns.
- **Whether the spotter lights a cap as it lights a ledge** (BODY-12): not read. A perch's mark is this page's own answer meanwhile.
- **Whether LOOK-23 merges sets in a tile:** LEAF-12 leans on it. If not, the kind map stays the look's source and the sets serve the kernel only.
- **The first climb** speaks of Challenge; whether Creative binds the bar was not read. The pinned oak's lowest rim is taken as 0.69 m and was not measured here.
- **The kind map on his garage's island:** shares were counted on the stand-in only. A count there gives eight shares in percent, kept in the lane's report: no area, no position, no object kind.
- **Never timed:** draws with five tree sets; the fill cost of tall blades; a hundred turns.
