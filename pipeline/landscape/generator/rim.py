"""The rim: the walls become great cliffs, with a desert on top (Run 4, packet WORLD-2).

    python -B -m pipeline.landscape.generator.generate --room <room folder> --world-scale 10 --edge rim --out <package>
    python -B -m pipeline.landscape.generator.rim --package <package> --room <room folder> --world-scale 10

A magnified room made with `--edge rim` has no sea. Past the wall line the land rises as a cliff of FACE_DEG to the
walls' own height (the lip), and past the lip lies level sand out to the tile edge, where the land's tiles end. Without
the option the land is the island it was, byte for byte: nothing here runs.

Openings. A window is a notch, cut from its sill up through the whole rim: its sides run straight through the cliff's
thickness (as far out as the lip) and widen FLARE_DEG a side past it; beyond it the desert lies at the sill's height,
between two scarps as steep as the cliffs (the low desert). So the sill is a broad bench one looks out of the window
from, a low sun can come in over the sand, and the sill opens no walk to the high desert. (Sides that widened from
the wall line met the cliff's face in a knife edge a climber topped out on; straight ones meet it in a corner.) A door is a bay: a recess in the cliff's foot with the room's own floor in it, closed at the back, where the
water leaves until WORLD-3 gives it a swallow-hole. A vent stays shut: it needs a roof.

Things against a wall. A thing standing nearer a wall than a slot (JOIN_GAP_M, true size) is a buttress: the gap behind
it is filled to the height of the thing's own edge, so its top runs into the cliff. A lifted thing nothing holds (a wall
shelf the scan kept) is one within LIFTED_JOIN_M. A gap a slot canyon can keep (WORLD-9) stays open.

The rim record (`x_generator.rim`) is the table of docs/runs/RUN-4.md, section 4, and nothing more. `Model` rebuilds the
rim's surface from that record alone, which is how the generator raises it too, so a reader of the record (BODY-4,
LOOK-24, WORLD-5) and the land agree. `validate` refuses a record that is not that shape: bounded numbers, closed
tokens, no other key.

The rim check (`rim_checks`) stands beside the sea check: on the written package it proves the record, that no sea is
left, that the land past the lip is level at the lip's height all round but for the openings, that each notch's floor
runs level out to the tile edge, that every wall is closed up to its lip, and that every buttress stands. `sun_gate`
sweeps the year's sun through each notch (the room's latitude and bearing, apparent solar time) and lists the hours a
ray comes through it and lands on the floor; a notch the sun never enters is said to have no beam.

Metres in the room's frame (y up, -Z forward); bearings in degrees clockwise from the room's -Z, seen from above.
"""
import argparse
import json
import math
from pathlib import Path

from .land import local, room_shape

FACE_DEG = 82.            # the cliffs' face, and every scarp's
TILE_EDGE_OUT_M = 10.     # the land's tiles end this far past the wall line (the grid's margin)
SEAM_PAST_LIP_M = 16.5    # blown sand is whole this far past the lip (as the open sea's seam was past the reef)
WRAP_PAST_SEAM_M = 1.5    # a walker still going out is turned this far past the seam
PERIOD_M = 12.8           # the sand's repeat; a wrap moves whole periods
FLARE_DEG = 30.           # a notch widens this much a side, past the lip
LIP_ROOM_M = 2.           # the lip keeps at least this much level sand inside the tiles (a taller wall's face steepens)
JOIN_GAP_M = .02          # true metres: a thing nearer a wall than this is a buttress (a slot's narrowest gap)
LIFTED_JOIN_M = .5        # true metres: a lifted thing nothing holds, this near a wall, is a buttress
JOIN_FACING = .7          # a side faces a wall when their normals agree this far (45 degrees)
BAY_DEPTH_K, BAY_DEPTH_M = .12, (1., 3.)   # a bay's depth: this share of its width, within these metres
MAX_OUTLINE, MAX_OPENINGS = 64, 16
NOTCH_KINDS = ('window',)
BAY_KINDS = ('door', 'garage_door', 'archway', 'portal', 'pet_door')
ON_WALL_M = .3            # true metres: an opening this far from the wall line is not this room's
FIELDS = ('outline_m', 'lip_y_m', 'lip_out_m', 'tile_edge_out_m', 'seam_out_m', 'wrap_out_m', 'period_m', 'openings')
OPENING_FIELDS = ('kind', 'centre_m', 'width_m', 'floor_y_m', 'flare_deg')
SAND_TINT = (1., .95, .8)       # the beaches' sand, until the roles of CR-3 land
SLAB_TINT = (1., 1., 1.)        # a bay's pale slab
CLIFF_TINT = (.98, .93, .86)    # the walls' rock (life.surface's)
# The sun gate's sweep: the 21st of each month, every SUN_STEP_MIN minutes of apparent solar time.
SUN_DAYS = (21, 52, 80, 111, 141, 172, 202, 233, 264, 294, 325, 355)
SUN_STEP_MIN = 10
SUN_MIN_ALT_DEG = 1.


# ---------------------------------------------------------------- the record
def _number(v, lo, hi, what):
    if type(v) not in (int, float) or not math.isfinite(v) or not lo <= v <= hi:
        raise ValueError('rim record: %s' % what)
    return float(v)


