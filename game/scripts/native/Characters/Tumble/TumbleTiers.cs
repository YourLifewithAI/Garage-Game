using GarageGame.Native.Kernel;

namespace GarageGame.Native.Characters;

/// <summary>What a landing does to a drawn body, by what it would cost in Challenge.</summary>
public enum TumbleTier
{
    /// <summary>It would cost nothing: the contact layer's landing squash (K4).</summary>
    None = 0,
    /// <summary>1 to 24 health: the contact layer's stumble (K4); control is kept.</summary>
    Stumble = 1,
    /// <summary>25 to 99 health: a tumble of about 1.2 s; control is held, and moving starts the get-up early.</summary>
    Tumble = 2,
    /// <summary>100 health, a knockout in Challenge: a longer, dazed tumble of about 2 s; control is held throughout.</summary>
    Knockout = 3,
}

/// <summary>
/// The tumble's tiers (docs/runs/RUN-2-CHARACTERS.md, "What counts as a big fall"): the tumble asks the vitals' fall damage
/// (<see cref="PlayerVitals.FallDamage"/>, with the team's safe drop: <see cref="BodyKnacks.FallDamage"/>) what the landing would
/// cost, in every mode, Creative included, at Standard whatever the save's difficulty, so a tumble always means "this would have
/// hurt", and it follows the world's gravity, a glide and deep water exactly as damage does. There is never a second set of numbers:
/// the three thresholds are health points read from the vitals (decision 24 of docs/game/MECHANICS.md: absolute, so more hearts
/// survive bigger falls and the look does not change with them), never speeds or heights, and retuning fall damage, or a Cat feet
/// knack that raises the safe drop, moves the tumble with it.
/// </summary>
public static class TumbleTiers
{
    /// <summary>A landing that would cost anything at all, past the safe drop (0.30 m, or the team's Cat feet), stumbles: the contact layer's.</summary>
    public const float StumbleHealth = 0f;
    /// <summary>A landing that would cost at least this much health tumbles: a quarter of the base body's health (five quarters of a heart's, about 0.5 m).</summary>
    public const float TumbleHealth = PlayerVitals.MaximumHealth / 4;
    /// <summary>A landing that would cost this much (all of the base body's health: five hearts, a knockout in Challenge, about 1.6 m) tumbles longer, dazed.</summary>
    public const float KnockoutHealth = PlayerVitals.MaximumHealth;

    /// <summary>A tumble's length: the flop on the ground and the get-up.</summary>
    public const float TumbleSeconds = 1.2f;
    /// <summary>A knockout tumble's length, which the host's recovery at the spawn waits for (KnockoutRecoveryDelayS).</summary>
    public const float KnockoutSeconds = 2.0f;
    /// <summary>The get-up: the bones blend from where they settled back to the standing pose.</summary>
    public const float GetUpSeconds = 0.4f;
    /// <summary>Moving starts a tumble's get-up early by at most this much (never a knockout's).</summary>
    public const float EarlyGetUpSeconds = 0.5f;

    /// <summary>What the landing would cost in Challenge (health), from the same function fall damage uses, for a body without a knack.</summary>
    public static float Cost(float landingSpeedMps, bool deepWater) => PlayerVitals.FallDamage(landingSpeedMps, deepWater);

    /// <summary>What the landing would cost this body (health): the vitals' curve with its Cat feet safe drop, the one the kernel hurts by.</summary>
    public static float Cost(float landingSpeedMps, bool deepWater, BodyKnacks knacks) => knacks.FallDamage(landingSpeedMps, deepWater);

    /// <summary>The tier of a landing at <paramref name="landingSpeedMps"/> (downward, m/s); deep water never tumbles.</summary>
    public static TumbleTier Classify(float landingSpeedMps, bool deepWater) => Classify(Cost(landingSpeedMps, deepWater));

    /// <summary>The tier of a landing at <paramref name="landingSpeedMps"/> for a body with these knacks.</summary>
    public static TumbleTier Classify(float landingSpeedMps, bool deepWater, BodyKnacks knacks) => Classify(Cost(landingSpeedMps, deepWater, knacks));

    /// <summary>The tier of a landing that would cost <paramref name="health"/>.</summary>
    public static TumbleTier Classify(float health) =>
        !(health > StumbleHealth) ? TumbleTier.None :
        health >= KnockoutHealth ? TumbleTier.Knockout :
        health >= TumbleHealth ? TumbleTier.Tumble : TumbleTier.Stumble;

    /// <summary>How long a tier tumbles (0 below the tumble tier: the contact layer's business).</summary>
    public static float Seconds(TumbleTier tier) => tier switch
    {
        TumbleTier.Knockout => KnockoutSeconds,
        TumbleTier.Tumble => TumbleSeconds,
        _ => 0,
    };
}
