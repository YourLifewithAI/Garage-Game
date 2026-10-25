using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Ui;

/// <summary>
/// Grow's unlocks on screen (MECHANICS.md decisions 76 and 104 to 107; docs/magic/GROW-UNLOCKS.md): which of the four the team's Grow
/// expertise has opened (the host's GrowLockedWords answers null for an open kind and its refusal's own words for a locked one, the same
/// words capabilities.list's locked_kinds stands for), what each does, and the words the wheel's wedge, key 2, the Magic panel and the
/// journal show. The counts (2, 3, 5) are the host's (GrowKindLaws); nothing here decides whether a kind works, and the host refuses an
/// early cast in its own words whatever this shows.
/// </summary>
public static class GrowLocks
{
    /// <summary>One unlock: the kernel's kind word, its name on screen, the count that opens it, what it does and its short name for a wedge.</summary>
    public sealed record Unlock(string Kind, string Name, int Count, string Does, string Short);

    /// <summary>The four, in the order capabilities.list names the locked ones (GrowKindWords.Order).</summary>
    public static readonly IReadOnlyList<Unlock> All = new[]
    {
        new Unlock(GrowKindWords.Sprouts, "Growing without seeds", GrowKindLaws.SeedlessAt, "Grow on bare soil starts small plants that grow in steps.", "sprouts"),
        new Unlock(GrowKindWords.Replant, "Replanting", GrowKindLaws.SeedlessAt, "Grow on burnt ground brings the nearest burnt plant back.", "replanting"),
        new Unlock(GrowKindWords.Bridge, "A bridge of plants", GrowKindLaws.BridgeAt, "Hold {key} and drag from lip to lip across a gap.", "a bridge"),
        new Unlock(GrowKindWords.Ivy, "Wall ivy", GrowKindLaws.IvyAt, "Grow at a wall to grow ivy you can climb.", "ivy"),
    };

    /// <summary>The unlocks the team's Grow expertise has opened, in order.</summary>
    public static IEnumerable<Unlock> Opened(CommandHost host) => All.Where(u => host.GrowLockedWords(u.Kind) == null);

    /// <summary>The first unlock still locked, or null once all four are open.</summary>
    public static Unlock? Next(CommandHost host) => All.FirstOrDefault(u => host.GrowLockedWords(u.Kind) != null);

    /// <summary>
    /// The wheel's Grow wedge, under its name: how many more of Grow's expertise the next unlock takes ("learn 2 more for sprouts"); null once all are open.
    /// Plain Grow is never locked, so the wedge stays bright and works; this is a note on it.
    /// </summary>
    public static string? WedgeNote(CommandHost host) =>
        Next(host) is { } next ? $"learn {Math.Max(1, next.Count - host.GrowExpertiseCount)} more for {next.Short}" : null;

    /// <summary>The Magic panel's lines under Grow: an open kind says what it does, a locked one is the host's own refusal.</summary>
    public static string PanelWords(CommandHost host) => string.Join('\n', All.Select(u =>
        host.GrowLockedWords(u.Kind) is { } locked ? "Locked: " + locked : $"{GubbleMagic.DoneMark} {u.Name}: {u.Does.Replace("{key}", PlayerControls.KeyLabel(Act.GubbleSlot2))}"));

    /// <summary>Whether these words are the host's refusal of a Grow unlock not yet found (its kernel words, whoever casts), to be shown as they are.</summary>
    public static bool IsLockWords(string? words) =>
        words is CommandHost.SproutsLocked or CommandHost.ReplantLocked or CommandHost.BridgeLocked or CommandHost.IvyLocked;

    /// <summary>The names of the unlocks found, for the journal's Grow lines.</summary>
    public static IEnumerable<string> FoundNames(CommandHost host) => Opened(host).Select(u => u.Name);

    /// <summary>What a Grow cast that made one of the kinds says (data.kind of its receipt); null for plain Grow and the waterpad, which say nothing extra.</summary>
    public static string? DoneWords(string? kind) => kind switch
    {
        GrowKindWords.Sprouts => "Sprouts push up from the soil.",
        GrowKindWords.Replant => "A burnt plant is growing back.",
        GrowKindWords.Bridge => "A bridge of plants spans the gap.",
        GrowKindWords.Ivy => "Ivy climbs the wall.",
        _ => null,
    };
}
