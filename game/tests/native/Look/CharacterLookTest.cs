using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GarageGame.Native.Look;
using FileAccess = Godot.FileAccess;

namespace GarageGame.Tests.Look;

/// <summary>
/// The characters' look, headless (Run 2, characters round, L8): each of the five GLBs under assets/characters is loaded from its
/// bytes (as RoomBuilder.LoadGlb loads a room's meshes) and given its look by CharacterLook.Apply. Checked: bubble parts get the
/// bubble shader with its late depth pass and cast no shadow, the hem ripples only the lowest fifth of the body; opaque parts keep
/// their exact GLB base colours as felt; ink marks are unshaded and dark (and dark parts that are not marks, such as a hat or a
/// horn, stay felt); effect parts emit brightly enough for the bloom and cast no shadow; the aura_color meta tints the rim and the
/// sparkles, Recolor follows it and one character's aura never reaches another's; nothing depends on a character's name (the same
/// look from the GLB alone, in a rig-like tree, under any name, and no character's name in the look's code); and every shader
/// uniform is set, read and declared. Rendering (how the bubble reads, its cost) needs a GPU: tools/look/character_sheet.ps1.
/// </summary>
public partial class CharacterLookTest : Node3D
{
    private int _checks;
    private int _failures;

    private static readonly string[] Characters = { "gubble", "potato_man", "tomato_man", "cloudpuff", "stickbear" };

    /// <summary>
    /// What the test expects, written from the models (not from the look's rules): the parts drawn as bubble, each part's number of
    /// ink surfaces, the effect parts and their surfaces, and dark surfaces that must stay felt because they are not marks.
    /// </summary>
    private sealed record Expected(string[] Bubble, Dictionary<string, int> Ink, Dictionary<string, int> Effect, string[] DarkButFelt);

    private static readonly Dictionary<string, Expected> Table = new()
    {
        ["gubble"] = new(new[] { "body", "arm_screen_right", "hand_screen_right" },
            new() { ["eye_screen_left"] = 2, ["eye_screen_right"] = 2, ["mouth"] = 1 },
            new() { ["floating_sparkle_0"] = 1, ["floating_sparkle_1"] = 1, ["floating_sparkle_2"] = 1, ["floating_sparkle_3"] = 1 },
            Array.Empty<string>()),
        ["potato_man"] = new(Array.Empty<string>(),
            new() { ["eye_screen_left"] = 2, ["eye_screen_right"] = 2, ["nostrils"] = 2, ["mouth"] = 1 },
            new(), new[] { "hat" }),
        ["tomato_man"] = new(Array.Empty<string>(),
            new() { ["eye_screen_left"] = 1, ["eye_screen_right"] = 1, ["mouth"] = 2 },
            new() { ["wand_sparkle_0"] = 1, ["wand_sparkle_1"] = 1, ["wand_sparkle_2"] = 1, ["floating_sparkle_0"] = 1, ["floating_sparkle_1"] = 1, ["floating_sparkle_2"] = 1, ["floating_sparkle_3"] = 1, ["floating_sparkle_4"] = 1 },
            new[] { "hat", "wand" }),
        ["cloudpuff"] = new(Array.Empty<string>(),
            new() { ["pupil_screen_left"] = 1, ["pupil_screen_right"] = 1, ["lid_screen_left"] = 1, ["lid_screen_right"] = 1, ["brow_screen_left"] = 1, ["brow_screen_right"] = 1, ["nose"] = 1, ["mouth"] = 1 },
            new(), Array.Empty<string>()),
        ["stickbear"] = new(Array.Empty<string>(),
            new() { ["eye_screen_left"] = 1, ["eye_screen_right"] = 1, ["mouth"] = 1 },
            new(), new[] { "horn_screen_left", "horn_screen_right" }),
    };

    private static readonly Color GubbleHintAura = new("b4eed8");

