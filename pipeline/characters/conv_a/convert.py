"""Offline, deterministic interpretation -> rounded parts -> self-contained GLB."""
from __future__ import annotations

import argparse
from functools import lru_cache
import hashlib
import json
import math
import os
from pathlib import Path
import re
import struct
import subprocess
import zlib

import numpy as np
from PIL import Image, ImageOps

# The data-format ids (contracts/README.md, "Schema ids"): contracts/schema_ids.py, loaded by its path. The converter writes
# the new ones; the game reads the committed characters' old ones too.
import importlib.util as _importlib_util
_IDS_SPEC = _importlib_util.spec_from_file_location("garage_game_schema_ids", Path(__file__).resolve().parents[3] / "contracts" / "schema_ids.py")
IDS = _importlib_util.module_from_spec(_IDS_SPEC)
_IDS_SPEC.loader.exec_module(IDS)
PART_KEY, MATERIAL_KEY = IDS.GLB_PART_KEYS[0], IDS.GLB_MATERIAL_KEYS[0]

DEFAULT_BLENDER = r"C:\Users\blues\AppData\Local\Programs\Blender\blender-5.2.2-windows-x64\blender.exe"


def canonical_png(raw):
    """Remove ancillary provenance/timing, preserving compressed pixels and colour."""
    signature = b"\x89PNG\r\n\x1a\n"
    if not raw.startswith(signature):
        raise ValueError("not a PNG render")
    result,offset = bytearray(signature),len(signature)
    while offset<len(raw):
        if offset+12>len(raw):
            raise ValueError("truncated PNG chunk")
        size = struct.unpack_from(">I",raw,offset)[0]
        end = offset+12+size
        if end>len(raw):
            raise ValueError("truncated PNG data")
        kind = raw[offset+4:offset+8]
        expected_crc = struct.unpack_from(">I",raw,end-4)[0]
        if zlib.crc32(raw[offset+4:end-4])!=expected_crc:
            raise ValueError("PNG checksum mismatch")
        if kind not in (b"tEXt",b"zTXt",b"iTXt",b"eXIf",b"tIME"):
            result.extend(raw[offset:end])
        offset = end
    return bytes(result)


def normalize_renders(folder):
    for name in ("front","three_quarter","side","back","turntable"):
        path = Path(folder)/(name+".png")
        path.write_bytes(canonical_png(path.read_bytes()))


def canonical(data):
    return (json.dumps(data, ensure_ascii=False, sort_keys=True, indent=2,
                       allow_nan=False) + "\n").encode("utf-8")


def read_json(path):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError(f"duplicate JSON key: {key}")
            result[key] = value
        return result
    return json.loads(Path(path).read_text(encoding="utf-8"), object_pairs_hook=pairs,
                      parse_constant=lambda v: (_ for _ in ()).throw(ValueError(v)))


def vector(value, length):
    a = np.asarray(value, dtype=float)
    if a.shape != (length,) or not np.isfinite(a).all() or np.max(np.abs(a)) > 100000:
        raise ValueError(f"expected {length} finite bounded coordinates")
    return a


