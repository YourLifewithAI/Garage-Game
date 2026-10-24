using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Saves;

namespace GarageGame.Native.Kernel;

/// <summary>
/// Trust (docs/game/MECHANICS.md section 5, "Trust, the later dial"; decisions 1, 6, 16, 42 and 45; M8 of
/// docs/runs/RUN-2-MECHANICS.md): a count per team the host keeps in the team file, computed only from what happened.
/// <list type="bullet">
/// <item><b>Clean initiative, +1, for paid actions only</b> (decision 69: "Only paid actions and consequences directly related to
/// those actions earn trust"). Every command on the Gubble's connection is its own idea (decision 5), an approved suggestion too
/// (decision 45): when one the guard charged pips for commits (Trust.Paid: a cast or an effect, read from what ChargeEnergy charged,
/// so a pack's prices decide), a 60 s window opens; a window that closes with no consequence fact in it gains, at most once in two
/// minutes. A goal's window waits for its job and opens only when it succeeds; a lead is paid only once its Lift is charged at the
/// rise (TrustLiftCharged), so a lead that is no lift, a fetch, or one that fails gains nothing. Free commands (grabs, pushes,
/// releases, free builds, goals, effect adjustments, stops, notes, checkpoints) never gain.</item>
/// <item><b>Consequence facts, -3, when they follow directly from a paid action.</b> The Gubble's fire hurting the player
/// (CommandHostConsequences), the Gubble's fire burning a build (M8; one fact per fire for each), and the Gubble's lead carrying the
/// player into fire that hurts them, whoever lit it (one fact per lead). Each is traced to its action (PaidFire, PaidCarry); a fact
/// from a free action is written in the journal as ever but moves no Trust and spoils no window.</item>
/// <item><b>The player's marks, +2 and -2.</b> receipt.mark ("yes, like that" or "not that") on a receipt of the Gubble's from this
/// session, each receipt once; trust.reset back to base. Both player-only, never held, never exposed to the companion.</item>
/// <item><b>Counted in Challenge and Live only</b> (team.schema.json); marks in Creative still teach (the journal keeps them).</item>
/// <item><b>The bands at 5, 15 and 30</b> widen the own-idea discount (TrustPrice, every ability alike, Fire included: decision 42)
/// and add the free early warning (the Gubble warns the player before burning ground reaches them). They never touch the fire
/// guardrails or the player's switches, and no ability is withheld behind a band.</item>
/// <item><b>Readable, never writable.</b> The journal keeps Trust's story in the game's own words as the AI's lessons (kind
/// consequence): the marks, a tier's change, a reset, "lesson applied". "Lesson applied" is the host's own observation, never
/// the AI's claim. No request field moves Trust; the number is never on screen (the player reads words; the aura reads TrustBand).</item>
/// </list>
/// </summary>
public partial class CommandHost
{
    public const string MarkNotFound = "There is no receipt of the Gubble's from this session with that action id.";
    public const string MarkedOnce = "That receipt is already marked: each receipt is marked once.";
    public const string EarlyWarningWords = "Careful: fire close by!";

    private readonly TrustLedger _trustLedger = new();
    /// <summary>Trust while no team file serves this host (a fixture, or a team file that did not read): this session only.</summary>
    private int _trustUnfiled;
    /// <summary>Whether the free early warning is showing for the fire near the player now (one warning per approach).</summary>
    private bool _warned;
    /// <summary>
    /// The pips the guard charged since Trust last looked (ChargeEnergy's hook, NoteTrustCharge): the command being executed's own
    /// charge, read and cleared when it commits (ObserveTrustCommand), or a lead's Lift's, read at its rise (TrustLiftCharged).
    /// </summary>
    private int _trustCharged;

    /// <summary>The player was warned before a hazard (the early warning's words): the Gubble's cue shows them (CompanionBridge).</summary>
    public event Action<string>? EarlyWarning;

