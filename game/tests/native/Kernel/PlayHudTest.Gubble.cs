using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Characters;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The drawn Gubble in the room (the characters round, K5; Characters/Gubble): the companion wears the family's Gubble in place of
/// the prototype figure; every gesture plays through the rig's gesture layer with the prototype's own envelopes and numbers, and the
/// cast swings the holding arm toward the spot; the glow's halo and light ride the aura bone; the aura recolours
/// the bubble's rim and the sparkles; the bubble darkens with the light level; the dim, the name tag, the pointing cue and the eye
/// views keep working; a library without the Gubble keeps the prototype.
/// </summary>
public partial class PlayHudTest
{
    private async Task TestDrawnGubble()
    {
        var companion = _world.Companion;
        var look = _world.Look;
        var host = CommandHost.Of(_world)!;
        var drawn = GubbleCharacter.Of(companion);
        var prototype = companion.GetNode<Node3D>("OriginalPrototypeBody");
        var prototypeParts = prototype.GetChildren().OfType<Node3D>().Where(n => n is not CharacterBody).ToList();
        Check(drawn != null && companion.BodyVisual == drawn.Visual && drawn.Body.Definition.Id == CharacterChoice.GubbleId && drawn.Body.GetParent() == prototype &&
              prototypeParts.Count > 0 && prototypeParts.All(p => !p.Visible) && _world.Player.BodyVisual == null,
            "the room's companion wears the family's Gubble, its prototype figure (and hat) hidden under the visual root; the player's own body is untouched");
        if (drawn == null) return;
        var body = drawn.Body;
        var layer = drawn.Gestures;
        Check(Enumerable.Range(0, drawn.Visual.Stack.Count).Count(i => drawn.Visual.Stack[i] == layer) == 1 && layer.Layer == PoseLayer.Gesture &&
              layer.ArmBone >= 0 && body.Bones[layer.ArmBone].Role == PartRole.Arm && body.Bones[layer.PropBone].Role == PartRole.Hand,
            $"its pose stack runs the gesture layer last, and the layer found the arm that ends in a hand by role ({body.Bones[Math.Max(layer.ArmBone, 0)].Name} reaching with {body.Bones[Math.Max(layer.PropBone, 0)].Name})");
        Check(body.CountBones(PartRole.Torch) == 0 && body.CountBones(PartRole.Flame) == 0,
            "the drawn Gubble carries no torch of its own: the torch is a separate item since the founder's note of 13 October");
        var surfaces = drawn.Visual.Look;
        var bubbleParts = surfaces.Of(CharacterSurfaceRole.Bubble).Select(s => s.Part).Distinct().OrderBy(p => p).ToArray();
        Check(bubbleParts.SequenceEqual(new[] { "arm_screen_right", "body", "hand_screen_right" }) && surfaces.Of(CharacterSurfaceRole.Effect).Count(s => s.Part.Contains("sparkle")) == 4,
            $"it wears its look: the bubble body and arm see-through ({string.Join(", ", bubbleParts)}), the four sparkles glowing");

        // Settle: the Gubble stays where it is, and nothing is playing.
        host.PlayerGoal("stay");
        for (var i = 0; i < 120 && companion.Playing != CompanionAvatar.Gesture.None; i++) await Frames(1);
        await Frames(2);

        await CheckGubbleLayers(companion, drawn);
        CheckGubbleGestures(companion, drawn, prototype);
        await CheckGubbleTorchArm(companion, drawn, prototype);
        await CheckGubbleGlowAnchors(companion, drawn, look);
        CheckGubbleAura(companion, drawn);
        await CheckGubbleLight(drawn, look);
        CheckGubbleDim(companion, drawn);
        Check(body.Pose.NonFiniteWrites == 0, $"no gesture wrote a NaN or an infinity into the pose ({body.Pose.NonFiniteWrites} refused)");
        await CheckGubbleFallback(look);
        host.PlayerGoal("follow");
    }

