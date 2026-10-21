using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Kernel;
using GarageGame.Native.Seams;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The mechanics round's energy and vitals (M1 of docs/runs/RUN-2-MECHANICS.md): the rules pack's six blocks read
/// fail-closed (storybook_wild v5, the contract's example and every invalid example), the body's and the pool's arithmetic
/// under the pack (hearts in quarters rounded at harm, Big heart's quarter, the cap and the knockout under every heart count,
/// fall damage absolute with a fall cap where the pack sets one, the bands and Big bar, the prices by principal, the refill's
/// pace by difficulty), the stamina ledger against the contract's fixture through the bound body, and the guard's budget.
/// The commands' side (the guard at commit, the shadow pool, the status, fire, healing, Fractals) is NatureTest.Energy.
/// </summary>
public partial class CommandHostTest
{
    private static string ContractsPath(string relative) => ProjectSettings.GlobalizePath("res://") + "../contracts/" + relative;
    private static string V5Text() => File.ReadAllText(ProjectSettings.GlobalizePath(IslandRules.PathFor("storybook_wild", 5)));

    /// <summary>v5 with one text replacement, as bytes.</summary>
    private static byte[] V5(string from, string to)
    {
        var text = V5Text();
        if (!text.Contains(from, StringComparison.Ordinal)) throw new InvalidOperationException("v5 no longer holds " + from);
        return Encoding.UTF8.GetBytes(text.Replace(from, to, StringComparison.Ordinal));
    }

    private void TestEnergyAndVitals()
    {
        TestMechanicsLoader();
        TestVitalsArithmetic();
        TestStaminaLedgerFixture();
        TestGuardBudget();
        TestModeRows();
        _host.UseRules(null, "The energy checks are over.");
        _host.ConfigurePlayerVitals(new ModeTestStore());
    }

