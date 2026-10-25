using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// M1's fix round (the Wave 1 review of the mechanics round): a launch reads the vitals before the island's rules and the team's
/// knacks (RoomWorld: the store in CommandHost.Attach, then LoadRules, then the team's knacks and the boot's one set-mode), and no
/// launch changes a saved body; player.status keeps its bounds at the top (720 health, 4.8 m), and so does the Gubble's notice; a
/// new island's wallet is the pack's start.
/// </summary>
public partial class NatureTest
{
    private static readonly Dictionary<string, int> BigHeart = new() { ["big_heart"] = 2 };
    private static readonly Dictionary<string, int> TopKnacks = new() { ["big_bar"] = 3, ["big_heart"] = 2 };

    private async Task TestVitalsFixRound()
    {
        await TestLaunchesKeepTheBody();
        await TestLaunchesKeepTheTop();
        await TestStartingWallet();
        await TestPickupCountedFirst();
        await TestEmptyPoolGivesTheRealReason();
        await TestSpendFractals();
        await TestSpendAtTheCaps();
        await TestSpendKeepsAcrossLaunches();
    }

    /// <summary>The real store's file for a team's mode under this suite's root (an absolute path, as the store needs).</summary>
    private static string TeamVitals(string team, string mode) => ProjectSettings.GlobalizePath($"{TestRoot}/teams/{team}/modes/{mode}/vitals.json");

