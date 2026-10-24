namespace GarageGame.Native;

/// <summary>
/// Engine-independent body dimensions in real-world metres. Avatar size never rescales the room or
/// gravity: one world unit is one metre, and the room stays at its captured size.
/// </summary>
public sealed record WorldScaleProfile(
    double HeightMeters,
    double RadiusMeters,
    double EyeHeightMeters,
    double InteractionReachMeters)
{
    public const int SchemaVersion = 1;
    public const double MetersPerWorldUnit = 1.0;

    /// <summary>The player: a 10 cm body in a real-size room (Run 1, P2).</summary>
    public static WorldScaleProfile SmallPlayer { get; } = new(0.10, 0.02, 0.087, 0.15);

    /// <summary>
    /// The companion: a 10 cm body like the player's (founder decision, 6 October: "the companion shrinks to match
    /// the player"). It stays a separate profile object because companion upgrades may later change its abilities.
    /// </summary>
    public static WorldScaleProfile Companion { get; } = new(0.10, 0.02, 0.087, 0.15);

    public bool IsValid =>
        double.IsFinite(HeightMeters) && HeightMeters is >= 0.05 and <= 3.0 &&
        double.IsFinite(RadiusMeters) && RadiusMeters > 0.0 && RadiusMeters * 2.0 <= HeightMeters &&
        double.IsFinite(EyeHeightMeters) && EyeHeightMeters > 0.0 && EyeHeightMeters < HeightMeters &&
        double.IsFinite(InteractionReachMeters) && InteractionReachMeters >= RadiusMeters && InteractionReachMeters <= 5.0;
}
