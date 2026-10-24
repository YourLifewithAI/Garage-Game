using System;
using System.Globalization;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Ui;

/// <summary>
/// The island's words for the things the mechanics round names, for the menu, which runs before any room and so before any command
/// host (MeterWords reads them from the host): the pack's names block, cleaned as the game cleans display text, else today's words.
/// </summary>
public static class MenuWords
{
    public static string Name(IslandRules? rules, string key) => rules?.Names?.Word(key) ?? NamesRules.Today(key);

    /// <summary>"1 expertise", "4 expertise": the count with the name, as one thing when the count is one.</summary>
    public static string Counted(IslandRules? rules, string key, int count)
    {
        var name = Name(rules, key);
        return string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? NamesRules.Singular(name) : name).ToLowerInvariant()}");
    }
}

/// <summary>What a knack does, in a line, from the pack's table (docs/game/MECHANICS.md section 7): the number it moves, and by how much at the first rank and the last.</summary>
public static class KnackWords
{
    public static string Moves(string moves) => moves switch
    {
        "walk_run_speed" => "walk and run speed",
        "hop_height" => "hop height",
        "stamina_per_bar" => "stamina in each bar",
        "climb_per_stamina" => "climbing for the same stamina",
        "swim" => "swim speed",
        "breath" => "stamina spent under water",
        "health_per_quarter" => "health in each quarter heart",
        "safe_drop" => "the drop you can land safely",
        "build_time" => "building and repair time",
        "plant_growth" => "plant growth",
        "gather_speed" => "gather and chop speed",
        _ => moves.Replace('_', ' '),
    };

    public static string Describe(KnackRule knack)
    {
        static string Amount(KnackRule k, double value) => k.Unit == "m"
            ? string.Create(CultureInfo.InvariantCulture, $"{value:0.00} m")
            : string.Create(CultureInfo.InvariantCulture, $"{(value >= 1 ? "+" : "-")}{Math.Abs(value - 1) * 100:0.#}%");
        if (knack.Ranks.Count == 0) return Moves(knack.Moves);
        var first = Amount(knack, knack.Ranks[0]);
        var last = Amount(knack, knack.Ranks[^1]);
        return $"{Moves(knack.Moves)} {first}" + (knack.Ranks.Count > 1 && last != first ? $" (up to {last})" : "");
    }
}