    private static void SaveBody(string team, string mode, DateTime written, PlayerVitals body)
    {
        var path = TeamVitals(team, mode);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, LocalPlayerVitalsStore.SerializeMode(team, mode, written, body));
    }

    private static PlayerVitals SavedBody(string team, string mode) => LocalPlayerVitalsStore.ParseMode(File.ReadAllBytes(TeamVitals(team, mode))).Vitals;

    /// <summary>
    /// One launch in the game's order (RoomWorld): the vitals store as the host is attached, then the island's rules, then the
    /// team's knacks and the boot's one set-mode (TeamBoot.Apply), all before the first frame. editPack: the island's pack is v5 so
    /// edited (UseEditedV5), as the pack the launch loads.
    /// </summary>
    private async Task Launch(string team, IReadOnlyDictionary<string, int> knacks, RoomData? room = null, bool packLoads = true, Action<JsonObject>? editPack = null)
    {
        JsonObject? booted = null;
        await NewWorld(SavePath("launch-" + team), roomOverride: room,
            beforeRules: () => _host.ConfigurePlayerVitals(new LocalPlayerVitalsStore(TeamVitals(team, "challenge"))),
            afterRules: () =>
            {
                if (editPack != null) UseEditedV5(editPack);
                // A pack that did not load leaves the host with no rules (LoadRules' notice), as before teams.
                if (!packLoads) _host.UseRules(null, "The island's rules did not load (a test).");
                _host.UseKnacks(knacks);
                booted = _host.BootMode(team, "challenge", "standard");
            });
        Check(booted != null && Ok(booted) && _host.Mode == "challenge", $"the boot plays {team}'s Challenge: " + Canonical(booted));
    }

    private string Quarters() => $"{_host.BodyRules.Quarters(_host.Vitals)} of {_host.BodyRules.MaxQuarters(_host.Vitals)} quarters ({_host.Vitals.Health}/{_host.Vitals.MaxHealth})";

    /// <summary>
    /// Review finding 1: with Big heart a body saved at 10 of 20 quarters booted at 12, then 14, then 17, because the vitals were
    /// read under no rules (100 health, 0.45 m) before the pack and the knacks stretched them back. Every launch now keeps it; time
    /// away heals it once, at the island's own pace.
    /// </summary>
    private async Task TestLaunchesKeepTheBody()
    {
        var rules = IslandRules.Load("storybook_wild", 5);
        var hearty = VitalsRules.For(rules, "challenge", "standard", BigHeart);
        var body = hearty.Harmed(hearty.Fresh(), 10);
        Check(hearty.Quarters(body) == 10 && body.Health == 60 && body.MaxHealth == 120, "the saved body: 10 of 20 quarters of 6 with Big heart at full rank");
        SaveBody("hearty", "challenge", _now, body);
        for (var launch = 1; launch <= 3; launch++)
        {
            await Launch("hearty", BigHeart);
            Check(_host.BodyRules.Quarters(_host.Vitals) == 10 && _host.BodyRules.MaxQuarters(_host.Vitals) == 20 && _host.Vitals.Health == 60 && _host.Vitals.MaxHealth == 120,
                $"launch {launch} reads the body the save holds: {Quarters()}");
            await FreeWorld();
            var saved = SavedBody("hearty", "challenge");
            Check(saved.Health == 60 && saved.MaxHealth == 120 && saved.Stamina == body.Stamina && saved.MaxStamina == body.MaxStamina,
                $"and leaves it saved as it was: {saved.Health}/{saved.MaxHealth}, {saved.Stamina}/{saved.MaxStamina} m");
        }
        // 12.5 s away under a pack that heals while the game is closed (every pack before decision 93): one quarter heals after the
        // 10 s wait, a quarter of 6 (the island's pace with the team's knack), once.
        _now += TimeSpan.FromSeconds(12.5);
        await Launch("hearty", BigHeart, editPack: HealsAsBefore);
        Check(_host.BodyRules.Quarters(_host.Vitals) == 11 && _host.Vitals.Health == 66, "time away heals one quarter of 6 under the island's rules: " + Quarters());
        await FreeWorld();
        await Launch("hearty", BigHeart, editPack: HealsAsBefore);
        Check(_host.Vitals.Health == 66 && SavedBody("hearty", "challenge").Health == 66, "and the next launch heals nothing more: " + Quarters());
        await FreeWorld();
        // Under v5 (decision 93) the time away heals nothing: the player comes back with the hearts they had.
        _now += TimeSpan.FromSeconds(12.5);
        await Launch("hearty", BigHeart);
        Check(_host.Vitals.Health == 66 && SavedBody("hearty", "challenge").Health == 66, "under v5 12.5 s away heals nothing (decision 93): " + Quarters());
        await FreeWorld();
    }

    /// <summary>
    /// Review findings 1 and 3: a top-band body (30 hearts and ten bands, Big bar and Big heart at full rank: 720 health, 4.8 m)
    /// booted at 0.45 m, and its player.status wrote 4.8 m as 4.800000190734863, past the contract's bound. Every launch keeps
    /// it, and every bounded field reads at its top within the bound: the status and the Gubble's notice.
    /// </summary>
    private async Task TestLaunchesKeepTheTop()
    {
        var rules = IslandRules.Load("storybook_wild", 5);
        var top = VitalsRules.For(rules, "challenge", "standard", TopKnacks);
        var grown = top.Normalize(top.Fresh().With(fractals: new PlayerFractals(170, 0, 25, 9)));
        grown = grown.With(health: grown.MaxHealth, stamina: grown.MaxStamina);
        Check(grown.MaxHealth == PlayerVitals.ContractMaxHealth && grown.MaxStamina == PlayerVitals.ContractMaxStamina && grown.Health == 720 && grown.Stamina == 4.8f,
            $"the top body: 30 hearts of 6 (720) and ten bands with Big bar (4.8 m): {grown.Health}/{grown.MaxHealth}, {grown.Stamina}/{grown.MaxStamina}");
        SaveBody("summit", "challenge", _now, grown);
        for (var launch = 1; launch <= 3; launch++)
        {
            await Launch("summit", TopKnacks);
            var vitals = _host.Vitals;
            Check(vitals.Health == 720 && vitals.MaxHealth == 720 && vitals.Stamina == 4.8f && vitals.MaxStamina == 4.8f,
                $"launch {launch} reads the top body as saved: {vitals.Health}/{vitals.MaxHealth}, {vitals.Stamina}/{vitals.MaxStamina} m");
            var text = _host.Handle(Query("player.status", new JsonObject()).ToJsonString(), Companion);
            var status = Send(Query("player.status", new JsonObject()), Companion)["data"];
            // "stamina" is the answer's last key since the wallet's retirement (P-Inventory-2), so it ends the object.
            Check((text.Contains("\"stamina\":4.8,", StringComparison.Ordinal) || text.Contains("\"stamina\":4.8}", StringComparison.Ordinal)) && text.Contains("\"max_stamina\":4.8,", StringComparison.Ordinal) &&
                  text.Contains("\"health\":720,", StringComparison.Ordinal) && text.Contains("\"max_health\":720,", StringComparison.Ordinal) &&
                  status?["stamina"]?.GetValue<double>() <= 4.8 && status["max_stamina"]?.GetValue<double>() <= 4.8 &&
                  status["hearts"]?["quarters"]?.GetValue<int>() == 120 && status["bar"]?["bands"]?.GetValue<int>() == 10,
                "player.status at the top stays within its bounds (720, 4.8 m): " + text);
            await FreeWorld();
            var saved = SavedBody("summit", "challenge");
            Check(saved.Health == 720 && saved.MaxHealth == 720 && saved.Stamina == 4.8f && saved.MaxStamina == 4.8f, $"and leaves it saved at the top: {saved.Health}, {saved.Stamina} m");
        }
        // A launch whose pack does not load reads the body under no rules: kept as saved, its own maxima and quarters, nothing
        // caught up; the next launch with the pack finds it as it was.
        await Launch("summit", TopKnacks, packLoads: false);
        Check(_host.Rules == null && _host.Vitals.Health == 720 && _host.Vitals.MaxHealth == 720 && _host.Vitals.Stamina == 4.8f && _host.Vitals.MaxStamina == 4.8f,
            $"with no rules loaded the body is kept as saved: {_host.Vitals.Health}/{_host.Vitals.MaxHealth}, {_host.Vitals.Stamina}/{_host.Vitals.MaxStamina} m");
        await FreeWorld();
        var kept = SavedBody("summit", "challenge");
        Check(kept.Health == 720 && kept.MaxHealth == 720 && kept.Stamina == 4.8f && kept.MaxStamina == 4.8f, $"and saved as it was: {kept.Health}, {kept.Stamina} m");
        // The Gubble's notice at the top: its fire hurts a 720 body, and health_before reads 720 within the bound.
        await Launch("summit", TopKnacks);
        var lit = Fire(Companion, PlayerSpot(), 0.25, 6, out _);
        Steps(2);
        var notice = Send(Query("player.status", new JsonObject()), Companion)["notices"]?[0];
        Check(Ok(lit) && notice?["kind"]?.GetValue<string>() == "fire_hurt_player" && notice["health_before"]?.GetValue<double>() == 720 &&
              notice["health_after"]?.GetValue<double>() < 720, "the Gubble's notice reads a 720 body within its bound: " + Canonical(notice));
        await FreeWorld();
    }

    /// <summary>
    /// Review finding 2 (decision 26) made the pack's traits.wallet.start a new island's wallet; P-Inventory-2 (decision 116) retires the
    /// wallet: logs and stones are stacks in the pockets and the chest. A new island has no wallet under any pack (traits.wallet is read
    /// and ignored), player.status carries none, and the island still counts as untouched, so a room import or a migration may land on it.
    /// </summary>
    private async Task TestStartingWallet()
    {
        await NewWorld(SavePath("wallet-start"));
        var start = _host.Rules?.Traits?.Wallet;
        var status = Send(Query("player.status", new JsonObject()), Player)["data"];
        Check(start == new WalletRules(0, 0, 0, 0) && status?["wallet"] == null && _host.Nature.Wallet.Count == 0,
            "a new v5 island has no wallet, and player.status carries none (the pack's traits.wallet is read and ignored): " + Canonical(status));
        Check(_host.Nature.Untouched(trailAllowed: false, _host.StartWallet),
            "and the new island still counts as untouched, so a room import or a migration may still land on it");
        _host.UseRules(IslandRules.Load("storybook_wild", 4));
        Check(_host.Nature.Wallet.Count == 0 && Send(Query("player.status", new JsonObject()), Player)["data"]?["wallet"] == null &&
              _host.Nature.Untouched(trailAllowed: false, _host.StartWallet),
            "nor under a pack without a wallet block (once today's 20 wood, 10 stone, 10 copper and 20 leaf)");
        await FreeWorld();
    }

    /// <summary>
    /// The review's minor: a pickup wrote the island save before the vitals, so a vitals save that failed lost the Fractal. It is
    /// counted first now: a count that cannot be saved leaves it on the island, and nothing moves.
    /// </summary>
    private async Task TestPickupCountedFirst()
    {
        var store = await EnergyWorld("pickup-counted-first", room: FractalRoom());
        store.FailSave = true;
        var revision = _host.Revision;
        var refused = Send(Command(NextId("pickup"), "item.pickup", new JsonObject { ["target"] = "fractal:near01" }), Player);
        Check(Code(refused) == "not_ready" && refused["error"]?["retryable"]?.GetValue<bool>() == true && _host.Revision == revision &&
              _host.Fractals.Count() == 2 && _host.Nature.FractalsPicked.Count == 0 && _host.Vitals.Fractals.Found == 0,
            "a count the vitals store cannot save refuses the pickup, and the Fractal stays on the island: " + Canonical(refused["error"]));
        store.FailSave = false;
        var picked = Send(Command(NextId("pickup"), "item.pickup", new JsonObject { ["target"] = "fractal:near01" }), Player);
        Check(Ok(picked) && _host.Revision == revision + 1 && _host.Vitals.Fractals.Found == 1 && store.Saved.For("challenge").Fractals.Found == 1 &&
              _host.Nature.FractalsPicked.Contains("fractal:near01"), "once it can save, the pickup counts it and the island lets it go");
        await FreeWorld();
    }

    /// <summary>
    /// The review's minor: the energy guard ran before a fire's own "Nothing can take hold", so an empty pool gave the wrong
    /// reason. Dry fuel round the edge of the area but none where a fire catches (its centre): the real reason comes back.
    /// </summary>
    private async Task TestEmptyPoolGivesTheRealReason()
    {
        await EnergyWorld("fire-no-hold", pips: 0);
        var centre = PlayerSpot();
        _host.NatureSampleForTests = (x, z) =>
        {
            var cell = Soil(x, z)!;
            if (new Vector2((x + 0.5f) * NatureState.CellM - centre.X, (z + 0.5f) * NatureState.CellM - centre.Z).Length() <= NatureLaws.IgnitionRadiusM + NatureState.CellM)
                cell.Moisture = 0.9f;
            return cell;
        };
        var lit = Fire(Player, centre, 0.25, 30, out var preview);
        Check(Ok(preview) && Code(lit) == "invalid_args" && Message(lit) == "Nothing can take hold at the center of that area." && Pips == 0,
            "an empty pool is not the reason a fire that cannot take hold gives: " + Canonical(lit["error"]));
        _host.NatureSampleForTests = Soil;
        await FreeWorld();
    }

    private JsonObject Spend(string grow, string principal = Player, bool preview = false, string? actionId = null) =>
        Send(Command(actionId ?? NextId("spend"), "fractal.spend", new JsonObject { ["grow"] = grow }, preview), principal);

    private static string? Message(JsonObject result) => result["error"]?["message"]?.GetValue<string>();
    private static bool Retryable(JsonObject result) => result["error"]?["retryable"]?.GetValue<bool>() == true;
    private static string? FieldPath(JsonObject result) => result["error"]?["field_path"]?.GetValue<string>();

    /// <summary>
    /// fractal.spend (the second contract round; decision 41): the player at the camp spends five Fractals for a heart that arrives
    /// full, or five for the next band whose metres join the bar; transient, no data, kept in this mode's vitals; refused to the
    /// companion, away from the camp and with too few in hand, in a child's words; a pace line in the journal.
    /// </summary>
    private async Task TestSpendFractals()
    {
        var store = await EnergyWorld("spend", body: v => v.With(health: 50, stamina: 0.2f, fractals: new PlayerFractals(13, 13, 0, 0)));
        var companion = Spend("heart", Companion);
        Check(Code(companion) == "permission_denied" && FieldPath(companion) == "$.op" && _host.Vitals.Fractals.Unspent == 13, "the companion never spends the player's Fractals");
        var camp = _player.GlobalPosition;
        _player.GlobalPosition = camp + new Vector3(0.8f, 0, 0);
        var away = Spend("heart");
        _player.GlobalPosition = camp;
        Check(Code(away) == "not_ready" && Retryable(away) && Message(away) == CommandHost.FractalsAtCamp && _host.Vitals.Fractals.Unspent == 13,
            "away from the camp: go back to the spawn (not_ready, retryable): " + Message(away));
        var saves = store.Saves;
        var preview = Spend("heart", preview: true);
        Check(Ok(preview) && preview["preview"]?.GetValue<bool>() == true && _host.Vitals.Fractals.Unspent == 13 && store.Saves == saves, "a preview spends nothing");
        var spend = Command("spend-heart", "fractal.spend", new JsonObject { ["grow"] = "heart" });
        var heart = Send(spend, Player);
        var vitals = _host.Vitals;
        Check(Ok(heart) && heart["transient"]?.GetValue<bool>() == true && heart["data"] == null && (heart["affected"]?.AsArray().Count ?? 0) == 0,
            "five Fractals grow a heart: a transient receipt with no data: " + Canonical(heart));
        Check(vitals.Fractals == new PlayerFractals(13, 8, 1, 0) && vitals.MaxHealth == 120 && vitals.Health == 70 && _host.BodyRules.Quarters(vitals) == 14 &&
              store.Saved.For("challenge").Fractals == vitals.Fractals && store.Saved.For("challenge").Health == 70,
            $"the new heart arrives full (10 quarters to 14 of 24), five leave the hand, and the vitals store keeps it: {vitals.Health}/{vitals.MaxHealth}");
        var replay = Send(spend, Player);
        Check(replay["replayed"]?.GetValue<bool>() == true && _host.Vitals.Fractals.Unspent == 8, "a retry replays and spends nothing twice");
        var band = _host.PlayerSpendFractals("band");
        vitals = _host.Vitals;
        var status = Send(Query("player.status", new JsonObject()), Player)["data"];
        Check(Ok(band) && vitals.Fractals == new PlayerFractals(13, 3, 1, 1) && Math.Abs(vitals.MaxStamina - 0.8f) < 1e-6 && Math.Abs(vitals.Stamina - 0.6f) < 1e-6 &&
              status?["bar"]?["bands"]?.GetValue<int>() == 2 && status["hearts"]?["max_quarters"]?.GetValue<int>() == 24 && status["hearts"]?["quarters"]?.GetValue<int>() == 14,
            $"five more open the next band, its 0.4 m joining the bar ({vitals.Stamina}/{vitals.MaxStamina} m), and player.status shows both: " + Canonical(status));
        var few = Spend("heart");
        Check(Code(few) == "not_ready" && Retryable(few) && Message(few) == "A new heart needs 5 Fractals. You have 3: find 2 more!" && _host.Vitals.Fractals.Unspent == 3,
            "with too few in hand: how many it takes and how many you have (not_ready, retryable): " + Message(few));
        var fewBand = Spend("band");
        Check(Message(fewBand) == "The next band of your stamina bar needs 5 Fractals. You have 3: find 2 more!", "and for a band: " + Message(fewBand));
        var lines = Send(Query("journal.read", new JsonObject { ["kind"] = "found" }), Player)["data"]?["entries"]?.AsArray().Select(e => e?["line"]?.GetValue<string>()).ToList();
        Check(lines?.Contains("You grew a new heart (6 hearts).") == true && lines.Contains("Your stamina bar grew a band (2 bands)."),
            "the journal keeps a pace line for each: " + string.Join(" | ", lines ?? new()));
        Check(store.Saved.For("creative").Fractals == PlayerFractals.None && store.Saved.For("live").Fractals == PlayerFractals.None, "every mode spends its own count: Creative's and Live's are untouched");
        await FreeWorld();
    }

    /// <summary>At the caps (the pack's cap_hearts, its last band) request_invalid at $.args.grow; under the typed flag a kind's own; a pack with no fractals block unsupported.</summary>
    private async Task TestSpendAtTheCaps()
    {
        await EnergyWorld("spend-caps", body: v => v.With(fractals: new PlayerFractals(180, 10, 25, 9)));
        var heart = Spend("heart");
        var band = Spend("band");
        Check(Code(heart) == "request_invalid" && FieldPath(heart) == "$.args.grow" && Message(heart) == "You have every heart this island allows (30)." &&
              Code(band) == "request_invalid" && FieldPath(band) == "$.args.grow" && Message(band) == "Your stamina bar has every band this island allows (10)." &&
              _host.Vitals.Fractals.Unspent == 10, $"at 30 hearts and the tenth band nothing more grows: {Message(heart)} {Message(band)}");
        var pack = JsonNode.Parse(Godot.FileAccess.GetFileAsString(IslandRules.PathFor("storybook_wild", 5)))!.AsObject();
        pack["fractals"]!["typed"] = true;
        await EnergyWorld("spend-typed", body: v => v.With(fractals: new PlayerFractals(7, 7, 0, 0, TypedHeart: 5, TypedBand: 2)));
        _host.UseRules(IslandRules.Parse(CanonicalJson.Bytes(pack)));
        var typedBand = Spend("band");
        var typedHeart = Spend("heart");
        Check(Message(typedBand) == "The next band of your stamina bar needs 5 band Fractals. You have 2: find 3 more!" && Ok(typedHeart) &&
              _host.Vitals.Fractals == new PlayerFractals(7, 2, 1, 0, TypedHeart: 0, TypedBand: 2),
            "under the typed flag a heart takes heart Fractals and a band band Fractals: " + Message(typedBand));
        _host.UseRules(IslandRules.Load("storybook_wild", 4));
        var none = Spend("band");
        Check(Code(none) == "unsupported_capability" && FieldPath(none) == "$.op", "a pack with no fractals block has none to spend: " + Message(none));
        await FreeWorld();
    }

    /// <summary>The grown body is the save's: relaunches in the game's order (vitals, rules, knacks) keep it, Big heart and all.</summary>
    private async Task TestSpendKeepsAcrossLaunches()
    {
        var rules = IslandRules.Load("storybook_wild", 5);
        var hearty = VitalsRules.For(rules, "challenge", "standard", BigHeart);
        SaveBody("grower", "challenge", _now, hearty.Harmed(hearty.Fresh(), 10).With(fractals: new PlayerFractals(10, 10, 0, 0)));
        await Launch("grower", BigHeart);
        var heart = _host.PlayerSpendFractals("heart");
        var band = _host.PlayerSpendFractals("band");
        Check(Ok(heart) && Ok(band) && _host.BodyRules.Quarters(_host.Vitals) == 14 && _host.Vitals.MaxHealth == 144 && _host.BodyRules.OpenBands(_host.Vitals) == 2,
            "with Big heart the new heart is four quarters of 6 (10 to 14 of 24) and the bar opens its second band: " + Quarters());
        await FreeWorld();
        for (var launch = 1; launch <= 2; launch++)
        {
            await Launch("grower", BigHeart);
            Check(_host.BodyRules.Quarters(_host.Vitals) == 14 && _host.Vitals.Health == 84 && _host.Vitals.MaxHealth == 144 && _host.BodyRules.OpenBands(_host.Vitals) == 2 &&
                  _host.Vitals.Fractals == new PlayerFractals(10, 0, 1, 1),
                $"relaunch {launch} keeps the grown body: {Quarters()}, {_host.BodyRules.OpenBands(_host.Vitals)} bands");
            await FreeWorld();
        }
    }
}
