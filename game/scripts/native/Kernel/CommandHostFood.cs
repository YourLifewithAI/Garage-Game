using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GarageGame.Native.Room;
using GarageGame.Native.Sandbox;

namespace GarageGame.Native.Kernel;

/// <summary>
/// What E would pick here (PlayerCanPick, in the camps' PlayerCanRest style), or why not: Ok with the prompt's words ("Pick an apple"),
/// or the reason (the pockets' refusal when they are full); the plant (item.pickup's target), the kind, and where the food is (the
/// prompt stands there). No source and no words: no food is faced within reach, so E does something else.
/// </summary>
public sealed record FoodOutcome(bool Ok, string Words, string? Source = null, string? Kind = null, Vector3? At = null);

/// <summary>
/// Food (P-Food; docs/game/MECHANICS.md decisions 92, 98, 101, 108, 111 and 113; D1 in docs/runs/RUN-2-PLAYTEST-ROUND-2.md):
/// <list type="bullet">
/// <item><b>Where.</b> The rules pack's food block names what each species bears (storybook_wild v5: windfall apples at the trunk's foot
/// of one broadleaf in three, two to four each, and one or two in the canopy reached with a hop; a cluster of berries at the player's
/// height on one shrub in four and one heather clump in four). Which plants bear, and where, is FoodLayout's: the plant's id alone, the
/// same on every load, no generator or manifest change. What was picked is the island's nature (NatureState.Food: the spots picked and
/// when the last was, absent while the plant bears everything).</item>
/// <item><b>Picking</b> (decisions 98 and 113): item.pickup naming the plant, the player's alone (a player-only op: the companion is
/// refused permission_denied before anything is looked up, and nothing on its surface points food out). One unit, the spot faced within
/// reach (a canopy apple with a hop: while the body is in the air its reach rises by its hop), into the pockets under their caps (full:
/// occupied, the pockets' own words, and nothing is picked). The pockets gain first, then the island's nature, one durable room
/// revision, as a Fractal's pickup; a refused revision puts the pockets back. E's reader is PlayerCanPick, its key PlayerPick.</item>
/// <item><b>Eating</b> is inventory.use (CommandHostInventory): heal_quarters at once with no unharmed wait (decision 111), the unit kept
/// at full hearts, nothing healed in Creative; every unit eaten makes its sound (decision 108, Creative too): FoodSound, which the look
/// fills, and the receipt says it played (data.sound "eat").</item>
/// <item><b>Growing back</b> by the island clock (x_clock's day scale): everything picked from a plant is back once the pack's regrow_h
/// island hours (24 in v5) have passed since its last pick, so a day away restocks the island. A Grow cast that grows a plant ripens it at
/// once (CastGrow), fire burns its food with it (BurnPlants: the record goes when the plant burns down), so a burnt plant bears again when
/// it is replanted (CastReplant). Picks are never undone.</item>
/// <item><b>The look</b> is Room/FoodView's, under the plant copies' view: the kernel hands it the food near its focus (FoodSpotsNear) and
/// tells it when something changed (ApplyFood).</item>
/// </list>
/// </summary>
public partial class CommandHost
{
    public const string NoFoodHere = "This island's plants bear no food.";
    public const string NoFoodOnIt = "There is no food on that plant.";
    public const string FoodHopForIt = "That is out of reach: hop for it.";
    public const string FoodInTheWay = "That is out of reach: something is in the way.";
    public const string NoFoodInReach = "There is no food in reach to pick.";
    public const string FoodTooMany = "The island cannot keep track of more picked plants today: eat what you have first.";
    /// <summary>The sound an eaten unit makes, as its receipt names it (contracts: data.inventory.sound).</summary>
    public const string FoodSoundEat = "eat";
    /// <summary>A plant rooted this far from the feet is looked at for E (the food's spots lie within it, a grown tree's too).</summary>
    public const float FoodNearM = 0.75f;

