# roomscan: room capture pipeline (Lane C)

Turns a folder of room photos into a coverage report (which parts of the room the photos cover, and specific
instructions for the photos to take next), then into the room's **shell** as a room manifest the game loads, and an
**inventory** of its objects with a stand-in recipe request for each kind a recipe exists for. It is stages C1 to C4 of
the [room capture pipeline](../../docs/pipeline/ROOM-CAPTURE-PIPELINE.md); the design notes for C3 and C4 are in
[SHELL-AND-INVENTORY.md](../../docs/pipeline/SHELL-AND-INVENTORY.md).

- **C1 `roomscan ingest`**: HEIC to JPEG, EXIF kept by allow-list (device, lens, focal length,
  exposure, timestamp) with GPS and every other location field stripped, exact and near-duplicate
  detection, blur and exposure-clipping scores, and a session manifest of every photo's name, size
  and SHA-256. CPU only.
- **C2 `roomscan coverage`**: camera poses from MapAnything in GPU batches that fit 8 GB, merged
  into one frame and refined; a view graph of which photos see the same surfaces; the room's floor,
  walls and ceiling fitted as a box; coverage grids (25 cm cells) of each surface; furniture found
  by an open-vocabulary detector, with per-object photo counts and the sides it was seen from; and
  ranked, plain-language guidance, a top-down coverage map and an HTML report.
- **C3 `roomscan shell`**: the floor, four walls and ceiling as planes (checked against the photos' points, coloured from
  them), the openings a reviewer marks (doors, windows, the garage door), a coarse `site` and clear spawns, written as a
  room manifest to the player's user data and checked with `contracts/validate.py`. CPU only.
- **C4 `roomscan inventory`**: the objects across the photos, each with a kind, a 3-D box in room coordinates (size, place,
  turn, which way it faces), broad colours, a confidence and, for the founder's five recipe kinds, a recipe request that
  `pipeline/recipes` accepts as it is; five objects picked for stand-ins; a top-down review image. Detector and segmenter
  on the GPU for about two minutes; everything after is CPU.

## Rules it follows

- The source folder is **read in place and never written**. Everything derived goes under
  `<repo>/captures/<room>/`, which Git ignores. Every write goes through `OutputGuard`, which refuses
  paths outside that folder or inside the source folder.
- Written images carry no GPS, maker notes, XMP or IPTC; each write is checked and fails closed.
- A session is identified by the sorted (name, size, SHA-256) listing of the photos together with the
  ingest version and the quality thresholds, so the same set under the same rules always maps to the same
  session, and adding photos or changing a rule makes a new session without touching the old one. An
  existing session's manifest is never overwritten by a different one.
- Derived JPEGs and thumbnails are named by the first 16 hex digits of the source's SHA-256 and nothing
  else, so the same bytes always give the same file.
- Names of files and EXIF text are treated as data: escaped in the HTML report, and escaped for Markdown in
  both Markdown reports.
- Model weights and caches go to `<repo>/.cache/` (ignored by Git), never the user profile.
- GPU stages pause while a window titled `EnFractal...` is open (the founder may be playing on the
  same card) and wait for free VRAM.
- Photos, derived images and reports of a real room are never committed.
- A captured room is written to the player's user data (`%APPDATA%\Godot\app_userdata\Garage-Game\rooms\<room>\` on
  Windows) and nowhere else: `write_room` refuses the resolved destination when it is inside *any* Git checkout (this one,
  another clone, a worktree) or when the room folder is a link that leads out of the rooms folder. It stages the manifest,
  runs `contracts/validate.py` on the staged copy and only then replaces the room that was there: a manifest that fails
  validation, or a validator that cannot run, leaves the earlier playable room untouched.
- Every picture a review helper saves (the shell and object overlays, the review image) goes through `OutputGuard`: a
  `.jpg` or `.png` inside the room's capture folder, and that folder must itself be a checkout's ignored `captures/` or
  outside every checkout. A path anywhere else (a `docs/` folder, another clone) is refused before any work is done.
- The manifest carries no GPS, no place and no longitude; its `site` is whole degrees of latitude, the bearing of -Z and a
  quarter hour of solar noon, which the contract's schema enforces.
- The inventory, the shell spec, the curation file and the review image stay under `captures/`. Commit only code, tests
  with synthetic fixtures and docs.

## Install

