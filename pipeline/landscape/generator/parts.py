"""Parts: each object in its own shape (Run 4, packet WORLD-1).

The founder, 23 November: "the rocky cliffs that clearly look like a chair but are still also obviously landscape.
Desks would be high mesas that they have to scale up to get to." Until this packet every box was melted into one
soft form (land.py's `profile`: a chair was a knoll, a couch a ridge, a bicycle a fin), and a form spread past its box.
Here a kind is a small grammar instead: at most twelve blocks, each standing where the thing's own part stands, all
inside the object's true box. The land a magnified room raises is the blocks' tops (`surface`), so a chair is a seat
with a back wall along one edge, a desk a flat top with sheer sides, a step ladder a giant's stair.

The frame is the object's own (land.py's `local`): x across, z from the front (-z) to the back (+z), y up from its
base; metres. A block is a box, or a drum (`round`: the ellipse in its rectangle), from `y0` (its underside) to `y1`
(its top). The scan gives boxes, not cavities (docs/ROOM-TO-LANDSCAPE.md), so the parts come from the kind and the
box's own proportions alone, never from scan points, and a kind the scan is not sure of is one block: its box.

What the blocks say, and what the land reads of them today:

- **Tops.** The land is a height field: under a top everything is rock. A table is its top on four legs here, and the
  land is the mesa their tops make. The legs and the undersides (`y0`) are kept for the packets that open hollows
  (halls, slots and pits: WORLD-9 to 11); nothing reads them yet.
- **Hard and soft.** A hard block's top is a flat cap bed the weather spares (land.py's `weather`): mesa, butte or
  hoodoo by its width (`cap_kind`). A soft block (cushions, a bean bag, a plant's crown) is capless: its top is
  rounded over a shoulder and fluted by rills that run down it.
- **Hollows.** A `cut` block lowers the land inside it to its `y1`: an open bin's dish, shallow enough that a
  starting bar climbs out (WORLD-9 makes the pit).
- **The grain.** `blocks(o, grain)` gives the blocks as land of that grain stands them: a part thinner than three
  grains is widened to three (a wall two grid cells wide tops out on a knife edge), and a hollow the grain cannot
  hold is left out. `blocks(o)` gives the true parts.

The proof is `likeness`: a shape seen from five sides (above, front, back, left, right), each view a relief (how
much solid lies behind the first surface the eye meets), two shapes compared view by view as the solid they share
over the solid either has. A land matches its blocks when the least of the five is `MATCH` or more; the blocks of an
unlike family, stood in the same box, fail it (tests/test_parts.py, on the stand-in garage's twelve floor boxes).

    python -B -m pipeline.landscape.generator.parts --package <package> --room <room folder>

prints the five scores of every box of a written package (a ten-times one reads its own world scale).
"""
import argparse
import math
from bisect import bisect_left
from collections import namedtuple

from .field import clamp

CONFIDENT = .5     # a kind the scan is this sure of keeps its grammar (land.py's CONFIDENT); below it, one block
MAX_BLOCKS = 12
LEAST_GRAINS = 3   # a part is at least this many grains wide: two full-height vertices across, a top to stand on
ROUND_GRAINS = 4   # ... and a drum this many: a round top that holds four vertices however it lies on the grid
MATCH = .85        # the least of the five views' scores at which a land matches its blocks: a drum in a box scores
#                    0.79 from above against the box (pi over four), and that is another shape
VIEWS = ('top', 'front', 'back', 'left', 'right')
DISH_M = .055      # an open vessel's dish is this deep at most: under what the starting bar climbs (0.6 m at ten times)
RILL_M = .12       # a soft top's rills lie about this far apart (true metres)

