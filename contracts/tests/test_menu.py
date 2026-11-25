"""The menu's files (contracts/menu.schema.json; README "The menu's files"): the last game, the carried builds and, since
Run 4's first contract round (CR-1, MENU-1), the ledger of a game New game put away (garage_game.set_aside).

  python -m unittest discover -s contracts/tests -v
"""
from __future__ import annotations

import contextlib
import copy
import hashlib
import io
import json
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

CONTRACTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(CONTRACTS))

import validate  # noqa: E402

IDS = validate.SCHEMA_IDS
EXAMPLES = CONTRACTS / "examples"
SCHEMA = CONTRACTS / "menu.schema.json"
MENU = EXAMPLES / "menu"
FOLDER = EXAMPLES / "set_aside" / "20261123T190512Z-example_team-challenge"
LEDGER = FOLDER / "set_aside.json"
INVALID = EXAMPLES / "set_aside_invalid"
TEAM = EXAMPLES / "teams" / "example_team"
VITALS = "modes/challenge/vitals.json"

# Each broken folder (examples/set_aside_invalid/<name>/<folder>) and the one reason it must fail.
SET_ASIDE_FAILURES = {
    # The folder against its ledger: a put-away game is whole, with nothing changed, missing or added.
    "file_changed_since_put_away": "modes/challenge/vitals.json hash does not match the files list",
    "file_gone_from_the_folder": "inventions.json does not exist",
    "file_the_ledger_never_listed": "inventions.json.pending is in the folder but not in the ledger",
    "folder_named_for_another_moment": "belongs in a folder named '20261123T190512Z-example_team-challenge'",
    # The ledger itself.
    "ledger_without_the_team_file": "does not list team.json",
    "ledger_file_of_another_mode": "file modes/creative/vitals.json is not under modes/challenge/",
    "ledger_holding_the_expertise": "$.files[0].path: 'expertise.json' does not match",
    "ledger_path_that_climbs_out": "'modes/challenge/../../../teams/other_team/team.json' does not match",
    "ledger_with_a_way_back": "('restore_to' was unexpected)",
    "ledger_in_the_old_spelling": "expected 'garage_game.set_aside', found 'enfractal.set_aside'",
}
# The two older kinds' definitions as they stood before the round (SHA-256 of their canonical JSON): not a byte of
# either changed, so every last_game and carried_builds file valid before is valid now and means what it meant.
BEFORE_THE_ROUND = {
    "last_game": "5b0ea2dca166efbdfabcbbf9e08ec4e2398a5f67083088a9d2f65af2f9687b7a",
    "carried_builds": "3c28b875fa817770cecf7433356ba7bb9ca99864dfffdaa330497670fc33678e",
}


def run_validator(*paths) -> tuple[int, str]:
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        status = validate.main([str(path) for path in paths])
    return status, out.getvalue()


