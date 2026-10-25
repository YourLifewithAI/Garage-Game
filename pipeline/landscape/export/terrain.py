"""Terrain tiles (the 10x world): the land cut into 16 m tiles with distance detail.

contracts/README.md, "Terrain tiles": each terrain part's GLB holds one root node per tile and level, named
tile_<tx>_<tz>_lod<level>, its positions in room metres inside its tile's square, every tile at every level.

- Level 0 is the package's own triangles, clipped exactly to the tile squares (a triangle that crosses a tile line
  is cut there; the cut points are interpolated from the edge's endpoints in a fixed order, so both sides share
  them bit for bit). The walk checks hold on it, and a colliding part collides through it only. Its normals and
  colours are the part's own, as the whole-part meshes had them, so tile lines do not show.
- Each coarser level clusters the level before's vertices, all roles of a tile together, on a grid of the level's
  spacing: an inside cluster becomes the mean of its points; a point on the tile's edge stays on it, clustered
  along that edge to one of its own points, which the neighbouring tile picks too, so tiles of one level meet
  without cracks. A role whose triangles all collapse in a tile keeps the level before's (the contract asks for
  every level of every tile; the other roles' clusters already cover its sliver). Coarse levels hang a skirt of
  the level's spacing below every tile edge, hiding the seam where a finer neighbour meets them. Level 0 has no
  skirt: it is the ground bodies walk on.

GLBs: one material (the role's, as the whole-part meshes had it), one mesh per node, float positions and normals,
COLOR_0 as normalized bytes, 16-bit indices where a node has fewer than 65,536 vertices.
"""
import math
import struct
from array import array

from pipeline.landscape.harness.common import canonical
from .ids import GLB_GENERATOR
from .materials import PALETTE, material

TILE_M = 16
_F32 = struct.Struct('<3f')
LEVEL_STEPS = (4, 16)  # coarser levels: the base grid times these (10x: 0.1, 0.4 and 1.6 m)
DEFAULT_GRID_M = .1


class Mesh:
    """One role's triangles in one tile at one level: parallel vertex lists and index triples."""
    __slots__ = ('pos', 'nrm', 'col', 'tris')

    def __init__(self):
        self.pos, self.nrm, self.col, self.tris = [], [], [], []


def levels_for(doc):
    """The level spacings, finest first: the package's grid (x_generator.cell_m), then 4 and 16 times it."""
    gen = doc.get('x_generator') if isinstance(doc.get('x_generator'), dict) else {}
    cell = gen.get('cell_m')
    if type(cell) not in (int, float) or not 0 < cell <= 1:
        cell = DEFAULT_GRID_M
    return [float(cell)] + [cell*step for step in LEVEL_STEPS]


def vertex_normals(positions, triangles):
    """Area-weighted face normals summed at shared indices (mesh.normals, without per-vertex lists)."""
    n = len(positions)
    sx, sy, sz = [0.]*n, [0.]*n, [0.]*n
    for a, b, c in triangles:
        pa, pb, pc = positions[a], positions[b], positions[c]
        ux, uy, uz = pb[0]-pa[0], pb[1]-pa[1], pb[2]-pa[2]
        vx, vy, vz = pc[0]-pa[0], pc[1]-pa[1], pc[2]-pa[2]
        cx, cy, cz = uy*vz-uz*vy, uz*vx-ux*vz, ux*vy-uy*vx
        sx[a] += cx; sy[a] += cy; sz[a] += cz
        sx[b] += cx; sy[b] += cy; sz[b] += cz
        sx[c] += cx; sy[c] += cy; sz[c] += cz
    result = []
    for x, y, z in zip(sx, sy, sz):
        length = math.sqrt(x*x+y*y+z*z)
        result.append((x/length, y/length, z/length) if length > 1e-20 else (0., 1., 0.))
    return result


