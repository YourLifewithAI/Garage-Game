# Sharing the game: taking it, making it your own, and one safe place to share

**Status:** a design proposal on record, not a plan. Nothing here is built or scheduled. Every recommendation is the integrator's and provisional; the decisions in section 9 are the founder's. Written 19 November 2026 by the Run 3 integrating session, on the founder's word, from a read-only survey of the repository at `1407a77` (`run3/integration`). Facts name the file they come from; section 11 lists what was not checked.

## 1. The question

The founder, 18 November 2026, in the Run 3 session:

> "we need to consider how we can make this game transferable and shareable. As in, how can other gamers take this game, copy it, and make it their own using their own AI, and how do we centralize a way for everyone to safely share the experiences they create?"

That is three questions: how someone **takes** the game, how they **make it their own** with their own AI, and how everyone **shares** what they made, safely, in one place.

## 2. In short

- **The design already suits sharing.** A world is data listed by hash, the loaders refuse anything they do not know, no content can carry code, world text reaches an AI as quoted data, and the companion needs no key or account.
- **Nothing a player could use exists yet.** There is no licence, no player build with the companion in it, no "Connect your AI" screen, no export or import, and no place to share.
- **The first blocker is not technical.** The repository is public and has no licence, so people can read it and nobody has permission to copy, change or pass it on.
- **Privacy is the hard part of sharing rooms.** An island made from a real room is a derived form of that room. Stripping the records helps; it does not make the island anonymous.
- **Recommended shape:** a licence; a Windows build that carries the companion; one "experience package" of data only; an export gate and an import gate; a curated gallery that starts as a GitHub repository; nothing for children to sign up to, type into or be seen in.

## 3. What the founder has already decided that bears on this

- **Bring your own AI** (6 October): no model is bundled and no hosted AI is provided; the surface is model-neutral ([ROOM-SCALE-DIRECTION.md](../ROOM-SCALE-DIRECTION.md), "Founder decisions").
- **The player's own AI makes the game's objects, and no hosted or paid service is ever required** (7 October), "so the capture guidance is part of the product".
- **"A toolchain with no strings attached, usable by anyone"** (7 October): Blender recipes and permissively licensed models; tools under restrictive licences are out.
- **Multiplayer is a real later goal** (6 October): "players and businesses scanning spaces, rebuilding them with their AI to the game's specs, and sharing them for individual or shared play." Transport, accounts, hosted services, consent and moderation are deferred to that phase.
- **The shared sea and the ferryman** (10 October, [ROOM-TO-LANDSCAPE.md](../ROOM-TO-LANDSCAPE.md)): distant islands may stand for other players' islands, and from the jetty a ferryman takes the player to a chosen one.
- **How good ideas spread, and how agents meet safely** (9 October, [SHARED-IMPROVEMENT.md](SHARED-IMPROVEMENT.md)): proof of ownership, trust earned over time, shared things as data and never instructions, and parents deciding what children's islands share.
- **The children's drawings are committed** (13 October), "knowing this repository is public" (`assets/characters/README.md`).
- **The room manifest already carries the owner's privacy statements** (`contracts/room-manifest.schema.json`, `source.capture.privacy`: `location_metadata_removed`, `people_visible`, `shareable`): "Sharing later depends on these being true statements." The capture pipeline writes `shareable: false` until the owner opts in. Nothing reads the field yet.

This page proposes the first sharing that needs none of the deferred multiplayer work: content moves between players as files; nobody is in anyone else's world.

## 4. What exists today

