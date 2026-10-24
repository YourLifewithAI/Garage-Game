using Godot;

namespace GarageGame.Native.Sandbox;

/// <summary>
/// The sandbox verbs' numbers (Run 2, P3; docs/engine/phase3/command-host.md, "The sandbox verbs"). They are tunings for
/// the founder's carry playtest, not physics constants: only the playtest says whether carrying feels right.
/// </summary>
public static class SandboxRules
{
    /// <summary>What the player can pick up and carry (contracts/README.md: the host's limit; unchanged until modes).</summary>
    public const float PlayerCarryLimitKg = 0.5f;
    /// <summary>What the companion can pick up and carry.</summary>
    public const float CompanionCarryLimitKg = 2.0f;
    /// <summary>A push moves up to this many times what the avatar can carry: shoving is easier than lifting.</summary>
    public const float PushLimitFactor = 2.0f;
    /// <summary>The contract's longest push (entity.push distance_m).</summary>
    public const float MaxPushM = 1.0f;
    /// <summary>
    /// How high above its feet an avatar can reach to pick something up or set it down, in body heights. A strict arm's
    /// reach (0.15 m) would put the top of the test room's 30 cm box out of reach of a 10 cm body; 3.5 body heights
    /// (35 cm) reaches the box and not the 75 cm table. A founder question.
    /// </summary>
    public const float ReachUpBodyHeights = 3.5f;
    /// <summary>A carried thing rides this far above its holder's head.</summary>
    public const float CarryGapM = 0.004f;
    /// <summary>The V key's push.</summary>
    public const float HandPushM = 0.10f;

    public static float CarryLimitKg(string avatar) => avatar == "avatar:companion" ? CompanionCarryLimitKg : PlayerCarryLimitKg;

    public static float PushLimitKg(string avatar) => CarryLimitKg(avatar) * PushLimitFactor;

    /// <summary>
    /// Whether a box is within a body's reach: horizontally within its reach of the body's side, and vertically between
    /// a reach below its feet and ReachUpBodyHeights above them.
    /// </summary>
    public static bool WithinReach(SmallPlayerController body, Aabb box)
    {
        var feet = body.GlobalPosition;
        var nearest = new Vector2(Mathf.Clamp(feet.X, box.Position.X, box.End.X), Mathf.Clamp(feet.Z, box.Position.Z, box.End.Z));
        var horizontal = new Vector2(feet.X, feet.Z).DistanceTo(nearest);
        if (horizontal > body.BodyRadiusM + body.ReachM) return false;
        return box.End.Y >= feet.Y - body.ReachM && box.Position.Y <= feet.Y + body.BodyHeightM * ReachUpBodyHeights;
    }
}
