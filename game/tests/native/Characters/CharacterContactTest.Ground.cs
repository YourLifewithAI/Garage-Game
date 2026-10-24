using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native.Characters;
using GarageGame.Native.Characters.Contact;
using GarageGame.Native.Kernel;

namespace GarageGame.Tests.Characters;

/// <summary>Feet on flat ground and slopes, the step, and the landing tiers from real drops.</summary>
public partial class CharacterContactTest
{
    private async Task TestFlat()
    {
        foreach (var s in _subjects) Place(s.Body, s.Course.Flat, 0);
        await Frames(45);
        foreach (var s in _subjects)
        {
            float Ground(Vector3 p) => s.Course.Origin.Y;
            var below = Below(s, Ground);
            if (s.Floats)
            {
                Report(below < 0 && !s.Contact.IkOn && s.Contact.Casts == 0,
                    $"{s.Id} floats on the flat: nothing touches down, no rays ({Mm(-below)} clear)");
                continue;
            }
            Report(below <= BelowToleranceM && below >= -0.003f, $"{s.Id} stands on the flat: the lowest point {Mm(-below)} from the floor (nothing below -5 mm)");
            if (s.Legged)
            {
                var worst = Enumerable.Range(0, s.Rig.Legs.Count).Max(l => Mathf.Abs(Sole(s, l).Y - Ground(Sole(s, l))));
                Report(s.Contact.IkOn && s.Contact.IkInfluence > 0.99f && worst <= SoleToleranceM, $"{s.Id}: the leg IK plants both soles on the flat (worst {Mm(worst)})");
            }
            else Check(s.Contact.Casts >= s.Contact.FootCount && Mathf.Abs(RootUp(s).Degrees) < 1, $"{s.Id}: a body without legs stands upright on the flat");
        }
    }

