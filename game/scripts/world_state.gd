extends RefCounted
class_name EnfractalWorldState
## Local prototype state: a hash-pinned room manifest plus sparse player edits.
## This is deliberately not an authority or permission system. Only commands already
## checked by the creation/permission layer may be passed to apply_validated_edit().

const COMPILER = preload("res://scripts/creation_compiler.gd")
const JSON_KERNEL = preload("res://scripts/creation_json.gd")
const SCHEMA_IDS = preload("res://scripts/schema_ids.gd")
## The snapshot's id as written; one written before the rename says it the old way and is read too.
const SCHEMA := "garage_game-world-state"
# Version 3 pins a room manifest (the SHA-256 of room.json's bytes) instead of a map package.
# Earlier versions pinned geography and are refused rather than migrated.
const SCHEMA_VERSION := 3
const MAX_ENTITIES := 256
const MAX_ACTIONS := 2048
const MAX_COMMAND_BYTES := 16384
const MAX_SAVE_BYTES := 4 * 1024 * 1024

var world_id := ""
var room_pin: Dictionary = {}
var revision := 0
var last_error := ""

# Never serialize the room package here. These dictionaries contain only
# authored entities and action receipts for this one world.
var _entities: Dictionary = {}
var _actions: Dictionary = {}


## The pin of a room directory: its room_id and the SHA-256 of its room.json bytes.
static func pin_room(directory: String) -> Dictionary:
	var path := directory.trim_suffix("/") + "/room.json"
	if not FileAccess.file_exists(path):
		return {}
	var parsed: Dictionary = JSON_KERNEL.parse(FileAccess.get_file_as_string(path))
	if not parsed.ok or not parsed.value is Dictionary or not parsed.value.get("room_id") is String:
		return {}
	return {"room_id": parsed.value.room_id, "manifest_sha256": FileAccess.get_sha256(path)}


func initialize(new_world_id: String, pin: Dictionary) -> bool:
	last_error = ""
	if not _valid_token(new_world_id) or not _valid_pin(pin):
		last_error = "invalid_world_identity_or_room"
		return false
	world_id = new_world_id
	room_pin = pin.duplicate(true)
	revision = 0
	_entities.clear()
	_actions.clear()
	return true


func get_entity(entity_id: String) -> Dictionary:
	if not _entities.has(entity_id):
		return {}
	return _entities[entity_id].duplicate(true)


func get_entities() -> Dictionary:
	return _entities.duplicate(true)


func has_action(action_id: String) -> bool:
	return _actions.has(action_id)


func get_snapshot() -> Dictionary:
	return _snapshot(_entities, _actions, revision)


func apply_validated_edit(command: Dictionary) -> Dictionary:
	if world_id.is_empty() or room_pin.is_empty():
		return _failure("world_not_initialized")
	if not _json_safe(command) or COMPILER.canonical_json(command).to_utf8_buffer().size() > MAX_COMMAND_BYTES:
		return _failure("invalid_command_data")
	var action_id: Variant = command.get("action_id")
	var expected_revision: Variant = command.get("expected_revision")
	var operation: Variant = command.get("op")
	if typeof(action_id) != TYPE_STRING or not _valid_token(action_id):
		return _failure("invalid_action_id")
	if typeof(expected_revision) != TYPE_INT or expected_revision < 0:
		return _failure("invalid_expected_revision")
	if typeof(operation) != TYPE_STRING or operation not in ["place", "update", "remove"]:
		return _failure("unsupported_operation")
	var fingerprint := COMPILER.canonical_json(command).sha256_text()
	if _actions.has(action_id):
		var prior: Dictionary = _actions[action_id]
		if prior.get("fingerprint") != fingerprint:
			return _failure("action_id_conflict")
		var repeated: Dictionary = prior["receipt"].duplicate(true)
		repeated["replayed"] = true
		return repeated
	if expected_revision != revision:
		return _failure("revision_conflict")
	if _actions.size() >= MAX_ACTIONS:
		return _failure("action_limit")

	var next_entities := _entities.duplicate(true)
	var entity_id := ""
	if operation == "place":
		if next_entities.size() >= MAX_ENTITIES:
			return _failure("entity_limit")
		if not command.get("entity") is Dictionary:
			return _failure("invalid_entity")
		var new_entity: Dictionary = command["entity"].duplicate(true)
		if new_entity.has("id") or new_entity.is_empty():
			return _failure("invalid_entity")
		entity_id = "edit:" + action_id
		if next_entities.has(entity_id):
			return _failure("entity_id_conflict")
		new_entity["id"] = entity_id
		next_entities[entity_id] = new_entity
	else:
		var requested_id: Variant = command.get("entity_id")
		if typeof(requested_id) != TYPE_STRING or not requested_id.begins_with("edit:") or not next_entities.has(requested_id):
			return _failure("entity_not_found")
		entity_id = requested_id
		if operation == "update":
			if not command.get("entity") is Dictionary:
				return _failure("invalid_entity")
			var replacement: Dictionary = command["entity"].duplicate(true)
			if replacement.is_empty() or (replacement.has("id") and replacement["id"] != entity_id):
				return _failure("invalid_entity")
			replacement["id"] = entity_id
			next_entities[entity_id] = replacement
		else:
			next_entities.erase(entity_id)

	var receipt := {"ok": true, "revision": revision + 1, "entity_id": entity_id, "replayed": false}
	var next_actions := _actions.duplicate(true)
	next_actions[action_id] = {"fingerprint": fingerprint, "receipt": receipt.duplicate(true)}
	if not _json_safe(next_entities) or COMPILER.canonical_json(_snapshot(next_entities, next_actions, revision + 1)).to_utf8_buffer().size() > MAX_SAVE_BYTES:
		return _failure("state_limit")
	_entities = next_entities
	_actions = next_actions
	revision += 1
	return receipt


