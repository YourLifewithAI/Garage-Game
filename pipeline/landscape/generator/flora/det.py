"""Arithmetic that gives the same bits on every machine and every Python.

A pinned plant is bytes, and the tests rebuild it. Growth is a long chain of small decisions, so one differing last
bit in a sine can move a twig. Everything the maker computes therefore uses only +, -, *, / and sqrt (each correctly
rounded under IEEE 754), in a fixed order:

- no libm sine, cosine, power, logarithm or hypot (their last bits differ between C libraries);
- no built-in sum (compensated since Python 3.12) and no random module (its helpers changed between versions);
- no set and no hash order: lists, and sorts with whole keys.
"""
import math

PI = 3.141592653589793
TAU = 6.283185307179586
HALF_PI = 1.5707963267948966
LN2 = 0.6931471805599453
_MASK = (1 << 64)-1
# 1/3!, 1/5!, ... 1/21!: sin x = x(1 - x2/3! + x4/5! - ...); on |x| <= pi/2 what is left is under 2e-18.
_SIN = tuple(1./math.factorial(n) for n in range(3, 23, 2))


def sin(x):
    x -= TAU*round(x/TAU)
    if x > HALF_PI:
        x = PI-x
    elif x < -HALF_PI:
        x = -PI-x
    x2 = x*x
    total = 0.
    for k in range(len(_SIN)-1, -1, -1):   # Horner, the smallest term first
        total = (_SIN[k] if k % 2 else -_SIN[k])+x2*total
    return x*(1.+x2*total)


def cos(x):
    return sin(x+HALF_PI)


def rad(deg):
    return deg*(PI/180.)


def ln(x):
    """Natural logarithm of a positive number: 2 atanh((m - 1)/(m + 1)) on the mantissa, plus the exponent's share."""
    m, e = math.frexp(x)   # exact: x is m times two to the e, m in [0.5, 1)
    z = (m-1.)/(m+1.)      # in (-1/3, 0]
    z2 = z*z
    total = 0.
    for k in range(41, 0, -2):
        total = 1./k+z2*total
    return 2.*z*total+e*LN2


def exp(x):
    k = round(x/LN2)
    r = x-k*LN2            # |r| <= 0.35
    total = 1.
    for n in range(22, 0, -1):
        total = 1.+r*total/n
    return math.ldexp(total, k)


def power(x, y):
    """x to the y for x >= 0."""
    return 0. if x <= 0. else exp(y*ln(x))


sqrt = math.sqrt   # correctly rounded everywhere


class Rng:
    """SplitMix64: whole-number arithmetic only, so the same stream on every machine and Python."""

    def __init__(self, seed):
        self.state = seed & _MASK

    def u64(self):
        self.state = (self.state+0x9E3779B97F4A7C15) & _MASK
        z = self.state
        z = ((z ^ (z >> 30))*0xBF58476D1CE4E5B9) & _MASK
        z = ((z ^ (z >> 27))*0x94D049BB133111EB) & _MASK
        return z ^ (z >> 31)

    def random(self):
        """In [0, 1): 53 bits."""
        return (self.u64() >> 11)*(1./9007199254740992.)

    def uniform(self, low, high):
        return low+(high-low)*self.random()

    def between(self, low, high):
        """A whole number, low to high inclusive."""
        return low+self.u64() % (high-low+1)

    def fork(self, label):
        """A stream of its own for one part of the work, so a new draw in one part never shifts another."""
        h = 0xCBF29CE484222325   # FNV-1a over the label
        for byte in label.encode('utf-8'):
            h = ((h ^ byte)*0x100000001B3) & _MASK
        return Rng(Rng(self.state ^ h).u64())


# ---------- vectors: plain tuples ----------

def add(a, b):
    return (a[0]+b[0], a[1]+b[1], a[2]+b[2])


def sub(a, b):
    return (a[0]-b[0], a[1]-b[1], a[2]-b[2])


def mul(a, k):
    return (a[0]*k, a[1]*k, a[2]*k)


def dot(a, b):
    return a[0]*b[0]+a[1]*b[1]+a[2]*b[2]


def cross(a, b):
    return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])


def length(a):
    return sqrt(a[0]*a[0]+a[1]*a[1]+a[2]*a[2])


def unit(a, fallback=(0., 1., 0.)):
    n = length(a)
    return (a[0]/n, a[1]/n, a[2]/n) if n > 1e-12 else fallback


def lerp(a, b, t):
    return a+(b-a)*t


def mix(a, b, t):
    return (a[0]+(b[0]-a[0])*t, a[1]+(b[1]-a[1])*t, a[2]+(b[2]-a[2])*t)


def clamp(x, low, high):
    return low if x < low else high if x > high else x


def smooth(t):
    t = clamp(t, 0., 1.)
    return t*t*(3.-2.*t)


def side(t):
    """Two unit vectors square to the unit vector t and to each other: (u, v) with u level where t is not upright."""
    ref = (0., 1., 0.) if abs(t[1]) < .92 else (1., 0., 0.)
    u = unit(cross(ref, t))
    return u, cross(u, t)


def carry(u, t):
    """The side vector u carried on to a new heading t without twisting (parallel transport)."""
    k = dot(u, t)
    moved = (u[0]-t[0]*k, u[1]-t[1]*k, u[2]-t[2]*k)
    if length(moved) < 1e-9:
        return side(t)[0]
    return unit(moved)
