"""Run 2: the mock host keeps step with the kernel host's team knowledge and journal (CommandHostJournal.cs, Lane P).

- The team's sight: for the companion, "in sight now" means either avatar sees it, and the player's avatar is always
  known; queries and command checks use it, while observe lists only the named avatar's own eyes.
- The team's map holds 1,024 entries; past the bound, routine things go first and what the team built and the targets
  of running goals and open tasks are never dropped; a link session keeps it; a removed thing leaves it once either
  avatar sees its place empty.
- The journal writer: tasks (one per task, updated in place), a task about something neither avatar has seen says
  "something" with no id and no pin until the team sees it, a finished fetch stays in the history.
- A walking goal (come, go_to, fetch; with or without a thing to walk to) blocked for 5 s fails with
  target_unreachable.
- A drop goes beside or behind the companion when an avatar stands in its front spot.
Every result is checked against the contract.
"""
from __future__ import annotations

import dataclasses
import json
import unittest

from support import COMPANION, PLAYER, FakeClock, HostPolicy, command, contract_problems, contracts, example, new_host, query

SAMPLE = example("command_creation_place_trigger_light")["args"]["source"]

BEHIND_THE_TABLE = [-0.9, 0.0, -1.4]  # the companion sees only the table from here
OUT_OF_EVERY_SIGHT = [-1.7, 0.0, -1.2]  # the player sees only the table (and the companion) from here
PLAYER_SPAWN = [0.0, 0.0, 0.6]  # the player sees the book, the doorstop, the box, the rug and the table
FAST = HostPolicy(companion_messages_per_s=1_000_000)


class TeamCase(unittest.TestCase):
    policy = FAST

    def setUp(self):
        self.host = new_host(policy=self.policy, clock=FakeClock())
        self._ids = 0

    def tearDown(self):
        for result in self.host.emitted:
            self.assertEqual(contract_problems(result), [], result)
            self.assertEqual(contracts().schema_errors(result), [], result)

    def next_id(self, prefix):
        self._ids += 1
        return f"{prefix}-{self._ids}"

    def ask(self, op, args=None, principal=COMPANION):
        return self.host.handle(principal, query(op, args or {}, self.next_id("q")))

    def act(self, op, args, principal=COMPANION, **extra):
        message = command(op, args, self.next_id("c"), **extra)
        return self.host.player_command(message) if principal == PLAYER else self.host.handle(principal, message)

    def goal(self, goal, principal=COMPANION, **args):
        return self.act("goal.set", {"actor": "avatar:companion", "goal": goal, **args}, principal)

    def place(self, who, position):
        self.host.move_avatar(who, position)

    def listed(self):
        return {item["id"]: item for item in self.ask("entities.list")["data"]["items"]}

    def journal(self, **args):
        result = self.ask("journal.read", args)
        self.assertTrue(result["ok"], result)
        return result["data"]


class SharedSight(TeamCase):
    def test_what_the_player_sees_is_in_the_companions_sight_now(self):
        self.place("avatar:companion", BEHIND_THE_TABLE)
        listed = self.listed()
        self.assertEqual(listed["obj:book"]["seen"], "now")
        self.assertEqual(self.ask("entity.inspect", {"target": "obj:book"})["data"]["entity"]["seen"], "now")
        # Command checks use the team's sight too.
        locked = self.act("protect.lock", {"targets": ["obj:book"]}, expected_entities={"obj:book": 0})
        self.assertTrue(locked["ok"], locked)

    def test_the_players_avatar_is_always_known(self):
        self.place("avatar:companion", BEHIND_THE_TABLE)
        self.place("avatar:player", [1.6, 0.0, 0.2])  # behind the box, out of the companion's own sight
        self.assertNotIn("avatar:player", self.host.perceived(COMPANION))
        self.assertTrue(self.ask("entity.inspect", {"target": "avatar:player"})["ok"])
        self.assertTrue(self.goal("come", target="avatar:player")["ok"])

    def test_observe_lists_only_the_named_avatars_own_sight(self):
        self.place("avatar:companion", BEHIND_THE_TABLE)
        observed = self.ask("observe", {"actor": "avatar:companion"})["data"]
        self.assertEqual([item["id"] for item in observed["visible"]], ["obj:table"])
        # In the team's sight now, so not "remembered" either.
        self.assertNotIn("obj:book", [item["id"] for item in observed.get("remembered", [])])

    def test_nothing_neither_avatar_sees_is_in_sight(self):
        self.place("avatar:companion", BEHIND_THE_TABLE)
        self.place("avatar:player", OUT_OF_EVERY_SIGHT)
        self.assertNotIn("obj:book", self.listed())
        self.assertEqual(self.ask("entity.inspect", {"target": "obj:book"})["error"]["code"], "target_not_found")
        self.assertEqual(self.ask("map.find", {"name": "book"})["data"], {"items": []})

    def test_one_avatars_sight_when_shared_sight_is_off(self):
        self.host.policy = dataclasses.replace(self.host.policy, shared_sight=False)
        self.place("avatar:companion", BEHIND_THE_TABLE)
        self.assertNotIn("obj:book", self.listed())


