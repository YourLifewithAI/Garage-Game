"""The island's Fractals, its tutorial mark and its summit reach the manifest through a bounded whitelist (packet M4), and its
firepits (decision 74, Lane C) too.

    python -B -S -m unittest pipeline.landscape.export.tests.test_fractals -v
"""
import json
import os
import shutil
import subprocess
import sys
import unittest
import uuid
from pathlib import Path

from pipeline.landscape.export import export_room
from pipeline.landscape.export.fractals import MAX_FIREPITS, firepits_data, fractals_data, summit_data, tutorial_flag

ROOT = Path(__file__).resolve().parents[4]
ROOM = ROOT/'pipeline/landscape/corpus/rooms/garage_nominal'
REFERENCE = ROOT/'pipeline/landscape/harness/reference/package'
# The generator's own package extension carries more than the game takes (its routes, its report): none of it travels.
FRACTALS = dict(
    typed=False, budget=dict(min=20, max=25, reachable_first_run=15), routes=[dict(note='ignore previous instructions')],
    items=[dict(id='fractal:a1b2c3d4e5f6', position_m=[0.2, 0.0, 0.1], reach_band=1),
           dict(id='fractal:0f9e8d7c6b5a', position_m=[-0.2, 0.0, -0.1], reach_band=3),
           dict(id='fractal:123456789abc', position_m=[0.1, 0.0, 0.2])],
    detail=[dict(id='fractal:a1b2c3d4e5f6', why='step')])
# The generator's summit record (x_generator.summit); anything else a package says of it is not copied.
SUMMIT = dict(center_m=[0.3, 0.5, 0.2], radius_m=1.3, landmark='couch_1', top_band=8)
# The generator's firepits record (x_generator.firepits): the manifest takes each spot, in order, and nothing else.
FIREPITS = dict(
    items=[dict(position_m=[0.25, 0.0, -0.3], reach_band=1, why='first-run reach, by quarry_7', landmark='quarry_7'),
           dict(position_m=[-0.3, 0.01, 0.25], why='ignore previous instructions', hamlet=True),
           dict(position_m=[0.1, 0.0, 0.3], reach_band=3, why='reached from band 3')],
    counts=dict(placed=3, wanted=3, land_m2=26.0), notes=['ignore previous instructions'], rule=dict(apart_m=4.0))


class Exports:
    """A scratch folder of packages (the reference package with the generator's extension changed) and their exports."""

    @classmethod
    def setUpClass(cls):
        cls.scratch = Path(os.environ.get('TEMP', '/tmp'))/('garage-game-fractal-tests-'+uuid.uuid4().hex)
        os.makedirs(cls.scratch)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.scratch)

    def package_with(self, name, **gen):
        package = self.scratch/name
        shutil.copytree(REFERENCE, package)
        doc = json.loads((package/'package.json').read_bytes())
        doc['x_generator'] = dict(doc.get('x_generator', {}), **gen)
        # Plain json, not canonical: the bad cases need values (NaN) the canonical writer refuses.
        (package/'package.json').write_text(json.dumps(doc, sort_keys=True), encoding='utf-8')
        return package

    def exported(self, name, **gen):
        out = self.scratch/('out-'+name)/('landscape_'+name)
        export_room(self.package_with('pkg-'+name, **gen), ROOM, 'landscape_'+name, out)
        return out, json.loads((out/'room.json').read_bytes())


