"""Nature on the mock host (mock_nature.py): Grow, Fire and its kinds, the campfires, repair, undo and the price by principal, as the
kernel's CommandHostNature.cs, CommandHostGrow.cs, CommandHostFireKinds.cs and CommandHostCamps.cs answer them.

test_host_alignment.py runs the scenarios both hosts can run in the test room (no plants there, and a fresh team has not learned
Fire); these are the rest, on the mock alone, with the kernel's words (test_kernel_alignment.py holds them to the kernel's source).
The security rules are the kernel's: the price follows the principal and never a field; no Fire before the first rest, whoever
asks; the Gubble's Fire behind the player's "Let the Gubble use fire", one fire of its own at a time and 20 s after its last; Snuff
lighting nothing; Creative's one ask before a fire burns a build; every player-only op still refused to the companion. Every result
is contract-valid.
"""
from __future__ import annotations

import json
import shutil
import tempfile
import unittest
from pathlib import Path

from support import (BEHIND_BOX, COMPANION, GUBBLE, IN_THE_OPEN, PLAYER, REPO, FakeClock, HostPolicy, command, contract_problems,
                     contracts, new_host, query)

from garage_game_companion import mock_nature as m
from garage_game_companion.mock_energy import ENERGY_EXHAUSTED_MESSAGE

OPEN_POLICY = HostPolicy(companion_approval_ops=frozenset(), companion_messages_per_s=1_000_000)
SPAWN = [0.0, 0.0, 0.6]  # the player's spawn in the test room; the starting campfire burns 0.2 m in front of it
CAMPFIRE = [0.0, 0.0, 0.4]
ON_THE_FLOOR = [1.0, 0.0, 1.0]  # bare wooden floor, beside the rug, in sight and within the Gubble's reach
FAR_FLOOR = [1.5, 0.0, 1.2]  # bare floor too, and more than a Snuff's 1.5 m (and a ring) from the starting campfire
RAINING_AT_S = 160.0  # the weather clock in full rain (the kernel's six-minute sequence: dry for 120 s, then rain)


def block(name: str, material: str = "wood") -> dict:
    return {"schema": "garage_game.creation", "version": 1, "name": name, "seed": 7, "mount": "ground",
            "parts": [{"id": "block", "shape": "box", "position_m": [0, 0.05, 0], "rotation_deg": [0, 0, 0],
                       "size_m": [0.1, 0.1, 0.1], "material": material}], "nodes": [], "edges": []}


def refusal(result: dict):
    return "ok" if result["ok"] else (result["error"]["code"], result["error"].get("field_path"), result["error"]["message"])


class NatureCase(unittest.TestCase):
    policy = OPEN_POLICY

    def setUp(self):
        self.clock = FakeClock()
        self.host = new_host(policy=self.policy, clock=self.clock)
        self.n = 0

    def tearDown(self):
        for result in self.host.emitted:
            self.assertEqual(contract_problems(result), [], result)

    def send(self, principal, op, args, **extra):
        self.n += 1
        return self.host.handle(principal, command(op, args, f"n-{self.n}", **extra))

    def ask(self, principal, op, args=None):
        self.n += 1
        return self.host.handle(principal, query(op, args or {}, f"q-{self.n}"))

    def later(self, seconds: float) -> None:
        """Time passes on the host's clock, and a request lets the host's own frames run (nature's steps, the pool's refill)."""
        self.clock.advance(seconds)
        self.ask(PLAYER, "player.status")

    def until(self, weather_s: float) -> None:
        self.later(max(0.0, weather_s - self.host._weather_s()))

    @staticmethod
    def fire(at, radius=0.25, duration=30, **extra) -> dict:
        return {"capability": "fire", "center_m": list(at), "radius_m": radius, "duration_s": duration, **extra}

    @staticmethod
    def grow(at, radius=0.3) -> dict:
        return {"capability": "grow", "center_m": list(at), "radius_m": radius, "duration_s": 1}

    def light(self, principal, args) -> dict:
        """A Fire kind previewed, then cast as exactly that preview (the Gubble's two commands, the player's click)."""
        preview = self.send(principal, "nature.cast", args, preview=True)
        self.assertTrue(preview["ok"], preview)
        return self.send(principal, "nature.cast", {**args, "preview_id": preview["data"]["preview_id"]})

    def pips(self) -> int:
        return self.ask(PLAYER, "player.status")["data"]["energy"]["pips"]

    def build(self, at, name="Tower", principal=PLAYER, material="wood") -> str:
        placed = self.send(principal, "creation.place", {"source": block(name, material), "placement": {"position_m": list(at)}})
        self.assertTrue(placed["ok"], placed)
        return placed["created"][0]

    def lessons(self) -> list[str]:
        return [e["line"] for e in self.ask(COMPANION, "journal.read", {"kind": "consequence", "limit": 50})["data"]["entries"]]


