using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native;
using GarageGame.Native.Characters;
using GarageGame.Native.Characters.Gaits;

namespace GarageGame.Tests.Characters;

/// <summary>The phase, each gait's own shapes, the life layer, the yield, the events and the bounce key.</summary>
public partial class CharacterGaitsTest
{
    /// <summary>One frame of a driven motion: the target speed, the turn rate, and whether the body climbs or swims.</summary>
    private readonly record struct Motion(float Speed, float Turn = 0, bool Climbing = false, bool Swimming = false);

    /// <summary>Feeds <paramref name="frames"/> frames of a scripted motion to a fresh installed visual and calls back after each.</summary>
    private (CharacterVisual Visual, GaitLayer Gait, LifeLayer Life) Drive(string id, int frames, Func<int, Motion> motion,
        Action<int, BodyState, CharacterVisual, GaitLayer, LifeLayer>? each = null, float dt = Dt, double timeOffset = 0, double distanceFrom = 0)
    {
        var (visual, gait, life) = Installed(id);
        var walker = new Walker { Distance = distanceFrom };
        for (var frame = 0; frame < frames; frame++)
        {
            var step = motion(frame);
            var state = walker.Step(step.Speed, step.Turn, dt, step.Climbing, step.Swimming);
            visual.Stack.Evaluate(state, new PoseClock(timeOffset + frame * (double)dt, dt, frame));
            each?.Invoke(frame, state, visual, gait, life);
        }
        return (visual, gait, life);
    }

    /// <summary>The phase follows the distance walked: not the clock, not the frame rate.</summary>
    private void TestPhaseByDistance()
    {
        foreach (var id in Shipped)
        {
            var (a, gaitA, _) = Drive(id, 180, _ => new Motion(MotionProfile.WalkSpeedMps));
            // The same states on a clock 1000 s later: the phase does not read the time.
            var (b, gaitB, _) = Drive(id, 180, _ => new Motion(MotionProfile.WalkSpeedMps), timeOffset: 1000);
            // Half the frame rate over the same 3 s: the same distance, the same phase.
            var (c, gaitC, _) = Drive(id, 90, _ => new Motion(MotionProfile.WalkSpeedMps), dt: 2 * Dt);
            // 2,000 km from home (a float's step there is 12.5 cm, far over a 5 mm tick): the same walk, the same phase.
            var (far, gaitFar, _) = Drive(id, 180, _ => new Motion(MotionProfile.WalkSpeedMps), distanceFrom: 2_000_000);
            Check(Math.Abs(gaitFar.Cycles / gaitA.Cycles - 1) < 1e-4, F($"{id}: 2,000 km from home the same walk gives the same phase ({gaitFar.Cycles:0.0000} against {gaitA.Cycles:0.0000})"));
            far.Body.Free();
            Check(gaitA.Cycles == gaitB.Cycles && gaitA.Cycles > 0, F($"{id}: the phase ignores the clock ({gaitA.Cycles:0.0000} cycles either way)"));
            Check(Math.Abs(gaitC.Cycles / gaitA.Cycles - 1) < 0.02, F($"{id}: at 30 frames a second the same walk gives the same phase ({gaitC.Cycles:0.0000} against {gaitA.Cycles:0.0000})"));
            foreach (var visual in new[] { a, b, c }) visual.Body.Free();
        }
        // Standing still for 120 frames, from rest: no step at all.
        var (still, gait, _) = Drive("stickbear", 120, _ => new Motion(0));
        Check(gait.Cycles == 0 && gait.Footfalls == 0, "standing still for 120 frames from rest takes no step");
        still.Body.Free();
    }

    private static float Angle(Quaternion rotation, Vector3 axis)
    {
        var q = rotation.W < 0 ? -rotation : rotation;
        return 2 * MathF.Atan2(new Vector3(q.X, q.Y, q.Z).Dot(axis), q.W);
    }

