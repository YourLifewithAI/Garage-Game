using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The mechanics round's energy and vitals through the command path (M1 of docs/runs/RUN-2-MECHANICS.md), under storybook_wild
/// v5 whatever the shipped default: the price by principal charged once at commit (never on a preview, a replay, a stop or a
/// refusal), energy_exhausted with the balance and the price, Creative's shadow pool, a held command paying its own
/// principal's price at the click, a running wish making the Gubble's commands pay as ordered, the refill by distance and
/// difficulty, the status's energy, hearts and bar, fire in quarters and healing in whole quarters, the time away, and the
/// player's pickup of a Fractal from a manifest that places some.
/// </summary>
public partial class NatureTest
{
    private sealed class EnergyStore(PlayerModesProfile profile) : IPlayerVitalsStore
    {
        public PlayerModesProfile Saved { get; private set; } = profile;
        public int Saves { get; private set; }
        /// <summary>Every save refused, as a full disk would (the vitals store's PlayerVitalsException).</summary>
        public bool FailSave { get; set; }
        public PlayerModesProfile Load() => Saved;
        public void Save(PlayerModesProfile next)
        {
            if (FailSave) throw new PlayerVitalsException("The test store cannot save.");
            Saved = next;
            Saves++;
        }
    }

    private sealed class EnergyGlow(GlowProgress progress) : IGlowProgressStore
    {
        public GlowProgress Load() => progress;
        public void Save(GlowProgress state) => progress = state;
    }

    private async Task TestEnergy()
    {
        await TestPriceByPrincipal();
        await TestShadowPool();
        await TestHeldCommandPays();
        await TestWishPaysAsOrdered();
        await TestRefillAndStatus();
        await TestQuartersInPlay();
        await TestVitalsTimeAway();
        await TestFractals();
        await TestGather();
        await TestVitalsFixRound();
    }

    /// <summary>A world under v5 (or v5 edited, before the body is read) with a body of our choosing in a mode (and difficulty).</summary>
    private async Task<EnergyStore> EnergyWorld(string name, string mode = "challenge", int pips = 8, string difficulty = "standard", Func<PlayerVitals, PlayerVitals>? body = null,
        DateTime? written = null, RoomData? room = null, Action<JsonObject>? editPack = null)
    {
        await NewWorld(SavePath(name), roomOverride: room);
        _host.LoadRules("storybook_wild", 5);
        if (editPack != null) UseEditedV5(editPack);
        _host.UseDifficulty(difficulty);
        var rules = VitalsRules.For(_host.Rules, mode, difficulty);
        var vitals = rules.Fresh().With(pips: pips);
        if (body != null) vitals = body(vitals);
        var modes = PlayerModesProfile.Modes.ToDictionary(m => m, m => m == mode ? vitals : PlayerVitals.Fresh);
        var stamps = written is { } at ? new Dictionary<string, DateTime> { [mode] = at } : null;
        var store = new EnergyStore(PlayerModesProfile.Restore(mode, modes, stamps));
        _host.ConfigurePlayerVitals(store);
        return store;
    }

    private int Pips => _host.Vitals.Pips;

    private JsonObject GlowSelf(string principal, string? actionId = null, bool preview = false) =>
        Send(Command(actionId ?? NextId("glow"), "effect.start", new JsonObject
        {
            ["capability"] = "glow", ["params"] = new JsonObject(),
            ["area"] = new JsonObject { ["center_m"] = Vec(_companion.GlobalPosition), ["radius_m"] = 0.6 }, ["duration_s"] = 30,
            ["targets"] = new JsonArray(CommandHost.CompanionAvatarId),
        }, preview), principal);

    private static JsonArray Vec(Vector3 at) => new(at.X, at.Y, at.Z);

    private static bool Exhausted(JsonObject result, int balance, int price) =>
        Code(result) == "energy_exhausted" && result["error"]?["balance"]?.GetValue<int>() == balance && result["error"]?["price"]?.GetValue<int>() == price &&
        result["error"]?["message"]?.GetValue<string>() == CommandHost.EnergyExhaustedMessage && result["error"]?["retryable"]?.GetValue<bool>() == true &&
        result["error"]?["field_path"]?.GetValue<string>() == "$.args.capability";

