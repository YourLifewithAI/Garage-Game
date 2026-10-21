using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Room;

namespace GarageGame.Native.Look;

/// <summary>
/// The numbers of the Gubble's Fireworks (Run 2, Bubbles and Fireworks; docs/look/EFFECTS.md). Code constants for this round, like
/// GlowLook: the founder tunes them by playing, and a later preset version can carry them as a look block. The Bubbles have none
/// here since the founder's redesign (decisions 55 to 60): the Bubble is one player-sized bubble the kernel holds (Kernel/Bubble) and
/// LookDirector.Bubble.cs draws; the stream of small bubbles this file once drew is gone.
/// </summary>
public static class EffectLook
{
    /// <summary>The most effects the look draws at once, glows and fireworks together: the engine's cap of 8 active effects per room.</summary>
    public const int MaxEffects = 8;

    // ---------- fireworks (particles.burst plus light.emit) ----------

    /// <summary>A burst at a spot happens this high above it; a burst on the Gubble this high above its head.</summary>
    public const float BurstHeightM = 0.8f, SelfBurstHeightM = 0.3f;
    /// <summary>The rocket's climb: RocketBaseS plus RocketSecondsPerM a metre, at most RocketMaxS.</summary>
    public const float RocketBaseS = 0.35f, RocketSecondsPerM = 0.25f, RocketMaxS = 1.0f;
    /// <summary>Sparks a burst throws: SparksBase plus SparksPerIntensity times the intensity, at most MaxSparks.</summary>
    public const int SparksBase = 24, SparksPerIntensity = 48, MaxSparks = 96;
    /// <summary>The trail of embers the rocket leaves, at most this many at once, each this long-lived.</summary>
    public const int MaxEmbers = 32;
    public const float EmberLifeS = 0.45f, EmberEveryS = 0.018f;
    /// <summary>The sparks fly out to the radius in about SparkOutS, slowed by SparkDrag a second and pulled down by SparkGravity.</summary>
    public const float SparkOutS = 0.55f, SparkDrag = 2.2f, SparkGravity = 0.35f, SparkLifeMinS = 0.9f, SparkLifeMaxS = 1.6f;
    /// <summary>A spark's size, metres, and how far over 1 its colour is pushed so the bloom catches it.</summary>
    public const float SparkM = 0.022f, SparkHot = 2.6f, EmberM = 0.014f;
    /// <summary>A burst on the Gubble is this much of the radius it is given.</summary>
    public const float SelfBurstScale = 0.5f;
    /// <summary>
    /// The flash: a brief omni light at the burst that lights the land for a moment. Its energy is FlashPerIntensity times the
    /// intensity at its peak (FlashRiseS in), gone FlashS after the burst; it reaches FlashRangeScale times the radius plus the burst's
    /// height (at most the engine's 3 m), and never casts a shadow, so it costs no shadow slot.
    /// </summary>
    public const float FlashPerIntensity = 2.0f, FlashRiseS = 0.04f, FlashS = 0.7f, FlashRangeScale = 1.5f, FlashMaxRangeM = 3f, FlashAttenuation = 1.0f;
    /// <summary>While a fireworks effect lasts, another rocket goes up this often (the host's duration decides how many).</summary>
    public const float VolleyEveryS = 3.5f;
}

public partial class LookDirector
{
    /// <summary>What kind of effect an id draws.</summary>
    public enum EffectKind { None, Glow, Fireworks }

    private sealed class Particle
    {
        public Vector3 Position, Velocity;
        public float Radius, Age, Life;
        public bool Alive;
        public Color Tint;
    }

    private sealed class Effect
    {
        public string Id = "";
        public EffectKind Kind;
        public Node3D Root = null!;
        public MultiMeshInstance3D Draw = null!;
        public Node3D? Follow;
        public Vector3 Center;
        public float RadiusM, Intensity;
        public Color Color;
        public Particle[] Items = Array.Empty<Particle>();
        public float Clock, NextVolley;
        public RandomNumberGenerator Random = new();
        // Fireworks: the rocket in flight, the burst point, the flash.
        public OmniLight3D? Flash;
        public bool RocketUp;
        public Vector3 RocketFrom, Burst;
        public float RocketS, RocketAge, FlashAge = -1f, EmberClock;
        public int Bursts;
    }

    private readonly Dictionary<string, Effect> _effects = new();
    private readonly List<Effect> _effectOrder = new();