    /// <summary>The eye views, the name tag and the pointing cue: the drawn body on the Gubble's layer, out of its own eye; the tag and cue outside the rig.</summary>
    private async Task CheckGubbleLayers(CompanionAvatar companion, DrawnGubble drawn)
    {
        await Frames(1);
        var meshes = drawn.Body.FindChildren("*", nameof(GeometryInstance3D), true, false).OfType<GeometryInstance3D>().ToList();
        Check(meshes.Count == drawn.Body.Bones.Count(b => b.HasMesh) && meshes.Count >= 10 && meshes.All(g => g.Layers == EyeView.GubbleBodyLayer) && (companion.EyeCamera.CullMask & EyeView.GubbleBodyLayer) == 0 &&
              (_world.Player.EyeCamera.CullMask & EyeView.GubbleBodyLayer) != 0,
            $"every drawn part ({meshes.Count} meshes) is on the Gubble's own layer: its eye leaves it out, the player's eye draws it");
        var tag = companion.GetNode<Label3D>("CompanionLabel");
        var cue = companion.GetNode<Node3D>("PointingCue");
        Check(tag.GetParent() == companion && cue.GetParent() == companion && !drawn.Body.IsAncestorOf(tag) && !drawn.Body.IsAncestorOf(cue) && tag.Text == companion.NameTag,
            "the name tag and the pointing cue stay on the avatar node, outside the rig");
    }

    /// <summary>The prototype's transform for a gesture at t, written out from the gesture's own constants (CompanionAvatar.GesturePose).</summary>
    private static Transform3D PrototypeGesture(CompanionAvatar.Gesture gesture, float t, float h, Vector3 toward)
    {
        static Transform3D About(Vector3 pivot, Transform3D pose) => new Transform3D(Basis.Identity, pivot) * pose * new Transform3D(Basis.Identity, -pivot);
        var fade = 1f - t;
        var middle = Vector3.Up * (h * 0.5f);
        return gesture switch
        {
            CompanionAvatar.Gesture.Shake => About(middle, new Transform3D(new Basis(Vector3.Up, 0.45f * fade * Mathf.Sin(t * Mathf.Tau * 3f)), Vector3.Zero)),
            CompanionAvatar.Gesture.Shiver => About(middle, new Transform3D(Basis.Identity, new Vector3(0.012f * h * Mathf.Sin(t * Mathf.Tau * 14f), 0, 0))),
            CompanionAvatar.Gesture.Wiggle => About(middle, new Transform3D(new Basis(Vector3.Back, 0.18f * fade * Mathf.Sin(t * Mathf.Tau * 2f)), Vector3.Up * (0.08f * h * Mathf.Sin(t * Mathf.Pi)))),
            CompanionAvatar.Gesture.Shrug => About(middle, new Transform3D(Basis.Identity, Vector3.Up * (0.06f * h * Mathf.Sin(t * Mathf.Pi)))),
            CompanionAvatar.Gesture.Cast => new Transform3D(new Basis(Vector3.Up.Cross(toward).Normalized(), CompanionAvatar.CastLeanRad * CompanionAvatar.CastEnvelope(t)),
                Vector3.Up * (0.05f * h * Mathf.Sin(t * Mathf.Pi))),
            CompanionAvatar.Gesture.CastSelf => new Transform3D(Basis.Identity, Vector3.Up * (CompanionAvatar.CastLiftHeights * h * CompanionAvatar.CastEnvelope(t))),
            _ => Transform3D.Identity,
        };
    }

    /// <summary>The largest difference of any basis or origin component (metres or unitless).</summary>
    private static float Difference(Transform3D a, Transform3D b)
    {
        var largest = 0f;
        foreach (var v in new[] { a.Basis.X - b.Basis.X, a.Basis.Y - b.Basis.Y, a.Basis.Z - b.Basis.Z, a.Origin - b.Origin })
            largest = Mathf.Max(largest, Mathf.Max(Mathf.Abs(v.X), Mathf.Max(Mathf.Abs(v.Y), Mathf.Abs(v.Z))));
        return largest;
    }

    /// <summary>A spot on the floor this far ahead of the Gubble (its -Z) and this far to its right (+X).</summary>
    private static Vector3 AheadOf(CompanionAvatar companion, float ahead, float right) =>
        companion.GlobalPosition - companion.GlobalBasis.Z.Normalized() * ahead + companion.GlobalBasis.X.Normalized() * right;