    public override void _Ready()
    {
        try
        {
            var repository = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
            GD.Print("CHARACTER_LOOK: glTF extras kept as meta: " + ExtrasNote(Load(repository, "gubble")));
            foreach (var id in Characters) CheckCharacter(repository, id);
            CheckAura(repository);
            CheckTorchProp(repository);
            CheckNoCrossTalk(repository);
            CheckNameIndependence(repository);
            CheckRigLikeTree(repository);
            CheckReapply(repository);
            CheckHem(repository);
            CheckShaders();
            CheckSharing();
            CheckSourcesNameNoCharacter(repository);
            GD.Print($"NATIVE_LOOK_CHARACTERS: {_checks - _failures}/{_checks} checks passed; the five characters' bubble, felt, ink and effect surfaces, exact colours, the aura and Recolor, the hem, no shadows from films and lights, no name-specific look, and the shaders' uniforms");
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (Exception exception)
        {
            GD.PushError("Character look test exception: " + exception);
            GetTree().Quit(1);
        }
    }

    // ---------- loading ----------

    private static string Folder(string repository, string id) => System.IO.Path.Combine(repository, "assets", "characters", id);

    /// <summary>A character's GLB from its bytes, as RoomBuilder.LoadGlb loads a room's meshes.</summary>
    private static Node3D Load(string repository, string id)
    {
        var bytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(Folder(repository, id), "character.glb"));
        var document = new GltfDocument();
        var state = new GltfState();
        var error = document.AppendFromBuffer(bytes, "", state);
        if (error != Error.Ok) throw new InvalidOperationException($"{id}: character.glb could not be read ({error})");
        return document.GenerateScene(state) as Node3D ?? throw new InvalidOperationException($"{id}: character.glb has no 3D scene");
    }

    private static IReadOnlyList<CharacterLookPart> Parts(string repository, string id) =>
        CharacterLook.PartsFromJson(System.IO.File.ReadAllText(System.IO.Path.Combine(Folder(repository, id), "character.json")));

    private static List<MeshInstance3D> Meshes(Node root) =>
        root.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>().Where(m => m.Mesh != null).ToList();

    private static string ExtrasNote(Node3D root)
    {
        var node = root.FindChild("body", true, false);
        var material = Meshes(root).Select(m => m.Mesh.SurfaceGetMaterial(0)).FirstOrDefault(m => m != null && m.ResourceName.StartsWith("bubble__", StringComparison.Ordinal));
        var text = $"node {(node != null && node.HasMeta("extras") ? "yes" : "no")}, material {(material != null && material.HasMeta("extras") ? "yes" : "no")}";
        root.Free();
        return text;
    }

    private static float LinearLuminance(Color colour)
    {
        var linear = colour.SrgbToLinear();
        return 0.2126f * linear.R + 0.7152f * linear.G + 0.0722f * linear.B;
    }

    // ---------- each character ----------