    /// <summary>How many effects the look is drawing: glows and fireworks together (at most EffectLook.MaxEffects).</summary>
    public int EffectCount => _glows.Count + _effects.Count;

    /// <summary>What an effect id draws (None for an unknown id).</summary>
    public EffectKind KindOf(string effectId) =>
        effectId == null ? EffectKind.None : _glows.ContainsKey(effectId) ? EffectKind.Glow : _effects.TryGetValue(effectId, out var e) ? e.Kind : EffectKind.None;

    /// <summary>Whether an effect with this id is drawn, of any kind.</summary>
    public bool HasEffect(string effectId) => KindOf(effectId) != EffectKind.None;

    /// <summary>A fireworks effect's root node, for tests and the review harness; glows have GlowRoot.</summary>
    public Node3D? EffectRoot(string effectId) => effectId != null && _effects.TryGetValue(effectId, out var e) ? e.Root : null;

    /// <summary>A fireworks effect's flash light while it is lit (null between bursts, and for any other id).</summary>
    public OmniLight3D? FlashLight(string effectId) => effectId != null && _effects.TryGetValue(effectId, out var e) && e.Flash != null && IsInstanceValid(e.Flash) ? e.Flash : null;

    /// <summary>The live sparks' and embers' positions and radii, for tests and the review harness.</summary>
    public IReadOnlyList<(Vector3 Position, float Radius)> EffectParticles(string effectId) =>
        effectId != null && _effects.TryGetValue(effectId, out var e) ? e.Items.Where(p => p.Alive).Select(p => (p.Position, p.Radius)).ToArray() : Array.Empty<(Vector3, float)>();

    /// <summary>How many bursts a fireworks effect has made; for tests.</summary>
    public int Bursts(string effectId) => effectId != null && _effects.TryGetValue(effectId, out var e) ? e.Bursts : 0;

    /// <summary>
    /// The wind of the world's weather (m/s in XZ), handed over by the host (CommandHostNature sets it with the weather). Nothing in
    /// the look reads it since the stream of small bubbles went (the Bubble is one bubble the kernel moves); it stays as the host's seam.
    /// </summary>
    public Vector2? Wind { get; set; }

    /// <summary>
    /// Start the Gubble's fireworks (particles.burst with a brief light.emit), cosmetic only. Without follow, a rocket climbs from the
    /// Gubble (when one is drawn; else from the spot) and bursts BurstHeightM above center into sparks reaching radiusM, with a
    /// flash that lights the land for a moment; with follow, a smaller burst over that body. Another rocket goes up every
    /// VolleyEveryS while the effect lasts. intensity (0.05 to 2.0) sets the sparks and the flash. The flash is a real light within
    /// the budgets (no shadow) and counts toward LightLevelAt while it is lit. Colours from the aura. The same rules as StartGlow.
    /// </summary>
    public bool StartFireworks(string effectId, Node3D? follow, Vector3 center, float radiusM, float intensity) =>
        StartParticles(EffectKind.Fireworks, effectId, follow, center, radiusM, intensity);

    /// <summary>End an effect of any kind (a glow or fireworks) and free everything it drew. An unknown id is a no-op.</summary>
    public void StopEffect(string effectId)
    {
        if (effectId == null) return;
        if (_glows.ContainsKey(effectId)) StopGlow(effectId);
        else FreeEffect(effectId);
    }

    /// <summary>End every effect: every glow and every fireworks.</summary>
    public void StopAllEffects()
    {
        StopAllGlows();
        for (var i = _effectOrder.Count - 1; i >= 0; i--) FreeEffect(_effectOrder[i].Id);
    }

    private bool UsableEffect(string effectId, Node3D? follow, Vector3 center, float radiusM, float intensity)
    {
        if (string.IsNullOrEmpty(effectId) || effectId.Length > 128 || !float.IsFinite(radiusM) || !float.IsFinite(intensity)) return false;
        if (follow != null && (!IsInstanceValid(follow) || follow.IsQueuedForDeletion() || !follow.IsInsideTree())) return false;
        if (follow == null && !(float.IsFinite(center.X) && float.IsFinite(center.Y) && float.IsFinite(center.Z))) return false;
        return HasEffect(effectId) || EffectCount < EffectLook.MaxEffects;
    }

