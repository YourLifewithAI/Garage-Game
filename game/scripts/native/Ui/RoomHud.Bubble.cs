using System;
using System.Text.Json.Nodes;
using Godot;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;
using GarageGame.Native.Ui;

namespace GarageGame.Native;

/// <summary>
/// Steering the Bubble by the smart ask (MECHANICS.md decisions 57, 58 and 60; Kernel/Bubble/CommandHostBubbleLead.cs):
/// - The first right-click on the bubble selects it (the kernel's BubbleAt picks it): it wears the focus highlight, as a thing that
///   can be carried does, and the reticle's tag says what the next right-click does.
/// - The next right-click on a spot leads it there: one command, goal.set lead, the player's own (CommandHost.PlayerLead), carried
///   out by the Gubble. Before the click commits, the reticle's tag asks the host's preview: "lead it there", or for a spot higher
///   than the bubble can drift, "lift it there" with its price in pips (dots, or "needs N" while the pool is short), as the wheel
///   shows prices. A refusal shows its words in the Gubble's thought bubble, as every refusal does (and in the tag before the click).
/// - Esc, a right-click on nothing, on the bubble again or on the Gubble lets it go. The selection also goes with the bubble (it
///   popped, a recast made another) and when the lead it was given ends (it arrived, popped, was blocked or stopped).
/// The HUD decides nothing: it asks the host (ActiveBubble, BubbleAt, PlayerLead) and tells the look what to draw.
/// </summary>
public partial class RoomHud
{
    /// <summary>A ray passing this close to the bubble's skin still picks it (the kernel's own default): the bubble is small.</summary>
    public const float BubblePickSlackM = 0.02f;
    /// <summary>
    /// The lead's preview is a whole command through the host (a few tenths of a millisecond and about 25 KB of garbage), so the reticle
    /// asks it again only when the aim has moved farther than this from the spot it last asked about (a spot a centimetre away is the
    /// same spot as far as a lift or a price goes), when the pool changed, and at most every LeadPreviewMinS; otherwise what it last
    /// said stands for LeadPreviewS (the ground and the pool may have changed under a still aim).
    /// </summary>
    public const float LeadPreviewNearM = 0.01f;
    public const double LeadPreviewS = 0.5, LeadPreviewMinS = 0.2;

    /// <summary>The bubble the smart ask has selected (its effect id), or null.</summary>
    public string? SelectedBubble { get; private set; }
    /// <summary>What the next right-click would lead: the host's preview of the lead to the aimed spot, as the reticle's tag says it (tests).</summary>
    public (bool Ok, bool Lift, int Price, string Words)? LeadPreview => _leadPreview is { } p ? (p.Ok, p.Lift, p.Price, p.Words) : null;
    /// <summary>Times the host was asked to preview a lead (the tag asks at the focus's pace, and not again for the same spot).</summary>
    public int LeadPreviews { get; private set; }

    /// <summary>What the notice line says when the choice ends while it still tells how to lead the bubble (it would be stale).</summary>
    public const string BubbleLetGoWords = "The bubble is let go.";

    private string? _leadJob;
    /// <summary>The instruction SelectBubble put on the notice line, while it stands there.</summary>
    private string _chosenNotice = "";
    /// <summary>Whether the lead the player committed is a lift (the host's word in the goal.set result); null for a lead the HUD did not set.</summary>
    private bool? _leadIsLift;
    private bool _selectedWasLed;
    private (Vector3 Point, int Pips, double At, bool Ok, bool Lift, int Price, string Words)? _leadPreview;
    private string? _previewFor;
    /// <summary>The HUD's own clock for the preview's cache: the frames' deltas added up (so a suite that runs frames faster than real time and a game at any frame rate both get the seconds they mean).</summary>
    private double _bubbleClockS;
    private CommandHost.BubbleView? _bubbleSeen;

