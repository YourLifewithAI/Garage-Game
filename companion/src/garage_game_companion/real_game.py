"""Run the real game host, the kernel's command host in the room, with the companion link on.

    python -m garage_game_companion.real_game [--room ID] [--isolated] [--window]

It starts Godot on `res://scripts/native/Companion/companion_room.tscn` (headless unless --window), waits until the
game has written its companion session file, prints where that file is, and keeps the room running until Ctrl+C.
Point an MCP client at it with the play-only profile (`python -m garage_game_companion.profile`), adding
`--session-file PATH` when the room runs --isolated.

--isolated gives the game a fresh, temporary user folder (its saves and its session file), as the companion suite
does; without it the game uses the player's own user folder, exactly as the game itself would.

The suite uses `RealGame` directly (companion/tests/test_real_host.py). This module only launches the game; the
MCP server never imports it.
"""
from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

from .contract import DEFAULT_REPO_ROOT

SCENE = "res://scripts/native/Companion/companion_room.tscn"
_ENGINE_VERSION = re.compile(r'Sdk="Godot\.NET\.Sdk/([0-9.]+)"')


class GameNotAvailable(RuntimeError):
    """This checkout cannot run the game: no Godot .NET, no .NET runtime, or the C# project is not built."""


def toolchain(repo: Path = DEFAULT_REPO_ROOT) -> tuple[Path, Path]:
    """The pinned Godot .NET executable and the .NET folder for DOTNET_ROOT, as the runners find them."""
    project = repo / "game"
    match = _ENGINE_VERSION.search((project / "GarageGame.csproj").read_text(encoding="utf-8"))
    if match is None:
        raise GameNotAvailable("game/GarageGame.csproj names no Godot .NET version")
    version = match.group(1)
    engine = os.environ.get("GARAGE_GAME_GODOT_DOTNET") or os.environ.get("GARAGE_GAME_GODOT")
    dotnet = os.environ.get("GARAGE_GAME_DOTNET")
    if os.name == "nt":
        if not engine:
            found = sorted((repo / ".cache" / "godot").rglob(f"Godot_v{version}-stable_mono_win64_console.exe")) \
                if (repo / ".cache" / "godot").is_dir() else []
            engine = str(found[0]) if found else None
        dotnet_root = Path(dotnet).parent if dotnet else repo / ".cache" / "dotnet"
        dotnet_exe = dotnet_root / "dotnet.exe"
    else:
        linux = repo / ".cache" / "linux"
        engine = engine or str(linux / "godot" / f"Godot_v{version}-stable_mono_linux_x86_64"
                               / f"Godot_v{version}-stable_mono_linux.x86_64")
        dotnet_root = Path(dotnet).parent if dotnet else linux / "dotnet"
        dotnet_exe = dotnet_root / "dotnet"
    if not engine or not Path(engine).is_file():
        raise GameNotAvailable(f"Godot .NET {version} is not installed in this checkout (tools/bootstrap-native.ps1)")
    if not dotnet_exe.is_file():
        raise GameNotAvailable("the pinned .NET SDK is not installed in this checkout")
    if not (project / ".godot" / "mono" / "temp" / "bin" / "Debug" / "GarageGame.dll").is_file():
        raise GameNotAvailable("the C# project is not built (run-engine-tests.ps1 builds it first)")
    return Path(engine), dotnet_root


def session_file_under(user_root: Path) -> Path:
    """Where Godot's user:// puts the session file when APPDATA (Windows) or XDG_DATA_HOME (POSIX) is user_root."""
    godot = "Godot" if os.name == "nt" else "godot"
    return user_root / godot / "app_userdata" / "Garage-Game" / "companion" / "session.json"