Needs [uv](https://docs.astral.sh/uv/). uv provides Python 3.12 and installs the locked versions into
`pipeline/roomscan/.venv`. Nothing is installed globally.

```powershell
cd pipeline/roomscan
uv sync --locked                 # C1 and the tests: CPU only, about 150 MB
uv sync --locked --extra pose    # adds C2 on an NVIDIA GPU: torch 2.14.1 + CUDA 12.6, about 4 GB
uv sync --locked --extra detect  # adds C4's detector and segmenter without MapAnything: torch, torchvision, transformers
```

With the `pose` (or `detect`) extra installed, **always pass the same `--extra`** to `uv run` and `uv sync`. Without it,
uv's exact sync removes the GPU packages again. C3 and the tests need neither.

The first `roomscan coverage` downloads the models into `<repo>/.cache/`:

| Model | Use | Size | Licence |
|---|---|---|---|
| `facebook/map-anything-apache` @ `00f9c24` | camera poses, depth | 4.91 GB | Apache-2.0 |
| `google/owlv2-base-patch16-ensemble` @ `cfd3195` | furniture detection (C2) and the inventory's kinds (C4) | 0.62 GB | Apache-2.0 |
| `facebook/sam2.1-hiera-small` @ `ee5bba1` | object masks from detector boxes (C4) | 0.18 GB | Apache-2.0 |
| `facebookresearch/dinov2` @ `7764ea0` (torch.hub, code only) | encoder definition MapAnything builds on | 4 MB | Apache-2.0 |

All three allow commercial use. One caveat on the third: at the pinned commit, DINOv2's `hubconf.py` also
imports the Cell-DINO (CC BY 4.0 code) and X-Ray-DINO modules, whose weights are non-commercial research
only. Importing it runs that code, but no Cell-DINO or X-Ray-DINO weights are fetched or used (the encoder
weights come from the MapAnything checkpoint). Not used, because they are non-commercial or gated:
`facebook/map-anything` (CC BY-NC 4.0), `facebook/VGGT-1B` (CC BY-NC 4.0), `naver/MASt3R`
(CC BY-NC-SA 4.0), `facebook/VGGT-1B-Commercial` (gated application). See `MODEL_REGISTRY` and
`REJECTED_MODELS` in `src/roomscan/coverage/backend.py`.

## Run

Pass the photo folder as an argument; never hard-code it. On the founder's machine Google Drive for
desktop shows it as a local folder; `tools/check-run1-readiness.ps1` finds it.

```powershell
cd pipeline/roomscan
uv run --locked --extra pose roomscan ingest --source "<folder of photos>" --room garage
uv run --locked --extra pose roomscan coverage --room garage
uv run --locked --extra pose roomscan sessions --room garage    # list sessions
```

C3 and C4 start from the poses C2 cached, so they need no pose model:

```powershell
uv run --locked --extra detect roomscan inventory --room garage   # C4: detector and segmenter on the GPU, then CPU
uv run --locked roomscan shell --room garage                       # C3: CPU only; writes the room to the game's user data
```

`inventory` reads `captures/<room>/inventory-curation.json` if it exists; `shell` reads
`captures/<room>/shell-spec.json`. Both are a reviewer's decisions, kept next to the capture (see
[SHELL-AND-INVENTORY.md](../../docs/pipeline/SHELL-AND-INVENTORY.md)). `shell` takes `--rooms-dir DIR` (default: the game's
`user://rooms`), `--created-utc` (fix it to rebuild the same bytes), `--spec PATH` and `--no-pictures`; `inventory` takes
`--curation PATH` and `--min-evidence X`. Run `inventory` first when a room has one: `shell` keeps the avatars' spawns off
the objects it picked.

