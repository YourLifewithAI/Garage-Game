import collections
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import unittest
import uuid

from pipeline.landscape.harness.common import PALETTE, SETUP, canonical
from pipeline.landscape.harness.kit import SIZES, prototype
from pipeline.landscape.harness.package import read_package, write_package
from pipeline.landscape.export import export_room, landwork
from pipeline.landscape.export.mesh import encode, ground, transform
from pipeline.landscape.export.mesh import cross
from pipeline.landscape.export.terrain import build_levels, tile_level0
from pipeline.landscape.export.trees import (POLE_OVERLAP_M, TerrainHeights, cap_mesh, climbing_parts, is_tree,
                                             WALK_CLEARANCE_M)

ROOT = Path(__file__).resolve().parents[4]
ROOM = ROOT/'pipeline/landscape/corpus/rooms/garage_nominal'
REFERENCE = ROOT/'pipeline/landscape/harness/reference/package'


def read_glb(path=None, data=None):
    data = data if data is not None else Path(path).read_bytes()
    magic, version, length = struct.unpack_from('<4sII', data)
    assert (magic, version, length) == (b'glTF', 2, len(data))
    size = struct.unpack_from('<I', data, 12)[0]
    doc = json.loads(data[20:20+size])
    binary = data[28+size:]

    def get(index):
        a = doc['accessors'][index]
        view = doc['bufferViews'][a['bufferView']]
        n = {'VEC3': 3, 'VEC4': 4, 'SCALAR': 1}[a['type']]
        code = 'f' if a['componentType'] == 5126 else 'I'
        offset = view.get('byteOffset', 0)+a.get('byteOffset', 0)
        return list(struct.iter_unpack('<'+code*n, binary[offset:offset+a['count']*n*4]))

    parts = []
    for p in doc['meshes'][0]['primitives']:
        positions = get(p['attributes']['POSITION'])
        ids = [i[0] for i in get(p['indices'])]
        mat = doc['materials'][p['material']]
        parts.append({'role': mat['extras']['role'], 'positions': positions,
                      'triangles': [ids[i:i+3] for i in range(0, len(ids), 3)],
                      'normals': get(p['attributes']['NORMAL']),
                      'colors': get(p['attributes']['COLOR_0']), 'material': mat})
    return doc, parts


def triangles(parts):
    return collections.Counter(tuple(tuple(p['positions'][i]) for i in t)
                               for p in parts for t in p['triangles'])


def read_tiles(path):
    """A tiled terrain GLB's nodes by name: role, positions, triangles, normals and colours (bytes to [0, 1])."""
    data = Path(path).read_bytes()
    size = struct.unpack_from('<I', data, 12)[0]
    doc = json.loads(data[20:20+size])
    binary = data[28+size:]
    codes = {5126: 'f', 5125: 'I', 5123: 'H', 5121: 'B'}

    def get(index):
        a = doc['accessors'][index]
        n = {'VEC3': 3, 'VEC4': 4, 'SCALAR': 1}[a['type']]
        values = struct.unpack_from('<'+codes[a['componentType']]*(a['count']*n), binary,
                                    doc['bufferViews'][a['bufferView']]['byteOffset'])
        if a.get('normalized'):
            values = [v/255 for v in values]
        return [tuple(values[i:i+n]) for i in range(0, len(values), n)] if n > 1 else list(values)

    nodes = {}
    for node in doc['nodes']:
        p = doc['meshes'][node['mesh']]['primitives'][0]
        ids = get(p['indices'])
        nodes[node['name']] = dict(role=doc['materials'][p['material']]['extras']['role'],
                                   positions=get(p['attributes']['POSITION']),
                                   triangles=[ids[i:i+3] for i in range(0, len(ids), 3)],
                                   normals=get(p['attributes']['NORMAL']), colors=get(p['attributes']['COLOR_0']))
    return doc, nodes


def read_table(path, kind, slots=0):
    """A plant table, parsed here independently of contracts/validate.py: (header, tile index, rows)."""
    data = Path(path).read_bytes()
    header = struct.unpack_from('<8s8I', data, 0)
    tiles = [struct.unpack_from('<iiII', data, 40+16*i) for i in range(header[5])]
    width = 56+(28+32*slots if kind == 'tree' else 0)
    start = 40+16*len(tiles)
    assert len(data) == start+width*header[4]
    form = '<11f3I'+('6fI'+'8f'*slots if kind == 'tree' else '')
    rows = [struct.unpack_from(form, data, start+width*i) for i in range(header[4])]
    return header, tiles, rows


def area(parts):
    total = 0.
    for p in parts:
        for face in p['triangles']:
            a, b, c = [p['positions'][i] for i in face]
            n = cross([b[i]-a[i] for i in range(3)], [c[i]-a[i] for i in range(3)])
            total += math.sqrt(sum(v*v for v in n))/2
    return total


def components(parts):
    """Independent connected components of the exported indexed geometry."""
    result = []
    for p in parts:
        groups = []
        for face in p['triangles']:
            touching = [g for g in groups if set(face) & g]
            merged = set(face).union(*touching)
            groups = [g for g in groups if g not in touching]+[merged]
        for group in groups:
            indices = sorted(group)
            remap = {old: new for new, old in enumerate(indices)}
            result.append(dict(role=p['role'], positions=[p['positions'][i] for i in indices],
                               triangles=[[remap[i] for i in t] for t in p['triangles'] if t[0] in group]))
    return result


class ExportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.scratch = Path(os.environ.get('TEMP', '/tmp'))/('garage-game-export-tests-'+uuid.uuid4().hex)
        os.makedirs(cls.scratch)
        cls.out = cls.scratch/'a'/'landscape_reference'
        cls.stats = export_room(REFERENCE, ROOM, 'landscape_reference', cls.out)
        cls.manifest = json.loads((cls.out/'room.json').read_bytes())
        cls.package, cls.meshes, _, _ = read_package(REFERENCE, ROOM)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.scratch)

    def test_reference_validates_and_all_files_are_pinned(self):
        proc = subprocess.run([sys.executable, '-B', str(ROOT/'contracts/validate.py'), '--room', str(self.out)],
                              capture_output=True, text=True, cwd=ROOT)
        print('VALIDATOR exit='+str(proc.returncode)+' '+proc.stdout.strip())
        self.assertEqual(proc.returncode, 0, proc.stdout+proc.stderr)
        self.assertEqual({p.relative_to(self.out).as_posix() for p in self.out.rglob('*') if p.is_file()}-{'room.json'},
                         {e['path'] for e in self.manifest['files']})
        for p in self.out.rglob('*.json'):
            data = p.read_bytes()
            self.assertNotIn(b'\r', data)
            self.assertTrue(data.endswith(b'\n'))

    def test_writes_the_new_ids_and_the_garage_games_name(self):
        """The rename (decision C, contracts/README.md "Schema ids"): the room and its assets are garage_game.<kind>, and no
        GLB names EnFractal as its generator (nor the Garage Game, whose first word labels every garage)."""
        self.assertEqual(self.manifest['schema'], 'garage_game.room')
        assets = [json.loads(p.read_bytes()) for p in self.out.rglob('asset.json')]
        self.assertTrue(assets and all(a['schema'] == 'garage_game.asset' for a in assets))
        glbs = list(self.out.rglob('*.glb'))
        self.assertTrue(glbs)
        for path in glbs:
            data = path.read_bytes()
            length = int.from_bytes(data[12:16], 'little')
            gltf = json.loads(data[20:20 + length])
            self.assertEqual(gltf['asset']['generator'], 'landscape room exporter v1', path.name)
        self.assertNotIn(b'enfractal', b''.join(p.read_bytes() for p in self.out.rglob('*') if p.is_file()).lower())

    def test_byte_deterministic(self):
        other = self.scratch/'b'/'landscape_reference'
        self.assertEqual(self.stats, export_room(REFERENCE, ROOM, 'landscape_reference', other))
        a = {p.relative_to(self.out).as_posix(): p.read_bytes() for p in self.out.rglob('*') if p.is_file()}
        b = {p.relative_to(other).as_posix(): p.read_bytes() for p in other.rglob('*') if p.is_file()}
        self.assertEqual(a, b)

    def test_reference_source_room_preserves_every_box_wall_and_opening(self):
        room = json.loads((ROOM/'room.json').read_bytes())
        inventory = json.loads((ROOM/'inventory.json').read_bytes())
        source = self.manifest['extensions']['x_landscape_source']
        self.assertEqual(source['room_id'], room['room_id'])
        for name in ('room', 'inventory'):
            self.assertEqual(source[name+'_sha256'], hashlib.sha256((ROOM/(name+'.json')).read_bytes()).hexdigest())
        self.assertEqual(source['bounds'], room['bounds'])
        self.assertEqual(len(source['objects']), len(inventory['objects']))
        self.assertEqual({o['id'] for o in source['objects']}, {o['id'] for o in inventory['objects']})
        parts = {p['id']: p for p in source['shell']['parts']}
        walls = [p for p in room['shell']['parts'] if p['role'] == 'wall']
        self.assertEqual(sum(p['role'] == 'wall' for p in parts.values()), len(walls))
        for part in room['shell']['parts']:
            self.assertEqual(parts[part['id']]['geometry'], part['geometry'])
            if part['role'] == 'wall':
                ys = [p[1] for p in part['geometry']['points_m']]
                self.assertEqual(parts[part['id']]['height_m'], max(ys)-min(ys))
        self.assertEqual(source['shell']['openings'],
                         sorted([{k: o[k] for k in ('id', 'kind', 'host_part_id', 'center_m', 'size_m')}
                                 for o in room['shell']['openings']], key=lambda o: o['id']))
        boxes = {o['id']: o for o in source['objects']}
        for obj in inventory['objects']:
            actual = boxes[obj['id']]
            self.assertEqual(actual['kind'], obj['kind'])
            self.assertEqual(actual['kind_confidence'], obj['confidence'])
            self.assertEqual(actual['size_m'], obj['box']['size_m'])
            for key in ('position_m', 'rotation', 'yaw_deg', 'support'):
                self.assertEqual(actual[key], obj['placement'][key])
            self.assertEqual(actual['colours'], sorted(obj['colours'], key=lambda c: (-c['share'], c['hex'])))

    def source_fixture(self, name, room, inventory):
        source = self.scratch/(name+'-source')
        os.makedirs(source)
        (source/'room.json').write_bytes(canonical(room))
        (source/'inventory.json').write_bytes(canonical(inventory))
        package = self.scratch/(name+'-package')
        write_package(package, source, meshes={'land': self.meshes['land']}, setup=dict(SETUP),
                      generator={'name':'test','version':'1'}, terrain=[{'mesh':'land'}])
        return source, package, self.scratch/('source_'+name)

    def test_source_labels_and_display_names_never_appear_in_any_output_file(self):
        room = json.loads((ROOM/'room.json').read_bytes())
        inventory = json.loads((ROOM/'inventory.json').read_bytes())
        forbidden = []

        def collect(value):
            if isinstance(value, dict):
                for key, child in value.items():
                    if key in {'label', 'labels', 'display_name', 'display-name'}:
                        if isinstance(child, str):
                            forbidden.append(child)
                        elif isinstance(child, list):
                            forbidden.extend(v for v in child if isinstance(v, str))
                    collect(child)
            elif isinstance(value, list):
                for child in value:
                    collect(child)

        collect(room)
        collect(inventory)
        for path in self.out.rglob('*'):
            if path.is_file():
                for label in forbidden:
                    self.assertNotIn(label.encode('utf-8'), path.read_bytes(), str(path))
        # Put unique free text at every dictionary depth, including geometry,
        # colour and support records. Re-pin the package through its real writer.
        def inject(value):
            if isinstance(value, dict):
                for child in list(value.values()):
                    inject(child)
                marker = 'UNTRUSTED_SOURCE_TEXT_'+str(len(forbidden))
                value['label'] = marker
                value['display_name'] = marker+' DISPLAY'
                value['notes'] = [marker+' NOTES']
                forbidden.extend([marker, marker+' DISPLAY', marker+' NOTES'])
            elif isinstance(value, list):
                for child in value:
                    inject(child)

        inject(room)
        inject(inventory)
        source, package, out = self.source_fixture('labels', room, inventory)
        export_room(package, source, out.name, out)
        for path in out.rglob('*'):
            if path.is_file():
                data = path.read_bytes()
                for label in forbidden:
                    self.assertNotIn(label.encode('utf-8'), data, str(path))

    def test_source_records_are_sorted_without_reordering_polygon_points(self):
        room = json.loads((ROOM/'room.json').read_bytes())
        inventory = json.loads((ROOM/'inventory.json').read_bytes())
        room['shell']['parts'].reverse()
        room['shell']['openings'].reverse()
        inventory['objects'].reverse()
        inventory['objects'][0]['colours'] = [{'hex':'#eeeeee', 'share':.2},
                                            {'hex':'#bbbbbb', 'share':.4}, {'hex':'#aaaaaa', 'share':.4}]
        source, package, out = self.source_fixture('sorting', room, inventory)
        export_room(package, source, out.name, out)
        intro = json.loads((out/'room.json').read_bytes())['extensions']['x_landscape_source']
        for values in (intro['objects'], intro['shell']['parts'], intro['shell']['openings']):
            self.assertEqual([v['id'] for v in values], sorted(v['id'] for v in values))
        box = next(o for o in intro['objects'] if o['id'] == inventory['objects'][0]['id'])
        self.assertEqual([c['hex'] for c in box['colours']], ['#aaaaaa', '#bbbbbb', '#eeeeee'])
        floor = next(p for p in intro['shell']['parts'] if p['role'] == 'floor')
        original = next(p for p in room['shell']['parts'] if p['role'] == 'floor')
        self.assertEqual(floor['geometry']['points_m'], original['geometry']['points_m'])

    def test_source_scan_pose_alternatives_and_unknown_surface_support(self):
        room = json.loads((ROOM/'room.json').read_bytes())
        inventory = json.loads((ROOM/'inventory.json').read_bytes())
        a, b = inventory['objects'][:2]
        del a['placement']['rotation']
        a['placement']['yaw_deg'] = 37
        a['placement']['support'] = {'kind':'surface', 'height_m':.4, 'target_id':None}
        del b['placement']['yaw_deg']
        del b['box']['yaw_deg']
        b['placement']['rotation'] = [0,0,1,0]  # preserve supplied full rotation
        source, package, out = self.source_fixture('poses', room, inventory)
        export_room(package, source, out.name, out)
        boxes = {o['id']: o for o in json.loads((out/'room.json').read_bytes())['extensions']['x_landscape_source']['objects']}
        self.assertNotIn('rotation', boxes[a['id']])
        self.assertEqual(boxes[a['id']]['yaw_deg'], 37)
        self.assertEqual(boxes[a['id']]['support'], a['placement']['support'])
        self.assertNotIn('yaw_deg', boxes[b['id']])
        self.assertEqual(boxes[b['id']]['rotation'], [0,0,1,0])

    def test_source_invalid_numbers_fail_before_output_creation(self):
        for field, value in (('confidence', 1.1), ('position', 1001), ('size', 0),
                             ('yaw', 361), ('rotation', 2), ('share', -.1), ('height', 1001),
                             ('confidence', float('nan')), ('position', float('inf'))):
            with self.subTest(field=field, value=value):
                room = json.loads((ROOM/'room.json').read_bytes())
                inventory = json.loads((ROOM/'inventory.json').read_bytes())
                obj = inventory['objects'][0]
                if field == 'confidence': obj['confidence'] = value
                elif field == 'position': obj['placement']['position_m'][0] = value
                elif field == 'size': obj['box']['size_m'][0] = value
                elif field == 'yaw': obj['placement']['yaw_deg'] = value
                elif field == 'rotation': obj['placement']['rotation'][3] = value
                elif field == 'share': obj['colours'][0]['share'] = value
                elif field == 'height': obj['placement']['support']['height_m'] = value
                # NaN/Infinity are deliberately malformed input, not pinned output.
                source = self.scratch/('invalid-source-'+uuid.uuid4().hex)
                os.makedirs(source)
                (source/'room.json').write_bytes(canonical(room))
                (source/'inventory.json').write_text(json.dumps(inventory), encoding='utf-8', newline='\n')
                package = self.scratch/('invalid-source-package-'+uuid.uuid4().hex)
                write_package(package, source, meshes={'land': self.meshes['land']}, setup=dict(SETUP),
                              generator={'name':'test','version':'1'}, terrain=[{'mesh':'land'}])
                out = self.scratch/('source_invalid_'+uuid.uuid4().hex)
                with self.assertRaises(ValueError):
                    export_room(package, source, out.name, out)
                self.assertFalse(out.exists())

    def test_a_magnified_package_exports_its_room_magnified(self):
        # The 10x world (S4): the package's source is the true-size room; x_generator.world_scale magnifies it, and the
        # export is of that magnified room: bounds, spawns, the intro's walls, openings and boxes. The pins stay.
        w = 10.
        room = json.loads((ROOM/'room.json').read_bytes())
        inventory = json.loads((ROOM/'inventory.json').read_bytes())
        land = [dict(p, positions=[[v[0]*w, v[1], v[2]*w] for v in p['positions']]) for p in self.meshes['land']]
        package = self.scratch/'magnified-package'
        write_package(package, ROOM, meshes={'land': land}, setup=dict(SETUP), generator={'name': 'test', 'version': '1'},
                      terrain=[{'mesh': 'land'}], extensions={'x_generator': {'world_scale': w}})
        out = self.scratch/'magnified'/'landscape_magnified'
        export_room(package, ROOM, out.name, out)
        manifest = json.loads((out/'room.json').read_bytes())
        source = manifest['extensions']['x_landscape_source']
        for name in ('room', 'inventory'):
            self.assertEqual(source[name+'_sha256'], hashlib.sha256((ROOM/(name+'.json')).read_bytes()).hexdigest())
        self.assertEqual(source['room_id'], room['room_id'])
        bounds = {k: [v*w for v in room['bounds'][k]] for k in ('min_m', 'max_m')}
        self.assertEqual((source['bounds'], manifest['bounds']), (bounds, bounds))
        parts = {p['id']: p for p in source['shell']['parts']}
        for part in room['shell']['parts']:
            self.assertEqual(parts[part['id']]['geometry']['points_m'],
                             [[v*w for v in p] for p in part['geometry']['points_m']])
        openings = {o['id']: o for o in source['shell']['openings']}
        for o in room['shell']['openings']:
            self.assertEqual((openings[o['id']]['center_m'], openings[o['id']]['size_m']),
                             ([v*w for v in o['center_m']], [v*w for v in o['size_m']]))
        boxes = {o['id']: o for o in source['objects']}
        for obj in inventory['objects']:
            self.assertEqual(boxes[obj['id']]['position_m'], [v*w for v in obj['placement']['position_m']])
            self.assertEqual(boxes[obj['id']]['size_m'], [v*w for v in obj['box']['size_m']])
            if 'height_m' in obj['placement']['support']:
                self.assertEqual(boxes[obj['id']]['support']['height_m'], obj['placement']['support']['height_m']*w)
        # The player arrives where the room puts it, magnified; the companion keeps its offset beside it.
        player = next(s for s in room['spawns'] if s['role'] == 'player')['position_m']
        for a, b in zip(room['spawns'], manifest['spawns']):
            self.assertEqual([b['position_m'][0], b['position_m'][2]],
                             [player[i]*w+a['position_m'][i]-player[i] for i in (0, 2)])

    def test_terrain_level0_is_the_package_land_cut_into_tiles(self):
        original = [p for r in self.package['terrain'] for p in self.meshes[r['mesh']]]
        block = self.manifest['terrain_tiles']
        self.assertEqual((block['tile_m'], [level['grid_m'] for level in block['levels']]), (16, [.1, .4, 1.6]))
        parts = {p['id']: p for p in self.manifest['shell']['parts']}
        self.assertEqual(sorted(e['part'] for e in block['parts']),
                         sorted('shell:terrain_'+role for role in {p['role'] for p in original}))
        exported = []
        for entry in block['parts']:
            part = parts[entry['part']]
            self.assertEqual((part['role'], part['collides']), ('ground', True))
            _, nodes = read_tiles(self.out/part['geometry']['mesh'])
            level0 = [n for name, n in sorted(nodes.items()) if name.endswith('_lod0')]
            self.assertEqual(len(level0), entry['tiles'])
            source = [p for p in original if 'shell:terrain_'+p['role'] == entry['part']]
            # Cut along the tile lines, level 0 covers exactly the package's land of that role ...
            self.assertAlmostEqual(area(level0), area(source), places=5)
            for node in level0:
                for x, y, z in node['positions']:
                    self.assertAlmostEqual(ground(source, x, z), y, places=5)
            # ... and a triangle inside one tile is kept as it was.
            kept = triangles(level0)
            for p in source:
                for face in p['triangles']:
                    vs = [p['positions'][i] for i in face]
                    if len({(math.floor(v[0]/16), math.floor(v[2]/16)) for v in vs}) == 1:
                        self.assertIn(tuple(tuple(v) for v in vs), kept)
            exported.extend(level0)
        for i in range(3):
            for f in (min, max):
                self.assertEqual(f(v[i] for p in original for v in p['positions']),
                                 f(v[i] for p in exported for v in p['positions']))

    def test_spawns_sample_terrain_and_keep_source_xz(self):
        terrain = [p for r in self.package['terrain'] for p in self.meshes[r['mesh']]]
        source = json.loads((ROOM/'room.json').read_bytes())
        for a, b in zip(source['spawns'], self.manifest['spawns']):
            self.assertEqual([a['position_m'][0], a['position_m'][2]],
                             [b['position_m'][0], b['position_m'][2]])
            self.assertAlmostEqual(b['position_m'][1], ground(terrain, b['position_m'][0], b['position_m'][2]))

    def assert_faces(self, spawn, target):
        dx, dz = target[0]-spawn['position_m'][0], target[2]-spawn['position_m'][2]
        yaw = math.radians(spawn['yaw_deg'])
        forward = (-math.sin(yaw), -math.cos(yaw))
        error = math.degrees(math.atan2(abs(forward[0]*dz-forward[1]*dx),
                                       forward[0]*dx+forward[1]*dz))
        self.assertLessEqual(error, .5)

    def test_reference_spawns_face_nearest_promised_destination(self):
        # The reference's movable crate is closer than its cottage and tower.
        target = self.package['objects'][0]['position_m']
        for spawn in self.manifest['spawns']:
            self.assert_faces(spawn, target)

    def test_spawns_choose_by_type_and_horizontal_distance_individually(self):
        for mode in ('movable', 'populated_cottage', 'scatter_cottage', 'scatter_tower'):
            with self.subTest(mode=mode):
                # Fixed crates/boulders are closer, but promise neither carrying nor doors.
                fixed = dict(id='nearby_decoration', kind='container', prototype='crate',
                             position_m=[0,0,-3], yaw_deg=0, size_m=[.1,.1,.1],
                             mass_kg=1, carriable=False)
                objects = [fixed]
                scatter = [dict(prototype='boulder', position_m=[1,0,-3],
                                yaw_deg=0, scale=[1,1,1])]
                targets = [[-1,1.85,-2.5], [2,0,-2.5]]
                name = 'tower' if mode == 'scatter_tower' else 'cottage'
                for i, target in enumerate(targets):
                    if mode.startswith('scatter_'):
                        scatter.append(dict(prototype=name, position_m=target,
                                            yaw_deg=0, scale=[1,1,1]))
                    else:
                        objects.append(dict(id='destination_'+str(i), kind='decor',
                                            prototype='crate' if mode == 'movable' else name,
                                            position_m=target, yaw_deg=0, size_m=[.1,.1,.1],
                                            mass_kg=.1, carriable=mode == 'movable'))
                package = self.scratch/('heading-package-'+mode)
                write_package(package, ROOM, meshes={'land': self.meshes['land']}, setup=dict(SETUP),
                              generator={'name':'test','version':'1'}, terrain=[{'mesh':'land'}],
                              objects=objects, scatter=scatter)
                room_id = 'heading_'+mode
                out = self.scratch/room_id
                export_room(package, ROOM, room_id, out)
                spawns = json.loads((out/'room.json').read_bytes())['spawns']
                for spawn, target in zip(spawns, targets):
                    self.assert_faces(spawn, target)

    def test_no_promised_destination_keeps_source_yaw(self):
        source = self.scratch/'yaw-source'
        os.makedirs(source)
        room = json.loads((ROOM/'room.json').read_bytes())
        for spawn, yaw in zip(room['spawns'], [-37,123]):
            spawn['yaw_deg'] = yaw
        (source/'room.json').write_text(json.dumps(room)+'\n', encoding='utf-8', newline='\n')
        (source/'inventory.json').write_bytes((ROOM/'inventory.json').read_bytes())
        package = self.scratch/'no-destination-package'
        # Both populated and scatter records exist; neither is a promised destination.
        write_package(package, source, meshes={'land': self.meshes['land']}, setup=dict(SETUP),
                      generator={'name':'test','version':'1'}, terrain=[{'mesh':'land'}],
                      objects=[dict(id='fixed_crate', kind='container', prototype='crate',
                                    position_m=[0,0,0], yaw_deg=0, size_m=[.1,.1,.1],
                                    mass_kg=1, carriable=False)],
                      scatter=[dict(prototype='boulder', position_m=[1,0,0], yaw_deg=0, scale=[1,1,1])])
        out = self.scratch/'heading_no_destination'
        export_room(package, source, out.name, out)
        spawns = json.loads((out/'room.json').read_bytes())['spawns']
        self.assertEqual([s['yaw_deg'] for s in spawns], [-37,123])

    def test_carryable_object_matches_host_mass_movable_rules(self):
        rules = (ROOT/'game/scripts/native/Sandbox/SandboxRules.cs').read_text()
        import re
        player = float(re.search(r'PlayerCarryLimitKg = ([\d.]+)f', rules)[1])
        companion = float(re.search(r'CompanionCarryLimitKg = ([\d.]+)f', rules)[1])
        obj = next(o for o in self.manifest['objects'] if o['id'] == 'obj:findable_crate')
        asset = json.loads((self.out/obj['asset']).read_bytes())
        self.assertTrue(asset['physics']['movable'])
        self.assertLessEqual(asset['physics']['mass_kg'], min(player, companion))
        self.assertEqual(asset['physics']['mass_kg'], .05)
        self.assertEqual(asset['dimensions_m'], [.12, .105, .105])
        self.assertEqual(asset['collision']['kind'], 'box')
        self.assertEqual(obj['support']['kind'], 'shell')
        self.assertNotIn('protected', obj)

    def test_materials_bake_tints_blends_without_double_multiplication(self):
        p = self.meshes['land'][0]
        doc, parts = read_glb(data=encode([p]))
        actual = parts[0]
        self.assertNotIn('_ROLE_BLEND', doc['meshes'][0]['primitives'][0]['attributes'])
        for i, color in enumerate(actual['colors']):
            w = p['blend_weights'][i]
            factor = actual['material']['pbrMetallicRoughness']['baseColorFactor']
            for channel in range(3):
                expected = ((1-w)*PALETTE[p['role']][channel]+w*PALETTE[p['blend_role']][channel])*p['tints'][i][channel]
                self.assertAlmostEqual(color[channel]*factor[channel], expected, places=6)
                self.assertTrue(0 <= color[channel] <= 1)
        _, solid = read_glb(data=encode([dict(p, blend_weights=[0]*4, blend_role='meadow')]))
        self.assertEqual(solid[0]['material']['pbrMetallicRoughness']['baseColorFactor'][:3], list(PALETTE['meadow']))

    def test_normals_share_indices_and_preserve_split_creases(self):
        p = dict(role='stone', positions=[[0,0,0],[1,0,0],[0,0,1],[0,1,0]], triangles=[[0,2,1],[0,1,3]])
        _, parts = read_glb(data=encode([p]))
        self.assertAlmostEqual(parts[0]['normals'][0][1], 1/math.sqrt(2), places=6)
        self.assertAlmostEqual(parts[0]['normals'][0][2], 1/math.sqrt(2), places=6)
        q = dict(role='stone', positions=[[0,0,0],[1,0,0],[0,1,0]], triangles=[[0,1,2]])
        _, parts = read_glb(data=encode([dict(p, triangles=[[0,2,1]]), q]))
        self.assertEqual(parts[0]['normals'][0], (0.,1.,0.))
        self.assertEqual(parts[0]['normals'][4], (0.,0.,1.))

    def test_nonuniform_scale_and_yaw_are_baked_in_order(self):
        p = dict(role='stone', positions=[[1,0,0],[0,1,0],[0,0,1]], triangles=[[0,1,2]])
        actual = transform([p], [2,3,4], [10,20,30], 90)[0]['positions']
        for a, b in zip(actual, [[10,20,28],[10,23,30],[14,20,30]]):
            for x, y in zip(a, b):
                self.assertAlmostEqual(x, y)
        _, parts = read_glb(data=encode(transform([p], [2,3,4])))
        n = parts[0]['normals'][0]
        # inverse-transpose of the original (1,1,1) face normal
        length = math.sqrt(.5**2+(1/3)**2+.25**2)
        for a, b in zip(n, [.5/length,(1/3)/length,.25/length]):
            self.assertAlmostEqual(a,b,places=6)

    def test_nonuniform_scatter_exports_copy_rows_and_entity_geometry(self):
        package = self.scratch/'scaled-package'
        records = [dict(prototype='broadleaf', position_m=[1,0,1], yaw_deg=90, scale=[.7,1.1,1.3]),
                   dict(prototype='cottage', position_m=[0,0,0], yaw_deg=90, scale=[1.2,.9,.8])]
        write_package(package, ROOM, meshes={'land': self.meshes['land']}, setup=dict(SETUP),
                      generator={'name':'test','version':'1'}, terrain=[{'mesh':'land'}], scatter=records)
        out = self.scratch/'scaled'/'landscape_scaled'
        export_room(package, ROOM, 'landscape_scaled', out)
        r = json.loads((out/'room.json').read_bytes())
        self.assertFalse(any(p['id'].startswith('shell:scatter_') for p in r['shell']['parts']))
        tree = next(s for s in r['plant_copies']['sets'] if s['set'] == 'broadleaf')
        _, _, rows = read_table(out/tree['table'], 'tree', tree['cap_slots'])
        x, y, z, yaw, sx, sy, sz = rows[0][:7]
        self.assertEqual((x, y, z, yaw), (1, 0, 1, 90))
        # The copy (position + yaw x (scale x mesh), Godot's rotation.y) is the merged tree it replaces.
        _, mesh = read_glb(out/tree['mesh'])
        c, s = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
        placed = [dict(p, positions=[[x+c*sx*v[0]+s*sz*v[2], y+sy*v[1], z-s*sx*v[0]+c*sz*v[2]]
                                     for v in p['positions']]) for p in mesh]
        expected = transform(prototype('broadleaf'), [.7,1.1,1.3], [1,0,1], 90)

        def rounded(parts):
            return triangles([dict(p, positions=[[round(v, 4)+0. for v in point] for point in p['positions']])
                              for p in parts])
        self.assertEqual(rounded(placed), rounded(expected))
        obj = r['objects'][0]
        asset = json.loads((out/obj['asset']).read_bytes())
        self.assertEqual(asset['dimensions_m'], [SIZES['cottage'][i]*records[1]['scale'][i] for i in range(3)])
        _, local = read_glb(out/obj['asset'].replace('asset.json','mesh.glb'))
        for i in range(3):
            span = max(v[i] for p in local for v in p['positions'])-min(v[i] for p in local for v in p['positions'])
            self.assertAlmostEqual(span, asset['dimensions_m'][i], places=6)
        self.assertAlmostEqual(obj['transform']['rotation'][1], math.sin(math.pi/4))

    def test_scatter_scenery_water_and_setup_policy(self):
        for p in self.manifest['shell']['parts']:
            if p['id'].startswith(('shell:water_', 'shell:scenery_')):
                self.assertFalse(p['collides'])
                self.assertEqual(p['role'], 'backdrop')
        # Plants, trees and stones are copies now: no merged scatter, no tree climbing part, no experiment extension.
        self.assertFalse(any(p['id'].startswith(('shell:scatter_', 'shell:tree_climb_'))
                             for p in self.manifest['shell']['parts']))
        self.assertNotIn('x_landscape_instances', self.manifest['extensions'])
        sets = {s['set']: s for s in self.manifest['plant_copies']['sets']}
        expected = {'broadleaf': ('tree', 'tree_climb', 'broadleaf', True), 'conifer': ('tree', 'tree_climb', 'conifer', True),
                    'shrub': ('plant', 'none', 'shrub', True), 'grass_tuft': ('plant', 'none', 'grass', True),
                    'flower_clump': ('plant', 'none', 'herb', True), 'fern': ('plant', 'none', 'herb', True),
                    'rock': ('stone', 'solid', 'none', False), 'marker': ('stone', 'solid', 'none', False)}
        self.assertEqual({name: (s['kind'], s['collides'], s['fuel_class'], s['seeds']) for name, s in sets.items()},
                         expected)
        self.assertEqual(sets['grass_tuft']['species'], 'grass')
        self.assertEqual(self.manifest['plant_copies']['tile_m'], 8)
        for s in sets.values():
            counts = [s['triangle_count']]+[lod['triangle_count'] for lod in s.get('lods', [])]
            self.assertEqual(counts, sorted(set(counts), reverse=True))  # each lighter than the one before
        names = [o['display_name'].lower() for o in self.manifest['objects']]
        self.assertTrue(any(n.startswith('cottage') for n in names))
        self.assertTrue(any(n.startswith('boulder') for n in names))
        self.assertFalse(any(n.startswith(('grass', 'fern', 'broadleaf')) for n in names))
        self.assertEqual(self.manifest['site'], {'latitude_deg':30, 'neg_z_bearing_deg':0, 'solar_noon_h':12})
        self.assertEqual(self.manifest['extensions']['x_landscape_setup'], SETUP)
        self.assertNotIn('ceiling_lamp', [h['kind'] for h in self.manifest['light_hints']])

    def test_broken_input_and_nonempty_output_fail_cleanly(self):
        bad = self.scratch/'bad-package'
        shutil.copytree(REFERENCE, bad)
        mesh = next(bad.glob('*.glb'))
        mesh.write_bytes(mesh.read_bytes()+b'broken')
        out = self.scratch/'bad'/'landscape_bad'
        proc = subprocess.run([sys.executable, '-B', '-S', '-m', 'pipeline.landscape.export',
                               '--package', str(bad), '--room', str(ROOM), '--room-id', 'landscape_bad', '--out', str(out)],
                              capture_output=True, text=True, cwd=ROOT)
        self.assertEqual(proc.returncode, 1)
        self.assertIn('EXPORT_INVALID file hash mismatch:', proc.stderr)
        self.assertNotIn('Traceback', proc.stderr)
        self.assertFalse(out.exists())
        with self.assertRaisesRegex(ValueError, 'empty'):
            export_room(REFERENCE, ROOM, 'landscape_reference', self.out)

    def test_room_budget_caps_scatter_entities_without_losing_geometry(self):
        package = self.scratch/'many-package'
        # More than the contract's 512 entities, but within the package budget.
        records = [dict(prototype='boulder', position_m=[0,0,0], yaw_deg=0, scale=[1,1,1]) for _ in range(514)]
        write_package(package, ROOM, meshes={'land': self.meshes['land']}, setup=dict(SETUP),
                      generator={'name':'test','version':'1'}, terrain=[{'mesh':'land'}], scatter=records)
        out = self.scratch/'many'/'landscape_many'
        stats = export_room(package, ROOM, 'landscape_many', out)
        self.assertEqual(stats['objects'], 512)
        self.assertEqual(stats['merged_scatter'], 2)
        # The two left over are stone copies, colliding as their hull.
        self.assertEqual(stats['copies'], {'boulder': 2})
        self.assertEqual(stats['scatter_solid_triangles'], 0)

    def tree_rows(self):
        """(prototype, row) of every tree copy in the reference."""
        result = []
        for s in self.manifest['plant_copies']['sets']:
            if s['kind'] == 'tree':
                result += [(s['prototype'], row) for row in read_table(self.out/s['table'], 'tree', s['cap_slots'])[2]]
        return result

    def test_tree_rows_carry_todays_fitted_climbing_shapes(self):
        terrain = [p for r in self.package['terrain'] for p in self.meshes[r['mesh']]]
        heights = TerrainHeights(terrain)
        rows = self.tree_rows()
        records = [r for r in self.package['scatter'] if r['prototype'] in ('broadleaf', 'conifer')]
        self.assertEqual(len(rows), len(records))
        self.assertEqual(self.stats['tree_count'], len(records))

        def close(a, b):
            self.assertAlmostEqual(a, b, delta=2e-6*max(1, abs(b)))
        for record in records:
            with self.subTest(tree=record['prototype']):
                name, row = next((n, r) for n, r in rows if n == record['prototype']
                                 and abs(r[0]-record['position_m'][0]) < 1e-6 and abs(r[2]-record['position_m'][2]) < 1e-6)
                original = transform(prototype(name), record['scale'], record['position_m'], record['yaw_deg'])
                pole, *caps = climbing_parts(name, original, heights)  # the meshes the tree_climb part held
                tx, tz, trunk_radius, pole_radius, trunk_top, pole_top, cap_count = row[14:21]
                bark = next(p for p in original if p['role'] == 'bark')
                top = max(v[1] for v in bark['positions'])
                bottom = min(v[1] for v in bark['positions'])
                close(trunk_top, top)
                close(min(v[1] for v in pole['positions']), trunk_top-POLE_OVERLAP_M)
                close(pole_top, max(v[1] for v in pole['positions']))
                crown_top = max(v[1] for p in original if p['role'] == 'foliage' for v in p['positions'])
                close(pole_top, crown_top-.015)
                close(pole_radius, max(math.hypot(v[0]-tx, v[2]-tz) for v in pole['positions']))
                close(trunk_radius, max(math.hypot(v[0]-tx, v[2]-tz) for v in bark['positions']
                                        if abs(v[1]-bottom) < 1e-8))
                self.assertEqual(cap_count, len(caps))
                self.assertEqual(len(caps), 5 if name == 'broadleaf' else 1)
                for k, cap in enumerate(caps):
                    cx, cz, rim_x, rim_z, rim, cap_top, top_x, top_z = row[21+8*k:29+8*k]
                    rim_ring, top_ring = cap['positions'][:12], cap['positions'][12:24]
                    for ring_points, (x, z, hx, hz, y) in ((rim_ring, (cx, cz, rim_x, rim_z, rim)),
                                                           (top_ring, (cx, cz, top_x, top_z, cap_top))):
                        xs, zs = [v[0] for v in ring_points], [v[2] for v in ring_points]
                        close(x, (min(xs)+max(xs))/2)
                        close(z, (min(zs)+max(zs))/2)
                        close(hx, (max(xs)-min(xs))/2)
                        close(hz, (max(zs)-min(zs))/2)
                        close(y, ring_points[0][1])
                # The highest perch is just under the drawn crown's top.
                close(max(row[26+8*k] for k in range(cap_count)), crown_top-.01)
                self.assertTrue(all(v == 0 for v in row[21+8*cap_count:]))  # unused cap slots are zero
        self.assertLessEqual(self.stats['shell_parts'], 128)
        self.assertLessEqual(self.stats['files']-1, 2048)

    def test_climbing_caps_have_only_upward_outward_front_faces(self):
        # The meshes a fixed populated tree still gets (shell:tree_climb), built from the same fit as a copy's row.
        heights = TerrainHeights([p for r in self.package['terrain'] for p in self.meshes[r['mesh']]])
        caps = [part for r in self.package['scatter'] if r['prototype'] in ('broadleaf', 'conifer')
                for part in climbing_parts(r['prototype'], transform(prototype(r['prototype']), r['scale'],
                                                                     r['position_m'], r['yaw_deg']), heights)[1:]]
        self.assertEqual(len(caps), 6)
        for cap_part in caps:
            vs = cap_part['positions']
            cx = (min(v[0] for v in vs)+max(v[0] for v in vs))/2
            cz = (min(v[2] for v in vs)+max(v[2] for v in vs))/2
            flat_faces = 0
            for face in cap_part['triangles']:
                a, b, c = [vs[i] for i in face]
                n = cross([b[i]-a[i] for i in range(3)], [c[i]-a[i] for i in range(3)])
                self.assertGreater(n[1], 0)  # glTF CCW front; no underside/bottom.
                radial = [(a[0]+b[0]+c[0])/3-cx, (a[2]+b[2]+c[2])/3-cz]
                self.assertGreaterEqual(n[0]*radial[0]+n[2]*radial[1], -1e-9)
                if abs(n[0])+abs(n[2]) < 1e-8:
                    flat_faces += 1
            self.assertGreater(flat_faces, 0)

    def test_tree_row_caps_clear_walkers_over_reference_terrain(self):
        terrain = [p for r in self.package['terrain'] for p in self.meshes[r['mesh']]]
        for _, row in self.tree_rows():
            for k in range(row[20]):
                part = cap_mesh(row[21+8*k:29+8*k])
                for face in part['triangles']:
                    points = [part['positions'][i] for i in face]
                    points += [[sum(v[i] for v in points)/3 for i in range(3)]]
                    for x, y, z in points:
                        floor = ground(terrain, x, z)
                        if floor is not None:
                            self.assertGreaterEqual(y-floor, WALK_CLEARANCE_M-1e-5)

    def test_sloped_terrain_clearance_uses_the_whole_crown_not_trunk_sample(self):
        terrain = [dict(role='stone', positions=[[-1,0,-1],[1,.55,-1],[1,.55,1],[-1,0,1]],
                        triangles=[[0,2,1],[0,3,2]])]
        # Also test rotation and nonuniform scale with a tree rooted above ground.
        tree = transform(prototype('broadleaf'), [.8,1.1,1.3], [0,.28,0], 37)
        parts = climbing_parts('broadleaf', tree, TerrainHeights(terrain))
        for cap_part in parts[1:]:
            x0 = min(v[0] for v in cap_part['positions'])
            x1 = max(v[0] for v in cap_part['positions'])
            z0 = min(v[2] for v in cap_part['positions'])
            z1 = max(v[2] for v in cap_part['positions'])
            # Independent analytic maximum of this planar slope's footprint.
            floor_max = max(ground(terrain, x, z) for x in (x0,x1) for z in (z0,z1))
            self.assertGreaterEqual(min(v[1] for v in cap_part['positions'])-floor_max,
                                    WALK_CLEARANCE_M-1e-8)

    def test_terrain_rectangle_query_includes_an_interior_peak(self):
        terrain = [dict(role='stone', positions=[[-1,0,-1],[1,0,-1],[1,0,1],[-1,0,1],[.1,.64,0]],
                        triangles=[[0,1,4],[1,2,4],[2,3,4],[3,0,4]])]
        heights = TerrainHeights(terrain)
        self.assertAlmostEqual(heights.maximum((-.2,.2,-.2,.2)), .64)
        parts = climbing_parts('broadleaf', prototype('broadleaf'), heights)
        for p in parts[1:]:
            # Each cap stays above the terrain at all vertices and centroids.
            for face in p['triangles']:
                vs = [p['positions'][i] for i in face]
                for v in vs+[[sum(v[i] for v in vs)/3 for i in range(3)]]:
                    self.assertGreaterEqual(v[1]-ground(terrain,v[0],v[2]), .12-1e-8)

    def test_shrubs_grass_ferns_and_flowers_get_no_climbing_geometry(self):
        for name in ('shrub','grass_tuft','fern','flower_clump','rock','boulder'):
            self.assertFalse(is_tree(name, prototype(name)))
            self.assertEqual(climbing_parts(name, prototype(name), None), [])

    def test_only_decorative_plants_export_no_tree_shell(self):
        package = self.scratch/'plants-package'
        records = [dict(prototype=name, position_m=[0,0,0], yaw_deg=0, scale=[1,1,1])
                   for name in ('shrub','grass_tuft','fern','flower_clump')]
        write_package(package, ROOM, meshes={'land': self.meshes['land']}, setup=dict(SETUP),
                      generator={'name':'test','version':'1'}, terrain=[{'mesh':'land'}], scatter=records)
        out = self.scratch/'landscape_plants'
        stats = export_room(package, ROOM, out.name, out)
        room = json.loads((out/'room.json').read_bytes())
        self.assertEqual(stats['tree_count'], 0)
        self.assertEqual(stats['tree_climb_triangles'], 0)
        self.assertFalse(any(p['id'].startswith('shell:tree_climb_') for p in room['shell']['parts']))

    def test_fixed_populated_tree_uses_hidden_trunk_instead_of_canopy_box(self):
        package = self.scratch/'populated-tree-package'
        write_package(package, ROOM, meshes={'land': self.meshes['land']}, setup=dict(SETUP),
                      generator={'name':'test','version':'1'}, terrain=[{'mesh':'land'}],
                      objects=[dict(id='tree',kind='decor',prototype='conifer',position_m=[0,0,0],
                                    yaw_deg=45,size_m=SIZES['conifer'],mass_kg=100,carriable=False)])
        out = self.scratch/'landscape_populated_tree'
        stats = export_room(package, ROOM, out.name, out)
        room = json.loads((out/'room.json').read_bytes())
        asset = json.loads((out/room['objects'][0]['asset']).read_bytes())
        self.assertEqual(stats['tree_count'], 1)
        self.assertEqual(asset['collision']['kind'], 'none')
        self.assertFalse(asset['physics']['movable'])
        self.assertEqual(len([p for p in room['shell']['parts'] if p['id'].startswith('shell:tree_climb_')]), 2)


