"""The shared character turntable: one GLB, four fixed views, one neutral look.

Every character converter's output is judged through this script, so the A/B compares the characters, not each
contender's lighting. It reads the GLB as the game would (glTF base colours, vertex colours, textures), lights it
the same way every time, and writes four views (front, three-quarter, side, back) and a strip of all four.

    blender -b --factory-startup -P pipeline/characters/turntable.py -- --glb <character.glb> --out <folder> [--size 640] [--samples 48]

Blender 5.2.2, Cycles on the CPU (headless, deterministic for a given machine). Godot axes in the GLB: +Y up, the
character faces -Z, pivot at the bottom centre. After glTF import Blender is Z-up and the character faces +Y.
It prints one line, TURNTABLE {...}, with the character's bounds in metres and where its pivot sits.
"""
import json
import math
import struct
import sys
from pathlib import Path

import bpy
import numpy as np
from mathutils import Vector

VIEWS = [('front', 0), ('three_quarter', 35), ('side', 90), ('back', 180)]
# Mid-tone on purpose: white characters (clouds, ghosts) and dark ones both read against it.
BACKGROUND = (0.46, 0.52, 0.58)
GROUND = (0.36, 0.38, 0.34)


def arguments():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    options = {'--size': '640', '--samples': '48'}
    for key, value in zip(argv[::2], argv[1::2]):
        options[key] = value
    if '--glb' not in options or '--out' not in options:
        raise SystemExit('usage: blender -b --factory-startup -P turntable.py -- --glb <file> --out <folder>')
    return Path(options['--glb']), Path(options['--out']), int(options['--size']), int(options['--samples'])


def bounds(objects):
    points = [o.matrix_world @ Vector(c) for o in objects for c in o.bound_box]
    lo = Vector([min(p[i] for p in points) for i in range(3)])
    hi = Vector([max(p[i] for p in points) for i in range(3)])
    return lo, hi


def apply_material_hints(glb):
    """Opt-in preview only. Unhinted materials and all scene settings stay intact."""
    raw = glb.read_bytes()
    length, kind = struct.unpack_from('<I4s', raw, 12)
    if kind != b'JSON':
        raise ValueError('GLB must start with its JSON chunk')
    document = json.loads(raw[20:20 + length])
    for source in document.get('materials', []):
        # The converter's key (contracts/schema_ids.py GLB_MATERIAL_KEYS), and the one the committed characters keep.
        extras = source.get('extras', {})
        hint = extras.get('garage_game_material', extras.get('enfractal_material', {}))
        if hint.get('kind') != 'bubble' or hint.get('see_through') is not True:
            continue
        material = bpy.data.materials.get(source['name'])
        if material is None:  # An unused palette entry need not be imported.
            continue
        nodes, links = material.node_tree.nodes, material.node_tree.links
        nodes.clear()
        output = nodes.new('ShaderNodeOutputMaterial')
        clear = nodes.new('ShaderNodeBsdfTransparent')
        film = nodes.new('ShaderNodeBsdfPrincipled')
        film.inputs['Base Color'].default_value = (1, 1, 1, 1)
        film.inputs['Roughness'].default_value = 0.12
        film.inputs['IOR'].default_value = 1.33
        film.inputs['Transmission Weight'].default_value = 1.0
        film.inputs['Thin Film Thickness'].default_value = 420
        film.inputs['Thin Film IOR'].default_value = 1.46
        # A view-dependent halo, with a clear centre instead of a glass marble.
        fresnel = nodes.new('ShaderNodeFresnel')
        fresnel.inputs['IOR'].default_value = 1.2
        noise = nodes.new('ShaderNodeTexNoise')
        noise.inputs['Scale'].default_value = 2.8
        ramp = nodes.new('ShaderNodeValToRGB')
        aura = hint.get('aura_colour', '#B4EED8')
        rgb = [int(aura[i:i + 2], 16) / 255 for i in (1, 3, 5)]
        rgb = [v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4 for v in rgb]
        stops = [(0.0, (0.8, 0.3, 0.55, 1)), (0.35, (*rgb, 1)),
                 (0.65, (0.35, 0.55, 1, 1)), (1.0, (1, 0.75, 0.3, 1))]
        for i, (position, colour) in enumerate(stops):
            element = ramp.color_ramp.elements[i] if i < 2 else ramp.color_ramp.elements.new(position)
            element.position, element.color = position, colour
        links.new(noise.outputs['Fac'], ramp.inputs['Fac'])
        halo = nodes.new('ShaderNodeEmission')
        links.new(ramp.outputs['Color'], halo.inputs['Color'])
        halo.inputs['Strength'].default_value = 0.8
        rim = nodes.new('ShaderNodeMixShader')
        rim.inputs[0].default_value = 0.12
        links.new(halo.outputs[0], rim.inputs[1])
        links.new(film.outputs[0], rim.inputs[2])
        edge = nodes.new('ShaderNodeMath')
        edge.operation = 'POWER'
        edge.inputs[1].default_value = 1.5
        links.new(fresnel.outputs['Fac'], edge.inputs[0])
        mix = nodes.new('ShaderNodeMixShader')
        links.new(edge.outputs[0], mix.inputs[0])
        links.new(clear.outputs[0], mix.inputs[1])
        links.new(rim.outputs[0], mix.inputs[2])
        links.new(mix.outputs[0], output.inputs['Surface'])


