"""Background Blender implementation. Never imported by ordinary Python."""
import json
import math
from pathlib import Path
import sys
import time

config=json.loads(Path(sys.argv[sys.argv.index('--')+1]).read_bytes())
sys.path.insert(0,config['repo'])
import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from pipeline.landscape.harness.common import PALETTE, canonical, sha, harness_revision
from pipeline.landscape.harness.kit import SIZES, prototype, ellipsoid, cone
from pipeline.landscape.harness.package import read_package
from pipeline.landscape.harness.png import strip, sheet, decode, encode, text
from pipeline.landscape.harness.views import camera_plan, sun_position, ground_height

SET=config['settings']
OUT=Path(config['out'])
MATERIALS={}
HAZE=(.57,.69,.78)
FIGURE=[]


def xyz(v): return (v[0],-v[2],v[1])


def node(tree,kind): return tree.nodes.new(kind)


def math_node(tree,operation,first,second=None):
    n=node(tree,'ShaderNodeMath'); n.operation=operation
    for index,value in enumerate([first,second]):
        if value is None: continue
        if isinstance(value,(float,int)): n.inputs[index].default_value=value
        else: tree.links.new(value,n.inputs[index])
    return n.outputs[0]


def mix_colour(tree,factor,a,b):
    n=node(tree,'ShaderNodeMixRGB'); n.blend_type='MIX'
    for i,v in enumerate([factor,a,b]):
        if isinstance(v,(int,float)): n.inputs[i].default_value=v
        elif isinstance(v,tuple): n.inputs[i].default_value=(*v,1)
        else: tree.links.new(v,n.inputs[i])
    return n.outputs[0]


def role_colour(tree,role,coords):
    ground=role in ('meadow','soil','worn_path','gravel','scree','moss','snow')
    noise=node(tree,'ShaderNodeTexNoise'); noise.inputs['Scale'].default_value=.65 if ground else 2.2
    noise.inputs['Detail'].default_value=1; noise.inputs['Roughness'].default_value=.45
    tree.links.new(coords,noise.inputs['Vector'])
    stretch=node(tree,'ShaderNodeVectorMath'); stretch.operation='MULTIPLY'
    tree.links.new(coords,stretch.inputs[0]); stretch.inputs[1].default_value=(1.5,.45,4)
    strokes=node(tree,'ShaderNodeTexNoise'); strokes.inputs['Scale'].default_value=8
    strokes.inputs['Detail'].default_value=1; tree.links.new(stretch.outputs[0],strokes.inputs['Vector'])
    camera=node(tree,'ShaderNodeCameraData')
    close=math_node(tree,'MAXIMUM',math_node(tree,'SUBTRACT',1,
                    math_node(tree,'DIVIDE',camera.outputs['View Distance'],2)),0)
    fine_weight=math_node(tree,'MULTIPLY',close,.10 if ground else .18)
    factor=math_node(tree,'ADD',noise.outputs['Fac'],math_node(tree,'MULTIPLY',
                     math_node(tree,'SUBTRACT',strokes.outputs['Fac'],.5),fine_weight))
    if role in ('rock','cliff','scree'):
        # Warped HEIGHT, rather than evenly repeating bands. Smooth face-angle
        # weighting removes courses from tops. Cliff cuts carry most strata;
        # rock/scree (including loose kit boulders) carry only a subtle hint.
        sep=node(tree,'ShaderNodeSeparateXYZ'); tree.links.new(coords,sep.inputs[0])
        warp=node(tree,'ShaderNodeTexNoise'); warp.inputs['Scale'].default_value=1.8
        warp.inputs['Detail'].default_value=1; tree.links.new(coords,warp.inputs['Vector'])
        height=math_node(tree,'ADD',sep.outputs['Z'],math_node(tree,'MULTIPLY',
                           math_node(tree,'SUBTRACT',warp.outputs['Fac'],.5),.20))
        phase=math_node(tree,'ADD',math_node(tree,'MULTIPLY',height,24),
                          math_node(tree,'MULTIPLY',noise.outputs['Fac'],5))
        band=math_node(tree,'SINE',phase)
        geometry=node(tree,'ShaderNodeNewGeometry'); normal=node(tree,'ShaderNodeSeparateXYZ')
        tree.links.new(geometry.outputs['Normal'],normal.inputs[0])
        steep=math_node(tree,'MINIMUM',math_node(tree,'MAXIMUM',math_node(tree,'MULTIPLY',
                    math_node(tree,'SUBTRACT',.70,math_node(tree,'ABSOLUTE',normal.outputs['Z'])),2),0),1)
        weight=math_node(tree,'MULTIPLY',steep,.18 if role=='cliff' else .035)
        factor=math_node(tree,'ADD',factor,math_node(tree,'MULTIPLY',band,weight))
    elif role in ('bark','wood','timber','cloth','stone_masonry','roof','flowing_water'):
        bands=node(tree,'ShaderNodeTexWave'); bands.wave_type='BANDS'
        bands.bands_direction='Z' if role=='stone_masonry' else 'X'
        bands.inputs['Scale'].default_value=25
        bands.inputs['Distortion'].default_value=2; bands.inputs['Detail'].default_value=1
        tree.links.new(coords,bands.inputs['Vector'])
        factor=math_node(tree,'ADD',math_node(tree,'MULTIPLY',factor,.8),math_node(tree,'MULTIPLY',bands.outputs['Fac'],.2))
    base=PALETTE[role]
    ramp=node(tree,'ShaderNodeValToRGB'); ramp.color_ramp.interpolation='EASE'
    colours=[tuple(max(0,c*.82+offset) for c,offset in zip(base,(0,.005,.015))),
             base,
             tuple(min(1,c*1.12+offset) for c,offset in zip(base,(.035,.022,.008)))]
    low,high=ramp.color_ramp.elements[:]
    for element,position,colour in zip([low,ramp.color_ramp.elements.new(.50),high], [.20,.50,.80],colours):
        element.position=position; element.color=(*colour,1)
    tree.links.new(factor,ramp.inputs[0])
    return ramp.outputs[0]


