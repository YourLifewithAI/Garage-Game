namespace GarageGame.Native.Kernel;

/// <summary>
/// The Bubble's numbers (docs/game/MECHANICS.md section 5 and decisions 55 to 60 name none but the prices, which the rules pack
/// holds): one table of provisional values, in the manner of NatureLaws, for the founder to tune by playing. The kernel reads them
/// for the bubble (CommandHostBubble, CommandHostBubbleLead) and the body for its ride (SmallPlayerController.Bubble.cs).
/// Metres, seconds, m/s.
/// </summary>
public static class BubbleLaws
{
    // ---- size: one player-sized bubble (decision 55), and the boat ----

    /// <summary>The Bubble's size, round the 10 cm body with room to spare: what effect.start forms whatever its area says, and a carry's default.</summary>
    public const float CarryRadiusDefaultM = 0.08f;
    /// <summary>The smallest and largest carry a nature.cast may name: smaller would not hold the body, larger would not fit where the body goes.</summary>
    public const float CarryRadiusMinM = 0.07f;
    public const float CarryRadiusMaxM = 0.2f;
    /// <summary>The boat's deck: what effect.start forms on water, and the bounds a nature.cast may name (a raft for one or two small bodies).</summary>
    public const float BoatRadiusDefaultM = 0.12f;
    public const float BoatRadiusMinM = 0.08f;
    public const float BoatRadiusMaxM = 0.3f;

    // ---- durations when the player's control names none (the pack's duration_max_s bounds every cast: 60 s in storybook_wild v5):
    // the nature.cast controls (BubbleCastArgs) and the Bubbles key (PlayerEffect, PlayerBubbleDurationS) alike ----

    /// <summary>A bubble lasts 8 seconds (decision 95, the founder's answer of 17 October; it was 20): a carry, or a bubble to walk into.</summary>
    public const float CarryDurationDefaultS = 8f;
    /// <summary>The boat keeps its own minute.</summary>
    public const float BoatDurationDefaultS = 60f;

    // ---- motion: a bubble moves only when the Gubble leads it (decision 57), and floats up only on a lift (58) ----

    /// <summary>How far a bubble that holds the player lifts the body above where it began to hold them, and how fast it rises there.</summary>
    public const float CarryHoverM = 0.05f;
    public const float CarryRiseMps = 0.2f;
    /// <summary>The most a lift rises above the height it began to rise from (three bands of the bar; "the 1.2 m rise stays, to test"), and how fast.</summary>
    public const float LiftRiseM = 1.2f;
    public const float LiftRiseMps = 0.15f;
    /// <summary>The string a led bubble is on, like a balloon: it heads for the spot but goes no farther than this from the Gubble (level distance).</summary>
    public const float LeashM = 0.5f;
    /// <summary>How fast a led carry drifts, and a led boat.</summary>
    public const float CarrySpeedMps = 0.35f;
    public const float BoatSpeedMps = 0.25f;
    /// <summary>The fastest the body follows the bubble that holds it (it keeps up with any drift above).</summary>
    public const float RideFollowMaxMps = 1.5f;

    // ---- the lead (goal.set lead, decisions 57, 58 and 60) ----

    /// <summary>A lead's spot whose resting height stands more than this above the bubble is higher than it can drift: the lead is a lift.</summary>
    public const float LiftAboveDriftM = 0.1f;
    /// <summary>
    /// The drift's own step: rising ground (a rug's edge, a book, a ramp's foot) lifts a bubble at most this above the height it
    /// formed, took the player or was lifted at; higher ground blocks its drift, and only a lift raises that height (decision 58).
    /// The same 0.1 m a lead calls a drift rather than a lift.
    /// </summary>
    public const float DriftStepM = LiftAboveDriftM;
    /// <summary>A lift begins to rise once the bubble is this near its spot (level), or once its drift has been blocked LiftWhenBlockedS.</summary>
    public const float LiftApproachM = 0.4f;
    public const double LiftWhenBlockedS = 0.3;
    /// <summary>A led bubble has arrived within this of its spot (level) and of its resting height there.</summary>
    public const float LeadArriveM = 0.04f;
    public const float LeadArriveRiseM = 0.03f;
    /// <summary>A led bubble that cannot move toward its spot for this long (a wall, the player in its way, the top of its rise, a ceiling over it) stops: the lead fails.</summary>
    public const double LeadBlockedAfterS = 3.0;
    /// <summary>The Gubble walks to within this of the spot (CompanionAvatar.GoTo's arrival), beside it rather than under it, and the bubble drifts the rest.</summary>
    public const float LeadGubbleStopM = 0.3f;

    // ---- no deflate: since decision 88 a bubble that pops in the air leaves the body falling (SmallPlayerController.Bubble.cs) ----

    // ---- the boat ----

    /// <summary>The deck floats this far above the water's surface.</summary>
    public const float BoatDeckAboveWaterM = 0.01f;
    /// <summary>Water, where the Bubble becomes a boat (decision 56: on water, a boat): at least this deep, its surface within BoatCastSurfaceM of the spot.</summary>
    public const float BoatCastMinDepthM = 0.01f;
    public const float BoatCastSurfaceM = 0.3f;
    /// <summary>A boat moves only where the water under it is at least this deep.</summary>
    public const float BoatFloatMinDepthM = 0.01f;
    /// <summary>Feet this far under the deck (wading or swimming beside it) or above it still step aboard.</summary>
    public const float BoardBelowDeckM = 0.1f;
    public const float BoardAboveDeckM = 0.05f;

    // ---- consent (MECHANICS.md section 5's three guardrails, and the security notes' "cannot keep a player in a bubble") ----

    /// <summary>A Gubble's carry on the player commits at least this long after its ghost preview, so the player has seen it coming.</summary>
    public const double GhostLeadS = 1.0;
    /// <summary>The ghost shows this long; a cast naming an older preview is refused (preview it again).</summary>
    public const double GhostLifetimeS = 15.0;
    /// <summary>A spot this near the player's feet (beyond the bubble's radius) is on the player: the Bubble there is a carry round them.</summary>
    public const float EnvelopReachM = 0.05f;
    /// <summary>A bubble at a point never forms round the player: their feet at least its radius and this away; a led empty bubble keeps this off them too.</summary>
    public const float StepInClearanceM = 0.02f;
    /// <summary>A body this far outside the bubble that holds it, for RiderLostAfterS, has left it (a teleport home, a wall): it pops.</summary>
    public const float RiderSlackM = 0.06f;
    public const double RiderLostAfterS = 0.3;

    // ---- caps (within the room's effect cap, CommandHost.MaxActiveEffects) ----

    /// <summary>One Bubble at a time (decision 59): a recast pops the one already there, and there is no pause after a pop.</summary>
    public const int MaxBubbles = 1;
    /// <summary>At most this many ghost previews wait at once; the oldest goes first.</summary>
    public const int MaxGhosts = 8;
}
