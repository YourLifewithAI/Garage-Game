"""Determinism and principle checks for the landscape generator.

    python -B -m unittest pipeline.landscape.generator.tests.test_generator -v

Named whole like this, the module brings the rim's tests with it (`load_tests`, below).
"""
import math
import os
import shutil
import tempfile
import uuid
import unittest
from pathlib import Path

from pipeline.landscape.generator import checks
from pipeline.landscape.generator.generate import generate

ROOT = Path(__file__).resolve().parents[4]
ROOMS = ROOT/'pipeline'/'landscape'/'corpus'/'rooms'


# One folder per run, so two overlapping runs (two lanes, or a lane and the
# integrator's suite) never share or delete each other's packages. Plain
# os.makedirs with a unique name: tempfile.mkdtemp's owner-only ACL blocks
# sandboxed writes on Windows.
RUN_DIR = os.path.join(tempfile.gettempdir(), 'landscape_generator_tests-%d-%s' % (os.getpid(), uuid.uuid4().hex[:12]))


def workdir(name):
    path = os.path.join(RUN_DIR, name)
    shutil.rmtree(path, ignore_errors=True)
    os.makedirs(path)
    return path


_GARAGE = {}


def garage():
    """The garage's package, generated once for every test that only reads it."""
    if 'path' not in _GARAGE:
        _GARAGE['path'] = os.path.join(workdir('a'), 'pkg')
        generate(ROOMS/'garage_nominal', _GARAGE['path'])
    return _GARAGE['path']


def tearDownModule():
    shutil.rmtree(RUN_DIR, ignore_errors=True)


def load_tests(loader, tests, pattern):
    """The rim's tests (test_rim, Run 4's WORLD-2) run with these when this module is named whole and theirs is not:
    the runners' line names this module and test_firepits (tools/linux/test-all.sh), and a land made with
    `--edge rim` is the same generator. Named beside it on the command line, or found by discovery, test_rim runs on
    its own and nothing is added here; a single class or test of this module named alone brings nothing with it."""
    import sys
    if pattern is None and not any('test_rim' in arg for arg in sys.argv):
        from pipeline.landscape.generator.tests import test_rim
        tests.addTests(loader.loadTestsFromModule(test_rim))
    return tests


class RunnersLine(unittest.TestCase):
    def test_this_module_named_whole_brings_the_rim_s_tests(self):
        """Named beside it, or found by discovery, it adds nothing: no test runs twice."""
        from unittest import mock
        here = 'pipeline.landscape.generator.tests.'

        def brought(argv, pattern=None):
            with mock.patch('sys.argv', ['unittest']+argv):
                suite = load_tests(unittest.TestLoader(), unittest.TestSuite(), pattern)
            return {type(t).__module__ for group in suite for t in group}, suite.countTestCases()
        modules, count = brought([here+'test_generator', here+'test_firepits'])
        self.assertEqual(modules, {here+'test_rim'})
        self.assertGreater(count, 30)
        self.assertEqual(brought([here+'test_generator', here+'test_rim'])[1], 0)
        self.assertEqual(brought([here+'test_generator', here+'test_rim.StandIn'])[1], 0)
        self.assertEqual(brought([here+'test_generator'], pattern='test*.py')[1], 0)


def tree(folder):
    return {p.relative_to(folder).as_posix(): p.read_bytes() for p in sorted(Path(folder).rglob('*')) if p.is_file()}


