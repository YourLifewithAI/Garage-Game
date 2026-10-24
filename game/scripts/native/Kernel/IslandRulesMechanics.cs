using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace GarageGame.Native.Kernel;

/// <summary>
/// Hearts, counted in quarters (the pack's traits.hearts). Where and how fast they heal, each optional and today's when absent: away
/// from a camp at HealQuarterEveryS (HealOutsideCamp, true: anywhere on safe ground); at a camp every CampHealQuarterEveryS (null:
/// twice the pace, decision 62's CampLaws.CampHeartsFactor); and the time the game was closed caught up at the resting pace
/// (HealWhileClosed, true). v5 says false, 1 s and false (decisions 84, 91 and 93: only resting at a camp heals, a quarter a second,
/// and you wake with what you had). Every heal waits HealAfterUnharmedS without harm.
/// </summary>
public sealed record HeartsRules(double HealthPerQuarter, int StartHearts, int CapHearts, double HealQuarterEveryS, double HealAfterUnharmedS,
    bool HealOutsideCamp = true, double? CampHealQuarterEveryS = null, bool HealWhileClosed = true);

/// <summary>How the world hurts, in quarters (traits.harm). FallCapQuarters null: no cap, today's behaviour.</summary>
public sealed record HarmRules(double FireQuartersPerS, int? FallCapQuarters);

/// <summary>
/// The stamina bar in bands (traits.stamina). The water rates null: nothing drains there, today's behaviour. At empty the body
/// cannot run until the first bar refills to EmptyNoRunUntilFraction of itself (the founder, 15 October: completely). The climb's
/// metres per metre of bar and the holding cost null: today's (a metre of bar per metre risen, 0.01 m a second hanging still);
/// v5 says 1.5 and 0 (the founder, 16 October, decisions 64 and 65).
/// </summary>
public sealed record StaminaRules(double StartM, double BandM, int Bands, int BandsPerBar, double RunningMPerS, double RefillMPerS,
    double? DeepWaterMPerS, double? DiveMPerS, double EmptyNoRunUntilFraction, double? ClimbMPerBarM = null, double? HoldingMPerS = null);

/// <summary>Connection energy in whole pips (traits.pips).</summary>
public sealed record PipsRules(int Start, int ExpertisePerPip, int Ceiling, double RefillNearEveryS, double RefillNearWithinM, double RefillFarEveryS, double CampFraction);

/// <summary>The team's wallet when a save is made (traits.wallet.start), in the creation compiler's materials.</summary>
public sealed record WalletRules(int Wood, int Stone, int Copper, int Leaf);

/// <summary>The body's and the pool's numbers (the pack's traits block). Each part null: today's behaviour for it.</summary>
public sealed record TraitsRules(HeartsRules? Hearts, HarmRules? Harm, StaminaRules? Stamina, PipsRules? Pips, WalletRules? Wallet);

/// <summary>One ability's price: the player's order pays Ordered; the Gubble's own idea pays OwnIdea, a whole number of pips.</summary>
public sealed record AbilityPrice(int Ordered, double OwnIdeaFactor)
{
    /// <summary>Ordered times the own-idea factor: whole by the loader's check, so rounding only clears float noise.</summary>
    public int OwnIdea { get; } = (int)Math.Round(Ordered * OwnIdeaFactor, MidpointRounding.AwayFromZero);
}

/// <summary>What the Gubble's abilities cost outside Creative (the pack's cost block).</summary>
public sealed record CostRules(IReadOnlyDictionary<string, AbilityPrice> Prices, int TorchFire);

/// <summary>One mode, one difficulty of Challenge or one kind of Live (the pack's modes rows).</summary>
public sealed record ModeRow(string Mode, string? Difficulty, string? Variant, bool Available, double DamageMultiplier, double RefillMultiplier,
    bool Consequences, bool Undo, bool ShadowPool, int? FallCapQuarters, bool TorchSelfIgnite);

/// <summary>The modes block: one record the game consults per mode and difficulty.</summary>
public sealed record ModesRules(IReadOnlyList<ModeRow> Rows)
{
    /// <summary>The row for a mode and, in Challenge, a difficulty (in Live, a variant when one is named: else the first Live row); null when the pack has none.</summary>
    public ModeRow? Row(string mode, string? difficulty, string? variant = null)
    {
        foreach (var row in Rows)
            if (row.Mode == mode && (mode != "challenge" || row.Difficulty == difficulty) && (mode != "live" || variant == null || row.Variant == variant)) return row;
        return null;
    }

