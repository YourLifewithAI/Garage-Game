using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Ui;
using Godot;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The names block served to the HUD (M10 of docs/runs/RUN-2-MECHANICS.md; docs/game/MECHANICS.md section 8): the kernel serves
/// the pack's words (CommandHostTest.ModeRows checks that side), and the HUD's labels read them instead of writing hearts,
/// stamina, pips, Fractals and expertise. A pack that renames them reaches the meters' numbers, the numbers switch's hint, the
/// status line, the Island page, the journal's index, Character and Abilities pages; a pack with no names block shows today's words.
/// </summary>
public partial class PlayHudTest
{
    /// <summary>v5 with its names block replaced (the HUD suite's own copy of the pack's text, as bytes).</summary>
    private static IslandRules PackNamed(string hearts, string stamina, string pips, string fractals, string expertise, string camp, string trust)
    {
        var text = File.ReadAllText(ProjectSettings.GlobalizePath(IslandRules.PathFor("storybook_wild", 5)));
        const string was = "\"hearts\": \"Hearts\", \"stamina\": \"Stamina bar\", \"pips\": \"Pips\", \"fractals\": \"Fractals\", \"expertise\": \"Expertise\", \"camp\": \"Camp\", \"trust\": \"Trust\"";
        if (!text.Contains(was, StringComparison.Ordinal)) throw new InvalidOperationException("v5 no longer holds its names");
        return IslandRules.Parse(Encoding.UTF8.GetBytes(text.Replace(was,
            $"\"hearts\": \"{hearts}\", \"stamina\": \"{stamina}\", \"pips\": \"{pips}\", \"fractals\": \"{fractals}\", \"expertise\": \"{expertise}\", \"camp\": \"{camp}\", \"trust\": \"{trust}\"", StringComparison.Ordinal)));
    }

    /// <summary>v5 with no names block at all, as a pack written before the block had it.</summary>
    private static IslandRules PackWithoutNames()
    {
        var pack = JsonNode.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(IslandRules.PathFor("storybook_wild", 5))))!.AsObject();
        if (!pack.Remove("names")) throw new InvalidOperationException("v5 no longer holds its names");
        return IslandRules.Parse(Encoding.UTF8.GetBytes(pack.ToJsonString()));
    }

    private async Task TestRenamedWords()
    {
        var host = CommandHost.Of(_world)!;
        var original = host.Rules;
        try
        {
            host.UseRules(PackNamed("Lives", "Zest", "Sparks", "Stars", "Lore", "Hearth", "Bond"));
            FreshBodies(host);
            host.PlayerMode("challenge");
            _hud.SetMeterNumbers(true);
            await Frames(6);
            // The meters, the switch's hint and the status line.
            Check(_hud.HeartsNumbers.Text.StartsWith("Lives ", StringComparison.Ordinal) && _hud.BarNumbers.Text.StartsWith("Zest ", StringComparison.Ordinal) &&
                  _hud.PipsRow.Numbers.StartsWith("Sparks ", StringComparison.Ordinal),
                $"the meters' numbers carry the pack's words ({_hud.HeartsNumbers.Text}, {_hud.BarNumbers.Text}, {_hud.PipsRow.Numbers})");
            Check(_hud.NumbersToggle!.TooltipText == "Lives, the zest and the sparks are drawn, not written. This adds their numbers beside them.",
                "and so does the numbers switch's hint: " + _hud.NumbersToggle.TooltipText);
            var line = (Label)_hud.FindChild("IslandVitalsLine", true, false)!;
            await Frames(12);
            Check(line.Text.Contains("Zest 100%", StringComparison.Ordinal) && !line.Text.Contains("Stamina", StringComparison.Ordinal), "the status line says Zest where it said Stamina: " + line.Text);
            _hud.ToggleMagicPanel();
            await Frames(12);
            var help = _hud.FindChildren("*", "Label", true, false).OfType<Label>().FirstOrDefault(l => l.Text.StartsWith("Health ", StringComparison.Ordinal) && l.Text.Contains("Climbing capacity", StringComparison.Ordinal));
            Check(help != null && help.Text.Contains("· Zest 100%", StringComparison.Ordinal) && help.Text.Contains("Climbing uses zest.", StringComparison.Ordinal) && !help.Text.Contains("tamina", StringComparison.Ordinal),
                "the Island page's help reads the pack's word: " + help?.Text);
            _hud.ToggleMagicPanel();
            await Frames(2);

            // The journal: the index, the Character and Abilities pages.
            _hud.OpenJournal();
            await Frames(JournalMoveFrames);
            var panel = _hud.Journal;
            panel.OpenPage(JournalPanel.IndexPage);
            Check(panel.PageText.Contains("01   Lore", StringComparison.Ordinal) && panel.PageText.Contains("Your character sheet, and Stars to spend", StringComparison.Ordinal) && !panel.PageText.Contains("Fractals", StringComparison.Ordinal),
                "the journal's index names its first page Lore and says Stars to spend on the character sheet");
            panel.OpenPage(JournalPages.Character);
            Check(panel.PageText.Contains("Zest 100% · climbing capacity", StringComparison.Ordinal) && panel.PageText.Contains("Sparks ", StringComparison.Ordinal) && !panel.PageText.Contains("Stamina", StringComparison.Ordinal) && !panel.PageText.Contains("Pips", StringComparison.Ordinal),
                "the Character page reads Zest and Sparks");
            panel.OpenPage(JournalPages.Abilities);
            Check(panel.PageText.Contains("Costs 2 sparks", StringComparison.Ordinal) && !panel.PageText.Contains("Costs 2 pips", StringComparison.Ordinal),
                "the Abilities page prices Glow in sparks");
            host.PlayerMode("creative");
            await Frames(6);
            panel.OpenPage(JournalPages.Character);
            Check(panel.PageText.Contains("climbing uses no zest", StringComparison.Ordinal), "and Creative's page says climbing uses no zest");
            _hud.CloseJournal();
            await Frames(JournalMoveFrames);

            // A pack with no names block shows today's words, as the HUD wrote them.
            host.UseRules(PackWithoutNames());
            FreshBodies(host);
            host.PlayerMode("challenge");
            await Frames(6);
            Check(_hud.HeartsNumbers.Text.StartsWith("Hearts ", StringComparison.Ordinal) && _hud.BarNumbers.Text.StartsWith("Stamina ", StringComparison.Ordinal) && !_hud.BarNumbers.Text.StartsWith("Stamina bar", StringComparison.Ordinal),
                $"a pack with no names block shows today's words ({_hud.HeartsNumbers.Text}, {_hud.BarNumbers.Text})");
            Check(_hud.NumbersToggle.TooltipText == "Hearts, the stamina bar and the pips are drawn, not written. This adds their numbers beside them.",
                "and the numbers switch's hint is the one the HUD always wrote: " + _hud.NumbersToggle.TooltipText);
            _hud.OpenJournal();
            await Frames(JournalMoveFrames);
            _hud.Journal.OpenPage(JournalPanel.IndexPage);
            Check(_hud.Journal.PageText.Contains("01   Discoveries", StringComparison.Ordinal) && _hud.Journal.PageText.Contains("Your character sheet, and Fractals to spend", StringComparison.Ordinal),
                "the journal is as it was: the Discoveries page, Fractals to spend");
            _hud.CloseJournal();
            await Frames(JournalMoveFrames);
        }
        finally
        {
            _hud.SetMeterNumbers(false);
            host.UseRules(original);
            FreshBodies(host);
            host.PlayerMode("creative");
            await Frames(6);
        }
    }
}