def vertex_colours(part, base):
    """COLOR_0 as mesh.encode bakes it: the role's library colour blended and tinted, over the material's base."""
    role = part['role']
    first = PALETTE[role]
    second = PALETTE[part.get('blend_role', role)]
    weights = part.get('blend_weights') if 'blend_role' in part else None
    tints = part.get('tints')
    result = []
    for j in range(len(part['positions'])):
        w = weights[j] if weights is not None else 0
        t = tints[j] if tints is not None else (1, 1, 1, 1)
        result.append(tuple(min(1, max(0, ((1-w)*first[i]+w*second[i])*t[i]/base[i])) for i in range(3)))
    return result


def _clip(polygon, axis, value, keep_above):
    """Clip a polygon of (x, y, z, nx, ny, nz, r, g, b) points to one side of an axis plane. A cut point is
    interpolated from the edge's lower endpoint (by position), so two polygons sharing the edge get the same point,
    and its coordinate on the plane is the plane's exactly."""
    result = []
    count = len(polygon)
    for k in range(count):
        a, b = polygon[k], polygon[(k+1) % count]
        inside_a = a[axis] >= value if keep_above else a[axis] <= value
        inside_b = b[axis] >= value if keep_above else b[axis] <= value
        if inside_a:
            result.append(a)
        if inside_a != inside_b:
            p, q = (a, b) if a[:3] <= b[:3] else (b, a)
            t = (value-p[axis])/(q[axis]-p[axis])
            point = [p[i]+t*(q[i]-p[i]) for i in range(9)]
            point[axis] = value
            result.append(tuple(point))
    return result


def _area2(a, b, c):
    ux, uy, uz = b[0]-a[0], b[1]-a[1], b[2]-a[2]
    vx, vy, vz = c[0]-a[0], c[1]-a[1], c[2]-a[2]
    x, y, z = uy*vz-uz*vy, uz*vx-ux*vz, ux*vy-uy*vx
    return x*x+y*y+z*z


def _unit(n):
    length = math.sqrt(n[0]*n[0]+n[1]*n[1]+n[2]*n[2])
    return (n[0]/length, n[1]/length, n[2]/length) if length > 1e-20 else (0., 1., 0.)


def tile_level0(terrain):
    """Cut the package's terrain parts into tiles. Returns (materials {role: glTF material}, tiles {(tx, tz):
    {role: Mesh}}), roles and tiles in sorted order."""
    roles = {}
    for part in terrain:
        roles.setdefault(part['role'], []).append(part)
    materials = {role: material(role, sorted({p['blend_role'] for p in parts if 'blend_role' in p}))
                 for role, parts in roles.items()}
    tiles = {}
    floor = math.floor
    for k, part in enumerate(terrain):
        role = part['role']
        base = materials[role]['pbrMetallicRoughness']['baseColorFactor']
        positions = [tuple(v) for v in part['positions']]
        normals = vertex_normals(positions, part['triangles'])
        colours = vertex_colours(part, base)
        tx = [floor(v[0]/TILE_M) for v in positions]
        tz = [floor(v[2]/TILE_M) for v in positions]
        buckets, crossing = {}, []
        for face in part['triangles']:
            a, b, c = face
            key = (tx[a], tz[a])
            if key == (tx[b], tz[b]) == (tx[c], tz[c]):
                bucket = buckets.get(key)
                if bucket is None:
                    bucket = buckets[key] = []
                bucket.append(face)
            else:
                crossing.append(face)
        for key, faces in buckets.items():
            mesh = tiles.setdefault(key, {}).get(role)
            if mesh is None:
                mesh = tiles[key][role] = Mesh()
            used = sorted({i for face in faces for i in face})
            offset = len(mesh.pos)
            remap = {old: offset+new for new, old in enumerate(used)}
            mesh.pos.extend(positions[i] for i in used)
            mesh.nrm.extend(normals[i] for i in used)
            mesh.col.extend(colours[i] for i in used)
            mesh.tris.extend((remap[a], remap[b], remap[c]) for a, b, c in faces)
        cut = {}  # (tile, point) -> index: cut points of this part shared by its pieces in one tile
        for face in crossing:
            polygon0 = [positions[i]+normals[i]+colours[i] for i in face]
            for x in range(min(tx[i] for i in face), max(tx[i] for i in face)+1):
                for z in range(min(tz[i] for i in face), max(tz[i] for i in face)+1):
                    polygon = polygon0
                    for axis, value, above in ((0, x*TILE_M, True), (0, (x+1)*TILE_M, False),
                                               (2, z*TILE_M, True), (2, (z+1)*TILE_M, False)):
                        polygon = _clip(polygon, axis, float(value), above)
                        if len(polygon) < 3:
                            break
                    if len(polygon) < 3:
                        continue
                    pieces = [(polygon[0], polygon[i], polygon[i+1]) for i in range(1, len(polygon)-1)]
                    pieces = [p for p in pieces if _area2(*p) > 1e-18]
                    if not pieces:
                        continue
                    mesh = tiles.setdefault((x, z), {}).get(role)
                    if mesh is None:
                        mesh = tiles[(x, z)][role] = Mesh()
                    for piece in pieces:
                        # Cut points are one vertex where the GLB stores them as one (float32): near a tile's
                        # corner, where a fine triangle's edge passes through it, two cut points can differ only
                        # below float32, and a piece left with two equal corners covers nothing and is dropped.
                        stored = [_F32.unpack(_F32.pack(*point[:3])) for point in piece]
                        if stored[0] == stored[1] or stored[1] == stored[2] or stored[0] == stored[2]:
                            continue
                        face_out = []
                        for point, position in zip(piece, stored):
                            key = ((x, z), position)
                            index = cut.get(key)
                            if index is None:
                                index = cut[key] = len(mesh.pos)
                                mesh.pos.append(point[:3])
                                mesh.nrm.append(_unit(point[3:6]))
                                mesh.col.append(point[6:9])
                            face_out.append(index)
                        mesh.tris.append(tuple(face_out))
    ordered = {key: {role: tiles[key][role] for role in sorted(tiles[key])} for key in sorted(tiles)}
    return {role: materials[role] for role in sorted(materials)}, ordered


