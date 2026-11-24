"""Blocks and caps: each object in its own shape (Run 4, packet WORLD-1).

    python -B -m unittest pipeline.landscape.generator.tests.test_parts -v

The grammar (parts.py) on made-up boxes; the land a magnified room raises from it (land.py), read on the stand-in
garage at ten times as generate.py makes it, stopped where the coast is cut (six seconds: the landforms are whole
there); the likeness measure, on the stand-in's twelve floor boxes and against unlike families; and true size, whose
bytes are as they were. A written ten-times package is measured too when one is named (LANDSCAPE_TEN_TIMES, as in
test_fractals.py: `generate`, or a package folder of corpus/rooms/garage_nominal).
"""
import contextlib
import io
import math
import os
import shutil
import subprocess
import sys
import unittest
from unittest import mock

from pipeline.landscape.generator import generate as G, land, parts, scale as world
from pipeline.landscape.generator.field import Grid, Noise, rr_dist, slope_field
from pipeline.landscape.generator.tests import test_generator
from pipeline.landscape.generator.tests.test_fractals import GARAGE_1X_FILES, package_digests
from pipeline.landscape.generator.tests.test_generator import ROOMS, ROOT, garage, workdir

TEN_TIMES = os.environ.get('LANDSCAPE_TEN_TIMES', '')
W10 = 10.
GRAIN10 = .03*W10    # the landforms' grain on a ten-times room: today's 3 cm grid, magnified
FLOOR_BOXES = ('bean_bag_1', 'bean_bag_2', 'bicycle_1', 'bin_1', 'cardboard_box_1', 'couch_1', 'desk_1', 'easel_1',
               'office_chair_1', 'shelving_unit_1', 'storage_tote_1', 'table_1')
# Pairs the proof may not lose: this box's land fails that family's blocks.
UNLIKE = (('office_chair_1', 'table'), ('office_chair_1', 'box'), ('office_chair_1', 'couch'), ('office_chair_1', 'beanbag'),
          ('desk_1', 'seat'), ('desk_1', 'couch'), ('desk_1', 'beanbag'), ('table_1', 'seat'), ('couch_1', 'table'),
          ('couch_1', 'seat'), ('couch_1', 'box'), ('bean_bag_1', 'box'), ('bean_bag_1', 'seat'), ('bicycle_1', 'box'),
          ('bicycle_1', 'couch'), ('easel_1', 'box'), ('easel_1', 'seat'), ('bin_1', 'box'), ('bin_1', 'seat'),
          ('cardboard_box_1', 'seat'), ('cardboard_box_1', 'beanbag'), ('shelving_unit_1', 'seat'),
          ('storage_tote_1', 'couch'))


def thing(kind, sx, sy, sz, confidence=.86, **more):
    """An object as land.parse_objects makes it, standing on the floor at the origin."""
    return dict(dict(id=kind.replace(' ', '_'), kind=kind, confidence=confidence, cx=0., cz=0., base=0., top=sy, sx=sx,
                     sy=sy, sz=sz, yaw=0., parent=None, phase=0.), **more)


# Every kind the grammar knows, in a box of a likely size (true metres).
SIZES = {
    'chair': (.45, .9, .5), 'office chair': (.6, 1.05, .6), 'desk': (1.05, .75, .5), 'table': (1.1, .72, .7),
    'couch': (2.28, .83, .95), 'sofa': (1.6, .8, .9), 'bed': (1.4, 1., 2.), 'bean bag': (.9, .65, .9),
    'shelving unit': (.8, 2.02, .28), 'bookcase': (.9, 1.8, .3), 'step ladder': (.5, 1.2, .9), 'easel': (.65, 1.65, .6),
    'bicycle': (1.75, 1.05, .5), 'monitor': (.52, .42, .12), 'laptop': (.36, .04, .26), 'bin': (.35, .5, .35),
    'basket': (.4, .3, .4), 'mug': (.09, .1, .09), 'cup': (.08, .09, .08), 'jar': (.09, .12, .09), 'paint can': (.17, .19, .17),
    'bottle': (.08, .25, .08), 'vase': (.15, .3, .15), 'french press': (.17, .245, .11), 'kettle': (.2, .24, .16),
    'lamp': (.3, 1.6, .3), 'plant': (.4, .8, .4), 'backpack': (.35, .5, .25), 'cardboard box': (.45, .3, .36),
    'storage tote': (.6, .38, .4), 'cabinet': (.8, .9, .45), 'suitcase': (.5, .7, .3), 'speaker': (.25, .9, .3),
}


class Stop(Exception):
    pass


_LANDS = {}