GRID = [(-2.5, -2.5), (2.5, 2.5), (-.5, .5), (.5, -.5), (2.5, -2.5), (-2.5, 2.5), (.5, .5), (-.5, -.5)]


class CopyTests(unittest.TestCase):
    """Plant copies on a small package: rows across 8 m tiles, groves and patches, the submerged flag, the meshes,
    and the second process's export against the inline one."""

    @classmethod
    def setUpClass(cls):
        cls.scratch = Path(os.environ.get('TEMP', '/tmp'))/('garage-game-copy-tests-'+uuid.uuid4().hex)
        os.makedirs(cls.scratch)
        land = read_package(REFERENCE, ROOM)[1]['land']
        scatter = [dict(prototype='broadleaf', position_m=[x, 0, z], yaw_deg=0, scale=[.6, .6, .6])
                   for x, z in ((-2, -2.2), (-1.7, -2.2), (-1.85, -1.9))]  # a grove of three
        scatter.append(dict(prototype='conifer', position_m=[2.6, 0, 1.2], yaw_deg=400-760, scale=[.5, .5, .5]))
        scatter += [dict(prototype='flower_clump', position_m=[.6+.05*i, 0, -2.6], yaw_deg=0, scale=[.4, .4, .4])
                    for i in range(6)]  # a patch of six
        scatter.append(dict(prototype='flower_clump', position_m=[-2.6, 0, 1.5], yaw_deg=0, scale=[.4, .4, .4]))
        for i, (x, z) in enumerate(GRID*2):
            scatter.append(dict(prototype='grass_tuft', position_m=[x+(.01 if i >= 8 else 0), 0, z],
                                yaw_deg=i, scale=[.5, .5, .5]))
        scatter += [dict(prototype='grass_tuft', position_m=[1.5, 0, 1.5], yaw_deg=yaw, scale=[.5, .5, .5])
                    for yaw in (30, -20, 10)]  # one place, three copies: package order kept; under the pool
        cls.scatter = scatter
        cls.package = cls.scratch/'copies-package'
        pool = dict(role='still_water', positions=[[1, .05, 1], [1, .05, 2], [2, .05, 2], [2, .05, 1]],
                    triangles=[[0, 1, 2], [0, 2, 3]])
        write_package(cls.package, ROOM, meshes={'land': land, 'pool': [pool]}, setup=dict(SETUP),
                      generator={'name': 'test', 'version': '1'}, terrain=[{'mesh': 'land'}],
                      water=[{'mesh': 'pool', 'kind': 'still'}], scatter=scatter)
        cls.out = cls.scratch/'a'/'landscape_copies'
        cls.stats = export_room(cls.package, ROOM, 'landscape_copies', cls.out)
        cls.room = json.loads((cls.out/'room.json').read_bytes())
        cls.sets = {s['set']: s for s in cls.room['plant_copies']['sets']}

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.scratch)

    def rows(self, name):
        s = self.sets[name]
        return read_table(self.out/s['table'], s['kind'], s.get('cap_slots', 0))

    def test_validates(self):
        proc = subprocess.run([sys.executable, '-B', str(ROOT/'contracts/validate.py'), '--room', str(self.out)],
                              capture_output=True, text=True, cwd=ROOT)
        self.assertEqual(proc.returncode, 0, proc.stdout+proc.stderr)

    def test_rows_sorted_by_tile_then_x_then_z_behind_the_tile_index(self):
        header, tiles, rows = self.rows('grass_tuft')
        self.assertEqual(header[:9], (b'EFPLANTS', 1, 0, 56, 19, 4, 0, 8, 0))
        self.assertEqual([t[:2] for t in tiles], [(-1, -1), (-1, 0), (0, -1), (0, 0)])
        first = 0
        for tx, tz, start, count in tiles:
            self.assertEqual(start, first)
            first += count
            for row in rows[start:start+count]:
                self.assertEqual((math.floor(row[0]/8), math.floor(row[2]/8)), (tx, tz))
        self.assertEqual(first, len(rows))
        keys = [(math.floor(r[0]/8), math.floor(r[2]/8), r[0], r[2]) for r in rows]
        self.assertEqual(keys, sorted(keys))
        # Copies at exactly one place keep the package's order.
        self.assertEqual([r[3] for r in rows if (r[0], r[2]) == (1.5, 1.5)], [30, -20, 10])
        self.assertEqual(self.sets['grass_tuft']['count'], 19)
        self.assertEqual(self.sets['grass_tuft']['tiles'], 4)

    def test_rows_carry_the_record_and_its_top(self):
        _, _, rows = self.rows('conifer')
        x, y, z, yaw, sx, sy, sz, r, g, b, top = rows[0][:11]
        self.assertEqual((x, y, z, yaw), struct.unpack('<4f', struct.pack('<4f', 2.6, 0, 1.2, 0)))  # -360 is 0
        self.assertAlmostEqual(sx, .5)
        self.assertEqual((r, g, b), (1, 1, 1))
        self.assertAlmostEqual(top, y+SIZES['conifer'][1]*sy, places=5)
        # A variety number per copy, from its set and place (three grass tufts share one place).
        rows = [(name, row) for name in self.sets for row in self.rows(name)[2]]
        self.assertEqual(len({row[13] for _, row in rows}), len({(name, row[0], row[2]) for name, row in rows}))
        self.assertEqual(len(rows)-len({row[13] for _, row in rows}), 2)

    def test_groves_and_patches_are_numbered_densely(self):
        self.assertEqual(self.room['plant_copies']['groups'], 2)
        self.assertEqual([r[12] for r in self.rows('broadleaf')[2]], [1, 1, 1])  # the grove, first in table order
        self.assertEqual([r[12] for r in self.rows('conifer')[2]], [0])  # a lone tree
        flowers = self.rows('flower_clump')[2]
        self.assertEqual(sorted(r[12] for r in flowers), [0]+[2]*6)
        self.assertEqual({r[12] for r in self.rows('grass_tuft')[2]}, {0})  # grass makes no patches

    def test_submerged_flag_marks_copies_under_water(self):
        flags = {(r[0], r[2], r[3]): r[11] for r in self.rows('grass_tuft')[2]}
        self.assertEqual([flags[(1.5, 1.5, yaw)] for yaw in (30, -20, 10)], [1, 1, 1])
        self.assertEqual(sum(flags.values()), 3)

    def test_meshes_are_the_prototypes_with_lighter_levels(self):
        for name, s in self.sets.items():
            meshes = [s['mesh']]+[lod['mesh'] for lod in s.get('lods', [])]
            counts = [s['triangle_count']]+[lod['triangle_count'] for lod in s.get('lods', [])]
            self.assertGreaterEqual(len(meshes), 2, name)
            for path, count in zip(meshes, counts):
                _, parts = read_glb(self.out/path)
                self.assertEqual(sum(len(p['triangles']) for p in parts), count)
                vs = [v for p in parts for v in p['positions']]
                self.assertAlmostEqual(min(v[1] for v in vs), 0, places=5)
                for i in (0, 2):
                    self.assertAlmostEqual(min(v[i] for v in vs)+max(v[i] for v in vs), 0, places=5)
            self.assertEqual(counts, sorted(set(counts), reverse=True))
            self.assertEqual(s['triangle_count'], sum(len(p['triangles']) for p in prototype(name)))

    def test_second_process_export_matches_inline(self):
        original = landwork.PARALLEL_BYTES
        landwork.PARALLEL_BYTES = 0
        try:
            other = self.scratch/'b'/'landscape_copies'
            self.assertEqual(export_room(self.package, ROOM, 'landscape_copies', other), self.stats)
        finally:
            landwork.PARALLEL_BYTES = original
        a = {p.relative_to(self.out).as_posix(): p.read_bytes() for p in self.out.rglob('*') if p.is_file()}
        b = {p.relative_to(other).as_posix(): p.read_bytes() for p in other.rglob('*') if p.is_file()}
        self.assertEqual(a, b)