    private void CheckCharacter(string repository, string id)
    {
        var root = Load(repository, id);
        var expected = Table[id];
        var meshes = Meshes(root);
        var glb = meshes.SelectMany(m => Enumerable.Range(0, m.Mesh.GetSurfaceCount()).Select(i => (Mesh: m, Surface: i, Material: m.Mesh.SurfaceGetMaterial(i)))).ToList();
        var result = CharacterLook.Apply(root, Parts(repository, id));
        Check(result.Surfaces.Count == glb.Count && result.Unmapped.Count == 0, $"{id}: every surface of every part is treated ({result.Surfaces.Count} of {glb.Count}; unmapped {string.Join(",", result.Unmapped)})");
        Check(glb.All(s => s.Mesh.Mesh.SurfaceGetMaterial(s.Surface) == s.Material && s.Mesh.GetSurfaceOverrideMaterial(s.Surface) != null),
            $"{id}: the look is a surface override; the GLB's own materials stay on the mesh");

        // Bubble parts: the film, its late depth pass, no shadow, the fallback colour as the tint.
        var bubbles = result.Of(CharacterSurfaceRole.Bubble).ToList();
        Check(bubbles.Select(s => s.Part).ToHashSet().SetEquals(expected.Bubble), $"{id}: the bubble parts are {string.Join(",", expected.Bubble)} (got {string.Join(",", bubbles.Select(s => s.Part).Distinct())})");
        foreach (var s in bubbles)
        {
            var material = s.Material as ShaderMaterial;
            var depth = material?.NextPass as ShaderMaterial;
            Check(material?.Shader?.ResourcePath == MaterialLibrary.BubbleShaderPath && depth?.Shader?.ResourcePath == MaterialLibrary.BubbleDepthShaderPath
                && depth.RenderPriority == MaterialLibrary.BubbleDepthRenderPriority && material.RenderPriority == 0,
                $"{id} {s.Part}: the bubble shader, with its depth pass chained after it and drawn late");
            Check(s.Mesh.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off, $"{id} {s.Part}: a bubble casts no shadow");
            Check(material != null && material.GetShaderParameter("tint_color").AsColor() == ((BaseMaterial3D)s.Mesh.Mesh.SurfaceGetMaterial(s.Surface)).AlbedoColor,
                $"{id} {s.Part}: the film's tint is exactly the GLB's opaque fallback colour");
        }

        // Ink: unshaded, dark, exactly its own colour; dark parts that are not marks stay felt.
        var ink = result.Of(CharacterSurfaceRole.Ink).ToList();
        var inkCounts = ink.GroupBy(s => s.Part).ToDictionary(g => g.Key, g => g.Count());
        Check(inkCounts.Count == expected.Ink.Count && expected.Ink.All(e => inkCounts.TryGetValue(e.Key, out var n) && n == e.Value),
            $"{id}: the ink marks are {string.Join(",", expected.Ink.Select(e => $"{e.Key}x{e.Value}"))} (got {string.Join(",", inkCounts.Select(e => $"{e.Key}x{e.Value}"))})");
        foreach (var s in ink)
            Check(s.Material is StandardMaterial3D { ShadingMode: BaseMaterial3D.ShadingModeEnum.Unshaded } m && m.AlbedoColor == s.BaseColor && LinearLuminance(s.BaseColor) < 0.06f,
                $"{id} {s.Part}: ink is unshaded, dark ({LinearLuminance(s.BaseColor):0.000}) and exactly its own colour");
        foreach (var part in expected.DarkButFelt)
        {
            var surfaces = result.Surfaces.Where(s => s.Part == part).ToList();
            Check(surfaces.Count > 0 && surfaces.All(s => s.Role == CharacterSurfaceRole.Plain) && surfaces.Any(s => LinearLuminance(s.BaseColor) < 0.06f),
                $"{id} {part}: dark but not a mark, so felt rather than ink");
        }

        // Effects: emissive enough for the bloom (it starts at 0.9), no shadow.
        var effects = result.Of(CharacterSurfaceRole.Effect).ToList();
        var effectCounts = effects.GroupBy(s => s.Part).ToDictionary(g => g.Key, g => g.Count());
        Check(effectCounts.Count == expected.Effect.Count && expected.Effect.All(e => effectCounts.TryGetValue(e.Key, out var n) && n == e.Value),
            $"{id}: the effect parts are {string.Join(",", expected.Effect.Keys)} (got {string.Join(",", effectCounts.Keys)})");
        foreach (var s in effects)
        {
            var bright = s.Material is StandardMaterial3D { EmissionEnabled: true } m ? Mathf.Max(m.Emission.R, Mathf.Max(m.Emission.G, m.Emission.B)) * m.EmissionEnergyMultiplier : 0f;
            Check(bright > 0.9f * 1.5f && s.Material is StandardMaterial3D { DisableReceiveShadows: true }, $"{id} {s.Part}: an effect emits for the bloom ({bright:0.00} against 0.9)");
            Check(s.Mesh.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off, $"{id} {s.Part}: an effect casts no shadow");
        }

        // Felt: exactly the GLB's colour; opaque parts still cast shadows.
        var felt = result.Of(CharacterSurfaceRole.Plain).ToList();
        Check(felt.Count == glb.Count - bubbles.Count - ink.Count - effects.Count && (felt.Count > 0 || id == "gubble"), $"{id}: the rest ({felt.Count} surfaces) is felt (the Gubble has none since its torch became a prop)");
        foreach (var s in felt)
        {
            var source = (BaseMaterial3D)s.Mesh.Mesh.SurfaceGetMaterial(s.Surface);
            Check(s.Material is ShaderMaterial m && m.Shader?.ResourcePath == MaterialLibrary.CharacterFeltShaderPath && m.GetShaderParameter("base_color").AsColor() == source.AlbedoColor
                && s.BaseColor == source.AlbedoColor, $"{id} {s.Part}: felt in exactly the GLB's colour #{source.AlbedoColor.ToHtml(false)}");
        }
        var opaqueMeshes = meshes.Where(m => result.Surfaces.Any(s => s.Mesh == m && s.Role is CharacterSurfaceRole.Plain or CharacterSurfaceRole.Ink)).ToList();
        Check(opaqueMeshes.All(m => m.CastShadow == GeometryInstance3D.ShadowCastingSetting.On), $"{id}: opaque parts still cast shadows");
        Check(meshes.All(m => m.HasMeta(CharacterLook.PartMeta)), $"{id}: every mesh is marked with its part");
        root.Free();
    }

