"""The island's firepits: the generator chooses them, the kernel reads them (MECHANICS.md decisions 62 and 74).

Firepits are spread evenly over any island (decision 74): one per 250 m2 of standable dry land, at least three and at
most twelve, at least 4 m apart and 1 m from any Fractal. The kernel spread them itself until now (CommandHostCamps.cs,
`CampLaws.SpreadFirepits`: Lloyd's relaxation over a 1.5 m grid of the land it probes); the generator knows the island
better, so it chooses them here and the exporter hands them to the game as the manifest's `x_firepits`:

    - the land is the base grid's standable dry ground (dry, over the sea, no face steeper than the kernel's 35 degrees, no
      cottage on it; the coast's headlands past the walls too, as the kernel's probe counts them), sampled every SAMPLE_M;
      its area sets the count, as the kernel's rule does;
    - a firepit stands where the generator's strict walk would stand a body (checks.Walker: an 11 cm disc, dry, gentle,
      clear of every trunk, stone and building), never on a flight, a path, a cottage's pad or the jetty's landing, at
      least FRACTAL_CLEAR_M from every Fractal and START_CLEAR_M from the starting campfire (the kernel's, 0.2 m in front
      of the player's spawn: START_CLEAR_M and that 0.2 m from the spawn, whichever way it faces);
    - one stands near the hamlet if the island has one (the nearest such ground to it, clear of its cottages);
    - the rest are spread evenly: farthest-point seeds, then SPREAD_ROUNDS of Lloyd's relaxation over the land (the
      starting campfire and the hamlet's firepit fixed sites), each firepit then on the ground of its own share nearest
      that share's centre, ground a first-run body reaches (band 1 of the climb-aware reach, reach.py) counting
      REACH_PULL_M nearer than it is, so a firepit takes reachable ground where its share offers some, and every two
      APART_M apart;
    - each says why it stands there: its reach band, the hamlet, the landmark it stands on or by.

No random number anywhere: the same island always gives the same firepits in the same order (nearest the start first),
which is the order the kernel numbers them in (firepit:1, firepit:2, ...).
"""
import math

AREA_PER_FIREPIT_M2 = 250.    # the kernel's CampLaws.AreaPerFirepitM2 (decision 74)
MIN_FIREPITS = 3              # CampLaws.MinFirepits
MAX_FIREPITS = 12             # CampLaws.MaxFirepitsByArea
APART_M = 4.                  # two firepits stand at least this far apart
FRACTAL_CLEAR_M = 1.          # ... and this far from any Fractal (CampLaws.FirepitFractalClearM)
START_CLEAR_M = 3.            # ... and from the starting campfire (the kernel's own)
STARTING_FIRE_AHEAD_M = .2    # the starting campfire burns this far in front of the spawn (CampLaws.StartingFireAheadM)
STANDABLE_DEG = 35.           # land: no face steeper than the kernel's standable ground (CommandHostCamps.StandableGround)
SAMPLE_M = .5                 # the land is sampled this finely (each sample standing for its square)
SPREAD_ROUNDS = 24            # Lloyd's relaxation, as the kernel's CampLaws.SpreadIterations
REACH_PULL_M = 5.             # first-run ground counts this much nearer a share's centre than it is
HAMLET_NEAR_M = 3.            # the hamlet's firepit lies within this of the hamlet ...
HAMLET_CLEAR_M = .3           # ... and this clear of each cottage's walls
JETTY_CLEAR_M = .5            # no firepit on the jetty's landing
PATH_CLEAR = .05              # a path's paint at or above this is a path (the flights' and the hamlet's rule)
LANDMARK_NEAR_M = 3.          # a firepit is "by" a landmark within this of its footprint


def count_for(land_m2):
    """How many firepits land of this area gets (decision 74, the kernel's CampLaws.FirepitCountFor: its Math.Round rounds a
    half to even, as Python's round does)."""
    if land_m2 <= 0:
        return 0
    return max(MIN_FIREPITS, min(MAX_FIREPITS, int(round(land_m2/AREA_PER_FIREPIT_M2))))


