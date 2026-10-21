using Godot;

namespace GarageGame.Native;

/// <summary>
/// The Gubble's answer to an order or a cast (the magic design's "What the player sees"): at once, a thought bubble over its name
/// tag shows the order's or ability's icon; "done" (a tick and a happy wiggle) only when the host's result says so; a refusal is a
/// head-shake with the host's reason, short, beside the bubble; a slot the island has no ability for is a shrug and "not yet".
/// The words are screen text on the HUD (the Glow playtest, 9 October: the 3D words were "almost impossible to read"), placed
/// over the Gubble every frame, at least as large as the HUD's own text and outlined dark so they read over any landscape. The
/// gestures and the dim are the Gubble's own (CompanionAvatar); this only asks for them. The CompanionStatusCue under the name
/// tag is the AI link's own state, which this does not replace. Looks are a first pass for the founder's eye.
/// </summary>
public partial class GubbleCue : Control
{
    /// <summary>The Gubble's name tag under its body, found every frame by a path kept for the game (S12).</summary>
    private static readonly NodePath TagPath = "CompanionLabel";
    /// <summary>How long a finished answer (done, refused, a shrug) stays before the bubble clears.</summary>
    public const double ShowS = 2.5;
    /// <summary>The bubble's and the reason's text sizes, in pixels: the HUD's own text is 18 (14 in its compact panels).</summary>
    public const int BubbleFontPx = 28;
    public const int ReasonFontPx = 22;
    /// <summary>The dark outline round the words, in pixels.</summary>
    public const int OutlinePx = 8;
    /// <summary>The gap between the name tag's top and the bubble, and between the bubble and the reason, in pixels.</summary>
    public const float GapPx = 4f;

    public CompanionAvatar Companion { get; set; } = null!;
    /// <summary>The bubble's icon now ("" when it shows nothing).</summary>
    public string Icon { get; private set; } = "";
    /// <summary>What the bubble's state is: "", thinking, done, refused, shrug.</summary>
    public string State { get; private set; } = "";
    /// <summary>The short text beside the bubble: the host's reason after a refusal, "not yet" after a shrug, else "".</summary>
    public string Reason { get; private set; } = "";
    /// <summary>
    /// What the Gubble holds (its display name, made safe by the host), or "": while nothing else shows, the bubble says it
    /// (the founder's playtest: the Gubble should say what it carries). The HUD sets it every frame from the host.
    /// </summary>
    public string Holding
    {
        get => _holding;
        set
        {
            if (_holding == (value ?? "")) return;
            _holding = value ?? "";
            Redraw();
        }
    }
    private string _holding = "";
    /// <summary>The Gubble's gestures and dim (CompanionAvatar's own), as the cue last asked.</summary>
    public CompanionAvatar.Gesture LastGesture => Companion?.LastGesture ?? CompanionAvatar.Gesture.None;
    public bool Dimmed => Companion?.Dimmed ?? false;
    /// <summary>The bubble's and the reason's screen labels.</summary>
    public Label Bubble { get; private set; } = null!;
    public Label ReasonLabel { get; private set; } = null!;
    /// <summary>The bubble's words changed (the playtest record's notice): its text and the reason beside it, "" when cleared.</summary>
    public event System.Action<string, string>? Shown;
    /// <summary>A refusal was shown: the reason as shown, and the host's code and the command's action id when the caller gave them ("" if not).</summary>
    public event System.Action<string, string, string>? Refused;

    private double _showFor;
    private string _shownText = "", _shownReason = "";

    public override void _Ready()
    {
        Name = "GubbleCue";
        MouseFilter = MouseFilterEnum.Ignore;
        Bubble = MakeLabel("GubbleThought", BubbleFontPx);
        ReasonLabel = MakeLabel("GubbleReason", ReasonFontPx);
        AddChild(Bubble);
        AddChild(ReasonLabel);
        Redraw();
    }

