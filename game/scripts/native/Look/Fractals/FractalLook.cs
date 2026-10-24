using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Look.Fractals;

/// <summary>Which body a Fractal is drawn with (the founder picks one from the sheet of three; docs/look/FRACTALS.md).</summary>
public enum FractalForm { Crystal, Seed, Star }

/// <summary>
/// The look of a Fractal (Run 2, L9: docs/runs/RUN-2-LOOK.md, docs/look/FRACTALS.md): every Fractal still on the island drawn
/// where the generator placed it, a glint above each that reads from a band away and at night, a slow life (a turn and a bob
/// inside the kernel's box), and the moment one is picked up (the body and its glint go out and a handful of sparks rise). Attach
/// it beside NatureLook once the host's nature is configured:
/// <code>
/// AddChild(FractalLook.Create(commandHost, Look));
/// </code>
/// It reads only the host's read-only state: CommandHost.AllFractals (to size its pools), Fractals (those still on the island,
/// re-read whenever NatureChanged says the nature moved on: a pickup commits through it) and FractalBox's size, never the manifest.
/// Three MultiMeshes, one draw each: the bodies, the glints, and the pickup's sparks; and at night a small pool of real light round
/// each Fractal nearest the player (no shadow, at most Tuning.MaxLights). Nothing here is an entity, saved, collides, or is seen by
/// the kernel, sight or map; the same drawing in every mode, every view and at any hour. The motion
/// is deterministic for the same ticks (Advance), which the look suite uses.
/// </summary>
public partial class FractalLook : Node3D
{
    /// <summary>
    /// Every number of the Fractals' look (code constants for this round, as the effects' are; the report proposes them as the
    /// preset's x_look_fractals block).
    /// </summary>
    public static class Tuning
    {
        /// <summary>
        /// The body every Fractal is drawn with: the star, the founder's pick from the sheet of three (15 October; the crystal and the
        /// seed stay built, for the record and the pin below, which overrides this for a capture).
        /// </summary>
        public const FractalForm Form = FractalForm.Star;

        // ---------- the colour: one family for every Fractal, never the Gubble's aura ----------

        /// <summary>The body's pale gold-white.</summary>
        public static readonly Color BodyColor = new(0.96f, 0.87f, 0.66f);
        /// <summary>The glint's cool white (its rays fringed with a rainbow), set apart from the glow's warm white-gold (GlowLook.DefaultColor).</summary>
        public static readonly Color GlintColor = new(0.93f, 0.96f, 1f);

        // ---------- where it rests and its slow life ----------

        /// <summary>The body's middle stands this far above the Fractal's spot: the middle of the kernel's 4 cm box.</summary>
        public const float RestCentreM = CommandHost.FractalHeightM * 0.5f;
        /// <summary>It turns once in TurnPeriodS and bobs BobM up and down over BobPeriodS; the star bobs as a wisp does (GLOW.md).</summary>
        public const float TurnPeriodS = 12f, BobM = 0.005f, BobPeriodS = 3f;
        public const float StarBobM = 0.012f, StarBobPeriodS = 3.2f;

        // ---------- the glint ----------

        /// <summary>The glint stands this far above the body's middle (it rides the bob), GlintSizeM wide up close, never under GlintMinPixels on screen.</summary>
        public const float GlintLiftM = 0.008f, GlintSizeM = 0.02f, GlintMinPixels = 12f;
        /// <summary>How bright it is (over 1: the bloom catches it at night, as the wisp's heart at 2.5).</summary>
        public const float GlintEnergy = 2.5f;
        /// <summary>The heart's and rays' shape: how tight the heart, how much halo, how thin and how strong the rays, how much rainbow on them.</summary>
        public const float GlintHeartSharpness = 26f, GlintHalo = 0.3f, GlintRaySharpness = 34f, GlintRays = 0.8f, GlintRainbow = 0.55f;
        /// <summary>A far glint (its quad grown to its pixels) softens its heart and thickens its rays no further than these, so it shows as a few bright pixels.</summary>
        public const float GlintFarHeartSharpness = 4f, GlintFarRaySharpness = 8f;
        /// <summary>Within GlintNearM of the eye it dims to GlintNearDim: the body in reach reads, not a flare. It is pulled GlintTowardCameraM toward the eye.</summary>
        public const float GlintNearM = 0.35f, GlintNearDim = 0.3f, GlintTowardCameraM = 0.03f;
        /// <summary>
        /// The twinkle: each Fractal on its own slow period between TwinkleMinS and TwinkleMaxS (its phase from its id, so no two blink
        /// together), never dimmer than TwinkleFloor, peaking briefly (TwinkleSharpness); the rays turn once in RayTurnPeriodS.
        /// </summary>
        public const float TwinkleMinS = 2.6f, TwinkleMaxS = 4.2f, TwinkleFloor = 0.45f, TwinkleSharpness = 6f, RayTurnPeriodS = 20f;

