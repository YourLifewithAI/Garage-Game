"""Reach by foot and by climb: where a body of each band gets, proved by the exact walk.

Feet. `checks.Walker` floods the lattice from the spawn: a node is standable when an 11 cm disc round it is dry, clear
of obstacles, inside the floor and on faces of 20 degrees or less, and every edge is sampled each centimetre for its
rise. That flood is the walk, exact; nothing here relaxes it.

Climbs. A climb is one more kind of edge. From a standable foot a face steeper than 55 degrees (the climb rule,
`climb.climb_from`) is followed up its fall line, no longer 0.6 m but as far as the tallest bar climbs, to its crest; a
standable node within a body's reach of the crest is its top. A climb costs bar, and a metre of bar buys the pack's
`climb_m_per_bar_m` of rise (1.5), so a climb is open to a bar of band b when its rise is at most what that bar climbs
(`gates.cap`: the bar's length times 1.5; before Run 4's BODY-21 the gates read a metre of rise a metre of bar, and the
game's body topped every gate from a band or two below). Stamina refills on standable ground, so the bar is full at
every foot: a climb is judged alone, never added to the one before.

The answer is a label per node, the lowest band from which it is reached: band 1 (the starting bar) for everything
the feet reach with no climb. Bands are flooded in turn, each from the tops the band's climbs open, so every node is
visited once. A body stretched by a knack (`gates.VARIANTS`) is the same flood with each bar's reach multiplied.

What is not modelled, so the answer is a lower bound for the body and every gate claim is made against the same
walker: slopes between 20 and 55 degrees (the body walks to 45 and scrambles above 35), shallow water (the body wades),
drops (a fall down is free of stamina), a hop onto a ledge under 6.5 cm, a tree's trunk and crown, the Gubble's help.
"""
import math
from array import array
from collections import deque

from . import climb, gates
from .climb import FACE_DEG, FOOT_DEG
from .field import face_slope_max

TOP_RISE_M = gates.cap(gates.BANDS, gates.VARIANTS[-1][1])   # the tallest rise anyone climbs: the strongest pair, top band (8.64 m)
# How far a climb is followed along its heading: far enough that the follow always ends at the tallest rise first. Past
# its first steep face a climb goes on over any ground steeper than a lip (`climb.LIP_DEG`, 35 degrees), where a rise of
# TOP_RISE_M runs 12.3 m along the ground. It was 9 m while the tallest rise was 5.76 m (8.2 m of run); tests/test_fractals.py
# holds the two together, so a pack that stretches the climb cannot cut a tall face short unseen.
MAX_RUN_M = 13.
FOOT_RADIUS_M = .13     # a foot's standable node lies within this of the foot (the disc keeps a node back from a face)
FOOT_TOL_M = .03        # ... on ground within this of the foot's height
TOP_SEARCH = 7          # ... a top's, of the crest
REACH_CELLS = (-2, -1, 0, 1, 2)   # a node opens the steep cells within this many cells (a node stands up to 0.13 m from a foot)
TOP_TOL_M = .08         # a top's node stands within this of the crest's height
N8 = ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1))


class Climb:
    """A climb edge: from the foot's node to the top's node, `rise` metres."""
    __slots__ = ('foot', 'top', 'rise', 'foot_xz', 'top_xz')

    def __init__(self, foot, top, rise, foot_xz, top_xz):
        self.foot, self.top, self.rise, self.foot_xz, self.top_xz = foot, top, rise, foot_xz, top_xz


class Candidate:
    """A steep cell's climb, found at the cell's foot; `result` is None until traced, False if it is no climb."""
    __slots__ = ('foot', 'foot_xz', 'foot_y', 'dir', 'result')

    def __init__(self, foot, foot_xz, foot_y, direction):
        self.foot, self.foot_xz, self.foot_y, self.dir, self.result = foot, foot_xz, foot_y, direction, None


class Labels:
    """The lowest band each lattice node is reached from, for one body (a stretch factor)."""

    def __init__(self, reach, factor, parent, label, climbs):
        self.reach, self.walker, self.factor = reach, reach.w, factor
        self.parent, self.label, self.climbs = parent, label, climbs   # climbs: [(Climb, band)] in the order opened

    def node(self, x, z):
        w = self.walker
        return w.node(w.key(x, z))

    def band_at(self, x, z):
        """The lowest band from which a body stands at (x, z), or None (not standable there, or never reached)."""
        n = self.node(x, z)
        if n is None or not self.label[n]:
            return None
        return self.label[n]

    def histogram(self):
        counts = [0]*(gates.BANDS+1)
        for b in self.label:
            counts[b] += 1
        return counts[1:]


