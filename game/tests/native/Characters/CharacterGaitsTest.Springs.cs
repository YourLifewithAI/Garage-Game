using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Characters;
using GarageGame.Native.Characters.Gaits;

namespace GarageGame.Tests.Characters;

/// <summary>The engine's springs under the gaits, in the tree (RIG-API.md section 4 asked K3 to check them under the squash).</summary>
public partial class CharacterGaitsTest
{
    /// <summary>
    /// Springs K1's suite pins to their starting tuning (Cloudpuff's legs at 0.7 of their pendulum, Potato Man's hat at 0.8),
    /// which stay floppy when the body travels; the K3 report proposes the retune. Every other spring must stay calm.
    /// </summary>
    private static readonly HashSet<string> PinnedSprings = new();

    /// <summary>Cancels the root's squash and stretch (a test layer after the gait), to see what the squash itself does to the springs.</summary>
    private sealed class NoSquash : IPoseLayer<BodyState>
    {
        public PoseLayer Layer => PoseLayer.Contact;
        public void Apply(PoseBuffer pose, in BodyState state, in PoseClock clock)
        {
            var scale = pose.GetScale(CharacterBody.RootBone);
            pose.MultiplyScale(CharacterBody.RootBone, new Vector3(1 / scale.X, 1 / scale.Y, 1 / scale.Z));
        }
    }

    /// <summary>One run's springs, by "character/root part": the largest swing, the swing once settled (radians); each body's lowest point.</summary>
    private sealed record SpringRun(List<string> Names, List<bool> Solid, List<float> Swing, List<float> Settled, bool Finite, Dictionary<string, float> Lowest);

    /// <summary>
    /// The five in the tree through the acceptance script, carried through the world as the capsule would carry them
    /// (<paramref name="travel"/>) or held in place. Each spring's swing is measured on its own joint: its final turn
    /// relative to its parent's final turn, against the turn the layers gave it (the skeleton shows the last frame's pose).
    /// </summary>
    private async Task<SpringRun> RunSprings(string name, float bounce, bool travel, bool squash, System.Action<CharacterVisual>? prepare = null)
    {
        var holder = new Node3D { Name = name, Position = new Vector3(5, 0, 0) };
        AddChild(holder);
        var visuals = new List<CharacterVisual>();
        foreach (var id in Shipped)
        {
            var (visual, _, _) = Installed(id);
            visual.Body.Profile.BounceScale = bounce;
            if (!squash) visual.Stack.Add(new NoSquash());
            prepare?.Invoke(visual);
            holder.AddChild(visual.Body);
            visuals.Add(visual);
        }
        var swing = visuals.Select(v => new float[v.Body.Secondary.Chains.Count]).ToArray();
        var settled = visuals.Select(v => new float[v.Body.Secondary.Chains.Count]).ToArray();
        var lowest = Enumerable.Repeat(float.MaxValue, visuals.Count).ToArray();
        var posed = visuals.Select(v => new Quaternion[v.Body.BoneCount]).ToArray();
        var finite = true;
        for (var frame = 0; frame < Frames600; frame++)
        {
            if (travel) holder.Transform = new Transform3D(new Basis(Vector3.Up, _script[frame].Yaw), new Vector3(5, 0, 0) + _script[frame].Position);
            for (var i = 0; i < visuals.Count; i++)
            {
                var body = visuals[i].Body;
                if (frame > 2)
                {
                    var chains = body.Secondary.Chains;
                    for (var c = 0; c < chains.Count; c++)
                    {
                        var bone = chains[c].RootBone;
                        var parent = body.Bones[bone].Parent;
                        var relative = body.FinalBoneTransform(parent).Basis.GetRotationQuaternion().Inverse() * body.FinalBoneTransform(bone).Basis.GetRotationQuaternion();
                        var angle = (relative * posed[i][bone].Inverse()).Normalized().GetAngle();
                        angle = Mathf.Min(angle, Mathf.Tau - angle);
                        swing[i][c] = Mathf.Max(swing[i][c], angle);
                        // The last 40 of the 120 frames standing still (the stop ends at frame 440).
                        if (frame > 400 && frame < 440) settled[i][c] = Mathf.Max(settled[i][c], angle);
                    }
                    foreach (var bone in body.Bones)
                    {
                        var t = body.FinalBoneTransform(bone.Index);
                        finite &= t.Origin.IsFinite() && t.Basis.X.IsFinite() && t.Basis.Y.IsFinite() && t.Basis.Z.IsFinite();
                    }
                    lowest[i] = Mathf.Min(lowest[i], body.FinalLowestPointY());
                }
                visuals[i].Stack.Run(_script[frame], Clock(frame));
                for (var b = 0; b < body.BoneCount; b++) posed[i][b] = body.Pose.GetRotation(b);
            }
            await Frames(1);
        }
        var run = new SpringRun(new List<string>(), new List<bool>(), new List<float>(), new List<float>(), finite, new Dictionary<string, float>());
        for (var i = 0; i < visuals.Count; i++)
        {
            var body = visuals[i].Body;
            run.Lowest[body.CharacterId] = lowest[i];
            for (var c = 0; c < body.Secondary.Chains.Count; c++)
            {
                run.Names.Add($"{body.CharacterId}/{body.Bones[body.Secondary.Chains[c].RootBone].Name}");
                run.Solid.Add(body.Bones[body.Secondary.Chains[c].RootBone].Kind == PartKind.Solid);
                run.Swing.Add(swing[i][c]);
                run.Settled.Add(settled[i][c]);
            }
        }
        holder.QueueFree();
        await Frames(1);
        return run;
    }

