using System.Text.Json.Nodes;
using Godot;
using GarageGame.Native.Kernel;
using GarageGame.Native.Ui;

namespace GarageGame.Native;

/// <summary>
/// Grow's drag on key 2 (MECHANICS.md decisions 76 and 106; docs/magic/GROW-UNLOCKS.md), as 6's drag is a Fireline (RoomHud.FireWheel.cs):
/// hold 2 and sweep the aim from one lip of a gap to the other, and the line from where 2 went down to where it came up (at most the
/// bridge's 1.2 m) is a bridge of plants. A tap is Grow at the press's spot, as it always was; the place decides what a tap makes (sprouts,
/// a replanting, ivy) and the host's refusal, in its own words, is what a tap of a kind not yet learned says. A drag before the bridge is
/// opened (three of Grow's expertise) sends nothing: it says the host's lock words, as 6 says its own before the first rest. The Gubble
/// comes closer first when the bridge's middle is out of its reach or the team's sight, then the host's preview (what the place makes of
/// it, the lips as center_m and end_m) and the cast of the same line go back to back, the outline of the preview flashing as it grows.
/// </summary>
public partial class RoomHud
{
    /// <summary>A drag of key 2 makes a bridge once the aim moved at least this between its press and release (provisional, as Fireline's).</summary>
    public const float GrowDragMinM = 0.3f;

    private Vector3? _growKeyStart;

    /// <summary>Whether key 2 is down waiting for the drag's end (tests).</summary>
    public bool GrowKeyHeld => _growKeyStart != null;

    /// <summary>Key 2 went down: where the aim is now is where a drag starts. Nothing to drag from, or no Grow here: the press is the cast, as it was.</summary>
    private void GrowKeyDown()
    {
        var aim = AimNow();
        _growKeyStart = aim.Hit && !aim.AtGubble ? aim.Point : null;
        if (SlotAbility(GubbleMagic.GrowSlot) is not { Capability: "grow" } || _growKeyStart == null)
        {
            CastSlot(GubbleMagic.GrowSlot, aim);
            _growKeyStart = null;
        }
    }

    /// <summary>Key 2 came up: the aim moved far enough is a bridge from the press's spot; anything else is a tap there.</summary>
    private void GrowKeyUp()
    {
        if (_growKeyStart is not { } start) return;
        _growKeyStart = null;
        var aim = AimNow();
        if (aim.Hit && !aim.AtGubble && aim.Point.DistanceTo(start) >= GrowDragMinM)
        {
            GrowDrag(start, aim.Point);
            return;
        }
        CastSlot(GubbleMagic.GrowSlot, new GubbleMagic.Aim(true, start, null, false));
    }

    /// <summary>
    /// A drag from lip to lip: locked, the host's lock words and nothing sent; open, the Gubble comes closer first when it must (the line's
    /// middle is the walk's spot, as for a Fireline), then the bridge is cast. The end is at most the bridge's length from the start.
    /// </summary>
    public void GrowDrag(Vector3 start, Vector3 pointer)
    {
        var host = Host;
        if (host == null || SlotAbility(GubbleMagic.GrowSlot) is not { } grow) return;
        var icon = GubbleMagic.SlotIcons[GubbleMagic.GrowSlot];
        if (host.GrowLockedWords(GrowKindWords.Bridge) is { } locked)
        {
            _cue.Shrug(icon, locked);
            _noticeText = locked;
            return;
        }
        var end = start + (pointer - start).LimitLength(GrowKindLaws.BridgeMaxM);
        ClearChains();
        _cue.Think(icon);
        // The host wants both lips and the middle within the Gubble's reach: the walk goes to the line's middle, and the walk stops
        // (UpdateCastAfter) once the lip that is farthest from it is inside the reach (the middle is never farther than the farther lip).
        var spot = (start + end) * 0.5f;
        var farLip = Companion.GlobalPosition.DistanceTo(start) >= Companion.GlobalPosition.DistanceTo(end) ? start : end;
        if (host.EffectSpot(grow.Capability, farLip) is { } place && (!place.InReach || !place.InSight))
        {
            var result = host.PlayerGoal("go_to", spot);
            if (!result["ok"]!.GetValue<bool>()) { Answer(result); return; }
            _goalFailed = false;
            _castCheckAge = 0;
            _castAfter = (Companion.IntentSerial, GubbleMagic.GrowSlot, grow.Capability, farLip);
            _castAfterLine = (start, end);
            return;
        }
        CastBridge(start, end);
    }

    /// <summary>
    /// The bridge cast: the host's preview of the line (what the place makes of it; its lips as center_m and end_m) and the cast of the same
    /// line back to back, the preview's outline flashing as it grows. A refused preview is the Gubble's head-shake in the host's words
    /// (not a gap, not level, too short) and nothing is cast. Returns the cast's result (or the refused preview).
    /// </summary>
    public JsonObject? CastBridge(Vector3 start, Vector3 end)
    {
        if (Host is not { } host) return null;
        var icon = GubbleMagic.SlotIcons[GubbleMagic.GrowSlot];
        _cue.Think(icon);
        var preview = host.PlayerGrowPreview(start, end);
        if (preview["ok"]?.GetValue<bool>() != true) { Answer(preview); return preview; }
        var data = preview["data"] as JsonObject;
        FlashGrowOutline(data);
        var result = host.PlayerGrowCast(start, end);
        if (!Answer(result, wiggle: false)) return result;
        Companion.CastGesture(start, "growth");
        _noticeText = GrowLocks.DoneWords(data?["kind"]?.GetValue<string>()) ?? "The bridge is growing.";
        return result;
    }

    /// <summary>The preview's outline of a bridge (data.center_m to data.end_m, the deck's width) drawn for FireFlashS, as a Fireline's: the plants themselves are the look's.</summary>
    private void FlashGrowOutline(JsonObject? data)
    {
        if (data == null || ReadVector(data["center_m"]) is not { } center || ReadVector(data["end_m"]) is not { } end) return;
        NatureVisual?.ShowFireLinePreview(center, end, GrowKindLaws.BridgeWidthM * 0.5f);
        _fireFlashAge = 0;
    }

    /// <summary>Test seam: the outline a preview's data would draw (the suite has no gap for the host to accept a bridge over).</summary>
    internal void FlashGrowOutlineForTests(JsonObject data) => FlashGrowOutline(data);
}
