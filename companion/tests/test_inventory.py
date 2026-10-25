"""P-Inventory at the companion boundary (25 October; docs/game/MECHANICS.md decisions 98 to 103; the kernel's
Kernel/CommandHostInventory.cs, its suite native_kernel_inventory):

- the adapter lists inventory_store and inventory_take (the chest's one companion path) and never inventory_use or inventory_drop
  (player-only ops);
- on the mock, every companion refusal comes before anything is looked up, in the kernel's words and order: the pockets in `to` or
  `from` are the player's alone, and with "Let the Gubble use the chest" off (the default; only the player's control turns it on)
  every store and take is refused at $.op; use and drop are refused outright;
- with the switch on, the companion stores what its own avatar holds into the chest at a camp, and takes a thing back into its own
  hands, never the player's; a store and a take are durable room changes with data (one revision each, as on the kernel since
  25 October); player.status shows the chest and capabilities.list the ops;
- test_kernel_alignment-style checks hold the mock's words and numbers to the kernel's.
"""
from __future__ import annotations

import re
import unittest

from support import COMPANION, GUBBLE, PLAYER, REPO, command, contract_problems, contracts, new_host, query

from garage_game_companion import mock_host, mock_inventory

KERNEL = REPO / "game" / "scripts" / "native" / "Kernel"
INVENTORY = KERNEL / "CommandHostInventory.cs"


def refusal(result: dict):
    return "ok" if result["ok"] else (result["error"]["code"], result["error"].get("field_path"), result["error"]["message"])


class TheSurface(unittest.TestCase):
    def test_store_and_take_have_tools_and_use_and_drop_none(self):
        c = contracts()
        self.assertLessEqual({"inventory.store", "inventory.take", "inventory.use", "inventory.drop"}, set(c.command_ops))
        self.assertLessEqual({"inventory.use", "inventory.drop"}, c.player_only_ops)
        self.assertFalse({"inventory.store", "inventory.take"} & c.player_only_ops, "the chest's one path is behind the player's switch, never a missing tool")


