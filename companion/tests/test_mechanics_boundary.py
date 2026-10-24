"""The mechanics round's contract at the companion boundary (M0, 14 October 2026; docs/companion/SECURITY.md, the rows marked
planned for M0, and the row built with M9): the player's new ops have no tool and refuse the companion, a Fractal is never the
companion's to gather, the Bubbles carry lands on the player's avatar only under the targets rule (the preview or the step-in the
consent, the dive key or X the exit), and no field of a request changes a price.

test_host_alignment.py holds the companion's side of these on both hosts; this file holds the player's side on the mock, the
adapter's refusals before anything is sent, and the prices the mock reads from the pack.
"""
from __future__ import annotations

import dataclasses
import inspect
import unittest

from mcp_harness import McpHarness
from support import COMPANION, GUBBLE, IN_THE_OPEN, PLAYER, command, contract_problems, contracts, new_host, query

from garage_game_companion import mock_host, refusals
from garage_game_companion.mock_host import load_rules, rules_path

NEW_PLAYER_OPS = {
    "item.pickup": {"target": "fractal:k3x9q2"},
    "item.gather": {"target": "obj:book"},
    "knack.set": {"slot": 1, "knack": "quick_feet"},
    "receipt.mark": {"action_id": "grow-0002", "mark": "not_that"},
    "trust.reset": {},
    "fractal.spend": {"grow": "heart"},
}
CARRY = {"capability": "bubble_carry", "center_m": IN_THE_OPEN, "radius_m": 0.08, "duration_s": 20, "target": "avatar:player"}


def refusal(result: dict):
    return "ok" if result["ok"] else (result["error"]["code"], result["error"].get("field_path"), result["error"]["message"])


