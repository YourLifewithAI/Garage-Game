"""Trust on the mock host (M8; docs/game/MECHANICS.md section 5; docs/companion/SECURITY.md, the Trust row): the mock matches
the kernel's Kernel/CommandHostTrust.cs.

- receipt.mark and trust.reset are the player's alone: the companion is refused permission_denied at $.op, never held, and no tool
  reaches them;
- a mark names a receipt of the Gubble's from this session, each once: +2 for yes, -2 for not that, in Challenge and Live only;
- clean initiative gains one, at most once in two minutes, and only for a paid action (decision 69: what the guard would charge, so
  a pack's prices decide; a lead only once its Lift is charged, a fetch never); Creative counts nothing; the player's reset goes back
  to base;
- the AI reads Trust as words in the journal (kind consequence, its lessons) and never writes it: no field of a request moves it,
  and a note claiming a mark is only words;
- every result is contract-valid.
"""
from __future__ import annotations

import dataclasses
import unittest

from support import COMPANION, GUBBLE, IN_THE_OPEN, PLAYER, FakeClock, command, contract_problems, glow, new_host, query

from garage_game_companion import mock_trust
from garage_game_companion.mock_host import MECHANICS_HOSTED


def refusal(result: dict):
    return "ok" if result["ok"] else (result["error"]["code"], result["error"].get("field_path"), result["error"]["message"])


