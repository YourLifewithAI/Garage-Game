"""Garage Game companion command surface (Run 1, packet A1).

A model-neutral MCP server whose tools map onto the game command contract
(`contracts/game-command.schema.json`), a loopback transport with a per-session
token to the running game, and a mock game host that implements the contract's
receipts, revisions, approvals and untrusted-text rules so the real kernel host
can replace it in Run 2.
"""

__version__ = "0.1.0"
