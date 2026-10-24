# Landscape package to game room

From the repository root, standard-library Python only:

```powershell
python -B -S -m pipeline.landscape.export --package <package-folder> --room pipeline/landscape/corpus/rooms/garage_nominal --room-id landscape_garage_nominal --out <empty-parent>/landscape_garage_nominal
python -B contracts/validate.py --room <empty-parent>/landscape_garage_nominal
python -B -S -m unittest pipeline.landscape.export.tests.test_export -v
```

The output directory's final component **must equal `--room-id`** (a game loader
and validator requirement). It must be empty or absent. `read_package` owns all
package validation. Input and mapping errors precede any output creation; an OS
write error can leave partial output and returns failure. Never reuse that folder
as a pinned room. Tests use `os.makedirs` in system temp and remove their output;
the validator subprocess needs the repository's existing contract dependencies.

| Package/source record | Room record / decision |
|---|---|
| Terrain | Role-separated `shell:terrain_<role>` parts, `ground`, colliding, each a tiled GLB (`terrain_tiles`, below): level 0 is the package's own triangles cut exactly at the 16 m tile lines, with no remeshing |
| Unreachable scenery | `backdrop`, non-colliding GLBs |
| Water | `backdrop`, non-colliding, contract material `water`; backdrop is the existing decorative shell role, while ground would imply footing |
| Populated objects | Named `obj:<id>`, individual assets, final dimensions, original mass, yaw quaternion, terrain support when present; authored `carriable` maps to `physics.movable`. Small loose crates/stones also qualify; woodpiles split as below |
| Cottages, towers, fences, boulders, crates, lanterns | Named fixed scatter entities with box collision (plants remain decorative); custom masonry/roof or non-plant forms at least 0.3 m also qualify |
| Plants, trees and stones | Plant copies (`plant_copies`, below): one set per prototype, its mesh and lighter meshes, one table of rows. Plants never collide, a tree's row carries its climbing collision, a stone collides as its mesh's hull. No per-copy game nodes and no merged `scatter_visual`/`scatter_solid` meshes for them |
| Other scatter past the entity budget | Merged by role into `scatter_visual` (drawn) and `scatter_solid` (colliding) shell meshes, as before |
| Woodpiles | Edge-connected closed mesh pieces become separate `Log` entities, preserving every drawn triangle under scale, yaw and tint. No assumed number of logs or primitives; seams join by exact geometric edges |
| Small loose stones / crates | Stones up to 11 cm with estimated mass at most 0.5 kg; hollow crates up to 10 cm with estimated mass at most 0.5 kg become movable box assets. Populated records retain authored mass; heavier authored records stay fixed |
| Loose things | At most 64 movable entities including authored carryables. Reserve authored records, whole piles, small crates, then stones, in package order within each priority. Excess scatter stays drawn and colliding in merged shells; excess populated items stay fixed. No partial pile is promoted. More than 64 authored carryables fail before writing |
| Trees (bark plus foliage, excluding small plants) | Scatter trees are copies whose rows carry the trunk, the hidden pole and the crown perches (below). Fixed populated trees keep their original asset visuals and two merged `ground`, colliding, `"drawn": false` shell parts, `shell:tree_climb_bark` and `shell:tree_climb_foliage` (an open pole continuing each trunk, coarse one-sided crown caps with flat perches, and the trunk), instead of a canopy box |
| Scatter exceeding entity budget | After reserving all populated objects, first landmarks in package order use remaining slots up to 512; the rest become copies (stones) or keep their drawn geometry in merged shell meshes |
| Source spawns | Same x/z and ids; y is the highest terrain triangle hit, including roofs of caves; missing ground fails. Yaw faces the horizontally nearest carriable/movable populated object or cottage/tower (including merged buildings), using Godot +Y rotation with -Z forward. Equal distances use package order (objects, then scatter); no destination or a coincident nearest destination keeps source yaw |
| Setup | Latitude/bearing in `site`; solar noon 12 because package time is apparent solar time; full answers in `extensions.x_landscape_setup` |
| Source room / inventory | Data-only `extensions.x_landscape_source`: source id and byte SHA-256 pins, bounds, floor/ceiling polygons, wall polygons and vertical heights, openings, and every inventory object's kind/confidence, bottom-centre pose, size, colours and support; records sorted by id |
| The island's sea (generator v4 on) | From the package's `x_generator.sea`, a whitelisted, bounded copy in `extensions.x_landscape_sea`: the sea level, the swim depth, the coast, reef and playable-water outlines (closed `[x, z]` loops of at most 4,096 points), the reef's crest height, band and passes, the beaches (id, wash-ashore point, Godot yaw facing inland, water point) and the jetty (root, end, yaw, width, deck top). The room's `bounds` grow to the playable area's bounds, out past the reef. A package without a sea keeps the room's own bounds |
| The island's Fractals and tutorial mark (the mechanics round, packet M4) | From the package's `x_generator.fractals` and `x_generator.tutorial`, a whitelisted, bounded copy: `extensions.x_fractals` (`typed`, and each Fractal's `id` (`fractal:` and lowercase letters and digits, derived from its place), `position_m`, its `kind` when typed and the generator's `reach_band`, in id order, at most 1,000) and `extensions.x_island_tutorial` (true only). The generator's routes, benches and reach report stay in the package. A package without them writes neither key |
| The island's firepits (decision 74, Lane C, 24 October) | From the package's `x_generator.firepits`, a whitelisted, bounded copy: `extensions.x_firepits` (`items`, each firepit's `position_m`, bottom centre on its ground, in the generator's order, nearest the start first, which is the order the kernel numbers them in; at most 16, numbers finite and within 1,000 m, no two at one spot). Why the generator chose each spot, its reach band, its landmark, the counts and the notes stay in the package. A package without them writes no key, and the kernel spreads the firepits itself |
| Indoor lamps / playable openings | Dropped from the playable shell; source openings remain in the intro data. One sun hint, plus at most 31 lantern `lamp` hints; no source textures, reference images or location records copied |

