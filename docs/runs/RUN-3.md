# Run 3: the story run (proposed 19 November 2026)

**Status: proposed on 19 November 2026 and answered by the founder the same day (section 7). The run is approved to start, orchestrated from a session on the founder's laptop ([RUN-3-STATUS.md](RUN-3-STATUS.md) is the handoff). Nothing here is built yet.** The founder asked (19 November): how to make the game as beautiful as possible, with characters that move more realistically, a landscape that looks more realistic and a UI that is intuitive; a plan to take the game from objective-less to story-driven; and how a team of agents would build it. This page is the answer as a run brief. Where section 7 records the founder's word, it wins over the rest of the page; every other choice is the planner's recommendation and can be overruled.

**Read with** [AGENTS.md](../../AGENTS.md), [ORCHESTRATION.md](ORCHESTRATION.md), [OWNERSHIP.md](OWNERSHIP.md), [docs/game/MECHANICS.md](../game/MECHANICS.md) (section 6: quests, the summit), [RUN-2-OPENING.md](RUN-2-OPENING.md), [RUN-2-CHARACTERS.md](RUN-2-CHARACTERS.md) and [docs/look/LOOK-BIBLE.md](../look/LOOK-BIBLE.md).

## 1. What the survey found

Four read-only surveys of the tree at `f7af65f`, checked against the code, plus pictures rendered in the cloud container.

| Area | What is there | What is missing |
|---|---|---|
| Play | 53 command verbs; hearts, stamina, pips, Fractals, knacks, expertise, five abilities, camps, inventory, food, repair, Trust, a shared map, clock, weather, seasons | No quest, no islander, no dialogue, no ending. The summit is in the island's data (`x_summit`) and no game code checks it. The opening is designed and unbuilt |
| Characters | A real skeleton per character, procedural gaits (stride, hop, waddle, float), lean, squash, leg IK, springs, look-at, blinks, climb, swim and carry poses | Every limb is one rigid part: no knees, no elbows. No faces beyond a blink. Leg IK on two of five characters. No gestures. The gaits have never been judged in play |
| Land | A 10 cm terrain grid with finer blocks, 21 land roles, procedural grass with wind, a real clock and seasons, a shore wash, an endless sea | A tree is a cone trunk and five ellipsoids; a rock is one ellipsoid. The land has no textures or normal maps. Outdoors there is no GI and the ambient light is one flat colour; sky reflections, sun scatter and aerial perspective are at zero. Water neither refracts nor reflects. Wind moves only the grass |
| UI | Meters, a ten-slot hotbar, a minimap, a journal drawn as a leather book, an Esc menu, a camp screen, E prompts | No theme file, no font file, no icon art (the hotbar's icons are Unicode characters). About 36 keys and 3 mouse inputs to learn. No gamepad binding at all. No tutorial and no objective on screen. The H help is a scrolling list |
| Sound | A chime made in code (the camp screen, Glow) | No audio file in the repository. No music, no footsteps, no ambience |

Two findings about how the work has been done matter as much as the gaps:

- **The game is built far more than it is played.** MECHANICS.md holds 135 numbered decisions. The record holds two plays, both the founder's, the second 4.7 minutes long, and none by the children. Every HUD packet of 25 October ends with a "Not seen" list (the bar's look, the padlock, the icons): three rounds of HUD were built by agents that never saw them, and judged by 1,221 checks that cannot tell clear from confusing.
- **Cloud agents are not blind.** The game's own HUD capture scene runs in a virtual display with Mesa's software OpenGL, with nothing installed: `tools/linux/capture-hud.sh` (new with this page) wrote three 1280x720 pictures of the test room in 83 s. It is good for layout, words and icons. It is not the look: software OpenGL means the Compatibility renderer, with no GI, depth of field or post effect. One island picture took 9 minutes. The real Forward+ renderer does start on a software Vulkan driver in the cloud, but too slowly to work with (section 7, item 6).

## 2. What "beautiful" should mean here

