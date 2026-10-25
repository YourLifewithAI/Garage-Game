using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace GarageGame.Native.Kernel;

/// <summary>
/// Consequences, and what the Gubble's AI learns from them (docs/magic/README.md "Fire"). The founder, 13 October: "Let
/// things like fire or other actions that might hurt a player, hurt the player. This becomes feedback for the AI and there
/// should be a learning mechanism of sorts that helps the AI make better choices later on." So the Gubble's fire is never
/// held for the player's sake; it is kept in bounds by guardrails, and what it does comes back to the AI:
/// - guardrails: the player's own setting "Let the Gubble use fire" (the profile's gubble_fire, on unless the player turns
///   it off; only the player's control changes it, no command reaches it), one fire of its own at a time, and
///   NatureLaws.CompanionFireCooldownS after its last one went out. Since decision 82 (the second recorded play) nothing but
///   burning out or Snuff ends a fire: the player's Stop (X) stops the Gubble and puts no fire out, and the player's own Fire
///   waits for the Gubble's as for any fire (one fire on the island at a time), unless it is Snuff aimed at it.
/// - feedback: when the Gubble's fire hurts the player, a fixed-format notice (what happened, which fire, health before and
///   after, whether the player had to recover; no prose from anyone) rides on the Gubble's next result, of any kind, once.
///   Until then any query returns it (player.status is the cheapest).
/// - learning across sessions: the host writes one consequence fact per fire into the team's journal, in the game's own
///   words ("The Gubble's fire burned you near ..."), naming only places the team knows. journal.read with kind
///   "consequence" reads the lessons back; the journal is the AI's memory.
/// - Trust (M8, CommandHostTrust): each new consequence fact that follows directly from a paid action of the Gubble's costs Trust
///   and spoils the windows of clean initiative open over it; since M8 a build the Gubble's fire burns writes one too (one fact per
///   fire, updated in place as it burns more), and so does the Gubble's lead carrying the player into fire that hurts them, whoever
///   lit it (one fact per lead, NoteLeadHarm). Decision 69: a fire is traced to its cast by its revision (PaidFire), a carry to its
///   lead's Lift or the Gubble's cast of the bubble (PaidCarry); a fact from a free action is written all the same, as the AI's
///   lesson, and moves no Trust.
/// - The fall after the Gubble's own pop (decision 96, the second playtest round): the Gubble popped the bubble holding the player by
///   its own action and the fall hurt them: one fact per pop, which costs Trust (NotePopFall). A pop at the time limit or by the
///   player's own cancel never writes one; nor does a fall that ends on a face the player grabbed.
/// </summary>
public partial class CommandHost
{
    /// <summary>The player's profile key (RoomHud.ProfilePath, [profile] section) for "Let the Gubble use fire".</summary>
    public const string FireProfileKey = "gubble_fire";
    /// <summary>The setting's value when the player never chose: on (the founder's decision is consequences, not prohibition).</summary>
    public const bool DefaultGubbleFire = true;
    public const string FireOffMessage = "The player has turned off \"Let the Gubble use fire\".";
    /// <summary>Unread notices kept for the Gubble, the oldest dropped first.</summary>
    public const int MaxNotices = 8;
    /// <summary>A consequence fact names the nearest thing the team knows within this distance of where it happened.</summary>
    public const float ConsequencePlaceM = 1.5f;

    private bool? _gubbleFire;
    /// <summary>The weather clock when the Gubble's own fire was last seen burning (its cooldown counts from there).</summary>
    private double _companionFireSeenS = double.NegativeInfinity;
    private readonly List<JsonObject> _notices = new();
    /// <summary>The unread notice still gathering one fire's harm (updated in place until the Gubble reads it).</summary>
    private JsonObject? _openNotice;
    /// <summary>The fire (by the room revision it was lit at) whose consequence fact is written, and that fact.</summary>
    private int _factFire = -1;
    private JsonObject? _factEntry;
    private bool _factRecovered;
    /// <summary>M8: the fire (by its room revision) whose burnt-build fact is written, that fact, and the builds it names.</summary>
    private int _buildFire = -1;
    private JsonObject? _buildEntry;
    private readonly List<string> _buildsBurnt = new();

