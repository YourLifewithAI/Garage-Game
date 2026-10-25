using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GarageGame.Native;

/// <summary>
/// The Gubble wheel (the magic design's "The wheel"; research 4, marking menus): hold the ask button and, after HoldOpenS, nine
/// fixed wedges open around the pointer and the game pauses. Moving the mouse picks a wedge; letting go chooses it. A flick and
/// release chooses one before the wheel is drawn (an expert never waits for it), and letting go in the centre cancels. A release
/// before HoldOpenS with no flick is a tap: the smart ask. The wheel only says what was chosen; RoomHud carries it out.
/// It is the Gubble's (the Glow playtest, 9 October: "the wheel doesn't say whose abilities these are"): a title over it names the
/// Gubble ("The Gubble's magic"), and its rim is the Gubble's aura colour. Slots the island lacks stay in place, dim, with their
/// name and "not yet", so they read as still to come rather than broken.
/// </summary>
public partial class GubbleWheel : Control
{
    /// <summary>How long the button is held before the wheel opens: a shorter press is a tap.</summary>
    public const double HoldOpenS = 0.25;
    /// <summary>A pointer move this far before the wheel is drawn is a flick: its direction chooses the wedge on release.</summary>
    public const float FlickPx = 24f;
    /// <summary>Inside this radius of the open wheel's centre, letting go cancels.</summary>
    public const float CentrePx = 30f;
    /// <summary>The wheel's radius on screen, to the middle of its wedges.</summary>
    public const float RadiusPx = 120f;
    /// <summary>How far the wheel's disc reaches past the wedges' middles, and the width of its aura-coloured rim.</summary>
    public const float DiscMarginPx = 44f;
    public const float RimPx = 6f;
    /// <summary>A slot the island lacks is drawn at this opacity.</summary>
    public const float LackingAlpha = 0.45f;
    /// <summary>The words under a lacking slot's name.</summary>
    public const string LackingWords = "not yet";
    /// <summary>A locked wedge (Fire until the first rest) is drawn at this opacity, its lock mark beside its icon.</summary>
    public const float LockedAlpha = 0.6f;
    public const float LockOffsetPx = 20f;
    public static readonly Color LockColour = new("f2d39b");

    public enum Outcome { None, Tap, Wedge, Cancel }

    /// <summary>
    /// What an ability costs the player, under its wedge (the mechanics round, docs/game/MECHANICS.md section 5): its dots, or
    /// "needs 4" while the pool is short. In Creative (the shadow pool, which never refuses) it is drawn faint.
    /// </summary>
    public readonly record struct Price(string Text, bool Needs, bool Faint);

    /// <summary>Whether the ask button is held (the wheel may not be drawn yet).</summary>
    public bool Held { get; private set; }
    /// <summary>Whether the wheel is drawn (and the game paused).</summary>
    public bool Open { get; private set; }
    /// <summary>How far the pointer has moved since the press, in screen pixels.</summary>
    public Vector2 Offset { get; private set; }
    /// <summary>The wedge the pointer is over now, or null for the centre.</summary>
    public int? Hovered => GubbleMagic.WedgeAt(Offset, Open ? CentrePx : FlickPx);

    private double _held;
    private bool _pausedTree;
    private Vector2 _centre;
    private readonly List<Label> _wedgeLabels = new();
    private readonly List<Label> _priceLabels = new();
    private Label _centreLabel = null!;
    private Label _title = null!;
    private Color _rim = new("eec471");
    private readonly List<int> _locked = new();
    private readonly List<int> _noted = new();

    /// <summary>The wedges drawn locked now (their lock mark and reason), by index (tests).</summary>
    public IReadOnlyList<int> LockedWedges => _locked;
    /// <summary>The wedges that work but carry a note and a padlock for what is still to unlock (Grow's bridge, ivy and the rest), by index (tests).</summary>
    public IReadOnlyList<int> NotedWedges => _noted;

    /// <summary>The title over the wheel: whose magic it is.</summary>
    public Label Title => _title;
    /// <summary>The rim's colour: the Gubble's aura.</summary>
    public Color Rim => _rim;

    /// <summary>The words on each wedge now, clockwise from the top (tests).</summary>
    public IReadOnlyList<Label> WedgeLabels => _wedgeLabels;

    /// <summary>The price under each wedge, in the same order (empty and hidden for an order, or an ability without a price).</summary>
    public IReadOnlyList<Label> PriceLabels => _priceLabels;