class FireAndTheCamp(NatureCase):
    def test_fire_is_locked_until_the_first_rest_by_the_starting_campfire(self):
        """Decision 72: no Fire, whoever asks, until the player rests by the starting campfire (0.2 m in front of the spawn, lit from
        the start); the rest key is the game's, sitting the player down within 0.3 m of a lit campfire and out of its flames."""
        self.assertEqual([(c.id, c.position, c.starting) for c in self.host.camp_sites], [(m.STARTING_ID, CAMPFIRE, True)])
        for principal in (PLAYER, COMPANION):
            with self.subTest(principal=principal):
                self.assertEqual(refusal(self.send(principal, "nature.cast", self.fire(IN_THE_OPEN), preview=True)),
                                 ("permission_denied", "$.args.capability", m.FIRE_NOT_LEARNED))
        self.host.move_avatar("avatar:player", [1.0, 0.0, 0.6])
        self.assertEqual((self.host.player_rests()["ok"], self.host.player_rests()["words"]), (False, m.REST_NEAR_FIRE))
        self.host.move_avatar("avatar:player", [0.0, 0.0, 0.42])
        self.assertEqual(self.host.player_rests()["words"], m.REST_IN_FLAMES)
        self.host.move_avatar("avatar:player", SPAWN)
        first = self.host.player_rests()
        self.assertEqual(first, {"ok": True, "words": m.REST_SITS, "campfire": m.STARTING_ID, "learned_fire": True, "new_camp": True})
        self.assertEqual(self.host.player_rests()["learned_fire"], False, "once per team")
        preview = self.send(COMPANION, "nature.cast", self.fire(IN_THE_OPEN), preview=True)
        self.assertEqual((refusal(preview), preview["data"]["kind"]), ("ok", m.KIND_FIRE))
        self.host.load_room(self.host.room_dir)
        self.assertTrue(self.host.fire_learned, "the team's expertise outlives a room")
        listed = self.ask(COMPANION, "capabilities.list", {"category": "fire"})["data"]["items"][0]
        self.assertNotIn("locked", listed)

    def test_a_fire_is_previewed_then_cast_as_exactly_that_preview(self):
        self.host.learn_fire()
        args = self.fire(IN_THE_OPEN)
        preview = self.send(COMPANION, "nature.cast", args, preview=True)
        data = preview["data"]
        self.assertTrue(preview["ok"] and preview["preview"], preview)
        self.assertEqual((data["kind"], len(data["preview_id"]), data["affected_ids"], preview["affected"], data["avatars_in_area"],
                          data["max_burning_cells"], data["requires_approval"]), (m.KIND_FIRE, 32, ["obj:rug"], ["obj:rug"], [], 200, False))
        self.assertTrue(0 < data["cell_count"] == len(data["cells"]) <= 200, data["cell_count"])
        token = data["preview_id"]
        again = ("revision_conflict", "$.args.preview_id", m.FIRE_PREVIEW_AGAIN)
        self.assertEqual(refusal(self.send(COMPANION, "nature.cast", args)), again, "Fire is previewed first")
        self.assertEqual(refusal(self.send(COMPANION, "nature.cast", {**args, "radius_m": 0.3, "preview_id": token})), again, "not that cast")
        self.assertEqual(refusal(self.send(PLAYER, "nature.cast", {**args, "preview_id": token})), again, "another's preview")
        revision = self.host.revision
        cast = self.send(COMPANION, "nature.cast", {**args, "preview_id": token})
        self.assertEqual((refusal(cast), cast["transient"], cast["revision"], cast["affected"]), ("ok", False, revision + 1, ["obj:rug"]))
        self.assertEqual(sorted(cast["data"]), ["cell_count", "center_m", "duration_s", "max_burning_cells", "radius_m", "requires_approval",
                                                "started_cells"])
        self.assertGreater(cast["data"]["started_cells"], 0)
        replay = self.host.handle(COMPANION, command("nature.cast", {**args, "preview_id": token}, f"n-{self.n}"))
        self.assertEqual((replay["replayed"], replay["revision"], self.host.revision), (True, revision + 1, revision + 1))
        described = self.ask(COMPANION, "nature.describe")["data"]
        self.assertEqual((described["nature_revision"], described["can_undo"]), (revision + 1, True))
        self.assertGreater(described["burning_cells"], 0)
        self.assertEqual(refusal(self.send(COMPANION, "nature.cast", {**args, "preview_id": token}))[0], "target_busy", "one fire at a time")

    def test_what_fire_reaches_burns_by_the_fire_rule(self):
        """The island's stock and plants burn down (gone, refused like an unknown id), locked things never burn and shelter their
        ground, a plant leaves ash that Grow greens, and the fire ends at its time."""
        self.host.learn_fire()
        self.host.add_plant("plant:grass-0", [0.75, 0.0, 1.0], 0.05)
        self.assertEqual(refusal(self.light(COMPANION, self.fire(IN_THE_OPEN, duration=10))), "ok")
        self.later(6)
        self.assertEqual(refusal(self.ask(COMPANION, "entity.inspect", {"target": "obj:rug"}))[0], "target_not_found", "burned down")
        self.assertNotIn("obj:rug", [e["id"] for e in self.ask(PLAYER, "entities.list", {"limit": 100})["data"]["items"]])
        self.assertEqual(self.host.plant_factor("plant:grass-0"), 0.0)
        self.later(5)
        self.assertIsNone(self.host.fire, "the fire ended at its time")
        greened = self.send(PLAYER, "nature.cast", self.grow([0.75, 0.0, 1.0], 0.1))
        self.assertEqual((refusal(greened), greened["data"]["grown_plants"]), ("ok", 0))
        self.assertIn("green_ash", self.host.nature_expertise["grow"])
        self.assertEqual(self.host.plant_factor("plant:grass-0"), 0.0, "Green ash greens the ground; a burnt plant stays gone")
        # A locked thing is fireproof, and so is the ground on it: a fire all over the locked book finds nothing to burn.
        self.assertTrue(self.send(PLAYER, "protect.lock", {"targets": ["obj:book"]}, expected_entities={"obj:book": 0})["ok"])
        on_the_book = self.send(PLAYER, "nature.cast", self.fire([0.45, 0.0, 0.1], radius=0.04), preview=True)
        self.assertEqual(refusal(on_the_book), ("invalid_args", "$.args.center_m", m.NO_DRY_FUEL))
        # Stone, cardboard and rubber do not burn: the box is no target of a fire beside it.
        beside_the_box = self.send(PLAYER, "nature.cast", self.fire([1.1, 0.0, 0.5], radius=0.3), preview=True)
        self.assertEqual((refusal(beside_the_box), beside_the_box["data"]["affected_ids"]), ("ok", []))