    /// <summary>
    /// Whether the pack offers this mode now (the rows' `available`): Creative; Challenge, at a difficulty when one is named (else at
    /// any); Live, as a variant when one is named (else any). A Creative or Live row has no difficulty and a Challenge or Creative row
    /// no variant, so naming one that cannot exist is not offered. The pack's Live rows say `available: false` until Live is built.
    /// </summary>
    public bool Available(string mode, string? difficulty = null, string? variant = null)
    {
        foreach (var row in Rows)
        {
            if (row.Mode != mode || !row.Available) continue;
            if (mode == "challenge" ? variant == null && (difficulty == null || row.Difficulty == difficulty)
                : mode == "live" ? difficulty == null && (variant == null || row.Variant == variant)
                : difficulty == null && variant == null) return true;
        }
        return false;
    }
}

/// <summary>
/// What the screen calls things (the pack's names block): untrusted text, shown only. The words reach the screen through
/// <see cref="Word"/> and <see cref="Knack"/> (KernelText.DisplayText at <see cref="MaxLength"/>: hidden characters become
/// spaces, runs collapse, the ends are trimmed, the length is cut), never as the pack wrote them; ids and icons never change.
/// A pack without the block, or one that leaves a name out, keeps today's words (<see cref="Today"/>).
/// </summary>
public sealed record NamesRules(IReadOnlyDictionary<string, string> Words, IReadOnlyDictionary<string, string> Knacks)
{
    /// <summary>The longest a name is shown (the contract's display_text cap for a name).</summary>
    public const int MaxLength = 40;

    /// <summary>The pack's word for hearts, stamina, pips, fractals, expertise, camp or trust as the screen shows it; null when the pack leaves it out (or it is all blanks).</summary>
    public string? Word(string key) => Shown(Words, key);

    /// <summary>The pack's name for a knack as the screen shows it; null when the pack does not name it.</summary>
    public string? Knack(string id) => Shown(Knacks, id);