    /// <summary>
    /// Every gesture, stepped as the frames step it: the root bone takes exactly the companion's GestureOffset, which is exactly the
    /// prototype's pose at that point of the gesture; the visual root's children are never moved; the layer is silent between gestures.
    /// </summary>
    private void CheckGubbleGestures(CompanionAvatar companion, DrawnGubble drawn, Node3D prototype)
    {
        var body = drawn.Body;
        var layer = drawn.Gestures;
        var pose = body.Pose;
        var state = companion.LastBodyState;
        var clock = new PoseClock(0, 1f / 60, 0);
        var h = companion.BodyHeightM;
        var children = prototype.GetChildren().OfType<Node3D>().Select(n => (Node: n, Transform: n.Transform)).ToList();
        var spot = AheadOf(companion, 0.4f, -0.1f);
        var toward = companion.ToLocal(spot) with { Y = 0 };
        toward = toward.Normalized();
        pose.Reset();
        pose.Begin(PoseLayer.Gesture);
        layer.Apply(pose, state, clock);
        var restHash = pose.Hash();
        pose.Reset();
        Check(restHash == pose.Hash() && companion.Playing == CompanionAvatar.Gesture.None, "with no gesture playing the layer leaves the pose alone");
        foreach (var gesture in new[] { CompanionAvatar.Gesture.Shake, CompanionAvatar.Gesture.Shiver, CompanionAvatar.Gesture.Wiggle, CompanionAvatar.Gesture.Shrug,
                     CompanionAvatar.Gesture.CastSelf, CompanionAvatar.Gesture.Cast })
        {
            switch (gesture)
            {
                case CompanionAvatar.Gesture.Shake: companion.Shake(); break;
                case CompanionAvatar.Gesture.Shiver: companion.Shiver(); break;
                case CompanionAvatar.Gesture.Wiggle: companion.Wiggle(); break;
                case CompanionAvatar.Gesture.Shrug: companion.Shrug(); break;
                case CompanionAvatar.Gesture.CastSelf: companion.CastGesture(null, "light"); break;
                default: companion.CastGesture(spot, "light"); break;
            }
            float offsetError = 0, prototypeError = 0, largest = 0, swingError = 0, peakSwing = 0;
            var steps = 0;
            var still = true;
            while (companion.Playing == gesture && steps < 200)
            {
                companion._Process(1.0 / 60);
                if (companion.Playing != gesture) break;
                steps++;
                pose.Reset();
                pose.Begin(PoseLayer.Gesture);
                layer.Apply(pose, state, clock);
                var root = pose.ModelTransform(CharacterBody.RootBone);
                var t = companion.GestureProgress;
                offsetError = Mathf.Max(offsetError, Difference(root, companion.GestureOffset));
                prototypeError = Mathf.Max(prototypeError, Difference(root, PrototypeGesture(gesture, t, h, toward)));
                largest = Mathf.Max(largest, Difference(root, Transform3D.Identity));
                if (gesture == CompanionAvatar.Gesture.Cast)
                {
                    swingError = Mathf.Max(swingError, Mathf.Abs(layer.LastArmSwingRad - GubbleGestureLayer.CastArmSwingRad * CompanionAvatar.CastEnvelope(t)));
                    peakSwing = Mathf.Max(peakSwing, layer.LastArmSwingRad);
                }
                else if (layer.LastArmSwingRad != 0) swingError = float.PositiveInfinity;
                still &= children.All(c => c.Node.Transform.IsEqualApprox(c.Transform));
            }
            pose.Reset();
            var seconds = gesture switch
            {
                CompanionAvatar.Gesture.Shake => CompanionAvatar.ShakeS, CompanionAvatar.Gesture.Shiver => CompanionAvatar.ShiverS,
                CompanionAvatar.Gesture.Wiggle => CompanionAvatar.WiggleS, CompanionAvatar.Gesture.Shrug => CompanionAvatar.ShrugS, _ => CompanionAvatar.CastS,
            };
            Check(Math.Abs(steps - seconds * 60) <= 2 && offsetError < 1e-5f && prototypeError < 1e-5f && largest > 1e-3f && still && companion.Playing == CompanionAvatar.Gesture.None &&
                  companion.GestureOffset == Transform3D.Identity,
                $"{gesture}: over its {steps} frames ({seconds} s) the root bone takes the companion's offset (within {offsetError:0.#e0}) and the prototype's own pose, same envelope and numbers (within {prototypeError:0.#e0}, at most {largest * 1000:0.#} mm or mrad), and the visual root's children never move");
            if (gesture == CompanionAvatar.Gesture.Cast)
                Check(swingError < 1e-4f && peakSwing > 0.95f * GubbleGestureLayer.CastArmSwingRad,
                    $"the cast swings the holding arm on the cast's own envelope (CastEnvelope × {GubbleGestureLayer.CastArmSwingRad} rad, within {swingError:0.#e0}; at the peak {peakSwing:0.00} rad); no other gesture moves it");
        }
    }