def validate(record):
    """The rim record, or ValueError: the table's fields and no other, bounded numbers, closed tokens."""
    if not isinstance(record, dict):
        raise ValueError('rim record: not a record')
    extra = set(record)-set(FIELDS)-{'home_wind_deg'}
    if extra or any(k not in record for k in FIELDS):
        raise ValueError('rim record: fields')
    outline = record['outline_m']
    if not isinstance(outline, list) or not 3 <= len(outline) <= MAX_OUTLINE:
        raise ValueError('rim record: outline')
    for p in outline:
        if not isinstance(p, list) or len(p) != 2:
            raise ValueError('rim record: outline point')
        for v in p:
            _number(v, -10000., 10000., 'outline point')
    if abs(_area([(p[0], p[1]) for p in outline])) < 1e-6:
        raise ValueError('rim record: outline encloses nothing')
    lip_y = _number(record['lip_y_m'], 1e-3, 1000., 'lip height')
    lip_out = _number(record['lip_out_m'], 1e-3, 1000., 'lip')
    tile = _number(record['tile_edge_out_m'], 1e-3, 1000., 'tile edge')
    seam = _number(record['seam_out_m'], 1e-3, 1000., 'sand seam')
    wrap = _number(record['wrap_out_m'], 1e-3, 1000., 'wrap')
    _number(record['period_m'], 1e-3, 1000., 'period')
    if not (lip_out < tile and lip_out < seam < wrap):
        raise ValueError('rim record: lip, tile edge, seam and wrap out of order')
    if 'home_wind_deg' in record:
        if _number(record['home_wind_deg'], 0., 360., 'home wind') >= 360.:
            raise ValueError('rim record: home wind')
    openings = record['openings']
    if not isinstance(openings, list) or len(openings) > MAX_OPENINGS:
        raise ValueError('rim record: openings')
    for o in openings:
        if not isinstance(o, dict) or set(o) != set(OPENING_FIELDS):
            raise ValueError('rim record: opening fields')
        if o['kind'] not in ('notch', 'bay'):
            raise ValueError('rim record: opening kind')
        if not isinstance(o['centre_m'], list) or len(o['centre_m']) != 2:
            raise ValueError('rim record: opening centre')
        for v in o['centre_m']:
            _number(v, -10000., 10000., 'opening centre')
        _number(o['width_m'], 1e-3, 1000., 'opening width')
        if _number(o['floor_y_m'], 0., 1000., 'opening floor') >= lip_y:
            raise ValueError('rim record: opening floor over the lip')
        _number(o['flare_deg'], 0., 60., 'opening flare')
    return record


def _area(poly):
    return sum(a[0]*b[1]-b[0]*a[1] for a, b in zip(poly, poly[1:]+poly[:1]))/2


def bearing_in_room(dx, dz):
    """Degrees clockwise from the room's -Z, seen from above (+X is a quarter turn clockwise of -Z)."""
    return math.degrees(math.atan2(dx, -dz)) % 360


def bay_depth(record, opening):
    """How far a bay reaches into the cliff: a share of its width, and never so far that its back wall's lip leaves
    the tiles. A function of the record, so a reader rebuilds the same bay."""
    room = record['tile_edge_out_m']-1.-record['lip_out_m']
    return max(.5, min(max(BAY_DEPTH_M[0], min(BAY_DEPTH_M[1], BAY_DEPTH_K*opening['width_m'])), room))


