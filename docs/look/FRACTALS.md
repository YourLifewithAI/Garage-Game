# The look of a Fractal

**Run 2, L9, Lane L (15 October).** The found things that grow the body, the game's namesake ([MECHANICS.md](../game/MECHANICS.md) section 6), drawn where the generator placed them: a body you can reach for, a glint that reads from a band away and at night, a slow life, and the moment one is picked up. The plan is [RUN-2-LOOK.md](../runs/RUN-2-LOOK.md), L9. The code is `game/scripts/native/Look/Fractals/` (`FractalLook`, its numbers in `FractalLook.Tuning`; the bodies in `FractalMesh`), the shaders `game/shaders/fractal_form.gdshader` and `fractal_glint.gdshader`, the checks `game/tests/native/Look/LookPresetTest.Fractals.cs`.

## What it reads, and what it never touches

- **The host's read-only state, nothing else.** `CommandHost.AllFractals` sizes the pools (at most `MaxFractals`, 1000), `Fractals` says which are still on the island, `FractalBox` is the 4 cm box the body stays in, and `NatureChanged` says when to read `Fractals` again (a pickup commits through the nature block). Never the manifest: the kernel read it, fail-closed, and a manifest it refused draws nothing (the look's summary repeats `FractalsNotice`).
- **Scenery, as the fish are.** No entity, no save, no collider, no light, nothing in the kernel's sight or the journal's map. The same drawing in Creative and Challenge (which Fractals remain is the save's), in every view and at any hour, on the world layer.
- **Attached beside `NatureLook`** in `RoomWorld` once the host's nature is configured (so the Fractals picked up in an earlier session are simply not there): `AddChild(FractalLook.Create(commandHost, Look))`.

## The body: the star, the founder's pick of three

The founder chose the body from a sheet of three, rendered on the 10x garage by day (09:00, the island's own setup) and at 21:00, blind (`fractal_forms.png`; the rows' order was a coin flip, written down before the sheet was shown: 1 the seed, 2 the crystal, 3 the star). **The founder picked 3, the star (15 October).** The agent had recommended the crystal, in writing before the sheet went out. `FractalLook.Tuning.Form` holds the pick; the crystal and the seed stay built, and `--look-fractal-form=crystal|seed|star` after `--` (or `GARAGE_GAME_LOOK_FRACTAL_FORM`) pins one for a capture.

| Form | What it is | Triangles |
|---|---|---|
| **Crystal** | A held fractal: a tetrahedron that grows a smaller one on the middle of each face, twice (the Koch tetrahedron's second step), a body diagonal upright, so it is a star of 144 facets with a point up and a point down, 2.7 cm tall. Pale gold-white, each facet its own tone, a crisp sparkle where a facet turns between the sun and the eye, a faint cool rainbow at grazing angles (a thin film). | 144 |
| **Seed** | A fern's crosier: a felt tube that rises from a short stem into a plump coil of 1.6 turns whose rounds touch, drifting sideways as it winds so it reads from every side as it turns, 2.5 cm tall. Pale gold felt (soft, matte, a velvet sheen and a rim from a light behind, as the characters' felt), a faint light from the heart of its curl. | about 180 |
| **Star** (the pick) | No body: a soft heart of light 1.2 cm across in a halo 4.5 cm across, with four long rays and four short, between the body's pale gold and the glint's cool white, the rays fringed with a rainbow: the wisp's heart (GLOW.md) made small and cool. It hovers 2 cm up and bobs as a wisp does, so its halo reaches the ground: where its light meets a surface it softens over 3 cm, from the depth buffer, as the wisp's halo does, so no hard line cuts it. It casts no shadow; at night it casts a small pool of light (below). | 0 (a quad) |

The crystal and the seed carry a faint inner light (`InnerLight`, 0.12 of their colour) so a Fractal in reach still reads at night; it lights nothing.

## The glint

Above each body a camera-facing quad of light (one MultiMesh, written on the CPU each frame, one draw), added to what is behind it: a heart, a halo and rays, cool white with a rainbow on the rays, never the Gubble's warm glow colour.

- **It reads from a band away:** 2 cm across up close, and never under 12 pixels on screen however far (its size grows with distance in the shader), so it is drawn at every distance the island is seen. As it grows its heart softens to fill it and its rays thicken, so a far glint is a small bright sparkle and not one pixel, and it rises by half of what it grew, so it stands on top of its Fractal clear of the grass round it (from a band below, a Fractal on a ledge sits behind the ledge's grass at a grazing angle). Its body fades out at the small plants' 40 m, dithered; the glint goes on to the far plane. In the F2 view the distance is in the depth of field's blur, which spreads the glint into a soft bright disc; it still reads.
- **A pitfall, fixed (15 October):** the pixel rule divides by the projection's `[1][1]`, which the engine makes negative when it flips Y for a render target (the capture harness's SubViewport). Taken as it came, the glint never grew: one pixel at 3 m and nothing at 14 in the first captures. The shader takes its size, and the look suite pins that.
- **It twinkles** on its own slow period (2.6 to 4.2 s, from its id, so no two blink together), never below 0.45 of its brightness, peaking briefly; its rays turn once in 20 s.
- **Up close it steps back:** within 35 cm of the eye it dims to 0.3, so the body in reach reads rather than a flare.
- **At night** its brightness (2.5, the wisp heart's) is over 1, so the bloom catches it.
- **The haze and the sea mist hide it as they hide the land.** The engine's own fog over an additive quad would add the fog's colour to the whole quad, so the shader skips it and mirrors the look's depth fog itself (`ViewDistances.FogAt`, the environment's begin, end, density and curve, followed each frame). From the seam out no play camera sees a glint, as it sees no island.

## The light at night

**A Fractal casts a small pool of real light at night** (the founder, 15 October: "a fun way for players to find it"; the plan had recommended a self-lit glint only). Each is an omni light at the Fractal's heart, energy 0.18 reaching 0.6 m with a steep falloff (1.5), between the body's pale gold and the glint's cool white: a soft pool on the ground round it that lights a player's near side and the nearest flowers and fades into the night. It fades in as daylight falls through the lamps' own switch (the preset's `switch_on_below_daylight`, fully on at half of it), so the Fractals light up when lamps would, and goes out with the body when one is picked up.