class TheTeamsMap(TeamCase):
    def test_the_map_holds_1024_entries_by_default(self):
        self.assertEqual(HostPolicy().perception_memory_entries, 1024)

    def test_the_players_eyes_fill_the_map_and_a_session_keeps_it(self):
        self.place("avatar:companion", BEHIND_THE_TABLE)
        self.ask("room.describe")
        self.place("avatar:player", OUT_OF_EVERY_SIGHT)
        self.host.session_event(COMPANION, "end")
        self.host.session_event(COMPANION, "start")
        self.assertEqual(self.listed()["obj:book"]["seen"], "remembered")
        self.assertEqual([i["entity"]["id"] for i in self.ask("map.find", {"name": "book"})["data"]["items"]], ["obj:book"])

    def test_a_removed_thing_leaves_the_map_once_either_avatar_sees_its_place_empty(self):
        self.ask("room.describe")  # both avatars see the book from their spawns
        self.place("avatar:companion", BEHIND_THE_TABLE)
        self.place("avatar:player", OUT_OF_EVERY_SIGHT)
        self.host.entities["obj:book"].removed = True  # gone while nobody looks
        self.assertEqual(self.listed()["obj:book"]["seen"], "remembered")
        self.place("avatar:player", PLAYER_SPAWN)  # the player sees the place empty
        self.assertNotIn("obj:book", self.listed())

    def test_eviction_spares_creations_and_task_targets(self):
        self.host.policy = dataclasses.replace(self.host.policy, perception_memory_entries=2)
        made = self.act("creation.place", {"source": SAMPLE, "placement": {"position_m": [0.8, 0.0, 0.9]}}, PLAYER)
        self.assertTrue(made["ok"], made)
        creation = made["created"][0]
        self.ask("room.describe")  # both avatars see everything: the bound keeps the creation, the rest is routine
        memory = self.host.memory[COMPANION].entries
        self.assertIn(creation, memory)
        walk = self.goal("go_to", target="obj:doorstop")
        self.assertTrue(walk["ok"], walk)
        self.ask("room.describe")  # the next look (the kernel's sight sweep) sees it as a running goal's target
        self.assertIn("obj:doorstop", memory)
        for spot in (BEHIND_THE_TABLE, [1.6, 0.0, 0.2]):
            self.place("avatar:companion", spot)
            self.place("avatar:player", spot)
            self.ask("room.describe")
            self.assertIn(creation, memory)
            self.assertIn("obj:doorstop", memory)


