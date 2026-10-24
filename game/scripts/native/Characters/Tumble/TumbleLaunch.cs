using Godot;

namespace GarageGame.Native.Characters;

/// <summary>How a tumble sets off: the velocity and spin its body starts with.</summary>
public readonly record struct TumbleKick(Vector3 Linear, Vector3 Angular);

/// <summary>
/// The tumble's launch and tether numbers (docs/runs/RUN-2-CHARACTERS.md, "The tumble (K7)"): the body takes the landing's
/// velocity (the part the ground does not stop: its sideways travel, and a small hop off the ground) and a small seeded spin,
/// and a soft spring holds where it lies to where the capsule landed. Pure functions of their inputs and the seed.
/// </summary>
public static class TumbleLaunch
{
    /// <summary>The most sideways travel a tumble keeps from the landing (m/s): a flop, never a slide away.</summary>
    public const float MaxCarryMps = 0.5f;
    /// <summary>The hop off the ground: this share of the landing speed, within <see cref="MinHopMps"/> and <see cref="MaxHopMps"/>.</summary>
    public const float HopShare = 0.12f;
    public const float MinHopMps = 0.10f, MaxHopMps = 0.35f;
    /// <summary>The seeded spin (rad/s) of a tumble, and of a dazed (knockout) one.</summary>
    public const float MinSpinRadS = 4f, MaxSpinRadS = 7f, MinDazedSpinRadS = 6f, MaxDazedSpinRadS = 9f;
    /// <summary>The most a hazard's impulse may set the body moving (m/s).</summary>
    public const float MaxImpulseMps = 3f;
    /// <summary>The tether: a spring per unit mass (1/s²) and its damping (1/s), critically damped (ω = 20 rad/s).</summary>
    public const float TetherStiffness = 400f;
    public const float TetherDamping = 40f;

    /// <summary>The kick of a landing at <paramref name="landingVelocity"/> (world, m/s; its downward part is the landing speed).</summary>
    public static TumbleKick FromLanding(Vector3 landingVelocity, uint seed, bool dazed)
    {
        if (!landingVelocity.IsFinite()) landingVelocity = Vector3.Zero;
        var carry = new Vector3(landingVelocity.X, 0, landingVelocity.Z).LimitLength(MaxCarryMps);
        var hop = Mathf.Clamp(HopShare * Mathf.Max(0, -landingVelocity.Y), MinHopMps, MaxHopMps);
        return new TumbleKick(carry + Vector3.Up * hop, Spin(carry, seed, dazed));
    }

    /// <summary>The kick of a hazard's push (world, m/s: an owl's swoop, a firework too close), limited to <see cref="MaxImpulseMps"/>.</summary>
    public static TumbleKick FromImpulse(Vector3 impulse, uint seed, bool dazed)
    {
        if (!impulse.IsFinite()) impulse = Vector3.Zero;
        impulse = impulse.LimitLength(MaxImpulseMps);
        var sideways = new Vector3(impulse.X, 0, impulse.Z);
        return new TumbleKick(impulse + Vector3.Up * MinHopMps, Spin(sideways, seed, dazed));
    }

    /// <summary>
    /// A spin about a level axis: toward the travel (the body pitches over the way it was going) when it travels, mixed with a
    /// seeded direction, at a seeded rate.
    /// </summary>
    private static Vector3 Spin(Vector3 travel, uint seed, bool dazed)
    {
        var random = Mix(seed);
        var angle = Unit(ref random) * Mathf.Tau;
        var axis = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * 0.6f;
        if (travel.LengthSquared() > 0.0025f) axis += Vector3.Up.Cross(travel.Normalized()) * 0.8f;
        if (axis.LengthSquared() < 1e-6f) axis = Vector3.Right;
        var rate = dazed ? Mathf.Lerp(MinDazedSpinRadS, MaxDazedSpinRadS, Unit(ref random)) : Mathf.Lerp(MinSpinRadS, MaxSpinRadS, Unit(ref random));
        return axis.Normalized() * rate;
    }

    private static uint Mix(uint seed)
    {
        seed ^= seed >> 16;
        seed *= 0x7FEB352Du;
        seed ^= seed >> 15;
        seed *= 0x846CA68Bu;
        seed ^= seed >> 16;
        return seed == 0 ? 0x9E3779B9u : seed;
    }

    private static float Unit(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (state & 0xFFFFFF) / (float)0x1000000;
    }
}
