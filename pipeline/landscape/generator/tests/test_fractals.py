"""Fractals and gates (the mechanics round, packet M4): the stamina bands and the two kinds of gate, the flights built into
the grid, the climb-aware reach proved by the exact walk, the Fractals' ids, and the 1x world left as it was.

    python -B -m unittest pipeline.landscape.generator.tests.test_fractals -v
"""
import json
import math
import re
import unittest
from pathlib import Path

from pipeline.landscape.generator import checks, climb, fractals as Fractals, gates, reach, routes as R, sites
from pipeline.landscape.generator.field import Grid, face_slope_max
from pipeline.landscape.generator.generate import generate
from pipeline.landscape.generator.tests.test_generator import ROOMS, garage

ROOT = Path(__file__).resolve().parents[4]
V5 = ROOT/'game'/'rules'/'storybook_wild'/'v5.json'


def pack_stamina():
    """The rules pack's stamina block (storybook_wild v5): the numbers the game's body climbs by."""
    return json.loads(V5.read_text(encoding='utf-8'))['traits']['stamina']


def pack_climb(band, stamina=None):
    """The metres of rise the pack's own body climbs on a full bar of `band` bands, worked out from the pack alone: the
    bar's length (start_m and a band_m more a band) times climb_m_per_bar_m (absent: 1, as the kernel reads it)."""
    s = stamina or pack_stamina()
    return (s['start_m']+(band-1)*s['band_m'])*s.get('climb_m_per_bar_m', 1)


def pack_body():
    """The pack's own body as a stretch of `gates.cap`: 1 while the gates carry the pack's climb_m_per_bar_m (before
    BODY-21 they carried none, and this was 1.5: the body out-climbed every gate claim by half)."""
    return pack_climb(1)/gates.cap(1)


# ------------------------------------------------------------------------------------------- a synthetic island


class Everywhere:
    """A stand-in for the exact walk's flood: every place inside the floor is reached."""

    def __init__(self, half):
        self.half = half

    def has(self, p):
        return abs(p[0]) < self.half-.5 and abs(p[1]) < self.half-.5


def synthetic_land(half=14., level=1.):
    """A flat island, `half` metres each way from the middle (the player arrives there), nothing on it."""
    g = Grid(-half, -half, half, half, .1)
    n = g.n
    land = sites.Land(g, [level]*n, [True]*n, [9.]*n, [False]*n, [False]*n, [False]*n, [0.]*n, [False]*n, [0.]*n,
                      [False]*n, [0.]*n, set(), [(0., 0.)], Everywhere(half), [])
    return land


