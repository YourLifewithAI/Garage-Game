using Godot;
using System;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Look;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Look;

/// <summary>
/// Run 2, Bubbles and Fireworks, Lane L: the look's API mirrors StartGlow (StartFireworks, StopEffect), one cap of 8 effects across
/// the kinds, and fireworks climb from the Gubble, burst above the spot and flash a brief real light within the budgets that
/// LightLevelAt counts while it is lit. How they look needs the GPU (docs/look/EFFECTS.md); these checks are what holds headless.
/// The Bubble is no effect since the founder's redesign (decisions 55 to 60): LookPresetTest.Bubble.cs checks its drawing, and that
/// the stream of small bubbles this file once checked is gone.
/// </summary>
public partial class LookPresetTest
{
    private const float Tick = 1f / 60f;

    private async Task CheckEffects(StylePreset preset, RoomData room)
    {
        CheckEffectApi(preset, room);
        await CheckBubbleLook(preset, room);
        await CheckFireworks(preset);
    }

    private void CheckEffectApi(StylePreset preset, RoomData room)
    {
        var (holder, look) = NewDirector(preset, room, "EffectApiHolder");
        look.SetProcess(false);
        var at = new Vector3(0.4f, 0f, -0.3f);
        Check(EffectLook.MaxEffects == 8 && look.EffectCount == 0 && look.KindOf("effect:b") == LookDirector.EffectKind.None, "the look draws at most 8 effects of every kind together, and none to start with");
        Check(look.StartFireworks("effect:b", null, at, 0.4f, 0.6f) && look.StartFireworks("effect:f", null, at, 0.6f, 0.6f)
            && look.KindOf("effect:b") == LookDirector.EffectKind.Fireworks && look.KindOf("effect:f") == LookDirector.EffectKind.Fireworks
            && look.EffectRoot("effect:b") is { TopLevel: true } && look.EffectRoot("effect:f") != null && look.EffectCount == 2 && look.GlowCount == 0,
            "fireworks start like a glow: an id, a body or a spot, a radius and an intensity");
        var glows = Enumerable.Range(0, 6).Select(i => look.StartGlow($"effect:g{i}", null, at + new Vector3(0.2f * i, 0f, 0f), 0.6f, 0.6f)).ToArray();
        var children = look.GetChildCount();
        Check(glows.All(g => g) && look.EffectCount == 8 && !look.StartFireworks("effect:x", null, at, 0.4f, 0.6f) && !look.StartFireworks("effect:y", null, at, 0.4f, 0.6f)
            && !look.StartGlow("effect:z", null, at, 0.6f, 0.6f) && look.EffectCount == 8 && look.GetChildCount() == children,
            "one cap across the kinds: with 6 glows and 2 effects, a ninth of any kind is refused and draws nothing");
        var replacedRoot = look.EffectRoot("effect:b")!;
        Check(look.StartGlow("effect:b", null, at, 0.6f, 0.6f) && look.KindOf("effect:b") == LookDirector.EffectKind.Glow && !IsInstanceValid(replacedRoot) && look.EffectCount == 8,
            "an id already in use is replaced whatever its kind, so it is not a new effect");
        var fireworksRoot = look.EffectRoot("effect:f")!;
        look.StopEffect("effect:f");
        look.StopEffect("effect:g0");
        look.StopEffect("effect:never");
        Check(!look.HasEffect("effect:f") && !IsInstanceValid(fireworksRoot) && !look.HasEffect("effect:g0") && look.EffectCount == 6, "StopEffect ends an effect of any kind and frees what it drew; an unknown id is a no-op");
        var gone = new Node3D();
        holder.AddChild(gone);
        gone.Free();
        Check(!look.StartFireworks("", null, at, 0.4f, 0.6f) && !look.StartFireworks("effect:n", null, new Vector3(float.NaN, 0f, 0f), 0.4f, 0.6f)
            && !look.StartFireworks("effect:i", null, at, 0.4f, float.PositiveInfinity) && !look.StartFireworks("effect:gone", gone, at, 0.4f, 0.6f),
            "an empty id, a number that is not finite or a body that is gone is refused");
        look.StopAllEffects();
        Check(look.EffectCount == 0 && look.FindChildren("Fireworks_*", "", true, false).Count == 0 && look.FindChildren("Glow_*", "", true, false).Count == 0, "StopAllEffects ends every glow and burst");
        holder.Free();
    }

    /// <summary>The light-level fixture (open land and a big overhang) with the Gubble and the player standing on it.</summary>
    private async Task<(Node3D Holder, LookDirector Look, CompanionAvatar Gubble, SmallPlayerController Player)> EffectWorld(StylePreset preset, string name)
    {
        var land = RoomData.Load(GlowLandFixture());
        var (holder, look) = NewDirector(preset, land, name);
        look.SetProcess(false);
        var built = RoomBuilder.Build(land);
        holder.AddChild(built);
        look.Dress(built);
        var gubble = new CompanionAvatar { Name = "Gubble" };
        holder.AddChild(gubble);
        gubble.SetPhysicsProcess(false);
        var player = new SmallPlayerController { Name = "Player", ReadKeyboard = false };
        holder.AddChild(player);
        player.SetPhysicsProcess(false);
        player.GlobalPosition = new Vector3(1.6f, 0f, 1.2f);
        look.FocusTarget = player;
        await PhysicsFrames(3);
        return (holder, look, gubble, player);
    }

