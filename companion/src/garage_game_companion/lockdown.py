"""Process lockdown for the companion MCP server: defence in depth, not an operating-system sandbox.

Having no file, shell or URL tools is the boundary. This is a second layer, for the case where the
server's own code or a dependency is tricked by input into calling something it should not. Once
the server has loaded the contracts and knows where the session file is, it

1. clears its own environment down to what Windows sockets need, so no inherited developer
   secret (API keys, tokens, cloud credentials) exists in the process to leak;
2. installs a Python audit hook (PEP 578) that refuses, for the rest of the process's life, these
   audited standard-library routes to the outside world:
   - `open` for writing anything, and for reading anything except the session file, the Python
     installation (with its site-packages), the package and the contracts; listing directories
     outside those; deleting, renaming, copying, linking, creating directories, changing modes;
   - starting or signalling processes (subprocess, os.system, exec, spawn, startfile, kill);
   - every audited `_winapi` call (CreateFile, CreateProcess, OpenProcess, TerminateProcess, pipes,
     junctions, file mappings) and every audited `ctypes` call (loading a library, calling a
     foreign function);
   - socket connects, binds and sends to anything but a loopback IP literal (127.0.0.0/8, ::1),
     name lookups of anything but such a literal ("localhost" included: no name is resolved), and
     URL fetching, the browser, the registry, sqlite and environment changes;
3. wraps the Windows event loop's connect, sendto and named-pipe connect, which go through
   `_overlapped` without an audit event, so asyncio cannot reach a non-loopback address or a named
   pipe either.

What it does not do, and the tests in companion/tests/test_lockdown.py pin both lists:
- An audit hook sees only what Python audits. Calls with no audit event are not covered: os.stat
  and os.path.exists still tell whether a path exists anywhere. Native extension modules already
  loaded in the process are not covered either: on Windows the MCP SDK's stdio transport imports
  pywin32 (win32api, win32job), whose functions raise no audit events.
- Paths are compared as text after normalising case and '..'. A symbolic link or junction that
  already exists inside an allowed folder is followed. Nothing in the process can create one.
- It cannot be undone, but code running in the process with the intent to escape (rather than
  being tricked into one call) can get around it; that is outside what an audit hook can stop.
- The process still runs with the player's full account rights. The MCP client should launch the
  server as the player's ordinary account with no extra privileges; an operating-system sandbox
  (a restricted token, an AppContainer, a separate account) is the step that would make this a
  containment boundary (docs/companion/SECURITY.md).
"""
from __future__ import annotations

import ipaddress
import os
import sys
from pathlib import Path

# What Windows needs for Winsock and the event loop; nothing else survives the scrub.
KEEP_ENV = ("SYSTEMROOT", "SystemRoot", "WINDIR", "windir")

_DENIED_EVENTS = (
    "subprocess.Popen", "os.system", "os.exec", "os.posix_spawn", "os.spawn", "os.startfile", "os.fork",
    "os.forkpty", "pty.spawn", "os.kill", "os.killpg", "signal.pthread_kill",
    "os.remove", "os.unlink", "os.rename", "os.replace", "os.rmdir", "os.mkdir", "os.makedirs", "os.chmod",
    "os.chown", "os.truncate", "os.link", "os.symlink", "os.utime", "os.chdir", "os.chflags", "os.lchflags",
    "os.lchmod", "os.lchown", "os.mkfifo", "os.mknod", "os.removexattr", "os.setxattr",
    "shutil.copyfile", "shutil.copymode", "shutil.copystat", "shutil.copytree", "shutil.move", "shutil.rmtree",
    "shutil.make_archive", "shutil.unpack_archive", "tempfile.mkstemp", "tempfile.mkdtemp",
    "urllib.Request", "webbrowser.open", "sqlite3.connect", "sqlite3.connect/handle",
    "ftplib.connect", "smtplib.connect", "imaplib.open", "poplib.connect", "nntplib.connect",
    "telnetlib.Telnet.open", "os.putenv", "os.unsetenv",
)
# Every audited _winapi and ctypes call: the server needs none of them once it is running.
_DENIED_PREFIXES = ("winreg.", "_winapi.", "ctypes.", "msvcrt.open_osfhandle", "os.posix_spawn", "os.spawn")
_NETWORK_HOST_EVENTS = ("socket.connect", "socket.bind", "socket.sendto", "socket.sendmsg")
_LOOKUP_EVENTS = ("socket.getaddrinfo", "socket.gethostbyname", "socket.gethostbyname_ex", "socket.gethostbyaddr",
                  "socket.getnameinfo")
_LIST_EVENTS = ("os.listdir", "os.scandir", "glob.glob", "glob.glob/2")
_WRITE_FLAGS = os.O_WRONLY | os.O_RDWR | os.O_APPEND | os.O_CREAT | os.O_TRUNC

_installed = False


