using System;
using Godot;

namespace GarageGame.Native.Ui;

/// <summary>
/// The small eye at the lower right, just above the journal's book (the founder, 13 October: "a small icon the player can select
/// that allows them to see through the eyes of the Gubble"): a leather roundel with stitching and a gilt rim, as the journal's
/// cover is, holding an eye whose iris is the Gubble's colour, with its key beneath, named from the input map. A click opens the
/// view through the Gubble's eyes and a second click returns. When the player's AI takes a picture through the Gubble's eyes,
/// the eye blinks once, softly (Blink): the cue is here, never on the Gubble. It never takes the keyboard focus, so Space and
/// Enter (ui_accept) can never press it.
/// </summary>
public partial class GubbleEyesIcon : Button
{
    /// <summary>One blink: the lid closes and opens again over this long, under a soft warm glow.</summary>
    public const float BlinkSeconds = 0.7f;
    /// <summary>The icon's size; the roundel is drawn in its upper part, the words beneath.</summary>
    public static readonly Vector2 IconSize = new(94, 84);

    /// <summary>The key's name as the player's controls say it now ("I").</summary>
    public Func<string> KeyLabel { get; set; } = () => "";
    /// <summary>The Gubble's colour now (its aura, else its body): the eye's iris.</summary>
    public Func<Color> IrisColor { get; set; } = () => new Color("65b9b0");
    /// <summary>The companion's name in a sentence ("the Gubble").</summary>
    public Func<string> GubbleName { get; set; } = () => "the Gubble";
    /// <summary>Whether the player is looking through the Gubble's eyes now: the eye is lit and a click returns.</summary>
    public bool Looking { get; set; }
    /// <summary>Blinks shown (for tests).</summary>
    public int Blinks { get; private set; }
    /// <summary>Whether a blink is playing now.</summary>
    public bool Blinking => _blinkAge < BlinkSeconds;

    private double _blinkAge = double.MaxValue;
    private string _shownWords = "";

    public override void _Ready()
    {
        Name = "GubbleEyesIcon";
        FocusMode = FocusModeEnum.None;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        foreach (var state in new[] { "normal", "hover", "pressed", "focus", "disabled" })
            AddThemeStyleboxOverride(state, new StyleBoxEmpty());
        MouseEntered += QueueRedraw;
        MouseExited += QueueRedraw;
        ShowWords();
    }

    /// <summary>A short, soft blink: the player's AI just looked through the Gubble's eyes.</summary>
    public void Blink()
    {
        _blinkAge = 0;
        Blinks++;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (Blinking)
        {
            _blinkAge += delta;
            QueueRedraw();
        }
        // A remapped key or a renamed Gubble shows at once, as the rest of the HUD's words do.
        if (Words() != _shownWords) ShowWords();
    }

    private string Words() => $"{Looking}|{KeyLabel()}|{GubbleName()}";

    private void ShowWords()
    {
        _shownWords = Words();
        var key = KeyLabel();
        var what = Looking ? "Back to your own eyes" : $"See through {GubbleName()}'s eyes";
        TooltipText = key.Length > 0 ? $"{what} · {key}" : what;
        QueueRedraw();
    }

    /// <summary>How far the lid is closed now: 0 open, 1 shut, at the middle of a blink.</summary>
    public float LidClosed => Blinking ? Mathf.Sin((float)(_blinkAge / BlinkSeconds) * Mathf.Pi) : 0f;

