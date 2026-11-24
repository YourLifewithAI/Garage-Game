"""The plant maker: a species sheet in, a pinned plant out.

    python -B -m pipeline.landscape.generator.flora.make             rebuild every pinned plant from its sheet
    python -B -m pipeline.landscape.generator.flora.make --check     fail if a pinned plant differs from its rebuild
    python -B -m pipeline.landscape.generator.flora.make --report    the counts of each, and nothing written
    python -B -m pipeline.landscape.generator.flora.make --preview <folder>   outlines as PNG, for the maker's eye

A pinned plant (pinned/<name>.json, README.md "The pinned plant") is three levels of detail, each a bark part and
a leaf part in the kit's shape (positions, triangles, role, tints); the climbing collision (trunk, pole and at most
eight caps, in the plant's own frame); the skeleton it was skinned from; and its counts. It is bytes: the same
sheet and seed give the same file on every machine (det.py).
"""
import argparse
import sys
from pathlib import Path

from pipeline.landscape.harness.common import require
from . import det, pinned, shape, sheet as Sheet
from .det import clamp, dot, lerp, mul, sqrt, sub
from .grow import UP, grow

MAKER_VERSION = 1
TOP_INSET_M = .01        # a perch sits this far under the leaves' top (export/trees.py)
POLE_TOP_GAP_M = .005    # the hidden pole stops this far under its perch (export/trees.py)
PERCH_MARGIN_M = .025    # a perch is at least the pole and this much round it (export/trees.py)
BLOB_TRIANGLES = 20      # a leaf clump is a twenty-sided solid


class Clump:
    """A mass of leaves: where, how wide and tall, the twig it hangs from and the limb that bears it."""
    __slots__ = ('centre', 'rh', 'rv', 'twig', 'limb', 'weight')

    def __init__(self, centre, rh, rv, twig, limb, weight):
        self.centre, self.rh, self.rv, self.twig, self.limb, self.weight = centre, rh, rv, twig, limb, weight


def _mean(points):
    x = y = z = 0.
    for p in points:
        x, y, z = x+p[0], y+p[1], z+p[2]
    n = float(len(points))
    return (x/n, y/n, z/n)


def _level(a, b):
    """Distance between two points on the level (x and z only)."""
    dx, dz = a[0]-b[0], a[2]-b[2]
    return sqrt(dx*dx+dz*dz)


def _nearest(points, to):
    best, best_d = 0, None
    for k, p in enumerate(points):
        e = sub(p, to)
        d = dot(e, e)
        if best_d is None or d < best_d:
            best, best_d = k, d
    return best


# ---------- fitting the wood to the triangle cap ----------

def trunk_points(tree, detail):
    """The trunk's rings: every joint, with two more inside the root flare at the finest detail, one at the
    middle one, and only the ground, the bole's top and the tip at the coarsest."""
    axis, t = tree.axes[0], tree.sheet['trunk']
    bole, r = axis.pts[1][1], axis.radii[1]
    flare_top = t['flare_height']*bole

    def girth(y):
        k = max(0., 1.-y/flare_top)
        return r*(1.+(t['flare']-1.)*k*k)
    below = [0.] if detail == 2 else [0., flare_top] if detail == 1 else [0., .3*flare_top, flare_top]
    below = [y for y in below if y < bole-1e-6]
    above = [axis.pts[1], axis.pts[-1]] if detail == 2 else axis.pts[1:]
    radii = [girth(y) for y in below]+([axis.radii[1], 0.] if detail == 2 else axis.radii[1:])
    return [(0., y, 0.) for y in below]+list(above), radii, flare_top


def bark_triangles(tree):
    sheet = tree.sheet
    total = shape.tube_triangles(len(trunk_points(tree, 0)[0]), sheet['trunk']['sides'])
    for a in tree.alive()[1:]:
        total += shape.tube_triangles(len(a.pts), sheet['orders'][a.order-1]['sides'])
    return total