    private static float Correlation(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        var ma = a.Average();
        var mb = b.Average();
        double ab = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            ab += (a[i] - ma) * (b[i] - mb);
            aa += (a[i] - ma) * (a[i] - ma);
            bb += (b[i] - mb) * (b[i] - mb);
        }
        return (float)(ab / Math.Sqrt(Math.Max(aa * bb, 1e-30)));
    }

    private static int Crossings(IReadOnlyList<float> values)
    {
        var mean = values.Average();
        var count = 0;
        for (var i = 1; i < values.Count; i++)
            if (values[i - 1] < mean && values[i] >= mean) count++;
        return count;
    }

    /// <summary>Stickbear's stride: legs opposite, the planted foot still on the floor, arms counter-swinging, the head bobbing twice a stride.</summary>
    private void TestStride()
    {
        var legR = new List<float>();
        var legL = new List<float>();
        var footZ = new List<float>();
        var pawZ = new List<float>();
        var nod = new List<float>();
        var slip = 0f;
        var sole = 0f;
        var plantedFrames = 0;
        var cycles0 = 0.0;
        var cycles1 = 0.0;
        float lastFootWorldZ = float.NaN;
        var (visual, gait, _) = Drive("stickbear", 300, _ => new Motion(MotionProfile.WalkSpeedMps), (frame, state, v, g, _) =>
        {
            if (frame < 60) { cycles0 = g.Cycles; return; }
            cycles1 = g.Cycles;
            var body = v.Body;
            var pose = body.Pose;
            var rig = g.Rig;
            legR.Add(Angle(pose.GetRotation(rig.Leg[GaitRig.Right]), Vector3.Right));
            legL.Add(Angle(pose.GetRotation(rig.Leg[GaitRig.Left]), Vector3.Right));
            var foot = rig.Foot[GaitRig.Right];
            var footModel = pose.ModelTransform(foot).Origin;
            footZ.Add(footModel.Z - body.Bones[foot].RestModel.Z);
            pawZ.Add(pose.ModelTransform(rig.Hand[GaitRig.Right]).Origin.Z - body.Bones[rig.Hand[GaitRig.Right]].RestModel.Z);
            nod.Add(Angle(pose.GetRotation(rig.HeadBone), Vector3.Right));
            // The right foot is planted while its own leg phase is in its contact share: its sole stays on the floor and in place.
            var stride = (StrideGait)g.Gait;
            var share = stride.ContactShare(g.Frame.StrideM, out var unusedSweep);
            var p = GaitMath.Frac(g.Cycles);
            var worldZ = footModel.Z + state.Position.Z;
            if (p > 0.02f && p < 0.5f * share - 0.02f)
            {
                var soleY = (pose.ModelTransform(foot) * new Vector3(0, -rig.SoleDepth[GaitRig.Right], 0)).Y;
                sole = Mathf.Max(sole, Mathf.Abs(soleY));
                if (float.IsFinite(lastFootWorldZ)) slip = Mathf.Max(slip, Mathf.Abs(worldZ - lastFootWorldZ));
                lastFootWorldZ = worldZ;
                plantedFrames++;
            }
            else lastFootWorldZ = float.NaN;
        });
        var cycles = cycles1 - cycles0;
        Check(Correlation(legR, legL) < -0.5f, F($"Stickbear's legs swing opposite each other (correlation {Correlation(legR, legL):0.00})"));
        Check(legR.Max() > 0.25f && legR.Min() < -0.25f, F($"Stickbear's right leg swings {Mathf.RadToDeg(legR.Min()):0}° to {Mathf.RadToDeg(legR.Max()):0}°"));
        Check(plantedFrames > 10 && slip < 0.0006f && sole < 0.0015f,
            F($"Stickbear's planted foot stays put: {plantedFrames} planted frames, slip at most {slip * 1000:0.00} mm a frame, sole within {sole * 1000:0.00} mm of the floor"));
        Check(Correlation(footZ, pawZ) < -0.4f, F($"Stickbear's arms counter-swing his legs (right paw against right foot: {Correlation(footZ, pawZ):0.00})"));
        var nods = Crossings(nod);
        Check(Math.Abs(nods - 2 * cycles) <= 1.5, F($"Stickbear's head bobs twice a stride ({nods} bobs in {cycles:0.00} cycles)"));
        visual.Body.Free();

        // A run leans him forward and lengthens the stride.
        var (runner, runGait, _) = Drive("stickbear", 120, _ => new Motion(MotionProfile.RunSpeedMps));
        var stride = (StrideGait)runGait.Gait;
        Check(Mathf.RadToDeg(runGait.LeanPitchRad) > 3 && runGait.Frame.StrideM > 1.5f * runner.Body.Profile.StrideAt(MotionProfile.WalkSpeedMps) &&
              stride.FlightM(1) > stride.FlightM(0),
            F($"a run leans Stickbear {Mathf.RadToDeg(runGait.LeanPitchRad):0.0}° forward, strides {runGait.Frame.StrideM * 100:0.0} cm and flies higher"));
        runner.Body.Free();
    }

    /// <summary>Cloudpuff and Tomato Man hop: rise, squash at the landing, stretch on the rise, arms up on the rise.</summary>
    private void TestHop()
    {
        foreach (var id in new[] { "cloudpuff", "tomato_man" })
        {
            var heights = new List<float>();
            var squash = new List<float>();
            var armUp = new List<float>();
            var (visual, gait, _) = Drive(id, 300, _ => new Motion(MotionProfile.WalkSpeedMps), (frame, state, v, g, _) =>
            {
                if (frame < 60) return;
                var pose = v.Body.Pose;
                heights.Add(pose.GetPosition(CharacterBody.RootBone).Y);
                squash.Add(pose.GetScale(CharacterBody.RootBone).Y);
                var hand = g.Rig.Hand[GaitRig.Right];
                armUp.Add(hand >= 0 ? pose.ModelTransform(hand).Origin.Y - pose.ModelTransform(CharacterBody.RootBone).Origin.Y : 0);
            });
            var hop = (HopGait)gait.Gait;
            var top = hop.HopHeightM(0);
            Check(heights.Max() > 0.8f * top && heights.Min() > -0.002f, F($"{id} hops {heights.Max() * 1000:0.0} mm high (designed {top * 1000:0.0} mm)"));
            Check(squash.Min() < 1 - 0.5f * hop.SquashDepth && squash.Max() > 1 + 0.2f * hop.SquashDepth,
                F($"{id} squashes to {squash.Min():0.000} at the landing and stretches to {squash.Max():0.000} on the rise"));
            var high = heights.Select((h, i) => (h, i)).Where(p => p.h > 0.6f * top).Select(p => armUp[p.i]).DefaultIfEmpty(0).Average();
            var low = heights.Select((h, i) => (h, i)).Where(p => p.h < 0.05f * top).Select(p => armUp[p.i]).DefaultIfEmpty(0).Average();
            Check(high > low + 0.001f, F($"{id}'s arms are up in the air ({(high - low) * 1000:0.0} mm higher than on the ground)"));
            Check(gait.Footfalls > 0 && gait.Rig.Chained[gait.Rig.Leg[GaitRig.Right] >= 0 ? gait.Rig.Leg[GaitRig.Right] : gait.Rig.Foot[GaitRig.Right]],
                $"{id} lands each hop (footfalls) and its legs or feet dangle on their springs");
            visual.Body.Free();
        }
    }

    /// <summary>Potato Man waddles: rolls onto each foot in turn, wider at a walk than at a run, a yaw wobble out of phase.</summary>
    private void TestWaddle()
    {
        float Roll(Quaternion q) => MathF.Asin(Mathf.Clamp((q * Vector3.Up).X, -1, 1));
        float Yaw(Quaternion q) { var f = q * Vector3.Forward; return MathF.Atan2(-f.X, -f.Z); }
        var rolls = new List<float>();
        var yaws = new List<float>();
        var lift = new List<float>();
        var cycles0 = 0.0;
        var cycles1 = 0.0;
        var (visual, gait, _) = Drive("potato_man", 300, _ => new Motion(MotionProfile.WalkSpeedMps), (frame, state, v, g, _) =>
        {
            if (frame < 60) { cycles0 = g.Cycles; return; }
            cycles1 = g.Cycles;
            var rotation = v.Body.Pose.GetRotation(CharacterBody.RootBone);
            rolls.Add(Roll(rotation) - g.LeanRollRad);
            yaws.Add(Yaw(rotation));
            lift.Add(v.Body.Pose.GetPosition(CharacterBody.RootBone).Y);
        });
        var waddle = (WaddleGait)gait.Gait;
        var cycles = cycles1 - cycles0;
        var walkRoll = 0.5f * (rolls.Max() - rolls.Min());
        Check(Math.Abs(Crossings(rolls) - cycles) <= 1.5, F($"Potato Man rolls onto each foot once a stride ({Crossings(rolls)} rolls in {cycles:0.00} cycles)"));
        Check(walkRoll > 0.7f * waddle.RollRad(0), F($"Potato Man rolls ±{Mathf.RadToDeg(walkRoll):0.0}° at a walk (designed ±{Mathf.RadToDeg(waddle.RollRad(0)):0.0}°)"));
        Check(Mathf.Abs(Correlation(rolls, yaws)) < 0.3f && yaws.Max() - yaws.Min() > 0.05f, F($"his yaw wobbles a quarter cycle out of phase (correlation {Correlation(rolls, yaws):0.00})"));
        Check(lift.Max() > 0.002f, F($"each roll lifts him ({lift.Max() * 1000:0.0} mm)"));
        visual.Body.Free();
        var runRolls = new List<float>();
        var (runner, _, _) = Drive("potato_man", 240, _ => new Motion(MotionProfile.RunSpeedMps), (frame, state, v, g, _) =>
        {
            if (frame >= 90) runRolls.Add(Roll(v.Body.Pose.GetRotation(CharacterBody.RootBone)) - g.LeanRollRad);
        });
        var runRoll = 0.5f * (runRolls.Max() - runRolls.Min());
        Check(runRoll < walkRoll, F($"tighter at a run (±{Mathf.RadToDeg(runRoll):0.0}° against ±{Mathf.RadToDeg(walkRoll):0.0}°)"));
        runner.Body.Free();
    }

    /// <summary>The Gubble floats: a ±4 mm bob over 2.6 s, never still, leaning into travel and banking into turns.</summary>
    private void TestFloat()
    {
        var heights = new List<float>();
        var stillFrames = 0;
        var last = Vector3.Zero;
        var (visual, gait, _) = Drive("gubble", 1560, _ => new Motion(0), (frame, state, v, g, _) =>
        {
            var position = v.Body.Pose.GetPosition(CharacterBody.RootBone);
            heights.Add(position.Y);
            if (frame > 0 && position == last) stillFrames++;
            last = position;
        });
        var bob = (FloatGait)gait.Gait;
        var amplitude = 0.5f * (heights.Max() - heights.Min());
        var period = 1560 * Dt / Math.Max(1, Crossings(heights));
        Check(Mathf.Abs(amplitude / bob.BobAmplitudeM - 1) < 0.25f && bob.BobAmplitudeM > 0.003f && bob.BobAmplitudeM < 0.005f,
            F($"the Gubble bobs ±{amplitude * 1000:0.00} mm (designed ±{bob.BobAmplitudeM * 1000:0.00} mm, about 4)"));
        Check(Math.Abs(period - FloatGait.BobPeriodS) < 0.3f, F($"over about {period:0.00} s (2.6), measured over 26 s"));
        Check(stillFrames == 0 && heights.Min() > -0.0005f, F($"never fully still, and the hem never dips below where it was drawn ({heights.Min() * 1000:0.00} mm)"));
        visual.Body.Free();
        var lean = 0f;
        var bank = 0f;
        var (mover, moving, _) = Drive("gubble", 240, f => new Motion(MotionProfile.WalkSpeedMps, f < 120 ? 0 : 2.5f), (frame, state, v, g, _) =>
        {
            if (frame < 30) lean = Mathf.Max(lean, g.LeanPitchRad);
            if (frame > 150) bank = Mathf.Min(bank, g.LeanRollRad);
        });
        Check(Mathf.RadToDeg(lean) > 2 && Mathf.RadToDeg(bank) < -1, F($"it leans {Mathf.RadToDeg(lean):0.0}° into a start and banks {Mathf.RadToDeg(-bank):0.0}° into a left turn"));
        Check(moving.Footfalls == 0, "a float has no footfalls");
        mover.Body.Free();
    }

    /// <summary>Blinks every 2 to 6 s from a seed, breathing ±1% over 3.5 s, and each effect moving by its hint.</summary>
    private void TestLife()
    {
        foreach (var id in Shipped)
        {
            var starts = new List<double>();
            var closed = 1f;
            var breath = new List<float>();
            var (visual, _, life) = Drive(id, 120 * 60, _ => new Motion(0), (frame, state, v, g, l) =>
            {
                if (starts.Count == 0 || starts[^1] != l.LastBlinkS) { if (!double.IsNaN(l.LastBlinkS)) starts.Add(l.LastBlinkS); }
                if (l.Blinkers.Count > 0) closed = Mathf.Min(closed, v.Body.Pose.GetScale(l.Blinkers[0]).Y);
                if (frame < 60 * 14) breath.Add(v.Body.Pose.GetScale(v.Body.BodyBone).X);
            });
            var gaps = starts.Zip(starts.Skip(1), (a, b) => b - a).ToList();
            Check(life.Blinkers.Count > 0 && gaps.Count >= 15 && gaps.All(g => g >= LifeLayer.BlinkMinS - 1e-6 && g <= LifeLayer.BlinkMaxS + 1e-6) && closed < 0.2f,
                F($"{id} blinks {starts.Count} times in 120 s, every {gaps.DefaultIfEmpty(0).Min():0.0} to {gaps.DefaultIfEmpty(0).Max():0.0} s, its eyes closing to {closed:0.00}"));
            var breathSwing = 0.5f * (breath.Max() - breath.Min());
            var period = 14.0 / Math.Max(1, Crossings(breath));
            Check(Mathf.Abs(breathSwing - LifeLayer.BreathAmount) < 0.002f && Math.Abs(period - LifeLayer.BreathPeriodS) < 0.6,
                F($"{id} breathes ±{breathSwing * 100:0.00}% over {period:0.00} s"));
            // The same seed blinks the same way; another seed does not.
            var (again, _, lifeAgain) = Drive(id, 30 * 60, _ => new Motion(0));
            var other = new LifeLayer(again.Body, life.Seed ^ 0x5A5A5A5Au);
            Check(starts.Count > 0 && Math.Abs(lifeAgain.LastBlinkS - starts.Where(s => s <= (30 * 60 - 1) * (double)Dt + 1e-9).Last()) < 1e-9 && other.NextBlinkS != new LifeLayer(again.Body, life.Seed).NextBlinkS,
                $"{id}: the same seed blinks at the same times; another seed blinks at others");
            visual.Body.Free();
            again.Body.Free();
        }

        // The Gubble's effects move by their hints; drift sparkles drift farther when it moves fast.
        var gubble = Visual("gubble");
        var bones = gubble.Body.Bones;
        int First(EffectMotion motion) => bones.First(b => b.Effect == motion).Index;
        var twinkle = First(EffectMotion.Twinkle);
        var drift = First(EffectMotion.Drift);
        var aura = First(EffectMotion.Pulse);
        gubble.Body.Free();
        var twinkles = new List<float>();
        var auras = new List<float>();
        var restReach = new List<float>();
        var runReach = new List<float>();
        var (g1, _, _) = Drive("gubble", 600, f => new Motion(f < 300 ? 0 : MotionProfile.RunSpeedMps), (frame, state, v, _, _) =>
        {
            var pose = v.Body.Pose;
            twinkles.Add(pose.GetScale(twinkle).X);
            auras.Add(pose.GetScale(aura).X);
            var offset = (pose.GetPosition(drift) - pose.RestPosition(drift)).Length();
            if (frame < 300) restReach.Add(offset);
            else if (frame > 400) runReach.Add(offset);
        });
        Check(twinkles.Max() - twinkles.Min() > 0.2f && auras.Max() - auras.Min() > 0.04f,
            F($"the Gubble's sparkles twinkle ({twinkles.Min():0.00} to {twinkles.Max():0.00}), its aura pulses"));
        // A flame flickers: the torch's, a separate prop since the founder's note of 13 October (built from its folder like a character).
        var torchFolder = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "assets", "props", "torch"));
        var torchEntry = CharacterLibrary.LoadFolder(torchFolder, "torch");
        Check(torchEntry.Definition != null, "the torch prop loads like a character: " + (torchEntry.RefusalReason ?? "loaded"));
        if (torchEntry.Definition != null)
        {
            var torch = CharacterVisual.Create(torchEntry.Definition);
            CharacterGaits.Install(torch);
            var flameBone = torch.Body.Bones.First(b => b.Effect == EffectMotion.Flicker).Index;
            var flames = new List<float>();
            var still = new BodyState { Grounded = true, FloorNormal = Vector3.Up, BodyHeightM = 0.1f, GravityMps2 = 3.5f, WaterSurfaceY = float.NaN };
            for (var frame = 1; frame <= 300; frame++)
            {
                torch.Stack.Evaluate(still, new PoseClock(frame / 60.0, 1f / 60, frame));
                flames.Add(torch.Body.Pose.GetScale(flameBone).Y);
            }
            Check(flames.Max() - flames.Min() > 0.1f, F($"the torch's flame flickers ({flames.Min():0.00} to {flames.Max():0.00})"));
            torch.Body.Free();
        }
        Check(restReach.Max() > 0.001f && runReach.Average() > 1.5f * restReach.Average(),
            F($"its drifting sparkles wander {restReach.Average() * 1000:0.0} mm at rest and {runReach.Average() * 1000:0.0} mm at a run"));
        g1.Body.Free();
    }

    /// <summary>
    /// Any drawing moves: every gait runs on every character, whatever it was drawn as (a float on legs, a stride without
    /// legs, a hop with springs on its legs), through the acceptance script: finite, nothing written to a rigid part, never
    /// below the floor, the squash within its limit.
    /// </summary>
    private void TestEveryGaitOnEveryBody()
    {
        foreach (var id in Shipped)
        {
            var failures = new List<string>();
            foreach (var kind in Enum.GetValues<MotionKind>())
            {
                var visual = Visual(id);
                var body = visual.Body;
                var rig = new GaitRig(body);
                IGait gait = kind switch
                {
                    MotionKind.Float => new FloatGait(rig),
                    MotionKind.Hop => new HopGait(rig),
                    MotionKind.Waddle => new WaddleGait(rig),
                    _ => new StrideGait(rig),
                };
                visual.Stack.Add(new GaitLayer(rig, gait));
                visual.Stack.Add(new LifeLayer(rig, 7));
                var scratch = new Transform3D[body.BoneCount];
                var nonFinite = body.Pose.NonFiniteWrites;
                var rigid = body.Pose.RigidWrites;
                float lowest = float.MaxValue, squash = 0;
                for (var frame = 0; frame < Frames600; frame++)
                {
                    visual.Stack.Evaluate(_script[frame], Clock(frame));
                    lowest = Mathf.Min(lowest, Mathf.Min(LowestAsSkeleton(body, body.Pose, scratch), body.LowestPointY()));
                    squash = Mathf.Max(squash, Mathf.Abs(body.Pose.GetScale(CharacterBody.RootBone).Y - 1));
                }
                if (body.Pose.NonFiniteWrites != nonFinite || body.Pose.RigidWrites != rigid || lowest < -0.005f || squash > GaitLayer.MaxSquash + 1e-4f)
                    failures.Add(F($"{kind}: lowest {lowest * 1000:0.00} mm, squash {squash * 100:0.0}%, non-finite {body.Pose.NonFiniteWrites - nonFinite}, rigid {body.Pose.RigidWrites - rigid}"));
                body.Free();
            }
            Check(failures.Count == 0, $"{id} moves under every gait, float, hop, waddle and stride: " + string.Join("; ", failures));
        }
    }

    /// <summary>Climbing or swimming, the gait yields to the contact layer's poses; breathing and blinking go on.</summary>
    private void TestYield()
    {
        foreach (var mode in new[] { "climbing", "swimming" })
        {
            var legAngle = 0f;
            var rootShift = 0f;
            var breath = new List<float>();
            var blinksBefore = 0;
            var (visual, gait, life) = Drive("stickbear", 600, f => new Motion(MotionProfile.WalkSpeedMps, 0, mode == "climbing" && f >= 120, mode == "swimming" && f >= 120), (frame, state, v, g, l) =>
            {
                if (frame == 120) blinksBefore = l.Blinks;
                if (frame < 180) return;
                var pose = v.Body.Pose;
                legAngle = Mathf.Max(legAngle, Mathf.Abs(Angle(pose.GetRotation(g.Rig.Leg[GaitRig.Right]), Vector3.Right)));
                rootShift = Mathf.Max(rootShift, (pose.GetPosition(CharacterBody.RootBone) - pose.RestPosition(CharacterBody.RootBone)).Length());
                breath.Add(pose.GetScale(v.Body.BodyBone).X);
            });
            Check(legAngle < 0.01f && rootShift < 0.0005f, F($"{mode}, Stickbear's gait yields (legs within {Mathf.RadToDeg(legAngle):0.00}°, root within {rootShift * 1000:0.00} mm)"));
            Check(breath.Max() - breath.Min() > 0.015f && life.Blinks > blinksBefore, $"{mode}, he keeps breathing and blinking");
            Check(gait.Cycles < 3, F($"{mode}, the stride phase holds ({gait.Cycles:0.00} cycles, all from the first two seconds)"));
            visual.Body.Free();
        }
    }

    /// <summary>Footfalls alternate feet; a landing and a splash are told with the landing speed.</summary>
    private void TestEvents()
    {
        var (visual, gait, _) = Installed("stickbear");
        var events = new List<GaitEvent>();
        gait.Footfall += (in GaitEvent e) => events.Add(e);
        gait.Landing += (in GaitEvent e) => events.Add(e);
        gait.Splash += (in GaitEvent e) => events.Add(e);
        var walker = new Walker();
        var frame = 0;
        void Feed(BodyState state) => visual.Stack.Evaluate(state, Clock(frame++));
        for (var i = 0; i < 120; i++) Feed(walker.Step(MotionProfile.WalkSpeedMps, 0, Dt));
        var feet = events.Where(e => e.Kind == GaitEventKind.Footfall).ToList();
        var right = gait.Rig.Foot[GaitRig.Right];
        var left = gait.Rig.Foot[GaitRig.Left];
        var alternate = feet.Zip(feet.Skip(1), (a, b) => a.FootBone != b.FootBone).All(x => x);
        Check(feet.Count >= 3 && alternate && feet.All(e => (e.FootBone == right && e.Side == BodySide.Right || e.FootBone == left && e.Side == BodySide.Left) && e.SpeedMps > 0.2f),
            $"Stickbear's footfalls alternate his right and left feet, with the speed ({feet.Count} footfalls)");
        events.Clear();
        var landed = walker.Step(0, 0, Dt);
        Feed(new BodyState { Tick = landed.Tick, Position = landed.Position, Grounded = true, Landed = true, LandingSpeedMps = 1.3f, DistanceM = landed.DistanceM, WaterSurfaceY = float.NaN });
        Feed(new BodyState { Tick = landed.Tick + 1, Position = landed.Position, Swimming = true, Landed = true, LandedInDeepWater = true, LandingSpeedMps = 2.1f, DistanceM = landed.DistanceM, WaterDepthM = 0.2f });
        Feed(new BodyState { Tick = landed.Tick + 2, Position = landed.Position, Swimming = true, DistanceM = landed.DistanceM, WaterDepthM = 0.2f });
        Feed(new BodyState { Tick = landed.Tick + 3, Position = landed.Position, DistanceM = landed.DistanceM, Grounded = true, WaterSurfaceY = float.NaN });
        Feed(new BodyState { Tick = landed.Tick + 4, Position = landed.Position, Swimming = true, Velocity = new Vector3(0, -0.4f, 0), DistanceM = landed.DistanceM, WaterDepthM = 0.2f });
        Check(events.Count == 3 && events[0].Kind == GaitEventKind.Landing && Mathf.IsEqualApprox(events[0].SpeedMps, 1.3f) &&
              events[1].Kind == GaitEventKind.Splash && Mathf.IsEqualApprox(events[1].SpeedMps, 2.1f) && events[1].Wet &&
              events[2].Kind == GaitEventKind.Splash && events[2].SpeedMps >= 0.4f && gait.Landings == 1 && gait.Splashes == 2,
            "a landing is told with its speed; a landing in deep water and wading into a swim splash, each once: " + string.Join(", ", events.Select(e => $"{e.Kind} {e.SpeedMps:0.0}")));
        visual.Body.Free();
        var (hopper, hop, _) = Drive("tomato_man", 120, _ => new Motion(MotionProfile.WalkSpeedMps));
        Check(hop.Footfalls > 0, "a hop lands on both feet: one footfall a hop");
        hopper.Body.Free();
    }

    private void TestBounceKey()
    {
        var (visual, gait, _) = Installed("tomato_man");
        var profile = visual.Body.Profile;
        var hop = (HopGait)gait.Gait;
        var height = hop.HopHeightM(0);
        var order = new[] { BounceKey.Cycle(visual.Body), BounceKey.Cycle(visual.Body), BounceKey.Cycle(visual.Body) };
        Check(order.SequenceEqual(new[] { 1.5f, 0.5f, 1f }) && profile.BounceScale == 1, $"the bounce key cycles 1 -> 1.5 -> 0.5 -> 1 ({string.Join(", ", order)})");
        BounceKey.Cycle(visual);
        Check(Mathf.IsEqualApprox(hop.HopHeightM(0), 1.5f * height) && Mathf.IsEqualApprox(profile.Bounce, 1.5f * 1.1f), "at 1.5 the hops are half as high again");
        Check(BounceKey.Cycle((IBodyVisual?)null) == null && BounceKey.Print((IBodyVisual?)null) == null, "the keys do nothing for the prototype figure");
        var text = BounceKey.Print(visual)!;
        var keys = ProfileMultipliers.Fields.Select(f => f.Key).ToArray();
        Check(keys.All(k => text.Contains("; " + k + " x")) && text.Contains("bounce scale 1.5") && text.Contains("hip ") && text.Contains("cadence") &&
              text.Contains("walk: stride") && text.Contains("run: stride") && text.Contains("spring wand") && text.Contains("hop: height"),
            "the print key prints every value of the profile: " + text);
        visual.Body.Free();
    }
}
