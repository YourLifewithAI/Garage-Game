extends Node3D
## Room adapter and bounded capability interpreter for creations. Recipes are data only.
## It renders creations from the authority's snapshot, runs their effects on the real C# bodies
## (player:local and companion:local, SmallPlayerController and CompanionAvatar), and answers the
## authority's physics surface queries. In the game, Kernel/CommandHost.cs sets command_sink so
## manual commits travel the same garage_game.command path as the companion's.
const AUTHORITY = preload("res://scripts/creation_authority.gd")
const VISUALS = preload("res://scripts/creation_visuals.gd")
const EXECUTION = preload("res://scripts/creation_execution.gd")
const SCHEMA_IDS = preload("res://scripts/schema_ids.gd")
const PLAYER := "player:local"
const COMPANION := "companion:local"
const MAX_FIELDS := 16
## A ground device can be used from this far away (the room is a few metres across).
const ACTIVATION_RANGE_M := 2.0
const PLACEMENT_AHEAD_M := 0.6
const PLACEMENT_SNAP_M := 0.05

var room: Dictionary = {}
var player: CharacterBody3D
var companion: CharacterBody3D
var authority
var save_path := "user://saves/rooms/inventions.json"
## The invention workshop (the part-by-part editor, the INVENTIONS panel and the keys B, F, E, V, Q and K) is gone:
## the founder retired it on 6 October 2026, and its code went with the geography-era templates on 9 October. This
## runtime binds no key and builds no interface; it renders creations and runs their effects and trigger graphs.
## Optional: Callable(command: Dictionary) -> Dictionary returning a garage_game.result.
var command_sink := Callable()
## Optional: Callable(poses: Dictionary), the command host's scene seam for objects play has moved (the
## authority's pose_sink). Set before this node enters the tree: a save's poses reach the scene while it loads.
var object_pose_sink := Callable()
## Optional: Callable(poses: Dictionary) -> Dictionary, the command host's check of a save's object poses (the
## authority's pose_check). Set before this node enters the tree.
var object_pose_check := Callable()
## Optional: Callable(team: Dictionary) -> Dictionary, the command host's check of a save's journal and discovered map
## (the authority's team_check). Set before this node enters the tree.
var team_check := Callable()
## Optional: Callable(nature: Dictionary, revision: int) -> Dictionary, the command host's check of a save's nature block
## (the authority's nature_check). Set before this node enters the tree.
var nature_check := Callable()
var assemblies: Dictionary = {}
var instances: Dictionary = {}
## Trusted host-derived scorch status. Recipes and commands cannot supply it; repair restores the original graph.
var nature_disabled_creations: Dictionary = {}
var fields: Array = []
var animations: Array = []
var next_trigger: Dictionary = {}
var clock_s := 0.0
var action_counter := 0
var seen_revision := -1
var seen_permissions := -1
## The last notice (the command host's save notice among them). Nothing displays it.
var message := ""
var activation_count := 0
var evaluated_nodes := 0
var rejected_activations := 0
var session_token := ""
var field_display: MultiMeshInstance3D
var local_consent := false
var placement_height_hint: Variant = null
var _creation_bodies: Array[RID] = []


## room: {room_id, manifest_sha256, bounds: {min_m, max_m}, entities}; bodies are the C# avatars.
func configure(room_data: Dictionary, player_body: CharacterBody3D, companion_body: CharacterBody3D = null) -> void:
	room = room_data.duplicate(true)
	player = player_body
	companion = companion_body


func _ready() -> void:
	process_physics_priority = -20
	session_token = Crypto.new().generate_random_bytes(8).hex_encode()
	authority = AUTHORITY.new()
	var configured: Dictionary = authority.configure(room, Callable(self, "surface_at"), save_path)
	authority.pose_sink = object_pose_sink
	authority.pose_check = object_pose_check
	authority.team_check = team_check
	authority.nature_check = nature_check
	var loaded: Dictionary = authority.load_saved() if configured.ok else configured
	authority.occupancy_query = Callable(self, "_check_occupancy")
	authority.activation_query = Callable(self, "_check_activation")
	_make_field_display()
	if not loaded.ok:
		notice("Invention save could not open: " + loaded.message)
	_refresh()


