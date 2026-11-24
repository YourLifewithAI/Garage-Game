"""The rim: the walls become great cliffs, with a desert on top (Run 4, packet WORLD-2).

    python -B -m pipeline.landscape.generator.generate --room <room folder> --world-scale 10 --edge rim --out <package>
    python -B -m pipeline.landscape.generator.rim --package <package> --room <room folder> --world-scale 10

A magnified room made with `--edge rim` has no sea. Past the wall line the land rises as a cliff of FACE_DEG to the
walls' own height (the lip), and past the lip lies level sand out to the tile edge, where the land's tiles end. Without
the option the land is the island it was, byte for byte: nothing here runs.

Openings. A window is a notch, cut from its sill up through the whole rim: its sides run straight through the cliff's
thickness and its mouth widens FLARE_DEG a side from the lip on; beyond it the desert lies at the sill's height,
between two scarps as steep as the cliffs (the low desert). So the sill is a broad bench one looks out of the window
from, a low sun can come in over the sand, and the sill opens no walk to the high desert. The widening starts where
the scarps' tops, at the lip's height, part at the lip (a scarp leans back, so its foot parts a little further out):
inside the lip no face of a notch falls as one goes out, and a side meets the cliff's face in a corner, never in a
knife edge a climber tops out on. A door is a bay: a recess in the cliff's foot with the room's own floor in it,
closed at the back, where the water leaves until WORLD-3 gives it a swallow-hole. A vent stays shut: it needs a roof.
In a room that is not a plain box an opening ends where it would meet another wall: every wall but its own keeps its
cliff and KEEP_TOP_M of level sand behind its lip, and a scarp falls from there to the opening's floor.

Things against a wall. A thing standing within JOIN_GAP_M of a wall (true size, with room for a scan's error), a side
turned to it, stands against it and is a buttress: from the thing's own crest (the tallest its land stands between
its middle and that side) the land runs level to the wall and into the cliff, so its top is the cliff's with no gap
between, whether its rock fills its box (a desk) or stands back from the box's side (a bicycle's fin). A lifted thing
nothing holds (a wall shelf the scan kept) is one within LIFTED_JOIN_M. A buttress stops at a door (a bay keeps its
floor) and rises no higher than a window's sill in the window (the notch stays open). The founder asked that things
on or against the walls become parts of the cliffs, so the gap a slot canyon could keep behind such a thing (WORLD-9:
2 to 10 cm) is filled; `--wall-join 0.02` on the generator keeps those slots open instead (only a thing nearer than a
slot joins; any gap from 0 to 1 m may be given), and the package says which rule made it (`x_generator.rim_join`).

The rim record (`x_generator.rim`) is the table of docs/runs/RUN-4.md, section 4, and nothing more. `Model` rebuilds the
rim's surface from that record alone, which is how the generator raises it too, so a reader of the record (BODY-4,
LOOK-24, WORLD-5) and the land agree. `validate` refuses a record that is not that shape: bounded numbers, closed
tokens, no other key.

The rim check (`rim_checks`) stands beside the sea check. It proves the written package against the room, not against
the package's own word: the record is the room's (the wall line its floor's outline, the lip at its walls' height,
each window a notch and each door a bay where it stands); no sea is left; the land past the lip is level at the lip's
height all round but for the openings; each notch's floor runs level out to the tile edge; every wall is closed up to
its lip but for its own openings; and every thing the room's inventory stands against a wall is joined to it, with no
gap between its edge and the cliff. `sun_gate` sweeps the year's sun through each notch (the room's latitude and
bearing, apparent solar time) and lists two things: the hours a ray comes through it into the room at all, and the
hours one lands on the bare floor; a notch the sun never enters is said to have no beam.

Metres in the room's frame (y up, -Z forward). Every bearing is in the room's frame too, degrees clockwise from the
room's -Z seen from above (`home_wind_deg`, `faces_deg`), but the one named a compass bearing (`faces_compass_deg`).
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
# True metres: a thing standing this near a wall stands against it, and is a buttress. 15 cm for the thing itself (the
# corpus's wall furniture stands 5 to 12.5 cm off) and 10 cm more for the scan, which may place a box that far from
# where the thing stands (corpus/README.md, the imperfection budget): over the corpus's eight rooms each thing that
# truly stands within 15 cm of a wall is still within 25 cm of it in both scans (24.8 cm at most), where 15 cm let 9
# of 25 go in one scan or the other.
JOIN_GAP_M = .25
SLOT_GAP_M = .02          # true metres: a slot's narrowest gap (`--wall-join 0.02`: only a thing nearer than a slot joins)
LIFTED_JOIN_M = .5        # true metres: a lifted thing nothing holds, this near a wall, is a buttress
JOIN_FACING = .7          # a side faces a wall when their normals agree this far (45 degrees)
CREST_STEP_M = .015       # true metres: a thing's crest is read on lines this far apart
CREST_READ_M = .005       # true metres: and along each line at this step, which is the rim check's too
CREST_TOL_M = .005        # true metres: where a thing's land stands this near its crest, it is the crest still
KEEP_TOP_M = 2.           # the level sand another wall keeps behind its lip where an opening would meet it
SAME_WALL_K = .25         # an edge lying this near an opening's own wall line (a share of the lip's reach) is that wall
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
    # (The bounds are read before anything is made a float: a whole number too large for one is out of bounds, not an
    # overflow; a number that is not one fails every comparison.)
    if type(v) not in (int, float) or not lo <= v <= hi:
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
        # Where a notch's foot starts to widen: so far past the lip that the two faces of a side (the straight one
        # and the widening one, each leaning back at the cliffs' angle) part at the lip's height over the lip itself.
        f['lean'] = (1-f['cos_flare'])/(f['tan_flare']*f['cos_flare']) if f['tan_flare'] > 0 else 0.
        f['flare_from'] = self.lip_out+f['lean']*(self.lip_y-f['floor'])/self.tan
        # Its own wall: every edge lying along its wall line (a ragged scan gives one wall several edges).
        same = SAME_WALL_K*self.lip_out
        f['wall'] = frozenset(j for j, (ax, az, _, ex_, ez_, _L) in enumerate(self.edges)
                              if abs((ax-cx)*nx+(az-cz)*nz) <= same and abs((ax+ex_-cx)*nx+(az+ez_-cz)*nz) <= same)
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

    def foot(self, x, z):
        """(x, z, distance) of the wall line's nearest point to (x, z)."""
        best = (math.inf, x, z)
        for ax, az, _, ex, ez, L in self.edges:
            t = 0. if L == 0 else min(1., max(0., ((x-ax)*ex+(z-az)*ez)/L))
            d = math.hypot(x-ax-t*ex, z-az-t*ez)
            if d < best[0]:
                best = (d, ax+t*ex, az+t*ez)
        return best[1], best[2], best[0]

    def ahead(self, x, z, dx, dz):
        """(distance, edge) to the wall straight ahead of (x, z) along the unit direction (dx, dz): where the line
        from the point first meets the wall line, and which of its edges that is. From a point outside the wall
        line (a scanned box that pokes through a wall) it is the way back in, as a negative distance. (inf, None)
        where no wall lies that way."""
        back = not self.inside(x, z)
        if back:
            dx, dz = -dx, -dz
        best = (math.inf, None)
        for k, (ax, az, _, ex, ez, _L) in enumerate(self.edges):
            den = dx*ez-dz*ex
            if abs(den) < 1e-12:
                continue
            t = ((ax-x)*ez-(az-z)*ex)/den
            u = ((ax-x)*dz-(az-z)*dx)/den
            if 1e-9 < t < best[0] and -1e-9 <= u <= 1.+1e-9:
                best = (t, k)
        return (-best[0], best[1]) if back else best

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
                # Straight through the cliff's thickness, widening from `flare_from` on: the nearer of the two sides.
                d = b-f['half']
                d = min(d, (d-(a-f['flare_from'])*f['tan_flare'])*f['cos_flare'])
                cap, lies = (f['floor']+tan*d, 'cliff') if d > 0 else (f['floor'], 'sand')
            else:
                da, db = a-f['depth'], b-f['half']
                d = math.hypot(da if da > 0 else 0., db if db > 0 else 0.)
                cap, lies = (f['floor']+tan*d, 'cliff') if d > 0 else (f['floor'], 'slab')
            if cap < y and not self.plain:
                keep = self._kept(f, x, z)
                if keep > cap:
                    cap, lies = keep, 'cliff'
            if cap < y:
                y, cover = cap, lies
        return y, cover

    def _kept(self, f, x, z):
        """What opening `f` must leave standing at (x, z), in a room that is not a plain box: every wall but its own
        keeps its cliff and KEEP_TOP_M of level sand behind its lip, and from there a scarp of the cliffs' own angle
        falls to the opening's floor. So a window that looks at another wall (a room with wings, a recess) cuts no
        gap in that wall from behind. Out from a wall's own stretch, and round each of its corners."""
        lip_y, tan, room, wall = self.lip_y, self.tan, self.lip_out+KEEP_TOP_M, f['wall']
        keep = 0.
        n = len(self.edges)
        for k, (ax, az, _, ex, ez, L) in enumerate(self.edges):
            if k in wall or L == 0:
                continue
            u = ((x-ax)*ex+(z-az)*ez)/L
            if u < 0.:
                if (k-1) % n in wall:
                    continue        # the corner it shares with the opening's own wall
                d = math.hypot(x-ax, z-az)
            elif u <= 1.:
                d = ((x-ax)*ez-(z-az)*ex)/math.sqrt(L)*self.sign
            else:
                continue            # the next edge's corner
            if d > 0:
                got = lip_y-tan*max(0., d-room)
                if got > keep:
                    keep = got
        return keep

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