def written(grid, h, half):
    """The land as a package would hold it (a walker reads these), and the room round it."""
    nx = grid.nx
    pos = [[grid.xs[q % nx], h[q], grid.zs[q//nx]] for q in range(grid.n)]
    meshes = {'land': [dict(role='meadow', positions=pos, triangles=[list(t) for t in grid.triangles()])]}
    doc = dict(terrain=[{'mesh': 'land'}], scenery=[], water=[], scatter=[], prototypes={}, objects=[],
               x_generator=dict(cell_m=grid.cell, world_scale=10.))
    room = dict(bounds=dict(min_m=[-half+.5, 0, -half+.5], max_m=[half-.5, 40, half-.5]), shell=dict(parts=[]), spawns=[])
    return doc, meshes, room


def climb_reach(land, half, spawn=(0., 0.)):
    doc, meshes, room = written(land.grid, land.h, half)
    terrain = checks.Terrain(doc, meshes)
    walker = checks.Walker(doc, meshes, terrain, room)
    return reach.ClimbReach(walker, walker.reach(spawn), land.grid, land.h)


SMALL = dict(min=2, max=40, reachable_first_run=6)
RECIPE = dict(ladders=((1, 2, 3),), shortcuts=(1, 2), steps=2)


class Island(unittest.TestCase):
    """One synthetic island with a ladder, two shortcuts and two steps, built and walked once."""

    @classmethod
    def setUpClass(cls):
        cls.half = 14.
        cls.land = synthetic_land(cls.half)
        cls.plan = Fractals.plan(cls.land, 7, (0., 0.), RECIPE)
        cls.cr = climb_reach(cls.land, cls.half)
        cls.block = Fractals.finish(cls.plan, cls.cr, (0., 0.), budget=SMALL)

    def test_every_flight_is_placed_and_every_bench_proved(self):
        self.assertEqual(len(self.plan.routes), 5, self.plan.notes)
        self.assertEqual(self.block['counts']['benches'], 3+1+1+2)
        self.assertEqual(self.block['counts']['benches_proved'], self.block['counts']['benches'])
        for route in self.block['routes']:
            for ledge in route['ledges']:
                self.assertEqual(ledge['reached_from']['base'], ledge['expected_band'], route['id'])

    def test_the_counts_and_the_notes_say_what_the_land_offered(self):
        c = self.block['counts']
        self.assertEqual(c['first_run']+c['out_of_reach'], c['placed'])
        self.assertEqual(sum(c['by_why'].values()), c['placed'])
        self.assertEqual(c['first_run'], sum(1 for i in self.block['items'] if i.get('reach_band') == 1))
        self.assertTrue(any('under the 10' in n for n in self.block['notes']), self.block['notes'])
        big = Fractals.finish(self.plan, self.cr, (0., 0.), budget=dict(SMALL, min=30))
        self.assertTrue(any('under the budget minimum of 30' in n for n in big['notes']), big['notes'])

    def test_the_ladder_is_the_same_for_everyone(self):
        """Band gates: bands 1, 2, 3 for the base body and one knack at full rank (no knack opens one early); the pair opens
        none below band 4's arithmetic (it opens a gate early only from band 4 on). The rises are the mended gates': 0.58,
        1.18 and 1.78 m, a course (0.6 m) more each."""
        ladder = next(r for r in self.block['routes'] if r['kind'] == 'ladder')
        for k, ledge in enumerate(ladder['ledges']):
            self.assertEqual(ledge['reached_from'], dict(base=k+1, one_knack=k+1, pair=k+1))
            self.assertEqual(ledge['opened_early_by'], {})
        self.assertEqual([l['rise_m'] for l in ladder['ledges']], [.58, 1.18, 1.78])

    def test_the_packs_own_body_takes_every_gate_from_the_band_it_was_built_as(self):
        """BODY-21, by the exact walk: the body the rules pack describes (`pack_body`: its bar times climb_m_per_bar_m)
        stands on every bench from the band it was built as, and on no Fractal from below the band it says. Before, that body
        climbed half as far again as the gates were sized for: a band-2 bar stood on band 3's ledge."""
        real = self.cr.run(pack_body())
        for route in self.block['routes']:
            for k, ledge in enumerate(route['ledges']):
                x, _, z = ledge['bench_m']
                self.assertEqual(real.band_at(x, z), ledge['expected_band'],
                                 '%s ledge %d (rise %.2f m), built as band %d' % (route['id'], k+1, ledge['rise_m'], ledge['band']))
        for item in self.block['items']:
            x, _, z = item['position_m']
            self.assertEqual(real.band_at(x, z), item.get('reach_band'), item)

    def test_a_shortcut_is_a_band_sooner_with_a_knack_and_never_for_the_base_body(self):
        self.assertEqual(len(self.block['shortcuts']), 2)
        for s in self.block['shortcuts']:
            self.assertEqual(s['one_knack_band'], s['base_band']-1, s)
            self.assertEqual(s['base_band'], s['over_band']+1, s)
            self.assertGreater(s['base_falls_short_m'], .02)
            self.assertGreater(s['knack_clears_by_m'], .02)
            # the pack's own body is the base body: it falls short too (before BODY-21 it cleared both shortcuts)
            self.assertGreater(s['rise_m'], pack_climb(s['over_band'])+.02)
            # even the strongest pair does not carry a shortcut into the next band's gate
            self.assertGreaterEqual(s['pair_band'], s['one_knack_band']-1)
            self.assertGreaterEqual(s['pair_band'], 1)
        by_band = {s['over_band']: s for s in self.block['shortcuts']}
        self.assertEqual((by_band[1]['base_band'], by_band[1]['one_knack_band']), (2, 1))
        self.assertEqual((by_band[2]['base_band'], by_band[2]['one_knack_band']), (3, 2))

    def test_nothing_is_reachable_only_with_a_knack(self):
        base = self.cr.run(1.)
        for factor in (gates.FULL_RANK, gates.FULL_RANK**2):
            other = self.cr.run(factor)
            self.assertEqual(sum(1 for b in other.label if b), sum(1 for b in base.label if b), factor)

    def test_every_fractal_is_on_a_standable_node_the_walk_reaches_from_the_band_it_says(self):
        base = self.cr.run(1.)
        self.assertGreaterEqual(len(self.block['items']), 5)
        for item in self.block['items']:
            x, y, z = item['position_m']
            self.assertEqual(base.band_at(x, z), item.get('reach_band'), item)
            self.assertAlmostEqual(y, self.land.grid.tri_height(self.land.h, x, z), places=2)

    def test_ids_come_from_the_place_never_a_count(self):
        ids = [it['id'] for it in self.block['items']]
        self.assertEqual(len(set(ids)), len(ids))
        for it in self.block['items']:
            self.assertRegex(it['id'], r'^fractal:[a-z0-9]{1,32}$')
            self.assertEqual(it['id'], Fractals.fractal_id(it['position_m'][0], it['position_m'][2]))
        self.assertEqual(ids, sorted(ids), 'the list is in id order: it says nothing of the order they were placed in')
        # Another Fractal elsewhere changes no other's id; the same place is the same id; a metre away is another.
        self.assertEqual(Fractals.fractal_id(1.234, 5.678), Fractals.fractal_id(1.234, 5.678))
        self.assertNotEqual(Fractals.fractal_id(1.234, 5.678), Fractals.fractal_id(1.234, 6.678))
        self.assertEqual(Fractals.fractal_id(1.2301, 5.6789), Fractals.fractal_id(1.2299, 5.6791), 'centimetres')

    def test_untyped_by_default_and_a_kind_each_when_typed(self):
        self.assertFalse(self.block['typed'])
        self.assertTrue(all('kind' not in it for it in self.block['items']))
        typed = Fractals.finish(self.plan, self.cr, (0., 0.), typed=True, budget=SMALL)
        self.assertTrue(typed['typed'])
        self.assertTrue(all(it['kind'] in ('heart', 'band') for it in typed['items']))
        self.assertEqual([it['id'] for it in typed['items']], [it['id'] for it in self.block['items']])

    def test_the_first_fractal_is_near_the_arrival(self):
        steps = [d for d in self.block['detail'] if d['why'] == 'step']
        first = min(math.hypot(*[c for c in next(i for i in self.block['items'] if i['id'] == d['id'])['position_m'][::2]])
                    for d in steps)
        self.assertLess(first, 20.)

    def test_the_written_package_proves_the_same_and_a_wrong_claim_fails(self):
        """checks.fractal_checks reads the package back (no knowledge of how it was built) and holds every bench and every
        Fractal to the band it says."""
        import copy
        doc, meshes, room = written(self.land.grid, self.land.h, self.half)
        room['spawns'] = [dict(id='player_start', role='player', position_m=[0., 1., 0.])]
        doc['x_generator']['fractals'] = self.block
        terrain = checks.Terrain(doc, meshes)
        ok, rows = checks.fractal_checks(doc, meshes, terrain, room)
        self.assertTrue(ok, rows)
        self.assertEqual(rows['placed'], len(self.block['items']))
        self.assertEqual(rows['failing'], 0)
        # a Fractal that says the wrong band, a bench built as the wrong band
        for what in ('item', 'bench'):
            wrong = copy.deepcopy(self.block)
            if what == 'item':
                wrong['items'][0]['reach_band'] = 7
            else:
                wrong['routes'][0]['ledges'][-1]['band'] += 2
            doc['x_generator']['fractals'] = wrong
            ok, rows = checks.fractal_checks(doc, meshes, checks.Terrain(doc, meshes), room)
            self.assertFalse(ok, what)
            self.assertEqual(rows['failing'], 1, what)

    def test_a_gate_built_too_low_is_found_out(self):
        """The exact walk's proof is not taken on trust: a ladder whose second ledge is only a shortcut's rise higher than
        a body of band 1 climbs reports the band that truly reaches it, not the band it was built as."""
        land = synthetic_land(self.half)
        g = land.grid
        # band 2's gate built with a band-1 rise: ledge 2 is reached from band 1, not 2
        low = R.Route('ladder', 150, 140, 1, 0, 1., R.make_ledges([(gates.gate_rise(1), 1), (gates.gate_rise(1), 2)], g.cell),
                      ident='low')
        low.apply(g, land.h)
        plan = Fractals.Plan(land, [low], None, [])
        cr = climb_reach(land, self.half)
        block = Fractals.finish(plan, cr, (0., 0.), budget=SMALL)
        row = block['routes'][0]['ledges'][1]
        self.assertFalse(row['proved'])
        self.assertEqual(row['reached_from']['base'], 1)
        self.assertEqual(row['expected_band'], 2)
        self.assertTrue(row.get('bypassed'))
        item = next(d for d in block['detail'] if d.get('route') == 'low' and d['ledge'] == 2)
        self.assertEqual(item['reach_band'], 1, 'a Fractal says the band that truly reaches it')


class Summit(unittest.TestCase):
    """The summit's corridor: a flight cut through a landmark's cliff to its top, walled in, upper ledges at the higher
    bands, its platform reached only from the band its ladder needs. The mesa stands 5.8 m over the plain: with the mended
    gates (0.58, 1.18, 1.78 m) its ladder is four ledges, bands 1 to 4 (it was five when a gate was a metre of bar)."""

    @classmethod
    def setUpClass(cls):
        cls.half = 20.
        land = synthetic_land(cls.half, level=1.)
        g = land.grid
        nx = g.nx
        cls.top = 6.8
        # A mesa 8 by 11 m, six metres above the plain, its sides sheer (a cliff of one cell), protected ground.
        for j in range(g.nz):
            for i in range(nx):
                x, z = g.xs[i], g.zs[j]
                if abs(x) <= 4. and -12. <= z <= -1.:
                    land.h[j*nx+i] = cls.top
                    land.protect[j*nx+i] = True
        for q in range(g.n):
            land.fslope[q] = 0.
        land.objects = [dict(id='mesa_1', kind='table', cx=0., cz=-6.5, sx=8., sz=11., sy=cls.top-1., base=1., top=cls.top,
                             yaw=0., parent=None, form='mesa')]
        cls.land = land
        cls.original = list(land.h)
        # The player arrives in the south, clear of the mesa.
        land.views = [(0., 12.)]
        # An island whose own Fractals open band 3 at most (10 of them, five a band, from band 1): a corridor to band 4 is
        # beyond them as it stands, so no stair is built above it.
        cls.budget = dict(min=1, max=10, reachable_first_run=0, per_band=5)
        cls.plan = Fractals.plan(land, 3, (0., 12.), dict(ladders=(), shortcuts=(), steps=0), budget=cls.budget)
        cls.cr = climb_reach(land, cls.half, (0., 12.))
        cls.block = Fractals.finish(cls.plan, cls.cr, (0., 12.), budget=cls.budget)

    def test_a_corridor_is_cut_to_the_top(self):
        self.assertIsNotNone(self.plan.summit, self.plan.notes)
        summit = self.plan.summit
        self.assertEqual(summit.landmark, 'mesa_1')
        rises = [l.rise for l in summit.ledges]
        self.assertAlmostEqual(sum(rises), self.top-summit.z0, places=6)
        self.assertEqual([l.band for l in summit.ledges], [1, 2, 3, 4])
        self.assertEqual([round(r, 2) for r in rises], [.58, 1.18, 1.78, 2.26], 'three gates and what is left')
        row = self.block['summit']
        self.assertEqual(row['landmark'], 'mesa_1')
        self.assertAlmostEqual(row['height_m'], self.top-summit.z0, places=3)

    def test_it_is_walled_in_so_no_way_round_it_exists(self):
        """The land beside the corridor stands over it where it is cut; and no band reaches the platform except by it:
        the mesa's top is reached from the band the ladder needs, and from no lower one."""
        row = self.block['summit']
        top_band = max(l['band'] for l in self.block['routes'][0]['ledges'])
        self.assertEqual(row['reached_from']['base'], top_band)
        self.assertEqual(top_band, row['top_band'])
        # the mesa's top beyond the corridor is reached from the corridor's top band, and from no lower one: the cliff
        # all round it is 5.8 m, which only the top band's bar climbs (6.0 m), long after the ladder's last ledge
        base = self.cr.run(1.)
        for x, z in ((0., -4.), (0., -7.), (-3., -9.)):
            self.assertEqual(base.band_at(x, z), top_band, (x, z))
        # ... and the land beside the corridor, at the foot, is reached from band 1 only (the cliff there is no way up)
        self.assertEqual(base.band_at(-6., -6.), 1)

    def test_every_ledge_of_the_corridor_is_proved(self):
        ledges = self.block['routes'][0]['ledges']
        self.assertTrue(all(l['proved'] for l in ledges))
        self.assertEqual([l['reached_from']['base'] for l in ledges], [1, 2, 3, 4])
        # ... by the pack's own body too (BODY-21): no ledge of the corridor from below its band
        real = self.cr.run(pack_body())
        self.assertEqual([real.band_at(l['bench_m'][0], l['bench_m'][2]) for l in ledges], [1, 2, 3, 4])

    def test_the_summit_holds_a_fractal_on_its_platform(self):
        summit = [d for d in self.block['detail'] if d['why'] == 'summit']
        self.assertEqual(len(summit), 1)
        self.assertEqual(summit[0]['reach_band'], self.block['summit']['reached_from']['base'])

    def test_no_stair_where_the_corridor_is_beyond_the_islands_own_bands_already(self):
        self.assertIsNone(self.plan.summit.tower)
        self.assertEqual(Fractals.top_band(self.plan.summit), 4)
        self.assertGreater(Fractals.top_band(self.plan.summit), gates.own_band(self.budget['max']))
        row = self.block['summit']
        self.assertEqual((row['built_ledges'], row['top_band'], row['own_band']), (0, 4, 3))
        self.assertEqual(self.plan.notes, [])

    def test_a_plateau_a_bar_climbs_is_no_lift_place(self):
        """The mesa stands 5.8 m over the plain and its corridor reaches the top, so no Fractal is put there as if nothing
        reached it (and its cliff, 5.8 m, is one the top band's bar climbs: 6.0 m)."""
        base = self.cr.run(1.)
        spots = Fractals.lift_places(self.land, base, self.plan.keep, [], 4)
        g = self.land.grid
        for x, z in spots:
            i, j = g.nearest(x, z)
            self.assertNotIn(j*g.nx+i, self.plan.keep)
        self.assertEqual(spots, [])
        self.assertNotIn('high', self.block['counts']['by_why'])

    def test_a_cliff_without_a_wall_is_not_a_corridor(self):
        """A landmark whose flank is a hill (no cliff to cut into) is passed over: the corridor is walled in or it is not built."""
        g = self.land.grid
        h = [1.]*g.n
        nx = g.nx
        for j in range(g.nz):
            for i in range(nx):
                x, z = g.xs[i], g.zs[j]
                d = max(abs(x)-4., abs(z+6.5)-5.5, 0.)   # a hill: 6 m over 30 m, 11 degrees
                h[j*nx+i] = max(1., 6.8-d*.2)
        land = synthetic_land(self.half)
        land.h = h
        land.objects = self.land.objects
        land.views = [(0., 12.)]
        siting = sites.Siting(land)
        self.assertIsNone(sites.plan_summit(land, siting, land.objects[0], lambda height: gates.ladder_rises(height)))


class SummitStair(unittest.TestCase):
    """Where the landmark's own ladder tops out at no more than the bands the island's own Fractals open (a mesa 5.8 m over
    the plain: bands 1 to 4, against the 7 that 30 Fractals open), the ladder goes on above the corridor's platform as a
    stair built on the landmark's top, one band more a ledge, so the summit needs a return visit (decided 15 October).
    With the mended gates the stair is four ledges, bands 5 to 8: 15.5 m of it, 11.9 m long."""

    @classmethod
    def setUpClass(cls):
        cls.half = 22.
        land = synthetic_land(cls.half, level=1.)
        g = land.grid
        nx = g.nx
        cls.top = 6.8
        # A mesa 22 by 11 m, sheer (a cliff of one cell), protected ground: wide enough for a stair across its top.
        for j in range(g.nz):
            for i in range(nx):
                x, z = g.xs[i], g.zs[j]
                if abs(x) <= 11. and -12. <= z <= -1.:
                    land.h[j*nx+i] = cls.top
                    land.protect[j*nx+i] = True
        for q in range(g.n):
            land.fslope[q] = 0.
        land.objects = [dict(id='mesa_1', kind='table', cx=0., cz=-6.5, sx=22., sz=11., sy=cls.top-1., base=1., top=cls.top,
                             yaw=0., parent=None, form='mesa')]
        land.views = [(0., 18.)]
        cls.land = land
        cls.original = list(land.h)
        cls.plan = Fractals.plan(land, 3, (0., 18.), dict(ladders=(), shortcuts=(), steps=0))
        cls.cr = climb_reach(land, cls.half, (0., 18.))
        cls.block = Fractals.finish(cls.plan, cls.cr, (0., 18.), budget=dict(Fractals.BUDGET, min=1))

    def test_the_stair_goes_on_one_band_more_a_ledge_to_more_than_the_island_opens(self):
        summit = self.plan.summit
        self.assertIsNotNone(summit.tower, self.plan.notes)
        self.assertEqual(self.plan.notes, [])
        self.assertEqual([l.band for l in summit.ledges], [1, 2, 3, 4])
        self.assertEqual([l.band for l in summit.tower.ledges], [5, 6, 7, 8])
        own = gates.own_band(Fractals.BUDGET['max'])
        self.assertEqual(own, 7, '30 Fractals, five a band, from band 1')
        self.assertEqual(Fractals.top_band(summit), own+1)
        # each rise is the band's gate, the stair's first foot is the corridor's platform
        self.assertEqual([round(l.rise, 2) for l in summit.tower.ledges], [2.98, 3.58, 4.18, 4.78])
        self.assertAlmostEqual(summit.tower.z0, summit.levels[-1], places=9)
        self.assertEqual(self.plan.summit_routes(), [summit, summit.tower])
        self.assertEqual(summit.tower.id, 'summit_top')

    def test_a_mesa_with_room_all_round_keeps_the_narrowest_stair(self):
        self.assertEqual(self.plan.summit.tower.half, R.HALF_CELLS)

    def test_it_stands_on_the_landmarks_top_and_leaves_the_plain_alone(self):
        g = self.land.grid
        tower = self.plan.summit.tower
        z_p = tower.z0
        for q, s, m in tower.cells(g.nx):
            self.assertGreaterEqual(self.original[q], z_p-sites.TOWER_DOWN_M, 'on the top, not over an edge')
            self.assertLessEqual(self.original[q], z_p+sites.TOWER_UP_M)
            self.assertIn(q, self.plan.keep, 'the planting keeps off it')
        # beyond the two flights and their skirts the land is as it was
        near = set()
        for rt in self.plan.summit_routes():
            i0, i1, j0, j1 = sites.flight_box(rt, 6)
            near.update(j*g.nx+i for j in range(j0, j1+1) for i in range(i0, i1+1))
        changed = [q for q in range(g.n) if self.land.h[q] != self.original[q] and q not in near]
        self.assertEqual(changed, [])

    def test_every_ledge_of_both_flights_is_proved_by_the_exact_walk(self):
        rows = {r['id']: r for r in self.block['routes']}
        self.assertEqual([l['reached_from']['base'] for l in rows['summit']['ledges']], [1, 2, 3, 4])
        self.assertEqual([l['reached_from']['base'] for l in rows['summit_top']['ledges']], [5, 6, 7, 8])
        self.assertTrue(all(l['proved'] for r in rows.values() for l in r['ledges']))
        # the pack's own body (BODY-21) stands on the summit from band 8 and no sooner: beyond the 7 the island opens
        real = self.cr.run(pack_body())
        top_bench = rows['summit_top']['ledges'][-1]['bench_m']
        self.assertEqual(real.band_at(top_bench[0], top_bench[2]), 8)
        self.assertGreater(real.band_at(top_bench[0], top_bench[2]), gates.own_band(Fractals.BUDGET['max']))
        # the top is reached from band 8, and a knack at full rank does not carry it a band sooner than 7
        top = rows['summit_top']['ledges'][-1]['reached_from']
        self.assertEqual((top['base'], top['one_knack']), (8, 7))

    def test_no_way_round_the_stair_the_plateau_is_reached_from_the_corridors_band(self):
        base = self.cr.run(1.)
        tower = self.plan.summit.tower
        g = self.land.grid
        # a place on the mesa's top well clear of the corridor and the stair is reached from the corridor platform's band
        keep = self.plan.keep

        def clear_of_flights(x, z):
            i, j = g.nearest(x, z)
            return not any(b*g.nx+a in keep for b in range(j-10, j+11) for a in range(i-10, i+11))
        spot = next((x, z) for x in (-9., 9., -6., 6.) for z in (-3., -9., -6.) if clear_of_flights(x, z))
        self.assertEqual(base.band_at(*spot), 4, "the mesa top beyond the stair is the corridor platform's level")
        self.assertEqual(base.band_at(*tower.bench_centre(g, 0)), 5)
        self.assertEqual(base.band_at(*tower.bench_centre(g, 1)), 6)
        self.assertEqual(base.band_at(*tower.bench_centre(g, 2)), 7)
        self.assertEqual(base.band_at(*tower.bench_centre(g, 3)), 8)

    def test_a_fractal_on_every_ledge_of_the_stair_and_the_summit_on_its_top(self):
        by_why = self.block['counts']['by_why']
        self.assertEqual((by_why.get('stair'), by_why.get('summit')), (3, 1))
        mine = {d['ledge']: d for d in self.block['detail'] if d.get('route') == 'summit_top'}
        self.assertEqual({k: d['reach_band'] for k, d in mine.items()}, {1: 5, 2: 6, 3: 7, 4: 8})
        self.assertEqual(mine[4]['why'], 'summit')
        self.assertFalse([d for d in self.block['detail'] if d.get('route') == 'summit'], 'none on the corridor itself')

    def test_the_summit_record_the_manifest_carries(self):
        rec = Fractals.summit_record(self.block)
        tower = self.plan.summit.tower
        g = self.land.grid
        cx, cz = tower.bench_centre(g, len(tower.ledges)-1)
        self.assertEqual(sorted(rec), ['center_m', 'landmark', 'radius_m', 'top_band'])
        self.assertEqual(rec['landmark'], 'mesa_1')
        self.assertEqual(rec['top_band'], 8)
        self.assertAlmostEqual(rec['center_m'][0], cx, places=3)
        self.assertAlmostEqual(rec['center_m'][2], cz, places=3)
        self.assertAlmostEqual(rec['center_m'][1], tower.levels[-1], places=3, msg='on its ground')
        # the radius covers the whole top bench (with a body's disc to spare), and no more than a bench
        top = tower.ledges[-1]
        first = tower.starts[-1]+top.riser_cells
        worst = max(math.hypot(g.xs[q % g.nx]-cx, g.zs[q//g.nx]-cz)
                    for s in range(first, first+top.bench_cells+1) for m in range(-tower.half, tower.half+1)
                    for q in (tower.vertex(s, m, g.nx),))
        self.assertGreaterEqual(rec['radius_m'], worst+sites.SUMMIT_BODY_M-1e-6)
        self.assertLess(rec['radius_m'], worst+sites.SUMMIT_BODY_M+sites.SUMMIT_RADIUS_STEP_M+1e-6)
        row = self.block['summit']
        self.assertEqual((row['cut_ledges'], row['built_ledges'], row['ledges'], row['top_band'], row['own_band']), (4, 4, 8, 8, 7))

    def test_the_landmark_still_reads_in_the_land_without_the_stair_the_island_built_over_it(self):
        """The room's reading rule (every object's footprint reads in the land: its top within a third of its height of the
        box's) is the room's, not the gate's: it is read without the ground under the stair, and with it the landmark is
        lost (a stair 15.5 m over its top)."""
        doc, meshes, room = written(self.land.grid, self.land.h, self.half)
        doc['x_generator']['fractals'] = self.block
        rects = checks.stair_rects(doc)
        self.assertEqual(len(rects), 1)
        g = self.land.grid
        tower = self.plan.summit.tower
        x0, x1, z0, z1 = rects[0]
        for q, s, m in tower.cells(g.nx):
            self.assertTrue(x0 <= g.xs[q % g.nx] <= x1 and z0 <= g.zs[q//g.nx] <= z1, 'every cell of the stair is under it')
        # ... and only the stair: a flight of the ladders' kind is not one
        self.assertEqual([r['id'] for r in doc['x_generator']['fractals']['routes'] if r.get('stair')], ['summit_top'])
        inventory = dict(objects=[dict(id='mesa_1', kind='table', placement=dict(support=dict(kind='floor')),
                                       box=dict(size_m=[22., 5.8, 11.], centre_m=[0., 3.9, -6.5], yaw_deg=0.))])
        terrain = checks.Terrain(doc, meshes)
        lost, rows = checks.footprint_checks(inventory, terrain, room, 1.)
        self.assertFalse(lost, rows)
        self.assertGreater(rows[0]['top_delta_m'], 15., "the stair stands 15.5 m over the box's top")
        reads, rows = checks.footprint_checks(inventory, terrain, room, 1., rects)
        self.assertTrue(reads, rows)
        self.assertLess(abs(rows[0]['top_delta_m']), 1., 'the mesa itself is where its box says')

    def test_the_written_package_proves_the_summit_and_a_wrong_one_fails(self):
        import copy
        doc, meshes, room = written(self.land.grid, self.land.h, self.half)
        room['spawns'] = [dict(id='player_start', role='player', position_m=[0., 1., 18.])]
        doc['x_generator']['fractals'] = self.block
        doc['x_generator']['summit'] = Fractals.summit_record(self.block)
        ok, rows = checks.fractal_checks(doc, meshes, checks.Terrain(doc, meshes), room)
        self.assertTrue(ok, rows)
        self.assertEqual(rows['failing'], 0)
        for what, change in (('a centre off its ground', lambda s: s['center_m'].__setitem__(1, s['center_m'][1]+.5)),
                             ('a centre outside the island', lambda s: s['center_m'].__setitem__(0, 400.)),
                             ('no width', lambda s: s.update(radius_m=0))):
            wrong = copy.deepcopy(doc['x_generator']['summit'])
            change(wrong)
            doc['x_generator']['summit'] = wrong
            ok, rows = checks.fractal_checks(doc, meshes, checks.Terrain(doc, meshes), room)
            self.assertFalse(ok, what)
            self.assertEqual(rows['failing'], 1, what)


class SummitShort(unittest.TestCase):
    """A landmark with room for the corridor and none for the stair (a mesa 9 by 8 m: the corridor runs 7.6 m, the stair
    11.9 m across it) keeps its corridor as it stands, and the island says so."""
    half = 20.

    @classmethod
    def mesa(cls):
        land = synthetic_land(cls.half, level=1.)
        g = land.grid
        nx = g.nx
        for j in range(g.nz):
            for i in range(nx):
                x, z = g.xs[i], g.zs[j]
                if abs(x) <= 4.5 and -12. <= z <= -4.:
                    land.h[j*nx+i] = 6.8
                    land.protect[j*nx+i] = True
        for q in range(g.n):
            land.fslope[q] = 0.
        land.objects = [dict(id='mesa_1', kind='table', cx=0., cz=-8., sx=9., sz=8., sy=5.8, base=1., top=6.8,
                             yaw=0., parent=None, form='mesa')]
        land.views = [(0., 12.)]
        return land

    @classmethod
    def setUpClass(cls):
        cls.land = cls.mesa()
        cls.plan = Fractals.plan(cls.land, 3, (0., 12.), dict(ladders=(), shortcuts=(), steps=0))
        cls.block = Fractals.finish(cls.plan, climb_reach(cls.land, cls.half, (0., 12.)), (0., 12.),
                                    budget=dict(Fractals.BUDGET, min=1))

    def test_the_corridor_stands_alone_and_the_report_says_the_island_cannot_make_it_wait(self):
        self.assertIsNotNone(self.plan.summit)
        self.assertIsNone(self.plan.summit.tower)
        self.assertEqual(Fractals.top_band(self.plan.summit), 4)
        self.assertTrue(any('beyond the 7 bands' in n for n in self.plan.notes), self.plan.notes)
        row = self.block['summit']
        self.assertEqual((row['built_ledges'], row['top_band'], row['own_band']), (0, 4, 7))
        self.assertEqual(Fractals.summit_record(self.block)['top_band'], 4)
        self.assertIn('within them', Fractals.report_text(self.block))

    def test_a_stair_over_an_edge_is_refused(self):
        land = self.mesa()    # as it was: the planned land has its corridor built in
        siting = sites.Siting(land)
        corridor = sites.plan_summit(land, siting, land.objects[0], lambda height: gates.ladder_rises(height))
        stats = {}
        bands = gates.stair_bands(max(l.band for l in corridor.ledges), gates.own_band(Fractals.BUDGET['max']))
        self.assertEqual(bands, [5, 6, 7, 8])
        self.assertIsNone(sites.plan_tower(land, siting, corridor, bands, land.objects[0], stats))
        self.assertTrue(any(k.startswith('stair:') for k in stats), stats)


class StairLedges(unittest.TestCase):
    """A stair built over a landform's top must leave no ledge beside it too narrow to be ground: the game's navigation bake
    cannot triangulate a ribbon of a few centimetres (it makes overlapping slivers, and the map reports edges more than
    two polygons share: the 10x garage's first stair did, on the crest's north rim, and the scale suite's runner failed)."""

    def lined(self, ledge_cells):
        """A flat island and a stair along +x at z = 0; the ground beside its two sides is walkable for `ledge_cells`
        vertices and then too steep to walk."""
        land = synthetic_land(10.)
        g = land.grid
        route = R.Route('summit', 150, 100, 1, 0, 1., R.make_ledges([(gates.gate_rise(6), 6)], g.cell, last_bench_m=1.6),
                        ident='summit_top')
        route.stair = True
        edge = route.half
        for j in range(g.nz):
            for i in range(g.nx):
                row = abs(j-route.j0)
                land.fslope[j*g.nx+i] = 0. if row <= edge+ledge_cells else 60.
        return land, route

    def test_a_ledge_of_a_few_centimetres_is_refused_and_none_or_a_wide_one_is_not(self):
        need = int(math.ceil(sites.LEDGE_MIN_M/.1))
        for cells, ok in ((0, True), (1, False), (3, False), (need-1, False), (need, True), (sites.LEDGE_LOOK_CELLS, True)):
            land, route = self.lined(cells)
            if cells == 0:
                # no ledge: the ground beside the stair is too steep to walk from the first vertex on
                for q in range(land.grid.n):
                    j = q//land.grid.nx
                    land.fslope[q] = 0. if abs(j-route.j0) <= route.half else 60.
            self.assertEqual(sites._ledge_ok(land, route), ok, cells)

    def test_the_widths_a_stair_may_take_run_narrowest_first(self):
        self.assertEqual(sites.TOWER_HALF_CELLS[0], R.HALF_CELLS)
        self.assertEqual(list(sites.TOWER_HALF_CELLS), sorted(sites.TOWER_HALF_CELLS))


class LiftPlaces(unittest.TestCase):
    """Places only the Gubble's lift or a built ladder reaches: on a plateau cliffs close in all round, whose rim stands
    over every bar's tallest climb, spread over the island; never on a hill, a low mesa, a flight or the water."""

    @classmethod
    def setUpClass(cls):
        from pipeline.landscape.generator.field import slope_field
        cls.half = 24.
        land = synthetic_land(cls.half, level=1.)
        g = land.grid
        nx = g.nx

        def stand(cx, cz, r, top):
            for j in range(g.nz):
                for i in range(nx):
                    if abs(g.xs[i]-cx) <= r and abs(g.zs[j]-cz) <= r:
                        land.h[j*nx+i] = top
        stand(-14., -12., 3., 9.)      # tall: a lift place
        stand(12., -14., 3., 9.5)      # tall, far from the first: another
        stand(0., -4., 3., 3.5)        # low: a bar climbs it (it stands on the hill's long flank, half a metre over it)
        stand(-19., 0., 3., 5.4)       # 4.4 m over the plain: a bar of band 8 climbs it (4.8 m), and its rim is under 4.5 m
        stand(-12., 12., 3., 8.)       # tall, but a flight stands on it (kept)
        # a hill the body walks up: a 20 degree flank to a flat top
        for j in range(g.nz):
            for i in range(nx):
                d = max(abs(g.xs[i]-14.)-2., abs(g.zs[j]-12.)-2., 0.)
                land.h[j*nx+i] = max(land.h[j*nx+i], 8.-d*.36)
        land.fslope = slope_field(g, land.h)
        cls.land = land
        cls.cr = climb_reach(land, cls.half, (0., 20.))
        cls.base = cls.cr.run(1.)

    def spots(self, **kw):
        kw.setdefault('keep', ())
        kw.setdefault('taken', [(0., 20.)])
        kw.setdefault('count', 4)
        return Fractals.lift_places(self.land, self.base, **kw)

    def test_tall_plateaus_cliffs_close_in_are_lift_places_and_hills_and_low_mesas_are_not(self):
        got = self.spots()
        near = lambda p, q: math.hypot(p[0]-q[0], p[1]-q[1]) < 5.
        self.assertEqual(len(got), 3, got)
        for t in [(-14., -12.), (12., -14.), (-12., 12.)]:
            self.assertTrue(any(near(p, t) for p in got), (t, got))
        self.assertFalse(any(near(p, (-19., 0.)) for p in got), 'a cliff of 4.4 m: a bar climbs it, and it is under the rim a lift place needs')
        # every lift place stands over the tallest climb of the pack's own body at the top band (6.0 m): BODY-21
        for p in got:
            i, j = self.land.grid.nearest(*p)
            self.assertGreater(self.land.h[j*self.land.grid.nx+i]-1., pack_climb(gates.BANDS), p)
        self.assertEqual(self.base.band_at(-19., 0.), 8)
        self.assertFalse(any(near(p, (0., -4.)) for p in got), 'a low mesa the walk reaches')
        g = self.land.grid
        for p in got:
            i, j = g.nearest(*p)
            self.assertGreater(self.land.h[j*g.nx+i], 7.5, 'on a tall top')

    def test_the_strongest_pair_climbs_what_the_base_body_cannot_and_the_follow_goes_that_far(self):
        """A sheer face is followed to the tallest rise anyone climbs (`reach.TOP_RISE_M`, 8.64 m: the strongest pair at the
        top band), so the 8.5 m stand the base body never tops is the pair's at band 10, and each lower stand opens at the band
        whose bar first covers it."""
        pair = self.cr.run(gates.FULL_RANK**2)
        self.assertIsNone(self.base.band_at(12., -14.), '8.5 m: over the 6.0 m of the base body at the top band')
        self.assertEqual(pair.band_at(12., -14.), 10, '8.5 m: within the pair\'s 8.64 m at the top band')
        self.assertIsNone(self.base.band_at(-14., -12.))
        self.assertEqual(pair.band_at(-14., -12.), 10, '8 m: the pair climbs 7.78 m at band 9')
        self.assertEqual(pair.band_at(-19., 0.), 6, '4.4 m: the pair climbs 5.18 m at band 6')

    def test_spread_apart_and_the_farthest_from_the_others_first(self):
        got = self.spots()
        for a in range(len(got)):
            for b in range(a+1, len(got)):
                self.assertGreaterEqual(math.hypot(got[a][0]-got[b][0], got[a][1]-got[b][1]), Fractals.LIFT_APART_M)
        far = max(got, key=lambda p: math.hypot(p[0]-0., p[1]-20.))
        self.assertEqual(got[0], far, 'the farthest from where the player arrives first')
        self.assertEqual(len(self.spots(count=2)), 2)
        self.assertEqual(self.spots(count=0), [])

    def test_not_near_a_fractal_already_there_nor_on_a_flight_nor_on_a_plateau_a_flight_stands_on(self):
        g = self.land.grid
        got = self.spots()
        crowded = self.spots(taken=[(0., 20.), got[0]])
        self.assertNotIn(got[0], crowded)
        flight = {j*g.nx+i for j in range(g.nz) for i in range(g.nx) if math.hypot(g.xs[i]+12., g.zs[j]-12.) < 1.5}
        kept = self.spots(keep=flight)
        self.assertEqual(len(kept), len(got)-1, 'the plateau a flight stands on is left to it')
        self.assertFalse(any(math.hypot(p[0]+12., p[1]-12.) < 5. for p in kept))

    def test_a_rim_with_a_way_down_is_no_lift_place(self):
        """A plateau 8 m up with a 39 degree ramp down one side: ground the body walks leads off it."""
        from pipeline.landscape.generator.field import slope_field
        land = synthetic_land(self.half, level=1.)
        g = land.grid
        nx = g.nx
        for j in range(g.nz):
            for i in range(nx):
                x, z = g.xs[i], g.zs[j]
                if abs(x+10.) <= 4. and abs(z+10.) <= 4.:
                    land.h[j*nx+i] = 9.
                elif -6. < x <= 4. and abs(z+10.) <= 1.5:
                    land.h[j*nx+i] = 9.-8.*(x+6.)/10.
        land.fslope = slope_field(g, land.h)
        cr = climb_reach(land, self.half, (0., 20.))
        self.assertEqual(Fractals.lift_places(land, cr.run(1.), (), [(0., 20.)], 4), [])


class FaceAngle(unittest.TestCase):
    """The body grabs any face it cannot stand on (45 degrees), the generator's climb rule a face over 55: a 0.96 m scarp
    at 50 degrees is no climb to the rule and a band-2 climb to the body (`reach.ClimbReach(face_deg=45)`, which
    `coverage.py` measures the island with)."""

    @classmethod
    def setUpClass(cls):
        cls.half = 14.
        land = synthetic_land(cls.half, level=1.)
        g = land.grid
        nx = g.nx
        for j in range(g.nz):
            for i in range(nx):
                z = g.zs[j]
                land.h[j*nx+i] = 1.+.96*min(1., max(0., (z+.3)/.8))    # 0.96 m over 0.8 m, on the grid: 50 degrees
        cls.land = land
        doc, meshes, room = written(g, land.h, cls.half)
        walker = checks.Walker(doc, meshes, checks.Terrain(doc, meshes), room)
        cls.walker, cls.r0 = walker, walker.reach((0., -8.))

    def labels(self, face_deg=None):
        kw = {} if face_deg is None else dict(face_deg=face_deg)
        return reach.ClimbReach(self.walker, self.r0, self.land.grid, self.land.h, **kw).run(1.)

    def test_a_fifty_degree_scarp_is_no_climb_to_the_rule(self):
        self.assertIsNone(self.labels().band_at(0., 6.))
        self.assertEqual(self.labels().band_at(0., -6.), 1)

    def test_it_is_a_climb_to_the_body_a_bar_of_band_two_opens(self):
        got = self.labels(45.)
        self.assertEqual(got.band_at(0., 6.), 2, 'a rise of 0.96 m: over the 0.6 m the starting bar climbs, within 1.2 m')
        self.assertEqual(got.band_at(0., -6.), 1)


class HighGround(unittest.TestCase):
    """The Fractal no band reaches rests on a plateau cliffs close in, never on a hill the body walks up."""

    def test_a_mesa_top_is_closed_in_and_a_hill_is_not(self):
        from pipeline.landscape.generator.field import slope_field
        half = 20.
        for kind, closed in (('mesa', True), ('hill', False)):
            land = synthetic_land(half)
            g = land.grid
            nx = g.nx
            for j in range(g.nz):
                for i in range(nx):
                    x, z = g.xs[i], g.zs[j]
                    d = max(abs(x)-4., abs(z+6.5)-5.5, 0.)
                    land.h[j*nx+i] = (6.8 if d == 0. else 1.) if kind == 'mesa' else max(1., 6.8-d*.4)
            land.fslope = slope_field(g, land.h)
            i, j = g.nearest(0., -6.5)
            got, region = Fractals.enclosed(land, i, j)
            self.assertEqual(got, closed, kind)
            if closed:
                self.assertLess(len(region)*g.cell*g.cell, 100., 'the plateau and no more')


class Held(unittest.TestCase):
    """A flight inside a detail block keeps the base's surface there (the surface it was built as)."""

    def test_the_blocks_vertices_under_a_flight_follow_the_base(self):
        land = synthetic_land(8.)
        g = land.grid
        plan = Fractals.plan(land, 1, (0., 0.), dict(ladders=(), shortcuts=(), steps=1))
        self.assertEqual(len(plan.routes), 1)

        class Fake:
            """The few things of a detail a rule needs: fine vertices on a lattice, the weights of the base's surface."""

            def __init__(self):
                self.ops = []
                self.n = g.nx*g.nz
                self.bench = [0.]*self.n

            def op(self, fn):
                self.ops.append(fn)
                fn()

            def span(self, x0, x1, z0, z1):
                i, j = g.span(x0, x1, z0, z1)
                return [b*g.nx+a for b in j for a in i]
        fake = Fake()
        plan.hold(fake)
        self.assertEqual(len(fake.ops), 1)
        route = plan.routes[0]
        i0, i1, j0, j1 = sites.flight_box(route, 1)
        held = {k for k, v in enumerate(fake.bench) if v == 1.}
        for q, s, m in route.cells(g.nx):
            self.assertIn(q, held)
        self.assertLessEqual(len(held), (i1-i0+3)*(j1-j0+3))


class TreesKeepOffWalls(unittest.TestCase):
    """The exporter fits a tree's climbing collision to the ground under its crown and refuses a crown whose top is not 12
    cm over the highest ground there. At 10x the planting keeps a tree off the foot of a wall; at 1x it is as it was."""

    def planter(self):
        import random
        from pipeline.landscape.generator.generate import Planter
        g = Grid(-3., -3., 3., 3., .1)
        nx = g.nx
        h = [.3]*g.n
        for j in range(g.nz):
            for i in range(nx):
                if g.xs[i] > .05:
                    h[j*nx+i] = 1.     # a wall, 0.7 m high, a cell wide at x = 0 to 0.1
        room = dict(bounds=dict(min_m=[-3., 0., -3.], max_m=[3., 3., 3.]), shell=dict(parts=[]), spawns=[])
        n = g.n
        p = Planter(g, h, [0.]*n, [9.]*n, [True]*n, [0.]*n, [0.]*n, [0.]*n, [False]*n, [0.]*n, [-1]*n, [], [], room,
                    random.Random(1), None, [0.]*n)
        p.path_gap = [9.]*n
        return p

    def put(self, planter, x):
        from pipeline.landscape.harness.kit import SIZES
        return planter.put('broadleaf', x, 0., [.5, .5, .5], 0., .02, SIZES['broadleaf'], sink=.008)

    def test_at_10x_a_tree_stands_clear_of_a_wall_and_at_1x_it_is_as_it_was(self):
        from pipeline.landscape.generator import scale
        was = scale.W
        try:
            scale.set_world(10.)
            self.assertIsNone(self.put(self.planter(), -.02), 'a tree at the foot of a 0.7 m wall is refused')
            self.assertIsNotNone(self.put(self.planter(), -1.5), 'a tree in the open is planted')
            scale.set_world(1.)
            self.assertIsNotNone(self.put(self.planter(), -.02), '1x is as it was')
        finally:
            scale.set_world(was)

    def test_the_crown_the_exporter_fits_is_the_highest_one(self):
        from pipeline.landscape.generator.generate import tree_crown
        r, high = tree_crown('broadleaf')
        self.assertAlmostEqual(high, .95, places=6)
        self.assertGreater(r, .15)
        r, high = tree_crown('conifer')
        self.assertAlmostEqual(high, 1.1, places=6)


class Gates(unittest.TestCase):
    def test_the_pack_and_the_generator_agree(self):
        pack = json.loads(V5.read_text(encoding='utf-8'))
        stamina = pack['traits']['stamina']
        self.assertEqual((gates.START_M, gates.BAND_M, gates.BANDS, gates.BANDS_PER_BAR),
                         (stamina['start_m'], stamina['band_m'], stamina['bands'], stamina['bands_per_bar']))
        # the bar climbs more than its own length (decision 65): the gates carry the pack's number, never a copy of 1
        self.assertEqual(gates.CLIMB_M_PER_BAR_M, stamina['climb_m_per_bar_m'])
        self.assertEqual(gates.CLIMB_M_PER_BAR_M, 1.5)
        table = {k['knack']: k['ranks'] for k in pack['knacks']['table']}
        for knack in gates.CLIMB_KNACKS:
            self.assertEqual(tuple(table[knack]), gates.KNACK_RANKS, knack)
        self.assertEqual(gates.FULL_RANK, max(table['big_bar']))
        fx = pack['fractals']
        self.assertEqual((Fractals.BUDGET['min'], Fractals.BUDGET['max'], Fractals.BUDGET['reachable_first_run']),
                         (fx['budget_min'], fx['budget_max'], fx['reachable_first_run']))
        self.assertEqual((Fractals.BUDGET['per_band'], gates.PER_BAND), (fx['per_band'], fx['per_band']))
        # the founder, 15 October: about fifteen within the base body's reach and ten or more out of it, so 25 or more
        self.assertGreaterEqual(fx['budget_min'], fx['reachable_first_run']+Fractals.OUT_OF_REACH_MIN)
        self.assertEqual((fx['budget_min'], fx['budget_max'], fx['reachable_first_run']), (25, 30, 15))
        self.assertFalse(fx['typed'], 'untyped is the default; typed stays behind the flag')

    def test_a_bar_climbs_what_the_pack_says(self):
        """`gates.cap` is the pack's own arithmetic, band by band: the bar's length times climb_m_per_bar_m. Worked out here
        from the pack alone, so a pack that moves and gates that do not (as when decision 65 made the bar climb 1.5) fail
        this test."""
        stamina = pack_stamina()
        for band in range(1, stamina['bands']+1):
            self.assertAlmostEqual(gates.cap(band), pack_climb(band, stamina), places=9, msg=band)
            self.assertAlmostEqual(gates.bar_m(band), stamina['start_m']+(band-1)*stamina['band_m'], places=9, msg=band)
        self.assertAlmostEqual(gates.COURSE_M, stamina['band_m']*stamina['climb_m_per_bar_m'], places=9)
        self.assertAlmostEqual(gates.COURSE_M, .6, places=9, msg='a course, one band\'s climb')

    def test_no_band_tops_the_next_bands_gate(self):
        """BODY-21. A band gate is the same for everyone only if the band below falls short of it. The pack's own body (its
        bar times climb_m_per_bar_m) opens its own band's gate and no base body opens the next one. Before, the gates were
        sized a metre of climb a metre of bar: a band-2 bar (0.8 m, 1.2 m of climb) topped band 3's gate (1.18 m)."""
        stamina = pack_stamina()
        for band in range(1, stamina['bands']+1):
            climbs = pack_climb(band, stamina)
            self.assertGreaterEqual(climbs, gates.gate_rise(band), 'band %d opens its own gate' % band)
            self.assertEqual(gates.band_for(gates.gate_rise(band), pack_body()), band)
            if band < stamina['bands']:
                self.assertLess(climbs, gates.gate_rise(band+1),
                                'a band-%d bar (%.1f m, %.2f m of climb) tops band %d\'s gate (%.2f m)'
                                % (band, climbs/stamina['climb_m_per_bar_m'], climbs, band+1, gates.gate_rise(band+1)))
        # ... and no rise a ladder builds is opened from below its band by the pack's own body
        for height in (3., 5.8, 7.6, 12., 20., 30.):
            for rise, band in gates.ladder_rises(height):
                self.assertEqual(gates.band_for(rise, pack_body()), band, (height, rise))

    def test_a_bar_climbs_one_and_a_half_times_its_length(self):
        self.assertAlmostEqual(gates.cap(1), .6)
        self.assertAlmostEqual(gates.cap(2), 1.2)
        self.assertAlmostEqual(gates.cap(5), 3.)
        self.assertAlmostEqual(gates.cap(10), 6.)
        self.assertEqual([round(gates.gate_rise(b), 2) for b in (1, 2, 3, 4)], [.58, 1.18, 1.78, 2.38],
                         'every gate half as high again: band 2\'s gate is where band 3\'s stood')
        self.assertEqual(gates.band_for(.6), 1)
        self.assertEqual(gates.band_for(.61), 2)
        self.assertEqual(gates.band_for(6.), 10)
        self.assertIsNone(gates.band_for(6.01))
        self.assertEqual(gates.band_for(6.75, gates.FULL_RANK), 10)
        self.assertEqual(gates.band_for(7.2, gates.FULL_RANK), 10)
        self.assertIsNone(gates.band_for(7.21, gates.FULL_RANK))
        with self.assertRaises(ValueError):
            gates.cap(0)
        with self.assertRaises(ValueError):
            gates.cap(gates.BANDS+1)

    def test_a_climb_is_followed_as_far_as_the_tallest_bar_climbs(self):
        """reach.py follows a face to the tallest rise anyone climbs (the strongest pair at the top band) and no farther, and
        far enough along the ground that the rise always ends the follow first: past its first steep face a climb goes on
        over any ground steeper than a lip (35 degrees)."""
        self.assertAlmostEqual(reach.TOP_RISE_M, gates.cap(gates.BANDS, gates.FULL_RANK**2), places=9)
        self.assertAlmostEqual(reach.TOP_RISE_M, 8.64, places=9)
        self.assertGreaterEqual(reach.MAX_RUN_M, reach.TOP_RISE_M/math.tan(math.radians(climb.LIP_DEG)))

    def test_the_arithmetic_block_names_the_number_the_gates_were_sized_with(self):
        """The package's `x_generator.fractals.arithmetic` (and the reach report) carry the climb a metre of bar buys, so a
        reader holding another pack can tell whether the island's gates were sized for it."""
        got = gates.describe_arithmetic()
        self.assertEqual(got['climb_m_per_bar_m'], pack_stamina()['climb_m_per_bar_m'])
        self.assertAlmostEqual(got['course_m'], .6, places=9)
        self.assertEqual([g['band'] for g in got['gates']], list(range(1, 11)))
        for g in got['gates']:
            self.assertAlmostEqual(g['bar_m'], .4*g['band'], places=4)
            self.assertAlmostEqual(g['cap_m'], g['bar_m']*1.5, places=4)
            self.assertAlmostEqual(g['gate_rise_m'], g['cap_m']-.02, places=4)

    def test_band_gates_and_the_arithmetic_that_gives_out(self):
        """No single knack opens a band gate early up to band 5; from band 6 on Big bar or Steady climber at full rank
        does, and the strongest pair opens one from band 4. The generator builds them all and reports exactly this."""
        for band in range(1, 11):
            got = gates.early_open(gates.gate_rise(band), band)
            self.assertEqual(got['base'], band)
            if band <= 5:
                self.assertNotIn('one_knack', got['early'], band)
            else:
                self.assertEqual(got['early']['one_knack'], band-1, band)
            if band <= 3:
                self.assertNotIn('pair', got['early'], band)
            if band >= 4:
                self.assertIn('pair', got['early'], band)

    def test_the_bands_the_islands_own_fractals_open_and_the_stair_above_a_short_corridor(self):
        self.assertEqual(gates.own_band(25), 6)
        self.assertEqual(gates.own_band(30), 7, 'the placeholder ceiling: six spends on bands from band 1')
        self.assertEqual(gates.own_band(10000), gates.BANDS)
        self.assertEqual(gates.own_band(4), 1)
        self.assertEqual(gates.stair_bands(5, 7), [6, 7, 8], "bands 7 to 8 for the garage: the founder's summit")
        self.assertEqual(gates.stair_bands(5, 6), [6, 7])
        self.assertEqual(gates.stair_bands(8, 7), [], 'beyond them already')
        self.assertEqual(gates.stair_bands(7, 7), [8], 'a top ledge that needs no more than they open gets one more')
        self.assertEqual(gates.stair_bands(10, 10), [], 'no band is beyond the top band')
        self.assertEqual(gates.stair_bands(4, 10), [5, 6, 7, 8, 9, 10])

    def test_a_shortcut_sits_between_the_base_body_and_one_knack(self):
        for band in (1, 2):
            rise = gates.shortcut_rise(band)
            self.assertGreater(rise, gates.cap(band)+.02, 'the base body falls short with a margin')
            self.assertLess(rise, gates.cap(band, gates.FULL_RANK)-.02, 'one knack clears it with a margin')
            self.assertLess(gates.cap(band, gates.FULL_RANK**2), gates.gate_rise(band+1),
                            'even the strongest pair does not carry it into the next gate (the arithmetic holds up to band 2)')
        # ... and past band 2 it cannot: a pair at band 3 climbs 2.59 m, the next gate is 2.38 m
        self.assertGreater(gates.cap(3, gates.FULL_RANK**2), gates.gate_rise(4))

    def test_ladders_of_gates_one_band_more_each(self):
        rises = gates.ladder_rises(11.4)
        self.assertAlmostEqual(sum(r for r, _ in rises), 11.4)
        self.assertEqual([b for _, b in rises][:5], [1, 2, 3, 4, 5])
        self.assertEqual([round(r, 2) for r, _ in rises], [.58, 1.18, 1.78, 2.38, 2.98, 2.5])
        self.assertTrue(all(r <= gates.cap(b)+1e-9 for r, b in rises))
        # the couch of the stand-in garage, 7.6 m: four gates and what is left (it was five and what was left)
        self.assertEqual([(round(r, 2), b) for r, b in gates.ladder_rises(7.6)], [(.58, 1), (1.18, 2), (1.78, 3), (2.38, 4), (1.68, 3)])
        self.assertIsNone(gates.ladder_rises(34.))
        top = gates.ladder_rises(32.79)
        self.assertEqual(len(top), 10)
        self.assertEqual(top[-1][1], 10)

    def test_the_climb_rule_follows_a_tall_face_only_when_asked(self):
        g = Grid(-3, -3, 3, 3, .1)
        h = [0.]*g.n
        route = R.Route('ladder', 20, 30, 1, 0, 0., R.make_ledges([(gates.gate_rise(3), 3)], g.cell))
        route.apply(g, h)
        x, z = route.xz(g, -.5)
        up = climb.climb_from(g, h, x, z, 1., 0., 0.)
        self.assertIsNone(up, 'the rule\'s 0.6 m follow does not top a 1.78 m face')
        up = climb.climb_from(g, h, x, z, 1., 0., 0., reach=reach.MAX_RUN_M, max_rise=reach.TOP_RISE_M)
        self.assertIsNotNone(up)
        self.assertAlmostEqual(up['rise'], 1.78, places=2)
        self.assertIsNone(climb.climb_from(g, h, x, z, 1., 0., 0., reach=reach.MAX_RUN_M, max_rise=1.))

    def test_a_long_climb_over_scramble_ground_is_followed_to_its_top(self):
        """Past its first steep face a climb goes on over any ground steeper than a lip. An 8 m rise at 40 degrees above a
        60 degree foot runs 9.4 m along the ground: within what the strongest pair climbs at the top band (8.64 m), so the
        follow reaches its top. Nine metres of follow, enough while the tallest rise was 5.76 m, gave it up as no climb."""
        g = Grid(-10, -3, 10, 3, .1)
        nx = g.nx
        foot, face = math.tan(math.radians(60.)), math.tan(math.radians(40.))
        h = [0.]*g.n
        for j in range(g.nz):
            for i in range(nx):
                x = g.xs[i]
                h[j*nx+i] = 0. if x <= -6. else min(8., (x+6.)*foot if x <= -5.8 else .2*foot+(x+5.8)*face)
        up = climb.climb_from(g, h, -6.03, 0., 1., 0., 0., reach=reach.MAX_RUN_M, max_rise=reach.TOP_RISE_M)
        self.assertIsNotNone(up)
        self.assertTrue(up['topped'])
        self.assertAlmostEqual(up['rise'], 8., places=1)
        self.assertGreater(up['r'], 9.)
        self.assertIsNone(climb.climb_from(g, h, -6.03, 0., 1., 0., 0., reach=9., max_rise=reach.TOP_RISE_M))
        # ... and a rise no bar climbs is given up at the tallest rise, however far the follow goes
        self.assertIsNone(climb.climb_from(g, h, -6.03, 0., 1., 0., 0., reach=reach.MAX_RUN_M, max_rise=gates.cap(gates.BANDS)))


class Flights(unittest.TestCase):
    def test_a_flight_is_steep_risers_and_flat_benches(self):
        g = Grid(-6, -6, 6, 6, .1)
        h = [1.]*g.n
        route = R.ladder('ladder', g, 20, 60, 1, 0, 1., (1, 2, 3, 4))
        route.apply(g, h)
        self.assertEqual(len(route.ledges), 4)
        for k, ledge in enumerate(route.ledges):
            self.assertAlmostEqual(ledge.rise, gates.gate_rise(k+1))
        fs = face_slope_max(g, h, g.triangles())
        nx = g.nx
        for k, ledge in enumerate(route.ledges):
            a = route.starts[k]
            for s in range(a, a+ledge.riser_cells):
                for m in range(-route.half, route.half+1):
                    q = route.vertex(s, m, nx)
                    self.assertGreater(fs[q], 66-1e-6, (k, s, m))
            # a bench's middle: the same height all over (every vertex of the flat part)
            for s in range(a+ledge.riser_cells+1, a+ledge.riser_cells+ledge.bench_cells):
                for m in range(-route.half+1, route.half):
                    q = route.vertex(s, m, nx)
                    self.assertEqual(h[q], route.levels[k+1])
                    self.assertLess(fs[q], 1e-6)
        # beyond the flight the land is as it was, and every side is a cliff
        self.assertEqual(h[route.vertex(-1, 0, nx)], 1.)
        self.assertEqual(h[route.vertex(route.length+1, 0, nx)], 1.)
        for k, ledge in enumerate(route.ledges):
            s = route.starts[k]+ledge.riser_cells+ledge.bench_cells//2
            side = h[route.vertex(s, route.half, nx)]-h[route.vertex(s, route.half+1, nx)]
            self.assertGreaterEqual(side, route.levels[k+1]-1.-1e-9)
            self.assertGreater(math.degrees(math.atan(side/g.cell)), 74)

    def test_a_riser_runs_at_least_66_degrees(self):
        for rise in (.38, .58, .78, 1.18, 1.58, 1.78, 1.98, 2.38, 2.98, 3.98, 5.98, .44, .66, .88, 1.32):
            run = R.riser_cells(rise, .1)*.1
            self.assertGreaterEqual(math.degrees(math.atan(rise/run)), 66-1e-6, rise)


class FractalsAreTenTimesThings(unittest.TestCase):
    """At world scale 1 nothing changes: the corpus stays byte-identical (the generator at 1x writes no Fractals and no
    tutorial mark, and refuses to be made to)."""

    def test_the_1x_garage_has_no_fractals_and_no_tutorial_mark(self):
        import os
        doc = json.loads(Path(garage(), 'package.json').read_text(encoding='utf-8'))
        self.assertNotIn('fractals', doc['x_generator'])
        self.assertNotIn('tutorial', doc['x_generator'])

    def test_fractals_cannot_be_asked_for_at_1x(self):
        with self.assertRaises(ValueError):
            generate(ROOMS/'near_empty_nominal', Path(garage()).parent/'never', fractals=True)


if __name__ == '__main__':
    unittest.main()