class RealGame:
    """The room with the companion link, in its own Godot process. A context manager.

    `lines` collects the game's output (its stdout and stderr, each line prefixed "out: " or "err: "). Nothing
    about the token is ever printed by the game.
    """

    def __init__(self, *, room: str | None = None, isolated: bool = True, window: bool = False,
                 repo: Path = DEFAULT_REPO_ROOT, extra_args: list[str] | None = None, ready_timeout_s: float = 90.0,
                 user_root: Path | None = None, expect_link: bool = True):
        """user_root: a user folder the caller owns (kept afterwards), so several games can share one, as two
        windows of the game share the player's. expect_link: start() waits for the room with the link on; False
        accepts a room that runs without it (`link` then says which)."""
        self.engine, self.dotnet_root = toolchain(repo)
        self.repo = repo
        self.room = room
        self.isolated = isolated or user_root is not None
        self.shared_root = user_root
        self.expect_link = expect_link
        self.link: str | None = None
        self.window = window
        self.extra_args = list(extra_args or [])
        self.ready_timeout_s = ready_timeout_s
        self.lines: list[str] = []
        self._changed = threading.Condition()
        self._user_root: Path | None = None
        self._process: subprocess.Popen | None = None
        self._exit_code: int | None = None
        self._threads: list[threading.Thread] = []
        self.session_path: Path | None = None
        self.quit_file: Path | None = None
        self.stopped_cleanly = False

    # ------------------------------------------------------------------ lifecycle

    def __enter__(self) -> "RealGame":
        self.start()
        return self

    def __exit__(self, *exc) -> None:
        self.stop()

    def start(self) -> None:
        env = dict(os.environ)
        env["DOTNET_ROOT"] = str(self.dotnet_root)
        env["PATH"] = str(self.dotnet_root) + os.pathsep + env.get("PATH", "")
        env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
        env["DOTNET_NOLOGO"] = "1"
        env.setdefault("GODOT_SILENCE_ROOT_WARNING", "1")
        if self.isolated:
            root = self.shared_root or Path(tempfile.mkdtemp(prefix="enfractal-real-game-"))
            self._user_root = None if self.shared_root else root
            for name in ("APPDATA", "LOCALAPPDATA", "XDG_DATA_HOME", "XDG_CONFIG_HOME"):
                env[name] = str(root)
            self.session_path = session_file_under(root)
            self.quit_file = root / f"quit-{os.getpid()}-{id(self)}"
            self.quit_file.unlink(missing_ok=True)
        else:
            base = Path(env.get("APPDATA") or Path.home() / "AppData" / "Roaming") if os.name == "nt" \
                else Path(env.get("XDG_DATA_HOME") or Path.home() / ".local" / "share")
            self.session_path = session_file_under(base)
            self.quit_file = Path(tempfile.gettempdir()) / f"enfractal-real-game-quit-{os.getpid()}"
            self.quit_file.unlink(missing_ok=True)
        arguments = [str(self.engine)]
        if not self.window:
            arguments.append("--headless")
        arguments += ["--path", str(self.repo / "game"), "--max-fps", "60", SCENE, "--",
                      f"--companion-quit-file={self.quit_file}", f"--companion-parent-pid={os.getpid()}"]
        if self.room:
            arguments.append(f"--room={self.room}")
        arguments += self.extra_args
        flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" and not self.window else 0
        self._process = subprocess.Popen(arguments, cwd=str(self.repo / "game"), env=env, stdin=subprocess.DEVNULL,
                                         stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=flags)
        for stream, prefix in ((self._process.stdout, "out: "), (self._process.stderr, "err: ")):
            thread = threading.Thread(target=self._pump, args=(stream, prefix), daemon=True)
            thread.start()
            self._threads.append(thread)
        try:
            ready = self.wait_for(r"COMPANION_ROOM_READY .* link=(on|off)", self.ready_timeout_s)
            self.link = ready.rsplit("link=", 1)[1].strip()
            if self.expect_link and self.link != "on":
                raise RuntimeError("the game started without the companion link:\n" + "\n".join(self.lines[-20:]))
        except Exception:
            self.stop()
            raise

    def stop(self) -> None:
        process = self._process
        if process is None:
            return
        self._process = None
        if process.poll() is None and self.quit_file is not None:
            try:
                self.quit_file.parent.mkdir(parents=True, exist_ok=True)
                self.quit_file.write_bytes(b"")
                process.wait(20)
            except (OSError, subprocess.TimeoutExpired):
                pass
        self.stopped_cleanly = process.poll() is not None
        if process.poll() is None:
            _kill_tree(process)
        self._exit_code = process.poll()
        for thread in self._threads:
            thread.join(5)
        for stream in (process.stdout, process.stderr):
            if stream is not None:
                stream.close()
        if self.quit_file is not None and (self.shared_root is not None or not self.isolated):
            self.quit_file.unlink(missing_ok=True)
        if self._user_root is not None:
            shutil.rmtree(self._user_root, ignore_errors=True)

    def kill(self) -> None:
        """End the game the way a crash would: no clean exit, so nothing it holds is cleaned up by the game."""
        process = self._process
        if process is not None and process.poll() is None:
            _kill_tree(process)
        self.stop()

    @property
    def running(self) -> bool:
        return self._process is not None and self._process.poll() is None

    @property
    def returncode(self) -> int | None:
        return self._exit_code if self._process is None else self._process.poll()

    # ------------------------------------------------------------------ output

    def _pump(self, stream, prefix: str) -> None:
        for raw in iter(stream.readline, b""):
            line = raw.decode("utf-8", errors="replace").rstrip("\r\n")
            with self._changed:
                self.lines.append(prefix + line)
                self._changed.notify_all()
        with self._changed:
            self._changed.notify_all()

    def wait_for(self, pattern: str, timeout_s: float = 30.0, *, after: int = 0) -> str:
        """The first output line from index `after` on whose text matches `pattern`; raises TimeoutError otherwise."""
        regex = re.compile(pattern)
        deadline = time.monotonic() + timeout_s
        with self._changed:
            while True:
                for line in self.lines[after:]:
                    if regex.search(line):
                        return line
                remaining = deadline - time.monotonic()
                if remaining <= 0 or (self._process is not None and self._process.poll() is not None
                                      and not any(t.is_alive() for t in self._threads)):
                    tail = "\n".join(self.lines[-40:])
                    raise TimeoutError(f"the game did not print {pattern!r} in time:\n{tail}")
                self._changed.wait(min(remaining, 0.25))

    def errors(self) -> list[str]:
        """Lines the game wrote to stderr (the runners treat any as a failure)."""
        with self._changed:
            return [line for line in self.lines if line.startswith("err: ") and line[5:].strip()]

    def states(self, after: int = 0) -> list[str]:
        """The companion's visible states, in the order the game showed them."""
        with self._changed:
            return [line.split()[-1] for line in self.lines[after:] if line.startswith("out: COMPANION_STATE ")]


