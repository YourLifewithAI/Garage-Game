using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native.Characters;

namespace GarageGame.Tests.Characters;

/// <summary>The pose API, the skeleton in the tree, and the engine modifiers the rig sets up.</summary>
public partial class CharacterRigTest
{
    /// <summary>A stand-in for the controller's snapshot: what the test layers read.</summary>
    private struct TestState
    {
        public float Speed;
        public float Distance;
    }

    /// <summary>A gait-like layer: sways the body, bobs and squashes the root, swings legs and arms, all phased by distance.</summary>
    private sealed class SwayLayer : IPoseLayer<TestState>
    {
        private readonly int _body, _legRight, _legLeft, _armRight, _armLeft;
        private readonly float _stride;
        public SwayLayer(CharacterBody body)
        {
            _body = body.BodyBone;
            _legRight = body.FindBone(PartRole.Leg, BodySide.Right);
            _legLeft = body.FindBone(PartRole.Leg, BodySide.Left);
            _armRight = body.FindBone(PartRole.Arm, BodySide.Right);
            _armLeft = body.FindBone(PartRole.Arm, BodySide.Left);
            _stride = body.Profile.StrideAt(MotionProfile.WalkSpeedMps);
        }
        public PoseLayer Layer => PoseLayer.Gait;
        public void Apply(PoseBuffer pose, in TestState state, in PoseClock clock)
        {
            var phase = Mathf.Tau * state.Distance / _stride;
            pose.AddRotation(_body, new Quaternion(Vector3.Back, 0.12f * Mathf.Sin(phase)));
            pose.AddPosition(CharacterBody.RootBone, new Vector3(0, 0.003f * Mathf.Abs(Mathf.Sin(phase)), 0));
            var squash = 1 - 0.05f * Mathf.Abs(Mathf.Cos(phase));
            pose.MultiplyScale(CharacterBody.RootBone, new Vector3(1 / Mathf.Sqrt(squash), squash, 1 / Mathf.Sqrt(squash)));
            if (_legRight > 0) pose.SetRotation(_legRight, new Quaternion(Vector3.Right, 0.4f * Mathf.Sin(phase)));
            if (_legLeft > 0) pose.SetRotation(_legLeft, new Quaternion(Vector3.Right, -0.4f * Mathf.Sin(phase)));
            if (_armRight > 0) pose.SetRotation(_armRight, new Quaternion(Vector3.Right, -0.3f * Mathf.Sin(phase)));
            if (_armLeft > 0) pose.SetRotation(_armLeft, new Quaternion(Vector3.Right, 0.3f * Mathf.Sin(phase)));
        }
    }

    /// <summary>A life-like layer: a breath on the body's scale.</summary>
    private sealed class BreathLayer : IPoseLayer<TestState>
    {
        private readonly int _body;
        public BreathLayer(CharacterBody body) => _body = body.BodyBone;
        public PoseLayer Layer => PoseLayer.Life;
        public void Apply(PoseBuffer pose, in TestState state, in PoseClock clock) =>
            pose.MultiplyScale(_body, Vector3.One * (1 + 0.01f * Mathf.Sin((float)(Mathf.Tau * clock.TimeS / 3.5))));
    }

    /// <summary>Records the order layers run in.</summary>
    private sealed class OrderLayer : IPoseLayer<TestState>
    {
        private readonly List<PoseLayer> _log;
        public OrderLayer(PoseLayer layer, List<PoseLayer> log) { Layer = layer; _log = log; }
        public PoseLayer Layer { get; }
        public void Apply(PoseBuffer pose, in TestState state, in PoseClock clock) => _log.Add(Layer);
    }

