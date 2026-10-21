"""Fetch, as the merged contract defines it (contracts/README.md, "The sandbox verbs"), on the mock host.

The Gubble floats to the target, picks it up with entity.grab's checks and limit, and brings it back to the
player, still holding it; its job succeeds then. entity.release, from the companion or the player directing it,
puts it down, so the goal stays transient and the move that is saved is its own durable command. Holding is not
saved: a carried thing is saved where it was last put down.

The mock models the two arrivals the real goal runner will report (at the thing, then back beside the player);
test_real_host.py runs the same lifecycle against the kernel host once it fetches.
"""
from __future__ import annotations

import contextlib
import io
import math
import unittest

from mcp_harness import McpHarness
from support import COMPANION, PLAYER, FakeClock, HostPolicy, command, contract_problems, new_host, query

from garage_game_companion import mock_game
from garage_game_companion.mock_host import COME_ARRIVAL_M, HOVER_M

FAST = HostPolicy(companion_messages_per_s=1_000_000)
BESIDE_THE_BOOK = [0.45, HOVER_M, 0.24]  # book at (0.45, 0, 0.1); Gubble spawn at (0.45, HOVER_M, 0.6)
BESIDE_THE_DOORSTOP = [-0.15, HOVER_M, 0.5]
OUT_OF_EVERY_SIGHT = [-1.7, 0.0, -1.2]


class FetchCase(unittest.TestCase):
    def setUp(self):
        self.clock = FakeClock()
        self.host = new_host(policy=FAST, clock=self.clock)
        self._ids = 0

    def tearDown(self):
        for result in self.host.emitted:
            self.assertEqual(contract_problems(result), [], result)

    def next_id(self, prefix):
        self._ids += 1
        return f"{prefix}-{self._ids}"

    def act(self, op, args, **extra):
        return self.host.handle(COMPANION, command(op, args, self.next_id("c"), **extra))

    def player(self, op, args, **extra):
        return self.host.player_command(command(op, args, self.next_id("p"), **extra))

    def ask(self, op, args):
        return self.host.handle(COMPANION, query(op, args, self.next_id("q")))

    def fetch(self, target, **extra):
        return self.act("goal.set", {"actor": "avatar:companion", "goal": "fetch", "target": target}, **extra)

    def arrive(self):
        return self.host.goal_arrived("avatar:companion")

    def status(self, job_id):
        return self.ask("jobs.status", {"job_id": job_id})["data"]

    def held_by(self, target):
        return self.ask("entity.inspect", {"target": target})["data"]["entity"].get("held_by")

    def position(self, entity_id):
        return self.host.entities[entity_id].position

    def walk_to(self, position):
        self.host.move_avatar("avatar:companion", position)