    // ---------- the aura ----------

    /// <summary>The Gubble's torch, a separate prop since the founder's note of 13 October: its flame glows in its own fire colours, its handle is felt.</summary>
    private void CheckTorchProp(string repository)
    {
        var folder = System.IO.Path.Combine(repository, "assets", "props", "torch");
        var document = new GltfDocument();
        var state = new GltfState();
        var read = document.AppendFromBuffer(System.IO.File.ReadAllBytes(System.IO.Path.Combine(folder, "character.glb")), "", state);
        Check(read == Error.Ok, "the torch prop's GLB reads from its bytes");
        if (read != Error.Ok || document.GenerateScene(state) is not Node3D root) return;
        var parts = CharacterLook.PartsFromJson(System.IO.File.ReadAllText(System.IO.Path.Combine(folder, "character.json")));
        var result = CharacterLook.Apply(root, parts);
        var flame = result.Of(CharacterSurfaceRole.Effect).ToList();
        Check(flame.Count == 2 && flame.All(s => s.Part == "torch_flame" && ((StandardMaterial3D)s.Material).Emission == s.BaseColor) &&
              result.Of(CharacterSurfaceRole.Plain).Any(s => s.Part == "torch") && result.Aura == null,
            "the torch prop: its flame's two surfaces glow in their own fire colours (no aura), its handle is felt");
        root.Free();
    }

    private void CheckAura(string repository)
    {
        var parts = Parts(repository, "gubble");
        var body = new Node3D { Name = "Body" };
        var root = Load(repository, "gubble");
        body.AddChild(root);
        var result = CharacterLook.Apply(root, parts);
        var bubble = (ShaderMaterial)result.Of(CharacterSurfaceRole.Bubble).First().Material;
        var sparkle = result.Of(CharacterSurfaceRole.Effect).First(s => s.Part.Contains("sparkle"));
        Check(result.Aura == GubbleHintAura && bubble.GetShaderParameter("aura_color").AsColor() == GubbleHintAura,
            "without an aura_color meta, the rim takes the hint's aura colour (#B4EED8)");
        Check(((StandardMaterial3D)sparkle.Material).Emission == CharacterLook.Tinted(sparkle.BaseColor, GubbleHintAura, CharacterEffectLook.Default.SparkleAuraTint),
            "without a meta, the sparkles lean toward the hint's aura");

        var violet = new Color("7a5cff");
        body.SetMeta(LookDirector.AuraColorMeta, violet);
        result = CharacterLook.Apply(root, parts);
        bubble = (ShaderMaterial)result.Of(CharacterSurfaceRole.Bubble).First().Material;
        sparkle = result.Of(CharacterSurfaceRole.Effect).First(s => s.Part.Contains("sparkle"));
        Check(result.Aura == violet && result.Of(CharacterSurfaceRole.Bubble).All(s => ((ShaderMaterial)s.Material).GetShaderParameter("aura_color").AsColor() == violet),
            "Apply reads the aura_color meta from the body above the visual root: every bubble's rim takes it");
        var sparkleColour = ((StandardMaterial3D)sparkle.Material).Emission;
        Check(Distance(sparkleColour, violet) < Distance(sparkle.BaseColor, violet) * 0.5f, "the aura tints the sparkles (most of the way)");

        var orange = new Color("ff9a3c");
        body.SetMeta(LookDirector.AuraColorMeta, orange);
        var changed = CharacterLook.Recolor(root);
        Check(changed == 3 + 4, $"Recolor recolours the three bubble surfaces and the four sparkles' surfaces ({changed})");
        Check(result.Of(CharacterSurfaceRole.Bubble).All(s => ((ShaderMaterial)s.Material).GetShaderParameter("aura_color").AsColor() == orange)
            && ((StandardMaterial3D)sparkle.Material).Emission == CharacterLook.Tinted(sparkle.BaseColor, orange, CharacterEffectLook.Default.SparkleAuraTint),
            "Recolor follows the meta: rim and sparkles in the new aura");
        CharacterLook.Recolor(root, aura: violet);
        Check(bubble.GetShaderParameter("aura_color").AsColor() == violet, "Recolor takes a colour given to it");
        body.RemoveMeta(LookDirector.AuraColorMeta);
        CharacterLook.Recolor(root);
        Check(bubble.GetShaderParameter("aura_color").AsColor() == GubbleHintAura
            && ((StandardMaterial3D)sparkle.Material).Emission == CharacterLook.Tinted(sparkle.BaseColor, GubbleHintAura, CharacterEffectLook.Default.SparkleAuraTint),
            "with the meta cleared, Recolor goes back to the character's own aura");
        body.SetMeta(LookDirector.AuraColorMeta, "not a colour");
        CharacterLook.Recolor(root);
        Check(bubble.GetShaderParameter("aura_color").AsColor() == GubbleHintAura, "a meta that is not a colour is ignored");
        Check(CharacterLook.SetLightLevel(root, 0.25f) == 3 && result.Of(CharacterSurfaceRole.Bubble).All(s => ((ShaderMaterial)s.Material).GetShaderParameter("light_level").AsSingle() == 0.25f)
            && CharacterLook.SetLightLevel(root, 7f) == 3 && bubble.GetShaderParameter("light_level").AsSingle() == 1f
            && CharacterLook.SetLightLevel(root, float.NaN) == 3 && bubble.GetShaderParameter("light_level").AsSingle() == 1f,
            "SetLightLevel tells every bubble how lit its place is, held to 0 to 1 (not a number: lit)");
        var other = new Node3D();
        other.SetMeta(LookDirector.AuraColorMeta, orange);
        CharacterLook.Recolor(root, other);
        Check(bubble.GetShaderParameter("aura_color").AsColor() == orange, "Recolor reads a body given to it");
        other.Free();
        body.Free();

        // A character without an aura keeps its own colours, and Recolor changes nothing on it.
        var potato = Load(repository, "potato_man");
        var potatoResult = CharacterLook.Apply(potato, Parts(repository, "potato_man"));
        Check(potatoResult.Aura == null && CharacterLook.Recolor(potato) == 0, "a character with no aura and no meta: no aura, and Recolor changes nothing");
        potato.Free();

        // Not the name: another character's sparkles keep their own colour without an aura, and take an aura its body carries.
        var tomatoBody = new Node3D();
        var tomato = Load(repository, "tomato_man");
        tomatoBody.AddChild(tomato);
        var tomatoResult = CharacterLook.Apply(tomato, Parts(repository, "tomato_man"));
        Check(tomatoResult.Of(CharacterSurfaceRole.Effect).All(s => ((StandardMaterial3D)s.Material).Emission == s.BaseColor), "sparkles with no aura keep exactly their own colour");
        tomatoBody.SetMeta(LookDirector.AuraColorMeta, violet);
        Check(CharacterLook.Recolor(tomato) == 8 && tomatoResult.Of(CharacterSurfaceRole.Effect).All(s => Distance(((StandardMaterial3D)s.Material).Emission, violet) < Distance(s.BaseColor, violet)),
            "any character's body that carries an aura tints its sparkles: the aura decides, not the name");
        tomatoBody.Free();
    }

