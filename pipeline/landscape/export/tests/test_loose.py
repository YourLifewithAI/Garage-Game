import copy
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys
import unittest
import uuid

from pipeline.landscape.export import export_room
from pipeline.landscape.export.loose import (LOOSE_CAP, bounds, closed_pieces, estimated_mass,
                                             footprint_maximum, log_boxes, volume)
from pipeline.landscape.export.mesh import transform
from pipeline.landscape.export.trees import TerrainHeights
from pipeline.landscape.generator.life import prototypes
from pipeline.landscape.harness.common import SETUP
from pipeline.landscape.harness.kit import SIZES, prototype
from pipeline.landscape.harness.package import read_package, write_package
from .test_export import ROOT, ROOM, read_glb, triangles


def flat_terrain():
    return [dict(role='meadow', positions=[[-4,0,-5], [4,0,-5], [4,0,3], [-4,0,3]],
                 triangles=[[0,2,1], [0,3,2]])]


def record(name, x=0, z=0, scale=(1,1,1), yaw=0):
    return dict(prototype=name, position_m=[x,0,z], scale=list(scale), yaw_deg=yaw)


def rounded_triangles(parts):
    return triangles([dict(p, positions=[[round(v, 6) for v in point] for point in p['positions']])
                      for p in parts])


