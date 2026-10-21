using Godot;
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native.Playtest;

namespace GarageGame.Tests;

/// <summary>The writer under strain: a killed game, the prune to thirty, the cap of 600 stills.</summary>
public partial class PlaytestTest
{
    /// <summary>
    /// A kill, simulated: the writer stops where it is, nothing queued is written, no quit, no manifest end (PlaytestRecorder.AbandonForTests).
    /// Every event older than a second is on disk, the file ends on a whole line, and the record is valid and unfinished.
    /// </summary>
    private async Task TestKill()
    {
        var recorder = Record($"{TestRoot}/killed");
        if (recorder == null) { Check(false, "a recorder for the kill"); return; }
        var folder = recorder.Folder;
        // The mark first: its still wakes the writer at once, so what follows reaches the file only by the writer's own beat.
        _hud._UnhandledInput(Press(Key.F9));
        await Seconds(0.1);
        for (var i = 0; i < 20; i++) Player("journal.note", new JsonObject { ["text"] = $"before {i}" });
        await Seconds(1.0);
        for (var i = 0; i < 5; i++) Player("journal.note", new JsonObject { ["text"] = $"after {i}" });
        recorder.AbandonForTests();
        var bytes = PlaytestRecords.EventBytes(folder);
        var notes = PlaytestRecords.Events(folder).Kind("command").Select(c => c["request"].Str("text")).ToList();
        var lost = Enumerable.Range(0, 5).Count(i => !notes.Contains($"after {i}"));
        Check(Enumerable.Range(0, 20).All(i => notes.Contains($"before {i}")) && PlaytestRecords.Events(folder).Kind("mark").Count() == 1,
            $"a killed game loses at most its last second: every event older than a second is on disk ({lost} of the last moment's 5 lost)");
        Check(bytes.Length > 0 && bytes[^1] == (byte)'\n', "the file ends on a whole line");
        var manifest = PlaytestRecords.Manifest(folder);
        Check(manifest["ended_utc"] == null && !PlaytestRecords.Events(folder).Kind("quit").Any(), "and has no quit and no end: an unfinished record, which is itself a finding");
        Validated(folder, "a killed game's record");
        await Unrecord(recorder);
        Check(PlaytestRecords.Manifest(folder)["ended_utc"] == null, "a killed record stays unfinished when its node leaves");
    }

    /// <summary>The newest thirty records are kept at launch; nothing that is not a record is touched, and never through a link.</summary>
    private async Task TestPrune()
    {
        var godotRoot = $"{TestRoot}/pruned";
        var root = ProjectSettings.GlobalizePath(godotRoot);
        Directory.CreateDirectory(root);
        for (var i = 0; i < 31; i++)
        {
            var old = Path.Combine(root, $"20260901-0000{i:00}-test_room");
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "manifest.json"), "{}");
        }
        Directory.CreateDirectory(Path.Combine(root, "notes"));
        Directory.CreateDirectory(Path.Combine(root, "20260801-000000-Test_Room"));
        File.WriteAllText(Path.Combine(root, "20260801-000001-test_room"), "a file, not a folder");
        // A link named as the oldest record, to a folder outside: the prune must not follow it (where this system lets a test make one).
        var outside = ProjectSettings.GlobalizePath($"{TestRoot}/outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        var linked = false;
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "20260701-000000-test_room"), outside);
            linked = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { GD.Print("PLAYTEST_PRUNE no link made here: " + error.GetType().Name); }

        var recorder = Record(godotRoot);
        if (recorder == null) { Check(false, "a recorder for the prune"); return; }
        var records = Directory.GetDirectories(root).Select(Path.GetFileName).Where(name => PlaytestRecorder.RecordId.IsMatch(name!)).ToList();
        var real = records.Where(name => name != "20260701-000000-test_room").ToList();
        Check(real.Count == PlaytestRecorder.KeepRecords && !real.Contains("20260901-000000-test_room") && !real.Contains("20260901-000001-test_room") &&
              real.Contains("20260901-000030-test_room") && real.Contains(recorder.Id),
            $"a launch keeps the newest thirty records, itself among them, and removes the older ones ({real.Count}: oldest {real.Order().FirstOrDefault()})");
        Check(Directory.Exists(Path.Combine(root, "notes")) && Directory.Exists(Path.Combine(root, "20260801-000000-Test_Room")) && File.Exists(Path.Combine(root, "20260801-000001-test_room")),
            "a folder or a file not named as a record is never touched");
        if (linked) Check(File.Exists(Path.Combine(outside, "keep.txt")) && Directory.Exists(Path.Combine(root, "20260701-000000-test_room")), "a link named as a record is never followed or removed");
        recorder.Finish(quitStill: false);
        await Unrecord(recorder);
        Check(PlaytestRecorder.Prune(root, PlaytestRecorder.KeepRecords) == 0, "thirty records: nothing more to remove");
        Check(PlaytestRecorder.Prune(root, 10) == 20 && Directory.GetDirectories(root).Count(d => PlaytestRecorder.RecordId.IsMatch(Path.GetFileName(d)) && (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0) == 10 &&
              Directory.Exists(Path.Combine(root, recorder.Id)), "a prune to ten keeps the ten newest");
        if (linked) Directory.Delete(Path.Combine(root, "20260701-000000-test_room"));
    }

    /// <summary>At most 600 stills: the steady beat stops at 560, the marks fill the rest, then the screen says the record is full.</summary>
    private async Task TestStillCap()
    {
        PlaytestRecorder.GrabForTests = () => PlaytestRecords.Picture(8, 8, Colors.White);
        var recorder = Record($"{TestRoot}/capped", "--playtest-beat=0");
        if (recorder == null) { Check(false, "a recorder for the cap"); return; }
        Check(recorder.BeatS == 0 && PlaytestRecords.Manifest(recorder.Folder)["beat_s"]!.GetValue<int>() == 0, "--playtest-beat=0 starts the play with the beat off");
        var steady = 0;
        for (var i = 0; i < 700; i++)
        {
            while (recorder.PendingStills >= PlaytestRecorder.MaxPendingSteady) await Task.Delay(1);
            if (recorder.TakeStillForTests("steady") == null) break;
            steady++;
        }
        Check(steady == PlaytestRecorder.MaxSteadyStills, $"the steady beat stops at {PlaytestRecorder.MaxSteadyStills} stills ({steady})");
        var marks = 0;
        var answer = "";
        for (var i = 0; i < 60; i++)
        {
            answer = recorder.Mark(i % 2 == 0 ? "off" : "keep");
            if (!answer.StartsWith("Mark", StringComparison.Ordinal) && !answer.StartsWith("Keep", StringComparison.Ordinal)) break;
            marks++;
        }
        Check(marks == PlaytestRecorder.MaxStills - PlaytestRecorder.MaxSteadyStills && recorder.Stills == PlaytestRecorder.MaxStills && answer == "This play's record is full; no more marks.",
            $"marks fill the rest to {PlaytestRecorder.MaxStills}, then the screen says the record is full ({marks} marks, {recorder.Stills} stills): {answer}");
        recorder.Finish(quitStill: true);
        var manifest = PlaytestRecords.Manifest(recorder.Folder);
        Check(manifest["counts"]?["stills"]?.GetValue<int>() == PlaytestRecorder.MaxStills && Directory.GetFiles(Path.Combine(recorder.Folder, "stills")).Length == PlaytestRecorder.MaxStills,
            "the quit takes no still past the cap, and the 600 are on disk");
        Validated(recorder.Folder, "a record at the cap");
        await Unrecord(recorder);
        PlaytestRecorder.GrabForTests = null;
    }
}
