using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;

namespace GarageGame.Native.Characters;

/// <summary>A part that swings on a spring chain, with the pendulum it is tuned to.</summary>
public readonly record struct HangingPart(int Chain, int RootBone, int EndBone, float LengthM, float PendulumHz, float FrequencyHz);

/// <summary>
/// A character's own sizes for every gait and spring, measured from its geometry when the rig is built
/// (docs/runs/RUN-2-CHARACTERS.md, "Per-character motion profiles"), then scaled by rig.json's named multipliers and
/// the testing key's <see cref="BounceScale"/>. Measurements are fixed; only <see cref="BounceScale"/> changes at runtime.
/// </summary>
public sealed class MotionProfile
{
    /// <summary>The game's tuned gravity (SmallPlayerController's room default), which sets every pendulum.</summary>
    public const float GravityMps2 = 3.5f;
    /// <summary>The shared capsule's walk and run speeds (SmallPlayerController).</summary>
    public const float WalkSpeedMps = 0.32f;
    public const float RunSpeedMps = 0.96f;
    /// <summary>A planted foot sweeps ±30° about the hip, so it covers one leg length per step.</summary>
    public const float StanceSweepRad = Mathf.Pi / 6;

    private readonly List<HangingPart> _hanging = new();

    public MotionKind Motion { get; private init; }
    /// <summary>The gait the motion falls back to: a stride with no legs becomes a waddle; the rest stay as hinted.</summary>
    public MotionKind Gait { get; private init; }

    /// <summary>The whole character at rest, from its meshes (metres).</summary>
    public float HeightM { get; private init; }
    public float WidthM { get; private init; }
    public float DepthM { get; private init; }
    /// <summary>The body part's own mesh (the part with the body role).</summary>
    public float BodyWidthM { get; private init; }
    public float BodyDepthM { get; private init; }
    public float BodyHeightM { get; private init; }

    public int LegCount { get; private init; }
    public bool HasLegs => LegCount > 0;
    /// <summary>Mean height of the hip joints above the ground (0 with no legs). Stickbear 0.043 m, Cloudpuff 0.030 m.</summary>
    public float HipHeightM { get; private init; }
    public int FootCount { get; private init; }
    /// <summary>Mean length of the feet or shoes along their longer horizontal extent (0 with none).</summary>
    public float FootLengthM { get; private init; }
    /// <summary>Distance between the right and left feet (or legs) across the body; 0 unless both sides have one.</summary>
    public float FootSpacingM { get; private init; }

    /// <summary>
    /// The pendulum length that sets the gait's tempo: the hip height when there are legs; otherwise the feet's or
    /// shoes' length (legless bodies step by their footprint); otherwise half the body's depth.
    /// </summary>
    public float LegLengthM { get; private init; }
    /// <summary>The legs' natural swing, a pendulum's √(g/L)/2π: one full stride cycle (two steps). Stickbear about 1.44 Hz.</summary>
    public float NaturalCadenceHz { get; private init; }
    /// <summary>How far a planted foot carries the body per step (2·L·sin 30° = L); farther travel between steps is flight.</summary>
    public float ReachM { get; private init; }
    /// <summary>The distance of one full stride cycle at a walk: the walk speed over the natural cadence.</summary>
    public float WalkStrideM { get; private init; }

    /// <summary>0 (flat, lanky) to 1 (full, round): the body's narrower horizontal size against 65% of the height.</summary>
    public float Roundness { get; private init; }
    /// <summary>Σ of the solid parts' bounding ellipsoids (m³): the weight proxy.</summary>
    public float VolumeM3 { get; private init; }
    /// <summary>The largest squash (fraction of height) at a hard landing, from roundness: 0.02 + 0.12·roundness.</summary>
    public float SquashAmount { get; private init; }
    /// <summary>Time to settle after a landing or turn, from weight: 0.12 s + 3 s/m·∛volume.</summary>
    public float SettleTimeS { get; private init; }

    public ProfileMultipliers Multipliers { get; private init; } = ProfileMultipliers.Default;

