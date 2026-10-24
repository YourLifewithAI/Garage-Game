"""Behavioural alignment: the same steps on the mock host and on the real game host, with the same expected outcome.

test_kernel_alignment.py compares the two hosts' constants; constants alone missed behaviour (Codex's review of Lane A's
round 2). Each scenario here is written once against a few moves (ask, walk the companion somewhere, finish a goal's
job) and runs on the mock below and on the kernel host in test_real_host.py (AlignmentOnTheRealHost). Both must give
the scenario's expected outcome.
"""
from __future__ import annotations

import json
import re
import unittest

from support import (BEHIND_BOX, COMPANION, FAR_IN_SIGHT, GUBBLE, IN_THE_OPEN, OUT_OF_REACH, OUTSIDE_ROOM, FakeClock,
                     HostPolicy, cast, command, contract_problems, glow, new_host, query)

from garage_game_companion import mock_nature

BEHIND_THE_BOX = [1.6, 0.0, 0.2]  # the box hides the book from the companion; the player at its spawn still sees it
HIDDEN_SPOT = [-0.9, 0.0, -1.42]  # behind the table: neither avatar sees it from beside the player


def block(name: str) -> dict:
    """A 10 cm wooden block, the creation compiler's smallest part."""
    return {"schema": "enfractal.creation", "version": 1, "name": name, "seed": 7, "mount": "ground",
            "parts": [{"id": "block", "shape": "box", "position_m": [0, 0.05, 0], "rotation_deg": [0, 0, 0],
                       "size_m": [0.1, 0.1, 0.1], "material": "wood"}], "nodes": [], "edges": []}


EFFECT_ID = re.compile(r"effect:[a-z2-7]{26}")
# capabilities.list's items for storybook_wild v4, including the command route and enforced limits.
GLOW_SUMMARY = {"capability": "glow", "category": "light", "params": {"intensity": {"min": 0.2, "max": 1.0}},
                "area_radius_max_m": 1.5, "duration_max_s": 600, "reach_m": 2.0, "max_active": 3,
                "command_op": "effect.start"}
# Bubbles: one bubble at a time since the founder's redesign (decision 59), whatever the pack's max_active says.
BUBBLES_SUMMARY = {"capability": "bubbles", "category": "float", "params": {"intensity": {"min": 0.2, "max": 1.0}},
                   "area_radius_max_m": 1.0, "duration_max_s": 60, "reach_m": 2.0, "max_active": 1,
                   "command_op": "effect.start"}
FIREWORKS_SUMMARY = {"capability": "fireworks", "category": "burst", "params": {"intensity": {"min": 0.3, "max": 1.0}},
                     "area_radius_max_m": 2.0, "duration_max_s": 6, "reach_m": 4.0, "max_active": 3,
                     "command_op": "effect.start"}
GROW_SUMMARY = {"capability": "grow", "category": "growth", "params": {"intensity": {"min": 0.1, "max": 0.5}},
                "area_radius_max_m": 1.0, "duration_max_s": 600, "reach_m": 2.0, "max_active": 1,
                "command_op": "nature.cast"}
# Fire is listed locked until the team's first rest by the starting campfire (decision 72): a fresh team on either host.
FIRE_SUMMARY = {"capability": "fire", "category": "fire", "params": {"intensity": {"min": 0.2, "max": 1.0}},
                "area_radius_max_m": 1.5, "duration_max_s": 60, "reach_m": 2.0, "max_active": 1,
                "command_op": "nature.cast", "locked": True}
LACKING = "teleport"
# Nature runs on both hosts (the mock's mock_nature.py since the nature packet, 24 October): its answers, words included, are the
# same on both, so a kernel change that moves a word, a check's order or a code fails here. Neither host's test room has plants,
# and a fresh team has not rested by the starting campfire, so Fire stays locked on both: Fire's casts, the campfires and undo are
# test_nature.py's on the mock, and test_kernel_alignment.py holds their words and numbers to the kernel's.


def refusal(result: dict):
    """(code, field_path) of a refusal, or "ok"."""
    return "ok" if result["ok"] else (result["error"]["code"], result["error"].get("field_path"))


