"""Portable records, hash verification and bounded package validation."""
import argparse
import copy
import math
from pathlib import Path, PurePosixPath
from .common import (FORMAT, FORMATS, VERSION, FRAME, MAX_BYTES, MAX_TRIANGLES, MAX_INSTANCES, MAX_EXPANDED_TRIANGLES,
                     ROLES, canonical, sha, load_json, finite, fields, require,
                     number, vector, token)
from .glb import encode_mesh, decode_mesh
from .kit import SIZES, roles, prototype


def inside_polygon(x,z,poly):
    inside=False
    for a,b in zip(poly,poly[1:]+poly[:1]):
        ax,az=a[0],a[2]; bx,bz=b[0],b[2]
        cross=(x-ax)*(bz-az)-(z-az)*(bx-ax)
        if abs(cross)<1e-7 and min(ax,bx)-1e-7<=x<=max(ax,bx)+1e-7 and min(az,bz)-1e-7<=z<=max(az,bz)+1e-7:
            return True
        if (az>z)!=(bz>z) and x<(bx-ax)*(z-az)/(bz-az)+ax:
            inside=not inside
    return inside


_CONVEX={}


def _convex(poly):
    """Whether a floor polygon (x, z) turns one way at every corner (read once per polygon)."""
    key=id(poly); got=_CONVEX.get(key)
    if got is None or got[0] is not poly:
        n=len(poly); turns=set()
        for k in range(n):
            a,b,c=poly[k],poly[(k+1)%n],poly[(k+2)%n]
            cross=(b[0]-a[0])*(c[2]-b[2])-(b[2]-a[2])*(c[0]-b[0])
            if abs(cross)>1e-12: turns.add(cross>0)
        got=_CONVEX[key]=(poly,len(turns)==1,(1. if True in turns else -1.))
    return got[1]


def _inward(poly,x,z):
    """Least distance of (x, z) inside a convex polygon's edges (negative outside)."""
    sign=_CONVEX[id(poly)][2]; best=float('inf')
    for a,b in zip(poly,poly[1:]+poly[:1]):
        ex,ez=b[0]-a[0],b[2]-a[2]; L=math.hypot(ex,ez)
        if L>0: best=min(best,sign*(ex*(z-a[2])-ez*(x-a[0]))/L)
    return best


def footprint_inside(position,size,yaw,room):
    lo,hi=room['bounds']['min_m'],room['bounds']['max_m']
    a=math.radians(yaw); c,s=math.cos(a),math.sin(a)
    corners=[[position[0]+c*x+s*z,position[2]-s*x+c*z]
             for x,z in [(-size[0]/2,-size[2]/2),(size[0]/2,-size[2]/2),
                         (size[0]/2,size[2]/2),(-size[0]/2,size[2]/2)]]
    require(lo[1]-1e-6<=position[1] and position[1]+size[1]<=hi[1]+1e-6, 'object outside vertical bounds')
    require(all(lo[0]-1e-6<=x<=hi[0]+1e-6 and lo[2]-1e-6<=z<=hi[2]+1e-6 for x,z in corners), 'object outside bounds')
    floors=[p['geometry']['points_m'] for p in room['shell']['parts'] if p['role']=='floor']
    # Inside a convex floor by more than a micrometre at all four corners: the whole footprint is inside it.
    for poly in floors:
        if _convex(poly) and all(_inward(poly,x,z)>1e-6 for x,z in corners):
            return
    # Segment partition at every polygon edge intersection also checks concavities.
    for a,b in zip(corners,corners[1:]+corners[:1]):
        cuts=[0.,1.]; dx,dz=b[0]-a[0],b[1]-a[1]
        for poly in floors:
            for u,v in zip(poly,poly[1:]+poly[:1]):
                ex,ez=v[0]-u[0],v[2]-u[2]; det=dx*ez-dz*ex
                if abs(det)>1e-12:
                    ux,uz=u[0]-a[0],u[2]-a[1]
                    t=(ux*ez-uz*ex)/det; q=(ux*dz-uz*dx)/det
                    if 0<t<1 and 0<=q<=1:
                        cuts.append(t)
        cuts=sorted(cuts)
        probes=cuts+[(t+u)/2 for t,u in zip(cuts,cuts[1:])]
        require(all(any(inside_polygon(a[0]+t*dx,a[1]+t*dz,p) for p in floors) for t in probes), 'object outside floor polygon')


