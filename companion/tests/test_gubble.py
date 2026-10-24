"""The floating companion's observable identity and mean hover on the mock host.

The mock accepts body positions from the game-side fixtures; it has no movement,
collision or bob simulation. Its spawn and fetch-return placements include hover.
"""
from __future__ import annotations

import copy
import unittest
from unittest.mock import patch

from support import COMPANION, contract_problems, contracts, new_host, query

from garage_game_companion.mock_host import HOVER_M


class Gubble(unittest.TestCase):
    def setUp(self):
        self.host = new_host()

    def tearDown(self):
        for result in self.host.emitted:
            self.assertEqual(contract_problems(result), [], result)

    def inspect(self, target="avatar:companion"):
        result = self.host.handle(COMPANION, query("entity.inspect", {"target": target}))
        self.assertTrue(result["ok"], result)
        return result["data"]["entity"]

    def test_the_gubble_spawns_above_the_room_spawn_and_keeps_the_players_height(self):
        spawns = {s["role"]: s["position_m"] for s in self.host.room["spawns"]}
        companion = self.inspect()
        self.assertEqual(companion["display_name"], "the Gubble")
        self.assertEqual(companion["position_m"],
                         [spawns["companion"][0], spawns["companion"][1] + HOVER_M, spawns["companion"][2]])
        self.assertEqual(self.inspect("avatar:player")["position_m"], spawns["player"])

    def test_an_elevated_room_spawn_and_other_companions_also_include_hover(self):
        validation = contracts().validate
        load = validation.load_strict

        def elevated_room(path):
            document = load(path)
            if path.name == "room.json":
                document = copy.deepcopy(document)
                for spawn in document["spawns"]:
                    if spawn["role"] == "companion":
                        spawn["position_m"][1] = 0.30
            return document

        with patch.object(validation, "load_strict", side_effect=elevated_room):
            host = new_host(extra_companions={"companion:other": "avatar:other"})
        for avatar in ("avatar:companion", "avatar:other"):
            self.assertAlmostEqual(host.entities[avatar].position[1], 0.32, places=6)
        self.assertEqual(host.entities["avatar:other"].display_name, "Other")

    def test_a_legacy_default_name_becomes_the_gubble_and_custom_names_stay(self):
        self.host.rename_entity("avatar:companion", "Wisp")
        self.assertEqual(self.inspect()["display_name"], "the Gubble")
        self.host.rename_entity("avatar:companion", "Pebble")
        self.assertEqual(self.inspect()["display_name"], "Pebble")
        self.host.rename_entity("obj:book", "Wisp")
        self.assertEqual(self.inspect("obj:book")["display_name"], "Wisp")

    def test_the_mock_reports_a_mean_hover_without_a_simulated_bob(self):
        position = self.inspect()["position_m"]
        self.host.clock.advance(2.6 / 4)
        self.assertEqual(self.inspect()["position_m"], position)


if __name__ == "__main__":
    unittest.main()
