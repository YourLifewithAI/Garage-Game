using Godot;

namespace GarageGame.Native;

/// <summary>
/// The HUD's part in the room-to-island intro (Look/Intro/RoomIntro.cs): while it plays the HUD is out of sight and blocked (the intro
/// takes every input first), with one line of its own, "Press any key to skip", on a layer above the intro's picture. And the
/// player's setting "Play the intro" in the customization panel: kept in the profile (PlayIntroProfileKey, on unless the player turns
/// it off), never in room state, read when a room loads.
/// </summary>
public partial class RoomHud
{
    /// <summary>The profile's key for "Play the intro", and its value when the player never chose (the founder: on every load).</summary>
    public const string PlayIntroProfileKey = "play_intro";
    public const bool DefaultPlayIntro = true;
    public const string SkipPromptText = "Press any key to skip";

    /// <summary>True while the intro plays: the HUD is hidden, its focus and tags off.</summary>
    public bool IntroPlaying { get; private set; }
    /// <summary>The skip prompt while an intro plays (null otherwise), for the HUD suite and the intro's tests.</summary>
    public Label? SkipPrompt => _skipPrompt;
    public CheckButton? IntroToggle => _introToggle;

    private CanvasLayer? _introLayer;
    private Label? _skipPrompt;
    private CheckButton? _introToggle;
    private bool _visibleBeforeIntro = true;

    /// <summary>"Play the intro" as the profile says: on when the player never chose, or when the profile cannot be read.</summary>
    public static bool LoadPlayIntro(string profilePath)
    {
        var config = new ConfigFile();
        if (config.Load(profilePath) != Error.Ok) return DefaultPlayIntro;
        var value = config.GetValue("profile", PlayIntroProfileKey, DefaultPlayIntro);
        return value.VariantType != Variant.Type.Bool || value.AsBool();
    }

    private void BuildIntroSetting(Node options)
    {
        _introToggle = new CheckButton
        {
            Name = "PlayIntroSetting", Text = "Play the intro",
            ButtonPressed = LoadPlayIntro(ProfilePath),
            TooltipText = "When the island loads: about 15 seconds of your room turning into its island. Any key skips it.",
            FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 38),
        };
        _introToggle.Toggled += SetPlayIntro;
        options.AddChild(_introToggle);
    }

    /// <summary>The player's choice, kept in the profile for the next load (this session only if it cannot be written).</summary>
    public void SetPlayIntro(bool play)
    {
        if (_introToggle != null && _introToggle.ButtonPressed != play) _introToggle.SetPressedNoSignal(play);
        var saved = SaveProfileValue(PlayIntroProfileKey, play);
        _noticeText = (play ? "The intro plays when the island loads." : "The intro is off: the island opens straight into play.")
            + (saved ? "" : " (Not saved: it lasts this session.)");
    }

    /// <summary>The intro begins: the HUD goes out of sight (its own layer of the skip prompt stays) and its focus is let go.</summary>
    public void BeginIntro()
    {
        if (IntroPlaying) return;
        IntroPlaying = true;
        _visibleBeforeIntro = Visible;
        Visible = false;
        SetFocus(null, default, "");
        if (_introLayer == null)
        {
            // A layer of its own: a CanvasLayer's visibility does not reach the layers under it, so the prompt shows while the HUD is hidden.
            _introLayer = new CanvasLayer { Name = "IntroPromptLayer", Layer = Layer + 100 };
            AddChild(_introLayer);
            var theme = new Theme { DefaultFontSize = 18 };
            theme.SetColor("font_color", "Label", new Color("f0e6cf"));
            theme.SetColor("font_shadow_color", "Label", new Color(0, 0, 0, 0.6f));
            theme.SetConstant("shadow_offset_x", "Label", 1);
            theme.SetConstant("shadow_offset_y", "Label", 1);
            _skipPrompt = new Label
            {
                Name = "SkipPrompt", Text = SkipPromptText, Theme = theme, MouseFilter = Control.MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Right, Modulate = new Color(1, 1, 1, 0),
            };
            _introLayer.AddChild(_skipPrompt);
            _skipPrompt.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
            _skipPrompt.GrowHorizontal = Control.GrowDirection.Begin;
            _skipPrompt.GrowVertical = Control.GrowDirection.Begin;
            _skipPrompt.OffsetRight = -28;
            _skipPrompt.OffsetBottom = -24;
        }
        _introLayer.Visible = true;
        SetSkipPrompt(0f);
    }

    /// <summary>How much the skip prompt shows (0 to 1), as the intro's moment says.</summary>
    public void SetSkipPrompt(float alpha)
    {
        if (_skipPrompt == null) return;
        var a = Mathf.Clamp(alpha, 0f, 1f) * 0.85f;
        if (!Mathf.IsEqualApprox(_skipPrompt.Modulate.A, a)) _skipPrompt.Modulate = new Color(1, 1, 1, a);
    }

    /// <summary>The intro ended (played out or skipped): the HUD is back as it was.</summary>
    public void EndIntro()
    {
        if (!IntroPlaying) return;
        IntroPlaying = false;
        Visible = _visibleBeforeIntro;
        // The prompt's layer goes with the intro: the HUD is left exactly as it was built.
        _introLayer?.QueueFree();
        _introLayer = null;
        _skipPrompt = null;
    }
}
