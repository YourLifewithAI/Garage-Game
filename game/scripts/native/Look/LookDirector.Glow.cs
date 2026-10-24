using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Room;

namespace GarageGame.Native.Look;

/// <summary>
/// The numbers of the Gubble's glow and of the light-level estimate (Run 2, Glow; docs/look/GLOW.md). They are code constants for
/// this round, like OpenLandLight; a later preset version can carry them as a look block once the founder has judged the glow on the GPU.
/// </summary>
public static class GlowLook
{
    /// <summary>The most glows the look draws at once: the schema's outer limit on max_active, so the look never refuses what a pack allows.</summary>
    public const int MaxGlows = 8;
    /// <summary>The most glows that cast a shadow at once, and only while the preset's shadow budget has a slot left after the sun and the room's lights.</summary>
    public const int MaxShadowedGlows = 1;
    /// <summary>The engine's bounds on light.emit's intensity (contracts/island-rules.schema.json); the look clamps to them.</summary>
    public const float MinIntensity = 0.05f;
    public const float MaxIntensity = 2.0f;
    /// <summary>The engine's bound on a glow's radius (area_radius_max_m at most 3 m), and the smallest the look draws.</summary>
    public const float MinRadiusM = 0.05f;
    public const float MaxRadiusM = 3.0f;
    /// <summary>The light's energy per unit of intensity: at the pack's default 0.6 the glow's energy is 0.3.</summary>
    public const float EnergyPerIntensity = 0.5f;
    /// <summary>The light's falloff exponent (Godot's omni_attenuation): gentler than the lamps' 2.5, so the glow is a soft pool and not a hot spot.</summary>
    public const float Attenuation = 0.75f;
    /// <summary>How large the source is, metres: soft shadow edges (when it has one) and soft highlights.</summary>
    public const float LightSizeM = 0.04f;
    /// <summary>How much highlight the glow puts on shiny things: low, so it paints rather than glints.</summary>
    public const float Specular = 0.3f;
    /// <summary>The colour of a glow when the Gubble has no aura colour: a warm white-gold.</summary>
    public static readonly Color DefaultColor = new("ffe4a8");
    /// <summary>How far an aura colour is lifted toward white for the light, so even a deep blue aura lights the ground readably.</summary>
    public const float AuraWhiteMix = 0.25f;

    /// <summary>A halo on a body sits at this fraction of the body's height (its middle, a little up), unless the body names anchors.</summary>
    public const float FollowHeightFraction = 0.55f;
    /// <summary>
    /// A drawn body names where its glow sits with these metas, each a Node3D that rides one of its bones (the characters round, K5):
    /// the halo (the Gubble's aura bone, its middle) and the light (its torch flame: the Gubble carries the light). A wisp's spark
    /// leaves from the halo's anchor. Without them, FollowHeightFraction of the height, as before.
    /// </summary>
    public const string HaloAnchorMeta = "glow_halo_anchor";
    public const string LightAnchorMeta = "glow_light_anchor";
    /// <summary>The halo's diameter in body heights, around an avatar; around any other node, HaloDefaultM.</summary>
    public const float HaloBodyHeights = 2.0f;
    public const float HaloDefaultM = 0.45f;
    /// <summary>A wisp floats this far above the point it was cast at (the player aims at a surface), and bobs gently.</summary>
    public const float WispLiftM = 0.06f;
    /// <summary>
    /// The wisp's heart is light, not an object (the founder's playtest, 9 October: "a light effect in a spot without the black orb
    /// object"): a soft camera-facing spot this wide, brightest in its middle and fading to nothing at its rim, in the glow's colour.
    /// </summary>
    public const float WispCoreM = 0.045f;
    public const float WispHaloM = 0.18f;
    /// <summary>How bright the heart's emission is, over 1 so the bloom catches it.</summary>
    public const float WispCoreEnergy = 2.5f;

    // ---------- the cast: a spark from the Gubble, then the bloom ----------

    /// <summary>
    /// A glow cast at a point travels from the Gubble (the founder: "it should come off of the Gubble and land where you point"): a
    /// spark leaves the Gubble's middle and arcs to the spot in SparkBaseS plus SparkSecondsPerM a metre, at most SparkMaxS (2 m, the
    /// pack's reach, takes half a second).
    /// </summary>
    public const float SparkBaseS = 0.2f;
    public const float SparkSecondsPerM = 0.15f;
    public const float SparkMaxS = 0.55f;
    /// <summary>The arc's height over the straight line: this fraction of the distance, within SparkArcMinM and SparkArcMaxM.</summary>
    public const float SparkArcFraction = 0.3f;
    public const float SparkArcMinM = 0.03f;
    public const float SparkArcMaxM = 0.5f;
    /// <summary>The spark's size (the wisp's heart's light, smaller), and the embers trailing it, each this much later on the arc.</summary>
    public const float SparkM = 0.035f;
    public const int SparkEmbers = 3;
    public const float SparkEmberLagS = 0.035f;
    /// <summary>No spark for a spot closer to the Gubble than this: the glow blooms where it is.</summary>
    public const float SparkMinDistanceM = 0.03f;
    /// <summary>
    /// When the spark lands (or at once, for a glow on the Gubble, which blooms out of its body), the halo opens from BloomFrom of its
    /// size to all of it over BloomS, and a wisp's light comes up from nothing with it.
    /// </summary>
    public const float BloomS = 0.3f;
    public const float BloomFrom = 0.2f;
    public const float BobAmplitudeM = 0.012f;
    public const float BobPeriodS = 3.2f;
    /// <summary>The halo breathes: its size swells and settles by this fraction over the period.</summary>
    public const float BreathAmount = 0.05f;
    public const float BreathPeriodS = 4.0f;
    /// <summary>The halo's opacity at its centre: base plus per unit of intensity, at most HaloAlphaMax.</summary>
    public const float HaloAlphaBase = 0.16f;
    public const float HaloAlphaPerIntensity = 0.2f;
    public const float HaloAlphaMax = 0.6f;
    /// <summary>The halo fades out as a camera comes within these distances (the eye view beside the Gubble), and softens where it meets a surface.</summary>
    public const float HaloFadeNearM = 0.04f;
    public const float HaloFadeFullM = 0.2f;
    public const float HaloProximityFadeM = 0.05f;