def _kill_tree(process: subprocess.Popen) -> None:
    if os.name == "nt":
        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], stdout=subprocess.DEVNULL,
                       stderr=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)
    else:
        process.kill()
    try:
        process.wait(10)
    except subprocess.TimeoutExpired:
        pass


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="python -m garage_game_companion.real_game", description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--room", help="a room id or folder (default: the test room)")
    parser.add_argument("--isolated", action="store_true", help="use a fresh temporary user folder")
    parser.add_argument("--window", action="store_true", help="open a window instead of running headless")
    options = parser.parse_args(argv)
    try:
        game = RealGame(room=options.room, isolated=options.isolated, window=options.window)
    except GameNotAvailable as error:
        print(f"cannot run the game: {error}", file=sys.stderr)
        return 2
    with game:
        print(f"room running; session file: {game.session_path}", flush=True)
        print("connect an MCP client with: python -m garage_game_companion.profile"
              + (f" --session-file \"{game.session_path}\"" if options.isolated else ""), flush=True)
        shown = 0
        try:
            while game.running:
                time.sleep(0.5)
                for line in game.lines[shown:]:
                    if "COMPANION_" in line or line.startswith("err: "):
                        print(line[5:], flush=True)
                shown = len(game.lines)
        except KeyboardInterrupt:
            pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