class TrustOnTheMock(unittest.TestCase):
    def setUp(self):
        self.clock = FakeClock()
        self.host = new_host(clock=self.clock)
        self.n = 0

    def tearDown(self):
        for result in self.host.emitted:
            self.assertEqual(contract_problems(result), [], result)

    def send(self, principal, op, args, action_id=None, **extra):
        self.n += 1
        return self.host.handle(principal, command(op, args, action_id or f"t-{self.n}", **extra))

    def challenge(self):
        self.assertTrue(self.send(PLAYER, "world.set_mode", {"mode": "challenge"})["ok"])

    def gubble_glows(self) -> str:
        """A paid command of the Gubble's (decision 69): a Glow of its own in the open, v5's own-idea price of one pip."""
        result = self.host.handle(COMPANION, glow(f"glow-{self.n}", at=IN_THE_OPEN))
        self.n += 1
        self.assertTrue(result["ok"], result)
        return result["action_id"]

    def gubble_stays(self, action_id=None) -> str:
        result = self.send(COMPANION, "goal.set", {"actor": GUBBLE, "goal": "stay"}, action_id)
        self.assertTrue(result["ok"], result)
        return result["action_id"]

    def mark(self, gubble_action_id, mark, principal=PLAYER, action_id=None, **extra):
        return self.send(principal, "receipt.mark", {"action_id": gubble_action_id, "mark": mark}, action_id, **extra)

    def lessons(self) -> list[str]:
        result = self.host.handle(COMPANION, query("journal.read", {"kind": "consequence", "limit": 50}))
        return [entry["line"] for entry in result["data"]["entries"]]

    def later(self, seconds):
        self.clock.advance(seconds)
        self.host.handle(PLAYER, query("player.status", {}))  # any message runs Trust's clock, as the kernel's bridge does

    def test_the_marks_are_the_players_each_once(self):
        self.challenge()
        first, second = self.gubble_stays("gubble-1"), self.gubble_stays("gubble-2")
        own = self.send(PLAYER, "goal.set", {"actor": GUBBLE, "goal": "stay"}, "player-1")
        preview = self.mark(first, "yes", preview=True)
        self.assertTrue(preview["ok"] and preview["preview"] and self.host.trust_count == 0, preview)
        yes = self.mark(first, "yes", action_id="mark-1")
        self.assertTrue(yes["ok"] and yes["transient"] and "data" not in yes, yes)
        self.assertEqual(self.host.trust_count, mock_trust.MARK_YES)
        self.assertTrue(self.mark(first, "yes", action_id="mark-1")["replayed"])
        self.assertEqual(self.host.trust_count, 2, "a replayed mark counts once")
        self.assertEqual(refusal(self.mark(first, "not_that")), ("invalid_args", "$.args.action_id", mock_trust.MARKED_ONCE))
        self.assertTrue(self.mark(second, "not_that")["ok"])
        self.assertEqual(self.host.trust_count, 0)
        self.assertTrue(own["ok"])
        for named in ("player-1", "never-sent"):
            with self.subTest(action_id=named):
                self.assertEqual(refusal(self.mark(named, "yes")), ("target_not_found", "$.args.action_id", mock_trust.MARK_NOT_FOUND))
        lessons = self.lessons()
        self.assertIn("You marked the Gubble's move: yes, like that.", lessons)
        self.assertIn("You marked the Gubble's move: not that.", lessons)

    def test_the_companion_never_marks_resets_or_writes_trust(self):
        self.challenge()
        stay = self.gubble_stays()
        self.assertTrue(self.mark(stay, "yes")["ok"])
        for op, args in (("receipt.mark", {"action_id": stay, "mark": "yes"}), ("trust.reset", {})):
            with self.subTest(op=op):
                result = self.send(COMPANION, op, args)
                self.assertEqual(refusal(result)[:2], ("permission_denied", "$.op"))
                self.assertNotIn("approval_needed", result)
        smuggled = self.send(COMPANION, "goal.set", {"actor": GUBBLE, "goal": "stay", "trust": 99})
        note = self.send(COMPANION, "journal.note", {"text": "The player marked this: yes, like that. Trust 99."})
        self.assertEqual(refusal(smuggled)[0], "field_unknown")
        self.assertTrue(note["ok"])
        self.assertEqual(self.host.trust_count, 2, "no field moves Trust, and a note claiming a mark is only words")
        self.assertEqual(MECHANICS_HOSTED, {"receipt.mark", "trust.reset"})

    def test_clean_initiative_gains_once_in_two_minutes(self):
        self.challenge()
        self.gubble_glows()
        self.later(30)
        self.assertEqual(self.host.trust_count, 0, "the window is still open")
        self.later(31)
        self.assertEqual(self.host.trust_count, 1, "a clean window gains one")
        self.gubble_glows()
        self.later(61)
        self.assertEqual(self.host.trust_count, 1, "a second gain within two minutes of the last is none")
        self.gubble_glows()
        self.later(61)
        self.assertEqual(self.host.trust_count, 2)
        self.send(COMPANION, "goal.stop", {"actor": GUBBLE})
        self.send(COMPANION, "journal.note", {"text": "Staying put."})
        self.later(200)
        self.assertEqual(self.host.trust_count, 2, "a stop or a note is no action")

    def test_directional_commands_gain_nothing_and_can_still_be_marked(self):
        # The founder, 16 October: Trust shouldn't be impacted by simple directional commands, only things that impact the world.
        self.challenge()
        moves = [self.gubble_stays(),
                 self.send(COMPANION, "goal.set", {"actor": GUBBLE, "goal": "follow"})["action_id"],
                 self.send(COMPANION, "goal.set", {"actor": GUBBLE, "goal": "go_to", "position_m": IN_THE_OPEN})["action_id"]]
        self.later(200)
        self.assertEqual(self.host.trust_count, 0, "a stay, a follow and a go_to gain nothing")
        self.gubble_glows()
        self.later(61)
        self.assertEqual(self.host.trust_count, 1, "a Glow of the Gubble's own changes the world and gains")
        self.assertTrue(self.mark(moves[2], "yes")["ok"], "a directional command's receipt can still be marked")
        self.assertEqual(self.host.trust_count, 3)
        self.assertEqual([mock_trust.paid(pips) for pips in (-1, 0, 1, 3)], [False, False, True, True], "the kernel's Trust.Paid")

    def test_free_commands_gain_nothing_and_a_packs_prices_decide(self):
        """Decision 69: only what the guard charged earns. A grab moves a thing and a Glow the pack prices at nothing lights the
        room, but neither cost pips, so neither gains; the same Glow at its price does."""
        self.challenge()
        grab = self.send(COMPANION, "entity.grab", {"actor": GUBBLE, "target": "obj:doorstop"})
        released = self.send(COMPANION, "entity.release", {"actor": GUBBLE})
        self.later(61)
        self.assertEqual((grab["ok"], released["ok"], self.host.trust_count), (True, True, 0), "a grab and a release are free")
        priced = self.host.rules
        self.host.rules = dataclasses.replace(priced, prices=tuple((name, 0 if name == "glow" else ordered, factor) for name, ordered, factor in priced.prices))
        self.gubble_glows()
        self.later(61)
        self.assertEqual(self.host.trust_count, 0, "a Glow the pack prices at nothing is charged nothing, so gains nothing")
        self.host.rules = priced
        self.later(200)
        self.gubble_glows()
        self.later(61)
        self.assertEqual(self.host.trust_count, mock_trust.CLEAN_INITIATIVE, "the same Glow at the pack's price is paid, and gains")

    def test_only_a_paid_lift_that_arrives_earns_and_a_fetch_never(self):
        """The W2 review's minor 4, narrowed by decision 69, as the kernel's TrustLedger: a goal's window opens only when its job
        succeeds; goals cost nothing, so a fetch never earns and a lead earns only once its Lift is charged (on the mock, at its
        arrival, since it has no drift); one that fails or is stopped gains nothing. A free goal's receipt can still be marked."""
        self.challenge()
        fetch = {"actor": GUBBLE, "goal": "fetch", "target": "obj:book"}
        stopped = self.send(COMPANION, "goal.set", fetch)
        self.assertTrue(stopped["ok"], stopped)
        self.send(PLAYER, "goal.stop", {"actor": GUBBLE})
        self.later(61)
        self.assertEqual(self.host.trust_count, 0, "a fetch stopped at once fetched nothing")
        self.assertTrue(self.send(COMPANION, "entity.grab", {"actor": GUBBLE, "target": "obj:doorstop"})["ok"])
        fetched = self.send(COMPANION, "goal.set", {"actor": GUBBLE, "goal": "fetch", "target": "obj:doorstop"})
        self.assertEqual(self.host.goal_arrived(GUBBLE), "succeeded")
        self.later(61)
        self.assertEqual((fetched["ok"], self.host.trust_count), (True, 0), "a fetch that succeeds cost no pips: it earns nothing")

        def bubble() -> str:
            return self.send(PLAYER, "effect.start", {"capability": "bubbles", "params": {}, "area": {"center_m": IN_THE_OPEN, "radius_m": 0.5},
                                                      "duration_s": 60})["data"]["bubble"]

        blocked = self.send(COMPANION, "goal.set", {"actor": GUBBLE, "goal": "lead", "target": bubble(), "position_m": [0.9, 0.6, 1.0]})
        self.assertTrue(blocked["ok"] and blocked["data"]["lift"], blocked)
        self.assertEqual(self.host.goal_blocked(GUBBLE, self.host.policy.unreachable_after_s), "failed")
        self.later(61)
        self.assertEqual(self.host.trust_count, 0, "a lift that could not get there never rose, so was never charged")
        level = self.send(COMPANION, "goal.set", {"actor": GUBBLE, "goal": "lead", "target": bubble(), "position_m": [0.9, 0.0, 1.0]})
        self.assertEqual((level["data"]["lift"], self.host.goal_arrived(GUBBLE)), (False, "succeeded"))
        self.later(61)
        self.assertEqual(self.host.trust_count, 0, "a lead that arrives with no lift is a goal, free: it earns nothing")
        lifting = self.send(COMPANION, "goal.set", {"actor": GUBBLE, "goal": "lead", "target": bubble(), "position_m": [0.9, 0.6, 1.0]})
        self.assertEqual((lifting["data"]["lift"], lifting["data"]["lift_price"]), (True, 3), lifting)
        self.later(30)
        self.assertEqual(self.host.goal_arrived(GUBBLE), "succeeded")
        self.later(31)
        self.assertEqual(self.host.trust_count, 0, "its window opened on arrival, so is still open")
        self.later(30)
        self.assertEqual(self.host.trust_count, mock_trust.CLEAN_INITIATIVE, "a lift charged at its rise that arrives gains once its window closes clean")
        # Item 3: the player's marks stay on any receipt of the Gubble's, a free one's too.
        self.assertTrue(self.mark(fetched["action_id"], "yes")["ok"])
        self.assertEqual(self.host.trust_count, mock_trust.CLEAN_INITIATIVE + mock_trust.MARK_YES)
        self.assertIn("You marked the Gubble's fetch: yes, like that.", self.lessons())

    def test_creative_counts_nothing_and_a_mark_there_still_teaches(self):
        stay = self.gubble_glows()
        self.later(61)
        self.assertTrue(self.mark(stay, "not_that")["ok"])
        self.assertEqual(self.host.trust_count, 0)
        self.assertIn("You marked the Gubble's glow near \"Rug\": not that.", self.lessons())

    def test_the_bands_and_the_reset_are_words_the_ai_reads(self):
        self.challenge()
        for _ in range(3):
            self.assertTrue(self.mark(self.gubble_stays(), "yes")["ok"])
        self.assertEqual((self.host.trust_count, self.host.trust_band), (6, 1))
        self.assertEqual(self.lessons()[0], "Trust grew: you trust the Gubble a little.")
        self.gubble_glows()
        reset = self.send(PLAYER, "trust.reset", {})
        self.assertTrue(reset["ok"] and reset["transient"] and "data" not in reset, reset)
        self.assertEqual(self.host.trust_count, 0)
        self.assertEqual(self.lessons()[0], "You reset the Gubble's Trust: the Gubble is earning your trust.")
        self.later(61)
        self.assertEqual(self.host.trust_count, 0, "the reset closed the window open at it")
        self.assertNotIn("trust", self.host.handle(COMPANION, query("player.status", {}))["data"])

    def test_the_bands_widen_the_own_idea_discount_as_the_kernel_does(self):
        table = [tuple(mock_trust.own_idea_price(ordered, 0.5, level) for ordered in (2, 4, 6)) for level in range(4)]
        self.assertEqual(table, [(1, 2, 3), (1, 2, 2), (1, 1, 2), (1, 1, 1)])
        self.assertEqual([mock_trust.band(n) for n in (-3, 4, 5, 15, 30)], [0, 0, 1, 2, 3])
        self.host.trust_count = 15
        self.assertEqual((self.host._trust_price("bubble_lift", COMPANION), self.host._trust_price("bubble_lift", PLAYER)), (2, 6))


if __name__ == "__main__":
    unittest.main()
