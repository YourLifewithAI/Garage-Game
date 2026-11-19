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