    /// <summary>The Gubble's words as screen text: light ink outlined dark, so they read over any landscape (also over its view, RoomHud.GubbleView.cs).</summary>
    public static Label MakeLabel(string name, int size)
    {
        var label = new Label { Name = name, Visible = false, MouseFilter = MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", new Color("fff3d6"));
        label.AddThemeColorOverride("font_outline_color", new Color("10241f"));
        label.AddThemeConstantOverride("outline_size", OutlinePx);
        return label;
    }

    /// <summary>The order or cast was asked: the icon shows at once, while the host decides.</summary>
    public void Think(string icon)
    {
        Icon = icon;
        State = "thinking";
        Reason = "";
        _showFor = double.MaxValue;
        Redraw();
    }

    /// <summary>The host's result says it happened: a tick, and a happy wiggle unless the Gubble gestures otherwise (a cast).</summary>
    public void Done(bool wiggle = true)
    {
        State = "done";
        Reason = "";
        _showFor = ShowS;
        if (wiggle) Companion?.Wiggle();
        Redraw();
    }

    /// <summary>The host refused: a head-shake and its reason, short (GubbleMagic.ShortReason). The code and action id are the record's.</summary>
    public void Refuse(string? hostReason, string? code = null, string? actionId = null)
    {
        State = "refused";
        Reason = GubbleMagic.ShortReason(hostReason);
        _showFor = ShowS;
        Companion?.Shake();
        Refused?.Invoke(Reason, code ?? "", actionId ?? "");
        Redraw();
    }

    /// <summary>A slot the island has no ability for: a shrug and "not yet" (no command was sent).</summary>
    public void Shrug(string icon, string words = GubbleMagic.NotYet)
    {
        Icon = icon;
        State = "shrug";
        Reason = words;
        _showFor = ShowS;
        Companion?.Shrug();
        Redraw();
    }

    /// <summary>The dusk moment: it shivers (and the HUD dims it).</summary>
    public void Shiver() => Companion?.Shiver();

    /// <summary>Dim or undim the Gubble's body (CompanionAvatar.Dim).</summary>
    public void Dim(bool on) => Companion?.Dim(on);

    /// <summary>Clear the bubble now.</summary>
    public void Clear()
    {
        Icon = "";
        State = "";
        Reason = "";
        Redraw();
    }

    private void Redraw()
    {
        if (Bubble == null) return;
        var text = State switch
        {
            "" => _holding.Length > 0 ? $"( {GubbleMagic.FetchIcon} {_holding} )" : "",
            "done" => $"( {Icon} {GubbleMagic.DoneMark} )",
            "refused" => $"( {Icon} {GubbleMagic.RefusedMark} )",
            _ => $"( {Icon} )",
        };
        Bubble.Text = text;
        ReasonLabel.Text = Reason;
        if (text != _shownText || Reason != _shownReason)
        {
            (_shownText, _shownReason) = (text, Reason);
            Shown?.Invoke(text, Reason);
        }
        Bubble.AddThemeColorOverride("font_color", State == "refused" ? new Color("ffb8a6") : State == "shrug" ? new Color("dfe8e4") : new Color("fff3d6"));
        Place();
    }

    public override void _Process(double delta)
    {
        if (_showFor != double.MaxValue && State.Length > 0)
        {
            _showFor -= delta;
            if (_showFor <= 0) Clear();
        }
        Place();
    }

    /// <summary>Where the name tag's top is on screen now (the bubble sits on it), or null when the Gubble is behind the view.</summary>
    public Vector2? TagTopOnScreen()
    {
        if (Companion == null || !IsInstanceValid(Companion) || !Companion.IsInsideTree() || !IsInsideTree()) return null;
        var camera = GetViewport().GetCamera3D();
        if (camera == null) return null;
        var tag = Companion.GetNodeOrNull<Node3D>(TagPath);
        var at = tag?.GlobalPosition ?? Companion.GlobalPosition + Vector3.Up * Companion.BodyHeightM * 1.35f;
        if (camera.IsPositionBehind(at)) return null;
        // The name tag is 2.5 % of the screen's height, centred on its node (CompanionAvatar).
        return camera.UnprojectPosition(at) - new Vector2(0, GetViewport().GetVisibleRect().Size.Y * 0.0125f + GapPx);
    }

    /// <summary>The bubble centred over the name tag, the reason over the bubble, both kept whole on screen; hidden off it.</summary>
    private void Place()
    {
        if (Bubble == null) return;
        var top = Bubble.Text.Length > 0 ? TagTopOnScreen() : null;
        Bubble.Visible = top != null && Bubble.Text.Length > 0;
        ReasonLabel.Visible = Bubble.Visible && Reason.Length > 0;
        if (top is not { } anchor) return;
        var screen = GetViewport().GetVisibleRect().Size;
        Vector2 Fit(Vector2 at, Vector2 size) => new(Mathf.Clamp(at.X, 0, Mathf.Max(0, screen.X - size.X)), Mathf.Clamp(at.Y, 0, Mathf.Max(0, screen.Y - size.Y)));
        var bubble = Bubble.GetCombinedMinimumSize();
        Bubble.Size = bubble;
        Bubble.Position = Fit(anchor - new Vector2(bubble.X * 0.5f, bubble.Y), bubble);
        if (!ReasonLabel.Visible) return;
        var reason = ReasonLabel.GetCombinedMinimumSize();
        ReasonLabel.Size = reason;
        ReasonLabel.Position = Fit(new Vector2(anchor.X - reason.X * 0.5f, Bubble.Position.Y - GapPx - reason.Y), reason);
    }
}
