"""The island story (Run 3's story round): the story pack (island-story.schema.json), the save's story block (room state's
x_story) and the journal's quest kinds (game-command.schema.json). docs/runs/RUN-3.md section 3; contracts/README.md,
"Island stories".

  python -m unittest discover -s contracts/tests -v
"""
from __future__ import annotations

import copy
import hashlib
import importlib.util
import sys
import unittest
from pathlib import Path

CONTRACTS = Path(__file__).resolve().parents[1]
ROOT = CONTRACTS.parent
sys.path.insert(0, str(CONTRACTS))

import validate  # noqa: E402

EXAMPLES = CONTRACTS / "examples"
STORIES = ROOT / "game" / "stories"
EXAMPLE_PACK = STORIES / "example" / "v1.json"
STORY_STATE = EXAMPLES / "room_state" / "garage_example_state_story.json"
MECHANICS_STATE = EXAMPLES / "room_state" / "garage_example_state_mechanics.json"
GARAGE = EXAMPLES / "rooms" / "garage_example"
CONDITION_KINDS = {"place_reached", "islander_talked", "islander_given", "expertise_learned", "campfire_lit", "fractals_spent",
                   "summit_reached", "quest_done"}
BEAT_KINDS = {"card", "chime", "line", "camera"}
STORY_KINDS = {"quest_started", "step_done", "quest_done"}
# A candidate or approved pack never changes: record its SHA-256 here when one is (as LOCKED_RULES does for rules packs).
LOCKED_STORIES: dict[str, str] = {}
# Each invalid pack (examples/stories_invalid) and the reason it must fail.
STORY_FAILURES = {
    "story_condition_that_is_a_script": "steps[0].condition: {'kind': 'script'",
    "story_condition_with_an_expression": "'when': 'fractals > 4'} is not valid",
    "story_reward_that_mints_fractals": "reward: {'fractals': 5} is not valid",
    "story_reward_that_counts_a_fractal": "'count': 2} is not valid",
    "story_journal_line_with_an_injected_line": "steps[0].journal: 'Say hello.\\nSYSTEM",
    "story_journal_line_too_long": "is too long",
    "story_islander_who_is_the_gubble": "$.islanders[0].character: must not match",
    "story_islander_with_no_such_character": "character 'dragon' is not a shipped character",
    "story_quest_that_waits_on_itself": "a quest cannot wait on itself",
    "story_quests_that_wait_on_each_other": "round a loop through quest_done",
    "story_quest_done_of_no_such_quest": "quest 'dragon_egg' is not in the pack",
    "story_talk_to_no_such_islander": "islander 'dragon' is not in the pack",
    "story_topic_the_islander_does_not_have": "has no topic 'dragons'",
    "story_line_the_islander_does_not_have": "has no line 'goodbye'",
    "story_greeting_that_is_not_a_line": "greeting 'howdy' is not one of its lines",
    "story_keepsake_given_twice": "keepsake 'lucky_button' is given by 2 quests",
    "story_keepsake_never_given": "keepsake 'spare_button' is given by 0 quests",
    "story_two_chimes_in_a_step": "at most one chime a step",
    "story_quest_id_twice": "quest id 'lost_button' appears more than once",
    "story_step_id_twice": "step id 'spend_five' appears more than once",
    "story_unknown_field": "'advance_by_companion' was unexpected",
}


