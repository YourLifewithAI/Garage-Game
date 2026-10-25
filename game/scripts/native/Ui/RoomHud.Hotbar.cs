using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using GarageGame.Native.Kernel;
using GarageGame.Native.Ui;

namespace GarageGame.Native;

/// <summary>
/// The hotbar (docs/game/HOTBAR.md; MECHANICS.md decisions 119, 120 and 128): the pockets and the Gubble's magic on one bar along the bottom,
/// ten slots on keys 1 to 0, in two layers. Tab (the pockets panel's old key, which the bar retired) brings the other layer down; the one not
/// in the hand stays above as a dimmed strip with its own selection, its keys idle. The mouse wheel over the bar steps the selection, a click
/// on a slot chooses it. Nothing here stops the body, takes the mouse or answers Esc.
/// <list type="bullet">
/// <item>The pockets' layer is the hand (inventory.select, player.status.hand, the kernel's): a slot chosen arms its stack's unit, which E (when
/// nothing in the world wants E) or a click uses (food eaten, a log or stone set down, a seed refused in the kernel's words), or takes its
/// unique thing into the hand (E sets it down, or at a camp stores it; F drops it); choosing another slot returns it.</item>
/// <item>The magic layer: the six abilities in the wheel's order, then the slots to come. Each key does what it did before the bar (a tap
/// casts at the aim; 2's sweep is the bridge, 6's tap lights Fire at once; 3, 4 and 5 cast on the press, as they and the wheel's wedges always
/// did), and the slot it casts from becomes the layer's selection (recorded through inventory.select while the hands are empty, so a key
/// never puts away what they carry). A locked slot shows its padlock and its lock words and casts nothing; the right button's hold still
/// opens the wheel for this round (decision 128), and its tap is still the smart ask.</item>
/// </list>
/// A hand never chosen in this mode (the host's HandSelection.None) shows the magic layer down with Glow chosen, so the keys 1 to 6 cast as
/// they did; the host records it the first time the player touches the bar. Every number and name is the host's; the HUD decides nothing.
/// </summary>
public partial class RoomHud
{
    /// <summary>
    /// How often the bar is read again from the host with nothing said (a knockout's loss, a key remapped, the pool's prices): it is read at once
    /// after every key, click or wheel step of the bar's own, and the frame after any command the host handles (a pick, a gather, E, F, a camp's
    /// button, the Gubble's own), so it is never rebuilt while nothing changes.
    /// </summary>
    public const double BarRefreshS = 0.5;
    /// <summary>How long the caption over the bar stands after a slot is chosen or pressed (the pointer over the bar shows it as long as it stays).</summary>
    public const double BarCaptionS = 2.5;
    public const string PocketsLayerName = "Pockets", MagicLayerName = "Magic";

    private Hotbar _hotbar = null!;
    private double _barAge = double.MaxValue;
    private bool _barDirty = true;
    private CommandHost? _barListening;
    private double _captionAge = double.MaxValue;
    private (int Index, bool Abilities) _captionFor;
    private string _barLayerShown = HandSelection.AbilitiesLayer;
    private readonly HotbarSlot[] _abilitySlots = new HotbarSlot[Hotbar.Slots], _pocketSlots = new HotbarSlot[Hotbar.Slots];
    private static readonly IReadOnlyList<InventoryEntryView> NoEntries = Array.Empty<InventoryEntryView>();

    /// <summary>The bar (tests read what it shows: its slots, its places, its caption).</summary>
    public Hotbar Hotbar => _hotbar;
    /// <summary>The layer the bar shows in the hand now, as last read from the host: pockets or abilities.</summary>
    public string BarLayerShown => _barLayerShown;
    /// <summary>The caption over the bar now ("" when none shows).</summary>
    public string BarCaption => _hotbar.CaptionCard.Visible ? _hotbar.Caption.Text : "";

    /// <summary>Test seam: where the pointer is (headless has none to move) for the wheel over the bar, its captions and a click's spot.</summary>
    internal Vector2? PointerForTests { get; set; }

    /// <summary>The pointer on the screen: the cursor, which is free in every view (or the suite's).</summary>
    private Vector2 Pointer() => PointerForTests ?? GetViewport().GetMousePosition();

    /// <summary>The layer in the hand now: the host's; a hand never chosen in this mode is the magic layer's.</summary>
    public string BarLayerNow => Host?.HandNow is { IsNone: false } hand ? hand.Layer : HandSelection.AbilitiesLayer;

