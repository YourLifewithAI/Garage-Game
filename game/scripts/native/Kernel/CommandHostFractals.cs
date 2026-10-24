using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Sandbox;

namespace GarageGame.Native.Kernel;

/// <summary>One Fractal of the island (the manifest's extensions.x_fractals): its id, where it rests (bottom centre), and its kind when typed.</summary>
public sealed record IslandFractal(string Id, Vector3 PositionM, string? Kind, int? ReachBand);

/// <summary>
/// Fractals (docs/game/MECHANICS.md section 6; contracts/README.md "The mechanics round"): the found things that grow the
/// body, placed by the generator in the manifest's extensions.x_fractals (packet M4) and read here from the room's pinned
/// manifest, fail-closed (a manifest whose block does not check has none). The player alone picks one up (item.pickup, a
/// player-only op): within reach and with a clear way to it, it leaves the island save into the team's count for this mode
/// (the vitals' fractals), as one durable room revision with its receipt. The island save keeps the ids picked up in the
/// host's nature block (x_play_nature's "fractals"), never an edit of the manifest; when the torch round's item kind lands
/// in room state, a picked Fractal can become an entity record of that kind. The Gubble may see, go to and look at one
/// (kind fractal in perception, in sight now; its map does not keep them yet), never gather it (NamesAFractal). Picking up
/// costs no pips and is the same in every mode; Creative's count is its own. At the camp the player spends them for a heart or
/// the next band (fractal.spend, SpendFractals).
/// </summary>
public partial class CommandHost
{
    /// <summary>A Fractal's size for sight and reach: a small box from its bottom centre (the look draws it within this).</summary>
    public const float FractalHalfWidthM = 0.02f;
    public const float FractalHeightM = 0.04f;
    public const int MaxFractals = 1000;
    /// <summary>Why the island has no Fractals although its manifest names some (its block did not check), for the player; empty otherwise.</summary>
    public string FractalsNotice { get; private set; } = "";
    private IReadOnlyList<IslandFractal>? _fractals;
    private bool _fractalsTyped;

    /// <summary>Every Fractal the manifest places, picked up or not, in id order.</summary>
    public IReadOnlyList<IslandFractal> AllFractals => _fractals ??= LoadFractals();

    /// <summary>Test seam: the island's Fractals in place of the manifest's (the play HUD's room places none), untyped.</summary>
    internal IReadOnlyList<IslandFractal> FractalsForTests
    {
        set
        {
            _fractals = value.OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
            _fractalsTyped = false;
        }
    }

    /// <summary>The Fractals still on the island (not picked up in this save).</summary>
    public IEnumerable<IslandFractal> Fractals => AllFractals.Where(f => !Nature.FractalsPicked.Contains(f.Id));

    /// <summary>The Fractals within the player's reach now, for the HUD's pickup hint.</summary>
    public IReadOnlyList<string> FractalsInReach()
    {
        if (Player == null || !IsInstanceValid(Player)) return Array.Empty<string>();
        return Fractals.Where(f => SandboxRules.WithinReach(Player, FractalBox(f).Grow(0.01f))).Select(f => f.Id).ToList();
    }

    public static Aabb FractalBox(IslandFractal fractal) =>
        new(fractal.PositionM - new Vector3(FractalHalfWidthM, 0, FractalHalfWidthM), new Vector3(FractalHalfWidthM * 2, FractalHeightM, FractalHalfWidthM * 2));

    public JsonObject PlayerPickUp(string fractal, bool preview = false) => PlayerCommand("item.pickup", new JsonObject { ["target"] = fractal }, preview);

    /// <summary>The manifest's x_fractals, read from the room's pinned manifest and checked as contracts/room-manifest.schema.json and validate.py do.</summary>
    private IReadOnlyList<IslandFractal> LoadFractals()
    {
        FractalsNotice = "";
        if (Room == null || string.IsNullOrEmpty(Room.Directory)) return Array.Empty<IslandFractal>();
        var path = Room.Directory + "/room.json";
        if (!Godot.FileAccess.FileExists(path)) return Array.Empty<IslandFractal>();
        var bytes = Godot.FileAccess.GetFileAsBytes(path);
        // The room as loaded: the manifest's own bytes, never a file that changed since.
        if (bytes.Length == 0 || CanonicalJson.Sha256Hex(bytes) != Room.ManifestSha256) return Refuse("its manifest changed since the room was loaded");
        try
        {
            using var document = CanonicalJson.Parse(bytes);
            if (!document.RootElement.TryGetProperty("extensions", out var extensions) || extensions.ValueKind != JsonValueKind.Object ||
                !extensions.TryGetProperty("x_fractals", out var block)) return Array.Empty<IslandFractal>();
            return ParseFractals(block, Room.Bounds, out _fractalsTyped) ?? Refuse("its Fractals do not check");
        }
        catch (CanonicalJsonException) { return Refuse("its manifest is not strict JSON"); }
    }

    private IReadOnlyList<IslandFractal> Refuse(string why)
    {
        FractalsNotice = $"This island's Fractals were not loaded: {why}.";
        GD.Print("COMMAND_HOST " + FractalsNotice);
        return Array.Empty<IslandFractal>();
    }

