"""Geology: every inventory box and the shell become uplift, then weathering.

Volume is the foundation; a confident kind chooses the landform's character;
colour picks the rock family (warm sandstone or cool slate) and its tint.
"""
import heapq
import math

from pipeline.landscape.harness.views import overview_camera
from .field import Noise, bell, clamp, lerp, rr_dist, smax, smooth
from . import parts, scale as world

# On a magnified room every box is raised as its own parts (parts.py: a chair a seat under a back wall, a desk a flat
# top with sheer sides), each under a hard cap the weather spares. False: the melted forms below there too (the land
# before Run 4's WORLD-1). At true size the land is the melted forms either way: its bytes are as they were.
BLOCKS = True
CONFIDENT = .5
# Kind -> landform. Unknown or low-confidence kinds fall back to proportions.
FORMS = {
    'couch': 'ridge', 'sofa': 'ridge', 'bed': 'mesa', 'bean bag': 'dome',
    'bicycle': 'fin', 'bin': 'stack', 'basket': 'stack', 'cardboard box': 'mesa',
    'box': 'mesa', 'desk': 'mesa', 'table': 'mesa', 'storage tote': 'mesa',
    'easel': 'spire', 'step ladder': 'spire', 'lamp': 'spire', 'french press': 'spire',
    'jar': 'cap', 'mug': 'cap', 'cup': 'cap', 'vase': 'spire', 'bottle': 'spire',
    'laptop': 'slab', 'book': 'slab', 'board game': 'slab', 'monitor': 'crag',
    'office chair': 'knoll', 'chair': 'knoll', 'shelving unit': 'crag',
    'bookcase': 'crag', 'wardrobe': 'crag', 'cabinet': 'mesa', 'printer': 'mesa',
    'chest of drawers': 'mesa', 'suitcase': 'mesa', 'paint can': 'stack', 'speaker': 'stack',
    'kettle': 'cap', 'plant': 'knoll',
}
SOFT = {'dome', 'ridge', 'knoll'}


def srgb_lin(c):
    c /= 255.
    return c/12.92 if c <= .04045 else ((c+.055)/1.055)**2.4


def hex_lin(h):
    return tuple(srgb_lin(int(h[i:i+2], 16)) for i in (1, 3, 5))


def geology_tint(colour, strength=.5):
    """Hue of the source colour drawn into the shared palette (never brighter)."""
    lin = hex_lin(colour)
    m = max(lin) or 1.
    return tuple(round(1-strength*(1-c/m), 4) for c in lin)


def proportion_form(o):
    w, d = max(o['sx'], o['sz']), min(o['sx'], o['sz'])
    if o['sy'] >= 1.8*w:
        return 'spire'
    if w >= 2.2*d:
        return 'crag' if o['sy'] > 1.2 else 'ridge'
    if o['sy'] < .45*d and o['parent']:
        return 'slab'
    if o['sy'] <= .8*w:
        return 'mesa'
    return 'stack' if w < .5 else 'dome'


def parse_objects(inventory):
    objects = []
    for e in inventory['objects']:
        b = e['box']
        sx, sy, sz = b['size_m']
        cx, cy, cz = b['centre_m']
        colours = e.get('colours') or [{'hex': '#8c8c8c', 'share': 1.}]
        colour = sorted(colours, key=lambda c: (-c['share'], c['hex']))[0]['hex']
        support = e['placement']['support']
        o = dict(id=e['id'], kind=e['kind'], confidence=float(e.get('confidence', 0)),
                 cx=cx, cz=cz, base=cy-sy/2, top=cy+sy/2, sx=sx, sy=sy, sz=sz,
                 yaw=float(b['yaw_deg']), colour=colour,
                 parent=support.get('target_id') if support['kind'] == 'object' else None)
        confident = o['confidence'] >= CONFIDENT and o['kind'] in FORMS
        o['form'] = FORMS[o['kind']] if confident else proportion_form(o)
        o['form_basis'] = 'kind' if confident else 'proportions'
        lin = hex_lin(colour)
        o['rock_role'] = 'cliff' if lin[0] >= lin[2] else 'rock'
        o['tint'] = geology_tint(colour, .55)
        # Confidence limits how far a form may spread over the land around it.
        o['reach_k'] = lerp(.45, 1., clamp((o['confidence']-.2)/.5))
        o['phase'] = (len(objects)*2.39996) % math.tau
        objects.append(o)
    ids = {o['id'] for o in objects}
    for o in objects:
        if o['parent'] not in ids:
            o['parent'] = None
    # A support whose load was confidently seen is itself confirmed: a desk
    # under a sure laptop and monitor is surely there, whatever its own label.
    for o in objects:
        kids = [c['confidence'] for c in objects if c['parent'] == o['id']]
        o['support_conf'] = max([o['confidence']]+[.9*k for k in kids])
    return objects


