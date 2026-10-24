"""Founder decision (6 October 2026): standard emoji markers are allowed in names and signs; every other
invisible character stays blocked.

VS15/VS16 only directly after a base that can take emoji presentation, one per base; ZWJ only between
two emoji; the keycap combiner only after 0-9, # or * (optionally with VS16). Runs and other placements
stay hidden, which defeats variation-selector smuggling. The vectors here are the same ones the
proposed contracts/validate.py and RoomData.cs tests use (docs/companion/proposals/).

Code points are written as escapes so this file stays plain ASCII.
"""
from __future__ import annotations

import random
import unittest

from support import COMPANION, HostPolicy, command, contract_problems, contracts, new_host, query

from mcp_harness import McpHarness

from garage_game_companion import textsafety
from garage_game_companion.contract import MARKER_PROBES, _ecma_regex, value_problems

TAGS_ENGLAND = "\U0001f3f4" + "".join(chr(0xE0000 + ord(c)) for c in "gbeng") + "\U000e007f"

# (name, text): every marker here is in place.
ALLOWED = [
    ("heart, emoji presentation", "❤️"),
    ("heart, text presentation", "❤︎"),
    ("smiley with a redundant VS16", "\U0001f600️"),
    ("copyright sign as emoji", "©️"),
    ("keycap one", "1️⃣"),
    ("keycap hash without a selector", "#⃣"),
    ("keycap star", "*️⃣"),
    ("digit, text presentation", "7︎"),
    ("family", "\U0001f468‍\U0001f469‍\U0001f467"),
    ("rainbow flag", "\U0001f3f3️‍\U0001f308"),
    ("technologist, medium skin tone", "\U0001f469\U0001f3fd‍\U0001f4bb"),
    ("handshake, two skin tones", "\U0001faf1\U0001f3fb‍\U0001faf2\U0001f3fc"),
    ("pirate flag", "\U0001f3f4‍☠️"),
    ("eye in speech bubble", "\U0001f441️‍\U0001f5e8️"),
    ("thumbs up, dark skin tone", "\U0001f44d\U0001f3ff"),
    ("flag of Japan", "\U0001f1ef\U0001f1f5"),
    ("a mug's name", "Mug ☕️"),
    ("letters and scripts", "Café 木の箱 עברית"),
]

# (name, text, the hidden characters it carries, display_text of it).
REFUSED = [
    ("VS16 after a letter", "a️", ["U+FE0F"], "a"),
    ("VS15 at the start", "︎abc", ["U+FE0E"], "abc"),
    ("two selectors on one emoji", "❤️️", ["U+FE0F"], "❤️"),
    ("text then emoji selector", "❤︎️", ["U+FE0F"], "❤︎"),
    ("a selector after a skin tone", "\U0001f44d\U0001f3ff️", ["U+FE0F"], "\U0001f44d\U0001f3ff"),
    ("another variation selector after an emoji", "❤︀", ["U+FE00"], "❤"),
    ("VS14 after an emoji", "❤︍", ["U+FE0D"], "❤"),
    ("a supplement selector after an emoji", "❤\U000e0100", ["U+E0100"], "❤"),
    ("a joiner between letters", "a‍b", ["U+200D"], "a b"),
    ("a joiner at the end", "\U0001f600‍", ["U+200D"], "\U0001f600"),
    ("a joiner at the start", "‍\U0001f600", ["U+200D"], "\U0001f600"),
    ("two joiners", "\U0001f468‍‍\U0001f469", ["U+200D", "U+200D"], "\U0001f468 \U0001f469"),
    ("a joiner before a letter", "\U0001f468‍x", ["U+200D"], "\U0001f468 x"),
    ("a joiner after a text selector", "❤︎‍\U0001f525", ["U+200D"], "❤︎ \U0001f525"),
    ("a keycap on a letter", "A⃣", ["U+20E3"], "A"),
    ("a keycap alone", "⃣", ["U+20E3"], "unnamed"),
    ("two keycaps", "1⃣⃣", ["U+20E3"], "1⃣"),
    ("a keycap after a text selector", "1︎⃣", ["U+20E3"], "1︎"),
    ("a tag-sequence flag (England)", TAGS_ENGLAND, ["U+E0067", "U+E0062", "U+E0065", "U+E006E", "U+E0067", "U+E007F"],
     "\U0001f3f4"),
    ("smuggling in a run of selectors", "\U0001f600︆︈︆︉",
     ["U+FE06", "U+FE08", "U+FE06", "U+FE09"], "\U0001f600"),
    ("smuggling in supplement selectors", "\U0001f600\U000e0158\U000e0159", ["U+E0158", "U+E0159"], "\U0001f600"),
    ("smuggling bits in emoji selectors", "\U0001f600︎️️︎", ["U+FE0F", "U+FE0F", "U+FE0E"],
     "\U0001f600︎"),
    ("a zero-width non-joiner", "a‌b", ["U+200C"], "a b"),
    ("a word joiner", "a⁠b", ["U+2060"], "a b"),
]