    /// <summary>A plant copy's id, as item.pickup names a food source (contracts: common plant_id).</summary>
    internal static readonly Regex FoodSourceId = new(@"\Aplant:[a-z][a-z0-9_]{0,31}-(0|[1-9][0-9]{0,7})\z", RegexOptions.Compiled);

    /// <summary>
    /// A unit was eaten (decision 108): the kind's id, in every mode (Creative heals nothing but keeps the sound). The look plays it;
    /// the game has no audio path for effects yet (docs/magic/README.md), so this is the named hook that fills later.
    /// </summary>
    public event Action<string>? FoodSound;
    /// <summary>How many units were eaten with their sound this session (the hook fired), for tests and the record.</summary>
    public int FoodSoundsPlayed { get; private set; }

    /// <summary>The pack's food block, while the pack has the inventory it picks into; null: no plant bears food.</summary>
    public FoodRules? FoodRulesNow => InventoryRules != null ? Rules?.Food : null;

    /// <summary>Every plant of the island that bears food (the layout, not what is left on it), for the HUD's map and tests.</summary>
    public IEnumerable<FoodPlant> FoodPlants => FoodRulesNow is { } rules && Room?.Copies is { } copies ? FoodLayout.All(copies, rules) : Array.Empty<FoodPlant>();

    /// <summary>The spots of a plant that bear food now, by their index in its layout (none for a plant bearing none, or burnt down).</summary>
    public IReadOnlyList<int> FoodLeft(string plant)
    {
        if (FoodRulesNow is not { } rules || Room?.Copies is not { } copies || !copies.TryResolve(plant, out var set, out var row) || ShownFactor(plant) <= 0 ||
            FoodLayout.Of(set, row, rules) is not { } food) return Array.Empty<int>();
        var picked = PickedNow(plant);
        return Enumerable.Range(0, food.Spots.Count).Where(i => (picked & (1 << i)) == 0).ToList();
    }

    // ---- what is left, by the island clock ----

    /// <summary>The spots picked from a plant now (bits): its record's, unless everything picked has grown back since its last pick.</summary>
    private int PickedNow(string plant) => Nature.Food.TryGetValue(plant, out var record) && !GrownBack(record) ? record.Picked : 0;

    /// <summary>Whether everything picked from a plant has grown back: regrow_h island hours since its last pick (the clock's day scale).</summary>
    private bool GrownBack(NatureFood record) =>
        FoodRulesNow is not { } rules || Math.Max(0, SownNowS() - record.PickedS) / 3600.0 * Math.Max(1, GrowDayScale()) >= rules.RegrowH - 1e-9;

    /// <summary>The size a plant is drawn at: 1 natural, grown above 1, a seedling below, 0 burnt down.</summary>
    private float ShownFactor(string plant) => Nature.Plants.TryGetValue(plant, out var record) ? record.Shown : 1;

    private static string GrowsBackWords(FoodRules rules) =>
        "Everything on it has been picked: it grows back within " + (Math.Abs(rules.RegrowH - 24) < 1e-9 ? "a day." : $"{rules.RegrowH:0.#} hours.");

    // ---- picking: item.pickup naming a plant ----

    /// <summary>item.pickup: a plant names food to pick (PickFood), anything else a Fractal (CommandHostFractals).</summary>
    private JsonObject PickUp(JsonElement args, string principal, string actionId, string fingerprint, bool preview) =>
        Str(args, "target") is { } target && FoodSourceId.IsMatch(target) ? PickFood(args, principal, actionId, fingerprint, preview)
            : PickUpFractal(args, principal, actionId, fingerprint, preview);

