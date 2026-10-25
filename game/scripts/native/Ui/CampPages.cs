using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Ui;

/// <summary>
/// The camp's pages (MECHANICS.md decision 87: "the place to manage magic, see the expertise found, check stats and knacks, manage the
/// inventory and spend Fractals"), written from what already exists and nothing else: the abilities as the journal lists
/// them, the expertise found (the journal's own list), the player's hearts, bar, pips and knacks (the host's vitals and the team file, in
/// the meters' own words), the Fractals and their two buttons (FractalSpend, decision 62's camp rules, as the journal's Character page has
/// them) and the Inventory page, live (InventoryPages: the chest with a Take for each entry, the pockets with a Store). No new rules:
/// every number is the host's, every button the player's own command through the host (fractal.spend, knack.set, inventory.take and
/// store) or a panel that exists (the Magic panel). Lines are JournalLines, as the journal's pages.
/// </summary>
public static class CampPages
{
    public const string Magic = "Magic";
    public const string You = "You";
    public const string Inventory = "Inventory";
    /// <summary>The Magic page's button: the Magic panel itself (Grow, Fire, Glow's lights, wishes), which the camp opens over its screen.</summary>
    public const string MagicTarget = "camp:magic";
    /// <summary>A knack's button: "knack:&lt;slot&gt;:&lt;knack id&gt;" (the player's knack.set).</summary>
    public const string KnackPrefix = "knack:";

    /// <summary>The pages in order, by their names on screen (the expertise and Fractals pages read the pack's own words).</summary>
    public static string[] Titles(CommandHost? host) => new[] { Magic, MeterWords.Name(host, "expertise"), You, MeterWords.Name(host, "fractals"), Inventory };

    /// <summary>One page's lines as the host's data writes them now (index into Titles).</summary>
    public static List<JournalLine> Build(int page, CommandHost host) => page switch
    {
        0 => MagicLines(host),
        1 => ExpertiseLines(host),
        2 => YouLines(host),
        3 => FractalSpend.Lines(host),
        _ => InventoryLines(host),
    };

    private static JournalLine Heading(string text) => new(JournalInk.Heading, text);
    private static JournalLine Text(string text) => new(JournalInk.Text, text);
    private static JournalLine Faint(string text) => new(JournalInk.Faint, text);

    /// <summary>The Gubble's magic here, as the journal's Abilities page lists it, and the Magic panel to use it.</summary>
    public static List<JournalLine> MagicLines(CommandHost host)
    {
        var lines = new List<JournalLine> { new(JournalInk.Button, $"Open the Magic panel · {PlayerControls.KeyLabel(Act.HudMagic)}", MagicTarget) };
        // The keys below are the hotbar's magic side (decision 128): Tab brings it down while the pockets are.
        lines.Add(Faint($"On the bar at the bottom: {PlayerControls.KeyLabel(Act.BarLayer)} brings the magic side down, then each one's key casts it (the right button's hold still opens the wheel)."));
        lines.AddRange(JournalPages.AbilityLines(host));
        return lines;
    }

    /// <summary>The expertise found, the journal's own list (Glow's, then the other abilities').</summary>
    public static List<JournalLine> ExpertiseLines(CommandHost host)
    {
        var lines = new List<JournalLine> { Heading("Magic we found") };
        lines.AddRange(JournalPages.GlowLines(host));
        lines.AddRange(JournalPages.OtherLines(host));
        return lines;
    }

    /// <summary>You: the hearts, the bar and the pips in the meters' words, and the knacks the team file holds, with the camp's knack.set.</summary>
    public static List<JournalLine> YouLines(CommandHost host)
    {
        var lines = new List<JournalLine>();
        var rules = host.BodyRules;
        var vitals = host.Vitals;
        lines.Add(Heading("You"));
        if (host.Mode == "creative")
            lines.Add(Faint($"Creative: nothing hurts here, and climbing uses no {MeterWords.Lower(host, "stamina")}."));
        lines.Add(Text(MeterMath.HeartsNumbers(MeterWords.Name(host, "hearts"), rules.Quarters(vitals), rules.MaxQuarters(vitals))));
        lines.Add(Text(MeterMath.BarNumbers(MeterWords.Name(host, "stamina"), vitals.Stamina, vitals.MaxStamina)));
        if (rules.HasPool) lines.Add(Text(MeterMath.PipsNumbers(MeterWords.Name(host, "pips"), Math.Max(0, vitals.Pips), rules.MaxPips)));
        lines.Add(Heading("Knacks"));
        if (host.Team is not { } team)
        {
            lines.Add(Text(host.TeamNotice.Length > 0 ? host.TeamNotice : "Knacks belong to a team: choose one at the opening menu."));
            return lines;
        }
        var table = host.Rules?.Knacks?.Table ?? Array.Empty<KnackRule>();
        if (team.Knacks.Count == 0) lines.Add(Text("No knack chosen yet."));
        foreach (var chosen in team.Knacks.OrderBy(k => k.Slot))
        {
            var rule = table.FirstOrDefault(k => k.Knack == chosen.Knack);
            lines.Add(new JournalLine(JournalInk.Title, $"{MeterWords.Knack(host, chosen.Knack)} · slot {chosen.Slot}, rank {chosen.Rank}"));
            if (rule != null) lines.Add(Faint(KnackWords.Describe(rule)));
        }
        // The first slot's knack can be swapped here, at the camp (decision 71, knack.set: the host checks the slot, the island's table and the camp).
        var offered = table.Where(k => k.Available && team.Knacks.All(c => c.Knack != k.Knack)).OrderBy(k => k.Knack, StringComparer.Ordinal).ToList();
        if (offered.Count > 0)
        {
            lines.Add(Faint(team.Knacks.Any(k => k.Slot == 1) ? "Swap the first one for another, here at the camp:" : "Choose one, here at the camp:"));
            foreach (var knack in offered)
                lines.Add(new JournalLine(JournalInk.Button, $"{MeterWords.Knack(host, knack.Knack)} · {KnackWords.Describe(knack)}", $"{KnackPrefix}1:{knack.Knack}", host.PlayerAtCamp()));
        }
        lines.Add(Faint(team.SlotsOpen == 1 ? "One knack slot is open; the next opens at a summit." : $"{team.SlotsOpen} knack slots are open."));
        return lines;
    }

    /// <summary>The inventory, live (decision 87): the chest with a Take for each entry, the pockets with a Store (InventoryPages.CampLines).</summary>
    public static List<JournalLine> InventoryLines(CommandHost host) => InventoryPages.CampLines(host);

    public static string Material(string key) => key switch
    {
        "wood" => "Wood", "stone" => "Stone", "copper" => "Copper", "leaf" => "Leaves", "metal" => "Metal", "glass" => "Glass", "cloth" => "Cloth", _ => "Material",
    };
}
