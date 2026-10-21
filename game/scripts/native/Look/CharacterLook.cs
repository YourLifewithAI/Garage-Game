using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GarageGame.Native.Look;

/// <summary>
/// The felt (character_felt.gdshader): a soft wrapped terminator, a velvet sheen and a little rim from lights behind the figure,
/// fine fibres and tufts in the light (never the colour). Numbers in code for this round, as the glow's are (docs/look/CHARACTERS.md).
/// </summary>
public sealed record FeltLook(
    float Wrap, float TerminatorWarmth, float Sheen, float SheenPower, float RimLight, float RimPower,
    float FuzzStrength, float FuzzScaleM, float TuftScaleM, float MarkFadeStart, float MarkFadeEnd)
{
    public static readonly FeltLook Default = new(0.45f, 0.2f, 0.3f, 3f, 0.6f, 3.5f, 0.18f, 0.0012f, 0.0045f, 0.15f, 0.45f);
}

/// <summary>
/// The soap bubble (bubble.gdshader). RimGlow is the share of the rim that stays in the dark (the aura's own light); Glint and
/// GlintSize the drawn glint. HemFraction is the share of the lowest bubble part's height that ripples (a fifth); the hem's height
/// is in metres in object space. DefaultAura is used only when neither the body's aura_color nor the hint names a colour.
/// RimDepthMin is the Fresnel rim above which the depth pass writes the outline's depth.
/// </summary>
public sealed record BubbleLook(
    float TintAlpha, float RimPower, float RimAlpha, float RimGlow, float Shimmer, float ShimmerScaleM, float ShimmerSpeed,
    float Iridescence, float FilmBands, float BackSkin, float Glint, float GlintSize,
    float HemFraction, float HemAmplitudeM, float HemWaves, float HemSpeed, float RimDepthMin, Color DefaultAura)
{
    public static readonly BubbleLook Default = new(
        0.08f, 2.4f, 0.6f, 0.35f, 0.35f, 0.025f, 0.25f,
        0.45f, 1.4f, 0.55f, 0.6f, 0.06f,
        0.2f, 0.0012f, 3f, 0.4f, 0.3f, new Color("b4eed8"));
}

/// <summary>
/// Effect parts: how bright their emission is (the look's bloom starts at 0.9) and how far an aura tints them, by motion hint:
/// a flame ("flicker") keeps most of its fire, a sparkle or a glow ("twinkle", "orbit", "drift", "pulse") takes most of the aura.
/// </summary>
public sealed record CharacterEffectLook(float SparkleEnergy, float SparkleAuraTint, float FlameEnergy, float FlameAuraTint)
{
    public static readonly CharacterEffectLook Default = new(1.8f, 0.65f, 2.2f, 0.3f);

    public bool IsFlame(string? motionHint) => string.Equals(motionHint, "flicker", StringComparison.OrdinalIgnoreCase);
    public float EnergyFor(string? motionHint) => IsFlame(motionHint) ? FlameEnergy : SparkleEnergy;
    public float AuraTintFor(string? motionHint) => IsFlame(motionHint) ? FlameAuraTint : SparkleAuraTint;
}

public sealed record CharacterLookTuning(FeltLook Felt, BubbleLook Bubble, CharacterEffectLook Effects)
{
    public static readonly CharacterLookTuning Default = new(FeltLook.Default, BubbleLook.Default, CharacterEffectLook.Default);
}

/// <summary>The hem's band in a bubble part's own space: full wave at BottomM, none from TopM up, about CenterM (x, z).</summary>
public readonly record struct BubbleHem(float BottomM, float TopM, Vector2 CenterM)
{
    public static readonly BubbleHem None = new(0f, 0f, Vector2.Zero);
}

/// <summary>One surface the look treated: the mesh, its surface index, the part, the role, the GLB's colour and the material it now wears.</summary>
public readonly record struct CharacterSurface(MeshInstance3D Mesh, int Surface, string Part, CharacterSurfaceRole Role, Color BaseColor, Material Material);

/// <summary>What CharacterLook.Apply did: every surface it treated, and the aura colour it used (null when the character has none).</summary>
public sealed class CharacterLookResult
{
    public CharacterLookResult(IReadOnlyList<CharacterSurface> surfaces, Color? aura, IReadOnlyList<string> unmapped)
    {
        Surfaces = surfaces;
        Aura = aura;
        Unmapped = unmapped;
    }

    public IReadOnlyList<CharacterSurface> Surfaces { get; }
    public Color? Aura { get; }
    /// <summary>Meshes no part was found for (they were drawn as felt by their own name).</summary>
    public IReadOnlyList<string> Unmapped { get; }
    public int Count(CharacterSurfaceRole role) => Surfaces.Count(s => s.Role == role);
    public IEnumerable<CharacterSurface> Of(CharacterSurfaceRole role) => Surfaces.Where(s => s.Role == role);
}