    public override void _Ready()
    {
        Name = "GubbleWheel";
        Visible = false;
        MouseFilter = MouseFilterEnum.Ignore;
        // The wheel runs while the game it paused waits.
        ProcessMode = ProcessModeEnum.Always;
        for (var i = 0; i < GubbleMagic.Wedges.Length; i++)
        {
            var label = new Label { Name = "Wedge" + i, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
            label.AddThemeFontSizeOverride("font_size", 18);
            label.AddThemeColorOverride("font_outline_color", new Color("18332d"));
            label.AddThemeConstantOverride("outline_size", 4);
            AddChild(label);
            _wedgeLabels.Add(label);
            var price = new Label { Name = "Price" + i, Visible = false, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
            price.AddThemeFontSizeOverride("font_size", 15);
            price.AddThemeColorOverride("font_outline_color", new Color("18332d"));
            price.AddThemeConstantOverride("outline_size", 4);
            AddChild(price);
            _priceLabels.Add(price);
        }
        _centreLabel = new Label { Name = "WheelCentre", Text = "cancel", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
        _centreLabel.AddThemeFontSizeOverride("font_size", 13);
        AddChild(_centreLabel);
        _title = new Label { Name = "WheelTitle", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
        _title.AddThemeFontSizeOverride("font_size", 22);
        _title.AddThemeColorOverride("font_outline_color", new Color("18332d"));
        _title.AddThemeConstantOverride("outline_size", 6);
        AddChild(_title);
    }

    /// <summary>Whose wheel it is: the title over it ("The Gubble's magic") and the rim's colour (its aura).</summary>
    public void SetOwner(string title, Color rim)
    {
        if (_title.Text != title) _title.Text = title;
        _rim = new Color(rim, 1f);
        _title.AddThemeColorOverride("font_color", _rim.Lerp(new Color("fff3d6"), 0.35f));
    }

    /// <summary>
    /// The wedges' words: an ability slot shows its icon and the island's display_name, or a dim "?" when the island has none in
    /// that category; an order shows its icon, name and key where it has one.
    /// </summary>
    public void SetWedges(System.Func<int, string?> abilityName, System.Func<GubbleMagic.Wedge, string> orderWords, System.Func<int, string?>? lockedWords = null,
        System.Func<int, string?>? noteWords = null)
    {
        _locked.Clear();
        _noted.Clear();
        for (var i = 0; i < _wedgeLabels.Count; i++)
        {
            var wedge = GubbleMagic.Wedges[i];
            var label = _wedgeLabels[i];
            var name = wedge.Kind == GubbleMagic.WedgeKind.Ability ? abilityName(wedge.Slot) : orderWords(wedge);
            var known = name != null;
            // A slot the island has but the team has not learned (Fire before the first rest): its name, the lock mark and the reason.
            var locked = known && wedge.Kind == GubbleMagic.WedgeKind.Ability ? lockedWords?.Invoke(wedge.Slot) : null;
            // An ability that works but has more still to unlock (Grow's bridge and the rest): a note under its name and a padlock, bright.
            var note = known && locked == null && wedge.Kind == GubbleMagic.WedgeKind.Ability ? noteWords?.Invoke(wedge.Slot) : null;
            var text = locked != null ? $"{wedge.Icon}\n{wedge.Slot + 1} {name}\n{WrapWords(locked)}"
                : known ? (wedge.Kind == GubbleMagic.WedgeKind.Ability ? $"{wedge.Icon}\n{wedge.Slot + 1} {name}" + (note != null ? "\n" + WrapWords(note) : "") : name!)
                : $"{wedge.Icon}\n{wedge.Slot + 1} {wedge.Name}\n{LackingWords}";
            if (label.Text != text) label.Text = text;
            label.Modulate = new Color(1, 1, 1, locked != null ? LockedAlpha : known ? 1f : LackingAlpha);
            if (locked != null) _locked.Add(i);
            else if (note != null) _noted.Add(i);
        }
    }

    /// <summary>
    /// The price under each ability's wedge: what the player pays (the ordered price), as dots, or "needs N" while the pool is short.
    /// An order, an ability the island lacks, or one with no price shows none.
    /// </summary>
    public void SetPrices(System.Func<int, Price?> priceOfSlot)
    {
        for (var i = 0; i < _priceLabels.Count; i++)
        {
            var wedge = GubbleMagic.Wedges[i];
            var price = wedge.Kind == GubbleMagic.WedgeKind.Ability ? priceOfSlot(wedge.Slot) : null;
            var label = _priceLabels[i];
            label.Visible = price != null;
            if (price is not { } shown) continue;
            if (label.Text != shown.Text) label.Text = shown.Text;
            label.AddThemeColorOverride("font_color", shown.Needs ? new Color("f2b46b") : new Color("fff3d6"));
            label.Modulate = new Color(1, 1, 1, shown.Faint && shown.Needs ? 0.5f : 1f);
        }
    }

    /// <summary>A wedge's longer words in two lines, broken at the space nearest their middle (the first window capture: one line ran across the wheel).</summary>
    public static string WrapWords(string text, int longest = 20)
    {
        if (text.Length <= longest) return text;
        var middle = text.Length / 2;
        var best = -1;
        for (var i = 0; i < text.Length; i++)
            if (text[i] == ' ' && (best < 0 || Math.Abs(i - middle) < Math.Abs(best - middle))) best = i;
        return best < 0 ? text : text[..best] + "\n" + text[(best + 1)..];
    }

    /// <summary>The ask button went down at this screen point: the hold starts.</summary>
    public void Begin(Vector2 centre)
    {
        Held = true;
        Open = false;
        _held = 0;
        Offset = Vector2.Zero;
        _centre = centre;
    }

    /// <summary>The pointer moved while the button is held (relative pixels; works while the mouse is captured).</summary>
    public void Move(Vector2 relative)
    {
        if (!Held || !relative.IsFinite()) return;
        Offset = (Offset + relative).LimitLength(RadiusPx * 1.5f);
        QueueRedraw();
    }

    /// <summary>Time passes while the button is held: after HoldOpenS the wheel opens and the game pauses.</summary>
    public void Tick(double delta)
    {
        if (!Held || Open) return;
        _held += delta;
        if (_held < HoldOpenS) return;
        Open = true;
        Visible = true;
        if (IsInsideTree() && !GetTree().Paused)
        {
            GetTree().Paused = true;
            _pausedTree = true;
        }
        Layout();
        QueueRedraw();
    }

    /// <summary>The button let go: what was chosen (a wedge's index in wedge), a tap, or a cancel. The wheel closes and the game resumes.</summary>
    public Outcome End(out int wedge)
    {
        wedge = -1;
        if (!Held) return Outcome.None;
        var hovered = Hovered;
        var outcome = hovered != null ? Outcome.Wedge : Open ? Outcome.Cancel : Outcome.Tap;
        if (hovered is { } index) wedge = index;
        Close();
        return outcome;
    }

    /// <summary>Close without choosing (the panel opened, the HUD left the tree).</summary>
    public void Close()
    {
        Held = false;
        Open = false;
        Visible = false;
        Offset = Vector2.Zero;
        if (_pausedTree && IsInsideTree()) GetTree().Paused = false;
        _pausedTree = false;
    }

    public override void _ExitTree()
    {
        if (_pausedTree && IsInsideTree()) GetTree().Paused = false;
        _pausedTree = false;
    }

    private void Layout()
    {
        var screen = GetViewportRect().Size;
        // Kept whole on screen, wherever the button was pressed.
        var margin = RadiusPx + 48f;
        _centre = new Vector2(Mathf.Clamp(_centre.X, margin, Mathf.Max(margin, screen.X - margin)), Mathf.Clamp(_centre.Y, margin, Mathf.Max(margin, screen.Y - margin)));
        for (var i = 0; i < _wedgeLabels.Count; i++)
        {
            var at = _centre + GubbleMagic.WedgeDirection(i) * RadiusPx;
            var size = _wedgeLabels[i].GetCombinedMinimumSize();
            _wedgeLabels[i].Position = at - size / 2;
            // The price hangs just under its wedge's words.
            var price = _priceLabels[i].GetCombinedMinimumSize();
            _priceLabels[i].Position = new Vector2(at.X - price.X / 2, at.Y + size.Y / 2 - 2f);
        }
        _centreLabel.Position = _centre - _centreLabel.GetCombinedMinimumSize() / 2;
        var title = _title.GetCombinedMinimumSize();
        _title.Position = _centre - new Vector2(title.X / 2, RadiusPx + DiscMarginPx + 6f + title.Y);
    }

    public override void _Draw()
    {
        if (!Open) return;
        DrawCircle(_centre, RadiusPx + DiscMarginPx, new Color(0.075f, 0.13f, 0.13f, 0.82f));
        // The rim in the Gubble's aura: the wheel is its.
        DrawArc(_centre, RadiusPx + DiscMarginPx - RimPx * 0.5f, 0, Mathf.Tau, 96, _rim, RimPx, true);
        DrawCircle(_centre, CentrePx, new Color(0.16f, 0.24f, 0.23f, 0.95f));
        if (Hovered is { } hovered) DrawCircle(_centre + GubbleMagic.WedgeDirection(hovered) * RadiusPx, 40f, new Color(0.95f, 0.84f, 0.54f, 0.35f));
        // A locked wedge carries a small padlock drawn beside its icon (decision 77: Fire until the first rest), whatever the font has.
        foreach (var locked in _locked.Concat(_noted))
        {
            var at = _centre + GubbleMagic.WedgeDirection(locked) * RadiusPx + new Vector2(LockOffsetPx, -LockOffsetPx);
            DrawArc(at + new Vector2(0, -3f), 4f, Mathf.Pi, Mathf.Tau, 12, LockColour, 2f, true);
            DrawRect(new Rect2(at + new Vector2(-6f, -3f), new Vector2(12f, 9f)), LockColour);
        }
        DrawLine(_centre, _centre + Offset, new Color(0.95f, 0.84f, 0.54f, 0.8f), 2f);
    }
}