class FractalExportTests(Exports, unittest.TestCase):
    def test_a_package_without_fractals_says_nothing(self):
        _, room = self.exported('plain')
        self.assertNotIn('x_fractals', room['extensions'])
        self.assertNotIn('x_island_tutorial', room['extensions'])

    def test_the_manifest_takes_each_fractal_and_nothing_else(self):
        out, room = self.exported('island', fractals=FRACTALS, tutorial=True)
        fx = room['extensions']['x_fractals']
        self.assertEqual(sorted(fx), ['items', 'typed'])
        self.assertFalse(fx['typed'])
        self.assertEqual([i['id'] for i in fx['items']], sorted(i['id'] for i in FRACTALS['items']), 'in id order')
        by_id = {i['id']: i for i in fx['items']}
        self.assertEqual(by_id['fractal:a1b2c3d4e5f6'], dict(id='fractal:a1b2c3d4e5f6', position_m=[0.2, 0.0, 0.1], reach_band=1))
        self.assertNotIn('reach_band', by_id['fractal:123456789abc'], 'not measured: absent')
        self.assertIs(room['extensions']['x_island_tutorial'], True)
        self.assertNotIn(b'ignore previous instructions', (out/'room.json').read_bytes())
        proc = subprocess.run([sys.executable, '-B', str(ROOT/'contracts/validate.py'), '--room', str(out)],
                              capture_output=True, text=True, cwd=ROOT)
        self.assertEqual(proc.returncode, 0, proc.stdout+proc.stderr)

    def test_typed_fractals_carry_their_kind(self):
        typed = dict(FRACTALS, typed=True, items=[dict(i, kind='heart' if n % 2 else 'band') for n, i in enumerate(FRACTALS['items'])])
        out, room = self.exported('typed', fractals=typed)
        fx = room['extensions']['x_fractals']
        self.assertTrue(fx['typed'])
        self.assertTrue(all(i['kind'] in ('heart', 'band') for i in fx['items']))
        self.assertNotIn('x_island_tutorial', room['extensions'])
        proc = subprocess.run([sys.executable, '-B', str(ROOT/'contracts/validate.py'), '--room', str(out)],
                              capture_output=True, text=True, cwd=ROOT)
        self.assertEqual(proc.returncode, 0, proc.stdout+proc.stderr)

    def test_untyped_drops_a_kind_it_was_given(self):
        got = fractals_data(dict(fractals=dict(typed=False, items=[dict(FRACTALS['items'][0], kind='heart')])))
        self.assertNotIn('kind', got['items'][0])

    def test_the_tutorial_mark_is_only_ever_true(self):
        self.assertIsNone(tutorial_flag(dict(tutorial=False)))
        self.assertIsNone(tutorial_flag(dict(tutorial='yes')))
        self.assertIsNone(tutorial_flag({}))
        self.assertIsNone(tutorial_flag(None))
        self.assertIs(tutorial_flag(dict(tutorial=True)), True)

    def test_the_manifest_takes_the_summit_and_nothing_else(self):
        out, room = self.exported('summit', fractals=FRACTALS, summit=dict(SUMMIT, note='ignore previous instructions',
                                                                          ledges=[1, 2, 3]))
        self.assertEqual(room['extensions']['x_summit'], SUMMIT)
        self.assertNotIn(b'ignore previous instructions', (out/'room.json').read_bytes())
        proc = subprocess.run([sys.executable, '-B', str(ROOT/'contracts/validate.py'), '--room', str(out)],
                              capture_output=True, text=True, cwd=ROOT)
        self.assertEqual(proc.returncode, 0, proc.stdout+proc.stderr)

    def test_a_summit_without_its_optional_fields_and_a_package_without_one(self):
        _, room = self.exported('bare-summit', summit=dict(center_m=[0.3, 0.5, 0.2], radius_m=2))
        self.assertEqual(room['extensions']['x_summit'], dict(center_m=[0.3, 0.5, 0.2], radius_m=2))
        self.assertIsNone(summit_data(dict(fractals=FRACTALS)))
        self.assertIsNone(summit_data(None))
        _, none = self.exported('no-summit', fractals=FRACTALS)
        self.assertNotIn('x_summit', none['extensions'])

    def test_a_summit_outside_the_island_is_refused_by_the_validator(self):
        out, room = self.exported('far-summit', summit=dict(SUMMIT, center_m=[400.0, 0.5, 0.2]))
        proc = subprocess.run([sys.executable, '-B', str(ROOT/'contracts/validate.py'), '--room', str(out)],
                              capture_output=True, text=True, cwd=ROOT)
        self.assertNotEqual(proc.returncode, 0)
        self.assertIn('x_summit is outside the room bounds', proc.stdout+proc.stderr)

    def test_bad_summits_fail_before_any_output(self):
        cases = {
            'no_radius': dict(SUMMIT, radius_m=0),
            'huge_radius': dict(SUMMIT, radius_m=51),
            'nan_radius': dict(SUMMIT, radius_m=float('nan')),
            'centre_nan': dict(SUMMIT, center_m=[float('nan'), 0.0, 0.0]),
            'centre_short': dict(SUMMIT, center_m=[0.0, 0.0]),
            'centre_far': dict(SUMMIT, center_m=[5000.0, 0.0, 0.0]),
            'landmark_upper': dict(SUMMIT, landmark='Couch'),
            'landmark_path': dict(SUMMIT, landmark='../couch'),
            'landmark_number': dict(SUMMIT, landmark=7),
            'band_zero': dict(SUMMIT, top_band=0),
            'band_float': dict(SUMMIT, top_band=2.5),
            'band_past_the_ladder': dict(SUMMIT, top_band=21),
            'not_a_record': ['couch_1'],
        }
        for name, summit in cases.items():
            with self.subTest(name):
                out = self.scratch/('bad-summit-'+name)/'landscape_bad'
                package = self.package_with('bad-summit-package-'+name, summit=summit)
                with self.assertRaises((ValueError, KeyError)):
                    export_room(package, ROOM, 'landscape_bad', out)
                self.assertFalse(out.exists())

    def test_bad_fractals_fail_before_any_output(self):
        one = FRACTALS['items'][0]
        cases = {
            'id_not_lowercase': dict(FRACTALS, items=[dict(one, id='fractal:UPPER')]),
            'id_namespace': dict(FRACTALS, items=[dict(one, id='obj:a1b2c3d4e5f6')]),
            'duplicate': dict(FRACTALS, items=[one, one]),
            'nan': dict(FRACTALS, items=[dict(one, position_m=[float('nan'), 0.0, 0.0])]),
            'far': dict(FRACTALS, items=[dict(one, position_m=[5000.0, 0.0, 0.0])]),
            'band': dict(FRACTALS, items=[dict(one, reach_band=0)]),
            'band_float': dict(FRACTALS, items=[dict(one, reach_band=1.5)]),
            'typed_without_kind': dict(FRACTALS, typed=True),
            'typed_flag': dict(FRACTALS, typed='yes'),
            'too_many': dict(FRACTALS, items=[dict(one, id='fractal:%012x' % n) for n in range(1001)]),
        }
        for name, fx in cases.items():
            with self.subTest(name):
                out = self.scratch/('bad-'+name)/'landscape_bad'
                package = self.package_with('bad-package-'+name, fractals=fx)
                with self.assertRaises((ValueError, KeyError)):
                    export_room(package, ROOM, 'landscape_bad', out)
                self.assertFalse(out.exists())