def start_clear_m():
    """How far from the player's spawn a firepit stands at least: START_CLEAR_M from the starting campfire whichever way the
    spawn faces (the exporter turns the spawn toward the nearest door or crate, and the kernel lights the starting campfire
    STARTING_FIRE_AHEAD_M in front of it)."""
    return START_CLEAR_M+STARTING_FIRE_AHEAD_M


class Spot:
    """A candidate firepit: where it stands (on a node of the walk), the lowest band reaching it (None: none), and the
    landmark it stands on or by."""
    __slots__ = ('x', 'y', 'z', 'band', 'landmark', 'on')

    def __init__(self, x, y, z, band, landmark=None, on=False):
        self.x, self.y, self.z, self.band, self.landmark, self.on = x, y, z, band, landmark, on

    def key(self, cx, cz):
        """How well it sits a share whose centre is (cx, cz): nearer is better, first-run ground REACH_PULL_M nearer."""
        return math.hypot(self.x-cx, self.z-cz)-(REACH_PULL_M if self.band == 1 else 0.)


def _box_distance(o, x, z):
    """How far (x, z) lies outside an object's footprint (0 inside it)."""
    a = math.radians(o['yaw'])
    c, s = math.cos(a), math.sin(a)
    dx, dz = x-o['cx'], z-o['cz']
    lx, lz = c*dx-s*dz, s*dx+c*dz
    return math.hypot(max(abs(lx)-o['sx']/2, 0.), max(abs(lz)-o['sz']/2, 0.))


def _cottage_distance(hh, x, z):
    """How far (x, z) lies outside a cottage's footprint (its half sizes hx, hz turned by its yaw, as generate.py lays it)."""
    a = math.radians(hh['yaw'])
    c, s = math.cos(a), math.sin(a)
    dx, dz = x-hh['x'], z-hh['z']
    lx, lz = c*dx-s*dz, s*dx+c*dz
    return math.hypot(max(abs(lx)-hh['hx'], 0.), max(abs(lz)-hh['hz'], 0.))


def landmark_of(objects, x, z):
    """(id, on) of the landmark nearest (x, z) within LANDMARK_NEAR_M (an object standing on the floor), or (None, False)."""
    best = None
    for o in objects:
        if o.get('parent') is not None:
            continue
        d = _box_distance(o, x, z)
        if d <= LANDMARK_NEAR_M and (best is None or (d, o['id']) < best):
            best = (d, o['id'])
    return (None, False) if best is None else (best[1], best[0] == 0.)


