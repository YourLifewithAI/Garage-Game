# The 10 cm body and room physics (Run 1, P2)

> **Kernel record, 6 October 2026, revised after the founder's first playtest the same day, and again that evening when the companion shrank to 10 cm and the hover was fixed.** What the player's body is, why, and what was measured. Feel is not certified here: the founder's playtest decides it. Scale and units follow the [room-scale direction](../../ROOM-SCALE-DIRECTION.md): one world unit is one metre. What the founder said and what changed is under [Founder playtest 1](#founder-playtest-1-6-october-what-was-said-and-what-changed); the companion's new size is under [The companion's body](#the-companions-body-10-cm-since-the-evening-of-6-october) and the hover under [Why the avatars looked like they hovered](#why-the-avatars-looked-like-they-hovered).

## The body

`game/native/WorldScaleProfile.cs` defines the player as **0.10 m tall, 0.02 m radius, eye at 0.087 m, reach 0.15 m**. The companion has the same body since the founder's decision on the evening of 6 October ("the companion shrinks to match the player"), but keeps its own profile object (`WorldScaleProfile.Companion`) and its own speeds, because companion upgrades may change its abilities later.

`game/scripts/native/SmallPlayerController.cs` holds the body properties. Values are tunings for the playtest, chosen body-relative (body heights per second, fractions of body height). The third column is the first pass the founder played; bold values are current.

| Property | 0.30 m body (Run 0) | 0.10 m, first pass | 0.10 m body (now) | Why |
|---|---|---|---|---|
| Walk / run | 0.9 / 1.5 m/s | 0.32 / 0.60 m/s | **0.32 / 0.96 m/s** | Run is 3× the walk (was 1.9×): 9.6 body heights per second, a 4 m room in about 4.2 s. Walk kept: the founder did not flag it, and 3.2 body heights per second suits careful exploring and lining up jumps. Measured: 0.314 m walked and 0.891 m run in the first second from a standstill |
| Ground / air acceleration | 9 / 3 m/s² | 4.0 / 1.4 m/s² | **6.0 / 2.0 m/s²**; air × the world's `air_control` | Keeps the run crisp at the higher speed: measured 10 ticks (0.17 s) to full run speed and 10 ticks to a stop (7.7 cm of slide); walk speed in about 3 ticks. Air control keeps its one-third ratio to the ground |
| Step height | 0.045 m | 0.02 m | **0.02 m** (20 % of height) | The 6 mm rug is a step; the 4 cm book is a jump |
| Jump | 1.55 m/s take-off | 6.5 cm apex | **6.5 cm apex** in the default gravity and heavier; **higher in lighter gravity**, up to 30 cm | Clears the book with 2.5 cm to spare under every preset. At and above the default gravity the take-off speed is solved from gravity and the 60 Hz tick for a 6.5 cm apex; below it the take-off stays the default's 0.67 m/s, so the leap grows as gravity falls (founder, 8 October), capped at three body heights |
| Jump buffer / coyote time | none | 0.10 s / 0.08 s | **0.10 s / 0.08 s** | A jump pressed just before landing or just after leaving an edge still happens |
| Floor snap / safe margin | 0.025 / 0.001 m | 0.015 / 0.001 m | **0.015 / 0.001 m** | Snap follows the rug edge and slopes but never pulls the body down off the book |
| Terminal fall | 8 m/s | 6 m/s | **6 m/s**, or the world's lower limit | Room-scale falls; floaty air lowers it to 0.6 m/s |
| Turn to face motion | none | none | **10 rad/s**, F3 only | In the diorama view the body turns to face where it walks (a half turn in about 0.3 s) |
| Eye camera near / far | 0.01 / 2500 m | 0.005 / 100 m | **0.005 / 100 m** | No wall clipping at a 2 cm radius; room-scale depth precision for the Compatibility renderer |
| Creation push cap | 8 m/s | 1.2 m/s | **1.2 m/s** | Inventions push the body at body scale |

The body also carries the creation-effect API ported from the retired 1.7 m GDScript fixture (`SetCreationEffects`, `ClearCreationMotion`, a position guard that keeps creation-driven motion and recovery out of locked zones). The invention runtime drives the real player and companion through it.

The visual body is an original blockout: a pill from the feet to three quarters of the height, a head and two eyes. Since the hover fix the pill reaches down to the collider's lowest point, and the visual is **seated** on whatever the capsule stands on (below).

## The companion's body: 10 cm since the evening of 6 October

`game/scripts/native/CompanionAvatar.cs`. Everything that was sized for the 0.24 m body is scaled to the 10 cm one (about ×0.42 for distances). The look is kept: the pill, the hat (a cone as wide as the body, a third of its height), the eyes, the gold pointing cue and the name label, all sized from the body.

| Property | 0.24 m companion (before) | 10 cm companion (now) | Why |
|---|---|---|---|
| Height / radius / eye / reach | 0.24 / 0.055 / 0.205 / 0.40 m | **0.10 / 0.02 / 0.087 / 0.15 m** | The player's body; its own profile object |
| Walk / run | 0.80 / 1.65 m/s | **0.32 / 1.20 m/s** | The run is 1.25× the player's 0.96 m/s run, so follow still closes a gap while the player runs. Follow and come always use the run speed as their cap; the walk is only for stepping aside |
| Ground / air acceleration | 9.0 / 3.0 m/s² | **7.5 / 2.5 m/s²** | The player's 0.16 s ramp to full run |
| Step / floor snap / jump | 0.04 / 0.025 / 0.065 m | **0.02 / 0.015 / 0.065 m** | The player's: the rug is a step, the book a jump |
| Follow band (near / far) | 0.30 / 0.65 m | **0.12 / 0.30 m** | At the near edge, 8 cm (two body-widths) of floor between the two figures |
| Follow place (side / ahead) | 0.40 / 0.08 m | **0.16 / 0.04 m** | Beside the player and in view of the over-the-shoulder camera |
| Come stops at | 0.32 m | **0.14 m** | |
| Steps aside when the player is within | 0.20 m | **0.08 m** | Below the come distance, so come never backs away |
| Side change, detour, corner, stuck distances | 6, 5, 5, 3 cm | **3, 3, 2.5, 1.5 cm** | Body-relative |
| Slowest approach | 0.15 m/s | **0.08 m/s** | The last centimetres never creep |
| Local steering probe | 0.14 m | **0.06 m** | Three body radii |
| Navigation agent (radius / height / climb) | 8 / 24 / 3 cm | **4 / 10 / 1 cm** | 2 cm body plus 2 cm clearance, exactly two cells |
| Label height / text | 0.33 m / 36 mm | **0.135 m / 18 mm** | |

The companion's tests were rescaled with it; the low passage it must cross without step headroom is now 11 cm (it was 26 cm for the 0.24 m body).

## Gravity: a world property, three presets

`game/scripts/world_physics_profile.gd` owns gravity, the air and wind as bounded, revisioned world rules. Gravity changes how long a jump or fall lasts; since 8 October, gravity lighter than the default also lets the body leap higher, as on the moon (the founder: "I do want to be able to jump higher in the low gravity mode … being able to leap up a hillside would be very satisfying"). Since the first playtest a profile also carries the air: `terminal_fall_mps`, the fastest anything falls (the body's own 6 m/s still caps it), and `air_control`, a multiplier on the body's air acceleration. Bounds: gravity 0.5 to 30 m/s² (was 1 to 30, lowered so floaty fits), fall limit 0.3 to 12 m/s, air control 0.25 to 3, wind at most 1 m/s. The companion now lives under the same profile as the player: when the player's revision is newer, the companion adopts it (before, pressing G changed the player's gravity only).

| Preset | Gravity | Fall limit | Air control | Jump: apex, airtime | Fall from the 0.75 m table |
|---|---:|---:|---:|---:|---:|
| `room_tuned` (default) | 3.5 m/s² | 6 m/s | ×1 | 6.5 cm, 0.38 s (measured 23 ticks) | 0.65 s |
| `room_real` | 9.8 m/s² | 6 m/s | ×1 | 6.5 cm, 0.22 s (measured 13 ticks) | 0.39 s |
| `room_floaty`, first pass | 1.6 m/s² | 6 m/s | ×1 | 6.5 cm, 0.55 s | 0.97 s |
| `room_floaty`, second pass | 0.6 m/s² | 0.6 m/s | ×2 | 6.5 cm, 0.93 s (measured 56 ticks) | 1.75 s (1 s to reach 0.6 m/s, then a steady drift) |
| **`room_floaty`, now** (8 October) | **0.6 m/s²** | **0.6 m/s** | **×2** | **30 cm (the cap; 38 cm uncapped), 2.00 s (measured 120 ticks)** | **1.75 s** |
| half the default (a custom profile) | 1.75 m/s² | 6 m/s | ×1 | 12.5 cm, 0.75 s (measured 45 ticks) | 0.93 s |

Real gravity at 10 cm reads as a hopping insect: a jump is over in a quarter of a second. The tuned preset gives a game-like 0.37 s jump while keeping falls brisk. **Recommendation: `room_tuned`**, confirmed or replaced by the founder in the playtest (key **G** cycles the presets live, through the command host, and prints the active one to the console).

**Floaty, after the first playtest.** The founder found the gravity shifts fun and asked for floaty to be more pronounced. It now hangs for almost a second on the same 6.5 cm jump, a fall drifts down at no more than 0.6 m/s (measured: a 40 cm drop caps at 0.600 m/s and takes 70 ticks), and air steering is doubled, so the body can be guided while it floats. A jump's take-off is 0.27 m/s, below the fall limit, so the limit slows long falls without changing the jump. Tuned and real are unchanged.

**Floaty leaps, 8 October.** The founder wanted to jump higher in low gravity and leap up hillsides. The rule changed: in gravity at or above the default the take-off is still solved for 6.5 cm; below it the legs push off at the default's 0.67 m/s, so half the gravity leaps about twice as high, as on the moon. Floaty's would be 38 cm and 2.3 s, so the body caps a leap at three body heights (30 cm, 2.0 s). Its take-off (0.60 m/s) now matches the 0.6 m/s fall limit, so the descent mirrors the rise. Air control stays doubled: 4 m/s² stops a full run in 0.24 s of a 2 s leap, enough to steer the landing without turning the leap into flight.

**The jump-height rule is kept: gravity never changes how high the body jumps.** Jump height is the body's reach into the room. If floaty also raised the jump, the 30 cm box and other ledges would become reachable in one preset only, and gravity would turn into a level-design key rather than a feel. Floaty changes how the jump feels (hang time, drift, steering) while every obstacle stays the same size. Whether low gravity should ever let the body climb higher is a design choice for the founder, listed under open questions below.

## Cameras

`game/scripts/native/RoomHud.cs` owns the views. **Two views since 13 October** (the founder: the game commits to first person and third person): F1 and F2, where F2 now zooms (below). F3 (diorama) and F4 (isometric) are shelved: their keys, and `[` and `]`, are unbound and free, and the rows below describe code that only the look harness reaches (`hud_view` 2 and 3 in `tools/look`).

**F2 zooms and orbits (13 October).** The wheel moves the over-the-shoulder camera from today's arm (3.2 body heights, 32 cm for the 10 cm body: exactly the view it always was) out to **8 m** (80 body heights, where the body is about 1 % of the frame's height), a click being 15 % in or 18 % out, about 20 clicks in all. Walls, furniture, the ceiling and the ground hold the lens in as they always held the arm. Up to **0.8 m** (eight body heights, six clicks) it is still the shoulder camera: a drag turns the body, W A S D are the body's, and the arm's pivot slides from the shoulder to the player's middle. Past 0.8 m the camera is the orbit rig's (the F3 rig, with F2's 68° lens): a left drag orbits it round the player (0.0025 rad a pixel, tilting between 2° and 63° down), W A S D move relative to the camera's heading and the body turns to face its motion, and the focus stays the player's body. **Under water, orbiting, W swims level across the camera's heading** (Space rises, Ctrl sinks); in F1, and in F2 up to 0.8 m, W swims the way you look. Zooming back in past 0.8 m hands the camera's heading and pitch to the body, so the view does not swing.

| Key | View | Framing | Mouse and wheel |
|---|---|---|---|
| F1 | Eye | 8.7 cm up, 72° lens | Mouse turns the body and looks up and down |
| F2 | Over the shoulder | 8 cm up, 32 cm behind on a spring arm, 68° lens | As F1; the arm pitches with the look |
| F3 | **Diorama** (was a frozen reference view) | A high-angle orbit centred on the player's middle: 50° down at 0.9 m by default, 38° lens | A left drag orbits round the player and tilts between **20° and 80°** down; the wheel zooms between **0.30 and 2.4 m** (about 12 % a notch); W A S D move relative to the view and the body turns to face its motion |

The diorama rig is not attached to the body, so it orbits without turning it, and its pivot eases after the body (a jump or a step does not jolt the view). It is a `SpringArm3D` with a 2 cm sphere that collides with world geometry only (layer 1), so walls, furniture and the ceiling hold the lens on the player's side instead of letting it pass through; the test checks it under a 12 cm deck. The narrow lens and the look's depth of field are what make it read as a miniature: `LookDirector` focuses whichever camera is current on the player (`FocusTarget`), and its tilt-shift narrows the in-focus band and strengthens the blur as the camera looks down, so the high angle gets the strongest miniature effect. The orbit's yaw, pitch and distance persist across F3 visits within a session; entering F3 starts behind the player.

## The companion's follow

`game/scripts/native/CompanionAvatar.cs`. The founder found that on follow the companion always moved directly behind them: follow aimed at a point rigidly attached 0.55 m behind and 0.22 m beside the player, so every turn swung it round. Follow is now loose:

Distances below are for the 10 cm companion (the 0.24 m body's are in the table above).

- **A comfortable band**, measured centre to centre on the floor: closer than **0.12 m** and it eases out, farther than **0.30 m** and it closes in. Both bodies are 10 cm with a 2 cm radius, so 0.12 m leaves about two body-widths between them.
- **Its place is beside the player's line of travel**, 0.16 m to the side and 4 cm ahead, on whichever side it is already on (it changes side only when it is clearly on the other one, 3 cm off the line). Beside and slightly ahead keeps it out of the over-the-shoulder camera's line and in view.
- **No re-targeting on turns.** The line of travel comes from the player's velocity and changes only while the player moves, so turning on the spot never moves it (checked: under 1 cm while the player turns 270°). Walking back the way you came, it keeps its side instead of crossing behind you.
- **Smooth, without oscillating.** While it follows it moves at the player's velocity plus 3 m/s per metre of distance to its place, capped at its 1.20 m/s run. It rests as soon as the player stops and it is comfortably inside the band, so it never hunts back and forth (measured: no velocity reversals while settling, resting 0.17 m from the player). Motion is exact through body-relative input while the body turns smoothly (8 rad/s) to face it; at rest it turns to face the player.
- **Catching up.** Far from its place it runs: starting 0.83 m behind with the player running at 0.96 m/s for over six seconds, the gap never grew past 0.83 m and it finished 0.17 m away, beside the player. While the player walks it stays beside them (measured: 0.16 m to the side and 4.5 cm ahead, never behind).

Come approaches to 0.14 m along the line from the player at up to its run speed, then stays (measured: 0.140 m). Stop, stay, look and point are unchanged.

## Navigation: the companion finds its way round furniture

The founder walked behind the big box, summoned the companion, and it stuck on the far side: it only steered locally (five headings round the straight line), and pressed flat against the box every heading collides. `game/scripts/native/Navigation/RoomNavigation.cs` gives it a real map, wired into the room by one line in `RoomWorld.cs` (`Navigation.RoomNavigation.Attach(this)`).

- **Baked at runtime from the room's static collision.** At room load a Recast navigation mesh is baked from the `StaticBody3D` colliders on the world layer under the built room, inside the room bounds. Cells are 2 cm across and 1 cm high. The agent is the companion: radius **4 cm** (its 2 cm body plus 2 cm clearance, exactly two cells; it was 8 cm for the 0.24 m body), height **10 cm** (was 24 cm), climb **1 cm** (three quarters of the 2 cm step, rounded down to whole layers; was 3 cm), slope 45°, with low ceilings and ledges filtered out. The climb sits below the body's 2 cm step, so a route never asks for a climb the body might fail: the 6 mm rug is walkable, and the 4 cm book and doorstop are walked round. The test room bakes to **64 polygons in about 24 ms** (69 in 22 ms with the old agent). The cells stay 2 cm: 1 cm cells would allow a 3 cm agent radius but cost about four times the bake.
- **Its own navigation map** (2 cm cells, synchronous updates at the end of each physics frame), so it never conflicts with the default map's cell size.
- **Re-baked when the room changes.** Every 0.25 s the source collision is fingerprinted (which static bodies and shapes, where, and each shape's dimensions to the millimetre; mesh-sized shapes by their `changed` signal). Before the Lane P review only shape instance ids were hashed, so a shape resized in place was missed; measured now: an in-place resize re-bakes about 30 ticks later. A change re-bakes once it has held still for 0.2 s, so a carried object is baked where it is put down, not on every frame. Measured: a box added to the test floor was in the mesh 17 ticks later. `MarkDirty()` forces a bake.
- **Used by follow and come.** Routes are re-planned every 0.1 s, or sooner when the goal moves 3 cm or the mesh is re-baked. Where the walkable line to the goal is straight, the companion keeps its local behaviour: velocity matching, the final approach, and local steering round the player's body. Where the route is a real detour (more than 3 cm longer than the straight line), it steers corner to corner. For follow, a detour to its place counts as outside the band, so a companion resting behind the box comes round. If a wall or furniture covers its side (its place is more than an agent radius off the walkable mesh), its place moves to the other side.
- **Blocked is reported honestly.** When the goal is not on the companion's walkable island, the route ends at the nearest point it can reach. A route reaches its goal only when it ends at the goal's own nearest walkable point (within 5 cm), not merely near the goal: across a thin wall the player can be inside the 14 cm come distance yet unreachable, which used to count as arrived. Measured now: with the player inside a pen of 1 cm walls and the companion 7 cm away outside, come reports blocked. The companion walks there and reports blocked, and the HUD shows "path blocked". It never teleports or crosses a wall. Trying to move for a second without covering 1.5 cm also reports blocked, and the route is planned again.

Measured headless (Godot 4.7.2, Jolt, 60 Hz). The first column is the 0.24 m companion with local steering only, as the founder played it; the others are with navigation, first for the 0.24 m body and now for the 10 cm one.

| Scenario | Local steering only (0.24 m) | Navigation, 0.24 m | Navigation, 10 cm (now) |
|---|---|---|---|
| Test room: player behind the big box, companion on the far side, come sent through the command host (key 3) | Stuck on the far side, 0.70 m away | Round the box, arrived in 60 ticks | Round the box, arrived in **87 ticks** (1.45 s, at the slower 1.20 m/s run), stopping 0.14 m away, no blocked ticks. Without navigation it still sticks, 0.65 m away |
| Test floor: the same box size, 1.2 m apart | Stuck, 0.93 m away, reported blocked | Arrived in 69 ticks; route 1.36 m | Arrived in **99 ticks**; the route is **1.30 m** against 1.2 m straight. Without navigation: stuck 0.87 m away, reported blocked |
| Follow starting behind the box | (not tested) | Rests 0.40 to 0.44 m from the player | Comes round and rests **0.20 m** (test floor) and **0.23 m** (test room) from the player, with nothing between them |
| Come from inside a closed pen | Reported blocked | Reported blocked, never crossed a wall | The same |
| A box added to the test floor | | In the mesh 17 ticks later | The same (17 ticks; bake about 100 ms on the 10 × 12 m floor) |

What the navigation does not do yet:

- **The bake runs on the main thread**: about 22 ms for the 4 × 3 m test room and 95 ms for the 10 × 12 m test floor. A large or detailed captured room may need the background bake (`BakeFromSourceGeometryDataAsync`) to avoid a hitch when furniture moves.
- **Only the room's built geometry is in the mesh.** Creations from the invention runtime and the two avatars are not; local steering handles them.
- **No jumps or drops in routes** (no navigation links): the companion never plans up onto the book or off a ledge. Its routes climb at most 1 cm, although its body steps 2 cm and jumps 6.5 cm.
- **Clearance is the companion's 10 cm.** In a captured room, anything with less than 10 cm under it is walked round, not under.
- **The player has no navigation**; only the companion plans routes.

## Why the avatars looked like they hovered

The look captures (`docs/look/reviews/run1/step3/over_shoulder.png`) showed both avatars floating slightly above the rug, with a strip of lit floor between each body and its shadow. The two candidates were a body-to-mesh offset (Play) and shadow bias too large for 10 cm objects (Look). Measured headless in the test room (Godot 4.7.2, Jolt, 60 Hz) at `0ea09ff`, with a ray cast for the surface under the body:

| Body, where | Collider's lowest point above the surface | Visual's lowest point above the collider's | Visual above the surface |
|---|---:|---:|---:|
| Player at its spawn (dropped 8 mm onto the rug) | 1.03 mm | 3.00 mm | **4.03 mm** |
| Player on the floor, the 6 mm rug, the 4 cm book top (teleported) | 0.66 mm | 3.00 mm | **3.66 mm** |
| Companion (0.24 m) at its spawn | 1.03 mm | 7.20 mm | **8.23 mm** |
| Companion (0.24 m) on the floor, the rug, the book top | 1.44 mm | 7.20 mm | **8.64 mm** |

The rug's and the book's visuals match their colliders exactly (tops at 6.00 and 40.00 mm), and the collider's lowest point is the body's feet. Floor snap (15 mm, 25 mm on the old companion) pulls bodies down and leaves no gap. Two real gaps remained:

1. **The visual pill started 3 % of the body height above the feet** (`CapsuleMesh` centred at 0.39 of the height, 0.72 tall): 3 mm on the 10 cm player, 7.2 mm on the 0.24 m companion.
2. **Jolt rests a `CharacterBody3D`'s capsule 0.5 to 1.5 mm above its support.** Its motion cast finds contact only to a fraction of the motion: casting the resting capsule down 3 mm reports contact between 0.94 and 1.13 mm, casting 20 mm between 0.31 and 1.25 mm, and `test_move` travels 0.78 mm, where a ray measures 1.03 mm. Where the body comes to rest depends on the last motion, so the gap varies (0.50 to 1.44 mm measured).

At the review hour (16:30 on 6 October) the key light is 24.9° above the horizon, so a gap g under a body moves the start of its shadow 2.15 g along the floor: about 9 mm for the player and 18 mm for the old companion. That is the strip of floor in the capture.

**The fix (`SmallPlayerController.cs`).** The pill now reaches down to the feet (centred at 0.375 of the height, 0.75 tall; its top is unchanged). Each physics tick, a ray along the floor normal from the capsule's nearest point to its support measures the resting gap, and the visual body is lowered by it: only on the floor, never by more than 2.5 mm, and never when the ray finds nothing that close (a ledge, a step). The physics body stays where Jolt put it, so steps, snapping and the jitter measurements are unchanged. `TestGroundContact` in the small-avatar test stands both bodies on the floor, the rug and the book top, after a teleport and after an 8 mm drop, and pins the visual's lowest point within 1 mm of the surface and no more than 0.2 mm into it. Measured: **0.00 mm** in all twelve cases, with the capsule itself 0.50 to 0.66 mm up. In the room, the same measurement at the spawn and on the floor, rug and book top gives 0.00 mm for both bodies.

**Shadows were measured too; bias is not the cause.** The key light has `shadow_bias` 0.03, `shadow_normal_bias` 1.0, blur 1.55, an angular size of 2.425° (soft shadows), two splits with the first at 10 %, and a 8.87 m shadow distance (1.6 × the 5.55 m room diagonal), on a 4096 px 16-bit atlas at soft-shadow quality 3. The ceiling lamp uses the same biases with a 0.19 m light size. A GPU check with the fix in place (the over-the-shoulder and companion review cameras at 1920 × 1080, settings changed at runtime in a scratch script, no look code edited):

| Key shadow settings | Contact darkness beside the player's base (1 = lit rug) | Plain rug luminance spread (acne) |
|---|---:|---:|
| As set (bias 0.03, normal bias 1.0) | 0.895 | 0.0152 |
| Both biases 0 | 0.891 | 0.0234 (acne) |
| Normal bias 0.3 | 0.901 | 0.0154 |
| Shadow distance 5.5 m | 0.861 | 0.0196 (faint bands) |
| Shadow distance 5.5 m and four splits | 0.834 | 0.0193 (faint bands) |
| Sharp key (0.5°) with bias 0.01, normal bias 0.3 | 0.592: the shadow starts at the base | 0.0373 (heavy acne bands) |

Removing both biases leaves the contact as it was and only adds acne, so today's biases do their job without detaching the shadow. The contact reads soft because the key is deliberately soft (a 2.4° sun). The sharp-key frame shows the shadow now starts exactly at the base, so the geometry touches. No shadow change is requested from Look. If the founder still wants a crisper footing, the levers are the key's softness or a small contact cue (for example ambient occlusion with a radius near the figures' size, where today's is 0.16 m), not the bias. A tighter shadow distance helps a little but brings faint acne unless the biases are retuned with it.

## The jitter spike

`game/tests/native/SmallAvatarPhysicsTest.cs` measures the same scenarios on a probe body twice: at true scale, and with the whole world, body, speeds and gravity multiplied by ten (the import-scale alternative from the direction document, 1 unit = 10 cm). Metrics are divided by the scale, so both columns are in body-scale millimetres. Run it with `-- --jitter-spike`; the default run keeps the assertions.

- **Floor ratio** and **transitions**: share of ticks on the floor, and how often that flips.
- **Roughness**: the second difference of height between ticks (largest and RMS). It is zero for smooth motion on a plane, so it catches bobbing that height spread hides.
- **Drift**: horizontal motion while standing or pushing into a wall.

Rerun after the Lane P review at `93d07bc` on the founder's machine (Godot 4.7.2, 60 Hz, headless), once per engine: the project as committed (Jolt) and the same worktree switched to Godot Physics for the run and switched back. The table that stood here was measured before the playtest retune and is replaced. Each cell is the RMS of the tick-to-tick second difference of body height in body-scale millimetres, with the largest single value in brackets. Every walking scenario stayed on the floor on every tick (floor ratio 1.000, no transitions) on both engines and both scales.

| Scenario | Godot Physics ×1 | Godot Physics ×10 | Jolt ×1 | Jolt ×10 |
|---|---|---|---|---|
| Standing: floor, 6 mm rug, 4 cm book top, half over the book edge, 10/20/30/40° slopes | 0, no drift | 0 | 0 | 0 |
| Pushing into the book's side for 2 s | 0, no drift | 0 | 0 | 0 |
| Walking over the rug (on and off) | 0.27 (3.6) | 0.24 (3.3) | 0.23 (3.2) | 0.25 (3.8) |
| Floor onto a 10° ramp (crease) | 0.17 (1.6) | 0.18 (1.7) | 0.09 (0.9) | 0.09 (0.9) |
| Up 10° | 0.05 (0.19) | 0.02 (0.18) | 0.03 (0.25) | 0.03 (0.19) |
| Down 10° | 0.04 (0.30) | 0.04 (0.29) | 0.04 (0.30) | 0.04 (0.29) |
| Floor onto 20° (crease) | 0.44 (2.2) | 0.29 (2.2) | 0.20 (2.0) | 0.20 (2.0) |
| Up 20° | **0.73** (2.0) | **0.65** (1.9) | 0.13 (1.0) | 0.25 (1.7) |
| Down 20° | 0.08 (0.57) | 0.35 (2.0) | 0.08 (0.58) | 0.08 (0.58) |
| Floor onto 30° (crease) | 0.49 (3.8) | 0.41 (4.0) | 0.31 (3.1) | 0.30 (3.0) |
| Up 30° | 0.07 (0.48) | 0.07 (0.50) | **0.33** (2.5) | **0.32** (2.5) |
| Down 30° | 0.12 (0.84) | 0.32 (1.7) | 0.12 (0.84) | 0.12 (0.80) |
| Floor onto 40° (crease) | 0.59 (**5.6**) | 0.34 (3.0) | 0.39 (3.8) | 0.36 (3.5) |
| Up 40° | **0.64** (4.1) | 0.06 (0.38) | 0.09 (0.65) | 0.05 (0.36) |
| Down 40° | 0.16 (1.09) | 0.16 (1.09) | 0.16 (1.09) | 0.16 (1.14) |

The Jolt column reproduces the independent reviewer's run on the same head to the last digit: the simulation is deterministic.

**Decisions, confirmed.**

1. **Jolt Physics stays.** It is not better everywhere: Godot Physics walks up the 30° ramp more smoothly (0.07 against 0.33 mm). But Jolt's worst walking case is 0.39 mm RMS (onto the 40° ramp) and its worst single tick 3.8 mm, where Godot Physics reaches 0.73 mm RMS walking up 20°, 0.64 mm up 40° and a 5.6 mm single tick. The old table's figures moved with the retune (Godot Physics up 30° was 0.57 mm and is now 0.07; Jolt up 20° was 0.21 and is now 0.13); the comparison still favours Jolt.
2. **No ×10 import scale.** At ×10 neither engine is consistently smoother: Godot Physics is better on six rows and worse on five, and Jolt's ×10 column is within 0.02 mm of its ×1 column except up 20°, where ×10 is worse (0.25 against 0.13). Nothing in the numbers is a precision problem that scale would fix.
3. The floor-to-ramp crease gives a few ticks of 1 to 6 mm bump on both engines and both scales: controller behaviour at a concave edge, not precision. A camera height smoother remains the fix if the playtest notices it.

**What this spike does not establish** (the reviewer's methodology notes):

- **One deterministic run.** Rerunning gives the same numbers, so repetition adds nothing; real variation would come from start positions, speeds, frame pacing and input timing, which the spike does not sample.
- **Body height, not the eye camera.** The eye camera is a rigid child of the body, so its height roughness is the body's exactly; but camera pitch, the over-the-shoulder spring arm and the F3/F4 rigs (which ease after the body) are not measured.
- **No measured noticeability threshold.** A rough screen estimate only: at the 72° eye lens on a 1080-pixel-tall view, a vertical eye shift of d moves features 0.3 m away by about d × 2,500 pixels per metre. So 0.33 mm RMS is about 0.8 pixel per tick (at the edge of visible), 0.73 mm about 1.8 pixels, and a one-tick crease bump of 3 to 6 mm about 7 to 14 pixels, likely visible on both engines. Only the founder's playtest certifies feel.
- **The ×10 descents cover more distance.** The scaled probe accelerates differently going down, so on Jolt the ×10 descents travelled further (234 against 208 mm body-scale on 40°, 257 against 235 mm on 30°); those rows compare slightly different walks.

Not measured here: physics at 120 Hz ticks. Physics interpolation was measured in the second playtest's fix round (see [Founder playtest 2](#founder-playtest-2-6-october-second-pass-what-was-said-and-what-changed)).

## G goes through the single command path

Since 7 October, key **G** sends `world.set_physics` (contract `7e2c779`) through the command host as the player, the same path as every other world change: `SmallPlayerController.RequestNextWorldPhysics` asks the host (`WorldPhysicsRequest`, which the host sets when it attaches) for the next preset in `PRESET_IDS`. The op is player-only, so a companion gets `permission_denied` and is never held; its receipt is transient and the room revision does not move, because gravity is not yet saved in room state. Both bodies adopt the preset at once, and the console still prints the `PHYSICS_PROFILE` line. Without a command host (the body suites), G does nothing; those suites call the internal `CycleWorldPhysics` seam directly. The playtest-only exception recorded here before is retired; see [command-host.md](command-host.md). Saving the world's physics in room state (a creation, a magic, a saved room rule) is still open, and then the receipt becomes durable.

## Founder playtest (ten minutes)

Run `pwsh -NoProfile -File run-room.ps1`. The cursor is free (since the Glow playtest, 9 October): hold the left button and drag to look around.

| Key | Does |
|---|---|
| W A S D, mouse | Walk and look |
| Shift | Run (3× the walk) |
| Space | Jump (6.5 cm) |
| R | Recover to the last safe footing |
| **G** | Cycle gravity: tuned (3.5) → real (9.8) → floaty (0.6, slow falls, more air steering) → tuned; the console prints which |
| F1 / F2 | Eye camera / over-the-shoulder (13 October: the wheel zooms F2 out to 8 m; past 0.8 m a left drag orbits and W A S D move relative to the view). F3, the diorama, is shelved |
| 1 2 3 4 5 | Companion: follow, stay, come, stop, point ahead |
| F4 | (Shelved, 13 October.) The isometric view; **Q** and **E** turned it a quarter turn (the invention workshop that also bound them is gone), then `[` and `]` |
| L | Lamps on or off |
| **T** | Step the time of day: dawn, morning, noon, late afternoon, sunset, dusk, night, then back to the real clock |
| **Shift+T** | Step the season: March equinox, June solstice, September equinox, December solstice, then back to the real calendar |

Try, in order: walk and run around the rug and across its 6 mm edge; walk into the 4 cm book, then jump onto it with a run-up and walk off its far edge; stand right at the book's edge; jump onto the doorstop (4 cm); walk under the table and look up; climb onto nothing taller than the book (the box at 30 cm is a wall for a 10 cm body); then press G twice and repeat the book jump in each gravity.

Check against: does crossing the room feel like exploring a big space or like trudging (speed); does the jump feel deliberate or twitchy (gravity); is the book a fair obstacle (jump height); does anything bob, shake or catch at the rug edge or the book edge (jitter); does the eye camera clip into the book or the wall when you press against them.

Record the verdict per question; numbers to change live in `SmallPlayerController.cs` (speeds, jump, step) and `world_physics_profile.gd` (gravity presets).

## Founder playtest 1 (6 October): what was said and what changed

| The founder said | What changed | Measured |
|---|---|---|
| "Running doesn't feel like a significant increase in pace." | Run 0.60 → **0.96 m/s** (1.9× → 3× the walk); ground acceleration 4.0 → **6.0 m/s²** and air 1.4 → **2.0 m/s²** so the faster run still arrives crisply. Walk kept at 0.32 m/s. | 0.891 m in the first second of a run; 10 ticks to full speed, 10 to stop |
| "The gravity shifts are fun. I'd make the floaty version even more pronounced." | Floaty 1.6 → **0.6 m/s²**, with a **0.6 m/s** fall limit and **2×** air control in that preset only; the world profile gained `terminal_fall_mps` and `air_control`; the gravity floor went from 1.0 to 0.5 m/s². Tuned and real unchanged. Jump height still fixed at 6.5 cm in every preset. The companion now shares the player's world profile. | Floaty jump 56 ticks (0.93 s, was 0.55 s); a 40 cm floaty fall caps at 0.600 m/s |
| "The camera freezes with the F3 view. I'd still want to be able to rotate my view, as in F1 and F2." | F3 is now the **diorama** camera: a high-angle orbit centred on the player, mouse to orbit (20° to 80° down), wheel to zoom (0.30 to 2.4 m), held out of walls and the ceiling by a spring arm, depth of field focused on the player by the look. Movement in F3 follows the view. HUD help updated. F1 and F2 unchanged. | Headless checks: orbit, pitch and zoom limits, centring, camera-relative walking, held under a 12 cm deck |
| "When I ask my companion to follow, it always moves directly behind me." | Loose follow: a 0.30 to 0.65 m band (for the 0.24 m body; 0.12 to 0.30 m since it shrank), a place beside the player's line of travel on the side the companion is already on, no re-targeting on turns, velocity matching without oscillation, running to catch up. | 0 mm moved while the player turned; 0 ticks behind while walking; no crossing when the player turns back; 0 reversals settling |
| "If I walk behind the big box and summon my comp, it gets stuck on the other side of the box. It can't work its way around." | Navigation: a mesh baked at runtime from the room's static collision with an 8 cm agent radius (4 cm since the companion shrank), re-baked when the room's static geometry changes, used by follow and come, with local steering kept for the final approach; unreachable goals report blocked. | In the test room, come round the big box arrives in 60 ticks (local steering alone stays stuck 0.70 m away) |
| (Building answers, evening) "The companion shrinks to match the player (10 cm)." | The companion is a 10 cm body like the player's, in its own profile object; speeds, follow band, come distance, navigation agent (4 cm radius, 10 cm high, 1 cm climb) and its hat, label and pointing cue are scaled with it. See [The companion's body](#the-companions-body-10-cm-since-the-evening-of-6-october). | Follow rests 0.17 m away, beside the player; come stops at 0.140 m; catches a running player from 0.83 m behind and ends 0.17 m away; round the big box in 87 ticks |
| (Look captures) Both avatars appear to hover over the rug. | The pill reaches down to the feet and the visual is seated on the support each tick. See [Why the avatars looked like they hovered](#why-the-avatars-looked-like-they-hovered). | Visual 4.0 mm (player) and 8.2 to 8.6 mm (old companion) above the surface before; 0.00 mm after, on the floor, the rug and the book |

## Founder playtest 2 (6 October, second pass): what was said and what changed

| The founder said | Cause | What changed | Measured |
|---|---|---|---|
| In F4 **Q** and **E** do not turn the view ("No worn design to revise. B opens a new draft."). | `invention_runtime.gd` also bound Q (revise the worn design) and E (use it), with B, F, V and K, and drew the INVENTIONS panel. | The workshop is retired in the playable room: the runtime has a `workshop_enabled` switch that the command host turns off, so no panel, no editor and no key handler exist there. The runtime itself stays (it renders creations and runs effects for the host), and the kernel suites keep the workshop on: its validation, budgets, receipts and undo may carry the Run 2 building kit. HUD help lines updated. | 6 new command host checks (201); `PlayHudTest` checks Q and E turn the F4 view a quarter turn each way. |
| The clock follows real time, so a 3 a.m. playtest only shows night. | - | **T** steps dawn, morning, noon, late afternoon, sunset, dusk and night, then returns to the real clock; **Shift+T** steps the four solstices and equinoxes, then returns to the real calendar. Each time of day is found from the sun's real rise and set on the date shown (`RoomHud.TimeStopHour`), so "sunset" is the golden hour in June and in December. The top panel shows the hour, the season and the date, and says whether each is pinned or real. It uses `LookDirector.SetClock` and `ReleaseClock` only. | `PlayHudTest`: 7 times in order, noon on the June solstice at 83° sun, the June day 14.1 h and the December day 10.2 h, both released back to the real clock. |
| The companion's name tag is blurred (F2) and smeared and doubled while the camera moves (F3). | The tag was alpha-blended: it wrote no depth, so depth of field read the wall behind it, and it wrote no motion, so temporal anti-aliasing (TAA) smeared it. | `Label3D` with `AlphaCut = Discard` (threshold 0.5): the tag is cut out and drawn solid, so it has depth and motion like the body. `OpaquePrepass` was worse (only the outline came out crisp); `Hash` matched `Discard`. | Edge sharpness of the tag (mean of the strongest tenth of Sobel gradients; bigger is crisper): F2 0.051 to **0.293**; F3 orbit 0.145 to **0.217**, and **0.282** once TAA is off (the tag at rest scores 0.286). |
| The player is soft while running, the companion at the same distance is sharp. | **TAA**, not depth of field and not the missing physics interpolation. | See the table below. The fix is a `project.godot` change (the integrator's file): TAA off and FXAA on. Physics interpolation is prepared in code and left off. | Below. |

**Diagnosis of the soft running player** (`tools/kernel/capture-play.ps1`, scenarios `run_f3` and `run_f2`, 1280 x 720 on the RTX 2070 SUPER, a 144 Hz display with vsync on; the player runs past a companion standing still). The number is how many pixels the body's silhouette takes to go from body to background across its torso: smaller is crisper.

| Setting | F3 running player | F3 companion standing | F2 running player | F2 companion standing |
|---|---|---|---|---|
| TAA on, no interpolation (as played) | 3.2 | 2.8 | 3.8 to 4.2 (up to 9.8 on some frames) | 2.3 to 2.4 |
| TAA on, **physics interpolation on** | 3.2 | 2.7 | 3.2 | 2.3 |
| TAA off, no AA | 2.3 | 1.6 | 1.8 | 1.1 |
| **TAA off, FXAA** | 2.3 | 2.0 | 1.8 | 1.5 |
| FSR 2 at native scale (TAA off) | 2.6 | 2.2 | 3.0 | 1.6 |
| 4x MSAA | not usable: the look's grain and vignette effect fails on an MSAA buffer ("needs the TEXTURE_USAGE_STORAGE_BIT usage flag") | | | |

Capped at 60 frames a second the TAA softness is worse (F2 running player 4.3), and interpolation changes nothing at 60 Hz. So TAA is the cause: it reprojects a moving body's history through a fast-moving floor, which smears its edges. Physics interpolation does not change the blur, but it does steady the picture at 144 Hz, where physics ticks (60 a second) fall unevenly between frames: the player's scatter about a smooth path across the screen (F3, running) falls from 2.3 to 0.8 px, and the floor's largest frame-to-frame step in F2 from 3.7 to 2.1 px. At 60 Hz there is nothing to gain.

The code is ready for interpolation either way: the F3 and F4 rig is not interpolated itself and follows the player's *interpolated* position (`GetGlobalTransformInterpolated`), and a teleport (`TryTeleportTo`, so respawn and recover) calls `ResetPhysicsInterpolation`. The suites pass with it on. What it costs: mouse look turns the body from input events, so with interpolation on the turn is expected to reach the screen up to one physics tick (about 16 ms) late (not measured: this is the thing to feel); and the look's depth-of-field focus (Look lane) still reads the physics position, a few millimetres behind the drawn body.

Frame time (`timing` scenario, uncapped, F3 on the standing avatars, two runs each): GPU 4.10 and 4.16 ms with TAA, **3.82 and 3.84 ms with FXAA** (3.80 and 3.82 with no AA); wall time 7.65 to 7.35 ms. Interpolation adds about 0.02 ms. No other EnFractal window was open.

The proposed `project.godot` diff, for the integrator:

```
 [rendering]
-anti_aliasing/quality/use_taa=true
+anti_aliasing/quality/use_taa=false
+anti_aliasing/quality/screen_space_aa=1
```

and, optionally, once the founder has felt mouse look with it:

```
 [physics]
 3d/physics_engine="Jolt Physics"
+common/physics_interpolation=true
```

Depth of field needs no change without TAA (its jitter shows no noise in the captures). The Look lane should judge fine floor-board shimmer in motion, which TAA hid. `PlayCaptureHarness` (`game/tests/native/Kernel/`) takes `--taa`, `--saa=fxaa`, `--interp`, `--fps` and `--vsync` to rerun any of this.

## Next playtest checklist

Run `pwsh -NoProfile -File run-room.ps1`; hold the left button and drag to look around.

1. **Shift** while walking with **W**: does the run now feel like a real change of pace, and does it start and stop crisply?
2. **G** twice to reach floaty (the console prints `room_floaty gravity=0.6`). **Space** next to the book: does the jump hang about a second? Hold a direction in the air: can you steer it? Walk off the box edge or the book: is the slow drift fun or too slow? **G** again returns to tuned.
3. **F3**: move the mouse to orbit and tilt, and roll the **wheel** to zoom. Walk with **W A S D**: does moving relative to the view feel right, and does the body turn naturally? Walk next to a wall and orbit towards it: does the camera stay in the room? Is the depth of field still on the avatars? Then **F1** and **F2**: unchanged?
4. **1** (follow), then walk, turn on the spot with the mouse in F2, and walk back the way you came: does the companion stay beside you and not swing behind? The companion is now your size (10 cm): is 12 to 30 cm, resting about 17 cm away, a comfortable distance? Does it keep up when you run (**Shift**)? Does it still read as a separate character (colour, hat) at this size?
5. Walk behind the big box so it is between you and the companion, then **3** (come), and **1** (follow) from there: does it walk round the box and reach you? Is the route natural? Come now stops 14 cm from you.
6. Stand still on the rug, then on the book, in **F2** and **F3**: do both avatars stand on the surface now, or does either still look as if it hovers? If one does, note the view, the time of day and where its shadow starts.
7. Anything that bobs, catches or clips, and anything the HUD says ("path blocked").

## Carry playtest (Run 2, P3)

Run `pwsh -NoProfile -File run-room.ps1`; hold the left button and drag to look around. The room remembers where things are put (`%APPDATA%\Godot\app_userdata\Garage-Game\saves\rooms\test_room\`); delete that folder to start over.

| Key | Does |
|---|---|
| **F** | Pick up the thing you face within reach; holding something, set it on the thing you face if it has a top (the box, the book), else down in front of you |
| **V** | Push the thing you face 10 cm |

1. The doorstop is the small block on the rug, a step ahead and to the left of where you start. Walk up to it, face it and press **F**: it rides over your head and the top panel says "holding Doorstop".
2. Carry it across the room to the big cardboard box (ahead and to your right, about a metre away), in F2 and F3: does it read as carrying, and does it stay with you through turns, jumps (**Space**) and the rug's edge?
3. Face the box and press **F**: it lands on top. Press **V** to push it along the top, and again until it falls off the far edge.
4. Press **F** facing the book: too heavy (0.6 kg against your 0.5 kg). Press **V** at it instead: it slides, stops at walls and rides over the rug's edge.
5. Pick the doorstop up again and set it on the book (stacking), then try to push the book.
6. Try: setting something down facing a wall or the box (no room), and putting it on the table (out of reach at 75 cm).

Check against: is 35 cm (3.5 body heights) the right height to reach up to; is carrying over the head right, or should it be in front; should carrying slow you down; is 10 cm a good push; are the messages clear.

## Open questions for the founder

- Should low gravity ever let the body jump higher (for example floaty reaching the 30 cm box)? Today gravity changes the feel only.
- Is a 0.6 m/s floaty fall too slow on long drops (1.75 s off the table)? It is one number in `world_physics_profile.gd`.
- In F3, should W A S D stay relative to the view (as now), or should the mouse turn the body as in F1 and F2?
- Should the companion ever plan routes that use its 2 cm step or its jump (onto the book)? Today its routes climb at most 1 cm: the floor and the rug.
- At 10 cm, should the companion run faster than the player (1.20 m/s against 0.96 m/s, so follow can catch up), or match the player and fall behind while the player runs?