def local(o, x, z):
    a = math.radians(o['yaw'])
    c, s = math.cos(a), math.sin(a)
    dx, dz = x-o['cx'], z-o['cz']
    return c*dx-s*dz, s*dx+c*dz


def mesa(lx, lz, a, b, H, cliff=.07, talus=.32, spread=None, dome=.05):
    d = rr_dist(lx, lz, a, b, min(.12, .4*min(a, b)))
    if spread is None:
        spread = .55*H+.12
    top = H*(1-dome)
    if d <= 0:
        return top+(H-top)*smooth(-d/.15)
    if d < cliff:
        return top+(talus*H-top)*smooth(d/cliff)
    t = (d-cliff)/spread
    return 0. if t >= 1 else talus*H*(1-t)**2


def dome(lx, lz, a, b, H, spread):
    rho = math.hypot(lx/(a+spread), lz/(b+spread))
    return H*bell(rho)


def hill(o, lx, lz, a, b, H, spread):
    """A weathered hill, not a dome: a summit ridge along the long axis, spurs
    with re-entrant gullies between them, and a long gentle tail toward open
    ground (the lee) against a shorter, steeper scarp on the other side."""
    ang = math.atan2(lz, lx)
    tail = o.get('tail', 0.)
    ph = o.get('phase', 0.)
    asym = 1+.5*math.cos(ang-tail)
    lobes = 1+.16*math.cos(3*ang+ph)+.08*math.cos(5*ang+2.1*ph)
    long_x = a >= b
    core = .45*(a if long_x else b)
    ex = max(0., abs(lx)-core) if long_x else lx
    ez = lz if long_x else max(0., abs(lz)-core)
    ra = (a-(core if long_x else 0)+spread*asym)*lobes
    rb = (b-(0 if long_x else core)+spread*asym)*lobes
    rho = math.hypot(ex/ra, ez/rb)
    if rho >= 1:
        return 0.
    # Convex shoulders, a concave foot, and a bench part-way down the scarp.
    body = bell(rho)**1.15
    bench = .07*max(0., 1-abs(rho-.55)/.12)*(.5-.5*math.cos(ang-tail))
    return H*(body+bench)


MIN_HALF = .09   # a landform narrower than two grid cells would alias away
CAPROCK_M = .05  # a tall needle's standable summit (radius)


def profile(o, lx, lz, noise):
    """Height above the object's base for a landform of the object's form."""
    a, b, H = o['sx']/2, o['sz']/2, o['sy']
    if o['parent'] is None:
        a, b = max(a, MIN_HALF), max(b, MIN_HALF)
    f = o['form']
    k = o.get('reach_k', 1.)
    if f == 'mesa':
        soft = o['form_basis'] == 'proportions'
        return mesa(lx, lz, a, b, H, cliff=.14 if soft else .07, talus=.42 if soft else .32,
                    dome=.10 if soft else .04, spread=(.55*H+.12)*k)
    if f == 'slab':
        return mesa(lx, lz, a, b, H, cliff=.02, talus=.5, spread=.04, dome=0)
    if f == 'dome':
        return hill(o, lx, lz, a, b, H, (.45+.25*H)*k)
    if f == 'cap':
        return dome(lx, lz, a, b, H, .06)
    if f == 'ridge':
        # Couch: a crest along the back, a bench terrace in front (local -Z).
        n = noise(lx*3.1+o['cx'], o['cz']*2.3)
        crest_h = H*(.93+.07*n)
        cz = .42*b
        ex = max(0., abs(lx)-a*.82)
        dz = lz-cz
        w = (.55*b+.42) if dz < 0 else (.3*b+.35)
        crest = crest_h*bell(math.hypot(ex*1.4, dz)/w)
        seat = mesa(lx, lz+.2*b, a*.94, b*.62, .58*H, cliff=.09, talus=.5, spread=.45, dome=.03)
        arms = max(dome(lx-(a-.13), lz+.1*b, .1, b*.6, .8*H, .12),
                   dome(lx+(a-.13), lz+.1*b, .1, b*.6, .8*H, .12))
        return smax(smax(crest, seat, .08), arms, .06)
    child = o['parent'] is not None
    if f == 'spire':
        r = math.hypot(lx/a, lz/b)*min(a, b)
        r0 = .72*min(a, b)
        # A hoodoo, not a point: a needle taller than two bodies keeps a
        # caprock summit a climber can stand on (Lane P's climbs).
        rs = CAPROCK_M if H > .2 else 0.
        r0 = max(r0, rs+.04)
        x = max(0., r-rs)/(r0-rs)
        needle = H*(1-x*x)**.55 if x < 1 else 0.
        apron = .26*H*max(0., 1-r/(r0+(.05 if child else .22+.25*H)))**2
        return max(needle, apron)
    if f == 'crag':
        ex = max(0., abs(lx)-a*.62)
        w = b+(.03+.06*H if child else .22+.22*H)
        t = math.hypot(ex*1.3, lz)/w
        peak = H*bell(t)**.75 if t < 1 else 0.
        apron = 0. if child else .3*H*max(0., 1-math.hypot(ex, lz)/(w+.35))**2
        return max(peak, apron)
    if f == 'fin':
        wheel = max(dome(lx-.58*a, lz, .2, .2, .7*H, .22), dome(lx+.58*a, lz, .2, .2, .7*H, .22))
        ex = max(0., abs(lx)-a*.75)
        serr = .88+.12*noise(lx*9+o['cx'], o['cz'])
        frame = H*serr*bell(math.hypot(ex*1.5, lz)/(b+.25))
        return smax(wheel, frame, .06)
    if f == 'stack':
        r = math.hypot(lx/a, lz/b)
        tor = H*max(0., 1-r**4)**.5 if r < 1 else 0.
        apron = .35*H*max(0., 1-(r-1)*min(a, b)/.25)**2 if r >= 1 else .35*H
        return max(tor, apron)
    if f == 'knoll':
        low = hill(o, lx, lz, a, b, .5*H, .4*k)
        r = math.hypot(lx/(a*.55), (lz-.55*b)/(b*.4))
        tor = H*.9*max(0., 1-r**3)**.5 if r < 1 else 0.
        return smax(low, tor, .08)
    raise ValueError(f)


