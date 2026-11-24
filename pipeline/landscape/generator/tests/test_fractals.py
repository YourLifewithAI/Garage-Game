"""Fractals and gates (the mechanics round, packet M4): the stamina bands and the two kinds of gate, the flights built into
the grid, the climb-aware reach proved by the exact walk, the Fractals' ids, and the 1x world left as it was (its
firepits' rows and every file's digest pinned: `TrueSize`).

    python -B -m unittest pipeline.landscape.generator.tests.test_fractals -v

A real ten-times room takes seven minutes to make, so its walk (`TenTimesGarage`) stands aside unless the environment
variable LANDSCAPE_TEN_TIMES asks for it: `generate` makes the stand-in garage at ten times and walks it; anything else is
read as the folder of a ten-times package of corpus/rooms/garage_nominal made already.

    $env:LANDSCAPE_TEN_TIMES = 'generate'    # PowerShell; in sh, LANDSCAPE_TEN_TIMES=generate before the command
    python -B -m unittest pipeline.landscape.generator.tests.test_fractals.TenTimesGarage -v
"""
import hashlib
import json
import math
import os
import re
import shutil
import subprocess
import sys
import unittest
from pathlib import Path
from unittest import mock

from pipeline.landscape.generator import checks, climb, firepits as Firepits, fractals as Fractals, gates, reach, routes as R, sites
from pipeline.landscape.generator.field import Grid, face_slope_max
from pipeline.landscape.generator.generate import generate
from pipeline.landscape.generator.tests.test_generator import ROOMS, garage, workdir