def wall_sides(model, cx, cz, sx, sz, yaw_deg):
    """The sides a box turns to a wall (its centre, footprint and yaw, in the land's metres): each side whose own
    outward direction agrees with that of the wall straight ahead of its middle (JOIN_FACING), with the gap between
    them, measured straight ahead of the side (`gap` at the nearest of its two ends and its middle, `far` at the
    furthest; negative where the box pokes through the wall), and `at(u, v)`: the room's point `u` along the side
    and `v` out from the box's middle (the side itself at `half_v`), with `to(x, z)` back again. The gap is to the
    wall the side is turned to: a thing by a corner is not against the second wall because its end is near the
    first. Where no wall lies ahead of one of the side's points (a scanned box in a corner, poking through the next
    wall: that end stands past the wall line), the side is read at its other points."""
    a, b = sx/2, sz/2
    ang = math.radians(yaw_deg)
    c, s = math.cos(ang), math.sin(ang)
    out = []
    for axis, sign in ((0, 1.), (0, -1.), (1, 1.), (1, -1.)):
        half_u, half_v = (b, a) if axis == 0 else (a, b)

        def at(u, v, axis=axis, sign=sign):
            lx, lz = (sign*v, u) if axis == 0 else (u, sign*v)
            return cx+c*lx+s*lz, cz-s*lx+c*lz

        def to(x, z, axis=axis, sign=sign):
            lx, lz = c*(x-cx)-s*(z-cz), s*(x-cx)+c*(z-cz)
            return (lz, sign*lx) if axis == 0 else (lx, sign*lz)
        side = [at(u, half_v) for u in (-half_u, 0., half_u)]
        ox, oz = (sign*c, -sign*s) if axis == 0 else (sign*s, sign*c)   # the side's own outward direction
        hits = [model.ahead(x, z, ox, oz) for x, z in side]
        found = [(t, k) for t, k in (hits[1], hits[0], hits[2]) if k is not None]
        if not found:
            continue
        _, _, _, ex, ez, L = model.edges[found[0][1]]       # (the wall ahead of its middle, where one lies there)
        facing = (ox*ez-oz*ex)/math.sqrt(L)*model.sign      # against the outward direction of the wall ahead
        if facing >= JOIN_FACING:
            gaps = [t for t, _ in found]
            out.append(dict(axis=axis, sign=sign, half_u=half_u, half_v=half_v, c=c, s=s, at=at, to=to, gap=min(gaps),
                            far=max(gaps), out=(ox, oz), mid=side[1], facing=facing))
    return out


