using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GarageGame.Native;
using GarageGame.Native.Characters;
using GarageGame.Native.Kernel;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The modes block in play (M10 of docs/runs/RUN-2-MECHANICS.md; docs/game/MECHANICS.md section 8). What each row of the pack
/// does to the rules a body plays by (damage, refill, consequences, the shadow pool, the Live row's torch), that the numbers
/// come from the pack and from no constant, that no hurt is ever removed (a hurt costs at least one quarter wherever damage
/// counts, so Gentle halves every hurt and removes none), that the tumble's look never reads the difficulty, the helper the
/// menu asks whether a mode and difficulty are on offer, and the names block served to the screen.
/// </summary>
public partial class CommandHostTest
{
    private void TestModeRows()
    {
        TestModeRowsPlayedByThePack();
        TestNoHurtIsRemoved();
        TestTumbleLookIsStandards();
        TestTorchSelfIgnite();
        TestModeAvailability();
        TestNamesServed();
    }

    // ---- every row, as the rules a body plays by ----

    private void TestModeRowsPlayedByThePack()
    {
        var v5 = IslandRules.Load("storybook_wild", 5);
        var standardRow = v5.Modes!.Row("challenge", "standard")!;
        foreach (var row in v5.Modes.Rows)
        {
            var label = $"{row.Mode}{(row.Difficulty != null ? " " + row.Difficulty : "")}{(row.Variant != null ? " " + row.Variant : "")}";
            // Live is refused at the menu and the boot; if a host is ever in it, it plays as Challenge Standard (VitalsRules.Row).
            var rules = row.Mode == "live" ? VitalsRules.For(v5, "live") : VitalsRules.For(v5, row.Mode, row.Difficulty ?? "standard");
            var played = row.Mode == "live" ? standardRow : row;
            Check(ReferenceEquals(rules.Row, played) && rules.DamageMultiplier == played.DamageMultiplier && rules.RefillMultiplier == played.RefillMultiplier &&
                  rules.Consequences == played.Consequences && rules.ShadowPool == played.ShadowPool && rules.Undo == played.Undo && (rules.Undo == (rules.Mode == "creative")),
                $"the {label} row's damage ({played.DamageMultiplier}), refill ({played.RefillMultiplier}), consequences ({played.Consequences}), undo ({played.Undo}) and shadow pool ({played.ShadowPool}) are the rules a body plays by, and undo is Creative's alone");
        }
        Check(VitalsRules.For(v5, "creative").HarmQuarters(500, true) == 0 && VitalsRules.For(v5, "creative").HarmQuarters(0.1f, false) == 0 && VitalsRules.For(v5, "creative").DamageMultiplier == 0,
            "Creative's damage stays 0: no hurt of any size costs a quarter there");

        // The numbers are the pack's: change them in a copy and the rules follow, with no constant behind them.
        var changed = IslandRules.Parse(V5Edits(
            ("\"difficulty\": \"gentle\", \"available\": true, \"damage_multiplier\": 0.5, \"refill_multiplier\": 1.5", "\"difficulty\": \"gentle\", \"available\": true, \"damage_multiplier\": 0.25, \"refill_multiplier\": 3"),
            ("\"difficulty\": \"brave\", \"available\": true, \"damage_multiplier\": 1.5, \"refill_multiplier\": 0.75", "\"difficulty\": \"brave\", \"available\": true, \"damage_multiplier\": 2, \"refill_multiplier\": 0.5")));
        var gentle = VitalsRules.For(changed, "challenge", "gentle");
        var standard = VitalsRules.For(changed, "challenge", "standard");
        var brave = VitalsRules.For(changed, "challenge", "brave");
        Check(gentle.DamageMultiplier == 0.25 && brave.DamageMultiplier == 2 && standard.DamageMultiplier == 1 &&
              gentle.HarmQuarters(40, false) == 2 && standard.HarmQuarters(40, false) == 8 && brave.HarmQuarters(40, false) == 16 &&
              Math.Abs(gentle.RefillEveryS(1) - 5 / 3.0) < 1e-9 && Math.Abs(brave.RefillEveryS(3) - 30) < 1e-9 && standard.RefillEveryS(3) == 15,
            "a pack that says Gentle 0.25 and 3 and Brave 2 and 0.5 plays so: 40 health is 2, 8 and 16 quarters, a pip every 5/3 s near at Gentle and every 30 s far at Brave");
        // A pack that leaves a difficulty out plays it as Standard (and offers it nowhere: TestModeAvailability).
        var gentleOnly = IslandRules.Parse(V5("{ \"mode\": \"challenge\", \"difficulty\": \"brave\", \"available\": true, \"damage_multiplier\": 1.5, \"refill_multiplier\": 0.75, \"consequences\": true, \"undo\": false, \"shadow_pool\": false, \"torch_self_ignite\": false },", ""));
        Check(VitalsRules.For(gentleOnly, "challenge", "brave").DamageMultiplier == 1 && gentleOnly.Modes!.Row("challenge", "brave") == null,
            "a pack with no Brave row plays Brave as Standard rather than inventing a number");
    }

