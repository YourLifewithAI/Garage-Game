using Godot;
using System;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Ui;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Fire's camp on screen (RoomHud.Camp.cs): Fire shown locked until the first rest (decision 72), the rest key (Z) sits the player down by the starting campfire in the rest crouch and
/// moving stands them up; the expertise announcement, a chime and the two-line card for every expertise learned, once each, never headless.
/// </summary>
public partial class PlayHudTest
{
    private async Task TestCampHud()
    {
        var host = CommandHost.Of(_world)!;
        var player = _world.Player;
        host.ConfigureGlowProgress(new HudGlowStore(GlowProgress.Fresh));
        var spawn = host.Room.SpawnFor("player").PositionM;
        // Decision 72: before the first rest the Magic panel shows Fire locked, its preview button off.
        _hud.ToggleMagicPanel();
        await Frames(2);
        var lockedBefore = _hud.FireShownLocked;
        _hud.ToggleMagicPanel();
        Check(lockedBefore, "before the first rest the Magic panel shows Fire locked (its preview off, the reason shown)");
        Check(PlayerControls.Label(Act.Rest) == "Z" && PlayerControls.Specs.Any(spec => spec.Action == Act.Rest), "the rest key is Z, an input action in the map and the list of controls");
        Check(player.TryTeleportTo(spawn), "(the player stands at the starting campfire)");
        await Frames(4);
        var part = player.GetNode<Node3D>("OriginalPrototypeBody").GetChildren().OfType<Node3D>().First();
        var standing = part.Transform;
        var cards = _hud.CardsShown;
        _hud._UnhandledInput(Press(Key.Z));
        await Frames(2);
        Check(_hud.Sitting && host.PlayerResting && Mathf.IsEqualApprox(part.Transform.Basis.Y.Length(), standing.Basis.Y.Length() * RoomHud.RestCrouch) &&
              host.GlowProgress.Has(ExpertiseLists.Fire, ExpertiseLists.FirstFire),
            "Z by the starting campfire sits the player down (the rest crouch), and the rest teaches Fire");
        _hud.ToggleMagicPanel();
        await Frames(2);
        var lockedAfter = _hud.FireShownLocked;
        _hud.ToggleMagicPanel();
        Check(!lockedAfter, "after it the Magic panel's Fire is no longer locked");
        Check(_hud.CardsShown == cards && !_hud.CardVisible && _hud.ChimesPlayed == 0, "headless, the announcement shows no card and plays no chime");
        Check(player.TryTeleportTo(spawn + new Vector3(0.12f, 0, 0)), "(the player moves)");
        await Frames(2);
        Check(!_hud.Sitting && part.Transform.IsEqualApprox(standing), "moving stands the player up, the body drawn as it was");
        // With the window's seam: the chime and the card, once for each expertise newly learned, in the journal's names.
        RoomHud.CardsHeadlessForTests = true;
        try
        {
            Check(host.RecordExpertise(ExpertiseLists.Fire, ExpertiseLists.Campfire), "(the Gubble learns Campfire)");
            await Frames(3);
            Check(_hud.CardVisible && _hud.CardText == "The Gubble has learned Campfire.\nWhat will you do with it?" && _hud.CardsShown == cards + 1 && _hud.ChimesPlayed == 1,
                "a chime and the two-line card announce it: " + _hud.CardText.Replace("\n", " / "));
            Check(_hud.CardTitleFor(ExpertiseLists.Fire, ExpertiseLists.FirstFire) == "The Gubble has learned Fire." &&
                  _hud.CardTitleFor(ExpertiseLists.Glow, GlowProgress.Lantern) == "The Gubble has learned Lantern.", "every ability's expertise is named as the journal names it");
            Check(!host.RecordExpertise(ExpertiseLists.Fire, ExpertiseLists.Campfire), "(learning it again records nothing)");
            for (var waited = 0; waited < 600 && _hud.CardVisible; waited++) await Frames(1);
            await Frames(3);
            Check(!_hud.CardVisible && _hud.CardsShown == cards + 1 && _hud.ChimesPlayed == 1, "the card fades after its time and never comes again for the same expertise");
        }
        finally { RoomHud.CardsHeadlessForTests = false; }
    }
}
