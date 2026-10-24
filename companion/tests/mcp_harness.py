"""An in-process MCP client wired to the companion server, which talks to a mock game over the real link."""
from __future__ import annotations

import json
import tempfile
from contextlib import AsyncExitStack
from pathlib import Path

from support import contract_problems, contracts, new_host

from garage_game_companion.link import LinkClient, LinkServer
from garage_game_companion.server import RESULT_PREAMBLE, Adapter, build_server


class McpHarness:
    def __init__(self, *, host=None, handler=None, mode: str = "auto", start_game: bool = True, using=None,
                 problems=contract_problems, **adapter_kwargs):
        """`using` and `problems` put the adapter on other contracts (and their validator), as a host built
        with new_host(using=...) is."""
        self.host = host if host is not None else new_host(using=using)
        self.contracts = using or contracts()
        self.problems = problems
        self.handler = handler
        self.mode = mode
        self.start_game = start_game
        self.adapter_kwargs = adapter_kwargs
        self.results: list[dict] = []

    async def __aenter__(self) -> "McpHarness":
        from mcp import Client

        self._stack = AsyncExitStack()
        tmp = self._stack.enter_context(tempfile.TemporaryDirectory(prefix="garage-game-mcp-"))
        self.session_path = Path(tmp) / "session.json"
        self.game = LinkServer(self.handler or self.host.handle, self.host.room_id,
                               on_session=None if self.handler else self.host.session_event)
        if self.start_game:
            await self.game.start(self.session_path)
        self.link = LinkClient.from_file(self.session_path)
        self.adapter = Adapter(self.contracts, self.link, **self.adapter_kwargs)
        self.server = build_server(self.adapter)
        self.client = await self._stack.enter_async_context(Client(self.server, mode=self.mode))
        return self

    async def __aexit__(self, *exc) -> None:
        await self.link.close()
        await self.game.close()
        await self._stack.aclose()

    async def call(self, name: str, arguments: dict | None = None) -> dict:
        """Call a tool and return its structured result, after checking both views of it agree."""
        response = await self.client.call_tool(name, arguments or {})
        text = response.content[0].text
        preamble, _, line = text.partition("\n")
        assert preamble == RESULT_PREAMBLE, preamble
        assert "\n" not in line and line.isascii(), "the result line must be one ASCII line"
        parsed = json.loads(line)
        assert parsed == response.structured_content, "text and structured content differ"
        assert response.is_error == (not parsed["ok"])
        problems = self.problems(parsed)
        assert not problems, problems
        self.results.append(parsed)
        return parsed
