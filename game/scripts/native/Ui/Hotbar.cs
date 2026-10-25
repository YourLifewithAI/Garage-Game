using System;
using System.Collections.Generic;
using Godot;

namespace GarageGame.Native.Ui;

/// <summary>
/// One slot as the hotbar shows it: its key, the big line (an ability's icon, a pocket's kind), the small line under it (an ability's name,
/// what a pocket's unit does), the count at the top right (a stack's units), and the slot's state: locked (a padlock, the lock words in the
/// caption), lacking (dim: no ability or nothing in the slot), noted (a padlock on a slot that works: Grow's unlocks still to find). The
/// caption is what the line over the bar says when this slot is chosen or pointed at: the kind, the count and what a unit does, or the
/// ability and how its key casts, or the lock words. Names are untrusted display text: shown in labels as plain text, never read.
/// </summary>
public readonly record struct HotbarSlot(string Key, string Big, string Small, string Count, bool Locked, bool Lacking, bool Noted, string Caption, string Strip);

/// <summary>
/// The hotbar (docs/game/HOTBAR.md, MECHANICS.md decisions 119, 120 and 128): ten slots along the bottom of the screen, keys 1 to 0, in two
/// layers, the pockets and the Gubble's magic. The layer in the hand is the row; the other is a thin dimmed strip above it with its own
/// selection, so both are always on screen; a caption over them says what the chosen (or pointed at) slot is. It decides nothing and holds
/// no state of its own: RoomHud.Hotbar.cs reads the host's hand (player.status.hand) and the pockets and hands the bar what to show. It never
/// takes the mouse (every part ignores it: a click or a drag over it goes to the game, the HUD reads where the pointer is).
/// </summary>
public partial class Hotbar : Control
{
    public const int Slots = 10;
    /// <summary>A slot of the row, in pixels of the HUD's canvas (1280x720; at 1152x648 the window shows them at nine tenths).</summary>
    public const float SlotWPx = 64, SlotHPx = 58, GapPx = 4;
    /// <summary>The other layer's strip over the row, its gap, the bottom edge's margin and the caption over the strip.</summary>
    public const float StripHPx = 20, StripGapPx = 4, BottomPx = 14, CaptionHPx = 26, CaptionGapPx = 6, EdgePx = 16;
    /// <summary>The words' sizes (canvas pixels): the key, the big line, the small line, the count, the strip, the tags and the caption.</summary>
    public const int KeyFont = 13, BigFont = 15, IconFont = 22, SmallFont = 12, CountFont = 15, StripFont = 12, TagFont = 13, CaptionFont = 16;
    /// <summary>The strip's opacity (dimmed: "also yours", its keys idle until Tab brings it down) and a lacking slot's.</summary>
    public const float StripAlpha = 0.6f, LackingAlpha = 0.5f;

    public static readonly Color SlotFill = new(0.075f, 0.13f, 0.13f, 0.86f);
    public static readonly Color SlotEdge = new("3d6760");
    public static readonly Color ChosenEdge = MenuStyle.Warm;
    public static readonly Color ChosenFill = new(0.16f, 0.27f, 0.25f, 0.92f);

    /// <summary>Where every part of the bar stands on a screen of a size (canvas pixels): the row's slots, the strip's, the tags and the caption.</summary>
    public readonly record struct Places(Rect2[] Row, Rect2[] Strip, Rect2 Block, Rect2 RowTag, Rect2 StripTag, Rect2 Caption)
    {
        /// <summary>The top of the bar's row and strip: a panel on the left ends above it.</summary>
        public float Top => Block.Position.Y;
    }

    /// <summary>The top of the bar's caption on a screen of this size: a panel on the left ends above it, a notice stays over it.</summary>
    public static float CaptionTop(Vector2 screen) => screen.Y - BottomPx - SlotHPx - StripGapPx - StripHPx - CaptionGapPx - CaptionHPx;

