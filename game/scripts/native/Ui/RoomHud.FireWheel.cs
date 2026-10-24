using System;
using System.Text.Json.Nodes;
using Godot;
using GarageGame.Native.Kernel;

namespace GarageGame.Native;

/// <summary>
/// Fire on the wheel and key 6 (MECHANICS.md decision 77). Until the team holds first_fire the wedge and the key show a lock and
/// "Rest by the starting campfire first." and send nothing. Then a tap of 6 (or the wheel's Fire wedge) previews the outlined fire at the
/// aimed spot, and a hold of the ask button for FireConfirmSeconds lights exactly that preview: the Magic panel's preview-and-hold, the
/// same command path (nature.cast, previewed, then cast naming the preview) and the same confirm time. The place decides what the
/// fire is, as for the Bubble: the host's preview says (data.kind) whether it lights an unlit firepit, makes a Will-o'-wisp on water,
/// is Snuff on something burning (once learned) or plain Fire, and the outline follows. Once Fireline is learned, holding 6 and moving
/// the aim at least FirelineDragMinM before letting go is a drag: the line from where 6 went down to where it came up (at most 3 m)
/// is previewed and held to light. Esc, a new preview, or the panels call the preview off. A spot out of the Gubble's reach or the
/// team's sight sends the Gubble closer first, as the other slots do; the host checks everything again on the cast.
/// </summary>
public partial class RoomHud
{
    /// <summary>A drag of key 6 makes a Fireline once the aim moved at least this between its press and release (provisional).</summary>
    public const float FirelineDragMinM = 0.3f;

    private Vector3? _fireKeyStart;
    private string? _wheelFireId;
    private Vector3 _wheelFirePoint;
    private Vector3? _wheelFireEnd;
    private string _wheelFireKind = "";
    private bool _wheelFireHolding;
    private double _wheelFireHoldAge;
    /// <summary>A Fireline waiting for the Gubble to come closer: its two ends (the walk's own spot is the line's middle).</summary>
    private (Vector3 Start, Vector3 End)? _castAfterLine;

    /// <summary>Whether a wheel or key-6 Fire preview is waiting for the hold (tests).</summary>
    public bool WheelFireArmed => _wheelFireId != null;
    /// <summary>What the place made of the armed preview: fire, firepit, fireline, snuff or will_o_wisp (the host's word).</summary>
    public string WheelFireKind => _wheelFireKind;
    /// <summary>The hold's progress toward lighting, 0 to 1.</summary>
    public double WheelFireProgress => Math.Clamp(_wheelFireHoldAge / FireConfirmSeconds, 0, 1);
    /// <summary>Whether the ask button's hold is lighting the armed preview now.</summary>
    public bool WheelFireHolding => _wheelFireHolding;

    /// <summary>Fire's slot as the island has it, or null (no Fire on this island).</summary>
    private IslandAbility? FireSlotAbility => SlotAbility(GubbleMagic.FireSlot);

    /// <summary>The lock's words for a slot: Fire's until the first rest (decision 77), none otherwise.</summary>
    private string? SlotLockedWords(int slot) => slot == GubbleMagic.FireSlot && Host is { } host && !host.FireLearned ? GubbleMagic.FireLockedWords : null;

    /// <summary>Key 6 went down: where the aim is now is where a drag starts.</summary>
    private void FireKeyDown()
    {
        var aim = AimNow();
        _fireKeyStart = aim.Hit && !aim.AtGubble ? aim.Point : null;
        if (FireSlotAbility == null || SlotLockedWords(GubbleMagic.FireSlot) != null || _fireKeyStart == null) { CastSlot(GubbleMagic.FireSlot, aim); _fireKeyStart = null; }
    }

    /// <summary>Key 6 came up: a drag (Fireline learned, the aim moved far enough) previews the line; anything else is a tap at the press's spot.</summary>
    private void FireKeyUp()
    {
        if (_fireKeyStart is not { } start) return;
        _fireKeyStart = null;
        var aim = AimNow();
        if (Host is { FirelineLearned: true } && aim.Hit && !aim.AtGubble && aim.Point.DistanceTo(start) >= FirelineDragMinM)
        {
            var end = start + (aim.Point - start).LimitLength(FireKindLaws.FirelineMaxM);
            CastFireAt(start, end);
            return;
        }
        CastSlot(GubbleMagic.FireSlot, new GubbleMagic.Aim(true, start, null, false));
    }

    /// <summary>
    /// Fire at a spot, or along a line: the Gubble comes closer first when the spot (a line's middle) is out of its reach or the team's
    /// sight, then the preview (UpdateCastAfter); otherwise the preview now.
    /// </summary>
    private void CastFireAt(Vector3 point, Vector3? end)
    {
        var host = Host;
        if (host == null || FireSlotAbility is not { } fire) return;
        ClearChains();
        _cue.Think(GubbleMagic.FireIcon);
        var spot = end is { } to ? (point + to) * 0.5f : point;
        if (host.EffectSpot(fire.Capability, spot) is { } place && (!place.InReach || !place.InSight))
        {
            var result = host.PlayerGoal("go_to", spot);
            if (!result["ok"]!.GetValue<bool>()) { Answer(result); return; }
            _goalFailed = false;
            _castCheckAge = 0;
            _castAfter = (Companion.IntentSerial, GubbleMagic.FireSlot, fire.Capability, spot);
            _castAfterLine = end is { } lineEnd ? (point, lineEnd) : null;
            return;
        }
        PreviewWheelFire(point, end);
    }

