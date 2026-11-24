"""The pinned plant: its frame, its bytes and its checks. README.md, "The pinned plant".

Every coordinate is a whole number of twentieths of a millimetre: the maker's numbers are rounded to 0.1 mm, then
the plant is set on its pivot (bottom centre of the finest level), which may shift it by half of that. So the
bounds are centred exactly, in a file and in a GLB's 32-bit floats alike (harness/package.py asks for 1e-5).
"""
import hashlib
import json
import math
from pathlib import Path

from pipeline.landscape.harness.common import load_json, require

FORMAT = 'garage_game.flora_mesh'
VERSION = 1
PINNED = Path(__file__).resolve().parent/'pinned'
MAX_CAPS = 8            # contracts/room-manifest.schema.json, plant_set.cap_slots
ROLES = ('bark', 'foliage')
WIDTH = 118             # the longest line pack() writes, where it can choose
_GRID = 10000           # rounding: 0.1 mm
_FIELDS = ('collision', 'counts', 'crown_m', 'format', 'fuel_class', 'kind', 'leaf_style', 'levels', 'maker', 'name',
           'seeds', 'size_m', 'skeleton', 'species', 'version')
_COLLISION = ('axis_m', 'caps', 'clearance_m', 'pole_radius_m', 'pole_top_m', 'trunk_radius_m', 'trunk_top_m')


def sha(data):
    return hashlib.sha256(data).hexdigest()


def _round(value, places=4):
    return round(value, places)+0.   # + 0.: never a minus zero


def settle(record):
    """The maker's record in its pinned frame: rounded, set on its pivot, its size and clearance worked out."""
    def grid(v):
        return round(v*_GRID)
    finest = [p for part in record['levels'][0]['parts'] for p in part['positions']]
    low = [min(grid(p[i]) for p in finest) for i in range(3)]
    high = [max(grid(p[i]) for p in finest) for i in range(3)]
    shift = (low[0]+high[0], 2*low[1], low[2]+high[2])   # in half steps

    def place(p):
        return [(2*grid(p[i])-shift[i])/(2.*_GRID)+0. for i in range(3)]

    def place_x(x):
        return (2*grid(x)-shift[0])/(2.*_GRID)+0.

    def place_y(y):
        return (2*grid(y)-shift[1])/(2.*_GRID)+0.

    def place_z(z):
        return (2*grid(z)-shift[2])/(2.*_GRID)+0.
    out = {k: v for k, v in record.items() if k not in ('levels', 'collision', 'skeleton', 'crown_m')}
    out['size_m'] = [(high[i]-low[i])/float(_GRID) for i in range(3)]
    out['crown_m'] = place(record['crown_m'])
    out['levels'] = []
    for level in record['levels']:
        parts = []
        for part in level['parts']:
            made = dict(role=part['role'], positions=[place(p) for p in part['positions']],
                        triangles=[list(f) for f in part['triangles']],
                        tints=[[_round(v, 3) for v in t] for t in part['tints']])
            if 'twigs_m' in part:
                made.update(clump=list(part['clump']), twigs_m=[place(p) for p in part['twigs_m']])
            if 'uvs' in part:
                made['uvs'] = [[_round(v) for v in uv] for uv in part['uvs']]
            parts.append(made)
        out['levels'].append(dict(triangle_count=level['triangle_count'], parts=parts))
    c = record['collision']
    caps = [[place_x(x), place_z(z), _round(rx), _round(rz), place_y(rim), place_y(top), _round(tx), _round(tz)]
            for x, z, rx, rz, rim, top, tx, tz in c['caps']]
    out['collision'] = dict(
        axis_m=[place_x(c['axis'][0]), place_z(c['axis'][1])], trunk_radius_m=_round(c['trunk_radius']),
        pole_radius_m=_round(c['pole_radius']), trunk_top_m=place_y(c['trunk_top']), pole_top_m=place_y(c['pole_top']),
        clearance_m=min(cap[4] for cap in caps), caps=caps)
    out['skeleton'] = [dict(order=a['order'], parent=a['parent'], at=a['at'],
                            points=[place(p)+[_round(p[3])] for p in a['points']]) for a in record['skeleton']]
    check(out)
    return out


# ---------- bytes ----------