        // ---------- the body ----------

        /// <summary>The body is drawn to the small plants' distance (DrawDistances.SmallPlantM), fading over the last FormFadeBand of it; the glint goes on.</summary>
        public const float FormFarM = DrawDistances.SmallPlantM, FormFadeBand = 0.15f;
        /// <summary>The paint: a facet's (or a felt patch's) tone variation, the crystal's thin-film rainbow, the faint inner light, the seed's heart.</summary>
        public const float ToneVariation = 0.12f, Iridescence = 0.35f, InnerLight = 0.12f, HeartLight = 0.6f;
        /// <summary>The light: wrapped diffuse, the crystal's facet sparkle, the felt's sheen and rim.</summary>
        public const float WrapLight = 0.35f, Sparkle = 2.5f, SparklePower = 48f, Sheen = 0.3f, RimLight = 0.5f;
        /// <summary>The star: a heart StarHeartM across (what stays in the box) in a soft halo StarSizeM across, with long rays.</summary>
        public const float StarHeartM = 0.012f, StarSizeM = 0.045f, StarEnergy = 2.2f, StarHalo = 0.9f, StarRays = 1f, StarHeartSharpness = 18f;
        /// <summary>Where the star's light meets a surface (its halo reaches the ground it hovers over) it softens over this, as the wisp's halo does.</summary>
        public const float StarSoftM = 0.03f;

        // ---------- the light at night ----------

        /// <summary>
        /// At night each Fractal casts a small pool of real light round its heart (the founder, 15 October: "a fun way for players to
        /// find it"): an omni light of LightEnergy reaching LightRangeM, in the star's colour, casting no shadow (so it takes no slot of
        /// the shadow budget) and baked into nothing. It fades in as daylight falls through the lamps' own switch (the preset's
        /// switch_on_below_daylight) to LightFullAtSwitch of it, so the Fractals light up when lamps would.
        /// </summary>
        public const float LightEnergy = 0.18f, LightRangeM = 0.6f, LightAttenuation = 1.5f, LightSpecular = 0.2f, LightFullAtSwitch = 0.5f;
        /// <summary>At most MaxLights Fractals are lit, those nearest the player, chosen again every LightChooseS; the engine fades a light out past LightFadeBeginM from the camera.</summary>
        public const int MaxLights = 32;
        public const float LightChooseS = 0.25f, LightFadeBeginM = 40f, LightFadeLengthM = 10f;

        // ---------- the pickup ----------

        /// <summary>The body and its glint go out over PickupFadeS (the glint flaring PickupFlare as they go).</summary>
        public const float PickupFadeS = 0.25f, PickupFlare = 1.6f;
        /// <summary>SparksPerPickup sparks rise SparkRiseM from the spot over SparkLifeS, spread SparkSpreadM, each SparkSizeM wide; at most MaxSparkBursts pickups at once.</summary>
        public const int SparksPerPickup = 8, MaxSparkBursts = 4;
        public const float SparkLifeS = 0.6f, SparkRiseM = 0.07f, SparkSpreadM = 0.018f, SparkSizeM = 0.008f;
    }

    public const string FormShaderPath = "res://shaders/fractal_form.gdshader";
    public const string GlintShaderPath = "res://shaders/fractal_glint.gdshader";
    /// <summary>The pin for a capture or a comparison: "--look-fractal-form=crystal|seed|star" after "--", or this environment variable.</summary>
    public const string FormArgument = "--look-fractal-form=";
    public const string FormVariable = "GARAGE_GAME_LOOK_FRACTAL_FORM";

    /// <summary>The uniforms FractalLook sets on each shader: every one it declares (the look suite holds both lists to the shaders).</summary>
    public static readonly string[] FormParameterNames =
    {
        "form", "body_color", "tone_variation", "iridescence", "inner_light", "heart_light", "wrap_light", "sparkle", "sparkle_power", "sheen", "rim_light", "far_m", "fade_band",
    };
    public static readonly string[] GlintParameterNames =
    {
        "glint_color", "energy", "size_m", "min_pixels", "heart_sharpness", "halo", "ray_sharpness", "rays", "rainbow", "far_heart_sharpness", "far_ray_sharpness",
        "near_m", "near_dim", "toward_camera_m", "soft_m",
        "fog_on", "fog_begin_m", "fog_end_m", "fog_density", "fog_curve",
    };

    /// <summary>Floats an instance takes in a pool's buffer: its transform (12), colour (4) and custom data (4).</summary>
    private const int Stride = 20;

    private sealed class Shown
    {
        public string Id = "";
        public Vector3 Rest;
        public float Phase;
        /// <summary>Seconds since it was picked up; negative while it is on the island.</summary>
        public float Leaving = -1f;
    }