def world_scale(doc):
    """The 10x world (S4): a package of a room magnified x W says so (`x_generator.world_scale`); its source stays the
    true-size room (its hashes), and its places are validated against that room magnified. 1 for any other package."""
    gen=doc.get('x_generator') if isinstance(doc,dict) else None
    w=gen.get('world_scale',1) if isinstance(gen,dict) else 1
    require(type(w) in (int,float) and math.isfinite(w) and 0<w<=100, 'world scale')
    return float(w)


def magnify_room(room, w):
    """The room magnified x w (a copy; metres only: bounds, the shell's polygons, openings, spawns and lights; angles,
    ids and text unchanged; wall thickness stays, it is not part of the land). The player arrives where the room puts
    it (x w); every other spawn keeps its offset from the player (the Gubble arrives beside the player as it does
    today). The generator magnifies with this too (generator/scale.py)."""
    room=copy.deepcopy(room); w=float(w)
    if w==1:
        return room
    room['bounds']={k:[v*w for v in room['bounds'][k]] for k in ('min_m','max_m')}
    for part in room['shell']['parts']:
        g=part['geometry']
        if 'points_m' in g:
            g['points_m']=[[v*w for v in p] for p in g['points_m']]
    for o in room['shell'].get('openings',[]):
        o['center_m']=[v*w for v in o['center_m']]
        o['size_m']=[v*w for v in o['size_m']]
    player=next((s for s in room['spawns'] if s['role']=='player'),room['spawns'][0])
    p0=list(player['position_m'])
    for s in room['spawns']:
        s['position_m']=[a*w+(b-a) for a,b in zip(p0,s['position_m'])]
    for lh in room.get('light_hints',[]):
        if 'position_m' in lh:
            lh['position_m']=[v*w for v in lh['position_m']]
    return room


def magnify_inventory(inventory, w):
    """The inventory magnified x w (a copy: boxes, footprints, placements and support heights in metres; kinds,
    confidences and colours unchanged)."""
    inv=copy.deepcopy(inventory); w=float(w)
    if w==1:
        return inv
    for e in inv['objects']:
        b=e['box']
        b['centre_m']=[v*w for v in b['centre_m']]
        b['size_m']=[v*w for v in b['size_m']]
        if 'footprint_m' in b:
            b['footprint_m']=[v*w for v in b['footprint_m']]
        pl=e['placement']
        pl['position_m']=[v*w for v in pl['position_m']]
        if 'height_m' in pl.get('support',{}):
            pl['support']['height_m']*=w
        if e.get('recipe_needed') and 'size_m' in e['recipe_needed']:
            e['recipe_needed']['size_m']=[v*w for v in e['recipe_needed']['size_m']]
    return inv


def validate_setup(setup):
    fields(setup,['gameplay_mode','water','latitude_deg','neg_z_bearing_deg','season','day_of_year','solar_time_h'])
    require(setup['gameplay_mode']=='sandbox', 'unknown gameplay mode')
    require(setup['water'] in ('none','a_little','some','plenty'), 'unknown water answer')
    require(setup['season'] in ('spring','summer','autumn','winter'), 'unknown season')
    require(type(setup['latitude_deg']) is int, 'latitude must be coarse whole degrees')
    require(type(setup['neg_z_bearing_deg']) is int, 'bearing must be whole degrees')
    number(setup['latitude_deg'],-66,66); number(setup['neg_z_bearing_deg'],0,359)
    require(type(setup['day_of_year']) is int, 'day must be integer')
    number(setup['day_of_year'],1,365); number(setup['solar_time_h'],0,23.999999)