class AlignmentScenarios:
    """A subclass provides ask(message), companion_to(position), finish(job_id) -> the job's final state,
    companion_home() (the companion beside the player or at its spawn, as the glow spots in support.py assume) and
    wait(seconds) on the host's clock."""

    async def ask(self, message: dict) -> dict:
        raise NotImplementedError

    async def companion_to(self, position: list[float]) -> None:
        raise NotImplementedError

    async def finish(self, job_id: str) -> str:
        raise NotImplementedError

    async def companion_home(self) -> None:
        raise NotImplementedError

    async def wait(self, seconds: float) -> None:
        raise NotImplementedError

    # Outcome keys a host cannot yet be held to (each with its reason in the subclass).
    unchecked: tuple[str, ...] = ()
    # Known gaps of one host against the contract, by name, each with its change request: the scenario checks that host still
    # answers as it does today, so the fix fails the test and the gap is dropped.
    known_gaps: dict[str, str] = {}

    def next_id(self, prefix: str) -> str:
        self._n = getattr(self, "_n", 0) + 1
        return f"{prefix}-{id(self) % 100000}-{self._n}"

    async def test_a_target_only_the_player_sees_is_in_sight_and_its_arrival_succeeds(self):
        await self.companion_to(BEHIND_THE_BOX)
        observed = await self.ask(query("observe", {"actor": "avatar:companion"}, self.next_id("q")))
        own_sight = {item["id"] for item in observed["data"]["visible"]}
        listed = await self.ask(query("entity.inspect", {"target": "obj:book"}, self.next_id("q")))
        look = await self.ask(command("goal.set", {"actor": "avatar:companion", "goal": "look_at", "target": "obj:book"},
                                      self.next_id("look")))
        outcome = {
            "the companion's own eyes see the book": "obj:book" in own_sight,
            "the book is in sight now": listed["ok"] and listed["data"]["entity"].get("seen"),
            "look_at accepted": (look["ok"], look.get("data", {}).get("target_seen")),
            "its job": await self.finish(look["job_id"]) if look["ok"] else None,
        }
        self.assertEqual(outcome, {
            "the companion's own eyes see the book": False,
            "the book is in sight now": "now",
            "look_at accepted": (True, "now"),
            "its job": "succeeded",
        })


    async def test_a_build_neither_avatar_sees_stays_unnamed_and_cannot_be_changed_or_removed_unseen(self):
        """Hidden build, unseen revision, unseen removal: a creation the companion places where neither avatar's eyes
        reach is "something" in the journal, with no id and no pin, and the team cannot name it: revising or removing it
        is refused exactly as for an id that does not exist, and writes nothing."""
        placed = await self.ask(command("creation.place", {"source": block("Hidden vault"),
                                                           "placement": {"position_m": HIDDEN_SPOT}}, self.next_id("hide")))
        self.assertTrue(placed["ok"], placed)
        made = placed["created"][0]
        inspect = await self.ask(query("entity.inspect", {"target": made}, self.next_id("q")))
        journal = (await self.ask(query("journal.read", {"kind": "built", "limit": 50}, self.next_id("q"))))["data"]
        built = [e for e in journal["entries"] if e["revision"] == placed["revision"]]
        before = (await self.ask(query("journal.read", {"limit": 50}, self.next_id("q"))))["data"]

        async def refused(op, args_for):
            answers = []
            for target in (made, "creation:zz-not-here"):
                result = await self.ask(command(op, args_for(target), self.next_id("try"), expected_revision=placed["revision"]))
                answers.append(None if result["ok"] else (result["error"]["code"], result["error"]["field_path"],
                                                           result["error"]["message"]))
            return answers[0] if answers[0] == answers[1] else answers

        revise = await refused("creation.revise", lambda t: {"target": t, "source": block("Renamed vault")})
        remove = await refused("entity.remove", lambda t: {"target": t})
        # room.undo takes back nature changes only: a build is not one, and nothing is undone (the kernel's NatureUndo).
        undo = {to: refusal(await self.ask(command("room.undo", {"to_revision": to}, self.next_id("undo"),
                                                    expected_revision=placed["revision"])))
                for to in (placed["revision"] - 1, placed["revision"])}
        after = (await self.ask(query("journal.read", {"limit": 50}, self.next_id("q"))))["data"]
        outcome = {
            "inspect": inspect["ok"] or inspect["error"]["code"],
            "built facts": [(e["line"], "subject" in e, "pin_m" in e) for e in built],
            "revise refused like an unknown id": revise and revise[0],
            "remove refused like an unknown id": remove and remove[0],
            "undo of the build": undo[placed["revision"] - 1],
            "undo to now": undo[placed["revision"]],
            "nothing written since": after == before,
            "named anywhere": "Hidden vault" in json.dumps(after) or made in json.dumps(after),
        }
        expected = {
            "inspect": "target_not_found",
            "built facts": [("Built something, on its own initiative", False, False)],
            "revise refused like an unknown id": "target_not_found",
            "remove refused like an unknown id": "target_not_found",
            "undo of the build": ("unsupported_capability", "$.args.to_revision"),
            "undo to now": ("invalid_args", "$.args.to_revision"),
            "nothing written since": True,
            "named anywhere": False,
        }
        for key in self.unchecked:
            outcome.pop(key)
            expected.pop(key)
        self.assertEqual(outcome, expected)



    # ----- Nature: checked and answered alike on both hosts -----

    async def test_nature_commands_are_checked_and_answered_alike(self):
        """Every check of a nature command gives the same answer on both hosts, its words included: the contract's args, the
        retired trail, a stale revision, a repair of something unseen, Fire locked before the first rest (before any other check
        of it), then Grow's own checks in the kernel's order (CheckCast: the island's radius, the island, the Gubble's reach, the
        team's sight) and, where the test room has no plant, why nothing grows. Nothing changes: the revision stays where it was."""
        await self.companion_home()
        bloom = {"capability": "grow", "center_m": IN_THE_OPEN, "radius_m": 0.25, "duration_s": 30}
        start = (await self.ask(query("room.describe", {}, self.next_id("q"))))["data"]["revision"]

        async def sent(op, args, **options):
            return refusal(await self.ask(command(op, args, self.next_id("nature"), **options)))

        checked = {
            "an argument it does not know": await sent("nature.cast", {**bloom, "intensity": 2}, preview=True),
            "an effect, not nature": await sent("nature.cast", {**bloom, "capability": "glow"}, preview=True),
            "a radius past the engine's": await sent("nature.cast", {**bloom, "radius_m": 2}, preview=True),
            "a place of two numbers": await sent("nature.cast", {**bloom, "center_m": [0, 0]}, preview=True),
            "a made-up preview id": await sent("nature.cast", {**bloom, "capability": "fire", "preview_id": "preview-1"}),
            "a trail that is neither": await sent("nature.trail", {"enabled": "yes"}),
            "a repair of an object": await sent("nature.repair", {"target": "obj:box"}),
            "a repair of something unseen": await sent("nature.repair", {"target": "creation:zz-not-here"}),
            "a stale revision": await sent("nature.trail", {"enabled": False}, expected_revision=10 ** 6, preview=True),
            "the retired flower trail": await sent("nature.trail", {"enabled": False}, preview=True),
            # Decision 72: a fresh team has not rested by the starting campfire yet, so the Gubble has no Fire on either host.
            "Fire before the first rest": await sent("nature.cast", {**bloom, "capability": "fire"}, preview=True),
        }
        self.assertEqual(checked, {
            "an argument it does not know": ("field_unknown", "$.args.intensity"),
            "an effect, not nature": ("request_invalid", "$.args.capability"),
            "a radius past the engine's": ("request_invalid", "$.args.radius_m"),
            "a place of two numbers": ("request_invalid", "$.args.center_m"),
            "a made-up preview id": ("request_invalid", "$.args.preview_id"),
            "a trail that is neither": ("request_invalid", "$.args.enabled"),
            "a repair of an object": ("request_invalid", "$.args.target"),
            "a repair of something unseen": ("target_not_found", "$.args.target"),
            "a stale revision": ("revision_conflict", "$.expected_revision"),
            "the retired flower trail": ("unsupported_capability", "$.op"),
            "Fire before the first rest": ("permission_denied", "$.args.capability"),
        })

        async def said(args, **options):
            result = await self.ask(command("nature.cast", args, self.next_id("nature"), **options))
            error = result.get("error", {})
            return "ok" if result["ok"] else (error["code"], error.get("field_path"), error["message"], error["retryable"], error.get("allowed"))

        m = mock_nature
        answered = {
            "Grow where no plant grows, previewed": await said(bloom, preview=True),
            "Grow where no plant grows": await said(bloom),
            "Grow wider than the island allows": await said({**bloom, "radius_m": 1.2}, preview=True),
            "Grow outside the island": await said({**bloom, "center_m": OUTSIDE_ROOM}, preview=True),
            "Grow beyond the Gubble's reach": await said({**bloom, "center_m": OUT_OF_REACH}, preview=True),
            "Grow where neither avatar sees": await said({**bloom, "center_m": BEHIND_BOX}, preview=True),
            "too wide and beyond reach": await said({**bloom, "radius_m": 1.2, "center_m": OUT_OF_REACH}, preview=True),
            "Fire too long before the first rest": await said({**bloom, "capability": "fire", "duration_s": 61}, preview=True),
            "a Fireline before the first rest": await said({**bloom, "capability": "fire", "end_m": FAR_IN_SIGHT}, preview=True),
        }
        no_plant = ("invalid_args", "$.args.center_m", m.GROW_NO_PLANT, False, None)
        locked = ("permission_denied", "$.args.capability", m.FIRE_NOT_LEARNED, False, None)
        self.assertEqual(answered, {
            "Grow where no plant grows, previewed": no_plant,
            "Grow where no plant grows": no_plant,
            "Grow wider than the island allows": ("invalid_args", "$.args.radius_m", m.RADIUS_TOO_BIG, False, 1.0),
            "Grow outside the island": ("out_of_bounds", "$.args.center_m", m.CAST_OUTSIDE, False, None),
            "Grow beyond the Gubble's reach": ("out_of_bounds", "$.args.center_m", m.CAST_TOO_FAR, True, None),
            "Grow where neither avatar sees": ("target_not_found", "$.args.center_m", m.CAST_UNSEEN, False, None),
            "too wide and beyond reach": ("invalid_args", "$.args.radius_m", m.RADIUS_TOO_BIG, False, 1.0),
            "Fire too long before the first rest": locked,
            "a Fireline before the first rest": locked,
        })
        described = await self.ask(query("nature.describe", {}, self.next_id("q")))
        self.assertTrue(described["ok"], described)
        data = dict(described["data"])
        weather = data.pop("weather")
        notice = data.pop("notice")
        self.assertEqual(notice, "", "nature waits for nothing: its notice is empty (the contract)")
        self.assertIn(weather["pattern"], ("fair", "showers", "rain"))
        self.assertTrue(0 <= weather["rain"] <= 1 and len(weather["wind_mps"]) == 3, weather)
        # A fresh island under storybook_wild v5: no nature change yet, nothing burning, the pack's wallet (decision 26: empty).
        self.assertEqual(data, {"nature_revision": 0, "trail_enabled": False, "planted_cells": 0, "burning_cells": 0,
                                "wallet": {"copper": 0, "leaf": 0, "stone": 0, "wood": 0}, "scorched": [], "can_undo": False})
        end = (await self.ask(query("room.describe", {}, self.next_id("q"))))["data"]["revision"]
        self.assertEqual(end, start, "nothing in this scenario changes the room")

    # ----- The mechanics round (M0): the contract's stubs and its boundary, alike on both hosts -----

    async def test_the_mechanics_rounds_boundary_holds_alike(self):
        """The companion never reaches the player's new ops, never carries, places, pushes or fetches a Fractal (the same
        answer whether or not it exists, before anything is looked up), may go to one, and casts the Bubbles expertise only
        under the targets rule: a Bubble on an avatar is previewed first, only the carry and the lift name a target, and the
        target is an avatar. The set-mode's difficulty is not built yet (M3); the Bubbles are (M9): a carry on the player is
        centred where they stand (this spot is not), and a boat needs shallow water (neither host's room has any)."""
        await self.companion_home()
        spot = IN_THE_OPEN
        carry = {"capability": "bubble_carry", "center_m": spot, "radius_m": 0.08, "duration_s": 20, "target": "avatar:player"}

        async def sent(op, args, **options):
            return refusal(await self.ask(command(op, args, self.next_id("m0"), **options)))

        outcome = {
            "pick up a Fractal": await sent("item.pickup", {"target": "fractal:k3x9q2"}),
            "gather a log": await sent("item.gather", {"target": "obj:book"}),
            "set a knack": await sent("knack.set", {"slot": 1, "knack": "quick_feet"}),
            "mark a receipt": await sent("receipt.mark", {"action_id": "grow-0002", "mark": "yes"}),
            "reset Trust": await sent("trust.reset", {}),
            "spend Fractals": await sent("fractal.spend", {"grow": "heart"}),
            "choose a difficulty": await sent("world.set_mode", {"mode": "challenge", "difficulty": "gentle"}),
            "grab a Fractal": await sent("entity.grab", {"target": "fractal:k3x9q2", "actor": GUBBLE}),
            "grab a Fractal nobody placed": await sent("entity.grab", {"target": "fractal:zz9", "actor": GUBBLE}),
            "fetch a Fractal": await sent("goal.set", {"actor": GUBBLE, "goal": "fetch", "target": "fractal:k3x9q2"}),
            "place a Fractal": await sent("entity.place", {"target": "fractal:k3x9q2", "placement": {"position_m": spot}}),
            "push a Fractal": await sent("entity.push", {"target": "fractal:k3x9q2", "distance_m": 0.2, "actor": GUBBLE}),
            "go to a Fractal": await sent("goal.set", {"actor": GUBBLE, "goal": "go_to", "target": "fractal:k3x9q2"}),
            "a carry on the player, previewed": await sent("nature.cast", carry, preview=True),
            "a carry on the player, unpreviewed": await sent("nature.cast", carry),
            "a boat at a point": await sent("nature.cast", {"capability": "bubble_boat", "center_m": spot, "radius_m": 0.1,
                                                            "duration_s": 20}, preview=True),
            "Grow on the player": await sent("nature.cast", {"capability": "grow", "center_m": spot, "radius_m": 0.25,
                                                              "duration_s": 30, "target": "avatar:player"}, preview=True),
            "a carry of a thing": await sent("nature.cast", {**carry, "target": "obj:box"}, preview=True),
        }
        self.assertEqual(outcome, {
            "pick up a Fractal": ("permission_denied", "$.op"),
            "gather a log": ("permission_denied", "$.op"),
            "set a knack": ("permission_denied", "$.op"),
            "mark a receipt": ("permission_denied", "$.op"),
            "reset Trust": ("permission_denied", "$.op"),
            "spend Fractals": ("permission_denied", "$.op"),
            "choose a difficulty": ("permission_denied", "$.op"),
            "grab a Fractal": ("permission_denied", "$.args.target"),
            "grab a Fractal nobody placed": ("permission_denied", "$.args.target"),
            "fetch a Fractal": ("permission_denied", "$.args.target"),
            "place a Fractal": ("permission_denied", "$.args.target"),
            "push a Fractal": ("permission_denied", "$.args.target"),
            "go to a Fractal": ("target_not_found", "$.args.target"),
            "a carry on the player, previewed": ("invalid_args", "$.args.center_m"),
            "a carry on the player, unpreviewed": ("request_invalid", "$"),
            "a boat at a point": ("invalid_args", "$.args.center_m"),
            "Grow on the player": ("request_invalid", "$.args.capability"),
            "a carry of a thing": ("request_invalid", "$.args.target"),
        })

    # ----- The Bubble (M9): the carry, alike on both hosts -----

    async def test_the_bubble_carries_the_player_alike(self):
        """A carry on the player: previewed (the ghost's preview_id), refused if cast before the ghost's lead, cast a moment later
        naming it (transient, its effect: id created), popped by the Gubble's own stop at once (popped), its receipt carrying the
        kind, the target, carried and how it ended; a bubble at a point never forms round the player."""
        await self.companion_home()
        inspected = await self.ask(query("entity.inspect", {"target": "avatar:player"}, self.next_id("q")))
        feet = inspected["data"]["entity"]["position_m"]
        carry = {"capability": "bubble_carry", "center_m": feet, "radius_m": 0.08, "duration_s": 20, "target": "avatar:player"}
        preview = await self.ask(command("nature.cast", carry, self.next_id("ghost"), preview=True))
        token = preview.get("data", {}).get("preview_id", "0" * 32)
        too_soon = await self.ask(command("nature.cast", {**carry, "preview_id": token}, self.next_id("carry")))
        await self.wait(1.2)
        cast = await self.ask(command("nature.cast", {**carry, "preview_id": token}, self.next_id("carry")))
        stop = await self.ask(command("goal.stop", {}, self.next_id("stop")))
        receipt = (await self.ask(query("receipt.lookup", {"action_id": cast["action_id"]}, self.next_id("q"))))["data"].get("receipt", {}).get("data", {})
        at_the_feet = await self.ask(command("nature.cast", {k: v for k, v in carry.items() if k != "target"}, self.next_id("carry")))
        bubble = cast.get("data", {}).get("bubble", "")
        outcome = {
            "the preview": (refusal(preview), len(token), preview.get("affected")),
            "cast before the ghost's lead": refusal(too_soon),
            "the cast": (refusal(cast), cast.get("transient"), cast.get("created") == [bubble], bubble.startswith("effect:")),
            "the Gubble's stop pops it": bubble in stop.get("affected", []),
            "its receipt": {k: receipt.get(k) for k in ("capability", "target", "carried", "ended")},
            "a bubble at the player's feet": refusal(at_the_feet),
        }
        self.assertEqual(outcome, {
            "the preview": ("ok", 32, ["avatar:player"]),
            "cast before the ghost's lead": ("not_ready", "$.args.preview_id"),
            "the cast": ("ok", True, True, True),
            "the Gubble's stop pops it": True,
            "its receipt": {"capability": "bubble_carry", "target": "avatar:player", "carried": True, "ended": "popped"},
            "a bubble at the player's feet": ("invalid_args", "$.args.center_m"),
        })

    # ----- The Bubble as redesigned and the Gubble's lead (the M9 follow-up): alike on both hosts -----

    async def test_the_bubble_and_its_lead_alike(self):
        """effect.start of Bubbles: on the player's feet a carry, the companion's previewed first (its ghost) and cast a moment
        later; goal.set lead names only a bubble and a spot; set, it runs as the Gubble's job; the Gubble's own stop pops the
        bubble it leads (popped) and the lead's receipt says stopped; elsewhere the Bubble is a bubble to walk into, and a recast
        pops the one there."""
        await self.companion_home()
        inspected = await self.ask(query("entity.inspect", {"target": "avatar:player"}, self.next_id("q")))
        feet = inspected["data"]["entity"]["position_m"]
        beside = [feet[0] + 0.3, feet[1], feet[2] + 0.1]

        def bubble(spot):
            return {"capability": "bubbles", "params": {}, "area": {"center_m": spot, "radius_m": 0.5}, "duration_s": 20}

        unpreviewed = await self.ask(command("effect.start", bubble(feet), self.next_id("bubble")))
        preview = await self.ask(command("effect.start", bubble(feet), self.next_id("ghost"), preview=True))
        await self.wait(1.2)
        carry = await self.ask(command("effect.start", bubble(feet), self.next_id("bubble")))
        id_ = carry.get("data", {}).get("bubble", "effect:" + "b" * 26)
        on_the_player = await self.ask(command("goal.set", {"actor": GUBBLE, "goal": "lead", "target": "avatar:player", "position_m": IN_THE_OPEN},
                                               self.next_id("lead")))
        no_spot = await self.ask(command("goal.set", {"actor": GUBBLE, "goal": "lead", "target": id_}, self.next_id("lead")))
        unknown = await self.ask(command("goal.set", {"actor": GUBBLE, "goal": "lead", "target": "effect:" + "a" * 26, "position_m": IN_THE_OPEN},
                                         self.next_id("lead")))
        led = await self.ask(command("goal.set", {"actor": GUBBLE, "goal": "lead", "target": id_, "position_m": IN_THE_OPEN}, self.next_id("lead")))
        stop = await self.ask(command("goal.stop", {}, self.next_id("stop")))

        async def receipt(result):
            looked = await self.ask(query("receipt.lookup", {"action_id": result.get("action_id", "none")}, self.next_id("q")))
            return looked.get("data", {}).get("receipt", {}).get("data", {})

        walk_in = await self.ask(command("effect.start", bubble(beside), self.next_id("bubble")))
        recast = await self.ask(command("effect.start", bubble([beside[0] + 0.05, beside[1], beside[2]]), self.next_id("bubble")))
        outcome = {
            "the Bubble on the player, unpreviewed": refusal(unpreviewed),
            "its preview": (refusal(preview), preview.get("data", {}).get("kind")),
            "cast after the ghost": (refusal(carry), carry.get("data", {}).get("kind"), carry.get("transient"), carry.get("data", {}).get("carried")),
            "a lead of the player's avatar": refusal(on_the_player),
            "a lead with no spot": refusal(no_spot),
            "a lead of an unknown bubble": refusal(unknown),
            "the lead": (refusal(led), led.get("data", {}).get("goal"), led.get("data", {}).get("bubble") == id_, "job_id" in led),
            "the Gubble's stop pops what it leads": id_ in stop.get("affected", []),
            "the lead's receipt": (await receipt(led)).get("ended"),
            "the bubble's receipt": ((await receipt(carry)).get("kind"), (await receipt(carry)).get("ended")),
            "elsewhere": (refusal(walk_in), walk_in.get("data", {}).get("kind")),
            "a recast pops it": (refusal(recast), (await receipt(walk_in)).get("ended")),
        }
        self.assertEqual(outcome, {
            "the Bubble on the player, unpreviewed": ("revision_conflict", "$.args.area.center_m"),
            "its preview": ("ok", "carry"),
            "cast after the ghost": ("ok", "carry", True, True),
            "a lead of the player's avatar": ("request_invalid", "$.args.target"),
            "a lead with no spot": ("request_invalid", "$.args.position_m"),
            "a lead of an unknown bubble": ("target_not_found", "$.args.target"),
            "the lead": ("ok", "lead", True, True),
            "the Gubble's stop pops what it leads": True,
            "the lead's receipt": "stopped",
            "the bubble's receipt": ("carry", "popped"),
            "elsewhere": ("ok", "walk_in"),
            "a recast pops it": ("ok", "popped"),
        })

    # ----- The Gubble's eyes (view.look): the same refusals on both hosts; a picture wherever the host can draw -----

    # Whether this host draws pictures: the mock always (a placeholder), the kernel host only with a renderer (never headless).
    renders: bool = True

    async def test_the_gubbles_eyes_refuse_alike_and_look_when_they_can_draw(self):
        from garage_game_companion import eyes

        async def looked(args):
            result = await self.ask(query("view.look", {"actor": "avatar:companion", **args}, self.next_id("look")))
            if result["ok"]:
                data = result["data"]
                return ("ok", data["actor"], sorted(data), data["image"]["untrusted"],
                        data["image"]["bytes"] <= eyes.LOOK_IMAGE_MAX_BYTES, 0 <= data["view_heading_deg"] < 360)
            return (result["error"]["code"], result["error"].get("field_path"), result["error"]["message"],
                    result["error"]["retryable"], "data" in result)

        await self.wait(1.1)  # whatever earlier scenarios spent, a look is available again
        outcome = {
            "the player's eyes": await looked({"actor": "avatar:player"}),
            "tilted too far": (await looked({"pitch_deg": 61}))[:2],
            "an unknown argument": (await looked({"zoom": 2}))[:2],
            "a look": await looked({"yaw_deg": 30, "pitch_deg": 20}),
        }
        burst = [(await looked({}))[0] for _ in range(eyes.LOOK_BURST + 1)]
        outcome["a quick burst ends rate limited"] = "rate_limited" in burst
        facts = ["actor", "eye_m", "fov_deg", "heading_deg", "image", "position_m", "view_heading_deg", "view_pitch_deg"]
        expected = {
            "the player's eyes": ("actor_denied", "$.args.actor", eyes.LOOK_ACTOR_MESSAGE, False, False),
            "tilted too far": ("invalid_args", "$.args.pitch_deg"),
            "an unknown argument": ("field_unknown", "$.args.zoom"),
            "a look": ("ok", "avatar:companion", facts, True, True, True) if self.renders
            else ("unavailable", None, eyes.NO_RENDERER_MESSAGE, False, False),
            "a quick burst ends rate limited": True,
        }
        if self.renders and outcome["a look"][0] == "ok":
            # time_of_day is there only where the room has a look (the kernel host's island clock).
            outcome["a look"] = (*outcome["a look"][:2], [k for k in outcome["a look"][2] if k != "time_of_day"], *outcome["a look"][3:])
        self.assertEqual(outcome, expected)

    # ----- Glow (docs/runs/RUN-2-GLOW.md section 5): the island's rules, on both hosts -----

    async def stop_all_glows(self) -> None:
        """Only the companion casts through the link, so its stop of all ends every glow a scenario left."""
        self.assertTrue((await self.ask(command("effect.stop", {"effect": "all"}, self.next_id("clear"))))["ok"])

    async def stop_one(self, effect_id: str) -> tuple:
        """A stop by id: (the ids it ended, how many). By id, the count is the glows only on both hosts."""
        result = await self.ask(command("effect.stop", {"effect": effect_id}, self.next_id("stop")))
        self.assertTrue(result["ok"], result)
        return result.get("affected", []), result["data"]["effects_stopped"]

    async def test_capabilities_list_answers_the_islands_rules(self):
        async def listed(args):
            result = await self.ask(query("capabilities.list", args, self.next_id("q")))
            return result["data"] if result["ok"] else refusal(result)

        outcome = {
            "all": await listed({}),
            "light": await listed({"category": "light"}),
            "float": await listed({"category": "float"}),
            "burst": await listed({"category": "burst"}),
            "air": await listed({"category": "air"}),
            "first page of one": await listed({"limit": 1, "cursor": "0"}),
            "the next page": await listed({"limit": 1, "cursor": "1"}),
            "the last page": await listed({"limit": 1, "cursor": "4"}),
            "past the last": await listed({"limit": 1, "cursor": "5"}),
            "the rest from the second": await listed({"cursor": "1"}),
            "beyond the end": await listed({"cursor": "6"}),
            "a signed cursor": await listed({"cursor": "+0"}),
            "a cursor of words": await listed({"cursor": "next"}),
        }
        self.assertEqual(outcome, {
            "all": {"items": [BUBBLES_SUMMARY, FIRE_SUMMARY, FIREWORKS_SUMMARY, GLOW_SUMMARY, GROW_SUMMARY]},
            "light": {"items": [GLOW_SUMMARY]},
            "float": {"items": [BUBBLES_SUMMARY]},
            "burst": {"items": [FIREWORKS_SUMMARY]},
            "air": {"items": []},
            "first page of one": {"items": [BUBBLES_SUMMARY], "next_cursor": "1"},
            "the next page": {"items": [FIRE_SUMMARY], "next_cursor": "2"},
            "the last page": {"items": [GROW_SUMMARY]},
            "past the last": {"items": []},
            "the rest from the second": {"items": [FIRE_SUMMARY, FIREWORKS_SUMMARY, GLOW_SUMMARY, GROW_SUMMARY]},
            "beyond the end": ("invalid_args", "$.args.cursor"),
            "a signed cursor": ("invalid_args", "$.args.cursor"),
            "a cursor of words": ("invalid_args", "$.args.cursor"),
        })

    async def test_the_companion_glows_itself_and_a_spot_in_sight(self):
        await self.companion_home()
        await self.stop_all_glows()
        own = glow(self.next_id("glow"), params={})  # the defaults fill what is left out
        mine = await self.ask(own)
        spot = await self.ask(glow(self.next_id("glow"), at=IN_THE_OPEN, params={"intensity": 1}, radius=1.5, duration=600))
        preview = await self.ask(glow(self.next_id("glow"), at=IN_THE_OPEN, params={"intensity": 0.9}, preview=True))
        replay = await self.ask(own)
        conflict = await self.ask(glow(own["action_id"], params={"intensity": 0.3}))
        lookup = await self.ask(query("receipt.lookup", {"action_id": own["action_id"]}, self.next_id("q")))
        light, wisp = mine.get("created", ["?"])[0], spot.get("created", ["?"])[0]
        data = mine.get("data", {})
        outcome = {
            "self": refusal(mine),
            "self receipt": (mine.get("transient"), mine.get("created") == mine.get("affected") == [light],
                             bool(EFFECT_ID.fullmatch(light)), data.get("effect") == light),
            "self data": (sorted(data), data.get("capability"), data.get("category"), data.get("target"), data.get("params"),
                          data.get("area", {}).get("radius_m"), data.get("duration_s"), data.get("variant"), data.get("touch")),
            "point": refusal(spot),
            "point data": (spot["data"]["target"], spot["data"]["area"], spot["data"]["params"], spot["data"]["duration_s"])
            if spot["ok"] else None,
            "preview": (refusal(preview), preview["preview"], "created" in preview, preview.get("data", {}).get("target"),
                        "effect" in preview.get("data", {}), "expires_utc" in preview.get("data", {}),
                        preview.get("data", {}).get("params")),
            "replay": (replay["replayed"], replay.get("created")) == (True, [light]),
            "conflict": refusal(conflict),
            "lookup": (lookup["data"]["found"], lookup["data"].get("receipt", {}).get("created")) == (True, [light]),
            "the point by id": await self.stop_one(wisp),
            "the same stop again": await self.stop_one(wisp),
            "an unknown id": await self.stop_one("effect:" + "a" * 26),
        }
        stop = await self.ask(command("goal.stop", {}, self.next_id("stop")))
        outcome["goal.stop ends its own glow"] = stop["ok"] and light in stop["affected"]
        outcome["nothing left to stop"] = await self.stop_one(light)
        self.assertEqual(outcome, {
            "self": "ok",
            "self receipt": (True, True, True, True),
            "self data": (["area", "capability", "category", "duration_s", "effect", "expires_utc", "params", "target", "touch", "variant"],
                          "glow", "light", "self", {"intensity": 0.6}, 0.5, 60, "lantern", "plain"),
            "point": "ok",
            "point data": ("point", {"center_m": IN_THE_OPEN, "radius_m": 1.5}, {"intensity": 1}, 600),
            "preview": ("ok", True, False, "point", False, False, {"intensity": 0.9}),
            "replay": True,
            "conflict": ("action_id_conflict", "$.action_id"),
            "lookup": True,
            "the point by id": ([wisp], 1),
            "the same stop again": ([], 0),
            "an unknown id": ([], 0),
            "goal.stop ends its own glow": True,
            "nothing left to stop": ([], 0),
        })

    async def test_the_companion_cannot_glow_beyond_the_islands_rules(self):
        """Each refusal with the kernel's code and field, the order of the checks where two apply at once, and
        nothing started by any of them: afterwards the pack's three glows still fit, and a fourth does not."""
        await self.companion_home()
        await self.stop_all_glows()
        cases = {
            "the player": glow("x", targets=["avatar:player"]),
            "a thing in sight": glow("x", targets=["obj:rug"]),
            "itself and a thing": glow("x", targets=[GUBBLE, "obj:rug"]),
            "a thing that is not there": glow("x", targets=["obj:zz_not_here"]),
            "an ability the island lacks": glow("x", capability=LACKING),
            "a param it does not take": glow("x", params={"speed": 1}),
            "too bright": glow("x", params={"intensity": 1.5}),
            "a word for a number": glow("x", params={"intensity": "bright"}),
            "too wide for the ability": glow("x", radius=2.0),
            "too wide for the contract": glow("x", radius=12),
            "too long for the contract": glow("x", duration=700),
            "beyond its reach": glow("x", at=OUT_OF_REACH),
            "outside the room": glow("x", at=OUTSIDE_ROOM),
            "a spot neither avatar sees": glow("x", at=BEHIND_BOX),
            # Two problems at once: the kernel's order of checks decides.
            "unseen target and unknown ability": glow("x", capability=LACKING, targets=["obj:zz_not_here"]),
            "unknown ability and the player": glow("x", capability=LACKING, targets=["avatar:player"]),
            "the player and too bright": glow("x", targets=["avatar:player"], params={"intensity": 1.5}),
            "too bright and beyond reach": glow("x", at=OUT_OF_REACH, params={"intensity": 1.5}),
        }
        for name in ("owner", "actor", "room_id", "principal", "approval_id", "role"):
            cases[f"a reserved param {name}"] = glow("x", params={name: 1})
        outcome = {}
        for label, message in cases.items():
            message["action_id"] = self.next_id("no")
            result = await self.ask(message)
            outcome[label] = refusal(result)
            if label in ("too bright", "too wide for the ability", "beyond its reach"):
                outcome[label + ": bounds"] = (result["error"].get("allowed"), result["error"].get("actual"))
            if label in ("beyond its reach", "outside the room"):
                outcome[label + ": retryable"] = result["error"]["retryable"]
        started = [await self.ask(glow(self.next_id("glow"), at=at)) for at in (None, IN_THE_OPEN, [0.5, 0.0, 0.9])]
        hidden_when_full = await self.ask(glow(self.next_id("glow"), at=BEHIND_BOX))
        fourth = await self.ask(glow(self.next_id("glow")))
        outcome["three glows"] = [refusal(r) for r in started]
        outcome["hidden, with the cap reached"] = refusal(hidden_when_full)
        outcome["a fourth"] = (refusal(fourth), fourth["error"].get("allowed"), fourth["error"].get("actual")) \
            if not fourth["ok"] else "ok"
        outcome["each stops by id"] = [(await self.stop_one(r["created"][0]))[1] for r in started if r["ok"]]
        expected = {
            "the player": ("permission_denied", "$.args.targets"),
            "a thing in sight": ("permission_denied", "$.args.targets"),
            "itself and a thing": ("permission_denied", "$.args.targets"),
            "a thing that is not there": ("target_not_found", "$.args.targets"),
            "an ability the island lacks": ("unsupported_capability", "$.args.capability"),
            "a param it does not take": ("invalid_args", "$.args.params.speed"),
            "too bright": ("invalid_args", "$.args.params.intensity"),
            "too bright: bounds": ([0.2, 1.0], 1.5),
            "a word for a number": ("invalid_args", "$.args.params.intensity"),
            "too wide for the ability": ("invalid_args", "$.args.area.radius_m"),
            "too wide for the ability: bounds": (1.5, 2.0),
            "too wide for the contract": ("request_invalid", "$.args.area.radius_m"),
            "too long for the contract": ("request_invalid", "$.args.duration_s"),
            "beyond its reach": ("out_of_bounds", "$.args.area.center_m"),
            "beyond its reach: bounds": (2.0, outcome.get("beyond its reach: bounds", (None, None))[1]),
            "beyond its reach: retryable": True,
            "outside the room": ("out_of_bounds", "$.args.area.center_m"),
            "outside the room: retryable": False,
            "a spot neither avatar sees": ("target_not_found", "$.args.area.center_m"),
            "unseen target and unknown ability": ("target_not_found", "$.args.targets"),
            "unknown ability and the player": ("unsupported_capability", "$.args.capability"),
            "the player and too bright": ("permission_denied", "$.args.targets"),
            "too bright and beyond reach": ("invalid_args", "$.args.params.intensity"),
            "three glows": ["ok", "ok", "ok"],
            "hidden, with the cap reached": ("target_not_found", "$.args.area.center_m"),
            "a fourth": (("budget_exceeded", "$.args.capability"), 3, 3),
            "each stops by id": [1, 1, 1],
        }
        for name in ("owner", "actor", "room_id", "principal", "approval_id", "role"):
            expected[f"a reserved param {name}"] = ("request_invalid", "$.args.params")
        self.assertEqual(outcome, expected)
        self.assertGreater(outcome["beyond its reach: bounds"][1], 2.0)

    async def test_each_kind_ends_at_its_duration(self):
        """A glow, the Bubble and a firework each end at their own duration_s; a longer one still runs."""
        await self.companion_home()
        await self.stop_all_glows()
        brief = [await self.ask(cast(kind, self.next_id(kind), duration=1, at=IN_THE_OPEN if kind == "bubbles" else None))
                 for kind in ("glow", "bubbles", "fireworks")]
        longer = await self.ask(cast("glow", self.next_id("glow"), at=IN_THE_OPEN, duration=60))
        await self.wait(2.0)
        outcome = {"started": ([refusal(r) for r in brief], refusal(longer)),
                   "the brief ones have ended": [await self.stop_one(r["created"][0]) for r in brief if r["ok"]],
                   "the longer one still runs": await self.stop_one(longer["created"][0])}
        self.assertEqual(outcome, {"started": (["ok", "ok", "ok"], "ok"), "the brief ones have ended": [([], 0)] * 3,
                                   "the longer one still runs": (longer["created"], 1)})

    # ----- Bubbles and Fireworks (docs/runs/RUN-2-BUBBLES-FIREWORKS.md piece 5): the same rules, on both hosts -----

    async def test_the_companion_casts_bubbles_and_fireworks_on_itself_and_at_a_spot(self):
        """Fireworks on the Gubble (self, the pack's defaults filling the params left out) and at a spot in sight; the Bubble
        only at a spot (a bubble to walk into there), never on the Gubble; Fireworks reach further than Bubbles; a preview
        starts nothing; goal.stop ends every kind the companion cast."""
        await self.companion_home()
        await self.stop_all_glows()
        bubbles = await self.ask(cast("bubbles", self.next_id("bubbles"), params={}))
        stream = await self.ask(cast("bubbles", self.next_id("bubbles"), at=IN_THE_OPEN, params={"intensity": 1},
                                     radius=1.0, duration=60))
        burst = await self.ask(cast("fireworks", self.next_id("fireworks"), params={}, radius=1.0, duration=6))
        rocket = await self.ask(cast("fireworks", self.next_id("fireworks"), at=FAR_IN_SIGHT, params={"intensity": 0.3},
                                     radius=2.0, duration=6))
        too_far = await self.ask(cast("bubbles", self.next_id("bubbles"), at=FAR_IN_SIGHT))
        preview = await self.ask(cast("fireworks", self.next_id("fireworks"), at=IN_THE_OPEN, preview=True))

        def about(result):
            data = result.get("data", {})
            return (refusal(result), result.get("transient"), bool(EFFECT_ID.fullmatch(data.get("effect", ""))),
                    data.get("capability"), data.get("category"), data.get("target"), data.get("params"),
                    data.get("area", {}).get("radius_m"), data.get("duration_s"))

        started = [r["created"][0] for r in (stream, burst, rocket) if r["ok"]]
        stop = await self.ask(command("goal.stop", {}, self.next_id("stop")))
        outcome = {
            "bubbles on itself": refusal(bubbles),
            "bubbles at a spot": about(stream),
            "what the place made": stream.get("data", {}).get("kind"),
            "the stream's spot": stream.get("data", {}).get("area", {}).get("center_m"),
            "fireworks over itself": about(burst),
            "fireworks at a far spot": about(rocket),
            "the rocket's spot": rocket.get("data", {}).get("area", {}).get("center_m"),
            "bubbles at the far spot": (refusal(too_far), too_far.get("error", {}).get("allowed"),
                                        too_far.get("error", {}).get("retryable")),
            "preview": (refusal(preview), preview["preview"], "created" in preview, "effect" in preview.get("data", {}),
                        preview.get("data", {}).get("category")),
            "goal.stop ends every kind": stop["ok"] and set(started) <= set(stop["affected"]),
            "nothing left to stop": [await self.stop_one(e) for e in started],
        }
        self.assertEqual(outcome, {
            "bubbles on itself": ("invalid_args", "$.args.targets"),
            "bubbles at a spot": ("ok", True, True, "bubbles", "float", "point", {}, 0.08, 60),
            "what the place made": "walk_in",
            "the stream's spot": IN_THE_OPEN,
            "fireworks over itself": ("ok", True, True, "fireworks", "burst", "self", {"intensity": 0.8}, 1.0, 6),
            "fireworks at a far spot": ("ok", True, True, "fireworks", "burst", "point", {"intensity": 0.3}, 2.0, 6),
            "the rocket's spot": FAR_IN_SIGHT,
            "bubbles at the far spot": (("out_of_bounds", "$.args.area.center_m"), 2.0, True),
            "preview": ("ok", True, False, False, "burst"),
            "goal.stop ends every kind": True,
            "nothing left to stop": [([], 0)] * 3,
        })

    async def test_the_companion_cannot_cast_bubbles_or_fireworks_beyond_the_islands_rules(self):
        """Bubbles' and Fireworks' own bounds, reach and max_active, with the kernel's codes, fields and numbers; the
        player and other things are never targets; one ability's cap leaves the others free."""
        await self.companion_home()
        await self.stop_all_glows()
        cases = {
            "bubbles on the player": cast("bubbles", "x", targets=["avatar:player"]),
            "fireworks on the player": cast("fireworks", "x", targets=["avatar:player"]),
            "bubbles on a thing in sight": cast("bubbles", "x", targets=["obj:rug"]),
            "fireworks on itself and a thing": cast("fireworks", "x", targets=[GUBBLE, "obj:rug"]),
            "fireworks on a thing that is not there": cast("fireworks", "x", targets=["obj:zz_not_here"]),
            "a param bubbles do not take": cast("bubbles", "x", params={"speed": 1}, at=IN_THE_OPEN),
            "bubbles too thick": cast("bubbles", "x", params={"intensity": 1.5}, at=IN_THE_OPEN),
            "fireworks too faint": cast("fireworks", "x", params={"intensity": 0.25}),
            "bubbles too wide": cast("bubbles", "x", radius=1.2, at=IN_THE_OPEN),
            "fireworks too wide": cast("fireworks", "x", radius=2.5),
            "bubbles too long": cast("bubbles", "x", duration=90, at=IN_THE_OPEN),
            "bubbles on the Gubble itself": cast("bubbles", "x"),
            "fireworks too long": cast("fireworks", "x", duration=8),
            "fireworks outside the room": cast("fireworks", "x", at=OUTSIDE_ROOM),
            "fireworks at a spot neither avatar sees": cast("fireworks", "x", at=BEHIND_BOX),
            "the player and too thick": cast("bubbles", "x", targets=["avatar:player"], params={"intensity": 1.5}),
        }
        bounded = ("bubbles too thick", "fireworks too faint", "bubbles too wide", "fireworks too wide", "bubbles too long",
                   "fireworks too long")
        outcome = {}
        for label, message in cases.items():
            message["action_id"] = self.next_id("no")
            result = await self.ask(message)
            outcome[label] = refusal(result)
            if label in bounded:
                outcome[label + ": bounds"] = (result["error"].get("allowed"), result["error"].get("actual"))
        # The caps, each ability's own while the others run. Fireworks last at most 6 s: they start and stop well inside that.
        # The Bubble is one at a time: a second replaces the first, which pops (decision 59).
        streams = [await self.ask(cast("bubbles", self.next_id("bubbles"), at=at)) for at in (IN_THE_OPEN, [0.5, 0.0, 0.9])]
        rockets = [await self.ask(cast("fireworks", self.next_id("fireworks"), at=at, duration=6))
                   for at in (None, IN_THE_OPEN, FAR_IN_SIGHT)]
        fourth = await self.ask(cast("fireworks", self.next_id("fireworks"), duration=6))
        outcome["three fireworks"] = [refusal(r) for r in rockets]
        outcome["a fourth"] = (refusal(fourth), fourth.get("error", {}).get("allowed"), fourth.get("error", {}).get("actual"))
        outcome["the fireworks stop by id"] = [(await self.stop_one(r["created"][0]))[1] for r in rockets if r["ok"]]
        light = await self.ask(glow(self.next_id("glow")))
        outcome["two bubbles, one after the other"] = [refusal(r) for r in streams]
        outcome["a glow still fits"] = refusal(light)
        outcome["the rest stop by id"] = [(await self.stop_one(r["created"][0]))[1] for r in streams + [light] if r["ok"]]
        expected = {
            "bubbles on the player": ("permission_denied", "$.args.targets"),
            "fireworks on the player": ("permission_denied", "$.args.targets"),
            "bubbles on a thing in sight": ("permission_denied", "$.args.targets"),
            "fireworks on itself and a thing": ("permission_denied", "$.args.targets"),
            "fireworks on a thing that is not there": ("target_not_found", "$.args.targets"),
            "a param bubbles do not take": ("invalid_args", "$.args.params.speed"),
            "bubbles too thick": ("invalid_args", "$.args.params.intensity"),
            "bubbles too thick: bounds": ([0.2, 1.0], 1.5),
            "fireworks too faint": ("invalid_args", "$.args.params.intensity"),
            "fireworks too faint: bounds": ([0.3, 1.0], 0.25),
            "bubbles too wide": ("invalid_args", "$.args.area.radius_m"),
            "bubbles too wide: bounds": (1.0, 1.2),
            "fireworks too wide": ("invalid_args", "$.args.area.radius_m"),
            "fireworks too wide: bounds": (2.0, 2.5),
            "bubbles too long": ("invalid_args", "$.args.duration_s"),
            "bubbles too long: bounds": (60, 90),
            "fireworks too long": ("invalid_args", "$.args.duration_s"),
            "fireworks too long: bounds": (6, 8),
            "fireworks outside the room": ("out_of_bounds", "$.args.area.center_m"),
            "fireworks at a spot neither avatar sees": ("target_not_found", "$.args.area.center_m"),
            "the player and too thick": ("permission_denied", "$.args.targets"),
            "bubbles on the Gubble itself": ("invalid_args", "$.args.targets"),
            "three fireworks": ["ok", "ok", "ok"],
            "a fourth": (("budget_exceeded", "$.args.capability"), 3, 3),
            "the fireworks stop by id": [1, 1, 1],
            "two bubbles, one after the other": ["ok", "ok"],
            "a glow still fits": "ok",
            "the rest stop by id": [0, 1, 1],
        }
        self.assertEqual(outcome, expected)


