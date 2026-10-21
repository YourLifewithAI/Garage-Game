using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Look;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Look;

/// <summary>
/// Run 2, Glow, Lane L: the Gubble's glow is a real light (an omni light in the look's budgets, a halo on the Gubble or a wisp at a
/// point, in its aura colour or a warm white-gold), and LightLevelAt estimates how lit a spot is (the sun or moon, the sky overhead,
/// bounce, lamps and glows). The Glow playtest round: a wisp is light with no solid core, a cast at a point is a spark from the Gubble
/// that blooms where it lands, moonlight alone is dark, and RecolorGlows. How the glow looks needs the GPU (docs/look/GLOW.md);
/// these checks are what holds headless.
/// </summary>
public partial class LookPresetTest
{
    private async Task CheckGlow(StylePreset preset, RoomData room)
    {
        CheckGlowNumbers();
        await CheckGlowLifecycle(preset, room);
        await CheckGlowFollowAndPoint(preset, room);
        CheckGlowCap(preset, room);
        await CheckGlowBudget(preset);
        await CheckLightLevel(preset, room);
    }

    private void CheckGlowNumbers()
    {
        var gold = GlowLook.DefaultColor;
        Check(gold.R >= gold.G && gold.G > gold.B && gold.B >= 0.55f && gold.R > 0.95f, $"a glow without an aura is a warm white-gold (#{gold.ToHtml(false)})");
        Check(GlowLook.LightColor(null) == gold && GlowLook.LightColor(new Color(0f, 0f, 0f)) == gold && GlowLook.LightColor(new Color(float.NaN, 0f, 0f)) == gold,
            "no aura, a black aura or a broken one gives the white-gold");
        var blue = GlowLook.LightColor(new Color(0.05f, 0.1f, 0.4f));
        Check(Mathf.IsEqualApprox(blue.B, 1f) && blue.B > blue.G && blue.G > blue.R && blue.R >= GlowLook.AuraWhiteMix - 1e-4f,
            $"an aura's hue becomes the light's at full brightness, lifted toward white so a deep blue still lights the ground (#{blue.ToHtml(false)})");
        Check(GlowLook.MaxGlows > 0 && GlowLook.MaxGlows <= 8 && GlowLook.MaxShadowedGlows <= 1, $"the look draws at most {GlowLook.MaxGlows} glows (8 or fewer), at most {GlowLook.MaxShadowedGlows} with a shadow");
        Check(Mathf.IsEqualApprox(GlowLook.Energy(0.6f), 0.6f * GlowLook.EnergyPerIntensity) && Mathf.IsEqualApprox(GlowLook.Energy(9f), GlowLook.Energy(GlowLook.MaxIntensity))
            && Mathf.IsEqualApprox(GlowLook.Energy(0f), GlowLook.Energy(GlowLook.MinIntensity)) && GlowLook.MinIntensity == 0.05f && GlowLook.MaxIntensity == 2f,
            "a glow's energy follows its intensity inside the engine's bounds, 0.05 to 2.0");
        Check(GlowLook.Level(0f) == 0f && GlowLook.Level(GlowLook.LevelFloorEnergy) == 0f && Mathf.IsEqualApprox(GlowLook.Level(GlowLook.LevelFullEnergy), 1f)
            && GlowLook.Level(1000f) == 1f && GlowLook.Level(float.NaN) == 0f && Mathf.IsEqualApprox(GlowLook.Level(GlowLook.EnergyForLevel(LookDirector.DarkThreshold)), LookDirector.DarkThreshold, 1e-4f),
            "the light level runs from 0 to 1 and is the inverse of EnergyForLevel");
    }