    private bool StartParticles(EffectKind kind, string effectId, Node3D? follow, Vector3 center, float radiusM, float intensity)
    {
        if (!UsableEffect(effectId, follow, center, radiusM, intensity)) return false;
        StopEffect(effectId);
        var effect = new Effect
        {
            Id = effectId, Kind = kind, Follow = follow, Center = center,
            RadiusM = Mathf.Clamp(radiusM, GlowLook.MinRadiusM, GlowLook.MaxRadiusM), Intensity = Mathf.Clamp(intensity, GlowLook.MinIntensity, GlowLook.MaxIntensity),
            Color = GlowLook.LightColor(AuraOf(follow ?? CompanionBody())),
        };
        effect.Random.Seed = (ulong)(Seed(effectId) * 1000f) + 17ul;
        var count = EffectLook.MaxSparks + EffectLook.MaxEmbers;
        effect.Items = Enumerable.Range(0, count).Select(_ => new Particle()).ToArray();
        effect.Root = new Node3D { Name = "Fireworks_" + RoomBuilder.NodeName(effectId), TopLevel = true };
        effect.Root.SetMeta(GlowMeta, effectId);
        var multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, InstanceCount = count,
            Mesh = new QuadMesh { Size = Vector2.One },
        };
        for (var i = 0; i < count; i++) multimesh.SetInstanceTransform(i, new Transform3D(Basis.Identity.Scaled(Vector3.Zero), Vector3.Zero));
        effect.Draw = new MultiMeshInstance3D
        {
            Name = "SparkDraw", Multimesh = multimesh, MaterialOverride = SparkMaterial(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
        };
        effect.Root.AddChild(effect.Draw);
        AddChild(effect.Root);
        _effects[effectId] = effect;
        _effectOrder.Add(effect);
        Launch(effect);
        return true;
    }

    private bool FreeEffect(string effectId)
    {
        if (!_effects.Remove(effectId, out var effect)) return false;
        _effectOrder.Remove(effect);
        if (IsInstanceValid(effect.Root)) effect.Root.Free();
        return true;
    }

    /// <summary>The Gubble's middle when it is drawn (where rockets leave), else null.</summary>
    private Vector3? GubbleMiddle()
    {
        var gubble = CompanionBody();
        if (gubble == null || !IsInstanceValid(gubble) || !gubble.IsInsideTree() || !gubble.IsVisibleInTree()) return null;
        return gubble.GetGlobalTransformInterpolated().Origin + Vector3.Up * ((gubble as SmallPlayerController)?.BodyHeightM ?? 0f) * GlowLook.FollowHeightFraction;
    }

    private Vector3 FollowMiddle(Effect effect)
    {
        var body = effect.Follow!;
        return body.GetGlobalTransformInterpolated().Origin + Vector3.Up * ((body as SmallPlayerController)?.BodyHeightM ?? 0f) * GlowLook.FollowHeightFraction;
    }

    private void UpdateEffects(double delta)
    {
        if (_effectOrder.Count == 0) return;
        var dt = (float)Math.Min(delta, 0.1);
        for (var i = _effectOrder.Count - 1; i >= 0; i--)
        {
            var effect = _effectOrder[i];
            if (effect.Follow != null && !IsInstanceValid(effect.Follow)) { FreeEffect(effect.Id); continue; }
            effect.Clock += dt;
            AdvanceFireworks(effect, dt);
            DrawParticles(effect);
        }
    }

    // ---------- fireworks ----------

    private void Launch(Effect effect)
    {
        var self = effect.Follow != null && IsInstanceValid(effect.Follow) && effect.Follow.IsInsideTree();
        var body = self ? effect.Follow as SmallPlayerController : null;
        effect.Burst = self
            ? effect.Follow!.GlobalPosition + Vector3.Up * ((body?.BodyHeightM ?? 0f) + EffectLook.SelfBurstHeightM)
            : effect.Center + Vector3.Up * EffectLook.BurstHeightM;
        effect.RocketFrom = self ? FollowMiddle(effect) : GubbleMiddle() ?? effect.Center + Vector3.Up * 0.01f;
        effect.RocketS = Mathf.Min(EffectLook.RocketMaxS, EffectLook.RocketBaseS + EffectLook.RocketSecondsPerM * effect.RocketFrom.DistanceTo(effect.Burst));
        effect.RocketAge = 0f;
        effect.RocketUp = true;
        effect.EmberClock = 0f;
        effect.NextVolley = effect.Clock + EffectLook.VolleyEveryS;
    }