def fit(tree):
    """Take the weakest childless stems off until the bark leaves room for the sheet's leaves under the cap of the
    finest level. Returns how many went."""
    sheet = tree.sheet
    room = sheet['levels'][0]-sheet['leaves']['count']*BLOB_TRIANGLES
    pruned = 0
    while bark_triangles(tree) > room:
        ends = [a for a in tree.alive()[1:] if not a.children]
        require(ends, f"{sheet['species']}: the trunk alone is over the triangle cap")
        deepest = max(a.order for a in ends)
        weakest = min((a for a in ends if a.order == deepest), key=lambda a: (len(a.light), a.index))
        tree.drop(weakest)
        tree.weigh()
        pruned += 1
    return pruned


# ---------- bark ----------

def bark(tree, detail, limbs=None):
    """The bark part of one level: 0 every stem, 1 the trunk and its `limbs` thickest limbs in three-sided skins,
    2 the trunk only."""
    sheet = tree.sheet
    t = sheet['trunk']
    part = dict(role='bark', positions=[], triangles=[], tints=[])

    def put(positions, triangles, tints):
        offset = len(part['positions'])
        part['positions'] += positions
        part['triangles'] += [[f[0]+offset, f[1]+offset, f[2]+offset] for f in triangles]
        part['tints'] += tints
    pts, radii, flare_top = trunk_points(tree, detail)
    phase = tree.rng.fork('roots').random()*det.TAU

    def ridge(k, a):   # the root flare's ridges: deepest at the ground, gone at the flare's top
        y = pts[k][1]
        if y >= flare_top or not t['roots']:
            return 1.
        fade = 1.-y/flare_top
        return 1.+t['root_depth']*fade*fade*det.cos(t['roots']*a-phase)
    sides = t['sides'] if detail == 0 else max(4, t['sides']//2+1) if detail == 1 else 3
    positions, triangles = shape.tube(pts, radii, sides, 0., ridge)
    # The foot of the trunk is darker: damp, and in its own shade.
    put(positions, triangles, [_grey(lerp(.7, 1., clamp(p[1]/flare_top, 0., 1.))) for p in positions])
    if detail == 2:
        return part
    stems = tree.alive()[1:]
    if detail == 1:
        thick = sorted((a for a in stems if a.order == 1), key=lambda a: (-a.pipes[0], a.index))[:limbs]
        stems = sorted(thick, key=lambda a: a.index)
    for a in stems:
        turn = tree.rng.fork(f'skin {a.index}').random()*det.TAU
        if detail == 0:
            positions, triangles = shape.tube(a.pts, a.radii, sheet['orders'][a.order-1]['sides'], turn)
        else:
            mid = (len(a.pts)-1)//2
            keep = [0, mid, len(a.pts)-1] if mid else [0, len(a.pts)-1]
            positions, triangles = shape.tube([a.pts[k] for k in keep], [a.radii[k] for k in keep], 3, turn)
        put(positions, triangles, [_grey(1.)]*len(positions))
    return part


def _grey(k):
    return (k, k, k)


# ---------- leaves ----------

def leaf_clumps(tree, count):
    """The leaves as at most `count` clumps: every leaf-bearing tip with its light, the nearest joined until few
    enough. A clump sits between its tips and its light, as wide as its light is spread."""
    leaves = tree.sheet['leaves']
    seeds = [([a], list(a.light)) for a in tree.tips() if a.light]
    mids = [_mean([tree.light[k] for k in light]) for _, light in seeds]
    while len(seeds) > count:
        best = None
        for i in range(len(seeds)):
            for j in range(i+1, len(seeds)):
                e = sub(mids[i], mids[j])
                d = dot(e, e)
                if best is None or d < best[0]:
                    best = (d, i, j)
        _, i, j = best
        seeds[i] = (seeds[i][0]+seeds[j][0], seeds[i][1]+seeds[j][1])
        mids[i] = _mean([tree.light[k] for k in seeds[i][1]])
        del seeds[j], mids[j]
    clumps = []
    for (tips, light), mid in zip(seeds, mids):
        ends = [a.pts[-1] for a in tips]
        centre = det.mix(_mean(ends), mid, .6)
        spread = 0.
        for k in light:
            d = _level(tree.light[k], centre)
            spread += d*d
        rh = clamp(leaves['cover']*sqrt(2.*spread/len(light)), leaves['size_m'][0], leaves['size_m'][1])
        twig = _nearest(ends, centre)
        clumps.append(Clump(centre, rh, rh*leaves['flat'], ends[twig], tips[twig].limb, len(light)))
    return clumps


def crowned(clumps):
    """True where leaves stand over the trunk's axis: the climber comes up the hidden pole through them."""
    return any(_level(c.centre, (0., 0., 0.)) < .5*c.rh for c in clumps)


def crown_clump(tree, clumps):
    """A clump over the trunk's axis for a crown that left a hole there: as high as the leaves round it."""
    near = sorted(clumps, key=lambda c: (_level(c.centre, (0., 0., 0.)), c.centre))[:3]
    sizes = sorted(c.rh for c in clumps)
    rh = sizes[len(sizes)//2]
    centre = (0., max(c.centre[1] for c in near), 0.)
    ends = [a.pts[-1] for a in tree.tips()]
    return Clump(centre, rh, rh*tree.sheet['leaves']['flat'], ends[_nearest(ends, centre)], -1, 1)


def mass(tree, clumps):
    """One clump that stands for several: as tall as they reach, as wide as they lie spread."""
    under = tree.sheet['leaves']['under']
    weight = x = z = 0.
    for c in clumps:
        w = c.rh*c.rh*c.rv
        weight, x, z = weight+w, x+w*c.centre[0], z+w*c.centre[2]
    x, z = x/weight, z/weight
    low = min(c.centre[1]-c.rv*under for c in clumps)
    high = max(c.centre[1]+c.rv for c in clumps)
    rv = (high-low)/(1.+under)
    centre = (x, high-rv, z)
    spread = 0.
    for c in clumps:
        d = _level(c.centre, centre)
        spread += c.rh*c.rh*c.rv*(2.*d*d+c.rh*c.rh)
    twigs = [c.twig for c in clumps]
    total = 0
    for c in clumps:
        total += c.weight
    return Clump(centre, sqrt(spread/weight), rv, twigs[_nearest(twigs, centre)], clumps[0].limb, total)


def by_limb(clumps):
    """The clumps grouped by the limb that bears them, in the order the limbs first appear."""
    groups, order = {}, []
    for c in clumps:
        if c.limb not in groups:
            groups[c.limb] = []
            order.append(c.limb)
        groups[c.limb].append(c)
    return [groups[k] for k in order]


def join_to(tree, groups, count):
    """Groups of clumps joined, the nearest two first, until at most `count` are left."""
    groups = [list(g) for g in groups]
    while len(groups) > max(1, count):
        mids = [mass(tree, g).centre for g in groups]
        best = None
        for i in range(len(groups)):
            for j in range(i+1, len(groups)):
                e = sub(mids[i], mids[j])
                d = dot(e, e)
                if best is None or d < best[0]:
                    best = (d, i, j)
        _, i, j = best
        groups[i] += groups[j]
        del groups[j]
    return groups


def far_crown(tree, finest, sectors, rings):
    """The far tree's leaves: a lantern drawn round the finest level's leaves, so the far outline is the near one.
    `rings` rings of `sectors` corners between a point below and a point above; each corner stands as far out as
    the finest leaves reach in its slice of height and its sector."""
    low, high = min(p[1] for p in finest), max(p[1] for p in finest)
    x = z = 0.
    for p in finest:
        x, z = x+p[0], z+p[2]
    x, z = x/len(finest), z/len(finest)
    edges = [(det.cos(det.TAU*j/sectors), det.sin(det.TAU*j/sectors)) for j in range(sectors)]
    mids = [(det.cos(det.TAU*(j+.5)/sectors), det.sin(det.TAU*(j+.5)/sectors)) for j in range(sectors)]
    reach = [[0.]*sectors for _ in range(rings)]
    for p in finest:
        k = min(rings-1, int((p[1]-low)/(high-low)*rings))
        dx, dz = p[0]-x, p[2]-z
        for j in range(sectors):
            a, b = edges[j], edges[(j+1) % sectors]
            if a[0]*dz-a[1]*dx >= 0. and b[0]*dz-b[1]*dx < 0.:
                reach[k][j] = max(reach[k][j], dx*mids[j][0]+dz*mids[j][1])
                break
    positions = [(x, low, z)]
    for k in range(rings):
        filled = [r for r in reach[k] if r > 0.]
        floor = .5*max(filled) if filled else 2.*tree.axes[0].radii[1]   # an empty slice is drawn in, not to a point
        y = low+(high-low)*(k+.5)/rings
        for j in range(sectors):
            r = .94*max(reach[k][j], floor)
            positions.append((x+r*mids[j][0], y, z+r*mids[j][1]))
    positions.append((x, high, z))
    top = len(positions)-1
    triangles = []
    for j in range(sectors):
        n = (j+1) % sectors
        triangles.append([0, 1+j, 1+n])
        triangles.append([top, 1+(rings-1)*sectors+n, 1+(rings-1)*sectors+j])
        for k in range(rings-1):
            a, b = 1+k*sectors, 1+(k+1)*sectors
            triangles += [[a+j, b+j, b+n], [a+j, b+n, a+n]]
    return positions, triangles


def card_fan(clump, count, rng, middle, under):
    """A clump as leaf cards filling the clump's own span: upright ones fanned about the upright, and one lying
    nearly flat with its outer edge dipped, which is what a body under the tree looks up at."""
    out = det.unit((clump.centre[0]-middle[0], 0., clump.centre[2]-middle[2]), (1., 0., 0.))
    half = clump.rv*(1.+under)/2.
    centre = (clump.centre[0], clump.centre[1]+clump.rv*(1.-under)/2., clump.centre[2])
    start = rng.random()*det.TAU
    positions, triangles, uvs = [], [], []

    def put(made):
        offset = len(positions)
        positions.extend(made[0])
        triangles.extend([[f[0]+offset, f[1]+offset, f[2]+offset] for f in made[1]])
        uvs.extend(made[2])
    for j in range(count-1):
        a = start+det.PI*j/(count-1)+.3*(rng.random()-.5)
        across = (det.cos(a)*clump.rh, 0., det.sin(a)*clump.rh)
        up = det.add(mul(UP, half), mul(out, half*.3*rng.random()))
        put(shape.card(centre, across, up))
    flat_across = mul(det.cross(UP, out), clump.rh*.75)
    flat_up = det.add(mul(out, clump.rh*.75), mul(UP, -half*.3))
    put(shape.card((centre[0], centre[1]-.3*half, centre[2]), flat_across, flat_up))
    return positions, triangles, uvs


def foliage(tree, clumps, style, label, solid=shape.ICO, cards=None, within=None):
    """The leaf part of one level. Every corner knows its clump, and every clump its twig point, so winter can
    draw the leaves in to the twig and bare the branching. Corners are shaded for a storybook crown: lit above,
    dark and cool beneath, darker deep inside."""
    leaves, crown = tree.sheet['leaves'], tree.crown
    part = dict(role='foliage', positions=[], triangles=[], tints=[], clump=[], twigs_m=[c.twig for c in clumps])
    if style == 'cards':
        part['uvs'] = []
    middle = crown.middle()
    for k, c in enumerate(clumps):
        rng = tree.rng.fork(f'{label} {k}')
        bright = lerp(.88, 1., rng.random())
        if style == 'cards':
            positions, triangles, uvs = card_fan(c, cards or leaves['cards'], rng, middle, leaves['under'])
            part['uvs'] += uvs
        else:
            positions, triangles = shape.blob(c.centre, c.rh, c.rv, rng, leaves['jitter'], leaves['under'], solid)
        if within:   # a lighter level never swells past the finest one's leaves
            positions = [tuple(clamp(p[i], within[0][i], within[1][i]) for i in range(3)) for p in positions]
        offset = len(part['positions'])
        part['positions'] += positions
        part['triangles'] += [[f[0]+offset, f[1]+offset, f[2]+offset] for f in triangles]
        part['clump'] += [k]*len(positions)
        deep = lerp(1.-.5*leaves['shade'], 1., clamp(crown.depth(c.centre), 0., 1.))
        low, span = c.centre[1]-c.rv*leaves['under'], c.rv*(1.+leaves['under'])
        for p in positions:
            lit = det.smooth((p[1]-low)/span)
            v = bright*deep*lerp(1.-leaves['shade'], 1., lit)
            part['tints'].append((v*lerp(.8, 1., lit), v, v*lerp(1., .82, lit)))
    return part


# ---------- climbing ----------

def climbing(tree, clumps, leaf_part):
    """The collision a tree's row carries (contracts/README.md, "Plant copies"), in the plant's own frame: the
    trunk as a tapering post to the first limbs, a hidden pole on up the same axis, and at most eight caps, each a
    round collar under a flat round perch on top of a mass of leaves. Round, so a copy may be turned any way.

    The first cap stands over the axis: the climber comes up the pole through it. The others crown the limbs'
    masses, the heaviest first. Every perch is the drawn leaves' top there, less a centimetre."""
    sheet = tree.sheet
    t, climb = sheet['trunk'], sheet['climb']
    trunk = tree.axes[0]
    pole_radius = trunk.radii[1]
    least = pole_radius+PERCH_MARGIN_M

    def cap(x, z, across, deep):
        top = shape.top_hit(leaf_part['positions'], leaf_part['triangles'], x, z)
        if top is None:
            return None
        top -= TOP_INSET_M
        perch = max(least, climb['perch']*.7*across)
        rim = max(1.25*perch, .7*across)
        rim_y = top-max(.02, .5*deep)
        return (x, z, rim, rim, rim_y, top, perch, perch)
    over = [c for c in clumps if _level(c.centre, (0., 0., 0.)) < .5*c.rh]
    require(over, f"{sheet['species']}: no leaves over the trunk's axis")
    widest = max(over, key=lambda c: (c.rh, c.centre))
    first = cap(0., 0., widest.rh, widest.rv)
    require(first is not None, f"{sheet['species']}: no leaves over the trunk's axis")
    caps = [first]
    groups = sorted(by_limb(clumps), key=lambda g: (-sum(c.weight for c in g), g[0].centre))
    for group in groups:
        if len(caps) >= climb['caps']:
            break
        crest = max(group, key=lambda c: (c.centre[1]+c.rv, c.centre))
        x, z = crest.centre[0], crest.centre[2]
        reach = max(crest.rh, .6*max(_level(c.centre, crest.centre)+c.rh for c in group))
        made = cap(x, z, min(reach, 2.2*crest.rh), crest.rv)
        if made is None or made[4] <= trunk.pts[1][1]:
            continue
        if any(_level((x, 0., z), (c[0], 0., c[1])) < made[6]+c[6] for c in caps):
            continue   # its perch would run into one already made
        caps.append(made)
    caps = [caps[0]]+sorted(caps[1:], key=lambda c: (-c[5], c[0], c[1]))
    trunk_top = trunk.pts[1][1]
    pole_top = caps[0][5]-POLE_TOP_GAP_M
    require(pole_top > trunk_top, f"{sheet['species']}: the leaves over the axis are no higher than the first limbs")
    return dict(axis=(0., 0.), trunk_radius=pole_radius*(1.+(t['flare']-1.)/2.), pole_radius=pole_radius,
                trunk_top=trunk_top, pole_top=pole_top, caps=caps)


# ---------- the whole plant ----------

def build(sheet, style, name):
    """A sheet made into a plant: the record pinned/<name>.json holds (pinned.pack gives its bytes)."""
    require(style in Sheet.STYLES, f'no leaf style {style!r}')
    tree = grow(sheet)
    pruned = fit(tree)
    leaves, caps = sheet['leaves'], sheet['levels']
    clumps = leaf_clumps(tree, leaves['count'])
    require(clumps, f"{sheet['species']}: no tip reached any light")
    if not crowned(clumps):
        clumps = leaf_clumps(tree, leaves['count']-1)
        clumps.append(crown_clump(tree, clumps))
    solids = foliage(tree, clumps, 'clumps', 'leaf')   # what the caps stand on, whichever way the leaves are drawn
    collision = climbing(tree, clumps, solids)

    # The finest level: every stem, every clump.
    levels = [[bark(tree, 0), solids if style == 'clumps' else foliage(tree, clumps, 'cards', 'leaf')]]
    finest = levels[0][1]['positions']
    within = ([min(p[i] for p in finest) for i in range(3)], [max(p[i] for p in finest) for i in range(3)])
    # The middle level: the trunk, its thickest limbs, and the clumps joined into as many masses as the cap allows.
    # (Counted in solid clumps for cards too, so both ways join the same masses: three cards a mass.)
    limbs = sum(a.order == 1 for a in tree.alive())
    while limbs and len(bark(tree, 1, limbs)['triangles'])+BLOB_TRIANGLES*min(4, len(clumps)) > caps[1]:
        limbs -= 1
    wood = bark(tree, 1, limbs)
    room = (caps[1]-len(wood['triangles']))//BLOB_TRIANGLES
    require(room >= 1, f"{sheet['species']}: the middle level's cap has no room for leaves")
    masses = [mass(tree, g) for g in join_to(tree, [[c] for c in clumps], room)]
    levels.append([wood, foliage(tree, masses, style, 'mass', cards=3, within=within)])
    # The far level: a three-sided trunk, and the crown as one outline (a lantern of solid leaves, or three cards).
    wood = bark(tree, 2)
    room = caps[2]-len(wood['triangles'])
    far = mass(tree, clumps)
    if style == 'cards':
        require(room >= 6, f"{sheet['species']}: the far level's cap has no room for leaves")
        levels.append([wood, foliage(tree, [far], style, 'far', cards=3, within=within)])
    else:
        sectors, rings = next(((s_, r_) for s_, r_ in ((6, 4), (5, 4), (5, 3), (4, 3), (4, 2), (3, 2), (3, 1))
                               if 2*s_*r_ <= room), (0, 0))
        require(sectors, f"{sheet['species']}: the far level's cap has no room for leaves")
        positions, triangles = far_crown(tree, finest, sectors, rings)
        low, high = within[0][1], within[1][1]
        part = dict(role='foliage', positions=positions, triangles=triangles, clump=[0]*len(positions),
                    twigs_m=[far.twig], tints=[])
        for p_ in positions:
            lit = det.smooth((p_[1]-low)/(high-low))
            v = .94*lerp(1.-leaves['shade'], 1., lit)
            part['tints'].append((v*lerp(.8, 1., lit), v, v*lerp(1., .82, lit)))
        levels.append([wood, part])

    counts = [sum(len(p['triangles']) for p in parts) for parts in levels]
    for k, (count, cap) in enumerate(zip(counts, caps)):
        require(count <= cap, f"{sheet['species']}: level {k} has {count} triangles, over its cap of {cap}")
    require(counts[0] > counts[1] > counts[2], f"{sheet['species']}: each level must be lighter than the one before")
    require(1 <= len(collision['caps']) <= pinned.MAX_CAPS, 'a tree has one to eight caps')

    alive = tree.alive()
    number = {a.index: k for k, a in enumerate(alive)}
    skeleton = [dict(order=a.order, parent=number.get(a.parent, -1), at=a.at,
                     points=[(p[0], p[1], p[2], 0. if i == len(a.pts)-1 else a.radii[i]) for i, p in enumerate(a.pts)])
                for a in alive]
    record = dict(
        format=pinned.FORMAT, version=pinned.VERSION, name=name, species=sheet['species'], leaf_style=style,
        kind=sheet['kind'], fuel_class=sheet['fuel_class'], seeds=sheet['seeds'],
        maker=dict(version=MAKER_VERSION, seed=sheet['seed'], sheet_sha256=Sheet.digest(sheet), model=sheet['model']),
        levels=[dict(triangle_count=count, parts=parts) for count, parts in zip(counts, levels)],
        collision=collision, skeleton=skeleton, crown_m=tree.crown.middle(),
        counts=dict(limbs=sum(a.order == 1 for a in alive), boughs=sum(a.order == 2 for a in alive),
                    twigs=sum(a.order == 3 for a in alive), flushes=len(tree.axes[0].pts)-2, clumps=len(clumps),
                    light=len(tree.light), pruned=pruned))
    return pinned.settle(record)


def build_all(folder=Sheet.SHEETS, style=None):
    """{name: record} for every sheet in the folder: each in the default leaf style (`style`, or sheet.py's), or in
    the styles it lists itself."""
    out = {}
    for species in Sheet.species_list(folder):
        sheet = Sheet.load(species, folder)
        for name, own in Sheet.names(sheet, style):
            require(name not in out, f'two plants named {name}')
            out[name] = build(sheet, own, name)
    return out


def report(record):
    c, col = record['counts'], record['collision']
    tris = ', '.join(str(level['triangle_count']) for level in record['levels'])
    size = ' x '.join(f'{v:.3f}' for v in record['size_m'])
    return (f"{record['name']}: {record['leaf_style']}, {size} m, triangles {tris}; "
            f"{c['limbs']} limbs in {c['flushes']} flushes, {c['boughs']} boughs, {c['twigs']} twigs, "
            f"{c['clumps']} clumps from {c['light']} light points, {c['pruned']} stems pruned to fit; "
            f"{len(col['caps'])} caps, lowest rim {col['clearance_m']:.3f} m, pole top {col['pole_top_m']:.3f} m")


def main(argv=None):
    parser = argparse.ArgumentParser(description='Make the pinned plants from their species sheets.')
    parser.add_argument('--check', action='store_true', help='compare with the pinned files; write nothing')
    parser.add_argument('--report', action='store_true', help='print the counts; write nothing')
    parser.add_argument('--style', choices=Sheet.STYLES,
                        help='the leaf style of every sheet that lists none (a change of mind: then set '
                             'DEFAULT_STYLE in sheet.py, so --check agrees)')
    parser.add_argument('--preview', metavar='FOLDER', help='write outlines as PNG to a scratch folder; pin nothing')
    args = parser.parse_args(argv)
    made = build_all(style=args.style)
    stale = []
    for name, record in made.items():
        data = pinned.pack(record)
        path = pinned.PINNED/f'{name}.json'
        print(report(record))
        if args.preview:
            from . import preview
            for file in preview.write(record, Path(args.preview)):
                print(f'  preview {file}')
        elif args.check:
            if not path.is_file() or path.read_bytes() != data:
                stale.append(f'{path.name} differs from its rebuild')
        elif not args.report:
            pinned.PINNED.mkdir(exist_ok=True)
            path.write_bytes(data)
            print(f'  wrote {path.name}: {len(data)} bytes, sha256 {pinned.sha(data)}')
    if args.check:
        stale += [f'{p.name} has no sheet' for p in sorted(pinned.PINNED.glob('*.json')) if p.stem not in made]
        for line in stale:
            print(f'STALE {line}')
        print(f'FLORA_PINNED: {len(made)} plants, {len(stale)} stale')
    return 1 if stale else 0


if __name__ == '__main__':
    sys.exit(main())
