using System.Globalization;
using System.Text;
using Godot;

namespace GarageGame.Native.Characters.Gaits;

/// <summary>
/// The tuning keys' logic (docs/runs/RUN-2-CHARACTERS.md, "Tuning by playing"): one cycles the current character's overall
/// bounce through 0.5×, 1× and 1.5× of its profile, the other prints every value of the profile, so the family can try
/// the three and the integrator can write their choice into the character's rig.json (profile.bounce). Binding the keys
/// is the HUD's (see the K3 report for the input map lines).
/// </summary>
public static class BounceKey
{
    /// <summary>The scales the key steps through, in order.</summary>
    public static readonly float[] Scales = { 0.5f, 1f, 1.5f };

    /// <summary>Steps <paramref name="profile"/>'s bounce scale to the next of 0.5, 1 and 1.5 (after 1.5, 0.5) and returns it.</summary>
    public static float Cycle(MotionProfile profile)
    {
        var current = profile.BounceScale;
        var nearest = 0;
        for (var i = 1; i < Scales.Length; i++)
            if (Mathf.Abs(Scales[i] - current) < Mathf.Abs(Scales[nearest] - current)) nearest = i;
        profile.BounceScale = Scales[(nearest + 1) % Scales.Length];
        return profile.BounceScale;
    }

    public static float Cycle(CharacterBody body) => Cycle(body.Profile);

    /// <summary>The key on whatever draws the body: the new scale, or null for the prototype figure (nothing to tune).</summary>
    public static float? Cycle(IBodyVisual? visual) => visual is CharacterVisual character ? Cycle(character.Body) : null;

    /// <summary>
    /// Every value of the body's motion profile: the measurements, every rig.json multiplier, the bounce scale and what it
    /// makes, the stride and cadence at a walk and a run, each spring, and the installed gait's own sizes.
    /// </summary>
    public static string Describe(CharacterBody body, GaitLayer? gait = null)
    {
        var p = body.Profile;
        var c = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.Append(c, $"{body.CharacterId}: motion {p.Motion.ToString().ToLowerInvariant()}, gait {p.Gait.ToString().ToLowerInvariant()}");
        text.Append(c, $"; height {p.HeightM:0.0000} m, width {p.WidthM:0.0000} m, depth {p.DepthM:0.0000} m");
        text.Append(c, $"; body {p.BodyWidthM:0.0000} x {p.BodyDepthM:0.0000} x {p.BodyHeightM:0.0000} m");
        text.Append(c, $"; legs {p.LegCount}, hip {p.HipHeightM:0.0000} m; feet {p.FootCount}, foot {p.FootLengthM:0.0000} m, spacing {p.FootSpacingM:0.0000} m");
        text.Append(c, $"; pendulum {p.LegLengthM:0.0000} m, cadence {p.NaturalCadenceHz:0.000} Hz, reach {p.ReachM:0.0000} m, walk stride {p.WalkStrideM:0.0000} m");
        text.Append(c, $"; roundness {p.Roundness:0.000}, volume {p.VolumeM3 * 1e6:0.0} cm3, squash amount {p.SquashAmount:0.0000}, settle time {p.SettleTimeS:0.000} s");
        foreach (var (key, get) in ProfileMultipliers.Fields) text.Append(c, $"; {key} x{get(p.Multipliers):0.###}");
        text.Append(c, $"; bounce scale {p.BounceScale:0.##} (key: 0.5, 1, 1.5) -> bounce {p.Bounce:0.###}, squash {p.Squash:0.0000}, settle {p.SettleS:0.000} s");
        text.Append(c, $"; walk: stride {p.StrideAt(MotionProfile.WalkSpeedMps):0.0000} m at {p.CadenceAt(MotionProfile.WalkSpeedMps):0.000} Hz");
        text.Append(c, $"; run: stride {p.StrideAt(MotionProfile.RunSpeedMps):0.0000} m at {p.CadenceAt(MotionProfile.RunSpeedMps):0.000} Hz");
        foreach (var hanging in p.HangingParts)
            text.Append(c, $"; spring {body.Bones[hanging.RootBone].Name} {hanging.LengthM:0.0000} m, pendulum {hanging.PendulumHz:0.00} Hz, tuned {hanging.FrequencyHz:0.00} Hz");
        switch (gait?.Gait)
        {
            case FloatGait f:
                text.Append(c, $"; float: bob ±{f.BobAmplitudeM * 1000:0.0} mm over {FloatGait.BobPeriodS:0.0} s");
                break;
            case HopGait h:
                text.Append(c, $"; hop: height {h.HopHeightM(0) * 1000:0.0} mm walking, {h.HopHeightM(1) * 1000:0.0} mm running, squash {h.SquashDepth * 100:0.0}%");
                break;
            case WaddleGait w:
                text.Append(c, $"; waddle: roll ±{Mathf.RadToDeg(w.RollRad(0)):0.0}° walking, ±{Mathf.RadToDeg(w.RollRad(1)):0.0}° running");
                break;
            case StrideGait s:
                text.Append(c, $"; stride: leg {s.LegLengthM * 1000:0.0} mm, flight {s.FlightM(0) * 1000:0.0} mm walking, {s.FlightM(1) * 1000:0.0} mm running");
                break;
        }
        if (gait != null) text.Append(c, $"; lean now {Mathf.RadToDeg(gait.LeanPitchRad):0.0}° forward, {Mathf.RadToDeg(gait.LeanRollRad):0.0}° right");
        return text.ToString();
    }

    /// <summary>Prints <see cref="Describe"/> to the log (the second key) and returns it.</summary>
    public static string Print(CharacterBody body, GaitLayer? gait = null)
    {
        var text = "BOUNCE " + Describe(body, gait);
        GD.Print(text);
        return text;
    }

    /// <summary>The print key on whatever draws the body; null for the prototype figure.</summary>
    public static string? Print(IBodyVisual? visual) =>
        visual is CharacterVisual character ? Print(character.Body, CharacterGaits.FindGait(character.Stack)) : null;
}