    private static bool IsPockets(string layer) => layer == HandSelection.PocketsLayer;

    private void BuildHotbar()
    {
        _hotbar = new Hotbar();
        AddChild(_hotbar);
    }

    /// <summary>
    /// Each frame: the bar shows unless the journal, the Gubble's eyes, the intro or the unfolded key help fill the screen; it is read from the
    /// host at BarRefreshS (the pockets change under it: a pick, a gather, a knockout's loss), and its caption follows the pointer.
    /// </summary>
    private void TickHotbar(double delta)
    {
        var shown = !IntroPlaying && !JournalOpen && !GubbleViewOpen && !_keyHelp.Visible;
        if (_hotbar.Visible != shown) _hotbar.Visible = shown;
        if (!shown) return;
        _hotbar.Place(GetViewport().GetVisibleRect().Size);
        var host = Host;
        if (!ReferenceEquals(host, _barListening))
        {
            StopListeningForTheBar();
            _barListening = host;
            if (host != null) host.Handled += OnHostHandled;
            _barDirty = true;
        }
        _barAge += delta;
        _captionAge += delta;
        if (_barDirty || _barAge >= BarRefreshS) RefreshHotbar();
        UpdateBarCaption();
    }

    /// <summary>Any command the host handled may have changed the pockets or the hand: the bar is read again next frame.</summary>
    private void OnHostHandled(CommandHandled handled) => _barDirty = true;

    /// <summary>The bar stops listening to its host (another host took its place, or the HUD leaves the tree).</summary>
    private void StopListeningForTheBar()
    {
        if (_barListening != null && IsInstanceValid(_barListening)) _barListening.Handled -= OnHostHandled;
        _barListening = null;
    }

    /// <summary>The bar from the host now: both layers' slots, the layer in the hand and each layer's selection.</summary>
    private void RefreshHotbar()
    {
        _barAge = 0;
        _barDirty = false;
        if (_hotbar == null) return;
        var host = Host;
        var hand = host?.HandNow ?? HandSelection.None;
        var abilities = hand.IsNone || hand.Layer == HandSelection.AbilitiesLayer;
        for (var i = 0; i < Hotbar.Slots; i++) _abilitySlots[i] = AbilitySlot(host, i);
        var inventory = host is { HasInventory: true };
        var entries = inventory ? host!.PocketEntries : NoEntries;
        var view = inventory ? host!.Hand : null;
        for (var i = 0; i < Hotbar.Slots; i++) _pocketSlots[i] = PocketSlot(host, entries, view, i);
        var abilityChosen = hand.IsNone ? 1 : hand.Abilities ?? 1;
        var layer = abilities ? HandSelection.AbilitiesLayer : HandSelection.PocketsLayer;
        if (layer != _barLayerShown) _barLayerShown = layer;
        var tab = K(Act.BarLayer);
        if (abilities) _hotbar.Show(true, _abilitySlots, abilityChosen, _pocketSlots, hand.Pockets, MagicLayerName, $"{tab}: {PocketsLayerName.ToLowerInvariant()}");
        else _hotbar.Show(false, _pocketSlots, hand.Pockets, _abilitySlots, abilityChosen, PocketsLayerName, $"{tab}: {MagicLayerName.ToLowerInvariant()}");
    }

    /// <summary>A slot of the magic layer: the ability in its category (its icon and the island's name for it), locked, lacking, or still to come.</summary>
    private HotbarSlot AbilitySlot(CommandHost? host, int index)
    {
        var key = K(Act.BarSlots[index]);
        if (index >= GubbleMagic.SlotCategories.Length)
            return new HotbarSlot(key, "·", "", "", false, true, false, $"{key} · magic still to come", key);
        var icon = GubbleMagic.SlotIcons[index];
        var name = SlotName(index);
        var strip = $"{key} {icon}";
        if (SlotAbility(index) == null) return new HotbarSlot(key, icon, name, "", false, true, false, $"{key} {name} · {GubbleMagic.NotYet}", strip);
        if (SlotLockedWords(index) is { } locked) return new HotbarSlot(key, icon, name, "", true, false, false, $"{key} {name} · locked: {locked}", strip);
        // Grow works and has more still to find (GrowLocks: the host's own words for the next unlock): a padlock on a bright slot, as on the wheel.
        var note = index == GubbleMagic.GrowSlot && host != null ? GrowLocks.WedgeNote(host) : null;
        var caption = $"{key} {name}{SlotPriceSuffix(index)} · {CastWords(index, key)}" + (note != null ? " · still to find: " + note : "");
        return new HotbarSlot(key, icon, name, "", false, false, note != null, caption, strip);
    }

