using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GarageGame.Native.Kernel;

public sealed class WishQueueException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record WishRefusal(string ActionId, string Code);
/// <summary>
/// A wish and the host's own evidence for it. An accepted action whose outcome comes later (a goal job) stays pending
/// until the host observes its end; only then is it a success or a refusal.
/// </summary>
public sealed record WishEntry(string Id, string ActionId, string Fingerprint, string Text, string? AimId,
    string Status, string? ResultCode, IReadOnlyList<string> SuccessfulActionIds, IReadOnlyList<WishRefusal> Refusals,
    IReadOnlyList<string> PendingActionIds)
{
    public int CommandCount => SuccessfulActionIds.Count + Refusals.Count + PendingActionIds.Count;
    public bool Recorded(string actionId) => SuccessfulActionIds.Contains(actionId) || PendingActionIds.Contains(actionId) || Refusals.Any(item => item.ActionId == actionId);
    public bool Terminal => Status is not ("queued" or "running");
    public string ActionPrefix => "w-" + Id[5..] + "-";
}

/// <summary>Immutable FIFO candidates: the host saves a candidate before making it current.</summary>
public sealed class WishQueue
{
    public const int MaxQueued = 16, MaxHistory = 64, MaxCommands = 32;
    public const string Schema = SchemaIds.WishQueue;
    /// <summary>Version 2 adds each wish's pending actions. Version 1 journals (no pending actions) still load.</summary>
    public const int Version = 2;
    private static readonly Regex Action = new(@"\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\z");
    private static readonly Regex Id = new(@"\Awish:[a-z2-7]{26}\z");
    private static readonly Regex Code = new(@"\A[a-z][a-z_]{0,63}\z");
    private static readonly Regex Aim = new(@"\A[a-z][a-z0-9_]*:[A-Za-z0-9_-]+\z");
    private static readonly Regex Fingerprint = new(@"\A[0-9a-f]{64}\z");
    public string RoomId { get; }
    public IReadOnlyList<WishEntry> Items { get; }
    public WishEntry? Active => Items.FirstOrDefault(item => item.Status == "running");
    public static WishQueue Fresh(string roomId) => new(roomId, Array.Empty<WishEntry>());
    private WishQueue(string roomId, IEnumerable<WishEntry> items)
    {
        RoomId = roomId;
        var all = items.ToArray();
        var history = all.Where(item => item.Terminal).TakeLast(MaxHistory).Select(item => item.Id).ToHashSet();
        Items = Array.AsReadOnly(all.Where(item => !item.Terminal || history.Contains(item.Id)).ToArray());
    }
    public WishQueue Submit(string actionId, string fingerprint, string text, string? aimId, out WishEntry entry)
    {
        if (!Action.IsMatch(actionId) || !Fingerprint.IsMatch(fingerprint) || text.Length is < 1 or > 280 ||
            KernelText.HasHidden(text) || aimId != null && !Aim.IsMatch(aimId))
            throw Error("invalid_args", "The wish needs a short single-line request.");
        var prior = Items.FirstOrDefault(item => item.ActionId == actionId);
        if (prior != null)
        {
            if (prior.Fingerprint != fingerprint) throw Error("action_id_conflict", "That action id already names another wish.");
            entry = prior;
            return this;
        }
        if (Items.Count(item => item.Status == "queued") >= MaxQueued)
            throw Error("budget_exceeded", "The wish queue is full. Let a wish finish or stop the queue first.");
        entry = new WishEntry("wish:" + System.Security.Cryptography.RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz234567", 26),
            actionId, fingerprint, text, aimId, "queued", null, Array.Empty<string>(), Array.Empty<WishRefusal>(), Array.Empty<string>());
        return new(RoomId, Items.Append(entry));
    }
    public WishQueue ClaimNext(out WishEntry? claimed)
    {
        claimed = Active;
        if (claimed != null) return this;
        var next = Items.FirstOrDefault(item => item.Status == "queued");
        if (next == null) return this;
        claimed = next with { Status = "running" };
        return Replace(claimed);
    }
    /// <summary>A receipt for one of the active wish's actions: done, refused, or accepted with its outcome still to come (pending).</summary>
    public WishQueue RecordReceipt(string wishId, string actionId, bool succeeded, string code, bool pending = false)
    {
        var active = RequireActive(wishId);
        if (active.Recorded(actionId)) return this;
        if (!actionId.StartsWith(active.ActionPrefix, StringComparison.Ordinal) || !Action.IsMatch(actionId) || !Code.IsMatch(code) || pending && !succeeded)
            throw Error("invalid_args", "That action does not belong to this wish.");
        if (active.CommandCount >= MaxCommands) throw Error("budget_exceeded", "This wish has reached its action limit.");
        if (pending) return Replace(active with { PendingActionIds = Append(active.PendingActionIds, actionId) });
        return Replace(Recorded(active, actionId, succeeded, code));
    }
    /// <summary>The host observed a pending action's end: it succeeded, or it failed or was cancelled with this code.</summary>
    public WishQueue Settle(string wishId, string actionId, bool succeeded, string code)
    {
        var active = RequireActive(wishId);
        if (!active.PendingActionIds.Contains(actionId)) return this;
        if (!Code.IsMatch(code)) throw Error("invalid_args", "That outcome needs a known code.");
        var settled = active with { PendingActionIds = Array.AsReadOnly(active.PendingActionIds.Where(id => id != actionId).ToArray()) };
        return Replace(Recorded(settled, actionId, succeeded, code));
    }
    public WishQueue Complete(string wishId, string resultCode)
    {
        var active = RequireActive(wishId);
        if (resultCode is not ("completed" or "failed" or "unsupported" or "cancelled" or "interrupted"))
            throw Error("invalid_args", "The runner supplied an unknown result code.");
        // Nothing grants a wish while one of its actions is still running.
        if (resultCode == "completed" && active.PendingActionIds.Count > 0) throw Error("not_ready", "This wish's actions are still running.");
        active = Unfinished(active);
        var status = resultCode switch
        {
            "cancelled" => "cancelled",
            "completed" when active.SuccessfulActionIds.Count > 0 && active.Refusals.Count == 0 => "granted",
            _ when active.SuccessfulActionIds.Count > 0 => "partial",
            "unsupported" => "unsupported",
            _ => "failed",
        };
        return Replace(active with { Status = status, ResultCode = resultCode });
    }
    public WishQueue InterruptActive() => Active is { } active ? Complete(active.Id, "interrupted") : this;
    public WishQueue CancelAll() => new(RoomId, Items.Select(item => item.Terminal ? item : Unfinished(item) with { Status = "cancelled", ResultCode = "cancelled" }));
    /// <summary>A wish that ends some other way than completed: whatever was still running did not finish under it.</summary>
    private static WishEntry Unfinished(WishEntry entry) => entry.PendingActionIds.Count == 0 ? entry : entry with
    {
        PendingActionIds = Array.Empty<string>(),
        Refusals = Array.AsReadOnly(entry.Refusals.Concat(entry.PendingActionIds.Select(id => new WishRefusal(id, "cancelled"))).ToArray()),
    };
    private static WishEntry Recorded(WishEntry entry, string actionId, bool succeeded, string code) => succeeded
        ? entry with { SuccessfulActionIds = Append(entry.SuccessfulActionIds, actionId) }
        : entry with { Refusals = Array.AsReadOnly(entry.Refusals.Append(new WishRefusal(actionId, code)).ToArray()) };
    private static IReadOnlyList<string> Append(IReadOnlyList<string> list, string value) => Array.AsReadOnly(list.Append(value).ToArray());
    private WishEntry RequireActive(string id) => Active is { } active && active.Id == id ? active : throw Error("target_not_found", "That wish is no longer running.");
    private WishQueue Replace(WishEntry entry) => new(RoomId, Items.Select(item => item.Id == entry.Id ? entry : item));
    private static WishQueueException Error(string code, string message) => new(code, message);