    /// <summary>
    /// item.pickup naming a plant: one unit of its food into the pockets. Refused, cheapest first: a pack whose plants bear nothing
    /// (unsupported_capability); a plant not in this room or burnt down (target_not_found, as any id); one bearing no food (target_not_found);
    /// everything on it picked (not_ready, retryable: it grows back); none of what is left within reach (out_of_bounds, retryable: come
    /// closer, or hop for a canopy apple) or with a clear way to it; the pockets past a cap (occupied, the pockets' words: nothing is
    /// picked). The spot taken is the one most faced, then the nearest. Committed as one durable room revision (the plant's record in the
    /// island's nature), the pockets saved first; no data: the plant is in affected, as a Fractal's pickup names its Fractal.
    /// </summary>
    private JsonObject PickFood(JsonElement args, string principal, string actionId, string fingerprint, bool preview)
    {
        if (principal != PlayerPrincipal) throw new Refusal("permission_denied", "Only the player can do that, directly in the game.", "$.op");
        RequireChange(principal);
        var target = Str(args, "target")!;
        if (FoodRulesNow is not { } rules || InventoryRules is not { } inventory) throw new Refusal("unsupported_capability", NoFoodHere, "$.args.target");
        if (IslandHeld) throw new Refusal("not_ready", TeamNotice);
        if (Room.Copies is not { } copies || !copies.TryResolve(target, out var set, out var row) || ShownFactor(target) <= 0)
            throw new Refusal("target_not_found", "That is not in this room.", "$.args.target");
        var plant = FoodLayout.Of(set, row, rules) ?? throw new Refusal("target_not_found", NoFoodOnIt, "$.args.target");
        var kind = inventory.Kind(plant.Source.Kind)!;
        if (Player == null || !IsInstanceValid(Player) || !Player.IsInsideTree()) throw new Refusal("not_ready", "The player is not in the room.", retryable: true);
        var picked = PickedNow(plant.Id);
        var factor = ShownFactor(plant.Id);
        var left = Enumerable.Range(0, plant.Spots.Count).Where(i => (picked & (1 << i)) == 0).ToList();
        if (left.Count == 0) throw new Refusal("not_ready", GrowsBackWords(rules), "$.args.target", retryable: true);
        var reached = left.Where(i => FoodWithinReach(Player, plant.Box(i, factor))).ToList();
        if (reached.Count == 0)
            throw left.Any(i => SandboxRules.WithinReach(Player, Lowered(plant.Box(i, factor), Player.JumpApexNowM)))
                ? new Refusal("out_of_bounds", FoodHopForIt, "$.args.target", retryable: true) : OutOfReach("$.args.target");
        var spot = reached.OrderBy(i => FoodAngle(Player, plant.Box(i, factor))).ThenBy(i => Player.GlobalPosition.DistanceSquaredTo(plant.Box(i, factor).GetCenter()))
            .Cast<int?>().FirstOrDefault(i => Physics().ReachClear(Player, plant.Box(i!.Value, factor), new Rid()))
            ?? throw new Refusal("out_of_bounds", FoodInTheWay, "$.args.target", retryable: true);
        var pockets = PocketContents;
        var next = pockets.WithUnits(kind.Id, pockets.Units(kind.Id) + 1);
        CheckFits(next, inventory, inventory.Pockets, pockets: true, kind);
        if (_natureUnavailable) throw new Refusal("not_ready", NatureNotice, retryable: true);
        var candidate = Nature.Copy();
        // What has grown back since needs no record: only plants picked within the last regrow_h hours are kept.
        foreach (var gone in candidate.Food.Values.Where(GrownBack).Select(f => f.Id).ToList()) candidate.Food.Remove(gone);
        if (!candidate.Food.ContainsKey(plant.Id) && candidate.Food.Count >= FoodLaws.MaxFoodPlants) throw new Refusal("occupied", FoodTooMany, "$.args.target");
        if (preview) return Previewed("item.pickup", principal, actionId, target);
        candidate.Food[plant.Id] = new NatureFood { Id = plant.Id, Picked = picked | (1 << spot), PickedS = SownNowS() };
        // The pockets gain first, then the plant gives (one room revision): a revision refused puts the pockets back.
        SavePockets(next);
        try { return CommitNature(candidate, "item.pickup", principal, actionId, fingerprint, null, Array.Empty<string>(), new JsonObject { ["food"] = plant.Id }, undo: false); }
        catch (Refusal)
        {
            SavePockets(pockets, keepIfUnsaved: true);
            throw;
        }
    }

