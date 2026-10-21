using Godot;
using System;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Look;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Look;

/// <summary>
/// The Bubble's look (MECHANICS.md decisions 55 to 60; LookDirector.Bubble.cs): the stream of small bubbles is gone; one bubble is
/// drawn from what the kernel says (ShowBubble, MoveBubble, HideBubble) and what the HUD adds (SetBubbleState): the Gubble's own soap
/// film in its aura, a bubble waiting at a spot breathing and bobbing, one holding the player calmer, a boat a dome over a disc, a
/// ghost fainter, a lift stretching and rushing, a string from the Gubble and a mark at the spot when it is led, and the selected
/// bubble wearing a focus highlight of its own beside the hand's. How it looks needs the GPU; these are what holds headless.
/// </summary>
public partial class LookPresetTest
{
    private async Task CheckBubbleLook(StylePreset preset, RoomData room)
    {
        // The stream is gone: the cosmetic effect, its kind and its numbers no longer exist.
        Check(typeof(LookDirector).GetMethod("StartBubbles") == null && !Enum.GetNames<LookDirector.EffectKind>().Contains("Bubbles") &&
              typeof(EffectLook).GetField("BubblesPerSecond") == null && typeof(LookDirector).GetMethod("TouchPops") == null,
            "the stream of small bubbles is gone: no StartBubbles, no bubbles kind of effect, no blown-per-second number, no touch pops");

        var (holder, look, gubble, player) = await EffectWorld(preset, "BubbleLookHolder");
        gubble.GlobalPosition = new Vector3(0.4f, 0f, 0.9f);
        var aura = new Color("e8509a");
        gubble.SetMeta(LookDirector.AuraColorMeta, aura);
        look.SetClock(12f, 172);

        // The kernel's call: one bubble at a spot, the Gubble's soap film in its aura. It is no effect of the room's cap.
        var spot = new Vector3(0.8f, 0.085f, -0.5f);
        look.ShowBubble("effect:one", spot, 0.08f, boat: false, ghost: false);
        var root = look.BubbleRoot("effect:one");
        var shell = look.BubbleShell("effect:one");
        var film = look.BubbleFilm("effect:one");
        Check(root != null && root.Position.IsEqualApprox(spot) && look.BubbleDrawCount == 1 && look.EffectCount == 0 && shell?.Mesh is SphereMesh { IsHemisphere: false } sphere &&
              Mathf.IsEqualApprox(sphere.Radius, 0.08f) && Mathf.IsEqualApprox(sphere.Height, 0.16f) && root.GetParent() == look,
            "ShowBubble draws one sphere the bubble's size at its spot, under the look, and it is no effect of the room's cap");
        Check(film != null && shell!.MaterialOverride == film && film.Shader.ResourcePath.EndsWith("bubble.gdshader", StringComparison.Ordinal) &&
              film.GetShaderParameter("aura_color").AsColor().IsEqualApprox(aura) && shell.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off,
            "it wears the Gubble's own soap bubble (bubble.gdshader) in the Gubble's aura, and casts no shadow");
        // Everything it may wear is made with it and shown a few frames too small to see (the renderer's first draws), then hidden until its state wants it.
        var warmParts = new[] { look.BubbleString("effect:one"), look.BubbleMark("effect:one"), look.BubbleColumn("effect:one"), look.BubbleHalo("effect:one") };
        look._Process(Tick);
        Check(warmParts.All(part => part is { Visible: true }) && warmParts.Take(3).All(part => part!.GlobalBasis.Y.Length() < 1e-3f) && warmParts[3]!.Scale.X < 1e-3f,
            "its string, ring, column and focus ring are made with it and shown too small to see while the renderer draws each once");
        for (var i = 1; i < BubbleDrawTuning.WarmFrames; i++) look._Process(Tick);
        Check(warmParts.All(part => part is { Visible: false }) && Mathf.IsEqualApprox(warmParts[3]!.Scale.X, 1f) && look.BubbleDeck("effect:one") == null,
            $"after {BubbleDrawTuning.WarmFrames} frames they are hidden until the bubble is led or chosen");
        look.MoveBubble("effect:one", spot + new Vector3(0.1f, 0f, 0f));
        look.ShowBubble("effect:one", spot + new Vector3(0.2f, 0f, 0f), 0.08f, boat: false, ghost: false);
        Check(look.BubbleDrawCount == 1 && root!.Position.IsEqualApprox(spot + new Vector3(0.2f, 0f, 0f)), "MoveBubble moves it, and a bubble shown again under its id is moved, not drawn twice");
        look.MoveBubble("effect:never", spot);
        look.HideBubble("effect:never");
        look.ShowBubble("", spot, 0.08f, false, false);
        look.ShowBubble("effect:bad", new Vector3(float.NaN, 0f, 0f), 0.08f, false, false);
        look.ShowBubble("effect:bad", spot, 0f, false, false);
        Check(look.BubbleDrawCount == 1, "an unknown id is a no-op, and an empty id, a place that is not finite or a size of nothing draws nothing");

        // Waiting at a spot (the walk-in) it breathes and bobs: an invitation. Holding the player it is calm.
        look.SetBubbleState("effect:one", new BubbleDrawState(BubbleForm.WalkIn, false, false, Vector3.Zero, false, false));
        var rims = Enumerable.Range(0, 160).Select(_ => { look._Process(Tick); return (Rim: film!.GetShaderParameter("rim_alpha").AsSingle(), Bob: shell!.Position.Y); }).ToArray();
        Check(rims.Max(r => r.Rim) - rims.Min(r => r.Rim) > 0.05f && rims.Max(r => r.Bob) > BubbleDrawTuning.BobM * 0.5f && rims.Min(r => r.Bob) < -BubbleDrawTuning.BobM * 0.5f &&
              Mathf.Abs(rims.Average(r => r.Rim) - BubbleDrawTuning.WaitingRimAlpha) < 0.06f,
            $"a bubble waiting at a spot breathes (its rim {rims.Min(r => r.Rim):0.00} to {rims.Max(r => r.Rim):0.00}) and bobs ({rims.Min(r => r.Bob) * 1000:0.0} to {rims.Max(r => r.Bob) * 1000:0.0} mm)");
        look.SetBubbleState("effect:one", new BubbleDrawState(BubbleForm.Carry, true, false, Vector3.Zero, false, false));
        var steady = Enumerable.Range(0, 120).Select(_ => { look._Process(Tick); return (Rim: film!.GetShaderParameter("rim_alpha").AsSingle(), Bob: shell!.Position.Y); }).ToArray();
        Check(steady.All(r => Mathf.IsEqualApprox(r.Rim, BubbleDrawTuning.HoldingRimAlpha) && r.Bob == 0f) && BubbleDrawTuning.HoldingRimAlpha < BubbleDrawTuning.WaitingRimAlpha &&
              Mathf.IsEqualApprox(film!.GetShaderParameter("tint_alpha").AsSingle(), BubbleDrawTuning.HoldingTintAlpha) && look.BubbleStateOf("effect:one") is { Holding: true, Form: BubbleForm.Carry },
            "holding the player it is calmer: a steady, fainter rim, no breath and no bob");

        // Lifting: stretched up and its colours rush; led, a string runs from the Gubble to its skin, a ring marks the spot and a lift has a column.
        var ledTo = new Vector3(0.5f, 0.3f, -0.8f);
        look.SetBubbleState("effect:one", new BubbleDrawState(BubbleForm.Carry, true, true, ledTo, true, false));
        look._Process(Tick);
        var rope = look.BubbleString("effect:one");
        var mark = look.BubbleMark("effect:one");
        var column = look.BubbleColumn("effect:one");
        var centre = root!.GlobalPosition;
        var gubbleMiddle = gubble.GlobalPosition + Vector3.Up * gubble.BodyHeightM * GlowLook.FollowHeightFraction;
        var toward = (centre - gubbleMiddle).Normalized();
        var skin = centre - toward * (0.08f * 0.97f);
        Check(rope is { Visible: true, TopLevel: true } && mark is { Visible: true } && column is { Visible: true } && look.BubbleStateOf("effect:one") is { Led: true, LedLift: true },
            "led to a higher spot it has a string, a ring at the spot and a lift's column above it");
        var half = rope!.GlobalBasis.Y * 0.5f;
        var ends = new[] { rope.GlobalPosition - half, rope.GlobalPosition + half };
        var nearGubble = ends.OrderBy(e => e.DistanceTo(gubbleMiddle)).First();
        var nearBubble = ends.OrderBy(e => e.DistanceTo(skin)).First();
        Check(nearGubble.DistanceTo(gubbleMiddle) < 0.002f && nearBubble.DistanceTo(skin) < 0.002f && Mathf.Abs(rope.GlobalBasis.Y.Normalized().Dot(toward)) > 0.999f,
            $"the string runs from the Gubble's middle ({nearGubble.DistanceTo(gubbleMiddle) * 1000:0.0} mm off) to the bubble's skin on the side that faces it ({nearBubble.DistanceTo(skin) * 1000:0.0} mm off)");
        Check(mark!.GlobalPosition.DistanceTo(ledTo + Vector3.Up * BubbleDrawTuning.MarkLiftM) < 0.001f && Mathf.IsEqualApprox(column!.GlobalPosition.X, ledTo.X) && Mathf.IsEqualApprox(column.GlobalPosition.Z, ledTo.Z) && column.GlobalPosition.Y > ledTo.Y,
            "the ring lies on the spot it is led to and the lift's column stands over it");
        look.SetBubbleState("effect:one", new BubbleDrawState(BubbleForm.Carry, true, true, ledTo, true, true));
        for (var i = 0; i < 40; i++) look._Process(Tick);
        Check(Mathf.Abs(shell!.Scale.Y - (1f + BubbleDrawTuning.LiftStretch)) < 0.005f && look.BubbleLiftShown("effect:one") > 0.99f &&
              Mathf.IsEqualApprox(film!.GetShaderParameter("iridescence").AsSingle(), BubbleDrawTuning.LiftIridescence) && film.GetShaderParameter("rim_alpha").AsSingle() >= BubbleDrawTuning.LiftRimAlpha - 0.001f,
            $"rising on a lift it stretches up ({shell.Scale.Y:0.000}) and its film rushes and brightens");
        look.SetBubbleState("effect:one", new BubbleDrawState(BubbleForm.Carry, true, false, Vector3.Zero, false, false));
        for (var i = 0; i < 60; i++) look._Process(Tick);
        Check(rope is { Visible: false } && mark is { Visible: false } && column is { Visible: false } && Mathf.IsEqualApprox(shell.Scale.Y, 1f),
            "when the lead ends the string, the ring and the column go and it settles back");

        // A spot that is not a lift has no column.
        look.SetBubbleState("effect:one", new BubbleDrawState(BubbleForm.Carry, true, true, spot, false, false));
        look._Process(Tick);
        Check(look.BubbleMark("effect:one") is { Visible: true } && look.BubbleColumn("effect:one") is { Visible: false }, "a lead to a spot that is no lift draws the ring and no column");

        // The selected bubble wears the focus highlight, a second one beside the hand's, so both can be lit at once.
        var thing = new MeshInstance3D { Name = "ThingToCarry", Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.05f, 0.05f) } };
        holder.AddChild(thing);
        thing.GlobalPosition = new Vector3(-0.3f, 0.03f, 0.2f);
        look.SetFocusHighlight(thing);
        look.SelectBubble("effect:one");
        look._Process(Tick);
        var selectedRim = film!.GetShaderParameter("rim_alpha").AsSingle();
        var ring = look.BubbleHalo("effect:one");
        var ringSkin = ring?.MaterialOverride as StandardMaterial3D;
        var ringAcross = (ring?.Mesh as QuadMesh)?.Size.X ?? 0f;
        Check(look.SelectedBubble == "effect:one" && ring is { Visible: true } && ringAcross > 0.16f && ringAcross < 0.2f && ring.GetParent() == root &&
              ringSkin is { BillboardMode: BaseMaterial3D.BillboardModeEnum.Enabled, ShadingMode: BaseMaterial3D.ShadingModeEnum.Unshaded } && ringSkin.AlbedoTexture != null &&
              film.GetShaderParameter("tint_color").AsColor().IsEqualApprox(FocusLook.Default.RimColor) && selectedRim > BubbleDrawTuning.HoldingRimAlpha + 0.05f &&
              shell.MaterialOverlay == null && look.Highlight.Target == thing && thing.MaterialOverlay == look.Highlight.Overlay,
            $"the selected bubble wears what a thing that can be carried does: a ring facing the eye just outside it ({ringAcross * 100:0.0} cm across) in the focus line's colours, a warm wash and a brighter rim ({selectedRim:0.00}); " +
            "no overlay on the film (its outline pass would fill the see-through shell), and the hand's focus keeps its own");
        var wornRing = ringSkin!.AlbedoColor.A;
        for (var i = 0; i < 70; i++) look._Process(Tick);
        Check(Mathf.Abs(ringSkin.AlbedoColor.A - wornRing) > 0.02f && ringSkin.AlbedoColor.A is > 0.7f and <= 1f, $"the line breathes slowly ({wornRing:0.00} to {ringSkin.AlbedoColor.A:0.00})");
        look.SelectBubble("effect:unknown");
        Check(look.SelectedBubble == null && ring is { Visible: false } && film.GetShaderParameter("tint_color").AsColor().IsEqualApprox(BubbleDrawTuning.FilmTint) && look.Highlight.Target == thing,
            "choosing a bubble that is not drawn lets go of the chosen one, and the hand's focus is untouched");
        look.SelectBubble("effect:one");
        look.SelectBubble(null);
        Check(look.SelectedBubble == null && ring is { Visible: false } && thing.MaterialOverlay == look.Highlight.Overlay,
            "letting go takes the ring off and leaves the hand's focus");
        look.SelectBubble("effect:one");
        look.HideBubble("effect:one");
        Check(look.BubbleDrawCount == 0 && look.SelectedBubble == null && look.BubbleHalo("effect:one") == null && root!.IsQueuedForDeletion() && rope.IsQueuedForDeletion() &&
              mark!.IsQueuedForDeletion() && column!.IsQueuedForDeletion(),
            "a bubble that pops takes its drawing, its string, its ring, its column and its choice with it");
        await PhysicsFrames(2);
        Check(!IsInstanceValid(root) && !IsInstanceValid(rope) && look.FindChildren("Bubble*", "", true, false).Count == 0, "and they are gone a frame later");
        look.SetFocusHighlight(null);
        thing.QueueFree();

        // A boat: a dome over a thin disc at the water; the HUD's state cannot make it anything else.
        look.ShowBubble("effect:boat", new Vector3(0.6f, 0.01f, 0.2f), 0.12f, boat: true, ghost: false);
        look.SetBubbleState("effect:boat", new BubbleDrawState(BubbleForm.WalkIn, false, false, Vector3.Zero, false, false));
        Check(look.BubbleShell("effect:boat")?.Mesh is SphereMesh { IsHemisphere: true } dome && Mathf.IsEqualApprox(dome.Radius, 0.12f) && look.BubbleDeck("effect:boat") is { Mesh: CylinderMesh deck } &&
              Mathf.IsEqualApprox(deck.TopRadius, 0.12f) && look.BubbleStateOf("effect:boat") is { Form: BubbleForm.Boat },
            "a boat is a dome over a thin disc at the water, and stays a boat");
        look.HideBubble("effect:boat");

        // A ghost preview is a fainter film, and the HUD's state does not touch it.
        look.ShowBubble("ghost:abc", new Vector3(0f, 0.05f, 0f), 0.08f, boat: false, ghost: true);
        var ghostFilm = look.BubbleFilm("ghost:abc")!;
        look.SetBubbleState("ghost:abc", new BubbleDrawState(BubbleForm.Carry, true, true, ledTo, true, true));
        Check(Mathf.IsEqualApprox(ghostFilm.GetShaderParameter("rim_alpha").AsSingle(), BubbleDrawTuning.GhostRimAlpha) && BubbleDrawTuning.GhostRimAlpha < BubbleDrawTuning.HoldingRimAlpha &&
              Mathf.IsEqualApprox(ghostFilm.GetShaderParameter("tint_alpha").AsSingle(), BubbleDrawTuning.GhostTintAlpha) && look.BubbleString("ghost:abc") == null,
            "a ghost preview is a fainter film and no lead ever draws on it");
        look.HideBubble("ghost:abc");

        // The Gubble's colour changes: the film's rim follows (RecolorGlows, as the HUD calls it).
        look.ShowBubble("effect:tint", new Vector3(0.3f, 0.085f, 0.3f), 0.08f, false, false);
        var tinted = look.BubbleFilm("effect:tint")!;
        var teal = new Color("65b9b0");
        gubble.SetMeta(LookDirector.AuraColorMeta, teal);
        look.RecolorGlows();
        Check(tinted.GetShaderParameter("aura_color").AsColor().IsEqualApprox(teal), "the Gubble's colour changes and the bubble's rim takes it");

        // The film is unshaded: it follows how lit its place is, read about four times a second.
        look.SetClock(12f, 172);
        for (var i = 0; i < 30; i++) look._Process(Tick);
        var noon = tinted.GetShaderParameter("light_level").AsSingle();
        var expected = look.LightLevelAt(look.BubbleRoot("effect:tint")!.GlobalPosition);
        look.SetClock(2f, 172);
        for (var i = 0; i < 30; i++) look._Process(Tick);
        var night = tinted.GetShaderParameter("light_level").AsSingle();
        Check(Mathf.Abs(night - look.LightLevelAt(look.BubbleRoot("effect:tint")!.GlobalPosition)) < 0.02f && noon > night + 0.1f && expected > 0.2f,
            $"it darkens with the place: {noon:0.00} at noon, {night:0.00} at 02:00");
        look.HideBubble("effect:tint");

        // WarmBubble (the HUD asks it once the room is up): a bubble too small to see is drawn in front of the eye for a few frames, parts and all,
        // then goes, so the first cast is not the renderer's first look at the film. With no eye it waits.
        look.WarmBubble();
        var eye = holder.GetViewport().GetCamera3D();
        var waited = true;
        if (eye == null)
        {
            look._Process(Tick);
            waited = look.BubbleDrawCount == 0;
            eye = new Camera3D { Name = "WarmEye", Position = new Vector3(0f, 0.3f, 1f) };
            holder.AddChild(eye);
            eye.MakeCurrent();
        }
        look._Process(Tick);
        var warming = look.BubbleRoot(LookDirector.WarmBubbleId);
        Check(waited && warming != null && warming.Scale.X < 1e-3f && look.BubbleDrawCount == 1 && look.BubbleStateOf(LookDirector.WarmBubbleId) is { Led: false } &&
              warming.GlobalPosition.DistanceTo(eye.GlobalPosition) is > 0.5f and < 0.7f,
            $"WarmBubble draws (once there is an eye) a bubble too small to see {warming?.GlobalPosition.DistanceTo(eye.GlobalPosition):0.00} m in front of it");
        for (var i = 0; i < BubbleDrawTuning.WarmFrames + 2; i++) look._Process(Tick);
        Check(look.BubbleDrawCount == 0 && look.BubbleRoot(LookDirector.WarmBubbleId) == null && look.SelectedBubble == null, "and once its parts have been drawn it is gone, and none is left to find");
        holder.Free();
        await PhysicsFrames(1);
    }
}
