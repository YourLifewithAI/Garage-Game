"""The Gubble's abilities: Glow (docs/runs/RUN-2-GLOW.md section 5), then Bubbles and Fireworks
(docs/runs/RUN-2-BUBBLES-FIREWORKS.md piece 5), now with storybook_wild v4's earned Glow allowance and nature routes.
The companion's boundary on the mock host and through the MCP surface.

test_host_alignment.py runs the steps the link can drive (the companion's casts, refusals, caps, expiry) on both hosts
with the same expected outcomes. This file adds what only the mock can stage, each against the kernel host's rules
(CommandHostEffects.cs, one set of rules for every primitive): other packs (cast_by, targets, tiers, packs that fail),
the player's own casts and stops, a second companion, the engine's cap across kinds, the team's sight turned off, no
Gubble in the room, a companion moved to the edge of Fireworks' reach; and the same boundary through MCP tool calls,
where reserved parameter names never leave the adapter.
"""
from __future__ import annotations

import copy
import dataclasses
import json
import tempfile
import unittest
from pathlib import Path

from support import (BEHIND_BOX, COMPANION, FAR_IN_SIGHT, GUBBLE, IN_THE_OPEN, NO_HOLDS, PLAYER, REPO, FakeClock,
                     HostPolicy, cast, command, contract_problems, glow, new_host, query)

from mcp_harness import McpHarness

from garage_game_companion.mock_host import RulesError, rules_path
from garage_game_companion.server import INSTRUCTIONS

PACK = json.loads(rules_path().read_bytes())
SPOT_ONLY_THE_PLAYER_SEES = [0.6, 0.0, 0.2]  # with the companion behind the box (below), the box hides it from the companion
BEHIND_THE_BOX = [1.6, 0.0, 0.2]
ABILITIES = ["bubbles", "fire", "fireworks", "glow", "grow"]
EFFECT_ABILITIES = ["bubbles", "fireworks", "glow"]


def variant(**changes) -> dict:
    """The shipped pack with its glow changed."""
    return ability_variant("glow", **changes)


def ability_variant(capability: str, **changes) -> dict:
    """The shipped pack with one ability changed."""
    pack = copy.deepcopy(PACK)
    next(a for a in pack["abilities"] if a["capability"] == capability).update(changes)
    return pack


class GlowCase(unittest.TestCase):
    policy: HostPolicy | None = None

    def setUp(self):
        self.clock = FakeClock()
        self.host = new_host(policy=self.policy, clock=self.clock)

    def tearDown(self):
        for result in self.host.emitted:
            self.assertEqual(contract_problems(result), [], result)

    def send(self, message, principal=COMPANION):
        return self.host.handle(principal, message)

    def player(self, message):
        return self.host.player_command(message)

    def assertRefused(self, result, code, field_path=None):
        self.assertFalse(result["ok"], result)
        self.assertEqual(result["error"]["code"], code, result)
        if field_path is not None:
            self.assertEqual(result["error"].get("field_path"), field_path, result)


