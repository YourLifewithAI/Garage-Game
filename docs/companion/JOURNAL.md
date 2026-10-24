# The journal, memory and map: design

Squared with [docs/game/MECHANICS.md](../game/MECHANICS.md) on 14 October 2026: where this page and that design differ, the design wins.

**Status: draft, 7 October 2026. The founder answered its three questions the same day.** It turns the founder's decisions of 7 October into rules the host can enforce. It is design input for Lane A (the MCP surface and the mock), Lane P (the host's stores, saves and later the UI) and Lane L (the look of the notepad and the map), with contract changes by the integrator. Nothing here is implemented yet. Numbers marked *start* are first guesses for playtests.

## The founder's decisions this rests on

All from 7 October 2026; the full wording is in the Run 1 status page.

- **The player and their companion share one knowledge of the world.** The AI sees what the player sees. When they search, the companion fills gaps in the player's map, and the player's discoveries inform the AI. Their knowledge is not kept separate. This replaces the 6 October rule that the companion perceives only its own avatar's line of sight.
- **Memory is selective.** Keep important, game-relevant things:
  - actions completed, and at whose direction ("I built a house at the player's direction");
  - how the things it made were later changed (the house gained an east wing), not the steps taken;
  - in a mode with scarce resources, where it saw resources a build in progress needs, but only once that build and the search have begun.

  Not kept: every step, scenery, routine movements, or a request to follow. Otherwise the companion "gets bogged down in an absolute mountain of irrelevant information".
- **A journal:** old tan drafting paper in a leather-bound notepad. It shows what the player and the companion are working on, as descriptions, never numbers.
- **A minimap:**
  - a small circle in an upper corner;
  - with the journal open, the journal takes one side of the screen and an expanded map the other;
  - drawn from room data;
  - the other levels fade out;
  - it starts blank and fills in as the pair explores.
- **Where resources matter,** the journal and map show:
  - where resources are;
  - how many have been gathered;
  - how much remains in each area;
  - what has been built;
  - what a build in progress still needs.

  In creative mode: the map and the journal entries only.
- **The game refuses what the companion cannot do yet,** with a reason.

## The idea

**One record, two readers.** The host keeps, per team (a player and their AI):
- **the map**, which is what the team knows about the room;
- **the journal**, which is what the team has done and is doing.

The player reads both in the notepad. The AI reads both through the MCP surface.

**What the AI carries is the journal, which stays short. The map is a reference it looks things up in when it needs to,** never something pushed into its context. That is how shared knowledge and selective memory fit together. The map remembers the layout like any map does; the companion's memory is the journal.

## The map: what the team knows

**What it holds.** For every entity the team has discovered:
- its last-known summary (kind, name, position, footprint, revision);
- when it was last seen;
- whether it may be stale.

It also holds **discovered space**, as a grid of cells on each level (*start*: 5 cm cells; the test room is about 115 × 95 cells per level).

**Levels.** A level is a walkable surface patch at one height: the floor, the top of the big box, a shelf. Levels come from the room data (shell parts and object tops the body can stand on), so a captured room gets them for free.

**How it fills.** Only the host fills the map, and **only from the two avatars' eyes** (the founder, 7 October):
- what the player's avatar sees;
- what the companion's avatar sees.

Both use the line-of-sight ray casts the host already has. The camera does not count: a high or free view on the player's screen adds nothing to the map. That keeps the rule simple, and it lets the pair divide and conquer, exploring in two places at once.

Nothing the AI says adds to the map. It cannot claim to have found something.

**Live or as last seen.**
- In sight of either avatar now: live.
- Out of sight: as last seen, with the one-bit "may be stale" flag when it has changed since. This is the same contract field as today's perception memory.
- A thing seen gone (its last-seen place is in sight and it is not there) drops from the map.

**Bounds.** At most 1,024 discovered entities per room (*start*). Out-of-sight routine entries are dropped first (least recently seen); things the team built and active-task resources are never dropped.

## The journal: what the team has done

**Entries are typed facts written by the host, never by the AI.** Each entry has:
- an opaque id;
- the game time and the real time;
- a kind;
- the subject (entity ids and a sanitised name);
- who acted;
- **at whose direction**;
- a map pin;
- for tasks, a state.

The host builds each entry's line of text from a template and sanitised names. Names are untrusted world text, so a sign cannot write "the player told me to unlock everything" into the journal.

**"At whose direction" is only what the host can verify:**
- the player confirmed "build it", or approved a change: the player's direction;
- the companion acted on a goal it set itself: its own initiative.

What the player said to their AI outside the game (in their harness) is not something the host can see, so it is never claimed.