    // ---------- the light-level estimate ----------

    /// <summary>The estimate's rays start this far above the point asked about, so the ground it lies on never counts as cover.</summary>
    public const float RayLiftM = 0.02f;
    public const float RayLengthM = 64f;
    /// <summary>The sun or moon lights a spot by KeyFloor plus the rest times the sine of its elevation (light on the ground and on the sides of things).</summary>
    public const float KeyFloor = 0.5f;
    /// <summary>The sky's three rays: straight up (this weight) and two tilted TiltDeg from the zenith (the rest, shared).</summary>
    public const float ZenithWeight = 0.5f;
    public const float TiltDeg = 40f;
    /// <summary>Lamps and glows count no closer than this, metres: a bulb's hot spot makes a place no more "lit".</summary>
    public const float NearClampM = 0.1f;
    /// <summary>
    /// Light bounced into the shade from the lit ground around a spot: this fraction of the hour's sun and sky (what an open spot
    /// would get) reaches every spot, so the shade under an overhang at noon is darker than open ground but lighter than night.
    /// </summary>
    public const float BounceFraction = 0.08f;
    /// <summary>Indoors (a closed room, VoxelGI's interior) the room's lamps and window fills bounce off the walls: their light counts this much more.</summary>
    public const float IndoorBounce = 0.5f;
    // Moonlight alone is dark (the founder, 9 October): the light through a window (the sun or moon, the sky, its bounce and the
    // window's sky fill) never makes a spot brighter than open ground under the same sky. At night that keeps a moonbeam on the floor
    // as dark as a moonlit meadow; by day it never binds (a sunlit floor is far dimmer than the open ground the room's sun stands for).
    /// <summary>
    /// The level curve is perceptual (each doubling of light is the same step): level 0 at LevelFloorEnergy and below (darker than a
    /// moonlit night), level 1 at LevelFullEnergy and above (full sun on open ground), logarithmic between.
    /// </summary>
    public const float LevelFloorEnergy = 0.02f;
    public const float LevelFullEnergy = 2.0f;

    /// <summary>The two tilted sky rays, in fixed world directions (deterministic): opposite each other, between the axes.</summary>
    public static readonly Vector3 TiltA = Tilt(30f);
    public static readonly Vector3 TiltB = Tilt(210f);

    private static Vector3 Tilt(float azimuthDeg)
    {
        var t = Mathf.DegToRad(TiltDeg);
        var a = Mathf.DegToRad(azimuthDeg);
        return new Vector3(Mathf.Sin(t) * Mathf.Cos(a), Mathf.Cos(t), Mathf.Sin(t) * Mathf.Sin(a)).Normalized();
    }

    /// <summary>The light's colour for an aura colour: its hue at full brightness (the energy carries the brightness), lifted a little toward white.</summary>
    public static Color LightColor(Color? aura)
    {
        if (aura is not { } c) return DefaultColor;
        var max = Mathf.Max(c.R, Mathf.Max(c.G, c.B));
        if (!float.IsFinite(max) || max < 1e-3f) return DefaultColor;
        var hue = new Color(Mathf.Clamp(c.R / max, 0f, 1f), Mathf.Clamp(c.G / max, 0f, 1f), Mathf.Clamp(c.B / max, 0f, 1f));
        return hue.Lerp(new Color(1f, 1f, 1f), AuraWhiteMix);
    }

    /// <summary>The light's energy for an intensity, clamped to the engine's bounds.</summary>
    public static float Energy(float intensity) => Mathf.Clamp(intensity, MinIntensity, MaxIntensity) * EnergyPerIntensity;

    /// <summary>The halo's centre opacity for an intensity.</summary>
    public static float HaloAlpha(float intensity) => Mathf.Min(HaloAlphaMax, HaloAlphaBase + HaloAlphaPerIntensity * Mathf.Clamp(intensity, MinIntensity, MaxIntensity));

