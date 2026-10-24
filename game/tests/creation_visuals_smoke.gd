extends SceneTree
## Checks generated geometry against admitted bounds and exercises real graph/timer code.
const GUARD = preload("res://tests/kernel_test_guard.gd")
## Fails the suite on any script or engine error (kernel_test_guard.gd).
var guard = GUARD.new()
const COMPILER = preload("res://scripts/creation_compiler.gd")
const VISUALS = preload("res://scripts/creation_visuals.gd")
const RUNTIME = preload("res://scripts/invention_runtime.gd")
var failures: Array[String] = []
var assemblies_checked := 0
var corners_checked := 0
var triangles_checked := 0


class BudgetRecorder extends RefCounted:
	var calls: Array = []
	var deny := false
	var revision := 0
	var permission_revision := 0
	func is_ready() -> bool:
		return true
	func consume_runtime_budget(id: String, principal: String, fields: int, evaluations: int, time: float) -> Dictionary:
		calls.append({"id":id,"principal":principal,"fields":fields,"nodes":evaluations,"time":time})
		return {"ok":false,"message":"test denial"} if deny else {"ok":true}
	func snapshot(_principal: String) -> Dictionary:
		return {"ok":true,"revision":0,"permission_revision":0,"consent":{"player:local":false}}
	func can_affect(_owner: String, _target: String, _position: Vector3, _ignore := "") -> bool:
		return false


## Stands in for the C# body's creation-effect API.
class TestActor extends CharacterBody3D:
	var BodyRadiusM := 0.02
	func SetCreationEffects(_acceleration: Vector3, _glide: float, _guard: Callable) -> void:
		pass
	func ClearCreationMotion() -> void:
		pass


func _initialize() -> void:
	OS.add_logger(guard)
	call_deferred("_run")


func _run() -> void:
	for fixture in ["fixture_worn_glide", "fixture_use_rotor_wind", "fixture_proximity_wind", "fixture_trigger_light", "fixture_timer_rotor"]:
		_geometry(JSON.parse_string(FileAccess.get_file_as_string("res://tests/fixtures/creations/%s.json" % fixture)))
	for shape in COMPILER.registry().shapes:
		for dimensions in [[4.0,0.1,0.2],[0.1,0.35,4.0],[3.4,0.25,1.3]]:
			var source := _single(shape, dimensions)
			_geometry(source)
	# This rests safely at Y=.5, but its long blade sweeps below the ground when tilted.
	var dipping := _single("rotor", [4,0.1,0.1])
	dipping.parts[0].position_m = [0,0.5,0]
	dipping.parts[0].rotation_deg = [45,0,0]
	var rejected: Dictionary = COMPILER.compile(dipping)
	_check(not rejected.ok and rejected.get("code") == "below_ground", "tilted rotor below-ground sweep must be rejected before placement")
	_graph_checks()
	if failures.is_empty():
		print("Creation visuals smoke passed: %d assemblies, %d mesh-bound corners, %d outward clockwise triangles; full rotor sweeps, graph deduplication, budget rejection and no timer catch-up." % [assemblies_checked,corners_checked,triangles_checked])
		quit(guard.exit_code(false))
	else:
		for failure in failures:
			push_error(failure)
		quit(guard.exit_code(true))