def in_the_way(model, side):
    """The openings of the wall a side is turned to: the ones a buttress from that side would run into."""
    ox, oz = side['out']
    reach = max(side['far'], 0.)+1.
    return [f for f in model.openings if f['nx']*ox+f['nz']*oz >= JOIN_FACING
            and -reach <= (side['mid'][0]-f['x'])*f['nx']+(side['mid'][1]-f['z'])*f['nz'] <= 1.]


class Rim:
    """One room's rim: its record, the surface the record gives, the things joined into the cliff, and the rules the
    generator replays wherever it reads the land (the base grid and every detail block alike). `join_gap` (true
    metres; None: JOIN_GAP_M) is how near a wall a thing stands to be joined into it."""

    def __init__(self, room, inventory, objects, setup, W, tile_edge_out=TILE_EDGE_OUT_M, join_gap=None):
        if W == 1:
            raise ValueError('the rim is a magnified room\'s: at true size the land is the island it was')
        self.join_gap = JOIN_GAP_M if join_gap is None else join_gap
        if type(self.join_gap) not in (int, float) or not 0. <= self.join_gap <= 1.:
            raise ValueError('a thing joins a wall from 0 to 1 m off it (true size)')
        self.lifted_gap = LIFTED_JOIN_M
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
            for side in wall_sides(m, o['cx'], o['cz'], o['sx'], o['sz'], o['yaw']):
                if side['gap'] > (self.lifted_gap if hung else self.join_gap)*W+1e-9:
                    continue
                half_u, half_v, at = side['half_u'], side['half_v'], side['at']
                ways = in_the_way(m, side)
                if all(_into(ways, *at(u, half_v))[0] for u in (-half_u, -half_u/2, 0., half_u/2, half_u)):
                    continue    # it stands in a door: no wall is behind it
                ext = max(side['far'], 0.)+m.lip_out/side['facing']+.5
                corners = [at(u, v) for u in (-half_u, half_u) for v in (0., half_v+ext)]
                out.append(dict(side, k=k, o=o, ext=ext, lifted=hung, cap=None, crests={}, ways=ways,
                                x0=min(p[0] for p in corners), x1=max(p[0] for p in corners),
                                z0=min(p[1] for p in corners), z1=max(p[1] for p in corners)))
        return out

    def _crest(self, j, n):
        """(height, where) of a thing's crest on line `n` of a join (the lines lie CREST_STEP_M apart along its
        side): the tallest its own land stands between its middle and the side it turns to the wall, no taller than
        the thing itself (what rests on it does not climb the wall with it), and how far out from its middle it last
        stands that tall. The buttress runs on from there."""
        got = j['crests'].get(n)
        if got is None:
            o, at, half_u, half_v, step = j['o'], j['at'], j['half_u'], j['half_v'], CREST_STEP_M*self.W
            read = CREST_READ_M*self.W
            if j['cap'] is None:
                j['cap'] = o['top']+self.land(o['cx'], o['cz'])[1]
            u = max(-half_u, min(half_u, n*step))
            steps = max(2, int(math.ceil(half_v/read)))
            line = [(half_v*i/steps, self.land(*at(u, half_v*i/steps))[0]) for i in range(steps+1)]
            top = min(max(y for _, y in line), j['cap'])
            got = j['crests'][n] = (top, max(v for v, y in line if y >= top-CREST_TOL_M*self.W))
        return got

    def _fill(self, j, x, z):
        """The height a buttress stands at (x, z): the thing's own crest on that line, carried level to the wall and
        into the cliff; None off the buttress (and on the thing itself, up to its crest). In a door's width there
        is none (the bay keeps its floor, and no wall stands behind the thing there); in a window's it rises no
        higher than the sill (the notch stays open)."""
        lx, lz = local(j['o'], x, z)
        u, v = (lz, lx*j['sign']) if j['axis'] == 0 else (lx, lz*j['sign'])
        if abs(u) > j['half_u'] or v <= 0. or v > j['half_v']+j['ext']:
            return None
        door, sill = _into(j['ways'], x, z) if j['ways'] else (False, None)
        if door:
            return None
        step = CREST_STEP_M*self.W
        n = math.floor(u/step)
        t = u/step-n
        (top0, from0), (top1, from1) = self._crest(j, n), self._crest(j, n+1)
        if v <= (from0 if t < .5 else from1):
            return None
        top = max(top0, top1)
        return top if sill is None else min(top, sill)

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
            reach = j['half_v']+max(j['gap'], 0.)/2+.5*min(.05*self.W, j['ext'])
            # (Read on a line that is joined: not one that runs into a door.)
            u = next((u for u in (0., -j['half_u']/2, j['half_u']/2, -.9*j['half_u'], .9*j['half_u'])
                      if not _into(j['ways'], *j['at'](u, reach))[0]), 0.)
            x, z = j['at'](u, reach)
            rows.append(dict(id=j['o']['id'], kind=j['o']['kind'], lifted=j['lifted'], gap_m=round(j['gap'], 3)+0.,
                             probe_m=[round(x, 3)+0., round(z, 3)+0.], stands_m=round(height_at(x, z), 3)))
        return dict(edge='rim', rim=self.record, sun_gate=self.sun_gate(), buttresses=rows,
                    rim_join=dict(gap_m=self.join_gap, lifted_m=self.lifted_gap),
                    rim_shut=self.shut, rim_openings=self.opening_ids)

    def sun_gate(self):
        if self._sun is None:
            self._sun = sun_gate(self.record, self.setup['latitude_deg'], self.setup['neg_z_bearing_deg'])
        return self._sun


