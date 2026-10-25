using Godot;
using System;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Ui;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The hotbar (docs/game/HOTBAR.md; MECHANICS.md decisions 119, 120 and 128) in the real rooms with the real HUD and host: its layout by numbers
/// at 1280x720 (the canvas) and 1152x648 (the window the founder plays in, which shows that canvas at nine tenths), the magic layer (the six
/// abilities in the wheel's order, a locked slot's padlock and lock words casting nothing, a key selecting within the frame, the wheel still
/// on the right button's hold), and in the inventory room the pockets' layer: Tab switching and Esc ignoring the bar, a unique thing in the hand
/// and back, F dropping it, a unit used from the bar falling by one (a click and E), a seed refused in the kernel's words, a stone set down and
/// picked up, the wheel over the bar and a click on it, and the body walking while anything is chosen.
/// </summary>
public partial class PlayHudTest
{
    /// <summary>A point on the screen well off the bar (the click there uses the armed unit).</summary>
    private static readonly Vector2 InvOffTheBar = new(640, 300);
    private static readonly Vector3 InvByPebble = new(0.7f, 0, 0.32f);

    /// <summary>The key of a bar slot (1 to 10: keys 1 to 9, then 0).</summary>
    private static Key InvBarKey(int slot) => slot == 10 ? Key.Key0 : Key.Key0 + slot;

    // ---- the layout, by numbers (the main room) ----