    /// <summary>The team's Trust count (the team file's, else this session's). Never on screen as a number; never a request's to set.</summary>
    public int TrustCount => Team?.Trust ?? _trustUnfiled;

    /// <summary>The band, 0 to 3 (Trust at 5, 15 and 30): the accessor the Gubble's aura reads for its Trust mark, in a later look pass.</summary>
    public int TrustBand => Trust.Band(TrustCount);

    /// <summary>Trust in words for the player's journal page, by the Gubble's name.</summary>
    public string TrustWords => Trust.PlayerWords(TrustCount,
        Companion is { } gubble && IsInstanceValid(gubble) ? KernelJson.DisplayText(gubble.CompanionName, 40) : "the Gubble");

    /// <summary>
    /// Trust as the AI may read it in player.status once the contract adds the field (M8's change request): the band and the game's
    /// words for it, never the count.
    /// </summary>
    public JsonObject TrustStatus()
    {
        SettleTrust();
        var phrase = Trust.Phrase(Trust.Tier(TrustCount));
        return new JsonObject { ["band"] = TrustBand, ["words"] = char.ToUpperInvariant(phrase[0]) + phrase[1..] + "." };
    }

    /// <summary>Whether Trust moves now: Challenge and Live (team.schema.json), never on an island held as it was.</summary>
    public bool TrustCounts => Mode != "creative" && !IslandHeld;

    /// <summary>The Gubble's commands this session that Trust remembers (tests).</summary>
    internal int TrustRemembered => _trustLedger.Count;

    // ---- the Gubble's commands: initiative, and what a mark names ----

    /// <summary>
    /// ChargeEnergy's hook (decision 69): the guard charged this price at a commit, or at a Lift's rise. Trust reads it, never an op list,
    /// so whatever a pack prices is what can earn.
    /// </summary>
    private void NoteTrustCharge(int price)
    {
        if (price > 0) _trustCharged += price;
    }

    /// <summary>The pips charged since Trust last looked, cleared: what the command just executed (or the Lift just risen) was charged.</summary>
    private int TakeTrustCharge()
    {
        var charged = _trustCharged;
        _trustCharged = 0;
        return charged;
    }

    /// <summary>
    /// After a command committed (CommandHost.Execute): a command of the Gubble's opens its window when the guard charged it pips, and
    /// every one is remembered for a mark. Its own words and place come from the command as committed, never from the receipt's text.
    /// The charge is taken first, whoever's command it was, so the player's own charge never reaches the Gubble's next command.
    /// </summary>
    private void ObserveTrustCommand(JsonElement root, JsonObject result, string principal)
    {
        var charged = TakeTrustCharge();
        if (principal != CompanionPrincipal || result["ok"]?.GetValue<bool>() != true || result["preview"]?.GetValue<bool>() == true) return;
        // The command has committed: nothing here may turn its answer into a failure. A Trust note that cannot be made is dropped.
        try { RememberGubbleCommand(root, result, charged); }
        catch (Exception error) when (error is not OutOfMemoryException) { GD.Print("COMMAND_HOST Trust could not note a command: " + error.GetType().Name); }
    }