class TileTests(unittest.TestCase):
    """Terrain tiles on a bumpy two-role land crossing tile lines: no cracks between tiles of one level."""

    @classmethod
    def setUpClass(cls):
        step, nx, nz = .25, 104, 72
        heights = [[.3*math.sin(i*.37)+.2*math.cos(j*.61+i*.13)+.05*((i*7919+j*104729) % 13)/13
                    for j in range(nz+1)] for i in range(nx+1)]
        # Off the tile lines, so they cut through triangles.
        points = [[-17.13+i*step, heights[i][j], -9.07+j*step] for i in range(nx+1) for j in range(nz+1)]
        roles = {'meadow': [], 'rock': []}
        for i in range(nx):
            for j in range(nz):
                a, b, c, d = i*(nz+1)+j, i*(nz+1)+j+1, (i+1)*(nz+1)+j+1, (i+1)*(nz+1)+j
                role = 'rock' if math.sin(i*.2)+j*.1 > 1 else 'meadow'
                roles[role] += [[a, b, c], [a, c, d]]
        cls.terrain = [dict(role=role, positions=points, triangles=faces) for role, faces in roles.items()]
        _, tiles = tile_level0(cls.terrain)
        cls.spacings = [step, step*4, step*16]
        cls.levels = build_levels(tiles, cls.spacings)
        cls.land = TerrainHeights(cls.terrain)
        cls.indexes = {}

    def height(self, tile, level, x, z):
        if (tile, level) not in self.indexes:
            self.indexes[tile, level] = TerrainHeights(self.surface(tile, level)) if tile in self.levels else None
        index = self.indexes[tile, level]
        return None if index is None else index.hit(x, z)[0]

    def surface(self, tile, level):
        return [dict(positions=m.pos, triangles=m.tris) for m in self.levels[tile][level].values()]

    def test_tiles_of_one_level_meet_without_cracks(self):
        self.assertEqual(sorted(self.levels), [(-2, -1), (-2, 0), (-1, -1), (-1, 0), (0, -1), (0, 0)])
        # Level 0's cut points are the same bits on both sides of a tile line.
        for left, right, value in (((-1, 0), (0, 0), 0.), ((-2, -1), (-1, -1), -16.), ((-1, -1), (0, -1), 0.)):
            a, b = [{p for m in self.levels[t][0].values() for p in m.pos if p[0] == value} for t in (left, right)]
            self.assertTrue(a)
            self.assertEqual(a, b)
        # Every level's surface is continuous across the lines: where the land is, the height just either side
        # agrees (a skirt is vertical, so it is never the ground).
        samples = 0
        lo = [min(v[i] for v in self.terrain[0]['positions']) for i in range(3)]
        hi = [max(v[i] for v in self.terrain[0]['positions']) for i in range(3)]
        for level, spacing in enumerate(self.spacings):
            for axis, value in ((0, -16.), (0, 0.), (2, 0.)):
                for k in range(1, 400):
                    t = lo[2-axis]+k*(hi[2-axis]-lo[2-axis])/400
                    at = [(value-1e-7, t), (value+1e-7, t)] if axis == 0 else [(t, value-1e-7), (t, value+1e-7)]
                    # A coarse level's own edge may draw in by a cell: sample a cell inside the land's.
                    if not all(lo[0]+spacing < x < hi[0]-spacing and lo[2]+spacing < z < hi[2]-spacing for x, z in at):
                        continue
                    ya, yb = [self.height((math.floor(x/16), math.floor(z/16)), level, x, z) for x, z in at]
                    self.assertIsNotNone(ya, (level, axis, value, t))
                    self.assertIsNotNone(yb, (level, axis, value, t))
                    self.assertAlmostEqual(ya, yb, delta=1e-5, msg=(level, axis, value, t))
                    samples += 1
        self.assertGreater(samples, 2500)

    def test_level0_is_closed_inside_each_tile(self):
        # Cut triangles meet their neighbours: inside a tile, level 0's only open edges are the land's own edges.
        lo = [min(v[i] for v in self.terrain[0]['positions']) for i in range(3)]
        hi = [max(v[i] for v in self.terrain[0]['positions']) for i in range(3)]
        for (tx, tz), per_level in self.levels.items():
            edges = collections.Counter()
            for mesh in per_level[0].values():
                for face in mesh.tris:
                    for a, b in ((face[0], face[1]), (face[1], face[2]), (face[2], face[0])):
                        edges[tuple(sorted((mesh.pos[a], mesh.pos[b])))] += 1
            for (a, b), count in edges.items():
                if count != 1:
                    continue
                on_line = any(a[i] == b[i] == v for i, v in ((0, tx*16.), (0, tx*16.+16), (2, tz*16.), (2, tz*16.+16)))
                on_edge = any(abs(a[i]-b[i]) < 1e-9 and abs(a[i]-v) < 1e-6 for i in (0, 2) for v in (lo[i], hi[i]))
                self.assertTrue(on_line or on_edge, ((tx, tz), a, b))

    def test_coarse_levels_are_lighter_and_skirted_and_level0_is_whole(self):
        triangles_at = [sum(len(m.tris) for tile in self.levels.values() for m in tile[level].values())
                        for level in range(3)]
        self.assertEqual(triangles_at, sorted(triangles_at, reverse=True))
        self.assertLess(triangles_at[1], triangles_at[0]/4)
        self.assertAlmostEqual(area([dict(positions=m.pos, triangles=m.tris) for tile in self.levels.values()
                                     for m in tile[0].values()]), area(self.terrain), places=6)
        lowest = min(v[1] for p in self.terrain for v in p['positions'])
        for level in (1, 2):
            skirt = [p for tile in self.levels.values() for m in tile[level].values() for p in m.pos
                     if p[1] < lowest-1e-9]
            self.assertTrue(skirt, level)
        self.assertFalse([p for tile in self.levels.values() for m in tile[0].values() for p in m.pos
                          if p[1] < lowest-1e-9])

    def test_a_fine_cell_over_a_tile_corner_leaves_no_empty_triangle(self):
        # S4's 2.5 cm cells: a cell's diagonal passing through a tile's corner cuts into points that differ only below
        # float32. They are one vertex as the GLB stores them, and no level-0 triangle has two equal corners.
        f32 = struct.Struct('<3f')
        corners = [[-32.02, -.63517, 31.98], [-31.995, -.6356, 31.98], [-31.995, -.6357, 32.005], [-32.02, -.63517, 32.005]]
        land = [dict(role='meadow', positions=[list(f32.unpack(f32.pack(*p))) for p in corners],
                     triangles=[[0, 2, 1], [0, 3, 2]])]
        _, tiles = tile_level0(land)
        self.assertEqual(sorted(tiles), [(-3, 1), (-3, 2), (-2, 1), (-2, 2)])
        meshes = [dict(positions=[list(f32.unpack(f32.pack(*p))) for p in m.pos], triangles=m.tris)
                  for t in tiles.values() for m in t.values()]
        for m in meshes:
            for face in m['triangles']:
                self.assertEqual(len({tuple(m['positions'][i]) for i in face}), 3, face)
        self.assertAlmostEqual(area(meshes), area(land), delta=1e-8)  # float32 positions

    def test_every_level_keeps_every_role_of_a_tile(self):
        for tile, per_level in self.levels.items():
            self.assertEqual([sorted(m) for m in per_level], [sorted(per_level[0])]*3, tile)
            for meshes in per_level:
                self.assertTrue(all(m.tris for m in meshes.values()), tile)


