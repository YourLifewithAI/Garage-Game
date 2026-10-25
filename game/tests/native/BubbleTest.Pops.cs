using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Tests;

/// <summary>
/// Decision 110 (the founder's ruling of 25 October): the Gubble's pop, defined, path by path. A carry the Gubble cast on the player holds
/// them; each way it can end is sent, and the host's seam hears how EndBubble classed it (BubbleEnd) and whether the fall from there is
/// left pending as the Gubble's consequence (decision 96: it costs Trust if the landing hurts). A pop the Gubble chose (its goal.stop,
/// its effect.stop, a new goal of its own, its recast, its wish runner ending the wish whose lead it was) is PoppedByGubble and leaves
/// the fall pending; the player's (the dive key, X, effect.stop, wish.stop, the runner closed) is PoppedByPlayer; the world's (the
/// timer, a rules change, the body put down elsewhere or gone, the room unloading, the player's new order or recast, the wish's action
/// cap) is Popped or TimedOut. Neither of the last two ever leaves a fall pending. A lead blocked by a ceiling, and the Gubble's new lead
/// of the same bubble, pop nothing. Decision 115 (P-Nature-2, item 3): on an island whose Bubbles wait for the player's yes (keyed_yes),
/// a Gubble recast the player said yes to (the approval card's Approve) is the player's pop, PoppedByPlayer with nothing pending, by
/// effect.start or nature.cast, led or not; one the player turned down pops nothing; the Gubble's own recast, unapproved, stays its own.
/// </summary>
public partial class BubbleTest
{
    /// <summary>
    /// One way a bubble ends: led first or not, inside a wish or not, the end and receipt word it must get (null: no pop at all), and
    /// whether the island's Bubbles wait for the player's yes (Keyed: the carry is approved first, as every Gubble Bubble then is).
    /// </summary>
    private sealed record PopPath(string Name, BubbleEnd? End, string? Word, bool Led, bool Wish, Func<PopScene, Task> Pop, bool Keyed = false);

    /// <summary>What a path's pop acts on: the carry's cast, its lead's goal.set, and the wish it runs under.</summary>
    private sealed record PopScene(JsonObject Carry, JsonObject? Lead, string WishId, string Prefix);

    private sealed class PopWishStore(string roomId) : IWishQueueStore
    {
        private WishQueue _queue = WishQueue.Fresh(roomId);
        public WishQueue Load() => _queue;
        public void Save(WishQueue queue) => _queue = queue;
    }

    private JsonObject RunnerUpdate(string wishId, string code) => _host.HandleWishRunner(CanonicalJson.Text(new JsonObject
    {
        ["op"] = "wish.update", ["args"] = new JsonObject { ["wish_id"] = wishId, ["status"] = code, ["result_code"] = code },
    }));

    private JsonObject GubbleStop(JsonObject? args = null) => Send(Command(NextId("stop"), "goal.stop", args ?? new JsonObject()), Companion);

    private JsonObject StopEffect(string principal, string effect) => Send(Command(NextId("stop"), "effect.stop", new JsonObject { ["effect"] = effect }), principal);