    private static string? Shown(IReadOnlyDictionary<string, string> names, string key)
    {
        if (!names.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return null;
        var shown = KernelText.DisplayText(raw, MaxLength);
        // DisplayText answers "Refused." for text with nothing left to show: a name made only of hidden characters is no name.
        return shown == "Refused." && raw.Trim() != "Refused." ? null : shown;
    }

    /// <summary>Today's word for each name, when the pack has no names block or leaves one out (the journal's page was Discoveries before the pack's word).</summary>
    public static string Today(string key) => key switch
    {
        "hearts" => "Hearts", "stamina" => "Stamina", "pips" => "Pips", "fractals" => "Fractals", "expertise" => "Discoveries",
        "camp" => "Camp", "trust" => "Trust", _ => key,
    };

    /// <summary>A knack's words when the pack does not name it: its id, "quick_feet" as "Quick feet".</summary>
    public static string TodayKnack(string id) =>
        id.Length == 0 ? id : char.ToUpperInvariant(id[0]) + id[1..].Replace('_', ' ');

    /// <summary>The word as one thing ("Fractals" reads "Fractal", "Pips" reads "Pip"): a name ending in s loses it; any other is kept.</summary>
    public static string Singular(string word) => word.Length > 1 && word.EndsWith('s') ? word[..^1] : word;
}

public sealed record KnackSlot(int Slot, string Opens);

/// <summary>One knack: the one number it moves and its ranks (rank 1 first).</summary>
public sealed record KnackRule(string Knack, string Moves, string Unit, IReadOnlyList<double> Ranks, bool Available);

public sealed record KnacksRules(IReadOnlyList<KnackSlot> Slots, IReadOnlyList<KnackRule> Table)
{
    /// <summary>The table's row for a knack id, or null.</summary>
    public KnackRule? Knack(string id) => Table.FirstOrDefault(k => k.Knack == id);
}

/// <summary>The island's Fractals: the budget and how they are spent (the pack's fractals block).</summary>
public sealed record FractalsRules(int BudgetMin, int BudgetMax, int? ReachableFirstRun, int PerHeart, int PerBand, bool Typed);

/// <summary>
/// The mechanics round's six optional blocks (contracts/island-rules.schema.json, M0 of docs/runs/RUN-2-MECHANICS.md), read
/// fail-closed as the rest of the pack is: the schema's rules (fields, types, ranges, enums, the mode rows' if/then) and the
/// checks contracts/validate.py makes across them (_check_mechanics): prices name the pack's abilities and halve to whole
/// pips, the most health and stamina a pack allows fit player.status's bounds, the mode rows are unique with one Creative
/// and a Challenge, knacks stay within a fifth and upside, names name the pack's knacks, the Fractal budget is ordered.
/// Any problem refuses the whole pack. An absent block is null: today's behaviour for everything it would hold.
/// </summary>
public sealed partial class IslandRules
{
    /// <summary>player.status's bounds (contracts/game-command.schema.json): 30 hearts with Big heart at full rank, the top band with Big bar.</summary>
    public const double ContractMaxHealth = 720;
    public const double ContractMaxStaminaM = 4.8;
    /// <summary>The Bubbles expertise a cost block may price, each carried out by the pack's particles.float ability.</summary>
    public static readonly IReadOnlyList<string> BubblesExpertise = Array.AsReadOnly(new[] { "bubble_carry", "bubble_boat", "bubble_lift" });
    /// <summary>Fire's kinds a cost block may price (decisions 78 to 80), each carried out by the pack's element.ignite ability.</summary>
    public static readonly IReadOnlyList<string> FireKinds = Array.AsReadOnly(new[] { "fireline", "snuff", "will_o_wisp" });
    private static readonly string[] KnackMoves =
    {
        "walk_run_speed", "hop_height", "stamina_per_bar", "climb_per_stamina", "swim", "breath", "health_per_quarter", "safe_drop", "build_time", "plant_growth", "gather_speed",
    };
    /// <summary>Knack numbers that fall to be better (validate.py KNACK_LOWERS); safe_drop is in metres.</summary>
    private static readonly string[] KnackLowers = { "breath", "build_time" };
    private static readonly string[] NameWords = { "hearts", "stamina", "pips", "fractals", "expertise", "camp", "trust" };

    public TraitsRules? Traits { get; private init; }
    public CostRules? Cost { get; private init; }
    public ModesRules? Modes { get; private init; }
    public NamesRules? Names { get; private init; }
    public KnacksRules? Knacks { get; private init; }
    public FractalsRules? Fractals { get; private init; }

    /// <summary>
    /// Whether this pack offers a mode, and in Challenge a difficulty, now (the modes rows' `available`): what the menu (M7) asks before
    /// it shows a choice, and what play checks before it plays one. The pack's Live rows are not available until Live is built; a pack
    /// without a modes block offers Creative and Challenge at Standard (today's), no other difficulty and no Live.
    /// </summary>
    public bool ModeAvailable(string mode, string? difficulty = null, string? variant = null) =>
        Modes != null ? Modes.Available(mode, difficulty, variant)
        : mode == "creative" ? difficulty == null && variant == null
        : mode == "challenge" && variant == null && (difficulty is null or "standard");

    private sealed record Mechanics(TraitsRules? Traits, CostRules? Cost, ModesRules? Modes, NamesRules? Names, KnacksRules? Knacks, FractalsRules? Fractals);

