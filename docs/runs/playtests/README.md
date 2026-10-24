# Playtests: the index and the debrief template

Every playtest, the founder's and the kids', recorded by the game ([RUN-2-PLAYTEST-LOOP.md](../RUN-2-PLAYTEST-LOOP.md)) and debriefed by the integrating session with the `/playtest` skill (`.claude/skills/playtest/SKILL.md`). A debrief is text in Git; the record (the events, the stills) stays on the machine that played, under `%APPDATA%\Godot\app_userdata\EnFractal\playtests\<record id>\`, and is never committed: the stills show a real room. The first debrief, the Glow page, was written by hand from recordings before the recorder existed; it is the shape every one after it takes.

## The index

One row per play, newest first. **Changed by it** names the brief or the packets the play's "Change" list became, so a reader can follow a finding to the work; it is filled in when the next brief is written.

| Date | Room, mode | Build | Who played | Record | Debrief | Changed by it |
|---|---|---|---|---|---|---|
| 2026-10-17 | `landscape_garage_nominal`, Challenge standard | `bb9f47f` | the founder | `20261017-133610-landscape_garage_nominal` on DiamondAge (4.7 min, no marks; the first play with Fractals, Fire and Grow) | [2026-10-17-landscape_garage_nominal.md](2026-10-17-landscape_garage_nominal.md) | [RUN-2-PLAYTEST-ROUND-2.md](../RUN-2-PLAYTEST-ROUND-2.md) (queued for 20 October) |
| 2026-10-16 | `landscape_garage_nominal`, Challenge standard | `db41d8a` | the founder | `20261016-044825-landscape_garage_nominal` on DiamondAge (4.8 min, 2 marks; the first record the game wrote itself) | [2026-10-16-landscape_garage_nominal.md](2026-10-16-landscape_garage_nominal.md) | [RUN-2-PLAYTEST-ROUND-1.md](../RUN-2-PLAYTEST-ROUND-1.md) (P0 the pack, P1 the kernel and body, L1 the shore and the hitches, P2 the HUD) |
| 2026-10-09 | `landscape_garage_nominal`, Creative | `ca6656d` | the founder | the founder's recordings in `C:\dev\EnFractal-art\playtests\2026-10-09-glow\` (before the recorder) | [RUN-2-GLOW-PLAYTEST.md](../RUN-2-GLOW-PLAYTEST.md) | the Glow round's Lane P part 1 and Lane L (the free cursor, the spark, readable words, the sea wrapping round) |

## The debrief template

Copy this into `docs/runs/playtests/<yyyy-mm-dd>-<room id>.md` and keep every heading, even when a section is one line. The founder's words stay their words.

```markdown
# Playtest: <room id>, <date> (<mode>, <difficulty>; build `<build>`; record `<record id>`)

**Read with** [RUN-2-PLAYTEST-LOOP.md](../RUN-2-PLAYTEST-LOOP.md), [RUN-2-STATUS.md](../RUN-2-STATUS.md), the brief the play tested (<link>) and [AGENTS.md](../../../AGENTS.md). <Minutes> minutes, <clean quit or not>, <n> marks, <n> refusals (<n> not shown on screen), worst frame window <ms> at <m:ss>, <share> of the time within 2 m of the Gubble.

## What the founder found (<date>, <machine>, "<shortcut or command>" at `<build>`)

- **<The thing, in a few words.>** "<The founder's words.>" (mark <n> at <m:ss>, still `<file>`)
- **Keep:** "<The founder's words.>" (keep <n> at <m:ss>)

## What the record shows

- **The marks:** <each mark in a line: what the 20 seconds before it hold, what the still shows>.
- **The refusals:** <by code; every one not shown on screen named as a silent failure>.
- **The frames:** <the worst window, where the body was, what was happening>.
- **Where the time went:** <body states, heights, near the Gubble, the companion's part>.
- **About the record:** <a clean quit or not; anything the reader reported wrong with the record>.

## The causes (the integrator, from the source)

- **<The thing.>** <File, rule, number: why it happened, in one or two sentences.>

## What the founder decided

- <A decision from the chat, in their words or close to them; or the question still open for them.>

## Keep

- <What stays as it is, and why.>

## Change

Written as guesses the next play can check, each with the lane it falls to.

1. **<Guess.>** <What to build or tune; Lane P / L / A / the kernel; the number or rule to move.>

## Drop

- <What the play showed is not worth keeping.>

## Not this round

- <What was seen and deliberately left.>

## The reader

- <Nothing, or what report.py got wrong about this record, for a Sonnet round.>
```