# Today's limits hold for an island of up to LAND_M2 of land. A bigger island (the 10x world) holds more land, sea
# and planting at today's density, so each limit grows with its land: x max(1, A / LAND_M2).
LAND_M2 = 100


def land_factor(doc, meshes):
    """max(1, A / 100 m2), A the terrain's horizontal extent: the x-z bounding box of every terrain mesh. Malformed
    records count for nothing here; validate_records refuses them."""
    terrain = doc.get('terrain') if isinstance(doc, dict) else None
    lo_x = lo_z = math.inf; hi_x = hi_z = -math.inf
    for item in terrain if isinstance(terrain, list) else []:
        mesh = meshes.get(item.get('mesh')) if isinstance(item, dict) and isinstance(item.get('mesh'), str) else None
        for p in mesh or []:
            xs = [v[0] for v in p['positions']]; zs = [v[2] for v in p['positions']]
            lo_x = min(lo_x, min(xs)); hi_x = max(hi_x, max(xs)); lo_z = min(lo_z, min(zs)); hi_z = max(hi_z, max(zs))
    if not lo_x < hi_x or not lo_z < hi_z:
        return 1.
    return max(1., (hi_x-lo_x)*(hi_z-lo_z)/LAND_M2)


def validate_records(doc, meshes, room):
    finite(doc)
    factor = land_factor(doc, meshes)
    fields(doc,['format','version','units','axes','floor_y_m','source','setup','generator',
                'files','material_roles','meshes','terrain','water','scenery','prototypes','scatter','objects'])
    require(doc['format'] in FORMATS and type(doc['version']) is int and doc['version']==VERSION, 'format/version')
    require(doc['units']=='m' and doc['axes']==FRAME and doc['floor_y_m']==0, 'units/frame')
    require(room['units']=='m' and room['axes']==FRAME, 'room units/frame')
    fields(doc['source'],['room_id','room_sha256','inventory_sha256'])
    require(doc['source']['room_id']==room['room_id'], 'source room id')
    fields(doc['generator'],['name','version'])
    for value in doc['generator'].values():
        require(isinstance(value,str) and 0<len(value)<=120, 'generator metadata')
    validate_setup(doc['setup'])
    used=set(); triangles=0
    require(isinstance(doc['meshes'],dict) and doc['meshes'].keys()==meshes.keys(), 'mesh index')
    for name, primitives in meshes.items():
        token(name)
        for p in primitives:
            used.add(p['role'])
            if 'blend_role' in p: used.add(p['blend_role'])
            triangles += len(p['triangles'])
    require(triangles<=MAX_TRIANGLES*factor, 'triangle limit')
    require(isinstance(doc['prototypes'],dict), 'prototype index')
    for name,p in doc['prototypes'].items():
        token(name); require(name not in SIZES, 'kit prototype shadow')
        fields(p,['mesh','size_m']); require(p['mesh'] in meshes, 'unknown prototype mesh')
        vector(p['size_m'],low=.0001,high=100)
        vs=[v for part in meshes[p['mesh']] for v in part['positions']]
        lo=[min(v[i] for v in vs) for i in range(3)]; hi=[max(v[i] for v in vs) for i in range(3)]
        require(abs(lo[1])<1e-5 and abs(lo[0]+hi[0])<1e-5 and abs(lo[2]+hi[2])<1e-5,
                'prototype pivot must be bottom centre')
        require(all(abs(hi[i]-lo[i]-p['size_m'][i])<1e-5 for i in range(3)), 'prototype size mismatch')
    mesh_counts={name:sum(len(p['triangles']) for p in ps) for name,ps in meshes.items()}
    kit_counts={name:sum(len(p['triangles']) for p in prototype(name)) for name in SIZES}
    expanded=0
    for category in ['terrain','water','scenery']:
        require(isinstance(doc[category],list), 'mesh records must be lists')
        for item in doc[category]:
            fields(item,['mesh']+(['kind'] if category=='water' else ['reachable'] if category=='scenery' else []))
            require(item['mesh'] in meshes,'unknown mesh')
            expanded += mesh_counts[item['mesh']]
            if category=='water': require(item['kind'] in ('still','flowing','falling'),'water kind')
            if category=='scenery': require(item['reachable'] is False,'scenery must be unreachable')
    require(isinstance(doc['scatter'],list) and isinstance(doc['objects'],list), 'instance lists')
    require(len(doc['scatter'])+len(doc['objects'])<=MAX_INSTANCES*factor, 'instance limit')
    ids=set()
    for category in ['scatter','objects']:
        for item in doc[category]:
            fields(item,['prototype','position_m','yaw_deg'] +
                   (['scale'] if category=='scatter' else ['id','kind','size_m','mass_kg','carriable']),['tint'])
            name=item['prototype']; require(name in SIZES or name in doc['prototypes'], 'unknown prototype')
            expanded += kit_counts[name] if name in SIZES else mesh_counts[doc['prototypes'][name]['mesh']]
            require(expanded<=MAX_EXPANDED_TRIANGLES*factor, 'expanded triangle limit')
            size=SIZES[name] if name in SIZES else doc['prototypes'][name]['size_m']
            vector(item['position_m'],low=-1000,high=1000); number(item['yaw_deg'],-360,360)
            if category=='scatter':
                vector(item['scale'],low=.001,high=100)
                size=[a*b for a,b in zip(size,item['scale'])]
            else:
                token(item['id']); token(item['kind']); require(item['id'] not in ids,'duplicate object id'); ids.add(item['id'])
                vector(item['size_m'],low=.0001,high=100); size=item['size_m']
                number(item['mass_kg'],0,1e6); require(type(item['carriable']) is bool,'carriable must be bool')
            if 'tint' in item: vector(item['tint'],3,0,1)
            footprint_inside(item['position_m'],size,item['yaw_deg'],room)
            if name in SIZES: used.update(roles(name))
    require(isinstance(doc['material_roles'],list) and doc['material_roles']==sorted(used), 'material role inventory')
    require(expanded<=MAX_EXPANDED_TRIANGLES*factor, 'expanded triangle limit')
    return {'triangles':triangles,'expanded_triangles':expanded,'instances':len(doc['scatter'])+len(doc['objects'])}