## The authority's surface query: the first room support below from_y at (x, z). Creations are not
## supports (a revision must not stand on itself); bodies are on other collision layers.
func surface_at(x: float, z: float, from_y: float) -> Dictionary:
	if not is_inside_tree() or not is_finite(x) or not is_finite(z) or not is_finite(from_y):
		return {"ok": false}
	var top := minf(from_y, float(room.bounds.max_m[1]) - 0.001)
	var bottom := float(room.bounds.min_m[1]) - 0.5
	var query := PhysicsRayQueryParameters3D.create(Vector3(x, top, z), Vector3(x, bottom, z), 1)
	query.exclude = _creation_bodies
	var hit := get_world_3d().direct_space_state.intersect_ray(query)
	if hit.is_empty():
		return {"ok": false}
	var entity := ""
	var node: Variant = hit.collider
	while node is Node and entity.is_empty():
		if node.has_meta("entity_id"):
			entity = String(node.get_meta("entity_id"))
		node = node.get_parent()
	return {"ok": true, "height_m": hit.position.y, "entity_id": entity}


func notice(text: String) -> void:
	message = text


func placement_limits() -> Dictionary:
	var bounds: Dictionary = room.get("bounds", {"min_m": [-2, 0, -2], "max_m": [2, 2, 2]})
	return {"x_min": float(bounds.min_m[0]), "x_max": float(bounds.max_m[0]), "z_min": float(bounds.min_m[2]), "z_max": float(bounds.max_m[2]), "step": PLACEMENT_SNAP_M}


func _action() -> String:
	action_counter += 1
	return "local_" + session_token + "_" + str(action_counter)


func _bodies() -> Array:
	var result: Array = []
	for body in [player, companion]:
		if body != null and is_instance_valid(body):
			result.append(body)
	return result


func _body_box(body: CharacterBody3D, position: Vector3) -> AABB:
	var radius := float(body.BodyRadiusM)
	return AABB(position - Vector3(radius, 0.0, radius), Vector3(radius * 2.0, float(body.BodyHeightM), radius * 2.0))


func _check_occupancy(artifact: Dictionary, point: Vector3, yaw_deg: float, _excluding: String) -> Dictionary:
	if artifact.source.mount != "ground":
		return {"ok": true}
	var minimum := VISUALS.vector(artifact.bounds.min)
	var maximum := VISUALS.vector(artifact.bounds.max)
	var box := Transform3D(Basis(Vector3.UP, deg_to_rad(yaw_deg)), point) * AABB(minimum, maximum - minimum)
	for body in _bodies():
		if box.intersects(_body_box(body, body.global_position)):
			return {"ok": false, "code": "occupied_placement", "path": "placement", "message": "Give the player and the companion room. Move this invention away from them."}
	return {"ok": true}


func _check_activation(principal: String, item: Dictionary) -> Dictionary:
	if item.source.mount == "avatar":
		return {"ok": item.owner_id == principal, "message": "Only the wearer can operate a worn design."}
	var actor: Node3D = player if principal == PLAYER else companion
	return {"ok": actor != null and actor.global_position.distance_to(VISUALS.vector(item.position_m)) < ACTIVATION_RANGE_M, "message": "Move within 2 m of this invention before using it."}


func default_placement() -> Dictionary:
	var point: Vector3 = player.global_position - player.global_basis.z * PLACEMENT_AHEAD_M
	var limits := placement_limits()
	placement_height_hint = player.global_position.y
	return {"x_m": clampf(snappedf(point.x, PLACEMENT_SNAP_M), limits.x_min, limits.x_max), "z_m": clampf(snappedf(point.z, PLACEMENT_SNAP_M), limits.z_min, limits.z_max), "yaw_deg": 0.0, "y_m": player.global_position.y}


func preflight_draft(source: Dictionary, instance_id: String, placement: Dictionary) -> Dictionary:
	return authority.preflight(PLAYER, source, placement.get("x_m"), placement.get("z_m"), placement.get("yaw_deg"), instance_id, placement.get("y_m", placement_height_hint))