    /// <summary>
    /// Carried through the world as in the game, every solid part K1's suite does not pin stays within 70° of its pose (it lags but never flips) and settles
    /// once the body stands; at the bounce key's 1.5, with the root's squash at its 15% cap, none flips either.
    /// </summary>
    private async Task TestSpringsInTree()
    {
        var game = await RunSprings("SpringsTravelling", 1, travel: true, squash: true);
        Check(game.Finite, "600 frames in the tree, carried through the world, leave every bone finite after the springs");
        var lines = new List<string>();
        for (var i = 0; i < game.Names.Count; i++)
        {
            var name = game.Names[i];
            var swing = Mathf.RadToDeg(game.Swing[i]);
            var settled = Mathf.RadToDeg(game.Settled[i]);
            lines.Add(F($"{name} {swing:0}°/{settled:0}°"));
            // Effects (a flame, sparkles) may stream and spin; solid parts must not flip.
            if (PinnedSprings.Contains(name) || !game.Solid[i]) continue;
            Check(swing <= 70 && settled <= 12, F($"{name}: carried through the walk, run, stop and turn, its spring lags at most {swing:0.0}° from the pose (never flips) and settles to {settled:0.0}° once the body stands"));
        }
        GD.Print("GAITS_SPRINGS: each spring's largest swing from its pose and its swing once settled, carried through the script at bounce x1: " + string.Join(", ", lines) +
            "; lowest point after the springs: " + string.Join(", ", game.Lowest.Select(pair => F($"{pair.Key} {pair.Value * 1000:0.0} mm"))));

        // The squash's own effect: the same script at the bounce key's 1.5 (every round body squashes at the 15% cap), held in
        // place, with the squash and with it cancelled. The springs ride a non-uniformly scaled parent; they must still not flip.
        var squashed = await RunSprings("SpringsSquashed", 1.5f, travel: false, squash: true);
        var flat = await RunSprings("SpringsUnsquashed", 1.5f, travel: false, squash: false);
        Check(squashed.Finite && flat.Finite, "at bounce x1.5, with and without the squash, every bone stays finite");
        var compared = new List<string>();
        for (var i = 0; i < squashed.Names.Count; i++)
        {
            var name = squashed.Names[i];
            var swing = Mathf.RadToDeg(squashed.Swing[i]);
            var settled = Mathf.RadToDeg(squashed.Settled[i]);
            compared.Add(F($"{name} {swing:0}° against {Mathf.RadToDeg(flat.Swing[i]):0}°"));
            if (PinnedSprings.Contains(name) || !squashed.Solid[i]) continue;
            Check(swing <= 70 && settled <= 12, F($"{name}: at bounce x1.5 with the root's squash at its 15% cap, its spring still lags at most {swing:0.0}° ({Mathf.RadToDeg(flat.Swing[i]):0.0}° without the squash) and settles to {settled:0.0}°"));
        }
        GD.Print("GAITS_SQUASH: each spring's largest swing at bounce x1.5 held in place, with the root's squash against without it: " + string.Join(", ", compared));
    }
}
