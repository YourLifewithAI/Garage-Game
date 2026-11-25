"""Fractals and gates: the generator's part of the mechanics round (packet M4).

The island places a budget of Fractals (docs/game/MECHANICS.md section 6): real small things at crests the body can
reach by climbing, untyped by default (a heart or a band only once spent at a camp; `typed` puts the kind in the
manifest behind the rules pack's flag). The mechanics need the land to be a ladder: each rise between ledges is one band
of the stamina bar, so the player who has opened band k finds a Fractal on a ledge that needs band k, sees the next
ledge one band up, and climbs it when the next five are spent.

Built here, in two parts either side of the planting:

`plan` (before the ground is worked at 2.5 cm, on the base grid): finds places and builds the flights (routes.py):
    - the summit's corridor, cut through the tallest landmark in reach to its top, and where that is no more than the
      island's own Fractals open, a stair built on the landmark's top above its platform (the upper ledges at the top
      bands), so the summit waits for a return visit (decided 15 October),
    - ladders of gates (band 1, 2, 3, ... each one rise more), freestanding on flat meadow,
    - knack shortcuts (a higher ledge one knack at full rank opens a band sooner, the base body does not),
    - steps (one band-1 ledge: the climb a first-run player makes for a Fractal).
`finish` (on the planted island, after the exact walk): the climb-aware reach (reach.py) of the base body, one knack at
    full rank and the strongest pair; each bench's band proved (a gate that is not the band it was built as is
    reported, a bench no one reaches is dropped); the Fractals placed on benches, in hidden corners the feet reach, and
    on high ground no band reaches, spread over the island; their ids from the place; the reach report.

Ids come from the place (the first 12 hex digits of a hash of where a Fractal rests), never a count, so an id says
nothing about how many the island holds or which was placed first.
"""
import hashlib
import math
import re

from . import gates, routes as R, sites

# The rules pack's `fractals` block (storybook_wild v5): the island's budget (the founder, 15 October: about fifteen within
# the base body's reach from the start and ten or more out of reach yet, so 25 or more; 30 is a placeholder ceiling).
BUDGET = dict(min=25, max=30, reachable_first_run=15, per_band=gates.PER_BAND)
OUT_OF_REACH_MIN = 10                 # ... of which this many or more lie where a first-run player cannot reach yet
LADDERS = ((1, 2, 3, 4), (1, 2, 3))   # ladders of gates on flat meadow (the bands of their ledges, foot to top)
SHORTCUT_BANDS = (1, 2)               # knack shortcuts: one for each band where the arithmetic holds (gates.py)
STEPS = 8                             # band-1 steps
FIRST_STEP_M = 12.                    # the nearest step stands about this far from where the player arrives
SUMMIT_MIN_M = 4.                     # a summit stands at least this far above the ground its corridor starts on
CORNER_SPACING_M = 6.                 # hidden corners lie this far apart
CORNER_RING_M = (1.5, 3.)             # ... and are measured closed in at these radii


def fractal_id(x, z):
    """A Fractal's id from where it rests (centimetres, x then z): 'fractal:' and 12 hex digits."""
    return 'fractal:'+hashlib.sha256(('fractal|%.2f|%.2f' % (x, z)).encode()).hexdigest()[:12]


class Plan:
    """What `plan` built: the flights, the summit's, and what the planting must leave alone."""

    def __init__(self, land, routes, summit, notes):
        self.land, self.routes, self.summit, self.notes = land, routes, summit, notes
        self.keep = set()

    def summit_routes(self):
        """The summit's flights, foot to top: its corridor, then the stair built above it (if it has one)."""
        if self.summit is None:
            return []
        return [self.summit]+([self.summit.tower] if self.summit.tower is not None else [])

    def all_routes(self):
        return self.summit_routes()+list(self.routes)

    def hold(self, detail):
        """On the 10x world: a flight that lies in a detail block keeps the base's surface there (the surface the flight
        was built as, like the ways the body walks: `Detail.walks_on_base`), a cell beyond its edge included."""
        g = self.land.grid
        for route in self.all_routes():
            i0, i1, j0, j1 = sites.flight_box(route, 1)
            x0, x1, z0, z1 = g.xs[max(0, i0)], g.xs[min(g.nx-1, i1)], g.zs[max(0, j0)], g.zs[min(g.nz-1, j1)]

            def run(x0=x0, x1=x1, z0=z0, z1=z1):
                bench = detail.bench
                for k in detail.span(x0, x1, z0, z1):
                    bench[k] = 1.
            detail.op(run)