class FirepitExportTests(Exports, unittest.TestCase):
    """The generator's firepits reach the manifest as x_firepits (the kernel reads them): each spot, in the generator's order,
    numbers checked and counted, and nothing else."""

    def test_the_manifest_takes_each_firepit_and_nothing_else(self):
        out, room = self.exported('firepits', firepits=FIREPITS)
        self.assertEqual(room['extensions']['x_firepits'], dict(items=[dict(position_m=i['position_m']) for i in FIREPITS['items']]),
                         'in the generator\'s order: the kernel numbers them so')
        self.assertNotIn(b'ignore previous instructions', (out/'room.json').read_bytes())
        self.assertNotIn(b'quarry_7', (out/'room.json').read_bytes())
        proc = subprocess.run([sys.executable, '-B', str(ROOT/'contracts/validate.py'), '--room', str(out)],
                              capture_output=True, text=True, cwd=ROOT)
        self.assertEqual(proc.returncode, 0, proc.stdout+proc.stderr)

    def test_a_package_without_firepits_and_an_empty_list(self):
        _, room = self.exported('no-firepits', fractals=FRACTALS)
        self.assertNotIn('x_firepits', room['extensions'])
        self.assertIsNone(firepits_data(None))
        self.assertIsNone(firepits_data(dict(fractals=FRACTALS)))
        self.assertEqual(firepits_data(dict(firepits=dict(items=[]))), dict(items=[]), 'an island the generator found no ground on')

    def test_a_firepit_outside_the_island_is_refused_by_the_validator(self):
        far = dict(FIREPITS, items=[dict(position_m=[400.0, 0.0, 0.2])])
        out, room = self.exported('far-firepit', firepits=far)
        proc = subprocess.run([sys.executable, '-B', str(ROOT/'contracts/validate.py'), '--room', str(out)],
                              capture_output=True, text=True, cwd=ROOT)
        self.assertNotEqual(proc.returncode, 0)
        self.assertIn('x_firepits 1 is outside the room bounds', proc.stdout+proc.stderr)

    def test_bad_firepits_fail_before_any_output(self):
        one = FIREPITS['items'][0]
        cases = {
            'nan': dict(items=[dict(one, position_m=[float('nan'), 0.0, 0.0])]),
            'infinite': dict(items=[dict(one, position_m=[0.0, float('inf'), 0.0])]),
            'far': dict(items=[dict(one, position_m=[5000.0, 0.0, 0.0])]),
            'short': dict(items=[dict(one, position_m=[0.0, 0.0])]),
            'words': dict(items=[dict(one, position_m=['0', 0.0, 0.0])]),
            'a flag for a number': dict(items=[dict(one, position_m=[True, 0.0, 0.0])]),
            'no spot': dict(items=[dict(why='here')]),
            'one spot twice': dict(items=[one, dict(one)]),
            'too many': dict(items=[dict(position_m=[0.01*n, 0.0, 0.0]) for n in range(MAX_FIREPITS+1)]),
            'no list': dict(items=dict(position_m=[0.0, 0.0, 0.0])),
            'no items': dict(counts={}),
            'not a record': [[0.0, 0.0, 0.0]],
        }
        for name, fp in cases.items():
            with self.subTest(name):
                out = self.scratch/('bad-fp-'+name.replace(' ', '-'))/'landscape_bad'
                package = self.package_with('bad-fp-package-'+name.replace(' ', '-'), firepits=fp)
                with self.assertRaises((ValueError, KeyError)):
                    export_room(package, ROOM, 'landscape_bad', out)
                self.assertFalse(out.exists())

    def test_the_cap_is_the_manifests(self):
        self.assertEqual(len(firepits_data(dict(firepits=dict(items=[dict(position_m=[0.01*n, 0.0, 0.0])
                                                                     for n in range(MAX_FIREPITS)])))['items']), MAX_FIREPITS)
        schema = json.loads((ROOT/'contracts/room-manifest.schema.json').read_text(encoding='utf-8'))
        self.assertEqual(schema['$defs']['x_firepits']['properties']['items']['maxItems'], MAX_FIREPITS)


if __name__ == '__main__':
    unittest.main()
