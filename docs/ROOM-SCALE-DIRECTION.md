# EnFractal direction: one room, two avatars, AI as magic

**Direction change, 6 October 2026.** EnFractal no longer starts from real geography at district or globe scale. It starts from **one real room or house that the player photographs**, reconstructed into individually editable game objects, restyled into something whimsical, and inhabited by a **player avatar about 10 cm tall** and a **separate avatar for the player's own AI**. The AI is the player's magic. Everything else in this repository is measured against that.

The earlier vision (Barton Creek, the Pfluger district, a shared painterly Earth) is no longer the product. Its code and data live on the `geography-era-final` branch; its planning documents are under `docs/history/` with superseded banners. The [cleanup plan](CLEANUP-PLAN.md) records what was removed, what carries forward and the founder decisions behind it.

## What stays true from the earlier vision

- **Two embodied identities.** The player has a personalized avatar. Their AI has a separate, customizable, named avatar with its own stable identity. Both are in the scene; neither is an editor cursor.
- **The AI is the magic.** The player speaks or types a wish; the companion interprets it and acts through the same validated world operations the player's manual controls use. The companion never gets a second physics or a bypass around the rules.
- **One baseline ruleset.** Data-only creation manifests, one compiler, one authority, budgets, receipts, and protection (save versus save-and-protect). The existing compiler/authority/world-state kernel is the seed of this; it is kept and generalized.
- **Game-only AI profile with a hard security boundary.** Scoped observations, typed commands, no shell/files/URLs/credentials, model-neutral transport (MCP or equivalent). Any capable AI client should be able to be the companion.
- **Native Godot .NET with C#.** [ADR 0001](engine/decisions/0001-native-godot-csharp.md) stands. New gameplay code is C#; surviving GDScript migrates incrementally.
- **Single-player first, multiplayer last.** Unchanged.
- **Honest evidence.** Checkpoints record what runs and what is still open. Screenshots do not certify collision; agent ratings do not certify fun.

## What changes

| Earlier | Now |
|---|---|
| Real geography from USGS/OSM, 4 km districts, a shared Earth | One room or house from the player's own photos |
| 0.30 m player in real-scale terrain | ~0.10 m player in a real-scale room (a 5 m garage is a 50-body-length world) |
| Terrain height grids, collision streaming, map packages, spatial pins | A room shell (floor, walls, ceiling, openings) plus a set of discrete object assets with stable IDs |
| Offline Python geodata builder | An **MCP server plus agent skill** that guides photo capture, reconstructs the space, produces per-object assets, applies a style, and exports a Godot room |
| Painterly Central Texas landscape as the art laboratory | A chosen whimsical style applied over reconstructed indoor objects; Tiny Glade warmth remains the reference for feel |
| PostgreSQL local save/travel service with sandbox worlds and portals | Simple local saves per room (file-based); travel between rooms of one house is in-process scene switching |
| Wish examples: hurricane, flood the river, castle on a bank | Wish examples: a dragon haunting the garage, a cozy village across the shelves, the room turned into a sci-fi spaceport |

## The experience, in order

1. **Capture.** The player photographs a room (phone is fine). Their AI reviews the set, says what is missing ("I have never seen the floor under the desk; take four more photos of the shelving from the left"), and asks for more until coverage is good enough.
2. **Reconstruct.** The pipeline recovers the room shell and an inventory of objects with positions, sizes and categories. Each object becomes its own complete, closed, collidable asset with metadata (category, material, mass estimate, movable or fixed, affordances such as openable or container). Fused scan meshes are reference material, not the game world.
3. **Stylize.** A style preset (painterly, storybook, toy, cel, and so on) is applied consistently across the shell and every object. The result should look handmade and inviting rather than like a photogrammetry scan.
4. **Inhabit.** The player and companion spawn on the floor at ~10 cm. The player walks, jumps, climbs onto low things, and manipulates objects within their reach; the companion follows, looks, points, fetches, and acts.
5. **Wish.** The player asks for something. The companion composes supported capabilities into a result: creatures with bodies and behaviors, structures from generators, transformations of existing objects, bounded effects. Previews gate destructive changes; locks protect what the player chose to keep.
6. **Persist.** Saves record the room's object state, creations, locks, companion identity and style pins. Reload reproduces them. Undo restores eligible prior revisions.

## Baseline rules the kernel must provide

These are the primitives every scenario (dragon fight, cozy village, spaceport) composes from. None of this is new to the project's thinking; the room scope makes it tractable.