def coarsen(tile, meshes, spacing):
    """One tile's next level (README of this module). meshes: {role: Mesh} at the level before.
    Returns ({role: Mesh} to cascade from, {role: Mesh} to draw: the same with its skirts)."""
    floor = math.floor
    x0, z0 = float(tile[0]*TILE_M), float(tile[1]*TILE_M)
    x1, z1 = x0+TILE_M, z0+TILE_M
    keys, members, lines_of = {}, [], []
    ids_of = {}
    for role, mesh in meshes.items():
        ids = []
        for point in mesh.pos:
            x, y, z = point
            lines = (x == x0) | (x == x1) << 1 | (z == z0) << 2 | (z == z1) << 3
            if lines == 0:
                key = (0, floor(x/spacing), floor(y/spacing), floor(z/spacing))
            elif lines in (1, 2):
                key = (lines, floor(z/spacing), floor(y/spacing))
            elif lines in (4, 8):
                key = (lines, floor(x/spacing), floor(y/spacing))
            else:
                key = (lines, x, y, z)  # a corner keeps its place
            cid = keys.get(key)
            if cid is None:
                cid = keys[key] = len(members)
                members.append(set())
                lines_of.append(lines)
            members[cid].add(point)
            ids.append(cid)
        ids_of[role] = ids
    reps = []
    for cid, points in enumerate(members):
        points = sorted(points)
        lines = lines_of[cid]
        if lines == 0:
            n = len(points)
            reps.append((sum(p[0] for p in points)/n, sum(p[1] for p in points)/n, sum(p[2] for p in points)/n))
        elif len(points) == 1:
            reps.append(points[0])
        else:
            # On an edge: the member nearest the cell's middle, a point both tiles sharing the edge hold.
            along = 2 if lines in (1, 2) else 0
            ca = (floor(points[0][along]/spacing)+.5)*spacing
            cy = (floor(points[0][1]/spacing)+.5)*spacing
            reps.append(min(points, key=lambda p: ((p[along]-ca)**2+(p[1]-cy)**2, p)))
    out, survivors = {}, {}
    edges = {}
    for role, mesh in meshes.items():
        ids = ids_of[role]
        kept, seen = [], set()
        for a, b, c in mesh.tris:
            ca, cb, cc = ids[a], ids[b], ids[c]
            if ca == cb or cb == cc or ca == cc:
                continue
            key = tuple(sorted((ca, cb, cc)))
            if key in seen or _area2(reps[ca], reps[cb], reps[cc]) <= 1e-18:
                continue
            seen.add(key)
            kept.append((ca, cb, cc))
        if not kept:
            out[role] = mesh  # collapsed: keep the level before's
            continue
        survivors[role] = kept
        for face in kept:
            for u, v in ((face[0], face[1]), (face[1], face[2]), (face[2], face[0])):
                edge = (u, v) if u < v else (v, u)
                edges[edge] = edges.get(edge, 0)+1
    drawn = {}
    for role, kept in survivors.items():
        mesh, ids = meshes[role], ids_of[role]
        nsum, csum, count = {}, {}, {}
        for i, cid in enumerate(ids):
            n, c = mesh.nrm[i], mesh.col[i]
            s = nsum.get(cid)
            if s is None:
                nsum[cid] = [n[0], n[1], n[2]]
                csum[cid] = [c[0], c[1], c[2]]
                count[cid] = 1
            else:
                s[0] += n[0]; s[1] += n[1]; s[2] += n[2]
                t = csum[cid]
                t[0] += c[0]; t[1] += c[1]; t[2] += c[2]
                count[cid] += 1
        local = {}
        result = Mesh()
        for face in kept:
            for cid in face:
                if cid not in local:
                    local[cid] = len(result.pos)
                    result.pos.append(reps[cid])
                    result.nrm.append(_unit(nsum[cid]))
                    k = count[cid]
                    t = csum[cid]
                    result.col.append((t[0]/k, t[1]/k, t[2]/k))
        result.tris = [(local[a], local[b], local[c]) for a, b, c in kept]
        out[role] = result
        # Skirts under every tile edge this role's triangles end on.
        skirt = Mesh()
        skirt.pos, skirt.nrm, skirt.col, skirt.tris = list(result.pos), list(result.nrm), list(result.col), list(result.tris)
        dropped = {}
        for face in kept:
            for u, v in ((face[0], face[1]), (face[1], face[2]), (face[2], face[0])):
                edge = (u, v) if u < v else (v, u)
                if edges[edge] != 1 or not lines_of[u] & lines_of[v]:
                    continue
                for cid in (u, v):
                    if cid not in dropped:
                        dropped[cid] = len(skirt.pos)
                        p = reps[cid]
                        skirt.pos.append((p[0], p[1]-spacing, p[2]))
                        skirt.nrm.append(result.nrm[local[cid]])
                        skirt.col.append(result.col[local[cid]])
                a, b, a2, b2 = local[u], local[v], dropped[u], dropped[v]
                skirt.tris += [(a, a2, b), (b, a2, b2)]
        drawn[role] = skirt
    return out, {role: drawn.get(role, out[role]) for role in meshes}