    /// <summary>How an ability's key casts it, in the caption: where you point, Grow's sweep once the bridge is open, Fire at once and its Fireline.</summary>
    private string CastWords(int index, string key)
    {
        var host = Host;
        if (index == GubbleMagic.FireSlot)
            return $"{key} lights it where you point, at once" + (host?.FirelineLearned == true ? $"; hold {key} and sweep for a Fireline" : "");
        if (index == GubbleMagic.GrowSlot && host?.BridgeUnlocked == true) return $"{key} casts it where you point; hold {key} and sweep from lip to lip for a bridge";
        return $"{key} casts it where you point";
    }

    /// <summary>
    /// A slot of the pockets' layer (1-based slot index+1): its entry (a stack: its kind, its count and what a unit does; a unique thing), the
    /// thing taken from it into the hand (its slot is kept for it), or empty. The names are the host's (untrusted display text).
    /// </summary>
    private HotbarSlot PocketSlot(CommandHost? host, IReadOnlyList<InventoryEntryView> entries, HandView? hand, int index)
    {
        var slot = index + 1;
        var key = K(Act.BarSlots[index]);
        InventoryEntryView? entry = null;
        for (var i = 0; i < entries.Count; i++)
            if (entries[i].Slot == slot) { entry = entries[i]; break; }
        if (entry == null)
        {
            if (host != null && hand is { Holds: "thing", PocketsSlot: { } held } && held == slot)
            {
                var name = KernelJson.DisplayText(host.HeldName(CommandHost.PlayerAvatar), 40);
                return new HotbarSlot(key, name, "in hand", "", false, false, false, $"{key} {name} · in your hand: {InventoryPages.HandThingWords(host)}", $"{key} {name}");
            }
            return new HotbarSlot(key, "", "", "", false, true, false, $"{key} · an empty slot", key);
        }
        if (entry.Kind == "stack")
        {
            var count = entry.Count.ToString(CultureInfo.InvariantCulture);
            return new HotbarSlot(key, entry.Name, InventoryPages.UnitDoes(entry), count, false, false, false,
                $"{key} {entry.Name} ×{count} · {InventoryPages.UnitCaption(host!, entry, K(Act.Interact), K(Act.LookDrag))}", $"{key} {entry.Name}");
        }
        var away = !entry.Here;
        return new HotbarSlot(key, entry.Name, away ? "from away" : "a thing", "", false, false, false,
            $"{key} {entry.Name} · " + (away ? "from another island: it comes out there" : "choose it to take it in your hand"), $"{key} {entry.Name}");
    }

    // ---- the caption ----

    /// <summary>The caption shows for this slot now (a key, a click or a wheel step on it), for BarCaptionS.</summary>
    private void ShowBarCaption(int index, bool abilities)
    {
        _captionFor = (index, abilities);
        _captionAge = 0;
        UpdateBarCaption();
    }

    /// <summary>Each frame: the slot under the pointer (row or strip), else the slot just chosen or pressed while its time lasts, else none.</summary>
    private void UpdateBarCaption()
    {
        var words = "";
        if (!ReadingPanelOpen && !_wheel.Held)
        {
            var pointer = Pointer();
            var rowAbilities = _barLayerShown == HandSelection.AbilitiesLayer;
            if (_hotbar.RowSlotAt(pointer) is { } row) words = (rowAbilities ? _abilitySlots : _pocketSlots)[row].Caption;
            else if (_hotbar.StripSlotAt(pointer) is { } strip) words = (rowAbilities ? _pocketSlots : _abilitySlots)[strip].Caption;
            else if (_captionAge < BarCaptionS && _captionFor.Index is >= 0 and < Hotbar.Slots)
                words = (_captionFor.Abilities ? _abilitySlots : _pocketSlots)[_captionFor.Index].Caption ?? "";
        }
        _hotbar.ShowCaption(words ?? "");
    }

    // ---- choosing ----

    /// <summary>
    /// A slot of a layer chosen (1 to 10, or none) as the player's inventory.select, that layer brought down. The bar is read again at once;
    /// a refusal (the pockets cannot take back what the hands carry, a held island, no inventory here) is the notice, in the host's words.
    /// Returns the refusal's words, or null when chosen.
    /// </summary>
    public string? SelectBarSlot(string layer, int? slot)
    {
        if (Host is not { } host) return null;
        var result = host.PlayerSelect(slot, layer);
        _focusAge = double.MaxValue;
        RefreshHotbar();
        if (result["ok"]?.GetValue<bool>() == true)
        {
            if (slot is { } chosen) ShowBarCaption(chosen - 1, !IsPockets(layer));
            return null;
        }
        var words = InventoryPages.Failed(result, "That slot could not be chosen.");
        _noticeText = words;
        return words;
    }