class SandboxViolation(PermissionError):
    pass


def scrub_environment() -> list[str]:
    """Remove every environment variable except the few Windows sockets need. Returns removed names."""
    removed = [key for key in list(os.environ) if key not in KEEP_ENV]
    for key in removed:
        del os.environ[key]
    return removed


def _norm(path) -> str | None:
    if isinstance(path, int):
        return None
    try:
        text = os.fsdecode(path)
    except TypeError:
        return ""
    return os.path.normcase(os.path.abspath(text))


def _under(path: str, roots: tuple[str, ...]) -> bool:
    return any(path == root or path.startswith(root.rstrip("\\/") + os.sep) for root in roots)


def _host_is_loopback(host) -> bool:
    """True only for a loopback IP literal (or None: a lookup with no host resolves nothing remote).

    Names are refused, "localhost" included: whatever a name resolves to is decided outside this
    process, so the hook could not vouch for it."""
    if host is None:
        return True
    if isinstance(host, bytes):
        host = host.decode("ascii", "replace")
    if not isinstance(host, str):
        return False
    try:
        return ipaddress.ip_address(host.split("%", 1)[0]).is_loopback
    except ValueError:
        return False


def _address_is_loopback(address) -> bool:
    if isinstance(address, tuple) and address:
        return _host_is_loopback(address[0])
    return False  # AF_UNIX paths and anything else are refused


def install(read_files: list[Path] = (), read_roots: list[Path] = ()) -> None:
    """Install the audit hook. Idempotent per process; cannot be undone."""
    global _installed
    if _installed:
        return
    # The Python installation and its site-packages (where the stdlib and every dependency live),
    # plus the roots the caller names (the package and the contracts). Deliberately not the
    # current directory, which is wherever the MCP client happened to start the server.
    roots = {sys.prefix, sys.base_prefix, sys.exec_prefix, sys.base_exec_prefix}
    roots.update(str(r) for r in read_roots)
    allowed_roots = tuple(sorted({os.path.normcase(os.path.abspath(r)) for r in roots}))
    allowed_files = frozenset(os.path.normcase(os.path.abspath(str(f))) for f in read_files)
    null_device = os.path.normcase(os.path.abspath(os.devnull))

    def deny(event: str) -> None:
        raise SandboxViolation(f"blocked by the Garage Game companion sandbox: {event}")

    def hook(event: str, args: tuple) -> None:
        if event == "open":
            path, mode, flags = (tuple(args) + (None, None, None))[:3]
            target = _norm(path)
            if target is None or target == null_device:
                return  # an already-open descriptor, or the null device
            writing = (isinstance(mode, str) and any(c in mode for c in "wax+")) or \
                      (isinstance(flags, int) and flags & _WRITE_FLAGS)
            if writing:
                deny(f"open for writing {os.path.basename(target)}")
            if target in allowed_files or _under(target, allowed_roots):
                return
            deny(f"open {os.path.basename(target)}")
        elif event in _DENIED_EVENTS or event.startswith(_DENIED_PREFIXES):
            deny(event)
        elif event in _NETWORK_HOST_EVENTS:
            if not _address_is_loopback(args[1] if len(args) > 1 else None):
                deny(event)
        elif event in _LOOKUP_EVENTS:
            if not _host_is_loopback(args[0] if args else None):
                deny(event)
        elif event == "http.client.connect":
            if not _host_is_loopback(args[1] if len(args) > 1 else None):
                deny(event)
        elif event in _LIST_EVENTS:
            target = _norm(args[0] if args else ".")
            if target is not None and not _under(target, allowed_roots):
                deny(event)

    sys.dont_write_bytecode = True
    sys.addaudithook(hook)
    _guard_event_loop()
    _installed = True


def _guard_event_loop() -> None:
    """The Windows proactor connects, sends and opens named pipes through _overlapped, which raises
    no audit event."""
    try:
        from asyncio import windows_events
    except ImportError:  # POSIX: the selector loop calls socket.connect, which the hook sees
        return
    proactor = windows_events.IocpProactor
    original_connect, original_sendto = proactor.connect, proactor.sendto

    def connect(self, conn, address):
        if not _address_is_loopback(address):
            raise SandboxViolation("blocked by the Garage Game companion sandbox: event loop connect")
        return original_connect(self, conn, address)

    def sendto(self, conn, buf, flags=0, addr=None):
        if addr is not None and not _address_is_loopback(addr):
            raise SandboxViolation("blocked by the Garage Game companion sandbox: event loop sendto")
        return original_sendto(self, conn, buf, flags, addr)

    def connect_pipe(self, address):
        # A named pipe reaches other local services (agents, engines); the server needs none.
        raise SandboxViolation("blocked by the Garage Game companion sandbox: event loop named pipe")

    proactor.connect = connect
    proactor.sendto = sendto
    proactor.connect_pipe = connect_pipe
