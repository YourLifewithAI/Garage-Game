"""The plant maker (Run 4, FLORA-1): one oak, two ways, proved by bytes and counts. No picture is looked at.

    python -B -m unittest pipeline.landscape.generator.flora.tests.test_flora -v

What is proved: the same sheet and seed give the same bytes (twice, in other processes, and as pinned); the triangle
caps hold and each level is lighter; a tree has at most eight caps and its collision keeps the contract's rules for
a tree row; the oak's two ways are one tree; and the tree is a tree by its numbers: a flared trunk, limbs in
flushes that leave wide below and steep above, thickness by the pipe model, a broad lobed crown, bare inside, on
twigs. A second, made-up sheet (tests/sheets) proves the maker takes more than the oak.
"""
import copy
import json
import math
import os
import struct
import subprocess
import sys
import unittest
from pathlib import Path

from pipeline.landscape.export import mesh as export_mesh, trees as export_trees
from pipeline.landscape.generator import flora
from pipeline.landscape.generator.flora import det, grow, make, pinned, shape, sheet as Sheet
from pipeline.landscape.harness import glb as harness_glb, kit
from pipeline.landscape.harness.common import token as harness_token

ROOT = Path(__file__).resolve().parents[5]
FLORA = Path(__file__).resolve().parents[1]
TEST_SHEETS = Path(__file__).resolve().parent/'sheets'
OAKS = ('oak_clumps', 'oak_cards')
TREE_CAPS = (1500, 300, 60)   # docs/runs/RUN-4-FLORA.md, "Provisional values": triangles by level for a tree
MAX_CAPS = 8                  # contracts/room-manifest.schema.json, plant_set.cap_slots
BODY_CLEARANCE_M = .12        # export/trees.py WALK_CLEARANCE_M: a 10 cm body and 2 cm to spare
SMALLEST_SCALE = .4           # the smallest a planter sets a tree today (generate.py: .4 + .3 r)

_BUILT = {}


def built(species, style, folder=Sheet.SHEETS, change=None):
    """A sheet built once per test run (the maker is deterministic, so a second build is the same record)."""
    key = (species, style, str(folder), json.dumps(change, sort_keys=True))
    if key not in _BUILT:
        sheet = Sheet.load(species, folder)
        if change:
            sheet = Sheet.resolve(Sheet._merge(sheet, change), folder)
        _BUILT[key] = make.build(sheet, style, f'{species}_{style}')
    return _BUILT[key]


def part(record, level, role):
    return next(p for p in record['levels'][level]['parts'] if p['role'] == role)


def level_reach(record, level=0):
    """How far the leaves reach from the trunk's axis in each of twelve sectors."""
    ax, az = record['collision']['axis_m']
    reach = [0.]*12
    for p in part(record, level, 'foliage')['positions']:
        dx, dz = p[0]-ax, p[2]-az
        k = int((math.atan2(dz, dx)+math.pi)/math.tau*12) % 12
        reach[k] = max(reach[k], math.hypot(dx, dz))
    return reach


def angle_from(a, b):
    """Degrees between two directions."""
    dot = sum(x*y for x, y in zip(a, b))
    return math.degrees(math.acos(max(-1., min(1., dot/(math.hypot(*a)*math.hypot(*b))))))


def first_step(axis):
    return [axis['points'][1][i]-axis['points'][0][i] for i in range(3)]


class SameArithmeticEverywhere(unittest.TestCase):
    def test_the_stream_is_splitmix64(self):
        """The published first outputs for seed 0: the stream is whole-number arithmetic, the same on any Python."""
        rng = det.Rng(0)
        self.assertEqual([rng.u64() for _ in range(3)],
                         [0xE220A8397B1DCDAF, 0x6E789E6AA1B965F4, 0x06C45D188009454F])
        self.assertEqual(det.Rng(1).random(), 0.5665615751722809)

    def test_sine_power_and_logarithm_agree_with_the_library(self):
        for k in range(-900, 900):
            x = k/37.
            self.assertAlmostEqual(det.sin(x), math.sin(x), delta=1e-13)
            self.assertAlmostEqual(det.cos(x), math.cos(x), delta=1e-13)
        for k in range(1, 400):
            x = k/29.
            self.assertAlmostEqual(det.ln(x), math.log(x), delta=1e-13)
            self.assertAlmostEqual(det.power(x, 1/2.3), x**(1/2.3), delta=1e-13)
        self.assertEqual(det.power(0., .5), 0.)

    def test_the_maker_calls_no_library_curve(self):
        """Nothing that grows or skins a plant calls a libm curve or the random module: their last bits differ
        between machines and versions (det.py). The pinned check and the preview may, for they pin nothing."""
        for name in ('grow.py', 'shape.py', 'make.py', 'det.py'):
            text = (FLORA/name).read_text(encoding='utf-8')
            for banned in ('math.sin', 'math.cos', 'math.tan', 'math.atan', 'math.acos', 'math.hypot', 'math.pow',
                           'math.exp', 'math.log', 'import random', '**'):
                self.assertNotIn(banned, text, f'{name} uses {banned}')


