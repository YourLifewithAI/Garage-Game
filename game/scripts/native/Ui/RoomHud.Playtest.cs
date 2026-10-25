using Godot;
using System.Collections.Generic;
using GarageGame.Native.Playtest;

namespace GarageGame.Native;

/// <summary>
/// Text the screen showed the player, the moment it changed (the playtest record's notice): the HUD's notice line (hud), the Gubble's
/// bubble and the reason beside it (gubble), a refusal shown (refusal) with the host's code and the command's action id when known.
/// An empty text is the line cleared.
/// </summary>
public readonly record struct ScreenNotice(string Channel, string Text, string Reason = "", string Code = "", string ActionId = "");

/// <summary>
/// The HUD's part in the playtest record (docs/runs/RUN-2-PLAYTEST-LOOP.md, R1; Playtest/PlaytestRecorder): the keys it sees and the
/// words it shows, as events the recorder listens to, and the record's four keys, live in every context, the panels and the Gubble's
/// eyes included, since what is off may be on a panel: F9 "something's off here" and F8 "keep this" (a still and a mark, the screen
/// answering "Mark 3" or "Keep 2"), F7 and F6 the steady stills faster and slower (off, 30, 10, 3, 1 s, the screen saying the beat).
/// With no recorder (headless, --no-playtest-record) the keys say the record is off. Never typed text: a key a text box takes never
/// reaches here, and only an action's name is reported.
/// </summary>
public partial class RoomHud
{
    /// <summary>An input action the HUD saw pressed, by its name in the input map, and what it chose or where it was pressed ("" for nothing).</summary>
    public event System.Action<string, string>? ActionHandled;
    /// <summary>Text the screen showed, the moment it changed.</summary>
    public event System.Action<ScreenNotice>? NoticeShown;

    /// <summary>Read every tick or pressed by the dozen: the breadcrumb covers movement, and the view's zoom and drag are not choices.</summary>
    private static readonly HashSet<StringName> UnrecordedActions = new()
    {
        Act.MoveForward, Act.MoveBack, Act.MoveLeft, Act.MoveRight, Act.MoveSprint, Act.MoveDive, Act.Jump,
        Act.ViewZoomIn, Act.ViewZoomOut, Act.LookDrag,
    };

    /// <summary>The room's playtest recorder (null when this launch does not record), found again when it changed.</summary>
    private PlaytestRecorder? Recorder
    {
        get
        {
            var parent = GetParent();
            if (_recorder != null && IsInstanceValid(_recorder) && parent != null && _recorder.GetParent() == parent) return _recorder;
            return _recorder = parent != null ? PlaytestRecorder.Of(parent) : null;
        }
    }
    private PlaytestRecorder? _recorder;

    /// <summary>The Gubble's bubble and its refusals, as notices (called once the bubble exists).</summary>
    private void WatchForPlaytest()
    {
        _cue.Shown += (text, reason) => NoticeShown?.Invoke(new ScreenNotice("gubble", text, reason));
        _cue.Refused += (reason, code, actionId) => NoticeShown?.Invoke(new ScreenNotice("refusal", reason, "", code, actionId));
    }

    /// <summary>The notice line shows new words (RoomHud._Process).</summary>
    private void NoticeLineShown(string text) => NoticeShown?.Invoke(new ScreenNotice("hud", text));

    /// <summary>Every action a fresh press is (but movement, the zoom and the drag), reported before anything handles it.</summary>
    private void RecordActions(InputEvent press)
    {
        if (ActionHandled == null) return;
        foreach (var spec in PlayerControls.Specs)
        {
            if (UnrecordedActions.Contains(spec.Action) || !press.IsActionPressed(spec.Action)) continue;
            // Shift+T is the season, not also the hour (PlayerControls.CheckedBefore).
            var nested = false;
            foreach (var (first, second) in PlayerControls.CheckedBefore)
                if (second == spec.Action && press.IsActionPressed(first)) nested = true;
            if (!nested) ActionHandled(spec.Action.ToString(), ActionDetail(spec.Action));
        }
    }

    /// <summary>What an action chose (the view, the slot's ability), or where it was pressed when a panel holds the keys.</summary>
    private string ActionDetail(StringName action)
    {
        if (action == Act.ViewEye) return "eye";
        if (action == Act.ViewShoulder) return "shoulder";
        // Reported before the key is handled: where the window is going.
        if (action == Act.ViewFullscreen) return Fullscreen ? "to a window" : "to full screen";
        for (var i = 0; i < Act.GubbleSlots.Length; i++)
            if (action == Act.GubbleSlots[i]) return SlotName(i);
        // E: what its prompt said it would do (RoomHud.Interact.cs).
        if (action == Act.Interact) return InteractDetail();
        if (_wheel.Held) return "while the wheel is open";
        if (GubbleViewOpen) return "through the Gubble's eyes";
        if (JournalOpen) return "with the journal open";
        if (MagicPanelOpen) return "with the Magic panel open";
        if (PocketsPanelOpen) return "with the pockets open";
        if (SettingsOpen) return "with the settings panel open";
        return "";
    }

    /// <summary>The ask button's release: what the tap asked for, or the wheel's wedge.</summary>
    private void RecordAsk(GubbleWheel.Outcome outcome, int wedge, GubbleMagic.Ask frozen)
    {
        var detail = outcome switch
        {
            GubbleWheel.Outcome.Tap => "tap: " + frozen.Rule.ToString().ToLowerInvariant(),
            GubbleWheel.Outcome.Wedge when wedge >= 0 && wedge < GubbleMagic.Wedges.Length => "wheel: " + GubbleMagic.Wedges[wedge].Id,
            _ => null,
        };
        if (detail != null) ActionHandled?.Invoke(Act.GubbleAsk.ToString(), detail);
    }

    /// <summary>The record's four keys, before every other (true when the press was one of them). The screen answers each.</summary>
    private bool HandlePlaytestKeys(InputEvent press)
    {
        string? words;
        if (PlayerControls.Hit(press, Act.PlaytestMark)) words = Recorder?.Mark("off");
        else if (PlayerControls.Hit(press, Act.PlaytestKeep)) words = Recorder?.Mark("keep");
        else if (PlayerControls.Hit(press, Act.PlaytestStillsFaster)) words = Recorder?.StepBeat(+1);
        else if (PlayerControls.Hit(press, Act.PlaytestStillsSlower)) words = Recorder?.StepBeat(-1);
        else return false;
        _noticeText = words ?? PlaytestRecorder.NotRecordingWords;
        return true;
    }

    /// <summary>The steady beat for the key help (-1 when this launch does not record).</summary>
    private int PlaytestBeat() => Recorder is { Recording: true, Finished: false } recorder ? recorder.BeatS : -1;

    /// <summary>The key help's line for the record's keys, with the beat now.</summary>
    private string PlaytestKeysLine()
    {
        var beat = PlaytestBeat();
        var now = beat < 0 ? "the record is off for this run" : "now " + PlaytestRecorder.BeatWords(beat).ToLowerInvariant();
        return $"Playtest record: {K(Act.PlaytestMark)} something's off here · {K(Act.PlaytestKeep)} keep this · {K(Act.PlaytestStillsFaster)} stills faster · {K(Act.PlaytestStillsSlower)} slower ({now})";
    }
}