class Builds(NatureCase):
    def test_creative_asks_once_before_a_fire_burns_a_build(self):
        """The fire rule in Creative: before any fire burns an unlocked build, the player is asked once; yes lets it scorch (repaired
        free), no lights the fire with the build spared. The player's own fire asks too."""
        self.host.learn_fire()
        tower = self.build([0.6, 0.0, 1.2])
        args = self.fire(IN_THE_OPEN, duration=10)
        preview = self.send(COMPANION, "nature.cast", args, preview=True)
        self.assertEqual((preview["data"]["requires_approval"], sorted(preview["data"]["affected_ids"])), (True, sorted([tower, "obj:rug"])))
        held = self.send(COMPANION, "nature.cast", {**args, "preview_id": preview["data"]["preview_id"]})
        self.assertEqual((held["error"]["code"], held["approval_needed"]["reason"]),
                         ("approval_required", m.FIRE_HOLD_BUILDS.format(whose="The Gubble's fire")))
        self.assertEqual(self.host.revision, 1, "nothing lit yet")
        approved = self.host.player_decide(held["approval_needed"]["request_id"], True)
        self.assertEqual((approved["state"], approved["result"]["ok"], approved["result"]["approved_by"]), ("approved", True, PLAYER))
        self.later(4)
        self.assertEqual(self.ask(COMPANION, "nature.describe")["data"]["scorched"], [tower])
        # The player's no: the fire is lit, and the build is spared.
        self.later(10)
        spared = self.build([1.0, 0.0, 1.2], "Hut")
        mine = self.light(PLAYER, self.fire(ON_THE_FLOOR, duration=10))
        self.assertEqual((mine["error"]["code"], mine["approval_needed"]["reason"]),
                         ("approval_required", m.FIRE_HOLD_BUILDS.format(whose="Your fire")))
        denied = self.host.player_decide(mine["approval_needed"]["request_id"], False)
        self.assertEqual((denied["state"], denied["result"]["ok"], "approved_by" in denied["result"]), ("denied", True, False))
        self.later(5)
        self.assertNotIn(spared, self.ask(PLAYER, "nature.describe")["data"]["scorched"])
        self.assertIsNotNone(self.host.fire, "lit, sparing the build")

    def test_a_scorched_build_can_be_repaired_or_removed_and_nothing_else(self):
        self.host.learn_fire()
        tower = self.build([0.6, 0.0, 1.2])
        held = self.light(COMPANION, self.fire(IN_THE_OPEN, duration=3))
        self.host.player_decide(held["approval_needed"]["request_id"], True)
        self.later(1)
        busy = ("target_busy", None, m.GUBBLE_FIRE_BURNING)
        self.assertEqual(refusal(self.send(PLAYER, "nature.repair", {"target": tower}))[:3:2], busy[::2], "not while a fire burns")
        self.later(5)
        scorched = ("target_busy", "$.args.target", m.REMAINS_SCORCHED)
        self.assertEqual(refusal(self.send(PLAYER, "creation.revise", {"target": tower, "source": block("Taller")},
                                           expected_entities={tower: self.host.entities[tower].revision})), scorched)
        self.assertEqual(refusal(self.send(PLAYER, "creation.activate", {"target": tower})), scorched)
        self.assertEqual(refusal(self.send(PLAYER, "protect.lock", {"targets": [tower]}, expected_revision=self.host.revision))[:2],
                         ("target_busy", "$.args.targets"))
        self.assertEqual(refusal(self.send(COMPANION, "nature.repair", {"target": tower})),
                         ("approval_required", None, "Waiting for the player to approve this change in the game."))
        self.assertEqual(self.host.pending_approvals()[-1]["reason"], m.REPAIR_HOLD.format(target=tower))
        preview = self.send(PLAYER, "nature.repair", {"target": tower}, preview=True)
        self.assertEqual((preview["data"], preview["affected"]), ({"cost": {}}, [tower]), "free in Creative")
        repaired = self.send(PLAYER, "nature.repair", {"target": tower})
        self.assertEqual((refusal(repaired), self.ask(PLAYER, "nature.describe")["data"]["scorched"]), ("ok", []))
        self.assertEqual(refusal(self.send(PLAYER, "nature.repair", {"target": tower})), ("invalid_args", "$.args.target", m.REPAIR_NOT_NEEDED))
        self.assertEqual(refusal(self.send(PLAYER, "nature.repair", {"target": "creation:gone"})),
                         ("target_not_found", "$.args.target", m.REPAIR_NOT_HERE))

    def test_outside_creative_a_build_burns_with_no_ask_and_the_gubbles_fire_is_a_lesson(self):
        """Challenge: nothing asks; a build the Gubble's fire scorches is a consequence fact in the kernel's words that costs Trust
        (its fire was paid); repair costs the build's materials from the team's wallet (storybook_wild v5 starts it empty)."""
        self.host.learn_fire()
        self.assertTrue(self.send(PLAYER, "world.set_mode", {"mode": "challenge"})["ok"])
        tower = self.build([0.6, 0.0, 1.2], "Tower")
        cast = self.light(COMPANION, self.fire(IN_THE_OPEN, duration=3))
        self.assertEqual((refusal(cast), cast["data"]["requires_approval"]), ("ok", False))
        self.later(5)
        self.assertEqual(self.ask(PLAYER, "nature.describe")["data"]["scorched"], [tower])
        self.assertIn(m.BUILD_BURNT_START + 'your "Tower".', self.lessons())
        self.assertEqual(self.host.trust_count, -3)
        self.assertEqual(refusal(self.send(PLAYER, "nature.repair", {"target": tower})), ("invalid_args", "$.args.target", m.REPAIR_NO_MATERIALS))
        self.host.wallet["wood"] = 1
        self.assertEqual(self.send(PLAYER, "nature.repair", {"target": tower}, preview=True)["data"], {"cost": {"wood": 1}})
        self.assertTrue(self.send(PLAYER, "nature.repair", {"target": tower})["ok"])
        self.assertEqual(self.ask(PLAYER, "nature.describe")["data"]["wallet"]["wood"], 0)