func save_to_path(path: String) -> bool:
	last_error = ""
	if world_id.is_empty() or not _valid_save_path(path):
		last_error = "invalid_save_path_or_world"
		return false
	var serialized := COMPILER.canonical_json(get_snapshot())
	if serialized.to_utf8_buffer().size() > MAX_SAVE_BYTES:
		last_error = "state_limit"
		return false
	var absolute := ProjectSettings.globalize_path(path)
	var parent := absolute.get_base_dir()
	if DirAccess.make_dir_recursive_absolute(parent) != OK:
		last_error = "save_directory_failed"
		return false
	var temporary := absolute + ".tmp"
	var file := FileAccess.open(temporary, FileAccess.WRITE)
	if file == null:
		last_error = "save_open_failed"
		return false
	file.store_string(serialized)
	file.flush()
	file.close()
	# Replace only after the complete new snapshot has been flushed. A failed rename
	# leaves the previous save intact; the temporary file can be discarded later.
	if DirAccess.rename_absolute(temporary, absolute) != OK:
		last_error = "save_replace_failed"
		return false
	return true


func load_from_path(path: String, expected_pin: Dictionary, expected_world_id := "") -> bool:
	last_error = ""
	if not _valid_save_path(path):
		last_error = "invalid_save_path"
		return false
	if not _valid_pin(expected_pin):
		last_error = "invalid_expected_room"
		return false
	var file := FileAccess.open(path, FileAccess.READ)
	if file == null:
		last_error = "save_open_failed"
		return false
	if file.get_length() > MAX_SAVE_BYTES:
		last_error = "state_limit"
		return false
	var parsed: Dictionary = JSON_KERNEL.parse(file.get_as_text())
	if not parsed.ok or not parsed.value is Dictionary:
		last_error = "invalid_save_json"
		return false
	var snapshot: Dictionary = parsed.value
	if not SCHEMA_IDS.is_schema(snapshot.get("schema"), "world-state") or _json_integer(snapshot.get("version")) != SCHEMA_VERSION:
		last_error = "unsupported_save_version"
		return false
	var loaded_world: Variant = snapshot.get("world_id")
	if typeof(loaded_world) != TYPE_STRING or not _valid_token(loaded_world):
		last_error = "invalid_world_identity"
		return false
	if not expected_world_id.is_empty() and loaded_world != expected_world_id:
		last_error = "world_identity_mismatch"
		return false
	if snapshot.get("room_pin") != expected_pin:
		last_error = "room_pin_mismatch"
		return false
	var loaded_revision := _json_integer(snapshot.get("revision"))
	var loaded_entities: Variant = snapshot.get("entities")
	var loaded_actions: Variant = snapshot.get("actions")
	if loaded_revision < 0 or not loaded_entities is Dictionary or not loaded_actions is Dictionary:
		last_error = "invalid_save_structure"
		return false
	if loaded_entities.size() > MAX_ENTITIES or loaded_actions.size() > MAX_ACTIONS or loaded_actions.size() != loaded_revision:
		last_error = "invalid_save_counts"
		return false
	for id in loaded_entities:
		if typeof(id) != TYPE_STRING or not id.begins_with("edit:") or not _valid_token(id.substr(5)):
			last_error = "invalid_entity_id"
			return false
		var entity: Variant = loaded_entities[id]
		if not entity is Dictionary or entity.get("id") != id or not _json_safe(entity):
			last_error = "invalid_entity"
			return false
	for id in loaded_actions:
		var action: Variant = loaded_actions[id]
		if typeof(id) != TYPE_STRING or not _valid_token(id) or not action is Dictionary:
			last_error = "invalid_action"
			return false
		if not _valid_sha256(action.get("fingerprint")) or not action.get("receipt") is Dictionary:
			last_error = "invalid_action"
			return false
		var old_receipt: Dictionary = action["receipt"]
		var receipt_revision := _json_integer(old_receipt.get("revision"))
		if old_receipt.get("ok") != true or receipt_revision < 1 or receipt_revision > loaded_revision:
			last_error = "invalid_receipt"
			return false
		old_receipt["revision"] = receipt_revision
	# No live state changes until every identity, hash, and structure check succeeds.
	world_id = loaded_world
	room_pin = expected_pin.duplicate(true)
	revision = loaded_revision
	_entities = loaded_entities.duplicate(true)
	_actions = loaded_actions.duplicate(true)
	return true


