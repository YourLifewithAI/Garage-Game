using Godot;

namespace GarageGame.Native.Characters.Gaits;

/// <summary>What happened under the body this tick, for a later sound packet (the plan's decision 8: sounds later).</summary>
public enum GaitEventKind
{
    /// <summary>A foot (or, for a hop, both feet) came down in the gait's own rhythm.</summary>
    Footfall,
    /// <summary>The body came down from the air onto a floor (BodyState.Landed, not into deep water).</summary>
    Landing,
    /// <summary>The body came down into deep water, or started to swim.</summary>
    Splash,
}

/// <summary>
/// One gait event. <see cref="FootBone"/> is the bone that came down, or -1 for the whole body (a hop on both feet, a
/// landing, a splash); <see cref="SpeedMps"/> is the body's speed then (for a landing or a splash, the downward landing
/// speed fall damage reads). <see cref="Wet"/>: the body stood in water (a footfall that should splash).
/// </summary>
public readonly record struct GaitEvent(GaitEventKind Kind, int FootBone, BodySide Side, float SpeedMps, Vector3 Position, bool Wet, ulong Tick);

/// <summary>A gait event's handler; the event is passed by reference, so listening allocates nothing.</summary>
public delegate void GaitEventHandler(in GaitEvent gaitEvent);
