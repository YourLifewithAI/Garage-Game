"""Principle checks on a written package (not on the generator's memory).

    python -m pipeline.landscape.generator.checks --package <folder> --room <room folder>

Rebuilds the terrain height grid from the package's own meshes, then checks:
water level and downhill; nothing built floating or sunk; the walk from the
player's spawn to the carryable object and back within the low-incline and
step limits (an 8-connected path search on the terrain's vertices, where a
vertex is walkable only if every triangle touching it is gentle enough); and
every inventory object's footprint still reads in the land; and the island:
the sea surrounds it out past the reef line (the shelf's edge), which lies a
short swim off the coast; nothing stands offshore near the surface (no reef
rocks, no sea stacks); every promised beach has a shelving shore a swimmer
walks out of the sea up, and the beaches and the jetty are reachable on foot
from the spawn.
"""
import argparse
import heapq
import json
import math
from array import array
from bisect import bisect_right
from collections import deque
from pathlib import Path

from pipeline.landscape.harness import read_package
from pipeline.landscape.harness.common import load_json
from pipeline.landscape.harness.kit import SIZES
from . import scale as world

WALK_DEG = 20.     # low incline, well under the body's 45 degrees
CARRY_DEG = 20.
STEP_M = .02       # the body's step-up
BUILT = {'cottage', 'tower', 'fence', 'crate', 'lantern', 'woodpile', 'drying_line', 'footbridge'}


def _face_slope(a, b, c):
    """A triangle's slope in degrees (the one formula `Terrain.hit` reports)."""
    ux, uy, uz = b[0]-a[0], b[1]-a[1], b[2]-a[2]
    vx, vy, vz = c[0]-a[0], c[1]-a[1], c[2]-a[2]
    nx_, ny_, nz_ = uy*vz-uz*vy, uz*vx-ux*vz, ux*vy-uy*vx
    return math.degrees(math.acos(min(1., abs(ny_)/(math.sqrt(nx_*nx_+ny_*ny_+nz_*nz_) or 1e-30))))


def world_scale(doc):
    """The world scale a package (or a generator probe) was built at: its
    x_generator.world_scale, else this process's (generator/scale.py)."""
    return (doc.get('x_generator') or {}).get('world_scale', world.W)


def walk_lattice(doc):
    """The walk's lattice: 2.5 cm on today's room; on a magnified room the
    body's limits are kept (the 11 cm disc, 2 cm steps sampled each
    centimetre, 20 degrees) on a 5 cm lattice, a quarter of the nodes."""
    return .025 if world_scale(doc) == 1 else WALK_LATTICE_W


_SUB = 4          # sub-buckets per bucket side (candidate lists for `hit`)
_BLOCK = 8        # buckets per block side, judged together (`Terrain.plain_slope`)
_MISSING = object()
_BROKEN = object()


