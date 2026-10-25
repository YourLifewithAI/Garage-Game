"""The data-format ids: contracts/schema_ids.py, the repository's one Python table, loaded by its path.

Every message names its kind in its `schema` field (the link and the wish runner's adapter in `protocol`). The companion
writes the new id (`garage_game.<kind>`) and reads either spelling, the one written before the rename (`enfractal.<kind>`)
too, as the game does (contracts/README.md, "Schema ids"). Readers ask `is_schema(value, kind)`; nothing compares the string.
"""
from __future__ import annotations

import importlib.util
from pathlib import Path
from types import ModuleType

# companion/src/garage_game_companion/ids.py -> the repository root is three levels above the package.
TABLE_PATH = Path(__file__).resolve().parents[3] / "contracts" / "schema_ids.py"


def _load(path: Path = TABLE_PATH) -> ModuleType:
    spec = importlib.util.spec_from_file_location("garage_game_schema_ids", path)
    if spec is None or spec.loader is None:
        raise ImportError(f"the id table is not at {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


TABLE = _load()
id_of = TABLE.id_of
ids_of = TABLE.ids_of
is_schema = TABLE.is_schema
kind_of = TABLE.kind_of