class Rule(unittest.TestCase):
    def test_emoji_markers_in_place_are_not_hidden(self):
        for name, text in ALLOWED:
            with self.subTest(name=name):
                self.assertEqual(textsafety.hidden_characters(text), [])
                self.assertEqual(textsafety.display_text(text, 80), text)
                self.assertEqual(textsafety.long_text(text, 500), text)
                self.assertEqual(value_problems({"note": text}), [])

    def test_markers_out_of_place_and_other_invisible_characters_are_hidden(self):
        for name, text, hidden, shown in REFUSED:
            with self.subTest(name=name):
                self.assertEqual(textsafety.hidden_characters(text), hidden)
                self.assertEqual(textsafety.display_text(text, 80), shown)
                self.assertEqual(value_problems({"note": text}), [(["note"], "hidden_text")])

    def test_a_run_leaves_at_most_one_selector_per_emoji(self):
        payload = "".join("︎" if bit == "0" else "️" for bit in format(0x5A5A, "016b"))
        shown = textsafety.display_text("\U0001f600" + payload, 80)
        self.assertEqual(len(shown), 2)
        self.assertEqual(textsafety.hidden_characters("\U0001f600" + payload), [f"U+{ord(c):04X}" for c in payload[1:]])

    def test_truncation_never_strands_a_joiner_or_a_selector(self):
        family = "\U0001f468‍\U0001f469‍\U0001f467"
        for length in range(1, len(family) + 1):
            with self.subTest(length=length):
                shown = textsafety.display_text(family, length)
                self.assertEqual(textsafety.hidden_characters(shown), [])
                self.assertLessEqual(len(shown), length)
        self.assertEqual(textsafety.long_text("ok \U0001f468‍\U0001f469", 5), "ok \U0001f468 ")

    def test_cleaning_is_final_and_leaves_nothing_hidden(self):
        """Random strings of bases and markers: one cleaning leaves nothing hidden, and a second changes nothing."""
        alphabet = ["a", "1", "#", " ", "\n", "❤", "\U0001f600", "\U0001f468", "\U0001f3fd", "\U0001f1ef",
                    "︎", "️", "︀", "‍", "‌", "⃣", "\U000e0041", "\U000e0100", "­"]
        rng = random.Random(20261006)
        for rules in (textsafety.RULES, textsafety.TextRules({textsafety.VS16}), textsafety.TextRules(set()),
                      contracts().text_rules):
            for _ in range(3000):
                text = "".join(rng.choice(alphabet) for _ in range(rng.randint(0, 12)))
                for length in (4, 80):
                    shown = rules.display_text(text, length)
                    self.assertEqual(rules.hidden_characters(shown), [], (ascii(text), ascii(shown)))
                    self.assertEqual(rules.display_text(shown, length), shown, ascii(text))
                    long = rules.long_text(text, length)
                    self.assertEqual(rules.hidden_characters(long, allow_newlines=True), [], ascii(text))
                    self.assertEqual(rules.long_text(long, length), long, ascii(text))

    def test_a_rule_without_a_marker_hides_it_everywhere(self):
        rules = textsafety.TextRules(textsafety.EMOJI_MARKERS - {textsafety.ZWJ})
        self.assertEqual(rules.hidden_characters("\U0001f468‍\U0001f469"), ["U+200D"])
        self.assertEqual(rules.display_text("\U0001f468‍\U0001f469", 80), "\U0001f468 \U0001f469")
        no_vs16 = textsafety.TextRules(textsafety.EMOJI_MARKERS - {textsafety.VS16})
        self.assertEqual(no_vs16.hidden_characters("\U0001f3f3️‍\U0001f308"), ["U+FE0F", "U+200D"])
        self.assertEqual(no_vs16.hidden_characters("1️⃣"), ["U+FE0F", "U+20E3"])

    def test_the_pictographic_table_is_sorted_and_disjoint(self):
        table = textsafety.EXTENDED_PICTOGRAPHIC
        self.assertEqual(len(table), 78)
        for (low, high), (next_low, _next_high) in zip(table, table[1:]):
            self.assertLessEqual(low, high)
            self.assertLess(high + 1, next_low)
        for code in (0x00A9, 0x2764, 0x1F600, 0x1F3F4, 0x1FAF2, 0x1FFFD):
            self.assertTrue(textsafety.is_pictographic(code), hex(code))
        for code in (0x0031, 0x00AA, 0x1F1EF, 0x1F3FB, 0x1F3FF, 0x1FFFE, 0xE0067):
            self.assertFalse(textsafety.is_pictographic(code), hex(code))