    /// <summary>
    /// The player's setting "Let the Gubble use fire": read once from the player's profile (never room state), then whatever
    /// the player's own control sets this session. A profile that exists but does not load counts as off.
    /// </summary>
    public bool GubbleFireAllowed => _gubbleFire ??= LoadGubbleFire(RoomHud.ProfilePath);

    /// <summary>The player's control (the HUD's setting) and tests. No command reaches this.</summary>
    public void SetGubbleFire(bool allowed) => _gubbleFire = allowed;

    public static bool LoadGubbleFire(string profilePath)
    {
        var config = new ConfigFile();
        var loaded = config.Load(profilePath);
        if (loaded == Error.FileNotFound) return DefaultGubbleFire;
        if (loaded != Error.Ok) return false;
        var value = config.GetValue("profile", FireProfileKey, DefaultGubbleFire);
        return value.VariantType == Variant.Type.Bool && value.AsBool();
    }

    /// <summary>The notices the Gubble has not read yet (tests and the HUD).</summary>
    public IReadOnlyList<JsonObject> UnreadNotices => _notices;

    // ---- the guardrails ----

    /// <summary>The Gubble's Fire, preview or cast: the player's setting, one fire of its own at a time, and the wait after its last.</summary>
    private void CheckCompanionFire()
    {
        if (!GubbleFireAllowed) throw new Refusal("permission_denied", FireOffMessage, "$.args.capability");
        if (Nature.Fire?.Principal == CompanionPrincipal)
            throw new Refusal("target_busy", "Your fire is still burning: one fire at a time. Wait for it to go out.", "$.args.capability", retryable: true);
        var wait = NatureLaws.CompanionFireCooldownS - (Nature.WeatherS - _companionFireSeenS);
        if (wait > 0)
            throw new Refusal("rate_limited", $"Wait about {Math.Ceiling(wait):0} seconds after your last fire before lighting another.", "$.args.capability",
                retryable: true, actual: JsonValue.Create(Math.Round(wait, 1)));
    }

    /// <summary>Notes that the Gubble's fire burns now (its cooldown counts from the last time it was seen burning).</summary>
    private void SeeCompanionFire()
    {
        if (Nature.Fire?.Principal == CompanionPrincipal) _companionFireSeenS = Nature.WeatherS;
    }

    // ---- the feedback ----

    /// <summary>
    /// A fire hurt the player: when the Gubble's lead carried them into it, that lead's consequence fact, whoever lit the fire
    /// (NoteLeadHarm); when the fire is the Gubble's, the Gubble's notice and the fire's consequence fact.
    /// </summary>
    private void NoteFireHarm(NatureFire fire, PlayerHarm harm, Vector3 at)
    {
        NoteLeadHarm(at);
        if (fire.Principal != CompanionPrincipal) return;
        if (_openNotice != null && _notices.Contains(_openNotice) && _openNotice["fire_revision"]!.GetValue<int>() == fire.Revision)
        {
            _openNotice["health_after"] = Health(harm.After);
            _openNotice["recovered"] = harm.Recovered || _openNotice["recovered"]!.GetValue<bool>();
            _openNotice["at_utc"] = Now();
        }
        else
        {
            _openNotice = new JsonObject
            {
                ["kind"] = "fire_hurt_player", ["fire_revision"] = fire.Revision, ["health_before"] = Health(harm.Before), ["health_after"] = Health(harm.After),
                ["recovered"] = harm.Recovered, ["at_utc"] = Now(),
            };
            _notices.Add(_openNotice);
            while (_notices.Count > MaxNotices) _notices.RemoveAt(0);
        }
        WriteConsequence(fire, harm, at);
    }

    /// <summary>A notice's health, within the contract's bound (consequence_notice: 720, 30 hearts with Big heart).</summary>
    private static double Health(float value) => Math.Round(Math.Clamp(value, 0, PlayerVitals.ContractMaxHealth), 1);

    /// <summary>
    /// The Gubble caught up with the player (CompanionAvatar.CatchUpM, packet S8 of the 10x world): it did not travel there, so its
    /// AI is told on its next result, once, as the game's own fixed form (contracts: $defs/catch_up_notice): where it was, where it
    /// is now beside the player, how far that is and when. The goal it had goes on.
    /// </summary>
    private void NoteCatchUp(Vector3 from, Vector3 to)
    {
        _notices.Add(new JsonObject
        {
            ["kind"] = "caught_up", ["from_m"] = KernelJson.Vector(from), ["to_m"] = KernelJson.Vector(to), ["distance_m"] = Math.Round(from.DistanceTo(to), 2),
            ["at_utc"] = Now(),
        });
        while (_notices.Count > MaxNotices) _notices.RemoveAt(0);
    }

