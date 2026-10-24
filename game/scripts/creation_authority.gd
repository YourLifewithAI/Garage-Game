extends RefCounted
## Host-owned command boundary for creations and locks in one room. The principal argument must come
## from a trusted adapter (Kernel/CommandHost.cs or the invention runtime); request fields cannot grant
## identity, permissions, costs, ownership or approval. Room bounds and lockable objects come from the
## room data, support heights from a physics surface query, and protection from locks: a locked
## object or creation is a no-build, no-effect zone until the player unlocks it.

const COMPILER = preload("res://scripts/creation_compiler.gd")
const JSON_KERNEL = preload("res://scripts/creation_json.gd")
const TEXT = preload("res://scripts/creation_text.gd")
const SCHEMA_IDS = preload("res://scripts/schema_ids.gd")
## The island save's id as written; a save written before the rename says it the old way and is read too.
const SCHEMA := "garage_game.creation-world"
## Version 2 pins a room manifest instead of a map, uses contract principals and adds locks. Version 3 adds
## the compacted receipt index and the checkpoints. Version 4 adds object_poses: where play has moved the
## room's objects (the sandbox verbs, Run 2 P3). Version 5 adds team: the team's journal and discovered map, a block
## the trusted host owns and checks (team_check). Version 6 adds nature: the island's ground field, weather, fire and
## their undo steps, another block the trusted host owns and checks (nature_check). Version 7 (the teams packet, M3) keeps
## the same blocks; the island save now lives under its team and mode, and its team block also says whose save it is and
## how it plays (team_id, mode and, in Challenge, difficulty: room state's x_team), checked by the host's team_check.
## Earlier saves still load (with none of the later blocks).
const VERSION := 7
const LOADABLE_VERSIONS := [2, 3, 4, 5, 6, 7]
const STYLE_VERSION := "painterly_v1"
const MAX_SAVE_BYTES := 4 * 1024 * 1024
## Durable receipts in the ledger. The last PLAYER_RECEIPT_RESERVE slots are the player's alone, so a
## companion that fills the ledger can never block the player's own lock, placement or moderation; a
## room.checkpoint compacts the ledger.
const MAX_RECEIPTS := 2048
const PLAYER_RECEIPT_RESERVE := 256
## Compacted receipts keep only their revision and a 64-bit fingerprint prefix, enough to replay and to
## refuse a conflicting reuse; the oldest are forgotten first.
const MAX_COMPACTED := 4096
const MAX_CHECKPOINTS := 16
## Transient activation receipts per principal for the session; the oldest are forgotten first, so an
## activation never fails for capacity.
const MAX_TRANSIENT_PER_PRINCIPAL := 1024
const MAX_LOCKS := 256
const MAX_REQUEST_BYTES := 65536
## A room lists at most 4,096 lockable entities of about nine JSON values each.
const MAX_ROOM_NODES := 65536
const MAX_TARGETS := 64
const PLAYER := "player:local"
const COMPANION := "companion:local"
const PRINCIPALS := [PLAYER, COMPANION]
## Footprint support samples (corners and origin) may differ in height by at most this much.
const MAX_SURFACE_STEP_M := 0.05
## A height hint probes from this far above it, so a spot under a table stays under the table.
const SURFACE_PROBE_M := 0.05
const COORDINATE_LIMIT_M := 1000.0
const OWNER_LIMITS := {"instances": 8, "parts": 96, "nodes": 64, "edges": 128, "fields": 16, "lights": 16, "rotors": 16}
const WORLD_LIMITS := {"instances": 16, "parts": 192, "nodes": 128, "edges": 256, "fields": 32, "lights": 32, "rotors": 32}
const DEFAULT_ROLES := {PLAYER: "owner", COMPANION: "editor"}
## The contract ops whose durable receipts carry result data the trusted host wrote (nature changes and their undo), and
## the mechanics round's player ops the host commits through the nature block: a Fractal picked up and a log or stone gathered.
const NATURE_OPS := ["nature.cast", "nature.trail", "nature.repair", "room.undo", "item.pickup", "item.gather"]

var revision := 0
var permission_revision := 0
var last_error := ""
# Optional trusted host seams, bound by the playable runtime.
var occupancy_query := Callable()
var activation_query := Callable()
# A trusted host adapter may replace the local file with a durable transaction.
# The sink must acknowledge commit before returning ok; errors retain old memory.
var persistence_sink := Callable()
var access_guard := Callable()
## The trusted host's scene seam: called with every object pose (a copy) whenever they change, and while a
## save loads before its creations are checked against the room's surfaces, so the scene stays derived from
## this state. Bound by the command host through the runtime; optional.
var pose_sink := Callable()
## The trusted host's check of a save's object poses against the room as it is (asset geometry, collision and
## support), called once the poses reach the scene and before the save's creations are checked: {ok} or
## {ok: false, message}. A refused pose refuses the whole save, as an unplaceable creation does. Optional.
var pose_check := Callable()
## The trusted host's check of a save's team block (its journal and discovered map) and its revision before the save
## is believed: {ok} or {ok: false, message}. Optional; without it any dictionary loads.
var team_check := Callable()
## The trusted host's check of a save's nature block and its revision before the save is believed: {ok} or {ok: false,
## message}. Optional; without it any dictionary loads.
var nature_check := Callable()
var _roles := DEFAULT_ROLES.duplicate()
var _consent := {PLAYER: false, COMPANION: false}
var _instances: Dictionary = {}
var _receipts: Dictionary = {}
var _compacted: Dictionary = {}
var _checkpoints: Array = []
var _activation_receipts: Dictionary = {}
var _locks: Dictionary = {}
var _entity_revisions: Dictionary = {}
## Where play has moved room objects: {"obj:x": {position_m, rotation, bounds: {min_m, max_m}}}. An object
## absent here stands where the room manifest puts it.
var _object_poses: Dictionary = {}
## The team's journal and discovered map ({} until the host writes them): opaque here, owned and checked by the host.
var _team: Dictionary = {}
## The island's nature ({} until the host writes it): opaque here, owned and checked by the host.
var _nature: Dictionary = {}
var _surface := Callable()
var _room_id := ""
var _manifest_sha256 := ""
var _room_min := Vector3.ZERO
var _room_max := Vector3.ZERO
var _room_entities: Dictionary = {}
var _save_path := ""
var _next_id := 1
var _configured := false
var _load_required := false
var _runtime_events: Array = []
var _last_budget_time := -1.0


## room: {room_id, manifest_sha256, bounds: {min_m, max_m}, entities: {"obj:x"|"shell:x": {kind, min_m, max_m}}}.
## surface_query(x, z, from_y) returns {ok: true, height_m, entity_id} for the first support below from_y.
func configure(room: Dictionary, surface_query: Callable, save_path: String) -> Dictionary:
	_configured = false
	_instances.clear()
	_receipts.clear()
	_compacted.clear()
	_checkpoints.clear()
	_activation_receipts.clear()
	_locks.clear()
	_entity_revisions.clear()
	_object_poses.clear()
	_team = {}
	_nature = {}
	_runtime_events.clear()
	_last_budget_time = -1.0
	revision = 0
	permission_revision = 0
	_next_id = 1
	_roles = DEFAULT_ROLES.duplicate()
	_consent = {PLAYER: false, COMPANION: false}
	var checked := _check_room(room)
	if not checked.ok or not surface_query.is_valid():
		return _failure("configuration_invalid", "room", "A pinned room with bounds and a surface query is required.")
	if not save_path.begins_with("user://") or ".." in save_path or save_path.ends_with("/"):
		return _failure("save_path_invalid", "save_path", "Creation saves must be inside the application user folder.")
	_room_id = room.room_id
	_manifest_sha256 = room.manifest_sha256
	_room_min = checked.min
	_room_max = checked.max
	_room_entities = checked.entities
	_surface = surface_query
	_save_path = save_path
	_load_required = FileAccess.file_exists(_save_path)
	_configured = true
	return {"ok": true, "load_required": _load_required}


