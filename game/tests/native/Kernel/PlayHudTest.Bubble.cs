using System;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;
using GarageGame.Native.Ui;
using Godot;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The Bubble by the smart ask (MECHANICS.md decisions 57, 58 and 60; RoomHud.Bubble.cs), in the real room with the real HUD, the
/// real look and the real host, headless:
/// - the first right-click on a bubble selects it (the kernel's BubbleAt picks it from the ray) and it is lit as a thing to carry is;
///   selecting sends nothing;
/// - the next right-click on a spot leads it there, one command (goal.set lead), the Gubble's thought bubble answering with the
///   host's result; before the click the reticle asks the host's preview: "lead it there", or for a spot higher than the bubble can
///   drift "lift it there" and its price in pips (dots, or "needs N"), as the wheel shows prices;
/// - a refusal shows its words; Esc, a right-click on nothing, on the bubble again or on the Gubble lets the bubble go; so does the
///   bubble popping and the lead ending.
/// </summary>
public partial class PlayHudTest
{
    /// <summary>Nothing sent: the player's transient receipts are how many commands the HUD made (a preview makes none).</summary>
    private static int Commands(CommandHost host) => host.TransientReceiptCount(CommandHost.PlayerPrincipal);

    private static (Vector3, Vector3) ThroughTheBubble(CommandHost host)
    {
        var centre = host.ActiveBubble!.Value.CenterM;
        return (centre + Vector3.Up * 0.3f, centre + Vector3.Down * 0.3f);
    }

    /// <summary>
    /// A bare floor spot of the test room's open middle (no object within half a metre: the table is far to the back, the box and the book
    /// to the right), by the ray the HUD aims with: where an empty bubble drifts freely, clear of the player at the room's front.
    /// </summary>
    private Vector3 OpenFloorAt(float x, float z)
    {
        _hud.AimRayForTests = Down(new Vector3(x, 0f, z));
        var aim = _hud.AimNow();
        Check(aim.Hit && aim.Entity == null && !aim.AtGubble && aim.Point.Y < 0.02f && CommandHost.Of(_world)!.Room.Bounds.HasPoint(aim.Point + Vector3.Up * 0.001f), $"(the open floor at {x:0.00}, {z:0.00} is bare: {aim.Point})");
        return aim.Point;
    }

    /// <summary>The bubble's lead has ended (it arrived, popped or was stopped): the host holds none for it any more.</summary>
    private async Task<int> UntilNoLead(CommandHost host, int frames = 1800)
    {
        var waited = 0;
        while (waited < frames && host.ActiveBubble is { LedToM: not null }) { await Frames(1); waited++; }
        return waited;
    }

