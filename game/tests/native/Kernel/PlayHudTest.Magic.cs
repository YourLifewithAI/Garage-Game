using System;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;
using Godot;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The Magic panel: the mode picker, Grow's button, the player's "Let the Gubble use fire", Fire's preview and
/// deliberate hold (no cast without a host preview, a release cancels, the hold casts exactly what was previewed), the
/// nature Undo button (Creative only), and the wish queue's curated words and Stop, all through the host's commands.
/// </summary>
public partial class PlayHudTest
{
    private async Task TestMagicPanel()
    {
        var host = CommandHost.Of(_world)!;
        var open = (Button)_hud.FindChild("MagicPanelButton", true, false)!;
        open.EmitSignal(Button.SignalName.Pressed);
        await Frames(1);
        Check(_hud.MagicPanelOpen && !_world.Player.InputEnabled && !GetTree().Paused,
            "the Magic panel holds local movement while the island and connected AI continue");
        // The in-play mode dropdown is gone (the mechanics round, decision 21: switching modes means switching saves, chosen at the
        // menu): the page names this save's mode and nothing on it changes it. The host still takes the command until the teams packet.
        Check(_hud.FindChild("ModeChoice", true, false) == null && _hud.FindChildren("*", "OptionButton", true, false).OfType<OptionButton>().All(b => b.Name != "ModeChoice"),
            "the Island page has no mode dropdown any more");
        var modeName = (Label)_hud.FindChild("ModeName", true, false)!;
        await Frames(12);
        Check(modeName.Text == "Creative", "the page names the save's mode instead: " + modeName.Text);
        host.PlayerMode("challenge");
        await Frames(12);
        var line = (Label)_hud.FindChild("IslandVitalsLine", true, false)!;
        Check(host.Mode == "challenge" && modeName.Text == "Challenge · Standard" && line.Text.StartsWith("Challenge · ", StringComparison.Ordinal) && !line.Text.Contains("Health", StringComparison.Ordinal),
            "in Challenge the page names the mode and its difficulty, and the status line carries no numbers until the setting asks for them: " + line.Text);
        _hud.SetMeterNumbers(true);
        await Frames(12);
        Check(line.Text.Contains("Stamina bar 100%", StringComparison.Ordinal) && !line.Text.Contains("0/0", StringComparison.Ordinal),
            "with the numbers option on, the small starting stamina reads as a full percentage: " + line.Text);
        _hud.SetMeterNumbers(false);
        var undoButton = (Button)_hud.FindChild("NatureUndoButton", true, false)!;
        await Frames(12);
        Check(!undoButton.Visible, "in Challenge there is no undo control (undo is Creative's alone)");
        host.PlayerMode("creative");
        await Frames(12);
        Check(host.Mode == "creative" && undoButton.Visible && modeName.Text == "Creative", "back in Creative through the host, its undo control and the page's name for the mode return");
        var fireSetting = (CheckButton)_hud.FindChild("GubbleFireSetting", true, false)!;
        Check(fireSetting.ButtonPressed && host.GubbleFireAllowed && fireSetting.Text == "Let the Gubble use fire", "the settings panel has \"Let the Gubble use fire\", on by default");
        fireSetting.EmitSignal(BaseButton.SignalName.Toggled, false);
        Check(!host.GubbleFireAllowed && !fireSetting.ButtonPressed && !CommandHost.LoadGubbleFire(GarageGame.Native.RoomHud.ProfilePath),
            "switching it off is obeyed by the host and saved in the player's profile");
        fireSetting.EmitSignal(BaseButton.SignalName.Toggled, true);
        Check(host.GubbleFireAllowed && CommandHost.LoadGubbleFire(GarageGame.Native.RoomHud.ProfilePath), "and so is switching it back on");
        // Decision 52: Bloom became Grow and its flower trail went: the panel has Grow's button and no trail switch.
        var growButton = _hud.FindChildren("*", "Button", true, false).OfType<Button>().SingleOrDefault(button => button.Text == "Grow the plants at the selected spot");
        Check(host.NatureConfigured && growButton != null && _hud.FindChild("FlowerTrailToggle", true, false) == null, "the Magic panel has Grow's button and no flower trail switch");
        var effects = host.ActiveEffectIds.Count;
        var natureReceipts = host.Authority.Call("receipt_count").AsInt32();
        _hud.BeginFireConfirmation();
        for (var i = 0; i < 30; i++) _hud.TickFireConfirmation(.1);
        Check(!host.Nature.Cells.Values.Any(cell => cell.BurningS > 0) && host.Authority.Call("receipt_count").AsInt32() == natureReceipts && host.ActiveEffectIds.Count == effects,
            "holding Fire without a successful preview cannot start a fire or manufacture a cast receipt");
        var previousSample = host.NatureSampleForTests;
        // The fire's cast is counted in the expertise store (Fire's shared rules): kept in memory here, so the suite's profile never
        // keeps the test's Fire (it would let rain put the starting campfire out before the camp's own test).
        host.ConfigureGlowProgress(new HudGlowStore(host.GlowProgress));
        // Decision 72: the team knows Fire, as after the first rest (the one helper every suite that lights fires uses).
        host.LearnFireForTests();
        var point = FloorNearGubble(.3f);
        host.NatureSampleForTests = (x, z) => new NatureCell { X = x, Z = z, Y = point.Y, Material = "soil", Fertile = true, Fuel = 1, Moisture = .2f, Light = .6f };
        try
        {
            _hud.AimRayForTests = Down(point);
            _hud.FindChildren("*", "Button", true, false).OfType<Button>().Single(button => button.Text == "Choose spot").EmitSignal(Button.SignalName.Pressed);
            Check(_hud.ChoosingMagicSpot && !_hud.MagicPanelOpen, "choosing a magic spot returns to the island for one world click");
            _hud._UnhandledInput(LeftButton(true));
            Check(_hud.MagicPanelOpen && !_hud.ChoosingMagicSpot, "the world click freezes a target and reopens Magic without casting");
            _hud.FindChildren("*", "Button", true, false).OfType<Button>().Single(button => button.Text == "Preview Fire at this spot").EmitSignal(Button.SignalName.Pressed);
            var confirm = (Button)_hud.FindChild("ConfirmFireButton", true, false)!;
            Check(_hud.FirePreviewReady && !confirm.Disabled && host.Nature.Fire == null,
                "Fire preview prepares an exact host token and leaves the island unburnt");
            confirm.EmitSignal(BaseButton.SignalName.ButtonDown);
            for (var i = 0; i < 5; i++) _hud.TickFireConfirmation(.1);
            confirm.EmitSignal(BaseButton.SignalName.ButtonUp);
            Check(host.Nature.Fire == null && _hud.FireConfirmationProgress == 0,
                "releasing Fire before its hold completes cancels the hold without casting");
            confirm.EmitSignal(BaseButton.SignalName.ButtonDown);
            for (var i = 0; i < 14; i++) _hud.TickFireConfirmation(.1);
            Check(host.Nature.Fire != null && !_hud.FirePreviewReady,
                "a completed deliberate hold sends the exact previewed Fire through the host");
            ((Button)_hud.FindChild("NatureUndoButton", true, false)!).EmitSignal(Button.SignalName.Pressed);
            Check(host.Nature.Fire == null && !host.Nature.Cells.Values.Any(cell => cell.BurningS > 0),
                "the nature Undo button removes the fire through the host");
        }
        finally
        {
            _hud.AimRayForTests = null;
            host.NatureSampleForTests = previousSample;
            host.ConfigureGlowProgress(new LocalGlowProgressStore(ProjectSettings.GlobalizePath(HudGlowProfile)));
            host.LearnFireForTests();
        }
        var tabs = (TabContainer)_hud.FindChild("MagicTabs", true, false)!;
        tabs.CurrentTab = 2;
        var wishes = (LineEdit)_hud.FindChild("WishText", true, false)!;
        var submit = (Button)_hud.FindChild("WishSubmitButton", true, false)!;
        var stop = (Button)_hud.FindChild("WishStopButton", true, false)!;
        var disclosure = _hud.FindChild("WishDisclosure", true, false) as Label;
        Check(disclosure is { Visible: true } && disclosure.Text == GarageGame.Native.RoomHud.WishDisclosure && disclosure.Text.Contains("sent to the AI you connected", StringComparison.Ordinal) &&
              disclosure.Text.Contains("company", StringComparison.Ordinal),
            "the wish page always says the wish's words go to the connected AI and its company");
        wishes.Text = "Build a small bridge.";
        submit.EmitSignal(Button.SignalName.Pressed);
        wishes.Text = "Add a flower beside it.";
        submit.EmitSignal(Button.SignalName.Pressed);
        await Frames(12);
        Check(host.Wishes.Items.Count == 2 && host.Wishes.Items.All(wish => wish.Status == "queued") &&
              host.Wishes.Items[0].Text == "Build a small bridge." && host.Wishes.Items[1].Text == "Add a flower beside it.",
            "wishes entered in the panel queue in their submitted order without launching a runner");
        var queue = (Label)_hud.FindChild("WishQueueWords", true, false)!;
        Check(queue.Text.Contains("Wish 1 · Waiting its turn", StringComparison.Ordinal) &&
              !queue.Text.Contains("Build a small bridge", StringComparison.Ordinal) && !host.WishRunnerConnected,
            "wish feedback uses curated statuses while a disconnected runner leaves the wishes waiting");
        stop.EmitSignal(Button.SignalName.Pressed);
        Check(host.Wishes.Items.All(wish => wish.Status == "cancelled"), "the panel's Stop button cancels the waiting wishes through the host");
        _hud._UnhandledInput(Press(Key.Escape));
        Check(!_hud.MagicPanelOpen && _world.Player.InputEnabled, "Escape closes Magic and returns player control");
    }
}