    private static Mechanics ParseMechanics(JsonElement root, List<IslandAbility> abilities)
    {
        var knacks = root.TryGetProperty("knacks", out var k) ? ParseKnacks(k) : null;
        var traits = root.TryGetProperty("traits", out var t) ? ParseTraits(t) : null;
        var cost = root.TryGetProperty("cost", out var c) ? ParseCost(c, abilities) : null;
        var modes = root.TryGetProperty("modes", out var m) ? ParseModes(m) : null;
        var names = root.TryGetProperty("names", out var n) ? ParseNames(n, knacks) : null;
        var fractals = root.TryGetProperty("fractals", out var f) ? ParseFractals(f) : null;
        // validate.py: the most health and stamina this pack allows fit player.status's bounds, with the best knack rank.
        double Best(string moves) => knacks?.Table.Where(r => r.Moves == moves).SelectMany(r => r.Ranks).DefaultIfEmpty(1.0).Max() ?? 1.0;
        if (traits?.Hearts is { } hearts)
            Expect(hearts.CapHearts * 4 * hearts.HealthPerQuarter * Best("health_per_quarter") <= ContractMaxHealth + 1e-9,
                "$.traits.hearts: the most health this pack allows (cap_hearts x 4 x health_per_quarter x the best health_per_quarter knack) is above player.status's 720.");
        if (traits?.Stamina is { } stamina)
            Expect((stamina.StartM + stamina.BandM * (stamina.Bands - 1)) * Best("stamina_per_bar") <= ContractMaxStaminaM + 1e-9,
                "$.traits.stamina: the most stamina this pack allows (the top band x the best stamina_per_bar knack) is above player.status's 4.8 m.");
        return new Mechanics(traits, cost, modes, names, knacks, fractals);
    }

    private static TraitsRules ParseTraits(JsonElement traits)
    {
        const string at = "$.traits";
        Expect(traits.ValueKind == JsonValueKind.Object, at + " must be an object.");
        Fields(traits, at, new[] { "hearts", "harm", "stamina", "pips", "wallet" }, Array.Empty<string>());
        Expect(traits.EnumerateObject().Any(), at + " holds at least one part.");
        HeartsRules? hearts = null;
        if (traits.TryGetProperty("hearts", out var h))
        {
            var where = at + ".hearts";
            Expect(h.ValueKind == JsonValueKind.Object, where + " must be an object.");
            var required = new[] { "health_per_quarter", "start_hearts", "cap_hearts", "heal_quarter_every_s", "heal_after_unharmed_s" };
            Fields(h, where, required.Concat(new[] { "heal_outside_camp", "camp_heal_quarter_every_s", "heal_while_closed" }).ToArray(), required);
            hearts = new HeartsRules(Num(h, "health_per_quarter", where, 0, 6, exclusiveMin: true), IntIn(h, "start_hearts", where, 1, 30), IntIn(h, "cap_hearts", where, 1, 30),
                Num(h, "heal_quarter_every_s", where, 0, 600, exclusiveMin: true), Num(h, "heal_after_unharmed_s", where, 0, 600),
                !h.TryGetProperty("heal_outside_camp", out _) || Bool(h, "heal_outside_camp", where),
                h.TryGetProperty("camp_heal_quarter_every_s", out _) ? Num(h, "camp_heal_quarter_every_s", where, 0, 600, exclusiveMin: true) : null,
                !h.TryGetProperty("heal_while_closed", out _) || Bool(h, "heal_while_closed", where));
            Expect(hearts.StartHearts <= hearts.CapHearts, where + ".start_hearts is above cap_hearts.");
        }
        HarmRules? harm = null;
        if (traits.TryGetProperty("harm", out var hm))
        {
            var where = at + ".harm";
            Expect(hm.ValueKind == JsonValueKind.Object, where + " must be an object.");
            Fields(hm, where, new[] { "fire_quarters_per_s", "fall_cap_quarters" }, new[] { "fire_quarters_per_s" });
            harm = new HarmRules(Num(hm, "fire_quarters_per_s", where, 0, 8, exclusiveMin: true),
                hm.TryGetProperty("fall_cap_quarters", out _) ? IntIn(hm, "fall_cap_quarters", where, 1, 120) : null);
        }
        StaminaRules? stamina = null;
        if (traits.TryGetProperty("stamina", out var s))
        {
            var where = at + ".stamina";
            Expect(s.ValueKind == JsonValueKind.Object, where + " must be an object.");
            var required = new[] { "start_m", "band_m", "bands", "bands_per_bar", "running_m_per_s", "refill_m_per_s", "empty_no_run_until_fraction" };
            Fields(s, where, required.Concat(new[] { "deep_water_m_per_s", "dive_m_per_s", "climb_m_per_bar_m", "holding_m_per_s" }).ToArray(), required);
            stamina = new StaminaRules(Num(s, "start_m", where, 0, 4.8, exclusiveMin: true), Num(s, "band_m", where, 0, 4.8, exclusiveMin: true),
                IntIn(s, "bands", where, 1, 20), IntIn(s, "bands_per_bar", where, 1, 20), Num(s, "running_m_per_s", where, 0, 1),
                Num(s, "refill_m_per_s", where, 0, 10, exclusiveMin: true),
                s.TryGetProperty("deep_water_m_per_s", out _) ? Num(s, "deep_water_m_per_s", where, 0, 1) : null,
                s.TryGetProperty("dive_m_per_s", out _) ? Num(s, "dive_m_per_s", where, 0, 1) : null,
                Num(s, "empty_no_run_until_fraction", where, 0, 1, exclusiveMin: true),
                s.TryGetProperty("climb_m_per_bar_m", out _) ? Num(s, "climb_m_per_bar_m", where, 0, 10, exclusiveMin: true) : null,
                s.TryGetProperty("holding_m_per_s", out _) ? Num(s, "holding_m_per_s", where, 0, 1) : null);
        }
        PipsRules? pips = null;
        if (traits.TryGetProperty("pips", out var p))
        {
            var where = at + ".pips";
            Expect(p.ValueKind == JsonValueKind.Object, where + " must be an object.");
            var names = new[] { "start", "expertise_per_pip", "ceiling", "refill_near_every_s", "refill_near_within_m", "refill_far_every_s", "camp_fraction" };
            Fields(p, where, names, names);
            pips = new PipsRules(IntIn(p, "start", where, 0, 40), IntIn(p, "expertise_per_pip", where, 1, 100), IntIn(p, "ceiling", where, 1, 40),
                Num(p, "refill_near_every_s", where, 0, 3600, exclusiveMin: true), Num(p, "refill_near_within_m", where, 0, 150, exclusiveMin: true),
                Num(p, "refill_far_every_s", where, 0, 3600, exclusiveMin: true), Num(p, "camp_fraction", where, 0, 1));
            Expect(pips.Start <= pips.Ceiling, where + ".start is above its ceiling.");
            Expect(pips.RefillNearEveryS <= pips.RefillFarEveryS, where + " refills slower near the player than apart.");
        }
        WalletRules? wallet = null;
        if (traits.TryGetProperty("wallet", out var w))
        {
            var where = at + ".wallet";
            Expect(w.ValueKind == JsonValueKind.Object, where + " must be an object.");
            Fields(w, where, new[] { "start" }, new[] { "start" });
            var start = w.GetProperty("start");
            Expect(start.ValueKind == JsonValueKind.Object, where + ".start must be an object.");
            var materials = new[] { "wood", "stone", "copper", "leaf" };
            Fields(start, where + ".start", materials, materials);
            wallet = new WalletRules(IntIn(start, "wood", where + ".start", 0, 10000), IntIn(start, "stone", where + ".start", 0, 10000),
                IntIn(start, "copper", where + ".start", 0, 10000), IntIn(start, "leaf", where + ".start", 0, 10000));
        }
        return new TraitsRules(hearts, harm, stamina, pips, wallet);
    }