    private void TestBubbleRules()
    {
        // The smart ask's rules for a bubble, alone (GubbleMagic.Decide): the first match wins, a bubble before everything else.
        var spot = new GubbleMagic.Aim(true, new Vector3(0.5f, 0, 0.5f), null, false);
        var onBubble = spot with { Bubble = "effect:b", InBubble = false };
        var inside = spot with { Bubble = "effect:b", InBubble = true };
        var none = new GubbleMagic.Aim(false, new Vector3(0.5f, 0, 0.5f), null, false);
        var gubble = new GubbleMagic.Aim(true, new Vector3(0.5f, 0, 0.5f), null, true);
        var thing = new GubbleMagic.Aim(true, new Vector3(0.5f, 0, 0.5f), "obj:crate", false);
        Func<string, bool> carryable = id => id == "obj:crate";
        Func<Vector3, bool> dark = _ => false;
        GubbleMagic.AskRule Rule(GubbleMagic.Aim aim, bool holds = false, string? selected = null) => GubbleMagic.Decide(aim, carryable, dark, holds, selected).Rule;
        Check(Rule(onBubble) == GubbleMagic.AskRule.SelectBubble && Rule(onBubble, holds: true) == GubbleMagic.AskRule.SelectBubble && Rule(inside) == GubbleMagic.AskRule.SelectBubble,
            "a right-click on a bubble selects it, before fetching or putting down anything, and from inside it too");
        Check(Rule(spot, selected: "effect:b") == GubbleMagic.AskRule.LeadBubble && Rule(thing, selected: "effect:b") == GubbleMagic.AskRule.LeadBubble &&
              Rule(spot, holds: true, selected: "effect:b") == GubbleMagic.AskRule.LeadBubble && Rule(inside, selected: "effect:b") == GubbleMagic.AskRule.LeadBubble,
            "with one selected, a right-click on a spot, or a thing's, or from inside the bubble leads it there, whatever else the aim would have asked");
        Check(Rule(none, selected: "effect:b") == GubbleMagic.AskRule.ClearBubble && Rule(onBubble, selected: "effect:b") == GubbleMagic.AskRule.ClearBubble &&
              Rule(gubble, selected: "effect:b") == GubbleMagic.AskRule.ClearBubble,
            "with one selected, a right-click on nothing, on the bubble again or on the Gubble lets it go");
        Check(Rule(spot) == GubbleMagic.AskRule.Look && Rule(thing) == GubbleMagic.AskRule.Fetch && Rule(gubble) == GubbleMagic.AskRule.Toggle && Rule(none) == GubbleMagic.AskRule.None,
            "with none selected the rules are what they were");
        Check(GubbleMagic.AskWords(GubbleMagic.AskRule.SelectBubble, false, "Glow") == "○ choose this bubble" && GubbleMagic.AskWords(GubbleMagic.AskRule.LeadBubble, false, "Glow") == "○ lead it there" &&
              GubbleMagic.AskWords(GubbleMagic.AskRule.ClearBubble, false, "Glow") == "○ let the bubble go",
            "and the reticle says so in icons and fixed words");
    }