    private static float Distance(Color a, Color b) => Mathf.Abs(a.R - b.R) + Mathf.Abs(a.G - b.G) + Mathf.Abs(a.B - b.B);

    private void CheckNoCrossTalk(string repository)
    {
        var a = Load(repository, "gubble");
        var b = Load(repository, "gubble");
        var parts = Parts(repository, "gubble");
        var ra = CharacterLook.Apply(a, parts);
        var rb = CharacterLook.Apply(b, parts);
        CharacterLook.Recolor(a, aura: new Color("ff0000"));
        Check(rb.Of(CharacterSurfaceRole.Bubble).All(s => ((ShaderMaterial)s.Material).GetShaderParameter("aura_color").AsColor() == GubbleHintAura)
            && rb.Of(CharacterSurfaceRole.Effect).All(s => ((StandardMaterial3D)s.Material).Emission == CharacterLook.Tinted(s.BaseColor, GubbleHintAura, CharacterEffectLook.Default.AuraTintFor(s.Part == "torch_flame" ? "flicker" : "twinkle"))),
            "recolouring one character never reaches another's bubble or sparkles");
        Check(ra.Of(CharacterSurfaceRole.Bubble).All(s => !rb.Surfaces.Any(o => ReferenceEquals(o.Material, s.Material))), "each character has its own bubble materials");
        a.Free();
        b.Free();
    }

    // ---------- nothing is name-specific ----------

    private static string Signature(CharacterLookResult result) =>
        string.Join(";", result.Surfaces.OrderBy(s => s.Part, StringComparer.Ordinal).ThenBy(s => s.Surface).Select(s => $"{s.Part}/{s.Surface}={s.Role}"));

