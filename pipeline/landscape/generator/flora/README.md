# The plant maker

Run 4, packet FLORA-1 (`docs/runs/RUN-4-FLORA.md`). A species sheet goes in; a pinned plant comes out: a tree in three levels of detail, with the collision a body climbs and the skeleton it grew from. Today there is one sheet, the oak, pinned two ways: leaves as solid clumps and as cut-out cards.

Nothing here runs in the game or when a land is generated. The maker runs when a sheet changes; everything else reads the pinned files.

```sh
python -B -m pipeline.landscape.generator.flora.make             # rebuild every pinned plant from its sheet
python -B -m pipeline.landscape.generator.flora.make --check     # exit 1 if a pinned plant differs from its rebuild
python -B -m pipeline.landscape.generator.flora.make --report    # the counts, nothing written
python -B -m pipeline.landscape.generator.flora.make --preview .cache/scratch/flora   # outlines as PNG, nothing pinned
python -B -m unittest pipeline.landscape.generator.flora.tests.test_flora
```

| File | Holds |
|---|---|
| `sheets/<species>.json` | A species sheet, written by hand |
| `pinned/<name>.json` | A pinned plant: bytes, never edited by hand (`.gitattributes` here keeps Git from converting them) |
| `sheet.py` | Reads and checks a sheet; `DEFAULT_STYLE`, the one place the leaf style is chosen |
| `grow.py` | The skeleton: trunk, limbs, boughs, twigs |
| `shape.py` | Skins: a stem's tube, a clump's solid, a card |
| `make.py` | The whole plant: fit to the caps, leaves, levels, collision; the command |
| `pinned.py` | The pinned frame, bytes and checks |
| `det.py` | Arithmetic that gives the same bits on every machine |
| `preview.py` | Outlines for whoever writes a sheet. Certifies nothing about the look in the game |
| `tests/` | The proofs, and two made-up sheets that are not species of the game |

## How a tree is made

1. **The crown is an outline with light in it.** The sheet gives the crown's width at each height (`crown.profile`) and how far the outline swells and dips round the tree (`crown.lobes`). Light points are sown in the outline's shell, none nearer another than `crown.light_gap_m`. Leaves live where the light is, so the inside of the crown stays bare.
2. **The trunk grows in flushes.** The bole stands upright to `trunk.bole`. Above it the leader runs `trunk.leader` of the way up the crown, in as many flushes as the limbs need (`orders[0].whorl` limbs a flush). A leader of 1 is one stem to the top (a spruce); the oak's 0.35 gives way to limbs: a forked crown.
3. **Buds sit by rule and wake by light.** Each flush ends in a cluster of buds standing evenly round the stem; each cluster is turned from the last by `divergence_deg`. A side stem leaves at `angle_deg`: wide low on its parent, steep near the tip. A bud wakes only if it sees light; if not, it tries the next places round the stem, then sleeps.
4. **A shoot bends.** Each step it leans away from its last bud (`kink_deg`, the zigzag of an oak), turns toward free light (`light`), sags under its weight near its base (`sag`) and lifts toward its tip (`lift`). It stays inside the outline, and it takes the light it reaches, so no two boughs fill the same air.
5. **Thickness is the pipe model.** Every leaf-bearing tip, and every light point it holds, sends one strand of pipe to the root. A stem's radius is the trunk's times (its strands / all strands) to the power 1 / `pipe_exponent`, so where a stem forks the areas add, and the bole carries the whole crown.
6. **The wood is fitted to the triangle cap.** If the bark leaves no room for the sheet's leaves under `levels[0]`, the weakest childless stems go first: twigs before boughs before limbs.
7. **Leaves hang from twigs.** Each tip takes the light points nearest it; tips are joined, nearest first, into at most `leaves.count` clumps. A clump is a twenty-sided solid, flatter beneath, lit above and dark below; or a fan of cards. Each names the twig tip it hangs from.
8. **Three levels are authored, not derived.** The finest has every stem. The middle has the trunk, the thickest limbs and the clumps joined into masses. The far one is a three-sided trunk and a lantern drawn round the finest level's leaves, so the far outline is the near one. No lighter level reaches past the finest one's leaves.
9. **The collision is read off the drawn tree.** The trunk is a tapering post to the first limbs; a hidden pole runs on up the same axis; each cap is a round collar under a round perch at the top of the drawn leaves there, less a centimetre. The first cap stands over the pole. The rest crown the limbs' masses, the heaviest first, at most `climb.caps` and never more than eight.

## Where the method comes from

The maker is this project's own code. It was built from these ideas, each named in `grow.py`:

