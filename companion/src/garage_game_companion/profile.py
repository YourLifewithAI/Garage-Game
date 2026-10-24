"""The play-only client profile: the one MCP server a play session needs, and nothing else.

Players bring their own AI: any MCP client or agent harness. The client session used for play
should hold this server only, with no shell, file, browser, web or other MCP tools, so that a sign
in the room that talks a model into "doing something" has nothing to do it with except game
commands, which the game checks. The game cannot see or limit what else a client loads; this
profile is the configuration that makes it true.

    python -m garage_game_companion.profile                       # the running game's session file
    python -m garage_game_companion.profile --mock                # the built-in mock game
    python -m garage_game_companion.profile --session-file PATH   # another session file
    python -m garage_game_companion.profile --client ID           # one client's own format (--list-clients)
    python -m garage_game_companion.profile --write DIR           # every client's files, into DIR only

prints the profile: by default in the widely used `mcpServers` JSON shape (one entry: a command line
for a stdio server). The command is the interpreter running this module, so run it with the
companion's own environment.

**Environment.** The server needs no keys or tokens, and it clears whatever environment it inherits
before it serves anything. The one variable a profile sets is `SYSTEMROOT`, on Windows only, with
this machine's value: some clients start servers with an almost empty environment, and without
`SYSTEMROOT` Python cannot load Winsock, so the server exits at once with `WinError 10106`.

**Per-client formats.** The same server, written in each client's own configuration format. The
formats are named by shape here; which client uses which, where its file goes and how to make a
session play-only are data, in `clients.json` (from docs/codex/reports/04-byoai-connect-survey.md),
which the server never reads. `--write` puts every file under the folder it is given and nowhere
else: it never edits a client's own configuration.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from dataclasses import dataclass, field
from pathlib import Path, PurePosixPath, PureWindowsPath

from . import ids

SERVER_KEY = "garage-game-companion"
CATALOGUE = Path(__file__).resolve().parent / "clients.json"
DEFAULT_SYSTEMROOT = "C:\\Windows"


@dataclass(frozen=True)
class ServerLaunch:
    """How a client starts the server: one executable, its arguments, and the environment it must be given."""

    command: str
    args: list[str]
    env: dict[str, str] = field(default_factory=dict)


def server_launch(*, mock: bool = False, session_file: Path | None = None, python: str | None = None,
                  schema_profile: str = "full", windows: bool | None = None, systemroot: str | None = None) -> ServerLaunch:
    """This package as a stdio server. On Windows the environment is SYSTEMROOT alone, with this machine's value."""
    if mock and session_file is not None:
        raise ValueError("choose --mock or --session-file, not both")
    windows = os.name == "nt" if windows is None else windows
    args = ["-m", "garage_game_companion"]
    if mock:
        args.append("--mock")
    elif session_file is not None:
        args += ["--session-file", _for_target(session_file, windows, follow_links=True)]
    if schema_profile != "full":
        args += ["--schema-profile", schema_profile]
    # Never following links: a POSIX virtual environment's python is a symlink, and following it would
    # launch the base interpreter without the companion's packages.
    command = _for_target(python or sys.executable, windows, follow_links=False)
    env = {}
    if windows:
        env["SYSTEMROOT"] = systemroot or os.environ.get("SYSTEMROOT") or os.environ.get("SystemRoot") or DEFAULT_SYSTEMROOT
    return ServerLaunch(command, args, env)


def _for_target(path, windows: bool, *, follow_links: bool) -> str:
    """A path as the target platform reads it. One that is absolute there (a drive or UNC path for Windows, one from / for POSIX) is
    kept exactly as given, whatever this machine is: a Windows profile written on Linux keeps its Windows path. A
    relative one is made absolute against this machine's working directory."""
    text = str(path)
    if (PureWindowsPath if windows else PurePosixPath)(text).is_absolute():
        return text
    return str(Path(text).resolve() if follow_links else Path(text).absolute())


def play_only_profile(*, mock: bool = False, session_file: Path | None = None, python: str | None = None,
                      schema_profile: str = "full", windows: bool | None = None, systemroot: str | None = None) -> dict:
    """The profile in the common mcpServers JSON shape: exactly one stdio server, this package."""
    launch = server_launch(mock=mock, session_file=session_file, python=python, schema_profile=schema_profile,
                           windows=windows, systemroot=systemroot)
    return render_document("mcpServers-json", launch)


# ---------------------------------------------------------------------------- the client catalogue

def clients() -> list[dict]:
    document = json.loads(CATALOGUE.read_bytes())
    if not ids.is_schema(document.get("schema"), "companion_clients") or document.get("version") != 1:
        raise ValueError(f"clients.json is not a {ids.id_of('companion_clients')} version 1")
    return document["clients"]


