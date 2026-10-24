using Godot;
using System;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Ui;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// One interaction key with a prompt (the second playtest round's P2, item 2; decisions 86 and 90; RoomHud.Interact.cs): by a lit campfire
/// the prompt "E: sit by the fire" stands beside it and E sits you down; sitting, "E: stand up" and E stands you up; Z still sits you down
/// this round; in the flames E says the host's words and does not sit; beside a Fractal in reach, "E: pick up the fractal" and E picks it
/// up. (The thing faced and the thing held are in the hand keys' test and the gather key's.)
/// </summary>
public partial class PlayHudTest
{
    private async Task TestInteractKey()
    {
        var host = CommandHost.Of(_world)!;
        var player = _world.Player;
        player.ReadKeyboard = false;
        _hud.SetViewMode(1);
        if (_hud.Sitting) _hud.RestKey();
        var spawn = host.Room.SpawnFor("player").PositionM;
        var camp = host.StartingCampfire!;
        host.Nature.WeatherS = 0;
        // A long suite's rain may have put the starting campfire out: relit through Fire's own path (the place decides).
        if (!host.IsCampfireLit(CampLaws.StartingId))
        {
            host.LearnFireForTests();
            host.PlayerFireLight(camp.PositionM);
        }
        Check(host.IsCampfireLit(CampLaws.StartingId), "(the starting campfire burns)");
        Check(player.TryTeleportTo(spawn), "(the player stands at the starting campfire)");
        await Frames(40);
        var prompt = _hud.InteractPrompt;
        Check(_hud.Interaction.Kind == RoomHud.InteractKind.Sit && _hud.Interaction.Words == "E: sit by the fire" && prompt.Visible && prompt.Text == "E: sit by the fire",
            $"by a lit campfire the prompt says \"E: sit by the fire\" ({_hud.Interaction.Kind}: {prompt.Text})");
        var camera = _hud.GetViewport().GetCamera3D();
        var fireOnScreen = camera.UnprojectPosition(camp.PositionM + Vector3.Up * 0.06f);
        var promptRect = new Rect2(prompt.Position, prompt.GetCombinedMinimumSize());
        var gap = promptRect.Grow(2).HasPoint(fireOnScreen) ? 0 : new[] { promptRect.Position, promptRect.End, new Vector2(promptRect.Position.X, promptRect.End.Y), new Vector2(promptRect.End.X, promptRect.Position.Y) }.Min(c => c.DistanceTo(fireOnScreen));
        Check(camera.IsPositionBehind(camp.PositionM) || gap < 40,
            $"and it stands beside the campfire on the screen: the fire at {fireOnScreen.X:0},{fireOnScreen.Y:0}, the prompt at {promptRect.Position.X:0},{promptRect.Position.Y:0} {promptRect.Size.X:0}x{promptRect.Size.Y:0} ({gap:0} px from it)");
        _hud._UnhandledInput(Press(Key.E));
        await Frames(3);
        Check(_hud.Sitting && host.PlayerResting, "E sits the player down by the fire (the host's rest)");
        Check(_hud.Interaction.Kind == RoomHud.InteractKind.Stand && prompt.Visible && prompt.Text == "E: stand up", "sitting, the prompt says \"E: stand up\": " + prompt.Text);
        _hud._UnhandledInput(Press(Key.E));
        await Frames(3);
        Check(!_hud.Sitting && !host.PlayerResting && _hud.Interaction.Kind == RoomHud.InteractKind.Sit, "E stands the player up again, and the prompt offers the sit again");
        _hud._UnhandledInput(Press(Key.Z));
        await Frames(2);
        var zSat = _hud.Sitting;
        _hud._UnhandledInput(Press(Key.E));
        await Frames(2);
        Check(zSat && !_hud.Sitting, "Z still sits you down this round (decision 90), and E stands you up from it");
        // In the flames: the resting spot is beside the fire (decision 85); E says the host's words and sits no one down.
        Check(player.TryTeleportTo(camp.PositionM), "(the player steps into the campfire's flames)");
        await Frames(30);
        var inFlames = _hud.Interaction;
        _hud._UnhandledInput(Press(Key.E));
        await Frames(2);
        Check(inFlames.Kind == RoomHud.InteractKind.Camp && inFlames.Words == CommandHost.RestInFlames && prompt.Text == CommandHost.RestInFlames && !_hud.Sitting && _hud.NoticeText == CommandHost.RestInFlames,
            "standing in its flames the prompt and E say the host's words, and nobody sits down in the fire: " + inFlames.Words);
        // A Fractal in reach, away from the campfire: "E: pick up the fractal", and E picks it up.
        Check(player.TryTeleportTo(spawn + new Vector3(-0.45f, 0, 0)), "(the player steps away from the campfire)");
        await Frames(10);
        var feet = player.GlobalPosition;
        var fractal = new IslandFractal("fractal:interact01", feet + new Vector3(0, 0, -0.07f), null, null);
        var found = host.Vitals.Fractals.Found;
        host.FractalsForTests = new[] { fractal };
        try
        {
            await Frames(30);
            Check(_hud.Interaction.Kind == RoomHud.InteractKind.Fractal && prompt.Visible && prompt.Text == $"E: pick up the {MeterWords.One(host, "fractals").ToLowerInvariant()}" &&
                  host.Fractals.Any(f => f.Id == fractal.Id),
                "beside a Fractal in reach (not touching it) the prompt says \"" + prompt.Text + "\"");
            _hud._UnhandledInput(Press(Key.E));
            await Frames(2);
            Check(!host.Fractals.Any(f => f.Id == fractal.Id) && host.Vitals.Fractals.Found == found + 1 && _hud.NoticeText.Contains("picked up", StringComparison.Ordinal),
                "E picks it up through the host's item.pickup: " + _hud.NoticeText);
        }
        finally { host.FractalsForTests = Array.Empty<IslandFractal>(); }
        await Frames(30);
        Check(_hud.Interaction.Kind is RoomHud.InteractKind.None or RoomHud.InteractKind.PickUp && !prompt.Visible, $"with the Fractal gone, no prompt stands beside it ({_hud.Interaction.Kind})");
    }
}