def landforms(blocks=True):
    """The stand-in garage's land at ten times as generate.py raises it, stopped where the coast is cut: the base grid
    (10 cm), its heights, the plain and the objects (magnified). `blocks` False: the melted forms."""
    if blocks not in _LANDS:
        got = {}

        def stop(grid, room, h, base, noise, objects, *rest):
            got.update(grid=grid, room=room, h=list(h), base=list(base), objects=objects)
            raise Stop()
        world.set_world(W10)
        try:
            with mock.patch.object(land, 'BLOCKS', blocks), mock.patch.object(G.Sea, 'carve_coast', stop), \
                    contextlib.redirect_stdout(io.StringIO()):
                try:
                    G.generate(ROOMS/'garage_nominal', os.path.join(workdir('never'), 'pkg'))
                except Stop:
                    pass
        finally:
            world.set_world(1.)
        _LANDS[blocks] = got
    return _LANDS[blocks]


def stands_on(got, o):
    """The level an object's land stands on: the plain under a floor thing's middle, its support's for one that rests."""
    by_id = {p['id']: p for p in got['objects']}
    foot = o
    while foot['parent'] is not None:
        foot = by_id[foot['parent']]
    return o['base']+got['grid'].sample(got['base'], foot['cx'], foot['cz'])


def land_of(got, o):
    """An object's land in its own frame: (lx, lz) -> height over its base."""
    grid, h = got['grid'], got['h']
    t = math.radians(o['yaw'])
    c, s = math.cos(t), math.sin(t)
    level = stands_on(got, o)
    return lambda lx, lz: grid.tri_height(h, o['cx']+c*lx+s*lz, o['cz']-s*lx+c*lz)-level


def floor_boxes(got):
    return [o for o in got['objects'] if o['parent'] is None]