func commit_draft(source: Dictionary, instance_id: String, placement: Dictionary, expected_revision: int, expected_permission_revision: int) -> Dictionary:
	var height: Variant = placement.get("y_m", placement_height_hint)
	var result: Dictionary
	if command_sink.is_valid():
		var args := {"source": source.duplicate(true), "placement": {"position_m": [placement.get("x_m"), height if height != null else float(room.bounds.max_m[1]), placement.get("z_m")], "rotation": _yaw_quaternion(float(placement.get("yaw_deg", 0.0)))}}
		if not instance_id.is_empty():
			args["target"] = instance_id
		result = _send("creation.place" if instance_id.is_empty() else "creation.revise", args, expected_revision)
	else:
		var request := {"op": "place" if instance_id.is_empty() else "revise", "action_id": _action(), "expected_revision": expected_revision, "expected_permission_revision": expected_permission_revision, "source": source.duplicate(true), "x_m": placement.get("x_m"), "z_m": placement.get("z_m"), "yaw_deg": placement.get("yaw_deg")}
		if height != null:
			request["y_m"] = height
		if not instance_id.is_empty():
			request["instance_id"] = instance_id
		result = authority.submit(PLAYER, request)
	if result.ok:
		_refresh()
		notice("Saved " + str(source.name) + ".")
	return result


func remove_creation(instance_id: String, expected_revision: int, expected_permission_revision: int) -> Dictionary:
	var result: Dictionary
	if command_sink.is_valid():
		result = _send("entity.remove", {"target": instance_id}, expected_revision)
	else:
		result = authority.submit(PLAYER, {"op": "remove", "action_id": _action(), "instance_id": instance_id, "expected_revision": expected_revision, "expected_permission_revision": expected_permission_revision})
	if result.ok:
		_refresh()
		notice("Invention removed. Its active effects have stopped.")
	return result


## Sends a manual command through the contract command host and adapts its garage_game.result
## to the {ok, instance_id, code, path, message} shape this runtime's callers read.
func _send(op: String, args: Dictionary, expected_revision: int) -> Dictionary:
	var command := {"schema": SCHEMA_IDS.id_of("command"), "version": 1, "action_id": _action(), "room_id": room.room_id, "op": op, "args": args, "expected_revision": expected_revision}
	var result: Variant = command_sink.call(command)
	if not result is Dictionary:
		return {"ok": false, "code": "internal_error", "path": "", "message": "The command host did not answer."}
	if result.get("ok", false):
		var ids: Array = result.get("created", []) + result.get("affected", [])
		return {"ok": true, "instance_id": ids[0] if not ids.is_empty() else "", "revision": result.get("revision", 0)}
	var error: Dictionary = result.get("error", {})
	return {"ok": false, "code": String(error.get("code", "internal_error")), "path": String(error.get("field_path", "")), "message": String(error.get("message", "The command was refused."))}


func set_local_consent(enabled: bool) -> Dictionary:
	var result: Dictionary = authority.set_consent(PLAYER, enabled)
	_refresh()
	return result


func set_companion_consent(enabled: bool) -> Dictionary:
	var result: Dictionary = authority.set_consent(COMPANION, enabled)
	_refresh()
	return result


func set_companion_builder(enabled: bool) -> Dictionary:
	var result: Dictionary = authority.set_role(PLAYER, COMPANION, "editor" if enabled else "visitor")
	_refresh()
	return result


func _clear_body_motion() -> void:
	for body in _bodies():
		body.ClearCreationMotion()


## The command host calls this right after a durable commit, so a creation's colliders appear or go at once and the
## next command's physics queries see the room as the state now is (otherwise they change on the next physics tick).
func refresh_now() -> void:
	_refresh()