    private async Task TestSlope()
    {
        // Facing uphill (pitch), across (roll) and downhill.
        foreach (var (name, yaw) in new[] { ("uphill", Facing(Vector3.Right)), ("across", 0f), ("downhill", Facing(Vector3.Left)) })
        {
            foreach (var s in Walkers) Place(s.Body, s.Course.SlopeCentre, yaw);
            await Frames(50);
            foreach (var s in Walkers)
            {
                var course = s.Course;
                var below = Below(s, course.SlopeY);
                if (s.Legged && name == "across")
                {
                    // Legs keep the body upright (only a body without legs tilts to the floor), so across a steep slope a
                    // wide body's low hand on the uphill side passes into it: a wide pose meeting the world (Decision 4, K8's
                    // tuck). What contact puts on the ground is held to the tolerance: the feet.
                    var feet = Below(s, course.SlopeY, b => PartRoles.IsFoot(b.Role) || (b.Parent > 0 && PartRoles.IsFoot(s.Rig.Bones[b.Parent].Role)));
                    Report(s.Body.IsOnFloor() && feet <= BelowToleranceM,
                        $"{s.Id} on the 30° slope facing across: the feet sink no more than 5 mm ({Mm(-feet)} from the slope; the whole body's lowest point {Mm(-below)}, a wide limb on the uphill side)");
                }
                else Report(s.Body.IsOnFloor() && below <= BelowToleranceM, $"{s.Id} on the 30° slope facing {name}: nothing below -5 mm (the lowest point {Mm(-below)} from the slope)");
                if (s.Legged)
                {
                    var worst = Enumerable.Range(0, s.Rig.Legs.Count).Max(l => Mathf.Abs(Sole(s, l).Y - course.SlopeY(Sole(s, l))));
                    Report(s.Contact.IkOn && worst <= SoleToleranceM && s.Contact.DropM > 0.001f,
                        $"{s.Id} on the 30° slope facing {name}: the soles plant within 5 mm of the slope (worst {Mm(worst)}; the pelvis drops {Mm(s.Contact.DropM)})");
                }
                else
                {
                    var (up, degrees) = RootUp(s);
                    var off = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(up.Dot(course.SlopeNormal), -1, 1)));
                    Report(degrees > 23 && degrees < 25.5f && off < 6.5f,
                        $"{s.Id} on the 30° slope facing {name}: the root tilts {degrees:0.0}° toward the floor (at most 25°; {off:0.0}° short of its normal)");
                }
            }
        }
    }

    private async Task TestSlopeWalk()
    {
        var worstBelow = new Dictionary<string, float>();
        var worstSole = new Dictionary<string, float>();
        var tilt = new Dictionary<string, (float Min, float Max)>();
        var frames = new Dictionary<string, int>();
        foreach (var s in Walkers)
        {
            Place(s.Body, s.Course.SlopeCentre + new Vector3(-0.15f, -0.15f * Mathf.Tan(Slope), 0), Facing(Vector3.Right));
            worstBelow[s.Id] = float.MinValue;
            worstSole[s.Id] = 0;
            tilt[s.Id] = (90, 0);
            frames[s.Id] = 0;
        }
        await Frames(20);
        for (var leg = 0; leg < 2; leg++)
        {
            foreach (var s in Walkers)
            {
                s.Body.Rotation = new Vector3(0, leg == 0 ? Facing(Vector3.Right) : Facing(Vector3.Left), 0);
                s.Body.SetControlInput(new Vector2(0, 1));
            }
            for (var f = 0; f < 50; f++)
            {
                await Frames(1);
                if (f < 8) continue;
                foreach (var s in Walkers)
                {
                    if (!s.Body.IsOnFloor() || s.Body.LastBodyState.SpeedMps < 0.1f) continue;
                    frames[s.Id]++;
                    worstBelow[s.Id] = Mathf.Max(worstBelow[s.Id], Below(s, s.Course.SlopeY));
                    if (s.Legged)
                        for (var l = 0; l < s.Rig.Legs.Count; l++)
                            worstSole[s.Id] = Mathf.Max(worstSole[s.Id], Mathf.Abs(Sole(s, l).Y - s.Course.SlopeY(Sole(s, l))));
                    else
                    {
                        var degrees = RootUp(s).Degrees;
                        tilt[s.Id] = (Mathf.Min(tilt[s.Id].Min, degrees), Mathf.Max(tilt[s.Id].Max, degrees));
                    }
                }
            }
        }
        foreach (var s in Walkers)
        {
            s.Body.SetControlInput(Vector2.Zero);
            Report(frames[s.Id] > 60 && worstBelow[s.Id] <= BelowToleranceM,
                $"{s.Id} walking up and down the 30° slope ({frames[s.Id]} frames): nothing below -5 mm (worst {Mm(-worstBelow[s.Id])} from the slope)");
            if (s.Legged)
                Report(worstSole[s.Id] <= SoleToleranceM, $"{s.Id} walking the slope: the soles stay within 5 mm of it (worst {Mm(worstSole[s.Id])})");
            else
                Report(tilt[s.Id].Min > 20 && tilt[s.Id].Max < 25.5f, $"{s.Id} walking the slope: the root's tilt follows it within 25° ({tilt[s.Id].Min:0.0}° to {tilt[s.Id].Max:0.0}°)");
        }
        await Frames(5);
    }

    /// <summary>The leading foot lifts onto a 2 cm step before the capsule climbs it, and the body follows a frame later.</summary>
    private async Task TestStep()
    {
        var lifted = new Dictionary<string, int>();
        var stepped = new Dictionary<string, int>();
        var lagAtStep = new Dictionary<string, float>();
        var lagAfter = new Dictionary<string, float>();
        var steps = new Dictionary<string, int>();
        foreach (var s in Walkers)
        {
            Place(s.Body, s.Course.StepStart, 0);
            lifted[s.Id] = stepped[s.Id] = -1;
            lagAtStep[s.Id] = lagAfter[s.Id] = 0;
        }
        await Frames(15);
        foreach (var s in Walkers)
        {
            steps[s.Id] = s.Body.StepsClimbed;
            s.Body.SetControlInput(new Vector2(0, 1));
        }
        for (var f = 0; f < 90; f++)
        {
            await Frames(1);
            foreach (var s in Walkers)
            {
                var floor = s.Course.Origin.Y;
                var lift = 0f;
                for (var i = 0; i < s.Contact.FootCount; i++)
                    lift = Mathf.Max(lift, s.Contact.FootIsLeg(i) ? s.Contact.FootTarget(i).Y - floor : s.Contact.FootLiftM(i));
                if (lifted[s.Id] < 0 && lift >= 0.01f && stepped[s.Id] < 0) lifted[s.Id] = f;
                if (stepped[s.Id] < 0 && s.Body.StepsClimbed > steps[s.Id])
                {
                    stepped[s.Id] = f;
                    lagAtStep[s.Id] = s.Contact.RiseLagM;
                }
                if (stepped[s.Id] >= 0 && f == stepped[s.Id] + 8) lagAfter[s.Id] = s.Contact.RiseLagM;
            }
        }
        foreach (var s in Walkers)
        {
            s.Body.SetControlInput(Vector2.Zero);
            Report(lifted[s.Id] >= 0 && stepped[s.Id] > lifted[s.Id],
                $"{s.Id} at a 2 cm step: the leading foot lifts 1 cm onto it at frame {lifted[s.Id]}, before the capsule steps up at frame {stepped[s.Id]}");
            Report(lagAtStep[s.Id] > 0.012f && lagAfter[s.Id] < 0.003f,
                $"{s.Id} at the step: the body follows a frame later (held {Mm(lagAtStep[s.Id])} low on the step's tick, {Mm(lagAfter[s.Id])} eight frames on)");
        }
        await Frames(10);
    }

    /// <summary>Real drops under the default 3.5 m/s² gravity: the tiers PlayerVitals.FallDamage gives.</summary>
    private async Task TestLandings()
    {
        foreach (var (height, expected) in new[] { (0.2f, LandingTier.Squash), (0.4f, LandingTier.Stumble), (0.6f, LandingTier.Tumble) })
        {
            var before = new Dictionary<string, int>();
            var squash = new Dictionary<string, float>();
            var stumbleFrames = new Dictionary<string, int>();
            var held = new Dictionary<string, bool>();
            var handDrop = new Dictionary<string, float>();
            var handRest = new Dictionary<string, float>();
            foreach (var s in Walkers) Place(s.Body, s.Course.Flat, 0);
            await Frames(20);
            foreach (var s in Walkers)
            {
                before[s.Id] = s.Contact.Landings;
                squash[s.Id] = 0;
                stumbleFrames[s.Id] = 0;
                held[s.Id] = false;
                handDrop[s.Id] = 0;
                handRest[s.Id] = HandsLowestY(s);
                s.Body.GlobalPosition = s.Course.Flat + Vector3.Up * height;
                s.Body.Velocity = Vector3.Zero;
                s.Body.ResetVitalsObservation();
            }
            for (var f = 0; f < 150; f++)
            {
                await Frames(1);
                foreach (var s in Walkers)
                {
                    if (s.Contact.Landings == before[s.Id]) continue;
                    squash[s.Id] = Mathf.Max(squash[s.Id], s.Contact.Squash);
                    if (s.Contact.Stumbling) stumbleFrames[s.Id]++;
                    held[s.Id] |= s.Body.ControlHeld;
                    handDrop[s.Id] = Mathf.Max(handDrop[s.Id], handRest[s.Id] - HandsLowestY(s));
                }
            }
            foreach (var s in Walkers)
            {
                var c = s.Contact;
                var speed = c.LastLandingSpeedMps;
                var damage = PlayerVitals.FallDamage(speed, false);
                var tier = damage <= 0 ? LandingTier.Squash : damage < ContactLayer.TumbleFromHealth ? LandingTier.Stumble : LandingTier.Tumble;
                var label = FormattableString.Invariant($"{s.Id} dropped {height:0.0} m: lands at {speed:0.00} m/s, {damage:0.0} health in Challenge, a {c.LastTier.ToString().ToLowerInvariant()}");
                Report(c.Landings == before[s.Id] + 1 && c.LastTier == expected && tier == expected && !held[s.Id], label + " (the tier fall damage gives; control kept)");
                var full = Mathf.Min(ContactLayer.MaxSquash, s.Rig.Profile.Squash);
                if (expected == LandingTier.Squash)
                {
                    var amplitude = full * Mathf.Clamp(speed / PlayerVitals.SafeLandingSpeedMps, 0, 1);
                    Report(squash[s.Id] > 0.8f * amplitude && squash[s.Id] <= full + 1e-4f && stumbleFrames[s.Id] == 0,
                        FormattableString.Invariant($"{s.Id} squashes {squash[s.Id] * 100:0.0}% on the {height:0.0} m landing (scaled by speed: {amplitude * 100:0.0}% of a full {full * 100:0.0}%), no stumble"));
                }
                else if (expected == LandingTier.Stumble)
                {
                    var seconds = stumbleFrames[s.Id] / 60f;
                    Report(Mathf.Abs(seconds - ContactLayer.StumbleS) <= 0.04f && squash[s.Id] >= 0.9f * full,
                        FormattableString.Invariant($"{s.Id} stumbles for {seconds:0.00} s on the {height:0.0} m landing (squash {squash[s.Id] * 100:0.0}%)"));
                    if (s.Contact.HasHands)
                        Report(handDrop[s.Id] > 0.004f, $"{s.Id}'s hand goes down in the stumble ({Mm(handDrop[s.Id])} lower)");
                }
                else Report(stumbleFrames[s.Id] == 0 && squash[s.Id] > 0, $"{s.Id} at the tumble's tier: no stumble, only the first squash before the tumble takes over");
            }
            await Frames(10);
        }
    }

    /// <summary>At a ledge: a foot over the drop finds no ground within 3 cm and keeps its pose; the other still plants.</summary>
    private async Task TestLedge()
    {
        // The slab's west edge at x=-2: the capsule's axis 3 mm in, facing -Z, its left side over the drop.
        foreach (var s in Walkers) Place(s.Body, s.Course.Origin + new Vector3(-2 + 0.003f, 0, 1.0f), 0);
        await Frames(40);
        foreach (var s in Walkers)
        {
            var over = -1;
            var on = -1;
            for (var i = 0; i < s.Contact.FootCount; i++)
            {
                var x = s.Contact.FootPosed(i).X - s.Course.Origin.X;
                if (x < -2.004f) over = i;
                else if (x > -1.99f) on = i;
            }
            if (over < 0)
            {
                Check(s.Body.IsOnFloor(), $"{s.Id}: no foot reaches past a ledge its capsule stands at (its feet are under the body)");
                continue;
            }
            var kept = s.Contact.FootIsLeg(over)
                ? s.Contact.FootTarget(over).DistanceTo(s.Contact.SkeletonTransform * (s.Rig.Pose.ModelTransform(s.Contact.FootBone(over)) * s.Contact.FootSole(over)))
                : s.Contact.FootLiftM(over);
            Report(s.Body.IsOnFloor() && !s.Contact.FootFoundGround(over) && kept < 0.001f && (on < 0 || s.Contact.FootFoundGround(on)),
                $"{s.Id} at a ledge: the foot over the drop finds no ground within 3 cm and keeps its pose ({Mm(kept)} from it); the other plants");
        }
    }

    /// <summary>The lowest hand (in the world), or the body's own height when it has none.</summary>
    private static float HandsLowestY(Subject s)
    {
        var lowest = float.MaxValue;
        foreach (var bone in s.Rig.Bones)
            if (PartRoles.IsHand(bone.Role) && !bone.IsRigid) lowest = Mathf.Min(lowest, World(s, bone.Index).Origin.Y);
        return lowest == float.MaxValue ? s.Body.GlobalPosition.Y : lowest;
    }
}