    /// <summary>
    /// What a point light gives a spot at a distance: Godot's own omni falloff (a smooth window to zero at the range, times distance
    /// to the minus decay), counted no closer than NearClampM, times the light's luma.
    /// </summary>
    public static float PointLight(float energy, Color color, float rangeM, float decay, float distanceM)
    {
        if (energy <= 0f || rangeM <= 0f || distanceM >= rangeM) return 0f;
        var nd = distanceM / rangeM;
        nd *= nd;
        nd *= nd;
        var window = 1f - nd;
        window *= window;
        return energy * ColorGrade.Luma(color) * window * Mathf.Pow(Mathf.Max(distanceM, NearClampM), -decay);
    }

    /// <summary>A light level from 0 (dark) to 1 (bright) for an estimated amount of light.</summary>
    public static float Level(float energy) =>
        energy > LevelFloorEnergy && float.IsFinite(energy) ? Mathf.Min(1f, Mathf.Log(energy / LevelFloorEnergy) / Mathf.Log(LevelFullEnergy / LevelFloorEnergy)) : 0f;

    /// <summary>The light a level stands for (the inverse of Level, for levels between 0 and 1).</summary>
    public static float EnergyForLevel(float level) => LevelFloorEnergy * Mathf.Pow(LevelFullEnergy / LevelFloorEnergy, Mathf.Clamp(level, 0f, 1f));
}

/// <summary>
/// What LightLevelAt counted at a spot, in the look's light units: the sun or moon, the sky, bounce, the room's lamps, its windows'
/// sky fill (after the rule that a window never makes a spot brighter than open ground under the same sky), and the glows.
/// </summary>
public readonly record struct LightEstimate(float Key, float Sky, float Bounce, float Lamps, float Windows, float Glows, float SkyVisibility, bool KeySeen)
{
    public float Energy => Key + Sky + Bounce + Lamps + Windows + Glows;
    /// <summary>The daylight or moonlight alone: everything but the lamps and the glows.</summary>
    public float Natural => Key + Sky + Bounce + Windows;
    public float Level => GlowLook.Level(Energy);
}

public partial class LookDirector
{
    /// <summary>
    /// The level below which the game counts a place as dark (the dusk moment, "aimed at a dark spot, glow there"): light 0.2 on the
    /// curve. On the default preset at the fallback site: summer noon on open ground is 0.99, the golden hour 0.75, under a big
    /// overhang at noon 0.43, 30 minutes after an October sunset 0.46, a moonlit night outdoors 0.32, and the default glow lifts a
    /// night spot half its radius away to 0.76 (docs/look/GLOW.md has the table).
    /// </summary>
    public const float DarkThreshold = 0.5f;
    /// <summary>The meta an avatar carries its aura colour in (a Color): the glow takes it. Lane P's to set (see docs/look/GLOW.md).</summary>
    public const string AuraColorMeta = "aura_color";
    /// <summary>The meta on each glow's root node naming the effect it draws.</summary>
    public const string GlowMeta = "look_glow";
    /// <summary>What the estimate's rays hit: the world layer only, so the hidden parts on layer 5 (drawn: false), the avatars and the water never count as cover.</summary>
    public const uint LightMask = RoomBuilder.WorldLayer;

    private sealed class Glow
    {
        public string Id = "";
        public Node3D Root = null!;
        public OmniLight3D Light = null!;
        public MeshInstance3D Halo = null!;
        public MeshInstance3D? Core;
        public Node3D? Follow;
        public float Lift;
        public Vector3 Center;
        public float RadiusM;
        public float Intensity;
        public float Phase;
        /// <summary>The light's full energy: what the light-level estimate counts from the moment the glow starts (the cast's effect is at once; only its drawing travels and blooms).</summary>
        public float Energy;
        /// <summary>The spark from the Gubble (its head and embers), while it flies; null for a glow with no spark.</summary>
        public Node3D? Spark;
        public Vector3 SparkFrom;
        public float FlightS;
        /// <summary>Seconds since the glow started (drawing only).</summary>
        public float Age;
        public bool Bloomed;
        /// <summary>How far the halo has opened (BloomFrom to 1); the breath swells and settles around it.</summary>
        public float Opened = 1f;
        public string Variant = "plain", Touch = "plain";
        public Vector3 Anchor;
        public MultiMeshInstance3D? Decoration;
        public AudioStreamPlayer3D? Chime;
    }

    private readonly Dictionary<string, Glow> _glows = new();
    private readonly List<Glow> _glowOrder = new();
    private double _glowClock;
    private GradientTexture2D? _haloTexture;
    private GradientTexture2D? _heartTexture;
    private RayCast3D? _probe;

    /// <summary>How many glows the look is drawing.</summary>
    public int GlowCount => _glows.Count;

    /// <summary>Whether a glow with this effect id is drawn.</summary>
    public bool HasGlow(string effectId) => effectId != null && _glows.ContainsKey(effectId);

    /// <summary>A glow's light, for tests and the review harness; null for an unknown id.</summary>
    public OmniLight3D? GlowLight(string effectId) => effectId != null && _glows.TryGetValue(effectId, out var glow) ? glow.Light : null;

    /// <summary>A glow's root node (its light, halo and, for a wisp, its core), for tests and the review harness; null for an unknown id.</summary>
    public Node3D? GlowRoot(string effectId) => effectId != null && _glows.TryGetValue(effectId, out var glow) ? glow.Root : null;

