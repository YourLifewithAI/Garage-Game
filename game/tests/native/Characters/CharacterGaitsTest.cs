using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Characters;
using GarageGame.Native.Characters.Gaits;

namespace GarageGame.Tests.Characters;

/// <summary>
/// K3's suite (docs/runs/RUN-2-CHARACTERS.md, acceptance item 1, the gaits' part): every character's gait and life layers
/// run 600 frames at a walk, a run, a stop and a turn from synthetic BodyStates with no non-finite write, the standing
/// pose's lowest point never below -5 mm, the root's volume kept within 2% and its squash within 15%, the phase advanced by
/// distance and never by time, the same frames giving the same pose hash, and no allocation per frame; the gaits' shapes,
/// blinks, breathing and effects, the yield to climbing and swimming, the events, the bounce key; the springs under the
/// squash in the tree; one real SmallPlayerController walking a floor with a drawn body; and the layers' CPU time for
/// both bodies against the plan's 0.4 ms. Headless, --fixed-fps 60. Run: res://tests/native_character_gaits.tscn
/// (add "-- --skip-budget" where wall time means nothing).
/// </summary>
public partial class CharacterGaitsTest : Node3D
{
    private static readonly string[] Shipped = { "cloudpuff", "gubble", "potato_man", "stickbear", "tomato_man" };
    private const float Dt = 1f / 60;
    private const int Frames600 = 600;
    private int _checks;
    private int _failures;
    private CharacterLibrary _library = null!;
    private BodyState[] _script = Array.Empty<BodyState>();
    private readonly List<string> _hashes = new();