class OnTheMock(unittest.TestCase):
    def setUp(self):
        self.host = new_host()
        self.n = 0

    def tearDown(self):
        for result in self.host.emitted:
            self.assertEqual(contract_problems(result), [], result)

    def send(self, principal, op, args, **extra):
        self.n += 1
        return self.host.handle(principal, command(op, args, f"m0-{self.n}", **extra))

    def test_the_new_ops_are_the_players_and_not_built_yet(self):
        """The companion meets permission_denied for every one; the player's receipt.mark and trust.reset are hosted since M8
        (test_trust.py), the rest answer as not available yet on the mock."""
        for op, args in NEW_PLAYER_OPS.items():
            with self.subTest(op=op):
                self.assertEqual(refusal(self.send(COMPANION, op, args))[:2], ("permission_denied", "$.op"))
                if op in mock_host.MECHANICS_HOSTED:
                    continue
                self.assertEqual(refusal(self.send(PLAYER, op, args)), ("unsupported_capability", "$.op", mock_host.MECHANICS_NOT_YET))
        self.assertEqual(mock_host.MECHANICS_HOSTED, {"receipt.mark", "trust.reset"})
        # The player's own: a mark of no receipt of the Gubble's is not found; a reset is a transient receipt with no data.
        self.assertEqual(refusal(self.send(PLAYER, "receipt.mark", NEW_PLAYER_OPS["receipt.mark"]))[:2], ("target_not_found", "$.args.action_id"))
        reset = self.send(PLAYER, "trust.reset", {})
        self.assertTrue(reset["ok"] and reset["transient"] and "data" not in reset, reset)

    def test_a_difficulty_is_chosen_at_the_menu_and_the_mode_alone_still_changes_today(self):
        self.assertEqual(refusal(self.send(PLAYER, "world.set_mode", {"mode": "challenge", "difficulty": "brave"})),
                         ("unsupported_capability", "$.op", mock_host.DIFFICULTY_NOT_YET))
        # Until the teams packet (M3) refuses it after boot, the mode alone changes as before.
        self.assertTrue(self.send(PLAYER, "world.set_mode", {"mode": "challenge"})["ok"])
        self.assertEqual(self.host.mode, "challenge")

    def test_the_companion_never_gathers_a_fractal_and_the_answer_says_nothing_about_it(self):
        answers = []
        for target in ("fractal:k3x9q2", "fractal:zz9", "fractal:" + "q" * 32):
            for op, args in (("entity.grab", {"target": target, "actor": GUBBLE}),
                             ("goal.set", {"actor": GUBBLE, "goal": "fetch", "target": target}),
                             ("entity.place", {"target": target, "placement": {"position_m": IN_THE_OPEN}}),
                             ("entity.push", {"target": target, "distance_m": 0.2, "actor": GUBBLE})):
                with self.subTest(op=op, target=target):
                    answer = refusal(self.send(COMPANION, op, args))
                    self.assertEqual(answer, ("permission_denied", "$.args.target", mock_host.FRACTAL_REFUSAL))
                    answers.append(answer)
        self.assertEqual(len(set(answers)), 1, "the same answer whether or not the Fractal exists")
        # Finding is the Gubble's: it may go to one (here none is placed, so it is an unknown id like any other).
        self.assertEqual(refusal(self.send(COMPANION, "goal.set", {"actor": GUBBLE, "goal": "go_to", "target": "fractal:k3x9q2"}))[0],
                         "target_not_found")
        # The player is not refused for gathering: a Fractal no room places is simply not there.
        self.assertEqual(refusal(self.send(PLAYER, "entity.grab", {"target": "fractal:k3x9q2"}))[0], "target_not_found")

    def test_the_targets_rule(self):
        self.assertEqual(refusal(self.send(COMPANION, "nature.cast", CARRY))[:2], ("request_invalid", "$"), "a carry on an avatar is previewed first")
        # M9: a carry on the player is centred where they stand; previewed, it answers the ghost's preview_id.
        self.assertEqual(refusal(self.send(COMPANION, "nature.cast", CARRY, preview=True))[:2], ("invalid_args", "$.args.center_m"))
        on_them = {**CARRY, "center_m": list(self.host.entities["avatar:player"].position)}
        previewed = self.send(COMPANION, "nature.cast", on_them, preview=True)
        self.assertTrue(previewed["ok"] and len(previewed["data"]["preview_id"]) == 32, previewed)
        self.assertEqual(refusal(self.send(COMPANION, "nature.cast", {**on_them, "preview_id": "a" * 32})),
                         ("revision_conflict", "$.args.preview_id", mock_host.BUBBLE_PREVIEW_AGAIN))
        for capability in ("grow", "fire", "bubble_boat"):
            with self.subTest(capability=capability):
                self.assertEqual(refusal(self.send(COMPANION, "nature.cast", {**CARRY, "capability": capability}, preview=True))[:2],
                                 ("request_invalid", "$.args.capability"))
        self.assertEqual(refusal(self.send(COMPANION, "nature.cast", {**CARRY, "target": "obj:box"}, preview=True))[:2],
                         ("request_invalid", "$.args.target"))
        # Every other ability still refuses the player's avatar: Bubbles as an effect cannot be cast on the player.
        bubbles = self.send(COMPANION, "effect.start", {"capability": "bubbles", "params": {"intensity": 0.6},
                                                        "area": {"center_m": IN_THE_OPEN, "radius_m": 0.5}, "duration_s": 10,
                                                        "targets": ["avatar:player"]})
        self.assertFalse(bubbles["ok"], bubbles)

    def test_no_field_of_a_request_changes_a_price(self):
        """The price is a function of the ability and the principal alone (the mock reads it; M1 charges it)."""
        rules = load_rules(contracts(), rules_path(rules_version=5))
        self.assertEqual(list(inspect.signature(rules.price).parameters), ["capability", "principal"])
        self.assertEqual((rules.price("fire", PLAYER), rules.price("fire", COMPANION)), (2, 1))
        for smuggled in ("price", "cost", "ordered", "own_idea_factor", "directed_by", "ordered_by"):
            with self.subTest(field=smuggled):
                result = self.send(COMPANION, "nature.cast", {"capability": "grow", "center_m": IN_THE_OPEN, "radius_m": 0.25,
                                                              "duration_s": 30, smuggled: 0}, preview=True)
                self.assertEqual(refusal(result)[0], "field_unknown")


    def test_the_status_reads_the_packs_body_and_pool_as_the_kernel_does(self):
        """M1: under a pack with traits, player.status is the kernel's (VitalsRules.Fresh and PlayerStatus): five hearts in
        quarters, the first band, a pool of 8 (the shadow pool in Creative); without them, today's numbers and no new field."""
        host = new_host(rules_file=rules_path(rules_version=5))
        status = host.handle(COMPANION, query("player.status", {}))["data"]
        self.assertEqual(status, {"mode": "creative", "health": 100.0, "max_health": 100.0, "stamina": 0.4, "max_stamina": 0.4,
                                  "completed_climbs": 0, "recoveries": 0, "consequences": False,
                                  "energy": {"pips": 8, "max_pips": 8, "shadow": True},
                                  "hearts": {"quarters": 20, "max_quarters": 20}, "bar": {"bands": 1, "slowed": False}})
        self.assertTrue(host.handle(PLAYER, command("world.set_mode", {"mode": "challenge"}, "m1-mode"))["ok"])
        self.assertEqual(host.handle(COMPANION, query("player.status", {}))["data"]["energy"], {"pips": 8, "max_pips": 8, "shadow": False})
        for result in host.emitted:
            self.assertEqual(contract_problems(result), [], result)
        today = new_host(rules_file=rules_path(rules_version=4)).handle(COMPANION, query("player.status", {}))["data"]
        self.assertFalse({"energy", "hearts", "bar"} & set(today))
        self.assertEqual((today["max_health"], today["max_stamina"]), (100.0, 0.45))