    /// <summary>A glow's spark from the Gubble while it flies (null once it has landed, for a glow on the Gubble, or with no Gubble drawn).</summary>
    public Node3D? GlowSpark(string effectId) => effectId != null && _glows.TryGetValue(effectId, out var glow) && glow.Spark != null && IsInstanceValid(glow.Spark) ? glow.Spark : null;

    /// <summary>
    /// Give every running glow the colour a restart would give it now (GlowLook.LightColor of the aura of the body it follows, or for
    /// a wisp of the Gubble's): its light, its halo, a wisp's heart and a spark in flight, in place, so a wisp keeps its spot and its
    /// effect id; the Bubble's film and the fireworks' sparks take it too. The HUD calls this when the Gubble's colour
    /// changes. Returns how many effects it recoloured.
    /// </summary>
    public int RecolorGlows()
    {
        foreach (var glow in _glowOrder) Paint(glow, GlowLook.LightColor(AuraOf(glow.Follow ?? CompanionBody())));
        // Fireworks take it too: new sparks, and a flash that is lit. The Bubble's film wears the aura as its rim.
        foreach (var effect in _effectOrder)
        {
            effect.Color = GlowLook.LightColor(AuraOf(effect.Follow ?? CompanionBody()));
            if (effect.Flash != null && IsInstanceValid(effect.Flash)) effect.Flash.LightColor = effect.Color;
        }
        RecolorBubbles();
        return _glowOrder.Count + _effectOrder.Count;
    }

    private static void Paint(Glow glow, Color color)
    {
        glow.Light.LightColor = color;
        if (glow.Halo.MaterialOverride is StandardMaterial3D halo) halo.AlbedoColor = new Color(color.R, color.G, color.B, halo.AlbedoColor.A);
        if (glow.Core?.MaterialOverride is StandardMaterial3D core) core.Emission = color;
        if (glow.Spark != null && IsInstanceValid(glow.Spark))
            foreach (var ember in glow.Spark.GetChildren().OfType<MeshInstance3D>())
                if (ember.MaterialOverride is StandardMaterial3D spark) spark.Emission = color;
    }

    /// <summary>
    /// Start the Gubble's glow, a real light: an omni light reaching radiusM, as bright as intensity (0.05 to 2.0) says. With follow,
    /// a soft halo on that body that moves with it; without, a small wisp floating just above center with a gentle bob. Its colour
    /// is the aura colour of the body (or, for a wisp, of the companion) where one is set (AuraColorMeta), else a warm white-gold.
    /// An id already in use is replaced. Returns false, and draws nothing, when the look already draws MaxGlows other glows or the
    /// call is unusable (an empty id, a number that is not finite, a follow node that is gone or not in the scene).
    /// A halo never casts a shadow (its light sits inside the body); a wisp casts one only while the shadow budget has a slot.
    /// The glow arrives from the Gubble: a wisp's spark leaves the Gubble (when one is drawn) and arcs to the spot, where the wisp
    /// blooms and its light comes up; a halo blooms out of the body. The light level counts the glow at once, wherever its drawing is.
    /// </summary>
    public bool StartGlow(string effectId, Node3D? follow, Vector3 center, float radiusM, float intensity)
    {
        if (string.IsNullOrEmpty(effectId) || effectId.Length > 128 || !float.IsFinite(radiusM) || !float.IsFinite(intensity)) return false;
        if (follow != null && (!IsInstanceValid(follow) || follow.IsQueuedForDeletion() || !follow.IsInsideTree())) return false;
        if (follow == null && !(float.IsFinite(center.X) && float.IsFinite(center.Y) && float.IsFinite(center.Z))) return false;
        // The look's cap counts every effect (glows, bubbles, fireworks): the engine's 8 a room.
        if (!HasEffect(effectId) && (EffectCount >= EffectLook.MaxEffects || _glows.Count >= GlowLook.MaxGlows)) return false;
        FreeGlow(effectId);
        FreeEffect(effectId);
        var glow = BuildGlow(effectId, follow, center, Mathf.Clamp(radiusM, GlowLook.MinRadiusM, GlowLook.MaxRadiusM), Mathf.Clamp(intensity, GlowLook.MinIntensity, GlowLook.MaxIntensity));
        _glows[effectId] = glow;
        _glowOrder.Add(glow);
        AssignGlowShadows();
        return true;
    }

    /// <summary>End a glow and free everything it drew. An unknown id is a no-op.</summary>
    public void StopGlow(string effectId)
    {
        if (effectId == null || !FreeGlow(effectId)) return;
        AssignGlowShadows();
    }

    /// <summary>End every glow.</summary>
    public void StopAllGlows()
    {
        for (var i = _glowOrder.Count - 1; i >= 0; i--) FreeGlow(_glowOrder[i].Id);
    }

    private bool FreeGlow(string effectId)
    {
        if (!_glows.Remove(effectId, out var glow)) return false;
        _glowOrder.Remove(glow);
        ReleaseGlowSound(glow);
        if (IsInstanceValid(glow.Root)) glow.Root.Free();
        UpdateGlowLinks();
        return true;
    }

