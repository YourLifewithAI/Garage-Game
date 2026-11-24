"""Outlines for the maker's eye: a pinned plant drawn flat-shaded, with no engine and no library.

    python -B -m pipeline.landscape.generator.flora.make --preview .cache/scratch/flora

One PNG a plant: its three levels side by side, seen from the front, from a quarter above and from under the
crown (where a small body stands). It certifies nothing about the look in the game (no painterly shader, and cards
are drawn as plain sheets); it is for whoever writes a species sheet to see what the numbers made. Write it to a
scratch folder, never into the repository.
"""
import math
from pathlib import Path

from pipeline.landscape.harness import png
from pipeline.landscape.harness.common import PALETTE

CELL = 340
MARGIN = 14
SKY = (.78, .86, .94)
VIEWS = ((0., 0.), (35., 24.), (20., -40.))   # (turned about the upright, tipped toward the eye), in degrees
LIGHT = (.38, .8, .46)


def _turn(p, yaw, pitch):
    c, s = math.cos(yaw), math.sin(yaw)
    x, y, z = c*p[0]+s*p[2], p[1], c*p[2]-s*p[0]
    c, s = math.cos(pitch), math.sin(pitch)
    return x, c*y-s*z, c*z+s*y


def _normal(a, b, c):
    u, v = [b[i]-a[i] for i in range(3)], [c[i]-a[i] for i in range(3)]
    n = (u[1]*v[2]-u[2]*v[1], u[2]*v[0]-u[0]*v[2], u[0]*v[1]-u[1]*v[0])
    size = math.sqrt(n[0]*n[0]+n[1]*n[1]+n[2]*n[2])
    return None if size < 1e-12 else (n[0]/size, n[1]/size, n[2]/size)


def _draw(pixels, depth, width, ox, oy, parts, yaw, pitch, scale, mid):
    """One level in one cell: flat triangles, the nearest kept (the eye looks along -z)."""
    for part in parts:
        base = PALETTE[part['role']]
        sheets = 'uvs' in part   # cards are seen from both sides
        seen = [_turn(p, yaw, pitch) for p in part['positions']]
        for f in part['triangles']:
            n = _normal(*(part['positions'][i] for i in f))
            if n is None:
                continue
            lit = n[0]*LIGHT[0]+n[1]*LIGHT[1]+n[2]*LIGHT[2]
            lit = abs(lit) if sheets else max(0., lit)
            tint = [(part['tints'][f[0]][k]+part['tints'][f[1]][k]+part['tints'][f[2]][k])/3. for k in range(3)]
            colour = bytes(min(255, int(255*(base[k]*tint[k]*(.5+.6*lit))**(1/2.2))) for k in range(3))
            a, b, c = (seen[i] for i in f)
            xs = [ox+CELL/2+(p[0]-mid[0])*scale for p in (a, b, c)]
            ys = [oy+CELL/2-(p[1]-mid[1])*scale for p in (a, b, c)]
            area = (xs[1]-xs[0])*(ys[2]-ys[0])-(xs[2]-xs[0])*(ys[1]-ys[0])
            if abs(area) < 1e-9:
                continue
            x0, x1 = max(ox, int(min(xs))), min(ox+CELL-1, int(max(xs))+1)
            y0, y1 = max(oy, int(min(ys))), min(oy+CELL-1, int(max(ys))+1)
            for py in range(y0, y1+1):
                for px in range(x0, x1+1):
                    w0 = ((xs[1]-px)*(ys[2]-py)-(xs[2]-px)*(ys[1]-py))/area
                    w1 = ((xs[2]-px)*(ys[0]-py)-(xs[0]-px)*(ys[2]-py))/area
                    w2 = 1.-w0-w1
                    if w0 < 0 or w1 < 0 or w2 < 0:
                        continue
                    z = w0*a[2]+w1*b[2]+w2*c[2]
                    at = py*width+px
                    if z > depth[at]:
                        depth[at] = z
                        pixels[at*3:at*3+3] = colour


def sheet(record):
    """(width, height, RGB bytes): the three views down, the three levels across, each view at one scale."""
    width, height = CELL*3, CELL*len(VIEWS)
    pixels = bytearray(bytes(int(255*v**(1/2.2)) for v in SKY)*(width*height))
    depth = [-1e9]*(width*height)
    for row, (yaw, pitch) in enumerate(VIEWS):
        yaw, pitch = math.radians(yaw), math.radians(pitch)
        every = [_turn(p, yaw, pitch) for level in record['levels'] for part in level['parts'] for p in part['positions']]
        low = [min(p[i] for p in every) for i in range(2)]
        high = [max(p[i] for p in every) for i in range(2)]
        scale = (CELL-2*MARGIN)/max(high[0]-low[0], high[1]-low[1])
        mid = ((low[0]+high[0])/2, (low[1]+high[1])/2)
        for col, level in enumerate(record['levels']):
            _draw(pixels, depth, width, col*CELL, row*CELL, level['parts'], yaw, pitch, scale, mid)
    return width, height, bytes(pixels)


def write(record, folder):
    folder = Path(folder)
    folder.mkdir(parents=True, exist_ok=True)
    width, height, pixels = sheet(record)
    path = folder/f"{record['name']}.png"
    path.write_bytes(png.encode(width, height, pixels))
    return [path]