# Kind -> family (the grammar that makes its blocks). A kind not listed, or one the scan is not sure of, is a `box`.
FAMILIES = {
    'chair': 'seat', 'office chair': 'seat',
    'desk': 'table', 'table': 'table',
    'couch': 'couch', 'sofa': 'couch',
    'bed': 'bed',
    'bean bag': 'beanbag',
    'shelving unit': 'shelf', 'bookcase': 'shelf',
    'step ladder': 'ladder',
    'easel': 'easel',
    'bicycle': 'bicycle',
    'monitor': 'screen',
    'laptop': 'laptop',
    'bin': 'vessel', 'basket': 'vessel', 'mug': 'vessel', 'cup': 'vessel', 'jar': 'vessel', 'paint can': 'vessel',
    'bottle': 'bottle', 'vase': 'bottle', 'french press': 'bottle', 'kettle': 'bottle',
    'lamp': 'lamp',
    'plant': 'plant',
    'backpack': 'cushion',
    'book': 'box', 'board game': 'box', 'cardboard box': 'box', 'box': 'box', 'storage tote': 'box', 'cabinet': 'box',
    'wardrobe': 'box', 'printer': 'box', 'chest of drawers': 'box', 'suitcase': 'box', 'speaker': 'box', 'cooler': 'box',
    'desktop computer': 'box', 'air conditioner': 'box', 'projector': 'box', 'guitar': 'box',
}
# What a family's land is, to the eye: families of one landform are alike as land (a table, a shelving unit and a box
# are each a flat top with sheer sides until the hollows are opened), families of two are unlike.
LANDFORMS = {
    'box': 'mesa', 'table': 'mesa', 'shelf': 'mesa', 'seat': 'seat', 'couch': 'couch', 'bed': 'bed',
    'beanbag': 'dome', 'cushion': 'dome', 'plant': 'dome', 'ladder': 'stair', 'easel': 'flatiron', 'bicycle': 'fin',
    'screen': 'wall', 'laptop': 'mesa', 'vessel': 'crater', 'bottle': 'stack', 'lamp': 'stack',
}


class Block(namedtuple('Block', 'name x0 x1 z0 z1 y0 y1 round soft cut')):
    """One part: its footprint (x0..x1 across, z0..z1 front to back), underside y0 and top y1 over the object's base."""
    __slots__ = ()

    def distance(self, lx, lz):
        """Signed distance from a point of the object's frame to the block's footprint (negative inside)."""
        hx, hz = (self.x1-self.x0)/2, (self.z1-self.z0)/2
        px, pz = lx-(self.x0+self.x1)/2, lz-(self.z0+self.z1)/2
        if self.round:
            return (math.hypot(px/hx, pz/hz)-1)*min(hx, hz)
        qx, qz = abs(px)-hx, abs(pz)-hz
        return math.hypot(max(qx, 0.), max(qz, 0.))+min(max(qx, qz), 0.)

    def shoulder(self):
        """A soft block's rounding: the radius its top turns over (no more than half its width, or its own height)."""
        return min((self.x1-self.x0)/2, (self.z1-self.z0)/2, self.y1-self.y0)

    def along(self, lx, lz):
        """How far round the block's edge the nearest point of its edge lies (metres): rills are counted along it."""
        hx, hz = (self.x1-self.x0)/2, (self.z1-self.z0)/2
        px, pz = lx-(self.x0+self.x1)/2, lz-(self.z0+self.z1)/2
        if self.round:
            return math.atan2(pz, px)*(hx+hz)/2
        if abs(px)-hx > abs(pz)-hz:
            return pz+(0. if px > 0 else 2*hz+2*hx)
        return px+(hz+hx if pz > 0 else 3*hz+3*hx)


def _b(name, x0, x1, z0, z1, y0, y1, round=False, soft=False, cut=False):
    return Block(name, x0, x1, z0, z1, y0, y1, round, soft, cut)


def family(o):
    """The grammar an object takes: its kind's, when the scan is sure of the kind; else `box`."""
    if o.get('confidence', 0.) >= CONFIDENT:
        return FAMILIES.get(o['kind'], 'box')
    return 'box'


# ------------------------------------------------------------------------------------------- the grammar
# Each takes the box's half width a, half depth b and height H, the world scale W (the box's metres to a true metre)
# and the least width of a part, and gives the true parts; `blocks` fits them to the box and the grain. Sizes a kind
# keeps whatever its box (a seat's height, a board's thickness) are true metres times W, held inside the box.


def _box(a, b, H, W, least):
    return [_b('block', -a, a, -b, b, 0., H)]


def _cushion(a, b, H, W, least):
    return [_b('cushion', -a, a, -b, b, 0., H, soft=True)]


def _beanbag(a, b, H, W, least):
    return [_b('bag', -a, a, -b, b, 0., H, round=True, soft=True)]