class TheGubblesFire(NatureCase):
    def test_one_fire_at_a_time_and_the_gubbles_guardrails(self):
        """One fire on the island at a time, in words for whoever asks; the Gubble waits 20 s after its last fire; the player's
        "Let the Gubble use fire" off refuses the Gubble's Fire, never the player's; no request reaches the setting."""
        self.host.learn_fire()
        self.assertTrue(self.light(COMPANION, self.fire(ON_THE_FLOOR, duration=5))["ok"])
        self.assertEqual(refusal(self.send(COMPANION, "nature.cast", self.fire(IN_THE_OPEN), preview=True)),
                         ("target_busy", "$.args.capability", m.FIRE_BUSY_OWN))
        self.assertEqual(refusal(self.send(PLAYER, "nature.cast", self.fire(IN_THE_OPEN), preview=True)),
                         ("target_busy", None, m.GUBBLE_FIRE_BURNING))
        self.later(6)
        waited = self.send(COMPANION, "nature.cast", self.fire(IN_THE_OPEN), preview=True)
        self.assertEqual(refusal(waited)[:2], ("rate_limited", "$.args.capability"))
        self.assertEqual(waited["error"]["message"], m.FIRE_WAIT.format(seconds=round(waited["error"]["actual"] + 0.49)))
        self.assertTrue(14 <= waited["error"]["actual"] <= 20, waited["error"])
        self.assertEqual(refusal(self.send(PLAYER, "nature.cast", self.fire(IN_THE_OPEN), preview=True)), "ok", "the player never waits")
        self.later(20)
        self.assertEqual(refusal(self.send(COMPANION, "nature.cast", self.fire(IN_THE_OPEN), preview=True)), "ok")
        self.host.set_gubble_fire(False)
        self.assertEqual(refusal(self.send(COMPANION, "nature.cast", self.fire(IN_THE_OPEN), preview=True)),
                         ("permission_denied", "$.args.capability", m.FIRE_OFF))
        self.assertEqual(refusal(self.send(PLAYER, "nature.cast", self.fire(IN_THE_OPEN), preview=True)), "ok")
        smuggled = self.send(COMPANION, "nature.cast", self.fire(IN_THE_OPEN, gubble_fire=True), preview=True)
        self.assertEqual(refusal(smuggled)[:2], ("field_unknown", "$.args.gubble_fire"))
        self.assertFalse(self.host.gubble_fire)

    def test_snuff_puts_out_what_burns_and_lights_nothing(self):
        """Rain on a fire the Gubble lit teaches Snuff (decision 79); then Fire aimed at something burning is Snuff: everything
        burning in 1.5 m goes out, the ground there cannot catch for 30 s, no room revision (a transient receipt), and none of the
        Gubble's fire guardrails stop it, since it lights nothing."""
        self.host.learn_fire()
        self.until(RAINING_AT_S)
        self.assertTrue(self.light(COMPANION, self.fire(FAR_FLOOR, duration=60))["ok"])
        self.later(1)
        self.assertIn("snuff", self.host.nature_expertise["fire"])
        self.host.set_gubble_fire(False)
        revision = self.host.revision
        preview = self.send(COMPANION, "nature.cast", self.fire(FAR_FLOOR), preview=True)
        self.assertEqual((refusal(preview), preview["data"]["kind"], preview["data"]["radius_m"], preview["data"]["duration_s"]),
                         ("ok", m.KIND_SNUFF, m.SNUFF_RADIUS_M, m.SNUFF_DAMP_S))
        self.assertFalse({"affected_ids", "max_burning_cells", "avatars_in_area"} & set(preview["data"]))
        snuffed = self.send(COMPANION, "nature.cast", {**self.fire(FAR_FLOOR), "preview_id": preview["data"]["preview_id"]})
        self.assertEqual((refusal(snuffed), snuffed["transient"], snuffed["data"]["kind"], self.host.revision), ("ok", True, m.KIND_SNUFF, revision))
        self.assertIsNone(self.host.fire)
        self.assertEqual(self.ask(COMPANION, "nature.describe")["data"]["burning_cells"], 0)
        self.host.set_gubble_fire(True)
        self.assertEqual(refusal(self.send(PLAYER, "nature.cast", self.fire(FAR_FLOOR), preview=True)),
                         ("invalid_args", "$.args.center_m", m.NO_DRY_FUEL), "damp for 30 s")
        self.later(31)
        self.assertEqual(refusal(self.send(PLAYER, "nature.cast", self.fire(FAR_FLOOR), preview=True)), "ok")

    def test_a_bubble_cast_on_a_fire_teaches_snuff(self):
        self.host.learn_fire()
        self.assertTrue(self.light(PLAYER, self.fire(ON_THE_FLOOR, duration=30))["ok"])
        self.assertTrue(self.send(PLAYER, "nature.cast", {"capability": "bubble_carry", "center_m": [ON_THE_FLOOR[0], 0.08, ON_THE_FLOOR[2]],
                                                          "radius_m": 0.08, "duration_s": 8})["ok"])
        self.assertIn("snuff", self.host.nature_expertise["fire"])

    def test_rain_puts_a_campfire_out_and_the_gubbles_fire_relights_it(self):
        """Decision 73: 30 s of rain puts a lit campfire out (the starting one only once the team has learned Fire, for nothing could
        relight it before); a campfire that is out is no place to rest; a Fire cast that reaches it relights it, and nothing else,
        with one cell and no undo step; the Gubble's Snuff of a lit campfire waits for the player's yes."""
        self.until(250)
        self.assertTrue(self.host.campfire_lit(m.STARTING_ID), "rain spares it before Fire is learned")
        self.assertTrue(self.host.player_rests()["learned_fire"])
        self.until(m.WEATHER_CYCLE_S + 250)
        self.assertFalse(self.host.campfire_lit(m.STARTING_ID))
        self.assertEqual(self.host.player_rests()["words"], m.REST_OUT)
        on_it = self.fire(CAMPFIRE)
        preview = self.send(COMPANION, "nature.cast", on_it, preview=True)
        self.assertEqual((preview["data"]["kind"], preview["data"]["cell_count"], preview["data"]["cells"], preview["data"]["affected_ids"]),
                         (m.KIND_FIREPIT, 1, [CAMPFIRE], []))
        revision = self.host.revision
        lit = self.send(COMPANION, "nature.cast", {**on_it, "preview_id": preview["data"]["preview_id"]})
        self.assertEqual((refusal(lit), lit["data"]["started_cells"], lit["revision"], lit["transient"]), ("ok", 1, revision + 1, False))
        self.assertTrue(self.host.campfire_lit(m.STARTING_ID))
        self.assertTrue(self.host.player_rests()["ok"])
        self.assertEqual(refusal(self.send(PLAYER, "room.undo", {"to_revision": revision}, expected_revision=revision + 1))[2],
                         m.UNDO_ONLY_NATURE, "lighting a campfire is no undo step")
        # The Gubble's Snuff of the lit campfire is held for the player's yes; approved, the campfire is out.
        self.host.nature_expertise["fire"].add("snuff")
        self.later(21)
        held = self.light(COMPANION, self.fire(CAMPFIRE))
        self.assertEqual((held["error"]["code"], held["approval_needed"]["reason"]), ("approval_required", m.SNUFF_HOLD_CAMPFIRE))
        decided = self.host.player_decide(held["approval_needed"]["request_id"], True)
        self.assertEqual((decided["state"], decided["result"]["transient"]), ("approved", True))
        self.assertFalse(self.host.campfire_lit(m.STARTING_ID))

    def test_the_manifests_firepits_are_lit_by_fire_and_become_camps(self):
        """x_firepits (decision 74): each one standing on the floor, 0.6 m from every other campfire, is firepit:<its number>; one that
        does not check is left out. A Fire cast lights it; the first one lit teaches Campfire; resting there makes it the camp."""
        with tempfile.TemporaryDirectory() as temporary:
            room = Path(temporary) / "room"
            shutil.copytree(REPO / "game" / "rooms" / "test_room", room)
            manifest = json.loads((room / "room.json").read_bytes())
            manifest["extensions"] = {"x_firepits": {"items": [{"position_m": [1.0, 0.0, -0.5]}, {"position_m": [0.05, 0.0, 0.45]},
                                                               {"position_m": [0.5, 1.0, 0.5]}]}}
            (room / "room.json").write_bytes((json.dumps(manifest, indent=2) + "\n").encode("utf-8"))
            host = self.host = new_host(policy=self.policy, clock=self.clock, room_dir=room)
            self.assertEqual([(c.id, c.position) for c in host.camp_sites], [(m.STARTING_ID, CAMPFIRE), ("firepit:1", [1.0, 0.0, -0.5])])
            host.learn_fire()
            host.move_avatar("avatar:player", [1.2, 0.0, -0.5])
            self.assertEqual(host.player_rests()["words"], m.REST_UNLIT)
            lit = self.light(COMPANION, self.fire([1.0, 0.0, -0.5]))
            self.assertEqual((refusal(lit), host.campfire_lit("firepit:1")), ("ok", True))
            self.assertIn("campfire", host.nature_expertise["fire"])
            self.assertEqual(host.player_rests(), {"ok": True, "words": m.REST_NEW_CAMP, "campfire": "firepit:1", "learned_fire": False,
                                                   "new_camp": True})