    private async Task TestPriceByPrincipal()
    {
        await EnergyWorld("energy-price");
        var status = Send(Query("player.status", new JsonObject()), Companion)["data"];
        Check(status?["energy"]?["pips"]?.GetValue<int>() == 8 && status["energy"]?["max_pips"]?.GetValue<int>() == 8 && status["energy"]?["shadow"]?.GetValue<bool>() == false &&
              status["hearts"]?["quarters"]?.GetValue<int>() == 20 && status["hearts"]?["max_quarters"]?.GetValue<int>() == 20 &&
              status["bar"]?["bands"]?.GetValue<int>() == 1 && status["bar"]?["slowed"]?.GetValue<bool>() == false && status["max_health"]?.GetValue<double>() == 100,
            "player.status carries the pool (8 of 8), the hearts in quarters (20 of 20) and the bar (one band, not slowed): " + Canonical(status));
        var own = GlowSelf(Companion, "own-idea-glow");
        Check(Ok(own) && Pips == 7, $"the Gubble's own Glow costs its own-idea price, one pip ({Pips} left)");
        var previewed = GlowSelf(Companion, preview: true);
        var replayed = GlowSelf(Companion, "own-idea-glow");
        Check(Ok(previewed) && previewed["preview"]?.GetValue<bool>() == true && Ok(replayed) && replayed["replayed"]?.GetValue<bool>() == true && Pips == 7,
            "a preview and a replay of the same command cost nothing");
        var far = Send(Command(NextId("far"), "effect.start", new JsonObject
        {
            ["capability"] = "glow", ["params"] = new JsonObject(), ["area"] = new JsonObject { ["center_m"] = new JsonArray(1.9, 0.05, -1.4), ["radius_m"] = 0.6 }, ["duration_s"] = 30,
        }), Companion);
        Check(Code(far) == "out_of_bounds" && Pips == 7, "a refused command costs nothing: " + Code(far));
        var ordered = GlowSelf(Player);
        Check(Ok(ordered) && Pips == 5, $"the player's order of the same Glow pays the ordered price, two pips ({Pips} left)");
        var stop = Send(Command(NextId("stop"), "effect.stop", new JsonObject { ["effect"] = "all" }), Player);
        Check(Ok(stop) && Pips == 5, "a stop costs nothing");
        var bloom = Send(Command(NextId("bloom"), "nature.cast", Cast("grow", NearGubble, 0.15, 1)), Companion);
        Check(Ok(bloom) && Pips == 3 && bloom["transient"]?.GetValue<bool>() == false, $"the Gubble's own Grow costs two pips at commit, a durable change ({Pips} left)");
        var revision = _host.Revision;
        var blocked = Send(Command(NextId("bloom-player"), "nature.cast", Cast("grow", NearGubble + new Vector3(0.2f, 0, 0), 0.15, 1)), Player);
        Check(Exhausted(blocked, 3, 4) && _host.Revision == revision && Pips == 3,
            "the player's Grow at four pips with three in the pool is refused energy_exhausted, carrying the balance and the price, and nothing changes: " + Canonical(blocked["error"]));
        var lit = Fire(Companion, new Vector3(0.9f, 0.01f, 1.2f), 0.15, 6, out var firePreview);
        Check(Ok(firePreview) && Ok(lit) && Pips == 2, $"the Gubble's Fire is previewed free and costs one pip when it is lit, half of 2 ordered (decisions 83 and 94; {Pips} left)");
        // The player's own Glow spends the last two pips; put out again, so the next light is not held back by the island's count.
        var lastTwo = GlowSelf(Player);
        Send(Command(NextId("stop"), "effect.stop", new JsonObject { ["effect"] = "all" }), Player);
        Check(Ok(lastTwo) && Pips == 0, $"(the player's Glow spends the last two pips: {Pips} left)");
        var effects = _host.ActiveEffectIds.Count;
        var empty = GlowSelf(Companion);
        Check(Exhausted(empty, 0, 1) && _host.ActiveEffectIds.Count == effects, "an empty pool refuses even the cheapest cast, and nothing starts: " + Canonical(empty["error"]));
        var stillStops = _host.PlayerGoal("stop");
        Check(Ok(stillStops) && Send(Query("player.status", new JsonObject()), Companion)["data"]?["energy"]?["pips"]?.GetValue<int>() == 0,
            "stops still apply with an empty pool, and cost nothing");
    }

    private async Task TestShadowPool()
    {
        await EnergyWorld("energy-shadow", mode: "creative", pips: 2);
        var lit = Fire(Companion, new Vector3(0.9f, 0.01f, 1.2f), 0.15, 6, out _);
        var glow = GlowSelf(Companion);
        var status = Send(Query("player.status", new JsonObject()), Player)["data"];
        Check(Ok(lit) && Ok(glow) && Pips == 0 && status?["energy"]?["shadow"]?.GetValue<bool>() == true && status["consequences"]?.GetValue<bool>() == false,
            $"Creative's shadow pool drains with every cast, never below empty, and never refuses ({Pips} left): " + Canonical(status?["energy"]));
    }

