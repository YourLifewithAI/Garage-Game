"""The play-only client profile (founder decision, 6 October 2026): one MCP server, nothing else.

Any MCP client or agent harness can take the profile. These tests check what it says, launch the server exactly
as it says with the official SDK's stdio client (the session offers the game's tools and nothing else), and check
the easier connection of A2 (docs/codex/reports/04-byoai-connect-survey.md):
- on Windows the profile gives the server SYSTEMROOT, with this machine's value, and nothing else: without it the
  server cannot load Winsock and exits with WinError 10106 when a client starts it with an empty environment;
- every client in clients.json gets the same server in its own format, which parses back to that server;
- --write puts the files in the folder it is given and nowhere else.
"""
from __future__ import annotations

import io
import json
import os
import re
import subprocess
import sys
import tempfile
import threading
import tomllib
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

from mcp import Client, StdioServerParameters
from support import contract_problems, contracts

from garage_game_companion import profile

VENDOR_WORDS = re.compile(r"anthropic|claude|openai|chatgpt|\bgpt|gemini|google|mistral|llama|copilot|bedrock|vertex",
                          re.IGNORECASE)
ON_WINDOWS = os.name == "nt"
# A path with a space, a quote, backslashes and a non-ASCII letter, which every format must carry unchanged.
AWKWARD = "C:\\Games\\EnFractal's D\u00e9v\\companion\\.venv\\Scripts\\python.exe"


def printed(*argv: str) -> dict:
    out = io.StringIO()
    with redirect_stdout(out):
        status = profile.main(list(argv))
    assert status == 0
    return json.loads(out.getvalue())


def parse_yaml_subset(text: str):
    """The YAML the generator writes: block mappings, '- ' lists of mappings, and scalars (single- or double-quoted
    strings, integers, booleans, flow lists of those). Anything else fails the test."""
    lines = [line for line in text.split("\n") if line.strip()]
    position = 0

    def split_flow(body: str) -> list[str]:
        items, current, quote, i = [], "", None, 0
        while i < len(body):
            c = body[i]
            if quote == "'" and c == "'" and body[i + 1:i + 2] == "'":
                current, i = current + "''", i + 2
                continue
            if quote == '"' and c == "\\":
                current, i = current + body[i:i + 2], i + 2
                continue
            if quote and c == quote:
                quote = None
            elif not quote and c in "'\"":
                quote = c
            elif not quote and c == ",":
                items.append(current)
                current, i = "", i + 1
                continue
            current, i = current + c, i + 1
        if current.strip():
            items.append(current)
        return items

    def scalar(token: str):
        token = token.strip()
        if token.startswith("'"):
            assert token.endswith("'") and len(token) >= 2, token
            return token[1:-1].replace("''", "'")
        if token.startswith('"'):
            return json.loads(token)
        if token in ("true", "false"):
            return token == "true"
        if token.startswith("["):
            assert token.endswith("]"), token
            return [scalar(item) for item in split_flow(token[1:-1])]
        return int(token)

    def block(indent: int):
        nonlocal position
        result = None
        while position < len(lines):
            line = lines[position]
            here = len(line) - len(line.lstrip(" "))
            if here < indent:
                break
            assert here == indent, f"unexpected indentation: {line!r}"
            body = line[indent:]
            if body.startswith("- "):
                result = [] if result is None else result
                assert isinstance(result, list), line
                lines[position] = " " * (indent + 2) + body[2:]
                result.append(block(indent + 2))
                continue
            key, colon, rest = body.partition(":")
            assert colon and key and " " not in key, f"not a mapping line: {line!r}"
            result = {} if result is None else result
            assert isinstance(result, dict) and key not in result, line
            position += 1
            result[key] = scalar(rest) if rest.strip() else block(indent + 2)
        return result

    document = block(0)
    assert position == len(lines), "the parser did not read every line"
    return document


def parse(format_name: str, text: str):
    if format_name.endswith("-json"):
        return json.loads(text)
    if format_name.endswith("-toml"):
        return tomllib.loads(text)
    return parse_yaml_subset(text)


def server_in(format_name: str, document) -> dict:
    """The one server entry a client's document configures."""
    if format_name == "mcpServers-json":
        servers = document["mcpServers"]
    elif format_name == "servers-json":
        servers = document["servers"]
    elif format_name == "mcp.servers-json":
        servers = document["mcp"]["servers"]
    elif format_name in ("mcp_servers-toml", "mcp_servers-yaml"):
        servers = document["mcp_servers"]
    elif format_name == "mcpServers-yaml-list":
        assert len(document["mcpServers"]) == 1
        entry = dict(document["mcpServers"][0])
        servers = {entry.pop("name"): entry}
    else:
        raise AssertionError(format_name)
    assert list(servers) == [profile.SERVER_KEY], servers
    return servers[profile.SERVER_KEY]


