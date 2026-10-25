using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using GarageGame.Native.Room;

namespace GarageGame.Native.Kernel;

/// <summary>A rules pack that does not pass the loader's checks. The message names the field, never the pack's text.</summary>
public sealed class IslandRulesException(string message) : Exception(message);

/// <summary>One numeric parameter of an ability: its inclusive range and the default the host fills in.</summary>
public sealed record AbilityParam(double Min, double Max, double Default);

/// <summary>One ability of an island (contracts/island-rules.schema.json $defs/ability): an engine primitive with the island's narrower bounds.</summary>
public sealed class IslandAbility
{
    public string Capability { get; init; } = "";
    public string Category { get; init; } = "";
    public string Primitive { get; init; } = "";
    /// <summary>Untrusted text, like every name.</summary>
    public string DisplayName { get; init; } = "";
    /// <summary>Which principals may cast it: "player", "companion" or both.</summary>
    public IReadOnlySet<string> CastBy { get; init; } = new HashSet<string>();
    /// <summary>auto, auto_undo, preview_commit or keyed_yes (LIVE-VOICE.md's T0 to T3).</summary>
    public string Tier { get; init; } = "";
    /// <summary>"self" (the effect follows the caster's avatar) and or "point" (a light at area.center_m).</summary>
    public IReadOnlySet<string> Targets { get; init; } = new HashSet<string>();
    public double ReachM { get; init; }
    /// <summary>Each parameter by name, in ordinal order.</summary>
    public IReadOnlyDictionary<string, AbilityParam> Params { get; init; } = new SortedDictionary<string, AbilityParam>(StringComparer.Ordinal);
    public double AreaRadiusDefaultM { get; init; }
    public double AreaRadiusMaxM { get; init; }
    public double DurationDefaultS { get; init; }
    public double DurationMaxS { get; init; }
    public int MaxActive { get; init; }
}

/// <summary>
/// An island's own laws as data (contracts/island-rules.schema.json): the abilities it grants. Loaded from
/// res://rules/&lt;rules_id&gt;/v&lt;N&gt;.json and checked fail-closed, as RoomData checks rooms, because a pack the game
/// reads is never run through contracts/validate.py first: the schema's rules (unknown fields, types, enums, lengths,
/// each primitive's outer limits) and the validator's extra checks (one ability per capability and per category,
/// min at most default at most max, each default radius and duration at most its maximum, the pack at its own path),
/// with the display-text rules on every text field. Any problem refuses the whole pack.
/// </summary>
public sealed partial class IslandRules
{
    public const string SchemaId = SchemaIds.IslandRules;
    /// <summary>A pack is small (at most 32 abilities); anything larger is refused before it is parsed.</summary>
    public const int MaxPackBytes = 65536;

    private static readonly Regex Token = new(@"\A[a-z][a-z0-9_-]{0,63}\z", RegexOptions.Compiled);
    private static readonly Regex ParamName = new(@"\A[a-z][a-z0-9_]{0,31}\z", RegexOptions.Compiled);
    private static readonly Regex ExtensionKey = new(@"\Ax_[a-z0-9_]{1,63}\z", RegexOptions.Compiled);
    private static readonly string[] ReservedParams = { "principal", "action_id", "approval", "approval_id", "approved", "owner", "owner_id", "room_id", "actor", "grant", "role" };
    // The mechanics round's six blocks (traits, cost, modes, names, knacks, fractals) are read by IslandRulesMechanics.cs.
    private static readonly string[] TopFields = { "schema", "version", "rules_id", "rules_version", "display_name", "description", "status", "magic_word", "abilities", "extensions", "traits", "cost", "modes", "names", "knacks", "fractals", "inventory", "food" };
    private static readonly string[] TopRequired = { "schema", "version", "rules_id", "rules_version", "display_name", "status", "magic_word", "abilities" };
    private static readonly string[] AbilityFields =
    {
        "capability", "category", "primitive", "display_name", "cast_by", "tier", "targets", "reach_m", "params",
        "area_radius_default_m", "area_radius_max_m", "duration_default_s", "duration_max_s", "max_active",
    };
    private static readonly string[] Statuses = { "seed", "draft", "candidate", "approved", "retired" };
    private static readonly string[] Tiers = { "auto", "auto_undo", "preview_commit", "keyed_yes" };