class Sheets(unittest.TestCase):
    def test_the_oak_names_both_ways(self):
        sheet = Sheet.load('oak')
        self.assertEqual(Sheet.names(sheet), [('oak_clumps', 'clumps'), ('oak_cards', 'cards')])
        self.assertEqual(sheet['model'], 'rauh')
        self.assertEqual(tuple(sheet['levels']), TREE_CAPS)
        self.assertLessEqual(sheet['climb']['caps'], MAX_CAPS)
        self.assertTrue(1.8 <= sheet['pipe_exponent'] <= 2.3, "Leonardo's exponent as measured (README)")

    def test_a_sheet_that_lists_no_style_takes_the_default(self):
        """RUN-4.md D7: clumps unless he picks cards, and then a rerun: one default, in one place."""
        spire = Sheet.load('spire', TEST_SHEETS)
        self.assertEqual(Sheet.DEFAULT_STYLE, 'clumps')
        self.assertEqual(Sheet.names(spire), [('spire', 'clumps')])
        self.assertEqual(Sheet.names(spire, 'cards'), [('spire', 'cards')])

    def test_a_typing_slip_fails(self):
        raw = json.loads((Sheet.SHEETS/'oak.json').read_text(encoding='utf-8'))
        for change in (lambda s: s['trunk'].update(flair=2), lambda s: s['trunk'].pop('bole'),
                       lambda s: s.update(model='palm'), lambda s: s['orders'][0].update(segments=0),
                       lambda s: s['crown'].update(profile=[[0, 1], [.5, 1]]), lambda s: s.update(levels=[60, 300, 1500])):
            broken = copy.deepcopy(raw)
            change(broken)
            with self.assertRaises(ValueError):
                Sheet.resolve(broken)

    def test_a_sheet_laid_over_its_base(self):
        base, old = Sheet.load('spire', TEST_SHEETS), Sheet.load('old_spire', TEST_SHEETS)
        self.assertEqual(old['trunk']['radius_m'], .045)
        self.assertEqual(old['orders'][0]['count'], [9, 9])
        for same in ('crown', 'leaves', 'levels', 'size'):
            self.assertEqual(old[same], base[same])
        self.assertEqual(old['orders'][0]['whorl'], base['orders'][0]['whorl'])
        self.assertEqual(old['orders'][1], base['orders'][1])
        self.assertNotEqual(Sheet.digest(old), Sheet.digest(base))


class SameBytes(unittest.TestCase):
    def test_twice_from_the_same_seed(self):
        sheet = Sheet.load('oak')
        for name, style in Sheet.names(sheet):
            first = pinned.pack(make.build(sheet, style, name))
            again = pinned.pack(make.build(Sheet.load('oak'), style, name))
            self.assertEqual(first, again, name)

    def test_another_seed_is_another_tree(self):
        other = built('oak', 'clumps', change={'seed': 2})
        self.assertNotEqual(pinned.pack(other), pinned.pack(built('oak', 'clumps')))
        pinned.check(other)

    def test_in_other_processes_under_other_hash_seeds(self):
        code = ('import sys\nfrom pipeline.landscape.generator.flora import make, pinned\n'
                'sys.stdout.write(" ".join(n+"="+pinned.sha(pinned.pack(r)) for n, r in sorted(make.build_all().items())))')
        want = ' '.join(f'{name}={pinned.sha((pinned.PINNED/(name+".json")).read_bytes())}' for name in sorted(OAKS))
        for seed in ('0', '4242'):
            run = subprocess.run([sys.executable, '-B', '-c', code], cwd=ROOT, capture_output=True, text=True,
                                 env=dict(os.environ, PYTHONHASHSEED=seed), timeout=300)
            self.assertEqual(run.returncode, 0, run.stderr)
            self.assertEqual(run.stdout, want, f'PYTHONHASHSEED={seed}')

    def test_the_pinned_files_are_their_rebuild(self):
        made = make.build_all()
        self.assertEqual(sorted(made), sorted(OAKS))
        self.assertEqual(flora.names(), sorted(OAKS), 'a pinned file no sheet makes')
        for name, record in made.items():
            self.assertEqual((pinned.PINNED/f'{name}.json').read_bytes(), pinned.pack(record), name)
            self.assertEqual(record['maker']['sheet_sha256'], Sheet.digest(Sheet.load('oak')))

    def test_the_check_command_agrees(self):
        run = subprocess.run([sys.executable, '-B', '-m', 'pipeline.landscape.generator.flora.make', '--check'],
                             cwd=ROOT, capture_output=True, text=True, timeout=300)
        self.assertEqual(run.returncode, 0, run.stdout+run.stderr)
        self.assertEqual(run.stdout.strip().splitlines()[-1], 'FLORA_PINNED: 2 plants, 0 stale')

    def test_pinned_bytes_are_plain(self):
        for name in OAKS:
            data = (pinned.PINNED/f'{name}.json').read_bytes()
            self.assertNotIn(b'\r', data)
            self.assertTrue(data.endswith(b'}\n') and not data.startswith(b'\xef\xbb\xbf'))
            data.decode('ascii')
            self.assertLessEqual(max(len(line) for line in data.split(b'\n')), pinned.WIDTH+40)
            self.assertEqual(pinned.pack(json.loads(data)), data, 'reading and packing gives the bytes back')
            self.assertNotIn(b'e-', data, 'no number in exponent form')

    def test_git_never_converts_a_pinned_plant(self):
        run = subprocess.run(['git', '-C', str(ROOT), 'check-attr', 'text', '--',
                              'pipeline/landscape/generator/flora/pinned/oak_clumps.json'],
                             capture_output=True, text=True)
        if run.returncode != 0:
            self.skipTest('not a git checkout')
        self.assertTrue(run.stdout.strip().endswith('text: unset'), run.stdout)