def _into(ways, x, z):
    """(a door, a sill) at (x, z) of the openings in a buttress's way: whether the point stands in a bay's width, and
    the floor of the notch whose width it stands in (None: none)."""
    sill = None
    for f in ways:
        if abs((x-f['x'])*f['tx']+(z-f['z'])*f['tz']) <= f['half']:
            if f['depth'] is not None:
                return True, None
            sill = f['floor'] if sill is None else min(sill, f['floor'])
    return False, sill


def plan(room, inventory, objects, setup, W, join_gap=None):
    return Rim(room, inventory, objects, setup, W, join_gap=join_gap)


# ---------------------------------------------------------------- the sun gate
def _rays(model, f, sun):
    """The rays of this sun that come through notch `f` into the room, of those tried: (reach, x, z, lands) for each,
    with (x, z) where it comes down to the floor's height, `reach` that point's distance from the notch's wall
    (metres) and `lands` whether it lies inside the wall line (a ray that does not land has met a cliff first).

    A ray through the notch crosses the sill's edge (where the cliff's face reaches the sill's height) above the
    sill, inside the notch at that height, and would have met the unbroken cliff. Out from there it must clear the two
    scarps until it stands over the lip. A scarp leans back from its foot, so at a height the notch is free as far as
    the wider of two straight lines (the straight side's and the widening side's); a straight ray inside the wider of
    two lines at the sill's edge, where the two cross and where it tops the lip is inside it all the way. In, it
    passes the wall line over the floor, into the room. Only the rim's own notch is in a ray's way here: nothing that
    stands in the room, and in a room with wings no other wing's cliff (WORLD-17 casts those)."""
    dx, dy, dz = sun
    ca = dx*f['nx']+dz*f['nz']
    if ca <= 1e-3 or dy <= 0:
        return   # the sun stands behind this wall
    kt = (dx*f['tx']+dz*f['tz'])/ca     # sideways, per metre out
    ky = dy/ca                          # up, per metre out
    tan, lip_y, floor, lip_out = model.tan, model.lip_y, f['floor'], model.lip_out
    tan_f, cos_f, lean, flare_from = f['tan_flare'], f['cos_flare'], f['lean'], f['flare_from']
    a_s = floor/tan

    def free(a, y):
        """Half the notch's free width `a` metres out, at height `y`."""
        s = (y-floor)/tan
        return f['half']+max(s, (a-flare_from)*tan_f+s/cos_f)
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
        a_x = (flare_from-lean*((y_s-floor)/tan-a_s*k))/(1+lean*k)
        at = [(a_top, free(a_top, lip_y))]
        if a_s < a_x < a_top:
            at.append((a_x, free(a_x, y_s+(a_x-a_s)*ky)))
        a_land = -y0/ky
        for n in range(17):
            b_s = hw*(n-8)/8
            if any(abs(b_s+(a-a_s)*kt) > w for a, w in at):
                continue
            # It passes the wall line into the room (a ray that slants past the wall's end meets the next cliff).
            b_wall = b_s-a_s*kt
            if not model.inside(f['x']+f['tx']*b_wall-f['nx']*1e-3, f['z']+f['tz']*b_wall-f['nz']*1e-3):
                continue
            b_land = b_s+(a_land-a_s)*kt
            x = f['x']+f['nx']*a_land+f['tx']*b_land
            z = f['z']+f['nz']*a_land+f['tz']*b_land
            yield -a_land, x, z, model.inside(x, z)


def _beam(model, f, sun):
    """The rays of this sun that come through notch `f` and land on the floor, of those tried: each landing's
    distance from the notch's wall (metres) and where it is, as (reach, x, z)."""
    return ((r, x, z) for r, x, z, lands in _rays(model, f, sun) if lands)


def _lit(model, f, sun):
    """How far into the room this sun's beam through notch `f` reaches: its deepest landing's distance from the
    notch's wall (metres), or None when no ray comes through and lands on the floor."""
    return max((r for r, _, _ in _beam(model, f, sun)), default=None)


def _hours(times):
    """(first, last, how long) of the sweep's steps in `times` (apparent solar hours), or (None, None, 0)."""
    if not times:
        return None, None, 0.
    return round(times[0], 3), round(times[-1], 3), round(len(times)*SUN_STEP_MIN/60, 3)