    /// <summary>
    /// The engine's outer limits per primitive (the schema's if/then rules): the category it fixes, the only params it
    /// takes with their outer range, and the most reach, radius, duration and active count a pack may grant.
    /// </summary>
    public sealed record PrimitiveLimits(string Category, IReadOnlyDictionary<string, (double Min, double Max)> Params, double ReachM, double RadiusM, double DurationS, int MaxActive);

    public static readonly IReadOnlyDictionary<string, PrimitiveLimits> Primitives = new Dictionary<string, PrimitiveLimits>(StringComparer.Ordinal)
    {
        ["light.emit"] = new("light", new Dictionary<string, (double, double)>(StringComparer.Ordinal) { ["intensity"] = (0.05, 2.0) }, 5, 3, 600, 8),
        ["particles.float"] = new("float", new Dictionary<string, (double, double)>(StringComparer.Ordinal) { ["intensity"] = (0.05, 2.0) }, 5, 3, 120, 4),
        ["particles.burst"] = new("burst", new Dictionary<string, (double, double)>(StringComparer.Ordinal) { ["intensity"] = (0.05, 2.0) }, 8, 3, 10, 4),
        ["growth.seed"] = new("growth", new Dictionary<string, (double, double)>(StringComparer.Ordinal) { ["intensity"] = (0.05, 2.0) }, 5, 0.6, 600, 1),
        // Grow (decision 52): intensity is the share of its natural size a cast grows each plant (a quarter in storybook_wild v5).
        ["growth.grow"] = new("growth", new Dictionary<string, (double, double)>(StringComparer.Ordinal) { ["intensity"] = (0.05, 1.0) }, 5, 1.0, 600, 1),
        ["element.ignite"] = new("fire", new Dictionary<string, (double, double)>(StringComparer.Ordinal) { ["intensity"] = (0.05, 2.0) }, 5, 1.5, 60, 1),
    };

    /// <summary>The capability an older pack's Bloom (primitive growth.seed) is read as: Grow (decision 52).</summary>
    public const string LegacyBloomAs = "grow";

    public string RulesId { get; private init; } = "";
    public int RulesVersion { get; private init; }
    public string DisplayName { get; private init; } = "";
    public string Description { get; private init; } = "";
    public string Status { get; private init; } = "";
    public string MagicWord { get; private init; } = "";
    /// <summary>SHA-256 of the pack's bytes, as packs are pinned.</summary>
    public string Sha256 { get; private init; } = "";
    /// <summary>The abilities in the pack's order.</summary>
    public IReadOnlyList<IslandAbility> Abilities { get; private init; } = Array.Empty<IslandAbility>();

    public static string PathFor(string rulesId, int rulesVersion) => $"res://rules/{rulesId}/v{rulesVersion}.json";

    /// <summary>The ability with this capability name, or null.</summary>
    public IslandAbility? Ability(string? capability) => capability == null ? null : Abilities.FirstOrDefault(a => a.Capability == capability);

    /// <summary>Loads and checks res://rules/&lt;rulesId&gt;/v&lt;rulesVersion&gt;.json. Throws IslandRulesException for any problem.</summary>
    public static IslandRules Load(string rulesId, int rulesVersion)
    {
        if (!Token.IsMatch(rulesId ?? "") || rulesVersion < 1) throw new IslandRulesException("A rules id is a lowercase token and its version at least 1.");
        var path = PathFor(rulesId!, rulesVersion);
        if (!Godot.FileAccess.FileExists(path)) throw new IslandRulesException($"There is no rules pack at {path}.");
        var bytes = Godot.FileAccess.GetFileAsBytes(path);
        if (bytes.Length == 0) throw new IslandRulesException($"The rules pack at {path} could not be read or is empty.");
        return Parse(bytes, rulesId, rulesVersion);
    }