class Journal(TeamCase):
    def test_a_fetch_opens_a_task_and_stays_in_the_history_when_done(self):
        started = self.goal("fetch", target="obj:book")
        self.assertTrue(started["ok"], started)
        journal = self.journal()
        [task] = journal["open_tasks"]
        name = self.host.entities["obj:book"].summary(self.host.text)["display_name"]
        self.assertEqual(task["line"], f'Fetching "{name}", on its own initiative')
        self.assertEqual((task["kind"], task["state"], task["actor"], task["directed_by"]),
                         ("task", "active", COMPANION, COMPANION))
        self.assertEqual(task["subject"], {"entities": ["obj:book"], "name": name})
        self.assertEqual(task["job_id"], started["job_id"])
        self.host.move_avatar("avatar:companion", [0.45, 0.0, 0.25])
        self.assertEqual(self.host.goal_arrived("avatar:companion"), "running")
        self.assertEqual(self.host.goal_arrived("avatar:companion"), "succeeded")
        journal = self.journal()
        self.assertEqual(journal["open_tasks"], [])
        [done] = journal["entries"]
        self.assertEqual((done["line"], done["state"]), (f'Fetched "{name}", on its own initiative', "done"))

    def test_follow_come_and_go_to_leave_no_history(self):
        for goal, args in (("follow", {"target": "avatar:player"}), ("come", {"target": "avatar:player"}),
                           ("go_to", {"target": "obj:box"})):
            self.assertTrue(self.goal(goal, **args)["ok"])
        [task] = self.journal()["open_tasks"]  # each new goal ended the one before it
        self.assertTrue(task["line"].startswith('Going to "'), task)
        self.act("goal.stop", {})
        self.assertEqual(self.journal(), {"open_tasks": [], "entries": []})

    def test_a_task_about_something_neither_avatar_has_seen_says_something(self):
        self.place("avatar:companion", BEHIND_THE_TABLE)
        self.place("avatar:player", OUT_OF_EVERY_SIGHT)
        started = self.goal("fetch", PLAYER, target="obj:book")  # the player may name anything
        self.assertTrue(started["ok"], started)
        [task] = self.journal()["open_tasks"]
        self.assertEqual(task["line"], "Fetching something, at the player's direction")
        self.assertNotIn("subject", task)
        self.assertNotIn("pin_m", task)
        self.assertNotIn("job_id", task)  # the player's job, not the companion's
        self.assertEqual(self.journal(about="obj:book")["open_tasks"], [])
        self.place("avatar:player", PLAYER_SPAWN)  # once the team sees it, the task names it
        [task] = self.journal()["open_tasks"]
        self.assertEqual(task["subject"]["entities"], ["obj:book"])
        self.assertIn("pin_m", task)

    def test_creations_built_changed_and_removed_are_facts(self):
        made = self.act("creation.place", {"source": SAMPLE, "placement": {"position_m": [0.8, 0.0, 0.9]}}, PLAYER)
        self.assertTrue(made["ok"], made)
        [built] = self.journal(kind="built")["entries"]
        self.assertEqual((built["actor"], built["directed_by"], built["revision"]), (PLAYER, PLAYER, made["revision"]))
        self.assertTrue(built["line"].startswith('You built "'))


class Unreachable(TeamCase):
    """Inject the body reporting a true blockage, such as a closed pen with a roof.

    A lone wall or open pen cannot imply blockage for the floating Gubble. This
    box-only mock does not compute routes; these cases test the host's timer.
    """

    def test_a_walking_goal_blocked_for_5_s_fails_with_target_unreachable(self):
        for goal, args in (("fetch", {"target": "obj:book"}), ("go_to", {"target": "obj:box"}),
                           ("come", {"target": "avatar:player"})):
            with self.subTest(goal=goal):
                started = self.goal(goal, **args)
                self.assertEqual(self.host.goal_blocked("avatar:companion", 4.9), "running")
                self.host.goal_moving("avatar:companion")
                self.assertEqual(self.host.goal_blocked("avatar:companion", 4.9), "running")
                self.assertEqual(self.host.goal_blocked("avatar:companion", 0.1), "failed")
                status = self.ask("jobs.status", {"job_id": started["job_id"]})["data"]
                self.assertEqual((status["state"], status["result"]["error"]["code"]), ("failed", "target_unreachable"))

    def test_come_with_no_target_and_go_to_a_place_stop_after_5_s_blocked(self):
        for goal, args in (("come", {}), ("go_to", {"position_m": [-1.5, 0.0, -1.5]})):
            with self.subTest(goal=goal):
                self.assertNotIn("job_id", self.goal(goal, **args))
                self.assertEqual(self.host.goal_blocked("avatar:companion", 5.0), "stopped")
                self.assertNotIn("avatar:companion", self.host.goals)

    def test_following_is_not_a_walk_to_somewhere(self):
        self.goal("follow", target="avatar:player")
        with self.assertRaises(KeyError):
            self.host.goal_blocked("avatar:companion", 10.0)