    /// <summary>
    /// An ability's slot becomes the magic layer's selection: recorded only when it changes and the hands are empty (a select would put what
    /// they carry into the pockets, and an ability's key must never do that).
    /// </summary>
    private void ChooseAbility(int slot)
    {
        if (Host is not { } host) return;
        var hand = host.HandNow;
        if (!hand.IsNone && hand.Layer == HandSelection.AbilitiesLayer && hand.Abilities == slot) return;
        if (host.HeldBy(CommandHost.PlayerAvatar) != null) return;
        host.PlayerSelect(slot, HandSelection.AbilitiesLayer);
        RefreshHotbar();
    }

    /// <summary>Whether an ability's slot (0-based) can be cast from: the island has it and it is not locked.</summary>
    private bool AbilityReady(int index) => index < GubbleMagic.SlotCategories.Length && SlotAbility(index) != null && SlotLockedWords(index) == null;

    /// <summary>
    /// A key of the bar went down (0-based): on the pockets' layer it chooses its pocket slot; on the magic layer it is the ability's key as it
    /// always was (and its slot the selection), a slot still to come shrugs.
    /// </summary>
    private void BarKeyDown(int index)
    {
        if (IsPockets(BarLayerNow))
        {
            SelectBarSlot(HandSelection.PocketsLayer, index + 1);
            return;
        }
        ShowBarCaption(index, abilities: true);
        if (index >= GubbleMagic.SlotCategories.Length)
        {
            _cue.Shrug(GubbleMagic.UnknownIcon);
            return;
        }
        if (AbilityReady(index)) ChooseAbility(index + 1);
        // 6 is Fire: its press starts a tap or a drag, its release lights it (RoomHud.FireWheel.cs); 2 likewise for Grow's bridge.
        if (index == GubbleMagic.FireSlot) FireKeyDown();
        else if (index == GubbleMagic.GrowSlot) GrowKeyDown();
        else CastSlot(index, AimNow());
    }

    /// <summary>
    /// Tab: the other layer down, at its own selection (the magic layer's is never none: Glow, slot 1, when nothing was chosen). From a hand
    /// never chosen, the magic layer's Glow is recorded first, so the pockets, once down, are the host's record and not a fresh hand.
    /// </summary>
    public void SwitchBarLayer()
    {
        if (Host is not { } host) return;
        var hand = host.HandNow;
        if (IsPockets(BarLayerNow))
        {
            SelectBarSlot(HandSelection.AbilitiesLayer, hand.Abilities ?? 1);
            return;
        }
        // (Refused, a held island or the hands' thing with no room in the pockets, the pockets' select below says why.)
        if (hand.IsNone) host.PlayerSelect(1, HandSelection.AbilitiesLayer);
        SelectBarSlot(HandSelection.PocketsLayer, host.HandNow.Pockets);
    }

    /// <summary>The wheel over the bar: the selection steps right (wheel down) or left (wheel up), round the ends; on the magic layer it skips slots that cannot cast.</summary>
    private void StepBar(int step)
    {
        if (Host is not { } host) return;
        var hand = host.HandNow;
        if (IsPockets(BarLayerNow))
        {
            var from = hand.Pockets ?? (step > 0 ? 0 : Hotbar.Slots + 1);
            SelectBarSlot(HandSelection.PocketsLayer, Mathf.PosMod(from - 1 + step, Hotbar.Slots) + 1);
            return;
        }
        var at = (hand.IsNone ? 1 : hand.Abilities ?? 1) - 1;
        for (var tries = 1; tries <= Hotbar.Slots; tries++)
        {
            var next = Mathf.PosMod(at + step * tries, Hotbar.Slots);
            if (!AbilityReady(next)) continue;
            ChooseAbility(next + 1);
            ShowBarCaption(next, abilities: true);
            return;
        }
    }