def landmarks(land):
    """The island's tall landforms, highest first (a landmark: an object that stands on the floor)."""
    objs = [o for o in land.objects if o['parent'] is None and o['sy'] >= SUMMIT_MIN_M and o['form'] not in ('dome', 'knoll')]
    return sorted(objs, key=lambda o: (-o['top'], o['id']))


def _near_reach(land, obj):
    """Whether the walk reaches ground near a landmark (within a few metres of its box)."""
    r = max(obj['sx'], obj['sz'])/2+5.
    for k in range(16):
        a = k*math.pi/8
        if land.reached(obj['cx']+r*math.cos(a), obj['cz']+r*math.sin(a)):
            return True
    return False


def top_band(route):
    """The band a flight needs to its top: the highest band of its ledges, and of the stair built above it."""
    return max(l.band for rt in ([route]+([route.tower] if route.tower is not None else [])) for l in rt.ledges)


def plan(land, seed, spawn, recipe=None, budget=None):
    """Find places and build the flights into `land.h`. `spawn` is (x, z) where the player arrives. Returns a Plan.
    `budget` is the island's (`BUDGET`): the summit must need more than its Fractals can open (`gates.own_band`)."""
    recipe = recipe or dict(ladders=LADDERS, shortcuts=SHORTCUT_BANDS, steps=STEPS)
    budget = budget or BUDGET
    grid, h = land.grid, land.h
    siting = sites.Siting(land, seed_bits=seed & 0xffff)
    notes = []
    own = gates.own_band(budget['max'], budget.get('per_band', gates.PER_BAND))

    def spawn_distance(x, z):
        return math.hypot(x-spawn[0], z-spawn[1])

    # The summit: the tallest landmark in reach with a corridor whose top (with the stair above its platform) needs more
    # than the island's own Fractals open; failing that, the highest summit there is, and the report says so.
    summit, short = None, None
    for obj in landmarks(land):
        if not _near_reach(land, obj):
            continue
        found = sites.plan_summit(land, siting, obj, lambda height: gates.ladder_rises(height) if height >= SUMMIT_MIN_M
                                  else None, own_band=own)
        if found is None:
            continue
        found.id, found.landmark = 'summit', obj['id']
        if found.tower is not None:
            found.tower.landmark = obj['id']
        if top_band(found) > own:
            summit = found
            break
        if short is None or top_band(found) > top_band(short):
            short = found
    if summit is None and short is not None:
        summit = short
        notes.append('no landmark in reach has a summit beyond the %d bands the island\'s own Fractals open: the summit '
                     'needs band %d' % (own, top_band(short)))
    if summit is None:
        notes.append('no landmark in reach has a corridor to its top')
    else:
        for route in ([summit]+([summit.tower] if summit.tower is not None else [])):
            siting.take(route)
    placed = []
    counter = [0]

    def ident(kind):
        counter[0] += 1
        return '%s%d' % (kind[0], counter[0])

    def spread(x, z, d_taken):
        return min(d_taken, 14.)+.1*min(spawn_distance(x, z), 25.)

    # Ladders first (the longest), then the shortcuts, then the steps; the first step near the spawn.
    for bands in recipe['ladders']:
        route = siting.place(lambda i0, j0, di, dj, z0, b=bands: R.ladder('ladder', grid, i0, j0, di, dj, z0, b, ident('ladder')),
                             prefer=spread)
        if route is None:
            notes.append('no flat meadow for a ladder of %d ledges' % len(bands))
        else:
            placed.append(route)
    for band in recipe['shortcuts']:
        route = siting.place(lambda i0, j0, di, dj, z0, b=band: R.shortcut(grid, i0, j0, di, dj, z0, b, ident('shortcut')),
                             prefer=spread)
        if route is None:
            notes.append('no flat meadow for a knack shortcut over band %d' % band)
        else:
            placed.append(route)
    for k in range(recipe['steps']):
        near_first = (lambda x, z, d: -abs(spawn_distance(x, z)-FIRST_STEP_M)) if k == 0 else spread
        route = siting.place(lambda i0, j0, di, dj, z0: R.ladder('step', grid, i0, j0, di, dj, z0, (1,), ident('step')),
                             prefer=near_first)
        if route is None:
            notes.append('no flat meadow for step %d' % (k+1))
            break
        placed.append(route)
    out = Plan(land, placed, summit, notes)
    # Build them, and keep the planting off them (a bare skirt round each flight).
    nx = grid.nx
    skirt = int(round(.5/grid.cell))
    for route in out.all_routes():
        route.apply(grid, h)
        i0, i1, j0, j1 = sites.flight_box(route, skirt)
        for j in range(max(0, j0), min(grid.nz, j1+1)):
            row = j*nx
            for i in range(max(0, i0), min(nx, i1+1)):
                out.keep.add(row+i)
    return out