def validate(spec):
    if spec["version"] != 1:
        raise ValueError("unsupported interpretation version")
    if not isinstance(spec["name"], str) or not 1 <= len(spec["name"]) <= 200:
        raise ValueError("name must be a short string")
    vector(spec["origin"], 3)
    if np.min(vector(spec["image_size"], 2)) <= 0:
        raise ValueError("positive image_size required")
    if spec["motion"]["kind"] not in ("float", "waddle", "hop", "stride"):
        raise ValueError("unknown motion kind")
    for key in ("observations", "uncertainties"):
        if not isinstance(spec[key], list) or not all(isinstance(s, str) for s in spec[key]):
            raise ValueError(f"{key} must be an array of strings")
    if "applied_notes" in spec:
        if (not isinstance(spec["applied_notes"], list)
                or not all(isinstance(s, str) and s.strip() for s in spec["applied_notes"])):
            raise ValueError("applied_notes must be an array of nonempty strings")
    if not isinstance(spec["colour_reasoning"], str):
        raise ValueError("colour_reasoning required")
    if not 1 <= len(spec["palette"]) <= 64:
        raise ValueError("palette budget exceeded")
    for colour in spec["palette"].values():
        if not isinstance(colour, str) or not re.fullmatch(r"#[0-9a-fA-F]{6}", colour):
            raise ValueError("palette colours must be #RRGGBB")
    if not 1 <= len(spec["parts"]) <= 128:
        raise ValueError("part budget exceeded")
    previous = {}
    count = 0
    for part in spec["parts"]:
        name = part["name"]
        if not re.fullmatch(r"[a-z][a-z0-9_]{0,63}", name) or name in previous or name == "character_root":
            raise ValueError(f"invalid or duplicate part name: {name}")
        if part["parent"] is not None and part["parent"] not in previous:
            raise ValueError("parents must precede children")
        if not isinstance(part["role"], str):
            raise ValueError("part role required")
        if part.get("kind", "solid") not in ("solid", "effect"):
            raise ValueError("part kind must be solid or effect")
        if part.get("kind") == "effect":
            hint = part.get("motion_hint", {})
            if hint.get("kind") not in ("twinkle", "orbit", "drift", "flicker", "pulse"):
                raise ValueError("effect needs a motion_hint kind")
            if not isinstance(hint.get("reason"), str):
                raise ValueError("effect needs a motion reason")
        if "material_hint" in part:
            hint = part["material_hint"]
            if (hint.get("kind") != "bubble" or hint.get("see_through") is not True
                    or hint.get("rim") != "shimmer"
                    or not re.fullmatch(r"#[0-9a-fA-F]{6}", hint.get("aura_colour", ""))):
                raise ValueError("bubble needs see_through, shimmer rim and #RRGGBB aura_colour")
        vector(part["pivot"], 3)
        for shape in part["shapes"]:
            count += 1
            if count > 256:
                raise ValueError("shape budget exceeded")
            if shape["colour"] not in spec["palette"]:
                raise ValueError("unknown palette reference")
            if "surface" in shape:
                surface = previous.get(shape["surface"])
                if not surface or not surface["shapes"] or surface["shapes"][0]["kind"] not in ("volume", "ellipsoid"):
                    raise ValueError("surface must be a preceding volume or ellipsoid part")
            kind = shape["kind"]
            if kind in ("ellipsoid", "volume"):
                vector(shape["center"], 3)
            if kind == "ellipsoid":
                if np.min(vector(shape["radii"], 3)) <= 0:
                    raise ValueError("ellipsoid radii must be positive")
            elif kind == "volume":
                if "thickness" in shape and not 0 < float(shape["thickness"]) <= 100000:
                    raise ValueError("volume thickness must be positive and bounded")
                if not 3 <= len(shape["contour"]) <= 64:
                    raise ValueError("volume needs 3 to 64 contour points")
                for point in shape["contour"]:
                    vector(point, 2)
                validate_contour(shape)
            elif kind == "tube":
                if not 2 <= len(shape["points"]) <= 64 or len(shape["points"]) != len(shape["radii"]):
                    raise ValueError("tube needs 2 to 64 points and one radius per point")
                for point in shape["points"]:
                    vector(point, 3)
                radii = np.asarray(shape["radii"], dtype=float)
                if not np.isfinite(radii).all() or min(radii) <= 0 or max(radii) > 100000:
                    raise ValueError("tube radii must be positive and bounded")
                if np.min(np.linalg.norm(np.diff(shape["points"], axis=0), axis=1)) < 1e-6:
                    raise ValueError("coincident tube points")
            else:
                raise ValueError(f"unknown shape kind: {kind}")
        previous[name] = part