class Model:
    """The rim's surface, from its record alone. Inside the wall line it is the room's (0 here: the land is whatever
    stands there); outside, the cliff, the lip, each notch with its low desert and each bay."""

    def __init__(self, record):
        validate(record)
        self.record = record
        poly = [(float(p[0]), float(p[1])) for p in record['outline_m']]
        self.poly = poly
        self.sign = 1. if _area(poly) > 0 else -1.
        self.edges = []
        for a, b in zip(poly, poly[1:]+poly[:1]):
            ex, ez = b[0]-a[0], b[1]-a[1]
            self.edges.append((a[0], a[1], b[1], ex, ez, ex*ex+ez*ez))
        self.box = (min(p[0] for p in poly), min(p[1] for p in poly), max(p[0] for p in poly), max(p[1] for p in poly))
        # A plain room: four edges along the axes, so a point inside the box is inside the wall line.
        self.plain = len(poly) == 4 and all(e[3] == 0 or e[4] == 0 for e in self.edges)
        self.lip_y = float(record['lip_y_m'])
        self.lip_out = float(record['lip_out_m'])
        self.tile = float(record['tile_edge_out_m'])
        self.tan = self.lip_y/self.lip_out      # the face: 82 degrees unless a tall wall steepened it
        self.openings = [self._frame(k, o) for k, o in enumerate(record['openings'])]

    def _frame(self, k, o):
        """An opening in its own frame: the centre on the wall line, the wall's outward normal there and its tangent."""
        cx, cz = o['centre_m']
        best = None
        for ax, az, _, ex, ez, L in self.edges:
            t = 0. if L == 0 else min(1., max(0., ((cx-ax)*ex+(cz-az)*ez)/L))
            d = math.hypot(cx-ax-t*ex, cz-az-t*ez)
            if best is None or d < best[0]-1e-9:
                best = (d, ex, ez, L)
        _, ex, ez, L = best
        length = math.sqrt(L) or 1.
        tx, tz = ex/length, ez/length
        # For a loop of positive area the inside lies to the left of its direction: (-tz, tx).
        nx, nz = tz*self.sign, -tx*self.sign
        flare = math.radians(o['flare_deg'])
        f = dict(k=k, kind=o['kind'], x=float(cx), z=float(cz), nx=nx, nz=nz, tx=tx, tz=tz, half=o['width_m']/2,
                 floor=float(o['floor_y_m']), tan_flare=math.tan(flare), cos_flare=math.cos(flare))
        f['depth'] = bay_depth(self.record, o) if o['kind'] == 'bay' else None
        return f

    def inside(self, x, z):
        """Whether (x, z) lies inside the wall line."""
        x0, z0, x1, z1 = self.box
        if x < x0 or x > x1 or z < z0 or z > z1:
            return False
        if self.plain:
            return True
        hit = False
        for ax, az, bz, ex, ez, _ in self.edges:
            if (az > z) != (bz > z) and x < ex*(z-az)/ez+ax:
                hit = not hit
        return hit

    def out(self, x, z):
        """How far (x, z) lies from the wall line (metres, never negative; read it for points outside)."""
        best = math.inf
        hypot = math.hypot
        for ax, az, _, ex, ez, L in self.edges:
            t = 0. if L == 0 else ((x-ax)*ex+(z-az)*ez)/L
            t = 0. if t < 0. else 1. if t > 1. else t
            d = hypot(x-ax-t*ex, z-az-t*ez)
            if d < best:
                best = d
        return best

    def signed(self, x, z):
        """Distance to the wall line: negative inside, positive outside."""
        d = self.out(x, z)
        return -d if self.inside(x, z) else d

    def normal(self, x, z, e=.01):
        """The wall line's outward direction near (x, z): the way its distance grows."""
        gx = self.signed(x+e, z)-self.signed(x-e, z)
        gz = self.signed(x, z+e)-self.signed(x, z-e)
        g = math.hypot(gx, gz)
        return (0., 0.) if g < 1e-12 else (gx/g, gz/g)

    def above(self, x, z, s):
        """(height, cover) of the rim's own surface at a point `s` metres outside the wall line. Cover says what lies
        there: `sand` on the level top and on a notch's floor, `slab` in a bay, `cliff` on every face."""
        tan = self.tan
        y = tan*s
        cover = 'cliff'
        if y >= self.lip_y:
            y, cover = self.lip_y, 'sand'
        for f in self.openings:
            dx, dz = x-f['x'], z-f['z']
            a = dx*f['nx']+dz*f['nz']
            if a < 0:
                continue
            b = abs(dx*f['tx']+dz*f['tz'])
            if f['depth'] is None:
                # Straight through the cliff's thickness, widening past the lip: the nearer of the two sides.
                d = b-f['half']
                d = min(d, (d-(a-self.lip_out)*f['tan_flare'])*f['cos_flare'])
                cap, lies = (f['floor']+tan*d, 'cliff') if d > 0 else (f['floor'], 'sand')
            else:
                da, db = a-f['depth'], b-f['half']
                d = math.hypot(da if da > 0 else 0., db if db > 0 else 0.)
                cap, lies = (f['floor']+tan*d, 'cliff') if d > 0 else (f['floor'], 'slab')
            if cap < y:
                y, cover = cap, lies
        return y, cover

    def height(self, x, z):
        """The rim's surface at (x, z): 0 inside the wall line."""
        if self.inside(x, z):
            return 0.
        return self.above(x, z, self.out(x, z))[0]

    def far(self, x, z):
        """The sand's height past the tile edge, where the land's tiles have ended: the lip's, or a notch's floor in
        its low desert."""
        return self.above(x, z, max(self.out(x, z), self.lip_out))[0] if not self.inside(x, z) else 0.


# ---------------------------------------------------------------- the room's rim
def outline(room):
    """The wall line: the room's largest floor outline clipped to its bounds, as [x, z] in the floor's own winding,
    its points on one line dropped. A room whose floor is more than one outline has no single wall line."""
    lo, hi, floors = room_shape(room)
    if len(floors) > 1:
        raise ValueError('the rim needs one floor outline; this room has %d' % len(floors))
    if floors:
        poly = [(p[0], p[2]) for p in floors[0]]
    else:
        poly = [(lo[0], lo[2]), (lo[0], hi[2]), (hi[0], hi[2]), (hi[0], lo[2])]
    for axis, bound, keep_lo in ((0, lo[0], False), (0, hi[0], True), (1, lo[2], False), (1, hi[2], True)):
        out = []
        for a, b in zip(poly, poly[1:]+poly[:1]):
            ina = a[axis] <= bound if keep_lo else a[axis] >= bound
            inb = b[axis] <= bound if keep_lo else b[axis] >= bound
            if ina:
                out.append(a)
            if ina != inb:
                t = (bound-a[axis])/(b[axis]-a[axis])
                out.append((a[0]+(b[0]-a[0])*t, a[1]+(b[1]-a[1])*t))
        poly = out
    pts = []
    for p in poly:
        p = (round(p[0], 3)+0., round(p[1], 3)+0.)
        if not pts or p != pts[-1]:
            pts.append(p)
    if len(pts) > 1 and pts[0] == pts[-1]:
        pts.pop()
    tol = 0.
    while True:
        kept = [b for a, b, c in zip(pts[-1:]+pts[:-1], pts, pts[1:]+pts[:1])
                if abs((b[0]-a[0])*(c[1]-b[1])-(b[1]-a[1])*(c[0]-b[0])) > 1e-9+tol*math.hypot(c[0]-a[0], c[1]-a[1])]
        if len(kept) <= MAX_OUTLINE:
            break
        tol = max(.01, tol*2)   # a ragged scan: corners shallower than this are let go until the loop fits
    if len(kept) < 3:
        raise ValueError('the rim needs a floor outline')
    return [[x, z] for x, z in kept]


