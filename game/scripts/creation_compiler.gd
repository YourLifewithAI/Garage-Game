extends RefCounted
## Data-only creation compiler. The artifact is derived; authority must recompile
## editable source before admitting an instance, never trust a supplied artifact.

const JSON_KERNEL = preload("res://scripts/creation_json.gd")
const TEXT = preload("res://scripts/creation_text.gd")
const SCHEMA_IDS = preload("res://scripts/schema_ids.gd")
const COMPILER_VERSION := 1
const STYLE_VERSION := "painterly_v1"
const MAX_BYTES := 32768
const MAX_PARTS := 24
const MAX_NODES := 16
const MAX_EDGES := 32


static func registry() -> Dictionary:
	return {
		"schema": SCHEMA_IDS.id_of("creation"), "version": 1,
		"limits": {"json_bytes": MAX_BYTES, "parts": MAX_PARTS, "nodes": MAX_NODES, "edges": MAX_EDGES,
			"name_bytes": 64, "id_bytes": 32, "position_min": -4.0, "position_max": 4.0,
			"size_min": 0.1, "size_max": 4.0, "rotation_min": -180.0, "rotation_max": 180.0},
		"mounts": ["ground", "avatar"], "shapes": ["box", "sphere", "cylinder", "wing", "rotor"],
		"materials": ["stone", "wood", "copper", "leaf"],
		"operations": {
			"interact": {"label": "Use", "trigger": true, "params": {}},
			"timer": {"label": "Repeat", "trigger": true, "params": {
				"interval_s": _number(0.5, 10.0, 2.0, 0.1, "s")}},
			"proximity": {"label": "Nearby avatar", "trigger": true, "params": {
				"radius_m": _number(0.5, 8.0, 3.0, 0.25, "m"),
				"interval_s": _number(0.25, 5.0, 1.0, 0.05, "s")}},
			"wind": {"label": "Friendly wind", "trigger": false, "params": {
				"direction": {"type": "vector3", "min": -1.0, "max": 1.0, "default": [0.0, 1.0, 0.0], "step": 0.1, "normalized": true},
				"acceleration_mps2": _number(0.1, 4.0, 3.0, 0.1, "m/s²"),
				"radius_m": _number(0.5, 8.0, 3.0, 0.25, "m"),
				"duration_s": _number(0.1, 2.0, 1.0, 0.1, "s")}},
			"spin": {"label": "Turn rotor", "trigger": false, "required_shape": "rotor", "params": {
				"speed_rpm": _number(-120.0, 120.0, 45.0, 1.0, "rpm"),
				"duration_s": _number(0.1, 5.0, 2.0, 0.1, "s")}},
			"light": {"label": "Warm light", "trigger": false, "params": {
				"intensity": _number(0.0, 2.0, 1.0, 0.1, ""),
				"duration_s": _number(0.1, 5.0, 2.0, 0.1, "s")}},
			"glide": {"label": "Controlled descent", "trigger": false, "mount": "avatar", "params": {
				"fall_speed_mps": _number(2.0, 7.0, 3.0, 0.25, "m/s")}}
		}
	}


static func _number(minimum: float, maximum: float, initial: float, step: float, unit: String) -> Dictionary:
	return {"type": "number", "min": minimum, "max": maximum, "default": initial, "step": step, "unit": unit}