def reach(o):
    H = o['sy']
    return max(o['sx'], o['sz'])/2+.7+.6*H+.3


def stronger_neighbours(o, objects):
    """Confident neighbours this form may not swallow: a form spreads over a
    neighbour's footprint only as far as its own confidence allows."""
    out = []
    for n in objects:
        if n is o or n['parent'] is not None or o['parent'] == n['id'] or n['parent'] == o['id']:
            continue
        # Any neighbour about as sure as this form (or surer) keeps its own
        # footprint: two confident forms meet as a terrace against a crag.
        if n['support_conf'] < o['support_conf']-.15:
            continue
        if math.hypot(n['cx']-o['cx'], n['cz']-o['cz']) > reach(o)+max(n['sx'], n['sz']):
            continue
        out.append(n)
    return out


def territory_cap(o, others, x, z):
    """Highest this (less confident) form may stand at (x, z): a little above a
    stronger neighbour's own top inside its footprint, rising with distance."""
    cap = math.inf
    for n in others:
        lx, lz = local(n, x, z)
        d = rr_dist(lx, lz, n['sx']/2, n['sz']/2, .05)
        if d > .6:
            continue
        gap = max(0., n['support_conf']-o['support_conf'])
        cap = min(cap, n['top']+.04+max(0., d)*(2.2-1.5*gap))
    return cap


class Heights(list):
    """The land's heights, with what `raise_blocks` knows of them for `weather`: `cap`, per vertex, 1 where the
    weather spares the land (a block's flat cap bed, every block's face and the plain at its foot), 0 on soft tops and
    the open plain."""
    cap = None


def raise_blocks(grid, objects, base, owner):
    """Each object raised as its own parts (parts.py) on the plain, inside its true box; record who formed each
    vertex. Returns Heights (their `cap` says what the weather spares).

    A thing on the floor stands on the plain under its middle, level, so its top is as far over its own ground as the
    box is tall; a thing that rests on another stands on that one's top, so a laptop still shows on its desk. Nothing
    spreads past its box but the face itself, one cell wide. `grid` and `objects` are the room at true size, as
    `uplift` is handed them at every world scale (generate.py shapes the landforms there and magnifies them)."""
    h = Heights(base)
    cap = [0.]*grid.n
    index = {o['id']: k for k, o in enumerate(objects)}
    order = sorted(objects, key=lambda o: (o['parent'] is not None, o['base'], o['id']))
    stand = {}    # id: how far over its box's own base an object's land stands (the plain under a floor thing's middle)
    cell = grid.cell
    nx = grid.nx
    raised = set()
    for o in order:
        k = index[o['id']]
        parent = objects[index[o['parent']]] if o['parent'] else None
        stand[o['id']] = grid.sample(base, o['cx'], o['cz']) if parent is None else stand[parent['id']]
        level = o['base']+stand[o['id']]
        blocks = parts.blocks(o, cell)
        least = parts.ROUND_GRAINS*cell    # a thing thinner than the grain stands is as wide as that
        R = math.hypot(max(o['sx'], least), max(o['sz'], least))/2+cell
        rx, rz = grid.span(o['cx']-R, o['cx']+R, o['cz']-R, o['cz']+R)
        phase = o.get('phase', 0.)
        for j in rz:
            z = grid.zs[j]
            for i in rx:
                x = grid.xs[i]
                lx, lz = local(o, x, z)
                p, hard = parts.surface(blocks, lx, lz, cell, phase)
                if p <= 0:
                    continue
                if parent is not None:
                    # Only over its support: a box scanned past its support's edge raises no pillar beside it.
                    plx, plz = local(parent, x, z)
                    if rr_dist(plx, plz, parent['sx']/2, parent['sz']/2, .05) > -.01:
                        continue
                q = j*nx+i
                if level+p > h[q]+1e-9:
                    h[q] = level+p
                    cap[q] = hard
                    owner[q] = k
                    raised.add(q)
    # The plain at a face's foot is spared too: the weather reads a face's steepness there and would cut a moat along
    # every cliff (1.2 m deep on the ten-times garage). A cliff meets level ground, where a climb's foot is.
    for q in raised:
        i, j = q % nx, q//nx
        for dj in (-1, 0, 1):
            for di in (-1, 0, 1):
                ii, jj = i+di, j+dj
                if 0 <= ii < nx and 0 <= jj < grid.nz and jj*nx+ii not in raised:
                    cap[jj*nx+ii] = 1.
    h.cap = cap
    return h


