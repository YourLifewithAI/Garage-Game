using Godot;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;

namespace GarageGame.Tests.Kernel;

public partial class CommandHostTest
{
    private async Task TestWishes()
    {
        var queue = WishQueue.Fresh(_room.RoomId);
        queue = queue.Submit("wish-one", new string('a', 64), "Make a light", null, out var first);
        queue = queue.Submit("wish-two", new string('b', 64), "Come here", null, out var second);
        var replay = queue.Submit("wish-one", new string('a', 64), "Make a light", null, out var replayed);
        Check(ReferenceEquals(queue, replay) && replayed.Id == first.Id, "wish submission replay retains its persisted identity");
        Check(WishThrows(() => queue.Submit("wish-one", new string('c', 64), "Another wish", null, out _)), "a reused wish action id cannot replace intent");
        queue = queue.ClaimNext(out var claimed);
        Check(claimed?.Id == first.Id && queue.Items.Single(entry => entry.Id == second.Id).Status == "queued", "wish claiming is FIFO and leaves the next wish queued");
        queue = queue.Complete(first.Id, "completed");
        Check(queue.Items.First().Status == "failed", "a model completion claim with no successful game receipt grants nothing");
        queue = queue.ClaimNext(out claimed);
        queue = queue.RecordReceipt(second.Id, second.ActionPrefix + "0", true, "completed");
        queue = queue.RecordReceipt(second.Id, second.ActionPrefix + "1", false, "out_of_bounds");
        queue = queue.Complete(second.Id, "completed");
        Check(queue.Items.Last().Status == "partial" && queue.Items.Last().Refusals.Single().Code == "out_of_bounds", "verified success plus a refusal produces a partial wish with its trusted refusal code");
        Check(WishQueue.Parse(queue.Serialize(), _room.RoomId).Items.Last().Status == "partial", "wish ledger status and evidence round-trip through strict local storage");
        Check(WishThrows(() => WishQueue.Parse(queue.Serialize(), "another_room")), "wish ledgers cannot be restored into a different room");
        Check(WishThrows(() => WishQueue.Parse(Encoding.UTF8.GetBytes("{\"schema\":null}"), _room.RoomId)), "malformed wish storage is refused");
        var versionOne = Encoding.UTF8.GetString(queue.Serialize()).Replace("\"version\":2", "\"version\":1").Replace(",\"pending_actions\":[]", "");
        Check(WishQueue.Parse(Encoding.UTF8.GetBytes(versionOne), _room.RoomId).Items.Last().Status == "partial", "a version 1 wish journal, from before pending actions, still loads");

        var running = WishQueue.Fresh(_room.RoomId).Submit("wish-three", new string('e', 64), "Fetch the book", null, out var third).ClaimNext(out _)
            .RecordReceipt(third.Id, third.ActionPrefix + "0", true, "completed", pending: true);
        Check(WishThrows(() => running.Complete(third.Id, "completed")) && WishQueue.Parse(running.Serialize(), _room.RoomId).Active?.PendingActionIds.SingleOrDefault() == third.ActionPrefix + "0",
            "a wish with a pending action cannot complete, and the pending action survives strict storage");
        var unreachable = running.Settle(third.Id, third.ActionPrefix + "0", false, "target_unreachable").Complete(third.Id, "completed").Items.Single();
        Check(unreachable.Status == "failed" && unreachable.Refusals.Single().Code == "target_unreachable", "a pending action that later fails settles as a refusal and grants nothing");
        Check(running.Complete(third.Id, "failed").Items.Single() is { PendingActionIds.Count: 0 } ended && ended.Refusals.Single().Code == "cancelled" &&
            running.CancelAll().Items.Single().Refusals.Single().Code == "cancelled" && running.InterruptActive().Items.Single().Refusals.Single().Code == "cancelled",
            "a wish that ends another way (failed, stopped, interrupted) records its still-running action as cancelled");

        var root = ProjectSettings.GlobalizePath(TestRoot + "/wishes-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(root, "queue.json");
        var store = new LocalWishQueueStore(file, _room.RoomId);
        store.Load();
        var inFlight = WishQueue.Fresh(_room.RoomId).Submit("crash", new string('d', 64), "Make a light", null, out var interrupted).ClaimNext(out _);
        store.Save(inFlight);
        var restoredStore = new LocalWishQueueStore(file, _room.RoomId);
        var restored = restoredStore.Load();
        Check(restored.Active == null && restored.Items.Single().Status == "failed" && restored.Items.Single().ResultCode == "interrupted",
            "a running wish interrupted by restart becomes a persisted failure instead of being executed twice");
        Check(WishThrows(() => store.Save(inFlight)), "an older game instance cannot overwrite a newer wish ledger");
        var preserved = File.ReadAllBytes(file);
        File.WriteAllText(file, "not a queue");
        var corrupt = new LocalWishQueueStore(file, _room.RoomId);
        Check(WishThrows(() => corrupt.Load()) && WishThrows(() => corrupt.Save(queue)) && File.ReadAllText(file) == "not a queue",
            "corrupt wish data blocks writes and keeps the original file available for recovery");

        _host.ConfigureWishes(new MemoryWishStore(_room.RoomId));
        var submitted = _host.PlayerWish("Make a light");
        Check(Ok(submitted) && _host.Wishes.Items.Count == 1 && _host.Wishes.Active == null, "the player queues intent without launching a program");
        var submissionAction = submitted["action_id"]!.GetValue<string>();
        Check(Code(Send(Command(submissionAction, "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "stay" }), Player)) == "action_id_conflict",
            "a saved wish submission owns its action id when a later goal tries to reuse it");
        Check(Code(Send(Command(submissionAction, "creation.place", new JsonObject { ["source"] = Source("Wish creation"), ["placement"] = Placement(1.5, 0, -1.0) }), Player)) == "action_id_conflict",
            "a saved wish submission cannot share its action id with a creation receipt");
        Check(Ok(Send(Command(submissionAction, "goal.stop", new JsonObject()), Player)), "Stop remains unconditional even under an existing wish action id");
        Check(Code(Send(Command("companion-wish", "wish.submit", new JsonObject { ["text"] = "Do this" }), Companion)) == "permission_denied",
            "a companion cannot manufacture player wishes");
        var claimedResult = _host.HandleWishRunner("{\"op\":\"wish.wait\"}");
        var id = claimedResult["wish"]?["wish_id"]?.GetValue<string>() ?? "";
        Check(id.Length > 0 && _host.Wishes.Active?.Id == id, "the dedicated runner claims only the first player wish");
        var denied = _host.HandleWishRunner("{\"op\":\"effect.start\",\"args\":{}}");
        Check(denied["ok"]?.GetValue<bool>() == false && denied["error"]?["code"]?.GetValue<string>() == "permission_denied", "the runner endpoint refuses world commands");
        var prefix = _host.Wishes.Active!.ActionPrefix;
        var beforePreview = _host.Wishes.Serialize();
        var rejectedPreview = Command(prefix + "preview", "goal.set", new JsonObject { ["actor"] = CommandHost.PlayerAvatar, ["goal"] = "stay" });
        rejectedPreview["preview"] = true;
        Check(!Ok(Send(rejectedPreview, Companion)) && _host.Wishes.Serialize().SequenceEqual(beforePreview),
            "a rejected wish preview records neither success nor refusal and leaves the wish ledger unchanged");
        var stayed = Send(Command(prefix + "0", "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "stay" }), Companion);
        Check(Ok(stayed) && _host.Wishes.Active!.SuccessfulActionIds.Count == 1, "only a verified tagged command receipt becomes wish evidence");
        var finished = _host.HandleWishRunner(CanonicalJson.Text(new JsonObject { ["op"] = "wish.update", ["args"] = new JsonObject { ["wish_id"] = id, ["status"] = "completed", ["result_code"] = "completed" } }));
        Check(finished["status"]?.GetValue<string>() == "granted", "a verified successful wish can finish with a curated granted status");
        _host.PlayerWish("Another light");
        _host.PlayerWish("And one more");
        _host.HandleWishRunner("{\"op\":\"wish.wait\"}");
        var cancelledPrefix = _host.Wishes.Active!.ActionPrefix;
        var stopped = _host.PlayerWishStop();
        Check(Ok(stopped) && stopped["data"]?["cancelled"]?.GetValue<int>() == 2 && _host.Wishes.Items.All(entry => entry.Terminal), "the player's stop cancels both active and queued wishes immediately");
        Check(Code(Send(Command(cancelledPrefix + "late", "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "stay" }), Companion)) == "permission_denied",
            "late commands from a cancelled wish cannot begin another action");
        _host.ConfigureWishes(new MemoryWishStore(_room.RoomId));
        await TestWishesWaitForTheirJobs();
        _host.ConfigureWishes(new MemoryWishStore(_room.RoomId));
    }

    /// <summary>
    /// Review blocker (13 October): an accepted goal.set is a running job, not a granted wish. The host keeps the wish's
    /// asynchronous actions pending until it observes their end, refuses a completion report meanwhile, and records a
    /// failed or cancelled job as the wish's refusal.
    /// </summary>
    private async Task TestWishesWaitForTheirJobs()
    {
        _host.ConfigureWishes(new MemoryWishStore(_room.RoomId));
        var wedge = PlaceCreation("Wedge", WedgeSpot);
        await SeenThenHidden();
        var (id, prefix) = ClaimWish("Look at the wedge");
        var aimed = Send(Command(prefix + "0", "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "look_at", ["target"] = wedge }), Companion);
        var job = aimed["job_id"]?.GetValue<string>() ?? "";
        var early = RunnerUpdate(id, "completed");
        Check(Ok(aimed) && job.Length > 0 && early["ok"]?.GetValue<bool>() == false && early["error"]?["code"]?.GetValue<string>() == "not_ready" && _host.Wishes.Active?.Id == id &&
            _host.Wishes.Active.PendingActionIds.Count == 1 && _host.Wishes.Active.SuccessfulActionIds.Count == 0,
            "a wish whose accepted goal is still running cannot be reported complete: " + early.ToJsonString());
        RemoveCreation(wedge);
        await MoveCompanion(CompanionSpawn, "to where the wedge's place is in sight");
        await AwaitArrival(job);
        Check(_host.Wishes.Active is { } failing && failing.SuccessfulActionIds.Count == 0 && failing.Refusals.SingleOrDefault()?.Code == "target_not_found",
            "the job's failure, observed by the host after the accepted receipt, becomes the wish's refusal");
        Check(RunnerUpdate(id, "completed")["status"]?.GetValue<string>() == "failed", "so the finished wish is failed, never granted");

        (id, prefix) = ClaimWish("Look at the doorstop");
        var cancelled = Send(Command(prefix + "0", "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "look_at", ["target"] = "obj:doorstop" }), Companion);
        _host.PlayerGoal("stop");
        Check(Ok(cancelled) && _host.Wishes.Active?.Refusals.SingleOrDefault()?.Code == "cancelled" && RunnerUpdate(id, "completed")["status"]?.GetValue<string>() == "failed",
            "a job the player's Stop cancels is a refusal too, so the wish is not granted");

        (id, prefix) = ClaimWish("Look at the doorstop again");
        var looked = Send(Command(prefix + "0", "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "look_at", ["target"] = "obj:doorstop" }), Companion);
        await AwaitArrival(looked["job_id"]?.GetValue<string>() ?? "");
        Check(_host.Wishes.Active?.SuccessfulActionIds.Count == 1 && RunnerUpdate(id, "completed")["status"]?.GetValue<string>() == "granted",
            "a job the host saw succeed grants the wish");

        (id, prefix) = ClaimWish("Walk over there");
        var walking = Send(Command(prefix + "0", "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "go_to", ["position_m"] = new JsonArray(0.6, 0, 1.0) }), Companion);
        var walkEarly = RunnerUpdate(id, "completed");
        var given = RunnerUpdate(id, "failed");
        Check(Ok(walking) && walking["job_id"] == null && walkEarly["error"]?["code"]?.GetValue<string>() == "not_ready" &&
            given["status"]?.GetValue<string>() == "failed" && _host.Wishes.Items.Last().Refusals.SingleOrDefault()?.Code == "cancelled" && _host.RunningGoal(CommandHost.CompanionAvatarId) == null,
            "a walk with no job id is watched the same way, and a failed report ends the wish's running goal as cancelled");

        (id, prefix) = ClaimWish("Follow me");
        var following = Send(Command(prefix + "0", "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "follow", ["target"] = CommandHost.PlayerAvatar }), Companion);
        Check(Ok(following) && RunnerUpdate(id, "completed")["status"]?.GetValue<string>() == "granted",
            "a follow runs until it is replaced, so it counts once the host accepts it");
        _host.PlayerGoal("stop");
    }

    private (string Id, string Prefix) ClaimWish(string text)
    {
        Check(Ok(_host.PlayerWish(text)), "the player wishes: " + text);
        var claimed = _host.HandleWishRunner("{\"op\":\"wish.wait\"}");
        return (claimed["wish"]?["wish_id"]?.GetValue<string>() ?? "", _host.Wishes.Active?.ActionPrefix ?? "w-none-");
    }

    private JsonObject RunnerUpdate(string id, string code) => _host.HandleWishRunner(CanonicalJson.Text(new JsonObject
    {
        ["op"] = "wish.update", ["args"] = new JsonObject { ["wish_id"] = id, ["status"] = code, ["result_code"] = code },
    }));

    private static bool WishThrows(Action action)
    {
        try { action(); return false; }
        catch (WishQueueException) { return true; }
    }
    private sealed class MemoryWishStore(string roomId) : IWishQueueStore
    {
        private WishQueue _queue = WishQueue.Fresh(roomId);
        public WishQueue Load() => _queue;
        public void Save(WishQueue queue) => _queue = queue;
    }
}
