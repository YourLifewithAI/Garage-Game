"""Mapping policy and deterministic room writer; no third-party dependencies."""
import math
import re
from pathlib import Path
from pipeline.landscape.harness.common import canonical, sha, require
from pipeline.landscape.harness.kit import SIZES, prototype
from pipeline.landscape.harness.package import read_package, world_scale
from .materials import CONTRACT_ROLES, PALETTE, hex_color, material
from .copies import build_copies, classify, MAX_SETS
from .fractals import firepits_data, fractals_data, summit_data, tutorial_flag
from .mesh import encode, transform
from .sea import sea_data
from .source import source_data
from .landwork import LandWork
from .terrain import TILE_M as TERRAIN_TILE_M
from .trees import TerrainHeights, climbing_parts, is_tree
from .loose import LOOSE_CAP, LOOSE_KINDS, closed_pieces, estimated_mass, footprint_maximum, log_boxes

CREATED = '2026-10-08T00:00:00Z'
LANDMARKS = {'cottage', 'tower', 'boulder', 'fence', 'crate', 'lantern'}


def triangle_count(parts):
    return sum(len(p['triangles']) for p in parts)


def scatter_policy(name, parts, size):
    """Geometry/size fallback for custom prototypes; never inspect generator keys."""
    roles = {p['role'] for p in parts}
    if name in LANDMARKS or roles & {'roof', 'stone_masonry'}:
        return 'landmark'
    if not roles & {'foliage', 'bark'} and max(size) >= .3:
        return 'landmark'
    return 'merged'


def colliding_parts(name, parts):
    # Small plants stay decorative. Original foliage is still visual only;
    # tree climbing geometry is added separately. Loose kit rocks are low-poly
    # ellipsoids, merged into one static triangle surface rather than 20k nodes.
    roles = {p['role'] for p in parts}
    if roles & {'foliage', 'bark'}:
        return [p for p in parts if p['role'] == 'bark']
    if name in {'grass_tuft', 'flower_clump', 'fern', 'shrub'}:
        return []
    return [p for p in parts if p['role'] not in {'cloth', 'still_water', 'flowing_water'}]


def export_room(package_folder, source_room_folder, room_id, out):
    """Return counts; validate inputs and construct bytes before creating output.

    The output folder's basename must equal room_id, as RoomData.Load requires.
    It must be empty; no replacement of a previously pinned room is permitted.
    """
    require(re.fullmatch(r'[a-z][a-z0-9_-]{0,63}', room_id) is not None, 'invalid room id')
    out = Path(out).absolute()
    require(out.name == room_id, 'output directory name must equal room-id (game loader rule)')
    require(not out.is_symlink(), 'output symlinks forbidden')
    require(not out.exists() or (out.is_dir() and not any(out.iterdir())), 'output folder must be empty')
    # The land's work (landwork.py) starts on the package's files while read_package validates them.
    with LandWork(package_folder) as land:
        return _export(land, package_folder, source_room_folder, room_id, out)