def uplift(grid, objects, base, noise, owner):
    """Merge object landforms into the base plain; record who formed each vertex. On a magnified room each object
    is raised as its own parts instead (`raise_blocks`)."""
    if BLOCKS and world.W != 1:
        return raise_blocks(grid, objects, base, owner)
    h = base[:]
    contrib = [0.]*grid.n
    order = sorted(objects, key=lambda o: (o['parent'] is not None, o['base'], o['id']))
    index = {o['id']: k for k, o in enumerate(objects)}
    for o in order:
        R = reach(o)
        others = stronger_neighbours(o, objects) if o['parent'] is None else []
        rx, rz = grid.span(o['cx']-R, o['cx']+R, o['cz']-R, o['cz']+R)
        rugged = .06 if o['form'] in SOFT else .07
        warp = .05+.05*min(o['sy'], 1.)
        k = index[o['id']]
        parent = objects[index[o['parent']]] if o['parent'] else None
        for j in rz:
            z = grid.zs[j]
            for i in rx:
                x = grid.xs[i]
                lx, lz = local(o, x, z)
                if parent is None:
                    lx += warp*noise(x*2.4+k*3.3, z*2.4)
                    lz += warp*noise(x*2.4-7.1, z*2.4+k*2.9)
                p = profile(o, lx, lz, noise)
                if p <= 0:
                    continue
                # Rugged flanks, but summits weathered smooth: the top a
                # climber pulls over onto is rock to stand on, not a crown of
                # noise spikes (softened ridgelines).
                rel = p/max(o['sy'], 1e-6)
                crown = 1-smooth((rel-.75)/.2)
                p += rugged*o['sy']*noise.fbm(x*4.3+k*5.1, z*4.3-k*3.7, 3)*smooth(rel*2.5)*crown
                if o['form'] in ('dome', 'knoll'):
                    p += .12*o['sy']*noise.ridged(x*2.6-k, z*2.6+k, 3)*smooth(rel*3)*crown
                q = j*grid.nx+i
                if others and rr_dist(*local(o, x, z), o['sx']/2, o['sz']/2, .05) > 0:
                    # Outside its own box a form gives way to a sure neighbour;
                    # where two boxes overlap, both keep their height.
                    p = min(p, max(0., territory_cap(o, others, x, z)-base[q]))
                if parent is not None:
                    plx, plz = local(parent, x, z)
                    if rr_dist(plx, plz, parent['sx']/2, parent['sz']/2, .05) > -.01:
                        continue
                if o['parent'] is None:
                    land = h[q]-base[q]
                    merged = smax(land, p, .07)
                    value = base[q]+merged
                else:
                    value = o['base']+p
                if value > h[q]+1e-9:
                    h[q] = value
                if p > contrib[q]:
                    contrib[q] = p
                    owner[q] = k
    return h


def room_shape(room):
    lo, hi = room['bounds']['min_m'], room['bounds']['max_m']
    floors = [p['geometry']['points_m'] for p in room['shell']['parts'] if p['role'] == 'floor']
    return lo, hi, floors


def point_in_poly(x, z, poly):
    inside = False
    for a, b in zip(poly, poly[1:]+poly[:1]):
        if (a[2] > z) != (b[2] > z) and x < (b[0]-a[0])*(z-a[2])/(b[2]-a[2])+a[0]:
            inside = not inside
    return inside


def poly_dist(x, z, poly):
    best = math.inf
    for a, b in zip(poly, poly[1:]+poly[:1]):
        dx, dz = b[0]-a[0], b[2]-a[2]
        L = dx*dx+dz*dz
        t = 0 if L == 0 else clamp(((x-a[0])*dx+(z-a[2])*dz)/L)
        best = min(best, math.hypot(x-a[0]-t*dx, z-a[2]-t*dz))
    return best


FAR_Y = -.03   # just under the harness's shared far ground (-0.025): the land sinks into it