- **Entities with stable IDs**: shell parts, scanned objects, avatars, creations, creatures, effects. Each has transform, bounds, material role, collision, protection state, provenance (scanned, generated, player-made, AI-made) and revision.
- **Operations**: inspect, propose, validate, preview, commit, revise, remove, act, stop. Manual controls and the AI use the same ones.
- **Capability registry**: typed, bounded primitives the compiler accepts (shapes, materials, joints, forces, lights, timers, triggers, movement goals, spawn, transform, restyle, and later health/damage/hit for combat).
- **Budgets**: per-wish and per-room limits on parts, active bodies, effect duration, generation time and inference cost. Splitting a wish cannot evade them.
- **Protection**: save versus save-and-protect; locks resist direct and indirect effects; unlock is a direct player action outside the AI's tools.
- **Receipts and idempotency**: every mutation has an action ID, fingerprint and durable receipt.
- **Scenario packs** (later): bundles of generators, creature families, behavior goals and style overrides that make "haunt this room with a dragon" or "turn this into a spaceport" a composition rather than a hard-coded mode. Combat needs health, damage and hit-detection primitives that do not exist yet; that is S3 work, not an assumption.

## Scale and physics decision

**Update, 13 October 2026: the world is 10x.** After walking an experiment, the founder chose to magnify the room ten times into the island around the same 10 cm body: "I MUCH prefer the island at this scale." The body, the Gubble and everything sized for the body (trees, cottages, plants, paths, water depths) keep their size; the room's landforms become ten times larger, so a garage becomes an island of about 67 × 77 m. One unit is still one metre. The plan is [runs/RUN-2-TEN-TIMES.md](runs/RUN-2-TEN-TIMES.md).

Keep **1 world unit = 1 metre** and keep all authored and captured dimensions in metres. The capture pipeline produces real-metre assets, the existing controllers and body-profile contract are metric, and nothing has to be rescaled. The player body profile becomes roughly 0.10 m tall, 0.02 m radius, 0.087 m eye height, 0.15 m reach; exact values are tuned in S1.

Two things are decisions, not assumptions:

- **Game feel at 10 cm.** Real gravity makes a 10 cm body fall and jump in a fraction of a second. The existing bounded physics-profile mechanism (`world_physics_profile.gd`) already allows a tuned gravity/jump profile; treat feel as a tunable, not a physics-accuracy claim.
- **Engine precision at small scale.** Godot physics (Jolt is built in since 4.4) is tuned around metre-scale bodies. If a 0.02 m radius capsule proves jittery, the fallback is to scale the room ×10 at import (1 unit = 10 cm) and keep the avatar at 1 unit. That is a one-line import setting if assets stay metric; it is not a reason to abandon metres now.

## Single-player choices that keep multiplayer possible

Multiplayer is a real future goal: players inviting other players and their companion AIs into a shared room. It is deferred until single-player is smooth. These four habits cost nothing now and avoid a rewrite later. None of them is networking.

- **One command path for every world change.** Typed command, action ID, authority check, receipt. No gameplay code moves or edits an entity outside that path. In multiplayer that path becomes the server's; everything else replicates its results.
- **The room is data; the scene is derived.** Shell, objects, creations, locks and avatars are fully described by serializable state, and the Godot scene is rebuilt from it. Sharing a room later is sending that state.
- **Every action names a principal.** Player, companion, and later guests and their companions are principals with grants. Keep "who did this" on every action even with one person in the room.
- **The companion connects through a restricted adapter.** Scoped observations and typed commands only. Each future guest brings their own AI through the same surface, which is what keeps a guest's companion from wrecking the host's room.

Deferred until the multiplayer phase: transport, replication, accounts, hosted services, consent and moderation, and any database service. The persistence semantics from the earlier save/travel work (idempotent receipts, fenced sessions, a checkpoint before any transfer, reconcile-before-retry) are kept as rules for the file-based saves, not as a server.

## Rendering decision to revisit

The earlier choice of the Compatibility renderer served an 8 GiB integrated-graphics baseline for a 4 km outdoor world. A single bounded room is the ideal case for **Forward+ with VoxelGI** (bounded interior global illumination) and depth of field for the tilt-shift "diorama" look described in the founder's art notes. Recommendation: switch the room scene to Forward+ and profile on the development machine; keep a Compatibility fallback only if a measured device requirement appears.

## Tracks, runs and the agent team

**Revised 6 October 2026** after reviewing the first R0–R5 draft against the actual target: a fully rendered, art-styled, sandboxable room. That draft reached "discrete objects in Godot" and "AI magic" but treated the art style as one line inside an assets phase, placed it after the capture pipeline, and skipped the basic sandbox verbs. The earlier art effort failed in exactly that shape (see the [painterly pipeline diagnosis](research/13-painterly-pipeline-diagnosis.md): a blockout was refined repeatedly instead of building an art pipeline). So the plan is now four parallel tracks that converge in integration runs, each run built by a team of parallel agents with owned files, one integrator and independent reviewers.