    /// <summary>v5 with several text replacements, as bytes.</summary>
    private static byte[] V5Edits(params (string From, string To)[] edits)
    {
        var text = V5Text();
        foreach (var (from, to) in edits)
        {
            if (!text.Contains(from, StringComparison.Ordinal)) throw new InvalidOperationException("v5 no longer holds " + from);
            text = text.Replace(from, to, StringComparison.Ordinal);
        }
        return Encoding.UTF8.GetBytes(text);
    }

    // ---- no hurt removed ----

    private void TestNoHurtIsRemoved()
    {
        var v5 = IslandRules.Load("storybook_wild", 5);
        var bigHeart = new Dictionary<string, int> { ["big_heart"] = 2 };
        var worst = "";
        var always = true;
        foreach (var difficulty in new[] { "gentle", "standard", "brave" })
            foreach (var knacks in new Dictionary<string, int>?[] { null, bigHeart })
            {
                var rules = VitalsRules.For(v5, "challenge", difficulty, knacks);
                var before = 0;
                for (var hurt = 0.01f; hurt <= 40f; hurt += 0.37f)
                {
                    var quarters = rules.HarmQuarters(hurt, false);
                    var counted = VitalsRules.For(v5, "challenge", "standard", knacks).HarmQuarters(hurt, false) >= 1;
                    var ok = (!counted || quarters >= VitalsRules.MinimumHurtQuarters) && quarters >= before && rules.HarmQuarters(hurt, true) == quarters;
                    if (!ok && always) worst = $"({difficulty}{(knacks == null ? "" : " with Big heart")}: {hurt:0.00} health took {quarters})";
                    always &= ok;
                    before = quarters;
                }
            }
        Check(always, "in Challenge at every difficulty (and with Big heart's larger quarter) every hurt Standard counts takes at least one quarter: the rounding never makes one free " + worst);
        var gentle = VitalsRules.For(v5, "challenge", "gentle");
        var standard = VitalsRules.For(v5, "challenge");
        var brave = VitalsRules.For(v5, "challenge", "brave");
        Check(gentle.HarmQuarters(4, false) == 1 && gentle.HarmQuarters(2.6f, true) == 1 && gentle.HarmQuarters(2.4f, true) == 0 && standard.HarmQuarters(2, false) == 0 && brave.HarmQuarters(2, true) == 1 &&
              gentle.HarmQuarters(10, false) == 1 && gentle.HarmQuarters(40, false) == 4 && standard.HarmQuarters(40, false) == 8 && brave.HarmQuarters(40, false) == 12,
            "a hurt of 4 health, which Standard counts as a quarter, is a quarter at Gentle too (half of 4 is 2, under half a quarter, and was removed); a scrape Standard rounds to none (2 health) stays none at Standard and Gentle; larger hurts are still halved and a half again: 40 health is 4, 8 and 12 quarters");

        // Through the host: the trusted hook hurts Gentle by the quarter and Creative not at all.
        _host.UseRules(v5);
        _host.UseDifficulty("gentle");
        _host.ConfigurePlayerVitals(new ModeTestStore());
        Check(Ok(_host.PlayerMode("challenge")) && _host.Difficulty == "gentle" && _host.Vitals.Health == 100, "the host plays a Gentle Challenge body, full hearts");
        var small = _host.ApplyPlayerDamage(4f, PlayerDamageSource.Fall);
        Check(small is { Before: 100, After: 95, Recovered: false } && _host.Vitals.Health == 95,
            $"a fall of 4 health (a quarter at Standard) at Gentle takes one quarter, not nothing ({_host.Vitals.Health} left)");
        Check(_host.ApplyPlayerDamage(1.5f, PlayerDamageSource.Fall) == null && _host.Vitals.Health == 95,
            $"and a scrape of 1.5 health, none at Standard, is none at Gentle ({_host.Vitals.Health} left)");
        _host.ApplyPlayerDamage(30, PlayerDamageSource.Fall);
        Check(_host.Vitals.Health == 80, $"and 30 health at Gentle takes three quarters of five, not six ({_host.Vitals.Health} left)");
        Check(Ok(_host.PlayerMode("creative")) && _host.ApplyPlayerDamage(1.5f, PlayerDamageSource.Fall) == null && _host.ApplyPlayerDamage(500, PlayerDamageSource.Fall) == null &&
              _host.Vitals.Health == 100, "in Creative nothing hurts, whatever its size");
        _host.UseDifficulty("standard");
    }