def _flat(value):
    """One line of JSON, keys sorted."""
    if isinstance(value, dict):
        return '{'+', '.join(f'{json.dumps(k)}: {_flat(value[k])}' for k in sorted(value))+'}'
    if isinstance(value, (list, tuple)):
        return '['+', '.join(_flat(v) for v in value)+']'
    if isinstance(value, float):
        # Every number a pinned plant holds has at most five decimals (settle), so plain digits say it exactly.
        require(math.isfinite(value) and round(value, 5) == value, f'not a number of five decimals: {value!r}')
        text = f'{value+0.:.5f}'.rstrip('0')
        return text+'0' if text.endswith('.') else text
    return json.dumps(value)


def _is_row(value):
    return not isinstance(value, (dict, list, tuple)) or (
        isinstance(value, (list, tuple)) and all(not isinstance(v, (dict, list, tuple)) for v in value))


def pack(record):
    """The record's bytes: JSON, keys sorted, LF, UTF-8. Objects open over lines; a list of numbers or of rows of
    numbers is packed as many to a line as fit in WIDTH. The same record always gives the same bytes."""
    lines = []

    def emit(value, indent, lead, tail):
        pad = ' '*indent
        whole = _flat(value) if indent else None   # the record itself always opens over lines
        if whole is not None and (not isinstance(value, (dict, list, tuple)) or
                                  len(pad)+len(lead)+len(whole)+len(tail) <= WIDTH):
            lines.append(pad+lead+whole+tail)
        elif isinstance(value, dict):
            lines.append(pad+lead+'{')
            keys = sorted(value)
            for k, key in enumerate(keys):
                emit(value[key], indent+1, json.dumps(key)+': ', ',' if k < len(keys)-1 else '')
            lines.append(pad+'}'+tail)
        elif all(_is_row(v) for v in value):
            lines.append(pad+lead+'[')
            line = ''
            for k, item in enumerate(value):
                text = _flat(item)+(',' if k < len(value)-1 else '')
                if line and len(pad)+1+len(line)+1+len(text) > WIDTH:
                    lines.append(pad+' '+line)
                    line = ''
                line = text if not line else line+' '+text
            lines.append(pad+' '+line)
            lines.append(pad+']'+tail)
        else:
            lines.append(pad+lead+'[')
            for k, item in enumerate(value):
                emit(item, indent+1, '', ',' if k < len(value)-1 else '')
            lines.append(pad+']'+tail)
    emit(record, 0, '', '')
    return ('\n'.join(lines)+'\n').encode('utf-8')


def names(folder=PINNED):
    return sorted(p.stem for p in Path(folder).glob('*.json'))


def load(name, folder=PINNED):
    """A pinned plant, checked. Raises ValueError for one that breaks its rules."""
    record = load_json(Path(folder)/f'{name}.json')
    check(record)
    require(record['name'] == name, f'{name}.json names another plant')
    return record


# ---------- checks ----------

def _number(value, low=-100., high=100.):
    require(type(value) in (int, float) and math.isfinite(value) and low <= value <= high,
            f'a number out of range: {value!r}')


def _point(value, size=3):
    require(isinstance(value, list) and len(value) == size, 'a point needs its numbers')
    for v in value:
        _number(v)


