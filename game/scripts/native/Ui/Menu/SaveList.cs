using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GarageGame.Native.Saves;

namespace GarageGame.Native.Ui;

/// <summary>One save the Esc menu's Load page offers: the island, the mode (and Challenge's difficulty), when it was last written, and whether it is the game being played.</summary>
public sealed record SaveListEntry(IslandEntry Island, string Mode, string? Difficulty, DateTime WrittenUtc, bool Playing)
{
    /// <summary>The game this save opens: the team's, on its island, in its mode and (Challenge) at the difficulty the save keeps.</summary>
    public MenuLaunch Launch(string team) => new(team, Island, Mode, Mode == "challenge" ? Difficulty ?? "standard" : null,
        MenuLaunch.ArgumentsFor(team, Mode, Mode == "challenge" ? Difficulty ?? "standard" : null));
}

/// <summary>
/// The Esc menu's Load page (decision 81) from what the menu already reads (M7: IslandCatalog, IslandSaveInfo, MenuLaunch): one save per
/// mode per island per team, written as the game is played, so loading is opening another of the team's saves (the game starts again
/// there, as "Back to the menu" does). Nothing is read but the saves' presence, their time and a Challenge save's difficulty; no save is
/// changed here.
/// </summary>
public static class SaveList
{
    /// <summary>The team's saves on this machine's islands, newest first, the one being played marked (it is not offered again).</summary>
    public static IReadOnlyList<SaveListEntry> For(TeamLayout layout, string team, IReadOnlyList<IslandEntry> islands, string? playingRoom, string? playingMode)
    {
        if (!TeamLayout.ValidTeamId(team)) return Array.Empty<SaveListEntry>();
        var found = new List<SaveListEntry>();
        foreach (var island in islands)
            foreach (var save in IslandSaveInfo.Of(layout, team, island))
                if (save.Exists)
                    found.Add(new SaveListEntry(island, save.Mode, save.Difficulty, save.WrittenUtc ?? DateTime.MinValue, island.RoomId == playingRoom && save.Mode == playingMode));
        return found.OrderByDescending(e => e.Playing).ThenByDescending(e => e.WrittenUtc).ThenBy(e => e.Island.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"The Garage · Challenge (Gentle)": the save's name on the page.</summary>
    public static string Title(SaveListEntry entry) =>
        $"{entry.Island.DisplayName} · {MenuModel.ModeWords(entry.Mode)}" + (entry.Mode == "challenge" ? $" ({MenuModel.DifficultyWords(entry.Difficulty ?? "standard")})" : "");

    /// <summary>"saved 5 minutes ago", "playing now".</summary>
    public static string When(SaveListEntry entry, DateTime nowUtc)
    {
        if (entry.Playing) return "playing now";
        var ago = nowUtc - entry.WrittenUtc;
        if (ago < TimeSpan.FromMinutes(1)) return "saved just now";
        if (ago < TimeSpan.FromHours(1)) return Counted((int)ago.TotalMinutes, "minute");
        if (ago < TimeSpan.FromDays(1)) return Counted((int)ago.TotalHours, "hour");
        if (ago < TimeSpan.FromDays(14)) return Counted((int)ago.TotalDays, "day");
        return "saved " + entry.WrittenUtc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }

    private static string Counted(int count, string unit) => $"saved {count} {unit}{(count == 1 ? "" : "s")} ago";
}
