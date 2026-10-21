using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using GarageGame.Native.Kernel;
using GarageGame.Native.Saves;

namespace GarageGame.Native.Ui;

/// <summary>
/// One team as its card shows it (docs/game/MECHANICS.md section 3): the person (the name and the costume), the numbers they play
/// with, the islands they have visited, and the Gubble tied to them (its name, colour, expertise and, in words, Trust). The numbers
/// are those of the mode the team played last, under the island rules the menu reads (a team that has not played yet shows the
/// starting ones). A team file that does not read is listed, greyed, with a plain reason, and never overwritten.
/// </summary>
public sealed record TeamCard(string TeamId, TeamFile? File, string Problem, int IslandsVisited, int ExpertiseCount, string LastMode, DateTime? LastPlayedUtc,
    int Hearts, double StaminaM, int Pips)
{
    public bool Readable => File != null;
    public string DisplayName => File?.DisplayName ?? TeamId;
}

/// <summary>The teams on this machine, read from the team folder (docs/game/MECHANICS.md section 2; TeamLayout).</summary>
public static class TeamCards
{
    public const int MaxTeams = 64;

    public static IReadOnlyList<TeamCard> Read(TeamLayout layout, IslandRules? rules)
    {
        var cards = new List<TeamCard>();
        if (!Directory.Exists(layout.TeamsRoot)) return cards;
        foreach (var folder in Directory.GetDirectories(layout.TeamsRoot).OrderBy(f => f, StringComparer.Ordinal))
        {
            var id = Path.GetFileName(folder);
            if (!TeamLayout.ValidTeamId(id) || cards.Count >= MaxTeams || !System.IO.File.Exists(layout.TeamFile(id))) continue;
            cards.Add(CardOf(layout, id, rules));
        }
        return cards.OrderByDescending(c => c.LastPlayedUtc ?? DateTime.MinValue).ThenBy(c => c.TeamId, StringComparer.Ordinal).ToList();
    }

    public static TeamCard CardOf(TeamLayout layout, string id, IslandRules? rules)
    {
        TeamFile? file = null;
        var problem = "";
        try { file = new TeamStore(layout.TeamFile(id), id).Load(); }
        catch (TeamFileException error) { problem = error.Message; }
        var expertise = Expertise(layout, id);
        var (mode, at, vitals) = LastPlayed(layout, id);
        var vr = VitalsRules.For(rules, mode, "standard", file?.KnackRanks, expertise);
        var body = vitals ?? vr.Fresh();
        return new TeamCard(id, file, problem, Visited(layout, id), expertise, mode, at, vr.MaxHearts(body), Math.Round(vr.MaxStaminaOf(body), 2), vr.MaxPips);
    }

    /// <summary>The expertise the team's Gubble has learned (0 when the store does not read yet: the card never stops for it).</summary>
    private static int Expertise(TeamLayout layout, string id)
    {
        try
        {
            var path = layout.ExpertiseFile(id);
            return System.IO.File.Exists(path) ? LocalGlowProgressStore.Parse(System.IO.File.ReadAllBytes(path)).ExpertiseCount : 0;
        }
        catch (Exception error) when (error is GlowProgressException or IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>The mode the team's vitals were written in last, when, and that mode's vitals (Creative, no time and none when it has not played).</summary>
    private static (string Mode, DateTime? At, PlayerVitals? Vitals) LastPlayed(TeamLayout layout, string id)
    {
        string mode = "creative";
        DateTime? latest = null;
        PlayerVitals? vitals = null;
        foreach (var candidate in new[] { "creative", "challenge" })
        {
            try
            {
                var path = layout.VitalsFile(id, candidate);
                if (!System.IO.File.Exists(path)) continue;
                var document = LocalPlayerVitalsStore.ParseMode(System.IO.File.ReadAllBytes(path));
                if (latest != null && document.WrittenUtc <= latest) continue;
                (mode, latest, vitals) = (candidate, document.WrittenUtc, document.Vitals);
            }
            catch (Exception error) when (error is PlayerVitalsException or IOException or UnauthorizedAccessException) { }
        }
        return (mode, latest, vitals);
    }

    /// <summary>How many different islands the team has a save on, in any mode.</summary>
    public static int Visited(TeamLayout layout, string id)
    {
        var rooms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mode in new[] { "creative", "challenge" })
        {
            var islands = layout.IslandsRoot(id, mode);
            if (!Directory.Exists(islands)) continue;
            foreach (var room in Directory.GetDirectories(islands))
                if (Directory.EnumerateFiles(room, "inventions.json", SearchOption.AllDirectories).Any()) rooms.Add(Path.GetFileName(room));
        }
        return rooms.Count;
    }
}

/// <summary>
/// Making and changing a team in the menu (docs/game/MECHANICS.md sections 2, 3 and 7): the "New team" panel names the person, picks
/// the costume, names the Gubble and picks its colour, and picks a knack or "surprise me" for a random one. The team file is
/// the team's alone: the menu writes it through the team store, atomically, and never over a file that is already there. Swapping
/// a knack is free in the menu (TeamFile.WithKnack: a swapped-out knack keeps the rank it earned).
/// </summary>
public static class TeamMaker
{
    public const string DefaultId = "team";
    public const int MaxIdLength = 32;