def _sd_rect(x, z, lo, hi):
    """Signed distance to the bounds rectangle with its nearest point and outward normal."""
    dx = max(lo[0]-x, x-hi[0])
    dz = max(lo[2]-z, z-hi[2])
    if dx <= 0 and dz <= 0:
        k = min([(x-lo[0], (lo[0], z), (-1., 0.)), (hi[0]-x, (hi[0], z), (1., 0.)),
                 (z-lo[2], (x, lo[2]), (0., -1.)), (hi[2]-z, (x, hi[2]), (0., 1.))])
        return -k[0], k[1][0], k[1][1], k[2][0], k[2][1]
    px, pz = clamp(x, lo[0], hi[0]), clamp(z, lo[2], hi[2])
    d = math.hypot(x-px, z-pz)
    return d, px, pz, (x-px)/d, (z-pz)/d


def _sd_poly(x, z, poly):
    best = None
    for a, b in zip(poly, poly[1:]+poly[:1]):
        dx, dz = b[0]-a[0], b[2]-a[2]
        L = dx*dx+dz*dz
        t = 0 if L == 0 else clamp(((x-a[0])*dx+(z-a[2])*dz)/L)
        px, pz = a[0]+t*dx, a[2]+t*dz
        d = math.hypot(x-px, z-pz)
        if best is None or d < best[0]:
            best = (d, px, pz)
    d, px, pz = best
    inside = point_in_poly(x, z, poly)
    if d < 1e-9:
        return 0., px, pz, 0., 0.
    ox, oz = (x-px)/d, (z-pz)/d
    if inside:
        return -d, px, pz, -ox, -oz
    return d, px, pz, ox, oz


def wall_frame_plain(room, x, z):
    """`wall_frame` written plainly (the reference its faster form is tested against)."""
    lo, hi, floors = room_shape(room)
    best = _sd_rect(x, z, lo, hi)
    if floors:
        cands = [_sd_poly(x, z, poly) for poly in floors]
        inside = [c for c in cands if c[0] <= 0]
        poly = max(inside, key=lambda c: c[0]) if inside else min(cands, key=lambda c: c[0])
        if poly[0] > best[0]:
            best = poly
    return best


_SHAPES = {}


def _shape(room):
    """The room's bounds and floor outlines, read once per room (edge by edge)."""
    got = _SHAPES.get(id(room))
    if got is None or got[0] is not room:
        lo, hi, floors = room_shape(room)
        polys = []
        for poly in floors:
            edges = []
            for a, b in zip(poly, poly[1:]+poly[:1]):
                dx, dz = b[0]-a[0], b[2]-a[2]
                edges.append((a[0], a[2], b[2], dx, dz, dx*dx+dz*dz, b[0]))
            polys.append(edges)
        if len(_SHAPES) > 16:
            _SHAPES.clear()
        got = _SHAPES[id(room)] = (room, (lo[0], lo[2], hi[0], hi[2], polys), _floor_holds_bounds(lo, hi, floors))
    return got[1]


def _floor_holds_bounds(lo, hi, floors):
    """Whether the walkable floor is just the bounds: no outline, or one
    convex outline holding the bounds a micrometre in from every edge. Then a
    point inside the bounds is nearer a bound than any outline's edge, and
    its distance inside the floor is its distance inside the bounds."""
    if not floors:
        return True
    if len(floors) != 1:
        return False
    poly = [(p[0], p[2]) for p in floors[0]]
    n = len(poly)
    turns = set()
    for k in range(n):
        (ax, az), (bx, bz), (cx, cz) = poly[k], poly[(k+1) % n], poly[(k+2) % n]
        cross = (bx-ax)*(cz-bz)-(bz-az)*(cx-bx)
        if abs(cross) > 1e-12:
            turns.add(cross > 0)
    if len(turns) != 1:
        return False
    sign = 1. if True in turns else -1.
    for (ax, az), (bx, bz) in zip(poly, poly[1:]+poly[:1]):
        L = math.hypot(bx-ax, bz-az)
        if L <= 0:
            continue
        for x, z in ((lo[0], lo[2]), (lo[0], hi[2]), (hi[0], lo[2]), (hi[0], hi[2])):
            if sign*((bx-ax)*(z-az)-(bz-az)*(x-ax))/L < 1e-6:
                return False
    return True