    private async Task TestHeldCommandPays()
    {
        await EnergyWorld("energy-held");
        var text = File.ReadAllText(ProjectSettings.GlobalizePath(IslandRules.PathFor("storybook_wild", 5)));
        const string glowTier = "\"display_name\": \"Glow\",\n      \"cast_by\": [\"player\", \"companion\"],\n      \"tier\": \"auto\"";
        if (!text.Contains(glowTier, StringComparison.Ordinal)) throw new InvalidOperationException("v5's Glow no longer reads as expected");
        _host.UseRules(IslandRules.Parse(Encoding.UTF8.GetBytes(text.Replace(glowTier, glowTier.Replace("\"auto\"", "\"keyed_yes\""), StringComparison.Ordinal))));
        var held = GlowSelf(Companion);
        var request = held["error"]?["actual"]?.GetValue<string>() ?? held["request_id"]?.GetValue<string>() ?? _host.PendingApprovals.FirstOrDefault()?.RequestId;
        Check(Code(held) == "approval_required" && Pips == 8 && request != null, "a held command costs nothing while it waits for the player's click");
        var approved = _host.Approve(request!);
        Check(Ok(approved) && approved["approved_by"]?.GetValue<string>() == Player && Pips == 7,
            $"the click commits it under its own principal at that principal's price: the Gubble's one pip ({Pips} left)");
        _host.ConfigurePlayerVitals(new EnergyStore(PlayerModesProfile.Fresh.Select("challenge").WithCurrent(_host.BodyRules.Fresh().With(pips: 0))));
        var second = GlowSelf(Companion);
        var again = _host.PendingApprovals.FirstOrDefault()?.RequestId;
        var refused = again == null ? new JsonObject() : _host.Approve(again);
        Check(Code(second) == "approval_required" && Exhausted(refused, 0, 1) && _host.PendingApprovals.Count == 0,
            "a click that finds the pool empty ends the request with energy_exhausted, the balance and the price: " + Canonical(refused["error"]));
    }

    /// <summary>While a wish runs, the Gubble's commands pay as ordered, whatever their action ids say; a "w-" tag alone changes nothing.</summary>
    private async Task TestWishPaysAsOrdered()
    {
        await EnergyWorld("energy-wish");
        _host.ConfigureWishes(new LocalWishQueueStore(ProjectSettings.GlobalizePath($"{TestRoot}/energy-wish/wishes.json"), _room.RoomId));
        var tagged = GlowSelf(Companion, "w-not-a-wish");
        Check(!Ok(tagged) && Pips == 8, "a Gubble command tagged as a wish's with no wish running is refused and costs nothing: " + Code(tagged));
        var plain = GlowSelf(Companion);
        Check(Ok(plain) && Pips == 7, "without a wish the Gubble's own idea pays its own-idea price");
        Check(Ok(_host.PlayerWish("light the way")) && _host.HandleWishRunner("{\"op\":\"wish.wait\"}")["ok"]?.GetValue<bool>() == true && _host.Wishes.Active != null,
            "the player's wish is running");
        var during = GlowSelf(Companion);
        Check(Ok(during) && Pips == 5, $"while a wish runs the Gubble's command pays as ordered, two pips, though its action id names no wish ({Pips} left)");
    }

    private async Task TestRefillAndStatus()
    {
        await EnergyWorld("energy-refill", pips: 0);
        var near = _player.GlobalPosition.DistanceTo(_companion.GlobalPosition);
        _player.VitalsTick!(4.9f, true);
        var before = Pips;
        _player.VitalsTick!(0.2f, true);
        Check(near <= 2 && before == 0 && Pips == 1, $"with the Gubble within 2 m ({near:0.00} m) a pip refills every 5 s");
        _companion.GlobalPosition = _player.GlobalPosition + new Vector3(0, 0, -2.05f);
        var apart = _player.GlobalPosition.DistanceTo(_companion.GlobalPosition);
        _player.VitalsTick!(14.5f, true);
        var stillOne = Pips;
        _player.VitalsTick!(0.5f, true);
        Check(apart > 2 && stillOne == 1 && Pips == 2, $"apart ({apart:0.00} m) one every 15 s");
        _player.VitalsTick!(600, true);
        Check(Pips == 8, "and never past the pool's maximum");
        await EnergyWorld("energy-gentle", pips: 0, difficulty: "gentle");
        _player.VitalsTick!(10.1f, true);
        var gentle = Pips;
        await EnergyWorld("energy-brave", pips: 0, difficulty: "brave");
        _player.VitalsTick!(10.1f, true);
        Check(gentle == 3 && Pips == 1, $"Gentle refills 1.5 times as fast (3 in 10 s), Brave 0.75 times (1 in 10 s; got {gentle} and {Pips})");
        await EnergyWorld("energy-expertise", pips: 0);
        _host.ConfigureGlowProgress(new EnergyGlow(GlowProgress.Restore(40, new[] { "lantern", "torch", "bioluminesce", "constellation", "skylight" }, Array.Empty<string>())));
        var status = Send(Query("player.status", new JsonObject()), Companion)["data"];
        Check(status?["energy"]?["max_pips"]?.GetValue<int>() == 9, "a team that holds Glow's five expertise has a pool of 9: " + Canonical(status?["energy"]));
        await EnergyWorld("energy-creative-refill", mode: "creative", pips: 0);
        _player.VitalsTick!(5.1f, true);
        Check(Pips == 1, "Creative's shadow pool refills at the Standard pace");
    }

