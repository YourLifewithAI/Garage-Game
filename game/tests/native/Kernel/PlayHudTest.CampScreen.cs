using Godot;
using System;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Ui;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The camp's screen (the second playtest round's P2, item 3; decision 87; RoomHud.CampScreen.cs): sitting opens it on the left under the
/// meters; its five pages gather what exists (the abilities and the Magic panel, the expertise found, you, the Fractals' spends, the
/// Inventory: PlayHudTest.Inventory.cs), every number the host's and every button the player's command; the Magic panel opens over it and gives it
/// back; standing up closes it (E, its button, a step away, the first Esc); it never takes the keyboard (the body still walks).
/// </summary>
public partial class PlayHudTest
{
    private async Task TestCampScreen()
    {
        var host = CommandHost.Of(_world)!;
        var player = _world.Player;
        player.ReadKeyboard = false;
        if (_hud.Sitting) _hud.RestKey();
        FreshBodies(host);
        var mode = host.Mode;
        host.PlayerMode("challenge");
        var spawn = host.Room.SpawnFor("player").PositionM;
        Check(player.TryTeleportTo(spawn) && host.IsCampfireLit(CampLaws.StartingId), "(the player stands at the lit starting campfire)");
        await Frames(30);
        Check(!_hud.CampOpen && !_hud.CampScreen.Visible, "standing, the camp is closed");
        _hud._UnhandledInput(Press(Key.E));
        await Frames(3);
        var screen = _hud.CampScreen;
        var rect = screen.GetGlobalRect();
        var view = _hud.GetViewport().GetVisibleRect().Size;
        Check(_hud.Sitting && _hud.CampOpen && screen.Visible, "sitting down by the fire opens the camp");
        Check(rect.Position == new Vector2(18, 150) && rect.Size.X >= 320 && rect.End.X <= view.X && rect.End.Y <= view.Y && !rect.Intersects(_hud.MeterPanel.GetGlobalRect()) &&
              rect.End.X < view.X * 0.5f,
            $"on the left, where the Magic panel opens, under the meters and clear of them, the fire's view beside it: {rect.Position.X:0},{rect.Position.Y:0} {rect.Size.X:0}x{rect.Size.Y:0} of {view.X:0}x{view.Y:0}");
        var titles = Enumerable.Range(0, _hud.CampTabs.GetTabCount()).Select(i => _hud.CampTabs.GetTabTitle(i)).ToArray();
        Check(titles.SequenceEqual(new[] { "Magic", MeterWords.Name(host, "expertise"), "You", MeterWords.Name(host, "fractals"), "Inventory" }),
            "its pages: Magic, the expertise found, You, the Fractals and the Inventory: " + string.Join(", ", titles));
        Check(screen.FindChildren("*", "BaseButton", true, false).OfType<BaseButton>().All(b => b.FocusMode == Control.FocusModeEnum.None) && _hud.CampTabs.TabFocusMode == Control.FocusModeEnum.None,
            "nothing on it takes the keyboard (the body still walks: Space jumps and never presses a button)");

        string Page(int index) { _hud.CampTabs.CurrentTab = index; return _hud.CampPageText; }
        await Frames(1);
        var magic = Page(0);
        Check(magic.StartsWith("Open the Magic panel · M", StringComparison.Ordinal) && JournalPages.AbilityLines(host).All(line => magic.Contains(line.Text, StringComparison.Ordinal)),
            "Magic: the island's abilities as the journal lists them, and the Magic panel itself");
        var expertise = Page(1);
        Check(expertise.StartsWith("Magic we found", StringComparison.Ordinal) && JournalPages.GlowLines(host).Concat(JournalPages.OtherLines(host)).All(line => expertise.Contains(line.Text, StringComparison.Ordinal)),
            "the expertise found: the journal's own list");
        var you = Page(2);
        var rules = host.BodyRules;
        var vitals = host.Vitals;
        Check(you.Contains(MeterMath.HeartsNumbers(MeterWords.Name(host, "hearts"), rules.Quarters(vitals), rules.MaxQuarters(vitals)), StringComparison.Ordinal) &&
              you.Contains(MeterMath.BarNumbers(MeterWords.Name(host, "stamina"), vitals.Stamina, vitals.MaxStamina), StringComparison.Ordinal) && you.Contains("Knacks", StringComparison.Ordinal),
            "You: the hearts and the bar in the meters' own words, and the knacks: " + you.Replace("\n", " | "));
        var inventory = Page(4);
        Check(!inventory.Contains("coming", StringComparison.Ordinal) && inventory.Contains("Carrying: empty hands", StringComparison.Ordinal) && inventory.Contains("In your wallet", StringComparison.Ordinal) &&
              inventory.Contains("The chest", StringComparison.Ordinal) && inventory.Contains("Your pockets", StringComparison.Ordinal),
            "Inventory: live now (it no longer says it is coming): what the hands and the wallet hold, the chest and the pockets");

        // The Fractals: the camp's two spends, as the journal's Character page has them, through the player's fractal.spend.
        host.ConfigurePlayerVitals(BodyHolding(5));
        var hearts = host.BodyRules.MaxHearts(host.Vitals);
        var fractals = Page(3);
        await Frames(1);
        var heartButton = screen.FindChild("spend_heart", true, false) as Button;
        Check(fractals == string.Join("\n", FractalSpend.Lines(host).Select(l => l.Text)) && heartButton is { Disabled: false, Visible: true },
            "the Fractals: what is in hand and the two spends, the heart's button lit with five in hand");
        heartButton!.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(1);
        Check(host.BodyRules.MaxHearts(host.Vitals) == hearts + 1 && _hud.CampFoot.StartsWith("A new heart!", StringComparison.Ordinal),
            "pressed, it spends five through the host: a new heart, said at the camp's foot: " + _hud.CampFoot);
        FreshBodies(host);

        // The Magic panel opens over the camp and gives it back; Esc closes the panel first, then stands you up, then opens the menu.
        _hud.CampButton(CampPages.MagicTarget);
        await Frames(2);
        Check(_hud.MagicPanelOpen && !screen.Visible && _hud.CampOpen && _hud.Sitting, "the Magic page's button opens the Magic panel over the camp, still sitting");
        _hud._UnhandledInput(Press(Key.Escape));
        await Frames(2);
        Check(!_hud.MagicPanelOpen && screen.Visible && _hud.Sitting && !_hud.PauseMenuOpen, "the first Esc closes the panel and the camp is back");
        _hud._UnhandledInput(Press(Key.Escape));
        await Frames(2);
        Check(!_hud.Sitting && !_hud.CampOpen && !screen.Visible && !_hud.PauseMenuOpen && !host.PlayerResting, "the next Esc stands you up and closes the camp");
        _hud._UnhandledInput(Press(Key.Escape));
        Check(_hud.PauseMenuOpen, "and the one after opens the menu");
        _hud.ClosePauseMenu();
        // The Stand up button, and a step away, close it too.
        _hud._UnhandledInput(Press(Key.E));
        await Frames(2);
        var sat = _hud.CampOpen;
        ((Button)screen.FindChild("CampStandUp", true, false)!).EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(2);
        Check(sat && !_hud.Sitting && !_hud.CampOpen, "its Stand up button stands you up and closes it");
        _hud._UnhandledInput(Press(Key.E));
        await Frames(2);
        sat = _hud.CampOpen;
        Check(player.TryTeleportTo(spawn + new Vector3(0.12f, 0, 0)), "(the player steps away)");
        await Frames(3);
        Check(sat && !_hud.Sitting && !_hud.CampOpen && !screen.Visible, "a step away stands you up and the camp closes");
        await TestPromptCost(host, spawn);
        _hud.CampTabs.CurrentTab = 0;
        host.PlayerMode(mode);
    }