class Grow(NatureCase):
    def test_grow_grows_the_islands_plants_up_to_their_cap(self):
        """Every plant rooted in the circle that is not at its cap grows the pack's share of its natural size (a quarter in
        storybook_wild v5), up to twice its size or 0.4 m taller; a stone never grows; twice the share while it rains; a preview
        changes nothing; each expertise is learned once."""
        host = self.host
        host.add_plant("plant:daisies-0", [0.6, 0.0, 1.0], 0.1)
        host.add_plant("plant:oak-0", [0.7, 0.0, 1.1], 1.0, "broadleaf", "tree")
        host.add_plant("plant:rock-0", [0.5, 0.0, 1.0], 0.2, "none", "stone")
        here = self.grow(IN_THE_OPEN)
        preview = self.send(COMPANION, "nature.cast", here, preview=True)
        self.assertEqual((preview["data"]["grown_plants"], host.plant_factor("plant:daisies-0")), (2, 1.0))
        first = self.send(COMPANION, "nature.cast", here)
        self.assertEqual((refusal(first), first["data"]["grown_plants"], first["transient"]), ("ok", 2, False))
        self.assertEqual([host.plant_factor(p) for p in ("plant:daisies-0", "plant:oak-0", "plant:rock-0")], [1.25, 1.25, 1.0])
        self.send(COMPANION, "nature.cast", here)
        self.assertEqual(host.plant_factor("plant:oak-0"), 1.4, "a tree grows at most 0.4 m taller")
        self.assertIn("tall_plant", host.nature_expertise["grow"])
        self.until(RAINING_AT_S)
        rained = self.send(COMPANION, "nature.cast", self.grow([0.6, 0.0, 1.0], 0.05))
        self.assertEqual((refusal(rained), host.plant_factor("plant:daisies-0")), ("ok", 2.0))
        self.assertIn("rain_garden", host.nature_expertise["grow"])
        self.assertEqual(refusal(self.send(COMPANION, "nature.cast", here)), ("invalid_args", "$.args.center_m", m.GROW_FULL))
        self.assertEqual(refusal(self.send(COMPANION, "nature.cast", self.grow(ON_THE_FLOOR, 0.1))),
                         ("invalid_args", "$.args.center_m", m.GROW_NO_PLANT))

    def test_three_full_plants_together_are_a_thicket(self):
        for i, x in enumerate((0.5, 0.7, 0.9)):
            self.host.add_plant(f"plant:fern-{i}", [x, 0.0, 1.0], 1.0, "herb")
        here = self.grow([0.7, 0.0, 1.0], 0.3)
        for _ in range(2):
            self.assertTrue(self.send(PLAYER, "nature.cast", here)["ok"])
        self.assertIn("thicket", self.host.nature_expertise["grow"])

    def test_grow_on_the_player_or_by_effect_start_is_refused(self):
        self.host.add_plant("plant:daisies-0", [0.6, 0.0, 1.0], 0.1)
        on_the_player = self.send(COMPANION, "nature.cast", {**self.grow(IN_THE_OPEN), "target": "avatar:player"}, preview=True)
        self.assertEqual(refusal(on_the_player)[:2], ("request_invalid", "$.args.capability"))
        effect = self.send(COMPANION, "effect.start", {"capability": "grow", "params": {}, "area": {"center_m": IN_THE_OPEN, "radius_m": 0.3},
                                                      "duration_s": 1})
        self.assertEqual(refusal(effect)[:2], ("unsupported_capability", "$.args.capability"))


