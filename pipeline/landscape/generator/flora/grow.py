"""The skeleton: a trunk by its sheet, then limbs, boughs and twigs, each grown by rule toward light.

The method is the maker's own, built from four ideas in the literature (README.md, "Where the method comes from"):

1. An architectural model sets the rule: where buds sit, in what order they wake and how a side axis is held.
2. The crown is an outline with light in it. Light points are sown in the outline's shell; a bud wakes only if it
   sees some, a growing shoot bends toward what is still free and takes what it reaches (space colonisation, kept
   on a rule's leash). So no two boughs fill the same air and the inside of the crown stays bare.
3. Weight and light bend a limb: it sags as it leaves the trunk and lifts toward its tip.
4. Thickness is the pipe model: every leaf-bearing tip sends a strand of pipe to the root, so where an axis forks
   the areas add (Leonardo's rule), and the trunk's girth is the sum of the whole crown.
"""
import math

from . import det
from .det import TAU, add, clamp, cos, cross, dot, length, lerp, mul, rad, sin, sqrt, sub, unit
from .sheet import MODELS

UP = (0., 1., 0.)
WOOD_TOP = .93      # no wood above this share of the height: the crown's top is leaves
CROWN_SKIN = .95    # wood stays inside this share of the outline
SEE_COS = .34       # a bud or a shoot sees light within 70 degrees of its heading
MIN_SEE = 2         # light points a bud must see to wake
BUD_TRIES = 4       # places round the stem a bud may try before it stays asleep
MIN_STEP_M = .012   # a shoot shorter than this a step never starts


def interp(table, t):
    """Straight lines between the [t, value] pairs of a rising table."""
    if t <= table[0][0]:
        return table[0][1]
    for (t0, v0), (t1, v1) in zip(table, table[1:]):
        if t <= t1:
            return v0+(v1-v0)*(t-t0)/(t1-t0)
    return table[-1][1]


class Crown:
    """The crown's outline: a width for every height, swelling and dipping round the tree, leaning if flagged."""

    def __init__(self, sheet, rng):
        size, crown = sheet['size'], sheet['crown']
        self.y0 = crown['base']*size['height_m']
        self.y1 = size['height_m']
        self.half = size['spread_m']/2
        self.profile = crown['profile']
        self.lobes = []
        for amp in crown['lobes']:
            a = rng.random()*TAU
            self.lobes.append((amp, cos(a), sin(a)))
        self.flag = crown['flag']*self.half
        self.shell = crown['shell']

    def t(self, y):
        return (y-self.y0)/(self.y1-self.y0)

    def centre(self, y):
        """The outline's middle at a height: a flagged crown leans toward +X as it rises."""
        return self.flag*clamp(self.t(y), 0., 1.), 0.

    def radius(self, y, c, s):
        """How far the outline reaches at height y toward the level unit direction (c, s) in x and z."""
        t = self.t(y)
        if t < 0. or t > 1.:
            return 0.
        r = self.half*interp(self.profile, t)
        swell, ck, sk = 1., c, s
        for amp, cp, sp in self.lobes:   # cos(k a - phase), by the angle-sum rule: no arctangent
            swell += amp*(ck*cp+sk*sp)
            ck, sk = ck*c-sk*s, sk*c+ck*s
        return r*swell

    def depth(self, p):
        """0 on the crown's axis, 1 on its outline, more outside it."""
        t = self.t(p[1])
        if t < 0. or t > 1.:
            return 9.
        cx, cz = self.centre(p[1])
        dx, dz = p[0]-cx, p[2]-cz
        d = sqrt(dx*dx+dz*dz)
        if d < 1e-9:
            return 0.
        r = self.radius(p[1], dx/d, dz/d)
        return d/r if r > 1e-9 else 9.

    def middle(self):
        """A point to shade leaves from: on the axis, two fifths of the way up the crown."""
        y = lerp(self.y0, self.y1, .4)
        cx, cz = self.centre(y)
        return (cx, y, cz)


