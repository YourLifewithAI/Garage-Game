"""Build original, editable painterly tree studies with Blender's Python runtime.

Run: blender --background --python tools/art/build_painterly_trees.py -- --species all
The branch architecture is authored here; deterministic scatter fills its canopy
guides with textured twig cards. Guides are retained in the .blend but never in
the exported game mesh. Blender Z-up metres become Godot Y-up through glTF.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import random
import struct
import sys
from pathlib import Path

import bpy
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[2]
SOURCE_DIR = ROOT / "assets/art_sources/painterly/trees"
GAME_DIR = ROOT / "game/assets/art/painterly/trees"
TEXTURES = ROOT / "game/assets/art/painterly/textures"
SEED = 3010250924

# Path points: x, y, height in metres, radius. Curves remain editable in source.
OAK_BRANCHES = [
    [(0,0,0,.38),(-.12,.02,.55,.355),(-.19,.05,1.3,.31),(.05,.03,2.0,.255),(.12,.03,2.65,.19),(-.10,.15,3.45,.132),(.08,.34,4.10,.084),(-.18,.58,4.85,.043),(-.47,.77,5.59,.013)],
    [(-.06,.03,1.76,.215),(-.51,-.03,2.22,.179),(-1.20,-.10,2.60,.132),(-2.10,-.20,2.88,.092),(-3.02,-.31,3.27,.050),(-3.78,-.16,3.75,.014)],
    [(.10,.03,2.34,.181),(.73,.08,2.55,.148),(1.43,.10,2.67,.108),(2.29,.18,3.04,.068),(3.18,.35,3.57,.031),(3.83,.52,4.04,.010)],
    [(-.01,.08,2.95,.128),(-.48,.67,3.18,.093),(-.81,1.43,3.61,.060),(-1.08,2.31,4.13,.026),(-1.18,2.99,4.58,.008)],
    [(.06,.03,2.52,.145),(.28,-.60,2.66,.106),(.58,-1.41,2.97,.074),(.37,-2.25,3.29,.043),(.06,-3.06,3.67,.012)],
    [(-2.08,-.19,2.88,.079),(-2.48,.39,3.37,.054),(-2.77,1.07,3.79,.031),(-2.99,1.66,4.07,.008)],
    [(1.65,.12,2.76,.086),(1.91,-.55,3.17,.056),(2.48,-1.15,3.55,.030),(2.93,-1.57,3.88,.008)],
    [(-.12,.46,4.50,.050),(.42,1.01,4.93,.030),(1.12,1.53,5.39,.008)],
    [(-.10,.17,3.55,.097),(-.77,-.05,4.12,.060),(-1.46,-.27,4.61,.031),(-2.08,-.15,4.93,.008)],
]


def vec(point):
    return Vector(point[:3])


def catmull_samples(points, steps=6):
    result = []
    for index in range(len(points)-1):
        p0 = Vector(points[max(0,index-1)])
        p1 = Vector(points[index])
        p2 = Vector(points[index+1])
        p3 = Vector(points[min(len(points)-1,index+2)])
        for step in range(steps):
            t = step/steps
            result.append(.5*((2*p1)+(-p0+p2)*t+(2*p0-5*p1+4*p2-p3)*t*t+(-p0+3*p1-3*p2+p3)*t*t*t))
    result.append(Vector(points[-1]))
    return result


def oak_architecture():
    branches=[list(path) for path in OAK_BRANCHES]
    crowns=[]
    # The final canopy follows smaller horizontal boughs, rather than placing a
    # round pad at the end of each equally thick main fork. Each row is authored:
    # primary branch, attachment fraction, direction, length, lift, leaf count.
    fans=[
        (0,.70,160,1.15,.75,18),(0,.89,-35,1.13,.62,18),(0,1,85,.80,.40,16),
        (1,.58,-120,1.25,.72,18),(1,.81,150,1.10,.62,20),(1,1,195,.62,.40,16),
        (2,.60,-40,1.20,.72,19),(2,.83,35,1.07,.57,18),(2,1,5,.63,.39,15),
        (3,.55,170,1.05,.63,18),(3,.80,65,1.02,.58,18),(3,1,100,.69,.40,15),
        (4,.57,-50,1.03,.58,18),(4,.78,-135,1.05,.50,18),(4,1,-100,.65,.34,15),
        (5,.65,130,.91,.56,17),(5,.98,90,.71,.41,15),
        (6,.60,-10,1.01,.55,17),(6,1,-45,.74,.39,15),
        (7,.65,55,.91,.45,17),(7,1,15,.77,.37,15),
        (8,.60,-135,.92,.46,18),(8,1,175,.76,.33,16),
    ]
    for index,(parent,fraction,bearing,length,lift,count) in enumerate(fans):
        samples=catmull_samples(OAK_BRANCHES[parent],12)
        attachment=samples[round((len(samples)-1)*fraction)]
        start=Vector(attachment[:3])
        angle=math.radians(bearing)
        outward=Vector((math.cos(angle),math.sin(angle),0))
        cross=Vector((-outward.y,outward.x,0))
        root_radius=min(.063,max(.023,attachment[3]*.52))
        middle=start+outward*length*.45+cross*(.12 if index%2 else -.08)+Vector((0,0,lift*.69))
        end=start+outward*length+Vector((0,0,lift))
        tip=end+outward*.18+cross*.08+Vector((0,0,.07))
        branches.append([(*start,root_radius),(*middle,root_radius*.64),(*end,.010),(*tip,.005)])
        # Staggered elongated foliage lenses overlap along their supporting
        # bough, giving irregular horizontal masses instead of terminal balls.
        main=start.lerp(end,.73)+Vector((0,0,.16))
        crowns.append((tuple(main),(.52+length*.34,.34+(index%3)*.045,.22+(index%2)*.055),count,angle))
        bridge=start.lerp(end,.33)+Vector((0,0,.14))
        crowns.append((tuple(bridge),(.43,.31,.20),8,angle+.16))
    return branches,crowns


def make_collection(name):
    collection = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(collection)
    return collection


def move_to_collection(obj, collection):
    for current in list(obj.users_collection):
        current.objects.unlink(obj)
    collection.objects.link(obj)


def guide_curve(points, name, collection):
    curve = bpy.data.curves.new(name, 'CURVE')
    curve.dimensions = '3D'
    curve.resolution_u = 12
    spline = curve.splines.new('BEZIER')
    spline.bezier_points.add(len(points)-1)
    for target, source in zip(spline.bezier_points, points):
        target.co = source[:3]
        target.radius = source[3]
        target.handle_left_type = 'AUTO'
        target.handle_right_type = 'AUTO'
    obj = bpy.data.objects.new(name, curve)
    collection.objects.link(obj)
    obj.hide_render = True
    obj['purpose'] = 'Editable authored branch control; mesh rebuilt by deterministic script.'
    return obj


def bark_material():
    mat = bpy.data.materials.new('Bark painterly')
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get('Principled BSDF')
    bsdf.inputs['Base Color'].default_value = (.28,.24,.18,1)
    bsdf.inputs['Roughness'].default_value = .92
    bark_path = TEXTURES / 'bark-paint-v1.png'
    if bark_path.exists():
        tex = mat.node_tree.nodes.new('ShaderNodeTexImage')
        tex.image = bpy.data.images.load(str(bark_path), check_existing=True)
        mat.node_tree.links.new(tex.outputs['Color'],bsdf.inputs['Base Color'])
    return mat


def leaf_material(species):
    mat = bpy.data.materials.new('Foliage '+species)
    mat.use_nodes = True
    mat.use_backface_culling = False
    mat.surface_render_method = 'DITHERED'
    bsdf = mat.node_tree.nodes.get('Principled BSDF')
    bsdf.inputs['Roughness'].default_value = .95
    bsdf.inputs['Specular IOR Level'].default_value = .08
    image_path = TEXTURES / ('juniper-spray-v1.png' if species == 'juniper' else 'oak-leaf-cluster-v1.png')
    if not image_path.exists():
        raise FileNotFoundError('Final original leaf atlas required: '+str(image_path))
    tex = mat.node_tree.nodes.new('ShaderNodeTexImage')
    tex.image = bpy.data.images.load(str(image_path),check_existing=True)
    mat.node_tree.links.new(tex.outputs['Color'],bsdf.inputs['Base Color'])
    mat.node_tree.links.new(tex.outputs['Alpha'],bsdf.inputs['Alpha'])
    mat['cutout_threshold'] = .42
    mat['vertex_channels'] = 'R=canopy interior occlusion; G=height wind weight; B=stable variation; A=1'
    return mat,image_path


def make_trunks(branches, collection, guides):
    vertices,faces,uvs = [],[],[]
    for branch_index,points in enumerate(branches):
        sides = 7 if max(point[3] for point in points)<.09 else 10
        guide_curve(points,f'Branch guide {branch_index:02d}',guides)
        samples = catmull_samples(points,4 if sides==7 else 6)
        base = len(vertices)
        distance = 0.0
        prev_side = Vector((1,0,0))
        for j,sample in enumerate(samples):
            centre = Vector(sample[:3])
            if j:
                distance += (centre-Vector(samples[j-1][:3])).length
            tangent = Vector(samples[min(j+1,len(samples)-1)][:3])-Vector(samples[max(0,j-1)][:3])
            tangent.normalize()
            side = prev_side-tangent*prev_side.dot(tangent)
            if side.length < .01:
                side = tangent.cross(Vector((0,1,0)))
            side.normalize()
            other = tangent.cross(side).normalized()
            prev_side = side
            for k in range(sides+1):
                angle = 2*math.pi*k/sides
                ripple = 1+.045*math.sin(angle*3+branch_index*1.21)+.023*math.sin(angle*5+j*.3)
                radius = max(.009,sample[3])*ripple
                vertices.append(centre+(side*math.cos(angle)+other*math.sin(angle))*radius)
                uvs.append((k/sides,distance/2.6))
        for j in range(len(samples)-1):
            for k in range(sides):
                a=base+j*(sides+1)+k
                faces.append((a,a+1,a+sides+2,a+sides+1))
        faces.append(tuple(base+k for k in reversed(range(sides))))
        final=base+(len(samples)-1)*(sides+1)
        faces.append(tuple(final+k for k in range(sides)))
    mesh=bpy.data.meshes.new('Painterly authored branch architecture')
    mesh.from_pydata(vertices,[],faces)
    mesh.update()
    uv=mesh.uv_layers.new(name='BarkUV')
    for loop in mesh.loops:
        uv.data[loop.index].uv=uvs[loop.vertex_index]
    for poly in mesh.polygons:
        poly.use_smooth=True
    obj=bpy.data.objects.new('Trunk',mesh)
    collection.objects.link(obj)
    obj.data.materials.append(bark_material())
    obj['dimensions_unit']='metres'
    obj['authoring']='Original tapering curve architecture; no scan claim.'
    return obj


def crown_guide(centre,radii,name,collection,bearing=0.0):
    obj=bpy.data.objects.new(name,None)
    collection.objects.link(obj)
    obj.empty_display_type='SPHERE'
    obj.empty_display_size=1
    obj.location=centre
    obj.scale=radii
    obj.rotation_euler.z=bearing
    obj.hide_render=True
    obj['purpose']='Leaf placement and analytic normal guide; not exported/rendered.'


def make_leaves(species,crowns,collection,guides):
    rng=random.Random(SEED+(0 if species=='oak' else 73))
    vertices,faces,uvs,normals,colors=[],[],[],[],[]
    # Bent 3x3 card grid. Its silhouette comes from the alpha image. Four corners
    # pull inward slightly; unlike full-screen billboards each card is fixed 3D.
    grid=[(0,0),(.5,0),(1,0),(0,.5),(.5,.5),(1,.5),(0,1),(.5,1),(1,1)]
    for group,crown in enumerate(crowns):
        centre_values,radii_values,count=crown[:3]
        bearing=crown[3] if len(crown)>3 else 0.0
        cos_b,sin_b=math.cos(bearing),math.sin(bearing)
        def rotate(local):
            return Vector((local.x*cos_b-local.y*sin_b,local.x*sin_b+local.y*cos_b,local.z))
        centre=Vector(centre_values)
        radii=Vector(radii_values)
        crown_guide(centre,radii,f'Canopy normal guide {group:02d}',guides,bearing)
        for card_index in range(count):
            azimuth=rng.uniform(0,2*math.pi)
            height=rng.uniform(-.69,1) if species=='oak' else rng.uniform(-.12,1)
            planar=math.sqrt(max(0,1-height*height))
            direction=Vector((math.cos(azimuth)*planar,math.sin(azimuth)*planar,height))
            # Most cards define the soft exterior; some fill interior branch groups.
            radial=rng.uniform(.66,1.03) if card_index%3 else rng.uniform(.35,.69)
            position=centre+rotate(Vector((direction.x*radii.x,direction.y*radii.y,direction.z*radii.z)))*radial
            # Irregular open seams reveal branches and sky rather than solid balls.
            if group%3==1 and direction.x < -.58 and abs(direction.y) < .28:
                continue
            outward=rotate(Vector((direction.x/radii.x,direction.y/radii.y,direction.z/radii.z))).normalized()
            if species=='juniper':
                # Evergreen sprays grow up/out. Radial lower-shell normals made
                # the first attempt read as drooping Christmas-tree skirts.
                facing=Vector((outward.x,outward.y,.18+rng.uniform(-.08,.12))).normalized()
            else:
                facing=(outward*.82+Vector((rng.uniform(-.25,.25),rng.uniform(-.25,.25),.22))).normalized()
            side=Vector((0,0,1)).cross(facing)
            if side.length<.05:
                side=Vector((1,0,0))
            side.normalize()
            up=facing.cross(side).normalized()
            angle=rng.uniform(-math.pi,math.pi) if species=='oak' else rng.uniform(-.45,.45)
            right=side*math.cos(angle)+up*math.sin(angle)
            vertical=-side*math.sin(angle)+up*math.cos(angle)
            width=rng.uniform(.67,1.27) if species=='oak' else rng.uniform(.52,.88)
            height_m=width*rng.uniform(.75,1.1) if species=='oak' else width*rng.uniform(.85,1.23)
            first=len(vertices)
            variation=rng.uniform(.12,.94)
            occlusion=max(.65,min(1,.69+.27*radial+.07*direction.z))
            for u,v in grid:
                bend=(1-(2*u-1)**2)*.07*width+(v-.5)*.025
                local=right*((u-.5)*width)+vertical*((v-.5)*height_m)+facing*bend
                point=position+local
                vertices.append(point)
                uvs.append((u,v))
                world_delta=point-centre
                delta=Vector((world_delta.x*cos_b+world_delta.y*sin_b,-world_delta.x*sin_b+world_delta.y*cos_b,world_delta.z))
                proxy=rotate(Vector((delta.x/(radii.x*radii.x),delta.y/(radii.y*radii.y),delta.z/(radii.z*radii.z))))
                proxy=(proxy.normalized()*.86+Vector((0,0,.14))).normalized()
                normals.append(proxy)
                colors.append((occlusion,max(0,min(1,point.z/7)),variation,1))
            for row in range(2):
                for column in range(2):
                    a=first+row*3+column
                    # Cross(right,vertical) points toward facing.
                    faces.append((a,a+1,a+4,a+3))
    mesh=bpy.data.meshes.new('Painted cluster cards with proxy normals')
    mesh.from_pydata(vertices,[],faces)
    mesh.update()
    uv=mesh.uv_layers.new(name='LeafClusterUV')
    for loop in mesh.loops:
        uv.data[loop.index].uv=uvs[loop.vertex_index]
    for poly in mesh.polygons:
        poly.use_smooth=True
    mesh.normals_split_custom_set_from_vertices(normals)
    data=mesh.color_attributes.new(name='FoliageControls',type='FLOAT_COLOR',domain='POINT')
    for item,color in zip(data.data,colors):
        item.color=color
    mesh.color_attributes.active_color=data
    obj=bpy.data.objects.new('Leaves',mesh)
    collection.objects.link(obj)
    material,image_path=leaf_material(species)
    obj.data.materials.append(material)
    obj['vertex_channels']='R=authored proxy occlusion, G=height wind, B=variation, A=1'
    obj['occlusion_note']='Authored canopy radial/height gradient; not ray-traced AO.'
    obj['card_count']=len(vertices)//9
    return obj,image_path


def juniper_architecture():
    # Irregular ascending fans with visible stem intervals. These deliberately
    # avoid rotationally regular rings, a perfect cone and hanging lower lobes.
    branches=[
        [(0,0,0,.23),(.12,-.02,.75,.205),(-.12,.05,1.5,.18),(-.20,.12,2.35,.15),(-.05,.08,3.25,.11),(-.35,.1,4.15,.075),(-.42,.05,5.08,.044),(-.30,.12,6.08,.009)],
        [(-.12,.05,1.5,.125),(.36,.1,2.03,.105),(.63,.02,2.78,.075),(.86,.2,3.48,.042),(.62,.43,4.23,.009)],
    ]
    fan_sites=[
        ((-1.25,-.18,2.42),(.85,.49,.31),36),
        ((.42,1.16,2.91),(.79,.57,.34),34),
        ((1.33,-.58,3.36),(.86,.49,.39),40),
        ((-1.06,.77,3.88),(.82,.53,.38),38),
        ((-.55,-.93,4.18),(.70,.51,.32),36),
        ((.70,.61,4.76),(.76,.55,.34),40),
        ((-.39,.06,5.83),(.50,.45,.57),55),
        ((-.83,.39,5.15),(.68,.46,.32),33),
        ((1.32,.39,3.94),(.67,.47,.29),34),
    ]
    crowns=[]
    for index,(centre_values,extent,count) in enumerate(fan_sites):
        centre=Vector(centre_values)
        stem_height=centre.z-.85
        stem=branches[1] if index in [2,5,8] else branches[0]
        attachment=min(catmull_samples(stem,12),key=lambda point:abs(point.z-stem_height))
        start=Vector(attachment[:3])
        middle=start.lerp(centre,.56)+Vector((.04,-.04,-.17))
        tip=centre+Vector((centre.x*.10,centre.y*.10,.38))
        radius=.069 if centre.z<3.8 else .042
        branches.append([(*start,radius),(*middle,radius*.72),(*centre,.024),(*tip,.008)])
        bearing=math.atan2(centre.y-start.y,centre.x-start.x)
        crowns.append((centre_values,extent,count,bearing))
        # A smaller secondary fan follows the connecting branch, avoiding the
        # isolated teardrop ornament shape of the previous terminal-only groups.
        bridge=start.lerp(centre,.53)+Vector((0,0,.16))
        crowns.append((tuple(bridge),(.59,.35,.23),18,bearing+.17))
    return branches,crowns


def patch_glb_mask(path):
    # glTF exporter alpha-mode inference differs between Blender releases. State
    # the real-time contract explicitly while keeping binary accessors untouched.
    raw=path.read_bytes()
    magic,version,length=struct.unpack_from('<III',raw)
    json_length,json_type=struct.unpack_from('<II',raw,12)
    document=json.loads(raw[20:20+json_length])
    for material in document.get('materials',[]):
        if material.get('name','').startswith('Foliage '):
            material['alphaMode']='MASK'
            material['alphaCutoff']=.42
            material['doubleSided']=True
    encoded=json.dumps(document,separators=(',',':')).encode('utf8')
    encoded+=b' '*((-len(encoded))%4)
    remainder=raw[20+json_length:]
    rebuilt=struct.pack('<III',magic,version,20+len(encoded)+len(remainder))+struct.pack('<II',len(encoded),json_type)+encoded+remainder
    path.write_bytes(rebuilt)


def build(species):
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
    for collection in list(bpy.data.collections):
        if collection.users==0:
            bpy.data.collections.remove(collection)
    rendered=make_collection('Exported tree')
    guides=make_collection('Editable branch and canopy guides - do not export')
    guides.hide_render=True
    branches,crowns=oak_architecture() if species=='oak' else juniper_architecture()
    trunk=make_trunks(branches,rendered,guides)
    leaves,image_path=make_leaves(species,crowns,rendered,guides)
    scene=bpy.context.scene
    scene.unit_settings.system='METRIC'
    scene.unit_settings.scale_length=1
    scene['asset_id']='painterly_'+('live_oak' if species=='oak' else 'ashe_juniper')+'_v1'
    scene['style']='Grounded painterly 3D; original reference-guided asset proof.'
    scene['source_map_claim']='Illustrative species morphology; not a scanned/surveyed individual.'
    bpy.context.view_layer.update()
    points=[obj.matrix_world@Vector(point) for obj in [trunk,leaves] for point in obj.bound_box]
    minimum=[min(point[axis] for point in points) for axis in range(3)]
    maximum=[max(point[axis] for point in points) for axis in range(3)]
    triangles={obj.name:sum(len(poly.vertices)-2 for poly in obj.data.polygons) for obj in [trunk,leaves]}
    name='live-oak-v1' if species=='oak' else 'ashe-juniper-v1'
    SOURCE_DIR.mkdir(parents=True,exist_ok=True)
    GAME_DIR.mkdir(parents=True,exist_ok=True)
    # Store the generator itself in the editable Blender file for provenance.
    text=bpy.data.texts.get('build_painterly_trees.py') or bpy.data.texts.new('build_painterly_trees.py')
    text.clear()
    text.write(Path(__file__).read_text(encoding='utf8'))
    for image in bpy.data.images:
        if image.source=='FILE' and image.packed_file is None:
            image.pack()
    guides.hide_viewport=True
    bpy.context.preferences.filepaths.save_version=0
    bpy.ops.wm.save_as_mainfile(filepath=str(SOURCE_DIR/(name+'.blend')))
    # The source retains packed originals for editable Blender previews. Runtime
    # imports use one shared texture set supplied by the Godot material family.
    for obj in [trunk,leaves]:
        for material in obj.data.materials:
            if material and material.use_nodes:
                for node in list(material.node_tree.nodes):
                    if node.type=='TEX_IMAGE':
                        material.node_tree.nodes.remove(node)
                material.node_tree.nodes.get('Principled BSDF').inputs['Base Color'].default_value=(1,1,1,1)
    bpy.ops.object.select_all(action='DESELECT')
    for obj in [trunk,leaves]:
        obj.select_set(True)
    bpy.context.view_layer.objects.active=trunk
    glb=GAME_DIR/(name+'.glb')
    bpy.ops.export_scene.gltf(filepath=str(glb),export_format='GLB',use_selection=True,export_yup=True,export_normals=True,export_texcoords=True,export_materials='EXPORT',export_vertex_color='ACTIVE',export_extras=True)
    patch_glb_mask(glb)
    metadata={
        'schema':'garage_game.art.asset.v1','id':scene['asset_id'],'species':species,
        'interpretation':'Original illustrative tree; no surveyed individual claim.',
        'builder':'tools/art/build_painterly_trees.py','builder_sha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        'seed':SEED,'blender_version':bpy.app.version_string,
        'source':str((SOURCE_DIR/(name+'.blend')).relative_to(ROOT)).replace('\\','/'),
        'export':str(glb.relative_to(ROOT)).replace('\\','/'),
        'leaf_texture':str(image_path.relative_to(ROOT)).replace('\\','/'),
        'leaf_texture_sha256':hashlib.sha256(image_path.read_bytes()).hexdigest(),
        'bounds_blender_z_up_m':{'minimum':minimum,'maximum':maximum,'dimensions':[maximum[i]-minimum[i] for i in range(3)]},
        'triangles':triangles,'total_triangles':sum(triangles.values()),'leaf_card_count':leaves['card_count'],
        'branch_curve_count':len(branches),'canopy_guide_count':len(crowns),
        'closed_canopy_shells_exported':0,
        'normal_method':'Analytic ellipsoidal canopy-proxy normals with restrained upward blend; fixed 3D cards.',
        'vertex_channels':{'R':'Authored radial canopy interior occlusion 0.65–1.0; not ray-traced AO','G':'Height-based wind weight','B':'Stable per-card variation','A':'1'},
        'foliage_material':'Foliage '+species,'foliage_alpha_mode':'MASK','alpha_cutoff':.42,
        'rights':'Tree geometry and builder originally authored for EnFractal. Texture provenance maintained separately by root agent.',
        'limitations':['Asset proof; no accepted visual quality score','No collision mesh included; root gameplay system supplies simplified collision','No automatic LODs yet','Source includes editable curves/empty guides; rerun builder to regenerate meshes after editing script recipe'],
    }
    (GAME_DIR/(name+'.asset.json')).write_text(json.dumps(metadata,indent=2)+'\n',encoding='utf8')
    print('PAINTERLY_TREE '+json.dumps(metadata))


if __name__=='__main__':
    argv=sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else []
    parser=argparse.ArgumentParser()
    parser.add_argument('--species',choices=['all','oak','juniper'],default='all')
    args=parser.parse_args(argv)
    for selected in (['oak','juniper'] if args.species=='all' else [args.species]):
        build(selected)