def horizontal_depth(tree):
    geometry=node(tree,'ShaderNodeNewGeometry')
    camera=node(tree,'ShaderNodeCombineXYZ'); camera.name='haze_camera_origin'
    difference=node(tree,'ShaderNodeVectorMath'); difference.operation='SUBTRACT'
    tree.links.new(geometry.outputs['Position'],difference.inputs[0]); tree.links.new(camera.outputs[0],difference.inputs[1])
    separate=node(tree,'ShaderNodeSeparateXYZ'); tree.links.new(difference.outputs[0],separate.inputs[0])
    return math_node(tree,'SQRT',math_node(tree,'ADD',
           math_node(tree,'MULTIPLY',separate.outputs['X'],separate.outputs['X']),
           math_node(tree,'MULTIPLY',separate.outputs['Y'],separate.outputs['Y'])))


def haze_factor(tree):
    distance=horizontal_depth(tree)
    near=math_node(tree,'MAXIMUM',math_node(tree,'SUBTRACT',distance,SET['haze_start_m']),0)
    far=math_node(tree,'MAXIMUM',math_node(tree,'SUBTRACT',distance,SET['far_haze_start_m']),0)
    optical=math_node(tree,'ADD',math_node(tree,'MULTIPLY',near,SET['haze_density']),
                       math_node(tree,'MULTIPLY',far,SET['far_haze_density']))
    return math_node(tree,'SUBTRACT',1,math_node(tree,'EXPONENT',math_node(tree,'MULTIPLY',optical,-1)))