def sow(crown, gap, rng):
    """Light points in the crown's shell, none nearer another than `gap` (darts, thrown until they stop landing)."""
    points, cells = [], {}
    inner, outer = crown.shell
    misses, tries = 0, 0
    floor = math.floor
    while misses < 400 and tries < 40000 and len(points) < 4000:
        tries += 1
        y = lerp(crown.y0, crown.y1, rng.random())
        a = rng.random()*TAU
        c, s = cos(a), sin(a)
        r = crown.radius(y, c, s)*lerp(inner, outer, sqrt(rng.random()))
        cx, cz = crown.centre(y)
        p = (cx+r*c, y, cz+r*s)
        key = (floor(p[0]/gap), floor(p[1]/gap), floor(p[2]/gap))
        ok = True
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for dz in (-1, 0, 1):
                    for q in cells.get((key[0]+dx, key[1]+dy, key[2]+dz), ()):
                        e = sub(p, points[q])
                        if dot(e, e) < gap*gap:
                            ok = False
        if not ok:
            misses += 1
            continue
        misses = 0
        cells.setdefault(key, []).append(len(points))
        points.append(p)
    return points


class Axis:
    """One stem: the trunk (order 0), a limb (1), a bough (2) or a twig (3)."""
    __slots__ = ('index', 'order', 'parent', 'at', 'pts', 'children', 'limb', 'light', 'pipes', 'radii', 'bears')

    def __init__(self, index, order, parent, at, pts, limb):
        self.index, self.order, self.parent, self.at, self.pts, self.limb = index, order, parent, at, pts, limb
        self.children = []   # axis indices, in the order they woke
        self.light = []      # light points its tip holds
        self.pipes = []      # strands of pipe through each point
        self.radii = []
        self.bears = True    # its tip may hold leaves (the stub that closes a forked trunk may not)

    def heading(self, i):
        """The stem's direction as it leaves point i."""
        j = min(i, len(self.pts)-2)
        return unit(sub(self.pts[j+1], self.pts[j]))