# ------------------------------------------------------------------------------------------- after the walk


def route_from_row(grid, row):
    """A flight rebuilt from what the package says of it (`Route.describe`)."""
    c = grid.cell
    ledges = [R.Ledge(l['rise_m'], l['band'], int(round(l['riser_run_m']/c)), int(round(l['bench_length_m']/c)))
              for l in row['ledges']]
    i0, j0 = grid.nearest(*row['origin_m'])
    route = R.Route(row['kind'], i0, j0, row['direction'][0], row['direction'][1], row['foot_level_m'], ledges,
                    half=int(round(row['half_width_m']/c)), ident=row['id'])
    if row['kind'] == 'shortcut':
        route.over = row['ledges'][0]['band']-1   # the band whose bar falls short of it
    return route


def _expected_band(route, k):
    """The band a bench is reached from by design: the highest band of the rises up to it."""
    return max(l.band for l in route.ledges[:k+1])


def _corner_score(land, x, z):
    """How closed in a place is: of 24 places on two rings round it, how many are steep, water, a landform's own
    ground or outside the floor."""
    g = land.grid
    nx = g.nx
    n = 0
    for r in CORNER_RING_M:
        for k in range(12):
            a = k*math.pi/6
            i, j = g.nearest(x+r*math.cos(a), z+r*math.sin(a))
            q = j*nx+i
            if land.fslope[q] > 35 or land.protect[q] or land.wet[q] < .3 or not land.inside[q]:
                n += 1
    return n


def hidden_corners(land, labels, taken, count, spawn, jitter):
    """Places the feet reach (band 1, no climb) that are closed in on most sides, spread apart: where a Fractal is
    tucked away. `taken` are (x, z) already used."""
    g = land.grid
    nx = g.nx
    found = []
    for j in range(0, g.nz, 10):
        for i in range(0, nx, 10):
            q = j*nx+i
            if not land.inside[q] or land.wet[q] < 2. or land.paths[q] >= .05 or land.pads[q] > 0 or land.protect[q] \
                    or land.fslope[q] > 12 or land.blocked[q]:
                continue
            x, z = g.xs[i], g.zs[j]
            if labels.band_at(x, z) != 1 or math.hypot(x-spawn[0], z-spawn[1]) < CORNER_SPACING_M:
                continue
            found.append((-_corner_score(land, x, z), jitter(i, j), x, z))
    found.sort()
    picked = []
    for _, _, x, z in found:
        if len(picked) >= count:
            break
        if all(math.hypot(x-a, z-b) >= CORNER_SPACING_M for a, b in picked+list(taken)):
            picked.append((x, z))
    return picked


HIGH_WALK_DEG = 40.  # a lift place is closed in by cliffs: ground no steeper than this (a body walks 45) leads no farther than ...
HIGH_REGION_CELLS = 40000   # ... this many cells (a 400 m2 plateau), else a hillside leads down from it
LIFT_GENTLE_DEG = 35.    # ... and where it rests the ground is one a body stands on (the climb rule's lip) ...
LIFT_RING = 3            # ... 0.3 m round it, in cells
LIFT_RIM_M = 6.5     # ... and its rim stands this far over the ground at the foot of the cliff: more than the top band's bar
#                      climbs (6.0 m, gates.cap), so no base body climbs it at any band, only the Gubble's lift or a built ladder reach it
LIFT_LOOK_M = 4.     # ... measured this far down the fall line from the rim
LIFT_APART_M = 6.    # lift places lie this far from each other and from every other Fractal
LIFT_PLACES = 3      # this many Fractals rest on lift places, spread over the island (where the budget allows)


def enclosed(land, i, j, max_deg=HIGH_WALK_DEG, limit=HIGH_REGION_CELLS):
    """Whether the ground round vertex (i, j), walked over land no steeper than `max_deg`, is closed in (a plateau all
    cliffs round it) rather than a hill one can walk up from below. Returns (closed, the vertices walked): when it is
    not closed in, the vertices are a hill's, and none of them is closed in either."""
    g = land.grid
    nx, nz = g.nx, g.nz
    fs = land.fslope
    start = j*nx+i
    seen = {start}
    stack = [start]
    while stack:
        q = stack.pop()
        qi, qj = q % nx, q//nx
        for di, dj in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            ni, nj = qi+di, qj+dj
            if not (0 <= ni < nx and 0 <= nj < nz):
                return False, seen
            n = nj*nx+ni
            if n in seen or fs[n] > max_deg:
                continue
            seen.add(n)
            if len(seen) > limit:
                return False, seen
            stack.append(n)
    return True, seen