    private void TestMechanicsLoader()
    {
        var v5 = IslandRules.Load("storybook_wild", 5);
        var traits = v5.Traits!;
        Check(traits.Hearts == new HeartsRules(5, 5, 30, 2.5, 10) && traits.Harm == new HarmRules(1, null) &&
              traits.Stamina == new StaminaRules(0.4, 0.4, 10, 5, 0.02, 0.1, 0.02, 0.04, 1, 1.5, 0) &&
              traits.Pips == new PipsRules(8, 5, 40, 5, 2, 15, 0.5) && traits.Wallet == new WalletRules(0, 0, 0, 0),
            "v5's traits: 5 hearts of 5 health a quarter to 30, healing a quarter every 2.5 s after 10 s, fire a quarter a second and no fall cap, the bar 0.4 m in ten bands of 0.4 m, running and deep water 0.02 m/s, diving 0.04 m/s, the refill 0.1 m/s, no running from empty until the first bar is full, a metre of bar buying 1.5 m of climb and hanging still free (decisions 64 and 65), 8 pips to 40, an empty wallet (decision 26)");
        var prices = v5.Cost!.Prices;
        Check(prices.Count == 11 && prices["glow"] is { Ordered: 2, OwnIdea: 1 } && prices["grow"] is { Ordered: 4, OwnIdea: 2 } && prices["fire"] is { Ordered: 6, OwnIdea: 3 } &&
              prices["bubble_carry"] is { Ordered: 4, OwnIdea: 2 } && prices["bubble_boat"] is { Ordered: 4, OwnIdea: 2 } && prices["bubble_lift"] is { Ordered: 6, OwnIdea: 3 } &&
              prices["fireline"] is { Ordered: 8, OwnIdea: 4 } && prices["snuff"] is { Ordered: 2, OwnIdea: 1 } && prices["will_o_wisp"] is { Ordered: 4, OwnIdea: 2 } && v5.Cost.TorchFire == 0,
            "v5's prices: Glow, Bubbles and Fireworks 2 and 1, Grow 4 and 2, Fire 6 and 3, the carry and the boat 4 and 2, the lift 6 and 3, Fire's kinds (decisions 78 to 80) Fireline 8 and 4, Snuff 2 and 1, Will-o'-wisp 4 and 2, a torch's fire free");
        var modes = v5.Modes!;
        Check(modes.Rows.Count == 6 && modes.Row("creative", null) is { ShadowPool: true, DamageMultiplier: 0, Undo: true } &&
              modes.Row("challenge", "gentle") is { DamageMultiplier: 0.5, RefillMultiplier: 1.5 } && modes.Row("challenge", "brave") is { DamageMultiplier: 1.5, RefillMultiplier: 0.75 } &&
              modes.Rows.Where(r => r.Mode == "live").All(r => !r.Available && r.TorchSelfIgnite),
            "v5's modes: Creative a shadow pool with undo and no harm; Gentle halves harm and refills 1.5 times; Brave 1.5 and 0.75; Live present and not available");
        Check(v5.Names!.Words["expertise"] == "Expertise" && v5.Names.Knacks["big_heart"] == "Big heart" && v5.Knacks!.Table.Count == 11 && v5.Knacks.Slots.Count == 3 &&
              v5.Knacks.Knack("cat_feet") is { Unit: "m", Ranks: [0.45, 0.6, 0.75] } && v5.Knacks.Knack("handy") is { Available: false } &&
              v5.Fractals == new FractalsRules(25, 30, 15, 5, 5, false),
            "v5's names, knacks (Cat feet in metres, Handy not available yet) and Fractals (25 to 30, fifteen within first-run reach, five a heart and a band, untyped)");
        var v4 = IslandRules.Load("storybook_wild", 4);
        Check(v4.Traits == null && v4.Cost == null && v4.Modes == null && v4.Names == null && v4.Knacks == null && v4.Fractals == null, "v4 has none of the blocks: today's behaviour");
        var example = IslandRules.Parse(File.ReadAllBytes(ContractsPath("examples/rules/mechanics_example.json")));
        Check(example.Traits?.Hearts != null && example.Cost != null && example.Modes != null, "the contract's mechanics example loads in the kernel");
        // Every invalid pack the contract keeps, refused by the kernel as validate.py refuses it.
        foreach (var file in Directory.GetFiles(ContractsPath("examples/rules_invalid"), "*.json").OrderBy(f => f, StringComparer.Ordinal))
            Check(Refused(File.ReadAllBytes(file)), $"the kernel refuses the contract's invalid pack {Path.GetFileNameWithoutExtension(file)}");
        var cases = new (string Label, byte[] Pack)[]
        {
            ("an unknown field in a block", V5("\"heal_after_unharmed_s\": 10 }", "\"heal_after_unharmed_s\": 10, \"regen\": 3 }")),
            ("an unknown block", V5("\"extensions\": {}", "\"extensions\": {}, \"economy\": {}")),
            ("a missing required part", V5("\"running_m_per_s\": 0.02, ", "")),
            ("a fractional heart count", V5("\"start_hearts\": 5", "\"start_hearts\": 5.5")),
            ("a quarter past 6 health", V5("\"health_per_quarter\": 5", "\"health_per_quarter\": 7")),
            ("30 hearts of 6 with Big heart past 720", V5("\"health_per_quarter\": 5", "\"health_per_quarter\": 6")),
            ("a price of an ability the pack lacks", V5("\"glow\": { \"ordered\": 2", "\"teleport\": { \"ordered\": 2")),
            ("a knack that moves an unknown number", V5("\"moves\": \"hop_height\"", "\"moves\": \"reach\"")),
            ("a name with a hidden character", V5("\"hearts\": \"Hearts\"", "\"hearts\": \"Hea\\u200brts\"")),
            ("an empty names block", V5("\"names\": {", "\"names\": {}, \"x\": {")),
            ("Live offered", V5("{ \"mode\": \"live\", \"variant\": \"cooperative\", \"available\": false", "{ \"mode\": \"live\", \"variant\": \"cooperative\", \"available\": true")),
            ("a fall cap of no quarters", V5("\"fire_quarters_per_s\": 1 }", "\"fire_quarters_per_s\": 1, \"fall_cap_quarters\": 0 }")),
            ("slower refills near than apart", V5("\"refill_near_every_s\": 5", "\"refill_near_every_s\": 20")),
        };
        foreach (var (label, pack) in cases) Check(Refused(pack), "the kernel refuses a pack with " + label);
        Check(!Refused(V5("\"fire_quarters_per_s\": 1 }", "\"fire_quarters_per_s\": 1, \"fall_cap_quarters\": 16 }")) &&
              !Refused(V5("\"deep_water_m_per_s\": 0.02, \"dive_m_per_s\": 0.04,", "")),
            "and loads the optional numbers: a fall cap, and a pack without the deep-water and dive drains");
    }

