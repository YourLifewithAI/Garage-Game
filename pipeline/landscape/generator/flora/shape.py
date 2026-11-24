"""Skins: a stem's tube, a leaf clump's solid, a leaf card. Front faces wind counter-clockwise seen from outside,
as the kit's do (harness/kit.py), in metres."""
from . import det
from .det import TAU, add, cos, cross, mul, sin, sub, unit

_T = (1.+det.sqrt(5.))/2.
_N = det.sqrt(1.+_T*_T)
# A twenty-sided solid: twelve corners, each a unit from the middle.
ICO = (tuple((x/_N, y/_N, z/_N) for x, y, z in (
    (-1, _T, 0), (1, _T, 0), (-1, -_T, 0), (1, -_T, 0), (0, -1, _T), (0, 1, _T), (0, -1, -_T), (0, 1, -_T),
    (_T, 0, -1), (_T, 0, 1), (-_T, 0, -1), (-_T, 0, 1))),
       ((0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11), (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6),
        (7, 1, 8), (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9), (4, 9, 5), (2, 4, 11), (6, 2, 10),
        (8, 6, 7), (9, 8, 1)))
# An eight-sided one, for the farthest level.
OCTA = (((1., 0., 0.), (-1., 0., 0.), (0., 1., 0.), (0., -1., 0.), (0., 0., 1.), (0., 0., -1.)),
        ((0, 2, 4), (2, 1, 4), (1, 3, 4), (3, 0, 4), (2, 0, 5), (1, 2, 5), (3, 1, 5), (0, 3, 5)))


def tube(pts, radii, sides, turn=0., swell=None):
    """A stem's skin: a ring of `sides` corners round every point but the last, which is its tip. `swell(k, angle)`
    scales ring k's radius at an angle round the stem (the root flare's ridges). Rings are carried up the stem
    without twisting, and a ring at a bend faces half-way between the way in and the way out."""
    positions, triangles = [], []
    u = None
    for k in range(len(pts)-1):
        t = unit(sub(pts[k+1], pts[k]))
        if k:
            t = unit(add(t, unit(sub(pts[k], pts[k-1]))), t)
        u = det.side(t)[0] if u is None else det.carry(u, t)
        v = cross(u, t)
        for j in range(sides):
            a = turn+TAU*j/sides
            r = radii[k]*(swell(k, a) if swell else 1.)
            positions.append(add(pts[k], mul(add(mul(u, cos(a)), mul(v, sin(a))), r)))
        if k:
            low, high = (k-1)*sides, k*sides
            for j in range(sides):
                n = (j+1) % sides
                triangles += [[low+j, high+j, high+n], [low+j, high+n, low+n]]
    tip, last = len(positions), (len(pts)-2)*sides
    positions.append(pts[-1])
    for j in range(sides):
        triangles.append([last+j, tip, last+(j+1) % sides])
    return positions, triangles


def tube_triangles(points, sides):
    """Triangles in tube() for a stem of `points` points."""
    return sides*(2*points-3)


def blob(centre, rh, rv, rng, jitter, under, solid=ICO):
    """A leaf clump: the solid stretched to rh across and rv up, each corner pushed in or out a little, the
    underside flattened (leaves hang in layers: a clump is flatter below), turned about the upright."""
    corners, faces = solid
    a = rng.random()*TAU
    c, s = cos(a), sin(a)
    positions = []
    for x, y, z in corners:
        k = 1.+jitter*(2.*rng.random()-1.)
        x, y, z = x*k, y*k, z*k
        x, z = c*x+s*z, c*z-s*x
        if y < 0.:
            y *= under
        positions.append((centre[0]+x*rh, centre[1]+y*rv, centre[2]+z*rh))
    return positions, [list(f) for f in faces]


def card(centre, across, up):
    """A leaf card: a flat four-cornered sheet through `centre`, `across` and `up` its half-sides. Drawn from both
    sides. Its picture's u runs along `across` and v along `up`."""
    positions = [sub(sub(centre, across), up), sub(add(centre, across), up),
                 add(add(centre, across), up), add(sub(centre, across), up)]
    return positions, [[0, 1, 2], [0, 2, 3]], [(0., 0.), (1., 0.), (1., 1.), (0., 1.)]


def top_hit(positions, triangles, x, z):
    """The highest point of a skin over (x, z), or None."""
    best = None
    for f in triangles:
        a, b, c = positions[f[0]], positions[f[1]], positions[f[2]]
        det_ = (b[2]-c[2])*(a[0]-c[0])+(c[0]-b[0])*(a[2]-c[2])
        if abs(det_) < 1e-14:
            continue
        u = ((b[2]-c[2])*(x-c[0])+(c[0]-b[0])*(z-c[2]))/det_
        v = ((c[2]-a[2])*(x-c[0])+(a[0]-c[0])*(z-c[2]))/det_
        if min(u, v, 1.-u-v) >= -1e-9:
            y = u*a[1]+v*b[1]+(1.-u-v)*c[1]
            if best is None or y > best:
                best = y
    return best