    private async Task TestQuartersInPlay()
    {
        await EnergyWorld("energy-fire");
        var lit = Fire(Player, PlayerSpot(), 0.25, 30, out _);
        Steps(1);
        var half = _host.Vitals.Health;
        Steps(1);
        var one = _host.Vitals.Health;
        Steps(2);
        var two = _host.Vitals.Health;
        Check(Ok(lit) && half == 100 && one == 95 && two == 90, $"fire takes one quarter a second, in whole quarters (after 0.5 s {half}, 1 s {one}, 2 s {two})");
        await EnergyWorld("energy-fire-gentle", difficulty: "gentle");
        Fire(Player, PlayerSpot(), 0.25, 30, out _);
        Steps(3);
        var gentleEarly = _host.Vitals.Health;
        Steps(1);
        Check(gentleEarly == 100 && _host.Vitals.Health == 95, $"Gentle halves fire and removes none: one quarter every 2 s ({gentleEarly}, then {_host.Vitals.Health})");
        // Under v5 hearts heal only at a camp (decision 84), a quarter a second there (91): away from it a minute's rest heals nothing.
        await EnergyWorld("energy-heal");
        var camp = _room.SpawnFor("player").PositionM;
        var away = camp + new Vector3(0.8f, 0, 0);
        Check(_player.TryTeleportTo(away) && !_host.PlayerAtCamp() && _host.HealQuarterEveryHereS() == null, "(the player stands away from the camp, where v5 heals nothing)");
        _host.ApplyPlayerDamage(14, PlayerDamageSource.Fall);
        var hurt = _host.Vitals.Health;
        for (var i = 0; i < 60; i++) _player.VitalsTick!(1f, true);
        Check(hurt == 85 && _host.Vitals.Health == 85,
            $"a hurt of 14 health takes three quarters (85), and a minute's rest on safe ground away from a camp heals none of it (decision 84; {_host.Vitals.Health})");
        // Resting is sitting by the fire (decisions 70 and 84, P1 of the second playtest round): standing at the camp heals nothing.
        Check(_player.TryTeleportTo(camp) && _host.PlayerAtCamp() && _host.HealQuarterEveryHereS() == null, "(the player stands at the camp, by the starting campfire, and does not sit)");
        await Frames(3);
        var standing = _host.Vitals.Health;
        for (var i = 0; i < 15; i++) _player.VitalsTick!(1f, true);
        Check(standing == 85 && _host.Vitals.Health == 85, $"standing at the camp heals nothing under v5: only resting there does ({standing}, {_host.Vitals.Health})");
        var sat = _host.PlayerRest();
        Check(sat.Ok && _host.PlayerResting && _host.HealQuarterEveryHereS() == 1, "(the player sits down by the fire: a quarter a second there) " + sat.Words);
        _host.ApplyPlayerDamage(_host.Vitals.Health - 5, PlayerDamageSource.Fall);
        var low = _host.Vitals.Health;
        _player.VitalsTick!(1.9f, true);
        _player.VitalsTick!(0.2f, true);
        var waited = _host.Vitals.Health;
        for (var i = 0; i < 4; i++) _player.VitalsTick!(1f, true);
        var fourSeconds = _host.Vitals.Health;
        _host.PlayerStand();
        for (var i = 0; i < 5; i++) _player.VitalsTick!(1f, true);
        var stoodUp = _host.Vitals.Health;
        Check(_player.TryTeleportTo(away) && !_host.PlayerAtCamp(), "(the player walks away from the camp)");
        for (var i = 0; i < 10; i++) _player.VitalsTick!(1f, true);
        var leftCamp = _host.Vitals.Health;
        Check(_player.TryTeleportTo(camp) && _host.PlayerAtCamp(), "(and back to the camp)");
        await Frames(3);
        var again = _host.PlayerRest();
        for (var i = 0; i < 15; i++) _player.VitalsTick!(1f, true);
        Check(again.Ok && low == 5 && waited == 5 && fourSeconds == 25 && stoodUp == 25 && leftCamp == 25 && _host.Vitals.Health == 100,
            $"resting at a camp heals nothing for 2 s after harm (decision 111, not 10), then a quarter every second (decision 91: five hearts in 20 s; 19 quarters in 19 s here); standing up or " +
            $"walking away stops it ({low}, {waited}, {fourSeconds}, {stoodUp}, {leftCamp}, {_host.Vitals.Health})");
        _host.ApplyPlayerDamage(PlayerVitals.ContractMaxHealth, PlayerDamageSource.Fall);
        var status = Send(Query("player.status", new JsonObject()), Companion)["data"];
        Check(_host.Vitals.Recoveries == 1 && _host.Vitals.Health == 50 && Pips >= 4 && status?["hearts"]?["quarters"]?.GetValue<int>() == 10,
            $"a knockout wakes at camp with half the hearts (10 quarters) and the pool at least half full ({Pips})");
        // Waking is no rest, even back on the spot where the player sat: the half stays until they sit down again.
        for (var i = 0; i < 20; i++) _player.VitalsTick!(1f, true);
        Check(!_host.PlayerResting && _host.Vitals.Health == 50, $"waking at camp with half the hearts stays: waking is not resting ({_host.Vitals.Health})");
        // A pack that names none of the three (as every pack before decision 84): the pack's own pace anywhere, twice as fast at a camp.
        await EnergyWorld("energy-heal-before", editPack: HealsAsBefore);
        Check(_player.TryTeleportTo(away) && !_host.PlayerAtCamp() && _host.HealQuarterEveryHereS() == 2.5, "(the player stands away from the camp)");
        _host.ApplyPlayerDamage(14, PlayerDamageSource.Fall);
        hurt = _host.Vitals.Health;
        _player.VitalsTick!(9.9f, true);
        var waiting = _host.Vitals.Health;
        _player.VitalsTick!(0.2f, true);
        _player.VitalsTick!(2.4f, true);
        var healed = _host.Vitals.Health;
        _player.VitalsTick!(5.1f, true);
        Check(hurt == 85 && waiting == 85 && healed == 90 && _host.Vitals.Health == 100,
            $"without them, a hurt of 14 health takes three quarters (85); nothing heals for 10 s, then a whole quarter every 2.5 s on safe ground ({waiting}, {healed}, {_host.Vitals.Health})");
        Check(_player.TryTeleportTo(camp) && _host.PlayerAtCamp() && _host.HealQuarterEveryHereS() == 1.25, "(the player stands at the camp, by the starting campfire)");
        _host.ApplyPlayerDamage(14, PlayerDamageSource.Fall);
        _player.VitalsTick!(9.9f, true);
        _player.VitalsTick!(0.2f, true);
        _player.VitalsTick!(2.4f, true);
        Check(_host.Vitals.Health == 95, $"and at a camp the same 2.6 s heal twice as much: two quarters, not one (Fire's camp, decision 62; {_host.Vitals.Health})");
        var bigHeart = await EnergyWorld("energy-big-heart");
        _host.UseKnacks(new Dictionary<string, int> { ["big_heart"] = 2 });
        var fall = PlayerVitals.FallDamage(MathF.Sqrt(2 * 3.5f * 1.5f), false);
        var top = _host.Vitals.MaxHealth;
        _host.ApplyPlayerDamage(fall, PlayerDamageSource.Fall);
        Check(top == 120 && _host.Vitals.Health == 120 - 16 * 6 && bigHeart.Saves > 0,
            $"with Big heart at full rank the maximum is 120 and the same 1.5 m fall takes 16 quarters of 6, not 19 ({_host.Vitals.Health} left)");
    }