    private static CostRules ParseCost(JsonElement cost, List<IslandAbility> abilities)
    {
        const string at = "$.cost";
        Expect(cost.ValueKind == JsonValueKind.Object, at + " must be an object.");
        Fields(cost, at, new[] { "prices", "torch_fire" }, new[] { "prices" });
        var list = cost.GetProperty("prices");
        Expect(list.ValueKind == JsonValueKind.Object && list.EnumerateObject().Count() is >= 1 and <= 64, at + ".prices names 1 to 64 abilities.");
        var prices = new Dictionary<string, AbilityPrice>(StringComparer.Ordinal);
        foreach (var entry in list.EnumerateObject())
        {
            Expect(Token.IsMatch(entry.Name), at + ".prices are named by lowercase tokens.");
            var where = $"{at}.prices.{entry.Name}";
            // An older pack's Bloom is read as Grow (IslandRules.LegacyBloomAs), and so is its price.
            var name = entry.Name == "bloom" && abilities.Any(a => a.Primitive == "growth.seed") ? IslandRules.LegacyBloomAs : entry.Name;
            Expect(entry.Value.ValueKind == JsonValueKind.Object, where + " must be an object.");
            Fields(entry.Value, where, new[] { "ordered", "own_idea_factor" }, new[] { "ordered", "own_idea_factor" });
            var price = new AbilityPrice(IntIn(entry.Value, "ordered", where, 0, 40), Num(entry.Value, "own_idea_factor", where, 0, 1));
            // validate.py: a price names an ability of this pack, or a Bubbles expertise of its particles.float ability.
            if (BubblesExpertise.Contains(entry.Name))
                Expect(abilities.Any(a => a.Primitive == "particles.float"), $"{where} prices a Bubbles expertise, but the pack grants no particles.float ability.");
            else if (FireKinds.Contains(entry.Name))
                Expect(abilities.Any(a => a.Primitive == "element.ignite"), $"{where} prices a Fire kind, but the pack grants no element.ignite ability.");
            else Expect(abilities.Any(a => a.Capability == name), $"{where} prices no ability of this pack.");
            var own = price.Ordered * price.OwnIdeaFactor;
            Expect(Math.Abs(own - Math.Round(own)) <= 1e-9, $"{where}: ordered times own_idea_factor is not a whole number of pips.");
            prices[name] = price;
        }
        var torch = cost.TryGetProperty("torch_fire", out _) ? IntIn(cost, "torch_fire", at, 0, 40) : 0;
        return new CostRules(prices, torch);
    }