def read_package(folder, room_folder):
    folder=Path(folder).resolve(); room_folder=Path(room_folder).resolve()
    require(folder.is_dir(), 'missing package folder')
    paths=list(folder.rglob('*'))
    require(not any(p.is_symlink() for p in paths), 'package symlinks forbidden')
    paths=[p for p in paths if p.is_file()]
    require(len(paths)<=256, 'package byte/file limit')
    total=sum(p.stat().st_size for p in paths)
    doc=load_json(folder/'package.json'); fields(doc,['format','version','units','axes','floor_y_m','source',
        'setup','generator','files','material_roles','meshes','terrain','water','scenery','prototypes','scatter','objects'])
    require(isinstance(doc['files'],dict), 'file inventory')
    actual={p.relative_to(folder).as_posix() for p in paths} - {'package.json'}
    require(actual==set(doc['files']), 'unlisted or missing file')
    blobs={}
    for name, digest in doc['files'].items():
        pure=PurePosixPath(name)
        require(not pure.is_absolute() and '..' not in pure.parts and '\\' not in name and ':' not in name,
                'unsafe file path')
        require(name.endswith('.glb'), 'only indexed mesh files allowed; debug data goes under x_')
        data=(folder/name).read_bytes(); require(sha(data)==digest, 'file hash mismatch: '+name); blobs[name]=data
    room=load_json(room_folder/'room.json')
    require(doc['source']['room_sha256']==sha((room_folder/'room.json').read_bytes()), 'room hash mismatch')
    require(doc['source']['inventory_sha256']==sha((room_folder/'inventory.json').read_bytes()), 'inventory hash mismatch')
    room=magnify_room(room,world_scale(doc))  # the 10x world: places are the magnified room's (returned so)
    require(isinstance(doc['meshes'],dict) and len(doc['meshes'])<=128, 'mesh count')
    require(set(doc['meshes'].values())==set(blobs) and len(set(doc['meshes'].values()))==len(doc['meshes']), 'mesh file mapping')
    # The terrain first: its extent sets the limits (land_factor), so the byte limit is checked before the rest.
    terrain=[r.get('mesh') for r in doc['terrain'] if isinstance(r,dict)] if isinstance(doc['terrain'],list) else []
    land={key:decode_mesh(blobs[doc['meshes'][key]]) for key in doc['meshes'] if key in terrain}
    require(total<=MAX_BYTES*land_factor(doc,land), 'package byte/file limit')
    meshes={key:land[key] if key in land else decode_mesh(blobs[name]) for key,name in doc['meshes'].items()}
    stats=validate_records(doc,meshes,room)
    return doc,meshes,room,stats


