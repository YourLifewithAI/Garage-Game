"""The island's firepits (decisions 62 and 74, firepits.py): spread evenly, one per 250 m2 of standable dry land, 4 m apart,
1 m from any Fractal, 3 m from the starting campfire, on ground the strict walk stands a body on, one near the hamlet, first-run
ground where a share offers it; the package carries them and the written package proves them.

    python -B -m unittest pipeline.landscape.generator.tests.test_firepits -v
"""
import copy
import json
import math
import re
import unittest
from pathlib import Path

from pipeline.landscape.generator import checks, firepits as Firepits
from pipeline.landscape.generator.tests.test_fractals import climb_reach, synthetic_land, written
from pipeline.landscape.generator.tests import test_generator
from pipeline.landscape.generator.tests.test_generator import ROOMS, garage

ROOT = Path(__file__).resolve().parents[4]
CAMPS = ROOT/'game'/'scripts'/'native'/'Kernel'/'CommandHostCamps.cs'


def lattice(inside, size_x, size_z, step=Firepits.SAMPLE_M):
    """Land samples of a made-up island (every `step`, where `inside(x, z)`)."""
    out = []
    x = step/2
    while x < size_x:
        z = step/2
        while z < size_z:
            if inside(x, z):
                out.append((x, z))
            z += step
        x += step
    return out


def spots_of(land, band=lambda x, z: 1):
    return [Firepits.Spot(x, 0., z, band(x, z)) for x, z in land]


class KernelAgrees(unittest.TestCase):
    def test_the_rule_is_the_kernels(self):
        """The kernel's CampLaws (the spread the kernel still uses where a manifest carries no firepits) and the generator's
        rule read the same numbers."""
        text = CAMPS.read_text(encoding='utf-8')

        def const(name):
            return float(re.search(r'const (?:float|int) %s = ([0-9.]+)f?;' % name, text).group(1))
        self.assertEqual(const('AreaPerFirepitM2'), Firepits.AREA_PER_FIREPIT_M2)
        self.assertEqual(const('MinFirepits'), Firepits.MIN_FIREPITS)
        self.assertEqual(const('MaxFirepitsByArea'), Firepits.MAX_FIREPITS)
        self.assertEqual(const('FirepitFractalClearM'), Firepits.FRACTAL_CLEAR_M)
        self.assertEqual(const('StartingFireAheadM'), Firepits.STARTING_FIRE_AHEAD_M)
        self.assertEqual(const('SpreadIterations'), Firepits.SPREAD_ROUNDS)
        self.assertLessEqual(Firepits.MAX_FIREPITS, const('MaxFirepits'), "the manifest's cap holds every island's count")

    def test_the_count_follows_the_area(self):
        self.assertEqual([Firepits.count_for(a) for a in (0, 100, 5*250, 3047, 1e6)], [0, 3, 5, 12, 12])
        self.assertEqual([Firepits.count_for(a) for a in (4.5*250, 5.5*250)], [4, 6], 'a half to even, as the kernel rounds')


