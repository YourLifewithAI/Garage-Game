"""Species sheets: what a kind of plant is, in a botanist's terms. README.md lists every field.

A sheet may name a `base` sheet and state only what differs (a veteran oak is an oak with a wider bole). Unknown
fields fail, so a typing slip never passes for a default.
"""
import copy
from pathlib import Path

from pipeline.landscape.harness.common import canonical, load_json, require, sha

FORMAT = 'garage_game.flora_sheet'
VERSION = 1
SHEETS = Path(__file__).resolve().parent/'sheets'
STYLES = ('clumps', 'cards')
# The one leaf style every sheet takes unless it lists its own (RUN-4.md D7: clumps, and a change of mind is a rerun).
DEFAULT_STYLE = 'clumps'

# Branching models, after Halle, Oldeman and Tomlinson's tree architecture. A model sets how side axes behave
# where the sheet does not say: `flat` (how level a side axis is held, 1 a flat tier) and `plane` (side axes of the
# second order and on leave left and right in one level sheet, not in a spiral).
MODELS = {
    'rauh': dict(flat=0., plane=False),      # trunk in flushes, limbs like small trunks: oak, pine, ash, maple
    'massart': dict(flat=.85, plane=True),   # trunk in flushes, limbs in flat tiers: spruce, fir
}

_NUM, _INT = 'number', 'whole'


def _range(low, high, kind=_NUM):
    return (kind, low, high)


def _pair(low, high, kind=_NUM):
    return ('pair', kind, low, high)


_ORDER = {
    'count': _pair(0, 24, _INT), 'whorl': _range(1, 8, _INT), 'angle_deg': _pair(5, 130),
    'reach': _range(.05, 1.2), 'segments': _range(1, 8, _INT), 'kink_deg': _range(0, 60),
    'light': _range(0, 1), 'sag': _range(0, 2), 'lift': _range(0, 2), 'flat': _range(0, 1),
    'sides': _range(3, 10, _INT),
}
_SPEC = {
    'format': ('const', FORMAT), 'version': ('const', VERSION),
    'species': ('token',), 'kind': ('one', ('tree',)), 'fuel_class': ('one', ('broadleaf', 'conifer')),
    'seeds': ('flag',), 'seed': _range(0, 2**32-1, _INT), 'model': ('one', tuple(MODELS)),
    'note': ('text',), 'styles': ('styles',),
    'size': {'height_m': _range(.1, 5), 'spread_m': _range(.05, 5)},
    'trunk': {'bole': _range(.03, .9), 'radius_m': _range(.002, .5), 'flare': _range(1, 4),
              'flare_height': _range(.05, 1), 'roots': _range(0, 8, _INT), 'root_depth': _range(0, .6),
              'sweep': _range(0, .2), 'leader': _range(0, 1), 'sides': _range(3, 12, _INT)},
    'crown': {'base': _range(0, .9), 'profile': ('profile',), 'lobes': ('lobes',), 'shell': _pair(0, 1),
              'light_gap_m': _range(.01, 1), 'flag': _range(0, 1)},
    'divergence_deg': _range(0, 360), 'pipe_exponent': _range(1.5, 3.5), 'twig_radius_m': _range(.0005, .05),
    'orders': ('orders',),
    'leaves': {'count': _range(2, 200, _INT), 'size_m': _pair(.005, 2), 'flat': _range(.2, 1.5),
               'reach_m': _range(.01, 2), 'cover': _range(.3, 2), 'jitter': _range(0, .4),
               'under': _range(.2, 1), 'shade': _range(0, 1), 'cards': _range(2, 8, _INT)},
    'levels': ('levels',),
    'climb': {'caps': _range(1, 8, _INT), 'perch': _range(.2, .8)},
}
_OPTIONAL = {'note', 'styles'}
_ORDER_OPTIONAL = {'flat', 'whorl'}


def _number(value, kind, low, high, where):
    ok = (type(value) is int) if kind == _INT else (type(value) in (int, float))
    require(ok and low <= value <= high, f'{where}: {value!r} is not a {kind} from {low} to {high}')


