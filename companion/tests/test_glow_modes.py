"""New command parity: host-observed Glow, cosmetic touches, mode isolation, nature checked then unavailable."""
from __future__ import annotations

import copy
import unittest

from support import (COMPANION, IN_THE_OPEN, NO_HOLDS, PLAYER, FakeClock, cast, command,
                     contract_problems, glow, new_host, query)
from garage_game_companion.glow_progress import GlowProgress


class GlowAndModes(unittest.TestCase):
    def setUp(self):
        self.clock = FakeClock()
        self.host = new_host(clock=self.clock, policy=NO_HOLDS)
        self.serial = 0

    def tearDown(self):
        for result in self.host.emitted:
            self.assertEqual(contract_problems(result), [], result)

    def send(self, op, args, principal=PLAYER, **options):
        self.serial += 1
        self.clock.advance(0.1)
        return self.host.handle(principal, command(op, args, f"extended-{self.serial}", **options))

    def cast(self, *, point=None, principal=PLAYER, variant=None, touch=None):
        self.serial += 1
        message = glow(f"extended-glow-{self.serial}", at=point)
        if variant is not None:
            message["args"]["variant"] = variant
        if touch is not None:
            message["args"]["touch"] = touch
        return self.host.handle(principal, message)

    def stop(self):
        self.assertTrue(self.send("effect.stop", {"effect": "all"})["ok"])

    def test_touch_and_audio_configuration_is_player_owned_previewed_and_applies_to_future_casts(self):
        args = {"touch": "chime", "sound_volume": 0.2, "muted": True}
        preview = self.send("glow.configure", args, preview=True)
        self.assertEqual(preview["data"], args)
        self.assertEqual(self.host.glow_progress.touch, "plain")
        self.assertEqual(self.send("glow.configure", args, COMPANION)["error"]["code"], "permission_denied")
        self.assertTrue(self.send("glow.configure", args)["ok"])
        result = self.cast()
        self.assertEqual((result["data"]["variant"], result["data"]["touch"]), ("lantern", "chime"))
        self.assertEqual((self.host.glow_progress.sound_volume, self.host.glow_progress.muted), (0.2, True))
        self.assertEqual(self.host.glow_progress.discoveries, {"lantern"})
        explicit = self.cast(touch="ember")
        self.assertEqual(explicit["data"]["touch"], "ember")
        self.assertEqual(self.host.glow_progress.touch, "chime", "a cast's touch does not rewrite the player's preference")

    def test_configuration_rejects_invalid_values_without_publication(self):
        for args in ({}, {"touch": "fire"}, {"sound_volume": 2}, {"sound_volume": True}, {"muted": "yes"}):
            with self.subTest(args=args):
                self.assertFalse(self.send("glow.configure", args)["ok"])
        self.assertEqual((self.host.glow_progress.touch, self.host.glow_progress.sound_volume), ("plain", 0.35))

    def test_real_cast_constellation_counts_once_and_earns_the_fourth_light_after_lantern(self):
        self.assertTrue(self.cast()["ok"])
        self.stop()
        ids = []
        for offset in (0, 0.08, 0.16):
            point = [IN_THE_OPEN[0] + offset, 0, IN_THE_OPEN[2]]
            result = self.cast(point=point, variant="plain")
            self.assertTrue(result["ok"], result)
            ids.append(result["created"][0])
        self.assertEqual(self.host.glow_progress.discoveries, {"lantern", "constellation"})
        self.assertEqual(self.host.glow_progress.earned_light_limit, 4)
        for effect_id in ids:
            self.assertEqual(self.host.effects[effect_id].connections, set(ids) - {effect_id})
        listed = self.host.handle(COMPANION, query("capabilities.list", {"category": "light"}))
        self.assertEqual(listed["data"]["items"][0]["max_active"], 4)

    def test_constellations_do_not_combine_different_casters(self):
        self.cast(point=IN_THE_OPEN, principal=PLAYER)
        self.cast(point=IN_THE_OPEN, principal=COMPANION)
        self.cast(point=IN_THE_OPEN, principal=COMPANION)
        self.assertNotIn("constellation", self.host.glow_progress.discoveries)

    def test_overlapping_lights_do_not_farm_the_constellation_discovery(self):
        for _ in range(3):
            self.assertTrue(self.cast(point=IN_THE_OPEN, variant="plain")["ok"])
        self.assertNotIn("constellation", self.host.glow_progress.discoveries)
        self.assertTrue(all(not effect.connections for effect in self.host.effects.values()))

    def test_glowing_bubbles_finds_no_stream_since_the_bubble_redesign(self):
        """Glow's glowing_bubbles lit the stream of small bubbles, which the founder's redesign removed (decision 55): with only
        the one Bubble now it finds no stream and is refused, until the founder decides whether it lights the Bubble."""
        refused = self.cast(variant="glowing_bubbles")
        self.assertEqual(refused["error"]["code"], "target_not_found")
        bubble = self.host.handle(PLAYER, cast("bubbles", "the-bubble", at=IN_THE_OPEN))
        self.assertTrue(bubble["ok"], bubble)
        again = self.cast(variant="glowing_bubbles", touch="frost")
        self.assertEqual((again["error"]["code"], again["error"]["field_path"]), ("target_not_found", "$.args.variant"))
        self.assertEqual((self.host.active_effect_ids(), self.host.glow_progress.discoveries), ([], set()))

    def test_adjustment_requires_fifth_light_checks_ownership_and_previews_without_change(self):
        first = self.cast(point=IN_THE_OPEN, variant="plain")["created"][0]
        args = {"effect": first, "center_m": [IN_THE_OPEN[0] + 0.05, 0, IN_THE_OPEN[2]], "connections": []}
        self.assertEqual(self.send("effect.adjust", args)["error"]["code"], "permission_denied")
        self.host.glow_progress = GlowProgress(5, GlowProgress.DISCOVERY_ORDER)
        second = self.cast(point=IN_THE_OPEN, variant="plain")["created"][0]
        args["connections"] = [second]
        original = copy.deepcopy(self.host.effects[first])
        self.assertTrue(self.send("effect.adjust", args, preview=True)["ok"])
        self.assertEqual(self.host.effects[first], original)
        self.assertEqual(self.send("effect.adjust", args, COMPANION)["error"]["code"], "permission_denied")
        moved = self.send("effect.adjust", args)
        self.assertEqual(moved["data"], args)
        self.assertEqual(self.host.effects[first].connections, {second})
        self.assertEqual(self.host.effects[second].connections, {first})
        self.assertTrue(self.send("effect.adjust", {"effect": first, "connections": []})["ok"])
        self.assertEqual(self.host.effects[second].connections, set())
        bad = self.send("effect.adjust", {"effect": first, "connections": [first]})
        self.assertEqual(bad["error"]["code"], "target_not_found")

    def test_mode_changes_are_player_owned_and_preserve_separate_progress_and_configuration(self):
        self.cast()
        self.send("glow.configure", {"touch": "frost"})
        self.assertTrue(self.send("world.set_mode", {"mode": "challenge"}, preview=True)["ok"])
        self.assertEqual(self.host.mode, "creative")
        self.assertEqual(self.send("world.set_mode", {"mode": "challenge"}, COMPANION)["error"]["code"], "permission_denied")
        self.assertTrue(self.send("world.set_mode", {"mode": "challenge"})["ok"])
        self.assertEqual(self.host.glow_progress.discoveries, set())
        self.assertEqual(self.host.glow_progress.touch, "plain")
        self.assertEqual(self.host.active_effect_ids(), [])
        status = self.host.handle(COMPANION, query("player.status", {}))["data"]
        # A new body is the pack's (100 health either way; the first band 0.45 m today, 0.4 m with v5's traits, which add
        # the pool, the hearts and the bar: test_mechanics_boundary).
        bar = self.host.rules.stamina_start_m if self.host.rules.stamina_start_m is not None else 0.45
        self.assertEqual({k: v for k, v in status.items() if k not in ("energy", "hearts", "bar")},
                         {"mode": "challenge", "health": 100, "max_health": 100, "stamina": bar,
                          "max_stamina": bar, "completed_climbs": 0, "recoveries": 0, "consequences": True})
        # Trusted saved-state fixture: wire messages cannot set these values.
        self.host._vitals_profiles["challenge"]["health"] = 80
        self.assertTrue(self.send("world.set_mode", {"mode": "live"})["ok"])
        self.assertEqual(self.host.handle(COMPANION, query("player.status", {}))["data"]["health"], 100)
        self.send("world.set_mode", {"mode": "creative"})
        self.assertEqual((self.host.glow_progress.discoveries, self.host.glow_progress.touch), ({"lantern"}, "frost"))
        self.send("world.set_mode", {"mode": "challenge"})
        self.host.load_room(self.host.room_dir)
        self.assertEqual(self.host.handle(COMPANION, query("player.status", {}))["data"]["health"], 80)

    def test_native_only_features_refuse_explicitly_without_fake_receipts_or_discoveries(self):
        """What needs the game itself is unavailable here (the contract's code for a host that cannot), never a fake receipt."""
        for variant in ("torch", "bioluminesce", "skylight"):
            with self.subTest(variant=variant):
                result = self.cast(point=IN_THE_OPEN, variant=variant)
                self.assertEqual(result["error"]["code"], "unavailable")
                self.assertIn("needs the game itself", result["error"]["message"])
        messages = [
            ("nature.cast", {"capability": "grow", "center_m": IN_THE_OPEN, "radius_m": 0.25, "duration_s": 30}),
            ("nature.repair", {"target": "creation:unbuilt"}),
            ("wish.submit", {"text": "Help me make a garden"}),
            ("wish.stop", {}),
        ]
        before = (self.host.revision, len(self.host.receipts), len(self.host.transient))
        for op, args in messages:
            with self.subTest(op=op):
                result = self.send(op, args)
                self.assertEqual(result["error"]["code"], "unavailable", result)
                self.assertIn("needs the game itself", result["error"]["message"])
        for op in ("nature.describe", "wishes.read"):
            result = self.host.handle(PLAYER, query(op, {}))
            self.assertEqual(result["error"]["code"], "unavailable")
        self.assertEqual(self.host.handle(COMPANION, query("wishes.read", {}))["error"]["code"], "permission_denied")
        self.assertEqual((self.host.revision, len(self.host.receipts), len(self.host.transient)), before)
        self.assertEqual(self.host.glow_progress.discoveries, set())
        items = self.host.handle(COMPANION, query("capabilities.list", {}))["data"]["items"]
        for item in items:
            nature = item["capability"] in {"grow", "fire"}
            self.assertNotIn("native_required", item)
            self.assertEqual(item["command_op"], "nature.cast" if nature else "effect.start")