    // ---- the tumble's look ----

    /// <summary>
    /// What a fall looks like never depends on the difficulty or the mode, only what it costs: the tumble asks the vitals' fall
    /// damage in health points (Characters/Tumble/TumbleTiers.cs, M5; absolute, decision 24), not the rules, so the three
    /// thresholds are Standard's in Creative, at every difficulty and with any Big heart. This pins the numbers, the quarters the
    /// same landing costs under each rule set against the one tier it shows, and that the tumble's files read no rules.
    /// </summary>
    private void TestTumbleLookIsStandards()
    {
        var v5 = IslandRules.Load("storybook_wild", 5);
        Check(TumbleTiers.StumbleHealth == 0 && TumbleTiers.TumbleHealth == 25 && TumbleTiers.KnockoutHealth == PlayerVitals.MaximumHealth && PlayerVitals.MaximumHealth == 100,
            "the tumble's thresholds are Standard's absolute health points on the base body: a stumble past 0, a flop from 25, the knockout flop at 100");
        var rulesets = new (string Label, VitalsRules Rules)[]
        {
            ("Creative", VitalsRules.For(v5, "creative")), ("Gentle", VitalsRules.For(v5, "challenge", "gentle")), ("Standard", VitalsRules.For(v5, "challenge")),
            ("Brave", VitalsRules.For(v5, "challenge", "brave")), ("Live", VitalsRules.For(v5, "live")),
            ("Big heart", VitalsRules.For(v5, "challenge", knacks: new Dictionary<string, int> { ["big_heart"] = 2 })),
        };
        var ladder = new[] { 0.31f, 0.4f, 0.55f, 0.9f, 1.2f, 1.6f, 2.5f };
        var sameTiers = true;
        var costsDiffer = false;
        var said = "";
        foreach (var height in ladder)
        {
            var speed = MathF.Sqrt(2 * 3.5f * height);
            var cost = PlayerVitals.FallDamage(speed, false);
            var tier = TumbleTiers.Classify(speed, false);
            var expected = cost <= 0 ? TumbleTier.None : cost >= 100 ? TumbleTier.Knockout : cost >= 25 ? TumbleTier.Tumble : TumbleTier.Stumble;
            foreach (var (label, rules) in rulesets)
            {
                // Whatever the rules take for the landing, the tier the body shows asks the cost in health points alone.
                var ok = tier == expected && TumbleTiers.Classify(cost) == tier && TumbleTiers.Classify(speed, false, BodyKnacks.None) == tier;
                if (!ok && sameTiers) said = $"{label} at {height} m";
                sameTiers &= ok;
            }
            costsDiffer |= rulesets[1].Rules.HarmQuarters(cost, true) != rulesets[3].Rules.HarmQuarters(cost, true);
        }
        Check(sameTiers && costsDiffer, "the same landings show the same tier under every mode, difficulty and Big heart, while what they cost in hearts differs by difficulty " + said);
        // The knockout flop at Standard's 100 health, with full hearts: Gentle halves what it costs and does not knock out, and the look is still the knockout's.
        var knockoutSpeed = PlayerVitals.SafeLandingSpeedMps + 100f / PlayerVitals.FallDamagePerMps + 0.01f;
        _host.UseRules(v5);
        _host.UseDifficulty("gentle");
        _host.ConfigurePlayerVitals(new ModeTestStore());
        Check(Ok(_host.PlayerMode("challenge")), "a Gentle Challenge body for the knockout landing");
        var recoveries = _host.Vitals.Recoveries;
        _player.FallObserved?.Invoke(knockoutSpeed, false);
        Check(TumbleTiers.Classify(knockoutSpeed, false) == TumbleTier.Knockout && _host.Vitals.Recoveries == recoveries && _host.Vitals.Health is > 0 and < 100,
            $"a landing the look shows as a knockout costs a full-hearted Gentle body half its hearts and knocks nothing out ({_host.Vitals.Health} left): the look follows the landing, the hearts follow the difficulty");
        _host.UseDifficulty("standard");

        // The tumble's own files read no rules: nothing there names the difficulty, the mode's multipliers or the host.
        var root = ProjectSettings.GlobalizePath("res://scripts/native/Characters/Tumble");
        var reads = new[] { "Difficulty", "DamageMultiplier", "RefillMultiplier", "VitalsRules", "BodyRules", "CommandHost", "ModeRow" };
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(root, "*.cs"))
        {
            var code = string.Join('\n', File.ReadAllLines(file).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            offenders.AddRange(reads.Where(word => code.Contains(word, StringComparison.Ordinal)).Select(word => Path.GetFileName(file) + " reads " + word));
        }
        Check(Directory.GetFiles(root, "*.cs").Length >= 5 && offenders.Count == 0, "and the tumble's code reads neither the difficulty nor the host's rules: " + string.Join(", ", offenders));
    }

