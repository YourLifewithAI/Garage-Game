"""The rim: great cliffs for walls, a desert on top, no sea (Run 4, packet WORLD-2).

    python -B -m unittest pipeline.landscape.generator.tests.test_rim -v

The record and the surface it gives, on made-up records; the sun gate, against rays marched one by one; the stand-in
garage at ten times as generate.py raises it with `--edge rim`, stopped where the water is planned (seven seconds: the
rim is whole there); things against a wall, on a made-up room stopped the same way; the option itself (without it
nothing of the rim runs); and a whole package of that made-up room (about a minute and a half), read back by every
check with the rim check where the sea check stood. Two more are asked for by name: LANDSCAPE_RIM_TWICE=1 makes that
package a second time and compares the bytes (a minute more), and LANDSCAPE_TEN_TIMES makes and checks the stand-in's
own whole package (`generate`: about ten minutes; or a package folder of corpus/rooms/garage_nominal made with
`--world-scale 10 --edge rim`). That the island's own bytes are what they were is test_fractals.TrueSize's pinned
digests at true size; at ten times the two packages were compared when the option was built (WORLD-2's report).
"""
import contextlib
import copy
import io
import json
import math
import os
import unittest
from pathlib import Path
from unittest import mock

from pipeline.landscape.generator import checks, generate as G, rim as Rim, scale as world
from pipeline.landscape.generator.tests.test_generator import ROOMS, tree, workdir
from pipeline.landscape.harness import read_package
from pipeline.landscape.harness.views import sun_position

TEN_TIMES = os.environ.get('LANDSCAPE_TEN_TIMES', '')
W10 = 10.
TAN = math.tan(math.radians(Rim.FACE_DEG))


def record(**more):
    """A rim record of a plain room 20 by 30 m under a 24 m lip: a notch in the +Z wall, a bay in the -X wall."""
    rec = dict(outline_m=[[-10., -15.], [-10., 15.], [10., 15.], [10., -15.]], lip_y_m=24., lip_out_m=3.373,
               tile_edge_out_m=10., seam_out_m=19.873, wrap_out_m=21.373, period_m=12.8, home_wind_deg=90.,
               openings=[dict(kind='notch', centre_m=[2., 15.], width_m=8., floor_y_m=11., flare_deg=30.),
                         dict(kind='bay', centre_m=[-10., -4.], width_m=8., floor_y_m=0., flare_deg=0.)])
    rec.update(more)
    return rec


# ---------------------------------------------------------------- made-up rooms
def made_room(name, size, things, openings, spawn=(0., 0.)):
    """A made-up room folder (room.json and inventory.json in the corpus's own shape, cut from the stand-in's):
    `size` (width, height, depth) in true metres round the origin; `things`: (id, kind, (sx, sy, sz), (x, z), more)
    with `more` a dict of `y` (its base's height), `on` (the id it rests on) or `lifted` (on a surface nothing
    holds); `openings`: (id, kind, wall, along, bottom, (width, height)) with wall one of 'x0', 'x1', 'z0', 'z1'."""
    folder = Path(workdir(name))/'room'
    folder.mkdir()
    src = ROOMS/'garage_nominal'
    room = json.loads((src/'room.json').read_text(encoding='utf-8'))
    inv = json.loads((src/'inventory.json').read_text(encoding='utf-8'))
    w, h, d = size
    room['room_id'] = name
    room['display_name'] = 'Made up'
    room['bounds'] = dict(min_m=[-w/2, 0., -d/2], max_m=[w/2, h, d/2])
    parts = {p['id']: p for p in room['shell']['parts']}
    parts['shell:floor']['geometry']['points_m'] = [[-w/2, 0., -d/2], [-w/2, 0., d/2], [w/2, 0., d/2], [w/2, 0., -d/2]]
    parts['shell:ceiling']['geometry']['points_m'] = [[-w/2, h, -d/2], [w/2, h, -d/2], [w/2, h, d/2], [-w/2, h, d/2]]
    walls = dict(z0=('shell:wall_a', (-w/2, -d/2), (w/2, -d/2)), x1=('shell:wall_b', (w/2, -d/2), (w/2, d/2)),
                 z1=('shell:wall_c', (w/2, d/2), (-w/2, d/2)), x0=('shell:wall_d', (-w/2, d/2), (-w/2, -d/2)))
    for part, a, b in walls.values():
        parts[part]['geometry']['points_m'] = [[a[0], 0., a[1]], [b[0], 0., b[1]], [b[0], h, b[1]], [a[0], h, a[1]]]
    room['shell']['openings'] = []
    for ident, kind, wall, along, bottom, (ow, oh) in openings:
        x, z = dict(x0=(-w/2, along), x1=(w/2, along), z0=(along, -d/2), z1=(along, d/2))[wall]
        room['shell']['openings'].append(dict(id=ident, kind=kind, host_part_id=walls[wall][0], center_m=[x, bottom+oh/2, z],
                                              size_m=[ow, oh], state='fixed', traversable=False))
    room['spawns'] = [dict(id='player_start', position_m=[spawn[0], 0., spawn[1]], role='player', yaw_deg=0.),
                      dict(id='companion_start', position_m=[spawn[0]+.4, 0., spawn[1]], role='companion', yaw_deg=0.)]
    room['light_hints'] = []
    like = inv['objects'][0]
    inv['room'] = name
    inv['picks'] = []
    inv['objects'] = []
    for ident, kind, (sx, sy, sz), (x, z), more in things:
        y = more.get('y', 0.)
        e = copy.deepcopy(like)
        e.update(id=ident, kind=kind)
        e.pop('pick', None)
        e['box'] = dict(centre_m=[x, y+sy/2, z], footprint_m=[x-sx/2, z-sz/2, x+sx/2, z+sz/2], size_m=[sx, sy, sz], yaw_deg=0)
        e['placement'].update(position_m=[x, y, z], support=dict(height_m=0., kind='floor'))
        if more.get('on'):
            e['placement']['support'] = dict(height_m=y, kind='object', target_id=more['on'])
        elif more.get('lifted'):
            e['placement']['support'] = dict(height_m=y, kind='surface', target_id=None)
        e['recipe_needed'].update(for_kind=kind, size_m=[sx, sy, sz])
        inv['objects'].append(e)
    for file, doc in (('room.json', room), ('inventory.json', inv)):
        (folder/file).write_text(json.dumps(doc, indent=2, sort_keys=True)+'\n', encoding='utf-8', newline='\n')
    return folder


