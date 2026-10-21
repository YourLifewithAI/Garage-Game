using Godot;

namespace GarageGame.Native.Characters.Gaits;

/// <summary>
/// A body's pace as the gaits and the contact layers measure it (docs/runs/RUN-2-MECHANICS.md, M5): against the walk and the run
/// this body has now (<see cref="BodyState.WalkSpeedMps"/>, <see cref="BodyState.RunSpeedMps"/>: its base times its knack and the empty
/// bar's slow), not against the shared base the capsule starts from (<see cref="MotionProfile.WalkSpeedMps"/>). A body with Quick
/// feet that walks at its full pace is walking, not half way to a run; the Gubble, whose run is quicker than the player's, is running
/// at its own. A state built without the speeds (a test double) reads as the shared base, as every state did before.
/// </summary>
/// <remarks>
/// What stays on the base is the stride's own model, <see cref="MotionProfile.StrideAt"/>: a measurement of the drawn character at
/// the shared walk. Phase is by distance, so a faster body takes longer strides, more of them, and its feet never slide.
/// </remarks>
public static class BodyPace
{
    /// <summary>A run is always a little quicker than a walk, so the blend between them can divide.</summary>
    private const float MinRunOverWalk = 1.05f;

    /// <summary>The speed this body walks at now (m/s).</summary>
    public static float Walk(in BodyState state) => Read(state.WalkSpeedMps, MotionProfile.WalkSpeedMps);

    /// <summary>The speed this body runs at now (m/s), always above its walk.</summary>
    public static float Run(in BodyState state) => Mathf.Max(Read(state.RunSpeedMps, MotionProfile.RunSpeedMps), Walk(state) * MinRunOverWalk);

    /// <summary>0 at this body's walk to 1 at its run.</summary>
    public static float Blend(in BodyState state, float speedMps)
    {
        var walk = Walk(state);
        return Mathf.Clamp((speedMps - walk) / (Run(state) - walk), 0, 1);
    }

    /// <summary>How fast the body goes against its own run, up to 1.5 (a body being carried faster than it runs).</summary>
    public static float OfRun(in BodyState state, float speedMps) => Mathf.Clamp(speedMps / Run(state), 0, 1.5f);

    private static float Read(float value, float fallback) => float.IsFinite(value) && value > 0 ? value : fallback;
}