    // ---- the Live row's torch ----

    private void TestTorchSelfIgnite()
    {
        var v5 = IslandRules.Load("storybook_wild", 5);
        var live = VitalsRules.For(v5, "live");
        Check(v5.Modes!.Rows.Where(r => r.Mode == "live").All(r => r.TorchSelfIgnite) && v5.Modes.Rows.Where(r => r.Mode != "live").All(r => !r.TorchSelfIgnite) &&
              live.LiveRow is { Mode: "live", Variant: "cooperative", Available: false, TorchSelfIgnite: true } && live.TorchSelfIgnite,
            "v5's Live rows say a torch left in dry grass catches and no other row does; the rules read the Live row (cooperative) and say so for a body in Live");
        Check(!VitalsRules.For(v5, "creative").TorchSelfIgnite && !VitalsRules.For(v5, "challenge", "gentle").TorchSelfIgnite && !VitalsRules.For(v5, "challenge").TorchSelfIgnite &&
              !VitalsRules.For(v5, "challenge", "brave").TorchSelfIgnite && !VitalsRules.For(null, "live").TorchSelfIgnite && !VitalsRules.For(IslandRules.Load("storybook_wild", 4), "live").TorchSelfIgnite,
            "in Creative and at every difficulty of Challenge it is false, and it is false for a pack with no modes block, whatever the mode");
        // From the pack: a Live row that says false reads false; Live itself plays as Challenge Standard (damage, refill) whatever its row says.
        var calm = IslandRules.Parse(V5Edits(("\"variant\": \"cooperative\", \"available\": false, \"damage_multiplier\": 1, \"refill_multiplier\": 1, \"consequences\": true, \"undo\": false, \"shadow_pool\": false, \"torch_self_ignite\": true",
            "\"variant\": \"cooperative\", \"available\": false, \"damage_multiplier\": 3, \"refill_multiplier\": 1, \"consequences\": true, \"undo\": false, \"shadow_pool\": false, \"torch_self_ignite\": false")));
        var calmLive = VitalsRules.For(calm, "live");
        Check(!calmLive.TorchSelfIgnite && calmLive.LiveRow!.DamageMultiplier == 3 && calmLive.DamageMultiplier == 1,
            "the switch is the pack's (false in a pack that says so), and the rest of Live's row waits: Live still hurts as Standard while it is refused");
        // The host says it as the rules do, and nothing in play changes: a game is never in Live (the boot and world.set_mode refuse it).
        _host.UseRules(v5);
        Check(!_host.TorchSelfIgnite, "the host reports no torch that catches by itself in a game that is not in Live");
    }

