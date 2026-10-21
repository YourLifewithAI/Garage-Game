using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;

namespace GarageGame.Native.Characters;

/// <summary>
/// Reads one character folder the way the converter writes it (pipeline/characters/conv_a/FORMAT.md and convert.py):
/// character.json checked key by key, character.glb checked against it node by node, the optional rig.json, then
/// the meshes built straight from the verified GLB bytes (never Godot's importer, never a second read from disk; see
/// <see cref="GlbReader"/>). Any problem refuses the folder with a reason.
/// </summary>
public static class CharacterFolder
{
    public const string Schema = "enfractal.character.draft";
    public const int Version = 1;
    public const float HeightM = 0.10f;
    public const int MaxTriangles = 70_000;
    /// <summary>Bones per character (the root and one per part).</summary>
    public const int MaxBones = 64;
    public const int MaxJsonBytes = 1024 * 1024;

    /// <summary>A character id or part name: <c>[a-z][a-z0-9_]{0,63}</c>.</summary>
    public static bool IsToken(string text)
    {
        if (text.Length is 0 or > 64 || text[0] is < 'a' or > 'z') return false;
        foreach (var c in text)
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')) return false;
        return true;
    }

    private static bool IsHex(string text, int length, bool upperAllowed)
    {
        if (text.Length != length) return false;
        foreach (var c in text)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')) && !(upperAllowed && c is >= 'A' and <= 'F')) return false;
        return true;
    }

    /// <summary>Reads and validates <paramref name="folder"/>; throws <see cref="CharacterLoadException"/> with the reason.</summary>
    public static CharacterDefinition Read(string folder, string id)
    {
        if (!IsToken(id)) throw new CharacterLoadException($"the folder name \"{StrictJson.Clip(id)}\" is not a character id ([a-z][a-z0-9_], up to 64)");
        folder = folder.TrimEnd('/');
        var jsonBytes = ReadFile(folder + "/character.json", "character.json", MaxJsonBytes);
        var glbBytes = ReadFile(folder + "/character.glb", "character.glb", GlbReader.MaxBytes);
        using var document = StrictJson.Parse(jsonBytes, "character.json", MaxJsonBytes);
        var meta = ParseMetadata(document.RootElement);
        var glb = GlbReader.Read(glbBytes, MaxTriangles);
        CrossCheck(meta, glb);

        var sidecarPath = folder + "/rig.json";
        var sidecar = RigSidecar.Empty;
        if (Godot.FileAccess.FileExists(sidecarPath))
        {
            var names = new List<string>();
            foreach (var part in meta.Parts) names.Add(part.Name);
            sidecar = RigSidecar.Parse(ReadFile(sidecarPath, "rig.json", RigSidecar.MaxBytes), names);
        }

        BuildMeshes(glbBytes, glb, meta.Parts);
        CheckMeasuredBounds(meta);
        return new CharacterDefinition
        {
            Id = id, DisplayName = meta.Name, Folder = folder, Motion = meta.Motion, RootPivotM = meta.RootPivot,
            HeightM = meta.Height, WidthM = meta.Width, DepthM = meta.Depth, BoundsM = meta.Bounds, Triangles = meta.Triangles,
            Parts = meta.Parts, GlbSha256 = Convert.ToHexString(SHA256.HashData(glbBytes)).ToLowerInvariant(), Sidecar = sidecar,
        };
    }

    private sealed class Metadata
    {
        public string Name = "";
        public MotionKind Motion;
        public Vector3 RootPivot;
        public float Height, Width, Depth;
        public Aabb Bounds;
        public int Triangles;
        public List<PartDefinition> Parts = new();
    }