def land_and_spots(grid, h, fslope, inside, wet, sea, cottage, avoid, walker, labels, objects, start, fractals, sea_y):
    """The island's land for the firepits and the spots one may stand on.

    `grid`, `h`, `fslope` (per vertex, the steepest face), `inside` (inside the room's walls, where the walk's floor is),
    `wet` (0 in water), `sea` and `cottage` (a cottage stands here) are the base grid's; `avoid(q, x, z)` says a vertex is no place for a firepit (a flight, a path, a pad,
    the jetty's landing); `walker` is the strict walk over the island as written (checks.Walker) and `labels` its
    climb-aware reach (reach.Labels, base body); `objects` the landmarks (parse_objects); `start` the player's spawn
    (x, z); `fractals` the Fractals' (x, z). Returns (land [(x, z)], the area it stands for in m2, spots [Spot])."""
    k = max(1, int(round(SAMPLE_M/grid.cell)))
    nx = grid.nx
    cell_m2 = (k*grid.cell)**2
    c = walker.t.cell
    clear = start_clear_m()
    land, spots = [], []
    for j in range(k//2, grid.nz, k):
        for i in range(k//2, nx, k):
            q = j*nx+i
            if sea[q] or wet[q] <= 0 or h[q] <= sea_y+.005 or fslope[q] > STANDABLE_DEG or cottage[q]:
                continue
            x, z = grid.xs[i], grid.zs[j]
            land.append((x, z))
            # Land past the walls (the coast's headlands) is spread over, but the walk stands a body only inside them.
            if not inside[q] or avoid(q, x, z) or math.hypot(x-start[0], z-start[1]) < clear+c:
                continue
            key = walker.key(x, z)
            if not walker.standable(key):
                continue
            # On the walk's own node, so the claims (standable, its band) hold exactly where it is written.
            x, z = key[0]*c, key[1]*c
            if math.hypot(x-start[0], z-start[1]) < clear or \
                    any(math.hypot(x-a, z-b) < FRACTAL_CLEAR_M for a, b in fractals):
                continue
            y = walker.t.height(x, z)
            if y != y:
                y = grid.tri_height(h, x, z)
            ident, on = landmark_of(objects, x, z)
            spots.append(Spot(x, y, z, labels.band_at(x, z), ident, on))
    return land, len(land)*cell_m2, spots


def _nearest(points, x, z):
    """Index of the point nearest (x, z), the first on a tie."""
    best, at = math.inf, -1
    for n, (a, b) in enumerate(points):
        d = (a-x)*(a-x)+(b-z)*(b-z)
        if d < best:
            best, at = d, n
    return at


def spread(land, spots, fixed, count, taken=()):
    """`count` firepits spread evenly over `land` [(x, z)] among `spots`, the sites in `fixed` [(x, z)] (the starting
    campfire, the hamlet's firepit) held where they are: farthest-point seeds from the fixed sites, SPREAD_ROUNDS of
    Lloyd's relaxation, then each on the spot of its own share that sits it best (`Spot.key`) at least APART_M from
    every firepit already chosen and from `taken` (the hamlet's firepit), any spot when its share has none. Returns
    ([Spot], [the share centres])."""
    if count <= 0 or not spots:
        return [], []
    # Seeds: the spot farthest from the fixed sites and the seeds so far, the first listed on a tie.
    seeds = []
    far = [min([math.hypot(s.x-a, s.z-b) for a, b in fixed] or [math.inf]) for s in spots]
    for _ in range(min(count, len(spots))):
        at = max(range(len(spots)), key=lambda n: (far[n], -n))
        seeds.append((spots[at].x, spots[at].z))
        far = [min(f, math.hypot(s.x-seeds[-1][0], s.z-seeds[-1][1])) for f, s in zip(far, spots)]
    sites = list(seeds)
    nf = len(fixed)
    for _ in range(SPREAD_ROUNDS):
        sums = [[0., 0., 0] for _ in sites]
        every = list(fixed)+sites
        for x, z in land:
            n = _nearest(every, x, z)-nf
            if n >= 0:
                acc = sums[n]
                acc[0] += x
                acc[1] += z
                acc[2] += 1
        sites = [(a/m, b/m) if m else s for (a, b, m), s in zip(sums, sites)]
    every = list(fixed)+sites
    share = [_nearest(every, s.x, s.z)-nf for s in spots]
    chosen = []
    near = list(taken)
    for n, (cx, cz) in enumerate(sites):
        pick = None
        for own in (True, False):
            for k, s in enumerate(spots):
                if (own and share[k] != n) or any(math.hypot(s.x-a, s.z-b) < APART_M for a, b in near):
                    continue
                if pick is None or s.key(cx, cz) < pick.key(cx, cz):
                    pick = s
            if pick is not None:
                break
        if pick is not None:
            chosen.append(pick)
            near.append((pick.x, pick.z))
    return chosen, sites


def hamlet_spot(spots, hamlet, houses):
    """The firepit near the hamlet: of the spots within HAMLET_NEAR_M of it and HAMLET_CLEAR_M clear of every cottage, the
    nearest to it (first-run ground first). None when there is none."""
    if hamlet is None:
        return None
    hx, hz = hamlet
    near = [s for s in spots if math.hypot(s.x-hx, s.z-hz) <= HAMLET_NEAR_M and
            all(_cottage_distance(hh, s.x, s.z) >= HAMLET_CLEAR_M for hh in houses)]
    if not near:
        return None
    return min(near, key=lambda s: (s.band != 1, math.hypot(s.x-hx, s.z-hz)))


def _why(spot, hamlet):
    if spot.band == 1:
        reach = 'first-run reach'
    elif spot.band is not None:
        reach = 'reached from band %d' % spot.band
    else:
        reach = "beyond the strict walk's reach"
    out = (['near the hamlet'] if hamlet else [])+[reach]
    if spot.landmark is not None:
        out.append(('on ' if spot.on else 'by ')+spot.landmark)
    return ', '.join(out)


def choose(land, area_m2, spots, start, hamlet=None, houses=()):
    """The package block (`x_generator.firepits`) from the land, its area, the spots (land_and_spots), the player's spawn
    (x, z), where the starting campfire burns, and the hamlet (x, z) with its cottages, or None."""
    wanted = count_for(area_m2)
    notes = []
    near_hamlet = hamlet_spot(spots, hamlet, houses) if wanted else None
    if hamlet is not None and wanted and near_hamlet is None:
        notes.append('no ground near the hamlet for a firepit')
    fixed = [start]+([(near_hamlet.x, near_hamlet.z)] if near_hamlet else [])
    taken = [(near_hamlet.x, near_hamlet.z)] if near_hamlet else []
    picked, _ = spread(land, spots, fixed, wanted-len(taken), taken)
    if near_hamlet:
        picked.append(near_hamlet)
    if len(picked) < wanted:
        notes.append('%d firepits placed, under the %d the land asks for: no more ground stands %.0f m from the others'
                     % (len(picked), wanted, APART_M))
    picked.sort(key=lambda s: (math.hypot(s.x-start[0], s.z-start[1]), s.x, s.z))
    items = []
    for s in picked:
        item = dict(position_m=[round(s.x, 4), round(s.y, 4), round(s.z, 4)], why=_why(s, s is near_hamlet))
        if s.band is not None:
            item['reach_band'] = s.band
        if s.landmark is not None:
            item['landmark'] = s.landmark
        if s is near_hamlet:
            item['hamlet'] = True
        items.append(item)
    pits = [(s.x, s.z) for s in picked]
    pairs = [math.hypot(a[0]-b[0], a[1]-b[1]) for n, a in enumerate(pits) for b in pits[n+1:]]
    farthest = max((min(math.hypot(x-a, z-b) for a, b in pits) for x, z in land), default=None) if pits else None
    camps = pits+[start]
    farthest_camp = max((min(math.hypot(x-a, z-b) for a, b in camps) for x, z in land), default=None)
    counts = dict(land_m2=round(area_m2, 1), wanted=wanted, placed=len(items), spots=len(spots),
                  first_run=sum(1 for s in picked if s.band == 1),
                  nearest_pair_m=round(min(pairs), 3) if pairs else None,
                  farthest_land_m=None if farthest is None else round(farthest, 3),
                  farthest_land_from_a_campfire_m=None if farthest_camp is None else round(farthest_camp, 3))
    rule = dict(area_per_firepit_m2=AREA_PER_FIREPIT_M2, min=MIN_FIREPITS, max=MAX_FIREPITS, apart_m=APART_M,
                fractal_clear_m=FRACTAL_CLEAR_M, start_clear_m=START_CLEAR_M, spawn_clear_m=start_clear_m(), standable_deg=STANDABLE_DEG,
                sample_m=SAMPLE_M, rounds=SPREAD_ROUNDS, reach_pull_m=REACH_PULL_M)
    return dict(items=items, counts=counts, rule=rule, spawn_m=[round(start[0], 4), round(start[1], 4)], notes=notes)


def report_line(block):
    """One line for the log and the founder: how many, how far apart, how far the land lies from them."""
    c = block['counts']
    return ('placed %d of %d over %.0f m2 (%d within first-run reach), the nearest two %s m apart, every land point within %s m '
            'of a firepit (%s m of a campfire, the start included)%s' % (
                c['placed'], c['wanted'], c['land_m2'], c['first_run'], c['nearest_pair_m'], c['farthest_land_m'],
                c['farthest_land_from_a_campfire_m'], '; notes: '+'; '.join(block['notes']) if block['notes'] else ''))