def client(client_id: str) -> dict:
    for entry in clients():
        if entry["id"] == client_id:
            return entry
    raise KeyError(f"no client {client_id!r}; known: {', '.join(e['id'] for e in clients())}")


# ---------------------------------------------------------------------------- formats, named by shape

def _server_entry(launch: ServerLaunch, *, typed: bool, env: bool, extra: dict | None = None) -> dict:
    entry: dict = {"type": "stdio"} if typed else {}
    entry["command"] = launch.command
    entry["args"] = list(launch.args)
    if env and launch.env:
        entry["env"] = dict(launch.env)
    entry.update(extra or {})
    return entry


def render_document(format_name: str, launch: ServerLaunch, entry: dict | None = None) -> dict:
    """The configuration as data, for the JSON-shaped formats (and the YAML and TOML ones before writing)."""
    entry = entry or {}
    env = entry.get("env", True)
    extra = entry.get("server_extra", {})
    if format_name == "mcpServers-json":
        return {"mcpServers": {SERVER_KEY: _server_entry(launch, typed=True, env=env, extra=extra)}}
    if format_name == "servers-json":
        return {"servers": {SERVER_KEY: _server_entry(launch, typed=True, env=env, extra=extra)}}
    if format_name == "mcp.servers-json":
        return {"mcp": {"servers": {SERVER_KEY: _server_entry(launch, typed=False, env=env, extra=extra)}}}
    if format_name == "mcp_servers-toml":
        return {"mcp_servers": {SERVER_KEY: _server_entry(launch, typed=False, env=env, extra=extra)}}
    if format_name == "mcp_servers-yaml":
        return {"mcp_servers": {SERVER_KEY: _server_entry(launch, typed=False, env=env, extra=extra)}}
    if format_name == "mcpServers-yaml-list":
        server = {"name": SERVER_KEY, **_server_entry(launch, typed=True, env=env, extra=extra)}
        return {**entry.get("document_extra", {}), "mcpServers": [server]}
    raise ValueError(f"unknown format {format_name!r}")


def render_text(format_name: str, launch: ServerLaunch, entry: dict | None = None) -> str:
    document = render_document(format_name, launch, entry)
    if format_name.endswith("-json"):
        return json.dumps(document, indent=2, ensure_ascii=False) + "\n"
    if format_name.endswith("-toml"):
        return _toml(document)
    return _yaml(document)


def _toml_value(value) -> str:
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, int):
        return str(value)
    if isinstance(value, str):
        # Literal strings keep Windows backslashes as they are; a path holding a quote or a control
        # character falls back to an escaped basic string.
        if "'" not in value and not any(ord(c) < 0x20 or ord(c) == 0x7F for c in value):
            return "'" + value + "'"
        return json.dumps(value, ensure_ascii=False)
    if isinstance(value, list):
        return "[" + ", ".join(_toml_value(v) for v in value) + "]"
    if isinstance(value, dict):
        return "{ " + ", ".join(f"{_toml_key(k)} = {_toml_value(v)}" for k, v in value.items()) + " }"
    raise TypeError(f"cannot write {type(value).__name__} as TOML")


def _toml_key(key: str) -> str:
    return key if key.replace("_", "").replace("-", "").isalnum() and key.isascii() else json.dumps(key)


def _toml(document: dict) -> str:
    lines = []
    for table, servers in document.items():
        for name, server in servers.items():
            lines.append(f"[{_toml_key(table)}.{_toml_key(name)}]")
            lines += [f"{_toml_key(k)} = {_toml_value(v)}" for k, v in server.items()]
    return "\n".join(lines) + "\n"


def _yaml_scalar(value) -> str:
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, int):
        return str(value)
    if isinstance(value, str):
        if any(ord(c) < 0x20 or ord(c) == 0x7F for c in value):
            return json.dumps(value, ensure_ascii=False)  # a double-quoted YAML scalar with JSON's escapes
        return "'" + value.replace("'", "''") + "'"  # single quotes keep backslashes as they are
    if isinstance(value, list) and all(not isinstance(v, (dict, list)) for v in value):
        return "[" + ", ".join(_yaml_scalar(v) for v in value) + "]"
    raise TypeError(f"cannot write {type(value).__name__} as a YAML scalar")