    private void TestPoseBuffer()
    {
        var body = Build("stickbear");
        var pose = body.Pose;
        var leg = body.FindBone(PartRole.Leg, BodySide.Right);
        Check(leg == body.FindBone("leg_screen_left") && body.FindBone(PartRole.Leg, BodySide.Left) == body.FindBone("leg_screen_right") &&
              body.FindBone(PartRole.Digit, BodySide.Right, 7) == body.FindBone("claw_left_4") && body.FindBone(PartRole.Digit, BodySide.Right, 8) == -1 &&
              body.CountBones(PartRole.Digit) == 17 && body.HeadBone == body.FindBone("head"), "bones are found by role and side (the character's right is +X)");
        Span<int> paws = stackalloc int[4];
        Check(body.GetBones(PartRole.Paw, paws) == 2 && paws[0] == body.FindBone("paw_screen_left"), "bones by role fill a caller's span");

        pose.Reset();
        var rest = pose.Hash();
        pose.Begin(PoseLayer.Gait);
        pose.SetRotation(leg, new Quaternion(Vector3.Right, 0.3f));
        pose.AddRotation(leg, new Quaternion(Vector3.Right, 0.2f));
        Check(pose.GetRotation(leg).IsEqualApprox(new Quaternion(Vector3.Right, 0.5f)), "AddRotation composes about the joint");
        pose.AddPosition(CharacterBody.RootBone, new Vector3(0, 0.01f, 0));
        var foot = body.FindBone(PartRole.Foot, BodySide.Right);
        var footModel = pose.ModelTransform(foot).Origin;
        var hip = body.Bones[leg].RestModel + new Vector3(0, 0.01f, 0);
        var expected = hip + new Basis(Vector3.Right, 0.5f) * (body.Bones[foot].RestModel - body.Bones[leg].RestModel);
        Check(footModel.IsEqualApprox(expected), "the model-space transform follows the parents");
        pose.Begin(PoseLayer.Contact);
        var threw = false;
        try { pose.Begin(PoseLayer.Gait); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "a layer cannot run after one it must precede");
        var nonFinite = pose.NonFiniteWrites;
        pose.SetRotation(leg, new Quaternion(float.NaN, 0, 0, 1));
        pose.AddPosition(leg, new Vector3(float.PositiveInfinity, 0, 0));
        pose.SetScale(leg, new Vector3(0, 1, 1));
        Check(pose.NonFiniteWrites == nonFinite + 3 && pose.GetRotation(leg).IsEqualApprox(new Quaternion(Vector3.Right, 0.5f)), "non-finite or degenerate writes are refused and counted");
        var moved = pose.Hash();
        pose.Reset();
        Check(pose.Hash() == rest && moved != rest && pose.Layer == PoseLayer.Rest, "a reset returns to the rest pose's hash");

        var log = new List<PoseLayer>();
        var stack = new PoseStack<TestState>(body);
        foreach (var layer in new[] { PoseLayer.Gesture, PoseLayer.Life, PoseLayer.Gait, PoseLayer.Tumble, PoseLayer.Contact, PoseLayer.Gait })
            stack.Add(new OrderLayer(layer, log));
        stack.Evaluate(new TestState(), new PoseClock(0, 1f / 60, 0));
        Check(log.SequenceEqual(new[] { PoseLayer.Gait, PoseLayer.Gait, PoseLayer.Contact, PoseLayer.Life, PoseLayer.Gesture, PoseLayer.Tumble }), "the stack runs its layers in the fixed order");
        log.Clear();
        foreach (var layer in new[] { PoseLayer.Gait, PoseLayer.Contact, PoseLayer.Life }) stack.Suspend(layer, true);
        stack.Evaluate(new TestState(), new PoseClock(0, 1f / 60, 1));
        Check(log.SequenceEqual(new[] { PoseLayer.Gesture, PoseLayer.Tumble }), "a tumble can hold layers 1 to 3");
        var refused = false;
        try { stack.Add(new OrderLayer(PoseLayer.Secondary, log)); } catch (ArgumentException) { refused = true; }
        Check(refused, "no pose layer may claim the skeleton's own modifiers");

        // Allocation-free per frame: 600 frames of two layers and the hash allocate nothing.
        var sway = new PoseStack<TestState>(body);
        sway.Add(new SwayLayer(body));
        sway.Add(new BreathLayer(body));
        var state = new TestState { Speed = MotionProfile.WalkSpeedMps };
        sway.Evaluate(state, new PoseClock(0, 1f / 60, 0));
        var before = GC.GetAllocatedBytesForCurrentThread();
        ulong sink = 0;
        for (var frame = 1; frame <= 600; frame++)
        {
            state.Distance += state.Speed / 60;
            sway.Evaluate(state, new PoseClock(frame / 60.0, 1f / 60, frame));
            sink ^= body.Pose.Hash();
            sink ^= (ulong)body.FindBone(PartRole.Leg, BodySide.Left);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0 && sink != 0, $"evaluating the stack, finding bones and hashing allocate nothing per frame ({allocated} bytes over 600 frames)");
        Check(body.Pose.NonFiniteWrites == nonFinite + 3 && body.Pose.RigidWrites == 0, "the test gait wrote only finite values to free bones");
        body.Free();
    }