def outcome(result: dict):
    return "ok" if result["ok"] else (result["error"]["code"], result["error"].get("field_path"))


class NatureInTheMock(unittest.TestCase):
    """The mock does not simulate nature yet (contracts/README.md "Nature"): it checks the nature commands as the kernel does
    before its nature runs (CommandHostNature.CheckNatureArgs; the expectations; a companion's repair target in sight), then
    answers unavailable. room.undo, with no nature change to take back, answers as the kernel does for any other change."""

    BLOOM = {"capability": "grow", "center_m": IN_THE_OPEN, "radius_m": 0.25, "duration_s": 30}

    def setUp(self):
        self.clock = FakeClock()
        self.host = new_host(clock=self.clock, policy=NO_HOLDS)
        self.serial = 0

    def tearDown(self):
        for result in self.host.emitted:
            self.assertEqual(contract_problems(result), [], result)

    def send(self, op, args, principal=COMPANION, **options):
        self.serial += 1
        self.clock.advance(0.1)
        return outcome(self.host.handle(principal, command(op, args, f"nature-{self.serial}", **options)))

    def test_the_nature_commands_args_are_refused_as_the_kernel_refuses_them(self):
        bloom = self.BLOOM
        cases = {
            "an argument it does not know": ("nature.cast", {**bloom, "intensity": 2}, {}, ("field_unknown", "$.args.intensity")),
            "no capability": ("nature.cast", {k: v for k, v in bloom.items() if k != "capability"}, {}, ("request_invalid", "$.args.capability")),
            "an effect, not nature": ("nature.cast", {**bloom, "capability": "glow"}, {}, ("request_invalid", "$.args.capability")),
            "a radius past the engine's": ("nature.cast", {**bloom, "radius_m": 2}, {}, ("request_invalid", "$.args.radius_m")),
            "no duration": ("nature.cast", {**bloom, "duration_s": 0}, {}, ("request_invalid", "$.args.duration_s")),
            "a place of two numbers": ("nature.cast", {**bloom, "center_m": [0, 0]}, {}, ("request_invalid", "$.args.center_m")),
            "a place far outside": ("nature.cast", {**bloom, "center_m": [0, 0, 5000]}, {}, ("request_invalid", "$.args.center_m")),
            "a made-up preview id": ("nature.cast", {**bloom, "preview_id": "preview-1"}, {}, ("request_invalid", "$.args.preview_id")),
            "a trail that is neither": ("nature.trail", {"enabled": "yes"}, {}, ("request_invalid", "$.args.enabled")),
            "a repair of an object": ("nature.repair", {"target": "obj:box"}, {}, ("request_invalid", "$.args.target")),
            "a stale revision": ("nature.trail", {"enabled": False}, {"expected_revision": 9}, ("revision_conflict", "$.expected_revision")),
            "a repair of a creation out of sight": ("nature.repair", {"target": "creation:nowhere"}, {}, ("target_not_found", "$.args.target")),
        }
        for label, (op, args, options, expected) in cases.items():
            with self.subTest(label):
                self.assertEqual(self.send(op, args, **options), expected)

    def test_valid_nature_commands_are_unavailable_and_change_nothing(self):
        # Decision 72: Fire is valid only once the team has learned it (the kernel's first rest; the mock host's own learn_fire).
        self.host.learn_fire()
        before = (self.host.revision, len(self.host.receipts), len(self.host.transient))
        fire = {**self.BLOOM, "capability": "fire"}
        for op, args, options in (("nature.cast", self.BLOOM, {}), ("nature.cast", fire, {"preview": True}),
                                  ("nature.trail", {"enabled": False}, {}), ("nature.repair", {"target": "creation:unbuilt"}, {})):
            for principal in (PLAYER, COMPANION):
                with self.subTest(op=op, principal=principal, **options):
                    unseen = op == "nature.repair" and principal == COMPANION
                    # The flower trail went with Bloom (decision 52): refused in the kernel's words, never unavailable.
                    expected = (("target_not_found", "$.args.target") if unseen else ("unsupported_capability", "$.op") if op == "nature.trail"
                                else ("unavailable", "$.op"))
                    self.assertEqual(self.send(op, args, principal, **options), expected)
        self.assertEqual(outcome(self.host.handle(COMPANION, query("nature.describe", {}))), ("unavailable", "$.op"))
        self.assertEqual((self.host.revision, len(self.host.receipts), len(self.host.transient)), before)

    def test_undo_takes_back_only_nature_changes_so_here_nothing(self):
        """The kernel's room.undo (NatureUndo): building, moving or protecting cannot be undone, by anyone."""
        self.assertEqual(self.send("entity.place", {"target": "obj:book", "placement": {"position_m": [0.3, 0, 0.3]}}, PLAYER), "ok")
        revision = self.host.revision
        before = copy.deepcopy(self.host._snapshot())
        for principal in (PLAYER, COMPANION):
            with self.subTest(principal=principal):
                self.assertEqual(self.send("room.undo", {"to_revision": revision - 1}, principal, expected_revision=revision),
                                 ("unsupported_capability", "$.args.to_revision"))
                self.assertEqual(self.send("room.undo", {"to_revision": revision}, principal, expected_revision=revision),
                                 ("invalid_args", "$.args.to_revision"))
                self.assertEqual(self.send("room.undo", {"to_revision": 0}, principal, expected_revision=revision - 1),
                                 ("revision_conflict", "$.expected_revision"))
        self.assertEqual((self.host.revision, self.host._snapshot()), (revision, before))
        self.assertEqual(self.host.entities["obj:book"].position, [0.3, 0, 0.3])

    def test_outside_creative_there_is_no_undo_at_all(self):
        """The founder, 13 October: what happens in Challenge and Live stays. Refused before anything else, stale revisions
        and previews included, as the kernel's Unsupported refuses it."""
        self.assertEqual(self.send("entity.place", {"target": "obj:book", "placement": {"position_m": [0.3, 0, 0.3]}}, PLAYER), "ok")
        revision = self.host.revision
        for mode in ("challenge", "live"):
            self.assertEqual(self.send("world.set_mode", {"mode": mode}, PLAYER), "ok")
            for principal in (PLAYER, COMPANION):
                for to_revision, expected, options in ((revision - 1, revision, {}), (revision, revision, {}), (0, 99, {}),
                                                       (revision - 1, revision, {"preview": True})):
                    with self.subTest(mode=mode, principal=principal, to_revision=to_revision, expected=expected, **options):
                        self.assertEqual(self.send("room.undo", {"to_revision": to_revision}, principal, expected_revision=expected, **options),
                                         ("unsupported_capability", "$.op"))
        self.assertEqual(self.send("world.set_mode", {"mode": "creative"}, PLAYER), "ok")
        self.assertEqual(self.send("room.undo", {"to_revision": revision - 1}, PLAYER, expected_revision=revision),
                         ("unsupported_capability", "$.args.to_revision"))
        self.assertEqual(self.host.entities["obj:book"].position, [0.3, 0, 0.3])


class ProgressiveHints(unittest.TestCase):
    def test_hints_follow_missing_discoveries_at_ten_use_intervals_and_never_repeat(self):
        progress = GlowProgress()
        for _ in range(50):
            progress.record_observed(None)
        self.assertEqual(progress.delivered_hints, set(GlowProgress.HINT_ORDER))
        for _ in range(10):
            progress.record_observed("lantern")
        self.assertEqual(progress.delivered_hints, set(GlowProgress.HINT_ORDER))
        self.assertEqual(progress.discoveries, {"lantern"})