class ReviewFixes(TeamCase):
    """Codex's review of Lane A's round 2 (both reviewers, verified by the integrator)."""

    def test_a_fetch_starts_its_return_with_no_blocked_time(self):
        # The host resets BlockedS at the pick-up: 4.9 s blocked on the way there and 0.1 s on the way back is no failure.
        started = self.goal("fetch", target="obj:book")
        self.assertEqual(self.host.goal_blocked("avatar:companion", 4.9), "running")
        self.host.move_avatar("avatar:companion", [0.45, 0.0, 0.25])
        self.assertEqual(self.host.goal_arrived("avatar:companion"), "running")  # picked up, turning for home
        self.assertEqual(self.host.goal_blocked("avatar:companion", 0.1), "running")
        self.assertEqual(self.host.goal_blocked("avatar:companion", 5.0), "failed")
        self.assertEqual(self.ask("jobs.status", {"job_id": started["job_id"]})["data"]["result"]["error"]["code"],
                         "target_unreachable")

    def test_arriving_judges_a_target_only_the_player_sees_with_the_teams_sight(self):
        self.place("avatar:companion", BEHIND_THE_TABLE)  # the player at its spawn sees the book; the companion does not
        started = self.goal("look_at", target="obj:book")
        self.assertEqual(started["data"]["target_seen"], "now")
        self.assertEqual(self.host.goal_arrived("avatar:companion"), "succeeded")
        self.assertIn("obj:book", self.host.memory[COMPANION].entries)  # the arrival's look kept it on the map

    def test_the_players_exploration_fills_the_map_while_the_companion_is_not_asking(self):
        self.place("avatar:companion", BEHIND_THE_TABLE)
        self.place("avatar:player", OUT_OF_EVERY_SIGHT)
        self.assertEqual(self.ask("map.find", {"name": "book"})["data"], {"items": []})
        self.place("avatar:player", PLAYER_SPAWN)  # the player walks out and looks round
        self.host.clock.advance(1.0)  # time passes with no request from the companion
        self.place("avatar:player", OUT_OF_EVERY_SIGHT)  # and walks back
        found = self.ask("map.find", {"name": "book"})["data"]["items"]
        self.assertEqual([item["entity"]["id"] for item in found], ["obj:book"])
        self.assertEqual(found[0]["entity"]["seen"], "remembered")

    def test_a_player_request_or_an_explicit_sweep_fills_the_map_too(self):
        self.place("avatar:companion", BEHIND_THE_TABLE)
        self.place("avatar:player", OUT_OF_EVERY_SIGHT)
        self.ask("room.describe")
        self.place("avatar:player", PLAYER_SPAWN)
        self.host.player_command(query("room.describe", {}, "p-q"))  # the player's own request looks too
        self.place("avatar:player", OUT_OF_EVERY_SIGHT)
        self.assertIn("obj:book", self.host.memory[COMPANION].entries)
        self.host.memory[COMPANION].entries.clear()
        self.place("avatar:player", PLAYER_SPAWN)
        self.host.sight_sweep()
        self.assertIn("obj:book", self.host.memory[COMPANION].entries)

    def test_the_sweep_can_be_turned_off(self):
        self.host.policy = dataclasses.replace(self.host.policy, team_sight_interval_s=0.0)
        self.place("avatar:companion", BEHIND_THE_TABLE)
        self.place("avatar:player", OUT_OF_EVERY_SIGHT)
        self.ask("room.describe")
        self.place("avatar:player", PLAYER_SPAWN)
        self.host.clock.advance(1.0)
        self.place("avatar:player", OUT_OF_EVERY_SIGHT)
        self.assertNotIn("obj:book", self.host.memory[COMPANION].entries)


HIDDEN_SPOT = [-0.9, 0.0, -1.42]  # behind the table: neither avatar sees it from its spawn
IN_THE_OPEN = [0.8, 0.0, 0.9]  # on the rug: both spawns see it; neither hiding place does
NEAR_THE_HIDING_PLACES = [-1.2, 0.0, -1.4]  # both hiding places see it


