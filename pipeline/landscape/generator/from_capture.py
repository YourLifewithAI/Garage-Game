"""A captured room as a generator room: the capture's shell and its inventory, ready for `generate`.

    python -B -m pipeline.landscape.generator.from_capture --shell <captured room.json> --inventory <inventory.json>
        --room-id <id> --wall-faces B=south --out <a folder Git ignores>

Writes, beside each other:
  <out>/<id>/room.json      the corpus's C3 shape: the captured shell (walls, floor, ceiling, openings), no objects,
                            two clear spawns and the site, its -Z bearing worked out from the wall the founder named;
  <out>/<id>/inventory.json the capture's C4 inventory, every object as the capture has it (only `room` names <id>);
  <out>/<id>.setup.json     the generator's setup answers: the harness's fixed answers with the room's own site;
then checks the room as the corpus checks its rooms (`problems`). Generate with
`generate --room <out>/<id> --setup <out>/<id>.setup.json --world-scale 10`.

A real room is derived data of a real place: the output must lie outside the repository or where Git ignores it
(a worktree's .cache/, captures/); anything else is refused. Nothing here knows a place: the site carries a compass
bearing and the shell's own coarse latitude and solar noon, or the look's fallback (30 degrees, noon at 12), never a
longitude or a finer latitude (the contract refuses them).
"""
import argparse
import copy
import importlib.util
import json
import math
import subprocess
from pathlib import Path

from pipeline.landscape.harness.common import canonical, load_json
from .land import inside_distance

REPO = Path(__file__).resolve().parents[3]
SETUP_PATH = REPO/'pipeline'/'landscape'/'harness'/'ab-setup.json'
COMPASS = {'north': 0, 'northeast': 45, 'east': 90, 'southeast': 135, 'south': 180, 'southwest': 225, 'west': 270,
           'northwest': 315}
# The look's fallback site (LOOK-BIBLE: 30 degrees north, noon at 12:00), used only where the shell has none: the
# contract needs the numbers, and they say nothing about where the room is.
FALLBACK_LATITUDE_DEG = 30
FALLBACK_SOLAR_NOON_H = 12
CLEAR_M = .3       # a spawn's clearance round every footprint (the corpus's rule)
WALL_CLEAR_M = .5  # and from the walls (the corpus's spawn grid starts half a metre in)
COMPANION_M = 1.   # the companion arrives a metre from the player (the corpus's spawns are a metre apart)
GRID_M = .05       # where a spawn is looked for when the capture's own is not clear