class Rim:
    """One room's rim: its record, the surface the record gives, the things joined into the cliff, and the rules the
    generator replays wherever it reads the land (the base grid and every detail block alike)."""

    def __init__(self, room, inventory, objects, setup, W, tile_edge_out=TILE_EDGE_OUT_M):
        if W == 1:
            raise ValueError('the rim is a magnified room\'s: at true size the land is the island it was')
        self.room, self.W, self.setup = room, W, setup
        self.objects = objects
        lo, hi, _ = room_shape(room)
        lip_y = round(float(hi[1]), 3)
        tile = float(tile_edge_out)
        lip_out = round(min(lip_y/math.tan(math.radians(FACE_DEG)), tile-LIP_ROOM_M), 3)
        record = dict(outline_m=outline(room), lip_y_m=lip_y, lip_out_m=lip_out, tile_edge_out_m=tile,
                      seam_out_m=round(lip_out+SEAM_PAST_LIP_M, 3), wrap_out_m=round(lip_out+SEAM_PAST_LIP_M+WRAP_PAST_SEAM_M, 3),
                      period_m=PERIOD_M, openings=[])
        bare = Model(record)
        self.shut = []      # openings left closed, and why
        found = []
        for o in sorted(room['shell'].get('openings', []), key=lambda o: o['id']):
            kind = 'notch' if o['kind'] in NOTCH_KINDS else 'bay' if o['kind'] in BAY_KINDS else None
            x, y, z = o['center_m']
            width, height = o['size_m']
            floor = max(0., y-height/2)
            why = None
            if kind is None:
                why = 'a %s needs a roof' % o['kind']
            elif abs(bare.signed(x, z)) > ON_WALL_M*W:
                why = 'not on the wall line'
            elif floor > lip_y-1.:
                why = 'its foot is over the lip'
            if why:
                self.shut.append(dict(id=o['id'], kind=o['kind'], why=why))
                continue
            nx, nz = bare.normal(x, z, .02*W)
            s = bare.signed(x, z)
            found.append((o, dict(kind=kind, centre_m=[round(x-nx*s, 3)+0., round(z-nz*s, 3)+0.], width_m=round(float(width), 3),
                                  floor_y_m=round(floor, 3), flare_deg=FLARE_DEG if kind == 'notch' else 0.)))
        if len(found) > MAX_OPENINGS:
            keep = sorted(found, key=lambda f: (-f[1]['width_m'], f[0]['id']))[:MAX_OPENINGS]
            self.shut += [dict(id=f[0]['id'], kind=f[0]['kind'], why='more openings than the record holds')
                          for f in found if f not in keep]
            found = [f for f in found if f in keep]
        self.opening_ids = [f[0]['id'] for f in found]
        record['openings'] = [f[1] for f in found]
        self.model = Model(record)
        # The home wind comes in by the widest door and blows into the room.
        doors = [(f, fr) for f, fr in zip(found, self.model.openings) if fr['kind'] == 'bay']
        if doors:
            _, fr = min(doors, key=lambda d: (-d[0][1]['width_m'], d[0][0]['id']))
            record['home_wind_deg'] = round(bearing_in_room(-fr['nx'], -fr['nz']), 1) % 360
        self.record = validate(record)
        self.land = None
        self.joins = self._joins(inventory, objects)
        self._sun = None

    # ------------------------------------------------------------ things against a wall
    def _joins(self, inventory, objects):
        ids = {e['id'] for e in inventory['objects']}
        lifted = {e['id'] for e in inventory['objects'] if e['placement']['support']['kind'] != 'floor'
                  and e['placement']['support'].get('target_id') not in ids}
        m, W = self.model, self.W
        out = []
        for k, o in enumerate(objects):
            if o['parent'] is not None:
                continue
            hung = o['id'] in lifted
            a, b = o['sx']/2, o['sz']/2
            ang = math.radians(o['yaw'])
            c, s = math.cos(ang), math.sin(ang)

            def to_room(lx, lz):
                return o['cx']+c*lx+s*lz, o['cz']-s*lx+c*lz
            for axis, sign in ((0, 1.), (0, -1.), (1, 1.), (1, -1.)):
                half_u, half_v = (b, a) if axis == 0 else (a, b)
                side = [to_room(*((sign*half_v, u) if axis == 0 else (u, sign*half_v))) for u in (-half_u, 0., half_u)]
                gaps = [-m.signed(x, z) for x, z in side]
                gap = min(gaps)
                if not (gap <= LIFTED_JOIN_M*W if hung else gap < JOIN_GAP_M*W):
                    continue
                ox, oz = (sign*c, -sign*s) if axis == 0 else (sign*s, sign*c)   # the side's own outward direction
                wx, wz = m.normal(*side[1], .02*W)
                facing = ox*wx+oz*wz
                if facing < JOIN_FACING:
                    continue
                ext = (max(max(gaps), 0.)+m.lip_out)/facing+.5
                inset = min(.045*W, .25*2*half_v)
                corners = [to_room(*((sign*v, u) if axis == 0 else (u, sign*v))) for u in (-half_u, half_u)
                           for v in (half_v, half_v+ext)]
                out.append(dict(k=k, o=o, axis=axis, sign=sign, half_u=half_u, half_v=half_v, ext=ext, inset=inset,
                                c=c, s=s, gap=gap, lifted=hung, cap=None, out=(ox, oz), mid=side[1],
                                x0=min(p[0] for p in corners), x1=max(p[0] for p in corners),
                                z0=min(p[1] for p in corners), z1=max(p[1] for p in corners)))
        return out

    def _fill(self, j, x, z):
        """The height a buttress stands at (x, z): the thing's own land just inside the side it turns to the wall,
        carried straight to the wall and into the cliff; None off the buttress."""
        o = j['o']
        lx, lz = local(o, x, z)
        u, v = (lz, lx*j['sign']) if j['axis'] == 0 else (lx, lz*j['sign'])
        if abs(u) > j['half_u'] or v <= j['half_v'] or v > j['half_v']+j['ext']:
            return None
        vi = j['sign']*(j['half_v']-j['inset'])
        px, pz = (vi, u) if j['axis'] == 0 else (u, vi)
        c, s = j['c'], j['s']
        top = self.land(o['cx']+c*px+s*pz, o['cz']-s*px+c*pz)[0]
        if j['cap'] is None:
            # No higher than the thing itself: what rests on it by this side does not climb the wall with it.
            j['cap'] = o['top']+self.land(o['cx'], o['cz'])[1]
        return min(top, j['cap'])

    # ------------------------------------------------------------ the land
    def under(self, landform):
        """`landform` (generate.py: rows of (height, plain, owner, flow) at every (x, z)) with the rim raised over
        it: the same rule at any point, so the base grid and every detail block read one land."""
        def at(x, z):
            return landform([x], [z])[0]
        self.land = at
        shape = self.shape

        def raised(xs, zs):
            rows = landform(xs, zs)
            out = []
            k = 0
            for z in zs:
                for x in xs:
                    hv, bv, ov, av = rows[k]
                    k += 1
                    y, who = shape(x, z, hv, ov)
                    out.append((y, bv, who, av))
            return out
        return raised

    def shape(self, x, z, hv, ov):
        """(height, owner) of the land at (x, z) under the rim, from the landform's own height and owner there."""
        m = self.model
        fill, who = None, -1
        for j in self.joins:
            if j['x0'] <= x <= j['x1'] and j['z0'] <= z <= j['z1']:
                t = self._fill(j, x, z)
                if t is not None and (fill is None or t > fill):
                    fill, who = t, j['k']
        if m.inside(x, z):
            if fill is not None and fill > hv:
                return fill, who
            return hv, ov
        y = m.above(x, z, m.out(x, z))[0]
        if y > hv:
            hv, ov = y, -1
        if fill is not None and fill > hv:
            return fill, who
        return hv, ov

    def no_coast(self, grid, frames):
        """The plan `sea.carve_coast` hands the rest of the generator, for a land with no coast: every vertex's wall
        distance, and a waterline no vertex ever lies past."""
        n = grid.n
        return dict(s=[f[0] for f in frames], c=[1e9]*n, reef=[1e9]*n, bw=[0.]*n, hl=[0.]*n, stacks=[], jetty=None,
                    coast=None, sea_cap={})

    def protect(self, grid, protect, floor_at):
        """A buttress keeps its land as the thing it joins does: no path, cottage or grading cuts into it."""
        nx, W = grid.nx, self.W
        for j in self.joins:
            o = j['o']
            keep = o['top']-max(.15*W, o['sy']/3)+.04*W
            rx, rz = grid.span(j['x0'], j['x1'], j['z0'], j['z1'])
            for jj in rz:
                for ii in rx:
                    if self.model.inside(grid.xs[ii], grid.zs[jj]) and self._fill(j, grid.xs[ii], grid.zs[jj]) is not None:
                        q = jj*nx+ii
                        protect[q] = True
                        floor_at[q] = max(floor_at[q], keep)

    def water_way(self, grid, outlet, inside):
        """Where the water leaves a land with no sea (`water.plan_and_carve`'s `sea`): into the bay of the door the
        plain tilts to, under the back wall; with no door there, to the wall's foot. Never a level it may not pass."""
        nx = grid.nx
        bay = None
        for f in self.model.openings:
            if f['depth'] is None:
                continue
            dx, dz = outlet['x']-f['x'], outlet['z']-f['z']
            if abs(dx*f['nx']+dz*f['nz']) <= 1. and abs(dx*f['tx']+dz*f['tz']) <= f['half']:
                bay = f
                break
        if bay is not None:
            side = max(0., bay['half']-.5)
            b0 = (outlet['x']-bay['x'])*bay['tx']+(outlet['z']-bay['z'])*bay['tz']
            b0 = max(-side, min(side, b0))

            def goal(q):
                dx, dz = grid.xs[q % nx]-bay['x'], grid.zs[q//nx]-bay['z']
                a, b = dx*bay['nx']+dz*bay['nz'], dx*bay['tx']+dz*bay['tz']
                return .5*bay['depth'] <= a <= bay['depth']-.2 and abs(b-b0) <= 1. and abs(b) <= bay['half']-.1
        else:
            def goal(q):
                return inside[q] and math.hypot(grid.xs[q % nx]-outlet['x'], grid.zs[q//nx]-outlet['z']) <= .5
        return dict(level=-1e6, goal=goal, avoid=[])

    # ------------------------------------------------------------ the surface
    def _cover(self, x, z):
        m = self.model
        if m.inside(x, z):
            return None
        return m.above(x, z, m.out(x, z))[1]

    @staticmethod
    def _paint(cover):
        if cover == 'cliff':
            return {'cliff': 1.}, CLIFF_TINT
        return {'gravel': 1.}, (SAND_TINT if cover == 'sand' else SLAB_TINT)

    def paint(self, grid, owner, wet, weights, tints):
        """Past the wall line the land is the rim's: sand on the level top and each notch's floor, a pale slab in a
        bay, rock on every face. A buttress keeps its thing's rock, and a stream's banks stay as the water left them."""
        nx = grid.nx
        for q in range(grid.n):
            if owner[q] >= 0 or wet[q] <= .1:
                continue
            cover = self._cover(grid.xs[q % nx], grid.zs[q//nx])
            if cover is not None:
                weights[q], tints[q] = self._paint(cover)

    def paint_fine(self, detail, fweights, ftints, weights, tints):
        """The same in the detail blocks, and on the base vertices they share."""
        cell = detail.g.cell
        for k in range(detail.n):
            if detail.S[k] < -cell or detail.owner[k] >= 0 or detail.wet[k] <= .1:
                continue
            cover = self._cover(detail.x[k], detail.z[k])
            if cover is not None:
                fweights[k], ftints[k] = self._paint(cover)
                q = detail.base_q[k]
                if q >= 0:
                    weights[q], tints[q] = fweights[k], ftints[k]

    # ------------------------------------------------------------ the package
    def blocks(self, height_at):
        """What the package says of its rim (x_generator): the record, the sun gate, and each buttress with a point in
        its gap and the height the land stands at there (`height_at(x, z)`: the land as built)."""
        rows = []
        for j in self.joins:
            reach = max(j['gap'], 0.)/2+.5*min(.05*self.W, j['ext'])
            x, z = j['mid'][0]+j['out'][0]*reach, j['mid'][1]+j['out'][1]*reach
            rows.append(dict(id=j['o']['id'], kind=j['o']['kind'], lifted=j['lifted'], gap_m=round(j['gap'], 3)+0.,
                             probe_m=[round(x, 3)+0., round(z, 3)+0.], stands_m=round(height_at(x, z), 3)))
        return dict(edge='rim', rim=self.record, sun_gate=self.sun_gate(), buttresses=rows,
                    rim_shut=self.shut, rim_openings=self.opening_ids)

    def sun_gate(self):
        if self._sun is None:
            self._sun = sun_gate(self.record, self.setup['latitude_deg'], self.setup['neg_z_bearing_deg'])
        return self._sun


def plan(room, inventory, objects, setup, W):
    return Rim(room, inventory, objects, setup, W)


# ---------------------------------------------------------------- the sun gate
def _beam(model, f, sun):
    """The rays of this sun that come through notch `f` and land on the floor, of those tried: each landing's
    distance from the notch's wall (metres) and where it is, as (reach, x, z).

    A ray through the notch crosses the sill's edge (where the cliff's face reaches the sill's height) above the
    sill, inside the notch at that height, and would have met the unbroken cliff. Out from there it must clear the two
    scarps until it stands over the lip. A scarp leans back from its foot, so at a height the notch is free as far as
    the wider of two straight lines (the straight side's and the widening side's); a straight ray inside the wider of
    two lines at the sill's edge, where the two cross and where it tops the lip is inside it all the way. In, it must
    come down on the floor inside the wall line."""
    dx, dy, dz = sun
    ca = dx*f['nx']+dz*f['nz']
    if ca <= 1e-3 or dy <= 0:
        return   # the sun stands behind this wall
    kt = (dx*f['tx']+dz*f['tz'])/ca     # sideways, per metre out
    ky = dy/ca                          # up, per metre out
    tan, lip_y, floor, lip_out = model.tan, model.lip_y, f['floor'], model.lip_out
    tan_f, cos_f = f['tan_flare'], f['cos_flare']
    a_s = floor/tan
    lean = (1-cos_f)/(tan_f*cos_f) if tan_f > 0 else 0.   # where the two lines cross, per metre of a scarp's lean

    def free(a, y):
        """Half the notch's free width `a` metres out, at height `y`."""
        s = (y-floor)/tan
        return f['half']+max(s, (a-lip_out)*tan_f+s/cos_f)
    steps = 16
    for i in range(steps):
        y_s = floor+(lip_y-floor)*(i+.5)/steps
        y0 = y_s-a_s*ky
        if y0 <= 0 or y_s+(lip_out-a_s)*ky >= lip_y:
            continue    # it meets the face under the sill, or it clears the cliff with no notch
        hw = free(a_s, y_s)
        a_top = a_s+(lip_y-y_s)/ky
        # Where the two lines cross along this ray (its height, and so a scarp's lean, grows as it goes out).
        k = ky/tan
        a_x = (lip_out-lean*((y_s-floor)/tan-a_s*k))/(1+lean*k)
        at = [(a_top, free(a_top, lip_y))]
        if a_s < a_x < a_top:
            at.append((a_x, free(a_x, y_s+(a_x-a_s)*ky)))
        a_land = -y0/ky
        for n in range(17):
            b_s = hw*(n-8)/8
            if any(abs(b_s+(a-a_s)*kt) > w for a, w in at):
                continue
            b_land = b_s+(a_land-a_s)*kt
            x = f['x']+f['nx']*a_land+f['tx']*b_land
            z = f['z']+f['nz']*a_land+f['tz']*b_land
            if model.inside(x, z):
                yield -a_land, x, z


def _lit(model, f, sun):
    """How far into the room this sun's beam through notch `f` reaches: its deepest landing's distance from the
    notch's wall (metres), or None when no ray comes through and lands on the floor."""
    return max((r for r, _, _ in _beam(model, f, sun)), default=None)


def sun_gate(record, latitude_deg, neg_z_bearing_deg):
    """Each notch's sunlit hours through the year: on the 21st of each month, the apparent solar hours between which a
    ray comes through the notch and lands on the floor, how long that is, and how far in the beam reaches at its
    deepest. A notch no ray enters on any of the days has no beam, and says so."""
    from pipeline.landscape.harness.views import sun_position
    model = Model(record)
    suns = {}
    for day in SUN_DAYS:
        row = []
        for n in range(24*60//SUN_STEP_MIN):
            t = n*SUN_STEP_MIN/60
            sun = sun_position(latitude_deg, day, t, neg_z_bearing_deg)
            if sun['altitude_deg'] >= SUN_MIN_ALT_DEG:
                row.append((t, sun['direction_room']))
        suns[day] = row
    notches = []
    for f in model.openings:
        if f['depth'] is not None:
            continue
        days = []
        for day in SUN_DAYS:
            lit = [(t, r) for t, r in ((t, _lit(model, f, sun)) for t, sun in suns[day]) if r is not None]
            if lit:
                days.append(dict(day=day, from_h=round(lit[0][0], 3), to_h=round(lit[-1][0], 3),
                                 lit_h=round(len(lit)*SUN_STEP_MIN/60, 3), reach_m=round(max(r for _, r in lit), 2)))
            else:
                days.append(dict(day=day, from_h=None, to_h=None, lit_h=0., reach_m=0.))
        beam = any(d['lit_h'] > 0 for d in days)
        row = dict(opening=f['k'], faces_deg=round((neg_z_bearing_deg+bearing_in_room(f['nx'], f['nz'])) % 360, 1) % 360,
                   beam=beam, days=days)
        if not beam:
            row['note'] = 'no beam: no sun comes through this notch on any of the days'
        notches.append(row)
    return dict(latitude_deg=latitude_deg, neg_z_bearing_deg=neg_z_bearing_deg, step_min=SUN_STEP_MIN,
                time='apparent solar', notches=notches)


# ---------------------------------------------------------------- the rim check
LEVEL_TOL_M = .005   # level sand lies this near its height
EDGE_CLEAR_M = .3    # level sand is read this far from where it ends (the land's grid rounds an edge)
SAMPLE_M = .5        # the rim is read on this lattice, and along each wall at this step
CLOSED_TOL_M = .05   # a wall's crest may stand this far under what its record says


def rim_of(doc):
    return (doc.get('x_generator') or {}).get('rim')


def rim_checks(doc, meshes, terrain, room):
    """The rim, proved on the written package (the land as its meshes hold it, the record as written):

    - the record is the table's shape (`validate`), and the package holds no sea;
    - the land's tiles reach the tile edge all round, and the player's spawn lies inside the wall line, under the lip;
    - level sand: wherever the record says the land is level (past the lip, and on each notch's floor past its
      sill's edge), it lies at that height, so the lip stands at the walls' height all round but for the openings and
      each notch runs out level to the tile edge onto its low desert;
    - closed walls: out from every stretch of wall the land climbs as high as the record says it does there (the lip;
      a notch's sill in its notch), so nothing but an opening breaks the cliffs;
    - every buttress the package lists stands where it says, as high as it says;
    - the sun gate listed is the one the record gives (`sun_gate`).
    """
    gen = doc.get('x_generator') or {}
    rows = {}
    try:
        model = Model(rim_of(doc))
    except (ValueError, TypeError, KeyError) as bad:
        return False, dict(error=str(bad) or 'no rim in the package (x_generator.rim)')
    rec = model.record
    ok = True
    rows['no_sea'] = 'sea' not in gen and not any(r['mesh'] == 'sea' for r in doc['water']) and 'sea' not in meshes
    ok &= rows['no_sea']
    # The land's tiles end at the tile edge.
    xs = [v[0] for p in meshes.get('land', ()) for v in p['positions']]
    zs = [v[2] for p in meshes.get('land', ()) for v in p['positions']]
    cell = gen.get('cell_m', .1)
    x0, z0, x1, z1 = model.box
    tile = model.tile
    rows['tiles_reach_m'] = [round(v, 3) for v in (x0-min(xs), z0-min(zs), max(xs)-x1, max(zs)-z1)] if xs else None
    rows['tiles_ok'] = bool(xs) and all(abs(v-tile) <= cell+1e-6 for v in rows['tiles_reach_m'])
    ok &= rows['tiles_ok']
    player = next((s for s in room['spawns'] if s['role'] == 'player'), room['spawns'][0])['position_m']
    rows['spawn_in_the_basin'] = model.inside(player[0], player[2]) and terrain.height(player[0], player[2]) < model.lip_y-1.
    ok &= rows['spawn_in_the_basin']
    # Level sand, read on a lattice over the tiles past the wall line.
    e = EDGE_CLEAR_M
    top = dict(points=0, off=0, worst_m=0.)
    floors = {f['k']: dict(points=0, off=0, worst_m=0., reach_m=0.) for f in model.openings if f['depth'] is None}
    ni, nj = int((x1-x0+2*tile)/SAMPLE_M), int((z1-z0+2*tile)/SAMPLE_M)
    for j in range(1, nj):
        z = z0-tile+j*SAMPLE_M
        for i in range(1, ni):
            x = x0-tile+i*SAMPLE_M
            if model.inside(x, z):
                continue
            y, cover = model.above(x, z, model.out(x, z))
            if cover != 'sand' or any(model.inside(a, b) or model.above(a, b, model.out(a, b)) != (y, 'sand')
                                      for a, b in ((x-e, z), (x+e, z), (x, z-e), (x, z+e))):
                continue
            row = top
            if y < model.lip_y:
                f = min((f for f in model.openings if f['depth'] is None and f['floor'] == y),
                        key=lambda f: math.hypot(x-f['x'], z-f['z']))
                row = floors[f['k']]
                row['reach_m'] = max(row['reach_m'], round((x-f['x'])*f['nx']+(z-f['z'])*f['nz'], 2))
            gap = abs(terrain.height(x, z)-y)
            row['points'] += 1
            if not gap <= LEVEL_TOL_M:      # (no land there at all reads as off, too)
                row['off'] += 1
                row['worst_m'] = max(row['worst_m'], round(gap, 4) if gap == gap else model.lip_y)
    rows['lip'] = dict(height_m=model.lip_y, **top)
    ok &= top['points'] > 0 and not top['off']
    rows['notches'] = []
    for f in model.openings:
        if f['depth'] is None:
            row = floors[f['k']]
            good = row['points'] > 0 and not row['off'] and row['reach_m'] >= tile-SAMPLE_M-e-1e-6
            rows['notches'].append(dict(opening=f['k'], floor_m=f['floor'], open_to_the_tile_edge=good, **row))
            ok &= good
    # Closed walls: out from each stretch of wall, the land's crest against the record's. Where the record's crest is
    # the level lip the land stands at it; where it is a face (a ray that leaves through an opening's side) the
    # land's grid rounds it by a cell of the face's rise.
    walls = dict(points=0, low=0, worst_m=0., through_openings=0)
    for (ax, az, _, ex, ez, L) in model.edges:
        length = math.sqrt(L)
        if length < 2*SAMPLE_M:
            continue
        tx, tz = ex/length, ez/length
        nx, nz = tz*model.sign, -tx*model.sign
        for k in range(1, int(length/SAMPLE_M)):
            px, pz = ax+tx*k*SAMPLE_M, az+tz*k*SAMPLE_M
            says = got = 0.
            for n in range(1, int((tile-e)/(SAMPLE_M/2))+1):
                x, z = px+nx*n*SAMPLE_M/2, pz+nz*n*SAMPLE_M/2
                if model.inside(x, z):
                    break   # round a corner and back in the room: this wall's own stretch has ended
                says = max(says, model.above(x, z, model.out(x, z))[0])
                y = terrain.height(x, z)
                if y > got:
                    got = y
            through = says < model.lip_y-1e-6
            slack = 1.5*model.tan*cell if through else CLOSED_TOL_M
            walls['points'] += 1
            walls['through_openings'] += through
            walls['low'] += got < says-slack
            if not through:
                walls['worst_m'] = max(walls['worst_m'], round(says-got, 3))
    rows['walls'] = walls
    ok &= walls['points'] > 0 and not walls['low']
    # Buttresses.
    bad = []
    for b in gen.get('buttresses', ()):
        x, z = b['probe_m']
        got = terrain.height(x, z)
        if got < b['stands_m']-CLOSED_TOL_M:
            bad.append(dict(id=b['id'], says_m=b['stands_m'], stands_m=round(got, 3)))
    rows['buttresses'] = dict(listed=len(gen.get('buttresses', ())), fallen=bad)
    ok &= not bad
    # The sun gate.
    setup = doc['setup']
    gate = sun_gate(rec, setup['latitude_deg'], setup['neg_z_bearing_deg'])
    rows['sun_gate_ok'] = gen.get('sun_gate') == gate
    ok &= rows['sun_gate_ok']
    rows['sun_gate'] = gate['notches']
    return bool(ok), rows


def run(package, room_dir):
    """Every check of checks.run on a package made with the rim, the rim check standing where the sea check stood
    (the result's `rim_ok` and `rim`; a package with a sea is checks.run's own)."""
    from pipeline.landscape.harness import read_package
    from pipeline.landscape.harness.common import load_json
    from . import checks, scale as world
    doc, meshes, room, stats = read_package(package, room_dir)
    if rim_of(doc) is None:
        return checks.run(package, room_dir)
    inventory = world.magnify_inventory(load_json(Path(room_dir)/'inventory.json'), checks.world_scale(doc))
    terrain = checks.Terrain(doc, meshes)
    w_ok, water, joins = checks.water_checks(doc, meshes, terrain)
    g_ok, ground = checks.grounding_checks(doc, terrain)
    floods = {}
    p_ok, walks = checks.walk_checks(doc, meshes, terrain, room, floods)
    f_ok, prints = checks.footprint_checks(inventory, terrain, room, checks.world_scale(doc), checks.stair_rects(doc))
    r_ok, rim = rim_checks(doc, meshes, terrain, room)
    c_ok, climbs = checks.climb_checks(doc, meshes)
    x_ok, fractals = checks.fractal_checks(doc, meshes, terrain, room, floods)
    fp_ok, firepits = checks.firepit_checks(doc, meshes, terrain, room, floods)
    return dict(ok=w_ok and g_ok and p_ok and f_ok and r_ok and x_ok and fp_ok, water_ok=w_ok, grounded_ok=g_ok,
                walk_ok=p_ok, footprints_ok=f_ok, rim_ok=r_ok, climb_ok=c_ok, fractals_ok=x_ok, firepits_ok=fp_ok,
                water=water, joins=joins, grounding=ground, walks=walks, footprints=prints, rim=rim, climbs=climbs,
                fractals=fractals, firepits=firepits, stats=stats)


def main():
    from . import scale as world
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--package', required=True)
    parser.add_argument('--room', required=True)
    parser.add_argument('--json', help='write the full result here')
    parser.add_argument('--world-scale', type=float, default=1., help='the package was built from a room magnified this much')
    args = parser.parse_args()
    if args.world_scale != 1:
        world.set_world(args.world_scale)
    result = run(args.package, args.room)
    if args.json:
        Path(args.json).write_text(json.dumps(result, indent=2, sort_keys=True)+'\n', encoding='utf-8', newline='\n')
    if 'rim' not in result:
        raise SystemExit('this package has a sea: run pipeline.landscape.generator.checks')
    rim = result['rim']
    for n in rim.get('sun_gate', ()):
        for d in n['days']:
            print('SUN_GATE', json.dumps(dict(d, opening=n['opening'], faces_deg=n['faces_deg']), sort_keys=True))
        if not n['beam']:
            print('SUN_GATE', json.dumps(dict(opening=n['opening'], faces_deg=n['faces_deg'], note=n['note']), sort_keys=True))
    print('RIM', json.dumps({k: v for k, v in rim.items() if k != 'sun_gate'}, sort_keys=True))
    bad = [f for f in result['footprints'] if not f['reads']]
    for f in bad:
        print('FOOTPRINT_LOST', json.dumps(f, sort_keys=True))
    print('CLIMB', json.dumps(result['climbs'], sort_keys=True))
    print('CHECKS water=%s grounded=%s walk=%s footprints=%s rim=%s climb=%s fractals=%s firepits=%s overall=%s' % (
        result['water_ok'], result['grounded_ok'], result['walk_ok'], result['footprints_ok'], result['rim_ok'],
        result['climb_ok'], result['fractals_ok'], result['firepits_ok'], result['ok']))
    raise SystemExit(0 if result['ok'] else 1)


if __name__ == '__main__':
    main()
