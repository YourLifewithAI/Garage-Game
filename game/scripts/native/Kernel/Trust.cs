using Godot;
using System;
using System.Collections.Generic;

namespace GarageGame.Native.Kernel;

/// <summary>
/// Trust's provisional numbers (docs/game/MECHANICS.md section 5, "Trust, the later dial"), named in one place until the rules
/// pack has a trust block (the change request in M8's report). Every number here is the founder's to move.
/// </summary>
public static class TrustLaws
{
    /// <summary>A paid Gubble action on its own initiative whose next CleanWindowS produced no consequence fact gains this (decision 69).</summary>
    public const int CleanInitiative = 1;
    /// <summary>The window after a Gubble action in which a consequence fact spoils its gain (seconds, real time).</summary>
    public const double CleanWindowS = 60;
    /// <summary>At most one gain from clean initiative in this long (seconds, real time).</summary>
    public const double GainSpacingS = 120;
    /// <summary>
    /// Each consequence fact that follows directly from a paid Gubble action (its paid fire hurt the player or burnt a build; its paid
    /// bubble or paid lift carried the player into fire) moves Trust by this. A consequence of a free action moves nothing (decision 69).
    /// </summary>
    public const int PerConsequence = -3;
    /// <summary>The player's "yes, like that" on a receipt.</summary>
    public const int MarkYes = 2;
    /// <summary>The player's "not that" on a receipt.</summary>
    public const int MarkNotThat = -2;
    /// <summary>The bands: Trust at or above each starts the next band (1, 2, 3).</summary>
    public static readonly int[] BandAt = { 5, 15, 30 };
    /// <summary>
    /// The Gubble's own-idea price at each band (0 to 3), as a share of the pack's own-idea price, rounded to whole pips and never
    /// below one pip for an ability that has a price: the bands widen the discount on its own ideas, every ability alike, Fire
    /// included (decision 42). Band 0 is the pack's price.
    /// </summary>
    public static readonly double[] OwnIdeaShare = { 1.0, 0.75, 0.5, 0.25 };
    /// <summary>
    /// The free early warning (the player told before a hazard reaches them): how near burning ground may come before the Gubble
    /// warns, at each band (0: none). Fire hurts within NatureLaws.PlayerBurnM (0.12 m), so each reach is well before that.
    /// </summary>
    public static readonly float[] EarlyWarningM = { 0f, 0.3f, 0.45f, 0.6f };
    /// <summary>"Lesson applied": a clean fire of the Gubble's own within this of where a consequence once followed one of its fires.</summary>
    public const float LessonPlaceM = 2.0f;
    /// <summary>The Gubble's commands the host remembers this session, for the windows and the marks; the oldest go first.</summary>
    public const int MaxRemembered = 1024;
    /// <summary>
    /// The Gubble's paid actions the host remembers this session, for tracing a consequence fact back to one (decision 69). Only a
    /// command the guard charged pips for enters this list, and the pool limits how many there can be, so no run of free commands
    /// pushes a paid fire or bubble out of it while its consequences can still follow.
    /// </summary>
    public const int MaxPaidRemembered = 64;
    // Decision 69 (the founder, 16 October, evening): "Only paid actions and consequences directly related to those actions earn
    // trust." Which commands those are is read from what the guard charged (CommandHostEnergy.ChargeEnergy, and a lead's Lift at its
    // rise), never from a list of ops: a pack's prices decide. It narrows decision 68, whose WorldOps and WorldGoals lists are gone.
}

/// <summary>Trust's pure rules: the band, the words (never a number on screen), the discounted own-idea price.</summary>
public static class Trust
{
    public const int Bound = 1_000_000;

    public static int Clamp(long count) => (int)Math.Clamp(count, -Bound, Bound);

    /// <summary>
    /// Decision 69's rule, the one both hosts keep: a command of the Gubble's is a paid action, and so can earn clean initiative and
    /// carry its consequences into Trust, when the host charged it pips (the guard's price above zero at its commit, or a lead's Lift
    /// at its rise). Free commands (grabs, pushes, releases, free builds, goals, stops, notes) never are.
    /// </summary>
    public static bool Paid(int chargedPips) => chargedPips > 0;

    /// <summary>The band, 0 to 3: what the bands at 5, 15 and 30 open (the discount, the early warning, the aura's mark).</summary>
    public static int Band(int count)
    {
        var band = 0;
        foreach (var at in TrustLaws.BandAt)
            if (count >= at) band++;
        return band;
    }

    /// <summary>The words' tier: -1 below base (trust to win back), else the band. The journal says so when the tier changes.</summary>
    public static int Tier(int count) => count < 0 ? -1 : Band(count);