def _yaml(document: dict, indent: int = 0) -> str:
    """Block YAML for mappings, lists of mappings, and scalars or flat lists of scalars as values."""
    pad = " " * indent
    lines = []
    for key, value in document.items():
        if isinstance(value, dict):
            lines.append(f"{pad}{key}:")
            lines.append(_yaml(value, indent + 2).rstrip("\n"))
        elif isinstance(value, list) and value and all(isinstance(v, dict) for v in value):
            lines.append(f"{pad}{key}:")
            for item in value:
                block = _yaml(item, indent + 4).rstrip("\n").split("\n")
                lines.append(f"{pad}  - " + block[0][indent + 4:])
                lines += block[1:]
        else:
            lines.append(f"{pad}{key}: {_yaml_scalar(value)}")
    return "\n".join(lines) + "\n"


# ---------------------------------------------------------------------------- writing a play folder

def client_files(entry: dict, launch: ServerLaunch) -> dict[str, str]:
    """Relative path -> text for one client: its configuration, plus any companion files it needs."""
    files = {entry["file"]: render_text(entry["format"], launch, entry)}
    for extra in entry.get("also", []):
        files[extra["file"]] = extra["text"]
    return files


def readme(launch: ServerLaunch) -> str:
    lines = [
        "Garage Game companion: MCP client configurations",
        "",
        "Each folder holds one client's configuration for the same server:",
        f"  command: {launch.command}",
        f"  args:    {' '.join(launch.args)}",
        f"  env:     {', '.join(f'{k}={v}' for k, v in launch.env.items()) or '(none)'}",
        "",
        "Nothing here was copied into a client's own settings. Registering a server is the player's choice: copy or",
        "merge the file as described, then let the client show its own review or trust prompt.",
        "A configuration connects the server; it does not make a session play-only. Play with this server only, in a",
        "session with no shell, file, browser or other MCP tools, and check the client's final tool list: 29 game",
        "tools, no resources, no prompts.",
        "",
    ]
    for entry in clients():
        lines += [f"{entry['name']} ({entry['id']})", f"  file: {entry['file']}"]
        lines += [f"  also: {extra['file']}" for extra in entry.get("also", [])]
        lines += [f"  goes: {entry['goes']}", f"  play-only: {entry['play_only']}"]
        if entry.get("env_note"):
            lines.append(f"  environment: {entry['env_note']}")
        lines += [f"  verified: {entry['verified']}", ""]
    return "\n".join(lines)


def write_profiles(folder: Path, launch: ServerLaunch, *, force: bool = False) -> list[Path]:
    """Write every client's files under folder (and nowhere else). Refuses to replace a different file unless forced."""
    folder = Path(folder).resolve()
    planned = {"README.txt": readme(launch)}
    for entry in clients():
        planned.update(client_files(entry, launch))
    targets = {}
    for relative, text in planned.items():
        target = (folder / relative).resolve()
        if folder != target and folder not in target.parents:
            raise ValueError(f"{relative} would be written outside {folder}")
        if target.exists() and target.read_bytes() != text.encode("utf-8") and not force:
            raise FileExistsError(f"{target} exists and differs; use --force to replace it")
        targets[target] = text
    for target, text in targets.items():
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(text.encode("utf-8"))
    return sorted(targets)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="python -m garage_game_companion.profile",
                                     description="Print or write the play-only MCP client profile for the Garage Game companion.")
    source = parser.add_mutually_exclusive_group()
    source.add_argument("--mock", action="store_true", help="serve the built-in mock game (no game needed)")
    source.add_argument("--session-file", type=Path, help="the game's companion session file (default: the game's own)")
    parser.add_argument("--schema-profile", default="full", choices=["full", "minimal"],
                        help="minimal: tool schemas with only the most widely supported JSON Schema keywords")
    output = parser.add_mutually_exclusive_group()
    output.add_argument("--client", help="print one client's configuration in its own format (see --list-clients)")
    output.add_argument("--write", type=Path, metavar="DIR", help="write every client's files into DIR and nowhere else")
    output.add_argument("--list-clients", action="store_true", help="list the clients with a known format")
    parser.add_argument("--force", action="store_true", help="with --write: replace files that differ")
    options = parser.parse_args(argv)
    launch = server_launch(mock=options.mock, session_file=options.session_file, schema_profile=options.schema_profile)
    if options.list_clients:
        for entry in clients():
            sys.stdout.write(f"{entry['id']:<12} {entry['name']}: {entry['file']}\n")
        return 0
    if options.client:
        try:
            entry = client(options.client)
        except KeyError as error:
            parser.error(str(error.args[0]))
        sys.stdout.write(render_text(entry["format"], launch, entry))
        return 0
    if options.write:
        try:
            written = write_profiles(options.write, launch, force=options.force)
        except (FileExistsError, ValueError) as error:
            sys.stderr.write(f"{error}\n")
            return 1
        for path in written:
            sys.stdout.write(f"{path}\n")
        return 0
    sys.stdout.write(json.dumps(render_document("mcpServers-json", launch), indent=2) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
