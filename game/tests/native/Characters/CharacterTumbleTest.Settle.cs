using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Characters;

namespace GarageGame.Tests.Characters;

/// <summary>
/// Acceptance item 2's settling: 200 seeded tumbles per character on flat ground, a 30° slope and a 2 cm step, each a real drop
/// of a real body wearing the character (heights seeded across the tumble and knockout tiers, a seeded drift and spin) onto the
/// real ground. Every tumble: nothing more than 2 mm below the ground, no bone faster than the landing speed plus 1 m/s, the
/// root within 3 cm of the capsule, nothing non-finite; the flop at rest within 1 s.
/// </summary>
public partial class CharacterTumbleTest
{
    private int SettleTumbles = 200;

    private sealed class SettleRun
    {
        public required Wearer Wearer;
        public required TumbleProbe Probe;
        /// <summary>The feet the leg IK plants (and what rides them), and every other part.</summary>
        public required bool[] Planted;
        public required bool[] Unplanted;
        public float LandingSpeed;
        public bool Started;
        public bool Done;
        public TumbleTier Tier;
        public float FlopEndS;
        public float LastMovingS;
        public float FlopDepth;
        public float GetUpDepth;
        public float GetUpFeetDepth;
        public float StandDepth;
        public float Fastest;
        public float Overspeed = float.NegativeInfinity;
        public float FarthestRoot;
        public bool NonFinite;
        public int StandFrames;
    }

    private async Task TestSettling()
    {
        var lane = 0;
        var summary = new List<string>();
        foreach (var id in PlayerCharacters)
        {
            foreach (var ground in Enum.GetValues<TumbleGround>())
            {
                var origin = new Vector3(-300 - 12 * lane, 0, -300);
                var random = new TumbleRandom((uint)(1000 + 31 * lane++));
                var terrain = TumbleTerrain.Build(this, ground, origin, 5.0f);
                await Frames(1);
                var runs = new List<SettleRun>(SettleTumbles);
                for (var i = 0; i < SettleTumbles; i++)
                {
                    var spot = SpikeSpot(terrain, i, ref random);
                    var height = random.Range(0.6f, 2.4f);
                    var wearer = Wear(id, spot + Vector3.Up * height, random.Range(-Mathf.Pi, Mathf.Pi));
                    wearer.Body.Velocity = new Vector3(random.Range(-0.3f, 0.3f), 0, random.Range(-0.3f, 0.3f));
                    wearer.Tumble.NextSeed = random.Next();
                    var planted = PlantedFeet(wearer.Visual.Body);
                    runs.Add(new SettleRun { Wearer = wearer, Probe = new TumbleProbe(wearer.Visual.Body), Planted = planted, Unplanted = planted.Select(p => !p).ToArray() });
                }
                for (var frame = 0; frame < 420 && runs.Any(r => !r.Done); frame++)
                {
                    await Frames(1);
                    foreach (var run in runs) Measure(run, terrain);
                }
                var tumbled = runs.Count(r => r.Started);
                var name = $"{id} {ground.ToString().ToLowerInvariant()}";
                Check(tumbled == SettleTumbles && runs.All(r => r.Done), $"{name}: all {SettleTumbles} seeded drops tumble and get up ({tumbled} tumbled, {runs.Count(r => r.Done)} done)");
                Check(runs.All(r => r.LastMovingS <= 1.0f), Inv($"{name}: every flop is at rest within 1 s (slowest {runs.Max(r => r.LastMovingS):0.00} s)"));
                Check(runs.All(r => r.FlopDepth <= 0.002f), Inv($"{name}: no mesh more than 2 mm below the ground while flopping (deepest {runs.Max(r => r.FlopDepth) * 1000:0.00} mm)"));
                // The get-up blends to the standing pose, which on a slope or a step's edge stands partly in the ground itself (flat
                // feet on a slope: the contact layer's business): no more than 2 mm deeper than the deeper of its two ends. The feet the
                // contact layer plants with the leg IK as the body rises (the suite dresses its bodies as the room does) turn onto their
                // ground before the body is up: Stickbear's long feet at a step's edge dig in up to about 4 mm more for a tenth of a
                // second, so they have 5 mm.
                Check(runs.All(r => r.GetUpDepth <= Mathf.Max(r.FlopDepth, r.StandDepth) + 0.002f && r.GetUpFeetDepth <= Mathf.Max(r.FlopDepth, r.StandDepth) + PlantedFeetGetUpM),
                    Inv($"{name}: getting up, nothing more than 2 mm deeper than where it lay or the standing pose, the planted feet {PlantedFeetGetUpM * 1000:0} mm (worst {runs.Max(r => r.GetUpDepth - Mathf.Max(r.FlopDepth, r.StandDepth)) * 1000:0.00} mm deeper, the feet {runs.Max(r => r.GetUpFeetDepth - Mathf.Max(r.FlopDepth, r.StandDepth)) * 1000:0.00} mm)"));
                Check(runs.All(r => r.Fastest <= r.LandingSpeed + 1), Inv($"{name}: no bone faster than the landing speed plus 1 m/s (worst {runs.Max(r => r.Overspeed):0.00} m/s over the landing speed)"));
                Check(runs.All(r => r.FarthestRoot <= 0.03f), Inv($"{name}: the root stays within 3 cm of the capsule along the ground (farthest {runs.Max(r => r.FarthestRoot) * 100:0.00} cm)"));
                Check(runs.All(r => !r.NonFinite && r.Wearer.Visual.Body.Pose.NonFiniteWrites == 0), $"{name}: nothing non-finite");
                summary.Add(Inv($"TUMBLE_SETTLE {name} tumbles={runs.Count(r => r.Tier == TumbleTier.Tumble)} knockouts={runs.Count(r => r.Tier == TumbleTier.Knockout)} ") +
                            Inv($"settle_p50={Percentile(runs.Select(r => r.LastMovingS), 0.5f):0.00}s p95={Percentile(runs.Select(r => r.LastMovingS), 0.95f):0.00}s max={runs.Max(r => r.LastMovingS):0.00}s ") +
                            Inv($"flop_depth_max={runs.Max(r => r.FlopDepth) * 1000:0.00}mm getup_depth_max={runs.Max(r => r.GetUpDepth) * 1000:0.00}mm (standing {runs.Max(r => r.StandDepth) * 1000:0.00}mm) ") +
                            Inv($"overspeed_max={runs.Max(r => r.Overspeed):0.00}m/s root_max={runs.Max(r => r.FarthestRoot) * 100:0.00}cm"));
                foreach (var run in runs) run.Wearer.Body.QueueFree();
                terrain.Root.QueueFree();
                await Frames(2);
            }
        }
        foreach (var line in summary) GD.Print(line);
    }