    private static ModesRules ParseModes(JsonElement modes)
    {
        const string at = "$.modes";
        Expect(modes.ValueKind == JsonValueKind.Object, at + " must be an object.");
        Fields(modes, at, new[] { "rows" }, new[] { "rows" });
        var list = modes.GetProperty("rows");
        Expect(list.ValueKind == JsonValueKind.Array && list.GetArrayLength() is >= 1 and <= 16, at + ".rows lists 1 to 16 rows.");
        var rows = new List<ModeRow>();
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            var where = $"{at}.rows[{index++}]";
            Expect(item.ValueKind == JsonValueKind.Object, where + " must be an object.");
            var required = new[] { "mode", "available", "damage_multiplier", "refill_multiplier", "consequences", "undo", "shadow_pool", "torch_self_ignite" };
            Fields(item, where, required.Concat(new[] { "difficulty", "variant", "fall_cap_quarters" }).ToArray(), required);
            var mode = Str(item, "mode", where);
            Expect(mode is "creative" or "challenge" or "live", where + ".mode must be creative, challenge or live.");
            string? difficulty = item.TryGetProperty("difficulty", out _) ? Str(item, "difficulty", where) : null;
            Expect(difficulty is null or "gentle" or "standard" or "brave", where + ".difficulty must be gentle, standard or brave.");
            string? variant = item.TryGetProperty("variant", out _) ? Str(item, "variant", where) : null;
            Expect(variant is null or "cooperative" or "competitive", where + ".variant must be cooperative or competitive.");
            var row = new ModeRow(mode, difficulty, variant, Bool(item, "available", where), Num(item, "damage_multiplier", where, 0, 4),
                Num(item, "refill_multiplier", where, 0, 4, exclusiveMin: true), Bool(item, "consequences", where), Bool(item, "undo", where),
                Bool(item, "shadow_pool", where), item.TryGetProperty("fall_cap_quarters", out _) ? IntIn(item, "fall_cap_quarters", where, 1, 120) : null,
                Bool(item, "torch_self_ignite", where));
            // The founder's rules per row (the schema's if/then).
            switch (mode)
            {
                case "creative":
                    Expect(!row.Consequences && row.Undo && row.ShadowPool && row.DamageMultiplier == 0 && !row.TorchSelfIgnite && difficulty == null && variant == null,
                        where + ": Creative has no consequences and no damage, undo and a shadow pool, no difficulty and no torch that catches by itself.");
                    break;
                case "challenge":
                    Expect(row.Consequences && !row.Undo && !row.ShadowPool && row.DamageMultiplier > 0 && !row.TorchSelfIgnite && difficulty != null && variant == null,
                        where + ": Challenge has consequences and a difficulty, hurts by more than nothing, and has no undo, no shadow pool and no torch that catches by itself.");
                    break;
                default:
                    Expect(row.Consequences && !row.Undo && !row.ShadowPool && row.DamageMultiplier > 0 && !row.Available && difficulty == null && variant != null,
                        where + ": Live has consequences and a variant, hurts by more than nothing, has no undo or shadow pool, and is not available yet.");
                    break;
            }
            rows.Add(row);
        }
        // validate.py: the rows are unique, with exactly one Creative and at least one Challenge.
        Expect(rows.Select(r => (r.Mode, r.Difficulty, r.Variant)).Distinct().Count() == rows.Count, at + " has a row more than once.");
        Expect(rows.Count(r => r.Mode == "creative") == 1, at + " needs exactly one creative row.");
        Expect(rows.Any(r => r.Mode == "challenge"), at + " needs a challenge row.");
        return new ModesRules(rows);
    }