Objects with zero mass, mass over 5,000 kg, more than 16 material roles, or ids
longer than the room contract permits fail cleanly. A `carriable` object above
the companion's 2 kg limit also fails; its authored mass is never silently changed.
At 0.5 kg or less both avatars can carry it. Reach, free hands, protection, other
objects resting on it, and physical clearance remain host checks. Export is not a
fetch or navigation certificate. Fixed scatter retains its 100 kg proxy mass.
Assets remain `draft`; their licensing is explicitly unverified, not newly granted.

Loose masses are authoring estimates, not measurements: convex closed log volume
times an assumed 600 kg/m³, closed stone volume times 2,600 kg/m³, and hollow-crate
volume scaled from the generator's 8 x 7 x 7 cm, 0.12 kg apple crate. All promoted
items are at most 0.5 kg. Plants stay rooted, bridges/buildings stay structural;
lanterns, fences and drying lines remain fixed pending a founder decision.

**Log collision approximation:** full cylinder bounding boxes overlap between
rows. Without changing the drawn stack, lower gameplay boxes end at the next
overlapping log's drawn bottom. Ground-contact bottoms use the highest terrain
point within the entire yawed footprint (triangle clipping includes interior
peaks). Upper boxes rest exactly on lower box tops. The mesh is rebased around
each box pivot, retaining its original world vertices and ground sink. Since the
contract's `box` uses `dimensions_m`, these dimensions describe the gameplay box;
the drawn envelope may extend below/above it. This deliberate approximation needs
game review, especially later manual stacking. Each asset records its actual
local visual bounds in `extensions.x_landscape_visual_bounds`.

`room.json`'s `extensions.x_landscape_loose` contains the cap, count, skipped
counts, and every movable entity's ID, source, mass, world box pivot, dimensions,
yaw and support. Log entries include visual bounds. This is review evidence,
not additional authoritative physics state. CLI statistics include loose counts.
64 is a provisional ceiling: at most 64 individual interactive bodies and 128
asset files, with at least 448 of the 512 entity slots available for fixed items.
It is not a measured frame-time guarantee; the integrator must measure in Godot.

