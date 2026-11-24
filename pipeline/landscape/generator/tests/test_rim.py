"""The rim: great cliffs for walls, a desert on top, no sea (Run 4, packet WORLD-2).

    python -B -m unittest pipeline.landscape.generator.tests.test_rim -v

The record and the surface it gives, on made-up records (a notch's sides to the lip, rooms with wings, a ragged wall);
the sun gate, against rays marched one by one; the stand-in garage at ten times as generate.py raises it with
`--edge rim`, stopped where the water is planned (seven seconds: the rim is whole there); things against a wall, at a
door and under a window, on made-up rooms stopped the same way; the rim check on those lands, as built and with the
generator broken at run time (no buttress raised, no window notched, a lip under the walls), read against the room and
its inventory; the bearings; the option itself (without it nothing of the rim runs); and a whole package of a made-up
room (about a minute and a half), read back by every check with the rim check where the sea check stood.

Two more are asked for by name: LANDSCAPE_RIM_TWICE=1 makes that package a second time and compares the bytes (a
minute more), and LANDSCAPE_TEN_TIMES makes and checks the stand-in's own whole package (`generate`: about ten
minutes; or a package folder of corpus/rooms/garage_nominal made with `--world-scale 10 --tutorial --edge rim`). That
the island's own bytes are what they were is test_fractals.TrueSize's pinned digests at true size; at ten times the
two packages were compared when the option was built and again when it was mended (WORLD-2's reports).
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
from pipeline.landscape.generator.land import parse_objects
from pipeline.landscape.generator.tests.test_generator import ROOMS, tree, workdir
from pipeline.landscape.harness import read_package
from pipeline.landscape.harness.common import load_json
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


# The same box of a room, to try a buttress at a door and at a window. A locker 1.6 m tall stands 5 cm off the south
# wall, half of it in front of the window (whose sill is at 1.1 m) and half beside it; a desk stands under the north
# window; a bench stands against the west wall, its end across the door; a trunk stands in the door, the wall nowhere
# behind it.
WAYS_ROOM = dict(
    size=(2.4, 2.4, 3.),
    things=[('locker_1', 'cabinet', (.9, 1.6, .3), (-.05, 1.3), {}),
            ('desk_1', 'desk', (.8, .75, .4), (.5, -1.25), {}),
            ('bench_1', 'box', (.4, .45, 1.), (-.97, .1), {}),
            ('trunk_1', 'cardboard box', (.4, .4, .5), (-.95, -.7), {})],
    openings=[('window_south', 'window', 'z1', .4, 1.1, (.8, .8)), ('window_north', 'window', 'z0', .6, 1.4, (.5, .6)),
              ('entry', 'door', 'x0', -.6, 0., (.8, 2.))],
    spawn=(.2, .3))


class Stop(Exception):
    pass


def raised(room, wall_join=None, room_of_the_rim=None, also=(), **patches):
    """A room at ten times as generate.py raises it with the rim, stopped where the water is planned: the base grid,
    its heights and owners, the plain, the objects, the rim's plan, the water's way out, and the room and inventory
    as the generator magnified them. `wall_join` is the generator's own option; `room_of_the_rim` changes the room
    the rim is planned from (the generator's fault, made up: the land's own room stays the true one); `also` are more
    patchers; `patches` set rim.py's own names for the run."""
    got = {}
    real = Rim.plan

    def plan(room_, inventory, *a, **k):
        got.update(room=room_, inventory=inventory)
        got['rim'] = real(room_of_the_rim(room_) if room_of_the_rim else room_, inventory, *a, **k)
        return got['rim']

    def stop(grid, room_, objects, h, base, inside, spawns, setup, noise, outside, sea=None, detail=None):
        got.update(grid=grid, h=list(h), base=list(base), objects=objects, inside=list(inside), way=sea)
        raise Stop()
    world.set_world(W10)
    try:
        with contextlib.ExitStack() as stack:
            for name, value in patches.items():
                stack.enter_context(mock.patch.object(Rim, name, value))
            for patcher in also:
                stack.enter_context(patcher)
            stack.enter_context(mock.patch.object(G, 'plan_and_carve', stop))
            stack.enter_context(mock.patch.object(G.Rim, 'plan', plan))
            stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
            try:
                G.generate(room, os.path.join(workdir('never'), 'pkg'), edge='rim', wall_join=wall_join)
            except Stop:
                pass
    finally:
        world.set_world(1.)
    return got


def height(land, x, z):
    return land['grid'].tri_height(land['h'], x, z)


class Land:
    """A land of `raised` as a check reads a package's terrain: its heights on the grid's own triangles."""

    def __init__(self, land):
        self.grid, self.h = land['grid'], land['h']

    def height(self, x, z):
        return self.grid.tri_height(self.h, x, z)


def checked(land, room=None, change=None):
    """The rim check on a land of `raised`, read as its package would be: what the package says of its rim
    (rim.blocks, as written and read back), the land's four corners for its tiles, the room and the inventory as
    magnified. `room` is another room to prove it against; `change(x_generator)` alters what the package says."""
    grid, rim, terrain = land['grid'], land['rim'], Land(land)
    doc = dict(x_generator=dict(cell_m=grid.cell, world_scale=W10, **rim.blocks(terrain.height)), water=[], setup=rim.setup)
    doc = json.loads(json.dumps(doc, allow_nan=False))
    if change:
        change(doc['x_generator'])
    meshes = dict(land=[dict(positions=[[x, 0., z] for x in (grid.xs[0], grid.xs[-1]) for z in (grid.zs[0], grid.zs[-1])])])
    return Rim.rim_checks(doc, meshes, terrain, room or land['room'], land['inventory'])


