"""Who holds the companion link when several games share the player's user folder (A2 security review, findings 1
and 3, 8 October 2026).

The link belongs to whichever game holds the account's ownership lock (`companion/session.lock` beside the session
file), taken before the session file is checked or written and held for the link's whole life; the operating system
frees it when that game ends, by a crash too. A session file says where to connect; it never decides who owns the
link. So:
- a second game never takes the link while the first holds it, even when the first's session file has gone;
- two games started together end with exactly one link;
- a game that crashed leaves a stale file, which the next game simply replaces;
- a stale file naming some other live process blocks nothing;
- the mock game (Python) and the real game (C#) honour the same lock.
"""
from __future__ import annotations

import asyncio
import json
import os
import shutil
import tempfile
import threading
import unittest
from pathlib import Path

from support import query

from garage_game_companion.link import LinkClient, SessionLock, parse_session
from garage_game_companion.real_game import GameNotAvailable, RealGame, session_file_under, toolchain

try:
    toolchain()
    WHY_NOT = ""
except GameNotAvailable as error:
    WHY_NOT = str(error)


@unittest.skipIf(WHY_NOT, f"the real game cannot run here: {WHY_NOT}")
class Ownership(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="garage-game-shared-user-"))
        self.games: list[RealGame] = []
        self.session_path = session_file_under(self.root)

    def tearDown(self):
        for game in reversed(self.games):
            game.stop()
        shutil.rmtree(self.root, ignore_errors=True)
        for game in self.games:
            self.assertEqual(game.errors(), [], "a game wrote to stderr")

    def game(self, **options) -> RealGame:
        game = RealGame(user_root=self.root, **options)
        self.games.append(game)
        game.start()
        return game

    @staticmethod
    def describe(info) -> dict:
        async def ask():
            client = LinkClient(lambda: info)
            try:
                return await client.request(query("room.describe", {}))
            finally:
                await client.close()
        return asyncio.run(ask())

    def test_a_second_game_never_takes_the_link_while_the_first_holds_it(self):
        first = self.game()
        info = parse_session(self.session_path.read_bytes())
        # The first game's session file goes (by hand, by a tool, or by another game's delete): that must not
        # hand the link to the next game.
        self.session_path.unlink()
        second = self.game(expect_link=False)
        self.assertEqual(second.link, "off")
        self.assertFalse(self.session_path.exists(), "the second game published a session file")
        self.assertTrue(self.describe(info)["ok"])  # the first game's link still serves its companion
        self.assertTrue(any("holds the companion link" in line for line in second.lines), second.lines[-10:])

    def test_two_games_started_together_end_with_one_link(self):
        games = [RealGame(user_root=self.root, expect_link=False) for _ in range(2)]
        self.games += games
        threads = [threading.Thread(target=game.start) for game in games]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(120)
        self.assertEqual(sorted(game.link for game in games), ["off", "on"])
        owner = next(game for game in games if game.link == "on")
        port = next(line for line in owner.lines if "COMPANION_LINK listening" in line).rsplit("port=", 1)[1]
        self.assertEqual(parse_session(self.session_path.read_bytes()).port, int(port))

    def test_the_link_is_free_again_when_its_game_ends_even_by_a_crash(self):
        first = self.game()
        crashed_port = parse_session(self.session_path.read_bytes()).port
        first.kill()
        self.assertTrue(self.session_path.exists())  # a crash cleans nothing up
        second = self.game()
        self.assertEqual(second.link, "on")
        self.assertNotEqual(parse_session(self.session_path.read_bytes()).port, crashed_port)

    def test_a_stale_session_file_naming_a_live_process_blocks_nothing(self):
        # Review finding 3: a file naming any running process (this test's own), with no creation time, used to
        # make the game run without the link.
        self.session_path.parent.mkdir(parents=True)
        for document in ({"schema": "garage_game.companion_session", "version": 1, "pid": os.getpid()},
                         {"pid": os.getpid(), "created_utc": "2000-01-01T00:00:00Z", "token": "not a token"}):
            with self.subTest(document=document):
                self.session_path.write_text(json.dumps(document), encoding="utf-8")
                game = self.game()
                self.assertEqual(game.link, "on")
                self.assertEqual(json.loads(self.session_path.read_bytes())["pid"] != os.getpid(), True)
                game.stop()
                self.assertFalse(self.session_path.exists())

    def test_the_mock_game_and_the_real_game_honour_the_same_lock(self):
        game = self.game()
        mock = SessionLock(self.session_path)
        self.assertFalse(mock.acquire(), "the mock game took the lock the real game holds")
        game.stop()
        self.assertTrue(mock.acquire())
        try:
            blocked = self.game(expect_link=False)
            self.assertEqual(blocked.link, "off")
        finally:
            mock.release()
        self.assertTrue(self.game().link == "on")


class SessionLocks(unittest.TestCase):
    """The Python side of the lock, which the mock game takes before it writes the session file."""

    def test_one_holder_at_a_time_and_free_once_released(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "companion" / "session.json"
            first, second = SessionLock(path), SessionLock(path)
            self.assertTrue(first.acquire())
            self.assertFalse(second.acquire())
            self.assertEqual(first.path.name, "session.lock")
            first.release()
            self.assertTrue(second.acquire())
            second.release()
            first.release()  # releasing twice is harmless


if __name__ == "__main__":
    unittest.main()