The look bible says "never photorealistic", and the founder's direction is storybook with Tiny Glade's warmth. The children ask for "more realistic". The recommendation is to read that as **more believable, not more photographic**: bodies with weight and bending limbs, trees that look like trees, light that behaves like light, and a world that moves and sounds alive. Breath of the Wild and Studio Ghibli are painted and believed. Chasing photorealism with generated primitives would lose the charm and still fall short.

That reading is a guess until the children point at pictures (question 1). In order of effect for the cost:

1. **Light and air.** Sky-driven ambient light and sky reflections in place of the flat colour, sun scatter and aerial perspective off zero, cloud shadows drifting over the land, a dawn and dusk haze. These are settings and small shader work, and they change every frame of the game.
2. **Trees, rocks and plants that are made, not assembled.** A plant kit with branching trunks, leaf clusters under the existing painterly foliage shader, several kinds with variants and distance levels, rocks with real silhouettes, and wind in the canopies. This is the largest single tell today.
3. **The ground at 10 cm.** The eye is less than 10 cm off the ground, so the ground is most of the view: detail textures and normals for rock, soil, sand and moss (generated, never photos of real places), pebbles, twigs and leaf litter near the camera.
4. **Water** that refracts, tints with depth and catches the sky.
5. **Sound** (section 4, Sound lane). No picture fixes a silent world.

The frame budget is already spent: the open meadow with the new grass measures 16.8 ms p95 against 16.7 ms on the RTX 2070 SUPER, and RUN-2-TEN-TIMES.md sets a second, stricter budget (question 3). So a performance packet comes first and pays for the rest.

**Characters.** Keep the motion procedural: there is no animator, the bodies are children's drawings of any shape, and agents can measure procedural motion (foot slide, intrusion) where they cannot judge keyframes. The founder's own decision of 13 October already names the next step, "bendy limbs later", with weights from the converter's tube centrelines. Then faces (pupils, brows, a mouth, driven by a small set of moods), a gesture layer (wave, point, cheer, shiver, startle, sit), foot planting for every legged character, and body language that carries the meters (a tired walk at low stamina, a wince at a lost heart).

**"More realistic physics" for the bodies (the founder, 19 November).** What it means here, in build order: limbs that bend; momentum and weight (speeding up, slowing down, a turning arc at a run, a landing that costs more the farther the fall); feet that plant and never slide; springs on every soft part, not only hats and hair; a carried thing's weight in the posture and the pace, and a lean into a push; and a second try at a jointed tumble once limbs have two segments (the physical bones of 13 October failed their test run and a rigid hull replaced them, so the jointed tumble ships only if it passes the tumble suite). One thing stays fixed: every character shares one collision body, as the traits design decides.

## 3. The story

**Story is island data, as MECHANICS.md section 6 already decides:** quests come from the island, never from the Gubble; they point to Fractals already in the island's budget or give unique things, and never mint a Fractal; no XP, no levels; the journal shows descriptions, never numbers; the AI never awards, announces or hints at a trigger. It must play with no AI connected, as both recorded plays did.

**The machinery (proposed).**

