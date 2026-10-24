"""Turn a corpus room into a landscape package.

    python -m pipeline.landscape.generator.generate --room <room folder> --out <empty folder>

Deterministic: the same room, inventory and setup answers give identical bytes.
"""
import argparse
import math
import random
import struct
import time
from pathlib import Path

from pipeline.landscape.harness import write_package
from pipeline.landscape.harness.common import load_json
from pipeline.landscape.harness.kit import SIZES
from .field import Grid, Noise, clamp, distance_field, face_slope_max, lerp, slope_field, smooth
from .land import (high_country, inside_distance, local, parse_objects, room_shape, sun_shadow, uplift,
                   wall_frame, weather, rr_dist)
from .life import (BRIDGE, grade_line, STEP_LIMIT_M, WALK_LIMIT_DEG, build_mesh, build_road, find_hamlet, flatten, pad_hold,
                   paint_line, paint_path, prototypes, reachable_from, surface, walk)
from .water import channel_cells, choose_outlet, lake_rho, plan_and_carve, resample
from . import sea as Sea
from . import scale as world
from .climb import give_lips, knife_edges
from .detail import Detail, plane_ok_fine
from . import firepits as Firepits, fractals as Fractals, reach as Reach, sites as Sites

GENERATOR = {'name': 'landscape-generator', 'version': '5'}
SEED = 20261008
CELL = .03
CELL_W = .1    # the base grid on a magnified room (2.5 cm detail where the body meets the land: detail.py)
MARGIN = 1.7   # the sea out past the reef and the playable water; beyond it the far sea mesh
REPO = Path(__file__).resolve().parents[3]
SETUP_PATH = REPO/'pipeline'/'landscape'/'harness'/'ab-setup.json'
# The hamlet on a magnified room is proved by the exact walk from the spawn (the 10x world: its cottages stand on
# narrow shores below banks the quick reachability flood cannot judge). Where no graded way reaches a door or the
# crate, the site is given up for the next one; if none of HAMLET_SITES sites is reached, the first stands.
HAMLET_SITES = 6
RAMP_PASSES = 6    # ways graded (each proved by a new walk) before a site is given up
# The planting is one random stream, and the game's navigation bake trips on about one layout in five (a rare layout of trunk
# holes it cannot triangulate: see REPORT.md, "Navigation edge errors"). A magnified room draws its planting with this salt
# unless one is asked for (`plant_salt=0`: none), the one the 10x nominal garage with its Fractals boots clean under. 1x: none.
WORLD_PLANT_SALT = 1
PAD_HOLD_M = .07   # the pad's flattened margin: nothing is cut or filled there by a way graded later


def hamlet_lost(reach, goals, houses):
    """The hamlet's goals (the crate, then each door) the flood `reach` from the player's spawn does not reach."""
    return [g for g in goals[:1+houses] if not reach.has(g)]


class HamletCutOff(Exception):
    """The hamlet at `site` cannot be reached on foot: its crate or a door has no way from the spawn."""

    def __init__(self, site, why):
        super().__init__('hamlet at (%.2f, %.2f) cut off: %s' % (site[0], site[1], why))
        self.site, self.why = site, why


_T0 = [None]


def _tick(label):
    """Stage timings for the scale experiment (printed only for a magnified room)."""
    if world.W == 1:
        return
    now = time.perf_counter()
    if _T0[0] is None:
        _T0[0] = now
    print('LANDSCAPE_STAGE %-10s %7.1f s' % (label, now-_T0[0]), flush=True)


def eyes(room):
    """Where the review eyes and figures stand, so planting keeps them clear."""
    lo, hi, _ = room_shape(room)
    cx, cz = (lo[0]+hi[0])/2, (lo[2]+hi[2])/2
    out = []
    for s in room['spawns']:
        x, _, z = s['position_m']
        ax, az = cx, cz
        if math.hypot(cx-x, cz-z) < 1.5*world.W:
            ax, az = max([(cx, lo[2]), (hi[0], cz), (cx, hi[2]), (lo[0], cz)],
                         key=lambda p: math.hypot(p[0]-x, p[1]-z))
        out.append((x, z, ax, az))
    wins = sorted([o for o in room['shell'].get('openings', []) if o['kind'] == 'window'], key=lambda w: w['id'])
    if wins:
        out.append((cx, cz, wins[0]['center_m'][0], wins[0]['center_m'][2]))
    else:
        out.append((cx, cz, cx, lo[2]))
    return out


def clear_of_eyes(views, x, z, radius, corridor=None):
    """A glade where a body arrives (each spawn) and at the land's middle; no
    sightline is kept open for any particular camera."""
    for ex, ez, ax, az in views:
        d = math.hypot(x-ex, z-ez)
        if d < radius:
            return False
    return True


def grounding(grid, h, objects, base):
    rows = []
    for o in objects:
        a, b = o['sx']/2, o['sz']/2
        R = math.hypot(a, b)+.05
        rx, rz = grid.span(o['cx']-R, o['cx']+R, o['cz']-R, o['cz']+R)
        vals = []
        for j in rz:
            for i in rx:
                lx, lz = local(o, grid.xs[i], grid.zs[j])
                if abs(lx) <= a and abs(lz) <= b:
                    vals.append(h[j*grid.nx+i])
        if not vals:
            ci, cj = grid.nearest(o['cx'], o['cz'])
            vals = [h[cj*grid.nx+ci]]
        top = max(vals)
        rows.append(dict(id=o['id'], kind=o['kind'], form=o['form'], basis=o['form_basis'],
                         rock=o['rock_role'], centre=[round(o['cx'], 3), round(o['cz'], 3)],
                         box_top=round(o['top'], 3), land_top=round(top, 3),
                         land_mean=round(sum(vals)/len(vals), 3), delta=round(top-o['top'], 3)))
    return rows


def generate(room_dir, out_dir, setup=None, seed=SEED, return_state=False, cell=None, detail=None, plant_salt=None,
             fractals=None, typed_fractals=False, tutorial=False):
    """The package of a room. On a magnified room a hamlet no graded road reaches (the exact walk from the player's
    spawn finds no way to the crate or a door) is given up and the land generated again with the next site.
    `plant_salt` (None: `WORLD_PLANT_SALT` on a magnified room, none at 1x; 0: none) draws the planting again, on the same land: the plants, trees and stones are a free
    choice, and the game's navigation bake meets a rare layout of trunk holes it cannot triangulate (see REPORT.md).
    `fractals` (None: on a magnified room, off at today's scale, where nothing changes) places the island's Fractals and
    builds the ladder of gates they sit on (fractals.py); `typed_fractals` gives each its kind (the rules pack's flag,
    off by default); `tutorial` marks the island as one the menu lists first."""
    if fractals and world.W == 1:
        raise ValueError('Fractals are things at world scale 10: the 1x corpus stays byte-identical')
    if plant_salt is None:
        plant_salt = WORLD_PLANT_SALT if world.W != 1 else 0
    skip, accept = [], None
    while True:
        try:
            return _generate(room_dir, out_dir, setup, seed, return_state, cell, detail, skip, accept, plant_salt,
                             fractals, typed_fractals, tutorial)
        except HamletCutOff as cut:
            skip.append(cut.site)
            print('LANDSCAPE_HAMLET_CUT_OFF', cut, flush=True)
        _T0[0] = None
        if len(skip) >= HAMLET_SITES:
            accept = skip[0]   # no site is reached by any way: the best-ranked one stands, as it always did


