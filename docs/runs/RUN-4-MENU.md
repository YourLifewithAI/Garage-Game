# Run 4: the opening menu, New game and Continue

**Status:** a design for the founder, 23 November 2026, reworked under his charter. Nothing is built. `M/` is `game/scripts/native/Ui/Menu/`, `S/` is `game/scripts/native/Saves/`, `N/` is `game/scripts/native/`, `I/` is `N/Look/Intro/`, `X/` is `pipeline/landscape/export/`. **(beyond)** marks what goes beyond what was asked: provisional, vetted in play. Where a lane, an After, a wave or a decision number here differs from `docs/runs/RUN-4.md`, the brief wins.

The game opens as a naturalist's journal, at a map of this room as the country it has become: pen and wash, lit by this hour's real sun, inked only where this game has explored. Continue opens the last game as it was left. New game starts clean, shelves the old game whole, and sends the eye diving from a grown-up's height to one centimetre. Nothing old loads unless a button says so. No download, no photo, no cost in play.

## What the founder asked

- "Right now the opening menu is.... ugly."
- "Every game I start, retains old saved information that I've asked to be wiped clean unless I ask to start a new game, which isn't currently an option."
- "It's also hard to see that the island is a 3D landscape made from my garage."
- Later: "great cliffs with water only where the land puts it", "a sandy desert" on top.
- His charter: "be expansive, be creative, do research".

## The design

**Why old things came back.** Play opens any save that is there (`M/MainMenu.cs:468-470`). Continue never checks its save exists (`M/MenuModel.cs:226-232`). A hand wipe is undone at the next boot: the old `EnFractal` folder is copied in again once its ledger is gone (`S/UserFolderMove.cs:87-88`).