def _table(a, b, H, W, least):
    t = min(.04*W, .2*H)                      # the top's thickness
    leg = min(max(.05*W, least), .5*a, .5*b)
    out = [_b('top', -a, a, -b, b, H-t, H)]
    for sx in (-1, 1):
        for sz in (-1, 1):
            x, z = sx*(a-leg/2), sz*(b-leg/2)
            out.append(_b('leg', x-leg/2, x+leg/2, z-leg/2, z+leg/2, 0., H-t))
    return out


def _seat(a, b, H, W, least, office=False):
    ys = clamp((.47 if office else .45)*W, .38*H, .55*H)     # the seat's top
    tb = min(max(.07*W, least), .7*b)                        # the back's thickness
    if not office:
        leg = min(max(.04*W, least), .5*a, .5*b)
        pan = max(ys-.05*W, .5*ys)
        out = [_b('seat', -a, a, -b, b, pan, ys), _b('back', -a, a, b-tb, b, ys, H)]
        for sx in (-1, 1):
            for sz in (-1, 1):
                x, z = sx*(a-leg/2), sz*(b-leg/2)
                out.append(_b('leg', x-leg/2, x+leg/2, z-leg/2, z+leg/2, 0., pan))
        return out
    yb = min(.09*W, .12*H)                                   # the star base: a low round plinth
    pan = max(ys-.08*W, yb)
    sa = .84*a                                               # the seat's half width
    ta = min(max(.05*W, least), .5*sa)                       # an arm's width
    ya = min(ys+.2*W, (ys+H)/2)                              # an arm's top
    post = min(max(.05*W, least), a, b)/2
    return [_b('base', -a, a, -b, b, 0., yb, round=True),
            _b('post', -post, post, -post, post, yb, pan),
            _b('seat', -sa, sa, -.84*b, b-tb, pan, ys),
            _b('arm', -sa, -sa+ta, -.4*b, b-tb, ya-min(.04*W, ya-ys), ya),
            _b('arm', sa-ta, sa, -.4*b, b-tb, ya-min(.04*W, ya-ys), ya),
            _b('back', -.86*sa, .86*sa, b-tb, b, ys, H)]


def _couch(a, b, H, W, least):
    wa = clamp(.2*W, least, .28*a)            # an arm's width
    ya = clamp(.62*W, .6*H, .82*H)            # an arm's top
    tb = clamp(.24*W, least, .64*b)           # the back's thickness
    ys = clamp(.44*W, .45*H, .6*H)            # the cushions' top
    yb = max(ys-.13*W, .5*ys)                 # the frame under them
    inner = 2*(a-wa)
    n = 3 if inner >= 1.5*W else 2 if inner >= .9*W else 1
    gap = .02*W
    out = [_b('arm', -a, -a+wa, -b, b, 0., ya, soft=True), _b('arm', a-wa, a, -b, b, 0., ya, soft=True),
           _b('back', -a+wa, a-wa, b-tb, b, yb, H, soft=True), _b('frame', -a+wa, a-wa, -b, b-tb, 0., yb)]
    width = (inner-(n-1)*gap)/n
    for k in range(n):
        x0 = -a+wa+k*(width+gap)
        out.append(_b('cushion', x0, x0+width, -b, b-tb, yb, ys, soft=True))
    return out


def _bed(a, b, H, W, least):
    board = H >= .75*W                        # a box this tall holds a headboard
    ym = clamp(.55*W, .4*H, .7*H) if board else H
    yf = min(.3*W, .5*ym)
    out = [_b('frame', -a, a, -b, b, 0., yf), _b('mattress', -a, a, -b, b, yf, ym, soft=True)]
    if board:
        tb = min(max(.06*W, least), .3*b)
        out.append(_b('headboard', -a, a, b-tb, b, 0., H))
    return out


def _shelf(a, b, H, W, least):
    board = min(.02*W, .1*H)
    side = min(.02*W, .25*a)
    n = int(clamp(round(H/(.35*W)), 1, MAX_BLOCKS-3))     # boards over the bottom one: a shelf every 35 cm
    out = [_b('side', -a, -a+side, -b, b, 0., H), _b('side', a-side, a, -b, b, 0., H),
           _b('board', -a+side, a-side, -b, b, 0., board)]
    for k in range(1, n+1):
        y = k*H/n
        out.append(_b('top' if k == n else 'board', -a+side, a-side, -b, b, y-board, y))
    return out