    /// <summary>Where the rocket is a time into its climb: from where it left, curving up to the burst (fast at first, slowing at the top).</summary>
    private static Vector3 RocketAt(Effect effect, float t)
    {
        t = Mathf.Clamp(t, 0f, 1f);
        var eased = 1f - (1f - t) * (1f - t);
        var flat = effect.RocketFrom.Lerp(effect.Burst, t);
        return new Vector3(flat.X, Mathf.Lerp(effect.RocketFrom.Y, effect.Burst.Y, eased), flat.Z);
    }

    private void AdvanceFireworks(Effect effect, float dt)
    {
        var r = effect.Random;
        var sparks = effect.Items.AsSpan(0, EffectLook.MaxSparks);
        var embers = effect.Items.AsSpan(EffectLook.MaxSparks);
        if (effect.RocketUp)
        {
            effect.RocketAge += dt;
            effect.EmberClock += dt;
            var at = RocketAt(effect, effect.RocketAge / effect.RocketS);
            // The rocket's head is the first ember slot; its trail fills the rest.
            var head = embers[0];
            head.Alive = true;
            head.Position = at;
            head.Radius = EffectLook.EmberM * 0.75f;
            head.Life = 1e9f;
            head.Tint = new Color(1f, 0.92f, 0.75f) * EffectLook.SparkHot;
            while (effect.EmberClock >= EffectLook.EmberEveryS)
            {
                effect.EmberClock -= EffectLook.EmberEveryS;
                var free = -1;
                for (var k = 1; k < embers.Length; k++) if (!embers[k].Alive) { free = k; break; }
                if (free < 0) break;
                var e = embers[free];
                e.Alive = true;
                e.Age = 0f;
                e.Life = EffectLook.EmberLifeS * r.RandfRange(0.7f, 1.2f);
                e.Position = at + new Vector3(r.RandfRange(-1f, 1f), r.RandfRange(-1f, 1f), r.RandfRange(-1f, 1f)) * 0.004f;
                e.Velocity = new Vector3(r.RandfRange(-1f, 1f), -1f, r.RandfRange(-1f, 1f)) * 0.03f;
                e.Radius = EffectLook.EmberM * r.RandfRange(0.6f, 1f);
                e.Tint = new Color(1f, 0.8f, 0.5f) * (EffectLook.SparkHot * 0.7f);
            }
            if (effect.RocketAge >= effect.RocketS)
            {
                effect.RocketUp = false;
                head.Alive = false;
                Explode(effect);
            }
        }
        foreach (var p in effect.Items)
        {
            if (!p.Alive || p == embers[0] && effect.RocketUp) continue;
            p.Age += dt;
            if (p.Age >= p.Life) { p.Alive = false; continue; }
            p.Velocity *= Mathf.Max(0f, 1f - EffectLook.SparkDrag * dt);
            p.Velocity += Vector3.Down * EffectLook.SparkGravity * dt;
            p.Position += p.Velocity * dt;
        }
        if (effect.Flash != null && IsInstanceValid(effect.Flash))
        {
            effect.FlashAge += dt;
            if (effect.FlashAge >= EffectLook.FlashS) { effect.Flash.Free(); effect.Flash = null; effect.FlashAge = -1f; }
            else effect.Flash.LightEnergy = FlashEnergy(effect);
        }
        if (!effect.RocketUp && effect.Clock >= effect.NextVolley) Launch(effect);
    }

