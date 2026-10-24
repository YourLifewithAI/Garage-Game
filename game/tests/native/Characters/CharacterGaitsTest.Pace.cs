using Godot;
using System;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Characters;
using GarageGame.Native.Characters.Gaits;
using GarageGame.Native.Room;
using GarageGame.Native.Seams;

namespace GarageGame.Tests.Characters;

/// <summary>
/// The gaits read the body's real walk and run (docs/runs/RUN-2-MECHANICS.md, M5; RUN-2-CHARACTERS.md, "Stride and cadence from leg
/// length"): a Quick feet body walking at its full pace is walking, a body slowed by an empty bar that runs is running, and a state built
/// without the speeds reads as the shared base, as every state did before.
/// </summary>
public partial class CharacterGaitsTest
{
    private void TestPaceReads()
    {
        var plain = new BodyState();
        Check(BodyPace.Walk(plain) == MotionProfile.WalkSpeedMps && BodyPace.Run(plain) == MotionProfile.RunSpeedMps && BodyPace.Blend(plain, 0.64f) == 0.5f,
            "a state without the body's speeds reads as the shared base: walk 0.32, run 0.96, half way at 0.64 m/s");
        var quick = new BodyState { WalkSpeedMps = 0.384f, RunSpeedMps = 1.152f };
        Check(BodyPace.Walk(quick) == 0.384f && BodyPace.Run(quick) == 1.152f && BodyPace.Blend(quick, 0.384f) == 0 && BodyPace.Blend(quick, 1.152f) == 1 && Mathf.IsEqualApprox(BodyPace.Blend(quick, 0.768f), 0.5f),
            "a Quick feet body's walk is 0.384 and its run 1.152: those are the ends of its blend");
        Check(BodyPace.Blend(quick, -3) == 0 && BodyPace.Blend(quick, 99) == 1 && Mathf.IsEqualApprox(BodyPace.OfRun(quick, 0.576f), 0.5f) && BodyPace.OfRun(quick, 99) == 1.5f,
            "the blend and the share of a run are bounded");
        var odd = new BodyState { WalkSpeedMps = 0.5f, RunSpeedMps = 0.2f };
        Check(BodyPace.Run(odd) > BodyPace.Walk(odd) && !float.IsNaN(BodyPace.Blend(odd, 0.5f)), "a run is always above the walk, so the blend can divide");
        var broken = new BodyState { WalkSpeedMps = float.NaN, RunSpeedMps = -1 };
        Check(BodyPace.Walk(broken) == MotionProfile.WalkSpeedMps && BodyPace.Run(broken) == MotionProfile.RunSpeedMps, "speeds that are not positive numbers read as the base");
        var (hopVisual, hopLayer, _) = Installed("cloudpuff");
        var hop = hopLayer.Gait as HopGait;
        Check(hop != null && Mathf.IsEqualApprox(hop.HopHeightM(0), hop.HopHeightAt(MotionProfile.WalkSpeedMps, 0)) && Mathf.IsEqualApprox(hop.HopHeightM(1), hop.HopHeightAt(MotionProfile.RunSpeedMps, 1)) &&
              Mathf.IsEqualApprox(hop.HopHeightM(1, 0.384f, 1.152f), hop.HopHeightAt(1.152f, 1, 0.384f)), "the hop's height at a walk and at a run follows the body's walk and run");
        hopVisual.Body.Free();
    }

    /// <summary>Every character's gait layer, fed a walk and a run at the body's own pace, on the states the controller would give.</summary>
    private void TestPaceOnEveryGait()
    {
        foreach (var id in Shipped)
        {
            foreach (var (name, walk, run) in new[] { ("base", 0.32f, 0.96f), ("Quick feet", 0.384f, 1.152f), ("half", 0.16f, 0.48f) })
            {
                var (visual, gait, _) = Installed(id);
                var knownPace = (Walk: 0.0f, Run: 0.0f);
                var walker = new Walker();
                var frame = 0;
                BodyState Next(float speed) => walker.Step(speed, 0, Dt) with { WalkSpeedMps = walk, RunSpeedMps = run };
                for (var i = 0; i < 120; i++) visual.Stack.Evaluate(Next(walk), Clock(frame++));
                knownPace.Walk = gait.Frame.Run;
                for (var i = 0; i < 120; i++) visual.Stack.Evaluate(Next(run), Clock(frame++));
                knownPace.Run = gait.Frame.Run;
                var check = gait.Frame;
                Check(knownPace.Walk < 0.03f && knownPace.Run > 0.97f && Mathf.IsEqualApprox(check.WalkMps, walk) && Mathf.IsEqualApprox(check.RunMps, run),
                    F($"{id} at {name} pace ({walk:0.000} walk, {run:0.000} run): its walk reads as a walk ({knownPace.Walk:0.000}) and its run as a run ({knownPace.Run:0.000})"));
                visual.Body.Free();

                // The same speeds on states built without the body's pace read against the shared base, as they always did.
                if (name == "base") continue;
                var (second, gait2, _) = Installed(id);
                var walker2 = new Walker();
                var frame2 = 0;
                for (var i = 0; i < 120; i++) second.Stack.Evaluate(walker2.Step(walk, 0, Dt), Clock(frame2++));
                var walkOnBase = gait2.Frame.Run;
                for (var i = 0; i < 120; i++) second.Stack.Evaluate(walker2.Step(run, 0, Dt), Clock(frame2++));
                var runOnBase = gait2.Frame.Run;
                Check(name == "Quick feet" ? walkOnBase > 0.07f && runOnBase > 0.97f : walkOnBase < 0.01f && runOnBase < 0.3f,
                    F($"{id} at {name} pace on states without the body's speeds reads against the base ({walkOnBase:0.000}, {runOnBase:0.000}): the reading the body's own speeds correct"));
                second.Body.Free();
            }
        }
    }