    /// <summary>
    /// Whether food is within the body's reach (SandboxRules.WithinReach); while the body is in the air (a hop), its reach rises by its
    /// hop, so a canopy apple a hop reaches is picked at any moment of the hop (decision 108: "fruit in the canopy reached with a hop").
    /// </summary>
    private static bool FoodWithinReach(SmallPlayerController body, Aabb box) =>
        SandboxRules.WithinReach(body, box) || (!body.IsOnFloor() && SandboxRules.WithinReach(body, Lowered(box, body.JumpApexNowM)));

    private static Aabb Lowered(Aabb box, float by) => new(box.Position - Vector3.Up * by, box.Size);

    /// <summary>The angle, in degrees, between the body's facing and the way to a box on the ground plane (0 over or under it).</summary>
    private static float FoodAngle(SmallPlayerController body, Aabb box)
    {
        var feet = body.GlobalPosition;
        var toward = new Vector3(box.GetCenter().X - feet.X, 0, box.GetCenter().Z - feet.Z);
        return toward.LengthSquared() < 1e-8f ? 0 : Mathf.RadToDeg(SandboxControls.Facing(body).AngleTo(toward.Normalized()));
    }

    // ---- E (decision 113: picked food goes to the pockets) ----

    /// <summary>
    /// What E would pick now, without picking it: the unit of food the player faces (within SandboxControls.AheadDegrees) within reach with
    /// a clear way to it, the most faced first, then the nearest. Ok: "Pick an apple" or "Pick berries" (the kind's words), the plant and
    /// where it is. When it would not fit the pockets: not Ok, the pockets' own words ("Your pockets are full: they hold 6 things."), the
    /// plant and where (E says so and picks nothing). Nothing faced: not Ok with no words and no plant, and E does something else.
    /// </summary>
    public FoodOutcome PlayerCanPick()
    {
        if (FoodRulesNow is not { } rules || InventoryRules is not { } inventory || Room?.Copies is not { } copies) return new(false, "");
        if (Player == null || !IsInstanceValid(Player) || !Player.IsInsideTree()) return new(false, "");
        var feet = Player.GlobalPosition;
        var candidates = new List<(FoodPlant Plant, Aabb Box, float Angle, float Gap)>();
        foreach (var plant in FoodLayout.Near(copies, rules, feet.X, feet.Z, FoodNearM))
        {
            var factor = ShownFactor(plant.Id);
            if (factor <= 0) continue;
            var picked = PickedNow(plant.Id);
            for (var i = 0; i < plant.Spots.Count; i++)
            {
                if ((picked & (1 << i)) != 0) continue;
                var box = plant.Box(i, factor);
                if (!FoodWithinReach(Player, box)) continue;
                var angle = FoodAngle(Player, box);
                if (angle <= SandboxControls.AheadDegrees) candidates.Add((plant, box, angle, feet.DistanceSquaredTo(box.GetCenter())));
            }
        }
        foreach (var (plant, box, _, _) in candidates.OrderBy(c => c.Angle).ThenBy(c => c.Gap).ThenBy(c => c.Plant.Id, StringComparer.Ordinal))
        {
            if (!Physics().ReachClear(Player, box, new Rid())) continue;
            var kind = inventory.Kind(plant.Source.Kind)!;
            var pockets = PocketContents;
            var at = box.GetCenter();
            if (FitsWords(pockets.WithUnits(kind.Id, pockets.Units(kind.Id) + 1), inventory, inventory.Pockets, pockets: true, kind) is { } full)
                return new(false, full, plant.Id, kind.Id, at);
            return new(true, "Pick " + FoodWords(kind), plant.Id, kind.Id, at);
        }
        return new(false, "");
    }