## receipt_meta comes only from the trusted command host: {fingerprint, op, at_utc, approved_by}.
## fingerprint replaces the internal one (the host fingerprints the contract command as received);
## approved_by "player:local" means the player approved this held command with a click.
func submit(principal: String, request: Dictionary, receipt_meta: Dictionary = {}) -> Dictionary:
	if not _available():
		return _failure("save_not_ready", "", "Load the saved room successfully before changing it.")
	if not _roles.has(principal):
		return _failure("principal_unknown", "principal", "The host did not admit this principal.")
	# The team and nature blocks are the trusted host's, bounded by the save's limit; the request's own limit covers the rest.
	var bounded: Dictionary = request
	for block in ["team", "nature"]:
		if request.has(block):
			if not request[block] is Dictionary or not _json_safe(request[block], MAX_SAVE_BYTES):
				return _failure("request_invalid", block, "The " + block + " block must be bounded finite JSON data.")
			if bounded.has(block):
				bounded = bounded.duplicate()
				bounded.erase(block)
	if not _json_safe(bounded, MAX_REQUEST_BYTES) or COMPILER.canonical_json(bounded).to_utf8_buffer().size() > MAX_REQUEST_BYTES:
		return _failure("request_invalid", "", "The command must contain bounded finite JSON data.")
	var action: Variant = request.get("action_id")
	if not _token(action):
		return _failure("action_id_invalid", "action_id", "Use a unique action ID of at most 64 letters, digits, underscores or hyphens.")
	var meta := _receipt_meta(principal, receipt_meta)
	if meta.is_empty():
		return _failure("request_invalid", "receipt_meta", "The host supplied invalid receipt metadata.")
	var key := principal + "|" + String(action)
	var fingerprint: String = receipt_meta.get("fingerprint", COMPILER.canonical_json(request).sha256_text())
	# Identity is checked above, but committed retries precede current permissions
	# and revision checks. The key is principal-bound, never globally action-bound.
	for ledger in [_receipts, _activation_receipts]:
		if ledger.has(key):
			var prior: Dictionary = ledger[key]
			if prior.fingerprint != fingerprint:
				return _failure("action_id_conflict", "action_id", "This action ID was already used for different content.")
			var receipt: Dictionary = prior.receipt.duplicate(true)
			receipt.replayed = true
			return receipt
	if _compacted.has(key):
		if String(_compacted[key][1]) != fingerprint.left(16):
			return _failure("action_id_conflict", "action_id", "This action ID was already used for different content.")
		return {"ok": true, "instance_id": "", "revision": int(_compacted[key][0]), "permission_revision": permission_revision, "replayed": true, "compacted": true, "affected": [], "created": []}
	var op: Variant = request.get("op")
	if op not in ["place", "revise", "remove", "activate", "lock", "unlock", "checkpoint", "move", "note", "nature"]:
		return _failure("operation_invalid", "op", "Choose place, revise, remove, activate, lock, unlock, checkpoint, move, note or nature.")
	var allowed := ["op", "action_id", "expected_revision", "expected_permission_revision"]
	if op in ["place", "revise"]:
		allowed.append_array(["source", "x_m", "z_m", "yaw_deg", "y_m", "on"])
	if op in ["revise", "remove", "activate"]:
		allowed.append("instance_id")
	if op in ["place", "revise", "remove"]:
		allowed.append("team")
	if op in ["lock", "unlock"]:
		allowed.append("targets")
	if op == "checkpoint":
		allowed.append("label")
	if op == "move":
		allowed.append_array(["target", "position_m", "rotation", "bounds"])
	if op == "note":
		allowed.append_array(["team", "entry_id"])
	# A removal may clear what fire did to the creation, in the same write.
	if op == "remove":
		allowed.append("nature")
	if op == "nature":
		allowed.append_array(["nature", "affected", "data", "undo"])
	for field in request:
		if field not in allowed:
			return _failure("field_unknown", String(field), "Identity, ownership, height, approval and compiled costs are assigned by the host.")
	if not _whole(request.get("expected_revision")) or int(request.expected_revision) != revision:
		return _failure("revision_conflict", "expected_revision", "The room changed. Refresh before confirming this edit.")
	if not _whole(request.get("expected_permission_revision")) or int(request.expected_permission_revision) != permission_revision:
		return _failure("permission_revision_conflict", "expected_permission_revision", "Permissions changed. Refresh before confirming this edit.")
	meta = _complete_meta(meta, op)
	if op in ["lock", "unlock"]:
		return _submit_lock(principal, op, request.get("targets"), key, fingerprint, meta)
	if op == "checkpoint":
		return _submit_checkpoint(principal, request.get("label", ""), key, fingerprint, meta)
	if op == "move":
		return _submit_move(principal, request, key, fingerprint, meta)
	if op == "note":
		return _submit_note(principal, request, key, fingerprint, meta)
	if op == "nature":
		return _submit_nature(principal, request, key, fingerprint, meta)
	var instance_id := ""
	if op != "place":
		if not request.get("instance_id") is String or not _instances.has(request.instance_id):
			return _failure("instance_not_found", "instance_id", "That invention no longer exists.")
		instance_id = request.instance_id
		if op != "activate" and _locks.has(instance_id):
			return _failure("target_locked", "instance_id", "That invention is protected. Only the player can unlock it.")
	if op == "activate":
		if not can_activate(principal, instance_id):
			return _failure("activation_denied", "instance_id", "Consent and an active permitted invention are required.")
		if activation_query.is_valid():
			var context: Variant = activation_query.call(principal, _instances[instance_id].duplicate(true))
			if not context is Dictionary or not context.get("ok", false):
				return _failure("activation_context", "instance_id", str(context.get("message", "This invention cannot be used from here.")) if context is Dictionary else "The host could not validate this activation.")
		var transient := {"ok": true, "instance_id": instance_id, "revision": revision, "permission_revision": permission_revision, "replayed": false, "transient": true, "affected": [instance_id], "created": []}
		_store_activation(principal, key, {"fingerprint": fingerprint, "receipt": transient.duplicate(true)})
		return transient
	var next := _state()
	next.instances = _instances.duplicate(true)
	if op in ["place", "revise"]:
		var prepared := _prepare_candidate(principal, request.get("source"), request.get("x_m"), request.get("z_m"), request.get("yaw_deg"), instance_id, request.get("y_m"), request.get("on", ""), meta.approved_by)
		if not prepared.ok:
			return prepared
		next.instances = prepared.instances
		next.next_id = int(prepared.next_id)
		instance_id = prepared.instance_id
	else:
		if not _can_build(principal):
			return _failure("build_denied", "principal", "Only an owner or editor may change inventions.")
		if not _may_change(principal, _instances[instance_id].owner_id, meta.approved_by, true):
			return _failure("ownership_denied", "instance_id", "Only its creator or the room owner can remove this invention.")
		if not _receipt_room(principal):
			return _receipt_limit()
		next.instances.erase(instance_id)
	# The journal's fact for this change, saved in the same write as the change and its receipt.
	if request.has("team"):
		next.team = request.team.duplicate(true)
	if request.has("nature"):
		next.nature = request.nature.duplicate(true)
	var created := [instance_id] if op == "place" else []
	var receipt := {"ok": true, "instance_id": instance_id, "revision": revision + 1, "permission_revision": permission_revision, "replayed": false, "affected": [] if op == "place" else [instance_id], "created": created}
	next.receipts = _receipts.duplicate(true)
	next.receipts[key] = {"principal": principal, "action_id": action, "fingerprint": fingerprint, "receipt": receipt.duplicate(true), "meta": meta}
	next.revision = revision + 1
	var committed := _commit(next)
	if not committed.ok:
		return committed
	return receipt


func _submit_lock(principal: String, op: String, targets: Variant, key: String, fingerprint: String, meta: Dictionary) -> Dictionary:
	if op == "unlock" and principal != PLAYER:
		return _failure("unlock_denied", "op", "Only the player can unlock, directly in the game.")
	if not _can_build(principal):
		return _failure("build_denied", "principal", "Only an owner or editor may protect things.")
	if not targets is Array or targets.is_empty() or targets.size() > MAX_TARGETS:
		return _failure("targets_invalid", "targets", "Name between 1 and 64 things to protect.")
	var seen := {}
	for target in targets:
		if not target is String or seen.has(target):
			return _failure("targets_invalid", "targets", "Targets must be distinct entity IDs.")
		seen[target] = true
		if not String(target).begins_with("creation:") and not String(target).begins_with("obj:"):
			return _failure("target_invalid", "targets", "Only objects and creations can be protected.")
		if entity_revision(target) < 0:
			return _failure("instance_not_found", "targets", "That thing is not in this room.")
		if op == "lock" and _locks.has(target):
			return _failure("already_locked", "targets", "That is already protected.")
		if op == "unlock" and not _locks.has(target):
			return _failure("not_locked", "targets", "That is not protected.")
	if op == "lock" and _locks.size() + targets.size() > MAX_LOCKS:
		return _failure("state_limit", "targets", "At most 256 things can be protected at once.")
	if not _receipt_room(principal):
		return _receipt_limit()
	var next := _state()
	next.instances = _instances.duplicate(true)
	next.locks = _locks.duplicate(true)
	next.entity_revisions = _entity_revisions.duplicate()
	for target in targets:
		if op == "lock":
			next.locks[target] = {"locked_by": principal, "locked_revision": revision + 1}
		else:
			next.locks.erase(target)
		if String(target).begins_with("creation:"):
			next.instances[target].revision = int(next.instances[target].revision) + 1
		else:
			next.entity_revisions[target] = int(next.entity_revisions.get(target, 0)) + 1
	var receipt := {"ok": true, "instance_id": "", "revision": revision + 1, "permission_revision": permission_revision, "replayed": false, "affected": targets.duplicate(), "created": []}
	next.receipts = _receipts.duplicate(true)
	next.receipts[key] = {"principal": principal, "action_id": key.get_slice("|", 1), "fingerprint": fingerprint, "receipt": receipt.duplicate(true), "meta": meta}
	next.revision = revision + 1
	var committed := _commit(next)
	if not committed.ok:
		return committed
	return receipt


## room.checkpoint: every durable receipt so far is compacted to its revision and a fingerprint prefix,
## which still replays and still refuses a conflicting reuse; receipt.lookup then reports compacted. It
## changes no world state and does not move the revision, and it never fails for a full ledger.
func _submit_checkpoint(principal: String, label: Variant, key: String, fingerprint: String, meta: Dictionary) -> Dictionary:
	if not label is String or label.length() > 80 or TEXT.has_hidden(label):
		return _failure("label_invalid", "label", "A checkpoint label is one line of at most 80 visible characters.")
	var next := _state()
	var compacted := _compacted.duplicate(true)
	for existing in _receipts:
		compacted[existing] = [int(_receipts[existing].receipt.revision), String(_receipts[existing].fingerprint).left(16)]
	var keys := compacted.keys()
	for index in range(maxi(0, keys.size() - MAX_COMPACTED)):
		compacted.erase(keys[index])
	var checkpoints := _checkpoints.duplicate(true)
	var number := 1 if checkpoints.is_empty() else int(String(checkpoints.back().id).trim_prefix("cp")) + 1
	checkpoints.append({"id": "cp%04d" % number, "revision": revision, "label": label})
	while checkpoints.size() > MAX_CHECKPOINTS:
		checkpoints.pop_front()
	var receipt := {"ok": true, "instance_id": "", "revision": revision, "permission_revision": permission_revision, "replayed": false, "affected": [], "created": []}
	next.compacted = compacted
	next.checkpoints = checkpoints
	next.receipts = {key: {"principal": principal, "action_id": key.get_slice("|", 1), "fingerprint": fingerprint, "receipt": receipt.duplicate(true), "meta": meta}}
	var committed := _commit(next)
	if not committed.ok:
		return committed
	var answer := receipt.duplicate(true)
	answer.checkpoint_id = checkpoints.back().id
	answer.compacted_count = compacted.size()
	return answer


