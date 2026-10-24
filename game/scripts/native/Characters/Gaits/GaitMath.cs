using System;
using Godot;

namespace GarageGame.Native.Characters.Gaits;

/// <summary>
/// A critically damped spring (no overshoot, the fastest settle without one), stepped exactly for any Δt, so a layer
/// that runs it is frame-rate independent and stable at any stiffness. The gaits move every lean, weight and blend
/// through one: starts, stops and turns get their anticipation and follow-through from it.
/// </summary>
public struct CriticalSpring
{
    public float Value;
    public float Velocity;

    public CriticalSpring(float value)
    {
        Value = value;
        Velocity = 0;
    }

    /// <summary>Moves toward <paramref name="target"/> for <paramref name="deltaS"/> at angular frequency <paramref name="omega"/> (rad/s).</summary>
    public void Step(float target, float omega, float deltaS)
    {
        if (!float.IsFinite(target)) target = 0;
        if (!float.IsFinite(Value) || !float.IsFinite(Velocity)) { Value = target; Velocity = 0; }
        var x = Value - target;
        var decay = MathF.Exp(-omega * deltaS);
        var push = (Velocity + omega * x) * deltaS;
        Value = target + (x + push) * decay;
        Velocity = (Velocity - omega * push) * decay;
    }

    /// <summary>Jumps to <paramref name="value"/> at rest.</summary>
    public void Reset(float value)
    {
        Value = value;
        Velocity = 0;
    }
}

/// <summary>Shapes and small helpers the gaits share. Pure functions, no allocation.</summary>
public static class GaitMath
{
    /// <summary>The fraction of <paramref name="value"/> in [0, 1).</summary>
    public static float Frac(double value) => (float)(value - Math.Floor(value));

    public static float Smoothstep(float edge0, float edge1, float x)
    {
        var t = Mathf.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Ken Perlin's smootherstep on [0, 1]: zero first and second derivatives at both ends.</summary>
    public static float Smootherstep(float t)
    {
        t = Mathf.Clamp(t, 0, 1);
        return t * t * t * (t * (6 * t - 15) + 10);
    }

    /// <summary>sin(2π·<paramref name="cycles"/>), the phase taken modulo 1 in double first so long play keeps its precision.</summary>
    public static float Wave(double cycles) => MathF.Sin(Mathf.Tau * Frac(cycles));

    /// <summary>+1 for the character's right (+X), -1 for its left, 0 for the middle.</summary>
    public static float Sign(BodySide side) => side == BodySide.Right ? 1 : side == BodySide.Left ? -1 : 0;

    /// <summary>A turn of <paramref name="angle"/> radians that tips the top toward -Z (a forward lean is positive).</summary>
    public static Quaternion Pitch(float angle) => new(Vector3.Right, -angle);

    /// <summary>A turn of <paramref name="angle"/> radians that tips the top toward +X, the character's right.</summary>
    public static Quaternion Roll(float angle) => new(Vector3.Back, -angle);

    /// <summary>A turn about +Y (positive turns the face toward -X, the character's left).</summary>
    public static Quaternion Yaw(float angle) => new(Vector3.Up, angle);

    /// <summary>A volume-keeping squash or stretch along Y: (1/√y, y, 1/√y).</summary>
    public static Vector3 SquashScale(float y)
    {
        var side = 1 / MathF.Sqrt(y);
        return new Vector3(side, y, side);
    }

    /// <summary>FNV-1a 32 over a string's characters: a stable seed (string.GetHashCode differs per process).</summary>
    public static uint Hash(string text, uint salt = 0)
    {
        var hash = 2166136261u ^ salt;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash == 0 ? 0x9E3779B9u : hash;
    }

    /// <summary>A stable value in [0, 1) from a seed and an index (a sparkle's own phase, a blink's own rhythm).</summary>
    public static float Hash01(uint seed, int index)
    {
        var x = seed ^ (uint)(index * 0x27D4EB2D);
        x ^= x >> 15; x *= 0x2C1B3C6Du;
        x ^= x >> 12; x *= 0x297A2D39u;
        x ^= x >> 15;
        return (x >> 8) * (1f / 16777216f);
    }
}

/// <summary>A small deterministic generator (xorshift32): the same seed gives the same blinks in every run.</summary>
public struct GaitRandom
{
    private uint _state;

    public GaitRandom(uint seed) => _state = seed == 0 ? 0x9E3779B9u : seed;

    /// <summary>A value in [0, 1).</summary>
    public float Next()
    {
        _state ^= _state << 13;
        _state ^= _state >> 17;
        _state ^= _state << 5;
        return (_state >> 8) * (1f / 16777216f);
    }
}