/// <summary>
/// The look of a drawn character (Run 2, characters round, L8; docs/look/CHARACTERS.md). Apply gives every mesh surface under a
/// character's visual root its treatment by part kind and hint: bubble parts the soap-bubble film (no shadow), effect parts an
/// emissive glow the bloom catches (tinted by the aura when the character has one), dark marks unshaded ink, and everything else
/// felt in exactly the GLB's colour. It works on any node tree whose MeshInstance3Ds can be mapped to part names: a mesh named for
/// its part (the GLB as loaded), or under a node named for it (the rig's BoneAttachment3D). The aura colour is the body's
/// aura_color meta (LookDirector.AuraColorMeta) when set, else the bubble hint's aura_colour; Recolor follows a change.
/// Nothing here depends on a character's name: everything comes from part names, kinds, hints and the GLB's materials.
/// Surface overrides carry the treatment, so the GLB's own materials stay on the mesh underneath.
/// </summary>
public static partial class CharacterLook
{
    /// <summary>The meta each treated material carries: its role ("plain", "ink", "effect", "bubble").</summary>
    public const string RoleMeta = "character_look";
    /// <summary>The meta each treated mesh carries: the part it was mapped to.</summary>
    public const string PartMeta = "character_part";
    /// <summary>The meta on the visual root holding the character's own aura (the hint's), used when the body sets none.</summary>
    public const string AuraDefaultMeta = "character_aura_default";
    private const string BaseMeta = "character_base_color";
    private const string TintMeta = "character_aura_tint";

    /// <summary>
    /// Give a loaded character its look. parts: the character's parts (CharacterLook.PartsFromJson of its character.json, or the
    /// rig's, with roles); null reads them from the GLB's own nodes. body: the node whose aura_color meta tints the bubble's rim and
    /// the effects; null looks on the visual root and its ancestors. Calling it again redoes the look from the GLB's materials.
    /// </summary>
    public static CharacterLookResult Apply(Node3D visualRoot, IEnumerable<CharacterLookPart>? parts = null, Node? body = null, CharacterLookTuning? tuning = null)
    {
        var look = tuning ?? CharacterLookTuning.Default;
        var byName = new Dictionary<string, CharacterLookPart>(StringComparer.Ordinal);
        foreach (var part in parts ?? PartsFromNodes(visualRoot)) byName.TryAdd(part.Name, part);
        var meshes = Meshes(visualRoot).ToList();

        // The character's own aura: the first bubble hint's aura colour, from the parts or the GLB's materials.
        var hintAura = byName.Values.Select(p => p.MaterialHint?.AuraColour).FirstOrDefault(c => c != null)
            ?? meshes.SelectMany(m => Enumerable.Range(0, m.Mesh.GetSurfaceCount()).Select(i => HintOf(m.Mesh.SurfaceGetMaterial(i))?.AuraColour)).FirstOrDefault(c => c != null);
        if (hintAura is { } own) visualRoot.SetMeta(AuraDefaultMeta, own);
        else if (visualRoot.HasMeta(AuraDefaultMeta)) visualRoot.RemoveMeta(AuraDefaultMeta);
        var aura = AuraOf(body, visualRoot) ?? hintAura;

        // Pass one: the role of every surface.
        var unmapped = new List<string>();
        var plan = new List<(MeshInstance3D Mesh, int Surface, CharacterLookPart Part, CharacterSurfaceRole Role, Color Base, CharacterMaterialHint? Hint)>();
        foreach (var mesh in meshes)
        {
            var part = PartFor(mesh, visualRoot, byName);
            if (part == null)
            {
                unmapped.Add(mesh.Name);
                part = new CharacterLookPart(mesh.Name, Kind: null);
            }
            mesh.SetMeta(PartMeta, part.Name);
            for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                var source = mesh.Mesh.SurfaceGetMaterial(surface);
                var baseColor = source is BaseMaterial3D standard ? standard.AlbedoColor : new Color(0.7f, 0.7f, 0.7f);
                var hint = part.MaterialHint ?? HintOf(source);
                plan.Add((mesh, surface, part, RoleOf(part, source, baseColor, hint), baseColor, hint));
            }
        }

        // The hem: the lowest fifth of the lowest bubble part, in the visual root's space.
        var bubbleMeshes = plan.Where(p => p.Role == CharacterSurfaceRole.Bubble).Select(p => p.Mesh).Distinct().ToList();
        var hemRoot = HemBand(visualRoot, bubbleMeshes, look.Bubble);

