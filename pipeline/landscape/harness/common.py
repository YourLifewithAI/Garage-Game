"""Versioned vocabulary and canonical bytes. No Blender dependency."""
import hashlib
import json
import math
from pathlib import Path

FORMAT = 'garage_game.landscape'
FORMATS = (FORMAT, 'enfractal.landscape')  # a package written before the rename reads too (contracts/schema_ids.py)
VERSION = 1
MAX_BYTES = 50_000_000
MAX_TRIANGLES = 2_000_000
MAX_EXPANDED_TRIANGLES = 8_000_000
MAX_INSTANCES = 20_000
KIT_VERSION = 1
MATERIALS_VERSION = 2
FRAME = 'y_up_neg_z_forward'
SETUP = dict(gameplay_mode='sandbox', water='some', latitude_deg=30,
             neg_z_bearing_deg=0, season='summer', day_of_year=172, solar_time_h=9)
# Linear RGB palette; tint is a multiplier, with 1 meaning unchanged.
PALETTE = {
    'meadow': (.19, .40, .10), 'soil': (.30, .19, .12),
    'worn_path': (.52, .40, .26), 'gravel': (.57, .49, .34),
    'scree': (.34, .36, .35), 'rock': (.34, .39, .43),
    'cliff': (.39, .32, .26), 'moss': (.23, .35, .16),
    'snow': (.80, .86, .91), 'still_water': (.12, .33, .43),
    'flowing_water': (.21, .43, .53), 'foliage': (.16, .37, .10),
    'bark': (.26, .16, .10), 'timber': (.43, .28, .16),
    'stone_masonry': (.48, .45, .38), 'roof': (.36, .18, .13),
    'wood': (.51, .33, .18), 'stone': (.46, .49, .47),
    'cloth': (.49, .28, .36), 'metal': (.43, .48, .52),
}
ROLES = frozenset(PALETTE)


def canonical(value):
    return (json.dumps(value, sort_keys=True, indent=2, ensure_ascii=False,
                       allow_nan=False) + '\n').encode('utf-8')


def sha(data):
    return hashlib.sha256(data).hexdigest()


def harness_revision(root=None):
    """Hash source bytes, relative names and explicit library versions."""
    root = Path(root) if root else Path(__file__).resolve().parent
    files = {p.relative_to(root).as_posix(): sha(p.read_bytes())
             for p in sorted(root.rglob('*.py'))}
    files['ab-setup.json'] = sha((root/'ab-setup.json').read_bytes())
    record = dict(files=files, kit_version=KIT_VERSION, materials_version=MATERIALS_VERSION)
    return dict(record, sha256=sha(canonical(record)))


def load_json(path):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError('duplicate JSON key: ' + key)
            result[key] = value
        return result
    result = json.loads(Path(path).read_bytes(), object_pairs_hook=pairs)
    finite(result)
    return result


def finite(value):
    if isinstance(value, float) and not math.isfinite(value):
        raise ValueError('non-finite number')
    if isinstance(value, dict):
        for item in value.values():
            finite(item)
    elif isinstance(value, list):
        for item in value:
            finite(item)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def fields(value, required, optional=()):
    require(isinstance(value, dict), 'record must be an object')
    require(set(required) <= value.keys(), 'missing fields: ' + str(set(required)-value.keys()))
    require(all(k in set(required) | set(optional) or k.startswith('x_') for k in value),
            'unknown field')


def number(value, low=-1e6, high=1e6):
    require(type(value) in (int, float) and math.isfinite(value) and low <= value <= high,
            'invalid number')


def vector(value, count=3, low=-1e6, high=1e6):
    require(isinstance(value, list) and len(value) == count, 'invalid vector')
    for v in value:
        number(v, low, high)


def token(value):
    require(isinstance(value, str) and 0 < len(value) <= 80 and
            all(c.isascii() and (c.isalnum() or c in '_-') for c in value), 'invalid id')