def sample(points, closed=False, steps=4, smooth=True):
    p = np.asarray(points, dtype=float)
    out = []
    for i in range(len(p) if closed else len(p) - 1):
        a, b = p[i], p[(i + 1) % len(p)]
        prev = p[(i - 1) % len(p)] if closed or i else 2 * a - b
        nxt = p[(i + 2) % len(p)] if closed or i + 2 < len(p) else 2 * b - a
        for t in np.arange(steps) / steps:
            if smooth:
                out.append(0.5 * ((2 * a) + (-prev + b) * t +
                                  (2 * prev - 5 * a + 4 * b - nxt) * t*t +
                                  (-prev + 3 * a - 3 * b + nxt) * t*t*t))
            else:
                out.append(a * (1-t) + b*t)
    if not closed:
        out.append(p[-1])
    return np.asarray(out)


def contour(shape):
    return sample(shape["contour"], closed=True, smooth=shape.get("smooth", True))


def cross2(a, b):
    return a[..., 0] * b[..., 1] - a[..., 1] * b[..., 0]


def validate_contour(shape):
    p = contour(shape)
    if np.min(np.linalg.norm(p-np.roll(p,1,axis=0),axis=1)) < 1e-7:
        raise ValueError("coincident contour points")
    for i in range(len(p)):
        a,b = p[i],p[(i+1)%len(p)]
        for j in range(i+2,len(p)):
            if i==0 and j==len(p)-1:
                continue
            c,d = p[j],p[(j+1)%len(p)]
            if cross2(b-a,c-a)*cross2(b-a,d-a)<0 and cross2(d-c,a-c)*cross2(d-c,b-c)<0:
                raise ValueError("self-intersecting contour; split it into pieces or disable smoothing")
    if abs(np.sum(cross2(p,np.roll(p,-1,axis=0))))<1e-7:
        raise ValueError("contour has no area")


def distance_to_outline(xy, boundary):
    edge = np.roll(boundary,-1,axis=0)-boundary
    delta = xy[:,None,:]-boundary[None,:,:]
    t = np.clip(np.sum(delta*edge,axis=2)/np.sum(edge*edge,axis=1),0,1)
    return np.sqrt(np.min(np.sum((delta-t[:,:,None]*edge)**2,axis=2),axis=1))


def improve_triangulation(vertices, faces, fixed, rounds=6):
    """Deterministic interior edge flips; silhouette edges never change."""
    faces = faces.copy()
    for _ in range(rounds):
        edges = {}
        for i,face in enumerate(faces):
            for j in range(3):
                a,b,c = int(face[j]),int(face[(j+1)%3]),int(face[(j+2)%3])
                edges.setdefault(tuple(sorted((a,b))),[]).append((i,c))
        used,changed = set(),False
        for (a,b),adjacent in sorted(edges.items()):
            if len(adjacent)!=2:
                continue
            (i,c),(j,d) = adjacent
            if i in used or j in used:
                continue
            if tuple(sorted((c,d))) in edges:
                continue
            if fixed[c] and fixed[d]:
                continue
            va,vb,vc,vd = vertices[[a,b,c,d]]
            if (cross2(vd-vc,va-vc)*cross2(vd-vc,vb-vc)>=-1e-10 or
                    cross2(vb-va,vc-va)*cross2(vb-va,vd-va)>=-1e-10):
                continue
            angle_c = math.atan2(abs(cross2(va-vc,vb-vc)),np.dot(va-vc,vb-vc))
            angle_d = math.atan2(abs(cross2(va-vd,vb-vd)),np.dot(va-vd,vb-vd))
            if angle_c+angle_d <= math.pi+1e-7:
                continue
            for index,tri in ((i,[c,d,a]),(j,[d,c,b])):
                p,q,r = vertices[tri]
                if cross2(q-p,r-p)<0:
                    tri[1],tri[2] = tri[2],tri[1]
                faces[index] = tri
            used.update((i,j))
            changed = True
        if not changed:
            break
    return faces


