"""The data-format ids: contracts/schema_ids.py, the repository's one Python table, loaded by its path.

roomscan writes the new ids (`garage_game.<kind>`, the room's pipeline name `garage-game-roomscan`) and reads the founder's
curation and shell spec files in either spelling, the one written before the rename (`enfractal.<kind>`) too
(contracts/README.md, "Schema ids").
"""
from __future__ import annotations

import importlib.util
from types import ModuleType

from .paths import find_repo_root


def _load() -> ModuleType:
    root = find_repo_root()
    if root is None or not (root / "contracts" / "schema_ids.py").is_file():
        raise ImportError("contracts/schema_ids.py is not in the checkout around this package")
    spec = importlib.util.spec_from_file_location("garage_game_schema_ids", root / "contracts" / "schema_ids.py")
    module = importlib.util.module_from_spec(spec)  # type: ignore[arg-type]
    spec.loader.exec_module(module)  # type: ignore[union-attr]
    return module


TABLE = _load()
id_of = TABLE.id_of
is_schema = TABLE.is_schema
GENERATOR = TABLE.GENERATOR[0]