    /// <summary>v5 without its four healing fields (decisions 84, 91, 93 and 111): hearts heal as every pack before them did.</summary>
    private static void HealsAsBefore(JsonObject pack)
    {
        var hearts = pack["traits"]!["hearts"]!.AsObject();
        foreach (var name in new[] { "heal_outside_camp", "camp_heal_quarter_every_s", "heal_while_closed", "camp_heal_after_unharmed_s" }) hearts.Remove(name);
    }

    private async Task TestVitalsTimeAway()
    {
        await EnergyWorld("energy-away", pips: 1, body: v => v.With(health: 50, stamina: 0), written: _now - TimeSpan.FromSeconds(60));
        Check(_host.Vitals.Health == 50 && Pips == 4 && Math.Abs(_host.Vitals.Stamina - _host.Vitals.MaxStamina) < 1e-6,
            $"a minute away under v5: the hearts as they were (decision 93: no healing while the game is closed), the bar refilled, and the pool woke half full ({_host.Vitals.Health}, {Pips}, {_host.Vitals.Stamina})");
        await EnergyWorld("energy-away-before", pips: 1, body: v => v.With(health: 50, stamina: 0), written: _now - TimeSpan.FromSeconds(60), editPack: HealsAsBefore);
        Check(_host.Vitals.Health == 100 && Pips == 4 && Math.Abs(_host.Vitals.Stamina - _host.Vitals.MaxStamina) < 1e-6,
            $"a pack that names no heal_while_closed heals as before: a minute away, the hearts healed at the resting pace, the bar refilled, the pool half full ({_host.Vitals.Health}, {Pips}, {_host.Vitals.Stamina})");
        await EnergyWorld("energy-away-short", pips: 6, body: v => v.With(health: 50), written: _now - TimeSpan.FromSeconds(12.5), editPack: HealsAsBefore);
        Check(_host.Vitals.Health == 55 && Pips == 6, $"and 12.5 s away heals one quarter after the 10 s wait, and a fuller pool keeps what it had ({_host.Vitals.Health}, {Pips})");
        await EnergyWorld("energy-away-never", pips: 1, body: v => v.With(health: 50));
        Check(_host.Vitals.Health == 50 && Pips == 1, "a mode never written by this host has no time away to catch up");
    }