def wall_frame(room, x, z):
    """Signed distance to the walkable floor (bounds within the floor polygons;
    negative inside), the nearest wall point and that wall's outward normal.
    Works for any floor outline, concave ones included. The same arithmetic
    as `wall_frame_plain`, the room read once."""
    lo0, lo2, hi0, hi2, polys = _shape(room)
    a, b = lo0-x, x-hi0
    dx = b if b > a else a
    a, b = lo2-z, z-hi2
    dz = b if b > a else a
    if dx <= 0 and dz <= 0:
        k = min((x-lo0, (lo0, z), (-1., 0.)), (hi0-x, (hi0, z), (1., 0.)), (z-lo2, (x, lo2), (0., -1.)),
                (hi2-z, (x, hi2), (0., 1.)))
        best = (-k[0], k[1][0], k[1][1], k[2][0], k[2][1])
    else:
        px = lo0 if x < lo0 else hi0 if x > hi0 else x
        pz = lo2 if z < lo2 else hi2 if z > hi2 else z
        d = math.hypot(x-px, z-pz)
        best = (d, px, pz, (x-px)/d, (z-pz)/d)
    if polys:
        within = least = None
        hypot = math.hypot
        for edges in polys:
            bd = None
            inside = False
            for a0, a2, b2, ex, ez, L, _ in edges:
                if L == 0:
                    t = 0
                else:
                    t = ((x-a0)*ex+(z-a2)*ez)/L
                    t = 0. if t < 0. else 1. if t > 1. else t
                px, pz = a0+t*ex, a2+t*ez
                d = hypot(x-px, z-pz)
                if bd is None or d < bd:
                    bd, bx, bz = d, px, pz
                if (a2 > z) != (b2 > z) and x < ex*(z-a2)/ez+a0:
                    inside = not inside
            if bd < 1e-9:
                c = (0., bx, bz, 0., 0.)
            else:
                ox, oz = (x-bx)/bd, (z-bz)/bd
                c = (-bd, bx, bz, -ox, -oz) if inside else (bd, bx, bz, ox, oz)
            if c[0] <= 0 and (within is None or c[0] > within[0]):
                within = c
            if least is None or c[0] < least[0]:
                least = c
        poly = within if within is not None else least
        if poly[0] > best[0]:
            best = poly
    return best


def outside_distance(room, x, z):
    """Distance outside the walkable floor (0 inside)."""
    return max(0., wall_frame(room, x, z)[0])


def inside_distance(room, x, z):
    """Distance inside the walkable floor to its nearest wall (negative outside).
    Inside the bounds of a room whose floor is just its bounds (`_floor_holds_bounds`)
    that is the nearest bound's distance, the same number `wall_frame` gives."""
    got = _SHAPES.get(id(room))
    if got is None or got[0] is not room:
        _shape(room)
        got = _SHAPES[id(room)]
    if got[2]:
        lo0, lo2, hi0, hi2, _ = got[1]
        if lo0 <= x <= hi0 and lo2 <= z <= hi2:
            return min(x-lo0, hi0-x, z-lo2, hi2-z)
    return -wall_frame(room, x, z)[0]


def perimeter(room, step=.1):
    """Points along the walkable floor's boundary, in a fixed order."""
    lo, hi, floors = room_shape(room)
    pts = []
    nx_, nz_ = int((hi[0]-lo[0])/step), int((hi[2]-lo[2])/step)
    for a in range(nx_+1):
        for b in range(nz_+1):
            x, z = lo[0]+a*step, lo[2]+b*step
            if -step < wall_frame(room, x, z)[0] <= 0:
                pts.append((round(x, 4), round(z, 4)))
    return pts


def openings(room):
    out = []
    lo, hi, _ = room_shape(room)
    for o in room['shell'].get('openings', []):
        x, _, z = o['center_m']
        width = o['size_m'][0]
        # Wall direction: the opening lies on the nearest bounds side.
        sides = [(abs(x-lo[0]), 'x'), (abs(x-hi[0]), 'x'), (abs(z-lo[2]), 'z'), (abs(z-hi[2]), 'z')]
        axis = min(sides)[1]
        out.append(dict(id=o['id'], kind=o['kind'], x=x, z=z, width=width,
                        along='z' if axis == 'x' else 'x'))
    return out


def opening_factor(room, x, z):
    """How much a wall opening lowers the ridgeline above this point: a door is a
    low pass, a window a col where the sunrise shows."""
    lowest = 1.
    for o in openings(room):
        along = abs((z-o['z']) if o['along'] == 'z' else (x-o['x']))
        across = abs((x-o['x']) if o['along'] == 'z' else (z-o['z']))
        if across > 2.:
            continue
        beyond = max(0., along-o['width']/2)
        depth = .32 if 'door' in o['kind'] else .55
        lowest = min(lowest, lerp(depth, 1., smooth(beyond/.6)))
    return lowest


def cameras(room):
    return [overview_camera(room, n, sx, sz) for n, sx, sz in
            [('overview_ne', 1, -1), ('overview_sw', -1, 1), ('overview_se', 1, 1)]]


def sightline_cap(room, cams, x, z):
    """Highest land at (x,z) outside the room that cannot hide the room from a
    review overview: below each camera's ray to where it first meets the room."""
    lo, hi, _ = room_shape(room)
    cap = math.inf
    for c in cams:
        cx, cy, cz = c['position_m']
        dx, dz = x-cx, z-cz
        dist = math.hypot(dx, dz)
        if dist < 1e-6:
            continue
        ux, uz = dx/dist, dz/dist
        # Ray/box entry distance in x-z.
        tmin, tmax = -math.inf, math.inf
        for o, u, a, b in [(cx, ux, lo[0], hi[0]), (cz, uz, lo[2], hi[2])]:
            if abs(u) < 1e-12:
                if not a <= o <= b:
                    tmin, tmax = math.inf, -math.inf
                continue
            t1, t2 = (a-o)/u, (b-o)/u
            tmin = max(tmin, min(t1, t2))
            tmax = min(tmax, max(t1, t2))
        if tmax < tmin or tmax < 0 or dist >= tmin:
            continue
        cap = min(cap, cy*(1-dist/tmin)-.04)
    return cap


