# The hamlet's charm

**Run 2, L11, Lane L (15 October).** The generator places a hamlet on every island, and nothing made it look lived in. This packet hangs the life details of a lived-in place on it ([LOOK-BIBLE.md](LOOK-BIBLE.md), "Later: not built now", the bible's L5): smoke from the chimneys, warm windows and a lantern's glow when the lamps are on, bunting and washing that sway in the meadow's wind, and fairy lights at night. The plan is [RUN-2-LOOK.md](../runs/RUN-2-LOOK.md), L11.

- **The code:** `game/scripts/native/Look/Charm/`: `CharmLook` (the node, every number in `CharmLook.Tuning`), `Hamlet.cs` (the survey and the plan, pure), `CharmMeshes` (the flag and the garment, built in code as the fish are).
- **The shaders:** `game/shaders/charm_smoke.gdshader`, `charm_cloth.gdshader`, `charm_glow.gdshader`.
- **The checks:** `game/tests/native/Look/LookPresetTest.Charm.cs`.

## What it reads, and what it never touches

- **The room's data and the look's read-only state.** The room's objects (the hamlet), its spawns, its sea and its trees (the plant copies), the ground under the strings (rays down, once), and the look's `LampsOn`, its haze and the meadow's wind axis (`LandscapeLook.Roles["meadow"]`).
- **Scenery, as the fish are.** No entity, no save, no collider, no light, nothing in the kernel's sight or the journal's map. Placement is deterministic from the room's id and `Tuning.Seed` (20261006, the preset's reserved `charm.seed`): the same island always wears the same charm.
- **On by default in open land, never in a room** (a room's charm is the bible's indoor list, not this packet). `--look-charm=off` after `--` (or `GARAGE_GAME_LOOK_CHARM=off`) pins it away for a capture that compares.
- **Attached beside `NatureLook`** in `RoomWorld` once the room is built: `AddChild(CharmLook.Create(Room, Built, Look))`.

## Finding the hamlet

The exporter (`pipeline/landscape/export/exporter.py`) writes each kit thing with an asset whose **`category` is the generator's prototype token**: `cottage`, `tower`, `fence`, `lantern`, `crate`, `drying_line`, and `woodpile` on a woodpile's logs. Its **display name** is the token with spaces, capitalised and numbered ("Cottage 2", "Drying line 15", "Log"), and its **id never carries it** (`obj:scatter_0001`, `obj:loose_s_0003_0000`). So `HamletSurvey` reads the category first and the display name only when the category is not a token. Checked on the shared fixtures (key `210dbd77c5d53d0b`):

| | Cottages | Lantern | Drying line | Woodpiles | Fences | Towers | Crate |
|---|---|---|---|---|---|---|---|
| **1x garage** (28 objects) | 3 | 1 | 1 | 2 (10 logs) | 0 | 0 | 1 (the apple crate) |
| **10x garage** (67 objects) | 3 | 0 | 0 | 0 | 0 | 0 | 1 (the apple crate) |

- **The 10x hamlet is three cottages and a crate.** The generator's yard things (`hamlet_props`: the woodpiles, the drying line, the lantern, the fences) are absent from its manifest: they were not placed or were dropped as unreachable, and no fence stands on either island. The rest of the 10x objects are 63 stones.
- **Every cottage's door and windows face its local -Z.** On the 10x garage that is the sea: the three doors face south across a beach strip 0.2 to 0.5 m deep. The island cameras' derived `hamlet` view stands on the island's side and sees their backs; the captures add a view from the shallows (below).
- **The kit's shapes** (`pipeline/landscape/harness/kit.py`, fitted to the kit's size) are known as fractions of a thing's own size, so they follow every cottage's scale (0.74 of the kit on both garages): the chimney top at the back right, the two windows beside the door, the roof's eave corners (it spans the full width and depth), a tower's round eave, a lantern's cloth, a fence's three posts. They were checked against the 1x garage's cottage mesh (the chimney's stone to 0.1 mm).

## The kinds

| Kind | Hangs on | By day | When the lamps are on | Drawn as |
|---|---|---|---|---|
| **Chimney smoke** | every cottage's chimney | a thin stream | thicker | 16 soft puffs a chimney, written on the CPU each frame (the bubbles' pattern), lit by the scene |
| **Warm windows** | each cottage's two windows | not drawn | a warm pane with mullions over the cloth, a slow flicker | a quad on the wall, added light |
| **Lantern glow** | every lantern | not drawn | a soft halo from its cloth | a quad facing the eye, added light |
| **Bunting** | a string between two cottages | always | always | flags in the palette's accents, swaying |
| **Washing** | a line between two cottages | always | always | towels, shirts, socks and sheets in washed-out colours, swaying |
| **Fairy lights** | fence tops and the bunting's string | not drawn | warm bulbs, a few coloured, twinkling | quads facing the eye, never under 3 pixels, added light |