@lru_cache(maxsize=64)
def cushion_data(points, smooth):
    """Ear-clipped polygon, refined and relaxed to distribute its interior vertices."""
    boundary = sample(points,closed=True,smooth=smooth)
    if np.sum(cross2(boundary,np.roll(boundary,-1,axis=0)))<0:
        boundary = boundary[::-1].copy()
    vertices = list(boundary)
    remaining = list(range(len(boundary)))
    faces = []
    while len(remaining)>3:
        found = False
        for i,b in enumerate(remaining):
            a,c = remaining[i-1],remaining[(i+1)%len(remaining)]
            va,vb,vc = boundary[[a,b,c]]
            if cross2(vb-va,vc-vb)<=1e-8:
                continue
            other = boundary[[j for j in remaining if j not in (a,b,c)]]
            inside = (cross2(vb-va,other-va)>=-1e-8)&(cross2(vc-vb,other-vb)>=-1e-8)&(cross2(va-vc,other-vc)>=-1e-8)
            if np.any(inside):
                continue
            faces.append((a,b,c))
            remaining.pop(i)
            found = True
            break
        if not found:
            raise ValueError("cannot triangulate contour; check repeated or crossing edges")
    faces.append(tuple(remaining))
    fixed = [True]*len(vertices)
    boundary_edges = {tuple(sorted((i,(i+1)%len(boundary)))) for i in range(len(boundary))}
    for _ in range(2):
        mids, refined, new_edges = {}, [], set()
        for a,b,c in faces:
            ids = []
            for j,k in ((a,b),(b,c),(c,a)):
                key = tuple(sorted((j,k)))
                if key not in mids:
                    mid = len(vertices)
                    mids[key] = mid
                    vertices.append((vertices[j]+vertices[k])/2)
                    fixed.append(key in boundary_edges)
                    if key in boundary_edges:
                        new_edges.update((tuple(sorted((j,mid))),tuple(sorted((mid,k)))))
                ids.append(mids[key])
            ab,bc,ca = ids
            refined.extend(((a,ab,ca),(ab,b,bc),(ca,bc,c),(ab,bc,ca)))
        faces,boundary_edges = refined,new_edges
    # An ear-tip triangle can have all three vertices on the silhouette. Give
    # it an interior point so its front and back cannot become coplanar copies.
    refined = []
    for a,b,c in faces:
        if fixed[a] and fixed[b] and fixed[c]:
            mid = len(vertices)
            vertices.append((vertices[a]+vertices[b]+vertices[c])/3)
            fixed.append(False)
            refined.extend(((a,b,mid),(b,c,mid),(c,a,mid)))
        else:
            refined.append((a,b,c))
    vertices,faces,fixed = np.array(vertices),np.array(refined),np.array(fixed)
    # A diagonal connecting two silhouette vertices would weld front and back
    # through the interior. Split each such chord with a true interior vertex.
    edges = {}
    for face in faces:
        for j in range(3):
            key = tuple(sorted((int(face[j]),int(face[(j+1)%3]))))
            edges[key] = edges.get(key,0)+1
    chords = {key for key,count in edges.items() if count==2 and fixed[key[0]] and fixed[key[1]]}
    mids,new_vertices,refined = {},list(vertices),[]
    for face in faces:
        for j in range(3):
            a,b,c = [int(face[(j+k)%3]) for k in range(3)]
            key = tuple(sorted((a,b)))
            if key in chords:
                if key not in mids:
                    mids[key] = len(new_vertices)
                    new_vertices.append((vertices[a]+vertices[b])/2)
                mid = mids[key]
                refined.extend(((a,mid,c),(mid,b,c)))
                break
        else:
            refined.append(face)
    fixed = np.concatenate((fixed,np.zeros(len(new_vertices)-len(vertices),dtype=bool)))
    vertices,faces = np.array(new_vertices),np.array(refined)
    for _ in range(3):
        faces = improve_triangulation(vertices,faces,fixed)
        neighbours = [set() for _ in vertices]
        incident = [[] for _ in vertices]
        for fi,(a,b,c) in enumerate(faces):
            neighbours[a].update((b,c)); neighbours[b].update((a,c)); neighbours[c].update((a,b))
            for i in (a,b,c):
                incident[i].append(fi)
        for _ in range(8):
            for i in np.where(~fixed)[0]:
                prior = vertices[i].copy()
                vertices[i] = prior*0.5+vertices[sorted(neighbours[i])].mean(axis=0)*0.5
                adjacent = faces[incident[i]]
                signed = cross2(vertices[adjacent[:,1]]-vertices[adjacent[:,0]],vertices[adjacent[:,2]]-vertices[adjacent[:,0]])
                if np.any(signed<=1e-5):
                    vertices[i] = prior
    distance = distance_to_outline(vertices,boundary)
    maxdist = max(distance.max(),1e-6)
    # Diffuse the squared bulge so distance-field medial axes don't leave sharp
    # ridges. Square-root at the end keeps the silhouette tangent rounded.
    phi = distance*(2*maxdist-distance)
    interior = np.where(~fixed)[0]
    source = {i:0.15*np.mean(np.sum((vertices[sorted(neighbours[i])]-vertices[i])**2,axis=1))
              for i in interior}
    for _ in range(60):
        updated = phi.copy()
        for i in interior:
            updated[i] = np.mean(phi[sorted(neighbours[i])])+source[i]
        phi = updated
        phi[fixed] = 0
    # Fuller shoulders retain more of the traced width away from the equator.
    # The outline remains fixed, including its unequal lobes and concavities.
    height = (np.maximum(0,phi)/max(phi.max(),1e-9))**0.35
    return vertices,faces,fixed,boundary,height