func _refresh() -> void:
	# A small readiness/revision check avoids copying every saved source 60 times/s.
	if authority.is_ready() and seen_revision == authority.revision and seen_permissions == authority.permission_revision:
		return
	var snapshot: Dictionary = authority.snapshot(PLAYER)
	if not snapshot.ok:
		local_consent = false
		fields.clear()
		animations.clear()
		next_trigger.clear()
		instances.clear()
		_clear_body_motion()
		for assembly in assemblies.values():
			remove_child(assembly)
			assembly.queue_free()
		assemblies.clear()
		_creation_bodies.clear()
		seen_revision = -1
		seen_permissions = -1
		return
	if seen_revision == int(snapshot.revision) and seen_permissions == int(snapshot.permission_revision):
		return
	# Recompile-backed replacement invalidates every effect and transient reference.
	fields.clear()
	animations.clear()
	next_trigger.clear()
	_clear_body_motion()
	for assembly in assemblies.values():
		remove_child(assembly)
		assembly.queue_free()
	assemblies.clear()
	_creation_bodies.clear()
	instances = snapshot.instances
	local_consent = bool(snapshot.consent[PLAYER])
	for item in instances.values():
		var assembly: Node3D = VISUALS.build(item.artifact, item.source.mount == "ground" and item.active)
		# Placed before it enters the tree: its colliders join the physics world where the creation is, so the command
		# host's queries see them at once (a transform set afterwards reaches the physics server only on the next flush).
		assembly.position = VISUALS.vector(item.position_m)
		assembly.rotation.y = deg_to_rad(float(item.yaw_deg))
		add_child(assembly)
		assemblies[item.id] = assembly
		_collect_bodies(assembly)
	seen_revision = int(snapshot.revision)
	seen_permissions = int(snapshot.permission_revision)
	notice(message)


func _collect_bodies(node: Node) -> void:
	if node is CollisionObject3D:
		_creation_bodies.append(node.get_rid())
	for child in node.get_children():
		_collect_bodies(child)


func nearest_creation() -> String:
	var closest := ""
	var distance := ACTIVATION_RANGE_M
	for id in assemblies:
		if instances[id].source.mount != "ground":
			continue
		var candidate: float = assemblies[id].global_position.distance_to(player.global_position)
		if candidate < distance:
			closest = id
			distance = candidate
	return closest


func equipped_creation(owner := PLAYER) -> String:
	for id in instances:
		if instances[id].source.mount == "avatar" and instances[id].owner_id == owner:
			return id
	return ""


## Manual Use. With a command host attached this is creation.activate through garage_game.command.
func activate_creation(id: String, principal := PLAYER) -> Dictionary:
	_refresh()
	if command_sink.is_valid() and principal == PLAYER:
		return _send("creation.activate", {"target": id}, authority.revision)
	return execute_activation(principal, {"op": "activate", "action_id": _action(), "instance_id": id, "expected_revision": authority.revision, "expected_permission_revision": authority.permission_revision})


## The one place an activation is admitted and run: the authority admits it (transient receipt),
## then the reachable graph fires. A replayed receipt never runs the graph again. When the graph cannot
## fire (no Use trigger, a busy budget), the receipt is dropped again, so a retry runs instead of
## replaying a success that never happened.
func execute_activation(principal: String, request: Dictionary, receipt_meta: Dictionary = {}) -> Dictionary:
	if nature_disabled_creations.has(String(request.get("instance_id", ""))):
		return {"ok": false, "code": "activation_context", "path": "instance_id", "message": "Repair this scorched creation before using it."}
	var result: Dictionary = authority.submit(principal, request, receipt_meta)
	if not result.ok or result.get("replayed", false):
		return result
	var id: String = result.instance_id
	var fired := {"ok": false, "code": "activation_context", "path": "instance_id", "message": "This design uses a timer, sensor or passive glide. It has no Use trigger."}
	for node in instances[id].source.nodes if instances.has(id) else []:
		if node.op == "interact":
			fired = _fire(id, node.id, principal, principal)
			break
	if not fired.ok:
		authority.forget_activation(principal, String(request.get("action_id", "")))
		return fired
	return result


## Stops running creation effects (fields and animations) for effect.stop and goal.stop: every effect for
## "", otherwise only the effects owner started. An effect belongs to whoever started it: the principal who
## used the creation, or for timers and sensors the creation's owner. The command host passes "" for the
## player's stop-all, which covers everything the player directs, and the companion's own principal for
## the companion's stops.
func stop_effects(owner := "") -> int:
	var all := owner.is_empty()
	var principal := owner
	var kept_fields: Array = []
	var kept_animations: Array = []
	var stopped := 0
	for field in fields:
		if all or field.by == principal:
			stopped += 1
		else:
			kept_fields.append(field)
	for animation in animations:
		if all or animation.by == principal:
			stopped += 1
			if is_instance_valid(animation.part) and animation.op == "light":
				var lamp = animation.part.get_node_or_null("Light_" + animation.node_id)
				if lamp:
					lamp.light_energy = 0.0
		else:
			kept_animations.append(animation)
	fields = kept_fields
	animations = kept_animations
	if stopped > 0:
		_clear_body_motion()
	return stopped