### The four tracks

**Track L — Look.** The style is developed on the placeholder room with primitive props *before* any captured asset exists. If a box room with box furniture cannot be made to read as a charming toy diorama, captured assets will not fix it.

| Packet | Deliverable |
|---|---|
| L1 look bible | Reference frames chosen with the founder; a written rubric: palette and desaturation, warm key light against cool shadow, soft edge treatment, clutter density, tilt-shift depth of field, what "handmade" means per material role; fixed review cameras in the placeholder room |
| L2 renderer baseline | Forward+ with VoxelGI, soft shadows, ambient occlusion, depth of field that keeps the player's reach crisp, colour grading with time-of-day and season; measured frame time on the development card |
| L3 material system | The painterly surface/foliage/bark shaders generalized into material roles (wall, wood, fabric, metal, glass, paper, plastic) with albedo softening, stroke normals and edge wear; one preset file drives them |
| L4 handmade geometry | Mesh treatment that softens silhouettes: bevels, slight wobble, decimation that keeps charm; applied to both proxies and generated assets |
| L5 procedural charm | Clutter, trim, vines and moss that emerge on built or transformed surfaces; density rules by surface role |
| L6 asset restyle | Offline per-asset texture restyle for a preset (image-to-image on the atlas with depth/normal conditioning); consistency across a whole room |
| L7 review harness | Fixed-camera captures, side-by-side against the look bible, independent reviewer scoring; the 8.5 target applies |

**Track C — Capture.** The MCP server and skill from the [pipeline design](pipeline/ROOM-CAPTURE-PIPELINE.md).

| Packet | Deliverable |
|---|---|
| C0 pilot | Five garage objects taken by hand through generation, fit, collision, restyle and Godot import before the server exists; decides backends and settings |
| C1 ingest | HEIC conversion, EXIF, dedupe, blur and exposure scores |
| C2 coverage | Fast poses in batches, view graph, coverage map, specific guidance text |
| C3 shell | Floor, walls, ceiling and openings as planes; reference splat for review |
| C4 inventory | Detect, segment, associate across views, 3D boxes and poses, tiering, annotated top-down review |
| C5 assets | Generation behind one interface (local, fal.ai, Modal), fit to the measured box, collision, mass, affordances, metadata |
| C6 export | The room folder layout and Godot import |
| C7 server and skill | FastMCP tools with job handles, the SKILL.md workflow and capture heuristics |

**Track P — Play.** The kernel and the sandbox.

| Packet | Deliverable |
|---|---|
| P1 kernel generalization | Room bounds instead of a plot rectangle, physics surface query instead of a terrain sampler, principal on every action, room manifest pin, the invention runtime rewired to the C# controller |
| P2 body and physics | 0.10 m profile, retuned step, speeds, margins and jump feel; the jitter spike that decides whether the ×10 import scale is needed |
| P3 sandbox verbs | Pick up, carry, drop, push, stack, climb, place on a surface with snapping, break and join, all through the command path |
| P4 saves | File-based room saves per the [persistence notes](engine/persistence-notes.md), checkpoints and undo |
| P5 HUD and controls | Room HUD, remappable inputs, text scaling, reduced motion |
| P6 world as data | The scene is rebuilt from room state (shell, objects, creations, locks, avatars); no hidden scene state |

**Track A — AI companion.**

| Packet | Deliverable |
|---|---|
| A1 command surface | Model-neutral MCP tools for inspect, propose, validate, preview, commit, act, stop; the security boundary tests from the [AI and security study](research/05-ai-mcp-and-security.md) |
| A2 embodiment | Follow, look, point, fetch and act as goals through the kernel; visible listening/planning/acting state |
| A3 first magic | Transform an object and spawn a creature as capability manifests; previews and locks |
| A4 scenario packs | Dragon encounter (adds health, damage and hit primitives), cozy building generators, spaceport restyle; each a composition of capabilities |

### The runs