class GarageLandscape(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.room = ROOMS/'garage_nominal'
        cls.a = garage()
        cls.b = os.path.join(workdir('b'), 'pkg')
        generate(cls.room, cls.b)
        cls.result = checks.run(cls.a, cls.room)

    def test_identical_bytes(self):
        first, second = tree(self.a), tree(self.b)
        self.assertEqual(sorted(first), sorted(second))
        for name in first:
            self.assertEqual(first[name], second[name], name)

    def test_water_level_and_downhill(self):
        self.assertTrue(self.result['water_ok'], self.result['water'])
        kinds = {w['kind'] for w in self.result['water']}
        self.assertIn('still', kinds)
        self.assertIn('flowing', kinds)

    def test_nothing_floating_or_sunk(self):
        self.assertTrue(self.result['grounded_ok'], [g for g in self.result['grounding'] if not g['ok']][:5])

    def test_walk_to_object_and_back(self):
        self.assertTrue(self.result['walk_ok'], self.result['walks'])
        for w in self.result['walks']:
            self.assertLessEqual(w['max_face_slope_deg'], checks.WALK_DEG)
            self.assertLessEqual(w['max_step_m'], checks.STEP_M)

    def test_every_footprint_reads(self):
        self.assertTrue(self.result['footprints_ok'], [f for f in self.result['footprints'] if not f['reads']])
        self.assertEqual(len(self.result['footprints']), 16)

    def test_pond_deep_enough_to_swim_with_a_shore_to_walk_out(self):
        """The founder's playtest pond, judged from the written package: a deep
        middle well over the 10 cm head, a shelving shore a swimmer can walk
        out up (no step on the way steeper than 35 degrees from water over the
        head to the dry shore), and a drop-off somewhere (a face over 45)."""
        from pipeline.landscape.harness import read_package
        doc, meshes, _, _ = read_package(self.a, self.room)
        terrain = checks.Terrain(doc, meshes)
        tarn = meshes['tarn'][0]
        level = tarn['positions'][0][1]
        xs = [v[0] for v in tarn['positions']]
        zs = [v[2] for v in tarn['positions']]
        c = .025
        cells = {}
        for i in range(int(min(xs)/c)-2, int(max(xs)/c)+3):
            for j in range(int(min(zs)/c)-2, int(max(zs)/c)+3):
                cells[i, j] = level-terrain.height(i*c, j*c)
        deepest = max(cells.values())
        self.assertGreaterEqual(deepest, .15)
        self.assertLessEqual(deepest, .30)

        def grade(a, b):
            run = c*math.hypot(a[0]-b[0], a[1]-b[1])
            return math.degrees(math.atan(abs(cells[a]-cells[b])/run))
        steps = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)]
        seen = {k for k, d in cells.items() if d >= .11}
        todo = list(seen)
        shore = False
        while todo and not shore:
            k = todo.pop()
            for di, dj in steps:
                n = (k[0]+di, k[1]+dj)
                if n in cells and n not in seen and grade(k, n) <= 35:
                    if cells[n] <= 0:
                        shore = True
                        break
                    seen.add(n)
                    todo.append(n)
        self.assertTrue(shore, 'no shelving way out of the deep water')
        wet = [k for k, d in cells.items() if d > .02]
        steepest = max(grade(k, (k[0]+di, k[1]+dj)) for k in wet for di, dj in steps[:4] if (k[0]+di, k[1]+dj) in cells)
        self.assertGreater(steepest, 45)

    def test_island_in_the_sea(self):
        """The founder's island: a sea below the land all round, out past a reef
        a short swim off the coast; beaches a swimmer walks out of the sea up and
        on to the spawn; the door a jetty reachable on foot."""
        self.assertTrue(self.result['sea_ok'], {k: v for k, v in self.result['sea'].items()})
        from pipeline.landscape.harness import read_package
        doc, meshes, room, _ = read_package(self.a, self.room)
        sea = doc['x_generator']['sea']
        self.assertLess(sea['level_m'], 0)
        self.assertEqual({'mesh': 'sea', 'kind': 'still'}, next(r for r in doc['water'] if r['mesh'] == 'sea'))
        self.assertGreaterEqual(sea['reef']['offshore_m'][0], .2)
        self.assertLessEqual(sea['reef']['offshore_m'][1], 1.)
        self.assertGreaterEqual(len(sea['beaches']), 1)
        self.assertEqual(sea['jetty']['door_id'], 'entry')
        self.assertIn({'mesh': 'jetty'}, doc['terrain'])
        # The playable water reaches past the room's walls on every side.
        lo, hi = room['bounds']['min_m'], room['bounds']['max_m']
        b = sea['play_area']['bounds_m']
        self.assertTrue(b['min_m'][0] < lo[0]-.5 and b['min_m'][2] < lo[2]-.5 and b['max_m'][0] > hi[0]+.5
                        and b['max_m'][2] > hi[2]+.5, b)
        self.assertLess(b['min_m'][1], sea['level_m']-.3)
        # A ragged coast: cliffs (faces over 45 degrees, climbable) and beaches (sand under 20) both meet the sea.
        terrain = checks.Terrain(doc, meshes)
        coast = sea['coast']['outline_m']
        cliffs = beaches = 0
        for x, z in coast:
            slopes = [terrain.hit(x+dx, z+dz)[1] for dx in (-.03, 0, .03) for dz in (-.03, 0, .03)]
            cliffs += max(slopes) > 45
            beaches += max(slopes) < 20
        self.assertGreater(cliffs, 5)
        self.assertGreater(beaches, 5)

    def test_clean_coast(self):
        """The founder, 10 October: beaches with smooth gradual transitions into
        deep ocean, sheer cliffs plummeting deep into it, and no walls offshore:
        no reef rocks or stacks near the surface, no distant islands."""
        from pipeline.landscape.harness import read_package
        doc, meshes, room, _ = read_package(self.a, self.room)
        sea = doc['x_generator']['sea']
        level = sea['level_m']
        off = self.result['sea']['offshore']
        self.assertEqual(off['walls'], 0, off['worst'])
        self.assertEqual(sea['stacks'], [])
        self.assertEqual(sea['distant_islands'], [])
        self.assertLess(max(v[1] for p in meshes['distant_islands'] for v in p['positions']), level-.6)
        # The reef line is the shelf's edge under water, not rock near the surface.
        self.assertLessEqual(sea['reef']['crest_y_m'], level-.2)
        # Cliffs drop into deep water: a short way off their faces it is over half a metre deep.
        self.assertGreater(off['cliff_feet'], 20)
        self.assertGreater(off['cliff_foot_depth_m'][1], .5)
        # Each promised beach shelves from dry sand into deep water without rising again.
        land = checks.Terrain(dict(doc, terrain=[{'mesh': 'land'}]), meshes)
        for b in sea['beaches']:
            (bx, _, bz), (wx, _, wz) = b['wash_ashore_m'], b['water_m']
            L = math.hypot(wx-bx, wz-bz)
            ux, uz = (wx-bx)/L, (wz-bz)/L
            ys = [land.height(bx+ux*k*.01, bz+uz*k*.01) for k in range(400)]
            deep = next(k for k, y in enumerate(ys) if level-y >= .6)
            rises = max(y-min(ys[:k+1]) for k, y in enumerate(ys[:deep+1]))
            steepest = max(math.degrees(math.atan(abs(b_-a_)/.01)) for a_, b_ in zip(ys[:deep], ys[1:deep+1]))
            self.assertLess(rises, .005, b['id'])
            self.assertLess(steepest, 30, b['id'])