    /// <summary>
    /// Checks a pack's bytes. When expectedId and expectedVersion are given (a pack loaded from its path), the pack must
    /// name them: a pack lives at rules/&lt;rules_id&gt;/v&lt;rules_version&gt;.json.
    /// </summary>
    public static IslandRules Parse(byte[] bytes, string? expectedId = null, int? expectedVersion = null)
    {
        if (bytes.Length > MaxPackBytes) throw new IslandRulesException($"A rules pack is at most {MaxPackBytes} bytes.");
        JsonDocument document;
        // Strict JSON: no byte-order mark, duplicate key, comment, trailing comma, non-finite number or unpaired surrogate.
        try { document = CanonicalJson.Parse(bytes); }
        catch (CanonicalJsonException error) { throw new IslandRulesException("The rules pack is not strict JSON: " + error.Message); }
        using (document)
        {
            var root = document.RootElement;
            Expect(root.ValueKind == JsonValueKind.Object, "A rules pack is a JSON object.");
            Fields(root, "$", TopFields, TopRequired);
            Expect(SchemaIds.IsSchema(Str(root, "schema", "$"), "island_rules"), $"$.schema must be {SchemaIds.Both("island_rules")}.");
            Expect(Int(root, "version", "$") == 1, "$.version must be 1.");
            var rulesId = TokenAt(root, "rules_id", "$");
            var rulesVersion = Int(root, "rules_version", "$");
            Expect(rulesVersion >= 1, "$.rules_version must be at least 1.");
            var displayName = Display(root, "display_name", "$", 80, 1);
            var description = root.TryGetProperty("description", out _) ? Display(root, "description", "$", 500, 0) : "";
            var status = Str(root, "status", "$");
            Expect(Statuses.Contains(status), "$.status must be seed, draft, candidate, approved or retired.");
            var magicWord = Display(root, "magic_word", "$", 40, 1);
            if (root.TryGetProperty("extensions", out var extensions))
            {
                Expect(extensions.ValueKind == JsonValueKind.Object, "$.extensions must be an object.");
                Expect(extensions.EnumerateObject().Count() <= 32, "$.extensions holds at most 32 keys.");
                foreach (var key in extensions.EnumerateObject()) Expect(ExtensionKey.IsMatch(key.Name), "$.extensions keys start with x_ and name the owning lane.");
            }
            var list = root.GetProperty("abilities");
            Expect(list.ValueKind == JsonValueKind.Array && list.GetArrayLength() is >= 1 and <= 32, "$.abilities lists 1 to 32 abilities.");
            var abilities = new List<IslandAbility>();
            var index = 0;
            foreach (var item in list.EnumerateArray()) abilities.Add(ParseAbility(item, $"$.abilities[{index++}]"));
            // The validator's checks a schema cannot express.
            Expect(abilities.Select(a => a.Capability).Distinct().Count() == abilities.Count, "Capabilities are unique within a pack.");
            Expect(abilities.Select(a => a.Category).Distinct().Count() == abilities.Count, "Categories are unique within a pack.");
            if (expectedId != null) Expect(rulesId == expectedId, "The pack's rules_id is not the one its path names.");
            if (expectedVersion != null) Expect(rulesVersion == expectedVersion, "The pack's rules_version is not the one its path names.");
            var mechanics = ParseMechanics(root, abilities);
            var inventory = ParseInventoryBlock(root);
            return new IslandRules
            {
                RulesId = rulesId, RulesVersion = rulesVersion, DisplayName = displayName, Description = description, Status = status, MagicWord = magicWord,
                Sha256 = CanonicalJson.Sha256Hex(bytes), Abilities = abilities,
                Traits = mechanics.Traits, Cost = mechanics.Cost, Modes = mechanics.Modes, Names = mechanics.Names, Knacks = mechanics.Knacks, Fractals = mechanics.Fractals,
                // P-Inventory's optional block (Kernel/Inventory/InventoryRules.cs), and P-Food's beside it (Kernel/CommandHostFoodLayout.cs).
                Inventory = inventory,
                Food = ParseFoodBlock(root, inventory),
            };
        }
    }