    private struct Spark
    {
        public Vector3 From, Drift;
        public float Age, Life, Phase;
    }

    private CommandHost? _host;
    private LookDirector? _look;
    private readonly List<Shown> _shown = new();
    private readonly Spark[] _sparks = new Spark[Tuning.SparksPerPickup * Tuning.MaxSparkBursts];
    private int _sparkCount;
    private float[] _formBuffer = Array.Empty<float>(), _glintBuffer = Array.Empty<float>(), _sparkBuffer = Array.Empty<float>();
    private int _capacity;
    private bool _dirty = true;
    private ShaderMaterial? _formMaterial, _glintMaterial, _starMaterial;
    private OmniLight3D[] _lights = Array.Empty<OmniLight3D>();
    private Shown?[] _lit = Array.Empty<Shown?>();
    private float _chooseClock = float.MaxValue;
    private (bool On, float Begin, float End, float Density, float Curve) _fog = (false, -1f, -1f, -1f, -1f);

    /// <summary>The body every Fractal is drawn with now.</summary>
    public FractalForm Form { get; private set; } = Tuning.Form;
    /// <summary>Whether the life runs on its own each frame (tests turn it off and step it with Advance).</summary>
    public bool Running { get; set; } = true;
    /// <summary>The seconds of life so far: the turn, the bob, the twinkle and the sparks all read this clock.</summary>
    public double LifeS { get; private set; }
    public MultiMeshInstance3D? Bodies { get; private set; }
    public MultiMeshInstance3D? Glints { get; private set; }
    public MultiMeshInstance3D? Sparks { get; private set; }
    /// <summary>How many Fractals are drawn on the island (not counting those going out after a pickup).</summary>
    public int Count => _shown.Count(s => s.Leaving < 0f);
    /// <summary>How many are going out after a pickup, and how many sparks are in the air.</summary>
    public int Leaving => _shown.Count(s => s.Leaving >= 0f);
    public int SparkCount => _sparkCount;
    /// <summary>The ids drawn on the island, in id order (the host's own order).</summary>
    public IReadOnlyList<string> Drawn => _shown.Where(s => s.Leaving < 0f).Select(s => s.Id).ToArray();
    public string Summary { get; private set; } = "";
    /// <summary>The lights of the night (at most Tuning.MaxLights), for the look suite.</summary>
    public IReadOnlyList<OmniLight3D> Lights => _lights;
    /// <summary>How much of the night's light is on now, 0 by day to 1 at night (the look's daylight through the lamps' switch).</summary>
    public float Night { get; private set; }

    public static FractalLook Create(CommandHost host, LookDirector? look)
    {
        var fractals = new FractalLook { Name = "FractalLook" };
        fractals.Configure(host, look);
        return fractals;
    }

    /// <summary>
    /// Bind to the host (and the look, for its haze): its Fractals are read now and again whenever its nature changes. Call it once the
    /// host's nature is configured (the island save read), so the Fractals picked up before are not drawn going out.
    /// </summary>
    public void Configure(CommandHost host, LookDirector? look)
    {
        if (_host != null && IsInstanceValid(_host)) _host.NatureChanged -= MarkDirty;
        _host = host;
        _look = look;
        _host.NatureChanged += MarkDirty;
        if (FormPin(OS.GetCmdlineUserArgs(), System.Environment.GetEnvironmentVariable(FormVariable)) is { } pinned) Form = pinned;
        Build();
    }

    /// <summary>The form a capture pins (the argument first, then the variable), or null.</summary>
    public static FractalForm? FormPin(IEnumerable<string> arguments, string? variable)
    {
        var text = arguments.FirstOrDefault(a => a.StartsWith(FormArgument, StringComparison.Ordinal))?[FormArgument.Length..] ?? variable;
        return text?.Trim().ToLowerInvariant() switch
        {
            "crystal" => FractalForm.Crystal,
            "seed" => FractalForm.Seed,
            "star" => FractalForm.Star,
            _ => null,
        };
    }

    /// <summary>Draw every Fractal with another body (the sheet and the tests; the game keeps Tuning.Form).</summary>
    public void SetForm(FractalForm form)
    {
        if (form == Form && Bodies != null) return;
        Form = form;
        Build();
    }

    private void MarkDirty() => _dirty = true;

    public override void _EnterTree() => TopLevel = true;

    public override void _ExitTree()
    {
        if (_host != null && IsInstanceValid(_host)) _host.NatureChanged -= MarkDirty;
    }

    public override void _Process(double delta)
    {
        if (!Running) return;
        Advance((float)delta);
    }

