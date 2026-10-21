using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using Godot;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The small in-play settings panel (M7; docs/game/MECHANICS.md section 3, decision 1): C, its button and Esc; the four switches that
/// left the Customize panel and the Magic page ("Let my AI see", "Let the Gubble use fire", "Play the intro", the numbers); none of the
/// avatars that moved to the menu; "Change my AI", and "Reset Trust" beside it, which asks first and then sends the player-only
/// trust.reset through the host.
/// </summary>
public partial class PlayHudTest
{
    private static JsonObject AsGubble(CommandHost host, string op, JsonObject args) => host.HandleObject(new JsonObject
    {
        ["schema"] = "enfractal.command", ["version"] = 1, ["action_id"] = "settings-" + Guid.NewGuid().ToString("N")[..12], ["room_id"] = host.Room.RoomId, ["op"] = op, ["args"] = args,
    }.ToJsonString(), CommandHost.CompanionPrincipal);

    private async Task TestSettingsPanel()
    {
        var host = CommandHost.Of(_world)!;
        var player = _world.Player;
        Check(!_hud.SettingsOpen && player.InputEnabled, "the settings panel is shut at the start");
        _hud._UnhandledInput(Press(Key.C));
        await Frames(2);
        Check(_hud.SettingsOpen && !player.InputEnabled && _hud.SettingsPanel.IsVisibleInTree(), "C opens the settings, and the body waits while they are open");
        var panel = _hud.SettingsPanel;

        // What is in it: the four switches, "Your AI", and nothing of the avatars.
        var switches = panel.FindChildren("*", "CheckButton", true, false).OfType<CheckButton>().Select(b => b.Text).ToArray();
        Check(switches.SequenceEqual(new[] { "Let my AI see through the Gubble's eyes", "Let the Gubble use fire", "Play the intro", "Show numbers on the meters" }),
            "the switches are \"Let my AI see\", \"Let the Gubble use fire\", \"Play the intro\" and the numbers: " + string.Join(" | ", switches));
        var buttons = panel.FindChildren("*", "Button", true, false).OfType<Button>().Where(b => b is not CheckButton).Select(b => b.Text).ToArray();
        var labels = panel.FindChildren("*", "Label", true, false).OfType<Label>().Select(l => l.Text).ToArray();
        Check(buttons.SequenceEqual(new[] { "Change my AI", "Reset Trust", "Keep it", "Yes, reset Trust", "Back to the menu", "Done" }) && !labels.Contains("YOUR TWO AVATARS"),
            "and the buttons are Change my AI, Reset Trust (with its question's two answers), Back to the menu and Done: no avatar, colour or Save appearance (they went to the menu): " + string.Join(" | ", buttons));
        Check(_hud.FindChildren("GubbleFireSetting", "CheckButton", true, false).Count == 1 && _hud.FireToggle != null && panel.IsAncestorOf(_hud.FireToggle),
            "\"Let the Gubble use fire\" is here and only here: it left the Magic page");
        var aiRow = panel.GetNode<HBoxContainer>("SettingsColumn/AiButtons");
        Check(aiRow.GetChildren().Select(c => c.Name.ToString()).SequenceEqual(new[] { "ChangeMyAi", "ResetTrust" }), "\"Reset Trust\" sits beside \"Change my AI\"");

        // Change my AI: whether an AI is connected, and how to hand the Gubble to another.
        _hud.ChangeAiButton.EmitSignal(BaseButton.SignalName.Pressed);
        Check(_hud.AiNote.Visible && _hud.AiNote.Text.EndsWith(RoomHud.ChangeAiWords, StringComparison.Ordinal) &&
              (_hud.AiNote.Text.StartsWith("Gubble: waiting for your AI. ", StringComparison.Ordinal) || _hud.AiNote.Text.StartsWith("Gubble: no AI link in this window. ", StringComparison.Ordinal)),
            "\"Change my AI\" says whether an AI is connected (none is here) and how to hand the Gubble to another: " + _hud.AiNote.Text);

        // Reset Trust: the question first; "Keep it" sends nothing; "Yes" sends trust.reset as the player.
        host.PlayerMode("challenge");
        await Frames(2);
        var stay = AsGubble(host, "creation.place", new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["schema"] = "enfractal.creation", ["version"] = 1, ["name"] = "Settings post", ["seed"] = 1, ["mount"] = "ground",
                ["parts"] = new JsonArray(new JsonObject
                {
                    ["id"] = "base", ["shape"] = "box", ["position_m"] = new JsonArray(0, 0.06, 0), ["rotation_deg"] = new JsonArray(0, 0, 0), ["size_m"] = new JsonArray(0.12, 0.12, 0.12), ["material"] = "wood",
                }),
                ["nodes"] = new JsonArray(new JsonObject { ["id"] = "use", ["op"] = "interact", ["part_id"] = "base", ["params"] = new JsonObject() }),
                ["edges"] = new JsonArray(),
            },
            ["placement"] = new JsonObject { ["position_m"] = new JsonArray(1.5, 0, -1.2) },
        });
        var receipt = stay["action_id"]?.GetValue<string>() ?? "";
        var marked = host.PlayerMarkReceipt(receipt, yes: true);
        var before = host.TrustCount;
        Check(stay["ok"]?.GetValue<bool>() == true && marked["ok"]?.GetValue<bool>() == true && before > 0,
            $"(Trust earned in Challenge for the test: the Gubble's build, marked yes: {before})");
        _hud._UnhandledInput(Press(Key.C));
        _hud._UnhandledInput(Press(Key.C));
        await Frames(2);
        Check(_hud.TrustLine.Visible && _hud.TrustLine.Text == host.TrustWords && !_hud.TrustConfirm.Visible, "opened again, the panel says Trust in words (never a number): " + _hud.TrustLine.Text);
        _hud.ResetTrustButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(1);
        Check(_hud.TrustConfirm.Visible && _hud.KeepTrustButton.HasFocus() && host.TrustCount == before, "Reset Trust asks first, with \"Keep it\" holding the focus; nothing is sent yet");
        _hud.KeepTrustButton.EmitSignal(BaseButton.SignalName.Pressed);
        Check(!_hud.TrustConfirm.Visible && host.TrustCount == before, "\"Keep it\" puts the question away and Trust is as it was");
        _hud.ResetTrustButton.EmitSignal(BaseButton.SignalName.Pressed);
        _hud.ConfirmResetTrustButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(1);
        var lesson = JsonNode.Parse(host.Handle(new JsonObject
        {
            ["schema"] = "enfractal.query", ["version"] = 1, ["query_id"] = "settings-journal", ["room_id"] = host.Room.RoomId, ["op"] = "journal.read",
            ["args"] = new JsonObject { ["kind"] = "consequence", ["limit"] = 10 },
        }.ToJsonString(), CommandHost.CompanionPrincipal))!.AsObject();
        var lines = lesson["data"]?["entries"]?.AsArray().Select(e => e?["line"]?.GetValue<string>() ?? "").ToArray() ?? Array.Empty<string>();
        Check(host.TrustCount == 0 && !_hud.TrustConfirm.Visible && _hud.TrustLine.Text == host.TrustWords && lines.Any(l => l.StartsWith("You reset the Gubble's Trust", StringComparison.Ordinal)),
            $"\"Yes, reset Trust\" sends the player's trust.reset: Trust is back to base ({host.TrustCount}), the panel says so in words (\"{_hud.TrustLine.Text}\"), and the journal keeps it: {string.Join(" | ", lines)} {lesson["error"]?["message"]}");
        // The companion can never do it (the host's rule, shown here from the panel's own host).
        var refused = AsGubble(host, "trust.reset", new JsonObject());
        Check(refused["ok"]?.GetValue<bool>() == false && refused["error"]?["code"]?.GetValue<string>() == "permission_denied", "and the companion is refused trust.reset: only the player's button resets Trust");
        host.PlayerMode("creative");

        // Back to the menu starts the game again there (here: the seam answers, nothing is launched).
        var asked = 0;
        _hud.BackToMenuForTests = () => { asked++; return true; };
        _hud.BackToMenuButton.EmitSignal(BaseButton.SignalName.Pressed);
        _hud.BackToMenuForTests = () => false;
        _hud.BackToMenuButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(2);
        var said = (_hud.FindChild("Notice", true, false) as Label)?.Text ?? "";
        Check(asked == 1 && said.StartsWith("The menu could not be opened from here", StringComparison.Ordinal),
            "\"Back to the menu\" asks the game to start again at the menu, and says so when it could not: " + said);
        _hud.BackToMenuForTests = null;

        // Closing: the button, then the key, then Esc, each hands the body back.
        var done = panel.FindChildren("*", "Button", true, false).OfType<Button>().First(b => b.Text == "Done");
        done.EmitSignal(BaseButton.SignalName.Pressed);
        Check(!_hud.SettingsOpen && player.InputEnabled, "Done closes the panel and the body is free again");
        _hud._UnhandledInput(Press(Key.C));
        Check(_hud.SettingsOpen, "C opens it again");
        _hud._UnhandledInput(Press(Key.Escape));
        Check(!_hud.SettingsOpen && player.InputEnabled, "and Esc closes it");
    }
}