`coverage` uses the latest session unless `--session s-...` is given. Other options:
`--chunk-size N` (photos per GPU batch; default from free VRAM, at most 32),
`--skip-detection` (no furniture, no GPU detector), `--recompute` (ignore cached poses),
`--max-views N` (a quick test on a subset), `--measurements PATH` (tape measurements; default
`captures/<room>/measurements.json` when it exists) and `--no-measurements` (keep the model's own scale).

Outputs, all under `captures/<room>/`:

| Path | What |
|---|---|
| `coverage-report.html` | **The report for the founder**: one self-contained page with the map embedded |
| `coverage-map.png`, `walls.png` | Top-down coverage map with numbered next photos; the walls unfolded |
| `coverage-report.md` | The same report as Markdown |
| `photos/`, `thumbs/` | Clean full-size JPEGs and 384 px previews, no location data |
| `sessions/<id>/manifest.json` | C1 result: every photo's name, size, SHA-256, EXIF, scores and flags |
| `sessions/<id>/ingest-report.md` | Duplicates, flags and lenses in brief |
| `sessions/<id>/coverage/coverage.json` | Every number behind the report, including the scale and the tape fit |
| `sessions/<id>/coverage/coverage-run.json` | Stage times, GPU seconds, peak VRAM, models and licences |
| `sessions/<id>/coverage/poses.npz`, `detections.json` | Caches; reused when the photos and settings match |
| `shell-spec.json` | C3 input, the reviewer's: openings, site, lamps, spawns, surface colours |
| `shell/plan.json`, `shell/wall_a.png` ... | C3: each plane's evidence and colours; the walls unfolded from the photos |
| `inventory-curation.json` | C4 input, the reviewer's: proposals to drop or relabel, objects to add, measurements |
| `inventory.json` | C4: every object (kind, box, colours, confidence, recipe request) and the five picks |
| `inventory-review.jpg` | C4: the room from above with every box, the picks numbered, and a panel for the five |
| `sessions/<id>/inventory/` | C4 caches and evidence: `detections.json`, `masks.npz`, `evidence/<object>.jpg` |

## Measured on the RTX 2070 SUPER (8 GB), October 2026

- MapAnything, fp16 trunk: 32 photos per batch, about 14 s each, peak 5.8 GB allocated
  (6.5 GB reserved). 8 photos: 3.9 GB; 24 photos: 5.2 GB.
- Model load: about 30 s from the cache (about 4 min the first time, including the download).
- Ingest of 370 iPhone HEIC files (911 MB): about 50 s on 6 CPU workers.
- Coverage of the 180 usable garage photos, from scratch: about 6 min. That is 96 s of GPU
  inference for poses, 17 s for furniture, and the rest CPU. A re-run with cached poses and
  detections takes about 1.5 min.
- Inventory of the same set: 69 detector phrases over 179 photos, each whole and as a 2 x 2 grid of tiles from a
  1536 px copy: 83 s of GPU inference (peak 0.9 GB), 3.6 min with image decoding. SAM 2.1 small: 297 masks in 35 s
  (peak 0.5 GB). A rerun with both caches warm takes about 25 s of CPU, most of it loading the poses (3 s) and the points.
- The shell: loading the scene 3 s, the plane evidence and colours 15 s, the rectified wall pictures 15 s.
- Downloads for C3 and C4 (October 2026): torch 2.14.1+cu126, torchvision and transformers 5.19 into the project's
  `.venv` (4.4 GB of wheels, 2 min); SAM 2.1 small's `model.safetensors` (184 MB). The OWLv2 weights were copied from the
  C2 cache (nothing downloaded). Nothing hosted, nothing paid.

## Test

```powershell
cd pipeline/roomscan
uv run --locked --extra pose pytest -q    # or: uv run --locked pytest -q (CPU-only environment)
```

The tests use only synthetic images and a ray-cast synthetic room (`tests/synth.py`,
`tests/scene.py`). A fake pose backend returns each batch in its own random frame and scale, and a
fake detector returns the true furniture boxes, so the whole C2 pipeline runs end to end without a
GPU, a model or a real photo. The one test that touches torch is skipped when torch is absent.

C3 and C4 add `tests/test_shell.py`, `tests/test_inventory.py` and `tests/test_inventory_fit.py`. Their scene
(`tests/inv_scene.py`) is ray-cast at the real stored-map size from a known room, so a lifted box, a fitted box or a
silhouette is checked against the truth the room was built from; the segmenter is a fake that returns known pixels. The
shell test writes a manifest to a temporary folder and runs `contracts/validate.py` on it (the validator's pinned
packages are in the dev group).

## How it works

1. **Ingest** (`ingest.py`, `exif.py`, `quality.py`). Blur is judged on two measures: the variance
   of the Laplacian on the sharpest quarter of tiles (relative to the session median), and the
   contrast-independent ratio of Laplacian to gradient on the strongest edges. A photo is *blurry*
   only when both are low. A low score with crisp edges is a plain surface (`low_detail`): it is
   kept, because walls and floors matter for coverage.
2. **Poses** (`coverage/backend.py`, `chunks.py`). One keyframe batch spread over the capture order
   defines the room frame; every other batch shares six keyframes (anchors) with it. Each batch is
   aligned to the room by a similarity fitted on the anchors' point maps. A batch whose anchors
   disagree is left out and its photos are reported, never placed somewhere wrong.
3. **Refinement** (`coverage/refine.py`). A trimmed ICP with normal checks pulls each batch onto the
   surfaces of all the others. The reference batch fixes the merged room's scale; see "Room scale" below.
4. **View graph** (`coverage/graph.py`). Photo i links to photo j when at least 10% of what i sees
   lands in j's view at the depth j measured. Photos outside the largest group are "not fitted".
5. **Room and grids** (`coverage/layout.py`, `grid.py`, `visibility.py`). Floor plane by RANSAC,
   walls by the dominant directions (Manhattan world), each at the strongest plane with almost
   nothing seen behind it; ceiling by downward-facing points. The map is turned so the first photo
   looks up the map, which keeps the wall letters stable between runs. Every 25 cm cell is
   projected into every photo's depth map. Each photo either sees the cell, sees furniture standing
   on or against it, is blocked by clutter further away (still a gap), or sees past it (a doorway
   or window). A cell is well covered when three photos from different directions see it. Photos
   taken below 0.8 m are counted separately, because that is the player's point of view.
6. **Furniture** (`coverage/objects.py`, `detect.py`). OWLv2 with a fixed word list, lifted to 3-D
   with the point maps and clustered across photos. A photo counts for an object when its surface
   points fall inside the object's box, and the side it was seen from is measured.
7. **Guidance and report** (`coverage/guidance.py`, `render.py`, `report.py`). Ranked
   instructions: lower walls and floor first, because the player is 10 cm tall.

## C3: the shell

1. **The scene** (`scene.py`). `load_scene` rebuilds what the coverage run knew before it drew anything: every fitted
   photo's camera and point map in the room frame at the tape-fitted scale. It refits the box with the same functions and
   stops with `SceneMismatch` if the result differs from the room `coverage.json` recorded, so a changed cache or fit
   cannot slip into an exported room.
2. **Planes** (`shell/planes.py`). The box is not fitted again (the ceiling moved 8 to 10 cm with the fifth digit of the
   scale when it was). Each of the six planes is checked against the points on it: how many, how far from the plane
   (median and spread), and how many photos saw it. Each gets one broad colour from the pixels of points within 4 cm of
   it, so furniture and posters in front of a wall do not paint it.
3. **Openings** (`shell/spec.py`, `shell/overlay.py`, `ortho.py`). Which gap in a wall is a door, a window or the garage
   door is a reviewer's call, made from the walls unfolded from the photos (`shell/wall_*.png`, one pixel per centimetre,
   each pixel the median of the three photos that see it best with nothing in front) and checked by drawing the result back
   onto photos. It is written to `shell-spec.json`, read strictly, and cut as real holes by `RoomBuilder`.