    private void TestVitalsArithmetic()
    {
        var v5 = IslandRules.Load("storybook_wild", 5);
        var standard = VitalsRules.For(v5, "challenge");
        var fresh = standard.Fresh();
        Check(fresh is { Health: 100, MaxHealth: 100, Stamina: 0.4f, MaxStamina: 0.4f, Pips: 8 } && standard.Quarters(fresh) == 20 && standard.MaxQuarters(fresh) == 20 &&
              standard.OpenBands(fresh) == 1 && standard.MaxPips == 8 && standard.CampPips == 4,
            "a new body under v5: five hearts of 20 quarters, the first band of 0.4 m, a full pool of 8 (4 at camp)");
        Check(VitalsRules.For(v5, "challenge", expertise: 5).MaxPips == 9 && VitalsRules.For(v5, "challenge", expertise: 20).MaxPips == 12 &&
              VitalsRules.For(v5, "challenge", expertise: 1000).MaxPips == 40, "the pool's maximum: one pip per five expertise (Glow's five reach 9), never past the ceiling of 40");
        // Big heart: a quarter of 5.5, then 6; the maximum is a field of the body.
        var big1 = VitalsRules.For(v5, "challenge", knacks: new Dictionary<string, int> { ["big_heart"] = 1 });
        var big2 = VitalsRules.For(v5, "challenge", knacks: new Dictionary<string, int> { ["big_heart"] = 2, ["handy"] = 3 });
        Check(Math.Abs(big1.HealthPerQuarter - 5.5) < 1e-9 && big2.HealthPerQuarter == 6 && big2.Fresh().MaxHealth == 120 && big2.Quarters(big2.Fresh()) == 20,
            "Big heart makes a quarter 5.5 and then 6 health (120 health in five hearts); a knack not available yet moves nothing");
        // The cap and the knockout under every heart count, at every Big heart rank.
        var everyCount = true;
        var worst = "";
        foreach (var rules in new[] { standard, big1, big2 })
            for (var bought = 0; bought <= 29; bought++)
            {
                var body = rules.Normalize(PlayerVitals.Of(1, 1, 0.4f, 0.4f, Array.Empty<string>(), 0, new PlayerFractals(bought * 5, 0, bought, 0), 8));
                body = body.With(health: body.MaxHealth);
                var hearts = Math.Min(30, 5 + bought);
                var quarters = rules.MaxQuarters(body);
                var top = rules.HarmQuarters(PlayerVitals.FallDamage(float.MaxValue, false), fall: true);
                var oneQuarter = rules.Harmed(body, 1);
                var healed = rules.HealedQuarter(rules.HealedQuarter(oneQuarter));
                var ok = rules.MaxHearts(body) == hearts && quarters == 4 * hearts && Math.Abs(body.MaxHealth - quarters * rules.HealthPerQuarter) < 1e-3 &&
                    body.MaxHealth <= PlayerVitals.ContractMaxHealth && top >= quarters && rules.Harmed(body, top).Health == 0 && rules.Harmed(body, 1000).Health == 0 &&
                    rules.Quarters(oneQuarter) == quarters - 1 && healed.Health == body.MaxHealth && Math.Abs(rules.Recovered(rules.Harmed(body, top)).Health - quarters / 2 * rules.HealthPerQuarter) < 1e-3;
                if (!ok && everyCount) worst = $"{hearts} hearts at {rules.HealthPerQuarter} a quarter";
                everyCount &= ok;
            }
        Check(everyCount, "at every heart count from 5 to 30 (and the cap holds at 30 with 29 bought) and every Big heart rank: the maximum is hearts x 4 x the quarter within 720, the hardest fall knocks out, harm stops at empty, one quarter is one quarter, healing stops at full and waking at camp gives half the hearts " + worst);
        // Rounded at the moment of harm, multiplied by the difficulty; falls absolute, so a bigger quarter takes fewer.
        var gentle = VitalsRules.For(v5, "challenge", "gentle");
        var brave = VitalsRules.For(v5, "challenge", "brave");
        var fall = PlayerVitals.FallDamage(MathF.Sqrt(2 * 3.5f * 1.5f), false);
        Check(standard.HarmQuarters(7, false) == 1 && standard.HarmQuarters(7.5f, false) == 2 && standard.HarmQuarters(2, false) == 0 &&
              gentle.HarmQuarters(10, false) == 1 && brave.HarmQuarters(10, false) == 3 && gentle.HarmQuarters(5, false) == 1,
            "harm rounds to the nearest quarter when it lands (7 health one quarter, 7.5 two) (2 health is none); Gentle halves it and removes none Standard counts, Brave takes half again");
        Check(Math.Abs(fall - 96) < 0.2f && standard.HarmQuarters(fall, true) == 19 && big2.HarmQuarters(fall, true) == 16 && standard.FallCapQuarters == null,
            $"a 1.5 m fall is the same {fall:0.0} health for every body: 19 quarters at 5 a quarter, 16 at Big heart's 6; v5 sets no fall cap, so none applies");
        var capped = IslandRules.Parse(V5("{ \"mode\": \"challenge\", \"difficulty\": \"gentle\", \"available\": true, \"damage_multiplier\": 0.5,",
            "{ \"mode\": \"challenge\", \"difficulty\": \"gentle\", \"available\": true, \"fall_cap_quarters\": 16, \"damage_multiplier\": 0.5,"));
        var harmCap = IslandRules.Parse(V5("\"fire_quarters_per_s\": 1 }", "\"fire_quarters_per_s\": 1, \"fall_cap_quarters\": 12 }"));
        Check(VitalsRules.For(capped, "challenge", "gentle").HarmQuarters(500, true) == 16 && VitalsRules.For(capped, "challenge", "gentle").HarmQuarters(500, false) == 50 &&
              VitalsRules.For(capped, "challenge").HarmQuarters(500, true) == 100 && VitalsRules.For(harmCap, "challenge", "brave").HarmQuarters(500, true) == 12,
            "a fall cap where the pack gives one: the mode's row over traits.harm's, for falls only, after the difficulty");
        Check(VitalsRules.For(v5, "live").DamageMultiplier == 1 && VitalsRules.For(v5, "live").Consequences && VitalsRules.For(v5, "creative") is { DamageMultiplier: 0, ShadowPool: true, Consequences: false },
            "Live plays as Challenge Standard until it exists; Creative hurts nothing and keeps a shadow pool");
        // The bar in bands, Big bar on each bar; the per-climb reward retired.
        var bands = Enumerable.Range(0, 10).Select(b => standard.MaxStaminaOf(PlayerVitals.Of(100, 100, 0, 0.4f, Array.Empty<string>(), 0, new PlayerFractals(b * 5, 0, 0, b), 8))).ToArray();
        var bigBar = VitalsRules.For(v5, "challenge", knacks: new Dictionary<string, int> { ["big_bar"] = 3 });
        var topBand = PlayerVitals.Of(100, 100, 0, 0.4f, Array.Empty<string>(), 0, new PlayerFractals(45, 0, 0, 9), 8);
        Check(bands.Zip(Enumerable.Range(1, 10)).All(p => Math.Abs(p.First - 0.4f * p.Second) < 1e-5) && Math.Abs(bigBar.MaxStaminaOf(topBand) - 4.8f) < 1e-5,
            "ten bands of 0.4 m, 0.4 to 4.0 m; Big bar at full rank makes the top band 4.8 m, the contract's bound");
        var climbed = standard.CompleteClimb(fresh, new string('c', 64));
        Check(climbed.CompletedClimbs == 1 && climbed.MaxStamina == fresh.MaxStamina && PlayerVitals.Fresh.CompleteClimb(new string('c', 64)).MaxStamina > PlayerVitals.Fresh.MaxStamina,
            "in bands a completed climb only counts (decision L); without the stamina block it still raises the bar 0.10 m, today's reward");
        var steady = VitalsRules.For(v5, "challenge", knacks: new Dictionary<string, int> { ["steady_climber"] = 3 });
        var farther = steady.SpendClimb(fresh, 1, 0.24f, out var reached);
        var plain = standard.SpendClimb(fresh, 1, 0.24f, out _);
        Check(reached && Math.Abs(farther.Stamina - (0.4f - 0.24f / 1.8f)) < 1e-5 && Math.Abs(plain.Stamina - 0.24f) < 1e-5,
            $"Steady climber climbs farther for the same stamina: under v5 0.24 m costs {0.4f - farther.Stamina:0.0000} m at full rank (0.24 / 1.5 / 1.2), {0.4f - plain.Stamina:0.0000} m without (0.24 / 1.5)");
        TestClimbPrices(v5, standard, steady);
        // Prices by principal: the ordered price for the player, half for the Gubble's own idea, ordered again while a wish runs.
        Check(standard.Price("glow", false, false) == 2 && standard.Price("glow", true, false) == 1 && standard.Price("glow", true, true) == 2 &&
              standard.Price("grow", false, false) == 4 && standard.Price("grow", true, false) == 2 && standard.Price("fire", true, false) == 3 &&
              standard.Price("bubble_boat", false, false) == 4 && standard.Price("bubble_boat", true, false) == 2 &&
              standard.Price("teleport", true, false) == 0 && VitalsRules.For(IslandRules.Load("storybook_wild", 4), "challenge").Price("fire", false, false) == 0,
            "the price follows the principal alone: Glow 2 or 1, Grow 4 or 2, Fire 6 or 3, the boat 4 or 2; a wish running makes the Gubble's ordered; unpriced and v4 abilities cost nothing");
        Check(standard.RefillEveryS(1) == 5 && standard.RefillEveryS(2) == 5 && standard.RefillEveryS(2.5f) == 15 && Math.Abs(gentle.RefillEveryS(1) - 5 / 1.5) < 1e-9 &&
              Math.Abs(brave.RefillEveryS(3) - 20) < 1e-9 && VitalsRules.For(v5, "creative").RefillEveryS(1) == 5,
            "a pip every 5 s within 2 m, every 15 s apart; Gentle 1.5 times as fast, Brave 0.75; Creative's shadow pool at the Standard pace");
        // Today's rules stay as they were without the blocks.
        var legacy = VitalsRules.For(null, "challenge");
        Check(legacy.Fresh() is { Health: 100, MaxHealth: 100, Stamina: 0.45f } && !legacy.InQuarters && !legacy.InBands && !legacy.HasPool && legacy.Price("glow", false, false) == 0 &&
              legacy.Drain(new BodyExertion(10, 0, 0)) == 0 && legacy.Gate(fresh, true) == StaminaGate.Free,
            "without the blocks: today's 100 health and 0.45 m, no pool, nothing charged, running free and nothing gated");
    }

