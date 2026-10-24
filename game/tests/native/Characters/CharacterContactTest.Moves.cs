using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native.Characters;
using GarageGame.Native.Characters.Contact;

namespace GarageGame.Tests.Characters;

/// <summary>Water, climbing, carrying, wind and the look targets.</summary>
public partial class CharacterContactTest
{
    /// <summary>How far a bone's pose turns it from rest (degrees): what the layers asked of it this frame.</summary>
    private static float Posed(Subject s, int bone) => TurnDegrees(s.Rig.Pose.GetRotation(bone));

    private static IEnumerable<int> Bones(Subject s, Func<RigBone, bool> which) => s.Rig.Bones.Where(b => b.Part >= 0 && !b.IsRigid && which(b)).Select(b => b.Index);

    /// <summary>Wading legs trail and paddle; swimmers keep the body's tilt and stroke and kick; divers tuck and stream; deep water never stumbles.</summary>
    private async Task TestWater()
    {
        var wadeTurn = new Dictionary<string, float>();
        var armRange = new Dictionary<string, (float Min, float Max)>();
        var kickRange = new Dictionary<string, (float Min, float Max)>();
        var swimTilt = new Dictionary<string, float>();
        foreach (var s in Walkers)
        {
            Place(s.Body, s.Course.Shelf, Mathf.Pi);
            wadeTurn[s.Id] = 0;
            armRange[s.Id] = (float.MaxValue, float.MinValue);
            kickRange[s.Id] = (float.MaxValue, float.MinValue);
            swimTilt[s.Id] = 0;
        }
        await Frames(10);
        foreach (var s in Walkers) s.Body.SetControlInput(new Vector2(0, 0.7f));
        for (var f = 0; f < 210; f++)
        {
            await Frames(1);
            foreach (var s in Walkers)
            {
                var legs = Bones(s, b => b.Role == PartRole.Leg || (PartRoles.IsFoot(b.Role) && s.Rig.Bones[b.Parent].Role != PartRole.Leg)).ToList();
                if (!s.Body.IsSwimming && s.Contact.Wading > 0.3f)
                    foreach (var leg in legs) wadeTurn[s.Id] = Mathf.Max(wadeTurn[s.Id], Posed(s, leg));
                if (s.Body.IsSwimming && s.Contact.SwimWeight > 0.9f)
                {
                    var arm = s.Rig.FindBone(PartRole.Arm);
                    if (arm > 0) armRange[s.Id] = (Mathf.Min(armRange[s.Id].Min, Posed(s, arm)), Mathf.Max(armRange[s.Id].Max, Posed(s, arm)));
                    if (legs.Count > 0) kickRange[s.Id] = (Mathf.Min(kickRange[s.Id].Min, Posed(s, legs[0])), Mathf.Max(kickRange[s.Id].Max, Posed(s, legs[0])));
                    swimTilt[s.Id] = Mathf.Max(swimTilt[s.Id], Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(s.Rig.GlobalBasis.Y.Normalized().Dot(Vector3.Up), -1, 1))));
                }
            }
            if (f == 160) foreach (var s in Walkers) s.Body.SetControlInput(new Vector2(0, 0.3f));
        }
        foreach (var s in Walkers)
        {
            Report(wadeTurn[s.Id] > 8, $"{s.Id} wading: the legs (or feet) trail and paddle (turned up to {wadeTurn[s.Id]:0.0}°)");
            var arms = armRange[s.Id].Max - armRange[s.Id].Min;
            var kicks = kickRange[s.Id].Max - kickRange[s.Id].Min;
            Report(s.Body.IsSwimming && swimTilt[s.Id] > 60 && arms > 30 && kicks > 8,
                $"{s.Id} swimming: the body keeps the visual root's tilt ({swimTilt[s.Id]:0}°), the arms stroke through {arms:0}° and the legs kick through {kicks:0}°");
        }

        // Diving: Ctrl takes the body under; the head tucks and the hat and hair stream back.
        var tuck = new Dictionary<string, float>();
        var stream = new Dictionary<string, float>();
        var dove = new Dictionary<string, bool>();
        foreach (var s in Walkers)
        {
            tuck[s.Id] = stream[s.Id] = 0;
            dove[s.Id] = false;
            s.Body.SetControlInput(new Vector2(0, 0.5f));
            s.Body.SetDiveInput(true, false);
        }
        for (var f = 0; f < 70; f++)
        {
            await Frames(1);
            foreach (var s in Walkers)
            {
                if (!s.Body.LastBodyState.Diving || s.Contact.DiveWeight < 0.5f) continue;
                dove[s.Id] = true;
                if (s.Rig.HeadBone != s.Rig.BodyBone) tuck[s.Id] = Mathf.Max(tuck[s.Id], Posed(s, s.Rig.HeadBone));
                foreach (var part in Bones(s, b => (b.Role == PartRole.Hat && s.Rig.Bones[b.Parent].Role != PartRole.Hat) || (b.Role == PartRole.Hair && s.Rig.Bones[b.Parent].Role != PartRole.Hair)))
                    stream[s.Id] = Mathf.Max(stream[s.Id], Posed(s, part));
            }
        }
        foreach (var s in Walkers)
        {
            s.Body.SetDiveInput(false, false);
            s.Body.SetControlInput(Vector2.Zero);
            var head = s.Rig.HeadBone != s.Rig.BodyBone;
            var streams = Bones(s, b => b.Role is PartRole.Hat or PartRole.Hair).Any();
            Report(dove[s.Id] && (!head || tuck[s.Id] > 10) && (!streams || stream[s.Id] > 10),
                $"{s.Id} diving: {(head ? $"the head tucks {tuck[s.Id]:0}°" : "no head to tuck")}, {(streams ? $"the hat and hair stream back {stream[s.Id]:0}°" : "no hat or hair to stream")}");
        }
        await Frames(60);

        // A fall into deep water never stumbles.
        var landings = new Dictionary<string, int>();
        foreach (var s in Walkers)
        {
            landings[s.Id] = s.Contact.Landings;
            s.Body.TryTeleportTo(s.Course.Shelf + Vector3.Up * 0.003f);
            s.Body.GlobalPosition = s.Course.DeepWater + Vector3.Up * 0.5f;
            s.Body.Velocity = Vector3.Zero;
            s.Body.ResetVitalsObservation();
        }
        var stumbled = new HashSet<string>();
        for (var f = 0; f < 120; f++)
        {
            await Frames(1);
            foreach (var s in Walkers)
                if (s.Contact.Stumbling) stumbled.Add(s.Id);
        }
        foreach (var s in Walkers)
            Report(s.Contact.Landings > landings[s.Id] && s.Contact.LastTier == LandingTier.Water && !stumbled.Contains(s.Id),
                FormattableString.Invariant($"{s.Id} falls 0.5 m into deep water: caught (a water landing, {s.Contact.LastLandingSpeedMps:0.00} m/s after the catch), never a stumble"));
    }

    /// <summary>The climber lies along the face, reaches up hand over hand and presses its feet and body to the wall.</summary>
    private async Task TestClimb()
    {
        var crossings = new Dictionary<string, int>();
        var raised = new Dictionary<string, float>();
        var gap = new Dictionary<string, float>();
        var lastSign = new Dictionary<string, float>();
        var climbed = new Dictionary<string, int>();
        var legsForward = new Dictionary<string, float>();
        foreach (var s in Walkers)
        {
            Place(s.Body, s.Course.ClimbStart, 0);
            crossings[s.Id] = climbed[s.Id] = 0;
            raised[s.Id] = float.MinValue;
            gap[s.Id] = float.NaN;
            lastSign[s.Id] = 0;
            legsForward[s.Id] = 0;
        }
        await Frames(5);
        foreach (var s in Walkers) s.Body.SetControlInput(new Vector2(0, 1));
        for (var f = 0; f < 220; f++)
        {
            await Frames(1);
            foreach (var s in Walkers)
            {
                if (!s.Body.IsClimbing || s.Contact.ClimbWeight < 0.9f || s.Body.GlobalPosition.Y - s.Course.Origin.Y > 0.12f) continue;
                climbed[s.Id]++;
                // The hands: above their shoulders, the higher one changing sides as the body climbs.
                // Each hand's height over its own shoulder (shoulders are rarely level): the one reaching higher changes sides.
                var heights = new List<(float Side, float Height)>();
                foreach (var arm in Bones(s, b => b.Role == PartRole.Arm))
                {
                    var hand = s.Rig.Bones[arm].Children.FirstOrDefault(c => PartRoles.IsHand(s.Rig.Bones[c].Role), -1);
                    if (hand < 0) continue;
                    var above = World(s, hand).Origin.Y - World(s, arm).Origin.Y;
                    raised[s.Id] = Mathf.Max(raised[s.Id], above);
                    heights.Add((s.Rig.Bones[arm].Side == BodySide.Left ? -1 : 1, above / Mathf.Max((World(s, hand).Origin - World(s, arm).Origin).Length(), 0.001f)));
                }
                if (heights.Count == 2)
                {
                    var sign = Mathf.Sign((heights[0].Height - heights[1].Height) * heights[0].Side);
                    if (sign != 0 && lastSign[s.Id] != 0 && sign != lastSign[s.Id]) crossings[s.Id]++;
                    if (sign != 0) lastSign[s.Id] = sign;
                }
                foreach (var leg in Bones(s, b => b.Role == PartRole.Leg || PartRoles.IsFoot(b.Role) && s.Rig.Bones[b.Parent].Role != PartRole.Leg))
                    legsForward[s.Id] = Mathf.Max(legsForward[s.Id], Posed(s, leg));
                // The body's front against the face (the wall's face at WallFaceZ; the body is on its +Z side).
                var body = s.Rig.Bones[s.Rig.BodyBone];
                var t = World(s, body.Index);
                var front = float.MaxValue;
                for (var c = 0; c < 8; c++)
                {
                    var b = body.MeshBounds;
                    front = Mathf.Min(front, (t * (b.Position + new Vector3((c & 1) != 0 ? b.Size.X : 0, (c & 2) != 0 ? b.Size.Y : 0, (c & 4) != 0 ? b.Size.Z : 0))).Z);
                }
                gap[s.Id] = front - s.Course.WallFaceZ;
            }
        }
        foreach (var s in Walkers)
        {
            s.Body.SetControlInput(Vector2.Zero, jump: true);
            var hands = Bones(s, b => b.Role == PartRole.Arm).Count(a => s.Rig.Bones[a].Children.Any(c => PartRoles.IsHand(s.Rig.Bones[c].Role)));
            Report(climbed[s.Id] > 40 && raised[s.Id] > 0.004f && (hands < 2 || crossings[s.Id] >= 1),
                $"{s.Id} climbing ({climbed[s.Id]} frames): the hands reach up {Mm(raised[s.Id])} above the shoulders, hand over hand ({crossings[s.Id]} changes)");
            Report(Mathf.Abs(gap[s.Id]) < 0.004f && legsForward[s.Id] > 8,
                $"{s.Id} climbing: the body's front {Mm(gap[s.Id])} from the face, the feet pressed to it ({legsForward[s.Id]:0}°)");
        }
        await Frames(2);
        foreach (var s in Walkers) s.Body.SetControlInput(Vector2.Zero);
        await Frames(60);
    }

    /// <summary>Carrying raises closed hands toward the load over the head.</summary>
    private async Task TestCarry()
    {
        var restAbove = new Dictionary<string, float>();
        foreach (var s in _subjects)
        {
            Place(s.Body, s.Course.Flat, 0);
            s.Body.HandsFull = () => true;
        }
        await Frames(45);
        foreach (var s in _subjects)
        {
            var worst = float.MaxValue;
            var curl = float.MaxValue;
            var arms = 0;
            foreach (var arm in Bones(s, b => b.Role == PartRole.Arm))
            {
                var hand = s.Rig.Bones[arm].Children.FirstOrDefault(c => PartRoles.IsHand(s.Rig.Bones[c].Role), -1);
                if (hand < 0) continue;
                arms++;
                worst = Mathf.Min(worst, World(s, hand).Origin.Y - World(s, arm).Origin.Y);
                curl = Mathf.Min(curl, Posed(s, hand));
            }
            Report(s.Contact.CarryWeight > 0.95f && (arms == 0 || (worst > 0.003f && curl > 20)),
                arms == 0 ? $"{s.Id} carries on its head (no hands)" : $"{s.Id} carrying: the hands rise {Mm(worst)} above the shoulders, closed ({curl:0}°)");
            s.Body.HandsFull = null;
        }
        await Frames(45);
        foreach (var s in _subjects) Check(s.Contact.CarryWeight < 0.05f, $"{s.Id}: putting the load down lowers the hands");
    }

    /// <summary>The world's wind reaches the springs: hats, hair, sparkles and dangling parts lean downwind.</summary>
    private async Task TestWind()
    {
        foreach (var s in _subjects) Place(s.Body, s.Course.Flat, 0);
        await Frames(30);
        // The tips of the chains whose springs run (planted legs' springs are muted while the IK holds them).
        // Each running chain's reach from its root joint to its tip (planted legs' springs are muted while the IK holds them).
        Dictionary<string, Vector3[]> Tips() => _subjects.ToDictionary(s => s.Id, s => s.Rig.Secondary.Chains.Where(c => c.Simulator.Active)
            .Select(c => Tail(s, c) - s.Rig.FinalBoneTransform(c.RootBone).Origin).ToArray());
        var calm = Tips();
        foreach (var s in _subjects) Check(s.Body.SetWorldPhysics(Preset("room_breeze_test", s.Body.WorldPhysicsRevision + 1)), $"{s.Id}: a breeze for the wind test");
        await Frames(60);
        var windy = Tips();
        foreach (var s in _subjects)
        {
            var simulator = s.Rig.Secondary.Simulator ?? s.Rig.Secondary.LegSimulator;
            if (simulator == null)
            {
                Check(true, $"{s.Id}: no springs for the wind");
                continue;
            }
            var force = simulator.ExternalForce;
            var swinging = calm[s.Id].Length;
            var downwind = Enumerable.Range(0, swinging).Sum(i => windy[s.Id][i].X - calm[s.Id][i].X);
            var lean = swinging == 0 ? 0 : Enumerable.Range(0, swinging).Average(i => Mathf.RadToDeg(calm[s.Id][i].AngleTo(windy[s.Id][i])));
            // A gentle breeze leans soft parts a few degrees and stiff ones (a held wand, sparkles on K3's stiffer springs) about one:
            // the wind must reach every swinging chain downwind, by at least a degree on average (3 before K3's retune, 13 October).
            Report(force.IsEqualApprox(ContactLayer.WindForce(new Vector2(0.4f, 0), 1f / 60)) && force.X > 0 && (swinging == 0 || (downwind > 0 && lean > 1 && lean < 45)),
                FormattableString.Invariant($"{s.Id} in a 0.4 m/s breeze along +X: the springs' external force is {force}; {swinging} swinging chains lean {lean:0.0}° on average, their tips {downwind * 1000:0.0} mm downwind in all"));
        }
        foreach (var s in _subjects) Check(s.Body.SetWorldPhysics(Preset("room_tuned", s.Body.WorldPhysicsRevision + 1)), $"{s.Id}: calm again");
        await Frames(30);
        foreach (var s in _subjects)
            Check((s.Rig.Secondary.Simulator ?? s.Rig.Secondary.LegSimulator) is not { } simulator || simulator.ExternalForce == Vector3.Zero, $"{s.Id}: no wind, no push on the springs");
    }

    /// <summary>
    /// Walking, the face looks where it goes and glances at a point of interest circling it, never past the rig's limits; a
    /// falling point is watched.
    /// </summary>
    private async Task TestLook()
    {
        var worstHead = new Dictionary<string, (float Yaw, float Pitch)>();
        var worstEye = new Dictionary<string, (float Yaw, float Pitch)>();
        var turned = new Dictionary<string, float>();
        var glances = new Dictionary<string, int>();
        var ahead = new Dictionary<string, float>();
        var aheadFrames = new Dictionary<string, int>();
        var sinceGlance = new Dictionary<string, int>();
        foreach (var s in _subjects)
        {
            Place(s.Body, s.Course.LookStart, 0);
            sinceGlance[s.Id] = 0;
            worstHead[s.Id] = worstEye[s.Id] = (0, 0);
            turned[s.Id] = 0;
            glances[s.Id] = aheadFrames[s.Id] = 0;
            ahead[s.Id] = 0;
        }
        await Frames(5);
        foreach (var s in _subjects) s.Body.SetControlInput(new Vector2(0, 1));
        for (var f = 0; f < 360; f++)
        {
            var angle = f / 60f * 1.3f;
            foreach (var s in _subjects)
                s.Interest = s.Body.GlobalPosition + new Vector3(0.4f * Mathf.Sin(angle), 0.08f, -0.4f * Mathf.Cos(angle));
            await Frames(1);
            if (f < 30) continue;
            foreach (var s in _subjects)
            {
                var look = s.Rig.Look;
                if (look.Head != null)
                {
                    var (yaw, pitch) = Relative(s, look.Head.Bone);
                    worstHead[s.Id] = (Mathf.Max(worstHead[s.Id].Yaw, Mathf.Abs(yaw)), Mathf.Max(worstHead[s.Id].Pitch, Mathf.Abs(pitch)));
                    turned[s.Id] = Mathf.Max(turned[s.Id], Mathf.Abs(yaw));
                }
                foreach (var eye in look.Eyes)
                {
                    var (yaw, pitch) = Relative(s, eye.Bone);
                    worstEye[s.Id] = (Mathf.Max(worstEye[s.Id].Yaw, Mathf.Abs(yaw)), Mathf.Max(worstEye[s.Id].Pitch, Mathf.Abs(pitch)));
                    if (look.Head == null) turned[s.Id] = Mathf.Max(turned[s.Id], Mathf.Abs(yaw));
                }
                if (s.Look.Glancing)
                {
                    glances[s.Id]++;
                    sinceGlance[s.Id] = 0;
                }
                else if (++sinceGlance[s.Id] > 30)
                {
                    var travel = new Vector3(s.Body.Velocity.X, 0, s.Body.Velocity.Z);
                    var direction = new Vector3(s.Look.Direction.X, 0, s.Look.Direction.Z);
                    if (travel.Length() > 0.1f && direction.Length() > 0.1f)
                    {
                        ahead[s.Id] = Mathf.Max(ahead[s.Id], Mathf.RadToDeg(travel.AngleTo(direction)));
                        aheadFrames[s.Id]++;
                    }
                }
            }
        }
        foreach (var s in _subjects)
        {
            s.Body.SetControlInput(Vector2.Zero);
            var look = s.Rig.Look;
            var active = (look.Head == null || look.Head.Modifier.Active) && look.Eyes.All(e => e.Modifier.Active) && s.Look.Active > 0.99f;
            var headLimit = look.Head != null ? (Mathf.RadToDeg(look.Head.YawLimitRad) + 3, Mathf.RadToDeg(look.Head.PitchLimitRad) + 3) : (0f, 0f);
            var eyeLimit = (Mathf.RadToDeg(LookRig.EyeYawLimit) + 3, Mathf.RadToDeg(LookRig.EyePitchLimit) + 3);
            Report(active && worstHead[s.Id].Yaw <= headLimit.Item1 && worstHead[s.Id].Pitch <= headLimit.Item2 && worstEye[s.Id].Yaw <= eyeLimit.Item1 && worstEye[s.Id].Pitch <= eyeLimit.Item2,
                $"{s.Id}'s look-ats are on and within the rig's limits: {(look.HeadIsBody ? "the body as head" : "the head")} at most {worstHead[s.Id].Yaw:0.0}° yaw, {worstHead[s.Id].Pitch:0.0}° pitch (limits {headLimit.Item1 - 3:0}°, {headLimit.Item2 - 3:0}°); the eyes {worstEye[s.Id].Yaw:0.0}°, {worstEye[s.Id].Pitch:0.0}° (limits {eyeLimit.Item1 - 3:0}°, {eyeLimit.Item2 - 3:0}°)");
            // The bigger Gubble (its torch a prop since 13 October) glances longer: 40 frames ahead is still two-thirds of a second.
            Report(glances[s.Id] > 20 && turned[s.Id] > 5 && aheadFrames[s.Id] > 40 && ahead[s.Id] < 20,
                $"{s.Id} walking looks where it goes (within {ahead[s.Id]:0.0}° of the travel over {aheadFrames[s.Id]} frames) and glances at the point of interest ({glances[s.Id]} frames, turning up to {turned[s.Id]:0.0}°)");
        }

        // A point of interest falling fast (a player off a ledge) is watched, and for a while after it lands.
        var start = _subjects.ToDictionary(s => s.Id, s => s.Body.GlobalPosition + new Vector3(0.3f, 0.5f, -0.3f));
        for (var f = 0; f < 12; f++)
        {
            foreach (var s in _subjects) s.Interest = start[s.Id] + Vector3.Down * (2.0f * f / 60f);
            await Frames(1);
        }
        await Frames(60);
        foreach (var s in _subjects)
            Check(s.Look.Watching && s.Look.Glancing, $"{s.Id} watches a point of interest that fell (a tumbling player) and keeps watching after it lands");
        foreach (var s in _subjects) s.Interest = null;
        await Frames(5);
    }

    /// <summary>The tip of a spring chain (its end bone's virtual tail), in the body's space.</summary>
    private static Vector3 Tail(Subject s, SpringChain chain)
    {
        var axis = chain.TailDirection switch
        {
            SkeletonModifier3D.BoneDirection.PlusX => Vector3.Right, SkeletonModifier3D.BoneDirection.MinusX => Vector3.Left,
            SkeletonModifier3D.BoneDirection.PlusY => Vector3.Up, SkeletonModifier3D.BoneDirection.MinusY => Vector3.Down,
            SkeletonModifier3D.BoneDirection.PlusZ => Vector3.Back, _ => Vector3.Forward,
        };
        return s.Rig.FinalBoneTransform(chain.EndBone) * (axis * chain.TailLengthM);
    }

    /// <summary>A bone's final turn relative to its parent's (yaw and pitch of its forward, degrees).</summary>
    private static (float Yaw, float Pitch) Relative(Subject s, int bone)
    {
        var parent = s.Rig.Bones[bone].Parent;
        var p = s.Rig.FinalBoneTransform(parent).Basis.Orthonormalized();
        var b = s.Rig.FinalBoneTransform(bone).Basis.Orthonormalized();
        var forward = (p.Transposed() * b) * Vector3.Forward;
        return (Mathf.RadToDeg(Mathf.Atan2(forward.X, -forward.Z)), Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(forward.Y, -1, 1))));
    }
}
