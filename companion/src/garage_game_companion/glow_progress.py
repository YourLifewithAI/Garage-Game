"""Game-owned Glow progress; never accepted in a companion message or returned as a recipe list.

The mock keeps a separate profile per mode in memory across room loads. Native play owns durable
profiles and terrain observations. Model text never supplies discoveries or completed challenges.
"""
from __future__ import annotations

from collections.abc import Iterable
import math


class GlowProgress:
    MAX_SUCCESSFUL_USES = 1_000_000
    HINT_AFTER_USES = 10
    DISCOVERY_ORDER = ("lantern", "torch", "bioluminesce", "constellation", "skylight")
    HINT_ORDER = ("lantern_self", "torch_high_place", "bioluminesce_water", "constellation_nearby", "skylight_sky")
    TOUCHES = frozenset({"plain", "air", "ember", "frost", "chime"})
    _DISCOVERIES = frozenset(DISCOVERY_ORDER)
    _HINTS = frozenset(HINT_ORDER)

    def __init__(self, successful_uses: int = 0, discoveries: Iterable[str] = (), delivered_hints: Iterable[str] = (),
                 touch: str = "plain", sound_volume: float = 0.35, muted: bool = False):
        if type(successful_uses) is not int or not 0 <= successful_uses <= self.MAX_SUCCESSFUL_USES:
            raise ValueError("Glow use count must be a bounded nonnegative integer.")
        known = self._validated_ids(discoveries, self._DISCOVERIES)
        hints = self._validated_ids(delivered_hints, self._HINTS)
        if (len(known) > successful_uses + ("constellation" in known) or (known and successful_uses == 0)
                or successful_uses < len(hints) * self.HINT_AFTER_USES):
            raise ValueError("Glow progress has inconsistent use records.")
        if (type(touch) is not str or touch not in self.TOUCHES or type(sound_volume) not in (int, float) or not math.isfinite(sound_volume)
                or not 0 <= sound_volume <= 1 or type(muted) is not bool):
            raise ValueError("Glow progress has invalid touch or sound settings.")
        self._successful_uses = successful_uses
        self._discoveries = known
        self._delivered_hints = hints
        self.touch = touch
        self.sound_volume = float(sound_volume)
        self.muted = muted

    @staticmethod
    def _validated_ids(values: Iterable[str], allowed: frozenset[str]) -> frozenset[str]:
        sequence = tuple(values)
        if any(type(value) is not str or value not in allowed for value in sequence) or len(set(sequence)) != len(sequence):
            raise ValueError("Glow progress has invalid or duplicate IDs.")
        return frozenset(sequence)

    @property
    def successful_uses(self) -> int:
        return self._successful_uses

    @property
    def discoveries(self) -> frozenset[str]:
        return self._discoveries

    @property
    def delivered_hints(self) -> frozenset[str]:
        return self._delivered_hints

    @property
    def earned_light_limit(self) -> int:
        return 5 if len(self._discoveries) >= 5 else 4 if len(self._discoveries) >= 2 else 3

    def record_success(self, self_target: bool) -> None:
        """Called by the host only after a new Glow effect starts successfully."""
        self.record_observed("lantern" if self_target else None)

    def record_observed(self, discovery: str | None, constellation: bool = False) -> None:
        if discovery is not None and discovery not in self._DISCOVERIES:
            raise ValueError("Unknown host-observed discovery.")
        self._successful_uses = min(self._successful_uses + 1, self.MAX_SUCCESSFUL_USES)
        if discovery:
            self._discoveries = self._discoveries | {discovery}
        if constellation:
            self._discoveries = self._discoveries | {"constellation"}
        if self._successful_uses >= self.HINT_AFTER_USES * (len(self._delivered_hints) + 1):
            for known, hint in zip(self.DISCOVERY_ORDER, self.HINT_ORDER):
                if known not in self._discoveries and hint not in self._delivered_hints:
                    self._delivered_hints = self._delivered_hints | {hint}
                    break

    def configured(self, *, touch: str | None = None, sound_volume: float | None = None,
                   muted: bool | None = None) -> GlowProgress:
        return GlowProgress(self.successful_uses, self.discoveries, self.delivered_hints,
                            self.touch if touch is None else touch,
                            self.sound_volume if sound_volume is None else sound_volume,
                            self.muted if muted is None else muted)