    private void RememberGubbleCommand(JsonElement root, JsonObject result, int charged)
    {
        var op = Op(root);
        if (StopOps.Contains(op) || op is "journal.note" or "room.checkpoint") return;
        var args = root.GetProperty("args");
        var capability = op switch
        {
            // A Bubble's receipt names what the place made of it (a carry, a boat, a bubble to walk into: M9's data.capability).
            "nature.cast" or "effect.start" => result["data"]?["capability"] is JsonValue made && made.TryGetValue<string>(out var kind) ? kind : Str(args, "capability"),
            "goal.set" => Str(args, "goal"),
            _ => null,
        };
        SettleTrust();
        // A goal's job decides whether it did anything: its window waits for the job (TrustGoalFinished). Goals cost nothing at their
        // commit; a lead becomes paid when its Lift is charged at the rise (TrustLiftCharged).
        var job = op == "goal.set" ? result["job_id"]?.GetValue<string>() : null;
        _trustLedger.Note(new TrustLedger.Entry
        {
            ActionId = Str(root, "action_id")!, Op = op, Capability = capability, What = TrustWhat(op, capability), Place = TrustPlace(op, args),
            At = Clock(), Counted = TrustCounts, Paid = Trust.Paid(charged), Revision = result["revision"] is JsonValue at && at.TryGetValue<int>(out var revision) ? revision : null,
            ApprovedBy = result["approved_by"]?.GetValue<string>(), JobId = job, Pending = job != null,
        });
        if (job == null) return;
        if (!_trustWatchesJobs)
        {
            GoalFinished += TrustGoalFinished;
            _trustWatchesJobs = true;
        }
        // A job that already ended inside its own command (a refusal on the spot) settles as it ended.
        if (_jobs.TryGetValue(CompanionPrincipal, out var jobs) && jobs.Find(j => j.Id == job) is { State: not "running" } ended)
            _trustLedger.JobEnded(job, ended.State == "succeeded", Clock());
    }

    /// <summary>Whether Trust hears the goal jobs' ends (subscribed once, at the first goal of the Gubble's that runs as a job).</summary>
    private bool _trustWatchesJobs;

    /// <summary>A goal job ended (GoalFinished): its window opens if it succeeded (gaining only if it was paid), and gains nothing otherwise.</summary>
    private void TrustGoalFinished(string actor, string jobId, string state)
    {
        try { _trustLedger.JobEnded(jobId, state == "succeeded", Clock()); }
        catch (Exception error) when (error is not OutOfMemoryException) { GD.Print("COMMAND_HOST Trust could not note a goal's end: " + error.GetType().Name); }
    }

    /// <summary>
    /// The Bubble's hook after a Lift's charge at the rise (CommandHostBubbleLead.StartLift): the pips just charged make the Gubble's
    /// lead a paid action (decision 69: "a Lift charged at its rise"), its window gaining if it then arrives clean, and fire it carries
    /// the player into a consequence of that paid action. A lead the player set is theirs: nothing for Trust.
    /// </summary>
    private void TrustLiftCharged(BubbleLead lead)
    {
        if (Trust.Paid(TakeTrustCharge()) && lead.Principal == CompanionPrincipal) _trustLedger.Charged(lead.ActionId);
    }

    /// <summary>What a mark's line calls a command: the ability or goal, in plain words from the host's own table.</summary>
    private string TrustWhat(string op, string? capability) => op switch
    {
        "nature.cast" or "effect.start" => capability switch
        {
            "fire" => "fire", "grow" => "Grow", "bubble_carry" => "bubble carry", "bubble_boat" => "bubble boat", "bubble_lift" => "bubble lift",
            { } ability when Rules?.Ability(ability) is { } known => known.Primitive == "particles.float" ? "bubble" : KernelJson.DisplayText(known.DisplayName, 24).ToLowerInvariant(),
            _ => "magic",
        },
        "effect.adjust" => "change to a glow",
        "goal.set" => capability switch { "lead" => "bubble lead", "fetch" => "fetch", "go_to" => "walk", "follow" => "follow", "come" => "coming to you", _ => "move" },
        "creation.place" => "build",
        "creation.revise" => "change to a build",
        "entity.remove" => "removal",
        "entity.grab" or "entity.release" or "entity.place" or "entity.push" => "move",
        "creation.activate" => "use of a build",
        "protect.lock" => "lock",
        "nature.trail" => "flower trail",
        "nature.repair" => "repair",
        "room.undo" => "undo",
        _ => "action",
    };