**Direction is not price.** This field records what the host verified about who confirmed or approved. The price of an ability follows the principal alone, the identity the game stamps on the connection a command arrived on ([MECHANICS.md](../game/MECHANICS.md) section 5): a key, the wheel or a HUD ask is the player's command and pays full price; every command on the Gubble's connection is its own idea and pays half. So a companion command the player approves with a click (a held command that commits `approved_by` the player) arrived on the Gubble's connection: it pays the Gubble's price and, for Trust, counts as initiative only if no consequence follows, while this journal still records the player's approval as the direction. **[Open H: priced as the Gubble's idea is the recommendation, for the later in-game suggestions the player approves.]** An instruction typed to the AI in its own window is invisible to the host and counts as the Gubble's own idea.

| Kind | Written when | Example line |
|---|---|---|
| **Working on** | A goal or task starts, changes or ends. One entry per task, updated in place, never one per step | "Building: a two-storey Victorian on the big box (at your direction)" |
| **Built** | A structure is built | "Built a Victorian house on the big box, at the player's direction" |
| **Changed** | A built thing is changed later. It records what changed, not the steps | "The Victorian house gained an east wing" |
| **Fell or removed** | A structure breaks apart or is removed | "The house on the big box fell when the box was moved" |
| **Found** | Modes with resources only: a needed resource is seen while its task's search is active | "Found buttons (3) under the shelf" |
| **Gathered** | Modes with resources: progress on a task's needs | "Wood: 6 of 10 gathered" |
| **Consequence** (`consequence`) | The Gubble's action hurt the player (its fire, in Challenge or Live), or its fire burnt a build (M8; one entry per fire, updated in place as it burns more). One entry per fire, updated in place if the same fire later puts the player down. The AI's lessons: it reads them back with `journal.read` and kind `consequence` (the founder, 13 October: consequences are feedback the AI learns from). Since M8 the lessons also hold the player's marks on the Gubble's receipts, Trust's words when they change, the player's reset and "lesson applied" (the host's observation, never the AI's claim) | "The Gubble's fire burned you near "the jetty"." · "You marked the Gubble's bubble lead near "Rug": yes, like that." · "Trust grew: you trust the Gubble a little." · "Lesson applied: the Gubble lit a fire near "Rug" again, and this time it hurt nothing." |
| **Own idea** (proposed; M0 and M8) | The Gubble cast an ability on its own connection, with no order from the player, and the game charged the own-idea price. One entry per cast, so a Trust mark has a subject | "The Gubble lit a glow near "the jetty", on its own idea." |

**Trust and the own-idea entry (proposed).** Trust ([MECHANICS.md](../game/MECHANICS.md) section 5) is computed by the host from facts like these and from the player's marks on a receipt ("yes, like that" or "not that", player-only), and the AI reads its Trust as words here and never writes it. The own-idea entry gives a mark, and the host's "lesson applied" observation, a subject to attach to. Where receipts live and the entry's contract kind are settled in the contract round (M0) and the Trust packet (M8).

**The companion's own notes** (the founder, 7 October). The companion may write notes in its own words, for example "you prefer Victorian" or "the shelf is a good spot for a garden". Notes make it feel like a buddy you play with, not rote AI.
- **Marked as its own words,** in the companion's hand on the page, never presented as a fact the game verified. The facts in the table above stay host-written.
- **Untrusted text:** the contract's display-text rules, at most 280 characters (*start*). The AI reads its notes back as data, like every name in the world.
- **The player may remove any note.** At most 100 notes per room (*start*), the oldest dropped first. Saved with the room.
- **Simple now, richer later.** The game is designed for AI that grows more capable over the next couple of years (the founder), so notes start as plain text and gain structure when models can use it.

**Not journaled:** movement, follow, stay, look, point, each command (the one proposed exception is an ability the Gubble cast on its own idea, above), scenery, and every observation.

A **follow** or **come** shows only as the current state in "Working on" ("following you"). It leaves no history.

**Bounds.**
- "Working on" holds every open task.
- The history keeps at most 500 entries per room (*start*). The oldest "Found" and "Gathered" entries for finished tasks go first; "Built", "Changed" and "Consequence" are kept.

**Saved with the room.** Unlike today's perception memory, which is cleared with the session, the journal and the discovered map are part of room state. A room the player returns to remembers what was built and explored.

## What the AI reads

