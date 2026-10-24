using Godot;
using GarageGame.Native.Companion;

namespace GarageGame.Native.Ui;

/// <summary>
/// "Gubble: connected" or "Gubble: waiting for your AI", in the room's first seconds (docs/game/MECHANICS.md section 3: the AI
/// connects only inside a room today, and nothing is drawn while no AI is linked, so the player is told which it is). A line at the top
/// of the screen under the meters' level, shown for <see cref="FirstSeconds"/> once the HUD is on screen (not during the intro), and again
/// for <see cref="ChangeSeconds"/> whenever the link changes (an AI joins or leaves). When the game runs without a link at all (the
/// run's switch is off, or another window holds it) the line says that instead of waiting for an AI that cannot come. The words are the
/// game's own; nothing an AI wrote is ever shown here.
/// </summary>
public partial class GubbleLinkNote : CanvasLayer
{
    public const double FirstSeconds = 8.0;
    public const double ChangeSeconds = 4.0;
    public const string Connected = "Gubble: connected";
    public const string Waiting = "Gubble: waiting for your AI";
    public const string NoLink = "Gubble: no AI link in this window";

    private RoomWorld _world = null!;
    private RoomHud? _hud;
    private Label _label = null!;
    private double _showingS;
    private string _last = "";
    private bool _first = true;

    /// <summary>The words showing now (empty when the line is hidden).</summary>
    public string Words => _label.Visible ? _label.Text : "";
    /// <summary>The line's words if shown now (the link's state in the game's words), whether or not the line is on screen.</summary>
    public string Status => StatusWords(CompanionBridge.Of(_world));

    public static GubbleLinkNote Attach(RoomWorld world, RoomHud? hud)
    {
        var note = new GubbleLinkNote { Name = "GubbleLinkNote", _world = world, _hud = hud, Layer = 3 };
        world.AddChild(note);
        return note;
    }

    /// <summary>The link's state in the game's words: connected, waiting for the AI, or no link here at all. Empty without a bridge (a fixture).</summary>
    public static string StatusWords(CompanionBridge? bridge)
    {
        if (bridge == null) return "";
        if (bridge.Link == null) return NoLink;
        return bridge.State == "offline" ? Waiting : Connected;
    }

    public override void _Ready()
    {
        _label = new Label
        {
            Name = "LinkLine", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center,
        };
        _label.AddThemeFontSizeOverride("font_size", 20);
        _label.AddThemeColorOverride("font_color", MenuStyle.Ink);
        _label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.7f));
        _label.AddThemeConstantOverride("shadow_offset_x", 1);
        _label.AddThemeConstantOverride("shadow_offset_y", 1);
        _label.AddThemeStyleboxOverride("normal", MenuStyle.Box(new Color(0.075f, 0.13f, 0.13f, 0.85f), null, 0, 12, 10));
        AddChild(_label);
        _label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
        _label.GrowHorizontal = Control.GrowDirection.Both;
        _label.OffsetTop = 18;
    }

    public override void _Process(double delta)
    {
        if (!IsInstanceValid(_world)) return;
        var status = StatusWords(CompanionBridge.Of(_world));
        if (status.Length == 0) return;
        // The line waits for the intro to end: the HUD is out of sight while it plays.
        var onScreen = _hud == null || !IsInstanceValid(_hud) || !_hud.IntroPlaying;
        if (!onScreen) { _label.Visible = false; _first = true; return; }
        if (_first) { _first = false; _last = status; _showingS = FirstSeconds; }
        else if (status != _last) { _last = status; _showingS = ChangeSeconds; }
        if (_showingS > 0) _showingS -= delta;
        _label.Text = status;
        _label.Visible = _showingS > 0;
    }
}