## The sandbox verbs' durable half (entity.release, entity.place, entity.push): the trusted host worked out
## where the object comes to rest with the room's physics; this records that pose with the receipt in one
## atomic save and moves the object's revision. Only room objects move here (creations move by revision),
## never a protected one, and never outside the room.
func _submit_move(principal: String, request: Dictionary, key: String, fingerprint: String, meta: Dictionary) -> Dictionary:
	if not _can_build(principal):
		return _failure("build_denied", "principal", "Only an owner or editor may move things.")
	var target: Variant = request.get("target")
	if not target is String or not _room_entities.has(target) or _room_entities[target].kind != "object":
		return _failure("target_invalid", "target", "Only the room's objects can be moved this way.")
	if _locks.has(target):
		return _failure("target_locked", "target", "That is protected. Only the player can unlock it.")
	var pose := _pose_problem(request)
	if not pose.ok:
		return pose
	if not _receipt_room(principal):
		return _receipt_limit()
	var next := _state()
	next.object_poses = _object_poses.duplicate(true)
	next.object_poses[target] = pose.pose
	next.entity_revisions = _entity_revisions.duplicate()
	next.entity_revisions[target] = int(next.entity_revisions.get(target, 0)) + 1
	var receipt := {"ok": true, "instance_id": "", "revision": revision + 1, "permission_revision": permission_revision, "replayed": false, "affected": [target], "created": []}
	next.receipts = _receipts.duplicate(true)
	next.receipts[key] = {"principal": principal, "action_id": key.get_slice("|", 1), "fingerprint": fingerprint, "receipt": receipt.duplicate(true), "meta": meta}
	next.revision = revision + 1
	var committed := _commit(next)
	if not committed.ok:
		return committed
	return receipt


## journal.note: the sender's own words, written by the trusted host into the team block it hands over, saved
## atomically with a durable receipt naming the entry. It changes no world state and does not move the revision.
func _submit_note(principal: String, request: Dictionary, key: String, fingerprint: String, meta: Dictionary) -> Dictionary:
	var team: Variant = request.get("team")
	var entry_id: Variant = request.get("entry_id")
	if not team is Dictionary or not entry_id is String or not _pattern(ENTRY_ID).search(entry_id):
		return _failure("request_invalid", "team", "A note names its entry and the team block it is written into.")
	if not _receipt_room(principal):
		return _receipt_limit()
	var next := _state()
	next.team = team.duplicate(true)
	var receipt := {"ok": true, "instance_id": "", "revision": revision, "permission_revision": permission_revision, "replayed": false, "affected": [], "created": [], "entry_id": entry_id}
	next.receipts = _receipts.duplicate(true)
	next.receipts[key] = {"principal": principal, "action_id": key.get_slice("|", 1), "fingerprint": fingerprint, "receipt": receipt.duplicate(true), "meta": meta}
	var committed := _commit(next)
	if not committed.ok:
		return committed
	return receipt


## A durable nature change (nature.cast, nature.trail, nature.repair, or room.undo of nature changes): the trusted host
## worked out the next nature block from the room's physics and the island's rules; this records it with the receipt
## (its affected entities and result data) in one atomic save and moves the revision. An undo never fails for a full
## ledger: the oldest receipts are compacted first, as a checkpoint compacts them, so they still replay and still refuse
## a conflicting reuse.
func _submit_nature(principal: String, request: Dictionary, key: String, fingerprint: String, meta: Dictionary) -> Dictionary:
	if not _can_build(principal):
		return _failure("build_denied", "principal", "Only an owner or editor may change the island's nature.")
	var nature: Variant = request.get("nature")
	var affected: Variant = request.get("affected", [])
	var data: Variant = request.get("data", {})
	var undo: Variant = request.get("undo", false)
	if not nature is Dictionary or not _entity_list(affected) or not data is Dictionary or data.size() > 32 or not _json_safe(data, MAX_REQUEST_BYTES) or not undo is bool:
		return _failure("request_invalid", "nature", "A nature change names its block, what it affected and its result data.")
	var next := _state()
	next.receipts = _receipts.duplicate(true)
	next.compacted = _compacted.duplicate(true)
	if not _receipt_room(principal):
		if not undo:
			return _receipt_limit()
		_make_receipt_room(principal, next)
	next.nature = nature.duplicate(true)
	var receipt := {"ok": true, "instance_id": "", "revision": revision + 1, "permission_revision": permission_revision, "replayed": false, "affected": affected.duplicate(), "created": [], "data": data.duplicate(true)}
	next.receipts[key] = {"principal": principal, "action_id": key.get_slice("|", 1), "fingerprint": fingerprint, "receipt": receipt.duplicate(true), "meta": meta}
	next.revision = revision + 1
	var committed := _commit(next)
	if not committed.ok:
		return committed
	return receipt


## Room in next's ledger for one more of principal's receipts: the oldest receipts (lowest revision first) are compacted
## to their revision and fingerprint prefix, and the compacted index keeps its newest MAX_COMPACTED.
func _make_receipt_room(principal: String, next: Dictionary) -> void:
	var limit := MAX_RECEIPTS if principal == PLAYER else MAX_RECEIPTS - PLAYER_RECEIPT_RESERVE
	var keys: Array = next.receipts.keys()
	keys.sort_custom(func(a, b): return [int(next.receipts[a].receipt.revision), String(a)] < [int(next.receipts[b].receipt.revision), String(b)])
	var index := 0
	while next.receipts.size() >= limit and index < keys.size():
		var oldest: String = keys[index]
		next.compacted[oldest] = [int(next.receipts[oldest].receipt.revision), String(next.receipts[oldest].fingerprint).left(16)]
		next.receipts.erase(oldest)
		index += 1
	var compacted_keys: Array = next.compacted.keys()
	for drop in range(maxi(0, compacted_keys.size() - MAX_COMPACTED)):
		next.compacted.erase(compacted_keys[drop])


## The island's nature as last saved ({} before the host wrote it).
func nature() -> Dictionary:
	return _nature.duplicate(true)


## The trusted host saves the island's nature (the simulation's throttled checkpoint, and on exit) and, when given, the
## team block in the same write. It keeps no receipt and does not move the revision, so the weather never conflicts with
## an undo or a command that names expected_revision. Never while the save is unavailable.
func set_nature(nature: Dictionary, team: Variant = null) -> Dictionary:
	if not _available():
		return _failure("save_not_ready", "", "Load the saved room successfully before changing it.")
	if not _json_safe(nature, MAX_SAVE_BYTES) or (team != null and (not team is Dictionary or not _json_safe(team, MAX_SAVE_BYTES))):
		return _failure("request_invalid", "nature", "The nature and team blocks must be bounded finite JSON data.")
	var next := _state()
	next.nature = nature.duplicate(true)
	if team is Dictionary:
		next.team = team.duplicate(true)
	return _commit(next)


## The team block as last saved ({} before the host wrote one).
func team() -> Dictionary:
	return _team.duplicate(true)


## The trusted host saves the team's journal and discovered map (a goal's task, a fact, newly discovered space). It
## changes no world state, keeps no receipt and does not move the revision. Never while the save is unavailable: a
## save that failed to load is never overwritten.
func set_team(team: Dictionary) -> Dictionary:
	if not _available():
		return _failure("save_not_ready", "", "Load the saved room successfully before changing it.")
	var next := _state()
	next.team = team.duplicate(true)
	return _commit(next)


## A pose as the host sends it and the save keeps it: {position_m, rotation (unit xyzw), bounds}, the origin
## and the bounds inside the room (1 mm tolerance). Returns {ok, pose} with plain floats, or a failure.
func _pose_problem(value: Dictionary) -> Dictionary:
	var position: Variant = value.get("position_m")
	var rotation: Variant = value.get("rotation")
	if not position is Array or position.size() != 3 or not rotation is Array or rotation.size() != 4:
		return _failure("placement_invalid", "position_m", "A pose has a position and a rotation.")
	for number in position + rotation:
		if not _number(number) or abs(float(number)) > COORDINATE_LIMIT_M:
			return _failure("placement_invalid", "position_m", "Use finite pose numbers.")
	var length := sqrt(float(rotation[0]) * float(rotation[0]) + float(rotation[1]) * float(rotation[1]) + float(rotation[2]) * float(rotation[2]) + float(rotation[3]) * float(rotation[3]))
	if abs(length - 1.0) > 0.001:
		return _failure("placement_invalid", "rotation", "A rotation is a unit quaternion.")
	var extent := _bounds(value.get("bounds"))
	if extent.is_empty():
		return _failure("placement_invalid", "bounds", "A pose names the bounds it occupies.")
	var origin := Vector3(float(position[0]), float(position[1]), float(position[2]))
	var tolerance := Vector3.ONE * 0.001
	var room := AABB(_room_min - tolerance, _room_max - _room_min + tolerance * 2.0)
	if not room.has_point(origin) or not room.has_point(extent[0]) or not room.has_point(extent[1]):
		return _failure("room_bounds", "position_m", "The object must stay inside the room.")
	var pose := {"position_m": [float(position[0]), float(position[1]), float(position[2])], "rotation": [float(rotation[0]), float(rotation[1]), float(rotation[2]), float(rotation[3])], "bounds": {"min_m": _array3(extent[0]), "max_m": _array3(extent[1])}}
	return {"ok": true, "pose": pose}


## Where play has moved one object ({} when it stands where the manifest puts it).
func object_pose(entity_id: String) -> Dictionary:
	return _object_poses[entity_id].duplicate(true) if _object_poses.has(entity_id) else {}


func _publish_poses(poses: Dictionary) -> void:
	if pose_sink.is_valid():
		pose_sink.call(poses.duplicate(true))


## Whether principal may add a durable receipt: the companion may fill the ledger up to the player's reserve.
func _receipt_room(principal: String) -> bool:
	var limit := MAX_RECEIPTS if principal == PLAYER else MAX_RECEIPTS - PLAYER_RECEIPT_RESERVE
	return _receipts.size() < limit


func _receipt_limit() -> Dictionary:
	return _failure("receipt_limit", "action_id", "The receipt ledger is full. A room.checkpoint compacts it.")


## Session receipts for activations, bounded per principal: the oldest is forgotten first.
func _store_activation(principal: String, key: String, entry: Dictionary) -> void:
	_activation_receipts.erase(key)
	_activation_receipts[key] = entry
	var mine: Array = []
	for existing in _activation_receipts:
		if String(existing).begins_with(principal + "|"):
			mine.append(existing)
	for index in range(maxi(0, mine.size() - MAX_TRANSIENT_PER_PRINCIPAL)):
		_activation_receipts.erase(mine[index])