    private async Task TestHotbarLayout()
    {
        await Frames(4);
        var bar = _hud.Hotbar;
        var view = _hud.GetViewport().GetVisibleRect().Size;
        Check(bar.Visible && view == new Vector2(1280, 720), $"the bar shows along the bottom of the HUD's canvas ({view.X:0}x{view.Y:0})");
        foreach (var screen in new[] { new Vector2(1280, 720), new Vector2(1152, 648) })
        {
            var at = Hotbar.Layout(screen);
            var row = at.Row;
            var inside = new Rect2(Vector2.Zero, screen).Grow(-8);
            var apart = Enumerable.Range(0, Hotbar.Slots - 1).All(i => row[i].End.X + Hotbar.GapPx - 0.01f <= row[i + 1].Position.X && row[i].Size == row[i + 1].Size);
            var centred = Mathf.Abs((row[0].Position.X + row[^1].End.X) * 0.5f - screen.X * 0.5f) <= 1;
            Check(row.Length == 10 && at.Strip.Length == 10 && row.All(inside.Encloses) && apart && centred && screen.Y - row[0].End.Y >= Hotbar.BottomPx - 0.01f,
                $"at {screen.X:0}x{screen.Y:0}: ten slots of {row[0].Size.X:0}x{row[0].Size.Y:0} in a row centred on the bottom edge, {Hotbar.GapPx:0} apart, {screen.Y - row[0].End.Y:0} px up, from x {row[0].Position.X:0} to {row[^1].End.X:0}");
            Check(at.Strip.Select((s, i) => s.End.Y <= row[i].Position.Y - Hotbar.StripGapPx + 0.01f && Mathf.IsEqualApprox(s.Position.X, row[i].Position.X)).All(b => b) &&
                  at.Caption.End.Y <= at.Strip[0].Position.Y && at.Caption.Position.Y >= screen.Y * 0.75f && at.RowTag.End.X <= row[0].Position.X && at.RowTag.Position.X >= Hotbar.EdgePx &&
                  Mathf.IsEqualApprox(RoomHud.BarClearTop(screen), at.Caption.Position.Y - RoomHud.BarClearPx),
                $"the other layer's strip sits {Hotbar.StripGapPx:0} px over each slot, the caption over the strip (from y {at.Caption.Position.Y:0}), the layer's name to the left of the row");
        }
        // The window the founder plays in shows the canvas at nine tenths (canvas_items, kept aspect): the sizes there, in its pixels.
        var scale = 1152f / 1280f;
        var smallest = new[] { Hotbar.KeyFont, Hotbar.SmallFont, Hotbar.StripFont, Hotbar.CountFont, Hotbar.BigFont }.Min() * scale;
        Check(Hotbar.SlotWPx * scale >= 56 && Hotbar.SlotHPx * scale >= 50 && smallest >= 10.5f,
            $"at 1152x648 a slot is {Hotbar.SlotWPx * scale:0.0}x{Hotbar.SlotHPx * scale:0.0} px and its smallest words {smallest:0.0} px");
        // The live bar: its labels where the layout puts them, words that fit their slots, clear of everything else at the screen's foot.
        var placed = bar.Placed;
        var font = ThemeDB.FallbackFont;
        var keysFit = Enumerable.Range(0, 10).All(i => bar.KeyLabels[i].Text.Length == 1 && placed.Row[i].Encloses(bar.KeyLabels[i].GetGlobalRect()) && placed.Row[i].Encloses(bar.SmallLabels[i].GetGlobalRect()) &&
                                                      font.GetStringSize(bar.SmallLabels[i].Text, HorizontalAlignment.Left, -1, Hotbar.SmallFont).X <= bar.SmallLabels[i].Size.X);
        var stripFits = Enumerable.Range(0, 10).All(i => font.GetStringSize(bar.StripLabels[i].Text, HorizontalAlignment.Left, -1, Hotbar.StripFont).X <= bar.StripLabels[i].Size.X);
        Check(keysFit && stripFits, "each slot's key and name (\"Glow\", \"Fireworks\") fit inside it, and the strip's words fit theirs: " + string.Join(" ", bar.SmallLabels.Select(l => l.Text)));
        var footer = _hud.GetNode<PanelContainer>("HelpFooter").GetGlobalRect();
        var book = _hud.Book.Dock.GetGlobalRect();
        var eye = _hud.EyesIcon.GetGlobalRect();
        var meters = _hud.MeterPanel.GetGlobalRect();
        Check(!placed.Block.Intersects(footer) && !placed.RowTag.Intersects(footer) && !placed.Block.Intersects(book) && !placed.Block.Intersects(eye) && !placed.Block.Intersects(meters) &&
              footer.Size.X < 200 && footer.Position.X == 18,
            $"the bar ({placed.Block.Position.X:0},{placed.Block.Position.Y:0} {placed.Block.Size.X:0}x{placed.Block.Size.Y:0}) is clear of the folded help (now a small box: {footer.Position.X:0},{footer.Position.Y:0} {footer.Size.X:0}x{footer.Size.Y:0}), the journal's book ({book.Position.X:0}), the eye icon and the meters");
        // A notice standing low stays over the bar and its caption.
        var low = RoomHud.NoticePlace(new Vector2(640, 700), new Vector2(380, 44), view, 0, view.Y - RoomHud.BarClearTop(view));
        Check(low.Y + 44 <= RoomHud.BarClearTop(view) + 0.01f, $"a notice under a low reticle stands above the bar's caption (its foot at {low.Y + 44:0}, the caption from {placed.Caption.Position.Y:0})");
        // H unfolds the help over the foot of the screen: the bar steps aside under it, and is back when it folds.
        _hud._UnhandledInput(Press(Key.H));
        await Frames(2);
        var unfolded = _hud.GetNode<PanelContainer>("HelpFooter").GetGlobalRect();
        var hidden = !bar.Visible && unfolded.Size.X > 900;
        _hud._UnhandledInput(Press(Key.H));
        await Frames(2);
        Check(hidden && bar.Visible && _hud.GetNode<PanelContainer>("HelpFooter").GetGlobalRect().Size.X < 200,
            $"unfolded, the help is the screen's width ({unfolded.Size.X:0} px) and the bar steps aside under it; folded again the bar is back and the help a small box");
    }

    // ---- the magic layer (the main room) ----