# A room 2.4 by 3 m under a 2.4 m ceiling: a window in the south wall (+Z; the room's -Z faces north) and one in the
# north wall, a door in the west wall. A cabinet stands flush against the east wall (half a centimetre off it), a
# shelf hangs on the west wall with nothing under it, a crate stands five centimetres off the north wall (a slot), a
# lamp hangs in the middle of the room, and a table stands clear with a jar on it.
CABINET_ROOM = dict(
    size=(2.4, 2.4, 3.),
    things=[('cabinet_1', 'cabinet', (.45, .9, .8), (.97, .5), {}),
            ('shelf_1', 'box', (.2, .3, .6), (-.95, .5), dict(y=1.2, lifted=True)),
            ('crate_1', 'cardboard box', (.5, .4, .4), (-.5, -1.25), {}),
            ('lamp_1', 'box', (.2, .2, .2), (.3, -.3), dict(y=1.5, lifted=True)),
            ('table_1', 'table', (.7, .6, .5), (-.1, .5), {}),
            ('jar_1', 'jar', (.09, .12, .09), (-.1, .5), dict(y=.6, on='table_1'))],
    openings=[('window_south', 'window', 'z1', .4, 1.1, (.8, .8)), ('window_north', 'window', 'z0', .6, 1.4, (.5, .6)),
              ('entry', 'door', 'x0', -.6, 0., (.8, 2.)), ('grille', 'vent', 'x1', -1., 2., (.3, .2))],
    spawn=(.2, -.6))


class Stop(Exception):
    pass


def raised(room, **patches):
    """A room at ten times as generate.py raises it with the rim, stopped where the water is planned: the base grid,
    its heights and owners, the plain, the objects, the rim's plan and the water's way out."""
    got = {}
    real = Rim.plan

    def plan(*a, **k):
        got['rim'] = real(*a, **k)
        return got['rim']

    def stop(grid, room_, objects, h, base, inside, spawns, setup, noise, outside, sea=None, detail=None):
        got.update(grid=grid, h=list(h), base=list(base), objects=objects, inside=list(inside), way=sea)
        raise Stop()
    world.set_world(W10)
    try:
        with contextlib.ExitStack() as stack:
            for name, value in patches.items():
                stack.enter_context(mock.patch.object(Rim, name, value))
            stack.enter_context(mock.patch.object(G, 'plan_and_carve', stop))
            stack.enter_context(mock.patch.object(G.Rim, 'plan', plan))
            stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
            try:
                G.generate(room, os.path.join(workdir('never'), 'pkg'), edge='rim')
            except Stop:
                pass
    finally:
        world.set_world(1.)
    return got


def height(land, x, z):
    return land['grid'].tri_height(land['h'], x, z)


def clears(model, x, z, sun, slack, step=.2):
    """Whether the ray from the floor at (x, z) toward the sun stays `slack` over the rim's surface (the room's own
    things aside) until it stands over the lip."""
    dx, dy, dz = sun
    t = step
    while t*dy < model.lip_y:
        px, pz = x+dx*t, z+dz*t
        if not model.inside(px, pz) and t*dy < model.height(px, pz)+slack:
            return False
        t += step
    return True


# ---------------------------------------------------------------- the record
class Record(unittest.TestCase):
    def test_the_record_is_the_table_and_nothing_more(self):
        rec = record()
        self.assertIs(Rim.validate(rec), rec)
        self.assertEqual(set(rec), set(Rim.FIELDS) | {'home_wind_deg'})
        self.assertEqual(set(rec['openings'][0]), set(Rim.OPENING_FIELDS))
        del rec['home_wind_deg']     # a room with no door has no home wind
        Rim.validate(rec)

    def test_a_record_of_another_shape_is_refused(self):
        notch = record()['openings'][0]
        bad = dict(
            another_key=dict(label='read me'), lip_as_text=dict(lip_y_m='27'), lip_as_a_flag=dict(lip_y_m=True),
            lip_not_a_number=dict(lip_y_m=float('nan')), lip_without_end=dict(lip_y_m=float('inf')),
            lip_out_of_bounds=dict(lip_y_m=1e6), two_points=dict(outline_m=[[0., 0.], [1., 1.]]),
            too_many_points=dict(outline_m=[[math.cos(k/10), math.sin(k/10)] for k in range(65)]),
            a_point_of_three=dict(outline_m=[[0., 0., 0.], [1., 0., 0.], [1., 1., 0.]]),
            a_line=dict(outline_m=[[0., 0.], [1., 0.], [2., 0.]]), lip_past_the_tiles=dict(lip_out_m=10.),
            seam_inside_the_lip=dict(seam_out_m=3.), wrap_inside_the_seam=dict(wrap_out_m=19.),
            wind_a_full_turn=dict(home_wind_deg=360.), openings_as_a_record=dict(openings={}),
            too_many_openings=dict(openings=[dict(notch)]*17), a_gate=dict(openings=[dict(notch, kind='gate')]),
            an_opening_with_a_name=dict(openings=[dict(notch, name='x')]),
            a_floor_over_the_lip=dict(openings=[dict(notch, floor_y_m=24.)]),
            a_flare_too_wide=dict(openings=[dict(notch, flare_deg=61.)]),
            a_width_of_nothing=dict(openings=[dict(notch, width_m=0.)]),
            a_centre_as_text=dict(openings=[dict(notch, centre_m='2,15')]))
        for name, more in bad.items():
            with self.subTest(name):
                with self.assertRaises(ValueError):
                    Rim.validate(record(**more))
        short = record()
        del short['period_m']
        for what in (short, [], None, 'rim'):
            with self.assertRaises(ValueError):
                Rim.validate(what)
        with self.assertRaises(ValueError):
            Rim.Model(record(lip_y_m='27'))     # a reader of the record refuses it the same way