## The runtime calls this when an admitted activation could not fire, so a retry runs again instead of
## replaying a success that never happened.
func forget_activation(principal: String, action_id: String) -> void:
	_activation_receipts.erase(principal + "|" + action_id)


func activation_receipt_count(principal: String) -> int:
	var count := 0
	for existing in _activation_receipts:
		if String(existing).begins_with(principal + "|"):
			count += 1
	return count


func preflight(principal: String, source: Dictionary, x_m: Variant, z_m: Variant, yaw_deg: Variant, instance_id: String = "", y_m: Variant = null, on: Variant = "", approved_by := "") -> Dictionary:
	# A read-only diagnostic of the same candidate path used by commit. It never
	# reserves an ID, capacity or receipt, changes revisions, or touches the save.
	var previous_error := last_error
	var candidate := _prepare_candidate(principal, source, x_m, z_m, yaw_deg, instance_id, y_m, on, approved_by)
	last_error = previous_error
	if not candidate.ok:
		return candidate
	return {"ok": true, "artifact": candidate.artifact, "position_m": candidate.position_m, "surface_entity": candidate.surface_entity, "instance_id": candidate.instance_id, "revision": revision, "permission_revision": permission_revision}


func _prepare_candidate(principal: String, source: Variant, x_m: Variant, z_m: Variant, yaw_deg: Variant, instance_id: String, y_m: Variant = null, on: Variant = "", approved_by := "") -> Dictionary:
	if not _available():
		return _failure("save_not_ready", "", "Load the saved room successfully before changing it.")
	if not _roles.has(principal):
		return _failure("principal_unknown", "principal", "The host did not admit this principal.")
	if not _can_build(principal):
		return _failure("build_denied", "principal", "Only an owner or editor may change inventions.")
	if not instance_id.is_empty():
		if not _instances.has(instance_id):
			return _failure("instance_not_found", "instance_id", "That invention no longer exists.")
		if _locks.has(instance_id):
			return _failure("target_locked", "instance_id", "That invention is protected. Only the player can unlock it.")
		if not _may_change(principal, _instances[instance_id].owner_id, approved_by, false):
			return _failure("ownership_denied", "instance_id", "You can revise only your own invention.")
	if not on is String or (not String(on).is_empty() and entity_revision(on) < 0):
		return _failure("surface_target_invalid", "on", "Place things on something in this room.")
	if not _receipt_room(principal):
		return _receipt_limit()
	var compiled: Dictionary = COMPILER.compile(source)
	if not compiled.ok:
		return compiled
	var placement := _validate_placement(compiled.artifact, x_m, z_m, yaw_deg, y_m, on, _lock_zones(_instances, _locks, instance_id))
	if not placement.ok:
		return placement
	if compiled.artifact.source.mount == "ground" and occupancy_query.is_valid():
		var position := Vector3(float(placement.position_m[0]), float(placement.position_m[1]), float(placement.position_m[2]))
		var occupancy: Variant = occupancy_query.call(compiled.artifact.duplicate(true), position, float(yaw_deg), instance_id)
		if not occupancy is Dictionary or not occupancy.has("ok") or not occupancy.ok is bool:
			return _failure("occupancy_unavailable", "position", "The host could not verify live avatar clearance.")
		if not occupancy.ok:
			return _failure(String(occupancy.get("code", "avatar_occupied")), String(occupancy.get("path", "position")), String(occupancy.get("message", "Move the invention away from live avatars before confirming.")))
	var next_instances := _instances.duplicate(true)
	var next_id := _next_id
	var candidate_id := instance_id
	if candidate_id.is_empty():
		candidate_id = "creation:%08d" % next_id
		next_id += 1
	var owner: String = principal if instance_id.is_empty() else _instances[instance_id].owner_id
	var instance_revision := 1 if instance_id.is_empty() else int(_instances[instance_id].revision) + 1
	next_instances[candidate_id] = {"id": candidate_id, "owner_id": owner, "revision": instance_revision, "source": compiled.artifact.source.duplicate(true), "artifact": compiled.artifact.duplicate(true), "position_m": placement.position_m, "yaw_deg": float(yaw_deg), "active": true}
	var capacity := _validate_capacity(next_instances)
	if not capacity.ok:
		return capacity
	return {"ok": true, "instances": next_instances, "next_id": next_id, "instance_id": candidate_id, "artifact": compiled.artifact, "position_m": placement.position_m, "surface_entity": placement.surface_entity}


func snapshot(principal: String) -> Dictionary:
	if not _available():
		return _failure("save_not_ready", "", "The room save is not available.")
	if not _roles.has(principal):
		return _failure("principal_unknown", "principal", "The host did not admit this principal.")
	var readable: Dictionary = {}
	for instance in _instances.values():
		var item: Dictionary = instance.duplicate(true)
		item.active = _can_build(item.owner_id)
		item.locked = _locks.has(item.id)
		readable[item.id] = item
	return {"ok": true, "revision": revision, "permission_revision": permission_revision, "instances": readable, "roles": _roles.duplicate(), "consent": _consent.duplicate(), "principal_role": _roles[principal], "capacity": _capacity(_instances), "limits": {"owner": OWNER_LIMITS.duplicate(), "world": WORLD_LIMITS.duplicate()}, "room_pin": _room_pin(), "locks": _locks.duplicate(true), "entity_revisions": _entity_revisions.duplicate(), "object_poses": _object_poses.duplicate(true), "bounds": {"min_m": _array3(_room_min), "max_m": _array3(_room_max)}}


func is_ready() -> bool:
	return _available()


## Whether principal may change the room now: the save is loaded and its role builds (an owner or an editor).
func may_change(principal: String) -> bool:
	return _available() and _roles.has(principal) and _can_build(principal)


## Revision of a room entity the authority knows: a creation's own revision, an object's or shell
## part's play revision (0 until play changes it), or -1 when it is not in this room.
func entity_revision(entity_id: Variant) -> int:
	if not entity_id is String:
		return -1
	if _instances.has(entity_id):
		return int(_instances[entity_id].revision)
	if _room_entities.has(entity_id):
		return int(_entity_revisions.get(entity_id, 0))
	return -1


func is_locked(entity_id: String) -> bool:
	return _locks.has(entity_id)


## The durable receipt record for (principal, action_id), or {} when none was committed or it was compacted.
func receipt_for(principal: String, action_id: String) -> Dictionary:
	var key := principal + "|" + action_id
	return _receipts[key].duplicate(true) if _receipts.has(key) else {}


## {revision, fingerprint_prefix} for an action a checkpoint compacted, or {}.
func compacted_receipt(principal: String, action_id: String) -> Dictionary:
	var key := principal + "|" + action_id
	return {"revision": int(_compacted[key][0]), "fingerprint_prefix": String(_compacted[key][1])} if _compacted.has(key) else {}


func receipt_count() -> int:
	return _receipts.size()


func checkpoints() -> Array:
	return _checkpoints.duplicate(true)


func room_bounds() -> AABB:
	return AABB(_room_min, _room_max - _room_min)


func room_entities() -> Dictionary:
	return _room_entities.duplicate(true)


func set_role(operator: String, target: String, role: String) -> Dictionary:
	if not _available():
		return _failure("save_not_ready", "", "The room save is not available.")
	if _roles.get(operator) != "owner":
		return _failure("role_denied", "operator", "Only the room owner can change roles.")
	if target != COMPANION or role not in ["editor", "visitor"]:
		return _failure("role_invalid", "role", "The player owns the room; the companion can be an editor or a visitor.")
	if _roles[target] == role:
		return {"ok": true, "revision": revision, "permission_revision": permission_revision, "changed": false}
	var next_roles := _roles.duplicate()
	next_roles[target] = role
	return _commit_permissions(next_roles, _consent.duplicate())


func set_consent(principal: String, enabled: bool) -> Dictionary:
	if not _available() or not _roles.has(principal):
		return _failure("consent_denied", "principal", "Only an admitted principal can have consent set.")
	if _consent[principal] == enabled:
		return {"ok": true, "revision": revision, "permission_revision": permission_revision, "changed": false}
	var next_consent := _consent.duplicate()
	next_consent[principal] = enabled
	return _commit_permissions(_roles.duplicate(), next_consent)


func can_activate(principal: String, instance_id: String) -> bool:
	return _available() and _roles.has(principal) and bool(_consent.get(principal, false)) and _instances.has(instance_id) and _can_build(_instances[instance_id].owner_id)


## Whether an effect owned by owner may push target at position. Locked zones are effect-free;
## ignore names the creation whose own field is being sampled (its own lock does not silence it).
func can_affect(owner: String, target: String, position: Vector3, ignore := "") -> bool:
	if not _available() or not _can_build(owner) or not _roles.has(target) or not bool(_consent.get(target, false)) or not position.is_finite():
		return false
	return _point_authorized(Vector2(position.x, position.z), ignore)


func consume_runtime_budget(instance_id: String, principal: String, fields: int, node_evaluations: int, now_seconds: float) -> Dictionary:
	if not can_activate(principal, instance_id):
		return _failure("activation_denied", "instance_id", "Consent or permissions were revoked before activation.")
	if fields < 0 or fields > 16 or node_evaluations < 1 or node_evaluations > 16 or not is_finite(now_seconds) or now_seconds < 0.0 or now_seconds < _last_budget_time:
		return _failure("runtime_cost_invalid", "runtime", "Runtime costs and monotonic host time must be bounded.")
	_last_budget_time = now_seconds
	var recent: Array = []
	for entry in _runtime_events:
		if float(entry.time) > now_seconds - 1.0:
			recent.append(entry)
	_runtime_events = recent
	var owner: String = _instances[instance_id].owner_id
	var actors := [owner]
	if principal != owner:
		actors.append(principal)
	var world_fields := fields
	var world_nodes := node_evaluations
	var instance_count := 1
	var instance_nodes := node_evaluations
	for entry in recent:
		world_fields += int(entry.fields)
		world_nodes += int(entry.nodes)
		if entry.instance_id == instance_id:
			instance_count += 1
			instance_nodes += int(entry.nodes)
	if recent.size() + 1 > 20 or world_fields > 16 or world_nodes > 320 or instance_count > 5 or instance_nodes > 80:
		return _failure("runtime_budget", "runtime", "The invention or room activation budget is busy. Try again shortly.")
	for actor in actors:
		var actor_count := 1
		var actor_fields := fields
		var actor_nodes := node_evaluations
		for entry in recent:
			if actor in entry.actors:
				actor_count += 1
				actor_fields += int(entry.fields)
				actor_nodes += int(entry.nodes)
		if actor_count > 10 or actor_fields > 8 or actor_nodes > 160:
			return _failure("runtime_budget", "runtime", "This principal's activation budget is busy. Try again shortly.")
	_runtime_events.append({"time": now_seconds, "instance_id": instance_id, "actors": actors, "fields": fields, "nodes": node_evaluations})
	return {"ok": true, "revision": revision, "permission_revision": permission_revision}