class Terrain:
    """The decoded package surface: topmost triangle under a point, from the
    package's own terrain (and scenery) meshes, with no knowledge of how the
    generator built them. `cell` is the sampling step the checks use.

    Speed without changing an answer: each bucket's triangles (in their
    order) are narrowed to the ones whose box, widened past the barycentric
    tolerance, reaches a quarter-bucket; a triangle's slope is worked out
    once. `plain_slope` vouches for a whole bucket of the package's own land
    grid (read back from its mesh and `x_generator.cell_m`): every point there
    hits the land, nothing else on top, no face steeper than the value; the
    walker skips sampling such ground (`Walker.passable`). On the 10x world
    the land's 2.5 cm detail blocks (`x_generator.detail.cell_m`, the same
    lattice subdivided) are vouched for the same way, cell by fine cell; the
    seams between them and the base (fans) are read in full."""

    def __init__(self, doc, meshes, cell=None, bucket=.1, only=None):
        self.cell = cell if cell is not None else walk_lattice(doc)
        self.bucket = bucket
        self.tris = []      # (a, b, c, det, box x0, x1, z0, z1, land)
        self.grid = {}
        self.win = win = window(doc)
        grid, tris = self.grid, self.tris
        floor = math.floor
        only_rows = {}
        for i, j in only or ():
            only_rows.setdefault(j, set()).add(i)
        # A triangle whose corners lie in neighbouring buckets reaches no bucket
        # beyond its first corner's neighbours: far from `only`, it is passed by.
        near = None if only is None else {(i+di, j+dj) for i, j in only for di in (-1, 0, 1) for dj in (-1, 0, 1)}
        self.land = None
        for rec in doc['terrain']+doc['scenery']:
            is_land = rec['mesh'] == 'land'
            for p in meshes[rec['mesh']]:
                vs = p['positions']
                if near is not None:
                    vb = [(int(floor(v[0]/bucket)), int(floor(v[2]/bucket))) for v in vs]
                for t in p['triangles']:
                    if near is not None:
                        (ia, ja), (ib, jb), (ic, jc) = vb[t[0]], vb[t[1]], vb[t[2]]
                        if (ia, ja) not in near and -1 <= ib-ia <= 1 and -1 <= ic-ia <= 1 and -1 <= jb-ja <= 1 \
                                and -1 <= jc-ja <= 1:
                            continue
                    a, b, c = vs[t[0]], vs[t[1]], vs[t[2]]
                    det = (b[2]-c[2])*(a[0]-c[0])+(c[0]-b[0])*(a[2]-c[2])
                    if abs(det) < 1e-12:
                        continue
                    ax, az, bx, bz, cx, cz = a[0], a[2], b[0], b[2], c[0], c[2]
                    x0 = ax if ax < bx else bx
                    x0 = x0 if x0 < cx else cx
                    x1 = ax if ax > bx else bx
                    x1 = x1 if x1 > cx else cx
                    z0 = az if az < bz else bz
                    z0 = z0 if z0 < cz else cz
                    z1 = az if az > bz else bz
                    z1 = z1 if z1 > cz else cz
                    if win is not None:
                        if x1 < win[0] or x0 > win[2] or z1 < win[1] or z0 > win[3]:
                            continue
                        i0, i1 = int(floor(max(x0, win[0])/bucket)), int(floor(min(x1, win[2])/bucket))
                        j0, j1 = int(floor(max(z0, win[1])/bucket)), int(floor(min(z1, win[3])/bucket))
                    else:
                        i0, i1, j0, j1 = int(floor(x0/bucket)), int(floor(x1/bucket)), int(floor(z0/bucket)), \
                            int(floor(z1/bucket))
                    if only is not None:
                        keys = [(i, j) for j in range(j0, j1+1) if j in only_rows
                                for i in range(i0, i1+1) if i in only_rows[j]]
                        if not keys:
                            continue
                    else:
                        keys = [(i, j) for j in range(j0, j1+1) for i in range(i0, i1+1)]
                    k = len(tris)
                    tris.append((a, b, c, det, x0, x1, z0, z1, is_land))
                    for key in keys:
                        cell_ = grid.get(key)
                        if cell_ is None:
                            grid[key] = [k]
                        else:
                            cell_.append(k)
        # With `only`, just those buckets are read (a corridor round a few walks): elsewhere hit() would be wrong, so
        # nothing is filed there and it says None.
        self.only = only
        self.slopes = {}
        self._sub = {}
        # Footbridge decks are walkable surfaces too: level, at the deck's top.
        self.decks = []
        for item in doc.get('scatter', ()):
            if item['prototype'] != 'footbridge':
                continue
            size = [b*s for b, s in zip(doc['prototypes']['footbridge']['size_m'], item['scale'])]
            a = math.radians(item['yaw_deg'])
            self.decks.append((item['position_m'][0], item['position_m'][2], math.cos(a), math.sin(a),
                               size[0]/2, size[2]/2, item['position_m'][1]+size[1]))
        self._memo = {}
        self._plain = {}
        self._halves, self._cell_halves, self._corners, self._whole = {}, {}, {}, {}
        # The detail's fine cells (level 1): the same, on the lattice subdivided.
        self._cell_halves_f, self._corners_f, self._whole_f = {}, {}, {}
        self._lattice = _MISSING
        self._fine_cell = ((doc.get('x_generator') or {}).get('detail') or {}).get('cell_m')
        self._lattice_src = (doc, meshes)
        self.walks = {}     # the walkers' shared answers about this ground (Walker._shared)

    def moved(self, points):
        """The land's vertices at these (x, z) were raised or lowered in place
        (the same position lists, so every triangle reads their new height):
        forget every answer that may read them. Hits and slopes are read
        again; the plain buckets and whole grid cells round them, their
        corners, and the walkers' shared answers (a disc's ground, an edge, a
        node's height) for nodes within reach are judged again when asked."""
        if not points:
            return
        self._memo.clear()
        self.slopes.clear()
        b, floor = self.bucket, math.floor
        lat = self._land_lattice()
        lc = lat[2] if lat is not None else b
        R = 2*lc+1e-3        # a bucket's verdict reads the cells within a cell of its square
        buckets = set()
        for x, z in points:
            buckets.update((i, j) for i in range(int(floor((x-R)/b)), int(floor((x+R)/b))+1)
                           for j in range(int(floor((z-R)/b)), int(floor((z+R)/b))+1))
        for key in buckets:
            self._plain.pop(key, None)
        for lat_, whole, corners, level in ((lat, self._whole, self._corners, 0),
                                            (self._fine_lattice(), self._whole_f, self._corners_f, 1)):
            if lat_ is None:
                continue
            lx, lz, c = lat_
            moved = set()
            for x, z in points:
                ri, rj = round((x-lx)/c), round((z-lz)/c)
                moved.add((ri, rj))
                for ci in (ri-1, ri):
                    for cj in (rj-1, rj):
                        whole.pop((ci, cj), None)
            tris = self.tris
            for key in buckets:
                for k in self.grid.get(key, ()):
                    t = tris[k]
                    got = self._halves.get(k, _MISSING)
                    if not t[8] or got is _MISSING or (got is not None and got[2] != level):
                        continue
                    for v in t[:3]:
                        p = (round((v[0]-lx)/c), round((v[2]-lz)/c))
                        if p in moved and corners.get(p) is not _BROKEN:
                            corners[p] = (v[0], v[1], v[2])
        # Walkers: a node reads the buckets within a disc's or an edge's reach.
        for got in self.walks.values():
            i0, j0, ni, nj = got['lat']
            c = self.cell
            Rn = R+b+max(CLEAR_R, c)+c
            nodes = set()
            for x, z in points:
                for i in range(int(floor((x-Rn)/c))-i0, int(floor((x+Rn)/c))+2-i0):
                    if 0 <= i < ni:
                        base = i*nj
                        nodes.update(base+j for j in range(max(0, int(floor((z-Rn)/c))-j0),
                                                           min(nj, int(floor((z+Rn)/c))+2-j0)))
            if 'ground' in got:
                ground, edges = got['ground'], got['edges']
                for n in nodes:
                    ground[n] = 0
                    for d in range(8):
                        edges.pop(n*8+d, None)
            elif 'slope' in got:
                slope = got['slope']
                for n in nodes:
                    slope[n] = -1.
            else:
                heights = got['heights']
                for n in nodes:
                    heights.pop(n, None)

    def deck(self, x, z):
        for x0, z0, c, s, hw, hl, top in self.decks:
            dx, dz = x-x0, z-z0
            if abs(c*dx-s*dz) <= hw and abs(s*dx+c*dz) <= hl:
                return top
        return None

    def slope(self, k):
        s = self.slopes.get(k)
        if s is None:
            a, b, c = self.tris[k][:3]
            s = self.slopes[k] = _face_slope(a, b, c)
        return s

    def candidates(self, x, z):
        """The triangles `hit` tests at (x, z): its bucket's, in their order,
        narrowed to those whose widened box reaches the point's quarter-bucket
        (every triangle the barycentric test can accept there is kept)."""
        bucket = self.bucket
        i, j = int(math.floor(x/bucket)), int(math.floor(z/bucket))
        sb = bucket/_SUB
        si = min(_SUB-1, max(0, int(math.floor((x-i*bucket)/sb))))
        sj = min(_SUB-1, max(0, int(math.floor((z-j*bucket)/sb))))
        key = (i, j, si, sj)
        got = self._sub.get(key)
        if got is None:
            full = self.grid.get((i, j), ())
            X0, Z0 = i*bucket+si*sb, j*bucket+sj*sb
            X1, Z1 = X0+sb, Z0+sb
            got = []
            tris = self.tris
            for k in full:
                t = tris[k]
                # A point the test accepts lies within 2e-7 of the triangle's extent outside its box.
                m = 1e-6*max(t[5]-t[4], t[7]-t[6])+1e-6
                if t[5]+m >= X0 and t[4]-m <= X1 and t[7]+m >= Z0 and t[6]-m <= Z1:
                    got.append(k)
            self._sub[key] = got
        return got

    def hit(self, x, z):
        """(height, slope degrees) of the topmost triangle at (x, z), or None."""
        key = (round(x, 5), round(z, 5))
        best = self._memo.get(key, _MISSING)
        if best is not _MISSING:
            return best
        best = None
        tris = self.tris
        for k in self.candidates(x, z):
            a, b, c, det = tris[k][:4]
            u = ((b[2]-c[2])*(x-c[0])+(c[0]-b[0])*(z-c[2]))/det
            if u < -1e-7:
                continue
            v = ((c[2]-a[2])*(x-c[0])+(a[0]-c[0])*(z-c[2]))/det
            w = 1-u-v
            if v < -1e-7 or w < -1e-7:
                continue
            y = u*a[1]+v*b[1]+w*c[1]
            # On an edge or vertex several faces meet at one height. Ties
            # (within 1 micrometre, beyond float32 rounding of a written
            # package) take the gentlest face, decided by value, never by
            # triangle order, so the generator's memory and its package agree.
            if best is None or y > best[0]-1e-6:
                slope = self.slope(k)
                if best is None or y > best[0]+1e-6:
                    best = (y, slope)
                else:
                    best = (max(y, best[0]), min(slope, best[1]))
        top = self.deck(x, z) if self.decks else None
        if top is not None and (best is None or top >= best[0]):
            best = (top, 0.)
        if len(self._memo) < 2_000_000:
            self._memo[key] = best
        return best

    def height(self, x, z):
        r = self.hit(x, z)
        return math.nan if r is None else r[0]

    def land_height(self, x, z):
        """`height` at (x, z), read straight from the land grid's own cell
        where the bucket is plain land and the point lies inside the cell, a
        ten-thousandth of it clear of its sides: only the cell's two halves
        can take the point there and nothing else is near its height, so the
        height is the half's (on their shared diagonal, the higher of the two
        within a micrometre, as `hit` takes it); else `height`."""
        b = self.bucket
        if self.plain_slope(int(math.floor(x/b)), int(math.floor(z/b))) is not None:
            lx, lz, c = self._lattice
            fx, fz = (x-lx)/c, (z-lz)/c
            ci, cj = int(math.floor(fx)), int(math.floor(fz))
            fx, fz = fx-ci, fz-cj
            halves = self._cell_halves.get((ci, cj))
            if halves is None and self._fine_cell:
                # In a detail block: the fine cell's two halves.
                lx, lz, c = self._fine_lattice()
                fx, fz = (x-lx)/c, (z-lz)/c
                ci, cj = int(math.floor(fx)), int(math.floor(fz))
                fx, fz = fx-ci, fz-cj
                halves = self._cell_halves_f.get((ci, cj))
            if 1e-4 < fx < 1-1e-4 and 1e-4 < fz < 1-1e-4 and halves is not None and len(halves) == 2:
                tris = self.tris
                best = None
                for k, _ in halves:
                    a, b_, cc, det = tris[k][:4]
                    u = ((b_[2]-cc[2])*(x-cc[0])+(cc[0]-b_[0])*(z-cc[2]))/det
                    v = ((cc[2]-a[2])*(x-cc[0])+(a[0]-cc[0])*(z-cc[2]))/det
                    w = 1-u-v
                    if u >= -1e-7 and v >= -1e-7 and w >= -1e-7:
                        y = u*a[1]+v*b_[1]+w*cc[1]
                        if best is None or y > best:
                            best = y
                if best is not None:
                    return best
        return self.height(x, z)

    # ---------------------------------------------------------------- plain ground
    def _land_lattice(self):
        """(x0, z0, cell) of the package's land grid, read back from its land
        mesh and `x_generator.cell_m`, or None (no such grid: nothing is vouched for)."""
        if self._lattice is _MISSING:
            self._lattice = None
            doc, meshes = self._lattice_src
            cell = (doc.get('x_generator') or {}).get('cell_m')
            if cell and 'land' in meshes and any(r['mesh'] == 'land' for r in doc['terrain']):
                xs = [v[0] for p in meshes['land'] for v in p['positions']]
                zs = [v[2] for p in meshes['land'] for v in p['positions']]
                if xs:
                    self._lattice = (min(xs), min(zs), cell)
            self._lattice_src = None
        return self._lattice

    def _fine_lattice(self):
        """(x0, z0, cell) of the land's detail lattice (the base lattice subdivided), or None."""
        lat = self._land_lattice()
        if lat is None or not self._fine_cell:
            return None
        return lat[0], lat[1], self._fine_cell

    def plain_slope(self, i, j):
        """For bucket (i, j): the steepest land face there when the bucket is
        plain land, else None. Plain: inside the checks' window; every grid
        cell the bucket reaches is whole (both halves of the package's land
        grid, its corners shared exactly with the neighbouring cells, so the
        land is one unbroken surface there); every other terrain or scenery
        triangle in the bucket lies wholly below that land; no footbridge deck
        reaches it. Then every point of the bucket hits that land, and no face
        under it is steeper than the value returned."""
        got = self._plain.get((i, j), _MISSING)
        if got is _MISSING:
            self._plain_block(i//_BLOCK, j//_BLOCK)
            got = self._plain[(i, j)]
        return got

    def _half(self, k):
        """Read land triangle k as half of a grid cell: ((ci, cj), the corner
        it misses: 0 (ci, cj), 1 (ci+1, cj), 2 (ci, cj+1), 3 (ci+1, cj+1),
        level: 0 the land grid, 1 its detail lattice), or None when it is not.
        Its corners are kept: a lattice point met with two different positions
        is marked broken (the land is not one surface there)."""
        got = self._halves.get(k, _MISSING)
        if got is not _MISSING:
            return got
        tol = 1e-3
        t = self.tris[k]
        x0, x1, z0, z1 = t[4], t[5], t[6], t[7]
        got = None
        for level, lat, corners, cells in ((0, self._land_lattice(), self._corners, self._cell_halves),
                                           (1, self._fine_lattice(), self._corners_f, self._cell_halves_f)):
            if lat is None:
                continue
            lx, lz, c = lat
            if abs(x1-x0-c) > 2*tol*c or abs(z1-z0-c) > 2*tol*c:
                continue    # not one cell of this lattice across
            fi, fj = (x0-lx)/c, (z0-lz)/c
            ri, rj = round(fi), round(fj)
            if not (abs(fi-ri) <= tol and abs(fj-rj) <= tol and abs((x1-lx)/c-ri-1) <= tol and
                    abs((z1-lz)/c-rj-1) <= tol):
                continue
            present = 0
            for v in t[:3]:
                bx, bz = (1 if v[0] == x1 else 0), (1 if v[2] == z1 else 0)
                bit = 1 << (bx+2*bz)
                if present & bit:
                    present = -1
                    break
                present |= bit
                key = (ri+bx, rj+bz)
                val = (v[0], v[1], v[2])
                was = corners.get(key)
                if was is None:
                    corners[key] = val
                elif was != val:
                    corners[key] = _BROKEN
            if present > 0:
                missing = (15 & ~present).bit_length()-1
                got = ((ri, rj), missing, level)
                cells.setdefault((ri, rj), []).append((k, missing))
            break
        self._halves[k] = got
        return got

    def _cell(self, ci, cj, level=0):
        """(steepest face, lowest corner) of a whole grid cell (level 0, or a
        detail cell, level 1): both halves on one diagonal, every corner one
        position; else None."""
        key = (ci, cj)
        whole, cells, corners = (self._whole, self._cell_halves, self._corners) if level == 0 else \
            (self._whole_f, self._cell_halves_f, self._corners_f)
        got = whole.get(key, _MISSING)
        if got is not _MISSING:
            return got
        got = None
        halves = cells.get(key)
        if halves is not None and len(halves) == 2 and {halves[0][1], halves[1][1]} in ({0, 3}, {1, 2}):
            ys = []
            for p in ((ci, cj), (ci+1, cj), (ci, cj+1), (ci+1, cj+1)):
                v = corners.get(p)
                if v is None or v is _BROKEN:
                    ys = None
                    break
                ys.append(v[1])
            if ys is not None:
                got = (max(self.slope(halves[0][0]), self.slope(halves[1][0])), min(ys))
        whole[key] = got
        return got

    def _fine_part(self, ci, cj, X0, X1, Z0, Z1):
        """(steepest face, lowest corner) of the detail cells inside land cell
        (ci, cj) that reach the box, when every one of them is whole; else None."""
        lat = self._fine_lattice()
        if lat is None:
            return None
        lx, lz, c = lat
        r = int(round(self._land_lattice()[2]/c))
        floor = math.floor
        steepest, low = 0., math.inf
        for fj in range(max(cj*r, int(floor((Z0-lz)/c))), min(cj*r+r-1, int(floor((Z1-lz)/c)))+1):
            for fi in range(max(ci*r, int(floor((X0-lx)/c))), min(ci*r+r-1, int(floor((X1-lx)/c)))+1):
                got = self._cell(fi, fj, 1)
                if got is None:
                    return None
                if got[0] > steepest:
                    steepest = got[0]
                if got[1] < low:
                    low = got[1]
        return steepest, low

    def _plain_block(self, bi, bj):
        """Judge a block of buckets at once."""
        keys = [(i, j) for j in range(bj*_BLOCK, (bj+1)*_BLOCK) for i in range(bi*_BLOCK, (bi+1)*_BLOCK)]
        lat = self._land_lattice()
        if lat is None:
            for key in keys:
                self._plain[key] = None
            return
        lx, lz, c = lat
        tris, grid, halves = self.tris, self.grid, self._halves
        # The land triangles of the block and a ring of buckets round it: every
        # half of a grid cell that reaches a bucket of the block is among them.
        for j in range(bj*_BLOCK-1, (bj+1)*_BLOCK+1):
            for i in range(bi*_BLOCK-1, (bi+1)*_BLOCK+1):
                for k in grid.get((i, j), ()):
                    if k not in halves and tris[k][8]:
                        self._half(k)
        b, win, eps = self.bucket, self.win, 1e-4
        boxes = self._deck_boxes()
        cell = self._cell
        fine = self._fine_cell
        for i, j in keys:
            X0, X1, Z0, Z1 = i*b-eps, (i+1)*b+eps, j*b-eps, (j+1)*b+eps
            out = None
            if (win is None or (X0 >= win[0] and X1 <= win[2] and Z0 >= win[1] and Z1 <= win[3])) and \
                    not any(x0+r >= X0 and x0-r <= X1 and z0+r >= Z0 and z0-r <= Z1 for x0, z0, r in boxes):
                steepest, low = 0., math.inf
                for cj in range(int(math.floor((Z0-lz)/c)), int(math.floor((Z1-lz)/c))+1):
                    for ci in range(int(math.floor((X0-lx)/c)), int(math.floor((X1-lx)/c))+1):
                        got = cell(ci, cj)
                        if got is None and fine:
                            got = self._fine_part(ci, cj, X0, X1, Z0, Z1)
                        if got is None:
                            low = None
                            break
                        if got[0] > steepest:
                            steepest = got[0]
                        if got[1] < low:
                            low = got[1]
                    if low is None:
                        break
                # Every land triangle here is half a whole cell; anything else lies below.
                if low is not None and all(halves[k] is not None if tris[k][8] else
                                           max(tris[k][0][1], tris[k][1][1], tris[k][2][1]) < low-1e-5
                                           for k in grid.get((i, j), ())):
                    out = steepest
            self._plain[(i, j)] = out

    def _deck_boxes(self):
        if not hasattr(self, '_dboxes'):
            self._dboxes = [(x0, z0, math.hypot(hw, hl)) for x0, z0, c, s, hw, hl, top in self.decks]
        return self._dboxes

    def plain(self, x, z, limit):
        """Whether the bucket holding (x, z) is plain land no steeper than `limit`."""
        b = self.bucket
        s = self.plain_slope(int(math.floor(x/b)), int(math.floor(z/b)))
        return s is not None and s <= limit

    def plain_box(self, x0, x1, z0, z1, limit):
        """Whether every bucket a box reaches (widened a hair past rounding) is plain land no steeper than `limit`."""
        b = self.bucket
        for j in range(int(math.floor((z0-1e-9)/b)), int(math.floor((z1+1e-9)/b))+1):
            for i in range(int(math.floor((x0-1e-9)/b)), int(math.floor((x1+1e-9)/b))+1):
                s = self.plain_slope(i, j)
                if s is None or s > limit:
                    return False
        return True


def _sea(doc):
    return (doc.get('x_generator') or {}).get('sea')


def _in_loop(x, z, loop):
    inside = False
    for a, b in zip(loop, loop[1:]+loop[:1]):
        if (a[1] > z) != (b[1] > z) and x < (b[0]-a[0])*(z-a[1])/(b[1]-a[1])+a[0]:
            inside = not inside
    return inside


def window(doc, margin=.6):
    """Where the checks look: the island's playable water and a margin (the
    sea and the distant islands run on to the horizon; nothing is walked or
    swum out there). None for a package without a sea."""
    sea = _sea(doc)
    if not sea:
        return None
    b = sea['play_area']['bounds_m']
    return b['min_m'][0]-margin, b['min_m'][2]-margin, b['max_m'][0]+margin, b['max_m'][2]+margin


def _meets(win, *vs):
    if win is None:
        return True
    return not (max(v[0] for v in vs) < win[0] or min(v[0] for v in vs) > win[2] or
                max(v[2] for v in vs) < win[1] or min(v[2] for v in vs) > win[3])


def _span(win, bucket, vs):
    """Bucket index ranges of a triangle's box, clipped to the window."""
    x0, x1 = min(v[0] for v in vs), max(v[0] for v in vs)
    z0, z1 = min(v[2] for v in vs), max(v[2] for v in vs)
    if win is not None:
        x0, x1, z0, z1 = max(x0, win[0]), min(x1, win[2]), max(z0, win[1]), min(z1, win[3])
    return (int(math.floor(x0/bucket)), int(math.floor(x1/bucket)), int(math.floor(z0/bucket)),
            int(math.floor(z1/bucket)))


def water_checks(doc, meshes, terrain):
    out = []
    ok = True
    sea = _sea(doc)
    for rec in doc['water']:
        ps = [v for p in meshes[rec['mesh']] for v in p['positions']]
        if rec['kind'] == 'still':
            ys = [v[1] for v in ps]
            spread = max(ys)-min(ys)
            # The still surface's own edge must sit under its banks.
            tris = [t for p in meshes[rec['mesh']] for t in p['triangles']]
            edges = {}
            for t in tris:
                for a, b in ((t[0], t[1]), (t[1], t[2]), (t[2], t[0])):
                    k = (min(a, b), max(a, b))
                    edges[k] = edges.get(k, 0)+1
            rim = sorted({k for e, n in edges.items() if n == 1 for k in e})
            inside_terrain = [k for k in rim if _covered(terrain, ps[k][0], ps[k][2])]
            exposed = [k for k in inside_terrain if terrain.height(ps[k][0], ps[k][2]) < ps[k][1]-.002]
            # A rim vertex may meet a stream that carries the water on.
            streams = [v for r in doc['water'] if r['kind'] != 'still' for p in meshes[r['mesh']] for v in p['positions']]
            loose = [k for k in exposed if not any(math.hypot(ps[k][0]-v[0], ps[k][2]-v[2]) < .12 for v in streams)]
            if sea and rec['mesh'] == sea['mesh']:
                # The sea runs on to the horizon past the playable water.
                loose = [k for k in loose if _in_loop(ps[k][0], ps[k][2], sea['play_area']['outline_m'])]
            scenery = rec['mesh'] == 'distant_lake'
            good = spread <= 1e-6 and (scenery or not loose)
            out.append(dict(mesh=rec['mesh'], kind='still', level=round(ys[0], 5), level_spread=spread,
                            rim_vertices=len(inside_terrain), rim_below_surface=len(exposed),
                            rim_open_not_at_stream=len(loose), scenery=scenery, ok=good))
            ok &= good
        else:
            ys = [v[1] for v in ps]
            across = max(abs(ys[2*k]-ys[2*k+1]) for k in range(len(ys)//2))
            rises = [ys[2*k+2]-ys[2*k] for k in range(len(ys)//2-1)]
            uphill = max([0.]+rises)
            good = across <= 1e-6 and uphill <= 1e-6
            out.append(dict(mesh=rec['mesh'], kind=rec['kind'], top=round(ys[0], 4), bottom=round(ys[-1], 4),
                            max_tilt_across_m=across, max_rise_downstream_m=uphill, ok=good))
            ok &= good
    # Consecutive runs of one stream must not step up where they join.
    joins = []
    names = sorted(r['mesh'] for r in doc['water'] if r['kind'] != 'still')
    for a, b in zip(names, names[1:]):
        if a.rsplit('_', 1)[0] != b.rsplit('_', 1)[0]:
            continue
        ya = meshes[a][0]['positions'][-1][1]
        yb = meshes[b][0]['positions'][0][1]
        joins.append(dict(upstream=a, downstream=b, step_m=round(yb-ya, 6)))
        ok &= yb <= ya+1e-6
    return ok, out, joins


def _covered(terrain, x, z):
    return terrain.hit(x, z) is not None


def footprint_points(item, size):
    a = math.radians(item['yaw_deg'])
    c, s = math.cos(a), math.sin(a)
    x0, _, z0 = item['position_m']
    pts = [(x0, z0)]
    for fx, fz in ((-.5, -.5), (.5, -.5), (.5, .5), (-.5, .5), (0, -.5), (0, .5), (-.5, 0), (.5, 0)):
        lx, lz = fx*size[0], fz*size[2]
        pts.append((x0+c*lx+s*lz, z0-s*lx+c*lz))
    return pts


def grounding_checks(doc, terrain):
    """Built things sit on the ground (whole footprint); plants root at their stem."""
    rows = []
    bad = 0
    protos = doc['prototypes']
    for category in ('scatter', 'objects'):
        for item in doc[category]:
            name = item['prototype']
            base = SIZES[name] if name in SIZES else protos[name]['size_m']
            size = item['size_m'] if category == 'objects' else [b*s for b, s in zip(base, item['scale'])]
            y = item['position_m'][1]
            if name == 'footbridge':
                # Carried by its two ends: the banks under each end of the deck.
                a = math.radians(item['yaw_deg'])
                ends = [(item['position_m'][0]+math.sin(a)*f*size[2]*.45, item['position_m'][2]+math.cos(a)*f*size[2]*.45)
                        for f in (-1, 1)]
                saved, terrain.decks = terrain.decks, []
                gs = [terrain.hit(x, z)[0] for x, z in ends]
                terrain._memo.clear()
                terrain.decks = saved
                gap = y-max(gs)          # the deck's underside above its higher bank
                sunk = max(gs)-(y+size[1])
                float_bad = gap > .012 or y-min(gs) > .03
                sunk_bad = sunk > 0
                kind = 'built'
            elif category == 'objects' or name in BUILT:
                gs = [terrain.height(x, z) for x, z in footprint_points(item, size)]
                gap = y-min(gs)          # >0: some of the base hangs in the air
                sunk = max(gs)-y         # >0: the ground covers the base
                float_bad = gap > .012
                sunk_bad = sunk > max(.025, .2*size[1])
                kind = 'built'
            else:
                g = terrain.height(item['position_m'][0], item['position_m'][2])
                gap = y-g
                sunk = g-y
                float_bad = gap > .002
                sunk_bad = sunk > .012+.3*size[1]
                kind = 'plant'
            if float_bad or sunk_bad:
                bad += 1
            if kind == 'built' or float_bad or sunk_bad:
                rows.append(dict(prototype=name, kind=kind, position=[round(v, 3) for v in item['position_m']],
                                 hang_m=round(gap, 4), sunk_m=round(sunk, 4),
                                 ok=not (float_bad or sunk_bad)))
    return bad == 0, rows


def _in_tri(x, z, vs):
    (ax, _, az), (bx, _, bz), (cx, _, cz) = vs
    d = (bz-cz)*(ax-cx)+(cx-bx)*(az-cz)
    if abs(d) < 1e-12:
        return False
    u = ((bz-cz)*(x-cx)+(cx-bx)*(z-cz))/d
    v = ((cz-az)*(x-cx)+(ax-cx)*(z-cz))/d
    return u >= -1e-6 and v >= -1e-6 and 1-u-v >= -1e-6


CLEAR_R = .055      # half of the 11 cm carrying clearance (body and a held item)
WALK_LATTICE_W = .05   # the walk's lattice on a magnified room (`walk_lattice`)
EDGE_SAMPLE = .01   # every walk edge is sampled each centimetre
# On a magnified island (`Walker.ways`): a goal's own pocket is flooded back up to this many nodes before the
# least-cost flood is left to find whether a way exists.
POCKET_NODES = 60_000
N8 = ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1))
_PASSABLE = frozenset(('grass', 'wildflowers', 'daisies', 'buttercups', 'heather', 'fern', 'reeds'))


def obstacles(doc):
    """Footprints a body cannot pass through: buildings and yard things as
    rotated rectangles, trunks, rocks and shrubs as discs. Ground cover is
    passable."""
    protos = doc['prototypes']
    out = []
    for item in doc['scatter']:
        name = item['prototype']
        if name in _PASSABLE or name == 'footbridge':
            continue   # ground cover is walked through; a footbridge is walked on, not around
        base = SIZES[name] if name in SIZES else protos[name]['size_m']
        size = [b*s for b, s in zip(base, item['scale'])]
        x, _, z = item['position_m']
        if name in BUILT:
            out.append(('box', x, z, size[0]/2, size[2]/2, math.radians(item['yaw_deg'])))
        elif name in ('broadleaf', 'conifer'):
            out.append(('disc', x, z, .045*size[0]/base[0]+.01))
        elif name in ('boulder', 'rock', 'shrub'):
            out.append(('disc', x, z, min(size[0], size[2])/2))
    return out


def blocks(o, x, z):
    """Whether obstacle `o` keeps an 11 cm lane from (x, z)."""
    if o[0] == 'disc':
        return math.hypot(x-o[1], z-o[2]) < o[3]+CLEAR_R
    a = o[5]
    c, s = math.cos(a), math.sin(a)
    dx, dz = x-o[1], z-o[2]
    lx, lz = c*dx-s*dz, s*dx+c*dz
    ex, ez = max(0., abs(lx)-o[3]), max(0., abs(lz)-o[4])
    return math.hypot(ex, ez) < CLEAR_R


def obstacle_index(obs):
    """Obstacles by the 20 cm buckets their lane reaches."""
    index = {}
    floor = math.floor
    for o in obs:
        r = o[3] if o[0] == 'disc' else math.hypot(o[3], o[4])
        for i in range(int(floor((o[1]-r-CLEAR_R)/.2)), int(floor((o[1]+r+CLEAR_R)/.2))+1):
            for j in range(int(floor((o[2]-r-CLEAR_R)/.2)), int(floor((o[2]+r+CLEAR_R)/.2))+1):
                got = index.get((i, j))
                if got is None:
                    index[(i, j)] = [o]
                else:
                    got.append(o)
    return index


def inside_row(room, c, i0, ni, j, r=CLEAR_R):
    """Whether each node (i, j), i0 <= i < i0+ni, has its disc inside the
    walkable floor (`inside_distance(room, i*c, j*c) >= r`), a row at a time,
    with the same answers: off the bounds or nearer a bound than r, no; with
    no floor outline, yes; near a floor outline's edge, read in full; away from
    every edge, yes when inside an outline (its crossings counted as
    `point_in_poly` counts them, so the deepest edge is over r away)."""
    from .land import _shape, inside_distance
    lo0, lo2, hi0, hi2, polys = _shape(room)
    z = j*c
    D = r+1e-6
    near, crossings = [], []
    for edges in polys:
        xs_ = []
        for a0, a2, b2, ex, ez, L, b0 in edges:
            if min(a2, b2)-D <= z <= max(a2, b2)+D:
                near.append((min(a0, b0)-D, max(a0, b0)+D))
            if (a2 > z) != (b2 > z):
                xs_.append(ex*(z-a2)/ez+a0)
        crossings.append(sorted(xs_))
    out = bytearray(ni)
    zl, zh = z-lo2, hi2-z
    for k in range(ni):
        x = (i0+k)*c
        a, b = lo0-x, x-hi0
        dx = b if b > a else a
        a, b = lo2-z, z-hi2
        dz = b if b > a else a
        if not (dx <= 0 and dz <= 0) or min(x-lo0, hi0-x, zl, zh) < r:
            out[k] = 2
        elif not polys:
            out[k] = 1
        elif any(lo_ <= x <= hi_ for lo_, hi_ in near):
            out[k] = 1 if inside_distance(room, x, z) >= r else 2
        else:
            # Crossings with x < xc, counted as point_in_poly counts them.
            out[k] = 1 if any((len(xs_)-bisect_right(xs_, x)) % 2 for xs_ in crossings) else 2
    return out


_WATER_INDEX = {}   # one shared water index (see Walker)
_INSIDE = {}        # one shared answer per room and lattice: is a node's disc inside the walkable floor
_RUN = 32           # nodes along a lattice row judged safe or not together (`Walker._is_safe`)


class Walker:
    """The walk over a lattice of the decoded surface (2.5 cm; 5 cm on a
    magnified room, `walk_lattice`). A node is standable when an 11 cm disc
    around it is dry, clear of obstacles, inside the walkable floor and on
    triangles no steeper than the limit; every edge is sampled each
    centimetre for its rise and incline.

    `reach(start)` floods every node a body reaches from the start (one flood
    answers every goal); `ways(start, goals)` finds the least-cost way (length
    plus twice the climb) to goals the flood reaches: one A* search per goal
    on today's room, exactly as before; on a magnified island one least-cost
    flood for all of them.

    Answers about the ground alone (a disc's ground and water, an edge) are
    kept on the Terrain and shared by every Walker over it, and whether a disc
    is inside the floor on the room; obstacles are each Walker's own. Where
    the Terrain vouches for plain land no steeper than the limit and no water
    within reach of a node (`Terrain.plain_slope`), the node's disc and every
    edge from it need no reading: each sample hits that land, gentle enough,
    dry, and a centimetre of it rises less than a step."""

    def __init__(self, doc, meshes, terrain, room, limit=WALK_DEG, step=STEP_M, index=None):
        from .land import inside_distance
        self.t = terrain
        self.room = room
        self.limit = limit
        self.step = step
        self.scale = world_scale(doc)
        self.inside = lambda x, z: inside_distance(room, x, z)
        self.obs = obstacle_index(obstacles(doc)) if index is None else index   # `index`: obstacle_index of doc's scatter
        win = window(doc, .2)
        # The water's index depends only on the water records, the meshes and the
        # window: Walkers over the same package share it (the generator builds
        # dozens of them, each with a different scatter). A Terrain read only in
        # a corridor (`only`) has its water read there too.
        only = terrain.only
        key = (id(doc['water']), id(meshes), win, None if only is None else id(only))
        hit = _WATER_INDEX.get(key)
        if hit is not None and hit[0] is doc['water'] and hit[1] is meshes and hit[4] is only:
            self.water, self.wbuck = hit[2], hit[3]
        else:
            self.water = []
            self.wbuck = {}
            for rec in doc['water']:
                for p in meshes[rec['mesh']]:
                    for t in p['triangles']:
                        vs = [p['positions'][k] for k in t]
                        if not _meets(win, *vs):
                            continue
                        i0, i1, j0, j1 = _span(win, .1, vs)
                        cells = [(i, j) for i in range(i0, i1+1) for j in range(j0, j1+1)]
                        if only is not None:
                            cells = [c for c in cells if c in only]
                            if not cells:
                                continue
                        k = len(self.water)
                        self.water.append(vs)
                        for c in cells:
                            self.wbuck.setdefault(c, []).append(k)
            if len(_WATER_INDEX) > 4:
                _WATER_INDEX.clear()
            _WATER_INDEX[key] = (doc['water'], meshes, self.water, self.wbuck, only)
        # The lattice over the room's bounds (nodes outside them never stand:
        # their disc is outside the floor). A node's id orders as its (i, j).
        c = terrain.cell
        lo, hi = room['bounds']['min_m'], room['bounds']['max_m']
        i0, j0 = int(math.floor(lo[0]/c))-2, int(math.floor(lo[2]/c))-2
        ni, nj = int(math.ceil(hi[0]/c))+3-i0, int(math.ceil(hi[2]/c))+3-j0
        self.i0, self.j0, self.ni, self.nj = i0, j0, ni, nj
        self.state = bytearray(ni*nj)    # this walker's answer per node: 0 unread, 1 standable, 2 not
        # Shared by every Walker over this ground, water, room and limits.
        lat = (i0, j0, ni, nj)

        def shared(key, make):
            got = terrain.walks.get(key)
            if got is None or got['wbuck'] is not self.wbuck or got['room'] is not room:
                got = terrain.walks[key] = dict(make(), wbuck=self.wbuck, room=room, lat=lat)
            return got
        got = shared(('walk', id(self.wbuck), id(room), limit, step), lambda: dict(ground=bytearray(ni*nj), edges={}))
        self.ground, self.edges = got['ground'], got['edges']
        # The steepest plain face within a node's reach (inf: not plain, or water), for every limit.
        self.nslope = shared(('slope', id(self.wbuck), id(room)), lambda: dict(slope=array('d', [-1.])*(ni*nj)))['slope']
        self.heights = shared(('heights', id(self.wbuck), id(room)), lambda: dict(heights={}))['heights']
        inside = _INSIDE.get((id(room), c))
        if inside is None or inside[0] is not room:
            if len(_INSIDE) > 8:
                _INSIDE.clear()
            inside = _INSIDE[(id(room), c)] = (room, bytearray(ni*nj), set())
        self.inside_ok, self.inside_rows = inside[1], inside[2]
        # A centimetre of plain land no steeper than the limit rises less than a step.
        self.vouch = EDGE_SAMPLE*math.tan(math.radians(min(limit, 89.)))+1e-9 < step
        self.d8 = tuple(di*nj+dj for di, dj in N8)

    # ---------------------------------------------------------------- nodes
    def key(self, x, z):
        return int(round(x/self.t.cell)), int(round(z/self.t.cell))

    def node(self, k):
        """A key's id, or None off the lattice (never standable)."""
        i, j = k[0]-self.i0, k[1]-self.j0
        if 0 <= i < self.ni and 0 <= j < self.nj:
            return i*self.nj+j
        return None

    def ij(self, n):
        i, j = divmod(n, self.nj)
        return i+self.i0, j+self.j0

    def wet(self, x, z):
        g = self.t.height(x, z)
        for k in self.wbuck.get((int(math.floor(x/.1)), int(math.floor(z/.1))), ()):
            vs = self.water[k]
            if _in_tri(x, z, vs) and g < max(v[1] for v in vs)+.002:
                return True
        return False

    def blocked(self, x, z):
        for o in self.obs.get((int(math.floor(x/.2)), int(math.floor(z/.2))), ()):
            if blocks(o, x, z):
                return True
        return False

    def standable(self, k):
        n = self.node(k)
        return n is not None and self._stand(n)

    def _stand(self, n):
        s = self.state[n]
        if s:
            return s == 1
        nj = self.nj
        j = n % nj
        if not self.inside_ok[n]:
            if j not in self.inside_rows:
                row = inside_row(self.room, self.t.cell, self.i0, self.ni, j+self.j0)
                self.inside_ok[j::nj] = row
                self.inside_rows.add(j)
        ok = self.inside_ok[n] == 1
        if ok:
            c = self.t.cell
            i = n//nj+self.i0
            x, z = i*c, (j+self.j0)*c
            ok = not self.blocked(x, z)
            if ok:
                g = self.ground[n]
                if not g:
                    g = self.ground[n] = 1 if self._is_safe(n) or self._ground(x, z) else 2
                ok = g == 1
        self.state[n] = 1 if ok else 2
        return ok

    def _is_safe(self, n):
        """Whether every bucket within a disc's or an edge's reach of the node
        is plain land no steeper than the limit, with no water: its disc and
        every edge from it pass without a reading (judged 32 nodes at a time,
        once for every limit)."""
        v = self.nslope[n]
        if v < 0:
            self._slope_run(n//self.nj//_RUN, n % self.nj)
            v = self.nslope[n]
        return v <= self.limit

    def _slope_run(self, ci, j):
        t, nj = self.t, self.nj
        c, b, eps = t.cell, t.bucket, 1e-9
        r = max(CLEAR_R, c)
        z = (j+self.j0)*c
        floor, inf = math.floor, math.inf
        jbs = range(int(floor((z-r-eps)/b)), int(floor((z+r+eps)/b))+1)
        wbuck, plain = self.wbuck, t.plain_slope
        cols = {}
        out = self.nslope
        for k in range(ci*_RUN, min(self.ni, (ci+1)*_RUN)):
            x = (self.i0+k)*c
            steepest = 0.
            for ib in range(int(floor((x-r-eps)/b)), int(floor((x+r+eps)/b))+1):
                col = cols.get(ib)
                if col is None:
                    col = 0.
                    for jb in jbs:
                        s = plain(ib, jb)
                        if s is None or (ib, jb) in wbuck:
                            col = inf
                            break
                        if s > col:
                            col = s
                    cols[ib] = col
                if col > steepest:
                    steepest = col
                    if col == inf:
                        break
            out[k*nj+j] = steepest

    def _ground(self, x, z):
        """The disc's ground: every sample on a face no steeper than the limit, and dry."""
        t, limit, wbuck, floor = self.t, self.limit, self.wbuck, math.floor
        for dx, dz in ((0, 0), (CLEAR_R, 0), (-CLEAR_R, 0), (0, CLEAR_R), (0, -CLEAR_R)):
            px, pz = x+dx, z+dz
            if t.plain(px, pz, limit) and (int(floor(px/.1)), int(floor(pz/.1))) not in wbuck:
                continue   # plain land, no water near: it hits the land, gentle enough, and is dry
            hit = t.hit(px, pz)
            if hit is None or hit[1] > limit or self.wet(px, pz):
                return False
        return True

    # ---------------------------------------------------------------- edges
    def edge(self, a, b):
        ax, az, bx, bz = a[0]*self.t.cell, a[1]*self.t.cell, b[0]*self.t.cell, b[1]*self.t.cell
        run = math.hypot(bx-ax, bz-az)
        n = max(1, int(math.ceil(run/EDGE_SAMPLE)))
        y = self.t.height(ax, az)
        rise = grade = 0.
        for m in range(1, n+1):
            f = m/n
            hit = self.t.hit(ax+f*(bx-ax), az+f*(bz-az))
            if hit is None:
                return None
            rise = max(rise, abs(hit[0]-y))
            grade = max(grade, hit[1])
            y = hit[0]
        if rise > self.step or grade > self.limit:
            return None
        return rise, grade

    def passable(self, a, b):
        """Whether the edge a -> b (lattice keys) passes (`edge` is not None)."""
        return self._pass(self.node(a), N8.index((b[0]-a[0], b[1]-a[1])))

    def _pass(self, n, d):
        """Whether the edge from node n in direction N8[d] passes, read once per ground."""
        if self.vouch and self._is_safe(n):
            return True
        key = n*8+d
        ok = self.edges.get(key)
        if ok is None:
            a = self.ij(n)
            di, dj = N8[d]
            b = (a[0]+di, a[1]+dj)
            c = self.t.cell
            ax, az, bx, bz = a[0]*c, a[1]*c, b[0]*c, b[1]*c
            if self.vouch and self.t.plain_box(min(ax, bx), max(ax, bx), min(az, bz), max(az, bz), self.limit):
                ok = True
            else:
                ok = self.edge(a, b) is not None
            self.edges[key] = ok
        return ok

    def height(self, n):
        h = self.heights.get(n)
        if h is None:
            i, j = self.ij(n)
            c = self.t.cell
            h = self.heights[n] = self.t.land_height(i*c, j*c)
        return h

    # ---------------------------------------------------------------- walks
    def reach(self, start):
        """Every node a body reaches from `start` (a breadth-first flood fill)."""
        s = self.node(self.key(*start))
        parent = array('i', [-2])*(self.ni*self.nj)
        count = 0
        queue = deque()
        if s is not None and self._stand(s):
            parent[s] = -1
            count = 1
            queue.append(s)
        return Reach(self, s, parent, self._flood(parent, queue, count))

    def reach_on(self, earlier, opened):
        """The flood `earlier` (made with more obstacles) carried on with this
        walker's: `opened` are the obstacles taken out since. Taking an
        obstacle out only frees nodes in its lane, so every node newly reached
        is reached through a step from a node already reached next to that
        lane; the flood goes on from those."""
        if not earlier.count:
            return self.reach(self.ij(earlier.start)) if earlier.start is not None else earlier
        parent = array('i', earlier.parent)
        c, nj, ni = self.t.cell, self.nj, self.ni
        floor = math.floor
        seeds = set()
        for o in opened:
            r = (o[3] if o[0] == 'disc' else math.hypot(o[3], o[4]))+CLEAR_R+c
            for i in range(max(0, int(floor((o[1]-r)/c))-self.i0-1), min(ni, int(floor((o[1]+r)/c))-self.i0+2)):
                for j in range(max(0, int(floor((o[2]-r)/c))-self.j0-1), min(nj, int(floor((o[2]+r)/c))-self.j0+2)):
                    n = i*nj+j
                    if parent[n] != -2:
                        seeds.add(n)
        queue = deque(sorted(seeds))
        return Reach(self, earlier.start, parent, self._flood(parent, queue, earlier.count))

    def _flood(self, parent, queue, count):
        pop, push = queue.popleft, queue.append
        state, stand, pass_ = self.state, self._stand, self._pass
        nj, vouch = self.nj, self.vouch
        steps = tuple(enumerate(zip(self.d8, N8)))
        while queue:
            k = pop()
            free = vouch and self._is_safe(k)
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
                count += 1
                push(n)
        return count

    def ways(self, start, goals, first=False):
        """A least-cost way (lattice keys; length plus twice the climb) from
        start to each goal, None where there is none. On today's room: one A*
        search per goal (`route`), only toward goals a flood reaches, exactly
        as before. On a magnified island: a goal shut in a pocket of its own
        is found so by a small flood back from it (`pocket`), and one
        least-cost flood serves all the others (`cheapest`). `first`: stop at
        the first goal with a way."""
        out = [None]*len(goals)
        if self.scale == 1:
            reach = self.reach(start)
            for m, g in enumerate(goals):
                if reach.has(g):
                    out[m] = self.route(start, g)[0]
                    if first and out[m]:
                        break
            return out
        open_ = [m for m, g in enumerate(goals) if self.pocket(start, g) != 'shut']
        for m, w in zip(open_, self.cheapest(start, [goals[m] for m in open_], first)):
            out[m] = w
        return out

    def pocket(self, start, goal, cap=POCKET_NODES):
        """Flood back from `goal` (edges walked toward it): 'shut' when no
        more than `cap` nodes reach it and the start is not among them (no
        way from the start), 'start' when the start is, 'open' otherwise."""
        s, g = self.node(self.key(*start)), self.node(self.key(*goal))
        if g is None or not self._stand(g):
            return 'shut'
        seen = {g}
        queue = deque((g,))
        state, stand, pass_, nj = self.state, self._stand, self._pass, self.nj
        steps = tuple(enumerate(zip(self.d8, N8)))
        while queue:
            n = queue.popleft()
            for d, (step, (di, dj)) in steps:
                k = n-step     # k -> n in direction d
                if k in seen:
                    continue
                s_ = state[k]
                if not (s_ == 1 if s_ else stand(k)):
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
                if not pass_(k, d):
                    continue
                if k == s:
                    return 'start'
                seen.add(k)
                if len(seen) > cap:
                    return 'open'
                queue.append(k)
        return 'shut'

    def cheapest(self, start, goals, first=False):
        """Least-cost ways (length plus twice the climb, as `route` counts)
        from start to every goal, one Dijkstra flood for all of them; it stops
        once every goal it can reach is settled."""
        out = [None]*len(goals)
        s = self.node(self.key(*start))
        want = {}
        for m, g in enumerate(goals):
            n = self.node(self.key(*g))
            if n is not None:
                want.setdefault(n, []).append(m)
        if s is None or not self._stand(s) or not want:
            return out
        c = self.t.cell
        diag = c*math.sqrt(2)
        N = self.ni*self.nj
        cost = array('d', [math.inf])*N
        prev = array('i', [-1])*N
        done = bytearray(N)
        cost[s] = 0.
        heap = [(0., s)]
        state, stand, pass_, nj = self.state, self._stand, self._pass, self.nj
        steps = tuple(enumerate(zip(self.d8, N8)))
        left = len(want)
        while heap and left:
            d, k = heapq.heappop(heap)
            if done[k]:
                continue
            done[k] = 1
            if k in want:
                left -= 1
                path = [k]
                while path[-1] != s:
                    path.append(prev[path[-1]])
                path = [self.ij(n) for n in reversed(path)]
                for m in want[k]:
                    out[m] = path
                if first:
                    break
            free = self.vouch and self._is_safe(k)
            hk = self.height(k)
            for e, (step, (di, dj)) in steps:
                n = k+step
                if done[n]:
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
                if not (free or pass_(k, e)):
                    continue
                nd = d+(diag if di and dj else c)+2*abs(self.height(n)-hk)
                if nd < cost[n]:
                    cost[n] = nd
                    prev[n] = k
                    heapq.heappush(heap, (nd, n))
        return out

    def route(self, start, goal):
        """One shortest way (A*, the lattice's least climbing length) from start to goal, or None."""
        s, g = self.key(*start), self.key(*goal)
        if not self.standable(s) or not self.standable(g):
            return None, dict(start_ok=self.standable(s), goal_ok=self.standable(g))
        c = self.t.cell
        cost = {s: 0.}
        prev = {}
        heap = [(0., 0., s)]
        while heap:
            _, d, k = heapq.heappop(heap)
            if d > cost.get(k, math.inf)+1e-12:
                continue
            if k == g:
                break
            kn = self.node(k)
            for e, (di, dj) in enumerate(N8):
                n = (k[0]+di, k[1]+dj)
                if not self.standable(n):
                    continue
                if di and dj and not (self.standable((k[0]+di, k[1])) and self.standable((k[0], k[1]+dj))):
                    continue
                if not self._pass(kn, e):
                    continue
                length = c*(math.sqrt(2) if di and dj else 1)
                nd = d+length+2*abs(self.height(self.node(n))-self.height(kn))
                if nd < cost.get(n, math.inf)-1e-12:
                    cost[n] = nd
                    prev[n] = k
                    heapq.heappush(heap, (nd+c*math.hypot(n[0]-g[0], n[1]-g[1]), nd, n))
        if g not in cost:
            return None, {}
        path = [g]
        while path[-1] != s:
            path.append(prev[path[-1]])
        path.reverse()
        return path, self.measure(path)

    def measure(self, path):
        """A way's length, steepest face, highest step and climb (each edge read in full)."""
        c = self.t.cell
        rises, grades, length = [0.], [0.], 0.
        for a, b in zip(path, path[1:]):
            r, gr = self.edge(a, b)
            rises.append(r)
            grades.append(gr)
            length += c*math.hypot(b[0]-a[0], b[1]-a[1])
        hs = [self.t.height(k[0]*c, k[1]*c) for k in path]
        return dict(length_m=round(length, 3), nodes=len(path), max_face_slope_deg=round(max(grades), 2),
                    max_step_m=round(max(rises), 4), climb_m=round(max(hs)-min(hs), 3),
                    clearance_m=2*CLEAR_R, lattice_m=c)


class Reach:
    """The nodes one flood reached from its start, each with the node it was
    reached from (so a way back to the start is at hand for any of them)."""

    def __init__(self, walker, start, parent, count):
        self.walker, self.start, self.parent, self.count = walker, start, parent, count

    def __len__(self):
        return self.count

    def _id(self, goal):
        return self.walker.node(self.walker.key(*goal))

    def has(self, goal):
        n = self._id(goal)
        return n is not None and self.parent[n] != -2

    def nodes(self):
        """Every reached node's lattice key."""
        ij = self.walker.ij
        return {ij(n) for n, p in enumerate(self.parent) if p != -2}

    def path(self, goal):
        """The flood's way from the start to `goal` (lattice keys), or None."""
        n = self._id(goal)
        if n is None or self.parent[n] == -2:
            return None
        out = [n]
        while self.parent[out[-1]] != -1:
            out.append(self.parent[out[-1]])
        ij = self.walker.ij
        return [ij(k) for k in reversed(out)]

    def route(self, goal):
        """(path, measures) like Walker.route, along the flood's own way (as few steps as any)."""
        path = self.path(goal)
        w = self.walker
        if path is None:
            return None, dict(start_ok=self.start is not None and w._stand(self.start),
                              goal_ok=w.standable(w.key(*goal)))
        return path, w.measure(path)


def holds(walker, path, extra):
    """Whether `path` (a way through the lattice that `walker` passes) still
    passes once the obstacles `extra` stand too: no node on it, and no corner
    a diagonal step cuts, lies in their lane. Edges are the ground's and do
    not change. True proves the way; False proves nothing."""
    if not extra:
        return True
    c = walker.t.cell
    index = obstacle_index(extra)

    def free(k):
        x, z = k[0]*c, k[1]*c
        return not any(blocks(o, x, z) for o in index.get((int(math.floor(x/.2)), int(math.floor(z/.2))), ()))
    if not free(path[0]):
        return False
    for a, b in zip(path, path[1:]):
        if not free(b):
            return False
        if a[0] != b[0] and a[1] != b[1] and not (free((b[0], a[1])) and free((a[0], b[1]))):
            return False
    return True


def proves(walker, path):
    """Whether `walker` (other ground, other obstacles) passes every node,
    every cut corner and every edge of `path`. True proves the way; False
    proves nothing."""
    if not path or not walker.standable(path[0]):
        return False
    for a, b in zip(path, path[1:]):
        if not walker.standable(b):
            return False
        if a[0] != b[0] and a[1] != b[1] and not (walker.standable((b[0], a[1])) and walker.standable((a[0], b[1]))):
            return False
        if not walker.passable(a, b):
            return False
    return True


def approaches(item, size, gap=.08):
    """Points a body would stand at to use a building or a yard thing: before
    its front (local -Z, a cottage's door) first, then its other sides."""
    a = math.radians(item['yaw_deg'])
    c, s = math.cos(a), math.sin(a)
    x0, _, z0 = item['position_m']
    out = []
    for lx, lz in ((0, -(size[2]/2+gap)), (0, size[2]/2+gap), (size[0]/2+gap, 0), (-(size[0]/2+gap), 0)):
        out.append((x0+c*lx+s*lz, z0-s*lx+c*lz))
    return out


YARD = ('woodpile', 'drying_line', 'firewood')


def walk_checks(doc, meshes, terrain, room, floods=None):
    """From the player's spawn to every carryable object and back carrying it,
    to every cottage door and to every workyard thing (any free side). One
    flood from each start answers every goal (`floods`, by start, may be
    shared with `sea_checks`)."""
    walker = Walker(doc, meshes, terrain, room)
    player = next((s for s in room['spawns'] if s['role'] == 'player'), room['spawns'][0])
    start = (player['position_m'][0], player['position_m'][2])
    results = []
    ok = True
    carry = [o for o in doc['objects'] if o['carriable']]
    targets = []
    for o in carry:
        at = (o['position_m'][0], o['position_m'][2])
        targets.append(('to '+o['id'], start, [at]))
        targets.append(('back carrying '+o['id'], at, [start]))
    protos = doc['prototypes']
    for k, item in enumerate(s for s in doc['scatter'] if s['prototype'] == 'cottage'):
        size = [b*s for b, s in zip(SIZES['cottage'], item['scale'])]
        targets.append(('to cottage %d door' % k, start, approaches(item, size)[:1]))
    for k, item in enumerate(s for s in doc['scatter'] if s['prototype'] in YARD):
        base = SIZES[item['prototype']] if item['prototype'] in SIZES else protos[item['prototype']]['size_m']
        size = [b*s for b, s in zip(base, item['scale'])]
        targets.append(('to %s %d' % (item['prototype'], k), start, approaches(item, size)))
    floods = {} if floods is None else floods
    for name, a, goals in targets:
        if a not in floods:
            floods[a] = walker.reach(a)
        info = {}
        path = None
        for goal in goals:
            path, info = floods[a].route(goal)
            if path is not None:
                break
        good = path is not None
        ok &= good
        results.append(dict(route=name, found=good, limit_deg=WALK_DEG, step_limit_m=STEP_M, **info))
    if not carry:
        ok = False
    return ok, results


SWIM_M = .08        # the body floats in water over this depth
WADE_DEG = 35.      # walking out of water: no step under the surface steeper (the pond's rule)
NEAR_SURFACE_M = .1  # offshore, nothing may stand within a body's height of the surface (the clean coast)


def _land_grid(doc, meshes, points=None):
    """The generator's grid read back from the package's land mesh: (grid, h),
    or (None, None) for a package without one. `points`, a dict, is filled
    with every land vertex's height by its (x, z) rounded to 0.1 mm (the
    10x world's detail vertices among them)."""
    from .field import Grid
    pts = {} if points is None else points
    for p in meshes.get('land', ()):
        for x, y, z in p['positions']:
            pts[(round(x, 4), round(z, 4))] = y
    gen = doc.get('x_generator') or {}
    if not pts or 'cell_m' not in gen:
        return None, None
    x0 = min(k[0] for k in pts)
    z0 = min(k[1] for k in pts)
    x1 = max(k[0] for k in pts)
    z1 = max(k[1] for k in pts)
    grid = Grid(x0, z0, x1, z1, gen['cell_m'])
    nx = grid.nx
    return grid, [pts.get((round(grid.xs[q % nx], 4), round(grid.zs[q//nx], 4)), -math.inf) for q in range(grid.n)]


def offshore_checks(doc, meshes, room, level):
    """The clean coast (the founder, 10 October): no walls offshore. The sea is
    the water joined to the grid's edge; everything else, and the sea within
    NEAR_SURFACE_M of the surface, is the island and its shallows. Shallows not
    joined to the island the player stands on (a reef's rocks, a sea stack, a
    rock breaking the surface) are walls offshore. Also measured: how deep the
    water is at the foot of the coast's cliffs (they drop into deep water)."""
    grid, h = _land_grid(doc, meshes)
    if grid is None:
        return False, dict(error='no land grid')
    nx, nz = grid.nx, grid.nz
    sea = [False]*grid.n
    todo = [q for q in range(grid.n) if (q % nx in (0, nx-1) or q//nx in (0, nz-1)) and h[q] < level]
    for q in todo:
        sea[q] = True
    n8 = ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1))
    while todo:
        q = todo.pop()
        i, j = q % nx, q//nx
        for di, dj in n8:
            ii, jj = i+di, j+dj
            if 0 <= ii < nx and 0 <= jj < nz and not sea[jj*nx+ii] and h[jj*nx+ii] < level:
                sea[jj*nx+ii] = True
                todo.append(jj*nx+ii)
    shallow = [not sea[q] or h[q] > level-NEAR_SURFACE_M for q in range(grid.n)]
    player = next((s for s in room['spawns'] if s['role'] == 'player'), room['spawns'][0])['position_m']
    pi, pj = grid.nearest(player[0], player[2])
    seen = [False]*grid.n
    seen[pj*nx+pi] = True
    todo = [pj*nx+pi]
    while todo:
        q = todo.pop()
        i, j = q % nx, q//nx
        for di, dj in n8:
            ii, jj = i+di, j+dj
            p = jj*nx+ii
            if 0 <= ii < nx and 0 <= jj < nz and shallow[p] and not seen[p]:
                seen[p] = True
                todo.append(p)
    walls = []
    for q0 in range(grid.n):
        if not shallow[q0] or seen[q0]:
            continue
        part, todo = [q0], [q0]
        seen[q0] = True
        while todo:
            q = todo.pop()
            i, j = q % nx, q//nx
            for di, dj in n8:
                ii, jj = i+di, j+dj
                p = jj*nx+ii
                if 0 <= ii < nx and 0 <= jj < nz and shallow[p] and not seen[p]:
                    seen[p] = True
                    todo.append(p)
                    part.append(p)
        if any(sea[q] for q in part):
            top = max(part, key=lambda q: h[q])
            walls.append(dict(at=[round(grid.xs[top % nx], 3), round(grid.zs[top//nx], 3)],
                              top_below_surface_m=round(level-h[top], 3), vertices=len(part)))
    walls.sort(key=lambda w: w['top_below_surface_m'])
    # The cliffs' feet: where the coast is a face over 60 degrees, the water a
    # short way out (0.3 m along the steepest descent).
    feet = []
    cell = grid.cell
    for q in range(grid.n):
        i, j = q % nx, q//nx
        if not (0 < i < nx-1 and 0 < j < nz-1) or not (level < h[q] < level+.03) or sea[q]:
            continue
        gx = (h[q+1]-h[q-1])/(2*cell)
        gz = (h[q+nx]-h[q-nx])/(2*cell)
        g = math.hypot(gx, gz)
        if g < math.tan(math.radians(60)):
            continue
        x, z = grid.xs[i]-gx/g*.3, grid.zs[j]-gz/g*.3
        ii, jj = grid.nearest(x, z)
        if sea[jj*nx+ii]:
            feet.append(level-h[jj*nx+ii])
    feet.sort()
    rows = dict(walls=len(walls), worst=walls[:6], near_surface_m=NEAR_SURFACE_M,
                cliff_feet=len(feet), cliff_foot_depth_m=[round(feet[0], 3), round(feet[len(feet)//2], 3)] if feet else None)
    return not walls, rows


def sea_checks(doc, meshes, terrain, room, floods=None):
    """The island: a sea at a level below the land, all round it past the reef;
    a reef a short swim off the coast; beaches a swimmer walks out of the sea
    up (water over SWIM_M deep, then no step steeper than the walk limit to dry
    sand) and reaches the spawn from on foot; the jetty reachable on foot."""
    sea = _sea(doc)
    if not sea:
        return False, dict(error='no sea in the package (x_generator.sea)')
    level = sea['level_m']
    rows = dict(level_m=level)
    ok = True
    rec = next((r for r in doc['water'] if r['mesh'] == sea['mesh']), None)
    ys = [v[1] for p in meshes[sea['mesh']] for v in p['positions']] if rec else []
    rows['sea_level_ok'] = bool(rec and rec['kind'] == 'still' and max(ys)-min(ys) <= 1e-6 and abs(ys[0]-level) < 1e-5)
    ok &= rows['sea_level_ok']
    # The playable water's edge is open sea all round, out past the reef.
    play = sea['play_area']['outline_m']
    shallow = [p for p in play if terrain.height(p[0], p[1]) > level-.1]
    rows['play_edge_points'] = len(play)
    rows['play_edge_not_deep'] = len(shallow)
    ok &= bool(play) and not shallow
    # The island stands above the sea: the player's spawn is inside the coast, on land.
    player = next((s for s in room['spawns'] if s['role'] == 'player'), room['spawns'][0])['position_m']
    coast = sea['coast']['outline_m']
    rows['spawn_on_island'] = bool(coast) and _in_loop(player[0], player[2], coast) and \
        terrain.height(player[0], player[2]) > level+.02
    ok &= rows['spawn_on_island']
    # The reef lies a short swim off the coast.
    reef = sea['reef']['outline_m']
    offs = []
    for x, z in reef:
        best = math.inf
        for a, b in zip(coast, coast[1:]+coast[:1]):
            dx, dz = b[0]-a[0], b[1]-a[1]
            L = dx*dx+dz*dz
            t = 0 if L == 0 else max(0., min(1., ((x-a[0])*dx+(z-a[1])*dz)/L))
            best = min(best, math.hypot(x-a[0]-t*dx, z-a[1]-t*dz))
        offs.append(best)
    rows['reef_offshore_m'] = [round(min(offs or [0]), 3), round(max(offs or [0]), 3)]
    ok &= bool(offs) and min(offs) >= .2 and max(offs) <= 1.
    # Beaches: walk out of the sea, then on to the spawn.
    start = (player[0], player[2])
    floods = {} if floods is None else floods
    if start not in floods:
        floods[start] = Walker(doc, meshes, terrain, room).reach(start)
    reach = floods[start]
    beaches = []
    for b in sea['beaches']:
        (wx, _, wz), (bx, _, bz) = b['water_m'], b['wash_ashore_m']
        depth0 = level-terrain.height(wx, wz)
        run = max(math.hypot(bx-wx, bz-wz), 1e-6)
        n = max(2, int(run/EDGE_SAMPLE))
        hs = [terrain.height(wx+(bx-wx)*k/n, wz+(bz-wz)*k/n) for k in range(n+1)]
        steep = wet_steep = 0.
        for k in range(n):
            if level-hs[k+1] < SWIM_M:   # feet on the bed from here on
                grade = math.degrees(math.atan(abs(hs[k+1]-hs[k])/(run/n)))
                if hs[k+1] < level:
                    wet_steep = max(wet_steep, grade)
                else:
                    steep = max(steep, grade)
        path, info = reach.route((bx, bz))
        good = depth0 >= SWIM_M and wet_steep <= WADE_DEG and steep <= WALK_DEG and hs[-1] > level+.02 \
            and path is not None
        beaches.append(dict(id=b['id'], start_depth_m=round(depth0, 3), wade_max_deg=round(wet_steep, 2),
                            dry_max_deg=round(steep, 2),
                            dry_y_m=round(hs[-1], 4), reachable=path is not None, ok=good, **info))
        ok &= good
    rows['beaches'] = beaches
    ok &= bool(beaches)
    # The clean coast: nothing offshore within a body's height of the surface.
    off_ok, rows['offshore'] = offshore_checks(doc, meshes, room, level)
    ok &= off_ok
    jetty = sea.get('jetty')
    if jetty:
        rx, ry, rz = jetty['root_m']
        path, info = reach.route((rx, rz))
        deck = terrain.height(rx, rz)
        rows['jetty'] = dict(reachable=path is not None, deck_at_root_m=round(deck, 4), **info)
        ok &= path is not None and abs(deck-jetty['deck_top_m']) < .005
    return ok, rows


def stair_rects(doc, pad=.5):
    """The ground under the stairs the generator built over a landform's top (a summit's, when its landmark is too short for
    the bands the island's own Fractals open: the routes of x_generator.fractals marked `stair`), as axis-aligned rectangles
    (x0, x1, z0, z1) in metres with `pad` round them: a flight runs along an axis."""
    out = []
    for row in ((doc.get('x_generator') or {}).get('fractals') or {}).get('routes', ()):
        if not row.get('stair'):
            continue
        (ox, oz), (di, dj) = row['origin_m'], row['direction']
        length, half = row['length_m'], row['half_width_m']
        xs = (ox, ox+di*length) if di else (ox-half, ox+half)
        zs = (oz, oz+dj*length) if dj else (oz-half, oz+half)
        out.append((min(xs)-pad, max(xs)+pad, min(zs)-pad, max(zs)+pad))
    return out


def footprint_checks(inventory, terrain, room, W=None, stairs=()):
    """Per object: where its box went and how far the land's rise is from it
    (inside the walkable floor only). The reading rule is the room's: on a
    room magnified x W (the 10x world) its metres (the ring round the box,
    the sampling step, the 15 and 25 cm the land's top may lie from the box's)
    are W times today's, as the generator's own protection of a landform's
    top is (generate's `floor_at`). `stairs` (`stair_rects`) are read without:
    a stair built over a landform's top is the island's gate, not the landform."""
    from .land import inside_distance
    W = world.W if W is None else W
    rows = []
    ok = True
    objs = inventory['objects']
    children = {}
    for e in objs:
        t = e['placement']['support'].get('target_id')
        if t:
            children.setdefault(t, []).append(e)
    for e in objs:
        b = e['box']
        sx, sy, sz = b['size_m']
        cx, cy, cz = b['centre_m']
        base, top = cy-sy/2, cy+sy/2
        a = math.radians(b['yaw_deg'])
        c, s = math.cos(a), math.sin(a)
        inside, ring = [], []
        R = math.hypot(sx, sz)/2+.7*W
        step = .03*W
        k = int(R/step)
        for dj in range(-k, k+1):
            for di in range(-k, k+1):
                x, z = cx+di*step, cz+dj*step
                lx, lz = c*(x-cx)-s*(z-cz), s*(x-cx)+c*(z-cz)
                dx, dz = abs(lx)-sx/2, abs(lz)-sz/2
                if dx <= 0 and dz <= 0 and inside_distance(room, x, z) < 0:
                    continue  # a scanned box poking through a wall: the ridge beyond is the wall's
                if dx <= 0 and dz <= 0:
                    # Skip where a taller box (a child or an overlapping
                    # neighbour) stands on this footprint: that land is its.
                    covered = False
                    for other in objs:
                        ob = other['box']
                        if other is e or ob['centre_m'][1]+ob['size_m'][1]/2 <= top:
                            continue
                        oa = math.radians(ob['yaw_deg'])
                        ox, oz = x-ob['centre_m'][0], z-ob['centre_m'][2]
                        olx, olz = math.cos(oa)*ox-math.sin(oa)*oz, math.sin(oa)*ox+math.cos(oa)*oz
                        if abs(olx) <= ob['size_m'][0]/2 and abs(olz) <= ob['size_m'][2]/2:
                            covered = True
                    if not covered and not any(a <= x <= b and c_ <= z <= d for a, b, c_, d in stairs):
                        inside.append(terrain.height(x, z))
                elif .45*W <= max(dx, dz) <= .7*W and not e['placement']['support'].get('target_id') \
                        and inside_distance(room, x, z) >= 0:
                    ring.append(terrain.height(x, z))   # surroundings within the room's floor
        land_top = max(inside)
        land_mean = sum(inside)/len(inside)
        around = sorted(ring)[len(ring)//2] if ring else base
        rise = land_top-around
        box_rise = top-(around if not e['placement']['support'].get('target_id') else base)
        # Reads if the land rises at least half the box's own rise above its
        # surroundings and its top lies within a third of the box height (min 0.15 m).
        reads = land_top-top >= -max(.15*W, sy/3) and land_top-top <= max(.25*W, sy*.45) \
            and (rise >= .5*min(box_rise, sy) or e['placement']['support'].get('target_id'))
        ok &= bool(reads)
        rows.append(dict(id=e['id'], kind=e['kind'], centre=[round(cx, 2), round(cz, 2)],
                         box_base=round(base, 3), box_top=round(top, 3), land_top=round(land_top, 3),
                         land_mean=round(land_mean, 3), top_delta_m=round(land_top-top, 3),
                         rise_above_surroundings_m=round(rise, 3), reads=bool(reads)))
    return ok, rows


CLIMB_FAIL_RISE = .1   # a knife edge a body's height up or more breaks the principle


def climb_checks(doc, meshes):
    """What you climb, you can stand on top of. The land's grid is read back
    from the package's own land mesh; from every standable foot (dry land,
    not under inland water or the sea) the climb straight up each face over
    55 degrees is followed over its top (`climb.knife_edges`). A climb that
    tops out on a knife edge or a needle a body's height up or more fails;
    lower ones are reported."""
    from .climb import knife_edges
    points = {}
    grid, h = _land_grid(doc, meshes, points)
    if grid is None:
        return True, dict(knife_edges=0, worst=[])
    level = (_sea(doc) or {}).get('level_m', -math.inf)
    water = [(vs[t[0]], vs[t[1]], vs[t[2]]) for rec in doc['water'] if rec['mesh'] != (_sea(doc) or {}).get('mesh')
             for p in meshes[rec['mesh']] for vs in (p['positions'],) for t in p['triangles']]
    wet = _wet_grid(grid, h, water)
    bad = knife_edges(grid, h, lambda q: h[q] > level+.005 and not wet[q])
    detail = (doc.get('x_generator') or {}).get('detail')
    if detail:
        # The 10x world: in its detail blocks the land is read at 2.5 cm.
        blocks = {tuple(b) for b in detail['blocks']}
        bad = [b for b in bad if _block_of(detail, *b['crest']) not in blocks]
        bad += _fine_climbs(detail, blocks, grid, h, points, water, level, meshes)
        bad.sort(key=lambda b: (-b['rise'], b['crest']))
    fails = [b for b in bad if b['rise'] >= CLIMB_FAIL_RISE]
    worst = [dict(crest=[round(b['crest'][0], 3), round(b['crest_y'], 3), round(b['crest'][1], 3)],
                  rise_m=round(b['rise'], 3), foot=[round(v, 3) for v in b['foot']]) for b in bad[:8]]
    return not fails, dict(knife_edges=len(bad), failing=len(fails), fail_rise_m=CLIMB_FAIL_RISE, worst=worst)


def _base_reach(doc, meshes, terrain, room, floods=None):
    """(the exact walk's flood from the player's spawn, its climb-aware reach for the base body: reach.Labels), made once per
    `floods` (shared by the Fractals' and the firepits' checks). On a magnified room the faces in a detail block are not the
    grid's (read at 2.5 cm), except a flight's, which keeps the base's surface."""
    from . import fractals as Fractals, reach, sites
    player = next((s for s in room['spawns'] if s['role'] == 'player'), room['spawns'][0])
    start = (player['position_m'][0], player['position_m'][2])
    floods = {} if floods is None else floods
    if start not in floods:
        floods[start] = Walker(doc, meshes, terrain, room).reach(start)
    r0 = floods[start]
    if ('base', start) in floods:
        return r0, floods[('base', start)]
    grid, h = _land_grid(doc, meshes)
    detail = (doc.get('x_generator') or {}).get('detail')
    blocks = {tuple(b) for b in detail['blocks']} if detail else set()
    nx = grid.nx
    held = set()
    for row in ((doc.get('x_generator') or {}).get('fractals') or {}).get('routes', ()):
        i0, i1, j0, j1 = sites.flight_box(Fractals.route_from_row(grid, row), 5)
        for j in range(max(0, j0), min(grid.nz, j1+1)):
            held.update(j*nx+i for i in range(max(0, i0), min(nx, i1+1)))
    per = int(round(detail['block_m']/grid.cell)) if detail else 0
    skip = (lambda i, j: (i//per, j//per) in blocks and j*nx+i not in held) if blocks else None
    cr = reach.ClimbReach(r0.walker, r0, grid, h, skip=skip)
    floods[('base', start)] = cr.run(1.)
    return r0, floods[('base', start)]


def firepit_checks(doc, meshes, terrain, room, floods=None):
    """The island's firepits (firepits.py), proved again on the written package: each inside the island, on a node the strict
    walk stands a body on, on the land as written, at the band it says (the climb-aware reach from the player's spawn; none:
    no band reaches it), every two firepits.APART_M apart, FRACTAL_CLEAR_M from every Fractal and start_clear_m() from the
    player's spawn, no more of them than the rule allows. A package without firepits has nothing to prove."""
    fp = (doc.get('x_generator') or {}).get('firepits')
    if fp is None:
        return True, dict(placed=0)
    from . import firepits as Firepits
    r0, base = _base_reach(doc, meshes, terrain, room, floods)
    w = r0.walker
    player = next((s for s in room['spawns'] if s['role'] == 'player'), room['spawns'][0])
    sx, sz = player['position_m'][0], player['position_m'][2]
    lo, hi = room['bounds']['min_m'], room['bounds']['max_m']
    fractals = [(i['position_m'][0], i['position_m'][2])
                for i in ((doc['x_generator'].get('fractals') or {}).get('items') or ())]
    items = fp['items']
    bad = []
    if len(items) > Firepits.MAX_FIREPITS:
        bad.append(dict(firepits=len(items), problems=['more than the rule allows']))
    eps = 1e-6
    for n, item in enumerate(items):
        x, y, z = item['position_m']
        why = []
        if not (lo[0] <= x <= hi[0] and lo[2] <= z <= hi[2]):
            why.append('outside the island')
        elif not w.standable(w.key(x, z)):
            why.append('no body stands there')
        elif abs(terrain.height(x, z)-y) > .002:
            why.append('not on the land')
        if base.band_at(x, z) != item.get('reach_band'):
            why.append('not the band it says')
        if math.hypot(x-sx, z-sz) < Firepits.start_clear_m()-eps:
            why.append('too near the starting campfire')
        if any(math.hypot(x-a, z-b) < Firepits.FRACTAL_CLEAR_M-eps for a, b in fractals):
            why.append('too near a Fractal')
        if any(math.hypot(x-o['position_m'][0], z-o['position_m'][2]) < Firepits.APART_M-eps for o in items[:n]):
            why.append('too near another firepit')
        if why:
            bad.append(dict(firepit=n+1, position_m=item['position_m'], problems=why))
    return not bad, dict(placed=len(items), failing=len(bad), problems=bad[:8],
                         first_run=sum(1 for i in items if i.get('reach_band') == 1), counts=fp.get('counts'))


def fractal_checks(doc, meshes, terrain, room, floods=None):
    """The mechanics round's Fractals and gates, proved again on the written package: the exact walk's climb-aware reach
    (reach.py, from the player's spawn) puts every bench of every route at the band it was built as, and every Fractal at
    the band it says (none: no band reaches it). A package without Fractals has nothing to prove."""
    fx = (doc.get('x_generator') or {}).get('fractals')
    if not fx:
        return True, dict(placed=0)
    _, base = _base_reach(doc, meshes, terrain, room, floods)
    bad, bypassed = [], []
    benches = 0
    for row in fx['routes']:
        for k, ledge in enumerate(row['ledges']):
            benches += 1
            x, _, z = ledge['bench_m']
            want = max(l['band'] for l in row['ledges'][:k+1])
            got = base.band_at(x, z)
            if got is not None and got < want and row['kind'] == 'summit':
                # The land beside a corridor cut into a landmark may offer a way up its middle (reported, not a fault).
                bypassed.append(dict(route=row['id'], ledge=k+1, built_as_band=want, reached_from=got))
            elif got != want:
                bad.append(dict(route=row['id'], ledge=k+1, built_as_band=want, reached_from=got))
    for item in fx['items']:
        x, y, z = item['position_m']
        got = base.band_at(x, z)
        if got != item.get('reach_band') or abs(terrain.height(x, z)-y) > .002:
            bad.append(dict(fractal=item['id'], says_band=item.get('reach_band'), reached_from=got))
    # The summit the manifest will carry (x_summit): its centre lies on the top bench, inside the island, and the walk
    # reaches it from the band it says (from a lower one only where the land beside the corridor offers a way round).
    summit = (doc.get('x_generator') or {}).get('summit')
    if summit:
        x, y, z = summit['center_m']
        lo, hi = room['bounds']['min_m'], room['bounds']['max_m']
        got = base.band_at(x, z)
        if not (lo[0] <= x <= hi[0] and lo[2] <= z <= hi[2]) or abs(terrain.height(x, z)-y) > .002 or not summit['radius_m'] > 0 \
                or got is None or got > summit['top_band']:
            bad.append(dict(summit=True, says_band=summit['top_band'], reached_from=got))
    return not bad, dict(placed=len(fx['items']), benches=benches, failing=len(bad), problems=bad[:8],
                         bypassed=bypassed, by_band=fx.get('counts', {}).get('by_band'))


def _wet_grid(grid, h, water):
    """Which of a grid's vertices lie under inland water (above the land there)."""
    nx = grid.nx
    wet = [False]*grid.n
    for a, b, c in water:
        rx, rz = grid.span(min(a[0], b[0], c[0]), max(a[0], b[0], c[0]), min(a[2], b[2], c[2]),
                           max(a[2], b[2], c[2]))
        for j in rz:
            for i in rx:
                if _in_tri(grid.xs[i], grid.zs[j], (a, b, c)) and max(a[1], b[1], c[1]) > h[j*nx+i]:
                    wet[j*nx+i] = True
    return wet


def _block_of(detail, x, z):
    size = detail['block_m']
    return int(math.floor((x-detail['origin_m'][0])/size)), int(math.floor((z-detail['origin_m'][1])/size))


FINE_TILE = 4   # detail blocks per side of one fine reading (`_fine_climbs`)


def _fine_climbs(detail, blocks, grid, h, points, water, level, meshes):
    """The climb check in the detail blocks, at 2.5 cm: the blocks holding a
    face over 55 degrees are read in tiles of up to 4 x 4 blocks, each on a
    2.5 cm grid reaching a climb's length (0.6 m) past them; heights are the
    land's own vertices, or the base grid's triangles between them (the
    seams' fans lie in their planes). Returns the bad climbs whose crest lies
    in a detail block."""
    from .climb import FACE_DEG, REACH_M, knife_edges
    from .field import Grid
    fc, size = detail['cell_m'], detail['block_m']
    ox, oz = detail['origin_m']
    per = int(round(size/fc))
    # Blocks with a steep face of their own (a fine triangle over 55 degrees).
    steep = set()
    tan_face = math.tan(math.radians(FACE_DEG))
    for p in meshes['land']:
        vs = p['positions']
        for t in p['triangles']:
            a, b, c = vs[t[0]], vs[t[1]], vs[t[2]]
            if max(a[0], b[0], c[0])-min(a[0], b[0], c[0]) > fc*1.01:
                continue
            ux, uy, uz = b[0]-a[0], b[1]-a[1], b[2]-a[2]
            vx, vy, vz = c[0]-a[0], c[1]-a[1], c[2]-a[2]
            nx_, ny_, nz_ = uy*vz-uz*vy, uz*vx-ux*vz, ux*vy-uy*vx
            if math.hypot(nx_, nz_) > tan_face*abs(ny_):
                blk = _block_of(detail, (a[0]+b[0]+c[0])/3, (a[2]+b[2]+c[2])/3)
                if blk in blocks:
                    steep.add(blk)
    tiles = {}
    for b in sorted(steep):
        tiles.setdefault((b[0]//FINE_TILE, b[1]//FINE_TILE), []).append(b)
    halo = int(math.ceil((REACH_M+.05)/fc))
    out = []
    for tile in sorted(tiles):
        mine = tiles[tile]
        bi0, bi1 = min(b[0] for b in mine), max(b[0] for b in mine)
        bj0, bj1 = min(b[1] for b in mine), max(b[1] for b in mine)
        fi0, fi1 = bi0*per-halo, (bi1+1)*per+halo
        fj0, fj1 = bj0*per-halo, (bj1+1)*per+halo
        tg = Grid(ox+fi0*fc, oz+fj0*fc, ox+fi1*fc, oz+fj1*fc, fc)
        tg.nx, tg.nz = fi1-fi0+1, fj1-fj0+1
        tg.n = tg.nx*tg.nz
        tg.xs = [round(ox+fi*fc, 6) for fi in range(fi0, fi1+1)]
        tg.zs = [round(oz+fj*fc, 6) for fj in range(fj0, fj1+1)]
        th = []
        for z in tg.zs:
            for x in tg.xs:
                y = points.get((round(x, 4), round(z, 4)))
                th.append(y if y is not None else grid.tri_height(h, x, z))
        x_lo, x_hi, z_lo, z_hi = tg.xs[0], tg.xs[-1], tg.zs[0], tg.zs[-1]
        wet = _wet_grid(tg, th, [w for w in water if max(v[0] for v in w) >= x_lo and min(v[0] for v in w) <= x_hi
                                  and max(v[2] for v in w) >= z_lo and min(v[2] for v in w) <= z_hi])
        near = [(ox+(b[0]+.5)*size, oz+(b[1]+.5)*size, .72*size) for b in mine]
        here = set(mine)
        for b in knife_edges(tg, th, lambda q: th[q] > level+.005 and not wet[q], near=near):
            if _block_of(detail, *b['crest']) in here:
                out.append(b)
    return out


def run(package, room_dir):
    # A magnified room's package is read against the room magnified (the harness's magnify_room).
    doc, meshes, room, stats = read_package(package, room_dir)
    inventory = world.magnify_inventory(load_json(Path(room_dir)/'inventory.json'), world_scale(doc))
    terrain = Terrain(doc, meshes)
    w_ok, water, joins = water_checks(doc, meshes, terrain)
    g_ok, ground = grounding_checks(doc, terrain)
    floods = {}
    p_ok, walks = walk_checks(doc, meshes, terrain, room, floods)
    f_ok, prints = footprint_checks(inventory, terrain, room, world_scale(doc), stair_rects(doc))
    s_ok, sea = sea_checks(doc, meshes, terrain, room, floods)
    c_ok, climbs = climb_checks(doc, meshes)
    x_ok, fractals = fractal_checks(doc, meshes, terrain, room, floods)
    fp_ok, firepits = firepit_checks(doc, meshes, terrain, room, floods)
    # The climb principle is reported beside the others; it joins `ok` once the
    # generator meets it on the whole corpus (tall object crests still fail it).
    return dict(ok=w_ok and g_ok and p_ok and f_ok and s_ok and x_ok and fp_ok, water_ok=w_ok, grounded_ok=g_ok,
                walk_ok=p_ok, footprints_ok=f_ok, sea_ok=s_ok, climb_ok=c_ok, fractals_ok=x_ok, firepits_ok=fp_ok, water=water,
                joins=joins, grounding=ground, walks=walks, footprints=prints, sea=sea, climbs=climbs, fractals=fractals,
                firepits=firepits, stats=stats)


def main():
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
    for w in result['water']:
        print('WATER', json.dumps(w, sort_keys=True))
    for j in result['joins']:
        print('JOIN', json.dumps(j, sort_keys=True))
    built = [g for g in result['grounding'] if g['kind'] == 'built']
    bad = [g for g in result['grounding'] if not g['ok']]
    print('GROUNDED built=%d worst_hang_m=%.4f worst_sunk_m=%.4f failures=%d' % (
        len(built), max([g['hang_m'] for g in built] or [0]), max([g['sunk_m'] for g in built] or [0]), len(bad)))
    for g in bad[:10]:
        print('  NOT_GROUNDED', json.dumps(g, sort_keys=True))
    for w in result['walks']:
        print('WALK', json.dumps(w, sort_keys=True))
    for f in result['footprints']:
        print('FOOTPRINT %-16s %-14s top %.3f land %.3f delta %+.3f rise %.3f %s' % (
            f['id'], f['kind'], f['box_top'], f['land_top'], f['top_delta_m'], f['rise_above_surroundings_m'],
            'reads' if f['reads'] else 'LOST'))
    sea = result['sea']
    for b in sea.get('beaches', ()):
        print('BEACH', json.dumps(b, sort_keys=True))
    print('SEA', json.dumps({k: v for k, v in sea.items() if k != 'beaches'}, sort_keys=True))
    print('CLIMB', json.dumps(result['climbs'], sort_keys=True))
    if result['fractals'].get('placed'):
        print('FRACTALS', json.dumps(result['fractals'], sort_keys=True))
    if result['firepits'].get('placed'):
        print('FIREPITS', json.dumps(result['firepits'], sort_keys=True))
    print('CHECKS water=%s grounded=%s walk=%s footprints=%s sea=%s climb=%s fractals=%s firepits=%s overall=%s' % (
        result['water_ok'], result['grounded_ok'], result['walk_ok'], result['footprints_ok'], result['sea_ok'],
        result['climb_ok'], result['fractals_ok'], result['firepits_ok'], result['ok']))
    raise SystemExit(0 if result['ok'] else 1)


if __name__ == '__main__':
    main()
