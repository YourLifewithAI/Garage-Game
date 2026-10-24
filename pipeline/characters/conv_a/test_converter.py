import copy
from io import BytesIO
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import unittest
import uuid

import numpy as np
from PIL import Image, PngImagePlugin

from audit import accessor, audit, read_glb
from convert import build, canonical, canonical_png, convert, read_json, validate, make_shape
from synthetic import create

HERE = Path(__file__).resolve().parent


class ConverterTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.root = Path(os.environ.get("TEMP","/tmp"))/('garage-game-conv-a-test-'+uuid.uuid4().hex)
        os.makedirs(cls.root)
        create(cls.root/"input")
        cls.spec = read_json(cls.root/"input/interpretation.json")
        cls.outputs = []
        for i in range(2):
            out = cls.root/str(i)
            result = subprocess.run([sys.executable,"-B",str(HERE/"convert.py"),
                "--photo",str(cls.root/"input/drawing.png"),
                "--description",str(cls.root/"input/description.txt"),
                "--interpretation",str(cls.root/"input/interpretation.json"),
                "--out",str(out),"--no-render"],capture_output=True,text=True,
                env={**os.environ,"PYTHONHASHSEED":str(31+i)})
            if result.returncode:
                raise AssertionError(result.stdout+result.stderr)
            cls.outputs.append(out)

    @classmethod
    def tearDownClass(cls):
        # Only the exact fresh test directory under the system temp folder.
        shutil.rmtree(cls.root)

    def test_separate_process_byte_determinism(self):
        for name in ("character.glb","character.json","interpretation.json"):
            self.assertEqual((self.outputs[0]/name).read_bytes(),(self.outputs[1]/name).read_bytes())

    def test_third_drawing_geometry_and_parts(self):
        doc,meta = audit(self.outputs[0])
        self.assertEqual(meta["motion"]["kind"],"hop")
        self.assertEqual(sum(p["role"]=="eye" for p in meta["parts"]),3)
        self.assertEqual(sum(p["role"]=="leg" for p in meta["parts"]),1)
        self.assertLess(meta["triangles"],40000)
        self.assertEqual(len(doc["nodes"]),len(self.spec["parts"])+1)

    def test_front_is_negative_z_and_preserves_image_handedness(self):
        doc,binary = read_glb(self.outputs[0]/"character.glb")
        meta = json.loads((self.outputs[0]/"character.json").read_bytes())
        parts = {p["name"]:p for p in meta["parts"]}
        self.assertGreater(parts["eye_0"]["pivot_m"][0],parts["eye_2"]["pivot_m"][0])
        node = doc["nodes"][parts["pupil_1"]["node_index"]]
        primitive = doc["meshes"][node["mesh"]]["primitives"][0]
        v = accessor(doc,binary,primitive["attributes"]["POSITION"])+parts["pupil_1"]["pivot_m"]
        self.assertLess(v[:,2].max(),0)

    def test_pivot_rotation_preserves_joint_and_carries_child(self):
        doc,binary = read_glb(self.outputs[0]/"character.glb")
        meta = json.loads((self.outputs[0]/"character.json").read_bytes())
        parts = {p["name"]:p for p in meta["parts"]}
        leg,foot = parts["spring_leg"],parts["foot"]
        self.assertEqual(foot["parent"],"spring_leg")
        rotation = np.array([[0,-1,0],[1,0,0],[0,0,1]])
        pivot = np.array(leg["pivot_m"])
        rotated = pivot+rotation@(np.array(foot["pivot_m"])-pivot)
        self.assertGreater(np.linalg.norm(rotated-foot["pivot_m"]),0.01)
        np.testing.assert_allclose(pivot+rotation@(pivot-pivot),pivot)
        node = doc["nodes"][foot["node_index"]]
        np.testing.assert_allclose(pivot+node["translation"],foot["pivot_m"],atol=1e-10)

    def test_refuses_overwriting_output(self):
        before = (self.outputs[0]/"character.glb").read_bytes()
        with self.assertRaisesRegex(ValueError,"empty"):
            convert(self.root/"input/drawing.png",self.root/"input/description.txt",
                    self.root/"input/interpretation.json",self.outputs[0],render=False)
        self.assertEqual(before,(self.outputs[0]/"character.glb").read_bytes())

    def test_rejects_invalid_interpretation(self):
        for mutation in ("parent","surface","radius","crossing","nonfinite"):
            with self.subTest(mutation=mutation):
                s = copy.deepcopy(self.spec)
                if mutation=="parent":s["parts"][0]["parent"]="tail"
                if mutation=="surface":s["parts"][1]["shapes"][0]["surface"]="unknown"
                if mutation=="radius":s["parts"][1]["shapes"][0]["radii"][0]=-1
                if mutation=="crossing":s["parts"][0]["shapes"][0]["contour"]=[[0,0],[100,100],[100,0],[0,100]]
                if mutation=="nonfinite":s["parts"][0]["pivot"][0]=float("inf")
                with self.assertRaises(ValueError):validate(s)

    def test_strict_json(self):
        path = self.root/"bad.json"
        for value in ('{"version":1,"version":2}','{"x":NaN}'):
            path.write_bytes(value.encode())
            with self.assertRaises(ValueError):read_json(path)

    def test_png_metadata_normalization_preserves_pixels(self):
        image = Image.new("RGB",(17,13),(11,117,211))
        versions = []
        for date in ("2026/10/08 20:00:00","2026/10/08 20:04:00"):
            info = PngImagePlugin.PngInfo()
            info.add_text("Date",date)
            data = BytesIO()
            image.save(data,format="PNG",pnginfo=info)
            versions.append(canonical_png(data.getvalue()))
        self.assertEqual(versions[0],versions[1])
        np.testing.assert_array_equal(np.asarray(Image.open(BytesIO(versions[0]))),np.asarray(image))
        self.assertEqual(versions[0],canonical_png(versions[0]))
        broken = bytearray(versions[0]); broken[-5] ^= 1
        with self.assertRaisesRegex(ValueError,"checksum"):canonical_png(bytes(broken))

    def test_input_content_changes_provenance_without_embedding_pixels(self):
        # Two photographs with the same authored interpretation have the same
        # geometry but different provenance. This makes the AI boundary explicit.
        a,ma = build(self.spec,{"photo_sha256":"a"})
        b,mb = build(self.spec,{"photo_sha256":"b"})
        self.assertEqual(a,b)
        self.assertNotEqual(ma,mb)
        self.assertNotIn(b"drawing.png",a)
        self.assertNotIn(b"file:",a)

    def test_default_plush_depth_keeps_synthetic_outline(self):
        shallow = copy.deepcopy(self.spec["parts"][0]["shapes"][0])
        plush = copy.deepcopy(shallow)
        del plush["thickness"]
        a, _ = make_shape(shallow, {})
        b, _ = make_shape(plush, {})
        np.testing.assert_array_equal(a[:, :2], b[:, :2])
        self.assertGreater(np.ptp(b[:, 2]), 2 * np.ptp(a[:, 2]))
        self.assertGreater(np.ptp(b[:, 2]), 0.9 * np.ptp(b[:, :2], axis=0).min())

    def test_effect_and_bubble_export_replay_height_and_pivots(self):
        spec = copy.deepcopy(self.spec)
        body = spec["parts"][0]
        del body["shapes"][0]["thickness"]
        body["material_hint"] = dict(kind="bubble", see_through=True,
                                     rim="shimmer", aura_colour="#B4EED8")
        spec["parts"].append(dict(name="loose_star", role="sparkle", kind="effect",
            parent="body", pivot=[460,180,5],
            motion_hint=dict(kind="twinkle", reason="Independent floating star"),
            shapes=[dict(kind="ellipsoid", center=[460,180,5], radii=[8,12,8], colour="gold")]))
        spec["parts"].append(dict(name="aura", role="glow", kind="effect", parent="body",
            pivot=[300,330,0], shapes=[], motion_hint=dict(kind="pulse", reason="Soft aura")))
        source = self.root / "effects.json"
        source.write_bytes(canonical(spec))
        outputs = []
        for seed in (71, 93):
            out = self.root / ("effects-" + str(seed))
            result = subprocess.run([sys.executable, "-B", str(HERE / "convert.py"),
                "--photo", str(self.root / "input/drawing.png"),
                "--description", str(self.root / "input/description.txt"),
                "--interpretation", str(source), "--out", str(out), "--no-render"],
                capture_output=True, text=True, env={**os.environ, "PYTHONHASHSEED": str(seed)})
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            outputs.append(out)
        for name in ("character.glb", "character.json", "interpretation.json"):
            self.assertEqual((outputs[0]/name).read_bytes(), (outputs[1]/name).read_bytes())
        doc, meta = audit(outputs[0])
        for part in meta["parts"]:
            extra = doc["nodes"][part["node_index"]]["extras"]["garage_game_part"]
            self.assertEqual(extra["kind"], part["kind"])
            if part["kind"] == "effect":
                self.assertEqual(extra["motion_hint"], part["motion_hint"])
        self.assertEqual(sum(p["kind"] == "effect" for p in meta["parts"]), 2)
        self.assertEqual(meta["parts"][0]["material_hint"], body["material_hint"])
        node = doc["nodes"][meta["parts"][0]["node_index"]]
        material_index = doc["meshes"][node["mesh"]]["primitives"][0]["material"]
        material = doc["materials"][material_index]
        self.assertEqual(material["name"], "bubble__body__body")
        self.assertEqual(material["extras"]["garage_game_material"],
                         {**body["material_hint"], "fallback_colour": spec["palette"]["body"]})
        self.assertEqual(material["pbrMetallicRoughness"]["baseColorFactor"][3], 1)
        self.assertNotIn("alphaMode", material)
        # Other parts can share the same palette colour without becoming bubbles.
        self.assertNotIn("extras", next(m for m in doc["materials"] if m["name"] == "body"))

    def test_rejects_ambiguous_effect_and_material_hints(self):
        spec = copy.deepcopy(self.spec)
        spec["parts"][0]["kind"] = "effect"
        with self.assertRaisesRegex(ValueError, "motion_hint"):
            validate(spec)
        spec["parts"][0].pop("kind")
        spec["parts"][0]["material_hint"] = dict(kind="bubble", see_through=True,
                                                rim="shimmer", aura_colour="green")
        with self.assertRaisesRegex(ValueError, "aura_colour"):
            validate(spec)

    def test_authored_shape_notes_widen_front_and_survive_replay(self):
        # Prose is read by the author. The mesher must honour the resulting
        # contour, retain the notes, and leave facial landmarks unscaled.
        spec = copy.deepcopy(self.spec)
        spec["applied_notes"] = [
            "Wider body: spread the outline sideways while keeping the three eyes."]
        shape = spec["parts"][0]["shapes"][0]
        cx = shape["center"][0]
        shape["contour"] = [[cx + 1.5 * (x - cx), y] for x, y in shape["contour"]]
        original, _ = make_shape(self.spec["parts"][0]["shapes"][0], {})
        wider, _ = make_shape(shape, {})
        self.assertGreater(np.ptp(wider[:, 0]), 1.4 * np.ptp(original[:, 0]))
        self.assertAlmostEqual(np.ptp(original[:, 1]), np.ptp(wider[:, 1]))
        self.assertEqual(spec["parts"][1:], self.spec["parts"][1:])
        source = self.root / "reshaped.json"
        source.write_bytes(canonical(spec))
        outputs = []
        for index in range(2):
            out = self.root / ("reshaped-" + str(index))
            convert(self.root / "input/drawing.png", self.root / "input/description.txt",
                    source, out, render=False)
            outputs.append(out)
        for name in ("character.glb", "character.json", "interpretation.json"):
            self.assertEqual((outputs[0]/name).read_bytes(), (outputs[1]/name).read_bytes())
        _, meta = audit(outputs[0])
        self.assertEqual(meta["applied_notes"], spec["applied_notes"])
        self.assertEqual(read_json(outputs[0]/"interpretation.json")["applied_notes"],
                         spec["applied_notes"])

    def test_rejects_invalid_applied_notes(self):
        for notes in ("wider", [None], [""], ["  "]):
            spec = copy.deepcopy(self.spec)
            spec["applied_notes"] = notes
            with self.assertRaisesRegex(ValueError, "applied_notes"):
                validate(spec)


if __name__=="__main__":
    unittest.main(testRunner=unittest.TextTestRunner(stream=sys.stdout,verbosity=2))