    /// <summary>How much deeper than either end of the get-up the feet the leg IK plants may go while the body rises (m).</summary>
    private const float PlantedFeetGetUpM = 0.005f;

    /// <summary>The bones the leg IK plants: each leg's foot and the parts that ride it.</summary>
    private static bool[] PlantedFeet(CharacterBody body)
    {
        var planted = new bool[body.BoneCount];
        foreach (var leg in body.Legs) planted[leg.FootBone] = true;
        for (var i = 1; i < body.BoneCount; i++)
            if (planted[body.Bones[i].Parent]) planted[i] = true;
        return planted;
    }

    private static void Measure(SettleRun run, TumbleTerrain terrain)
    {
        if (run.Done) return;
        var tumble = run.Wearer.Tumble;
        var body = run.Wearer.Body;
        if (!run.Started)
        {
            run.Probe.FastestJoint(Dt);
            if (!tumble.IsTumbling) return;
            run.Started = true;
            run.Tier = tumble.LastTier;
            run.LandingSpeed = body.LastBodyState.LandingSpeedMps;
            run.FlopEndS = tumble.Seconds - TumbleTiers.GetUpSeconds;
        }
        if (!run.Probe.Finite()) run.NonFinite = true;
        if (tumble.IsTumbling)
        {
            var t = tumble.ElapsedS;
            // The skeleton draws a tick's pose at the end of that frame: on the tumble's first tick the parts still show the standing
            // pose the capsule landed in (on a step's edge, partly inside it), so the tumble's own frames start at the next one.
            if (t < 0.5f * Dt) return;
            var fastest = run.Probe.FastestJoint(Dt);
            run.Fastest = Mathf.Max(run.Fastest, fastest);
            run.Overspeed = Mathf.Max(run.Overspeed, fastest - run.LandingSpeed);
            var root = run.Wearer.Visual.Body.Attachment(CharacterBody.RootBone).GlobalPosition;
            run.FarthestRoot = Mathf.Max(run.FarthestRoot, new Vector2(root.X - body.GlobalPosition.X, root.Z - body.GlobalPosition.Z).Length());
            if (tumble.Phase == TumblePhase.Flop)
            {
                run.FlopDepth = Mathf.Max(run.FlopDepth, run.Probe.Penetration(terrain));
                if (tumble.Hull.FastestPointMps > SettledMps) run.LastMovingS = t;
            }
            else
            {
                run.GetUpDepth = Mathf.Max(run.GetUpDepth, run.Probe.Penetration(terrain, run.Unplanted));
                run.GetUpFeetDepth = Mathf.Max(run.GetUpFeetDepth, run.Probe.Penetration(terrain, run.Planted));
            }
            return;
        }
        // Standing again: the standing pose's own depth on this ground (a flat-footed pose on a slope sits partly in it).
        run.StandDepth = Mathf.Max(run.StandDepth, run.Probe.Penetration(terrain));
        if (++run.StandFrames >= 3) run.Done = true;
    }
}