class TheIslandsRules(GlowCase):
    def test_cast_by_withholds_a_caster(self):
        self.host.use_rules(variant(cast_by=["player"]))
        self.assertRefused(self.send(glow("c-1")), "permission_denied", "$.args.capability")
        self.assertTrue(self.player(glow("p-1"))["ok"])
        self.host.use_rules(variant(cast_by=["companion"]))
        self.assertEqual(self.host.active_effect_ids(), [], "new rules end the glows started under the old")
        self.assertRefused(self.player(glow("p-2")), "permission_denied", "$.args.capability")
        self.assertTrue(self.send(glow("c-2"))["ok"])

    def test_an_ability_without_self_or_without_point(self):
        self.host.use_rules(variant(targets=["point"]))
        refused = self.send(glow("c-1"))
        self.assertRefused(refused, "invalid_args", "$.args.targets")
        self.assertEqual(refused["error"]["allowed"], ["point"])
        self.assertTrue(self.send(glow("c-2", at=IN_THE_OPEN))["ok"])
        self.host.use_rules(variant(targets=["self"]))
        self.assertRefused(self.send(glow("c-3", at=IN_THE_OPEN)), "invalid_args", "$.args.targets")
        self.assertTrue(self.send(glow("c-4"))["ok"])

    def test_the_bounds_are_the_packs(self):
        self.host.use_rules(variant(duration_max_s=400, area_radius_max_m=1.0,
                                    params={"intensity": {"min": 0.3, "max": 0.5, "default": 0.4}}))
        self.assertRefused(self.send(glow("c-1", params={}, duration=450)), "invalid_args", "$.args.duration_s")
        self.assertRefused(self.send(glow("c-2", params={}, radius=1.2)), "invalid_args", "$.args.area.radius_m")
        too_bright = self.send(glow("c-3", params={"intensity": 0.6}))
        self.assertEqual(too_bright["error"]["allowed"], [0.3, 0.5])
        listed = self.send(query("capabilities.list", {}))["data"]["items"]
        self.assertEqual([item["capability"] for item in listed], ABILITIES)
        self.assertEqual(listed[ABILITIES.index("glow")], {"capability": "glow", "category": "light", "params": {"intensity": {"min": 0.3, "max": 0.5}},
                                     "area_radius_max_m": 1.0, "duration_max_s": 400, "reach_m": 2.0,
                                     "max_active": 3, "command_op": "effect.start"})
        started = self.send(glow("c-4", params={}))
        self.assertEqual(started["data"]["params"], {"intensity": 0.4})

    def test_a_pack_that_fails_its_checks_leaves_no_abilities(self):
        broken = {
            "a reserved param name": variant(params={"owner": {"min": 0, "max": 1, "default": 0},
                                                     "intensity": {"min": 0.2, "max": 1.0, "default": 0.6}}),
            "an intensity over light.emit's 2.0": variant(params={"intensity": {"min": 0.2, "max": 2.5, "default": 0.6}}),
            "max_active over the engine's 8": variant(max_active=9),
            "a reach over light.emit's 5 m": variant(reach_m=6),
            "a default outside its range": variant(params={"intensity": {"min": 0.2, "max": 1.0, "default": 1.2}}),
            "a companion that is not a principal": variant(cast_by=["companion", "guest"]),
            # Bubbles and Fireworks: their primitives' own outer limits (contracts/README.md, the island's rules).
            "bubbles longer than particles.float's 120 s": ability_variant("bubbles", duration_max_s=150),
            "bubbles more than particles.float's 4 at once": ability_variant("bubbles", max_active=5),
            "fireworks longer than particles.burst's 10 s": ability_variant("fireworks", duration_max_s=12),
            "fireworks reaching past particles.burst's 8 m": ability_variant("fireworks", reach_m=9),
            "fireworks in the float category": ability_variant("fireworks", category="float"),
            "bubbles brighter than 2.0": ability_variant("bubbles", params={"intensity": {"min": 0.2, "max": 2.5, "default": 0.6}}),
            "a primitive the engine lacks": ability_variant("bubbles", primitive="particles.rain"),
        }
        for label, pack in broken.items():
            with self.subTest(label):
                with self.assertRaises(RulesError):
                    self.host.use_rules(pack)
        # Each primitive's limits are its own: Fireworks may reach past light.emit's 5 m, up to particles.burst's 8.
        self.host.use_rules(ability_variant("fireworks", reach_m=6.0))
        self.assertEqual(self.host.rules.ability("fireworks").reach_m, 6.0)
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "rules" / "storybook_wild" / rules_path().name
            path.parent.mkdir(parents=True)
            path.write_bytes(rules_path().read_bytes().replace(b'"reach_m": 2.0,', b'"reach_m": 2.0, "reach_m": 4.0,'))
            self.host.load_rules(path)  # a duplicate key: refused, never a crash
        self.assertIsNone(self.host.rules)
        self.assertIn("did not load", self.host.rules_notice)
        self.assertRefused(self.send(glow("c-1")), "unsupported_capability", "$.args.capability")
        self.assertEqual(self.send(query("capabilities.list", {}))["data"], {"items": []})
        self.assertTrue(self.send(command("effect.stop", {"effect": "all"}, "s-1"))["ok"], "stops still apply")
        self.host.load_rules(REPO / "game" / "rules" / "storybook_wild" / "v9.json")
        self.assertIsNone(self.host.rules)
        self.host.load_rules(rules_path())
        self.assertTrue(self.send(glow("c-2"))["ok"])

    def test_a_keyed_yes_ability_waits_for_the_players_click_even_with_nothing_else_held(self):
        self.host.policy = NO_HOLDS
        self.host.use_rules(variant(tier="keyed_yes"))
        self.assertRefused(self.send(glow("c-1", params={"intensity": 1.4})), "invalid_args")  # checked before it is held
        held = self.send(glow("c-2"))
        self.assertRefused(held, "approval_required")
        self.assertEqual(held["approval_needed"]["reason"], "The companion asks to use glow. Approve or deny it in the game.")
        self.assertEqual(self.host.active_effect_ids(), [])
        decision = self.host.player_decide(held["approval_needed"]["request_id"], approve=True)
        self.assertEqual(decision["state"], "approved")
        self.assertEqual((decision["result"]["approved_by"], decision["result"]["principal"]), (PLAYER, COMPANION))
        self.assertEqual(self.host.active_effect_ids(), decision["result"]["created"])
        self.assertTrue(self.player(glow("p-1", at=IN_THE_OPEN))["ok"], "the player's own runs at once")


