using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GarageGame.Native.Room;

namespace GarageGame.Native.Kernel;

/// <summary>
/// The team's journal and map, host side (docs/companion/JOURNAL.md; contracts/README.md "The journal and the map").
/// - The map store replaces the companion's perception memory: one store per team (the player and their AI), filled
///   only by the two avatars' eyes (the companion's on each of its messages, both avatars' on the host's sight sweep;
///   never the camera, never anything the AI says), holding every discovered object and creation as last seen and the
///   discovered space of each level (a walkable surface: the floor, an object's top) as 5 cm cells. It is saved with
///   the room and survives link sessions.
/// - The journal writer records goals as tasks (one entry per task, updated in place), finished fetches, and creations
///   built, changed and removed, with who acted and at whose direction as far as the host can verify it, and the
///   consequences of the Gubble's actions for the player (kind "consequence": the AI's lessons, read back by kind). Not
///   every step, sight or routine move. Lines come from templates and sanitised names, so a name can never write an entry.
/// - journal.note is the sender's own words, marked as such (untrusted), never a fact; journal.read and map.find answer
///   only from the journal and the team's map, so nothing undiscovered can leak through them.
/// Both are kept in the authority's save as the host-owned team block, checked when a save loads.
/// </summary>
public partial class CommandHost
{
    public const int MaxOpenTasks = 32;
    public const int MaxHistory = 500;
    public const int MaxNotes = 100;
    public const int MaxDiscoveredEntities = PerceptionMemoryEntries;
    public const int MaxLevels = 64;
    public const double DiscoverCellM = 0.05;
    /// <summary>Space is discovered within this horizontal distance of an avatar's eye.</summary>
    public const float DiscoverRadiusM = 2.0f;
    /// <summary>At most this many cells are looked at per avatar per sweep, nearest first, so a sweep stays cheap.</summary>
    public const int DiscoverRaysPerSweep = 160;
    /// <summary>The host's sight sweep: both avatars' eyes fill the team's map this often (0 turns it off; tests of one avatar's sight).</summary>
    public static double DefaultTeamSightIntervalS { get; set; } = 0.25;
    public double TeamSightIntervalS { get; set; } = DefaultTeamSightIntervalS;
    /// <summary>Cells looked at per avatar per sweep, at most (the enumeration's bound; review major 4).</summary>
    public const int DiscoverCandidatesPerSweep = 8192;
    /// <summary>A cell a ray did not reach waits this many sweeps before it is tried again, so it never starves farther cells.</summary>
    public const int DiscoverRetrySweeps = 16;
    /// <summary>Objects and creations whose sight each avatar's sweep checks, at most, in turn (15 rays each).</summary>
    public const int SweepEntitiesPerAvatar = 48;
    /// <summary>All levels' bitmaps together stay under this many bytes, leaving the save room for receipts and the journal (review minor 9).</summary>
    public const int MaxDiscoveredLevelBytes = 256 * 1024;
    /// <summary>Newly discovered space and sightings are saved at most this often (journal entries are saved at once).</summary>
    public double TeamSaveIntervalS { get; set; } = 5.0;