class Tree:
    def __init__(self, sheet):
        self.sheet = sheet
        self.rng = det.Rng(sheet['seed'])
        self.crown = Crown(sheet, self.rng.fork('crown'))
        self.light = sow(self.crown, sheet['crown']['light_gap_m'], self.rng.fork('light'))
        self.free = [True]*len(self.light)   # not yet reached by wood
        self.owner = [-1]*len(self.light)    # the axis whose tip holds it, once weighed
        self.axes = []
        self.divergence = rad(sheet['divergence_deg'])
        self.height = sheet['size']['height_m']
        self.wood_top = self.height*WOOD_TOP

    # ----- what a shoot knows of the light -----

    def run(self, start, heading):
        """How far a straight shoot from `start` would go before it left the crown."""
        d = 0.
        while d < 4.:
            d += .02
            p = add(start, mul(heading, d))
            if p[1] > self.wood_top or self.crown.depth(p) > CROWN_SKIN:
                if p[1] < self.crown.y0 and heading[1] > .05:
                    continue   # a limb that starts under the crown's base is on its way up into it
                break
        return d

    def seen(self, pos, heading, sense):
        """(how many, mean direction) of the free light within `sense` and 70 degrees of the heading."""
        count, total = 0, (0., 0., 0.)
        for k, p in enumerate(self.light):
            if not self.free[k]:
                continue
            e = sub(p, pos)
            d = length(e)
            if d > sense or d < 1e-9:
                continue
            e = mul(e, 1./d)
            if dot(e, heading) < SEE_COS:
                continue
            count += 1
            total = add(total, e)
        return count, (unit(total, heading) if count else None)

    def take(self, pos, reach):
        for k, p in enumerate(self.light):
            if self.free[k]:
                e = sub(p, pos)
                if dot(e, e) < reach*reach:
                    self.free[k] = False

    # ----- growth -----

    def trunk(self):
        """The bole, upright, then the leader through as many flushes as the limbs need, then a stub or a tip."""
        sheet, rng = self.sheet, self.rng.fork('trunk')
        t, limbs = sheet['trunk'], sheet['orders'][0]
        bole = t['bole']*self.height
        lead = bole+t['leader']*(self.wood_top-bole)
        self.limb_count = rng.between(*limbs['count'])
        flushes = max(1, -(-self.limb_count//limbs['whorl']))
        if lead-bole < .02*self.height:
            flushes = 1
        pts = [(0., 0., 0.), (0., bole, 0.)]
        x = z = 0.
        for k in range(1, flushes):
            x += t['sweep']*self.height*(2.*rng.random()-1.)
            z += t['sweep']*self.height*(2.*rng.random()-1.)
            pts.append((x, lerp(bole, lead, k/(flushes-1)), z))
        # The trunk ends in a point: a short stub hidden among the limbs of a forked crown, or the leader's own tip.
        whole = t['leader'] >= .9
        stub = max(.03*self.height, 1.5*t['radius_m'])
        last = pts[-1]
        pts.append((last[0], min(self.height*.98, last[1]+(stub*2. if whole else stub)), last[2]))
        axis = Axis(0, 0, -1, 0, pts, -1)
        axis.bears = whole
        self.axes.append(axis)
        return axis

    def extend(self, start, heading, spec, rng):
        """Grow one stem from `start`: `segments` steps, each turned by the kink, the pull of free light, sag and
        lift, and kept inside the crown. Returns its points, or None for a shoot too short to be one."""
        n = spec['segments']
        step = spec['reach']*self.run(start, heading)/n
        if step < MIN_STEP_M:
            return None
        kink = rad(spec['kink_deg'])
        kink = sin(kink)/cos(kink)
        pos, d = start, heading
        u = det.side(d)[0]
        turn = rng.random()*TAU
        pts = [start]
        for i in range(n):
            f = (i+1.)/n
            want = d
            if i:   # a zigzag: each step leans away from the last bud, round the stem by the divergence
                u = det.carry(u, d)
                v = cross(u, d)
                a = turn+i*self.divergence
                want = add(want, mul(add(mul(u, cos(a)), mul(v, sin(a))), kink))
            want = unit(want)
            _, pull = self.seen(pos, d, 2.5*step+.1)
            if pull is not None:
                want = unit(add(mul(want, 1.-spec['light']), mul(pull, spec['light'])))
            level = sqrt(want[0]*want[0]+want[2]*want[2])
            y = (want[1]+spec['lift']*f*f-spec['sag']*(1.-f)*level)*(1.-spec['flat'])
            want = unit((want[0], y, want[2]))
            new = add(pos, mul(want, step))
            new = self.keep_in(new)
            if length(sub(new, pos)) < .35*step:
                break
            d = unit(sub(new, pos))
            pos = new
            pts.append(pos)
            self.take(pos, .75*step)
        return pts if len(pts) > 1 else None

    def keep_in(self, p):
        """A point pulled back inside the crown's skin and under the wood's top."""
        y = min(p[1], self.wood_top)
        p = (p[0], y, p[2])
        if y < self.crown.y0:
            return p
        depth = self.crown.depth(p)
        if depth <= CROWN_SKIN:
            return p
        cx, cz = self.crown.centre(y)
        k = CROWN_SKIN/depth
        return (cx+(p[0]-cx)*k, y, cz+(p[2]-cz)*k)

    def bud(self, parent, order):
        """Set a stem's side buds and grow those that see light. Buds sit at the stem's joints, the farthest out
        served first; the buds of one joint stand evenly round it and each joint is turned from the last by the
        divergence, so flushes interleave."""
        spec = self.sheet['orders'][order-1]
        rng = self.rng.fork(f'bud {parent.index}')
        plane = MODELS[self.sheet['model']]['plane'] and order >= 2
        joints = list(range(len(parent.pts)-2, 0, -1))   # every point but the base and the tip
        if not joints:
            return
        want = self.limb_count if order == 1 else rng.between(*spec['count'])
        want = min(want, len(joints)*spec['whorl'])
        share = [want//len(joints)+(1 if k < want % len(joints) else 0) for k in range(len(joints))]
        base_turn = rng.random()*TAU
        low, high = spec['angle_deg']
        made = 0
        for k, joint in enumerate(joints):
            t = parent.heading(joint)
            u, v = det.side(t)
            # 0 at the stem's first joint, 1 at its last: side stems leave wide low down and steep near the tip.
            along = (joint-1.)/(len(parent.pts)-3.) if len(parent.pts) > 3 else .5
            for j in range(share[k]):
                angle = rad(lerp(high, low, along)+8.*(rng.random()-.5))
                turn = base_turn+k*self.divergence+j*TAU/share[k]+.5*(rng.random()-.5)
                if plane:
                    turn = (0. if (made+j) % 2 else det.PI)+.3*(rng.random()-.5)
                pts = None
                for attempt in range(BUD_TRIES):
                    a = turn+attempt*self.divergence
                    d = unit(add(mul(t, cos(angle)), mul(add(mul(u, cos(a)), mul(v, sin(a))), sin(angle))))
                    count, _ = self.seen(parent.pts[joint], d, .1+.35*self.sheet['size']['spread_m'])
                    if count >= MIN_SEE:
                        pts = self.extend(parent.pts[joint], d, spec, rng)
                        if pts is not None:
                            break
                    if plane:
                        break
                if pts is None:
                    continue
                limb = len(self.axes) if order == 1 else parent.limb
                axis = Axis(len(self.axes), order, parent.index, joint, pts, limb)
                parent.children.append(axis.index)
                self.axes.append(axis)
            made += share[k]

    # ----- weight -----

    def tips(self):
        """The stems whose tips may hold leaves."""
        return [a for a in self.axes if a.bears and a.order >= 0]

    def weigh(self):
        """Give each light point to the nearest leaf-bearing tip in reach, then count pipes and set radii."""
        reach = self.sheet['leaves']['reach_m']
        tips = self.tips()
        for a in self.axes:
            a.light = []
        for k, p in enumerate(self.light):
            best, best_d = None, reach*reach
            for a in tips:
                e = sub(p, a.pts[-1])
                d = dot(e, e)
                if d < best_d:
                    best, best_d = a, d
            self.owner[k] = -1 if best is None else best.index
            if best is not None:
                best.light.append(k)
        for a in reversed(self.axes):   # children were made after their parents
            tip = 1.+len(a.light) if a.bears else 1.
            a.pipes = [tip]*len(a.pts)
            for c in a.children:
                child = self.axes[c]
                for i in range(child.at+1):
                    a.pipes[i] += child.pipes[0]
        trunk = self.axes[0]
        root, r0 = trunk.pipes[0], self.sheet['trunk']['radius_m']
        inverse, twig = 1./self.sheet['pipe_exponent'], self.sheet['twig_radius_m']
        for a in self.axes:
            a.radii = [max(twig, r0*det.power(p/root, inverse)) for p in a.pipes]

    def drop(self, axis):
        """Take a childless stem off the tree."""
        parent = self.axes[axis.parent]
        parent.children = [c for c in parent.children if c != axis.index]
        axis.order = -1

    def alive(self):
        return [a for a in self.axes if a.order >= 0]


def grow(sheet):
    """The whole skeleton, weighed. Deterministic: the sheet's seed and nothing else."""
    tree = Tree(sheet)
    tree.trunk()
    for order in range(1, len(sheet['orders'])+1):
        for parent in [a for a in tree.axes if a.order == order-1]:
            tree.bud(parent, order)
    tree.weigh()
    return tree
