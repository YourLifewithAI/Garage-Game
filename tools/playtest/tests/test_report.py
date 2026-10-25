"""What the report says: the example play, the twenty-minute fixture, and small plays made to test one rule each."""
from __future__ import annotations

import re
import tempfile
import unittest
from pathlib import Path

import support
from support import body, command, ev, load, needs_pillow, notice, report, section, write_play

try:
    from PIL import Image
except ImportError:
    Image = None


def seconds_of(text: str) -> float:
    """'20 min 4.3 s' or '58.0 s' as seconds."""
    match = re.fullmatch(r"(?:(\d+) min )?(\d+(?:\.\d)?) s", text.strip())
    assert match, text
    return int(match.group(1) or 0) * 60 + float(match.group(2))


def table_rows(block: str) -> list:
    return [[c.strip() for c in line.strip().strip("|").split("|")] for line in block.splitlines() if line.startswith("| ") and "---" not in line]


class ExamplePlayReport(unittest.TestCase):
    """R0's example play (contracts/examples/playtests): the report and the sheet from it."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.tmp.cleanup)
        cls.folder = support.copy_play(support.EXAMPLE_PLAY, Path(cls.tmp.name), stills=True)
        cls.result = report.write_report(cls.folder)
        cls.text = (cls.folder / "REPORT.md").read_text(encoding="utf-8")

    def test_the_play_in_a_line(self):
        first = self.text.split("\n\n")[2]
        for part in ("island_example", "Challenge (standard)", "c19d99b", "0.5 minutes", "quit cleanly"):
            self.assertIn(part, first)

    def test_the_marks_come_first_each_with_its_context_window(self):
        self.assertLess(self.text.index("## The marks"), self.text.index("## Refusals"))
        marks = section(self.text, "## The marks")
        self.assertIn("### Mark 1, something's off at 0:18", marks)
        self.assertIn("### Keep 1, keep this at 0:25", marks)
        first = marks.split("### Keep 1")[0]
        self.assertIn("`journal.note` ok", first)  # 3.5 s before the mark
        self.assertIn("`-18.7 s` ready", first)  # the whole twenty seconds, back to the room coming up
        self.assertIn("[hud] `Mark 1`", first)
        self.assertIn("`+4.2 s` command: `companion:local` `view.look`", first)  # four seconds after, inside the five
        self.assertNotIn("playtest_keep", first)  # the keep is 6.4 s after: outside

    def test_a_refusal_shown_on_screen_is_matched_by_its_action_id(self):
        refusals = section(self.text, "## Refusals")
        self.assertIn("### `energy_exhausted`: 1 time", refusals)
        self.assertIn("matched by action_id", refusals)
        self.assertNotIn("not shown on screen**", refusals)

    def test_the_sheet_and_the_stills_list(self):
        if Image is None:  # the contracts' environment has no Pillow: the report says so and there is no sheet
            self.assertIn("Pillow is not installed", self.result["sheet_note"])
            self.assertFalse((self.folder / "sheet.jpg").exists())
            self.assertNotIn("![Contact sheet]", self.text)
        else:
            self.assertEqual(self.result["sheet_note"], "")
            self.assertTrue((self.folder / "sheet.jpg").is_file())
        stills = section(self.text, "## Stills")
        self.assertIn("7 stills named in the record, 7 on disk", stills)
        self.assertEqual("![Contact sheet](sheet.jpg)" in stills, Image is not None)

    def test_the_time_budgets_add_up_to_the_plays_length(self):
        time = section(self.text, "## Where the time went")
        for block in time.split("### ")[1:]:
            rows = table_rows(block)
            body_rows, total = rows[1:-1], rows[-1]
            self.assertEqual(total[0], "**total**")
            self.assertAlmostEqual(sum(seconds_of(r[1]) for r in body_rows), seconds_of(total[1]), places=6)
            self.assertAlmostEqual(seconds_of(total[1]), 30.2, places=6)


class FixtureReport(unittest.TestCase):
    """The twenty-minute fixture, with stills made at run time."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.tmp.cleanup)
        cls.folder = support.copy_play(support.FIXTURE_PLAY, Path(cls.tmp.name), stills=True)
        cls.result = report.write_report(cls.folder)
        cls.text = (cls.folder / "REPORT.md").read_text(encoding="utf-8")
        cls.play = cls.result["play"]

    def test_the_play_in_a_line(self):
        first = self.text.split("\n\n")[2]
        for part in ("island_example", "Challenge (brave)", "19a8924", "20.1 minutes", "quit cleanly"):
            self.assertIn(part, first)
        self.assertNotIn("About this record", self.text)  # a clean record has nothing wrong with it

    def test_the_sections_come_in_the_briefs_order(self):
        headings = [line for line in self.text.splitlines() if line.startswith("## ")]
        self.assertEqual(headings, ["## At a glance", "## The marks", "## Refusals", "## Commands", "## Where the time went", "## Frames",
                                    "## Notices, in order", "## Stills"])

    def test_five_marks_in_order_each_with_its_still(self):
        marks = section(self.text, "## The marks")
        headings = re.findall(r"^### (.*)$", marks, re.M)
        self.assertEqual(headings, ["Mark 1, something's off at 5:12", "Keep 1, keep this at 7:58", "Mark 2, something's off at 15:05",
                                    "Keep 2, keep this at 16:00", "Mark 3, something's off at 19:20"])
        stills = re.findall(r"!\[(?:Mark|Keep)\]\(stills/([^)]+)\)", marks)
        self.assertEqual(stills, ["0033-mark-1.jpg", "0052-keep-1.jpg", "0099-mark-2.jpg", "0106-keep-2.jpg", "0188-mark-3.jpg"])

    def test_the_first_marks_window_is_the_twenty_seconds_before_and_five_after(self):
        first = section(self.text, "## The marks").split("### Keep 1")[0]
        self.assertIn("Ouch! A heart and a quarter lost", first)  # 1.8 s after
        self.assertIn("Are you all right?", first)  # 3.6 s after
        self.assertIn("`falling`", first)  # where the body was
        self.assertNotIn("Picked up the log", first)  # 32.8 s after
        self.assertNotIn("It is a long way down", first)  # 51.4 s before
        self.assertIn("Where the body was:", first)

    def test_a_still_body_is_not_listed_every_two_seconds(self):
        third = section(self.text, "## The marks").split("### Mark 3")[1]
        self.assertLessEqual(third.count(": `standing` at"), 3)  # the body stood still for the whole window
        self.assertIn("breadcrumbs that differ little", third)

    def test_refusals_by_code_with_the_notice_that_showed_them(self):
        refusals = section(self.text, "## Refusals")
        groups = dict(re.findall(r"^### `(\w+)`: (\d+) time", refusals, re.M))
        self.assertEqual(groups, {"energy_exhausted": "3", "budget_exceeded": "1", "occupied": "1", "permission_denied": "1", "request_invalid": "1",
                                  "target_too_heavy": "1", "target_unreachable": "1"})
        self.assertIn("9 refused, 4 shown on screen and 5 not shown on screen", refusals)
        shown = {m[0]: m[1] for m in re.findall(r"`(cast-0001|cast-0011|grab-0002|goal-0002)`.*?matched by ([a-z_ ]+)", refusals)}
        self.assertEqual(shown, {"cast-0001": "action_id", "cast-0011": "code and time", "grab-0002": "code and time", "goal-0002": "code and time"})
        for action_id in ("cast-0005", "cast-0009", "place-0002", "unlock-0001"):  # never shown, or shown too late to be this one
            self.assertRegex(refusals, rf"`{action_id}`[^\n]*\*\*not shown on screen\*\*")
        self.assertIn("Refusal notices the screen showed that no refused command claimed", refusals)  # the late notice at 11:44

    def test_commands_by_op_and_principal_with_the_companions_part_apart(self):
        commands = section(self.text, "## Commands")
        player, companion = commands.split("### The companion's part")
        self.assertNotIn("companion:local", player)
        self.assertNotIn("player:local", companion)
        rows = {(r[0], r[1]): r[2:5] for r in table_rows(companion)}
        self.assertEqual(rows[("`companion:local`", "`observe`")], ["36", "36", "0"])
        self.assertEqual(rows[("`companion:local`", "`nature.cast`")], ["11", "7", "4"])
        self.assertEqual(rows[("`companion:local`", "`protect.unlock`")], ["1", "0", "1"])
        grab = {(r[0], r[1]): r[2:5] for r in table_rows(player)}[("`player:local`", "`entity.grab`")]
        self.assertEqual(grab, ["2", "1", "1"])
        self.assertIn("58 commands: 42 queries and 16 that change", companion)

    def test_every_time_budget_adds_up_to_the_plays_length(self):
        for name, rows in report.budgets(self.play).items():
            self.assertAlmostEqual(sum(s for _l, s in rows), self.play.length_s, places=6, msg=name)
        time = section(self.text, "## Where the time went")
        blocks = time.split("### ")[1:]
        self.assertEqual(len(blocks), 4)
        for block in blocks:
            rows = table_rows(block)
            total = rows[-1]
            self.assertEqual(seconds_of(total[1]), 1204.3)
            self.assertAlmostEqual(sum(seconds_of(r[1]) for r in rows[1:-1]), 1204.3, places=6)  # the printed rows add up too
            self.assertEqual(sum(int(r[2].rstrip("%")) for r in rows[1:-1]), 100)

    def test_the_budgets_hold_what_the_scenario_did(self):
        tables = {name: dict(rows) for name, rows in report.budgets(self.play).items()}
        self.assertAlmostEqual(tables["state"]["climbing"], 80.0)
        self.assertAlmostEqual(tables["state"]["swimming"], 90.0)
        self.assertAlmostEqual(tables["state"]["carried"], 40.0)
        self.assertAlmostEqual(tables["height"][report.band_label(6)], 14.0)  # 2.0 to 2.4 m: the climb's last 12 s and the first of the fall
        self.assertAlmostEqual(tables["height"][report.band_label(7)], 42.0)  # 2.4 to 2.8 m: the shelf top
        self.assertAlmostEqual(tables["height"][report.BELOW_FLOOR], 58.0)  # the dive
        self.assertAlmostEqual(tables["gubble"]["farther than 2 m from the Gubble"], 318.0)
        self.assertAlmostEqual(tables["link"]["offline"], 118.3)  # the two stretches without the AI, to the quit
        self.assertAlmostEqual(tables["state"][report.NO_BREADCRUMB], 2.0)  # before the first breadcrumb

    def test_the_five_worst_frame_windows_with_their_positions(self):
        frames = section(self.text, "## Frames")
        rows = table_rows(frames)[1:]
        self.assertEqual([(r[0], r[1]) for r in rows], [("10:00–10:05", "212.4"), ("10:40–10:45", "188.0"), ("10:20–10:25", "148.7"),
                                                        ("10:05–10:10", "96.2"), ("10:45–10:50", "64.9")])
        self.assertEqual(rows[0][4], "(-0.12, 0.00, 0.95)")
        self.assertIn("`nature.cast` (companion)", rows[0][6])  # what the Gubble was casting while the frames hitched
        self.assertIn("240 windows", frames)
        self.assertIn("7 had a frame over 33.3 ms", frames)
        # and the windows that were slow all the way through, which is a different list: the highest mean frame time
        self.assertIn("the highest mean frame time): 17:05–17:10 at 25.0 ms (longest 48.0), 10:40–10:45 at 17.3 ms (longest 188.0), "
                      "10:00–10:05 at 16.8 ms (longest 212.4).", frames)

    def test_the_notices_in_order(self):
        notices = [line for line in section(self.text, "## Notices, in order").splitlines() if line.startswith("- `")]
        wanted = [e for e in support.read_events(support.FIXTURE_PLAY) if e["kind"] == "notice"]
        self.assertEqual(len(notices), len(wanted))
        for line, event in zip(notices, wanted):
            self.assertIn(f"[{event['channel']}]", line)
            if event["text"]:
                self.assertIn(event["text"], line)
        times = [seconds_of(re.match(r"- `(\d+:\d\d)`", l).group(1).replace(":", " min ") + " s") for l in notices]
        self.assertEqual(times, sorted(times))

    def test_the_stills_listed_and_the_sheet(self):
        stills = section(self.text, "## Stills")
        self.assertIn("193 stills named in the record, 193 on disk: 180 steady, 4 refusal, 3 mark, 2 keep, 2 mode, 1 home, 1 quit.", stills)
        self.assertIn("steady ×46", stills)  # a run of the one-second beat is one row
        for name in ("0033-mark-1.jpg", "0016-refusal.jpg", "0084-home.jpg", "0193-quit.jpg"):
            self.assertIn(name, stills)

    @needs_pillow
    def test_the_contact_sheet_holds_at_most_sixty_with_marks_and_refusals_first(self):
        selection = report.sheet_selection(report.find_stills(self.play))
        self.assertEqual(len(selection), 60)
        self.assertEqual([s.why for s in selection[:9]], ["mark", "keep", "mark", "keep", "mark"] + ["refusal"] * 4)
        self.assertEqual(len({s.file for s in selection}), 60)
        self.assertEqual(self.result["sheet_items"], 60)
        with Image.open(self.folder / "sheet.jpg") as sheet:
            self.assertEqual(sheet.format, "JPEG")
            self.assertEqual(sheet.size, (6 * (256 + 8), 10 * (144 + 14 + 8)))

    def test_the_report_is_the_same_every_time(self):
        before = (self.folder / "REPORT.md").read_bytes()
        report.write_report(self.folder)
        self.assertEqual((self.folder / "REPORT.md").read_bytes(), before)

    def test_the_report_is_plain_utf8_with_lf_and_names_nothing_outside_the_play(self):
        data = (self.folder / "REPORT.md").read_bytes()
        self.assertNotIn(b"\r", data)
        self.assertNotIn("http", support.outside_code_spans(data.decode("utf-8")))  # the fixture's hostile link is quoted in a code span
        self.assertIn("Château des rêves ✨".encode("utf-8"), data)  # real text survives