func load_saved() -> Dictionary:
	if not _configured:
		return _failure("configuration_invalid", "", "Configure a room before loading inventions.")
	if not FileAccess.file_exists(_save_path):
		if _load_required:
			return _failure("save_missing", "save", "The previously detected save disappeared; it has not been overwritten.")
		return {"ok": true, "loaded": false, "revision": revision, "permission_revision": permission_revision}
	_load_required = true
	var file := FileAccess.open(_save_path, FileAccess.READ)
	if file == null or file.get_length() > MAX_SAVE_BYTES:
		return _failure("save_invalid", "save", "The save is unreadable or exceeds the local size limit.")
	var text := file.get_as_text()
	file.close()
	# Exact strict JSON: Godot's own parser can read a saved number back one ulp off.
	var parsed: Dictionary = JSON_KERNEL.parse(text)
	if not parsed.ok or not parsed.value is Dictionary or not _json_safe(parsed.value, MAX_SAVE_BYTES):
		return _failure("save_invalid", "save", "The save contains invalid JSON; it has not been overwritten.")
	return load_envelope(parsed.value)


func load_envelope(data: Dictionary) -> Dictionary:
	if not _configured:
		return _failure("configuration_invalid", "", "Configure a room before loading inventions.")
	_load_required = true
	# The saved poses go into the scene before the saved creations are checked against the room's surfaces:
	# a creation may stand on an object play has moved. A save that then fails puts the scene back.
	var poses := _saved_poses(data)
	if not poses.ok:
		return poses
	_publish_poses(poses.poses)
	if pose_check.is_valid() and not poses.poses.is_empty():
		var verdict: Variant = pose_check.call(poses.poses.duplicate(true))
		if not verdict is Dictionary or verdict.get("ok") != true:
			_publish_poses(_object_poses)
			var reason := String(verdict.get("message", "")) if verdict is Dictionary else ""
			return _failure("save_invalid", "object_poses", reason if not reason.is_empty() else "A saved object pose does not fit the room.")
	var saved_team: Variant = data.get("team", {}) if _whole(data.get("version")) and int(data.version) >= 5 else {}
	if not saved_team is Dictionary:
		_publish_poses(_object_poses)
		return _failure("save_invalid", "team", "The saved journal and map are invalid.")
	if team_check.is_valid() and not saved_team.is_empty():
		var judged: Variant = team_check.call(saved_team.duplicate(true), int(data.revision) if _whole(data.get("revision")) else -1)
		if not judged is Dictionary or judged.get("ok") != true:
			_publish_poses(_object_poses)
			var why := String(judged.get("message", "")) if judged is Dictionary else ""
			return _failure("save_invalid", "team", why if not why.is_empty() else "The saved journal and map are invalid.")
	var saved_nature := _checked_nature(data)
	if not saved_nature.ok:
		_publish_poses(_object_poses)
		return saved_nature
	var checked := _validate_saved(data)
	if not checked.ok:
		_publish_poses(_object_poses)
		return checked
	_object_poses = poses.poses
	_team = saved_team.duplicate(true)
	_nature = saved_nature.nature
	_instances = checked.instances
	_receipts = data.receipts.duplicate(true)
	_compacted = {}
	for key in data.get("compacted", {}):
		_compacted[key] = [int(data.compacted[key][0]), String(data.compacted[key][1])]
	_checkpoints = []
	for checkpoint in data.get("checkpoints", []):
		_checkpoints.append({"id": checkpoint.id, "revision": int(checkpoint.revision), "label": checkpoint.label})
	_roles = data.roles.duplicate()
	_locks = data.locks.duplicate(true)
	_entity_revisions = {}
	for entity_id in data.entity_revisions:
		_entity_revisions[entity_id] = int(data.entity_revisions[entity_id])
	for entity_id in _locks:
		_locks[entity_id].locked_revision = int(_locks[entity_id].locked_revision)
	# Consent is a session decision. Inventions are readable on reload, but no
	# saved opt-in can silently cause forces on a returning player or companion.
	_consent = {PLAYER: false, COMPANION: false}
	revision = int(data.revision)
	permission_revision = int(data.permission_revision) + 1
	_next_id = int(data.next_id)
	_activation_receipts.clear()
	_runtime_events.clear()
	_last_budget_time = -1.0
	_load_required = false
	return {"ok": true, "loaded": true, "revision": revision, "permission_revision": permission_revision}


## The trusted host rebuilds (or migrates) a room with no play into a whole state: checked exactly as a save is, written,
## and only then believed. If any check or the write fails, nothing changes: the state stays as it was and the scene's
## object poses (shown while the creations are checked against them) go back.
func adopt_envelope(data: Dictionary) -> Dictionary:
	if not _available() or revision != 0 or not _receipts.is_empty() or not _compacted.is_empty() or not _instances.is_empty() or not _locks.is_empty() or not _object_poses.is_empty() or not _entity_revisions.is_empty() or not _nature.is_empty():
		return _failure("save_not_ready", "", "Only a room with no play takes a whole state.")
	if not _json_safe(data, MAX_SAVE_BYTES):
		return _failure("save_invalid", "save", "The state exceeds its structural limits.")
	var poses := _saved_poses(data)
	if not poses.ok:
		return poses
	_publish_poses(poses.poses)
	if pose_check.is_valid() and not poses.poses.is_empty():
		var verdict: Variant = pose_check.call(poses.poses.duplicate(true))
		if not verdict is Dictionary or verdict.get("ok") != true:
			_publish_poses(_object_poses)
			var reason := String(verdict.get("message", "")) if verdict is Dictionary else ""
			return _failure("save_invalid", "object_poses", reason if not reason.is_empty() else "A saved object pose does not fit the room.")
	var saved_team: Variant = data.get("team", {}) if _whole(data.get("version")) and int(data.version) >= 5 else {}
	if not saved_team is Dictionary:
		_publish_poses(_object_poses)
		return _failure("save_invalid", "team", "The journal and map are invalid.")
	if team_check.is_valid() and not saved_team.is_empty():
		var judged: Variant = team_check.call(saved_team.duplicate(true), int(data.revision) if _whole(data.get("revision")) else -1)
		if not judged is Dictionary or judged.get("ok") != true:
			_publish_poses(_object_poses)
			return _failure("save_invalid", "team", String(judged.get("message", "The journal and map are invalid.")) if judged is Dictionary else "The journal and map are invalid.")
	var saved_nature := _checked_nature(data)
	if not saved_nature.ok:
		_publish_poses(_object_poses)
		return saved_nature
	var checked := _validate_saved(data)
	if not checked.ok:
		_publish_poses(_object_poses)
		return checked
	var compacted := {}
	for key in data.get("compacted", {}):
		compacted[key] = [int(data.compacted[key][0]), String(data.compacted[key][1])]
	var checkpoints: Array = []
	for checkpoint in data.get("checkpoints", []):
		checkpoints.append({"id": checkpoint.id, "revision": int(checkpoint.revision), "label": checkpoint.label})
	var locks: Dictionary = data.locks.duplicate(true)
	for entity_id in locks:
		locks[entity_id].locked_revision = int(locks[entity_id].locked_revision)
	var revisions := {}
	for entity_id in data.entity_revisions:
		revisions[entity_id] = int(data.entity_revisions[entity_id])
	var next := {"instances": checked.instances, "receipts": data.receipts.duplicate(true), "compacted": compacted, "checkpoints": checkpoints, "revision": int(data.revision), "permission_revision": int(data.permission_revision) + 1, "roles": data.roles.duplicate(), "consent": {PLAYER: false, COMPANION: false}, "next_id": int(data.next_id), "locks": locks, "entity_revisions": revisions, "object_poses": poses.poses, "team": saved_team.duplicate(true), "nature": saved_nature.nature}
	# Written first; believed (and the poses published for good) only once it is on disk.
	var committed := _commit(next)
	if not committed.ok:
		_publish_poses(_object_poses)
		return committed
	_activation_receipts.clear()
	_runtime_events.clear()
	_last_budget_time = -1.0
	_publish_poses(_object_poses)
	return {"ok": true, "revision": revision, "permission_revision": permission_revision}


func export_envelope() -> Dictionary:
	return _envelope(_state())


## A save's nature block, judged by the trusted host before the save is believed: {ok, nature} or a failure. Saves
## before version 6 have none.
func _checked_nature(data: Dictionary) -> Dictionary:
	var saved: Variant = data.get("nature", {}) if _whole(data.get("version")) and int(data.version) >= 6 else {}
	if not saved is Dictionary:
		return _failure("save_invalid", "nature", "The saved nature is invalid.")
	if nature_check.is_valid() and not saved.is_empty():
		var judged: Variant = nature_check.call(saved.duplicate(true), int(data.revision) if _whole(data.get("revision")) else -1)
		if not judged is Dictionary or judged.get("ok") != true:
			var why := String(judged.get("message", "")) if judged is Dictionary else ""
			return _failure("save_invalid", "nature", why if not why.is_empty() else "The saved nature is invalid.")
	return {"ok": true, "nature": saved.duplicate(true)}


func _state() -> Dictionary:
	return {"instances": _instances, "receipts": _receipts, "compacted": _compacted, "checkpoints": _checkpoints, "revision": revision, "permission_revision": permission_revision, "roles": _roles, "consent": _consent, "next_id": _next_id, "locks": _locks, "entity_revisions": _entity_revisions, "object_poses": _object_poses, "team": _team, "nature": _nature}


