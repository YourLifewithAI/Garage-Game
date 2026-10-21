using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Characters;
using GarageGame.Native.Characters.Gaits;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Characters;

/// <summary>The budget, a real body walking a floor, and how bones compose.</summary>
public partial class CharacterGaitsTest
{
    /// <summary>
    /// The gait and life layers' CPU time for both bodies (the player and the Gubble), synthetic frames: reported, not the budget's
    /// check (TestMotionBudget measures every layer CharacterMotion installs, on real bodies).
    /// </summary>
    private void TestCpu()
    {
        double Measure(IReadOnlyList<CharacterVisual> visuals, bool run)
        {
            var best = double.MaxValue;
            for (var round = 0; round < 5; round++)
            {
                var clock = Stopwatch.StartNew();
                for (var frame = 0; frame < Frames600; frame++)
                    foreach (var visual in visuals)
                    {
                        if (run) visual.Stack.Run(_script[frame], Clock(frame + round * Frames600));
                        else visual.Stack.Evaluate(_script[frame], Clock(frame + round * Frames600));
                    }
                best = Math.Min(best, clock.Elapsed.TotalMilliseconds / Frames600);
            }
            return best;
        }
        var both = new[] { Installed("stickbear").Visual, Installed("gubble").Visual };
        Measure(both, false);
        var layers = Measure(both, false);
        var withSkeleton = Measure(both, true);
        var all = Shipped.Select(id => Installed(id).Visual).ToArray();
        Measure(all, false);
        var five = Measure(all, false);
        GD.Print(F($"GAITS_CPU: the gait and life layers alone for both bodies (Stickbear, the most bones, and the Gubble): {layers * 1000:0.0} µs a frame; with the pose handed to the skeletons and the springs kept tuned: {withSkeleton * 1000:0.0} µs; all five characters: {five * 1000:0.0} µs (best of five rounds of 600 frames, headless, Debug build)"));
        foreach (var visual in both.Concat(all)) visual.Body.Free();
    }

