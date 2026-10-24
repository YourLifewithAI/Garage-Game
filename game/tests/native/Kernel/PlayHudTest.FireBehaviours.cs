using Godot;
using System;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The Fire behaviours packet on screen: collecting a Fractal by walking into it (and by the hands key), the Magic panel's own key (M),
/// and Fire on the wheel and key 6 (decision 77): the ninth wedge just after Fireworks, locked with its mark and reason until the first
/// rest, then a tap that lights the fire where you point at once, its outline flashing as it catches (decision 83), and X that stops the
/// Gubble but never puts a fire out (decision 82).
/// </summary>
public partial class PlayHudTest
{
    private static InputEventKey Release(Key key) => new() { PhysicalKeycode = key, Pressed = false };

    /// <summary>Item 0: the body walks into a Fractal and it is counted and gone; one beside the path is not; the hands key picks up one in reach.</summary>
    private async Task TestFractalTouch()
    {
        var host = CommandHost.Of(_world)!;
        var player = _world.Player;
        var spawn = host.Room.SpawnFor("player").PositionM;
        // Face west, away from the starting campfire in front of the spawn, and walk a few steps to learn which way W takes the body.
        Check(player.TryTeleportTo(spawn + new Vector3(0, 0, 0.1f)), "(the player stands just behind the spawn)");
        player.Rotation = new Vector3(0, Mathf.Pi / 2, 0);
        await Frames(4);
        var reading = player.ReadKeyboard;
        player.ReadKeyboard = true;
        var before = player.GlobalPosition;
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.W, Keycode = Key.W, Pressed = true });
        await Frames(12);
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.W, Keycode = Key.W, Pressed = false });
        await Frames(10);
        var feet = player.GlobalPosition;
        var heading = new Vector3(feet.X - before.X, 0, feet.Z - before.Z);
        Check(heading.Length() > 0.01f, $"(W walks the body: {heading.Length():0.000} m)");
        heading = heading.Normalized();
        var side = new Vector3(-heading.Z, 0, heading.X);
        var ahead = new IslandFractal("fractal:touch01", new Vector3(feet.X, feet.Y, feet.Z) + heading * 0.22f, null, null);
        var beside = new IslandFractal("fractal:touch02", new Vector3(feet.X, feet.Y, feet.Z) + heading * 0.22f + side * 0.14f, null, null);
        host.FractalsForTests = new[] { ahead, beside };
        var found = host.Vitals.Fractals.Found;
        var touched = _hud.FractalsTouched;
        await Frames(12);
        Check(host.Fractals.Count() == 2 && host.Vitals.Fractals.Found == found, "standing 22 cm short of a Fractal collects nothing: it is collected by touch, not by sight");
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.W, Keycode = Key.W, Pressed = true });
        for (var i = 0; i < 240 && host.Fractals.Any(f => f.Id == ahead.Id); i++) await Frames(1);
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.W, Keycode = Key.W, Pressed = false });
        await Frames(6);
        player.ReadKeyboard = reading;
        Check(!host.Fractals.Any(f => f.Id == ahead.Id) && host.Nature.FractalsPicked.Contains(ahead.Id) && host.Vitals.Fractals.Found == found + 1 && _hud.FractalsTouched == touched + 1,
            $"walking into a Fractal collects it: counted ({host.Vitals.Fractals.Found} found), gone from the island, through the player's item.pickup");
        Check(host.Fractals.Any(f => f.Id == beside.Id), "the Fractal 14 cm beside the path is not touched, and stays");
        // The hands key on a Fractal in reach (not touching): facing it and pressing F picks it up too.
        var near = beside.PositionM - side * 0.07f;
        Check(player.TryTeleportTo(near with { Y = player.GlobalPosition.Y }), "(the player steps up beside the second Fractal)");
        await Frames(6);
        var stillThere = host.Fractals.Any(f => f.Id == beside.Id);
        _hud._UnhandledInput(Press(Key.F));
        await Frames(2);
        Check(stillThere && !host.Fractals.Any(f => f.Id == beside.Id) && host.Vitals.Fractals.Found == found + 2 && _hud.NoticeText.Contains("picked up", StringComparison.Ordinal),
            "a Fractal within reach is not taken by walking near it, and the hands key picks it up: " + _hud.NoticeText);
        host.FractalsForTests = Array.Empty<IslandFractal>();
    }

    /// <summary>M opens and closes the Magic panel (it was only a button inside H), and the help names it.</summary>
    private async Task TestMagicKey()
    {
        Check(PlayerControls.Label(Act.HudMagic) == "M" && PlayerControls.Label(Act.GubbleSlot6) == "6", "the Magic panel's key is M and Fire's is 6, input actions in the map");
        _hud._UnhandledInput(Press(Key.M));
        await Frames(2);
        var opened = _hud.MagicPanelOpen && !_world.Player.InputEnabled;
        _hud._UnhandledInput(Press(Key.M));
        await Frames(2);
        Check(opened && !_hud.MagicPanelOpen && _world.Player.InputEnabled, "M opens the Magic panel, and M again closes it and gives the body back");
        Check(HelpLines().Any(line => line.Contains("M the Gubble's magic", StringComparison.Ordinal)) && HelpLines().Any(line => line.Contains("Walk into a fractal to collect it", StringComparison.Ordinal)),
            "the H help names the Magic key and how Fractals are collected");
    }

    /// <summary>Item 1: Fire on the wheel and key 6 (decision 77).</summary>
    private async Task TestFireWheel()
    {
        var host = CommandHost.Of(_world)!;
        host.ConfigureGlowProgress(new HudGlowStore(GlowProgress.Fresh));
        var names = GubbleMagic.Wedges.Select(w => w.Id).ToArray();
        Check(names.SequenceEqual(new[] { "glow", "fireworks", "fire", "stay", "grow", "build", "fetch", "come", "bubbles" }) &&
              GubbleMagic.Wedges[2].Slot == GubbleMagic.FireSlot && GubbleMagic.SlotCategories[GubbleMagic.FireSlot] == "fire",
            "nine wedges: Fire's just after Fireworks, the other seven in their clockwise order");
        Check(Enumerable.Range(0, 9).All(i => GubbleMagic.WedgeAt(GubbleMagic.WedgeDirection(i) * 80f, GubbleWheel.CentrePx) == i) &&
              GubbleMagic.WedgeAt(GubbleMagic.WedgeDirection(2).Rotated(0.3f) * 80f, GubbleWheel.CentrePx) == 2 && GubbleMagic.WedgeAt(new Vector2(3, 3), GubbleWheel.CentrePx) == null,
            "the wedge maths for nine: each ninth of the turn is its wedge, and the centre cancels");
        // Locked until the first rest: the wedge's mark and reason, and key 6 says so and sends nothing.
        _hud.AimRayForTests = Down(FloorNearGubble(.3f));
        var clicks = _hud.FireClicks;
        _hud._UnhandledInput(RightButton(true));
        await Frames(20);
        var label = _hud.Wheel.WedgeLabels[2].Text;
        var locked = _hud.Wheel.LockedWedges.SequenceEqual(new[] { 2 }) && label.EndsWith(GubbleWheel.WrapWords(GubbleMagic.FireLockedWords), StringComparison.Ordinal) &&
                     label.Contains('\n' + "campfire first.", StringComparison.Ordinal) && _hud.Wheel.WedgeLabels[2].Modulate.A < 1f && !_hud.Wheel.PriceLabels[2].Visible;
        _hud._UnhandledInput(Motion(GubbleMagic.WedgeDirection(2) * 90f));
        _hud._UnhandledInput(RightButton(false));
        var receipts = host.TransientReceiptCount(CommandHost.PlayerPrincipal);
        var revision = host.Revision;
        Check(locked && _hud.Cue.State == "shrug" && _hud.Cue.Reason == GubbleMagic.FireLockedWords && _hud.FireClicks == clicks,
            "before the first rest the Fire wedge shows a lock and \"Rest by the starting campfire first.\" in two lines, and no price; choosing it shrugs those words: " + label.Replace("\n", " "));
        _hud._UnhandledInput(Press(Key.Key6));
        _hud._UnhandledInput(Release(Key.Key6));
        Check(_hud.NoticeText == GubbleMagic.FireLockedWords && _hud.FireClicks == clicks && host.TransientReceiptCount(CommandHost.PlayerPrincipal) == receipts && host.Revision == revision,
            "key 6 says the same and sends no command");
        // Learned: a tap of 6 lights the fire at the aimed spot at once (decision 83), its outline flashing as it catches.
        host.LearnFireForTests();
        // Fair weather for the test, and the starting campfire lit (a long suite's rain may have put it out once Fire was learned): a fire
        // beside it is then plain Fire. Relit through the wheel's own path, since on a campfire that is out Fire lights it (the place decides).
        host.Nature.WeatherS = 0;
        if (host.IsCampfireOut(CampLaws.StartingId) && host.StartingCampfire is { } camp)
        {
            _hud.AimRayForTests = Down(camp.PositionM);
            _hud._UnhandledInput(Press(Key.Key6));
            _hud._UnhandledInput(Release(Key.Key6));
            await Frames(2);
            Check(_hud.LastFireKind == FireKindWords.Firepit && host.IsCampfireLit(CampLaws.StartingId), "(on the starting campfire that rain put out, the wheel's Fire relights it at once)");
        }
        var point = FloorNearGubble(.3f);
        var previousSample = host.NatureSampleForTests;
        host.NatureSampleForTests = (x, z) => new NatureCell { X = x, Z = z, Y = point.Y, Material = "soil", Fertile = true, Fuel = 1, Moisture = .2f, Light = .6f };
        var look = _world.GetNodeOrNull<NatureLook>("NatureLook");
        try
        {
            _hud.AimRayForTests = Down(point);
            var receiptsBefore = host.Authority.Call("receipt_count").AsInt32();
            _hud._UnhandledInput(Press(Key.Key6));
            _hud._UnhandledInput(Release(Key.Key6));
            await Frames(1);
            // The suite's earlier tests left builds in the room: in Creative the host's one ask comes as a card on the click (the fire rule).
            var asked = _hud.LastFireClick is { Held: true } ? host.PendingApprovals.LastOrDefault()?.RequestId : null;
            var heldWords = _hud.NoticeText;
            var answered = asked == null || host.Approve(asked)["ok"]?.GetValue<bool>() == true;
            Check(answered && host.Nature.Fire != null && _hud.LastFireClick is { Previewed: true } && _hud.LastFireKind == FireKindWords.Fire && !GetTree().Paused && !_hud.Wheel.Held,
                "once Fire is learned, a tap of 6 lights the fire where you point at once, through the host, with no hold" + (asked != null ? " (its builds' card answered yes)" : ""));
            Check(asked != null ? heldWords == RoomHud.FireHeldWords : _hud.NoticeText.StartsWith("The fire is lit.", StringComparison.Ordinal) && _hud.NoticeText.Contains("Snuff", StringComparison.Ordinal),
                "the notice says it is lit and that it burns until it burns out or is Snuffed (or, held, that the card asks first): " + (asked != null ? heldWords : _hud.NoticeText));
            Check(_hud.FireFlashing && look is { FirePreviewVisible: true, PreviewIsLine: false } && !_hud.AskTag.Text.Contains("hold", StringComparison.Ordinal),
                "the outline flashes as it lights, and the reticle has no hold to wait for");
            Check(host.Authority.Call("receipt_count").AsInt32() >= receiptsBefore + 1, "(one cast receipt: the preview costs nothing)");
            for (var i = 0; i < 120 && _hud.FireFlashing; i++) await Frames(1);
            Check(!_hud.FireFlashing && look is { FirePreviewVisible: false } && host.Nature.Fire != null, "the flash is over in a moment; the fire burns on");
            // Decision 82: X stops the Gubble, never a fire.
            _hud._UnhandledInput(Press(Key.X));
            await Frames(2);
            Check(host.Nature.Fire != null, "X stops the Gubble and leaves the fire burning (decision 82)");
            Check(host.PlayerNatureUndo()["ok"]?.GetValue<bool>() == true && host.Nature.Fire == null, "(Creative's undo takes it back)");
            // The wheel's Fire wedge lights at the frozen aim too.
            _hud.ChooseWedge(2, _hud.AimNow());
            var wedgeAsked = _hud.LastFireClick is { Held: true } ? host.PendingApprovals.LastOrDefault()?.RequestId : null;
            if (wedgeAsked != null) host.Approve(wedgeAsked);
            Check(host.Nature.Fire != null && _hud.LastFireKind == FireKindWords.Fire, "the wheel's Fire wedge lights at the frozen aim at once");
            Check(host.PlayerNatureUndo()["ok"]?.GetValue<bool>() == true && host.Nature.Fire == null, "(and undo takes it back)");
            await Frames(2);
            Check(HelpLines().Any(line => line.Contains("6 lights Fire where you point, at once", StringComparison.Ordinal)), "the help says how key 6 works");
        }
        finally
        {
            _hud.AimRayForTests = null;
            host.NatureSampleForTests = previousSample;
            host.ConfigureGlowProgress(new LocalGlowProgressStore(ProjectSettings.GlobalizePath(HudGlowProfile)));
        }
    }
}
