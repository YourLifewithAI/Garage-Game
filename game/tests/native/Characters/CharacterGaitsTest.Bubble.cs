using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native;
using GarageGame.Native.Characters;
using GarageGame.Native.Characters.Gaits;

namespace GarageGame.Tests.Characters;

/// <summary>
/// Held by a bubble (BodyState.InBubble, a carry or a lift: decision 43) the drawn body floats, whatever its own gait is: it takes no
/// step and no airborne squash, its legs do not swing, it bobs and hangs as a float does, and it blends in and out of that as the
/// bubble takes it and lets it go. The Gubble, already a float, is the same either way.
/// </summary>
public partial class CharacterGaitsTest
{
    /// <summary>Walks at the walking pace outside a bubble, then is carried at the bubble's drift inside one, then is let go and walks again.</summary>
    private void TestInBubble()
    {
        foreach (var id in Shipped.Where(id => id != "gubble"))
        {
            var (visual, gait, _) = Installed(id);
            var walker = new Walker();
            var rig = gait.Rig;
            var legs = new[] { rig.Leg[GaitRig.Right], rig.Leg[GaitRig.Left] }.Where(bone => bone >= 0).ToArray();
            var walkingSwing = 0f;
            var carriedSwing = 0f;
            var roots = new List<float>();
            var cyclesIn = 0.0;
            var footfallsIn = 0;
            var weightIn = 0f;
            double cyclesOut = 0;
            var footfallsOut = 0;
            var weightOut = 1f;
            var after = 0.0;
            var carriedAir = 0f;
            var frame = 0;
            // 90 frames walking, 240 carried (the bubble's drift, 0.35 m/s; the last second airborne as a lift is), 150 let go.
            for (; frame < 480; frame++)
            {
                var carried = frame >= 90 && frame < 330;
                var airborne = carried && frame >= 270;
                var state = walker.Step(carried ? 0.35f : MotionProfile.WalkSpeedMps, 0, Dt, airTime: airborne ? 0.5f : 0f) with { InBubble = carried, Grounded = !airborne };
                visual.Stack.Evaluate(state, Clock(frame));
                var swing = legs.Length == 0 ? 0f : legs.Max(bone => MathF.Abs(Angle(visual.Body.Pose.GetRotation(bone), Vector3.Right)));
                if (frame is >= 30 and < 90) walkingSwing = Mathf.Max(walkingSwing, swing);
                if (frame is >= 150 and < 330) carriedSwing = Mathf.Max(carriedSwing, swing);
                if (frame is >= 150 and < 270) roots.Add(visual.Body.Pose.GetPosition(CharacterBody.RootBone).Y);
                if (frame is >= 280 and < 330) carriedAir = Mathf.Max(carriedAir, gait.Frame.Air);
                if (frame == 89) { cyclesIn = gait.Cycles; footfallsIn = gait.Footfalls; }
                if (frame == 329) { cyclesOut = gait.Cycles; footfallsOut = gait.Footfalls; weightIn = gait.BubbleWeight; }
                if (frame == 479) { weightOut = gait.BubbleWeight; after = gait.Cycles; }
            }
            Check(weightIn > 0.99f && weightOut < 0.01f, F($"{id}: held by a bubble the body floats (weight {weightIn:0.000}) and let go it walks again (weight {weightOut:0.000})"));
            Check(cyclesOut - cyclesIn < 1e-6 && footfallsOut == footfallsIn && after > cyclesOut + 0.2,
                F($"{id}: carried it takes no step ({footfallsOut - footfallsIn} footfalls, {cyclesOut - cyclesIn:0.000} cycles) and the walk resumes after ({after - cyclesOut:0.00} cycles)"));
            // (A hopper's legs do not swing when it walks; its bounce is what the bob below holds in.)
            Check(legs.Length == 0 || walkingSwing < 0.02f || carriedSwing < 0.25f * walkingSwing,
                F($"{id}: its legs stop swinging in the bubble ({Mathf.RadToDeg(carriedSwing):0.0}° against {Mathf.RadToDeg(walkingSwing):0.0}° walking)"));
            var bob = roots.Max() - roots.Min();
            Check(bob > 0.002f && bob < 0.012f, F($"{id}: it hangs and bobs as a float does ({bob * 1000:0.0} mm of bob)"));
            Check(carriedAir == 0, F($"{id}: carried in the air it takes no airborne squash (air {carriedAir:0.00})"));
            visual.Body.Free();
        }

        // The airborne squash and stretch is off while a bubble holds the body (a lift carries it up through the air).
        var (lift, liftGait, _) = Installed("stickbear");
        var carriedUp = new Walker();
        var air = 0f;
        var plain = 0f;
        for (var frame = 0; frame < 120; frame++)
        {
            var state = carriedUp.Step(0, 0, Dt, airTime: frame * Dt + 0.2f) with { InBubble = true, Grounded = false };
            lift.Stack.Evaluate(state, Clock(frame));
            air = Mathf.Max(air, liftGait.Frame.Air);
        }
        lift.Body.Free();
        var (fall, fallGait, _) = Installed("stickbear");
        var falling = new Walker();
        for (var frame = 0; frame < 120; frame++)
        {
            var state = falling.Step(0, 0, Dt, airTime: frame * Dt + 0.2f) with { Grounded = false };
            fall.Stack.Evaluate(state, Clock(frame));
            plain = Mathf.Max(plain, fallGait.Frame.Air);
        }
        fall.Body.Free();
        Check(air == 0 && plain > 0.9f, F($"carried up through the air by a bubble the body is not airborne (air {air:0.00}); falling the same way it is (air {plain:0.00})"));

        // The Gubble floats anyway: held at rest by a bubble or free, the same frames give the same pose.
        var hashes = new List<ulong>[2];
        for (var run = 0; run < 2; run++)
        {
            hashes[run] = new List<ulong>();
            var (gubble, _, _) = Installed("gubble");
            var drift = new Walker();
            for (var frame = 0; frame < 240; frame++)
            {
                var state = drift.Step(0f, 0, Dt) with { InBubble = run == 1 };
                gubble.Stack.Evaluate(state, Clock(frame));
                hashes[run].Add(gubble.Body.Pose.Hash());
            }
            gubble.Body.Free();
        }
        Check(hashes[0].SequenceEqual(hashes[1]), "the Gubble, a float already, is posed the same at rest inside a bubble or out");
    }
}