    /// <summary>Where a command aimed, when it named a place (a cast's centre, an effect's area, a goal's or a placement's position).</summary>
    private static Vector3? TrustPlace(string op, JsonElement args)
    {
        try
        {
            if (args.TryGetProperty("center_m", out var center)) return KernelJson.ReadVector(center);
            if (args.TryGetProperty("area", out var area) && area.TryGetProperty("center_m", out var areaCenter)) return KernelJson.ReadVector(areaCenter);
            if (args.TryGetProperty("position_m", out var position)) return KernelJson.ReadVector(position);
            if (args.TryGetProperty("placement", out var placement) && placement.TryGetProperty("position_m", out var placed)) return KernelJson.ReadVector(placed);
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or KeyNotFoundException) { }
        return null;
    }

    // ---- the clock: windows closing, lessons applied, the early warning ----

    /// <summary>
    /// Settles every window that has closed by now (real time; exact whenever it runs, so a late call loses nothing): a clean,
    /// counted window gains, at most once in two minutes; a clean fire of the Gubble's own where a consequence once followed is
    /// "lesson applied". Then the early warning. The companion bridge calls it each frame; the host before it reads or writes Trust.
    /// </summary>
    public void SettleTrust()
    {
        foreach (var (entry, clean, gained) in _trustLedger.Settle(Clock()))
        {
            if (gained && TrustCounts) MoveTrust(TrustLaws.CleanInitiative, CompanionPrincipal);
            if (clean && entry.Counted && entry is { Op: "nature.cast", Capability: "fire", Place: { } at }) ApplyLesson(at, entry.ApprovedBy ?? CompanionPrincipal);
        }
        CheckEarlyWarning();
    }

    /// <summary>
    /// "Lesson applied", from the host's own observation: the Gubble lit a fire of its own within TrustLaws.LessonPlaceM of where a
    /// consequence fact once followed one of its fires, and none followed this time. Once per lesson: the fact carries the lesson's
    /// own pin, so the journal itself (saved with the room) says which lessons are applied.
    /// </summary>
    private void ApplyLesson(Vector3 at, string directedBy)
    {
        var applied = _history.Where(e => e["kind"]?.GetValue<string>() == "consequence" && e["line"]?.GetValue<string>().StartsWith(LessonAppliedPrefix, StringComparison.Ordinal) == true)
            .Select(e => e["pin_m"]?.ToJsonString()).Where(p => p != null).ToHashSet(StringComparer.Ordinal);
        foreach (var lesson in _history)
        {
            if (lesson["kind"]?.GetValue<string>() != "consequence" || lesson["actor"]?.GetValue<string>() != CompanionPrincipal ||
                lesson["line"]?.GetValue<string>().StartsWith("The Gubble's fire", StringComparison.Ordinal) != true || lesson["pin_m"] is not JsonArray pin) continue;
            if (applied.Contains(pin.ToJsonString())) continue;
            var place = new Vector3((float)pin[0]!.GetValue<double>(), (float)pin[1]!.GetValue<double>(), (float)pin[2]!.GetValue<double>());
            if (place.DistanceTo(at) > TrustLaws.LessonPlaceM) continue;
            var name = NearestKnownPlace(place);
            var fact = Fact("consequence", LessonAppliedPrefix + " the Gubble lit a fire" + (name == null ? "" : " near " + Quoted(name)) + " again, and this time it hurt nothing.",
                CompanionPrincipal, directedBy, null, null, null);
            fact["pin_m"] = pin.DeepClone();
            AddHistory(fact);
            SaveTeam();
            return;
        }
    }

    private const string LessonAppliedPrefix = "Lesson applied:";

    /// <summary>
    /// The free early warning, from the first band (TrustLaws.EarlyWarningM, wider at each band): burning ground nearer the player
    /// than the reach, in a mode where fire hurts, warns once per approach. It costs nothing, asks nothing and changes nothing: the
    /// fire guardrails and the player's switches are untouched.
    /// </summary>
    private void CheckEarlyWarning()
    {
        var reach = TrustLaws.EarlyWarningM[Math.Clamp(TrustBand, 0, TrustLaws.EarlyWarningM.Length - 1)];
        if (reach <= 0 || Mode == "creative" || !NatureConfigured || Player == null || !IsInstanceValid(Player) || !Player.IsInsideTree())
        {
            _warned = false;
            return;
        }
        var feet = Player.GlobalPosition;
        var near = false;
        foreach (var cell in Nature.Cells.Values)
            if (cell.BurningS > 0 && new Vector2(cell.Position.X - feet.X, cell.Position.Z - feet.Z).Length() <= reach && Math.Abs(cell.Position.Y - feet.Y) <= reach)
            {
                near = true;
                break;
            }
        if (near && !_warned) EarlyWarning?.Invoke(EarlyWarningWords);
        _warned = near;
    }