class GrowUnlocks(NatureCase):
    """Grow's three unlocks (decisions 76 and 104 to 107) on the mock, in the kernel's words: each locked before the team's Grow
    expertise count opens it; replanting and sprouts growing through their phases on the host's clock; the bridge's checks over the
    box world's floor. The box world has no faces, so never ivy (the kernel's NatureTest.GrowUnlocks climbs it)."""

    def grass_floor(self) -> None:
        """The box world's floor made grass: fertile ground, as an island's soil is (the test room's floor is wood)."""
        self.host.entities["shell:floor"].materials = ["grass"]

    def test_each_unlock_is_refused_before_its_count_whoever_asks(self):
        host = self.host
        host.add_plant("plant:daisies-0", [1.4, 0.0, 1.2], 0.1, "herb")
        far = [5.0, 0.0, 1.0]
        for principal in (PLAYER, COMPANION):
            with self.subTest(principal=principal):
                self.assertEqual(refusal(self.send(principal, "nature.cast", {**self.grow(ON_THE_FLOOR), "end_m": far})),
                                 ("permission_denied", "$.args.end_m", m.BRIDGE_LOCKED), "a drag before three, before every other check")
                self.assertEqual(refusal(self.send(principal, "nature.cast", self.grow(ON_THE_FLOOR, 0.1))),
                                 ("invalid_args", "$.args.center_m", m.GROW_NO_PLANT), "the wooden floor is not fertile: nothing grows there")
        host.plant_state["plant:daisies-0"] = m.PlantState(burn=1.0)
        self.assertEqual(refusal(self.send(PLAYER, "nature.cast", self.grow([1.4, 0.0, 1.2], 0.05))),
                         ("permission_denied", "$.args.center_m", m.REPLANT_LOCKED))
        self.grass_floor()
        revision = host.revision
        self.assertEqual(refusal(self.send(COMPANION, "nature.cast", self.grow(ON_THE_FLOOR, 0.1))),
                         ("permission_denied", "$.args.center_m", m.SPROUTS_LOCKED))
        host.learn_grow(1)
        self.assertEqual(refusal(self.send(PLAYER, "nature.cast", self.grow(ON_THE_FLOOR, 0.1)))[0], "permission_denied", "one is not two")
        self.assertEqual((host.revision, host.sprouts), (revision, []), "refused before the price: nothing changed")

    def test_a_burnt_plant_is_replanted_as_a_seedling_growing_in_phases(self):
        host = self.host
        host.add_plant("plant:oak-0", [1.4, 0.0, 1.2], 1.0, "broadleaf", "tree")
        host.add_plant("plant:oak-1", [1.5, 0.0, 1.2], 1.0, "broadleaf", "tree")
        for plant in ("plant:oak-0", "plant:oak-1"):
            host.plant_state[plant] = m.PlantState(burn=1.0)
        host.learn_grow(2)
        host.move_avatar("avatar:player", [1.4, 0.0, 1.2])
        here = self.grow([1.38, 0.0, 1.2], 0.2)
        self.assertEqual(refusal(self.send(PLAYER, "nature.cast", here)), ("target_busy", "$.args.center_m", m.REPLANT_STANDING))
        host.move_avatar("avatar:player", SPAWN)
        self.assertEqual(self.send(COMPANION, "nature.cast", here, preview=True)["data"]["kind"], m.KIND_REPLANT)
        replanted = self.send(COMPANION, "nature.cast", here)
        self.assertEqual((refusal(replanted), replanted["data"].get("kind"), replanted["data"]["grown_plants"]), ("ok", None, 1),
                         "its durable receipt keeps the plain Grow shape, as the kernel's")
        self.assertEqual((host.plant_factor("plant:oak-0"), host.plant_factor("plant:oak-1")), (0.5, 0.0), "one plant: the nearest burnt one")
        phases = [host.plant_factor("plant:oak-0")]
        for _hour in range(3):
            self.later(3600)
            phases.append(round(host.plant_factor("plant:oak-0"), 4))
        self.assertEqual(phases, [0.5, 0.6667, 0.8333, 1.0])
        self.assertEqual(refusal(self.send(COMPANION, "nature.cast", self.grow([1.4, 0.0, 1.2], 0.05))), "ok")
        self.assertEqual((host.plant_factor("plant:oak-0"), host.plant_state["plant:oak-0"].sown), (1.25, None), "grown on, a plant like any other")

    def test_sprouts_on_bare_fertile_ground(self):
        host = self.host
        self.grass_floor()
        host.add_plant("plant:grass-0", [1.4, 0.0, 1.3], 0.13)
        host.add_plant("plant:daisies-0", [1.5, 0.0, 1.3], 0.1, "herb")
        host.add_plant("plant:oak-0", [0.2, 0.0, 1.3], 1.0, "broadleaf", "tree")
        host.learn_grow(2)
        here = self.grow([1.0, 0.0, 0.9], 0.5)
        preview = self.send(PLAYER, "nature.cast", here, preview=True)
        self.assertEqual((preview["data"]["kind"], preview["data"]["grown_plants"], host.sprouts), (m.KIND_SPROUTS, 12, []))
        sprouted = self.send(PLAYER, "nature.cast", here)
        self.assertEqual((refusal(sprouted), sprouted["data"]["grown_plants"], len(host.sprouts)), ("ok", 12, 12))
        self.assertEqual({s.template for s in host.sprouts} <= {"plant:grass-0", "plant:daisies-0"}, True, "never a tree")
        self.assertTrue(all(s.factor == 0.5 for s in host.sprouts))
        casts = [12]
        while True:
            more = self.send(PLAYER, "nature.cast", here)
            if not more["ok"]:
                break
            casts.append(more["data"]["grown_plants"])
        self.assertEqual((refusal(more), sum(casts)), (("invalid_args", "$.args.center_m", m.SPROUTS_GROUND_FULL), len(host.sprouts)))
        self.assertTrue(self.send(PLAYER, "room.undo", {"to_revision": host.revision - 1}, expected_revision=host.revision)["ok"])
        self.assertEqual(len(host.sprouts), sum(casts) - casts[-1], "Creative's undo takes the last cast's sprouts back")
        self.later(3 * 3600)
        self.send(PLAYER, "nature.cast", self.grow([1.0, 0.0, 0.9], 0.01), preview=True)
        self.assertTrue(all(s.factor == 1.0 for s in host.sprouts), "three hours on, at their natural size")

    def test_a_bridge_needs_a_gap_the_box_world_does_not_have(self):
        self.host.learn_grow(3)
        start = [0.2, 0.0, 1.0]
        self.assertEqual(refusal(self.send(PLAYER, "nature.cast", {**self.grow(start), "end_m": [1.5, 0.0, 1.0]})),
                         ("invalid_args", "$.args.end_m", m.BRIDGE_TOO_LONG.format(most="1.2")))
        self.assertEqual(refusal(self.send(PLAYER, "nature.cast", {**self.grow(start), "end_m": [0.4, 0.0, 1.0]})),
                         ("invalid_args", "$.args.end_m", m.BRIDGE_TOO_SHORT.format(least="0.4")))
        self.assertEqual(refusal(self.send(PLAYER, "nature.cast", {**self.grow(start), "end_m": [1.2, 0.0, 1.0]})),
                         ("invalid_args", "$.args.end_m", m.BRIDGE_NO_GAP), "the floor is under the whole deck")


class ThePrice(NatureCase):
    def test_the_price_follows_the_principal_never_a_field(self):
        """Outside Creative a cast pays the pack's price by its principal, once, at commit: the player's order the ordered price, the
        Gubble's own idea half of it (storybook_wild v5: Grow 4 and 2, Fire 2 and 1); a preview, a replay and a refused cast pay
        nothing; no field changes a price; an empty pool refuses with the balance and the price; the pool refills on the clock."""
        host = self.host
        host.learn_fire()
        host.add_plant("plant:daisies-0", [0.6, 0.0, 1.0], 0.1)
        self.assertTrue(self.send(PLAYER, "world.set_mode", {"mode": "challenge"})["ok"])
        here = self.grow(IN_THE_OPEN)
        self.assertEqual(self.pips(), 8)
        self.assertTrue(self.send(PLAYER, "nature.cast", here, preview=True)["ok"])
        self.assertEqual(self.pips(), 8, "a preview pays nothing")
        mine = self.send(PLAYER, "nature.cast", here)
        self.assertEqual((refusal(mine), self.pips()), ("ok", 4), "the player's order: 4")
        self.assertTrue(self.host.handle(PLAYER, command("nature.cast", here, mine["action_id"]))["replayed"])
        self.assertEqual(self.pips(), 4, "a replay pays nothing")
        self.assertTrue(self.send(COMPANION, "nature.cast", here)["ok"])
        self.assertEqual(self.pips(), 2, "the Gubble's own idea: 2")
        for smuggled in ("price", "ordered", "directed_by", "own_idea_factor"):
            with self.subTest(field=smuggled):
                self.assertEqual(refusal(self.send(COMPANION, "nature.cast", {**here, smuggled: 0}))[0], "field_unknown")
        revision = self.host.revision
        empty = self.send(PLAYER, "nature.cast", here)
        self.assertEqual((empty["error"]["code"], empty["error"]["message"], empty["error"]["balance"], empty["error"]["price"],
                          empty["error"]["field_path"], empty["error"]["retryable"], self.host.revision),
                         ("energy_exhausted", ENERGY_EXHAUSTED_MESSAGE, 2, 4, "$.args.capability", True, revision))
        fire = self.light(COMPANION, self.fire(ON_THE_FLOOR, duration=2))
        self.assertEqual((refusal(fire), self.pips()), ("ok", 1), "the Gubble's Fire: 1")
        self.later(5)
        self.assertEqual(self.pips(), 2, "one pip every 5 s while the Gubble is near the player")

    def test_in_creative_the_shadow_pool_shows_the_price_and_never_refuses(self):
        self.host.add_plant("plant:daisies-0", [0.6, 0.0, 1.0], 0.1)
        for _ in range(3):
            self.assertTrue(self.send(PLAYER, "nature.cast", self.grow(IN_THE_OPEN))["ok"])
        status = self.ask(PLAYER, "player.status")["data"]["energy"]
        self.assertEqual(status, {"pips": 0, "max_pips": 8, "shadow": True})