    private Glow BuildGlow(string id, Node3D? follow, Vector3 center, float radiusM, float intensity)
    {
        var body = follow as SmallPlayerController;
        var color = GlowLook.LightColor(AuraOf(follow ?? CompanionBody()));
        var glow = new Glow
        {
            Id = id, Follow = follow, RadiusM = radiusM, Intensity = intensity,
            Lift = body != null ? body.BodyHeightM * GlowLook.FollowHeightFraction : 0f,
            Center = center + Vector3.Up * GlowLook.WispLiftM,
            Phase = Seed(id) / 100f * Mathf.Tau,
            Energy = GlowLook.Energy(intensity),
        };
        // The glow's nodes stand in world space (top level), so they never inherit the look's transform and need no tree to be placed.
        glow.Root = new Node3D { Name = "Glow_" + RoomBuilder.NodeName(id), TopLevel = true };
        glow.Root.SetMeta(GlowMeta, id);
        glow.Light = new OmniLight3D
        {
            Name = "Light", OmniRange = radiusM, OmniAttenuation = GlowLook.Attenuation, LightEnergy = GlowLook.Energy(intensity),
            LightColor = color, LightSize = GlowLook.LightSizeM, LightSpecular = GlowLook.Specular, LightBakeMode = Light3D.BakeMode.Dynamic,
            ShadowEnabled = false,
        };
        if (Preset != null)
        {
            var s = Preset.Tuning.Shadows;
            glow.Light.ShadowBlur = s.BlurBase + s.BlurPerSoftness * Preset.ShadowSoftness;
            glow.Light.ShadowBias = s.LampBias;
            glow.Light.ShadowNormalBias = s.LampNormalBias;
        }
        glow.Root.AddChild(glow.Light);
        var haloSize = follow == null ? GlowLook.WispHaloM : body != null ? body.BodyHeightM * GlowLook.HaloBodyHeights : GlowLook.HaloDefaultM;
        glow.Halo = new MeshInstance3D
        {
            Name = "Halo", Mesh = new QuadMesh { Size = new Vector2(haloSize, haloSize) }, MaterialOverride = HaloMaterial(color, intensity),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
        };
        glow.Root.AddChild(glow.Halo);
        if (follow == null)
        {
            // The wisp's heart is light, not an object: a soft spot of emission in the glow's colour (bright enough for the bloom),
            // added to what is behind it and fading to nothing at its rim. No solid core: an unshaded black sphere drew the black orb
            // the founder saw, because unshaded draws the albedo and ignores emission.
            glow.Core = new MeshInstance3D
            {
                Name = "Core", Mesh = new QuadMesh { Size = new Vector2(GlowLook.WispCoreM, GlowLook.WispCoreM) }, MaterialOverride = HeartMaterial(color, "glow heart"),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            };
            glow.Root.AddChild(glow.Core);
        }
        PlaceGlow(glow, follow != null);
        AddChild(glow.Root);
        StartCast(glow);
        return glow;
    }

    /// <summary>
    /// How the glow arrives. A wisp with the Gubble drawn: a spark leaves the Gubble's middle and arcs to the spot, and the wisp blooms
    /// as it lands, its light coming up from nothing. A halo blooms out of the Gubble's body (its light is lit at once: it sits inside
    /// the body). With no Gubble drawn (fixtures, a look without a companion) a wisp is simply there, lit.
    /// </summary>
    private void StartCast(Glow glow)
    {
        glow.Age = 0f;
        if (glow.Follow != null)
        {
            glow.Opened = GlowLook.BloomFrom;
            glow.Halo.Scale = Vector3.One * GlowLook.BloomFrom;
            return;
        }
        var gubble = CompanionBody();
        var drawn = gubble != null && IsInstanceValid(gubble) && gubble.IsInsideTree() && gubble.IsVisibleInTree();
        var from = !drawn ? Vector3.Zero : AnchorAt(gubble, GlowLook.HaloAnchorMeta, drawing: true)
            ?? gubble!.GetGlobalTransformInterpolated().Origin + Vector3.Up * ((gubble as SmallPlayerController)?.BodyHeightM ?? 0f) * GlowLook.FollowHeightFraction;
        var distance = from.DistanceTo(glow.Center);
        if (!drawn || !float.IsFinite(distance) || distance < GlowLook.SparkMinDistanceM)
        {
            glow.Bloomed = true;
            return;
        }
        glow.SparkFrom = from;
        glow.FlightS = Mathf.Min(GlowLook.SparkMaxS, GlowLook.SparkBaseS + GlowLook.SparkSecondsPerM * distance);
        // The spark: a head and a few embers trailing it, all the heart's soft light, no light of its own (the glow's one omni light
        // waits at the spot), no shadow, no GI. It stands in world space under the glow's root, so stopping the glow frees it.
        glow.Spark = new Node3D { Name = "Spark", TopLevel = true, Position = from };
        var material = HeartMaterial(glow.Light.LightColor, "glow spark");
        for (var i = 0; i <= GlowLook.SparkEmbers; i++)
        {
            var size = GlowLook.SparkM * (1f - 0.22f * i);
            glow.Spark.AddChild(new MeshInstance3D
            {
                Name = i == 0 ? "Head" : $"Ember{i}", Mesh = new QuadMesh { Size = new Vector2(size, size) }, MaterialOverride = material, TopLevel = true, Position = from,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            });
        }
        glow.Root.AddChild(glow.Spark);
        glow.Light.LightEnergy = 0f;
        glow.Halo.Visible = false;
        if (glow.Core != null) glow.Core.Visible = false;
    }