# ---------------------------------------------------------------- the surface the record gives
class Surface(unittest.TestCase):
    """The made-up record: a room 20 by 30 m, a notch 8 m wide in its +Z wall with its floor at 11 m, a bay in -X."""

    @classmethod
    def setUpClass(cls):
        cls.m = Rim.Model(record())

    def test_the_walls_are_cliffs_of_82_degrees_up_to_the_lip(self):
        m = self.m
        self.assertEqual(m.height(-6., 0.), 0.)                 # the room is the room's
        for (x, z, ox, oz) in ((-9.5, 15., 0., 1.), (0., -15., 0., -1.), (10., 5., 1., 0.), (-10., 8., -1., 0.)):
            for s in (.5, 1., 2., 3.):
                self.assertAlmostEqual(m.height(x+ox*s, z+oz*s), TAN*s, places=2)
            self.assertAlmostEqual(math.degrees(math.atan(m.height(x+ox, z+oz))), Rim.FACE_DEG, places=2)
            for s in (3.4, 5., 9.9):
                self.assertEqual(m.above(x+ox*s, z+oz*s, s), (24., 'sand'))
        self.assertEqual(m.above(-9.5, 16., 1.)[1], 'cliff')
        self.assertEqual(m.height(13., 18.), 24.)               # round a corner: the lip, a quarter circle out
        self.assertAlmostEqual(m.height(11., 16.), self.m.tan*math.hypot(1., 1.), places=6)

    def test_a_notch_runs_out_level_from_its_sill(self):
        m = self.m
        self.assertAlmostEqual(m.height(2., 16.), m.tan, places=6)     # the face under the sill
        for a in (1.6, 3., 5., 9.9, 40.):
            self.assertEqual(m.above(2., 15.+a, a), (11., 'sand'))
            self.assertEqual(m.above(5.9, 15.+a, a), (11., 'sand'))
            self.assertEqual(m.above(-1.9, 15.+a, a), (11., 'sand'))

    def test_a_notch_s_sides_run_straight_through_the_cliff_and_widen_past_the_lip(self):
        m = self.m
        # Inside the cliff's thickness: a scarp of 82 degrees from each side of the window's own width.
        for d in (.25, .5, 1.):
            self.assertAlmostEqual(m.height(6.+d, 18.), 11.+m.tan*d, places=6)
            self.assertAlmostEqual(m.height(-2.-d, 18.), 11.+m.tan*d, places=6)
            self.assertEqual(m.above(6.+d, 18., 3.)[1], 'cliff')
        # Four metres past the lip the notch is 30 degrees a side wider.
        a = m.lip_out+4.
        wide = 4.+4.*math.tan(math.radians(30.))
        self.assertEqual(m.height(2.+wide-.05, 15.+a), 11.)
        self.assertEqual(m.height(2.-wide+.05, 15.+a), 11.)
        self.assertAlmostEqual(m.height(2.+wide+1., 15.+a), 11.+m.tan*math.cos(math.radians(30.)), places=6)
        self.assertEqual(m.height(2.+wide+4., 15.+a), 24.)

    def test_no_side_of_an_opening_meets_the_cliff_in_a_knife_edge(self):
        """Where an opening's side meets the cliff's face the land never falls as one goes out or away from the
        opening, so a climb up the side goes on up the face (sides that widened from the wall line fell away behind;
        the whole package's climb check says the same of the land as built)."""
        m = self.m
        for k in range(1, 27):
            a = k*.1
            for n in range(0, 24):
                b = n*.1
                here = m.height(6.+b, 15.+a)
                self.assertGreaterEqual(m.height(6.+b, 15.+a+.1), here-1e-9, (a, b))
                self.assertGreaterEqual(m.height(6.+b+.1, 15.+a), here-1e-9, (a, b))
                here = m.height(-10.-a, 0.+b)                    # the bay's side (the bay ends at z = 0)
                self.assertGreaterEqual(m.height(-10.-a-.1, b), here-1e-9, (a, b))
                self.assertGreaterEqual(m.height(-10.-a, b+.1), here-1e-9, (a, b))

    def test_a_bay_is_a_recess_with_the_room_s_floor_in_it(self):
        m, rec = self.m, record()
        self.assertEqual(Rim.bay_depth(rec, rec['openings'][1]), 1.)
        for z in (-7.9, -4., -.1):
            self.assertEqual(m.above(-10.5, z, .5), (0., 'slab'))
            self.assertAlmostEqual(m.height(-11.5, z), m.tan*.5, places=6)     # its back wall
            self.assertEqual(m.height(-15., z), 24.)                           # closed to the lip
        self.assertAlmostEqual(m.height(-10.5, .25), m.tan*.25, places=6)      # its side
        wide = dict(rec['openings'][1], width_m=26.)
        self.assertEqual(Rim.bay_depth(rec, wide), 3.)

    def test_past_the_tiles_the_sand_lies_at_the_lip_or_at_a_notch_s_floor(self):
        m = self.m
        self.assertEqual(m.far(2., 15.+60.), 11.)       # the low desert
        self.assertEqual(m.far(-20., 15.+60.), 11.)     # still inside the widening
        self.assertEqual(m.far(-60., 15.+60.), 24.)
        self.assertEqual(m.far(0., -75.), 24.)
        self.assertEqual(m.far(-70., -4.), 24.)         # a bay opens on nothing
        self.assertEqual(m.far(-6., 16.), 24.)          # over a face: what the sand beyond stands at

    def test_the_outline_s_winding_and_shape_do_not_matter(self):
        back = Rim.Model(record(outline_m=record()['outline_m'][::-1]))
        for x, z in ((-6., 16.), (2., 18.), (6.5, 18.), (-10.5, -4.), (-11.5, -4.), (13., 18.), (0., 0.), (9., 30.)):
            self.assertAlmostEqual(back.height(x, z), self.m.height(x, z), places=9)
        ell = Rim.Model(record(outline_m=[[-10., -15.], [-10., 15.], [10., 15.], [10., 0.], [0., 0.], [0., -15.]],
                               openings=[]))
        self.assertTrue(ell.inside(-5., -10.))
        self.assertFalse(ell.inside(5., -10.))          # the corner cut out of the L
        self.assertAlmostEqual(ell.height(5., -1.), ell.tan, places=6)
        self.assertEqual(ell.height(5., -8.), 24.)