    /// <summary>The own-idea price at a band: the pack's own-idea price times the band's share, whole pips, at least one when priced.</summary>
    public static int OwnIdeaPrice(int ordered, double ownIdeaFactor, int band)
    {
        var share = TrustLaws.OwnIdeaShare[Math.Clamp(band, 0, TrustLaws.OwnIdeaShare.Length - 1)];
        var price = (int)Math.Round(ordered * ownIdeaFactor * share, MidpointRounding.AwayFromZero);
        return ordered > 0 ? Math.Max(1, price) : 0;
    }

    /// <summary>The words for the player's page (you, the player; the Gubble by its name), one tier each, no number.</summary>
    public static string PlayerWords(int count, string gubble) => Tier(count) switch
    {
        -1 => $"{gubble} has your trust to win back.",
        0 => $"{gubble} is earning your trust.",
        1 => $"You trust {gubble} a little.",
        2 => $"You trust {gubble}.",
        _ => $"You trust {gubble} deeply.",
    };

    /// <summary>The same tier as the journal's facts say it (the game's own words, for the player and the AI alike).</summary>
    public static string Phrase(int tier) => tier switch
    {
        -1 => "the Gubble has your trust to win back",
        0 => "the Gubble is earning your trust",
        1 => "you trust the Gubble a little",
        2 => "you trust the Gubble",
        _ => "you trust the Gubble deeply",
    };

    /// <summary>The journal's line when the tier changes: grew or fell, and the words now.</summary>
    public static string ChangeLine(int before, int after) => (after > before ? "Trust grew: " : "Trust fell: ") + Phrase(after) + ".";
}

/// <summary>
/// The Gubble's committed commands this session, as Trust needs them (host memory only; never saved, never in a receipt): each
/// one's 60 s window, whether it was paid, whether a consequence fact spoiled it, and the player's mark. A receipt is marked once.
/// Beside them, the paid actions (TrustLaws.MaxPaidRemembered), which a consequence fact is traced back to.
/// </summary>
public sealed class TrustLedger
{
    /// <summary>A paid action of the Gubble's: its action id, op, ability and the room revision it committed at (a fire's revision).</summary>
    public readonly record struct PaidAction(string ActionId, string Op, string? Capability, int? Revision);

    public sealed class Entry
    {
        public required string ActionId { get; init; }
        public required string Op { get; init; }
        /// <summary>The ability or goal it used (nature.cast's and effect.start's capability, goal.set's goal), or null.</summary>
        public string? Capability { get; init; }
        /// <summary>The words a mark's line names it by ("fire", "bubble carry", "fetch").</summary>
        public required string What { get; init; }
        /// <summary>Where it aimed, when the command named a place.</summary>
        public Vector3? Place { get; init; }
        public DateTime At { get; init; }
        /// <summary>Whether Trust counted when it committed (Challenge and Live): only those gain.</summary>
        public bool Counted { get; init; }
        /// <summary>
        /// Whether the host charged it pips (Trust.Paid, decision 69): at its commit, or a lead's Lift at its rise (TrustLedger.Charged).
        /// Only a paid action's window can gain. Every one, paid or free, can be marked.
        /// </summary>
        public bool Paid { get; internal set; }
        /// <summary>The room revision its result named (a fire's nature.cast commits at the revision its fire is lit at), when it had one.</summary>
        public int? Revision { get; init; }
        /// <summary>The player when the player's click committed it (an approved suggestion, decision 45), else null.</summary>
        public string? ApprovedBy { get; init; }
        /// <summary>
        /// A goal's job (a lead, a fetch, any goal that runs as a job): its id. Its window waits for the job (Pending), opens when the job
        /// succeeds, and gains nothing when the job fails or is stopped. A lead becomes paid while it runs, when its Lift is charged.
        /// </summary>
        public string? JobId { get; init; }
        /// <summary>Its job is still running: the window has not opened, and a consequence fact meanwhile spoils it.</summary>
        public bool Pending { get; internal set; }
        /// <summary>Its job ended without succeeding: it changed nothing in the world, so it gains nothing.</summary>
        public bool JobFailed { get; internal set; }
        /// <summary>When its window opened: when it committed, or when its job succeeded.</summary>
        public DateTime? OpenedAt { get; internal set; }
        public bool Spoiled { get; internal set; }
        public bool Settled { get; internal set; }
        /// <summary>yes or not_that once the player marked its receipt.</summary>
        public string? Mark { get; internal set; }
        public DateTime Opens => OpenedAt ?? At;
        public DateTime Closes => Opens + TimeSpan.FromSeconds(TrustLaws.CleanWindowS);
    }

