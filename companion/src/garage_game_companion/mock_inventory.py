"""The pockets and the chest on the mock host (P-Inventory, 25 October; the kernel's Kernel/CommandHostInventory.cs; docs/game/MECHANICS.md
decisions 98 to 103): small, for the companion's boundary.

- The companion's refusals are the kernel's, in its order and words, each before anything is looked up: the pockets in `to` or `from`
  are the player's alone (permission_denied at that field), and with the player's team setting "Let the Gubble use the chest" off
  (`gubble_chest`, off by default; only the player's control, `set_gubble_chest`, changes it, never a request) every companion store or
  take is permission_denied at $.op. inventory.use and inventory.drop are player-only ops, refused before any handler runs.
- With the switch on, the companion's one path: what its own avatar holds into the chest, at a camp (within the kernel's CampRadiusM of
  the player's spawn while the starting campfire burns, or of a lit campfire), and a thing back into its own hands. A thing in the chest
  is gone from the room (removed) and keeps its id; the chest's cap is the pack's count of slots (decision 120: counts, not weights),
  occupied past it, in the kernel's words ("The chest is full: twenty things."). The box
  world names no asset of a kind, so every held thing is a unique thing here, as under v5 in the kernel.
- The player's inventory needs the body and the team file: the mock answers it not available yet (the kernel hosts it).
- A store or a take is a durable room change with data, one room revision, as the kernel's (decided 25 October: the chest and a
  thing held away are the island's room state); the thing's entity revision is that revision. player.status carries the pockets
  (empty here) and the chest, capabilities.list the four ops on the whole list's first answer.

test_kernel_alignment.py holds these words and numbers to the kernel's.
"""
from __future__ import annotations

import math

from .refusals import HostError

INVENTORY_OPS = ("inventory.store", "inventory.take", "inventory.use", "inventory.drop")
INVENTORY_NOT_HERE = "This island has no pockets or chest."  # kernel: CommandHost.InventoryNotHere
POCKETS_ARE_PLAYERS = "The pockets are the player's alone."  # kernel: CommandHost.PocketsArePlayers
GUBBLE_CHEST_OFF = "The player has not let the Gubble use the chest."  # kernel: CommandHost.GubbleChestOff
CHEST_AT_CAMP = "The chest is at the camp: store and take things by a lit campfire."  # kernel: CommandHost.ChestAtCamp
HOLD_SOMETHING = "Hold something to put it away: pick it up first."  # kernel: CommandHost.HoldSomething
NOT_WHAT_IS_HELD = "That is not what is being held."  # kernel: CommandHost.NotWhatIsHeld
NOT_IN_CHEST = "That is not in the chest."  # kernel: CommandHost.NotInChest
STACK_TO_HAND = "Food and seeds come out of the chest into your pockets."  # kernel: CommandHost.StackToHand
A_THING_IS_ONE = "A thing is one: name no count for it."  # kernel: CommandHost.AThingIsOne
HANDS_FULL = "This avatar is already holding something. Put it down first."
PLAYER_ONLY = "Only the player can do that, directly in the game."
CAMP_RADIUS_M = 0.5  # kernel: CommandHost.CampRadiusM
DEFAULT_GUBBLE_CHEST = False  # decision 100: off unless the player turns it on


_NUMBER_WORDS = ("no", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen",
                 "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty")


def number_words(count: int) -> str:
    """The kernel's InventoryWords.Number: "ten" up to twenty, digits above."""
    return _NUMBER_WORDS[count] if 0 <= count < len(_NUMBER_WORDS) else str(count)


def chest_full_words(slots: int) -> str:
    """The kernel's ChestFullWords (decision 120), for twenty slots: The chest is full: twenty things."""
    return f"The chest is full: {number_words(slots)} thing{'' if slots == 1 else 's'}."


def _kilograms(value: float) -> float:
    return round(value, 3)


