using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Godot;
using GarageGame.Native.Kernel;
using GarageGame.Native.Ui;

namespace GarageGame.Native;

/// <summary>
/// The repair's icon (MECHANICS.md decision 131): the Gubble repairs what is scorched without asking when the repair spends logs or stones
/// (a build the player made still waits for the player's yes, in the kernel's words), and the spend is made visible. The host raises
/// RepairSpent once the Gubble's repair has committed (never for the player's own repair, never in Creative); this part listens, and for each
/// puts a small card over the thing repaired, in the world: the kinds' marks with the kernel's words for what was spent ("2 logs, 1 stone"),
/// rising a little and fading, gone after about three seconds. At most RepairIconsMax stand at once (the oldest goes), a second repair of the
/// same thing replaces its card, and they stand aside with the notice while the journal, the Gubble's eyes or the intro fill the screen.
/// The event is only queued where it is raised; the card is made on the HUD's next frame, so a listener can never disturb the command.
/// Nothing here costs a frame while no icon stands: a comparison and an empty queue (PromptCost.Piece.Repair measures the rest).
/// </summary>
public partial class RoomHud
{
    /// <summary>The most icons standing at once; a new one past this takes the oldest's place.</summary>
    public const int RepairIconsMax = 3;
    /// <summary>How far above the thing's top (its anchor on the screen) the card's foot stands, and the screen's margin it is kept inside.</summary>
    public const float RepairIconAbovePx = 8, RepairIconMarginPx = 16;

    private readonly List<RepairIcon> _repairIcons = new();
    private readonly ConcurrentQueue<RepairSpentView> _repairPending = new();
    private CommandHost? _repairListening;

    /// <summary>The icons standing now, oldest first (tests read their words, marks, places and ages).</summary>
    public IReadOnlyList<RepairIcon> RepairIcons => _repairIcons;

    /// <summary>The host's word that the Gubble's repair spent: queued for the next frame (this can be raised from anywhere inside a command).</summary>
    private void OnRepairSpent(RepairSpentView view) => _repairPending.Enqueue(view);

    /// <summary>Test seam: the host's word, as if it had raised it (the suite checks the cap, the replacing and the fading without a creation to burn for each).</summary>
    internal void RepairSpentForTests(RepairSpentView view) => OnRepairSpent(view);

    /// <summary>The HUD stops listening for repairs (another host took its place, or the HUD leaves the tree).</summary>
    private void StopListeningForRepairs()
    {
        if (_repairListening != null && IsInstanceValid(_repairListening)) _repairListening.RepairSpent -= OnRepairSpent;
        _repairListening = null;
    }

    /// <summary>
    /// Each frame: listen to the room's host, make the cards the host asked for, and age, place and take away the ones standing. The card's
    /// place is its thing's top on the screen, a little above it and rising; a thing behind the view puts it in the lower middle of the screen
    /// instead (the focus tag's rule), and it is always kept on the screen, above the hotbar's caption and out of the minimap's corner.
    /// </summary>
    private void TickRepairIcons(double delta)
    {
        var host = Host;
        if (!ReferenceEquals(host, _repairListening))
        {
            StopListeningForRepairs();
            _repairListening = host;
            if (host != null) host.RepairSpent += OnRepairSpent;
        }
        while (_repairPending.TryDequeue(out var view)) AddRepairIcon(host, view);
        if (_repairIcons.Count == 0) return;
        var aside = JournalOpen || GubbleViewOpen || IntroPlaying;
        var camera = GetViewport().GetCamera3D();
        var screen = GetViewport().GetVisibleRect().Size;
        for (var i = _repairIcons.Count - 1; i >= 0; i--)
        {
            var icon = _repairIcons[i];
            icon.Advance(delta);
            if (icon.Done)
            {
                _repairIcons.RemoveAt(i);
                icon.QueueFree();
                continue;
            }
            var shown = !aside && camera != null;
            if (icon.Visible != shown) icon.Visible = shown;
            if (!shown) continue;
            var size = icon.GetCombinedMinimumSize();
            if (icon.Size != size) icon.Size = size;
            icon.Position = camera!.IsPositionBehind(icon.At)
                ? new Vector2(Mathf.Round(screen.X * 0.5f - size.X * 0.5f), screen.Y * 0.72f - RepairIcon.RiseAt(icon.Age))
                : RepairIconPlace(camera.UnprojectPosition(icon.At), size, screen, RepairIcon.RiseAt(icon.Age));
        }
    }