def validate_package(folder,room_folder):
    return read_package(folder,room_folder)[3]


def write_package(folder,room_folder,*,meshes,setup,generator,terrain,water=(),scenery=(),prototypes=None,scatter=(),objects=(),extensions=None):
    folder=Path(folder).resolve(); room_folder=Path(room_folder).resolve()
    require(not folder.exists() or not any(folder.iterdir()), 'writer requires an empty output folder')
    blobs={}
    for name,p in sorted(meshes.items()):
        token(name); blobs[name+'.glb']=encode_mesh(p)
    # Decode before validation to catch triangles collapsed by float32 quantization.
    rounded={name:decode_mesh(blobs[name+'.glb'],canonical=False) for name in meshes}
    used={p['role'] for ps in rounded.values() for p in ps}
    used.update(p['blend_role'] for ps in rounded.values() for p in ps if 'blend_role' in p)
    for item in list(scatter)+list(objects):
        if item['prototype'] in SIZES: used.update(roles(item['prototype']))
    room=load_json(room_folder/'room.json')
    doc=dict(format=FORMAT,version=VERSION,units='m',axes=FRAME,floor_y_m=0,
             source=dict(room_id=room['room_id'],room_sha256=sha((room_folder/'room.json').read_bytes()),
                         inventory_sha256=sha((room_folder/'inventory.json').read_bytes())),
             setup=setup,generator=generator,files={k:sha(v) for k,v in blobs.items()},
             material_roles=sorted(used),meshes={n:n+'.glb' for n in sorted(meshes)},
             terrain=list(terrain),water=list(water),scenery=list(scenery),prototypes=prototypes or {},
             scatter=list(scatter),objects=list(objects))
    for key,value in (extensions or {}).items():
        require(key.startswith('x_'),'extension prefix'); doc[key]=value
    validate_records(doc,rounded,magnify_room(room,world_scale(doc)))
    require(sum(map(len,blobs.values()))+len(canonical(doc))<=MAX_BYTES*land_factor(doc,rounded),'package byte limit')
    folder.mkdir(parents=True,exist_ok=True)
    for name,data in blobs.items(): (folder/name).write_bytes(data)
    (folder/'package.json').write_bytes(canonical(doc))
    return doc


def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--package',required=True); parser.add_argument('--room',required=True)
    args=parser.parse_args()
    try:
        print('PACKAGE_VALID',validate_package(args.package,args.room))
    except (ValueError, OSError) as exc:
        parser.exit(1,'PACKAGE_INVALID '+str(exc)+'\n')


if __name__=='__main__': main()
