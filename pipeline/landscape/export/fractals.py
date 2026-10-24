"""The island's Fractals for the game: a bounded whitelist of the generator's x_generator.fractals.

The mechanics round (docs/game/MECHANICS.md section 6, packet M4) puts Fractals on the island: small things the player
picks up and spends at a camp. The generator (pipeline/landscape/generator/fractals.py) lists them in its package
extension with everything it measured (its routes, benches, reach report); the game's manifest takes only what it
needs, in `extensions.x_fractals` (contracts/room-manifest.schema.json): whether the island's Fractals are typed, and
each one's id, where it rests (bottom centre, room metres), its kind when typed and the lowest stamina band from which
the generator's exact walk reaches it. Nothing descriptive is copied, and nothing unbounded.

`x_island_tutorial` is the generator's `x_generator.tutorial` flag: true for an island the opening menu lists first
(none is locked); absent otherwise.

`x_summit` is the generator's `x_generator.summit` (the second contract round, 15 October): the top of the island's one
summit, `center_m` on its ground, `radius_m` covering that top, the landmark's id and the band its highest ledge needs
(the contract's `x_summit`; `contracts/validate.py` checks the centre lies inside the island). Four fields, nothing else.

`x_firepits` is the generator's `x_generator.firepits` (decisions 62 and 74; the generator's real firepits, Lane C): each
firepit's spot, bottom centre on its ground in room metres, in the generator's order (nearest the start first), which is the
order the kernel numbers them in (firepit:1, firepit:2, ...). At most MAX_FIREPITS; numbers finite and bounded, no two at
one spot. Nothing else travels: not why the generator chose a spot, its reach band, its landmark, the counts or the notes.
"""
import re

from pipeline.landscape.harness.common import require
from .source import number, records, vector

MAX_FRACTALS = 1000   # the manifest schema's ceiling
BOUND_M = 1000
KINDS = ('heart', 'band')
FRACTAL_ID = re.compile(r'fractal:[a-z0-9]{1,32}')
TOKEN = re.compile(r'[a-z][a-z0-9_-]{0,63}')    # the contract's token
SUMMIT_RADIUS_MAX_M = 50                        # the contract's bound
MAX_FIREPITS = 16     # the manifest schema's ceiling (the kernel's CampLaws.MaxFirepits)


def fractals_data(gen):
    """The manifest's `x_fractals` record from the package's x_generator, or None for a package without Fractals."""
    fx = gen.get('fractals') if isinstance(gen, dict) else None
    if fx is None:
        return None
    require(isinstance(fx, dict), 'invalid fractals record')
    typed = fx.get('typed', False)
    require(type(typed) is bool, 'invalid fractals flag')
    items, seen = [], set()
    for item in records(fx['items'], MAX_FRACTALS):
        ident = item['id']
        require(isinstance(ident, str) and FRACTAL_ID.fullmatch(ident) is not None, 'invalid fractal id')
        require(ident not in seen, 'duplicate fractal id')
        seen.add(ident)
        row = {'id': ident, 'position_m': vector(item['position_m'], 3, -BOUND_M, BOUND_M)}
        if typed:
            require(item.get('kind') in KINDS, 'a typed fractal names its kind')
            row['kind'] = item['kind']
        if 'reach_band' in item:
            require(type(item['reach_band']) is int, 'invalid fractal reach band')
            row['reach_band'] = number(item['reach_band'], 1, 20)
        items.append(row)
    return {'typed': typed, 'items': sorted(items, key=lambda r: r['id'])}


def summit_data(gen):
    """The manifest's `x_summit` record from the package's x_generator, or None for a package without a summit."""
    summit = gen.get('summit') if isinstance(gen, dict) else None
    if summit is None:
        return None
    require(isinstance(summit, dict), 'invalid summit record')
    row = {'center_m': vector(summit['center_m'], 3, -BOUND_M, BOUND_M)}
    radius = number(summit['radius_m'], 0, SUMMIT_RADIUS_MAX_M)
    require(radius > 0, 'a summit has a width')
    row['radius_m'] = radius
    if 'landmark' in summit:
        require(isinstance(summit['landmark'], str) and TOKEN.fullmatch(summit['landmark']) is not None, 'invalid summit landmark')
        row['landmark'] = summit['landmark']
    if 'top_band' in summit:
        require(type(summit['top_band']) is int, 'invalid summit band')
        row['top_band'] = number(summit['top_band'], 1, 20)
    return dict(sorted(row.items()))


def firepits_data(gen):
    """The manifest's `x_firepits` record from the package's x_generator, or None for a package without firepits."""
    fp = gen.get('firepits') if isinstance(gen, dict) else None
    if fp is None:
        return None
    require(isinstance(fp, dict), 'invalid firepits record')
    items = [{'position_m': vector(item['position_m'], 3, -BOUND_M, BOUND_M)} for item in records(fp['items'], MAX_FIREPITS)]
    require(len({tuple(i['position_m']) for i in items}) == len(items), 'two firepits at one spot')
    return {'items': items}


def tutorial_flag(gen):
    """True when the package marks the island as a tutorial (the menu lists it first), else None."""
    return True if isinstance(gen, dict) and gen.get('tutorial') is True else None