    /// <summary>Move the life on by dt seconds: re-read the host if its nature changed, age the pickups and sparks, and draw.</summary>
    public void Advance(float dt)
    {
        if (_host == null || !IsInstanceValid(_host) || _capacity == 0) return;
        if (_dirty) Refresh();
        LifeS += dt;
        for (var i = _shown.Count - 1; i >= 0; i--)
        {
            var shown = _shown[i];
            if (shown.Leaving < 0f) continue;
            shown.Leaving += dt;
            if (shown.Leaving >= Tuning.PickupFadeS) _shown.RemoveAt(i);
        }
        for (var i = _sparkCount - 1; i >= 0; i--)
        {
            _sparks[i].Age += dt;
            if (_sparks[i].Age >= _sparks[i].Life) _sparks[i] = _sparks[--_sparkCount];
        }
        Draw();
        Light(dt);
        FollowHaze();
    }

    // ---------- the host's Fractals ----------

    /// <summary>The pools sized for every Fractal the manifest places (at most MaxFractals), and those on the island drawn at rest.</summary>
    private void Build()
    {
        foreach (var pool in new[] { Bodies, Glints, Sparks })
            if (pool != null && IsInstanceValid(pool)) { RemoveChild(pool); pool.QueueFree(); }
        foreach (var light in _lights)
            if (IsInstanceValid(light)) { RemoveChild(light); light.QueueFree(); }
        Bodies = Glints = Sparks = null;
        _lights = Array.Empty<OmniLight3D>();
        _lit = Array.Empty<Shown?>();
        _chooseClock = float.MaxValue;
        _shown.Clear();
        _sparkCount = 0;
        if (_host == null) return;
        var all = _host.AllFractals;
        _capacity = Math.Min(all.Count, CommandHost.MaxFractals);
        if (_capacity == 0)
        {
            Summary = _host.FractalsNotice.Length > 0 ? "no Fractals drawn: " + _host.FractalsNotice : "the island has no Fractals";
            GD.Print("FRACTAL_LOOK: " + Summary);
            return;
        }
        foreach (var fractal in _host.Fractals)
        {
            if (_shown.Count == _capacity) break;
            _shown.Add(new Shown { Id = fractal.Id, Rest = fractal.PositionM, Phase = PhaseOf(fractal.Id) });
        }
        _dirty = false;
        _formMaterial = FormMaterial(Form);
        _glintMaterial = GlintMaterial(star: false);
        _starMaterial = Form == FractalForm.Star ? GlintMaterial(star: true) : null;
        var box = all.Take(_capacity).Select(CommandHost.FractalBox).Aggregate((a, b) => a.Merge(b));
        var quad = new QuadMesh { Size = Vector2.One };
        Bodies = Pool("Bodies", FractalMesh.For(Form) ?? (Mesh)quad, _capacity, Form == FractalForm.Star ? _starMaterial! : _formMaterial,
            box.Grow(Tuning.StarSizeM), Form != FractalForm.Star);
        // A glint is drawn wider with distance (never under its pixels), so its box reaches a few metres round the Fractals.
        Glints = Pool("Glints", quad, _capacity, _glintMaterial, box.Grow(4f), false);
        Sparks = Pool("Sparks", quad, _sparks.Length, _glintMaterial, box.Grow(Tuning.SparkRiseM + 0.1f), false);
        // The night's lights: a fixed pool, given to the Fractals nearest the player.
        var lightColor = Tuning.BodyColor.Lerp(Tuning.GlintColor, 0.5f);
        _lights = new OmniLight3D[Math.Min(_capacity, Tuning.MaxLights)];
        _lit = new Shown?[_lights.Length];
        for (var i = 0; i < _lights.Length; i++)
        {
            _lights[i] = new OmniLight3D
            {
                Name = "Light" + i, OmniRange = Tuning.LightRangeM, OmniAttenuation = Tuning.LightAttenuation, LightColor = lightColor,
                LightEnergy = 0f, LightSpecular = Tuning.LightSpecular, ShadowEnabled = false, LightBakeMode = Light3D.BakeMode.Disabled,
                DistanceFadeEnabled = true, DistanceFadeBegin = Tuning.LightFadeBeginM, DistanceFadeLength = Tuning.LightFadeLengthM, Visible = false,
            };
            AddChild(_lights[i]);
        }
        _formBuffer = new float[_capacity * Stride];
        _glintBuffer = new float[_capacity * Stride];
        _sparkBuffer = new float[_sparks.Length * Stride];
        Draw();
        Summary = $"{_shown.Count} of {all.Count} Fractals drawn as the {Form.ToString().ToLowerInvariant()}" +
                  (all.Count > _capacity ? $" (at most {CommandHost.MaxFractals})" : "");
        GD.Print("FRACTAL_LOOK: " + Summary);
    }