    private async Task CheckGlowLifecycle(StylePreset preset, RoomData room)
    {
        var (holder, look) = NewDirector(preset, room, "GlowLifeHolder");
        await Frames(1);
        var at = new Vector3(0.4f, 0f, -0.3f);
        var lightsBefore = look.FindChildren("*", "Light3D", true, false).Count;
        Check(look.GlowCount == 0 && !look.HasGlow("effect:a") && look.GlowLight("effect:a") == null, "a new look draws no glow");
        Check(look.StartGlow("effect:a", null, at, 0.6f, 0.6f), "a glow starts at a point");
        var first = look.GlowLight("effect:a");
        var firstRoot = look.GlowRoot("effect:a");
        Check(first != null && firstRoot != null && look.IsAncestorOf(first) && Mathf.IsEqualApprox(first.OmniRange, 0.6f) && Mathf.IsEqualApprox(first.LightEnergy, GlowLook.Energy(0.6f))
            && Mathf.IsEqualApprox(first.OmniAttenuation, GlowLook.Attenuation) && first.LightColor.IsEqualApprox(GlowLook.DefaultColor) && first.Visible,
            "it is a real omni light of the look: its range is the radius and its energy follows the intensity, in white-gold");
        Check(look.GlowSpark("effect:a") == null && firstRoot!.GetNode<MeshInstance3D>("Halo").Visible && firstRoot.GetNode<MeshInstance3D>("Core").Visible,
            "with no Gubble drawn (a fixture) there is no spark: the wisp is simply there, lit");
        Check(firstRoot!.TopLevel && firstRoot.Position.IsEqualApprox(at + Vector3.Up * GlowLook.WispLiftM) && firstRoot.GetMeta(LookDirector.GlowMeta).AsString() == "effect:a",
            "the wisp floats just above its point, in world space");
        Check(look.StartGlow("effect:a", null, at + new Vector3(0.5f, 0f, 0f), 1.2f, 1.0f) && look.GlowCount == 1, "starting an id already in use replaces it: still one glow");
        var second = look.GlowLight("effect:a");
        Check(!IsInstanceValid(first) && !IsInstanceValid(firstRoot) && second != null && Mathf.IsEqualApprox(second.OmniRange, 1.2f) && Mathf.IsEqualApprox(second.LightEnergy, GlowLook.Energy(1f))
            && look.GlowRoot("effect:a")!.Position.IsEqualApprox(at + new Vector3(0.5f, GlowLook.WispLiftM, 0f)),
            "the old glow is freed and the new one has the new place, radius and intensity");
        var secondRoot = look.GlowRoot("effect:a")!;
        look.StopGlow("effect:a");
        Check(look.GlowCount == 0 && !look.HasGlow("effect:a") && !IsInstanceValid(second) && !IsInstanceValid(secondRoot), "stopping a glow frees its light, halo and core at once");
        Check(look.FindChildren("Glow_*", "", true, false).Count == 0 && look.FindChildren("*", "Light3D", true, false).Count == lightsBefore, "nothing of it is left under the look");
        var children = look.GetChildCount();
        look.StopGlow("effect:a");
        look.StopGlow("effect:never");
        look.StopGlow("");
        Check(look.GlowCount == 0 && look.GetChildCount() == children, "stopping an unknown id (or one already stopped) is a no-op");
        Check(look.StartGlow("effect:a", null, at, 0.6f, 0.6f) && look.GlowCount == 1, "a freed id can be used again");
        look.StopAllGlows();
        Check(look.GlowCount == 0 && look.FindChildren("Glow_*", "", true, false).Count == 0, "StopAllGlows ends every glow");
        // Unusable calls draw nothing.
        var gone = new Node3D { Name = "Gone" };
        holder.AddChild(gone);
        gone.Free();
        var outside = new Node3D { Name = "Outside" };
        var refused = new[]
        {
            look.StartGlow("", null, at, 0.6f, 0.6f), look.StartGlow("effect:nan", null, new Vector3(float.NaN, 0f, 0f), 0.6f, 0.6f),
            look.StartGlow("effect:r", null, at, float.PositiveInfinity, 0.6f), look.StartGlow("effect:i", null, at, 0.6f, float.NaN),
            look.StartGlow("effect:gone", gone, at, 0.6f, 0.6f), look.StartGlow("effect:outside", outside, at, 0.6f, 0.6f),
        };
        outside.Free();
        Check(refused.All(r => !r) && look.GlowCount == 0 && look.GetChildCount() == children, "an empty id, a number that is not finite, or a body that is gone or not in the scene is refused, and draws nothing");
        Check(look.StartGlow("effect:big", null, at, 9f, 5f) && Mathf.IsEqualApprox(look.GlowLight("effect:big")!.OmniRange, GlowLook.MaxRadiusM)
            && Mathf.IsEqualApprox(look.GlowLight("effect:big")!.LightEnergy, GlowLook.Energy(GlowLook.MaxIntensity)),
            "a radius or intensity past the engine's bounds is held to them (3 m, 2.0)");
        look.StopAllGlows();
        holder.Free();
    }

