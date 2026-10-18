# EnFractal direction: planetoids, primitives, one material, a teammate

**Proposal, 18 October 2026. Not approved.** Drafted from the founder's design conversation of 15 to 18 October. When the founder approves it, this page supersedes [ROOM-SCALE-DIRECTION.md](ROOM-SCALE-DIRECTION.md) and [ROOM-TO-LANDSCAPE.md](ROOM-TO-LANDSCAPE.md), which get superseded banners in the same way the geography era did. Until then, nothing here changes code, contracts or branches.

## The game in a paragraph

A few blocks of matter float in the void. Gravity points down. You and your AI are two bodies on top, with the same body, the same physics and the same consequences. The world is made of six primitive shapes and one material, counted by volume. Dig to earn matter, place to spend it, demolish to get it back, fall off the edge and lose what you carry. Nothing is undone; mistakes are fixed by playing. Either of you can propose a ghost of a building and either of you can build it. The AI keeps a short notebook about how you like to play, which you can read and edit. Later, rules written as data let the two of you invent games on the same planet without breaking it.

The thesis this tests: **people want to play with their AI as a teammate**, embodied beside them, under the same rules. Everything that doesn't test that thesis waits.

## What stays true

- **Two embodied identities,** one shared ruleset, one command path. Every world change is a typed command through the kernel, from the keyboard or from the AI.
- **The world is data; the scene is derived.** A world manifest plus its state fully describe a world.
- **Bring your own AI** through a model-neutral MCP surface. No bundled model, no hosted AI.
- **The security boundary.** The adapter assigns the principal, no request carries an approval, every name and label in the world is data and never an instruction, and the companion surface exposes no files, shell, URLs, credentials or saves. This boundary is what makes it safe to give the AI everything inside the game.
- **Native Godot .NET with C#,** the pinned toolchain, the kernel, the command host, the companion server and its boundary tests, saves, the body controller and the HUD. These are kept and retuned, not rebuilt.
- **Single-player first, multiplayer last,** and the four habits that keep multiplayer possible.
- **Evidence over claims.** Only the founder passes a gate.

## What changes

| Room era | Now |
|---|---|
| A photographed room, reconstructed and stylised | A planetoid of matter in the void. Capture is deferred until the game is proven and returns later as an importer |
| A 0.10 m avatar in a real-scale room | The avatar is the unit. Two blocks tall; everything is ratios |
| Scanned objects, landforms, materials by kind, fish, water | Six primitives, one material by volume, no water |
| A painterly look developed over seven packets | Skins: the renderer enforces cohesion, the artist does not. Neon first, felt second |
| Three cameras with tilt-shift | One elevated fixed camera with four yaw snaps and zoom, plus a snapshot key the AI reads |
| Trust tiers, previews that auto-commit, a spoken "yes", undo, checkpoints, locks | None. Irreversible actions, consequence, "stop", ghost proposals, and a notebook |
| A journal of world facts and a minimap | The world remembers the world. A notebook remembers the team |
| A building kit, structure classes, generators, scenario packs, modes | Blueprints, and later rules as data. One mode |
| Four parallel tracks, lane worktrees, A/B tests | One lane at a time until the ten-minute script passes |

## The world

- **A planet** is a grid of unit blocks of matter, clipped to a convex primitive with a flat top. The first planet is a block, 24 by 24 on top and 12 deep as a starting guess. Other shapes, a cylinder, a hex prism, a stepped pyramid, are the same grid clipped differently and always start the player on flat ground. Thickness is the planet's matter budget.
- **Gravity is global, down.** The +Y invariant in [AGENTS.md](../AGENTS.md) stands. The top face is the world. The sides are cliffs you fall past. Dig all the way through and you fall out of the bottom.
- **The void** replaces water, walls, reefs and currents. Fall past the lowest planet and you respawn at home.
- **An archipelago** is more planets in the same manifest, at the same or different heights, bridged with primitives. Reaching a new planet costs matter, so the archipelago is the progression loop and later the shape of multiplayer. The manifest holds a list of planets from day one, capped by a constant and never procedural. The first playtest ships one planet. The second ships three.
- **Scale.** Contracts stay nominally in metres so nothing there changes. The avatar is two blocks tall and steps up half a block; the fine placement grid is half a block. Jolt is happiest at this scale, so the small-body jitter work ends.

## Matter

- **One material.** Every primitive costs its volume in blocks. There are no material kinds; behaviour comes from tags, later driven by rules.
- **Two states.** Built matter is static and must be supported by the planet or by other built matter. Carried matter is a number on each body, capped, with a stockpile at home to bank it. There are no loose chunk entities.
- **Dig** a block and your carried amount rises. **Place** a primitive and it falls. **Demolish** refunds, at a rate that starts at full and is a tuning knob. **Fall** and your carried amount goes to zero; that matter has left the world for good, which is the only true loss and, over a long game, the pressure that sends you to the next planet.
- **The budget is the economy.** The matter count replaces the parts budget, the body budget, inventory and the AI's spawn limit. The AI spends the same matter you do.

## Primitives