class OnTheMock(unittest.TestCase):
    def setUp(self):
        self.host = new_host()
        self.n = 0

    def tearDown(self):
        for result in self.host.emitted:
            self.assertEqual(contract_problems(result), [], result)

    def send(self, principal, op, args, **extra):
        self.n += 1
        return self.host.handle(principal, command(op, args, f"inv-{self.n}", **extra))

    def ask(self, principal, op, args):
        self.n += 1
        return self.host.handle(principal, query(op, args, f"q-{self.n}"))

    def gubble_holds(self, target="obj:doorstop"):
        """The player has the Gubble pick something up (the player may direct the Gubble's avatar)."""
        grabbed = self.send(PLAYER, "entity.grab", {"target": target, "actor": GUBBLE})
        self.assertTrue(grabbed["ok"], grabbed)

    def test_every_companion_refusal_comes_before_anything_is_looked_up(self):
        self.assertFalse(self.host.gubble_chest, "off by default (decision 100)")
        # The companion holds nothing and the chest is empty: a lookup would say so. The switch answers first.
        self.assertEqual(refusal(self.send(COMPANION, "inventory.store", {"to": "chest"})), ("permission_denied", "$.op", mock_inventory.GUBBLE_CHEST_OFF))
        self.assertEqual(refusal(self.send(COMPANION, "inventory.take", {"item": "obj:nothing", "to": "hand"})),
                         ("permission_denied", "$.op", mock_inventory.GUBBLE_CHEST_OFF))
        for args, path in (({"to": "pockets"}, "$.args.to"), ({"from": "pockets", "to": "chest", "item": "apple"}, "$.args.from")):
            with self.subTest(args=args):
                self.assertEqual(refusal(self.send(COMPANION, "inventory.store", args)), ("permission_denied", path, mock_inventory.POCKETS_ARE_PLAYERS))
        self.assertEqual(refusal(self.send(COMPANION, "inventory.take", {"item": "apple", "to": "pockets"})),
                         ("permission_denied", "$.args.to", mock_inventory.POCKETS_ARE_PLAYERS))
        # P-Inventory-2: the pockets' take into the hand and the take off the ground are the player's too, refused where the kernel refuses them.
        for args, path in (({"from": "pockets", "item": "obj:cup", "to": "hand"}, "$.args.from"), ({"from": "ground", "item": "apple", "to": "pockets"}, "$.args.to")):
            with self.subTest(args=args):
                self.assertEqual(refusal(self.send(COMPANION, "inventory.take", args)), ("permission_denied", path, mock_inventory.POCKETS_ARE_PLAYERS))
        for op in ("inventory.use", "inventory.drop"):
            with self.subTest(op=op):
                self.assertEqual(refusal(self.send(COMPANION, op, {"item": "apple"})), ("permission_denied", "$.op", mock_inventory.PLAYER_ONLY))
        self.assertEqual(refusal(self.send(COMPANION, "inventory.store", {"to": "chest", "actor": "avatar:player"}))[0], "field_unknown",
                         "no field names an avatar: the player's hand is never the companion's")
        # The switch on: the pockets stay the player's, and the chest's path now looks things up.
        self.host.set_gubble_chest(True)
        self.assertEqual(refusal(self.send(COMPANION, "inventory.store", {"to": "pockets"}))[:2], ("permission_denied", "$.args.to"))
        self.assertEqual(refusal(self.send(COMPANION, "inventory.store", {"to": "chest"})), ("request_invalid", "$.args", mock_inventory.HOLD_SOMETHING))
        self.assertEqual(refusal(self.send(COMPANION, "inventory.take", {"item": "obj:nothing", "to": "hand"})),
                         ("target_not_found", "$.args.item", mock_inventory.NOT_IN_CHEST))

    def test_with_the_switch_on_the_gubble_uses_the_chest_with_its_own_hands(self):
        self.host.set_gubble_chest(True)
        start = list(self.host.entities["avatar:player"].position)
        self.gubble_holds()
        revision = self.host.revision
        stored = self.send(COMPANION, "inventory.store", {"to": "chest"})
        self.assertTrue(stored["ok"] and stored["transient"] is False and stored["data"] == {"item": "obj:doorstop", "count": 1, "to": "chest"}, stored)
        self.assertEqual((self.host.revision, stored["revision"], self.host.chest, self.host.holding.get(GUBBLE)), (revision + 1, revision + 1, ["obj:doorstop"], None),
                         "a durable room change, as the kernel's: one room revision")
        self.assertEqual(self.host.entities["obj:doorstop"].revision, revision + 1)
        replay = self.host.handle(COMPANION, command("inventory.store", {"to": "chest"}, f"inv-{self.n}"))
        self.assertTrue(replay["replayed"] and replay["revision"] == revision + 1 and self.host.chest == ["obj:doorstop"], replay)
        listed = {item["id"] for item in self.ask(COMPANION, "entities.list", {})["data"]["items"]}
        self.assertNotIn("obj:doorstop", listed, "a thing in the chest is gone from the room")
        status = self.ask(COMPANION, "player.status", {})["data"]
        self.assertEqual(status["chest"]["entries"], [{"item": "obj:doorstop", "kind": "thing", "name": "Doorstop", "count": 1, "mass_kg": 0.3}])
        self.assertEqual(status["pockets"]["entries"], [])
        taken = self.send(COMPANION, "inventory.take", {"item": "obj:doorstop", "to": "hand"})
        self.assertTrue(taken["ok"] and taken["transient"] is False and taken["revision"] == revision + 2 and
                        taken["data"] == {"item": "obj:doorstop", "count": 1, "to": "hand"}, taken)
        self.assertEqual((self.host.holding.get(GUBBLE), self.host.holding.get("avatar:player"), self.host.chest), ("obj:doorstop", None, []))
        self.assertEqual(self.host.entities["avatar:player"].position, start, "nothing the Gubble did moved the player")
        self.host.set_gubble_chest(False)
        self.assertEqual(refusal(self.send(COMPANION, "inventory.store", {"to": "chest"}))[:2], ("permission_denied", "$.op"), "off again")

    def test_the_chest_is_at_the_camp_and_has_caps(self):
        self.host.set_gubble_chest(True)
        self.gubble_holds("obj:book")
        self.host.entities[GUBBLE].position = [1.5, 0.0, -0.5]
        self.assertEqual(refusal(self.send(COMPANION, "inventory.store", {"to": "chest"})), ("not_ready", "$.args.to", mock_inventory.CHEST_AT_CAMP))
        self.host.entities[GUBBLE].position = [0.3, 0.0, 0.7]
        self.assertTrue(self.send(COMPANION, "inventory.store", {"to": "chest"})["ok"])
        self.host.rules.inventory["chest"]["entries"] = 1
        self.gubble_holds("obj:doorstop")
        self.assertEqual(refusal(self.send(COMPANION, "inventory.store", {"to": "chest"})), ("occupied", "$.args", "The chest is full: one thing."))

    def test_the_players_inventory_is_the_kernels_and_the_readers_say_so(self):
        for op, args in (("inventory.store", {"to": "chest"}), ("inventory.take", {"item": "apple", "to": "pockets"}),
                         ("inventory.use", {"item": "apple"}), ("inventory.drop", {"item": "obj:book"})):
            with self.subTest(op=op):
                self.assertEqual(refusal(self.send(PLAYER, op, args)), ("unsupported_capability", "$.op", mock_host.MECHANICS_NOT_YET))
        listed = self.ask(COMPANION, "capabilities.list", {})["data"]
        self.assertEqual(listed["inventory"], {"gubble_chest": False, "ops": [
            {"op": "inventory.store", "player_only": False}, {"op": "inventory.take", "player_only": False},
            {"op": "inventory.use", "player_only": True}, {"op": "inventory.drop", "player_only": True}]})
        self.assertNotIn("inventory", self.ask(COMPANION, "capabilities.list", {"category": "light"})["data"], "a filtered page repeats nothing")
        self.assertNotIn("inventory", self.ask(COMPANION, "capabilities.list", {"cursor": "1"})["data"], "nor a later one")

    def test_a_pack_without_the_block_has_no_pockets_and_no_chest(self):
        host = new_host(rules_file=mock_host.rules_path(rules_version=4))
        self.assertIsNone(host.rules.inventory)
        host.set_gubble_chest(True)
        self.assertEqual(refusal(host.handle(COMPANION, command("inventory.store", {"to": "chest"}, "inv-v4"))),
                         ("unsupported_capability", "$.op", mock_inventory.INVENTORY_NOT_HERE))
        status = host.handle(COMPANION, query("player.status", {}, "q-v4"))["data"]
        self.assertFalse({"pockets", "chest"} & set(status))


