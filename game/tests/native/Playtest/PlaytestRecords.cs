using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace GarageGame.Tests;

/// <summary>
/// Reading a playtest record in a test (Playtest/PlaytestRecorder): its manifest, its events line by line, and the contract's own
/// validator run on it (contracts/validate.py, through the contracts' environment: .cache/contracts-venv on Windows,
/// .cache/linux/contracts-venv on Linux, or "-- --validator=PYTHON"). Where no environment exists the suites still hold the record to
/// the contract's shape in C# and say the validator did not run.
/// </summary>
public static class PlaytestRecords
{
    /// <summary>The repository's root (the folder above game/).</summary>
    public static string Repository => Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));

    /// <summary>The contracts' Python, or null when this machine has none.</summary>
    public static string? Validator()
    {
        var named = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--validator=", StringComparison.Ordinal))?["--validator=".Length..];
        var candidates = new[]
        {
            named,
            Path.Combine(Repository, ".cache", "contracts-venv", "Scripts", "python.exe"),
            Path.Combine(Repository, ".cache", "linux", "contracts-venv", "bin", "python"),
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrEmpty(path) && File.Exists(path));
    }

    /// <summary>contracts/validate.py on a record's folder: (passed, its output). Null when there is no validator here.</summary>
    public static (bool Ok, string Output)? Validate(string folder)
    {
        if (Validator() is not { } python) return null;
        var start = new System.Diagnostics.ProcessStartInfo(python)
        {
            WorkingDirectory = Repository, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-I", Path.Combine(Repository, "contracts", "validate.py"), folder }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        return (process.ExitCode == 0 && output.StartsWith("OK", StringComparison.Ordinal), output.Trim());
    }

    public static JsonObject Manifest(string folder) => JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")))!.AsObject();

    /// <summary>The file's bytes, read while the game may still be writing it.</summary>
    public static byte[] EventBytes(string folder)
    {
        using var stream = new FileStream(Path.Combine(folder, "events.jsonl"), FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>Every line of events.jsonl as an object (a line that does not parse is an empty object).</summary>
    public static List<JsonObject> Events(string folder)
    {
        var text = System.Text.Encoding.UTF8.GetString(EventBytes(folder));
        var events = new List<JsonObject>();
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            try { events.Add(JsonNode.Parse(line)!.AsObject()); }
            catch (Exception) { events.Add(new JsonObject()); }
        }
        return events;
    }

    public static IEnumerable<JsonObject> Kind(this IEnumerable<JsonObject> events, string kind) => events.Where(e => e["kind"]?.GetValue<string>() == kind);

    public static string Str(this JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    /// <summary>Every key anywhere in a node.</summary>
    public static IEnumerable<string> Keys(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject map:
                foreach (var (key, value) in map)
                {
                    yield return key;
                    foreach (var inner in Keys(value)) yield return inner;
                }
                break;
            case JsonArray list:
                foreach (var item in list)
                    foreach (var inner in Keys(item)) yield return inner;
                break;
        }
    }

    /// <summary>
    /// The contract's rules for a record, in C# (contracts/validate.py check_playtest, the parts a test can say quickly): every line
    /// strict JSON ending in LF, t and utc never running backwards, the stills numbered in order, a mark's still taken before it, the
    /// quit last with the totals, and at a clean quit the manifest's counts agreeing. Returns the problems.
    /// </summary>
    public static List<string> Shape(string folder)
    {
        var problems = new List<string>();
        var bytes = EventBytes(folder);
        if (bytes.Length > 0 && bytes[^1] != (byte)'\n') problems.Add("the last line has no LF");
        if (bytes.Contains((byte)'\r')) problems.Add("a CR in the events");
        var lines = System.Text.Encoding.UTF8.GetString(bytes).Split('\n').Where(l => l.Length > 0).ToList();
        var manifest = Manifest(folder);
        double previous = -1;
        var stills = new HashSet<string>();
        var lastStill = 0;
        var marks = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            JsonObject line;
            try { using var strict = GarageGame.Native.Kernel.CanonicalJson.Parse(lines[i]); line = JsonNode.Parse(lines[i])!.AsObject(); }
            catch (Exception error) { problems.Add($"line {i + 1} is not strict JSON: {error.Message}"); continue; }
            if (System.Text.Encoding.UTF8.GetByteCount(lines[i]) + 1 > 8192) problems.Add($"line {i + 1} is too long");
            var t = line["t"]?.GetValue<double>() ?? -1;
            if (t < previous) problems.Add($"line {i + 1}: t runs backwards");
            previous = t;
            var kind = line.Str("kind");
            if (Keys(line).Any(k => k is "data" or "picture" or "base64" or "token" or "session_file" or "save" or "secret" or "password" or "credential"))
                problems.Add($"line {i + 1} carries a forbidden key");
            if (kind == "still")
            {
                var file = line.Str("file");
                var number = int.Parse(file[..4]);
                if (number <= lastStill || !stills.Add(file)) problems.Add($"line {i + 1}: still {file} out of order");
                lastStill = number;
            }
            if (kind == "mark")
            {
                marks++;
                if (!stills.Contains(line.Str("still"))) problems.Add($"line {i + 1}: a mark before its still");
            }
            if (kind == "quit" && i != lines.Count - 1) problems.Add("the quit is not the last line");
        }
        if (manifest["ended_utc"] != null)
        {
            var counts = manifest["counts"]!;
            if (counts["events"]!.GetValue<int>() != lines.Count || counts["stills"]!.GetValue<int>() != stills.Count || counts["marks"]!.GetValue<int>() != marks)
                problems.Add($"the manifest's counts disagree: {counts.ToJsonString()} vs {lines.Count} lines, {stills.Count} stills, {marks} marks");
            if (lines.Count == 0 || !lines[^1].Contains("\"kind\":\"quit\"", StringComparison.Ordinal)) problems.Add("a clean quit's last line is not the quit");
        }
        return problems;
    }

    /// <summary>A test picture (the suites are headless): a plain colour, a new image every time (the writer frees it).</summary>
    public static Image Picture(int width, int height, Color colour)
    {
        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        image.Fill(colour);
        return image;
    }
}