    private async Task TestHotbarAbilities()
    {
        var host = CommandHost.Of(_world)!;
        var bar = _hud.Hotbar;
        host.ConfigureGlowProgress(new HudGlowStore(GlowProgress.Fresh));
        await Frames(8);
        if (_hud.BarLayerNow != HandSelection.AbilitiesLayer) _hud.SwitchBarLayer();
        await Frames(2);
        var names = bar.Row.Select(s => s.Small).ToArray();
        Check(bar.AbilitiesRow && _hud.BarLayerShown == HandSelection.AbilitiesLayer && names.Take(6).SequenceEqual(new[] { "Glow", "Grow", "Bubbles", "Fireworks", "Build", "Fire" }) &&
              bar.Row.Take(6).Select(s => s.Big).SequenceEqual(GubbleMagic.SlotIcons) && bar.Row.Skip(6).All(s => s.Lacking && s.Big == "·") &&
              bar.Row.Select(s => s.Key).SequenceEqual(new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" }) && bar.RowTag.Text == "Magic" && bar.StripTag.Text == "Tab: pockets",
            "the magic layer: the six abilities in the wheel's slot order with their icons, then the slots to come, on keys 1 to 0: " + string.Join(" ", names));
        Check(bar.Row[4].Lacking && bar.Row[4].Caption == "5 Build · not yet", "an ability this island lacks (Build) is dim and says \"not yet\": " + bar.Row[4].Caption);
        // Fire before the first rest: a padlock and the lock words on its slot; its key casts nothing and sends nothing.
        Check(!host.FireLearned && bar.Row[5] is { Locked: true, Lacking: false } && bar.Row[5].Caption == "6 Fire · locked: " + GubbleMagic.FireLockedWords,
            "Fire's slot carries the wheel's padlock and lock words while it is locked: " + bar.Row[5].Caption);
        var chosen = bar.RowChosen;
        var receipts = host.TransientReceiptCount(CommandHost.PlayerPrincipal);
        var revision = host.Revision;
        var clicks = _hud.FireClicks;
        _hud.AimRayForTests = Down(FloorNearGubble(.3f));
        _hud._UnhandledInput(Press(Key.Key6));
        _hud._UnhandledInput(Release(Key.Key6));
        await Frames(1);
        Check(_hud.FireClicks == clicks && host.TransientReceiptCount(CommandHost.PlayerPrincipal) == receipts && host.Revision == revision && host.Nature.Fire == null &&
              bar.RowChosen == chosen && _hud.BarCaption == bar.Row[5].Caption && _hud.NoticeText == GubbleMagic.FireLockedWords,
            "6 on the locked slot casts nothing and sends nothing, is not chosen, and the caption over the bar shows its lock words: " + _hud.BarCaption);
        // Grow works and has more to find: a padlock on a bright slot, and the host's words for the next unlock in its caption (as the wheel's note).
        Check(bar.Row[1] is { Locked: false, Lacking: false } && (GrowLocks.WedgeNote(host) is { } note ? bar.Row[1].Noted && bar.Row[1].Caption.EndsWith("still to find: " + note, StringComparison.Ordinal) : !bar.Row[1].Noted),
            "Grow's slot works and, while it has more to find, carries a padlock and the host's words for the next unlock: " + bar.Row[1].Caption);
        // A key chooses its slot within the frame and casts as it always did: 4 is Fireworks where you point.
        var spot = FloorNearGubble(.4f);
        _hud.AimRayForTests = Down(spot);
        _hud._UnhandledInput(Press(Key.Key4));
        Check(host.HandNow is { Layer: HandSelection.AbilitiesLayer, Abilities: 4 } && bar.RowChosen == 4 && host.ActiveEffectIds.Any(id => _world.Look.KindOf(id) == GarageGame.Native.Look.LookDirector.EffectKind.Fireworks),
            "4 chooses Fireworks' slot in the frame of the key and casts them where you point, as the key always did");
        Check(_world.Player.InputEnabled && Input.MouseMode != Input.MouseModeEnum.Captured && !GetTree().Paused, "the body is never stopped and the mouse never taken");
        // The body walks while an ability is chosen.
        var from = _world.Player.GlobalPosition;
        _world.Player.SetControlInput(new Vector2(0, 1));
        await Frames(30);
        _world.Player.SetControlInput(Vector2.Zero);
        Check(PlanarDistance(from, _world.Player.GlobalPosition) > 0.03f, $"the body walks with Fireworks chosen ({PlanarDistance(from, _world.Player.GlobalPosition) * 100:0.0} cm in half a second)");
        _hud._UnhandledInput(Press(Key.X));
        // 7 to 0 are the slots to come: a shrug, nothing sent, nothing chosen.
        receipts = host.TransientReceiptCount(CommandHost.PlayerPrincipal);
        _hud._UnhandledInput(Press(Key.Key8));
        Check(_hud.Cue.State == "shrug" && _hud.Cue.Reason == GubbleMagic.NotYet && host.TransientReceiptCount(CommandHost.PlayerPrincipal) == receipts && bar.RowChosen == 4 &&
              _hud.BarCaption == "8 · magic still to come", "8, a slot still to come, shrugs \"not yet\" and sends nothing");
        // The wheel is still on the right button's hold this round (decision 128), and its tap is still the smart ask.
        _hud._UnhandledInput(RightButton(true));
        await Frames(20);
        var wheel = _hud.Wheel.Open;
        _hud._UnhandledInput(RightButton(false));
        await Frames(2);
        Check(wheel && !_hud.Wheel.Open && !GetTree().Paused, "the right button's hold still opens the wheel");
        // Tab: the pockets down (this island's pack has them), the magic dim above with Fireworks still its choice; Tab again, the magic back.
        _hud._UnhandledInput(Press(Key.Tab));
        var pockets = _hud.BarLayerShown == HandSelection.PocketsLayer && !bar.AbilitiesRow && bar.StripChosen == 4 && bar.StripTag.Text == "Tab: magic" && bar.RowTag.Text == "Pockets";
        _hud._UnhandledInput(Press(Key.Key1));
        var pocketOne = host.HandNow is { Layer: HandSelection.PocketsLayer, Pockets: 1 } && host.ActiveEffectIds.Count == 0;
        _hud._UnhandledInput(Press(Key.Tab));
        Check(pockets && pocketOne && _hud.BarLayerShown == HandSelection.AbilitiesLayer && bar.RowChosen == 4 && host.HandNow is { Layer: HandSelection.AbilitiesLayer, Abilities: 4, Pockets: 1 },
            "Tab brings the pockets down (1 then chooses a pocket slot and casts nothing), Tab again the magic with its choice kept");
        // Esc ignores the bar: the menu opens at once, the bar as it was.
        _hud._UnhandledInput(Press(Key.Escape));
        Check(_hud.PauseMenuOpen && bar.RowChosen == 4 && host.HandNow is { Layer: HandSelection.AbilitiesLayer, Abilities: 4 }, "Esc has nothing of the bar's to let go: the menu opens, the bar untouched");
        _hud.ClosePauseMenu();
        await Frames(2);
        // The help names the bar, Tab and its keys; the dusk hint names Tab while the pockets are down.
        Check(HelpLines().Any(l => l.StartsWith("Bar: 1 to 0 choose a slot", StringComparison.Ordinal) && l.Contains("Tab: pockets or magic", StringComparison.Ordinal)),
            "the H help has a line for the bar: its keys, Tab, what a pocket slot does");
        Check(CampPages.MagicLines(host).Any(l => l.Text.StartsWith("On the bar at the bottom: Tab brings the magic side down, then each one's key casts it", StringComparison.Ordinal)),
            "the camp's Magic page, the keys list beside the Magic panel's button, says the keys are the bar's");
        _hud._UnhandledInput(Press(Key.Tab));
        await Frames(8);
        var dusk = _hud.DuskHintLabel.Text;
        _hud._UnhandledInput(Press(Key.Tab));
        await Frames(8);
        Check(dusk == "Tab, then 1 or right-click: Light!" && _hud.DuskHintLabel.Text == "1 or right-click: Light!", $"the dusk hint names Tab while the pockets are down: \"{dusk}\"");
        _hud.AimRayForTests = null;
    }

    // ---- the pockets' layer (the inventory room) ----

    /// <summary>Tab, a key within the frame, a unique thing in the hand and back, Esc ignoring the bar, F dropping it; the shipped ten slots after.</summary>
    private async Task InvBarThing()
    {
        var host = _ih;
        var hud = _ihud;
        var bar = hud.Hotbar;
        var player = CommandHost.PlayerAvatar;
        Check(PlayerControls.Label(Act.BarLayer) == "Tab" && InputMap.HasAction(Act.BarLayer) && PlayerControls.Specs.Any(spec => spec.Action == Act.BarLayer) && hud.FindChild("PocketsPanel", true, false) == null,
            "Tab is the bar's layer switch (an input action in the map and the list of controls), and the pockets panel it opened is gone");
        await Frames(8);
        Check(host.HandNow.IsNone && hud.BarLayerShown == HandSelection.AbilitiesLayer && bar.AbilitiesRow && bar.RowChosen == 1 && bar.Row[0] is { Big: "☀", Small: "Glow" } &&
              bar.StripSlots[0].Strip == "1 Brick" && bar.StripChosen == null,
            $"a hand never chosen: the magic is down with Glow chosen, and the pockets dim above it, the brick in their first slot ({bar.StripSlots[0].Strip})");
        var revision = host.Revision;
        await InvKey(Key.Tab);
        Check(hud.BarLayerShown == HandSelection.PocketsLayer && host.HandNow is { Layer: HandSelection.PocketsLayer, Abilities: 1, Pockets: null } && !bar.AbilitiesRow &&
              bar.Row[0] is { Big: "Brick", Small: "a thing", Lacking: false } && bar.Row[1].Lacking && bar.StripSlots[0].Strip == "1 ☀" && bar.StripChosen == 1 && bar.RowChosen == null && host.HeldBy(player) == null,
            "Tab brings the pockets down (the brick in slot 1, the rest empty), the magic dim above with Glow its choice, nothing chosen in the pockets yet");
        Check(_iw.Player.InputEnabled && Input.MouseMode != Input.MouseModeEnum.Captured && !hud.PauseMenuOpen && host.Revision == revision && bar.MouseFilter == Control.MouseFilterEnum.Ignore &&
              bar.FindChildren("*", "Control", true, false).OfType<Control>().All(c => c.MouseFilter == Control.MouseFilterEnum.Ignore),
            "the switch changes no room state, the body never waits, and no part of the bar takes the mouse");
        // Tab is also the engine's focus-next key: a help button holding the focus does not take it (the HUD lets the focus go first).
        await InvKey(Key.H);
        var follow = hud.FindChild("CompanionActions", true, false)!.GetChildren().OfType<Button>().First(b => b.Text == "Follow");
        follow.GrabFocus();
        Check(hud.GetViewport().GuiGetFocusOwner() == follow, "(a button of the help holds the keyboard focus)");
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.Tab, Keycode = Key.Tab, Pressed = true });
        await Frames(3);
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.Tab, Keycode = Key.Tab, Pressed = false });
        await Frames(2);
        Check(hud.BarLayerShown == HandSelection.AbilitiesLayer && hud.GetViewport().GuiGetFocusOwner() == null, "Tab sent to the engine switches the bar even with a clicked button holding the focus");
        // (The engine hands the key to every HUD in the tree: the suite's first room's switched too, and is put back.)
        if (_hud.BarLayerNow != HandSelection.AbilitiesLayer) _hud.SwitchBarLayer();
        await InvKey(Key.H);
        await InvKey(Key.Tab);
        // The shipped ten slots again: the brick stays in its slot.
        host.UseRules(InventoryHudPack());
        await Frames(2);
        // A key chooses its slot in the same frame: the brick, a unique thing, comes out of the pockets into the hand (the kernel's take).
        hud._UnhandledInput(Press(Key.Key1));
        Check(host.HandNow.Pockets == 1 && bar.RowChosen == 1 && host.HeldBy(player) == "obj:brick" && host.Hand is { Holds: "thing", Item: "obj:brick" } && bar.Row[0] is { Big: "Brick", Small: "in hand" },
            "1 chooses the first slot within the frame of the key: the brick is in the hand, and its slot says so");
        Check(hud.BarCaption == "1 Brick · in your hand: E sets it down, F drops it", "the caption over the bar says what it is and what E and F do: " + hud.BarCaption);
        await Frames(4);
        Check(hud.Interaction.Kind is RoomHud.InteractKind.SetDown or RoomHud.InteractKind.SetOn && hud.Interaction.Words.StartsWith("E: set Brick ", StringComparison.Ordinal),
            "E's prompt sets the bar's thing down; it does not put it back in the pockets: " + hud.Interaction.Words);
        var from = _iw.Player.GlobalPosition;
        _iw.Player.SetControlInput(new Vector2(0, 1));
        await Frames(30);
        _iw.Player.SetControlInput(Vector2.Zero);
        var walked = PlanarDistance(from, _iw.Player.GlobalPosition);
        Check(_iw.Player.InputEnabled && walked > 0.03f, $"the body walks while the brick is chosen and held ({walked * 100:0.0} cm in half a second)");
        // Another slot returns it to its own slot.
        hud._UnhandledInput(Press(Key.Key3));
        Check(host.HeldBy(player) == null && host.PocketEntries.SingleOrDefault() is { Item: "obj:brick", Slot: 1 } && host.HandNow.Pockets == 3 && host.Hand.Holds == "nothing" &&
              bar.RowChosen == 3 && bar.Row[0] is { Big: "Brick", Small: "a thing" } && bar.Row[2].Lacking,
            "3 chooses an empty slot: the brick goes back to its own slot and the hands are empty");
        hud._UnhandledInput(Press(Key.Key1));
        Check(host.HeldBy(player) == "obj:brick", "(1 again: the brick in the hand)");
        // Esc ignores the bar: with nothing going on it opens the menu, and the hand is as it was.
        await InvStand(InvAway);
        await InvKey(Key.Escape);
        Check(hud.PauseMenuOpen && host.HeldBy(player) == "obj:brick" && host.HandNow.Pockets == 1 && hud.BarLayerShown == HandSelection.PocketsLayer,
            "Esc ignores the bar: nothing on it is let go, the menu opens, and the brick is still in the hand on slot 1");
        hud.ClosePauseMenu();
        await Frames(2);
        // F drops it, as it drops anything held: out of the pockets onto the island; the slot is empty and still chosen.
        await InvKey(Key.F, 10);
        Check(host.HeldBy(player) == null && host.PocketEntries.Count == 0 && host.HandNow.Pockets == 1 && bar.Row[0].Lacking && host.Room.Objects.Any(o => o.Id == "obj:brick"),
            "F drops the brick: out of the pockets onto the island, its slot empty and still chosen: " + hud.NoticeText);
    }

    /// <summary>The armed units used from the bar: a click and E (food eaten, the count falling by one), a seed refused, a stone set down and picked up again.</summary>
    private async Task InvBarUnits()
    {
        var host = _ih;
        var hud = _ihud;
        var bar = hud.Hotbar;
        var prompt = hud.InteractPrompt;
        await Frames(8);
        var berrySlot = host.PocketEntries.First(e => e.Item == "berries").Slot;
        var appleSlot = host.PocketEntries.First(e => e.Item == "apple").Slot;
        Check(bar.Row[berrySlot - 1] is { Big: "Berries", Count: "1", Small: "eat" } && bar.Row[appleSlot - 1] is { Big: "Apples", Count: "2", Small: "eat" },
            $"what was picked is on the bar at once, each slot with its kind, its count and what a unit does ({bar.Row[berrySlot - 1].Big} ×{bar.Row[berrySlot - 1].Count}, {bar.Row[appleSlot - 1].Big} ×{bar.Row[appleSlot - 1].Count})");
        // Away from everything, E has nothing in the world to do: it uses the armed unit; a click uses it anywhere.
        await InvStand(InvAway);
        await InvKey(InvBarKey(appleSlot), 4);
        Check(host.Hand is { Holds: "unit", Item: "apple", Count: 2 } && hud.Interaction.Kind == RoomHud.InteractKind.UseUnit && hud.Interaction.Words == "E: eat an apple" && prompt.Visible &&
              hud.BarCaption == $"{appleSlot} Apples ×2 · E or a click eats one: nothing heals in Creative",
            $"the apples' slot chosen arms a unit; away from anything else E's prompt eats it, and the caption says so: \"{hud.Interaction.Words}\", \"{hud.BarCaption}\"");
        var sounds = 0;
        host.FoodSound += _ => sounds++;
        var seeds = InvUnits("seed");
        hud.PointerForTests = InvOffTheBar;
        hud._UnhandledInput(LeftButton(true));
        hud._UnhandledInput(LeftButton(false));
        await Frames(2);
        Check(InvUnits("apple") == 1 && bar.Row[appleSlot - 1] is { Count: "1", Big: "Apple" } && InvUnits("seed") == seeds + 1 && hud.NoticeText == "Crunch! You ate an apple." && sounds == 1 &&
              Input.MouseMode != Input.MouseModeEnum.Captured,
            "a click uses the armed unit: the apples' count falls by one, from 2 to 1 on the bar, leaving a seed (decisions 118 and 125; in Creative nothing heals): " + hud.NoticeText);
        await InvKey(Key.E);
        Check(InvUnits("apple") == 0 && InvUnits("seed") == seeds + 2 && sounds == 2 && hud.NoticeText == "Crunch! You ate an apple." && host.HandNow.Pockets == appleSlot,
            "E eats the other: the stack is used up, and its slot stays chosen: " + hud.NoticeText);
        Check(bar.Row[berrySlot - 1] is { Big: "Berries", Count: "1" } && hud.Interaction.Kind != RoomHud.InteractKind.UseUnit, "(the berries are still there; with nothing armed E has nothing of the bar's to do)");
        // A seed: armed, used, refused in the kernel's words; the count stays.
        var seedSlot = host.PocketEntries.First(e => e.Item == "seed").Slot;
        await InvKey(InvBarKey(seedSlot), 4);
        seeds = InvUnits("seed");
        Check(bar.Row[seedSlot - 1] is { Big: "Seeds" or "Seed", Small: "keep" } && hud.Interaction.Words == "E: use a seed", $"a seed's slot says it is kept, and E's prompt asks to use one: \"{hud.Interaction.Words}\"");
        hud._UnhandledInput(LeftButton(true));
        hud._UnhandledInput(LeftButton(false));
        await Frames(2);
        Check(InvUnits("seed") == seeds && hud.NoticeText == "Seeds have no use yet: planting comes with farming.", "a click on a seed is refused in the kernel's words and the count stays: " + hud.NoticeText);
        hud.PointerForTests = null;
        // A stone: gathered into the pockets (the kernel chooses its slot on the bar), set down with E, picked up again.
        Check(await InvCarry("obj:pebble", InvByPebble, InvAway), "the player carries the pebble (a stone) away from everything");
        Check(hud.Interaction.Kind == RoomHud.InteractKind.Gather, "E's prompt gathers it: " + hud.Interaction.Words);
        await InvKey(Key.E, 8);
        var stoneSlot = host.PocketEntries.FirstOrDefault(e => e.Item == "stone")?.Slot ?? 0;
        Check(stoneSlot > 0 && host.HandNow.Pockets == stoneSlot && bar.RowChosen == stoneSlot && bar.Row[stoneSlot - 1] is { Big: "Stone", Count: "1", Small: "set down" },
            "gathered, the stone joins its stack and its slot is chosen on the bar at once (decision 116): " + hud.NoticeText);
        await Frames(4);
        Check(hud.Interaction.Kind == RoomHud.InteractKind.UseUnit && hud.Interaction.Words == "E: set a stone down", "E's prompt sets one down: " + hud.Interaction.Words);
        await InvKey(Key.E, 4);
        Check(InvUnits("stone") == 0 && host.LooseUnits.Any(u => u.Kind == "stone") && hud.NoticeText == "You set a stone down.", "E sets the stone down, loose before the player: " + hud.NoticeText);
        await Frames(40);
        Check(hud.Interaction.Kind == RoomHud.InteractKind.PickFood && hud.Interaction.Words == "E: pick up a stone", "and E picks it up again: " + hud.Interaction.Words);
        await InvKey(Key.E, 4);
        Check(InvUnits("stone") == 1, "(the stone back in the pockets: " + hud.NoticeText + ")");
    }

    /// <summary>The wheel over the bar steps its selection (the zoom left alone), a click on a slot chooses it, a click on the strip brings that layer down.</summary>
    private async Task InvBarWheelAndClick()
    {
        var hud = _ihud;
        var host = _ih;
        var bar = hud.Hotbar;
        hud.SetViewMode(1);
        await Frames(8);
        if (hud.BarLayerShown != HandSelection.PocketsLayer) await InvKey(Key.Tab);
        await InvKey(Key.Key3);
        var zoom = hud.ShoulderDistanceM;
        hud.PointerForTests = bar.Placed.Row[4].GetCenter();
        hud._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.WheelDown, Pressed = true });
        var down = host.HandNow.Pockets;
        hud._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true });
        hud._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true });
        var up = host.HandNow.Pockets;
        Check(down == 4 && up == 2 && Mathf.IsEqualApprox(zoom, hud.ShoulderDistanceM) && bar.RowChosen == 2, $"the wheel over the bar steps the selection (3 to {down}, back to {up}) and zooms nothing");
        await Frames(2);
        Check(hud.BarCaption == bar.Row[4].Caption, "with the pointer over a slot, the caption is that slot's: " + hud.BarCaption);
        hud.PointerForTests = InvOffTheBar;
        hud._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.WheelDown, Pressed = true });
        Check(hud.ShoulderDistanceM > zoom + 0.001f, $"off the bar the wheel zooms the view as before ({zoom:0.00} to {hud.ShoulderDistanceM:0.00} m)");
        hud.ZoomShoulder(1000);
        // A click on a slot chooses it (and eats nothing); a click on the strip brings the magic down at that slot.
        hud.PointerForTests = bar.Placed.Row[6].GetCenter();
        var units = host.PocketEntries.Sum(e => e.Count);
        hud._UnhandledInput(LeftButton(true));
        hud._UnhandledInput(LeftButton(false));
        Check(host.HandNow.Pockets == 7 && bar.RowChosen == 7 && host.PocketEntries.Sum(e => e.Count) == units, "a click on slot 7 chooses it, and uses nothing");
        hud.PointerForTests = bar.Placed.Strip[3].GetCenter();
        hud._UnhandledInput(LeftButton(true));
        hud._UnhandledInput(LeftButton(false));
        Check(hud.BarLayerShown == HandSelection.AbilitiesLayer && host.HandNow is { Layer: HandSelection.AbilitiesLayer, Abilities: 4, Pockets: 7 } && bar.StripChosen == 7,
            "a click on the strip's fourth slot brings the magic down with Fireworks chosen; the pockets keep slot 7");
        hud.PointerForTests = null;
        // An ability's key never puts away what the hands carry: the brick picked up again on the magic side, 1 casts, the brick stays held.
        var player = CommandHost.PlayerAvatar;
        var brick = host.ObjectNodeOf("obj:brick")?.GlobalPosition ?? InvAway;
        Check(await InvCarry("obj:brick", new Vector3(brick.X, 0, brick.Z) + new Vector3(0, 0, 0.06f), InvAway), "(the player picks the brick up again, the magic side down)");
        var abilities = host.HandNow.Abilities;
        hud.AimRayForTests = Down(InvAway + new Vector3(0.05f, 0, 0.05f));
        await InvKey(Key.Key1);
        Check(host.HeldBy(player) == "obj:brick" && host.HandNow is { Layer: HandSelection.AbilitiesLayer } && host.HandNow.Abilities == abilities,
            "1 on the magic side with the brick in the hands: the brick stays held (the key records no selection while they carry something)");
        hud._UnhandledInput(Press(Key.X));
        hud.AimRayForTests = null;
        // Tab brings the pockets down through the kernel's select, which never touches what the hands carry (decision 129); a pocket slot
        // chosen then puts the carried brick into the pockets first (the kernel's rule for a choice on the pockets' layer).
        await InvKey(Key.Tab);
        Check(hud.BarLayerShown == HandSelection.PocketsLayer && host.HeldBy(player) == "obj:brick" && host.PocketEntries.All(e => e.Item != "obj:brick"),
            "Tab to the pockets keeps the carried brick in the hands (decision 129: switching the layers never touches what they carry)");
        await InvKey(InvBarKey(host.HandNow.Pockets ?? 1));
        Check(hud.BarLayerShown == HandSelection.PocketsLayer && host.HeldBy(player) == null && host.PocketEntries.Any(e => e.Item == "obj:brick"),
            "a pocket slot's key then puts the carried brick into the pockets first, the kernel's rule for a choice on the pockets' layer");
    }
}