    /// <summary>
    /// The bubble on a ray, if the ray passes through it before it meets anything (stopAt metres along it): the kernel's pick at the
    /// ray's closest approach to the bubble, held within the ray's reach. A ray that starts inside the bubble (a first-person view in
    /// a carry) is on it; inside says so.
    /// </summary>
    internal string? BubbleOnRay(CommandHost.BubbleView bubble, Vector3 from, Vector3 to, float stopAt, out bool inside)
    {
        inside = false;
        var host = Host;
        if (host == null || !bubble.Selectable) return null;
        var ray = to - from;
        var length = ray.Length();
        if (length < 1e-6f || !float.IsFinite(length)) return null;
        var dir = ray / length;
        var pick = bubble.RadiusM + BubblePickSlackM;
        var offset = from - bubble.CenterM;
        var c = offset.LengthSquared() - pick * pick;
        if (c <= 0)
        {
            inside = true;
            return host.BubbleAt(from, BubblePickSlackM);
        }
        var b = offset.Dot(dir);
        var disc = b * b - c;
        if (b >= 0 || disc < 0) return null;
        var entry = -b - Mathf.Sqrt(disc);
        var reach = Mathf.Min(stopAt, length);
        if (entry > reach) return null;
        return host.BubbleAt(from + dir * Mathf.Clamp(-b, entry, reach), BubblePickSlackM);
    }

    /// <summary>The first right-click on a bubble: it is the one the smart ask steers, lit as a thing that can be carried is.</summary>
    public void SelectBubble(string? id)
    {
        if (id == null) { ClearBubbleSelection(); return; }
        if (Host?.ActiveBubble is not { Selectable: true } view || view.Id != id) return;
        var began = BubbleCost.Begin();
        SelectedBubble = id;
        _selectedWasLed = view.LedToM != null;
        _leadPreview = null;
        _askAge = double.MaxValue;
        Look?.SelectBubble(id);
        _noticeText = _chosenNotice = $"Bubble chosen: {K(Act.GubbleAsk)} a spot to lead it there (a higher spot lifts it), {K(Act.MouseRelease)} lets it go.";
        BubbleCost.End(began, BubbleCost.Piece.Choose);
    }

    /// <summary>Let the bubble go: Esc, a right-click on nothing, on the bubble again or on the Gubble, or its bubble gone.</summary>
    public void ClearBubbleSelection()
    {
        if (SelectedBubble == null) return;
        SelectedBubble = null;
        _selectedWasLed = false;
        _leadPreview = null;
        _askAge = double.MaxValue;
        Look?.SelectBubble(null);
        // The line that told how to lead it must not stand once it is let go (another notice that has replaced it stays).
        if (_chosenNotice.Length > 0 && _noticeText == _chosenNotice) _noticeText = BubbleLetGoWords;
        _chosenNotice = "";
    }

    /// <summary>The second right-click: lead the selected bubble to the spot, as the player's command; the Gubble's bubble answers when the lead ends.</summary>
    private void LeadSelectedBubble(Vector3 spot)
    {
        var host = Host;
        if (host == null) { _noticeText = $"The command host is not attached; {Companion.CompanionName}'s keys are off."; return; }
        if (SelectedBubble is not { } id) return;
        ClearChains();
        _cue.Think(GubbleMagic.BubbleIcon);
        var began = BubbleCost.Begin();
        var result = host.PlayerLead(id, spot);
        BubbleCost.End(began, BubbleCost.Piece.Lead);
        // A refusal shows its words and leaves the bubble chosen: another spot may do.
        if (!result["ok"]!.GetValue<bool>()) { Answer(result); return; }
        _leadIsLift = result["data"]?["lift"]?.GetValue<bool>() == true;
        _leadJob = result["job_id"]?.GetValue<string>();
        if (_leadJob == null) _cue.Done();
    }