def _outer_rim(grid, region):
    """The plateau's outer rim: {region vertex: a vertex outside the region, joined to the open land beyond it}. A pit or a
    bump inside the plateau is a hole in the region, not a rim (the open land is found by flooding the vertices outside the
    region from the edge of its bounding box)."""
    nx = grid.nx
    ii = [q % nx for q in region]
    jj = [q//nx for q in region]
    i0, i1, j0, j1 = min(ii)-1, max(ii)+1, min(jj)-1, max(jj)+1
    if i0 < 0 or j0 < 0 or i1 > nx-1 or j1 > grid.nz-1:
        return {}
    open_land = set()
    stack = [j*nx+i for j in (j0, j1) for i in range(i0, i1+1)]+[j*nx+i for i in (i0, i1) for j in range(j0+1, j1)]
    open_land.update(stack)
    while stack:
        q = stack.pop()
        qi, qj = q % nx, q//nx
        for di, dj in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            ni, nj = qi+di, qj+dj
            if not (i0 <= ni <= i1 and j0 <= nj <= j1):
                continue
            n = nj*nx+ni
            if n in open_land or n in region:
                continue
            open_land.add(n)
            stack.append(n)
    rim = {}
    for q in region:
        for d in (1, -1, nx, -nx):
            if q+d in open_land:
                rim[q] = q+d
                break
    return rim


def rim_drop(land, region, look_m=LIFT_LOOK_M):
    """How far the ground falls, at the least, from the outer rim of a closed-in plateau (`region`: its vertices): from
    every third rim vertex straight down the fall line for `look_m`. A plateau cliffs close in all round has a large least
    drop; a rim with a way down in it (a saddle, a ledge, a ramp) has a small one. Where the land rises beyond the plateau
    (the wall of another landform) there is no cliff to fall from: a line down that comes back to the plateau is not
    counted."""
    g, h = land.grid, land.h
    nx = g.nx
    least = math.inf
    for k, (q, out) in enumerate(sorted(_outer_rim(g, region).items())):
        if k % 3:
            continue
        x, z = g.xs[out % nx], g.zs[out//nx]    # the first vertex over the rim: the cliff's face
        y0 = h[q]
        low = min(y0, h[out])
        back = False
        for _ in range(int(look_m/.2)):
            gx = g.sample(h, x+.05, z)-g.sample(h, x-.05, z)
            gz = g.sample(h, x, z+.05)-g.sample(h, x, z-.05)
            n = math.hypot(gx, gz)
            if n < 1e-9:
                break
            x, z = x-gx/n*.2, z-gz/n*.2
            if not (g.xs[0] <= x <= g.xs[-1] and g.zs[0] <= z <= g.zs[-1]):
                break
            i_, j_ = g.nearest(x, z)
            if j_*nx+i_ in region:
                back = True    # the line down came back to the plateau: a wall rises here, no cliff falls
                break
            low = min(low, g.sample(h, x, z))
        if not back:
            least = min(least, y0-low)
    return least


def lift_places(land, labels, keep=(), taken=(), count=LIFT_PLACES):
    """Places only the Gubble's lift or a built ladder reaches (a return visit with more capability), spread over the
    island: ground the body stands on (nothing planted in its lane) on a plateau that cliffs close in
    all round (`enclosed`), whose rim stands `LIFT_RIM_M` over the ground at the foot of its cliffs (`rim_drop`: more than
    any bar climbs), that no band reaches, one to a plateau, never on a flight (`keep`: the vertices of the flights and
    their skirts, nor on a plateau a flight stands on), `LIFT_APART_M` from each other and from `taken` ((x, z) of the
    Fractals already placed), the farthest from the others first. They are not walked to, so need not be nodes the strict
    walk stands on. Returns [(x, z)], at most `count`."""
    g = land.grid
    nx, nz = g.nx, g.nz
    w = labels.walker
    ring = LIFT_RING
    found = []
    for j in range(ring, nz-ring, 5):
        for i in range(ring, nx-ring, 5):
            q = j*nx+i
            if land.h[q] < LIFT_RIM_M-1. or not land.inside[q] or land.fslope[q] > LIFT_GENTLE_DEG or land.wet[q] < 2.:
                continue
            around = [(j+dj)*nx+i+di for di, dj in ((ring, 0), (-ring, 0), (0, ring), (0, -ring))]
            if any(land.fslope[a] > LIFT_GENTLE_DEG or a in keep for a in around) or q in keep:
                continue
            x, z = g.xs[i], g.zs[j]
            n = labels.node(x, z)
            if (n is not None and labels.label[n]) or w.blocked(x, z):
                continue
            found.append((-land.h[q], i, j))
    found.sort()
    seen_ground = set()
    spots = []
    for _, i, j in found:
        if j*nx+i in seen_ground:
            continue
        closed, region = enclosed(land, i, j)
        seen_ground |= region    # a plateau or a hillside: every candidate on it is the same place
        if closed and not (keep and any(q in keep for q in region)) and rim_drop(land, region) >= LIFT_RIM_M:
            spots.append((g.xs[i], g.zs[j]))
    placed = []
    others = list(taken)
    while spots and len(placed) < count:
        score = [(min([math.hypot(x-a, z-b) for a, b in others] or [LIFT_APART_M*10.]), -k) for k, (x, z) in enumerate(spots)]
        best = max(range(len(spots)), key=lambda k: score[k])
        if score[best][0] < LIFT_APART_M:
            break
        x, z = spots.pop(best)
        placed.append((x, z))
        others.append((x, z))
    return placed


NATURAL_CLUSTER_M = 1.5   # climbs whose tops lie within this of each other are one way up in the report


def natural_ways(plan, variants):
    """The ways up the land itself offers (climbs not in a flight) that the exact walk used, one per top place:
    where the climb ends, its rise, and the lowest band each variant of the body takes it from."""
    grid = plan.land.grid
    nx = grid.nx
    keep = plan.keep

    def built(xz):
        i, j = grid.nearest(*xz)
        return j*nx+i in keep
    w = variants['base'].walker
    ways = {}
    for name, labels in variants.items():
        for c, band in labels.climbs:
            if built(c.foot_xz) or built(c.top_xz):
                continue
            key = (int(round(c.top_xz[0]/NATURAL_CLUSTER_M)), int(round(c.top_xz[1]/NATURAL_CLUSTER_M)))
            row = ways.setdefault(key, dict(top_m=[round(c.top_xz[0], 3), round(w.t.height(*c.top_xz), 3),
                                                  round(c.top_xz[1], 3)], rise_m=round(c.rise, 3), reached_from={}))
            row['rise_m'] = min(row['rise_m'], round(c.rise, 3))
            got = row['reached_from'].get(name)
            if got is None or band < got:
                row['reached_from'][name] = band
    return sorted(ways.values(), key=lambda r: (min(r['reached_from'].values()), r['top_m']))


def finish(plan, cr, spawn, typed=False, budget=BUDGET):
    """Prove the flights on the planted island and place the Fractals. `cr` is the reach.ClimbReach of the land as it
    will be written. Returns the package block (`x_generator.fractals`)."""
    land = plan.land
    grid = land.grid
    variants = cr.variants()
    base = variants['base']
    w = cr.w
    own = gates.own_band(budget['max'], budget.get('per_band', gates.PER_BAND))
    benches, route_rows = [], []
    slots = []   # (priority, kind, route id, ledge index, x, z) candidate Fractals on benches
    for route in plan.all_routes():
        row = route.describe(grid)
        for k, ledge in enumerate(row['ledges']):
            x, y, z = ledge['bench_m']
            exp = _expected_band(route, k)
            bands = {name: v.band_at(x, z) for name, v in variants.items()}
            got = bands['base']
            row['ledges'][k]['expected_band'] = exp
            row['ledges'][k]['reached_from'] = bands
            row['ledges'][k]['proved'] = got == exp
            if got is not None and got < exp:
                row['ledges'][k]['bypassed'] = True
            early = {name: b for name, b in bands.items() if b is not None and b < exp}
            row['ledges'][k]['opened_early_by'] = early
            benches.append(dict(route=route.id, kind=route.kind, ledge=k+1, band=exp, rise_m=ledge['rise_m'],
                                bench_m=ledge['bench_m'], reached_from=bands, proved=got == exp))
            if got is not None:
                slots.append((route, k, x, z, got))
        route_rows.append(row)
    items, detail = [], []
    taken = []

    def add(x, z, band, why, route=None, ledge=None):
        y = w.t.height(x, z)    # the land as written, at the Fractal's own place
        if y != y:
            y = land.grid.tri_height(land.h, x, z)
        item = dict(id=fractal_id(x, z), position_m=[round(x, 4), round(y, 4), round(z, 4)])
        if band is not None:
            item['reach_band'] = band
        items.append(item)
        info = dict(id=item['id'], why=why, reach_band=band,
                    reached_from={name: v.band_at(x, z) for name, v in variants.items()})
        if route is not None:
            info.update(route=route.id, ledge=ledge+1)
        detail.append(info)
        taken.append((x, z))
    # On benches: every ledge of the ladders, the shortcuts and the steps; of the summit, every ledge of the stair built
    # above the corridor (the prizes of a return visit) and its top, or the corridor's top where it has no stair.
    summit_routes = plan.summit_routes()
    for route, k, x, z, got in slots:
        if route.kind != 'summit':
            add(x, z, got, route.kind, route, k)
        elif route is summit_routes[-1]:
            add(x, z, got, 'summit' if k == len(route.ledges)-1 else 'stair', route, k)
    # Hidden corners the feet reach, to make the first run's count.
    first_run = sum(1 for it in items if it.get('reach_band') == 1)
    want = max(0, budget['reachable_first_run']-first_run)

    def jitter(i, j):
        return ((i*73856093 ^ j*19349663) & 0xffff)/65535.
    for x, z in hidden_corners(land, base, taken, want, spawn, jitter):
        add(x, z, 1, 'corner')
    # Some on high ground no band reaches (only the Gubble's lift or a built ladder, the design's other ways), spread over
    # the island: at least LIFT_PLACES, and as many more as the budget's floor and the ten out of reach need.
    out = sum(1 for it in items if it.get('reach_band') != 1)
    want_lift = max(LIFT_PLACES, OUT_OF_REACH_MIN-out, budget['min']-len(items))
    want_lift = min(want_lift, budget['max']-len(items))
    for x, z in lift_places(land, base, plan.keep, taken, want_lift):
        add(x, z, None, 'high')
    # The budget's ceiling, then typed kinds.
    items = items[:budget['max']] if len(items) > budget['max'] else items
    ids = {it['id'] for it in items}
    detail = [d for d in detail if d['id'] in ids]
    if len(ids) != len(items):
        raise ValueError('two Fractals share an id')
    if typed:
        for it in items:
            it['kind'] = 'heart' if int(it['id'][-1], 16) % 2 == 0 else 'band'
    items.sort(key=lambda it: it['id'])
    detail.sort(key=lambda d: d['id'])
    by_band = {}
    for it in items:
        key = str(it['reach_band']) if 'reach_band' in it else 'none'
        by_band[key] = by_band.get(key, 0)+1
    shortcuts = []
    for route in plan.routes:
        if route.kind != 'shortcut':
            continue
        row = next(r for r in route_rows if r['id'] == route.id)
        ledge = row['ledges'][0]
        reached = ledge['reached_from']
        d = next((d for d in detail if d.get('route') == route.id), None)
        shortcuts.append(dict(
            route=route.id, rise_m=ledge['rise_m'], bench_m=ledge['bench_m'],
            base_band=reached['base'], one_knack_band=reached['one_knack'], pair_band=reached['pair'],
            over_band=route.over,
            base_falls_short_m=round(ledge['rise_m']-gates.cap(route.over), 4),
            knack_clears_by_m=round(gates.cap(route.over, gates.FULL_RANK)-ledge['rise_m'], 4),
            fractal=None if d is None else d['id']))
    summit_row = None
    if plan.summit is not None:
        srows = [next(r for r in route_rows if r['id'] == rt.id) for rt in summit_routes]
        top_row, top_route = srows[-1], summit_routes[-1]
        summit_row = dict(landmark=plan.summit.landmark, ledges=sum(len(r['ledges']) for r in srows),
                          cut_ledges=len(srows[0]['ledges']), built_ledges=sum(len(r['ledges']) for r in srows[1:]),
                          length_m=round(sum(r['length_m'] for r in srows), 4),
                          top_band=max(l['band'] for r in srows for l in r['ledges']), own_band=own,
                          top_m=top_row['ledges'][-1]['bench_m'], radius_m=sites.summit_radius(top_route, grid.cell),
                          foot_level_m=srows[0]['foot_level_m'],
                          height_m=round(top_row['ledges'][-1]['level_m']-srows[0]['foot_level_m'], 3),
                          reached_from=top_row['ledges'][-1]['reached_from'])
    # What the exact walk reaches of the island's dry land (the generator's lower bound: a 20 degree, 2 cm, dry, planted walk)
    land_m2 = sum(1 for q in range(grid.n) if land.inside[q] and land.wet[q] > 0 and not land.sea[q])*grid.cell**2
    node_m2 = w.t.cell**2
    coverage = dict(land_m2=round(land_m2, 1),
                    reached_m2={name: round(sum(v.histogram())*node_m2, 1) for name, v in variants.items()},
                    reached_m2_by_band=[round(n*node_m2, 1) for n in base.histogram()])
    counts = dict(placed=len(items), by_band=dict(sorted(by_band.items())), benches=len(benches),
                  benches_proved=sum(1 for b in benches if b['proved']),
                  benches_bypassed=sum(1 for r in route_rows for l in r['ledges'] if l.get('bypassed')),
                  routes=len(route_rows), first_run=sum(1 for it in items if it.get('reach_band') == 1),
                  out_of_reach=sum(1 for it in items if it.get('reach_band') != 1),
                  by_why={k: sum(1 for d in detail if d['why'] == k) for k in sorted({d['why'] for d in detail})},
                  budget=budget, reached_nodes={name: v.histogram() for name, v in variants.items()},
                  coverage=coverage)
    notes = list(plan.notes)
    if counts['placed'] < budget['min']:
        notes.append('%d Fractals placed, under the budget minimum of %d: the land offers no more places'
                     % (counts['placed'], budget['min']))
    if counts['out_of_reach'] < OUT_OF_REACH_MIN:
        notes.append('%d out of reach yet, under the %d the design asks for' % (counts['out_of_reach'], OUT_OF_REACH_MIN))
    return dict(typed=typed, budget=budget, items=items, detail=detail, routes=route_rows, benches=benches,
                shortcuts=shortcuts, natural=natural_ways(plan, variants), summit=summit_row, counts=counts,
                notes=notes,
                arithmetic=gates.describe_arithmetic())


# ------------------------------------------------------------------------------------------- the report


def report_text(block):
    """The reach report for the founder (markdown): what was placed at each band, every gate and shortcut, and where
    the arithmetic of the two kinds of gate gives out."""
    c = block['counts']
    out = ['# Reach report: Fractals and gates', '',
           'Placed %d Fractals (budget %d to %d): %d within a first-run body\'s reach (band 1, no knack; the pack says %d) and %d '
           'out of reach yet (ten or more); by the lowest band that reaches them: %s; by where they rest: %s.'
           % (c['placed'], block['budget']['min'], block['budget']['max'], c['first_run'],
              block['budget']['reachable_first_run'], c['out_of_reach'],
              ', '.join('band %s: %d' % (k, v) for k, v in c['by_band'].items()),
              ', '.join('%s %d' % kv for kv in c['by_why'].items())),
           '%d benches in %d routes, %d proved by the exact walk (the band each was built as is the band that reaches it)%s.'
           % (c['benches'], c['routes'], c['benches_proved'],
              '; %d reached sooner than built, by the land beside the summit corridor (marked below)' % c['benches_bypassed']
              if c.get('benches_bypassed') else ''), '']
    if block['notes']:
        out += ['Notes: '+'; '.join(block['notes'])+'.', '']
    out += ['## Knack shortcuts', '',
            'A higher ledge one climb knack at full rank (Big bar or Steady climber, +20 percent) opens a band sooner and the '
            'base body does not. Nothing is reachable only with a knack.', '',
            '| Route | Rise (m) | Base body | One knack | Strongest pair | Base falls short by | Knack clears by | Fractal |',
            '|---|---|---|---|---|---|---|---|']
    for s in block['shortcuts']:
        out.append('| %s | %.3f | band %s | band %s | band %s | %.3f m | %.3f m | %s |' % (
            s['route'], s['rise_m'], s['base_band'], s['one_knack_band'], s['pair_band'], s['base_falls_short_m'],
            s['knack_clears_by_m'], s['fractal']))
    out += ['', '## The gates', '',
            'A band gate is a rise of the band less %.2f m. The table says from which lower band a body stretched by a '
            'knack climbs it (nothing listed: no knack opens it early).' % gates.GATE_SLACK_M, '',
            '| Band | Reach (m) | Gate rise (m) | Opened from a lower band by |', '|---|---|---|---|']
    for g in block['arithmetic']['gates']:
        early = ', '.join('%s from band %s' % (k, v) for k, v in g['opened_from'].items()) or 'none'
        out.append('| %d | %.2f | %.2f | %s |' % (g['band'], g['cap_m'], g['gate_rise_m'], early))
    out += ['', '## Routes', '']
    for r in block['routes']:
        out.append('- **%s** (%s), %.1f m long, from ground at %.2f m:' % (r['id'], r['kind'], r['length_m'], r['foot_level_m']))
        for k, l in enumerate(r['ledges']):
            early = '' if r['kind'] == 'shortcut' or not l['opened_early_by'] else                 ', opened early by '+', '.join('%s (band %s)' % kv for kv in l['opened_early_by'].items())
            if r['kind'] == 'shortcut':
                early = ', one knack at full rank from band %s, the strongest pair from band %s' % (
                    l['reached_from']['one_knack'], l['reached_from']['pair'])
            out.append('  - ledge %d: rise %.2f m (needs band %d), bench at %s, base body from band %s%s%s' % (
                k+1, l['rise_m'], l['band'], l['bench_m'], l['reached_from']['base'],
                '' if l['proved'] else ' (NOT the band it was built as)', early))
    if block.get('natural'):
        out += ['', '## Ways up the land itself offers', '',
                'Climbs the exact walk found outside the built flights (a real room may offer shortcuts of its own geometry), one '
                'per place the climb ends: the rise, and the lowest band each body takes it from (none: not from any band).',
                '', '| Top at (x, y, z) | Rise (m) | Base body | One knack | Strongest pair |', '|---|---|---|---|---|']
        for n in block['natural']:
            r = n['reached_from']
            out.append('| %s | %.2f | %s | %s | %s |' % (n['top_m'], n['rise_m'], r.get('base', 'none'), r.get('one_knack', 'none'),
                                                        r.get('pair', 'none')))
    if block['summit']:
        s = block['summit']
        out += ['', '## The summit', '',
                'A corridor of %d ledges cut through %s%s, to %.2f m above its foot, %.1f m long; its top ledge needs band %d '
                'and the top is reached from band %s (one knack: %s, the strongest pair: %s). The island\'s own Fractals open '
                'up to band %d at most (every one of the budget\'s %d spent on a band, %d a band), so %s'
                % (s['cut_ledges'], s['landmark'],
                   ' and above its platform a stair of %d ledges built on its top' % s['built_ledges'] if s['built_ledges'] else '',
                   s['height_m'], s['length_m'], s['top_band'], s['reached_from']['base'], s['reached_from']['one_knack'],
                   s['reached_from']['pair'], s['own_band'], block['budget']['max'], block['budget'].get('per_band', gates.PER_BAND),
                   'the summit waits for a return visit with more capability, a Bubble lift or a built ladder.'
                   if s['top_band'] > s['own_band'] else
                   'the summit is within them (its corridor tops out below them and no stair above it fits the landmark\'s top); a '
                   'Bubble lift or a built ladder reach it sooner.')]
    if c.get('coverage'):
        cv = c['coverage']
        out += ['', '## How much of the island the exact walk reaches', '',
                'The generator proves its claims on a strict walk (20 degrees, 2 cm steps, dry, the planting in the way): it reaches '
                '%.0f of the island\'s %.0f m2 of dry land (%.0f percent) on the base bar, %.0f with one knack at full rank, %.0f '
                'with the strongest pair; by band on the base bar (m2): %s. The body walks 45 degrees, wades, and climbs any face '
                'steeper than it stands on, so the claim is a lower bound: `python -B -m pipeline.landscape.generator.coverage` '
                'measures the same island with the body\'s own limits.'
                % (cv['reached_m2']['base'], cv['land_m2'], 100.*cv['reached_m2']['base']/cv['land_m2'], cv['reached_m2']['one_knack'],
                   cv['reached_m2']['pair'], ', '.join('%d: %.0f' % (k+1, v) for k, v in enumerate(cv['reached_m2_by_band']) if v))]
    return '\n'.join(out)+'\n'


def summit_record(block):
    """The package's `x_generator.summit` (what the exporter writes as the manifest's `x_summit`): the summit's top bench
    (`center_m`, on its ground), `radius_m` covering that top, the landmark's id when it is a token, and the band its
    highest ledge needs. None for an island without a summit."""
    s = block.get('summit')
    if not s:
        return None
    rec = dict(center_m=list(s['top_m']), radius_m=s['radius_m'], top_band=s['top_band'])
    if re.fullmatch(r'[a-z][a-z0-9_-]{0,63}', s['landmark'] or ''):
        rec['landmark'] = s['landmark']
    return dict(sorted(rec.items()))
