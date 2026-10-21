using System;
using Godot;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Ui;

/// <summary>
/// The journal as a small leather book docked at the lower right (the founder's brief: it expands and opens to the index, then
/// closes and shrinks back). Opening, the book flies up from its dock, grows to the spread and its cover swings open onto the
/// index (OpenSeconds); closing runs the same way back (CloseSeconds). A reversal midway is continuous, and every opening
/// starts at the index. The book holds the keyboard and the mouse from the moment it starts to open until it is back in its
/// dock (IsActive): the HUD blocks every other key, and the shade under the pages takes every click. It never keeps the focus,
/// so a later Space or Enter (ui_accept) cannot press anything of it.
/// </summary>
public partial class JournalPresentation : Control
{
    public const float OpenSeconds = 0.95f;
    public const float CloseSeconds = 0.8f;
    /// <summary>The written pages fade in over the last part of the opening (and out over the first part of the closing).</summary>
    public const float PagesFrom = 0.82f;

    public Func<CommandHost?> FindHost { get; set; } = () => null;
    /// <summary>The journal key's name now, for the dock and the pages.</summary>
    public Func<string> KeyLabel { get; set; } = () => "";
    /// <summary>The dock was clicked: the HUD decides what that does (it closes its other panels first).</summary>
    public Action DockPressed { get; set; } = () => { };
    /// <summary>IsActive changed: the HUD holds or frees the player's controls.</summary>
    public Action ActivityChanged { get; set; } = () => { };
    /// <summary>A button on a page was pressed: the HUD does what it asks and answers with the words for the page's foot.</summary>
    public Func<string, string> ButtonPressed { get; set; } = _ => "";
    public JournalPanel Panel { get; private set; } = null!;
    public JournalBookIcon Dock { get; private set; } = null!;
    /// <summary>Whether the book is opening or open (false while it closes or rests in its dock).</summary>
    public bool WantsOpen { get; private set; }
    /// <summary>0 in the dock, 1 open at the spread.</summary>
    public float Progress { get; private set; }
    /// <summary>Open, opening or closing: the book has the player's controls until it is back in its dock.</summary>
    public bool IsActive => WantsOpen || Progress > 0;
    public bool IsAnimating => Progress > 0 && Progress < 1;
    private ColorRect _shade = null!;
    private JournalFlight _flight = null!;

    public override void _Ready()
    {
        Name = "JournalPresentation";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Dock = new JournalBookIcon { KeyLabel = KeyLabel };
        AddChild(Dock);
        Dock.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomRight);
        Dock.OffsetLeft = -112;
        Dock.OffsetRight = -18;
        Dock.OffsetTop = -118;
        Dock.OffsetBottom = -18;
        Dock.Pressed += () => DockPressed();
        _shade = new ColorRect { Name = "JournalShade", Color = new Color(0.06f, 0.04f, 0.025f, 0), MouseFilter = MouseFilterEnum.Stop, Visible = false };
        AddChild(_shade);
        _shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Panel = new JournalPanel { Visible = false, FindHost = FindHost, CloseRequested = () => Close(), KeyLabel = KeyLabel, ButtonPressed = action => ButtonPressed(action) };
        AddChild(Panel);
        _flight = new JournalFlight { Name = "JournalFlight", Visible = false, Presentation = this, MouseFilter = MouseFilterEnum.Stop };
        AddChild(_flight);
        _flight.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        UpdateVisuals();
    }

    /// <summary>Open the book (or turn a closing one back): it always opens at the index.</summary>
    public void Open()
    {
        var wasActive = IsActive;
        WantsOpen = true;
        Panel.OpenPage(JournalPanel.IndexPage);
        Settle(wasActive);
    }

    /// <summary>Close the book (or turn an opening one back) to its dock. instant: straight to the dock, for another panel taking over.</summary>
    public void Close(bool instant = false)
    {
        var wasActive = IsActive;
        WantsOpen = false;
        if (instant) Progress = 0;
        Settle(wasActive);
    }

    public void Toggle()
    {
        if (WantsOpen) Close();
        else Open();
    }

    public override void _Process(double delta)
    {
        var wasActive = IsActive;
        Progress = Mathf.MoveToward(Progress, WantsOpen ? 1 : 0, (float)delta / (WantsOpen ? OpenSeconds : CloseSeconds));
        Settle(wasActive);
    }

    /// <summary>Draw the book where Progress puts it, and tell the HUD when it took or gave back the controls.</summary>
    private void Settle(bool wasActive)
    {
        Panel.Reading = WantsOpen;
        ReleaseBookFocus();
        UpdateVisuals();
        if (wasActive != IsActive) ActivityChanged();
    }

    /// <summary>
    /// No part of the book keeps the keyboard focus (the dock cannot take it; a button on a page could): a later Space or Enter
    /// (the engine's ui_accept) would press it. The note's line keeps its focus while the book is open, for typing.
    /// </summary>
    private void ReleaseBookFocus()
    {
        if (!IsInsideTree() || GetViewport().GuiGetFocusOwner() is not { } owner || !IsAncestorOf(owner)) return;
        if (WantsOpen && owner is LineEdit) return;
        owner.ReleaseFocus();
    }

    private void UpdateVisuals()
    {
        if (Dock == null) return;
        Dock.Visible = !IsActive;
        _shade.Visible = IsActive;
        _shade.Color = new Color(0.055f, 0.035f, 0.02f, 0.42f * Progress);
        var ink = Mathf.SmoothStep(0, 1, Mathf.Clamp((Progress - PagesFrom) / (1 - PagesFrom), 0, 1));
        Panel.Visible = Progress > PagesFrom;
        Panel.Modulate = new Color(1, 1, 1, ink);
        _flight.Visible = IsActive && Progress < 1;
        _flight.Modulate = new Color(1, 1, 1, 1 - ink);
        _flight.QueueRedraw();
    }
}