func _snapshot(entities: Dictionary, actions: Dictionary, current_revision: int) -> Dictionary:
	return {
		"schema": SCHEMA,
		"version": SCHEMA_VERSION,
		"world_id": world_id,
		"room_pin": room_pin.duplicate(true),
		"revision": current_revision,
		"entities": entities.duplicate(true),
		"actions": actions.duplicate(true),
	}


func _valid_pin(pin: Dictionary) -> bool:
	return pin.size() == 2 and pin.get("room_id") is String and _valid_token(pin.room_id) and _valid_sha256(pin.get("manifest_sha256"))


func _valid_sha256(value: Variant) -> bool:
	if typeof(value) != TYPE_STRING or value.length() != 64:
		return false
	for index in range(value.length()):
		if not "0123456789abcdef".contains(value[index]):
			return false
	return true


func _json_integer(value: Variant) -> int:
	if typeof(value) == TYPE_INT:
		return value
	if typeof(value) == TYPE_FLOAT and not is_nan(value) and not is_inf(value) and value >= 0.0 and value <= float(MAX_ACTIONS) and value == floor(value):
		return int(value)
	return -1


func _valid_token(value: String) -> bool:
	if value.is_empty() or value.length() > 100:
		return false
	for index in range(value.length()):
		if not "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._:-".contains(value[index]):
			return false
	return true


func _valid_save_path(path: String) -> bool:
	if not path.begins_with("user://") or path.contains("\\") or path.contains(".."):
		return false
	var relative := path.trim_prefix("user://")
	if relative.is_empty() or relative.begins_with("/") or relative.ends_with("/"):
		return false
	for index in range(relative.length()):
		if not "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-/".contains(relative[index]):
			return false
	return true


func _json_safe(value: Variant, depth := 0) -> bool:
	if depth > 16:
		return false
	match typeof(value):
		TYPE_NIL, TYPE_BOOL, TYPE_INT:
			return true
		TYPE_FLOAT:
			return not is_nan(value) and not is_inf(value)
		TYPE_STRING:
			return value.length() <= 1024
		TYPE_ARRAY:
			if value.size() > 256:
				return false
			for item in value:
				if not _json_safe(item, depth + 1):
					return false
			return true
		TYPE_DICTIONARY:
			if value.size() > 256:
				return false
			for key in value:
				if typeof(key) != TYPE_STRING or key.length() > 128 or not _json_safe(value[key], depth + 1):
					return false
			return true
	return false


func _failure(code: String) -> Dictionary:
	return {"ok": false, "error": code, "revision": revision}
