using Godot;
using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GarageGame.Native.Kernel;

/// <summary>The player submits intent; an explicitly started local runner can claim and report only its work.</summary>
public partial class CommandHost
{
    public const string RunnerPrincipal = "runner:local";
    public static string DefaultWishSaveRoot { get; set; } = "user://single_player/wishes";
    public WishQueue Wishes { get; private set; } = WishQueue.Fresh("unconfigured");
    public string WishNotice { get; private set; } = "";
    public bool WishRunnerConnected { get; private set; }
    private IWishQueueStore? _wishStore;
    private bool _wishesUnavailable;

    public void ConfigureWishes(IWishQueueStore store)
    {
        _wishStore = store;
        try { Wishes = store.Load(); _wishesUnavailable = false; WishNotice = ""; }
        catch (WishQueueException) { _wishesUnavailable = true; WishNotice = "The wish journal could not be loaded. The existing file was kept."; }
    }
    public JsonObject PlayerWish(string text, string? aimId = null)
    {
        var args = new JsonObject { ["text"] = text };
        if (aimId != null) args["aim_id"] = aimId;
        return PlayerCommand("wish.submit", args);
    }
    public JsonObject PlayerWishStop() => PlayerCommand("wish.stop", new JsonObject());
    public JsonObject PlayerWishes() => WishesRead(PlayerPrincipal);

    private void PublishWishes(WishQueue candidate)
    {
        if (ReferenceEquals(candidate, Wishes)) return;
        if (_wishesUnavailable || _wishStore == null) throw new WishQueueException("not_ready", "The wish journal is not ready.");
        _wishStore.Save(candidate);
        Wishes = candidate;
        WishNotice = "";
    }
    private JsonObject WishSubmit(JsonElement args, string principal, string actionId, string fingerprint, bool preview)
    {
        if (principal != PlayerPrincipal) throw new Refusal("permission_denied", "Only the player can make a wish.");
        try
        {
            if (_wishesUnavailable || _wishStore == null) throw new WishQueueException("not_ready", "The wish journal is not ready.");
            var aimId = Str(args, "aim_id");
            if (aimId != null && !KnownToTeam(aimId)) throw new Refusal("target_not_found", "Neither of you knows that target.", "$.args.aim_id");
            // Preview checks capacity and text but allocates no persisted ID or receipt.
            var candidate = Wishes.Submit(actionId, fingerprint, Str(args, "text")!, aimId, out var entry);
            if (preview) return Previewed("wish.submit", principal, actionId);
            var replay = ReferenceEquals(candidate, Wishes);
            PublishWishes(candidate);
            var result = WishSubmitReceipt(entry);
            if (replay) result["replayed"] = true;
            if (!WishRunnerConnected) WishNotice = "Your wish is queued. Start the local wish runner when you are ready; the game never starts another program for you.";
            return result;
        }
        catch (WishQueueException error) { throw new Refusal(error.Code, error.Message, retryable: error.Code == "not_ready"); }
    }
    private JsonObject WishSubmitReceipt(WishEntry entry)
    {
        var result = Result("wish.submit", PlayerPrincipal, entry.ActionId, null, true);
        result["transient"] = false;
        result["data"] = new JsonObject { ["wish_id"] = entry.Id, ["status"] = "queued" };
        return result;
    }
    /// <summary>Wish submission owns its action id across every operation, as other durable receipts do.</summary>
    private JsonObject? ReplayWish(string principal, string actionId, string fingerprint)
    {
        if (principal != PlayerPrincipal || Wishes.Items.FirstOrDefault(entry => entry.ActionId == actionId) is not { } entry) return null;
        if (entry.Fingerprint != fingerprint) throw new Refusal("action_id_conflict", "That action id already names another wish.");
        var receipt = WishSubmitReceipt(entry);
        receipt["replayed"] = true;
        return receipt;
    }
    /// <summary>Fallback for receipt.lookup: wish receipts live with this bounded local wish ledger.</summary>
    private JsonObject? WishReceiptLookup(string principal, string actionId) => principal == PlayerPrincipal &&
        Wishes.Items.FirstOrDefault(entry => entry.ActionId == actionId) is { } entry
            ? new JsonObject { ["found"] = true, ["receipt"] = WishSubmitReceipt(entry) } : null;

    private JsonObject WishStop(string principal, string actionId, bool preview)
    {
        if (principal != PlayerPrincipal) throw new Refusal("permission_denied", "Only the player can stop the wish queue.");
        if (preview) return Previewed("wish.stop", principal, actionId);
        var pending = Wishes.Items.Where(entry => !entry.Terminal).ToArray();
        var cancelled = Wishes.CancelAll();
        try { PublishWishes(cancelled); }
        catch (WishQueueException)
        {
            Wishes = cancelled; _wishesUnavailable = true;
            WishNotice = "The Gubble and this session's wishes stopped. The journal could not be saved; recover it before starting more wishes.";
        }
        // Revocation is immediate, including a command waiting for a click. A later wish never approves one.
        foreach (var approval in PendingApprovals.Where(approval => pending.Any(entry => approval.ActionId.StartsWith(entry.ActionPrefix, StringComparison.Ordinal))).ToArray())
            Deny(approval.RequestId);
        PlayerGoal("stop");
        var result = Result("wish.stop", principal, actionId, null, true);
        result["transient"] = true;
        result["data"] = new JsonObject { ["cancelled"] = pending.Length };
        return result;
    }