class InventoryMixin:
    """The chest and the Gubble's chest switch for MockHost (its rules, entities, holding and campfires)."""

    def _init_inventory_team(self) -> None:
        # The team's switch (the kernel's team file settings.gubble_chest): a room reload keeps it.
        self.gubble_chest = DEFAULT_GUBBLE_CHEST

    def _reset_inventory(self) -> None:
        # The island's chest (the kernel's island save camp.chest): the ids of the things in it, oldest first.
        self.chest: list[str] = []

    def set_gubble_chest(self, allowed: bool) -> None:
        """The player's own control ("Let the Gubble use the chest"): never reached by a request."""
        with self._lock:
            self.gubble_chest = bool(allowed)

    # ---- the checks, in the kernel's order ----

    def _companion_chest_only(self, pockets_path: str | None) -> None:
        if pockets_path is not None:
            raise HostError("permission_denied", POCKETS_ARE_PLAYERS, field_path=pockets_path)
        if not self.gubble_chest:
            raise HostError("permission_denied", GUBBLE_CHEST_OFF, field_path="$.op")

    def _inventory_rules(self) -> dict:
        block = self.rules.inventory if self.rules is not None else None
        if block is None:
            raise HostError("unsupported_capability", INVENTORY_NOT_HERE, field_path="$.op")
        return block

    def _at_camp(self, position: list[float]) -> bool:
        """The kernel's AtCamp: within CAMP_RADIUS_M of the player's spawn while the starting campfire burns, or of a lit campfire."""
        spawn = next((s for s in self.room["spawns"] if s["role"] == "player"), None)
        if spawn is not None and self._is_lit("camp:start") and math.dist(position, spawn["position_m"]) <= CAMP_RADIUS_M:
            return True
        return any(self._is_lit(camp.id) and math.dist(position, camp.position) <= CAMP_RADIUS_M for camp in self._ensure_camps())


    def _not_yet_for_the_player(self) -> None:
        from .mock_host import MECHANICS_NOT_YET
        raise HostError("unsupported_capability", MECHANICS_NOT_YET, field_path="$.op")

    # ---- the commands ----

    def _op_inventory_store(self, principal, message, apply, new_revision):
        args = message["args"]
        to, source = args["to"], args.get("from", "hand")
        player = principal.startswith("player:")
        if not player:
            self._companion_chest_only("$.args.to" if to == "pockets" else "$.args.from" if source == "pockets" else None)
        caps = self._inventory_rules()["chest"]
        if player:
            self._not_yet_for_the_player()
        actor = self.avatars[principal]
        held_id = self.holding.get(actor)
        held = self.entities.get(held_id) if held_id else None
        if held is None or held.removed:
            raise HostError("request_invalid", HOLD_SOMETHING, field_path="$.args")
        if "item" in args and args["item"] != held_id:
            raise HostError("request_invalid", NOT_WHAT_IS_HELD, field_path="$.args.item")
        if args.get("count", 1) != 1:
            raise HostError("invalid_args", A_THING_IS_ONE, field_path="$.args.count")
        if not self._at_camp(self.entities[actor].position):
            raise HostError("not_ready", CHEST_AT_CAMP, field_path="$.args.to", retryable=True)
        if len(self.chest) + 1 > caps["entries"]:
            raise HostError("occupied", chest_full_words(caps["entries"]), field_path="$.args", allowed=caps["entries"])
        if not apply:
            return {"affected": [held_id]}
        held.held_by = None
        del self.holding[actor]
        held.removed = True
        self._touch(held, new_revision)
        self.chest.append(held_id)
        return {"affected": [held_id], "data": {"item": held_id, "count": 1, "to": "chest"}}

    def _op_inventory_take(self, principal, message, apply, new_revision):
        args = message["args"]
        item, to = args["item"], args["to"]
        player = principal.startswith("player:")
        if not player:
            self._companion_chest_only("$.args.to" if to == "pockets" else None)
        self._inventory_rules()
        if player:
            self._not_yet_for_the_player()
        actor = self.avatars[principal]
        if item not in self.chest:
            raise HostError("target_not_found", NOT_IN_CHEST, field_path="$.args.item")
        if not self._at_camp(self.entities[actor].position):
            raise HostError("not_ready", CHEST_AT_CAMP, field_path="$.args.item", retryable=True)
        if args.get("count", 1) != 1:
            raise HostError("invalid_args", A_THING_IS_ONE, field_path="$.args.count")
        if self.holding.get(actor) is not None:
            raise HostError("target_busy", HANDS_FULL, field_path="$.args.to")
        thing = self.entities[item]
        limit = self.policy.carry_limit_kg.get(actor, 0.0)
        if thing.mass_kg is not None and thing.mass_kg > limit:
            raise HostError("target_too_heavy", f"That is too heavy to carry: {_kilograms(thing.mass_kg):g} kg, and this avatar can manage {_kilograms(limit):g} kg.",
                            field_path="$.args.item", actual=_kilograms(thing.mass_kg), allowed=_kilograms(limit))
        if not apply:
            return {"affected": [item]}
        self.chest.remove(item)
        thing.removed = False
        thing.position = list(self.entities[actor].position)
        self._touch(thing, new_revision)
        self._take_hold(actor, thing)
        return {"affected": [item], "data": {"item": item, "count": 1, "to": "hand"}}

    def _op_inventory_use(self, principal, message, apply, new_revision):
        """Player-only (refused to a companion before this runs): the pockets need the body and the team file."""
        if not principal.startswith("player:"):
            raise HostError("permission_denied", PLAYER_ONLY, field_path="$.op")
        self._inventory_rules()
        self._not_yet_for_the_player()

    _op_inventory_drop = _op_inventory_use
    # P-Inventory-2: the hand's selection is the player's alone (refused to a companion before this runs); the kernel hosts it.
    _op_inventory_select = _op_inventory_use

    # ---- the readers ----

    def _inventory_status(self) -> dict | None:
        """player.status's pockets (empty on the mock) and chest, as the kernel's AddInventoryStatus shows them, or None without the block."""
        block = self.rules.inventory if self.rules is not None else None
        if block is None:
            return None
        entries = [{"item": i, "kind": "thing", "name": self.text.display_text(self.entities[i].display_name, 80), "count": 1,
                    "mass_kg": round(self.entities[i].mass_kg or 0.0, 4)} for i in self.chest if i in self.entities]
        return {
            "pockets": {"entries": [], "max_entries": block["pockets"]["entries"]},
            "chest": {"entries": entries, "max_entries": block["chest"]["entries"]},
        }

    def _inventory_ops(self, args: dict) -> dict | None:
        """capabilities.list's inventory, on the whole list's first answer (no category, no cursor), as the kernel's WithInventoryOps."""
        if self.rules is None or self.rules.inventory is None or "category" in args or "cursor" in args:
            return None
        return {"gubble_chest": self.gubble_chest,
                "ops": [{"op": op, "player_only": op in self.contracts.player_only_ops} for op in INVENTORY_OPS]}
