"""The data-format ids (contracts/schema_ids.py; README "Schema ids"): writers write garage_game.<kind>, readers accept that
and the enfractal.<kind> written before the rename, and the C#, GDScript and Python tables agree.

  python -m unittest discover -s contracts/tests -v
"""
from __future__ import annotations

import copy
import json
import re
import sys
import unittest
from pathlib import Path

CONTRACTS = Path(__file__).resolve().parents[1]
ROOT = CONTRACTS.parent
sys.path.insert(0, str(CONTRACTS))

import validate  # noqa: E402

IDS = validate.SCHEMA_IDS
EXAMPLES = CONTRACTS / "examples"
# The two records written before the rename that keep their bytes (build_examples.py): the garage room with its assets,
# and the room state saved before Run 2, which pins that room. The later states carry its receipts and its creation forward.
PINNED_EXAMPLES = {
    "rooms/garage_example/room.json", "rooms/garage_example/objects/bean_bag/asset.json",
    "rooms/garage_example/objects/paint_clutter/asset.json", "rooms/garage_example/objects/pine_shelving/asset.json",
    "room_state/garage_example_state.json", "fixtures/creation_spinner.json",
    "room_state/garage_example_state_run2.json", "room_state/garage_example_state_mechanics.json",
    # Messages that say on purpose that a reader accepts the old spelling.
    "messages/valid/query_describe_room_with_the_old_id.json", "messages/valid/result_room_describe_with_the_old_id.json",
}
ANY_ID = re.compile(r'"((?:enfractal|garage_game)[.-][a-z_.-]+)"')


def every_id(value):
    """Every id-shaped string a document carries in a `schema` or `protocol` field, at any depth."""
    if isinstance(value, dict):
        for key, item in value.items():
            if key in ("schema", "protocol") and isinstance(item, str):
                yield item
            yield from every_id(item)
    elif isinstance(value, list):
        for item in value:
            yield from every_id(item)


class TheTable(unittest.TestCase):
    def test_each_kind_has_its_new_and_its_old_spelling(self):
        seen = set()
        for kind, (new, old) in IDS.IDS.items():
            with self.subTest(kind=kind):
                self.assertTrue(new.startswith("garage_game") and old.startswith("enfractal"), (new, old))
                self.assertEqual(new[len("garage_game"):], old[len("enfractal"):], "only the prefix changes")
                self.assertIn(new[len("garage_game"):][0], ".-")
                self.assertEqual(new[len("garage_game") + 1:] if kind != "world-state" else "world-state", kind)
                self.assertFalse({new, old} & seen)
                seen |= {new, old}
        self.assertEqual(IDS.GENERATOR, ("garage-game-roomscan", "enfractal-roomscan"))
        self.assertEqual((IDS.GLB_PART_KEYS, IDS.GLB_MATERIAL_KEYS),
                         (("garage_game_part", "enfractal_part"), ("garage_game_material", "enfractal_material")))

    def test_readers_accept_both_and_nothing_else(self):
        for kind, (new, old) in IDS.IDS.items():
            with self.subTest(kind=kind):
                self.assertTrue(IDS.is_schema(new, kind) and IDS.is_schema(old, kind))
                self.assertEqual((IDS.kind_of(new), IDS.kind_of(old)), (kind, kind))
                self.assertEqual((IDS.is_old(new), IDS.is_old(old)), (False, True))
                for wrong in (new.upper(), new + " ", " " + old, new.replace("garage_game", "garage-game", 1), "garagegame" + new[11:], old[:-1], None, 1, [new]):
                    self.assertFalse(IDS.is_schema(wrong, kind), wrong)
                    self.assertIsNone(IDS.kind_of(wrong), wrong)
        self.assertFalse(IDS.is_schema("garage_game.query", "command"), "one kind's id is not another's")
        with self.assertRaises(KeyError):
            IDS.is_schema("garage_game.room", "rooms")  # a misspelt kind is a mistake in the reader, never False