    /// <summary>The cast in real frames: the rig's hand (after the arm's spring) swings toward the spot and settles back; the body's node stays put.</summary>
    private async Task CheckGubbleTorchArm(CompanionAvatar companion, DrawnGubble drawn, Node3D prototype)
    {
        var body = drawn.Body;
        var torch = drawn.Gestures.PropBone;
        var spot = AheadOf(companion, 0.4f, 0f);
        float Level(Vector3 point) => new Vector2(point.X - spot.X, point.Z - spot.Z).Length();
        Vector3 TorchAt() => body.GlobalTransform * body.FinalBoneTransform(torch).Origin;
        var before = Level(TorchAt());
        var bodyPose = body.Transform;
        companion.CastGesture(spot, "light");
        var closest = before;
        var rootMoved = 0f;
        for (var i = 0; i < 40; i++)
        {
            await Frames(1);
            closest = Mathf.Min(closest, Level(TorchAt()));
            rootMoved = Mathf.Max(rootMoved, body.Skeleton.GetBonePoseRotation(CharacterBody.RootBone).GetAngle());
        }
        await Frames(90);
        var after = Level(TorchAt());
        // Settled within 8 mm: the float gait (K3) never holds the body quite still, and the arm's spring rings down slowly.
        Check(before - closest > 0.004f && rootMoved > 0.2f && body.Transform == bodyPose && Mathf.Abs(after - before) < 0.008f,
            $"in the room the cast leans the drawn body ({rootMoved:0.00} rad at most) and swings its hand toward the spot, {(before - closest) * 1000:0.#} mm nearer through the arm's spring, then settles ({(after - before) * 1000:0.#} mm off); the body's node itself never moves");
    }

    /// <summary>The glow's anchors: the halo and its light on the aura bone (no torch flame since 13 October), riding the bone; the spark leaves the Gubble's middle.</summary>
    private async Task CheckGubbleGlowAnchors(CompanionAvatar companion, DrawnGubble drawn, LookDirector look)
    {
        var body = drawn.Body;
        Check(body.Bones[drawn.HaloBone].Role == PartRole.Aura && drawn.LightAnchor == null && drawn.LightBone < 0 &&
              companion.GetMeta(GlowLook.HaloAnchorMeta).AsGodotObject() == drawn.HaloAnchor && !companion.HasMeta(GlowLook.LightAnchorMeta),
            $"the companion names its halo anchor to the look ({body.Bones[drawn.HaloBone].Name}) and no light anchor: with no torch flame the light sits with the halo");
        Vector3 Expected(int bone) => body.GlobalTransform * body.FinalBoneTransform(bone) * (body.Bones[bone].HasMesh ? body.Bones[bone].MeshBounds.GetCenter() : Vector3.Zero);
        const string id = "effect:k5_halo";
        Check(look.StartGlow(id, companion, Vector3.Zero, 0.6f, 0.6f), "a glow starts on the drawn Gubble");
        await Frames(2);
        var light = look.GlowLight(id)!;
        var halo = look.GlowRoot(id)!.GetNode<MeshInstance3D>("Halo");
        look._Process(0);
        var up = drawn.HaloAnchor.GlobalPosition - companion.GlobalPosition;
        // Since the torch left the drawing (13 October) the bigger bubble's middle is near 0.55 of the height; that the halo rides the
        // bone, not the fraction, shows mid-cast below, where the bone moves and the fraction would not.
        Check(halo.GlobalPosition.DistanceTo(drawn.HaloAnchor.GlobalPosition) < 1e-4f && drawn.HaloAnchor.GlobalPosition.DistanceTo(Expected(drawn.HaloBone)) < 1e-4f,
            $"the halo sits on the aura bone, the bubble's middle ({up.Y * 100:0.0} cm up; {GlowLook.FollowHeightFraction} of the height is {companion.BodyHeightM * GlowLook.FollowHeightFraction * 100:0.0} cm)");
        Check(light.GlobalPosition.DistanceTo(drawn.HaloAnchor.GlobalPosition) < 1e-4f,
            $"the glow's light sits with the halo in the bubble's middle ({up.Y * 100:0.0} cm up)");
        var probe = drawn.HaloAnchor.GlobalPosition + new Vector3(0.2f, 0f, 0.1f);
        var counted = look.EstimateLightAt(probe).Glows;
        var expected = GlowLook.PointLight(GlowLook.Energy(0.6f), light.LightColor, 0.6f, GlowLook.Attenuation, drawn.HaloAnchor.GlobalPosition.DistanceTo(probe));
        Check(look.GlowCount == 1 && Mathf.Abs(counted - expected) < 1e-5f, $"the light level counts the glow from where its light is, the bubble's middle ({counted:0.0000} against {expected:0.0000})");
        // The anchors ride the bones: mid-cast the body leans and both move with it.
        var haloRest = drawn.HaloAnchor.GlobalPosition;
        companion.CastGesture(AheadOf(companion, 0.4f, 0f), "light");
        var moved = 0f;
        var follows = true;
        for (var i = 0; i < 12; i++)
        {
            await Frames(1);
            look._Process(0);
            moved = Mathf.Max(moved, drawn.HaloAnchor.GlobalPosition.DistanceTo(haloRest));
            follows &= halo.GlobalPosition.DistanceTo(drawn.HaloAnchor.GlobalPosition) < 1e-4f && light.GlobalPosition.DistanceTo(drawn.HaloAnchor.GlobalPosition) < 1e-4f &&
                       drawn.HaloAnchor.GlobalPosition.DistanceTo(Expected(drawn.HaloBone)) < 1e-4f;
        }
        Check(follows && moved > 0.004f, $"mid-cast the anchor follows its bone as the body leans ({moved * 1000:0.#} mm), and the halo and the light follow it");
        look.StopGlow(id);
        for (var i = 0; i < 60 && companion.Playing != CompanionAvatar.Gesture.None; i++) await Frames(1);
        // A wisp: the spark leaves the Gubble's middle, the aura bone.
        const string wisp = "effect:k5_wisp";
        look.StartGlow(wisp, null, AheadOf(companion, 0.5f, 0.2f), 0.6f, 0.6f);
        var head = look.GlowSpark(wisp)?.GetNodeOrNull<Node3D>("Head");
        Check(head != null && head.GlobalPosition.DistanceTo(drawn.HaloAnchor.GlobalPosition) < 1e-4f, "cast at a spot, the spark leaves from the drawn Gubble's middle (its aura bone)");
        look.StopGlow(wisp);
    }