def block(name: str) -> dict:
    """A 10 cm wooden block, the creation compiler's smallest part."""
    return {"schema": "garage_game.creation", "version": 1, "name": name, "seed": 7, "mount": "ground",
            "parts": [{"id": "block", "shape": "box", "position_m": [0, 0.05, 0], "rotation_deg": [0, 0, 0],
                       "size_m": [0.1, 0.1, 0.1], "material": "wood"}], "nodes": [], "edges": []}


class FactsFromTeamKnowledge(TeamCase):
    """Built, changed and removed facts take their subject and pin from the team's knowledge when written (Lane P's host
    fix of the reviews): in sight now as it is, remembered as last seen, never seen "something" with no id or pin."""

    def build(self, name, at, principal=PLAYER):
        made = self.act("creation.place", {"source": block(name), "placement": {"position_m": at}}, principal)
        self.assertTrue(made["ok"], made)
        return made["created"][0]

    def facts(self, kind):
        return self.journal(kind=kind)["entries"]

    def test_a_build_neither_avatar_sees_is_built_something(self):
        self.build("Secret vault", HIDDEN_SPOT, COMPANION)
        [fact] = self.facts("built")
        self.assertEqual(fact["line"], "Built something, on its own initiative")
        self.assertNotIn("subject", fact)
        self.assertNotIn("pin_m", fact)

    def test_a_change_to_a_creation_neither_avatar_has_seen_is_changed_something(self):
        made = self.build("Secret vault", HIDDEN_SPOT)
        revised = self.act("creation.revise", {"target": made, "source": block("Bigger vault")}, PLAYER,
                           expected_entities={made: 1})
        self.assertTrue(revised["ok"], revised)
        [fact] = self.facts("changed")
        self.assertEqual(fact["line"], "You changed something")
        self.assertNotIn("subject", fact)
        self.assertNotIn("pin_m", fact)
        self.assertNotIn("Vault", json.dumps(self.journal()))

    def test_a_thing_moved_out_of_sight_is_recorded_where_the_team_last_saw_it(self):
        made = self.build("Lantern", IN_THE_OPEN)
        self.ask("room.describe")  # the team sees it on the rug
        self.place("avatar:companion", BEHIND_THE_TABLE)
        self.place("avatar:player", OUT_OF_EVERY_SIGHT)
        moved = self.act("creation.revise", {"target": made, "placement": {"position_m": [0.3, 0.0, 0.9]},  # still unseen
                                             "source": block("Moved lantern")}, PLAYER, expected_entities={made: 1})
        self.assertTrue(moved["ok"], moved)
        removed = self.act("entity.remove", {"target": made}, PLAYER, expected_entities={made: 2})
        self.assertTrue(removed["ok"], removed)
        for kind in ("changed", "removed"):
            [fact] = self.facts(kind)
            self.assertEqual(fact["subject"], {"entities": [made], "name": "Lantern"})  # the last-seen name
            self.assertAlmostEqual(fact["pin_m"][0], IN_THE_OPEN[0], places=3)  # where the team last saw it
            self.assertAlmostEqual(fact["pin_m"][2], IN_THE_OPEN[2], places=3)

    def test_a_thing_in_sight_is_named_as_it_is_now(self):
        made = self.build("Lantern", IN_THE_OPEN)
        self.ask("room.describe")
        self.host.entities[made].display_name = "Blue lantern"  # changed in sight
        removed = self.act("entity.remove", {"target": made}, PLAYER, expected_entities={made: 1})
        self.assertTrue(removed["ok"], removed)
        [fact] = self.facts("removed")
        self.assertEqual(fact["line"], 'You removed "Blue lantern"')