**One game** is one team in one mode: the folder `teams/<team>/modes/<mode>/` (hearts, stamina, Fractals, every room's save: `S/TeamLayout.cs:56-60`) plus that mode's pockets and hand in `team.json` (`S/TeamFile.cs:62,69`). A mode's rooms share one body (`S/TeamBoot.cs:96-98`), so a smaller unit resets half.

**The title page** is the journal's open book, drawn in code (`N/Ui/JournalPaper.cs:21-87`), in its system fonts (`N/Ui/JournalPanel.cs:123-129`). Its head is a field journal's: date, hour, moon. On the right: **Continue**, two lines, never cut; **New game**; small "Choose" and "Quit".

**The map, on the left,** comes from `x_landscape_source`, which already holds wall heights, openings by kind, and each thing's kind, colour and support (`X/source.py:64-119`); today's card reads less (`M/IslandThumbnail.cs:57-87`).
- **The land.** Walls are a band of cliff in hachures. Each thing is a landform in its scan colour; one by a wall joins the cliff; one resting on another sits on top. A door is a bay, a window a notch. Sand runs to the paper's edge. Water shows only where the manifest holds it.
- **It draws itself (beyond).** First the room's chalk plan: true boxes, wall lines, door arcs. Then the land is inked over it, in two seconds. A held key brings the chalk back.
- **This hour (beyond).** The sun stands where the room's `site` and the solar model put it (`N/Look/RoomSite.cs:45`, `N/Look/LookClock.cs:132`). Cliffs and landforms cast true shadows; each window throws its beam, a gold patch crossing the floor. No declared site, no shadow (`N/Ui/JournalMap.cs:35`). The hour's insects or birds play from the game's code-made loops (`N/Audio/AudioDirector.cs:127`).
- **This game's survey (beyond).** What shows from afar always shows. What must be found stays pencil until explored: the menu reads the save's discovered block (`N/Kernel/CommandHostJournal.cs:575-603`), as it reads a save's difficulty (`M/MenuIslands.cs:134-147`).
- **The hand lens (beyond).** The pointer is a ten-times loupe, with a pin figure at true size and a scale bar in body heights and minutes of walking.

**One rule for every start,** per team, room and mode:
- a save here: Continue, New game;
- no save here, a game of this mode elsewhere: "Bring my game here", New game;
- no game in this mode: New game, nothing asked;
- a rebuilt room (a save only under an older manifest): New game alone: "This room was rebuilt. Your old game is kept."

New game over a game asks once: "Your game from 25 Oct is put away, not deleted." "Go back" has the focus; meanwhile the map's ink fades to pencil. Every regenerated Run 4 room is the last case: old hearts never enter the new world, and the earlier-save offer, which has no button (`N/Kernel/CommandHostRoomState.cs:592`), never hangs.

**The Dive (beyond).** Today the menu is freed at the click (`M/MenuBoot.cs:127-132`), and the intro starts under a black cover, inside the room (`I/RoomIntro.cs:254-260`, `I/IntroCameraPath.cs:73`), on every load (`N/Ui/RoomHud.Intro.cs:13`). Instead the map holds while the room loads, and the first camera key looks straight down, framed as the map: paper becomes land with no cut. On New game the land grows from the room as the eye sinks. It pauses at a grown-up's standing height, the room blurred like a model, then falls to one centimetre: the blur thins (`N/Look/LookDirector.cs:938`) and a chair's flank swells into a cliff. Continue falls in three seconds. Drawing only.

**The Choose page** is today's three rows (`M/MainMenu.cs:120-205`) in the same paper, the rule's buttons where Play was. Each world's card is a pen sketch with a days-away stamp and a name the family types (beyond). Put-away games stand below as closed journals, dated on the spine (beyond). Rooms without shape data (`M/MenuIslands.cs:92`) fold under "Other rooms"; Live becomes one line.

**Putting away.** `S/GameSetAside.cs` (built in MENU-2), run by the menu before any host exists: not a command, never the companion's, like the Creative copy (`M/MenuModel.cs:210-217`). Holding the team's lease alone, it writes a note first (`teams/<team>/put_away.pending`), copies `team.json` into `user://set_aside/<utc>-<team>-<mode>/` and moves the mode folder whole to `modes/<mode>/` inside it: the folder is shaped as the team's own, as the contract fixes it (`contracts/README.md`, "The menu's files"), and the mode folder is not renamed into the folder's root. It hashes the bytes again after the move, empties that mode's pockets and hand (`S/TeamFile.cs:77,109`), writes the ledger last and removes the note. A fault puts things back. A put-away cut short by the process ending is put back by the next start or the next New game, and a room that cannot put it back does not open. The save folder's move from the old name and the migration of the saves from before teams never write into a put-away game's place (`S/TeamBoot.cs` holds each off while it would). It lies outside `teams/`, so no list of teams or saves finds it (`M/MenuTeams.cs:35-39`; `M/SaveList.cs:26-35`; `N/Kernel/CommandHostRoomState.cs:575-586`). A missing vitals file boots a fresh body (`N/Kernel/PlayerVitals.cs:295`).

**Never under a live room.** A leaving room writes on exit (`N/Kernel/CommandHost.cs:339-346`) and remakes its folder unchecked (`game/scripts/creation_authority.gd:1144-1161`). So a room holds `teams/<team>/in_play.lock` open while it is up; a crash frees it. Putting away takes the lease or refuses. After "Back to the menu" (`M/MenuBoot.cs:162-171`) both buttons wait for it.

**Launches.** A bare `--room=` still goes straight in and continues (`M/MenuBoot.cs:46`), for tests. The three room shortcuts gain `-Menu` (`run-room.ps1:11-15`). The Esc menu gets no New game.

## Packets

`game/tests/native/Menu/**` and `game/tests/native/Saves/**` are unlisted in OWNERSHIP.md: each lane needs a grant. A fresh Opus reviewer reads MENU-2 and MENU-3. MENU-7 on are beyond what was asked. **MENU-1 to 5 are wave 1,** so the first thing he plays in Run 4 is New game and Continue on the title page. MENU-1 takes decision 1's recommended answer, which is safe because nothing is deleted. MENU-3 puts the rule's buttons on today's page where Play was, so New game works before the title page lands. MENU-4 is split: the title page there, the Choose page in MENU-15. MENU-7 and MENU-8 draw on the title page, so they follow MENU-4.

| Id | Title | Lane | Paths | After | GPU | Founder's eye | Model | Acceptance |
|---|---|---|---|---|---|---|---|---|
| MENU-1 | Rule and ledger | Integrator | `contracts/menu.schema.json`, examples, validator, `contracts/README.md`; MECHANICS section 3 | none; decision 1's recommended answer | no | no | Opus | A new ledger kind beside the two the schema holds; `validate.py` passes a ledger example, fails a broken one; every file valid before stays valid |
| MENU-2 | Put away; the lease | Track P; integrator wires `RoomWorld.cs` | `S/GameSetAside.cs`, `S/PlayLease.cs`, `S/TeamBoot.cs` | MENU-1 | no | no | Opus | Failing first: a new start finds health 50, the fire out. After: full hearts, lit fire, empty pockets, no room save; the team's rest unchanged; put-away bytes match by SHA-256; nothing brings it back; a held lease refuses; a fault leaves the old game whole |
| MENU-3 | The start rule, on today's page | U | `M/MenuModel.cs`, `M/MenuSaves.cs`, `M/MainMenu.cs` | MENU-2 | no | no | Opus | `MenuTest`: no Continue without its save; the four cases; a second press over a game; buttons wait on a lease; on today's page the rule's buttons stand where Play was |
| MENU-4 | The title page | U | `M/TitleScreen.cs`, `M/MenuTheme.cs`, `M/MenuBoot.cs` | MENU-3; MENU-5 | pictures only | yes | Sonnet | Headless: the title page fits 1280x720; Continue's two lines never trimmed; no "island" on screen; the land's picture on the left. One before and one after picture set on the stand-in, founder not playing |
| MENU-5 | The land's picture | U | `M/LandPicture.cs`, `M/IslandThumbnail.cs`, `M/MenuIslands.cs` | none | no | in MENU-4's pictures | Sonnet | Counted as `game/tests/native/Menu/MenuTest.Boot.cs:88` counts: cliff bands, landforms, cliff-side things, things on tops, bays, notches; no sea; names cleaned (`M/MenuIslands.cs:85`) |
| MENU-6 | Shortcuts, title bar | Integrator | `run-room.ps1`, `LEAD.md`, the shortcuts, `M/MenuBoot.cs:82` | MENU-4 | no | yes | Integrator | Each shortcut opens the title page on its room; a shortcut for the basin once WORLD-8 installs it; preview shortcuts for dawn, noon and lamps-on (R4-6) |
| MENU-7 | Chalk plan, self-drawing page, hand lens | U | `M/PlanLines.cs`, `M/HandLens.cs` | MENU-4 | no | yes | Sonnet | Counted chalk: an outline a box, an arc a door, a mark a window; the land inked over it in 2 s, any key skips; at rest the picture is MENU-5's; the loupe shows the same data at ten times |
| MENU-8 | This hour: light and sound | U | `M/LandLight.cs`, `M/MenuSound.cs` | MENU-4 | no | yes | Sonnet | Pinned instants, and the preview clock's pins move the page too: a wall's shadow tip lands within a pixel of the solar model's; a beam only when the sun faces its window; no site, no shadow; the moon's phase right; loops follow the hour |
| MENU-9 | The survey in pencil and ink | U | `M/SurveyInk.cs` | MENU-3, MENU-5 | no | yes | Sonnet | Discovered cells ink their ground; no save, or an unreadable one, draws pencil and no error; the question previews pencil |
| MENU-10 | The Dive: hand-over | U; integrator wires `RoomWorld.cs` | `M/MenuBoot.cs`, `M/MenuModel.cs` | MENU-4 | no | at MENU-11 | Opus | The map holds until the intro's first frame; a launch names the full or short dive; a failed room still returns to the menu (`M/MenuBoot.cs:56-66`) |
| MENU-11 | The Dive: the fall | L | `I/IntroCameraPath.cs`, `I/IntroTimeline.cs`, `I/RoomIntro.cs` | MENU-10; LOOK-13 | yes | yes | Opus | The first key looks straight down, framed as the map within 2%; it ends in the play camera, as now; short dive 3 s; setting off, none; all put back; no frame slower than today's intro |
| MENU-12 | Sketched cards, typed names, the shelf | U; integrator, a schema id | `M/WorldCard.cs`, `M/WorldNames.cs`, `M/Shelf.cs` | MENU-2, MENU-5, MENU-15 | no | yes | Sonnet | A pen sketch a world; days away from the save's time; a typed name cleaned as display text, shown by the menu only; a dated spine a ledger |
| MENU-13 | The living map | Integrator, the grid; U, the drawing | `X/exporter.py`, `contracts/validate.py`, `M/LandRelief.cs` | WORLD-5; VILLAGE-1, VILLAGE-3; LOOK-5 | no | yes | Opus; Sonnet | A bounded height, biome and water grid, hostile data refused; folk only in found villages, where `FolkDay` puts them; no room loaded |
| MENU-14 | Journal pages: tops, signs, postcards | U; L, the still; integrator, the layout | `N/Ui/JournalPages.cs`, `N/Ui/JournalMap.cs`, `M/TitleScreen.cs` | WORLD-6; VILLAGE-1; the Squint | stills | yes | Sonnet | A top or sign shows once its ground is discovered; names only from a shipped table; stills stay in the game's folder, never in Git; the companion's answers unchanged |
| MENU-15 | The Choose page in paper | U | `M/MainMenu*.cs`, `M/MenuTheme.cs` | MENU-4 | pictures only | yes | Sonnet | Today's three rows in the title page's paper; the rule's buttons where Play was; rooms without shape data under "Other rooms"; Live one line; fits 1280x720; one before and one after picture set |

## Provisional values

- Every word on screen is a placeholder for the family. "World" stands for "island" (`M/MainMenu.cs:103,335`); folder and contract names keep it.
- New game always puts away, never deletes: `set_aside/<yyyyMMddTHHmmssZ>-<team>-<mode>`, kept without limit.
- A title page before the three rows changes MECHANICS section 3. The shortcuts open it. System fonts.
- Title buttons 520 by 120 px at 1280x720; a child's text at size 18 or more.
- Lease wait after "Back to the menu": 5 s.
- The picture: the 400 largest things (`M/IslandThumbnail.cs:17`); a cliff band 4% of its short side; a thing within one band of a wall is cliff.
- Light: form shading always from the page's upper left; only cast shadows and the beam follow the sun.
- The Dive: about 15 s as today (`I/IntroTimeline.cs:29`), or 3 s; the pause at 16 m of land, 1.6 m of room.
- Names: 40 characters; until typed, the room's name without "Landscape". The manifest stays: its hash names the save (`M/MenuIslands.cs:94`).
- The lens reads room centimetres once BODY-1 types the scale. The team card keeps "Stamina 0.4 m" (`M/MainMenu.cs:353-354`) until BODY-6.
- New game keeps the last mode and difficulty.

## Decisions for the founder

1. **What New game puts away.** Recommended: that mode's game: hearts, stamina, Fractals, pockets, every room (today three Challenge saves; Creative's seven stay). You, the Gubble, its expertise, Trust and ranks stay; "New team" is the blank start. He said "wiped clean"; the run puts away and never deletes, because a delete needs his own words, and a put-away game is out of his way all the same. The brief takes this answer (its D1) and builds MENU-1 and MENU-2 in wave 1: another answer changes what the button moves, never his data.