def main():
    glb, out, size, samples = arguments()
    out.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=str(glb))
    apply_material_hints(glb)
    meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
    if not meshes:
        raise SystemExit(f'{glb}: no meshes')
    lo, hi = bounds(meshes)
    centre = (lo + hi) / 2
    height = hi.z - lo.z
    extent = max(hi.x - lo.x, hi.y - lo.y, height)

    scene = bpy.context.scene
    scene.render.engine = 'CYCLES'
    scene.cycles.device = 'CPU'
    scene.cycles.samples = samples
    scene.cycles.use_denoising = True
    scene.cycles.seed = 0
    scene.render.resolution_x = scene.render.resolution_y = size
    scene.render.film_transparent = False
    scene.view_settings.view_transform = 'Standard'
    world = bpy.data.worlds.new('turntable')
    world.use_nodes = True
    world.node_tree.nodes['Background'].inputs[0].default_value = (*BACKGROUND, 1)
    world.node_tree.nodes['Background'].inputs[1].default_value = 0.9
    scene.world = world

    # A soft ground disc so the feet read as standing, never as floating.
    bpy.ops.mesh.primitive_circle_add(vertices=64, radius=extent * 1.4, fill_type='NGON', location=(centre.x, centre.y, lo.z))
    ground = bpy.context.object
    material = bpy.data.materials.new('ground')
    material.use_nodes = True
    material.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value = (*GROUND, 1)
    material.node_tree.nodes['Principled BSDF'].inputs['Roughness'].default_value = 1.0
    ground.data.materials.append(material)

    key = bpy.data.objects.new('key', bpy.data.lights.new('key', 'SUN'))
    key.data.energy = 2.4
    key.data.angle = math.radians(12)
    key.rotation_euler = (math.radians(-50), 0, math.radians(-35))  # from the front, up and to one side
    scene.collection.objects.link(key)

    camera = bpy.data.objects.new('camera', bpy.data.cameras.new('camera'))
    camera.data.lens = 70
    scene.collection.objects.link(camera)
    scene.camera = camera
    distance = extent * 3.4
    elevation = math.radians(12)
    paths = []
    for name, azimuth_deg in VIEWS:
        azimuth = math.radians(azimuth_deg)
        offset = Vector((math.sin(azimuth) * math.cos(elevation), math.cos(azimuth) * math.cos(elevation), math.sin(elevation))) * distance
        camera.location = centre + offset
        camera.rotation_euler = (centre - camera.location).to_track_quat('-Z', 'Y').to_euler()
        path = out / f'{name}.png'
        scene.render.filepath = str(path)
        bpy.ops.render.render(write_still=True)
        paths.append(path)

    # The strip: four views side by side, through Blender's own image API (no extra packages).
    images = [bpy.data.images.load(str(p)) for p in paths]
    pixels = []
    for image in images:
        array = np.empty(size * size * 4, dtype=np.float32)
        image.pixels.foreach_get(array)
        pixels.append(array.reshape(size, size, 4))
    strip = np.concatenate(pixels, axis=1)
    sheet = bpy.data.images.new('strip', width=size * len(images), height=size, alpha=True)
    sheet.pixels.foreach_set(strip.ravel())
    sheet.filepath_raw = str(out / 'turntable.png')
    sheet.file_format = 'PNG'
    sheet.save()

    for o in meshes:
        o.data.calc_loop_triangles()
    # Blender Z-up back to the game's axes: x stays, game y is Blender z, game z is minus Blender y.
    report = {
        'glb': glb.name,
        'height_m': round(height, 4),
        'width_m': round(hi.x - lo.x, 4),
        'depth_m': round(hi.y - lo.y, 4),
        'pivot_offset_m': [round(centre.x, 4), round(lo.z, 4), round(-centre.y, 4)],
        'meshes': len(meshes),
        'triangles': sum(len(o.data.loop_triangles) for o in meshes),
        'views': [p.name for p in paths] + ['turntable.png'],
    }
    print('TURNTABLE ' + json.dumps(report))


main()