    /// <summary>Times a drawn body's whole tick: every layer of its stack, the pose handed to the skeleton, the springs kept tuned.</summary>
    private sealed class TimedVisual : IBodyVisual
    {
        public required CharacterVisual Inner { get; init; }
        public readonly List<double> TickMs = new();
        public bool Timing;
        public Node3D Root => Inner.Root;
        public void BodyTicked(in BodyState state, float delta)
        {
            var start = Stopwatch.GetTimestamp();
            Inner.BodyTicked(state, delta);
            if (Timing) TickMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Review minor 10: the plan's 0.4 ms is both bodies' layers, so the check runs every layer CharacterMotion installs (the gaits
    /// and life, contact with its rays and look targets, the tumble's watch) on real bodies as the room dresses them: Stickbear
    /// (the most bones) walking, running, stopping and turning on a floor, and a floating Gubble beside him, each tick timed whole
    /// (the stack, the pose handed to the skeleton, the springs tuned). The engine's own modifier pass is not in it. "--skip-budget"
    /// reports without checking, where wall time means nothing.
    /// </summary>
    private async Task TestMotionBudget()
    {
        var fixture = new Node3D { Name = "BudgetFixture", Position = new Vector3(-40, 0, 20) };
        AddChild(fixture);
        var floor = new StaticBody3D { Position = new Vector3(0, -0.05f, 0), CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(8, 0.1f, 8) } });
        fixture.AddChild(floor);
        await Frames(2);
        TimedVisual Dress(string id, SmallPlayerController body)
        {
            var visual = Visual(id);
            CharacterMotion.Install(visual, body);
            var timed = new TimedVisual { Inner = visual };
            body.SetBodyVisual(timed);
            fixture.AddChild(body);
            return timed;
        }
        var walker = new SmallPlayerController { Name = "BudgetWalker", ReadKeyboard = false, Position = new Vector3(0, 0.01f, 1.5f) };
        var floater = new SmallPlayerController { Name = "BudgetFloater", ReadKeyboard = false, Floats = true, Position = new Vector3(0.3f, 0.03f, 1.5f) };
        var player = Dress("stickbear", walker);
        var gubble = Dress("gubble", floater);
        await Frames(30);
        player.Timing = gubble.Timing = true;
        async Task Go(int frames, Vector2 input, bool sprint = false, float turn = 0)
        {
            walker.SetControlInput(input, sprint: sprint);
            floater.SetControlInput(input, sprint: sprint);
            for (var i = 0; i < frames; i++)
            {
                if (turn != 0) walker.SetMovementFrame((walker.MovementFrameYaw ?? walker.GlobalRotation.Y) + turn * Dt);
                await Frames(1);
            }
        }
        await Go(150, new Vector2(0, 1));
        await Go(150, new Vector2(0, 1), sprint: true);
        await Go(140, Vector2.Zero);
        await Go(160, new Vector2(0, 1), turn: 2.5f);
        walker.SetMovementFrame(null);
        walker.SetControlInput(Vector2.Zero);
        floater.SetControlInput(Vector2.Zero);
        player.Timing = gubble.Timing = false;
        var ticks = Math.Min(player.TickMs.Count, gubble.TickMs.Count);
        var both = Enumerable.Range(0, ticks).Select(i => player.TickMs[i] + gubble.TickMs[i]).OrderBy(ms => ms).ToList();
        var median = ticks > 0 ? both[ticks / 2] : double.NaN;
        var p95 = ticks > 0 ? both[(int)(ticks * 0.95)] : double.NaN;
        var layers = string.Join(", ", player.Inner.Stack.Count > 0 ? Enumerable.Range(0, player.Inner.Stack.Count).Select(i => player.Inner.Stack[i].GetType().Name) : Array.Empty<string>());
        GD.Print(F($"GAITS_BUDGET: every layer CharacterMotion installs ({layers}) on both real bodies, Stickbear walking, running, stopping and turning and the Gubble floating: median {median * 1000:0.0} µs a tick, 95th percentile {p95 * 1000:0.0} µs, over {ticks} ticks (headless, Debug build; the engine's modifier pass not included)"));
        Check(ticks >= 550 && CharacterTumble.Find(player.Inner) != null && GarageGame.Native.Characters.Contact.CharacterContact.FindContact(gubble.Inner) != null,
            $"both bodies were dressed as the room dresses them and timed every tick ({ticks} ticks)");
        if (!OS.GetCmdlineUserArgs().Contains("--skip-budget"))
            Check(median < 0.4, F($"both bodies' layers, all of CharacterMotion's, fit the 0.4 ms frame budget (median {median:0.000} ms a tick)"));
        walker.SetBodyVisual(null);
        floater.SetBodyVisual(null);
        player.Inner.Body.QueueFree();
        gubble.Inner.Body.QueueFree();
        fixture.QueueFree();
        await Frames(2);
    }

