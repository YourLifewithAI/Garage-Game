# The garage as land: the landscape generator (C5)

## The firepits (Run 2, Lane C, 24 October)

Decision 74 (firepits spread evenly over any island) was the kernel's own rule until now (`CampLaws.SpreadFirepits`). The generator
now chooses them (`firepits.py`) and the exporter hands them to the game as the manifest's `x_firepits` (spots only); the kernel reads
them, checks each on the island and fills a left-out place with its spread. The rule: one per 250 m2 of standable dry land (3 to 12,
the kernel's count), at least 4 m apart, 1 m from every Fractal, 3 m from the starting campfire (3.2 m from the spawn, whichever way
it faces), on a node the strict walk stands a body on (never water, a steep face, a trunk, a stone, a cottage, a flight, a path or the
jetty's landing), one near the hamlet; the rest by Lloyd's relaxation over the land (the starting campfire and the hamlet's firepit
fixed), each on the ground of its share nearest the share's centre, first-run ground (band 1) counting 5 m nearer. Every claim (the
spot standable, on the land, its band) is made on the package as it is read back, single precision and the land grid read from its
mesh, so `checks.firepit_checks` (in `checks.run`'s `ok`) proves it on the written package. Every island gets them, the 1x corpus
too: its other outputs are byte-identical (the 1x and 10x garage compared file by file, and `package.json` without
`x_generator.firepits`, against `63f1846`).

- **The 10x garage:** 12 firepits over 2,992 m2 (the kernel's probe finds 3,042 m2), 8 within first-run reach, the nearest two 11.4 m
  apart, every land point within 22.4 m of a campfire (the farthest the headland past the walls' north-east corner, where the strict
  walk stands nobody, so no firepit can go); the kernel keeps all 12 (its log: every land point within 22.3 m, the same corner; its
  own spread had 20.9 m). The firepit stage costs about 12 s.
- **The 1x garage:** 3 firepits over 27 m2, all within first-run reach, the nearest two 4.1 m apart, every land point within 3.0 m of a
  campfire.

```
python -B -m unittest pipeline.landscape.generator.tests.test_firepits -v
python -B -S -m unittest pipeline.landscape.export.tests.test_fractals -v
```

## The follow-up: the summit waits, the Fractals spread, why the strict walk reaches a fifth (Run 2, packet M4's follow-up, 15 October)

The founder's answers of 15 October (`docs/game/MECHANICS.md` decisions 22, 27, 35 and 36), built at world scale 10 only: the 1x
corpus is byte-identical (the garage, `near_empty_nominal` and `kitchen_scan_17` compared file by file against the generator before
this follow-up), and the 10x fixture rebuilds itself on the next runner.

| File | What changed |
|---|---|
| `gates.py` | `own_band(budget_max)`: the highest band the island's own Fractals can open (every one spent on a band, `PER_BAND` = 5 a band, from band 1: 7 on a ceiling of 30). `stair_bands(last_band, own)`: the bands to add above a corridor so its top needs more |
| `sites.py` | `plan_summit(own_band=)` and `plan_tower`: the stair built above the corridor's platform; `summit_radius` |
| `routes.py` | a flight can be a `stair` (marked in the package's route row); `Route.tower` |
| `fractals.py` | the budget (25 to 30, 15 first run, `per_band` 5); the stair's Fractals; `lift_places` (several places only the Gubble's lift or a built ladder reach, spread over the island) in place of the one `high_unreached`; the counts (`first_run`, `out_of_reach`, `by_why`), the shortfall notes and the walk's coverage in the block; `summit_record` |
| `generate.py` | the package's `x_generator.summit` (`center_m`, `radius_m`, `landmark`, `top_band`) |
| `checks.py` | `fractal_checks` proves the summit record on the written package; `stair_rects` and `footprint_checks(stairs=)` read a landmark's footprint without the stair built over it |
| `climb.py`, `reach.py` | `face_deg` (default 55, unchanged): the steepness of a face the climber grabs; the body's own is 45 (`SmallPlayerController.Climbable` is any face past what it stands on) |
| `coverage.py` | new: `python -B -m pipeline.landscape.generator.coverage --package <pkg> --room <room> --world-scale 10` measures the same island with the strict walk and with the body's own limits |
| `../export/fractals.py`, `exporter.py` | `x_generator.summit` to the manifest's `x_summit` (four fields, bounded, nothing else); `contracts/validate.py` already checks it lies inside the island |

**The summit waits for a return visit.** The rule, in the generator and for any island: the corridor's top must need more than the bands
the island's own Fractals can open. A corridor is cut into the tallest landmark in reach as before (its lower ledges are unchanged: on the
garage the couch, bands 1 to 5 and a remainder ledge). Where its top ledge needs no more than `own_band`, a stair of ledges one band more
each (the gates: 2.38, 2.78 and 3.18 m for bands 6, 7 and 8) is built above its platform on the landmark's top, across the corridor's
axis, on ground within 0.5 m over and 2.5 m under the platform and clear of everything but the landmark's own protected ground (the
flatter of the two sides) and leaves no ledge of walkable ground beside it narrower than 0.6 m (`sites._ledge_ok`: the narrowest of 1.4, 2.0,
2.6, 3.2 and 4.0 m that does not, so a stair on a crest takes the crest's width: on the garage 2.0 m). Every ledge is a bench 1.2 m long (the top 1.6 m)
the exact walk stands on, each proved: the stair's ledges are reached from bands 6, 7 and 8, a knack at full rank from 5, 6 and 7. A landmark with
no room for a stair keeps its corridor, and the notes and the report say the summit is within what the island's Fractals open.

**Why the stair is as wide as the crest (the game's navigation bake).** The first stair was 1.4 m wide and left a 0.3 m ledge of crest ground between
its north wall and the crest's north rim: the game's navigation bake (Recast, a 2 cm cell) triangulates a walkable ribbon 10 cm wide as overlapping
slivers (duplicate triangles along the wall's foot, 5 edge keys with three or four polygons), and Godot's map answers "Navigation region
synchronization had 8 edge error(s)", which fails the scale suite's runner (its 10 checks still pass). Not a plant-salt layout: the same 8 under salts
1 to 3, and a clean island with the stair left out (or with one ledge, on the west). Found by listing the edge keys more than two polygons share in a
copy of the game's bake (nothing of `game/` changed here); the rule is the generator's, so no stair leaves such a ledge. At 2.0 m the stair's wall
stands at the rim and the bake is clean (scale suite 10 of 10, no warning, planting salts 1 and 3; salt 2 fails the suite's walk across the island, as
a layout of its own).

**Why a stair and not a longer corridor.** No landmark on the garage hosts a cut corridor to bands 7 and 8. A ladder to band 8 climbs 14.24 m
(every gate from band 1) and runs about 21 m. The tall landmarks, the shelf (20.2 m) and the easel (16.5 m), lie in the unreached south: the
shelf is a crag whose face is 3 m deep (the flight would stand 10 m or more up from the plain, where a corridor may stand 2.5 m), the easel a
spire with no top to stand 1.6 by 1.4 m on. The couch (8.3 m) and the table (7.2 m) are the only landforms the corridor search cuts into, to
7.6 m (band 5). So the stair stands on the couch's crest. **Its price:** the room's reading rule (every object's footprint reads in the land,
its top within a third of its height of the box's) lost the couch under a stair 8.7 m over its box, so the rule is read without the 2.0 by 7.6 m
under the stair (`checks.stair_rects`); the rest of the couch reads. An alternative the rule allows is raising the couch's own landform by at
most 3.7 m (bands up to 7, not 8): a change to the geology, not made here.

| The 10x garage, nominal, seed 20261008, `--tutorial` | before | after |
|---|---|---|
| Fractals (budget) | 24 (20 to 25) | **28** (25 to 30) |
| within a first-run body's reach (band 1, no knack) | 15 | **15** |
| out of reach yet | 9 | **13** |
| by the lowest band that reaches them | 1: 15, 2: 3, 3: 3, 4: 1, 5: 1, none: 1 | 1: 15, 2: 3, 3: 3, 4: 1, 6: 1, 7: 1, 8: 1, **none: 3** |
| by where they rest | steps 8, corners 5, ladders 7, shortcuts 2, summit 1, high 1 | steps 8, corners 5, ladders 7, shortcuts 2, **stair 2, summit 1, high 3** |
| the summit | a corridor through the couch, 6 ledges, bands 1 to 5, 7.62 m, its top reached from band 5 | the same corridor and **a stair of 3 ledges (bands 6, 7, 8)** on the couch's crest, 15.96 m above its foot; the top ledge needs band 8 and is reached from band 8 (one knack 7, the pair 6); `x_summit` centre (-13.42, 17.03, -30.92), radius 1.5 m (the top bench is 1.6 by 2.0 m), `top_band` 8 |
| benches and routes | 23 in 13, 22 proved | 26 in 14, 25 proved (the one not as built is as before: the corridor's fifth ledge, which the couch's seat terrace offers a way round) |
| the strict walk's reach | 832 m2 (21 %) | 829 m2 (21 %) on the written package (845 m2 on the feet alone, 906 with the climbs the generator's rule knows, as the packet counts them): the follow-up leaves the walk's reach as it was |
| generation (back to back on a quiet machine) | 267.8 s (stage lines: 60.7 s to the Fractals, 143.8 s to their reach, 179.6 s written) | 275.7 s and 278.6 s (two runs; 60.8 s, 151.0 s, 186.2 s): 8 to 11 s more, 3 to 4 percent, about 4.6 minutes. The Fractals' stages cost 3.1 and 3.0 s (against 3.1 and 2.4); the rest is the planting round the stair. The runner's fixture build: 283.6 s generated, 36.6 s exported |
| checks | `overall` true | `overall` true (water, grounded, walk, footprints, sea, Fractals; the climb principle is reported beside it, as before) |

**Where the 13 out-of-reach Fractals rest.** The gated ones lie where a gate can be proved, on ground the strict walk stands on: the two
ladders (5 above band 1), the two knack shortcuts, the stair and the summit (the north and north-west, the walked fifth). The three lift places
are spread over the island: the **shelf's crest** (26.1, 20.9, 29.6), 20.9 m up in the south-east, the **table's top** (-12.9, 8.0, 11.6) in the
west of the middle and the **bin's top** (25.6, 5.6, 11.1) on the east. Each is a plateau cliffs close in all round whose rim stands 4.5 m or
more over the ground at its foot (`rim_drop`: more than the top band's 4.0 m bar, so no base body climbs it at any band), ground the body stands
on, nothing planted in its lane, no band reaches it, 6 m or more from every other Fractal. A first-run player cannot reach any of the 13: they
need a higher band, a ladder, the Gubble's Bubble lift or Build. Ids are still the hash of the place, never a count.

### Why the strict walk reaches a fifth of the island

Measured with `coverage.py` on the written 10x garage (the walkable floor's dry land is 4,010 m2 by the tool, 3,990 by the generator's own count):

| walk from the player's spawn | m2 | share |
|---|---|---|
| **strict** (20 degrees, 2 cm steps, dry, an 11 cm lane clear of every trunk and stone): what every claim is proved on | 829 | 21 % |
| strict, no planting | 1,102 | 28 % |
| the body's limits: 45 degrees, 4 cm steps, the planting in the way | 1,929 | 48 % |
| the body's limits, no planting, no water in the way | 2,607 | 65 % |
| the body's walk and its climbs (every face over 45 degrees a bar of the band climbs), the base bar | 2,758 | 69 % (2,622 of it from band 1; the strongest pair 2,850) |

**It is not a bug in the walk, and not in the grading.** The walk is exact (the game's body at 11 cm, 2 cm and 20 degrees, every edge sampled each
centimetre) and the grading does what it is asked to: it joins the hamlet's crate and three doors, the beaches and the jetty to the spawn (and
gives the hamlet's site up when it cannot). The island's real shape does the rest:

1. **Straight scarps at the edges of the landforms' skirts.** The geology (`land.uplift`, made on today's 3 cm grid and magnified to 10x with its
   heights) leaves a skirt round each object landform that ends at the edge of its territory: a 2 to 3 cm step at 1x, a 0.3 to 0.5 m scarp at 10x
   along 20 to 30 m. At x = -15 m the north terrace (1.08 m) falls 0.47 m between z = -3.4 and -2.8 m (faces of 48 to 53 degrees over the lower
   0.3 m) to a channel at 0.61 m and a terrace at 0.75 m; climbing back up from the terrace is 0.33 m. The strict walk stops at it (20 degrees), and
   the generator's climb rule does not see it either (it grabs faces over 55 degrees), though the game's body does: `SmallPlayerController.Climbable`
   is any face steeper than the 45 degrees it stands on, so this scarp is a band-1 climb in the game and no climb to the generator. It is why the
   walked region is the north, bounded by the straight line at z = -3 m from the west coast to the middle (and by the next scarp at z = 6 m east of it).
2. **The planting** costs 25 percent of what is left (1,102 to 829 m2): it keeps lanes only to the goals it knows (the hamlet, the shore, each
   flight's foot), and the south is thicket (16 to 290 m2 pockets when the strict walk is flooded from a seed there).
3. The body's slope limit (45 against 20 degrees) is the rest: with it and the planting the body walks 48 percent, without the planting 65.

**A fix is not small, and the Fractals do not wait on it:** the first-run fifteen are proved on the strict walk and lie where it stands, the gated
ones can only be proved on ground it stands on, and the lift places and the summit's stair do not depend on it. **Proposals, with the numbers:**
- *Count the 45 to 55 degree faces as climbs in the generator's reach* (`reach.ClimbReach(face_deg=45)`, built and measured here, not switched on):
  the base bar then reaches 2,622 m2 on band 1 (66 percent) and 2,758 with every band's climbs (69), against 829 (21). It would let the flights, the
  first-run Fractals and the leads spread over the whole island, each proved from where it stands. The cost is a second model of the body in the
  generator (the strict one stays for the principle checks) and tens of seconds of generation (33 s for the flood and 5 s for the climbs in the
  measurement above, cold); a scarp whose toe eases in over more than the 5 cm the foot rule reads is also missed and needs teaching.
- *Soften the skirts' edges at 10x* (a 12 degree ramp over 2 to 4 m where a territory ends, so the strict walk crosses): a change to the geology of a
  magnified room, the 1x corpus untouched; the strict walk's bound without the planting is 28 percent, so it needs the planting to keep lanes to more goals
  to approach the 48 the body's own slope limit gives.
- *Neither*: the body reaches most of the island already; the generator proves the part it can, as today.

**Decisions and numbers for the founder (this follow-up):**
- **The summit's stair** on the couch (above), the footprint rule read without it, and the alternative of raising the couch by 3.7 m (bands to 7).
- **`budget_max` 30 is a placeholder** and sets what the summit must beat: `own_band` is 7, the summit's top band 8. At 25 the island opens 6 and the
  stair would be two ledges (6 and 7); `per_band` 5 is the rate.
- **Lift places** need a rim of 4.5 m or more: Big bar at full rank on band 10 climbs 4.8 m, so the bin's top (rim 4.57 m) is the one place a body with
  that knack and the top band reaches alone (the Bubble lift reaches it without a knack, as it does every Fractal here). A rim of 5 m would leave the
  shelf's crest and the table's top (5.59 m) and lose the bin's.
- The walk's coverage: which proposal, if any.
- The `contracts/examples` invalid case `rules_fractal_budget_upside_down` now breaks both bounds (`budget_min` 30 above `budget_max` 25): with a ceiling of 30 its old mutation, `budget_min` 30, is valid. One line of `build_examples.py` beyond the grant's `fractals` entry.

```
python -B -m pipeline.landscape.generator.generate --room pipeline/landscape/corpus/rooms/garage_nominal --world-scale 10 --tutorial --reach-report <report.md> --out <pkg>
python -B -m pipeline.landscape.generator.checks --package <pkg> --room pipeline/landscape/corpus/rooms/garage_nominal --world-scale 10
python -B -m pipeline.landscape.generator.coverage --package <pkg> --room pipeline/landscape/corpus/rooms/garage_nominal --world-scale 10
python -B -m unittest pipeline.landscape.generator.tests.test_fractals -v     # alone: it shares the 1x garage with test_generator, whose teardown removes it
python -B -S -m unittest pipeline.landscape.export.tests.test_fractals -v
```

## Fractals and the ladder of gates (the first build: Run 2 mechanics round, packet M4, 15 October morning; the follow-up above supersedes the garage's counts and the summit's)

The mechanics design (`docs/game/MECHANICS.md` sections 6 and 7) needs the land to be a ladder: the stamina bar grows in ten bands of
0.4 m, a climb costs the metres it rises, and Fractals sit on ledges that need a band. Built here, at world scale 10 only (nothing
changes at 1x: the corpus is byte-identical, three rooms compared against the generator before this packet).

| File | What |
|---|---|
| `gates.py` | The bands, the knacks' reach (Big bar, Steady climber: x1.1, x1.15, x1.2; two stack), the two kinds of gate and their arithmetic (`early_open`, `ladder_rises`). Its copies of the pack's numbers are pinned to `game/rules/storybook_wild/v5.json` by a test |
| `routes.py` | A **flight**: a straight run of ledges built into the base grid along one axis, each ledge a riser (66 degrees or steeper, over whole cells) and a flat bench 1.2 m long and 1.4 m wide; every vertex set to the profile, so a freestanding flight has a cliff on every side |
| `sites.py` | Where flights go: flat dry meadow the exact walk reaches (3 cm of relief, 0.8 m clear all round, 1.5 m between flights, 5 m from where the player arrives); and the summit's corridor |
| `reach.py` | The **climb-aware reach**: the walker's flood (`checks.Walker`, exact, unchanged) for the feet, and a climb as one more edge: from a standable foot, a face over 55 degrees followed up its fall line (`climb.climb_from`, its 0.6 m follow raised to the tallest rise a bar climbs, 5.76 m with the strongest pair) to a crest, a standable node beside it the top. A climb of rise r is open to a bar of band b when r is at most `gates.cap(b)`; the bar is full at every foot (it refills on standable ground). A label per lattice node: the lowest band that reaches it; flooded band by band, each node once. A body stretched by a knack is the same run with each bar multiplied |
| `fractals.py` | `plan` (before the 2.5 cm ground: find places, build the flights, keep the planting off them) and `finish` (on the planted island: three runs of the reach, each bench proved, the Fractals placed, the report) |
| `checks.py` | `fractal_checks`, in `overall`: the same reach, read back from the written package, puts every bench and every Fractal at the band it says |
| `generate.py` | `--no-fractals`, `--typed-fractals`, `--tutorial`, `--reach-report <md>`; on by default when `--world-scale` is not 1 |
| `export/fractals.py` | The exporter's whitelist: `x_generator.fractals` to the manifest's `x_fractals` (id, position, kind when typed, `reach_band`), `x_generator.tutorial` to `x_island_tutorial`; nothing else travels |

**The two kinds of gate, as built.** A *band gate* is a rise of the band's full reach less 0.02 m, so the ladder runs 0.38, 0.78, 1.18,
1.58, 1.98 ... m: each ledge-to-ledge rise on a route is one band more than the last, so the next ledge is always in view of a player
one band short (the design's wording is "each ledge-to-ledge rise is one band"; this is the reading that gives the summit its "upper
ledges at the top bands" and the first Fractal a 0.4 m climb). A *knack shortcut* is a higher ledge half way between the base bar and
one knack at full rank (0.44 m over band 1, 0.88 m over band 2): the base body falls short by 0.04 and 0.08 m, one knack clears it by
the same, and the base body reaches it one band later, so nothing is reachable only with a knack.

**Where the arithmetic gives out** (printed in every reach report): a knack at full rank stretches a bar by a fifth, and a band is 0.4
m, so from band 6 on Big bar or Steady climber opens the next gate from the band below, and the strongest pair (both at full rank,
x1.44) opens a gate from band 4 on. Shortcuts are therefore built only over bands 1 and 2, where even the pair does not carry one into
the next gate. The generator builds every gate and reports which a knack opens early; "no knack ever opens a gate early" cannot hold
above band 5 with ranks of +20 percent (a decision for the founder, below).

**On the nominal garage at 10x** (seed 20261008, `--tutorial`; the report is `--reach-report`):

| | |
|---|---|
| Fractals placed | 24 (budget 20 to 25): 8 on steps, 7 on ladders, 5 in hidden corners, 2 on shortcuts, 1 on the summit, 1 no band reaches |
| By the lowest band that reaches them | band 1: 15 (the pack's "reachable on a first run"), band 2: 3, band 3: 3, band 4: 1, band 5: 1, none: 1 (on the tote's top, 4.4 m up, closed in by cliffs: a Bubble lift or a built ladder) |
| Routes and benches | 13 routes, 23 benches: a ladder of 4 (bands 1 to 4) and one of 3, 8 steps (one band-1 ledge, 0.38 m), 2 shortcuts, the summit; 22 benches reached from exactly the band they were built as |
| Knack shortcuts | over band 1: a 0.44 m ledge, base body from band 2, one knack at full rank from band 1; over band 2: 0.88 m, base body from band 3, one knack from band 2 |
| The summit | a corridor of 6 ledges (bands 1 to 5, the last a 1.72 m rise) cut through the couch's south cliff, seat and crest to 7.62 m above its foot; its top is reached from band 5 (one knack: 4, the strongest pair: 3) |
| The one bench not as built | the summit's fifth ledge (built as band 5) is reached from band 4: the couch's own seat terrace beside the corridor offers a way round it; reported, not a fault (`fractal_checks` allows it for a summit only) |
| Ways the land itself offers | none under salt 1 (a 0.29 m step at the north-east coast appears under another planting draw) |
| Added to generation | the stage lines say what the run spent: siting and building 3.5 s (`fractals`), the reach and the placing 3.1 s (`fractal reach`): about 7 s, on a machine other lanes were loading (the whole run took 369 s against 286 s before the packet on a quiet one, so the stage lines are the evidence) |
| Checks | `overall` true with the Fractal check in it; exported and validated against the room manifest schema; the 1x corpus byte-identical (garage, near_empty, kitchen scan 17) |

**What a Fractal is.** An entry of `x_generator.fractals.items`: `fractal:` and the first 12 hex digits of a hash of where it rests
(centimetres, x then z), never a count, so an id says nothing about how many the island holds or which was placed first; the list is in
id order. `reach_band` is what the exact walk measured, not what was built. Untyped by default; `--typed-fractals` gives each a kind
(by the id's last digit, a placeholder: the design says only five per heart and per band). A Fractal no band reaches has no
`reach_band`.

**Built into a landscape that is already finished.** Flights go in after the hamlet and the ways are graded and proved, before the 2.5
cm ground is worked, so nothing a walk was proved on changes. A flight inside a detail block keeps the base's surface there
(`Plan.hold`, the way the ways the body walks do). The planting leaves each flight and 0.5 m round it bare, and each flight's foot is
a goal of the planting walk, so a tree on the way to it is taken out like one on the way to a door.

**Trees and walls.** The first fixture build with flights failed the exporter's tree fit (`export/trees.py`: a tree's climbing crown must
stand 12 cm over the highest ground under it): two trees stood at the foot of a 0.6 m bank, in a layout that differs from the baseline's by
luck (the planting is one random stream). At world scale 10 the planting now keeps a tree off such a wall (`generate.tree_crown`); 1x is
untouched.

**The planting salt.** The layout the packet's changes drew tripped the game's navigation bake (the rare trunk-hole layout of "Navigation
edge errors" below: three bakes with 2, 4 and 2 edge errors, so `tools/test-room.ps1 -Scale` threw on stderr though its 10 checks passed).
A magnified room now draws its planting with `WORLD_PLANT_SALT` (1) unless `--plant-salt` says otherwise (`--plant-salt 0`: none); salt 1, the
first tried, passes the scale suite (10 of 10, no warnings), also from a clean fixture cache. 1x is untouched.

**Other rooms at 10x.** Where the exact walk from the spawn reaches little the packet places little: `near_empty_nominal` at 10x reaches 47
m2 (the hamlet's) and gets no flights and no Fractals (the notes in its report say why); a room with no hamlet is walked once for the siting
(a fallback flood of the graded land). `kitchen_nominal` at 10x stops with an empty `road_pts` in the hamlet's yards, before and after this
packet.

**What the walk does not model, so every claim is a lower bound for the body and every gate claim is made against the same walker:**
slopes between 20 and 55 degrees (the body walks 45), shallow water (the body wades), drops, a hop onto a ledge under 6.5 cm, a tree's
trunk and crown, and the Gubble's help. On the nominal garage the strict walk reaches about a fifth of the island (the north): a ditch
about 0.5 m deep whose north bank is 58 degrees (at x = -10 m, z = -3 m) splits it, so every Fractal sits inside that region; the body,
which walks 45 degrees, crosses the ditch.

**Decisions and numbers for the founder:**
- The gates' reading above (ascending ladders), against "every ledge-to-ledge rise is one band".
- The arithmetic limit: a knack opens a gate early from band 6, the strongest pair from band 4. Options: accept it; make the high ranks +10 percent; or let a knack move only the first bar.
- No swim shortcut (the deep-water and dive drains are unset in `v5`) and no hop shortcut (a hop of 6.5 cm is under the 2.5 cm ground's resolution to size, and the design gives no rule for a hop against a pull-over).
- (Answered 15 October, see the follow-up above.) The summit's top band is 5, not the top bands: the tallest landmarks (the shelf, the easel) lie in the unreached south, so a team that finds all 24 Fractals (four spends) can climb the corridor without the Gubble. Now the summit needs band 8, beyond the 7 that 30 Fractals open.
- (Answered 15 October.) The summit's place is in the package (`summit.top_m`) but the manifest has no field for it, and "past the tutorial" needs one. Now `x_summit`.
- Numbers the docs do not give, each a constant: gate slack 0.02 m, riser 66 degrees, bench 1.2 by 1.4 m, 8 steps, a ladder of 4 and one of 3, the corner spacing 6 m, a step placed about 12 m from the player's arrival (the nearest Fractal is 8 m away).

```
python -B -m pipeline.landscape.generator.generate --room pipeline/landscape/corpus/rooms/garage_nominal --world-scale 10 --tutorial --reach-report <report.md> --out <pkg>
python -B -m pipeline.landscape.generator.checks --package <pkg> --room pipeline/landscape/corpus/rooms/garage_nominal --world-scale 10
python -B -m unittest pipeline.landscape.generator.tests.test_fractals -v
python -B -S -m unittest pipeline.landscape.export.tests.test_fractals -v
```

## The hamlet proved by the walk, and a cottage on its pad (Run 2, garage-road, 13 October)

The founder's real garage at 10x (`landscape_my_garage`, converted by `from_capture.py`) failed `overall` with its hamlet
out of reach and one cottage 10 cm into its slope. Both came from the 10 cm base grid meeting a 5 cm walk:

- **Why the hamlet was cut off.** `find_hamlet` takes the best-sheltered flat place the quick flood (`reachable_from`:
  10 cm cells, slope up to 38 degrees, 24 cm off the water) calls reachable. On the garage that is a shelf 0.2 to 0.4 m wide
  at (9.38, 22.22) between three cottages, the sea and a bank. The exact walk (11 cm disc, 20 degrees, 5 cm lattice) found
  the crate's pocket and the first door's enclosed by the pads' skirts, which are steeper than 20 degrees; the ways graded
  to them (`grade_line` along the steeper walk's route) stopped at the skirts or cut the pads' rims, and the grading loop
  made two passes and never looked again. The checks found none of its five routes (the crate there and back, three doors).
- **Why the cottage sat in its slope.** The pad is levelled 7 cm out from the footprint, but a way graded later cut and
  filled the rim cells that lie outside `pads` > .3 (that protects only 4 cm from the footprint), the vertices of a
  10 cm cell meeting the footprint's edge lie beyond the rim, in the bank, and the next cottage's feathering reaches
  them. The garage's first cottage (floor at 6.4 cm) read 8.5 cm of ground at one corner after the pad and 16.1 cm after
  the ways: 9.7 cm sunk, where the check allows 8.6.
- **What changed (a magnified room only; every 1x corpus room stays byte-identical):**
  - `flatten(reach=)` levels the vertices of every cell that meets a footprint, and `pad_hold` keeps them, with the pad's
    margin, from `build_road` and `grade_line` (`keep_out`); a second pass puts each pad's core back on its own level after
    the neighbours' feathering (a footing now reads flat, 4 mm under its floor, at every yaw).
  - The ways are graded and **proved by a new exact walk** until the crate and every door are reached (up to 6 passes and
    one more that only proves; the room at 1x keeps its two).
  - A hamlet no way reaches is **given up for the next site** (`HamletCutOff`): `find_hamlet(skip=)` takes the best site
    1.5 m or more from each given up, up to 6 sites; if none is reached the first stands, as it always did.
    `x_generator.hamlet.given_up` lists them (only after a retry). A second proof reads the land and the planting as they
    will be written (the 2.5 cm ground and the plants), so a hamlet the base proved but the detail cut is also given up.
- **The corpus at 3x (the generator before this cut the hamlet off in five rooms):** home_office nominal and scan 73
  (the crate and the first door), garage scan 17 (a door), workshop scans 17 and 73 (the third door) all walk now, and
  their cottages stand flat (4 mm under the floor by design; 3.4 to 5.2 cm in before, in three of them).
- **The real garage:** the first site stands (no retry), every walk found (the crate and back, three doors, the
  woodpile), the jetty and four beaches reached, all five built things grounded and all three cottages exactly on their
  pads (4 mm under the floor). Generation about 3 to 4 min on a loaded machine. The 10x nominal garage's first site is now
  given up (once the pads' rims are held, no way reaches its goals) and its hamlet moves 1.6 m, to (2.78, -34.02); it still
  passes `overall`, and generating it takes about 100 s longer (the failed attempt).
- **Still failing on the real garage (`overall` is not yet true), as before this packet:**
  - `footprints`: six objects (cardboard box 2, desktop computer, jar, laptop, monitor, printer) rest on surfaces the
    inventory does not hold, so they read as lost; they become ordinary children once the furniture the scan dropped is
    in the inventory (the founder is supplying it).
  - `sea`: the reef line comes 0.044 m from the coast at one vertex, (-27.0, -22.1), where the coast has a notch the reef's
    offset curve cuts across, and lies 1.046 m from it at another, (-12.9, 22.5); the check keeps 0.2 to 1.0 m. The reef is
    shaped in wall offsets and the coast it is measured against is the waterline; shaping it from the waterline's own
    distance field is the likely fix. Not touched here (it reshapes the sea floor of every magnified room). The game reads
    the line only for how far a point is past the reef (`RoomSea.PastReefM`).

## Navigation edge errors, and the planting salt (Run 2, garage-road, 13 October)

The first regenerated real garage booted (`tools/smoke-boot.ps1`) with "Navigation region synchronization had 2 edge
error(s)", which the installed one, made before the hamlet fix, did not. Looking for overlapping data found none:

- **The land is not at fault.** At the one place the errors are, the land of the two packages is the same: all 60,830
  vertices of an 11 by 10 m window round it compared, none differs. No triangle there is doubled or coincident (288 triangles
  of 0.005 m2, each point of the window under exactly one), and no trunk or stone stands within 2.4 m.
- **Where it is** (the game's island builder read in a copy of `game/`, nothing of the repository's changed): tile 35 of the
  8 m navigation lattice, round (1.73, 11.73). Recast's triangulation of that tile's one open region, which has 31 trunk and
  stone holes in it, made five triangles meet at one vertex with one covering two of the others, so one edge had three
  polygons (Godot's map keys edges to 1 mm and allows two). Six of the tile's holes are needed for it (broadleaf 282, 524,
  526, 531, 621 and rock 433): taking any one out, or all of them, bakes clean, and another partitioning scheme only
  moves it to another tile.
- **Why this export and not the old.** The planting is one random stream, so every difference upstream (the hamlet's pads
  and ways) draws every plant on the island again; the trunk holes of the old garage did not make this layout.
  Of the layouts tried one in five does (the old garage, the 10x nominal garage and three salted layouts of the
  real garage all boot clean).
- **What changed.** `generate(plant_salt=N)` and `--plant-salt N` draw the planting again on the very same land (0, the
  default, is as always; the package says `x_generator.plant_salt` only when it is not 0). The real garage is generated
  with `--plant-salt 1`. A bake that does not meet such holes needs a change in the game's navigation (the contour or
  hole handling in `game/Scripts/native/Navigation/RoomNavigation.cs`), which is not this lane's.

## A captured room as a generator room (Run 2, 13 October)

- **`from_capture.py`** turns a captured shell (`room.json`, shell only) and its C4 inventory into a corpus-shaped room
  folder: every inventory object kept as it is, the spawns moved only where they stand within 30 cm of a footprint or
  50 cm of a wall, and the site's -Z bearing worked out from the wall the founder names (`--wall-faces B=south`, say); the
  setup answers it writes beside the room carry that bearing. It refuses to write where Git tracks: a real room's
  derived data stays under `.cache/` or `captures/`. Its tests use the invented corpus garage only.
- **A road reaches its own goal** (`life.build_road`): a road keeps 24 cm off water it cannot bridge, and a goal inside
  that band (a cottage's forecourt by the shore) was refused, so the road was never built. On the 10x world a dry
  room's hamlet stands by the sea, and the corpus garage at 10x had no road to its hamlet either (its walk passed only
  through the regrading). Now the goal itself is let in when it lies on dry land. No 1x corpus room has such a goal
  (all 24 probed), so 1x stays byte-identical; the 10x garage gets its road and still passes `overall`.
- **Not done:** a box resting on a surface the inventory does not hold (C4's `support.kind: surface`, no target) rises
  from the plain at its own height, while the checks read its footprint at its real top, so it reads as lost. What land
  goes under such a box (the furniture the scan dropped) is a design decision; adding that furniture to the inventory
  makes them ordinary children. And a dry room's coastal hamlet at 10x can still be cut off on a narrow shore below a
  bluff (its forecourts, the roads and the regrading meet there).

## Ground for the 10x world (Run 2, scale packet S4, 13 October)

The experiment's 10x ground was today's 3 cm samples stretched over 10 cm cells: a 10 cm brook was one cell wide,
path and material edges showed as triangles, and thin rock was planed 30 cm wide. Now:

- **A 10 cm base with 2.5 cm detail blocks of 1 m** (`detail.py`) round what the body meets: the paths and graded ways,
  the streams and their banks, the tarn's shore, the beaches' wading shelf, the hamlet, the jetty's landing, the glades
  and the climbs. On the garage: 483 blocks (about a tenth of the land), 0.78 million fine vertices.
- **How the detail is made:** every step that shapes the base (the coast, the landing, the tarn, the streams, the
  glades, the pads, the paths and their grading, the tarn's rim) also records its rule; once the base is done the rules
  are replayed on the blocks, from today's landforms read at each fine point. Decisions (a level, a path's graded line,
  a stream's surface) stay the base's; streams and paths are read along their lines, not sample by sample.
- **Where the body walks** (benches, pads, forecourts, glades, the landing) the blocks keep the base's surface, the one
  the walk was graded on; only their surface is painted at 2.5 cm there. Elsewhere (banks, beds, shores, the shelf, the
  lips) the shape is the detail's.
- **No crack:** a block side that meets base ground has its fine vertices on the straight line between the base
  vertices, and the base triangle beside it is a fan through them (one plane, no T-junction). The package's land is one
  conforming mesh: every inner edge is shared by exactly two triangles (`tests.DetailSeams`; on the 10x garage 2.54
  million triangles, 0 open edges, 88,000 rays across the blocks' sides all find the land).
- **The package says where:** `x_generator.detail` (`cell_m` 0.025, `block_m`, `origin_m`, `blocks`, `reasons`).
- **Walks stay fast:** the checks vouch for whole 2.5 cm cells as they do for the base's (`Terrain.plain_slope`);
  only the seams' fans are read in full. The walk of the yards reads the meshes in single precision from the start, so
  the beaches are proved on the same reading.
- **Magnified inside the generator** from the true-size room (`--world-scale 10`; `scale.magnify`): the package's source
  is the true-size room (its hashes), so the source contract's 20 m opening holds; the harness reads the package against
  the room magnified by the package's `x_generator.world_scale` (the harness's `magnify_room`, merged with S5), and the
  exporter exports that magnified room. The x10 room file is gone.
- **The 10x gaps:**
  - the hamlet's third door: its forecourt lies 6 cm under the next cottage's, and the cottages were levelled again
    after grading, over the ramp graded to it. On the 10x world the hamlet is levelled before the walk, and every way
    graded keeps the forecourts' and the landing's levels;
  - the reef's distance is the body's (a short swim, the plan's BODY list): the coast it is measured from is the
    farthest-reaching within .2 and .35 m, not x W; the check's 0.2 to 1.0 m stays (now 0.24 to 0.84 m);
  - the footprint rule is the room's (the plan's ROOM list: a third of a box's height, 15 cm x W): the check reads its
    metres x W, as the generator's own protection of a landform's top already did (the laptop reads again);
  - thin rock is planed at the body's size, 10 cm at 2.5 cm (then 15 cm round a knife edge left), in every block with a
    face over 55 degrees; the base is no longer planed 30 cm wide. Climb (beside `overall`): 3 crests (S3: 3), an
    object's protected top, a fin by the river, a coastal rib;
  - a stream's bank is cut where the land lies within 10 cm of it; a landform it runs beside keeps its face, rising from
    the water (no 5 m wall cut into a cliff's foot), and a spring cuts nothing behind it;
  - a path's bench is never narrower than the walk resolves (10.5 cm each side, the 11 cm disc plus one 5 cm step).
- **1x is unchanged:** byte-identical (four corpus rooms; the 25 unit tests pass).

The 10x garage passes `overall` (walk 5 of 5, sea, footprints 16 of 16, water, grounded). Two yard things (a woodpile
and the laundry) are still left out at 10x, as in S3. The land is 0.99 million base triangles (5,960 of them fans) and
1.55 million detail triangles (land.glb 75 MB). Generation, S3's and this run back to back on the same loaded machine:
184 s and 227 s (S3 alone earlier: 133 s). The detail's own stages: climbs 4 s, detail 11 s, lips 7 s (the base's
30 cm planing, 13 s, is gone), the surface and the stitched mesh +20 s, the yards' walk over the finer land +10 s,
writing the bigger land +15 s.

```
python -B -m pipeline.landscape.generator.generate --room pipeline/landscape/corpus/rooms/garage_nominal --world-scale 10 --out <pkg>
python -B -m pipeline.landscape.generator.checks --package <pkg> --room pipeline/landscape/corpus/rooms/garage_nominal --world-scale 10
```

## Generation fast enough at world scale (Run 2, scale packet S3, 13 October)

The founder moved the game to a 10x world (13 October): the room magnified ten times round today's body. The scale
experiment's 10x garage took 6.9 minutes to generate, and its walk checks gave up on some goals and took others on
trust. Now:

- **The walk is exact again, island-wide** (`checks.Walker`): one flood from the spawn per planting state answers every
  goal (the crate, the doors, the yards, the beaches, the jetty); no node budget, nothing taken on trust. The lattice
  is 2.5 cm on today's room and 5 cm on a magnified one; the body's limits are unchanged (11 cm disc, 2 cm steps sampled
  each centimetre, 20 degrees). Grading's ways are the same A* searches as before on today's room; on a magnified one a
  goal shut in a pocket is found so by a small flood back from it, and one least-cost flood serves the others.
- **The same answers, faster:** the package's own land grid vouches for plain buckets (whole cells, corners shared
  exactly, nothing on top, no deck, no water), so a node there needs no sample read; answers about the ground are shared
  by every walk over it; a flood goes on after a lane is cleared; a graded Terrain is updated where the land moved.
  Tests pin every shortcut against the full reading (and on the 10x garage the fast floods equal the full ones).
- **The package is written once:** the beach check reads the meshes as the package will hold them (float32) and proves
  each flood's way there, with no scratch package written and read back.
- **1x is unchanged:** all 24 corpus rooms come out byte-identical to the generator before this packet (3 to 5 times
  faster: the garage 136 s to 37 s with three rooms generating at once).

On the 10x garage the generator is no longer the slow part: the harness's `write_package` takes a third of the time
(see the change request in the S3 report). The checks now see what the experiment did not: the hamlet's third door is
unreachable at 10x, and the 1x thresholds for the reef's distance, footprints and climbs do not hold there yet.

## The clean coast (Run 2, Bubbles and Fireworks round, 10 October; generator version 5)

The founder, after swimming out from the garage island: "We have the land meet the sea on mostly beaches with smooth gradual transitions into deep ocean and sheer cliffs plummeting deep into the ocean but not with weird walls off shore."

- **Nothing stands offshore** (`sea.py`):
  - the reef's rock band and the rocks breaking the surface are gone;
  - so are the sea stacks;
  - so are the distant islands. The far sea floor mesh keeps its name `distant_islands` and its floor (the game's `RoomSea.OpenSeaBedAt`), with nothing rising from it.
- **The reef line is the shelf's edge under water.** Off the soft shores a shelf at the lagoon's depth (0.22 m) runs out to it, then falls to the open sea (0.62 m) over 0.7 m (was 0.4 m), so the slope is gentler.
- **Cliffs plummet into deep water.** At a cliff's foot there is no shelf: the 72 degree face runs straight down to the open sea's floor.
- **Mostly beaches between the headlands.** The low 30 degree rocky shore now takes 2 to 18% of a room's coast (was 4 to 39%). Headlands still go where furniture stands near a wall, so a crowded room like the garage keeps a mostly cliffed coast (see "For the founder").
- **The seabed** is scree, with rock at a cliff's foot; no ring marks the reef line.
- **Kept:**
  - the jetty, its harbour and the pass in the data;
  - B home;
  - the beaches' walk-out and wade-in (8 degree wading shelf, then 22 degrees);
  - the swim and dive depths;
  - the climbs;
  - the promised beaches.

### The package's `x_generator.sea`

The fields are unchanged, so the exporter's `x_landscape_sea`, `RoomSea` and `OpenSea` need no change. Three values change:
- `reef.crest_y_m` is the shelf's nominal floor, `level - 0.22` (was `level - 0.022`, the rock's crest). The game only prints it.
- `stacks` is `[]`.
- `distant_islands` is `[]`.

### Checks

`sea` gains `offshore`. The sea is the water joined to the grid's edge. Everything else, plus the sea within 0.1 m of the surface (a body's height), is the island and its shallows. Shallows not joined to the island the player stands on are walls offshore: reef rocks, a stack, a rock breaking the surface. The check fails on any.

It also reports `cliff_foot_depth_m`: the depth 0.3 m out from each coast face over 60 degrees (min, median).

### The garage, before and after

| | Before (`90d1f1d`) | After |
|---|---|---|
| Walls offshore | 7: four stacks up to 0.32 m above the sea, three reef stretches breaking it by 3 cm | none |
| Cliffs: depth 0.1 / 0.2 / 0.3 / 0.8 m out (median) | 0.21 / 0.22 / 0.20 / 0.59 m (a lagoon at the foot) | 0.32 / 0.60 / 0.62 / 0.61 m |
| beach_00, walking out | 8 cm deep at 0.63 m, then a reef ridge 9 cm high (a 77 degree step) | 8 cm deep at 0.63 m, 22 cm at 0.98 m, 60 cm at 1.92 m; never steeper than 22 degrees, never rising |
| Distant islands | six, up to 2.6 m high | none; the far floor lies at -0.68 m |
| Coast (garage) | 79% cliff, 10% beach-like, 11% between | unchanged |

### Corpus: 23 of 24 pass all checks (was 21 of 24 at `90d1f1d`)

- **Fixed:**
  - awkward_l_scan_17 (walk, a cottage door);
  - living_room_scan_73 (grounding and the beach);
  - workshop_nominal now also meets the climb principle.
- **Still failing: home_office_scan_73.** Its jetty landing is unreachable, as before. A woodpile and the promised beach on the west coast are now unreachable too. A new beach there blends into a short stretch of rocky shore and leaves a bluff 2 to 4 cm high (35 to 38 degrees) between the beach and the plain. That bluff cuts off a strip the walk used to cross. Starting the rocky shore's steep rise half a metre inland, like the beach's, did not fix it; left for the next round.
- **No room has a wall offshore.**
- **Cliff feet:** in 22 rooms the water 0.3 m off the coast's steep faces is 0.59 to 0.62 m deep (median). In awkward_l nominal and scan 73 it is 0.23 to 0.24 m. The measure counts every face over 60 degrees at the waterline, so those two are not yet explained.

Command, per room, as before; the run uses `generate` then `checks` on each of the 24 rooms.

### For the founder

"Mostly beaches": crowded rooms still have mostly cliffed coasts, because furniture near a wall makes a headland. The garage measures 74% headland from its furniture. Shorter headlands would give more beach but cut into the landforms' footprints. That is the founder's call to make by playing.

## Climbing and the last rooms (Run 2, the open sea round, 9 October)

- **What you climb, you can stand on top of** (`climb.py`, Lane P's measurements on the garage). A climber goes up the fall line of a face over 55 degrees from standable ground and over its top; a knife edge or needle is a crest with no 5 cm lip at 35 degrees or less and no standable patch within a body length.
  - The generator weathers rock too thin to stand on: a 9 cm grey-scale opening of the land, then 15 cm round any knife edge left. It never raises land. Water, banks, paths, the hamlet, the jetty and the sea stay; an object's own landform weathers at most to the height its footprint still reads by.
  - Spires over 20 cm keep a 5 cm caprock summit (hoodoos, not points); summits are weathered smooth of the flank noise.
  - Lane P's two examples are gone: the table corner the coast had cut into a sliver, and the knife ridge beside it.
  - `checks` prints `CLIMB` and `climb=`; it is reported beside `overall`, not in it, because tall crests of object landforms (crags and the chair's tor, 0.4 to 1.9 m up) still fail it in most rooms.
- **Beaches are promised only if the written package reaches them** (single precision), not only the generator's memory; with none, the harbour's sand beside the jetty is tried.
- **Corpus: 21 of 24** (the full run gave 18; three rooms lost a footprint to planing, fixed by an absolute weathering floor and rerun with three others, all passing). Fixed: home_office_nominal, living_room_scan_17. Still failing: awkward_l_scan_17 (walk, a cottage door), home_office_scan_73 (the jetty's landing is a pocket below a bluff), living_room_scan_73 (no beach a body reaches; a cottage hangs 1.4 cm).
- **Not done: the ragged coast.** The coast still reads as a rounded rectangle.

## The island (Run 2, the island round, 9 October; generator version 4)

Every room is an island in an endless sea (`sea.py`). Synthetic top-down pictures were made outside the repository; no Blender renders yet.

- **The coast** follows the walkable floor's outline, so an L-shaped room is an L-shaped island. The walls' ridges are gone. Read along the wall line:
  - **cliffs and headlands** where a landform stands within 0.5 m of the wall, plus a few rocky points. The coast reaches 0.06 to 0.28 m past the wall there. The landform keeps its height to the waterline, then drops at 72 degrees or steeper (climbable);
  - **coves with beaches** elsewhere, the water 0.07 to 0.33 m inside the wall: sand at 9 then 16 degrees, an 8 degree wading shelf, then 22 degrees down. Half a metre inland the beach stops cutting, so a landform meets it as a bluff and keeps its height;
  - **a low rocky shore** (30 degrees) between them;
  - up to four **sea stacks** off the headlands, standing alone in the lagoon.
- **The door becomes a jetty** in a harbour cove: a level plank deck 0.16 m wide, 0.05 m above the sea, on timber posts, square to the door's wall, from a landing on the shore out over the lagoon. A worn path leads to it, and a pass through the reef lies before it.
- **The sea:** the level is `SEA_Y = -0.02` m, 2 cm under the room's floor. The lagoon is 0.22 m deep. The reef lies 0.3 to 0.6 m off the coast (0.55 to 0.6 m off a beach, so its shelf reaches swimming water first): rock 2 cm under the surface, with rocks breaking it. Past the reef the sea falls to 0.62 m. The sea's still surface covers the grid wherever the sea floor lies under it, then runs on as a strip to 60 m. **Distant islands** (six, 5 to 19 m out) rise where the far hills were.
- **Rivers run to the sea:** the river (or a dry room's rill) leaves the tarn or its spring for the sea, never below sea level. It ends in an estuary on a low shore, or falls over a cliff. The distant lake is gone.
- **Beaches promised to a swimmer:** at the middle of each beach stretch, a wash-ashore place on dry sand. Under water, no step where the feet touch is steeper than 35 degrees (the pond's rule); on dry sand none is steeper than 20. Each place is reachable on foot from the spawn; at most five are promised, at least 1.2 m apart.

### The package's `x_generator.sea` (for brief 21 and the contract change)

Units are metres and degrees, in the room frame (y up, -Z forward). Outlines are closed loops of `[x, z]` in the room's floor-polygon winding, with the first point not repeated.

| Field | Meaning |
|---|---|
| `level_m` | sea surface y (-0.02) |
| `mesh` | `"sea"`: the still water record (`water` kind `still`), the island cut out |
| `swim_depth_m`, `lagoon_depth_m`, `open_sea_depth_m` | 0.08 (the body floats), 0.22, 0.62 |
| `coast.outline_m` | the island's waterline (the loop round the player's spawn) |
| `reef.outline_m` | the reef's crest line |
| `reef.crest_y_m`, `reef.band_half_width_m` | crest y (level - 0.022), half width (0.085) |
| `reef.offshore_m` | `[min, max]` distance of the crest from the coast |
| `reef.passes[]` | `centre_m [x, z]` and `width_m` of the pass before the jetty |
| `play_area.outline_m` | the playable sea's edge: 0.3 m past the reef (`past_reef_m`); the current belongs between the reef and this line |
| `play_area.bounds_m` | `min_m`/`max_m` `[x, y, z]`: the room bounds to grow to (deepest sea floor to the room's top) |
| `beaches[]` | `id`, `wash_ashore_m [x, y, z]` (dry sand, standable, reachable on foot), `yaw_deg` (Godot yaw facing inland), `water_m [x, level, z]` (swimming water straight off it), `water_depth_m` |
| `jetty` | `mesh` `"jetty"` (a `terrain` record), `door_id`, `root_m`/`end_m [x, deck_top, z]`, `yaw_deg` (root to end), `width_m`, `length_m`, `deck_top_m` |
| `stacks[]` | `centre_m [x, z]`, `radius_m`, `top_m` |
| `river_mouths[]` | `[x, z]` where a stream meets the sea |
| `distant_islands[]` | `x`, `z`, `radius_m`, `top_m` (scenery mesh `distant_islands`) |

**Package records:**
- `terrain`: `land` (the whole grid: island, beaches, cliffs, lagoon floor, reef and stacks, so the swimmer has a bed) and `jetty`;
- `water`: `sea` and the inland water;
- `scenery`: `distant_islands` (the sea floor beyond the grid, and the islands).

The walls' `ridges`, `far_hills` and `distant_lake` are gone.

### Checks

The checks gain `sea`:
- the sea is level at `level_m`;
- every point of the playable edge lies in water at least 0.1 m deep;
- the player's spawn is on the island;
- the reef lies 0.2 to 1.0 m off the coast (measured to the nearest coast point, which stretches at convex corners);
- every promised beach has swimming water off it and a walk out of the sea (35 degrees under water, 20 on dry sand), and is reachable on foot from the spawn; there is at least one beach;
- the jetty's landing end is reachable on foot.

`walk` keeps the crate, the doors and the yards. The checks look only at the island and its water (the playable bounds plus 0.6 m), not out to the horizon.

### Corpus (9 October): 14 of 24 pass all checks (C6 had 23 of 24 before the island)

Command, per room:
```
python3 -B -m pipeline.landscape.generator.generate --room <room> --out <pkg>
python3 -B -m pipeline.landscape.generator.checks --package <pkg> --room <room>
```

**Pass:**
- awkward_l nominal and scan 73;
- bedroom nominal and scan 17;
- garage, all three;
- home_office, all three;
- living_room_nominal;
- near_empty_nominal;
- workshop nominal and scan 73.

The garage: `CHECKS water=True grounded=True walk=True footprints=True sea=True overall=True`. It has its tarn (the pond test still passes), climbable headland cliffs, one promised beach and the jetty.

**Fail, by cause:**
- **The jetty's landing is not reachable on foot:** kitchen, all three; bedroom_scan_73, whose spawn is not standable, as in C6.
- **No beach could be promised:** living_room scan 17 and scan 73; workshop_scan_17; bedroom_scan_73.
- **A promised beach the generator's walk reached is unreachable in the written package:** near_empty scan 17 and scan 73; kitchen_scan_73. A difference between the generator's in-memory probe and the package remains to be found.
- **A footprint is lost:**
  - kitchen_scan_73's cabinet_2;
  - living_room_scan_17's lamp_1.
- **Other:**
  - living_room_scan_73: a cottage is not grounded;
  - awkward_l_scan_17: a cottage door is unreachable.

**Not yet done:**
- **Generation time:** about 110 s for the garage (was 50 s).
- **The look:** distant islands carry no trees or settlements yet. Foam and breakers are Lane L's.


Renders: [sheet.png](renders/sheet.png) (final), [scan_17_draft_sheet.png](renders/scan_17_draft_sheet.png), [corpus_contact_sheet.png](renders/corpus_contact_sheet.png), [corpus_variants_sheet.png](renders/corpus_variants_sheet.png). Seed `20261008`.

## How the room becomes land
- **Geology.** Every box is uplift on a 3 cm grid, merged smoothly. Confident kinds pick forms; low confidence spreads less. Soft kinds are hills (summit ridge, spurs, gullies, a shared lee tail). A form gives way to a sure neighbour outside its own box, and every landform is at least 18 cm wide. Three rounds of erosion.
- **Shell.** A grassy ridge ring follows the floor outline and is highest far from the outlet. It rises behind objects against the wall and sinks into the shared surround without a seam. Distant hills surround the land. Nothing is shaped to a camera.
- **Water (C6).** Each room's floor chooses its water:
  - **a tarn** where a broad hollow is clear (clear radius 0.8 m or more): shelving shores toward the walker's side and the brook's delta, drop-offs of about 58° under low cut banks, and a deep middle of 14 to 24 cm (the garage's is 23 cm);
  - **a dry upland** with only a rill where the room is small and crowded (clear radius under 0.55 m and 20% or more of the floor covered);
  - **otherwise a river** from a far hillside, with up to three deep pools (17 cm) below steep stretches or on bends.

  Levels are held by the lowest rim, and water never runs uphill.
- **People.**
  - A hamlet where water, flat ground and shelter meet; its demands relax step by step if a room offers no ideal site, but it never covers an object.
  - Cottages keep lanes apart and have level forecourts.
  - Graded 17 cm paths, with a level timber footbridge where one must cross a stream.
  - Woodpiles and a laundry line in reachable yards.
  - Where a body cannot reach the crate or a door, the land is cut and filled to grade.

## Checks
```
python -B -m pipeline.landscape.generator.generate --room <room> --out <pkg>
python -B -m pipeline.landscape.generator.checks --package <pkg> --room <room>
python -B -m unittest pipeline.landscape.generator.tests.test_generator -v   # 8 tests OK
```
garage_nominal:
```
GROUNDED built=9 worst_hang_m=-0.0020 worst_sunk_m=0.0114 failures=0
WALK crate 6.045 m (7.52 deg), back carrying 6.045 m (11.15 deg); doors 5.93/6.61/5.97 m;
     woodpiles 6.42/6.13/6.79 m; laundry 6.45 m
CHECKS water=True grounded=True walk=True footprints=True overall=True
```
The walk is a flood from the spawn on a 2.5 cm lattice (5 cm on a magnified room) of the decoded triangles plus footbridge decks. An 11 cm disc must be dry, clear of obstacles and on faces of 20° or less, and every edge is sampled each centimetre (step 0.02 m).

Garage grounding (box top → land top, Δ):

| Object | Box top | Land top | Δ |
|---|---|---|---|
| couch | 0.830 | 0.896 | +0.066 |
| bean bags | 0.650 / 0.600 | 0.786 / 0.735 | +0.136 / +0.135 |
| bicycle | 1.050 | 1.051 | +0.001 |
| bin | 0.500 | 0.574 | +0.074 |
| box | 0.300 | 0.390 | +0.090 |
| desk | 0.750 | 0.991 | +0.241 |
| easel | 1.650 | 1.730 | +0.080 |
| french press | 0.966 | 0.912 | −0.054 |
| jar | 2.140 | 2.142 | +0.002 |
| laptop | 0.790 | 0.970 | +0.180 |
| monitor | 1.170 | 1.158 | −0.012 |
| chair | 1.050 | 1.077 | +0.027 |
| shelf | 2.020 | 2.177 | +0.157 |
| tote | 0.380 | 0.452 | +0.072 |
| table | 0.720 | 0.821 | +0.101 |

## Corpus (water / grounded / walk / footprints, walk to crate in m, invented)

C6 (8 October, late night): 23 of 24 pass. bedroom_scan_73's walk fails because its rill has no footbridge. The table below is C5 part 2's and predates C6's water choice.

In C5, every room invented a tarn and a river.

| Room | Checks | Walk | Invented beyond the tarn and river |
|---|---|---|---|
| awkward_l_nominal | all pass | 5.447 | 2 cottages, footbridge, laundry, 2 woodpiles |
| awkward_l_scan_17 | all pass | 4.364 | 2 cottages, footbridge, laundry, 1 woodpile |
| awkward_l_scan_73 | all pass | 6.345 | 2 cottages, footbridge, laundry, 2 woodpiles |
| bedroom_nominal | **water FAIL** | 1.415 | 1 cottage, laundry, 1 woodpile |
| bedroom_scan_17 | all pass | 1.566 | 2 cottages, laundry, 2 woodpiles |
| bedroom_scan_73 | all pass | 6.66 | 3 cottages, laundry, 2 woodpiles |
| garage_nominal | all pass | 6.045 | 3 cottages, laundry, 3 woodpiles |
| garage_scan_17 | all pass | 3.405 | 3 cottages, laundry, 3 woodpiles |
| garage_scan_73 | all pass | 4.193 | 3 cottages, laundry, 2 woodpiles |
| home_office_nominal | all pass | 6.584 | 2 cottages, laundry, 2 woodpiles |
| home_office_scan_17 | all pass | 4.194 | 3 cottages, laundry, 3 woodpiles |
| home_office_scan_73 | all pass | 6.948 | 2 cottages, laundry, 2 woodpiles |
| kitchen_nominal | all pass | 1.314 | 3 cottages, laundry, 3 woodpiles |
| kitchen_scan_17 | all pass | 1.613 | 3 cottages, laundry, 3 woodpiles |
| kitchen_scan_73 | all pass | 4.165 | 3 cottages, laundry, 2 woodpiles |
| living_room_nominal | all pass | 4.363 | 3 cottages, laundry, 2 woodpiles |
| living_room_scan_17 | all pass | 5.773 | 3 cottages, laundry, 2 woodpiles |
| living_room_scan_73 | all pass | 4.387 | 3 cottages, laundry, 3 woodpiles |
| near_empty_nominal | all pass | 3.066 | 3 cottages, footbridge, laundry, 3 woodpiles |
| near_empty_scan_17 | all pass | 4.3 | 3 cottages, laundry, 3 woodpiles |
| near_empty_scan_73 | all pass | 3.84 | 3 cottages, laundry, 3 woodpiles |
| workshop_nominal | **walk FAIL** | – | 3 cottages, laundry, 3 woodpiles |
| workshop_scan_17 | all pass | 4.999 | 2 cottages, laundry, 1 woodpile |
| workshop_scan_73 | all pass | 3.57 | 3 cottages, laundry, 3 woodpiles |

## Weak
- bedroom_nominal's tarn edge is breached at 3 points.
- workshop_nominal's walk is blocked beside a small protected landform.
- Every room gets a tarn at water "some".
- In the SW overview the high country's flank fills the near side.
- Hills are still rounded up close.
- Some player eyes face a hillside (spawn facing belongs to the exporter).