class Undo(NatureCase):
    def test_undo_takes_back_the_latest_nature_changes(self):
        """room.undo names the revision before a nature change: everything the change and its fire did comes back (the burnt rug,
        the grown plant), the fire goes out, and the undo is itself a revision; the Gubble's undo of the player's change waits for
        the player; a revision that is not just before one is invalid_args, with the one it can reach."""
        host = self.host
        host.learn_fire()
        host.add_plant("plant:daisies-0", [0.6, 0.0, 1.0], 0.1)
        self.assertTrue(self.send(PLAYER, "entity.place", {"target": "obj:book", "placement": {"position_m": [0.3, 0, 0.3]}})["ok"])
        moved = host.revision
        self.assertTrue(self.send(PLAYER, "nature.cast", self.grow(IN_THE_OPEN))["ok"])
        grown = host.revision
        self.assertTrue(self.light(COMPANION, self.fire(IN_THE_OPEN, duration=20))["ok"])
        self.later(6)
        self.assertEqual(refusal(self.ask(PLAYER, "entity.inspect", {"target": "obj:rug"}))[0], "target_not_found")
        burnt = host.revision
        past_the_move = self.send(PLAYER, "room.undo", {"to_revision": moved - 1}, expected_revision=burnt)
        self.assertEqual((refusal(past_the_move), past_the_move["error"]["allowed"]),
                         (("invalid_args", "$.args.to_revision", m.UNDO_NOT_REACHABLE), grown), "a move cannot be undone")
        undone = self.send(PLAYER, "room.undo", {"to_revision": grown}, expected_revision=burnt)
        self.assertEqual((refusal(undone), undone["data"], undone["revision"]),
                         ("ok", {"undone": "Fire", "undone_steps": 1, "requires_approval": False}, burnt + 1))
        self.assertEqual(refusal(self.ask(PLAYER, "entity.inspect", {"target": "obj:rug"})), "ok", "the rug is back")
        self.assertIsNone(host.fire)
        self.assertEqual((host.plant_factor("plant:daisies-0"), self.ask(PLAYER, "nature.describe")["data"]["can_undo"]), (1.25, True))
        again = self.send(PLAYER, "room.undo", {"to_revision": moved}, expected_revision=host.revision)
        self.assertEqual((refusal(again), again["data"]["undone"], host.plant_factor("plant:daisies-0")), ("ok", "Grow", 1.0))
        self.assertEqual(host.entities["obj:book"].position, [0.3, 0, 0.3], "the move stays")

    def test_undo_back_two_steps_and_the_gubbles_undo_of_the_players_change_waits(self):
        host = self.host
        host.add_plant("plant:daisies-0", [0.6, 0.0, 1.0], 0.1)
        self.assertTrue(self.send(PLAYER, "nature.cast", self.grow(IN_THE_OPEN))["ok"])
        self.assertTrue(self.send(COMPANION, "nature.cast", self.grow(IN_THE_OPEN))["ok"])
        self.assertEqual(host.plant_factor("plant:daisies-0"), 1.5)
        own = self.send(COMPANION, "room.undo", {"to_revision": 1}, expected_revision=2, preview=True)
        self.assertEqual((refusal(own), own["data"]["requires_approval"]), ("ok", False), "its own change: no ask")
        held = self.send(COMPANION, "room.undo", {"to_revision": 0}, expected_revision=2)
        self.assertEqual((held["error"]["code"], held["approval_needed"]["reason"]), ("approval_required", m.UNDO_HOLD))
        decided = host.player_decide(held["approval_needed"]["request_id"], True)
        self.assertEqual((decided["state"], decided["result"]["data"]), ("approved", {"undone": "Grow, Grow", "undone_steps": 2, "requires_approval": True}))
        self.assertEqual(host.plant_factor("plant:daisies-0"), 1.0)
        self.assertEqual(refusal(self.send(PLAYER, "room.undo", {"to_revision": 2}, expected_revision=3)),
                         ("unsupported_capability", "$.args.to_revision", m.UNDO_ONLY_NATURE))

    def test_what_happens_outside_creative_stays(self):
        """A fire lit in Creative that burns on in Challenge seals its step: back in Creative it cannot be undone; a change of another
        kind after a nature change breaks the chain; a partly burnt thing locked since is the player's to unlock first."""
        host = self.host
        host.learn_fire()
        self.assertTrue(self.light(PLAYER, self.fire(IN_THE_OPEN, duration=10))["ok"])
        lit = host.revision
        self.assertTrue(self.send(PLAYER, "world.set_mode", {"mode": "challenge"})["ok"])
        self.later(1)
        self.assertTrue(self.send(PLAYER, "world.set_mode", {"mode": "creative"})["ok"])
        self.assertEqual(refusal(self.send(PLAYER, "room.undo", {"to_revision": lit - 1}, expected_revision=lit)),
                         ("unsupported_capability", "$.args.to_revision", m.UNDO_SEALED))
        self.later(30)
        self.assertTrue(self.light(PLAYER, self.fire([0.45, 0.0, 0.25], radius=0.2, duration=1))["ok"])
        self.later(3)
        burnt = host.revision
        self.assertTrue(0 < host.burn_records["obj:book"].burn < 1, host.burn_records.get("obj:book"))
        self.assertTrue(self.send(PLAYER, "protect.lock", {"targets": ["obj:book"]}, expected_entities={"obj:book": 0})["ok"])
        self.assertEqual(refusal(self.send(PLAYER, "room.undo", {"to_revision": burnt - 1}, expected_revision=host.revision))[2],
                         m.UNDO_ONLY_NATURE, "the lock came between")
        self.assertTrue(self.send(PLAYER, "protect.unlock", {"targets": ["obj:book"]},
                                  expected_entities={"obj:book": host.entities["obj:book"].revision})["ok"])