Six to start, two reserved: **box, sphere, cylinder, cone, wedge, capsule**, with pyramid and torus held back until a playtest asks. Positions and sizes are on the half-block grid, rotations are 90° steps, composition is union only, no booleans. Colour is a **palette slot**, never an RGB value, so a thing painted "accent" is magenta in neon and mustard in felt, and rules can refer to slots. Tags are free strings with a small reserved set.

The avatars are built from the same primitives on the existing capsule controller, with procedural lean and bob driven by velocity. No skeleton, no animation assets, no ragdoll. A tumble when airborne off the edge is a later flourish. Because the bodies are compositions, the AI can customise its own body with the tools it builds with, and every skin applies to the bodies for free.

## Skins

Cohesion comes from the renderer. A skin may change how things look and never what they do.

| Shared by every skin | Owned by the skin |
|---|---|
| Geometry, transforms, composition | Material and shader: emissive wire, matte felt, clay, triplanar wood |
| Collision, placement, snapping | Bevel radius, visual only; collision always uses the sharp primitive |
| Rules, tags, state, matter | Lighting rig: none and bloom, or warm key and soft shadows |
| Colour as a palette slot | The slot colours, the panorama, fog, particles, UI font |

- **Neon first,** because it needs no lighting pipeline. In 3D the recipe is wireframe edges, a dim face fill, a grid on the ground, distance fog and restrained bloom; bare glow on black loses depth. Saturated colour is reserved for what is active or interactive, so state is visible as colour.
- **Felt second,** reusing the Forward+ baseline and the material roles already built. The felt figurines survive.
- The style preset contract becomes a skin: palette, bevel, material, light, panorama. Per-asset restyle, handmade geometry treatment and the painterly texture work move to the room-era branch.
- A cheaper look widens the audience: the neon skin should run on integrated graphics, and that is measured, not assumed.

## The camera and the snapshot

One elevated fixed camera, four 90° yaw snaps, zoom, a drop shadow under each body so edges read. No wall collision, no occlusion fading. A free camera is a later option once the snapshot carries the pose.

**The snapshot key** is a query the AI reads: the camera pose and active yaw snap, the player's position and facing, the world point under the cursor, and the visible entities with screen-relative positions. An image is optional, because some clients are text-only. The cursor point is what makes "build it there" resolve to coordinates. One query op, no vision pipeline. Because the player and the AI share one frame, "left of the tower" means the same thing to both.

## Blueprints and ghosts

A **blueprint** is a composition with no position: shapes, sizes, offsets from a bottom-centre anchor, rotations, slots, tags, and its cost. One type, four operations, both players get all four:

- **Capture** built things in the world as a blueprint.
- **Compose** a blueprint directly from data.
- **Propose** a blueprint as a ghost at a position. Ghosts live in the world state, are visible to both, carry who proposed them and the cost, cost nothing, and are capped at two or three live. Either player can nudge, build or dismiss one.
- **Build** a ghost, or a blueprint directly, paying matter.

Blueprints are the only thing a player owns besides matter. They live in the profile and travel with the player. They are gained by capturing anything reachable: your own build, your teammate's, or later a structure seeded on a far planet. No pickups, no items, no inventory screen. Starter buildings ship as blueprints. This replaces the building kit, structure classes, the grounded and enterable rules, and the creation manifest compiler's ceremony.

## Teammate, not automaton

The trust tiers, preview countdowns and spoken "yes" are removed. What replaces protection is consequence, communication and memory.

| | Player | AI |
|---|---|---|
| Every gameplay verb: move, jump, dig, place, demolish, push, propose, build, capture, set goals, later make rules | yes | yes |
| Spend the shared matter, fall, lose what it carries | yes | yes |
| Save, load, quit, settings, skin, keybindings | yes | no: not gameplay |
| Change the game's rules: budgets, physics, what each can see | no | no |
| Act as the other | no | no |
| Reach outside the game: files, shell, web, credentials | n/a | no |

- **Irreversible.** No undo, no checkpoints, no locks. One rolling autosave per world, no manual slots. Fixing a mistake costs play: demolish refunds matter.
- **"Stop" is instant,** in both directions. It is what a teammate owes, not a permission.
- **A ghost is a proposal, not a gate.** Either player may build directly or show a ghost first and ask. The AI decides when to ask from what it knows about the player.
- **The command set shrinks.** Proposed for the contract round: remove `room.undo`, `room.checkpoint`, `protect.lock`, `protect.unlock`, `creation.*`, `style.set` and the journal ops; add dig, place, demolish, `blueprint.capture`, `blueprint.compose`, `blueprint.list`, `ghost.propose`, `ghost.move`, `ghost.dismiss`, `build`, the snapshot query, and `notebook.read` and `notebook.write`. `goal.set`, `goal.stop`, `observe`, `entities.list` and `entity.inspect` stay. Grab, release and push apply to bodies, not to built things.
- Team ownership of built things returns as a team rule when guests and their AIs arrive. It is a multiplayer problem.

## The notebook

The world remembers the world; the AI can query it at any moment. The notebook remembers the team: what worked, what didn't, what the player enjoyed, which strategies held up, how the player wants to be asked.