class Grammar(unittest.TestCase):
    def test_every_kind_is_at_most_twelve_blocks_inside_its_true_box(self):
        for kind, (sx, sy, sz) in SIZES.items():
            for scale in (.6, 1., 1.7):
                o = thing(kind, sx*scale, sy*scale, sz*scale)
                blocks = parts.blocks(o)
                self.assertTrue(1 <= len(blocks) <= parts.MAX_BLOCKS, (kind, len(blocks)))
                for b in blocks:
                    self.assertTrue(-o['sx']/2-1e-9 <= b.x0 < b.x1 <= o['sx']/2+1e-9, (kind, b))
                    self.assertTrue(-o['sz']/2-1e-9 <= b.z0 < b.z1 <= o['sz']/2+1e-9, (kind, b))
                    self.assertTrue(0 <= b.y0 < b.y1 <= o['sy']+1e-9, (kind, b))
                # Its land reaches the box's own top somewhere (the footprint rule reads the top), and nowhere over it.
                self.assertAlmostEqual(max(b.y1 for b in blocks if not b.cut), o['sy'], places=9, msg=kind)

    def test_a_room_ten_times_over_has_the_same_blocks_ten_times_over(self):
        for kind, (sx, sy, sz) in SIZES.items():
            one = parts.blocks(thing(kind, sx, sy, sz), .03)
            ten = parts.blocks(thing(kind, sx*10, sy*10, sz*10), .3, 10.)
            self.assertEqual([b.name for b in one], [b.name for b in ten], kind)
            for a, b in zip(one, ten):
                for u, v in zip(a[1:7], b[1:7]):
                    self.assertAlmostEqual(u*10, v, places=6, msg=(kind, a, b))

    def test_no_part_is_thinner_than_the_grain_stands(self):
        """Three grains for a box, four for a drum: a top with two full-height vertices across, whatever the grid."""
        grain = .03
        for kind, (sx, sy, sz) in SIZES.items():
            o = thing(kind, sx, sy, sz)
            for b in parts.blocks(o, grain):
                least = grain*(parts.ROUND_GRAINS if b.round else parts.LEAST_GRAINS)
                self.assertGreaterEqual(b.x1-b.x0, least-1e-9, (kind, b))
                self.assertGreaterEqual(b.z1-b.z0, least-1e-9, (kind, b))
                # ... and still inside the box, unless the box itself is thinner than that.
                self.assertLessEqual(max(-b.x0, b.x1), max(o['sx'], least)/2+1e-9, (kind, b))
                self.assertLessEqual(max(-b.z0, b.z1), max(o['sz'], least)/2+1e-9, (kind, b))

    def test_every_box_of_the_corpus_takes_its_grammar(self):
        """All 24 corpus rooms: every box makes its blocks at the land's grain, and their land stands no higher than
        the box and reaches its top. A box with no size raises nothing."""
        from pipeline.landscape.harness.common import load_json
        rooms = sorted(p for p in ROOMS.iterdir() if (p/'inventory.json').is_file())
        self.assertEqual(len(rooms), 24)
        seen = set()
        for room in rooms:
            for o in land.parse_objects(load_json(room/'inventory.json')):
                blocks = parts.blocks(o, .03)
                seen.add(parts.family(o))
                self.assertTrue(1 <= len(blocks) <= parts.MAX_BLOCKS, (room.name, o['id']))
                tops = [parts.surface(blocks, o['sx']*(i-4)/8, o['sz']*(j-4)/8, .03, o['phase'])[0]
                        for i in range(9) for j in range(9)]
                self.assertLessEqual(max(tops), o['sy']+1e-9, (room.name, o['id']))
                self.assertGreaterEqual(max(tops), .6*o['sy'], (room.name, o['id']))
        self.assertGreaterEqual(len(seen), 12, seen)
        self.assertEqual(parts.blocks(thing('desk', 1., 0., .5)), ())

    def test_a_kind_the_scan_is_not_sure_of_is_one_block_its_box(self):
        for o in (thing('office chair', .6, 1.05, .6, confidence=.3), thing('a thing nobody named', .4, .5, .3)):
            self.assertEqual(parts.family(o), 'box')
            (b,) = parts.blocks(o)
            self.assertEqual((b.x0, b.x1, b.z0, b.z1, b.y0, b.y1), (-o['sx']/2, o['sx']/2, -o['sz']/2, o['sz']/2, 0., o['sy']))
            self.assertFalse(b.soft or b.round or b.cut)

    def test_a_chair_is_a_seat_under_a_back_wall(self):
        for kind in ('chair', 'office chair'):
            o = thing(kind, *SIZES[kind])
            by = {}
            for b in parts.blocks(o):
                by.setdefault(b.name, []).append(b)
            seat, back = by['seat'][0], by['back'][0]
            self.assertAlmostEqual(back.y1, o['sy'])
            self.assertTrue(.38*o['sy'] <= seat.y1 <= .55*o['sy'], seat)
            self.assertAlmostEqual(back.z1, o['sz']/2, msg='the back stands along the back edge')
            self.assertLess(back.z1-back.z0, .4*o['sz'], 'a wall, not a block')
            self.assertGreater(seat.z1-seat.z0, .5*o['sz'])
        arms = [b for b in parts.blocks(thing('office chair', *SIZES['office chair'])) if b.name == 'arm']
        self.assertEqual(len(arms), 2)
        self.assertEqual(len([b for b in parts.blocks(thing('chair', *SIZES['chair'])) if b.name == 'leg']), 4)

    def test_a_desk_is_a_flat_top_on_four_legs(self):
        o = thing('desk', *SIZES['desk'])
        blocks = parts.blocks(o)
        top = next(b for b in blocks if b.name == 'top')
        self.assertEqual((top.x0, top.x1, top.z0, top.z1, top.y1), (-o['sx']/2, o['sx']/2, -o['sz']/2, o['sz']/2, o['sy']))
        self.assertGreater(top.y0, .7*o['sy'], 'its underside is kept for the hall under it')
        self.assertEqual(len([b for b in blocks if b.name == 'leg']), 4)
        self.assertEqual(parts.cap_kind(top), 'mesa')

    def test_a_couch_has_arms_a_back_and_cushions(self):
        o = thing('couch', *SIZES['couch'])
        blocks = parts.blocks(o)
        arms = [b for b in blocks if b.name == 'arm']
        cushions = [b for b in blocks if b.name == 'cushion']
        back = next(b for b in blocks if b.name == 'back')
        self.assertEqual((len(arms), len(cushions)), (2, 3))
        self.assertTrue(all(c.soft for c in cushions+arms+[back]))
        self.assertTrue(cushions[0].y1 < arms[0].y1 < back.y1 == o['sy'])
        self.assertAlmostEqual(back.z1, o['sz']/2)

    def test_a_step_ladder_is_a_stair_and_a_bin_has_a_dish_a_starting_bar_climbs_out_of(self):
        treads = [b for b in parts.blocks(thing('step ladder', *SIZES['step ladder'])) if b.name in ('tread', 'top')]
        self.assertGreaterEqual(len(treads), 3)
        self.assertEqual([b.y1 for b in treads], sorted(b.y1 for b in treads))
        self.assertEqual([b.z0 for b in treads], sorted(b.z0 for b in treads), 'up from the front')
        o = thing('bin', *SIZES['bin'])
        for grain, scale in ((0., 1.), (.03, 1.), (.3, 10.)):
            big = dict(o, sx=o['sx']*scale, sy=o['sy']*scale, sz=o['sz']*scale)
            wall, hollow = parts.blocks(big, grain, scale)
            self.assertTrue(hollow.cut and hollow.round and wall.round)
            self.assertLessEqual(wall.y1-hollow.y1, parts.DISH_M*scale+1e-9)
            from pipeline.landscape.generator import gates
            if scale == 10:
                self.assertLess(wall.y1-hollow.y1, gates.cap(1), 'the starting bar climbs out of the dish')
        # A mug is too small for the grain to hold a dish: a plain drum there, a dish in its true parts.
        self.assertEqual(len(parts.blocks(thing('mug', *SIZES['mug']), .03)), 1)
        self.assertEqual(len(parts.blocks(thing('mug', *SIZES['mug']))), 2)

    def test_a_flat_top_is_a_mesa_a_butte_or_a_hoodoo_by_its_width(self):
        def cap(kind, name):
            return parts.cap_kind(next(b for b in parts.blocks(thing(kind, *SIZES[kind])) if b.name == name))
        self.assertEqual(cap('table', 'top'), 'mesa')
        self.assertEqual(cap('shelving unit', 'top'), 'butte')
        self.assertEqual(cap('easel', 'mast'), 'hoodoo')
        self.assertIsNone(cap('bean bag', 'bag'), 'a soft thing has no cap')