def _ladder(a, b, H, W, least):
    """A step ladder standing open: treads up its front to a top a little behind the middle, two rails down its back.
    As land it is a stair, one tread to a rise."""
    zt = .15*b                                               # the top's front edge
    n = int(clamp(round(H/(.25*W)), 2, MAX_BLOCKS-5))        # rises to the top: a tread every 25 cm
    while n > 2 and (zt+b)/n < least:
        n -= 1
    run = (zt+b)/n
    out = []
    for k in range(1, n):
        z0 = -b+(k-.5)*run
        out.append(_b('tread', -a, a, z0, z0+run, max(0., k*H/n-.03*W), k*H/n))
    zc = min(b, zt+max(.12*W, least))                        # the top's back edge
    out.append(_b('top', -a, a, -b+(n-.5)*run, zc, max(0., H-.03*W), H))
    rail = min(max(.05*W, least), a)
    mid = (zc+b)/2
    for x0, x1 in ((-a, -a+rail), (a-rail, a)):
        out.append(_b('rail', x0, x1, zc, mid, 0., .62*H))
        out.append(_b('rail', x0, x1, mid, b, 0., .25*H))
    return out


def _easel(a, b, H, W, least):
    """An easel: a tray across its front legs, the board leaning back above it, a mast to the top, one leg behind."""
    mast = min(max(.05*W, least), a)/2
    return [_b('tray', -a, a, -b, -.55*b, 0., .38*H),
            _b('board', -.8*a, .8*a, -.75*b, -.45*b, .38*H, .62*H),
            _b('board', -.8*a, .8*a, -.45*b, -.15*b, .62*H, .85*H),
            _b('mast', -mast, mast, -.2*b, .15*b, 0., H),
            _b('leg', -mast, mast, .15*b, .5*b, 0., .62*H),
            _b('leg', -mast, mast, .5*b, b, 0., .25*H)]


def _bicycle(a, b, H, W, least):
    """A bicycle, its length along x: two wheels (each a round of three slices), the frame between them, a saddle,
    and the bars across its whole width at the front."""
    d = min(.64*H, .8*a)                      # a wheel's height
    tw = min(max(.05*W, least), 2*b)/2        # half a wheel's thickness
    out = []
    for c in (-a+d/2, a-d/2):
        out += [_b('wheel', c-.5*d, c+.5*d, -tw, tw, 0., .55*d), _b('wheel', c-.4*d, c+.4*d, -tw, tw, .55*d, .8*d),
                _b('wheel', c-.22*d, c+.22*d, -tw, tw, .8*d, d)]
    xr, xf = -a+d/2, a-d/2
    yf = max(d, .78*H)
    xs = xr+.3*(xf-xr)
    xh = xf-.3*d
    bar = min(max(.04*W, least), a)/2
    saddle = min(max(.14*W, least), 2*b)/2
    out += [_b('frame', xr, xh, -tw, tw, .5*d, yf),
            _b('saddle', xs-.12*W, xs+.12*W, -saddle, saddle, yf, max(yf, .9*H)),
            _b('stem', xh-bar, xh+bar, -tw, tw, yf, H),
            _b('bars', xh-bar, xh+bar, -b, b, max(yf, H-.04*W), H)]
    return out


def _screen(a, b, H, W, least):
    foot = min(.03*W, .1*H)
    panel = min(max(.03*W, least), 2*b)/2
    neck = min(max(.05*W, least), a)/2
    return [_b('foot', -.5*a, .5*a, -b, b, 0., foot), _b('neck', -neck, neck, -panel, panel, foot, .3*H),
            _b('panel', -a, a, -panel, panel, .22*H, H)]


def _laptop(a, b, H, W, least):
    if H <= .1*W:
        return [_b('deck', -a, a, -b, b, 0., H)]     # shut
    deck = min(.025*W, .5*H)
    lid = min(max(.01*W, least), b)
    return [_b('deck', -a, a, -b, b, 0., deck), _b('lid', -a, a, b-lid, b, deck, H)]


def _vessel(a, b, H, W, least):
    """An open vessel: a drum with a shallow dish in its top, inside a rim."""
    rim = max(.03*W, 2*least/LEAST_GRAINS)    # the rim stands two grains wide at the least
    inner = min(a, b)-rim
    out = [_b('wall', -a, a, -b, b, 0., H, round=True)]
    if inner > 0 and 2*inner >= least:        # a dish the grain can hold
        out.append(_b('hollow', -a+rim, a-rim, -b+rim, b-rim, 0., H-min(.3*H, DISH_M*W), round=True, cut=True))
    return out


