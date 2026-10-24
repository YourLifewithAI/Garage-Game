using System;
using Godot;

namespace GarageGame.Native.Characters;

/// <summary>
/// The fixed order of the pose stack (docs/runs/RUN-2-CHARACTERS.md, "The pose stack"). Layers write into the
/// <see cref="PoseBuffer"/> in this order; <see cref="Secondary"/> is the skeleton's own modifiers, which run after
/// the buffer is applied, so no <see cref="IPoseLayer{TState}"/> may claim it.
/// </summary>
public enum PoseLayer
{
    /// <summary>The rest pose, before any layer.</summary>
    Rest = 0,
    /// <summary>1. The gait (K3): the base pose from distance travelled, speed and the motion kind.</summary>
    Gait = 1,
    /// <summary>2. Contact (K4): feet on the ground, climbing, swimming, carrying, landing squash.</summary>
    Contact = 2,
    /// <summary>3. Life (K3, K4): breathing, blinks, the look target.</summary>
    Life = 3,
    /// <summary>4. The skeleton's modifiers: springs, look-at, leg IK. Not a buffer layer.</summary>
    Secondary = 4,
    /// <summary>5. Gestures and overrides (K5): additive root motion, last.</summary>
    Gesture = 5,
    /// <summary>T. The tumble (K7): the get-up blend back to the standing pose.</summary>
    Tumble = 6,
}

/// <summary>The frame a pose is for: time since the stack started, this frame's delta, and a frame counter.</summary>
public readonly record struct PoseClock(double TimeS, float DeltaS, long Frame);

/// <summary>
/// One layer of the pose stack. <typeparamref name="TState"/> is the body's per-frame snapshot (in the game, the
/// controller's <c>BodyState</c>; in tests, anything). A layer must be a pure function of the snapshot, the clock and
/// state it keeps itself, write only the bones it owns (or add to others), and allocate nothing per frame.
/// </summary>
public interface IPoseLayer<TState> where TState : struct
{
    PoseLayer Layer { get; }
    void Apply(PoseBuffer pose, in TState state, in PoseClock clock);
}

/// <summary>
/// Runs a body's layers in the fixed order each frame: reset to rest, every layer that is not suspended, then the
/// skeleton. Layers of the same <see cref="PoseLayer"/> run in the order they were added.
/// </summary>
public sealed class PoseStack<TState> where TState : struct
{
    private IPoseLayer<TState>[] _layers = Array.Empty<IPoseLayer<TState>>();
    private readonly bool[] _suspended = new bool[(int)PoseLayer.Tumble + 1];

    public PoseStack(CharacterBody body)
    {
        Body = body ?? throw new ArgumentNullException(nameof(body));
    }

    public CharacterBody Body { get; }
    public int Count => _layers.Length;
    public IPoseLayer<TState> this[int index] => _layers[index];

    public void Add(IPoseLayer<TState> layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (layer.Layer is PoseLayer.Rest or PoseLayer.Secondary || !Enum.IsDefined(layer.Layer))
            throw new ArgumentException($"{layer.Layer} is not a layer a pose layer may claim");
        var position = _layers.Length;
        while (position > 0 && _layers[position - 1].Layer > layer.Layer) position--;
        var grown = new IPoseLayer<TState>[_layers.Length + 1];
        Array.Copy(_layers, grown, position);
        grown[position] = layer;
        Array.Copy(_layers, position, grown, position + 1, _layers.Length - position);
        _layers = grown;
    }

    public bool Remove(IPoseLayer<TState> layer)
    {
        var index = Array.IndexOf(_layers, layer);
        if (index < 0) return false;
        var shrunk = new IPoseLayer<TState>[_layers.Length - 1];
        Array.Copy(_layers, shrunk, index);
        Array.Copy(_layers, index + 1, shrunk, index, _layers.Length - index - 1);
        _layers = shrunk;
        return true;
    }

    /// <summary>Holds every layer of one kind (the tumble suspends Gait, Contact and Life while it owns the bones).</summary>
    public void Suspend(PoseLayer layer, bool suspended) => _suspended[(int)layer] = suspended;
    public bool IsSuspended(PoseLayer layer) => _suspended[(int)layer];

    /// <summary>Builds the frame's pose into <see cref="CharacterBody.Pose"/> without touching the skeleton.</summary>
    public void Evaluate(in TState state, in PoseClock clock)
    {
        var pose = Body.Pose;
        pose.Reset();
        foreach (var layer in _layers)
        {
            if (_suspended[(int)layer.Layer]) continue;
            pose.Begin(layer.Layer);
            layer.Apply(pose, state, clock);
        }
    }

    /// <summary><see cref="Evaluate"/>, then hands the pose to the skeleton and keeps the springs tuned to this frame's delta.</summary>
    public void Run(in TState state, in PoseClock clock)
    {
        Evaluate(state, clock);
        Body.ApplyPose();
        Body.Secondary.MatchTimestep(clock.DeltaS);
    }
}