def cushion(shape):
    return cushion_data(tuple(tuple(p) for p in shape["contour"]),shape.get("smooth",True))


def volume_thickness(shape):
    """Default to a plush volume; explicit thickness still permits thin marks."""
    if "thickness" in shape:
        return shape["thickness"]
    return 0.95 * float(np.ptp(contour(shape), axis=0).min())


def cushion_mesh(shape):
    xy,faces,fixed,boundary,profile = cushion(shape)
    height = volume_thickness(shape)/2*profile
    height[fixed] = 0
    vertices = np.column_stack((xy,shape["center"][2]+height))
    back = np.arange(len(vertices))
    interior = np.where(~fixed)[0]
    back[interior] = np.arange(len(vertices),len(vertices)+len(interior))
    vertices = np.concatenate((vertices,np.column_stack((xy[interior],shape["center"][2]-height[interior]))))
    faces = np.concatenate((faces,back[faces[:,[0,2,1]]]))
    return vertices,faces


def radial_mesh(boundary, center, halfdepth, rings=16):
    """Latitudes of a rounded solid whose equator is an arbitrary star contour."""
    n = len(boundary)
    vertices = [[*center[:2], center[2] + halfdepth]]
    for j in range(1, rings):
        angle = math.pi * j / rings
        xy = center[:2] + (boundary - center[:2]) * math.sin(angle)
        vertices.extend([[x, y, center[2]+halfdepth*math.cos(angle)] for x, y in xy])
    vertices.append([*center[:2], center[2]-halfdepth])
    faces = []
    for i in range(n):
        k = (i+1) % n
        faces.append([0, 1+i, 1+k])
        for j in range(rings-2):
            a, b = 1+j*n+i, 1+j*n+k
            faces.extend([[a, a+n, b], [b, a+n, b+n]])
        a = 1+(rings-2)*n
        faces.append([len(vertices)-1, a+k, a+i])
    vertices, faces = np.asarray(vertices), np.asarray(faces)
    # All faces share winding; orient using the top cap.
    normal = np.cross(vertices[faces[0,1]]-vertices[0], vertices[faces[0,2]]-vertices[0])
    if normal[2] < 0:
        faces = faces[:, [0, 2, 1]]
    return vertices, faces