static func compile(manifest: Variant) -> Dictionary:
	# Check containers before encoding: no cyclic/deep objects or non-JSON Variants.
	var tree_error := _json_tree_error(manifest, "$", 0, [0, 0])
	if not tree_error.is_empty():
		return tree_error
	if canonical_json(manifest).to_utf8_buffer().size() > MAX_BYTES:
		return _error("json_budget", "$", "Creation source exceeds the 32 KiB limit.")
	var error := _keys(manifest, ["schema", "version", "name", "seed", "mount", "parts", "nodes", "edges"], "$")
	if not error.is_empty():
		return error
	if not SCHEMA_IDS.is_schema(manifest.schema, "creation"):
		return _error("schema", "$.schema", "Expected a garage_game.creation source.")
	if not _integer(manifest.version) or int(manifest.version) != 1:
		return _error("version", "$.version", "Only creation schema version 1 is supported.")
	# A name is untrusted world text: the project's invisible-character rule (creation_text.gd) refuses
	# controls, format characters, blanks, variation selectors and plane 14, except emoji markers in place.
	if not manifest.name is String or manifest.name.strip_edges().is_empty() or manifest.name.to_utf8_buffer().size() > 64 or TEXT.has_hidden(manifest.name):
		return _error("name", "$.name", "Use a nonempty name of at most 64 UTF-8 bytes without control or invisible characters.")
	if not _integer(manifest.seed) or float(manifest.seed) < 0 or float(manifest.seed) > 2147483647:
		return _error("seed", "$.seed", "Seed must be an integer from 0 to 2147483647.")
	var vocabulary := registry()
	if not manifest.mount is String or not manifest.mount in vocabulary.mounts:
		return _error("mount", "$.mount", "Mount must be ground or avatar.")
	for collection in ["parts", "nodes", "edges"]:
		if not manifest[collection] is Array:
			return _error("type", "$." + collection, "Expected an array.")
	if manifest.parts.size() < 1 or manifest.parts.size() > MAX_PARTS:
		return _error("part_budget", "$.parts", "A creation needs 1–24 parts.")
	if manifest.nodes.size() > MAX_NODES:
		return _error("node_budget", "$.nodes", "A creation supports at most 16 behavior nodes.")
	if manifest.edges.size() > MAX_EDGES:
		return _error("edge_budget", "$.edges", "A creation supports at most 32 wires.")
	var parts: Array = []
	var parts_by_id: Dictionary = {}
	var bounds_min := Vector3(INF, INF, INF)
	var bounds_max := Vector3(-INF, -INF, -INF)
	for i in range(manifest.parts.size()):
		var part: Variant = manifest.parts[i]
		var path := "$.parts[%d]" % i
		error = _keys(part, ["id", "shape", "position_m", "rotation_deg", "size_m", "material"], path)
		if not error.is_empty():
			return error
		if not _valid_id(part.id):
			return _error("id", path + ".id", "Part ID must start with a letter and contain at most 32 letters, digits, underscores or hyphens.")
		if parts_by_id.has(part.id):
			return _error("duplicate_part", path + ".id", "Part IDs must be unique.")
		if not part.shape is String or not part.shape in vocabulary.shapes:
			return _error("shape", path + ".shape", "Choose an approved geometric part.")
		if not part.material is String or not part.material in vocabulary.materials:
			return _error("material", path + ".material", "Choose stone, wood, copper or leaf.")
		for field in ["position_m", "rotation_deg", "size_m"]:
			var limits: Array = {"position_m": [-4.0, 4.0], "rotation_deg": [-180.0, 180.0], "size_m": [0.1, 4.0]}[field]
			error = _vector_error(part[field], limits[0], limits[1], path + "." + field)
			if not error.is_empty():
				return error
		var normalized: Dictionary = part.duplicate(true)
		for field in ["position_m", "rotation_deg", "size_m"]:
			normalized[field] = _numbers(part[field])
		parts.append(normalized)
		parts_by_id[part.id] = normalized
		var position := _vec(part.position_m)
		var half_size := _vec(part.size_m) * 0.5
		var rotation := Basis.from_euler(_vec(part.rotation_deg) * (PI / 180.0))
		if part.shape == "rotor":
			# Reserve the complete local-Y sweep, including tilted/nonuniform rotors.
			# Runtime composes authored basis * local spin; rest-pose bounds alone
			# would let a narrow rotor swing across an admitted plot boundary.
			var radius := Vector2(half_size.x, half_size.z).length()
			var extent := Vector3.ZERO
			for axis in range(3):
				extent[axis] = Vector2(rotation.x[axis], rotation.z[axis]).length() * radius + absf(rotation.y[axis]) * half_size.y
			bounds_min = bounds_min.min(position - extent)
			bounds_max = bounds_max.max(position + extent)
		else:
			for x in [-1.0, 1.0]:
				for y in [-1.0, 1.0]:
					for z in [-1.0, 1.0]:
						var corner: Vector3 = position + rotation * (half_size * Vector3(x, y, z))
						bounds_min = bounds_min.min(corner)
						bounds_max = bounds_max.max(corner)
	if manifest.mount == "ground" and bounds_min.y < -0.00001:
		return _error("below_ground", "$.parts", "A ground creation must keep every rotated part above its local origin.")
	var nodes: Array = []
	var nodes_by_id: Dictionary = {}
	var counts := {"interact": 0, "glide": 0, "wind": 0, "light": 0, "spin": 0}
	for i in range(manifest.nodes.size()):
		var node: Variant = manifest.nodes[i]
		var path := "$.nodes[%d]" % i
		error = _keys(node, ["id", "op", "part_id", "params"], path)
		if not error.is_empty():
			return error
		if not _valid_id(node.id):
			return _error("id", path + ".id", "Node ID must be a bounded local name.")
		if nodes_by_id.has(node.id):
			return _error("duplicate_node", path + ".id", "Node IDs must be unique.")
		if not node.op is String or not vocabulary.operations.has(node.op):
			return _error("operation", path + ".op", "This behavior is not in the approved capability registry.")
		if not node.part_id is String or not parts_by_id.has(node.part_id):
			return _error("part_reference", path + ".part_id", "Behavior must refer to a part in this design.")
		var definition: Dictionary = vocabulary.operations[node.op]
		if definition.has("required_shape") and parts_by_id[node.part_id].shape != definition.required_shape:
			return _error("part_kind", path + ".part_id", "Spin requires a rotor part.")
		if definition.has("mount") and manifest.mount != definition.mount:
			return _error("operation_mount", path + ".op", "Glide is available only on equipped avatar designs.")
		error = _keys(node.params, definition.params.keys(), path + ".params")
		if not error.is_empty():
			return error
		var params: Dictionary = {}
		for key in definition.params:
			var rule: Dictionary = definition.params[key]
			var value: Variant = node.params[key]
			var parameter_path: String = path + ".params." + key
			if rule.type == "vector3":
				error = _vector_error(value, rule.min, rule.max, parameter_path)
				if not error.is_empty():
					return error
				if absf(_vec(value).length() - 1.0) > 0.00001:
					return _error("unit_vector", parameter_path, "Wind direction must have unit length; normalize its three components.")
				params[key] = _numbers(value)
			else:
				if not _finite_number(value):
					return _error("number", parameter_path, "Expected a finite number, not text or a boolean.")
				if float(value) < rule.min or float(value) > rule.max:
					return _error("range", parameter_path, "Value must be between %s and %s." % [rule.min, rule.max])
				params[key] = _normalized_number(value)
		if counts.has(node.op):
			counts[node.op] += 1
		if counts.interact > 1 or counts.glide > 1:
			return _error("single_capability", path + ".op", "A design supports at most one interact trigger and one passive glide.")
		var normalized := {"id": node.id, "op": node.op, "part_id": node.part_id, "params": params}
		nodes.append(normalized)
		nodes_by_id[node.id] = normalized
	var edges: Array = []
	var edge_keys: Dictionary = {}
	var outgoing: Dictionary = {}
	var incoming: Dictionary = {}
	for node_id in nodes_by_id:
		outgoing[node_id] = []
		incoming[node_id] = 0
	for i in range(manifest.edges.size()):
		var edge: Variant = manifest.edges[i]
		var path := "$.edges[%d]" % i
		error = _keys(edge, ["from", "to"], path)
		if not error.is_empty():
			return error
		for endpoint in ["from", "to"]:
			if not edge[endpoint] is String or not nodes_by_id.has(edge[endpoint]):
				return _error("node_reference", path + "." + endpoint, "Wire endpoint must name a node in this design.")
		var key: String = edge.from + ":" + edge.to
		if edge_keys.has(key):
			return _error("duplicate_edge", path, "This wire already exists.")
		var destination: Dictionary = nodes_by_id[edge.to]
		if vocabulary.operations[destination.op].trigger or destination.op == "glide":
			return _error("incoming_trigger", path + ".to", "Triggers and passive glide cannot receive wires.")
		edge_keys[key] = true
		edges.append({"from": edge.from, "to": edge.to})
		outgoing[edge.from].append(edge.to)
		incoming[edge.to] += 1
	var ready: Array = []
	for node_id in incoming:
		if incoming[node_id] == 0:
			ready.append(node_id)
	var order: Array = []
	while not ready.is_empty():
		ready.sort()
		var node_id: String = ready.pop_front()
		order.append(node_id)
		for target in outgoing[node_id]:
			incoming[target] -= 1
			if incoming[target] == 0:
				ready.append(target)
	if order.size() != nodes.size():
		return _error("cycle", "$.edges", "Wires must form an acyclic behavior graph.")
	parts.sort_custom(func(a: Dictionary, b: Dictionary) -> bool: return a.id < b.id)
	nodes.sort_custom(func(a: Dictionary, b: Dictionary) -> bool: return a.id < b.id)
	edges.sort_custom(func(a: Dictionary, b: Dictionary) -> bool: return (a.from + ":" + a.to) < (b.from + ":" + b.to))
	# The source keeps the spelling of its id it was written with: its hash pins it (a saved creation's source_sha256, a
	# repair's quote), so a creation saved before the rename stays the same creation (contracts/README.md, "Schema ids").
	var source := {"schema": manifest.schema, "version": 1, "name": manifest.name.strip_edges(),
		"seed": int(manifest.seed), "mount": manifest.mount, "parts": parts, "nodes": nodes, "edges": edges}
	return {"ok": true, "artifact": {"source": source, "hash": canonical_json(source).sha256_text(),
		"compiler_version": COMPILER_VERSION, "style_version": STYLE_VERSION, "order": order,
		"bounds": {"min": _array(bounds_min), "max": _array(bounds_max)},
		"cost": {"parts": parts.size(), "nodes": nodes.size(), "edges": edges.size(),
			"fields": counts.wind, "lights": counts.light, "rotors": counts.spin}}}


