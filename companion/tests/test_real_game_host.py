"""RealGame's host override (companion/src/garage_game_companion/real_game.py): GARAGE_GAME_HOST and GARAGE_GAME_HOST_ARGS
start another host of the game in Godot's place, with Godot's switches after the host's own arguments; without them
the Godot command line is exactly what it always was; a misconfigured override fails loudly instead of skipping."""
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path
from unittest import mock

from support import REPO

from garage_game_companion import real_game
from garage_game_companion.real_game import GameNotAvailable, HostMisconfigured, RealGame, session_file_under

UNSET = {"GARAGE_GAME_HOST": "", "GARAGE_GAME_HOST_ARGS": ""}

# A host that keeps the process contract and reports how it was started: its arguments, its folder and its user
# folder, as one JSON line; then the ready line, once its session file is written at Godot's path; then it waits
# for the quit file and exits cleanly.
FAKE_HOST = textwrap.dedent('''\
    import json, os, sys, time
    from pathlib import Path
    args = sys.argv[1:]
    quit_file = next(a.split("=", 1)[1] for a in args if a.startswith("--companion-quit-file="))
    print("FAKE_HOST " + json.dumps({"args": args, "cwd": os.getcwd(), "appdata": os.environ.get("APPDATA"),
                                     "dotnet_root": os.environ.get("DOTNET_ROOT")}), flush=True)
    godot = "Godot" if os.name == "nt" else "godot"
    session = Path(os.environ["APPDATA" if os.name == "nt" else "XDG_DATA_HOME"]) / godot / "app_userdata" / "Garage-Game" / "companion" / "session.json"
    session.parent.mkdir(parents=True, exist_ok=True)
    session.write_text("{}", encoding="utf-8")
    print("COMPANION_ROOM_READY room=test_room link=on", flush=True)
    print("COMPANION_STATE listening", flush=True)
    while not os.path.exists(quit_file):
        time.sleep(0.05)
    session.unlink()
    sys.exit(0)
''')


class FakeProcess:
    """Enough of a Popen for start(): it prints the ready line and has ended."""

    def __init__(self, arguments, **options):
        self.arguments, self.options = arguments, options
        read, write = os.pipe()
        os.write(write, b"COMPANION_ROOM_READY room=test_room link=on\n")
        os.close(write)
        self.stdout = os.fdopen(read, "rb")
        self.stderr = open(os.devnull, "rb")
        self.pid = 0

    def poll(self):
        return 0

    def wait(self, timeout=None):
        return 0


class GodotStaysAsItWas(unittest.TestCase):
    ENGINE, DOTNET = Path("C:/godot/Godot_console.exe"), Path("C:/repo/.cache/dotnet")

    def launch(self, window: bool):
        """start() and stop() with Popen stubbed: the one process (its arguments and options), the environment the
        launcher began from, and the game."""
        started: list[FakeProcess] = []

        def popen(arguments, **options):
            started.append(FakeProcess(arguments, **options))
            return started[-1]

        with mock.patch.dict(os.environ, UNSET), \
                mock.patch.object(real_game, "toolchain", lambda repo=None: (self.ENGINE, self.DOTNET)), \
                mock.patch.object(real_game.subprocess, "Popen", popen):
            os.environ.pop("GODOT_SILENCE_ROOT_WARNING", None)  # so the launcher's own default shows
            before = dict(os.environ)
            game = RealGame(room="test_room", repo=REPO, extra_args=["--extra"], window=window)
            game.start()
            game.stop()
        self.assertEqual(len(started), 1)
        self.assertIsNone(game.host_args)
        return started[0], before, game

    def assertEnvironment(self, env: dict, before: dict, game: RealGame) -> None:
        """The caller's environment plus exactly Godot's five keys and the isolated user folder's four. A failure names
        keys, and values only of those nine: the rest of the environment is the caller's own and stays out of logs."""
        root = game.quit_file.parent
        self.assertEqual(game.session_path, session_file_under(root))
        expected = {"DOTNET_ROOT": str(self.DOTNET), "PATH": str(self.DOTNET) + os.pathsep + before.get("PATH", ""),
                    "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1", "GODOT_SILENCE_ROOT_WARNING": "1"}
        for name in ("APPDATA", "LOCALAPPDATA", "XDG_DATA_HOME", "XDG_CONFIG_HOME"):
            expected[name] = str(root)
        self.assertEqual({name: env.get(name) for name in expected}, expected)
        self.assertEqual(sorted(name for name in set(env) | set(before)
                                if name not in expected and env.get(name) != before.get(name)), [],
                         "keys added, removed or changed besides the nine")

    def test_without_the_override_godot_gets_the_same_command_line_folder_and_runtime(self):
        process, before, game = self.launch(window=False)
        self.assertEqual(process.arguments, [
            str(self.ENGINE), "--headless", "--path", str(REPO / "game"), "--max-fps", "60", real_game.SCENE, "--",
            f"--companion-quit-file={game.quit_file}", f"--companion-parent-pid={os.getpid()}", "--room=test_room",
            "--extra"])
        self.assertEqual(process.options["cwd"], str(REPO / "game"))
        self.assertEnvironment(process.options["env"], before, game)
        self.assertEqual(process.options["creationflags"], subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)

    def test_with_a_window_godot_gets_the_same_without_headless_and_may_open_its_window(self):
        process, before, game = self.launch(window=True)
        self.assertEqual(process.arguments, [
            str(self.ENGINE), "--path", str(REPO / "game"), "--max-fps", "60", real_game.SCENE, "--",
            f"--companion-quit-file={game.quit_file}", f"--companion-parent-pid={os.getpid()}", "--room=test_room",
            "--extra"])
        self.assertEqual(process.options["cwd"], str(REPO / "game"))
        self.assertEnvironment(process.options["env"], before, game)
        self.assertEqual(process.options["creationflags"], 0)