- One small text document per player profile, shown in the profile menu, written by the AI and editable by the player. Two tools, read and replace.
- A hard size cap, a few thousand characters to start, later a profile setting with a few sizes. The cap forces distillation. With bring-your-own-AI a larger notebook costs the player's own tokens every session.
- It is text: instructions to the AI, never instructions to the game.
- It travels per player, not per world, into every planet and later into multiplayer, and it works with any client, including one with no memory of its own.

This keeps the founder's 7 October decision that the companion writes its own notes, with the facts removed and the judgment kept.

## Rules as data, after v0

The second milestone, not the first. A rule is `when <trigger> if <condition> then <effects>`, where every effect is an existing verb through the one command path. A rule is an entity with an owner, a budget and provenance. Budgets bound firings per second, matter spent, effect duration and chain depth; "stop" halts all rules. Unknown vocabulary is refused with a reason, and the refusal log is the vocabulary's roadmap. A game mode is a saved bundle of blueprints and rules, so one planet hosts many games, and bundles are the shareable unit. The acceptance test for rules: a player and their AI set up hide and seek on the planet in five minutes with no code, and nothing breaks.

## v0 scope and acceptance

**In:** one planet, the void, global gravity, dig, place, demolish, carried matter and the stockpile, six primitives on the grid, palette slots, the neon skin, the fixed camera with snaps and the snapshot key, two bodies of primitives with lean and bob, blueprints and ghosts, the stripped command set, the notebook, one rolling autosave, a share key that exports a still with no location metadata, and a one-step "connect your AI" screen.

**Out until the script passes:** more planets, bridges, rules, the felt skin, any new primitive, pyramid or torus, water, climbing, swimming, creatures, voice, a minimap, modes, multiplayer, capture.

**The ten-minute script.** A fresh player and their AI, no coaching:

1. Launch. One planet in the void, two figures. Connect your AI in one step.
2. Walk to the edge. Snap the camera around. The drop reads.
3. Dig a few blocks. The carried count rises. Bank some at home.
4. Press the snapshot key, point, and say "build a small house there." The AI proposes a ghost and asks. Nudge it. Either of you builds it. Matter is spent.
5. Build a tower by hand. Capture it. The AI builds a second one elsewhere from the blueprint.
6. One of you goes off the edge, or pushes the other off, and loses what they carried.
7. Quit and relaunch. The autosave restored everything. The notebook says something true about the session.
8. Export a picture and send it to someone.

Passing is the founder watching a fresh player finish all eight in ten minutes and want to show someone, plus the founder's eye on the look from the fixed camera. Nothing new enters the plan before that.

## Isolating the two visions

Follow the 6 October pattern:

1. **Tag the current tree** as the room era's final state, `room-era-final`, the way `geography-era-final` was kept.
2. **Move to the branch:** `pipeline/` entirely, the capture, recipe and landscape-generator work and the room corpus; the garage examples with meshes; the landscape look, water and fish code; the per-asset schema; the painterly restyle work.
3. **Keep on main:** the kernel, the command host, the companion server and its tests, saves, the body controller retuned to the new scale, the HUD, the Forward+ baseline and material roles for the felt skin later, and the research.
4. **Contracts, as proposals for the integrator:** a world manifest of planets, blocks, primitives, palette slots and matter; a skin schema replacing the style preset; the command schema changes above. `common.schema.json` stays.
5. **Docs:** superseded banners on ROOM-SCALE-DIRECTION, ROOM-TO-LANDSCAPE, the Run 2 briefs, BUILDING, LIVE-VOICE's tiers, JOURNAL and PERCEPTION. `docs/research/` stays as research.
6. **Process:** one lane at a time until the ten-minute script passes. Short reports. The refusal log starts on day one.

## Open questions for the founder

- A name for the new world. "Planetoid" is used here as a placeholder.
- The six primitives, and whether pyramid or torus belongs in the first six.
- Planet size and avatar proportions for the first playtest; 24 by 24 by 12 and two blocks tall are guesses.
- The demolish refund rate: full, or a small tax as an economy sink.
- The first consumer MCP client to verify end to end, since that is the largest bring-your-own-AI audience.
- A licence for the repository before any build reaches a tester.

## Decisions recorded, 15 to 18 October 2026

- Capture, reconstruction and room-to-landscape are deferred until the game is proven.
- The core is a confined creative space where a player and their AI play many ways; the AI is a teammate under the same rules, not an ability system.
- Simple geometric primitives, six to eight, fewest first; union only; one material by volume.
- Skins over shared primitives; neon first, felt second; the look is settled by the renderer, not by iteration.
- A thick planetoid with a flat start, global gravity, fall off the edge, no water, no heightfield; an archipelago later with a capped planet count.
- One elevated fixed camera with 90° yaw snaps; a snapshot key that orients the AI.
- One mode. Falling costs what you carry. The Gubble shares the player's body, physics and consequences.
- Trust tiers removed. Irreversible actions, no undo, one rolling autosave, no slots.
- Memory is a short, player-editable notebook per profile about the quality of play, not a log of the world.
- Blueprints are the only non-matter thing a player owns; both players propose ghosts and either builds.