class ContractAware(unittest.TestCase):
    def test_the_host_emits_only_markers_its_contract_accepts(self):
        rules = contracts().text_rules
        defs = contracts().common_schema["$defs"]
        for marker, probe in MARKER_PROBES.items():
            accepted = all(_ecma_regex(defs[name]["pattern"]).search(probe) for name in ("display_text", "long_text"))
            self.assertEqual(marker in rules.markers, accepted, hex(marker))

    def test_world_names_and_signs_keep_their_emoji_and_lose_the_rest(self):
        host = new_host()
        family = "\U0001f468‍\U0001f469‍\U0001f467"
        host.rename_entity("obj:book", "Mug ☕️️")
        host.rename_entity("obj:box", "Family " + family)
        host.add_world_text("obj:book", "Keycap 1️⃣ and smuggled \U0001f600︆︈ " + TAGS_ENGLAND)
        observed = host.handle(COMPANION, query("observe", {"actor": "avatar:companion"}))
        self.assertEqual(contract_problems(observed), [])
        names = {v["id"]: v["display_name"] for v in observed["data"]["visible"]}
        self.assertEqual(names["obj:book"], "Mug ☕️")
        joined = textsafety.ZWJ in contracts().text_rules.markers
        self.assertEqual(names["obj:box"], "Family " + (family if joined else "\U0001f468 \U0001f469 \U0001f467"))
        self.assertEqual(observed["data"]["texts"][0]["text"],
                         "Keycap 1️⃣ and smuggled \U0001f600   \U0001f3f4" + " " * 6)

    def test_requests_with_emoji_pass_and_requests_with_hidden_markers_are_refused(self):
        host = new_host(policy=HostPolicy(companion_messages_per_s=1_000_000))
        ok = host.handle(COMPANION, command("goal.set", {"actor": "avatar:companion", "goal": "stay"}, "g-1",
                                            note="Back soon ❤️ 1️⃣"))
        self.assertTrue(ok["ok"], ok)
        for i, (_name, text, _hidden, _shown) in enumerate(REFUSED):
            with self.subTest(name=_name):
                result = host.handle(COMPANION, command("goal.set", {"actor": "avatar:companion", "goal": "stay"},
                                                        f"g-r{i}", note="x" + text))
                self.assertEqual(result["error"]["code"], "request_invalid", result)
                self.assertEqual(result["error"]["field_path"], "$.note")


class ThroughTheAdapter(unittest.IsolatedAsyncioTestCase):
    async def test_the_adapter_passes_emoji_and_refuses_hidden_markers_before_sending(self):
        async with McpHarness() as h:
            ok = await h.call("goal_set", {"action_id": "g-1", "goal": "stay", "note": "Mug ☕️"})
            self.assertTrue(ok["ok"], ok)
            sent = len(h.host.emitted)
            refused = await h.call("goal_set", {"action_id": "g-2", "goal": "stay", "note": "Mug️️"})
            self.assertEqual(refused["error"]["code"], "request_invalid")
            self.assertEqual(len(h.host.emitted), sent, "refused before anything reached the game")


if __name__ == "__main__":
    unittest.main()