/// <summary>
/// The book between its dock and the spread: first the closed book flies up and grows (to 0.42 of the way), then the cover swings
/// open about the spine, its inside page passing over, while the leaves of the spread appear beneath.
/// </summary>
public partial class JournalFlight : Control
{
    /// <summary>The share of the opening spent flying from the dock; the rest turns the cover.</summary>
    public const float FlightShare = 0.42f;
    public JournalPresentation Presentation { get; set; } = null!;

    public override void _Draw()
    {
        var p = Presentation.Progress;
        var spread = Presentation.Panel.GetRect();
        var spine = spread.Position.X + spread.Size.X * JournalPaper.SpineShare;
        var right = new Rect2(spine, spread.Position.Y, spread.End.X - spine, spread.Size.Y);
        var origin = Presentation.Dock.CoverRect;
        if (p < FlightShare)
        {
            var travel = Mathf.SmoothStep(0, 1, p / FlightShare);
            var position = origin.Position.Lerp(right.Position, travel);
            position.Y -= Mathf.Sin(travel * Mathf.Pi) * Size.Y * 0.07f;
            JournalBookIcon.DrawCover(this, new Rect2(position, origin.Size.Lerp(right.Size, travel)), 1);
            return;
        }
        var opening = Mathf.SmoothStep(0, 1, (p - FlightShare) / (1 - FlightShare));
        var leftWidth = (spine - spread.Position.X) * opening;
        var visibleSpread = new Rect2(spine - leftWidth, spread.Position.Y, leftWidth + right.Size.X, spread.Size.Y);
        DrawStyleBox(new StyleBoxFlat
        {
            BgColor = new Color("503325"), BorderColor = new Color("2b1d18"), BorderWidthLeft = 3, BorderWidthTop = 3, BorderWidthRight = 3, BorderWidthBottom = 3,
            CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12, CornerRadiusBottomLeft = 12, CornerRadiusBottomRight = 12,
        }, visibleSpread);
        var paper = visibleSpread.Grow(-18);
        DrawRect(paper, new Color("e7d2a6"));
        for (var layer = 0; layer < 5; layer++)
            DrawLine(new Vector2(paper.Position.X, paper.End.Y - layer * 2), new Vector2(paper.End.X, paper.End.Y - layer * 2), new Color("bba275"));
        for (var i = 0; i < 18; i++)
            DrawLine(new Vector2(spine + i, paper.Position.Y), new Vector2(spine + i, paper.End.Y), new Color(0.28f, 0.15f, 0.06f, (18 - i) * 0.014f));
        var turn = Mathf.Cos(opening * Mathf.Pi);
        if (turn > 0)
        {
            // The cover still over the right-hand page, turning towards the reader.
            JournalBookIcon.DrawCover(this, new Rect2(spine, spread.Position.Y, right.Size.X * turn, spread.Size.Y), 0.6f + 0.4f * turn);
        }
        else if (opening < 0.91f)
        {
            // Past upright: the inside of the cover comes down over the left.
            var width = leftWidth * -turn;
            DrawRect(new Rect2(spine - width, paper.Position.Y, width, paper.Size.Y), new Color("d3b986"));
            DrawLine(new Vector2(spine - width, paper.Position.Y), new Vector2(spine - width, paper.End.Y), new Color("987546"), 2);
        }
    }
}
