"""The plant maker (Run 4, FLORA-1): procedural trees from species sheets, pinned as bytes. README.md.

What the rest of the pipeline reads (the pinned files only; nothing here grows a tree at run time):

    from pipeline.landscape.generator import flora
    flora.names()               ['oak_cards', 'oak_clumps']
    flora.load(name)            the pinned record, checked (pinned.check)
    flora.prototype(name)       (parts, size_m): the finest level in the kit's shape, for a package's prototypes
    flora.levels(name)          the three levels' parts, finest first, all in one frame
    flora.collision(name)       trunk, pole and caps in the plant's own frame, in the shape export/trees.py fits
    flora.placed(name, scale, position_m, yaw_deg)   that collision set down in the world (round caps turn freely)
    flora.stand(name, x, z, scale, yaw_deg)          where the pivot goes for the trunk to stand at (x, z)
"""
import math

from . import pinned

_CACHE = {}


def names():
    return pinned.names()


def load(name):
    if name not in _CACHE:
        _CACHE[name] = pinned.load(name)
    return _CACHE[name]


def _kit(part):
    """A pinned part as the kit's and the exporter's meshes are: role, positions, triangles, tints of four."""
    return dict(role=part['role'], positions=[list(p) for p in part['positions']],
                triangles=[list(f) for f in part['triangles']], tints=[[*t, 1] for t in part['tints']])


def levels(name):
    return [[_kit(part) for part in level['parts']] for level in load(name)['levels']]


def prototype(name):
    record = load(name)
    return levels(name)[0], list(record['size_m'])


def collision(name):
    """In the plant's own frame, unscaled: export/trees.py's `fit` shape (caps as (x, z, rim half x, rim half z,
    rim y, top y, top half x, top half z))."""
    c = load(name)['collision']
    return dict(axis=tuple(c['axis_m']), trunk_radius=c['trunk_radius_m'], pole_radius=c['pole_radius_m'],
                trunk_top=c['trunk_top_m'], pole_top=c['pole_top_m'], caps=[tuple(cap) for cap in c['caps']])


def placed(name, scale, position_m, yaw_deg):
    """The collision of one copy: scaled, turned about +Y as the exporter turns a mesh (export/mesh.py transform)
    and set at its place. Level sizes take the larger of the two level scales, so an uneven copy's caps stay round
    and never overhang less than drawn. Fitting to the ground (a rim 12 cm over the land) stays the exporter's."""
    c = collision(name)
    sx, sy, sz = scale
    wide = max(sx, sz)
    angle = math.radians(yaw_deg)
    cos, sin = math.cos(angle), math.sin(angle)

    def spot(x, z):
        x, z = x*sx, z*sz
        return position_m[0]+cos*x+sin*z, position_m[2]-sin*x+cos*z

    def up(y):
        return position_m[1]+y*sy
    caps = []
    for x, z, rim_x, rim_z, rim, top, top_x, top_z in c['caps']:
        cx, cz = spot(x, z)
        caps.append((cx, cz, rim_x*wide, rim_z*wide, up(rim), up(top), top_x*wide, top_z*wide))
    return dict(axis=spot(*c['axis']), trunk_radius=c['trunk_radius']*wide, pole_radius=c['pole_radius']*wide,
                trunk_top=up(c['trunk_top']), pole_top=up(c['pole_top']), caps=caps)


def stand(name, x, z, scale, yaw_deg):
    """The (x, z) to give a copy's pivot so that its trunk stands at (x, z). The pivot is the bottom centre of the
    plant's bounds, as every prototype's is, and a crown is never quite centred on its trunk."""
    ax, az = load(name)['collision']['axis_m']
    ax, az = ax*scale[0], az*scale[2]
    angle = math.radians(yaw_deg)
    cos, sin = math.cos(angle), math.sin(angle)
    return x-(cos*ax+sin*az), z-(cos*az-sin*ax)