@unittest.skipUnless(INVENTORY.is_file(), "the kernel host is not in this checkout")
class TheKernelsWords(unittest.TestCase):
    def test_the_mocks_words_and_numbers_are_the_kernels(self):
        source = INVENTORY.read_text(encoding="utf-8")

        def words(name: str) -> str:
            match = re.search(rf'\bpublic const string {name} = "((?:[^"\\]|\\.)*)";', source)
            self.assertIsNotNone(match, name)
            return match.group(1).replace('\\"', '"').replace("\\\\", "\\")

        for mock, kernel in (("INVENTORY_NOT_HERE", "InventoryNotHere"), ("POCKETS_ARE_PLAYERS", "PocketsArePlayers"), ("GUBBLE_CHEST_OFF", "GubbleChestOff"),
                             ("CHEST_AT_CAMP", "ChestAtCamp"), ("HOLD_SOMETHING", "HoldSomething"), ("NOT_WHAT_IS_HELD", "NotWhatIsHeld"),
                             ("NOT_IN_CHEST", "NotInChest"), ("STACK_TO_HAND", "StackToHand"), ("A_THING_IS_ONE", "AThingIsOne")):
            with self.subTest(words=mock):
                self.assertEqual(getattr(mock_inventory, mock), words(kernel))
        self.assertIn(f'throw new Refusal("target_busy", "{mock_inventory.HANDS_FULL}", "$.args.to")', source)
        # Decision 120: the chest's words for its slots are the kernel's.
        self.assertIn('public static string ChestFullWords(InventoryCaps caps) => $"The chest is full: {InventoryWords.Things(caps.Entries)}.";', source)
        self.assertEqual((mock_inventory.chest_full_words(20), mock_inventory.chest_full_words(1)), ("The chest is full: twenty things.", "The chest is full: one thing."))
        teams = (REPO / "game" / "scripts" / "native" / "Saves" / "CommandHostTeams.cs").read_text(encoding="utf-8")
        self.assertEqual(float(re.search(r"public const float CampRadiusM = ([0-9.]+)f;", teams).group(1)), mock_inventory.CAMP_RADIUS_M)
        # The companion's checks come first in each command, the pockets before the switch, both before the rules and any lookup.
        for method in ("private JsonObject InventoryStore(", "private JsonObject InventoryTake("):
            with self.subTest(method=method):
                body = source[source.index(method):]
                self.assertLess(body.index("CompanionChestOnly("), body.index("RequireInventory(principal)"))
                self.assertLess(body.index("RequireInventory(principal)"), body.index("Settle()"))
        # P-Inventory-2: a take from the pockets or the ground names the pockets in from, as the mock's does.
        self.assertIn('CompanionChestOnly(to == "pockets" ? "$.args.to" : from is "pockets" or FromTheGround ? "$.args.from" : null);', source)
        only = source[source.index("private void CompanionChestOnly("):]
        self.assertLess(only.index("PocketsArePlayers"), only.index("GubbleChestOff"))

    def test_the_same_ops_are_durable_on_both_hosts(self):
        """A store, a take and a drop are durable room changes on the kernel (decided 25 October); a use stays transient: the mock agrees."""
        host = (KERNEL / "CommandHost.cs").read_text(encoding="utf-8")
        transient = set(re.findall(r'"([a-z_.]+)"', re.search(r"TransientOps = new\(\) \{([^}]*)\};", host).group(1)))
        inventory = set(mock_inventory.INVENTORY_OPS)
        self.assertEqual(transient & inventory, {"inventory.use"})
        self.assertEqual(mock_host.TRANSIENT_OPS & inventory, transient & inventory)