def _generate(room_dir, out_dir, setup, seed, return_state, cell, detail, skip, accept=None, plant_salt=0,
              fractals=None, typed_fractals=False, tutorial=False):
    room_dir = Path(room_dir)
    room = load_json(room_dir/'room.json')
    inventory = load_json(room_dir/'inventory.json')
    setup = setup if setup is not None else load_json(SETUP_PATH)
    W = world.W
    if cell is None:
        cell = CELL if W == 1 else CELL_W
    if W != 1:
        # The 10x world: the true-size room magnified in memory (the package's source stays the true room).
        room, inventory = world.magnify(room, inventory, W)
    _tick('start')
    noise = Noise(seed, scale=W)
    rnd = random.Random(seed if not plant_salt else '%d/%d' % (seed, plant_salt))   # the planting's stream alone
    objects = parse_objects(inventory)
    lo, hi, _ = room_shape(room)
    # The grid's margin holds the coast's widest headland (x W), the reef, the
    # playable water and the shelf's fall past it (body).
    margin = MARGIN+.28*(W-1)
    grid = Grid(lo[0]-margin, lo[2]-margin, hi[0]+margin, hi[2]+margin, cell)
    nx = grid.nx
    spawns = [s['position_m'] for s in room['spawns']]
    # Each vertex's wall frame (signed distance, nearest wall point and normal), read once for the coast too.
    frames = [wall_frame(room, grid.xs[q % nx], grid.zs[q//nx]) for q in range(grid.n)]
    inside = [f[0] <= 0 for f in frames]
    outlet = choose_outlet(room, spawns)
    diag = math.hypot(hi[0]-lo[0], hi[2]-lo[2])
    # World scale: the plain, the landforms and their weathering are today's
    # island exactly: computed on the unmagnified room on today's 3 cm grid (its
    # own margin, unmagnified noise and boxes), then magnified onto this grid
    # (bilinear between today's samples, which lie 3 cm x W apart; heights x W).
    ugrid, unoise, uobjects = grid, noise, objects
    if W != 1:
        ugrid = Grid(lo[0]/W-MARGIN, lo[2]/W-MARGIN, hi[0]/W+MARGIN, hi[2]/W+MARGIN, CELL)
        unoise = Noise(seed)
        uobjects = [dict(o, cx=o['cx']/W, cz=o['cz']/W, base=o['base']/W, top=o['top']/W, sx=o['sx']/W,
                         sy=o['sy']/W, sz=o['sz']/W) for o in objects]
    # The plain: living ground tilting gently toward the pass where water leaves.
    base = []
    for q in range(ugrid.n):
        x, z = ugrid.xs[q % ugrid.nx], ugrid.zs[q//ugrid.nx]
        t = clamp(math.hypot(x*W-outlet['x'], z*W-outlet['z'])/diag)
        base.append(.035+.055*t+.018*unoise.fbm(x*.55+2, z*.55-1, 3))
    owner = [-1]*ugrid.n
    # One lee for the whole land: soft hills trail their long gentle side the
    # way the land drains, from the high country toward the pass, so every
    # hill shares the same weathering, like crag-and-tail country.
    hx_, hz_ = high_country(room, outlet)
    for o, uo in zip(objects, uobjects):
        a = math.radians(o['yaw'])
        dx, dz = outlet['x']-hx_, outlet['z']-hz_
        o['tail'] = uo['tail'] = math.atan2(math.sin(a)*dx+math.cos(a)*dz, math.cos(a)*dx-math.sin(a)*dz)
    h = uplift(ugrid, uobjects, base, unoise, owner)
    outside = [not v for v in inside]
    slope = slope_field(ugrid, h)
    h, acc = weather(ugrid, h, outside, slope, unoise)
    if W != 1:
        hu, bu, ou, au = h, base, owner, acc
        h, base, owner, acc = [], [], [], []
        # ugrid.sample and ugrid.nearest, each column's and each row's part worked out once.
        unx, unz, ux0, uz0, uc = ugrid.nx, ugrid.nz, ugrid.x0, ugrid.z0, ugrid.cell

        def part(v, v0, n):
            f = clamp((v-v0)/uc, 0, n-1.000001)
            k = int(f)
            return k, f-k, min(n-1, max(0, int(round((v-v0)/uc))))

        def landform(xs, zs):
            """(height, plain, owner, flow) at every (x, z), rows of zs by columns of xs."""
            cols = [part(x/W, ux0, unx) for x in xs]
            out = []
            for z in zs:
                j, v, jn = part(z/W, uz0, unz)
                r0, r1, rn = j*unx, (j+1)*unx, jn*unx
                for i, u, inn in cols:
                    a, b, c, d = hu[r0+i], hu[r0+i+1], hu[r1+i], hu[r1+i+1]
                    hv = ((a*(1-u)+b*u)*(1-v)+(c*(1-u)+d*u)*v)*W
                    a, b, c, d = bu[r0+i], bu[r0+i+1], bu[r1+i], bu[r1+i+1]
                    out.append((hv, ((a*(1-u)+b*u)*(1-v)+(c*(1-u)+d*u)*v)*W, ou[rn+inn], au[rn+inn]))
            return out
        for hv, bv, ov, av in landform(grid.xs, grid.zs):
            h.append(hv)
            base.append(bv)
            owner.append(ov)
            acc.append(av)
        # The 10x world's ground: 2.5 cm detail where the body meets the land (detail.py).
        if detail is None:
            detail = cell > 1.5*CELL
        detail = Detail(grid, W, noise, landform) if detail else None
    else:
        detail = None
    _tick('landforms')
    # ---------------- the island: the walls become a ragged coast, the door a jetty
    h_was = h[:] if detail else None
    coast = Sea.carve_coast(grid, room, h, base, noise, objects, spawns, seed, frames)
    del frames
    jetty = coast['jetty']
    if detail:
        detail.coast(coast, [q for q in range(grid.n) if h[q] < h_was[q]])
        del h_was
    _tick('coast')
    Sea.land_jetty(grid, h, jetty)
    if detail:
        detail.jetty(jetty)
    avoid_jetty = [] if jetty is None else [(jetty['root'][0], jetty['root'][1], .7)]

    def sea_goal(q):
        if h[q] >= Sea.SEA_Y-.03 or coast['s'][q] < coast['c'][q]+.05:
            return False
        return jetty is None or math.hypot(grid.xs[q % nx]-jetty['root'][0], grid.zs[q//nx]-jetty['root'][1]) > .8
    water = plan_and_carve(grid, room, objects, h, base, inside, spawns, setup, noise, outside,
                           sea=dict(level=Sea.SEA_Y, goal=sea_goal, avoid=avoid_jetty), detail=detail)
    wet = water['wet']
    _tick('water')
    # Banks the streams raised past the waterline go back under the sea.
    for q, v in coast['sea_cap'].items():
        if v < h[q]:
            h[q] = v
    if detail:
        detail.sea_cap()
    river_mouths = channel_cells(grid, water['streams'], margin=0.)
    sea = Sea.sea_mask(grid, h, river_mouths)
    for q in range(grid.n):
        if sea[q]:
            wet[q] = 0.
    sea_near = distance_field(grid, sea)
    reach = distance_field(grid, [w <= 0 for w in wet])
    for q in range(grid.n):
        if reach[q] < wet[q]:
            wet[q] = reach[q]
    if detail:
        detail.wet_final(wet[:], sea)
        detail.mark_water_edges(h, coast, water.get('lake'), sea)
    views = eyes(room)
    # Resting places where the review eyes stand: a small level clearing.
    for ex, ez, _, _ in views:
        R, blend = .16, .26
        rx, rz = grid.span(ex-R-blend, ex+R+blend, ez-R-blend, ez+R+blend)
        ring = sorted(h[j*nx+i] for j in rz for i in rx if math.hypot(grid.xs[i]-ex, grid.zs[j]-ez) <= R)
        if not ring or min(wet[j*nx+i] for j in rz for i in rx) < .1:
            continue
        level = ring[len(ring)//2]
        for j in rz:
            for i in rx:
                d = math.hypot(grid.xs[i]-ex, grid.zs[j]-ez)
                h[j*nx+i] = lerp(level, h[j*nx+i], smooth((d-R)/blend))
        if detail:
            detail.glade(ex, ez, R, blend, level)
    # ---------------- the hamlet: water, flat ground and shelter meet
    slope = slope_field(grid, h)
    avoid = lambda x, z: clear_of_eyes(views, x, z, .75, (2.3, .5))
    player0 = next((s for s in room['spawns'] if s['role'] == 'player'), room['spawns'][0])['position_m']
    lake = water.get('lake')

    def crossable(q):
        """A stream a footbridge can span (inside the land, never the lake)."""
        if not inside[q] or sea_near[q] < .3:
            return False
        return lake is None or lake_rho(lake, noise, grid.xs[q % nx], grid.zs[q//nx]) > 1.15
    dry = reachable_from(grid, h, wet, inside, (player0[0], player0[2]), crossable=crossable)
    site = accept or find_hamlet(grid, h, slope, wet, inside, spawns, avoid, lambda x, z: inside_distance(room, x, z),
                                 dry, skip)
    if site is None and skip:
        site = skip[0]   # no site left to try: the first one stands
    last = site is not None and site in skip   # the first site again, taken back: nothing more is tried
    # Every object's own footprint keeps its landform: no cottage, path or
    # grading cuts into it (paths go round).
    protect = [False]*grid.n
    # How low a thin top on an object's landform may weather: never under the
    # height its footprint is still read by (a third of its height, 15 cm at
    # least, under its top).
    floor_at = [-math.inf]*grid.n
    for o in objects:
        if o['parent'] is not None:
            continue
        R = math.hypot(o['sx'], o['sz'])/2+.05*W
        rx_, rz_ = grid.span(o['cx']-R, o['cx']+R, o['cz']-R, o['cz']+R)
        for j in rz_:
            for i in rx_:
                if rr_dist(*local(o, grid.xs[i], grid.zs[j]), max(o['sx']/2, .06*W), max(o['sz']/2, .06*W), .02*W) <= .03*W:
                    protect[j*nx+i] = True
                    floor_at[j*nx+i] = max(floor_at[j*nx+i], o['top']-max(.15*W, o['sy']/3)+.04*W)
    houses = []
    # A magnified room's coarse grid: the vertices of every cell meeting a cottage's footprint are levelled with its pad.
    pad_reach = 0. if detail is None else grid.cell

    def pad_cores(cottages):
        """A magnified room: each pad's core (its margin and the cells meeting the footprint) on its own level again,
        after the feathering of the pads laid beside it (a neighbour's skirt reached a footing)."""
        if pad_reach:
            for hh in cottages:
                flatten(grid, h, hh['x'], hh['z'], hh['hx'], hh['hz'], hh['yaw'], blend=1e-6, level=hh['y'],
                        reach=pad_reach)
    pads = [0.]*grid.n
    hold = [False]*grid.n   # a magnified room: the cottages' flattened pads, never cut or filled by a way graded later
    fields = [0.]*grid.n
    blocked = [False]*grid.n
    scatter = []
    objects_out = []
    hamlet = None
    if site is not None:
        sx, sz = site
        # Nearest water direction: doors face the water.
        best = None
        for j in range(grid.nz):
            for i in range(nx):
                q = j*nx+i
                if wet[q] == 0 and inside[q]:
                    d = math.hypot(grid.xs[i]-sx, grid.zs[j]-sz)
                    if best is None or d < best[0]:
                        best = (d, grid.xs[i], grid.zs[j])
        wx, wz = (best[1], best[2]) if best else (sx, sz-1)
        toward = math.atan2(wz-sz, wx-sx)
        cot = SIZES['cottage']
        for k, (da, r, sc) in enumerate([(0., .0, .74), (1.9, .72, .68), (-1.75, .7, .64), (3.1, .74, .7),
                                         (1.0, .8, .66), (-.9, .8, .66)]):
            a = toward+da
            x, z = sx+math.cos(a)*r, sz+math.sin(a)*r
            hx, hz = cot[0]*sc/2, cot[2]*sc/2
            # Face the water: local -Z (the door) points at the nearest water.
            yaw = math.degrees(math.atan2(-(wx-x), -(wz-z)))
            yaw = round(yaw/5)*5
            if inside_distance(room, x, z) < math.hypot(hx, hz)+.08:
                continue
            i, j = grid.nearest(x, z)
            if wet[j*nx+i] < math.hypot(hx, hz)+.06:
                continue
            cs, sn = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
            if any(protect[grid.nearest(x+cs*lx_+sn*lz_, z-sn*lx_+cs*lz_)[1]*nx+grid.nearest(x+cs*lx_+sn*lz_, z-sn*lx_+cs*lz_)[0]]
                   for lx_ in (-hx-.08, 0, hx+.08) for lz_ in (-hz-.2, -hz, 0, hz+.08)):
                continue   # never build over an object's landform
            # Room for an 11 cm lane (and a yard) between any two cottages.
            if any(math.hypot(x-hh['x'], z-hh['z']) < math.hypot(hx, hz)+math.hypot(hh['hx'], hh['hz'])+.16
                   for hh in houses):
                continue
            if not clear_of_eyes(views, x, z, .6, (2.0, .3)):
                continue
            level = flatten(grid, h, x, z, hx, hz, yaw, reach=pad_reach)
            # A level forecourt before the door: the land gives way to the threshold.
            ya = math.radians(yaw)
            flatten(grid, h, x-math.sin(ya)*(hz+.12), z-math.cos(ya)*(hz+.12), hx, .1, yaw, margin=.03,
                    blend=.14, level=level)
            if detail:
                detail.flatten(x, z, hx, hz, yaw, .07, .12, level)
                detail.flatten(x-math.sin(ya)*(hz+.12), z-math.cos(ya)*(hz+.12), hx, .1, yaw, .03, .14, level)
            houses.append(dict(x=x, z=z, yaw=yaw, scale=sc, y=level, hx=hx, hz=hz))
            if len(houses) == 3:
                break
        nx_ = grid.nx
        pad_cores(houses)
        for hh in houses:
            R = max(hh['hx'], hh['hz'])+.1
            rx, rz = grid.span(hh['x']-R, hh['x']+R, hh['z']-R, hh['z']+R)
            a = math.radians(hh['yaw'])
            c, s = math.cos(a), math.sin(a)
            for j in rz:
                for i in rx:
                    dx, dz = grid.xs[i]-hh['x'], grid.zs[j]-hh['z']
                    lx, lz = c*dx-s*dz, s*dx+c*dz
                    q = j*nx_+i
                    if abs(lx) <= hh['hx']+.01 and abs(lz) <= hh['hz']+.01:
                        blocked[q] = True
                    d = max(abs(lx)-hh['hx'], abs(lz)-hh['hz'], 0.)
                    pads[q] = max(pads[q], .55*(1-smooth(d/.08)))
        if detail:
            # The pad is levelled to its margin, but a way graded beside it (or through a gap between two cottages)
            # cut and filled cells of that margin outside `pads` > .3, which left a cottage's footing in the slope.
            hold = pad_hold(grid, houses, PAD_HOLD_M, pad_reach)
        hamlet = dict(x=sx, z=sz, water=[wx, wz], houses=len(houses))
        if skip:
            hamlet['given_up'] = [[round(a, 2), round(b, 2)] for a, b in skip]   # sites no way reached
        if detail:
            detail.hamlet(houses)
    _tick('hamlet')
    if W != 1:
        print('LANDSCAPE_HAMLET', hamlet, 'player', player0, 'lake', water.get('lake') and
              {k: round(v, 2) for k, v in water['lake'].items() if k in ('x', 'z', 'rx', 'rz')}, flush=True)
    # ---------------- paths (built and graded) and the carryable object
    paths = [0.]*grid.n
    player = next((s for s in room['spawns'] if s['role'] == 'player'), room['spawns'][0])
    px, pz = player['position_m'][0], player['position_m'][2]
    carry = None
    route = None
    roads = []
    bridges = []
    # Paths keep an 11 cm carrying lane clear of walls: route them that far off.
    near_built = distance_field(grid, blocked)
    lane_blocked = [d < .07 or protect[q] for q, d in enumerate(near_built)]
    keep_out = [1. if protect[q] or hold[q] else pads[q] for q in range(grid.n)]
    # The detail replays each path's cut and fill (its graded line) and its worn surface.
    on_road = None if detail is None else (lambda pts, g, width, reach: detail.road(pts, g, width, reach))
    on_grade = None if detail is None else (lambda pts, g, width, reach: detail.road(pts, g, width, reach, grade=True))

    def paint(pts, width=.035):
        paint_line(grid, pts, paths, width)
        if detail:
            detail.paint(pts, width)

    def forecourt(x, z):
        """The level a path or a graded way keeps where (x, z) lies on a
        cottage's forecourt or the jetty's landing, else None (the 10x world:
        the hamlet and the landing are levelled before the land is graded, and
        the ways graded to them keep their levels, so nothing has to be
        levelled again over a way the walk was graded for)."""
        for hh in houses:
            a = math.radians(hh['yaw'])
            c, s_ = math.cos(a), math.sin(a)
            dx, dz = x-(hh['x']-s_*(hh['hz']+.12)), z-(hh['z']-c*(hh['hz']+.12))
            if abs(c*dx-s_*dz) <= hh['hx']+.03 and abs(s_*dx+c*dz) <= .13:
                return hh['y']
        if jetty is not None and math.hypot(x-jetty['root'][0], z-jetty['root'][1]) <= .09:
            return jetty['top']-.004
        return None
    pins = forecourt if detail is not None else None
    if houses:
        door = houses[0]
        a = math.radians(door['yaw'])
        # In front of the first cottage's door, beside the path.
        fx = door['x']-math.sin(a)*(door['hz']+.12)+math.cos(a)*.13
        fz = door['z']-math.cos(a)*(door['hz']+.12)-math.sin(a)*.13
        road = build_road(grid, h, wet, inside, lane_blocked, keep_out, (px, pz), (fx, fz),
                          crossable=crossable, bridges=bridges, record=on_road, pins=pins)
        if road:
            roads.append(road)
            paint(road)
            for hh in houses[1:]:
                a = math.radians(hh['yaw'])
                dx_, dz_ = hh['x']-math.sin(a)*(hh['hz']+.08), hh['z']-math.cos(a)*(hh['hz']+.08)
                r2 = build_road(grid, h, wet, inside, lane_blocked, keep_out, (fx, fz), (dx_, dz_), .075,
                                crossable=crossable, bridges=bridges, record=on_road, pins=pins)
                if r2:
                    roads.append(r2)
                    paint(r2, .028)
    # A worn path from the spawn down to the jetty's landing.
    if jetty is not None:
        ix, iz = -jetty['ux'], -jetty['uz']
        jetty['stand'] = (jetty['root'][0]+ix*.12, jetty['root'][1]+iz*.12)
        r3 = build_road(grid, h, wet, inside, lane_blocked, keep_out, (px, pz), jetty['stand'], .075,
                        crossable=crossable, bridges=bridges, record=on_road, pins=pins)
        if r3:
            roads.append(r3)
            paint(r3, .028)
    _tick('paths')
    sea_pos, sea_tris = Sea.sea_surface(grid, sea)
    # The checks' walker looks only at the island and its water, not out to the horizon.
    look = dict(sea=dict(play_area=dict(bounds_m=dict(min_m=[grid.xs[0], 0, grid.zs[0]],
                                                      max_m=[grid.xs[-1], 0, grid.zs[-1]]))), cell_m=cell)
    beaches = Sea.find_beaches(grid, h, room, coast['coast'], jetty)
    shore_goals = [b['stand'] for b in beaches]+([jetty['root']] if jetty is not None else [])
    def settle(houses_too=True):
        """What grading may have disturbed, put right: the tarn's rim, the
        jetty's landing, the cottages' pads and forecourts (`houses_too`)."""
        # Whatever was levelled after the tarn was filled (glades, pads, paths)
        # may not breach its shore: the rim stands above the water again, except
        # where a stream carries the water on.
        if lake is not None:
            R = max(lake['rx'], lake['rz'])*1.6
            channel = set()
            for st in water['streams']:
                halves = st['half'] if isinstance(st['half'], list) else [st['half']]*len(st['pts'])
                for (x_, z_), hw in zip(st['pts'], halves):
                    if math.hypot(x_-lake['x'], z_-lake['z']) > R+.2:
                        continue
                    sx_, sz_ = grid.span(x_-hw-.05, x_+hw+.05, z_-hw-.05, z_+hw+.05)
                    channel.update(j*nx+i for j in sz_ for i in sx_
                                   if math.hypot(grid.xs[i]-x_, grid.zs[j]-z_) <= hw+.05)
            rx, rz = grid.span(lake['x']-R, lake['x']+R, lake['z']-R, lake['z']+R)
            for j in rz:
                for i in rx:
                    q = j*nx+i
                    if q not in channel and 1. <= lake_rho(lake, noise, grid.xs[i], grid.zs[j]) <= 1.35:
                        h[q] = max(h[q], lake['level']+.006)
            if detail:
                discs = []
                for st in water['streams']:
                    halves = st['half'] if isinstance(st['half'], list) else [st['half']]*len(st['pts'])
                    discs += [(x_, z_, hw+.05) for (x_, z_), hw in zip(st['pts'], halves)
                              if math.hypot(x_-lake['x'], z_-lake['z']) <= R+.2]
                detail.tarn_rim(lake, R, discs)
        # Paths and grading may have filled round the jetty's landing: level it again under the deck.
        Sea.land_jetty(grid, h, jetty)
        if detail:
            detail.jetty(jetty)
        # Grading toward the beaches and the jetty may have nicked a cottage's pad or
        # forecourt: level them again at the cottage's own floor.
        for hh in (houses if houses_too else ()):
            flatten(grid, h, hh['x'], hh['z'], hh['hx'], hh['hz'], hh['yaw'], level=hh['y'], reach=pad_reach)
            ya = math.radians(hh['yaw'])
            flatten(grid, h, hh['x']-math.sin(ya)*(hh['hz']+.12), hh['z']-math.cos(ya)*(hh['hz']+.12), hh['hx'], .1,
                    hh['yaw'], margin=.03, blend=.14, level=hh['y'])
            if detail:
                detail.flatten(hh['x'], hh['z'], hh['hx'], hh['hz'], hh['yaw'], .07, .12, hh['y'])
                detail.flatten(hh['x']-math.sin(ya)*(hh['hz']+.12), hh['z']-math.cos(ya)*(hh['hz']+.12), hh['hx'], .1,
                               hh['yaw'], .03, .14, hh['y'])
        if houses_too:
            pad_cores(houses)

    if detail is not None:
        # The 10x world: the hamlet, the landing and the tarn's rim are put
        # right before the walk, not after it: a cottage levelled again after
        # grading cut the ramp graded to its neighbour's door (the hamlet's
        # third door, its forecourt 6 cm under the next cottage's). The ways
        # graded below keep the forecourts' and the landing's levels (`pins`).
        settle()
    # Walk the graded land as a body would (11 cm lane, cottages in the way,
    # bridges underfoot). Where it cannot reach the crate or a door, the land
    # gives way: the route a steeper walk finds is cut and filled to grade.
    near = None   # the exact walk's last flood of the graded land from the spawn: the Fractals' places lie inside it
    cut_off = None
    if detail is not None and site is not None and not last and not (houses and roads):
        cut_off = 'no cottage placed' if not houses else 'no road built'
    if houses and roads:
        from .checks import Terrain as _T, Walker as _W
        keep_tris = [list(t) for t in grid.triangles() if inside_tri(grid, inside, t)]
        wrec = [{'mesh': w['name'], 'kind': w['kind']} for w in water['meshes']]+[{'mesh': 'sea', 'kind': 'still'}]
        wmesh = {w['name']: [dict(role='still_water', positions=w['positions'], triangles=w['triangles'])]
                 for w in water['meshes']}
        wmesh['sea'] = [dict(role='still_water', positions=sea_pos, triangles=sea_tris)]
        cots = [dict(prototype='cottage', position_m=[hh['x'], hh['y'], hh['z']], yaw_deg=hh['yaw'],
                     scale=[hh['scale']]*3) for hh in houses]
        decks = [dict(prototype='footbridge', position_m=[b['x'], b['top']-BRIDGE[1], b['z']], yaw_deg=b['yaw'],
                      scale=[1., 1., b['length']/BRIDGE[2]]) for b in bridges]
        goals = [(fx, fz)]+[(hh['x']-math.sin(math.radians(hh['yaw']))*(hh['hz']+.08),
                             hh['z']-math.cos(math.radians(hh['yaw']))*(hh['hz']+.08)) for hh in houses]
        goals += shore_goals
        # The land is read once; after grading, only where it moved (`Terrain.moved`).
        land_pos = [[grid.xs[q % nx], h[q], grid.zs[q//nx]] for q in range(grid.n)]
        terr0 = land = probe0 = None
        # A magnified room: the ways are graded (and each proved by a new walk) until every goal is reached or
        # RAMP_PASSES are spent; the last pass only proves. Today's room keeps its two passes.
        for _pass in range(2 if detail is None else RAMP_PASSES+1):
            jmesh = Sea.jetty_mesh(grid, h, jetty) if jetty is not None else None
            if terr0 is not None and jmesh == land.get('jetty'):
                moved = [q for q in range(grid.n) if land_pos[q][1] != h[q]]
                for q in moved:
                    land_pos[q][1] = h[q]
                terr0.moved([(land_pos[q][0], land_pos[q][2]) for q in moved])
            else:
                for q in range(grid.n):
                    land_pos[q][1] = h[q]
                land = {'land': [dict(role='meadow', positions=land_pos, triangles=keep_tris)], **wmesh}
                if jmesh is not None:
                    land['jetty'] = jmesh
                probe0 = dict(terrain=[{'mesh': 'land'}]+([{'mesh': 'jetty'}] if jetty is not None else []),
                              scenery=[], water=wrec, scatter=cots+decks, objects=[], x_generator=look,
                              prototypes={'footbridge': {'size_m': list(BRIDGE)}})
                terr0 = _T(probe0, land)
            strict = _W(probe0, land, terr0, room)
            loose = _W(probe0, land, terr0, room, limit=40., step=.04)
            # One flood from the spawn answers every goal; the steeper walk
            # finds its least-cost way only to the goals out of reach.
            near = strict.reach((px, pz))
            failing = [goal for goal in goals if not near.has(goal)]
            if detail is not None:
                lost = hamlet_lost(near, goals, len(houses))
                cut_off = None if not lost else 'no way to %d of %d goals after %d passes' % (len(lost), 1+len(houses), _pass)
                # Two passes as on today's room; more only while the hamlet is cut off (a way is graded in pieces, so
                # the count of goals reached may not move for several passes before it joins).
                if _pass == RAMP_PASSES or (_pass >= 2 and not lost):
                    break
            fixed = 0
            for path in (loose.ways((px, pz), failing) if failing else ()):
                if path:
                    grade_line(grid, h, wet, keep_out, resample([(k[0]*terr0.cell, k[1]*terr0.cell) for k in path], .025),
                               bridges=bridges, record=on_grade, pins=pins)
                    fixed += 1
            if not fixed:
                break
    if cut_off and not last:
        # No way graded to the hamlet's crate and doors on the land as built: the next site is tried.
        raise HamletCutOff(site, cut_off)
    _tick('graded')
    # On the 10x world only the tarn's rim and the landing are put right again (the ways keep the hamlet's levels).
    settle(houses_too=detail is None)
    # The mechanics round: the Fractals and the ladder of gates they sit on (fractals.py), built into the base grid
    # before the 2.5 cm ground is worked, from places the exact walk reaches (1x: nothing changes).
    fx_on = W != 1 if fractals is None else fractals
    fx_plan = None
    if fx_on and near is None:
        # No hamlet was graded to (the walk's flood was not made): one exact flood of the land as it stands.
        from .checks import Terrain as _T0, Walker as _W0
        cmesh = {w['name']: [dict(role='still_water', positions=w['positions'], triangles=w['triangles'])]
                 for w in water['meshes']}
        cmesh['sea'] = [dict(role='still_water', positions=sea_pos, triangles=sea_tris)]
        cmesh['land'] = [dict(role='meadow', positions=[[grid.xs[q % nx], h[q], grid.zs[q//nx]] for q in range(grid.n)],
                              triangles=[list(t) for t in grid.triangles() if inside_tri(grid, inside, t)])]
        if jetty is not None:
            cmesh['jetty'] = Sea.jetty_mesh(grid, h, jetty)
        cprobe = dict(terrain=[{'mesh': 'land'}]+([{'mesh': 'jetty'}] if jetty is not None else []), scenery=[],
                      water=[{'mesh': w['name'], 'kind': w['kind']} for w in water['meshes']]+[{'mesh': 'sea', 'kind': 'still'}],
                      scatter=[], objects=[], x_generator=look, prototypes={'footbridge': {'size_m': list(BRIDGE)}})
        near = _W0(cprobe, cmesh, _T0(cprobe, cmesh), room).reach((px, pz))
    if fx_on and near is not None:
        land_fx = Sites.Land(grid, h, inside, wet, sea, [coast['s'][q] > coast['c'][q] for q in range(grid.n)], protect,
                             pads, hold, paths, blocked, slope_field(grid, h),
                             set(detail.marks) if detail is not None else set(), [(x, z) for x, z, _, _ in views], near,
                             objects)
        fx_plan = Fractals.plan(land_fx, seed, (px, pz))
        for q in fx_plan.keep:
            blocked[q] = True   # the planting leaves each flight and its skirt bare
        if detail is not None:
            fx_plan.hold(detail)
        print('LANDSCAPE_FRACTALS_PLAN routes %d summit %s notes %s' % (
            len(fx_plan.routes), fx_plan.summit is not None, fx_plan.notes), flush=True)
        _tick('fractals')
    # What you climb, you can stand on top of: rock too thin to stand on (a
    # needle, a knife edge, the sliver where the coast cut a landform's
    # corner) weathers down to a ledge a body stands on (Lane P's climbs on
    # the garage). Water, its banks, paths, the hamlet, the jetty's landing
    # and the sea past the waterline stay as they are.
    jroot = None if jetty is None else jetty['root']

    def plane_ok(q):
        if wet[q] < .04 or paths[q] >= .3 or pads[q] > 0 or blocked[q] or coast['s'][q] > coast['c'][q]:
            return False
        return jroot is None or math.hypot(grid.xs[q % nx]-jroot[0], grid.zs[q//nx]-jroot[1]) >= .35
    # An object's own landform keeps the height its footprint is read by.
    ground = grid
    if detail is None:
        planed = give_lips(grid, h, lambda q: h[q] > Sea.SEA_Y+.005 and wet[q] > 0, plane_ok,
                           lambda q: max(0., h[q]-floor_at[q]))
    else:
        # The 10x world: the detail blocks are chosen round the body-sized
        # features (and round the climbs the base finds: a climb from standable
        # ground topping out on a knife edge), and every rule is replayed on
        # them at 2.5 cm. Where the body walks (the paths and graded ways, the
        # pads and forecourts, the glades, the jetty's landing) the blocks keep
        # the base's surface, the one the walk above was graded on; only its
        # surface is painted at 2.5 cm there.
        bad = knife_edges(grid, h, lambda q: h[q] > Sea.SEA_Y+.005 and wet[q] > 0)
        _tick('climbs')
        lip_q = set()
        for b in bad:
            cx_, cz_ = b['crest']
            rx_, rz_ = grid.span(cx_-.35, cx_+.35, cz_-.35, cz_+.35)
            lip_q.update(j*nx+i for j in rz_ for i in rx_)
        detail.build(h, objects, sorted(lip_q), wet)
        detail.walks_on_base(h)
        detail.merge(h)
        _tick('detail')
        # Rock too thin to stand on is planed at the body's size (a 10 cm
        # opening at 2.5 cm, not 30 cm on the base), in every block holding a
        # face a climber grabs.
        planed = detail.lips(h, plane_ok_fine(detail, jroot))
        detail.merge(h)
        ground = detail.ground(h)
        look['detail'] = dict(cell_m=detail.fc)
    _tick('lips')
    slope = slope_field(grid, h)
    tris = grid.triangles()
    fslope = face_slope_max(grid, h, tris)
    if houses and roads:
        route = roads[0]
        if route:
            gy = ground.tri_height(h, fx, fz)
            carry = dict(id='apple_crate', kind='crate', prototype='crate',
                         position_m=[round(fx, 4), round(gy-.002, 4), round(fz, 4)],
                         yaw_deg=round(door['yaw']+20, 1), size_m=[.08, .07, .07],
                         mass_kg=.12, carriable=True, tint=[1., .82, .7])
            objects_out.append(carry)
    shadow = sun_shadow(grid, h, water_sun(setup), reach_m=2.6*W)
    weights, tints = surface(grid, h, base, fslope, owner, objects, shadow, wet, acc, outside,
                             paths, pads, fields, noise)
    sea = Sea.sea_mask(grid, h, river_mouths)
    weights, tints = Sea.sand_and_seabed(grid, h, coast, sea, weights, tints)
    far_sea, islands = Sea.distant_islands(grid, room, noise, seed)
    if detail is None:
        meshes = {'land': build_mesh(grid, h, weights, tints, tris), 'distant_islands': far_sea}
    else:
        fweights, ftints = detail.surface(objects, shadow, noise, weights, tints)
        land, counts = detail.assemble(h, weights, tints, fweights, ftints)
        meshes = {'land': land, 'distant_islands': far_sea}
        if W != 1:
            print('LANDSCAPE_DETAIL', counts, flush=True)
    terrain_records = [{'mesh': 'land'}]
    if jetty is not None:
        meshes['jetty'] = Sea.jetty_mesh(ground, h, jetty)
        terrain_records.append({'mesh': 'jetty'})
    sea_pos, sea_tris = Sea.sea_surface(grid, sea)
    meshes['sea'] = [dict(role='still_water', positions=sea_pos, triangles=sea_tris)]
    water_records = [{'mesh': 'sea', 'kind': 'still'}]
    for w in water['meshes']:
        meshes[w['name']] = [dict(role='still_water' if w['kind'] == 'still' else 'flowing_water',
                                  positions=w['positions'], triangles=w['triangles'])]
        water_records.append({'mesh': w['name'], 'kind': w['kind']})
    protos = prototypes()
    proto_records = {}
    for name, (prims, size) in protos.items():
        meshes['proto_'+name] = prims
        proto_records[name] = {'mesh': 'proto_'+name, 'size_m': size}
    # Footbridges where the paths cross a stream: level decks, ends on the filled banks.
    for b in bridges:
        scatter.append(dict(prototype='footbridge', position_m=[round(b['x'], 4), round(b['top']-BRIDGE[1], 4),
                                                              round(b['z'], 4)],
                            yaw_deg=round(b['yaw'], 1), scale=[1., 1., round(b['length']/BRIDGE[2], 3)]))
    _tick('surface')
    # ---------------- planting
    # Things a body cannot pass keep an 11 cm carrying lane clear beside every path.
    path_gap = distance_field(grid, [v >= .5 for v in paths])
    plant = Planter(grid, h, fslope, wet, inside, shadow, paths, pads, blocked, base, owner, objects,
                    views, room, rnd, noise, acc)
    plant.path_gap = path_gap
    plant.heights = ground
    plant.sandy = [coast['bw'][q]*(1-coast['hl'][q]) > .3 and h[q] < Sea.SEA_Y+.08 or sea_near[q] < .02
                   for q in range(grid.n)]
    plant.bridge_rects = [(b['x'], b['z'], b['yaw'], b['length']) for b in bridges]
    for b in bridges:
        plant.occupy(b['x'], b['z'], b['length']/2+.05)
    if jetty is not None:
        plant.occupy(jetty['root'][0], jetty['root'][1], .2)
    for hh in houses:
        scatter.append(dict(prototype='cottage', position_m=[round(hh['x'], 4), round(hh['y']-.004, 4), round(hh['z'], 4)],
                            yaw_deg=hh['yaw'], scale=[hh['scale']]*3))
        plant.occupy(hh['x'], hh['z'], max(hh['hx'], hh['hz'])+.06)
    if carry:
        plant.occupy(carry['position_m'][0], carry['position_m'][2], .08)
    if houses:
        scatter += plant.hamlet_props(houses)
        # A trodden lane from the first door to each yard thing keeps it reachable.
        lane = [1. if v >= .5 else 0. for v in paths]
        road_pts = [pt for r in roads for pt in r]
        for rec in scatter:
            if rec['prototype'] in ('drying_line', 'woodpile'):
                ay = math.radians(rec['yaw_deg'])
                fronts = [(rec['position_m'][0]-math.sin(ay)*f, rec['position_m'][2]-math.cos(ay)*f) for f in (.1, -.1)]
                front, door = min(((f, min(road_pts, key=lambda pt: math.hypot(pt[0]-f[0], pt[1]-f[1])))
                                   for f in fronts), key=lambda fd: math.hypot(fd[0][0]-fd[1][0], fd[0][1]-fd[1][1]))
                steps = max(2, int(math.hypot(front[0]-door[0], front[1]-door[1])/.02))
                paint_line(grid, [(door[0]+(front[0]-door[0])*k/steps, door[1]+(front[1]-door[1])*k/steps)
                                  for k in range(steps+1)], lane, .02)
        plant.path_gap = distance_field(grid, [v >= .5 for v in lane])
    scatter += plant.everything()
    _tick('planting')
    # Promise only what a body can reach: a yard thing no 11 cm lane reaches
    # from the spawn is left out (the same walk the checks run).
    from .checks import YARD, Terrain, Walker, approaches, blocks, obstacles
    probe = dict(terrain=terrain_records, scenery=[], water=water_records, scatter=scatter,
                 prototypes=proto_records, objects=objects_out, x_generator=look)
    # Where planting closed the way to the crate, a door or a yard (laundry,
    # woodpile), the way is cleared: trees, shrubs, stones and fences within a
    # carrying lane of the route past the buildings are taken out. Only a yard
    # thing that even a cleared lane cannot reach is left out.
    keep = {'cottage', 'footbridge'} | set(YARD)
    walk_meshes = meshes
    if W != 1:
        # The 10x world: the walk reads the meshes as the package will hold them
        # (single precision) from the start, so the promised beaches are proved
        # on this very reading (`written_walk`), not on a second one.
        names = {r['mesh'] for r in terrain_records+water_records} | {'distant_islands'}
        walk_meshes = {k: (as_written(v) if k in names else v) for k, v in meshes.items()}
    terr = Terrain(probe, walk_meshes)
    # One flood from the spawn per planting state answers every target here,
    # the beaches and the jetty (flooded again only when the planting changes).
    walks = Walks(probe, walk_meshes, terr, room, (px, pz))

    def size_of(rec):
        base_size = SIZES[rec['prototype']] if rec['prototype'] in SIZES else proto_records[rec['prototype']]['size_m']
        return [b*s for b, s in zip(base_size, rec['scale'])]
    targets = [(None, [(carry['position_m'][0], carry['position_m'][2])])] if carry else []
    targets += [(None, [(hh['x']-math.sin(math.radians(hh['yaw']))*(hh['hz']+.08),
                         hh['z']-math.cos(math.radians(hh['yaw']))*(hh['hz']+.08))]) for hh in houses]
    targets += [(None, [g]) for g in shore_goals]
    # Each flight's foot is walked to: a tree in the way of it is taken out, as for the crate and the doors.
    targets += [(None, [r.xz(grid, -3)]) for r in (fx_plan.all_routes() if fx_plan else ())]
    targets += [(rec, approaches(rec, size_of(rec))) for rec in scatter if rec['prototype'] in YARD]
    dropped = []
    for rec_, goals_ in targets:
        if rec_ is not None and rec_ not in scatter:
            continue
        if any(walks.now().has(g) for g in goals_):
            continue
        # Toward the crate or a door even a yard thing may stand aside; toward
        # a yard, the other yards stay.
        hold = keep if rec_ is not None else {'cottage', 'footbridge'}
        fixed = Walker(dict(probe, scatter=[r for r in scatter if r['prototype'] in hold]), walk_meshes, terr, room)
        path = next((pth for pth in fixed.ways((px, pz), goals_, first=True) if pth), None)
        if path:
            pts = [(k[0]*terr.cell, k[1]*terr.cell) for k in path]
            near_path = {}
            for a, b in pts:
                near_path.setdefault((int(math.floor(a/.4)), int(math.floor(b/.4))), []).append((a, b))
            for rec in [r for r in scatter if r['prototype'] in BLOCKING and r['prototype'] not in hold and r is not rec_]:
                x, z = rec['position_m'][0], rec['position_m'][2]
                ix, iz = int(math.floor(x/.4)), int(math.floor(z/.4))
                near = [(a, b) for di in (-1, 0, 1) for dj in (-1, 0, 1) for a, b in near_path.get((ix+di, iz+dj), ())
                        if math.hypot(x-a, z-b) < .4]
                if near and any(blocks(o, a, b) for o in obstacles(dict(probe, scatter=[rec])) for a, b in near):
                    scatter.remove(rec)
                    walks.changed(removed=rec)
                    if rec['prototype'] in YARD:
                        dropped.append(rec['prototype'])
        if rec_ is not None and not any(walks.now().has(g) for g in goals_):
            # Move the yard rather than lose it: the first level, reachable spot
            # around any cottage.
            scatter.remove(rec_)
            walks.changed(removed=rec_)
            moved = None
            size = DRYING_LINE if rec_['prototype'] == 'drying_line' else [.09, .044, .1]
            for hh in houses:
                if moved:
                    break
                a = math.radians(hh['yaw'])
                c, s_ = math.cos(a), math.sin(a)
                for lx, lz, turn in [(hh['hx']+.32, -hh['hz']*.6, 90), (-hh['hx']-.32, -hh['hz']*.6, 90),
                                     (hh['hx']+.32, .1, 90), (-hh['hx']-.32, .1, 90), (0., hh['hz']+.32, 0),
                                     (hh['hx']+.45, -hh['hz']-.2, 45), (-hh['hx']-.45, -hh['hz']-.2, -45)]:
                    cand = plant.put(rec_['prototype'], hh['x']+c*lx+s_*lz, hh['z']-s_*lx+c*lz, rec_['scale'],
                                     hh['yaw']+turn, .02, size, sink=.002, built=True)
                    if not cand:
                        continue
                    if any(walks.reaches_with(cand, g) for g in approaches(cand, size_of(cand))) and \
                            all(walks.reaches_with(cand, g[0]) for _, g in targets if _ is None):
                        moved = cand
                        break
            if moved:
                scatter.append(moved)
                walks.changed()
            else:
                dropped.append(rec_['prototype'])
    if detail is not None and site is not None and not last:
        # The walk the checks make, on the land and the planting as they will be written: the crate and every door
        # still have their way (the base's proof above does not see the 2.5 cm ground or the planting).
        hamlet_goals = [g for _, goals_ in targets[:(1 if carry else 0)+len(houses)] for g in goals_]
        if hamlet_lost(walks.now(), hamlet_goals, len(houses)):
            raise HamletCutOff(site, 'the crate or a door is cut off on the land as written')
    _tick('yards')
    # Promise only the beaches a body can walk to from the spawn.
    final = walks.now()
    beaches = [b for b in Sea.find_beaches(ground, h, room, coast['coast'], jetty) if final.has(b['stand'])]
    if not beaches:
        # No beach stretch a body reaches: the harbour's sand beside the jetty.
        beaches = [b for b in Sea.find_beaches(ground, h, room, coast['coast'], jetty, harbour=True)
                   if final.has(b['stand'])][:1]
    rest = dict(walk_limit_deg=WALK_LIMIT_DEG, step_limit_m=STEP_LIMIT_M,
                                  water_character=water.get('character'), water_why=water.get('why'),
                                  lake=water['lake'], pools=water.get('pools', []),
                                  spring=[round(v, 3) for v in water.get('spring', (0, 0))],
                                  hamlet=hamlet, bridges=len(bridges), dropped_yard=dropped, planed=dict(vertices=planed[0], deepest_m=round(planed[1], 3), knife_edges_left=len(planed[2])),
                                  forms={o['id']: [o['form'], o['form_basis'], o['rock_role']] for o in objects},
                                  grounding=grounding(grid, h, objects, base))
    if detail:
        rest['detail'] = detail.describe()
    if plant_salt:
        rest['plant_salt'] = plant_salt
    if fx_plan is not None:
        rest['fractals'] = _finish_fractals(fx_plan, _climb_reach(final, grid, h, fslope, detail, fx_plan.keep), (px, pz),
                                            typed_fractals)
        summit = Fractals.summit_record(rest['fractals'])
        if summit is not None:
            rest['summit'] = summit    # the exporter writes it as the manifest's x_summit
        _tick('fractal reach')
    # The island's firepits (firepits.py), on every island: the exporter writes them as the manifest's x_firepits and the
    # kernel reads them there. Each claim is made on the package as it will be read back (single precision, the land grid read
    # from its mesh: checks.firepit_checks's own reading), so the written package proves it; nothing else in it changes.
    fdoc = dict(probe, x_generator=dict(look, **({'detail': rest['detail']} if detail else {}),
                                        **({'fractals': rest['fractals']} if 'fractals' in rest else {})))
    if W != 1:
        fmeshes, fterr, floods = walk_meshes, terr, {(px, pz): final}
    else:
        names = {r['mesh'] for r in terrain_records+water_records} | {'distant_islands'}
        fmeshes = {k: (as_written(v) if k in names else v) for k, v in meshes.items()}
        fterr, floods = Terrain(fdoc, fmeshes), {}
    from .checks import _base_reach
    fwalk, fbase = _base_reach(fdoc, fmeshes, fterr, room, floods)
    keep_fx = fx_plan.keep if fx_plan is not None else set()
    jroot = None if jetty is None else jetty['root']

    def no_firepit(q, x, z):
        return q in keep_fx or paths[q] >= Firepits.PATH_CLEAR or pads[q] > 0 or \
            (jroot is not None and math.hypot(x-jroot[0], z-jroot[1]) < Firepits.JETTY_CLEAR_M)
    start = (px, pz)   # the spawn: the kernel's starting campfire burns 0.2 m in front of it
    fractal_xz = [(it['position_m'][0], it['position_m'][2]) for it in rest.get('fractals', {}).get('items', ())]
    fp_land, fp_area, fp_spots = Firepits.land_and_spots(
        grid, h, fslope, inside, wet, sea, [blocked[q] and q not in keep_fx for q in range(grid.n)], no_firepit,
        fwalk.walker, fbase, objects, start, fractal_xz, Sea.SEA_Y)
    rest['firepits'] = Firepits.choose(fp_land, fp_area, fp_spots, start,
                                       None if hamlet is None else (hamlet['x'], hamlet['z']), houses)
    if W != 1:
        print('LANDSCAPE_FIREPITS', Firepits.report_line(rest['firepits']), flush=True)
    _tick('firepits')
    if tutorial:
        rest['tutorial'] = True

    def write(folder, beaches):
        sea_doc = describe_sea(grid, h, room, coast, jetty, beaches, islands, player0, water['streams'], ground)
        extensions = {'x_generator': dict(seed=seed, cell_m=cell, margin_m=margin, sea=sea_doc, **rest)}
        if W != 1:
            extensions['x_generator']['world_scale'] = W
        # The harness validates the places against the room magnified by x_generator.world_scale.
        return write_package(folder, room_dir, meshes=meshes, setup=setup, generator=GENERATOR,
                             terrain=terrain_records, water=water_records,
                             scenery=[{'mesh': 'distant_islands', 'reachable': False}],
                             prototypes=proto_records, scatter=scatter, objects=objects_out, extensions=extensions)
    # Promise only the beaches a body reaches in the written package itself:
    # its meshes are stored in single precision, and the walk is that strict.
    # The package is written once: the walk reads the meshes as the package
    # will hold them (`as_written`), and the flood's own way to each beach is
    # proved there step by step (flooded again only if a way fails).
    if beaches:
        sea_doc = describe_sea(grid, h, room, coast, jetty, beaches, islands, player0, water['streams'], ground)
        written = dict(terrain=terrain_records, scenery=[{'mesh': 'distant_islands', 'reachable': False}],
                       water=water_records, scatter=scatter, prototypes=proto_records, objects=objects_out,
                       x_generator=dict(sea=sea_doc, cell_m=cell, **({'world_scale': W} if W != 1 else {}),
                                        **({'detail': rest['detail']} if detail else {})))
        promised = [(rec['wash_ashore_m'][0], rec['wash_ashore_m'][2]) for rec in sea_doc['beaches']]
        kept = [b for b, ok in zip(beaches, written_walk(final, promised, written, meshes, room, (px, pz),
                                                         *((walk_meshes, terr) if W != 1 else ()))) if ok]
        beaches = kept or beaches
    _tick('beaches')
    doc = write(out_dir, beaches)
    _tick('written')
    if return_state:
        return doc, dict(grid=grid, h=h, route=route, carry=carry, houses=houses)
    return doc


def _climb_reach(final, grid, h, fslope, detail, keep=()):
    """The climb-aware reach of the planted island (`final`: the exact walk's flood from the spawn). The faces in a detail
    block are not the grid's (read at 2.5 cm), except a flight's (`keep`), which keeps the base's surface."""
    nx = grid.nx
    skip = None if detail is None else (lambda i, j: (i//10, j//10) in detail.block_set and j*nx+i not in keep)
    return Reach.ClimbReach(final.walker, final, grid, h, fslope=fslope, skip=skip)


def _finish_fractals(plan, cr, spawn, typed):
    """The benches proved and the Fractals placed on the climb-aware reach `cr` (fractals.finish): the package block."""
    block = Fractals.finish(plan, cr, spawn, typed=typed)
    c = block['counts']
    print('LANDSCAPE_FRACTALS placed %d (first run %d, out of reach %d) by_band %s benches %d proved %d routes %d' % (
        c['placed'], c['first_run'], c['out_of_reach'], c['by_band'], c['benches'], c['benches_proved'], c['routes']),
        flush=True)
    if block['summit']:
        s = block['summit']
        print('LANDSCAPE_SUMMIT %s top band %d (own bands %d) %d ledges (%d built) at %s, radius %.2f m' % (
            s['landmark'], s['top_band'], s['own_band'], s['ledges'], s['built_ledges'], s['top_m'], s['radius_m']), flush=True)
    return block


class Walks:
    """The walk from the spawn over the planted island: one flood per
    planting state (the scatter list as it stands), answering every target,
    beach and the jetty until something is taken out (`changed(removed)`: the
    flood goes on from where the lane opened) or put in (`changed()`)."""

    def __init__(self, probe, meshes, terrain, room, start):
        self.probe, self.meshes, self.terrain, self.room, self.start = probe, meshes, terrain, room, start
        self._now = None
        self._with = {}
        self._index = None
        self._opened = []

    def changed(self, removed=None):
        from .checks import CLEAR_R, obstacles
        self._with = {}
        if removed is None or self._now is None:
            self._now, self._index, self._opened = None, None, []
            return
        # The obstacle index without it (the same lists a fresh index would hold, in order).
        index = dict(self._index)
        for o in obstacles(dict(self.probe, scatter=[removed])):
            r = o[3] if o[0] == 'disc' else math.hypot(o[3], o[4])
            for i in range(int(math.floor((o[1]-r-CLEAR_R)/.2)), int(math.floor((o[1]+r+CLEAR_R)/.2))+1):
                for j in range(int(math.floor((o[2]-r-CLEAR_R)/.2)), int(math.floor((o[2]+r+CLEAR_R)/.2))+1):
                    got = list(index[(i, j)])
                    got.remove(o)
                    index[(i, j)] = got
            self._opened.append(o)
        self._index = index

    def now(self):
        from .checks import Walker, obstacle_index, obstacles
        if self._now is None:
            self._index = obstacle_index(obstacles(self.probe))
            self._now = Walker(self.probe, self.meshes, self.terrain, self.room, index=self._index).reach(self.start)
            self._opened = []
        elif self._opened:
            walker = Walker(self.probe, self.meshes, self.terrain, self.room, index=self._index)
            self._now = walker.reach_on(self._now, self._opened)
            self._opened = []
        return self._now

    def reaches_with(self, extra, goal):
        """Whether `goal` is reached once `extra` stands too. An obstacle more
        never opens a way; when the flood's own way keeps clear of it the way
        stands; otherwise the planting with `extra` is flooded."""
        from .checks import Walker, holds, obstacles
        base = self.now()
        path = base.path(goal)
        if path is None:
            return False
        if holds(base.walker, path, obstacles(dict(self.probe, scatter=[extra]))):
            return True
        got = self._with.get(id(extra))
        if got is None or got[0] is not extra:
            probe = dict(self.probe, scatter=self.probe['scatter']+[extra])
            got = self._with[id(extra)] = (extra, Walker(probe, self.meshes, self.terrain, self.room).reach(self.start))
        return got[1].has(goal)


def as_written(primitives):
    """Primitives as a written package reads them back: positions in single
    precision (the harness's GLB stores float32), triangles as they are."""
    out = []
    for p in primitives:
        flat = [v for row in p['positions'] for v in row]
        r = struct.unpack('<%df' % len(flat), struct.pack('<%df' % len(flat), *flat))
        out.append(dict(role=p['role'], positions=[list(r[i:i+3]) for i in range(0, len(r), 3)],
                        triangles=p['triangles']))
    return out


def written_walk(reach, goals, doc, meshes, room, start, single=None, terrain=None):
    """Which goals a body reaches on the package as written (`doc` as the
    package will read, its meshes in single precision), without writing it.
    The flood `reach` (the same walk on the generator's own meshes) offers a
    way to each goal; the written package's walker proves that way node by
    node and edge by edge, reading only the ground along it. A goal with no
    way, or whose way fails, is answered by flooding the written package."""
    from .checks import CLEAR_R, Terrain, Walker, proves
    if single is None:
        single = {r['mesh']: as_written(meshes[r['mesh']]) for r in doc['terrain']+doc['scenery']+doc['water']}
    paths = [reach.path(g) for g in goals]
    cell, b = reach.walker.t.cell, .1
    r = CLEAR_R+.01
    only = set()
    for path in paths:
        for k in path or ():
            x0, x1, z0, z1 = (k[0]-1)*cell-r, (k[0]+1)*cell+r, (k[1]-1)*cell-r, (k[1]+1)*cell+r
            only.update((i, j) for i in range(int(math.floor(x0/b)), int(math.floor(x1/b))+1)
                        for j in range(int(math.floor(z0/b)), int(math.floor(z1/b))+1))
    # `terrain`: the flood's own reading of these single-precision meshes (the 10x world's walk reads them so).
    walker = Walker(doc, single, terrain if terrain is not None else Terrain(doc, single, only=only), room)
    out, full = [], None
    for g, path in zip(goals, paths):
        if path is not None and proves(walker, path):
            out.append(True)
            continue
        if full is None:
            full = Walker(doc, single, terrain if terrain is not None else Terrain(doc, single), room).reach(start)
        out.append(full.has(g))
    return out


def _godot_yaw(dx, dz):
    """Yaw (degrees, Godot +Y rotation, -Z forward) that faces (dx, dz)."""
    return round(math.degrees(math.atan2(-dx, -dz)), 2)+0.   # never -0.0


def describe_sea(grid, h, room, coast, jetty, beaches, islands, player0, streams, ground=None):
    """The sea, the reef, the playable water, the beaches and the jetty, for
    the exporter and the game (x_generator.sea). Metres and degrees in the room
    frame; outlines are closed [x, z] loops in the room floor polygons' winding."""
    nx = grid.nx
    lo, hi, floors = room_shape(room)
    sign = -1. if floors and Sea.area([(p[0], p[2]) for p in floors[0]]) < 0 else 1.
    at = (player0[0], player0[2])

    def outline(f):
        loop = Sea.main_loop(Sea.contour(grid, f), at)
        # Simplified to a third of a cell (1 cm on today's 3 cm grid).
        tol = .01 if grid.cell <= CELL else grid.cell/3
        return [[round(x, 3), round(z, 3)] for x, z in Sea.oriented(Sea.simplify(loop, tol), sign)]
    shore = outline([Sea.SEA_Y-v for v in h])
    reef = outline([coast['s'][q]-coast['reef'][q] for q in range(grid.n)])
    play = outline([coast['s'][q]-coast['reef'][q]-Sea.PLAY_PAST_REEF for q in range(grid.n)])
    offs = [Sea.poly_seg_dist(x, z, shore) for x, z in reef] if shore else [0.]
    deepest = min([h[q] for q in range(grid.n) if coast['s'][q] < coast['reef'][q]+Sea.PLAY_PAST_REEF]
                  or [Sea.SEA_Y-Sea.DEEP_DEPTH])
    xs, zs = [p[0] for p in play] or [lo[0], hi[0]], [p[1] for p in play] or [lo[2], hi[2]]
    out = dict(
        level_m=Sea.SEA_Y, mesh='sea', swim_depth_m=Sea.SWIM_DEPTH,
        lagoon_depth_m=Sea.LAGOON_DEPTH, open_sea_depth_m=Sea.DEEP_DEPTH,
        coast=dict(outline_m=shore),
        # The reef line is the shelf's edge under water now (the clean coast): its
        # "crest" is the shelf's nominal floor, a lagoon's depth down, not rock.
        reef=dict(outline_m=reef, crest_y_m=round(Sea.SEA_Y-Sea.LAGOON_DEPTH, 4), band_half_width_m=.085,
                  offshore_m=[round(min(offs), 3), round(max(offs), 3)],
                  passes=[] if jetty is None else [dict(centre_m=[round(jetty['wall'][0]+jetty['ux']*jetty['reef'], 3),
                                                                  round(jetty['wall'][1]+jetty['uz']*jetty['reef'], 3)],
                                                        width_m=.36)]),
        play_area=dict(outline_m=play, past_reef_m=Sea.PLAY_PAST_REEF,
                       bounds_m=dict(min_m=[round(min(xs), 3), round(min(deepest, Sea.SEA_Y-Sea.DEEP_DEPTH)-.02, 3),
                                            round(min(zs), 3)],
                                     max_m=[round(max(xs), 3), room['bounds']['max_m'][1], round(max(zs), 3)])),
        beaches=[], jetty=None,
        stacks=[dict(centre_m=[st['x'], st['z']], radius_m=st['r'], top_m=st['top']) for st in coast['stacks']],
        river_mouths=[[round(st['pts'][-1][0], 3), round(st['pts'][-1][1], 3)] for st in streams
                      if st['W'][-1] <= Sea.SEA_Y+1e-6],
        distant_islands=islands)
    ground = grid if ground is None else ground
    for k, b in enumerate(beaches):
        (x, z), (wx, wz), (ix, iz) = b['stand'], b['water'], b['inward']
        out['beaches'].append(dict(id='beach_%02d' % k, wash_ashore_m=[round(x, 4), round(ground.tri_height(h, x, z), 4), round(z, 4)],
                                   yaw_deg=_godot_yaw(ix, iz),
                                   water_m=[round(wx, 4), Sea.SEA_Y, round(wz, 4)],
                                   water_depth_m=round(Sea.SEA_Y-ground.tri_height(h, wx, wz), 4)))
    if jetty is not None:
        (rx, rz), (ex, ez) = jetty['root'], jetty['end']
        out['jetty'] = dict(mesh='jetty', door_id=jetty['door_id'], root_m=[round(rx, 4), round(jetty['top'], 4), round(rz, 4)],
                            end_m=[round(ex, 4), round(jetty['top'], 4), round(ez, 4)], yaw_deg=_godot_yaw(ex-rx, ez-rz),
                            width_m=jetty['width'], length_m=round(math.hypot(ex-rx, ez-rz), 4),
                            deck_top_m=round(jetty['top'], 4))
    return out


def water_sun(setup):
    from pipeline.landscape.harness.views import sun_position
    return sun_position(setup['latitude_deg'], setup['day_of_year'], setup['solar_time_h'],
                        setup['neg_z_bearing_deg'])['direction_room']


def inside_tri(grid, inside, t):
    return sum(inside[q] for q in t) >= 2


DRYING_LINE = [.36, .2, .03]
CROWN_GAP_M = .13   # the exporter's tree fit: a crown's top is this far over the highest ground under it (12 cm walk clearance)
_CROWNS = {}


def tree_crown(name):
    """(R, high) of a tree prototype's highest crown at scale 1: the farthest a vertex lies from the trunk's axis, and its
    top. The exporter fits a tree's climbing collision to the ground under the crown (export/trees.py `fit`) and refuses
    a tree whose crown top is not 12 cm over the highest ground there."""
    got = _CROWNS.get(name)
    if got is None:
        from pipeline.landscape.harness.kit import prototype
        crowns = [p for p in prototype(name) if p['role'] == 'foliage']
        top = max(crowns, key=lambda p: max(v[1] for v in p['positions']))
        got = _CROWNS[name] = (max(math.hypot(v[0], v[2]) for v in top['positions']), max(v[1] for v in top['positions']))
    return got
_ROUND = tuple((math.cos(a), math.sin(a)) for a in (0, 1.57, 3.14, 4.71))   # a footing's four sides (Planter.ground)
BLOCKING = {'broadleaf', 'conifer', 'rock', 'boulder', 'shrub', 'cottage', 'woodpile', 'fence', 'lantern',
            'crate', 'drying_line'}


class Planter:
    """Plants follow water, slope, height and light; nothing roots in water,
    on a path, a pad or in front of a review eye."""

    def __init__(self, grid, h, fslope, wet, inside, shadow, paths, pads, blocked, base, owner, objects,
                 views, room, rnd, noise, acc):
        self.__dict__.update(locals())
        self.taken = []   # (x, z, r)
        self.cells = {}
        self.heights = grid   # the land's heights as built (detail.Ground on the 10x world)

    def occupy(self, x, z, r):
        self.taken.append((x, z, r))
        key = (int(math.floor(x/.5)), int(math.floor(z/.5)))
        self.cells.setdefault(key, []).append((x, z, r))

    def free(self, x, z, r):
        kx, kz = int(math.floor(x/.5)), int(math.floor(z/.5))
        for dx in (-1, 0, 1):
            for dz in (-1, 0, 1):
                for ox, oz, orr in self.cells.get((kx+dx, kz+dz), ()):
                    if math.hypot(x-ox, z-oz) < r+orr:
                        return False
        return True

    def at(self, x, z):
        g = self.grid   # grid.nearest, inline
        nx = g.nx
        i = int(round((x-g.x0)/g.cell))
        j = int(round((z-g.z0)/g.cell))
        i = 0 if i < 0 else nx-1 if i > nx-1 else i
        j = 0 if j < 0 else g.nz-1 if j > g.nz-1 else j
        return j*nx+i

    def ground(self, x, z, r):
        g, h = self.heights, self.h
        hs = [g.tri_height(h, x, z)]+[g.tri_height(h, x+r*ca, z+r*sa) for ca, sa in _ROUND]
        return min(hs), max(hs)

    def fits(self, x, z, size, scale):
        sx, sy, sz = size[0]*scale[0], size[1]*scale[1], size[2]*scale[2]
        r = math.hypot(sx, sz)/2
        return inside_distance(self.room, x, z) > r+.005

    def put(self, proto, x, z, scale, yaw, root, size, tint=None, sink=.004, clear=.0, built=False):
        if not self.fits(x, z, size, scale):
            return None
        q = self.at(x, z)
        if not self.inside[q] or self.wet[q] < .02+root or self.paths[q] > .2 or self.pads[q] > .05 or self.blocked[q]:
            return None
        if getattr(self, 'sandy', None) and self.sandy[q] and proto not in ('rock', 'boulder'):
            return None   # beaches stay open sand
        for bx, bz, by, bl in getattr(self, 'bridge_rects', ()):
            a_ = math.radians(by)
            dx, dz = x-bx, z-bz
            if abs(math.cos(a_)*dx-math.sin(a_)*dz) < .12 and abs(math.sin(a_)*dx+math.cos(a_)*dz) < bl/2+.05:
                return None   # nothing grows under or on a footbridge
        if proto in BLOCKING:
            if proto in ('broadleaf', 'conifer'):
                r = .045*scale[0]+.01
            elif proto in ('rock', 'boulder', 'shrub'):
                r = min(size[0]*scale[0], size[2]*scale[2])/2
            else:
                r = math.hypot(size[0]*scale[0], size[2]*scale[2])/2
            if self.path_gap[q] < r+.075:
                return None
        if built:
            # Built things need level ground under the whole footprint.
            a = math.radians(yaw)
            c, s = math.cos(a), math.sin(a)
            hx, hz = size[0]*scale[0]/2, size[2]*scale[2]/2
            hs = [self.heights.tri_height(self.h, x+c*lx+s*lz, z-s*lx+c*lz)
                  for lx in (-hx, 0, hx) for lz in (-hz, 0, hz)]
            lo_, hi_ = min(hs), max(hs)
            if hi_-lo_ > .012:
                return None
            sink = min(sink, .002)
        else:
            lo_, hi_ = self.ground(x, z, root)
            if hi_-lo_ > max(.012, .25*size[1]*scale[1]):
                return None
        y = lo_-sink
        if y < .0005:
            return None
        if world.W != 1 and proto in ('broadleaf', 'conifer'):
            # The 10x world: no tree stands at the foot of a wall higher than its crown (the exporter refuses its fit:
            # the first fixture with flights had two beside a 0.6 m bank). Today's room is as it was.
            R_, high = tree_crown(proto)
            r = max(.7*R_*scale[0], .07)+.02
            top = y+high*scale[1]
            if max(self.heights.tri_height(self.h, x+r*a, z+r*b) for a in (-1, -.5, 0, .5, 1)
                   for b in (-1, -.5, 0, .5, 1)) > top-CROWN_GAP_M:
                return None
        if y+size[1]*scale[1] > self.room['bounds']['max_m'][1]-.01:
            return None
        rec = dict(prototype=proto, position_m=[round(x, 4), round(y, 4), round(z, 4)],
                   yaw_deg=round(yaw, 1), scale=[round(s, 3) for s in scale])
        if tint:
            rec['tint'] = [round(c, 3) for c in tint]
        if clear:
            self.occupy(x, z, clear)
        return rec

    def hamlet_props(self, houses):
        out = []
        rnd = self.rnd
        for k, hh in enumerate(houses):
            a = math.radians(hh['yaw'])
            c, s = math.cos(a), math.sin(a)
            # Behind each cottage: a woodpile or crates; beside the first, a lantern.
            # A woodpile stacked against the gable or the back wall, with room to reach it.
            for lx, lz in [(hh['hx']+.14, .08), (-hh['hx']-.14, .08), (0., hh['hz']+.14), (hh['hx']+.14, -.1)]:
                x = hh['x']+c*lx+s*lz
                z = hh['z']-s*lx+c*lz
                rec = self.put('woodpile', x, z, [1.]*3, hh['yaw']+rnd.uniform(-15, 15), .02, [.09, .044, .1],
                               sink=.002, clear=.06, built=True)
                if rec:
                    out.append(rec)
                    break
            if (k+5) % 2 == 0:
                x = hh['x']-c*(hh['hx']+.05)+s*.1
                z = hh['z']+s*(hh['hx']+.05)+c*.1
                rec = self.put('crate', x, z, [.45]*3, hh['yaw']+rnd.uniform(-15, 15), .02, SIZES['crate'],
                               sink=.002, clear=.06, built=True)
                if rec:
                    out.append(rec)
        # Laundry drying on a line in the open beside a cottage.
        done = False
        for hh in houses:
            if done:
                break
            a = math.radians(hh['yaw'])
            c, s = math.cos(a), math.sin(a)
            # Door side first (local -Z), where the paths arrive.
            for lx, lz, turn in [(hh['hx']+.3, -hh['hz']*.6, 90), (-hh['hx']-.3, -hh['hz']*.6, 90),
                                 (hh['hx']+.3, 0., 90), (-hh['hx']-.3, 0., 90), (0., hh['hz']+.3, 0)]:
                x = hh['x']+c*lx+s*lz
                z = hh['z']-s*lx+c*lz
                rec = self.put('drying_line', x, z, [1., 1., 1.], hh['yaw']+turn, .02, DRYING_LINE, sink=.002,
                               clear=.3, built=True)
                if rec:
                    out.append(rec)
                    done = True
                    break
        hh = houses[0]
        a = math.radians(hh['yaw'])
        x = hh['x']-math.sin(a)*(hh['hz']+.1)-math.cos(a)*(hh['hx']+.02)
        z = hh['z']-math.cos(a)*(hh['hz']+.1)+math.sin(a)*(hh['hx']+.02)
        rec = self.put('lantern', x, z, [.5]*3, hh['yaw'], .01, SIZES['lantern'], sink=.002, clear=.04, built=True)
        if rec:
            out.append(rec)
        # A fenced garden on the sunny side of the hamlet.
        for hh in houses[:2]:
            a = math.radians(hh['yaw'])
            for side in (1,):
                for n in range(2):
                    lx, lz = side*(hh['hx']+.12), -hh['hz']+.05+n*.3
                    x = hh['x']+math.cos(a)*lx+math.sin(a)*lz
                    z = hh['z']-math.sin(a)*lx+math.cos(a)*lz
                    rec = self.put('fence', x, z, [.5, .55, .5], hh['yaw']+90, .03, SIZES['fence'], sink=.004, clear=.05,
                                   built=True)
                    if rec:
                        out.append(rec)
        return out

    def everything(self):
        W = world.W
        g = self.grid
        out = []
        rnd = self.rnd
        noise = self.noise
        lo, hi, _ = room_shape(self.room)

        def lattice(spacing):
            pts = []
            nxs = int((hi[0]-lo[0])/spacing)
            nzs = int((hi[2]-lo[2])/spacing)
            for b in range(nzs):
                for a in range(nxs):
                    pts.append((lo[0]+(a+rnd.random())*spacing, lo[2]+(b+rnd.random())*spacing))
            return pts

        def env(x, z):
            q = self.at(x, z)
            o = self.objects[self.owner[q]] if self.owner[q] >= 0 else None
            return q, self.h[q]-self.base[q], self.fslope[q], self.wet[q], self.shadow[q], o

        # Trees: riparian broadleaves; conifers on ridges, tops and the wild edges.
        for x, z in lattice(.2):
            q, rel, s, w, sh, o = env(x, z)
            if s > 30 or not clear_of_eyes(self.views, x, z, .45, (1.1, .25)):
                continue
            edge = smooth((.9*W-inside_distance(self.room, x, z))/(.6*W))
            riparian = smooth((.55-w)/.35)*(w > .06)
            high = smooth((rel-.18*W)/(.25*W))
            n = noise.fbm(x*1.6+9, z*1.6, 3)
            groves = smooth((n+.05)/.35)
            hills = smooth((rel-.04*W)/(.1*W))*(1-smooth((rel-.5*W)/(.2*W)))
            p_broad = .6*riparian+.22*groves*(1-high)*(1-edge)+.3*hills*groves
            p_con = .6*high*(1-smooth((s-26)/8))+.55*edge*groves+.2*groves*(sh > .5)
            u = rnd.random()
            if u < p_con*.6 and self.h[q] < 1.45*W:
                sc = .38+.3*rnd.random()
                scale = [sc, sc*(.9+.3*rnd.random()), sc]
                if self.free(x, z, .1*sc+.06):
                    rec = self.put('conifer', x, z, scale, rnd.uniform(0, 360), .03*sc, SIZES['conifer'],
                                   tint=(lerp(.75, .95, rnd.random()), lerp(.85, 1, rnd.random()), lerp(.8, .95, rnd.random())),
                                   sink=.008, clear=.1*sc+.06)
                    if rec:
                        out.append(rec)
            elif u < (p_con*.6+p_broad*.75):
                sc = .4+.3*rnd.random()
                scale = [sc, sc*(.85+.3*rnd.random()), sc]
                if self.free(x, z, .16*sc+.06):
                    rec = self.put('broadleaf', x, z, scale, rnd.uniform(0, 360), .035*sc, SIZES['broadleaf'],
                                   tint=(lerp(.85, 1, rnd.random()), lerp(.9, 1, rnd.random()), lerp(.7, .9, rnd.random())),
                                   sink=.008, clear=.16*sc+.06)
                    if rec:
                        out.append(rec)
        # Shrubs at grove edges and on banks; loose stones below cliffs.
        for x, z in lattice(.13):
            q, rel, s, w, sh, o = env(x, z)
            if not clear_of_eyes(self.views, x, z, .3, (.8, .18)):
                continue
            u = rnd.random()
            n = noise.fbm(x*1.6+9, z*1.6, 3)
            if s < 32 and u < .22*smooth((n+.2)/.4)+.2*smooth((.4-w)/.3):
                sc = .35+.35*rnd.random()
                if self.free(x, z, .1*sc):
                    rec = self.put('shrub', x, z, [sc, sc*(.8+.4*rnd.random()), sc], rnd.uniform(0, 360), .05*sc,
                                   SIZES['shrub'], tint=(lerp(.8, 1, rnd.random()), lerp(.9, 1, rnd.random()), .8),
                                   sink=.01, clear=.1*sc)
                    if rec:
                        out.append(rec)
            elif o is not None and o['form'] not in ('dome',) and 22 < s < 42 and u < .5:
                sc = .18+.35*rnd.random()
                if self.free(x, z, .07*sc+.01):
                    rec = self.put('rock', x, z, [sc, sc*(.7+.5*rnd.random()), sc], rnd.uniform(0, 360), .06*sc,
                                   SIZES['rock'], tint=tuple(lerp(1, c, .7) for c in o['tint']), sink=.01*sc+.004)
                    if rec:
                        out.append(rec)
        # Ground cover: grass everywhere it can grow; flowers in the morning sun;
        # heather on high dry tops; ferns in shade; reeds where feet get wet.
        for x, z in lattice(.07):
            q, rel, s, w, sh, o = env(x, z)
            if s > 34 or not clear_of_eyes(self.views, x, z, .22, (.45, .08)):
                continue
            u = rnd.random()
            yaw = rnd.uniform(0, 360)
            if w < .09:
                if u < .55 and w > .025:
                    sc = .7+.5*rnd.random()
                    rec = self.put('reeds', x, z, [sc, sc, sc], yaw, .02, [.08, .07, .08], sink=.004)
                    if rec:
                        out.append(rec)
                continue
            if sh > .55 and u < .5:
                sc = .2+.15*rnd.random()
                rec = self.put('fern', x, z, [sc, sc, sc], yaw, .03, SIZES['fern'], tint=(.8, 1, .85), sink=.004)
            elif rel > .3*W and sh < .4 and w > .5 and u < .55:
                sc = .8+.5*rnd.random()
                rec = self.put('heather', x, z, [sc, sc, sc], yaw, .03, [.13, .022, .13], sink=.003)
            elif sh < .35 and u < .16+.12*smooth((.8-w)/.6):
                kind = ('wildflowers', 'daisies', 'buttercups')[int(3*((noise(x*2.3, z*2.3)+1)/2*.999))]
                size = {'wildflowers': [.11, .03, .11], 'daisies': [.1, .028, .1], 'buttercups': [.09, .027, .09]}[kind]
                sc = .8+.5*rnd.random()
                tint = None
                if kind == 'wildflowers':
                    tint = [(1, .55, .5), (1, .8, 1), (.75, .6, 1)][int(rnd.random()*2.999)]
                rec = self.put(kind, x, z, [sc, sc, sc], yaw, .03, size, tint=tint, sink=.004)
            elif u < .78:
                sc = .8+.6*rnd.random()
                lush = smooth((.6-w)/.5)
                rec = self.put('grass', x, z, [sc, sc*(.8+.5*rnd.random()), sc], yaw, .03, [.14, .028, .14],
                               tint=(lerp(1, .85, lush), 1, lerp(.7, .85, lush)), sink=.004)
            else:
                rec = None
            if rec:
                out.append(rec)
        return out


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--room', required=True)
    parser.add_argument('--out', required=True)
    parser.add_argument('--setup', default=str(SETUP_PATH))
    parser.add_argument('--seed', type=int, default=SEED)
    # The 10x world (scale.py): the room magnified x W around today's body, in memory.
    parser.add_argument('--world-scale', type=float, default=1.)
    parser.add_argument('--cell', type=float, default=None,
                        help='base grid cell (m); default %g, or %g on a magnified room' % (CELL, CELL_W))
    parser.add_argument('--no-detail', action='store_true', help='a magnified room without its 2.5 cm detail')
    parser.add_argument('--plant-salt', type=int, default=None,
                        help='draw the planting again on the same land (default: %d on a magnified room, none at 1x; 0: none)' % WORLD_PLANT_SALT)
    # The mechanics round (fractals.py): on a magnified room the Fractals and the ladder of gates are built unless refused.
    parser.add_argument('--no-fractals', action='store_true', help='a magnified room without its Fractals and gates')
    parser.add_argument('--typed-fractals', action='store_true', help='each Fractal carries its kind (the rules pack flag)')
    parser.add_argument('--tutorial', action='store_true', help='mark the island as one the menu lists first')
    parser.add_argument('--reach-report', help='write the Fractals reach report (markdown) here')
    args = parser.parse_args()
    if args.world_scale != 1:
        world.set_world(args.world_scale)
    t0 = time.perf_counter()
    doc = generate(args.room, args.out, load_json(args.setup), args.seed, cell=args.cell,
                   detail=False if args.no_detail else None, plant_salt=args.plant_salt,
                   fractals=False if args.no_fractals else None, typed_fractals=args.typed_fractals,
                   tutorial=args.tutorial)
    if args.reach_report and 'fractals' in doc['x_generator']:
        Path(args.reach_report).write_text(Fractals.report_text(doc['x_generator']['fractals']), encoding='utf-8', newline='\n')
    if args.world_scale != 1:
        print('LANDSCAPE_TIMING generate_s %.1f' % (time.perf_counter()-t0))
    print('LANDSCAPE_PACKAGE', args.out, 'meshes', len(doc['meshes']), 'scatter', len(doc['scatter']),
          'objects', len(doc['objects']))


if __name__ == '__main__':
    main()
