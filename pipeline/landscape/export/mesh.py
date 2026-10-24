"""Plain self-contained GLBs, indexed smooth normals, and terrain queries."""
import math
import struct
from pipeline.landscape.harness.common import canonical
from .ids import GLB_GENERATOR
from .materials import PALETTE, material


def cross(a, b):
    return [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]]


def normals(positions, triangles):
    sums = [[0., 0., 0.] for _ in positions]
    for face in triangles:
        a, b, c = [positions[i] for i in face]
        n = cross([b[i]-a[i] for i in range(3)], [c[i]-a[i] for i in range(3)])
        for index in face:
            for i in range(3):
                sums[index][i] += n[i]
    result = []
    for n in sums:
        length = math.sqrt(sum(v*v for v in n))
        result.append([v/length for v in n] if length > 1e-20 else [0., 1., 0.])
    return result


def transform(primitives, scale=(1, 1, 1), position=(0, 0, 0), yaw=0, tint=(1, 1, 1)):
    """Bake S then Godot +Y rotation then T; indices are never welded."""
    angle = math.radians(yaw)
    c, s = math.cos(angle), math.sin(angle)
    result = []
    for p in primitives:
        q = dict(p)
        q['positions'] = []
        for v in p['positions']:
            x, y, z = [v[i]*scale[i] for i in range(3)]
            q['positions'].append([position[0]+c*x+s*z, position[1]+y, position[2]-s*x+c*z])
        q['tints'] = [[t[i]*tint[i] for i in range(3)] + [1]
                      for t in p.get('tints', [[1, 1, 1, 1]]*len(p['positions']))]
        result.append(q)
    return result


def encode(primitives):
    """One primitive per primary role; preserve each input's vertex identity."""
    grouped = {}
    for p in primitives:
        grouped.setdefault(p['role'], []).append(p)
    binary = bytearray()
    doc = {'asset': {'version': '2.0', 'generator': GLB_GENERATOR},
           'scene': 0, 'scenes': [{'nodes': [0]}], 'nodes': [{'mesh': 0}],
           'meshes': [{'primitives': []}], 'materials': [], 'accessors': [],
           'bufferViews': [], 'buffers': []}

    def accessor(rows, kind, component=5126):
        flat = [v for row in rows for v in row] if kind != 'SCALAR' else rows
        data = struct.pack('<' + ('f' if component == 5126 else 'I')*len(flat), *flat)
        index = len(doc['accessors'])
        view = len(doc['bufferViews'])
        doc['bufferViews'].append({'buffer': 0, 'byteOffset': len(binary), 'byteLength': len(data)})
        record = {'bufferView': view, 'componentType': component, 'count': len(rows), 'type': kind}
        if kind == 'VEC3':
            rounded = list(struct.iter_unpack('<fff', data))
            record['min'] = [min(v[i] for v in rounded) for i in range(3)]
            record['max'] = [max(v[i] for v in rounded) for i in range(3)]
        doc['accessors'].append(record)
        binary.extend(data)
        return index

    for role, parts in sorted(grouped.items()):
        blends = sorted({p['blend_role'] for p in parts if 'blend_role' in p})
        mat = material(role, blends)
        base = mat['pbrMetallicRoughness']['baseColorFactor']
        positions, ns, colors, indices = [], [], [], []
        for p in parts:
            offset = len(positions)
            positions.extend(p['positions'])
            ns.extend(normals(p['positions'], p['triangles']))
            indices.extend(offset+i for t in p['triangles'] for i in t)
            for j in range(len(p['positions'])):
                w = p.get('blend_weights', [])[j] if 'blend_role' in p else 0
                second = PALETTE[p.get('blend_role', role)]
                tint = p['tints'][j] if 'tints' in p else [1, 1, 1, 1]
                colors.append([min(1, max(0, ((1-w)*PALETTE[role][i]+w*second[i])*tint[i]/base[i]))
                               for i in range(3)] + [1])
        attrs = {'POSITION': accessor(positions, 'VEC3'), 'NORMAL': accessor(ns, 'VEC3'),
                 'COLOR_0': accessor(colors, 'VEC4')}
        doc['meshes'][0]['primitives'].append({'attributes': attrs,
            'indices': accessor(indices, 'SCALAR', 5125), 'mode': 4, 'material': len(doc['materials'])})
        doc['materials'].append(mat)
    doc['buffers'] = [{'byteLength': len(binary)}]
    js = canonical(doc).rstrip(b'\n')
    js += b' ' * (-len(js) % 4)
    binary.extend(b'\0' * (-len(binary) % 4))
    return (struct.pack('<4sII', b'glTF', 2, 28+len(js)+len(binary)) +
            struct.pack('<I4s', len(js), b'JSON') + js +
            struct.pack('<I4s', len(binary), b'BIN\0') + binary)


def ground(primitives, x, z):
    """Topmost downward triangle hit, including caves/overhangs (as harness)."""
    best = None
    for p in primitives:
        for t in p['triangles']:
            a, b, c = [p['positions'][i] for i in t]
            det = (b[2]-c[2])*(a[0]-c[0])+(c[0]-b[0])*(a[2]-c[2])
            if abs(det) < 1e-14:
                continue
            u = ((b[2]-c[2])*(x-c[0])+(c[0]-b[0])*(z-c[2]))/det
            v = ((c[2]-a[2])*(x-c[0])+(a[0]-c[0])*(z-c[2]))/det
            if min(u, v, 1-u-v) >= -1e-7:
                y = u*a[1]+v*b[1]+(1-u-v)*c[1]
                if best is None or y > best:
                    best = y
    return best


def open_edges(primitives):
    """Geometric boundary diagnostics, joining exact positions across role seams."""
    edges = {}
    for p in primitives:
        for t in p['triangles']:
            vs = [tuple(p['positions'][i]) for i in t]
            for a, b in zip(vs, vs[1:]+vs[:1]):
                edge = tuple(sorted((a, b)))
                edges[edge] = edges.get(edge, 0)+1
    return [list(map(list, e)) for e, count in sorted(edges.items()) if count == 1]
