using System;
using System.Collections.Generic;
using System.Globalization;
using GarageGame.Native.Kernel;

namespace GarageGame.Native;

/// <summary>
/// The numbers a team's knacks move on the player's body (docs/game/MECHANICS.md section 7): walk and run speed (Quick feet), the
/// hop's height (High hop), swim speed (Strong swimmer) and the safe drop in metres (Cat feet). They live on the body's controller
/// (<see cref="SmallPlayerController.Knacks"/>), never in a drawn character's motion profile, and none of them touches the collision
/// capsule, the size, the eye height or the reach, which every character shares: this type has no field that could. The knacks that
/// move the vitals (Big bar, Steady climber, Long breath, Big heart, and Strong swimmer's deep-water saving) are the kernel's
/// (<see cref="VitalsRules"/>); the body reads this much of the pack's knacks table and leaves the rest alone.
/// </summary>
/// <remarks>
/// <see cref="None"/> is today's body exactly. The kernel resolves the team's ranks against the island's pack
/// (<see cref="FromPack"/>) whenever either changes and hands the result to the body; until the team file exists a rank comes from a
/// boot argument (<see cref="FromArguments"/>) or a test. Nothing a request carries reaches here: the companion never reads, writes
/// or rerolls a knack.
/// </remarks>
public sealed record BodyKnacks
{
    /// <summary>The pack's <c>knacks.table[].moves</c> ids this type reads (contracts/island-rules.schema.json).</summary>
    public const string MovesWalkRunSpeed = "walk_run_speed", MovesHopHeight = "hop_height", MovesSwim = "swim", MovesSafeDrop = "safe_drop";

    /// <summary>The most knacks a boot argument names: the three slots (one at creation, the summit, the second summit).</summary>
    public const int MaxBootKnacks = 3;

    /// <summary>No knack: today's body.</summary>
    public static BodyKnacks None { get; } = new();

    /// <summary>Walk and run speed, times this (Quick feet: 1.1, 1.15, 1.2).</summary>
    public float WalkRunFactor { get; init; } = 1f;
    /// <summary>The hop's apex, times this (High hop: 1.1, 1.15, 1.2).</summary>
    public float HopFactor { get; init; } = 1f;
    /// <summary>Swim speed, at the surface and under it, times this (Strong swimmer: 1.1, 1.15, 1.2).</summary>
    public float SwimFactor { get; init; } = 1f;
    /// <summary>The highest drop that costs nothing, in metres (Cat feet: 0.45, 0.60, 0.75; 0.30 without).</summary>
    public float SafeDropM { get; init; } = PlayerVitals.SafeFallHeight;

    /// <summary>Whether no number is moved: the body is exactly today's.</summary>
    public bool IsNone => WalkRunFactor == 1f && HopFactor == 1f && SwimFactor == 1f && SafeDropM == PlayerVitals.SafeFallHeight;

    /// <summary>
    /// The landing speed (downward, m/s) that costs nothing: a free fall through the safe drop under the vitals' reference gravity
    /// (PlayerVitals.SafeLandingSpeedMps is that fall through 0.30 m, so a height scales it by its square root and nothing here
    /// repeats the gravity).
    /// </summary>
    public float SafeLandingSpeedMps => PlayerVitals.SafeLandingSpeedMps * MathF.Sqrt(Math.Max(SafeDropM, PlayerVitals.SafeFallHeight) / PlayerVitals.SafeFallHeight);

    /// <summary>How much faster a landing may be than the vitals' safe one before it costs anything (0 without Cat feet).</summary>
    public float SafeLandingCreditMps => SafeLandingSpeedMps - PlayerVitals.SafeLandingSpeedMps;

    /// <summary>
    /// What a landing costs in health points (decision 24: absolute, the same for every body, before any difficulty): the vitals'
    /// own curve (<see cref="PlayerVitals.FallDamage"/>) with the team's safe drop, so a taller safe drop moves where the curve
    /// starts and nothing else. The kernel hurts the player by this and the tumble's look reads it, so they can never disagree.
    /// </summary>
    public float FallDamage(float landingSpeedMps, bool deepWater) =>
        PlayerVitals.FallDamage(float.IsFinite(landingSpeedMps) ? landingSpeedMps - SafeLandingCreditMps : landingSpeedMps, deepWater);

    /// <summary>
    /// The pack's knacks table read for the ranks a team holds (id to rank, 1 first), as the kernel's rules read it: a knack the pack
    /// lacks, has not made available, or holds at rank 0 counts for nothing; a rank past the pack's last is its last; factors from
    /// different knacks that move the same number multiply, and the highest safe drop wins.
    /// </summary>
    public static BodyKnacks FromPack(KnacksRules? table, IReadOnlyDictionary<string, int>? ranks)
    {
        if (table == null || ranks == null || ranks.Count == 0) return None;
        double walk = 1, hop = 1, swim = 1, drop = PlayerVitals.SafeFallHeight;
        foreach (var (id, rank) in ranks)
        {
            if (rank < 1 || table.Knack(id) is not { Available: true } knack || knack.Ranks.Count == 0) continue;
            var value = knack.Ranks[Math.Min(rank, knack.Ranks.Count) - 1];
            if (!double.IsFinite(value) || value <= 0) continue;
            switch (knack.Moves)
            {
                case MovesWalkRunSpeed when knack.Unit == "factor": walk *= value; break;
                case MovesHopHeight when knack.Unit == "factor": hop *= value; break;
                case MovesSwim when knack.Unit == "factor": swim *= value; break;
                case MovesSafeDrop when knack.Unit == "m": drop = Math.Max(drop, value); break;
            }
        }
        return new BodyKnacks { WalkRunFactor = (float)walk, HopFactor = (float)hop, SwimFactor = (float)swim, SafeDropM = (float)drop };
    }

    /// <summary>
    /// The knacks a boot names until the team file exists: <c>--knack=quick_feet:3</c> (the id, then its rank 1 to 3; the rank is 1
    /// when left off), repeated up to three times or listed as <c>--knacks=quick_feet:3,cat_feet:2</c>. Anything else is ignored: a
    /// malformed argument gives no knack, never a guess. The ranks go to <c>CommandHost.UseKnacks</c>, which resolves them against
    /// the island's pack. Default: none, today's body.
    /// </summary>
    public static Dictionary<string, int> FromArguments(IEnumerable<string>? arguments)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        if (arguments == null) return ranks;
        foreach (var argument in arguments)
        {
            if (argument == null) continue;
            string list;
            if (argument.StartsWith("--knack=", StringComparison.Ordinal)) list = argument["--knack=".Length..];
            else if (argument.StartsWith("--knacks=", StringComparison.Ordinal)) list = argument["--knacks=".Length..];
            else continue;
            foreach (var entry in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ranks.Count >= MaxBootKnacks) return ranks;
                var colon = entry.IndexOf(':');
                var id = colon < 0 ? entry : entry[..colon];
                var rank = 1;
                if (colon >= 0 && !(int.TryParse(entry[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out rank) && rank is >= 1 and <= 3)) continue;
                if (!SmallPlayerController.IsCharacterToken(id)) continue;
                ranks[id] = rank;
            }
        }
        return ranks;
    }
}