    /// <summary>
    /// What this round adds to a frame (PromptCost: the notice, E's prompt and its question to the host, the camp's screen), against the HUD
    /// packet's budget of 0.5 ms: walking about the camp with notices coming, then sitting with the camp open on its busiest page.
    /// </summary>
    private async Task TestPromptCost(CommandHost host, Vector3 spawn)
    {
        var player = _world.Player;
        await Frames(10);
        PromptCost.Reset();
        PromptCost.Measuring = true;
        for (var i = 0; i < 600; i++)
        {
            if (i % 60 == 0)
            {
                player.TryTeleportTo(spawn + new Vector3(0.1f * (i / 60 % 5) - 0.2f, 0, 0));
                player.ResetPhysicsInterpolation();
                _hud._UnhandledInput(Press(Key.P, shift: true));
            }
            await Frames(1);
        }
        var walking = (PromptCost.MeanMs, PromptCost.WorstMs, PromptCost.Frames, PromptCost.SamplesMs.OrderBy(m => m).ElementAt((int)(PromptCost.SamplesMs.Count * 0.99)));
        GD.Print("PLAY_HUD_COST: the round's prompt, notice and camp walking about the camp, ms a frame: " +
                 string.Join(", ", Enum.GetValues<PromptCost.Piece>().Select(p => $"{p} {PromptCost.PieceMeanMs(p):0.0000}")) + $"; mean {walking.Item1:0.0000}, 99% under {walking.Item4:0.000}, worst {walking.Item2:0.000} over {walking.Item3} frames");
        player.TryTeleportTo(spawn);
        await Frames(4);
        _hud._UnhandledInput(Press(Key.E));
        _hud.CampTabs.CurrentTab = 2;
        await Frames(4);
        var sitting = _hud.CampOpen;
        PromptCost.Reset();
        for (var i = 0; i < 300; i++) await Frames(1);
        var resting = (PromptCost.MeanMs, PromptCost.WorstMs, PromptCost.Frames, PromptCost.SamplesMs.OrderBy(m => m).ElementAt((int)(PromptCost.SamplesMs.Count * 0.99)));
        PromptCost.Measuring = false;
        GD.Print("PLAY_HUD_COST: sitting with the camp open on You, ms a frame: " +
                 string.Join(", ", Enum.GetValues<PromptCost.Piece>().Select(p => $"{p} {PromptCost.PieceMeanMs(p):0.0000}")) + $"; mean {resting.Item1:0.0000}, 99% under {resting.Item4:0.000}, worst {resting.Item2:0.000} over {resting.Item3} frames, {PromptCost.AllocatedBytes / Math.Max(1, resting.Item3)} bytes a frame");
        Check(walking.Item3 >= 550 && walking.Item1 < 0.5 && walking.Item4 < 0.5 && sitting && resting.Item3 >= 280 && resting.Item1 < 0.5 && resting.Item4 < 0.5,
            $"the round's additions cost {walking.Item1:0.000} ms a frame walking about the camp and {resting.Item1:0.000} ms sitting with the camp open (99 in 100 under {walking.Item4:0.000} and {resting.Item4:0.000} ms), against the HUD's budget of 0.5 ms");
        if (_hud.Sitting) _hud.RestKey();
    }
}