## How many running effects principal started (all of them for "").
func effect_count(principal := "") -> int:
	var count := 0
	for effect in fields + animations:
		if principal.is_empty() or effect.by == principal:
			count += 1
	return count


func _fire(id: String, trigger: String, principal: String, by := "") -> Dictionary:
	if nature_disabled_creations.has(id):
		return {"ok": false, "code": "activation_context", "path": "instance_id", "message": "This creation is scorched."}
	if by.is_empty():
		by = principal
	if not instances.has(id):
		return {"ok": false, "code": "instance_not_found", "path": "instance_id", "message": "That invention no longer exists."}
	var item: Dictionary = instances[id]
	var ordered: Array = EXECUTION.plan(item.artifact, trigger)
	var field_count := 0
	for node in ordered:
		if node.op == "wind":
			field_count += 1
	if fields.size() + field_count > MAX_FIELDS:
		rejected_activations += 1
		return {"ok": false, "code": "runtime_budget", "path": "runtime", "message": "The room's wind capacity is busy. Try again shortly."}
	var admitted: Dictionary = authority.consume_runtime_budget(id, principal, field_count, ordered.size(), clock_s)
	if not admitted.ok:
		rejected_activations += 1
		return admitted
	activation_count += 1
	evaluated_nodes += ordered.size()
	var parts: Dictionary = assemblies[id].get_meta("parts")
	for node in ordered:
		var part: Node3D = parts[node.part_id]
		var params: Dictionary = node.params
		if node.op == "wind":
			fields.append({"instance_id": id, "owner": item.owner_id, "by": by, "part": part, "direction": VISUALS.vector(params.direction), "acceleration": float(params.acceleration_mps2), "radius": float(params.radius_m), "until": clock_s + float(params.duration_s)})
		elif node.op in ["spin", "light"]:
			# Same node replaces its earlier animation; no growing duplicate jobs.
			animations = animations.filter(func(a): return not (a.instance_id == id and a.node_id == node.id))
			animations.append({"instance_id": id, "node_id": node.id, "by": by, "part": part, "op": node.op, "params": params, "until": clock_s + float(params.duration_s), "angle": 0.0, "base": part.get_meta("authored_basis")})
	return {"ok": true}


func _physics_process(delta: float) -> void:
	clock_s += delta
	_refresh()
	# Worn designs attach to their wearer's feet; no user-supplied target IDs.
	for id in assemblies:
		var item: Dictionary = instances[id]
		if item.source.mount == "avatar":
			var wearer: Node3D = player if item.owner_id == PLAYER else companion
			if wearer == null:
				continue
			assemblies[id].global_transform = wearer.global_transform
			assemblies[id].visible = item.owner_id != PLAYER or player.get("EyeCamera") == null or not player.EyeCamera.current
	fields = fields.filter(func(field): return not nature_disabled_creations.has(field.instance_id) and clock_s < field.until and is_instance_valid(field.part) and instances.has(field.instance_id) and instances[field.instance_id].active)
	for id in instances:
		var item: Dictionary = instances[id]
		if not item.active or nature_disabled_creations.has(id):
			continue
		var parts: Dictionary = assemblies[id].get_meta("parts")
		for node in item.source.nodes:
			if node.op not in ["timer", "proximity"]:
				continue
			var key: String = id + "/" + node.id
			if clock_s < float(next_trigger.get(key, 0.0)):
				continue
			next_trigger[key] = clock_s + float(node.params.interval_s)
			var actor: String = item.owner_id
			if node.op == "proximity":
				actor = ""
				for target in [PLAYER, COMPANION]:
					var body: Node3D = player if target == PLAYER else companion
					if body == null:
						continue
					var point := body.global_position
					if authority.can_affect(item.owner_id, target, point) and point.distance_to(parts[node.part_id].global_position) <= float(node.params.radius_m):
						actor = target
						break
			if not actor.is_empty():
				_fire(id, node.id, actor, item.owner_id)
	_step_animations(delta)
	if field_display:
		field_display.multimesh.visible_instance_count = fields.size()
		for index in range(fields.size()):
			var field: Dictionary = fields[index]
			var radius: float = field.radius
			field_display.multimesh.set_instance_transform(index, Transform3D(Basis.IDENTITY.scaled(Vector3(radius, 0.12, radius)), field.part.global_position))
	if player:
		var effect := effect_at(PLAYER, player.global_position)
		player.SetCreationEffects(effect.acceleration, effect.glide, Callable(self, "_player_position_allowed") if effect.allowed else Callable())
	if companion:
		var effect := effect_at(COMPANION, companion.global_position)
		companion.SetCreationEffects(effect.acceleration, effect.glide, Callable(self, "_companion_position_allowed") if effect.allowed else Callable())