def material(role,blend=None):
    key=(role,blend)
    if key in MATERIALS: return MATERIALS[key]
    m=bpy.data.materials.new('shared_'+role+('_'+blend if blend else '')); m.use_nodes=True
    tree=m.node_tree; tree.nodes.clear()
    coord=node(tree,'ShaderNodeTexCoord')
    colour=role_colour(tree,role,coord.outputs['Object'])
    if blend:
        attr=node(tree,'ShaderNodeAttribute'); attr.attribute_name='role_weight'
        colour=mix_colour(tree,attr.outputs['Fac'],colour,role_colour(tree,blend,coord.outputs['Object']))
    for name in ['tint','instance_tint']:
        attr=node(tree,'ShaderNodeVertexColor'); attr.layer_name=name
        tint=node(tree,'ShaderNodeMixRGB'); tint.blend_type='MULTIPLY'; tint.inputs[0].default_value=1
        tree.links.new(colour,tint.inputs[1]); tree.links.new(attr.outputs['Color'],tint.inputs[2]); colour=tint.outputs[0]
    bsdf=node(tree,'ShaderNodeBsdfPrincipled'); tree.links.new(colour,bsdf.inputs['Base Color'])
    bsdf.inputs['Roughness'].default_value=.26 if 'water' in role else .48 if role=='metal' else .86
    bsdf.inputs['Metallic'].default_value=.35 if role=='metal' else 0
    bsdf.inputs['Sheen Weight'].default_value=.12 if role in ('cloth','foliage','meadow') else 0
    # Mild stroke normals, shared in strength; no photographic texture maps.
    mark=node(tree,'ShaderNodeTexNoise'); mark.inputs['Scale'].default_value=35; mark.inputs['Detail'].default_value=1
    tree.links.new(coord.outputs['Object'],mark.inputs['Vector'])
    bump=node(tree,'ShaderNodeBump'); bump.inputs['Distance'].default_value=.003
    detail_camera=node(tree,'ShaderNodeCameraData')
    detail=math_node(tree,'MULTIPLY',math_node(tree,'MAXIMUM',math_node(tree,'SUBTRACT',1,
                        math_node(tree,'DIVIDE',detail_camera.outputs['View Distance'],2)),0),.10)
    tree.links.new(detail,bump.inputs['Strength'])
    tree.links.new(mark.outputs['Fac'],bump.inputs['Height']); tree.links.new(bump.outputs['Normal'],bsdf.inputs['Normal'])
    # Horizontal aerial perspective for eyes; overhead height is not distance
    # through the landscape. Overview haze is disabled on reachable land by
    # the per-view density, while far ground/scenery keep their boundary haze.
    fog=haze_factor(tree)
    emission=node(tree,'ShaderNodeEmission'); emission.inputs['Color'].default_value=(*HAZE,1)
    mix=node(tree,'ShaderNodeMixShader'); tree.links.new(fog,mix.inputs[0]); tree.links.new(bsdf.outputs[0],mix.inputs[1]); tree.links.new(emission.outputs[0],mix.inputs[2])
    # Overview cutaway: land outside the room that would hide the room's floor
    # from this overview is invisible to camera rays only (it still casts
    # shadows and bounces light). Eye views name no attribute, so nothing cuts.
    cut=node(tree,'ShaderNodeAttribute'); cut.name='overview_cutaway'; cut.attribute_name='no_cutaway'
    path=node(tree,'ShaderNodeLightPath'); clear=node(tree,'ShaderNodeBsdfTransparent')
    hide=node(tree,'ShaderNodeMixShader'); amount=cut.outputs['Fac']
    if role in GROUND_ROLES:
        # Through a cut, a camera ray may meet land from inside the solid
        # (a back face); in an overview that is cut away as well.
        flag=node(tree,'ShaderNodeValue'); flag.name='overview_flag'; flag.outputs[0].default_value=0
        back=node(tree,'ShaderNodeNewGeometry')
        amount=math_node(tree,'MAXIMUM',amount,math_node(tree,'MULTIPLY',back.outputs['Backfacing'],flag.outputs[0]))
    tree.links.new(math_node(tree,'MULTIPLY',amount,path.outputs['Is Camera Ray']),hide.inputs[0])
    tree.links.new(mix.outputs[0],hide.inputs[1]); tree.links.new(clear.outputs[0],hide.inputs[2])
    output=node(tree,'ShaderNodeOutputMaterial'); tree.links.new(hide.outputs[0],output.inputs['Surface'])
    MATERIALS[key]=m
    return m


CREASE_DEG=60.
GROUND_ROLES=('meadow','soil','worn_path','gravel','scree','rock','cliff','moss','snow')
CUT_INSET=.9


def soft_normals(primitives,crease_deg=CREASE_DEG):
    """Per-corner normals for one land, water or scenery record.

    Faces that touch the same vertex position are averaged (area-weighted)
    across every primitive of the record, so the per-role primitive split
    the format imposes leaves no seam. Two faces meeting at more than the
    crease angle keep a hard edge, and vertices a primitive deliberately
    splits (same position, different index) never smooth with each other.
    Returns one list of Blender-space corner normals per primitive.
    """
    limit=math.cos(math.radians(crease_deg))
    faces=[]; around={}
    for pi,p in enumerate(primitives):
        vs=p['positions']
        for ti,t in enumerate(p['triangles']):
            a,b,c=[vs[k] for k in t]
            ux,uy,uz=b[0]-a[0],b[1]-a[1],b[2]-a[2]
            vx,vy,vz=c[0]-a[0],c[1]-a[1],c[2]-a[2]
            n=(uy*vz-uz*vy,uz*vx-ux*vz,ux*vy-uy*vx)
            length=math.sqrt(n[0]*n[0]+n[1]*n[1]+n[2]*n[2]) or 1e-30
            faces.append((n,(n[0]/length,n[1]/length,n[2]/length),pi,t))
            f=len(faces)-1
            for k in t:
                around.setdefault(tuple(vs[k]),[]).append((f,pi,k))
    out=[[] for _ in primitives]
    for f,(n,u,pi,t) in enumerate(faces):
        vs=primitives[pi]['positions']
        for k in t:
            sx=sy=sz=0.
            for g,pj,kj in around[tuple(vs[k])]:
                if pj==pi and kj!=k: continue
                m,w=faces[g][0],faces[g][1]
                if g!=f and u[0]*w[0]+u[1]*w[1]+u[2]*w[2]<limit: continue
                sx+=m[0]; sy+=m[1]; sz+=m[2]
            length=math.sqrt(sx*sx+sy*sy+sz*sz) or 1e-30
            out[pi].append(xyz((sx/length,sy/length,sz/length)))
    return out