def sun_gate(record, latitude_deg, neg_z_bearing_deg):
    """Each notch's sunlit hours through the year, on the 21st of each month, in apparent solar hours. Two things
    are listed side by side, so a reader can tell a notch the sun never enters from one whose sun meets a cliff:
    `through_from_h`, `through_to_h` and `through_h`, the hours between which a ray comes through the notch into the
    room at all, and `from_h`, `to_h` and `lit_h`, the hours one lands on the bare floor, with `reach_m`, how far in
    it lands at its deepest. `beam` says whether a ray lands on the floor on any of the days, `through` whether one
    comes in; a notch with no beam says which it is. `faces_deg` is the way the notch looks out in the room's frame
    (as `home_wind_deg` is); `faces_compass_deg` is the same as a compass bearing. `shade` says what is in a ray's
    way in this sweep: the notch alone."""
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
            came, lit = [], []
            for t, sun in suns[day]:
                rays = [(r, lands) for r, _, _, lands in _rays(model, f, sun)]
                if rays:
                    came.append(t)
                if any(lands for _, lands in rays):
                    lit.append((t, max(r for r, lands in rays if lands)))
            from_h, to_h, lit_h = _hours([t for t, _ in lit])
            through_from_h, through_to_h, through_h = _hours(came)
            days.append(dict(day=day, from_h=from_h, to_h=to_h, lit_h=lit_h,
                             reach_m=round(max(r for _, r in lit), 2) if lit else 0.,
                             through_from_h=through_from_h, through_to_h=through_to_h, through_h=through_h))
        beam = any(d['lit_h'] > 0 for d in days)
        through = any(d['through_h'] > 0 for d in days)
        faces = bearing_in_room(f['nx'], f['nz'])
        row = dict(opening=f['k'], faces_deg=round(faces, 1) % 360, faces_compass_deg=round((neg_z_bearing_deg+faces) % 360, 1) % 360,
                   beam=beam, through=through, days=days)
        if not through:
            row['note'] = 'no beam: no sun comes through this notch on any of the days'
        elif not beam:
            row['note'] = 'no beam: the sun comes through this notch and meets the cliffs, never the floor'
        notches.append(row)
    return dict(latitude_deg=latitude_deg, neg_z_bearing_deg=neg_z_bearing_deg, step_min=SUN_STEP_MIN,
                time='apparent solar', shade='the notch alone', notches=notches)


# ---------------------------------------------------------------- the rim check
LEVEL_TOL_M = .005   # level sand lies this near its height
EDGE_CLEAR_M = .3    # level sand is read this far from where it ends (the land's grid rounds an edge)
SAMPLE_M = .5        # the rim is read on this lattice, and along each wall at this step
CLOSED_TOL_M = .05   # a wall's crest may stand this far under what its record says
JOIN_LINES = 9       # a buttress is read along this many lines, from the thing's crest to the cliff
JOIN_HELD = .75      # and stands when this share of them hold (a line may run along a part's own edge)
JOIN_TOL_M = .1      # a line holds when the land along it stays this near the thing's edge
# What each kind of opening must be. The check's own word, not the generator's lists: a generator that shut every
# window would otherwise agree with itself.
MUST_OPEN = {'window': 'notch', 'door': 'bay', 'garage_door': 'bay', 'archway': 'bay', 'portal': 'bay', 'pet_door': 'bay'}


def rim_of(doc):
    return (doc.get('x_generator') or {}).get('rim')


def is_rim(doc):
    """Whether a package was made with the rim: it says so (`edge`), or holds a rim record. (One that says so and
    has lost its record is still the rim check's to fail.)"""
    gen = doc.get('x_generator') or {}
    return gen.get('edge') == 'rim' or gen.get('rim') is not None


def _the_rooms(model, room, W):
    """The record against the room it is the rim of (rows, each with its `ok`): the wall line is the floor's
    outline; the lip stands at the walls' height, behind a face of FACE_DEG or steeper; every window of the room has
    its notch and every door its bay, where it stands on the wall line, as wide, its floor at its foot, unless it is
    shut for a reason that holds (off the wall line, its foot over the lip, the record full; a kind that needs a
    roof); and the record holds no opening the room lacks."""
    rec = model.record
    rows = {}
    _, hi, _ = room_shape(room)
    rows['walls_height'] = dict(room_m=round(float(hi[1]), 3), lip_m=model.lip_y, ok=abs(model.lip_y-float(hi[1])) <= 2e-3)
    face = math.degrees(math.atan(model.tan))
    rows['face'] = dict(deg=round(face, 2), ok=face >= FACE_DEG-.05)
    try:
        line = sorted(map(tuple, outline(room)))
    except ValueError:
        line = None
    says = sorted((p[0], p[1]) for p in rec['outline_m'])
    rows['wall_line'] = dict(points=len(says), ok=line is not None and len(line) == len(says) and all(
        abs(a[0]-b[0]) <= 2e-3 and abs(a[1]-b[1]) <= 2e-3 for a, b in zip(line, says)))
    used, shut, missing = set(), [], []
    full = len(rec['openings']) >= MAX_OPENINGS
    openings = sorted(room['shell'].get('openings', []), key=lambda o: o['id'])
    for o in openings:
        x, y, z = o['center_m']
        width, tall = o['size_m']
        floor = max(0., y-tall/2)
        must = MUST_OPEN.get(o['kind'])
        fx, fz, off = model.foot(x, z)
        hit = next((k for k, r in enumerate(rec['openings']) if k not in used and r['kind'] == must
                    and math.hypot(r['centre_m'][0]-fx, r['centre_m'][1]-fz) <= .05
                    and abs(r['width_m']-width) <= 2e-3 and abs(r['floor_y_m']-floor) <= 2e-3), None)
        if hit is not None:
            used.add(hit)
            continue
        why = 'a %s needs a roof' % o['kind'] if must is None else 'not on the wall line' if off > ON_WALL_M*W else \
            'its foot is over the lip' if floor > model.lip_y-1. else 'more openings than the record holds' if full else None
        (shut if why else missing).append(dict(id=o['id'], kind=o['kind'], why=why or 'no %s in the record' % must))
    extra = [k for k in range(len(rec['openings'])) if k not in used]
    rows['openings'] = dict(of_the_room=len(openings), notches=sum(r['kind'] == 'notch' for r in rec['openings']),
                            bays=sum(r['kind'] == 'bay' for r in rec['openings']), shut=shut, missing=missing,
                            not_the_rooms=extra, ok=not missing and not extra)
    return rows