    private JsonObject WishesRead(string principal)
    {
        if (principal != PlayerPrincipal) throw new Refusal("permission_denied", "The wish journal belongs to the player.");
        return new JsonObject { ["items"] = new JsonArray(Wishes.Items.Select(entry => (JsonNode)WishSummary(entry)).ToArray()) };
    }
    private static JsonObject WishSummary(WishEntry entry)
    {
        var row = new JsonObject
        {
            ["wish_id"] = entry.Id, ["text"] = entry.Text, ["status"] = entry.Status,
            ["action_count"] = entry.CommandCount,
            ["refusal_codes"] = new JsonArray(entry.Refusals.Select(value => (JsonNode?)JsonValue.Create(value.Code)).ToArray()),
        };
        if (entry.AimId != null) row["aim_id"] = entry.AimId;
        if (entry.ResultCode != null) row["result_code"] = entry.ResultCode;
        return row;
    }

    /// <summary>These checks run before a wish-tagged companion command. Stops remain unconditional.</summary>
    private void CheckWishCommandBudget(JsonElement root, string principal)
    {
        if (principal != CompanionPrincipal || !SchemaIds.IsSchema(Str(root, "schema"), "command")) return;
        var actionId = Str(root, "action_id") ?? "";
        if (!actionId.StartsWith("w-", StringComparison.Ordinal) || StopOps.Contains(Op(root))) return;
        var active = Wishes.Active;
        if (_wishesUnavailable || active == null || !actionId.StartsWith(active.ActionPrefix, StringComparison.Ordinal))
            throw new Refusal("permission_denied", "This wish is no longer running.");
        if (Op(root) is not ("goal.set" or "effect.start" or "effect.adjust" or "nature.cast" or "nature.trail" or "nature.repair" or "entity.grab" or "entity.release" or "entity.place" or "entity.push" or "entity.remove" or "protect.lock" or "room.undo" or "creation.place" or "creation.revise" or "creation.activate"))
            throw new Refusal("permission_denied", "The wish runner can use only its game actions.");
        if (active.Recorded(actionId))
            throw new Refusal("action_id_conflict", "That wish action already has a receipt; look it up.");
        if (active.CommandCount >= WishQueue.MaxCommands)
            throw new Refusal("budget_exceeded", "This wish has reached its action limit.");
    }
    /// <summary>
    /// The receipt itself is evidence. Runner claims and model prose can never write a success. An accepted goal with a
    /// job (fetch, go_to, come, look_at, point_at) has not happened yet: it stays pending until <see cref="WishGoalEnded"/>
    /// sees the job end. A follow runs until something replaces it, so it counts once accepted.
    /// </summary>
    private void ObserveWishCommand(JsonElement root, JsonObject result, string principal)
    {
        if (principal != CompanionPrincipal || !SchemaIds.IsSchema(Str(root, "schema"), "command") || Wishes.Active is not { } active ||
            root.TryGetProperty("preview", out var preview) && preview.ValueKind == JsonValueKind.True ||
            result["preview"]?.GetValue<bool>() == true || result["replayed"]?.GetValue<bool>() == true) return;
        var actionId = Str(root, "action_id") ?? "";
        if (!actionId.StartsWith(active.ActionPrefix, StringComparison.Ordinal) || StopOps.Contains(Op(root))) return;
        var ok = result["ok"]?.GetValue<bool>() == true;
        var code = result["error"]?["code"]?.GetValue<string>() ?? "completed";
        // Approval-required is still pending work: the final result is observed after the player's click.
        if (code == "approval_required") return;
        if (active.Recorded(actionId)) return;
        var pending = ok && Op(root) == "goal.set" && _runningGoals.TryGetValue(CompanionAvatarId, out var job) &&
            job.ActionId == actionId && job.Principal == principal && job.Goal != "follow";
        try
        {
            // A refused 33rd action cannot fit another receipt. End this bounded wish honestly;
            // reaching the action cap is not a journal-storage failure.
            if (active.CommandCount >= WishQueue.MaxCommands)
            {
                StopWishGoal(active);
                PublishWishes(Wishes.Complete(active.Id, "failed"));
            }
            else PublishWishes(Wishes.RecordReceipt(active.Id, actionId, ok, code, pending));
        }
        catch (WishQueueException)
        {
            _wishesUnavailable = true;
            WishNotice = "The action finished, but its wish record could not be saved. Further wish actions are waiting for recovery.";
        }
    }