def clears(model, x, z, sun, slack, step=.2, start=0.):
    """Whether the ray from the floor at (x, z) toward the sun stays `slack` over the rim's surface (the room's own
    things aside) until it stands over the lip; read from `start` along it on."""
    dx, dy, dz = sun
    t = start+step
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
            a_centre_as_text=dict(openings=[dict(notch, centre_m='2,15')]),
            # A whole number too large for a float is out of bounds like any other, never an overflow.
            a_lip_of_400_digits=dict(lip_y_m=10**400), a_wind_of_400_digits=dict(home_wind_deg=-10**400),
            a_point_of_400_digits=dict(outline_m=[[0, 0], [10**400, 0], [1, 1]]),
            a_width_of_400_digits=dict(openings=[dict(notch, width_m=10**400)]),
            a_floor_of_400_digits=dict(openings=[dict(notch, floor_y_m=-10**400)]))
        for name, more in bad.items():
            with self.subTest(name):
                with self.assertRaises(ValueError):
                    Rim.validate(record(**more))
                with self.assertRaises(ValueError):
                    Rim.Model(json.loads(json.dumps(record(**more))) if 'not_a_number' not in name and 'without_end' not in name
                              else record(**more))
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

    def test_a_notch_s_sides_run_straight_through_the_cliff_and_its_mouth_widens_from_the_lip(self):
        m = self.m
        f = m.openings[0]
        # Inside the cliff's thickness: a scarp of 82 degrees from each side of the window's own width.
        for d in (.25, .5, 1.):
            self.assertAlmostEqual(m.height(6.+d, 18.), 11.+m.tan*d, places=6)
            self.assertAlmostEqual(m.height(-2.-d, 18.), 11.+m.tan*d, places=6)
            self.assertEqual(m.above(6.+d, 18., 3.)[1], 'cliff')
        # At the lip's height the mouth is wider by a scarp's lean, and from the lip on 30 degrees a side wider.
        lean = (24.-11.)/m.tan
        for past in (0., 1., 4.):
            a = m.lip_out+past+1e-6
            wide = 4.+lean+past*math.tan(math.radians(30.))
            self.assertLess(m.height(2.+wide-.02, 15.+a), 24., past)
            self.assertEqual(m.height(2.+wide+.02, 15.+a), 24., past)
            self.assertLess(m.height(2.-wide+.02, 15.+a), 24., past)
            self.assertEqual(m.height(2.-wide-.02, 15.+a), 24., past)
        # Its foot starts to widen a little further out: where those two faces part at the lip's height over the lip.
        self.assertAlmostEqual(f['flare_from'], m.lip_out+lean*(1-math.cos(math.radians(30.)))/math.sin(math.radians(30.)), places=9)
        self.assertGreater(f['flare_from'], m.lip_out+.4)
        a = f['flare_from']+4.
        wide = 4.+4.*math.tan(math.radians(30.))
        self.assertEqual(m.height(2.+wide-.05, 15.+a), 11.)
        self.assertEqual(m.height(2.-wide+.05, 15.+a), 11.)
        self.assertAlmostEqual(m.height(2.+wide+1., 15.+a), 11.+m.tan*math.cos(math.radians(30.)), places=6)
        self.assertEqual(m.height(2.+wide+4., 15.+a), 24.)

    def test_no_side_of_an_opening_meets_the_cliff_in_a_knife_edge(self):
        """From the wall line all the way to the lip, and from an opening's side as far as its scarp stands under
        the lip, the land never falls as one goes out or away from the opening: a climb up the side goes on up the
        face. (Sides that widened from the wall line fell away behind; a widening that started at the lip left a
        face falling outward just inside it, 624 lattice points of it under 65 m walls.) On this record, on the
        stand-in's shape, and under walls so tall that the face steepens. Past the lip a notch widens, and there
        too nothing falls as one goes away from it: a scarp's top is the level lip, never a crest."""
        shapes = (record(), record(lip_y_m=27., lip_out_m=3.795, seam_out_m=20.295, wrap_out_m=21.795),
                  record(lip_y_m=65., lip_out_m=8., seam_out_m=24.5, wrap_out_m=26.))
        for rec in shapes:
            m = Rim.Model(rec)
            inside = past = 0
            for k in range(1, 99):
                a = k*.1
                for n in range(0, 110):
                    b = n*.1
                    here = m.height(6.+b, 15.+a)
                    self.assertGreaterEqual(m.height(6.+b+.1, 15.+a), here-1e-9, (rec['lip_y_m'], a, b))
                    if a+.1 <= m.lip_out+1e-9:
                        self.assertGreaterEqual(m.height(6.+b, 15.+a+.1), here-1e-9, (rec['lip_y_m'], a, b))
                        inside += 1
                    else:
                        past += 1
            self.assertGreater(inside, 3000)
            self.assertGreater(past, 2000)
        m = self.m
        for k in range(1, 34):
            a = k*.1
            for n in range(0, 24):
                b = n*.1
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

    def test_in_a_room_with_wings_a_notch_leaves_every_other_wall_its_cliff(self):
        """A U: two wings 10 m apart, joined at one end. A window in one wing's inner wall looks across at the
        other's. Its notch ends before that wall: the wall keeps its cliff, its lip and two metres of level sand,
        and a scarp of the cliffs' angle falls from there to the notch's floor. (The notch ran on through the other
        wing's cliff from behind: its crest stood at the sill's 11 m.)"""
        u = [[-15., -15.], [-15., 15.], [15., 15.], [15., -15.], [5., -15.], [5., 5.], [-5., 5.], [-5., -15.]]
        rec = record(outline_m=u, openings=[dict(kind='notch', centre_m=[-5., -5.], width_m=4., floor_y_m=11., flare_deg=30.)])
        m, bare = Rim.Model(rec), Rim.Model(dict(rec, openings=[]))
        self.assertFalse(m.plain)
        self.assertEqual(m.above(-3., -5., 2.), (11., 'sand'))                  # the notch's own floor
        self.assertAlmostEqual(m.height(-4.5, -5.), m.tan*.5, places=6)         # the face under its sill
        room = m.lip_out+Rim.KEEP_TOP_M
        self.assertAlmostEqual(m.height(-1., -5.), 24.-m.tan*(6.-room), places=6)      # the scarp back up
        self.assertEqual(m.above(-1., -5., 4.)[1], 'cliff')
        for x in (-.3, 0., 1., 1.6):
            self.assertEqual(m.height(x, -5.), 24.)                             # the other wall's lip, whole
        for z in (-14.5, -9., -5., -1.):
            crest = max(m.height(5.-k*.25, z) for k in range(1, 40) if not m.inside(5.-k*.25, z))
            self.assertEqual(crest, 24., z)
            for k in range(1, 14):                                              # and its face, as with no window
                self.assertEqual(m.height(5.-k*.25, z), bare.height(5.-k*.25, z), (z, k))
        # An L: a window whose notch widens toward the wall round the inner corner is kept off that wall the same way.
        ell = record(outline_m=[[-10., -15.], [-10., 15.], [10., 15.], [10., 0.], [0., 0.], [0., -15.]],
                     openings=[dict(kind='notch', centre_m=[0., -10.], width_m=4., floor_y_m=11., flare_deg=30.)])
        m = Rim.Model(ell)
        self.assertEqual(m.height(9., -9.), 11.)
        self.assertAlmostEqual(m.height(9., -5.5), 24.-m.tan*(5.5-room), places=6)
        for x in (5., 8., 9.5):
            self.assertEqual(max(m.height(x, -k*.25) for k in range(1, 40)), 24., x)

    def test_a_ragged_wall_is_one_wall(self):
        """A scan gives one wall several edges, a few centimetres off one line. A notch in it is the notch of the
        plain room: the edges beside its own are its own wall, not walls to keep off."""
        ragged = record(outline_m=[[-10., -15.], [-10., 15.], [-3., 15.02], [4., 14.97], [10., 15.], [10., -15.]])
        m, plain = Rim.Model(ragged), self.m
        self.assertFalse(m.plain)
        self.assertEqual(m.openings[0]['wall'], frozenset((1, 2, 3)))
        self.assertEqual(m.openings[1]['wall'], frozenset((0,)))
        for a in (3., 5., 9.):
            for x in (-6., -1.9, 2., 5.9, 8.):                  # the floor, in front of every edge of the wall
                if plain.height(x, 15.+a) == 11.:
                    self.assertEqual(m.height(x, 15.+a+.05), 11., (a, x))
        self.assertEqual(m.height(8., 15.+9.), 11.)             # (the widening, in front of the edge beside its own)
        for x, z in ((0., 16.), (7., 17.), (-8., 20.), (9.5, 24.), (13., 18.), (-11., -4.), (-13., 5.)):
            self.assertAlmostEqual(m.height(x, z), plain.height(x, z), delta=1.)


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
        self.assertEqual((gate['latitude_deg'], gate['neg_z_bearing_deg'], gate['step_min'], gate['time'], gate['shade']),
                         (30., 0., Rim.SUN_STEP_MIN, 'apparent solar', 'the notch alone'))
        self.assertEqual(len(gate['notches']), 1)       # the bay has no hours
        notch = gate['notches'][0]
        self.assertEqual((notch['opening'], notch['faces_deg'], notch['faces_compass_deg'], notch['beam'], notch['through']),
                         (0, 180., 180., True, True))
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
        self.assertEqual(days[172], dict(day=172, from_h=None, to_h=None, lit_h=0., reach_m=0., through_from_h=None,
                                         through_to_h=None, through_h=0.))
        self.assertGreater(days[355]['reach_m'], days[80]['reach_m'])
        # The hours a ray comes through at all hold the hours one lands on the floor.
        for d in notch['days']:
            self.assertGreaterEqual(d['through_h'], d['lit_h'], d)
            if d['lit_h']:
                self.assertLessEqual(d['through_from_h'], d['from_h'], d)
                self.assertGreaterEqual(d['through_to_h'], d['to_h'], d)
        json.dumps(gate, allow_nan=False)

    def test_a_notch_the_sun_never_enters_says_it_has_no_beam(self):
        narrow = record()
        narrow['openings'][0]['width_m'] = 1.
        gate = Rim.sun_gate(narrow, 30., 180.)          # the room turned half round: a narrow window that looks north
        notch = gate['notches'][0]
        # In the room's frame the window looks the way it did (as the home wind blows the way it did); only the
        # compass bearing has turned.
        self.assertEqual((notch['faces_deg'], notch['faces_compass_deg']), (180., 0.))
        self.assertEqual((notch['beam'], notch['through']), (False, False))
        self.assertEqual(notch['note'], 'no beam: no sun comes through this notch on any of the days')
        self.assertTrue(all(d['lit_h'] == 0. and d['from_h'] is None and d['through_h'] == 0. and d['through_from_h'] is None
                            for d in notch['days']))
        self.assertNotIn('note', Rim.sun_gate(self.rec, 30., 0.)['notches'][0])

    def test_a_sun_that_comes_through_and_meets_a_cliff_is_told_from_no_sun(self):
        """The 8 m window, looking north: the summer sun rises north of east and comes through it low, on to the
        cliffs and never down to the floor. The gate lists those hours beside an empty floor, and says which it is.
        (It listed the row of a notch the sun never enters.)"""
        notch = Rim.sun_gate(self.rec, 30., 180.)['notches'][0]
        self.assertEqual((notch['beam'], notch['through']), (False, True))
        self.assertEqual(notch['note'], 'no beam: the sun comes through this notch and meets the cliffs, never the floor')
        days = {d['day']: d for d in notch['days']}
        summer = days[172]
        self.assertEqual((summer['from_h'], summer['to_h'], summer['lit_h'], summer['reach_m']), (None, None, 0., 0.))
        self.assertGreater(summer['through_h'], 3.5)
        self.assertLess(summer['through_from_h'], 6.)
        self.assertGreater(summer['through_to_h'], 18.)
        self.assertEqual(days[355]['through_h'], 0.)        # in winter the sun stays south of the wall
        # Marched one by one: each such ray passes the notch's wall over the floor, clear of the rim from there on,
        # and comes down to the floor's height only past a wall.
        m, f = self.m, self.notch
        tried = hours = 0
        for k in range(10, 39):
            sun = sun_position(30., 172, k*.5, 180.)['direction_room']
            rays = list(Rim._rays(m, f, sun))
            if not rays:
                continue
            hours += 1
            self.assertEqual(list(Rim._beam(m, f, sun)), [])
            self.assertIsNone(Rim._lit(m, f, sun))
            for reach, x, z, lands in rays[::max(1, len(rays)//8)]:
                tried += 1
                self.assertFalse(lands)
                self.assertFalse(m.inside(x, z))
                at_wall = reach/(sun[0]*f['nx']+sun[2]*f['nz'])       # along the ray, where it passes the notch's wall
                self.assertGreater(at_wall*sun[1], 0.)
                self.assertTrue(m.inside(x+sun[0]*(at_wall-.05), z+sun[2]*(at_wall-.05)))
                self.assertTrue(clears(m, x, z, sun, -.03, start=at_wall), (k, x, z))
        self.assertGreaterEqual(hours, 6)
        self.assertGreater(tried, 30)


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
        lip = broken = faces = 0
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
                if not near and not any(j['x0']-.2 <= px <= j['x1']+.2 and j['z0']-.2 <= pz <= j['z1']+.2 for j in self.rim.joins):
                    faces += 1
                    for s in (1., 2., 3.):      # the face: 82 degrees (where no thing is joined into it)
                        self.assertAlmostEqual(height(land, px+ox*s, pz+oz*s), m.tan*s, delta=.02, msg=(px, pz, s))
        self.assertGreater(lip, 700)
        self.assertGreater(broken, 20)
        self.assertGreater(faces, 100)
        for x, z in ((34., 39.), (-34., 39.), (-34., -39.), (36., -41.)):       # and round the corners
            self.assertAlmostEqual(height(land, x, z), 27., delta=.005)

    def test_the_window_s_notch_opens_onto_its_low_desert(self):
        m, land = self.m, self.land
        self.assertAlmostEqual(height(land, 30.5, -5.), m.tan*.5, delta=.02)       # the cliff's face
        # (Under the sill the bicycle leans: the face there is its buttress, up to its crest, under the sill.)
        self.assertTrue(9. < height(land, 30.5, -17.5) < 11.)
        for a in (1.8, 3., 5., 8., 9.9):
            for b in (-4.7, 0., 4.7):
                self.assertAlmostEqual(height(land, 30.+a, -17.5+b), 11., delta=.005, msg=(a, b))
        # At the tile edge its foot has widened 30 degrees a side, and the sand beyond lies at its floor.
        flare_from = m.openings[1]['flare_from']
        self.assertAlmostEqual(flare_from, m.lip_out+.6, delta=.01)
        wide = 5.+(9.9-flare_from)*math.tan(math.radians(30.))
        self.assertGreater(wide, 8.1)
        for b in (-wide+.3, wide-.3):
            self.assertAlmostEqual(height(land, 39.9, -17.5+b), 11., delta=.005)
        self.assertEqual(m.far(60., -17.5), 11.)
        self.assertEqual(m.far(60., 20.), 27.)
        # Its sides are scarps as tall as the cliff: the sill opens no walk to the high desert.
        for b in (-wide-3., wide+3.):
            self.assertAlmostEqual(height(land, 39.9, -17.5+b), 27., delta=.005)
        for a in (3., 3.7, 6., 9.):
            half = 5.+max(0., a-flare_from)*math.tan(math.radians(30.))
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

    def test_the_things_standing_against_the_walls_are_parts_of_the_cliffs(self):
        """Six of the stand-in's twelve floor things stand against a wall, a side turned to it: the desk (5 cm off
        it, true size), the shelving unit (7, in a corner), the couch (7.5, in a corner), the office chair (8), the
        bicycle (10, under the window) and the easel (12.5). Each is joined into the cliff, and so is the storage
        tote, 25 cm off: as far as a scan may place a thing that stands against a wall. (With a slot's 2 cm as the
        rule, none was.)"""
        joins = [(j['o']['id'], round(j['gap'], 3), tuple(round(v) for v in j['out'])) for j in self.rim.joins]
        self.assertEqual(joins, [('bicycle_1', 1., (1, 0)), ('couch_1', .75, (0, -1)), ('desk_1', .5, (0, 1)),
                                 ('easel_1', 1.25, (-1, 0)), ('office_chair_1', .8, (0, 1)), ('shelving_unit_1', .7, (1, 0)),
                                 ('storage_tote_1', 2.5, (1, 0))])
        self.assertEqual((Rim.JOIN_GAP_M, self.rim.join_gap), (.25, .25))
        # The bin and the cardboard box, 27.5 cm off their walls, stand free.
        self.assertFalse({'bin_1', 'cardboard_box_1'} & {j['o']['id'] for j in self.rim.joins})
        # Each to the wall it is turned to: the couch's end is 36 cm (true) from the -X wall, the shelving unit's
        # side 41 cm from the +Z wall, and neither is joined there for standing near the corner.
        land = self.land
        # The desk, a mesa 7.5 m tall: from its top the land runs level across the gap and into the +Z cliff. (Read
        # clear of the things that stand on the desk: the join is the desk's own top, not theirs.)
        top = height(land, 10., 33.)
        self.assertAlmostEqual(top, 7.5, delta=1.)
        for z in (34., 34.3, 34.5, 34.75, 35., 35.5, 35.9):
            self.assertAlmostEqual(height(land, 10., z), top, delta=.1, msg=z)
        self.assertAlmostEqual(height(land, 10., 36.4), self.m.tan*1.4, delta=.02)       # the cliff's own face, above it
        self.assertGreater(height(land, 13.5, 33.), top+1.)                             # a thing on the desk
        self.assertAlmostEqual(height(land, 13.5, 35.), top, delta=.1)                  # behind it, the desk's top still
        # The bicycle, a fin through the middle of its box: joined from its crest, 3 m from the box's side.
        for z, tall in ((-17.5, 9.), (-15., 8.), (-24., 6.5)):
            crest = max(height(land, 26.+k*.1, z) for k in range(31))
            self.assertGreater(crest, tall)
            for x in (27., 28., 29.2, 29.5, 30., 30.6):
                self.assertAlmostEqual(height(land, x, z), crest, delta=.1, msg=(x, z))
        crest = max(height(land, 26.+k*.1, -17.5) for k in range(31))
        # It stands under the window (sill 11 m), and stays under it: the notch is open above.
        self.assertLess(crest, 11.)
        self.assertEqual(height(land, 33., -17.5), 11.)
        # The rim check proves the same from the inventory and the land.
        ok, rows = checked(land)
        self.assertTrue(ok, rows)
        got = rows['buttresses']
        self.assertEqual([(r['id'], r['faces_deg']) for r in got['joined']],
                         [('bicycle_1', 90.), ('couch_1', 0.), ('desk_1', 180.), ('easel_1', 270.),
                          ('office_chair_1', 180.), ('shelving_unit_1', 90.), ('storage_tote_1', 90.)])
        self.assertEqual({r['id']: r['edge_m'] for r in got['joined'] if r['id'] != 'bicycle_1'},
                         dict(couch_1=8.3, desk_1=7.5, easel_1=16.5, office_chair_1=10.5, shelving_unit_1=20.2,
                              storage_tote_1=3.8))
        for r in got['joined']:
            self.assertTrue(r['ok'], r)
            self.assertAlmostEqual(r['stands_m'], r['edge_m'], delta=Rim.JOIN_TOL_M, msg=r)
            # Every line holds. (Where the office chair's top steps, a buttress at the mean of the two crests beside
            # a line stood a hand's breadth under the taller: it carries the taller now.)
            self.assertEqual((r['lines'], r['held']), (9, 9), r)
        self.assertEqual((got['listed'], got['fallen'], got['unlisted'], got['unfounded'], got['left_free']), (7, [], [], [], []))
        self.assertEqual(got['rule'], dict(gap_m=.25, lifted_m=.5))

    def test_the_slot_rule_is_a_switch(self):
        """`--wall-join 0.02`: only a thing nearer a wall than a slot joins it, so the gap behind the desk (5 cm, true
        size) stays an open slot. The package says which rule made it, and the check names what that left free."""
        near = raised(ROOMS/'garage_nominal', wall_join=Rim.SLOT_GAP_M)
        self.assertEqual(near['rim'].joins, [])
        self.assertLess(height(near, 13.5, 34.75), 1.5)
        self.assertGreater(height(self.land, 13.5, 34.75), height(near, 13.5, 34.75)+5.)
        ok, rows = checked(near)
        self.assertTrue(ok, rows)
        got = rows['buttresses']
        self.assertEqual(got['rule'], dict(gap_m=.02, lifted_m=.5))
        self.assertEqual((got['joined'], got['listed']), ([], 0))
        self.assertEqual(got['left_free'], ['bicycle_1', 'couch_1', 'desk_1', 'easel_1', 'office_chair_1', 'shelving_unit_1',
                                            'storage_tote_1'])
        # A package that says the slot rule made it, over a land the wider rule made, is found out the other way.
        ok, rows = checked(self.land, change=lambda gen: gen['rim_join'].update(gap_m=.02))
        self.assertFalse(ok)
        self.assertEqual(rows['buttresses']['unfounded'],
                         ['bicycle_1', 'couch_1', 'desk_1', 'easel_1', 'office_chair_1', 'shelving_unit_1', 'storage_tote_1'])
        # The founder's rule read narrowly (15 cm, no room for a scan's error) is one more value of the same switch.
        mid = raised(ROOMS/'garage_nominal', wall_join=.15)
        self.assertEqual([j['o']['id'] for j in mid['rim'].joins],
                         ['bicycle_1', 'couch_1', 'desk_1', 'easel_1', 'office_chair_1', 'shelving_unit_1'])
        ok, rows = checked(mid)
        self.assertTrue(ok, rows)
        self.assertEqual((rows['buttresses']['rule']['gap_m'], rows['buttresses']['left_free']), (.15, ['storage_tote_1']))

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
        # At midwinter the low sun comes in from the south-east and meets the far cliff before the floor: the gate
        # lists the four hours it comes through beside an empty floor (it listed the same row as for no sun at all).
        self.assertEqual((days[355]['lit_h'], days[355]['from_h'], days[355]['reach_m']), (0., None, 0.))
        self.assertGreater(days[355]['through_h'], 4.)
        self.assertLess(days[355]['through_from_h'], 7.5)
        self.assertGreater(days[355]['through_to_h'], 11.)
        self.assertEqual(sum(d['lit_h'] > 0 for d in notch['days']), 9)
        self.assertEqual(sum(d['through_h'] > 0 for d in notch['days']), 12)
        self.assertEqual((notch['faces_compass_deg'], notch['through']), (90., True))
        self.assertNotIn('note', notch)


# ---------------------------------------------------------------- things against a wall
class Buttress(unittest.TestCase):
    """The made-up cabinet room at ten times: 24 by 30 m under walls of 24 m."""

    @classmethod
    def setUpClass(cls):
        cls.room = made_room('cabinet_room', **CABINET_ROOM)
        cls.land = raised(cls.room)
        cls.slots = raised(cls.room, wall_join=Rim.SLOT_GAP_M)
        cls.free = raised(cls.room, JOIN_GAP_M=0., LIFTED_JOIN_M=-1.)
        cls.rim = cls.land['rim']

    def test_which_things_are_joined_into_the_cliff(self):
        self.assertEqual([(j['o']['id'], j['lifted'], round(j['gap'], 3), j['out']) for j in self.rim.joins],
                         [('cabinet_1', False, .05, (1., -0.)), ('shelf_1', True, 1.5, (-1., 0.)),
                          ('crate_1', False, .5, (-0., -1.))])
        self.assertEqual((self.rim.join_gap, self.rim.lifted_gap), (.25, .5))
        self.assertEqual([j['o']['id'] for j in self.slots['rim'].joins], ['cabinet_1', 'shelf_1'])
        self.assertEqual(self.free['rim'].joins, [])

    def test_a_thing_standing_against_a_wall_runs_into_the_cliff(self):
        land, free = self.land, self.free
        top = height(land, 11.5, 5.)                 # the cabinet's own land by the side it turns to the wall
        self.assertGreater(top, 9.)
        for x in (12.3, 12.6, 12.9):                 # past the wall line: the cliff's face stood lower here
            self.assertAlmostEqual(height(land, x, 5.), top, delta=.01)
            self.assertAlmostEqual(height(free, x, 5.), self.rim.model.tan*(x-12.), delta=.02)
        # No groove is left at the cabinet's own side: from its top to the cliff the land nowhere dips. (A buttress
        # raised only past the box's side left the box's own shoulder under it: 8.25 m here, under a top of 9.)
        for k in range(15):
            self.assertAlmostEqual(height(land, 11.5+k*.05, 5.), top, delta=.01, msg=k)
        self.assertLess(height(free, 11.95, 5.), top-.5)
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

    def test_a_thing_a_slot_away_is_joined_unless_slots_are_kept(self):
        # The crate stands 5 cm (true size) off the -Z wall. It stands against the wall: its top runs into the cliff.
        top = height(self.land, -5., -13.5)
        self.assertGreater(top, 4.)
        for z in (-14.6, -14.75, -15., -15.4):
            self.assertAlmostEqual(height(self.land, -5., z), top, delta=.05, msg=z)
        # With the slot rule, 50 cm of floor stays between it and the cliff.
        self.assertLess(height(self.slots, -5., -14.75), 1.5)
        self.assertEqual(height(self.slots, -5., -14.75), height(self.free, -5., -14.75))

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


class Scanned(unittest.TestCase):
    """A scan's error: a box placed through a wall. (The corpus's scans do it by up to 8 cm.)"""

    @classmethod
    def setUpClass(cls):
        # A dresser in the south-east corner: 2 cm off the south wall, and poking 6 cm through the east wall.
        spec = dict(CABINET_ROOM, things=[('dresser_1', 'cabinet', (.8, 1., .4), (.86, 1.28), {}),
                                          ('table_1', 'table', (.7, .6, .5), (-.3, -.3), {})])
        cls.room = made_room('scanned_room', **spec)
        cls.land = raised(cls.room)
        cls.rim = cls.land['rim']

    def test_a_box_through_one_wall_is_still_joined_to_the_other(self):
        """Its long side's east end stands past the wall line, where no wall lies ahead of it: the side was dropped
        whole, and a slot was left between the dresser and the south wall."""
        joins = sorted((j['o']['id'], round(j['gap'], 3), tuple(round(v) for v in j['out'])) for j in self.rim.joins)
        self.assertEqual(joins, [('dresser_1', -.6, (1, 0)), ('dresser_1', .2, (0, 1))])
        land = self.land
        top = height(land, 8., 12.)
        self.assertAlmostEqual(top, 10., delta=1.)
        for x in (5.5, 8., 10.5):
            for z in (14.85, 14.95, 15.3, 15.9):
                self.assertAlmostEqual(height(land, x, z), top, delta=.05, msg=(x, z))      # into the south cliff
        for z in (11.5, 13., 14.):
            for x in (12.1, 12.6, 13.2):
                self.assertGreaterEqual(height(land, x, z), top-.05, (x, z))                 # and the east one
        ok, rows = checked(land)
        self.assertTrue(ok, rows)
        self.assertEqual([(r['id'], r['gap_m'], r['faces_deg'], r['ok']) for r in rows['buttresses']['joined']],
                         [('dresser_1', -.6, 90., True), ('dresser_1', .2, 180., True)])

    def test_the_side_is_read_where_a_wall_lies_ahead_of_it(self):
        m = self.rim.model
        sides = {tuple(round(v) for v in s['out']): s for s in Rim.wall_sides(m, 8.6, 12.8, 8., 4., 0.)}
        self.assertEqual(sorted(sides), [(-1, 0), (0, -1), (0, 1), (1, 0)])
        self.assertAlmostEqual(sides[(0, 1)]['gap'], .2)        # read at its middle and its west end
        self.assertAlmostEqual(sides[(1, 0)]['gap'], -.6)       # the way back in
        self.assertAlmostEqual(sides[(0, -1)]['gap'], 25.8)
        # A box wholly past the wall line turns no side to a wall from inside.
        self.assertEqual([s for s in Rim.wall_sides(m, 30., 0., 2., 2., 0.) if s['gap'] > 0], [])


class Turned(unittest.TestCase):
    """A thing turned on the grid, in a corner: the corpus's near-empty room, whose office chair stands turned 25
    degrees, 6 cm off one wall and 22 cm off the next, a seat with a taller back."""

    @classmethod
    def setUpClass(cls):
        cls.land = raised(ROOMS/'near_empty_nominal')
        cls.rim = cls.land['rim']

    def test_a_thing_joined_on_two_sides_is_read_on_each_clear_of_the_other(self):
        self.assertEqual(sorted((j['o']['id'], round(j['gap'], 2)) for j in self.rim.joins),
                         [('office_chair_1', .57), ('office_chair_1', 2.22), ('plant_1', 2.25), ('plant_1', 2.25)])
        ok, rows = checked(self.land)
        self.assertTrue(ok, rows)
        got = rows['buttresses']
        self.assertEqual((got['fallen'], got['unlisted'], got['unfounded']), ([], [], []))
        chair = [r for r in got['joined'] if r['id'] == 'office_chair_1']
        self.assertEqual([(r['faces_deg'], r['ok']) for r in chair], [(245., True), (335., True)])
        for r in chair:
            # Half of a side's lines start in the half of the chair the other side's buttress fills, and are that
            # side's to hold; every line read holds. (Read whole, the further side held 5 lines of 9 and the room
            # failed its check, though both buttresses stood.)
            self.assertTrue(4 <= r['lines'] <= 5, r)
            self.assertEqual(r['held'], r['lines'], r)

    def test_a_line_holds_the_crest_the_grid_can_carry(self):
        """Beside the chair's back the grid's triangles carry the back's height a cell sideways. Read without the
        cell, a line there takes that height for its own crest and finds the buttress under it."""
        land, rim = self.land, self.rim
        m, terrain = rim.model, Land(land)
        against = Rim._against(m, land['inventory'], W10, rim.join_gap, rim.lifted_gap)
        sides = [s for e, s, _ in against if e['id'] == 'office_chair_1']
        self.assertEqual(len(sides), 2)
        strict = [Rim._joined(terrain.height, m, s, 10., W10, (), [o for o in sides if o is not s]) for s in sides]
        cell = [Rim._joined(terrain.height, m, s, 10., W10, (), [o for o in sides if o is not s], land['grid'].cell) for s in sides]
        self.assertTrue(any(held < read for read, held, _, _ in strict), strict)
        self.assertTrue(all(held == read for read, held, _, _ in cell), cell)
        # The cell forgives a step in the thing's own top, never a buttress that is not there.
        bare = raised(ROOMS/'near_empty_nominal', also=[mock.patch.object(Rim.Rim, '_fill', lambda self, j, x, z: None)])
        ok, rows = checked(bare)
        self.assertFalse(ok)
        self.assertEqual(rows['buttresses']['fallen'], ['office_chair_1', 'plant_1'])


class Kitchen(unittest.TestCase):
    """The corpus's kitchen. A cabinet 18.5 m tall stands before the door (its north side: no wall behind it), and
    its east side stands 20 cm off the east wall, right beside the window: the last metre of that side runs into the
    flank of the window's notch, which leans back from the sill and never stands as high as the cabinet."""

    @classmethod
    def setUpClass(cls):
        cls.land = raised(ROOMS/'kitchen_nominal')
        cls.rim = cls.land['rim']

    def test_a_buttress_beside_a_notch_holds_what_the_notch_s_flank_holds(self):
        land, m = self.land, self.rim.model
        self.assertEqual([(round(j['gap'], 2), tuple(round(v) for v in j['out'])) for j in self.rim.joins
                          if j['o']['id'] == 'cabinet_2'], [(2., (1, 0))])
        notch = next(f for f in m.openings if f['depth'] is None)
        self.assertEqual((notch['x'], notch['z'], notch['half'], notch['floor']), (24., -12.5, 5., 11.))
        # The buttress stands at the cabinet's top from its side (x = 22) to the wall and into the cliff, the whole
        # of its width (z from -24 to -17); the notch's own side is at z = -17.5.
        top = height(land, 20., -20.5)
        self.assertAlmostEqual(top, 18.5, delta=.5)
        for z in (-23., -20.5, -18.5, -17.8):
            for x in (22.5, 23.5, 24.5, 25.5):
                self.assertAlmostEqual(height(land, x, z), top, delta=.05, msg=(x, z))
        # Beside it the cliff's flank is lower than the cabinet all the way out: 30 cm from the notch's side it
        # stands 13.1 m at most.
        flank = max(m.height(24.+k*.1, -17.8) for k in range(1, 100))
        self.assertAlmostEqual(flank, 11.+m.tan*.3, delta=.05)
        self.assertLess(flank, top-5.)
        ok, rows = checked(land)
        self.assertTrue(ok, rows)
        got = rows['buttresses']
        row = next(r for r in got['joined'] if r['id'] == 'cabinet_2')
        # Every line is read and holds, the ones by the notch at what the flank holds. (They were held to the
        # cabinet's own top, which no cliff reaches there; and the half of the cabinet by the door was left to a
        # buttress the door does not have. The room failed its check at 3 lines of 5.)
        self.assertEqual((row['faces_deg'], row['gap_m'], row['lines'], row['held'], row['ok']), (90., 2., 9, 9, True))
        self.assertEqual((got['fallen'], got['at_a_door']), ([], []))

    def test_what_the_flank_holds_is_still_asked_for(self):
        bare = raised(ROOMS/'kitchen_nominal', also=[mock.patch.object(Rim.Rim, '_fill', lambda self, j, x, z: None)])
        ok, rows = checked(bare)
        self.assertFalse(ok)
        self.assertIn('cabinet_2', rows['buttresses']['fallen'])
        row = next(r for r in rows['buttresses']['joined'] if r['id'] == 'cabinet_2')
        self.assertEqual((row['lines'], row['held']), (9, 0))


class Ways(unittest.TestCase):
    """A buttress at a door and at a window (the made-up WAYS_ROOM at ten times)."""

    @classmethod
    def setUpClass(cls):
        cls.room = made_room('ways_room', **WAYS_ROOM)
        cls.land = raised(cls.room)
        cls.free = raised(cls.room, JOIN_GAP_M=0.)
        cls.rim = cls.land['rim']

    def test_a_thing_in_a_door_has_no_wall_behind_it(self):
        self.assertEqual([j['o']['id'] for j in self.rim.joins], ['locker_1', 'desk_1', 'bench_1'])
        # The trunk stands in the door's width (z from -10 to -2): the bay's floor runs on behind it.
        for x in (-11.7, -12.2, -12.6):
            self.assertEqual(height(self.land, x, -7.), height(self.free, x, -7.))
            self.assertLess(height(self.land, x, -7.), 2.5)

    def test_a_buttress_stops_at_a_door(self):
        land, free = self.land, self.free
        top = height(land, -9.7, 4.)                     # the bench (4.5 m), against the -X wall from z = -4 to 6
        self.assertAlmostEqual(top, 4.5, delta=1.)
        for x in (-11.8, -12., -12.3):
            self.assertAlmostEqual(height(land, x, 4.), top, delta=.05)      # beside the door: into the cliff
            self.assertEqual(height(land, x, -3.), height(free, x, -3.))    # in the door's width: the bay's floor
            self.assertLess(height(land, x, -3.), 2.5)

    def test_a_buttress_rises_no_higher_than_a_window_s_sill(self):
        land, m = self.land, self.rim.model
        top = height(land, -3., 13.)                     # the locker (16 m), x from -5 to 4; the window from 0 to 8
        self.assertAlmostEqual(top, 16., delta=1.)
        for z in (14.75, 15.2, 15.8):
            self.assertAlmostEqual(height(land, -3., z), top, delta=.05)     # beside the window: into the cliff
        self.assertAlmostEqual(height(land, 2.5, 14.75), 11., delta=.05)     # in the window: up to the sill, no higher
        self.assertAlmostEqual(height(land, 2.5, 15.5), 11., delta=.05)      # (the face under the sill is filled to it)
        for z in (17., 18.5, 20.):
            self.assertEqual(height(land, 2.5, z), 11.)                      # the notch is open behind the locker
        # The desk (7.5 m) under the north window (sill 14 m) is joined whole: the face under the sill is the cliff.
        top = height(land, 6., -12.)
        self.assertAlmostEqual(top, 7.5, delta=1.)
        for z in (-14.6, -14.8, -15., -15.5, -15.9):
            self.assertAlmostEqual(height(land, 6., z), top, delta=.05, msg=z)
        self.assertEqual(m.above(6., -17., 2.), (14., 'sand'))

    def test_the_rim_check_reads_a_buttress_the_same_way(self):
        ok, rows = checked(self.land)
        self.assertTrue(ok, rows)
        got = rows['buttresses']
        self.assertEqual([(r['id'], r['faces_deg'], r['ok']) for r in got['joined']],
                         [('locker_1', 180., True), ('desk_1', 0., True), ('bench_1', 270., True)])
        self.assertEqual(got['at_a_door'], ['trunk_1'])
        self.assertEqual((got['fallen'], got['unlisted'], got['unfounded']), ([], [], []))
        rows = {r['id']: r for r in got['joined']}
        self.assertLess(rows['bench_1']['lines'], Rim.JOIN_LINES)       # (the lines that run into the door are not read)
        self.assertAlmostEqual(rows['locker_1']['edge_m'], 16., delta=.01)


class Check(unittest.TestCase):
    """The rim check proves a land against its room, not against the package's own word. The made-up cabinet room
    raised to the water stage, as built and with the generator broken at run time; read as its package would be."""

    @classmethod
    def setUpClass(cls):
        cls.room = made_room('check_room', **CABINET_ROOM)
        cls.land = raised(cls.room)

    def test_the_land_as_built_passes(self):
        ok, rows = checked(self.land)
        self.assertTrue(ok, rows)
        self.assertEqual(rows['walls_height'], dict(room_m=24., lip_m=24., ok=True))
        self.assertEqual(rows['face'], dict(deg=82., ok=True))
        self.assertEqual(rows['wall_line'], dict(points=4, ok=True))
        self.assertEqual(rows['openings'], dict(of_the_room=4, notches=2, bays=1, missing=[], not_the_rooms=[], ok=True,
                                                shut=[dict(id='grille', kind='vent', why='a vent needs a roof')]))
        got = rows['buttresses']
        self.assertEqual([(r['id'], r['lifted'], r['gap_m'], r['faces_deg'], r['lines'], r['held'], r['ok']) for r in got['joined']],
                         [('cabinet_1', False, .05, 90., 9, 9, True), ('shelf_1', True, 1.5, 270., 9, 9, True),
                          ('crate_1', False, .5, 0., 9, 9, True)])
        self.assertEqual([(r['edge_m'], r['stands_m']) for r in got['joined']], [(9., 9.), (15., 15.), (4., 4.)])

    def test_a_buttress_that_was_never_raised_fails(self):
        """The package still lists it, standing as high as the land stands there: its own word bears it out. The
        inventory says a cabinet 9 m tall stands half a centimetre off the wall, and the land has a gap."""
        land = raised(self.room, also=[mock.patch.object(Rim.Rim, '_fill', lambda self, j, x, z: None)])
        self.assertEqual([j['o']['id'] for j in land['rim'].joins], ['cabinet_1', 'shelf_1', 'crate_1'])
        ok, rows = checked(land)
        self.assertFalse(ok)
        got = rows['buttresses']
        self.assertEqual(got['fallen'], ['cabinet_1', 'crate_1', 'shelf_1'])
        self.assertEqual([(r['id'], r['held'], r['edge_m'], r['ok']) for r in got['joined']],
                         [('cabinet_1', 0, 9., False), ('shelf_1', 0, 15., False), ('crate_1', 0, 4., False)])
        for r in got['joined']:
            self.assertLess(r['stands_m'], 1.5, r)
        # Nothing else is wrong with that land.
        self.assertTrue(all(rows[k]['ok'] for k in ('walls_height', 'face', 'wall_line', 'openings')))
        self.assertEqual((rows['lip']['off'], rows['walls']['low']), (0, 0))

    def test_a_buttress_raised_half_way_fails(self):
        real = Rim.Rim._fill

        def half(rim, j, x, z):
            top = real(rim, j, x, z)
            return None if top is None else top/2
        ok, rows = checked(raised(self.room, also=[mock.patch.object(Rim.Rim, '_fill', half)]))
        self.assertFalse(ok)
        self.assertEqual(rows['buttresses']['fallen'], ['cabinet_1', 'crate_1', 'shelf_1'])

    def test_a_room_whose_windows_got_no_notch_fails(self):
        land = raised(self.room, NOTCH_KINDS=())
        self.assertEqual([o['kind'] for o in land['rim'].record['openings']], ['bay'])
        ok, rows = checked(land)
        self.assertFalse(ok)
        self.assertEqual(rows['openings']['missing'],
                         [dict(id='window_north', kind='window', why='no notch in the record'),
                          dict(id='window_south', kind='window', why='no notch in the record')])
        self.assertFalse(rows['openings']['ok'])
        self.assertEqual((rows['lip']['off'], rows['walls']['low'], rows['notches']), (0, 0, []))     # the land is what the record says

    def test_a_door_with_no_bay_fails(self):
        ok, rows = checked(raised(self.room, BAY_KINDS=()))
        self.assertFalse(ok)
        self.assertEqual(rows['openings']['missing'], [dict(id='entry', kind='door', why='no bay in the record')])

    def test_a_lip_under_the_walls_height_fails(self):
        def lower(room):
            room = copy.deepcopy(room)
            room['bounds']['max_m'][1] *= .75
            return room
        land = raised(self.room, room_of_the_rim=lower)
        self.assertEqual(land['rim'].record['lip_y_m'], 18.)
        ok, rows = checked(land)
        self.assertFalse(ok)
        self.assertEqual(rows['walls_height'], dict(room_m=24., lip_m=18., ok=False))
        self.assertEqual((rows['lip']['height_m'], rows['lip']['off'], rows['walls']['low']), (18., 0, 0))

    def test_a_record_of_another_room_fails(self):
        land = self.land
        wider = copy.deepcopy(land['room'])
        for part in wider['shell']['parts']:
            for point in part['geometry'].get('points_m', ()):
                point[0] *= 1.25
        wider['bounds']['min_m'][0] *= 1.25
        wider['bounds']['max_m'][0] *= 1.25
        ok, rows = checked(land, room=wider)
        self.assertFalse(ok)
        self.assertFalse(rows['wall_line']['ok'])
        # A window the room has and the record lacks; an opening the record has and the room lacks.
        more = copy.deepcopy(land['room'])
        more['shell']['openings'].append(dict(id='window_east', kind='window', host_part_id='shell:wall_b',
                                              center_m=[12., 16., 9.], size_m=[4., 6.], state='fixed', traversable=False))
        ok, rows = checked(land, room=more)
        self.assertFalse(ok)
        self.assertEqual(rows['openings']['missing'], [dict(id='window_east', kind='window', why='no notch in the record')])
        fewer = copy.deepcopy(land['room'])
        fewer['shell']['openings'] = [o for o in fewer['shell']['openings'] if o['id'] != 'window_south']
        ok, rows = checked(land, room=fewer)
        self.assertFalse(ok)
        self.assertEqual(rows['openings']['not_the_rooms'], [2])
        # A window that is not on the wall line, or whose foot is over the lip, is rightly shut.
        for change, why in ((dict(center_m=[4., 15., 9.]), 'not on the wall line'),
                            (dict(center_m=[12., 27., 9.]), 'its foot is over the lip')):
            more['shell']['openings'][-1].update(change)
            ok, rows = checked(land, room=more)
            self.assertTrue(ok, rows)
            self.assertEqual(rows['openings']['shut'][-1], dict(id='window_east', kind='window', why=why))

    def test_a_steeper_or_a_shallower_face_is_told(self):
        rim = self.land['rim']
        self.assertAlmostEqual(math.degrees(math.atan(rim.model.tan)), Rim.FACE_DEG, places=1)
        shallow = Rim.Model(dict(rim.record, lip_out_m=6.))
        self.assertFalse(Rim._the_rooms(shallow, self.land['room'], W10)['face']['ok'])
        self.assertTrue(Rim._the_rooms(Rim.Model(dict(rim.record, lip_out_m=3.)), self.land['room'], W10)['face']['ok'])

    def test_what_the_package_says_of_its_buttresses_is_held_to_the_inventory(self):
        land = self.land
        ok, rows = checked(land, change=lambda gen: gen['buttresses'].pop())
        self.assertFalse(ok)
        self.assertEqual(rows['buttresses']['unlisted'], ['crate_1'])
        table = dict(id='table_1', kind='table', lifted=False, gap_m=.1, probe_m=[-1., 5.], stands_m=0.)
        ok, rows = checked(land, change=lambda gen: gen['buttresses'].append(table))
        self.assertFalse(ok)
        self.assertEqual(rows['buttresses']['unfounded'], ['table_1'])
        ok, rows = checked(land, change=lambda gen: gen['buttresses'][0].update(stands_m=gen['buttresses'][0]['stands_m']+2.))
        self.assertFalse(ok)
        self.assertEqual(rows['buttresses']['fallen'], ['cabinet_1'])
        for change, error in ((lambda gen: gen.pop('rim_join'), 'rim join: fields'),
                              (lambda gen: gen['rim_join'].update(gap_m=10**400), 'rim record: join gap'),
                              (lambda gen: gen['rim_join'].update(note='read me'), 'rim join: fields'),
                              (lambda gen: gen['buttresses'][0].update(probe_m='here'), 'rim buttresses: fields'),
                              (lambda gen: gen.update(buttresses=None), 'rim buttresses: fields'),
                              (lambda gen: gen.update(world_scale='10'), 'rim record: world scale'),
                              (lambda gen: gen['rim'].update(lip_y_m=10**400), 'rim record: lip height'),
                              (lambda gen: gen['rim']['openings'][1].update(width_m=10**400), 'rim record: opening width')):
            self.assertEqual(checked(land, change=change), (False, dict(error=error)))

    def test_a_wall_cut_from_behind_by_another_wall_s_notch_fails(self):
        """A U-shaped room, read against a land in which the window's notch runs on through the other wing's cliff
        (as it did before a notch ended at another wall): the record alone bore that land out."""
        u = [[-15., -15.], [-15., 15.], [15., 15.], [15., -15.], [5., -15.], [5., 5.], [-5., 5.], [-5., -15.]]
        rec = record(outline_m=u, openings=[dict(kind='notch', centre_m=[-5., -5.], width_m=4., floor_y_m=11., flare_deg=30.)])
        del rec['home_wind_deg']
        model = Rim.Model(rec)

        class As:
            def __init__(self, kept):
                self.kept = kept

            def height(self, x, z):
                if model.inside(x, z):
                    return 0.
                with mock.patch.object(Rim.Model, '_kept', (lambda m, f, x, z: 0.) if not self.kept else Rim.Model._kept):
                    return model.height(x, z)
        room = dict(bounds=dict(min_m=[-15., 0., -15.], max_m=[15., 24., 15.]), spawns=[dict(role='player', position_m=[-10., 0., 0.])],
                    shell=dict(parts=[dict(id='shell:floor', role='floor', geometry=dict(points_m=[[x, 0., z] for x, z in u]))],
                               openings=[dict(id='window', kind='window', center_m=[-5., 13., -5.], size_m=[4., 4.])]))
        self.assertEqual(Rim.outline(room), u)
        setup = load_json(G.SETUP_PATH)
        gen = dict(cell_m=.1, world_scale=W10, rim=rec, rim_join=dict(gap_m=.15, lifted_m=.5), buttresses=[],
                   sun_gate=Rim.sun_gate(rec, setup['latitude_deg'], setup['neg_z_bearing_deg']))
        doc = dict(x_generator=gen, water=[], setup=setup)
        meshes = dict(land=[dict(positions=[[x, 0., z] for x in (-25., 25.) for z in (-25., 25.)])])
        ok, rows = Rim.rim_checks(doc, meshes, As(True), room, dict(objects=[]))
        self.assertTrue(ok, rows)
        self.assertEqual(rows['walls']['low'], 0)
        ok, rows = Rim.rim_checks(doc, meshes, As(False), room, dict(objects=[]))
        self.assertFalse(ok)
        self.assertGreater(rows['walls']['low'], 10)


# ---------------------------------------------------------------- the bearings
class Bearings(unittest.TestCase):
    def test_every_bearing_is_in_the_room_s_frame_but_the_one_named_a_compass_bearing(self):
        """The home wind and the way a notch looks are read by the game in the room's own frame, so turning the
        room on the compass changes neither; `faces_compass_deg` turns with it."""
        folder = made_room('turned', **CABINET_ROOM)
        world.set_world(W10)
        try:
            room, inventory = world.magnify(load_json(folder/'room.json'), load_json(folder/'inventory.json'), W10)
            objects = parse_objects(inventory)
            for turned in (0., 135.):
                setup = dict(load_json(G.SETUP_PATH), neg_z_bearing_deg=turned)
                rim = Rim.Rim(room, inventory, objects, setup, W10)
                self.assertEqual(rim.record['home_wind_deg'], 90.)      # in by the door in the -X wall, toward +X
                north, south = rim.sun_gate()['notches']
                self.assertEqual((north['faces_deg'], south['faces_deg']), (0., 180.))
                self.assertEqual((north['faces_compass_deg'], south['faces_compass_deg']), (turned % 360, (turned+180.) % 360))
        finally:
            world.set_world(1.)


# ---------------------------------------------------------------- the option
class Option(unittest.TestCase):
    def test_only_a_magnified_room_has_a_rim(self):
        room = made_room('true_size', **CABINET_ROOM)
        out = os.path.join(workdir('true_size_pkg'), 'pkg')
        with self.assertRaises(ValueError):
            G.generate(room, out, edge='rim')
        with self.assertRaises(ValueError):
            G.generate(room, out, edge='moat')
        # How near a wall a thing joins it is the rim's alone, from 0 to 1 m.
        with self.assertRaises(ValueError):
            G.generate(room, out, wall_join=.02)
        world.set_world(W10)
        try:
            for bad in (2., -.01, '0.02'):
                with self.assertRaises(ValueError), contextlib.redirect_stdout(io.StringIO()):
                    G.generate(room, out, edge='rim', wall_join=bad)
        finally:
            world.set_world(1.)
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
        for argv, edge, join in ((['--room', 'r', '--out', 'o'], 'sea', None),
                                 (['--room', 'r', '--out', 'o', '--edge', 'rim'], 'rim', None),
                                 (['--room', 'r', '--out', 'o', '--edge', 'rim', '--wall-join', '0.02'], 'rim', .02)):
            with mock.patch.object(G, 'generate', return_value=made) as gen, mock.patch('sys.argv', ['generate']+argv), \
                    mock.patch.object(G, 'load_json', return_value={}), contextlib.redirect_stdout(io.StringIO()):
                G.main()
            self.assertEqual((gen.call_args.kwargs['edge'], gen.call_args.kwargs['wall_join']), (edge, join))
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
        cls.inventory = world.magnify_inventory(load_json(cls.room/'inventory.json'), W10)
        cls.gen = cls.doc['x_generator']

    def check(self, change=None, room=None, inventory=None):
        doc = copy.deepcopy(self.doc)
        if change:
            change(doc, doc['x_generator'])
        return Rim.rim_checks(doc, self.meshes, self.terrain, room or self.magnified, inventory or self.inventory)

    def test_every_check_passes_with_the_rim_check_where_the_sea_check_stood(self):
        r = self.result
        self.assertEqual({k: v for k, v in r.items() if k.endswith('ok')},
                         dict(ok=True, water_ok=True, grounded_ok=True, walk_ok=True, footprints_ok=True, rim_ok=True,
                              climb_ok=True, fractals_ok=True, firepits_ok=True))
        self.assertNotIn('sea', r)
        self.assertNotIn('sea_ok', r)
        self.assertEqual(self.check(), (True, r['rim']))

    def test_the_rim_check_runs_inside_the_checks_own_run(self):
        """Rim.run is checks.run itself, the rim check handed to it where the sea check stands (it was a copy of
        checks.run's body, so a check added there later would not have run on a rim package)."""
        sea = checks.sea_checks

        def later(package, room_dir):       # checks.run as it may be one day: one more check, the edge's still asked for
            self.assertIsNot(checks.sea_checks, sea)
            ok, rows = checks.sea_checks(self.doc, self.meshes, self.terrain, self.magnified, {})
            return dict(ok=ok, sea_ok=ok, sea=rows, added_later_ok=True)
        with mock.patch.object(checks, 'run', later):
            got = Rim.run(self.pkg, self.room)
        self.assertIs(checks.sea_checks, sea)
        self.assertEqual(got, dict(ok=True, rim_ok=True, rim=self.result['rim'], added_later_ok=True))
        # A package with a sea goes to the sea check, and keeps its names.
        island = dict(x_generator=dict(sea=dict(level_m=0.)))

        def of_an_island(package, room_dir):
            ok, rows = checks.sea_checks(island, {}, None, None, {})
            return dict(ok=ok, sea_ok=ok, sea=rows)
        with mock.patch.object(checks, 'sea_checks', lambda *a: (True, dict(the='sea'))), mock.patch.object(checks, 'run', of_an_island):
            self.assertEqual(Rim.run('p', 'r'), dict(ok=True, sea_ok=True, sea=dict(the='sea')))
        self.assertFalse(Rim.is_rim(island))
        self.assertFalse(Rim.is_rim(dict()))
        # A package that says it was made with the rim and has lost its record is the rim check's to fail.
        lost = copy.deepcopy(self.doc)
        del lost['x_generator']['rim']
        self.assertTrue(Rim.is_rim(lost) and Rim.is_rim(self.doc) and Rim.is_rim(dict(x_generator=dict(rim=self.gen['rim']))))

        def of_a_lost_record(package, room_dir):
            ok, rows = checks.sea_checks(lost, self.meshes, self.terrain, self.magnified, {})
            return dict(ok=ok, sea_ok=ok, sea=rows)
        with mock.patch.object(checks, 'run', of_a_lost_record):
            got = Rim.run(self.pkg, self.room)
        self.assertEqual((got['ok'], got['rim_ok']), (False, False))
        self.assertIn('error', got['rim'])
        # And the sea check is back whatever happens.

        def broken(package, room_dir):
            raise RuntimeError('broken')
        with mock.patch.object(checks, 'run', broken), self.assertRaises(RuntimeError):
            Rim.run(self.pkg, self.room)
        self.assertIs(checks.sea_checks, sea)

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
        self.assertEqual([(b['id'], b['kind'], b['lifted'], b['gap_m']) for b in gen['buttresses']],
                         [('cabinet_1', 'cabinet', False, .05), ('shelf_1', 'box', True, 1.5), ('crate_1', 'cardboard box', False, .5)])
        self.assertEqual(gen['rim_join'], dict(gap_m=.25, lifted_m=.5))
        for b, tall in zip(gen['buttresses'], (9., 15., 4.)):
            self.assertAlmostEqual(self.terrain.height(*b['probe_m']), b['stands_m'], delta=.01)
            self.assertAlmostEqual(b['stands_m'], tall, delta=1.)       # (the finished land: its top's own relief)
        # The sun gate: the north window has no beam and says why; the south one takes the midwinter noon.
        gate = gen['sun_gate']
        self.assertEqual((gate['time'], gate['shade']), ('apparent solar', 'the notch alone'))
        north, south = gate['notches']
        self.assertEqual((north['opening'], north['faces_deg'], north['faces_compass_deg'], north['beam']), (1, 0., 0., False))
        self.assertIn(north['note'], ('no beam: no sun comes through this notch on any of the days',
                                      'no beam: the sun comes through this notch and meets the cliffs, never the floor'))
        self.assertEqual(north['note'].endswith('never the floor'), north['through'])
        self.assertEqual((south['opening'], south['faces_deg'], south['beam'], south['through']), (2, 180., True, True))
        winter = [d for d in south['days'] if d['day'] == 355][0]
        self.assertTrue(winter['from_h'] < 12. < winter['to_h'])
        self.assertTrue(winter['through_from_h'] <= winter['from_h'] and winter['to_h'] <= winter['through_to_h'])

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
        # Read against the room and its inventory, not the package's own word.
        self.assertEqual(rim['walls_height'], dict(room_m=24., lip_m=24., ok=True))
        self.assertEqual(rim['wall_line'], dict(points=4, ok=True))
        self.assertEqual(rim['openings'], dict(of_the_room=4, notches=2, bays=1, missing=[], not_the_rooms=[], ok=True,
                                               shut=[dict(id='grille', kind='vent', why='a vent needs a roof')]))
        got = rim['buttresses']
        self.assertEqual({k: v for k, v in got.items() if k != 'joined'},
                         dict(rule=dict(gap_m=.25, lifted_m=.5), listed=3, fallen=[], unlisted=[], unfounded=[], at_a_door=[],
                              left_free=[]))
        self.assertEqual([(r['id'], r['lifted'], r['gap_m'], r['faces_deg'], r['ok']) for r in got['joined']],
                         [('cabinet_1', False, .05, 90., True), ('shelf_1', True, 1.5, 270., True), ('crate_1', False, .5, 0., True)])
        for r, tall in zip(got['joined'], (9., 15., 4.)):
            # From the thing's own top, across the gap and into the cliff, the finished land stays as high.
            self.assertAlmostEqual(r['edge_m'], tall, delta=1., msg=r)
            self.assertGreaterEqual(r['stands_m'], r['edge_m']-Rim.JOIN_TOL_M, r)
            self.assertGreaterEqual(r['held'], 7, r)

    def test_the_rim_check_holds_the_package_to_its_room_and_its_inventory(self):
        """What the package says of itself is not enough: the room's walls, its windows and the things its inventory
        stands against a wall are read, and the land is held to them."""
        taller = copy.deepcopy(self.magnified)
        taller['bounds']['max_m'][1] += 8.
        ok, rows = self.check(room=taller)
        self.assertFalse(ok)
        self.assertEqual(rows['walls_height'], dict(room_m=32., lip_m=24., ok=False))
        self.assertEqual((rows['lip']['off'], rows['walls']['low']), (0, 0))        # the land is what the record says
        more = copy.deepcopy(self.magnified)
        more['shell']['openings'].append(dict(id='window_east', kind='window', host_part_id='shell:wall_b',
                                              center_m=[12., 16., 9.], size_m=[4., 6.], state='fixed', traversable=False))
        ok, rows = self.check(room=more)
        self.assertFalse(ok)
        self.assertEqual(rows['openings']['missing'], [dict(id='window_east', kind='window', why='no notch in the record')])
        # A thing the inventory stands against a wall, where the land has no buttress and the package lists none.
        inv = copy.deepcopy(self.inventory)
        cupboard = copy.deepcopy(next(e for e in inv['objects'] if e['id'] == 'cabinet_1'))
        cupboard['id'] = 'cupboard_1'
        cupboard['box']['centre_m'][2] = cupboard['placement']['position_m'][2] = -9.
        inv['objects'].append(cupboard)
        ok, rows = self.check(inventory=inv)
        self.assertFalse(ok)
        self.assertEqual(rows['buttresses']['unlisted'], ['cupboard_1'])
        # A thing the package lists as joined, which the inventory stands clear of every wall.
        inv = copy.deepcopy(self.inventory)
        cabinet = next(e for e in inv['objects'] if e['id'] == 'cabinet_1')
        cabinet['box']['centre_m'][0] -= 3.
        ok, rows = self.check(inventory=inv)
        self.assertFalse(ok)
        self.assertEqual(rows['buttresses']['unfounded'], ['cabinet_1'])
        # A buttress that is not there: the land read as it stood before any thing was joined (the cliff's own face
        # past the wall line, the room's floor in the gap).
        m = Rim.Model(self.gen['rim'])
        real = self.terrain

        class Bare:
            def height(self, x, z):
                if m.inside(x, z):
                    return real.height(x, z) if m.signed(x, z) < -.6 else min(real.height(x, z), 1.)
                return m.height(x, z)
        doc = copy.deepcopy(self.doc)
        ok, rows = Rim.rim_checks(doc, self.meshes, Bare(), self.magnified, self.inventory)
        self.assertFalse(ok)
        self.assertEqual(rows['buttresses']['fallen'], ['cabinet_1', 'crate_1', 'shelf_1'])
        self.assertEqual((rows['lip']['off'], rows['walls']['low']), (0, 0))

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
        self.assertEqual(rows['walls_height'], dict(room_m=24., lip_m=25., ok=False))
        ok, rows = self.check(a_notch_left_out)
        self.assertFalse(ok)
        self.assertGreater(rows['lip']['off'], 50)
        self.assertGreater(rows['walls']['low'], 5)
        self.assertEqual(rows['openings']['missing'], [dict(id='window_south', kind='window', why='no notch in the record')])
        ok, rows = self.check(a_notch_somewhere_else)
        self.assertFalse(ok)
        self.assertFalse(rows['notches'][1]['open_to_the_tile_edge'])
        ok, rows = self.check(a_lower_sill)
        self.assertFalse(ok)
        self.assertFalse(rows['notches'][0]['open_to_the_tile_edge'])
        ok, rows = self.check(a_taller_buttress)
        self.assertFalse(ok)
        self.assertEqual(rows['buttresses']['fallen'], ['cabinet_1'])
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
        # The report alone (what the checks' own command can hand a rim package to): the same lines, and 1 when a
        # check fails.
        out = io.StringIO()
        with contextlib.redirect_stdout(out), self.assertRaises(SystemExit) as stop:
            Rim.report(dict(self.result, ok=False))
        self.assertEqual(stop.exception.code, 1)
        again = out.getvalue().splitlines()
        self.assertEqual(again[:-1], lines[:-1])
        self.assertTrue(again[-1].endswith('rim=True climb=True fractals=True firepits=True overall=False'))

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
        self.assertEqual(rim['walls_height'], dict(room_m=27., lip_m=27., ok=True))
        self.assertTrue(rim['openings']['ok'] and rim['wall_line']['ok'])

    def test_the_stand_in_s_things_against_its_walls_are_parts_of_its_cliffs(self):
        """On the finished land (the water carved, the flights cut, the detail laid), read from the inventory."""
        got = self.result['rim']['buttresses']
        self.assertEqual([(b['id'], b['faces_deg'], b['ok']) for b in got['joined']],
                         [('bicycle_1', 90., True), ('couch_1', 0., True), ('desk_1', 180., True), ('easel_1', 270., True),
                          ('office_chair_1', 180., True), ('shelving_unit_1', 90., True), ('storage_tote_1', 90., True)])
        self.assertEqual({k: v for k, v in got.items() if k != 'joined'},
                         dict(rule=dict(gap_m=.25, lifted_m=.5), listed=7, fallen=[], unlisted=[], unfounded=[], at_a_door=[],
                              left_free=[]))

    def test_the_other_checks_hold_on_the_rim(self):
        r = self.result
        self.assertEqual({k: r[k] for k in ('water_ok', 'grounded_ok', 'walk_ok', 'fractals_ok', 'firepits_ok')},
                         dict(water_ok=True, grounded_ok=True, walk_ok=True, fractals_ok=True, firepits_ok=True))


def tearDownModule():
    import shutil
    from pipeline.landscape.generator.tests.test_generator import RUN_DIR
    shutil.rmtree(RUN_DIR, ignore_errors=True)
