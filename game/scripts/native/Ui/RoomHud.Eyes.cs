using Godot;

namespace GarageGame.Native;

/// <summary>
/// The player's own setting "Let my AI see through the Gubble's eyes" (view.look; docs/companion/PERCEPTION.md). It lives in
/// the player's profile (CommandHost.EyesProfileKey), never in room state, and only this control changes it: no command or
/// request reaches it. The command host reads it and refuses the AI's look while it is off.
/// </summary>
public partial class RoomHud
{
    private CheckButton? _eyesToggle;

    /// <summary>The setting's switch in the customization panel (also read by the headless HUD suite).</summary>
    public CheckButton? EyesToggle => _eyesToggle;

    private void BuildEyesSetting(Node options)
    {
        var host = Host;
        _eyesToggle = new CheckButton
        {
            Name = "AiEyesSetting", Text = "Let my AI see through the Gubble's eyes",
            ButtonPressed = host?.GubbleEyesAllowed ?? Kernel.CommandHost.LoadGubbleEyes(ProfilePath),
            TooltipText = "Your AI can ask for a small picture of what the Gubble sees. The picture goes to your AI's provider.",
            FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 38),
        };
        _eyesToggle.Toggled += SetAiEyes;
        options.AddChild(_eyesToggle);
    }

    /// <summary>The player's choice: the host follows it at once, and the profile keeps it (this session only if it cannot be written).</summary>
    public void SetAiEyes(bool allowed)
    {
        Host?.SetGubbleEyes(allowed);
        if (_eyesToggle != null && _eyesToggle.ButtonPressed != allowed) _eyesToggle.SetPressedNoSignal(allowed);
        var saved = SaveProfileValue(Kernel.CommandHost.EyesProfileKey, allowed);
        _noticeText = (allowed ? "Your AI can see through the Gubble's eyes." : "Your AI no longer sees through the Gubble's eyes.")
            + (saved ? "" : " (Not saved: it lasts this session.)");
    }
}
