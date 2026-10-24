extends SceneTree
## The real authority, capability interpreter and C# bodies (SmallPlayerController as player:local,
## CompanionAvatar as companion:local) on real physics floors. Only the room description is a fixture:
## a 120 x 120 m test hall whose floor top is y = 0, with obj:garden as a lockable zone.
const GUARD = preload("res://tests/kernel_test_guard.gd")
## Fails the suite on any script or engine error (kernel_test_guard.gd).
var guard = GUARD.new()
const Runtime = preload("res://scripts/invention_runtime.gd")
const Player = preload("res://scripts/native/SmallPlayerController.cs")
const Companion = preload("res://scripts/native/CompanionAvatar.cs")
const ManagedShutdown = preload("res://tests/native/ManagedShutdown.cs")
const PLAYER := "player:local"
const COMPANION := "companion:local"
const SAVE_PATH := "user://tests/manual_invention_runtime.json"
const ROOM := {
	"room_id": "runtime_fixture",
	"manifest_sha256": "5555555555555555555555555555555555555555555555555555555555555555",
	"bounds": {"min_m": [-60, -1, 290], "max_m": [60, 30, 410]},
	"entities": {"obj:garden": {"min_m": [-15, 0, 380], "max_m": [15, 1, 410]}},
}

var failures := 0
var checks := 0
var world: Node3D
var player
var companion
var runtime


func _initialize() -> void:
	OS.add_logger(guard)
	call_deferred("_run")


func _run() -> void:
	_remove_test_save()
	world = Node3D.new()
	root.add_child(world)
	var floor := StaticBody3D.new()
	floor.set_meta("entity_id", "shell:floor")
	var floor_shape := CollisionShape3D.new()
	var floor_box := BoxShape3D.new()
	floor_box.size = Vector3(150, 1, 150)
	floor_shape.shape = floor_box
	floor.position = Vector3(0, -0.5, 350)
	floor.add_child(floor_shape)
	world.add_child(floor)
	player = Player.new()
	player.name = "RealSmallPlayer"
	player.ReadKeyboard = false
	player.position = Vector3(0, 0.003, 350)
	world.add_child(player)
	companion = Companion.new()
	companion.name = "RealCompanion"
	companion.position = Vector3(7, 0.01, 346)
	world.add_child(companion)
	runtime = Runtime.new()
	runtime.save_path = SAVE_PATH
	runtime.configure(ROOM, player, companion)
	world.add_child(runtime)
	await _frames(25)
	companion.Stop()
	if runtime.authority == null or not runtime.authority.is_ready() or not is_instance_valid(runtime.field_display):
		_expect(false, "runtime dependencies initialize without script errors")
		_finish()
		return
	_expect(player.is_on_floor() and player.BodyHeightM < 0.11, "the real 10 cm body settles on the physics floor")
	var surface: Dictionary = runtime.surface_at(0.0, 350.0, 5.0)
	_expect(surface.ok and is_zero_approx(surface.height_m) and surface.entity_id == "shell:floor", "surface query finds the floor and its entity id through physics")
	_expect(not runtime.authority.snapshot(PLAYER).consent[PLAYER], "fresh runtime begins without effect consent")
	_expect(runtime.set_local_consent(true).ok, "player explicitly opts into invention effects")
	var source := _wind_source()
	var before_occupied: int = runtime.authority.revision
	var occupied := _commit(source, "", 0.0, 350.0)
	_expect(not occupied.ok and occupied.get("code") == "occupied_placement" and runtime.authority.revision == before_occupied, "live clearance blocks a collider overlapping the player")
	occupied = _commit(source, "", 7.0, 346.0)
	_expect(not occupied.ok and occupied.get("code") == "occupied_placement", "live clearance protects the companion's body")
	var placed := _commit(source, "", 1.0, 350.0)
	_expect(placed.get("ok", false), "manual data design commits through runtime and authority")
	if not placed.get("ok", false):
		print(placed)
		_finish()
		return
	var id: String = placed.instance_id
	_expect(runtime.instances.has(id) and runtime.assemblies.has(id), "host instance rendered from compiled source")
	var on_top: Dictionary = runtime.surface_at(1.0, 350.0, 5.0)
	_expect(on_top.ok and is_zero_approx(on_top.height_m), "surface queries ignore creations, so a revision cannot stand on itself")
	var before_x: float = player.position.x
	_expect(runtime.activate_creation(id).ok, "Use trigger activates capability graph")
	_expect(runtime.fields.size() == 1 and runtime.evaluated_nodes == 2, "reachable graph evaluated once and one field admitted")
	await _frames(35)
	_expect(player.position.x > before_x + 0.08, "compiled wind changes actual CharacterBody movement")
	_expect(player.velocity.length() < player.MaxCreationSpeedMps + player.RunSpeedMps + 0.01, "creation force stays within the small body's cap")
	_expect(runtime.set_local_consent(false).ok, "local player can revoke consent during a field")
	_expect(runtime.fields.is_empty(), "consent change invalidates transient field references")
	_expect(runtime.effect_at(PLAYER, player.position).acceleration.is_zero_approx(), "revoked consent immediately removes sampled force")
	var after_revoke: float = player.position.x
	await _frames(8)
	_expect(abs(player.position.x - after_revoke) < 0.06, "creation-induced velocity does not coast after consent revocation")
	_expect(not runtime.activate_creation(id).ok, "revoked local player cannot reactivate")
	_expect(runtime.set_local_consent(true).ok, "local player can consent again")
	_expect(runtime.activate_creation(id).ok, "explicit activation can start another bounded lifetime")
	await _frames(130)
	_expect(runtime.fields.is_empty() and player.CreationVelocity.is_zero_approx(), "field lifetime expires and induced velocity is cleared")
	await _test_revision_removal_reload(id, source)
	await _test_companion()
	await _test_timer_and_glide()
	await _test_slope_revocation()
	await _test_lock_boundary_motion()
	await _test_stop_effects()
	await _test_corrupt_load_cleanup()
	_finish()


