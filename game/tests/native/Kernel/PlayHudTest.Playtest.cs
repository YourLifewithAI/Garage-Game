using Godot;
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Playtest;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The playtest record's keys on the HUD (docs/runs/RUN-2-PLAYTEST-LOOP.md, R1): with the record off they say so; with it on, F9 and
/// F8 write a still and a mark and the screen answers "Mark 1" or "Keep 1", in every context (the journal, the appearance panel);
/// F7 and F6 step the beat through off, 30, 10, 3 and 1 s and say so; the key help lists the four keys with the beat; a remapped key
/// marks and the help follows it; the HUD's actions and notices land in the record as events.
/// </summary>
public partial class PlayHudTest
{
    private const string PlaytestRoot = "user://tests/play_hud/playtest";

    private string NoticeText() => ((Label)_hud.FindChild("Notice", true, false)!).Text;
    private string PlaytestHelp() => _hud.GetNode<PanelContainer>("HelpFooter").GetChild<VBoxContainer>(0).GetNode<VBoxContainer>("KeyHelp").GetNode<Label>("PlaytestKeys").Text;

    private async Task TestPlaytestKeys()
    {
        // The game booted headless: the one line in RoomWorld (when wired) attached nothing, and the keys say the record is off.
        _hud._UnhandledInput(Press(Key.F9));
        await Frames(2);
        Check(PlaytestRecorder.Of(_world) == null && NoticeText() == PlaytestRecorder.NotRecordingWords, "headless, F9 says the record is off: " + NoticeText());
        Check(PlaytestHelp() == "Playtest record: F9 something's off here · F8 keep this · F7 stills faster · F6 slower (the record is off for this run)",
            "and the key help lists the four keys, the record off: " + PlaytestHelp());

        PlaytestRecorder.RootOverride = PlaytestRoot;
        PlaytestRecorder.DisplayServerOverride = "Windows";
        PlaytestRecorder.ArgumentsOverride = new[] { "--build=hud0001" };
        PlaytestRecorder.GrabForTests = () =>
        {
            var image = Image.CreateEmpty(256, 144, false, Image.Format.Rgba8);
            image.Fill(new Color(0.5f, 0.6f, 0.3f));
            return image;
        };
        var recorder = PlaytestRecorder.Attach(_world);
        PlaytestRecorder.ArgumentsOverride = null;
        PlaytestRecorder.DisplayServerOverride = null;
        Check(recorder is { Recording: true }, "a windowed launch's recorder on the HUD's world");
        if (recorder == null) return;
        SaveMap();
        try
        {
            await Frames(2);
            Check(PlaytestHelp() == "Playtest record: F9 something's off here · F8 keep this · F7 stills faster · F6 slower (now stills every 10 s)", "the help shows the beat: " + PlaytestHelp());
            _hud._UnhandledInput(Press(Key.F9));
            await Frames(2);
            Check(NoticeText() == "Mark 1" && recorder.Marks == 1, "F9: a mark, and the screen answers Mark 1: " + NoticeText());
            _hud._UnhandledInput(Press(Key.F8));
            await Frames(2);
            Check(NoticeText() == "Keep 1" && recorder.Marks == 2, "F8: a keep, and the screen answers Keep 1: " + NoticeText());

            var beats = new System.Collections.Generic.List<(int Beat, string Words)>();
            foreach (var key in new[] { Key.F7, Key.F7, Key.F7, Key.F6, Key.F6, Key.F6, Key.F6, Key.F6 })
            {
                _hud._UnhandledInput(Press(key));
                await Frames(2);
                beats.Add((recorder.BeatS, NoticeText()));
            }
            Check(beats.SequenceEqual(new[]
                {
                    (3, "Stills every 3 s"), (1, "Stills every 1 s"), (1, "Stills every 1 s, the fastest."), (3, "Stills every 3 s"), (10, "Stills every 10 s"),
                    (30, "Stills every 30 s"), (0, "Stills off"), (0, "Stills are already off."),
                }), "F7 and F6 step the beat through 10, 3, 1 and back through 3, 10, 30 and off, and the screen says each: " + string.Join("; ", beats));
            Check(PlaytestHelp().EndsWith("(now stills off)", StringComparison.Ordinal), "the help follows the beat: " + PlaytestHelp());

            // In every context: with the journal open, then the appearance panel.
            _hud._UnhandledInput(Press(Key.J));
            await Frames(30);
            var journal = _hud.JournalOpen;
            _hud._UnhandledInput(Press(Key.F9));
            Check(journal && _hud.JournalOpen && recorder.Marks == 3, "F9 marks with the journal open, and the journal stays open");
            _hud._UnhandledInput(Press(Key.J));
            await Frames(60);
            _hud._UnhandledInput(Press(Key.C));
            await Frames(2);
            _hud._UnhandledInput(Press(Key.F8));
            Check(_hud.SettingsOpen && recorder.Marks == 4, "F8 keeps with the appearance panel open, and the panel stays open");
            _hud._UnhandledInput(Press(Key.C));
            await Frames(2);

            // The keys are physical-key actions the player can remap; the help follows.
            Bind(Act.PlaytestMark, Key.Kp1);
            await Frames(2);
            _hud._UnhandledInput(Press(Key.F9));
            var unchanged = recorder.Marks;
            _hud._UnhandledInput(Press(Key.Kp1));
            await Frames(2);
            Check(unchanged == 4 && recorder.Marks == 5 && NoticeText() == "Mark 3" && PlaytestHelp().StartsWith("Playtest record: Kp 1 something's off here", StringComparison.Ordinal),
                "remapped to Kp 1: F9 does nothing, Kp 1 marks, and the help says Kp 1: " + PlaytestHelp());
            RestoreMap();

            // The HUD's actions and notices as events.
            _hud._UnhandledInput(Press(Key.F1));
            _hud._UnhandledInput(Press(Key.X));
            await Frames(10);
            _hud._UnhandledInput(Press(Key.F2));
            await Frames(2);
            // The full-screen key and the swim hint (PlayHudTest.Screen.cs) reach the record as the others do.
            _hud._UnhandledInput(Press(Key.F11));
            await Frames(2);
            _hud._UnhandledInput(Press(Key.F11));
            await Frames(2);
            await StandForViews(FreeSpot, 0.4f, 0f);
            await AddWater();
            await RemoveWater();
            recorder.Finish(quitStill: false);
            var events = PlaytestRecords.Events(recorder.Folder);
            var marks = events.Kind("mark").Select(m => (m.Str("flag"), m["number"]!.GetValue<int>())).ToList();
            Check(marks.SequenceEqual(new[] { ("off", 1), ("keep", 1), ("off", 2), ("keep", 2), ("off", 3) }), "five marks, each flag counted on its own: " + string.Join(", ", marks));
            Check(events.Kind("mark").All(m => events.Kind("still").Any(s => s.Str("file") == m.Str("still") && s["width"]!.GetValue<int>() == 256)),
                "each with its full-size still");
            var rates = events.Kind("stills_rate").Select(r => r["beat_s"]!.GetValue<int>()).ToList();
            Check(rates.SequenceEqual(new[] { 3, 1, 3, 10, 30, 0 }), "the beat's six moves are stills_rate events, none where it could not move: " + string.Join(", ", rates));
            var actions = events.Kind("action").Select(a => a.Str("name")).ToList();
            Check(actions.Count(a => a == "playtest_mark") == 3 && actions.Count(a => a == "playtest_keep") == 2 && actions.Count(a => a == "playtest_stills_faster") == 3 &&
                  actions.Count(a => a == "playtest_stills_slower") == 5 && actions.Contains("journal") && actions.Contains("hud_customize") && actions.Contains("view_eye") &&
                  actions.Contains("gubble_stop") && events.Kind("action").Any(a => a.Str("name") == "playtest_mark" && a.Str("detail") == "with the journal open"),
                "the HUD's actions land as events by their map names, with where they were pressed: " + string.Join(", ", actions));
            var notices = events.Kind("notice").ToList();
            Check(notices.Any(n => n.Str("channel") == "hud" && n.Str("text") == "Mark 1") && notices.Any(n => n.Str("channel") == "hud" && n.Str("text") == "Stills are already off.") &&
                  notices.Any(n => n.Str("channel") == "gubble" && n.Str("text").Length > 0),
                "and its notices: the line's words and the Gubble's bubble, the moment they changed");
            var screenActions = events.Kind("action").Where(a => a.Str("name") == "view_fullscreen").Select(a => a.Str("detail")).ToList();
            Check(screenActions.SequenceEqual(new[] { "to full screen", "to a window" }) &&
                  notices.Any(n => n.Str("channel") == "hud" && n.Str("text") == "Full screen: F11 goes back to a window.") &&
                  notices.Any(n => n.Str("channel") == "hud" && n.Str("text") == "A window again: F11 goes full screen."),
                "the full-screen key lands as an action with where it goes, and its two notices as the line's words: " + string.Join(", ", screenActions));
            var hudTexts = notices.Where(n => n.Str("channel") == "hud").Select(n => n.Str("text")).ToList();
            var hintAt = hudTexts.IndexOf("Deep water: Space leaps · hold Ctrl to dive · B home");
            Check(hintAt >= 0 && hudTexts.Skip(hintAt + 1).FirstOrDefault() == "" && hudTexts.Count(text => text.StartsWith("Deep water", StringComparison.Ordinal)) == 1,
                "the swim hint lands as a notice, once for the entry, and the clear after it when the body left the water");
            var shape = PlaytestRecords.Shape(recorder.Folder);
            Check(shape.Count == 0, "the record holds to the contract's shape: " + string.Join("; ", shape));
            if (PlaytestRecords.Validate(recorder.Folder) is { } verdict) Check(verdict.Ok, "contracts/validate.py passes the HUD's record: " + verdict.Output);
        }
        finally
        {
            RestoreMap();
            recorder.QueueFree();
            await Frames(2);
            PlaytestRecorder.RootOverride = null;
            PlaytestRecorder.GrabForTests = null;
            var folder = ProjectSettings.GlobalizePath(PlaytestRoot);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
