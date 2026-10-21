using Godot;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Ui;

/// <summary>
/// The words on screen for the things the mechanics round names (the pack's names block, docs/game/MECHANICS.md section 8): hearts,
/// stamina, pips, Fractals, expertise, camp and Trust, and the knacks. The kernel serves them (CommandHost.NameOf, KnackName: the
/// pack's words are untrusted text, cut to a short plain line through KernelText.DisplayText); the HUD's labels read them here
/// instead of writing the words, so a pack's renaming reaches every label. Ids and icons never change. A pack without the block
/// keeps today's words (the journal's page was Discoveries).
/// </summary>
public static class MeterWords
{
    public const int MaxLength = NamesRules.MaxLength;

    /// <summary>Today's word for each name, when the pack has no names block (or leaves one out).</summary>
    public static string Today(string key) => NamesRules.Today(key);

    /// <summary>The word for a name on this island: the pack's, sanitised, else today's.</summary>
    public static string Name(CommandHost? host, string key) =>
        host != null && GodotObject.IsInstanceValid(host) ? host.NameOf(key) : Today(key);

    /// <summary>The name in lower case, for the middle of a sentence ("your stamina bar").</summary>
    public static string Lower(CommandHost? host, string key) => Name(host, key).ToLowerInvariant();

    /// <summary>The name for one of them ("Pips" reads "Pip", "Fractals" reads "Fractal"), in lower case.</summary>
    public static string One(CommandHost? host, string key) => NamesRules.Singular(Name(host, key)).ToLowerInvariant();

    /// <summary>The name of a knack on this island: the pack's, sanitised, else the id in words.</summary>
    public static string Knack(CommandHost? host, string id) =>
        host != null && GodotObject.IsInstanceValid(host) ? host.KnackName(id) : NamesRules.TodayKnack(id);

    /// <summary>"1 pip", "2 pips": the count with the name, as one when the count is one.</summary>
    public static string Counted(CommandHost? host, string key, int count) => count == 1 ? One(host, key) : Lower(host, key);
}