    /// <summary>The knacks the island offers a new team: the pack's available ones, in the pack's order.</summary>
    public static IReadOnlyList<KnackRule> Offered(IslandRules? rules) =>
        rules?.Knacks?.Table.Where(k => k.Available).ToList() ?? new List<KnackRule>();

    /// <summary>What the screen calls a knack: the pack's name (cleaned), else its id in words.</summary>
    public static string KnackName(IslandRules? rules, string id) => rules?.Names?.Knack(id) ?? NamesRules.TodayKnack(id);

    /// <summary>
    /// A team id from the name the person typed: lowercase letters, digits, - and _, starting with a letter (a name that gives
    /// none is "team"), never a Windows device's name, and not one that is taken (a number is added: mira, mira-2).
    /// </summary>
    public static string IdFrom(string name, ICollection<string> taken)
    {
        var slug = new StringBuilder();
        foreach (var c in (name ?? "").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9') slug.Append(lower);
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }
        var text = slug.ToString().Trim('-');
        if (text.Length == 0 || text[0] is >= '0' and <= '9') text = DefaultId + (text.Length > 0 ? "-" + text : "");
        if (text.Length > MaxIdLength) text = text[..MaxIdLength].TrimEnd('-');
        var id = text;
        for (var n = 2; !TeamLayout.ValidTeamId(id) || taken.Contains(id); n++) id = (TeamLayout.ValidTeamId(text) ? text : DefaultId) + "-" + n.ToString(CultureInfo.InvariantCulture);
        return id;
    }

    /// <summary>
    /// The new team's file, written (never over one that is there). <paramref name="knack"/> is a knack id, "" for none, or null
    /// for "surprise me" (one the island offers, picked with <paramref name="random"/>). Throws TeamFileException with the reason.
    /// </summary>
    public static TeamFile Create(TeamLayout layout, IslandRules? rules, string person, string costume, string gubbleName, string gubbleColour, string? knack,
        DateTime nowUtc, Random? random = null)
    {
        var taken = Directory.Exists(layout.TeamsRoot) ? Directory.GetDirectories(layout.TeamsRoot).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal) : new HashSet<string>();
        var shownPerson = KernelText.DisplayText((person ?? "").Trim(), TeamFile.MaxNameLength);
        if (shownPerson.Length == 0 || shownPerson == "Refused.") throw new TeamFileException("Give the team a name first.");
        var shownGubble = KernelText.DisplayText((gubbleName ?? "").Trim(), TeamFile.MaxNameLength);
        if (shownGubble.Length == 0 || shownGubble == "Refused.") shownGubble = CompanionAvatar.DefaultName;
        var id = IdFrom(shownPerson, taken);
        var team = TeamFile.Create(id, nowUtc, shownPerson, costume.Length == 0 ? TeamFile.PrototypeCostume : costume, shownGubble, gubbleColour);
        var chosen = knack;
        if (chosen == null)
        {
            var offered = Offered(rules);
            chosen = offered.Count == 0 ? "" : offered[(random ?? Random.Shared).Next(offered.Count)].Knack;
        }
        if (chosen.Length > 0) team = team.WithKnack(1, chosen, rules?.Knacks);
        var store = new TeamStore(layout.TeamFile(id), id);
        if (store.Load() != null) throw new TeamFileException("A team with that name is already here.");
        store.Save(team);
        return team;
    }

    /// <summary>
    /// "Redraw yourself any day; keep everything" (docs/game/MECHANICS.md section 1): the person's name, the costume, the Gubble's name and
    /// colour changed in the team file, and the knack swapped when one is named (null or "" keeps it). The team's id, creation time,
    /// Trust, ranks and slots stay; so does everything else on disk. Written through the team store; a file that would not read is kept.
    /// </summary>
    public static TeamFile Update(TeamLayout layout, IslandRules? rules, string teamId, string person, string costume, string gubbleName, string gubbleColour, string? knack)
    {
        var store = new TeamStore(layout.TeamFile(teamId), teamId);
        var team = store.Load() ?? throw new TeamFileException("This team has no team file.");
        var shownPerson = KernelText.DisplayText((person ?? "").Trim(), TeamFile.MaxNameLength);
        if (shownPerson.Length == 0 || shownPerson == "Refused.") throw new TeamFileException("Give the team a name first.");
        var shownGubble = KernelText.DisplayText((gubbleName ?? "").Trim(), TeamFile.MaxNameLength);
        if (shownGubble.Length == 0 || shownGubble == "Refused.") shownGubble = CompanionAvatar.DefaultName;
        var next = (team with { DisplayName = shownPerson, Costume = costume.Length == 0 ? TeamFile.PrototypeCostume : costume, GubbleName = shownGubble, GubbleColour = gubbleColour }).Checked();
        if (!string.IsNullOrEmpty(knack)) next = next.WithKnack(1, knack, rules?.Knacks);
        if (!next.Serialize().AsSpan().SequenceEqual(team.Serialize())) store.Save(next);
        return next;
    }

    /// <summary>The team with this knack in this slot, written (a free swap in the menu).</summary>
    public static TeamFile SwapKnack(TeamLayout layout, IslandRules? rules, string teamId, int slot, string knack)
    {
        var store = new TeamStore(layout.TeamFile(teamId), teamId);
        var team = store.Load() ?? throw new TeamFileException("This team has no team file.");
        var next = team.WithKnack(slot, knack, rules?.Knacks);
        if (!ReferenceEquals(next, team)) store.Save(next);
        return next;
    }
}