func _test_revision_removal_reload(id: String, source: Dictionary) -> void:
	player.TryTeleportTo(Vector3(0, 0.003, 350))
	var old_assembly = runtime.assemblies[id]
	var weak_old: WeakRef = weakref(old_assembly)
	_expect(runtime.activate_creation(id).ok, "field can be active before source revision")
	var edited := source.duplicate(true)
	edited.name = "Revised wind"
	edited.nodes[1].params.direction = [0, 0, 1]
	var result := _commit(edited, id, 1.0, 350.0)
	_expect(result.get("ok", false), "revision uses same host identity with new source: " + str(result))
	if not result.get("ok", false):
		return
	_expect(runtime.fields.is_empty() and runtime.instances[id].revision == 2, "revision clears old effects and advances instance revision")
	await process_frame
	_expect(weak_old.get_ref() == null, "replaced compiled assembly is freed")
	var load_result: Dictionary = runtime.authority.load_saved()
	_expect(load_result.ok, "runtime authority reload succeeds")
	runtime._refresh()
	_expect(runtime.instances[id].source.name == "Revised wind", "reload retains revised editable source")
	_expect(not runtime.authority.can_activate(PLAYER, id), "reloaded session requires fresh consent")
	runtime.set_local_consent(true)
	var removed: Dictionary = runtime.remove_creation(id, runtime.authority.revision, runtime.authority.permission_revision)
	_expect(removed.ok and not runtime.instances.has(id) and not runtime.assemblies.has(id), "remove unpublishes assembly and effects")
	_expect(not runtime.activate_creation(id).ok, "removed invention cannot activate")
	await _frames(2)