class LooseTests(unittest.TestCase):
    def setUp(self):
        self.scratch = Path(os.environ.get('TEMP', '/tmp'))/('garage-game-loose-tests-'+uuid.uuid4().hex)
        os.makedirs(self.scratch)

    def tearDown(self):
        shutil.rmtree(self.scratch)

    def package(self, name, scatter=(), objects=(), logs=None, terrain=None):
        parts, size = prototypes()['woodpile']
        parts = logs if logs is not None else parts
        size = [b-a for a, b in zip(*bounds(parts))]
        folder = self.scratch/(name+'-package')
        write_package(folder, ROOM, meshes={'land': terrain or flat_terrain(), 'logs': parts},
                      setup=dict(SETUP), generator={'name':'test', 'version':'1'},
                      terrain=[{'mesh':'land'}], prototypes={'woodpile': {'mesh':'logs', 'size_m':size}},
                      scatter=scatter, objects=objects)
        return folder

    def export(self, package, name='loose_test'):
        out = self.scratch/name
        stats = export_room(package, ROOM, name, out)
        room = json.loads((out/'room.json').read_bytes())
        return out, room, stats

    def test_closed_pieces_are_topological_not_primitive_or_log_count(self):
        logs = prototypes()['woodpile'][0]
        merged = dict(role='bark', positions=[], triangles=[])
        for part in logs[:4]:
            offset = len(merged['positions'])
            merged['positions'].extend(part['positions'])
            merged['triangles'].extend([[i+offset for i in face] for face in part['triangles']])
        pieces = closed_pieces([merged])
        self.assertEqual(len(pieces), 4)
        self.assertEqual(triangles([p for piece in pieces for p in piece]), triangles([merged]))
        package = self.package('four-logs', scatter=[record('woodpile')], logs=[merged])
        out, room, stats = self.export(package)
        self.assertEqual(stats['loose_things'], 4)
        self.assertEqual([o['display_name'] for o in room['objects']], ['Log']*4)
        # Each face has private indices and alternating roles: seams still close.
        seams = []
        for k, face in enumerate(logs[0]['triangles']):
            seams.append(dict(role='bark' if k % 2 else 'wood',
                              positions=[logs[0]['positions'][i] for i in face], triangles=[[0,1,2]],
                              tints=[[.7,.8,.9,1]]*3, blend_role='timber', blend_weights=[.25]*3))
        self.assertEqual(len(closed_pieces(seams)), 1)
        for part in closed_pieces(seams)[0]:
            self.assertEqual(part['tints'], [[.7,.8,.9,1]]*3)
            self.assertEqual(part['blend_weights'], [.25]*3)
        opened = copy.deepcopy(logs)
        opened[0]['triangles'].pop()
        with self.assertRaisesRegex(ValueError, 'closed manifold'):
            closed_pieces(opened)

    def test_yawed_scaled_logs_keep_every_drawn_triangle_and_validate(self):
        rec = record('woodpile', x=.7, z=-1, scale=(1.3,.7,.8), yaw=37)
        rec['tint'] = [.8,.9,.7]
        package = self.package('transformed', scatter=[rec])
        out, room, stats = self.export(package)
        doc, meshes, _, _ = read_package(package, ROOM)
        expected = transform(meshes['logs'], rec['scale'], rec['position_m'], rec['yaw_deg'])
        drawn = []
        for item in room['objects']:
            asset = json.loads((out/item['asset']).read_bytes())
            self.assertEqual(item['display_name'], 'Log')
            self.assertTrue(asset['physics']['movable'])
            self.assertLessEqual(asset['physics']['mass_kg'], .5)
            self.assertGreater(asset['physics']['mass_kg'], .001)
            self.assertEqual(asset['collision']['kind'], 'box')
            local = read_glb(out/item['asset'].replace('asset.json', 'mesh.glb'))[1]
            drawn.extend(transform(local, position=item['transform']['position_m'], yaw=rec['yaw_deg']))
        self.assertEqual(rounded_triangles(drawn), rounded_triangles(expected))
        self.assertEqual(stats['loose_things'], 5)
        proc = subprocess.run([sys.executable, '-B', str(ROOT/'contracts/validate.py'), '--room', str(out)],
                              capture_output=True, text=True, cwd=ROOT)
        print('LOOSE_VALIDATOR exit='+str(proc.returncode)+' '+proc.stdout.strip())
        self.assertEqual(proc.returncode, 0, proc.stdout+proc.stderr)
        other = self.scratch/'repeat'/out.name
        export_room(package, ROOM, out.name, other)
        self.assertEqual({p.relative_to(out):p.read_bytes() for p in out.rglob('*') if p.is_file()},
                         {p.relative_to(other):p.read_bytes() for p in other.rglob('*') if p.is_file()})

    def test_boxes_contact_ground_and_lower_logs_without_overlap(self):
        parts = prototypes()['woodpile'][0]
        terrain = flat_terrain()
        for p in terrain:
            p['positions'] = [[x, .006+.02*x+.01*z, z] for x, _, z in p['positions']]
        heights = TerrainHeights(terrain)
        position, yaw = [.1, 0, -.2], 53
        boxes = log_boxes(parts, [1,1,1], position, yaw, heights)
        # Compare in the pile's axes. Rotation preserves intersections.
        actual = []
        angle = math.radians(yaw)
        for box in boxes:
            x, y, z = box['position_m']
            dx, dz = x-position[0], z-position[2]
            local = [math.cos(angle)*dx-math.sin(angle)*dz, y,
                     math.sin(angle)*dx+math.cos(angle)*dz]
            size = box['size_m']
            actual.append(([local[0]-size[0]/2, y, local[2]-size[2]/2],
                           [local[0]+size[0]/2, y+size[1], local[2]+size[2]/2]))
        for index, (lo, hi) in enumerate(actual):
            ground_y = footprint_maximum(heights, lo, hi, position, yaw)
            # Independent analytic plane maximum over the four yawed corners.
            corners = [(position[0]+math.cos(angle)*x+math.sin(angle)*z,
                        position[2]-math.sin(angle)*x+math.cos(angle)*z)
                       for x in (lo[0], hi[0]) for z in (lo[2], hi[2])]
            self.assertAlmostEqual(ground_y, max(.006+.02*x+.01*z for x, z in corners))
            self.assertGreaterEqual(lo[1]+1e-9, ground_y)
            support = boxes[index]['support_index']
            if support is None:
                self.assertAlmostEqual(lo[1], ground_y)
            else:
                self.assertAlmostEqual(lo[1], actual[support][1][1])
                self.assertTrue(all(min(hi[i], actual[support][1][i]) >
                                    max(lo[i], actual[support][0][i]) for i in (0,2)))
            for other_lo, other_hi in actual[:index]:
                self.assertFalse(all(min(hi[i], other_hi[i])-max(lo[i], other_lo[i]) > 1e-8
                                     for i in range(3)))

    def test_every_kit_and_custom_prototype_has_a_deliberate_policy(self):
        expected = set(SIZES) | {'grass','wildflowers','daisies','buttercups','heather','reeds',
                                'woodpile','drying_line','footbridge'}
        self.assertEqual(set(SIZES) | set(prototypes()), expected)
        for name in expected-{'woodpile'}:
            parts, size = (prototype(name), SIZES[name]) if name in SIZES else prototypes()[name]
            self.assertIsNone(estimated_mass(name, parts, size), name)
        for name, scale in [('rock', .3), ('boulder', .12), ('crate', .45)]:
            parts, size = prototype(name), SIZES[name]
            scaled = transform(parts, [scale]*3)
            mass = estimated_mass(name, scaled, [v*scale for v in size])
            self.assertIsNotNone(mass)
            self.assertLessEqual(mass, .5)
        self.assertIsNone(estimated_mass('crate', prototype('crate'), SIZES['crate']))
        self.assertGreater(volume(prototypes()['woodpile'][0]), 0)

    def test_stones_crates_and_authored_masses(self):
        authored = dict(id='kept', kind='crate', prototype='crate', position_m=[-1,0,0], yaw_deg=0,
                        size_m=[.08,.07,.07], mass_kg=.321, carriable=True)
        fixed = dict(authored, id='new', mass_kg=.123, carriable=False)
        records = [record('rock', x=.5, scale=[.3]*3), record('crate', x=1, scale=[.45]*3),
                   record('lantern', x=1.5, scale=[.5]*3), record('boulder', x=2)]
        out, room, stats = self.export(self.package('cases', scatter=records, objects=[authored, fixed]))
        items = room['extensions']['x_landscape_loose']['items']
        masses = {i['id']:i['mass_kg'] for i in items}
        self.assertEqual(masses['obj:kept'], .321)
        self.assertEqual(masses['obj:new'], .123)
        self.assertEqual(stats['loose_things'], 4)
        self.assertEqual({i['name'] for i in items if i['source'].startswith('scatter:')}, {'Stone','Crate'})
        self.assertEqual(len(room['light_hints']), 2)

    def test_cap_keeps_whole_piles_and_all_overflow_geometry(self):
        records = [record('rock', x=-1, scale=[.2]*3) for _ in range(80)]
        records += [record('woodpile', x=1) for _ in range(13)]
        out, room, stats = self.export(self.package('cap', scatter=records))
        items = room['extensions']['x_landscape_loose']['items']
        self.assertEqual(stats['loose_things'], LOOSE_CAP)
        self.assertEqual(sum(i['name'] == 'Log' for i in items), 60)
        self.assertEqual(stats['loose_skipped'], {'woodpile':5, 'rock':76})
        merged = []
        for p in room['shell']['parts']:
            if p['id'].startswith('shell:scatter_'):
                merged.extend(read_glb(out/p['geometry']['mesh'])[1])
        # The pile left over stays merged; the stones left over are stone copies.
        self.assertEqual(sum(len(p['triangles']) for p in merged),
                         sum(len(p['triangles']) for p in prototypes()['woodpile'][0]))
        self.assertEqual(stats['copies'], {'rock': 76})
        self.assertLessEqual(stats['objects'], 512)
        self.assertLessEqual(stats['files']-1, 2048)

    def test_entity_budget_reserves_logs_after_many_landmarks(self):
        records = [record('boulder') for _ in range(509)]+[record('woodpile')]
        out, room, stats = self.export(self.package('entities', scatter=records))
        self.assertEqual(stats['objects'], 512)
        self.assertEqual(stats['loose_things'], 5)
        self.assertEqual(stats['merged_scatter'], 2)
        self.assertLessEqual(stats['files']-1, 2048)

    def test_authored_carryables_over_cap_fail_before_output(self):
        objects = [dict(id='crate_'+str(i), kind='crate', prototype='crate', position_m=[0,0,0],
                        yaw_deg=0, size_m=[.08,.07,.07], mass_kg=.1, carriable=True) for i in range(65)]
        package = self.package('authored-cap', objects=objects)
        with self.assertRaisesRegex(ValueError, 'authored carryables exceed'):
            export_room(package, ROOM, 'over_cap', self.scratch/'over_cap')
        self.assertFalse((self.scratch/'over_cap').exists())

    def test_populated_woodpile_is_split_with_object_support_links(self):
        obj = dict(id='pile', kind='woodpile', prototype='woodpile', position_m=[0,0,0], yaw_deg=0,
                   size_m=[.09,.044,.1], mass_kg=1, carriable=False)
        out, room, stats = self.export(self.package('populated', objects=[obj]))
        self.assertEqual(stats['loose_things'], 5)
        ids = {o['id'] for o in room['objects']}
        top = [o for o in room['objects'] if o['support']['kind'] == 'object']
        self.assertEqual(len(top), 2)
        self.assertTrue(all(o['support']['target_id'] in ids for o in top))


if __name__ == '__main__':
    unittest.main()