class Profile(unittest.TestCase):
    def test_names_exactly_one_stdio_server_that_runs_this_package(self):
        for argv, extra in (((), []), (("--mock",), ["--mock"]),
                            (("--session-file", "game/session.json"), ["--session-file", str(Path("game/session.json").resolve())]),
                            (("--mock", "--schema-profile", "minimal"), ["--mock", "--schema-profile", "minimal"])):
            with self.subTest(argv=argv):
                document = printed(*argv)
                self.assertEqual(list(document), ["mcpServers"])
                self.assertEqual(list(document["mcpServers"]), ["garage-game-companion"])
                server = document["mcpServers"]["garage-game-companion"]
                # No environment but SYSTEMROOT on Windows: the server needs no keys, and clears the rest anyway.
                self.assertEqual(set(server), {"type", "command", "args"} | ({"env"} if ON_WINDOWS else set()))
                self.assertEqual(server["type"], "stdio")
                self.assertTrue(Path(server["command"]).is_absolute())
                self.assertEqual(server["args"], ["-m", "garage_game_companion", *extra])
                if ON_WINDOWS:
                    self.assertEqual(server["env"], {"SYSTEMROOT": os.environ["SYSTEMROOT"]})

    def test_the_command_is_the_companions_own_interpreter_not_what_it_links_to(self):
        self.assertEqual(profile.play_only_profile()["mcpServers"]["garage-game-companion"]["command"],
                         str(Path(sys.executable).absolute()))

    def test_offers_nothing_but_the_game_and_names_no_vendor(self):
        document = printed("--mock")
        server = document["mcpServers"]["garage-game-companion"]
        self.assertEqual(list(server.get("env", {})), ["SYSTEMROOT"] if ON_WINDOWS else [])
        server.pop("env", None)
        text = json.dumps(document)
        self.assertIsNone(VENDOR_WORDS.search(text))
        self.assertNotIn("--no-lockdown", text)
        for word in ("shell", "http", "url", "env", "token", "key", "secret"):
            self.assertNotIn(f'"{word}', text.lower())

    def test_refuses_two_game_sources_at_once(self):
        with self.assertRaises(ValueError):
            profile.play_only_profile(mock=True, session_file=Path("x.json"))