class SheetSelection(unittest.TestCase):
    def still(self, n, t, why, present=True, name=None):
        file = name or f"{n:04d}-{why if why not in ('mark', 'keep') else why + '-1'}.jpg"
        return report.StillRec(n, t, file, why, "eye", None, None, Path(file) if present else None)

    def test_a_few_stills_are_all_there(self):
        stills = [self.still(1, 1.0, "steady"), self.still(2, 2.0, "mark"), self.still(3, 3.0, "steady"), self.still(4, 4.0, "refusal")]
        self.assertEqual([s.n for s in report.sheet_selection(stills)], [2, 4, 1, 3])  # marks and refusals first, then in time

    def test_marks_and_refusals_win_the_places_and_the_steady_beat_spreads_over_time(self):
        stills = [self.still(i, float(i), "steady") for i in range(1, 400)] + [self.still(500 + i, 100.5 + i, "refusal") for i in range(5)] \
            + [self.still(600 + i, 50.5 + i, "mark") for i in range(3)]
        chosen = report.sheet_selection(stills)
        self.assertEqual(len(chosen), 60)
        self.assertEqual([s.why for s in chosen[:8]], ["mark"] * 3 + ["refusal"] * 5)
        steady = [s.t for s in chosen[8:]]
        self.assertEqual(steady, sorted(steady))
        self.assertEqual((steady[0], steady[-1]), (1.0, 399.0))  # the first and the last are in
        gaps = [b - a for a, b in zip(steady, steady[1:])]
        self.assertLess(max(gaps) - min(gaps), 2.0)  # evenly spread

    def test_more_marks_than_the_sheet_holds_still_gives_sixty(self):
        stills = [self.still(i, float(i), "mark", name=f"{i:04d}-mark-{i}.jpg") for i in range(1, 80)]
        self.assertEqual(len(report.sheet_selection(stills)), 60)

    def test_a_steady_still_that_is_missing_is_not_chosen_but_a_missing_mark_is_kept_as_a_gap(self):
        stills = [self.still(1, 1.0, "steady", present=False), self.still(2, 2.0, "mark", present=False), self.still(3, 3.0, "steady")]
        self.assertEqual([s.n for s in report.sheet_selection(stills)], [2, 3])

    def test_quit_home_and_mode_stills_outrank_the_steady_beat(self):
        stills = [self.still(i, float(i), "steady") for i in range(1, 100)] + [self.still(200, 500.0, "quit")]
        chosen = report.sheet_selection(stills)
        self.assertEqual(len(chosen), 60)
        self.assertEqual(chosen[-1].why, "quit")