class Lifecycle(FetchCase):
    def test_a_fetch_picks_the_thing_up_and_brings_it_to_the_player_still_holding_it(self):
        started = self.fetch("obj:book")
        self.assertTrue(started["ok"], started)
        self.assertTrue(started["transient"])
        self.assertEqual(started["data"], {"actor": "avatar:companion", "goal": "fetch", "target_seen": "now"})
        job = started["job_id"]
        self.assertEqual(self.status(job)["state"], "running")
        self.assertEqual(self.host.holding, {})

        self.walk_to(BESIDE_THE_BOOK)
        self.assertEqual(self.arrive(), "running")  # picked up; on its way back
        self.assertEqual(self.status(job), {"job_id": job, "state": "running"})
        self.assertEqual(self.host.holding, {"avatar:companion": "obj:book"})
        self.assertEqual(self.held_by("obj:book"), "avatar:companion")

        self.assertEqual(self.arrive(), "succeeded")  # back beside the player
        self.assertEqual(self.status(job), {"job_id": job, "state": "succeeded"})
        self.assertEqual(self.held_by("obj:book"), "avatar:companion")  # a fetch ends holding the thing
        companion, player = self.position("avatar:companion"), self.position("avatar:player")
        self.assertAlmostEqual(math.hypot(companion[0] - player[0], companion[2] - player[2]),
                               COME_ARRIVAL_M, places=3)
        self.assertAlmostEqual(companion[1], player[1] + HOVER_M, places=6)
        self.assertEqual(self.position("obj:book"), companion)  # carried with it
        self.assertEqual(self.host.revision, 0)  # holding and carrying are not saved
        self.assertNotIn("avatar:companion", self.host.goals)

        released = self.act("entity.release", {"actor": "avatar:companion"})
        self.assertTrue(released["ok"], released)
        self.assertFalse(released["transient"])  # putting it down is the saved move
        self.assertEqual((released["affected"], released["revision"]), (["obj:book"], 1))
        self.assertNotIn("data", released)
        self.assertEqual(self.host.holding, {})
        self.assertIsNone(self.held_by("obj:book"))
        # The player stands in front of the companion it came back to, so the book goes down beside it, never
        # on the player (SandboxPhysics.Drop; test_team_knowledge.py pins the turns).
        book = self.host.entities["obj:book"].bounds()
        player = self.host.entities["avatar:player"].bounds()
        self.assertGreater(max(book["min_m"][0] - player["max_m"][0], player["min_m"][0] - book["max_m"][0],
                               book["min_m"][2] - player["max_m"][2], player["min_m"][2] - book["max_m"][2]), 0)
        self.assertLess(math.dist(self.position("obj:book"), companion), 0.3)

    def test_the_player_can_send_the_companion_and_set_the_thing_down_with_it(self):
        started = self.player("goal.set", {"actor": "avatar:companion", "goal": "fetch", "target": "obj:doorstop"})
        self.assertTrue(started["ok"], started)
        self.walk_to(BESIDE_THE_DOORSTOP)
        self.assertEqual(self.arrive(), "running")
        self.assertEqual(self.arrive(), "succeeded")
        placed = self.player("entity.release", {"actor": "avatar:companion",
                                                "placement": {"position_m": [0.1, 0.0, 0.9]}})
        self.assertTrue(placed["ok"], placed)
        self.assertEqual(self.position("obj:doorstop"), [0.1, 0.0, 0.9])
        # The job is the player's: the companion cannot read it.
        self.assertEqual(self.ask("jobs.status", {"job_id": started["job_id"]})["error"]["code"], "target_not_found")

    def test_a_fetch_of_something_already_in_hand_only_brings_it_back(self):
        self.assertTrue(self.act("entity.grab", {"actor": "avatar:companion", "target": "obj:doorstop"})["ok"])
        started = self.fetch("obj:doorstop")
        self.assertTrue(started["ok"], started)
        self.assertEqual(self.arrive(), "succeeded")
        self.assertEqual(self.held_by("obj:doorstop"), "avatar:companion")

    def test_a_fetch_returns_level_with_the_player_on_the_30_cm_box(self):
        started = self.fetch("obj:book")
        self.assertTrue(started["ok"], started)
        self.walk_to(BESIDE_THE_BOOK)
        self.assertEqual(self.arrive(), "running")
        box = self.host.entities["obj:box"].bounds()
        top = box["max_m"][1]
        self.assertAlmostEqual(top, 0.30, places=6)
        player = self.position("obj:box")[:]
        player[1] = top
        self.host.move_avatar("avatar:player", player)
        self.assertEqual(self.arrive(), "succeeded")
        companion = self.position("avatar:companion")
        self.assertAlmostEqual(companion[1], 0.32, places=6)
        self.assertAlmostEqual(math.hypot(companion[0] - player[0], companion[2] - player[2]),
                               COME_ARRIVAL_M, places=3)
        self.assertEqual(self.position("obj:book"), companion)
        self.assertEqual(self.held_by("obj:book"), "avatar:companion")
        self.assertEqual(self.status(started["job_id"])["state"], "succeeded")
        self.assertEqual(self.host.revision, 0)

    def test_carrying_moves_the_thing_with_its_holder_and_saves_nothing(self):
        self.assertTrue(self.act("entity.grab", {"actor": "avatar:companion", "target": "obj:book"})["ok"])
        self.walk_to([1.0, 0.0, 1.0])
        self.assertEqual(self.position("obj:book"), [1.0, 0.0, 1.0])
        self.assertEqual(self.host.revision, 0)
        self.assertEqual(self.host.entities["obj:book"].revision, 0)