- **Architectural models.** Hallé, Oldeman and Tomlinson sort trees by how the trunk and side axes grow. Oak, pine and maple follow Rauh's model: a trunk growing in flushes, each ending in a tier of limbs that are small trunks themselves. Spruce and fir follow Massart's: the same trunk, limbs held in flat tiers. `model` in a sheet names one; `sheet.py` `MODELS` lists what each sets. Tomlinson, *American Scientist* 1983: https://harvardforest1.fas.harvard.edu/publications/pdfs/Tomlinson_AmScientist_1983.pdf. Old oaks measured against Rauh's model: https://www.biorxiv.org/content/10.1101/795286v1.full.pdf
- **Space colonisation.** Runions, Lane and Prusinkiewicz (2007) grow a tree toward attraction points in an envelope and remove each point a branch reaches. Here the points only steer and feed shoots that a rule has already placed: https://algorithmicbotany.org/papers/colonization.egwnp2007.html
- **The pipe model and Leonardo's rule.** Shinozaki's pipe model (1964) treats a tree as strands running from leaves to root; Leonardo's rule says the areas of the daughters add up to the mother's. Measured exponents lie between 1.8 and 2.3 (Eloy 2011, https://arxiv.org/abs/1105.2591). The oak's sheet takes 2.3, the stout end.
- **Leaf arrangement.** An oak's leaves stand in a 2/5 spiral, 144 degrees from one to the next (https://mathworld.wolfram.com/Phyllotaxis.html); the sheet's `divergence_deg`.
- **From memory, not opened for this packet:** L-systems (Prusinkiewicz and Lindenmayer, *The Algorithmic Beauty of Plants*); Honda's branch angles and length ratios (1971); Weber and Penn's parametric trees (1995); bud fate by light in self-organising trees (Palubicki and others, 2009); that an oak's buds cluster at the tips of its shoots, which is why its limbs come in tiers.

## The species sheet

Every field is required unless marked. An unknown field fails. `tests/sheets/` holds two examples besides the oak.

| Field | Means |
|---|---|
| `species`, `kind`, `fuel_class`, `seeds` | As a plant set's (`contracts/README.md`, "Plant copies"). `kind` is `tree` today |
| `seed` | The individual. Another seed is another tree of the same kind |
| `model` | `rauh` or `massart` |
| `base` (optional) | Another sheet in the folder to lay this one over: objects merge, `orders` merges order by order, all else replaces |
| `styles` (optional) | Leaf styles to pin, as `<species>_<style>`. A sheet without it is pinned once, as `<species>`, in `DEFAULT_STYLE` |
| `note` (optional) | For the reader |
| `size.height_m`, `size.spread_m` | The outline's height and greatest width. The pinned `size_m` is what grew |
| `trunk.bole` | Height of the first limbs, as a share of the height |
| `trunk.radius_m` | The bole's radius above the flare |
| `trunk.flare`, `flare_height` | Radius at the ground over the bole's; the flare's top as a share of the bole |
| `trunk.roots`, `root_depth` | Root ridges round the foot (0 for none) and how deep they stand, as a share of the radius |
| `trunk.sweep` | How far the leader wanders a flush, as a share of the height. The bole itself is upright |
| `trunk.leader` | How far up the crown the trunk stays one stem |
| `trunk.sides` | Corners of the trunk's rings at the finest level |
| `crown.base` | The crown's underside, as a share of the height |
| `crown.profile` | `[height, width]` pairs, both as shares, heights rising from 0 to 1 |
| `crown.lobes` | How far the outline swells with one, two, three lobes round the tree |
| `crown.shell` | The inner and outer edge of the lit shell, as shares of the outline |
| `crown.light_gap_m` | The spacing of light points. Finer costs time, not triangles |
| `crown.flag` | 0, or how far the crown leans toward +X as it rises: a wind-flagged tree. A planter turns +X downwind |
| `divergence_deg` | The turn from one bud cluster to the next |
| `pipe_exponent`, `twig_radius_m` | Step 5, and the thinnest a stem is drawn |
| `orders` | One to three: limbs, boughs, twigs |
| `orders[].count` | `[fewest, most]` side stems a parent |
| `orders[].whorl` (optional, 1) | Most side stems at one joint |
| `orders[].angle_deg` | `[near the parent's tip, near its base]`, from the parent's heading |
| `orders[].reach` | Length, as a share of the way to the outline |
| `orders[].segments`, `sides` | Steps along the stem; corners of its rings |
| `orders[].kink_deg`, `light`, `sag`, `lift` | Step 4 |
| `orders[].flat` (optional, the model's) | 0 to 1: how level the stem is held |
| `leaves.count` | Most clumps at the finest level |
| `leaves.size_m`, `cover`, `flat`, `under` | A clump's least and greatest half-width; how far it overlaps its light; its height over its width; how much flatter it is beneath |
| `leaves.reach_m` | How far from a tip its leaves may hang |
| `leaves.jitter` | How uneven a clump's corners are |
| `leaves.shade` | How dark a clump's underside and the crown's inside are. 0 switches the shading off |
| `leaves.cards` | Cards a clump, in the card style |
| `levels` | Triangle caps, finest first. A tree's are 1,500, 300 and 60 |
| `climb.caps`, `perch` | Most caps (eight at most); a perch's width over its rim's |

## The pinned plant

`pinned/<name>.json`, format `garage_game.flora_mesh` version 1. `pinned.check` holds its rules and runs on every load.

| Field | Holds |
|---|---|
| `name`, `species`, `leaf_style`, `kind`, `fuel_class`, `seeds` | What it is. `leaf_style` is `clumps` or `cards` |
| `maker` | The maker's version, the sheet's seed, model and hash (of the resolved sheet's canonical JSON) |
| `size_m` | The finest level's bounds |
| `levels` | Three, finest first, in one frame: `triangle_count` and two `parts`, bark then leaves |
| a part | `role` (`bark` or `foliage`), `positions`, `triangles`, `tints` (red, green, blue, 0 to 1): the kit's shape, so the exporter's `encode` takes it as it is (`flora.levels`, `flora.prototype`) |
| a leaf part, also | `clump`: for each corner, its clump. `twigs_m`: for each clump, the twig tip it hangs from. Drawing every corner in to its twig point bares the tree for winter |
| a card part, also | `uvs`: each card is four corners, (0,0) (1,0) (1,1) (0,1), `u` across and `v` up. Drawn from both sides |
| `crown_m` | A point inside the crown. The direction from it to a leaf corner is a soft shading normal for cards |
| `collision` | `axis_m` (x, z), `trunk_radius_m` (at the foot: half-way out the flare), `pole_radius_m` (the bole's top, and the hidden pole), `trunk_top_m`, `pole_top_m`, `clearance_m` (the lowest rim), `caps` |
| a cap | Eight numbers, in the order `export/trees.py` and a tree's row use: x, z, rim half x, rim half z, rim height, top height, top half x, top half z. Round, so a copy turns freely |
| `skeleton` | Every stem: `order` (0 the trunk), `parent`, `at` (the parent's point it leaves), `points` of x, y, z and radius. A stem's last point is its tip, radius 0 |
| `counts` | Limbs, boughs, twigs, flushes, clumps, light points, stems pruned to fit |

**The frame.** Metres, +Y up, the pivot at the bottom centre of the finest level's bounds, as `harness/package.py` asks of every prototype. Every coordinate is a whole number of twentieths of a millimetre, so the bounds are centred exactly, in the file and in a GLB's 32-bit floats. A crown is never quite centred on its trunk: `flora.stand` gives the pivot that sets the trunk on a chosen spot.

**The bytes.** JSON, keys sorted, LF, ASCII, numbers in plain digits. The same sheet gives the same bytes on every machine, because `det.py` computes with +, -, *, / and square roots only, in a fixed order, and draws its random numbers from whole-number arithmetic. The tests rebuild each pinned plant and compare bytes; so does `--check`.

**The fixture key.** `tools/landscape-fixture.ps1` hashes `.py` and `.json` under the generator, so a change here rekeys the landscape fixtures. Nothing under `tests/` is hashed.

## What the next packets take

- **FLORA-2 (the exporter).** `flora.levels(name)` gives the three levels ready for `encode`; `flora.collision(name)` and `flora.placed(name, scale, position_m, yaw_deg)` give trunk, pole and caps in the shape `trees.fit` returns. What is left to the exporter is the ground: a rim 12 cm over the land under it. The kit's trees are untouched.
- **FLORA-20 (planting).** `flora.prototype(name)` is `(parts, size_m)` for a package's `prototypes`. `collision.clearance_m` times a copy's scale is the room under its lowest cap. The oak was made to stand about a metre tall.
- **FLORA-21 (more sheets).** Write `sheets/<species>.json`, run the maker with `--preview`, then without. A veteran or a flagged form is a sheet with a `base`. A new branching model is a line in `MODELS`; a bush needs a new `kind` (several stems from the ground, no collision), which is not built.
- **FLORA-7 (the look).** Cards carry `uvs`; every leaf corner carries its clump and every clump its twig; `crown_m` gives cards a normal.

## Beyond what was asked

Each is here because it cost little; each has its switch.

- **The same bytes on every machine** (`det.py`), so the rebuild check is not tied to the machine that pinned. No switch: nothing draws.
- **The far level is the near outline** (the lantern), so a tree does not change shape as it swaps levels. No switch: it is the far level's one form, sixty triangles or fewer.
- **Round caps, perches on the drawn leaves, and the first cap over the pole.** `climb.caps` 1 leaves only the pole's cap.
- **Leaves shaded in their corners:** lit above, dark and cool beneath, darker inside the crown. `leaves.shade` 0.
- **Root ridges and a dark foot.** `trunk.roots` 0, `trunk.flare` 1.
- **A flagged crown** for wind-shaped trees. `crown.flag` 0, as the oak has it.
- **Sheets laid over a base.** Leave `base` out.
- **The skeleton pinned with the mesh,** for winter, for bridges between crowns, for the tests. Readers may ignore it.
- **The preview.** Runs only when asked; writes only where told.

## Not verified

- No picture from the game. Whether solid clumps read as leaves, and how cards read, is the founder's eye in the first basin (RUN-4.md D7). The previews are flat-shaded triangles and show cards as plain sheets.
- The pinned bytes were made and rebuilt on one Windows machine with one Python (3.14). The arithmetic is built to agree elsewhere; no other machine has run it.
- No copy has been through the exporter or the game: FLORA-2 and FLORA-20.
- The card picture does not exist yet; the cards' size and number are a first guess.