    private static NamesRules ParseNames(JsonElement names, KnacksRules? knacks)
    {
        const string at = "$.names";
        Expect(names.ValueKind == JsonValueKind.Object, at + " must be an object.");
        Fields(names, at, NameWords.Append("knacks").ToArray(), Array.Empty<string>());
        Expect(names.EnumerateObject().Any(), at + " names at least one thing.");
        var words = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var word in NameWords)
            if (names.TryGetProperty(word, out _)) words[word] = Display(names, word, at, 40, 1);
        var named = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (names.TryGetProperty("knacks", out var list))
        {
            Expect(list.ValueKind == JsonValueKind.Object && list.EnumerateObject().Count() is >= 1 and <= 32, at + ".knacks names 1 to 32 knacks.");
            foreach (var entry in list.EnumerateObject())
            {
                Expect(Token.IsMatch(entry.Name), at + ".knacks are named by lowercase tokens.");
                // validate.py: names name only the pack's knacks.
                Expect(knacks?.Knack(entry.Name) != null, at + ".knacks names a knack this pack does not have.");
                named[entry.Name] = Display(list, entry.Name, at + ".knacks", 40, 1);
            }
        }
        return new NamesRules(words, named);
    }

    private static KnacksRules ParseKnacks(JsonElement knacks)
    {
        const string at = "$.knacks";
        Expect(knacks.ValueKind == JsonValueKind.Object, at + " must be an object.");
        Fields(knacks, at, new[] { "slots", "table" }, new[] { "slots", "table" });
        var slotList = knacks.GetProperty("slots");
        Expect(slotList.ValueKind == JsonValueKind.Array && slotList.GetArrayLength() is >= 1 and <= 3, at + ".slots lists 1 to 3 slots.");
        var slots = new List<KnackSlot>();
        var index = 0;
        foreach (var item in slotList.EnumerateArray())
        {
            var where = $"{at}.slots[{index++}]";
            Expect(item.ValueKind == JsonValueKind.Object, where + " must be an object.");
            Fields(item, where, new[] { "slot", "opens" }, new[] { "slot", "opens" });
            var opens = Str(item, "opens", where);
            Expect(opens is "creation" or "first_summit" or "second_island_summit", where + ".opens must be creation, first_summit or second_island_summit.");
            slots.Add(new KnackSlot(IntIn(item, "slot", where, 1, 3), opens));
        }
        // validate.py: the slots are numbered 1 to n, each once, and slot 1 opens at creation.
        Expect(slots.Select(s => s.Slot).OrderBy(n => n).SequenceEqual(Enumerable.Range(1, slots.Count)), at + ".slots number the slots 1 to n, each once.");
        Expect(slots.All(s => s.Slot != 1 || s.Opens == "creation"), at + ": knack slot 1 opens at creation.");
        var tableList = knacks.GetProperty("table");
        Expect(tableList.ValueKind == JsonValueKind.Array && tableList.GetArrayLength() is >= 1 and <= 32, at + ".table lists 1 to 32 knacks.");
        var table = new List<KnackRule>();
        index = 0;
        foreach (var item in tableList.EnumerateArray())
        {
            var where = $"{at}.table[{index++}]";
            Expect(item.ValueKind == JsonValueKind.Object, where + " must be an object.");
            var fields = new[] { "knack", "moves", "unit", "ranks", "available" };
            Fields(item, where, fields, fields);
            var knack = TokenAt(item, "knack", where);
            var moves = Str(item, "moves", where);
            Expect(KnackMoves.Contains(moves), where + ".moves is not a number a knack may move.");
            var unit = Str(item, "unit", where);
            Expect(unit is "factor" or "m", where + ".unit must be factor or m.");
            var rankList = item.GetProperty("ranks");
            Expect(rankList.ValueKind == JsonValueKind.Array && rankList.GetArrayLength() is >= 1 and <= 3, where + ".ranks lists 1 to 3 ranks.");
            var ranks = new List<double>();
            foreach (var rank in rankList.EnumerateArray())
            {
                Expect(rank.ValueKind == JsonValueKind.Number, where + ".ranks are numbers.");
                double value;
                try { value = CanonicalJson.ReadNumber(rank); }
                catch (CanonicalJsonException) { throw new IslandRulesException(where + ".ranks are finite numbers."); }
                Expect(value > 0 && value <= 10, where + ".ranks are more than 0 and at most 10.");
                ranks.Add(value);
            }
            // validate.py: metres only for the safe drop, rising; every factor within a fifth, upside, each rank further.
            Expect((unit == "m") == (moves == "safe_drop"), where + ": only the safe drop is in metres, and every other number a factor.");
            if (unit == "m") Expect(ranks.Zip(ranks.Skip(1)).All(pair => pair.Second > pair.First), where + ": its ranks must rise.");
            else
            {
                var lowers = KnackLowers.Contains(moves);
                Expect(ranks.All(r => Math.Abs(r - 1) <= 0.2 + 1e-9), where + ": a rank moves its number by more than a fifth.");
                Expect(ranks.All(r => lowers ? r < 1 : r > 1), where + ": a rank is not upside.");
                var steps = ranks.Select(r => Math.Abs(r - 1)).ToList();
                Expect(steps.Zip(steps.Skip(1)).All(pair => pair.Second > pair.First), where + ": each rank must move its number further than the one before.");
            }
            table.Add(new KnackRule(knack, moves, unit, ranks, Bool(item, "available", where)));
        }
        Expect(table.Select(k => k.Knack).Distinct().Count() == table.Count, at + ": a knack appears more than once.");
        return new KnacksRules(slots, table);
    }

    private static FractalsRules ParseFractals(JsonElement fractals)
    {
        const string at = "$.fractals";
        Expect(fractals.ValueKind == JsonValueKind.Object, at + " must be an object.");
        var required = new[] { "budget_min", "budget_max", "per_heart", "per_band", "typed" };
        Fields(fractals, at, required.Append("reachable_first_run").ToArray(), required);
        var rules = new FractalsRules(IntIn(fractals, "budget_min", at, 0, 1000), IntIn(fractals, "budget_max", at, 1, 1000),
            fractals.TryGetProperty("reachable_first_run", out _) ? IntIn(fractals, "reachable_first_run", at, 0, 1000) : null,
            IntIn(fractals, "per_heart", at, 1, 100), IntIn(fractals, "per_band", at, 1, 100), Bool(fractals, "typed", at));
        Expect(rules.BudgetMin <= rules.BudgetMax, at + ".budget_min is above budget_max.");
        Expect((rules.ReachableFirstRun ?? 0) <= rules.BudgetMax, at + ".reachable_first_run is above budget_max.");
        return rules;
    }

    // ---- the schema's number rules ----

    /// <summary>A number within [min, max], or (min, max] when exclusiveMin.</summary>
    private static double Num(JsonElement element, string name, string at, double min, double max, bool exclusiveMin = false)
    {
        var value = Number(element, name, at);
        Expect((exclusiveMin ? value > min : value >= min) && value <= max,
            $"{at}.{name} must be {(exclusiveMin ? "more than" : "at least")} {Text(min)} and at most {Text(max)}.");
        return value;
    }

    private static int IntIn(JsonElement element, string name, string at, int min, int max)
    {
        var value = Int(element, name, at);
        Expect(value >= min && value <= max, $"{at}.{name} must be an integer from {min} to {max}.");
        return value;
    }

    private static bool Bool(JsonElement element, string name, string at)
    {
        var value = element.GetProperty(name);
        Expect(value.ValueKind is JsonValueKind.True or JsonValueKind.False, $"{at}.{name} must be true or false.");
        return value.ValueKind == JsonValueKind.True;
    }
}