class WhatMarksContain(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.dir = Path(self.tmp.name)

    def test_the_window_is_twenty_seconds_before_and_five_after_edges_in_and_just_outside_out(self):
        events = [ev(0.0, "ready", spawn_m=[0, 0, 0]),
                  notice(79.9, "before-outside"), notice(80.0, "before-edge"), notice(100.0, "at-the-mark"),
                  ev(100.0, "still", file="0001-mark-1.jpg", why="mark"), ev(100.0, "mark", number=1, flag="off", still="0001-mark-1.jpg"),
                  notice(105.0, "after-edge"), notice(105.1, "after-outside"), ev(110.0, "quit", played_s=110.0, events=9, stills=1, marks=1)]
        play = load(self.dir, events)
        mark = report.find_marks(play)[0]
        text = "\n".join(report.context_lines(play, mark.ev))
        for word, shown in (("before-outside", False), ("before-edge", True), ("at-the-mark", True), ("after-edge", True), ("after-outside", False)):
            self.assertEqual(word in text, shown, word)

    def test_a_mark_with_no_neighbours_says_so(self):
        play = load(self.dir, [ev(5.0, "mark", number=1, flag="off", still="0001-mark-1.jpg")])
        self.assertEqual(report.context_lines(play, play.timeline[0]), ["- _nothing recorded in this window._"])

    def test_a_crowded_window_is_capped_and_says_how_many_were_left_out(self):
        events = [command(100.0 + i * 0.1, "observe", principal="companion:local") for i in range(150)] + [ev(105.0, "mark", number=1, flag="off", still="x")]
        events.sort(key=lambda e: e["t"])
        play = load(self.dir, events)
        lines = report.context_lines(play, [e for e in play.timeline if e.kind == "mark"][0])
        self.assertEqual(len(lines), report.WINDOW_LINES + 1)
        self.assertRegex(lines[-1], r"more events in this window are not shown")

    def test_the_thinning_keeps_what_changed(self):
        samples = [body(100.0 + 2 * i, state="standing", position=(0, 0, 0)) for i in range(5)]
        samples += [body(110.0, state="walking", position=(0, 0, 0)), body(112.0, state="walking", position=(0.1, 0, 0)), body(114.0, state="walking", position=(0.7, 0, 0)),
                    body(117.0, state="walking", position=(0.7, 0, 0))]
        play = load(self.dir, samples + [ev(115.0, "mark", number=1, flag="off", still="x")])
        kept, left_out = report.thin_bodies(report.window(play, 95.0, 120.0), 115.0)
        # the first, the change of state, the move of half a metre (also the one before the mark), and the first after the mark
        self.assertEqual([e.t for e in kept if e.kind == "body"], [100.0, 110.0, 114.0, 117.0])
        self.assertEqual(left_out, 5)


class RefusalMatching(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.dir = Path(self.tmp.name)

    def match(self, events):
        play = load(self.dir, events)
        return report.match_refusals(play, report.find_stills(play))

    def refusal(self, t, code="energy_exhausted", action_id=None, **kw):
        fields = {"action_id": action_id} if action_id else {}
        return command(t, "nature.cast", ok=False, code=code, message="The Gubble needs a moment.", principal="companion:local", **fields, **kw)

    def test_by_action_id_even_when_the_notice_comes_late_or_says_other_words(self):
        refusals, stray = self.match([self.refusal(10.0, action_id="a-1"), notice(25.0, "Hm.", channel="gubble", action_id="a-1", code="other_code")])
        self.assertEqual((refusals[0].how, refusals[0].notice.d["text"]), ("action_id", "Hm."))
        self.assertEqual(stray, [])

    def test_an_action_id_is_not_matched_by_a_different_id(self):
        refusals, _ = self.match([self.refusal(10.0, action_id="a-1"), notice(10.1, "x", channel="refusal", action_id="a-2", code="energy_exhausted")])
        self.assertEqual(refusals[0].how, "code and time")  # the code and the time still tie it, as the brief says: else by code and time

    def test_by_code_and_time_from_a_second_before_to_five_after(self):
        for dt, shown in ((-1.0, True), (-1.5, False), (0.0, True), (5.0, True), (5.5, False)):
            refusals, _ = self.match([self.refusal(10.0), notice(10.0 + dt, "x", channel="hud", code="energy_exhausted")])
            self.assertEqual(refusals[0].notice is not None, shown, dt)

    def test_by_the_same_words_on_the_refusal_channel_when_the_notice_has_no_code(self):
        refusals, _ = self.match([self.refusal(10.0), notice(10.5, "The Gubble needs a moment.", channel="refusal")])
        self.assertEqual(refusals[0].how, "the same words")
        refusals, _ = self.match([self.refusal(10.0), notice(10.5, "The Gubble needs a moment.", channel="hud")])
        self.assertIsNone(refusals[0].notice)  # the same words on another channel are not a refusal shown

    def test_one_notice_shows_one_refusal(self):
        refusals, _ = self.match([self.refusal(10.0), self.refusal(10.2), notice(10.5, "x", code="energy_exhausted")])
        self.assertEqual([r.notice is not None for r in refusals], [True, False])

    def test_a_refusal_notice_nothing_claimed_is_listed_apart(self):
        _, stray = self.match([notice(3.0, "No!", channel="refusal", code="occupied"), command(50.0, "entity.grab", ok=False, code="occupied")])
        self.assertEqual([n.d["text"] for n in stray], ["No!"])

    def test_the_refusal_still_is_the_one_taken_after_the_notice(self):
        refusals, _ = self.match([self.refusal(10.0), notice(10.0, "x", channel="refusal", code="energy_exhausted"),
                                  ev(10.5, "still", file="0001-refusal.jpg", why="refusal")])
        self.assertEqual(refusals[0].still.file, "0001-refusal.jpg")

    def test_a_result_with_no_code_is_grouped_under_no_code(self):
        refusals, _ = self.match([ev(1.0, "command", principal="player:local", op="x", result={"ok": False}, ms=1.0)])
        self.assertEqual(refusals[0].code, "(no code)")

    def test_a_refused_preview_is_a_preview_not_a_refusal(self):
        """The second recorded play (17 October): the reticle's lead previews the pool could not pay were the HUD asking, 32 of them
        counted as silent refusals. The host now records them with result.preview: never a refusal the player met, counted apart."""
        lead = command(10.0, "goal.set", ok=False, code="energy_exhausted", message="The Gubble needs a moment.", action_id="lead-1")
        lead["result"]["preview"] = True
        events = [lead, self.refusal(20.0, action_id="cast-1"), notice(20.1, "The Gubble needs a moment.", channel="refusal", action_id="cast-1")]
        refusals, stray = self.match(events)
        self.assertEqual(([r.ev.d.get("action_id") for r in refusals], stray), (["cast-1"], []))
        play = load(self.dir, events)
        rows = {r.op: r for r in report.command_rows(play)}
        self.assertEqual((rows["goal.set"].total, rows["goal.set"].ok, rows["goal.set"].refused, rows["goal.set"].previews_refused), (1, 0, 0, 1))
        self.assertEqual((rows["nature.cast"].refused, rows["nature.cast"].previews_refused), (1, 0))
        report.write_report(write_play(self.dir, events))
        written = (self.dir / support.RECORD / "REPORT.md").read_text(encoding="utf-8")
        self.assertIn("**Refusals:** 1, 1 shown on screen, 0 not shown on screen; 1 refused preview not counted (the HUD asking, not a refusal)", written)
        self.assertIn("1 refused, 1 shown on screen and 0 not shown on screen", section(written, "## Refusals"))
        self.assertIn("| Refused previews |", section(written, "## Commands"))
        self.assertIn("`goal.set` preview refused `energy_exhausted`", report.event_line(play.timeline[0], 0.0))


class TimeBudgets(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.dir = Path(self.tmp.name)

    def budgets(self, events, **manifest):
        play = report.load_play(write_play(self.dir, events, support.manifest_for(events, **manifest)))
        return play, {name: dict(rows) for name, rows in report.budgets(play).items()}

    def test_a_breadcrumb_holds_to_the_next_and_the_rest_is_no_breadcrumb(self):
        play, tables = self.budgets([body(2.0, "standing"), body(4.0, "walking"), body(10.0, "running"), ev(14.0, "quit", played_s=14.0, events=4, stills=0, marks=0)])
        self.assertEqual(tables["state"], {"standing": 2.0, "walking": 6.0, "running": 4.0, report.NO_BREADCRUMB: 2.0})  # 0-2 before the first
        self.assertAlmostEqual(sum(tables["state"].values()), play.length_s)

    def test_a_gap_longer_than_the_hold_cap_is_not_blamed_on_the_last_state(self):
        _, tables = self.budgets([body(2.0, "standing"), body(30.0, "walking")])
        self.assertEqual(tables["state"]["standing"], 6.0)
        self.assertNotIn("walking", tables["state"])  # the last breadcrumb is at the very end: no time left for it
        self.assertAlmostEqual(sum(tables["state"].values()), 30.0)
        self.assertAlmostEqual(tables["state"][report.NO_BREADCRUMB], 2.0 + 22.0)

    def test_height_bands_and_the_gubble_two_metres(self):
        events = [body(2.0, height=0.0, distance=2.0), body(4.0, height=0.3, distance=2.01), body(6.0, height=0.5, distance=0.1),
                  body(8.0, height=1.99, distance=5.0), body(10.0, height=2.0, distance=1.0), body(12.0, height=-0.3, distance=1.0)]
        _, tables = self.budgets(events + [ev(14.0, "quit", played_s=14.0, events=7, stills=0, marks=0)])
        self.assertEqual({k: v for k, v in tables["height"].items() if k != report.NO_BREADCRUMB},
                         {report.band_label(1): 4.0, report.band_label(2): 2.0, report.band_label(5): 2.0, report.band_label(6): 2.0, report.BELOW_FLOOR: 2.0})
        self.assertEqual(tables["gubble"]["within 2 m of the Gubble"], 8.0)  # exactly two metres is within
        self.assertEqual(tables["gubble"]["farther than 2 m from the Gubble"], 4.0)

    def test_the_height_bands_are_the_games_own_point_four_metre_bands(self):
        """The rules pack's band_m is 0.4 (docs/game/MECHANICS.md): band 1 is 0 to 0.4 m, and a height exactly on an edge is in the band above it."""
        wanted = {0.0: 1, 0.39: 1, 0.4: 2, 0.79: 2, 0.8: 3, 1.2: 4, 1.6: 5, 2.0: 6, 2.4: 7, 3.99: 10, 7.6: 20, 1000.0: 20, -0.004: 1}
        for height, band in wanted.items():
            self.assertEqual(report.height_band(height), report.band_label(band), height)
        self.assertEqual(report.height_band(-0.3), report.BELOW_FLOOR)
        self.assertEqual(report.band_label(1), "band 1: 0.0 to 0.4 m")
        self.assertEqual(report.band_label(report.BANDS_SHOWN), "band 20 and up: 7.6 m and above")
        self.assertEqual(report.BAND_M, 0.4)
        self.assertEqual(len(set(report.HEIGHT_LABELS)), report.BANDS_SHOWN + 1)

    def test_no_breadcrumbs_means_one_bucket_that_is_the_whole_play(self):
        play, tables = self.budgets([command(3.0, "observe"), ev(10.0, "quit", played_s=10.0, events=2, stills=0, marks=0)])
        self.assertEqual(tables["state"], {report.NO_BREADCRUMB: 10.0})
        self.assertIn("No breadcrumbs", "\n".join(report.section_time(play, [], [], [], [], {"state": [], "height": [], "gubble": [], "link": []})))

    def test_rounding_keeps_the_printed_rows_adding_up(self):
        for values in ([1 / 3, 1 / 3, 1 / 3], [0.05, 0.05, 0.05, 0.05, 0.8], [10.04, 20.04, 30.04, 40.04, 59.84]):
            total = sum(values)
            rounded = report.round_to_sum(values, total, 0.1)
            self.assertAlmostEqual(sum(rounded), round(total, 1), places=9, msg=values)
        self.assertEqual(report.round_to_sum([], 0.0, 0.1), [])
        self.assertEqual(report.round_to_sum([33.4, 33.3, 33.3], 100.0, 1.0), [34.0, 33.0, 33.0])


class FramesAndCommands(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.dir = Path(self.tmp.name)

    def test_the_worst_windows_by_longest_frame_ties_by_mean_at_most_five(self):
        frames = [ev(5.0 * i, "frame", window_s=5, mean_ms=8.0 + (i == 3), longest_ms=100.0 if i in (2, 3) else float(10 + i), position_m=[i, 0, 0])
                  for i in range(1, 12)]
        play = load(self.dir, frames)
        worst = sorted(report.frame_records(play), key=lambda f: (-f.longest_ms, -f.mean_ms, f.ev.t))[:5]
        self.assertEqual([f.ev.t for f in worst][:2], [15.0, 10.0])  # the tie goes to the higher mean
        text = report.section_frames(play, [], [], [], report.frame_records(play), {})
        self.assertEqual(len(table_rows("\n".join(text))), 6)  # a header and five

    def test_the_worst_windows_longest_frame_is_split_when_the_record_says(self):
        """The recorder since the first recorded play's fix round: the engine's process and physics time at the window's longest frame,
        and the window's collections and pauses; a record without them (every play before) prints no split."""
        split = {"longest_process_ms": 71.5, "longest_physics_ms": 22.25, "gc_pause_ms": 9.4, "gc_collections": "3/1/0"}
        frames = [ev(5.0 * i, "frame", window_s=5, mean_ms=8.0, longest_ms=100.0 if i == 2 else 10.0, position_m=[i, 0, 0], **(split if i == 2 else {}))
                  for i in range(1, 5)]
        play = load(self.dir, frames)
        text = "\n".join(report.section_frames(play, [], [], [], report.frame_records(play), {}))
        self.assertIn("The worst window's longest frame (0:05–0:10), as the record splits it: process 71.5 ms, physics 22.2 ms; "
                      "the window's collections `3/1/0` (gen 0/1/2) paused 9.4 ms.", text)
        self.assertNotIn("longest_process_ms", text)
        other = tempfile.TemporaryDirectory()
        self.addCleanup(other.cleanup)
        plain = load(Path(other.name), [ev(5.0 * i, "frame", window_s=5, mean_ms=8.0, longest_ms=10.0, position_m=[i, 0, 0]) for i in range(1, 5)])
        self.assertNotIn("as the record splits it", "\n".join(report.section_frames(plain, [], [], [], report.frame_records(plain), {})))

    def test_a_play_with_no_frames_says_so(self):
        play = load(self.dir, [ev(1.0, "ready", spawn_m=[0, 0, 0])])
        self.assertIn("No frame windows were recorded.", report.section_frames(play, [], [], [], [], {}))

    def test_a_command_with_no_result_is_counted_in_the_total_only(self):
        play = load(self.dir, [command(1.0, "observe"), ev(2.0, "command", principal="player:local", op="observe", result="??", ms=1)])
        row = report.command_rows(play)[0]
        self.assertEqual((row.total, row.ok, row.refused, row.unknown), (2, 1, 0, 1))


if __name__ == "__main__":
    unittest.main()