func _test_companion() -> void:
	var source := _wind_source()
	source.nodes[1].params.direction = [0, 1, 0]
	player.TryTeleportTo(Vector3(5.6, 0.003, 346))
	await _frames(3)
	var placed := _commit(source, "", 6.4, 346.0)
	_expect(placed.get("ok", false), "field source can be placed beside the companion with physical clearance: " + str(placed))
	if not placed.get("ok", false):
		return
	var id: String = placed.instance_id
	_expect(runtime.activate_creation(id).ok, "field starts near the companion")
	_expect(runtime.effect_at(COMPANION, companion.position).acceleration.is_zero_approx(), "a companion without consent receives no force")
	_expect(runtime.set_companion_consent(true).ok, "the companion can be given consent")
	_expect(runtime.activate_creation(id).ok, "field can restart after the permission revision")
	var before: float = companion.position.y
	await _frames(25)
	_expect(companion.position.y > before + 0.03, "the consenting companion's real body visibly moves under the field")
	runtime.set_companion_consent(false)
	_expect(companion.CreationVelocity.is_zero_approx(), "companion revocation clears induced momentum")
	_expect(runtime.effect_at(COMPANION, companion.position).acceleration.is_zero_approx(), "companion revocation blocks the next effect sample")
	await _frames(40)
	runtime.set_companion_builder(true)
	runtime.set_companion_consent(true)
	var companion_request := {"op": "place", "action_id": "companion_runtime_creation", "source": source, "x_m": 6.4, "z_m": 345.2, "yaw_deg": 0.0, "expected_revision": runtime.authority.revision, "expected_permission_revision": runtime.authority.permission_revision}
	var companion_placed: Dictionary = runtime.authority.submit(COMPANION, companion_request)
	_expect(companion_placed.ok, "the companion can contribute its own source: " + str(companion_placed))
	if not companion_placed.ok:
		return
	runtime._refresh()
	_expect(runtime.activate_creation(companion_placed.instance_id, COMPANION).ok, "the companion operates its own invention")
	runtime.set_companion_builder(false)
	_expect(runtime.fields.is_empty() and not runtime.instances[companion_placed.instance_id].active, "revoking the companion's build role removes its live effects")
	_expect(not runtime.activate_creation(companion_placed.instance_id, COMPANION).ok, "a revoked creator cannot operate its disabled invention")
	_expect(runtime.effect_at(COMPANION, companion.position).acceleration.is_zero_approx(), "revoked creator effects remain absent")
	runtime.remove_creation(id, runtime.authority.revision, runtime.authority.permission_revision)
	runtime.remove_creation(companion_placed.instance_id, runtime.authority.revision, runtime.authority.permission_revision)
	runtime.set_companion_builder(true)
	player.TryTeleportTo(Vector3(0, 0.003, 350))
	await _frames(2)


func _test_timer_and_glide() -> void:
	var source := _wind_source()
	source.name = "Periodic wind"
	source.nodes[0].op = "timer"
	source.nodes[0].params = {"interval_s": 0.5}
	var placed := _commit(source, "", -10.0, 350.0)
	_expect(placed.get("ok", false), "timer design commits: " + str(placed))
	if not placed.get("ok", false):
		return
	var baseline: int = runtime.activation_count
	await _frames(70)
	var fired: int = runtime.activation_count - baseline
	_expect(fired >= 2 and fired <= 4, "timer fires on bounded cadence without catch-up burst")
	runtime.remove_creation(placed.instance_id, runtime.authority.revision, runtime.authority.permission_revision)
	var glider: Dictionary = JSON.parse_string(FileAccess.get_file_as_string("res://tests/fixtures/creations/fixture_worn_glide.json"))
	for node in glider.nodes:
		if node.op == "wind":
			node.params.direction = [0, -1, 0]
			node.params.acceleration_mps2 = 4.0
			node.params.duration_s = 2.0
	var equipped := _commit(glider, "", 0.0, 350.0)
	_expect(equipped.ok, "composed glider equips through general system")
	if not equipped.ok:
		return
	player.position = Vector3(0, 15, 350)
	player.velocity = Vector3.ZERO
	await _frames(50)
	_expect(player.velocity.y >= -3.1 and player.velocity.y < 0.0, "equipped glide constrains actual descent to source value (vy=%.3f)" % player.velocity.y)
	_expect(runtime.activate_creation(equipped.instance_id).ok, "downwind capability can run alongside passive glide")
	await _frames(30)
	_expect(player.velocity.y >= -3.1 and player.velocity.y < 0.0, "downwind remains constrained by equipped glide")
	var before_revoke: float = player.velocity.y
	runtime.set_local_consent(false)
	_expect(player.velocity.y <= before_revoke + 0.02 and player.velocity.y < 0.0, "revoking clipped downwind cannot add phantom upward velocity")
	await _frames(20)
	_expect(player.velocity.y < -3.1, "revoking consent releases passive glide cap (vy=%.3f)" % player.velocity.y)
	runtime.set_local_consent(true)
	runtime.remove_creation(equipped.instance_id, runtime.authority.revision, runtime.authority.permission_revision)
	await _frames(240)
	_expect(player.TryTeleportTo(Vector3(0, 0.003, 350)), "player returns after the glide fall")
	await _frames(20)