class TheMapsHardBound(TeamCase):
    """The map's bound is hard (Lane P's host fix): past it, routine things out of sight, then routine things in sight,
    then creations out of the team's sight, then creations in sight, each least recently seen first; the targets of
    running goals and open tasks are never dropped."""

    def test_creations_are_dropped_past_a_lowered_bound_out_of_sight_first(self):
        self.host.policy = dataclasses.replace(self.host.policy, perception_memory_entries=1)
        first = self.act("creation.place", {"source": block("First"), "placement": {"position_m": IN_THE_OPEN}}, PLAYER)["created"][0]
        self.ask("room.describe")
        self.assertEqual(list(self.host.memory[COMPANION].entries), [first])
        self.place("avatar:companion", BEHIND_THE_TABLE)
        self.place("avatar:player", OUT_OF_EVERY_SIGHT)
        second = self.act("creation.place", {"source": block("Second"), "placement": {"position_m": NEAR_THE_HIDING_PLACES}},
                          PLAYER)["created"][0]
        self.ask("room.describe")  # the first is out of the team's sight now, the second in it
        self.assertEqual(list(self.host.memory[COMPANION].entries), [second])

    def test_a_running_goals_target_is_never_dropped(self):
        self.host.policy = dataclasses.replace(self.host.policy, perception_memory_entries=1)
        first = self.act("creation.place", {"source": block("First"), "placement": {"position_m": IN_THE_OPEN}}, PLAYER)["created"][0]
        self.ask("room.describe")
        self.place("avatar:companion", BEHIND_THE_TABLE)
        self.place("avatar:player", OUT_OF_EVERY_SIGHT)
        walk = self.goal("go_to", target=first)
        self.assertEqual(walk["data"]["target_seen"], "remembered")
        self.act("creation.place", {"source": block("Second"), "placement": {"position_m": NEAR_THE_HIDING_PLACES}}, PLAYER)
        self.ask("room.describe")
        self.assertEqual(list(self.host.memory[COMPANION].entries), [first])

    def test_the_bound_holds_with_many_creations_in_sight(self):
        self.host.policy = dataclasses.replace(self.host.policy, perception_memory_entries=3)
        for i in range(5):
            self.act("creation.place", {"source": block(f"Block {i}"), "placement": {"position_m": [0.2 + 0.15 * i, 0.0, 1.2]}}, PLAYER)
        self.ask("room.describe")
        self.assertEqual(len(self.host.memory[COMPANION].entries), 3)


class SinceAsInstants(TeamCase):
    def test_since_utc_compares_instants_whatever_the_fractional_digits(self):
        note = self.act("journal.note", {"text": "a note"})
        written = [e for e in self.journal()["entries"] if e["entry_id"] == note["data"]["entry_id"]][0]["at_utc"]
        whole = written[:19] + "Z"  # the same second, written without a fraction
        for since in (whole, written[:19] + ".0Z", written[:19] + ".000000Z"):
            with self.subTest(since=since):
                self.assertEqual(len(self.journal(since_utc=since)["entries"]), 1)
        later = written[:17] + f"{int(written[17:19]) + 1:02d}" + ".5Z"
        self.assertEqual(self.journal(since_utc=later)["entries"], [])

    def test_a_time_that_is_not_a_real_instant_is_refused(self):
        result = self.ask("journal.read", {"since_utc": "2026-99-01T00:00:00Z"})
        self.assertEqual((result["error"]["code"], result["error"]["field_path"]), ("request_invalid", "$.args.since_utc"))


class DropBeside(TeamCase):
    def test_a_drop_goes_beside_then_behind_when_an_avatar_stands_in_front(self):
        self.host.move_avatar("avatar:companion", [0.45, 0.0, 0.25])
        self.assertTrue(self.act("entity.grab", {"actor": "avatar:companion", "target": "obj:doorstop"})["ok"])
        self.host.facing["avatar:companion"] = (1.0, 0.0)  # it faces +X
        self.place("avatar:player", [0.55, 0.0, 0.25])  # in its front spot
        self.assertTrue(self.act("entity.release", {"actor": "avatar:companion"})["ok"])
        x, _y, z = self.host.entities["obj:doorstop"].position
        self.assertAlmostEqual(x, 0.45, places=3)
        self.assertLess(z, 0.25)  # a quarter turn to the left of +X is -Z

    def test_with_nobody_in_front_it_goes_in_front(self):
        self.host.move_avatar("avatar:companion", [0.45, 0.0, 0.25])
        self.act("entity.grab", {"actor": "avatar:companion", "target": "obj:doorstop"})
        self.host.facing["avatar:companion"] = (1.0, 0.0)
        self.act("entity.release", {"actor": "avatar:companion"})
        x, _y, z = self.host.entities["obj:doorstop"].position
        self.assertGreater(x, 0.45)
        self.assertAlmostEqual(z, 0.25, places=3)


if __name__ == "__main__":
    unittest.main()
