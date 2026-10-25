using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using Godot;

namespace GarageGame.Tests.Kernel;

/// <summary>The player's setting "Let my AI see through the Gubble's eyes": a switch in the settings panel, kept in the profile, obeyed by the host.</summary>
public partial class PlayHudTest
{
    private async Task TestEyesSetting()
    {
        var host = CommandHost.Of(_world)!;
        var toggle = _hud.EyesToggle;
        Check(toggle != null && toggle.ButtonPressed && host.GubbleEyesAllowed && toggle.Text == "Let my AI see through the Gubble's eyes",
            "the settings panel has the eyes setting, on by default, and the host agrees");
        toggle!.ButtonPressed = false;
        await Frames(1);
        var look = new JsonObject
        {
            ["schema"] = "garage_game.query", ["version"] = 1, ["query_id"] = "hud-look", ["room_id"] = _world.Room.RoomId, ["op"] = "view.look",
            ["args"] = new JsonObject { ["actor"] = CommandHost.CompanionAvatarId },
        };
        var refused = await host.HandleObjectAsync(look.ToJsonString(), CommandHost.CompanionPrincipal);
        Check(!host.GubbleEyesAllowed && !CommandHost.LoadGubbleEyes(RoomHud.ProfilePath) && refused["error"]?["code"]?.GetValue<string>() == "permission_denied",
            "switching it off is saved in the player's profile and the host refuses the AI's look");
        toggle.ButtonPressed = true;
        await Frames(1);
        Check(host.GubbleEyesAllowed && CommandHost.LoadGubbleEyes(RoomHud.ProfilePath), "switching it back on is saved too");
        await TestSavingAppearanceKeepsSettings(host);
    }

    /// <summary>A setting writes its own key only: the player's other settings, and any a later build adds, stay as they are.</summary>
    private async Task TestSavingAppearanceKeepsSettings(CommandHost host)
    {
        var fireSetting = (CheckButton)_hud.FindChild("GubbleFireSetting", true, false)!;
        _hud.EyesToggle!.ButtonPressed = false;
        fireSetting.EmitSignal(BaseButton.SignalName.Toggled, false);
        await Frames(1);
        var config = new ConfigFile();
        Check(config.Load(RoomHud.ProfilePath) == Error.Ok, "the profile holds the two settings");
        config.SetValue("profile", "a_later_setting", 7);
        Check(config.Save(RoomHud.ProfilePath) == Error.Ok, "and a setting a later build adds");
        _hud.SetPlayIntro(false);
        var saved = new ConfigFile();
        Check(saved.Load(RoomHud.ProfilePath) == Error.Ok && !CommandHost.LoadGubbleEyes(RoomHud.ProfilePath) && !CommandHost.LoadGubbleFire(RoomHud.ProfilePath) &&
            saved.GetValue("profile", "a_later_setting", 0).AsInt32() == 7 && !RoomHud.LoadPlayIntro(RoomHud.ProfilePath),
            "a third setting saved beside them keeps \"Let my AI see\" and \"Let the Gubble use fire\" off, and every other setting, exactly as they were");
        _hud.SetPlayIntro(true);
        _hud.EyesToggle.ButtonPressed = true;
        fireSetting.EmitSignal(BaseButton.SignalName.Toggled, true);
        await Frames(1);
        Check(host.GubbleEyesAllowed && host.GubbleFireAllowed && CommandHost.LoadGubbleEyes(RoomHud.ProfilePath) && CommandHost.LoadGubbleFire(RoomHud.ProfilePath),
            "both settings switch back on for the tests that follow");
    }
}