    /// <summary>Where the spark is a time into its flight: along the straight line from the Gubble to the spot, lifted on an arc.</summary>
    private static Vector3 SparkAt(Glow glow, float t)
    {
        t = Mathf.Clamp(t, 0f, 1f);
        var distance = glow.SparkFrom.DistanceTo(glow.Center);
        var arc = Mathf.Clamp(distance * GlowLook.SparkArcFraction, GlowLook.SparkArcMinM, GlowLook.SparkArcMaxM);
        var eased = t * t * (3f - 2f * t) * 0.35f + t * 0.65f;
        return glow.SparkFrom.Lerp(glow.Center, eased) + Vector3.Up * (arc * 4f * t * (1f - t));
    }

    /// <summary>Advance a glow's arrival: the spark's flight, then the bloom (the halo opening and a wisp's light coming up).</summary>
    private void AdvanceCast(Glow glow, float delta)
    {
        if (glow.Bloomed) return;
        glow.Age += delta;
        var landed = glow.Age - glow.FlightS;
        if (glow.Spark != null && IsInstanceValid(glow.Spark))
        {
            if (landed < 0f)
            {
                var embers = glow.Spark.GetChildren();
                for (var i = 0; i < embers.Count; i++)
                    if (embers[i] is Node3D ember) ember.Position = SparkAt(glow, (glow.Age - i * GlowLook.SparkEmberLagS) / glow.FlightS);
                return;
            }
            glow.Spark.Free();
            glow.Spark = null;
            var visibleSource = glow.Variant != "bioluminesce";
            glow.Halo.Visible = visibleSource;
            if (glow.Core != null) glow.Core.Visible = visibleSource;
        }
        var bloom = Mathf.Clamp(Mathf.Max(0f, landed) / GlowLook.BloomS, 0f, 1f);
        var opened = 1f - (1f - bloom) * (1f - bloom);
        if (glow.Follow == null) glow.Light.LightEnergy = glow.Energy * bloom * bloom * (3f - 2f * bloom);
        glow.Opened = Mathf.Lerp(GlowLook.BloomFrom, 1f, opened);
        if (bloom >= 1f) glow.Bloomed = true;
    }

    /// <summary>The soft spot of light a wisp's heart and a spark are drawn with: emission in the glow's colour through a radial fade, added to what is behind.</summary>
    private StandardMaterial3D HeartMaterial(Color color, string name) => new()
    {
        ResourceName = name, AlbedoColor = new Color(0f, 0f, 0f, 1f), AlbedoTexture = HeartTexture(),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
        BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled, BillboardKeepScale = true,
        EmissionEnabled = true, Emission = color, EmissionEnergyMultiplier = GlowLook.WispCoreEnergy, EmissionTexture = HeartTexture(),
        SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled, MetallicSpecular = 0f, Roughness = 1f,
        DisableAmbientLight = true, DisableReceiveShadows = true,
    };

