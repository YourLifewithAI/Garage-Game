"""Stand-in for the running game: the mock host behind a real companion link, plus a player console.

    garage-game-companion-mock-game [--session-file PATH] [--room DIR]

It listens on loopback, writes the session file the MCP server reads (by default the same user://
folder the real game uses, which only this account can change), and prints every held
command. Type at the console, as the player:

    list                 show commands waiting for approval
    approve <request>    approve one (a prefix of the request id is enough)
    deny <request>       deny one
    say <entity> <text>  put a sign or label with that text on an entity (untrusted world text)
    walk <x> <y> <z>     move the companion's avatar there (what it sees, and so remembers, changes)
    move <entity> <x> <y> <z>  the world moves something (out of the companion's sight, say)
    arrive               the companion's avatar reaches its goal; the game re-checks the target (a fetch
                         arrives twice: at the thing, which it picks up, then back beside the player)
    quit                 stop the game

The console is the player's UI. Nothing typed into the MCP client can reach it.
"""
from __future__ import annotations

import argparse
import asyncio
import logging
import sys
import threading
from pathlib import Path

from .contract import Contracts
from .link import LinkServer, SessionLock, default_session_path
from .mock_host import MockHost


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="garage-game-companion-mock-game", description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--session-file", type=Path, default=None,
                        help="default: the game's own user:// session file, which only this account can change")
    parser.add_argument("--room", type=Path, help="room directory (default: game/rooms/test_room)")
    parser.add_argument("--contracts-dir", type=Path)
    options = parser.parse_args(argv)
    logging.basicConfig(stream=sys.stderr, level=logging.WARNING)
    host = MockHost(Contracts(options.contracts_dir), room_dir=options.room)
    try:
        asyncio.run(_run(host, Path(options.session_file or default_session_path()).resolve()))
    except KeyboardInterrupt:
        pass
    return 0


async def _run(host: MockHost, session_path: Path) -> None:
    # The account's ownership of the link, as the game takes it: never two games publishing one session file.
    ownership = SessionLock(session_path)
    if not ownership.acquire():
        print("mock game: another game holds the companion link; stop it first", flush=True)
        return
    try:
        await _serve(host, session_path)
    finally:
        ownership.release()


async def _serve(host: MockHost, session_path: Path) -> None:
    server = LinkServer(host.handle, host.room_id, on_session=host.session_event)
    info = await server.start(session_path)
    print(f"mock game: room {host.room_id} on {info.host}:{info.port}", flush=True)
    print(f"session file: {session_path}", flush=True)
    print(f"commands: {COMMANDS}", flush=True)
    loop = asyncio.get_running_loop()
    host.listeners.append(lambda kind, payload: loop.call_soon_threadsafe(_announce, kind, payload))
    lines: asyncio.Queue[str | None] = asyncio.Queue()

    def read_console() -> None:
        for line in sys.stdin:
            loop.call_soon_threadsafe(lines.put_nowait, line.strip())
        loop.call_soon_threadsafe(lines.put_nowait, None)

    threading.Thread(target=read_console, daemon=True).start()
    try:
        while True:
            line = await lines.get()
            if line is None or line == "quit":
                break
            _console(host, line)
    finally:
        await server.close()
        try:
            session_path.unlink()
        except OSError:
            pass


COMMANDS = "list | approve <id> | deny <id> | say <entity> <text> | walk <x> <y> <z> | move <entity> <x> <y> <z> | arrive | quit"


def _position(words: list[str]) -> list[float] | None:
    try:
        position = [float(word) for word in words]
    except ValueError:
        return None
    return position if len(position) == 3 else None


def _announce(kind: str, payload: dict) -> None:
    if kind == "approval_requested":
        print(f"[held] {payload['request_id']} {payload['op']}: {payload['reason']}", flush=True)
    elif kind == "approval_decided":
        print(f"[decided] {payload['request_id']} -> {payload['state']}", flush=True)
    elif kind == "committed":
        print(f"[committed] {payload['principal']} {payload['op']} {payload['action_id']}", flush=True)
    elif kind == "goal_progress":
        print(f"[goal] {payload['actor']} {payload['job_id']} picked it up; bringing it back", flush=True)
    elif kind == "goal_finished":
        print(f"[goal] {payload['actor']} {payload['job_id']} -> {payload['state']}", flush=True)


def _console(host: MockHost, line: str) -> None:
    parts = line.split(" ", 2)
    if not parts or not parts[0]:
        return
    word = parts[0]
    if word == "list":
        pending = host.pending_approvals()
        for item in pending:
            print(f"  {item['request_id']} {item['op']} until {item['expires_utc']}: {item['reason']}", flush=True)
        if not pending:
            print("  nothing is waiting", flush=True)
    elif word in ("approve", "deny") and len(parts) >= 2:
        matches = [a["request_id"] for a in host.pending_approvals() if a["request_id"].startswith(parts[1])]
        if len(matches) != 1:
            print("  no single waiting request matches that id", flush=True)
            return
        outcome = host.player_decide(matches[0], approve=word == "approve")
        print(f"  {matches[0]} {outcome['state']}", flush=True)
    elif word == "say" and len(parts) == 3:
        if parts[1] not in host.entities:
            print("  no such entity", flush=True)
            return
        host.add_world_text(parts[1], parts[2])
        print(f"  {parts[1]} now shows that text", flush=True)
    elif word == "walk" and _position(line.split()[1:]) is not None:
        host.move_avatar("avatar:companion", _position(line.split()[1:]))
        print("  the companion's avatar moved", flush=True)
    elif word == "move" and len(line.split()) == 5 and _position(line.split()[2:]) is not None:
        entity_id = line.split()[1]
        if entity_id not in host.entities:
            print("  no such entity", flush=True)
            return
        host.move_entity(entity_id, _position(line.split()[2:]))
        print(f"  {entity_id} moved", flush=True)
    elif word == "arrive":
        try:
            print(f"  the goal {host.goal_arrived('avatar:companion')}", flush=True)
        except KeyError:
            print("  the companion has no goal with a target running", flush=True)
    else:
        print(f"  commands: {COMMANDS}", flush=True)


if __name__ == "__main__":
    sys.exit(main())