    private JsonObject GubbleGoal(string principal, string goal) =>
        Send(Command(NextId("goal"), "goal.set", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = goal }), principal);

    /// <summary>v5 with its Bubbles of tier keyed_yes: every Gubble Bubble, effect.start or nature.cast, waits for the player's yes.</summary>
    private void KeyedBubbles()
    {
        var pack = JsonNode.Parse(System.IO.File.ReadAllText(ProjectSettings.GlobalizePath(IslandRules.PathFor("storybook_wild", 5))))!.AsObject();
        foreach (var ability in pack["abilities"]!.AsArray())
            if (ability?["capability"]?.GetValue<string>() == "bubbles") ability["tier"] = "keyed_yes";
        _host.UseRules(IslandRules.Parse(System.Text.Encoding.UTF8.GetBytes(pack.ToJsonString())));
    }

    /// <summary>A held command answered by the player's click (Approve, or Deny when no): the click's result, or the answer itself when nothing was held.</summary>
    private JsonObject Clicked(JsonObject held, bool yes = true) =>
        held["approval_needed"]?["request_id"]?.GetValue<string>() is { } request ? (yes ? _host.Approve(request) : _host.Deny(request)) : held;

    private async Task TestEveryPopIsClassified()
    {
        var paths = new List<PopPath>
        {
            // The Gubble's own choice (decision 110): its pop leaves the fall pending.
            new("the Gubble's stop of the bubble it leads", BubbleEnd.PoppedByGubble, "popped", true, false, _ => { GubbleStop(); return Task.CompletedTask; }),
            new("the Gubble's stop of its own bubble", BubbleEnd.PoppedByGubble, "popped", false, false, _ => { GubbleStop(); return Task.CompletedTask; }),
            new("the Gubble's stop naming itself", BubbleEnd.PoppedByGubble, "popped", false, false,
                _ => { GubbleStop(new JsonObject { ["actor"] = CommandHost.CompanionAvatarId }); return Task.CompletedTask; }),
            new("the Gubble's effect.stop by id", BubbleEnd.PoppedByGubble, "popped", false, false, s => { StopEffect(Companion, BubbleOf(s.Carry)); return Task.CompletedTask; }),
            new("the Gubble's effect.stop of all", BubbleEnd.PoppedByGubble, "popped", false, false, _ => { StopEffect(Companion, "all"); return Task.CompletedTask; }),
            new("a new goal of the Gubble's own", BubbleEnd.PoppedByGubble, "popped", true, false, _ => { GubbleGoal(Companion, "follow"); return Task.CompletedTask; }),
            new("the Gubble's recast", BubbleEnd.PoppedByGubble, "popped", false, false,
                _ => { CastTheBubble(Companion, _player.GlobalPosition + new Vector3(0.35f, 0, 0.1f)); return Task.CompletedTask; }),
            // Decision 115: a recast of the Gubble's the player said yes to is the player's pop (nothing pending); turned down, nothing pops.
            new("the Gubble's recast the player said yes to", BubbleEnd.PoppedByPlayer, "popped_by_player", false, false,
                _ => { Clicked(CastTheBubble(Companion, _player.GlobalPosition + new Vector3(0.35f, 0, 0.1f))); return Task.CompletedTask; }, Keyed: true),
            new("the Gubble's recast the player said yes to, mid-lead", BubbleEnd.PoppedByPlayer, "popped_by_player", true, false,
                _ => { Clicked(CastTheBubble(Companion, _player.GlobalPosition + new Vector3(0.35f, 0, 0.1f))); return Task.CompletedTask; }, Keyed: true),
            new("the Gubble's nature.cast recast the player said yes to", BubbleEnd.PoppedByPlayer, "popped_by_player", false, false,
                _ => { Clicked(AtPoint(Companion, "bubble_carry", _player.GlobalPosition + new Vector3(0.35f, 0, 0.1f))); return Task.CompletedTask; }, Keyed: true),
            new("the Gubble's recast the player turned down", null, null, false, false,
                _ => { Clicked(CastTheBubble(Companion, _player.GlobalPosition + new Vector3(0.35f, 0, 0.1f)), yes: false); return Task.CompletedTask; }, Keyed: true),
            new("the Gubble's runner ending its wish (failed)", BubbleEnd.PoppedByGubble, "popped", true, true, s => { RunnerUpdate(s.WishId, "failed"); return Task.CompletedTask; }),
            new("the Gubble's runner ending its wish (cancelled)", BubbleEnd.PoppedByGubble, "popped", true, true, s => { RunnerUpdate(s.WishId, "cancelled"); return Task.CompletedTask; }),
            // The player's own: never the Gubble's consequence.
            new("the dive key", BubbleEnd.PoppedByPlayer, "popped_by_player", false, false, _ => { _host.PlayerPopsBubble(); return Task.CompletedTask; }),
            new("X", BubbleEnd.PoppedByPlayer, "popped_by_player", false, false, _ => { _host.PlayerGoal("stop"); return Task.CompletedTask; }),
            new("X mid-lead", BubbleEnd.PoppedByPlayer, "popped_by_player", true, false, _ => { _host.PlayerGoal("stop"); return Task.CompletedTask; }),
            new("the player's effect.stop", BubbleEnd.PoppedByPlayer, "popped_by_player", false, false, s => { StopEffect(Player, BubbleOf(s.Carry)); return Task.CompletedTask; }),
            new("the player's wish.stop", BubbleEnd.PoppedByPlayer, "popped_by_player", true, true, _ => { _host.PlayerWishStop(); return Task.CompletedTask; }),
            new("the wish runner closed", BubbleEnd.PoppedByPlayer, "popped_by_player", true, true, _ => { _host.WishRunnerSession(false); return Task.CompletedTask; }),
            // The world's, and the player's orders to the Gubble: popped or timed out, never pending.
            new("the player's new order for the Gubble", BubbleEnd.Popped, "popped", true, false, _ => { GubbleGoal(Player, "follow"); return Task.CompletedTask; }),
            new("the player's recast", BubbleEnd.Popped, "popped", false, false,
                _ => { CastTheBubble(Player, _player.GlobalPosition + new Vector3(0.35f, 0, 0.1f)); return Task.CompletedTask; }),
            new("the timer", BubbleEnd.TimedOut, "timed_out", true, false, _ =>
            {
                Wait(20.5);
                _host.AdvanceBubbles(Tick);
                return Task.CompletedTask;
            }),
            new("a rules change", BubbleEnd.Popped, "popped", true, false, _ =>
            {
                _host.LoadRules("storybook_wild", 5);
                _host.AdvanceBubbles(Tick);
                return Task.CompletedTask;
            }),
            new("the body put down elsewhere", BubbleEnd.Popped, "popped", true, false, async _ =>
            {
                Check(_player.TryTeleportTo(_player.GlobalPosition + new Vector3(0.5f, 0, 0.5f)), "(the player is put down elsewhere)");
                await Frames(30);
            }),
            new("the body gone", BubbleEnd.Popped, "popped", true, false, _ =>
            {
                _world!.RemoveChild(_player);
                _host.AdvanceBubbles(Tick);
                _world.AddChild(_player);
                return Task.CompletedTask;
            }),
            new("the wish's action cap", BubbleEnd.Popped, "popped", true, true, s =>
            {
                // The lead was the wish's first action; 31 refused ones fill its 32, and the 33rd ends the wish (the kernel's bound, not the
                // Gubble's choice). Sent in halves, within the companion's 30 messages a second (the bubble's 20 s are not up by then).
                for (var i = 1; i <= WishQueue.MaxCommands; i++)
                {
                    if (i % 16 == 1) Wait(1.1);
                    Send(Command(s.Prefix + i, "goal.set", new JsonObject { ["actor"] = CommandHost.PlayerAvatar, ["goal"] = "stay" }), Companion);
                }
                return Task.CompletedTask;
            }),
            new("the room unloading", BubbleEnd.Popped, null, true, false, async _ => await FreeWorld()),
            // No pop at all: the bubble stays, the player in it.
            new("the Gubble's new lead of the same bubble", null, null, true, false, s =>
            {
                Lead(Companion, BubbleOf(s.Carry), _player.GlobalPosition + new Vector3(-0.4f, 0, 0.3f));
                return Task.CompletedTask;
            }),
        };
        var table = new List<string>();
        foreach (var path in paths)
        {
            await NewWorld("pop-" + new string(path.Name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant());
            if (path.Keyed) KeyedBubbles();
            var heard = new List<(BubbleEnd End, bool Pending)>();
            _host.BubbleEndedForTests = (end, pending) => heard.Add((end, pending));
            var wishId = "";
            var prefix = "";
            if (path.Wish)
            {
                _host.ConfigureWishes(new PopWishStore(_room.RoomId));
                _host.PlayerWish("Carry me over there");
                wishId = _host.HandleWishRunner("{\"op\":\"wish.wait\"}")["wish"]?["wish_id"]?.GetValue<string>() ?? "";
                prefix = _host.Wishes.Active?.ActionPrefix ?? "w-none-";
            }
            var feet = _player.GlobalPosition;
            var carry = TheBubbleOnThePlayer(Companion, duration: 20);
            // On a keyed island the Gubble's carry itself waited for the player's yes: approved here, it holds them as any other.
            if (path.Keyed) carry = Clicked(carry);
            JsonObject? led = null;
            if (path.Led)
                led = Send(Command(path.Wish ? prefix + "0" : NextId("lead"), "goal.set", new JsonObject
                {
                    ["actor"] = CommandHost.CompanionAvatarId, ["goal"] = "lead", ["target"] = BubbleOf(carry), ["position_m"] = Vec(feet + new Vector3(0.6f, 0, -0.3f)),
                }), Companion);
            var ready = Ok(carry) && _host.PlayerInBubble && (!path.Led || Ok(led!)) && (!path.Wish || wishId.Length > 0);
            await path.Pop(new PopScene(carry, led, wishId, prefix));
            var word = path.Word == null || _world == null ? null : ReceiptData(Companion, carry)?["ended"]?.GetValue<string>();
            var said = heard.Count == 0 ? "no pop" : string.Join(", ", heard.Select(h => $"{h.End}{(h.Pending ? " (fall pending)" : "")}"));
            table.Add($"{path.Name}: {said}");
            Check(ready && (path.End is { } end
                    ? heard.Count == 1 && heard[0].End == end && heard[0].Pending == (end == BubbleEnd.PoppedByGubble) && (path.Word == null || word == path.Word)
                    : heard.Count == 0 && _host.PlayerInBubble),
                $"decision 110, {path.Name}: {said}{(word != null ? $", its receipt {word}" : "")} (expected {(path.End?.ToString() ?? "no pop")})");
        }
        GD.Print("BUBBLE_POPS " + string.Join("; ", table));
        // A lead a ceiling stops ends blocked and pops nothing: the player is still held under it, nothing pending (BubbleTest.Review's ceiling).
        await NewWorld("pop-ceiling");
        var heardBlocked = new List<(BubbleEnd End, bool Pending)>();
        _host.BubbleEndedForTests = (end, pending) => heardBlocked.Add((end, pending));
        var top = _player.GlobalPosition;
        var slab = new StaticBody3D { Name = "PopCeiling", CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
        slab.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.8f, 0.02f, 0.8f) } });
        slab.Position = top + new Vector3(0, 0.3f, 0);
        _world!.AddChild(slab);
        await Frames(4);
        var held = TheBubbleOnThePlayer(Companion, duration: 60);
        var lift = Lead(Companion, BubbleOf(held), top + new Vector3(0.05f, 0.9f, 0));
        var state = await UntilLeadEnds(Companion, lift, 60 * 10);
        Check(Ok(held) && Ok(lift) && state == "failed" && ReceiptData(Companion, lift)?["ended"]?.GetValue<string>() == "blocked" && heardBlocked.Count == 0 && _host.PlayerInBubble,
            $"decision 110, a lead blocked too long (a ceiling over a lift): it ends blocked and pops nothing, the player still held ({state}, {heardBlocked.Count} pops)");
    }
}