    /// <summary>
    /// A copy of the test room whose manifest places two Fractals (one in front of the player's spawn, one across the room) and
    /// two loose things to gather beside the spawn: a log (bark) at its right and a stone at its left.
    /// </summary>
    private RoomData FractalRoom()
    {
        var source = ProjectSettings.GlobalizePath(RoomWorld.DefaultRoom);
        var target = ProjectSettings.GlobalizePath($"{TestRoot}/fractals/test_room");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".import", StringComparison.Ordinal)))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "room.json")))!.AsObject();
        manifest["extensions"] = new JsonObject
        {
            ["x_fractals"] = new JsonObject
            {
                ["typed"] = false,
                ["items"] = new JsonArray(
                    new JsonObject { ["id"] = "fractal:near01", ["position_m"] = new JsonArray(0.0, 0.0, 0.5), ["reach_band"] = 1 },
                    new JsonObject { ["id"] = "fractal:far02", ["position_m"] = new JsonArray(1.2, 0.0, 0.6) }),
            },
        };
        var files = manifest["files"]!.AsArray();
        var objects = manifest["objects"]!.AsArray();
        foreach (var (id, asset, name, role, size, x) in new[] { ("obj:log", "log_proxy", "Log", "bark", new[] { 0.03, 0.03, 0.1 }, 0.12), ("obj:stone", "stone_proxy", "Stone", "stone", new[] { 0.04, 0.035, 0.04 }, -0.12) })
        {
            var record = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "objects/doorstop_proxy/asset.json")))!.AsObject();
            record["asset_id"] = asset;
            record["display_name"] = name;
            record["category"] = name == "Log" ? "woodpile" : "rock";
            record["category_group"] = "decor";
            record["dimensions_m"] = new JsonArray(size.Select(v => (JsonNode?)v).ToArray());
            record["physics"] = new JsonObject { ["movable"] = true, ["mass_kg"] = 0.03 };
            record["materials"] = new JsonArray(new JsonObject { ["slot"] = "body", ["role"] = role, ["base_color"] = "#8b6f59" });
            record["affordances"] = new JsonArray("walkable_top");
            var bytes = Encoding.UTF8.GetBytes(record.ToJsonString());
            var relative = $"objects/{asset}/asset.json";
            Directory.CreateDirectory(Path.Combine(target, "objects", asset));
            File.WriteAllBytes(Path.Combine(target, relative), bytes);
            files.Add(new JsonObject { ["path"] = relative, ["sha256"] = CanonicalJson.Sha256Hex(bytes), ["bytes"] = bytes.Length });
            objects.Add(new JsonObject
            {
                ["id"] = id, ["asset"] = relative,
                ["transform"] = new JsonObject { ["position_m"] = new JsonArray(x, 0.0, 0.62), ["rotation"] = new JsonArray(0.0, 0.0, 0.0, 1.0) },
                ["support"] = new JsonObject { ["kind"] = "shell", ["target_id"] = "shell:floor" },
            });
        }
        File.WriteAllText(Path.Combine(target, "room.json"), manifest.ToJsonString(), new UTF8Encoding(false));
        return RoomData.Load($"{TestRoot}/fractals/test_room");
    }

    private async Task TestFractals()
    {
        // The contract's example manifest's Fractals read as the kernel reads them.
        var example = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(ProjectSettings.GlobalizePath("res://") + "../contracts/examples/rooms/island_example/room.json"));
        var parsed = CommandHost.ParseFractals(example.RootElement.GetProperty("extensions").GetProperty("x_fractals"), new Aabb(new Vector3(-16, -1, -16), new Vector3(32, 7, 16)), out var typed);
        Check(parsed is { Count: 3 } && !typed && parsed.Select(f => f.Id).SequenceEqual(new[] { "fractal:k3x9q2", "fractal:p7m2c5", "fractal:w4n8t6" }) && parsed[1].ReachBand == 2,
            "the contract's example island places three untyped Fractals, read in id order with their reach bands");
        example.Dispose();
        var room = FractalRoom();
        var store = await EnergyWorld("energy-fractals", room: room);
        var near = "fractal:near01";
        Check(_host.AllFractals.Count == 2 && _host.FractalsNotice.Length == 0, "the host reads the manifest's two Fractals: " + _host.FractalsNotice);
        var listed = Send(Query("entities.list", new JsonObject { ["filter"] = new JsonObject { ["kind"] = "fractal" } }), Player)["data"]?["items"]?.AsArray();
        var summary = listed?.FirstOrDefault(e => e?["id"]?.GetValue<string>() == near);
        Check(listed?.Count == 2 && summary?["kind"]?.GetValue<string>() == "fractal" && summary["movable"]?.GetValue<bool>() == false &&
              summary["provenance_kind"]?.GetValue<string>() == "procedural" && summary["display_name"]?.GetValue<string>() == "Fractal",
            "Fractals are entities of kind fractal: listed, small, not movable, placed by the generator: " + Canonical(summary));
        var seen = Send(Query("observe", new JsonObject { ["actor"] = CommandHost.CompanionAvatarId }), Companion)["data"]?["visible"]?.AsArray()
            .FirstOrDefault(e => e?["id"]?.GetValue<string>() == near);
        Check(seen?["kind"]?.GetValue<string>() == "fractal" && seen["seen"]?.GetValue<string>() == "now",
            "the Gubble sees a Fractal in its line of sight, kind fractal (it may find one, go to it and look at it): " + Canonical(seen));
        var grab = Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = near, ["actor"] = CommandHost.CompanionAvatarId }), Companion);
        var mine = Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = near, ["actor"] = CommandHost.PlayerAvatar }), Player);
        Check(Code(grab) == "permission_denied" && Code(mine) == "invalid_args" && mine["error"]?["message"]?.GetValue<string>().Contains("item.pickup", StringComparison.Ordinal) == true,
            "the Gubble never gathers one; the player's own grab says a Fractal is picked up, not carried");
        var far = Send(Command(NextId("pickup"), "item.pickup", new JsonObject { ["target"] = "fractal:far02" }), Player);
        Check(Code(far) == "out_of_bounds" && far["error"]?["retryable"]?.GetValue<bool>() == true, "a Fractal across the room is out of reach: come closer");
        var revision = _host.Revision;
        var preview = Send(Command(NextId("pickup"), "item.pickup", new JsonObject { ["target"] = near }, preview: true), Player);
        Check(Ok(preview) && _host.Revision == revision && _host.Fractals.Count() == 2, "a preview picks nothing up");
        var pickup = Command("pickup-near", "item.pickup", new JsonObject { ["target"] = near });
        var picked = Send(pickup, Player);
        Check(Ok(picked) && picked["transient"]?.GetValue<bool>() == false && picked["revision"]?.GetValue<int>() == revision + 1 && picked["data"] == null &&
              picked["affected"]?.AsArray().Select(a => a!.GetValue<string>()).SequenceEqual(new[] { near }) == true && Pips == 8,
            "the player picks it up: one durable room revision, the Fractal in affected, no data, no pips: " + Canonical(picked));
        Check(_host.Vitals.Fractals == new PlayerFractals(1, 1, 0, 0) && store.Saved.For("challenge").Fractals.Found == 1 && _host.Fractals.Count() == 1 &&
              _host.Nature.FractalsPicked.SetEquals(new[] { near }),
            "it leaves the island into this mode's count (found 1, in hand 1), saved with the vitals, and the island keeps it as picked up");
        var replay = Send(pickup, Player);
        var again = Send(Command(NextId("pickup"), "item.pickup", new JsonObject { ["target"] = near }), Player);
        var lookup = Send(Query("receipt.lookup", new JsonObject { ["action_id"] = "pickup-near" }), Player);
        Check(replay["replayed"]?.GetValue<bool>() == true && Code(again) == "target_not_found" && lookup["data"]?["found"]?.GetValue<bool>() == true && _host.Vitals.Fractals.Found == 1,
            "a retry replays the receipt, a second pickup finds nothing there, and the count moves once");
        var fact = Send(Query("journal.read", new JsonObject { ["kind"] = "found" }), Player)["data"]?["entries"]?.AsArray().LastOrDefault();
        Check(fact?["line"]?.GetValue<string>() == "You found a Fractal (1 found)." && fact["quantity"]?["count"]?.GetValue<int>() == 1,
            "the journal keeps the reward's pace line, in the game's words: " + Canonical(fact));
        var state = _host.ExportRoomState();
        Check(state?["extensions"]?["x_play_nature"]?["fractals"]?.AsArray().Select(a => a!.GetValue<string>()).SequenceEqual(new[] { near }) == true &&
              NatureBlock.Problem(state["extensions"]!["x_play_nature"], _host.Revision, room) == null,
            "room state keeps the Fractal picked up in the host's nature block, which checks whole");
        var companionList = Send(Query("entities.list", new JsonObject { ["filter"] = new JsonObject { ["kind"] = "fractal" } }), Companion)["data"]?["items"]?.AsArray();
        Check(companionList != null && companionList.All(e => e?["id"]?.GetValue<string>() != near), "a Fractal picked up is gone for the Gubble too");
        // The island save reloads (a new host on the same save path) with the Fractal still gone and its receipt still replaying.
        await EnergyWorld("energy-fractals", room: room);
        var replayed = Send(pickup, Player);
        Check(_host.Authority.Call("is_ready").AsBool() && _host.Fractals.Select(f => f.Id).SequenceEqual(new[] { "fractal:far02" }) &&
              replayed["replayed"]?.GetValue<bool>() == true && replayed["affected"]?[0]?.GetValue<string>() == near,
            "after a save and reload the Fractal is still gone and its receipt still replays");
    }

    /// <summary>
    /// Gathering (decision 26): the player picks a loose log up and gathers it into the wallet (wood); it leaves the island as
    /// one durable revision, the wallet's count shows on player.status, and a save and reload keep both. The companion is
    /// refused before anything is looked up; nothing held, or a thing that is no log or stone, is request_invalid.
    /// </summary>
    private async Task TestGather()
    {
        var room = FractalRoom();
        await EnergyWorld("energy-gather", room: room);
        var log = room.Objects.First(o => o.Id == "obj:log");
        Check(CommandHost.GatherMaterial(log) == "wood" && CommandHost.GatherMaterial(room.Objects.First(o => o.Id == "obj:stone")) == "stone" &&
              CommandHost.GatherMaterial(room.Objects.First(o => o.Id == "obj:book")) == null && CommandHost.GatherMaterial(room.Objects.First(o => o.Id == "obj:box")) == null,
            "a log (bark) gathers as wood and a stone as stone; a book or a cardboard box is no loose log or stone");
        var before = Send(Query("player.status", new JsonObject()), Player)["data"]?["wallet"];
        // The pack's starting wallet (decision 26: v5's is empty).
        var wood = _host.Rules!.Traits!.Wallet!.Wood;
        var stone = _host.Rules.Traits.Wallet.Stone;
        var companion = Send(Command(NextId("gather"), "item.gather", new JsonObject { ["target"] = "obj:log" }), Companion);
        var nothing = Send(Command(NextId("gather"), "item.gather", new JsonObject { ["target"] = "obj:log" }), Player);
        Check(Code(companion) == "permission_denied" && companion["error"]?["field_path"]?.GetValue<string>() == "$.op" &&
              Code(nothing) == "request_invalid" && nothing["error"]?["field_path"]?.GetValue<string>() == "$.args.target" &&
              before?["wood"]?.GetValue<int>() == wood && before["stone"]?.GetValue<int>() == stone,
            "the companion is refused gathering outright, and the player holding nothing is told to pick it up first; the wallet starts as the pack says: " + Canonical(before));
        var grab = Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:log", ["actor"] = CommandHost.PlayerAvatar }), Player);
        var other = Send(Command(NextId("gather"), "item.gather", new JsonObject { ["target"] = "obj:stone" }), Player);
        Check(Ok(grab) && _host.HeldBy(CommandHost.PlayerAvatar) == "obj:log" && Code(other) == "request_invalid",
            "the player picks the log up; gathering a thing it does not hold is request_invalid: " + Code(grab));
        var revision = _host.Revision;
        var preview = Send(Command(NextId("gather"), "item.gather", new JsonObject { ["target"] = "obj:log" }, preview: true), Player);
        Check(Ok(preview) && _host.Revision == revision && _host.HeldBy(CommandHost.PlayerAvatar) == "obj:log", "a preview gathers nothing");
        var gather = Command("gather-log", "item.gather", new JsonObject { ["target"] = "obj:log" });
        var gathered = Send(gather, Player);
        var status = Send(Query("player.status", new JsonObject()), Player)["data"];
        var entities = Send(Query("entities.list", new JsonObject()), Player)["data"]?["items"]?.AsArray();
        Check(Ok(gathered) && gathered["transient"]?.GetValue<bool>() == false && gathered["revision"]?.GetValue<int>() == revision + 1 && gathered["data"] == null &&
              gathered["affected"]?.AsArray().Select(a => a!.GetValue<string>()).SequenceEqual(new[] { "obj:log" }) == true,
            "the log is gathered: one durable room revision, the log in affected, no data: " + Canonical(gathered));
        Check(status?["wallet"]?["wood"]?.GetValue<int>() == wood + CommandHost.GatherUnits && status["wallet"]?["stone"]?.GetValue<int>() == stone &&
              _host.HeldBy(CommandHost.PlayerAvatar) == null && entities?.All(e => e?["id"]?.GetValue<string>() != "obj:log") == true &&
              _host.Nature.Objects["obj:log"] is { Gathered: true, BurnedDown: true },
            "the wallet holds one more wood on player.status, the player's hands are empty and the log has left the island: " + Canonical(status?["wallet"]));
        var replay = Send(gather, Player);
        var fact = Send(Query("journal.read", new JsonObject { ["kind"] = "gathered" }), Player)["data"]?["entries"]?.AsArray().LastOrDefault();
        Check(replay["replayed"]?.GetValue<bool>() == true && _host.Nature.Wallet["wood"] == wood + CommandHost.GatherUnits &&
              fact?["line"]?.GetValue<string>() == $"You gathered \"Log\": {wood + CommandHost.GatherUnits} wood in the wallet." && fact["quantity"]?["count"]?.GetValue<int>() == wood + CommandHost.GatherUnits,
            "a retry replays and counts nothing twice; the journal keeps a gathered fact: " + Canonical(fact));
        var state = _host.ExportRoomState();
        var removed = state?["entities"]?["obj:log"];
        Check(removed?["removed"]?.GetValue<bool>() == true && state?["extensions"]?["x_play_nature"]?["objects"]?["obj:log"]?["gathered"]?.GetValue<bool>() == true &&
              NatureBlock.Problem(state!["extensions"]!["x_play_nature"], _host.Revision, room) == null,
            "room state keeps the log as a removed entity, marked gathered in the host's nature block, which checks whole");
        // A pickup and a gather survive a save and reload (the authority keeps both receipts: its NATURE_OPS).
        var pickup = Command("pickup-in-gather", "item.pickup", new JsonObject { ["target"] = "fractal:near01" });
        Check(Ok(Send(pickup, Player)), "the player also picks up the Fractal before the reload");
        await EnergyWorld("energy-gather", room: room);
        var again = Send(gather, Player);
        var picked = Send(pickup, Player);
        var reloaded = Send(Query("player.status", new JsonObject()), Player)["data"]?["wallet"];
        Check(_host.Authority.Call("is_ready").AsBool() && again["replayed"]?.GetValue<bool>() == true && picked["replayed"]?.GetValue<bool>() == true &&
              reloaded?["wood"]?.GetValue<int>() == wood + CommandHost.GatherUnits && _host.Nature.Objects["obj:log"].Gathered && !_host.Fractals.Any(f => f.Id == "fractal:near01") &&
              Send(Query("entities.list", new JsonObject()), Player)["data"]?["items"]?.AsArray().All(e => e?["id"]?.GetValue<string>() != "obj:log") == true,
            "after a save and reload the log is still gone, the wallet still holds it, the Fractal is still picked up, and both receipts replay");
    }
}