4. **The manifest** (`shell/manifest.py`). One polygon per surface, walls A and C running 12 cm past B and D; windows
   light the room, with a `sun` hint; spawns on floor the photos saw clear (the coverage floor grid), clear of the objects
   the inventory picked. Pinned bytes, so the same inputs give the same file.

## C4: the inventory

1. **Detections** (`inventory/detection.py`). OWLv2 is asked for 69 phrases (`inventory/vocabulary.py` groups them into
   kinds, with a plausible size for each and the recipe that builds a stand-in) over every fitted photo, whole and in tiles.
2. **Sightings and clusters** (`lift.py`, `cluster.py`). A box lifts to the points of its middle's distance; the same kind
   at the same place in three photos (two when the detector is sure) is a candidate; candidates sharing most of their
   volume are one object.
3. **A reviewer's corrections** (`curation.py`). Proposals are dropped or relabelled, missed objects added, and boxes
   measured, all by *place* because the order the detector finds things in is not stable. The garage's first automatic
   pass was judged by eye from the evidence sheets, and most of its proposals were clutter or the wrong kind.
4. **Following each object** (`track.py`, `segment.py`, `masks.py`). The object's 3-D position projects into every photo
   that sees it; up to ten of them (close, square-on, spread round it) get a box prompt for SAM 2.1, run on a crop of the
   full-size photo. The mask is judged (size against the box, the box's middle on the mask, overlap), pushed onto the point
   map, and cached.
5. **Box, turn, front, colours** (`fit.py`, `build.py`, `recipes.py`). The pooled points give the smallest rectangle round
   their middle 94 per cent, turned to the room's axes when close, with the height worked out photo by photo. A thing that
   stands on the floor starts on it. The front is where a sofa is lower, away from the wall a thing stands against, or
   toward the photos that saw it. Slot colours for a recipe come from the object's own pixels, and a slot the scan cannot
   show is left out so the recipe uses its default.
6. **Picks and the review image** (`picks.py`, `review.py`). The founder's kinds first; where the room lacks one, the
   best-evidenced object of a kind not yet picked, from a different kind of thing, with the recipe it still needs named.

Sizes and places are good to about 15 per cent and 10 cm: the pose model's depth is noisy at edges (its own edge masking
shrinks small things) and its poses move a box by a few centimetres between photos. The review image and the `confidence`
say so; a reviewer's measurement replaces a fit where the photos plainly disagree.