func _step_animations(delta: float) -> void:
	var remaining: Array = []
	for animation in animations:
		if not is_instance_valid(animation.part):
			continue
		if nature_disabled_creations.has(animation.instance_id):
			if animation.op == "light":
				var scorched_lamp = animation.part.get_node_or_null("Light_" + animation.node_id)
				if scorched_lamp:
					scorched_lamp.light_energy = 0.0
			continue
		if animation.op == "light":
			var lamp = animation.part.get_node_or_null("Light_" + animation.node_id)
			if lamp:
				lamp.light_energy = float(animation.params.intensity) if clock_s < animation.until else 0.0
		elif animation.op == "spin":
			var bounded_delta := minf(delta, maxf(0.0, float(animation.until) - (clock_s - delta)))
			animation.angle = fposmod(float(animation.angle) + float(animation.params.speed_rpm) * TAU / 60.0 * bounded_delta, TAU)
			animation.part.basis = animation.base * Basis(Vector3.UP, animation.angle)
		if clock_s < animation.until:
			remaining.append(animation)
	animations = remaining


func effect_at(target: String, position: Vector3) -> Dictionary:
	var acceleration := Vector3.ZERO
	var glide := 0.0
	var body: Node3D = player if target == PLAYER else companion
	for field in fields:
		if nature_disabled_creations.has(field.instance_id):
			continue
		if _body_allowed(field.owner, target, position, body) and authority.can_affect(field.owner, target, field.part.global_position, field.instance_id) and position.distance_to(field.part.global_position) <= field.radius:
			acceleration += field.part.get_parent().global_basis * field.part.get_meta("authored_basis") * field.direction * field.acceleration
	for item in instances.values():
		if nature_disabled_creations.has(item.id):
			continue
		if item.source.mount == "avatar" and item.owner_id == target and _body_allowed(item.owner_id, target, position, body):
			for node in item.source.nodes:
				if node.op == "glide":
					glide = float(node.params.fall_speed_mps)
	return {"acceleration": acceleration.limit_length(4.0), "glide": glide, "allowed": not acceleration.is_zero_approx() or glide > 0.0}


func _player_position_allowed(position: Vector3) -> bool:
	# All admitted force sources share the room; owner ACL is rechecked in effect_at.
	return _body_allowed(PLAYER, PLAYER, position, player)


func _companion_position_allowed(position: Vector3) -> bool:
	return _body_allowed(PLAYER, COMPANION, position, companion)


func _body_allowed(owner: String, target: String, position: Vector3, body: Node3D) -> bool:
	var radius := float(body.BodyRadiusM) if body != null and body.get("BodyRadiusM") != null else 0.02
	for offset in [Vector3(-radius, 0, -radius), Vector3(radius, 0, -radius), Vector3(-radius, 0, radius), Vector3(radius, 0, radius)]:
		if not authority.can_affect(owner, target, position + offset):
			return false
	return true


func _make_field_display() -> void:
	field_display = MultiMeshInstance3D.new()
	field_display.name = "BoundedWindIndicators"
	var ring := TorusMesh.new()
	ring.inner_radius = 0.98
	ring.outer_radius = 1.0
	ring.rings = 24
	ring.ring_segments = 4
	var paint := StandardMaterial3D.new()
	paint.albedo_color = Color("acd6c6")
	paint.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	ring.material = paint
	var batch := MultiMesh.new()
	batch.transform_format = MultiMesh.TRANSFORM_3D
	batch.mesh = ring
	batch.instance_count = MAX_FIELDS
	batch.visible_instance_count = 0
	field_display.multimesh = batch
	add_child(field_display)


static func _yaw_quaternion(yaw_deg: float) -> Array:
	var half := deg_to_rad(yaw_deg) * 0.5
	return [0.0, sin(half), 0.0, cos(half)]