class TheBubbleOnTheMock(unittest.TestCase):
    """The Bubble on the mock (M9 and its follow-up, decisions 55 to 60; no physics: a bubble neither drifts nor deflates, and
    the kernel suite native_kernel_bubble holds the body's side, the deflate and the lift's rise among it): the place deciding,
    one bubble at a time, the room's cap, the Gubble's lead, the security notes' row by name (extended to the lead), and the
    receipt the player marks."""

    def setUp(self):
        self.host = new_host()
        self.n = 0
        self.feet = list(self.host.entities["avatar:player"].position)
        self.beside = [self.feet[0] + 0.3, self.feet[1], self.feet[2] + 0.1]

    def tearDown(self):
        for result in self.host.emitted:
            self.assertEqual(contract_problems(result), [], result)

    def send(self, principal, op, args, **extra):
        self.n += 1
        return self.host.handle(principal, command(op, args, f"m9-{self.n}", **extra))

    def carry_args(self, capability="bubble_carry", **extra):
        return {"capability": capability, "center_m": self.feet, "radius_m": 0.08, "duration_s": 20, "target": "avatar:player", **extra}

    def carry(self, principal=COMPANION, capability="bubble_carry", wait=True):
        """Previewed, the ghost's lead waited out, then cast naming its preview: the commit's result."""
        preview = self.send(principal, "nature.cast", self.carry_args(capability), preview=True)
        if wait:
            self.host.clock.advance(mock_host.BUBBLE_GHOST_LEAD_S + 0.05)
        return self.send(principal, "nature.cast", self.carry_args(capability, preview_id=preview.get("data", {}).get("preview_id", "e" * 32)))

    @staticmethod
    def the_bubble(spot, duration=20, **extra):
        return {"capability": "bubbles", "params": {}, "area": {"center_m": spot, "radius_m": 0.5}, "duration_s": duration, **extra}

    def bubble_on_the_player(self, principal=COMPANION):
        """effect.start of Bubbles at the player's feet done right: the Gubble previews (the ghost) and casts the same a moment later."""
        if principal != PLAYER:
            self.send(principal, "effect.start", self.the_bubble(self.feet), preview=True)
            self.host.clock.advance(mock_host.BUBBLE_GHOST_LEAD_S + 0.05)
        return self.send(principal, "effect.start", self.the_bubble(self.feet))

    def lead(self, principal, bubble, spot, **extra):
        return self.send(principal, "goal.set", {"actor": GUBBLE, "goal": "lead", "target": bubble, "position_m": spot}, **extra)

    def lookup(self, principal, committed):
        return self.host.handle(principal, query("receipt.lookup", {"action_id": committed["action_id"]}, f"q-{self.n}"))["data"]["receipt"]["data"]

    def job(self, principal, set_):
        return self.host.handle(principal, query("jobs.status", {"job_id": set_["job_id"]}, f"q-{self.n}"))["data"]

    def test_the_place_decides_one_bubble_at_a_time(self):
        """effect.start of Bubbles: one player-sized bubble at a spot; on the player a carry (the Gubble's previewed first),
        elsewhere a bubble to walk into; never on the Gubble; a recast pops the one there; the room's cap counts it."""
        unpreviewed = self.send(COMPANION, "effect.start", self.the_bubble(self.feet))
        ghost = self.send(COMPANION, "effect.start", self.the_bubble(self.feet), preview=True)
        too_soon = self.send(COMPANION, "effect.start", self.the_bubble(self.feet))
        self.assertEqual([refusal(r) for r in (unpreviewed, too_soon)],
                         [("revision_conflict", "$.args.area.center_m", mock_host.BUBBLE_GHOST_FIRST),
                          ("not_ready", "$.args.area.center_m", mock_host.BUBBLE_GHOST_LEAD)])
        self.assertEqual((ghost["preview"], ghost["data"]["kind"], len(self.host.bubble_ghosts), list(self.host.bubbles)), (True, "carry", 1, []))
        self.host.clock.advance(mock_host.BUBBLE_GHOST_LEAD_S + 0.05)
        carry = self.send(COMPANION, "effect.start", self.the_bubble(self.feet))
        bubble = carry["data"]["bubble"]
        self.assertEqual((carry["data"]["kind"], carry["created"], carry["affected"], carry["data"]["carried"], carry["data"]["area"]["radius_m"]),
                         ("carry", [bubble], [bubble, "avatar:player"], True, mock_host.BUBBLE_CARRY_RADIUS_DEFAULT_M))
        walk_in = self.send(PLAYER, "effect.start", self.the_bubble(self.beside))
        self.assertEqual((walk_in["data"]["kind"], walk_in["data"]["carried"], list(self.host.bubbles)), ("walk_in", False, [walk_in["data"]["bubble"]]))
        self.assertEqual(self.lookup(COMPANION, carry)["ended"], "popped", "a recast pops the one already there")
        own = self.send(PLAYER, "effect.start", self.the_bubble(self.feet))
        self.assertEqual((own["data"]["kind"], own["data"]["carried"]), ("carry", True), "the player's own key is the consent")
        on_the_gubble = self.send(COMPANION, "effect.start", self.the_bubble(self.feet, targets=[GUBBLE]))
        self.assertEqual(refusal(on_the_gubble), ("invalid_args", "$.args.targets", mock_host.BUBBLE_AT_A_SPOT))
        # The room's cap: a bubble counts at an effect's start; a bubble's recast replaces it.
        self.host.policy = dataclasses.replace(self.host.policy, max_active_effects=1)
        glow = self.send(PLAYER, "effect.start", {"capability": "glow", "params": {}, "area": {"center_m": IN_THE_OPEN, "radius_m": 0.5}, "duration_s": 60})
        recast = self.send(PLAYER, "effect.start", self.the_bubble(self.beside))
        self.assertEqual((refusal(glow)[:2], glow["error"]["allowed"], glow["error"]["actual"], recast["ok"]),
                         (("budget_exceeded", "$.args.capability"), 1, 1, True))
        listed = self.host.handle(PLAYER, query("capabilities.list", {}, "q-cap"))["data"]["items"]
        self.assertEqual(next(i["max_active"] for i in listed if i["capability"] == "bubbles"), 1)

    def test_a_companion_cast_never_targets_the_player_without_the_preview_or_the_step_in(self):
        unpreviewed = self.send(COMPANION, "nature.cast", self.carry_args())
        made_up = self.send(COMPANION, "nature.cast", self.carry_args(preview_id="d" * 32))
        at_the_feet = self.send(COMPANION, "nature.cast", {k: v for k, v in self.carry_args().items() if k != "target"})
        too_soon = self.carry(wait=False)
        bubble_unpreviewed = self.send(COMPANION, "effect.start", self.the_bubble(self.feet))
        self.assertEqual([refusal(r)[:2] for r in (unpreviewed, made_up, at_the_feet, too_soon, bubble_unpreviewed)],
                         [("request_invalid", "$"), ("revision_conflict", "$.args.preview_id"), ("invalid_args", "$.args.center_m"),
                          ("not_ready", "$.args.preview_id"), ("revision_conflict", "$.args.area.center_m")])
        self.assertEqual(refusal(too_soon)[2], mock_host.BUBBLE_GHOST_LEAD)
        self.assertFalse(self.host.bubbles, "no refusal forms a bubble")
        # Beside the player a bubble waits, carrying no one, until they step in.
        beside = self.send(COMPANION, "nature.cast", {"capability": "bubble_carry", "center_m": self.beside, "radius_m": 0.08, "duration_s": 20})
        self.assertTrue(beside["ok"] and beside["transient"] and beside["data"]["carried"] is False and "target" not in beside["data"], beside)
        self.assertTrue(self.host.player_steps_into(beside["data"]["bubble"]))
        self.assertIs(self.lookup(COMPANION, beside)["carried"], True)

    def test_the_dive_key_pops_it_at_any_moment(self):
        carry = self.carry()
        self.assertTrue(carry["ok"] and carry["transient"] and carry["created"] == [carry["data"]["bubble"]], carry)
        self.assertEqual(carry["affected"], [carry["data"]["bubble"], "avatar:player"])
        self.assertTrue(self.host.player_pops_bubble())
        self.assertEqual(self.lookup(COMPANION, carry)["ended"], "popped_by_player")
        self.assertFalse(self.host.bubbles)
        # Mid-lead too: the lead fails (the bubble popped before it got there) and its receipt says how.
        again = self.bubble_on_the_player()
        led = self.lead(COMPANION, again["data"]["bubble"], IN_THE_OPEN)
        self.assertEqual(self.job(COMPANION, led)["state"], "running")
        self.assertTrue(self.host.player_pops_bubble())
        failed = self.job(COMPANION, led)
        self.assertEqual((failed["state"], failed["result"]["error"]["code"], failed["result"]["error"]["message"]),
                         ("failed", "target_not_found", mock_host.LEAD_POPPED))
        self.assertEqual((self.lookup(COMPANION, led)["ended"], self.lookup(COMPANION, again)["ended"]), ("popped_by_player", "popped_by_player"))

    def test_the_companion_cannot_keep_a_player_in_a_bubble_the_player_wants_out_of(self):
        carry = self.carry()
        led = self.lead(COMPANION, carry["data"]["bubble"], IN_THE_OPEN)
        x = self.send(PLAYER, "goal.stop", {"actor": GUBBLE})
        self.assertIn(carry["data"]["bubble"], x["affected"])
        self.assertEqual((self.lookup(COMPANION, carry)["ended"], self.lookup(COMPANION, led)["ended"], self.job(COMPANION, led)["state"]),
                         ("popped_by_player", "stopped", "cancelled"), "X pops the bubble the Gubble leads, popped by the player")
        # No pause after a pop (decision 59): the Gubble may offer again at once, and the ghost and its lead are the consent.
        again = self.send(COMPANION, "nature.cast", self.carry_args(), preview=True)
        self.assertTrue(again["ok"], again)
        later = self.carry()
        self.assertTrue(later["ok"], later)
        # Another offer while one holds them replaces it (one bubble at a time), and the dive key pops that one too.
        replaced = self.carry()
        self.assertEqual((replaced["ok"], self.lookup(COMPANION, later)["ended"], len(self.host.bubbles)), (True, "popped", 1))
        self.assertTrue(self.host.player_pops_bubble())
        # The Gubble can never stop the player's own exit, and its stops pop only its own bubbles and the one it leads.
        mine = self.send(PLAYER, "nature.cast", {"capability": "bubble_carry", "center_m": self.beside, "radius_m": 0.08, "duration_s": 20})
        theirs = self.send(COMPANION, "effect.stop", {"effect": mine["data"]["bubble"]})
        self.assertEqual(theirs["data"]["effects_stopped"], 0)
        self.assertTrue(self.host.player_steps_into(mine["data"]["bubble"]))
        self.assertTrue(self.host.player_pops_bubble())

    def test_every_other_ability_still_refuses_the_players_avatar(self):
        outcome = {}
        for capability in ("grow", "fire", "bubble_boat"):
            outcome[capability] = refusal(self.send(COMPANION, "nature.cast", {**self.carry_args(), "capability": capability}, preview=True))[:2]
        outcome["bubbles"] = refusal(self.send(COMPANION, "effect.start", self.the_bubble(self.feet, targets=["avatar:player"])))[:2]
        outcome["directing"] = refusal(self.send(COMPANION, "goal.set", {"actor": "avatar:player", "goal": "go_to", "position_m": IN_THE_OPEN}))[:2]
        outcome["the Gubble"] = refusal(self.send(COMPANION, "nature.cast", {**self.carry_args(), "target": GUBBLE}, preview=True))[:2]
        self.assertEqual(outcome, {"grow": ("request_invalid", "$.args.capability"), "fire": ("request_invalid", "$.args.capability"),
                                   "bubble_boat": ("request_invalid", "$.args.capability"), "bubbles": ("permission_denied", "$.args.targets"),
                                   "directing": ("actor_denied", "$.args.actor"), "the Gubble": ("invalid_args", "$.args.target")})

    def test_a_lead_targets_only_a_bubble_and_moves_the_player_only_under_the_carry_exception(self):
        """goal.set lead (the security row, extended to it): its target is only a bubble (an effect: id), never an avatar or a
        thing; it names a spot inside the island; the companion leads only a bubble the team sees; the actor is the Gubble."""
        carry = self.bubble_on_the_player()
        bubble = carry["data"]["bubble"]
        refused = {
            "the player": self.lead(COMPANION, "avatar:player", IN_THE_OPEN),
            "the Gubble": self.lead(COMPANION, GUBBLE, IN_THE_OPEN),
            "a thing": self.lead(COMPANION, "obj:box", IN_THE_OPEN),
            "no spot": self.send(COMPANION, "goal.set", {"actor": GUBBLE, "goal": "lead", "target": bubble}),
            "an unknown bubble": self.lead(COMPANION, "effect:" + "a" * 26, IN_THE_OPEN),
            "the player's body": self.send(COMPANION, "goal.set", {"actor": "avatar:player", "goal": "lead", "target": bubble, "position_m": IN_THE_OPEN}),
            "outside the island": self.lead(COMPANION, bubble, [0.6, 0.0, 1.7]),
        }
        self.assertEqual({k: refusal(v)[:2] for k, v in refused.items()}, {
            "the player": ("request_invalid", "$.args.target"), "the Gubble": ("request_invalid", "$.args.target"),
            "a thing": ("request_invalid", "$.args.target"), "no spot": ("request_invalid", "$.args.position_m"),
            "an unknown bubble": ("target_not_found", "$.args.target"), "the player's body": ("actor_denied", "$.args.actor"),
            "outside the island": ("out_of_bounds", "$.args.position_m")})
        self.assertEqual(refusal(refused["the player"])[2], refusals.LEAD_NAMES_A_BUBBLE)
        # The lead runs as the Gubble's job: arriving, the bubble is at the spot and its receipt says arrived.
        led = self.lead(COMPANION, bubble, IN_THE_OPEN)
        self.assertEqual((led["data"]["goal"], led["data"]["bubble"], led["data"]["lift"], led["data"]["target_seen"], led["affected"]),
                         ("lead", bubble, False, "now", [GUBBLE, bubble]))
        self.assertEqual(self.host.goal_arrived(GUBBLE), "succeeded")
        self.assertEqual((self.lookup(COMPANION, led)["ended"], self.host.bubbles[bubble].centre), ("arrived", IN_THE_OPEN))
        # A higher spot is a lift (its price in the data), set by the player or the companion.
        up = self.lead(PLAYER, bubble, [1.1, 0.3, 0.2])
        self.assertEqual((up["data"]["lift"], up["data"]["lift_price"]), (True, 6))
        # A new lead of the same bubble re-aims it; the Gubble's own stop pops the bubble it leads (popped), even the player's.
        again = self.lead(COMPANION, bubble, self.beside)
        self.assertEqual((self.job(PLAYER, up)["state"], self.lookup(PLAYER, up)["ended"], bubble in self.host.bubbles), ("cancelled", "stopped", True))
        stop = self.send(COMPANION, "goal.stop", {})
        self.assertIn(bubble, stop["affected"])
        self.assertEqual((self.lookup(COMPANION, again)["ended"], self.lookup(COMPANION, carry)["ended"], self.job(COMPANION, again)["state"]),
                         ("stopped", "popped", "cancelled"))
        # A new goal pops the bubble the Gubble leads; a lead that cannot get on fails (target_unreachable).
        players = self.send(PLAYER, "effect.start", self.the_bubble(self.beside))
        follow_led = self.lead(PLAYER, players["data"]["bubble"], IN_THE_OPEN)
        self.send(COMPANION, "goal.set", {"actor": GUBBLE, "goal": "follow"})
        self.assertEqual((self.job(PLAYER, follow_led)["state"], self.lookup(PLAYER, players)["ended"]), ("cancelled", "popped"))
        stuck = self.send(PLAYER, "effect.start", self.the_bubble(self.beside))
        blocked = self.lead(PLAYER, stuck["data"]["bubble"], IN_THE_OPEN)
        self.assertEqual(self.host.goal_blocked(GUBBLE, self.host.policy.unreachable_after_s), "failed")
        self.assertEqual((self.job(PLAYER, blocked)["result"]["error"]["code"], self.lookup(PLAYER, blocked)["ended"], stuck["data"]["bubble"] in self.host.bubbles),
                         ("target_unreachable", "blocked", True))

    def test_a_bubble_is_judged_where_it_forms(self):
        """The W2 review's blocker, as the kernel's PlaceTheBubble and BubbleCast: a bubble to walk into rests on what is under its
        spot, so one aimed under the feet of a player on the box would form round them: effect.start makes it a carry (the
        companion's previewed first), and nature.cast at a point refuses it, before any price."""
        self.host.move_avatar("avatar:player", [0.955, 0.3, 0.2])
        self.host.move_avatar(GUBBLE, [0.6, 0.0, 0.2])
        buried = [0.935, 0.16, 0.2]  # inside the box, 14 cm under the feet: beyond the carry's reach, on the box top once formed
        bubble = self.send(COMPANION, "effect.start", self.the_bubble(buried))
        point = self.send(COMPANION, "nature.cast", {"capability": "bubble_carry", "center_m": [0.935, 0.2, 0.2], "radius_m": 0.07, "duration_s": 20})
        self.assertEqual([refusal(r) for r in (bubble, point)],
                         [("revision_conflict", "$.args.area.center_m", mock_host.BUBBLE_GHOST_FIRST),
                          ("invalid_args", "$.args.center_m", mock_host.BUBBLE_ON_THE_PLAYER)])
        self.assertFalse(self.host.bubbles, "nothing forms round the player without the ghost")
        ghost = self.send(COMPANION, "effect.start", self.the_bubble(buried), preview=True)
        self.host.clock.advance(mock_host.BUBBLE_GHOST_LEAD_S + 0.05)
        carry = self.send(COMPANION, "effect.start", self.the_bubble(buried))
        self.assertEqual((ghost["data"]["kind"], carry["data"]["kind"], carry["data"]["carried"]), ("carry", "carry", True), "the ghost first is the consent")
        self.assertTrue(self.host.player_pops_bubble())
        # Beside them on the box top a bubble still waits for the walk-in.
        self.host.move_avatar("avatar:player", [1.1, 0.3, 0.2])
        beside = self.send(COMPANION, "effect.start", self.the_bubble([0.96, 0.3, 0.2]))
        self.assertEqual((beside["data"]["kind"], beside["data"]["carried"]), ("walk_in", False))

    def test_the_companion_leads_only_a_bubble_the_team_sees(self):
        """The lead's sight check (the kernel's CommandHostBubbleLead): a bubble the box hides from both avatars answers to the
        companion's lead as one that is not there; the player's own order may lead it."""
        self.host.move_avatar("avatar:player", [1.75, 0.0, 0.15])
        self.host.move_avatar(GUBBLE, [1.75, 0.0, 0.45])
        hidden = self.send(PLAYER, "effect.start", self.the_bubble([1.45, 0.0, 0.15]))
        self.assertEqual(hidden["data"]["kind"], "walk_in", hidden)
        self.host.move_avatar("avatar:player", self.feet)
        self.host.move_avatar(GUBBLE, [0.45, 0.0, 0.6])
        by_the_gubble = self.lead(COMPANION, hidden["data"]["bubble"], [1.6, 0.0, 0.7])
        by_the_player = self.lead(PLAYER, hidden["data"]["bubble"], [1.6, 0.0, 0.7], preview=True)
        self.assertEqual((refusal(by_the_gubble)[:2], by_the_player["ok"]), (("target_not_found", "$.args.target"), True))

    def test_one_rule_for_the_islands_bounds_with_a_sea_and_without(self):
        """The W2 review's minor 8, as the kernel's InsideIsland: without a sea the room's bounds hold every side; round an island
        the sea has no edge, so only the top and the bottom hold, for a lead and an effect alike."""
        outcome = {}
        for sea in (False, True):
            if sea:
                self.host.room.setdefault("extensions", {})[mock_host.SEA_EXTENSION] = {}
            bubble = self.send(PLAYER, "effect.start", self.the_bubble(self.beside))["data"]["bubble"]
            outcome[sea] = {
                "lead past the side": refusal(self.lead(COMPANION, bubble, [2.3, 0.0, 0.6], preview=True))[:2],
                "lead over the top": refusal(self.lead(COMPANION, bubble, [0.6, 2.5, 0.6], preview=True))[:2],
                "glow over the top": refusal(self.send(COMPANION, "effect.start", {"capability": "glow", "params": {}, "duration_s": 10,
                                                                                   "area": {"center_m": [self.feet[0], 2.5, self.feet[2]], "radius_m": 0.5}})),
                "glow past the side": refusal(self.send(COMPANION, "effect.start", {"capability": "glow", "params": {}, "duration_s": 10,
                                                                                    "area": {"center_m": [0.6, 0.0, 1.6], "radius_m": 0.5}}))[:2],
            }
        outside = ("out_of_bounds", "$.args.area.center_m", "That spot is outside the room.")
        self.assertEqual(outcome[False], {"lead past the side": ("out_of_bounds", "$.args.position_m"), "lead over the top": ("out_of_bounds", "$.args.position_m"),
                                          "glow over the top": outside, "glow past the side": outside[:2]})
        self.assertEqual((outcome[True]["lead past the side"], outcome[True]["lead over the top"], outcome[True]["glow over the top"]),
                         ("ok", ("out_of_bounds", "$.args.position_m"), outside))
        self.assertNotEqual(outcome[True]["glow past the side"][0], "out_of_bounds", "round an island the sides are open")

    def test_a_bubbles_receipt_says_how_it_ended(self):
        """The receipt the player marks (M8): the kind, the target, carried, and ended: timed out, popped by the Gubble's own stop."""
        timed = self.carry()
        self.host.clock.advance(21)
        self.assertEqual(self.lookup(COMPANION, timed)["ended"], "timed_out")
        carry = self.carry()
        self.send(COMPANION, "goal.stop", {})
        ended = self.lookup(COMPANION, carry)
        self.assertEqual((ended["capability"], ended["target"], ended["carried"], ended["ended"]), ("bubble_carry", "avatar:player", True, "popped"))
        # A replay answers the receipt as it stands now; bubble_lift is no cast (Lift is a lead); no boat on the box world's dry floor.
        replay = self.host.handle(COMPANION, command("nature.cast", self.carry_args(preview_id="f" * 32), carry["action_id"]))
        self.assertEqual(replay["error"]["code"], "action_id_conflict")
        lift = self.carry(capability="bubble_lift")
        self.assertEqual(refusal(lift), ("unsupported_capability", "$.args.capability", mock_host.BUBBLE_LIFT_IS_A_LEAD))
        boat = self.send(COMPANION, "nature.cast", {"capability": "bubble_boat", "center_m": IN_THE_OPEN, "radius_m": 0.12, "duration_s": 60})
        self.assertEqual(refusal(boat), ("invalid_args", "$.args.center_m", mock_host.BUBBLE_NO_WATER))