    private static void Run(LookDirector look, float seconds)
    {
        for (var t = 0f; t < seconds; t += Tick) look._Process(Tick);
    }

    private async Task CheckFireworks(StylePreset preset)
    {
        var (holder, look, gubble, player) = await EffectWorld(preset, "FireworksHolder");
        gubble.GlobalPosition = new Vector3(0.6f, 0f, 1.0f);
        var middle = gubble.GlobalPosition + Vector3.Up * gubble.BodyHeightM * GlowLook.FollowHeightFraction;
        var aura = new Color("e8509a");
        gubble.SetMeta(LookDirector.AuraColorMeta, aura);
        look.SetClock(2f, 172);
        var spot = new Vector3(1.2f, 0f, -0.4f);
        var dark = look.EstimateLightAt(spot);
        var lightsBefore = look.FindChildren("*", "Light3D", true, false).Count;
        Check(look.StartFireworks("effect:fw", null, spot, 0.6f, 0.6f), "the Gubble sends fireworks at a spot");
        look._Process(Tick);
        var rocket = look.EffectParticles("effect:fw");
        Check(rocket.Count >= 1 && rocket.Min(p => p.Position.DistanceTo(middle)) < 0.1f && look.FlashLight("effect:fw") == null, "a rocket leaves the Gubble's middle, unlit");
        var burst = spot + Vector3.Up * EffectLook.BurstHeightM;
        var climb = 0f;
        var highest = float.NegativeInfinity;
        while (look.Bursts("effect:fw") == 0 && climb < 2f)
        {
            look._Process(Tick);
            climb += Tick;
            highest = Mathf.Max(highest, look.EffectParticles("effect:fw").Max(p => p.Position.Y));
        }
        var flash = look.FlashLight("effect:fw");
        var sparks = look.EffectParticles("effect:fw").Where(p => p.Position.DistanceTo(burst) < 0.05f).ToList();
        var expected = EffectLook.SparksBase + (int)(EffectLook.SparksPerIntensity * 0.6f);
        Check(look.Bursts("effect:fw") == 1 && climb < 1.1f && sparks.Count >= expected && highest <= burst.Y + 0.05f,
            $"it climbs to {EffectLook.BurstHeightM} m above the spot in {climb:0.##} s and bursts there into {sparks.Count} sparks");
        Check(flash != null && !flash.ShadowEnabled && flash.OmniRange <= EffectLook.FlashMaxRangeM && flash.OmniRange >= EffectLook.BurstHeightM && flash.LightEnergy > 0f
            && flash.LightColor.IsEqualApprox(GlowLook.LightColor(aura)) && look.FindChildren("*", "Light3D", true, false).Count == lightsBefore + 1,
            $"the burst flashes a real light in the aura's colour that reaches the land below ({flash?.OmniRange:0.##} m), casts no shadow and is the effect's one light");
        Run(look, 0.08f);
        var lit = look.EstimateLightAt(spot);
        Check(lit.Glows > dark.Glows + 0.02f && lit.Level > dark.Level, $"while it is lit LightLevelAt counts it: {dark.Level:0.##} to {lit.Level:0.##} at the spot under it");
        Run(look, 0.5f);
        var spread = look.EffectParticles("effect:fw").Where(p => p.Position.Y > spot.Y + 0.2f).Select(p => p.Position.DistanceTo(burst)).DefaultIfEmpty(0f).Max();
        Check(spread > 0.6f * 0.6f && spread < 0.6f * 1.6f, $"the sparks fly out to about the radius ({spread:0.##} m of 0.6)");
        Run(look, EffectLook.FlashS);
        Check(look.FlashLight("effect:fw") == null && Mathf.IsEqualApprox(look.EstimateLightAt(spot).Level, dark.Level, 1e-4f) && look.FindChildren("*", "Light3D", true, false).Count == lightsBefore,
            "a moment later the flash is gone, and so is its light in the level");
        Run(look, EffectLook.VolleyEveryS);
        Check(look.Bursts("effect:fw") == 2, "while the effect lasts, another rocket goes up");
        look.StopEffect("effect:fw");
        // On the Gubble: a small burst over its head.
        look.StartFireworks("effect:self", gubble, Vector3.Zero, 0.6f, 0.6f);
        Run(look, 1.2f);
        var head = gubble.GlobalPosition.Y + gubble.BodyHeightM;
        var over = look.EffectParticles("effect:self");
        Check(look.Bursts("effect:self") == 1 && over.Count > 0 && over.Average(p => p.Position.Y) > head && over.All(p => new Vector2(p.Position.X - gubble.GlobalPosition.X, p.Position.Z - gubble.GlobalPosition.Z).Length() < 0.6f * EffectLook.SelfBurstScale * 1.7f),
            "on the Gubble, a small burst over its head");
        look.StopAllEffects();
        // No Gubble drawn: the rocket climbs from the spot itself.
        gubble.Visible = false;
        look.StartFireworks("effect:alone", null, spot, 0.6f, 0.6f);
        look._Process(Tick);
        Check(look.EffectParticles("effect:alone").Any(p => new Vector2(p.Position.X - spot.X, p.Position.Z - spot.Z).Length() < 0.02f && p.Position.Y < spot.Y + 0.1f),
            "with no Gubble drawn the rocket climbs from the spot itself");
        gubble.Visible = true;
        look.StopAllEffects();
        holder.Free();
        await PhysicsFrames(1);
    }
}