        // Pass two: the materials.
        var surfaces = new List<CharacterSurface>();
        foreach (var (mesh, surface, part, role, baseColor, hint) in plan)
        {
            Material material = role switch
            {
                // The GLB's base colour is the opaque fallback (FORMAT.md), the film's light tint.
                CharacterSurfaceRole.Bubble => MaterialLibrary.ForBubble(baseColor, aura ?? look.Bubble.DefaultAura, HemFor(visualRoot, mesh, hemRoot), look.Bubble),
                CharacterSurfaceRole.Effect => EffectMaterial(baseColor, aura, part.MotionHint, look.Effects),
                CharacterSurfaceRole.Ink => MaterialLibrary.ForCharacterInk(baseColor),
                _ => MaterialLibrary.ForCharacterFelt(baseColor, look.Felt),
            };
            if (role == CharacterSurfaceRole.Bubble) material.SetMeta(AuraDefaultMeta, hintAura ?? look.Bubble.DefaultAura);
            mesh.SetSurfaceOverrideMaterial(surface, material);
            surfaces.Add(new CharacterSurface(mesh, surface, part.Name, role, baseColor, material));
        }

        // A film and a light cast no shadow: a part that is all bubble or all effect casts none.
        foreach (var mesh in meshes)
        {
            var roles = surfaces.Where(s => s.Mesh == mesh).Select(s => s.Role).ToList();
            if (roles.Count > 0 && roles.All(r => r is CharacterSurfaceRole.Bubble or CharacterSurfaceRole.Effect))
                mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        }
        return new CharacterLookResult(surfaces, aura, unmapped);
    }

    /// <summary>
    /// Follow a change of aura: the bubble's rim and the effects take the body's aura_color meta now (or aura, when given; the
    /// character's own hint colour when neither is set). Returns how many materials changed colour.
    /// </summary>
    public static int Recolor(Node3D visualRoot, Node? body = null, Color? aura = null, CharacterLookTuning? tuning = null)
    {
        var look = tuning ?? CharacterLookTuning.Default;
        var hintAura = visualRoot.HasMeta(AuraDefaultMeta) ? visualRoot.GetMeta(AuraDefaultMeta).AsColor() : (Color?)null;
        var now = aura ?? AuraOf(body, visualRoot) ?? hintAura;
        var changed = 0;
        foreach (var mesh in Meshes(visualRoot))
            for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                var material = mesh.GetSurfaceOverrideMaterial(surface);
                if (material == null || !material.HasMeta(RoleMeta)) continue;
                switch (material.GetMeta(RoleMeta).AsString())
                {
                    case "bubble" when material is ShaderMaterial bubble:
                        var rim = now ?? (bubble.HasMeta(AuraDefaultMeta) ? bubble.GetMeta(AuraDefaultMeta).AsColor() : look.Bubble.DefaultAura);
                        MaterialLibrary.SetBubbleAura(bubble, rim);
                        changed++;
                        break;
                    case "effect" when material is StandardMaterial3D effect && effect.HasMeta(BaseMeta):
                        var glow = Tinted(effect.GetMeta(BaseMeta).AsColor(), now, effect.GetMeta(TintMeta).AsSingle());
                        effect.AlbedoColor = glow;
                        effect.Emission = glow;
                        changed++;
                        break;
                }
            }
        return changed;
    }

    /// <summary>
    /// Tell the character's bubble how lit its place is (0 dark to 1 lit; the look's LightLevelAt is the natural source): the film is
    /// unshaded for its budget, so this is how its tint and glint follow the light. Returns how many bubble materials took it.
    /// </summary>
    public static int SetLightLevel(Node3D visualRoot, float level)
    {
        var changed = 0;
        foreach (var mesh in Meshes(visualRoot))
            for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
                if (mesh.GetSurfaceOverrideMaterial(surface) is ShaderMaterial bubble && bubble.HasMeta(RoleMeta) && bubble.GetMeta(RoleMeta).AsString() == "bubble")
                {
                    MaterialLibrary.SetBubbleLight(bubble, float.IsFinite(level) ? Mathf.Clamp(level, 0f, 1f) : 1f);
                    changed++;
                }
        return changed;
    }

    /// <summary>The aura colour on the body (LookDirector.AuraColorMeta, a Color), else on the visual root or its ancestors; null when none is set.</summary>
    public static Color? AuraOf(Node? body, Node visualRoot)
    {
        if (body != null) return ColorMeta(body);
        for (Node? node = visualRoot; node != null; node = node.GetParent())
            if (ColorMeta(node) is { } colour) return colour;
        return null;
    }

    private static Color? ColorMeta(Node node) =>
        GodotObject.IsInstanceValid(node) && node.HasMeta(LookDirector.AuraColorMeta) && node.GetMeta(LookDirector.AuraColorMeta).VariantType == Variant.Type.Color
            ? node.GetMeta(LookDirector.AuraColorMeta).AsColor() : null;

    /// <summary>
    /// A surface's role: a bubble hint (or role) is the film; an effect part (or role, or a sparkle's or flame's name when nothing
    /// says its kind) glows; a dark surface that is a mark (a mark's name or role, an ink role, or the converter's ink colour)
    /// is ink; the rest is felt. An explicit "plain" role keeps a part felt.
    /// </summary>
    public static CharacterSurfaceRole RoleOf(CharacterLookPart part, Material? source, Color baseColor, CharacterMaterialHint? hint)
    {
        var role = part.Role?.ToLowerInvariant();
        switch (role)
        {
            case "bubble": return CharacterSurfaceRole.Bubble;
            case "effect": return CharacterSurfaceRole.Effect;
            case "plain": return CharacterSurfaceRole.Plain;
        }
        if (string.Equals((hint ?? HintOf(source))?.Kind, "bubble", StringComparison.OrdinalIgnoreCase)) return CharacterSurfaceRole.Bubble;
        // A part whose kind nobody gave (a bare mesh name) is an effect when its name says sparkle or flame.
        if (part.IsEffect || (part.Kind == null && IsEffectName(part.Name))) return CharacterSurfaceRole.Effect;
        var mark = role == "ink" || IsMarkName(part.Name) || IsMarkName(role) || Words(source?.ResourceName).Contains(InkWord);
        return mark && IsDark(baseColor) ? CharacterSurfaceRole.Ink : CharacterSurfaceRole.Plain;
    }

    private static StandardMaterial3D EffectMaterial(Color baseColor, Color? aura, string? motionHint, CharacterEffectLook look)
    {
        var tint = look.AuraTintFor(motionHint);
        var material = MaterialLibrary.ForCharacterEffect(Tinted(baseColor, aura, tint), look.EnergyFor(motionHint));
        material.SetMeta(BaseMeta, baseColor);
        material.SetMeta(TintMeta, tint);
        return material;
    }

    /// <summary>An effect's colour: its own colour moved tint of the way toward the aura (its own colour when there is no aura).</summary>
    public static Color Tinted(Color own, Color? aura, float tint) => aura is { } a ? own.Lerp(new Color(a.R, a.G, a.B, own.A), tint) : own;

    private static CharacterLookPart? PartFor(MeshInstance3D mesh, Node3D visualRoot, IReadOnlyDictionary<string, CharacterLookPart> byName)
    {
        for (Node? node = mesh; node != null; node = node.GetParent())
        {
            if (byName.TryGetValue(node.Name, out var part)) return part;
            if (node == visualRoot) break;
        }
        return null;
    }

    private static IEnumerable<MeshInstance3D> Meshes(Node3D root) =>
        root.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>().Prepend(root as MeshInstance3D)
            .OfType<MeshInstance3D>().Where(m => m.Mesh != null);

    /// <summary>The hem's band in the visual root's space: the lowest fifth (HemFraction) of the lowest bubble part, and its middle.</summary>
    private static (float Bottom, float Top, Vector3 Center)? HemBand(Node3D visualRoot, IReadOnlyList<MeshInstance3D> bubbles, BubbleLook look)
    {
        if (bubbles.Count == 0) return null;
        var boxes = bubbles.Select(m => RelativeTransform(visualRoot, m) * m.Mesh.GetAabb()).ToList();
        var lowest = boxes.OrderBy(b => b.Position.Y).First();
        var center = lowest.GetCenter();
        return (lowest.Position.Y, lowest.Position.Y + lowest.Size.Y * look.HemFraction, new Vector3(center.X, lowest.Position.Y, center.Z));
    }

    /// <summary>The hem's band in one bubble part's own space (a part above the band gets a band below itself: no wave).</summary>
    private static BubbleHem HemFor(Node3D visualRoot, MeshInstance3D mesh, (float Bottom, float Top, Vector3 Center)? band)
    {
        if (band is not { } b) return BubbleHem.None;
        var inverse = RelativeTransform(visualRoot, mesh).AffineInverse();
        var bottom = inverse * new Vector3(b.Center.X, b.Bottom, b.Center.Z);
        var top = inverse * new Vector3(b.Center.X, b.Top, b.Center.Z);
        return new BubbleHem(bottom.Y, top.Y, new Vector2(bottom.X, bottom.Z));
    }

    /// <summary>Transform of a descendant relative to an ancestor, valid before the nodes enter the tree (as RoomBuilder's).</summary>
    public static Transform3D RelativeTransform(Node3D ancestor, Node3D descendant)
    {
        var result = Transform3D.Identity;
        for (Node? node = descendant; node != null && node != ancestor; node = node.GetParent())
            if (node is Node3D spatial) result = spatial.Transform * result;
        return result;
    }
}