def add_mesh(name,primitives,position=(0,0,0),yaw=0,scale=(1,1,1),tint=(1,1,1),smooth=False,bevel=False,soft=False):
    objects=[]
    corners=soft_normals(primitives) if soft else None
    for index,p in enumerate(primitives):
        data=bpy.data.meshes.new(name+'_'+str(index)); data.from_pydata([xyz(v) for v in p['positions']],[],p['triangles']); data.update()
        obj=bpy.data.objects.new(data.name,data); bpy.context.collection.objects.link(obj)
        obj.location=xyz(position); obj.rotation_euler.z=math.radians(yaw)
        obj.scale=(scale[0],scale[2],scale[1])
        for attr_name,values in [('tint',p.get('tints',[[1,1,1,1]]*len(p['positions']))),
                                 ('instance_tint',[list(tint)+[1]]*len(p['positions']))]:
            attr=data.color_attributes.new(name=attr_name,type='FLOAT_COLOR',domain='POINT')
            for item,value in zip(attr.data,values): item.color=value
        weight=data.attributes.new(name='role_weight',type='FLOAT',domain='POINT')
        for item,value in zip(weight.data,p.get('blend_weights',[0]*len(p['positions']))): item.value=value
        data.materials.append(material(p['role'],p.get('blend_role')))
        for polygon in data.polygons: polygon.use_smooth=smooth or soft
        if soft: data.normals_split_custom_set(corners[index])
        if bevel:
            modifier=obj.modifiers.new('kit_soft_edges','BEVEL'); modifier.width=.007; modifier.segments=2
        objects.append(obj)
    return objects


def make_world(setup,sun):
    global HAZE
    elevation=sun['altitude_deg']; day=max(.03,min(1,(elevation+6)/25))
    season=setup['season']; seasonal={'summer':1,'spring':.9,'autumn':.85,'winter':.7}[season]
    horizon=(.48,.70,1.0) if elevation>15 else (.69,.45,.28) if elevation>-6 else (.035,.055,.10)
    zenith=(.07,.26,.70) if elevation>15 else (.26,.34,.48) if elevation>-6 else (.009,.02,.055)
    if season=='winter': horizon=tuple(c*.9+.06 for c in horizon); zenith=tuple(c*.8+.08 for c in zenith)
    HAZE=horizon
    world=bpy.context.scene.world; world.use_nodes=True; tree=world.node_tree; tree.nodes.clear()
    coords=node(tree,'ShaderNodeTexCoord'); sep=node(tree,'ShaderNodeSeparateXYZ'); tree.links.new(coords.outputs['Normal'],sep.inputs[0])
    # World Normal points back towards the viewer; invert its vertical sign.
    altitude=math_node(tree,'MULTIPLY',sep.outputs['Z'],-1)
    gradient=math_node(tree,'MINIMUM',math_node(tree,'MULTIPLY',math_node(tree,'MAXIMUM',altitude,0),1.8),1)
    colour=mix_colour(tree,gradient,horizon,zenith)
    clouds=node(tree,'ShaderNodeTexNoise'); clouds.inputs['Scale'].default_value=4; clouds.inputs['Detail'].default_value=1
    tree.links.new(coords.outputs['Normal'],clouds.inputs['Vector'])
    cloudfac=math_node(tree,'MULTIPLY',math_node(tree,'MAXIMUM',math_node(tree,'SUBTRACT',clouds.outputs['Fac'],.58),0),.8)
    colour=mix_colour(tree,cloudfac,colour,tuple(min(1,c+.17) for c in horizon))
    below=math_node(tree,'LESS_THAN',altitude,0)
    lower=math_node(tree,'MINIMUM',math_node(tree,'MULTIPLY',math_node(tree,'MAXIMUM',math_node(tree,'MULTIPLY',altitude,-1),0),5),1)
    colour=mix_colour(tree,below,colour,mix_colour(tree,lower,horizon,(.24,.32,.19)))
    # Soft sun disk and halo in the same procedural sky used for fill.
    dot=node(tree,'ShaderNodeVectorMath'); dot.operation='DOT_PRODUCT'
    tree.links.new(coords.outputs['Normal'],dot.inputs[0]); dot.inputs[1].default_value=tuple(-v for v in xyz(sun['direction_room']))
    halo=math_node(tree,'POWER',math_node(tree,'MAXIMUM',dot.outputs['Value'],0),1000)
    colour=mix_colour(tree,halo,colour,(1.,.82,.55))
    bg=node(tree,'ShaderNodeBackground'); tree.links.new(colour,bg.inputs['Color']); bg.inputs['Strength'].default_value=SET['sky_strength']*day*seasonal
    output=node(tree,'ShaderNodeOutputWorld'); tree.links.new(bg.outputs[0],output.inputs[0])
    light=bpy.data.lights.new('solar_key','SUN'); light.energy=SET['sun_energy']*max(0,min(1,elevation/15))*seasonal
    light.color=(1,.90,.74) if elevation>15 else (1,.66,.35); light.angle=math.radians(SET['sun_angle_deg'] if season!='winter' else 4)
    obj=bpy.data.objects.new('solar_key',light); bpy.context.collection.objects.link(obj)
    obj.rotation_euler=(-Vector(xyz(sun['direction_room']))).to_track_quat('-Z','Y').to_euler()