    private async Task TestRestInTheTree()
    {
        var holder = new Node3D { Name = "RestHolder", Position = new Vector3(5, 0, 0) };
        AddChild(holder);
        var bodies = Shipped.Select(Build).ToList();
        foreach (var body in bodies) holder.AddChild(body);
        await Frames(3);
        foreach (var body in bodies)
        {
            var worst = 0f;
            foreach (var bone in body.Bones) worst = Mathf.Max(worst, (body.FinalBoneTransform(bone.Index).Origin - bone.RestModel).Length());
            Check(worst < 1e-5f, FormattableString.Invariant($"{body.CharacterId}: in the tree, at rest, every part sits at its pivot (worst {worst:E2} m)"));
            Check(Mathf.Abs(body.FinalLowestPointY()) < 1e-4f, FormattableString.Invariant($"{body.CharacterId}: in the tree, the feet stand at y=0 ({body.FinalLowestPointY():0.000000})"));
            var mesh = body.PartMesh(body.BodyBone)!;
            var world = mesh.GlobalTransform * mesh.Mesh.GetAabb();
            Check(Mathf.Abs(world.Position.X - (5 + body.Bones[body.BodyBone].RestModel.X + body.Bones[body.BodyBone].MeshBounds.Position.X)) < 1e-5f,
                $"{body.CharacterId}: the body's mesh rides its bone to its place in the world");
        }
        holder.QueueFree();
        await Frames(1);
    }

    /// <summary>Two bodies of each character fed the same frames give the same pose hashes, before and after the modifiers.</summary>
    private async Task TestDeterministicHashes()
    {
        var holder = new Node3D { Name = "HashHolder", Position = new Vector3(10, 0, 0) };
        AddChild(holder);
        var pairs = new List<(CharacterBody A, CharacterBody B, PoseStack<TestState> StackA, PoseStack<TestState> StackB)>();
        foreach (var id in Shipped)
        {
            var a = Build(id);
            var b = Build(id);
            holder.AddChild(a);
            holder.AddChild(b);
            var stackA = new PoseStack<TestState>(a);
            var stackB = new PoseStack<TestState>(b);
            stackA.Add(new SwayLayer(a)); stackA.Add(new BreathLayer(a));
            stackB.Add(new SwayLayer(b)); stackB.Add(new BreathLayer(b));
            a.Look.SetActive(true, 0.5f);
            b.Look.SetActive(true, 0.5f);
            pairs.Add((a, b, stackA, stackB));
        }
        var state = new TestState();
        var poseSame = true;
        var finalSame = true;
        var finalMoved = true;
        var finite = true;
        var hashes = new ulong[pairs.Count];
        for (var frame = 0; frame < 240; frame++)
        {
            state.Speed = frame < 120 ? MotionProfile.WalkSpeedMps : frame < 200 ? MotionProfile.RunSpeedMps : 0;
            state.Distance += state.Speed / 60;
            var clock = new PoseClock(frame / 60.0, 1f / 60, frame);
            for (var i = 0; i < pairs.Count; i++)
            {
                var (a, b, stackA, stackB) = pairs[i];
                a.Look.Target.Position = new Vector3(0.3f * Mathf.Sin(frame * 0.05f), 0.08f, -0.5f);
                b.Look.Target.Position = a.Look.Target.Position;
                stackA.Run(state, clock);
                stackB.Run(state, clock);
                poseSame &= a.Pose.Hash() == b.Pose.Hash();
                var finalA = a.FinalPoseHash();
                if (finalA != b.FinalPoseHash()) finalSame = false;
                hashes[i] ^= finalA;
                finite &= a.Bones.All(bone => float.IsFinite(a.FinalBoneTransform(bone.Index).Origin.Length()));
            }
            await Frames(1);
        }
        foreach (var (a, _, _, _) in pairs)
            finalMoved &= a.FinalPoseHash() != PoseHashAtRest(a);
        Check(poseSame, "the same frames give the same pose hash for two bodies of each character");
        Check(finalSame, "the same frames give the same final hash after springs and look-at");
        Check(finite, "240 frames of walk, run and stop leave every bone finite");
        Check(finalMoved, "the final poses moved away from rest (the layers reached the skeleton)");
        Check(pairs.All(p => p.A.Secondary.TunedDeltaS > 0.016f && p.A.Secondary.TunedDeltaS < 0.0175f || p.A.Secondary.Chains.Count == 0),
            "the springs are tuned to the 60 Hz frame");
        GD.Print("CHARACTERS_HASHES: " + string.Join(" ", pairs.Select((p, i) => $"{p.A.CharacterId}={hashes[i]:x16}")));
        holder.QueueFree();
        await Frames(1);
    }

    private static ulong PoseHashAtRest(CharacterBody body)
    {
        var hash = PoseHash.Seed;
        foreach (var bone in body.Bones) PoseHash.Mix(ref hash, new Transform3D(Basis.Identity, bone.RestModel));
        return hash;
    }