def tube_mesh(shape):
    p = sample(shape["points"], steps=5, smooth=shape.get("smooth", True))
    # Linear radius interpolation prevents a Catmull overshoot becoming negative.
    r = sample(np.array(shape["radii"])[:, None], steps=5, smooth=False)[:, 0]
    tangents = np.gradient(p, axis=0)
    tangents /= np.linalg.norm(tangents, axis=1)[:, None]
    rings, n = [], 12
    prior = None
    for point, tangent, radius in zip(p, tangents, r):
        if prior is None:
            axis = np.eye(3)[np.argmin(np.abs(tangent))]
            side = np.cross(tangent, axis)
        else:
            side = prior - tangent*np.dot(prior, tangent)
        side /= np.linalg.norm(side)
        prior = side
        other = np.cross(tangent, side)
        rings.extend([point+radius*(math.cos(t)*side+math.sin(t)*other)
                      for t in np.arange(n)*2*math.pi/n])
    vertices = np.asarray(rings + [p[0], p[-1]])
    faces = []
    for j in range(len(p)-1):
        for i in range(n):
            a, b = j*n+i, j*n+(i+1)%n
            faces.extend([[a,b,a+n], [b,b+n,a+n]])
    for i in range(n):
        k = (i+1)%n
        faces.append([len(vertices)-2,k,i])
        faces.append([len(vertices)-1,(len(p)-1)*n+i,(len(p)-1)*n+k])
    return vertices, np.asarray(faces)


def front_depth(shape, xy, surfaces):
    c = np.asarray(shape["center"])
    delta = xy-c[:2]
    if shape["kind"] == "ellipsoid":
        radius2 = np.sum((delta/np.asarray(shape["radii"][:2]))**2, axis=1)
        h = shape["radii"][2]
    else:
        vertices,faces,_,_,profile = cushion(shape)
        a,b,cxy = (vertices[faces[:,i]] for i in range(3))
        ab,ac = b-a,cxy-a
        den = cross2(ab,ac)
        if np.any(den<=0):
            raise ValueError("folded cushion triangulation")
        depth = []
        for start in range(0,len(xy),128):
            delta = xy[start:start+128,None,:]-a
            u = cross2(delta,ac)/den
            v = cross2(ab,delta)/den
            inside = (u>=-1e-7)&(v>=-1e-7)&(u+v<=1+1e-7)
            value = (1-u-v)*profile[faces[:,0]]+u*profile[faces[:,1]]+v*profile[faces[:,2]]
            depth.extend(np.max(np.where(inside,value,0),axis=1))
        d = c[2]+volume_thickness(shape)/2*np.asarray(depth)
    if shape["kind"] == "ellipsoid":
        d = c[2] + h*np.sqrt(np.maximum(0,1-radius2))
    if "surface" in shape:
        d += front_depth(surfaces[shape["surface"]], xy, surfaces)
    return d


def make_shape(shape, surfaces):
    kind = shape["kind"]
    if kind == "tube":
        v, f = tube_mesh(shape)
    elif kind == "volume":
        v, f = cushion_mesh(shape)
    else:
        c = np.asarray(shape["center"],dtype=float)
        a = np.arange(32)*2*math.pi/32
        boundary = c[:2] + np.stack((np.cos(a)*shape["radii"][0],np.sin(a)*shape["radii"][1]),axis=1)
        halfdepth = shape["radii"][2]
        v, f = radial_mesh(boundary,c,halfdepth)
    if "surface" in shape:
        v[:,2] += front_depth(surfaces[shape["surface"]], v[:,:2], surfaces)
    return v, f