    private MultiMeshInstance3D Pool(string name, Mesh mesh, int count, Material material, Aabb box, bool shadow)
    {
        var multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true, Mesh = mesh,
            InstanceCount = count, VisibleInstanceCount = 0, CustomAabb = box,
        };
        var pool = new MultiMeshInstance3D
        {
            Name = name, Multimesh = multimesh, MaterialOverride = material, CustomAabb = box,
            CastShadow = shadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
        };
        AddChild(pool);
        return pool;
    }

    /// <summary>
    /// The host's nature moved on: Fractals no longer on the island go out (the pickup moment) and any that came back (a save
    /// restored) appear. A cheap walk in the host's id order first, so a nature step that changed no Fractal costs nothing more.
    /// </summary>
    private void Refresh()
    {
        _dirty = false;
        if (_host == null) return;
        var index = 0;
        var same = true;
        foreach (var fractal in _host.Fractals)
        {
            while (index < _shown.Count && _shown[index].Leaving >= 0f) index++;
            if (index >= _shown.Count || _shown[index].Id != fractal.Id) { same = false; break; }
            index++;
        }
        while (same && index < _shown.Count) { if (_shown[index].Leaving < 0f) same = false; index++; }
        if (same) return;
        var present = _host.Fractals.Take(_capacity).ToDictionary(f => f.Id, StringComparer.Ordinal);
        foreach (var shown in _shown)
        {
            if (present.ContainsKey(shown.Id)) shown.Leaving = -1f;
            else if (shown.Leaving < 0f) PickedUp(shown);
        }
        var drawn = _shown.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var fractal in present.Values.Where(f => !drawn.Contains(f.Id)))
            _shown.Add(new Shown { Id = fractal.Id, Rest = fractal.PositionM, Phase = PhaseOf(fractal.Id) });
        _shown.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        // A picked one keeps its light while it goes out; the next choice, soon, gives the light to another.
        _chooseClock = Tuning.LightChooseS * 0.5f;
    }

    /// <summary>The pickup moment begins: the body and its glint go out, and a handful of sparks rise from where it rested.</summary>
    private void PickedUp(Shown shown)
    {
        shown.Leaving = 0f;
        var centre = BodyCentre(Form, shown.Rest, shown.Phase, LifeS);
        for (var j = 0; j < Tuning.SparksPerPickup; j++)
        {
            // The oldest sparks give way when every burst is in use.
            if (_sparkCount == _sparks.Length) { Array.Copy(_sparks, 1, _sparks, 0, _sparks.Length - 1); _sparkCount--; }
            var h1 = Hash(shown.Id, 11 + j);
            var h2 = Hash(shown.Id, 37 + j);
            var h3 = Hash(shown.Id, 71 + j);
            var angle = Mathf.Tau * (j + h1 * 0.6f) / Tuning.SparksPerPickup;
            var out_ = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            _sparks[_sparkCount++] = new Spark
            {
                From = centre + out_ * (Tuning.SparkSpreadM * (0.3f + 0.7f * h2)),
                Drift = out_ * Tuning.SparkSpreadM * (0.5f + h3) + Vector3.Up * Tuning.SparkRiseM * (0.7f + 0.6f * h1),
                Life = Tuning.SparkLifeS * (0.8f + 0.4f * h3), Phase = h2,
            };
        }
    }

    // ---------- the life, as pure functions (the look suite checks them) ----------

    /// <summary>A Fractal's own phase in [0, 1), from its id: its turn, bob and twinkle start there, so no two move or blink together.</summary>
    public static float PhaseOf(string id) => Hash(id, 0);

    private static float Hash(string id, int salt)
    {
        var hash = 2166136261u ^ (uint)salt * 16777619u;
        foreach (var c in id) hash = (hash ^ c) * 16777619u;
        hash ^= hash >> 15;
        hash *= 0x2c1b3c6du;
        hash ^= hash >> 12;
        return (hash & 0xFFFFFF) / (float)0x1000000;
    }

    private static float Cycle(double t, float period, float phase) => (float)((t / period + phase) % 1.0);

    /// <summary>The middle of a Fractal's body at a moment: its rest height plus the bob (the star's is a wisp's).</summary>
    public static Vector3 BodyCentre(FractalForm form, Vector3 rest, float phase, double t)
    {
        var (bob, period) = form == FractalForm.Star ? (Tuning.StarBobM, Tuning.StarBobPeriodS) : (Tuning.BobM, Tuning.BobPeriodS);
        return rest + Vector3.Up * (Tuning.RestCentreM + bob * Mathf.Sin(Mathf.Tau * Cycle(t, period, phase)));
    }

    /// <summary>How far round a Fractal's body has turned at a moment, radians.</summary>
    public static float Turn(float phase, double t) => Mathf.Tau * Cycle(t, Tuning.TurnPeriodS, phase * 7.31f % 1f);

    /// <summary>The body's transform at a moment (the star, which has no body, is its heart's place).</summary>
    public static Transform3D BodyTransform(FractalForm form, Vector3 rest, float phase, double t, float scale = 1f) =>
        new(new Basis(Vector3.Up, Turn(phase, t)).Scaled(Vector3.One * scale), BodyCentre(form, rest, phase, t));

    /// <summary>The box the body fills at a moment, from its mesh's extents (what the player reaches for is what they see).</summary>
    public static Aabb BodyBounds(FractalForm form, Vector3 rest, float phase, double t)
    {
        var (half, radius) = FractalMesh.Extents(form);
        var centre = BodyCentre(form, rest, phase, t);
        return new Aabb(centre - new Vector3(radius, half, radius), new Vector3(radius * 2f, half * 2f, radius * 2f));
    }

    /// <summary>The glint's brightness at a moment, TwinkleFloor to 1: a slow pulse on the Fractal's own period, peaking briefly.</summary>
    public static float Twinkle(float phase, double t)
    {
        var period = Mathf.Lerp(Tuning.TwinkleMinS, Tuning.TwinkleMaxS, phase * 13.37f % 1f);
        var wave = 0.5f + 0.5f * Mathf.Cos(Mathf.Tau * Cycle(t, period, phase * 3.7f % 1f));
        return Tuning.TwinkleFloor + (1f - Tuning.TwinkleFloor) * Mathf.Pow(wave, Tuning.TwinkleSharpness);
    }

    /// <summary>Where the glint stands at a moment: above the body's middle, riding its bob.</summary>
    public static Vector3 GlintAt(FractalForm form, Vector3 rest, float phase, double t) =>
        BodyCentre(form, rest, phase, t) + Vector3.Up * (form == FractalForm.Star ? 0f : Tuning.GlintLiftM);

    // ---------- drawing ----------

    private void Draw()
    {
        if (Bodies?.Multimesh is not { } bodies || Glints?.Multimesh is not { } glints || Sparks?.Multimesh is not { } sparks) return;
        var star = Form == FractalForm.Star;
        var starScale = Tuning.StarSizeM / Tuning.GlintSizeM;
        var lift = star ? 0f : Tuning.GlintLiftM;
        var count = Math.Min(_shown.Count, _capacity);
        for (var i = 0; i < count; i++)
        {
            var shown = _shown[i];
            var going = shown.Leaving >= 0f ? Mathf.Clamp(shown.Leaving / Tuning.PickupFadeS, 0f, 1f) : 0f;
            var shrink = (1f - going) * (1f - going);
            var fade = (1f + Tuning.PickupFlare * MathF.Sin(MathF.PI * going)) * (1f - going);
            var twinkle = Twinkle(shown.Phase, LifeS);
            var rays = Mathf.Tau * Cycle(LifeS, Tuning.RayTurnPeriodS, shown.Phase);
            var centre = BodyCentre(Form, shown.Rest, shown.Phase, LifeS);
            // The star has no body to turn: its quad of light faces the eye, so only its size moves.
            var turn = star ? 0f : Turn(shown.Phase, LifeS);
            Write(_formBuffer, i, turn, star ? starScale * shrink : shrink, centre, star ? (0.8f + 0.2f * twinkle) * fade : 1f, shown.Phase, rays * 0.5f, 1f);
            Write(_glintBuffer, i, 0f, 1f + going, centre + Vector3.Up * lift, twinkle * fade, shown.Phase, rays, 1f);
        }
        for (var i = 0; i < _sparkCount; i++)
        {
            var s = _sparks[i];
            var u = Mathf.Clamp(s.Age / s.Life, 0f, 1f);
            // Out quickly, slowing as they rise; bright at once, gone by the end.
            var ease = 1f - (1f - u) * (1f - u);
            var brightness = Mathf.Clamp(u * 12f, 0f, 1f) * MathF.Pow(1f - u, 1.5f);
            Write(_sparkBuffer, i, 0f, Tuning.SparkSizeM / Tuning.GlintSizeM * (1f - 0.4f * u), s.From + s.Drift * ease, brightness * 1.4f, s.Phase, s.Phase * Mathf.Tau + u * 2f, 0.6f);
        }
        bodies.VisibleInstanceCount = glints.VisibleInstanceCount = count;
        RenderingServer.MultimeshSetBuffer(bodies.GetRid(), _formBuffer);
        RenderingServer.MultimeshSetBuffer(glints.GetRid(), _glintBuffer);
        // The sparks' pool is written only while a pickup moment lasts.
        if (_sparkCount > 0 || sparks.VisibleInstanceCount > 0)
        {
            sparks.VisibleInstanceCount = _sparkCount;
            if (_sparkCount > 0) RenderingServer.MultimeshSetBuffer(sparks.GetRid(), _sparkBuffer);
        }
    }

    /// <summary>One instance: turned by yaw about the upright axis and scaled, at a place; white with a brightness; its custom data (rainbow, rays, ray amount).</summary>
    private static void Write(float[] buffer, int i, float yaw, float scale, Vector3 at, float brightness, float hue, float rays, float rayAmount)
    {
        var o = i * Stride;
        var c = MathF.Cos(yaw) * scale;
        var s = MathF.Sin(yaw) * scale;
        // The rows of Basis(Vector3.Up, yaw) scaled: (c, 0, s), (0, 1, 0), (-s, 0, c).
        buffer[o + 0] = c; buffer[o + 1] = 0f; buffer[o + 2] = s; buffer[o + 3] = at.X;
        buffer[o + 4] = 0f; buffer[o + 5] = scale; buffer[o + 6] = 0f; buffer[o + 7] = at.Y;
        buffer[o + 8] = -s; buffer[o + 9] = 0f; buffer[o + 10] = c; buffer[o + 11] = at.Z;
        buffer[o + 12] = 1f; buffer[o + 13] = 1f; buffer[o + 14] = 1f; buffer[o + 15] = brightness;
        buffer[o + 16] = hue; buffer[o + 17] = rays; buffer[o + 18] = rayAmount; buffer[o + 19] = 0f;
    }

    /// <summary>The instance a pool draws at an index, read back from what was last written (for the look suite).</summary>
    public (Transform3D Transform, Color Color) Instance(MultiMeshInstance3D pool, int index)
    {
        var buffer = pool == Bodies ? _formBuffer : pool == Glints ? _glintBuffer : _sparkBuffer;
        var o = index * Stride;
        var basis = new Basis(new Vector3(buffer[o], buffer[o + 4], buffer[o + 8]), new Vector3(buffer[o + 1], buffer[o + 5], buffer[o + 9]), new Vector3(buffer[o + 2], buffer[o + 6], buffer[o + 10]));
        return (new Transform3D(basis, new Vector3(buffer[o + 3], buffer[o + 7], buffer[o + 11])), new Color(buffer[o + 12], buffer[o + 13], buffer[o + 14], buffer[o + 15]));
    }

    /// <summary>
    /// The night's light: how much of it is on (the look's daylight through the lamps' switch), each light riding its Fractal's heart
    /// and going out with it when it is picked up, and every LightChooseS the lights given again to the Fractals nearest the player.
    /// </summary>
    private void Light(float dt)
    {
        if (_lights.Length == 0) return;
        Night = _look != null && IsInstanceValid(_look) && _look.Moment != null && _look.Preset != null
            ? 1f - Mathf.SmoothStep(_look.Preset.Tuning.Lamps.SwitchOnBelowDaylight * Tuning.LightFullAtSwitch, _look.Preset.Tuning.Lamps.SwitchOnBelowDaylight, _look.Moment.Daylight)
            : 0f;
        _chooseClock += dt;
        if (_chooseClock >= Tuning.LightChooseS) { _chooseClock = 0f; ChooseLit(); }
        for (var i = 0; i < _lights.Length; i++)
        {
            var light = _lights[i];
            var shown = _lit[i];
            var energy = 0f;
            if (shown != null && Night > 0f)
            {
                var going = shown.Leaving >= 0f ? Mathf.Clamp(shown.Leaving / Tuning.PickupFadeS, 0f, 1f) : 0f;
                energy = Tuning.LightEnergy * Night * (1f - going);
                light.Position = BodyCentre(Form, shown.Rest, shown.Phase, LifeS);
            }
            if (light.LightEnergy != energy) light.LightEnergy = energy;
            var on = energy > 0f;
            if (light.Visible != on) light.Visible = on;
        }
    }

    /// <summary>The lights to the Fractals on the island nearest the player (every one, when there are no more than the lights).</summary>
    private void ChooseLit()
    {
        Array.Clear(_lit);
        var anchor = _host?.Player is { } player && IsInstanceValid(player) ? player.GlobalPosition : (Vector3?)null;
        var count = 0;
        for (var i = 0; i < _shown.Count; i++)
        {
            var shown = _shown[i];
            if (shown.Leaving >= 0f) continue;
            if (count < _lit.Length) { _lit[count++] = shown; continue; }
            if (anchor is not { } at) break;
            // Replace the farthest lit one when this one is nearer.
            var farthest = 0;
            for (var k = 1; k < _lit.Length; k++)
                if (_lit[k]!.Rest.DistanceSquaredTo(at) > _lit[farthest]!.Rest.DistanceSquaredTo(at)) farthest = k;
            if (shown.Rest.DistanceSquaredTo(at) < _lit[farthest]!.Rest.DistanceSquaredTo(at)) _lit[farthest] = shown;
        }
    }

    /// <summary>The look's haze and sea mist, mirrored into the glint (the engine's own fog would add its colour over the quad).</summary>
    private void FollowHaze()
    {
        if (_glintMaterial == null) return;
        var environment = _look != null && IsInstanceValid(_look) ? _look.Environment : GetWorld3D()?.Environment;
        var fog = environment is { FogEnabled: true } e && e.FogMode == Godot.Environment.FogModeEnum.Depth
            ? (true, e.FogDepthBegin, e.FogDepthEnd, e.FogDensity, e.FogDepthCurve)
            : (false, 0f, 1f, 0f, 1f);
        if (fog == _fog) return;
        _fog = fog;
        foreach (var material in new[] { _glintMaterial, _starMaterial })
        {
            if (material == null) continue;
            material.SetShaderParameter("fog_on", fog.Item1 ? 1f : 0f);
            material.SetShaderParameter("fog_begin_m", fog.Item2);
            material.SetShaderParameter("fog_end_m", fog.Item3);
            material.SetShaderParameter("fog_density", fog.Item4);
            material.SetShaderParameter("fog_curve", fog.Item5);
        }
    }

    // ---------- materials ----------

    /// <summary>The body's material for a form (the star's body is the glint's shader, StarMaterial).</summary>
    public static ShaderMaterial FormMaterial(FractalForm form)
    {
        var material = new ShaderMaterial { Shader = GD.Load<Shader>(FormShaderPath), ResourceName = "fractal " + form.ToString().ToLowerInvariant() };
        material.SetShaderParameter("form", form == FractalForm.Seed ? 1 : 0);
        material.SetShaderParameter("body_color", Tuning.BodyColor);
        material.SetShaderParameter("tone_variation", Tuning.ToneVariation);
        material.SetShaderParameter("iridescence", Tuning.Iridescence);
        material.SetShaderParameter("inner_light", Tuning.InnerLight);
        material.SetShaderParameter("heart_light", Tuning.HeartLight);
        material.SetShaderParameter("wrap_light", Tuning.WrapLight);
        material.SetShaderParameter("sparkle", Tuning.Sparkle);
        material.SetShaderParameter("sparkle_power", Tuning.SparklePower);
        material.SetShaderParameter("sheen", Tuning.Sheen);
        material.SetShaderParameter("rim_light", Tuning.RimLight);
        material.SetShaderParameter("far_m", Tuning.FormFarM);
        material.SetShaderParameter("fade_band", Tuning.FormFadeBand);
        return material;
    }

    /// <summary>The glint's material, or the star's body (larger, softer, its own size up close and no growth with distance: its glint does that).</summary>
    public static ShaderMaterial GlintMaterial(bool star)
    {
        var material = new ShaderMaterial { Shader = GD.Load<Shader>(GlintShaderPath), ResourceName = star ? "fractal star" : "fractal glint" };
        material.SetShaderParameter("glint_color", star ? Tuning.BodyColor.Lerp(Tuning.GlintColor, 0.5f) : Tuning.GlintColor);
        material.SetShaderParameter("energy", star ? Tuning.StarEnergy : Tuning.GlintEnergy);
        material.SetShaderParameter("size_m", Tuning.GlintSizeM);
        material.SetShaderParameter("min_pixels", star ? 0f : Tuning.GlintMinPixels);
        material.SetShaderParameter("heart_sharpness", star ? Tuning.StarHeartSharpness : Tuning.GlintHeartSharpness);
        material.SetShaderParameter("halo", star ? Tuning.StarHalo : Tuning.GlintHalo);
        material.SetShaderParameter("ray_sharpness", Tuning.GlintRaySharpness);
        material.SetShaderParameter("rays", star ? Tuning.StarRays : Tuning.GlintRays);
        material.SetShaderParameter("rainbow", Tuning.GlintRainbow);
        material.SetShaderParameter("far_heart_sharpness", Tuning.GlintFarHeartSharpness);
        material.SetShaderParameter("far_ray_sharpness", Tuning.GlintFarRaySharpness);
        material.SetShaderParameter("near_m", Tuning.GlintNearM);
        material.SetShaderParameter("near_dim", star ? 1f : Tuning.GlintNearDim);
        material.SetShaderParameter("toward_camera_m", star ? 0f : Tuning.GlintTowardCameraM);
        material.SetShaderParameter("soft_m", star ? Tuning.StarSoftM : 0f);
        material.SetShaderParameter("fog_on", 0f);
        material.SetShaderParameter("fog_begin_m", 0f);
        material.SetShaderParameter("fog_end_m", 1f);
        material.SetShaderParameter("fog_density", 0f);
        material.SetShaderParameter("fog_curve", 1f);
        return material;
    }
}