    private const string Base32 = "abcdefghijklmnopqrstuvwxyz234567";
    private static readonly Regex EntryIdPattern = new(@"\Aentry-[a-z2-7]{26}\z", RegexOptions.Compiled);
    private static readonly Regex UtcPattern = new(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?Z\z", RegexOptions.Compiled);
    private static readonly Regex KnownIdPattern = new(@"\A(obj|creation):[A-Za-z0-9_-]{1,64}\z", RegexOptions.Compiled);
    private static readonly Regex SupportPattern = new(@"\A(shell|obj|creation):[A-Za-z0-9_-]{1,64}\z", RegexOptions.Compiled);
    /// <summary>The kinds of entry. "consequence" is the host's fact of what the Gubble's actions did to the player (CommandHostConsequences): its lessons.</summary>
    private static readonly HashSet<string> JournalKinds = new() { "task", "built", "changed", "removed", "found", "gathered", "consequence", "note" };
    private static readonly HashSet<string> Principals = new() { PlayerPrincipal, CompanionPrincipal };
    /// <summary>Goals whose task stays in the history when it ends: a completed action. Following, coming and going somewhere leave none.</summary>
    private static readonly HashSet<string> RecordedGoals = new() { "fetch" };
    /// <summary>Goals that show as a task while they run.</summary>
    private static readonly HashSet<string> TaskGoals = new() { "fetch", "follow", "come", "go_to" };

    private List<JsonObject> _openTasks = new();
    private List<JsonObject> _history = new();
    private List<JsonObject> _notes = new();
    /// <summary>Host-private: each open task's goal, saved with the team block so a later session can close it honestly.</summary>
    private Dictionary<string, string> _taskGoals = new(StringComparer.Ordinal);
    /// <summary>This session: each running job's task entry.</summary>
    private readonly Dictionary<string, string> _taskOfJob = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, Level> _levels = new(StringComparer.Ordinal);
    /// <summary>This session: each open task's target, so a task about something the team had not seen can name it once it is seen.</summary>
    private readonly Dictionary<string, string> _taskTargets = new(StringComparer.Ordinal);
    /// <summary>Cells a ray did not reach, and the sweep they were last tried in (a cell discovered leaves it).</summary>
    private readonly Dictionary<(string Support, int Cell), int> _cellCooldown = new();
    private int _sweepCount;
    private int _sweepEntityCursor;
    private int _sweepGoneCursor;
    private double _sightClock;
    private double _saveClock;
    private bool _teamDirty;

    /// <summary>Discovered space on one walkable surface: a grid of cells from (MinX, MinZ), one bit per cell, LSB first.</summary>
    private sealed class Level
    {
        public required string Support { get; init; }
        public double Height { get; set; }
        public double MinX { get; set; }
        public double MinZ { get; set; }
        public int Columns { get; set; }
        public int Rows { get; set; }
        public byte[] Cells { get; set; } = Array.Empty<byte>();
        public bool Get(int c, int r) { var i = r * Columns + c; return (Cells[i >> 3] & (1 << (i & 7))) != 0; }
        public void Set(int c, int r) { var i = r * Columns + c; Cells[i >> 3] |= (byte)(1 << (i & 7)); }
        public int Count => Cells.Sum(b => System.Numerics.BitOperations.PopCount(b));
    }

    // ---- the journal writer ----

    /// <summary>A journal entry's real time, to the millisecond, so entries written within one second keep their order.</summary>
    private string EntryTime() => Clock().ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static string NewEntryId() => "entry-" + System.Security.Cryptography.RandomNumberGenerator.GetString(Base32, 26);

    /// <summary>A name as a fact's line quotes it: display text, at most 60 characters, its own quotes turned to apostrophes.</summary>
    private static string Quoted(string? name)
    {
        var clean = KernelJson.DisplayText(name ?? "", 60).Replace('"', '\'').Trim();
        return "\"" + (clean.Length == 0 ? "something" : clean) + "\"";
    }

    private static string SubjectName(string? name)
    {
        var clean = KernelJson.DisplayText(name ?? "", 80).Trim();
        return clean.Length == 0 ? "something" : clean;
    }

    /// <summary>At whose direction, as a line says it: only what the host can verify.</summary>
    private static string Direction(string actor, string directedBy) =>
        actor == PlayerPrincipal ? "" : directedBy == PlayerPrincipal ? ", at the player's direction" : ", on its own initiative";

    private JsonObject Fact(string kind, string line, string actor, string directedBy, string? subjectId, string? subjectName, Vector3? pin, string? state = null, int? revision = null)
    {
        var entry = new JsonObject
        {
            ["entry_id"] = NewEntryId(), ["kind"] = kind, ["at_utc"] = EntryTime(), ["revision"] = revision ?? Revision,
            ["line"] = KernelJson.DisplayText(line, 200), ["actor"] = actor, ["directed_by"] = directedBy,
        };
        if (subjectId != null) entry["subject"] = new JsonObject { ["entities"] = new JsonArray(subjectId), ["name"] = SubjectName(subjectName) };
        if (pin is { } at) entry["pin_m"] = KernelJson.Vector(at);
        if (state != null) entry["state"] = state;
        return entry;
    }

    private static void AddTo(List<JsonObject> history, JsonObject entry)
    {
        history.Add(entry);
        // The oldest found and gathered entries go first, then other routine ones; built, changed and the lessons (consequence) are kept.
        while (history.Count > MaxHistory)
        {
            var drop = history.FindIndex(e => e["kind"]!.GetValue<string>() is "found" or "gathered");
            if (drop < 0) drop = history.FindIndex(e => e["kind"]!.GetValue<string>() is not ("built" or "changed" or "consequence"));
            history.RemoveAt(drop < 0 ? 0 : drop);
        }
    }

    private void AddHistory(JsonObject entry) => AddTo(_history, entry);

    /// <summary>
    /// Whether the team knows a thing: the avatars always; otherwise it is on the team's map or in sight of either avatar now.
    /// Only what the team knows may be named in the journal (review major 7).
    /// </summary>
    private bool KnownToTeam(string id) => id is PlayerAvatar or CompanionAvatarId || TeamView(id) != null;

    /// <summary>Whether either avatar's eyes see a thing now (the team's sight, whatever SharedSight says for queries).</summary>
    private bool TeamSees(string id, List<JsonObject> entities)
    {
        foreach (var (body, avatar) in new (SmallPlayerController?, string)[] { (Player, PlayerAvatar), (Companion, CompanionAvatarId) })
            if (body != null && IsInstanceValid(body) && body.IsInsideTree() && Perceive(body, avatar, entities).Contains(id)) return true;
        return false;
    }

    /// <summary>Whether either avatar's eyes reach a place now (for a thing about to stand there).</summary>
    private bool TeamSeesPlace(Aabb place)
    {
        foreach (var body in new SmallPlayerController?[] { Player, Companion })
            if (body != null && IsInstanceValid(body) && body.IsInsideTree() && SeesBox(body, place)) return true;
        return false;
    }

    /// <summary>A thing's name and place as the team knows it: in either avatar's sight now, as it is; else as last seen on the map; null when neither avatar has seen it.</summary>
    private (string Name, Vector3 Pin)? TeamView(string id)
    {
        var entities = Entities();
        if (entities.FirstOrDefault(e => e["id"]!.GetValue<string>() == id) is { } live && TeamSees(id, entities))
            return (live["display_name"]!.GetValue<string>(), BoundsOf(live).GetCenter());
        if (TeamMemory().Entries.TryGetValue(id, out var seen)) return (seen.Summary["display_name"]!.GetValue<string>(), BoundsOf(seen.Summary).GetCenter());
        return null;
    }

    private static string TaskLine(string goal, string? name, string actor, string directedBy) => goal switch
    {
        "fetch" => "Fetching " + (name == null ? "something" : Quoted(name)) + Direction(actor, directedBy),
        "follow" => "Following you",
        "come" => "Coming to you",
        _ => "Going to " + (name == null ? "somewhere" : Quoted(name)),
    };

    /// <summary>
    /// A goal job started: its task opens in "Working on" (fetch, follow, come, go_to; looking and pointing are not journaled).
    /// Its subject and pin come only from the team's knowledge: a thing neither avatar has seen is "something", with no id
    /// and no place, until the team sees it.
    /// </summary>
    private void TaskStarted(GoalJob job, JsonObject? known)
    {
        if (!TaskGoals.Contains(job.Goal) || !job.Exposed) return;
        var actor = job.Actor == CompanionAvatarId ? CompanionPrincipal : PlayerPrincipal;
        var view = job.Target == PlayerAvatar ? ("Player", Vector3.Zero) : TeamView(job.Target);
        var task = Fact("task", TaskLine(job.Goal, view?.Name, actor, job.Principal), actor, job.Principal, view == null ? null : job.Target, view?.Name,
            view == null || job.Target == PlayerAvatar ? null : view.Value.Pin, "active");
        while (_openTasks.Count >= MaxOpenTasks) CloseTask(_openTasks[0], "cancelled");
        _openTasks.Add(task);
        var entryId = task["entry_id"]!.GetValue<string>();
        _taskGoals[entryId] = job.Goal;
        _taskTargets[entryId] = job.Target;
        _taskOfJob[job.Id] = entryId;
        SaveTeam();
    }

    /// <summary>Open tasks about something the team had not seen get their subject once it has (the task is updated in place).</summary>
    private void NameSeenTasks()
    {
        foreach (var task in _openTasks.Where(t => t["subject"] == null).ToList())
        {
            var entryId = task["entry_id"]!.GetValue<string>();
            if (!_taskTargets.TryGetValue(entryId, out var target) || target.Length == 0 || TeamView(target) is not { } view) continue;
            task["subject"] = new JsonObject { ["entities"] = new JsonArray(target), ["name"] = SubjectName(view.Name) };
            task["pin_m"] = KernelJson.Vector(view.Pin);
            task["line"] = KernelJson.DisplayText(TaskLine(_taskGoals.GetValueOrDefault(entryId, "go_to"), view.Name, task["actor"]!.GetValue<string>(), task["directed_by"]!.GetValue<string>()), 200);
        }
    }

    /// <summary>A goal job ended: its task closes; a fetch stays in the history as done, failed or stopped, the others leave none.</summary>
    private void TaskEnded(GoalJob job)
    {
        if (!_taskOfJob.Remove(job.Id, out var entryId)) return;
        var task = _openTasks.FirstOrDefault(t => t["entry_id"]!.GetValue<string>() == entryId);
        if (task == null) return;
        NameSeenTasks();
        CloseTask(task, job.State switch { "succeeded" => "done", "failed" => "failed", _ => "cancelled" });
        SaveTeam();
    }

    private void CloseTask(JsonObject task, string state)
    {
        var entryId = task["entry_id"]!.GetValue<string>();
        _openTasks.Remove(task);
        _taskGoals.Remove(entryId, out var goal);
        _taskTargets.Remove(entryId);
        if (goal == null || !RecordedGoals.Contains(goal)) return;
        var subject = task["subject"]?["name"]?.GetValue<string>();
        var name = subject == null ? "something" : Quoted(subject);
        var actor = task["actor"]!.GetValue<string>();
        var directedBy = task["directed_by"]!.GetValue<string>();
        var line = state switch { "done" => "Fetched " + name, "failed" => "Could not fetch " + name, _ => "Stopped fetching " + name } + Direction(actor, directedBy);
        var closed = (JsonObject)task.DeepClone();
        closed["line"] = KernelJson.DisplayText(line, 200);
        closed["state"] = state;
        closed["at_utc"] = EntryTime();
        closed["revision"] = Revision;
        AddHistory(closed);
    }

    /// <summary>
    /// The fact a creation command will write (built, changed, removed), with who acted and at whose direction (the player
    /// when the player approved it), dated at the revision the command commits at. It is saved in the same write as the
    /// creation and its receipt (review major 3): see FactTeam. Its subject and pin come from the team's knowledge when it is
    /// written, as a task's do (review major, Lane A's round): a thing in either avatar's sight now as it is, a remembered one
    /// as last seen, and one neither avatar has seen is "something" with no id and no place.
    /// </summary>
    private JsonObject CreationFact(string op, string principal, string? approvedBy, string id, (string Name, Vector3 Pin)? view)
    {
        var directedBy = approvedBy ?? principal;
        var (kind, verb) = op switch { "creation.place" => ("built", "built"), "creation.revise" => ("changed", "changed"), _ => ("removed", "removed") };
        var what = view == null ? "something" : Quoted(view.Value.Name);
        var line = principal == PlayerPrincipal ? $"You {verb} {what}" : $"{char.ToUpperInvariant(verb[0])}{verb[1..]} {what}{Direction(principal, directedBy)}";
        return Fact(kind, line, principal, directedBy, view == null ? null : id, view?.Name, view?.Pin, revision: Revision + 1);
    }

    /// <summary>The team block with one more fact in the history, for the authority to save with the command that made it.</summary>
    private Godot.Collections.Dictionary FactTeam(JsonObject fact)
    {
        var history = _history.ToList();
        AddTo(history, fact);
        return KernelJson.ToVariant(ExportTeam(_notes, history)).AsGodotDictionary();
    }

    // ---- journal.note, journal.read, map.find ----

    private JsonObject Note(JsonElement args, string principal, string actionId, Godot.Collections.Dictionary meta, bool preview)
    {
        var text = Str(args, "text")!;
        if (preview) return Previewed("journal.note", principal, actionId);
        var entry = new JsonObject
        {
            ["entry_id"] = NewEntryId(), ["kind"] = "note", ["at_utc"] = EntryTime(), ["revision"] = Revision,
            ["author"] = principal, ["text"] = text, ["untrusted"] = true,
        };
        var notes = _notes.Select(n => n).Append(entry).ToList();
        while (notes.Count > MaxNotes) notes.RemoveAt(0);
        var team = ExportTeam(notes);
        var request = new Godot.Collections.Dictionary
        {
            ["op"] = "note", ["action_id"] = actionId, ["expected_revision"] = Revision, ["expected_permission_revision"] = PermissionRevision,
            ["team"] = KernelJson.ToVariant(team), ["entry_id"] = entry["entry_id"]!.GetValue<string>(),
        };
        var result = Submit(request, principal, actionId, meta);
        _notes = notes;
        _teamDirty = false;
        return result;
    }

    private JsonObject JournalRead(JsonElement args, string principal)
    {
        var kind = Str(args, "kind");
        var about = Str(args, "about");
        var since = Str(args, "since_utc");
        // An instant, however many fractional digits either side writes (review minor, Lane A's round).
        var sinceAt = since == null ? (DateTime?)null : ParseUtc(since);
        bool Match(JsonObject e) =>
            (kind == null || e["kind"]!.GetValue<string>() == kind) &&
            (about == null || (e["subject"]?["entities"]?.AsArray().Any(x => x!.GetValue<string>() == about) ?? false)) &&
            (sinceAt == null || (ParseUtc(e["at_utc"]!.GetValue<string>()) is { } at && at >= sinceAt.Value));
        NameSeenTasks();
        var jobOfTask = _taskOfJob.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
        var open = _openTasks.Where(Match).Reverse().Select(t =>
        {
            var copy = (JsonObject)t.DeepClone();
            // A task's job id only for the principal whose job it is, while it runs: the handle jobs.status answers.
            if (jobOfTask.TryGetValue(t["entry_id"]!.GetValue<string>(), out var jobId) && _jobs.TryGetValue(principal, out var mine) && mine.Any(j => j.Id == jobId && j.State == "running"))
                copy["job_id"] = jobId;
            return (JsonNode?)copy;
        }).ToArray();
        var others = _history.Select((e, i) => (Entry: e, Order: i)).Concat(_notes.Select((e, i) => (Entry: e, Order: i)))
            .Where(p => Match(p.Entry))
            .OrderByDescending(p => p.Entry["revision"]!.GetValue<int>()).ThenByDescending(p => p.Entry["at_utc"]!.GetValue<string>(), StringComparer.Ordinal).ThenByDescending(p => p.Order)
            .Select(p => p.Entry).ToList();
        var start = 0;
        if (args.TryGetProperty("cursor", out var cursor) && (!int.TryParse(cursor.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out start) || start < 0 || start > others.Count))
            throw new Refusal("invalid_args", "That cursor is not from this journal.", "$.args.cursor");
        var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : 20;
        var page = others.Skip(start).Take(limit).Select(e => (JsonNode?)e.DeepClone()).ToArray();
        var data = new JsonObject { ["open_tasks"] = new JsonArray(open), ["entries"] = new JsonArray(page) };
        if (start + page.Length < others.Count) data["next_cursor"] = (start + page.Length).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return data;
    }

    /// <summary>
    /// "Where have we seen this?": only from the team's map (objects and creations the avatars' eyes found), nearest first.
    /// Each is as the team knows it: in the companion's sight now, live; otherwise as last seen, marked. An empty answer
    /// looks the same whether or not such a thing exists.
    /// </summary>
    private JsonObject MapFind(JsonElement args, string principal)
    {
        var group = Str(args, "category_group");
        var name = Str(args, "name")?.ToLowerInvariant();
        var body = principal == PlayerPrincipal ? (SmallPlayerController?)Player : Companion;
        var from = args.TryGetProperty("near_m", out var near) ? KernelJson.ReadVector(near) : body != null && IsInstanceValid(body) ? body.GlobalPosition : Vector3.Zero;
        var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : 5;
        var live = Entities().ToDictionary(e => e["id"]!.GetValue<string>(), StringComparer.Ordinal);
        var items = new List<(float Distance, string Id, JsonObject Entity)>();
        foreach (var (id, entry) in TeamMemory().Entries)
        {
            if (!KnownIdPattern.IsMatch(id)) continue;
            JsonObject entity;
            if (principal == PlayerPrincipal) entity = live.TryGetValue(id, out var now) ? now : RememberedSummary(entry);
            else if (Perceives(principal, id) && live.TryGetValue(id, out var seen)) { entity = (JsonObject)seen.DeepClone(); entity["seen"] = "now"; }
            else entity = RememberedSummary(entry);
            if (group != null && entity["category_group"]?.GetValue<string>() != group) continue;
            if (name != null && !(entity["display_name"]!.GetValue<string>().ToLowerInvariant().Contains(name, StringComparison.Ordinal) ||
                                  (entity["category"]?.GetValue<string>().ToLowerInvariant().Contains(name, StringComparison.Ordinal) ?? false))) continue;
            items.Add((Distance(entity, from), id, entity));
        }
        var found = items.OrderBy(i => i.Distance).ThenBy(i => i.Id, StringComparer.Ordinal).Take(limit)
            .Select(i => (JsonNode?)new JsonObject { ["entity"] = i.Entity, ["distance_m"] = Math.Min(3500, Math.Round(i.Distance, 3)) }).ToArray();
        return new JsonObject { ["items"] = new JsonArray(found) };
    }

    // ---- the team's map: both avatars' eyes ----

    /// <summary>The team's map (one team in single player): the store the companion's perception memory became.</summary>
    private PerceptionMemory TeamMemory() => MemoryOf(CompanionPrincipal);

    private int LevelBytes() => _levels.Values.Sum(l => l.Cells.Length);

    /// <summary>
    /// Levels from the room as it is: each floor part, and the top of each object with a walkable top, where it stands now.
    /// A level whose support moved starts blank again (its cells were where it stood; review minor 8), and all bitmaps
    /// together stay within MaxDiscoveredLevelBytes (floors first).
    /// </summary>
    private void RefreshLevels(List<JsonObject> entities)
    {
        foreach (var entity in entities.OrderBy(e => e["kind"]!.GetValue<string>() == "shell" ? 0 : 1).ThenBy(e => e["id"]!.GetValue<string>(), StringComparer.Ordinal))
        {
            var id = entity["id"]!.GetValue<string>();
            var kind = entity["kind"]!.GetValue<string>();
            var floor = kind == "shell" && Room.Shell.Any(p => p.Id == id && p.Role == "floor");
            var top = kind == "object" && entity["held_by"] == null && entity["affordances"]!.AsArray().Any(a => a!.GetValue<string>() == "walkable_top");
            if (!floor && !top) continue;
            // Space is the room's: a floor part that runs under the walls is cut at the room's bounds.
            var whole = BoundsOf(entity);
            var low = new Vector3(Mathf.Max(whole.Position.X, Room.Bounds.Position.X), whole.Position.Y, Mathf.Max(whole.Position.Z, Room.Bounds.Position.Z));
            var high = new Vector3(Mathf.Min(whole.End.X, Room.Bounds.End.X), whole.End.Y, Mathf.Min(whole.End.Z, Room.Bounds.End.Z));
            if (high.X <= low.X || high.Z <= low.Z) continue;
            var box = new Aabb(low, high - low);
            var columns = Math.Clamp((int)Math.Ceiling(box.Size.X / DiscoverCellM - 1e-6), 1, 1024);
            var rows = Math.Clamp((int)Math.Ceiling(box.Size.Z / DiscoverCellM - 1e-6), 1, 1024);
            var (height, minX, minZ) = (Math.Round(box.End.Y, 4), Math.Round(box.Position.X, 4), Math.Round(box.Position.Z, 4));
            var bytes = (columns * rows + 7) / 8;
            if (!_levels.TryGetValue(id, out var level))
            {
                if (_levels.Count >= MaxLevels || LevelBytes() + bytes > MaxDiscoveredLevelBytes) continue;
                _levels[id] = level = new Level { Support = id, Columns = columns, Rows = rows, Height = height, MinX = minX, MinZ = minZ, Cells = new byte[bytes] };
                continue;
            }
            var moved = Math.Abs(level.Height - height) > 0.001 || Math.Abs(level.MinX - minX) > 0.001 || Math.Abs(level.MinZ - minZ) > 0.001;
            if (!moved && level.Columns == columns && level.Rows == rows) continue;
            if (LevelBytes() - level.Cells.Length + bytes > MaxDiscoveredLevelBytes) { _levels.Remove(id); continue; }
            (level.Columns, level.Rows, level.Height, level.MinX, level.MinZ, level.Cells) = (columns, rows, height, minX, minZ, new byte[bytes]);
            foreach (var key in _cellCooldown.Keys.Where(k => k.Support == id).ToList()) _cellCooldown.Remove(key);
        }
    }

    /// <summary>
    /// Cells near an avatar's eye that a ray from the eye reaches: discovered. Nearest first among the undiscovered cells not
    /// tried lately; a cell a ray did not reach waits DiscoverRetrySweeps sweeps, so cells never in sight (under a tabletop)
    /// do not starve the rest (review major 4). At most DiscoverCandidatesPerSweep cells are looked at, the levels in turn.
    /// </summary>
    private void SelectDiscoverCandidates(Vector3 eye, List<DiscoverCandidate> candidates)
    {
        candidates.Clear();
        var levels = _discoverLevels;
        levels.Clear();
        levels.AddRange(_levels.Values);
        var examined = 0;
        for (var n = 0; n < levels.Count && examined < DiscoverCandidatesPerSweep; n++)
        {
            var level = levels[(n + _sweepCount) % levels.Count];
            var c0 = Math.Max(0, (int)Math.Floor((eye.X - DiscoverRadiusM - level.MinX) / DiscoverCellM));
            var c1 = Math.Min(level.Columns - 1, (int)Math.Floor((eye.X + DiscoverRadiusM - level.MinX) / DiscoverCellM));
            var r0 = Math.Max(0, (int)Math.Floor((eye.Z - DiscoverRadiusM - level.MinZ) / DiscoverCellM));
            var r1 = Math.Min(level.Rows - 1, (int)Math.Floor((eye.Z + DiscoverRadiusM - level.MinZ) / DiscoverCellM));
            for (var r = r0; r <= r1 && examined < DiscoverCandidatesPerSweep; r++)
            for (var c = c0; c <= c1 && examined < DiscoverCandidatesPerSweep; c++)
            {
                examined++;
                var tried = _cellCooldown.TryGetValue((level.Support, r * level.Columns + c), out var last);
                if (level.Get(c, r) || (tried && _sweepCount - last < DiscoverRetrySweeps)) continue;
                var point = new Vector3((float)(level.MinX + (c + 0.5) * DiscoverCellM), (float)level.Height + 0.005f, (float)(level.MinZ + (r + 0.5) * DiscoverCellM));
                var distance = new Vector2(point.X - eye.X, point.Z - eye.Z).Length();
                if (distance <= DiscoverRadiusM) candidates.Add(new DiscoverCandidate(tried ? 1 : 0, tried ? last : 0, distance, candidates.Count, level, c, r, point));
            }
        }
        levels.Clear();
        // Never-tried cells first, nearest first; then retries, the longest-waiting first. Cells that are never in sight can
        // never take every ray from cells not yet looked at (review major 7, second review). (Ties keep the order the cells were
        // examined in, as a stable sort would.) The sweep casts at most DiscoverRaysPerSweep of them (DiscoverCell), in this order.
        candidates.Sort(DiscoverCandidate.Order);
    }

    /// <summary>One candidate cell looked at from the eye: discovered when a ray reaches it, else it waits DiscoverRetrySweeps sweeps.</summary>
    private void DiscoverCell(SmallPlayerController body, Vector3 eye, Rid exclude, DiscoverCandidate cell)
    {
        var (level, c, r, point) = (cell.Level, cell.C, cell.R, cell.Point);
        if (!CastSight(body, eye, point, exclude, out var hit) || hit.DistanceTo(point) <= 0.015f)
        {
            level.Set(c, r);
            _cellCooldown.Remove((level.Support, r * level.Columns + c));
        }
        else _cellCooldown[(level.Support, r * level.Columns + c)] = _sweepCount;
    }

    /// <summary>A cell Discover may look at, and the order it is looked at in (tried, last tried, distance, then as examined).</summary>
    private readonly record struct DiscoverCandidate(int Tried, int Last, float Distance, int Examined, Level Level, int C, int R, Vector3 Point)
    {
        public static readonly Comparison<DiscoverCandidate> Order = (a, b) =>
            a.Tried != b.Tried ? a.Tried.CompareTo(b.Tried) : a.Last != b.Last ? a.Last.CompareTo(b.Last)
            : a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Examined.CompareTo(b.Examined);
    }

    private readonly List<Level> _discoverLevels = new();

    /// <summary>Test seam: forget all discovered space (the levels stay, blank) and every cell's tries.</summary>
    internal void ForgetDiscoveredSpace()
    {
        foreach (var level in _levels.Values) level.Cells = new byte[level.Cells.Length];
        _cellCooldown.Clear();
    }

    /// <summary>Test seam: how many cells of a level are discovered (-1 when the level is unknown).</summary>
    internal int DiscoveredCells(string support) => _levels.TryGetValue(support, out var level) ? level.Count : -1;

    /// <summary>Test seam: whether the cell holding a point on a level is discovered.</summary>
    internal bool DiscoveredAt(string support, float x, float z)
    {
        if (!_levels.TryGetValue(support, out var level)) return false;
        var c = (int)Math.Floor((x - level.MinX) / DiscoverCellM);
        var r = (int)Math.Floor((z - level.MinZ) / DiscoverCellM);
        return c >= 0 && r >= 0 && c < level.Columns && r < level.Rows && level.Get(c, r);
    }

    // ---- saving the team block (journal + discovered + the open tasks' goals) ----

    /// <summary>
    /// The sweep's throttled save: the team block, with nature in the same write when nature has changed and waits for its own
    /// throttle (S12: each write is the whole save, about 8 ms of the main thread on the 10x island; two a few seconds apart were two
    /// hitches). Nature is only ever saved sooner this way, never later.
    /// </summary>
    private void SaveTeamOrBoth()
    {
        if (!NatureConfigured || _natureUnavailable || !_natureDirty)
        {
            SaveTeam();
            return;
        }
        // The team's throttle starts again whatever happens, as SaveTeam's does (a save offer pending writes nothing).
        _saveClock = 0;
        SaveNature();
    }

    /// <summary>Save the journal and the map now (a fact, a task or the sweep's throttle). Never while the save is unavailable.</summary>
    private void SaveTeam()
    {
        _saveClock = 0;
        if (Authority == null || !Authority.Call(KernelNames.IsReady).AsBool()) return;
        // While an earlier save is offered and the player has not answered, sightings and tasks wait in memory: writing them
        // would make a save of this room and withdraw the offer (review major 6, second review).
        if (_migrationOffer != null && !Godot.FileAccess.FileExists(SavePath))
        {
            _teamDirty = true;
            return;
        }
        using var team = KernelJson.ToVariant(ExportTeam(_notes));
        _teamDirty = !SavedOk(Authority.Call(KernelNames.SetTeam, team));
        // One write of the whole save at a time (S12, each is about 8 ms of the main thread on the 10x island): nature, if it
        // changes meanwhile, waits a whole throttle from this write and then goes with whatever else waits then.
        if (!_teamDirty) _natureSaveClock = 0;
    }

    /// <summary>Whether a save call answered ok; its answer is freed here.</summary>
    private static bool SavedOk(Variant answer)
    {
        using (answer)
        using (var outcome = answer.AsGodotDictionary())
        using (var ok = outcome.TryGetValue(SavedKeys.Ok, out var value) ? value : default)
            return ok.VariantType == Variant.Type.Bool && ok.AsBool();
    }

    private static class SavedKeys
    {
        public static readonly Variant Ok = "ok";
    }

    /// <summary>The team block as the save keeps it: room state's journal and discovered, plus the open tasks' goals, and whose save it is (M3).</summary>
    private JsonObject ExportTeam(List<JsonObject> notes, List<JsonObject>? history = null) => WithCamp(WithSaveTeam(new()
    {
        ["journal"] = ExportJournal(notes, history),
        ["discovered"] = ExportDiscovered(),
        ["task_goals"] = new JsonObject(_taskGoals.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(p.Value)))),
        ["avatars"] = AvatarIdentities(),
    }));

    /// <summary>The avatars' identities as the save keeps them: the player's colour, the companion's name and colour.</summary>
    private JsonObject AvatarIdentities()
    {
        var avatars = new JsonObject();
        if (Player != null && IsInstanceValid(Player))
            avatars[PlayerAvatar] = WithBody(new JsonObject { ["display_name"] = "Player", ["color"] = "#" + Player.AppearanceColor.ToHtml(false) }, Player);
        if (Companion != null && IsInstanceValid(Companion))
            avatars[CompanionAvatarId] = WithBody(new JsonObject { ["display_name"] = KernelJson.DisplayText(Companion.CompanionName, 40), ["color"] = "#" + Companion.AppearanceColor.ToHtml(false) }, Companion);
        return avatars;
    }

    /// <summary>Room state's journal block: open tasks (never with a job id), history and notes, each oldest first.</summary>
    public JsonObject ExportJournal() => ExportJournal(_notes);

    private JsonObject ExportJournal(List<JsonObject> notes, List<JsonObject>? history = null)
    {
        static JsonArray Copy(IEnumerable<JsonObject> list) => new(list.Select(e => { var c = (JsonObject)e.DeepClone(); c.Remove("job_id"); return (JsonNode?)c; }).ToArray());
        return new JsonObject { ["open_tasks"] = Copy(_openTasks), ["history"] = Copy(history ?? _history), ["notes"] = Copy(notes) };
    }

    /// <summary>Room state's discovered block: the levels with anything discovered, and every discovered object and creation as last seen.</summary>
    public JsonObject ExportDiscovered()
    {
        var levels = new JsonArray();
        foreach (var level in _levels.Values.Where(l => l.Count > 0))
            levels.Add(new JsonObject
            {
                ["support"] = level.Support, ["height_m"] = level.Height, ["min_xz_m"] = new JsonArray(level.MinX, level.MinZ),
                ["columns"] = level.Columns, ["rows"] = level.Rows, ["cells"] = Convert.ToBase64String(level.Cells),
            });
        var entities = new JsonObject();
        var memory = TeamMemory();
        var listed = memory.Order.Where(i => KnownIdPattern.IsMatch(i)).ToList();
        var cap = Math.Min(PerceptionMemoryLimit, MaxDiscoveredEntities);
        var dropped = new HashSet<string>(EvictionOrder(listed, new HashSet<string>(StringComparer.Ordinal)).Take(Math.Max(0, listed.Count - cap)), StringComparer.Ordinal);
        foreach (var id in listed.Where(i => !dropped.Contains(i)).OrderBy(i => i, StringComparer.Ordinal))
        {
            var entry = memory.Entries[id];
            var entity = (JsonObject)entry.Summary.DeepClone();
            foreach (var field in new[] { "seen", "last_seen_ago_s", "last_seen_revision", "may_be_stale", "held_by" }) entity.Remove(field);
            var known = new JsonObject
            {
                ["entity"] = entity, ["last_seen_utc"] = KernelJson.Utc(entry.SeenAt), ["last_seen_revision"] = entry.SeenRevision, ["may_be_stale"] = entry.Changed,
            };
            if (entry.ProtectedBy.Length > 0) known["protected_by"] = entry.ProtectedBy;
            entities[id] = known;
        }
        return new JsonObject { ["cell_m"] = DiscoverCellM, ["levels"] = levels, ["entities"] = entities };
    }

    /// <summary>The authority's team_check: a save's team block is believed only if it reads back whole (TeamProblem).</summary>
    public Godot.Collections.Dictionary CheckTeam(Godot.Collections.Dictionary team, int revision)
    {
        var problem = TeamProblem(Normalized(KernelJson.ToJson(team)), revision);
        return problem == null ? new() { ["ok"] = true } : new() { ["ok"] = false, ["message"] = "The saved journal or map is invalid: " + problem + "; the save was not loaded and is kept as it was." };
    }

    /// <summary>A block as strict JSON reads it (whole numbers as integers), whatever way it arrived (a Godot dictionary reads them as floats).</summary>
    private static JsonObject? Normalized(JsonNode? node)
    {
        try { return node is JsonObject ? JsonNode.Parse(CanonicalJson.Text(node)) as JsonObject : null; }
        catch (Exception) { return null; }
    }

    /// <summary>What is wrong with a team block (or null): its journal, its discovered map, or its open tasks' goals.</summary>
    internal string? TeamProblem(JsonObject? team, int revision)
    {
        // Anything malformed refuses the load whole; nothing that passes may throw later.
        try { return TeamProblemUnchecked(team, revision); }
        catch (Exception error) { return "an unreadable block (" + error.GetType().Name + ")"; }
    }

    private static readonly string[] UtcFormats = { "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'" };
    private static readonly HashSet<string> ProvenanceKinds = new() { "captured_generated", "captured_scanned", "hand_authored", "procedural", "ai_created", "player_created", "placeholder" };

    /// <summary>A UTC time in the contract's form as an instant, or null.</summary>
    private static DateTime? ParseUtc(string text) =>
        UtcPattern.IsMatch(text) && DateTime.TryParseExact(text, UtcFormats, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    /// <summary>A real UTC time in the contract's form (the pattern alone admits month 99).</summary>
    private static bool RealTime(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String && UtcPattern.IsMatch(node.GetValue<string>()) &&
        DateTime.TryParseExact(node.GetValue<string>(), UtcFormats, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out _);

    private static bool Number(JsonNode? node, double limit = 1000) =>
        node?.GetValueKind() == JsonValueKind.Number && node.GetValue<double>() is var d && double.IsFinite(d) && Math.Abs(d) <= limit;

    private static bool Whole(JsonNode? node, double max = int.MaxValue) => Number(node, double.MaxValue) && node!.GetValue<double>() is var d && d >= 0 && d <= max && d == Math.Floor(d);

    private static bool Vec3(JsonNode? node) => node is JsonArray a && a.Count == 3 && a.All(v => Number(v));

    private static bool Text(JsonNode? node, int max) => node?.GetValueKind() == JsonValueKind.String && node.GetValue<string>() is { Length: > 0 } t &&
        KernelText.CodePoints(t).Length <= max && !KernelText.HasHidden(t);

    /// <summary>A discovered entity's summary as the contract types it: every field, its type and range (review major 1).</summary>
    private static string? SummaryProblem(JsonObject entity, string id, int storeRevision)
    {
        var allowed = new HashSet<string> { "id", "kind", "display_name", "category", "category_group", "position_m", "bounds_m", "affordances", "movable", "protected", "provenance_kind", "revision" };
        if (entity.Any(p => !allowed.Contains(p.Key))) return "fields";
        if (entity["id"]?.GetValueKind() != JsonValueKind.String || entity["id"]!.GetValue<string>() != id) return "id";
        if (entity["kind"]?.GetValueKind() != JsonValueKind.String || entity["kind"]!.GetValue<string>() != (id.StartsWith("obj:", StringComparison.Ordinal) ? "object" : "creation")) return "kind";
        if (!Text(entity["display_name"], 80) || (entity["category"] != null && !Text(entity["category"], 60))) return "name";
        if (entity["category_group"] is { } group && (group.GetValueKind() != JsonValueKind.String || !Token.IsMatch(group.GetValue<string>()))) return "category group";
        if (!Vec3(entity["position_m"]) || entity["bounds_m"] is not JsonObject bounds || bounds.Count != 2 || !Vec3(bounds["min_m"]) || !Vec3(bounds["max_m"])) return "place";
        for (var axis = 0; axis < 3; axis++)
            if (bounds["min_m"]![axis]!.GetValue<double>() > bounds["max_m"]![axis]!.GetValue<double>()) return "bounds";
        if (entity["affordances"] is not JsonArray affordances || affordances.Count > 16 ||
            affordances.Any(a => a?.GetValueKind() != JsonValueKind.String || !Affordances.Contains(a.GetValue<string>())) ||
            affordances.Select(a => a!.GetValue<string>()).Distinct().Count() != affordances.Count) return "affordances";
        if (entity["movable"]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False) || entity["protected"]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) return "flags";
        if (entity["provenance_kind"]?.GetValueKind() != JsonValueKind.String || !ProvenanceKinds.Contains(entity["provenance_kind"]!.GetValue<string>())) return "provenance";
        if (!Whole(entity["revision"], Math.Max(storeRevision, 0))) return "revision";
        return null;
    }

    private string? TeamProblemUnchecked(JsonObject? team, int revision)
    {
        if (team == null) return "not an object";
        if (team.Any(p => p.Key is not ("journal" or "discovered" or "task_goals" or "avatars") && !IsSaveTeamKey(p.Key) && !IsCampKey(p.Key))) return "an unknown block";
        if (SaveTeamProblem(team) is { } whose) return whose;
        // Fire's camp: the start spots, the lit campfires and the island's clock (CommandHostCamps).
        if (CampKeysProblem(team) is { } camp) return camp;
        if (team["avatars"] is { } avatars && (avatars is not JsonObject identities || identities.Any(p => p.Key is not (PlayerAvatar or CompanionAvatarId) ||
            p.Value is not JsonObject identity || identity.Count != (identity.ContainsKey("body") ? 3 : 2) || !BodyIsValid(identity) || !Text(identity["display_name"], 40) ||
            identity["color"]?.GetValueKind() != JsonValueKind.String || !System.Text.RegularExpressions.Regex.IsMatch(identity["color"]!.GetValue<string>(), @"\A#[0-9a-fA-F]{6}\z") ||
            (p.Key == PlayerAvatar && identity["display_name"]!.GetValue<string>() != "Player")))) return "an avatar's identity";
        if (team["journal"] is JsonObject journal && JournalProblem(journal, revision) is { } j) return j;
        if (team["discovered"] is JsonObject discovered && DiscoveredProblem(discovered, revision) is { } d) return d;
        if (team["journal"] is not (null or JsonObject) || team["discovered"] is not (null or JsonObject)) return "a block that is not an object";
        if (team["task_goals"] is { } goals && (goals is not JsonObject map || map.Any(p => !EntryIdPattern.IsMatch(p.Key) || p.Value?.GetValueKind() != JsonValueKind.String || !TaskGoals.Contains(p.Value.GetValue<string>()))))
            return "an open task's goal";
        return null;
    }

    private static string? JournalProblem(JsonObject journal, int storeRevision)
    {
        if (journal.Count != 3 || journal["open_tasks"] is not JsonArray open || journal["history"] is not JsonArray history || journal["notes"] is not JsonArray notes) return "the journal's lists";
        if (open.Count > MaxOpenTasks || history.Count > MaxHistory || notes.Count > MaxNotes) return "the journal's bounds";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (list, part) in new[] { (open, "open_tasks"), (history, "history"), (notes, "notes") })
        {
            var previous = -1;
            foreach (var node in list)
            {
                if (node is not JsonObject entry || EntryProblem(entry) is { } problem) return $"an entry in {part}" + (node is JsonObject e2 && EntryProblem(e2) is { } p2 ? $" ({p2})" : "");
                var kind = entry["kind"]!.GetValue<string>();
                if (part == "notes" ? kind != "note" : kind == "note") return $"a note out of place in {part}";
                if (part == "open_tasks" ? kind != "task" || entry["state"]!.GetValue<string>() != "active" : kind == "task" && entry["state"]!.GetValue<string>() == "active") return $"a task out of place in {part}";
                if (entry.ContainsKey("job_id")) return "a saved job id";
                if (!ids.Add(entry["entry_id"]!.GetValue<string>())) return "an entry listed twice";
                var revision = entry["revision"]!.GetValue<int>();
                if (revision < previous || revision > storeRevision) return $"the order of {part}";
                previous = revision;
            }
        }
        return null;
    }

    /// <summary>One entry: the contract's fields for its kind (a note carries none of a fact's, a fact none of a note's), display text, ids.</summary>
    private static string? EntryProblem(JsonObject entry)
    {
        if (!EntryIdPattern.IsMatch(entry["entry_id"]?.GetValueKind() == JsonValueKind.String ? entry["entry_id"]!.GetValue<string>() : "")) return "entry_id";
        if (entry["kind"]?.GetValueKind() != JsonValueKind.String || !JournalKinds.Contains(entry["kind"]!.GetValue<string>())) return "kind";
        if (!RealTime(entry["at_utc"]) || !Whole(entry["revision"])) return "time";
        var kind = entry["kind"]!.GetValue<string>();
        var note = new[] { "author", "text", "untrusted" };
        var fact = new[] { "line", "subject", "actor", "directed_by", "pin_m", "state", "quantity" };
        var allowed = new HashSet<string> { "entry_id", "kind", "at_utc", "revision" };
        allowed.UnionWith(kind == "note" ? note : fact);
        if (entry.Any(p => !allowed.Contains(p.Key))) return "fields";
        if (kind == "note")
            return Principals.Contains(entry["author"]?.GetValueKind() == JsonValueKind.String ? entry["author"]!.GetValue<string>() : "") && Text(entry["text"], 280) &&
                entry["untrusted"]?.GetValueKind() == JsonValueKind.True ? null : "a note";
        if (!Text(entry["line"], 200) || entry["actor"]?.GetValueKind() != JsonValueKind.String || !Principals.Contains(entry["actor"]!.GetValue<string>()) ||
            entry["directed_by"]?.GetValueKind() != JsonValueKind.String || !Principals.Contains(entry["directed_by"]!.GetValue<string>())) return "a fact";
        if ((kind == "task") != entry.ContainsKey("state") || (entry["state"] is { } state && (state.GetValueKind() != JsonValueKind.String || state.GetValue<string>() is not ("active" or "done" or "failed" or "cancelled")))) return "a state";
        if ((kind is "found" or "gathered") != entry.ContainsKey("quantity")) return "a quantity";
        if (entry["subject"] is { } subject && (subject is not JsonObject s || s.Count != 2 || s["entities"] is not JsonArray list || list.Count is < 1 or > 8 ||
            list.Any(x => x?.GetValueKind() != JsonValueKind.String || !EntityId.IsMatch(x.GetValue<string>())) || list.Select(x => x!.GetValue<string>()).Distinct().Count() != list.Count || !Text(s["name"], 80))) return "a subject";
        if (entry["pin_m"] is { } pin && !Vec3(pin)) return "a pin";
        if (entry["quantity"] is { } quantity && (quantity is not JsonObject q || q.Any(x => x.Key is not ("count" or "needed")) || !Whole(q["count"], 1000000) ||
            (q["needed"] != null && (!Whole(q["needed"], 1000000) || q["needed"]!.GetValue<double>() < 1)))) return "a quantity";
        return null;
    }

    private string? DiscoveredProblem(JsonObject discovered, int storeRevision)
    {
        if (discovered.Count != 3 || discovered["cell_m"]?.GetValueKind() != JsonValueKind.Number || Math.Abs(discovered["cell_m"]!.GetValue<double>() - DiscoverCellM) > 1e-9 ||
            discovered["levels"] is not JsonArray levels || discovered["entities"] is not JsonObject entities) return "the discovered map's blocks";
        if (levels.Count > MaxLevels || entities.Count > MaxDiscoveredEntities) return "the discovered map's bounds";
        var supports = new HashSet<string>(StringComparer.Ordinal);
        var budget = 0;
        foreach (var node in levels)
        {
            if (node is not JsonObject level || level.Any(p => p.Key is not ("support" or "surface" or "height_m" or "min_xz_m" or "columns" or "rows" or "cells"))) return "a level";
            var support = level["support"]?.GetValueKind() == JsonValueKind.String ? level["support"]!.GetValue<string>() : "";
            if (!SupportPattern.IsMatch(support) || !supports.Add(support)) return "a level's support";
            if (level["columns"]?.GetValueKind() != JsonValueKind.Number || level["rows"]?.GetValueKind() != JsonValueKind.Number) return "a level's grid";
            var columns = level["columns"]!.GetValue<double>();
            var rows = level["rows"]!.GetValue<double>();
            if (columns is < 1 or > 1024 || rows is < 1 or > 1024 || columns != Math.Floor(columns) || rows != Math.Floor(rows)) return "a level's grid";
            if (!Number(level["height_m"]) || level["min_xz_m"] is not JsonArray min || min.Count != 2 || min.Any(v => !Number(v))) return "a level's place";
            if (level["surface"] is { } surface && (surface.GetValueKind() != JsonValueKind.String || !Token.IsMatch(surface.GetValue<string>()))) return "a level's surface";
            if (level["cells"]?.GetValueKind() != JsonValueKind.String) return "a level's cells";
            var bits = Convert.FromBase64String(level["cells"]!.GetValue<string>());
            var count = (int)columns * (int)rows;
            if (bits.Length != (count + 7) / 8 || (count % 8 != 0 && bits[^1] >> (count % 8) != 0)) return "a level's cells";
            budget += bits.Length;
            if (budget > MaxDiscoveredLevelBytes) return "discovered space over its budget";
        }
        foreach (var (id, node) in entities)
        {
            if (!KnownIdPattern.IsMatch(id) || (id.StartsWith("obj:", StringComparison.Ordinal) && Room.Objects.All(o => o.Id != id))) return "a discovered entity's id";
            if (node is not JsonObject known || known.Any(p => p.Key is not ("entity" or "parts" or "protected_by" or "last_seen_utc" or "last_seen_revision" or "may_be_stale"))) return "a discovered entity";
            if (known["entity"] is not JsonObject entity || SummaryProblem(entity, id, storeRevision) is { } why) return "a discovered entity's summary" + (known["entity"] is JsonObject e && SummaryProblem(e, id, storeRevision) is { } w ? $" ({w})" : "");
            if (!RealTime(known["last_seen_utc"]) || !Whole(known["last_seen_revision"], Math.Max(storeRevision, 0)) || known["may_be_stale"]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) return "a discovered entity's sighting";
            if (known["parts"] is { } parts && (parts is not JsonObject partMap || partMap.Count > 32 || partMap.Any(x => !Token.IsMatch(x.Key) || !Number(x.Value, 1) || x.Value!.GetValue<double>() < 0))) return "a discovered entity's parts";
            if (known["protected_by"] is { } by && (by.GetValueKind() != JsonValueKind.String || !Principals.Contains(by.GetValue<string>()))) return "a discovered entity's protection";
        }
        return null;
    }

    /// <summary>
    /// Believe a team block (checked by CheckTeam when the save loaded): the journal, the open tasks' goals, the levels and
    /// the discovered entities. A new session closes the tasks an earlier one left open: their jobs did not survive it.
    /// </summary>
    private void LoadTeam(JsonObject? team, bool closeOpenTasks)
    {
        try { LoadTeamUnchecked(Normalized(team), closeOpenTasks); }
        catch (Exception error)
        {
            GD.Print("COMMAND_HOST the saved journal and map could not be read and were left out: " + error.GetType().Name);
            LoadTeamUnchecked(null, false);
        }
    }

    private void LoadTeamUnchecked(JsonObject? team, bool closeOpenTasks)
    {
        // A sweep under way read the map and the levels as they were: the next one starts from what is loaded.
        _sweep = null;
        _openTasks = new(); _history = new(); _notes = new(); _taskGoals = new(StringComparer.Ordinal);
        _taskTargets.Clear();
        _cellCooldown.Clear();
        _levels.Clear();
        var memory = TeamMemory();
        memory.Clear();
        _savedTeam = null;
        LoadCampBlock(null);
        if (team == null || team.Count == 0 || TeamProblem(team, Revision) != null) return;
        // Whose save it is and how it plays (M3): the boot's choice of difficulty defers to it.
        _savedTeam = ReadSaveTeam(team);
        // Fire's camp: the start spots and the lit campfires, and the island's clock (CommandHostCamps).
        LoadCampBlock(team["camp"] as JsonObject);
        LoadClockBlock(team["clock"]);
        if (team["journal"] is JsonObject journal)
        {
            _openTasks = journal["open_tasks"]!.AsArray().Select(n => (JsonObject)n!.DeepClone()).ToList();
            _history = journal["history"]!.AsArray().Select(n => (JsonObject)n!.DeepClone()).ToList();
            _notes = journal["notes"]!.AsArray().Select(n => (JsonObject)n!.DeepClone()).ToList();
        }
        if (team["task_goals"] is JsonObject goals) foreach (var (key, value) in goals) _taskGoals[key] = value!.GetValue<string>();
        if (team["discovered"] is JsonObject discovered)
        {
            foreach (var node in discovered["levels"]!.AsArray())
            {
                var level = node!.AsObject();
                var support = level["support"]!.GetValue<string>();
                _levels[support] = new Level
                {
                    Support = support, Height = level["height_m"]!.GetValue<double>(), MinX = level["min_xz_m"]![0]!.GetValue<double>(), MinZ = level["min_xz_m"]![1]!.GetValue<double>(),
                    Columns = (int)level["columns"]!.GetValue<double>(), Rows = (int)level["rows"]!.GetValue<double>(), Cells = Convert.FromBase64String(level["cells"]!.GetValue<string>()),
                };
            }
            var known = discovered["entities"]!.AsObject().OrderBy(p => p.Value!["last_seen_utc"]!.GetValue<string>(), StringComparer.Ordinal).ThenBy(p => p.Key, StringComparer.Ordinal);
            foreach (var (id, node) in known)
            {
                var sighting = node!.AsObject();
                memory.Add(id, new Remembered
                {
                    Summary = (JsonObject)sighting["entity"]!.DeepClone(), ProtectedBy = sighting["protected_by"]?.GetValue<string>() ?? "",
                    SeenAt = DateTime.Parse(sighting["last_seen_utc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
                    SeenRevision = (int)sighting["last_seen_revision"]!.GetValue<double>(), Changed = sighting["may_be_stale"]!.GetValue<bool>(),
                });
            }
        }
        if (team["avatars"] is JsonObject identities)
        {
            if (identities[CompanionAvatarId] is JsonObject companion && Companion != null && IsInstanceValid(Companion))
            {
                // A save from before the founder named the companion still says Wisp: it is the Gubble now.
                Companion.SetDisplayName(CompanionAvatar.SavedName(companion["display_name"]!.GetValue<string>()));
                Companion.SetAppearance(new Color(companion["color"]!.GetValue<string>()));
            }
            if (identities[PlayerAvatar] is JsonObject player && Player != null && IsInstanceValid(Player))
            {
                Player.SetAppearance(new Color(player["color"]!.GetValue<string>()));
                // The drawn character the player wore in this room (the characters round), when the save names one.
                if (StrOf(player["body"]) is { } body) Player.SetCharacterId(body);
            }
        }
        EvictPastTheBound(TeamMemory(), new HashSet<string>(StringComparer.Ordinal));
        if (!closeOpenTasks || _openTasks.Count == 0) return;
        foreach (var task in _openTasks.ToList()) CloseTask(task, "cancelled");
        SaveTeam();
    }

    /// <summary>Test seam: the team block as it would be saved now.</summary>
    internal JsonObject TeamBlock() => ExportTeam(_notes);
}