class TheSchema(unittest.TestCase):
    def setUp(self):
        self.schema = validate.load_strict(SCHEMA)

    def test_a_third_kind_beside_the_two_which_stand_as_they_were(self):
        self.assertEqual(list(self.schema["$defs"]), ["last_game", "carried_builds", "set_aside"])
        for kind, before in BEFORE_THE_ROUND.items():
            with self.subTest(kind=kind):
                self.assertEqual(hashlib.sha256(validate.canonical_bytes(self.schema["$defs"][kind])).hexdigest(), before)
        older, new = self.schema["properties"]["schema"]["anyOf"]
        self.assertEqual(older, {"enum": ["garage_game.last_game", "garage_game.carried_builds", "enfractal.last_game", "enfractal.carried_builds"]})
        self.assertEqual(new["const"], "garage_game.set_aside")
        self.assertEqual([rule["then"]["$ref"] for rule in self.schema["allOf"]],
                         ["#/$defs/last_game", "#/$defs/carried_builds", "#/$defs/set_aside"])
        self.assertEqual(self.schema["allOf"][2]["if"], {"properties": {"schema": {"const": "garage_game.set_aside"}}, "required": ["schema"]})

    def test_the_ledgers_id_has_one_spelling_everywhere(self):
        self.assertEqual(validate.SET_ASIDE, "garage_game.set_aside")
        self.assertEqual(self.schema["$defs"]["set_aside"]["properties"]["schema"]["const"], validate.SET_ASIDE)
        self.assertEqual(validate.SCHEMA_FILES[validate.SET_ASIDE], "menu.schema.json")
        self.assertNotIn("enfractal.set_aside", validate.SCHEMA_FILES)
        ledger = validate.load_strict(LEDGER)
        self.assertEqual(ledger["schema"], validate.SET_ASIDE)
        self.assertEqual(validate.schema_errors({**ledger, "schema": "enfractal.set_aside"}), ["$.schema: unknown schema 'enfractal.set_aside'"])
        # The id table: a kind first written after the rename has no old spelling, so it stands under NEW_ONLY, and the
        # validator, the examples' builder and the game's C# take the id from their tables, never from a spelling of their own.
        self.assertNotIn("set_aside", IDS.IDS)
        self.assertEqual(IDS.NEW_ONLY["set_aside"], "garage_game.set_aside")
        self.assertEqual((IDS.id_of("set_aside"), IDS.ids_of("set_aside"), IDS.kind_of(validate.SET_ASIDE)), (validate.SET_ASIDE, (validate.SET_ASIDE,), "set_aside"))
        self.assertIsNone(IDS.kind_of("enfractal.set_aside"))
        builder = (EXAMPLES / "build_examples.py").read_text(encoding="utf-8")
        self.assertIn('SET_ASIDE_ID = ID("set_aside")', builder)
        self.assertNotIn('"garage_game.set_aside"', builder)
        game = CONTRACTS.parent / "game" / "scripts" / "native"
        self.assertIn(f'public const string SetAside = "{validate.SET_ASIDE}";', (game / "SchemaIds.cs").read_text(encoding="utf-8"))
        self.assertIn("public const string SchemaId = SchemaIds.SetAside;", (game / "Saves" / "GameSetAside.cs").read_text(encoding="utf-8"))

    def test_the_readme_lists_the_kind_and_the_validator_reads_all_three(self):
        readme = (CONTRACTS / "README.md").read_text(encoding="utf-8")
        section = readme[readme.index("## Schema ids"):]
        section = section[:section.index("\n## ", 4)]
        self.assertIn("| set_aside | `garage_game.set_aside` | none (written after the rename) |", section)
        self.assertIn("## The menu's files", readme)
        for kind in ("last_game", "carried_builds"):
            self.assertEqual(validate.KIND_FILES[kind], "menu.schema.json")