Focused tests and the full CPU corpus check (working/output folders in system temp):

```powershell
python -B -S -m unittest discover -s pipeline/landscape/export/tests -t . -v
python -B -S -m pipeline.landscape.export.tests.corpus_smoke --scratch <temp-folder>
```

The corpus check generates all 24 synthetic packages, exports each twice, compares
every byte, validates each room through `contracts/validate.py`, checks log boxes
against exact terrain and each other, and independently verifies the original
spawn/destination rule. It prints per-room counts and mass ranges; complete boxes
are in each room's extension. It uses no Godot, GPU, photos or paid services.

## Plant copies (the 10x world)

`plant_copies` follows contracts/README.md, "Plant copies" (`copies.py`). A scatter
record that is not an entity or a loose thing becomes a copy when its prototype is a
tree (`trees.is_tree`), a plant (foliage, colliding nowhere: grass, flowers, ferns,
heather, reeds, shrubs) or a stone (rock all through); anything else, and a 65th
kind, stays merged. Each set is one prototype (`copies/<set>.glb`, unscaled, bottom
centre) with up to three lighter meshes (`copies/<set>_lod<n>.glb`): pieces of at
least 12 triangles cluster their vertices on a 3, then 2 cells-a-side grid, then the
whole prototype on a 2-cell grid (tree crowns merge into one blob); blades and flower
heads are thinned to the largest half, sixth, twentieth. A level that is not lighter
ends the ladder. Its table (`copies/<set>.bin`) holds one row per copy, sorted by 8 m
tile (of the stored float32 x and z), then x, then z, copies at one place in package
order; row n is `plant:<set>-n`.

- **Row:** the record's pivot, yaw folded into -180..180, scale and tint; `top_m` from
  the mesh's height; the `submerged` flag when the topmost water surface over the
  pivot is above it; a group; a variety seed (CRC-32 of the float32 x and z and the
  set's name, so a copy keeps it wherever its row moves).
- **Trees:** the row's tail is `trees.fit`, the same fit the merged `tree_climb` part
  had, as numbers: the trunk's axis (the centre of its top ring), its radius at the
  base and at the top (the pole's), the trunk's top, the pole's top (5 mm under the
  highest perch) and each crown's cap (rim ellipse, rim height, flat top ellipse and
  height). `cap_slots` is the set's most caps (broadleaf 5, conifer 1).
- **Species, fuel and seeds:** the prototype's name (`grass_tuft` is grass,
  `flower_clump` flowers, `boulder` rock); fuel `grass` for grass and reeds, `shrub`
  for shrubs, `herb` for other plants, `broadleaf` or `conifer` for trees, `none` for
  stones; plants and trees seed, stones do not.
- **Groups** are found here (the package does not record them): trees of any set within
  0.5 m of one another chain into a grove of at least 3; flowers of one species, reeds,
  ferns and heather within 0.15 m chain into a patch of at least 6. Grass, shrubs and
  stones are in no group. Numbers run from 1 in table order (sets by name, rows in
  order), every one used. At 10x the garage has 3,441 groups; the biggest are flower
  patches of a few thousand.

## Terrain tiles (the 10x world)

`terrain_tiles` follows contracts/README.md, "Terrain tiles" (`terrain.py`). Each
terrain role's GLB holds a root node `tile_<tx>_<tz>_lod<n>` per 16 m tile and level,
its own mesh, positions in room metres. Levels are the package's grid
(`x_generator.cell_m`, else 0.1 m) and 4 and 16 times it: 0.1, 0.4 and 1.6 m at 10x,
0.03, 0.12 and 0.48 m at 1x. At 10x level 0 also holds the generator's 2.5 cm detail
blocks (`x_generator.detail`, S4): the land is one conforming mesh, cut and clustered
like the rest.