def _against(model, inventory, W, gap, lifted):
    """The things of the room's inventory that stand against a wall, read from the inventory's own boxes and not
    from the generator's plan: (entry, side, hung) for each side a thing on the floor turns to a wall within `gap`
    true metres of it, or a lifted thing nothing holds within `lifted`. What rests on another thing stands on it,
    not against the wall."""
    ids = {e['id'] for e in inventory['objects']}
    out = []
    for e in inventory['objects']:
        support = e['placement']['support']
        held = support.get('target_id') in ids
        if support['kind'] == 'object' and held:
            continue
        hung = support['kind'] != 'floor' and not held
        b = e['box']
        for side in wall_sides(model, b['centre_m'][0], b['centre_m'][2], b['size_m'][0], b['size_m'][2], float(b['yaw_deg'])):
            if side['gap'] <= (lifted if hung else gap)*W+1e-9:
                out.append((e, side, hung))
    return out


def _flights(gen, pad):
    """The ground of every flight the generator cut or built (x_generator.fractals.routes: a ladder's, a step's, the
    summit's corridor and its stair), as rectangles (x0, x1, z0, z1) with `pad` round them (a flight runs along an
    axis). A flight is the island's gate, not the thing it is cut into: a buttress is read without it, as a
    landform's footprint is (checks.stair_rects)."""
    out = []
    for row in (gen.get('fractals') or {}).get('routes', ()):
        (ox, oz), (di, dj) = row['origin_m'], row['direction']
        length, half = row['length_m'], row['half_width_m']
        xs = (ox, ox+di*length) if di else (ox-half, ox+half)
        zs = (oz, oz+dj*length) if dj else (oz-half, oz+half)
        out.append((min(xs)-pad, max(xs)+pad, min(zs)-pad, max(zs)+pad))
    return out


def _joined(height, model, side, top, W, flights=(), others=(), cell=0.):
    """Whether the land joins a thing's side to the cliff, read on the land as built: on JOIN_LINES lines across the
    side, from the thing's crest (the tallest the land stands between its middle and the side, no taller than the
    thing's own top `top`; in a window, no higher than its sill) straight out to the wall and on into the cliff
    until the rim's own face stands as high, the land never falls under that crest. The ground of a flight
    (`flights`: rectangles) is not read; a line that runs into a door has no wall to join, and one within
    EDGE_CLEAR_M of an opening's side is not read. `others` are the thing's other sides that stand against a wall
    (a thing in a corner): a line whose crest would be read in the half of the thing another side's buttress fills
    is left to that side, for there the land is that buttress's and not the thing's own. `cell` is the land's grid:
    its triangles carry a height a cell sideways, so where a thing's top steps a line reads the taller part beside
    it, and the crest a line must hold is the lowest of those read on it and a cell to either side. And a line holds
    no more than the cliff it runs into: beside a notch the cliff's flank leans back from the notch's floor and may
    never stand as high as the thing, and there the land must hold what that flank holds at its tallest on the line
    (in a window's own width, the sill). Returns (lines read, lines that hold, the tallest crest read, the lowest
    the land stands along that line)."""
    half_u, half_v, at = side['half_u'], side['half_v'], side['at']
    trim = min(.3*half_u, .08*W)    # clear of the side's two ends, where the thing's own corners are rounded
    step = CREST_READ_M*W
    reach = half_v+max(side['far'], 0.)+model.lip_out/side['facing']+.5
    ways = in_the_way(model, side)
    steps = max(2, int(math.ceil(half_v/step)))

    def land(x, z):
        if any(x0 <= x <= x1 and z0 <= z <= z1 for x0, x1, z0, z1 in flights):
            return None
        y = height(x, z)
        return y if y is not None and y == y else -math.inf
    read = held = 0
    tallest = (-math.inf, 0.)
    for i in range(JOIN_LINES):
        u = (2*i/(JOIN_LINES-1)-1)*(half_u-trim)
        v = half_v
        while v < reach and model.signed(*at(u, v)) <= 0:
            v += step
        x, z = at(u, v)     # where the line meets the wall
        skip, sill = False, None
        for f in ways:
            b = abs((x-f['x'])*f['tx']+(z-f['z'])*f['tz'])-f['half']
            if abs(b) < EDGE_CLEAR_M or (b < 0 and f['depth'] is not None):
                skip = True
            elif b < 0:
                sill = f['floor'] if sill is None else min(sill, f['floor'])
        if not skip:
            for o in others:
                uo, vo = o['to'](*at(u, half_v/2))
                if vo > 0 and abs(uo) <= o['half_u']:
                    skip = True
        if skip:
            continue
        line = [(v, y) for v, y in ((half_v*k/steps, land(*at(u, half_v*k/steps))) for k in range(steps+1)) if y is not None]
        if not line:
            continue
        want = min(max(y for _, y in line), top)
        if cell > 0:
            for beside in (u-cell, u+cell):
                if abs(beside) <= half_u:
                    there = [y for y in (land(*at(beside, half_v*k/steps)) for k in range(steps+1)) if y is not None]
                    if there:
                        want = min(want, max(there))
        if sill is not None:
            want = min(want, sill)
        peak, v = -math.inf, half_v
        while v < reach:
            x, z = at(u, v)
            out = model.signed(x, z)
            if out > 0:
                peak = max(peak, model.above(x, z, out)[0])
            v += step
        if peak > -math.inf:
            want = min(want, peak)
        low, v = math.inf, max(v for v, y in line if y >= want-JOIN_TOL_M)
        while v < reach:
            x, z = at(u, v)
            out = model.signed(x, z)
            if out > 0 and model.above(x, z, out)[0] >= want-JOIN_TOL_M:
                break
            y = land(x, z)
            if y is not None:
                low = min(low, y)
            v += step
        low = min(low, want)
        read += 1
        held += low >= want-JOIN_TOL_M
        if want > tallest[0]:
            tallest = (want, low)
    return read, held, tallest[0], tallest[1]