    // ---- consequences ----

    /// <summary>
    /// A consequence fact was written (a new one, never an update in place). When it follows directly from a paid action of the
    /// Gubble's (paid: PaidFire or PaidCarry), open windows are spoiled and Trust loses; a fact from a free action stays the journal's
    /// lesson and moves no Trust, neither the count nor a window (decision 69).
    /// </summary>
    private void NoteTrustConsequence(bool paid)
    {
        if (!paid) return;
        SettleTrust();
        _trustLedger.Consequence(Clock());
        if (TrustCounts) MoveTrust(TrustLaws.PerConsequence, CompanionPrincipal);
    }

    /// <summary>
    /// Decision 69's trace for a fire: the Gubble's fire is traced by the room revision it was lit at (NatureFire.Revision, the
    /// revision its nature.cast committed at) to the Gubble's Fire cast at that revision, paid when the guard charged it pips. A fire
    /// not traced to a paid cast (one the pack prices at nothing, or one lit in an earlier session) is a free action's.
    /// </summary>
    private bool PaidFire(NatureFire fire) => fire.Principal == CompanionPrincipal && _trustLedger.PaidFireAt(fire.Revision);

    /// <summary>
    /// Decision 69's trace for a carry into fire ("a bubble it cast"): the Gubble's lead is paid when its Lift was charged at the rise;
    /// else the bubble holding the player is traced by its action id (Bubble.ActionId) to the Gubble's cast of it, paid when the guard
    /// charged it pips. A lead that is no lift, of a bubble the player cast or one the pack prices at nothing, is a free action's.
    /// </summary>
    private bool PaidCarry(BubbleLead lead) =>
        _trustLedger.WasPaid(lead.ActionId) ||
        (HoldsPlayer() is { Principal: CompanionPrincipal } bubble && ReferenceEquals(bubble.CarriedBy, lead) && _trustLedger.WasPaid(bubble.ActionId));

    // ---- the price: the bands' discount on the Gubble's own ideas ----

    /// <summary>
    /// The price with Trust's discount (CommandHostEnergy.PriceFor): the Gubble's own-idea price at its band, every ability alike, Fire
    /// included (decision 42); the player's price, a wish's (charged as ordered) and band 0 are the pack's. Whole pips, at least one.
    /// </summary>
    private int TrustPrice(string capability, string principal, int price)
    {
        if (principal == PlayerPrincipal || price <= 0) return price;
        var band = TrustBand;
        if (band == 0 || WishRunning() || _vitalsRules.Cost?.Prices.TryGetValue(capability, out var listed) != true) return price;
        return Trust.OwnIdeaPrice(listed!.Ordered, listed.OwnIdeaFactor, band);
    }

    // ---- the player's commands ----