    /// <summary>The engine's spring swings a chain near the frequency the rig tuned it to.</summary>
    private async Task TestSpringFrequency()
    {
        var (measured, peak, tail, chain) = await SwingLeg("SpringHolder", new Vector3(15, 0, 0), 1f / 60);
        _legSwingHz = measured;
        Check(Mathf.Abs(measured / chain.FrequencyHz - 1) < 0.2f,
            FormattableString.Invariant($"Cloudpuff's dangling leg swings at {measured:0.00} Hz, tuned for {chain.FrequencyHz:0.00} Hz (its {chain.LengthM * 100:0.0} cm pendulum's {chain.PendulumHz:0.00} Hz x 1.61)"));
        Check(peak > 0.05f && tail < 0.3f * peak, FormattableString.Invariant($"the leg swings when the body jerks and settles ({Mathf.RadToDeg(peak):0.0}° to {Mathf.RadToDeg(tail):0.0}°)"));
    }

    private float _legSwingHz;

    /// <summary>
    /// Review major 2: the springs step on the physics tick, the Δt PoseStack.Run tunes them to (the body's ticks run the stack),
    /// never on the render frame. With the physics tick at 30 Hz under the suite's 60 Hz frames (a 144 Hz display draws 2.4
    /// frames a 60 Hz tick), Cloudpuff's leg, tuned to the 30 Hz tick, still swings at its tuned frequency and as it does at 60 Hz;
    /// on render frames its stiffness and drag would act twice a tick and it would swing about √2 times faster.
    /// </summary>
    private async Task TestSpringsOnThePhysicsTick()
    {
        var probe = Build("cloudpuff");
        var plain = new Skeleton3D();
        Check(probe.Skeleton.ModifierCallbackModeProcess == Skeleton3D.ModifierCallbackModeProcessEnum.Physics,
            $"the rig's modifiers (springs, look-ats, leg IK) run on the physics tick (the engine's default is {plain.ModifierCallbackModeProcess})");
        plain.Free();
        probe.Free();
        var ticks = Engine.PhysicsTicksPerSecond;
        Engine.PhysicsTicksPerSecond = 30;
        (float Hz, float Peak, float Tail, SpringChain Chain) slow;
        try
        {
            await Frames(2);
            slow = await SwingLeg("SpringHolder30", new Vector3(18, 0, 0), 1f / 30);
        }
        finally
        {
            Engine.PhysicsTicksPerSecond = ticks;
        }
        await Frames(2);
        GD.Print(FormattableString.Invariant($"CHARACTERS_SPRING_TICK: Cloudpuff's leg swings at {_legSwingHz:0.00} Hz with 60 Hz ticks and {slow.Hz:0.00} Hz with 30 Hz ticks under 60 Hz frames (tuned {slow.Chain.FrequencyHz:0.00} Hz)"));
        Check(Mathf.Abs(slow.Hz / slow.Chain.FrequencyHz - 1) < 0.2f && Mathf.Abs(slow.Hz / _legSwingHz - 1) < 0.12f && slow.Peak > 0.05f && slow.Tail < 0.3f * slow.Peak,
            FormattableString.Invariant($"with the physics tick at 30 Hz under 60 Hz frames, the leg tuned to the tick swings at {slow.Hz:0.00} Hz (tuned {slow.Chain.FrequencyHz:0.00} Hz; {_legSwingHz:0.00} Hz at 60 Hz) and settles ({Mathf.RadToDeg(slow.Peak):0.0}° to {Mathf.RadToDeg(slow.Tail):0.0}°)"));
    }

    /// <summary>
    /// Cloudpuff jerked forward 12 mm and left: its dangling leg's swing frequency (zero crossings over 300 frames of 1/60 s), its
    /// largest swing and its swing over the last second, with the springs tuned to <paramref name="tunedFor"/> (the tick's Δt).
    /// </summary>
    private async Task<(float Hz, float Peak, float Tail, SpringChain Chain)> SwingLeg(string name, Vector3 at, float tunedFor)
    {
        var holder = new Node3D { Name = name, Position = at };
        AddChild(holder);
        var body = Build("cloudpuff");
        holder.AddChild(body);
        body.Secondary.MatchTimestep(tunedFor);
        var chain = body.Secondary.Chains[0];
        var hip = chain.Bones[0];
        var foot = chain.Bones[1];
        float Angle()
        {
            var d = body.FinalBoneTransform(foot).Origin - body.FinalBoneTransform(hip).Origin;
            return Mathf.Atan2(d.Z, -d.Y);
        }
        await Frames(20);
        var rest = Angle();
        var samples = new List<float>();
        for (var frame = 0; frame < 300; frame++)
        {
            if (frame < 3) holder.Position += new Vector3(0, 0, -0.004f);
            await Frames(1);
            samples.Add(Angle() - rest);
        }
        var crossings = new List<int>();
        for (var i = 6; i < samples.Count; i++)
            if (Mathf.Sign(samples[i]) != Mathf.Sign(samples[i - 1]) && Mathf.Abs(samples[i] - samples[i - 1]) > 1e-4f) crossings.Add(i);
        var measured = crossings.Count >= 3 ? (crossings.Count - 1) / 2f / ((crossings[^1] - crossings[0]) / 60f) : 0;
        var peak = samples.Max(Mathf.Abs);
        var tail = samples.Skip(240).Max(Mathf.Abs);
        holder.QueueFree();
        await Frames(1);
        return (measured, peak, tail, chain);
    }