- **Level 0** is the package's triangles; one crossing a tile line is cut there, each
  cut point interpolated from its edge's lower endpoint so both tiles hold the same
  bits. Cut points that the GLB stores as one float32 point are one vertex, and a piece
  left with two equal corners is dropped (near a tile's corner a 2.5 cm cell's diagonal
  can pass through it). Normals and colours are the whole part's (interpolated at
  cuts), so tile lines do not show. Only level 0 is ground to walk on and has no skirt.
  At 10x it covers the package's land exactly: the same area, the same open edges (split
  where they cross a tile line), no edge shared by more than two triangles.
- **Levels 1 and 2** cluster the level before's vertices, all roles of a tile together,
  on a grid of the level's spacing (an inside cluster at its points' mean); a point on a
  tile's edge stays on it, clustered along that edge to the member nearest the cell's
  middle, which the neighbouring tile picks too, so tiles of one level meet without
  cracks. A role whose triangles all collapse in a tile keeps the level before's (its
  sliver is already covered by the other roles). Each hangs a skirt of its spacing below
  its tile edges to hide the seam against a finer neighbour. At 10x the garage's land
  (S4's ground: 0.99M base and 1.55M detail triangles) is 2.58M, 121k and 11k triangles
  at the three levels (before S4: 1.10M, 120k, 11k); sampled from above, level 1 is
  1.6 mm off level 0 at the median (4.8 cm at the 95th percentile) and level 2 3.3 cm
  (39 cm); inside the detail blocks 1.9 mm (4.8 cm) and 2.6 cm (25 cm). The largest
  misses are on cliff faces.
- **Encoding:** float positions and normals, `COLOR_0` as normalized bytes, 16-bit
  indices where a node has fewer than 65,536 vertices (other GLBs are unchanged).

## Speed

The 10x garage (S4's ground: 2.5 million land triangles) exports in 38-40 s and 96 MB on
a shared machine; the export before S4, back to back, took 30-31 s and 60 MB. The land's
work runs in two more processes (`landwork.PARTS`: the tiles and open edges, about 22 s;
the trees' fits, about 18 s) beside `read_package`, the longest of the three; the
harness checks a mesh's vertices and triangles inline (S4's land: 20 s to 13.5 s).

At 10x a third of the export is the land's (`landwork.py`: tiling, the open-edge
diagnostic, every scatter tree's fit) and as much again is `read_package`'s
validation. Packages of 4 MB or more start the land's work in a second process on
the package's files (each checked against package.json's pins) before
`read_package` runs, and use its answer once `read_package` has passed and both
read the same package.json; smaller packages run it inline after `read_package`,
with the same bytes (a test compares them). The terrain index answers the main
process's few hundred height questions, building each 4 m block on first use.

Tree poles reuse the trunk's top perimeter, overlap it by 1 cm and finish 5 mm
below the highest cap's flat perch, 1.5 cm below the visual crown top. Broadleaf
caps approximate each lobe's upper quarter. Conifers get only their highest
tier's upper half; lower skirts stay decorative. Each cap has 12 collar sectors
and a flat top fan (36 triangles). Needle tips become small flat perches with a
radius at least the pole radius plus 2.5 cm, allowing the 2 cm capsule to stand
beside the pole. Caps have no underside, bottom or vertical edge wall. All glTF
front faces (CCW) point upward and outward, including horizontal top faces.

Clearance is **12 cm above terrain**, the 10 cm walker plus 2 cm of margin.
The exporter clips terrain triangles to each cap's entire footprint rectangle
and uses its highest terrain point, including interior peaks and overhangs,
rather than sampling only beneath the trunk. Low lobes that cannot clear this
height are omitted; the highest crown must clear it or export fails before any
output is written. Clearance and pole offsets apply after instance transforms,
so nonuniform scale does not shrink the margin. Carriable trees fail explicitly:
their merged static colliders cannot follow a moved entity. Shrubs, grass, ferns
and flowers get no climbing geometry; other collision and all source visuals
remain unchanged. Populated trees never add more than two shell parts/files.

**Collision only:** both populated-tree parts carry `"drawn": false` (the room contract's
collision-only parts), so the game builds their collision and never renders
them; the trees' own meshes stay the drawn ones. The caps rely on one-sided mesh
collision (the contract's rule: a body passes through a face from behind) and
on the glTF importer keeping front-face winding.

GLBs contain one primitive per primary harness role, `NORMAL` and `COLOR_0`, no
external resources, and identity scene nodes. Material `extras.role` preserves the
primary harness role, `role_base_color` its exact linear library colour, and
`blend_roles` the secondary roles. The exporter imports the **single palette table
in harness/common.py**, which mirrors the library used by harness/worker.py;
roughness and metalness mirror that worker too. It bakes vertex and instance tints
and linear role blends, without Blender's noise, bevels or brush textures.

For an unblended role the material factor is exactly its library colour. A blended
role uses the per-channel envelope of its primary/secondary library colours, with
`COLOR_0 = tinted_blended_colour / envelope`. This preserves brighter blends without
clipping or multiplying the role colour twice: glTF limits vertex colour to [0,1]
and multiplies it by the material factor ([glTF 2.0](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#meshes)).
Normals average area-weighted faces at shared indices; duplicate vertices remain
separate, keeping authored creases. Scaling precedes yaw and translation, including
non-uniform scale; normals are calculated after that transform.

**Current game limitations:** the Look pass replaces these imported materials with
a shader that ignores vertex colour. The report proposes preserving plain imported
materials until Lane L's role loader exists. Season, day and solar time in the setup
extension also await Lane L. Exported terrain may have open edges: their exact
endpoints are recorded in `extensions.x_landscape_terrain_open_edges`; this is a
diagnostic, not a closure proof. The body now enforces horizontal room bounds
through `SmallPlayerController.WithinBounds` (velocity limiting) and
`HoldInsideBounds` (post-move clamping), alongside fall recovery. No perimeter
collider export is required. Internal holes, concave cut-outs,
terrain connectivity, collision and navigation still require game testing.

Identical ordered packages and arguments produce identical bytes, inline or with the
second process, with a fixed
export-format creation timestamp. JSON is sorted UTF-8/LF. Every output file other
than `room.json` is pinned in that manifest, and each asset pins its own GLB.
No exported package or room belongs in Git.

`x_landscape_source` gives Lane L the rough original room for its future skippable
15-second intro. It is data, never instructions or live gameplay entities. Its
`room_id`, `room_sha256` and `inventory_sha256` pin the source files' exact bytes;
`bounds`, `shell.parts`, `shell.openings` and `objects` need no external files.
Part geometry retains polygon winding/thickness; walls add `height_m` (vertical
span of their points). Ceiling polygons are included to support the sky dissolve.
Objects retain C4's `id`, `kind`, `position_m`, `size_m`, `colours` (hex/share),
`support` (kind/height/target), and supplied `rotation` and/or `yaw_deg`;
inventory `confidence` maps to `kind_confidence`. Both the corpus and roomscan
use these fields. No labels, display names, notes, descriptions, evidence,
recipes, location metadata or truth oracle are copied.

Coordinates/support heights are finite within +/-1000 m; box sizes are positive
and at most 1000 m; yaw is within +/-360 degrees, quaternion components within
[-1,1] with unit-length tolerance 0.001, and confidence/colour shares within [0,1].
Source lists allow 128 shell parts, 64 openings, 64 points per polygon, 20,000
inventory objects and three colours per object. Thickness is positive up to 1 m;
opening dimensions are positive up to 20 m. Invalid data fails before output
creation. C3 polygon shells are supported; source mesh shell parts fail explicitly
because copying only a mesh path would leave the intro without its geometry.
Parts/openings/objects sort by id, colours by descending share then hex; polygon
points and quaternion components keep their authored order.

The [extension contract](../../../contracts/common.schema.json) allows up to 32
keys matching `^x_[a-z0-9_]{1,63}$`, with no value shape or byte limit; this export
also carries the loose-item evidence key. Animation, timing and skipping are Lane L's later game work.