    /// <summary>The reticle's words for a right-click that would lead the selected bubble to the aimed spot: asked of the host's preview.</summary>
    private string LeadWords(Vector3 spot)
    {
        var host = Host;
        if (host == null || SelectedBubble is not { } id) return "";
        var pips = PipBalance();
        var now = _bubbleClockS;
        if (_leadPreview is { } kept && _previewFor == id && kept.Pips == pips)
        {
            var samePlace = kept.Point.DistanceSquaredTo(spot) < LeadPreviewNearM * LeadPreviewNearM;
            if ((samePlace && now - kept.At < LeadPreviewS) || now - kept.At < LeadPreviewMinS) return kept.Words;
        }
        LeadPreviews++;
        var previewStart = BubbleCost.Begin();
        var result = host.PlayerLead(id, spot, preview: true);
        string words;
        bool ok = result["ok"]!.GetValue<bool>(), lift = false;
        var price = 0;
        if (ok)
        {
            lift = result["data"]?["lift"]?.GetValue<bool>() == true;
            price = result["data"]?["lift_price"]?.GetValue<int>() ?? 0;
            var tag = MeterMath.Tag(price, pips);
            words = lift ? $"{GubbleMagic.BubbleIcon} lift it there" + (tag.Shown ? " " + tag.Text : "") : $"{GubbleMagic.BubbleIcon} lead it there";
        }
        else
        {
            // A lift the pool cannot pay says so with its price, as the wheel does; any other refusal says its own words.
            var error = result["error"];
            if (error?["code"]?.GetValue<string>() == "energy_exhausted" && error["price"] is { } priced)
            {
                lift = true;
                price = priced.GetValue<int>();
                words = $"{GubbleMagic.BubbleIcon} lift it there {MeterMath.Needs(price)}";
            }
            else words = $"{GubbleMagic.RefusedMark} {GubbleMagic.ShortReason(error?["message"]?.GetValue<string>())}";
        }
        _previewFor = id;
        _leadPreview = (spot, pips, now, ok, lift, price, words);
        BubbleCost.End(previewStart, BubbleCost.Piece.Preview);
        return words;
    }

    /// <summary>The job of a lead the player set ended: the Gubble's bubble answers with the host's word (done, or why it failed).</summary>
    private bool LeadFinished(string jobId, string state)
    {
        if (_leadJob == null || jobId != _leadJob) return false;
        _leadJob = null;
        if (state == "succeeded") _cue.Done();
        else if (state == "failed") _cue.Refuse(JobFailureWords(jobId));
        else _cue.Clear();
        return true;
    }

    /// <summary>The host's own reason for a job that failed, from its record (jobs.status).</summary>
    private string? JobFailureWords(string jobId)
    {
        var status = Host?.HandleObject(CanonicalJson.Text(new JsonObject
        {
            ["schema"] = "enfractal.query", ["version"] = 1, ["query_id"] = "hud-ask-" + Guid.NewGuid().ToString("N")[..24],
            ["room_id"] = Host.Room.RoomId, ["op"] = "jobs.status", ["args"] = new JsonObject { ["job_id"] = jobId },
        }), CommandHost.PlayerPrincipal);
        return status?["data"]?["result"]?["error"]?["message"]?.GetValue<string>();
    }

    /// <summary>Whether a spot is a lift for a bubble the HUD did not lead itself: higher than the bubble can drift (BubbleLaws.LiftAboveDriftM), as the host reckons, near enough for a drawing.</summary>
    private static bool LooksLikeALift(CommandHost.BubbleView view, Vector3 spot) =>
        spot.Y - (view.CenterM.Y - view.RadiusM) > BubbleLaws.LiftAboveDriftM && view.Kind != "boat";

    /// <summary>Each frame: the selection goes with its bubble or its lead, and the look is told what to draw of the bubble the host has.</summary>
    private void UpdateBubble(double delta)
    {
        _bubbleClockS += delta;
        var view = Host?.ActiveBubble;
        if (SelectedBubble != null)
        {
            if (view is not { Selectable: true } kept || kept.Id != SelectedBubble) ClearBubbleSelection();
            else
            {
                // It was led and the lead has ended (arrived, popped, blocked, stopped): the choice is over.
                var led = kept.LedToM != null;
                if (_selectedWasLed && !led) ClearBubbleSelection();
                else _selectedWasLed = led;
            }
        }
        if (view is not { } bubble)
        {
            _leadIsLift = null;
            _bubbleSeen = null;
            return;
        }
        if (bubble.LedToM == null && !bubble.Lifting) _leadIsLift = null;
        if (Look == null || _bubbleSeen == bubble) return;
        _bubbleSeen = bubble;
        var form = bubble.Kind switch { "boat" => BubbleForm.Boat, "walk_in" => BubbleForm.WalkIn, _ => BubbleForm.Carry };
        var to = bubble.LedToM ?? Vector3.Zero;
        Look.SetBubbleState(bubble.Id, new BubbleDrawState(form, bubble.HoldsPlayer, bubble.LedToM != null, to, bubble.LedToM != null && (_leadIsLift ?? LooksLikeALift(bubble, to)), bubble.Lifting));
    }
}