    /// <summary>The leg IK, once active, puts the sole on its target; off again, the foot goes back to rest.</summary>
    private async Task TestLegIk()
    {
        var holder = new Node3D { Name = "IkHolder", Position = new Vector3(20, 0, 0) };
        AddChild(holder);
        foreach (var id in new[] { "stickbear", "cloudpuff" })
        {
            var body = Build(id);
            holder.AddChild(body);
            await Frames(2);
            var worst = 0f;
            foreach (var leg in body.Legs)
            {
                var goal = leg.RestSole + new Vector3(0.002f, 0.006f, -0.004f);
                leg.Target.Position = goal;
                body.UseLegIk(true);
                await Frames(3);
                var sole = body.FinalBoneTransform(leg.FootBone) * leg.SoleOffset;
                worst = Mathf.Max(worst, sole.DistanceTo(goal));
                leg.Target.Position = leg.RestSole;
            }
            Check(worst < 0.0005f, FormattableString.Invariant($"{id}: an active leg IK puts the sole on its target (worst {worst * 1000:0.00} mm)"));
            body.UseLegIk(false);
            await Frames(3);
            var back = body.Legs.Max(l => (body.FinalBoneTransform(l.FootBone).Origin - body.Bones[l.FootBone].RestModel).Length());
            Check(back < 1e-5f, $"{id}: switched off, the IK leaves the feet at rest");
        }
        holder.QueueFree();
        await Frames(1);
    }

    /// <summary>The head turns toward the target, within its limit; the eyes within theirs.</summary>
    private async Task TestLookAt()
    {
        var holder = new Node3D { Name = "LookHolder", Position = new Vector3(25, 0, 0) };
        AddChild(holder);
        var body = Build("stickbear");
        holder.AddChild(body);
        var head = body.Look.Head!;
        var headRest = body.Bones[head.Bone].RestModel;
        float Yaw(int bone)
        {
            var forward = -body.FinalBoneTransform(bone).Basis.Z;
            return Mathf.Atan2(forward.X, -forward.Z);
        }
        body.Look.Target.Position = headRest + new Vector3(0.3f, 0, -0.3f);
        body.Look.SetActive(true);
        await Frames(30);
        var toward = Yaw(head.Bone);
        Check(Mathf.Abs(toward - Mathf.Pi / 4) < Mathf.DegToRad(4), FormattableString.Invariant($"Stickbear's head turns 45° toward a target (turned {Mathf.RadToDeg(toward):0.0}°)"));
        body.Look.Target.Position = headRest + new Vector3(0.5f, 0, 0.05f);
        await Frames(30);
        var limited = Yaw(head.Bone);
        Check(limited > Mathf.DegToRad(20) && limited < head.YawLimitRad + Mathf.DegToRad(3),
            FormattableString.Invariant($"a target behind the shoulder turns the head no farther than its {Mathf.RadToDeg(head.YawLimitRad):0}° limit ({Mathf.RadToDeg(limited):0.0}°)"));
        var eye = body.Look.Eyes[0];
        var eyeRelative = (body.FinalBoneTransform(head.Bone).Basis.Inverse() * body.FinalBoneTransform(eye.Bone).Basis).GetEuler();
        Check(Mathf.Abs(eyeRelative.Y) <= eye.YawLimitRad + Mathf.DegToRad(3), FormattableString.Invariant($"the eyes stay within {Mathf.RadToDeg(eye.YawLimitRad):0}° of the head ({Mathf.RadToDeg(eyeRelative.Y):0.0}°)"));
        body.Look.SetActive(false);
        await Frames(3);
        Check(Mathf.Abs(Yaw(head.Bone)) < 1e-3f, "switched off, the head faces -Z again");
        holder.QueueFree();
        await Frames(1);
    }
}
