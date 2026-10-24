using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using GarageGame.Native.Characters;

namespace GarageGame.Tests.Characters;

/// <summary>Malformed folders, written to a temporary root, each refused with a reason; and synthetic converter output that loads.</summary>
public partial class CharacterRigTest
{
    private void TestMalformedFolders()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"enfractal-k1-characters-{System.Environment.ProcessId}");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        try
        {
            var gubbleJson = Godot.FileAccess.GetFileAsBytes("res://characters/gubble/character.json");
            var gubbleGlb = Godot.FileAccess.GetFileAsBytes("res://characters/gubble/character.glb");
            JsonNode Gubble() => JsonNode.Parse(gubbleJson)!;
            void Folder(string id, byte[]? json, byte[]? glb, string? rig = null)
            {
                var folder = System.IO.Path.Combine(root, id);
                Directory.CreateDirectory(folder);
                if (json != null) File.WriteAllBytes(System.IO.Path.Combine(folder, "character.json"), json);
                if (glb != null) File.WriteAllBytes(System.IO.Path.Combine(folder, "character.glb"), glb);
                if (rig != null) File.WriteAllText(System.IO.Path.Combine(folder, "rig.json"), rig);
            }
            byte[] Text(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());
            JsonNode Part(JsonNode doc, string name) => doc["parts"]!.AsArray().First(p => (string)p!["name"]! == name)!;

            var expected = new Dictionary<string, string>();
            void Expect(string id, string reason) => expected[id] = reason;

            Folder("a_copy", gubbleJson, gubbleGlb);
            Folder("bad_json", gubbleJson[..(gubbleJson.Length / 2)], gubbleGlb);
            Expect("bad_json", "malformed JSON");
            var motion = Gubble(); motion["motion"]!["kind"] = "swim";
            Folder("unknown_motion", Text(motion), gubbleGlb);
            Expect("unknown_motion", "unknown motion kind \"swim\"");
            var effect = Gubble(); Part(effect, "floating_sparkle_0")["motion_hint"]!["kind"] = "spin";
            Folder("unknown_effect", Text(effect), gubbleGlb);
            Expect("unknown_effect", "unknown effect hint \"spin\"");
            var noParts = Gubble(); noParts["parts"] = new JsonArray();
            Folder("missing_parts", Text(noParts), gubbleGlb);
            Expect("missing_parts", "parts is empty");
            Folder("missing_glb", gubbleJson, null);
            Expect("missing_glb", "character.glb is missing");
            var orphan = Gubble(); Part(orphan, "mouth")["parent"] = "nobody";
            Folder("missing_parent", Text(orphan), gubbleGlb);
            Expect("missing_parent", "\"nobody\" is missing or comes after its child");
            var tall = Gubble(); tall["height_m"] = 0.12;
            Folder("wrong_height", Text(tall), gubbleGlb);
            Expect("wrong_height", "exactly 0.10 m tall");
            var infinite = Gubble(); Part(infinite, "mouth")["pivot_m"]![0] = 424242.0;
            Folder("nonfinite", Encoding.UTF8.GetBytes(infinite.ToJsonString().Replace("424242", "1e999")), gubbleGlb);
            Expect("nonfinite", "pivot_m[0] is not a finite number");
            var duplicate = Encoding.UTF8.GetString(gubbleJson).Replace("\"name\": \"the Gubble\"", "\"name\": \"the Gubble\", \"name\": \"twice\"");
            Folder("duplicate_key", Encoding.UTF8.GetBytes(duplicate), gubbleGlb);
            Expect("duplicate_key", "repeats the key \"name\"");
            var moved = Gubble();
            var mouth = Part(moved, "mouth");
            mouth["local_pivot_m"]![0] = (double)mouth["local_pivot_m"]![0]! + 0.001;
            mouth["pivot_m"]![0] = (double)mouth["pivot_m"]![0]! + 0.001;
            Folder("node_mismatch", Text(moved), gubbleGlb);
            Expect("node_mismatch", "the part mouth sits away from its local_pivot_m");
            var lied = Gubble(); lied["triangles"] = 16000;
            Folder("triangle_mismatch", Text(lied), gubbleGlb);
            Expect("triangle_mismatch", "triangles, but character.json says 16000");
            Folder("bad_sidecar", gubbleJson, gubbleGlb, "{\"schema\": \"garage_game.character.rig\", \"version\": 1, \"wiggle\": 2}");
            Expect("bad_sidecar", "rig.json has an unknown key \"wiggle\"");
            Folder("sidecar_unknown_part", gubbleJson, gubbleGlb, "{\"schema\": \"garage_game.character.rig\", \"version\": 1, \"rigid\": [\"tail\"]}");
            Expect("sidecar_unknown_part", "names \"tail\", which is not a part");
            Folder("sidecar_bad_role", gubbleJson, gubbleGlb, "{\"schema\": \"garage_game.character.rig\", \"version\": 1, \"roles\": {\"aura\": \"halo_ring\"}}");
            Expect("sidecar_bad_role", "not a known role");
            Folder("sidecar_out_of_range", gubbleJson, gubbleGlb, "{\"schema\": \"garage_game.character.rig\", \"version\": 1, \"profile\": {\"squash\": 9}}");
            Expect("sidecar_out_of_range", "rig.json.profile.squash is 9, outside 0 to 4");
            Folder("Bad Name", gubbleJson, gubbleGlb);
            Expect("Bad Name", "is not a character id");

            var (overJson, overGlb) = Synthetic(70_001);
            Folder("too_many_triangles", overJson, overGlb);
            Expect("too_many_triangles", "70,001 triangles, over the 70,000-triangle limit");
            var (atJson, atGlb) = Synthetic(70_000);
            Folder("at_triangle_limit", atJson, atGlb);
            var (externalJson, externalGlb) = Synthetic(60, externalUri: true);
            Folder("external_file", externalJson, externalGlb);
            Expect("external_file", "must be self-contained");
            var (crowdJson, crowdGlb) = Synthetic(4, extraParts: 63);
            Folder("too_many_parts", crowdJson, crowdGlb);
            Expect("too_many_parts", "64-bone budget");
            // Review major 1: the vertices a GLB makes the reader copy are bounded, not only its triangles. Primitives sharing one
            // POSITION accessor each copy it whole: 48 primitives of one triangle naming 5,000 vertices each is 240,000 vertices.
            var (sharedJson, sharedGlb) = SharedVertices(parts: 6, primitivesPerPart: 8, vertices: 5_000);
            Folder("shared_vertices", sharedJson, sharedGlb);
            Expect("shared_vertices", "240,000 vertices in all");
            var (unusedJson, unusedGlb) = SharedVertices(parts: 1, primitivesPerPart: 1, vertices: 250_000);
            Folder("unused_vertices", unusedJson, unusedGlb);
            Expect("unused_vertices", "over the 210,000-vertex limit");
            var (withinJson, withinGlb) = SharedVertices(parts: 2, primitivesPerPart: 2, vertices: 5_000);
            Folder("shared_within", withinJson, withinGlb);
            var (kiteJson, kiteGlb) = Synthetic(48);
            Folder("synthetic_kite", kiteJson, kiteGlb,
                "{\"schema\": \"garage_game.character.rig\", \"version\": 1, \"roles\": {\"wing_screen_left\": \"arm\"}, \"rigid\": [\"hat\"], \"chains\": {\"leg_screen_left\": {\"enabled\": false}}}");

            var library = CharacterLibrary.Load(root);
            Check(library.Entries.Count == expected.Count + 4, $"every fixture folder is listed ({library.Entries.Count})");
            foreach (var (id, reason) in expected)
            {
                var entry = library.Entry(id);
                Check(entry != null && !entry.IsLoaded && entry.RefusalReason!.Contains(reason, StringComparison.Ordinal),
                    $"{id} is refused because {reason} (got: {entry?.RefusalReason ?? "loaded"})");
                Check(library.Build(id) == null, $"{id} builds no body: the caller keeps the prototype");
            }
            Check(library.Entry("a_copy")?.IsLoaded == true && library.Entry("at_triangle_limit")?.IsLoaded == true,
                "a copied character and one at exactly 70,000 triangles load from an absolute path: " + library.Entry("at_triangle_limit")?.RefusalReason);
            Check(library.Entry("shared_within")?.IsLoaded == true,
                "primitives may share an accessor while the vertices they copy stay within the limit: " + library.Entry("shared_within")?.RefusalReason);
            Check(library.Build(null) == null && library.Build("nobody_here") == null, "no id or an unknown id builds no body");
            Check(CharacterRig.Build(System.IO.Path.Combine(root, "unknown_motion")) == null &&
                  !CharacterRig.TryBuild(System.IO.Path.Combine(root, "unknown_motion"), out _, out var why) && why!.Contains("unknown motion kind"),
                "CharacterRig.Build(folder) refuses with the same reason");

            // Any converter output moves: the synthetic kite gets roles, chains and IK from its names and hints alone.
            var kite = library.Build("synthetic_kite");
            Check(kite != null, "the synthetic character loads: " + library.Entry("synthetic_kite")?.RefusalReason);
            if (kite != null)
            {
                var roles = string.Join(" ", kite.Bones.Skip(1).Select(b => $"{b.Name}:{PartRoles.Token(b.Role)}"));
                Check(roles == "body:body leg_screen_left:leg foot_screen_left:foot hat:hat wing_screen_left:arm floating_sparkle_0:sparkle",
                    "the synthetic kite's roles come from its names, and rig.json renames the wing an arm: " + roles);
                var chains = string.Join(" ", kite.Secondary.Chains.Select(c => kite.Bones[c.RootBone].Name));
                Check(chains == "floating_sparkle_0" && kite.Notes.Any(n => n.Contains("removes the DanglingLeg chain at leg_screen_left")),
                    "rig.json removes the leg's chain and keeps the rigid hat off a spring: " + chains);
                Check(kite.Legs.Count == 1 && kite.Bones[kite.FindBone("hat")].IsRigid && kite.Profile.HasLegs && kite.Look.HeadIsBody,
                    "the synthetic kite has one leg IK, a rigid hat, legs in its profile and its body as its head");
                var hat = kite.FindBone("hat");
                kite.Pose.Reset();
                kite.Pose.Begin(PoseLayer.Gait);
                kite.Pose.SetRotation(hat, Quaternion.FromEuler(new Vector3(0, 0, 0.5f)));
                Check(kite.Pose.GetRotation(hat) == Quaternion.Identity && kite.Pose.RigidWrites == 1, "a rigid part ignores pose writes");
                kite.Free();
            }
            var at = library.Build("at_triangle_limit");
            Check(at != null && at.Definition.Triangles == 70_000 && at.Profile.HeightM is > 0.0999f and < 0.1001f, "the 70,000-triangle character builds, 0.10 m tall");
            at?.Free();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// A character whose every primitive names one shared POSITION accessor of <paramref name="vertices"/> vertices (a box from
    /// the ground to 0.10 m) and one shared index accessor of a single triangle: a body and <paramref name="parts"/> - 1 marks at
    /// its joint, each mesh with <paramref name="primitivesPerPart"/> primitives. Valid in every other way, so only the vertex
    /// bound can refuse it.
    /// </summary>
    private static (byte[] Json, byte[] Glb) SharedVertices(int parts, int primitivesPerPart, int vertices)
    {
        var low = new Vector3(-0.02f, -0.05f, -0.01f);
        var high = new Vector3(0.02f, 0.05f, 0.01f);
        var pivot = new Vector3(0, 0.05f, 0);
        var binary = new byte[vertices * 12 + 12];
        var at = 0;
        void Put(float f) { BitConverter.TryWriteBytes(binary.AsSpan(at), f); at += 4; }
        for (var k = 0; k < vertices; k++)
        {
            var t = k / (float)Math.Max(1, vertices - 1);
            var v = k == 0 ? low : k == 1 ? high : new Vector3(Mathf.Lerp(low.X, high.X, t), (k & 1) == 0 ? low.Y * 0.5f : high.Y * 0.5f, 0);
            Put(v.X); Put(v.Y); Put(v.Z);
        }
        foreach (var index in new uint[] { 0, 2, 1 }) { BitConverter.TryWriteBytes(binary.AsSpan(at), index); at += 4; }
        var meshes = new JsonArray();
        var nodes = new JsonArray { new JsonObject { ["name"] = "character_root", ["children"] = new JsonArray(1) } };
        var metaParts = new JsonArray();
        for (var p = 0; p < parts; p++)
        {
            var name = p == 0 ? "body" : $"mark_{p}";
            var primitives = new JsonArray();
            for (var q = 0; q < primitivesPerPart; q++)
                primitives.Add(new JsonObject { ["attributes"] = new JsonObject { ["POSITION"] = 0 }, ["indices"] = 1, ["mode"] = 4, ["material"] = 0 });
            meshes.Add(new JsonObject { ["name"] = name, ["primitives"] = primitives });
            var local = p == 0 ? pivot : Vector3.Zero;
            var node = new JsonObject { ["name"] = name, ["translation"] = new JsonArray(local.X, local.Y, local.Z), ["mesh"] = p };
            if (p == 0 && parts > 1) node["children"] = new JsonArray(Enumerable.Range(2, parts - 1).Select(i => (JsonNode)i).ToArray());
            nodes.Add(node);
            metaParts.Add(new JsonObject
            {
                ["kind"] = "solid", ["local_pivot_m"] = new JsonArray(local.X, local.Y, local.Z), ["name"] = name, ["node_index"] = p + 1,
                ["parent"] = p == 0 ? null : "body", ["pivot_m"] = new JsonArray(pivot.X, pivot.Y, pivot.Z), ["role"] = "shared " + name,
            });
        }
        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "Garage Game review fix test" },
            ["scene"] = 0, ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
            ["nodes"] = nodes, ["meshes"] = meshes,
            ["accessors"] = new JsonArray(
                new JsonObject { ["bufferView"] = 0, ["componentType"] = 5126, ["count"] = vertices, ["type"] = "VEC3" },
                new JsonObject { ["bufferView"] = 1, ["componentType"] = 5125, ["count"] = 3, ["type"] = "SCALAR" }),
            ["bufferViews"] = new JsonArray(
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = 0, ["byteLength"] = vertices * 12 },
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = vertices * 12, ["byteLength"] = 12 }),
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = binary.Length }),
            ["materials"] = new JsonArray(new JsonObject { ["name"] = "body", ["pbrMetallicRoughness"] = new JsonObject { ["baseColorFactor"] = new JsonArray(0.8, 0.6, 0.4, 1.0) } }),
        };
        var json = Encoding.UTF8.GetBytes(gltf.ToJsonString()).ToList();
        while (json.Count % 4 != 0) json.Add((byte)' ');
        using var glb = new MemoryStream();
        using (var writer = new BinaryWriter(glb, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x46546C67u);
            writer.Write(2u);
            writer.Write((uint)(12 + 8 + json.Count + 8 + binary.Length));
            writer.Write((uint)json.Count);
            writer.Write(0x4E4F534Au);
            writer.Write(json.ToArray());
            writer.Write((uint)binary.Length);
            writer.Write(0x004E4942u);
            writer.Write(binary);
        }
        var hex = new string('0', 64);
        var top = pivot + high;
        var bottom = pivot + low;
        var character = new JsonObject
        {
            ["bounds_m"] = new JsonObject { ["max"] = new JsonArray(top.X, top.Y, top.Z), ["min"] = new JsonArray(bottom.X, bottom.Y, bottom.Z) },
            ["collision_hint"] = new JsonObject { ["height_m"] = 0.1, ["kind"] = "capsule", ["radius_m"] = 0.02, ["visual_overhang"] = false },
            ["depth_m"] = high.Z - low.Z, ["forward"] = "-Z", ["height_m"] = 0.1,
            ["motion"] = new JsonObject { ["kind"] = "waddle", ["reason"] = "A test box" },
            ["name"] = "A shared-vertex box", ["parts"] = metaParts,
            ["provenance"] = new JsonObject { ["description_sha256"] = hex, ["interpretation_sha256"] = hex, ["photo_sha256"] = hex, ["photo_size_px"] = new JsonArray(800, 1000) },
            ["root_pivot_m"] = new JsonArray(0, 0, 0), ["schema"] = "garage_game.character.draft", ["triangles"] = parts * primitivesPerPart,
            ["uncertainties"] = new JsonArray("Synthetic"), ["units"] = "metres", ["up"] = "+Y", ["version"] = 1, ["width_m"] = high.X - low.X,
        };
        return (Encoding.UTF8.GetBytes(character.ToJsonString()), glb.ToArray());
    }

    /// <summary>
    /// A converter-shaped character of <paramref name="triangles"/> triangles in all: a body (a zigzag strip from the
    /// ground to 0.10 m), a leg, a foot, a hat, a wing and a drifting sparkle, or <paramref name="extraParts"/> more parts.
    /// </summary>
    private static (byte[] Json, byte[] Glb) Synthetic(int triangles, bool externalUri = false, int extraParts = 0)
    {
        var bodyTriangles = triangles - (extraParts == 0 ? 5 * 8 : 0);
        var parts = new List<(string Name, string? Parent, Vector3 Pivot, Vector3 Low, Vector3 High, int Triangles, string Kind, string? Hint)>
        {
            ("body", null, new Vector3(0, 0.05f, 0), new Vector3(-0.02f, -0.05f, -0.01f), new Vector3(0.02f, 0.05f, 0.01f), bodyTriangles, "solid", null),
        };
        if (extraParts == 0)
        {
            parts.Add(("leg_screen_left", "body", new Vector3(0.01f, 0.03f, 0), new Vector3(-0.002f, -0.02f, -0.002f), new Vector3(0.002f, 0, 0.002f), 8, "solid", null));
            parts.Add(("foot_screen_left", "leg_screen_left", new Vector3(0.01f, 0.008f, 0), new Vector3(-0.004f, -0.008f, -0.006f), new Vector3(0.004f, 0, 0.002f), 8, "solid", null));
            parts.Add(("hat", "body", new Vector3(0, 0.09f, 0), new Vector3(-0.01f, 0, -0.01f), new Vector3(0.01f, 0.01f, 0.01f), 8, "solid", null));
            parts.Add(("wing_screen_left", "body", new Vector3(0.02f, 0.06f, 0), new Vector3(0, -0.01f, -0.002f), new Vector3(0.03f, 0.01f, 0.002f), 8, "solid", null));
            parts.Add(("floating_sparkle_0", "body", new Vector3(-0.03f, 0.08f, 0), new Vector3(-0.002f, -0.002f, -0.001f), new Vector3(0.002f, 0.002f, 0.001f), 8, "effect", "drift"));
        }
        for (var i = 0; i < extraParts; i++)
            parts.Add(($"mark_{i}", "body", new Vector3(0, 0.05f, -0.01f), new Vector3(-0.001f, -0.001f, -0.001f), new Vector3(0.001f, 0.001f, 0.001f), 2, "solid", null));

        var binary = new List<byte>();
        var accessors = new JsonArray();
        var views = new JsonArray();
        var meshes = new JsonArray();
        var nodes = new JsonArray { new JsonObject { ["name"] = "character_root", ["children"] = new JsonArray() } };
        var metaParts = new JsonArray();
        var low = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var high = -low;
        var total = 0;
        for (var p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            var count = part.Triangles + 2;
            var positionsStart = binary.Count;
            var vmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var vmax = -vmin;
            for (var k = 0; k < count; k++)
            {
                var t = k / (float)(count - 1);
                var v = new Vector3(Mathf.Lerp(part.Low.X, part.High.X, t), (k & 1) == 0 ? part.Low.Y : part.High.Y, (k % 3) switch { 0 => part.Low.Z, 1 => part.High.Z, _ => 0.5f * (part.Low.Z + part.High.Z) });
                if (k == 0) v = part.Low;
                if (k == count - 1) v = part.High;
                foreach (var f in new[] { v.X, v.Y, v.Z }) binary.AddRange(BitConverter.GetBytes(f));
                vmin = vmin.Min(v);
                vmax = vmax.Max(v);
            }
            var indicesStart = binary.Count;
            for (var k = 0; k < part.Triangles; k++)
                foreach (var index in (k & 1) == 0 ? new uint[] { (uint)k, (uint)k + 1, (uint)k + 2 } : new uint[] { (uint)k + 1, (uint)k, (uint)k + 2 })
                    binary.AddRange(BitConverter.GetBytes(index));
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = positionsStart, ["byteLength"] = indicesStart - positionsStart });
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = indicesStart, ["byteLength"] = binary.Count - indicesStart });
            accessors.Add(new JsonObject
            {
                ["bufferView"] = views.Count - 2, ["componentType"] = 5126, ["count"] = count, ["type"] = "VEC3",
                ["min"] = new JsonArray(vmin.X, vmin.Y, vmin.Z), ["max"] = new JsonArray(vmax.X, vmax.Y, vmax.Z),
            });
            accessors.Add(new JsonObject { ["bufferView"] = views.Count - 1, ["componentType"] = 5125, ["count"] = part.Triangles * 3, ["type"] = "SCALAR" });
            meshes.Add(new JsonObject
            {
                ["name"] = part.Name,
                ["primitives"] = new JsonArray(new JsonObject { ["attributes"] = new JsonObject { ["POSITION"] = accessors.Count - 2 }, ["indices"] = accessors.Count - 1, ["mode"] = 4, ["material"] = 0 }),
            });
            var parentIndex = part.Parent == null ? -1 : parts.FindIndex(x => x.Name == part.Parent);
            var parentPivot = parentIndex < 0 ? Vector3.Zero : parts[parentIndex].Pivot;
            var local = part.Pivot - parentPivot;
            nodes.Add(new JsonObject { ["name"] = part.Name, ["translation"] = new JsonArray(local.X, local.Y, local.Z), ["mesh"] = p });
            nodes[parentIndex + 1]!.AsObject().TryAdd("children", new JsonArray());
            nodes[parentIndex + 1]!["children"]!.AsArray().Add(p + 1);
            var meta = new JsonObject
            {
                ["kind"] = part.Kind, ["local_pivot_m"] = new JsonArray(local.X, local.Y, local.Z), ["name"] = part.Name, ["node_index"] = p + 1,
                ["parent"] = part.Parent, ["pivot_m"] = new JsonArray(part.Pivot.X, part.Pivot.Y, part.Pivot.Z), ["role"] = "synthetic " + part.Name,
            };
            if (part.Hint != null) meta["motion_hint"] = new JsonObject { ["kind"] = part.Hint, ["reason"] = "a test sparkle" };
            metaParts.Add(meta);
            low = low.Min(part.Pivot + vmin);
            high = high.Max(part.Pivot + vmax);
            total += part.Triangles;
        }
        while (binary.Count % 4 != 0) binary.Add(0);
        var buffer = new JsonObject { ["byteLength"] = binary.Count };
        if (externalUri) buffer["uri"] = "../../secrets.bin";
        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "Garage Game K1 test" },
            ["scene"] = 0, ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
            ["nodes"] = nodes, ["meshes"] = meshes, ["accessors"] = accessors, ["bufferViews"] = views, ["buffers"] = new JsonArray(buffer),
            ["materials"] = new JsonArray(new JsonObject { ["name"] = "body", ["pbrMetallicRoughness"] = new JsonObject { ["baseColorFactor"] = new JsonArray(0.8, 0.6, 0.4, 1.0) } }),
        };
        var json = Encoding.UTF8.GetBytes(gltf.ToJsonString()).ToList();
        while (json.Count % 4 != 0) json.Add((byte)' ');
        var glb = new List<byte>();
        glb.AddRange(BitConverter.GetBytes(0x46546C67u));
        glb.AddRange(BitConverter.GetBytes(2u));
        glb.AddRange(BitConverter.GetBytes((uint)(12 + 8 + json.Count + 8 + binary.Count)));
        glb.AddRange(BitConverter.GetBytes((uint)json.Count));
        glb.AddRange(BitConverter.GetBytes(0x4E4F534Au));
        glb.AddRange(json);
        glb.AddRange(BitConverter.GetBytes((uint)binary.Count));
        glb.AddRange(BitConverter.GetBytes(0x004E4942u));
        glb.AddRange(binary);

        var hex = new string('0', 64);
        var character = new JsonObject
        {
            ["bounds_m"] = new JsonObject { ["max"] = new JsonArray(high.X, high.Y, high.Z), ["min"] = new JsonArray(low.X, low.Y, low.Z) },
            ["collision_hint"] = new JsonObject { ["height_m"] = 0.1, ["kind"] = "capsule", ["radius_m"] = 0.02, ["visual_overhang"] = false },
            ["depth_m"] = high.Z - low.Z, ["forward"] = "-Z", ["height_m"] = 0.1,
            ["motion"] = new JsonObject { ["kind"] = "hop", ["reason"] = "A test kite with one leg" },
            ["name"] = "A synthetic kite", ["parts"] = metaParts,
            ["provenance"] = new JsonObject { ["description_sha256"] = hex, ["interpretation_sha256"] = hex, ["photo_sha256"] = hex, ["photo_size_px"] = new JsonArray(800, 1000) },
            ["root_pivot_m"] = new JsonArray(0, 0, 0), ["schema"] = "garage_game.character.draft", ["triangles"] = total,
            ["uncertainties"] = new JsonArray("Synthetic"), ["units"] = "metres", ["up"] = "+Y", ["version"] = 1, ["width_m"] = high.X - low.X,
        };
        return (Encoding.UTF8.GetBytes(character.ToJsonString()), glb.ToArray());
    }
}