    /// <summary>A climber's bar spent the way the body spends it: a tick at a time at the climb's speed, until the grip would fail. The rise it bought.</summary>
    private static float ClimbUntilEmpty(VitalsRules rules, PlayerVitals body, float climbSpeedMps = 0.12f)
    {
        const float tick = 1f / 60;
        var risen = 0f;
        for (var i = 0; i < 100_000; i++)
        {
            body = rules.SpendClimb(body, tick, climbSpeedMps * tick, out var enough);
            if (!enough) break;
            risen += climbSpeedMps * tick;
        }
        return risen;
    }

    /// <summary>A climber hanging still for a while, a tick at a time: what is left of its bar.</summary>
    private static float HangFor(VitalsRules rules, PlayerVitals body, float seconds)
    {
        const float tick = 1f / 60;
        for (var i = (int)Math.Round(seconds * 60); i > 0; i--) body = rules.SpendClimb(body, tick, 0, out _);
        return body.Stamina;
    }

    /// <summary>
    /// The climb's two pack values (decisions 64 and 65; P1 of the first recorded play's fix round): under v5 a metre of bar buys 1.5 m
    /// of climb and hanging still costs nothing, Steady climber multiplying the climb; a pack without the values, v4 and the legacy
    /// rules charge as before them (a metre of bar a metre risen, 0.01 m a second hanging).
    /// </summary>
    private void TestClimbPrices(IslandRules v5, VitalsRules standard, VitalsRules steady)
    {
        var fresh = standard.Fresh();
        Check(standard.ClimbMPerBarM == 1.5 && standard.HoldingMPerS == 0 && steady.ClimbMPerBarM == 1.5 && Math.Abs(steady.ClimbFactor - 1.2) < 1e-9,
            "v5's climb: 1.5 m for each metre of bar, hanging still free; Steady climber at full rank 1.2 on top");
        var bought = ClimbUntilEmpty(standard, fresh);
        var whole = standard.SpendClimb(fresh, 5, 0.6f, out var wholeEnough);
        var over = standard.SpendClimb(fresh, 5, 0.61f, out var overEnough);
        Check(Math.Abs(bought - 0.6f) < 0.003f && wholeEnough && whole.Stamina < 1e-5 && !overEnough && over.Stamina == 0,
            $"under v5 a full 0.4 m starting bar buys 0.6 m of rise ({bought:0.000} m climbed a tick at a time at 0.12 m/s; 0.6 m at once leaves {whole.Stamina:0.00000} m, 0.61 m cannot be paid)");
        var hung = HangFor(standard, fresh, 10);
        var hungOnce = standard.SpendClimb(fresh, 10, 0, out var hungEnough);
        var tired = standard.SpendClimb(fresh.With(stamina: 0.05f), 1, 0.03f, out _);
        Check(hung == fresh.Stamina && hungEnough && hungOnce.Stamina == fresh.Stamina && HangFor(standard, tired, 10) == tired.Stamina,
            $"under v5 hanging still for ten seconds costs nothing, from a full bar ({hung:0.000} m left) or a nearly empty one ({tired.Stamina:0.000} m kept)");
        var steadyBought = ClimbUntilEmpty(steady, steady.Fresh());
        Check(Math.Abs(steadyBought - 0.72f) < 0.003f && HangFor(steady, steady.Fresh(), 10) == steady.Fresh().Stamina,
            $"with Steady climber at full rank the same bar buys 0.72 m ({steadyBought:0.000} m: 0.4 x 1.5 x 1.2), and hanging is still free");
        var empty = standard.SpendClimb(fresh.With(stamina: 0), 1, 0, out var emptyEnough);
        Check(!emptyEnough && empty.Stamina == 0, "an empty bar still cannot hold on, even where hanging costs nothing: the grip fails at empty");
        // Today's numbers wherever the pack says nothing of them: v4, a pack with the stamina block but without the two values, and the legacy rules.
        var v4 = VitalsRules.For(IslandRules.Load("storybook_wild", 4), "challenge");
        var v4Fresh = v4.Fresh();
        var v4Bought = ClimbUntilEmpty(v4, v4Fresh);
        var v4Hung = HangFor(v4, v4Fresh, 10);
        Check(v4.ClimbMPerBarM == 1 && Math.Abs(v4.HoldingMPerS - 0.01) < 1e-6 && Math.Abs(v4Bought - 0.45f) < 0.003f && Math.Abs(v4Hung - (v4Fresh.Stamina - 0.1f)) < 1e-4,
            $"under v4 today's numbers: the 0.45 m bar buys {v4Bought:0.000} m of rise and ten seconds hanging cost {v4Fresh.Stamina - v4Hung:0.000} m");
        var silent = VitalsRules.For(IslandRules.Parse(V5("\"climb_m_per_bar_m\": 1.5, \"holding_m_per_s\": 0,", "")), "challenge");
        var silentBought = ClimbUntilEmpty(silent, silent.Fresh());
        var silentHung = HangFor(silent, silent.Fresh(), 10);
        Check(silent.InBands && silent.ClimbMPerBarM == 1 && Math.Abs(silentBought - 0.4f) < 0.003f && Math.Abs(silentHung - 0.3f) < 1e-4,
            $"a pack with the stamina block but neither value charges as before them: 0.4 m of bar buys {silentBought:0.000} m, ten seconds hanging leave {silentHung:0.000} m");
        var legacy = VitalsRules.Legacy;
        var same = true;
        foreach (var (seconds, rise) in new[] { (0.0166667f, 0.002f), (1f, 0f), (3f, 0.35f), (5f, 0.6f), (0.5f, 0.001f), (10f, 0.05f) })
        {
            var now = legacy.SpendClimb(PlayerVitals.Fresh, seconds, rise, out var nowEnough);
            var before = Math.Max(0, PlayerVitals.Fresh.Stamina - (float)Math.Max(rise, PlayerVitals.HoldingCostPerSecond * seconds));
            same &= now.Stamina == before && nowEnough == (PlayerVitals.Fresh.Stamina + 0.000001f >= (float)Math.Max(rise, PlayerVitals.HoldingCostPerSecond * seconds));
        }
        Check(same && legacy.ClimbMPerBarM == 1 && legacy.HoldingMPerS == PlayerVitals.HoldingCostPerSecond,
            "VitalsRules.Legacy is untouched: the same float as before the pack's values for every climb tried");
    }