## Garage Game canonical JSON v1 (scripts/creation_json.gd): exact numbers, sorted keys, no
## whitespace; byte-identical with the C# command host and tools/kernel/canonical_json.py.
## compile performs the depth/size/type guard first; callers hashing requests should likewise
## bound them before using this general utility. Returns "" for a value JSON cannot hold.
static func canonical_json(value: Variant) -> String:
	return JSON_KERNEL.canonical(value)


static func _keys(value: Variant, allowed: Array, path: String) -> Dictionary:
	if not value is Dictionary:
		return _error("type", path, "Expected an object.")
	for key in value:
		if not key in allowed:
			return _error("unknown_field", path + "." + str(key), "This field is not part of the creation schema.")
	for key in allowed:
		if not value.has(key):
			return _error("missing_field", path + "." + key, "This required field is missing.")
	return {}


static func _json_tree_error(value: Variant, path: String, depth: int, visited: Array) -> Dictionary:
	visited[0] += 1
	if depth > 12 or visited[0] > 8192:
		return _error("json_complexity", path, "JSON exceeds the supported nesting or element budget.")
	if value is Dictionary:
		for key in value:
			if not key is String:
				return _error("json_type", path, "JSON object keys must be strings.")
			if key.length() > MAX_BYTES:
				return _error("json_budget", "$", "Creation source exceeds the 32 KiB limit.")
			visited[1] += key.to_utf8_buffer().size()
			if visited[1] > MAX_BYTES:
				return _error("json_budget", "$", "Creation source exceeds the 32 KiB limit.")
			var error := _json_tree_error(value[key], path + "." + key, depth + 1, visited)
			if not error.is_empty():
				return error
	elif value is Array:
		for i in range(value.size()):
			var error := _json_tree_error(value[i], path + "[%d]" % i, depth + 1, visited)
			if not error.is_empty():
				return error
	elif value is String:
		if value.length() > MAX_BYTES:
			return _error("json_budget", "$", "Creation source exceeds the 32 KiB limit.")
		visited[1] += value.to_utf8_buffer().size()
		if visited[1] > MAX_BYTES:
			return _error("json_budget", "$", "Creation source exceeds the 32 KiB limit.")
	elif typeof(value) in [TYPE_INT, TYPE_FLOAT]:
		if not _finite_number(value):
			return _error("number", path, "Numbers must be finite.")
	elif not typeof(value) in [TYPE_NIL, TYPE_BOOL, TYPE_STRING]:
		return _error("json_type", path, "Only data-only JSON values are allowed.")
	return {}