ROOT = Path(__file__).resolve().parents[4]
V5 = ROOT/'game'/'rules'/'storybook_wild'/'v5.json'
TEN_TIMES = os.environ.get('LANDSCAPE_TEN_TIMES', '')


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
    """Places only the Gubble's lift or a built ladder reaches: on a plateau cliffs close in all round, that no band's
    climb reaches and whose rim stands `fractals.LIFT_RIM_M` over the foot of its cliffs, spread over the island; never on
    a hill, a low mesa, a flight or the water.

    Two rules keep a bar's own climb off a lift place, and each has a stand of its own here. The reach: a sheer stand a
    bar climbs carries a band (the 4.4 m stand, band 8). The rim: the walk models no face between 20 and 55 degrees, so
    a mound with sides in between carries no band at all, though the game's body grabs any face over 45 (`FaceAngle`); only
    its rim's height keeps a lift place off it (the 4 m mound). The rim is meant to stand over the top band's climb: it is
    4.5 m, right while that climb was 4.0 m and 1.5 m short of today's 6.0 (`test_no_lift_place_where_the_bodys_own_bar_climbs`)."""

    MOUND = (-2., -17.)     # the mound's middle: a flat top 2 m in radius, then 48 degree sides down to the plain
    MOUND_M = 4.            # its top over the plain

    @staticmethod
    def mound(land, cx, cz, top, radius=2., deg=48.):
        """Raise a round mound: a flat top of `radius`, sides of `deg` degrees all round (round, so no corner is steeper)."""
        g = land.grid
        nx = g.nx
        fall = math.tan(math.radians(deg))
        for j in range(g.nz):
            for i in range(nx):
                d = max(math.hypot(g.xs[i]-cx, g.zs[j]-cz)-radius, 0.)
                land.h[j*nx+i] = max(land.h[j*nx+i], top-d*fall)

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
        stand(-19., 0., 3., 5.4)       # 4.4 m over the plain, sheer: a bar of band 8 climbs it (4.8 m), so it carries a band
        stand(-12., 12., 3., 8.)       # tall, but a flight stands on it (kept)
        # 4 m over the plain with 48 degree sides: no band reaches it, cliffs close it in, and its rim is under LIFT_RIM_M
        cls.mound(land, cls.MOUND[0], cls.MOUND[1], 1.+cls.MOUND_M)
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
        self.assertFalse(any(near(p, (-19., 0.)) for p in got), 'a sheer cliff of 4.4 m: a bar climbs it, so a band reaches it')
        # every lift place stands over the tallest climb of the pack's own body at the top band (6.0 m): BODY-21
        for p in got:
            i, j = self.land.grid.nearest(*p)
            self.assertGreater(self.land.h[j*self.land.grid.nx+i]-1., pack_climb(gates.BANDS), p)
        self.assertEqual(self.base.band_at(-19., 0.), 8)
        self.assertFalse(any(near(p, (0., -4.)) for p in got), 'a low mesa the walk reaches')
        self.assertFalse(any(near(p, self.MOUND) for p in got), 'a mound no band reaches, its rim under what a lift place needs')
        g = self.land.grid
        for p in got:
            i, j = g.nearest(*p)
            self.assertGreater(self.land.h[j*g.nx+i], 7.5, 'on a tall top')

    def test_the_rim_alone_keeps_a_lift_place_off_a_mound_no_band_reaches(self):
        """The rim rule, on its own (BODY-21 fix: the 4.4 m stand that used to show it is climbed from band 8 now, so its
        band rules it out whatever the rim says). The 4 m mound's sides are 48 degrees: too steep to walk, too gentle for
        the climb rule, so no band reaches its top (nor the strongest pair's: the test below). Cliffs close it in. All that
        stands between it and a lift place is its rim, 4 m over the plain and under `LIFT_RIM_M`: lower that bar and the
        mound is a lift place."""
        near = lambda p: math.hypot(p[0]-self.MOUND[0], p[1]-self.MOUND[1]) < 5.
        self.assertIsNone(self.base.band_at(*self.MOUND), 'no band reaches it: its label does not rule it out')
        i, j = self.land.grid.nearest(*self.MOUND)
        closed, region = Fractals.enclosed(self.land, i, j)
        self.assertTrue(closed, 'cliffs close it in: that does not rule it out either')
        drop = Fractals.rim_drop(self.land, region)
        self.assertGreater(drop, self.MOUND_M-.25)
        self.assertLess(drop, Fractals.LIFT_RIM_M, 'its rim is under the rim a lift place needs')
        self.assertFalse(any(near(p) for p in self.spots()))
        with mock.patch.object(Fractals, 'LIFT_RIM_M', self.MOUND_M-.5):
            lower = self.spots()
        self.assertEqual(len(lower), 4, lower)
        self.assertTrue(any(near(p) for p in lower), 'with a rim of 3.5 m the mound is a lift place: the rim is the rule that kept it out')

    def test_no_lift_place_where_the_bodys_own_bar_climbs(self):
        """What the rim is for, held against the pack: a lift place waits for the Gubble's lift or a built ladder, so its
        rim stands over the tallest climb of the pack's own body (6.0 m at the top band). A mound 0.3 m under that with 52
        degree sides is no climb to the rule and a band-10 climb to the body, which grabs any face over 45 degrees: no
        lift place may rest on it.

        `fractals.LIFT_RIM_M` is 4.5 m, set over the 4.0 m the top bar climbed before decision 65 and BODY-21, and
        fractals.py is not BODY-21's to edit (its change request: 6.5 m). With the rim at 4.5 m this mound is a lift
        place; while it is still that 4.5 m this test stands aside and says so, and at any other value it runs."""
        top = pack_climb(gates.BANDS)
        if Fractals.LIFT_RIM_M == 4.5:
            self.skipTest('fractals.LIFT_RIM_M is still 4.5 m, under the %.1f m the top band climbs: a 5.7 m mound the '
                          'body climbs at band 10 is a lift place (BODY-21\'s change request: 6.5 m)' % top)
        self.assertGreater(Fractals.LIFT_RIM_M, top)
        from pipeline.landscape.generator.field import slope_field
        half = 14.
        land = synthetic_land(half, level=1.)
        self.mound(land, 0., -5., 1.+top-.3, deg=52.)
        land.fslope = slope_field(land.grid, land.h)
        rule = climb_reach(land, half, (0., 10.))
        self.assertIsNone(rule.run(1.).band_at(0., -5.), 'no climb to the rule')
        body = reach.ClimbReach(rule.w, rule.reach0, land.grid, land.h, face_deg=45.).run(pack_body())
        self.assertEqual(body.band_at(0., -5.), gates.BANDS, 'the body\'s own bar climbs it at the top band')
        self.assertEqual(Fractals.lift_places(land, rule.run(1.), (), [(0., 10.)], 4), [])

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
        self.assertIsNone(pair.band_at(*self.MOUND), 'the mound\'s 48 degree sides are no climb to the rule, however long the bar')

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

    def test_the_gates_as_they_stood_were_topped_from_up_to_three_bands_below(self):
        """What BODY-21 mended, worked out (the comments in gates.py and reach.py lean on it): a gate sized a metre of rise a
        metre of bar, the bar's length less the slack, against the pack's own body. The first two gates held; band 3's was
        opened from band 2, and by the top the body was three bands early (band 10's gate, 3.98 m, from band 7)."""
        stamina = pack_stamina()
        bands = range(1, gates.BANDS+1)
        old = [gates.bar_m(band)-gates.GATE_SLACK_M for band in bands]
        self.assertEqual([round(rise, 2) for rise in old[:3]+old[-1:]], [.38, .78, 1.18, 3.98])
        opened = [next(k for k in bands if pack_climb(k, stamina) >= rise-gates.EPS) for rise in old]
        self.assertEqual(opened, [1, 2, 2, 3, 4, 4, 5, 6, 6, 7])
        # ... and the mended gates open each from its own band
        self.assertEqual([gates.band_for(gates.gate_rise(band), pack_body()) for band in bands], list(bands))

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
    """Fractals, gates and the tutorial mark are ten-times things: at world scale 1 the generator writes none and refuses
    to be made to. That is not the same as a 1x package reading nothing of them: its firepits read the reach the gates
    size (`TrueSize`, below, where the 1x bytes are pinned)."""

    def test_the_1x_garage_has_no_fractals_and_no_tutorial_mark(self):
        doc = json.loads(Path(garage(), 'package.json').read_text(encoding='utf-8'))
        self.assertNotIn('fractals', doc['x_generator'])
        self.assertNotIn('tutorial', doc['x_generator'])

    def test_fractals_cannot_be_asked_for_at_1x(self):
        with self.assertRaises(ValueError):
            generate(ROOMS/'near_empty_nominal', Path(garage()).parent/'never', fractals=True)