    // ---- what the menu may offer ----

    private void TestModeAvailability()
    {
        var v5 = IslandRules.Load("storybook_wild", 5);
        Check(v5.ModeAvailable("creative") && v5.ModeAvailable("challenge") && v5.ModeAvailable("challenge", "gentle") && v5.ModeAvailable("challenge", "standard") && v5.ModeAvailable("challenge", "brave"),
            "v5 offers Creative and Challenge at Gentle, Standard and Brave");
        Check(!v5.ModeAvailable("live") && !v5.ModeAvailable("live", variant: "cooperative") && !v5.ModeAvailable("live", variant: "competitive") && !v5.ModeAvailable("live", "standard"),
            "and offers no Live, cooperative or competitive: its rows say available false");
        Check(!v5.ModeAvailable("creative", "standard") && !v5.ModeAvailable("challenge", "expert") && !v5.ModeAvailable("challenge", "standard", "cooperative") && !v5.ModeAvailable("sandbox") && !v5.ModeAvailable(""),
            "a choice that cannot exist is not offered: a difficulty for Creative, an unknown difficulty, a variant for Challenge, an unknown mode");
        var noBrave = IslandRules.Parse(V5("\"difficulty\": \"brave\", \"available\": true,", "\"difficulty\": \"brave\", \"available\": false,"));
        Check(noBrave.ModeAvailable("challenge") && noBrave.ModeAvailable("challenge", "gentle") && !noBrave.ModeAvailable("challenge", "brave"),
            "a Challenge difficulty the pack marks unavailable is not offered, the others are");
        var v4 = IslandRules.Load("storybook_wild", 4);
        Check(v4.ModeAvailable("creative") && v4.ModeAvailable("challenge") && v4.ModeAvailable("challenge", "standard") && !v4.ModeAvailable("challenge", "gentle") && !v4.ModeAvailable("challenge", "brave") && !v4.ModeAvailable("live"),
            "a pack with no modes block offers Creative and Challenge at Standard, the way it plays today, and nothing else");
        Check(v5.Modes!.Row("live", null, "competitive") is { Variant: "competitive" } && v5.Modes.Row("live", null) is { Variant: "cooperative" } && v5.Modes.Row("live", null, "solo") == null,
            "the Live rows are found by variant");
        // In play the same rows gate the difficulty: a boot asking for one the pack withholds keeps the current one.
        _host.UseRules(noBrave);
        _host.UseDifficulty("standard");
        Check(!_host.UseDifficulty("brave") && _host.Difficulty == "standard" && _host.UseDifficulty("gentle") && _host.Difficulty == "gentle" && !_host.UseDifficulty("hard") && _host.Difficulty == "gentle",
            "the host plays a Challenge difficulty only if the pack offers it (UseDifficulty says whether it did): Brave withheld, Gentle taken, an unknown word ignored");
        _host.UseRules(v4);
        Check(_host.UseDifficulty("brave") && _host.Difficulty == "brave", "a pack with no modes block withholds no difficulty (it has no rows to say so), as before");
        _host.UseDifficulty("standard");
        _host.UseRules(v5);
    }

    // ---- the names block, served to the screen ----