def _export(land, package_folder, source_room_folder, room_id, out):
    # The 10x world: a package of the true-size room magnified x W (x_generator.world_scale) is read against that
    # room magnified, and that magnified room is what is exported (bounds, spawns, the intro's room and boxes).
    doc, meshes, source, _ = read_package(package_folder, source_room_folder)
    intro_source = source_data(source, doc['source'], source_room_folder, world_scale(doc))
    # An island's sea (generator v4 on): the bounds grow out past the reef, and the game gets the reef,
    # the beaches and the jetty. A package without one keeps the room's own bounds.
    sea = doc.get('x_generator', {}).get('sea') if isinstance(doc.get('x_generator'), dict) else None
    # The mechanics round: the island's Fractals and its tutorial mark, from the generator's package extension.
    fractals = fractals_data(doc.get('x_generator'))
    tutorial = tutorial_flag(doc.get('x_generator'))
    summit = summit_data(doc.get('x_generator'))
    # The island's firepits (decision 74), chosen by the generator: the manifest's x_firepits, which the kernel reads.
    firepits = firepits_data(doc.get('x_generator'))
    margin = doc['x_generator'].get('margin_m') if sea is not None else None
    sea, bounds = sea_data(sea, intro_source['bounds'], margin) if sea is not None else (None, intro_source['bounds'])
    require(doc['terrain'], 'landscape has no terrain')
    require(len(doc['objects']) <= 512, 'room contract allows at most 512 populated objects')
    blobs, shell, objects = {}, [], []
    counts = {}

    def put(path, data):
        blobs[path] = data
        return {'path': path, 'sha256': sha(data), 'bytes': len(data)}

    def add_shell(label, parts, role, collides, drawn=True):
        # One part per role gives the existing loader the closest contract role
        # instead of treating every surface of the landscape as one material.
        groups = {}
        for p in parts:
            groups.setdefault(p['role'], []).append(p)
        for harness_role, group in sorted(groups.items()):
            path = f'shell/{label}_{harness_role}.glb'
            put(path, encode(group))
            shell.append({'id': f'shell:{label}_{harness_role}', 'role': role,
                          'geometry': {'kind': 'mesh', 'mesh': path}, 'collides': collides,
                          'material_role': CONTRACT_ROLES[harness_role],
                          'base_color': hex_color(PALETTE[harness_role])})
            if not drawn:
                shell[-1]['drawn'] = False
        counts[label+'_triangles'] = triangle_count(parts)

    terrain = [p for r in doc['terrain'] for p in meshes[r['mesh']]]
    # One index of the terrain answers the main process's height questions: loose items' footprints, supports,
    # spawns and populated trees' perches.
    tree_terrain = TerrainHeights(terrain)
    counts['terrain_triangles'] = triangle_count(terrain)
    tree_parts = []
    counts.update(tree_count=0, tree_pole_triangles=0, tree_cap_triangles=0)

    def add_tree(name, transformed, include_trunk=False):
        if not is_tree(name, transformed):
            return
        climb = climbing_parts(name, transformed, tree_terrain)
        counts['tree_count'] += 1
        counts['tree_pole_triangles'] += triangle_count(climb[:1])
        counts['tree_cap_triangles'] += triangle_count(climb[1:])
        tree_parts.extend(climb)
        if include_trunk:
            tree_parts.extend(p for p in transformed if p['role'] == 'bark')
    # The harness overrides water primitive roles using each water record.
    water = [dict(p, role='still_water' if r['kind'] == 'still' else 'flowing_water')
             for r in doc['water'] for p in meshes[r['mesh']]]
    add_shell('water', water, 'backdrop', False)
    add_shell('scenery', [p for r in doc['scenery'] for p in meshes[r['mesh']]], 'backdrop', False)

    prototypes = {}

    def get_prototype(name):
        if name not in prototypes:
            prototypes[name] = ((prototype(name), SIZES[name]) if name in SIZES else
                (meshes[doc['prototypes'][name]['mesh']], doc['prototypes'][name]['size_m']))
        return prototypes[name]

    def support_at(position):
        # The topmost terrain part over the point, the first part on ties.
        best, k = tree_terrain.hit(position[0], position[2])
        return best, (None if k is None else 'shell:terrain_'+terrain[k]['role'])

    loose_items = []
    loose_skipped = {}

    def add_object(record, index, scatter=False, loose=None):
        name = record['prototype']
        parts, original_size = get_prototype(name)
        size = ([original_size[i]*record['scale'][i] for i in range(3)] if scatter else record['size_m'])
        movable = False if scatter else record['carriable']
        tree = is_tree(name, parts)
        require(not tree or not movable, 'climbable trees must be fixed (static shell collision)')
        mass = 100.0 if scatter else record['mass_kg']
        if loose is not None:
            movable, mass = True, loose['mass_kg']
            size = loose['size_m']
        require(0 < mass <= 5000, 'object mass must fit asset contract (0,5000] kg')
        require(not movable or mass <= 2.0, 'carriable object exceeds companion carry limit (2 kg)')
        require(all(0 < v <= 100 for v in size), 'object size exceeds asset contract')
        roles = sorted({p['role'] for p in parts})
        require(len(roles) <= 16, 'asset allows at most 16 material slots')
        asset_id = f'landscape_{index:04d}'
        instance_id = f'obj:scatter_{index:04d}' if scatter else 'obj:'+record['id']
        if loose is not None:
            instance_id = loose.get('id', instance_id)
        require(re.fullmatch(r'obj:[A-Za-z0-9_-]{1,64}', instance_id) is not None,
                'package object id does not fit room entity id')
        kind = name if scatter else record['kind']
        display = (record['id'] if not scatter else name).replace('_', ' ').replace('-', ' ').capitalize()
        if scatter:
            display += f' {index+1}'
        display = display[:80]
        if loose is not None:
            display = loose['display_name']
        scaled = transform(parts, scale=[size[i]/original_size[i] for i in range(3)],
                           tint=record.get('tint', (1, 1, 1)))
        if loose is not None:
            scaled = transform(loose['parts'], tint=record.get('tint', (1, 1, 1)))
            roles = sorted({p['role'] for p in scaled})
        asset_dir = f'objects/{asset_id}'
        geometry = put(asset_dir+'/mesh.glb', encode(scaled))
        geometry['path'] = 'mesh.glb'
        collision = 'none' if tree or (not colliding_parts(name, parts) and not movable) else 'box'
        # A canopy box would block paths and collide with every leaf. Vegetation
        # never becomes a landmark entity; populated vegetation uses decoration
        # collision unless explicitly carriable.
        asset = {
            'schema': 'enfractal.asset', 'version': 1, 'asset_id': asset_id,
            'display_name': display, 'category': kind[:60],
            'category_group': ('structure' if name in {'cottage', 'tower', 'fence'} or 'roof' in roles
                               else 'lighting' if name == 'lantern' else 'container' if name == 'crate' else 'decor'),
            'tier': 'hero' if movable else 'standard',
            'provenance': {'kind': 'procedural', 'license': 'Inherited from source package; licensing unverified',
                           'created_utc': CREATED, 'notes': 'Landscape package v1 geometry, transformed without texture or shader dependencies.'},
            'pivot': 'bottom_center', 'dimensions_m': list(size),
            'geometry': {'kind': 'mesh', 'mesh': 'mesh.glb', 'triangle_count': triangle_count(scaled)},
            'collision': {'kind': collision}, 'physics': {'movable': movable, 'mass_kg': mass},
            'materials': [{'slot': r, 'role': CONTRACT_ROLES[r],
                           'base_color': hex_color(material(r)['pbrMetallicRoughness']['baseColorFactor'][:3])}
                          for r in roles],
            'affordances': ['walkable_top'] if collision != 'none' else [],
            'review': {'status': 'draft', 'notes': 'Exported for founder review; no gameplay or look gate certified.'},
            'files': [geometry],
        }
        if name == 'crate':
            asset['affordances'].append('container')
        if name == 'lantern':
            asset['affordances'].append('light_source')
        if loose is not None:
            asset['extensions'] = {'x_landscape_visual_bounds': loose['visual_bounds_m']}
            asset['provenance']['notes'] += ' Box dimensions approximate contact; visual bounds are in extensions.'
        put(asset_dir+'/asset.json', canonical(asset))
        if tree:
            add_tree(name, transform(parts, [size[i]/original_size[i] for i in range(3)],
                                     record['position_m'], record['yaw_deg']), include_trunk=True)
        position = loose['position_m'] if loose is not None else record['position_m']
        _, support = support_at(position)
        angle = math.radians(record['yaw_deg'])/2
        objects.append({'id': instance_id, 'asset': asset_dir+'/asset.json', 'display_name': display,
                        'transform': {'position_m': position,
                                      'rotation': [0, math.sin(angle), 0, math.cos(angle)]},
                        'support': {'kind': 'shell', 'target_id': support} if support else {'kind': 'none'}})
        if loose is not None and loose.get('support_id') is not None:
            objects[-1]['support'] = {'kind': 'object', 'target_id': loose['support_id']}
        if movable:
            loose_items.append({'id': instance_id, 'name': display, 'prototype': name, 'mass_kg': mass,
                                'position_m': position, 'yaw_deg': record['yaw_deg'], 'box_m': list(size),
                                'support': objects[-1]['support'],
                                **({'visual_bounds_m': loose['visual_bounds_m'], 'source': loose['source']}
                                   if loose is not None else {'source': 'object:'+record['id']})})

    # Reserve authored entities/carryables first. Never silently revoke an authored
    # carry promise. Whole piles precede crates and stones; no half-movable stack.
    authored_loose = sum(r['carriable'] for r in doc['objects'])
    require(authored_loose <= LOOSE_CAP, 'authored carryables exceed loose thing cap (64)')
    planned, pieces_cache = {}, {}
    reserved_entities, reserved_loose = len(doc['objects']), authored_loose
    candidates = []
    for scatter, records in ((False, doc['objects']), (True, doc['scatter'])):
        for source_index, record in enumerate(records):
            name = record['prototype']
            if not scatter and record['carriable'] and name != 'woodpile':
                continue
            if name not in LOOSE_KINDS:
                continue  # estimated_mass knows no other kind: nothing else can be loose
            parts, original_size = get_prototype(name)
            size = ([original_size[i]*record['scale'][i] for i in range(3)] if scatter else record['size_m'])
            scale = [size[i]/original_size[i] for i in range(3)]
            scaled = transform(parts, scale)
            if name == 'woodpile':
                if name not in pieces_cache:
                    pieces_cache[name] = closed_pieces(parts)
                count, priority = len(pieces_cache[name]), 0
                mass = None
            else:
                mass = estimated_mass(name, scaled, size)
                if mass is None or (not scatter and record['mass_kg'] > .5):
                    continue
                count, priority = 1, 1 if name == 'crate' else 2
            candidates.append((priority, scatter, source_index, record, parts, scale, size, count, mass))
    for _, scatter, source_index, record, parts, scale, size, count, mass in sorted(candidates, key=lambda c: c[:3]):
        name = record['prototype']
        extra_entities = count if scatter else count-1
        extra_loose = count-(not scatter and record['carriable'])
        if reserved_loose+extra_loose > LOOSE_CAP or reserved_entities+extra_entities > 512:
            require(scatter or not record['carriable'], 'authored loose pile exceeds room budget')
            loose_skipped[name] = loose_skipped.get(name, 0)+count
            continue
        source_key = f'{"scatter" if scatter else "object"}:{source_index}'
        if name == 'woodpile':
            specs = log_boxes(parts, scale, record['position_m'], record['yaw_deg'], tree_terrain)
            ids = [f'obj:loose_{"s" if scatter else "o"}_{source_index:04d}_{j:04d}' for j in range(count)]
            for j, spec in enumerate(specs):
                spec.update(id=ids[j], display_name='Log', source=source_key,
                            support_id=ids[spec['support_index']] if spec['support_index'] is not None else None)
        else:
            scaled = transform(parts, scale)
            low = [-size[0]/2, 0, -size[2]/2]
            high = [size[0]/2, size[1], size[2]/2]
            bottom = footprint_maximum(tree_terrain, low, high, record['position_m'], record['yaw_deg'])
            if bottom is None or bottom >= record['position_m'][1]+size[1]-.001:
                loose_skipped[name] = loose_skipped.get(name, 0)+1
                continue
            # Keep the drawn mesh at its original position, including its ground sink.
            shift = max(bottom-record['position_m'][1], 0)
            specs = [dict(parts=transform(scaled, position=[0, -shift, 0]),
                          position_m=[record['position_m'][0], record['position_m'][1]+shift, record['position_m'][2]],
                          size_m=[size[0], size[1]-shift, size[2]],
                          mass_kg=mass if scatter else record['mass_kg'],
                          display_name='Crate' if name == 'crate' else 'Stone', source=source_key,
                          visual_bounds_m={'min_m': [-size[0]/2, -shift, -size[2]/2],
                                           'max_m': [size[0]/2, size[1]-shift, size[2]/2]})]
        planned[scatter, source_index] = specs
        reserved_loose += extra_loose
        reserved_entities += extra_entities

    for index, record in enumerate(doc['objects']):
        if (False, index) in planned:
            for spec in planned[False, index]:
                add_object(record, len(objects), loose=spec)
        else:
            add_object(record, len(objects))
    entity_scatter = 0
    merged_visual, merged_solid = [], []
    pending_loose_entities = sum(len(specs) for (scatter, _), specs in planned.items() if scatter)
    # Plants, trees and stones become plant copies (copies.py), at most 64 sets (by name); anything else, or a
    # 65th kind, stays merged into the scatter shells.
    kinds = {}
    for record in doc['scatter']:
        name = record['prototype']
        if name not in kinds:
            parts, _ = get_prototype(name)
            kinds[name] = classify(name, parts, colliding_parts(name, parts))
    copy_names = set(sorted(name for name, kind in kinds.items() if kind is not None)[:MAX_SETS])
    copy_records = {}
    for source_index, record in enumerate(doc['scatter']):
        name = record['prototype']
        parts, size = get_prototype(name)
        final_size = [size[i]*record['scale'][i] for i in range(3)]
        if (True, source_index) in planned:
            for spec in planned[True, source_index]:
                add_object(record, len(objects), scatter=True, loose=spec)
                pending_loose_entities -= 1
            entity_scatter += 1
            continue
        if scatter_policy(name, parts, final_size) == 'landmark' and len(objects)+pending_loose_entities < 512:
            add_object(record, len(objects), scatter=True)
            entity_scatter += 1
            continue
        if name in copy_names:
            copy_records.setdefault(name, (kinds[name], []))[1].append((source_index, record))
            continue
        solid = colliding_parts(name, parts)
        # Transform before normals: non-uniform scale needs inverse-transpose
        # normals, obtained equivalently from transformed face cross products.
        transformed = transform(parts, record['scale'], record['position_m'], record['yaw_deg'],
                                record.get('tint', (1, 1, 1)))
        add_tree(name, transformed)
        for source_part, p in zip(parts, transformed):
            (merged_solid if any(source_part is q for q in solid) else merged_visual).append(p)
    add_shell('scatter_visual', merged_visual, 'backdrop', False)
    add_shell('scatter_solid', merged_solid, 'ground', True)
    # The land's work: the terrain in 16 m tiles with distance detail (terrain.py), one tiled part per role with
    # the ids and paths the whole-part meshes had, first in the shell; the open-edge diagnostic; the trees' fits.
    work = land.answer(doc, meshes)
    spacings, tiled, terrain_shell = work['spacings'], [], []
    counts['terrain_level_triangles'] = [0]*len(spacings)
    for harness_role, (glb, tile_count, level_triangles) in work['parts'].items():
        path = f'shell/terrain_{harness_role}.glb'
        put(path, glb)
        terrain_shell.append({'id': f'shell:terrain_{harness_role}', 'role': 'ground',
                              'geometry': {'kind': 'mesh', 'mesh': path}, 'collides': True,
                              'material_role': CONTRACT_ROLES[harness_role],
                              'base_color': hex_color(PALETTE[harness_role])})
        tiled.append({'part': f'shell:terrain_{harness_role}', 'tiles': tile_count})
        counts['terrain_level_triangles'] = [a+b for a, b in zip(counts['terrain_level_triangles'], level_triangles)]
    shell[0:0] = terrain_shell
    terrain_tiles = {'tile_m': TERRAIN_TILE_M, 'levels': [{'grid_m': round(g, 6)} for g in spacings], 'parts': tiled}
    counts['terrain_tiles'] = sum(t['tiles'] for t in tiled)
    plant_copies, copy_stats = (build_copies(copy_records, get_prototype, tree_terrain, water, bounds, put,
                                             work['fits']) if copy_records else (None, dict(copies={}, groups=0)))
    counts.update(copy_stats)
    counts['tree_count'] += sum(len(records) for kind, records in copy_records.values() if kind == 'tree')
    # Two merged shell parts regardless of tree count. Lane P hides their GLB
    # scenes after extracting one-sided trimesh collision (see README/report).
    # Collision only: the trees' own meshes stay the drawn ones.
    add_shell('tree_climb', tree_parts, 'ground', True, drawn=False)
    require(len(shell) <= 128, 'output exceeds room shell part limit (128)')
    require(len(blobs) <= 2048, 'output exceeds room file limit (2048)')
    require(len({o['id'] for o in objects}) == len(objects), 'populated id collides with generated scatter entity id')
    require(len(loose_items) <= LOOSE_CAP and len(objects) <= 512, 'loose/entity budget exceeded')

    # Package v1 carriable objects become movable assets. Only cottage/tower
    # prototypes promise a door; other fixed landmarks are not destinations.
    # Include buildings merged after the entity budget, since they remain drawn.
    destinations = [r['position_m'] for r in doc['objects']
                    if r['carriable'] or r['prototype'] in {'cottage', 'tower'}]
    destinations.extend(r['position_m'] for r in doc['scatter']
                        if r['prototype'] in {'cottage', 'tower'})
    spawns = []
    for spawn in source['spawns']:
        spawn = {'id': spawn['id'], 'role': spawn['role'],
                 'position_m': list(spawn['position_m']), 'yaw_deg': spawn['yaw_deg']}
        y, _ = tree_terrain.hit(spawn['position_m'][0], spawn['position_m'][2])
        require(y is not None, 'spawn '+spawn['id']+' has no terrain underneath')
        spawn['position_m'][1] = y
        require(all(source['bounds']['min_m'][i]-1e-6 <= spawn['position_m'][i] <= source['bounds']['max_m'][i]+1e-6
                    for i in range(3)), 'lifted spawn outside source room bounds')
        if destinations:
            x, _, z = spawn['position_m']
            # min keeps package order on ties. Height never affects selection.
            target = min(destinations, key=lambda p: (p[0]-x)**2 + (p[2]-z)**2)
            dx, dz = target[0]-x, target[2]-z
            if dx != 0 or dz != 0:
                # Godot +Y yaw turns -Z towards -X; a coincident target has no heading.
                spawn['yaw_deg'] = math.degrees(math.atan2(-dx, -dz))
        spawns.append(spawn)
    setup = doc['setup']
    edges = work['edges']
    lights = [{'id': 'sun', 'kind': 'sun', 'color': '#fff1d2', 'relative_intensity': 1.0, 'estimated': True}]
    lanterns = [r for r in [*doc['objects'], *doc['scatter']] if r['prototype'] == 'lantern']
    for i, r in enumerate(lanterns[:31]):
        lights.append({'id': f'lantern_{i:04d}', 'kind': 'lamp',
                       'position_m': r['position_m'], 'color': '#ffe7bf',
                       'relative_intensity': .25, 'estimated': True})
    room = {
        'schema': 'enfractal.room', 'version': 1, 'room_id': room_id,
        'display_name': room_id.replace('_', ' ').replace('-', ' ').capitalize()[:80],
        'created_utc': CREATED, 'units': 'm', 'axes': 'y_up_neg_z_forward',
        'source': {'kind': 'procedural', 'pipeline': {'name': 'landscape-room-export', 'version': '1'}},
        'bounds': bounds, 'shell': {'parts': shell, 'openings': []},
        'objects': objects, 'spawns': spawns, 'light_hints': lights,
        'site': {'latitude_deg': setup['latitude_deg'], 'neg_z_bearing_deg': setup['neg_z_bearing_deg'],
                 'solar_noon_h': 12},
        **({'plant_copies': plant_copies} if plant_copies is not None else {}),
        'terrain_tiles': terrain_tiles,
        'files': [{'path': path, 'sha256': sha(data), 'bytes': len(data)} for path, data in sorted(blobs.items())],
        'extensions': {'x_landscape_setup': setup,
                       'x_landscape_source': intro_source,
                       'x_landscape_package_sha256': sha((Path(package_folder)/'package.json').read_bytes()),
                       'x_landscape_terrain_open_edges': edges,
                       'x_landscape_loose': {'cap': LOOSE_CAP, 'count': len(loose_items),
                                             'items': loose_items, 'skipped': loose_skipped},
                       **({'x_landscape_sea': sea} if sea is not None else {}),
                       **({'x_fractals': fractals} if fractals is not None else {}),
                       **({'x_summit': summit} if summit is not None else {}),
                       **({'x_firepits': firepits} if firepits is not None else {}),
                       **({'x_island_tutorial': tutorial} if tutorial is not None else {})},
    }
    blobs['room.json'] = canonical(room)
    # Output creation begins only after package, ids, budgets, and spawns pass.
    out.mkdir(parents=True, exist_ok=True)
    for path, data in sorted(blobs.items()):
        target = out/path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    return dict(counts, room_id=room_id, files=len(blobs), bytes=sum(map(len, blobs.values())),
                shell_parts=len(shell), objects=len(objects), populated_objects=len(doc['objects']),
                scatter_entities=entity_scatter, merged_scatter=len(doc['scatter'])-entity_scatter,
                loose_things=len(loose_items), loose_cap=LOOSE_CAP, loose_skipped=loose_skipped,
                terrain_open_edges=len(edges), lantern_hints=len(lights)-1)