    private static IslandAbility ParseAbility(JsonElement item, string at)
    {
        Expect(item.ValueKind == JsonValueKind.Object, at + " must be an object.");
        Fields(item, at, AbilityFields, AbilityFields);
        var capability = TokenAt(item, "capability", at);
        var category = TokenAt(item, "category", at);
        var primitive = Str(item, "primitive", at);
        Expect(Primitives.TryGetValue(primitive, out var limits), at + ".primitive is not an engine primitive.");
        var displayName = Display(item, "display_name", at, 80, 1);
        var castBy = Set(item, "cast_by", at, new[] { "player", "companion" });
        var tier = Str(item, "tier", at);
        Expect(Tiers.Contains(tier), at + ".tier must be auto, auto_undo, preview_commit or keyed_yes.");
        var targets = Set(item, "targets", at, new[] { "self", "point" });
        var reach = Positive(item, "reach_m", at, 10);
        var radiusDefault = Positive(item, "area_radius_default_m", at, 10);
        var radiusMax = Positive(item, "area_radius_max_m", at, 10);
        var durationDefault = Positive(item, "duration_default_s", at, 600);
        var durationMax = Positive(item, "duration_max_s", at, 600);
        var maxActive = Int(item, "max_active", at);
        Expect(maxActive is >= 1 and <= 64, at + ".max_active is 1 to 64.");
        var parameters = item.GetProperty("params");
        Expect(parameters.ValueKind == JsonValueKind.Object && parameters.EnumerateObject().Count() <= 16, at + ".params is an object of at most 16 parameters.");
        var bounds = new SortedDictionary<string, AbilityParam>(StringComparer.Ordinal);
        foreach (var parameter in parameters.EnumerateObject())
        {
            var where = $"{at}.params.{(ParamName.IsMatch(parameter.Name) ? parameter.Name : "?")}";
            Expect(ParamName.IsMatch(parameter.Name) && !ReservedParams.Contains(parameter.Name), at + ".params names are plain lowercase names, never identity or authority names.");
            Expect(parameter.Value.ValueKind == JsonValueKind.Object, where + " must be an object.");
            Fields(parameter.Value, where, new[] { "min", "max", "default" }, new[] { "min", "max", "default" });
            var min = Number(parameter.Value, "min", where);
            var max = Number(parameter.Value, "max", where);
            var fallback = Number(parameter.Value, "default", where);
            foreach (var value in new[] { min, max, fallback }) Expect(Math.Abs(value) <= 1000000, where + " values are within one million.");
            // The validator: min at most max, and the default inside them.
            Expect(min <= max, where + ".min is above its max.");
            Expect(min <= fallback && fallback <= max, where + ".default is outside its min and max.");
            bounds[parameter.Name] = new AbilityParam(min, max, fallback);
        }
        // The primitive's outer limits (the schema's if/then): its category, only its params, each inside its range, and its maxima.
        Expect(category == limits!.Category, $"{at}.category must be {limits.Category} for {primitive}.");
        foreach (var name in limits.Params.Keys) Expect(bounds.ContainsKey(name), $"{at}.params needs {name} for {primitive}.");
        foreach (var (name, value) in bounds)
        {
            Expect(limits.Params.TryGetValue(name, out var range), $"{at}.params may hold only {string.Join(" and ", limits.Params.Keys)} for {primitive}.");
            foreach (var number in new[] { value.Min, value.Max, value.Default })
                Expect(number >= range.Min && number <= range.Max, $"{at}.params.{name} must stay within {Text(range.Min)} and {Text(range.Max)} for {primitive}.");
        }
        Expect(reach <= limits.ReachM, $"{at}.reach_m is at most {Text(limits.ReachM)} for {primitive}.");
        Expect(radiusDefault <= limits.RadiusM && radiusMax <= limits.RadiusM, $"{at} radii are at most {Text(limits.RadiusM)} m for {primitive}.");
        Expect(durationDefault <= limits.DurationS && durationMax <= limits.DurationS, $"{at} durations are at most {Text(limits.DurationS)} s for {primitive}.");
        Expect(maxActive <= limits.MaxActive, $"{at}.max_active is at most {limits.MaxActive} for {primitive}.");
        // The validator: each default at most its maximum.
        Expect(radiusDefault <= radiusMax, at + ".area_radius_default_m is above area_radius_max_m.");
        Expect(durationDefault <= durationMax, at + ".duration_default_s is above duration_max_s.");
        // An older pack's Bloom (growth.seed) is read as Grow (decision 52: Bloom became Grow; storybook_wild v4 stays byte-exact).
        var legacyBloom = primitive == "growth.seed";
        return new IslandAbility
        {
            Capability = legacyBloom ? LegacyBloomAs : capability, Category = category, Primitive = primitive, DisplayName = legacyBloom ? "Grow" : displayName,
            CastBy = castBy, Tier = tier, Targets = targets,
            ReachM = reach, Params = bounds, AreaRadiusDefaultM = radiusDefault, AreaRadiusMaxM = radiusMax, DurationDefaultS = durationDefault,
            DurationMaxS = durationMax, MaxActive = maxActive,
        };
    }