class Fireline(NatureCase):
    def test_fireline_is_learned_along_a_line_then_drags(self):
        host = self.host
        host.learn_fire()
        line = self.fire([-0.2, 0.0, 1.0], end_m=[0.8, 0.0, 1.0])
        self.assertEqual(refusal(self.send(PLAYER, "nature.cast", line, preview=True)), ("permission_denied", "$.args.end_m", m.FIRELINE_NOT_LEARNED))
        for x in (-0.2, 0.4, 1.0):
            self.assertTrue(self.light(PLAYER, self.fire([x, 0.0, 1.3], radius=0.1, duration=1))["ok"])
            self.later(2)
        self.assertIn("fireline", host.nature_expertise["fire"])
        too_long = self.send(PLAYER, "nature.cast", self.fire([-0.2, 0.0, 1.0], end_m=[-0.2, 0.0, -2.5]), preview=True)
        self.assertEqual((refusal(too_long), too_long["error"]["allowed"]), (("invalid_args", "$.args.end_m", "A Fireline is at most 3 m long."), 3.0))
        self.assertEqual(refusal(self.send(PLAYER, "nature.cast", self.fire([0.5, 0.0, 1.0], end_m=[0.6, 0.0, 1.0]), preview=True)),
                         ("invalid_args", "$.args.end_m", m.FIRELINE_TOO_SHORT))
        preview = self.send(PLAYER, "nature.cast", line, preview=True)
        self.assertEqual((preview["data"]["kind"], preview["data"]["end_m"]), (m.KIND_FIRELINE, [0.8, 0.0, 1.0]))
        cast = self.send(PLAYER, "nature.cast", {**line, "preview_id": preview["data"]["preview_id"]})
        self.assertEqual((refusal(cast), cast["data"]["kind"], cast["data"]["started_cells"] > 0), ("ok", m.KIND_FIRELINE, True))
        self.assertEqual(self.send(PLAYER, "room.undo", {"to_revision": cast["revision"] - 1}, expected_revision=cast["revision"])["data"]["undone"],
                         "Fireline")


class TheBoundary(NatureCase):
    def test_the_gubbles_own_pop_that_hurts_is_a_lesson(self):
        """Decision 96: when the Gubble popped the carry holding the player by its own action and the landing hurt, a consequence
        fact in the kernel's words that costs Trust; the player's own pop, the timer's and anything in Creative never."""
        host = self.host
        self.assertTrue(self.send(PLAYER, "world.set_mode", {"mode": "challenge"})["ok"])
        feet = host.entities["avatar:player"].position
        carry = {"capability": "bubble_carry", "center_m": list(feet), "radius_m": 0.08, "duration_s": 8, "target": "avatar:player"}

        def carried() -> None:
            preview = self.send(COMPANION, "nature.cast", carry, preview=True)
            self.later(1.2)
            self.assertTrue(self.send(COMPANION, "nature.cast", {**carry, "preview_id": preview["data"]["preview_id"]})["ok"])

        carried()
        self.assertTrue(host.player_pops_bubble())
        self.assertFalse(host.player_lands(hurt=True), "the player's own pop")
        carried()
        self.assertTrue(self.send(COMPANION, "goal.stop", {})["ok"])
        self.assertTrue(host.player_lands(hurt=True))
        self.assertEqual([line for line in self.lessons() if line.startswith(m.POP_FALL_PREFIX)], [m.POP_FALL_PREFIX + ' near "Rug".'])
        self.assertEqual(host.trust_count, -3)
        carried()
        self.later(9)
        self.assertFalse(host.player_lands(hurt=True), "the timer's pop")
        self.assertTrue(self.send(PLAYER, "world.set_mode", {"mode": "creative"})["ok"])
        carried()
        self.send(COMPANION, "goal.stop", {})
        self.assertFalse(host.player_lands(hurt=True), "in Creative nothing hurts")

    def test_the_players_bubbles_key_names_eight_seconds(self):
        result = self.host.player_bubble([0.4, 0.0, 1.0])
        self.assertEqual((refusal(result), result["data"]["duration_s"], result["data"]["kind"]), ("ok", 8.0, "walk_in"))

    def test_every_player_only_op_is_still_refused_to_the_companion(self):
        """Hosting nature opened nothing: every op of the contract's player_only_ops is refused to the companion before anything is
        looked up, never held."""
        examples = {"protect.unlock": {"targets": ["obj:book"]}, "world.set_physics": {"preset": "room_real"}, "world.set_mode": {"mode": "challenge"},
                    "glow.configure": {"touch": "chime"}, "wish.submit": {"text": "Light a fire"}, "wish.stop": {},
                    "item.pickup": {"target": "fractal:k3x9q2"}, "item.gather": {"target": "obj:book"}, "knack.set": {"slot": 1, "knack": "quick_feet"},
                    "fractal.spend": {"grow": "heart"}, "receipt.mark": {"action_id": "n-1", "mark": "yes"}, "trust.reset": {}}
        for op in sorted(contracts().player_only_ops):
            with self.subTest(op=op):
                if op in contracts().query_ops:
                    result = self.ask(COMPANION, op, {})
                else:
                    result = self.send(COMPANION, op, examples[op], expected_revision=self.host.revision)
                self.assertEqual((result["error"]["code"], result["error"].get("field_path")), ("permission_denied", "$.op"))
                self.assertNotIn("approval_needed", result)

    def test_what_the_companion_learns_is_the_teams_knowledge(self):
        """A fire's affected things and the scorched builds count only what the team has perceived: a build hidden from both avatars
        is no affected id, and no scorched one, for the companion; the player knows all of it."""
        host = self.host
        host.learn_fire()
        hidden = self.build(BEHIND_BOX, "Hideout")
        self.assertTrue(self.send(PLAYER, "world.set_mode", {"mode": "challenge"})["ok"])
        args = self.fire([1.3, 0.0, 0.5], radius=0.5, duration=6)
        player_view = self.send(PLAYER, "nature.cast", args, preview=True)["data"]["affected_ids"]
        companion_view = self.send(COMPANION, "nature.cast", args, preview=True)["data"]["affected_ids"]
        self.assertIn(hidden, player_view)
        self.assertNotIn(hidden, companion_view)
        self.assertTrue(self.light(PLAYER, args)["ok"])
        self.later(5)
        self.assertEqual((self.ask(PLAYER, "nature.describe")["data"]["scorched"], self.ask(COMPANION, "nature.describe")["data"]["scorched"]),
                         ([hidden], []))


if __name__ == "__main__":
    unittest.main()