    public override void _Draw()
    {
        var centre = new Vector2(Size.X * 0.5f, 31);
        const float radius = 27;
        var blink = LidClosed;
        if (IsHovered()) DrawCircle(centre, radius + 9, new Color(0.9f, 0.72f, 0.4f, 0.16f));
        // The cue: a soft warm glow that swells and fades with the blink.
        if (blink > 0) DrawCircle(centre, radius + 12 * blink, new Color(1f, 0.86f, 0.55f, 0.22f * blink));
        if (Looking) DrawCircle(centre, radius + 6, new Color(0.95f, 0.8f, 0.5f, 0.24f));
        // The roundel: a shadow, worn leather, a dark edge, the tooled ring with its stitching, a gilt rim.
        DrawCircle(centre + new Vector2(2, 3), radius + 1, new Color(0, 0, 0, 0.36f));
        DrawCircle(centre, radius, new Color("2c1c17"));
        DrawCircle(centre, radius - 2, new Color("563525"));
        var rng = new RandomNumberGenerator { Seed = 1847 };
        for (var i = 0; i < 46; i++)
        {
            var angle = rng.RandfRange(0, Mathf.Tau);
            var scuff = centre + Vector2.FromAngle(angle) * rng.RandfRange(4, radius - 5);
            DrawLine(scuff, scuff + new Vector2(rng.RandfRange(1, 3.5f), rng.RandfRange(-1.5f, 1.5f)), new Color(0.85f, 0.64f, 0.37f, 0.09f), 0.6f);
        }
        DrawArc(centre, radius - 3.5f, 0, Mathf.Tau, 48, new Color("d2ac70"), 1.4f, true);
        for (var i = 0; i < 24; i++)
        {
            var angle = i * Mathf.Tau / 24;
            DrawLine(centre + Vector2.FromAngle(angle) * (radius - 7), centre + Vector2.FromAngle(angle + 0.12f) * (radius - 7), new Color("c0a16e"), 0.8f, true);
        }
        DrawEye(centre, 15, blink);
        var key = KeyLabel();
        var words = (Looking ? "Back" : "Eyes") + (key.Length > 0 ? $" · {key}" : "");
        DrawString(ThemeDB.FallbackFont, new Vector2(9, Size.Y - 2), words, HorizontalAlignment.Center, Size.X - 18, 13, new Color("f0e6cf"));
    }

    /// <summary>The almond of the eye in cream and gold, its iris the Gubble's colour; the lid comes down over it as it blinks.</summary>
    private void DrawEye(Vector2 centre, float halfWidth, float closed)
    {
        var open = 1f - closed;
        var gold = new Color("c6a36c");
        var height = halfWidth * 0.62f * open;
        const int steps = 18;
        var almond = new Vector2[steps * 2];
        for (var i = 0; i < steps; i++)
        {
            var t = (float)i / (steps - 1);
            var x = Mathf.Lerp(-halfWidth, halfWidth, t);
            var y = Mathf.Sin(t * Mathf.Pi) * height;
            almond[i] = centre + new Vector2(x, -y);
            almond[steps * 2 - 1 - i] = centre + new Vector2(x, y);
        }
        if (open > 0.08f)
        {
            DrawColoredPolygon(almond, new Color("efe2c2"));
            // The iris and pupil, clipped by the lids as they close.
            var iris = halfWidth * 0.46f;
            var visible = Mathf.Min(iris, height);
            DrawCircle(centre, visible, IrisColor().Darkened(0.05f));
            DrawCircle(centre, Mathf.Min(iris * 0.48f, height * 0.9f), new Color("183631"));
            DrawCircle(centre + new Vector2(-iris * 0.32f, -iris * 0.3f), Mathf.Min(iris * 0.2f, height * 0.4f), new Color(1, 1, 1, 0.8f));
            var outline = new Vector2[almond.Length + 1];
            Array.Copy(almond, outline, almond.Length);
            outline[^1] = almond[0];
            DrawPolyline(outline, gold, 1.4f, true);
        }
        else
        {
            // Shut: the lid's line and three lashes.
            DrawLine(centre + new Vector2(-halfWidth, 0), centre + new Vector2(halfWidth, 0), gold, 1.6f, true);
            for (var i = -1; i <= 1; i++)
                DrawLine(centre + new Vector2(i * halfWidth * 0.45f, 1), centre + new Vector2(i * halfWidth * 0.55f, 5), gold, 1.1f, true);
        }
    }
}