    // ---- the schema's rules ----

    private static void Fields(JsonElement element, string at, string[] allowed, string[] required)
    {
        foreach (var property in element.EnumerateObject())
            Expect(allowed.Contains(property.Name), $"{at} has a field the contract does not define.");
        foreach (var name in required) Expect(element.TryGetProperty(name, out _), $"{at} needs '{name}'.");
    }

    private static string Str(JsonElement element, string name, string at)
    {
        var value = element.GetProperty(name);
        Expect(value.ValueKind == JsonValueKind.String, $"{at}.{name} must be a string.");
        return value.GetString()!;
    }

    private static string TokenAt(JsonElement element, string name, string at)
    {
        var value = Str(element, name, at);
        Expect(Token.IsMatch(value), $"{at}.{name} must be a lowercase token.");
        return value;
    }

    /// <summary>display_text (contracts/common.schema.json): no hidden characters, emoji markers only in place, and a length in code points.</summary>
    private static string Display(JsonElement element, string name, string at, int maxLength, int minLength)
    {
        var value = Str(element, name, at);
        var length = KernelText.CodePoints(value).Length;
        Expect(length >= minLength && length <= maxLength && RoomData.IsSafeText(value) && !value.EnumerateRunes().Any(r => r.Value is >= 0xE0000 and <= 0xEFFFF),
            $"{at}.{name} must be {minLength} to {maxLength} characters of display text without control, bidirectional or invisible characters or misplaced emoji markers.");
        return value;
    }

    private static int Int(JsonElement element, string name, string at)
    {
        var value = element.GetProperty(name);
        Expect(value.ValueKind == JsonValueKind.Number && value.GetRawText().All(c => char.IsAsciiDigit(c) || c == '-') && value.TryGetInt32(out _), $"{at}.{name} must be an integer.");
        return value.GetInt32();
    }

    private static double Number(JsonElement element, string name, string at)
    {
        var value = element.GetProperty(name);
        Expect(value.ValueKind == JsonValueKind.Number, $"{at}.{name} must be a number.");
        try { return CanonicalJson.ReadNumber(value); }
        catch (CanonicalJsonException) { throw new IslandRulesException($"{at}.{name} must be a finite number."); }
    }

    /// <summary>A number more than 0 and at most max.</summary>
    private static double Positive(JsonElement element, string name, string at, double max)
    {
        var value = Number(element, name, at);
        Expect(value > 0 && value <= max, $"{at}.{name} must be more than 0 and at most {Text(max)}.");
        return value;
    }

    /// <summary>A non-empty array of distinct values from allowed.</summary>
    private static IReadOnlySet<string> Set(JsonElement element, string name, string at, string[] allowed)
    {
        var value = element.GetProperty(name);
        Expect(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() is >= 1 && value.GetArrayLength() <= allowed.Length, $"{at}.{name} lists 1 to {allowed.Length} values.");
        var items = value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : "").ToList();
        Expect(items.All(allowed.Contains) && items.Distinct().Count() == items.Count, $"{at}.{name} lists distinct values from {string.Join(", ", allowed)}.");
        return new HashSet<string>(items, StringComparer.Ordinal);
    }

    private static string Text(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new IslandRulesException(message);
    }
}