## Room scale: the model's guess and the founder's tape

Each GPU batch comes back in its own metric frame and the batches disagree by tens of percent: in the
garage set, taking any one batch's own scale as right would put the room from 1.6% smaller to 26% larger
than the first (seed) batch does, and every one of them put it larger than the founder's tape does.
**The merged room is in the seed batch's scale** and that spread is only an error bar. Nothing in the
photos fixes the scale; a tape does.

`captures/<room>/measurements.json` (local, never committed) lists what was measured:

```json
{"room": "garage", "measurements": [
  {"id": "length", "kind": "wall_to_wall", "between": ["B", "D"], "session_id": "s-...", "value_m": 5.842,
   "sigma_m": 0.01, "where": "inside length"},
  {"id": "height", "kind": "floor_to_ceiling", "between": [], "session_id": "s-...", "value_m": 2.464},
  {"id": "corner", "kind": "diagonal", "between": ["AB", "CD"], "session_id": "s-...", "value_m": 7.4},
  {"id": "back-door", "kind": "opening", "between": ["B"], "session_id": "s-...", "value_m": 0.787}]}
```

- `between` is two opposite walls (`A`/`C` or `B`/`D`), two opposite corners, or the one wall an opening is in.
  Wall letters are on the coverage map (wall A is the one the first photo faced). `session_id` is the session
  whose map you read them from: they follow the first photo, so a measurement is used only for that session.
- `sigma_m` is the tape's own error (default 1 cm); the fit adds 10 cm for where the model puts a wall, the
  floor or the ceiling.
- **Fit**: one uniform scale, by weighted least squares on `ln(tape / model)`, never one scale per axis;
  openings are checks only. It is applied to the merged poses and the room box before any grid is built, so
  the 25 cm cells, the 0.8 m low-photo threshold and the guidance distances are in real metres. The box is the
  one fitted before scaling, scaled with the poses: fitting it again on the scaled points moved the garage
  ceiling by 8 to 10 cm with the fifth digit of the factor.
- **The report** gives each measurement against the model in cm and per cent, the scale each would ask for
  alone (information only), the RMS, and flags a residual above twice the typical spread between the batches'
  own copies of a wall (at least 5 cm). `coverage-run.json` and `coverage.json` record `scale_source`
  (`tape` or `seed_batch`) and the applied factor.
- A file with a mistake stops the run before any work, saying what to fix; measurements for another session,
  or an implausible scale (outside 0.6 to 1.6), are left out and the report says why.

## Input handling

- **Digital zoom.** iPhone photos taken zoomed in (DigitalZoomRatio above 1.05) get no EXIF intrinsics: the
  model estimates their focal length itself. The garage set shows why: eight photos at ratio 1.42 keep the
  26 mm equivalent of an unzoomed photo, while the one at 1.71 records 44 mm, which is 26 mm times the zoom.
  EXIF does not say consistently whether the equivalent includes the zoom, and a wrong focal length bends the
  batch it is in. The report lists these photos and asks for 1x.
- **Photo shape.** The model takes 3:4 portrait input. A photo within 8% of that shape is scaled to cover it
  and trimmed (an iPhone portrait loses a pixel or two). Any other shape, landscape or 9:16, is scaled to fit
  and padded with grey, never cropped (a landscape photo cropped to portrait lost 43% of its width); the
  padding is masked out of the point map and the principal point follows the photo.

## Limits

- Room sizes are only as good as the model's metric scale. Without tape measurements they can be off by
  tens of percent (see "Room scale").
- The room is assumed to be a box. An L-shaped room or an alcove shows up as a poor wall fit.
- Furniture detection misses and mislabels things; the inventory treats it as a proposal a reviewer checks (C4).
- The shell is one box: no alcoves, no slanted ceilings, one flat floor (a garage floor that falls toward the door is
  levelled), and openings are rectangles.
- The photos carry no location and no compass. The manifest's `site` comes from the reviewer, and the bearing of -Z is a
  placeholder until the owner says which way a wall faces.
- A box is only as deep as what the photos saw of a thing against a wall, and an L-shaped sofa is one rectangle.
- The blur threshold was calibrated by eye on one photo set (the garage, October 2026).