## Persists the complete next state, then publishes it. A failed write publishes nothing.
func _commit(next: Dictionary) -> Dictionary:
	var persisted := _persist(next)
	if not persisted.ok:
		return persisted
	_instances = next.instances
	_receipts = next.receipts
	_compacted = next.compacted
	_checkpoints = next.checkpoints
	_roles = next.roles
	_consent = next.consent
	_next_id = int(next.next_id)
	_locks = next.locks
	_entity_revisions = next.entity_revisions
	var moved: bool = next.object_poses != _object_poses
	_object_poses = next.object_poses
	_team = next.team
	_nature = next.nature
	revision = int(next.revision)
	permission_revision = int(next.permission_revision)
	if moved:
		_publish_poses(_object_poses)
	return {"ok": true}


func _commit_permissions(roles: Dictionary, consent: Dictionary) -> Dictionary:
	var next := _state()
	next.roles = roles
	next.consent = consent
	next.revision = revision + 1
	next.permission_revision = permission_revision + 1
	var result := _commit(next)
	if not result.ok:
		return result
	return {"ok": true, "revision": revision, "permission_revision": permission_revision, "changed": true}


func _may_change(principal: String, owner: String, approved_by: String, removal: bool) -> bool:
	if owner == principal:
		return true
	# The room owner may remove anything for moderation but never silently rewrites another's source.
	if removal and _roles.get(principal) == "owner":
		return true
	# A held companion command the player approved acts with the player's consent on the player's creation.
	return approved_by == PLAYER and owner == PLAYER


func _validate_placement(artifact: Dictionary, x: Variant, z: Variant, yaw: Variant, y_hint: Variant = null, on: Variant = "", zones: Array = []) -> Dictionary:
	if not _number(x) or not _number(z) or not _number(yaw) or abs(float(yaw)) > 180.0 or (y_hint != null and not _number(y_hint)):
		return _failure("placement_invalid", "position", "Use finite placement coordinates and yaw between -180 and 180 degrees.")
	# Check scalar doubles before converting to single-precision Vector3 values;
	# huge finite JSON numbers must never overflow into NaN footprint comparisons.
	if float(x) < _room_min.x or float(x) > _room_max.x or float(z) < _room_min.z or float(z) > _room_max.z:
		return _failure("room_bounds", "position", "The placement origin must be inside the room.")
	var footprint := _footprint(artifact, float(x), float(z), float(yaw))
	var minimum: Vector2 = footprint[0]
	var maximum: Vector2 = footprint[1]
	var inside := _rectangle_authorized(minimum, maximum, zones)
	if not inside.ok:
		return inside
	# Field centers are attached to their part. The whole radius, including
	# proximity sensors, must stay inside the room and outside protected zones.
	var basis := Basis(Vector3.UP, deg_to_rad(float(yaw)))
	var parts: Dictionary = {}
	for part in artifact.source.parts:
		parts[part.id] = part
	for node in artifact.source.nodes:
		if node.op in ["wind", "proximity"]:
			var p: Array = parts[node.part_id].position_m
			var center3 := basis * Vector3(float(p[0]), float(p[1]), float(p[2]))
			var center := Vector2(float(x) + center3.x, float(z) + center3.z)
			var radius := float(node.params.radius_m)
			var field := _rectangle_authorized(center - Vector2.ONE * radius, center + Vector2.ONE * radius, zones)
			if not field.ok:
				field.path = "nodes." + String(node.id) + ".params.radius_m"
				return field
	var from_y := _room_max.y if y_hint == null else minf(float(y_hint) + SURFACE_PROBE_M, _room_max.y)
	var heights: Array[float] = []
	var centre_entity := ""
	for sample in [minimum, maximum, Vector2(minimum.x, maximum.y), Vector2(maximum.x, minimum.y), Vector2(float(x), float(z))]:
		var hit: Variant = _surface.call(sample.x, sample.y, from_y)
		if not hit is Dictionary or hit.get("ok") != true or not _number(hit.get("height_m")) or float(hit.height_m) < _room_min.y - 0.01 or float(hit.height_m) > _room_max.y:
			return _failure("surface_unavailable", "position", "This footprint needs a solid surface under every corner.")
		heights.append(float(hit.height_m))
		centre_entity = String(hit.get("entity_id", ""))
	var highest: float = heights.max()
	var lowest: float = heights.min()
	if highest - lowest > MAX_SURFACE_STEP_M:
		return _failure("surface_uneven", "position", "Choose a flatter spot: the corners differ by more than 5 cm.")
	if on is String and not String(on).is_empty() and centre_entity != on:
		return _failure("surface_mismatch", "on", "That spot is not on the named surface.")
	if artifact.source.mount == "ground" and highest + float(artifact.bounds.max[1]) > _room_max.y + 0.001:
		return _failure("room_bounds", "position", "The invention would not fit under the ceiling here.")
	return {"ok": true, "position_m": [float(x), highest, float(z)], "surface_entity": centre_entity}


func _footprint(artifact: Dictionary, x: float, z: float, yaw: float) -> Array:
	var basis := Basis(Vector3.UP, deg_to_rad(yaw))
	var low: Array = artifact.bounds.min
	var high: Array = artifact.bounds.max
	var minimum := Vector2(INF, INF)
	var maximum := Vector2(-INF, -INF)
	for bx in [float(low[0]), float(high[0])]:
		for bz in [float(low[2]), float(high[2])]:
			var point := basis * Vector3(bx, 0.0, bz) + Vector3(x, 0.0, z)
			minimum = minimum.min(Vector2(point.x, point.z))
			maximum = maximum.max(Vector2(point.x, point.z))
	return [minimum, maximum]


## Protected XZ rectangles: every locked object and creation except ignore.
func _lock_zones(instances: Dictionary, locks: Dictionary, ignore := "") -> Array:
	var zones: Array = []
	for entity_id in locks:
		if entity_id == ignore:
			continue
		if instances.has(entity_id):
			var item: Dictionary = instances[entity_id]
			zones.append(_footprint(item.artifact, float(item.position_m[0]), float(item.position_m[2]), float(item.yaw_deg)))
		elif _object_poses.has(entity_id):
			var moved: Dictionary = _object_poses[entity_id].bounds
			zones.append([Vector2(float(moved.min_m[0]), float(moved.min_m[2])), Vector2(float(moved.max_m[0]), float(moved.max_m[2]))])
		elif _room_entities.has(entity_id):
			var entity: Dictionary = _room_entities[entity_id]
			zones.append([Vector2(entity.min.x, entity.min.z), Vector2(entity.max.x, entity.max.z)])
	return zones


func _rectangle_authorized(minimum: Vector2, maximum: Vector2, zones: Array) -> Dictionary:
	if minimum.x < _room_min.x or minimum.y < _room_min.z or maximum.x > _room_max.x or maximum.y > _room_max.z:
		return _failure("room_bounds", "position", "The whole invention and its fields must fit inside the room.")
	for zone in zones:
		var low: Vector2 = zone[0]
		var high: Vector2 = zone[1]
		if maximum.x >= low.x and minimum.x <= high.x and maximum.y >= low.y and minimum.y <= high.y:
			return _failure("protected_zone", "position", "Something here is protected. Only the player can unlock it.")
	return {"ok": true}


func _point_authorized(point: Vector2, ignore := "") -> bool:
	if point.x < _room_min.x or point.x > _room_max.x or point.y < _room_min.z or point.y > _room_max.z:
		return false
	for zone in _lock_zones(_instances, _locks, ignore):
		var low: Vector2 = zone[0]
		var high: Vector2 = zone[1]
		if point.x >= low.x and point.x <= high.x and point.y >= low.y and point.y <= high.y:
			return false
	return true


func _capacity(instances: Dictionary) -> Dictionary:
	var world := {"instances": 0, "parts": 0, "nodes": 0, "edges": 0, "fields": 0, "lights": 0, "rotors": 0}
	var owners := {PLAYER: world.duplicate(), COMPANION: world.duplicate()}
	for instance in instances.values():
		world.instances += 1
		owners[instance.owner_id].instances += 1
		for key in instance.artifact.cost:
			world[key] += int(instance.artifact.cost[key])
			owners[instance.owner_id][key] += int(instance.artifact.cost[key])
	return {"world": world, "owners": owners}


func _validate_capacity(instances: Dictionary) -> Dictionary:
	var counts := _capacity(instances)
	var mounted := {PLAYER: 0, COMPANION: 0}
	for instance in instances.values():
		if instance.source.mount == "avatar":
			mounted[instance.owner_id] += 1
			if mounted[instance.owner_id] > 1:
				return _failure("avatar_slot_full", "mount", "Each body can wear one invention. Revise or remove the worn one first.")
	for key in WORLD_LIMITS:
		if int(counts.world[key]) > int(WORLD_LIMITS[key]):
			return _failure("world_capacity", "cost." + key, "The room's aggregate " + key + " limit would be exceeded.")
	for owner in counts.owners:
		for key in OWNER_LIMITS:
			if int(counts.owners[owner][key]) > int(OWNER_LIMITS[key]):
				return _failure("owner_capacity", "cost." + key, "This creator's aggregate " + key + " limit would be exceeded.")
	return {"ok": true}


func _envelope(state: Dictionary) -> Dictionary:
	var stored_instances: Array = []
	for instance in state.instances.values():
		var stored: Dictionary = instance.duplicate(true)
		stored.erase("artifact")
		stored.erase("active")
		stored_instances.append(stored)
	return {"schema": SCHEMA, "version": VERSION, "compiler_version": 1, "style_version": STYLE_VERSION, "room_pin": _room_pin(), "revision": state.revision, "permission_revision": state.permission_revision, "next_id": state.next_id, "roles": state.roles.duplicate(true), "consent": state.consent.duplicate(true), "instances": stored_instances, "receipts": state.receipts.duplicate(true), "compacted": state.compacted.duplicate(true), "checkpoints": state.checkpoints.duplicate(true), "locks": state.locks.duplicate(true), "entity_revisions": state.entity_revisions.duplicate(true), "object_poses": state.object_poses.duplicate(true), "team": state.team.duplicate(true), "nature": state.nature.duplicate(true)}