class Surface(unittest.TestCase):
    def test_a_hard_top_is_flat_and_a_face_is_one_grain_wide(self):
        o = thing('cardboard box', .45, .3, .36)
        blocks = parts.blocks(o, .03)
        self.assertEqual(parts.surface(blocks, 0., 0., .03), (.3, 1.))
        self.assertEqual(parts.surface(blocks, .2, .16, .03), (.3, 1.), 'flat to within half a grain of its edge')
        self.assertAlmostEqual(parts.surface(blocks, .225, 0., .03)[0], .15, places=9, msg='half way up at the edge')
        self.assertEqual(parts.surface(blocks, .241, 0., .03)[0], 0., 'nothing half a grain past the box')

    def test_a_soft_top_turns_over_its_shoulder_and_is_rilled_and_weathers(self):
        o = thing('bean bag', .9, .65, .9)
        blocks = parts.blocks(o, .03)
        middle, soft = parts.surface(blocks, 0., 0., .03)
        self.assertAlmostEqual(middle, .65, places=6)
        self.assertEqual(soft, 0., 'no cap: the weather works a soft top')
        ring = [parts.surface(blocks, .3*math.cos(t), .3*math.sin(t), .03)[0] for t in (k*math.tau/90 for k in range(90))]
        self.assertLess(max(ring), .62)
        self.assertGreater(min(ring), .4)
        self.assertGreater(max(ring)-min(ring), .01, 'rills round the shoulder')
        self.assertEqual(parts.surface(blocks, .45, 0., .03)[1], 1., 'its face is spared like any face')

    def test_a_hollow_lowers_the_land_inside_its_rim(self):
        blocks = parts.blocks(thing('bin', .35, .5, .35), .03)
        self.assertAlmostEqual(parts.surface(blocks, 0., 0., .03)[0], .5-parts.DISH_M, places=9)
        self.assertAlmostEqual(parts.surface(blocks, .14, 0., .03)[0], .5, places=9, msg='the rim stands at the box\'s top')