def far_ground(room):
    lo,hi=room['bounds']['min_m'],room['bounds']['max_m']; y=lo[1]-.025
    x0,x1,z0,z1=lo[0],hi[0],lo[2],hi[2]; reach=1000.
    from pipeline.landscape.harness.build_reference import quad
    return add_mesh('shared_far_ground',[
        quad(-reach,x0,-reach,reach,y,'moss'),quad(x1,reach,-reach,reach,y,'moss'),
        quad(x0,x1,-reach,z0,y,'moss'),quad(x0,x1,z1,reach,y,'moss')])


def cutaway(objects,plan,room):
    """Per overview, mark land and scenery faces outside the room's bounds that
    stand between the camera and the room's floor (the camera's ray through
    the face would land on the floor inside the bounds). Only the instrument
    changes: the package's land is untouched, and eye views never cut."""
    lo,hi=room['bounds']['min_m'],room['bounds']['max_m']; floor=lo[1]
    # Only what hides the room's interior is cut: rays landing within the
    # walls' own inner foothills (CUT_INSET) leave the land in place.
    a,b=lo[0]+CUT_INSET,hi[0]-CUT_INSET; c,d=lo[2]+CUT_INSET,hi[2]-CUT_INSET
    counts={}
    for view in plan:
        if view['kind']!='overview': continue
        cx,cy,cz=view['position_m']; total=0
        for obj in objects:
            data=obj.data
            attr=data.attributes.new(name='cut_'+view['name'],type='FLOAT',domain='FACE')
            for face,item in zip(data.polygons,attr.data):
                X,Y,Z=face.center; x,y,z=X,Z,-Y
                cut=0.
                if not (lo[0]<=x<=hi[0] and lo[2]<=z<=hi[2]) and floor+.005<y<cy:
                    t=(cy-floor)/(cy-y); hx=cx+(x-cx)*t; hz=cz+(z-cz)*t
                    if a<hx<b and c<hz<d: cut=1.
                item.value=cut; total+=cut>0
        counts[view['name']]=total
    return counts


def geometry_bvh(objects):
    vertices=[]; triangles=[]; solids=[]
    deps=bpy.context.evaluated_depsgraph_get()
    for obj in objects:
        evaluated=obj.evaluated_get(deps); data=evaluated.to_mesh(); data.calc_loop_triangles(); base=len(vertices)
        points=[evaluated.matrix_world@v.co for v in data.vertices]
        faces=[tuple(t.vertices) for t in data.loop_triangles]
        vertices += points; triangles += [tuple(base+i for i in t) for t in faces]
        edges={}
        for t in faces:
            for a,b in zip(t,t[1:]+t[:1]):
                key=tuple(sorted((a,b))); edges[key]=edges.get(key,0)+1
        # Open terrain cannot establish enclosure. Test each closed component
        # independently so overlapping solids do not cancel via XOR parity.
        if edges and all(n==2 for n in edges.values()):
            lo=Vector(tuple(min(p[i] for p in points) for i in range(3)))
            hi=Vector(tuple(max(p[i] for p in points) for i in range(3)))
            solids.append((lo,hi,BVHTree.FromPolygons(points,faces,all_triangles=True)))
        evaluated.to_mesh_clear()
    return (BVHTree.FromPolygons(vertices,triangles,all_triangles=True),solids) if triangles else None


def eye_blocked(bvh,location):
    if bvh is None: return False
    aggregate,solids=bvh
    near=aggregate.find_nearest(location)
    if near[0] is not None and near[3]<.02: return True
    direction=Vector((.913,.277,.297)).normalized()
    for lo,hi,solid in solids:
        if not all(lo[i]<=location[i]<=hi[i] for i in range(3)): continue
        origin=location.copy(); hits=0
        for _ in range(256):
            point,normal,index,distance=solid.ray_cast(origin,direction,1000)
            if point is None: break
            hits+=1; origin=point+direction*.00001
        if hits%2: return True
    return False