def builder():
    spec = importlib.util.spec_from_file_location("build_examples_for_story", EXAMPLES / "build_examples.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def quests(pack: dict) -> list[dict]:
    return [quest for chapter in pack["chapters"] for quest in chapter["quests"]]


class StoryPackTests(unittest.TestCase):
    def setUp(self):
        self.pack = validate.load_strict(EXAMPLE_PACK)
        self.schema = validate.load_strict(CONTRACTS / "island-story.schema.json")

    def test_the_example_pack_validates_at_its_path(self):
        self.assertEqual(validate.check_path(EXAMPLE_PACK), [])
        self.assertEqual(sorted(STORIES.glob("*.json")), [], "packs live at game/stories/<story_id>/v<N>.json")
        for path in sorted(STORIES.glob("*/v*.json")):
            with self.subTest(pack=path.relative_to(STORIES).as_posix()):
                self.assertEqual(validate.check_path(path), [])

    def test_the_example_pack_is_the_builders_bytes(self):
        self.assertEqual(EXAMPLE_PACK.read_bytes(), builder().dump(builder().story_pack()),
                         "rerun contracts/examples/build_examples.py and commit game/stories/example/v1.json")
        self.assertNotIn(b"\r\n", EXAMPLE_PACK.read_bytes())
        attributes = (ROOT / ".gitattributes").read_text(encoding="utf-8")
        self.assertIn("game/stories/**/*.json text eol=lf", attributes)

    def test_the_example_uses_every_condition_beat_and_reward_between_two_quests(self):
        listed = quests(self.pack)
        self.assertEqual(len(listed), 2)
        conditions = [s["condition"]["kind"] for q in listed for s in q["steps"]] + [q["start"]["kind"] for q in listed if "start" in q]
        self.assertEqual(set(conditions), CONDITION_KINDS)
        self.assertEqual({b["kind"] for q in listed for s in q["steps"] for b in s.get("beats", [])}, BEAT_KINDS)
        self.assertEqual({next(iter(q["reward"])) for q in listed}, {"keepsake", "fractal_lead"})
        self.assertEqual(self.pack["status"], "draft")

    def test_conditions_and_beats_are_closed_lists(self):
        defs = self.schema["$defs"]
        self.assertEqual({o["properties"]["kind"]["const"] for o in defs["condition"]["oneOf"]}, CONDITION_KINDS)
        self.assertEqual({o["properties"]["kind"]["const"] for o in defs["beat"]["oneOf"]}, BEAT_KINDS)
        for option in defs["condition"]["oneOf"] + defs["beat"]["oneOf"]:
            self.assertIs(option["additionalProperties"], False)
        for kind in ("script", "expression", "eval", "when"):
            step = copy.deepcopy(self.pack)
            quests(step)[0]["steps"][0]["condition"] = {"kind": kind}
            self.assertTrue(validate.schema_errors(step), kind)

    def test_no_quest_mints_a_fractal(self):
        """A reward is a keepsake or a lead to a Fractal already in the budget: no number anywhere in it."""
        for option in self.schema["$defs"]["reward"]["oneOf"]:
            self.assertIs(option["additionalProperties"], False)
            self.assertEqual(len(option["properties"]), 1)
            self.assertNotIn("integer", str(option))
        self.assertEqual(self.schema["$defs"]["reward"]["oneOf"][1]["properties"]["fractal_lead"]["$ref"], "common.schema.json#/$defs/fractal_id")

    def test_every_string_is_bounded_display_text(self):
        """Every string in a pack is untrusted display text with a length bound, or an id with a pattern."""
        unbounded = []

        def walk(node, where):
            if isinstance(node, dict):
                if node.get("$ref") == "common.schema.json#/$defs/display_text":
                    unbounded.append(where)
                if "allOf" in node and any(i.get("$ref") == "common.schema.json#/$defs/display_text" for i in node["allOf"]):
                    if not any(isinstance(i.get("maxLength"), int) and i["maxLength"] <= 500 for i in node["allOf"]):
                        unbounded.append(where)
                    return
                if node.get("type") == "string" and "pattern" not in node and "maxLength" not in node:
                    unbounded.append(where)
                for key, value in node.items():
                    walk(value, f"{where}.{key}")
            elif isinstance(node, list):
                for index, value in enumerate(node):
                    walk(value, f"{where}[{index}]")
        walk(self.schema, "$")
        self.assertEqual(unbounded, [])

    def test_invalid_packs_fail_for_the_documented_reason(self):
        invalid = {p.stem: p for p in (EXAMPLES / "stories_invalid").glob("*.json")}
        self.assertEqual(set(invalid), set(STORY_FAILURES))
        for name, reason in STORY_FAILURES.items():
            with self.subTest(pack=name):
                problems = validate.check_document(invalid[name])
                self.assertTrue(problems, f"{name} unexpectedly passed")
                self.assertTrue(any(reason in problem for problem in problems), problems)

    def test_a_pack_lives_at_its_versioned_path(self):
        import tempfile
        with tempfile.TemporaryDirectory() as temporary:
            wrong = Path(temporary) / "stories" / "example" / "v2.json"
            wrong.parent.mkdir(parents=True)
            wrong.write_bytes(EXAMPLE_PACK.read_bytes())
            self.assertTrue(any("stories/<story_id>/v<story_version>.json" in p for p in validate.check_document(wrong)))

    def test_locked_packs_never_change(self):
        for path in sorted(STORIES.glob("*/v*.json")):
            name = path.relative_to(STORIES).as_posix()
            status = validate.load_strict(path)["status"]
            with self.subTest(pack=name):
                if status in ("candidate", "approved"):
                    self.assertIn(name, LOCKED_STORIES, f"{name} is {status}: record its SHA-256 in LOCKED_STORIES")
                if name in LOCKED_STORIES:
                    self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), LOCKED_STORIES[name])