class Likeness(unittest.TestCase):
    def reliefs(self, o, kind=None, turn=False):
        f = parts.shape(o, .03, kind=kind)
        return parts.reliefs((lambda lx, lz: f(-lx, -lz)) if turn else f, o['sx']/2, o['sz']/2, o['sy'])

    def test_a_shape_is_like_itself_from_every_side(self):
        o = thing('office chair', .6, 1.05, .6)
        got = parts.likeness(self.reliefs(o), self.reliefs(o))
        self.assertEqual([got[v] for v in parts.VIEWS], [1.]*5)
        self.assertEqual(got['least'], 1.)

    def test_the_four_sides_are_four_views(self):
        """A chair turned round is the same from above and another thing from the front and the back: a view from a
        side reads the first surface the eye meets, not an outline."""
        o = thing('chair', .45, .9, .45)
        got = parts.likeness(self.reliefs(o), self.reliefs(o, turn=True))
        self.assertLess(got['front'], parts.MATCH)
        self.assertLess(got['back'], parts.MATCH)
        self.assertLess(got['least'], parts.MATCH)

    def test_unlike_families_fail_and_like_ones_do_not(self):
        chair = thing('office chair', .6, 1.05, .6)
        for other in ('table', 'box', 'couch', 'beanbag', 'bicycle', 'easel', 'vessel'):
            self.assertTrue(parts.unlike(chair, other, .03), other)
            self.assertLess(parts.likeness(self.reliefs(chair), self.reliefs(chair, other))['least'], parts.MATCH, other)
        desk = thing('desk', 1.05, .75, .5)
        # As land a desk, a shelving unit and a box are one landform: a flat top with sheer sides.
        for other in ('box', 'shelf'):
            self.assertFalse(parts.unlike(desk, other, .03), other)
            self.assertEqual(parts.LANDFORMS['table'], parts.LANDFORMS[other])
        for other in ('seat', 'couch', 'beanbag', 'ladder', 'easel', 'bicycle'):
            self.assertTrue(parts.unlike(desk, other, .03), other)
            self.assertNotEqual(parts.LANDFORMS['table'], parts.LANDFORMS[other])

    def test_a_score_is_the_solid_shared_over_the_solid_either_has(self):
        half = dict(top=[1., 0.], front=[1., 0.], back=[1., 0.], left=[.5, .5], right=[1., 1.])
        full = dict(top=[1., 1.], front=[1., 0.], back=[1., 0.], left=[1., 1.], right=[1., 1.])
        got = parts.likeness(half, full)
        self.assertEqual((got['top'], got['front'], got['left'], got['right'], got['least']), (.5, 1., .5, 1., .5))