    /// <summary>
    /// The real thing: a SmallPlayerController wearing a drawn character with K3's layers, walking, running, stopping and
    /// turning on a test floor. The phase follows the distance the controller reports; nothing goes non-finite or sinks.
    /// </summary>
    private async Task TestController()
    {
        var fixture = new Node3D { Name = "ControllerFixture", Position = new Vector3(-20, 0, 20) };
        AddChild(fixture);
        var floor = new StaticBody3D { Position = new Vector3(0, -0.05f, 0), CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(8, 0.1f, 8) } });
        fixture.AddChild(floor);
        await Frames(2);
        var x = -1f;
        foreach (var id in new[] { "stickbear", "tomato_man" })
        {
            var (visual, gait, _) = Installed(id);
            var body = new SmallPlayerController { Name = "Walker_" + id, ReadKeyboard = false, Position = new Vector3(x, 0.01f, 1.5f) };
            x += 2;
            body.SetBodyVisual(visual);
            fixture.AddChild(body);
            await Frames(30);
            // Stickbear walks 2,000 km from home: the controller keeps the distance in double, so its 5 mm ticks still count
            // (review minor 11; in a float they rounded to nothing and the phase stalled).
            if (id == "stickbear") typeof(SmallPlayerController).GetField("_distanceM", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(body, 2_000_000.0);
            await Frames(2);
            var pose = visual.Body.Pose;
            var nonFinite = pose.NonFiniteWrites;
            var lowest = float.MaxValue;
            var finite = true;
            var startDistance = body.LastBodyState.DistanceM;
            var expected = 0.0;
            var lastDistance = startDistance;
            async Task Go(int frames, Vector2 input, bool sprint = false, float turn = 0)
            {
                body.SetControlInput(input, sprint: sprint);
                for (var i = 0; i < frames; i++)
                {
                    if (turn != 0) body.SetMovementFrame((body.MovementFrameYaw ?? body.GlobalRotation.Y) + turn * Dt);
                    await Frames(1);
                    var state = body.LastBodyState;
                    var moved = state.DistanceM - lastDistance;
                    lastDistance = state.DistanceM;
                    if (moved > 0 && state.Grounded) expected += moved / visual.Body.Profile.StrideAt(Mathf.Max(state.SpeedMps, 0));
                    lowest = Mathf.Min(lowest, visual.Body.FinalLowestPointY());
                    foreach (var bone in visual.Body.Bones) finite &= visual.Body.FinalBoneTransform(bone.Index).Origin.IsFinite();
                }
            }
            var cyclesBefore = gait.Cycles;
            await Go(120, new Vector2(0, 1));
            await Go(90, new Vector2(0, 1), sprint: true);
            await Go(20, Vector2.Zero);
            var stillCycles = gait.Cycles;
            var stillFootfalls = gait.Footfalls;
            await Go(120, Vector2.Zero);
            var stood = gait.Cycles == stillCycles && gait.Footfalls == stillFootfalls;
            await Go(150, new Vector2(0, 1), turn: 2.5f);
            body.SetMovementFrame(null);
            body.SetControlInput(Vector2.Zero);
            var walked = body.LastBodyState.DistanceM - startDistance;
            var cycles = gait.Cycles - cyclesBefore;
            Check(walked > 0.6f && gait.Footfalls > 4, F($"{id} on a real controller: walked, ran and turned {walked:0.00} m with {gait.Footfalls} footfalls"));
            Check(cycles > 0 && Math.Abs(cycles / expected - 1) < 0.25, F($"{id}: its phase follows the controller's distance ({cycles:0.00} cycles; the distance over the stride gives {expected:0.00})"));
            Check(stood, $"{id}: standing still on the real controller for 120 frames takes no step");
            Check(pose.NonFiniteWrites == nonFinite && finite, $"{id}: nothing goes non-finite on the real controller");
            Check(lowest >= -0.005f, F($"{id}: after the springs, its lowest point never sinks below -5 mm in its own space ({lowest * 1000:0.00} mm)"));
            body.SetBodyVisual(null);
            body.QueueFree();
            visual.Body.QueueFree();
            await Frames(2);
        }
        fixture.QueueFree();
        await Frames(1);
    }

    /// <summary>
    /// How a bone that both turns and squashes composes: the skeleton turns, then scales in the bone's own frame
    /// (Skeleton3D's rotation-then-scale); PoseBuffer.ModelTransform (K1) scales in the parent's frame. The suite's own
    /// composition must match the skeleton; the difference in PoseBuffer is reported for the integrator.
    /// </summary>
    private async Task TestComposition()
    {
        var holder = new Node3D { Name = "CompositionHolder", Position = new Vector3(30, 0, 0) };
        AddChild(holder);
        var visual = Visual("stickbear");
        var body = visual.Body;
        holder.AddChild(body);
        var pose = body.Pose;
        pose.Reset();
        pose.Begin(PoseLayer.Gait);
        pose.MultiplyScale(CharacterBody.RootBone, GaitMath.SquashScale(1.15f));
        pose.AddRotation(CharacterBody.RootBone, GaitMath.Pitch(0.2f));
        body.ApplyPose();
        await Frames(3);
        var head = body.HeadBone;
        var final = body.FinalBoneTransform(head).Origin;
        var scratch = new Transform3D[body.BoneCount];
        LowestAsSkeleton(body, pose, scratch);
        var mine = scratch[head].Origin;
        var theirs = pose.ModelTransform(head).Origin;
        Check(mine.DistanceTo(final) < 1e-5f, F($"the suite composes bones as the skeleton does ({mine.DistanceTo(final) * 1000:0.000} mm apart)"));
        GD.Print(F($"GAITS_NOTE: with the root turned 11.5° and stretched 15%, PoseBuffer.ModelTransform puts Stickbear's head {theirs.DistanceTo(final) * 1000:0.00} mm from where the skeleton draws it (scale composed in the parent's frame; the skeleton scales in the bone's own). The gaits keep the root's squash small and the suite checks both."));
        holder.QueueFree();
        await Frames(1);
    }
}