    /// <summary>The aura: a new colour reaches the bubble's rim and the sparkles (and the flame, a little) at once; cleared, the drawing's own.</summary>
    private void CheckGubbleAura(CompanionAvatar companion, DrawnGubble drawn)
    {
        var before = companion.AuraColor;
        var blue = new Color("3a6df0");
        var surfaces = drawn.Visual.Look.Surfaces;
        bool Wears(Color? aura, Color hint) => surfaces.All(s => s.Role switch
        {
            CharacterSurfaceRole.Bubble => ((ShaderMaterial)s.Mesh.GetSurfaceOverrideMaterial(s.Surface)).GetShaderParameter("aura_color").AsColor().IsEqualApprox(aura ?? hint),
            CharacterSurfaceRole.Effect => ((StandardMaterial3D)s.Mesh.GetSurfaceOverrideMaterial(s.Surface)).AlbedoColor.IsEqualApprox(CharacterLook.Tinted(s.BaseColor, aura ?? hint,
                s.Part == "torch_flame" ? CharacterEffectLook.Default.FlameAuraTint : CharacterEffectLook.Default.SparkleAuraTint)),
            _ => true,
        });
        var hint = new Color("b4eed8");
        Check(before != null && Wears(before, hint), $"the profile's aura ({before?.ToHtml(false)}) is on the bubble's rim and the sparkles already");
        companion.SetAuraColor(blue);
        Check(Wears(blue, hint), "\"Change the Gubble's color\" (SetAuraColor) recolours the rim and the sparkles at once, the flame a little");
        companion.SetAuraColor(null);
        Check(Wears(null, hint), "with no aura they take the drawing's own mint again");
        companion.SetAuraColor(before);
        Check(Wears(before, hint), "(the profile's aura put back)");
    }