class AlignmentOnTheMock(AlignmentScenarios, unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.host = new_host(policy=HostPolicy(companion_messages_per_s=1_000_000), clock=FakeClock())

    def tearDown(self):
        for result in self.host.emitted:
            self.assertEqual(contract_problems(result), [], result)

    async def ask(self, message: dict) -> dict:
        return self.host.handle(COMPANION, message)

    async def companion_to(self, position: list[float]) -> None:
        self.host.move_avatar("avatar:companion", position)

    async def finish(self, job_id: str) -> str:
        self.host.goal_arrived("avatar:companion")
        return (await self.ask(query("jobs.status", {"job_id": job_id}, self.next_id("q"))))["data"]["state"]

    async def companion_home(self) -> None:
        pass  # a fresh mock host per test: the companion is at its spawn

    async def wait(self, seconds: float) -> None:
        self.host.clock.advance(seconds)

    async def test_fire_waits_for_the_first_rest_then_is_previewed(self):
        """Decision 72 on the mock: Fire is refused in the kernel's words until the team learns it (the player's first rest by
        the starting campfire, the game side's player_rests), then a Fire preview says what the place makes of it; Fire stays
        listed meanwhile, locked."""
        fire = {"capability": "fire", "center_m": IN_THE_OPEN, "radius_m": 0.25, "duration_s": 30}
        locked = await self.ask(command("nature.cast", fire, self.next_id("fire"), preview=True))
        listed = await self.ask(query("capabilities.list", {}, self.next_id("q")))
        rest = self.host.player_rests()
        learned = await self.ask(command("nature.cast", fire, self.next_id("fire"), preview=True))
        relisted = await self.ask(query("capabilities.list", {"category": "fire"}, self.next_id("q")))
        self.assertEqual([item.get("locked") for item in listed["data"]["items"] if item["capability"] == "fire"], [True])
        self.assertNotIn("locked", relisted["data"]["items"][0])
        self.assertEqual((refusal(locked), locked["error"]["message"]), (("permission_denied", "$.args.capability"),
                         "The Gubble has not learned Fire yet: rest by the starting campfire first."))
        self.assertIn("fire", [item["capability"] for item in listed["data"]["items"]])
        self.assertEqual((rest["ok"], rest["campfire"], rest["learned_fire"]), (True, "camp:start", True))
        self.assertEqual((refusal(learned), learned["data"]["kind"], len(learned["data"]["preview_id"])), ("ok", "fire", 32))


if __name__ == "__main__":
    unittest.main()