- **Strings.** The hamlet's buildings (cottages and towers) within 1.1 m of each other, nearest eave corner to nearest eave corner, as a minimum spanning forest: each building strung to its nearest neighbours, never a loop. A cottage no other building reaches strings to its nearest tree's trunk. The strings sag 8% of their span in the middle. The first string (in an order from the room and the seed) carries bunting, the next washing, and so on. On both garages: bunting between one pair, washing between another.
- **The sway.** Each flag's and garment's free edge leans along the meadow's wind axis on a gust of its own, a flutter runs across it, and the whole string swings in its middle as one. It is all in the vertex shader (no CPU), so it never stops and costs nothing on the CPU.
- **The night.** The windows, the lantern's glow and the fairy lights fade in over 2 s when `LookDirector.LampsOn` comes on and out when it goes off. By day they are not drawn at all. The smoke is always there, thicker once the lamps are on.
- **No light cast** (recommended): every glow of the charm's is added light that lights nothing. The light budget's slots are the sun's, the windows' and the wisps'.
- **But an exported lantern already lights its hamlet.** The exporter writes a `lamp` light hint at every lantern (up to 31), and the look makes each one a room lamp: a real omni light whose range is the island's diagonal, switched by `LampsOn`, and an emissive sphere 3.5 cm across at the hint, which is the lantern's foot. On the 1x garage that lamp lights the whole hamlet at night. The charm's halo sits on the lantern's cloth beside it. Whether a lantern should keep its real light is the founder's call (decision 2 below); the charm changes nothing in the room lamps.
- **The haze.** The added light mirrors the look's depth fog, as the Fractals' glint does: the engine's own fog over added light would add its colour. The smoke and the cloth take the engine's fog as the land does.

## Where it stops

- **It hangs from the hamlet's own things**, so nothing is placed on the ground.
- **A string is left out** (and the plan's notes say why) when:
  - it would pass within 0.35 m of a spawn;
  - it would hang over the sea (ground below the sea's level);
  - its washing would hang less than 12 cm over the ground under it (a graded path, a pond's bed or a meadow alike), so a body walks under it;
  - it would cross another object's box. Only the two things it hangs from may hold its ends.
- **No piece is inside another object's box**: a chimney's smoke starts on its own roof, a window's pane sits 1.5 mm off its own wall, and a fence's bulbs ride its own posts.
- **The generator's drying line is left alone.** Its washing is part of its own mesh (`life.py` hangs four garments on it), and no look can sway another object's mesh. The charm's washing hangs on its own line between two cottages instead. On the 10x garage, which has no drying line, that line is the hamlet's only laundry.

## Cost

Six MultiMeshes at most, one draw each, no shadows. The cloth is 7 triangles a flag and 26 a garment, string included (the fish have 200). Only the smoke is written on the CPU.

- **CPU, measured headless** (the debug build the suites use), 600 frames: **24 µs a frame** on the 10x garage (48 puffs) and on the synthetic hamlet, **nothing allocated**. The look suite holds it under 0.3 ms and to zero bytes.
- **Planning, once**, on the first physics frame, while the room is still loading: about 45 ms on the 10x garage, nearly all of it the first load of the charm's three shaders. The plan itself is a few milliseconds: the survey, the trees within reach (4 ms), and 34 rays down to the ground under the two strings (1.5 ms), each spanning only the hamlet's own heights. Where the land's collision is not built yet round the hamlet, its tile is built for the rays (none on the garage islands: the spawn's tiles already reach it).
- **GPU:** not resolvable. The before and after pairs (300 frames each, a quiet card) measured 4.07 to 3.66 ms (`hamlet`, 09:00), 6.66 to 6.14 (`hamlet_front`, 09:00), 4.24 to 4.04 and 6.85 to 6.37 (21:00): the after is no slower, within the runs' own spread (a repeat of the same after camera moved 0.4 ms).

## The captures

On the 10x garage (`-Room landscape10`, `-NoLightChecks`), each set at 09:00 (the landscape's own setup) and at 21:00 with the lamps on, `before` with the charm pinned off (`GARAGE_GAME_LOOK_CHARM=off`):
- the island cameras' derived `hamlet` (`tools/look/island_cameras.json`), which stands on the island's side and sees the cottages' backs;
- `hamlet_front`, a fixed scratch camera in the shallows south of the hamlet, which sees their doors and windows (the warm windows show only from there);
- `charm_close`, the two strings from 0.6 m (after only).

On the 1x garage, `after` at 21:00 only: its lantern beside the first door, which the 10x hamlet does not have.

**The motion clip.** The harness takes stills only, so the clip is the harness itself recorded by Godot's Movie Maker (`--write-movie`, 60 frames a second of game time whatever the recording's speed), from 0.9 m north of the 10x hamlet at 09:00: ten seconds of its measured frames, every third, as a GIF (640×360, 20 fps). The flags and the washing sway in it and the smoke drifts.

## What needs the founder's eye

1. **Which kinds first?** Built: smoke, warm windows, lantern glow, bunting, washing, fairy lights. Planters, a bench and a swing are not built.
2. **Do lanterns and fairy lights cast light?** Recommended and built: the charm's glows are emissive only. An exported lantern, though, already casts real light through the look's room lamps (its light hint, above): keep that lamp, or have the exporter stop writing lantern hints so a lantern only glows?
3. **Smoke always, or only with the lamps?** Recommended and built: always, thicker once the lamps are on.
4. **The real garage island** gets the same charm on its hamlet through the same code; nothing to install.

*Does the hamlet feel lived in? Is the smoke too much? Are the fairy lights welcome at night, or is the night too busy?*

These numbers are code constants for this round (`CharmLook.Tuning`); the preset's `charm` block carries them once the founder has judged.