# ---------------------------------------------------------------- the sun gate
class SunGate(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.rec = record()
        cls.m = Rim.Model(record(openings=cls.rec['openings'][:1]))     # the notch alone: a bay lets a ray by too
        cls.bare = Rim.Model(record(openings=[]))
        cls.notch = cls.m.openings[0]
        suns = [sun_position(30., day, hour, 0.) for day in (21, 80, 141, 264, 355)
                for hour in (8.5, 9.75, 11., 12., 13.25, 14.5, 15.75)]
        cls.suns = [s['direction_room'] for s in suns if s['altitude_deg'] >= Rim.SUN_MIN_ALT_DEG]

    def test_every_ray_the_gate_counts_comes_through_the_notch(self):
        """Marched one by one over the surface the record gives: each lands in the room and clears the rim."""
        tried = 0
        for sun in self.suns:
            rays = list(Rim._beam(self.m, self.notch, sun))
            for reach, x, z in rays[::max(1, len(rays)//10)]:
                tried += 1
                self.assertTrue(self.m.inside(x, z))
                self.assertGreater(reach, 0.)
                self.assertTrue(clears(self.m, x, z, sun, -.03), (sun, x, z))
        self.assertGreater(tried, 100)

    def test_a_ray_that_comes_through_the_notch_is_not_missed(self):
        """From a lattice over the floor: where a ray clears the rim that the unbroken cliff would have stopped, the
        gate says the notch is lit."""
        lit = dark = 0
        for sun in self.suns:
            through = any(clears(self.m, x+.5, z+.5, sun, .05) and not clears(self.bare, x+.5, z+.5, sun, 0.)
                          for x in range(-10, 10, 2) for z in range(-15, 15, 2))
            lit += through
            dark += not through
            if through:
                self.assertIsNotNone(Rim._lit(self.m, self.notch, sun), sun)
        self.assertGreater(lit, 5)
        self.assertGreater(dark, 2)

    def test_the_gate_lists_each_notch_s_sunlit_hours(self):
        gate = Rim.sun_gate(self.rec, 30., 0.)
        self.assertEqual((gate['latitude_deg'], gate['neg_z_bearing_deg'], gate['step_min'], gate['time']),
                         (30., 0., Rim.SUN_STEP_MIN, 'apparent solar'))
        self.assertEqual(len(gate['notches']), 1)       # the bay has no hours
        notch = gate['notches'][0]
        self.assertEqual((notch['opening'], notch['faces_deg'], notch['beam']), (0, 180., True))
        self.assertEqual([d['day'] for d in notch['days']], list(Rim.SUN_DAYS))
        days = {d['day']: d for d in notch['days']}
        # A south window at 30 degrees north: at midwinter the noon ray comes through and reaches deep into the room.
        winter = days[355]
        self.assertLess(winter['from_h'], 12.)
        self.assertGreater(winter['to_h'], 12.)
        self.assertGreater(winter['reach_m'], 20.)
        self.assertAlmostEqual(winter['lit_h'], (winter['to_h']-winter['from_h'])+Rim.SUN_STEP_MIN/60, places=2)
        noon = sun_position(30., 355, 12., 0.)['direction_room']
        self.assertIsNotNone(Rim._lit(self.m, self.notch, noon))
        # At midsummer the sun stands over the wall: nothing comes through the notch.
        self.assertEqual(days[172], dict(day=172, from_h=None, to_h=None, lit_h=0., reach_m=0.))
        self.assertGreater(days[355]['reach_m'], days[80]['reach_m'])
        json.dumps(gate, allow_nan=False)

    def test_a_notch_the_sun_never_enters_says_it_has_no_beam(self):
        gate = Rim.sun_gate(self.rec, 30., 180.)        # the room turned half round: the window looks north
        notch = gate['notches'][0]
        self.assertEqual(notch['faces_deg'], 0.)
        self.assertFalse(notch['beam'])
        self.assertTrue(notch['note'].startswith('no beam'))
        self.assertTrue(all(d['lit_h'] == 0. and d['from_h'] is None for d in notch['days']))
        self.assertNotIn('note', Rim.sun_gate(self.rec, 30., 0.)['notches'][0])


# ---------------------------------------------------------------- the stand-in garage
class StandIn(unittest.TestCase):
    """The stand-in garage at ten times, as generate.py raises it with `--edge rim` (stopped where the water is
    planned): 60 by 70 m under walls of 27 m, the garage door in the -Z wall, a window in the +X wall."""

    @classmethod
    def setUpClass(cls):
        cls.land = raised(ROOMS/'garage_nominal')
        cls.rim = cls.land['rim']
        cls.rec = cls.rim.record
        cls.m = cls.rim.model

    def test_the_rim_record_is_written_in_the_table_s_shape(self):
        rec = self.rec
        Rim.validate(rec)
        self.assertEqual({k: v for k, v in rec.items() if k not in ('outline_m', 'openings')},
                         dict(lip_y_m=27., lip_out_m=3.795, tile_edge_out_m=10., seam_out_m=20.295, wrap_out_m=21.795,
                              period_m=12.8, home_wind_deg=180.))
        self.assertEqual(sorted(map(tuple, rec['outline_m'])), [(-30., -35.), (-30., 35.), (30., -35.), (30., 35.)])
        self.assertEqual(rec['openings'],
                         [dict(kind='bay', centre_m=[15.5, -35.], width_m=26., floor_y_m=0., flare_deg=0.),
                          dict(kind='notch', centre_m=[30., -17.5], width_m=10., floor_y_m=11., flare_deg=30.)])
        self.assertEqual(json.loads(json.dumps(rec, allow_nan=False)), rec)
        self.assertEqual(self.rim.shut, [])
        self.assertAlmostEqual(math.degrees(math.atan(rec['lip_y_m']/rec['lip_out_m'])), Rim.FACE_DEG, places=1)
        # The land's tiles end at the tile edge, 10 m past the wall line.
        g = self.land['grid']
        self.assertEqual((g.xs[0], g.xs[-1], g.zs[0], g.zs[-1]), (-40., 40., -45., 45.))

    def test_there_is_no_sea(self):
        land, m = self.land, self.m
        self.assertEqual(land['way']['level'], -1e6)        # no level the water may not pass
        self.assertEqual(land['way']['avoid'], [])
        g, h = land['grid'], land['h']
        low = min(h[q] for q in range(g.n) if not m.inside(g.xs[q % g.nx], g.zs[q//g.nx]))
        self.assertGreaterEqual(low, 0.)                    # the island's shore fell under its sea

    def test_the_walls_are_cliffs_with_the_lip_at_27_m_all_round_but_for_the_openings(self):
        m, land = self.m, self.land
        walls = ((-30., -35., -30., 35., -1., 0.), (30., -35., 30., 35., 1., 0.), (-30., 35., 30., 35., 0., 1.),
                 (-30., -35., 30., -35., 0., -1.))
        lip = broken = 0
        for x0, z0, x1, z1, ox, oz in walls:
            for k in range(57):
                px, pz = x0+(x1-x0)*k/56, z0+(z1-z0)*k/56
                near = [f for f in m.openings if (ox, oz) == (f['nx'], f['nz'])
                        and abs((px-f['x'])*f['tx']+(pz-f['z'])*f['tz']) < f['half']+7.]
                for s in (4.2, 6., 8., 9.8):
                    y = height(land, px+ox*s, pz+oz*s)
                    if near:
                        broken += y < 26.9
                    else:
                        lip += 1
                        self.assertAlmostEqual(y, 27., delta=.005, msg=(px, pz, s))
                if not near:
                    for s in (1., 2., 3.):      # the face: 82 degrees
                        self.assertAlmostEqual(height(land, px+ox*s, pz+oz*s), m.tan*s, delta=.02, msg=(px, pz, s))
        self.assertGreater(lip, 700)
        self.assertGreater(broken, 20)
        for x, z in ((34., 39.), (-34., 39.), (-34., -39.), (36., -41.)):       # and round the corners
            self.assertAlmostEqual(height(land, x, z), 27., delta=.005)

    def test_the_window_s_notch_opens_onto_its_low_desert(self):
        m, land = self.m, self.land
        self.assertAlmostEqual(height(land, 30.5, -17.5), m.tan*.5, delta=.02)     # the face under the sill
        for a in (1.8, 3., 5., 8., 9.9):
            for b in (-4.7, 0., 4.7):
                self.assertAlmostEqual(height(land, 30.+a, -17.5+b), 11., delta=.005, msg=(a, b))
        # At the tile edge it has widened 30 degrees a side from the lip, and the sand beyond lies at its floor.
        wide = 5.+(9.9-m.lip_out)*math.tan(math.radians(30.))
        self.assertGreater(wide, 8.4)
        for b in (-wide+.3, wide-.3):
            self.assertAlmostEqual(height(land, 39.9, -17.5+b), 11., delta=.005)
        self.assertEqual(m.far(60., -17.5), 11.)
        self.assertEqual(m.far(60., 20.), 27.)
        # Its sides are scarps as tall as the cliff: the sill opens no walk to the high desert.
        for b in (-wide-3., wide+3.):
            self.assertAlmostEqual(height(land, 39.9, -17.5+b), 27., delta=.005)
        for a in (3., 3.7, 6., 9.):
            half = 5.+max(0., a-m.lip_out)*math.tan(math.radians(30.))
            for side in (-1., 1.):
                rise = height(land, 30.+a, -17.5+side*(half+.6))-height(land, 30.+a, -17.5+side*(half+.3))
                self.assertGreater(math.degrees(math.atan(rise/.3)), 75., (a, side))

    def test_the_door_is_a_bay_the_water_leaves_by(self):
        m, land = self.m, self.land
        bay = m.openings[0]
        self.assertEqual(bay['depth'], 3.)
        for x in (4., 15.5, 27.):
            self.assertLess(height(land, x, -36.5), 1.5)                                 # the room's own floor
            self.assertAlmostEqual(height(land, x, -39.), m.tan*1., delta=.02)           # its back wall
            self.assertAlmostEqual(height(land, x, -44.5), 27., delta=.005)              # closed to the lip
        g = land['grid']
        goal = [q for q in range(g.n) if land['way']['goal'](q)]
        self.assertGreater(len(goal), 50)
        for q in goal:
            x, z = g.xs[q % g.nx], g.zs[q//g.nx]
            self.assertTrue(2.59 <= x <= 28.41 and -37.81 <= z <= -36.49, (x, z))       # on the bay's own floor

    def test_a_thing_a_slot_away_from_a_wall_stands_free(self):
        """The desk stands 5 cm (true size) off the +Z wall: a slot a body squeezes into, left open. Were a slot
        any narrower gap than 6 cm, the desk would be a buttress."""
        self.assertEqual(self.rim.joins, [])
        near = raised(ROOMS/'garage_nominal', JOIN_GAP_M=.06)
        joins = near['rim'].joins
        self.assertEqual([(j['o']['id'], j['lifted'], round(j['gap'], 3)) for j in joins], [('desk_1', False, .5)])
        j = joins[0]
        x, z = j['mid'][0]+j['out'][0]*.25, j['mid'][1]+j['out'][1]*.25
        self.assertLess(height(self.land, x, z), 1.5)
        self.assertGreater(height(near, x, z), height(self.land, x, z)+3.)

    def test_the_sun_gate_lists_the_window_s_sunlit_hours(self):
        gate = self.rim.sun_gate()
        self.assertEqual(gate, Rim.sun_gate(self.rec, self.rim.setup['latitude_deg'], self.rim.setup['neg_z_bearing_deg']))
        self.assertEqual(len(gate['notches']), 1)
        notch = gate['notches'][0]
        self.assertEqual((notch['opening'], notch['faces_deg'], notch['beam']), (1, 90., True))
        days = {d['day']: d for d in notch['days']}
        # An east window: the morning's sun, from sunrise at midsummer.
        self.assertEqual(days[172]['from_h'], 6.)
        self.assertLess(days[172]['to_h'], 12.)
        self.assertGreater(days[172]['lit_h'], 5.)
        self.assertGreater(days[80]['reach_m'], 50.)
        # At midwinter the low sun comes in from the south-east and meets the far cliff before the floor.
        self.assertEqual(days[355]['lit_h'], 0.)
        self.assertEqual(sum(d['lit_h'] > 0 for d in notch['days']), 9)


# ---------------------------------------------------------------- things against a wall
class Buttress(unittest.TestCase):
    """The made-up cabinet room at ten times: 24 by 30 m under walls of 24 m."""

    @classmethod
    def setUpClass(cls):
        cls.room = made_room('cabinet_room', **CABINET_ROOM)
        cls.land = raised(cls.room)
        cls.free = raised(cls.room, JOIN_GAP_M=0., LIFTED_JOIN_M=-1.)
        cls.rim = cls.land['rim']

    def test_which_things_are_joined_into_the_cliff(self):
        self.assertEqual([(j['o']['id'], j['lifted'], round(j['gap'], 3), j['out']) for j in self.rim.joins],
                         [('cabinet_1', False, .05, (1., -0.)), ('shelf_1', True, 1.5, (-1., 0.))])
        self.assertEqual(self.free['rim'].joins, [])

    def test_a_thing_standing_against_a_wall_runs_into_the_cliff(self):
        land, free = self.land, self.free
        top = height(land, 11.5, 5.)                 # the cabinet's own land by the side it turns to the wall
        self.assertGreater(top, 9.)
        for x in (12.3, 12.6, 12.9):                 # past the wall line: the cliff's face stood lower here
            self.assertAlmostEqual(height(land, x, 5.), top, delta=.01)
            self.assertAlmostEqual(height(free, x, 5.), self.rim.model.tan*(x-12.), delta=.02)
        self.assertGreater(height(land, 11.975, 5.), height(free, 11.975, 5.)+3.)     # the gap itself
        self.assertAlmostEqual(height(land, 13.7, 5.), self.rim.model.tan*1.7, delta=.02)   # the face, above the top
        # Only as wide as the cabinet (8 m, round z = 5).
        for z in (-.5, 10.5):
            self.assertEqual(height(land, 12.3, z), height(free, 12.3, z))

    def test_a_lifted_thing_nothing_holds_is_a_buttress_from_further_off(self):
        land, free = self.land, self.free
        top = height(land, -9.5, 5.)                 # the shelf, 1.5 m (15 cm true) off the -X wall
        self.assertGreater(top, 14.)
        for x in (-11.8, -11.25, -10.7, -12.3):
            self.assertAlmostEqual(height(land, x, 5.), top, delta=.01)
        self.assertLess(height(free, -11.25, 5.), 1.5)
        # The lamp hangs in the middle of the room: nothing joins it to a wall.
        self.assertNotIn('lamp_1', [j['o']['id'] for j in self.rim.joins])

    def test_a_slot_stays_open(self):
        # The crate stands 5 cm (true size) off the -Z wall: 50 cm of floor between it and the cliff.
        self.assertLess(height(self.land, -5., -14.75), 1.5)
        self.assertEqual(height(self.land, -5., -14.75), height(self.free, -5., -14.75))

    def test_a_buttress_keeps_its_land(self):
        g = self.land['grid']
        protect, floor_at = [False]*g.n, [0.]*g.n
        self.rim.protect(g, protect, floor_at)
        i, j = g.nearest(11.98, 5.)
        self.assertTrue(protect[j*g.nx+i])
        self.assertGreater(floor_at[j*g.nx+i], 5.)
        i, j = g.nearest(3., 3.)
        self.assertFalse(protect[j*g.nx+i])

    def test_a_window_is_a_notch_a_door_a_bay_and_a_vent_stays_shut(self):
        rim = self.rim
        self.assertEqual(rim.opening_ids, ['entry', 'window_north', 'window_south'])
        self.assertEqual(rim.record['openings'],
                         [dict(kind='bay', centre_m=[-12., -6.], width_m=8., floor_y_m=0., flare_deg=0.),
                          dict(kind='notch', centre_m=[6., -15.], width_m=5., floor_y_m=14., flare_deg=30.),
                          dict(kind='notch', centre_m=[4., 15.], width_m=8., floor_y_m=11., flare_deg=30.)])
        self.assertEqual(rim.shut, [dict(id='grille', kind='vent', why='a vent needs a roof')])
        self.assertEqual(rim.record['home_wind_deg'], 90.)      # in by the door in the -X wall, blowing toward +X
        self.assertAlmostEqual(height(self.land, 12.+5., -10.), 24., delta=.005)    # the vent's wall is whole


# ---------------------------------------------------------------- the option
class Option(unittest.TestCase):
    def test_only_a_magnified_room_has_a_rim(self):
        room = made_room('true_size', **CABINET_ROOM)
        out = os.path.join(workdir('true_size_pkg'), 'pkg')
        with self.assertRaises(ValueError):
            G.generate(room, out, edge='rim')
        with self.assertRaises(ValueError):
            G.generate(room, out, edge='moat')
        self.assertFalse(os.path.exists(out))

    def test_without_the_option_nothing_of_the_rim_runs(self):
        room = made_room('island', **CABINET_ROOM)
        got = []

        def stop(grid, room_, objects, h, base, inside, spawns, setup, noise, outside, sea=None, detail=None):
            got.append((list(h), sea['level']))
            raise Stop()
        world.set_world(W10)
        try:
            for edge in (None, 'sea'):
                with mock.patch.object(G, 'plan_and_carve', stop), contextlib.redirect_stdout(io.StringIO()), \
                        mock.patch.object(G.Rim, 'plan', side_effect=AssertionError('the rim was planned')), \
                        mock.patch.object(G.Rim.Rim, 'under', side_effect=AssertionError('the rim was raised')):
                    with self.assertRaises(Stop):
                        G.generate(room, os.path.join(workdir('never'), 'pkg'), edge=edge)
        finally:
            world.set_world(1.)
        self.assertEqual(got[0][1], G.Sea.SEA_Y)
        self.assertEqual(got[0], got[1])
        self.assertLess(min(got[0][0]), G.Sea.SEA_Y)        # the island's shore runs under its sea

    def test_the_command_line_makes_the_island_unless_told(self):
        made = dict(meshes={}, scatter=[], objects=[], x_generator={})
        for argv, edge in ((['--room', 'r', '--out', 'o'], 'sea'), (['--room', 'r', '--out', 'o', '--edge', 'rim'], 'rim')):
            with mock.patch.object(G, 'generate', return_value=made) as gen, mock.patch('sys.argv', ['generate']+argv), \
                    mock.patch.object(G, 'load_json', return_value={}), contextlib.redirect_stdout(io.StringIO()):
                G.main()
            self.assertEqual(gen.call_args.kwargs['edge'], edge)
        with mock.patch('sys.argv', ['generate', '--room', 'r', '--out', 'o', '--edge', 'moat']), \
                contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            G.main()


# ---------------------------------------------------------------- a whole package
class Package(unittest.TestCase):
    """A whole package of the made-up cabinet room with the rim, read back by every check (about a minute and a
    half)."""

    @classmethod
    def setUpClass(cls):
        cls.room = made_room('cabinet_package', **CABINET_ROOM)
        cls.pkg = os.path.join(workdir('rim_pkg'), 'pkg')
        world.set_world(W10)
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                G.generate(cls.room, cls.pkg, edge='rim')
                cls.result = Rim.run(cls.pkg, cls.room)
                cls.doc, cls.meshes, cls.magnified, _ = read_package(cls.pkg, cls.room)
                cls.terrain = checks.Terrain(cls.doc, cls.meshes)
        finally:
            world.set_world(1.)
        cls.gen = cls.doc['x_generator']

    def check(self, change):
        doc = copy.deepcopy(self.doc)
        change(doc, doc['x_generator'])
        return Rim.rim_checks(doc, self.meshes, self.terrain, self.magnified)

    def test_every_check_passes_with_the_rim_check_where_the_sea_check_stood(self):
        r = self.result
        self.assertEqual({k: v for k, v in r.items() if k.endswith('ok')},
                         dict(ok=True, water_ok=True, grounded_ok=True, walk_ok=True, footprints_ok=True, rim_ok=True,
                              climb_ok=True, fractals_ok=True, firepits_ok=True))
        self.assertNotIn('sea', r)
        self.assertEqual(self.check(lambda doc, gen: None)[0], True)

    def test_no_climb_tops_out_on_a_knife_edge(self):
        # (Sides that widened from the wall line left 56 of them on this room, each over a body's height.)
        self.assertEqual(self.result['climbs']['knife_edges'], 0)

    def test_the_package_has_no_sea(self):
        doc, gen = self.doc, self.gen
        self.assertEqual(gen['edge'], 'rim')
        self.assertNotIn('sea', gen)
        self.assertEqual(doc['scenery'], [])
        self.assertTrue(doc['water'])
        self.assertFalse(any(name in ('sea', 'distant_islands', 'jetty') for name in doc['meshes']))
        self.assertFalse(any(w['mesh'] == 'sea' for w in doc['water']))
        self.assertEqual(gen['margin_m'], 10.)
        self.assertTrue(self.result['rim']['no_sea'])

    def test_the_package_says_what_its_rim_is(self):
        gen = self.gen
        Rim.validate(gen['rim'])
        self.assertEqual([o['kind'] for o in gen['rim']['openings']], ['bay', 'notch', 'notch'])
        self.assertEqual(gen['rim_openings'], ['entry', 'window_north', 'window_south'])
        self.assertEqual(gen['rim_shut'], [dict(id='grille', kind='vent', why='a vent needs a roof')])
        self.assertEqual([(b['id'], b['lifted'], b['gap_m']) for b in gen['buttresses']],
                         [('cabinet_1', False, .05), ('shelf_1', True, 1.5)])
        for b in gen['buttresses']:
            self.assertAlmostEqual(self.terrain.height(*b['probe_m']), b['stands_m'], delta=.01)
            self.assertGreater(b['stands_m'], 9.)
        # The sun gate: the north window has no beam and says so; the south one takes the midwinter noon.
        north, south = gen['sun_gate']['notches']
        self.assertEqual((north['opening'], north['faces_deg'], north['beam']), (1, 0., False))
        self.assertTrue(north['note'].startswith('no beam'))
        self.assertEqual((south['opening'], south['faces_deg'], south['beam']), (2, 180., True))
        winter = [d for d in south['days'] if d['day'] == 355][0]
        self.assertTrue(winter['from_h'] < 12. < winter['to_h'])

    def test_the_rim_check_reads_the_land_as_built(self):
        rim = self.result['rim']
        self.assertEqual(rim['tiles_reach_m'], [10., 10., 10., 10.])
        self.assertTrue(rim['tiles_ok'] and rim['spawn_in_the_basin'] and rim['sun_gate_ok'])
        self.assertEqual((rim['lip']['height_m'], rim['lip']['off']), (24., 0))
        self.assertGreater(rim['lip']['points'], 2000)
        self.assertEqual([(n['opening'], n['floor_m'], n['open_to_the_tile_edge'], n['off']) for n in rim['notches']],
                         [(1, 14., True, 0), (2, 11., True, 0)])
        self.assertTrue(all(n['points'] > 100 and n['reach_m'] >= 9. for n in rim['notches']))
        self.assertEqual(rim['walls']['low'], 0)
        self.assertGreater(rim['walls']['points'], 200)
        self.assertGreater(rim['walls']['through_openings'], 20)
        self.assertEqual(rim['buttresses'], dict(listed=2, fallen=[]))

    def test_the_rim_check_fails_a_package_whose_rim_is_not_what_it_says(self):
        def no_record(doc, gen):
            del gen['rim']

        def record_of_another_shape(doc, gen):
            gen['rim']['label'] = 'read me'

        def a_sea(doc, gen):
            gen['sea'] = dict(level_m=0.)

        def a_higher_lip(doc, gen):
            gen['rim']['lip_y_m'] += 1.

        def a_notch_left_out(doc, gen):
            del gen['rim']['openings'][2]

        def a_notch_somewhere_else(doc, gen):
            gen['rim']['openings'][2]['centre_m'][0] -= 9.

        def a_lower_sill(doc, gen):
            gen['rim']['openings'][1]['floor_y_m'] -= 2.

        def a_taller_buttress(doc, gen):
            gen['buttresses'][0]['stands_m'] += 2.

        def another_sun(doc, gen):
            gen['sun_gate']['notches'][1]['days'][0]['lit_h'] += 1.
        ok, rows = self.check(no_record)
        self.assertFalse(ok)
        self.assertIn('error', rows)
        ok, rows = self.check(record_of_another_shape)
        self.assertEqual((ok, rows), (False, dict(error='rim record: fields')))
        ok, rows = self.check(a_sea)
        self.assertEqual((ok, rows['no_sea']), (False, False))
        ok, rows = self.check(a_higher_lip)
        self.assertFalse(ok)
        self.assertGreater(rows['lip']['off'], 1000)
        ok, rows = self.check(a_notch_left_out)
        self.assertFalse(ok)
        self.assertGreater(rows['lip']['off'], 50)
        self.assertGreater(rows['walls']['low'], 5)
        ok, rows = self.check(a_notch_somewhere_else)
        self.assertFalse(ok)
        self.assertFalse(rows['notches'][1]['open_to_the_tile_edge'])
        ok, rows = self.check(a_lower_sill)
        self.assertFalse(ok)
        self.assertFalse(rows['notches'][0]['open_to_the_tile_edge'])
        ok, rows = self.check(a_taller_buttress)
        self.assertFalse(ok)
        self.assertEqual([b['id'] for b in rows['buttresses']['fallen']], ['cabinet_1'])
        ok, rows = self.check(another_sun)
        self.assertEqual((ok, rows['sun_gate_ok']), (False, False))

    def test_the_check_s_own_command_prints_the_rim_where_the_sea_stood(self):
        out = io.StringIO()
        with mock.patch.object(Rim, 'run', return_value=self.result), contextlib.redirect_stdout(out), \
                mock.patch('sys.argv', ['rim', '--package', self.pkg, '--room', str(self.room), '--world-scale', '10']):
            try:
                with self.assertRaises(SystemExit) as stop:
                    Rim.main()
            finally:
                world.set_world(1.)
        self.assertEqual(stop.exception.code, 0)
        lines = out.getvalue().splitlines()
        self.assertEqual(lines[-1], 'CHECKS water=True grounded=True walk=True footprints=True rim=True climb=True '
                                    'fractals=True firepits=True overall=True')
        self.assertEqual(sum(line.startswith('SUN_GATE ') for line in lines), 25)     # 12 days twice, and a note
        self.assertEqual(sum(line.startswith('RIM ') for line in lines), 1)

    @unittest.skipUnless(os.environ.get('LANDSCAPE_RIM_TWICE'), 'set LANDSCAPE_RIM_TWICE=1: a second whole package (a minute)')
    def test_the_same_room_makes_the_same_bytes(self):
        again = os.path.join(workdir('rim_pkg_again'), 'pkg')
        world.set_world(W10)
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                G.generate(self.room, again, edge='rim')
        finally:
            world.set_world(1.)
        a, b = tree(self.pkg), tree(again)
        self.assertEqual(sorted(a), sorted(b))
        self.assertEqual([name for name in a if a[name] != b[name]], [])
        for name, data in a.items():
            if name.endswith('.json'):
                self.assertNotIn(b'\r', data)


# ---------------------------------------------------------------- the stand-in's whole package
@unittest.skipUnless(TEN_TIMES, 'set LANDSCAPE_TEN_TIMES=generate (about ten minutes) or to a package folder of the '
                                'stand-in garage made with --world-scale 10 --edge rim')
class TenTimes(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.room = ROOMS/'garage_nominal'
        cls.pkg = TEN_TIMES
        world.set_world(W10)
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                if TEN_TIMES == 'generate':
                    cls.pkg = os.path.join(workdir('rim_ten_times'), 'pkg')
                    G.generate(cls.room, cls.pkg, edge='rim', tutorial=True)
                cls.result = Rim.run(cls.pkg, cls.room)
        finally:
            world.set_world(1.)

    def test_the_stand_in_s_rim_stands(self):
        r = self.result
        self.assertTrue(r['rim_ok'], r['rim'])
        rim = r['rim']
        self.assertEqual((rim['lip']['height_m'], rim['lip']['off']), (27., 0))
        self.assertEqual([(n['opening'], n['floor_m'], n['open_to_the_tile_edge']) for n in rim['notches']], [(1, 11., True)])
        self.assertEqual(rim['walls']['low'], 0)
        self.assertTrue(rim['no_sea'] and rim['tiles_ok'] and rim['sun_gate_ok'])

    def test_the_other_checks_hold_on_the_rim(self):
        r = self.result
        self.assertEqual({k: r[k] for k in ('water_ok', 'grounded_ok', 'walk_ok', 'fractals_ok', 'firepits_ok')},
                         dict(water_ok=True, grounded_ok=True, walk_ok=True, fractals_ok=True, firepits_ok=True))


def tearDownModule():
    import shutil
    from pipeline.landscape.generator.tests.test_generator import RUN_DIR
    shutil.rmtree(RUN_DIR, ignore_errors=True)