    private sealed class PaceLedger : IStaminaLedger
    {
        public StaminaGate GateValue = StaminaGate.Free;
        public void Report(BodyExertion exertion) { }
        public StaminaGate Gate => GateValue;
    }

    /// <summary>A real body with Quick feet and with an empty bar, wearing a character: the layer's walk and run follow the body's.</summary>
    private async Task TestPaceOnARealBody()
    {
        var fixture = new Node3D { Name = "PaceFixture", Position = new Vector3(-40, 0, 20) };
        AddChild(fixture);
        var floor = new StaticBody3D { Position = new Vector3(0, -0.05f, -8), CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(4, 0.1f, 24) } });
        fixture.AddChild(floor);
        await Frames(2);
        foreach (var id in new[] { "stickbear", "cloudpuff" })
        {
            var (visual, gait, _) = Installed(id);
            var body = new SmallPlayerController { Name = "Pace_" + id, ReadKeyboard = false, Position = new Vector3(0, 0.01f, 2) };
            body.SetBodyVisual(visual);
            fixture.AddChild(body);
            await Frames(10);
            var ledger = new PaceLedger();
            body.StaminaLedger = ledger;
            body.VitalsTick = (_, _) => { };
            async Task<(float Walk, float Run, BodyState State)> Measure(string label)
            {
                body.TryTeleportTo(fixture.GlobalPosition + new Vector3(0, 0.003f, 2));
                await Frames(4);
                body.SetControlInput(new Vector2(0, 1));
                await Frames(80);
                var walk = gait.Frame.Run;
                var walkState = body.LastBodyState;
                body.SetControlInput(new Vector2(0, 1), sprint: true);
                await Frames(80);
                var run = gait.Frame.Run;
                body.SetControlInput(Vector2.Zero);
                await Frames(20);
                Check(walkState.WalkSpeedMps == body.WalkSpeedNowMps && walkState.RunSpeedMps == body.RunSpeedNowMps,
                    F($"{id} {label}: the body's state carries its walk ({walkState.WalkSpeedMps:0.000}) and run ({walkState.RunSpeedMps:0.000}) m/s"));
                return (walk, run, walkState);
            }
            var plain = await Measure("with no knack");
            Check(plain.Walk < 0.03f && plain.Run > 0.97f && plain.State.WalkSpeedMps == 0.32f, F($"{id}: with no knack a walk is a walk and a run a run ({plain.Walk:0.000}, {plain.Run:0.000})"));
            body.Knacks = new BodyKnacks { WalkRunFactor = 1.2f };
            var quick = await Measure("with Quick feet at full rank");
            Check(quick.Walk < 0.03f && quick.Run > 0.97f && Mathf.IsEqualApprox(quick.State.WalkSpeedMps, 0.384f),
                F($"{id}: with Quick feet a walk at 0.384 m/s still reads as a walk ({quick.Walk:0.000}) and the faster run as a run ({quick.Run:0.000}); against the base the walk would read {Mathf.Clamp((0.384f - 0.32f) / 0.64f, 0, 1):0.000}"));
            body.Knacks = BodyKnacks.None;
            ledger.GateValue = new StaminaGate(true, true);
            var spent = await Measure("with an empty bar");
            Check(spent.Walk < 0.03f && spent.Run < 0.03f && Mathf.IsEqualApprox(spent.State.RunSpeedMps, 0.96f),
                F($"{id}: with an empty bar Shift held still walks and reads as a walk ({spent.Run:0.000}); the body's run stays {spent.State.RunSpeedMps:0.00} m/s for when it comes back"));
            body.StaminaLedger = null;
            body.VitalsTick = null;
            body.SetBodyVisual(null);
            body.QueueFree();
            visual.Body.QueueFree();
            await Frames(2);
        }
        fixture.QueueFree();
        await Frames(1);
    }
}