def render_view(view,camera,diagnostic=False):
    scene=bpy.context.scene
    camera.location=xyz(view['position_m']); target=Vector(xyz(view['target_m']))
    camera.rotation_euler=(target-camera.location).to_track_quat('-Z','Y').to_euler()
    camera.data.type='PERSP'
    camera.data.shift_x=view.get('shift_x',0); camera.data.shift_y=view.get('shift_y',0)
    origin=xyz(view['target_m'] if view['kind']=='overview' else view['position_m'])
    for m in MATERIALS.values():
        for n in m.node_tree.nodes:
            if n.name=='haze_camera_origin':
                for i,v in enumerate(origin): n.inputs[i].default_value=v
            if n.name=='overview_cutaway':
                n.attribute_name='cut_'+view['name'] if view['kind']=='overview' and not diagnostic else 'no_cutaway'
            if n.name=='overview_flag':
                n.outputs[0].default_value=1. if view['kind']=='overview' and not diagnostic else 0.

    camera.data.lens=SET['overview_lens_mm'] if view['kind']=='overview' else SET['eye_lens_mm']
    camera.data.dof.use_dof=not diagnostic
    camera.data.dof.focus_distance=(camera.location-target).length if view['kind']=='overview' else SET['eye_focus_m']
    camera.data.dof.aperture_fstop=SET['overview_fstop'] if view['kind']=='overview' else SET['eye_fstop']
    for obj in FIGURE:
        obj.location=xyz(view['figure_position_m'])
    name='slope' if diagnostic else view['name']; scene.render.filepath=str(OUT/(name+'.png'))
    assert bpy.app.background and scene.cycles.device=='CPU' and not scene.cycles.denoising_use_gpu
    start=time.perf_counter(); bpy.ops.render.render(write_still=True); elapsed=time.perf_counter()-start
    raw_bytes=(OUT/(name+'.png')).stat().st_size; strip(OUT/(name+'.png'))
    final_bytes=(OUT/(name+'.png')).stat().st_size
    if final_bytes>2_000_000: raise ValueError('render binary exceeds brief 2 MB limit')
    print(f'HARNESS_RENDER view={name} seconds={elapsed:.3f} bytes={final_bytes}',flush=True)
    return dict(view,path=name+'.png',seconds=round(elapsed,6),raw_bytes=raw_bytes,bytes=final_bytes,
                rendered_harness_sha256=config['harness_revision']['sha256'],
                sha256=sha((OUT/(name+'.png')).read_bytes()),focus_distance_m=camera.data.dof.focus_distance,
                fstop=camera.data.dof.aperture_fstop,projection=camera.data.type,lens_mm=camera.data.lens)