    private async Task CheckGlowFollowAndPoint(StylePreset preset, RoomData room)
    {
        var (holder, look) = NewDirector(preset, room, "GlowFollowHolder");
        var gubble = new CompanionAvatar { Name = "Gubble" };
        holder.AddChild(gubble);
        gubble.SetPhysicsProcess(false);
        gubble.GlobalPosition = new Vector3(0.5f, 0f, 0.2f);
        var player = new SmallPlayerController { Name = "Player", ReadKeyboard = false };
        holder.AddChild(player);
        player.SetPhysicsProcess(false);
        player.GlobalPosition = new Vector3(0.3f, 0f, 0.2f);
        await Frames(1);
        Check(look.StartGlow("effect:halo", gubble, new Vector3(9f, 9f, 9f), 0.6f, 0.6f), "a glow starts on the Gubble");
        var light = look.GlowLight("effect:halo")!;
        var root = look.GlowRoot("effect:halo")!;
        var lift = gubble.BodyHeightM * GlowLook.FollowHeightFraction;
        Check(light.GlobalPosition.IsEqualApprox(gubble.GlobalPosition + Vector3.Up * lift), $"the halo's light sits in the Gubble's middle ({lift:0.###} m up), not at the point it was given");
        var halo = root.GetNode<MeshInstance3D>("Halo");
        var opening = halo.Scale.X;
        Check(look.GlowSpark("effect:halo") == null && Mathf.IsEqualApprox(opening, GlowLook.BloomFrom) && Mathf.IsEqualApprox(light.LightEnergy, GlowLook.Energy(0.6f)),
            $"a glow on the Gubble has no spark: its halo blooms out of the body (from {opening:0.##} of its size) around a light lit at once");
        for (var i = 0; i < 30; i++) look._Process(1.0 / 60.0);
        Check(halo.Scale.X > 1f - GlowLook.BreathAmount - 1e-4f && halo.Visible, $"within half a second the halo has opened ({halo.Scale.X:0.##})");
        var haloMaterial = halo.MaterialOverride as StandardMaterial3D;
        Check(root.GetNodeOrNull("Core") == null && halo.Mesh is QuadMesh quad && Mathf.IsEqualApprox(quad.Size.X, gubble.BodyHeightM * GlowLook.HaloBodyHeights)
            && haloMaterial is { BillboardMode: BaseMaterial3D.BillboardModeEnum.Enabled, BillboardKeepScale: true, BlendMode: BaseMaterial3D.BlendModeEnum.Add, ShadingMode: BaseMaterial3D.ShadingModeEnum.Unshaded }
            && haloMaterial.AlbedoTexture is GradientTexture2D && haloMaterial.ProximityFadeEnabled,
            "on the Gubble the glow is a soft halo: a camera-facing additive disc two body heights across, soft where it meets a surface, with no wisp core");
        Check(!light.ShadowEnabled && halo.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off && halo.GIMode == GeometryInstance3D.GIModeEnum.Disabled,
            "a halo casts no shadow (its light sits inside the Gubble's body) and its disc casts none either");
        Check(haloMaterial!.DistanceFadeMode == BaseMaterial3D.DistanceFadeModeEnum.PixelAlpha && haloMaterial.DistanceFadeMinDistance < haloMaterial.DistanceFadeMaxDistance
            && haloMaterial.DistanceFadeMaxDistance <= CompanionAvatar.FollowNearM + 0.1f,
            "the halo fades out only when a camera is right inside it, so the eye view beside the Gubble is not washed out");
        gubble.GlobalPosition = new Vector3(-0.4f, 0.1f, -0.3f);
        look._Process(1.0 / 60.0);
        Check(light.GlobalPosition.IsEqualApprox(gubble.GlobalPosition + Vector3.Up * lift), "the halo moves with the Gubble");
        var sizes = new List<float>();
        for (var i = 0; i < 240; i++) { look._Process(1.0 / 60.0); sizes.Add(halo.Scale.X); }
        Check(sizes.Max() - sizes.Min() > GlowLook.BreathAmount && sizes.Max() <= 1f + GlowLook.BreathAmount + 1e-4f && light.GlobalPosition.IsEqualApprox(gubble.GlobalPosition + Vector3.Up * lift),
            "the halo breathes gently while it stays on the Gubble");
        // Every view and any time of day: the look hides no glow by view or hour, and the halo is on the cameras' layers.
        var shown = true;
        var anyCamera = new Camera3D { Name = "AnyView" };
        holder.AddChild(anyCamera);
        foreach (var view in Enum.GetValues<LookView>())
        {
            look.View = view;
            look.Observe = view == LookView.Diorama;
            look._Process(1.0 / 60.0);
            shown &= light.Visible && halo.Visible && root.Visible && (halo.Layers & player.EyeCamera.CullMask) != 0 && (halo.Layers & anyCamera.CullMask) != 0;
        }
        look.View = null;
        look.Observe = false;
        var energy = light.LightEnergy;
        look.SetClock(2f, 172);
        var night = light.LightEnergy;
        look.SetClock(12f, 172);
        Check(shown && Mathf.IsEqualApprox(night, energy) && Mathf.IsEqualApprox(light.LightEnergy, energy) && light.Visible,
            "the glow shows in every view (eye, shoulder, diorama, isometric, observe) and at any hour, at its own brightness: a real light the clock does not dim");
        // A wisp at a point: a spark leaves the Gubble and arcs to the spot, where the wisp blooms (a soft heart, a small halo, a gentle bob).
        var point = new Vector3(-1f, 0f, 0.5f);
        var lights = look.FindChildren("*", "Light3D", true, false).Count;
        Check(look.StartGlow("effect:wisp", null, point, 0.6f, 0.6f), "a glow starts at a point beside the halo");
        var wisp = look.GlowRoot("effect:wisp")!;
        var wispLight = look.GlowLight("effect:wisp")!;
        var wispHalo = wisp.GetNode<MeshInstance3D>("Halo");
        var core = wisp.GetNodeOrNull<MeshInstance3D>("Core");
        var spark = look.GlowSpark("effect:wisp");
        var restAt = point + Vector3.Up * GlowLook.WispLiftM;
        var from = gubble.GlobalPosition + Vector3.Up * lift;
        var head = spark?.GetNodeOrNull<MeshInstance3D>("Head");
        Check(spark != null && head != null && head.GlobalPosition.IsEqualApprox(from) && wisp.Position.IsEqualApprox(restAt) && wispLight.LightEnergy == 0f && !wispHalo.Visible && core is { Visible: false }
            && look.FindChildren("*", "Light3D", true, false).Count == lights + 1 && look.EstimateLightAt(point).Glows > 0f,
            "cast at a point, the glow leaves the Gubble as a spark from its middle; the wisp waits at its spot, unlit, with no light of the spark's own; the light level counts it at once");
        Check(spark != null && spark.GetChildren().OfType<MeshInstance3D>().Count() == GlowLook.SparkEmbers + 1 && spark.GetChildren().OfType<MeshInstance3D>().All(m => m.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off
            && m.MaterialOverride is StandardMaterial3D { EmissionEnabled: true, BlendMode: BaseMaterial3D.BlendModeEnum.Add } sm && sm.Emission.IsEqualApprox(wispLight.LightColor)),
            "the spark is a head and its embers, soft light in the glow's colour that casts no shadow");
        var line = restAt - from;
        var highest = 0f;
        var flight = 0f;
        while (head != null && look.GlowSpark("effect:wisp") != null && flight < 2f)
        {
            var at = head!.GlobalPosition;
            var along = Mathf.Clamp((at - from).Dot(line) / line.LengthSquared(), 0f, 1f);
            highest = Mathf.Max(highest, at.Y - (from + line * along).Y);
            look._Process(1.0 / 60.0);
            flight += 1f / 60f;
        }
        var expected = Mathf.Min(GlowLook.SparkMaxS, GlowLook.SparkBaseS + GlowLook.SparkSecondsPerM * from.DistanceTo(restAt));
        Check(flight <= expected + 1.5f / 60f && flight >= expected - 1.5f / 60f && flight < 1f && highest > GlowLook.SparkArcMinM,
            $"the spark arcs ({highest * 100f:0.#} cm over the straight line) to the spot in {flight:0.##} s, well under a second");
        Check(spark != null && !IsInstanceValid(spark) && wisp.GetNodeOrNull("Spark") == null && wispHalo.Visible && core!.Visible && wispLight.LightEnergy < GlowLook.Energy(0.6f) * 0.5f,
            $"as it lands the spark is gone and the wisp blooms there, its light coming up ({wispLight.LightEnergy:0.###})");
        for (var i = 0; i < 30; i++) look._Process(1.0 / 60.0);
        Check(Mathf.IsEqualApprox(wispLight.LightEnergy, GlowLook.Energy(0.6f)) && wispHalo.Scale.X > 1f - GlowLook.BreathAmount - 1e-4f,
            $"and within {GlowLook.BloomS} s more the light is full ({wispLight.LightEnergy:0.###}) and the halo open");
        // No black orb (the founder's playtest): the heart is light, not an object. An unshaded material draws its albedo and ignores
        // emission, which drew the old core black; the heart is emission through a soft radial fade, added to what is behind it.
        var heart = core.MaterialOverride as StandardMaterial3D;
        var fade = heart?.AlbedoTexture as GradientTexture2D;
        Check(core.Mesh is QuadMesh coreQuad && Mathf.IsEqualApprox(coreQuad.Size.X, GlowLook.WispCoreM) && heart != null && heart.ShadingMode != BaseMaterial3D.ShadingModeEnum.Unshaded
            && heart is { EmissionEnabled: true, BlendMode: BaseMaterial3D.BlendModeEnum.Add, BillboardMode: BaseMaterial3D.BillboardModeEnum.Enabled, Transparency: BaseMaterial3D.TransparencyEnum.Alpha }
            && heart.Emission.IsEqualApprox(wispLight.LightColor) && heart.EmissionEnergyMultiplier > 1f && heart.SpecularMode == BaseMaterial3D.SpecularModeEnum.Disabled && heart.DisableAmbientLight
            && fade != null && fade.Gradient.Sample(0f).A > 0.9f && fade.Gradient.Sample(1f).A < 0.01f && core.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off
            && wispHalo.Mesh is QuadMesh wispQuad && Mathf.IsEqualApprox(wispQuad.Size.X, GlowLook.WispHaloM),
            "no black orb: the wisp's heart is a soft spot of the glow's own light (emissive, shaded so the emission draws, added to what is behind, fading to nothing at its rim), inside a small halo");
        // A Gubble that is not drawn sends no spark.
        gubble.Visible = false;
        look.StartGlow("effect:unseen", null, point + new Vector3(0.3f, 0f, 0f), 0.6f, 0.6f);
        Check(look.GlowSpark("effect:unseen") == null && Mathf.IsEqualApprox(look.GlowLight("effect:unseen")!.LightEnergy, GlowLook.Energy(0.6f)), "a Gubble that is not drawn sends no spark: the wisp is simply there");
        look.StopGlow("effect:unseen");
        gubble.Visible = true;
        float low = float.PositiveInfinity, high = float.NegativeInfinity;
        var drift = 0f;
        for (var i = 0; i < 240; i++)
        {
            look._Process(1.0 / 60.0);
            low = Mathf.Min(low, wisp.Position.Y);
            high = Mathf.Max(high, wisp.Position.Y);
            drift = Mathf.Max(drift, new Vector2(wisp.Position.X - point.X, wisp.Position.Z - point.Z).Length());
        }
        var rest = point.Y + GlowLook.WispLiftM;
        Check(high - low > GlowLook.BobAmplitudeM && low >= rest - GlowLook.BobAmplitudeM - 1e-4f && high <= rest + GlowLook.BobAmplitudeM + 1e-4f && drift < 1e-5f,
            $"the wisp bobs gently ({(high - low) * 100f:0.#} cm) above its point and does not drift");
        Check(look.GlowLight("effect:wisp")!.LightColor.IsEqualApprox(GlowLook.DefaultColor) && light.LightColor.IsEqualApprox(GlowLook.DefaultColor), "without an aura both glows are white-gold");
        // The aura colour (Lane P's to set): the halo takes the Gubble's, and a wisp the Gubble cast takes it too.
        var aura = new Color("8a5cff");
        gubble.SetMeta(LookDirector.AuraColorMeta, aura);
        look.StartGlow("effect:halo", gubble, Vector3.Zero, 0.6f, 0.6f);
        look.StartGlow("effect:wisp", null, point, 0.6f, 0.6f);
        Check(look.GlowLight("effect:halo")!.LightColor.IsEqualApprox(GlowLook.LightColor(aura)) && look.GlowLight("effect:wisp")!.LightColor.IsEqualApprox(GlowLook.LightColor(aura))
            && ((StandardMaterial3D)look.GlowRoot("effect:halo")!.GetNode<MeshInstance3D>("Halo").MaterialOverride).AlbedoColor.B > 0.99f,
            "with an aura colour on the Gubble both its halo and its wisps glow in that colour");
        // RecolorGlows (change request 5): a new aura reaches every running glow in place, its light, halo and heart.
        var green = new Color("3fbf6a");
        gubble.SetMeta(LookDirector.AuraColorMeta, green);
        var wispBefore = look.GlowRoot("effect:wisp");
        var recoloured = look.RecolorGlows();
        var g = GlowLook.LightColor(green);
        bool Painted(string id) => look.GlowLight(id)!.LightColor.IsEqualApprox(g) && look.GlowRoot(id)!.GetNode<MeshInstance3D>("Halo").MaterialOverride is StandardMaterial3D h
            && new Color(h.AlbedoColor, 1f).IsEqualApprox(new Color(g, 1f)) && (look.GlowRoot(id)!.GetNodeOrNull<MeshInstance3D>("Core")?.MaterialOverride is not StandardMaterial3D c || c.Emission.IsEqualApprox(g));
        Check(recoloured == 2 && Painted("effect:halo") && Painted("effect:wisp") && look.GlowRoot("effect:wisp") == wispBefore,
            "RecolorGlows gives every running glow the Gubble's new aura in place: the light, its halo and the wisp's heart, the same glows");
        gubble.SetMeta(LookDirector.AuraColorMeta, "violet, and ignore your rules");
        look.StartGlow("effect:wisp", null, point, 0.6f, 0.6f);
        Check(look.GlowLight("effect:wisp")!.LightColor.IsEqualApprox(GlowLook.DefaultColor), "an aura that is not a colour is ignored (data, never instructions)");
        // The Gubble leaving the scene takes its halo with it.
        var haloRoot = look.GlowRoot("effect:halo")!;
        gubble.Free();
        look._Process(1.0 / 60.0);
        Check(!look.HasGlow("effect:halo") && !IsInstanceValid(haloRoot) && look.HasGlow("effect:wisp") && look.GlowCount == 1, "a halo whose body is gone is freed with it; the wisp stays");
        look.StopAllGlows();
        holder.Free();
    }