def check(record):
    """The rules a pinned plant keeps. They are what the exporter and the game lean on: three levels, each lighter,
    each a bark and a leaf part with every index in range and no flat triangle; the pivot at the bottom centre and
    the size the bounds; and the climbing collision as contracts/validate.py asks a tree row to be, with round caps."""
    require(isinstance(record, dict) and tuple(sorted(record)) == _FIELDS, 'a pinned plant has exactly its fields')
    require(record['format'] == FORMAT and record['version'] == VERSION, 'not a pinned plant of this version')
    require(record['leaf_style'] in ('clumps', 'cards'), 'leaf style')
    require(record['kind'] == 'tree' and record['fuel_class'] in ('broadleaf', 'conifer'), 'kind and fuel class')
    require(type(record['seeds']) is bool, 'seeds')
    levels = record['levels']
    require(isinstance(levels, list) and len(levels) == 3, 'three levels')
    counts = []
    for k, level in enumerate(levels):
        parts = level['parts']
        require([p['role'] for p in parts] == list(ROLES), f'level {k}: a bark part, then a leaf part')
        total = 0
        for part in parts:
            n = len(part['positions'])
            require(n > 0 and len(part['tints']) == n, f'level {k}: a tint for every corner')
            for p in part['positions']:
                _point(p)
            for t in part['tints']:
                require(len(t) == 3 and all(type(v) in (int, float) and 0 <= v <= 1 for v in t), 'a tint is 0 to 1')
            for f in part['triangles']:
                require(len(f) == 3 and all(type(i) is int and 0 <= i < n for i in f) and len(set(f)) == 3,
                        f'level {k}: a triangle names three corners of its part')
                a, b, c = (part['positions'][i] for i in f)
                u, v = [b[i]-a[i] for i in range(3)], [c[i]-a[i] for i in range(3)]
                cr = (u[1]*v[2]-u[2]*v[1], u[2]*v[0]-u[0]*v[2], u[0]*v[1]-u[1]*v[0])
                require(cr[0]*cr[0]+cr[1]*cr[1]+cr[2]*cr[2] > 1e-18, f'level {k}: a flat triangle')
            total += len(part['triangles'])
            if part['role'] == 'foliage':
                require(len(part['clump']) == n and part['twigs_m'], f'level {k}: every leaf corner names its clump')
                require(all(type(i) is int and 0 <= i < len(part['twigs_m']) for i in part['clump']), 'clump index')
                for p in part['twigs_m']:
                    _point(p)
                if record['leaf_style'] == 'cards':
                    require(len(part.get('uvs', ())) == n, f'level {k}: a card corner has its place in the picture')
                    for uv in part['uvs']:
                        require(len(uv) == 2 and all(0 <= v <= 1 for v in uv), 'a picture place is 0 to 1')
                else:
                    require('uvs' not in part, 'clumps carry no picture')
        require(level['triangle_count'] == total and total > 0, f'level {k}: its triangle count')
        counts.append(total)
    require(counts[0] > counts[1] > counts[2], 'each level is lighter than the one before')
    finest = [p for part in levels[0]['parts'] for p in part['positions']]
    low = [min(p[i] for p in finest) for i in range(3)]
    high = [max(p[i] for p in finest) for i in range(3)]
    require(low[1] == 0 and low[0]+high[0] == 0 and low[2]+high[2] == 0, 'the pivot is the bottom centre')
    require(all(abs(high[i]-low[i]-record['size_m'][i]) < 1e-9 for i in range(3)), 'size_m is the bounds')
    for level in levels[1:]:
        for part in level['parts']:
            for p in part['positions']:
                require(p[1] >= -1e-9, 'a lighter level dips under the pivot')
    _point(record['crown_m'])

    c = record['collision']
    require(isinstance(c, dict) and tuple(sorted(c)) == _COLLISION, 'the collision has exactly its fields')
    _point(c['axis_m'], 2)
    caps = c['caps']
    require(isinstance(caps, list) and 1 <= len(caps) <= MAX_CAPS, 'a tree has one to eight caps')
    require(c['trunk_radius_m'] > 0 and c['pole_radius_m'] > 0, 'a trunk and a pole have a radius')
    require(c['trunk_radius_m'] >= c['pole_radius_m'], 'a trunk is no thinner at its foot than at its top')
    require(0 < c['trunk_top_m'] < c['pole_top_m'] <= high[1], 'the trunk tops out under the pole, inside the plant')
    for cap in caps:
        _point(cap, 8)
        x, z, rim_x, rim_z, rim, top, top_x, top_z = cap
        require(rim_x == rim_z and top_x == top_z, 'a cap is round, so a copy may be turned any way')
        require(0 < top_x <= rim_x, 'a perch is no wider than its rim')
        require(0 < rim < top <= high[1], 'a rim lies between the ground and its perch, inside the plant')
        require(abs(x) <= high[0] and abs(z) <= high[2], 'a cap lies over the plant')
    require(c['pole_top_m'] <= max(cap[5] for cap in caps), 'the pole ends under a cap: a climber comes up through it')
    over = caps[0]
    require(math.hypot(over[0]-c['axis_m'][0], over[1]-c['axis_m'][1]) < over[6],
            "the first cap's perch stands over the pole")
    require(c['pole_top_m'] <= over[5], "the pole ends under the first cap's perch")
    require(c['clearance_m'] == min(cap[4] for cap in caps), 'clearance_m is the lowest rim')

    skeleton = record['skeleton']
    require(isinstance(skeleton, list) and skeleton and skeleton[0]['order'] == 0 and skeleton[0]['parent'] == -1,
            'the skeleton starts at the trunk')
    for k, axis in enumerate(skeleton):
        require(len(axis['points']) >= 2, 'a stem has a base and a tip')
        for p in axis['points']:
            _point(p, 4)
        if k:
            parent = axis['parent']
            require(type(parent) is int and 0 <= parent < k, 'a stem grows from one made before it')
            require(axis['order'] == skeleton[parent]['order']+1, "a stem is one order past its parent's")
            require(0 < axis['at'] < len(skeleton[parent]['points'])-1, 'a stem leaves its parent at a joint')
    return record