    /// <summary>CancelGoal and Finish call this when a goal job ends: a wish's pending goal becomes its success or refusal.</summary>
    private void WishGoalEnded(GoalJob job)
    {
        if (job.Principal != CompanionPrincipal || Wishes.Active is not { } active || !active.PendingActionIds.Contains(job.ActionId)) return;
        var succeeded = job.State == "succeeded";
        var code = succeeded ? "completed" : job.Result?["error"]?["code"]?.GetValue<string>() ?? job.State;
        try { PublishWishes(Wishes.Settle(active.Id, job.ActionId, succeeded, code)); }
        catch (WishQueueException)
        {
            _wishesUnavailable = true;
            WishNotice = "A wish action ended, but its wish record could not be saved. Further wish actions are waiting for recovery.";
        }
    }

    /// <summary>
    /// A wish that ends without completing takes its still-running goal with it, recorded as cancelled. by: who ended it, as CancelGoal
    /// takes it. The runner's own report (wish.update failed, cancelled or unsupported) is the Gubble ending its work, so a lead's pop is
    /// then the Gubble's own (decision 110, as its stop); the wish's action cap is the kernel's bound, not its choice (null).
    /// </summary>
    private void StopWishGoal(WishEntry wish, string? by = null)
    {
        if (Companion == null || !_runningGoals.TryGetValue(CompanionAvatarId, out var job) || !wish.PendingActionIds.Contains(job.ActionId)) return;
        CancelGoal(CompanionAvatarId, by);
        Companion.Stop();
    }

    public void WishRunnerSession(bool connected)
    {
        WishRunnerConnected = connected;
        if (!connected)
        {
            var active = Wishes.Active;
            try { PublishWishes(Wishes.InterruptActive()); }
            catch (WishQueueException) { _wishesUnavailable = true; WishNotice = "The wish runner disconnected and its journal needs recovery."; }
            if (active != null)
            {
                foreach (var approval in PendingApprovals.Where(approval => approval.ActionId.StartsWith(active.ActionPrefix, StringComparison.Ordinal)).ToArray()) Deny(approval.RequestId);
                PlayerGoal("stop");
            }
        }
    }
    /// <summary>Called only by the dedicated authenticated runner listener. It cannot invoke game commands.</summary>
    public JsonObject HandleWishRunner(string raw)
    {
        try
        {
            if (_wishesUnavailable || _wishStore == null) throw new WishQueueException("not_ready", "The wish journal is not ready.");
            using var doc = CanonicalJson.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new WishQueueException("request_invalid", "Use the wish runner protocol.");
            var op = Str(root, "op");
            if (op == "wish.wait" && root.EnumerateObject().Count() == 1)
            {
                var candidate = Wishes.ClaimNext(out var entry);
                PublishWishes(candidate);
                var wish = entry == null ? null : new JsonObject { ["wish_id"] = entry.Id, ["text"] = entry.Text, ["room_id"] = Room.RoomId };
                if (entry?.AimId != null) wish!["aim_id"] = entry.AimId;
                return new JsonObject { ["ok"] = true, ["wish"] = wish };
            }
            if (op != "wish.update" || root.EnumerateObject().Count() != 2 || !root.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object ||
                args.EnumerateObject().Count() != 3 || args.EnumerateObject().Any(property => property.Name is not ("wish_id" or "status" or "result_code")))
                throw new WishQueueException("permission_denied", "The runner may only wait for or update a wish.");
            var id = Str(args, "wish_id") ?? "";
            var status = Str(args, "status") ?? "";
            var code = Str(args, "result_code") ?? "";
            if (status is not ("running" or "completed" or "failed" or "unsupported" or "cancelled") || code is not ("completed" or "failed" or "unsupported" or "cancelled") ||
                status != "running" && status != code) throw new WishQueueException("invalid_args", "Use a known wish result code.");
            if (Wishes.Active?.Id != id) throw new WishQueueException("target_not_found", "That wish is no longer running.");
            if (status != "running")
            {
                var active = Wishes.Active!;
                // A completion report waits for the host to see every accepted action end; ask again later.
                if (code == "completed" && active.PendingActionIds.Count > 0)
                    throw new WishQueueException("not_ready", "This wish's actions are still running.");
                // A result cannot silently leave a held action alive. Deny while the wish is active,
                // so the host's normal approval result is included in its receipt evidence.
                foreach (var approval in PendingApprovals.Where(approval => approval.ActionId.StartsWith(active.ActionPrefix, StringComparison.Ordinal)).ToArray())
                    Deny(approval.RequestId);
                // The runner ending its wish is the Gubble's own act: a bubble its lead carried pops as the Gubble's pop (decision 110).
                if (Wishes.Active is { } ending && ending.Id == id) StopWishGoal(ending, CompanionPrincipal);
                if (Wishes.Active?.Id == id) PublishWishes(Wishes.Complete(id, code));
            }
            return new JsonObject { ["ok"] = true, ["status"] = Wishes.Items.First(entry => entry.Id == id).Status };
        }
        catch (Exception error) when (error is WishQueueException or CanonicalJsonException or InvalidOperationException)
        {
            return new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = error is WishQueueException wish ? wish.Code : "request_invalid" } };
        }
    }
}