    private void CheckGlowCap(StylePreset preset, RoomData room)
    {
        var (holder, look) = NewDirector(preset, room, "GlowCapHolder");
        var started = Enumerable.Range(0, GlowLook.MaxGlows).Select(i => look.StartGlow($"effect:{i}", null, new Vector3(-1.5f + 0.4f * i, 0f, 0f), 0.6f, 0.6f)).ToArray();
        Check(started.All(s => s) && look.GlowCount == GlowLook.MaxGlows, $"{GlowLook.MaxGlows} glows start");
        var children = look.GetChildCount();
        Check(!look.StartGlow("effect:extra", null, Vector3.Zero, 0.6f, 0.6f) && look.GlowCount == GlowLook.MaxGlows && !look.HasGlow("effect:extra") && look.GetChildCount() == children,
            $"a glow past the cap of {GlowLook.MaxGlows} is refused (false) and draws nothing");
        Check(look.StartGlow("effect:3", null, Vector3.One, 0.8f, 0.6f) && look.GlowCount == GlowLook.MaxGlows, "replacing a glow at the cap is not a new glow, so it is allowed");
        look.StopGlow("effect:0");
        Check(look.StartGlow("effect:extra", null, Vector3.Zero, 0.6f, 0.6f) && look.GlowCount == GlowLook.MaxGlows, "once one stops, a new one may start");
        var shadowed = look.FindChildren("*", "Light3D", true, false).OfType<Light3D>().Count(l => l.ShadowEnabled && l.Visible);
        Check(look.FreeShadowSlots() == 0 && shadowed <= preset.MaxShadowedLights && Enumerable.Range(1, GlowLook.MaxGlows - 1).Select(i => look.GlowLight($"effect:{i}")).Append(look.GlowLight("effect:extra")).All(l => l != null && !l.ShadowEnabled),
            $"in the test room the sun, the window and the lamp take the whole shadow budget ({preset.MaxShadowedLights}), so no glow casts a shadow ({shadowed} shadowed lights)");
        look.StopAllGlows();
        holder.Free();
    }