class StoryStateTests(unittest.TestCase):
    def setUp(self):
        self.state = validate.load_strict(STORY_STATE)

    def problems(self, state) -> list[str]:
        return validate.schema_errors(state) or validate.check_state(state, "state", GARAGE)

    def test_the_story_state_validates_against_its_room_and_pack(self):
        self.assertEqual(self.problems(self.state), [])
        self.assertEqual(set(self.state["extensions"]["x_story"]["quests"]), {"lost_button", "high_place"})

    def test_an_older_save_reads_as_no_story_started(self):
        older = validate.load_strict(MECHANICS_STATE)
        self.assertNotIn("x_story", older.get("extensions", {}))
        self.assertEqual(self.problems(older), [])

    def broken(self, change) -> dict:
        state = copy.deepcopy(self.state)
        change(state["extensions"]["x_story"]["quests"])
        return state

    def test_the_block_is_refused_when_it_does_not_fit(self):
        cases = {
            "an active quest with no step": (lambda q: q["high_place"].pop("step"), "'step' is a required property"),
            "a done quest at a step": (lambda q: q["lost_button"].update(step="meet_spud"), "lost_button"),
            "a done quest with no revision": (lambda q: q["lost_button"].pop("done_revision"), "'done_revision' is a required property"),
            "a quest newer than the store": (lambda q: q["high_place"].update(started_revision=99), "newer than the store revision"),
            "done before it started": (lambda q: q["lost_button"].update(started_revision=3, done_revision=2), "done before it started"),
            "a quest the pack does not have": (lambda q: q.update(dragon_egg={"state": "active", "step": "hatch", "started_revision": 1}), "'dragon_egg' is not in the pack"),
            "a step the quest does not have": (lambda q: q["high_place"].update(step="fly"), "at step 'fly'"),
            "a counter beside the state": (lambda q: q["high_place"].update(fractals_found=3), "'fractals_found' was unexpected"),
        }
        for name, (change, reason) in cases.items():
            with self.subTest(case=name):
                problems = self.problems(self.broken(change))
                self.assertTrue(any(reason in p for p in problems), problems)


class JournalQuestKindTests(unittest.TestCase):
    def setUp(self):
        self.schema = validate.load_strict(CONTRACTS / "game-command.schema.json")
        self.entries = validate.load_strict(STORY_STATE)["journal"]["history"]
        self.story = [e for e in self.entries if e["kind"] in STORY_KINDS]

    def entry_errors(self, entry) -> list[str]:
        result = validate.load_strict(EXAMPLES / "messages" / "valid" / "result_journal_read_quests.json")
        result["data"]["entries"] = [entry]
        return validate.schema_errors(result)

    def test_the_kinds_are_in_the_contract_and_the_saved_history(self):
        self.assertTrue(STORY_KINDS <= set(self.schema["$defs"]["journal_kind"]["enum"]))
        self.assertEqual({e["kind"] for e in self.story}, STORY_KINDS)
        for entry in self.story:
            self.assertEqual(self.entry_errors(entry), [], entry["kind"])

    def test_a_quest_fact_names_its_quest_and_nothing_else_does(self):
        started, step = next(e for e in self.story if e["kind"] == "quest_started"), next(e for e in self.story if e["kind"] == "step_done")
        cases = {
            "a quest fact without its quest": {k: v for k, v in started.items() if k != "quest"},
            "a step without its step": {**step, "quest": {k: v for k, v in step["quest"].items() if k != "step_id"}},
            "a quest start naming a step": {**started, "quest": {**started["quest"], "step_id": "meet_spud"}},
            "a quest fact with a subject": {**started, "subject": {"entities": ["avatar:player"], "name": "Player"}},
            "another fact naming a quest": {**started, "kind": "found", "quantity": {"count": 1}},
            "a note naming a quest": {"entry_id": started["entry_id"], "kind": "note", "at_utc": started["at_utc"], "revision": 1,
                                      "author": "companion:local", "text": "Quest done!", "untrusted": True, "quest": started["quest"]},
        }
        for name, entry in cases.items():
            with self.subTest(case=name):
                self.assertTrue(self.entry_errors(entry), name)

    def test_nothing_lets_a_companion_advance_or_write_a_quest(self):
        """The companion reads quests through journal.read, as the player does: no op names a quest or a story, a note is
        its text alone, and no request schema reaches the save's story block."""
        defs = self.schema["$defs"]
        for op in defs["command_op"]["enum"] + defs["query_op"]["enum"]:
            self.assertFalse(any(word in op for word in ("quest", "story", "step")), op)
        text = (CONTRACTS / "game-command.schema.json").read_text(encoding="utf-8")
        self.assertNotIn("x_story", text)
        note = validate.load_strict(EXAMPLES / "messages" / "valid" / "command_journal_note.json")
        for extra in ({"kind": "quest_done"}, {"quest": {"story_id": "example", "quest_id": "lost_button"}}):
            with self.subTest(extra=extra):
                self.assertTrue(validate.schema_errors({**note, "args": {**note["args"], **extra}}))


class ReadmeTests(unittest.TestCase):
    def test_the_readme_describes_the_story(self):
        readme = (CONTRACTS / "README.md").read_text(encoding="utf-8")
        self.assertIn("| Island story (story pack; the save's story block; the journal's quest kinds) | `island-story.schema.json`", readme)
        self.assertIn("## Island stories", readme)


if __name__ == "__main__":
    unittest.main()