    private readonly List<Entry> _entries = new();
    private readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    private readonly List<PaidAction> _paid = new();

    /// <summary>When clean initiative last gained (the window's close), or null since the session began or the last reset.</summary>
    public DateTime? LastGain { get; private set; }
    public int Count => _entries.Count;

    public Entry? Find(string actionId) => _byId.GetValueOrDefault(actionId);

    /// <summary>A committed command of the Gubble's; the oldest is forgotten past TrustLaws.MaxRemembered. A paid one is remembered as paid.</summary>
    public void Note(Entry entry)
    {
        if (_byId.Remove(entry.ActionId, out var earlier)) _entries.Remove(earlier);
        _entries.Add(entry);
        _byId[entry.ActionId] = entry;
        while (_entries.Count > TrustLaws.MaxRemembered)
        {
            _byId.Remove(_entries[0].ActionId);
            _entries.RemoveAt(0);
        }
        if (entry.Paid) RememberPaid(new PaidAction(entry.ActionId, entry.Op, entry.Capability, entry.Revision));
    }

    /// <summary>
    /// The host charged pips for a command after it committed (a lead's Lift at its rise): it is a paid action from now on, its window
    /// able to gain once its job succeeds, and its consequences traced to it. A command no longer remembered is still remembered as paid.
    /// </summary>
    public void Charged(string actionId)
    {
        var entry = Find(actionId);
        if (entry != null) entry.Paid = true;
        RememberPaid(new PaidAction(actionId, entry?.Op ?? "goal.set", entry?.Capability, entry?.Revision));
    }

    /// <summary>Whether the host charged pips for this command of the Gubble's (a consequence's trace to its action).</summary>
    public bool WasPaid(string actionId) => _paid.Exists(p => p.ActionId == actionId);

    /// <summary>Whether the Gubble's Fire lit at this room revision was paid: a fire's NatureFire.Revision is the revision its cast committed at.</summary>
    public bool PaidFireAt(int revision) => _paid.Exists(p => p is { Op: "nature.cast", Capability: "fire" } && p.Revision == revision);

    private void RememberPaid(PaidAction paid)
    {
        if (WasPaid(paid.ActionId)) return;
        _paid.Add(paid);
        while (_paid.Count > TrustLaws.MaxPaidRemembered) _paid.RemoveAt(0);
    }

    /// <summary>A consequence fact at this moment: every window still open over it is spoiled, and every job still running.</summary>
    public void Consequence(DateTime at)
    {
        foreach (var entry in _entries)
            if (!entry.Settled && (entry.Pending || (entry.Opens <= at && at <= entry.Closes))) entry.Spoiled = true;
    }

    /// <summary>
    /// A goal's job ended (the host's GoalFinished): succeeded, its window opens now; failed or stopped, it gains nothing. Only an
    /// entry still waiting for its job moves.
    /// </summary>
    public void JobEnded(string jobId, bool succeeded, DateTime at)
    {
        foreach (var entry in _entries)
        {
            if (!entry.Pending || entry.JobId != jobId) continue;
            entry.Pending = false;
            if (succeeded) entry.OpenedAt = at;
            else entry.JobFailed = true;
        }
    }

    /// <summary>
    /// The windows closed by now, in the order they closed, each settled once: whether it was clean and whether it gained (a clean,
    /// counted, paid window gains when no gain came in the GainSpacingS before its close). A window still waiting for its job stays open.
    /// </summary>
    public List<(Entry Entry, bool Clean, bool Gained)> Settle(DateTime now)
    {
        var closed = new List<Entry>();
        foreach (var entry in _entries)
            if (!entry.Settled && !entry.Pending && entry.Closes <= now) closed.Add(entry);
        closed.Sort((a, b) => a.Closes.CompareTo(b.Closes));
        var outcome = new List<(Entry, bool, bool)>(closed.Count);
        foreach (var entry in closed)
        {
            entry.Settled = true;
            var clean = !entry.Spoiled;
            var gained = clean && entry.Counted && entry.Paid && !entry.JobFailed && (LastGain is not { } last || (entry.Closes - last).TotalSeconds >= TrustLaws.GainSpacingS);
            if (gained) LastGain = entry.Closes;
            outcome.Add((entry, clean, gained));
        }
        return outcome;
    }

    /// <summary>The player's reset: every open window ends with no gain and the spacing starts again. Marks stay: each receipt once.</summary>
    public void Reset()
    {
        foreach (var entry in _entries) entry.Settled = true;
        LastGain = null;
    }
}
