using System;
using System.Text.Json.Nodes;
using Godot;
using GarageGame.Native.Kernel;

namespace GarageGame.Native;

/// <summary>
/// Fire on the wheel, key 6 and the Magic panel (MECHANICS.md decisions 77, 82 and 83). Until the team holds first_fire the wedge and the
/// key show a lock and "Rest by the starting campfire first." and send nothing. Then a tap of 6 (or the wheel's Fire wedge) lights Fire
/// at the aimed spot at once: the host's PlayerFireLight sends the player's preview and the cast of exactly that preview back to back
/// (decision 83: no wait for a preview or a hold; casting it near yourself is a learning moment), and the outline the preview drew flashes
/// for FireFlashS as it lights. The place decides what the fire is, as for the Bubble: the host's preview says (data.kind) whether it
/// lights an unlit firepit, makes a Will-o'-wisp on water, is Snuff on something burning (once learned) or plain Fire. Once Fireline is
/// learned, holding 6 and moving the aim at least FirelineDragMinM before letting go is a drag: the line from where 6 went down to where
/// it came up (at most 3 m) is lit the same way. Creative's one ask about builds still holds the cast for the player's card. A spot out of
/// the Gubble's reach or the team's sight sends the Gubble closer first, as the other slots do; the host checks everything again on the
/// cast. A fire burns until it burns itself out or is Snuffed (decision 82): X stops the Gubble, never a fire.
/// </summary>
public partial class RoomHud
{
    /// <summary>A drag of key 6 makes a Fireline once the aim moved at least this between its press and release (provisional).</summary>
    public const float FirelineDragMinM = 0.3f;
    /// <summary>How long the lit fire's outline flashes, in seconds (provisional): long enough to see where it caught.</summary>
    public const double FireFlashS = 0.6;

    private Vector3? _fireKeyStart;
    /// <summary>A Fireline waiting for the Gubble to come closer: its two ends (the walk's own spot is the line's middle).</summary>
    private (Vector3 Start, Vector3 End)? _castAfterLine;
    private double _fireFlashAge = -1;

    /// <summary>Whether the lit fire's outline is flashing now (tests).</summary>
    public bool FireFlashing => _fireFlashAge >= 0;
    /// <summary>What the place made of the last Fire click: fire, firepit, fireline, snuff or will_o_wisp (the host's word; "" when refused).</summary>
    public string LastFireKind { get; private set; } = "";
    /// <summary>The last Fire click as the host answered it, and how many there have been this session (tests).</summary>
    public CommandHost.FireClick? LastFireClick { get; private set; }
    public int FireClicks { get; private set; }

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

    /// <summary>Key 6 came up: a drag (Fireline learned, the aim moved far enough) lights the line; anything else is a tap at the press's spot.</summary>
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
    /// sight, then it lights (UpdateCastAfter); otherwise it lights now.
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
        LightWheelFire(point, end);
    }

    /// <summary>
    /// The click lights it (decision 83): the host's preview and cast back to back. The preview's outline flashes as it lights; a refused
    /// preview is the Gubble's head-shake with the host's words; Creative's ask about builds is the host's card; lit, the Gubble gestures and
    /// the notice says what the place made of it.
    /// </summary>
    public CommandHost.FireClick? LightWheelFire(Vector3 point, Vector3? end = null)
    {
        if (Host is not { } host) return null;
        _cue.Think(GubbleMagic.FireIcon);
        var click = host.PlayerFireLight(point, end);
        var words = FireClickAnswer(click);
        if (click.Lit) Companion.CastGesture(point, "fire");
        if (words != null) _noticeText = words;
        return click;
    }

    /// <summary>
    /// What a Fire click answered, on screen, for the wheel, key 6 and the Magic panel alike: the outline flashed where the preview drew it,
    /// the Gubble's bubble (done, or the host's refusal), and the words for the notice (null when the refusal already said them).
    /// </summary>
    private string? FireClickAnswer(CommandHost.FireClick click)
    {
        LastFireClick = click;
        LastFireKind = click.Kind;
        FireClicks++;
        if (!click.Previewed) { Answer(click.Result); return null; }
        FlashFireOutline(click.Preview["data"] as JsonObject);
        // The one ask (Creative: may your builds burn?) is the host's card: the cast is not refused, it waits for the answer.
        if (click.Held)
        {
            _cue.Clear();
            return FireHeldWords;
        }
        if (!Answer(click.Result, wiggle: false)) return null;
        return FireLitWords(click.Kind) + (PlayerInFire(click.Preview["data"] as JsonObject) ? " " + FireAroundYouWords : "");
    }

    /// <summary>The notice while Creative's card asks about the builds a fire would reach.</summary>
    public const string FireHeldWords = "Your builds are in reach: answer the card, and the fire is lit as you choose.";
    /// <summary>Said after a fire lit round the player (decision 83: casting it near yourself is a learning moment).</summary>
    public const string FireAroundYouWords = "You are standing in its circle: step out of the flames!";

    /// <summary>What a lit Fire did, by what the place made of it (decision 82: a fire burns until it burns out or is Snuffed).</summary>
    public static string FireLitWords(string kind) => kind switch
    {
        FireKindWords.Firepit => "The firepit is lit: a campfire to come back to.",
        FireKindWords.Snuff => "Snuffed out. The ground stays damp for a while.",
        FireKindWords.Wisp => "A Will-o'-wisp hovers over the water: a cold flame that burns nothing.",
        FireKindWords.Fireline => "The line is alight. It burns until it burns itself out, or Snuff puts it out.",
        _ => "The fire is lit. It burns until it burns itself out, or Snuff puts it out.",
    };

    /// <summary>Whether the preview found the player's own body inside the fire's reach (its avatars_in_area, as the player perceives them).</summary>
    private static bool PlayerInFire(JsonObject? data)
    {
        if (data?["avatars_in_area"] is not JsonArray inside) return false;
        foreach (var id in inside)
            if (id?.GetValue<string>() == CommandHost.PlayerAvatar) return true;
        return false;
    }

    /// <summary>The preview's outline (data.center_m, radius_m, end_m) drawn for FireFlashS, then gone: the fire itself is the look's.</summary>
    private void FlashFireOutline(JsonObject? data)
    {
        if (data == null || ReadVector(data["center_m"]) is not { } center) return;
        var radius = data["radius_m"]?.GetValue<float>() ?? 0.25f;
        if (ReadVector(data["end_m"]) is { } end) NatureVisual?.ShowFireLinePreview(center, end, FireKindLaws.FirelineHalfWidthM);
        else NatureVisual?.ShowFirePreview(center, radius);
        _fireFlashAge = 0;
    }

    /// <summary>Each frame: the flash's clock; at FireFlashS the outline goes.</summary>
    private void TickFireFlash(double delta)
    {
        if (_fireFlashAge < 0) return;
        _fireFlashAge += Math.Clamp(delta, 0, 0.1);
        if (_fireFlashAge < FireFlashS) return;
        _fireFlashAge = -1;
        NatureVisual?.HideFirePreview();
    }

    /// <summary>A JSON [x, y, z] as a vector, or null.</summary>
    private static Vector3? ReadVector(JsonNode? node) =>
        node is JsonArray { Count: 3 } xyz ? new Vector3(xyz[0]!.GetValue<float>(), xyz[1]!.GetValue<float>(), xyz[2]!.GetValue<float>()) : null;
}