class Glb:
    def __init__(self, palette):
        self.binary = bytearray()
        self.data = {"asset":{"version":"2.0","generator":"Garage Game drawing converter 1"},
                     "scene":0,"scenes":[{"nodes":[0]}],
                     "nodes":[{"name":"character_root","children":[]}],
                     "meshes":[],"accessors":[],"bufferViews":[],"materials":[]}
        self.colours = {}
        for name, colour in sorted(palette.items()):
            rgb = [int(colour[i:i+2],16)/255 for i in (1,3,5)]
            linear = [v/12.92 if v<=0.04045 else ((v+0.055)/1.055)**2.4 for v in rgb]
            self.colours[name] = len(self.data["materials"])
            self.data["materials"].append({"name":name,"pbrMetallicRoughness":{
                "baseColorFactor":linear+[1],"metallicFactor":0,"roughnessFactor":0.78}})

    def material(self, colour, part, palette):
        if "material_hint" not in part:
            return self.colours[colour]
        name = part["material_hint"]["kind"] + "__" + part["name"] + "__" + colour
        if name not in self.colours:
            base = self.data["materials"][self.colours[colour]]
            self.colours[name] = len(self.data["materials"])
            self.data["materials"].append({
                "name": name, "pbrMetallicRoughness": base["pbrMetallicRoughness"],
                "extras": {MATERIAL_KEY: {
                    **part["material_hint"], "fallback_colour": palette[colour]}}})
        return self.colours[name]

    def accessor(self, array, kind, component, target):
        self.binary.extend(b"\0"*((-len(self.binary))%4))
        raw = array.tobytes()
        view = {"buffer":0,"byteOffset":len(self.binary),"byteLength":len(raw),"target":target}
        self.binary.extend(raw)
        idx = len(self.data["bufferViews"])
        self.data["bufferViews"].append(view)
        access = {"bufferView":idx,"componentType":component,"count":len(array),"type":kind}
        if kind == "VEC3":
            access.update(min=array.min(axis=0).tolist(),max=array.max(axis=0).tolist())
        self.data["accessors"].append(access)
        return len(self.data["accessors"])-1

    def primitive(self, vertices, faces, colour):
        normals = np.zeros_like(vertices)
        face_normals = np.cross(vertices[faces[:,1]]-vertices[faces[:,0]],vertices[faces[:,2]]-vertices[faces[:,0]])
        for i in range(3):
            np.add.at(normals,faces[:,i],face_normals)
        lengths = np.linalg.norm(normals,axis=1)
        if np.any(lengths < 1e-15):
            raise ValueError("degenerate mesh normals")
        normals /= lengths[:,None]
        return {"attributes":{
            "POSITION":self.accessor(vertices.astype("<f4"),"VEC3",5126,34962),
            "NORMAL":self.accessor(normals.astype("<f4"),"VEC3",5126,34962)},
            "indices":self.accessor(faces.astype("<u4").ravel(),"SCALAR",5125,34963),
            "material":self.colours[colour],"mode":4}

    def bytes(self):
        self.binary.extend(b"\0"*((-len(self.binary))%4))
        self.data["buffers"] = [{"byteLength":len(self.binary)}]
        doc = json.dumps(self.data,sort_keys=True,separators=(",",":"),allow_nan=False).encode("utf-8")
        doc += b" "*((-len(doc))%4)
        length = 12+8+len(doc)+8+len(self.binary)
        return (struct.pack("<III",0x46546C67,2,length)+struct.pack("<II",len(doc),0x4E4F534A)+doc+
                struct.pack("<II",len(self.binary),0x004E4942)+self.binary)


