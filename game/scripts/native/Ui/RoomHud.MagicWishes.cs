using System.Linq;
using Godot;

namespace GarageGame.Native;

public partial class RoomHud
{
    private LineEdit _wishText = null!;
    private Label _wishLimit = null!;
    private Label _wishRunner = null!;
    private Label _wishQueueWords = null!;
    private Button _wishSubmit = null!;
    private Button _wishStop = null!;
    private CheckButton _wishUseTarget = null!;
    /// <summary>Wish words leave the game: they go to the AI the player connected, and so to the company that runs it.</summary>
    public const string WishDisclosure = "The words of your wish are sent to the AI you connected to play the Gubble. If that AI is online, the company that runs it gets them too.";

    private void BuildWishPage(VBoxContainer page)
    {
        MagicHeading(page, "MAKE A WISH");
        page.AddChild(WrapMagicLabel("Tell the Gubble what you want to try. Wishes wait their turn, in the order you make them."));
        // The magic design's decided disclosure: always shown, whichever AI receives the wish.
        var disclosure = WrapMagicLabel(WishDisclosure); disclosure.Name = "WishDisclosure"; page.AddChild(disclosure);
        _wishRunner = WrapMagicLabel(""); _wishRunner.Name = "WishRunnerStatus"; page.AddChild(_wishRunner);
        _wishText = new LineEdit { Name = "WishText", PlaceholderText = "I wish…", MaxLength = 280, ExpandToTextLength = false };
        _wishText.TextChanged += _ => RefreshWishPage();
        _wishText.TextSubmitted += _ => SubmitWish();
        page.AddChild(_wishText);
        _wishLimit = new Label { Text = "0 / 280", HorizontalAlignment = HorizontalAlignment.Right }; page.AddChild(_wishLimit);
        _wishUseTarget = new CheckButton { Name = "WishUseTarget", Text = "About the selected thing" }; page.AddChild(_wishUseTarget);
        _wishSubmit = new Button { Name = "WishSubmitButton", Text = "Make this wish" };
        _wishSubmit.Pressed += SubmitWish; page.AddChild(_wishSubmit);
        _wishStop = new Button { Name = "WishStopButton", Text = "Stop this wish and clear the waiting wishes" };
        _wishStop.Pressed += () => { if (Host is { } host) MagicResult(host.PlayerWishStop(), "The Gubble has stopped. Waiting wishes are cancelled."); };
        page.AddChild(_wishStop);
        MagicHeading(page, "WISH JOURNAL");
        _wishQueueWords = WrapMagicLabel("Your wishes will appear here."); _wishQueueWords.Name = "WishQueueWords"; page.AddChild(_wishQueueWords);
    }

    private void SubmitWish()
    {
        if (Host is not { } host || string.IsNullOrWhiteSpace(_wishText.Text)) return;
        var result = host.PlayerWish(_wishText.Text, _wishUseTarget.ButtonPressed ? _magicAim.Entity : null);
        if (result["ok"]?.GetValue<bool>() == true) _wishText.Text = "";
        MagicResult(result, host.WishRunnerConnected ? "Your wish is queued." : "Your wish is queued and will wait for your wish runner.");
    }

    private void RefreshWishPage()
    {
        if (_wishText == null || _wishSubmit == null || Host is not { } host) return;
        _wishLimit.Text = $"{_wishText.Text.Length} / 280";
        _wishUseTarget.Disabled = _magicAim.Entity == null;
        if (_magicAim.Entity == null) _wishUseTarget.SetPressedNoSignal(false);
        _wishSubmit.Disabled = string.IsNullOrWhiteSpace(_wishText.Text) || host.Wishes.Items.Count(item => !item.Terminal) >= 16;
        _wishStop.Disabled = !host.Wishes.Items.Any(item => !item.Terminal);
        _wishRunner.Text = host.WishRunnerConnected ? "Your wish runner is connected." : host.Wishes.Items.Count == 0 ?
            "Before your first wish: start and connect your own AI wish runner when you are ready. The game asks you to start it; it never opens another program by itself." :
            "Your wish runner is not connected. Queued wishes will wait until you start it.";
        if (host.WishNotice.Length > 0) _wishRunner.Text += "\n" + host.WishNotice;
        // Only the player's input box contains their words. Gubble feedback comes from these fixed lines,
        // never provider prose, a subprocess's output, an untrusted note or a runner's claimed success.
        _wishQueueWords.Text = host.Wishes.Items.Count == 0 ? "Your wishes will appear here." : string.Join("\n", host.Wishes.Items.Select((item, index) =>
            $"Wish {index + 1} · " + (item.Status switch
            {
                "queued" => "Waiting its turn", "running" => "Working on it", "granted" => "Granted",
                "partial" => "Partly granted", "unsupported" => "That magic is not available yet",
                "cancelled" => "Stopped", _ => "Could not finish this wish",
            })).TakeLast(16));
    }
}