def ring(lo, hi, grow):
    return [[lo[0]-grow, lo[2]-grow], [hi[0]+grow, lo[2]-grow], [hi[0]+grow, hi[2]+grow], [lo[0]-grow, hi[2]+grow]]


class SeaExportTests(unittest.TestCase):
    """An island's sea (x_generator.sea) reaches the game as x_landscape_sea, whitelisted and bounded."""

    @classmethod
    def setUpClass(cls):
        cls.scratch = Path(os.environ.get('TEMP', '/tmp'))/('garage-game-sea-tests-'+uuid.uuid4().hex)
        os.makedirs(cls.scratch)
        plain = cls.scratch/'plain'/'landscape_plain'
        export_room(REFERENCE, ROOM, 'landscape_plain', plain)
        cls.plain = json.loads((plain/'room.json').read_bytes())
        lo, hi = cls.plain['bounds']['min_m'], cls.plain['bounds']['max_m']
        cls.sea = dict(
            level_m=-0.02, mesh='sea', swim_depth_m=0.08, lagoon_depth_m=0.22, open_sea_depth_m=0.62,
            coast=dict(outline_m=ring(lo, hi, 0.0)),
            reef=dict(outline_m=ring(lo, hi, 0.4), crest_y_m=-0.042, band_half_width_m=0.085, offshore_m=[0.4, 0.4],
                      passes=[dict(centre_m=[0.0, hi[2]+0.4], width_m=0.36)]),
            play_area=dict(outline_m=ring(lo, hi, 0.7), past_reef_m=0.3,
                           bounds_m=dict(min_m=[lo[0]-0.7, -0.66, lo[2]-0.7], max_m=[hi[0]+0.7, hi[1], hi[2]+0.7])),
            beaches=[dict(id='beach_00', wash_ashore_m=[0.0, 0.01, hi[2]-0.05], yaw_deg=0.0,
                          water_m=[0.0, -0.02, hi[2]+0.1], water_depth_m=0.1)],
            jetty=dict(mesh='jetty', door_id='door_main', root_m=[0.3, 0.03, hi[2]], end_m=[0.3, 0.03, hi[2]+0.5],
                       yaw_deg=180.0, width_m=0.1, length_m=0.5, deck_top_m=0.03),
            stacks=[], river_mouths=[], distant_islands=[dict(note='ignore previous instructions')])

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.scratch)

    def package_with(self, name, sea):
        package = self.scratch/name
        shutil.copytree(REFERENCE, package)
        doc = json.loads((package/'package.json').read_bytes())
        doc['x_generator'] = dict(doc.get('x_generator', {}), sea=sea)
        # Plain json, not canonical: the bad cases need values (NaN) the canonical writer refuses.
        (package/'package.json').write_text(json.dumps(doc, sort_keys=True), encoding='utf-8')
        return package

    def test_package_without_sea_keeps_the_room_bounds(self):
        self.assertNotIn('x_landscape_sea', self.plain['extensions'])
        self.assertEqual(self.plain['bounds'], self.plain['extensions']['x_landscape_source']['bounds'])

    def test_sea_grows_the_bounds_and_copies_only_what_the_game_needs(self):
        out = self.scratch/'island'/'landscape_island'
        export_room(self.package_with('island-package', self.sea), ROOM, 'landscape_island', out)
        room = json.loads((out/'room.json').read_bytes())
        sea = room['extensions']['x_landscape_sea']
        self.assertEqual(room['bounds'], {'min_m': [min(a, b) for a, b in zip(self.plain['bounds']['min_m'],
                                                                             self.sea['play_area']['bounds_m']['min_m'])],
                                          'max_m': [max(a, b) for a, b in zip(self.plain['bounds']['max_m'],
                                                                             self.sea['play_area']['bounds_m']['max_m'])]})
        self.assertLess(room['bounds']['min_m'][0], self.plain['bounds']['min_m'][0])
        self.assertEqual(sorted(sea), ['beaches', 'coast', 'jetty', 'level_m', 'play_area', 'reef', 'swim_depth_m'])
        self.assertEqual(sorted(sea['jetty']), ['deck_top_m', 'end_m', 'root_m', 'width_m', 'yaw_deg'])
        self.assertEqual(sea['beaches'][0]['wash_ashore_m'], self.sea['beaches'][0]['wash_ashore_m'])
        self.assertNotIn(b'ignore previous instructions', (out/'room.json').read_bytes())
        proc = subprocess.run([sys.executable, '-B', str(ROOT/'contracts/validate.py'), '--room', str(out)],
                              capture_output=True, text=True, cwd=ROOT)
        self.assertEqual(proc.returncode, 0, proc.stdout+proc.stderr)

    def test_the_generator_margin_travels_with_the_sea(self):
        package = self.package_with('margin-package', self.sea)
        doc = json.loads((package/'package.json').read_bytes())
        doc['x_generator']['margin_m'] = 1.7
        (package/'package.json').write_text(json.dumps(doc, sort_keys=True), encoding='utf-8')
        out = self.scratch/'margin'/'landscape_margin'
        export_room(package, ROOM, 'landscape_margin', out)
        sea = json.loads((out/'room.json').read_bytes())['extensions']['x_landscape_sea']
        self.assertEqual(sea['grid_margin_m'], 1.7)

    def test_bad_sea_fails_before_any_output(self):
        lo = self.plain['bounds']['min_m']
        cases = {
            'nan': dict(self.sea, level_m=float('nan')),
            'points': dict(self.sea, coast=dict(outline_m=[[0.0, 0.0]]*5000)),
            'beach': dict(self.sea, beaches=[dict(self.sea['beaches'][0], wash_ashore_m=[lo[0]-5, 0.0, 0.0])]),
            'duplicate': dict(self.sea, beaches=[self.sea['beaches'][0]]*2),
            'missing': {k: v for k, v in self.sea.items() if k != 'reef'},
        }
        for name, sea in cases.items():
            with self.subTest(name):
                out = self.scratch/('bad-'+name)/'landscape_bad'
                package = self.package_with('bad-package-'+name, sea)
                with self.assertRaises((ValueError, KeyError)):
                    export_room(package, ROOM, 'landscape_bad', out)
                self.assertFalse(out.exists())


if __name__ == '__main__':
    unittest.main()