def build(spec, provenance):
    validate(spec)
    meshes, surfaces = [], {}
    for part in spec["parts"]:
        items = []
        for shape in part["shapes"]:
            v,f = make_shape(shape,surfaces)
            items.append((v,f,shape["colour"]))
        meshes.append(items)
        if part["shapes"]:
            surfaces[part["name"]] = part["shapes"][0]
    all_vertices = np.concatenate([v for items in meshes for v,_,_ in items])
    lo,hi = all_vertices.min(axis=0),all_vertices.max(axis=0)
    height = hi[1]-lo[1]
    if height <= 1e-6:
        raise ValueError("character needs nonzero height")
    scale = 0.1/height
    origin = np.asarray(spec["origin"],dtype=float).copy()
    origin[1] = hi[1]
    def world(v):
        return (origin-v)*scale
    glb = Glb(spec["palette"])
    metadata, nodes, pivots = [], {}, {}
    triangles = 0
    for part,items in zip(spec["parts"],meshes):
        pivot = world(np.asarray(part["pivot"]))
        parent = part["parent"]
        local = pivot-pivots[parent] if parent else pivot
        node = {"name":part["name"],"translation":local.tolist()}
        hints = {"kind": part.get("kind", "solid")}
        for key in ("motion_hint", "material_hint"):
            if key in part:
                hints[key] = part[key]
        node["extras"] = {PART_KEY: hints}
        primitives = []
        for v,f,colour in items:
            # Drawing-to-game mapping reflects all three axes; reverse winding.
            primitive = glb.primitive(world(v)-pivot,f[:,[0,2,1]],colour)
            primitive["material"] = glb.material(colour, part, spec["palette"])
            primitives.append(primitive)
            triangles += len(f)
        if primitives:
            node["mesh"] = len(glb.data["meshes"])
            glb.data["meshes"].append({"name":part["name"],"primitives":primitives})
        index = len(glb.data["nodes"])
        glb.data["nodes"].append(node)
        glb.data["nodes"][nodes[parent] if parent else 0].setdefault("children",[]).append(index)
        nodes[part["name"]], pivots[part["name"]] = index,pivot
        metadata.append({"name":part["name"],"role":part["role"],"parent":parent,
                         "node_index":index,"pivot_m":pivot.tolist(),"local_pivot_m":local.tolist(),
                         **hints})
    if triangles > 150000:
        raise ValueError("triangle budget exceeded (150000)")
    bounds_min,bounds_max = world(hi),world(lo)
    width,depth = bounds_max[0]-bounds_min[0],bounds_max[2]-bounds_min[2]
    meta = {"schema":IDS.id_of("character.draft"),"version":1,"name":spec["name"],
            "units":"metres","up":"+Y","forward":"-Z","root_pivot_m":[0,0,0],
            "height_m":0.1,"width_m":float(width),"depth_m":float(depth),
            "bounds_m":{"min":bounds_min.tolist(),"max":bounds_max.tolist()},
            "triangles":triangles,"parts":metadata,"motion":spec["motion"],
            "collision_hint":{"kind":"capsule","height_m":0.1,"radius_m":0.02,
                              "visual_overhang":bool(width>0.06 or depth>0.06)},
            "provenance":provenance,"uncertainties":spec["uncertainties"]}
    if "applied_notes" in spec:
        meta["applied_notes"] = spec["applied_notes"]
    return glb.bytes(),canonical(meta)


def convert(photo, description, interpretation, out, blender=DEFAULT_BLENDER, render=True):
    photo,description,interpretation,out = map(Path,(photo,description,interpretation,out))
    if out.exists() and any(out.iterdir()):
        raise ValueError("output folder must be empty; choose a new folder")
    spec = read_json(interpretation)
    with Image.open(photo) as source:
        image_size = ImageOps.exif_transpose(source).size
    description.read_text(encoding="utf-8")
    provenance = {"photo_sha256":hashlib.sha256(photo.read_bytes()).hexdigest(),
                  "description_sha256":hashlib.sha256(description.read_bytes()).hexdigest(),
                  "interpretation_sha256":hashlib.sha256(canonical(spec)).hexdigest(),
                  "photo_size_px":list(image_size)}
    glb,meta = build(spec,provenance)
    os.makedirs(out,exist_ok=True)
    (out/"character.glb").write_bytes(glb)
    (out/"character.json").write_bytes(meta)
    (out/"interpretation.json").write_bytes(canonical(spec))
    data = json.loads(meta)
    print("CHARACTER "+json.dumps({k:data[k] for k in ("name","height_m","width_m","depth_m","triangles")}),flush=True)
    print("PARTS "+", ".join(p["name"] for p in data["parts"]),flush=True)
    print("GLB_SHA256 "+hashlib.sha256(glb).hexdigest(),flush=True)
    if render:
        turntable = Path(__file__).resolve().parents[1]/"turntable.py"
        subprocess.run([str(blender),"-b","--factory-startup","-P",str(turntable),"--",
                        "--glb",str((out/"character.glb").resolve()),"--out",str(out.resolve())],check=True)
        normalize_renders(out)
    return data


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--photo",required=True,type=Path)
    parser.add_argument("--description",required=True,type=Path)
    parser.add_argument("--interpretation",required=True,type=Path)
    parser.add_argument("--out",required=True,type=Path)
    parser.add_argument("--blender",default=DEFAULT_BLENDER)
    parser.add_argument("--no-render",action="store_true",help="geometry/test iteration only")
    args = parser.parse_args()
    convert(args.photo,args.description,args.interpretation,args.out,args.blender,not args.no_render)


if __name__ == "__main__":
    main()