    private async Task TestBubbleAsk()
    {
        var host = CommandHost.Of(_world)!;
        var companion = _world.Companion;
        var player = _world.Player;
        var look = _world.Look;
        TestBubbleRules();

        // A Challenge body with its eight pips, the Gubble beside the player, nothing running.
        FreshBodies(host);
        host.PlayerMode("challenge");
        _hud.LightForTests = _ => 0.9f;
        _hud.AimRayForTests = Air();
        host.PlayerGoal("follow");
        for (var i = 0; i < 600 && companion.GlobalPosition.DistanceTo(player.GlobalPosition) > 0.4f; i++) await Frames(1);
        host.PlayerGoal("stop");
        host.PlayerGoal("stay");
        await Frames(10);
        var notice = (Label)_hud.FindChild("Notice", true, false)!;

        // The bubble is cast at a bare spot (key 3, the Bubble's slot): one bubble to walk into, drawn, and nothing selected.
        var floor = OpenFloorAt(-0.35f, -0.05f);
        _hud.AimRayForTests = Down(floor);
        _hud._UnhandledInput(Press(Key.Key3));
        await Frames(6);
        var view = host.ActiveBubble;
        Check(view is { Kind: "walk_in", Selectable: true, HoldsPlayer: false, LedToM: null } && _hud.SelectedBubble == null && look.BubbleRoot(view.Value.Id) is { } &&
              look.BubbleStateOf(view.Value.Id) is { Form: BubbleForm.WalkIn, Holding: false, Led: false } && host.Vitals.Pips == 6,
            $"key 3 at a spot casts the one bubble, a walk-in, for two pips (6 of 8 left); the look draws it as one that waits and none is chosen: {view}");
        var id = view!.Value.Id;

        // The ray through the bubble picks it; one beside it, or stopped by the world first, does not; from inside it is on it.
        _hud.AimRayForTests = ThroughTheBubble(host);
        await Frames(16);
        Check(_hud.AskPreview.Rule == GubbleMagic.AskRule.SelectBubble && _hud.AskPreview.Aim.Bubble == id && _hud.AskTag.Visible && _hud.AskTag.Text == "right-click: ○ choose this bubble",
            $"aimed through the bubble the reticle says it would choose it: \"{_hud.AskTag.Text}\"");
        var aside = OpenFloorAt(-0.1f, -0.4f);
        _hud.AimRayForTests = Down(aside);
        await Frames(16);
        Check(_hud.AskPreview.Aim.Bubble == null && _hud.AskPreview.Rule != GubbleMagic.AskRule.SelectBubble && !_hud.AskTag.Text.Contains("bubble", StringComparison.Ordinal),
            $"aimed at the floor beside it the bubble is not picked: \"{_hud.AskTag.Text}\"");
        var c = view.Value.CenterM;
        Check(_hud.BubbleOnRay(view.Value, c + Vector3.Up * 0.3f, c + Vector3.Down * 0.3f, 10f, out var isInside) == id && !isInside &&
              _hud.BubbleOnRay(view.Value, c + Vector3.Up * 0.3f, c + Vector3.Down * 0.3f, 0.1f, out _) == null &&
              _hud.BubbleOnRay(view.Value, c + Vector3.Up * 0.3f, c + Vector3.Down * 0.3f, 0.21f, out _) == id &&
              _hud.BubbleOnRay(view.Value, c + Vector3.Up * 0.3f, c + Vector3.Up * 0.6f, 10f, out _) == null &&
              _hud.BubbleOnRay(view.Value, c + new Vector3(0.2f, 0.3f, 0), c + new Vector3(0.2f, -0.3f, 0), 10f, out _) == null &&
              _hud.BubbleOnRay(view.Value, c, c + Vector3.Up, 10f, out isInside) == id && isInside,
            "the ray test: through it picks it, a world hit before the bubble hides it, one behind the eye or beside it misses, and from its middle the view is inside it");

        // The first right-click selects it. Nothing is sent. It is lit as a thing that can be carried is.
        _hud.AimRayForTests = ThroughTheBubble(host);
        await Frames(16);
        var sent = Commands(host);
        var pips = host.Vitals.Pips;
        Tap();
        await Frames(2);
        Check(_hud.SelectedBubble == id && look.SelectedBubble == id && look.BubbleHalo(id) is { Visible: true } && look.BubbleFilm(id)!.GetShaderParameter("tint_color").AsColor().IsEqualApprox(FocusLook.Default.RimColor) &&
              Commands(host) == sent && host.Vitals.Pips == pips && notice.Text.StartsWith("Bubble chosen", StringComparison.Ordinal),
            $"the first right-click on the bubble selects it: the look lights it with the focus highlight, nothing is sent and nothing is spent, the notice says what to do next: \"{notice.Text}\"");
        _hud.AimRayForTests = ThroughTheBubble(host);
        await Frames(16);
        Check(_hud.AskPreview.Rule == GubbleMagic.AskRule.ClearBubble && _hud.AskTag.Text == "right-click: ○ let the bubble go",
            "and aimed at the bubble again the reticle says a right-click would let it go: " + _hud.AskTag.Text);

        // The reticle asks the host before the click: a lead on the floor, a lift to the box top with its price, and "needs N" with a poorer pool.
        var there = OpenFloorAt(-0.85f, -0.05f);
        _hud.AimRayForTests = Down(there);
        await Frames(16);
        Check(_hud.AskPreview.Rule == GubbleMagic.AskRule.LeadBubble && _hud.AskTag.Text == "right-click: ○ lead it there" && _hud.LeadPreview is { Ok: true, Lift: false } && Commands(host) == sent && _hud.LeadPreviews >= 1,
            $"aimed at a spot on the floor the reticle says it would lead it there, from the host's preview (which sends nothing): \"{_hud.AskTag.Text}\"");
        var previews = _hud.LeadPreviews;
        await Frames(12);
        Check(_hud.LeadPreviews <= previews + 1, $"the same spot is not asked again at every refresh ({_hud.LeadPreviews - previews} more previews in 12 frames)");
        // A hand that shakes within a centimetre costs the host next to nothing; one that sweeps across the floor, five asks a second at most.
        previews = _hud.LeadPreviews;
        for (var i = 0; i < 60; i++)
        {
            _hud.AimRayForTests = Down(there + new Vector3(0.002f * Mathf.Sin(i), 0, 0.002f * Mathf.Cos(i)));
            await Frames(1);
        }
        var shaking = _hud.LeadPreviews - previews;
        previews = _hud.LeadPreviews;
        for (var i = 0; i < 60; i++)
        {
            _hud.AimRayForTests = Down(there + new Vector3(0.04f * (i % 12) - 0.2f, 0, 0));
            await Frames(1);
        }
        var sweeping = _hud.LeadPreviews - previews;
        Check(shaking <= 3 && sweeping <= 6 && sweeping >= 3, $"a shaking aim asks the host {shaking} times in a second and a sweeping one {sweeping} (at most five a second)");
        _hud.AimRayForTests = Down(there);
        await Frames(16);
        var boxTop = new Vector3(1.1f, 0.3f, 0.2f);
        _hud.AimRayForTests = Down(boxTop);
        await Frames(16);
        Check(_hud.AskPreview.Rule == GubbleMagic.AskRule.LeadBubble && _hud.AskTag.Text == $"right-click: ○ lift it there {Dots(6)}" && _hud.LeadPreview is { Ok: true, Lift: true, Price: 6 } && Commands(host) == sent,
            $"aimed at a spot higher than the bubble can drift (the box top) it says it is a lift and its price, six dots, before the click: \"{_hud.AskTag.Text}\"");
        host.ConfigurePlayerVitals(PoolOf(2));
        host.PlayerMode("challenge");
        await Frames(16);
        Check(host.Vitals.Pips == 2 && _hud.AskTag.Text == "right-click: ○ lift it there needs 6" && _hud.LeadPreview is { Ok: false, Lift: true, Price: 6 },
            $"with two pips the lift's price reads \"needs 6\", as the wheel's does, and the lead on the floor still reads \"lead it there\": \"{_hud.AskTag.Text}\"");
        _hud.AimRayForTests = Down(there);
        await Frames(16);
        Check(_hud.AskTag.Text == "right-click: ○ lead it there", "(the floor lead is free)");

        // A refusal shows its words: the lift the pool cannot pay, and a spot outside the island. The bubble stays chosen.
        _hud.AimRayForTests = Down(boxTop);
        await Frames(16);
        sent = Commands(host);
        Tap();
        await Frames(3);
        Check(_hud.Cue.State == "refused" && _hud.Cue.Reason == "The Gubble needs a moment" && _hud.Cue.Icon == GubbleMagic.BubbleIcon && notice.Text == CommandHost.EnergyExhaustedMessage &&
              _hud.SelectedBubble == id && host.ActiveBubble is { LedToM: null } && host.Vitals.Pips == 2 && _hud.PipsRow.FlashLeftS > 0,
            $"a lift the pool cannot pay is refused with the host's words, the Gubble shakes its head and the pips blink; the bubble stays chosen and nothing moves: \"{_hud.Cue.Reason}\" / \"{notice.Text}\"");
        var farAway = new Vector3(40f, 0.1f, 40f);
        _hud.Ask(new GubbleMagic.Ask(GubbleMagic.AskRule.LeadBubble, new GubbleMagic.Aim(true, farAway, null, false)));
        await Frames(3);
        Check(_hud.Cue.State == "refused" && _hud.Cue.Reason == "That spot is outside the island" && _hud.SelectedBubble == id && host.ActiveBubble is { LedToM: null },
            $"a spot outside the island is refused with its own words, and the bubble stays chosen: \"{_hud.Cue.Reason}\"");
        await Frames(160);

        // The second right-click leads it there: one command, the Gubble thinking until the lead ends, the bubble led, drawn with its string.
        host.ConfigurePlayerVitals(new MeterStore());
        host.PlayerMode("challenge");
        await Frames(4);
        host.PlayerGoal("stay");
        _hud.AimRayForTests = Down(there);
        await Frames(16);
        sent = Commands(host);
        pips = host.Vitals.Pips;
        Tap();
        await Frames(3);
        var led = host.ActiveBubble;
        Check(Commands(host) == sent + 1 && led is { LedToM: { } to } && PlanarDistance(to, there) < 0.01f && _hud.Cue.State == "thinking" && _hud.Cue.Icon == GubbleMagic.BubbleIcon &&
              _hud.SelectedBubble == id && host.Vitals.Pips == pips,
            $"the second right-click leads it to the spot: one command (goal.set lead), the Gubble thinking with the bubble's icon, the lead free of pips: {led}");
        await Frames(4);
        Check(look.BubbleStateOf(id) is { Led: true, LedLift: false } state && PlanarDistance(state.LedTo, there) < 0.01f && look.BubbleString(id) is { Visible: true } && look.BubbleMark(id) is { Visible: true } &&
              look.BubbleColumn(id) is not { Visible: true },
            "the look draws it led: a string from the Gubble and a ring at the spot, and no column for a spot that is no lift");
        var waited = await UntilNoLead(host);
        await Frames(4);
        Check(waited < 1800 && PlanarDistance(host.ActiveBubble!.Value.CenterM, there) < 0.06f && _hud.Cue.State == "done" && _hud.Cue.Icon == GubbleMagic.BubbleIcon,
            $"the bubble arrives ({waited / 60.0:0.0} s) and the Gubble's bubble says done: {_hud.Cue.Bubble.Text} {_hud.Cue.Reason}; the bubble is {(host.ActiveBubble is { } now ? PlanarDistance(now.CenterM, there).ToString("0.000") + " m from the spot" : "gone")}");
        Check(_hud.SelectedBubble == null && look.SelectedBubble == null && look.BubbleHalo(id) is not { Visible: true } && look.BubbleString(id) is { Visible: false },
            "the lead ended, so the choice is over: the highlight goes, and so does the string");

        // A lift: select again, the box top, the price charged when it starts to rise (the Gubble's walk and the bubble's drift to the box take a while).
        _hud.AimRayForTests = ThroughTheBubble(host);
        await Frames(16);
        Tap();
        await Frames(2);
        Check(_hud.SelectedBubble == id, "chosen again");
        _hud.AimRayForTests = Down(boxTop);
        await Frames(16);
        pips = host.Vitals.Pips;
        sent = Commands(host);
        Check(_hud.AskTag.Text == $"right-click: ○ lift it there {Dots(6)}", "the lift reads as six dots again with a full pool: " + _hud.AskTag.Text);
        Tap();
        await Frames(3);
        Check(Commands(host) == sent + 1 && host.Vitals.Pips == pips && look.BubbleStateOf(id) is { Led: true, LedLift: true } && look.BubbleColumn(id) is { Visible: true },
            "the lift is one command and costs nothing yet; the look draws the column over the spot");
        var rose = false;
        var chargedAt = -1;
        for (var i = 0; i < 2400 && host.ActiveBubble is { LedToM: not null }; i++)
        {
            await Frames(1);
            if (host.Vitals.Pips < pips && chargedAt < 0) chargedAt = i;
            rose |= host.ActiveBubble is { Lifting: true } && look.BubbleStateOf(id) is { Lifting: true };
        }
        await Frames(4);
        Check(rose && chargedAt > 0 && host.Vitals.Pips == pips - 6 && _hud.Cue.State == "done" && host.ActiveBubble!.Value.CenterM.Y > boxTop.Y,
            $"it rises on the lift, which is drawn rising, the price charged then ({chargedAt / 60.0:0.0} s in, {pips} to {host.Vitals.Pips} pips), and it arrives above the box ({host.ActiveBubble?.CenterM.Y:0.00} m)");
        Check(look.BubbleLiftShown(id) < 0.5f || host.ActiveBubble is { Lifting: false }, "(the stretch eases away once it is up)");

        // Letting go: nothing, the bubble again, the Gubble, Esc; each leaves the bubble where it is and sends nothing.
        FreshBodies(host);
        host.PlayerMode("challenge");
        await Frames(4);
        foreach (var way in new[] { "nothing", "the bubble again", "the Gubble", "Esc" })
        {
            _hud.AimRayForTests = ThroughTheBubble(host);
            await Frames(16);
            Tap();
            await Frames(2);
            Check(_hud.SelectedBubble == id, $"({way}) chosen");
            sent = Commands(host);
            var intent = companion.CurrentIntent;
            switch (way)
            {
                case "nothing": _hud.AimRayForTests = Air(); await Frames(16); Tap(); break;
                case "the bubble again": _hud.AimRayForTests = ThroughTheBubble(host); await Frames(16); Tap(); break;
                case "the Gubble": _hud.AimRayForTests = AtGubble(); await Frames(16); Tap(); break;
                default: _hud._UnhandledInput(Press(Key.Escape)); break;
            }
            await Frames(2);
            Check(_hud.SelectedBubble == null && look.SelectedBubble == null && look.BubbleHalo(id) is not { Visible: true } && Commands(host) == sent && host.ActiveBubble is { LedToM: null } &&
                  companion.CurrentIntent == intent && _hud.Cue.State != "refused" && notice.Text == RoomHud.BubbleLetGoWords,
                $"a right-click on {(way == "Esc" ? "nothing but Esc" : way)} lets the bubble go: it stays where it is, nothing is sent, the Gubble is not asked anything");
        }

        // A bubble that pops takes the choice with it; so does a recast (one bubble at a time).
        _hud.AimRayForTests = ThroughTheBubble(host);
        await Frames(16);
        Tap();
        await Frames(2);
        _hud.AimRayForTests = Down(floor);
        _hud._UnhandledInput(Press(Key.Key3));
        // (The Gubble may have to come closer first.)
        for (var i = 0; i < 900 && host.ActiveBubble is { } old && old.Id == id; i++) await Frames(1);
        await Frames(4);
        var recast = host.ActiveBubble;
        Check(recast is { } && recast.Value.Id != id && _hud.SelectedBubble == null && look.SelectedBubble == null && look.BubbleHalo(id) == null && look.BubbleRoot(id) == null,
            "a recast pops the chosen bubble and the choice goes with it; the new one is drawn and none is chosen");
        _hud.AimRayForTests = ThroughTheBubble(host);
        await Frames(16);
        Tap();
        await Frames(2);
        Check(_hud.SelectedBubble == recast!.Value.Id, "the new bubble is chosen");
        _hud.StopGubble();
        await Frames(4);
        Check(host.ActiveBubble == null && _hud.SelectedBubble == null && look.SelectedBubble == null && look.BubbleDrawCount == 0,
            "X pops the bubble, and the choice and its highlight go with it");

        // A second right-click while it is led re-aims it (the host's rule); the Gubble's bubble answers the last lead only.
        host.PlayerGoal("stay");
        _hud.AimRayForTests = Down(floor);
        _hud._UnhandledInput(Press(Key.Key3));
        for (var i = 0; i < 900 && host.ActiveBubble == null; i++) await Frames(1);
        await Frames(6);
        var third = host.ActiveBubble!.Value;
        _hud.AimRayForTests = ThroughTheBubble(host);
        await Frames(16);
        Tap();
        var first = OpenFloorAt(-0.9f, -0.05f);
        var second = OpenFloorAt(-0.6f, -0.4f);
        _hud.AimRayForTests = Down(first);
        await Frames(16);
        Tap();
        await Frames(30);
        _hud.AimRayForTests = Down(second);
        await Frames(16);
        Tap();
        await Frames(4);
        Check(_hud.SelectedBubble == third.Id && host.ActiveBubble is { LedToM: { } reaimed } && PlanarDistance(reaimed, second) < 0.01f,
            "while it is led it stays chosen: a right-click on another spot re-aims the lead");
        _hud.StopGubble();
        await Frames(4);
        // Back as the suite expects it: Creative, a full pool, the Gubble waiting beside the player.
        _hud.AimRayForTests = Air();
        FreshBodies(host);
        host.PlayerMode("creative");
        host.PlayerGoal("follow");
        for (var i = 0; i < 900 && companion.GlobalPosition.DistanceTo(player.GlobalPosition) > 0.4f; i++) await Frames(1);
        host.PlayerGoal("stop");
        host.PlayerGoal("stay");
        await Frames(10);
    }

    /// <summary>A Challenge body with this many pips in its pool.</summary>
    private static MeterStore PoolOf(int pips) =>
        new(PlayerModesProfile.Fresh.Select("challenge").WithCurrent(PlayerVitals.Of(100f, 100f, 0.4f, 0.4f, Array.Empty<string>(), 0, PlayerFractals.None, pips)));
}