def high_country(room, outlet):
    """Where the wall ring rises highest: the stretch of wall farthest from the
    pass where water leaves, so the land drains from the heights to the pass.
    Chosen from the room alone, never from a camera."""
    return max(perimeter(room), key=lambda p: (round(math.hypot(p[0]-outlet['x'], p[1]-outlet['z']), 3), p))


def shell_land(grid, room, h, noise, outlet, objects=()):
    """Walls dissolve into irregular ridgelines, highest in the high country and
    broken by passes at the openings. Each ridge's far side runs down into the
    shared surround (FAR_Y) as a flat skirt, so no seam shows where they meet.
    Interior land rises a little at the walls. Returns (outside mask, skirt
    weight: 1 where the land has become the surround)."""
    lo, hi, _ = room_shape(room)
    cx, cz = (lo[0]+hi[0])/2, (lo[2]+hi[2])/2
    margin = min(grid.xs[-1]-hi[0], lo[0]-grid.x0, grid.zs[-1]-hi[2], lo[2]-grid.z0)
    far = high_country(room, outlet)
    outside = [False]*grid.n
    skirt = [0.]*grid.n
    for j, z in enumerate(grid.zs):
        for i, x in enumerate(grid.xs):
            q = j*grid.nx+i
            s, px_, pz_, ox, oz = wall_frame(room, x, z)
            if s < -.95:
                continue
            if s > 0:
                outside[q] = True
            ang = math.atan2(z-cz, x-cx)
            ca, sa = math.cos(ang), math.sin(ang)
            crest = .42+.28*noise.fbm(ca*2.2+9, sa*2.2-4, 3)
            crest *= .55+.45*(.5+.5*noise(px_*1.6+21, pz_*1.6-13))
            fd = math.hypot(px_-far[0], pz_-far[1])
            crest += 1.05*bell(fd/3.0)
            crest *= opening_factor(room, x, z)
            dc = .45+.6*(.5+.5*noise(px_*.7-3, pz_*.7+8))+.25*max(0., crest-.6)
            # Broad, grassy inner slopes: the foot reaches farther in under higher crests.
            foot = -.15-.3*(.5+.5*noise(px_*1.3+2, pz_*1.3+5))-.35*max(0., crest-.45)
            # The outer slope reaches the surround well inside the grid.
            reach_out = min(margin-.15, dc+.55+.75*crest)
            if s < foot:
                continue
            if s < dc:
                ridge = crest*bell((dc-s)/(dc-foot))
                fade = 0.
            else:
                t = (s-dc)/max(.2, reach_out-dc)
                fade = smooth(t)
                ridge = crest*(1-fade)
            ridge += .05*noise.ridged(x*3.3, z*3.3)*smooth(s/.3)*smooth((crest-.1)/.3)*(1-fade)
            if s <= 0:
                # The ridge rises behind what stands against the wall, never over it.
                cap = math.inf
                for o in objects:
                    if o['parent'] is not None:
                        continue
                    lx, lz = local(o, x, z)
                    d = rr_dist(lx, lz, o['sx']/2, o['sz']/2, .05)
                    if d < .5:
                        cap = min(cap, o['top']+.04+max(0., d)*1.6)
                h[q] = max(h[q], min(h[q]+ridge, cap))
                continue
            land = max(h[q], h[q]+ridge)
            h[q] = lerp(land, FAR_Y, fade)
            skirt[q] = smooth((fade-.55)/.45)
    return outside, skirt


def flow_accumulation(grid, h):
    """D8 steepest-descent flow; returns accumulated upslope area (cells)."""
    nx, nz = grid.nx, grid.nz
    order = sorted(range(grid.n), key=lambda q: (-h[q], q))
    acc = [1.]*grid.n
    down = [-1]*grid.n
    c = grid.cell
    diag = c*math.sqrt(2)
    for q in order:
        i, j = q % nx, q//nx
        best = 0.
        target = -1
        for di, dj in ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)):
            ii, jj = i+di, j+dj
            if 0 <= ii < nx and 0 <= jj < nz:
                p = jj*nx+ii
                drop = (h[q]-h[p])/(diag if di and dj else c)
                if drop > best:
                    best = drop
                    target = p
        down[q] = target
        if target >= 0:
            acc[target] += acc[q]
    return acc, down