Each run is built by a parallel team: one builder per packet group with owned files, one integrator who owns the contracts and merges, and one independent reviewer when a lane completes a whole chunk of its roadmap, per [the orchestration policy](runs/ORCHESTRATION.md#reviews). No lane marks its own gate passed; the founder's judgment is final on look and fun.

| Run | Lanes in parallel | Exit evidence |
|---|---|---|
| **Run 0 — contracts** (done 6 October 2026) | Schemas for the room manifest, asset metadata, style preset, game command and room state; the owned-file map per track | [contracts/](../contracts/README.md), [ownership](runs/OWNERSHIP.md), [Run 1 brief](runs/RUN-1.md); the game loads rooms and presets through them |
| **Run 1 — the charming box** | L1 L2 L3 · P1 P2 · C1 C2 · A1 | The placeholder room with primitive props passes the look gate from fixed cameras; the 10 cm body feels right; the garage photo set yields a coverage report with specific guidance; the command surface passes its boundary tests with a mock client |
| **Run 2 — the scanned garage, first pass** | C3 C4 C5 C7 (started) · L4 L6 · P3 P6 · A2 · the journal's data | The garage's shell and five of its objects, as stylised stand-ins, load from data with collision; the player picks one up and carries it; the companion fetches one; the room rebuilds from data |
| **Run 3 — the garage** | C6 C7 · L5 L7 · P4 P5 · A3 | **The milestone:** the whole garage captured, styled, sandboxable and saved; a real AI client performs one loose wish; locks and stop hold |
| **Run 4 — scenarios and a second room** | A4 · the back yard as an outdoor shell · look iteration | Each scenario runs from a loose wish; the outdoor edge case works; budgets hold |
| **Run 5 — playtests and release** | Accessibility, performance, fresh-player sessions, Windows build | Founder and fresh players complete the loop without coaching |

The cleanup plan's "R0" work is Run 1's P lane. Multiplayer remains a later project with its own gates; the four invariants below keep these runs compatible with it.

## Founder decisions, 6 October 2026

- **Cleanup approved and executed**: geography, the PostgreSQL save/travel service, the multiplayer experiments and the geography research are gone; the painterly art is kept as reference; superseded plans live in `docs/history/`.
- **Multiplayer is a real later goal**: players and businesses scanning spaces, rebuilding them with their AI to the game's specs, and sharing them for individual or shared play. On hold until single-player is robust. The invariants above are how single-player stays compatible with it.
- **Capture device**: a base iPhone 17 with no LiDAR. Photos are the input; a cheap LiDAR measuring rig is a possible side project, not a dependency.
- **Compute and cost**: keep extra spend minimal; a few dollars at a time is acceptable. Options are being costed in `docs/pipeline/COMPUTE-OPTIONS.md` for the founder to choose from.
- **First style preset**: painterly/storybook with Tiny Glade warmth.
- **Scale**: 1 unit = 1 metre with a 0.10 m avatar; size and scale will be tuned in play.
- **First AI client**: model-neutral MCP surface; Claude is acceptable as the first client to pair. Accessibility to any capable AI is the requirement.

## Founder decisions during Run 1, 6 October 2026

Details and the founder's reference notes are in [the Run 1 status page](runs/RUN-1-STATUS.md).

- **Light comes only from real sources** (windows and lamps). Darkness is possible and is gameplay. Changing the lights is a 10 cm puzzle: the avatars need tools or powers, and the companion helps.
- **The player chooses a material medium** (felt, stone, clay, yarn, cardboard and so on), and the whole room manifests in it. Architecture styles (cottage, urban, modern) are player options too. "Fanciful" and "cozy" are the target words.
- **Cameras:** over-the-shoulder, first person and isometric, all with a gentle tilt-shift blur. While moving, focus follows the avatars; while building, it follows the cursor or a free camera.
- **The time of day follows the real clock** and the seasons follow the calendar, with stronger swings.
- **Avatars are felt figurines** with a few head, torso, arm and leg options for now.
- **The companion sees what is in its line of sight.** (Replaced on 7 October by [shared team knowledge and selective memory](companion/JOURNAL.md); companion-only sight remains the current code until Run 2.) Voice is for talking to the companion; the player uses the keyboard.
- **No approval clicks.** Host-enforced tiers with undo, preview-then-commit, and a spoken or keyed "yes" only for the irreversible; see [the live voice design](companion/LIVE-VOICE.md). A fixed command set comes first, with a help panel.
- **Bring your own AI.** Players connect their own AI or agent harness to the game's MCP surface. No model is bundled for now, and no hosted AI is provided. The audience is people who already use AI well. Multiplayer with players and their AIs comes later.
- **Style before medium.** First settle one artistic style in the spirit of Tiny Glade, with camera angles that read as artistic rather than photorealistic. Material media come later.
- **Building is a conversation over a ghost draft.** A kit of combinable pieces, starting with a Tiny Glade-like Victorian set, is assembled under host-enforced rules. Structure classes have their own rule sets, and buildings are always enterable. Buildings are grounded unless the player lets them float. The player confirms every build, can edit it afterwards and can save designs. Buildings are sized for the 10 cm figurines. See [the building design](companion/BUILDING.md).
- **The companion is the player's size (10 cm).**
- **Modes, later:**
  - a strictly creative mode;
  - a grounded challenge mode, with physics for both avatars, critters in the shadows, resources to gather, and AI upgrades that improve the companion's in-game abilities;
  - a "build only with what you see" mode in a captured room.
- **Physics:** the 10 cm body runs at 1 unit = 1 metre with no ×10 import scale, on Jolt Physics. The room renders with Forward+.

## Founder decisions during Run 1, 7 October 2026

Details are in [the Run 1 status page](runs/RUN-1-STATUS.md).

- **The player and their companion share one knowledge of the world.** This replaces "the companion sees what is in its line of sight". What either avatar's eyes see goes on one shared map; the camera adds nothing, so the pair can divide and conquer. Things out of view show as last seen. In multiplayer, knowledge is per team. See [the journal design](companion/JOURNAL.md).
- **Design for AI that keeps improving.** The game is built for AI systems as they grow more capable over the next couple of years: simple designs now, more complex later. The companion writes its own notes in the journal, so it feels like a buddy rather than rote AI.
- **Memory is selective.** The companion keeps important, game-relevant things: actions completed and at whose direction, how the things it made were later changed, and, in challenge modes, where it saw resources a build in progress needs. It keeps no log of every step or sight.
- **A journal and a minimap.**
  - The journal is old tan drafting paper in a leather-bound notepad. It shows what the player and companion are working on, as descriptions, never numbers. It also shows what has been built, and, where resources matter, where they are and how many remain.
  - The minimap is a small circle in an upper corner. With the journal open, the journal takes one side of the screen and an expanded map the other.
  - The map is drawn from room data, fades the levels the player is not on, and starts blank, filling in as the pair explores.
- **Cameras in challenge modes:**
  - only over-the-shoulder and first person;
  - the overview unlocks when the objective is met, or, in an open-ended challenge, at 75% of the map discovered;
  - creative modes are unrestricted.
- **The game refuses what the companion cannot do yet,** with a reason. More freedom for the AI is a later challenge.
- **Voice is deferred** until the baseline game is fully designed and working; it is a UI extension. The UI comes first: keyboard, fixed commands and symbolic buttons.
- **Run 2 keeps its spine: real objects first.** Building comes after the sandbox verbs and the real host.
- **The player's own AI makes the game's objects** (late on 7 October). It uses software on the player's computer (Blender, local models) or its own connectors and tools, guided by the game's MCP. No hosted or paid service is ever required, so the capture guidance (C7) is part of the product. Run 2 spends nothing online.
- **The objective, restated:** scan a room and get a generally faithful space, then stylise it into a charming, playable one. Exact replicas of real objects are out of scope. Objects come from the room scan, never from separate photos of each object. They are faithful in kind, size, place and broad colour, and stylised in detail.
- **A toolchain with no strings attached, usable by anyone.** Blender recipes come first (one script per kind of object), and TripoSR (MIT) handles shapes a recipe cannot. Stability Community License tools are out. The first recipes: a cardboard box, a couch with wooden legs, a gaming laptop, an empty Bonne Maman jam jar and a metal French press.
- **Run 2 builds the garage's shell and five of its objects as stand-ins,** pulled forward from Run 3, and **starts the journal's data work.** See [the Run 2 plan](runs/RUN-2.md).
- **The Look direction** is the founder's Google Doc `Art inspiration/Look and Art Style direction`: details make a scene, consistency within a theme, colour contrast as focus, palettes by season, and tight tilt-shift for an observe view.

## Pointers

- [Capture-to-Godot pipeline design](pipeline/ROOM-CAPTURE-PIPELINE.md): the MCP/skill, method recommendations, and the garage test case.
- [Cleanup plan](CLEANUP-PLAN.md): what is deleted, kept, or generalized, and the questions that gate deletion.
- [Held for later: how good ideas spread, and how agents meet safely](ideas/SHARED-IMPROVEMENT.md): the founder's notes of 9 October on verifying fun at scale and on agents meeting with proof of ownership and trust. Not a plan.
- [Sharing the game](ideas/SHARING-THE-GAME.md): the design answer of 19 November to the founder's question on how others take the game, make it their own with their own AI, and share what they make safely in one place. A proposal, not a plan.
- Historical: [earlier world vision](history/WORLD-VISION.md), [earlier roadmap](history/ROADMAP.md), [earlier backlog](history/BACKLOG.md).