    /// <summary>The bubble's light level: read a few times a second from the look at the Gubble's middle, lower at night.</summary>
    private async Task CheckGubbleLight(DrawnGubble drawn, LookDirector look)
    {
        var bubbles = drawn.Visual.Look.Surfaces.Where(s => s.Role == CharacterSurfaceRole.Bubble).Select(s => (ShaderMaterial)s.Mesh.GetSurfaceOverrideMaterial(s.Surface)).ToList();
        bool Holds(float level) => bubbles.All(m => Mathf.IsEqualApprox(m.GetShaderParameter("light_level").AsSingle(), level));
        var readings = drawn.LightReadings;
        await Frames(60);
        Check(drawn.Look == look && drawn.LightReadings - readings is >= 3 and <= 5 && Holds(drawn.LightLevel) && bubbles.Count == 3,
            $"the bubble's light level is read from the look {drawn.LightReadings - readings} times in a second and every bubble surface holds it ({drawn.LightLevel:0.00})");
        try
        {
            look.SetClock(12f, 172);
            var noon = drawn.ReadLight();
            var noonHeld = Holds(noon ?? -1);
            look.SetClock(2f, 172);
            var night = drawn.ReadLight();
            Check(noon is { } day && night is { } dark && noonHeld && Holds(dark) && dark < day - 0.05f &&
                  Mathf.IsEqualApprox(dark, look.LightLevelAt(drawn.HaloAnchor.GlobalPosition)),
                $"at night the unshaded bubble darkens: its level follows LightLevelAt at its middle ({noon:0.00} at noon, {night:0.00} at 2:00)");
        }
        finally
        {
            look.ReleaseClock();
            drawn.ReadLight();
        }
    }

    /// <summary>The dim (the dusk moment's cue) lies over every drawn part and comes off again.</summary>
    private void CheckGubbleDim(CompanionAvatar companion, DrawnGubble drawn)
    {
        var meshes = drawn.Body.FindChildren("*", nameof(GeometryInstance3D), true, false).OfType<GeometryInstance3D>().ToList();
        var was = companion.Dimmed;
        companion.Dim(false);
        companion.Dim(true);
        var overlay = meshes[0].MaterialOverlay as StandardMaterial3D;
        Check(overlay != null && meshes.All(m => m.MaterialOverlay == overlay) && Mathf.IsEqualApprox(overlay.AlbedoColor.A, CompanionAvatar.DimAlpha),
            $"dimmed, the overlay lies on every drawn part ({meshes.Count})");
        companion.Dim(false);
        Check(meshes.All(m => m.MaterialOverlay == null), "undimmed, it comes off every one");
        companion.Dim(was);
    }

    /// <summary>A library without the Gubble keeps the prototype; dressed and undressed again, a companion goes back to it.</summary>
    private async Task CheckGubbleFallback(LookDirector look)
    {
        var holder = new Node3D { Name = "GubbleFallback" };
        AddChild(holder);
        var spare = new CompanionAvatar { Name = "SpareGubble" };
        holder.AddChild(spare);
        spare.SetPhysicsProcess(false);
        spare.GlobalPosition = new Vector3(40f, 0f, 40f);
        var parts = spare.GetNode<Node3D>("OriginalPrototypeBody").GetChildren().OfType<Node3D>().ToList();
        var empty = CharacterLibrary.Load("user://tests/play_hud/no_characters");
        Check(GubbleCharacter.Dress(spare, empty, look) == null && GubbleCharacter.Dress(spare, null, look) == null && spare.BodyVisual == null &&
              parts.All(p => p.Visible) && !spare.HasMeta(GlowLook.HaloAnchorMeta),
            "a library without the Gubble (or none) leaves the companion its prototype figure");
        spare.Shake();
        spare._Process(1.0 / 60);
        spare._Process(1.0 / 60);
        var prototypeMoves = parts.Any(p => p.Transform.Basis != Basis.Identity);
        await Frames(40);
        Check(prototypeMoves && spare.Playing == CompanionAvatar.Gesture.None && parts.All(p => p.Transform.Basis == Basis.Identity),
            "and its gestures move the prototype's parts as before, back to rest after");
        var dressed = GubbleCharacter.Dress(spare, _world.Characters, look);
        var first = dressed?.Body;
        var again = GubbleCharacter.Dress(spare, _world.Characters, look);
        await Frames(1);
        Check(dressed != null && again != null && GubbleCharacter.Of(spare) == again && first != null && !IsInstanceValid(first) && parts.All(p => !p.Visible),
            "dressed twice, the second Gubble replaces the first, which is freed");
        GubbleCharacter.Undress(spare);
        await Frames(1);
        Check(spare.BodyVisual == null && GubbleCharacter.Of(spare) == null && parts.All(p => p.Visible) && !spare.HasMeta(GlowLook.HaloAnchorMeta) &&
              !spare.HasMeta(GlowLook.LightAnchorMeta) && !IsInstanceValid(again!.Body),
            "undressed, the prototype figure comes back, the look's anchors go, and the drawn Gubble is freed");
        holder.QueueFree();
        await Frames(1);
    }
}