    public byte[] Serialize()
    {
        var entries = new JsonArray(Items.Select(item => (JsonNode)new JsonObject
        {
            ["wish_id"] = item.Id, ["action_id"] = item.ActionId, ["fingerprint"] = item.Fingerprint,
            ["text"] = item.Text, ["aim_id"] = item.AimId, ["status"] = item.Status, ["result_code"] = item.ResultCode,
            ["successful_actions"] = new JsonArray(item.SuccessfulActionIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["refusals"] = new JsonArray(item.Refusals.Select(value => (JsonNode)new JsonObject { ["action_id"] = value.ActionId, ["code"] = value.Code }).ToArray()),
            ["pending_actions"] = new JsonArray(item.PendingActionIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
        }).ToArray());
        return Encoding.UTF8.GetBytes(CanonicalJson.Text(new JsonObject { ["schema"] = Schema, ["version"] = Version, ["room_id"] = RoomId, ["items"] = entries }) + "\n");
    }
    public static WishQueue Parse(byte[] bytes, string roomId)
    {
        try
        {
            new UTF8Encoding(false, true).GetCharCount(bytes);
            using var doc = CanonicalJson.Parse(bytes);
            var root = doc.RootElement;
            Exact(root, "schema", "version", "room_id", "items");
            var version = root.GetProperty("version").GetInt32();
            if (!SchemaIds.IsSchema(root.GetProperty("schema").GetString(), "wish_queue") || version is not (1 or Version) || root.GetProperty("room_id").GetString() != roomId)
                throw new FormatException();
            var rows = root.GetProperty("items");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > MaxHistory + MaxQueued + 1) throw new FormatException();
            var entries = new List<WishEntry>();
            foreach (var row in rows.EnumerateArray())
            {
                if (version == 1) Exact(row, "wish_id", "action_id", "fingerprint", "text", "aim_id", "status", "result_code", "successful_actions", "refusals");
                else Exact(row, "wish_id", "action_id", "fingerprint", "text", "aim_id", "status", "result_code", "successful_actions", "refusals", "pending_actions");
                var id = row.GetProperty("wish_id").GetString()!;
                var action = row.GetProperty("action_id").GetString()!;
                var fingerprint = row.GetProperty("fingerprint").GetString()!;
                var text = row.GetProperty("text").GetString() ?? throw new FormatException();
                var aim = row.GetProperty("aim_id").GetString();
                var status = row.GetProperty("status").GetString()!;
                var result = row.GetProperty("result_code").GetString();
                if (!Id.IsMatch(id) || !Action.IsMatch(action) || !Fingerprint.IsMatch(fingerprint) || text.Length is < 1 or > 280 || KernelText.HasHidden(text) ||
                    aim != null && !Aim.IsMatch(aim) || status is not ("queued" or "running" or "granted" or "partial" or "failed" or "unsupported" or "cancelled") ||
                    result is not (null or "completed" or "failed" or "unsupported" or "cancelled" or "interrupted")) throw new FormatException();
                var successes = row.GetProperty("successful_actions").EnumerateArray().Select(value => value.GetString()!).ToArray();
                var failures = row.GetProperty("refusals").EnumerateArray().Select(value =>
                {
                    Exact(value, "action_id", "code");
                    return new WishRefusal(value.GetProperty("action_id").GetString()!, value.GetProperty("code").GetString()!);
                }).ToArray();
                var pending = version == 1 ? Array.Empty<string>() : row.GetProperty("pending_actions").EnumerateArray().Select(value => value.GetString()!).ToArray();
                var prefix = "w-" + id[5..] + "-";
                var actions = successes.Concat(failures.Select(value => value.ActionId)).Concat(pending).ToArray();
                if (actions.Length > MaxCommands || actions.Distinct().Count() != actions.Length || actions.Any(value => !Action.IsMatch(value) || !value.StartsWith(prefix, StringComparison.Ordinal)) ||
                    failures.Any(value => !Code.IsMatch(value.Code)) || status == "granted" && (successes.Length == 0 || failures.Length != 0) || status == "partial" && successes.Length == 0 ||
                    status is "queued" or "running" && result != null || status == "queued" && actions.Length != 0 ||
                    status is not ("queued" or "running") && result == null ||
                    status == "granted" && result != "completed" || status == "cancelled" && result != "cancelled" ||
                    status == "unsupported" && (result != "unsupported" || successes.Length != 0) ||
                    status == "failed" && (successes.Length != 0 || result is not ("completed" or "failed" or "interrupted")) ||
                    status == "partial" && result == "cancelled" || status != "running" && pending.Length != 0) throw new FormatException();
                entries.Add(new(id, action, fingerprint, text, aim, status, result, Array.AsReadOnly(successes), Array.AsReadOnly(failures), Array.AsReadOnly(pending)));
            }
            if (entries.Select(entry => entry.Id).Distinct().Count() != entries.Count || entries.Select(entry => entry.ActionId).Distinct().Count() != entries.Count ||
                entries.Count(entry => entry.Status == "running") > 1 || entries.Count(entry => entry.Status == "queued") > MaxQueued || entries.Count(entry => entry.Terminal) > MaxHistory)
                throw new FormatException();
            return new(roomId, entries);
        }
        catch (Exception error) when (error is CanonicalJsonException or DecoderFallbackException or InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException)
        { throw Error("not_ready", "The wish journal could not be read. The saved file was kept."); }
    }
    private static void Exact(JsonElement value, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != keys.Length || value.EnumerateObject().Any(property => !keys.Contains(property.Name))) throw new FormatException();
    }
}

