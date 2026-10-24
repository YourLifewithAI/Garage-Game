using Godot;
using System.Collections.Generic;

namespace GarageGame.Native.Companion;

/// <summary>
/// The companion's state, shown on its avatar while the player's AI is linked (A2; LIVE-VOICE.md's accessibility
/// list): listening, planning, acting, or waiting for the player's yes. One short line under the name tag, in the tag's
/// style (solid, alpha-cut, billboarded, a fixed screen size), each state in its own colour; nothing when no AI is
/// linked. The words and colours are a first pass for the founder's eye, not settled art. Since M8 it also says Trust's free early
/// warning for a few seconds (Warn), linked AI or not, then shows the state again; the state itself is unchanged.
/// </summary>
public partial class CompanionStatusCue : Label3D
{
    public static readonly IReadOnlyDictionary<string, (string Text, Color Colour)> Looks = new Dictionary<string, (string, Color)>
    {
        ["listening"] = ("listening", new Color("bfe3dc")),
        ["planning"] = ("planning…", new Color("f2d58a")),
        ["acting"] = ("acting", new Color("f4a46a")),
        ["waiting"] = ("waiting for your yes", new Color("f0a8b8")),
    };

    public string State { get; private set; } = "offline";
    /// <summary>How long the early warning stays (M8; provisional).</summary>
    public const double WarningHoldS = 3.0;
    public static readonly Color WarningColour = new("ffb36b");
    /// <summary>The words of the warning showing now, or empty.</summary>
    public string Warning { get; private set; } = "";
    private double _warningLeftS;

    public override void _Ready()
    {
        Name = "CompanionState";
        FontSize = 30;
        OutlineSize = 10;
        PixelSize = 0.0003f;
        FixedSize = true;
        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
        NoDepthTest = false;
        AlphaCut = AlphaCutMode.Discard;
        AlphaScissorThreshold = 0.5f;
        OutlineModulate = new Color("18332d");
        // One line below the name tag (offsets are in label pixels, so the gap holds at every distance). As large as the tag,
        // with a thick dark outline, so it reads over the landscape (the Glow playtest, 9 October).
        Offset = new Vector2(0, -42);
        Display(State);
    }

    /// <summary>The free early warning (M8): its curated words over the state for WarningHoldS, then the state again.</summary>
    public void Warn(string words)
    {
        Warning = words;
        _warningLeftS = WarningHoldS;
        Text = words;
        Modulate = WarningColour;
        Visible = true;
    }

    public override void _Process(double delta)
    {
        if (_warningLeftS <= 0) return;
        _warningLeftS -= delta;
        if (_warningLeftS > 0) return;
        Warning = "";
        Display(State);
    }

    public void Display(string state)
    {
        State = state;
        if (_warningLeftS > 0) return;
        if (!Looks.TryGetValue(state, out var look))
        {
            Visible = false;
            return;
        }
        Text = look.Text;
        Modulate = look.Colour;
        Visible = true;
    }
}