class Refusals(FetchCase):
    def unchanged(self):
        return (dict(self.host.goals), dict(self.host.holding), self.host.revision)

    def test_a_fetch_is_judged_by_entity_grabs_rules_when_it_is_set(self):
        self.player("protect.lock", {"targets": ["obj:book"]}, expected_entities={"obj:book": 0})
        self.assertTrue(self.player("entity.grab", {"actor": "avatar:player", "target": "obj:doorstop"})["ok"])
        before = self.unchanged()
        for target, code, path in (("obj:table", "target_too_heavy", "$.args.target"),
                                   ("obj:book", "target_protected", "$.args.target"),
                                   ("obj:doorstop", "target_busy", "$.args.target"),
                                   ("avatar:player", "permission_denied", "$.args.target"),
                                   ("shell:floor", "permission_denied", "$.args.target")):
            with self.subTest(target=target):
                result = self.fetch(target)
                self.assertEqual((result["error"]["code"], result["error"].get("field_path")), (code, path), result)
        too_heavy = self.fetch("obj:table")["error"]
        self.assertEqual((too_heavy["allowed"], too_heavy["actual"]), (2.0, 25.0))
        self.assertEqual(self.unchanged(), before)

    def test_a_companion_already_holding_something_puts_it_down_first(self):
        self.assertTrue(self.act("entity.grab", {"actor": "avatar:companion", "target": "obj:book"})["ok"])
        result = self.fetch("obj:doorstop")
        self.assertEqual((result["error"]["code"], result["error"]["field_path"]), ("target_busy", "$.args.actor"))

    def test_the_pick_up_checks_again_on_arrival(self):
        for change, code in (("locked", "target_protected"), ("taken", "target_busy")):
            with self.subTest(change=change):
                self.setUp()
                job = self.fetch("obj:doorstop")["job_id"]
                if change == "locked":
                    self.player("protect.lock", {"targets": ["obj:doorstop"]}, expected_entities={"obj:doorstop": 0})
                else:
                    self.assertTrue(self.player("entity.grab", {"actor": "avatar:player", "target": "obj:doorstop"})["ok"])
                self.walk_to(BESIDE_THE_DOORSTOP)
                self.assertEqual(self.arrive(), "failed")
                state = self.status(job)
                self.assertEqual(state["state"], "failed")
                self.assertEqual(state["result"]["error"]["code"], code)
                self.assertEqual(state["result"]["action_id"], self.host.jobs[job]["action_id"])
                self.assertNotIn("avatar:companion", self.host.holding)
                self.tearDown()

    def test_a_thing_gone_before_the_companion_arrives_fails_as_any_goal_does(self):
        job = self.fetch("obj:doorstop")["job_id"]
        self.host.move_entity("obj:doorstop", OUT_OF_EVERY_SIGHT)
        self.walk_to(BESIDE_THE_DOORSTOP)
        self.assertEqual(self.arrive(), "failed")
        self.assertEqual(self.status(job)["result"]["error"]["code"], "target_not_found")
        self.assertEqual(self.host.holding, {})

    def test_losing_the_thing_on_the_way_back_fails_the_fetch(self):
        job = self.fetch("obj:book")["job_id"]
        self.walk_to(BESIDE_THE_BOOK)
        self.assertEqual(self.arrive(), "running")
        self.assertTrue(self.player("entity.remove", {"target": "obj:book"}, expected_entities={"obj:book": 0})["ok"])
        self.assertEqual(self.arrive(), "failed")
        self.assertEqual(self.status(job)["result"]["error"]["code"], "target_not_found")

    def test_the_companion_never_fetches_with_the_players_avatar(self):
        result = self.act("goal.set", {"actor": "avatar:player", "goal": "fetch", "target": "obj:book"})
        self.assertEqual(result["error"]["code"], "actor_denied")