def _bottle(a, b, H, W, least):
    m = min(a, b)                             # a round body: a handle or a spout beyond it is not land
    neck = min(max(.45*m, least/2), m)
    return [_b('body', -m, m, -m, m, 0., .72*H, round=True), _b('neck', -neck, neck, -neck, neck, .72*H, H, round=True)]


def _lamp(a, b, H, W, least):
    m = min(a, b)
    foot = min(.04*W, .1*H)
    pole = min(max(.03*W, least), m)/2
    return [_b('foot', -.6*m, .6*m, -.6*m, .6*m, 0., foot, round=True),
            _b('pole', -pole, pole, -pole, pole, foot, .72*H, round=True),
            _b('shade', -m, m, -m, m, .72*H, H, round=True)]


def _plant(a, b, H, W, least):
    pot = min(.35*H, .3*W)
    return [_b('pot', -.6*a, .6*a, -.6*b, .6*b, 0., pot, round=True),
            _b('crown', -a, a, -b, b, pot, H, round=True, soft=True)]


GRAMMAR = {
    'box': _box, 'cushion': _cushion, 'beanbag': _beanbag, 'table': _table, 'seat': _seat, 'couch': _couch, 'bed': _bed,
    'shelf': _shelf, 'ladder': _ladder, 'easel': _easel, 'bicycle': _bicycle, 'screen': _screen, 'laptop': _laptop,
    'vessel': _vessel, 'bottle': _bottle, 'lamp': _lamp, 'plant': _plant,
}


def _fit(block, a, b, H, least):
    """A block held inside the box, no part of it thinner than `least` (a box thinner than that is as wide as it; a
    drum's least is a third more)."""
    if block.round:
        least = least*ROUND_GRAINS/LEAST_GRAINS
    spans = []
    for lo, hi, half in ((block.x0, block.x1, a), (block.z0, block.z1, b)):
        half = max(half, least/2)
        lo, hi = max(lo, -half), min(hi, half)
        if hi-lo < least:
            mid = clamp((lo+hi)/2, -half+least/2, half-least/2)
            lo, hi = mid-least/2, mid+least/2
        spans.append((lo, hi))
    y1 = clamp(block.y1, 0., H)
    return block._replace(x0=spans[0][0], x1=spans[0][1], z0=spans[1][0], z1=spans[1][1],
                          y0=clamp(block.y0, 0., y1), y1=y1)


def blocks(o, grain=0., W=1., kind=None):
    """An object's blocks, at most twelve, inside its true box. `o` is land.py's object (its size `sx`, `sy`, `sz`,
    its `kind` and `confidence`), in the metres of a room magnified `W` times. `grain` is the cell of the grid the land
    is raised on (0: the true parts): no part is thinner than three grains then. `kind` takes another family's grammar
    for the same box (the proof's unlike families)."""
    name = kind or family(o)
    a, b, H = o['sx']/2, o['sz']/2, o['sy']
    if min(a, b, H) <= 0:
        return ()     # a box with no size raises nothing
    least = LEAST_GRAINS*grain
    made = GRAMMAR[name](a, b, H, W, least, office=o['kind'] == 'office chair') if name == 'seat' \
        else GRAMMAR[name](a, b, H, W, least)
    out = tuple(blk for blk in (_fit(blk, a, b, H, least) for blk in made) if blk.y1 > blk.y0)
    if not 1 <= len(out) <= MAX_BLOCKS:
        raise ValueError('%s: %d blocks' % (name, len(out)))
    return out


def cap_kind(block):
    """What a hard block's flat top is, by its width against its height: a mesa is wider than it is tall, a butte
    narrower, a hoodoo a pillar. None for a soft block or a hollow."""
    if block.soft or block.cut:
        return None
    wide = max(block.x1-block.x0, block.z1-block.z0)
    return 'mesa' if wide >= block.y1 else 'butte' if wide >= .35*block.y1 else 'hoodoo'


# ------------------------------------------------------------------------------------------- the land the blocks make