class Spread(unittest.TestCase):
    """Decision 74's spread on two shapes of land (the kernel's own test shapes): the count follows the area, every land point
    is near a campfire, none is pushed to an edge, every two stand 4 m apart, and the same land gives the same firepits."""

    SHAPES = (
        ('a 120 by 6 m strip', lambda x, z: 0 < x < 120 and 0 < z < 6, 120, 6, (3.75, 2.25)),
        ('an L of 9 m arms, 33 m each way', lambda x, z: (0 < x < 33 and 0 < z < 9) or (0 < x < 9 and 0 < z < 33), 33, 33,
         (3.75, 3.75)),
    )

    def test_even_over_the_land_and_never_at_its_edges(self):
        for name, inside, sx, sz, start in self.SHAPES:
            with self.subTest(name):
                land = lattice(inside, sx, sz)
                area = len(land)*Firepits.SAMPLE_M**2
                block = Firepits.choose(land, area, spots_of(land), start)
                pits = [(i['position_m'][0], i['position_m'][2]) for i in block['items']]
                self.assertEqual(len(pits), Firepits.count_for(area), block['notes'])
                cover = max(min(math.hypot(x-a, z-b) for a, b in pits+[start]) for x, z in land)
                self.assertLessEqual(cover, 20., 'every land point within 20 m of a campfire')
                outside = [(x-6, z-6) for x, z in lattice(lambda x, z: not inside(x-6, z-6), sx+12, sz+12)]
                edge = min(min(math.hypot(a-x, b-z) for x, z in outside) for a, b in pits)
                self.assertGreaterEqual(edge, 2.9, 'no firepit pushed to the edge (the strip is 6 m wide)')
                self.assertGreaterEqual(block['counts']['nearest_pair_m'], Firepits.APART_M)
                self.assertEqual(block, Firepits.choose(land, area, spots_of(land), start), 'the same land, the same firepits')
                self.assertEqual(block['counts']['farthest_land_from_a_campfire_m'], round(cover, 3))

    def test_nearest_the_start_first(self):
        name, inside, sx, sz, start = self.SHAPES[0]
        land = lattice(inside, sx, sz)
        block = Firepits.choose(land, len(land)*.25, spots_of(land), start)
        d = [math.hypot(i['position_m'][0]-start[0], i['position_m'][2]-start[1]) for i in block['items']]
        self.assertEqual(d, sorted(d), 'the order the kernel numbers them in: firepit:1 the nearest')

    def test_four_metres_apart_even_where_the_land_wants_more(self):
        """A crowded island: the land's area asks for three, but only so many stand 4 m apart; the block says so."""
        land = lattice(lambda x, z: True, 4, 4)
        spots = spots_of(land)
        block = Firepits.choose(land, 100., spots, (2., 2.))
        pits = [(i['position_m'][0], i['position_m'][2]) for i in block['items']]
        self.assertEqual(len(pits), 2, 'only two corners of a 4 m square stand 4 m apart')
        self.assertTrue(all(math.hypot(a[0]-b[0], a[1]-b[1]) >= Firepits.APART_M for n, a in enumerate(pits) for b in pits[n+1:]))
        self.assertTrue(any('under the 3 the land asks for' in n for n in block['notes']), block['notes'])

    def test_first_run_ground_where_a_share_offers_it(self):
        """The west half is first-run ground and the east half beyond reach: a firepit whose share holds first-run ground within
        REACH_PULL_M of its centre takes it, so more firepits stand within reach than with no pull, and none moves farther."""
        inside = lambda x, z: 0 < x < 60 and 0 < z < 40   # noqa: E731
        land = lattice(inside, 60, 40)
        reach = lambda x, z: 1 if x < 31 else None        # noqa: E731
        pulled = Firepits.choose(land, len(land)*.25, spots_of(land, reach), (2., 2.))
        was = Firepits.REACH_PULL_M
        try:
            Firepits.REACH_PULL_M = 0.
            plain = Firepits.choose(land, len(land)*.25, spots_of(land, reach), (2., 2.))
        finally:
            Firepits.REACH_PULL_M = was
        self.assertGreater(pulled['counts']['first_run'], plain['counts']['first_run'])
        self.assertEqual(pulled['counts']['placed'], plain['counts']['placed'])
        for item in pulled['items']:
            self.assertEqual(item.get('reach_band'), reach(item['position_m'][0], item['position_m'][2]))
            self.assertEqual(item['why'].startswith('first-run reach'), item.get('reach_band') == 1, item)

    def test_one_near_the_hamlet_clear_of_its_cottages(self):
        land = lattice(lambda x, z: 0 < x < 40 and 0 < z < 40, 40, 40)
        houses = [dict(x=30., z=30., yaw=0., hx=.2, hz=.17), dict(x=30.7, z=30.3, yaw=40., hx=.2, hz=.17)]
        block = Firepits.choose(land, len(land)*.25, spots_of(land), (3., 3.), hamlet=(30., 30.), houses=houses)
        near = [i for i in block['items'] if i.get('hamlet')]
        self.assertEqual(len(near), 1)
        x, _, z = near[0]['position_m']
        self.assertLessEqual(math.hypot(x-30., z-30.), Firepits.HAMLET_NEAR_M)
        for hh in houses:
            self.assertGreaterEqual(Firepits._cottage_distance(hh, x, z), Firepits.HAMLET_CLEAR_M)
        self.assertTrue(near[0]['why'].startswith('near the hamlet'))
        self.assertGreaterEqual(block['counts']['nearest_pair_m'], Firepits.APART_M)
        self.assertFalse(any(i.get('hamlet') for i in Firepits.choose(land, len(land)*.25, spots_of(land), (3., 3.))['items']))


