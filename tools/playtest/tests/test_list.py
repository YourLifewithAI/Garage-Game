"""--list: every play under a folder of plays, and the command line."""
from __future__ import annotations

import contextlib
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import support
from support import report, write_play

UNCLEAN = "20261016-130000-island_example"


class ListPlays(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        support.copy_play(support.EXAMPLE_PLAY, self.root, stills=False)
        support.copy_play(support.FIXTURE_PLAY, self.root, stills=False)
        self.unclean = self.make_unclean()

    def make_unclean(self) -> Path:
        """The fixture's first five and a half minutes, the game killed: no ended_utc, no quit line, a line cut in half."""
        events = [e for e in support.read_events(support.FIXTURE_PLAY) if e["t"] <= 330.0]
        manifest = support.manifest_for(events, ended=False, record_id=UNCLEAN)
        data = "".join(json.dumps(e) + "\n" for e in events).encode() + b'{"t": 331.0, "utc": "2026-10-16T17:00:00Z", "ki'
        return write_play(self.root, data, manifest, name=UNCLEAN)

    def test_three_plays_newest_first_one_not_quit_cleanly(self):
        rows = report.list_plays(self.root)
        self.assertEqual([r.name for r in rows], [UNCLEAN, "20261016-101500-island_example", "20261015-143000-island_example"])
        killed, fixture, example = rows
        self.assertFalse(killed.clean_quit)
        self.assertTrue(fixture.clean_quit and example.clean_quit)
        self.assertEqual((killed.date, killed.room, killed.marks), ("2026-10-16 13:00", "island_example", 1))  # the fall at 5:12 was marked
        self.assertAlmostEqual(killed.minutes, 330.0 / 60, places=2)  # as far as the events got
        self.assertEqual((fixture.date, fixture.marks), ("2026-10-16 10:15", 5))
        self.assertAlmostEqual(fixture.minutes, 1204.3 / 60, places=3)
        self.assertEqual((example.date, example.marks), ("2026-10-15 14:30", 2))
        self.assertAlmostEqual(example.minutes, 30.2 / 60, places=3)
        self.assertEqual([r.has_report for r in rows], [False, False, False])

    def test_the_listing_says_so_in_words_and_notes_which_have_a_report(self):
        report.write_report(self.root / "20261016-101500-island_example")
        text = report.format_list(self.root, report.list_plays(self.root))
        lines = text.splitlines()
        self.assertEqual(len(lines), 4)  # a heading and three plays
        self.assertIn("3 plays under", lines[0])
        self.assertEqual(text.count("NOT QUIT CLEANLY"), 1)
        self.assertIn("NOT QUIT CLEANLY", lines[1])
        self.assertIn(UNCLEAN, lines[1])
        self.assertIn("5.5 min", lines[1])
        self.assertIn("1 mark ", lines[1])
        self.assertRegex(lines[2], r"2026-10-16 10:15  island_example  +20\.1 min  5 marks +quit cleanly +report: yes +20261016-101500-island_example")
        self.assertRegex(lines[3], r"report: no +20261015-143000-island_example")
        self.assertEqual(text.count("report: yes"), 1)

    def test_a_clean_play_is_listed_from_its_manifest_alone(self):
        killed = self.root / UNCLEAN
        with mock.patch.object(report, "read_events", side_effect=AssertionError("a clean play's events were read")):
            rows = {r.name: r for r in report.list_plays(self.root) if r.name != UNCLEAN}
            self.assertEqual(len(rows), 2)
        self.assertTrue(killed.is_dir())

    def test_what_is_not_a_play_is_left_out_and_what_is_damaged_is_still_listed(self):
        (self.root / "notes").mkdir()
        (self.root / "notes" / "todo.txt").write_text("hi")
        (self.root / "loose.txt").write_text("hi")
        write_play(self.root, b"", b"not json", name="20261016-140000-broken")  # a play whose manifest is garbage
        write_play(self.root, [support.ev(1.0, "ready", spawn_m=[0, 0, 0])], None, name="20261016-150000-nomanifest")
        rows = {r.name: r for r in report.list_plays(self.root)}
        self.assertNotIn("notes", rows)
        self.assertNotIn("loose.txt", rows)
        self.assertEqual(len(rows), 5)
        self.assertTrue(rows["20261016-140000-broken"].unreadable)
        self.assertIn("NO MANIFEST", report.format_list(self.root, list(rows.values())))

    def test_no_plays_and_no_folder(self):
        empty = self.root / "empty"
        empty.mkdir()
        self.assertIn("No plays under", report.format_list(empty, report.list_plays(empty)))
        self.assertEqual(report.list_plays(self.root / "not-there"), [])

    def test_the_listing_of_hostile_names_is_plain_text(self):
        write_play(self.root, [support.ev(1.0, "ready", spawn_m=[0, 0, 0])], support.manifest_for([], ended=False, room_id="evil‮\x1b[31m\nroom"),
                   name="20261016-160000-evil")
        text = report.format_list(self.root, report.list_plays(self.root))
        self.assertNotIn("\x1b", text)
        self.assertNotIn("‮", text)
        self.assertEqual(len(text.splitlines()), 5)


class CommandLine(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)

    def run_main(self, *args) -> tuple:
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            try:
                code = report.main([str(a) for a in args])
            except SystemExit as stop:
                code = stop.code
        return code, out.getvalue(), err.getvalue()

    def test_a_report_from_the_command_line_names_what_it_wrote(self):
        folder = support.copy_play(support.FIXTURE_PLAY, self.root, stills=True)
        code, out, err = self.run_main(folder)
        self.assertEqual((code, err), (0, ""))
        self.assertIn("20.1 minutes, quit cleanly, 5 marks, 193 stills", out)
        self.assertIn("REPORT.md", out)
        self.assertTrue((folder / "REPORT.md").is_file())
        if support.Image is not None:
            self.assertIn("sheet.jpg (60 stills)", out)
            self.assertTrue((folder / "sheet.jpg").is_file())
        self.assertEqual(sorted(p.name for p in folder.iterdir()), sorted(["REPORT.md", "events.jsonl", "manifest.json", "stills"] + (["sheet.jpg"] if support.Image else [])))

    def test_without_pillow_the_report_still_comes_and_says_there_is_no_sheet(self):
        if support.Image is None:
            self.skipTest("Pillow is not installed: that is the case under test, and it is what every other sheet-less run shows")
        folder = support.copy_play(support.FIXTURE_PLAY, self.root, stills=True)  # the stills are made with Pillow, then the reader runs without it
        with mock.patch.dict(sys.modules, {"PIL": None, "PIL.Image": None, "PIL.ImageDraw": None, "PIL.ImageFont": None}):
            code, out, _err = self.run_main(folder)
        self.assertEqual(code, 0)
        self.assertIn("no contact sheet: Pillow is not installed", out)
        self.assertIn("No contact sheet: Pillow is not installed", (folder / "REPORT.md").read_text(encoding="utf-8"))
        self.assertFalse((folder / "sheet.jpg").exists())

    def test_as_a_script_under_isolated_mode_with_nothing_but_the_standard_library_needed(self):
        folder = support.copy_play(support.EXAMPLE_PLAY, self.root, stills=False)
        done = subprocess.run([sys.executable, "-I", str(support.TOOL / "report.py"), str(folder)], capture_output=True, text=True, timeout=120)
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertIn("0.5 minutes, quit cleanly, 2 marks, 7 stills", done.stdout)
        listing = subprocess.run([sys.executable, "-I", str(support.TOOL / "report.py"), "--list", str(self.root)], capture_output=True, text=True, timeout=120)
        self.assertEqual(listing.returncode, 0, listing.stderr)
        self.assertIn("report: yes", listing.stdout)

    def test_list_from_the_command_line(self):
        support.copy_play(support.EXAMPLE_PLAY, self.root, stills=False)
        code, out, _ = self.run_main("--list", self.root)
        self.assertEqual(code, 0)
        self.assertIn("1 play under", out)
        self.assertIn("20261015-143000-island_example", out)

    def test_list_with_no_folder_looks_in_the_games_user_folder(self):
        base = self.root / "appdata"
        env = {"APPDATA": str(base), "XDG_DATA_HOME": str(base)}
        with mock.patch.dict(os.environ, env):
            where = report.default_plays_folder()
            if os.name == "nt":
                self.assertEqual(where, base / "Godot" / "app_userdata" / "Garage-Game" / "playtests")
            elif sys.platform != "darwin":
                self.assertEqual(where, base / "godot" / "app_userdata" / "Garage-Game" / "playtests")
            code, out, err = self.run_main("--list")
        self.assertEqual(code, 2)  # nothing recorded there yet
        self.assertIn("no folder of plays", err)

    def test_list_reads_the_old_folder_until_the_renamed_game_has_run(self):
        base = self.root / "appdata-before-the-rename"
        env = {"APPDATA": str(base), "XDG_DATA_HOME": str(base)}
        godot = "Godot" if os.name == "nt" else "godot"
        if sys.platform == "darwin":
            self.skipTest("the user folder is under the home folder on macOS")
        old = base / godot / "app_userdata" / "EnFractal" / "playtests"
        old.mkdir(parents=True)
        with mock.patch.dict(os.environ, env):
            self.assertEqual(report.default_plays_folder(), old)
            new = base / godot / "app_userdata" / "Garage-Game" / "playtests"
            new.mkdir(parents=True)
            self.assertEqual(report.default_plays_folder(), new)

    def test_usage_errors_are_messages(self):
        code, _out, err = self.run_main()
        self.assertEqual(code, 2)
        self.assertIn("give a play's folder", err)
        code, _out, err = self.run_main(self.root / "nowhere")
        self.assertEqual(code, 2)
        self.assertIn("is not a play's folder", err)
        code, _out, err = self.run_main("--list", self.root / "nowhere")
        self.assertEqual(code, 2)


if __name__ == "__main__":
    unittest.main()