- **Within the budgets:** it casts no shadow, so it takes no slot of `max_shadowed_lights` (the sun's, the windows', the lamps' and the wisps'), and it is baked into nothing. At most 32 are lit (the 10x garage has 24, so all of them), those nearest the player, chosen again every 0.25 s; the engine fades a light out past 40 m from the camera.
- **Cost:** on the 10x garage at 21:00, 0.15 to 0.26 ms more GPU time a frame than the same views without the lights (the first tuning, energy 0.3 over 0.4 m); the CPU's share stays in the 15 µs a frame below.
- **Not in the light estimate:** `LookDirector.LightLevelAt` counts the lamps and the Gubble's glows, not the Fractals' pools, so a spot by a Fractal still reads dark to the dusk moment and the smart ask. Counting them is a small change in `LookDirector.Glow.cs` (not L9's file); a question for the founder, since it would make Fractals a light source the game reasons about.
- **What it does and does not do for finding:** in reach and a few metres off the pool reads at once. From 14 m off and a band below, a Fractal on a ledge hides its pool behind the ledge's edge; there only the glint carries it.

## The slow life

The body turns once in 12 s and bobs 5 mm up and down over 3 s about the middle of the kernel's box (2 cm up); the star bobs as a wisp does, 1.2 cm over 3.2 s. Each Fractal's phase comes from its id. Through its whole life every body stays inside `FractalBox`, so what the player reaches for is what they see. The life is a clock the look keeps (`LifeS`), so it is the same for the same ticks.

## The pickup moment

When `Fractals` loses an id (read on the next tick after `NatureChanged`), that Fractal's body shrinks away over 0.25 s while its glint flares and goes out, and eight sparks rise from its spot over about 0.6 s, spreading a little (the Fractal look's own small MultiMesh of the glint's quads; never `StartGlow` or the effects, whose cap is magic's). No words, no sound. A Fractal that comes back (a save restored) simply appears.

## Cost

Three MultiMeshes, one draw each, under 200 triangles a body; the sparks' pool is written only while a pickup moment lasts. Measured headless (the debug build the suites use):

- **The 10x garage's 24 Fractals: about 15 µs a frame on the CPU with the night's lights, nothing allocated** over 600 frames (the plan's target: under 0.1 ms). The look suite holds it under 100 µs and to zero bytes.
- **A thousand (the kernel's most): about 0.5 ms a frame, nothing allocated**; the suite holds it under 0.6 ms.
- The island frame probe (`native_look_island_probe.tscn`, the 10x garage) cannot resolve it: switching the node off moves the frame by less than its noise (the process step 0.55 against 0.61 ms, other nodes swinging as much), and the allocations a frame do not change.

## The cameras

`tools/look/island_cameras.json` has two fixed cameras for the 10x garage alone (its Fractals exist at 10x only), both at **`fractal:c90007134ecf`**, the island's most open Fractal (band 1, on a low stone ledge): **`fractal_near`** sees it from a body length away with the player beside it, and **`fractal_far`** from where the player stands 14 m off and a band (0.38 m) below, as the F2 view zoomed out with the wheel (1.2 m behind the player, 0.55 m up, focus on the player as in play). Capture with `capture-look.ps1 -Room landscape10 -Cameras tools/look/island_cameras.json -Only fractal_near,fractal_far,spawn -NoLightChecks`.

**How the spots were found, and what it says about finding Fractals.** The Fractal nearest the spawn sits on a stone ledge ringed by trees: from no spot 10 to 20 m off is it in view at the 10 cm eye. The spots were chosen with a line-of-sight model built from the export (a conservative height envelope of every shell mesh, the room's objects, and every copy taller than 6 cm as a cylinder), which reproduced every diagnostic picture. Run over all 24 Fractals, 12 have no spot 10 to 16 m off and 0.15 to 0.8 m below from which the land, the trees and the shrubs leave a line to them; the most open has 70. M4's promise that "the next ledge is always in view of a player one band short" holds for the land, but at the 10 cm eye the copies hide a 4 cm Fractal well before the land does: the glint is what can carry it, where any line is left.

## What needs the founder's eye

1. **The form**, from the sheet, blind.
2. **Does a Fractal light its surroundings at night?** Decided by the founder (15 October): **yes**, a small pool of light (above). Still open: whether the light estimate counts it.
3. **Does it move?** Recommended and built: the slow turn and the small bob; the star bobs like a wisp.
4. **The colour:** recommended and built: one family, pale gold-white with a cool rainbow glint, never the aura colour.
5. **The pickup moment:** recommended and built: the sparks, no words; sound later.

These numbers are code constants for this round (`FractalLook.Tuning`); the preset's `x_look_fractals` block carries them once the founder has judged.