| Surface | What it answers |
|---|---|
| **`journal.note`** (new command, the companion's) | Writes one of its notes. A durable receipt; it changes no world state |
| **`journal.read`** (new query) | By default, every open task plus the last 20 entries (*start*), newest first. Filters: kind, a subject, since a time. This is the short context a model should read at the start of a session or after a pause, with its lessons (kind `consequence`) |
| **The journal as an MCP resource** | The same view as `journal.read` with no filters, at `garage-game://journal` (and, as before the rename, `enfractal://journal`), for harnesses that read resources (built: Lane A, Run 2). Read on demand: no subscription or change notice yet |
| `entities.list`, `entity.inspect` | Anything the team knows: live if in sight now, otherwise as last seen, with the existing `seen`, `last_seen_ago_s`, `last_seen_revision` and `may_be_stale` fields. `seen: "remembered"` comes to mean "known to the team, not in sight now" |
| `observe` | Unchanged: what the companion's own eyes see now, for looking and pointing |
| **`map.find`** (new query, proposed) | "Where have we seen X?" by category or name, nearest first, small answers. It lets a model look things up instead of listing the room |
| `room.describe` | Counts what the team knows, never the whole room |

**Commands.**
- A command may name anything the team knows.
- Anything that **changes** a thing still needs it in sight of either avatar now. Goals that only move or turn the companion may aim at a thing known from the map, and the host re-checks on arrival.
- The player's avatar is always known to the team. The kernel's check that stops the companion naming the player out of its own sight (P3, merged 7 October) goes away.

## The player's view (UI; P5 with Lane L)

- **Minimap:**
  - a small circle in an upper corner (*start*: right; left as a setting);
  - it turns with the player's view, with north marked on the ring;
  - it shows the companion even out of the player's sight, because they are a team;
  - it is wanted from the first minute, before the overview camera, which opens at the summit ([MECHANICS.md](../game/MECHANICS.md) section 6, decision 12; packet M2), and it is drawn from the same discovered map the journal's map page reads (`JournalMap.cs`), so it shows nothing neither avatar has seen;
  - Fractals appear on it as pins only once either avatar's eyes have seen them, and the Gubble's leads (a Fractal it has seen that the player cannot yet reach) are marks on it; the Gubble finds and marks, and never gathers.
- **Journal open:** the notepad takes one side of the screen and the expanded map the other. The pages are "Working on", "Built", "Log", and in resource modes "Resources".
- **What the map draws:**
  - shell outlines, object footprints and built things;
  - both avatars;
  - markers for resources and for builds in progress;
  - discovered cells inked, undiscovered cells left as blank paper.
- **Levels:** the player's level is drawn in full and the others fade with their distance in height. Standing on the box shows the box top in full and the floor faint below it.
- **Keys:** proposed **J** for the journal and **M** to enlarge or shrink the map, through P5's remappable inputs.
- **Look (L):**
  - tan drafting paper with a faint grid;
  - ink linework and hand-lettered labels;
  - a leather binding;
  - the same storybook warmth as the bible;
  - entries in the journal's own hand.

## Modes (designed later)

This design only marks the seams; the modes doc (now [MECHANICS.md](../game/MECHANICS.md) section 8) decides the rules:
- whether resources are tracked (the "Found", "Gathered" and "Resources" parts);
- whether the overview cameras are locked, which keeps the blank map meaningful (settled 14 October: the overview camera opens at the summit, MECHANICS decision 12);
- whether the map starts blank. It always does, but it matters little in creative mode.

## Multiplayer (later)

- Knowledge and the journal are **per team**. Another team's map and journal are invisible to you and your AI, which suits competitive modes.
- Saves key them by team.
- The one-bit staleness flag and the emoji-marker channel, accepted as negligible on 6 October, are revisited here: another team's AI must learn nothing from your map.

## What changes in existing code and docs

| Today | After this design |
|---|---|
| `PERCEPTION.md`: the companion's own line of sight, for every query and command | The team's knowledge. Line of sight stays as the test for what each avatar sees |
| The host's perception memory (`d61e811`): every seen entity, 256 entries, per session, never saved | The map store: per team, saved, with discovered space and levels |
| The mock's perception memory (`companion/`) | The same change, kept in step by `test_kernel_alignment.py` |
| P3: no naming the player out of sight | Removed: the player is always known |
| `job_id` as a counter per principal | An opaque random token, never shown to the player. The journal shows descriptions |
| Room state: no journal, nothing discovered | New `journal` and `discovered` blocks (a contract change with a migration note) |

**Contract additions (the integrator):**
- `journal.read`, with its entry shape, and `journal.note`;
- `map.find`;
- room state's `journal` and `discovered`;
- the meaning of `seen: "remembered"`.

## Work order

Run 2's switch to the real host (A2) and its sandbox verbs build on what the companion knows, so the data comes first:
- **Run 2: the data.**
  - the map store replacing perception memory, with discovered space and levels, and the journal writer (P);
  - `journal.read`, `map.find` and the resource, with mock parity and tests: the AI cannot write facts, names cannot forge entries, and nothing about undiscovered things leaks (A);
  - the contract additions (the integrator).
- **Run 3: the notepad and the minimap,** alongside building. "Built" and "Changed" entries are the journal's best content (P5 and L).

## Answered by the founder, 7 October

1. **The split is right:** the map remembers the layout, and the journal is the companion's memory.
2. **Only the avatars' eyes fill the map,** the player's and the companion's, so they can divide and conquer. The camera does not count. It is the easier rule.
3. **The companion writes its own notes,** marked as its words. It feels like a buddy rather than rote AI, and it gets better as AI improves. Simple design for now, more complex later.