public interface IWishQueueStore { WishQueue Load(); void Save(WishQueue queue); }

public sealed class LocalWishQueueStore(string absolutePath, string roomId) : IWishQueueStore
{
    public const int MaxBytes = 524_288;
    private readonly string _path = Path.IsPathFullyQualified(absolutePath) ? Path.GetFullPath(absolutePath) : throw new ArgumentException("Use an absolute wish journal path.");
    private readonly object _gate = new();
    private byte[]? _last;
    private bool _loaded, _readOnly;
    public WishQueue Load()
    {
        lock (_gate)
        {
            try
            {
                var bytes = Read();
                var queue = bytes == null ? WishQueue.Fresh(roomId) : WishQueue.Parse(bytes, roomId);
                _last = bytes; _loaded = true; _readOnly = false;
                var recovered = queue.InterruptActive();
                if (!ReferenceEquals(queue, recovered)) Save(recovered);
                return recovered;
            }
            catch (Exception error) when (error is WishQueueException || FileError(error))
            { _readOnly = true; throw new WishQueueException("not_ready", "The wish journal could not be loaded. The existing file was kept."); }
        }
    }
    public void Save(WishQueue queue)
    {
        lock (_gate)
        {
            if (!_loaded || _readOnly || queue.RoomId != roomId) throw new WishQueueException("not_ready", "The wish journal must be recovered before continuing.");
            string? temp = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using var lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var current = Read();
                if (_last == null ? current != null : current == null || !_last.AsSpan().SequenceEqual(current))
                { _readOnly = true; throw new WishQueueException("not_ready", "The wish journal changed in another session. Reload before continuing."); }
                var bytes = queue.Serialize();
                if (bytes.Length > MaxBytes) throw new WishQueueException("budget_exceeded", "The wish journal is full.");
                temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
                File.Move(temp, _path, true); temp = null; _last = bytes;
            }
            catch (Exception error) when (FileError(error)) { throw new WishQueueException("not_ready", "The wish journal could not be saved. Earlier wishes were kept."); }
            finally { if (temp != null) try { File.Delete(temp); } catch (Exception error) when (FileError(error)) { } }
        }
    }
    private byte[]? Read()
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > MaxBytes) throw new WishQueueException("not_ready", "The wish journal has an invalid size.");
            var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    private static bool FileError(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
