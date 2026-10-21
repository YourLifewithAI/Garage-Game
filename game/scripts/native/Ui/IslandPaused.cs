using GarageGame.Native.Kernel;

namespace GarageGame.Native.Ui;

/// <summary>
/// What the pages say while an island is held (docs/game/MECHANICS.md decision 54, the founder on 15 October: "While an island is
/// held (the boot could not set its mode), the journal's nature page says the island is paused instead of describing Creative").
/// A held island (CommandHost.IslandHeld) is the island save kept as it was because the boot could not open it in its own mode:
/// the host plays under another mode's rules (Creative, in practice) and nothing done on the island is saved. Any page that would
/// describe the mode, its rules or what it does for the player says this instead: the island is paused, why (the host's own
/// words), and that the way out is to quit and launch again. The words are the host's; nothing here changes anything.
/// </summary>
public static class IslandPaused
{
    /// <summary>The short name that stands where a mode's name would (a heading, the Magic panel's island line).</summary>
    public const string Name = "Paused";
    /// <summary>The heading over the page's words.</summary>
    public const string Heading = "This island is paused";
    /// <summary>The words when the host gave none (it always does; this keeps the page whole if a later change clears them).</summary>
    public const string Fallback = "It could not open in its own mode, so it is held as it was, and nothing done on it now is saved. Quit and launch again.";

    /// <summary>Why it is paused, in the host's own words (CommandHost.TeamNotice, written when the island was held).</summary>
    public static string Words(CommandHost host) => host.TeamNotice.Length > 0 ? host.TeamNotice : Fallback;
}
