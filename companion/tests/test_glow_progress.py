"""The mock's game-owned Glow progress and its command boundary, shared by player and companion."""
from __future__ import annotations

import dataclasses
import json
import unittest

from support import (COMPANION, IN_THE_OPEN, NO_HOLDS, OUT_OF_REACH, PLAYER, REPO, FakeClock, cast, command,
                     contract_problems, glow, new_host, query)

from garage_game_companion.glow_progress import GlowProgress
from garage_game_companion.mock_host import rules_path


class GlowProgressBoundary(unittest.TestCase):
    def setUp(self):
        self.clock = FakeClock()
        self.host = new_host(clock=self.clock, policy=NO_HOLDS)

    def tearDown(self):
        for result in self.host.emitted:
            self.assertEqual(contract_problems(result), [], result)

    def send(self, message, principal=COMPANION):
        return self.host.handle(principal, message)

    def stop(self):
        self.assertTrue(self.send(command("effect.stop", {"effect": "all"}, "stop"), PLAYER)["ok"])

    def state(self):
        progress = self.host.glow_progress
        return progress.successful_uses, progress.discoveries, progress.earned_light_limit

    def seed(self, discoveries):
        # This is a trusted profile fixture, not a command the companion can send. Later variants have no triggers yet.
        self.host.glow_progress = GlowProgress(successful_uses=len(discoveries), discoveries=discoveries)

    def test_only_successful_new_glow_casts_count_and_only_self_discovers_lantern(self):
        self.assertTrue(self.send(glow("preview", preview=True))["ok"])
        refused = self.send(glow("far", at=OUT_OF_REACH))
        self.assertEqual(refused["error"]["code"], "out_of_bounds")
        self.assertTrue(self.send(cast("bubbles", "bubbles", at=IN_THE_OPEN))["ok"])
        self.assertEqual(self.state(), (0, frozenset(), 3))

        self.assertTrue(self.send(glow("point", at=IN_THE_OPEN))["ok"])
        self.assertEqual(self.state(), (1, frozenset(), 3))
        message = glow("lantern")
        self.assertTrue(self.send(message, PLAYER)["ok"])
        replay = self.send(message, PLAYER)
        self.assertTrue(replay["ok"] and replay["replayed"])
        conflict = self.send(glow("lantern", at=IN_THE_OPEN), PLAYER)
        self.assertEqual(conflict["error"]["code"], "action_id_conflict")
        self.assertEqual(self.state(), (2, frozenset({"lantern"}), 3))
        self.assertTrue(self.send(glow("third"))["ok"])
        self.assertEqual(self.send(glow("capped"))["error"]["code"], "budget_exceeded")
        self.assertEqual(self.state(), (3, frozenset({"lantern"}), 3))

    def test_held_cast_waits_and_approval_counts_once(self):
        self.host.policy = dataclasses.replace(NO_HOLDS, companion_approval_ops=frozenset({"effect.start"}))
        held = self.send(glow("held"))
        self.assertEqual(held["error"]["code"], "approval_required")
        self.assertEqual(self.state(), (0, frozenset(), 3))
        self.assertTrue(self.send(glow("held"))["replayed"])
        approved = self.host.player_decide(held["approval_needed"]["request_id"], approve=True)
        self.assertTrue(approved["result"]["ok"])
        self.assertEqual(self.state(), (1, frozenset({"lantern"}), 3))
        self.assertTrue(self.send(glow("held"))["replayed"])
        denied = self.send(glow("denied"))
        self.host.player_decide(denied["approval_needed"]["request_id"], approve=False)
        self.assertEqual(self.state(), (1, frozenset({"lantern"}), 3))

    def test_repeating_lantern_never_grants_another_discovery_or_a_fourth_light(self):
        for index in range(12):
            self.assertTrue(self.send(glow(f"lantern-{index}"), PLAYER if index % 2 else COMPANION)["ok"])
            self.stop()
        self.assertEqual(self.state(), (12, frozenset({"lantern"}), 3))

    def test_distinct_discoveries_earn_four_and_five_lights_across_both_principals(self):
        for discoveries, limit in ((["lantern", "torch"], 4),
                                   (["lantern", "torch", "bioluminesce", "constellation"], 4),
                                   (["lantern", "torch", "bioluminesce", "constellation", "skylight"], 5)):
            with self.subTest(discoveries=discoveries):
                self.stop()
                self.seed(discoveries)
                for index in range(limit):
                    result = self.send(glow(f"limit-{len(discoveries)}-{index}"), PLAYER if index % 2 else COMPANION)
                    self.assertTrue(result["ok"], result)
                refused = self.send(glow(f"over-{len(discoveries)}"))
                self.assertEqual((refused["error"]["code"], refused["error"]["allowed"], refused["error"]["actual"]),
                                 ("budget_exceeded", limit, limit))

    def test_earned_allowance_never_raises_the_pack_or_engine_ceiling(self):
        self.seed(["lantern", "torch", "bioluminesce", "constellation", "skylight"])
        self.host.load_rules(REPO / "game" / "rules" / "storybook_wild" / "v2.json")
        for index in range(3):
            self.assertTrue(self.send(glow(f"legacy-{index}"))["ok"])
        self.assertEqual(self.send(glow("legacy-over"))["error"]["allowed"], 3)

        self.host.load_rules(rules_path())
        # Four lights, the one Bubble (one at a time) and three fireworks fill the engine's eight, the bubble counted.
        for index, kind in enumerate(["glow"] * 4 + ["bubbles"] + ["fireworks"] * 3):
            self.assertTrue(self.send(cast(kind, f"engine-{index}", at=IN_THE_OPEN if kind == "bubbles" else None))["ok"], (index, kind))
        refused = self.send(glow("engine-over"))
        self.assertEqual((refused["error"]["allowed"], refused["error"]["actual"]), (8, 8))

    def test_progress_survives_effect_expiry_stop_room_and_rules_reload(self):
        self.assertTrue(self.send(glow("learn"))["ok"])
        before = self.state()
        self.clock.advance(61)
        self.assertEqual(self.host.active_effect_ids(), [])
        self.stop()
        self.host.load_rules(rules_path())
        self.host.load_room(self.host.room_dir)
        self.assertEqual(self.state(), before)
        self.assertEqual(self.host.active_effect_ids(), [])

    def test_ten_point_casts_deliver_only_the_implemented_lantern_hint_once(self):
        for index in range(12):
            self.assertTrue(self.send(glow(f"point-{index}", at=IN_THE_OPEN), PLAYER)["ok"])
            self.stop()
            expected = frozenset({"lantern_self"}) if index >= 9 else frozenset()
            self.assertEqual(self.host.glow_progress.delivered_hints, expected)
        self.assertEqual(self.state(), (12, frozenset(), 3))
        self.assertTrue(self.send(glow("discover-after-hint"), PLAYER)["ok"])
        self.host.load_room(self.host.room_dir)
        self.assertEqual(self.host.glow_progress.delivered_hints, frozenset({"lantern_self"}))
        self.assertEqual(self.state(), (13, frozenset({"lantern"}), 3))

    def test_wire_cannot_claim_progress_and_queries_reveal_no_reserved_recipes(self):
        claimed = glow("claim")
        claimed["args"]["discoveries"] = ["lantern", "torch"]
        self.assertFalse(self.send(claimed)["ok"])
        self.assertEqual(self.state(), (0, frozenset(), 3))
        listed = json.dumps(self.send(query("capabilities.list", {})))
        for recipe in ("torch", "bioluminesce", "constellation", "skylight"):
            self.assertNotIn(recipe, listed)


class GlowProgressState(unittest.TestCase):
    def test_restored_profile_accepts_only_known_discoveries_and_bounded_integer_counts(self):
        for value in (-1, True, 1.5, GlowProgress.MAX_SUCCESSFUL_USES + 1):
            with self.subTest(value=value), self.assertRaises(ValueError):
                GlowProgress(successful_uses=value)
        with self.assertRaises(ValueError):
            GlowProgress(discoveries=["anything_from_a_note"])
        with self.assertRaises(ValueError):
            GlowProgress(successful_uses=2, discoveries=["lantern", "lantern"])
        with self.assertRaises(ValueError):
            GlowProgress(successful_uses=1, discoveries=["lantern", "torch"])
        with self.assertRaises(ValueError):
            GlowProgress(successful_uses=9, delivered_hints=["lantern_self"])

    def test_use_count_saturates(self):
        progress = GlowProgress(successful_uses=GlowProgress.MAX_SUCCESSFUL_USES)
        progress.record_success(True)
        self.assertEqual(progress.successful_uses, GlowProgress.MAX_SUCCESSFUL_USES)
        self.assertEqual(progress.discoveries, frozenset({"lantern"}))