class Stopping(FetchCase):
    def test_a_stop_on_the_way_cancels_it_and_nothing_is_held(self):
        job = self.fetch("obj:book")["job_id"]
        self.assertTrue(self.act("goal.stop", {})["ok"])
        self.assertEqual(self.status(job)["state"], "cancelled")
        self.assertEqual(self.host.holding, {})
        with self.assertRaises(KeyError):
            self.arrive()

    def test_a_stop_on_the_way_back_cancels_it_and_the_thing_stays_in_hand(self):
        job = self.fetch("obj:book")["job_id"]
        self.walk_to(BESIDE_THE_BOOK)
        self.assertEqual(self.arrive(), "running")
        stop = self.act("goal.stop", {"actor": "avatar:companion"})
        self.assertTrue(stop["ok"], stop)
        self.assertEqual(self.status(job)["state"], "cancelled")
        self.assertEqual(self.host.holding, {"avatar:companion": "obj:book"})
        self.assertTrue(self.act("entity.release", {"actor": "avatar:companion"})["ok"])

    def test_a_new_goal_or_the_players_stop_cancels_a_fetch(self):
        first = self.fetch("obj:book")["job_id"]
        self.walk_to(BESIDE_THE_BOOK)
        self.arrive()
        self.assertTrue(self.act("goal.set", {"actor": "avatar:companion", "goal": "follow"})["ok"])
        self.assertEqual(self.status(first)["state"], "cancelled")
        self.assertEqual(self.host.holding, {"avatar:companion": "obj:book"})
        self.assertTrue(self.act("entity.release", {"actor": "avatar:companion"})["ok"])
        second = self.fetch("obj:doorstop")["job_id"]
        self.assertTrue(self.player("goal.stop", {})["ok"])
        self.assertEqual(self.status(second)["state"], "cancelled")


class TheConsole(FetchCase):
    def console(self, line):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            mock_game._console(self.host, line)
        return out.getvalue()

    def test_arrive_twice_fetches(self):
        self.fetch("obj:doorstop")
        self.console("walk -0.15 0 0.5")
        self.assertIn("running", self.console("arrive"))
        self.assertIn("succeeded", self.console("arrive"))
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            mock_game._announce("goal_progress", {"actor": "avatar:companion", "job_id": "job-x", "phase": "return"})
        self.assertIn("bringing it back", out.getvalue())


class ThroughTheAdapter(unittest.IsolatedAsyncioTestCase):
    async def test_a_model_fetches_through_the_mcp_tools_and_puts_the_thing_down(self):
        host = new_host(policy=FAST)
        async with McpHarness(host=host) as h:
            started = await h.call("goal_set", {"action_id": "fetch-1", "goal": "fetch", "target": "obj:doorstop"})
            self.assertTrue(started["ok"], started)
            self.assertEqual((await h.call("jobs_status", {"job_id": started["job_id"]}))["data"]["state"], "running")
            host.move_avatar("avatar:companion", BESIDE_THE_DOORSTOP)
            self.assertEqual(host.goal_arrived("avatar:companion"), "running")
            self.assertEqual(host.goal_arrived("avatar:companion"), "succeeded")
            status = await h.call("jobs_status", {"job_id": started["job_id"]})
            self.assertEqual(status["data"], {"job_id": started["job_id"], "state": "succeeded"})
            seen = await h.call("entity_inspect", {"target": "obj:doorstop"})
            self.assertEqual(seen["data"]["entity"]["held_by"], "avatar:companion")
            released = await h.call("entity_release", {"action_id": "down-1"})
            self.assertTrue(released["ok"], released)
            self.assertEqual(released["principal"], COMPANION)
            self.assertNotIn("held_by", (await h.call("entity_inspect", {"target": "obj:doorstop"}))["data"]["entity"])
        self.assertTrue(all(r["principal"] == COMPANION for r in h.results))
        self.assertNotIn(PLAYER, {r["principal"] for r in h.results})


if __name__ == "__main__":
    unittest.main()