class WalkFlood(unittest.TestCase):
    """One flood from the spawn answers every walk goal, and the shortcuts that
    make it island-sized (narrowed candidate lists, plain land vouched for)
    never change an answer: on the garage's package the flood reaches exactly
    the nodes the walk reaches when every sample is read in full, and the
    shortest-way search finds a way exactly where the flood says one exists."""

    @classmethod
    def setUpClass(cls):
        from pipeline.landscape.harness import read_package
        cls.room_dir = ROOMS/'garage_nominal'
        cls.doc, cls.meshes, cls.room, _ = read_package(garage(), cls.room_dir)
        player = next(s for s in cls.room['spawns'] if s['role'] == 'player')['position_m']
        cls.start = (player[0], player[2])

    def full_reading(self):
        """A Terrain that reads every sample in full: its bucket's whole list, nothing vouched for."""
        terrain = checks.Terrain(self.doc, self.meshes)
        b = terrain.bucket
        terrain.candidates = lambda x, z: terrain.grid.get((int(math.floor(x/b)), int(math.floor(z/b))), ())
        terrain.plain_slope = lambda i, j: None
        return terrain

    def test_flood_matches_the_full_reading(self):
        fast = checks.Walker(self.doc, self.meshes, checks.Terrain(self.doc, self.meshes), self.room)
        slow = checks.Walker(self.doc, self.meshes, self.full_reading(), self.room)
        a, b = fast.reach(self.start), slow.reach(self.start)
        self.assertGreater(len(a), 5000)
        self.assertEqual(a.nodes(), b.nodes())
        # The vouched-for ground is most of it (otherwise the shortcut is idle).
        plain = sum(1 for v in fast.t._plain.values() if v is not None and v <= checks.WALK_DEG)
        self.assertGreater(plain, 1000)
        # Every reached node's way back is a walk the full reading passes.
        c = fast.t.cell
        far = max(a.nodes(), key=lambda k: (len(a.path((k[0]*c, k[1]*c))), k))
        path = a.path((far[0]*c, far[1]*c))
        self.assertGreater(len(path), 50)
        self.assertTrue(checks.proves(slow, path))
        info = slow.measure(path)
        self.assertLessEqual(info['max_face_slope_deg'], checks.WALK_DEG)
        self.assertLessEqual(info['max_step_m'], checks.STEP_M)

    def test_hits_match_the_whole_bucket(self):
        import random
        fast, slow = checks.Terrain(self.doc, self.meshes), self.full_reading()
        rnd = random.Random(5)
        lo, hi = self.room['bounds']['min_m'], self.room['bounds']['max_m']
        pts = [(rnd.uniform(lo[0]-1, hi[0]+1), rnd.uniform(lo[2]-1, hi[2]+1)) for _ in range(20000)]
        # Points on the grid's own lines and corners, where faces meet.
        pts += [(round(-4.7+.03*i, 6), round(-5.2+.03*j, 6)) for i in range(0, 300, 7) for j in range(0, 340, 7)]
        for x, z in pts:
            self.assertEqual(fast.hit(x, z), slow.hit(x, z), (x, z))
        # Heights read straight from the land grid's cell where it is plain.
        fresh = checks.Terrain(self.doc, self.meshes)
        for x, z in pts:
            a, b = fresh.land_height(x, z), slow.height(x, z)
            self.assertTrue(a == b or (math.isnan(a) and math.isnan(b)), (x, z, a, b))
        self.assertGreater(len(fresh._whole), 1000)

    def test_shortest_way_found_exactly_where_the_flood_reaches(self):
        walker = checks.Walker(self.doc, self.meshes, checks.Terrain(self.doc, self.meshes), self.room)
        reach = walker.reach(self.start)
        c = walker.t.cell
        nodes = sorted(reach.nodes())
        goals = [(k[0]*c, k[1]*c) for k in nodes[::max(1, len(nodes)//6)]]
        # And some it does not reach: under the tarn, on a landform's face.
        tarn = self.meshes['tarn'][0]['positions']
        goals += [(sum(v[0] for v in tarn)/len(tarn), sum(v[2] for v in tarn)/len(tarn))]
        goals += [(o['position_m'][0], o['position_m'][2]) for o in self.doc['scatter'] if o['prototype'] == 'cottage']
        for g in goals:
            self.assertEqual(walker.route(self.start, g)[0] is not None, reach.has(g), g)

    def test_written_meshes_read_in_single_precision(self):
        """The generator's beach check reads its meshes as the package will
        hold them, without writing it: exactly what the harness reads back."""
        from pipeline.landscape.generator.generate import as_written
        from pipeline.landscape.harness.glb import decode_mesh, encode_mesh
        import random
        rnd = random.Random(3)
        pos = [[rnd.uniform(-40, 40), rnd.uniform(-1, 3), rnd.uniform(-40, 40)] for _ in range(300)]
        prims = [dict(role='meadow', positions=pos, triangles=[[k, k+1, k+2] for k in range(0, 297, 3)])]
        back = decode_mesh(encode_mesh(prims))
        mine = as_written(prims)
        self.assertEqual([p['positions'] for p in mine], [p['positions'] for p in back])
        self.assertNotEqual(mine[0]['positions'], pos)

    def test_land_moved_in_place_reads_like_new(self):
        """Grading moves the land between two walks: the Terrain told where
        (`moved`) answers exactly as one built afresh, hits, plain buckets and
        floods alike."""
        import copy
        import random
        grid, h = checks._land_grid(self.doc, self.meshes)
        pos = [[grid.xs[q % grid.nx], h[q], grid.zs[q//grid.nx]] for q in range(grid.n)]
        land = dict(self.meshes, land=[dict(role='meadow', positions=pos, triangles=[list(t) for t in grid.triangles()])])
        probe = dict(self.doc, terrain=[{'mesh': 'land'}], scenery=[])
        old = checks.Terrain(probe, land)
        walker = checks.Walker(probe, land, old, self.room)
        before = walker.reach(self.start)
        far = list(sorted(before.nodes()))[len(before)//2]
        # A cut and a bank across the walk, 30 cm either side of a reached node.
        c = old.cell
        moved = []
        for q in range(grid.n):
            x, z = pos[q][0], pos[q][2]
            if abs(x-far[0]*c) < .3 and abs(z-far[1]*c) < .05:
                pos[q][1] += .04 if x > far[0]*c else -.03
                moved.append((x, z))
        self.assertGreater(len(moved), 10)
        old.moved(moved)
        fresh = checks.Terrain(probe, dict(land, land=copy.deepcopy(land['land'])))
        for limit, step in ((checks.WALK_DEG, checks.STEP_M), (40., .04)):
            a = checks.Walker(probe, land, old, self.room, limit=limit, step=step).reach(self.start)
            b = checks.Walker(probe, land, fresh, self.room, limit=limit, step=step).reach(self.start)
            self.assertEqual(a.nodes(), b.nodes(), limit)
        self.assertNotEqual(before.nodes(), checks.Walker(probe, land, old, self.room).reach(self.start).nodes())
        rnd = random.Random(9)
        for _ in range(3000):
            x, z = far[0]*c+rnd.uniform(-.5, .5), far[1]*c+rnd.uniform(-.3, .3)
            self.assertEqual(old.hit(x, z), fresh.hit(x, z))
            b = old.bucket
            i, j = int(math.floor(x/b)), int(math.floor(z/b))
            self.assertEqual(old.plain_slope(i, j), fresh.plain_slope(i, j))

    def test_an_obstacle_on_the_way_is_noticed(self):
        """`holds` proves a flood's way still open with one more obstacle, and
        refuses it when the obstacle stands on the way."""
        walker = checks.Walker(self.doc, self.meshes, checks.Terrain(self.doc, self.meshes), self.room)
        reach = walker.reach(self.start)
        crate = self.doc['objects'][0]['position_m']
        path = reach.path((crate[0], crate[2]))
        self.assertIsNotNone(path)
        c = walker.t.cell
        mid = path[len(path)//2]
        on_way = checks.obstacles(dict(self.doc, scatter=[dict(prototype='boulder', position_m=[mid[0]*c, 0, mid[1]*c],
                                                                 yaw_deg=0, scale=[.4, .4, .4])]))
        far = checks.obstacles(dict(self.doc, scatter=[dict(prototype='boulder', position_m=[50., 0, 50.],
                                                              yaw_deg=0, scale=[.4, .4, .4])]))
        self.assertFalse(checks.holds(walker, path, on_way))
        self.assertTrue(checks.holds(walker, path, far))


class RoomFrames(unittest.TestCase):
    """The room read once (`wall_frame`, `inside_distance`) answers exactly
    as the plain reading, on every corpus room and the garage magnified."""

    def test_same_numbers(self):
        import random
        from pipeline.landscape.generator.land import inside_distance, wall_frame, wall_frame_plain
        from pipeline.landscape.generator.scale import magnify_room
        from pipeline.landscape.harness.common import load_json
        rooms = [load_json(path/'room.json') for path in sorted(ROOMS.iterdir()) if (path/'room.json').exists()]
        rooms.append(magnify_room(load_json(ROOMS/'garage_nominal'/'room.json'), 10))
        rnd = random.Random(4)
        for room in rooms:
            lo, hi = room['bounds']['min_m'], room['bounds']['max_m']
            pts = [(rnd.uniform(lo[0]-1, hi[0]+1), rnd.uniform(lo[2]-1, hi[2]+1)) for _ in range(3000)]
            pts += [(lo[0], lo[2]), (hi[0], hi[2]), (lo[0], (lo[2]+hi[2])/2), (lo[0]+1e-12, lo[2]+3e-7)]
            for x, z in pts:
                plain = wall_frame_plain(room, x, z)
                self.assertEqual(wall_frame(room, x, z), plain, (room['room_id'], x, z))
                d = inside_distance(room, x, z)
                self.assertEqual((d, math.copysign(1, d)), (-plain[0], math.copysign(1, -plain[0])), (room['room_id'], x, z))


class LShapedIsland(unittest.TestCase):
    """An L-shaped room becomes an L-shaped island: the notch is sea, both arms land."""

    def test_l_room_l_island(self):
        room = ROOMS/'awkward_l_nominal'
        out = os.path.join(workdir('l'), 'pkg')
        generate(room, out)
        result = checks.run(out, room)
        self.assertTrue(result['sea_ok'], result['sea'])
        from pipeline.landscape.harness import read_package
        doc, meshes, _, _ = read_package(out, room)
        terrain = checks.Terrain(doc, meshes)
        level = doc['x_generator']['sea']['level_m']
        self.assertLess(terrain.height(1.8, 1.8), level-.1)      # the notch
        self.assertGreater(terrain.height(-1.5, 1.5), level+.02)  # one arm
        self.assertGreater(terrain.height(1.5, -1.5), level+.02)  # the other


class CheckerCatchesContradictions(unittest.TestCase):
    """The checks are not vacuous: a broken stream or a floating cottage fails."""

    def test_uphill_stream_and_floating_cottage_fail(self):
        from pipeline.landscape.harness import read_package
        room = ROOMS/'garage_nominal'
        doc, meshes, _, _ = read_package(garage(), room)
        river = next(r['mesh'] for r in doc['water'] if r['kind'] == 'flowing')
        terrain = checks.Terrain(doc, meshes)
        pos = meshes[river][0]['positions']
        pos[-1][1] += .05
        pos[-2][1] += .05
        ok, _, _ = checks.water_checks(doc, meshes, terrain)
        self.assertFalse(ok)
        cottage = next(s for s in doc['scatter'] if s['prototype'] == 'cottage')
        cottage['position_m'][1] += .05
        ok, _ = checks.grounding_checks(doc, terrain)
        self.assertFalse(ok)
        cottage['position_m'][1] -= .05
        # A boulder dropped on the crate leaves no 11 cm place to stand there.
        crate = doc['objects'][0]['position_m']
        doc['scatter'].append(dict(prototype='boulder', position_m=list(crate), yaw_deg=0, scale=[.4, .4, .4]))
        from pipeline.landscape.harness.common import load_json
        ok, walks = checks.walk_checks(doc, meshes, terrain, load_json(room/'room.json'))
        self.assertFalse(ok)
        self.assertFalse(walks[0]['found'])
        # A beach that drops off steeply, or a reef run up onto the coast, fails.
        sea = doc['x_generator']['sea']
        beach = sea['beaches'][0]
        beach['water_m'] = list(beach['wash_ashore_m'])
        ok, _ = checks.sea_checks(doc, meshes, terrain, load_json(room/'room.json'))
        self.assertFalse(ok)

    def test_rock_offshore_fails(self):
        """A rock raised near the surface out past the coast (a reef's rocks, a
        sea stack) is a wall offshore: the sea check fails and names it."""
        from pipeline.landscape.harness import read_package
        from pipeline.landscape.harness.common import load_json
        room = ROOMS/'garage_nominal'
        doc, meshes, _, _ = read_package(garage(), room)
        play = doc['x_generator']['sea']['play_area']['outline_m']
        level = doc['x_generator']['sea']['level_m']
        # In from the playable water's edge toward the room's middle, where the water is over 0.3 m deep.
        x0, z0 = play[0]
        cx, cz = sum(p[0] for p in play)/len(play), sum(p[1] for p in play)/len(play)
        terrain = checks.Terrain(doc, meshes)
        rx, rz = next((x0+(cx-x0)*f, z0+(cz-z0)*f) for f in (k*.01 for k in range(2, 40))
                      if level-terrain.height(x0+(cx-x0)*f, z0+(cz-z0)*f) > .3)
        raised = 0
        for part in meshes['land']:
            for v in part['positions']:
                if math.hypot(v[0]-rx, v[2]-rz) < .06:
                    v[1] = level-.03
                    raised += 1
        self.assertGreater(raised, 3)
        terrain = checks.Terrain(doc, meshes)
        ok, rows = checks.sea_checks(doc, meshes, terrain, load_json(room/'room.json'))
        self.assertFalse(ok)
        self.assertGreaterEqual(rows['offshore']['walls'], 1, rows['offshore'])


class NoisyScanKeepsItsPlace(unittest.TestCase):
    """Scan 17: the desk is no longer swallowed (confidence limits spread) and
    the land gives way so the doors, the crate and the yards are reachable."""

    def test_scan_17_all_checks(self):
        room = ROOMS/'garage_scan_17'
        out = os.path.join(workdir('s17'), 'pkg')
        generate(room, out)
        result = checks.run(out, room)
        self.assertTrue(result['footprints_ok'], [r for r in result['footprints'] if not r['reads']])
        self.assertTrue(result['ok'], [w for w in result['walks'] if not w['found']])


class RoomsDiffer(unittest.TestCase):
    """Not every room gets a tarn: the room's own floor suggests a tarn, a
    river with deep pools or a dry upland (no generation; the choice only)."""

    def test_water_follows_the_room(self):
        from pipeline.landscape.generator.land import parse_objects
        from pipeline.landscape.generator.water import water_character
        from pipeline.landscape.harness.common import load_json
        setup = {'water': 'some'}
        got = {}
        for name in ('garage_nominal', 'bedroom_nominal', 'workshop_nominal', 'near_empty_nominal'):
            room = load_json(ROOMS/name/'room.json')
            objects = parse_objects(load_json(ROOMS/name/'inventory.json'))
            got[name] = water_character(room, objects, [s['position_m'] for s in room['spawns']], setup)[0]
        self.assertEqual(got, {'garage_nominal': 'tarn', 'bedroom_nominal': 'dry', 'workshop_nominal': 'river',
                               'near_empty_nominal': 'tarn'})


class StreamCrossing(unittest.TestCase):
    """A path that must cross a stream does so on a level footbridge that the
    walk check uses as ground. On the islands no corpus room's hamlet lies
    across water any more (near_empty_nominal's moved to the coast), so this
    checks the room walks and swims whole; the bridge itself is untested."""

    def test_footbridge_carries_the_walk(self):
        room = ROOMS/'near_empty_nominal'
        out = os.path.join(workdir('ne'), 'pkg')
        generate(room, out)
        result = checks.run(out, room)
        self.assertTrue(result['ok'], [w for w in result['walks'] if not w['found']])


class RoadReachesItsGoal(unittest.TestCase):
    """A road keeps a band off the water but still reaches a goal inside that band: a cottage's forecourt by the shore
    (on the 10x world a hamlet by the sea was left with no road)."""

    def test_forecourt_by_the_water(self):
        from pipeline.landscape.generator.field import Grid
        from pipeline.landscape.generator.life import build_road
        g = Grid(-1., -.5, 1., .5, .03)
        n = g.n
        wet = [max(0., .81-g.xs[q % g.nx]) for q in range(n)]   # the shore at x = 0.81, water beyond
        # The forecourt 22 cm from the water, inside the 24 cm the road keeps off it.
        road = build_road(g, [0.]*n, wet, [True]*n, [False]*n, [0.]*n, (-.8, 0.), (.59, 0.))
        self.assertIsNotNone(road)
        self.assertLess(math.hypot(road[-1][0]-.59, road[-1][1]), .02)
        # Only its goal lies inside the band.
        self.assertTrue(all(x <= .81-.24+1e-6 for x, _ in road[:-1]), road[-6:])
        # A goal on the shore line or in the water stays out of reach.
        for x in (.81, .9):
            self.assertIsNone(build_road(g, [0.]*n, wet, [True]*n, [False]*n, [0.]*n, (-.8, 0.), (x, 0.)), x)


class ClimbLips(unittest.TestCase):
    """What you climb, you can stand on top of (Lane P's climbs on the garage):
    a knife-edge ridge and a needle are found from standable ground; planing
    gives them a ledge; a mesa with a flat top is never flagged."""

    @staticmethod
    def land(shape):
        from pipeline.landscape.generator.field import Grid
        g = Grid(-.6, -.6, .6, .6, .03)
        return g, [shape(g.xs[q % g.nx], g.zs[q//g.nx]) for q in range(g.n)]

    def test_knife_needle_and_mesa(self):
        from pipeline.landscape.generator.climb import knife_edges, plane_thin_tops
        knife = lambda x, z: max(0., .3-abs(x)*2.2) if abs(z) < .3 else 0.          # a 66 degree ridge, 30 cm up
        needle = lambda x, z: max(0., .4-math.hypot(x, z)*3.)                       # a 72 degree cone, 40 cm up
        mesa = lambda x, z: .3 if max(abs(x), abs(z)) < .2 else max(0., .3-(max(abs(x), abs(z))-.2)*4)
        for name, shape, bad in (('knife', knife, True), ('needle', needle, True), ('mesa', mesa, False)):
            g, h = self.land(shape)
            found = knife_edges(g, h, lambda q: True)
            self.assertEqual(bool(found), bad, name)
            if bad:
                self.assertTrue(all(b['rise'] > .2 for b in found[:1]), name)
                moved, deepest = plane_thin_tops(g, h, lambda q: True)
                self.assertGreater(moved, 0, name)
                self.assertLess(deepest, .2, name)
                self.assertEqual(knife_edges(g, h, lambda q: True), [], name)



class DetailSeams(unittest.TestCase):
    """The 10x world's ground (detail.py): 2.5 cm blocks of 1 m on a 10 cm
    base, stitched without a crack. Blocks in an L, a lone block and a pair
    touching only at a corner, a path cut through them: every edge inside the
    land is shared by exactly two triangles (no T-junction), a vertical ray
    anywhere across the blocks' sides finds the land at the height the
    generator reads there, and the checks' plain-ground shortcut over the
    blocks answers exactly as reading every sample."""

    @classmethod
    def setUpClass(cls):
        from pipeline.landscape.generator.detail import Detail
        from pipeline.landscape.generator.field import Grid, Noise
        g = Grid(-3., -3., 3., 3., .1)

        def land(x, z):
            return .3+.2*math.sin(x*1.3)*math.cos(z*.9)+.01*math.sin(x*7+z*5)

        def landform(xs, zs):
            return [(land(x, z), 0., -1, 0.) for z in zs for x in xs]
        h = [r[0] for r in landform(g.xs, g.zs)]
        d = Detail(g, 10., Noise(1, scale=10.), landform)
        for b in ((1, 1), (2, 1), (1, 2), (4, 4), (3, 2)):
            d.marks[b] = {'test'}
        # A path cut 4 cm into the land through the blocks (a graded line).
        pts = [(-1.95+.025*k, -1.6+.016*k) for k in range(100)]
        d.road(pts, [land(x, z)-.04 for x, z in pts], .085, .185)
        d.marks = {b: {'test'} for b, w in d.marks.items() if 'test' in w}
        d.build(h, [])
        d.merge(h)
        one = {'meadow': 1.}
        prims, cls.counts = d.assemble(h, [one]*g.n, [(1., 1., 1.)]*g.n, [one]*d.n, [(1., 1., 1.)]*d.n)
        cls.g, cls.d, cls.h, cls.prims, cls.land = g, d, h, prims, staticmethod(land)
        cls.doc = dict(terrain=[{'mesh': 'land'}], scenery=[], water=[], scatter=[], objects=[], prototypes={},
                       x_generator=dict(cell_m=.1, world_scale=10., detail=dict(cell_m=d.fc)))
        cls.meshes = {'land': prims}

    def test_no_crack(self):
        edges = {}
        for p in self.prims:
            vs = p['positions']
            for t in p['triangles']:
                a, b, c = (vs[k] for k in t)
                # Every face is a real triangle, wound like the base's (counter-clockwise seen from +Y).
                self.assertGreater((b[2]-a[2])*(c[0]-a[0])-(b[0]-a[0])*(c[2]-a[2]), 1e-9)
                for u, v in ((a, b), (b, c), (c, a)):
                    key = tuple(sorted(((round(u[0], 6), round(u[2], 6)), (round(v[0], 6), round(v[2], 6)))))
                    edges[key] = edges.get(key, 0)+1
        lo, hi = self.g.xs[0], self.g.xs[-1]

        def outer(e):
            (x0, z0), (x1, z1) = e
            return (x0 == x1 and x0 in (lo, hi)) or (z0 == z1 and z0 in (lo, hi))
        open_ = [e for e, n in edges.items() if n != 2 and not (n == 1 and outer(e))]
        self.assertEqual(open_, [])
        self.assertGreater(self.counts['seam_fans'], 50)
        self.assertEqual(self.counts['blocks'], 5)

    def test_rays_find_the_land(self):
        terrain = checks.Terrain(self.doc, self.meshes)
        ground = self.d.ground(self.h)
        # Every 7 mm across the blocks and their sides, and on the block sides and corners exactly.
        pts = [(-2.05+.007*i, -2.05+.007*j) for i in range(330) for j in range(330)]
        pts += [(x, z) for x in (-2., -1., 0., 1.) for z in [-2.+.0125*k for k in range(240)]]
        pts += [(x, z) for z in (-2., -1., 0., 1.) for x in [-2.+.0125*k for k in range(240)]]
        for x, z in pts:
            hit = terrain.hit(x, z)
            self.assertIsNotNone(hit, (x, z))
            self.assertAlmostEqual(hit[0], ground.tri_height(self.h, x, z), delta=2e-5, msg=(x, z))
        # The path's cut is in the blocks' 2.5 cm land (4 cm down on its line).
        x, z = -1.95+.025*20, -1.6+.016*20
        self.assertAlmostEqual(ground.tri_height(self.h, x, z), self.land(x, z)-.04, delta=.003)

    def test_plain_shortcut_over_the_blocks(self):
        fast = checks.Terrain(self.doc, self.meshes)
        slow = checks.Terrain(self.doc, self.meshes)
        b = slow.bucket
        slow.candidates = lambda x, z: slow.grid.get((int(math.floor(x/b)), int(math.floor(z/b))), ())
        slow.plain_slope = lambda i, j: None
        room = {'bounds': {'min_m': [-2.9, 0., -2.9], 'max_m': [2.9, 2., 2.9]}, 'shell': {'parts': []},
                'spawns': [{'role': 'player', 'position_m': [0., 0., 0.]}]}
        a = checks.Walker(self.doc, self.meshes, fast, room).reach((-1.6, -1.9))
        c = checks.Walker(self.doc, self.meshes, slow, room).reach((-1.6, -1.9))
        self.assertGreater(len(a), 5000)
        self.assertEqual(a.nodes(), c.nodes())
        # Buckets wholly in a block are vouched for; one on a seam is read in full.
        self.assertIsNotNone(fast.plain_slope(-16, -16))
        self.assertIsNone(fast.plain_slope(-20, -16))
        import random
        rnd = random.Random(7)
        for _ in range(4000):
            x, z = rnd.uniform(-2.2, .2), rnd.uniform(-2.2, .2)
            self.assertEqual(fast.hit(x, z), slow.hit(x, z), (x, z))
            self.assertEqual(fast.land_height(x, z), slow.height(x, z), (x, z))


class CottageFooting(unittest.TestCase):
    """A cottage stands on its pad on a coarse grid (the founder's garage at 10x: a cottage 10 cm into its bank).
    Two things left its footing in the slope: a way graded beside the pad cut and filled the pad's rim outside
    `pads` > .3 (4 cm from the footprint; the pad is levelled 7 cm out), and a 10 cm cell meeting the footprint's
    edge had vertices beyond that rim, in the bank. `flatten(reach=)` levels those, `pad_hold` keeps them from the ways."""

    def footing(self, offset, held, yaw=25., reach=.1):
        """The checks' grounding of one cottage on a 30 degree bank after a way is graded `offset` m beside it."""
        from pipeline.landscape.generator import scale as world
        from pipeline.landscape.generator.field import Grid, smooth
        from pipeline.landscape.generator.life import flatten, grade_line, pad_hold
        from pipeline.landscape.harness.kit import SIZES
        world.set_world(10.)
        self.addCleanup(world.set_world, 1.)
        g = Grid(-1.2, -1.2, 1.2, 1.2, .1)
        n = g.n
        h = [.6*g.zs[q//g.nx]+.12*math.sin(3*g.xs[q % g.nx]) for q in range(n)]
        sc = .74
        hx, hz = SIZES['cottage'][0]*sc/2, SIZES['cottage'][2]*sc/2
        level = flatten(g, h, 0., 0., hx, hz, yaw, reach=reach if held else 0.)
        a = math.radians(yaw)
        c, s_ = math.cos(a), math.sin(a)
        pads = [.55*(1-smooth(max(abs(c*g.xs[q % g.nx]-s_*g.zs[q//g.nx])-hx, abs(s_*g.xs[q % g.nx]+c*g.zs[q//g.nx])-hz,
                                 0.)/.08)) for q in range(n)]
        hold = pad_hold(g, [dict(x=0., z=0., yaw=yaw, hx=hx, hz=hz)], .07, reach) if held else [False]*n
        keep_out = [1. if hold[q] else pads[q] for q in range(n)]
        grade_line(g, h, [1.]*n, keep_out, [(offset, -1.+.02*k) for k in range(101)])
        land = [dict(role='meadow', positions=[[g.xs[q % g.nx], h[q], g.zs[q//g.nx]] for q in range(n)],
                     triangles=[list(t) for t in g.triangles()])]
        doc = dict(terrain=[{'mesh': 'land'}], scenery=[], water=[], objects=[], prototypes={},
                   x_generator=dict(cell_m=.1, world_scale=10.),
                   scatter=[dict(prototype='cottage', position_m=[0., level-.004, 0.], yaw_deg=yaw, scale=[sc]*3)])
        ok, rows = checks.grounding_checks(doc, checks.Terrain(doc, {'land': land}))
        return ok, rows[0]

    def test_a_graded_way_beside_a_pad_leaves_the_footing(self):
        for offset in (-.45, -.3, .3, .4):
            was, row = self.footing(offset, False)
            self.assertFalse(was, ('a way beside the pad as it was', offset, row))   # the defect
            ok, row = self.footing(offset, True)
            self.assertTrue(ok, (offset, row))
            self.assertLess(row['sunk_m'], .025)
            self.assertLess(row['hang_m'], .012)

    def test_a_cell_at_the_footprints_edge_is_levelled_whole(self):
        # The pad alone on a 45 degree bank (no way near it): the ground over the whole footprint, read every few cm.
        from pipeline.landscape.generator.field import Grid
        from pipeline.landscape.generator.life import flatten
        from pipeline.landscape.harness.kit import SIZES
        hx, hz = SIZES['cottage'][0]*.74/2, SIZES['cottage'][2]*.74/2

        def span(yaw, reach):
            g = Grid(-1.2, -1.2, 1.2, 1.2, .1)
            h = [g.zs[q//g.nx]+.12*math.sin(3*g.xs[q % g.nx]) for q in range(g.n)]
            flatten(g, h, 0., 0., hx, hz, yaw, reach=reach)
            c, s_ = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
            ys = [g.tri_height(h, c*i/20*hx+s_*j/20*hz, -s_*i/20*hx+c*j/20*hz) for i in range(-20, 21) for j in range(-20, 21)]
            return max(ys)-min(ys)
        was = [span(yaw, 0.) for yaw in (0., 10., 25., 45., 70., 110., 155.)]
        self.assertGreater(max(was), .03, was)                                   # the bank beyond the rim, in a corner
        for yaw in (0., 10., 25., 45., 70., 110., 155.):
            self.assertLess(span(yaw, .1), 1e-9, yaw)                            # every cell meeting the footprint, level

    def test_the_hold_is_the_rim_and_the_cells_meeting_the_footprint(self):
        from pipeline.landscape.generator.field import Grid
        from pipeline.landscape.generator.life import pad_hold, touches
        g = Grid(-1., -1., 1., 1., .1)
        house = dict(x=0., z=0., yaw=0., hx=.2, hz=.2)
        rim = pad_hold(g, [house], .07)
        for q in range(g.n):
            d = max(abs(g.xs[q % g.nx])-.2, abs(g.zs[q//g.nx])-.2, 0.)
            self.assertEqual(rim[q], d <= .07+1e-9, (g.xs[q % g.nx], g.zs[q//g.nx]))
        both = pad_hold(g, [house], .07, .1)
        self.assertTrue(all(b or not r for r, b in zip(rim, both)))
        for q in range(g.n):
            x, z = g.xs[q % g.nx], g.zs[q//g.nx]
            self.assertEqual(touches(x, z, .1, .2, .2, 1., 0.), abs(x) <= .3+1e-9 and abs(z) <= .3+1e-9)
        self.assertGreater(sum(both), sum(rim))
        # A turned footprint: the vertices meeting it, against a reading of its points every 5 mm.
        a = math.radians(33)
        c, s_ = math.cos(a), math.sin(a)
        points = [(c*lx+s_*lz, -s_*lx+c*lz) for lx in [i*.005 for i in range(-40, 41)] for lz in [j*.005 for j in range(-40, 41)]]
        for x in [i*.05 for i in range(-12, 13)]:
            for z in [j*.05 for j in range(-12, 13)]:
                reach = min(max(abs(x-px), abs(z-pz)) for px, pz in points)
                got = touches(x, z, .1, .2, .2, c, s_)
                self.assertTrue(not got or reach <= .1+.0036, (x, z, reach))
                self.assertTrue(got or reach >= .1-.0036, (x, z, reach))


class HamletSites(unittest.TestCase):
    """The hamlet's site is the best the land offers; a site given up (no way reaches it) rules out its
    neighbourhood and the best of the rest is found, until none is left."""

    @staticmethod
    def shore(reachable=None):
        from pipeline.landscape.generator.field import Grid, slope_field
        g = Grid(-6., -6., 6., 6., .1)
        n, nx = g.n, g.nx
        h = [.05+sum(.5*max(0., 1-math.hypot(g.xs[q % nx]-cx, g.zs[q//nx]-2.5)/1.6) for cx in (-3., 3.)) for q in range(n)]
        wet = [max(0., 5.-g.zs[q//nx]) for q in range(n)]   # a shore along z = 5, the sea beyond it
        return (g, h, slope_field(g, h), wet, [True]*n, [[0., 0., -5.]], lambda x, z: True, lambda x, z: 5., reachable)

    def test_the_next_site_lies_beyond_the_neighbourhood_given_up(self):
        from pipeline.landscape.generator.life import SITE_GAP_M, find_hamlet
        args = self.shore()
        best = find_hamlet(*args)
        self.assertEqual(find_hamlet(*args, skip=()), best)
        given_up = []
        for _ in range(12):
            site = find_hamlet(*args, skip=given_up)
            self.assertIsNotNone(site)
            self.assertTrue(all(math.hypot(site[0]-a, site[1]-b) >= SITE_GAP_M for a, b in given_up), (site, given_up))
            given_up.append(site)
        self.assertEqual(given_up[0], best)
        self.assertEqual(len(set(given_up)), 12)

    def test_none_left(self):
        from pipeline.landscape.generator.life import find_hamlet
        g = self.shore()[0]
        best = find_hamlet(*self.shore())
        only = {q for q in range(g.n) if math.hypot(g.xs[q % g.nx]-best[0], g.zs[q//g.nx]-best[1]) < .5}
        args = self.shore(only)
        self.assertEqual(find_hamlet(*args), best)
        self.assertIsNone(find_hamlet(*args, skip=[best]))


class HamletProvedByTheWalk(unittest.TestCase):
    """On a magnified room the hamlet is proved by the exact walk from the player's spawn: a site whose crate or
    door no graded way reaches is given up and the land is made again with the next site (the founder's garage at
    10x: three cottages on a shore below a bank, which the quick reachability flood accepted and the exact walk
    never reached). A world of 2x runs the same code on a small room in about a minute."""

    room = 'bedroom_nominal'

    def generate_at_2x(self, name, lost):
        from unittest import mock
        from pipeline.landscape.generator import generate as gen, scale as world
        world.set_world(2.)
        self.addCleanup(world.set_world, 1.)
        out = os.path.join(workdir(name), 'pkg')
        with mock.patch.object(gen, 'hamlet_lost', lost(gen.hamlet_lost)):
            return out, gen.generate(ROOMS/self.room, out)

    def test_a_cut_off_site_is_given_up_for_the_next(self):
        first = []

        def lost(real):
            def hamlet_lost(reach, goals, houses):
                """The first site's crate and doors have no way from the spawn; the next site's are the walk's own."""
                first.append(goals[0]) if not first else None
                return list(goals[:1+houses]) if goals[0] == first[0] else real(reach, goals, houses)
            return hamlet_lost
        out, doc = self.generate_at_2x('hamlet2x', lost)
        hamlet = doc['x_generator']['hamlet']
        self.assertEqual(len(hamlet['given_up']), 1, hamlet)
        site = hamlet['given_up'][0]
        self.assertGreaterEqual(math.hypot(hamlet['x']-site[0], hamlet['z']-site[1]), 1.5, hamlet)
        result = checks.run(out, ROOMS/self.room)
        self.assertTrue(result['walk_ok'], [w for w in result['walks'] if not w['found']])
        self.assertTrue(result['grounded_ok'], [g for g in result['grounding'] if not g['ok']])

    def test_a_hamlet_the_old_generator_left_cut_off(self):
        """home_office_nominal at 3x, as the generator was before this packet: no way from the spawn to the crate or
        the first door (the other rooms of the corpus at 3x with such a hamlet: home_office scan 73, garage scan 17,
        workshop scans 17 and 73, each with a door or the crate out of reach)."""
        from pipeline.landscape.generator import generate as gen, scale as world
        world.set_world(3.)
        self.addCleanup(world.set_world, 1.)
        room = ROOMS/'home_office_nominal'
        out = os.path.join(workdir('hamlet3x'), 'pkg')
        gen.generate(room, out)
        result = checks.run(out, room)
        self.assertTrue(result['walk_ok'], [w for w in result['walks'] if not w['found']])
        self.assertTrue(result['grounded_ok'], [g for g in result['grounding'] if not g['ok']])

    def test_the_first_site_stands_when_none_is_reached(self):
        from unittest import mock
        from pipeline.landscape.generator import generate as gen
        sites = []

        def lost(real):
            def hamlet_lost(reach, goals, houses):
                if not sites or sites[-1] != goals[0]:
                    sites.append(goals[0])
                return list(goals[:1+houses])       # no site is ever reached
            return hamlet_lost
        with mock.patch.object(gen, 'HAMLET_SITES', 2):
            out, doc = self.generate_at_2x('hamlet2x_none', lost)
        hamlet = doc['x_generator']['hamlet']
        self.assertEqual(len(sites), 3)             # two given up, then the first again
        self.assertEqual(sites[0], sites[2])
        self.assertEqual(len(hamlet['given_up']), 2)
        self.assertAlmostEqual(hamlet['x'], hamlet['given_up'][0][0], delta=.006)
        self.assertAlmostEqual(hamlet['z'], hamlet['given_up'][0][1], delta=.006)
        self.assertEqual(hamlet['houses'], 3)


class PlantSalt(unittest.TestCase):
    """A planting salt draws the plants, trees and stones again on the very same land. The game's navigation bake
    triangulates every tile's open ground round its trunk holes, and a rare layout of them (on the real garage, six
    holes in one open 8 m tile) makes it put three polygons on one edge ("Navigation region synchronization had 2
    edge error(s)"), where the land itself holds no overlap: the land there was the same in a layout that boots clean."""

    def test_the_same_land_and_other_plants(self):
        import json
        room = ROOMS/'bedroom_nominal'
        plain, salted = os.path.join(workdir('salt0'), 'pkg'), os.path.join(workdir('salt1'), 'pkg')
        generate(room, plain)
        generate(room, salted, plant_salt=2)
        for name in ('land.glb', 'jetty.glb', 'sea.glb'):
            self.assertEqual(Path(plain, name).read_bytes(), Path(salted, name).read_bytes(), name)
        before = json.loads(Path(plain, 'package.json').read_text(encoding='utf-8'))
        after = json.loads(Path(salted, 'package.json').read_text(encoding='utf-8'))
        self.assertNotIn('plant_salt', before['x_generator'])
        self.assertEqual(after['x_generator']['plant_salt'], 2)
        self.assertEqual(after['x_generator']['hamlet'], before['x_generator']['hamlet'])

        def plants(d):
            return sorted((r['prototype'], tuple(r['position_m'])) for r in d['scatter']
                          if r['prototype'] in ('broadleaf', 'conifer', 'rock', 'shrub'))
        self.assertGreater(len(plants(before)), 50)
        self.assertNotEqual(plants(before), plants(after))

    def test_a_magnified_room_takes_the_salt_its_island_bakes_clean_under(self):
        """The proof is `tools/test-room.ps1 -Scale`: it plays the ten-times stand-in garage and refuses a navigation
        bake's "edge error(s)". This holds the generator to the salt that proof last passed under. Run 4's block land
        with its summit back drew, under salt 1, a layout whose bake had 3 edge errors (the suite's 10 checks passed
        and the runner threw); under salt 2, none. Every change to a magnified room's land draws the planting again:
        whoever makes one runs -Scale, picks the salt again if the bake complains, and moves this number with it.
        A salt that is asked for is taken as asked, and true size takes none."""
        import inspect
        from unittest import mock
        from pipeline.landscape.generator import generate as G, scale as world
        self.assertEqual(G.WORLD_PLANT_SALT, 2, 'the salt tools/test-room.ps1 -Scale last passed under')
        names, taken = inspect.signature(G._generate), []

        def stand_in(*args, **kwargs):
            taken.append(names.bind(*args, **kwargs).arguments['plant_salt'])

        with mock.patch.object(G, '_generate', stand_in):
            G.generate('room', 'out')
            G.generate('room', 'out', plant_salt=5)
            world.set_world(10.)
            try:
                G.generate('room', 'out')
                G.generate('room', 'out', plant_salt=0)
                G.generate('room', 'out', plant_salt=3)
            finally:
                world.set_world(1.)
        self.assertEqual(taken, [0, 5, G.WORLD_PLANT_SALT, 0, 3])


if __name__ == '__main__':
    unittest.main()
