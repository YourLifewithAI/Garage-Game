"""The data-format ids: the one table every Python reader and writer in this repository uses.

Every document names its kind in its `schema` field (a protocol, in its `protocol` field). The Garage Game writes
`garage_game.<kind>`. Files written before the rename (decision C of docs/runs/GARAGE-GAME-RENAME.md, 21 October 2026)
say `enfractal.<kind>`, and pinned files (rooms, assets, presets, rules packs, the characters) keep that spelling for as
long as they exist, so readers accept both ids for good, not for a transition. Writers write `id_of(kind)`; readers ask
`is_schema(value, kind)` and never compare the string themselves. The kinds and both spellings are listed in
contracts/README.md ("Schema ids").

C# and GDScript hold the same table (game/scripts/native/SchemaIds.cs, game/scripts/schema_ids.gd);
contracts/tests/test_schema_ids.py checks that the three agree. Standard library only: the companion, roomscan, the
landscape exporter, the character converter and the playtest reader load this file by its path.
"""
from __future__ import annotations

# kind -> (the id writers write, the id written before the rename). One line per kind; the alignment test reads them.
IDS: dict[str, tuple[str, str]] = {
    # The contracts (contracts/*.schema.json), validated by contracts/validate.py.
    "command": ("garage_game.command", "enfractal.command"),
    "query": ("garage_game.query", "enfractal.query"),
    "result": ("garage_game.result", "enfractal.result"),
    "creation": ("garage_game.creation", "enfractal.creation"),
    "room": ("garage_game.room", "enfractal.room"),
    "asset": ("garage_game.asset", "enfractal.asset"),
    "style": ("garage_game.style", "enfractal.style"),
    "room_state": ("garage_game.room_state", "enfractal.room_state"),
    "island_rules": ("garage_game.island_rules", "enfractal.island_rules"),
    "team": ("garage_game.team", "enfractal.team"),
    "player_modes": ("garage_game.player_modes", "enfractal.player_modes"),
    "glow_progress": ("garage_game.glow_progress", "enfractal.glow_progress"),
    "playtest_manifest": ("garage_game.playtest_manifest", "enfractal.playtest_manifest"),
    "last_game": ("garage_game.last_game", "enfractal.last_game"),
    "carried_builds": ("garage_game.carried_builds", "enfractal.carried_builds"),
    "companion_session": ("garage_game.companion_session", "enfractal.companion_session"),
    "companion_link": ("garage_game.companion_link", "enfractal.companion_link"),
    "stamina_ledger_fixture": ("garage_game.stamina_ledger_fixture", "enfractal.stamina_ledger_fixture"),
    # The game's own files: the island save, the GDScript kernel's world snapshot, the wish queue, the legacy-save ledger,
    # the characters' description and rig sidecar, the character sheets' index.
    "creation-world": ("garage_game.creation-world", "enfractal.creation-world"),
    "world-state": ("garage_game-world-state", "enfractal-world-state"),
    "wish_queue": ("garage_game.wish_queue", "enfractal.wish_queue"),
    "legacy_migration": ("garage_game.legacy_migration", "enfractal.legacy_migration"),
    "character.draft": ("garage_game.character.draft", "enfractal.character.draft"),
    "character.rig": ("garage_game.character.rig", "enfractal.character.rig"),
    "character_sheets": ("garage_game.character_sheets", "enfractal.character_sheets"),
    # The companion: its clients file and the wish runner's adapter protocol.
    "companion_clients": ("garage_game.companion_clients", "enfractal.companion_clients"),
    "wish_adapter": ("garage_game.wish_adapter", "enfractal.wish_adapter"),
    # The capture pipeline (pipeline/roomscan).
    "inventory": ("garage_game.inventory", "enfractal.inventory"),
    "inventory_curation": ("garage_game.inventory_curation", "enfractal.inventory_curation"),
    "shell_spec": ("garage_game.shell_spec", "enfractal.shell_spec"),
    "shell_plan": ("garage_game.shell_plan", "enfractal.shell_plan"),
    "coverage_report": ("garage_game.coverage_report", "enfractal.coverage_report"),
    "capture_session": ("garage_game.capture_session", "enfractal.capture_session"),
    # The landscape package (pipeline/landscape/harness), which its owners move to the new id.
    "landscape": ("garage_game.landscape", "enfractal.landscape"),
    "landscape.render-receipt": ("garage_game.landscape.render-receipt", "enfractal.landscape.render-receipt"),
    # Other owners' files, named in the rename's change requests: the Look lane's review cameras, the recipe runner's
    # receipt, the painterly art's metadata (whose committed v1 assets keep the old id).
    "look_review_cameras": ("garage_game.look_review_cameras", "enfractal.look_review_cameras"),
    "recipe_receipt": ("garage_game.recipe_receipt", "enfractal.recipe_receipt"),
    "art.asset.v1": ("garage_game.art.asset.v1", "enfractal.art.asset.v1"),
}

# Names that are not schema ids but carry the game's name inside a data format, written new and read either way.
GENERATOR = ("garage-game-roomscan", "enfractal-roomscan")  # a captured room's generator.name
GLB_PART_KEYS = ("garage_game_part", "enfractal_part")  # a character GLB node's extras key
GLB_MATERIAL_KEYS = ("garage_game_material", "enfractal_material")  # a character GLB material's extras key

_BY_ID = {spelling: kind for kind, pair in IDS.items() for spelling in pair}


def id_of(kind: str) -> str:
    """The id a writer writes for `kind` (a KeyError for a kind the table does not have)."""
    return IDS[kind][0]


def ids_of(kind: str) -> tuple[str, str]:
    """Both spellings a reader accepts for `kind`, the new one first."""
    return IDS[kind]


def is_schema(value: object, kind: str) -> bool:
    """Whether `value` names `kind` in either spelling. A kind the table does not have is a KeyError, never False."""
    return isinstance(value, str) and value in IDS[kind]


def kind_of(value: object) -> str | None:
    """The kind `value` names in either spelling, or None for anything else (another string, a non-string)."""
    return _BY_ID.get(value) if isinstance(value, str) else None


def is_old(value: object) -> bool:
    """Whether `value` is an id in the spelling written before the rename."""
    kind = kind_of(value)
    return kind is not None and value == IDS[kind][1]