class TheContracts(unittest.TestCase):
    def test_every_schema_field_takes_exactly_both_spellings(self):
        def enums(node):
            if isinstance(node, dict):
                for key, value in node.items():
                    if key == "enum" and isinstance(value, list) and any(IDS.kind_of(v) for v in value):
                        yield value
                    if key == "const":
                        self.assertIsNone(IDS.kind_of(value), f"const {value!r}: an id is an enum of both spellings")
                    yield from enums(value)
            elif isinstance(node, list):
                for item in node:
                    yield from enums(item)
        found = 0
        for path in sorted(CONTRACTS.glob("*.schema.json")):
            schema = validate.load_strict(path)
            self.assertTrue(schema["$id"].startswith("https://contracts.garage-game.invalid/v1/"), path.name)
            for values in enums(schema):
                found += 1
                with self.subTest(schema=path.name, enum=values):
                    kinds = [IDS.kind_of(v) for v in values]
                    self.assertNotIn(None, kinds)
                    self.assertEqual(values, [IDS.id_of(k) for k in kinds[:len(kinds) // 2]] + [IDS.ids_of(k)[1] for k in kinds[:len(kinds) // 2]],
                                     "the new ids, then the old, in one order")
        self.assertGreaterEqual(found, 30)

    def test_the_validator_tables_hold_both_spellings(self):
        for kind, file_name in validate.KIND_FILES.items():
            new, old = IDS.ids_of(kind)
            self.assertEqual((validate.SCHEMA_FILES[new], validate.SCHEMA_FILES[old]), (file_name, file_name))
        for kind in ("command", "query", "result"):
            new, old = IDS.ids_of(kind)
            self.assertEqual(validate.MESSAGE_LIMITS[new], validate.MESSAGE_LIMITS[old])
        self.assertEqual(validate.PLAYTEST_MANIFEST, "garage_game.playtest_manifest")

    def test_examples_are_written_with_the_new_ids_but_the_pinned_records(self):
        old_carriers = set()
        for path in sorted(EXAMPLES.rglob("*.json")):
            relative = path.relative_to(EXAMPLES).as_posix()
            text = path.read_text(encoding="utf-8")
            spellings = ANY_ID.findall(text)
            if any(IDS.is_old(s) for s in spellings):
                old_carriers.add(relative)
            if "rules_invalid" in relative or "duplicate_keys" in relative or relative.startswith("messages/invalid"):
                continue
            try:
                document = validate.load_strict(path)
            except validate.ContractError:
                continue
            if isinstance(document, dict) and "schema" in document and relative not in PINNED_EXAMPLES:
                with self.subTest(example=relative):
                    self.assertFalse(IDS.is_old(document["schema"]), "written with the old id")
        self.assertEqual(old_carriers, PINNED_EXAMPLES)
        # The pinned records carry the old spelling of every id in them.
        for relative in ("rooms/garage_example/room.json", "room_state/garage_example_state.json"):
            spellings = list(every_id(validate.load_strict(EXAMPLES / relative)))
            self.assertTrue(spellings and all(IDS.is_old(s) for s in spellings), relative)

    def test_a_message_reads_in_either_spelling_and_in_no_other(self):
        for path in sorted((EXAMPLES / "messages" / "valid").glob("*.json")):
            document = validate.load_strict(path)
            kind = IDS.kind_of(document["schema"])
            with self.subTest(example=path.name):
                for spelling in IDS.ids_of(kind):
                    self.assertEqual(validate.schema_errors({**document, "schema": spelling}), [], spelling)
                for wrong in ("garagegame." + kind, "garage_game." + kind + "s", "enfractal-" + kind, kind):
                    problems = validate.schema_errors({**document, "schema": wrong})
                    self.assertEqual(problems, [f"$.schema: unknown schema {wrong!r}"])

    def test_a_document_is_checked_as_its_kind_in_either_spelling(self):
        team = validate.load_strict(EXAMPLES / "teams" / "example_team" / "team.json")
        twice = copy.deepcopy(team)
        twice["knacks"]["chosen"] = [twice["knacks"]["chosen"][0]] * 2 if twice["knacks"]["chosen"] else []
        for spelling in IDS.ids_of("team"):
            with self.subTest(spelling=spelling):
                self.assertEqual(validate.check_team_document({**team, "schema": spelling}, "team"), [])
                if twice["knacks"]["chosen"]:
                    self.assertTrue(validate.check_team_document({**twice, "schema": spelling}, "team"))
        result = validate.load_strict(EXAMPLES / "messages" / "valid" / "result_map_find.json")
        twice = copy.deepcopy(result)
        twice["data"]["items"] = twice["data"]["items"] * 2
        for spelling in IDS.ids_of("result"):
            with self.subTest(spelling=spelling):
                self.assertEqual(validate.check_message({**result, "schema": spelling}, "r"), [])
                self.assertTrue(validate.check_message({**twice, "schema": spelling}, "r"))
        self.assertEqual(validate.schema_errors(team, "team"), [])
        self.assertEqual(validate.schema_errors(team, "garage_game.team"), [])
        self.assertEqual(validate.schema_errors(team, "enfractal.team"), [])
        self.assertEqual(validate.schema_errors(team, "room"), ["$.schema: expected 'garage_game.room' (or 'enfractal.room'), found 'garage_game.team'"])

    def test_the_pinned_files_keep_their_old_ids_and_stay_valid(self):
        pinned = [ROOT / "game" / "rooms" / "test_room" / "room.json", ROOT / "game" / "styles" / "storybook_painterly" / "v1.json",
                  *sorted((ROOT / "game" / "rules" / "storybook_wild").glob("v*.json"))]
        for path in pinned:
            with self.subTest(path=path.relative_to(ROOT).as_posix()):
                self.assertTrue(IDS.is_old(validate.load_strict(path)["schema"]))
                self.assertEqual(validate.check_path(path if path.name != "room.json" else path.parent), [])

    def test_the_readme_lists_every_kind(self):
        readme = (CONTRACTS / "README.md").read_text(encoding="utf-8")
        section = readme[readme.index("## Schema ids"):]
        section = section[:section.index("\n## ", 4)]
        for kind, (new, old) in IDS.IDS.items():
            with self.subTest(kind=kind):
                self.assertIn(f"| `{new}` | `{old}` |", section)


class TheOtherTables(unittest.TestCase):
    """C# (game/scripts/native/SchemaIds.cs) and GDScript (game/scripts/schema_ids.gd) hold the same pairs as Python."""

    CSHARP = ROOT / "game" / "scripts" / "native" / "SchemaIds.cs"
    GDSCRIPT = ROOT / "game" / "scripts" / "schema_ids.gd"

    def pairs(self, text: str, pattern: str) -> dict[str, tuple[str, str]]:
        found = {}
        for kind, new, old in re.findall(pattern, text):
            self.assertNotIn(kind, found, f"{kind} twice")
            found[kind] = (new, old)
        return found

    def test_csharp_agrees(self):
        text = self.CSHARP.read_text(encoding="utf-8")
        constants = dict(re.findall(r'public const string (\w+) = "([^"]+)";', text))
        table = {}
        for kind, name, old in re.findall(r'\["([^"]+)"\] = \((\w+), "([^"]+)"\)', text):
            self.assertNotIn(kind, table)
            table[kind] = (constants[name], old)
        self.assertGreaterEqual(len(table), 20)
        for kind, pair in table.items():
            self.assertEqual(pair, IDS.IDS[kind], kind)
        self.assertEqual(re.findall(r'"(\w+_part)"', text)[:2], list(IDS.GLB_PART_KEYS))
        self.assertEqual(re.findall(r'"(\w+_material)"', text)[:2], list(IDS.GLB_MATERIAL_KEYS))

    def test_gdscript_agrees(self):
        table = self.pairs(self.GDSCRIPT.read_text(encoding="utf-8"), r'"([^"]+)": \["([^"]+)", "([^"]+)"\]')
        self.assertGreaterEqual(len(table), 5)
        for kind, pair in table.items():
            self.assertEqual(pair, IDS.IDS[kind], kind)


if __name__ == "__main__":
    unittest.main()