    private void CheckNameIndependence(string repository)
    {
        foreach (var id in Characters)
        {
            var withParts = Load(repository, id);
            var fromJson = Signature(CharacterLook.Apply(withParts, Parts(repository, id)));
            withParts.Free();
            var bare = Load(repository, id);
            bare.Name = "someone_new";
            var fromGlb = CharacterLook.Apply(bare);
            Check(Signature(fromGlb) == fromJson && fromGlb.Unmapped.Count == 0, $"{id}: the GLB alone (its node and material names, any root name) gives the same look as its character.json");
            bare.Free();
        }
    }

    /// <summary>The rig's tree (K1): each part's mesh, renamed, under a node named for its part (a BoneAttachment3D there).</summary>
    private void CheckRigLikeTree(string repository)
    {
        foreach (var id in new[] { "gubble", "stickbear" })
        {
            var glb = Load(repository, id);
            var expected = Signature(CharacterLook.Apply(glb, Parts(repository, id)));
            var rig = new Node3D { Name = "CharacterBody" };
            var skeleton = new Node3D { Name = "Skeleton3D" };
            rig.AddChild(skeleton);
            foreach (var mesh in Meshes(glb))
            {
                var attachment = new Node3D { Name = mesh.Name, Transform = CharacterLook.RelativeTransform(glb, mesh) };
                var copy = new MeshInstance3D { Name = "Mesh", Mesh = mesh.Mesh };
                attachment.AddChild(copy);
                skeleton.AddChild(attachment);
            }
            glb.Free();
            var result = CharacterLook.Apply(rig, Parts(repository, id));
            Check(Signature(result) == expected && result.Unmapped.Count == 0, $"{id}: in a rig-like tree (meshes under nodes named for their parts) the look is the same");
            rig.Free();
        }
    }

    private void CheckReapply(string repository)
    {
        var root = Load(repository, "gubble");
        var parts = Parts(repository, "gubble");
        var first = CharacterLook.Apply(root, parts);
        var second = CharacterLook.Apply(root, parts);
        Check(Signature(first) == Signature(second) && second.Surfaces.Count == first.Surfaces.Count, "applying the look again gives the same look (it starts from the GLB's materials)");
        root.Free();
    }

    // ---------- the hem ----------

    private void CheckHem(string repository)
    {
        var root = Load(repository, "gubble");
        var result = CharacterLook.Apply(root, Parts(repository, "gubble"));
        var body = result.Of(CharacterSurfaceRole.Bubble).First(s => s.Part == "body");
        var box = body.Mesh.Mesh.GetAabb();
        var m = (ShaderMaterial)body.Material;
        var depth = (ShaderMaterial)m.NextPass;
        float P(ShaderMaterial s, string n) => s.GetShaderParameter(n).AsSingle();
        Check(Mathf.Abs(P(m, "hem_bottom_m") - box.Position.Y) < 1e-6f && Mathf.Abs(P(m, "hem_top_m") - P(m, "hem_bottom_m") - box.Size.Y * 0.2f) < 1e-6f,
            $"the hem is the lowest fifth of the body, in its own space ({P(m, "hem_bottom_m"):0.0000} to {P(m, "hem_top_m"):0.0000} m)");
        Check(new[] { "hem_bottom_m", "hem_top_m", "hem_amplitude_m", "hem_waves", "hem_speed" }.All(n => P(m, n) == P(depth, n))
            && m.GetShaderParameter("hem_center_m").AsVector2() == depth.GetShaderParameter("hem_center_m").AsVector2(),
            "the depth pass ripples the hem exactly as the colour pass does");
        var amplitude = P(m, "hem_amplitude_m");
        Check(amplitude > 0f && amplitude <= 0.02f * box.Size.Y && P(m, "hem_speed") > 0f && P(m, "hem_waves") == Mathf.Round(P(m, "hem_waves")),
            $"the hem's wave is gentle ({amplitude * 1000f:0.0} mm on a {box.Size.Y * 100f:0.0} cm body) and moves, a whole number of waves round");
        foreach (var s in result.Of(CharacterSurfaceRole.Bubble).Where(s => s.Part != "body"))
        {
            var material = (ShaderMaterial)s.Material;
            Check(s.Mesh.Mesh.GetAabb().Position.Y >= P(material, "hem_top_m"), $"{s.Part}: above the hem, so it does not ripple");
        }
        root.Free();
    }