    private void Explode(Effect effect)
    {
        var r = effect.Random;
        effect.Bursts++;
        var self = effect.Follow != null;
        var radius = effect.RadiusM * (self ? EffectLook.SelfBurstScale : 1f);
        var count = Math.Min(EffectLook.MaxSparks, EffectLook.SparksBase + (int)(EffectLook.SparksPerIntensity * effect.Intensity));
        // The sparks' speed so that, slowed by the drag, they reach the radius: distance = v / drag × (1 - e^(-drag × t)).
        var speed = radius * EffectLook.SparkDrag / (1f - Mathf.Exp(-EffectLook.SparkDrag * EffectLook.SparkOutS));
        var accent = new Color(1f, 0.93f, 0.78f);
        for (var k = 0; k < count; k++)
        {
            var p = effect.Items[k];
            // An even spread over the sphere (a Fibonacci lattice), turned by the seed so bursts differ.
            var y = 1f - 2f * (k + 0.5f) / count;
            var ring = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
            var a = k * 2.39996323f + r.RandfRange(0f, Mathf.Tau) * 0.15f + effect.Bursts;
            var direction = new Vector3(Mathf.Cos(a) * ring, y, Mathf.Sin(a) * ring);
            p.Alive = true;
            p.Age = 0f;
            p.Life = r.RandfRange(EffectLook.SparkLifeMinS, EffectLook.SparkLifeMaxS);
            p.Position = effect.Burst;
            p.Velocity = direction * speed * r.RandfRange(0.85f, 1.05f);
            p.Radius = EffectLook.SparkM * r.RandfRange(0.7f, 1.1f) * (self ? 0.75f : 1f);
            p.Tint = (k % 4 == 0 ? accent : effect.Color) * EffectLook.SparkHot;
        }
        if (effect.Flash != null && IsInstanceValid(effect.Flash)) effect.Flash.Free();
        effect.Flash = new OmniLight3D
        {
            Name = "Flash", TopLevel = true, Position = effect.Burst, LightColor = effect.Color, LightEnergy = 0f,
            OmniRange = FlashRange(effect), OmniAttenuation = EffectLook.FlashAttenuation, ShadowEnabled = false,
            LightBakeMode = Light3D.BakeMode.Disabled, LightSpecular = GlowLook.Specular,
        };
        effect.FlashAge = 0f;
        effect.Root.AddChild(effect.Flash);
        effect.Flash.LightEnergy = FlashEnergy(effect);
    }

    private static float FlashRange(Effect effect) =>
        Mathf.Min(EffectLook.FlashMaxRangeM, effect.RadiusM * (effect.Follow != null ? EffectLook.SelfBurstScale : 1f) * EffectLook.FlashRangeScale
            + (effect.Follow != null ? EffectLook.SelfBurstHeightM : EffectLook.BurstHeightM));

    /// <summary>The flash's energy now: up fast to its peak, then fading to nothing by FlashS.</summary>
    private static float FlashEnergy(Effect effect)
    {
        if (effect.FlashAge < 0f) return 0f;
        var peak = EffectLook.FlashPerIntensity * effect.Intensity;
        if (effect.FlashAge < EffectLook.FlashRiseS) return peak * effect.FlashAge / EffectLook.FlashRiseS;
        var t = Mathf.Clamp((effect.FlashAge - EffectLook.FlashRiseS) / (EffectLook.FlashS - EffectLook.FlashRiseS), 0f, 1f);
        return peak * (1f - t) * (1f - t);
    }

    /// <summary>What the fireworks' flashes give a spot now, for the light-level estimate (brief: only while a flash is lit).</summary>
    private float FlashesAt(Vector3 position)
    {
        var sum = 0f;
        foreach (var effect in _effectOrder)
            if (effect.Flash != null && IsInstanceValid(effect.Flash))
                sum += GlowLook.PointLight(effect.Flash.LightEnergy, effect.Flash.LightColor, effect.Flash.OmniRange, effect.Flash.OmniAttenuation, effect.Burst.DistanceTo(position));
        return sum;
    }

    // ---------- drawing ----------

    private void DrawParticles(Effect effect)
    {
        var multimesh = effect.Draw.Multimesh;
        for (var i = 0; i < effect.Items.Length; i++)
        {
            var p = effect.Items[i];
            if (!p.Alive) { multimesh.SetInstanceTransform(i, new Transform3D(Basis.Identity.Scaled(Vector3.Zero), Vector3.Zero)); continue; }
            var fade = Mathf.Clamp(1f - p.Age / p.Life, 0f, 1f);
            var size = 2f * p.Radius * (0.6f + 0.4f * fade);
            var alpha = fade * fade;
            multimesh.SetInstanceTransform(i, new Transform3D(Basis.Identity.Scaled(Vector3.One * size), p.Position));
            multimesh.SetInstanceColor(i, new Color(p.Tint.R, p.Tint.G, p.Tint.B, alpha));
        }
    }

    /// <summary>A spark or an ember: a soft dot of burning light, added to what is behind it, its colour (over 1, for the bloom) per spark.</summary>
    private StandardMaterial3D SparkMaterial() => new()
    {
        ResourceName = "spark", AlbedoTexture = HeartTexture(), VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
        BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled, BillboardKeepScale = true, DisableReceiveShadows = true,
    };
}