class RaisedLand(unittest.TestCase):
    """land.raise_blocks and the weather's caps, on made-up plains."""

    def raised(self, objects, cell=.03, half=1.5, plain=.05):
        grid = Grid(-half, -half, half, half, cell)
        base = [plain]*grid.n
        owner = [-1]*grid.n
        world.set_world(W10)
        try:
            h = land.uplift(grid, objects, base, Noise(1), owner)
        finally:
            world.set_world(1.)
        return grid, h, owner

    def test_a_box_turned_any_way_keeps_a_level_top_and_a_wall_a_top_to_stand_on(self):
        for yaw in (0., 17., 30., 45., 73.):
            wall = thing('cardboard box', 1.2, .9, .09, yaw=yaw)   # three grains thick: a chair's back, say
            grid, h, _ = self.raised([wall])
            full = [q for q in range(grid.n) if abs(h[q]-.95) < 1e-9]
            self.assertTrue(full, yaw)
            self.assertLessEqual(max(h), .95+1e-9, 'nothing stands over the box')
            # Along the wall every 3 cm of its length has a full-height vertex beside another: a crest, not a point.
            t = math.radians(yaw)
            along = sorted((grid.xs[q % grid.nx]*math.cos(t)-grid.zs[q//grid.nx]*math.sin(t)) for q in full)
            self.assertLess(max(b-a for a, b in zip(along, along[1:])), .045, yaw)
            self.assertGreater(len(full), 1.1/.03*1.5, 'two full-height vertices across it, most of its length (%g)' % yaw)

    def test_it_is_the_blocks_and_the_cap_is_marked(self):
        chair = thing('office chair', .6, 1.05, .6)
        grid, h, owner = self.raised([chair])
        self.assertIsInstance(h, land.Heights)
        blocks = parts.blocks(chair, .03)
        for q in range(grid.n):
            p, hard = parts.surface(blocks, grid.xs[q % grid.nx], grid.zs[q//grid.nx], .03)
            self.assertAlmostEqual(h[q], .05+p, places=9)
            self.assertEqual(owner[q], 0 if p > 0 else -1)
            if p > 0:
                self.assertEqual(h.cap[q], hard)
        # The plain at the foot of a face is spared with it; the open plain is not.
        i, j = grid.nearest(.33, 0.)
        self.assertEqual((h[j*grid.nx+i], h.cap[j*grid.nx+i]), (.05, 1.))
        i, j = grid.nearest(.6, 0.)
        self.assertEqual((h[j*grid.nx+i], h.cap[j*grid.nx+i]), (.05, 0.))

    def test_a_thing_that_rests_on_another_stands_on_its_top_and_only_over_it(self):
        desk = thing('desk', 1.05, .75, .5)
        laptop = thing('laptop', .36, .04, .26, cx=.1, base=.75, top=.79, parent='desk')
        hanging = thing('jar', .09, .12, .09, cx=.6, base=.75, top=.87, parent='desk', id='jar')   # scanned past the desk's edge
        grid, h, owner = self.raised([desk, laptop, hanging])
        at = lambda x, z: h[grid.nearest(x, z)[1]*grid.nx+grid.nearest(x, z)[0]]
        self.assertAlmostEqual(at(-.3, 0.), .8, places=9, msg='the desk: its box over the plain')
        self.assertAlmostEqual(at(.1, 0.), .84, places=9, msg='the laptop shows on it, its own height over the desk\'s top')
        self.assertEqual(owner[grid.nearest(.1, 0.)[1]*grid.nx+grid.nearest(.1, 0.)[0]], 1)
        self.assertAlmostEqual(at(.63, 0.), .05, places=9, msg='no pillar beside the desk')

    def test_the_weather_spares_a_cap_and_works_soft_ground(self):
        box, bag = thing('cardboard box', .6, .5, .6, cx=-.6), thing('bean bag', .6, .5, .6, cx=.6, id='bag')
        grid, h, _ = self.raised([box, bag])
        slope = slope_field(grid, h)
        out, _ = land.weather(grid, h, [False]*grid.n, slope, Noise(1))
        inside = lambda o, q: rr_dist(grid.xs[q % grid.nx]-o['cx'], grid.zs[q//grid.nx], .3, .3, 0.) < -.02
        moved_box = max(abs(out[q]-h[q]) for q in range(grid.n) if inside(box, q))
        moved_bag = max(abs(out[q]-h[q]) for q in range(grid.n) if inside(bag, q))
        self.assertEqual(moved_box, 0., 'a cap bed is as it was raised')
        self.assertGreater(moved_bag, .002, 'a soft top weathers')
        # The same heights without their caps (a plain list, as at true size) lose their rim: what the cap is for.
        bare, _ = land.weather(grid, list(h), [False]*grid.n, slope, Noise(1))
        rim = [q for q in range(grid.n) if h.cap[q] == 1. and abs(h[q]-.55) < 1e-9 and
               min(h[q-1], h[q+1], h[q-grid.nx], h[q+grid.nx]) < .5]
        self.assertTrue(rim)
        self.assertGreater(max(h[q]-bare[q] for q in rim), .03)
        self.assertEqual(max(abs(h[q]-out[q]) for q in rim), 0.)

    def test_at_true_size_uplift_is_the_melted_forms_and_asks_the_grammar_nothing(self):
        chair = thing('office chair', .6, 1.05, .6, form='knoll', form_basis='kind', reach_k=1., support_conf=.86)
        grid = Grid(-1.5, -1.5, 1.5, 1.5, .03)
        with mock.patch.object(parts, 'blocks', side_effect=AssertionError('the grammar was asked at true size')):
            h = land.uplift(grid, [chair], [.05]*grid.n, Noise(1), [-1]*grid.n)
        self.assertIs(type(h), list)
        self.assertGreater(max(h), .8)
        # ... and with the blocks switched off a magnified room is raised the same way, vertex for vertex.
        world.set_world(W10)
        try:
            with mock.patch.object(land, 'BLOCKS', False):
                again = land.uplift(grid, [chair], [.05]*grid.n, Noise(1), [-1]*grid.n)
        finally:
            world.set_world(1.)
        self.assertEqual(again, h)


class StandIn(unittest.TestCase):
    """The packet's proof, on the stand-in garage at ten times."""

    @classmethod
    def setUpClass(cls):
        cls.got = landforms()
        cls.boxes = floor_boxes(cls.got)
        cls.scores = {o['id']: parts.measure(o, land_of(cls.got, o), GRAIN10, W10) for o in cls.boxes}
        cls.families = sorted({parts.family(o) for o in cls.boxes})

    def test_twelve_floor_boxes_match_their_blocks_from_five_sides(self):
        self.assertEqual(tuple(sorted(o['id'] for o in self.boxes)), FLOOR_BOXES)
        low = {k: v for k, v in self.scores.items() if v['least'] < parts.MATCH}
        self.assertEqual(low, {}, 'floor boxes whose land is not their blocks')
        for ident, got in self.scores.items():
            for view in parts.VIEWS:
                self.assertGreaterEqual(got[view], parts.MATCH, (ident, view))
        # Not by a hair: the least of the sixty views stands well over the bar.
        self.assertGreaterEqual(min(v['least'] for v in self.scores.values()), parts.MATCH+.05, self.scores)

    def test_unlike_families_fail(self):
        """Each box's land against the blocks of every other family of the room, stood in the same box: where that
        family's shape is another shape there (parts.unlike), the land fails it."""
        self.assertEqual(self.families, ['beanbag', 'bicycle', 'box', 'couch', 'easel', 'seat', 'shelf', 'table', 'vessel'])
        tried, passed = [], []
        for o in self.boxes:
            for other in self.families:
                if other == parts.family(o) or not parts.unlike(o, other, GRAIN10, W10):
                    continue
                tried.append((o['id'], other))
                got = parts.measure(o, land_of(self.got, o), GRAIN10, W10, kind=other)
                if got['least'] >= parts.MATCH:
                    passed.append((o['id'], other, got))
        self.assertEqual(passed, [], 'a land that matches an unlike family\'s blocks')
        self.assertGreaterEqual(len(tried), 70, 'of %d pairs' % (12*8))
        self.assertEqual([pair for pair in UNLIKE if pair not in tried], [], 'pairs the proof may not lose')
        # What is left out is alike by its own blocks, and of one landform but for a tall drum and a tall soft one.
        alike = sorted({(parts.family(o), other) for o in self.boxes for other in self.families
                        if other != parts.family(o) and (o['id'], other) not in tried})
        for a, b in alike:
            self.assertTrue(parts.LANDFORMS[a] == parts.LANDFORMS[b] or {a, b} == {'vessel', 'beanbag'}, (a, b))

    def test_the_land_before_this_packet_fails(self):
        """Failing before: the melted forms of the same room (the blocks switched off) are not their blocks."""
        melted = landforms(blocks=False)
        scores = {o['id']: parts.measure(o, land_of(melted, o), GRAIN10, W10)['least'] for o in floor_boxes(melted)}
        failing = sorted(k for k, v in scores.items() if v < parts.MATCH)
        for ident in ('office_chair_1', 'couch_1', 'bicycle_1', 'easel_1', 'bean_bag_1', 'bean_bag_2'):
            self.assertIn(ident, failing, scores)
        self.assertGreaterEqual(len(failing), 7, scores)

    def test_each_stands_where_it_stands_and_nothing_spreads_past_its_box(self):
        got = self.got
        grid, h, base = got['grid'], got['h'], got['base']
        nx = grid.nx
        near = [math.inf]*grid.n
        for o in self.boxes:
            R = math.hypot(o['sx'], o['sz'])/2+2.
            rx, rz = grid.span(o['cx']-R, o['cx']+R, o['cz']-R, o['cz']+R)
            for j in rz:
                for i in rx:
                    d = rr_dist(*land.local(o, grid.xs[i], grid.zs[j]), o['sx']/2, o['sz']/2, 0.)
                    near[j*nx+i] = min(near[j*nx+i], d)
        lo, hi = got['room']['bounds']['min_m'], got['room']['bounds']['max_m']
        # A face is drawn between two vertices of the grain, so it ends within two grains of the box's edge.
        spread = [(h[q]-base[q], q) for q in range(grid.n) if near[q] > 2*GRAIN10
                  and lo[0] <= grid.xs[q % nx] <= hi[0] and lo[2] <= grid.zs[q//nx] <= hi[2]]
        self.assertGreater(len(spread), 100000)
        self.assertLess(max(spread)[0], .05, 'two grains past every box the land is the plain')
        # A cliff meets level ground: no moat at a face's foot.
        moat = min(h[q]-base[q] for q in range(grid.n) if 0 < near[q] <= 1.)
        self.assertGreater(moat, -.05)
        for o in self.boxes:
            # Its top stands as far over its own ground as the box is tall (higher only by what rests on it).
            f = land_of(got, o)
            peak = max(f(-o['sx']/2+(a+.5)*o['sx']/40, -o['sz']/2+(b+.5)*o['sz']/40) for a in range(40) for b in range(40))
            rests = [c for c in got['objects'] if c['parent'] == o['id']]
            if not rests:
                self.assertAlmostEqual(peak, o['sy'], delta=.02*o['sy']+.05, msg=o['id'])
            else:
                self.assertGreaterEqual(peak, o['sy']-.05, o['id'])

    def test_a_desk_is_a_mesa_and_a_chair_a_seat_under_a_back_wall(self):
        by = {o['id']: o for o in self.got['objects']}
        desk, chair, bag = by['desk_1'], by['office_chair_1'], by['bean_bag_1']
        f = land_of(self.got, desk)
        # The desk's top, clear of what rests on it: flat to a centimetre over six metres, at the box's height.
        top = [f(lx, lz) for lx in (-4.8, -4.4, -4., -3.6) for lz in (-2., -1., 0., 1., 2.)]
        self.assertLess(max(top)-min(top), .01)
        self.assertAlmostEqual(top[0], desk['sy'], delta=.01)
        # Its sides are cliffs: from the plain to the top within the grain.
        self.assertLess(abs(f(-desk["sx"]/2-2*GRAIN10, 0.)), .05)
        self.assertGreater(f(-desk["sx"]/2+2*GRAIN10, 0.), desk["sy"]-.01)
        g = land_of(self.got, chair)
        seat, back = g(0., -.5), g(0., chair['sz']/2-.45)
        self.assertAlmostEqual(seat, 4.7, delta=.05)
        self.assertAlmostEqual(back, chair['sy'], delta=.05)
        # Its arms stand over the seat and under the back. (The star base's step, half a metre wide here, is under
        # two grains: it folds into the face until the blocks are replayed on the finer grids.)
        for side in (-1, 1):
            self.assertAlmostEqual(g(side*2.07, 0.), 6.7, delta=.05, msg='an arm')
        self.assertLess(seat, back)
        # A bean bag is a dome with rills, no flat cap.
        b = land_of(self.got, bag)
        ring = [b(2.6*math.cos(t), 2.6*math.sin(t)) for t in (k*math.tau/60 for k in range(60))]
        self.assertGreater(max(ring)-min(ring), .08)
        self.assertLess(max(ring), b(0., 0.))

    def test_what_rests_on_a_thing_shows_on_its_top(self):
        by = {o['id']: o for o in self.got['objects']}
        for ident in ('laptop_1', 'monitor_1', 'french_press_1', 'jar_1'):
            o = by[ident]
            f = land_of(self.got, o)
            self.assertAlmostEqual(f(0., 0.), o['sy'], delta=.12*o['sy']+.02, msg=ident)


def true_size_garage():
    """test_generator's stand-in garage at true size; made again where that suite, run before this one in the same
    process, has taken its folder down with it."""
    folder = garage()
    if not os.path.isfile(os.path.join(folder, 'package.json')):
        test_generator._GARAGE.clear()
        folder = garage()
    return folder


class TrueSize(unittest.TestCase):
    @unittest.skipUnless(sys.platform == 'win32', 'the digests are pinned on Windows (test_fractals.GARAGE_1X_FILES)')
    def test_the_garage_at_true_size_is_the_same_bytes(self):
        """The packet's clause: every file of the stand-in garage's true-size package, by the digests test_fractals.py
        pins (taken before this packet)."""
        got = package_digests(true_size_garage())
        moved = sorted(name for name in set(got) | set(GARAGE_1X_FILES) if got.get(name) != GARAGE_1X_FILES.get(name))
        self.assertEqual(moved, [], 'true-size output bytes changed in %d of %d files (%s, %s)'
                         % (len(moved), len(got), sys.platform, sys.version.split()[0]))

    def test_the_switch_moves_no_true_size_byte(self):
        """On any system: the garage made again with the blocks switched off is the same bytes, file for file."""
        folder = workdir('true_size_melted')
        self.addCleanup(shutil.rmtree, folder, ignore_errors=True)
        with mock.patch.object(land, 'BLOCKS', False), contextlib.redirect_stdout(io.StringIO()):
            G.generate(ROOMS/'garage_nominal', os.path.join(folder, 'pkg'))
        self.assertEqual(package_digests(os.path.join(folder, 'pkg')), package_digests(true_size_garage()))


@unittest.skipUnless(TEN_TIMES, 'a real ten-times room takes six minutes to make: set LANDSCAPE_TEN_TIMES=generate, or to the '
                                'folder of a ten-times package of corpus/rooms/garage_nominal')
class TenTimesPackage(unittest.TestCase):
    """The same proof on the land the game loads: the written ten-times package, after the coast, the water, the ways,
    the flights and the 2.5 cm ground."""

    @classmethod
    def setUpClass(cls):
        room = ROOMS/'garage_nominal'
        if TEN_TIMES.lower() in ('1', 'generate'):
            folder = os.path.join(workdir('ten_times_parts'), 'pkg')
            cls.addClassCleanup(shutil.rmtree, os.path.dirname(folder), ignore_errors=True)
            subprocess.run([sys.executable, '-B', '-m', 'pipeline.landscape.generator.generate', '--room', str(room),
                            '--world-scale', '10', '--tutorial', '--out', folder], cwd=str(ROOT), check=True,
                           stdout=subprocess.DEVNULL)
        else:
            folder = TEN_TIMES
        cls.rows = parts.package_rows(folder, room)

    def test_twelve_floor_boxes_match_their_blocks_on_the_written_land(self):
        floor = {r['id']: r for r in self.rows if r['floor']}
        self.assertEqual(tuple(sorted(floor)), FLOOR_BOXES)
        self.assertEqual({k: r['least'] for k, r in floor.items() if not r['match']}, {})


if __name__ == '__main__':
    unittest.main()