    private static byte[] ReadFile(string path, string label, int maxBytes)
    {
        if (!Godot.FileAccess.FileExists(path)) throw new CharacterLoadException($"{label} is missing");
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file == null) throw new CharacterLoadException($"{label} could not be opened ({Godot.FileAccess.GetOpenError()})");
        var length = file.GetLength();
        if (length > (ulong)maxBytes) throw new CharacterLoadException($"{label} is {length} bytes, over the {maxBytes}-byte limit");
        var bytes = file.GetBuffer((long)length);
        if ((ulong)bytes.Length != length) throw new CharacterLoadException($"{label} could not be read whole");
        return bytes;
    }

    private static Metadata ParseMetadata(JsonElement root)
    {
        const string label = "character.json";
        StrictJson.Keys(root, label,
            new[] { "schema", "version", "name", "units", "up", "forward", "root_pivot_m", "height_m", "width_m", "depth_m", "bounds_m",
                "triangles", "parts", "motion", "collision_hint", "provenance", "uncertainties" },
            new[] { "applied_notes" });
        if (StrictJson.String(root.GetProperty("schema"), "schema", 64) != Schema) throw new CharacterLoadException($"{label}: schema must be \"{Schema}\"");
        if (StrictJson.Integer(root.GetProperty("version"), "version", 0, 1000) != Version) throw new CharacterLoadException($"{label}: version must be {Version}");
        var meta = new Metadata { Name = StrictJson.String(root.GetProperty("name"), "name", 200) };
        if (meta.Name.Length == 0) throw new CharacterLoadException($"{label}: name must not be empty");
        if (StrictJson.String(root.GetProperty("units"), "units", 16) != "metres" || StrictJson.String(root.GetProperty("up"), "up", 4) != "+Y" ||
            StrictJson.String(root.GetProperty("forward"), "forward", 4) != "-Z")
            throw new CharacterLoadException($"{label}: units, up and forward must be metres, +Y and -Z");
        meta.RootPivot = StrictJson.Vector(root.GetProperty("root_pivot_m"), "root_pivot_m", 1);
        var height = StrictJson.Number(root.GetProperty("height_m"), "height_m");
        if (Math.Abs(height - HeightM) > 1e-6)
            throw new CharacterLoadException(FormattableString.Invariant($"{label}: height_m is {height:0.######} m; every character stands exactly 0.10 m tall"));
        meta.Height = (float)height;
        meta.Width = (float)StrictJson.Number(root.GetProperty("width_m"), "width_m", 1e-4, 1);
        meta.Depth = (float)StrictJson.Number(root.GetProperty("depth_m"), "depth_m", 1e-4, 1);
        var bounds = root.GetProperty("bounds_m");
        StrictJson.Keys(bounds, "bounds_m", new[] { "min", "max" }, System.Array.Empty<string>());
        var min = StrictJson.Vector(bounds.GetProperty("min"), "bounds_m.min", 1);
        var max = StrictJson.Vector(bounds.GetProperty("max"), "bounds_m.max", 1);
        if (Math.Abs(min.Y) > 1e-6 || Math.Abs(max.Y - HeightM) > 1e-6)
            throw new CharacterLoadException(FormattableString.Invariant($"{label}: bounds_m spans y {min.Y:0.######} to {max.Y:0.######} m; the feet stand at y=0 and the top at 0.10 m"));
        if (!(min.X < max.X && min.Z < max.Z)) throw new CharacterLoadException($"{label}: bounds_m min must be below max");
        meta.Bounds = new Aabb(min, max - min);
        meta.Triangles = StrictJson.Integer(root.GetProperty("triangles"), "triangles", 1, int.MaxValue);
        if (meta.Triangles > MaxTriangles)
            throw new CharacterLoadException(FormattableString.Invariant($"{label}: {meta.Triangles:N0} triangles, over the {MaxTriangles:N0}-triangle limit per character"));

        var motion = root.GetProperty("motion");
        StrictJson.Keys(motion, "motion", new[] { "kind", "reason" }, System.Array.Empty<string>());
        var kind = StrictJson.String(motion.GetProperty("kind"), "motion.kind", 32);
        meta.Motion = kind switch
        {
            "float" => MotionKind.Float, "hop" => MotionKind.Hop, "waddle" => MotionKind.Waddle, "stride" => MotionKind.Stride,
            _ => throw new CharacterLoadException($"{label}: unknown motion kind \"{StrictJson.Clip(kind)}\" (float, hop, waddle or stride)"),
        };
        StrictJson.String(motion.GetProperty("reason"), "motion.reason", 2000);

        var collision = root.GetProperty("collision_hint");
        StrictJson.Keys(collision, "collision_hint", new[] { "kind", "height_m", "radius_m", "visual_overhang" }, System.Array.Empty<string>());
        if (StrictJson.String(collision.GetProperty("kind"), "collision_hint.kind", 16) != "capsule") throw new CharacterLoadException($"{label}: collision_hint.kind must be capsule");
        StrictJson.Number(collision.GetProperty("height_m"), "collision_hint.height_m", 0.01, 1);
        StrictJson.Number(collision.GetProperty("radius_m"), "collision_hint.radius_m", 0.001, 1);
        StrictJson.Bool(collision.GetProperty("visual_overhang"), "collision_hint.visual_overhang");

        var provenance = root.GetProperty("provenance");
        StrictJson.Keys(provenance, "provenance", new[] { "description_sha256", "interpretation_sha256", "photo_sha256", "photo_size_px" }, System.Array.Empty<string>());
        foreach (var key in new[] { "description_sha256", "interpretation_sha256", "photo_sha256" })
            if (!IsHex(StrictJson.String(provenance.GetProperty(key), "provenance." + key, 64), 64, false)) throw new CharacterLoadException($"{label}: provenance.{key} must be a SHA-256 in lowercase hex");
        var size = StrictJson.Array(provenance.GetProperty("photo_size_px"), "provenance.photo_size_px", 2, 2);
        StrictJson.Integer(size[0], "provenance.photo_size_px[0]", 1, 1_000_000);
        StrictJson.Integer(size[1], "provenance.photo_size_px[1]", 1, 1_000_000);
        StrictJson.Strings(root.GetProperty("uncertainties"), "uncertainties", 256, 4000);
        if (root.TryGetProperty("applied_notes", out var notes)) StrictJson.Strings(notes, "applied_notes", 256, 4000, nonEmpty: true);

        var parts = StrictJson.Array(root.GetProperty("parts"), "parts", 0, 1000);
        if (parts.GetArrayLength() == 0) throw new CharacterLoadException($"{label}: parts is empty; a character needs at least its body");
        if (parts.GetArrayLength() > MaxBones - 1)
            throw new CharacterLoadException($"{label}: {parts.GetArrayLength()} parts, over the rig's {MaxBones}-bone budget (the root and one bone per part)");
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var nodeIndices = new HashSet<int>();
        var i = 0;
        foreach (var part in parts.EnumerateArray())
        {
            var path = new JsonPath("parts", i);
            StrictJson.Keys(part, path, new[] { "name", "role", "parent", "node_index", "pivot_m", "local_pivot_m", "kind" }, new[] { "motion_hint", "material_hint" });
            var name = StrictJson.String(part.GetProperty("name"), path.Key("name"), 64);
            if (!IsToken(name) || name == "character_root") throw new CharacterLoadException($"{label}: {path}.name \"{StrictJson.Clip(name)}\" is not a part name");
            if (indexByName.ContainsKey(name)) throw new CharacterLoadException($"{label}: the part name \"{name}\" appears twice");
            path = new JsonPath("parts." + name);
            var roleText = StrictJson.String(part.GetProperty("role"), path.Key("role"), 400);
            var parentValue = part.GetProperty("parent");
            var parent = -1;
            if (parentValue.ValueKind != JsonValueKind.Null)
            {
                var parentName = StrictJson.String(parentValue, path.Key("parent"), 64);
                if (!indexByName.TryGetValue(parentName, out parent))
                    throw new CharacterLoadException($"{label}: {path}.parent \"{StrictJson.Clip(parentName)}\" is missing or comes after its child");
            }
            var nodeIndex = StrictJson.Integer(part.GetProperty("node_index"), path.Key("node_index"), 1, 128);
            if (!nodeIndices.Add(nodeIndex)) throw new CharacterLoadException($"{label}: {path}.node_index {nodeIndex} is used twice");
            var pivot = StrictJson.Vector(part.GetProperty("pivot_m"), path.Key("pivot_m"), 1);
            var local = StrictJson.Vector(part.GetProperty("local_pivot_m"), path.Key("local_pivot_m"), 1);
            var expected = (parent >= 0 ? meta.Parts[parent].PivotM : meta.RootPivot) + local;
            if ((expected - pivot).Length() > 1e-6f)
                throw new CharacterLoadException($"{label}: {path}.pivot_m is not its parent's pivot plus local_pivot_m");
            var kindText = StrictJson.String(part.GetProperty("kind"), path.Key("kind"), 16);
            var partKind = kindText switch
            {
                "solid" => PartKind.Solid, "effect" => PartKind.Effect,
                _ => throw new CharacterLoadException($"{label}: {path}.kind \"{StrictJson.Clip(kindText)}\" must be solid or effect"),
            };
            var effect = EffectMotion.None;
            if (part.TryGetProperty("motion_hint", out var hint))
            {
                StrictJson.Keys(hint, path.Key("motion_hint"), new[] { "kind", "reason" }, System.Array.Empty<string>());
                var hintKind = StrictJson.String(hint.GetProperty("kind"), path.Key("motion_hint").Key("kind"), 32);
                effect = hintKind switch
                {
                    "twinkle" => EffectMotion.Twinkle, "orbit" => EffectMotion.Orbit, "drift" => EffectMotion.Drift,
                    "flicker" => EffectMotion.Flicker, "pulse" => EffectMotion.Pulse,
                    _ => throw new CharacterLoadException($"{label}: {path} has an unknown effect hint \"{StrictJson.Clip(hintKind)}\" (twinkle, orbit, drift, flicker or pulse)"),
                };
                StrictJson.String(hint.GetProperty("reason"), path.Key("motion_hint").Key("reason"), 2000);
            }
            else if (partKind == PartKind.Effect)
            {
                throw new CharacterLoadException($"{label}: {path} is an effect with no motion_hint");
            }
            BubbleHint? bubble = null;
            if (part.TryGetProperty("material_hint", out var material))
            {
                StrictJson.Keys(material, path.Key("material_hint"), new[] { "kind", "see_through", "rim", "aura_colour" }, System.Array.Empty<string>());
                var materialKind = StrictJson.String(material.GetProperty("kind"), path.Key("material_hint").Key("kind"), 32);
                if (materialKind != "bubble") throw new CharacterLoadException($"{label}: {path} has an unknown material hint \"{StrictJson.Clip(materialKind)}\"");
                var aura = StrictJson.String(material.GetProperty("aura_colour"), path.Key("material_hint").Key("aura_colour"), 7);
                if (!StrictJson.Bool(material.GetProperty("see_through"), path.Key("material_hint").Key("see_through")) ||
                    StrictJson.String(material.GetProperty("rim"), path.Key("material_hint").Key("rim"), 16) != "shimmer" || aura.Length != 7 || aura[0] != '#' || !IsHex(aura[1..], 6, true))
                    throw new CharacterLoadException($"{label}: {path}'s bubble hint needs see_through, a shimmer rim and an #RRGGBB aura_colour");
                bubble = new BubbleHint(new Color(aura));
            }
            indexByName[name] = i;
            meta.Parts.Add(new PartDefinition
            {
                Index = i, Name = name, RoleText = roleText, Parent = parent, NodeIndex = nodeIndex, PivotM = pivot, LocalPivotM = local,
                Kind = partKind, Effect = effect, Bubble = bubble,
            });
            i++;
        }
        return meta;
    }

    private static void CrossCheck(Metadata meta, GlbReader glb)
    {
        const string label = "character.glb";
        if (glb.SceneRoots.Length != 1 || glb.SceneRoots[0] != 0 || glb.Nodes[0].Name != "character_root")
            throw new CharacterLoadException($"{label} must have one scene whose root is the node character_root");
        if (glb.Nodes[0].HasTransformBeyondTranslation || (glb.Nodes[0].Translation - Vector3.Zero).Length() > 1e-6f)
            throw new CharacterLoadException($"{label}: character_root must sit at the origin");
        if (glb.Nodes.Count != meta.Parts.Count + 1)
            throw new CharacterLoadException($"{label} has {glb.Nodes.Count} nodes; character.json describes {meta.Parts.Count} parts and the root");
        var glbParent = new int[glb.Nodes.Count];
        System.Array.Fill(glbParent, -1);
        for (var n = 0; n < glb.Nodes.Count; n++)
            foreach (var child in glb.Nodes[n].Children)
            {
                if (child == 0 || glbParent[child] != -1) throw new CharacterLoadException($"{label}: its node tree is not a tree");
                glbParent[child] = n;
            }
        var meshUsers = new HashSet<int>();
        foreach (var part in meta.Parts)
        {
            if (part.NodeIndex >= glb.Nodes.Count) throw new CharacterLoadException($"{label} has no node {part.NodeIndex} for the part {part.Name}");
            var node = glb.Nodes[part.NodeIndex];
            if (node.Name != part.Name) throw new CharacterLoadException($"{label}: node {part.NodeIndex} is \"{StrictJson.Clip(node.Name)}\", but character.json says {part.Name}");
            if (node.HasTransformBeyondTranslation) throw new CharacterLoadException($"{label}: the part {part.Name} is rotated or scaled; parts carry translations only");
            if ((node.Translation - part.LocalPivotM).Length() > 1e-6f) throw new CharacterLoadException($"{label}: the part {part.Name} sits away from its local_pivot_m");
            var expectedParent = part.Parent >= 0 ? meta.Parts[part.Parent].NodeIndex : 0;
            if (glbParent[part.NodeIndex] != expectedParent) throw new CharacterLoadException($"{label}: the part {part.Name} hangs from a different parent than character.json says");
            if (node.Mesh >= 0 && !meshUsers.Add(node.Mesh)) throw new CharacterLoadException($"{label}: two parts share one mesh");
        }
        if (glb.Triangles != meta.Triangles)
            throw new CharacterLoadException($"{label} has {glb.Triangles} triangles, but character.json says {meta.Triangles}");
    }

    private static void BuildMeshes(byte[] bytes, GlbReader glb, List<PartDefinition> parts)
    {
        foreach (var part in parts)
        {
            var meshIndex = glb.Nodes[part.NodeIndex].Mesh;
            if (meshIndex < 0) continue;
            var mesh = glb.BuildMesh(bytes, meshIndex, part.Name);
            part.Mesh = mesh;
            part.MeshBounds = mesh.GetAabb();
            part.Triangles = glb.MeshTriangles[meshIndex];
        }
    }

    /// <summary>The meshes themselves must stand 0.10 m tall with the feet at y=0, as character.json says.</summary>
    private static void CheckMeasuredBounds(Metadata meta)
    {
        var any = false;
        var low = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var high = -low;
        foreach (var part in meta.Parts)
        {
            if (part.Mesh == null) continue;
            var bounds = part.MeshBounds;
            if (!float.IsFinite(bounds.Position.X + bounds.Position.Y + bounds.Position.Z + bounds.Size.X + bounds.Size.Y + bounds.Size.Z))
                throw new CharacterLoadException($"character.glb: the part {part.Name} has non-finite vertices");
            low = low.Min(part.PivotM + bounds.Position);
            high = high.Max(part.PivotM + bounds.End);
            any = true;
        }
        if (!any) throw new CharacterLoadException("character.glb has no meshes");
        if (Math.Abs(low.Y) > 1e-5f || Math.Abs(high.Y - HeightM) > 1e-5f)
            throw new CharacterLoadException(FormattableString.Invariant($"character.glb's meshes span y {low.Y:0.######} to {high.Y:0.######} m; the feet stand at y=0 and the top at 0.10 m"));
        if ((low - meta.Bounds.Position).Length() > 1e-5f || (high - meta.Bounds.End).Length() > 1e-5f)
            throw new CharacterLoadException("character.glb's meshes do not match character.json's bounds_m");
    }
}