    // ---------- shaders ----------

    private static string Source(Shader shader) => Regex.Replace(Regex.Replace(shader.Code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    private static string Function(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return "";
        var depth = 0;
        for (var i = source.IndexOf('{', start); i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }
        return "";
    }

    private void CheckUniforms(string path, IReadOnlyCollection<string> setNames)
    {
        var shader = GD.Load<Shader>(path);
        var declared = shader.GetShaderUniformList().Select(u => u.AsGodotDictionary()["name"].AsString()).ToHashSet();
        var set = setNames.ToHashSet();
        var name = System.IO.Path.GetFileName(path);
        Check(declared.Count >= 6, $"{name} lists its uniforms ({declared.Count}): it parses");
        Check(set.Except(declared).ToArray() is { Length: 0 }, $"{name}: every parameter set is a uniform: not declared {string.Join(",", set.Except(declared))}");
        Check(declared.Except(set).ToArray() is { Length: 0 }, $"{name}: every uniform is set, none left at its default: unset {string.Join(",", declared.Except(set))}");
        var source = Source(shader);
        var body = Regex.Replace(source, @"^\s*uniform\s+[^;]*;", "", RegexOptions.Multiline);
        var ignored = declared.Where(n => !Regex.IsMatch(body, $@"\b{n}\b")).ToArray();
        Check(ignored.Length == 0, $"{name} reads every uniform it declares: ignored {string.Join(",", ignored)}");
    }

    private void CheckShaders()
    {
        CheckUniforms(MaterialLibrary.CharacterFeltShaderPath, MaterialLibrary.FeltParameterNames);
        CheckUniforms(MaterialLibrary.BubbleShaderPath, MaterialLibrary.BubbleParameterNames);
        CheckUniforms(MaterialLibrary.BubbleDepthShaderPath, MaterialLibrary.BubbleDepthParameterNames);
        var bubble = Source(GD.Load<Shader>(MaterialLibrary.BubbleShaderPath));
        var depth = Source(GD.Load<Shader>(MaterialLibrary.BubbleDepthShaderPath));
        var felt = Source(GD.Load<Shader>(MaterialLibrary.CharacterFeltShaderPath));
        string Modes(string s) => Regex.Match(s, @"render_mode([^;]*);").Groups[1].Value;
        Check(Modes(bubble).Contains("depth_draw_never") && Modes(bubble).Contains("cull_disabled") && Modes(bubble).Contains("blend_premul_alpha") && !Modes(bubble).Contains("world_vertex_coords"),
            "the bubble draws both skins, see-through, writes no depth itself and works in object space");
        // The budget (0.15 ms at 1080p): lit, the forward pass's lights, soft shadows and VoxelGI cones cost the film 0.54 ms at a
        // play distance on the founder's card (docs/look/CHARACTERS.md); unshaded, it follows the place's light through light_level.
        Check(Modes(bubble).Contains("unshaded") && Modes(bubble).Contains("shadows_disabled") && Regex.IsMatch(Function(bubble, "void fragment()"), @"\blight_level\b"),
            "the bubble is unshaded for its budget and takes the place's light from light_level");
        Check(Modes(depth).Contains("depth_draw_always") && Modes(depth).Contains("cull_back") && Modes(depth).Contains("unshaded") && !Modes(depth).Contains("world_vertex_coords")
            && Regex.IsMatch(Function(depth, "void fragment()"), @"<\s*rim_depth_min\)\s*discard") && Regex.IsMatch(Function(depth, "void fragment()"), @"ALPHA\s*=\s*0\.0"),
            "the depth pass writes the front skin's depth at the rim only and draws no colour");
        var waveA = Regex.Replace(Function(bubble, "vec3 hem_wave("), @"\s+", " ");
        var waveB = Regex.Replace(Function(depth, "vec3 hem_wave("), @"\s+", " ");
        Check(waveA.Length > 50 && waveA == waveB && Regex.IsMatch(Function(bubble, "void vertex()"), @"VERTEX\s*=\s*hem_wave\(VERTEX\)")
            && Regex.IsMatch(Function(depth, "void vertex()"), @"VERTEX\s*=\s*hem_wave\(VERTEX\)"), "both passes ripple the hem with the same function, in object space");
        // The budget's reason: a fixed handful of arithmetic per pixel, no textures and no loops.
        Check(!Regex.IsMatch(bubble + depth, @"\b(texture|textureLod|texelFetch|for|while)\b") && !bubble.Contains("hint_screen_texture") && !bubble.Contains("hint_depth_texture"),
            "the bubble samples no texture (no screen or depth copy) and has no loops");
        Check(Regex.Matches(felt, @"\bALBEDO\s*=").Count == 1 && Regex.IsMatch(felt, @"\bALBEDO\s*=\s*base_color\s*;"), "the felt's albedo is exactly its base colour, nowhere changed");
        var light = Function(felt, "void light()");
        Check(Regex.IsMatch(felt, @"ROUGHNESS\s*=\s*1\.0") && Regex.IsMatch(felt, @"SPECULAR\s*=\s*0\.0") && Regex.Matches(light, @"SPECULAR_LIGHT\s*\+=").Count == 1
            && Regex.IsMatch(light, @"SPECULAR_LIGHT\s*\+=[^;]*rim_light") && !light.Contains("halfway"), "felt is matte: no highlight, only the rim from lights behind");
        Check(!Regex.IsMatch(felt, @"\bEMISSION\b") && Modes(felt).Contains("depth_draw_opaque"), "felt is opaque and lit only by real light (nothing glows)");
    }

    private void CheckSharing()
    {
        var colour = new Color("8a7a66");
        Check(ReferenceEquals(MaterialLibrary.ForCharacterFelt(colour), MaterialLibrary.ForCharacterFelt(colour))
            && ReferenceEquals(MaterialLibrary.ForCharacterInk(colour), MaterialLibrary.ForCharacterInk(colour)), "felt and ink materials are shared per colour");
        Check(!ReferenceEquals(MaterialLibrary.ForCharacterFelt(colour), MaterialLibrary.ForCharacterFelt(new Color("8a7a67"))), "a different colour is a different felt");
        Check(!ReferenceEquals(MaterialLibrary.ForBubble(colour, colour, BubbleHem.None), MaterialLibrary.ForBubble(colour, colour, BubbleHem.None))
            && !ReferenceEquals(MaterialLibrary.ForCharacterEffect(colour, 2f), MaterialLibrary.ForCharacterEffect(colour, 2f)), "bubbles and effects are each character's own");
        Check(CharacterLook.IsMarkName("eye_screen_left") && CharacterLook.IsMarkName("straps_screen_right") && !CharacterLook.IsMarkName("hat")
            && !CharacterLook.IsMarkName("body") && CharacterLook.IsEffectName("wand_sparkle_2") && CharacterLook.IsEffectName("torch_flame"), "the name grammar reads the converter's part words");
        var light = new CharacterLookPart("eye_screen_left", Role: "ink");
        Check(CharacterLook.RoleOf(light, null, new Color("eeeeee"), null) == CharacterSurfaceRole.Plain
            && CharacterLook.RoleOf(light, null, new Color("111111"), null) == CharacterSurfaceRole.Ink
            && CharacterLook.RoleOf(new CharacterLookPart("eye_screen_left", Role: "plain"), null, new Color("111111"), null) == CharacterSurfaceRole.Plain
            && CharacterLook.RoleOf(new CharacterLookPart("lantern", Role: "effect"), null, new Color("eeeeee"), null) == CharacterSurfaceRole.Effect
            && CharacterLook.RoleOf(new CharacterLookPart("cape", Role: "bubble"), null, new Color("eeeeee"), null) == CharacterSurfaceRole.Bubble,
            "a caller's role decides (an ink role still draws a light surface as felt)");
    }

    private void CheckSourcesNameNoCharacter(string repository)
    {
        var files = new[]
        {
            "game/scripts/native/Look/CharacterLook.cs", "game/scripts/native/Look/CharacterLookParts.cs", "game/scripts/native/Look/MaterialLibrary.cs",
            "game/shaders/bubble.gdshader", "game/shaders/character_bubble_depth.gdshader", "game/shaders/character_felt.gdshader",
        };
        var names = new Regex(@"gubble|potato|tomato|cloudpuff|stickbear", RegexOptions.IgnoreCase);
        foreach (var file in files)
        {
            var path = System.IO.Path.Combine(repository, file);
            var text = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "";
            var code = Regex.Replace(Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
            Check(text.Length > 0 && !names.IsMatch(code), $"{file}: no character's name in its code");
        }
    }

    private void Check(bool condition, string label)
    {
        _checks++;
        if (condition) return;
        _failures++;
        GD.PushError("Character look: " + label);
    }
}