| Piece | State | Where |
|---|---|---|
| A room or island | A folder whose manifest lists every file with its SHA-256; the game checks them again at load and refuses unknown fields and paths | `contracts/room-manifest.schema.json`, `game/scripts/native/Room/RoomData.cs` |
| Finding islands | The menu lists the player's own rooms (`user://rooms`) before the shipped ones (`res://rooms`), so a valid room folder copied there already appears | `game/scripts/native/Ui/Menu/MenuIslands.cs` |
| Characters | A shipped root and a player root (`user://characters`) exist in the library; no in-game flow fills the player root | `game/scripts/native/Characters/Library/CharacterLibrary.cs` |
| Rules packs, style presets | Load only from inside the game's own files (`res://rules`, `res://styles`) | `Kernel/IslandRules.cs`, `Look/StylePreset.cs` |
| Story packs | The contract and its validator are merged; the game does not read a pack yet (Run 3, Q1) | `contracts/island-story.schema.json`, `game/stories/example/v1.json` |
| The companion | A standard MCP server on the player's own machine. It needs no keys or tokens and clears its environment at start. It prints its configuration for six clients; Codex CLI and the Claude CLI have been run with it, the other four formats come from documentation. Setup is a command line and a Python environment | `docs/companion/README.md`, `LEAD.md` |
| "Connect your AI" in the game | Proposed, not built | `docs/codex/reports/04-byoai-connect-survey.md`, "Proposal" |
| A build for players | One export preset, an unsigned development build. Its notices say the companion connection and room persistence are not included; that file predates the island and was not rechecked | `game/export_presets.cfg`, `game/RELEASE-CREDITS.md` |
| A room from photos | A developer pipeline: an NVIDIA card (a measured peak of 5.8 GB on the founder's 8 GB card), about 6 GB of model weights, Blender for objects, review files written by hand, all at a command line. The capture's weights are all Apache-2.0 | `pipeline/roomscan/README.md`, `docs/pipeline/COMPUTE-OPTIONS.md` |
| An island from a room | The generator and the exporter are Python with no Blender (no file under `pipeline/landscape/generator` uses it; the exporter is standard library only). The same inputs give the same bytes. The 10x garage is 96 MB | `pipeline/landscape/export/README.md` |
| Sound | Made in code at run time; there are no sample files to license | `docs/audio/README.md` |
| A licence | None. No licence file is tracked, and GitHub reports the repository as public with no licence | repository root |

## 5. What stands in the way

**To take it.** No licence. No installer and no signed build. The build leaves the companion out, and connecting an AI means a Python environment and a configuration file. A few tools still assume the founder's machine (a default Blender folder in `pipeline/landscape/harness/render.py`, a Drive folder in the readiness check).

**To make it your own.** Stories, rules and looks cannot be loaded from the player's folder. Characters have a player root and no flow. A room of one's own needs the capture pipeline, which is a developer's tool. There is no guide for an AI that helps a player author content, as distinct from the AI that plays.

**To share.** No package, no export, no import, no place. And an exported island tells more about a home than it looks like it does (section 7.4).

## 6. Three depths of "making it your own"

Each depth uses the player's own AI differently, and each needs a different safety boundary.

1. **Play with your AI.** The companion as it is: the AI is the Gubble, inside the game's checked commands. The boundary is AGENTS.md's: no files, shell, URLs or credentials, and every name and sign is untrusted data.
2. **Create with your AI.** The AI helps author data the game loads: a story pack, a character from a drawing, a look, a room. This is a *workshop* session, separate from the play session, because it needs files and tools the play session must never have. Its output is only ever data, and it reaches the game through the validator (`contracts/validate.py`) and the fail-closed loaders, like anything else.
3. **Rebuild with your AI.** A fork of the code. The repository is already written for agents (AGENTS.md, the contracts, the ownership map, the runners), so this mostly needs a licence and a README for outsiders.

Most players will stop at 1 or 2. Depth 2 is where "the experiences they create" come from, so it is where the design effort belongs.

## 7. The proposal

### 7.1 Take it

- **A licence** (section 9, decisions 1 to 3), with a notices file for what the game builds on.
- **A Windows build that carries the companion,** so a player installs nothing else. Brief 04's proposal already says how: a bundled, versioned companion runtime, and a **Connect your AI** screen that writes the play-only profile for the player's client and checks the connection.
- **Checksums at least, a signature if the founder approves the cost.** An unsigned build shows Windows' warning to every new player.
- **The other four client formats tried for real,** and the founder-machine paths removed.

### 7.2 Make it your own

- **Packs from the player's folder.** Stories, rules and looks get a player root beside the shipped one, as rooms and characters already have. A pack from the player's folder passes the same checks as a shipped one and is labelled as the player's own in the menu.
- **A character from a drawing, inside the game or one command away.** The converter exists; the flow into `user://characters` does not.
- **A room of one's own, three ways:**
  - *An invented room* (the default for sharing): the player or their AI describes walls and furniture as boxes that match no real place. The island generator already runs from that data alone, with no photos, no graphics card and no Blender. It exposes nothing, because there is nothing real behind it.
  - *A sketch of a real room:* the same path from a measured or remembered room. No photos leave the camera roll, but the sketch is still the room's layout (section 7.4).
  - *From photos:* the capture pipeline, kept as the advanced path, operated by the player's AI from the capture guidance.
- **A creator's kit for the workshop session:** the contracts, the validator, the examples and a short guide (a skill) that tells any capable AI how to author a story pack or a room description and check it. It ships with the game and needs no service.

### 7.3 The experience package

One file that holds one experience. A proposal for a new contract, `garage_game.experience`, which like every contract would change only in an integrator commit with its examples, consumers and tests.

- **Data only.** Its parts are the kinds the game already loads fail-closed: a room, a story pack, a rules pack, a style preset, characters. Never a script, a scene, a shader or a program.
- **A manifest at its root** lists each part by kind, id, version, path, size and SHA-256, plus the game and contract versions it was made with, a title and an author's display name (both untrusted text), a content licence, and the owner's privacy statements.
- **Never in a package:** saves, the journal, playtest records, team files, the companion's session file, voice recordings or transcripts, source photos, or anything from `captures/`.
- **Two sizes, later.** A full package carries the island (about 100 MB at 10x). A *recipe* package could carry only the room description and the generator's version and be rebuilt on the other machine, since the generator is deterministic. Full comes first: it needs no generator on the player's machine.
- **Moving the family's own game** between their own machines (saves and teams) is a different, private export. It never goes to the gallery.

### 7.4 The export gate: what leaves, said plainly

What an island made from a real room holds today:

- `extensions.x_landscape_source`: the source room's bounds, floor and ceiling polygons, walls with heights, openings, and every object's kind, pose, size and colours. It is there for the intro. It carries no labels, names, notes or location records (`pipeline/landscape/export/README.md`).
- `source.capture`: the session id, the photo count, device names, and the first and last capture times.
- Wall and object colours that come from the photos.
- **The land itself.** The island is generated from the room's walls and furniture, so its shape is a derived form of the room. Removing the records above does not undo that.

So the gate does three things:

1. **It strips** `x_landscape_source` (the shared island opens without the room intro, or with a generic one), the device names and the times, and it refuses anything that is not in the package manifest.
2. **It shows** exactly what will leave, in words and a picture, and states what kind of place the island came from: invented, sketched from a real room, or captured from photos.
3. **It asks a grown-up,** behind the grown-ups' gate, to confirm the owner's statements (nobody is visible, it is theirs to share) before it writes the file. It flips `shareable` only then.

Recommended rule for the public gallery: **invented rooms and the shipped stand-in only, at first.** Islands from real rooms pass between people who know each other, as files. This matches AGENTS.md's rule that exported rooms of real places are never committed without the founder's explicit approval.

### 7.5 The import gate: what may come in

- **Check the file before unpacking it:** only the entries its manifest names; no absolute or parent paths; a ceiling on each file, on the total and on the count; every hash.
- **Run every part through its existing loader checks,** then install it under the player's folder in a place named for the package, never over something the player already has.
- **Close two known gaps first.** The survey found no overall size limit on `room.json`, and the extension contract allows values with "no value shape or byte limit". Both need ceilings before files from strangers are read.
- **Say where it came from.** An imported island is marked "from someone else" in the menu, with its author's display name as untrusted text.
- **Treat its words as hostile to an AI.** Someone else's signs, names and story lines will reach the player's AI. The story contract already marks quest text quoted and `untrusted`; the companion already treats world text as data. With shared islands the play-only profile stops being advice: the Connect screen should check for it before an AI joins an imported island.
- **Behind the grown-ups' gate.** A child cannot import by themselves. The gate is the one already designed for live wishes (`docs/magic/README.md`; unbuilt).

### 7.6 One place: the gallery

Start with the smallest thing that is safe, and grow it only if it is outgrown.

1. **Files first.** Export and import work with no central place at all. Two families can share an island on a memory stick. Everything below sits on top of this.
2. **A curated gallery as a public GitHub repository.**
   - An entry arrives as a pull request: a small entry file (title, author's display name, licence, description, sizes, hashes, a thumbnail) with the package attached as a release file. GitHub refuses files over 100 MB inside a repository and an island is about that size, so the repository holds entries, not islands.
   - A check runs the validator and the import gate's checks on every submission.
   - **A person plays it before it is listed.** An AI can screen text and flag things; it does not certify that something is fit for a child (AGENTS.md: an agent score certifies nothing).
   - The gallery publishes **one signed list** of entries with their hashes, and a list of entries withdrawn. The game ships the key, fetches the list only when a grown-up asks it to, sends nothing else, and imports only what the list names. A withdrawn entry is hidden, and flagged if already installed.
   - It costs nothing and needs no server, and the pull request history is a first, honest record of who submitted what.
3. **In the game, the gallery is the sea.** The founder's own picture fits: listed islands stand on the horizon, and the ferryman at the jetty takes the player to one a grown-up has brought in.
4. **A hosted service only if the gallery is outgrown.** Accounts, uploads from inside the game and ratings bring cost, moderation duties and the law on children's data. They belong with the multiplayer phase, where the founder already put them.

**Who may submit, and on what terms.** Adults, by invitation at first. Each submission states that it is the submitter's to share, that it comes from an invented place or one they may share, that it shows no person and holds no personal information, and which licence it carries. There is a public way to report an entry, and withdrawal is one line in the list.

### 7.7 Children

- No accounts, no chat, no comments, no ratings, nothing collected, in the game or the gallery.
- Sharing is one-way and reviewed: a child can visit a listed island; a child cannot publish, message or be found.
- Exporting and importing sit behind the grown-ups' gate.
- No child's name in a package. The export gate warns when a title, author or sign looks like one.
- A child's own creations and characters are the child's work. Whether they are shared is the parents' decision each time, never a default.
- What a child says to the game (Run 3's voice work) never leaves the machine in any package.

## 8. What can go wrong, and what stops it

| Risk | What stops it today | What is still needed |
|---|---|---|
| A shared file runs code | Content is data; no manifest field can name a script, scene or shader; loaders fail closed; hashes | Archive checks before unpacking; size ceilings; a view on how far to trust the engine's own GLB reader with hostile files |
| Shared text steers the player's AI | World text is quoted, untrusted data; the play-only profile has no shell, files or browser | The Connect screen checking the profile; provenance on imported text; screening at the gallery |
| Something unfit for a child | Nothing: no shared content exists | Human review before listing; the grown-ups' gate; listed entries only |
| A home's layout leaks | The pipeline strips location data and writes `shareable: false` | The export gate; invented rooms as the gallery's rule |
| A forged or altered entry | Hashes inside a room | The signed list; the game refusing anything not on it |
| Someone shares what is not theirs | Nothing | The submitter's statement, a content licence, a report path, withdrawal |
| A tampered copy of the game | Nothing: the build is unsigned | Checksums; a signature if approved |
| A pack that changes the rules unfairly | Not a risk alone | For a later competitive mode, MECHANICS.md's sealed ruleset already answers it |

## 9. Decisions for the founder

1. **The code's licence.** *Recommended: Apache-2.0*, the licence of every model the capture pipeline uses, in the spirit of "no strings attached, usable by anyone". MIT is simpler and nearly the same in effect. A copyleft licence would keep forks open. A non-commercial source-available licence would keep commercial rights with the founder and would not be open source.
2. **The licence for the game's original art, shaders aside.** *Recommended: CC BY 4.0.*
3. **The children's characters.** They are in the public repository with no licence. *Recommended:* they stay the family's, all rights reserved, said so in `assets/characters/README.md` and excluded from the repository's licence; the public game ships a neutral cast.
4. **Where the Run 3 story lives.** The islanders are the children's own characters and the tale is the family's. *Recommended:* the family's story is the first experience package, the family's own, and the public game ships the example pack. That also proves the package on the game's own content. This touches Run 3's T1 before it writes a word.
5. **The first sharing place.** *Recommended:* files, then a curated GitHub gallery; no hosted service.
6. **Who may submit.** *Recommended:* by invitation, adults only, the founder reviewing.
7. **The gallery's rule on real rooms.** *Recommended:* invented rooms and the stand-in only.
8. **Spending.** A code-signing certificate and a lawyer's look at the gallery's terms and at children's data both cost money. Neither is needed for sharing by file. Nothing is spent without approval.
9. **The name.** Whether a fork may call itself the Garage Game. *Recommended:* forks choose their own name.

## 10. Order of work

**Can ride along in Run 3, with no new packet:**

- The licence decisions: one commit whenever the founder decides.
- Q1's story reader takes its roots as a list, as the menu's island reader does, with only the shipped root in it for now. A player root later is then one line.
- Lane U's fonts and icons carry licences that allow them to be passed on.
- Batch 4's Windows build: decide whether it carries the companion.

**A sharing run of its own, after the story run:**

| Packet | What | Done when |
|---|---|---|
| S0 | Licences, notices, a README for outsiders, founder-machine paths removed | A stranger can clone, build and run the tests from the README alone |
| S1 | The player build: companion inside, Connect your AI, checksums | Someone who is not the founder installs it and plays with their own AI, unhelped |
| S2 | Packs and characters from the player's folder | A story, a look and a character made outside the game load and play |
| S3 | The experience package, the export gate, the import gate, the two size ceilings | A package made on one machine plays on another; hostile test packages are all refused |
| S4 | The gallery repository, its checks, the signed list, the ferryman | A listed island is reached from the jetty; a withdrawn one disappears |
| S5 | The invented-room path and the creator's kit | A player's AI makes a new island and story from a description, with no photos |

S0 to S3 need no central place and no money. S4 is where the founder's review time becomes the real cost.

## 11. Not checked

- **The law.** Nothing here is legal advice. A public gallery for a children's game needs a lawyer's look at its terms, at takedowns and at children's data before it opens to strangers.
- **The engine's GLB reader against hostile files.** The game requires self-contained GLBs and checks their hashes; whether Godot's importer is safe on a deliberately malformed one was not examined.
- **The island generator's other dependencies** beyond "no Blender", and whether it can be bundled for players.
- **GitHub's file limits,** quoted from memory.
- **Four of the six client formats,** which nobody has run.
- **Whether `game/RELEASE-CREDITS.md` still describes the build.**
- **Nothing was built or run for this page.** The survey was read-only.
