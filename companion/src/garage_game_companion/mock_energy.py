"""Connection energy for the mock host: the kernel's Kernel/CommandHostEnergy.cs (docs/game/MECHANICS.md section 5; contracts/README.md
"Price by principal"), minus the body.

- One pool per mode (the kernel keeps it with the player's vitals, the team's), in whole pips: a new pool is full, its maximum the
  pack's start plus one pip per expertise_per_pip expertise the team holds, never past the ceiling. Without traits.pips there is no
  pool and nothing is charged.
- The price follows the principal alone, never a field: the player's commands pay the ordered price, every command on the Gubble's
  connection the own-idea price, with Trust's band discount (mock_trust.py's _trust_price). The mock runs no wish, so nothing pays
  the ordered price for the Gubble.
- The guard (_guard_energy) runs where the kernel's GuardEnergy runs, after every other check of a command that would commit: a
  preview, a stop, a replay and a refused command cost nothing, and a held command pays its own principal's price when the player's
  click commits it. An empty pool refuses with energy_exhausted, carrying the balance and the price, in the kernel's words; in
  Creative the shadow pool drains and never refuses. The charge (_charge_energy) lands once the command has committed, never below
  empty, and Trust reads it (decision 69).
- The refill, on the host's clock: one pip every refill_near_every_s while the Gubble is within refill_near_within_m of the player,
  one every refill_far_every_s apart (the mock plays no difficulty, so its refill multiplier is 1). The camp's half pool and the time
  away, which need the body and the save, are the kernel's alone.

test_kernel_alignment.py holds the words and the rule equal to the kernel's.
"""
from __future__ import annotations

import math

from .refusals import HostError

ENERGY_EXHAUSTED_MESSAGE = "The Gubble needs a moment."  # kernel: CommandHost.EnergyExhaustedMessage


class EnergyMixin:
    """Mixed into MockHost. The pools are the team's, one per mode, so a room reload keeps them (as the kernel's vitals store does)."""

    def _reset_energy(self) -> None:
        self._pools: dict[str, int] = {}  # mode -> pips; absent: full (the kernel's unset pool)
        self._pip_progress = 0.0
        self._pool_clock = self.clock.now()

    # ------------------------------------------------------------------ the pool

    def _expertise_count(self) -> int:
        """Every expertise the team holds, as the kernel's GlowProgress.ExpertiseCount sums them for the pips: Glow's discoveries
        and nature's (Fire's and Grow's, mock_nature.py)."""
        return len(self.glow_progress.discoveries) + sum(len(found) for found in self.nature_expertise.values())

    def _max_pips(self) -> int:
        """The pool's maximum (the kernel's VitalsRules.MaxPips): 0 without the pack's traits.pips."""
        rules = self.rules
        if rules is None or rules.pips is None:
            return 0
        start, per_pip, ceiling = rules.pips
        return min(ceiling, start + max(0, self._expertise_count()) // per_pip)

    def _pool_pips(self) -> int:
        """The pips in this mode's pool now: a pool never charged is full; one charged is kept within the maximum."""
        most = self._max_pips()
        held = self._pools.get(self.mode)
        return most if held is None else max(0, min(held, most))

    def _refill_pips(self) -> None:
        """The kernel's RefillPips over the time since the last refill: faster while the Gubble is near the player."""
        now = self.clock.now()
        elapsed, self._pool_clock = max(0.0, now - self._pool_clock), now
        rules = self.rules
        if rules is None or rules.pips is None or rules.refill is None or elapsed <= 0:
            return
        most = self._max_pips()
        pips = self._pool_pips()
        if pips >= most:
            self._pip_progress = 0.0
            return
        near_every_s, near_within_m, far_every_s = rules.refill
        player = self.entities.get("avatar:player")
        gubble = self.entities.get("avatar:companion")
        distance = math.dist(player.position, gubble.position) if player is not None and gubble is not None else math.inf
        self._pip_progress += elapsed / (near_every_s if distance <= near_within_m else far_every_s)
        if self._pip_progress < 1:
            return
        gained = int(math.floor(self._pip_progress))
        self._pip_progress -= gained
        pips = min(most, pips + gained)
        if pips >= most:
            self._pip_progress = 0.0
        self._pools[self.mode] = pips

    # ------------------------------------------------------------------ the guard and the charge

    def _guard_energy(self, capability: str, principal: str) -> int:
        """The kernel's GuardEnergy: the price this principal pays for this ability (its price key: a Fire kind's or a bubble's own),
        refused with energy_exhausted when a real pool cannot pay it. The shadow pool (Creative) never refuses. Changes nothing."""
        price = self._charge_price(capability, principal)
        if price <= 0 or self.mode == "creative":
            return price
        balance = self._pool_pips()
        if balance < price:
            raise HostError("energy_exhausted", ENERGY_EXHAUSTED_MESSAGE, field_path="$.args.capability", retryable=True,
                            balance=balance, price=price)
        return price

    def _charge_energy(self, price: int) -> None:
        """The kernel's ChargeEnergy, once the command committed: Trust reads the charge (decision 69), and the pool (the shadow pool
        in Creative) loses the price, never below empty."""
        self._trust_charged += max(0, price)
        if price <= 0 or self.rules is None or self.rules.pips is None:
            return
        self._pools[self.mode] = max(0, self._pool_pips() - price)