class CapsHold(unittest.TestCase):
    def test_triangle_caps_hold_and_each_level_is_lighter(self):
        for name in OAKS:
            record = flora.load(name)
            counts = [level['triangle_count'] for level in record['levels']]
            self.assertEqual(len(counts), 3)
            for count, cap in zip(counts, TREE_CAPS):
                self.assertLessEqual(count, cap, name)
            self.assertTrue(counts[0] > counts[1] > counts[2], name)
            for level, count in zip(record['levels'], counts):
                self.assertEqual(sum(len(p['triangles']) for p in level['parts']), count)

    def test_the_levels_hold_one_tree(self):
        """A lighter level never swells past the finest one's leaves, and stands as tall: no pop as it swaps."""
        for name in OAKS:
            record = flora.load(name)
            fine = part(record, 0, 'foliage')['positions']
            low = [min(p[i] for p in fine) for i in range(3)]
            high = [max(p[i] for p in fine) for i in range(3)]
            for level in (1, 2):
                leaves = part(record, level, 'foliage')['positions']
                for p in leaves:
                    for i in range(3):
                        self.assertTrue(low[i]-1e-4 <= p[i] <= high[i]+1e-4, f'{name} level {level}')
                self.assertGreater(max(p[1] for p in leaves), high[1]-.12, f'{name} level {level} is as tall')
                wide = max(p[0] for p in leaves)-min(p[0] for p in leaves)
                self.assertGreater(wide, .75*(high[0]-low[0]), f'{name} level {level} is as wide')

    def test_at_most_eight_caps_a_tree(self):
        for name in OAKS:
            caps = flora.load(name)['collision']['caps']
            self.assertTrue(1 <= len(caps) <= MAX_CAPS, name)
        self.assertGreaterEqual(len(flora.load('oak_clumps')['collision']['caps']), 4, 'an oak is more than a pole')
        with self.assertRaises(ValueError):
            Sheet.resolve(Sheet._merge(Sheet.load('oak'), {'climb': {'caps': 9}}))

    def test_a_tight_cap_prunes_twigs_and_still_holds(self):
        tight = built('oak', 'clumps', change={'levels': [1300, 280, 56]})
        counts = [level['triangle_count'] for level in tight['levels']]
        for count, cap in zip(counts, (1300, 280, 56)):
            self.assertLessEqual(count, cap)
        self.assertGreater(tight['counts']['pruned'], 0)
        self.assertLess(tight['counts']['twigs'], flora.load('oak_clumps')['counts']['twigs'])
        self.assertEqual(tight['counts']['limbs'], flora.load('oak_clumps')['counts']['limbs'], 'twigs go first')

    def test_a_cap_no_tree_fits_fails_loudly(self):
        sheet = Sheet.resolve(Sheet._merge(Sheet.load('oak'), {'levels': [700, 300, 60]}))
        with self.assertRaisesRegex(ValueError, 'over the triangle cap'):
            make.build(sheet, 'clumps', 'oak_clumps')


