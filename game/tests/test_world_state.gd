extends SceneTree
## Sparse world edits pinned to the shipped test room's manifest hash.
const GUARD = preload("res://tests/kernel_test_guard.gd")
## Fails the suite on any script or engine error (kernel_test_guard.gd).
var guard = GUARD.new()

const WorldState = preload("res://scripts/world_state.gd")
const ROOM := "res://rooms/test_room"
const SAVE_PATH := "user://tests/world_state_test.json"
const BROKEN_PATH := "user://tests/world_state_broken.json"

var failures := 0


func _initialize() -> void:
	OS.add_logger(guard)
	call_deferred("_run")


func _run() -> void:
	var pin: Dictionary = WorldState.pin_room(ROOM)
	_expect(pin.get("room_id") == "test_room" and pin.get("manifest_sha256") == FileAccess.get_sha256(ROOM + "/room.json"), "room pin is the room id and the SHA-256 of room.json's bytes")
	_expect(WorldState.pin_room("res://rooms/missing_room").is_empty(), "a missing room has no pin")
	var state = WorldState.new()
	_expect(not state.initialize("home-test-room-local", {"room_id": "test_room", "manifest_sha256": "not-a-hash"}), "a malformed room pin is refused")
	_expect(state.initialize("home-test-room-local", pin), "initialize pinned world")
	var entity := {
		"kind": "platform",
		"owner_id": "player:local",
		"provenance": "player-authored",
		"generator_version": 1,
		"transform": {"x_m": 0.4, "y_m": 0.0, "z_m": 0.3, "yaw_deg": 0.0},
		"params": {"width_m": 0.3, "depth_m": 0.4, "thickness_m": 0.03},
	}
	var placement := {"action_id": "action-001", "expected_revision": 0, "op": "place", "actor_id": "player:local", "entity": entity}
	var first: Dictionary = state.apply_validated_edit(placement)
	_expect(first.get("ok") == true and first.get("entity_id") == "edit:action-001" and state.revision == 1, "place gets stable ID and revision")
	var replay: Dictionary = state.apply_validated_edit(placement)
	_expect(replay.get("ok") == true and replay.get("replayed") == true and state.revision == 1, "place retry is idempotent")
	var reordered := {"entity": entity, "op": "place", "expected_revision": 0, "actor_id": "player:local", "action_id": "action-001"}
	_expect(state.apply_validated_edit(reordered).get("replayed") == true, "key order does not change the canonical fingerprint")
	var conflict := placement.duplicate(true)
	conflict["entity"]["params"]["width_m"] = 0.9
	_expect(state.apply_validated_edit(conflict).get("error") == "action_id_conflict", "same action ID cannot change intent")
	var stale := {"action_id": "action-stale", "expected_revision": 0, "op": "remove", "actor_id": "player:local", "entity_id": "edit:action-001"}
	_expect(state.apply_validated_edit(stale).get("error") == "revision_conflict", "stale edit rejected")
	var updated_entity := entity.duplicate(true)
	updated_entity["params"]["width_m"] = 0.35
	var revision := {"action_id": "action-002", "expected_revision": 1, "op": "update", "actor_id": "player:local", "entity_id": "edit:action-001", "entity": updated_entity}
	_expect(state.apply_validated_edit(revision).get("ok") == true and state.get_entity("edit:action-001")["params"]["width_m"] == 0.35, "update retains stable ID")
	var copy := state.get_entity("edit:action-001")
	copy["params"]["width_m"] = 2.0
	_expect(state.get_entity("edit:action-001")["params"]["width_m"] == 0.35, "read does not expose mutable state")
	_expect(state.save_to_path(SAVE_PATH), "first snapshot save")
	var removal := {"action_id": "action-003", "expected_revision": 2, "op": "remove", "actor_id": "player:local", "entity_id": "edit:action-001"}
	_expect(state.apply_validated_edit(removal).get("ok") == true and state.get_entity("edit:action-001").is_empty(), "remove persists a tombstone receipt")
	_expect(state.apply_validated_edit(removal).get("replayed") == true and state.revision == 3, "remove retry remains idempotent")
	_expect(state.save_to_path(SAVE_PATH), "atomic replacement of existing snapshot")
	var restored = WorldState.new()
	var loaded: bool = restored.load_from_path(SAVE_PATH, pin, "home-test-room-local")
	if not loaded:
		print("Reload failed: ", restored.last_error)
	_expect(loaded, "reload saved world")
	_expect(restored.revision == 3 and restored.get_entities().is_empty() and restored.has_action("action-003"), "edits and receipts survive reload")
	_expect(restored.apply_validated_edit(removal).get("replayed") == true, "idempotency survives reload")
	var other_room: Dictionary = pin.duplicate(true)
	other_room["manifest_sha256"] = "0".repeat(64)
	_expect(not restored.load_from_path(SAVE_PATH, other_room) and restored.last_error == "room_pin_mismatch", "a re-exported room (different manifest bytes) is refused")
	var renamed: Dictionary = pin.duplicate(true)
	renamed["room_id"] = "other_room"
	_expect(not restored.load_from_path(SAVE_PATH, renamed) and restored.last_error == "room_pin_mismatch", "another room id is refused even with the same bytes")
	_expect(restored.revision == 3, "failed load preserves live state")
	_expect(not restored.load_from_path(SAVE_PATH, pin, "other-world"), "cross-world load rejected")
	_expect(not restored.save_to_path("user://../escape.json"), "path traversal rejected")
	# The rename (decision C): written with the new id; a snapshot written before it, with the old one, still loads.
	var written := FileAccess.get_file_as_string(SAVE_PATH)
	_expect(written.contains("\"schema\":\"garage_game-world-state\""), "the snapshot is written with the new id")
	for spelling in ["enfractal-world-state", "garagegame-world-state"]:
		var renamed_file := FileAccess.open(BROKEN_PATH, FileAccess.WRITE)
		if renamed_file != null:
			renamed_file.store_string(written.replace("garage_game-world-state", spelling))
			renamed_file.close()
		var other = WorldState.new()
		var read: bool = other.load_from_path(BROKEN_PATH, pin, "home-test-room-local")
		if spelling.begins_with("enfractal"):
			_expect(read and other.revision == 3, "a snapshot written before the rename loads")
		else:
			_expect(not read and other.last_error == "unsupported_save_version", "a snapshot named with another id is refused")
	var old_snapshot: Dictionary = state.get_snapshot()
	old_snapshot["version"] = 2
	var old_file := FileAccess.open(BROKEN_PATH, FileAccess.WRITE)
	if old_file != null:
		old_file.store_string(JSON.stringify(old_snapshot))
		old_file.close()
	_expect(not restored.load_from_path(BROKEN_PATH, pin) and restored.last_error == "unsupported_save_version", "map-pinned version 2 saves are refused, not migrated")
	var bad := FileAccess.open(BROKEN_PATH, FileAccess.WRITE)
	if bad != null:
		bad.store_string("{not json")
		bad.close()
	_expect(not restored.load_from_path(BROKEN_PATH, pin), "corrupt save rejected")
	_expect(restored.revision == 3, "corrupt save does not mutate live state")
	DirAccess.remove_absolute(ProjectSettings.globalize_path(SAVE_PATH))
	DirAccess.remove_absolute(ProjectSettings.globalize_path(BROKEN_PATH))
	print("World state tests: ", "PASS" if failures == 0 else "%d failure(s)" % failures)
	quit(guard.exit_code(failures != 0))


func _expect(condition: bool, description: String) -> void:
	if condition:
		return
	failures += 1
	push_error("World state test failed: " + description)