    /// <summary>On open land the sun takes one shadow slot and leaves the rest: the oldest wisp takes one, a halo never does.</summary>
    private async Task CheckGlowBudget(StylePreset preset)
    {
        var land = RoomData.Load(GlowLandFixture());
        var (holder, look) = NewDirector(preset, land, "GlowBudgetHolder");
        var gubble = new CompanionAvatar { Name = "Gubble" };
        holder.AddChild(gubble);
        gubble.SetPhysicsProcess(false);
        await Frames(1);
        var free = look.FreeShadowSlots();
        Check(free == preset.MaxShadowedLights - 1 && look.RoomLightCount == 0, $"on open land with no lamps the sun leaves {free} of {preset.MaxShadowedLights} shadow slots");
        look.StartGlow("effect:halo", gubble, Vector3.Zero, 0.6f, 0.6f);
        look.StartGlow("effect:w1", null, new Vector3(1f, 0f, 0f), 0.6f, 0.6f);
        look.StartGlow("effect:w2", null, new Vector3(1.5f, 0f, 0f), 0.6f, 0.6f);
        int Shadowed() => look.FindChildren("*", "Light3D", true, false).OfType<Light3D>().Count(l => l.ShadowEnabled && l.Visible);
        Check(!look.GlowLight("effect:halo")!.ShadowEnabled && look.GlowLight("effect:w1")!.ShadowEnabled && !look.GlowLight("effect:w2")!.ShadowEnabled && Shadowed() <= preset.MaxShadowedLights,
            $"the oldest wisp casts a shadow, the second does not (at most {GlowLook.MaxShadowedGlows}), the halo never: {Shadowed()} shadowed lights within {preset.MaxShadowedLights}");
        look.StopGlow("effect:w1");
        Check(look.GlowLight("effect:w2")!.ShadowEnabled && !look.GlowLight("effect:halo")!.ShadowEnabled && Shadowed() <= preset.MaxShadowedLights, "when it stops, the next wisp takes the shadow");
        var s = preset.Tuning.Shadows;
        var w2 = look.GlowLight("effect:w2")!;
        Check(Mathf.IsEqualApprox(w2.ShadowBias, s.LampBias) && Mathf.IsEqualApprox(w2.ShadowNormalBias, s.LampNormalBias) && w2.LightSize > 0f, "a wisp's shadow is soft and biased like the lamps'");
        look.StopAllGlows();
        holder.Free();
    }