class TheGubbleCarriesItOut(GlowCase):
    def test_the_player_may_cast_only_on_the_gubble_or_a_spot(self):
        for targets in (["avatar:player"], ["obj:rug"], ["obj:nothing_here"], [GUBBLE, "obj:rug"]):
            with self.subTest(targets=targets):
                self.assertRefused(self.player(glow(f"p-{len(targets)}-{targets[-1][-3:]}", targets=targets)),
                                   "invalid_args", "$.args.targets")
        self.assertEqual(self.host.active_effect_ids(), [])

    def test_no_gubble_in_the_room_is_not_ready(self):
        self.host.entities[GUBBLE].removed = True
        result = self.player(glow("p-1", at=IN_THE_OPEN))
        self.assertRefused(result, "not_ready")
        self.assertTrue(result["error"]["retryable"])

    def test_max_active_counts_both_principals_then_the_engine_caps_the_room(self):
        self.assertTrue(self.player(glow("p-1"))["ok"])
        self.assertTrue(self.player(glow("p-2", at=IN_THE_OPEN))["ok"])
        self.assertTrue(self.send(glow("c-1"))["ok"])
        for result in (self.send(glow("c-2")), self.player(glow("p-3"))):
            self.assertRefused(result, "budget_exceeded", "$.args.capability")
            self.assertEqual((result["error"]["allowed"], result["error"]["actual"]), (3, 3))
        self.player(command("effect.stop", {"effect": "all"}, "p-stop"))
        self.host.policy = dataclasses.replace(self.host.policy, max_active_effects=2)
        self.assertTrue(self.send(glow("c-3"))["ok"])
        self.assertTrue(self.player(glow("p-4"))["ok"])
        capped = self.send(glow("c-4", at=IN_THE_OPEN))
        self.assertRefused(capped, "budget_exceeded", "$.args.capability")
        self.assertEqual(capped["error"]["allowed"], 2)

    def test_a_companion_stops_only_its_own_glows_and_the_player_any(self):
        host = new_host(clock=self.clock, extra_companions={"companion:other": "avatar:other"})
        mine = host.handle(COMPANION, glow("c-1"))["created"][0]
        theirs = host.handle("companion:other", glow("o-1", targets=["avatar:other"]))["created"][0]
        players = host.player_command(glow("p-1", at=IN_THE_OPEN))["created"][0]
        self.assertEqual(host.handle(COMPANION, glow("c-2", targets=["avatar:other"]))["error"]["code"], "permission_denied")
        for which in (theirs, players, "all"):
            stop = host.handle(COMPANION, command("effect.stop", {"effect": which}, f"s-{which[-3:]}"))
            self.assertTrue(stop["ok"], stop)
        self.assertEqual(host.active_effect_ids(), [theirs, players])
        self.assertNotIn(mine, host.active_effect_ids())
        self.assertNotIn(players, host.handle(COMPANION, command("goal.stop", {}, "s-goal"))["affected"])
        everything = host.player_command(command("effect.stop", {"effect": "all"}, "p-stop"))
        self.assertEqual((everything["affected"], everything["data"]["effects_stopped"]), ([theirs, players], 2))
        for result in host.emitted:
            self.assertEqual(contract_problems(result), [], result)

    def test_a_preview_starts_nothing_and_a_previewed_stop_stops_nothing(self):
        preview = self.send(glow("c-1", at=IN_THE_OPEN, preview=True))
        self.assertTrue(preview["ok"] and preview["preview"], preview)
        self.assertEqual(self.host.active_effect_ids(), [])
        light = self.send(glow("c-2"))["created"][0]
        for message in (command("effect.stop", {"effect": "all"}, "s-1", preview=True),
                        command("goal.stop", {}, "s-2", preview=True)):
            result = self.send(message)
            self.assertTrue(result["ok"] and result["preview"], result)
            self.assertNotIn("affected", result)
        self.assertEqual(self.host.active_effect_ids(), [light])

    def test_glows_are_not_entities(self):
        before = (self.host.revision, copy.deepcopy(self.host._snapshot()))
        light = self.send(glow("c-1"))["created"][0]
        wisp = self.send(glow("c-2", at=IN_THE_OPEN))["created"][0]
        listed = json.dumps([self.send(query("entities.list", {}, "q-1")),
                             self.send(query("observe", {"actor": GUBBLE}, "q-2")),
                             self.send(query("room.describe", {}, "q-3")),
                             self.send(query("map.find", {"name": "glow"}, "q-4"))])
        self.assertNotIn(light, listed)
        self.assertNotIn(wisp, listed)
        self.assertRefused(self.send(query("entity.inspect", {"target": light}, "q-5")), "target_not_found")
        self.assertEqual((self.host.revision, self.host._snapshot()), before, "Glow learning is separate from room state")


