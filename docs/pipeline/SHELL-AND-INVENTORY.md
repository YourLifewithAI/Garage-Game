# C3 and C4: the shell and the inventory

**Run 2, Lane C.** The poses C2 computed become two things: the room's **shell** as a room manifest the game loads from
the player's data, and an **inventory** of the room's objects, each ready to feed a stand-in recipe. Code is in
[`pipeline/roomscan`](../../pipeline/roomscan/README.md) (`shell/`, `inventory/`, `scene.py`, `ortho.py`); this page is what
a person, or a player's AI, needs to run the stages and review their results.

Photos, the shell spec, the curation file, the inventory and its pictures describe a real place: they live under
`captures/<room>/` (Git ignores it) and the manifest goes to `user://rooms/<room>/`, never into any Git checkout. The code
enforces it: pictures are written only inside the room's capture folder, the manifest's resolved destination may not be in
any checkout or lead out of the rooms folder, and the manifest is validated before it replaces the room that was playable.

## The loop

```text
ingest  ->  coverage (poses)  ->  inventory  ->  [review the evidence, write corrections]  ->  inventory again
                              \->  shell     ->  [review the wall pictures, write the spec]  ->  shell again  ->  game
```

Both stages are *propose, review, dispose*. The detector and the plane fit propose; a reviewer (the founder, or a vision-capable
AI looking at the pictures the stages write) checks them and writes what it found into a small JSON file next to the capture.
Rerunning applies the corrections. Run the inventory first when a room has one: the shell then keeps the avatars' spawns off
the picked objects.

## Conventions

- **Room frame.** Metres, +Y up, floor at y = 0, centred on the origin in x and z, the first photo looking toward -Z. Walls:
  **A** at z_min (faced by the first photo), **B** at x_max, **C** at z_max, **D** at x_min. Seen from above with -Z up the
  page, +X is to the right (the coverage map's orientation). The scale is the tape fit's.
- **A wall's own axes.** `u` runs left to right as someone inside the room sees the wall (A from x_min, B from z_min,
  C from x_max, D from z_max) and `v` is height. The rectified pictures and the spec's openings use them.