    private float _bounceScale = 1;

    /// <summary>The testing key's scale on the bounce (0.5, 1, 1.5); finite and from 0 to 3.</summary>
    public float BounceScale
    {
        get => _bounceScale;
        set => _bounceScale = float.IsFinite(value) ? Mathf.Clamp(value, 0, 3) : 1;
    }

    /// <summary>The overall bounce: rig.json's bounce times <see cref="BounceScale"/>.</summary>
    public float Bounce => Multipliers.Bounce * BounceScale;
    /// <summary>The squash a gait should use: measured, times rig.json's squash, times the bounce.</summary>
    public float Squash => SquashAmount * Multipliers.Squash * Bounce;
    public float SettleS => SettleTimeS * Multipliers.Settle;

    /// <summary>Each spring chain with its length and frequency.</summary>
    public IReadOnlyList<HangingPart> HangingParts => _hanging;

    /// <summary>A pendulum's frequency in the game's gravity: √(g/L)/2π.</summary>
    public static float PendulumHz(float lengthM) => Mathf.Sqrt(GravityMps2 / Mathf.Max(lengthM, 0.002f)) / Mathf.Tau;

    /// <summary>
    /// The stride cycle's length at <paramref name="speedMps"/>: the walk stride times rig.json's stride, lengthened
    /// at a run by √(speed/walk) (a run lengthens the stride; the cadence rises by the same factor).
    /// Phase a gait by distance: cycles += distance / StrideAt(speed).
    /// </summary>
    public float StrideAt(float speedMps)
    {
        var ratio = Mathf.Max(Mathf.Abs(speedMps), 0.05f * WalkSpeedMps) / WalkSpeedMps;
        return WalkStrideM * Multipliers.Stride * Mathf.Sqrt(ratio);
    }

    /// <summary>Stride cycles per second at <paramref name="speedMps"/> (speed / <see cref="StrideAt"/>).</summary>
    public float CadenceAt(float speedMps) => Mathf.Abs(speedMps) / StrideAt(speedMps);

    internal void AddHanging(HangingPart part) => _hanging.Add(part);

    internal static MotionProfile Measure(MotionKind motion, IReadOnlyList<RigBone> bones, ProfileMultipliers multipliers)
    {
        var low = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var high = -low;
        var volume = 0.0;
        RigBone? body = null;
        float hips = 0, footLength = 0;
        int legs = 0, feet = 0;
        float rightX = 0, leftX = 0;
        int rights = 0, lefts = 0;
        foreach (var bone in bones)
        {
            if (bone.Part < 0) continue;
            if (bone.HasMesh)
            {
                low = low.Min(bone.RestModel + bone.MeshBounds.Position);
                high = high.Max(bone.RestModel + bone.MeshBounds.End);
                if (bone.Kind == PartKind.Solid)
                    volume += Math.PI / 6 * bone.MeshBounds.Size.X * bone.MeshBounds.Size.Y * bone.MeshBounds.Size.Z;
            }
            if (bone.Role == PartRole.Body && body == null) body = bone;
            if (bone.Role == PartRole.Leg)
            {
                legs++;
                hips += bone.RestModel.Y;
            }
            if (PartRoles.IsFoot(bone.Role) && bone.HasMesh)
            {
                feet++;
                footLength += Mathf.Max(bone.MeshBounds.Size.X, bone.MeshBounds.Size.Z);
            }
            if (bone.Role == PartRole.Leg || (PartRoles.IsFoot(bone.Role) && bones[bone.Parent].Role != PartRole.Leg))
            {
                if (bone.Side == BodySide.Right) { rightX += bone.RestModel.X; rights++; }
                if (bone.Side == BodySide.Left) { leftX += bone.RestModel.X; lefts++; }
            }
        }
        if (low.X > high.X) { low = Vector3.Zero; high = Vector3.Zero; }
        body ??= FirstPart(bones);
        var bodySize = body is { HasMesh: true } ? body.MeshBounds.Size : high - low;
        var hip = legs > 0 ? hips / legs : 0;
        var foot = feet > 0 ? footLength / feet : 0;
        var leg = legs > 0 ? hip : feet > 0 ? foot : 0.5f * bodySize.Z;
        leg = Mathf.Max(leg, 0.005f);
        var cadence = PendulumHz(leg);
        var height = Mathf.Max(high.Y - low.Y, 1e-4f);
        var roundness = Mathf.Clamp(Mathf.Min(bodySize.X, bodySize.Z) / (0.65f * height), 0, 1);
        return new MotionProfile
        {
            Motion = motion,
            Gait = motion == MotionKind.Stride && legs == 0 ? MotionKind.Waddle : motion,
            HeightM = height, WidthM = high.X - low.X, DepthM = high.Z - low.Z,
            BodyWidthM = bodySize.X, BodyDepthM = bodySize.Z, BodyHeightM = bodySize.Y,
            LegCount = legs, HipHeightM = hip, FootCount = feet, FootLengthM = foot,
            FootSpacingM = rights > 0 && lefts > 0 ? Mathf.Abs(rightX / rights - leftX / lefts) : 0,
            LegLengthM = leg, NaturalCadenceHz = cadence, ReachM = 2 * leg * Mathf.Sin(StanceSweepRad), WalkStrideM = WalkSpeedMps / cadence,
            Roundness = roundness, VolumeM3 = (float)volume,
            SquashAmount = 0.02f + 0.12f * roundness,
            SettleTimeS = 0.12f + 3.0f * MathF.Cbrt((float)volume),
            Multipliers = multipliers,
        };
    }