class TheTeamsSight(GlowCase):
    def setUp(self):
        super().setUp()
        self.host.move_avatar(GUBBLE, BEHIND_THE_BOX)

    def test_with_the_teams_sight_the_companion_lights_a_spot_only_the_player_sees(self):
        self.assertNotIn("obj:book", self.host.perceived(COMPANION))  # the box is between them
        self.assertTrue(self.send(glow("c-1", at=SPOT_ONLY_THE_PLAYER_SEES))["ok"])


class OneAvatarsSight(GlowCase):
    policy = HostPolicy(shared_sight=False, team_sight_interval_s=0)

    def test_without_it_only_the_casters_own_eyes_count(self):
        self.host.move_avatar(GUBBLE, BEHIND_THE_BOX)
        self.assertRefused(self.send(glow("c-1", at=SPOT_ONLY_THE_PLAYER_SEES)), "target_not_found", "$.args.area.center_m")
        self.assertTrue(self.player(glow("p-1", at=SPOT_ONLY_THE_PLAYER_SEES))["ok"], "the player's own eyes count for the player")


class GlowThroughMcp(unittest.IsolatedAsyncioTestCase):
    def assertRefused(self, result, code, field_path=None):
        self.assertFalse(result["ok"], result)
        self.assertEqual(result["error"]["code"], code, result)
        if field_path is not None:
            self.assertEqual(result["error"].get("field_path"), field_path, result)

    @staticmethod
    def args(action_id, **kwargs) -> dict:
        message = glow(action_id, **kwargs)
        return {"action_id": action_id, **message["args"], **{k: v for k, v in message.items() if k == "preview"}}

    async def test_the_companion_glows_itself_and_a_spot_and_stops_them(self):
        async with McpHarness() as h:
            listed = await h.call("capabilities_list", {})
            self.assertEqual([i["capability"] for i in listed["data"]["items"]], ABILITIES)
            own = await h.call("effect_start", self.args("g-1", params={}))
            spot = await h.call("effect_start", self.args("g-2", at=IN_THE_OPEN))
            self.assertEqual((own["data"]["target"], spot["data"]["target"]), ("self", "point"))
            stop = await h.call("effect_stop", {"effect": spot["created"][0]})
            self.assertEqual((stop["affected"], stop["data"]["effects_stopped"]), (spot["created"], 1))
            ended = await h.call("goal_stop", {})
            self.assertIn(own["created"][0], ended["affected"])
            self.assertEqual(h.host.active_effect_ids(), [])

    async def test_the_boundary_holds_through_tool_calls(self):
        async with McpHarness() as h:
            players = h.host.player_command(glow("p-1", at=IN_THE_OPEN))["created"][0]
            for i, (targets, code) in enumerate(((["avatar:player"], "permission_denied"), (["obj:rug"], "permission_denied"),
                                                 ([players], "target_not_found"))):
                with self.subTest(targets=targets):
                    self.assertRefused(await h.call("effect_start", self.args(f"t-{i}", targets=targets)), code, "$.args.targets")
            self.assertRefused(await h.call("effect_start", self.args("b-1", params={"intensity": 1.5})),
                               "invalid_args", "$.args.params.intensity")
            self.assertRefused(await h.call("effect_start", self.args("b-2", at=BEHIND_BOX)),
                               "target_not_found", "$.args.area.center_m")
            for i in range(2):
                self.assertTrue((await h.call("effect_start", self.args(f"g-{i}")))["ok"])
            self.assertRefused(await h.call("effect_start", self.args("g-2")), "budget_exceeded")
            mine = await h.call("effect_stop", {"effect": "all"})
            self.assertEqual(mine["data"]["effects_stopped"], 2)
            self.assertEqual(h.host.active_effect_ids(), [players], "the player's glow outlasts the companion's stops")
            self.assertEqual((await h.call("effect_stop", {"effect": players}))["data"]["effects_stopped"], 0)

    async def test_reserved_parameter_names_never_leave_the_adapter(self):
        async with McpHarness() as h:
            for name in ("principal", "owner", "owner_id", "approval", "approval_id", "approved", "grant", "role"):
                with self.subTest(name=name):
                    self.assertRefused(await h.call("effect_start", self.args(f"r-{name}", params={name: 1})),
                                       "field_unknown", f"$.params.{name}")
            for name in ("actor", "room_id", "action_id"):
                with self.subTest(name=name):
                    self.assertRefused(await h.call("effect_start", self.args(f"r-{name}", params={name: 1})),
                                       "request_invalid", "$.args.params")
            self.assertEqual(h.host.emitted, [], "nothing reached the game")

    async def test_the_tools_say_what_self_and_point_mean(self):
        async with McpHarness() as h:
            tools = {tool.name: tool for tool in (await h.client.list_tools()).tools}
        start = tools["effect_start"]
        for words in ("self", "point", "your own avatar", "area.center_m", "capabilities_list", "effect_stop"):
            self.assertIn(words, start.description)
        properties = start.input_schema["properties"]
        self.assertIn("avatar:companion", properties["targets"]["description"])
        self.assertIn("capabilities_list", properties["capability"]["description"])
        self.assertIn("default", properties["params"]["description"])
        self.assertIn("effect_start", tools["effect_stop"].input_schema["properties"]["effect"]["description"])
        # Every ability the island grants is named where a model looks first, not only Glow.
        for name in EFFECT_ABILITIES:
            with self.subTest(ability=name):
                for text in (start.description, tools["capabilities_list"].description, INSTRUCTIONS.lower(),
                             properties["capability"]["description"]):
                    self.assertIn(name, text)
        self.assertIn("Glow is a light", INSTRUCTIONS)