    /// <summary>
    /// The places on a screen of this size: the row centred on the bottom edge, BottomPx above it; the strip StripGapPx over the row; the
    /// caption over the strip; the two tags right-aligned before the row and the strip. On a narrow screen the slots narrow to fit.
    /// </summary>
    public static Places Layout(Vector2 screen)
    {
        var width = Mathf.Min(SlotWPx, (screen.X - 2 * EdgePx - (Slots - 1) * GapPx) / Slots);
        width = Mathf.Max(width, 24);
        var total = width * Slots + GapPx * (Slots - 1);
        var left = Mathf.Round((screen.X - total) * 0.5f);
        var rowY = screen.Y - BottomPx - SlotHPx;
        var stripY = rowY - StripGapPx - StripHPx;
        var row = new Rect2[Slots];
        var strip = new Rect2[Slots];
        for (var i = 0; i < Slots; i++)
        {
            var x = left + i * (width + GapPx);
            row[i] = new Rect2(x, rowY, width, SlotHPx);
            strip[i] = new Rect2(x, stripY, width, StripHPx);
        }
        var block = new Rect2(left, stripY, total, rowY + SlotHPx - stripY);
        var tagW = Mathf.Max(0, Mathf.Min(150, left - EdgePx - 8));
        var rowTag = new Rect2(left - 8 - tagW, rowY, tagW, SlotHPx);
        var stripTag = new Rect2(left - 8 - tagW, stripY, tagW, StripHPx);
        var caption = new Rect2(left, stripY - CaptionGapPx - CaptionHPx, total, CaptionHPx);
        return new Places(row, strip, block, rowTag, stripTag, caption);
    }

    private readonly Label[] _key = new Label[Slots], _big = new Label[Slots], _small = new Label[Slots], _count = new Label[Slots], _strip = new Label[Slots];
    private Label _rowTag = null!, _stripTag = null!, _caption = null!;
    private PanelContainer _captionCard = null!;
    private Places _places;
    private Vector2 _laidFor = new(-1, -1);
    private readonly HotbarSlot[] _row = new HotbarSlot[Slots], _other = new HotbarSlot[Slots];
    private int? _rowChosen, _stripChosen;
    private bool _abilitiesRow = true;
    private string _rowTagWords = "", _stripTagWords = "";
    private readonly List<int> _padlocks = new();

    /// <summary>What the row and the strip show now, and which of each is chosen (tests).</summary>
    public IReadOnlyList<HotbarSlot> Row => _row;
    public IReadOnlyList<HotbarSlot> StripSlots => _other;
    public int? RowChosen => _rowChosen;
    public int? StripChosen => _stripChosen;
    /// <summary>Whether the row is the magic layer (else the pockets).</summary>
    public bool AbilitiesRow => _abilitiesRow;
    public Places Placed => _places;
    /// <summary>The labels, for the suite's layout by numbers: each slot's key, big and small lines and count; the strip's; the tags and the caption.</summary>
    public IReadOnlyList<Label> KeyLabels => _key;
    public IReadOnlyList<Label> BigLabels => _big;
    public IReadOnlyList<Label> SmallLabels => _small;
    public IReadOnlyList<Label> CountLabels => _count;
    public IReadOnlyList<Label> StripLabels => _strip;
    public Label RowTag => _rowTag;
    public Label StripTag => _stripTag;
    public Label Caption => _caption;
    public PanelContainer CaptionCard => _captionCard;
    /// <summary>How many times the bar's words were written (the suite checks that an unchanged bar writes nothing).</summary>
    public int Writes { get; private set; }