func _persist(state: Dictionary) -> Dictionary:
	var envelope := _envelope(state)
	# A save is never written unless load_saved could read it back: the same node budget and bounds.
	var problem := _envelope_bounds_problem(envelope)
	if not problem.is_empty():
		return problem
	var serialized := COMPILER.canonical_json(envelope)
	if serialized.is_empty() or serialized.to_utf8_buffer().size() > MAX_SAVE_BYTES:
		return _failure("save_size", "save", "The creation save exceeds the local size limit.")
	if persistence_sink.is_valid():
		var result: Variant = persistence_sink.call(envelope)
		if not result is Dictionary or not result.get("ok", false):
			return _failure("durable_save_pending", "save", str(result.get("message", "The durable save has not been confirmed. Reconnect before editing.")) if result is Dictionary else "The durable save has not been confirmed. Reconnect before editing.")
		return {"ok": true}
	var absolute := ProjectSettings.globalize_path(_save_path)
	if DirAccess.make_dir_recursive_absolute(absolute.get_base_dir()) != OK:
		return _failure("save_directory", "save", "The save folder could not be created.")
	var temporary := absolute + ".pending"
	var file := FileAccess.open(temporary, FileAccess.WRITE)
	if file == null:
		return _failure("save_write", "save", "The pending save could not be opened.")
	file.store_string(serialized)
	file.flush()
	var write_error := file.get_error()
	file.close()
	if write_error != OK:
		return _failure("save_write", "save", "The pending save could not be written completely.")
	# The committed file is replaced only after a complete flushed write. Failure
	# never publishes the candidate in memory and never removes the prior file.
	if DirAccess.rename_absolute(temporary, absolute) != OK:
		return _failure("save_replace", "save", "The previous save could not be replaced safely.")
	return {"ok": true}


## The structural bounds a save must meet to load: the JSON node budget, and every collection's size.
## _persist checks them before writing and _validate_saved on loading, so a written save always loads.
func _envelope_bounds_problem(data: Dictionary) -> Dictionary:
	if not _json_safe(data, MAX_SAVE_BYTES):
		return _failure("save_size", "save", "The creation save exceeds its structural limits.")
	if not data.get("instances") is Array or data.instances.size() > int(WORLD_LIMITS.instances) or not data.get("receipts") is Dictionary or data.receipts.size() > MAX_RECEIPTS:
		return _failure("save_size", "save", "Saved instances or receipts exceed their bounds.")
	if not data.get("locks") is Dictionary or data.locks.size() > MAX_LOCKS or not data.get("compacted", {}) is Dictionary or data.get("compacted", {}).size() > MAX_COMPACTED or not data.get("checkpoints", []) is Array or data.get("checkpoints", []).size() > MAX_CHECKPOINTS:
		return _failure("save_size", "save", "Saved locks, compacted receipts or checkpoints exceed their bounds.")
	if not data.get("object_poses", {}) is Dictionary or data.get("object_poses", {}).size() > _room_entities.size():
		return _failure("save_size", "save", "Saved object poses exceed their bounds.")
	return {}


func _validate_saved(data: Dictionary) -> Dictionary:
	var expected := ["schema", "version", "compiler_version", "style_version", "room_pin", "revision", "permission_revision", "next_id", "roles", "consent", "instances", "receipts", "locks", "entity_revisions"]
	if _whole(data.get("version")) and int(data.version) >= 3:
		expected.append_array(["compacted", "checkpoints"])
	if _whole(data.get("version")) and int(data.version) >= 4:
		expected.append("object_poses")
	if _whole(data.get("version")) and int(data.version) >= 5:
		expected.append("team")
	if _whole(data.get("version")) and int(data.version) >= 6:
		expected.append("nature")
	if not _exact_keys(data, expected) or not SCHEMA_IDS.is_schema(data.get("schema"), "creation-world") or not _whole(data.get("version")) or int(data.version) not in LOADABLE_VERSIONS or not _whole(data.get("compiler_version")) or int(data.compiler_version) != 1 or data.get("style_version") != STYLE_VERSION or data.get("room_pin") != _room_pin():
		return _failure("save_incompatible", "save", "The save's room, compiler or style version does not match this room.")
	var bounded := _envelope_bounds_problem(data)
	if not bounded.is_empty():
		return _failure("save_invalid", "save", bounded.message)
	if not _whole(data.revision) or int(data.revision) < 0 or not _whole(data.permission_revision) or int(data.permission_revision) < 0 or not _whole(data.next_id) or int(data.next_id) < 1:
		return _failure("save_invalid", "revision", "Saved revisions or identity counters are invalid.")
	if not data.roles is Dictionary or not _exact_keys(data.roles, PRINCIPALS) or data.roles[PLAYER] != "owner" or data.roles[COMPANION] not in ["editor", "visitor"]:
		return _failure("save_invalid", "roles", "Saved roles are invalid.")
	if not data.consent is Dictionary or not _exact_keys(data.consent, PRINCIPALS) or not data.consent[PLAYER] is bool or not data.consent[COMPANION] is bool:
		return _failure("save_invalid", "consent", "Saved consent is invalid.")
	if not data.instances is Array or data.instances.size() > int(WORLD_LIMITS.instances) or not data.receipts is Dictionary or data.receipts.size() > MAX_RECEIPTS:
		return _failure("save_invalid", "instances", "Saved instances or receipts exceed their bounds.")
	var rebuilt: Dictionary = {}
	for item in data.instances:
		if not item is Dictionary or not _exact_keys(item, ["id", "owner_id", "revision", "source", "position_m", "yaw_deg"]):
			return _failure("save_invalid", "instances", "Saved instance fields are invalid.")
		if not item.id is String or not String(item.id).begins_with("creation:") or rebuilt.has(item.id) or item.owner_id not in PRINCIPALS or not _whole(item.revision) or int(item.revision) < 1 or int(item.revision) > int(data.revision):
			return _failure("save_invalid", "instances.id", "Saved identity or revision is invalid.")
		var suffix := String(item.id).trim_prefix("creation:")
		if not suffix.is_valid_int() or int(suffix) < 1 or int(suffix) >= int(data.next_id) or item.id != "creation:%08d" % int(suffix):
			return _failure("save_invalid", "instances.id", "Saved instance identity is not host-assigned.")
		if not item.position_m is Array or item.position_m.size() != 3 or not _number(item.position_m[1]):
			return _failure("save_invalid", "instances.position_m", "Saved placement is invalid.")
		var compiled: Dictionary = COMPILER.compile(item.source)
		if not compiled.ok:
			return _failure("save_source_invalid", compiled.get("path", "source"), compiled.get("message", "The saved source did not compile."))
		# Locks applied after a creation was placed may cover it; on reload only the room and its surfaces are rechecked.
		var placed := _validate_placement(compiled.artifact, item.position_m[0], item.position_m[2], item.yaw_deg, item.position_m[1])
		if not placed.ok or abs(float(placed.get("position_m", [0, INF, 0])[1]) - float(item.position_m[1])) > 0.00001:
			return _failure("save_placement_invalid", "instances.position_m", "Saved placement does not pass current room and surface checks.")
		var rebuilt_item: Dictionary = item.duplicate(true)
		rebuilt_item.revision = int(item.revision)
		rebuilt_item.artifact = compiled.artifact
		rebuilt_item.source = compiled.artifact.source
		rebuilt_item.active = true
		rebuilt[item.id] = rebuilt_item
	var capacity := _validate_capacity(rebuilt)
	if not capacity.ok:
		return capacity
	for key in data.receipts:
		var entry: Variant = data.receipts[key]
		if not entry is Dictionary or not _exact_keys(entry, ["principal", "action_id", "fingerprint", "receipt", "meta"]) or entry.principal not in PRINCIPALS or not _token(entry.action_id) or key != entry.principal + "|" + entry.action_id or not _hash(entry.fingerprint):
			return _failure("save_receipt_invalid", "receipts", "Saved action identity or fingerprint is invalid.")
		var receipt: Variant = entry.receipt
		# A note's receipt names its journal entry and a nature change's carries its result data; no other receipt does.
		var receipt_keys := ["ok", "instance_id", "revision", "permission_revision", "replayed", "affected", "created"]
		if entry.meta is Dictionary and entry.meta.get("op") == "journal.note":
			receipt_keys.append("entry_id")
		if entry.meta is Dictionary and entry.meta.get("op") in NATURE_OPS:
			receipt_keys.append("data")
		if not receipt is Dictionary or not _exact_keys(receipt, receipt_keys) or (receipt.has("entry_id") and (not receipt.entry_id is String or not _pattern(ENTRY_ID).search(receipt.entry_id))) or (receipt.has("data") and (not receipt.data is Dictionary or receipt.data.size() > 32)) or receipt.ok != true or receipt.replayed != false or not receipt.instance_id is String or (not receipt.instance_id.is_empty() and not receipt.instance_id.begins_with("creation:")) or not _whole(receipt.revision) or int(receipt.revision) < 0 or int(receipt.revision) > int(data.revision) or not _whole(receipt.permission_revision) or int(receipt.permission_revision) < 0 or int(receipt.permission_revision) > int(data.permission_revision) or not _entity_list(receipt.affected) or not _entity_list(receipt.created):
			return _failure("save_receipt_invalid", "receipts.receipt", "Saved receipt revisions are invalid.")
		if _receipt_meta(entry.principal, entry.meta).is_empty() or not _exact_keys(entry.meta, ["op", "at_utc", "approved_by"]):
			return _failure("save_receipt_invalid", "receipts.meta", "Saved receipt metadata is invalid.")
		receipt.revision = int(receipt.revision)
		receipt.permission_revision = int(receipt.permission_revision)
	for key in data.get("compacted", {}):
		var entry: Variant = data.compacted[key]
		var parts := String(key).split("|")
		if data.receipts.has(key) or parts.size() != 2 or parts[0] not in PRINCIPALS or not _token(parts[1]) or not entry is Array or entry.size() != 2 or not _whole(entry[0]) or int(entry[0]) < 0 or int(entry[0]) > int(data.revision) or not entry[1] is String or String(entry[1]).length() != 16 or not _hash(String(entry[1]) + "0".repeat(48)):
			return _failure("save_receipt_invalid", "compacted", "A saved compacted receipt is invalid.")
	for checkpoint in data.get("checkpoints", []):
		if not checkpoint is Dictionary or not _exact_keys(checkpoint, ["id", "revision", "label"]) or not checkpoint.id is String or not _pattern(CHECKPOINT_ID).search(checkpoint.id) or not _whole(checkpoint.revision) or int(checkpoint.revision) < 0 or int(checkpoint.revision) > int(data.revision) or not checkpoint.label is String or checkpoint.label.length() > 80 or TEXT.has_hidden(checkpoint.label):
			return _failure("save_invalid", "checkpoints", "A saved checkpoint is invalid.")
	if not data.locks is Dictionary or data.locks.size() > MAX_LOCKS:
		return _failure("save_invalid", "locks", "Saved locks are invalid.")
	for entity_id in data.locks:
		var lock: Variant = data.locks[entity_id]
		var known: bool = rebuilt.has(entity_id) or (_room_entities.has(entity_id) and String(entity_id).begins_with("obj:"))
		if not known or not lock is Dictionary or not _exact_keys(lock, ["locked_by", "locked_revision"]) or lock.locked_by not in PRINCIPALS or not _whole(lock.locked_revision) or int(lock.locked_revision) < 1 or int(lock.locked_revision) > int(data.revision):
			return _failure("save_invalid", "locks", "A saved lock names something not in this room.")
	if not data.entity_revisions is Dictionary:
		return _failure("save_invalid", "entity_revisions", "Saved object revisions are invalid.")
	for entity_id in data.entity_revisions:
		if not _room_entities.has(entity_id) or not _whole(data.entity_revisions[entity_id]) or int(data.entity_revisions[entity_id]) < 1 or int(data.entity_revisions[entity_id]) > int(data.revision):
			return _failure("save_invalid", "entity_revisions", "A saved object revision names something not in this room.")
	# A moved object has been changed by play, so it has a revision of its own.
	for entity_id in data.get("object_poses", {}):
		if not data.entity_revisions.has(entity_id):
			return _failure("save_invalid", "object_poses", "A saved object pose has no object revision.")
	return {"ok": true, "instances": rebuilt}