class SystemRoot(unittest.TestCase):
    def test_windows_profiles_give_the_server_systemroot_with_this_machines_value(self):
        launch = profile.server_launch(windows=True, systemroot="D:\\WINNT")
        self.assertEqual(launch.env, {"SYSTEMROOT": "D:\\WINNT"})
        here = profile.server_launch(windows=True)
        self.assertEqual(here.env, {"SYSTEMROOT": os.environ.get("SYSTEMROOT") or profile.DEFAULT_SYSTEMROOT})
        self.assertEqual(profile.server_launch(windows=False).env, {})
        posix = profile.play_only_profile(windows=False)["mcpServers"]["garage-game-companion"]
        self.assertNotIn("env", posix)

    def run_server(self, env: dict) -> tuple[int | None, list[dict], str]:
        """Start the server exactly as a client would with this environment, and speak MCP to it over stdio. Each
        answer is awaited before the next message, and stdin stays open until the last one, as a client keeps it."""
        launch = profile.server_launch(mock=True)
        process = subprocess.Popen([launch.command, *launch.args], env=env, stdin=subprocess.PIPE,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        replies: list[dict] = []
        ended: list[bool] = []
        arrived = threading.Condition()

        def read() -> None:
            for line in process.stdout:
                if line.strip():
                    with arrived:
                        replies.append(json.loads(line))
                        arrived.notify_all()
            with arrived:
                ended.append(True)  # the server closed its output: nothing more will come
                arrived.notify_all()

        reader = threading.Thread(target=read, daemon=True)
        reader.start()

        def send(message: dict) -> bool:
            try:
                process.stdin.write((json.dumps(message) + "\n").encode("utf-8"))
                process.stdin.flush()
                return True
            except OSError:  # the server has already gone
                return False

        def answer(request_id: int) -> bool:
            with arrived:
                arrived.wait_for(lambda: bool(ended) or any(r.get("id") == request_id for r in replies), 60)
                return any(r.get("id") == request_id for r in replies)

        initialize = {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
            "protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "profile-test", "version": "1"}}}
        if send(initialize) and answer(1):
            send({"jsonrpc": "2.0", "method": "notifications/initialized"})
            if send({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}):
                answer(2)
        try:
            process.stdin.close()
        except OSError:
            pass
        try:
            process.wait(30)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
        reader.join(10)
        err = process.stderr.read().decode("utf-8", errors="replace")
        process.stdout.close()
        process.stderr.close()
        return process.returncode, replies, err

    @unittest.skipUnless(ON_WINDOWS, "SYSTEMROOT is a Windows variable")
    def test_the_server_serves_with_only_the_environment_the_profile_gives_it(self):
        server = printed("--mock")["mcpServers"]["garage-game-companion"]
        code, replies, err = self.run_server(dict(server["env"]))
        tools = next(r for r in replies if r.get("id") == 2)["result"]["tools"]
        self.assertEqual(len(tools), len(contracts().tool_specs()), err)
        self.assertEqual({t["name"] for t in tools}, {s.name for s in contracts().tool_specs()})

    @unittest.skipUnless(ON_WINDOWS, "SYSTEMROOT is a Windows variable")
    def test_without_systemroot_the_server_cannot_start_which_is_why_the_profile_sets_it(self):
        code, replies, err = self.run_server({})
        if code == 0 and replies:
            self.skipTest("this Python loads Winsock without SYSTEMROOT; the profile still sets it")
        self.assertNotEqual(code, 0)
        self.assertEqual(replies, [])
        self.assertIn("10106", err)


class ClientFormats(unittest.TestCase):
    def setUp(self):
        self.launch = profile.server_launch(python=AWKWARD, session_file=Path("game/session.json"), windows=True,
                                            systemroot="C:\\Windows")

    def test_the_catalogue_names_the_six_clients_with_known_formats(self):
        entries = profile.clients()
        self.assertEqual([e["id"] for e in entries], ["claude-code", "codex", "vscode", "continue", "hermes", "openclaw"])
        files = []
        for entry in entries:
            with self.subTest(client=entry["id"]):
                self.assertTrue({"id", "name", "format", "file", "goes", "play_only", "server_extra", "env", "verified"} <= set(entry))
                profile.render_document(entry["format"], self.launch, entry)  # a format the generator knows
                files += [entry["file"], *(extra["file"] for extra in entry.get("also", []))]
        self.assertEqual(len(files), len(set(files)))
        self.assertTrue(all(not Path(f).is_absolute() and ".." not in Path(f).parts for f in files))

    def test_every_client_gets_the_same_server_in_its_own_format(self):
        for entry in profile.clients():
            with self.subTest(client=entry["id"]):
                text = profile.render_text(entry["format"], self.launch, entry)
                server = server_in(entry["format"], parse(entry["format"], text))
                self.assertEqual(server["command"], AWKWARD)  # the space, the quote and the backslashes survive
                self.assertEqual(server["args"], self.launch.args)
                self.assertEqual(server.get("env"), {"SYSTEMROOT": "C:\\Windows"} if entry["env"] else None)
                for key, value in entry["server_extra"].items():
                    self.assertEqual(server[key], value)
                self.assertNotIn("--no-lockdown", text)
                for word in ("token", "secret", "password", "api_key", "apikey"):
                    self.assertNotIn(word, text.lower())

    def test_a_path_is_read_as_the_target_platform_reads_it_on_any_host(self):
        # A Windows profile keeps its Windows paths on a Linux host, and a POSIX one its POSIX paths on Windows;
        # only a path relative on the target is made absolute, against this machine's working directory.
        self.assertEqual(profile.server_launch(python=AWKWARD, windows=True).command, AWKWARD)
        session = "D:\\Play\\companion\\session.json"
        self.assertEqual(profile.server_launch(session_file=session, windows=True).args[-1], session)
        posix = "/opt/enfractal/companion/.venv/bin/python"
        self.assertEqual(profile.server_launch(python=posix, windows=False).command, posix)
        self.assertEqual(profile.server_launch(python="venv/python", windows=ON_WINDOWS).command,
                         str(Path("venv/python").absolute()))
        for entry in profile.clients():
            with self.subTest(client=entry["id"]):
                text = profile.render_text(entry["format"], profile.server_launch(python=AWKWARD, windows=True), entry)
                self.assertEqual(server_in(entry["format"], parse(entry["format"], text))["command"], AWKWARD)

    def test_the_documented_shapes_hold(self):
        codex = profile.render_text("mcp_servers-toml", self.launch, profile.client("codex"))
        self.assertTrue(codex.startswith("[mcp_servers.garage-game-companion]\n"))
        self.assertIn("env = { SYSTEMROOT = 'C:\\Windows' }", codex)
        self.assertIn("required = true", codex)
        plain = profile.server_launch(python="C:\\Games\\EnFractal Dev\\python.exe", windows=True, systemroot="C:\\Windows")
        self.assertIn("command = 'C:\\Games\\EnFractal Dev\\python.exe'", profile.render_text("mcp_servers-toml", plain))
        hermes = parse_yaml_subset(profile.render_text("mcp_servers-yaml", self.launch, profile.client("hermes")))
        self.assertEqual(hermes["mcp_servers"]["garage-game-companion"]["tools"], {"resources": False, "prompts": False})
        cont = parse_yaml_subset(profile.render_text("mcpServers-yaml-list", self.launch, profile.client("continue")))
        self.assertEqual((cont["schema"], cont["mcpServers"][0]["type"]), ("v1", "stdio"))
        vscode = json.loads(profile.render_text("servers-json", self.launch, profile.client("vscode")))
        self.assertEqual(list(vscode), ["servers"])
        openclaw = json.loads(profile.render_text("mcp.servers-json", self.launch, profile.client("openclaw")))
        self.assertNotIn("type", openclaw["mcp"]["servers"]["garage-game-companion"])

    def test_write_puts_every_file_in_the_folder_and_nowhere_else(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp) / "play"
            written = profile.write_profiles(folder, self.launch)
            expected = {"README.txt"} | {e["file"] for e in profile.clients()} | \
                {x["file"] for e in profile.clients() for x in e.get("also", [])}
            self.assertEqual({p.relative_to(folder.resolve()).as_posix() for p in written}, expected)
            self.assertEqual(sorted(p.name for p in Path(tmp).iterdir()), ["play"])
            readme = (folder / "README.txt").read_text(encoding="utf-8")
            for entry in profile.clients():
                self.assertIn(entry["file"], readme)
                self.assertIn(entry["play_only"], readme)
            agent = (folder / "vscode/.github/agents/garage-game.agent.md").read_text(encoding="utf-8")
            self.assertIn("tools: ['garage-game-companion/*']", agent)
            self.assertEqual(profile.write_profiles(folder, self.launch), written)  # the same files again: fine
            other = profile.server_launch(python="C:\\Other\\python.exe", windows=True, systemroot="C:\\Windows")
            with self.assertRaises(FileExistsError):
                profile.write_profiles(folder, other)
            profile.write_profiles(folder, other, force=True)
            self.assertIn("C:\\\\Other", (folder / "claude-code/play-mcp.json").read_text(encoding="utf-8"))

    def test_the_command_line_prints_one_client_or_writes_them_all(self):
        out = io.StringIO()
        with redirect_stdout(out):
            self.assertEqual(profile.main(["--mock", "--client", "codex"]), 0)
        self.assertTrue(out.getvalue().startswith("[mcp_servers.garage-game-companion]"))
        self.assertIn("'--mock'", out.getvalue())
        with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            profile.main(["--client", "nobody"])
        with tempfile.TemporaryDirectory() as tmp, redirect_stdout(io.StringIO()) as listed:
            self.assertEqual(profile.main(["--write", tmp]), 0)
            self.assertEqual(len(listed.getvalue().splitlines()), 8)
        with redirect_stdout(io.StringIO()) as names:
            profile.main(["--list-clients"])
        self.assertEqual(len(names.getvalue().splitlines()), 6)


class LaunchedFromTheProfile(unittest.IsolatedAsyncioTestCase):
    async def test_a_client_launched_from_the_profile_sees_the_game_and_nothing_else(self):
        server = printed("--mock")["mcpServers"]["garage-game-companion"]
        parameters = StdioServerParameters(command=server["command"], args=server["args"], env=server.get("env"))
        async with Client(parameters, mode="legacy") as client:
            capabilities = client.server_capabilities
            self.assertEqual([str(r.uri) for r in (await client.list_resources()).resources], ["enfractal://journal"])
            self.assertIsNone(capabilities.prompts)
            tools = (await client.list_tools()).tools
            self.assertEqual({t.name for t in tools}, {s.name for s in contracts().tool_specs()})
            self.assertNotIn("protect_unlock", {t.name for t in tools})
            observed = (await client.call_tool("observe", {})).structured_content
            self.assertTrue(observed["ok"])
            self.assertEqual(contract_problems(observed), [])


if __name__ == "__main__":
    unittest.main()