class TheSetAsideLedger(unittest.TestCase):
    def setUp(self):
        self.ledger = validate.load_strict(LEDGER)
        self.temporary = tempfile.TemporaryDirectory()
        self.base = Path(self.temporary.name)

    def tearDown(self):
        self.temporary.cleanup()

    def copy(self, name: str = FOLDER.name) -> Path:
        return Path(shutil.copytree(FOLDER, self.base / name))

    def changed(self, **fields) -> dict:
        return {**copy.deepcopy(self.ledger), **fields}

    def with_path(self, path: str) -> dict:
        ledger = copy.deepcopy(self.ledger)
        ledger["files"][0]["path"] = path
        return ledger

    # ----- the examples -----

    def test_the_example_passes_as_a_folder_and_as_its_ledger(self):
        self.assertEqual(validate.check_path(FOLDER), [])
        self.assertEqual(validate.check_document(LEDGER), [])
        self.assertEqual(validate.check_set_aside(self.ledger, "ledger"), [])
        self.assertEqual(run_validator(FOLDER), (0, "OK: 1 item(s) checked, 0 problem(s)\n"))

    def test_each_broken_folder_fails_for_its_one_reason(self):
        self.assertEqual({path.name for path in INVALID.iterdir()}, set(SET_ASIDE_FAILURES))
        for name, reason in SET_ASIDE_FAILURES.items():
            with self.subTest(folder=name):
                folders = list((INVALID / name).iterdir())
                self.assertEqual(len(folders), 1)
                problems = validate.check_path(folders[0])
                self.assertEqual(len(problems), 1, problems)
                self.assertIn(reason, problems[0])
                status, output = run_validator(folders[0])
                self.assertEqual((status, output.splitlines()[-1]), (1, "FAILED: 1 item(s) checked, 1 problem(s)"))
                # Read as a file rather than a folder, it fails all the same.
                self.assertTrue(validate.check_document(folders[0] / "set_aside.json"))

    def test_the_example_is_the_example_teams_challenge_game_kept_whole(self):
        listed = {entry["path"]: entry for entry in self.ledger["files"]}
        self.assertEqual(sorted([*listed, "set_aside.json"]), validate._files_in(FOLDER))
        # The team file and the vitals store are the example team's own bytes, and still read as what they are.
        for path in ("team.json", VITALS):
            with self.subTest(file=path):
                data = (FOLDER / path).read_bytes()
                self.assertEqual(data, (TEAM / path).read_bytes())
                self.assertEqual((listed[path]["sha256"], listed[path]["bytes"]), (hashlib.sha256(data).hexdigest(), len(data)))
                self.assertEqual(validate.check_document(FOLDER / path), [])
        team = validate.load_strict(FOLDER / "team.json")
        self.assertEqual((team["team_id"], self.ledger["team_id"], self.ledger["mode"]), ("example_team", "example_team", "challenge"))
        self.assertTrue(team["pockets"]["challenge"] and team["hand"]["challenge"], "the put-away team file holds the game's pockets and hand")
        self.assertEqual(self.ledger["last_played_utc"], validate.load_strict(FOLDER / VITALS)["written_utc"])
        # Each island save stands where it stood under the team's folder: modes/<mode>/islands/<room_id>/<manifest prefix>/.
        island = hashlib.sha256((EXAMPLES / "rooms" / "island_example" / "room.json").read_bytes()).hexdigest()[:16]
        self.assertEqual([path for path in sorted(listed) if "/islands/" in path],
                         ["modes/challenge/islands/garage_example/0123456789abcdef/inventions.json",
                          f"modes/challenge/islands/island_example/{island}/inventions.json"])
        self.assertEqual(team["pockets"]["challenge"]["things"][0]["manifest_prefix"], "0123456789abcdef")
        # The other mode, the expertise and the menu's own files are not the game: none is put away.
        self.assertFalse([path for path in listed if path.startswith("modes/creative/") or path in ("expertise.json", "carried_builds.json")])

    def test_a_ledger_as_the_game_writes_it_passes(self):
        """C# writes one line of JSON and a line feed (the menu's other writers do); the ledger's meaning is not its spacing."""
        folder = self.copy()
        (folder / "set_aside.json").write_bytes((json.dumps(self.ledger, separators=(",", ":")) + "\n").encode("utf-8"))
        self.assertEqual(validate.check_path(folder), [])

    # ----- the schema -----

    def test_the_schema_refuses(self):
        entry = self.ledger["files"][0]
        cases = {
            "another version": self.changed(version=2),
            "a version written as a fraction": self.changed(version=1.0),
            "a mode there is none of": self.changed(mode="story"),
            "a team named for a Windows device": self.changed(team_id="nul"),
            "a team named by a path": self.changed(team_id="../other"),
            "a moment with a split second": self.changed(set_aside_utc="2026-11-23T19:05:12.5Z"),
            "a moment in local time": self.changed(set_aside_utc="2026-11-23T13:05:12-06:00"),
            "a last played that is a date alone": self.changed(last_played_utc="2026-10-14"),
            "no files": self.changed(files=[]),
            "more files than a ledger lists": self.changed(files=[{**entry, "path": f"modes/challenge/f{n}"} for n in range(4097)]),
            "a file with where it came from": self.changed(files=[{**entry, "from": "teams/example_team/modes/challenge/vitals.json"}]),
            "a file without its hash": self.changed(files=[{"path": "team.json", "bytes": 1}]),
            "a hash in capitals": self.changed(files=[{**entry, "sha256": entry["sha256"].upper()}]),
            "a size below nothing": self.changed(files=[{**entry, "bytes": -1}]),
            "a principal": self.changed(principal="companion:local"),
            "a note": self.changed(note="Put away on a Monday"),
            "a list of rooms beside the files": self.changed(islands=["garage_example"]),
        }
        for name in ("schema", "version", "team_id", "mode", "set_aside_utc", "last_played_utc", "files"):
            cases[f"no {name}"] = {key: value for key, value in self.ledger.items() if key != name}
        for name, ledger in cases.items():
            with self.subTest(case=name):
                self.assertTrue(validate.schema_errors(ledger))
        self.assertEqual(validate.schema_errors(self.ledger), [])
        self.assertEqual(validate.schema_errors(self.changed(files=[{**entry, "path": f"modes/challenge/f{n}"} for n in range(4096)])), [])

    def test_a_path_never_leaves_its_folder(self):
        refused = [
            "expertise.json", "carried_builds.json", "last_game.json", "set_aside.json", "Team.json", "team.json/", "/team.json",
            "teams/example_team/team.json", "modes", "modes/challenge", "modes/challenge/", "modes/story/vitals.json",
            "modes/Challenge/vitals.json", "modes/challenge//vitals.json", "modes/challenge/./vitals.json",
            "modes/challenge/../creative/vitals.json", "modes/challenge/islands/..", "modes/challenge/islands/../../../team.json",
            "../team.json", "C:/Users/someone/team.json", "modes/challenge/C:/vitals.json", "modes/challenge\\vitals.json",
            "modes/challenge/islands\\..\\..\\..\\other", "modes/challenge/vitals.json\n", "modes/challenge/vit\u0000als.json",
            "modes/challenge/vitals.json:stream", "modes/challenge/vitals?.json", "modes/challenge/*.json", "modes/challenge/\"vitals\".json",
            "modes/challenge/<vitals>.json", "modes/challenge/vitals|more.json", "modes/challenge/" + "a" * 400, "",
        ]
        for path in refused:
            with self.subTest(refused=path):
                self.assertTrue(validate.schema_errors(self.with_path(path)), path)
        # A stray file someone left beside a save is put away with the game: its name is no reason to refuse.
        kept = [
            "team.json", "modes/challenge/vitals.json", "modes/creative/vitals.json", "modes/live/islands/room/0123456789abcdef/inventions.json",
            "modes/challenge/islands/garage_example/0123456789abcdef/inventions - Copy.json",
            "modes/challenge/islands/garage_example/0123456789abcdef/inventions.json.pending",
            "modes/challenge/islands/garage_example/0123456789abcdef/inventions.json.0f8fad5bd9cb469fa16570867728950e.tmp",
            "modes/challenge/vitals (1).json", "modes/challenge/notes for later.txt", "modes/challenge/caf\u00e9.txt", "modes/challenge/..hidden",
            "modes/challenge/a...b", "modes/challenge/" + "a" * 384,
        ]
        for path in kept:
            with self.subTest(kept=path):
                self.assertEqual(validate.schema_errors(self.with_path(path)), [], path)
        # A misplaced emoji marker is refused in a path as in every string (the validator's text rule, not the pattern).
        self.assertEqual(validate.schema_errors(self.with_path("modes/challenge/vitals\u200d.json")),
                         ["$.files[0].path: contains the invisible, unpaired or misplaced character U+200D"])

    # ----- what the schema cannot say -----

    def test_the_ledgers_own_rules(self):
        entry = next(e for e in self.ledger["files"] if e["path"] == VITALS)
        team = next(e for e in self.ledger["files"] if e["path"] == "team.json")
        big = [{**entry, "path": f"modes/challenge/{n:04d}-" + "a" * 360} for n in range(4095)] + [team]
        cases = {
            "a file twice": (self.changed(files=self.ledger["files"] + [entry]), f"file {VITALS} is listed twice"),
            "put away on no such day": (self.changed(set_aside_utc="2026-11-31T19:05:12Z"), "set_aside_utc '2026-11-31T19:05:12Z' is not a real moment"),
            "put away at no such hour": (self.changed(set_aside_utc="2026-11-23T24:05:12Z"), "is not a real moment"),
            "last played on no such day": (self.changed(last_played_utc="2026-02-30T19:30:00Z"), "last_played_utc '2026-02-30T19:30:00Z' is not a real moment"),
            "without the team file": (self.changed(files=[entry]), "does not list team.json"),
            "another mode's file": (self.changed(files=self.ledger["files"] + [{**entry, "path": "modes/live/vitals.json"}]),
                                    "file modes/live/vitals.json is not under modes/challenge/"),
            "a mode that is not its files'": (self.changed(mode="creative"), f"file {VITALS} is not under modes/creative/"),
            "past its size": (self.changed(files=big), f"the limit is {validate.SET_ASIDE_LEDGER_LIMIT}"),
            # The last save is the newest write among the game's files as the put-away found them: never after it.
            "last played years after it was put away": (self.changed(last_played_utc="2031-01-01T00:00:00Z"),
                                                        "last_played_utc '2031-01-01T00:00:00Z' is after set_aside_utc '2026-11-23T19:05:12Z'"),
            "last played a moment after it was put away": (self.changed(last_played_utc="2026-11-23T19:05:12.001Z"), "a game is last saved before it is put away"),
        }
        for name, (ledger, reason) in cases.items():
            with self.subTest(case=name):
                self.assertEqual(validate.schema_errors(ledger), [], "the schema passes it: the rule is the validator's")
                problems = validate.check_set_aside(ledger, "ledger")
                self.assertTrue(any(reason in problem for problem in problems), problems)
        # A leap day is a real day; a fraction on the last save's moment is the vitals store's to write.
        self.assertEqual(validate.check_set_aside(self.changed(last_played_utc="2024-02-29T23:59:59.250Z"), "ledger"), [])
        # Saved in the very second it was put away, or a split second before: both are before the put-away.
        for moment in ("2026-11-23T19:05:12Z", "2026-11-23T19:05:11.999999Z"):
            self.assertEqual(validate.check_set_aside(self.changed(last_played_utc=moment), "ledger"), [], moment)
        # No real moment, no order to tell: the one problem is the moment's own.
        self.assertEqual(len(validate.check_set_aside(self.changed(last_played_utc="2027-02-30T19:30:00Z"), "ledger")), 1)
        # The team file alone is a game: pockets filled and nothing saved yet.
        self.assertEqual(validate.check_set_aside(self.changed(files=[team]), "ledger"), [])

    def test_the_folder_is_named_for_its_moment_its_team_and_its_mode(self):
        self.assertEqual(validate.set_aside_folder_name(self.ledger), FOLDER.name)
        self.assertEqual(validate.set_aside_folder_name({"set_aside_utc": "2027-01-02T03:04:05Z", "team_id": "my-team_2", "mode": "live"}),
                         "20270102T030405Z-my-team_2-live")
        for name in ("20261123T190512Z-example_team-creative", "20261123T190512Z-other_team-challenge", "20261124T190512Z-example_team-challenge",
                     "example_team-challenge", "set_aside"):
            with self.subTest(folder=name):
                problems = validate.check_path(self.copy(name))
                self.assertEqual(len(problems), 1, problems)
                self.assertIn(f"belongs in a folder named {FOLDER.name!r}", problems[0])

    def test_one_changed_byte_in_any_file_is_caught(self):
        for entry in self.ledger["files"]:
            with self.subTest(file=entry["path"]):
                folder = self.copy(f"{len(list(self.base.iterdir()))}/{FOLDER.name}")
                target = folder / entry["path"]
                data = bytearray(target.read_bytes())
                data[len(data) // 2] ^= 1
                target.write_bytes(bytes(data))
                self.assertEqual(validate.check_path(folder), [f"{FOLDER.name}/set_aside.json: {entry['path']} hash does not match the files list"])

    def test_a_folder_is_held_to_its_ledger(self):
        def problems_after(change) -> list[str]:
            folder = self.copy(f"{len(list(self.base.iterdir()))}/{FOLDER.name}")
            change(folder)
            return validate.check_path(folder)

        label = f"{FOLDER.name}/set_aside.json"
        self.assertEqual(problems_after(lambda folder: (folder / "modes" / "challenge" / "islands" / "empty_room").mkdir()), [],
                         "an empty folder is no file")
        self.assertEqual(problems_after(lambda folder: (folder / "notes.txt").write_bytes(b"mine\n")),
                         [f"{label}: file notes.txt is in the folder but not in the ledger; a put-away game is whole, with nothing added"])
        self.assertEqual(problems_after(lambda folder: (folder / "modes" / "creative").mkdir() or (folder / "modes" / "creative" / "vitals.json").write_bytes(b"{}\n")),
                         [f"{label}: file modes/creative/vitals.json is in the folder but not in the ledger; a put-away game is whole, with nothing added"])
        cut = problems_after(lambda folder: (folder / VITALS).write_bytes((folder / VITALS).read_bytes()[:-10]))
        self.assertEqual(cut, [f"{label}: {VITALS} is 630 bytes, files list says 640", f"{label}: {VITALS} hash does not match the files list"])
        self.assertEqual(problems_after(lambda folder: (folder / "team.json").unlink()), [f"{label}: listed file team.json does not exist"])
        self.assertEqual(problems_after(lambda folder: shutil.rmtree(folder / "modes")),
                         [f"{label}: listed file {entry['path']} does not exist" for entry in self.ledger["files"] if entry["path"] != "team.json"])

    def test_a_file_is_listed_by_its_own_name(self):
        """Where the file system is forgiving (Windows: another case, a trailing dot or space), one file answers to more than
        one name, so a ledger could list it twice and still find every file "there". A listed file is there under its listed
        name; on a file system that tells the names apart, the second name is simply missing. Refused either way."""
        island = next(e["path"] for e in self.ledger["files"] if "/islands/garage_example/" in e["path"])
        label = f"{FOLDER.name}/set_aside.json"
        cases = {
            "another case": (VITALS, "modes/challenge/Vitals.json"),
            "a trailing dot": (VITALS, VITALS + "."),
            "a trailing space": (VITALS, VITALS + " "),
            "a folder in another case": (island, island.replace("/islands/", "/Islands/")),
        }
        for name, (real, other) in cases.items():
            with self.subTest(case=name):
                entry = next(e for e in self.ledger["files"] if e["path"] == real)
                ledger = self.changed(files=self.ledger["files"] + [{**entry, "path": other}])
                self.assertEqual(validate.schema_errors(ledger), [], "the schema passes it: the rule is the validator's")
                folder = self.copy(f"{len(list(self.base.iterdir()))}/{FOLDER.name}")
                (folder / "set_aside.json").write_bytes(validate.canonical_bytes(ledger) + b"\n")
                problems = validate.check_path(folder)
                if (folder / other).is_file():  # this file system answers to the other name with the same file
                    self.assertEqual(problems, [f"{label}: listed file {other} is {real} in the folder: a file is listed by its own name, once"])
                else:
                    self.assertEqual(problems, [f"{label}: listed file {other} does not exist"])
        # The mend's own rule, on any file system: a name the folder's walk does not hold is not the file's own.
        self.assertEqual(validate._other_spelling("modes/challenge/Vitals.json.", [VITALS, "team.json"]), VITALS)
        self.assertEqual(validate._other_spelling("modes/challenge/VITALS.JSON . .", [VITALS]), VITALS)
        self.assertEqual(validate._other_spelling("modes/Challenge ./vitals.json", [VITALS]), VITALS)
        self.assertIsNone(validate._other_spelling("modes/challenge/vitals.jsonx", [VITALS]))

    def test_a_folder_given_by_a_relative_path_is_read_as_the_folder_it_is(self):
        """The folder's name is its real one, not the last word of the path as it was typed: from inside the folder, or
        through a path that ends in '..', a put-away game is the same game."""
        folder = self.copy()
        misnamed = self.copy("20261123T190513Z-example_team-challenge")
        before = Path.cwd()
        try:
            os.chdir(folder)
            self.assertEqual(validate.check_path(Path(".")), [])
            self.assertEqual(validate.check_path(Path("modes") / ".."), [])
            self.assertEqual(validate.check_document(Path("set_aside.json")), [])
            self.assertEqual(run_validator("."), (0, "OK: 1 item(s) checked, 0 problem(s)\n"))
            self.assertEqual(run_validator("set_aside.json"), (0, "OK: 1 item(s) checked, 0 problem(s)\n"))
            os.chdir(folder / "modes" / "challenge")
            self.assertEqual(validate.check_path(Path("..") / ".."), [])
            # A folder with the wrong name is still refused for it, and named by its real name.
            os.chdir(misnamed)
            for path in (Path("."), Path("modes") / ".."):
                self.assertEqual(validate.check_path(path),
                                 [f"{misnamed.name}/set_aside.json: the ledger belongs in a folder named {FOLDER.name!r} (its moment, team and mode), not {misnamed.name!r}"])
        finally:
            os.chdir(before)

    def test_the_ledger_has_one_name_and_is_strict_json(self):
        folder = self.copy()
        (folder / "set_aside.json").rename(folder / "ledger.json")
        self.assertEqual(validate.check_document(folder / "ledger.json"),
                         [f"{FOLDER.name}/ledger.json: a set-aside ledger is named set_aside.json in its folder",
                          f"{FOLDER.name}/ledger.json: file ledger.json is in the folder but not in the ledger; a put-away game is whole, with nothing added"])
        # No ledger, no put-away game: the folder is not read as one (a put-away that did not finish is never a game).
        self.assertTrue(validate.check_path(folder))
        for name, data in (("a byte-order mark", b"\xef\xbb\xbf" + LEDGER.read_bytes()), ("CR LF", LEDGER.read_bytes().replace(b"\n", b"\r\n")),
                           ("a key twice", LEDGER.read_bytes().replace(b'"version": 1,', b'"version": 1,\n  "version": 1,')),
                           ("half a file", LEDGER.read_bytes()[:200]), ("a list", b"[]\n"), ("no schema", b"{}\n")):
            with self.subTest(ledger=name):
                broken = self.copy(f"{len(list(self.base.iterdir()))}/{FOLDER.name}")
                (broken / "set_aside.json").write_bytes(data)
                self.assertEqual(len(validate.check_path(broken)), 1)


class TheOlderFiles(unittest.TestCase):
    """The last game and the carried builds (M7): read by the validator since this round, by their schema alone."""

    def test_the_examples_pass_in_either_spelling(self):
        self.assertEqual(sorted(path.name for path in MENU.iterdir()), ["carried_builds.json", "last_game.json"])
        for kind in ("last_game", "carried_builds"):
            path = MENU / f"{kind}.json"
            document = validate.load_strict(path)
            with self.subTest(kind=kind):
                self.assertEqual(validate.check_document(path), [])
                self.assertEqual(document["schema"], IDS.id_of(kind))
                for spelling in IDS.ids_of(kind):
                    self.assertEqual(validate.schema_errors({**document, "schema": spelling}, kind), [])
                self.assertTrue(validate.schema_errors({**document, "principal": "companion:local"}))
        self.assertEqual(run_validator(MENU / "last_game.json", MENU / "carried_builds.json"), (0, "OK: 2 item(s) checked, 0 problem(s)\n"))

    def test_they_refuse_what_they_refused(self):
        last = validate.load_strict(MENU / "last_game.json")
        carried = validate.load_strict(MENU / "carried_builds.json")
        copy_ = carried["islands"]["garage_example"]
        cases = {
            "a room named by a path": {**last, "room_id": "../rooms/garage"},
            "a mode there is none of": {**last, "mode": "story"},
            "a difficulty there is none of": {**last, "difficulty": "hard"},
            "a last game with no moment": {key: value for key, value in last.items() if key != "written_utc"},
            "a copy out of Challenge": {**carried, "islands": {"garage_example": {**copy_, "from_mode": "challenge"}}},
            "a build that is no creation": {**carried, "islands": {"garage_example": {**copy_, "builds": ["obj:bean_bag"]}}},
            "an island named by a path": {**carried, "islands": {"../garage": copy_}},
            # One kind's fields under another's id.
            "a ledger's fields as a last game": {**validate.load_strict(LEDGER), "schema": last["schema"]},
            "a last game's fields as a ledger": {**last, "schema": validate.SET_ASIDE},
        }
        for name, document in cases.items():
            with self.subTest(case=name):
                self.assertTrue(validate.schema_errors(document))
        self.assertEqual(validate.schema_errors({**last, "mode": "creative", "difficulty": None}), [])


if __name__ == "__main__":
    unittest.main()