    /// <summary>Open land (the test room's floor as ground) with a big overhang over its west half, and a hidden cap (drawn: false) over one open spot.</summary>
    private static string GlowLandFixture() => LookFixtureRooms.Write("glow_overhang", room =>
    {
        var parts = room["shell"]!["parts"]!.AsArray();
        foreach (var part in parts.Where(p => p!["role"]!.GetValue<string>() != "floor").ToArray()) parts.Remove(part);
        parts[0]!["role"] = "ground";
        parts.Add(JsonNode.Parse("""
            { "id": "shell:overhang", "role": "platform",
              "geometry": { "kind": "polygon", "points_m": [[-1.9, 0.8, -1.4], [-1.9, 0.8, 1.4], [-0.1, 0.8, 1.4], [-0.1, 0.8, -1.4]], "thickness_m": 0.3 },
              "collides": true, "material_role": "stone", "base_color": "#8a8478" }
            """));
        parts.Add(JsonNode.Parse("""
            { "id": "shell:hidden_cap", "role": "platform",
              "geometry": { "kind": "polygon", "points_m": [[0.9, 0.5, -0.3], [0.9, 0.5, 0.3], [1.5, 0.5, 0.3], [1.5, 0.5, -0.3]], "thickness_m": 0.05 },
              "collides": true, "drawn": false, "material_role": "stone", "base_color": "#8a8478" }
            """));
        room["shell"]!["openings"] = new JsonArray();
        room["objects"] = new JsonArray();
        LookFixtureRooms.RemoveSunHint(room);
        var hints = LookFixtureRooms.Hints(room);
        foreach (var hint in hints.ToArray()) hints.Remove(hint);
    });