class AnotherHost(unittest.TestCase):
    def setUp(self):
        self.folder = Path(tempfile.mkdtemp(prefix="garage-game-fake-host-"))
        self.script = self.folder / "fake_host.py"
        self.script.write_text(FAKE_HOST, encoding="utf-8")

    def tearDown(self):
        for path in sorted(self.folder.rglob("*"), reverse=True):
            path.unlink() if path.is_file() else path.rmdir()
        self.folder.rmdir()

    def override(self, *host_args: str) -> dict:
        return {"GARAGE_GAME_HOST": sys.executable, "GARAGE_GAME_HOST_ARGS": json.dumps(["-I", str(self.script), *host_args])}

    def test_the_host_runs_with_its_arguments_then_godots_switches_in_the_repository_and_quits_cleanly(self):
        with mock.patch.dict(os.environ, self.override("-game", "C:/path with spaces/x.uproject")):
            os.environ.pop("DOTNET_ROOT", None)
            self.assertEqual(real_game.toolchain(REPO), (Path(sys.executable), Path(sys.executable).parent))
            game = RealGame(room="test_room", repo=REPO, extra_args=["--extra"], ready_timeout_s=30)
            try:
                game.start()
                self.assertEqual(game.link, "on")
                self.assertTrue(game.session_path.is_file())
                report = json.loads(game.wait_for(r"^out: FAKE_HOST ")[len("out: FAKE_HOST "):])
                root = game.session_path.parents[4]
                self.assertEqual(game.session_path, session_file_under(root))
            finally:
                game.stop()
        self.assertEqual(report["args"], [
            "-game", "C:/path with spaces/x.uproject", f"--companion-quit-file={game.quit_file}",
            f"--companion-parent-pid={os.getpid()}", "--room=test_room", "--extra"])
        self.assertEqual(Path(report["cwd"]).resolve(), REPO.resolve())
        self.assertIsNone(report["dotnet_root"], "the launcher gives Godot's runtime to Godot only")
        if os.name == "nt":
            self.assertEqual(Path(report["appdata"]), root)
        self.assertTrue(game.stopped_cleanly)
        self.assertEqual(game.returncode, 0)
        self.assertEqual(game.errors(), [])
        self.assertEqual(game.states(), ["listening"])
        self.assertFalse(root.exists(), "the isolated user folder is removed afterwards")

    def test_no_host_arguments_is_the_empty_list(self):
        with mock.patch.dict(os.environ, {"GARAGE_GAME_HOST": sys.executable, "GARAGE_GAME_HOST_ARGS": ""}):
            self.assertEqual(real_game.host_override(), (Path(sys.executable), []))

    def test_a_relative_host_is_taken_from_the_callers_folder(self):
        try:
            relative = os.path.relpath(sys.executable)
        except ValueError:
            self.skipTest("the interpreter is on another drive than this folder")
        with mock.patch.dict(os.environ, {"GARAGE_GAME_HOST": relative, "GARAGE_GAME_HOST_ARGS": ""}):
            host, _ = real_game.host_override()
        self.assertTrue(host.is_absolute())
        self.assertEqual(host, Path(os.path.abspath(sys.executable)))

    def test_a_misconfigured_override_fails_loudly_and_is_not_a_skip(self):
        self.assertFalse(issubclass(HostMisconfigured, GameNotAvailable))
        cases = {
            "a missing host": {"GARAGE_GAME_HOST": str(self.folder / "missing.exe"), "GARAGE_GAME_HOST_ARGS": ""},
            "arguments that are not JSON": {"GARAGE_GAME_HOST": sys.executable, "GARAGE_GAME_HOST_ARGS": "-game -nullrhi"},
            "arguments that are not strings": {"GARAGE_GAME_HOST": sys.executable, "GARAGE_GAME_HOST_ARGS": '["-game", 1]'},
            "arguments that are not a list": {"GARAGE_GAME_HOST": sys.executable, "GARAGE_GAME_HOST_ARGS": '"-game"'},
            "arguments without a host": {"GARAGE_GAME_HOST": "", "GARAGE_GAME_HOST_ARGS": '["-game"]'},
        }
        for name, environment in cases.items():
            with self.subTest(name), mock.patch.dict(os.environ, environment):
                with self.assertRaises(HostMisconfigured):
                    RealGame(repo=REPO)
                with self.assertRaises(HostMisconfigured):
                    real_game.toolchain(REPO)

    def test_the_command_line_launcher_reports_a_misconfigured_host(self):
        environment = dict(os.environ, GARAGE_GAME_HOST=str(self.folder / "missing.exe"), GARAGE_GAME_HOST_ARGS="")
        environment["PYTHONPATH"] = str(REPO / "companion" / "src")
        finished = subprocess.run([sys.executable, "-m", "garage_game_companion.real_game"], env=environment,
                                  capture_output=True, text=True, timeout=60)
        self.assertEqual(finished.returncode, 2)
        self.assertIn("GARAGE_GAME_HOST names no file", finished.stderr)


if __name__ == "__main__":
    unittest.main()
