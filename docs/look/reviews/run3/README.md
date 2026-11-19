# Run 3 look reviews

## The "before" sets (18 November 2026, the integrator)

The 10x garage island as it stood when Run 3 opened: the game code of `f7af65f` (main), the stand-in garage from the fixture cache (`-Room landscape10`), the island cameras (`tools/look/island_cameras.json`), 1920x1080, on the RTX 2070 SUPER. Every Run 3 look packet is judged against these.

| Folder | Clock | Sheet |
|---|---|---|
| `before/` | 09:00, 21 June (the landscape's own setup) | `sheet-before.jpg` |
| `before-dusk/` | 19:00, lamps off | `sheet-before-dusk.jpg` |
| `before-night/` | 21:00, lamps on | `sheet-before-night.jpg` |

```powershell
pwsh -NoProfile -File tools/look/capture-look.ps1 -Label before -OutDir docs/look/reviews/run3/before -Cameras tools/look/island_cameras.json -Room landscape10 -NoLightChecks -Jpg 88
```

The dusk and night sets used copies of the cameras file with only the `clock` block changed (`hour` 19.0; `hour` 21.0 with `lamps` on).

Four launches, 28 images rendered, 21 kept. The first launch rendered the day set and then failed in the time-of-day sweep, so it was retaken without `-Sweep`.

### Frame times (ms: p50 / p95 / worst frame; 300 frames each)

| Camera | Day | Dusk | Night |
|---|---|---|---|
| spawn | 14.6 / **18.1** / 67 | 14.7 / **18.2** / 65 | 15.0 / **18.3** / 66 |
| summit (see below) | 10.1 / 13.5 / 25 | 10.0 / 13.4 / 29 | 10.1 / 13.4 / 26 |
| hamlet | 8.0 / 11.3 / 24 | 7.9 / 11.1 / 24 | 7.7 / 11.8 / 29 |
| beach | 10.8 / 14.1 / 27 | 10.7 / 14.1 / 27 | 10.9 / 14.1 / 30 |
| flight | 4.7 / 7.7 / 17 | 4.8 / 7.8 / 10 | 4.9 / 7.9 / 10 |
| fractal_near | 11.8 / 15.2 / 16 | 12.0 / 15.4 / 27 | 11.9 / 15.3 / 21 |
| fractal_far | 16.2 / **19.6** / 41 | 16.2 / **19.5** / 39 | 16.2 / **19.4** / 31 |

Bold is over the provisional budget of 16.7 ms p95. Two lanes were reading documents while these ran; none was building.

### What the harness could not do

- **The summit picture is not from the summit.** The harness reported "the player could not be placed at (22.469, 20.213, 30.217) (no ground there yet?)" in all three sets, so `summit.jpg` is taken from where the player already stood. There is no picture from the top of the island yet.
- **`-Sweep` fails with the island cameras** (`LookCaptureHarness.Sweep`, line 595: no camera matches the one the sweep looks for). The time-of-day sets above stand in for it.
- **`flight.jpg` is mostly sky and sea.** The flight is there for its frame times.

## L-perf (18 November 2026, Lane L)

**The freeze after the island appears** (the frame probe, windowed, on the 10x island; `FrameProbe --split` times each of the world's children): the first frame after the load was 846 to 1064 ms in four runs. In it ran every view's first process (a CanvasLayer of the HUD's about 100 ms, the look's bubble warm-up 66 ms, the campfires 50 ms, the copies 40 ms, the command host's physics 25 to 45 ms) and the intro's set-up (140 ms, plus about 100 ms more round it), and the next frame draws the island's first 87 pipelines (render CPU 230 to 300 ms). The intro's set-up now waits two frames (`RoomIntro.SetUpAfterFrames`, under the black cover): the freeze frame is 727 to 729 ms in two runs and the set-up a frame of 183 to 198 ms of its own. What is left is outside the look (the HUD, the command host, the engine's pipelines).

**The summit, from the summit.** The 10x island's highest ground is a slope the player cannot stand on, so the summit camera is now the highest ground the body's own ray finds footing on (`IslandCameras.RayFooting`). `summit/`, `summit-dusk/` and `summit-night/` are that view by day, at 19:00 and at 21:00 with the lamps on, the look unchanged (`-Only summit`). The flight flies at the summit's height plus 3 m, so it is lower now and its frame times are not comparable with the `before` set's.

**`after-perf/`**: the full set after the packet (`-NoLightChecks -Jpg 88`). The look is unchanged, and so are the views' frame times within run-to-run spread (ms, p50 / p95):

| Camera | before | after |
|---|---|---|
| spawn | 14.6 / 18.1 | 14.6 / 18.2 |
| summit | 10.1 / 13.5 (not the summit) | 8.2 / 11.8 (the summit) |
| hamlet | 8.0 / 11.3 | 7.9 / 10.7 |
| beach | 10.8 / 14.1 | 10.8 / 13.9 |
| flight | 4.7 / 7.7 | 7.6 / 11.7 (a lower flight) |
| fractal_near | 11.8 / 15.2 | 11.8 / 15.4 |
| fractal_far | 16.2 / 19.6 | 15.9 / 19.3 |

**Where the spawn's GPU time goes** (Godot's `--gpu-profile`, the play camera at the spawn, 10.2 ms of GPU): depth pre-pass 2.7 ms, opaque pass 4.3, the sun's shadows 1.0, transparent 0.4, glow 0.4, tonemap 0.3, depth of field 0.3, SSAO 0.2, sky 0.1. The land's geometry, drawn in the pre-pass and again in the opaque pass, is where headroom has to come from.

**Tried and refused:** small plants drawn to 30 m instead of 40 (`DrawDistances.SmallPlantM`) gave spawn 13.4 / 16.6 against 14.5 / 17.8 back to back, and fractal_far 12.7 / 16.0 against 13.3 / 16.7, but the spawn picture moved past the noise (mean |d| 9.6 and 37.5 % of pixels over 8, against 2.7 and 6.8 % between two captures of the same code). A visible change, so not kept.