    /// <summary>A card for a repair's spend: the kinds' marks from their materials (the pack's), their words from the kernel. A repair that spent nothing shows none.</summary>
    private void AddRepairIcon(CommandHost? host, RepairSpentView view)
    {
        if (view.Spent.Count == 0) return;
        var spent = new List<(string? Material, string Words)>(view.Spent.Count);
        foreach (var unit in view.Spent)
            spent.Add((host?.InventoryRules?.Kind(unit.Kind)?.Material, KernelJson.DisplayText(unit.Words, 40)));
        // A thing repaired again while its card stands has the new card in its place; past the most, the oldest goes.
        for (var i = _repairIcons.Count - 1; i >= 0; i--)
            if (_repairIcons[i].Thing == view.Thing) { _repairIcons[i].QueueFree(); _repairIcons.RemoveAt(i); }
        while (_repairIcons.Count >= RepairIconsMax) { _repairIcons[0].QueueFree(); _repairIcons.RemoveAt(0); }
        var icon = new RepairIcon();
        icon.Build(view.Thing, AnchorOfRepair(host, view), spent);
        AddChild(icon);
        // Under the notice card (which was added after the panels), over everything else.
        if (_noticeCard != null && IsInstanceValid(_noticeCard)) MoveChild(icon, _noticeCard.GetIndex());
        _repairIcons.Add(icon);
    }

    /// <summary>
    /// Where the card is anchored in the world: the host's point above the thing; when it gave none (the thing was not listed), the top of the
    /// thing's built node, else the player's feet.
    /// </summary>
    private Vector3 AnchorOfRepair(CommandHost? host, RepairSpentView view)
    {
        if (view.At != Vector3.Zero) return view.At;
        if (host?.ObjectNodeOf(view.Thing) is { } node && IsInstanceValid(node)) return node.GlobalPosition + Vector3.Up * 0.05f;
        return Player != null && IsInstanceValid(Player) ? Player.GlobalPosition + Vector3.Up * Player.BodyHeightM : Vector3.Zero;
    }

    /// <summary>
    /// Where a card of this size stands for a thing's top at this point on a screen of this size, risen by this much (the suite checks it by
    /// numbers): centred over the point with its foot RepairIconAbovePx above it, kept inside the screen's margin, above the hotbar's caption
    /// and out of the minimap's corner.
    /// </summary>
    public static Vector2 RepairIconPlace(Vector2 anchor, Vector2 size, Vector2 screen, float rise = 0)
    {
        var x = anchor.X - size.X * 0.5f;
        var y = anchor.Y - size.Y - RepairIconAbovePx - rise;
        var foot = Mathf.Max(RepairIconMarginPx, BarClearTop(screen) - size.Y);
        x = Mathf.Clamp(x, RepairIconMarginPx, Mathf.Max(RepairIconMarginPx, screen.X - size.X - RepairIconMarginPx));
        y = Mathf.Clamp(y, RepairIconMarginPx, foot);
        if (x + size.X > screen.X - Sandbox.SandboxControls.FocusWords.MinimapWidthPx && y < Sandbox.SandboxControls.FocusWords.MinimapHeightPx)
            y = Mathf.Min(foot, Sandbox.SandboxControls.FocusWords.MinimapHeightPx);
        return new Vector2(Mathf.Round(x), Mathf.Round(y));
    }
}
