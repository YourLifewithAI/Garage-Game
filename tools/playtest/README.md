# The playtest reader (R2)

Turns a play's folder into a report a person can read. The game writes the record (`manifest.json`, `events.jsonl`, `stills/`; contract in [contracts/README.md](../../contracts/README.md) "The playtest record"); this reads it. Roadmap: [docs/runs/RUN-2-PLAYTEST-LOOP.md](../../docs/runs/RUN-2-PLAYTEST-LOOP.md).

```sh
python tools/playtest/report.py <play folder>     # writes REPORT.md and sheet.jpg inside the folder
python tools/playtest/report.py --list [<plays>]  # one line per play: date, room, minutes, marks, clean quit or not, report or not
```

Without a folder, `--list` looks in the game's user folder (`%APPDATA%\Godot\app_userdata\Garage-Game\playtests` on Windows; a play recorded under `-Profile` lives under that profile's own folder, so name it). `--list` puts the newest play first; its folder name is what `report.py` takes.

## What the report holds

The play in a line (room, mode, difficulty, build, minutes, a clean quit or not); a short "At a glance"; **the marks first**, each with its still, where the body was, and the events of the 20 seconds before and 5 after; refusals grouped by code, each with the notice that showed it or "not shown on screen" (matched by `action_id`, else by code and time, else by the same words); commands by op and principal with the companion's part apart; time by body state, height (in the game's own 0.4 m bands), within 2 m of the Gubble and companion link, each adding up to the play's length; the five worst frame windows (by longest single frame) with their positions, and the three slowest on average; the notices in order; the stills as a list, and `sheet.jpg` (at most 60 thumbnails with their times, marks and refusals first).

What is wrong with a record (unknown kinds, extra fields, a truncated last line, a missing still, a missing manifest field, no `ended_utc`) is listed under "About this record" and the report goes on: a damaged record gets a report, never a crash.

## Everything in a record is data

A creation's name or a note can carry any text a player typed. The reader prints record text only inside code spans (control and invisible characters stripped, capped, backticks and pipes swapped for lookalikes), executes and follows nothing, fetches nothing, and opens no path a record names except its own `stills/<name>` (the name held to the contract's pattern, the file to that folder). Stills are never in Git: the tests make tiny ones at run time.

## Files

| File | What |
|---|---|
| `report.py` | the reader: standard library only, plus Pillow for the sheet (the report is written without it and says so) |
| `requirements.txt` | Pillow, pinned |
| `tests/build_fixture.py` | writes the twenty-minute fixture, deterministically |
| `tests/fixtures/<record_id>/` | the fixture play (`manifest.json`, `events.jsonl`; byte-exact, valid under `contracts/validate.py`; never any stills) |
| `tests/support.py`, `tests/test_*.py` | the tests: the example play, the fixture, damaged and hostile records, `--list` and the command line |

```sh
uv venv .cache/playtest-venv && uv pip install -r tools/playtest/requirements.txt
.cache/playtest-venv/bin/python -m unittest discover -s tools/playtest/tests          # Scripts\python.exe on Windows
.cache/contracts-venv/bin/python -I contracts/validate.py tools/playtest/tests/fixtures/20261016-101500-island_example
python tools/playtest/tests/build_fixture.py                                          # after changing the scenario
```

The tests run headless on Windows and Linux with no network and read nothing of the founder's records. One of them runs the contract validator on the fixture if an interpreter with `jsonschema` is at hand (this one, or `.cache/contracts-venv`), and says so when it skips.