func _geometry(source: Dictionary) -> void:
	var compiled: Dictionary = COMPILER.compile(source)
	if not compiled.ok:
		_check(false, "geometry fixture cannot compile: " + str(compiled))
		return
	assemblies_checked += 1
	var artifact: Dictionary = compiled.artifact
	var assembly: Node3D = VISUALS.build(artifact, false)
	var parts: Dictionary = assembly.get_meta("parts")
	for definition in artifact.source.parts:
		var part: Node3D = parts[definition.id]
		_check(part.position.is_equal_approx(VISUALS.vector(definition.position_m)), "renderer preserves stable part position " + definition.id)
		var authored := Basis.from_euler(VISUALS.vector(definition.rotation_deg) * PI / 180.0)
		_check(part.basis.is_equal_approx(authored), "renderer preserves authored part basis " + definition.id)
		if definition.shape in ["sphere","cylinder"]:
			var generated_mesh: MeshInstance3D = part.get_child(0)
			var relative_size := generated_mesh.mesh.get_aabb().size / VISUALS.vector(definition.size_m)
			# An eight-ring sphere samples just short of the equator; allow its
			# documented tessellation, while still catching ignored axis scaling.
			for axis in range(3):
				_check(relative_size[axis] >= 0.97 and relative_size[axis] <= 1.00001, "nonuniform " + definition.shape + " must honor all three edited dimensions")
		if definition.shape == "rotor":
			# Sample all quadrants and diagonal poses of the actual generated blades.
			for step in range(48):
				part.basis = authored * Basis(Vector3.UP, TAU * float(step) / 48.0)
				_bounds(part, part.transform, artifact.bounds, definition.id)
			part.basis = authored
		else:
			_bounds(part, part.transform, artifact.bounds, definition.id)
		_winding(part, definition.id)
	assembly.free()


func _bounds(node: Node3D, transform: Transform3D, bounds: Dictionary, label: String) -> void:
	if node is MeshInstance3D:
		var box: AABB = node.mesh.get_aabb()
		for corner_index in range(8):
			var corner: Vector3 = transform * box.get_endpoint(corner_index)
			corners_checked += 1
			for axis in range(3):
				if corner[axis] < float(bounds.min[axis]) - 0.00002 or corner[axis] > float(bounds.max[axis]) + 0.00002:
					_check(false, "%s generated/swept corner %s escapes compiled bounds %s" % [label,corner,bounds])
					return
	for child in node.get_children():
		if child is Node3D:
			_bounds(child, transform * child.transform, bounds, label)


func _winding(node: Node3D, label: String) -> void:
	if node is MeshInstance3D:
		for surface in range(node.mesh.get_surface_count()):
			var arrays: Array = node.mesh.surface_get_arrays(surface)
			var vertices: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]
			var normals: PackedVector3Array = arrays[Mesh.ARRAY_NORMAL]
			var indices: PackedInt32Array = arrays[Mesh.ARRAY_INDEX] if arrays[Mesh.ARRAY_INDEX] != null else PackedInt32Array()
			var count := indices.size() if not indices.is_empty() else vertices.size()
			for offset in range(0,count,3):
				var ia: int = indices[offset] if not indices.is_empty() else offset
				var ib: int = indices[offset+1] if not indices.is_empty() else offset+1
				var ic: int = indices[offset+2] if not indices.is_empty() else offset+2
				var cross := (vertices[ib]-vertices[ia]).cross(vertices[ic]-vertices[ia])
				if cross.length_squared() < 0.0000000001:
					continue
				var normal := normals[ia]+normals[ib]+normals[ic]
				_check(cross.dot(normal) < 0.0, label + " has an inward or counterclockwise visible face")
				for index in [ia,ib,ic]:
					_check(absf(normals[index].length() - 1.0) < 0.0001, label + " has a non-unit lighting normal")
				triangles_checked += 1
	for child in node.get_children():
		if child is Node3D:
			_winding(child,label)