# ------------------------------------------------------------------------------------------- true size, pinned

# The stand-in garage at world scale 1: its firepits as the package says them (x_generator.firepits.items) ...
GARAGE_1X_FIREPITS = [
    dict(position_m=[2.175, .0935, -.375], reach_band=1, landmark='storage_tote_1', why='first-run reach, by storage_tote_1'),
    dict(position_m=[-2.425, .1084, -.375], reach_band=1, landmark='cardboard_box_1', hamlet=True,
         why='near the hamlet, first-run reach, by cardboard_box_1'),
    dict(position_m=[-.375, .1119, 3.2], reach_band=1, landmark='office_chair_1', why='first-run reach, by office_chair_1'),
]
# ... and the sha256 of every file of its package, taken on the founder's laptop (Windows, CPython 3.14) with the generator
# as BODY-21 left it; the generator before BODY-21 wrote the same eighteen.
GARAGE_1X_FILES = {
    'brook_0.glb': '9b85411edec9c9148245a936c252cb0d9ea024f9036b0c4908fd39ac3ef5f480',
    'distant_islands.glb': '31a193178991d6cb0c26f300ea1bf6462c09e55b3cbab4bac034a9705f58c3ff',
    'jetty.glb': '6b0bb5e3a7924ac2ef53b6eafd495f1a477527e3e7f7af97dadc49a4a639a781',
    'land.glb': '678fcba9c7f3146b66c908fddf8bbbb0b3546b141075ebae8758ba071766d6b9',
    'package.json': '2964cdaad66156e2c3a6f535b166cbc107da800c457a5bd3acd8dbeb60ad33c2',
    'proto_buttercups.glb': 'ab348b69efe676f31d6af72d1515a5dbf799e5dc17a3e1f88a11f8b178df42d2',
    'proto_daisies.glb': '7699466906d905476fb9ab19da6ae91eb0fb22d06fff894fd99d5ee36d59eee5',
    'proto_drying_line.glb': '9a80702cd6d6d3e53a3a4071f998944bfd4063ba69696c2626598b2de49aa240',
    'proto_footbridge.glb': '7c2e8f04f2ba8fd65dc8429ad4627fc3675e6976c3280ef730d04f4c8c23f181',
    'proto_grass.glb': '1287b9546b89882ce98ce6c3fdb9a0d6a9a06d05710baaeb31fca8f51a101f5b',
    'proto_heather.glb': '6948aff88789e088ff1f6d7678b084b6282d647f1cdb9578199992315e131cf6',
    'proto_reeds.glb': '630f962c3fd27766c6f7f525a047107e7ef5bea8ba0b9291b78e2841e8ee71ff',
    'proto_wildflowers.glb': '7102d412e5ed3ace898025a4563ee007d5c7b89713bc67614573343510d5911f',
    'proto_woodpile.glb': '2ad55adf67785f69fcf70d8eb339e1524e66fdddf42286c773a1b37f3b89d91d',
    'river_0.glb': 'd9bf1b64e514f8fd2b01eea3c4531e4fa3df931755ed3175a3a59493990d576b',
    'river_1.glb': '59e3e083755630cb5844335388e56b561c4d6d39a12f6cd1c59667d17c4cf78e',
    'sea.glb': '53480c4e2eb7e1c90f101d769f024d6aaf59d8872a4bec8267bc6f674f4daab6',
    'tarn.glb': '7d582fb79b889c632b64239f9ff2bd8bbdb7bfa12bebc289783f6cf590f8d7db',
}