## The saved object poses, checked: room objects only, each pose inside the room. Saves before version 4 have none.
func _saved_poses(data: Dictionary) -> Dictionary:
	# Only a version 4 save of this room moves its objects; any other save fails in _validate_saved for its own reason.
	if data.get("room_pin") != _room_pin() or not _whole(data.get("version")) or int(data.version) < 4:
		return {"ok": true, "poses": {}}
	var saved: Variant = data.get("object_poses", {})
	if not saved is Dictionary or saved.size() > _room_entities.size():
		return _failure("save_invalid", "object_poses", "Saved object poses are invalid.")
	var poses := {}
	for entity_id in saved:
		var pose: Variant = saved[entity_id]
		if not entity_id is String or not _room_entities.has(entity_id) or _room_entities[entity_id].kind != "object" or not pose is Dictionary or not _exact_keys(pose, ["position_m", "rotation", "bounds"]):
			return _failure("save_invalid", "object_poses", "A saved object pose names something not in this room.")
		var checked := _pose_problem(pose)
		if not checked.ok:
			return _failure("save_invalid", "object_poses", "A saved object pose is outside this room.")
		poses[entity_id] = checked.pose
	return {"ok": true, "poses": poses}


## Every durable receipt names its contract op and commit time, so receipt.lookup can rebuild the
## contract result even for commits that came through the runtime directly.
func _complete_meta(meta: Dictionary, op: String) -> Dictionary:
	var complete := meta.duplicate()
	if String(complete.op).is_empty():
		complete.op = {"place": "creation.place", "revise": "creation.revise", "remove": "entity.remove", "lock": "protect.lock", "unlock": "protect.unlock", "activate": "creation.activate", "checkpoint": "room.checkpoint", "move": "entity.place", "note": "journal.note", "nature": "nature.cast"}[op]
	if String(complete.at_utc).is_empty():
		complete.at_utc = Time.get_datetime_string_from_system(true) + "Z"
	return complete


## Normalized receipt metadata from the trusted host, or {} when it is malformed.
func _receipt_meta(principal: String, supplied: Variant) -> Dictionary:
	if not supplied is Dictionary:
		return {}
	for field in supplied:
		if field not in ["fingerprint", "op", "at_utc", "approved_by"] or not supplied[field] is String:
			return {}
	if supplied.has("fingerprint") and not _hash(supplied.fingerprint):
		return {}
	var approved: String = supplied.get("approved_by", "")
	if not approved.is_empty() and (approved != PLAYER or principal == PLAYER):
		return {}
	var op: String = supplied.get("op", "")
	if not op.is_empty() and not _pattern(META_OP).search(op):
		return {}
	var at: String = supplied.get("at_utc", "")
	if not at.is_empty() and not _pattern(META_AT).search(at):
		return {}
	return {"op": op, "at_utc": at, "approved_by": approved}


func _check_room(room: Dictionary) -> Dictionary:
	var invalid := {"ok": false}
	if not _json_safe(room, MAX_ROOM_NODES) or not room.get("room_id") is String or not _pattern(ROOM_ID).search(room.room_id) or not _hash(room.get("manifest_sha256")):
		return invalid
	var bounds := _bounds(room.get("bounds"))
	if bounds.is_empty():
		return invalid
	var entities: Dictionary = {}
	var listed: Variant = room.get("entities", {})
	if not listed is Dictionary or listed.size() > 4096:
		return invalid
	var pattern := _pattern(ROOM_ENTITY_ID)
	for entity_id in listed:
		var entity: Variant = listed[entity_id]
		if not entity_id is String or not pattern.search(entity_id) or not entity is Dictionary:
			return invalid
		var extent := _bounds(entity)
		if extent.is_empty():
			return invalid
		entities[entity_id] = {"kind": "shell" if String(entity_id).begins_with("shell:") else "object", "min": extent[0], "max": extent[1]}
	return {"ok": true, "min": bounds[0], "max": bounds[1], "entities": entities}


func _bounds(value: Variant) -> Array:
	if not value is Dictionary or not value.get("min_m") is Array or not value.get("max_m") is Array or value.min_m.size() != 3 or value.max_m.size() != 3:
		return []
	for axis in range(3):
		for corner in [value.min_m, value.max_m]:
			if not _number(corner[axis]) or abs(float(corner[axis])) > COORDINATE_LIMIT_M:
				return []
		if float(value.min_m[axis]) > float(value.max_m[axis]):
			return []
	return [Vector3(float(value.min_m[0]), float(value.min_m[1]), float(value.min_m[2])), Vector3(float(value.max_m[0]), float(value.max_m[1]), float(value.max_m[2]))]


func _room_pin() -> Dictionary:
	return {"room_id": _room_id, "manifest_sha256": _manifest_sha256}


func _available() -> bool:
	return _configured and not _load_required and (not access_guard.is_valid() or bool(access_guard.call()))


func _can_build(principal: String) -> bool:
	return _roles.get(principal, "") in ["owner", "editor"]


func _failure(code: String, path: String, message: String) -> Dictionary:
	last_error = code
	return {"ok": false, "code": code, "path": path, "message": message}


static func _array3(value: Vector3) -> Array:
	return [value.x, value.y, value.z]


static func _entity_list(value: Variant) -> bool:
	if not value is Array or value.size() > MAX_TARGETS:
		return false
	for item in value:
		if not item is String or not _pattern(ENTITY_ID).search(item):
			return false
	return true


static func _number(value: Variant) -> bool:
	return (value is int or value is float) and is_finite(float(value))


static func _whole(value: Variant) -> bool:
	return _number(value) and float(value) == floor(float(value)) and abs(float(value)) <= 2147483647.0


static func _token(value: Variant) -> bool:
	if not value is String or value.is_empty() or value.length() > 64:
		return false
	for character in value:
		if not (character >= "a" and character <= "z") and not (character >= "A" and character <= "Z") and not (character >= "0" and character <= "9") and character not in ["_", "-"]:
			return false
	return true


static func _hash(value: Variant) -> bool:
	if not value is String or value.length() != 64:
		return false
	for character in value:
		if character not in "0123456789abcdef":
			return false
	return true


static func _exact_keys(value: Dictionary, keys: Array) -> bool:
	if value.size() != keys.size():
		return false
	for key in keys:
		if not value.has(key):
			return false
	return true


## JSON-shaped, finite, at most 24 levels deep and at most max_nodes values. Every JSON value takes at least
## one byte of its text, so a budget equal to a byte limit never refuses what fits that limit: requests use
## MAX_REQUEST_BYTES, saves MAX_SAVE_BYTES (review: a fixed 32,768 refused saves past about 1,930 receipts).
static func _json_safe(value: Variant, max_nodes: int, depth := 0, visited: Array = []) -> bool:
	if visited.is_empty():
		visited.append(0)
	visited[0] += 1
	if depth > 24 or int(visited[0]) > max_nodes:
		return false
	if value == null or value is bool or value is String:
		return true
	if value is int or value is float:
		return is_finite(float(value))
	if value is Array:
		for item in value:
			if not _json_safe(item, max_nodes, depth + 1, visited):
				return false
		return true
	if value is Dictionary:
		for key in value:
			if not key is String or not _json_safe(value[key], max_nodes, depth + 1, visited):
				return false
		return true
	return false


const META_OP := "\\A[a-z]+\\.[a-z_]+\\z"
const META_AT := "\\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\\.[0-9]{1,6})?Z\\z"
const ROOM_ID := "\\A[a-z][a-z0-9_-]{0,63}\\z"
const ROOM_ENTITY_ID := "\\A(shell|obj):[A-Za-z0-9_-]{1,64}\\z"
const ENTITY_ID := "\\A(shell|obj|creation|avatar|effect|edit):[A-Za-z0-9_-]{1,64}\\z"
const CHECKPOINT_ID := "\\Acp[0-9]{4,9}\\z"
const ENTRY_ID := "\\Aentry-[a-z2-7]{26}\\z"
static var _patterns: Dictionary = {}


## Anchored patterns use \A and \z, never ^ and $: PCRE's $ also matches before a final newline.
static func _pattern(source: String) -> RegEx:
	if not _patterns.has(source):
		_patterns[source] = RegEx.create_from_string(source)
	return _patterns[source]