    /// <summary>
    /// The Gubble's next result, of any kind, carries every notice it has not read, once; a result that would grow past the
    /// result bound keeps them for the next one. Further harm by the same fire after that starts a new notice.
    /// </summary>
    private void DeliverNotices(JsonObject result)
    {
        if (_notices.Count == 0) return;
        result["notices"] = new JsonArray(_notices.Select(n => (JsonNode?)n.DeepClone()).ToArray());
        if (CanonicalJson.Bytes(result).Length > MaxResultBytes)
        {
            result.Remove("notices");
            return;
        }
        _notices.Clear();
        _openNotice = null;
    }

    /// <summary>
    /// One consequence fact per fire that hurt the player, in the game's own words and saved with the journal at once:
    /// "The Gubble's fire burned you near "the jetty"." The place is the nearest thing the team knows (never one it has not
    /// seen), quoted as world text; the fact is updated in place if the same fire later puts the player down.
    /// </summary>
    private void WriteConsequence(NatureFire fire, PlayerHarm harm, Vector3 at)
    {
        if (_factFire == fire.Revision && _factEntry != null)
        {
            if (!harm.Recovered || _factRecovered) return;
            _factRecovered = true;
            _factEntry["line"] = KernelJson.DisplayText(ConsequenceLine(NearestKnownPlace(at), recovered: true), 200);
            SaveTeam();
            return;
        }
        _factFire = fire.Revision;
        _factRecovered = harm.Recovered;
        _factEntry = Fact("consequence", ConsequenceLine(NearestKnownPlace(at), harm.Recovered), CompanionPrincipal, fire.DirectedBy, null, null, at);
        AddHistory(_factEntry);
        // M8: a new consequence fact costs Trust and spoils the open windows of clean initiative, when the fire was paid (decision 69).
        NoteTrustConsequence(PaidFire(fire));
        SaveTeam();
    }

    /// <summary>
    /// M8: a build the Gubble's fire burns, in a mode with consequences (in Creative the player said yes first): one consequence fact
    /// per fire, in the game's own words ("The Gubble's fire burned your "tower"."), updated in place as the same fire burns more
    /// ("... and 2 more builds"), naming a build only as the team knows it. A new fact costs Trust, as the player's hurt does, when the
    /// fire was a paid cast (decision 69).
    /// </summary>
    private void NoteBuildBurnt(NatureFire fire, string creation, Vector3 at)
    {
        if (fire.Principal != CompanionPrincipal || Mode == "creative") return;
        if (_buildFire == fire.Revision && _buildEntry != null)
        {
            if (_buildsBurnt.Contains(creation, StringComparer.Ordinal)) return;
            _buildsBurnt.Add(creation);
            _buildEntry["line"] = KernelJson.DisplayText(BuildBurntLine(_buildsBurnt[0]), 200);
            _buildEntry["at_utc"] = EntryTime();
            if (_buildEntry["subject"]?["entities"] is JsonArray named && named.Count < 8 && KnownToTeam(creation)) named.Add(creation);
            SaveTeam();
            return;
        }
        _buildFire = fire.Revision;
        _buildsBurnt.Clear();
        _buildsBurnt.Add(creation);
        var view = TeamView(creation);
        _buildEntry = Fact("consequence", BuildBurntLine(creation), CompanionPrincipal, fire.DirectedBy, view != null ? creation : null, view?.Name, view?.Pin ?? at);
        AddHistory(_buildEntry);
        NoteTrustConsequence(PaidFire(fire));
        SaveTeam();
    }

    /// <summary>The burnt-build fact's line: the first build by the name the team knows it by, and how many more the same fire burned.</summary>
    private string BuildBurntLine(string first)
    {
        var name = TeamView(first)?.Name;
        var more = _buildsBurnt.Count - 1;
        return "The Gubble's fire burned " + (name == null ? "one of your builds" : "your " + Quoted(name)) +
            (more == 0 ? "." : $" and {more} more {(more == 1 ? "build" : "builds")}.");
    }