class OnTheLand(unittest.TestCase):
    """land_and_spots on a walked island: water, steep ground, a cottage, a path, a Fractal and the start are no place for a
    firepit, and the land's area counts what a body stands on."""

    @classmethod
    def setUpClass(cls):
        from pipeline.landscape.generator.field import face_slope_max
        cls.half = 14.
        land = synthetic_land(cls.half)
        g = land.grid
        nx = g.nx
        cls.wet = [9.]*g.n
        cls.cottage = [False]*g.n
        cls.path = [False]*g.n
        for j in range(g.nz):
            for i in range(nx):
                x, z = g.xs[i], g.zs[j]
                q = j*nx+i
                if x > 8 and z > 8:
                    cls.wet[q] = 0.             # a pond in the north-east corner
                if x < -8 and z < -8:
                    land.h[q] = 1.+(abs(x)-8)*1.2  # a 50 degree bank in the south-west
                if -1 < x < 1 and 4 < z < 6:
                    cls.cottage[q] = True
                if abs(x-6) < .2:
                    cls.path[q] = True
        cls.land = land
        cls.fslope = face_slope_max(g, land.h, g.triangles())
        cls.cr = climb_reach(land, cls.half)
        cls.fractals = [(-6., 6.)]
        cls.spawn = (0., 0.)
        cls.got = Firepits.land_and_spots(g, land.h, cls.fslope, [True]*g.n, cls.wet, [False]*g.n, cls.cottage,
                                          lambda q, x, z: cls.path[q], cls.cr.w, cls.cr.run(1.), [], cls.spawn,
                                          cls.fractals, -.02)

    def test_no_firepit_in_water_on_a_bank_a_cottage_or_a_path(self):
        land, area, spots = self.got
        self.assertTrue(spots)
        for s in spots:
            self.assertFalse(s.x > 8-.3 and s.z > 8-.3, 'water')
            self.assertFalse(s.x < -8 and s.z < -8, 'a bank too steep to stand on')
            self.assertFalse(-1 < s.x < 1 and 4 < s.z < 6, 'a cottage')
            self.assertGreater(abs(s.x-6), .15, 'a path')
            self.assertTrue(self.cr.w.standable(self.cr.w.key(s.x, s.z)))
            self.assertAlmostEqual(s.y, self.land.grid.tri_height(self.land.h, s.x, s.z), places=3)

    def test_clear_of_the_fractals_and_the_start(self):
        _, _, spots = self.got
        for s in spots:
            self.assertGreaterEqual(math.hypot(s.x+6., s.z-6.), Firepits.FRACTAL_CLEAR_M)
            self.assertGreaterEqual(math.hypot(s.x, s.z), Firepits.START_CLEAR_M+Firepits.STARTING_FIRE_AHEAD_M)

    def test_the_land_counts_what_a_body_stands_on(self):
        land, area, _ = self.got
        # The walk's floor is the island less half a metre each side; the pond, the bank and the cottage are not land.
        self.assertTrue(all(not (x > 8 and z > 8) and not (-1 < x < 1 and 4 < z < 6) for x, z in land))
        self.assertAlmostEqual(area, len(land)*Firepits.SAMPLE_M**2)
        self.assertLess(area, (2*self.half)**2-60.)

    def test_each_spot_says_its_band(self):
        _, _, spots = self.got
        base = self.cr.run(1.)
        self.assertTrue(all(s.band == base.band_at(s.x, s.z) for s in spots))


class Garage(unittest.TestCase):
    """The 1x garage's package carries its firepits; the written package proves them, and a wrong claim fails."""

    @classmethod
    def setUpClass(cls):
        if not Path(garage()).exists():
            test_generator._GARAGE.clear()   # removed by another module's teardown in the same run: made again
        cls.folder = garage()
        cls.doc = json.loads(Path(cls.folder, 'package.json').read_text(encoding='utf-8'))

    def test_the_package_carries_the_firepits(self):
        fp = self.doc['x_generator']['firepits']
        c = fp['counts']
        self.assertEqual(c['placed'], len(fp['items']))
        self.assertGreaterEqual(c['placed'], 1)
        self.assertLessEqual(c['placed'], c['wanted'])
        self.assertEqual(c['wanted'], Firepits.count_for(c['land_m2']))
        for item in fp['items']:
            self.assertEqual(len(item['position_m']), 3)
            self.assertIsInstance(item['why'], str)

    def test_the_written_package_proves_them_and_a_wrong_claim_fails(self):
        from pipeline.landscape.harness import read_package
        doc, meshes, room, _ = read_package(self.folder, ROOMS/'garage_nominal')
        terrain = checks.Terrain(doc, meshes)
        ok, rows = checks.firepit_checks(doc, meshes, terrain, room)
        self.assertTrue(ok, rows)
        self.assertEqual(rows['placed'], len(doc['x_generator']['firepits']['items']))
        items = doc['x_generator']['firepits']['items']
        spawn = next(s for s in room['spawns'] if s['role'] == 'player')['position_m']
        wrong = {
            'in the sea': dict(items[0], position_m=[room['bounds']['max_m'][0]-.05, -.05, items[0]['position_m'][2]]),
            'beside another': dict(items[0], position_m=[items[1]['position_m'][0]+1., items[1]['position_m'][1],
                                                         items[1]['position_m'][2]]),
            'at the start': dict(items[0], position_m=[spawn[0]+.5, items[0]['position_m'][1], spawn[2]]),
            'the wrong band': dict(items[0], reach_band=5),
        }
        for name, item in wrong.items():
            with self.subTest(name):
                bad = copy.deepcopy(doc)
                bad['x_generator']['firepits']['items'][0] = item
                ok, rows = checks.firepit_checks(bad, meshes, checks.Terrain(bad, meshes), room)
                self.assertFalse(ok, name)
                self.assertGreaterEqual(rows['failing'], 1, name)


def tearDownModule():
    test_generator.tearDownModule()


if __name__ == '__main__':
    unittest.main()
