# Sound

The Sound lane's design, for the integrator and the settings screen. Everything the game plays is made in code: no audio file, sample or library is downloaded or copied. Music is not made (the founder has not chosen where it comes from); the Music bus exists and is empty.

## Wiring (integrator)

- `AudioDirector.Attach(world)` in `RoomWorld._Ready`, after `CommandHost.Attach` (it finds the host with `CommandHost.Of(world)`). One call; it edits no other file and reads only public state.
- The bus layout is `game/assets/audio/default_bus_layout.tres` (Master with a hard limiter at -3 dB, then Ambience, Effects, UI, Voice, Music, all sent to Master). `AudioBuses.EnsureLayout()` makes the same buses when the project does not set the file, so the game works either way.
- Settings screen: `AudioSettings.Get/Set(bus, 0..1)`, `Mute/IsMuted`, `Reset`, `Save/Load` (`user://audio.cfg`). `Attach` loads the saved file.

## What listens to what

| Sound | Source |
|---|---|
| Wind (soft and gusty loops) | `NatureWeather.WindMps` length; quiet indoors |
| Sea | distance to the coast outline (`RoomSea.Coast`), 14 m hearing range |
| Rain | `NatureWeather.Rain01` |
| Birds by day, insects by night | `LookMoment.Daylight` and `Season`; none in rain, few in winter, none indoors |
| Campfire | `CommandHost.CampSites` and `IsCampfireLit`, positional, nearest lit fire within 6 m |
| Footsteps (grass, dirt, sand, stone, wood) | the player's speed and floor contact; ground from a short ray, named or tagged (`ground` meta) colliders, else grass outdoors and wood indoors |
| Wading, swimming, splash | `SmallPlayerController.Water` |
| Pick up, put down | `CommandHost.Handled`: `item.pickup`, `item.gather`, `inventory.take` and `inventory.drop`, `inventory.store`; only the player's, successful, real (not preview, not replay) |
| Eating | `CommandHost.FoodSound` |
| UI ticks | every `BaseButton` that enters the scene tree (press, hover) |

## Why synthesised at run time

The whole bank (28 sounds, 2.4 MB as 16-bit) takes about 0.2 to 0.3 s on one worker thread at start. The main thread only wraps finished samples in streams (three a frame) and sets volumes, so nothing hitches. Baked files would add pinned bytes, import files and a builder for no gain at this cost. Samples are made with a seeded generator; the recipe is deterministic within a build (a platform's maths library could differ in the last bit, which is why no hash of the samples is pinned).

## Listening

`pwsh -NoProfile -File tools/audio/render-preview.ps1` writes every sound as a WAV into `.cache\scratch\preview` (loops three times over to hear the seam). Headless: nothing plays through the speakers. The renders are not committed.

## Provisional

Every level, stride, range and timing here is a first guess for the founder's ears: bus defaults, the 6 m fire range, 14 m sea range, bird spacing (5 to 15 s), footstep strides (0.11 m walking, 0.17 m running).