    private static string ConsequenceLine(string? place, bool recovered) =>
        "The Gubble's fire burned you" + (place == null ? "" : " near " + Quoted(place)) + (recovered ? " until you had to recover." : ".");

    /// <summary>
    /// The W2 review's minor: the Gubble's lead carried the player, in the bubble that still holds them, into fire that hurts them,
    /// whoever lit it. One consequence fact per lead, in the game's own words ("The Gubble's bubble carried you into fire near
    /// ..."), saved with the journal at once; when it follows from a paid action of the Gubble's (its lead's Lift, or its cast of the
    /// bubble: PaidCarry) it costs Trust and spoils the windows open over it, the lead's own (still running or just arrived) among
    /// them, so the lead earns nothing; from free actions it moves no Trust (decision 69). A lead the player ordered is theirs.
    /// </summary>
    private void NoteLeadHarm(Vector3 at)
    {
        if (GubbleLeadHoldingThePlayer() is not { HarmNoted: false } lead) return;
        lead.HarmNoted = true;
        var place = NearestKnownPlace(at);
        var directedBy = _trustLedger.Find(lead.ActionId)?.ApprovedBy ?? CompanionPrincipal;
        AddHistory(Fact("consequence", LeadHarmPrefix + (place == null ? "" : " near " + Quoted(place)) + ".", CompanionPrincipal, directedBy, null, null, at));
        NoteTrustConsequence(PaidCarry(lead));
        SaveTeam();
    }

    /// <summary>The start of a lead's consequence fact (the mock simulates no fire, so writes none).</summary>
    public const string LeadHarmPrefix = "The Gubble's bubble carried you into fire";

    /// <summary>The start of the consequence fact for a fall that hurt the player after the Gubble popped their bubble (decision 96).</summary>
    public const string PopFallPrefix = "The Gubble popped your bubble and you fell";

    /// <summary>
    /// The Gubble popped the carry holding the player by its own action (its stop, its effect.stop, a new goal of its own ending its lead,
    /// its recast, its runner ending the wish whose lead it was; BubbleEnd.PoppedByGubble, decision 110), and the fall from there has not
    /// ended yet (decision 96). No pop from the world sets it, and every other pop of a carry holding the player clears it (EndBubble).
    /// </summary>
    private bool _popFallPending;

    private void NotePopFallPending() => _popFallPending = true;

    /// <summary>The fall after the Gubble's pop ended some other way: a face grabbed (the climb's from then on), a bubble holding them again, a recovery.</summary>
    private void ForgetPopFall() => _popFallPending = false;

    /// <summary>
    /// Decision 96 ("yes, but only for a pop the Gubble causes by its own action, never the time limit or the player's own cancel"): the
    /// landing that ends the fall after the Gubble's own pop (ObservePlayerFall, the body's first grounded or water-caught tick), when it
    /// hurt the player, is one consequence fact in the game's own words ("The Gubble popped your bubble and you fell near ..."), saved
    /// with the journal at once, that costs Trust and spoils the windows open over it, as the Gubble's fire's harm does (decision 96 names
    /// the pop itself as the action, whoever cast or lifted the bubble). A landing that did not hurt ends it with nothing written. In
    /// Creative nothing hurts, so nothing is written.
    /// </summary>
    private void NotePopFall(PlayerHarm? harm, Vector3 at)
    {
        if (harm is not { } hurt) return;
        var place = NearestKnownPlace(at);
        var line = PopFallPrefix + (place == null ? "" : " near " + Quoted(place)) + (hurt.Recovered || hurt.After <= 0 ? " until you had to recover." : ".");
        AddHistory(Fact("consequence", line, CompanionPrincipal, CompanionPrincipal, null, null, at));
        NoteTrustConsequence(paid: true);
        SaveTeam();
    }

    /// <summary>The name of the thing the team knows nearest a place, within ConsequencePlaceM; null when it knows none there.</summary>
    private string? NearestKnownPlace(Vector3 at)
    {
        string? name = null;
        var best = ConsequencePlaceM;
        foreach (var (id, seen) in TeamMemory().Entries)
        {
            if (!KnownIdPattern.IsMatch(id)) continue;
            var distance = Distance(seen.Summary, at);
            if (distance > best) continue;
            best = distance;
            name = seen.Summary["display_name"]?.GetValue<string>();
        }
        return name;
    }
}
