extends RefCounted
## The data-format ids for the GDScript kernel (contracts/README.md, "Schema ids"; the Python table is
## contracts/schema_ids.py, C#'s scripts/native/SchemaIds.cs, and contracts/tests/test_schema_ids.py checks they agree).
## Writers write id_of(kind), the garage_game id; readers ask is_schema(value, kind), which also accepts the enfractal
## spelling written before the rename (decision C, 24 October 2026) and kept by pinned files and old saves.

## kind: [the id written, the id written before the rename]
const IDS := {
	"command": ["garage_game.command", "enfractal.command"],
	"result": ["garage_game.result", "enfractal.result"],
	"creation": ["garage_game.creation", "enfractal.creation"],
	"creation-world": ["garage_game.creation-world", "enfractal.creation-world"],
	"world-state": ["garage_game-world-state", "enfractal-world-state"],
}


## The id a writer writes for kind.
static func id_of(kind: String) -> String:
	assert(IDS.has(kind), "no schema kind " + kind)
	return IDS[kind][0]


## Whether value names kind in either spelling.
static func is_schema(value: Variant, kind: String) -> bool:
	assert(IDS.has(kind), "no schema kind " + kind)
	return value is String and (value == IDS[kind][0] or value == IDS[kind][1])