class ATreeByItsNumbers(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.record = flora.load('oak_clumps')
        cls.sheet = Sheet.load('oak')
        cls.skeleton = cls.record['skeleton']
        cls.axis = cls.record['collision']['axis_m']

    def test_the_trunk_flares_into_roots(self):
        sides = self.sheet['trunk']['sides']
        bark = part(self.record, 0, 'bark')['positions']
        rings = [bark[k*sides:(k+1)*sides] for k in range(3)]   # the trunk's rings come first, from the ground up
        self.assertTrue(all(p[1] == 0. for p in rings[0]), 'the first ring stands on the ground')
        radii = [[math.hypot(p[0]-self.axis[0], p[2]-self.axis[1]) for p in ring] for ring in rings]
        mean = [sum(r)/sides for r in radii]
        bole = self.skeleton[0]['points'][1][3]
        self.assertGreater(mean[0], 1.6*bole, 'the foot is far wider than the bole')
        self.assertTrue(mean[0] > mean[1] > mean[2] >= bole-1e-3, 'and narrows up the flare')
        self.assertGreater(mean[0]-mean[1], mean[1]-mean[2], 'steepest at the ground: a flare, not a cone')
        self.assertGreater(max(radii[0])/min(radii[0]), 1.15, 'root ridges at the ground')
        self.assertLess(max(radii[2])/min(radii[2]), 1.02, 'and a round bole above them')

    def test_stems_branch_by_rule(self):
        counts = self.record['counts']
        orders = [a['order'] for a in self.skeleton]
        self.assertEqual([orders.count(k) for k in range(4)], [1, counts['limbs'], counts['boughs'], counts['twigs']])
        low, high = self.sheet['orders'][0]['count']
        self.assertTrue(low <= counts['limbs'] <= high)
        self.assertGreater(counts['boughs'], counts['limbs'])
        self.assertGreater(counts['twigs'], counts['limbs'])
        for axis in self.skeleton[1:]:
            parent = self.skeleton[axis['parent']]
            self.assertEqual(axis['points'][0][:3], parent['points'][axis['at']][:3], 'a stem starts on its parent')
            self.assertEqual(axis['order'], parent['order']+1)

    def test_limbs_come_in_flushes_wide_below_and_steep_above(self):
        """Rauh's model: the trunk grows in flushes and each ends in a cluster of limbs. The lowest leave near
        level and the highest climb, as an oak's do."""
        limbs = [a for a in self.skeleton if a['order'] == 1]
        flushes = sorted({a['at'] for a in limbs})
        self.assertEqual(len(flushes), self.record['counts']['flushes'])
        self.assertGreaterEqual(len(flushes), 2)
        whorl = self.sheet['orders'][0]['whorl']
        mean_angle = []
        for at in flushes:
            cluster = [a for a in limbs if a['at'] == at]
            self.assertLessEqual(len(cluster), whorl)
            mean_angle.append(sum(angle_from(first_step(a), (0, 1, 0)) for a in cluster)/len(cluster))
            turns = sorted(math.degrees(math.atan2(first_step(a)[2], first_step(a)[0])) for a in cluster)
            gaps = [b-a for a, b in zip(turns, turns[1:])]+[turns[0]+360-turns[-1]]
            if len(cluster) > 1:
                self.assertGreater(min(gaps), 50, 'the limbs of one flush stand round the trunk, not in a bunch')
        self.assertGreater(mean_angle[0], mean_angle[-1]+25, 'wide below, steep above')
        self.assertGreater(mean_angle[0], 70)
        self.assertLess(mean_angle[-1], 60)

    def test_a_low_limb_sags_and_every_limb_lifts(self):
        limbs = [a for a in self.skeleton if a['order'] == 1]
        lowest = [a for a in limbs if a['at'] == 1]
        self.assertTrue(any(min(p[1] for p in a['points']) < a['points'][0][1]-.005 for a in lowest),
                        'a limb of the lowest flush dips under its own base')
        for a in limbs:
            ys = [p[1] for p in a['points']]
            self.assertGreater(ys[-1], min(ys), 'a limb ends higher than its lowest point')
            self.assertGreater(ys[-1], ys[0], 'and higher than it began')
            self.assertEqual(len(ys), self.sheet['orders'][0]['segments']+1)
        top = [a for a in limbs if a['at'] == max(x['at'] for x in limbs)]
        for a in top:
            self.assertGreater(a['points'][-1][1]-a['points'][0][1], .2, 'the top flush climbs: a forked crown')

    def test_stems_are_crooked(self):
        """An oak's limbs zigzag: no limb is a straight line."""
        for a in (a for a in self.skeleton if a['order'] == 1):
            turns = [angle_from([b[i]-a_[i] for i in range(3)], [c[i]-b[i] for i in range(3)])
                     for a_, b, c in zip(a['points'], a['points'][1:], a['points'][2:])]
            self.assertGreater(max(turns), 8.)

    def test_thickness_is_the_pipe_model(self):
        """Every leaf-bearing tip sends a strand to the root, so at a fork the areas add (Leonardo's rule, to the
        sheet's exponent), no stem thickens toward its tip, and the bole carries the whole crown."""
        tree = grow.grow(self.sheet)
        make.fit(tree)
        stems = tree.alive()
        exponent, twig = self.sheet['pipe_exponent'], self.sheet['twig_radius_m']
        tips = [a for a in stems if a.bears]
        self.assertEqual(stems[0].pipes[0], len(stems)+sum(len(a.light) for a in tips))
        self.assertEqual(stems[0].radii[0], self.sheet['trunk']['radius_m'])
        forks = 0
        for a in stems:
            for i in range(len(a.pts)-1):
                self.assertGreaterEqual(a.radii[i], a.radii[i+1])
                children = [tree.axes[c] for c in a.children if tree.axes[c].at == i]
                if not children or min([a.radii[i+1]]+[c.radii[0] for c in children]) <= twig:
                    continue
                forks += 1
                whole = a.radii[i]**exponent
                parts = a.radii[i+1]**exponent+sum(c.radii[0]**exponent for c in children)
                self.assertAlmostEqual(parts/whole, 1., delta=1e-9)
        self.assertGreater(forks, 15)
        for a in self.skeleton:   # and as pinned
            radii = [p[3] for p in a['points']]
            self.assertEqual(radii, sorted(radii, reverse=True))
            self.assertEqual(radii[-1], 0., 'a stem ends in a point')
        limbs = [a['points'][0][3] for a in self.skeleton if a['order'] == 1]
        twigs = [a['points'][0][3] for a in self.skeleton if a['order'] == 3]
        self.assertGreater(min(limbs), max(twigs), 'the thinnest limb is thicker than the thickest twig')
        self.assertGreater(sorted(limbs)[len(limbs)//2], 2*sorted(twigs)[len(twigs)//2], 'and limbs are far thicker')

    def test_the_crown_is_broad_lobed_and_stands_clear(self):
        size = self.record['size_m']
        leaves = part(self.record, 0, 'foliage')['positions']
        floor = min(p[1] for p in leaves)
        self.assertGreater((size[0]+size[2])/2, size[1], 'an oak in the open is broader than it is tall')
        self.assertGreater(min(size[0], size[2]), 1.2*(size[1]-floor), 'and its crown broader than deep')
        reach = level_reach(self.record)
        self.assertGreater(min(reach), .35, 'leaves all the way round')
        self.assertGreater(max(reach)/min(reach), 1.15, 'an outline that swells and dips, not a lathe')
        self.assertGreater(floor, 2*BODY_CLEARANCE_M-.02, 'the lowest leaves hang two bodies clear at full size')
        self.assertGreater(floor, .6*self.record['collision']['trunk_top_m'], 'no leaves down the bole')
        widest = max(leaves, key=lambda p: math.hypot(p[0]-self.axis[0], p[2]-self.axis[1]))
        self.assertLess(widest[1], floor+.6*(size[1]-floor), 'widest below its middle: a dome, not a ball on a stick')

    def test_the_crown_is_bare_inside(self):
        """Leaves live in the crown's shell, where the light is: looking up from the bole a body sees the limbs."""
        leaves = part(self.record, 0, 'foliage')
        centre, top = self.record['crown_m'], self.record['size_m'][1]
        inside = [p for p in leaves['positions']
                  if math.hypot(p[0]-self.axis[0], p[2]-self.axis[1]) < .2 and p[1] < centre[1]+.1]
        self.assertLess(len(inside), .05*len(leaves['positions']))
        self.assertLess(self.skeleton[0]['points'][-1][1], .65*top, 'the trunk gives way to limbs: no pole to the top')

    def test_leaves_hang_from_twigs(self):
        """Every clump names the twig tip it hangs from, so winter can draw the leaves in and bare the branching."""
        tips = {tuple(a['points'][-1][:3]) for a in self.skeleton[1:]}
        for name in OAKS:
            record = flora.load(name)
            for level in range(3):
                leaves = part(record, level, 'foliage')
                self.assertEqual(sorted(set(leaves['clump'])), list(range(len(leaves['twigs_m']))))
                for twig in leaves['twigs_m']:
                    self.assertIn(tuple(twig), {tuple(a['points'][-1][:3]) for a in record['skeleton'][1:]})
        leaves = part(self.record, 0, 'foliage')
        self.assertEqual(len(leaves['twigs_m']), self.record['counts']['clumps'])
        self.assertTrue({tuple(t) for t in leaves['twigs_m']} <= tips)
        for p, k in zip(leaves['positions'], leaves['clump']):
            self.assertLess(math.dist(p, leaves['twigs_m'][k]), .45, 'a leaf is near its twig')

    def test_leaves_are_lit_above_and_dark_beneath(self):
        leaves = part(self.record, 0, 'foliage')
        for k in range(self.record['counts']['clumps']):
            corners = sorted((p[1], t[1]) for p, t, c in zip(leaves['positions'], leaves['tints'], leaves['clump'])
                             if c == k)
            self.assertGreater(corners[-1][1], corners[0][1]+.15, "a clump's top corner is lighter than its lowest")
            self.assertGreater(sum(g for _, g in corners[6:])/6, sum(g for _, g in corners[:6])/6+.1)
        self.assertTrue(all(0 < v <= 1 for t in leaves['tints'] for v in t))
        flat = built('oak', 'clumps', change={'leaves': {'shade': 0}})
        greens = {round(t[1], 1) for t in part(flat, 0, 'foliage')['tints']}
        self.assertEqual(greens, {.9, 1.}, 'shade 0 switches the shading off: only the clumps differ a little')

    def test_skins_face_outward(self):
        """Front faces wind counter-clockwise from outside, as the kit's do: a clump's solid holds a volume, and a
        stem's skin faces away from its own axis."""
        for solid in (shape.ICO, shape.OCTA):
            corners, faces = solid
            volume = sum(det.dot(corners[a], det.cross(corners[b], corners[c])) for a, b, c in faces)/6
            self.assertGreater(volume, .5)
            edges = {}
            for f in faces:
                for a, b in zip(f, f[1:]+f[:1]):
                    edges[(a, b)] = edges.get((a, b), 0)+1
            self.assertTrue(all(edges.get((b, a)) == 1 and n == 1 for (a, b), n in edges.items()), 'closed')
        pts = [(0., 0., 0.), (.1, .5, 0.), (.3, .9, .2), (.3, 1.2, .3)]
        positions, triangles = shape.tube(pts, [.1, .08, .05, 0.], 6)
        self.assertEqual(len(triangles), shape.tube_triangles(len(pts), 6))
        for f in triangles:
            a, b, c = (positions[i] for i in f)
            normal = det.cross(det.sub(b, a), det.sub(c, a))
            middle = det.mul(det.add(det.add(a, b), c), 1/3)
            nearest = min(pts, key=lambda p: det.length(det.sub(p, middle)))
            self.assertGreater(det.dot(normal, det.sub(middle, nearest)), 0)
        leaves = part(self.record, 0, 'foliage')
        for k in range(self.record['counts']['clumps']):
            faces = [f for f in leaves['triangles'] if leaves['clump'][f[0]] == k]
            self.assertEqual(len(faces), make.BLOB_TRIANGLES)
            volume = sum(det.dot(leaves['positions'][a], det.cross(leaves['positions'][b], leaves['positions'][c]))
                         for a, b, c in faces)
            self.assertGreater(volume, 0)
        far = part(self.record, 2, 'foliage')   # the far lantern: one closed skin, facing out
        volume = sum(det.dot(far['positions'][a], det.cross(far['positions'][b], far['positions'][c]))
                     for a, b, c in far['triangles'])/6
        self.assertGreater(volume, .2)
        edges = {}
        for f in far['triangles']:
            for a, b in zip(f, f[1:]+f[:1]):
                edges[(a, b)] = edges.get((a, b), 0)+1
        self.assertTrue(all(edges.get((b, a)) == 1 and n == 1 for (a, b), n in edges.items()), 'the lantern is closed')


class OneOakTwoWays(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.clumps, cls.cards = flora.load('oak_clumps'), flora.load('oak_cards')

    @staticmethod
    def from_trunk(record, point):
        """A point as whole twentieths of a millimetre from the trunk's foot (each plant stands on its own pivot)."""
        ax, az = record['collision']['axis_m']
        return (round((point[0]-ax)*20000), round(point[1]*20000), round((point[2]-az)*20000))

    def test_it_is_one_tree(self):
        self.assertEqual(self.clumps['maker'], self.cards['maker'])
        self.assertEqual(self.clumps['counts'], self.cards['counts'])
        self.assertEqual((self.clumps['leaf_style'], self.cards['leaf_style']), ('clumps', 'cards'))
        for level in range(3):
            a, b = part(self.clumps, level, 'bark'), part(self.cards, level, 'bark')
            self.assertEqual(a['triangles'], b['triangles'])
            self.assertEqual(a['tints'], b['tints'])
            self.assertEqual([self.from_trunk(self.clumps, p) for p in a['positions']],
                             [self.from_trunk(self.cards, p) for p in b['positions']], f'bark of level {level}')
        for a, b in zip(self.clumps['skeleton'], self.cards['skeleton']):
            self.assertEqual([self.from_trunk(self.clumps, p) for p in a['points']],
                             [self.from_trunk(self.cards, p) for p in b['points']])
        self.assertEqual([self.from_trunk(self.clumps, t) for t in part(self.clumps, 0, 'foliage')['twigs_m']],
                         [self.from_trunk(self.cards, t) for t in part(self.cards, 0, 'foliage')['twigs_m']])

    def test_it_climbs_the_same(self):
        a, b = self.clumps['collision'], self.cards['collision']
        for key in ('trunk_radius_m', 'pole_radius_m', 'trunk_top_m', 'pole_top_m', 'clearance_m'):
            self.assertEqual(a[key], b[key], key)
        self.assertEqual(len(a['caps']), len(b['caps']))
        for one, other in zip(a['caps'], b['caps']):
            self.assertEqual(one[2:], other[2:])
            self.assertEqual(self.from_trunk(self.clumps, (one[0], 0, one[1])),
                             self.from_trunk(self.cards, (other[0], 0, other[1])))

    def test_clumps_are_solids_and_cards_are_sheets(self):
        solid, sheets = part(self.clumps, 0, 'foliage'), part(self.cards, 0, 'foliage')
        self.assertNotIn('uvs', solid)
        self.assertEqual(len(solid['positions']), 12*self.clumps['counts']['clumps'])
        per_clump = Sheet.load('oak')['leaves']['cards']
        self.assertEqual(len(sheets['positions']), 4*per_clump*self.cards['counts']['clumps'])
        self.assertEqual(len(sheets['triangles']), 2*per_clump*self.cards['counts']['clumps'])
        for k in range(0, len(sheets['positions']), 4):
            self.assertEqual(sheets['uvs'][k:k+4], [[0., 0.], [1., 0.], [1., 1.], [0., 1.]])
            a, b, c, d = sheets['positions'][k:k+4]
            for i in range(3):
                self.assertAlmostEqual(a[i]+c[i], b[i]+d[i], delta=3e-4, msg='a card is a flat four-cornered sheet')
        self.assertLess(self.cards['levels'][0]['triangle_count'], self.clumps['levels'][0]['triangle_count'])
        self.assertEqual(self.cards['levels'][2]['triangle_count']-len(part(self.cards, 2, 'bark')['triangles']), 6,
                         'far off, three crossed cards')


class Climbing(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.record = flora.load('oak_clumps')
        cls.collision = cls.record['collision']

    def test_the_contracts_rules_for_a_tree_row(self):
        """contracts/validate.py, a tree row: radii positive; base < trunk top < pole top; every cap's perch no
        wider than its rim and its rim between the base and its top; the pole under the highest cap."""
        c = self.collision
        self.assertTrue(c['trunk_radius_m'] >= c['pole_radius_m'] > 0)
        self.assertTrue(0 < c['trunk_top_m'] < c['pole_top_m'] <= self.record['size_m'][1])
        for x, z, rim_x, rim_z, rim, top, top_x, top_z in c['caps']:
            self.assertTrue(0 < top_x <= rim_x and 0 < top_z <= rim_z)
            self.assertTrue(0 < rim < top <= self.record['size_m'][1])
            self.assertEqual((rim_x, top_x), (rim_z, top_z), 'round: a copy may be turned any way')
        self.assertLessEqual(c['pole_top_m'], max(cap[5] for cap in c['caps']))
        self.assertEqual(c['trunk_top_m'], self.record['skeleton'][0]['points'][1][1], 'the trunk ends at the first limbs')
        self.assertEqual(c['pole_radius_m'], self.record['skeleton'][0]['points'][1][3])

    def test_the_climber_comes_up_through_the_first_cap(self):
        c = self.collision
        over = c['caps'][0]
        self.assertEqual(over[:2], c['axis_m'])
        self.assertAlmostEqual(over[5]-c['pole_top_m'], make.POLE_TOP_GAP_M, delta=2e-4)
        self.assertGreaterEqual(over[6], c['pole_radius_m']+make.PERCH_MARGIN_M-1e-4, 'room to stand round the pole')

    def test_every_perch_is_the_top_of_the_drawn_leaves(self):
        leaves = part(self.record, 0, 'foliage')
        for x, z, _, _, rim, top, perch, _ in self.collision['caps']:
            drawn = shape.top_hit(leaves['positions'], leaves['triangles'], x, z)
            self.assertIsNotNone(drawn)
            self.assertAlmostEqual(drawn-top, make.TOP_INSET_M, delta=5e-4, msg='a body stands on leaves, not in air')
            self.assertGreaterEqual(perch, self.collision['pole_radius_m']+make.PERCH_MARGIN_M-1e-4)
            self.assertGreater(top-rim, .019)

    def test_a_hall_under_the_low_limbs(self):
        """An oak is a hall: a body walks under every cap of the smallest tree a planter sets."""
        self.assertEqual(self.collision['clearance_m'], min(cap[4] for cap in self.collision['caps']))
        self.assertGreater(self.collision['clearance_m']*SMALLEST_SCALE, 2*BODY_CLEARANCE_M)
        self.assertGreater(self.collision['trunk_top_m']*SMALLEST_SCALE, .1, 'the first limbs are over a body')

    def test_perches_do_not_run_into_each_other(self):
        caps = self.collision['caps']
        for i, a in enumerate(caps):
            for b in caps[i+1:]:
                self.assertGreaterEqual(math.hypot(a[0]-b[0], a[1]-b[1]), a[6]+b[6]-2e-4, 'two perches never overlap')

    def test_a_copy_is_set_down_as_the_exporter_sets_a_mesh(self):
        scale, position, yaw = (.6, .7, .6), (12.5, 3.25, -40.), 73.
        placed = flora.placed('oak_clumps', scale, position, yaw)
        c = self.collision

        def moved(x, y, z):
            return export_mesh.transform([dict(role='bark', positions=[[x, y, z]], triangles=[])],
                                         scale, position, yaw)[0]['positions'][0]
        ax = moved(c['axis_m'][0], 0, c['axis_m'][1])
        self.assertAlmostEqual(placed['axis'][0], ax[0], delta=1e-9)
        self.assertAlmostEqual(placed['axis'][1], ax[2], delta=1e-9)
        self.assertAlmostEqual(placed['trunk_top'], position[1]+c['trunk_top_m']*.7, delta=1e-9)
        for cap, got in zip(c['caps'], placed['caps']):
            want = moved(cap[0], cap[5], cap[1])
            self.assertAlmostEqual(got[0], want[0], delta=1e-9)
            self.assertAlmostEqual(got[1], want[2], delta=1e-9)
            self.assertAlmostEqual(got[5], want[1], delta=1e-9)
            self.assertAlmostEqual(got[2], cap[2]*.6, delta=1e-9)
        x, z = flora.stand('oak_clumps', 5., -2., scale, yaw)
        there = flora.placed('oak_clumps', scale, (x, 0., z), yaw)['axis']
        self.assertAlmostEqual(there[0], 5., delta=1e-9)
        self.assertAlmostEqual(there[1], -2., delta=1e-9)


class FitsThePipeline(unittest.TestCase):
    def test_a_prototype_keeps_the_harness_rule(self):
        """harness/package.py: a prototype's pivot is its bottom centre and its size its bounds, within 1e-5, on
        the 32-bit floats a GLB holds."""
        for name in OAKS:
            parts, size = flora.prototype(name)
            vs = [struct.unpack('<3f', struct.pack('<3f', *v)) for p in parts for v in p['positions']]
            lo = [min(v[i] for v in vs) for i in range(3)]
            hi = [max(v[i] for v in vs) for i in range(3)]
            self.assertTrue(abs(lo[1]) < 1e-5 and abs(lo[0]+hi[0]) < 1e-5 and abs(lo[2]+hi[2]) < 1e-5, name)
            self.assertTrue(all(abs(hi[i]-lo[i]-size[i]) < 1e-5 for i in range(3)), name)
            harness_token(name)
            self.assertNotIn(name, kit.SIZES, 'a kit prototype is never shadowed')
            for level in flora.levels(name):
                harness_glb.check_primitives(level)   # the package format's own rules for a mesh
            self.assertEqual(harness_glb.decode_mesh(harness_glb.encode_mesh(parts))[0]['triangles'],
                             parts[0]['triangles'], 'and through a package GLB and back')

    def test_every_level_is_a_tree_the_exporter_can_write(self):
        for name in OAKS:
            for k, parts in enumerate(flora.levels(name)):
                self.assertTrue(export_trees.is_tree(name, parts))
                self.assertEqual({p['role'] for p in parts}, {'bark', 'foliage'})
                glb = export_mesh.encode(parts)
                size = struct.unpack_from('<I', glb, 12)[0]
                doc = json.loads(glb[20:20+size])
                drawn = sum(doc['accessors'][p['indices']]['count'] for p in doc['meshes'][0]['primitives'])//3
                self.assertEqual(drawn, flora.load(name)['levels'][k]['triangle_count'])
                self.assertTrue(all(len(t) == 4 for p in parts for t in p['tints']))

    def test_the_fixture_key_sees_the_pinned_plants(self):
        """tools/landscape-fixture.ps1 hashes .py and .json under the generator, but nothing under a tests folder:
        the pinned plants and the sheets are in, the test sheets are out (RUN-4.md section 4)."""
        for path in (pinned.PINNED/'oak_clumps.json', Sheet.SHEETS/'oak.json'):
            self.assertTrue(path.is_file() and '/tests/' not in path.as_posix() and path.suffix == '.json')
        self.assertIn('/tests/', (TEST_SHEETS/'spire.json').as_posix())


class TheCheckRefuses(unittest.TestCase):
    """Five slips the pinned check must catch (pinned.check runs on every load and at the end of every build)."""

    def broken(self, change):
        record = copy.deepcopy(flora.load('oak_clumps'))
        change(record)
        with self.assertRaises(ValueError):
            pinned.check(record)

    def test_a_ninth_cap(self):
        self.broken(lambda r: r['collision']['caps'].extend([list(r['collision']['caps'][1])]*8))

    def test_a_cap_that_is_not_round(self):
        self.broken(lambda r: r['collision']['caps'][1].__setitem__(2, r['collision']['caps'][1][2]+.01))

    def test_a_pole_over_every_cap(self):
        self.broken(lambda r: r['collision'].update(pole_top_m=r['size_m'][1]))

    def test_a_level_that_is_not_lighter(self):
        self.broken(lambda r: r['levels'].__setitem__(1, copy.deepcopy(r['levels'][0])))

    def test_a_triangle_past_its_part(self):
        self.broken(lambda r: part(r, 0, 'bark')['triangles'][0].__setitem__(0, 10**6))

    def test_and_passes_what_is_sound(self):
        for name in OAKS:
            pinned.check(copy.deepcopy(flora.load(name)))


class ASecondSheet(unittest.TestCase):
    """A made-up tree with one leader and flat tiers (Massart's model), nothing like an oak: the maker is its
    sheets' servant. It is a test sheet, not a species of the game, and nothing of it is pinned."""

    @classmethod
    def setUpClass(cls):
        cls.spire = built('spire', 'clumps', TEST_SHEETS)
        cls.sheet = Sheet.load('spire', TEST_SHEETS)

    def test_it_builds_within_its_own_caps_both_ways(self):
        for style in Sheet.STYLES:
            record = built('spire', style, TEST_SHEETS)
            for level, cap in zip(record['levels'], self.sheet['levels']):
                self.assertLessEqual(level['triangle_count'], cap)
            self.assertTrue(1 <= len(record['collision']['caps']) <= self.sheet['climb']['caps'])
            self.assertEqual(pinned.pack(record),
                             pinned.pack(make.build(Sheet.load('spire', TEST_SHEETS), style, f'spire_{style}')))

    def test_it_is_a_spire_not_an_oak(self):
        size = self.spire['size_m']
        self.assertGreater(size[1], 2*max(size[0], size[2]), 'taller than wide')
        trunk = self.spire['skeleton'][0]['points']
        self.assertGreater(trunk[-1][1], .9*size[1], 'one leader to the top')
        limbs = [a for a in self.spire['skeleton'] if a['order'] == 1]
        self.assertGreaterEqual(len({a['at'] for a in limbs}), 3, 'tiers')
        for a in limbs:
            run = math.dist(a['points'][0][:3], a['points'][-1][:3])
            self.assertLess(abs(a['points'][-1][1]-a['points'][0][1])/run, .45, 'limbs held level')
        oak = flora.load('oak_clumps')['size_m']
        self.assertGreater((oak[0]+oak[2])/oak[1], 2.5*(size[0]+size[2])/size[1])

    def test_the_far_level_keeps_its_outline(self):
        """The far crown is a lantern drawn round the finest leaves: a spire stays a spire and an oak a dome."""
        for record, taller in ((self.spire, True), (flora.load('oak_clumps'), False)):
            far = part(record, 2, 'foliage')['positions']
            wide = max(max(p[0] for p in far)-min(p[0] for p in far), max(p[2] for p in far)-min(p[2] for p in far))
            tall = max(p[1] for p in far)-min(p[1] for p in far)
            self.assertEqual(tall > 1.5*wide, taller)

    def test_a_flagged_crown_leans_downwind(self):
        """crown.flag (0 for the oak): the crown leans toward +X as it rises, for the rim's wind-flagged trees."""
        def lean(record):
            leaves = part(record, 0, 'foliage')['positions']
            top = sorted(leaves, key=lambda p: p[1])[-len(leaves)//3:]
            return sum(p[0] for p in top)/len(top)-record['collision']['axis_m'][0]
        flagged = built('spire', 'clumps', TEST_SHEETS, {'crown': {'flag': .8}})
        self.assertGreater(lean(flagged), lean(self.spire)+.05)

    def test_a_based_sheet_builds(self):
        old = built('old_spire', 'clumps', TEST_SHEETS)
        self.assertEqual(old['skeleton'][0]['points'][0][3], .045)
        self.assertLessEqual(old['counts']['limbs'], 9)
        self.assertNotEqual(old['maker']['sheet_sha256'], self.spire['maker']['sheet_sha256'])


if __name__ == '__main__':
    unittest.main()