    private static RigBone? FirstPart(IReadOnlyList<RigBone> bones)
    {
        foreach (var bone in bones)
            if (bone.Part >= 0) return bone;
        return null;
    }

    /// <summary>The profile as text, for the testing key that prints it and for logs.</summary>
    public string Describe(IReadOnlyList<RigBone>? bones = null)
    {
        var c = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.Append(c, $"motion {Motion.ToString().ToLowerInvariant()} (gait {Gait.ToString().ToLowerInvariant()}); ");
        text.Append(c, $"size {WidthM * 100:0.0} x {DepthM * 100:0.0} x {HeightM * 100:0.0} cm; body {BodyWidthM * 100:0.0} x {BodyDepthM * 100:0.0} x {BodyHeightM * 100:0.0} cm; ");
        text.Append(c, $"legs {LegCount} hip {HipHeightM * 100:0.00} cm; feet {FootCount} length {FootLengthM * 100:0.00} cm spacing {FootSpacingM * 100:0.00} cm; ");
        text.Append(c, $"pendulum {LegLengthM * 100:0.00} cm -> cadence {NaturalCadenceHz:0.00} Hz, walk stride {WalkStrideM * 100:0.0} cm, reach {ReachM * 100:0.00} cm; ");
        text.Append(c, $"roundness {Roundness:0.00}, volume {VolumeM3 * 1e6:0.0} cm3, squash {SquashAmount:0.000} (x{Multipliers.Squash:0.##}), settle {SettleTimeS:0.000} s (x{Multipliers.Settle:0.##}); ");
        text.Append(c, $"bounce {Multipliers.Bounce:0.##} x {BounceScale:0.##}");
        foreach (var (key, get) in ProfileMultipliers.Fields)
        {
            var value = get(Multipliers);
            if (key is "bounce" or "squash" or "settle" || Math.Abs(value - 1) < 1e-6f) continue;
            text.Append(c, $", {key} x{value:0.##}");
        }
        foreach (var hanging in _hanging)
        {
            var name = bones != null ? bones[hanging.RootBone].Name : hanging.RootBone.ToString(c);
            text.Append(c, $"; chain {name} {hanging.LengthM * 100:0.00} cm {hanging.PendulumHz:0.00} -> {hanging.FrequencyHz:0.00} Hz");
        }
        return text.ToString();
    }
}