    /// <summary>x_fractals checked: typed and items only; each item an id of one spelling, listed once, inside the bounds, a kind only when typed; null for any problem.</summary>
    internal static IReadOnlyList<IslandFractal>? ParseFractals(JsonElement block, Aabb bounds, out bool typed)
    {
        typed = false;
        if (block.ValueKind != JsonValueKind.Object || block.EnumerateObject().Any(p => p.Name is not ("typed" or "items")) ||
            !block.TryGetProperty("typed", out var flag) || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !block.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > MaxFractals) return null;
        typed = flag.ValueKind == JsonValueKind.True;
        var list = new List<IslandFractal>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Any(p => p.Name is not ("id" or "position_m" or "kind" or "reach_band"))) return null;
            if (Str(item, "id") is not { } id || !FractalId.IsMatch(id) || !item.TryGetProperty("position_m", out var at) || at.ValueKind != JsonValueKind.Array ||
                at.GetArrayLength() != 3 || at.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.Number)) return null;
            var position = new Vector3((float)at[0].GetDouble(), (float)at[1].GetDouble(), (float)at[2].GetDouble());
            if (!position.IsFinite() || !bounds.Grow(0.001f).HasPoint(position)) return null;
            string? kind = null;
            if (item.TryGetProperty("kind", out var k))
            {
                kind = k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                if (!typed || kind is not ("heart" or "band")) return null;
            }
            else if (typed) return null;
            int? band = null;
            if (item.TryGetProperty("reach_band", out var reach))
            {
                if (!IsIntegerLiteral(reach) || reach.GetInt64() is < 1 or > 20) return null;
                band = reach.GetInt32();
            }
            list.Add(new IslandFractal(id, position, kind, band));
        }
        if (list.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != list.Count) return null;
        list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return list;
    }

    /// <summary>The Fractals still on the island as entity summaries (kind fractal): small, not movable, placed by the generator.</summary>
    private void AddFractals(List<(string Id, JsonObject Summary)> list)
    {
        var name = Rules?.Names?.Words.TryGetValue("fractals", out var word) == true ? Singular(word) : "Fractal";
        foreach (var fractal in Fractals)
        {
            var box = FractalBox(fractal);
            list.Add((fractal.Id, Summary(fractal.Id, "fractal", name, null, null, fractal.PositionM, box, Array.Empty<string>(), false, false, "procedural", 0)));
        }
    }

    /// <summary>The pack's word for Fractals, as one (untrusted display text: "Fractals" reads "Fractal").</summary>
    private static string Singular(string word) => word.Length > 1 && word.EndsWith('s') ? word[..^1] : word;

    /// <summary>
    /// item.pickup: the player picks a Fractal up. It must be on the island now (target_not_found otherwise, as any id), within
    /// the player's reach with a clear way to it (out_of_bounds, retryable: come closer). Committed as one durable room
    /// revision (the island save's nature block keeps it as picked up), then counted into this mode's vitals; a pace line.
    /// </summary>
    private JsonObject PickUpFractal(JsonElement args, string principal, string actionId, string fingerprint, bool preview)
    {
        if (principal != PlayerPrincipal) throw new Refusal("permission_denied", FractalRefusal, "$.args.target");
        RequireChange(principal);
        var target = Str(args, "target")!;
        var fractal = Fractals.FirstOrDefault(f => f.Id == target) ?? throw new Refusal("target_not_found", "That is not in this room.", "$.args.target");
        if (Player == null || !IsInstanceValid(Player)) throw new Refusal("not_ready", "The player is not in the room.", retryable: true);
        var box = FractalBox(fractal);
        if (!SandboxRules.WithinReach(Player, box.Grow(0.01f))) throw OutOfReach("$.args.target");
        if (!Physics().ReachClear(Player, box, new Rid()))
            throw new Refusal("out_of_bounds", "That is out of reach: something is in the way.", "$.args.target", retryable: true);
        if (_playerVitalsUnavailable) throw new Refusal("not_ready", VitalsNotice, retryable: true);
        if (preview) return Previewed("item.pickup", principal, actionId, target);
        // Counted first (the review's minor): the vitals store keeps the count before the island lets the Fractal go, so a count
        // that cannot be saved leaves it where it is, and an island save that is refused takes the count back.
        var counted = Vitals.Fractals;
        var found = Vitals.With(fractals: counted.Picked(_fractalsTyped ? fractal.Kind : null));
        if (TrySaveVitals(_playerModes.WithCurrent(found)) is not { } saved)
            throw new Refusal("not_ready", "The Fractal could not be counted, so it stays where it is. Try again in a moment.", retryable: true);
        _playerModes = saved;
        _vitalsDirty = false;
        var candidate = Nature.Copy();
        candidate.FractalsPicked.Add(fractal.Id);
        // The receipt names the Fractal in its data (the authority knows no fractal: ids); the result carries it in affected.
        JsonObject result;
        try { result = CommitNature(candidate, "item.pickup", principal, actionId, fingerprint, null, Array.Empty<string>(), new JsonObject { ["fractal"] = fractal.Id }, undo: false); }
        catch (Refusal)
        {
            _playerModes = _playerModes.WithCurrent(Vitals.With(fractals: counted));
            _vitalsDirty = true;
            FlushPlayerVitals();
            throw;
        }
        NotePace("fractal", found.Fractals.Found);
        return result;
    }

    // ---- fractal.spend: growing the body at the camp ----

    public const string FractalsAtCamp = "Fractals are spent at the camp: go back to where you started and try again.";

    /// <summary>The HUD's or the menu's spend: fractal.spend of "heart" or "band", as the player's command.</summary>
    public JsonObject PlayerSpendFractals(string grow, bool preview = false) => PlayerCommand("fractal.spend", new JsonObject { ["grow"] = grow }, preview);

    /// <summary>
    /// fractal.spend (the second contract round; the founder, 15 October: the player's to do at will, decision 41): the pack's
    /// fractals.per_heart unspent Fractals for one more heart, its per_band for the next band of the bar, from this mode's own
    /// count (as the pickup), of that kind under the pack's typed flag. The refusals, the cheapest to learn first: a pack with no
    /// fractals block (unsupported_capability); at the cap, the pack's cap_hearts or its last band, or an island whose hearts or
    /// bar do not grow at all (request_invalid at $.args.grow); too few in hand (not_ready, retryable: how many it takes and how
    /// many the player holds); away from the camp (not_ready, retryable: the spawn, within the same CampRadiusM as knack.set).
    /// The new heart arrives full and the new band's metres join the bar (provisional). Kept in the vitals store, never the room:
    /// a transient receipt with no data, saved before it answers (a save that fails changes nothing); a pace line in the journal.
    /// </summary>
    private JsonObject SpendFractals(JsonElement args, string principal, string actionId, string fingerprint, bool preview)
    {
        if (principal != PlayerPrincipal) throw new Refusal("permission_denied", "Only the player can do that, directly in the game.", "$.op");
        var grow = Str(args, "grow")!;
        var heart = grow == "heart";
        if (_vitalsRules.FractalRules is not { } fractals) throw new Refusal("unsupported_capability", $"This island has no {NameOf("fractals")} to spend.", "$.op");
        if (_playerVitalsUnavailable) throw new Refusal("not_ready", VitalsNotice, retryable: true);
        var vitals = Vitals;
        if (heart ? _vitalsRules.Hearts == null : _vitalsRules.Stamina == null)
            throw new Refusal("request_invalid", heart ? "This island's hearts do not grow." : "This island's stamina bar does not grow.", "$.args.grow");
        if (heart && _vitalsRules.MaxHearts(vitals) >= _vitalsRules.Hearts!.CapHearts)
            throw new Refusal("request_invalid", $"You have every heart this island allows ({_vitalsRules.Hearts.CapHearts}).", "$.args.grow");
        if (!heart && _vitalsRules.OpenBands(vitals) >= _vitalsRules.Stamina!.Bands)
            throw new Refusal("request_invalid", $"Your stamina bar has every band this island allows ({_vitalsRules.Stamina.Bands}).", "$.args.grow");
        var price = heart ? fractals.PerHeart : fractals.PerBand;
        var held = vitals.Fractals.InHand(grow, fractals.Typed);
        if (held < price)
        {
            var kind = fractals.Typed ? (heart ? "heart " : "band ") + NameOf("fractals") : NameOf("fractals");
            var what = heart ? "A new heart" : $"The next band of your {NameOf("stamina").ToLowerInvariant()}";
            throw new Refusal("not_ready", $"{what} needs {price} {kind}. You have {held}: find {price - held} more!", retryable: true);
        }
        if (!PlayerAtCamp()) throw new Refusal("not_ready", FractalsAtCamp, retryable: true);
        if (preview) return Previewed("fractal.spend", principal, actionId);
        var grown = vitals.With(fractals: vitals.Fractals.Spent(grow, price));
        var maxHealth = _vitalsRules.MaxHealthOf(grown);
        var maxStamina = _vitalsRules.MaxStaminaOf(grown);
        // The new heart arrives full (four more quarters); the new band's metres join the bar. Nothing else changes.
        var health = heart ? (float)((_vitalsRules.Quarters(vitals) + 4) * _vitalsRules.HealthPerQuarter) : vitals.Health;
        var stamina = heart ? vitals.Stamina : vitals.Stamina + Math.Max(0, maxStamina - vitals.MaxStamina);
        grown = grown.With(health: health, maxHealth: maxHealth, stamina: stamina, maxStamina: maxStamina);
        if (TrySaveVitals(_playerModes.WithCurrent(grown)) is not { } saved)
            throw new Refusal("not_ready", "The Fractals could not be spent: saving failed, so nothing changed. Try again in a moment.", retryable: true);
        _playerModes = saved;
        _vitalsDirty = false;
        if (heart) NotePace("heart", _vitalsRules.MaxHearts(grown));
        else NotePace("band", _vitalsRules.OpenBands(grown));
        return Transient("fractal.spend", principal, actionId, fingerprint, new JsonArray(), null);
    }
}