# ---------------------------------------------------------------------------- Bubbles and Fireworks, on the mock

class BubblesAndFireworks(GlowCase):
    """storybook_wild v2's particle abilities under the same rules as Glow (CommandHostEffects.cs), on what only the mock
    can stage."""

    def test_cast_by_withholds_each_ability_on_its_own(self):
        self.host.use_rules(ability_variant("bubbles", cast_by=["player"]))
        self.assertRefused(self.send(cast("bubbles", "c-1", at=IN_THE_OPEN)), "permission_denied", "$.args.capability")
        self.assertTrue(self.send(cast("fireworks", "c-2"))["ok"], "the others are still the companion's")
        self.assertTrue(self.player(cast("bubbles", "p-1", at=IN_THE_OPEN))["ok"], "the player still casts it, through the Gubble")
        self.host.use_rules(ability_variant("fireworks", cast_by=["player"]))
        self.assertEqual((self.host.active_effect_ids(), list(self.host.bubbles)), ([], []), "new rules end what the old ones started")
        self.assertRefused(self.send(cast("fireworks", "c-3", at=IN_THE_OPEN)), "permission_denied", "$.args.capability")
        self.assertTrue(self.send(cast("bubbles", "c-4", at=IN_THE_OPEN))["ok"])
        self.host.use_rules(ability_variant("bubbles", cast_by=["companion"]))
        self.assertRefused(self.player(cast("bubbles", "p-2", at=IN_THE_OPEN)), "permission_denied", "$.args.capability")

    def test_an_ability_without_self_or_without_point(self):
        self.host.use_rules(ability_variant("fireworks", targets=["point"]))
        refused = self.send(cast("fireworks", "c-1"))
        self.assertRefused(refused, "invalid_args", "$.args.targets")
        self.assertEqual(refused["error"]["allowed"], ["point"])
        self.assertTrue(self.send(cast("fireworks", "c-2", at=IN_THE_OPEN))["ok"])
        # The Bubble is cast at a spot (decision 55): a pack without point leaves it nowhere to land, and never on the Gubble.
        self.host.use_rules(ability_variant("bubbles", targets=["self"]))
        self.assertRefused(self.send(cast("bubbles", "c-3", at=IN_THE_OPEN)), "invalid_args", "$.args.targets")
        self.assertRefused(self.send(cast("bubbles", "c-4")), "invalid_args", "$.args.targets")

    def test_the_player_casts_them_on_the_gubble_or_a_spot_and_nothing_else(self):
        # The Bubble only at a spot, where the place decides (here a bubble to walk into); never on the Gubble itself.
        on_gubble = self.player(cast("bubbles", "p-bubbles-1"))
        at_spot = self.player(cast("bubbles", "p-bubbles-2", at=IN_THE_OPEN))
        self.assertRefused(on_gubble, "invalid_args", "$.args.targets")
        self.assertEqual((at_spot["principal"], at_spot["data"]["target"], at_spot["data"]["kind"]), (PLAYER, "point", "walk_in"))
        for kind in ("bubbles", "fireworks"):
            with self.subTest(kind):
                if kind == "fireworks":
                    on_gubble = self.player(cast(kind, f"p-{kind}-1"))
                    at_spot = self.player(cast(kind, f"p-{kind}-2", at=IN_THE_OPEN))
                    self.assertEqual((on_gubble["data"]["target"], at_spot["data"]["target"]), ("self", "point"))
                    self.assertEqual(on_gubble["principal"], PLAYER)
                for targets in (["avatar:player"], ["obj:rug"], [GUBBLE, "obj:rug"]):
                    self.assertRefused(self.player(cast(kind, f"p-{kind}-{targets[-1][-3:]}-{len(targets)}", targets=targets)),
                                       "invalid_args", "$.args.targets")

    def test_no_companion_casts_on_another_avatar(self):
        host = new_host(clock=self.clock, extra_companions={"companion:other": "avatar:other"})
        for kind in ("bubbles", "fireworks"):
            with self.subTest(kind):
                for principal, targets in ((COMPANION, ["avatar:other"]), ("companion:other", [GUBBLE]),
                                           ("companion:other", ["avatar:player"]), (COMPANION, [GUBBLE, "avatar:other"])):
                    refused = host.handle(principal, cast(kind, f"{principal[-5:]}-{kind}-{targets[-1][-5:]}-{len(targets)}",
                                                          targets=targets))
                    self.assertEqual((refused["ok"], refused["error"]["code"], refused["error"]["field_path"]),
                                     (False, "permission_denied", "$.args.targets"), refused)
        self.assertEqual(host.active_effect_ids(), [])
        theirs = host.handle("companion:other", cast("fireworks", "o-1", targets=["avatar:other"]))["created"][0]
        mine = host.handle(COMPANION, cast("fireworks", "c-1"))["created"][0]
        stops = [host.handle(COMPANION, command("effect.stop", {"effect": which}, f"s-{i}")) for i, which in enumerate((theirs, "all"))]
        self.assertEqual([s["data"]["effects_stopped"] for s in stops], [0, 1])
        self.assertEqual(host.active_effect_ids(), [theirs], "a companion stops only its own")
        self.assertNotIn(mine, host.active_effect_ids())
        for result in host.emitted:
            self.assertEqual(contract_problems(result), [], result)

    def test_each_ability_has_its_own_cap_and_the_room_caps_them_all(self):
        # Starting Glow allowance (3), the one Bubble and the pack's 3 fireworks, across both principals.
        for i, (kind, who) in enumerate([("glow", self.player), ("glow", self.send), ("glow", self.send), ("bubbles", self.player),
                                         ("fireworks", self.send), ("fireworks", self.player), ("fireworks", self.send)]):
            self.assertTrue(who(cast(kind, f"fill-{i}", at=IN_THE_OPEN if kind == "bubbles" else None))["ok"], (kind, i))
        for kind, cap in (("glow", 3), ("fireworks", 3)):
            with self.subTest(kind):
                for who in (self.send, self.player):
                    refused = who(cast(kind, f"over-{kind}-{who.__name__}"))
                    self.assertRefused(refused, "budget_exceeded", "$.args.capability")
                    self.assertEqual((refused["error"]["allowed"], refused["error"]["actual"]), (cap, cap),
                                     "an ability's own cap answers first")
        # The Bubble is one at a time: a recast replaces it rather than being refused (decision 59).
        first = list(self.host.bubbles)
        self.assertTrue(self.send(cast("bubbles", "over-bubbles", at=IN_THE_OPEN))["ok"])
        self.assertEqual(len(self.host.bubbles), 1)
        self.assertNotEqual(list(self.host.bubbles), first)
        self.player(command("effect.stop", {"effect": "all"}, "p-stop"))
        self.host.policy = dataclasses.replace(self.host.policy, max_active_effects=4)
        for i, kind in enumerate(("bubbles", "fireworks", "fireworks", "bubbles", "fireworks")):
            self.assertTrue(self.send(cast(kind, f"room-{i}", at=IN_THE_OPEN if kind == "bubbles" or i % 2 else None))["ok"], (kind, i))
        capped = self.send(glow("room-glow"))
        self.assertRefused(capped, "budget_exceeded", "$.args.capability")
        self.assertEqual((capped["error"]["allowed"], capped["error"]["actual"]), (4, 4),
                         "then the room's cap, whatever the kind, the bubble counted at an effect's start")

    def test_fireworks_reach_four_metres_and_bubbles_two(self):
        self.host.move_avatar(GUBBLE, [1.8, 0.0, 1.3])
        near, far = [-1.8, 0.0, -0.3], [-1.9, 0.0, -0.6]  # 3.94 m and 4.16 m away, both in sight
        rocket = self.send(cast("fireworks", "c-1", at=near))
        self.assertTrue(rocket["ok"], rocket)
        for kind, spot, allowed in (("fireworks", far, 4.0), ("bubbles", near, 2.0), ("glow", near, 2.0)):
            with self.subTest(kind):
                refused = self.send(cast(kind, f"c-{kind}", at=spot))
                self.assertRefused(refused, "out_of_bounds", "$.args.area.center_m")
                self.assertEqual(refused["error"]["allowed"], allowed)
                self.assertGreater(refused["error"]["actual"], allowed)
                self.assertTrue(refused["error"]["retryable"], "it can come closer")

    def test_a_companions_stop_ends_its_own_of_every_kind_and_none_is_an_entity(self):
        before = (self.host.revision, copy.deepcopy(self.host._snapshot()))
        players = [self.player(cast("fireworks", "p-fireworks", at=IN_THE_OPEN))["created"][0]]
        mine = [self.send(cast("bubbles", "c-bubbles", at=IN_THE_OPEN))["created"][0], self.send(cast("fireworks", "c-fireworks"))["created"][0]]
        listed = json.dumps([self.send(query("entities.list", {}, "q-1")), self.send(query("observe", {"actor": GUBBLE}, "q-2")),
                             self.send(query("map.find", {"name": "bubbles"}, "q-3"))])
        for effect_id in players + mine:
            self.assertNotIn(effect_id, listed)
        self.assertRefused(self.send(query("entity.inspect", {"target": mine[0]}, "q-4")), "target_not_found")
        ended = self.send(command("goal.stop", {}, "s-1"))
        self.assertEqual(set(mine) & set(ended["affected"]), set(mine))
        self.assertEqual(self.host.active_effect_ids(), players, "the player's outlast the companion's stop")
        self.assertEqual(list(self.host.bubbles), [], "and the companion's bubble pops with it")
        self.assertEqual((self.host.revision, self.host._snapshot()), before, "effects change no saved state")