def weather(grid, h, outside, slope, noise, rounds=3):
    """Erosion signature: a few rounds of stream-power incision, so gullies
    gather into branching drainage and the ground between them stands as
    spurs; then softened crests and talus below steep faces. Where the land
    carries caps (`Heights.cap`: a magnified room's blocks) a cap bed, a
    face and the plain at its foot are spared: the weather cuts and rounds
    only soft tops and the open plain."""
    from .field import slope_field
    cap = getattr(h, 'cap', None)
    out = h[:]
    for r in range(rounds):
        acc, _ = flow_accumulation(grid, out)
        s_now = slope_field(grid, out) if r else slope
        for q in range(grid.n):
            s = s_now[q]
            if s < 10 or (cap is not None and cap[q]):
                continue
            a = min(acc[q], 600.)
            out[q] -= .0016*math.sqrt(a)*smooth((s-10)/25)*(1.25 if r == 0 else 1.)
    # Soften crests: blur where convex and steep.
    nx = grid.nx
    soft = out[:]
    for j in range(1, grid.nz-1):
        for i in range(1, nx-1):
            q = j*nx+i
            if cap is not None and cap[q]:
                continue
            avg = (out[q-1]+out[q+1]+out[q-nx]+out[q+nx])*.25
            if avg < out[q]:
                soft[q] = out[q]+(avg-out[q])*.45*smooth(slope[q]/30)
            elif slope[q] < 40:
                soft[q] = out[q]+(avg-out[q])*.25
    return soft, acc


def sun_shadow(grid, h, sun_dir, step=3, reach_m=2.6):
    """1 where the morning sun is blocked by land (marched on a coarse lattice)."""
    sx, sy, sz = sun_dir
    horiz = math.hypot(sx, sz)
    ux, uz = sx/horiz, sz/horiz
    rise = sy/horiz
    nx, nz = grid.nx, grid.nz
    coarse = {}
    dstep = grid.cell*1.5
    nsteps = int(reach_m/dstep)
    # The march's offsets, worked out once (the same products as step by step).
    march = [(ux*t, uz*t, rise*t) for t in (k*dstep for k in range(1, nsteps))]
    x0, z0, cell = grid.x0, grid.z0, grid.cell
    xs, zs = grid.xs, grid.zs
    # A rising ray over the highest land meets none after it.
    top = max(h) if rise > 0 else math.inf
    for j in range(0, nz, step):
        z = zs[j]
        for i in range(0, nx, step):
            q = j*nx+i
            x, y0 = xs[i], h[q]+.004
            lit = 1.
            for ox, oz, oy in march:
                if y0+oy > top:
                    break
                # grid.nearest, inline
                ii = int(round((x+ox-x0)/cell))
                ii = 0 if ii < 0 else nx-1 if ii > nx-1 else ii
                jj = int(round((z+oz-z0)/cell))
                jj = 0 if jj < 0 else nz-1 if jj > nz-1 else jj
                if h[jj*nx+ii] > y0+oy:
                    lit = 0.
                    break
            coarse[(i, j)] = 1.-lit
    out = [0.]*grid.n
    last_i = ((nx-1)//step)*step
    cols = []
    for i in range(nx):
        i0 = (i//step)*step
        i1 = min(i0+step, last_i)
        cols.append((i0, i1, 0 if i1 == i0 else (i-i0)/(i1-i0)))
    for j in range(nz):
        j0 = (j//step)*step
        j1 = min(j0+step, ((nz-1)//step)*step)
        v = 0 if j1 == j0 else (j-j0)/(j1-j0)
        row = j*nx
        for i, (i0, i1, u) in enumerate(cols):
            a = coarse[(i0, j0)]
            b = coarse[(i1, j0)]
            c = coarse[(i0, j1)]
            d = coarse[(i1, j1)]
            out[row+i] = (a*(1-u)+b*u)*(1-v)+(c*(1-u)+d*u)*v
    return out


def dijkstra(grid, cost, start, goals, step=1, blocked=None):
    """Least-cost 8-connected path between grid indices; cost is per metre."""
    nx, nz = grid.nx, grid.nz
    goalset = set(goals)
    dist = {start: 0.}
    prev = {}
    heap = [(0., start)]
    c = grid.cell*step
    while heap:
        d, q = heapq.heappop(heap)
        if q in goalset:
            path = [q]
            while q in prev:
                q = prev[q]
                path.append(q)
            return path[::-1]
        if d > dist.get(q, math.inf):
            continue
        i, j = q % nx, q//nx
        for di, dj in ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)):
            ii, jj = i+di*step, j+dj*step
            if not (0 <= ii < nx and 0 <= jj < nz):
                continue
            p = jj*nx+ii
            if blocked is not None and blocked(q, p):
                continue
            w = (c*math.sqrt(2) if di and dj else c)*(cost[q]+cost[p])*.5
            nd = d+w
            if nd < dist.get(p, math.inf)-1e-12:
                dist[p] = nd
                prev[p] = q
                heapq.heappush(heap, (nd, p))
    return None