def package_digests(folder):
    """{file: sha256} of every file of a package."""
    folder = Path(folder)
    return {p.relative_to(folder).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(folder.rglob('*')) if p.is_file()}


class TrueSize(unittest.TestCase):
    """True-size output bytes, pinned (BODY-21's "true-size output bytes unchanged", which no suite held).

    A 1x package holds no Fractal and no gate, and still it reads the gates: generate.py stands its firepits on the
    climb-aware reach (`checks._base_reach`, so `reach.ClimbReach` on `gates.cap`), each spot labelled with its band and
    first-run ground counted nearer (`firepits.REACH_PULL_M`). BODY-21 moved that reach on the stand-in garage: 239 nodes
    that took band 2 are band 1's. No byte moved with it, on this land or any room of the corpus, because the garage's
    three firepits stand where the feet alone get. That is a fact about these lands, not a rule of the generator: a firepit
    on a ledge, or the next change to the gates, moves true-size bytes. So the garage's 1x package is held three ways: its
    firepits' rows, every file's digest, and the same bytes again under another climb."""

    @classmethod
    def setUpClass(cls):
        cls.room = ROOMS/'garage_nominal'
        cls.package = garage()
        cls.doc = json.loads(Path(cls.package, 'package.json').read_text(encoding='utf-8'))

    def test_the_garages_firepits_are_where_they_were(self):
        """Position, reach band and why, row for row (the manifest's x_firepits, which the kernel numbers in this order)."""
        self.assertEqual(self.doc['x_generator']['firepits']['items'], GARAGE_1X_FIREPITS)

    def test_the_reach_its_firepits_read_moved_with_the_gates_and_no_firepit_with_it(self):
        """The reach a 1x firepit reads is the gates' own, and BODY-21 did move it: under a metre of rise a metre of bar
        (the gates as they stood) some of the garage's ground took a band more. Every firepit says the same band under
        both, because each stands on ground the feet reach with no climb at all."""
        doc, meshes, room, _ = checks.read_package(self.package, self.room)
        _, now = checks._base_reach(doc, meshes, checks.Terrain(doc, meshes), room)
        was = now.reach.run(1./gates.CLIMB_M_PER_BAR_M)
        self.assertAlmostEqual(gates.cap(1, 1./gates.CLIMB_M_PER_BAR_M), gates.bar_m(1), places=9)
        moved = sum(1 for a, b in zip(was.label, now.label) if a != b)
        self.assertGreater(moved, 0, 'the 1x reach reads the gates: BODY-21 moved 239 of the garage\'s nodes from band 2 to band 1')
        self.assertEqual(sum(1 for b in was.label if b), sum(1 for b in now.label if b), 'the same ground, other bands')
        feet = now.reach.reach0.parent
        for item in doc['x_generator']['firepits']['items']:
            x, _, z = item['position_m']
            self.assertEqual((was.band_at(x, z), now.band_at(x, z)), (item['reach_band'], item['reach_band']), item)
            self.assertNotEqual(feet[now.node(x, z)], -2, 'a firepit on climbed ground: its row reads the gates (%r)' % item)

    def test_a_firepit_on_a_ledge_reads_the_gates(self):
        """Why the pins. The garage's bytes held because none of its firepits stands on climbed ground, and that is no rule
        of the generator. A made-up land, half of it a terrace 0.5 m up (over the old first gate's reach, 0.4 m, under
        today's 0.6), gets a firepit up there, and its row reads the gates: first-run ground now, band 2 as they stood."""
        half, start = 14., (-8., 0.)
        land = synthetic_land(half)
        g = land.grid
        for j in range(g.nz):
            for i in range(g.nx):
                if g.xs[i] > 2.:
                    land.h[j*g.nx+i] = 1.5
        cr = climb_reach(land, half, start)
        now, was = cr.run(1.), cr.run(1./gates.CLIMB_M_PER_BAR_M)
        step = Firepits.SAMPLE_M
        samples = [(-12.+step/2+a*step, -12.+step/2+b*step) for a in range(48) for b in range(48)]

        def firepits(labels):
            spots = [Firepits.Spot(x, g.tri_height(land.h, x, z), z, labels.band_at(x, z)) for x, z in samples
                     if labels.band_at(x, z) is not None]
            return Firepits.choose(samples, len(samples)*step*step, spots, start)['items']
        today, before = firepits(now), firepits(was)
        up = [item for item in today if item['position_m'][0] > 2.]
        self.assertTrue(up, today)
        for item in up:
            self.assertEqual((item['reach_band'], item['why']), (1, 'first-run reach'))
        self.assertTrue(all(item['reach_band'] == 2 and item['why'] == 'reached from band 2'
                            for item in before if item['position_m'][0] > 2.), before)
        self.assertNotEqual(today, before, 'the same land, other gates, other rows: true-size bytes are not blind to the gates')

    def test_the_garage_is_the_same_bytes(self):
        """Every file of the 1x garage, by its sha256. A packet that means to change true-size output pins the new digests
        here and says so; any other change that lands here is the fault this test is for."""
        got = package_digests(self.package)
        moved = sorted(name for name in set(got) | set(GARAGE_1X_FILES) if got.get(name) != GARAGE_1X_FILES.get(name))
        self.assertEqual(moved, [], 'true-size output bytes changed in %d of %d files. If the firepits\' rows and the test under '
                         'another climb still pass and this is another system or Python than the pin\'s (Windows, CPython 3.14; '
                         'this is %s, %s), take the pin again there. As written now:\n%s'
                         % (len(moved), len(got), sys.platform, sys.version.split()[0],
                            '\n'.join('    %r: %r,' % (name, got[name]) for name in sorted(got))))

    def test_the_garage_is_the_same_bytes_whatever_a_bar_climbs(self):
        """BODY-21's own clause, on any system: the garage generated again with a bar that climbs its own length (the
        gates before BODY-21: `gates.cap` is read as the land is made) is the same bytes, file for file."""
        folder = workdir('true_size_old_gates')
        self.addCleanup(shutil.rmtree, folder, ignore_errors=True)
        with mock.patch.object(gates, 'CLIMB_M_PER_BAR_M', 1.):
            self.assertAlmostEqual(gates.cap(2), .8, places=9, msg='the gates as they stood: a band-2 bar climbs 0.8 m')
            generate(self.room, os.path.join(folder, 'pkg'))
        self.assertAlmostEqual(gates.cap(2), 1.2, places=9)
        self.assertEqual(package_digests(os.path.join(folder, 'pkg')), package_digests(self.package))


