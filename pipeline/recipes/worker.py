"""Blender-only worker. Paths and launch flags come from the trusted runner."""
import argparse
import hashlib
import importlib
import json
import math
from pathlib import Path
import struct
import sys
import time
import zlib

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
import bmesh
import bpy
from mathutils import Matrix, Vector
from geometry import bounds, inspect_glb, material, render_preview, reset
from specs import RECIPES, material_entries, resolve, strict_loads, values
from contract_check import check_glb
import style


def srgb(value):
    return value / 12.92 if value <= 0.04045 else ((value + 0.055) / 1.055) ** 2.4


def rgb(hex_colour):
    return tuple(int(hex_colour[i:i + 2], 16) / 255 for i in (1, 3, 5))


def pattern_image(path, colour, pattern):
    """A tiny deterministic sRGB PNG; packed before export. No external art."""
    width = 64
    rows = []
    for y in range(width):
        row = bytearray([0])
        for x in range(width):
            if pattern == 'gingham':
                tint = ((x // 8) % 2 + (y // 8) % 2) / 2
            else:
                tint = (x // 8) % 2
            row.extend(round(255 * (1 - tint + channel * tint)) for channel in colour)
            row.append(255)
        rows.append(bytes(row))

    def chunk(tag, data):
        return struct.pack('>I', len(data)) + tag + data + struct.pack('>I', zlib.crc32(tag + data))

    path.write_bytes(b'\x89PNG\r\n\x1a\n'
                     + chunk(b'IHDR', struct.pack('>IIBBBBB', width, width, 8, 6, 0, 0, 0))
                     + chunk(b'IDAT', zlib.compress(b''.join(rows), 9)) + chunk(b'IEND', b''))
    image = bpy.data.images.load(str(path), check_existing=False)
    image.name = 'lid_pattern_64px'
    image.pack()
    return image


def materials(used, out):
    data = values(used)
    result = {}
    for entry in material_entries(used):
        slot, role = entry['slot'], entry['role']
        colour = rgb(entry['base_color'])
        mat = material(slot, tuple(srgb(c) for c in colour),
                       roughness=0.08 if role == 'glass' else 0.42 if role == 'metal' else 0.75,
                       metallic=1 if role in ('metal', 'metal_painted') else 0,
                       transmission=1 if role == 'glass' else 0)
        mat['material_role'] = role
        if style.enabled():
            bsdf = mat.node_tree.nodes.get('Principled BSDF')
            bsdf.inputs['Roughness'].default_value = 0.32 if role == 'glass' else 0.58
            bsdf.inputs['Metallic'].default_value = 0.25 if role in ('metal', 'metal_painted') else 0
            if role == 'glass':
                bsdf.inputs['Transmission Weight'].default_value = 0
                bsdf.inputs['Alpha'].default_value = 0.22
                mat.surface_render_method = 'BLENDED'
                mat['glass_treatment'] = 'tinted_alpha_thick_shell'
        result[slot] = mat
        if slot == 'screen':
            bsdf = mat.node_tree.nodes.get('Principled BSDF')
            bsdf.inputs['Emission Color'].default_value = (*[srgb(c) for c in colour], 1)
            bsdf.inputs['Emission Strength'].default_value = 0.25
        if data['recipe'] == 'jam_jar' and slot == 'lid' and data['params']['lid_pattern'] != 'solid':
            image = pattern_image(out / 'lid_pattern.png', colour, data['params']['lid_pattern'])
            texture = mat.node_tree.nodes.new('ShaderNodeTexImage')
            texture.image = image
            texture.interpolation = 'Closest'
            mat.node_tree.links.new(texture.outputs['Color'],
                                    mat.node_tree.nodes.get('Principled BSDF').inputs['Base Color'])
    return result


def y_up_box(lo, hi):
    return {'min_m': [lo[0], lo[2], -hi[1]], 'max_m': [hi[0], hi[2], -lo[1]]}


def fit_and_check(parts, name, size):
    bpy.context.view_layer.update()
    lo, hi = bounds(parts)
    assert all(math.isfinite(v) for v in lo + hi)
    shift = Vector(((lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, lo[2]))
    target = [size[0], size[2], size[1]]
    scale = [target[i] / (hi[i] - lo[i]) for i in range(3)]
    root = bpy.data.objects.new(name, None)
    bpy.context.collection.objects.link(root)
    topology = {}
    for obj in parts:
        world = obj.matrix_world.copy()
        for vertex in obj.data.vertices:
            point = world @ vertex.co - shift
            vertex.co = Vector([point[i] * scale[i] for i in range(3)])
        obj.matrix_world = Matrix.Identity(4)
        obj.parent = root
        obj.data.update()
        bm = bmesh.new()
        bm.from_mesh(obj.data)
        nonmanifold = sum(not edge.is_manifold or not edge.is_contiguous for edge in bm.edges)
        bad_vertices = sum(not vertex.is_manifold for vertex in bm.verts)
        volume = bm.calc_volume(signed=True)
        bm.free()
        obj.data.calc_loop_triangles()
        degenerate = sum(tri.area <= 0 or not math.isfinite(tri.area) for tri in obj.data.loop_triangles)
        if nonmanifold or bad_vertices or degenerate or not math.isfinite(volume) or volume <= 0:
            raise ValueError(f'{obj.name}: invalid closed part: nonmanifold={nonmanifold}, '
                             f'bad_vertices={bad_vertices}, degenerate={degenerate}, volume={volume}')
        a, b = bounds([obj])
        topology[obj.name] = {'triangles': len(obj.data.loop_triangles),
                              'nonmanifold_edges': nonmanifold, 'nonmanifold_vertices': bad_vertices,
                              'degenerate_triangles': degenerate, 'signed_volume_m3': volume,
                              **y_up_box(a, b)}
    bpy.context.view_layer.update()
    lo, hi = bounds(parts)
    box = y_up_box(lo, hi)
    actual = [box['max_m'][i] - box['min_m'][i] for i in range(3)]
    assert all(abs(a - b) <= 0.001 for a, b in zip(actual, size)), 'Bounds mismatch'
    assert abs(box['min_m'][1]) < 1e-6
    assert abs(box['min_m'][0] + box['max_m'][0]) < 1e-6
    assert abs(box['min_m'][2] + box['max_m'][2]) < 1e-6
    assert root.matrix_world == Matrix.Identity(4)
    return root, topology, box, scale


def collision_shapes(topology):
    # Coarse per-part solids; omit tiny visual details. Capture reviews these hints.
    skip = ('key_', 'neck_thread', 'screen', 'touchpad', 'filter_plate', 'plunger_rod')
    result = []
    for name, info in topology.items():
        if name.startswith(skip):
            continue
        lo, hi = info['min_m'], info['max_m']
        result.append({'part': name, 'shape': 'box',
                       'centre_m': [(a + b) / 2 for a, b in zip(lo, hi)],
                       'size_m': [b - a for a, b in zip(lo, hi)],
                       'rotation_xyzw': [0, 0, 0, 1]})
    assert len(result) <= 64
    return result


def main():
    started = time.perf_counter()
    parser = argparse.ArgumentParser()
    parser.add_argument('--input', required=True, type=Path)
    parser.add_argument('--out', required=True, type=Path)
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    used = strict_loads(args.input.read_text(encoding='utf-8'))
    data = values(used)
    resolve(data)  # Validate even when somebody invokes this internal worker directly.
    name = data['recipe']
    reset()
    style.configure(data)
    mats = materials(used, args.out)
    parts = importlib.import_module(name).build(data, mats)
    style.finish(parts)
    root, topology, box, scale = fit_and_check(parts, name, data['size_m'])
    triangles = sum(part['triangles'] for part in topology.values())
    limit = 20000 if name == 'couch' else 10000
    assert triangles <= limit, f'Triangle budget {triangles} > {limit}'
    entries = material_entries(used)
    roles = json.loads((Path(__file__).resolve().parents[2] / 'contracts' / 'common.schema.json')
                       .read_text(encoding='utf-8'))['$defs']['material_role']['enum']
    assert all(entry['role'] in roles for entry in entries)
    assert {obj.data.materials[0].name for obj in parts} == set(mats)
    bpy.ops.object.select_all(action='DESELECT')
    root.select_set(True)
    for obj in parts:
        obj.select_set(True)
    glb = args.out / (name + '.glb')
    export_started = time.perf_counter()
    bpy.ops.export_scene.gltf(filepath=str(glb), export_format='GLB', use_selection=True,
                              export_yup=True, export_apply=True, export_materials='EXPORT',
                              export_image_format='AUTO', export_animations=False,
                              export_cameras=False, export_lights=False, export_extras=True)
    decoded = inspect_glb(glb)
    assert decoded['triangle_count'] == triangles
    assert set(decoded['parts']) == set(topology)
    for part_name, part in topology.items():
        exported = decoded['parts'][part_name]
        assert exported['triangles'] == part['triangles']
        for key in ('min_m', 'max_m'):
            assert all(abs(a - b) < 1e-5 for a, b in zip(part[key], exported[key]))
    exported_mats = {m['name']: m for m in decoded['materials']}
    assert set(exported_mats) == set(mats)
    for entry in entries:
        assert exported_mats[entry['slot']]['extras']['material_role'] == entry['role']
    problems = check_glb(glb.read_bytes(), name)
    assert not problems, '\n'.join(problems)
    # Actual Blender re-import, in addition to decoding the embedded position/index bytes.
    previous = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=str(glb))
    imported = set(bpy.data.objects) - previous
    meshes = [obj for obj in imported if obj.type == 'MESH']
    bpy.context.view_layer.update()
    imported_box = y_up_box(*bounds(meshes))
    assert len(meshes) == len(parts)
    imported_triangles = 0
    for obj in meshes:
        obj.data.calc_loop_triangles()
        imported_triangles += len(obj.data.loop_triangles)
    assert imported_triangles == triangles
    for key in ('min_m', 'max_m'):
        assert all(abs(a - b) < 1e-5 for a, b in zip(box[key], imported_box[key]))
    for obj in imported:
        bpy.data.objects.remove(obj, do_unlink=True)
    export_seconds = time.perf_counter() - export_started
    render_started = time.perf_counter()
    render_preview(args.out / (name + '.png'), parts, samples=64)
    checks = {key: {'passed': True} for key in (
        'finite_matching_bounds', 'bottom_centre_identity_root', 'no_degenerate_triangles',
        'closed_parts_manifold_positive_volume', 'named_slots_and_roles', 'triangle_budget',
        'decoded_glb_comparison', 'blender_reimport_comparison', 'embedded_resources', 'cpu_preview')}
    checks['contract_check_glb'] = {'passed': True, 'problems': problems}
    raw = glb.read_bytes()
    result = {'schema': 'garage_game.recipe_receipt', 'version': 1, 'inputs_used': used,
              'blender': {'version': bpy.app.version_string,
                          'build_hash': bpy.app.build_hash.decode('ascii')},
              'triangle_count': triangles, 'bounds': box,
              'dimensions_m': decoded['dimensions_m_y_up'],
              'pivot': 'bottom_centre', 'root_transform': [1, 0, 0, 0, 0, 1, 0, 0,
                                                          0, 0, 1, 0, 0, 0, 0, 1],
              'template_fit_scale_xyz_blender': scale, 'materials': entries,
              'glb': {'path': glb.name, 'bytes': len(raw), 'sha256': hashlib.sha256(raw).hexdigest()},
              'preview': {'path': name + '.png', 'pixels': [512, 384], 'engine': 'CYCLES',
                          'device': 'CPU', 'samples': 64, 'denoising': 'OPENIMAGEDENOISE_CPU',
                          'camera_angle_deg': 30},
              'runtime_seconds': {'worker': time.perf_counter() - started,
                                  'export_and_checks': export_seconds,
                                  'render': time.perf_counter() - render_started},
              'checks': checks, 'parts': topology, 'collision_shapes': collision_shapes(topology),
              'collision_note': 'Conservative solid boxes, including hollow vessels. '
                                'Capture must review gaps, rotated lids and container access.'}
    (args.out / (name + '.receipt.json')).write_text(json.dumps(result, indent=2, allow_nan=False) + '\n',
                                                   encoding='utf-8', newline='\n')
    print('RECIPE_RESULT ' + json.dumps({'recipe': name, 'triangles': triangles, 'checks': checks}), flush=True)


if __name__ == '__main__':
    main()