Everything else is built as provisional.

## Beyond what was asked (to vet in play)

- MENU-7: the self-drawing page, the chalk plan, the hand lens.
- MENU-8: this hour's sun, beams and sound on the title page.
- MENU-9: the map as this game's survey; New game as ink turning to pencil.
- MENU-10, MENU-11: the Dive, and three seconds of it on Continue.
- MENU-12: sketched cards, worlds the family names, the shelf.
- MENU-13: the living map: lakes and scree from a height, biome and water grid; folk as specks in found villages; lamps after dark; dawn mist; the market's flag; flora's flower dial.
- MENU-14: journal pages for tops and signs; a postcard from each cairn, the newest on Continue.

## Research notes

- Hachures (Lehmann, 1799): strokes run downhill, heavier where steeper, flat tops blank. https://en.wikipedia.org/wiki/Hachure_map
- Relief inversion: lit from below, hills read as hollows, so mapmakers light from the upper left. A noon sun lights a north-up map from below; only cast shadows follow it. https://isprs-archives.copernicus.org/articles/XLII-4/101/2018/
- Field journals head each page with date, time, place and weather. https://yellowstonenaturalist.com/keeping-a-field-journal/
- From the builders, unsearched here: blur makes a scene read as a model (https://pmc.ncbi.nlm.nih.gov/articles/PMC3088122); a lower eye makes a room read larger; pen-drawn landform maps.

## What was rejected and why

- **From the three first proposals:** a re-dress only (every choice stays on a child's first screen); new choosing pages (built twice); `--new-game` (a put-away with no question); a 3 s write check (it sees no second window); moving the wish file (one a room for all teams, `N/Kernel/CommandHost.cs:256`); deleting; wiping one room (its Fractals come twice, `N/Kernel/CommandHostFractals.cs:17-21`); a sea in the picture; a Bring back button (later).
- **A live 3D backdrop:** a room behind the menu costs its load and the GPU; the Dive does its work.
- **The whole map shaded by the real sun:** noon would invert the relief.
- **A plan the player must earn:** the outline is his own room, which he asked to see.
- **Naming the world at New game:** it stays one click and one question.
- **A promise that folk stand where the map showed:** rain shelters them, and the menu reads no weather.
- **Stamps for tops and villages now:** the save keeps one height a surface (`N/Kernel/CommandHostJournal.cs:86-98`); they wait for cairns.
- **Chalk lines in the 3D world; the Sun Gate's days on the map:** the world page's Squint and sun sweep come first.

## Not verified

- Nothing was built, run or drawn; no menu picture was seen.
- The lines on saves, the lease and the first proposals are the first draft's; three were spot-checked. The rest were read today.
- How long the room loads under the held map, and whether the window paints meanwhile; the Dive's frame cost.
- Whether a straight-down key keeps the map's north: the intro's look-down takes +Z as up (`I/IntroCameraPath.cs:152`).
- Whether a discovered cell tells a top from its foot.
- Whether the real garage's source lists a window: its rooms were not opened. The stand-in's has one (`pipeline/landscape/corpus/generate.py:186-190`).
- Why the window reads "Garage-Game (DEBUG)"; the lease on Linux; an atomic rename here.
- Whether the sound director runs from a menu source: its `Create` is a test seam.