static func _vector_error(value: Variant, minimum: float, maximum: float, path: String) -> Dictionary:
	if not value is Array or value.size() != 3:
		return _error("vector", path, "Expected exactly three numeric components.")
	for i in range(3):
		if not _finite_number(value[i]):
			return _error("number", path + "[%d]" % i, "Vector components must be finite numbers.")
		if float(value[i]) < minimum or float(value[i]) > maximum:
			return _error("range", path + "[%d]" % i, "Component must be between %s and %s." % [minimum, maximum])
	return {}


static func _valid_id(value: Variant) -> bool:
	if not value is String:
		return false
	var pattern := RegEx.new()
	# \A and \z: PCRE's $ also matches before a final newline.
	pattern.compile("\\A[A-Za-z][A-Za-z0-9_-]{0,31}\\z")
	return pattern.search(value) != null


static func _finite_number(value: Variant) -> bool:
	return typeof(value) in [TYPE_INT, TYPE_FLOAT] and is_finite(float(value))


static func _integer(value: Variant) -> bool:
	return _finite_number(value) and float(value) == floorf(float(value))


static func _normalized_number(value: Variant) -> Variant:
	if float(value) == 0.0:
		return 0
	if float(value) == floorf(float(value)) and absf(float(value)) <= 9007199254740991.0:
		return int(value)
	return float(value)


static func _numbers(values: Array) -> Array:
	var result: Array = []
	for value in values:
		result.append(_normalized_number(value))
	return result


static func _vec(values: Array) -> Vector3:
	return Vector3(float(values[0]), float(values[1]), float(values[2]))


static func _array(value: Vector3) -> Array:
	return [_normalized_number(value.x), _normalized_number(value.y), _normalized_number(value.z)]


static func _error(code: String, path: String, message: String) -> Dictionary:
	return {"ok": false, "code": code, "path": path, "message": message}