    public override async void _Ready()
    {
        try
        {
            _library = CharacterLibrary.Load(CharacterLibrary.ShippedRoot);
            Check(Shipped.All(id => _library.TryGet(id, out _)), "the five shipped characters load");
            _script = Script();
            TestInstaller();
            foreach (var id in Shipped) TestScenario(id);
            foreach (var id in Shipped) TestBounceScales(id);
            TestPhaseByDistance();
            TestStride();
            TestHop();
            TestWaddle();
            TestFloat();
            TestInBubble();
            TestEveryGaitOnEveryBody();
            TestLife();
            TestYield();
            TestEvents();
            TestBounceKey();
            TestCpu();
            await TestSpringsInTree();
            await TestController();
            TestPaceReads();
            TestPaceOnEveryGait();
            await TestPaceOnARealBody();
            await TestMotionBudget();
            await TestComposition();
            GD.Print("GAITS_HASHES: " + string.Join(" ", _hashes));
            GD.Print($"NATIVE_CHARACTER_GAITS: {_checks - _failures}/{_checks} checks passed; gaits and life: five characters at a walk, a run, a stop and a turn, " +
                "phase by distance, volume, footing, determinism, allocation, blinks, breathing, effects, events, the bounce key, a real body walking; not the look or the feel");
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (Exception exception)
        {
            GD.PushError("Native character gaits test exception: " + exception);
            GetTree().Quit(1);
        }
    }

    private void Check(bool condition, string label)
    {
        _checks++;
        if (condition) return;
        _failures++;
        GD.Print("FAIL " + label);
    }

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private Task Frames(int count)
    {
        var source = new TaskCompletionSource();
        var remaining = count;
        void Tick()
        {
            if (--remaining > 0) return;
            GetTree().ProcessFrame -= Tick;
            source.SetResult();
        }
        GetTree().ProcessFrame += Tick;
        return source.Task;
    }

    private CharacterVisual Visual(string id)
    {
        if (!_library.TryGet(id, out var definition)) throw new InvalidOperationException($"{id} did not load");
        return CharacterVisual.Create(definition);
    }

    /// <summary>A visual with K3's layers installed, through the installer the integrator calls.</summary>
    private (CharacterVisual Visual, GaitLayer Gait, LifeLayer Life) Installed(string id)
    {
        var visual = Visual(id);
        CharacterGaits.Install(visual);
        return (visual, CharacterGaits.FindGait(visual.Stack)!, CharacterGaits.FindLife(visual.Stack)!);
    }

    private static PoseClock Clock(int frame, float dt = Dt) => new(frame * (double)dt, dt, frame);

    /// <summary>A body walking on flat ground the way the controller moves one: speed eased at its 6 m/s² toward a target, a turn rate.</summary>
    private struct Walker
    {
        public Vector3 Position;
        public float Yaw;
        public double Distance;
        public float Speed;
        public ulong Tick;

        public BodyState Step(float targetSpeed, float turnRate, float dt, bool climbing = false, bool swimming = false, float airTime = 0, Vector3? extraVelocity = null)
        {
            Speed = Mathf.MoveToward(Speed, targetSpeed, 6 * dt);
            Yaw += turnRate * dt;
            var forward = new Vector3(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
            var velocity = forward * Speed + (extraVelocity ?? Vector3.Zero);
            Position += velocity * dt;
            Distance += Speed * dt;
            Tick++;
            return new BodyState
            {
                Tick = Tick, Position = Position, Yaw = Yaw, Velocity = velocity, SpeedMps = Speed, DistanceM = Distance,
                Grounded = airTime <= 0 && !swimming && !climbing, FloorNormal = Vector3.Up, AirTimeS = airTime, Climbing = climbing,
                ClimbNormal = Vector3.Back, Swimming = swimming, SwimTilt = swimming ? 1 : 0, Floating = false,
                WaterSurfaceY = float.NaN, BodyHeightM = 0.1f, GravityMps2 = 3.5f,
            };
        }
    }

    /// <summary>The acceptance script: 150 frames walking, 150 running, 140 stopping and standing, 160 walking a circle.</summary>
    private static BodyState[] Script()
    {
        var states = new BodyState[Frames600];
        var walker = new Walker();
        for (var frame = 0; frame < Frames600; frame++)
        {
            var (speed, turn) = frame switch
            {
                < 150 => (MotionProfile.WalkSpeedMps, 0f),
                < 300 => (MotionProfile.RunSpeedMps, 0f),
                < 440 => (0f, 0f),
                _ => (MotionProfile.WalkSpeedMps, 2.5f),
            };
            states[frame] = walker.Step(speed, turn, Dt);
        }
        return states;
    }

    /// <summary>
    /// The lowest mesh corner under a pose, composing each bone as the skeleton does (rotation then scale in the bone's own
    /// frame, Skeleton3D's set_quaternion_scale): the truth on screen. PoseBuffer.ModelTransform composes scale in the parent's
    /// frame instead, which differs where a bone both turns and squashes (see TestComposition).
    /// </summary>
    private static float LowestAsSkeleton(CharacterBody body, PoseBuffer pose, Transform3D[] scratch)
    {
        var lowest = float.MaxValue;
        for (var i = 0; i < body.BoneCount; i++)
        {
            var local = new Transform3D(new Basis(pose.GetRotation(i)) * Basis.FromScale(pose.GetScale(i)), pose.GetPosition(i));
            scratch[i] = pose.Parent(i) < 0 ? local : scratch[pose.Parent(i)] * local;
            var bone = body.Bones[i];
            if (!bone.HasMesh) continue;
            for (var corner = 0; corner < 8; corner++)
            {
                var point = bone.MeshBounds.Position + new Vector3((corner & 1) != 0 ? bone.MeshBounds.Size.X : 0,
                    (corner & 2) != 0 ? bone.MeshBounds.Size.Y : 0, (corner & 4) != 0 ? bone.MeshBounds.Size.Z : 0);
                lowest = Mathf.Min(lowest, (scratch[i] * point).Y);
            }
        }
        return lowest == float.MaxValue ? 0 : lowest;
    }

    private void TestInstaller()
    {
        var visual = Visual("stickbear");
        CharacterGaits.Install(visual);
        var count = visual.Stack.Count;
        CharacterGaits.Install(visual);
        var gait = CharacterGaits.FindGait(visual.Stack);
        var life = CharacterGaits.FindLife(visual.Stack);
        Check(count == 2 && visual.Stack.Count == 2 && gait != null && life != null && visual.Stack[0] == gait && visual.Stack[1] == life,
            "the installer adds the gait (layer 1) and the life layer (layer 3) once");
        Check(gait!.Gait is StrideGait && gait.Layer == PoseLayer.Gait && life!.Layer == PoseLayer.Life, "Stickbear strides");
        var kinds = Shipped.ToDictionary(id => id, id => { var (v, g, _) = Installed(id); var kind = g.Gait.Kind; v.Body.Free(); return kind; });
        Check(kinds["gubble"] == MotionKind.Float && kinds["cloudpuff"] == MotionKind.Hop && kinds["tomato_man"] == MotionKind.Hop &&
              kinds["potato_man"] == MotionKind.Waddle && kinds["stickbear"] == MotionKind.Stride, "each character gets the gait its motion profile names");
        Check(CharacterGaits.SeedFor(visual.Body) == GaitMath.Hash("stickbear") && GaitMath.Hash("stickbear") != GaitMath.Hash("gubble"),
            "the blink seed is a stable hash of the character's id");
        visual.Body.Free();
    }

    /// <summary>The acceptance run for one character: 600 frames at a walk, a run, a stop and a turn.</summary>
    private void TestScenario(string id)
    {
        var (a, gaitA, _) = Installed(id);
        var (b, gaitB, _) = Installed(id);
        var body = a.Body;
        var pose = body.Pose;
        var scratch = new Transform3D[body.BoneCount];
        var hashes = new ulong[Frames600];
        var nonFinite = pose.NonFiniteWrites;
        var rigid = pose.RigidWrites;
        float lowest = float.MaxValue, lowestK1 = float.MaxValue, worstVolume = 0, worstSquash = 0;
        var stillCycles = double.NaN;
        var stillFootfalls = 0;
        var stillSteps = false;
        var cyclesAtWalk = 0.0;
        var footfallsAtWalk = 0;
        for (var frame = 0; frame < Frames600; frame++)
        {
            a.Stack.Evaluate(_script[frame], Clock(frame));
            hashes[frame] = pose.Hash();
            lowest = Mathf.Min(lowest, LowestAsSkeleton(body, pose, scratch));
            lowestK1 = Mathf.Min(lowestK1, body.LowestPointY(pose));
            var scale = pose.GetScale(CharacterBody.RootBone);
            worstVolume = Mathf.Max(worstVolume, Mathf.Abs(scale.X * scale.Y * scale.Z - 1));
            worstSquash = Mathf.Max(worstSquash, Mathf.Max(Mathf.Abs(scale.Y - 1), Mathf.Max(Mathf.Abs(scale.X - 1), Mathf.Abs(scale.Z - 1))));
            if (frame == 149) { cyclesAtWalk = gaitA.Cycles; footfallsAtWalk = gaitA.Footfalls; }
            // The stop: the speed is zero from about frame 310; the last 120 frames of it stand still.
            if (frame == 319) { stillCycles = gaitA.Cycles; stillFootfalls = gaitA.Footfalls; }
            if (frame > 319 && frame < 440 && (gaitA.Cycles != stillCycles || gaitA.Footfalls != stillFootfalls)) stillSteps = true;
        }
        var same = true;
        for (var frame = 0; frame < Frames600; frame++)
        {
            b.Stack.Evaluate(_script[frame], Clock(frame));
            same &= b.Body.Pose.Hash() == hashes[frame];
        }
        var expectedWalk = _script[149].DistanceM / body.Profile.StrideAt(MotionProfile.WalkSpeedMps);
        Check(pose.NonFiniteWrites == nonFinite && pose.RigidWrites == rigid, $"{id}: 600 frames write no non-finite value and nothing to a rigid part");
        Check(lowest >= -0.005f && lowestK1 >= -0.005f, F($"{id}: the standing pose's lowest point never goes below -5 mm ({lowest * 1000:0.00} mm as the skeleton composes, {lowestK1 * 1000:0.00} mm by PoseBuffer)"));
        Check(worstVolume <= 0.02f, F($"{id}: the root's squash and stretch keep its volume within 2% ({worstVolume * 100:0.000}%)"));
        Check(worstSquash <= GaitLayer.MaxSquash + 1e-4f, F($"{id}: the root never squashes or stretches more than 15% ({worstSquash * 100:0.0}%)"));
        Check(!stillSteps, $"{id}: standing still for 120 frames takes no step (the phase and the footfalls hold)");
        Check(Math.Abs(cyclesAtWalk / expectedWalk - 1) < 0.15, F($"{id}: walking {_script[149].DistanceM:0.000} m advances {cyclesAtWalk:0.000} stride cycles (distance over the walk stride: {expectedWalk:0.000})"));
        var steps = gaitA.Gait.StepsPerCycle;
        Check(steps == 0 ? gaitA.Footfalls == 0 : footfallsAtWalk >= (int)(cyclesAtWalk * steps) - 1 && footfallsAtWalk <= (int)(cyclesAtWalk * steps) + 1,
            F($"{id}: {footfallsAtWalk} footfalls while walking {cyclesAtWalk:0.00} cycles of {steps} steps"));
        Check(same, $"{id}: a second body fed the same frames gives the same pose hash on every frame");
        Check(hashes.Distinct().Count() > 500, $"{id}: the pose changes from frame to frame ({hashes.Distinct().Count()} distinct of 600)");

        // No allocation per frame: the same 600 frames again on a third body, after one warm frame.
        var (c, _, _) = Installed(id);
        c.Stack.Evaluate(_script[0], Clock(0));
        ulong sink = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 1; frame < Frames600; frame++)
        {
            c.Stack.Evaluate(_script[frame], Clock(frame));
            sink ^= c.Body.Pose.Hash();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0 && sink != 0, $"{id}: the gait and life layers allocate nothing per frame ({allocated} bytes over 599 frames)");
        _hashes.Add($"{id}={hashes[^1]:x16}");
        foreach (var visual in new[] { a, b, c }) visual.Body.Free();
    }

    /// <summary>The bounce key's other two settings: still finite, above the floor, within the squash limit, volume kept.</summary>
    private void TestBounceScales(string id)
    {
        foreach (var bounce in new[] { 0.5f, 1.5f })
        {
            var (visual, _, _) = Installed(id);
            var body = visual.Body;
            body.Profile.BounceScale = bounce;
            var scratch = new Transform3D[body.BoneCount];
            float lowest = float.MaxValue, worstVolume = 0, worstSquash = 0;
            var nonFinite = body.Pose.NonFiniteWrites;
            for (var frame = 0; frame < Frames600; frame++)
            {
                visual.Stack.Evaluate(_script[frame], Clock(frame));
                lowest = Mathf.Min(lowest, Mathf.Min(LowestAsSkeleton(body, body.Pose, scratch), body.LowestPointY()));
                var scale = body.Pose.GetScale(CharacterBody.RootBone);
                worstVolume = Mathf.Max(worstVolume, Mathf.Abs(scale.X * scale.Y * scale.Z - 1));
                worstSquash = Mathf.Max(worstSquash, Mathf.Abs(scale.Y - 1));
            }
            Check(body.Pose.NonFiniteWrites == nonFinite && lowest >= -0.005f && worstVolume <= 0.02f && worstSquash <= GaitLayer.MaxSquash + 1e-4f,
                F($"{id} at bounce x{bounce}: finite, lowest {lowest * 1000:0.00} mm, volume within {worstVolume * 100:0.000}%, squash {worstSquash * 100:0.0}%"));
            body.Free();
        }
    }
}