- **A story pack**, a new contract beside the rules pack: `game/stories/<id>/v<N>.json`, bytes pinned. Chapters hold quests; quests hold steps. A step has a journal line, a condition, an optional lead on the map and optional beats (a card, the chime, an islander's line, a camera moment).
- **Conditions are a closed list** over facts the kernel already holds: a place reached, an islander talked to or given a thing, an expertise learned, a campfire lit, Fractals spent, the summit reached, another quest done. No scripting and no free expressions, so every pack is validated and every step is testable headless.
- **A quest ledger** in the kernel listens to `CommandHost.Handled` and the existing events, and saves its state in the island save. The companion reads quest state through the journal as the player does and cannot advance or write it.
- **Islanders:** characters standing in the world with a home, a look-at, a few gestures and curated lines in a speech bubble. Talking and giving are player-only commands. Every line is shipped text, written for an 11-year-old.
- **On screen:** one objective line under the minimap, a Quests page in the journal, a lead on the map.

**The spine (a first draft for the founder and the children to rewrite).** The islanders are the children's own characters: whichever of Cloudpuff, Potato Man, Stickbear and Tomato Man the player is not wearing lives on the island. Their rigs and gaits exist, so they cost little, and the children can write what each one wants.

| Chapter | What happens | What it teaches |
|---|---|---|
| The opening | RUN-2-OPENING.md as written: the desk, the room becoming the island, waking by the fire, the Gubble out of the fire, the rain, the relight, "The Gubble has learned Fire. What will you do with it?" | Who you are, who the Gubble is |
| The first night | Relight the fire, find the first Fractal, meet the first islander, spend five Fractals at camp | Move, E, asking the Gubble, Fractals, the camp |
| The friends | Each islander needs one thing, and each need is one ability: a withered garden (Grow), a friend across the water (Bubbles), a dark path home (Glow), a cold night and a firepit (Fire). Each gives a keepsake and comes to the camp | One ability and one part of the island each |
| The summit | With what the friends taught, climb the landmark. At the top the overview opens and the island is seen whole: it is the room. A souvenir, the second knack slot, and the choice to stay and build | The ending the design already holds (`x_summit`, decision 12) |

Open in the draft: the garage's summit stands at band 8, beyond what its own Fractals open, so the last chapter must rest on Bubble lift or a built ladder, both already decided as ways up. The quest lane checks that route before any text is written.

### Speaking with the islanders (the founder, 19 November)

The founder: the children should speak to the game, not write; speech to speech with their characters; and through the founder's Claude subscription rather than the API if that can be done.

**What the documentation says** (read 19 November; re-check before building):

- Claude's models take text and images and give text. There is no audio in or out, so speech-to-text and text-to-speech are separate parts. Both can run on the PC, free and offline ([models overview](https://platform.claude.com/docs/en/about-claude/models/overview)).
- Signed in with a plan, `claude -p` and the Agent SDK draw on the subscription's limits ([support article](https://support.claude.com/en/articles/15036540-use-the-claude-agent-sdk-with-your-claude-plan)). The founder's own Claude client playing the companion on the founder's PC is the bring-your-own-AI design this game already has.
- The consumer terms require the account's user to be 18 or older and forbid sharing the account ([consumer terms](https://www.anthropic.com/legal/consumer-terms)); the guidance for products that children use is written for products built on the API ([usage policy](https://www.anthropic.com/legal/aup)). **So children holding a live conversation with Claude through the founder's account is not something those terms clearly cover.** No page read settles a family's own game either way; ask Anthropic before relying on it.
- Claude Code has dictation, not spoken replies; the Claude apps' voice mode is a beta. Channels, a research preview, are the documented way for a local MCP server to push "the player said this" into a running Claude Code session ([channels](https://code.claude.com/docs/en/channels)).

**Proposed, in two steps.**

1. **Voice with no live model (build this first).** Speech-to-text and text-to-speech on the PC, as [LIVE-VOICE.md](../companion/LIVE-VOICE.md)'s Talker layer already plans.
   - *Telling the story:* a child holds a key and tells the game who their character is and what it wants. The game records and transcribes on the PC. The founder, as the Claude user, has an agent turn the transcripts into the story pack's lines and approves every line.
   - *Playing:* a child holds a key and speaks to an islander. The words are matched to that islander's authored topics for the quest step, a closed set, and the islander answers aloud. A line may be a voice from the PC or the child's own recording of it.
   - It plays with no AI connected, quests stay island data, and nothing a child says leaves the PC.
2. **Open conversation (later, and the founder's decision).** A live model behind `companion.say` and the grown-ups' password, as the magic design already holds it, by whichever route Anthropic confirms: an API key under the guidance for products children use, or the family's subscription if that is allowed.

The speech parts must have no strings attached, as the toolchain rule says; the lane checks each candidate's licence before it is added.

## 4. Lanes and packets

One agent per lane at a time, each in its own worktree and branch, editing only its paths. New paths (`game/stories/**`, the quest kernel, the audio folder) get owners in OWNERSHIP.md when the run opens.

| Lane | Packets | Model | Sees its work by |
|---|---|---|---|
| **Integrator** | The story pack contract and its examples; the save's story block; journal kinds; merges; the suite; the status page | The session itself | — |
| **Q, quests** (kernel) | Q1 the ledger, the pack reader and conditions. Q2 islanders as entities, talk and give. Q3 the summit check and the ending's flags | Opus | Headless tests; a quest walk-through scene |
| **T, the tale** (content) | T1 the garage's story pack, chapter by chapter, with a pace check against MECHANICS.md's first-hour targets. The founder and the children edit every line | Opus drafts; the family decides | The journal export |
| **U, the UI** | U1 a theme, two open-licence fonts and a drawn icon set in the journal's leather and paper. U2 fewer keys: one interact key, test keys behind a switch, prompts for everything. U3 gamepad. U4 the objective line, the Quests page, speech bubbles. U5 text size and remapping | Sonnet, Opus if the log shows rework | `capture-hud.sh` before and after, looked at by the agent, in every report |
| **K, characters** | K9 bendy limbs. K10 faces and moods. K11 gestures and the opening's animations (O2). K12 islanders' idle life. K8 as planned | Opus | Motion sheets; the family's blind vote, as for the converters |
| **L, the look** | L-perf first (the 941 ms freeze, the grass's cost, culling). L-air (light and atmosphere). L-water. L-ground | Opus | The founder's GPU, or a cloud renderer if question 6 allows |
| **C, the plant kit** | Trees, rocks and plants as real meshes with levels and wind; better grass and flowers (the founder, 19 November); the generator places them | Opus | The landscape harness, then the look captures |
| **V, voice** | V1 speech-to-text and text-to-speech on the PC behind one interface, push-to-talk. V2 telling the story: record, transcribe, the founder's review. V3 speaking to islanders: topics matched, lines spoken (section 3) | Opus for the boundary, Sonnet for the parts | Headless tests on recorded clips; the founder's and the children's ears |
| **Sound** | An audio bus and volume settings; hooks for steps, water, fire, pickups, the chime, UI; a first set of sounds; music by question 5 | Sonnet | Headless checks that hooks fire; the founder's ears |
| **Reviewer** | One fresh Opus review when a lane finishes a chunk, as ORCHESTRATION.md says | Opus | — |

## 5. Waves and gates

**The founder's ruling (19 November): the children play after the whole update, not after each wave.** So each wave below ends with the founder's own look and listen on the laptop, and the children's recorded plays, with `/playtest` debriefs, come when the update is done. The gates written for the children below become the checks for that final round.

As proposed: each wave is a vertical slice that ends in a build the children play, with the recorder on and a `/playtest` debrief. The next wave's packets come from the debrief. **The gate is the play, not the suite.**

- **Wave 0, see and decide (two or three days).** The founder captures the 10x island as it is today (no picture has been committed since 8 October); the children pick what "realistic" means from reference frames; the children play the current build for fifteen minutes with the recorder on, as the baseline; the founder answers section 7. No building beyond `capture-hud.sh`.
- **Wave 1, the first twenty minutes (about two weeks).** The contract, Q1, the opening's beats (O1) and animations, the first night's quests and one islander, U1 to U4 on the screens that chapter touches, the Sound lane's bus and first sounds, L-perf and L-air. **Gate: a child who has not played completes the first night without coaching.** That is Run 5's exit evidence, pulled forward to where it can still change the design.
- **Wave 2, the friends and the living island (about two weeks).** Q2, the other islanders and their quests, the plant kit, K9 and K10, L-water and L-ground, music. **Gate: the children finish two friends' quests and say which they liked.**
- **Wave 3, the summit and the ending (about two weeks).** Q3, the reveal, credits naming the children as authors, U5, performance, a Windows build. **Gate: the story played start to end, in one sitting or two.**

**Mechanics freeze:** no new verb or rule during the run unless a quest needs it.

The weeks are guesses. They depend on the founder's weekly allowance, on how often the family plays, and on question 6. By the routing log a lane round costs 300k to 700k tokens, so a wave of eight to ten rounds with its reviews is roughly 5 to 7 million tokens, and the run 15 to 25 million. Nothing in the plan spends money.

## 6. How the team runs: what changes

ORCHESTRATION.md's lanes, owned paths, routing log and reviews stay. Proposed changes:

1. **Agents look at what they make.** Every UI report carries before and after pictures from `capture-hud.sh`, and the agent reads them before it reports. A "Not seen" list on a UI packet is a failed packet.
2. **Play is the gate** (section 5). Tests keep the kernel honest; only a child at the keyboard shows whether a screen is clear.
3. **Fewer questions for the founder.** Where a packet meets an open value it takes the recommended one, marks it provisional in the pack, and moves on. Only the look, the story's words, what is fit for a child, and spending wait for the founder. Open points are gathered once a week with pictures, not at the end of every packet.
4. **Three ways to run the lanes,** by need:
   - *Subagents of one integrating session* (the Agent tool, one worktree each): two to four lanes in parallel, merged and tested by the session. The default, and how Wave 1 would run.
   - *A workflow script* for the repeated shape build, independent review, fix round, when the founder asks for one.
   - *Separate cloud sessions,* one per lane and branch, for lanes that outlast one session's context; and a session on the founder's GPU machine for look captures, frame times and the Windows runners.

## 7. The founder's answers (19 November)

1. **"Realistic" means believable, not photographic.** Yes: "Believable doesn't need to mean photographic." The look bible stands.
2. **The story:** the children's characters as the islanders, yes. The children do not write: **they speak to the game**, speech to speech with their characters, through the founder's Claude subscription if that can be done (section 3, "Speaking with the islanders").
3. **The frame budget:** not answered. Provisional: the look bible's 16.7 ms at 1080p on its card, until the founder says otherwise.
4. **Gamepad:** not answered. Provisional: built as a first-class way to play.
5. **Sound:** yes, "to give the game more ambience". Where music comes from is not answered. Provisional: ambience and effects made in code, no music until the founder chooses.
6. **A cloud renderer:** yes, agents may try the software Vulkan driver. Tried the same day: Forward+ starts on it (`Vulkan 1.4.318 - Forward+ - Using Device #0: Unknown - llvmpipe`, VoxelGI baked in 368 ms), but the test room had written no frame after five minutes when the founder stopped the run, where software OpenGL takes 69 s. It is too slow to work with. The founder: do the work on the laptop.
7. **How to run it:** the founder wants to see what the planner can do as the orchestrator, and asked for the work to move to the laptop.

**The founder on the seven improvements:** the children's playtests come after the big update (section 5); light and air, yes; real trees and rocks, "something I've been wanting to see", with better grass and flowers too; better, more realistic physics for the character models (section 2); the UI's simplification is to be worked through together (below); sound, yes.

### The controls, proposed for the founder to mark up

Today a player meets about 36 keys. The proposal is nine things to know, and the rest found on screen:

| To do | Today | Proposed |
|---|---|---|
| Move, jump, run | WASD, Space, Shift | The same |
| Look | Hold the left button and drag | Move the mouse; the right stick on a gamepad |
| Act on what you face | E use, F pick up, V push, Z rest | **E** for all of it, with the prompt saying what E will do; hold E for the other choice |
| The Gubble | Right-click, Q come, X stop, I its eyes, the wheel | Right-click to point and ask; hold for the wheel, which holds come, stop and its eyes |
| What you carry and cast | 1 to 0, Tab | The same, and the mouse wheel |
| Views | F1, F2, O | The mouse wheel past its ends: all the way in is first person, all the way out is the overview |
| Everything else | H help, C settings, M magic, J journal, Esc | **One book:** J opens the journal with tabs for quests, the map, magic, the character, settings and help; Esc pauses |
| Dive, recover, home | Ctrl, R, B | Dive stays; recover and home move into the book |
| Test keys | G, T, L, P, F6 to F9 | Off unless a switch in settings turns them on |

A prompt shows the right key or button for the device last touched, and a first-time hint retires after it has been followed three times.

## 8. Not verified

- The look as it is today: the cloud pictures are the Compatibility renderer, and the newest committed look captures are from 8 October, before the island, the 10x world, the characters and the new grass.
- What the children mean by "realistic", and that they find the UI confusing where the founder does: neither is recorded in the repository.
- Whether the family's subscription may carry a child's live conversation with a character (section 3): no page read settles it.
- The token and calendar estimates, which rest on the routing log's past rounds.
- `tools/linux/test-all.sh` was not run for this page: it adds one script and docs, and changes no game code.