    /// <summary>
    /// receipt.mark, the player's alone (refused for the companion before this, as every player-only op): yes ("yes, like that")
    /// or not_that on a receipt of one of the Gubble's commands from this session, Bubbles' leads included, each receipt once. The
    /// journal keeps the mark as one of the AI's lessons; Trust moves +2 or -2 in Challenge and Live. Transient, no data.
    /// </summary>
    private JsonObject MarkReceipt(JsonElement args, string principal, string actionId, string fingerprint, bool preview)
    {
        if (principal != PlayerPrincipal) throw new Refusal("permission_denied", "Only the player can do that, directly in the game.", "$.op");
        if (IslandHeld) throw new Refusal("not_ready", TeamNotice);
        SettleTrust();
        var entry = _trustLedger.Find(Str(args, "action_id")!) ?? throw new Refusal("target_not_found", MarkNotFound, "$.args.action_id");
        if (entry.Mark != null) throw new Refusal("invalid_args", MarkedOnce, "$.args.action_id");
        if (preview) return Previewed("receipt.mark", principal, actionId);
        var mark = Str(args, "mark")!;
        entry.Mark = mark;
        var name = entry.Place is { } at ? NearestKnownPlace(at) : null;
        AddHistory(Fact("consequence", $"You marked the Gubble's {entry.What}" + (name == null ? "" : " near " + Quoted(name)) + (mark == "yes" ? ": yes, like that." : ": not that."),
            PlayerPrincipal, PlayerPrincipal, null, null, entry.Place));
        if (TrustCounts) MoveTrust(mark == "yes" ? TrustLaws.MarkYes : TrustLaws.MarkNotThat, PlayerPrincipal);
        SaveTeam();
        return Transient("receipt.mark", principal, actionId, fingerprint, new JsonArray(), null);
    }

    /// <summary>
    /// trust.reset, the player's alone (beside "Change my AI", decision 1; the game never resets it): Trust back to base, every open
    /// window closed with no gain. The journal says so. Transient, no data.
    /// </summary>
    private JsonObject ResetTrust(string principal, string actionId, string fingerprint, bool preview)
    {
        if (principal != PlayerPrincipal) throw new Refusal("permission_denied", "Only the player can do that, directly in the game.", "$.op");
        if (IslandHeld) throw new Refusal("not_ready", TeamNotice);
        if (preview) return Previewed("trust.reset", principal, actionId);
        _trustLedger.Reset();
        _warned = false;
        WriteTrust(0);
        AddHistory(Fact("consequence", "You reset the Gubble's Trust: " + Trust.Phrase(0) + ".", PlayerPrincipal, PlayerPrincipal, null, null, null));
        SaveTeam();
        return Transient("trust.reset", principal, actionId, fingerprint, new JsonArray(), null);
    }

    /// <summary>The player's mark through the game's own controls (the HUD's, later): receipt.mark as the player.</summary>
    public JsonObject PlayerMarkReceipt(string gubbleActionId, bool yes) =>
        PlayerCommand("receipt.mark", new JsonObject { ["action_id"] = gubbleActionId, ["mark"] = yes ? "yes" : "not_that" });

    /// <summary>The player's reset (the menu's, beside "Change my AI"): trust.reset as the player.</summary>
    public JsonObject PlayerResetTrust() => PlayerCommand("trust.reset", new JsonObject());

    // ---- the count ----

    /// <summary>Trust moves by delta (bounded); when its words' tier changes, the journal says so, in the cause's name.</summary>
    private void MoveTrust(int delta, string actor)
    {
        var before = TrustCount;
        var after = Trust.Clamp((long)before + delta);
        if (after == before) return;
        WriteTrust(after);
        if (Trust.Tier(before) == Trust.Tier(after)) return;
        AddHistory(Fact("consequence", Trust.ChangeLine(Trust.Tier(before), Trust.Tier(after)), actor, actor, null, null, null));
        SaveTeam();
    }

    /// <summary>
    /// The count into the team file (Saves/TeamFile.WithTrust), at once; without a team file, for this session. A team file that
    /// cannot be written keeps the count for the session and says so once.
    /// </summary>
    private void WriteTrust(int count)
    {
        if (Team is not { } team)
        {
            _trustUnfiled = count;
            return;
        }
        var next = team.WithTrust(count);
        if (ReferenceEquals(next, team)) return;
        try { _teamStore?.Save(next); }
        catch (TeamFileException) { if (TeamNotice.Length == 0) TeamNotice = "Trust could not be saved to the team file; it counts for this session."; }
        Team = next;
    }
}