# ---------------------------------------------------------------------------- Bubbles and Fireworks, through MCP

class BubblesAndFireworksThroughMcp(unittest.IsolatedAsyncioTestCase):
    def assertRefused(self, result, code, field_path=None):
        self.assertFalse(result["ok"], result)
        self.assertEqual(result["error"]["code"], code, result)
        if field_path is not None:
            self.assertEqual(result["error"].get("field_path"), field_path, result)

    @staticmethod
    def args(capability, action_id, **kwargs) -> dict:
        message = cast(capability, action_id, **kwargs)
        return {"action_id": action_id, **message["args"]}

    @staticmethod
    def harness() -> McpHarness:
        # Many casts in a moment: lift the host's rate limit (its clock stands still here) and the adapter's burst.
        return McpHarness(host=new_host(policy=HostPolicy(companion_messages_per_s=1_000_000)), command_burst=100)

    async def test_the_companion_casts_both_on_itself_and_at_a_spot(self):
        async with self.harness() as h:
            listed = await h.call("capabilities_list", {})
            self.assertEqual([(i["capability"], i["category"]) for i in listed["data"]["items"]],
                             [("bubbles", "float"), ("fire", "fire"), ("fireworks", "burst"), ("glow", "light"), ("grow", "growth")])
            # The Bubble only at a spot (the place decides: here a bubble to walk into), never on the companion itself.
            on_itself = await h.call("effect_start", self.args("bubbles", "bubbles-1", params={}))
            self.assertRefused(on_itself, "invalid_args", "$.args.targets")
            started = {}
            started["bubbles", "point"] = await h.call("effect_start", self.args("bubbles", "bubbles-2", at=IN_THE_OPEN))
            started["fireworks", "self"] = await h.call("effect_start", self.args("fireworks", "fireworks-1", params={}))
            started["fireworks", "point"] = await h.call("effect_start", self.args("fireworks", "fireworks-2", at=IN_THE_OPEN))
            started["fireworks", "far"] = await h.call("effect_start", self.args("fireworks", "fireworks-3", at=FAR_IN_SIGHT))
            self.assertEqual({key: (r["ok"], r["data"]["capability"], r["data"]["target"]) for key, r in started.items()}, {
                ("bubbles", "point"): (True, "bubbles", "point"), ("fireworks", "self"): (True, "fireworks", "self"),
                ("fireworks", "point"): (True, "fireworks", "point"), ("fireworks", "far"): (True, "fireworks", "point")})
            self.assertEqual((started["bubbles", "point"]["data"]["kind"], started["fireworks", "self"]["data"]["params"]),
                             ("walk_in", {"intensity": 0.8}), "a bubble to walk into; the island's defaults")
            ids = [r["created"][0] for r in started.values()]
            stop = await h.call("effect_stop", {"effect": ids[1]})
            self.assertEqual((stop["affected"], stop["data"]["effects_stopped"]), ([ids[1]], 1))
            ended = await h.call("goal_stop", {})
            self.assertEqual(set(ids) - {ids[1]}, set(ended["affected"]) & set(ids))
            self.assertEqual((h.host.active_effect_ids(), list(h.host.bubbles)), ([], []))

    async def test_the_boundary_holds_through_tool_calls(self):
        async with self.harness() as h:
            players = h.host.player_command(cast("bubbles", "p-1", at=IN_THE_OPEN))["created"][0]
            refusals = {
                "bubbles on the player": (self.args("bubbles", "t-1", targets=["avatar:player"]),
                                          ("permission_denied", "$.args.targets")),
                "fireworks on a thing": (self.args("fireworks", "t-2", targets=["obj:rug"]), ("permission_denied", "$.args.targets")),
                "fireworks on the player's bubbles": (self.args("fireworks", "t-3", targets=[players]),
                                                      ("target_not_found", "$.args.targets")),
                "bubbles too thick": (self.args("bubbles", "b-1", params={"intensity": 1.5}, at=IN_THE_OPEN),
                                      ("invalid_args", "$.args.params.intensity")),
                "fireworks too long": (self.args("fireworks", "b-2", duration=8), ("invalid_args", "$.args.duration_s")),
                "bubbles too wide": (self.args("bubbles", "b-3", radius=1.2, at=IN_THE_OPEN), ("invalid_args", "$.args.area.radius_m")),
                "bubbles on the companion itself": (self.args("bubbles", "b-6"), ("invalid_args", "$.args.targets")),
                "bubbles too far": (self.args("bubbles", "b-4", at=FAR_IN_SIGHT), ("out_of_bounds", "$.args.area.center_m")),
                "fireworks unseen": (self.args("fireworks", "b-5", at=BEHIND_BOX), ("target_not_found", "$.args.area.center_m")),
            }
            for label, (arguments, (code, field_path)) in refusals.items():
                with self.subTest(label):
                    self.assertRefused(await h.call("effect_start", arguments), code, field_path)
            self.assertEqual((h.host.active_effect_ids(), list(h.host.bubbles)), ([], [players]), "no refusal started anything")
            # One bubble at a time: the companion's replaces the player's (decision 59), which pops.
            mine = await h.call("effect_start", self.args("bubbles", "m-1", at=[IN_THE_OPEN[0] - 0.2, 0.0, IN_THE_OPEN[2]]))
            self.assertTrue(mine["ok"], mine)
            self.assertEqual(list(h.host.bubbles), [mine["created"][0]])
            # What cast_by withholds is refused through the tools too.
            h.host.use_rules(ability_variant("fireworks", cast_by=["player"]))
            self.assertRefused(await h.call("effect_start", self.args("fireworks", "w-1")), "permission_denied",
                               "$.args.capability")
            self.assertEqual((await h.call("effect_stop", {"effect": "all"}))["data"]["effects_stopped"], 0,
                             "new rules ended everything; the companion's stop has nothing left")


if __name__ == "__main__":
    unittest.main()