def build_levels(tiles, spacings):
    """Every tile's levels: {(tx, tz): [{role: Mesh} per level]}, level 0 the tiles as cut."""
    result = {}
    for tile, level0 in tiles.items():
        levels, current = [level0], level0
        for spacing in spacings[1:]:
            current, drawn = coarsen(tile, current, spacing)
            levels.append(drawn)
        result[tile] = levels
    return result


def encode_tiles(mat, nodes):
    """A tiled GLB: nodes [(name, Mesh)], each a root with its own mesh and no transform."""
    binary = bytearray()
    doc = {'asset': {'version': '2.0', 'generator': GLB_GENERATOR},
           'scene': 0, 'scenes': [{'nodes': list(range(len(nodes)))}], 'nodes': [], 'meshes': [],
           'materials': [mat], 'accessors': [], 'bufferViews': [], 'buffers': []}

    def view(data, record):
        binary.extend(b'\0'*(-len(binary) % 4))
        doc['bufferViews'].append({'buffer': 0, 'byteOffset': len(binary), 'byteLength': len(data)})
        binary.extend(data)
        record['bufferView'] = len(doc['bufferViews'])-1
        doc['accessors'].append(record)
        return len(doc['accessors'])-1

    for index, (name, mesh) in enumerate(nodes):
        positions = array('f', [v for p in mesh.pos for v in p])
        record = {'componentType': 5126, 'count': len(mesh.pos), 'type': 'VEC3'}
        record['min'] = [min(positions[i::3]) for i in range(3)]
        record['max'] = [max(positions[i::3]) for i in range(3)]
        attrs = {'POSITION': view(positions.tobytes(), record)}
        normals = array('f', [v for n in mesh.nrm for v in n])
        attrs['NORMAL'] = view(normals.tobytes(), {'componentType': 5126, 'count': len(mesh.nrm), 'type': 'VEC3'})
        colours = bytearray()
        for c in mesh.col:
            colours += bytes((round(c[0]*255), round(c[1]*255), round(c[2]*255), 255))
        attrs['COLOR_0'] = view(bytes(colours), {'componentType': 5121, 'normalized': True,
                                                 'count': len(mesh.col), 'type': 'VEC4'})
        wide = len(mesh.pos) > 65535
        indices = array('I' if wide else 'H', [i for t in mesh.tris for i in t])
        if indices.itemsize != (4 if wide else 2):
            raise ValueError('unexpected array item size')
        idx = view(indices.tobytes(), {'componentType': 5125 if wide else 5123, 'count': len(indices),
                                       'type': 'SCALAR'})
        doc['meshes'].append({'name': name, 'primitives': [{'attributes': attrs, 'indices': idx, 'mode': 4,
                                                            'material': 0}]})
        doc['nodes'].append({'name': name, 'mesh': index})
    binary.extend(b'\0'*(-len(binary) % 4))
    doc['buffers'] = [{'byteLength': len(binary)}]
    js = canonical(doc).rstrip(b'\n')
    js += b' '*(-len(js) % 4)
    return (struct.pack('<4sII', b'glTF', 2, 28+len(js)+len(binary)) +
            struct.pack('<I4s', len(js), b'JSON') + js +
            struct.pack('<I4s', len(binary), b'BIN\0') + bytes(binary))