def kit_picture(camera):
    """A separate catalogue, excluded from the blind sheet and package."""
    from bpy_extras.object_utils import world_to_camera_view
    for obj in list(bpy.context.scene.objects):
        if obj.type=='MESH': bpy.data.objects.remove(obj,do_unlink=True)
    from pipeline.landscape.harness.build_reference import quad
    add_mesh('catalogue_ground',[quad(-1000,1000,-1000,1000,-.025,'moss')])
    positions=[]
    for i,name in enumerate(SIZES):
        position=[(i%5-2)*1.35,0,(i//5-1)*1.5]
        positions.append((name,position))
        add_mesh('catalogue_'+name,prototype(name),position,smooth=name not in ('cottage','fence','crate','lantern'),
                 bevel=name in ('cottage','fence','crate','lantern'))
    camera.location=xyz([0,7,-8]); target=Vector(xyz([0,.2,0]))
    camera.rotation_euler=(target-camera.location).to_track_quat('-Z','Y').to_euler()
    camera.data.lens=40; camera.data.shift_x=0; camera.data.shift_y=0; camera.data.dof.use_dof=False
    scene=bpy.context.scene; scene.render.filepath=str(OUT/'kit.png')
    for m in MATERIALS.values():
        for n in m.node_tree.nodes:
            if n.name=='haze_camera_origin':
                for i in range(3): n.inputs[i].default_value=0
    bpy.context.view_layer.update()
    start=time.perf_counter()
    bpy.ops.render.render(write_still=True)
    seconds=round(time.perf_counter()-start,6)
    w,h,p=decode((OUT/'kit.png').read_bytes())
    for name,position in positions:
        label=world_to_camera_view(scene,camera,Vector(xyz([position[0],0,position[2]-.25])))
        text(p,w,h,round(label.x*w)-len(name)*3,round((1-label.y)*h),name,1)
    data=encode(w,h,p)
    if len(data)>2_000_000: raise ValueError('kit picture exceeds 2 MB')
    (OUT/'kit.png').write_bytes(data)
    print('HARNESS_KIT seconds='+str(seconds)+' bytes='+str(len(data)),flush=True)
    return dict(path='kit.png',seconds=seconds,bytes=len(data),sha256=sha(data),
                rendered_harness_sha256=config['harness_revision']['sha256'])


def main():
    global FIGURE
    if not bpy.app.background or tuple(bpy.app.version)!=(5,2,2):
        raise RuntimeError('requires Blender 5.2.2 background')
    bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)
    scene=bpy.context.scene; scene.render.engine='CYCLES'; scene.cycles.device='CPU'
    scene.cycles.samples=SET['samples']; scene.cycles.seed=SET['seed']; scene.cycles.use_animated_seed=False
    scene.cycles.use_adaptive_sampling=False; scene.cycles.use_denoising=True
    scene.cycles.denoiser='OPENIMAGEDENOISE'; scene.cycles.denoising_use_gpu=False
    scene.cycles.max_bounces=SET['max_bounces']
    # The overview cutaway lets camera rays pass many cut layers of land.
    scene.cycles.transparent_max_bounces=64; scene.render.threads_mode='FIXED'; scene.render.threads=SET['threads']
    scene.render.resolution_x=SET['width']; scene.render.resolution_y=SET['height']; scene.render.resolution_percentage=100
    scene.render.image_settings.file_format='PNG'; scene.render.image_settings.color_mode='RGB'
    scene.render.image_settings.color_depth='8'; scene.render.image_settings.compression=100
    scene.render.use_stamp=False; scene.view_settings.view_transform=SET['view_transform']; scene.view_settings.look=SET['view_look']
    scene.view_settings.exposure=SET['exposure']
    doc,meshes,room,stats=read_package(config['package'],config['room'])
    if config['expected_setup'] is not None and doc['setup']!=config['expected_setup']:
        raise ValueError('package setup differs from expected setup')
    if harness_revision()!=config['harness_revision']:
        raise ValueError('harness sources changed between launch and render')
    sun=sun_position(doc['setup']['latitude_deg'],doc['setup']['day_of_year'],doc['setup']['solar_time_h'],doc['setup']['neg_z_bearing_deg'])
    make_world(doc['setup'],sun)
    # An island package brings its own sea out to the horizon; the shared far
    # ground (a moss plain at -0.025) would cover it.
    if not (doc.get('x_generator') or {}).get('sea'):
        far_ground(room)
    terrain_objects=[]; collision_objects=[]; land_objects=[]
    for category in ['terrain','water','scenery']:
        for index,item in enumerate(doc[category]):
            primitives=meshes[item['mesh']]
            if category=='water':
                role='still_water' if item['kind']=='still' else 'flowing_water'
                primitives=[dict(p,role=role) for p in primitives]
                primitives=[{k:v for k,v in p.items() if k not in ('blend_role','blend_weights')} for p in primitives]
            objects=add_mesh(category+'_'+str(index),primitives,soft=True)
            if category=='terrain': terrain_objects+=objects; collision_objects+=objects
            if category!='water': land_objects+=objects
    for category in ['scatter','objects']:
        for i,item in enumerate(doc[category]):
            name=item['prototype']
            primitives=prototype(name) if name in SIZES else meshes[doc['prototypes'][name]['mesh']]
            base=SIZES[name] if name in SIZES else doc['prototypes'][name]['size_m']
            scale=item['scale'] if category=='scatter' else [a/b for a,b in zip(item['size_m'],base)]
            collision_objects+=add_mesh(category+'_'+str(i),primitives,item['position_m'],item['yaw_deg'],scale,
                                        item.get('tint',(1,1,1)),smooth=name in SIZES and name not in ('crate','cottage','fence','lantern'),
                                        bevel=name in ('crate','cottage','fence','lantern'))
    plan=camera_plan(room,meshes,doc['terrain']); bpy.context.view_layer.update()
    if config.get('eye'):
        # A caller's own 10 cm eye (review of one place, such as a pond) takes the window eye's tile.
        x,z,ax,az=config['eye']; ground=ground_height(meshes,doc['terrain'],x,z)
        y=(ground if ground is not None else room['bounds']['min_m'][1])+.087
        plan=[v for v in plan if v['name']!='eye_window']+[dict(name='eye_custom',kind='eye',position_m=[x,y,z],
              target_m=[ax,y,az],ground_height_m=ground,ground_missing=ground is None)]
    cut_faces=cutaway(land_objects,plan,room)
    bvh=geometry_bvh(collision_objects)
    for view in plan:
        view['inside_geometry']=eye_blocked(bvh,Vector(xyz(view['position_m']))) if view['kind']=='eye' else False
    player=next(v for v in plan if v['name']=='eye_player')
    figure_position=list(player['position_m']); figure_position[1]-=.087
    for view in plan:
        position=list(figure_position)
        if view['kind']=='eye':
            x,_,z=view['position_m']; aim=view['target_m']
            dx,dz=aim[0]-x,aim[2]-z; length=math.hypot(dx,dz)
            dx,dz=dx/length,dz/length
            # 40 cm forward and 7 cm to camera right, never at its own eye.
            fx,fz=x+.4*dx-.07*dz,z+.4*dz+.07*dx
            ground=ground_height(meshes,doc['terrain'],fx,fz)
            position=[fx,ground if ground is not None else room['bounds']['min_m'][1],fz]
            view['figure_ground_missing']=ground is None
        view['figure_position_m']=position
        view['figure_inside_geometry']=eye_blocked(bvh,Vector(xyz([position[0],position[1]+.05,position[2]])))
    figure=[cone(.014,.018,0,'metal',.012),cone(.02,.05,.018,'cloth',.014),
            ellipsoid([.032,.032,.032],[0,.084,0],'wood',5,12)]
    FIGURE=add_mesh('scale_figure_10cm',figure,figure_position,smooth=True)
    camera=bpy.data.objects.new('fixed_review_camera',bpy.data.cameras.new('fixed_review_camera'))
    bpy.context.collection.objects.link(camera); scene.camera=camera; camera.data.clip_start=.003; camera.data.clip_end=2000
    requested=config.get('views')
    previous=config.get('previous_receipt')
    def retain(view,entry):
        for key,value in view.items():
            if key=='projection': value='PERSP'  # Plan label versus Blender's enum.
            if entry.get(key)!=value:
                raise ValueError('retained camera/figure changed: '+view['name']+' '+key)
        return dict(entry,rendered_harness_sha256=entry.get('rendered_harness_sha256',
                    previous['harness_revision']['sha256']))
    # Validate every retained camera before overwriting any image.
    retained={}
    if requested is not None:
        old={v['name']:v for v in previous['views']}
        if previous['sun']!=sun: raise ValueError('retained lighting changed')
        for view in plan:
            if view['name'] not in requested: retained[view['name']]=retain(view,old[view['name']])
        if 'slope' not in requested: retained['slope']=retain(plan[0],previous['diagnostic'])
    renders=[]
    for view in plan:
        if view['name'] in retained:
            print('HARNESS_RETAIN view='+view['name'],flush=True)
            renders.append(retained[view['name']])
        else: renders.append(render_view(view,camera))
    sheet(OUT,renders,config['label'],doc['setup'])
    # Separate assumed slope diagnostic: untouched meshes, colour by face normal.
    slope_mats=[]
    for name,colour in [('walkable',(.25,.7,.3)),('climbable',(.9,.63,.15)),('too_steep',(.8,.18,.18))]:
        m=bpy.data.materials.new('diagnostic_'+name); m.use_nodes=True
        tree=m.node_tree; tree.nodes.clear()
        emission=node(tree,'ShaderNodeEmission'); emission.inputs['Color'].default_value=(*colour,1)
        output=node(tree,'ShaderNodeOutputMaterial'); tree.links.new(emission.outputs[0],output.inputs['Surface'])
        slope_mats.append(m)
    for obj in list(bpy.context.scene.objects):
        if obj.type=='MESH' and obj not in terrain_objects: obj.hide_render=True
    for obj in list(bpy.context.scene.objects):
        if obj.name.startswith('scale_figure'): obj.hide_render=True
    for obj in terrain_objects:
        obj.data.materials.clear()
        for m in slope_mats: obj.data.materials.append(m)
        for face in obj.data.polygons:
            slope=math.degrees(math.acos(max(-1,min(1,face.normal.z))))
            face.material_index=0 if slope<=35 else 1 if slope<=55 else 2
    diagnostic=retained['slope'] if 'slope' in retained else render_view(plan[0],camera,True)
    receipt=dict(format='garage_game.landscape.render-receipt',version=1,
                 blender='.'.join(map(str,bpy.app.version)),blender_display=bpy.app.version_string,
                 harness_revision=config['harness_revision'],expected_setup=config['expected_setup'],
                 settings=SET,package_sha256=sha((Path(config['package'])/'package.json').read_bytes()),
                 room_sha256=doc['source']['room_sha256'],setup=doc['setup'],sun=sun,views=renders,
                 diagnostic=dict(diagnostic,assumed_thresholds_deg=[35,55],unlit=True,depth_of_field=False,
                                 note='Slope only; no connectivity, clearance or collision certification.'),
                 stats=stats,scale_figure_height_m=.1,
                 overview_cutaway=dict(faces=cut_faces,rule='land/scenery faces outside bounds whose camera ray lands on the floor more than inset_m inside bounds; camera rays only',inset_m=CUT_INSET),
                 smooth_shading=dict(crease_deg=CREASE_DEG,scope='terrain, water and scenery; welded by position across primitives'),
                 partial_rerender=requested is not None,retained_views=sorted(retained),
                 uniform_harness_revision=all(v['rendered_harness_sha256']==config['harness_revision']['sha256']
                                              for v in renders+[diagnostic]),
                 total_view_seconds=round(sum(v['seconds'] for v in renders),6))
    if config['kit_picture']: receipt['kit']=kit_picture(camera)
    receipt['total_render_seconds']=round(receipt['total_view_seconds']+diagnostic['seconds']+
                                        receipt.get('kit',{}).get('seconds',0),6)
    (OUT/'receipt.json').write_bytes(canonical(receipt))
    print('HARNESS_COMPLETE seconds='+str(receipt['total_view_seconds']),flush=True)



if __name__=='__main__': main()