def _check(value, rule, where):
    if isinstance(rule, dict):
        require(isinstance(value, dict), f'{where}: an object')
        extra = sorted(set(value)-set(rule))
        require(not extra, f'{where}: unknown field {extra}')
        for key, sub in rule.items():
            if key not in value:
                require(key in _OPTIONAL or (where.startswith('orders') and key in _ORDER_OPTIONAL),
                        f'{where}: missing {key}')
                continue
            _check(value[key], sub, f'{where}.{key}' if where else key)
        return
    kind = rule[0]
    if kind in (_NUM, _INT):
        _number(value, kind, rule[1], rule[2], where)
    elif kind == 'pair':
        require(isinstance(value, list) and len(value) == 2, f'{where}: two numbers')
        for v in value:
            _number(v, rule[1], rule[2], rule[3], where)
    elif kind == 'const':
        require(value == rule[1], f'{where}: must be {rule[1]!r}')
    elif kind == 'one':
        require(value in rule[1], f'{where}: one of {list(rule[1])}')
    elif kind == 'flag':
        require(type(value) is bool, f'{where}: true or false')
    elif kind == 'text':
        require(isinstance(value, str) and len(value) <= 400, f'{where}: a short text')
    elif kind == 'token':
        require(isinstance(value, str) and 0 < len(value) <= 24 and value[0].isalpha() and value.islower() and
                all(c.isascii() and (c.isalnum() or c == '_') for c in value), f'{where}: a lowercase name')
    elif kind == 'styles':
        require(isinstance(value, list) and value and len(set(value)) == len(value) and
                all(v in STYLES for v in value), f'{where}: some of {list(STYLES)}')
    elif kind == 'profile':
        require(isinstance(value, list) and 2 <= len(value) <= 12, f'{where}: 2 to 12 [height, radius] pairs')
        for pair in value:
            require(isinstance(pair, list) and len(pair) == 2, f'{where}: [height, radius] pairs')
            _number(pair[0], _NUM, 0, 1, where)
            _number(pair[1], _NUM, 0, 1.5, where)
        heights = [p[0] for p in value]
        require(heights[0] == 0 and heights[-1] == 1 and all(a < b for a, b in zip(heights, heights[1:])),
                f'{where}: heights rise from 0 to 1')
        require(max(p[1] for p in value) > 0, f'{where}: a crown has some width')
    elif kind == 'lobes':
        require(isinstance(value, list) and len(value) <= 4, f'{where}: at most four numbers')
        for v in value:
            _number(v, _NUM, 0, .4, where)
        require(sum(value) <= .8, f'{where}: lobes that deep would turn the crown inside out')   # a check, not geometry
    elif kind == 'orders':
        require(isinstance(value, list) and 1 <= len(value) <= 3, f'{where}: one to three orders (limb, bough, twig)')
        for k, order in enumerate(value):
            _check(order, _ORDER, f'orders[{k}]')
            require(order['count'][0] <= order['count'][1], f'orders[{k}].count: low then high')
    elif kind == 'levels':
        require(isinstance(value, list) and len(value) == 3, f'{where}: three triangle caps')
        for v in value:
            _number(v, _INT, 12, 100000, where)
        require(value[0] > value[1] > value[2], f'{where}: each level lighter than the one before')
    else:
        raise AssertionError(kind)


def _merge(base, over):
    """`over` laid on `base`: objects merge, `orders` merges order by order, everything else replaces."""
    out = copy.deepcopy(base)
    for key, value in over.items():
        if isinstance(value, dict) and isinstance(out.get(key), dict):
            out[key] = _merge(out[key], value)
        elif key == 'orders' and isinstance(value, list) and isinstance(out.get(key), list):
            merged = [_merge(out[key][k], order) if k < len(out[key]) and isinstance(order, dict) else order
                      for k, order in enumerate(value)]
            out[key] = merged+out[key][len(value):]   # orders the sheet leaves unsaid stay the base's
        else:
            out[key] = copy.deepcopy(value)
    return out


def resolve(raw, folder=SHEETS, seen=()):
    """A sheet as written, laid over its base, checked, with its model's defaults filled in."""
    require(isinstance(raw, dict), 'a sheet is an object')
    if 'base' in raw:
        name = raw['base']
        require(isinstance(name, str) and name not in seen and len(seen) < 4, 'base: a sheet, and no circle of them')
        base = resolve(load_json(Path(folder)/f'{name}.json'), folder, (*seen, name))
        base.pop('styles', None)   # a base's leaf styles are its own
        raw = _merge(base, {k: v for k, v in raw.items() if k != 'base'})
    sheet = copy.deepcopy(raw)
    _check(sheet, _SPEC, '')
    model = MODELS[sheet['model']]
    for order in sheet['orders']:
        order.setdefault('flat', model['flat'])
        order.setdefault('whorl', 1)
    shell = sheet['crown']['shell']
    require(shell[0] < shell[1], 'crown.shell: inner then outer')
    require(sheet['leaves']['size_m'][0] <= sheet['leaves']['size_m'][1], 'leaves.size_m: small then large')
    require(sheet['crown']['base'] < 1, 'crown.base: below the top')
    return sheet


def load(species, folder=SHEETS):
    sheet = resolve(load_json(Path(folder)/f'{species}.json'), folder)
    require(sheet['species'] == species, f'{species}.json names another species')
    return sheet


def digest(sheet):
    """The resolved sheet's hash: of its canonical JSON, never of the file's bytes, so line endings cannot move it."""
    return sha(canonical(sheet))


def names(sheet, default=None):
    """[(pinned name, leaf style)] a sheet makes: its species in the default style, or species_style for each
    style it lists itself (the oak, both ways, until the founder picks)."""
    if 'styles' not in sheet:
        return [(sheet['species'], default or DEFAULT_STYLE)]
    return [(f"{sheet['species']}_{style}", style) for style in sheet['styles']]


def species_list(folder=SHEETS):
    return sorted(p.stem for p in Path(folder).glob('*.json'))