    public override void _Ready()
    {
        Name = "Hotbar";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        for (var i = 0; i < Slots; i++)
        {
            _key[i] = Words("Key" + i, KeyFont, MenuStyle.Muted, HorizontalAlignment.Left);
            _count[i] = Words("Count" + i, CountFont, MenuStyle.Ink, HorizontalAlignment.Right);
            _big[i] = Words("Big" + i, BigFont, MenuStyle.Ink, HorizontalAlignment.Center);
            _small[i] = Words("Small" + i, SmallFont, MenuStyle.Muted, HorizontalAlignment.Center);
            _strip[i] = Words("Strip" + i, StripFont, MenuStyle.Ink, HorizontalAlignment.Center);
        }
        _rowTag = Words("RowTag", TagFont, MenuStyle.Warm, HorizontalAlignment.Right);
        _stripTag = Words("StripTag", TagFont, MenuStyle.Muted, HorizontalAlignment.Right);
        _captionCard = new PanelContainer { Name = "Caption", MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _captionCard.AddThemeStyleboxOverride("panel", MenuStyle.Box(new Color(0.075f, 0.13f, 0.13f, 0.9f), MenuStyle.Warm, 1, 8, 4));
        AddChild(_captionCard);
        _caption = new Label { Name = "CaptionWords", MouseFilter = MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center };
        _caption.AddThemeFontSizeOverride("font_size", CaptionFont);
        _caption.AddThemeColorOverride("font_color", MenuStyle.Ink);
        _captionCard.AddChild(_caption);
    }

    private Label Words(string name, int size, Color colour, HorizontalAlignment align)
    {
        var label = new Label
        {
            Name = name, MouseFilter = MouseFilterEnum.Ignore, HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center,
            ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", colour);
        label.AddThemeColorOverride("font_outline_color", new Color("10201e"));
        label.AddThemeConstantOverride("outline_size", 3);
        AddChild(label);
        return label;
    }

    /// <summary>
    /// What to show: the row (the layer in the hand) and its chosen slot, the strip (the other layer) and its chosen slot, and the two tags.
    /// Labels are written only where their words changed, and the bar is drawn again only when something did.
    /// </summary>
    public void Show(bool abilitiesRow, IReadOnlyList<HotbarSlot> row, int? rowChosen, IReadOnlyList<HotbarSlot> strip, int? stripChosen, string rowTag, string stripTag)
    {
        var changed = abilitiesRow != _abilitiesRow || rowChosen != _rowChosen || stripChosen != _stripChosen || rowTag != _rowTagWords || stripTag != _stripTagWords;
        for (var i = 0; i < Slots; i++)
        {
            var slot = i < row.Count ? row[i] : default;
            var other = i < strip.Count ? strip[i] : default;
            if (slot != _row[i] || other != _other[i]) changed = true;
            _row[i] = slot;
            _other[i] = other;
        }
        if (!changed) return;
        _abilitiesRow = abilitiesRow;
        _rowChosen = rowChosen;
        _stripChosen = stripChosen;
        _rowTagWords = rowTag;
        _stripTagWords = stripTag;
        Writes++;
        for (var i = 0; i < Slots; i++)
        {
            var slot = _row[i];
            Set(_key[i], slot.Key);
            Set(_count[i], slot.Count);
            Set(_big[i], slot.Big);
            Set(_small[i], slot.Small);
            _big[i].AddThemeFontSizeOverride("font_size", abilitiesRow ? IconFont : BigFont);
            var dim = slot.Lacking ? LackingAlpha : 1f;
            _big[i].Modulate = _small[i].Modulate = new Color(1, 1, 1, dim);
            Set(_strip[i], _other[i].Strip);
            _strip[i].Modulate = new Color(1, 1, 1, _other[i].Lacking ? StripAlpha * LackingAlpha : StripAlpha);
        }
        Set(_rowTag, rowTag);
        Set(_stripTag, stripTag);
        _stripTag.Modulate = new Color(1, 1, 1, StripAlpha + 0.2f);
        _padlocks.Clear();
        for (var i = 0; i < Slots; i++)
            if (_row[i].Locked || _row[i].Noted) _padlocks.Add(i);
        QueueRedraw();
    }

    private static void Set(Label label, string text)
    {
        if (label.Text != text) label.Text = text;
    }

    /// <summary>The caption over the bar: these words, or none (hidden).</summary>
    public void ShowCaption(string words)
    {
        if (_caption.Text != words)
        {
            _caption.Text = words;
            _captionCard.ResetSize();
            PlaceCaption();
        }
        var shown = words.Length > 0;
        if (_captionCard.Visible != shown) _captionCard.Visible = shown;
    }

    private void PlaceCaption()
    {
        var size = _captionCard.GetCombinedMinimumSize();
        var screen = _laidFor.X > 0 ? _laidFor : GetViewportRect().Size;
        var x = Mathf.Clamp(_places.Caption.GetCenter().X - size.X * 0.5f, EdgePx, Mathf.Max(EdgePx, screen.X - size.X - EdgePx));
        _captionCard.Position = new Vector2(Mathf.Round(x), _places.Caption.End.Y - size.Y);
        _captionCard.Size = size;
    }

    /// <summary>Each frame (from the HUD): the places again only when the screen's size changed.</summary>
    public void Place(Vector2 screen)
    {
        if (screen == _laidFor) return;
        _laidFor = screen;
        _places = Layout(screen);
        for (var i = 0; i < Slots; i++)
        {
            var cell = _places.Row[i];
            Put(_key[i], new Rect2(cell.Position + new Vector2(5, 2), new Vector2(cell.Size.X * 0.45f, 16)));
            Put(_count[i], new Rect2(cell.Position + new Vector2(cell.Size.X * 0.4f, 2), new Vector2(cell.Size.X * 0.6f - 5, 16)));
            Put(_big[i], new Rect2(cell.Position + new Vector2(3, 16), new Vector2(cell.Size.X - 6, 24)));
            Put(_small[i], new Rect2(cell.Position + new Vector2(3, cell.Size.Y - 18), new Vector2(cell.Size.X - 6, 16)));
            Put(_strip[i], _places.Strip[i]);
        }
        Put(_rowTag, _places.RowTag);
        Put(_stripTag, _places.StripTag);
        PlaceCaption();
        QueueRedraw();
    }

    private static void Put(Control control, Rect2 at)
    {
        control.Position = at.Position;
        control.Size = at.Size;
    }

    /// <summary>The slot of the row (0-based) under a point on the screen, or null.</summary>
    public int? RowSlotAt(Vector2 point)
    {
        for (var i = 0; i < Slots; i++)
            if (_places.Row != null && _places.Row[i].HasPoint(point)) return i;
        return null;
    }

    /// <summary>The slot of the strip (0-based) under a point on the screen, or null.</summary>
    public int? StripSlotAt(Vector2 point)
    {
        for (var i = 0; i < Slots; i++)
            if (_places.Strip != null && _places.Strip[i].HasPoint(point)) return i;
        return null;
    }

    /// <summary>Whether a point is over the bar (its row or its strip, the gaps between them included).</summary>
    public bool Over(Vector2 point) => _places.Row != null && _places.Block.HasPoint(point);

    public override void _Draw()
    {
        if (_places.Row == null) return;
        for (var i = 0; i < Slots; i++)
        {
            var chosen = _rowChosen == i + 1;
            var cell = _places.Row[i];
            DrawStyleBox(Box(chosen ? ChosenFill : SlotFill, chosen ? ChosenEdge : SlotEdge, chosen ? 3 : 1, 8), cell);
            var strip = _places.Strip[i];
            var stripChosen = _stripChosen == i + 1;
            DrawStyleBox(Box(new Color(SlotFill, SlotFill.A * StripAlpha), stripChosen ? new Color(ChosenEdge, 0.85f) : new Color(SlotEdge, StripAlpha), stripChosen ? 2 : 1, 5), strip);
        }
        // A padlock on a locked slot (Fire before the first rest) and on one that works with more still to unlock (Grow), as the wheel draws it.
        foreach (var i in _padlocks)
        {
            var cell = _places.Row[i];
            var at = new Vector2(cell.End.X - 12, cell.Position.Y + 12);
            DrawArc(at + new Vector2(0, -3f), 4f, Mathf.Pi, Mathf.Tau, 12, GubbleWheel.LockColour, 2f, true);
            DrawRect(new Rect2(at + new Vector2(-6f, -3f), new Vector2(12f, 9f)), GubbleWheel.LockColour);
        }
    }

    // The boxes, made once per look (the bar draws them again only when it changes).
    private readonly Dictionary<(Color, Color, int, int), StyleBoxFlat> _boxes = new();

    private StyleBoxFlat Box(Color fill, Color edge, int width, int radius)
    {
        if (_boxes.TryGetValue((fill, edge, width, radius), out var box)) return box;
        return _boxes[(fill, edge, width, radius)] = MenuStyle.Box(fill, edge, width, radius, 0);
    }
}