    /// <summary>
    /// The stamina ledger's fixture (contracts/examples/ledger/stamina_ledger.json): each case through the bound body's ledger
    /// (Report, then VitalsTick, then Gate) under a pack with the fixture's numbers (its water rates are its own, never v5's).
    /// </summary>
    private void TestStaminaLedgerFixture()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(ContractsPath("examples/ledger/stamina_ledger.json")));
        var root = fixture.RootElement;
        var stamina = root.GetProperty("stamina");
        var tolerance = root.GetProperty("tolerance_m").GetDouble();
        var ledger = _player.StaminaLedger;
        Check(ledger is KernelStaminaLedger, "the host binds its stamina ledger to the body (BindPlayerVitals)");
        var passed = 0;
        var cases = root.GetProperty("cases").EnumerateArray().ToList();
        foreach (var item in cases)
        {
            var omit = item.TryGetProperty("omit", out var o) ? o.EnumerateArray().Select(x => x.GetString()!).ToHashSet() : new HashSet<string>();
            var block = new JsonObject();
            foreach (var p in stamina.EnumerateObject()) if (!omit.Contains(p.Name)) block[p.Name] = JsonNode.Parse(p.Value.GetRawText());
            var pack = JsonNode.Parse(V5Text())!.AsObject();
            pack["traits"]!["stamina"] = block;
            _host.UseRules(IslandRules.Parse(Encoding.UTF8.GetBytes(pack.ToJsonString())));
            var max = (float)item.GetProperty("max_stamina_m").GetDouble();
            var band = (int)Math.Round((max - stamina.GetProperty("start_m").GetDouble()) / stamina.GetProperty("band_m").GetDouble());
            var body = PlayerVitals.Of(100, 100, (float)item.GetProperty("stamina_m").GetDouble(), max, Array.Empty<string>(), 0, new PlayerFractals(band * 5, 0, 0, band), 8);
            _host.ConfigurePlayerVitals(new ModeTestStore(PlayerModesProfile.Fresh.Select("challenge").WithCurrent(body)));
            var ok = _host.StaminaSlowed == item.GetProperty("no_run").GetBoolean() && Math.Abs(_host.Vitals.MaxStamina - max) < 1e-5 &&
                Math.Abs(_host.BodyRules.FirstBarOf(_host.Vitals) - item.GetProperty("first_bar_m").GetDouble()) < 1e-5;
            var where = "";
            foreach (var step in item.GetProperty("steps").EnumerateArray())
            {
                ledger!.Report(new BodyExertion((float)step.GetProperty("sprint_s").GetDouble(), (float)step.GetProperty("deep_water_s").GetDouble(), (float)step.GetProperty("dive_s").GetDouble()));
                _player.VitalsTick!((float)step.GetProperty("seconds").GetDouble(), step.GetProperty("safely_grounded").GetBoolean());
                var expect = step.GetProperty("expect");
                var gate = ledger.Gate;
                var fine = Math.Abs(_host.Vitals.Stamina - expect.GetProperty("stamina_m").GetDouble()) <= tolerance + 1e-6 &&
                    gate.Empty == expect.GetProperty("empty").GetBoolean() && gate.NoRun == expect.GetProperty("no_run").GetBoolean();
                if (!fine && where.Length == 0) where = $" (stamina {_host.Vitals.Stamina:0.0000}, empty {gate.Empty}, no run {gate.NoRun})";
                ok &= fine;
            }
            Check(ok, "the ledger's fixture: " + item.GetProperty("name").GetString() + where);
            if (ok) passed++;
        }
        Check(passed == cases.Count && cases.Count == 10, $"every one of the fixture's {cases.Count} cases holds on the kernel's side of the seam");
        // Creative never drains and never gates; nor does a pack without the stamina block.
        _host.UseRules(IslandRules.Load("storybook_wild", 5));
        _host.ConfigurePlayerVitals(new ModeTestStore());
        ledger!.Report(new BodyExertion(30, 0, 0));
        _player.VitalsTick!(30, true);
        Check(ledger.Gate == StaminaGate.Free && _host.Vitals.Stamina == _host.Vitals.MaxStamina, "in Creative running drains nothing and nothing is gated");
        _host.UseRules(IslandRules.Load("storybook_wild", 4));
        _host.ConfigurePlayerVitals(new ModeTestStore(PlayerModesProfile.Fresh.Select("challenge")));
        ledger.Report(new BodyExertion(30, 0, 0));
        _player.VitalsTick!(30, false);
        Check(ledger.Gate == StaminaGate.Free && _host.Vitals.Stamina == PlayerVitals.InitialStamina, "under a pack without the stamina block running is free, today's play");
    }

    /// <summary>The guard's budget (RUN-2-MECHANICS section 9): under 0.05 ms per command at commit and no allocation on its way through.</summary>
    private void TestGuardBudget()
    {
        _host.UseRules(IslandRules.Load("storybook_wild", 5));
        _host.ConfigurePlayerVitals(new ModeTestStore(PlayerModesProfile.Fresh.Select("challenge")));
        for (var i = 0; i < 1000; i++) { _host.GuardEnergy("glow", Companion); _host.GuardEnergy("fire", Player); }
        const int calls = 20000;
        var total = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < calls; i++) total += _host.GuardEnergy("glow", Companion) + _host.GuardEnergy("grow", Player);
        var ended = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var perCallMs = (ended - started) * 1000.0 / Stopwatch.Frequency / (2.0 * calls);
        Check(total == calls * 5 && allocated == 0 && perCallMs < 0.05,
            $"the guard prices and checks a command in {perCallMs * 1000:0.000} µs (budget 50 µs) and allocates nothing ({allocated} bytes over {2 * calls} calls)");
    }
}
