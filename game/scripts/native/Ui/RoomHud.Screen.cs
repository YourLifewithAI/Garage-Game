using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GarageGame.Native;

/// <summary>
/// The screen the game is played on (docs/game/MECHANICS.md decision 66, 16 October: "I tend to want to play at full screen"). The
/// window opens in full screen when a launch asks for it ("--fullscreen", run-room.ps1 -Fullscreen) or when the machine's settings
/// remember it (<c>fullscreen</c> in the profile, beside "Play the intro"); the full-screen key (F11, the input action view_fullscreen)
/// toggles it and writes the choice, so after the founder presses it once every launch opens the same way. A forced launch is not
/// written. Nothing happens when headless, in a boot check (--quit-after-ready=) or in any launch that is not the game's own boot
/// (NativeGameBoot: the look's capture tools and the tests build their own scenes), so the runners and the capture tools keep their
/// windows. The H help also reads out the settings (SettingsWords), the one place every key and setting is read.
/// </summary>
public partial class RoomHud
{
    /// <summary>The profile's key for the window in full screen, and its value when the player never chose (a window).</summary>
    public const string FullscreenProfileKey = "fullscreen";
    public const bool DefaultFullscreen = false;
    /// <summary>The launch argument that forces full screen for one launch (a user argument, after "--").</summary>
    public const string FullscreenArgument = "--fullscreen";
    private const string BootCheckArgument = "--quit-after-ready=";

    /// <summary>Whether the window is in full screen now; with no window (headless) what the HUD last asked for.</summary>
    public bool Fullscreen => HasWindow ? DisplayServer.WindowGetMode() is DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen : _fullscreenAsked;
    private bool _fullscreenAsked;
    /// <summary>Whether this launch has a window to put in full screen (read once: the display server never changes).</summary>
    private static bool HasWindow => _hasWindow ??= DisplayServer.GetName() != "headless";
    private static bool? _hasWindow;

    /// <summary>"Full screen" as the profile says: off unless the player turned it on with the key, or when the profile cannot be read.</summary>
    public static bool LoadFullscreen(string profilePath)
    {
        var config = new ConfigFile();
        if (config.Load(profilePath) != Error.Ok) return DefaultFullscreen;
        var value = config.GetValue("profile", FullscreenProfileKey, DefaultFullscreen);
        return value.VariantType == Variant.Type.Bool && value.AsBool();
    }

    /// <summary>
    /// Whether a launch starts in full screen. Never headless, never a boot check (the runners and the smoke boot pass
    /// --quit-after-ready=), and only the game's own boot (NativeGameBoot, the main scene), never a tool's or a test's own scene, which
    /// sizes its own window (the look's captures read the profile of the machine too). Then "--fullscreen" forces it, else the
    /// remembered choice decides.
    /// </summary>
    public static bool StartsFullscreen(string displayServer, IReadOnlyCollection<string> userArguments, bool gameBoot, bool remembered)
    {
        if (displayServer == "headless" || !gameBoot || userArguments.Any(a => a.StartsWith(BootCheckArgument, System.StringComparison.Ordinal))) return false;
        return userArguments.Contains(FullscreenArgument) || remembered;
    }

    /// <summary>The end of the HUD's build: a launch that asks for full screen gets it, on the main screen. Not written to the profile.</summary>
    private void StartFullscreenIfAsked()
    {
        var gameBoot = GetParent()?.GetParent() is NativeGameBoot;
        if (StartsFullscreen(DisplayServer.GetName(), OS.GetCmdlineUserArgs(), gameBoot, LoadFullscreen(ProfilePath))) PutWindow(fullscreen: true);
    }

    /// <summary>The window mode asked for: full screen on the main screen, or the window back as it was. With no window, only remembered here.</summary>
    private void PutWindow(bool fullscreen)
    {
        _fullscreenAsked = fullscreen;
        if (!HasWindow) return;
        if (fullscreen) DisplayServer.WindowSetCurrentScreen(DisplayServer.GetPrimaryScreen());
        DisplayServer.WindowSetMode(fullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);
    }

    /// <summary>The full-screen key: the window the other way, the choice kept in the profile (this session only if it cannot be written), the screen saying so.</summary>
    public void ToggleFullscreen() => SetFullscreen(!Fullscreen);

    /// <summary>Full screen or a window, kept in the profile for every later launch.</summary>
    public void SetFullscreen(bool fullscreen)
    {
        PutWindow(fullscreen);
        var saved = SaveProfileValue(FullscreenProfileKey, fullscreen);
        _noticeText = (fullscreen ? $"Full screen: {K(Act.ViewFullscreen)} goes back to a window." : $"A window again: {K(Act.ViewFullscreen)} goes full screen.")
            + (saved ? "" : " (Not saved: it lasts this session.)");
    }

    /// <summary>The help's line for the window's key: what it does and that the game remembers it.</summary>
    private string ScreenKeysLine() => $"{K(Act.ViewFullscreen)} full screen on or off (kept for your next launch)";

    // ---- the settings, read out in the H help ----

    private Label? _settingsLine;
    /// <summary>The settings line at the bottom of the H help's status lines.</summary>
    public Label? SettingsLine => _settingsLine;

    private void BuildSettingsLine(Node statusLines)
    {
        _settingsLine = new Label { Name = "SettingsLine" };
        statusLines.AddChild(_settingsLine);
        _keyTexts.Add((words => _settingsLine.Text = words, SettingsWords));
    }

    /// <summary>The player's own switches as they are now: the window, the intro, the numbers on the meters and the AI's eyes.</summary>
    private string SettingsWords()
    {
        static string OnOff(bool on) => on ? "on" : "off";
        return $"Settings ({K(Act.HudCustomize)} changes them): full screen {OnOff(Fullscreen)} · play the intro {OnOff(_introToggle?.ButtonPressed ?? DefaultPlayIntro)} · " +
               $"numbers on the meters {OnOff(MeterNumbers)} · your AI sees through the Gubble's eyes {OnOff(_eyesToggle?.ButtonPressed ?? true)}";
    }

    /// <summary>The settings the help reads out, as bits the key texts compare: when one changes the line is made again.</summary>
    private int SettingsBits() =>
        (Fullscreen ? 1 : 0) | ((_introToggle?.ButtonPressed ?? DefaultPlayIntro) ? 2 : 0) | (MeterNumbers ? 4 : 0) | ((_eyesToggle?.ButtonPressed ?? true) ? 8 : 0);
}