func _test_slope_revocation() -> void:
	var ramp := StaticBody3D.new()
	var collider := CollisionShape3D.new()
	var box := BoxShape3D.new()
	box.size = Vector3(8.0, 0.5, 2.0)
	collider.shape = box
	ramp.add_child(collider)
	ramp.position = Vector3(-20, 1, 350)
	ramp.rotation.z = deg_to_rad(10.0)
	world.add_child(ramp)
	await _frames(2)
	_expect(player.TryTeleportTo(Vector3(-22, 1.2, 350)), "player can stand on a 10 degree ramp")
	await _frames(40)
	_expect(player.is_on_floor() and player.position.y > 0.5, "actual CharacterBody settles on sloped collision surface")
	var placed := _commit(_wind_source(), "", -22.0, 348.6)
	_expect(placed.get("ok", false), "field for sloped contact regression commits: " + str(placed))
	if not placed.get("ok", false):
		ramp.queue_free()
		return
	var start: Vector3 = player.position
	_expect(runtime.activate_creation(placed.instance_id).ok, "field activates beside sloped contact")
	await _frames(30)
	_expect(player.position.x > start.x + 0.05, "wind causes tangible movement along slope")
	runtime.set_local_consent(false)
	_expect(player.CreationVelocity.is_zero_approx() and Vector2(player.velocity.x, player.velocity.z).length() < 0.2, "slope contact does not retain force after revocation")
	var stopped: Vector3 = player.position
	await _frames(8)
	_expect(abs(player.position.x - stopped.x) < 0.06, "revoked slope force does not leave residual drift")
	runtime.set_local_consent(true)
	runtime.remove_creation(placed.instance_id, runtime.authority.revision, runtime.authority.permission_revision)
	ramp.queue_free()
	player.TryTeleportTo(Vector3(0, 0.003, 350))
	await _frames(2)


func _test_lock_boundary_motion() -> void:
	var lock: Dictionary = runtime.authority.submit(PLAYER, {"op": "lock", "action_id": "lock_garden", "targets": ["obj:garden"], "expected_revision": runtime.authority.revision, "expected_permission_revision": runtime.authority.permission_revision})
	_expect(lock.ok, "the garden object is locked")
	var source := _wind_source()
	source.mount = "avatar"
	source.nodes[1].params.direction = [0, 0, 1]
	source.nodes[1].params.radius_m = 1.0
	var placed := _commit(source, "", 0.0, 350.0)
	_expect(placed.ok, "avatar field compiles with authorized initial footprint")
	if not placed.ok:
		return
	_expect(player.TryTeleportTo(Vector3(0, 0.003, 379.3)), "player stands just outside the locked garden")
	await _frames(3)
	_expect(runtime.activate_creation(placed.instance_id).ok, "moving avatar invention can trigger near a locked zone")
	await _frames(65)
	_expect(player.position.z > 379.35 and player.position.z < 380.0, "creation-induced movement approaches but cannot overlap the locked garden (z=%.3f)" % player.position.z)
	_expect(runtime.effect_at(PLAYER, Vector3(0, 0, 385)).acceleration.is_zero_approx(), "a locked zone receives no force")
	_expect(player.HasCreationGuard, "boundary recovery regression retains an active motion guard")
	_expect(player.TryTeleportTo(Vector3(0, 0.003, 385)), "fixture records a checkpoint inside the locked zone")
	player.Recover()
	_expect(player.position.z < 380.0 and player.position.z >= 290.0, "recovery during creation motion cannot return to a locked-zone checkpoint (z=%.3f)" % player.position.z)
	_expect(player.CreationVelocity.is_zero_approx(), "recovery clears invention-induced momentum")
	runtime.remove_creation(placed.instance_id, runtime.authority.revision, runtime.authority.permission_revision)
	runtime.authority.submit(PLAYER, {"op": "unlock", "action_id": "unlock_garden", "targets": ["obj:garden"], "expected_revision": runtime.authority.revision, "expected_permission_revision": runtime.authority.permission_revision})
	player.TryTeleportTo(Vector3(0, 0.003, 350))
	await _frames(2)