def rim_checks(doc, meshes, terrain, room, inventory):
    """The rim, proved on the written package against the room and its inventory (both magnified as the package
    is): the land as its meshes hold it, and the record only where the room bears it out.

    - the record is the table's shape (`validate`), and the package holds no sea;
    - the record is the room's (`_the_rooms`): the wall line the floor's outline, the lip at the walls' height,
      each window a notch and each door a bay where it stands;
    - the land's tiles reach the tile edge all round, and the player's spawn lies inside the wall line, under the lip;
    - level sand: wherever the record says the land is level (past the lip, and on each notch's floor past its
      sill's edge), it lies at that height, so the lip stands at the walls' height all round but for the openings and
      each notch runs out level to the tile edge onto its low desert;
    - closed walls: out from every stretch of wall the land climbs to the lip, or through one of that wall's own
      openings as high as the record says, so nothing but a wall's own openings breaks its cliff;
    - buttresses: every thing the inventory stands against a wall (within the gap the package says it was made
      with, `x_generator.rim_join`) is joined to it on the land as built (`_joined`: read without the flights cut
      into it), and the package lists those things and no other; `left_free` names the things a narrower gap than
      JOIN_GAP_M left standing free, `at_a_door` the ones with no wall behind them;
    - the sun gate listed is the one the record gives (`sun_gate`).
    """
    gen = doc.get('x_generator') or {}
    rows = {}
    try:
        model = Model(rim_of(doc))
        join = gen.get('rim_join')
        if not isinstance(join, dict) or set(join) != {'gap_m', 'lifted_m'}:
            raise ValueError('rim join: fields')
        gap, lifted = _number(join['gap_m'], 0., 1., 'join gap'), _number(join['lifted_m'], -1., 5., 'lifted join')
        W = _number(gen.get('world_scale'), 1., 1000., 'world scale')
        cell = _number(gen.get('cell_m'), 1e-3, 10., 'cell')
        listed = gen.get('buttresses')
        if not isinstance(listed, list) or not all(
                isinstance(b, dict) and isinstance(b.get('id'), str) and isinstance(b.get('probe_m'), list)
                and len(b['probe_m']) == 2 and all(type(v) in (int, float) and abs(v) <= 1e4 for v in b['probe_m'])
                and type(b.get('stands_m')) in (int, float) and abs(b['stands_m']) <= 1e4 for b in listed):
            raise ValueError('rim buttresses: fields')
        flights = _flights(gen, .05*W)
    except (ValueError, TypeError, KeyError, OverflowError) as bad:
        return False, dict(error=str(bad) or 'no rim in the package (x_generator.rim)')
    rec = model.record
    ok = True
    rows['no_sea'] = 'sea' not in gen and not any(r['mesh'] == 'sea' for r in doc['water']) and 'sea' not in meshes
    ok &= rows['no_sea']
    # The record is this room's.
    rows.update(_the_rooms(model, room, W))
    ok &= all(rows[k]['ok'] for k in ('walls_height', 'face', 'wall_line', 'openings'))
    # The land's tiles end at the tile edge.
    xs = [v[0] for p in meshes.get('land', ()) for v in p['positions']]
    zs = [v[2] for p in meshes.get('land', ()) for v in p['positions']]
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
            gap_y = abs(terrain.height(x, z)-y)
            row['points'] += 1
            if not gap_y <= LEVEL_TOL_M:      # (no land there at all reads as off, too)
                row['off'] += 1
                row['worst_m'] = max(row['worst_m'], round(gap_y, 4) if gap_y == gap_y else model.lip_y)
    rows['lip'] = dict(height_m=model.lip_y, **top)
    ok &= top['points'] > 0 and not top['off']
    rows['notches'] = []
    for f in model.openings:
        if f['depth'] is None:
            row = floors[f['k']]
            good = row['points'] > 0 and not row['off'] and row['reach_m'] >= tile-SAMPLE_M-e-1e-6
            rows['notches'].append(dict(opening=f['k'], floor_m=f['floor'], open_to_the_tile_edge=good, **row))
            ok &= good
    # Closed walls: out from each stretch of wall, the land's crest against what the record gives with that wall's
    # own openings alone (another wall's notch must not cut it from behind). Where that crest is the level lip the
    # land stands at it; where it is a face (a ray that leaves through an opening's side) the land's grid rounds it
    # by a cell of the face's rise.
    walls = dict(points=0, low=0, worst_m=0., through_openings=0)
    own = {}
    for k, (ax, az, _, ex, ez, L) in enumerate(model.edges):
        length = math.sqrt(L)
        if length < 2*SAMPLE_M:
            continue
        mine = tuple(f['k'] for f in model.openings if k in f['wall'])
        if mine not in own:
            own[mine] = Model(dict(rec, openings=[rec['openings'][n] for n in mine]))
        wall = own[mine]
        tx, tz = ex/length, ez/length
        nx, nz = tz*model.sign, -tx*model.sign
        for along in range(1, int(length/SAMPLE_M)):
            px, pz = ax+tx*along*SAMPLE_M, az+tz*along*SAMPLE_M
            says = got = 0.
            for n in range(1, int((tile-e)/(SAMPLE_M/2))+1):
                x, z = px+nx*n*SAMPLE_M/2, pz+nz*n*SAMPLE_M/2
                if model.inside(x, z):
                    break   # round a corner and back in the room: this wall's own stretch has ended
                says = max(says, wall.above(x, z, wall.out(x, z))[0])
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
    # Buttresses: what the inventory stands against a wall, joined on the land as built.
    against = _against(model, inventory, W, gap, lifted)
    joined, at_a_door = [], []
    tops = [e['box']['centre_m'][1]+e['box']['size_m'][1]/2 for e, _, _ in against]
    # (A side with no wall behind it has no buttress: it leaves none of a thing's lines to another side.)
    walled = [_joined(terrain.height, model, side, top, W, flights, (), cell)[0] > 0 for (_, side, _), top in zip(against, tops)]
    for (entry, side, hung), top in zip(against, tops):
        others = [s for (e, s, _), has in zip(against, walled) if has and e is entry and s is not side]
        read, held, edge, stands = _joined(terrain.height, model, side, top, W, flights, others, cell)
        if not read:
            at_a_door.append(entry['id'])
            continue
        joined.append(dict(id=entry['id'], kind=entry['kind'], lifted=hung, gap_m=round(side['gap'], 3)+0.,
                           faces_deg=round(bearing_in_room(*side['out']), 1) % 360, lines=read, held=held,
                           edge_m=round(edge, 3), stands_m=round(stands, 3), ok=held >= JOIN_HELD*read))
    names = [b['id'] for b in listed]
    fallen = sorted({r['id'] for r in joined if not r['ok']} |
                    {b['id'] for b in listed if not terrain.height(*b['probe_m']) >= b['stands_m']-CLOSED_TOL_M})
    wider = _against(model, inventory, W, max(gap, JOIN_GAP_M), max(lifted, LIFTED_JOIN_M))
    rows['buttresses'] = dict(rule=dict(gap_m=gap, lifted_m=lifted), listed=len(names), joined=joined, fallen=fallen,
                              unlisted=sorted({r['id'] for r in joined}-set(names)),
                              unfounded=sorted(set(names)-{entry['id'] for entry, _, _ in against}),
                              at_a_door=sorted(set(at_a_door)-{r['id'] for r in joined}),
                              left_free=sorted({entry['id'] for entry, _, _ in wider}-{entry['id'] for entry, _, _ in against}))
    ok &= not fallen and not rows['buttresses']['unlisted'] and not rows['buttresses']['unfounded']
    # The sun gate.
    setup = doc['setup']
    gate = sun_gate(rec, setup['latitude_deg'], setup['neg_z_bearing_deg'])
    rows['sun_gate_ok'] = gen.get('sun_gate') == gate
    ok &= rows['sun_gate_ok']
    rows['sun_gate'] = gate['notches']
    return bool(ok), rows