def _validator():
    spec = importlib.util.spec_from_file_location('capture_room_contract_validator', REPO/'contracts'/'validate.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def wall_outward(room, wall_id):
    """The unit (x, z) direction out of the room through a wall: a valid manifest's wall faces into the room
    (contracts/validate.py), so outward is against its polygon's Newell normal."""
    part = next((p for p in room['shell']['parts'] if p['id'] in (wall_id, 'shell:'+wall_id, 'shell:wall_'+wall_id.lower())
                 and p['role'] == 'wall'), None)
    if part is None:
        raise ValueError('no wall %r in the shell' % wall_id)
    points = part['geometry']['points_m']
    nx = nz = 0.
    for a, b in zip(points, points[1:]+points[:1]):
        nx += (a[1]-b[1])*(a[2]+b[2])
        nz += (a[0]-b[0])*(a[1]+b[1])
    length = math.hypot(nx, nz)
    if length == 0:
        raise ValueError('wall %s is not upright' % part['id'])
    return -nx/length, -nz/length


def bearing_in_room(dx, dz):
    """Degrees clockwise from the room's -Z, seen from above (+X is a quarter turn clockwise of -Z)."""
    return math.degrees(math.atan2(dx, -dz)) % 360


def neg_z_bearing(room, wall_id, faces_deg):
    """The compass bearing of the room's -Z when `wall_id` faces `faces_deg` (outward), in whole degrees."""
    return int(round(faces_deg-bearing_in_room(*wall_outward(room, wall_id)))) % 360


def on_the_ground(inventory):
    """Objects whose landform rises from the plain: everything not resting on an object the inventory holds (the
    floor, or a surface the inventory does not hold; the generator raises those from the plain too)."""
    ids = {o['id'] for o in inventory['objects']}
    return [o for o in inventory['objects']
            if not (o['placement']['support']['kind'] == 'object' and o['placement']['support'].get('target_id') in ids)]


def footprint(o):
    """The box's axis-aligned footprint (x0, z0, x1, z1) from its size and yaw (C4's own when it has one)."""
    if 'footprint_m' in o['box']:
        return tuple(o['box']['footprint_m'])
    sx, _, sz = o['box']['size_m']
    cx, _, cz = o['box']['centre_m']
    a = math.radians(o['box']['yaw_deg'])
    hx = abs(math.cos(a))*sx/2+abs(math.sin(a))*sz/2
    hz = abs(math.sin(a))*sx/2+abs(math.cos(a))*sz/2
    return cx-hx, cz-hz, cx+hx, cz+hz


def is_clear(room, inventory, x, z):
    if inside_distance(room, x, z) < WALL_CLEAR_M:
        return False
    for o in on_the_ground(inventory):
        x0, z0, x1, z1 = footprint(o)
        if x0-CLEAR_M <= x <= x1+CLEAR_M and z0-CLEAR_M <= z <= z1+CLEAR_M:
            return False
    return True


def _nearest_clear(room, inventory, x, z, away_from=None):
    lo, hi = room['bounds']['min_m'], room['bounds']['max_m']
    best = None
    i0, i1 = math.ceil(lo[0]/GRID_M), math.floor(hi[0]/GRID_M)
    j0, j1 = math.ceil(lo[2]/GRID_M), math.floor(hi[2]/GRID_M)
    for j in range(j0, j1+1):
        for i in range(i0, i1+1):
            px, pz = round(i*GRID_M, 3), round(j*GRID_M, 3)
            if away_from is not None and math.hypot(px-away_from[0], pz-away_from[1]) < COMPANION_M-1e-9:
                continue
            if is_clear(room, inventory, px, pz):
                d = math.hypot(px-x, pz-z)
                if best is None or d < best[0]-1e-12:
                    best = (d, px, pz)
    if best is None:
        raise ValueError('no clear place for a spawn')
    return best[1], best[2]


def choose_spawns(room, inventory):
    """The capture's spawns where they are clear; otherwise the nearest clear place (the player), or a clear place a
    metre from the player, turning from the capture's own direction (the companion). Returns (spawns, moved ids)."""
    spawns = copy.deepcopy(room['spawns'])
    moved = []
    player = next((s for s in spawns if s['role'] == 'player'), None)
    if player is None:
        lo, hi = room['bounds']['min_m'], room['bounds']['max_m']
        player = {'id': 'player_start', 'role': 'player', 'position_m': [(lo[0]+hi[0])/2, 0., (lo[2]+hi[2])/2],
                  'yaw_deg': 0.}
        spawns.insert(0, player)
        moved.append(player['id'])
    px, _, pz = player['position_m']
    if not is_clear(room, inventory, px, pz):
        px, pz = _nearest_clear(room, inventory, px, pz)
        player['position_m'] = [px, 0., pz]
        if player['id'] not in moved:
            moved.append(player['id'])
    for s in spawns:
        if s is player:
            continue
        x, _, z = s['position_m']
        if is_clear(room, inventory, x, z) and math.hypot(x-px, z-pz) >= COMPANION_M-1e-9:
            continue
        start = math.atan2(z-pz, x-px) if (x, z) != (px, pz) else math.pi
        for k in range(72):
            turn = start+math.radians(5*((k+1)//2)*(1 if k % 2 else -1))
            cx, cz = round(px+COMPANION_M*math.cos(turn), 3), round(pz+COMPANION_M*math.sin(turn), 3)
            if is_clear(room, inventory, cx, cz) and math.hypot(cx-px, cz-pz) >= COMPANION_M-1e-9:
                break
        else:
            cx, cz = _nearest_clear(room, inventory, x, z, away_from=(px, pz))
        s['position_m'] = [cx, 0., cz]
        moved.append(s['id'])
    return spawns, moved


def convert(shell, inventory, room_id, wall, faces_deg, setup=None):
    """(room, inventory, setup, notes) for a generator room built from a captured shell and its inventory."""
    room = copy.deepcopy(shell)
    if room.get('objects'):
        raise ValueError('the shell must hold no objects: the inventory is what becomes land')
    if room.get('files'):
        raise ValueError('the shell must list no files')
    room['room_id'] = room_id
    inv = copy.deepcopy(inventory)
    inv['room'] = room_id
    room['spawns'], moved = choose_spawns(room, inv)
    site = room.get('site', {})
    room['site'] = {'latitude_deg': site.get('latitude_deg', FALLBACK_LATITUDE_DEG),
                    'neg_z_bearing_deg': neg_z_bearing(room, wall, faces_deg),
                    'solar_noon_h': site.get('solar_noon_h', FALLBACK_SOLAR_NOON_H)}
    answers = dict(load_json(SETUP_PATH) if setup is None else setup)
    answers['latitude_deg'] = room['site']['latitude_deg']
    answers['neg_z_bearing_deg'] = room['site']['neg_z_bearing_deg']
    ids = {o['id'] for o in inv['objects']}
    lifted = sorted(o['id'] for o in inv['objects'] if o['placement']['support']['kind'] != 'floor'
                    and o['placement']['support'].get('target_id') not in ids)
    notes = dict(moved_spawns=moved, neg_z_bearing_deg=room['site']['neg_z_bearing_deg'],
                 objects=len(inv['objects']), on_unheld_surfaces=lifted,
                 through_walls=sorted(o['id'] for o in inv['objects'] if pokes_through(room, o)))
    return room, inv, answers, notes


def pokes_through(room, o):
    """Whether a box's corner lies outside the walkable floor (a scan's box through a wall)."""
    sx, _, sz = o['box']['size_m']
    cx, _, cz = o['box']['centre_m']
    a = math.radians(o['box']['yaw_deg'])
    c, s = math.cos(a), math.sin(a)
    for lx, lz in ((-sx/2, -sz/2), (sx/2, -sz/2), (sx/2, sz/2), (-sx/2, sz/2)):
        # The inverse of land.local's turn.
        if inside_distance(room, cx+c*lx+s*lz, cz-s*lx+c*lz) < -1e-9:
            return True
    return False


def problems(folder, setup_path=None):
    """The corpus's own checks on a generator room folder: the contract, the shell only, the inventory's room, ids,
    supports and boxes, the spawns clear of every footprint on the ground, and the site (and setup) agreeing."""
    folder = Path(folder)
    out = list(_validator().check_room(folder))
    room, inv = load_json(folder/'room.json'), load_json(folder/'inventory.json')
    if room['objects'] or room['files']:
        out.append('room.json must be the shell only (no objects, no files)')
    # roomscan writes garage_game.inventory since the rename; captures and corpus rooms from before it say enfractal.inventory.
    if inv.get('schema') not in ('garage_game.inventory', 'enfractal.inventory') or inv.get('room') != room['room_id']:
        out.append('inventory.json must be a garage_game.inventory of room %r' % room['room_id'])
    ids = [o['id'] for o in inv['objects']]
    if len(ids) != len(set(ids)):
        out.append('inventory ids must be unique')
    for o in inv['objects']:
        target = o['placement']['support'].get('target_id')
        if target is not None and target not in ids:
            out.append('%s rests on unknown %s' % (o['id'], target))
        if not all(v > 0 for v in o['box']['size_m']):
            out.append('%s has an empty box' % o['id'])
        q = o['placement'].get('rotation')
        if q is not None and abs(sum(v*v for v in q)-1) > 1e-5:
            out.append('%s rotation is not a unit quaternion' % o['id'])
        if o['placement'].get('yaw_deg', o['box']['yaw_deg']) != o['box']['yaw_deg']:
            out.append('%s placement and box disagree on yaw' % o['id'])
    players = [s for s in room['spawns'] if s['role'] == 'player']
    if len(players) != 1:
        out.append('one player spawn')
    for s in room['spawns']:
        x, _, z = s['position_m']
        if not is_clear(room, inv, x, z):
            out.append('spawn %s is not clear of the walls and every footprint' % s['id'])
        if players and s is not players[0] and math.dist((x, z), (players[0]['position_m'][0], players[0]['position_m'][2])) < COMPANION_M-1e-9:
            out.append('spawn %s is under a metre from the player' % s['id'])
    site = room.get('site')
    if not site or set(site) != {'latitude_deg', 'neg_z_bearing_deg', 'solar_noon_h'}:
        out.append('the room needs a site of its three coarse numbers')
    if setup_path is not None and site:
        setup = load_json(setup_path)
        if (setup['latitude_deg'], setup['neg_z_bearing_deg']) != (site['latitude_deg'], site['neg_z_bearing_deg']):
            out.append('the setup answers and the room disagree on the site')
    return out


def _ignored_by_git(path):
    """Refuse to write a real room where Git would track it."""
    path = Path(path).resolve()
    try:
        path.relative_to(REPO)
    except ValueError:
        return True
    run = subprocess.run(['git', '-C', str(REPO), 'check-ignore', '-q', str(path)], capture_output=True)
    return run.returncode == 0


def write(out, room, inv, setup):
    out = Path(out).resolve()
    folder = out/room['room_id']
    if not _ignored_by_git(folder):
        raise SystemExit('refused: %s is not ignored by Git; write a real room under .cache/ or captures/' % folder)
    folder.mkdir(parents=True, exist_ok=True)
    (folder/'room.json').write_bytes(canonical(room))
    (folder/'inventory.json').write_bytes(canonical(inv))
    setup_path = out/(room['room_id']+'.setup.json')
    setup_path.write_bytes(canonical(setup))
    return folder, setup_path


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--shell', required=True, help="the captured shell's room.json (shell only)")
    parser.add_argument('--inventory', required=True, help="the capture's inventory.json")
    parser.add_argument('--room-id', required=True)
    parser.add_argument('--wall-faces', required=True, help='WALL=DIRECTION, e.g. B=south: which way a wall faces outward')
    parser.add_argument('--out', required=True, help='a folder outside Git (the room and its setup answers go in it)')
    args = parser.parse_args()
    wall, _, direction = args.wall_faces.partition('=')
    direction = direction.strip().lower()
    faces = COMPASS[direction] if direction in COMPASS else int(direction)
    room, inv, setup, notes = convert(load_json(args.shell), load_json(args.inventory), args.room_id, wall.strip(), faces)
    folder, setup_path = write(args.out, room, inv, setup)
    found = problems(folder, setup_path)
    print('ROOM_FROM_CAPTURE %s setup=%s %s' % (folder, setup_path, json.dumps(notes, sort_keys=True)))
    for p in found:
        print('PROBLEM', p)
    print('ROOM_CHECKS %s' % ('ok' if not found else '%d problems' % len(found)))
    raise SystemExit(1 if found else 0)


if __name__ == '__main__':
    main()