    /// <summary>E's pick: the food PlayerCanPick faces, as the player's item.pickup (refused, with the pockets' words, when they are full).</summary>
    public JsonObject PlayerPick(bool preview = false) => PlayerCanPick().Source is { } plant ? PlayerPickFrom(plant, preview)
        : Fail("item.pickup", PlayerPrincipal, null, null, new Refusal("target_not_found", NoFoodInReach, "$.args.target"));

    /// <summary>One unit of a plant's food into the pockets, as the player's item.pickup.</summary>
    public JsonObject PlayerPickFrom(string plant, bool preview = false) => PlayerCommand("item.pickup", new JsonObject { ["target"] = plant }, preview);

    /// <summary>A kind as E names one unit: "an apple", "berries" (a kind whose one-word and plural are one), from the pack's untrusted words.</summary>
    private static string FoodWords(InventoryKind kind)
    {
        var name = KernelJson.DisplayText(kind.Name, InventoryContents.MaxNameLength).ToLowerInvariant();
        if (kind.Plural == kind.Name) return name;
        return ("aeiou".Contains(name.Length > 0 ? name[0] : 'x') ? "an " : "a ") + name;
    }

    // ---- the hooks: Grow, fire and eating ----

    /// <summary>A Grow cast ripens the food of every plant it grew at once (CastGrow): their records go, so they bear everything again.</summary>
    private static void RipenFood(NatureState candidate, IEnumerable<string> grown)
    {
        foreach (var plant in grown) candidate.Food.Remove(plant);
    }

    /// <summary>A plant burnt down takes its food with it (BurnPlants): the record goes, so a replanted plant bears everything again.</summary>
    private void FoodBurnsWith(string plant) => Nature.Food.Remove(plant);

    /// <summary>
    /// A unit of a kind that heals was eaten (inventory.use, every mode; decision 108): the receipt says its sound played, and FoodSound
    /// tells whoever plays it. Returns the receipt's data.
    /// </summary>
    private JsonObject Eaten(string kind, JsonObject data)
    {
        data["sound"] = FoodSoundEat;
        FoodSoundsPlayed++;
        FoodSound?.Invoke(kind);
        return data;
    }

    // ---- the look ----

    private Func<Vector3, float, IReadOnlyList<FoodView.Spot>>? _foodSource;

    /// <summary>The food view under the plant copies' view (PlantCopiesView.Food) reads the island's food from here, again now.</summary>
    private void ApplyFood()
    {
        var view = GetParent()?.GetNodeOrNull<PlantCopiesView>(CopiesPath)?.Food;
        if (view == null || !IsInstanceValid(view)) return;
        view.Source = _foodSource ??= FoodSpotsNear;
        view.Changed();
    }

    /// <summary>
    /// The food still on its plants rooted within radius of a point (the view's focus), each its bottom centre on its plant as drawn now (a
    /// grown or replanted plant's moved with its size), its look by its kind (an apple, else berries) and its stalk's length up to the crown.
    /// </summary>
    internal IReadOnlyList<FoodView.Spot> FoodSpotsNear(Vector3 at, float radius)
    {
        var spots = new List<FoodView.Spot>();
        if (FoodRulesNow is not { } rules || Room?.Copies is not { } copies) return spots;
        foreach (var plant in FoodLayout.Near(copies, rules, at.X, at.Z, radius))
        {
            var factor = ShownFactor(plant.Id);
            if (factor <= 0) continue;
            var picked = PickedNow(plant.Id);
            var look = plant.Source.Kind == "apple" ? FoodView.Look.Apple : FoodView.Look.Berries;
            for (var i = 0; i < plant.Spots.Count; i++)
                if ((picked & (1 << i)) == 0) spots.Add(new FoodView.Spot(plant.At(i, factor), look, plant.Spots[i].HangM * factor));
        }
        return spots;
    }
}