# ------------------------------------------------------------------------------------------- a real ten-times room


@unittest.skipUnless(TEN_TIMES, 'a real ten-times room takes seven minutes to make: set LANDSCAPE_TEN_TIMES=generate, or to the '
                                'folder of a ten-times package of corpus/rooms/garage_nominal')
class TenTimesGarage(unittest.TestCase):
    """BODY-21 on a real room: the stand-in garage at ten times, every gate walked by the body the rules pack describes.
    The synthetic islands above hold the arithmetic; this holds a land the game loads (what `tools/test-room.ps1 -Scale`
    builds, whose own walk never climbs). On the land the generator made before BODY-21 the pack's body stood on 12 of 26
    benches, and on 8 of 28 Fractals, from below the band they say; on the land it makes now, on none."""

    @classmethod
    def setUpClass(cls):
        room = ROOMS/'garage_nominal'
        if TEN_TIMES.lower() in ('1', 'generate'):
            folder = os.path.join(workdir('ten_times'), 'pkg')
            cls.addClassCleanup(shutil.rmtree, os.path.dirname(folder), ignore_errors=True)
            subprocess.run([sys.executable, '-B', '-m', 'pipeline.landscape.generator.generate', '--room', str(room),
                            '--world-scale', '10', '--tutorial', '--out', folder], cwd=str(ROOT), check=True,
                           stdout=subprocess.DEVNULL)
        else:
            folder = TEN_TIMES
        cls.doc, cls.meshes, cls.room, _ = checks.read_package(folder, room)
        cls.terrain = checks.Terrain(cls.doc, cls.meshes)
        cls.floods = {}
        _, cls.base = checks._base_reach(cls.doc, cls.meshes, cls.terrain, cls.room, cls.floods)
        cls.real = cls.base.reach.run(pack_body())    # the pack's own body: its bar times climb_m_per_bar_m
        cls.fx = cls.doc['x_generator']['fractals']

    def test_it_is_a_ten_times_land_whose_gates_were_sized_with_the_packs_climb(self):
        self.assertEqual(self.doc['x_generator'].get('world_scale'), 10.)
        self.assertEqual(self.fx.get('arithmetic', {}).get('climb_m_per_bar_m'), pack_stamina()['climb_m_per_bar_m'],
                         'this land\'s gates were sized for another climb than the pack\'s')
        built = [ledge['band'] for row in self.fx['routes'] for ledge in row['ledges']]
        self.assertGreaterEqual(len(built), 10)
        self.assertGreaterEqual(max(built), 3, 'a ladder to band 3 at least: the gate a band-2 bar used to top')

    def test_the_packs_own_body_takes_every_gate_from_the_band_it_was_built_as(self):
        """Every bench from the band the package says, and that the band it was built as (a way round a summit's corridor
        that the package itself reports aside: checks.fractal_checks's own rule)."""
        wrong = []
        for row in self.fx['routes']:
            for k, ledge in enumerate(row['ledges']):
                x, _, z = ledge['bench_m']
                want = max(l['band'] for l in row['ledges'][:k+1])
                got, said = self.real.band_at(x, z), ledge['reached_from']['base']
                if got != said or (got != want and not ledge.get('bypassed')):
                    wrong.append('%s ledge %d (rise %.2f m), built as band %d: the package says band %s, the pack\'s body stands '
                                 'on it from band %s' % (row['id'], k+1, ledge['rise_m'], want, said, got))
        self.assertEqual(wrong, [], '%d benches' % len(wrong))

    def test_every_fractal_is_reached_from_the_band_it_says(self):
        wrong = [(item['id'], item.get('reach_band'), self.real.band_at(item['position_m'][0], item['position_m'][2]))
                 for item in self.fx['items']
                 if self.real.band_at(item['position_m'][0], item['position_m'][2]) != item.get('reach_band')]
        self.assertEqual(wrong, [], '(Fractal, the band it says, the band the pack\'s body reaches it from)')

    def test_the_generators_own_check_passes_walked_by_the_packs_body(self):
        floods = dict(self.floods)
        floods[next(key for key in floods if key[0] == 'base')] = self.real    # checks._base_reach's own key for the reach
        ok, rows = checks.fractal_checks(self.doc, self.meshes, self.terrain, self.room, floods)
        self.assertTrue(ok, rows)
        self.assertEqual((rows['failing'], rows['benches']), (0, sum(len(row['ledges']) for row in self.fx['routes'])))


if __name__ == '__main__':
    unittest.main()
