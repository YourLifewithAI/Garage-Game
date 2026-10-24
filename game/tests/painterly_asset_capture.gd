extends SceneTree
## Reproducible turntable in the destination renderer, never a Blender beauty render.

const ASSETS := preload("res://scripts/painterly_assets.gd")

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	var output := OS.get_environment("GARAGE_GAME_ART_OUTPUT")
	if output.is_empty():
		output = ProjectSettings.globalize_path("res://../.cache/asset-proof")
	DirAccess.make_dir_recursive_absolute(output)
	root.msaa_3d = Viewport.MSAA_4X
	var stage := Node3D.new()
	root.add_child(stage)
	var environment_node := WorldEnvironment.new()
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color(0.58, 0.70, 0.73)
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color(0.66, 0.74, 0.84)
	environment.ambient_light_energy = 0.48
	environment.tonemap_mode = Environment.TONE_MAPPER_FILMIC
	environment_node.environment = environment
	stage.add_child(environment_node)
	var light := DirectionalLight3D.new()
	light.rotation_degrees = Vector3(-43, -35, 0)
	light.light_energy = 0.72
	light.shadow_enabled = true
	light.directional_shadow_max_distance = 45.0
	stage.add_child(light)
	var floor_mesh := MeshInstance3D.new()
	var plane := PlaneMesh.new()
	plane.size = Vector2(80, 80)
	floor_mesh.mesh = plane
	var floor_material := StandardMaterial3D.new()
	floor_material.albedo_color = Color(0.47, 0.52, 0.37)
	floor_material.roughness = 1.0
	floor_mesh.material_override = floor_material
	stage.add_child(floor_mesh)
	var camera := Camera3D.new()
	camera.fov = 47
	camera.current = true
	stage.add_child(camera)
	for species in ["oak", "juniper"]:
		var tree := ASSETS.make_tree(species, false)
		stage.add_child(tree)
		for view in range(8):
			var angle := TAU * float(view) / 8.0
			camera.position = Vector3(sin(angle) * 14.0, 4.8, cos(angle) * 14.0)
			camera.look_at(Vector3(0, 3.6, 0))
			await _capture(output.path_join("%s-%02d.png" % [species, view]))
		camera.position = Vector3(2.8, 1.67, 6.5)
		camera.look_at(Vector3(0, 3.7, 0))
		await _capture(output.path_join("%s-walking.png" % species))
		light.light_color = Color(1.0, 0.87, 0.70)
		await _capture(output.path_join("%s-warm.png" % species))
		light.light_color = Color.WHITE
		camera.position = Vector3(8.0, 1.67, 42.0)
		camera.look_at(Vector3(0, 3.7, 0))
		await _capture(output.path_join("%s-far.png" % species))
		print("Asset proof ", species, " triangles=", ASSETS.triangle_count(tree))
		tree.free()
	quit(0)

func _capture(path: String) -> void:
	for frame in range(3):
		await process_frame
	await RenderingServer.frame_post_draw
	var result := root.get_texture().get_image().save_png(path)
	if result != OK:
		push_error("Capture failed " + path)
		quit(1)
	print(path)