    private void TestNamesServed()
    {
        var v5 = IslandRules.Load("storybook_wild", 5);
        _host.UseRules(v5);
        Check(_host.NameOf("hearts") == "Hearts" && _host.NameOf("stamina") == "Stamina bar" && _host.NameOf("pips") == "Pips" && _host.NameOf("fractals") == "Fractals" &&
              _host.NameOf("expertise") == "Expertise" && _host.NameOf("camp") == "Camp" && _host.NameOf("trust") == "Trust" &&
              _host.KnackName("quick_feet") == "Quick feet" && _host.KnackName("steady_climber") == "Steady climber" && _host.KnackName("strong_arm") == "Strong arm",
            "the kernel serves the pack's words: hearts, the stamina bar, pips, Fractals, expertise, camp, Trust and the knacks");
        _host.UseRules(IslandRules.Load("storybook_wild", 4));
        Check(_host.NameOf("hearts") == "Hearts" && _host.NameOf("stamina") == "Stamina" && _host.NameOf("expertise") == "Discoveries" && _host.NameOf("fractals") == "Fractals" &&
              _host.NameOf("camp") == "Camp" && _host.NameOf("trust") == "Trust" && _host.KnackName("quick_feet") == "Quick feet" && _host.KnackName("a_made_up_one") == "A made up one",
            "a pack with no names block keeps today's words (the journal's page was Discoveries), and a knack is its id in words");
        _host.UseRules(null, "no rules for the names");
        Check(_host.NameOf("pips") == "Pips" && _host.NameOf("expertise") == "Discoveries", "and so does a game whose rules did not load");

        // The pack's own renaming reaches the screen, one name at a time; a name it leaves out keeps today's.
        var renamed = IslandRules.Parse(V5("\"hearts\": \"Hearts\", \"stamina\": \"Stamina bar\", \"pips\": \"Pips\", \"fractals\": \"Fractals\", \"expertise\": \"Expertise\", \"camp\": \"Camp\", \"trust\": \"Trust\"",
            "\"hearts\": \"Lives\", \"stamina\": \"Zest\", \"pips\": \"Sparks\", \"fractals\": \"Stars\", \"expertise\": \"Lore\", \"camp\": \"Hearth\", \"trust\": \"Bond\""));
        _host.UseRules(renamed);
        Check(_host.NameOf("hearts") == "Lives" && _host.NameOf("stamina") == "Zest" && _host.NameOf("pips") == "Sparks" && _host.NameOf("fractals") == "Stars" &&
              _host.NameOf("expertise") == "Lore" && _host.NameOf("camp") == "Hearth" && _host.NameOf("trust") == "Bond" && _host.NameOf("not_a_name") == "not_a_name",
            "a pack that renames them all is read: Lives, Zest, Sparks, Stars, Lore, Hearth, Bond");
        var partial = IslandRules.Parse(V5("\"hearts\": \"Hearts\", \"stamina\": \"Stamina bar\", \"pips\": \"Pips\", \"fractals\": \"Fractals\", \"expertise\": \"Expertise\", \"camp\": \"Camp\", \"trust\": \"Trust\"",
            "\"pips\": \"Sparks\""));
        _host.UseRules(partial);
        Check(_host.NameOf("pips") == "Sparks" && _host.NameOf("hearts") == "Hearts" && _host.NameOf("expertise") == "Discoveries" && _host.KnackName("big_heart") == "Big heart",
            "a names block that names only the pips keeps today's words for the rest");

        // Names are data: shown through KernelText.DisplayText at the names' cap. The loader refuses what the contract refuses; a block
        // made in code is cleaned all the same.
        var spaced = new NamesRules(new Dictionary<string, string>
        {
            ["hearts"] = "  Big   hearts \t", ["stamina"] = "   ", ["pips"] = "Pi​ps", ["fractals"] = new string('F', 60), ["camp"] = "​​", ["trust"] = "Refused.",
        }, new Dictionary<string, string> { ["quick_feet"] = " Fast  feet ", ["high_hop"] = "‮" });
        Check(spaced.Word("hearts") == "Big hearts" && spaced.Word("stamina") == null && spaced.Word("pips") == "Pi ps" && spaced.Word("fractals") == new string('F', NamesRules.MaxLength) &&
              spaced.Word("camp") == null && spaced.Word("trust") == "Refused." && spaced.Word("expertise") == null && spaced.Knack("quick_feet") == "Fast feet" && spaced.Knack("high_hop") == null && spaced.Knack("cat_feet") == null,
            "a name is cleaned as display text is: runs of blanks become one, hidden characters become spaces, it is cut at 40 characters, and a name with nothing left to show is no name");
        Check(Refused(V5("\"hearts\": \"Hearts\"", "\"hearts\": \"" + new string('H', 41) + "\"")) && Refused(V5("\"camp\": \"Camp\"", "\"camp\": \"Ca\\u202emp\"")),
            "and the pack's own loader refuses a name past 40 characters or with a hidden one");
        Check(NamesRules.Singular("Fractals") == "Fractal" && NamesRules.Singular("Pips") == "Pip" && NamesRules.Singular("Stars") == "Star" && NamesRules.Singular("Lore") == "Lore" && NamesRules.Singular("s") == "s",
            "a name as one thing loses a trailing s and nothing else");
        _host.UseRules(null, "The names checks are over.");
    }
}