class ClimbReach:
    """The climb-aware reach of one land: `walker` (a checks.Walker), its flood `reach0` from the spawn, the base grid
    `grid` with heights `h`. `fslope` (per vertex, the steepest face: field.face_slope_max) is worked out if not given;
    `skip(i, j)` says which cells are not read (the 10x world's detail blocks: their faces are not the grid's).
    `face_deg` is the steepness of a face a climber grabs: the climb rule's 55 degrees, or the body's own 45 (any face past
    what it stands on), for the body's own reach (`coverage.py`)."""

    def __init__(self, walker, reach0, grid, h, fslope=None, skip=None, face_deg=FACE_DEG):
        self.w, self.reach0, self.grid, self.h = walker, reach0, grid, h
        self.face_deg = face_deg
        if fslope is None:
            fslope = face_slope_max(grid, h, grid.triangles())
        nx, nz = grid.nx, grid.nz
        self.nx, self.nz = nx, nz
        # Steep cells (a cell is named by its lower-left vertex), and the cells within one of them.
        steep = bytearray(nx*nz)
        for q, s in enumerate(fslope):
            if s > face_deg:
                i, j = q % nx, q//nx
                for ci in (i-1, i):
                    for cj in (j-1, j):
                        if 0 <= ci < nx-1 and 0 <= cj < nz-1 and not (skip and skip(ci, cj)):
                            steep[cj*nx+ci] = 1
        near = bytearray(nx*nz)
        for q in range(nx*nz):
            if steep[q]:
                i, j = q % nx, q//nx
                for dj in REACH_CELLS:
                    row = (j+dj)*nx
                    if 0 <= j+dj < nz:
                        for di in REACH_CELLS:
                            if 0 <= i+di < nx:
                                near[row+i+di] = 1
        self.steep, self.near = steep, near
        self.slope_at = climb._cell_points(grid, h)
        self._cands = {}
        self._init = None   # the candidates the first flood's nodes open, found once

    # ---------------------------------------------------------------- candidates
    def _foot_node(self, x, z, y):
        """The standable node a body stands on to start a climb at the foot (x, z, y): within FOOT_RADIUS_M, the nearest
        whose way to the foot is ground of 30 degrees or less at the foot's height (the disc and the lattice keep a
        node's centre a little back from a face's base)."""
        w, grid, h = self.w, self.grid, self.h
        c = w.t.cell
        ki, kj = int(round(x/c)), int(round(z/c))
        reach = int(math.ceil(FOOT_RADIUS_M/c))
        found = []
        for di in range(-reach, reach+1):
            for dj in range(-reach, reach+1):
                d = math.hypot((ki+di)*c-x, (kj+dj)*c-z)
                if d <= FOOT_RADIUS_M+1e-9:
                    found.append((d, ki+di, kj+dj))
        for d, i, j in sorted(found):
            n = w.node((i, j))
            if n is None or not w._stand(n):
                continue
            ok = True
            for t in range(1, 6):
                px, pz = x+(i*c-x)*t/6, z+(j*c-z)*t/6
                hit = climb.tri_hit(grid, h, px, pz)
                if hit is None or hit[1] > FOOT_DEG or abs(hit[0]-y) > FOOT_TOL_M:
                    ok = False
                    break
            if ok:
                return n
        return None

    def candidates(self, cell):
        """The climbs a steep cell starts: for each of its two faces over 55 degrees, the foot straight back down the
        fall line within 5 cm on ground of 30 degrees or less, if a standable node lies there."""
        got = self._cands.get(cell)
        if got is not None:
            return got
        grid, h = self.grid, self.h
        nx = self.nx
        i, j = cell % nx, cell//nx
        c = grid.cell
        out = []
        for p, (fu, fv) in enumerate(climb._POINTS):
            if self.slope_at(i, j, p) <= self.face_deg:
                continue
            x, z = grid.xs[i]+c*fu, grid.zs[j]+c*fv
            up = climb._uphill(grid, h, x, z)
            if up is None:
                continue
            dx, dz = up
            foot = None
            for back in (.01, .02, .03, .04, .05):
                fx, fz = x-dx*back, z-dz*back
                hit = climb.tri_hit(grid, h, fx, fz)
                if hit is None:
                    break
                if hit[1] <= FOOT_DEG:
                    foot = (fx, hit[0], fz)
                    break
            if foot is None:
                continue
            node = self._foot_node(foot[0], foot[2], foot[1])
            if node is None:
                continue
            out.append(Candidate(node, (foot[0], foot[2]), foot[1], (dx, dz)))
        self._cands[cell] = out
        return out

    def _trace(self, cand):
        """Follow the candidate's face to its crest and find the top (the result is kept on the candidate)."""
        r = climb.climb_from(self.grid, self.h, cand.foot_xz[0], cand.foot_xz[1], cand.dir[0], cand.dir[1],
                             cand.foot_y, None, reach=MAX_RUN_M, max_rise=TOP_RISE_M, face_deg=self.face_deg)
        cand.result = False
        if r is None or r['rise'] < climb.MIN_RISE:
            return
        top = self._top_node(r['crest'][0], r['crest'][1], r['crest_y'])
        if top is not None:
            w = self.w
            cx, cz = w.ij(top)
            cand.result = Climb(cand.foot, top, r['rise'], cand.foot_xz, (cx*w.t.cell, cz*w.t.cell))

    def _top_node(self, x, z, y):
        w = self.w
        c = w.t.cell
        ki, kj = int(round(x/c)), int(round(z/c))
        for rad in range(TOP_SEARCH+1):
            for di in range(-rad, rad+1):
                for dj in range(-rad, rad+1):
                    if max(abs(di), abs(dj)) != rad:
                        continue
                    n = w.node((ki+di, kj+dj))
                    if n is None or abs(w.height(n)-y) > TOP_TOL_M:
                        continue
                    if w._stand(n):
                        return n
        return None

    # ---------------------------------------------------------------- activation: which steep cells a flood has reached
    def _open_cells(self, nodes, seen, opened):
        """Candidates of the steep cells beside `nodes` (a cell is opened once per run)."""
        w, grid, nx, nz = self.w, self.grid, self.nx, self.nz
        c, i0, j0, nj = w.t.cell, w.i0, w.j0, w.nj
        gx0, gz0, gc = grid.x0, grid.z0, grid.cell
        near, steep = self.near, self.steep
        out = []
        floor = math.floor
        for n in nodes:
            i, j = divmod(n, nj)
            ci = int(floor(((i+i0)*c-gx0)/gc))
            cj = int(floor(((j+j0)*c-gz0)/gc))
            if not (0 <= ci < nx-1 and 0 <= cj < nz-1):
                continue
            key = cj*nx+ci
            if not near[key] or key in seen:
                continue
            seen.add(key)
            for dj in REACH_CELLS:
                for di in REACH_CELLS:
                    cell = (cj+dj)*nx+ci+di
                    if 0 <= cj+dj < nz-1 and 0 <= ci+di < nx-1 and steep[cell] and cell not in opened:
                        opened.add(cell)
                        out.extend(self.candidates(cell))
        return out

    def _flood(self, parent, label, queue, band, new):
        w = self.w
        pop, push = queue.popleft, queue.append
        state, stand, pass_ = w.state, w._stand, w._pass
        nj, vouch = w.nj, w.vouch
        steps = tuple(enumerate(zip(w.d8, N8)))
        while queue:
            k = pop()
            free = vouch and w._is_safe(k)
            for d, (step, (di, dj)) in steps:
                n = k+step
                if parent[n] != -2:
                    continue
                s_ = state[n]
                if not (s_ == 1 if s_ else stand(n)):
                    continue
                if di and dj:
                    o = k+di*nj
                    s_ = state[o]
                    if not (s_ == 1 if s_ else stand(o)):
                        continue
                    o = k+dj
                    s_ = state[o]
                    if not (s_ == 1 if s_ else stand(o)):
                        continue
                if not (free or pass_(k, d)):
                    continue
                parent[n] = k
                label[n] = band
                new.append(n)
                push(n)

    # ---------------------------------------------------------------- a run
    def run(self, factor=1., bands=gates.BANDS):
        """Label every node by the lowest band a body stretched by `factor` reaches it from. Returns Labels."""
        w = self.w
        parent = array('i', self.reach0.parent)
        label = bytearray(len(parent))
        first = []
        for n, p in enumerate(parent):
            if p != -2:
                label[n] = 1
                first.append(n)
        if self._init is None:
            seen0, opened0 = set(), set()
            self._init = (self._open_cells(first, seen0, opened0), seen0, opened0)
        # The first flood's candidates are known (found once): a run starts from them and opens only what its
        # climbs reach.
        pending, seen, opened = list(self._init[0]), set(self._init[1]), set(self._init[2])
        used = []
        for band in range(1, bands+1):
            limit = gates.cap(band, factor)
            while True:
                seeds, rest = [], []
                for cand in pending:
                    if cand.result is False:
                        continue
                    if parent[cand.foot] == -2:
                        rest.append(cand)
                        continue
                    if cand.result is None:
                        self._trace(cand)
                        if cand.result is False:
                            continue
                    c_ = cand.result
                    if parent[c_.top] != -2:
                        continue    # its top is already reached: the climb opens nothing
                    if c_.rise <= limit+gates.EPS:
                        seeds.append(c_)
                    else:
                        rest.append(cand)
                pending = rest
                if not seeds:
                    break
                queue, new = deque(), []
                for c_ in seeds:
                    if parent[c_.top] == -2:
                        parent[c_.top] = -1
                        label[c_.top] = band
                        new.append(c_.top)
                        queue.append(c_.top)
                        used.append((c_, band))
                self._flood(parent, label, queue, band, new)
                pending.extend(self._open_cells(new, seen, opened))
        return Labels(self, factor, parent, label, used)

    def variants(self):
        """Labels for every variant of the body (`gates.VARIANTS`): {name: Labels}."""
        return {name: self.run(factor) for name, factor in gates.VARIANTS}