    /// <summary>The preview: the host says what the place makes of it; the outline is drawn and the hold waits. A refusal is the Gubble's head-shake.</summary>
    public void PreviewWheelFire(Vector3 point, Vector3? end = null)
    {
        CancelWheelFire();
        if (Host is not { } host) return;
        var result = host.PlayerFirePreview(point, end);
        if (result["ok"]?.GetValue<bool>() != true) { Answer(result); return; }
        var data = result["data"];
        _wheelFireId = data?["preview_id"]?.GetValue<string>();
        if (_wheelFireId == null) return;
        _wheelFirePoint = point;
        _wheelFireEnd = end;
        _wheelFireKind = data?["kind"]?.GetValue<string>() ?? FireKindWords.Fire;
        var radius = data?["radius_m"]?.GetValue<float>() ?? 1.5f;
        if (end is { } to) NatureVisual?.ShowFireLinePreview(point, to, FireKindLaws.FirelineHalfWidthM);
        else NatureVisual?.ShowFirePreview(point, radius);
        _cue.Think(GubbleMagic.FireIcon);
        var approval = data?["requires_approval"]?.GetValue<bool>() == true;
        _noticeText = WheelFireWords(_wheelFireKind, data?["cell_count"]?.GetValue<int>() ?? 0, data?["affected_ids"]?.AsArray().Count ?? 0, radius, approval);
    }

    /// <summary>The preview's words: what the place made of it, and the hold that lights it.</summary>
    private string WheelFireWords(string kind, int cells, int affected, float radius, bool approval)
    {
        var hold = $"Hold {K(Act.GubbleAsk)} to light it; {K(Act.MouseRelease)} calls it off.";
        var what = kind switch
        {
            FireKindWords.Firepit => "Fire will light this firepit: it becomes a campfire.",
            FireKindWords.Snuff => $"Snuff puts out everything burning within {radius:0.#} m, and the ground stays damp for a while.",
            FireKindWords.Wisp => "A Will-o'-wisp will hover over the water: a cold flame that burns nothing.",
            FireKindWords.Fireline => $"The whole line will catch at once. {affected} {(affected == 1 ? "thing" : "things")} may be affected.",
            _ => $"Fire stays within the outlined {radius:0.##} m. {affected} {(affected == 1 ? "thing" : "things")} may be affected.",
        };
        if (kind == FireKindWords.Snuff) hold = $"Hold {K(Act.GubbleAsk)} to put it out; {K(Act.MouseRelease)} calls it off.";
        if (kind == FireKindWords.Wisp) hold = $"Hold {K(Act.GubbleAsk)} to set it; {K(Act.MouseRelease)} calls it off.";
        return what + (approval ? " You will be asked first." : "") + " " + hold;
    }

    /// <summary>The ask button went down while a preview waits: the hold starts (instead of the wheel).</summary>
    internal void BeginWheelFireHold()
    {
        if (_wheelFireId == null) return;
        _wheelFireHolding = true;
        _wheelFireHoldAge = 0;
    }

    /// <summary>The ask button came up before the hold finished: the hold stops, the preview stays.</summary>
    internal void EndWheelFireHold()
    {
        _wheelFireHolding = false;
        _wheelFireHoldAge = 0;
    }

    /// <summary>The preview called off (Esc, a new preview, a panel): its outline goes.</summary>
    private void CancelWheelFire()
    {
        if (_wheelFireId == null && !_wheelFireHolding) return;
        _wheelFireId = null;
        _wheelFireEnd = null;
        _wheelFireKind = "";
        EndWheelFireHold();
        NatureVisual?.HideFirePreview();
    }

    /// <summary>Each frame: the hold's clock, and at FireConfirmSeconds the cast of exactly the preview.</summary>
    private void TickWheelFire(double delta)
    {
        if (!_wheelFireHolding || _wheelFireId == null) return;
        if (ReadingPanelOpen) { CancelWheelFire(); return; }
        _wheelFireHoldAge += Math.Clamp(delta, 0, .1);
        if (_wheelFireHoldAge < FireConfirmSeconds) return;
        var token = _wheelFireId;
        var point = _wheelFirePoint;
        var end = _wheelFireEnd;
        var kind = _wheelFireKind;
        CancelWheelFire();
        if (Host is not { } host) return;
        _cue.Think(GubbleMagic.FireIcon);
        var result = host.PlayerFireCast(point, end, token);
        // The one ask (Creative: may your builds burn?) is the host's card: the hold is not refused, it waits for the answer.
        if (result["error"]?["code"]?.GetValue<string>() == "approval_required")
        {
            _cue.Clear();
            _noticeText = "Your builds are in reach: answer the card, and the fire is lit as you choose.";
            return;
        }
        if (!Answer(result, wiggle: false)) return;
        Companion.CastGesture(point, "fire");
        _noticeText = kind switch
        {
            FireKindWords.Firepit => "The firepit is lit: a campfire to come back to.",
            FireKindWords.Snuff => "Snuffed out.",
            FireKindWords.Wisp => "A Will-o'-wisp hovers over the water.",
            FireKindWords.Fireline => "The line is alight. Rain and wind will shape it.",
            _ => "The fire is lit. Rain and wind will shape it.",
        };
    }

    /// <summary>The ask tag while a preview waits: the hold's words and how far it has come.</summary>
    private string WheelFireTag()
    {
        var filled = (int)Math.Round(WheelFireProgress * 8);
        return $"{K(Act.GubbleAsk)} hold: {GubbleMagic.FireIcon} " + new string('▮', filled) + new string('▯', 8 - filled);
    }
}