- **A box.** `size_m` is `[width, height, depth]` in the object's own frame: width along its local +X, depth along its
  local +Z, and its front looks along local -Z (Godot's forward). `yaw_deg` turns the front to the direction it faces,
  positive turning left seen from above; `placement.rotation` is the same turn as a unit quaternion for a manifest's
  `transform.rotation`. `placement.position_m` is the **bottom centre**, the asset pivot.
  `footprint_m` is the turned box's axis-aligned `[x0, z0, x1, z1]`.
- **Support.** `floor`, `object` (the top of another inventory object: `target_id`), or `surface` (something above the
  floor the inventory has no object for: a desk that was not found). A stand-in on a `surface` hangs in the air unless the
  surface is built too: see "What C5 needs".

## `shell-spec.json` (the reviewer's, read strictly)

```json
{"schema": "garage_game.shell_spec", "version": 1, "room": "garage", "session_id": "s-...",
 "display_name": "Garage", "description": "...",
 "site": {"latitude_deg": 30, "neg_z_bearing_deg": 0, "solar_noon_h": 12},
 "openings": [{"id": "window_a", "kind": "window", "wall": "A", "u_m": 1.5, "width_m": 0.9, "v_bottom_m": 0.9,
               "height_m": 1.2, "state": "fixed", "traversable": false, "note": "how it was measured"}],
 "lamps": [{"id": "ceiling_light", "position_m": [0.0, 2.4, 0.0], "color": "#fff4e0", "relative_intensity": 0.8}],
 "spawns": [], "surface_colours": {}, "thickness_m": 0.12}
```

- A spec belongs to its **session**: wall letters follow the first photo, so a spec for another session is refused.
- `openings`: `u_m` is the centre along the wall, `v_bottom_m` the lower edge. A window is always a hole (the builder cuts it
  and the sun comes through); a door or garage door is a hole only when `state` is `open`. Openings that run off a wall,
  stand taller than the room or overlap are refused.
- `site`: whole degrees of latitude, the compass bearing of -Z, solar noon on a quarter hour. The contract allows nothing
  finer and there is no longitude. The photos carry no location and no compass, so these come from the owner.
- `spawns` empty means the stage finds two clear spots of floor (photos saw it bare, a metre apart, away from the picks).
- To read openings off the photos: look at `captures/<room>/shell/wall_*.png` (one pixel is a centimetre; the
  window and door recesses show), then draw the result onto photos with `shell.overlay.overlay_sheet`.

## `inventory-curation.json` (the reviewer's, read strictly)

Corrections name a **place**, never an id: the order the detector finds things in changes when anything upstream does.

```json
{"schema": "garage_game.inventory_curation", "version": 1,
 "drop": [{"at_m": [x, y, z], "radius_m": 0.5, "kind": "backpack"}],
 "relabel": [{"near": {"at_m": [x, y, z], "radius_m": 0.4}, "kind": "couch"}],
 "add": [{"kind": "jar", "at_m": [x, y, z], "size_hint_m": 0.15, "note": "..."}],
 "override": [{"near": {"at_m": [x, y, z], "radius_m": 0.6, "kind": "laptop"}, "size_m": [0.3, 0.02, 0.22],
               "centre_xz_m": [x, z], "yaw_deg": 0, "base_y": 0, "note": "how it was measured"}]}
```

- `drop` removes a detector proposal whose centre is near the point (and of that kind if named); `relabel` renames it.
- `add` follows an object the detector missed: the point projects into every photo that sees it and SAM masks it there.
  A seed replaces a detector proposal of the same kind at the same place.
- `override` replaces parts of the fitted box with the reviewer's measurement, and the entry says so in its notes. Use it
  where the photos plainly disagree with the fit (the pose model's depth shrinks small things and moves a box by a few
  centimetres), not to tidy numbers.
- To review: the evidence sheet of each object (`sessions/<id>/inventory/evidence/<id>.jpg`, the crops of the photos
  with the mask outlined: green accepted) and `inventory.overlay.object_overlay_sheet` (the box drawn onto photos, the
  front edge in magenta).

## `inventory.json`

One entry per object:

| Field | Meaning |
|---|---|
| `id`, `kind` | `couch_1`; the kind from `inventory/vocabulary.py` (plain English names, never text read from a photo) |
| `confidence` | 0 to 1 from photos that agreed, the detector's score, a plausible size, how much of the circle round the object has a photo, and the share of prompts whose mask was accepted. Not a probability |
| `box` | `centre_m`, `size_m`, `yaw_deg`, `footprint_m` (conventions above) |
| `placement` | `position_m` (bottom centre), `yaw_deg`, `rotation` (quaternion), `support`, `front_known` |
| `colours` | up to three broad colours, `{"hex": "#rrggbb", "share": 0.6}`, from the object's mask pixels |
| `evidence` | photos followed, masks accepted, points, detector views and score, other names the place was called, the evidence sheet |
| `notes` | what was done to the box: front from where it is taller, away from wall A, reviewer's measurement, only two photos |
| `recipe` | for a kind with a stand-in recipe: exactly `recipe`, `size_m`, `colours`, `params`, the input of `pipeline/recipes/build.py`; `null` otherwise |
| `recipe_slots` | which colour slots came from the scan and which are left to the recipe's default |
| `recipe_needed` | for a kind without a recipe: the kind, the box's size and the colours, as a brief for the next recipe |
| `pick` | on the five picks: the rank and why |

Sizes and places are good to about 15 per cent and 10 cm. `picks` lists the five objects chosen for stand-ins: the
founder's kinds (a cardboard box, a couch, a gaming laptop, an empty jam jar, a metal French press) where the room has them,
the floor winning over a shelf when they are close; for a kind the room lacks, the best-evidenced object of a kind not yet
picked from a different kind of thing, with the recipe it needs named.

## What C5 needs from this

1. **Build** each pick with `pipeline/recipes/build.py`, giving it the entry's `recipe` as the input file as it stands. A pick
   with `recipe_needed` waits for a recipe of that kind (the entry lists its size and colours).
2. **Place** it with `placement.position_m` and `placement.rotation` as the manifest instance's `transform`, and the
   support below. The recipes build at the bottom-centre pivot with the front toward -Z, which is the convention here;
   a recipe whose "front" is another side (a laptop's screen against its keyboard) is turned in the stand-in step, not here.
3. **Support.** A laptop on a desk and a jar on a table are above the floor. The manifest's `support.kind: "object"` needs
   the object in the manifest, so either the desk and table are built too (the test room's primitive box proxies show how
   cheaply) or the stand-ins are dropped to the floor. This is the integrator's and the founder's call.
4. **Collision, mass and affordances** are C5's: the recipes' receipts give boxes and cylinders to start from.

## Known limits

- The detector's phrases are a fixed list; what is not on it is not found. `inventory/vocabulary.py` is where a new kind
  goes, with a plausible size and the recipe that builds it.
- One rectangle per object: an L-shaped sofa is its main straight section, an open box is its closed outline.
- Colours are broad, from auto-exposed phone photos under mixed lamps: a white appliance in a dim corner reads grey.
- The segmenter's weights (SAM 2.1 small, Apache-2.0) and the detector's (OWLv2, Apache-2.0) are pinned in
  `coverage/backend.py`'s `MODEL_REGISTRY`; nothing is hosted or paid.