def run(package, room_dir):
    """Every check of checks.run on a package made with the rim, the rim check standing where the sea check stands
    (the result's `rim_ok` and `rim`; a package with a sea is checks.run's own). It is checks.run itself that runs,
    with the rim check handed to it in the sea check's place, so a check added there runs on a rim package too."""
    from pipeline.landscape.harness.common import load_json
    from . import checks, scale as world
    sea_checks, rims = checks.sea_checks, []

    def edge_checks(doc, meshes, terrain, room, floods):
        if not is_rim(doc):
            return sea_checks(doc, meshes, terrain, room, floods)
        rims.append(True)
        inventory = world.magnify_inventory(load_json(Path(room_dir)/'inventory.json'), checks.world_scale(doc))
        return rim_checks(doc, meshes, terrain, room, inventory)
    checks.sea_checks = edge_checks
    try:
        result = checks.run(package, room_dir)
    finally:
        checks.sea_checks = sea_checks
    if rims:
        result['rim_ok'], result['rim'] = result.pop('sea_ok'), result.pop('sea')
    return result


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
    if 'rim' not in result:
        if args.json:
            Path(args.json).write_text(json.dumps(result, indent=2, sort_keys=True)+'\n', encoding='utf-8', newline='\n')
        raise SystemExit('this package has a sea: run pipeline.landscape.generator.checks')
    report(result, args.json)


def report(result, json_path=None):
    """Print what the checks found on a package made with the rim (`run`'s result), the rim's rows where the sea's
    stood, write the whole result to `json_path` if one is given, and leave: 0 when every check passes. (Apart from
    `main` so that the checks' own command can hand a rim package here.)"""
    if json_path:
        Path(json_path).write_text(json.dumps(result, indent=2, sort_keys=True)+'\n', encoding='utf-8', newline='\n')
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