func _graph_checks() -> void:
	var source := _single("box", [1,1,1])
	source.parts[0].position_m = [0,0.5,0]
	source.parts[0].rotation_deg = [0,0,0]
	source.nodes = [_node("use","interact",{}), _node("left","light",{"intensity":1,"duration_s":1}),
		_node("right","light",{"intensity":1,"duration_s":1}), _node("end","light",{"intensity":1,"duration_s":1}),
		_node("unreachable","light",{"intensity":1,"duration_s":1})]
	source.edges = [{"from":"use","to":"left"},{"from":"use","to":"right"},{"from":"left","to":"end"},{"from":"right","to":"end"}]
	var runtime = RUNTIME.new()
	var recorder := BudgetRecorder.new()
	runtime.authority = recorder
	var assembly := _install(runtime, source)
	var fired: Dictionary = runtime._fire("fixture", "use", "player:local")
	_check(fired.ok and runtime.evaluated_nodes == 4 and runtime.animations.size() == 3, "real graph interpreter must deduplicate diamond join and exclude unreachable node")
	_check(recorder.calls.size() == 1 and recorder.calls[0].nodes == 4, "runtime admission must receive exact reachable graph cost")
	_check(runtime._fire("fixture", "use", "player:local").ok and runtime.animations.size() == 3, "repeat activation replaces same-node light jobs")
	var before: int = runtime.evaluated_nodes
	recorder.deny = true
	var denied: Dictionary = runtime._fire("fixture","use","player:local")
	_check(not denied.ok and runtime.evaluated_nodes == before and runtime.animations.size() == 3, "denied runtime quota must prevent all graph effects")
	assembly.free()
	runtime.free()
	# Exercise the live actuator, not just an independently rotated preview mesh.
	source = _single("rotor", [4,0.1,0.2])
	source.nodes = [_node("use","interact",{}),_node("turn","spin",{"speed_rpm":60,"duration_s":2})]
	source.edges = [{"from":"use","to":"turn"}]
	runtime = RUNTIME.new()
	runtime.authority = BudgetRecorder.new()
	assembly = _install(runtime,source)
	var spinning_part: Node3D = assembly.get_meta("parts")["part"]
	var original_basis := spinning_part.basis
	var artifact: Dictionary = runtime.instances.fixture.artifact
	_check(runtime._fire("fixture","use","player:local").ok, "rotor trigger must be admitted")
	for step in range(48):
		runtime.clock_s += 1.0 / 48.0
		runtime._step_animations(1.0 / 48.0)
		var expected := original_basis * Basis(Vector3.UP,TAU * float(step+1)/48.0)
		_check(spinning_part.basis.is_equal_approx(expected), "live actuator must spin about the authored local Y axis")
		_bounds(spinning_part,spinning_part.transform,artifact.bounds,"live tilted rotor")
	assembly.free()
	runtime.free()
	# Advance the real physics adapter by a long frame. A timer may fire once,
	# never execute a backlog proportional to elapsed time.
	source.nodes = [_node("beat","timer",{"interval_s":0.5}),_node("light","light",{"intensity":1,"duration_s":0.1})]
	source.edges = [{"from":"beat","to":"light"}]
	runtime = RUNTIME.new()
	recorder = BudgetRecorder.new()
	runtime.authority = recorder
	var actor := TestActor.new()
	root.add_child(actor)
	runtime.player = actor
	runtime._make_field_display()
	runtime.seen_revision = 0
	runtime.seen_permissions = 0
	assembly = _install(runtime,source)
	runtime._physics_process(30.0)
	_check(runtime.activation_count == 1 and recorder.calls.size() == 1, "long frame causes one timer activation, not catch-up burst")
	runtime._physics_process(0.1)
	_check(runtime.activation_count == 1, "timer cannot fire before its next host deadline")
	runtime._physics_process(0.4)
	_check(runtime.activation_count == 2, "timer fires once when next deadline is reached")
	_check(runtime.animations.size() <= 1, "repeating timer does not grow duplicate effect jobs")
	assembly.free()
	actor.free()
	runtime.free()


func _install(runtime, source: Dictionary) -> Node3D:
	var artifact: Dictionary = COMPILER.compile(source).artifact
	var assembly: Node3D = VISUALS.build(artifact,false)
	root.add_child(assembly)
	runtime.instances = {"fixture":{"id":"fixture","source":artifact.source,"artifact":artifact,"owner_id":"player:local","active":true}}
	runtime.assemblies = {"fixture":assembly}
	return assembly


func _single(shape: String, dimensions: Array) -> Dictionary:
	return {"schema":"garage_game.creation","version":1,"name":"Geometry test","seed":7,"mount":"ground",
		"parts":[{"id":"part","shape":shape,"position_m":[0,3,0],"rotation_deg":[35,25,12],"size_m":dimensions,"material":"stone"}],"nodes":[],"edges":[]}


func _node(id: String, op: String, params: Dictionary) -> Dictionary:
	return {"id":id,"op":op,"part_id":"part","params":params}


func _check(condition: bool, message: String) -> void:
	if not condition:
		failures.append(message)