    /// <summary>
    /// A click (the look drag's button let go without a drag) at a point: on a slot of the row it chooses it; on the strip it brings that layer
    /// down at that slot; anywhere else it uses the armed unit (decision 119: E or the left button). Returns whether the click did anything.
    /// </summary>
    private bool BarClick(Vector2 at)
    {
        if (_hotbar.Visible && _hotbar.RowSlotAt(at) is { } row)
        {
            if (IsPockets(BarLayerNow)) SelectBarSlot(HandSelection.PocketsLayer, row + 1);
            else
            {
                ShowBarCaption(row, abilities: true);
                if (AbilityReady(row)) ChooseAbility(row + 1);
            }
            return true;
        }
        if (_hotbar.Visible && _hotbar.StripSlotAt(at) is { } strip)
        {
            if (IsPockets(BarLayerNow)) SelectBarSlot(HandSelection.AbilitiesLayer, AbilityReady(strip) ? strip + 1 : Host?.HandNow.Abilities ?? 1);
            else
            {
                if (Host is { HandNow.IsNone: true } fresh) fresh.PlayerSelect(1, HandSelection.AbilitiesLayer);
                SelectBarSlot(HandSelection.PocketsLayer, strip + 1);
            }
            return true;
        }
        return Host?.Hand is { Holds: "unit" } && UseFromBar().Length > 0;
    }

    // ---- the hand: the armed unit used ----

    /// <summary>
    /// The armed unit used (E with nothing in the world for it, or a click): food is eaten (inventory.use), a log or stone set down
    /// (inventory.drop), anything else asked to be used and refused in the host's words (a seed's). Returns the words (the notice), or "" when
    /// nothing is armed.
    /// </summary>
    public string UseFromBar()
    {
        if (Host is not { } host) return "";
        var hand = host.Hand;
        if (hand.Holds != "unit" || hand.Item == null) return "";
        var words = InventoryPages.UseSelectedWords(host, hand.Item);
        _noticeText = words;
        _focusAge = double.MaxValue;
        RefreshHotbar();
        ShowBarCaption((hand.PocketsSlot ?? 1) - 1, abilities: false);
        return words;
    }

    /// <summary>How far above the bar's caption a panel on the left ends, and a notice card stands.</summary>
    public const float BarClearPx = 8;

    /// <summary>The highest a panel on the left (the camp, the Magic panel) or a notice may reach down to on a screen of this size: above the bar and its caption.</summary>
    public static float BarClearTop(Vector2 screen) => Hotbar.CaptionTop(screen) - BarClearPx;

    /// <summary>
    /// Tab is also the engine's focus-next key, which a button the player clicked (a footer button) would take before the HUD's unhandled
    /// input saw it: with no panel or menu of the HUD's own open (their buttons are meant to take the keyboard), the focus is let go first.
    /// </summary>
    public override void _Input(InputEvent input)
    {
        if (!PlayerControls.Fresh(input) || !PlayerControls.Hit(PlayerControls.Normalise(input), Act.BarLayer)) return;
        if (_pauseOpen || SettingsOpen || JournalOpen || GubbleViewOpen) return;
        if (GetViewport().GuiGetFocusOwner() != null) GetViewport().GuiReleaseFocus();
    }

    /// <summary>
    /// The help footer's place: unfolded, the width of the screen up to the journal's dock, growing up from the bottom edge (the bar hides
    /// under it); folded, only as wide as its "H keys" line at the lower left, so the hotbar beside it is clear and a click on the bar reaches
    /// the game (the footer is a panel, which would catch it).
    /// </summary>
    private void PlaceFooter()
    {
        var open = _keyHelp.Visible;
        _footer.AnchorRight = open ? 1 : 0;
        _footer.OffsetRight = open ? -JournalDockClearancePx : _footer.OffsetLeft;
        _helpHint.AutowrapMode = open ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off;
    }

    /// <summary>The bar's keys as the help says them: "1 to 0" for the default ten, else each one's key in turn.</summary>
    private static string BarKeysWords()
    {
        var keys = new string[Act.BarSlots.Length];
        for (var i = 0; i < keys.Length; i++) keys[i] = K(Act.BarSlots[i]);
        return string.Join("", keys) == "1234567890" ? "1 to 0" : string.Join("/", keys);
    }

    /// <summary>The H help's line for the bar (it names Tab and every key of the bar, from the input map).</summary>
    private string BarKeysLine() =>
        $"Bar: {BarKeysWords()} choose a slot (or the {PlayerControls.ZoomLabel()} or a {K(Act.LookDrag)} over it) · {K(Act.BarLayer)}: pockets or magic · " +
        $"{K(Act.Interact)} or a {K(Act.LookDrag)} uses a pocket slot, {K(Act.Interact)} or {K(Act.Hands)} sets a thing down";
}