def surface(parts, lx, lz, ramp, phase=0., W=1.):
    """The land the blocks make at a point of the object's frame: (height over the object's base, whether the
    weather spares it). A block's edge is a face one `ramp` wide (the grid's cell: the face is as steep as the grid
    draws it, and a box turned any way keeps a straight edge). A hard top is flat, a cap bed (1), and every face is
    spared with it (1); a soft top turns over its shoulder, is fluted by rills, and weathers (0). `phase` turns the
    rills (the object's own, so two bean bags differ)."""
    top = hard = 0.
    half = ramp/2
    for blk in parts:
        if blk.cut:
            continue
        d = blk.distance(lx, lz)
        if d >= half:
            continue
        cover = 1. if d <= -half else .5-d/ramp
        y = blk.y1
        if blk.soft:
            r = blk.shoulder()
            if r > 0:
                t = clamp(-d/r)
                y -= r*(1-math.sqrt(1-(1-t)*(1-t)))
                if 0 < t < 1:
                    s = blk.along(lx, lz)/(RILL_M*W)*math.tau
                    flute = (.5+.5*math.cos(s+phase))*(.6+.4*math.cos(.37*s+2.1*phase))
                    y -= .09*r*flute*math.sin(math.pi*t)
        v = y*cover
        if v > top:
            top, hard = v, 0. if blk.soft and cover >= 1 else 1.
    for blk in parts:
        if blk.cut and top > blk.y1:
            d = blk.distance(lx, lz)
            if d < half:
                top -= (top-blk.y1)*(1. if d <= -half else .5-d/ramp)
    return top, hard


def shape(o, grain=0., W=1., kind=None, n=48):
    """The land an object's blocks make, as a function of its frame: `likeness` reads it. Drawn with faces one grain
    wide (a sample of the lattice `n` across where no grain is given)."""
    parts = blocks(o, grain, W, kind)
    ramp = grain or min(o['sx'], o['sz'])/n
    return lambda lx, lz: surface(parts, lx, lz, ramp, o.get('phase', 0.), W)[0]


# ------------------------------------------------------------------------------------------- likeness from five sides


def reliefs(height, a, b, H, n=48, levels=48):
    """A shape in a box, seen from five sides. `height(lx, lz)` is its height over the box's base at a point of the
    box's frame (half width a, half depth b, height H); it is read on a lattice n by n and at `levels` heights. Each
    view is a relief, 0 to 1 a ray: from above, the height; from a side, how much of the box lies behind the first
    surface the ray meets (a ray that passes over the shape: 0). Returns {view: [values]}."""
    hs = [[clamp(height(-a+(i+.5)*2*a/n, -b+(j+.5)*2*b/n)/H) for j in range(n)] for i in range(n)]
    marks = [(k+.5)/levels for k in range(levels)]
    out = dict(top=[v for row in hs for v in row], front=[], back=[], left=[], right=[])
    lines = [(out['front'], out['back'], [hs[i][j] for j in range(n)]) for i in range(n)]
    lines += [(out['left'], out['right'], [hs[i][j] for i in range(n)]) for j in range(n)]
    for near, far, line in lines:
        # The first surface a ray at height y meets from either end: where the running highest first reaches y.
        ahead, behind = list(line), list(reversed(line))
        for run in (ahead, behind):
            for k in range(1, n):
                if run[k] < run[k-1]:
                    run[k] = run[k-1]
        for y in marks:
            near.append((n-bisect_left(ahead, y))/n)
            far.append((n-bisect_left(behind, y))/n)
    return out


def likeness(one, other):
    """Two shapes' reliefs compared: per view, the solid they share over the solid either has (1: the same from that
    side); `least` is the lowest of the five. A land matches its blocks when `least` is MATCH or more."""
    out = {}
    for view in VIEWS:
        a, b = one[view], other[view]
        both = sum(max(x, y) for x, y in zip(a, b))
        out[view] = round(sum(min(x, y) for x, y in zip(a, b))/both, 4) if both > 0 else 1.
    out['least'] = min(out[v] for v in VIEWS)
    return out


def measure(o, land, grain=0., W=1., kind=None, n=48):
    """How like its blocks an object's land is. `land(lx, lz)` is the land's height over the object's base in the
    object's frame; the blocks are the object's own (or `kind`'s, for the same box). Returns `likeness`'s scores."""
    a, b, H = o['sx']/2, o['sz']/2, o['sy']
    return likeness(reliefs(land, a, b, H, n), reliefs(shape(o, grain, W, kind, n), a, b, H, n))