def node_name(tile, level):
    return f'tile_{tile[0]}_{tile[1]}_lod{level}'


def tiled_parts(terrain, spacings):
    """{role: (material, [(node name, Mesh)], tile count, level-0 triangles)} for every terrain role, nodes sorted
    by tile then level; a role's tiles are those its level 0 reaches."""
    materials, tiles = tile_level0(terrain)
    levels = build_levels(tiles, spacings)
    result = {}
    for role, mat in materials.items():
        nodes, count, triangles = [], 0, 0
        for tile, per_level in levels.items():
            if role not in per_level[0]:
                continue
            count += 1
            triangles += len(per_level[0][role].tris)
            for level, meshes in enumerate(per_level):
                nodes.append((node_name(tile, level), meshes[role]))
        result[role] = (mat, nodes, count, triangles)
    return result


def open_edges(primitives):
    """mesh.open_edges with integer keys: geometric boundary edges, joining exact positions across parts."""
    ids, points = {}, []
    edges = {}
    for p in primitives:
        local = []
        for v in p['positions']:
            key = (v[0], v[1], v[2])
            index = ids.get(key)
            if index is None:
                index = ids[key] = len(points)
                points.append(key)
            local.append(index)
        for a, b, c in p['triangles']:
            a, b, c = local[a], local[b], local[c]
            for u, v in ((a, b), (b, c), (c, a)):
                edge = (u, v) if points[u] <= points[v] else (v, u)
                edges[edge] = edges.get(edge, 0)+1
    found = sorted((points[u], points[v]) for (u, v), count in edges.items() if count == 1)
    return [[list(a), list(b)] for a, b in found]