    private GradientTexture2D HeartTexture()
    {
        if (_heartTexture != null) return _heartTexture;
        var gradient = new Gradient
        {
            Offsets = new[] { 0f, 0.25f, 0.6f, 1f },
            Colors = new[] { new Color(1f, 1f, 1f, 1f), new Color(0.75f, 0.75f, 0.75f, 0.8f), new Color(0.18f, 0.18f, 0.18f, 0.25f), new Color(0f, 0f, 0f, 0f) },
        };
        _heartTexture = new GradientTexture2D
        {
            Gradient = gradient, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f), Width = 64, Height = 64,
        };
        return _heartTexture;
    }

    /// <summary>A soft, painterly halo: a camera-facing disc in the glow's colour, added to what is behind it, with a faint brighter rim.</summary>
    private StandardMaterial3D HaloMaterial(Color color, float intensity) => new()
    {
        ResourceName = "glow halo", ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
        BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled, BillboardKeepScale = true,
        AlbedoTexture = HaloTexture(), AlbedoColor = new Color(color.R, color.G, color.B, GlowLook.HaloAlpha(intensity)),
        ProximityFadeEnabled = true, ProximityFadeDistance = GlowLook.HaloProximityFadeM,
        DistanceFadeMode = BaseMaterial3D.DistanceFadeModeEnum.PixelAlpha,
        DistanceFadeMinDistance = GlowLook.HaloFadeNearM, DistanceFadeMaxDistance = GlowLook.HaloFadeFullM,
    };

    private GradientTexture2D HaloTexture()
    {
        if (_haloTexture != null) return _haloTexture;
        var gradient = new Gradient
        {
            Offsets = new[] { 0f, 0.42f, 0.58f, 0.78f, 1f },
            Colors = new[] { new Color(1f, 1f, 1f, 0.75f), new Color(1f, 1f, 1f, 0.34f), new Color(1f, 1f, 1f, 0.4f), new Color(1f, 1f, 1f, 0.1f), new Color(1f, 1f, 1f, 0f) },
        };
        _haloTexture = new GradientTexture2D
        {
            Gradient = gradient, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f), Width = 128, Height = 128,
        };
        return _haloTexture;
    }

    private static Color? AuraOf(Node3D? body)
    {
        if (body == null || !IsInstanceValid(body) || !body.HasMeta(AuraColorMeta)) return null;
        var value = body.GetMeta(AuraColorMeta);
        return value.VariantType == Variant.Type.Color ? value.AsColor() : null;
    }

    /// <summary>
    /// Where a body's glow anchor named by a meta (GlowLook.HaloAnchorMeta or LightAnchorMeta) is, or null when it names none in the
    /// tree; for drawing, carried by the body's interpolated transform.
    /// </summary>
    private static Vector3? AnchorAt(Node3D? body, string meta, bool drawing)
    {
        if (body == null || !IsInstanceValid(body) || !body.HasMeta(meta)) return null;
        var named = body.GetMeta(meta);
        if (named.VariantType != Variant.Type.Object || named.AsGodotObject() is not Node3D anchor || !IsInstanceValid(anchor) || !anchor.IsInsideTree()) return null;
        return drawing ? body.GetGlobalTransformInterpolated() * (body.GlobalTransform.AffineInverse() * anchor.GlobalPosition) : anchor.GlobalPosition;
    }

    /// <summary>Puts a glow's light where GlowPosition says, and a halo on its body's halo anchor (where the body names one).</summary>
    private void PlaceGlow(Glow glow, bool drawing)
    {
        glow.Root.Position = GlowPosition(glow, drawing);
        if (glow.Follow != null) glow.Halo.Position = AnchorAt(glow.Follow, GlowLook.HaloAnchorMeta, drawing) is { } halo ? halo - glow.Root.Position : Vector3.Zero;
    }

    /// <summary>
    /// Where a glow's light is now: on its body (interpolated for drawing; on the body's light anchor, else its halo anchor, where it
    /// names them), or floating above its point (bobbing, when drawing).
    /// </summary>
    private Vector3 GlowPosition(Glow glow, bool drawing)
    {
        if (glow.Follow != null)
        {
            if (!IsInstanceValid(glow.Follow) || !glow.Follow.IsInsideTree()) return glow.Root.Position;
            if ((AnchorAt(glow.Follow, GlowLook.LightAnchorMeta, drawing) ?? AnchorAt(glow.Follow, GlowLook.HaloAnchorMeta, drawing)) is { } anchor) return anchor;
            var origin = drawing ? glow.Follow.GetGlobalTransformInterpolated().Origin : glow.Follow.GlobalPosition;
            return origin + Vector3.Up * glow.Lift;
        }
        if (!drawing) return glow.Center;
        if (glow.Variant is "bioluminesce" or "skylight") return glow.Center;
        return glow.Center + Vector3.Up * (GlowLook.BobAmplitudeM * (float)Math.Sin(_glowClock * Math.Tau / GlowLook.BobPeriodS + glow.Phase));
    }

    private void UpdateGlows(double delta)
    {
        if (_glowOrder.Count == 0) return;
        _glowClock += delta;
        for (var i = _glowOrder.Count - 1; i >= 0; i--)
        {
            var glow = _glowOrder[i];
            // A halo whose body is gone goes with it.
            if (glow.Follow != null && !IsInstanceValid(glow.Follow)) { StopGlow(glow.Id); continue; }
            UpdateGlowVariant(glow, (float)delta);
            PlaceGlow(glow, drawing: true);
            AdvanceCast(glow, (float)delta);
            var breath = glow.Opened * (1f + GlowLook.BreathAmount * (float)Math.Sin(_glowClock * Math.Tau / GlowLook.BreathPeriodS + glow.Phase));
            glow.Halo.Scale = new Vector3(breath, breath, breath);
        }
        UpdateGlowLinks();
    }

    /// <summary>The shadow slots the preset's budget has left after the sun and the room's windows and lamps (counted whether lit or not).</summary>
    public int FreeShadowSlots()
    {
        if (Preset == null || !Preset.ShadowsEnabled) return 0;
        var used = Key != null && Key.ShadowEnabled && Key.Visible ? 1 : 0;
        foreach (var light in _roomLights) if (light.ShadowEnabled) used++;
        return Math.Max(0, Preset.MaxShadowedLights - used);
    }

    /// <summary>The oldest wisps take the free shadow slots, at most MaxShadowedGlows; a halo never does.</summary>
    private void AssignGlowShadows()
    {
        var slots = Math.Min(FreeShadowSlots(), GlowLook.MaxShadowedGlows);
        foreach (var glow in _glowOrder)
        {
            var shadowed = glow.Follow == null && slots > 0;
            glow.Light.ShadowEnabled = shadowed;
            if (shadowed) slots--;
        }
    }

    // ---------- the light-level estimate ----------

    /// <summary>
    /// How lit a spot is, from 0 (dark) to 1 (bright): an estimate for the game (the dusk moment, the smart ask), not the renderer. It
    /// counts the sun or moon by the look's own clock and the room's site (one ray toward it), the sky overhead (three rays: up and
    /// two tilted), each on the world layer only, and the room's lamps, window fills and glows within their range (no rays). Bounce
    /// light (GI) and leaves that do not collide are not counted. Deterministic for the same scene and clock, and allocation-free after
    /// the first call. Compare with DarkThreshold.
    /// </summary>
    public float LightLevelAt(Vector3 position) => EstimateLightAt(position).Level;

    /// <summary>What LightLevelAt counts at a spot, term by term.</summary>
    public LightEstimate EstimateLightAt(Vector3 position)
    {
        float key = 0f, sky = 0f, bounce = 0f, lamps = 0f, windows = 0f, glows = 0f, visibility = 0f, open = 0f;
        var keySeen = false;
        var inTree = IsInsideTree();
        var probe = inTree ? Probe() : null;
        var origin = position + Vector3.Up * GlowLook.RayLiftM;
        if (Moment != null && Key != null)
        {
            if (Key.Visible && Key.LightEnergy > 0f)
            {
                var toKey = (inTree ? Key.GlobalBasis.Z : Key.Basis.Z).Normalized();
                if (toKey.Y > 1e-3f)
                {
                    var openKey = Key.LightEnergy * ColorGrade.Luma(Key.LightColor) * (GlowLook.KeyFloor + (1f - GlowLook.KeyFloor) * toKey.Y);
                    bounce += openKey;
                    if (Clear(probe, origin, toKey)) { keySeen = true; key = openKey; }
                }
            }
            var skyEnergy = Moment.AmbientEnergy * ColorGrade.Luma(Moment.AmbientColor) * OpenLandLight.AmbientScale;
            if (skyEnergy > 0f)
            {
                var side = 0.5f * (1f - GlowLook.ZenithWeight);
                visibility = (Clear(probe, origin, Vector3.Up) ? GlowLook.ZenithWeight : 0f)
                    + (Clear(probe, origin, GlowLook.TiltA) ? side : 0f) + (Clear(probe, origin, GlowLook.TiltB) ? side : 0f);
                sky = skyEnergy * visibility;
                bounce += skyEnergy;
            }
            // What open ground gets under this sky: the whole key, the whole sky and the bounce.
            open = bounce * (1f + GlowLook.BounceFraction);
            bounce *= GlowLook.BounceFraction;
        }
        // The room's lights stand in the look's own space.
        var local = inTree ? ToLocal(position) : position;
        foreach (var lamp in _lamps)
            if (lamp.Visible && lamp is OmniLight3D omni)
                lamps += GlowLook.PointLight(omni.LightEnergy, omni.LightColor, omni.OmniRange, omni.OmniAttenuation, omni.Position.DistanceTo(local));
        foreach (var fill in _skyFills)
            if (fill.Visible) windows += SpotAt(fill, local);
        if (!OpenLand)
        {
            lamps *= 1f + GlowLook.IndoorBounce;
            windows *= 1f + GlowLook.IndoorBounce;
        }
        // A window never makes a spot brighter than open ground under the same sky, so moonlight alone stays dark.
        windows = Mathf.Clamp(open - key - sky - bounce, 0f, windows);
        foreach (var glow in _glowOrder)
            glows += GlowLook.PointLight(glow.Energy, glow.Light.LightColor, glow.RadiusM, glow.Light.OmniAttenuation, GlowPosition(glow, drawing: false).DistanceTo(position));
        // The fireworks' flashes count too, briefly: only while one is lit.
        glows += FlashesAt(position);
        return new LightEstimate(key, sky, bounce, lamps, windows, glows, visibility, keySeen);
    }

    /// <summary>A window's sky fill at a spot: the point-light falloff inside Godot's spot cone (1 - rim^angle_attenuation).</summary>
    private static float SpotAt(SpotLight3D spot, Vector3 local)
    {
        var offset = local - spot.Position;
        var distance = offset.Length();
        if (distance >= spot.SpotRange || distance < 1e-5f) return 0f;
        var cutoff = Mathf.Cos(Mathf.DegToRad(spot.SpotAngle));
        var c = (offset / distance).Dot(-spot.Basis.Z);
        if (c <= cutoff) return 0f;
        var rim = Mathf.Max(1e-4f, (1f - c) / (1f - cutoff));
        var cone = 1f - Mathf.Pow(rim, spot.SpotAngleAttenuation);
        return cone * GlowLook.PointLight(spot.LightEnergy, spot.LightColor, spot.SpotRange, spot.SpotAttenuation, distance);
    }

    /// <summary>One ray the estimate reuses (no allocation per call); made on first use, so a look that is never asked has none.</summary>
    private RayCast3D Probe()
    {
        if (_probe != null && IsInstanceValid(_probe)) return _probe;
        _probe = new RayCast3D
        {
            Name = "LightProbe", Enabled = false, TopLevel = true, CollisionMask = LightMask, CollideWithAreas = false, CollideWithBodies = true,
            HitFromInside = false, HitBackFaces = true,
        };
        AddChild(_probe);
        return _probe;
    }

    /// <summary>Whether the sky (or the sun) is open from a point along a direction. Without a scene to cast in, it is.</summary>
    private static bool Clear(RayCast3D? probe, Vector3 origin, Vector3 direction)
    {
        if (probe == null) return true;
        probe.Position = origin;
        probe.TargetPosition = direction * GlowLook.RayLengthM;
        probe.ForceRaycastUpdate();
        return !probe.IsColliding();
    }
}