def unlike(o, kind, grain=0., W=1., n=48):
    """Whether `kind`'s blocks, stood in this object's box, are another shape than its own (they fail `likeness`
    against each other). A table and a cardboard box are not unlike as land; a table and a chair are."""
    a, b, H = o['sx']/2, o['sz']/2, o['sy']
    return likeness(reliefs(shape(o, grain, W, None, n), a, b, H, n),
                    reliefs(shape(o, grain, W, kind, n), a, b, H, n))['least'] < MATCH


# ------------------------------------------------------------------------------------------- a written package


def package_rows(package, room_dir, n=48):
    """Every box of a written package against its own blocks: [{id, kind, family, floor, top, front, back, left,
    right, least, match}]. The land is read as the game loads it (checks.Terrain). A box's base is the ground round
    it: the middle height of a ring half a true metre out for a thing on the floor (inside the room's floor, clear of
    every other box), and of a ring on its support's top for a thing that rests on another. A ten-times package is
    read at its own scale."""
    from pathlib import Path
    from pipeline.landscape.harness.common import load_json
    from . import checks, scale as world
    from .land import inside_distance, local, parse_objects
    doc, meshes, room, _ = checks.read_package(package, room_dir)
    W = checks.world_scale(doc)
    objects = parse_objects(world.magnify_inventory(load_json(Path(room_dir)/'inventory.json'), W))
    terrain = checks.Terrain(doc, meshes)
    grain = .03*W     # the landforms are raised on today's 3 cm grid, whatever the world's scale (generate.py)
    by_id = {o['id']: o for o in objects}

    def world_xz(o, lx, lz):
        t = math.radians(o['yaw'])
        c, s = math.cos(t), math.sin(t)
        return o['cx']+c*lx+s*lz, o['cz']-s*lx+c*lz

    def within(o, x, z, grow):
        lx, lz = local(o, x, z)
        return abs(lx) <= o['sx']/2+grow and abs(lz) <= o['sz']/2+grow

    def ground(o):
        """The middle height of the land on a ring round an object's box, or None where the ring finds none."""
        parent = by_id.get(o['parent'])
        out = .5*W if parent is None else 2*grain
        a, b = o['sx']/2+out, o['sz']/2+out
        ring = []
        for k in range(64):
            t = k/16.
            side, f = int(t), 2*(t-int(t))-1
            lx, lz = ((f*a, -b), (a, f*b), (-f*a, b), (-a, -f*b))[side]
            x, z = world_xz(o, lx, lz)
            if parent is None:
                if inside_distance(room, x, z) < 0:
                    continue
            elif not within(parent, x, z, -2*grain):
                continue
            if any(within(p, x, z, 2*grain) for p in objects if p is not o and p is not parent):
                continue
            y = terrain.height(x, z)
            if y == y:
                ring.append(y)
        ring.sort()
        return ring[len(ring)//2] if ring else None

    rows, tops = [], {}
    for o in sorted(objects, key=lambda o: (o['parent'] is not None, o['id'])):
        base = ground(o)
        if base is None:
            # Nothing to stand a ring on: its box's own base, on its support's top where it rests on one.
            base = o['base'] if o['parent'] is None else tops[o['parent']]
        tops[o['id']] = base+o['sy']

        def land(lx, lz, o=o, base=base):
            y = terrain.height(*world_xz(o, lx, lz))
            return (y-base) if y == y else 0.
        got = measure(o, land, grain, W, n=n)
        rows.append(dict(id=o['id'], kind=o['kind'], family=family(o), floor=o['parent'] is None,
                         match=got['least'] >= MATCH, **got))
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--package', required=True)
    parser.add_argument('--room', required=True)
    args = parser.parse_args()
    rows = package_rows(args.package, args.room)
    for r in rows:
        print('LIKENESS %-18s %-14s %-8s %s top %.2f front %.2f back %.2f left %.2f right %.2f least %.2f %s' % (
            r['id'], r['kind'], r['family'], 'floor' if r['floor'] else 'rests', r['top'], r['front'], r['back'],
            r['left'], r['right'], r['least'], 'matches' if r['match'] else 'UNLIKE'))
    floor = [r for r in rows if r['floor']]
    print('LIKENESS_FLOOR %d of %d floor boxes match their blocks from five sides (%.2f or more on each)' % (
        sum(1 for r in floor if r['match']), len(floor), MATCH))
    raise SystemExit(0 if all(r['match'] for r in floor) else 1)


if __name__ == '__main__':
    main()