func _test_stop_effects() -> void:
	var placed := _commit(_wind_source(), "", 1.0, 350.0)
	_expect(placed.get("ok", false), "a field source for the stop regression commits")
	if not placed.get("ok", false):
		return
	_expect(runtime.activate_creation(placed.instance_id).ok and runtime.fields.size() == 1, "a field runs before stop")
	await _frames(5)
	_expect(runtime.stop_effects() >= 1 and runtime.fields.is_empty() and player.CreationVelocity.is_zero_approx(), "stop removes every running effect and its momentum at once")
	runtime.remove_creation(placed.instance_id, runtime.authority.revision, runtime.authority.permission_revision)


func _commit(source: Dictionary, id: String, x: float, z: float) -> Dictionary:
	return runtime.commit_draft(source, id, {"x_m": x, "z_m": z, "yaw_deg": 0.0, "y_m": 0.0}, runtime.authority.revision, runtime.authority.permission_revision)


func _test_corrupt_load_cleanup() -> void:
	player.TryTeleportTo(Vector3(0, 0.003, 350))
	var placed := _commit(_wind_source(), "", -1.0, 350.0)
	_expect(placed.get("ok", false), "create an assembly before corrupt-load regression")
	if not placed.get("ok", false):
		return
	_expect(runtime.activate_creation(placed.instance_id).ok and runtime.fields.size() == 1, "live field exists before failed load")
	var file := FileAccess.open(SAVE_PATH, FileAccess.WRITE)
	file.store_string("{invalid")
	file.close()
	_expect(not runtime.authority.load_saved().ok, "runtime's authority fails closed on corrupt save")
	runtime._refresh()
	_expect(runtime.assemblies.is_empty() and runtime.instances.is_empty() and runtime.fields.is_empty() and runtime.animations.is_empty(), "failed authority snapshot removes runtime assemblies and transient effects")
	_expect(player.CreationVelocity.is_zero_approx(), "failed load clears actual player external velocity")
	await _frames(2)


func _wind_source() -> Dictionary:
	return {"schema": "garage_game.creation", "version": 1, "name": "Runtime wind", "seed": 1, "mount": "ground", "parts": [{"id": "base", "shape": "box", "position_m": [0, 0.25, 0], "rotation_deg": [0, 0, 0], "size_m": [0.5, 0.5, 0.5], "material": "stone"}], "nodes": [{"id": "use", "op": "interact", "part_id": "base", "params": {}}, {"id": "wind", "op": "wind", "part_id": "base", "params": {"direction": [1, 0, 0], "acceleration_mps2": 4.0, "radius_m": 3.0, "duration_s": 2.0}}], "edges": [{"from": "use", "to": "wind"}]}


func _frames(count: int) -> void:
	for _index in range(count):
		await physics_frame


func _expect(condition: bool, label: String) -> void:
	checks += 1
	if not condition:
		failures += 1
		push_error("Invention runtime check failed: " + label)


func _remove_test_save() -> void:
	DirAccess.remove_absolute(ProjectSettings.globalize_path(SAVE_PATH))
	DirAccess.remove_absolute(ProjectSettings.globalize_path(SAVE_PATH + ".pending"))


func _finish() -> void:
	Input.set_mouse_mode(Input.MOUSE_MODE_VISIBLE)
	world.queue_free()
	# Let the freed bodies go before quitting: drop this suite's references, let the frees happen, then have .NET collect the
	# bodies' managed wrappers. Quitting with them still alive aborted the debug engine at exit after every check had passed
	# (about half the runs on a busy machine; see ManagedShutdown.cs).
	player = null
	companion = null
	runtime = null
	await _frames(2)
	ManagedShutdown.new().Collect()
	await _frames(2)
	_remove_test_save()
	print("Invention runtime: %d checks, %d failures" % [checks, failures])
	quit(guard.exit_code(failures != 0))