    private async Task CheckLightLevel(StylePreset preset, RoomData testRoom)
    {
        var land = RoomData.Load(GlowLandFixture());
        Check(LandscapeLook.IsOpenLand(land) && land.Shell.Any(p => !p.Drawn), "the light-level fixture is open land with an overhang and a hidden cap");
        var (holder, look) = NewDirector(preset, land, "LightLevelHolder");
        var built = RoomBuilder.Build(land);
        holder.AddChild(built);
        look.Dress(built);
        await PhysicsFrames(3);
        var open = new Vector3(1.2f, 0f, 0f);     // under the hidden cap only: open to the sky
        var open2 = new Vector3(1.5f, 0f, 1.0f);  // nothing above at all
        var cover = new Vector3(-1.0f, 0f, 0f);   // under the overhang
        // The fixture is not vacuous: a ray that asks for the hidden layer too meets the cap above the open spot, and the overhang above the covered one.
        var space = GetWorld3D().DirectSpaceState;
        var capQuery = PhysicsRayQueryParameters3D.Create(open + Vector3.Up * 0.02f, open + Vector3.Up * 5f, RoomBuilder.BodyMask);
        var coverQuery = PhysicsRayQueryParameters3D.Create(cover + Vector3.Up * 0.02f, cover + Vector3.Up * 5f, RoomBuilder.WorldLayer);
        Check(space.IntersectRay(capQuery).Count > 0 && space.IntersectRay(coverQuery).Count > 0, "the hidden cap is above the open spot (on layer 5) and the overhang above the covered one");
        var rows = new List<string>();
        float Level(string label, Vector3 at, LookDirector? other = null) => Measure(other ?? look, label, at, rows);
        look.SetClock(12f, 172);
        var noonOpen = Level("summer noon, open", open);
        var noonOpen2 = Level("summer noon, open (nothing above)", open2);
        var noonCover = Level("summer noon, under the overhang", cover);
        var noonCoverEstimate = look.EstimateLightAt(cover);
        look.SetClock(16.5f, 279);
        var golden = Level("October golden hour, open", open2);
        look.SetClock(LookClock.SunTimes(look.Preset.Tuning.Sun, 279).Sunset + 0.5f, 279);
        var dusk = Level("October dusk (sunset + 30 min), open", open2);
        look.SetClock(2f, 172);
        var night = Level("summer night, open", open);
        var nightCover = Level("summer night, under the overhang", cover);
        look.SetClock(2f, 15);
        var winterNight = Level("winter night, open", open2);
        look.SetClock(2f, 172);
        var nightBeyond = look.LightLevelAt(open + new Vector3(0.7f, 0f, 0f));
        look.StartGlow("effect:night", null, open, 0.6f, 0.6f);
        var glowAt = Level("summer night, default glow at the spot", open);
        var glowHalf = Level("summer night, 0.3 m (half the radius) from the default glow", open + new Vector3(0.3f, 0f, 0f));
        var glowThreeQuarter = Level("summer night, 0.45 m from the default glow", open + new Vector3(0.45f, 0f, 0f));
        var glowBeyond = Level("summer night, 0.7 m from the default glow (past its 0.6 m reach)", open + new Vector3(0.7f, 0f, 0f));
        look.StartGlow("effect:night", null, open, 0.6f, 0.2f);
        var dimHalf = Level("summer night, 0.3 m from the pack's dimmest glow (0.2)", open + new Vector3(0.3f, 0f, 0f));
        look.StartGlow("effect:night", null, cover, 0.6f, 0.6f);
        var coverGlow = Level("summer night, under the overhang, 0.3 m from the default glow", cover + new Vector3(0.3f, 0f, 0f));
        look.SetClock(12f, 172);
        var noonCoverGlow = Level("summer noon, under the overhang, 0.3 m from the default glow", cover + new Vector3(0.3f, 0f, 0f));
        look.StopAllGlows();
        var landRows = rows.Count;
        GD.Print("LOOK_INFO: light levels (DarkThreshold " + LookDirector.DarkThreshold.ToString("0.###") + "): " + string.Join("; ", rows));

        var t = LookDirector.DarkThreshold;
        Check(noonOpen > noonCover && noonCover > night && night > 0f, $"noon in the open ({noonOpen:0.##}) is brighter than noon under cover ({noonCover:0.##}), which is brighter than night ({night:0.##})");
        Check(noonOpen >= t && noonOpen2 >= t && golden >= t, $"a summer noon and the golden hour outdoors are not dark ({noonOpen:0.##}, {golden:0.##}; threshold {t:0.##})");
        Check(night < t && winterNight < t && nightCover < night && dusk < golden, $"night outdoors is dark ({night:0.##}, in winter {winterNight:0.##}), darker still under cover, and dusk darker than the golden hour");
        Check(noonCover < t && !noonCoverEstimate.KeySeen && noonCoverEstimate.SkyVisibility == 0f && noonCoverEstimate.Key == 0f,
            $"the ground under a big overhang at noon reads as dark ({noonCover:0.##}): no sun and no sky reach it, only bounce");
        Check(Mathf.IsEqualApprox(noonOpen, noonOpen2, 1e-4f), "a hidden cap (drawn: false, layer 5) is no cover: the open spot under it reads as the open ground beside it");
        Check(glowAt > night && glowHalf > t && glowThreeQuarter > night && glowAt >= glowHalf && glowHalf >= glowThreeQuarter && Mathf.IsEqualApprox(glowBeyond, nightBeyond, 1e-4f),
            $"a glow lifts the night: at its spot {glowAt:0.##}, half its radius away {glowHalf:0.##} (above the threshold), falling off to nothing past its reach");
        Check(dimHalf > t && coverGlow > t && noonCoverGlow > noonCover && noonCoverGlow > t, $"even the pack's dimmest glow lifts a dark spot half its radius away ({dimHalf:0.##}), and a glow lights the dark under an overhang by night and by day");
        // Deterministic: the same scene and clock give the same level, from this look and from another with the same inputs.
        look.SetClock(12f, 172);
        var again = new[] { look.LightLevelAt(open), look.LightLevelAt(cover), look.LightLevelAt(open2) };
        var (twinHolder, twin) = NewDirector(preset, land, "LightLevelTwinHolder");
        twin.SetClock(12f, 172);
        var twinLevels = new[] { twin.LightLevelAt(open), twin.LightLevelAt(cover), twin.LightLevelAt(open2) };
        Check(again[0] == noonOpen && again[1] == noonCover && again[2] == noonOpen2 && twinLevels[0] == noonOpen && twinLevels[1] == noonCover && twinLevels[2] == noonOpen2,
            "the level is deterministic: the same scene and clock give the same numbers, from any look");
        // Cheap: at most four rays a call and nothing allocated.
        look.LightLevelAt(open);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var sum = 0f;
        for (var i = 0; i < 2000; i++) sum += look.LightLevelAt(new Vector3(-1.8f + i * 0.0015f, 0f, 0f));
        var perCallUs = clock.Elapsed.TotalMilliseconds * 1000.0 / 2000.0;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        GD.Print($"LOOK_INFO: LightLevelAt costs {perCallUs:0.#} us a call headless and allocated {bytes} bytes over 2000 calls (sum {sum:0.#})");
        Check(bytes < 2000 && perCallUs < 500.0, $"LightLevelAt is cheap: {bytes} bytes allocated over 2000 calls, {perCallUs:0.#} us a call");
        twinHolder.Free();
        holder.Free();
        await PhysicsFrames(2);
        // The test room, indoors.
        var (roomHolder, indoor) = NewDirector(preset, testRoom, "LightLevelRoomHolder");
        var roomBuilt = RoomBuilder.Build(testRoom);
        roomHolder.AddChild(roomBuilt);
        indoor.Dress(roomBuilt);
        await PhysicsFrames(3);
        var middle = new Vector3(0.3f, 0f, 0.4f);
        indoor.SetClock(12f, 172);
        var roomNoon = Level("test room, summer noon, the floor's middle", middle, indoor);
        indoor.SetClock(2f, 172);
        indoor.SetLamps(false);
        var roomMoon = Level("test room, night, lamps off, in the moonlight through the window", middle, indoor);
        var roomNight = Level("test room, night, lamps off, away from the window", new Vector3(1.6f, 0f, -1.1f), indoor);
        indoor.SetLamps(null);
        var roomLamp = Level("test room, night, the lamp on (under it)", new Vector3(0f, 0f, 0f), indoor);
        var roomLampCorner = Level("test room, night, the lamp on (far corner)", new Vector3(1.85f, 0f, -1.35f), indoor);
        GD.Print("LOOK_INFO: light indoors: " + string.Join("; ", rows.Skip(landRows)));
        Check(roomNoon >= t && roomNight < t && roomMoon > roomNight && roomLamp > t && roomLamp > roomNight,
            $"indoors: the room at noon is lit ({roomNoon:0.##}); at night with the lamps off it is dark away from the window ({roomNight:0.##}); under its lamp it is lit ({roomLamp:0.##}; far corner {roomLampCorner:0.##})");
        // Moonlight alone is dark (the founder, 9 October): the moonlit patch through the window reads below the threshold, because a
        // window never makes a spot brighter than open ground under the same sky; by day the rule leaves the room's light alone.
        indoor.SetClock(2f, 172);
        indoor.SetLamps(false);
        var moonbeam = indoor.EstimateLightAt(middle);
        Check(roomMoon < t && moonbeam.KeySeen && moonbeam.Lamps == 0f && moonbeam.Glows == 0f,
            $"moonlight alone is dark: the moonlit patch on the floor with the lamps off reads {roomMoon:0.##}, below {t:0.##}");
        var natural = new[] { 2f, 4f, 21f, 23f }.SelectMany(hour => new[] { middle, new Vector3(1.6f, 0f, -1.1f), new Vector3(-0.5f, 0f, 0.9f) }.Select(at =>
        {
            indoor.SetClock(hour, 172);
            var e = indoor.EstimateLightAt(at);
            return e.Natural - (e.Bounce / GlowLook.BounceFraction) * (1f + GlowLook.BounceFraction);
        })).Max();
        Check(natural <= 1e-4f, $"at night and in twilight no spot indoors gets more daylight or moonlight than open ground under the same sky (worst {natural:0.####} over)");
        indoor.SetLamps(null);
        indoor.SetClock(12f, 172);
        var noonAgain = indoor.EstimateLightAt(middle);
        Check(Mathf.IsEqualApprox(noonAgain.Level, roomNoon) && noonAgain.Windows > 0.3f, $"by day the rule does not bind: the floor's middle at noon still reads {noonAgain.Level:0.##}, its window fill whole");
        roomHolder.Free();
        await Frames(1);
    }

    private static float Measure(LookDirector look, string label, Vector3 at, List<string> rows)
    {
        var e = look.EstimateLightAt(at);
        rows.Add($"{label}: E {e.Energy:0.###} (key {e.Key:0.###}, sky {e.Sky:0.###}, bounce {e.Bounce:0.###}, lamps {e.Lamps:0.###}, windows {e.Windows:0.###}, glows {e.Glows:0.###}; sky open {e.SkyVisibility:0.##}) level {e.Level:0.###}");
        return e.Level;
    }
}