class ThroughMcp(unittest.IsolatedAsyncioTestCase):
    async def test_no_tool_reaches_the_players_new_ops(self):
        async with McpHarness() as harness:
            tools = {tool.name for tool in (await harness.client.list_tools()).tools}
            for op in NEW_PLAYER_OPS:
                self.assertNotIn(op.replace(".", "_"), tools)
            self.assertIn("nature_cast", tools)

    async def test_the_tools_say_what_the_bubble_does_now(self):
        """The companion's tool texts describe the Bubble as redesigned (M9's change request): one bubble at a spot where the
        place decides, nature_cast's named forms and no bubble_lift cast, and goal_set's lead; nothing left of the stream."""
        from garage_game_companion import server
        async with McpHarness() as harness:
            tools = {tool.name: tool.description for tool in (await harness.client.list_tools()).tools}
        for words in ("bubble_carry", "bubble_boat", "bubble_lift is no cast", "goal_set lead", "One bubble at a time"):
            self.assertIn(words, tools["nature_cast"])
        for words in ("the place decides", "a carry round them", "a boat", "walk into it", "One bubble at a time"):
            self.assertIn(words, tools["effect_start"])
        self.assertIn("lead needs a target, a bubble's effect id (never an avatar or a thing), and position_m", tools["goal_set"])
        self.assertIn("pop your bubbles and the bubble you lead", tools["goal_stop"])
        for name in ("effect_start", "capabilities_list", "goal_stop", "nature_cast"):
            self.assertNotIn("stream", tools[name], name)
        self.assertNotIn("a stream of bubbles", server.INSTRUCTIONS)
        self.assertIn("goal_set lead", server.INSTRUCTIONS)

    async def test_the_adapter_refuses_an_unpreviewed_carry_and_a_fractal_grab_before_sending_anything(self):
        async with McpHarness() as harness:
            carry = await harness.call("nature_cast", {"action_id": "carry-1", **CARRY})
            self.assertFalse(carry["ok"], carry)
            self.assertEqual(carry["error"]["code"], "request_invalid")
            self.assertEqual(harness.host.emitted, [], "refused before anything reached the game")
            grab = await harness.call("entity_grab", {"action_id": "grab-1", "target": "fractal:k3x9q2"})
            self.assertEqual((grab["error"]["code"], grab["error"]["field_path"]), ("permission_denied", "$.args.target"))
            fetch = await harness.call("goal_set", {"action_id": "fetch-1", "goal": "fetch", "target": "fractal:k3x9q2"})
            self.assertEqual(fetch["error"]["code"], "permission_denied")
            # A lead names a bubble, never the player's avatar, and a spot: refused at the adapter as the game refuses it.
            on_the_player = await harness.call("goal_set", {"action_id": "lead-1", "goal": "lead", "target": "avatar:player", "position_m": IN_THE_OPEN})
            no_spot = await harness.call("goal_set", {"action_id": "lead-2", "goal": "lead", "target": "effect:" + "a" * 26})
            self.assertEqual([(r["error"]["code"], r["error"]["field_path"]) for r in (on_the_player, no_spot)],
                             [("request_invalid", "$.args.target"), ("request_invalid", "$.args.position_m")])
            self.assertEqual(harness.host.emitted, [], "refused before anything reached the game")


if __name__ == "__main__":
    unittest.main()
