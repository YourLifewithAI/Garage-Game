using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GarageGame.Native.Kernel;

/// <summary>
/// The island's nature laws in one place (docs/magic/README.md "The ground layer", "Weather patterns" and "Fire"). Every
/// number here is provisional: a first guess for playtests, not a decision. What a pack grants (reach, radius, duration,
/// intensity, who may cast) comes from the island's rules instead, and the engine's caps sit on NatureState.
/// </summary>
public static class NatureLaws
{
    // ---- the clock ----

    /// <summary>One simulation step, in seconds of play. The host runs whole steps and keeps the remainder.</summary>
    public const float StepS = 0.5f;
    /// <summary>At most this many steps run in one physics tick; time beyond them is dropped (no catch-up after a stall or a reload).</summary>
    public const int MaxCatchUpSteps = 2;
    /// <summary>
    /// Cells farther than this, horizontally, from both avatars rest until an avatar comes near, then catch up on the time
    /// they rested (RestS) in steps of StepS, as if they had been simulated all along (the founder, 13 October: time away).
    /// Fire never rests: burning cells and the cells inside a fire's circle always run.
    /// </summary>
    public const float ActiveRadiusM = 12f;
    /// <summary>
    /// At most this many resting cell steps are replayed per simulation step, in grid order, so coming back to a large
    /// field spreads its catch-up over a few frames instead of one spike.
    /// </summary>
    public const int CatchUpCellStepsPerStep = 8192;
    /// <summary>Cells whose light is measured per physics tick (up to four rays each), in turn.</summary>
    public const int LightProbesPerTick = 24;
    /// <summary>The light a cell assumes when the room has no look to measure it.</summary>
    public const float DefaultLight = 0.6f;

    // ---- weather: one six-minute sequence, the same everywhere on the island ----

    public const double WeatherCycleS = 360;
    /// <summary>The cycle starts dry for this long, then rain ramps up over WeatherRampS.</summary>
    public const double WeatherDryS = 120;
    public const double WeatherRampS = 45;
    /// <summary>Rain is full until this point of the cycle, then ramps down over WeatherRampS.</summary>
    public const double WeatherWetUntilS = 225;
    /// <summary>The wind turns one radian in this many seconds.</summary>
    public const double WindTurnS = 150;
    public const float WindCalmMps = 0.12f;
    public const float WindRainMps = 0.22f;
    /// <summary>Above this much rain the pattern reads "rain"; above WeatherShowers01, "showers"; otherwise "fair".</summary>
    public const float WeatherRain01 = 0.65f;
    public const float WeatherShowers01 = 0.02f;

    // ---- the ground ----

    public const float FreshMoisture = 0.35f;
    /// <summary>Fresh ground is this much wetter in full rain.</summary>
    public const float RainMoisture = 0.35f;
    public const float RainWettingPerS = 0.18f;
    public const float LightDryingPerS = 0.009f;
    public const float GrassFuel = 0.7f;
    public const float WoodFuel = 1f;
    public const float LightFuel = 0.8f;
    /// <summary>Flowers add at least this much fuel to their cell.</summary>
    public const float FlowerFuel = 0.35f;

    // ---- flowers (Bloom's, retired by decision 52: an older save's are dropped on load, NatureState.DropFlowers) ----

    public const float GrowthPerS = 0.035f;
    public const float GrowthMinMoisture = 0.12f;
    public const float GrowthMinLight = 0.12f;
    public const float FadeWhileAlivePerS = 0.006f;
    public const float FadeAfterLifePerS = 0.025f;

    // ---- Fire ----

    /// <summary>Ignition takes hold within this radius of the centre; spread does the rest.</summary>
    public const float IgnitionRadiusM = 0.13f;
    public const float IgnitionMaxS = 10f;
    public const float SpreadBurnMaxS = 8f;
    /// <summary>At or above this moisture nothing ignites; at or above SpreadMaxMoisture fire does not spread into a cell.</summary>
    public const float IgnitionMaxMoisture = 0.8f;
    public const float SpreadMaxMoisture = 0.7f;
    public const float SpreadMinFuel = 0.03f;
    /// <summary>A cell keeps burning only while its ground stays within this height of where it caught.</summary>
    public const float BurnHeightToleranceM = 0.04f;
    /// <summary>Fire climbs or drops at most this much between neighbouring cells.</summary>
    public const float SpreadStepM = 0.07f;
    /// <summary>Downwind, one neighbour in this many catches per step; upwind and across, one in SpreadGateOther.</summary>
    public const int SpreadGateDownwind = 3;
    public const int SpreadGateOther = 7;
    /// <summary>Rain at or above this stops spread; each unit of rain burns a cell out this many times faster.</summary>
    public const float RainStopsSpread01 = 0.8f;
    public const float RainQuench = 5f;
    /// <summary>Fuel burned and ash laid per second, per unit of the pack's intensity (0.6 gives 0.08 and 0.2).</summary>
    public const float FuelBurnPerIntensityS = 0.08f / 0.6f;
    public const float AshPerIntensityS = 0.2f / 0.6f;
    public const float BurntOutFuel = 0.005f;
    /// <summary>An object near burning ground burns this fast per unit of intensity, slowed by rain (0.6 gives 0.22).</summary>
    public const float ObjectBurnPerIntensityS = 0.22f / 0.6f;
    /// <summary>Burning ground this close to the object's bounds burns it.</summary>
    public const float ObjectCatchM = 0.08f;
    /// <summary>Fireproof bounds (locks, Creative's creations) shelter the ground within this margin.</summary>
    public const float ShelterMarginM = 0.04f;
    /// <summary>The player is hurt this much per step within PlayerBurnM of burning ground (in modes with consequences), whoever lit it.</summary>
    public const float PlayerBurnDamage = 6f;
    public const float PlayerBurnM = 0.12f;

    // ---- the guardrails on the Gubble's fire (the founder, 13 October: consequences with guardrails, not holds) ----

    /// <summary>After the Gubble's fire goes out (burnt out, snuffed or taken back), it waits this long before lighting another. The player never waits for it.</summary>
    public const float CompanionFireCooldownS = 20f;
    /// <summary>After an injury recovery, fire cannot hurt the player for this long.</summary>
    public const float RecoveryFireGraceS = 5f;
    /// <summary>The ground this close to where the player recovers is put out at once and cannot burn during the grace.</summary>
    public const float RecoverySafeM = 0.3f;

    // ---- regrowth ----

    public const float AshRegrowMoisture = 0.2f;
    public const float AshFadePerS = 0.006f;
    public const float FuelRegrowPerS = 0.002f;
}

/// <summary>One cell of the ground field: saved simulation data. The meshes are a disposable view of these cells.</summary>
public sealed class NatureCell
{
    public int X { get; set; }
    public int Z { get; set; }
    public float Y { get; set; }
    /// <summary>The entity the cell lies on (a shell part, an object, a creation).</summary>
    public string Surface { get; set; } = "";
    public string Material { get; set; } = "soil";
    public bool Fertile { get; set; }
    public float Moisture { get; set; } = NatureLaws.FreshMoisture;
    public float Growth { get; set; }
    public float LifeS { get; set; }
    public float Fuel { get; set; }
    public float BurningS { get; set; }
    public float Ash { get; set; }
    public float Light { get; set; } = NatureLaws.DefaultLight;
    public bool Exposed { get; set; } = true;
    /// <summary>Seconds of simulation this cell has not had yet because it rested far from both avatars (caught up on return).</summary>
    public double RestS { get; set; }
    public string Key => NatureState.Key(X, Z);
    public Vector3 Position => new((X + 0.5f) * NatureState.CellM, Y, (Z + 0.5f) * NatureState.CellM);
    public NatureCell Copy() => (NatureCell)MemberwiseClone();
}

/// <summary>
/// What fire has done to one object or creation. A creation keeps its scorch and its repair quote (one unit of material
/// per original part, from the hash-pinned design). A stock object that burned down keeps its manifest id and the pose it
/// stood at when it burned, which is all a later rebuild needs; the manifest itself is never edited.
/// </summary>
public sealed class NatureObject
{
    public string Id { get; set; } = "";
    public bool Creation { get; set; }
    /// <summary>0 to 1. A creation with any burn is scorched; a stock object at 1 has burned down.</summary>
    public float Burn { get; set; }
    public string SourceHash { get; set; } = "";
    public Dictionary<string, int> RepairCost { get; set; } = new(StringComparer.Ordinal);
    /// <summary>A stock object's pose when it burned down (the authority's, else the manifest's); null until then.</summary>
    public Vector3? PositionM { get; set; }
    public Quaternion? Rotation { get; set; }
    /// <summary>
    /// Gone from the room: a stock object burned down, or gathered into the wallet (item.gather, Gathered). Either way it is
    /// hidden, has no collision, is refused like an unknown id and is a removed entity in room state, at the pose it left.
    /// </summary>
    public bool BurnedDown => !Creation && Burn >= 1;
    /// <summary>A stock object the player gathered into the wallet (its Burn is 1: gone); never set for one fire took.</summary>
    public bool Gathered { get; set; }
    public NatureObject Copy() => new()
    {
        Id = Id, Creation = Creation, Burn = Burn, SourceHash = SourceHash, RepairCost = new(RepairCost, StringComparer.Ordinal),
        PositionM = PositionM, Rotation = Rotation, Gathered = Gathered,
    };
}

/// <summary>
/// What Grow and fire did to one of the island's plant copies (plant:&lt;set&gt;-&lt;row&gt;, contracts/README.md "Plant copies"): its size
/// as a factor of its natural size (1 natural, up to its cap: PlantLaws.CapFactor), and how far it has burned. A plant at 1 burn
/// has burned down: gone with its drawing and its collision, for good unless it is replanted (decision 67); the simulation never
/// regrows it. A replanted one (decision 105) is a seedling again: SownS is when it was sown on the island clock, and its factor is
/// the seedling's phase (GrowKindLaws.SeedlingFactor: half its natural size, natural after the last phase), derived from the clock
/// (CommandHost.RefreshSeedlings), never saved. Room state, never the manifest, which is pinned.
/// </summary>
public sealed class NaturePlant
{
    public string Id { get; set; } = "";
    public float Factor { get; set; } = 1;
    /// <summary>0 to 1; at 1 it has burned down.</summary>
    public float Burn { get; set; }
    /// <summary>A replanted seedling: when it was sown, in whole seconds of the island clock's UTC (null: not replanted, or grown on since).</summary>
    public long? SownS { get; set; }
    public bool Burnt => Burn >= 1;
    public bool Grown => !Burnt && Factor > 1;
    /// <summary>Still growing through its phases (below its natural size): Grow does not grow it yet.</summary>
    public bool Seedling => SownS != null && !Burnt && Factor < 1;
    /// <summary>The factor the island draws and collides it at: 0 when it burned down.</summary>
    public float Shown => Burnt ? 0 : Factor;
    public NaturePlant Copy() => (NaturePlant)MemberwiseClone();
}

/// <summary>
/// What was picked from a plant that bears food (P-Food, decision 108; CommandHostFood): the spots picked since it last bore all of
/// it, as bits (bit n is the plant's spot n, FoodLayout's order; the count picked is how many are set), and when its regrowth clock
/// started, in whole seconds of the island clock's UTC (P-Inventory-2, decision 124: one unit grows back every FoodRules.RegrowH island
/// hours from then, the lowest spot first, derived from the clock, never saved; CommandHostFood.Picking keeps the clock's phase across
/// picks). A plant with no record bears all its food. Not part of any undo step: a pick is never
/// taken back. A Grow cast that grows the plant ripens it (the record goes), and so does the plant burning down (its food burns with
/// it), so a replanted plant bears again.
/// </summary>
public sealed class NatureFood
{
    public string Id { get; set; } = "";
    public int Picked { get; set; }
    public long PickedS { get; set; }
    public NatureFood Copy() => (NatureFood)MemberwiseClone();
}

/// <summary>
/// A loose unit lying in the world (P-Inventory-2, decision 118): a unit of a stack set down with inventory.drop (an apple, berries, a
/// seed, a log, a stone), of the rules pack's kind, its bottom centre on the ground. Picked up again into its stack with F or E (the
/// pockets' take from the ground); burns in fire. Saved with the island in the order dropped, at most NatureState.MaxLoose, the oldest
/// going first. A windfall is not one of these: it lies loose at its plant's trunk from the start, by the food layout (FoodLayout).
/// </summary>
public sealed class NatureLoose
{
    public string Kind { get; set; } = "";
    public Vector3 PositionM { get; set; }
    public NatureLoose Copy() => (NatureLoose)MemberwiseClone();
}

/// <summary>
/// A sprout (decision 104, Grow's seedless growth): a plant Grow grew on bare fertile soil, copying one of the island's grass or flower
/// copies (Template, plant:&lt;set&gt;-&lt;row&gt;: its set's mesh, its size and its tint), rooted at PositionM and turned by a hash of its spot.
/// It is a seedling from SownS on the island clock, growing through its phases to its natural size (Factor, derived, never saved); it
/// burns as a plant (Burn 0 to 1) and is gone at 1. Saved with the island beside the plants; Creative's undo takes a cast back.
/// </summary>
public sealed class NatureSprout
{
    public string Template { get; set; } = "";
    public Vector3 PositionM { get; set; }
    public long SownS { get; set; }
    public float Burn { get; set; }
    /// <summary>Its size now, a factor of the copy's natural size: the seedling's phase (derived from SownS, never saved).</summary>
    public float Factor { get; set; } = GrowKindLaws.SeedlingStart;
    public NatureSprout Copy() => (NatureSprout)MemberwiseClone();
}

/// <summary>
/// A bridge of plants (decision 106): a level deck GrowKindLaws.BridgeWidthM wide from one lip of a gap to the other (FromM to ToM, the
/// same height), at most GrowKindLaws.BridgeMaxM long; its collision covers only the void between them (SpanFromM to SpanToM along
/// the deck, from FromM), one-sided facing up. It burns as a plant (Burn 0 to 1) and is gone at 1. Saved and undone like the waterpad.
/// </summary>
public sealed class NatureBridge
{
    public string Id { get; set; } = "";
    public Vector3 FromM { get; set; }
    public Vector3 ToM { get; set; }
    public float SpanFromM { get; set; }
    public float SpanToM { get; set; }
    public float Burn { get; set; }
    public float LengthM => FromM.DistanceTo(ToM);
    public NatureBridge Copy() => (NatureBridge)MemberwiseClone();
}

/// <summary>
/// Wall ivy (decision 107): a strip GrowKindLaws.IvyLengthM long and GrowKindLaws.IvyWidthM wide up a wall at least 70 degrees steep,
/// rooted on the standable ground at its foot: BaseM is the strip's bottom on the wall's face, NormalM the face's outward normal, and
/// LedgeM the standable ledge its top reaches, if any (one ivy a ledge). Drawn, not collided: a climb on it drains the bar at
/// GrowKindLaws.IvyClimbFactor of a bare face's rate. It burns as a plant (Burn 0 to 1) and is gone at 1. Saved and undone like the waterpad.
/// </summary>
public sealed class NatureIvy
{
    public string Id { get; set; } = "";
    public Vector3 BaseM { get; set; }
    public Vector3 NormalM { get; set; }
    public Vector3? LedgeM { get; set; }
    public float Burn { get; set; }
    public NatureIvy Copy() => (NatureIvy)MemberwiseClone();
}

/// <summary>
/// A waterpad (decision 75): one large water lily leaf a Grow cast on water grew, floating at the surface, the player able to stand on
/// it. Its spot is its leaf's centre at the top of the leaf; it burns like any plant (burn 0 to 1) and is gone at 1. Saved with the
/// island like the grown plants; Creative's undo takes it back.
/// </summary>
public sealed class NatureWaterpad
{
    public string Id { get; set; } = "";
    public Vector3 PositionM { get; set; }
    public float Burn { get; set; }
    public NatureWaterpad Copy() => (NatureWaterpad)MemberwiseClone();
}

/// <summary>The one fire that may burn at a time: who lit it, at whose direction, and what it was allowed to touch.</summary>
public sealed class NatureFire
{
    /// <summary>The principal whose cast lit it (a fire saved before 13 October counts as the player's: no one is blamed without proof).</summary>
    public string Principal { get; set; } = CommandHost.PlayerPrincipal;
    /// <summary>At whose direction, as far as the host can verify it: the player when the player approved it, otherwise the one who lit it.</summary>
    public string DirectedBy { get; set; } = CommandHost.PlayerPrincipal;
    public Vector3 Center { get; set; }
    public float RadiusM { get; set; }
    public float RemainingS { get; set; }
    /// <summary>The pack's intensity for Fire when it was lit: it scales how fast fuel burns and objects catch.</summary>
    public float Intensity { get; set; } = 0.6f;
    /// <summary>The room revision the fire was lit at: its cells are recorded in that revision's undo step.</summary>
    public int Revision { get; set; }
    /// <summary>Only these creations, at these revisions, may be scorched: a creation built or revised since never inherits an approval.</summary>
    public Dictionary<string, int> AuthorizedCreations { get; set; } = new(StringComparer.Ordinal);
    public NatureFire Copy() => new()
    {
        Principal = Principal, DirectedBy = DirectedBy, Center = Center, RadiusM = RadiusM, RemainingS = RemainingS, Intensity = Intensity, Revision = Revision,
        AuthorizedCreations = new(AuthorizedCreations, StringComparer.Ordinal),
    };
}

/// <summary>
/// One durable nature change, as room.undo takes it back: the room revision it committed at, the revision whose state it
/// started from (Base), and every cell and object as it was before the change touched it (null: it did not exist).
/// </summary>
public sealed class NatureUndoStep
{
    public string Principal { get; set; } = CommandHost.PlayerPrincipal;
    public int Revision { get; set; }
    public int Base { get; set; }
    public string Label { get; set; } = "";
    /// <summary>
    /// The mode it was recorded in ("" for a step saved before modes sealed undo). Only a step recorded wholly in Creative
    /// can be undone (the founder, 13 October: undo exists only in Creative); a fire still burning when the mode is not
    /// Creative seals its step, so switching modes opens no back door.
    /// </summary>
    public string Mode { get; set; } = "creative";
    public bool Undoable => Mode == "creative";
    public Dictionary<string, NatureCell?> Cells { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, NatureObject?> Objects { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Each plant as it was before the change touched it (null: it had nothing recorded, natural and unburnt).</summary>
    public Dictionary<string, NaturePlant?> Plants { get; set; } = new(StringComparer.Ordinal);
    /// <summary>The waterpads before this step changed them (a waterpad grown or burnt); null while the step did not touch them.</summary>
    public List<NatureWaterpad>? Waterpads { get; set; }
    /// <summary>The sprouts, bridges and ivy before this step changed them (Grow's unlocks, decisions 104 to 107); null while it did not.</summary>
    public List<NatureSprout>? Sprouts { get; set; }
    public List<NatureBridge>? Bridges { get; set; }
    public List<NatureIvy>? Ivy { get; set; }
    public Dictionary<string, int> Wallet { get; set; } = new(StringComparer.Ordinal);
    public bool TrailEnabled { get; set; }
    public string TrailPrincipal { get; set; } = "player";
    public string TrailRulesSha256 { get; set; } = "";

    public NatureUndoStep Copy() => new()
    {
        Principal = Principal, Revision = Revision, Base = Base, Label = Label, Mode = Mode,
        Cells = Cells.ToDictionary(p => p.Key, p => p.Value?.Copy(), StringComparer.Ordinal),
        Objects = Objects.ToDictionary(p => p.Key, p => p.Value?.Copy(), StringComparer.Ordinal),
        Plants = Plants.ToDictionary(p => p.Key, p => p.Value?.Copy(), StringComparer.Ordinal),
        Waterpads = Waterpads?.Select(w => w.Copy()).ToList(),
        Sprouts = Sprouts?.Select(s => s.Copy()).ToList(), Bridges = Bridges?.Select(b => b.Copy()).ToList(), Ivy = Ivy?.Select(i => i.Copy()).ToList(),
        Wallet = new(Wallet, StringComparer.Ordinal), TrailEnabled = TrailEnabled, TrailPrincipal = TrailPrincipal, TrailRulesSha256 = TrailRulesSha256,
    };
}

public readonly record struct NatureWeather(float Rain01, Vector3 WindMps, string Pattern);

/// <summary>
/// The island's nature: the ground field (sparse 5 cm cells, only where nature has acted), the objects fire has touched,
/// the weather clock, the flower trail's setting, the team's material wallet, the one fire and the undo steps. It is
/// room state, saved by the authority as the host's nature block (NatureBlock); the simulation runs on it in memory.
/// </summary>
public sealed class NatureState
{
    public const float CellM = 0.05f;
    /// <summary>The engine's caps: cells that exist at once, cells burning at once, objects fire has touched.</summary>
    public const int MaxCells = 16000;
    public const int MaxBurning = 200;
    public const int MaxObjects = 1024;
    /// <summary>Grown plants the island keeps (the brief's 4,096), and burnt ones (provisional): each is one record in the save.</summary>
    public const int MaxGrownPlants = 4096;
    public const int MaxBurntPlants = 8192;
    /// <summary>The most waterpads an island keeps (decision 75's per-island cap, provisional).</summary>
    public const int MaxWaterpads = 8;
    /// <summary>Grow's unlocks (decisions 104 to 107): the sprouts an island keeps (about 80 KB saved), its bridges and its ivy strips.</summary>
    public const int MaxSprouts = 1024;
    public const int MaxBridges = 8;
    public const int MaxIvy = 8;
    /// <summary>Durable nature changes room.undo can still take back, and the cells they remember together; the oldest go first.</summary>
    public const int MaxUndoSteps = 8;
    public const int MaxUndoCells = 8192;
    public const int MaxWalletKinds = 32;
    public const int MaxWalletUnits = 100000;
    /// <summary>The loose units an island keeps (P-Inventory-2, decision 118); the 257th dropped takes the oldest's place.</summary>
    public const int MaxLoose = 256;

    public double WeatherS { get; set; }
    /// <summary>The room revision of the latest durable nature change (0 before any): nature.describe's nature_revision.</summary>
    public int ChangedAt { get; set; }
    public bool TrailEnabled { get; set; } = true;
    /// <summary>Whose choice the trail's setting is ("player" or "companion"): the trail plants with that caster's permission.</summary>
    public string TrailPrincipal { get; set; } = "player";
    /// <summary>The rules pack a companion's keyed trail was approved under ("" otherwise); other rules stop it.</summary>
    public string TrailRulesSha256 { get; set; } = "";
    public Dictionary<string, NatureCell> Cells { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, NatureObject> Objects { get; set; } = new(StringComparer.Ordinal);
    /// <summary>The plant copies Grow grew or fire burned (only those: a plant with no record is as the manifest has it).</summary>
    public Dictionary<string, NaturePlant> Plants { get; set; } = new(StringComparer.Ordinal);
    /// <summary>The waterpads Grow grew on the island's water (decision 75), at most MaxWaterpads, in the order grown.</summary>
    public List<NatureWaterpad> Waterpads { get; set; } = new();
    /// <summary>Grow's unlocks, each in the order grown: the sprouts on bare soil (at most MaxSprouts), the bridges and the ivy strips.</summary>
    public List<NatureSprout> Sprouts { get; set; } = new();
    public List<NatureBridge> Bridges { get; set; } = new();
    public List<NatureIvy> Ivy { get; set; } = new();
    /// <summary>
    /// The retired materials wallet (P-Inventory-2, decision 116: logs and stones are stacks in the pockets and the chest now). Only an
    /// older save's is read (NatureBlock.Unpack), and the host moves it into the chest's stacks on load (CommandHost.MigrateWallet); it is
    /// never written again, so it is empty on every island once loaded.
    /// </summary>
    public Dictionary<string, int> Wallet { get; set; } = FreshWallet();
    public NatureFire? Fire { get; set; }
    /// <summary>The undo steps room.undo can reach, oldest first.</summary>
    public List<NatureUndoStep> Undo { get; set; } = new();
    /// <summary>The latest room.undo: the room revision it committed at, and the revision whose state it restored (-1, -1 for none).</summary>
    public int UndoneAt { get; set; } = -1;
    public int UndoneTo { get; set; } = -1;
    /// <summary>
    /// The Fractals the player picked up on this island (item.pickup, CommandHostFractals): gone from the island save, never
    /// an edit of the manifest. Not part of any undo step: a pickup is never taken back.
    /// </summary>
    public HashSet<string> FractalsPicked { get; set; } = new(StringComparer.Ordinal);
    /// <summary>What was picked from the plants that bear food (P-Food), by plant id: only plants picked and not grown back yet.</summary>
    public Dictionary<string, NatureFood> Food { get; set; } = new(StringComparer.Ordinal);
    /// <summary>The loose units dropped on the island (P-Inventory-2), oldest first, at most MaxLoose. No undo step remembers them.</summary>
    public List<NatureLoose> Loose { get; set; } = new();

    /// <summary>A unit dropped loose: at the end of the list, the oldest going when the island already keeps MaxLoose.</summary>
    public void Drop(NatureLoose unit)
    {
        while (Loose.Count >= MaxLoose) Loose.RemoveAt(0);
        Loose.Add(unit);
    }

    /// <summary>A new island's wallet: empty, whatever the pack says (the wallet is retired, P-Inventory-2; traits.wallet.start is read and ignored).</summary>
    public static Dictionary<string, int> FreshWallet() => new(StringComparer.Ordinal);

    /// <summary>A new island's wallet under a pack: empty, as FreshWallet() (a pack's traits.wallet.start no longer fills it).</summary>
    public static Dictionary<string, int> FreshWallet(WalletRules? start) => FreshWallet();
    public static string Key(int x, int z) => $"{x}:{z}";
    public static (int X, int Z) Grid(Vector3 at) => ((int)Math.Floor(at.X / CellM), (int)Math.Floor(at.Z / CellM));

    /// <summary>
    /// Nothing has happened to this nature yet, the weather clock aside: its wallet is still the island's start (the pack's,
    /// FreshWallet). trailAllowed lets the flower trail's own flowers count as nothing, as sightings do: the trail plants as the
    /// Gubble walks, with no command.
    /// </summary>
    public bool Untouched(bool trailAllowed, IReadOnlyDictionary<string, int> startWallet) =>
        Objects.Count == 0 && Plants.Count == 0 && Waterpads.Count == 0 && Sprouts.Count == 0 && Bridges.Count == 0 && Ivy.Count == 0 && Fire == null && Undo.Count == 0 && UndoneAt < 0 && ChangedAt == 0 && TrailEnabled && TrailPrincipal == "player" && FractalsPicked.Count == 0 && Food.Count == 0 && Loose.Count == 0 &&
        TrailRulesSha256.Length == 0 && Wallet.Count == startWallet.Count && startWallet.All(p => Wallet.GetValueOrDefault(p.Key, -1) == p.Value) &&
        (trailAllowed || Cells.Count == 0);

    public NatureState Copy() => new()
    {
        WeatherS = WeatherS, ChangedAt = ChangedAt, TrailEnabled = TrailEnabled, TrailPrincipal = TrailPrincipal, TrailRulesSha256 = TrailRulesSha256,
        Cells = Cells.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal),
        Objects = Objects.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal),
        Plants = Plants.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal),
        Waterpads = Waterpads.Select(w => w.Copy()).ToList(),
        Sprouts = Sprouts.Select(s => s.Copy()).ToList(), Bridges = Bridges.Select(b => b.Copy()).ToList(), Ivy = Ivy.Select(i => i.Copy()).ToList(),
        Wallet = new(Wallet, StringComparer.Ordinal), Fire = Fire?.Copy(), Undo = Undo.Select(s => s.Copy()).ToList(),
        UndoneAt = UndoneAt, UndoneTo = UndoneTo, FractalsPicked = new(FractalsPicked, StringComparer.Ordinal),
        Food = Food.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal),
        Loose = Loose.Select(l => l.Copy()).ToList(),
    };

    /// <summary>The weather now: a function of the saved clock, so a reload resumes the same weather (with no offline catch-up).</summary>
    public NatureWeather Weather => WeatherAt(WeatherS);

    /// <summary>The weather at any point of the clock: a resting cell's catch-up replays the weather it missed.</summary>
    public static NatureWeather WeatherAt(double weatherS)
    {
        var t = weatherS % NatureLaws.WeatherCycleS;
        var fallFrom = NatureLaws.WeatherWetUntilS + NatureLaws.WeatherRampS;
        var rain = (float)(t < NatureLaws.WeatherDryS ? 0
            : t < NatureLaws.WeatherDryS + NatureLaws.WeatherRampS ? (t - NatureLaws.WeatherDryS) / NatureLaws.WeatherRampS
            : t < NatureLaws.WeatherWetUntilS ? 1
            : t < fallFrom ? (fallFrom - t) / NatureLaws.WeatherRampS
            : 0);
        var direction = (float)(weatherS / NatureLaws.WindTurnS);
        var wind = new Vector3(MathF.Cos(direction), 0, MathF.Sin(direction)) * (NatureLaws.WindCalmMps + NatureLaws.WindRainMps * rain);
        var pattern = rain > NatureLaws.WeatherRain01 ? "rain" : rain > NatureLaws.WeatherShowers01 ? "showers" : "fair";
        return new NatureWeather(rain, wind, pattern);
    }

    /// <summary>The grid cells whose centres lie within radius of center, horizontally.</summary>
    public static IEnumerable<(int X, int Z)> Disk(Vector3 center, float radius)
    {
        var (cx, cz) = Grid(center);
        var span = (int)Math.Ceiling(radius / CellM) + 1;
        for (var x = cx - span; x <= cx + span; x++)
        for (var z = cz - span; z <= cz + span; z++)
            if (new Vector2((x + 0.5f) * CellM - center.X, (z + 0.5f) * CellM - center.Z).LengthSquared() <= radius * radius)
                yield return (x, z);
    }

    // ---- undo steps ----

    /// <summary>The revision whose state the room is in at roomRevision: a room.undo's revision stands for the state it restored.</summary>
    public int Effective(int roomRevision) => roomRevision == UndoneAt ? UndoneTo : roomRevision;

    /// <summary>
    /// Opens the undo step of a durable change committing at revision (the room's next revision). Steps that no longer
    /// connect to it (a newer non-nature change came between) can never be reached again and go now; past the bounds
    /// the oldest go first.
    /// </summary>
    public NatureUndoStep BeginUndo(string label, int revision, string principal, string mode = "creative")
    {
        var step = new NatureUndoStep
        {
            Principal = principal, Revision = revision, Base = Effective(revision - 1), Label = label, Mode = mode,
            TrailEnabled = TrailEnabled, TrailPrincipal = TrailPrincipal, TrailRulesSha256 = TrailRulesSha256,
        };
        if (Undo.Count > 0 && Undo[^1].Revision != step.Base) Undo.Clear();
        Undo.Add(step);
        TrimUndo();
        return step;
    }

    /// <summary>Keeps the undo steps within MaxUndoSteps and MaxUndoCells, dropping the oldest first (never the newest).</summary>
    public void TrimUndo()
    {
        while (Undo.Count > MaxUndoSteps) Undo.RemoveAt(0);
        while (Undo.Count > 1 && Undo.Sum(s => s.Cells.Count) > MaxUndoCells) Undo.RemoveAt(0);
    }

    /// <summary>
    /// The newest steps, newest first, that take the room back to target from roomRevision; null when they cannot. The
    /// target may be the revision a step started from, or a room.undo's revision standing for that state. A step recorded
    /// outside Creative cuts the chain: nothing at or before it can be taken back.
    /// </summary>
    public List<NatureUndoStep>? StepsBackTo(int target, int roomRevision)
    {
        var steps = new List<NatureUndoStep>();
        var goal = Effective(target);
        var expected = Effective(roomRevision);
        for (var i = Undo.Count - 1; i >= 0 && goal < expected; i--)
        {
            if (Undo[i].Revision != expected || !Undo[i].Undoable) return null;
            steps.Add(Undo[i]);
            expected = Undo[i].Base;
            if (expected == goal) return steps;
        }
        return null;
    }

    /// <summary>The revision the latest reachable nature change started from (what room.undo names to take it back), or null.</summary>
    public int? UndoTarget(int roomRevision) => Undo.Count > 0 && Undo[^1].Revision == Effective(roomRevision) && Undo[^1].Undoable ? Undo[^1].Base : null;

    /// <summary>Whether the latest change is a nature change recorded outside Creative: one there is no undo for.</summary>
    public bool LatestSealed(int roomRevision) => Undo.Count > 0 && Undo[^1].Revision == Effective(roomRevision) && !Undo[^1].Undoable;

    private NatureUndoStep? StepAt(int revision) => Undo.LastOrDefault(s => s.Revision == revision);

    public static void RememberCell(NatureUndoStep? step, Dictionary<string, NatureCell> cells, string key)
    {
        if (step != null && !step.Cells.ContainsKey(key)) step.Cells[key] = cells.TryGetValue(key, out var value) ? value.Copy() : null;
    }

    public void RememberObject(NatureUndoStep? step, string id)
    {
        if (step != null && !step.Objects.ContainsKey(id)) step.Objects[id] = Objects.TryGetValue(id, out var value) ? value.Copy() : null;
    }

    /// <summary>The waterpads as they are, remembered once by a step that grows or burns one.</summary>
    public void RememberWaterpads(NatureUndoStep? step)
    {
        if (step != null && step.Waterpads == null) step.Waterpads = Waterpads.Select(w => w.Copy()).ToList();
    }

    /// <summary>A new waterpad's id: waterpad:n, the smallest n no waterpad on the island has.</summary>
    public string NextWaterpadId()
    {
        for (var n = 1; ; n++)
            if (Waterpads.All(w => w.Id != "waterpad:" + n)) return "waterpad:" + n;
    }

    /// <summary>The sprouts, bridges and ivy as they are, each list remembered once by a step that grows or burns one of them.</summary>
    public void RememberSprouts(NatureUndoStep? step)
    {
        if (step != null && step.Sprouts == null) step.Sprouts = Sprouts.Select(s => s.Copy()).ToList();
    }

    public void RememberBridges(NatureUndoStep? step)
    {
        if (step != null && step.Bridges == null) step.Bridges = Bridges.Select(b => b.Copy()).ToList();
    }

    public void RememberIvy(NatureUndoStep? step)
    {
        if (step != null && step.Ivy == null) step.Ivy = Ivy.Select(i => i.Copy()).ToList();
    }

    /// <summary>A new bridge's or ivy strip's id: bridge:n or ivy:n, the smallest n the island's others do not have.</summary>
    public string NextBridgeId()
    {
        for (var n = 1; ; n++)
            if (Bridges.All(b => b.Id != "bridge:" + n)) return "bridge:" + n;
    }

    public string NextIvyId()
    {
        for (var n = 1; ; n++)
            if (Ivy.All(i => i.Id != "ivy:" + n)) return "ivy:" + n;
    }

    public void RememberPlant(NatureUndoStep? step, string id)
    {
        if (step != null && !step.Plants.ContainsKey(id)) step.Plants[id] = Plants.TryGetValue(id, out var value) ? value.Copy() : null;
    }

    /// <summary>The plant's record, made when it has none (natural size, unburnt).</summary>
    public NaturePlant Plant(string id)
    {
        if (!Plants.TryGetValue(id, out var plant)) Plants[id] = plant = new NaturePlant { Id = id };
        return plant;
    }

    public int GrownPlants => Plants.Values.Count(p => p.Grown);
    public int BurntPlants => Plants.Values.Count(p => p.Burnt);

    /// <summary>
    /// Bloom's flowers go (decision 52: Bloom became Grow and its flower trail was removed): every cell's flowers, and every undo
    /// step's remembered ones, are dropped; a cell left with nothing (no flowers, ash or fire) goes. An older island's save loads
    /// cleanly without them. Returns whether anything was dropped.
    /// </summary>
    public bool DropFlowers()
    {
        var dropped = false;
        foreach (var cell in Cells.Values.Concat(Undo.SelectMany(s => s.Cells.Values).Where(c => c != null).Select(c => c!)))
        {
            if (cell.Growth <= 0 && cell.LifeS <= 0) continue;
            cell.Growth = 0;
            cell.LifeS = 0;
            dropped = true;
        }
        foreach (var key in Cells.Where(p => Expired(p.Value)).Select(p => p.Key).ToList()) Cells.Remove(key);
        return dropped;
    }

    /// <summary>
    /// Takes the given steps back (newest first, as StepsBackTo lists them): every cell, object, wallet and trail setting
    /// as it was before them. Any fire goes out. The undo itself is a room revision (at) standing for target's state.
    /// </summary>
    public void UndoSteps(IReadOnlyList<NatureUndoStep> steps, int at, int target)
    {
        foreach (var step in steps)
        {
            foreach (var (key, cell) in step.Cells)
            {
                if (cell == null) Cells.Remove(key);
                else Cells[key] = cell.Copy();
            }
            foreach (var (id, item) in step.Objects)
            {
                if (item == null) Objects.Remove(id);
                else Objects[id] = item.Copy();
            }
            foreach (var (id, plant) in step.Plants)
            {
                if (plant == null) Plants.Remove(id);
                else Plants[id] = plant.Copy();
            }
            if (step.Waterpads != null) Waterpads = step.Waterpads.Select(w => w.Copy()).ToList();
            if (step.Sprouts != null) Sprouts = step.Sprouts.Select(s => s.Copy()).ToList();
            if (step.Bridges != null) Bridges = step.Bridges.Select(b => b.Copy()).ToList();
            if (step.Ivy != null) Ivy = step.Ivy.Select(i => i.Copy()).ToList();
            // The wallet is retired (P-Inventory-2): an older step's is not brought back; logs and stones are stacks, which no undo touches.
            TrailEnabled = step.TrailEnabled;
            TrailPrincipal = step.TrailPrincipal;
            TrailRulesSha256 = step.TrailRulesSha256;
            Undo.Remove(step);
        }
        Fire = null;
        foreach (var cell in Cells.Values) cell.BurningS = 0;
        UndoneTo = Effective(target);
        UndoneAt = at;
        ChangedAt = at;
    }

    // ---- the casts ----

    /// <summary>
    /// Lights the one fire: dry fuel near the centre catches, and the fire may spread within radius for durationS. It
    /// remembers who lit it and at whose direction (the journal and the Gubble's notices name the one who lit it).
    /// </summary>
    public int Ignite(Vector3 center, float radius, float durationS, float intensity, int revision, Dictionary<string, int> authorized, Func<int, int, NatureCell?> sample, NatureUndoStep? step,
        string principal = CommandHost.PlayerPrincipal, string? directedBy = null) =>
        IgniteCells(Disk(center, Math.Min(radius, NatureLaws.IgnitionRadiusM)), center, radius, durationS, intensity, revision, authorized, sample, step, principal, directedBy);

    /// <summary>
    /// Lights the one fire at exactly these cells (Fireline, decision 78: every ground cell along the line that can catch, at once), at
    /// most MaxBurning of them; the fire may then spread within radius of center for durationS, as any fire. Ignite is this at the
    /// cells near its centre.
    /// </summary>
    public int IgniteCells(IEnumerable<(int X, int Z)> cells, Vector3 center, float radius, float durationS, float intensity, int revision, Dictionary<string, int> authorized,
        Func<int, int, NatureCell?> sample, NatureUndoStep? step, string principal = CommandHost.PlayerPrincipal, string? directedBy = null)
    {
        Fire = new NatureFire
        {
            Principal = principal, DirectedBy = directedBy ?? principal,
            Center = center, RadiusM = radius, RemainingS = durationS, Intensity = intensity, Revision = revision,
            AuthorizedCreations = new(authorized, StringComparer.Ordinal),
        };
        var lit = 0;
        foreach (var (x, z) in cells)
        {
            if (lit >= MaxBurning) break;
            var key = Key(x, z);
            var ground = sample(x, z);
            if (ground == null || ground.Fuel <= 0 || ground.Moisture >= NatureLaws.IgnitionMaxMoisture) continue;
            if (!Cells.ContainsKey(key) && Cells.Count >= MaxCells) break;
            CatchUpCell(key);
            var cell = Cells.GetValueOrDefault(key) ?? ground;
            if (cell.Moisture >= NatureLaws.IgnitionMaxMoisture || cell.Fuel <= 0) continue;
            RememberCell(step, Cells, key);
            cell.BurningS = Math.Min(NatureLaws.IgnitionMaxS, durationS);
            Cells[key] = cell;
            lit++;
        }
        return lit;
    }

    // ---- the simulation ----

    /// <summary>
    /// One bounded, deterministic step. Wind biases spread; rain, wet soil, fireproof ground and gaps without fuel stop it.
    /// Cells for which active is false rest (the weather clock runs everywhere) and count the time they missed; when an
    /// avatar comes back they catch up on it first, replaying the weather they missed in steps of StepS, at most
    /// CatchUpCellStepsPerStep per step (CatchUp). Burning cells always run. Every cell the fire changes is recorded in the
    /// fire's undo step. Light is measured elsewhere (the host's light batch). Returns whether anything but the clock changed.
    /// </summary>
    public bool Step(float seconds, Func<int, int, NatureCell?> sample, Func<NatureCell, bool> fireproof, Func<NatureCell, bool> active)
    {
        if (!float.IsFinite(seconds) || seconds <= 0 || seconds > 1) throw new ArgumentOutOfRangeException(nameof(seconds));
        WeatherS += seconds;
        var weather = Weather;
        // Resting cells that are active again replay what they missed, up to the step before this one.
        var changed = CatchUp(WeatherS - seconds, active, NatureLaws.CatchUpCellStepsPerStep);
        var burning = Cells.Values.Where(c => c.BurningS > 0).OrderBy(c => c.X).ThenBy(c => c.Z).Take(MaxBurning).ToArray();
        foreach (var cell in Cells.Values)
        {
            if (cell.BurningS <= 0 && (cell.RestS > 0 || !active(cell)))
            {
                cell.RestS += seconds;
                continue;
            }
            changed = true;
            Tend(cell, seconds, weather);
        }
        changed |= Burn(seconds, weather, burning, sample, fireproof);
        // Expired flower-only cells can be sampled again. Ash stays until regrowth consumes it.
        var expired = Cells.Where(p => p.Value.Growth <= 0 && p.Value.LifeS <= 0 && p.Value.BurningS <= 0 && p.Value.Ash <= 0).Select(p => p.Key).ToArray();
        foreach (var key in expired) Cells.Remove(key);
        return changed || expired.Length > 0;
    }

    /// <summary>One cell's own step, fire aside: rain wets it and light dries it, its flowers grow or fade, moist ash regrows fuel.</summary>
    private static void Tend(NatureCell cell, float seconds, NatureWeather weather)
    {
        var wetting = cell.Exposed ? weather.Rain01 * NatureLaws.RainWettingPerS : 0;
        cell.Moisture = Math.Clamp(cell.Moisture + seconds * (wetting - NatureLaws.LightDryingPerS * cell.Light), 0, 1);
        cell.LifeS = Math.Max(0, cell.LifeS - seconds);
        if (cell.BurningS > 0) return;
        if (cell.LifeS > 0 && cell.Moisture > NatureLaws.GrowthMinMoisture && cell.Light > NatureLaws.GrowthMinLight)
            cell.Growth = Math.Min(1, cell.Growth + seconds * NatureLaws.GrowthPerS * cell.Moisture * Math.Min(1, cell.Light));
        else cell.Growth = Math.Max(0, cell.Growth - seconds * (cell.LifeS > 0 ? NatureLaws.FadeWhileAlivePerS : NatureLaws.FadeAfterLifePerS));
        if (cell.Ash > 0 && cell.Fertile && cell.Moisture > NatureLaws.AshRegrowMoisture)
        {
            cell.Ash = Math.Max(0, cell.Ash - seconds * NatureLaws.AshFadePerS * cell.Moisture);
            cell.Fuel = Math.Min(NatureLaws.FlowerFuel, cell.Fuel + seconds * NatureLaws.FuelRegrowPerS * cell.Moisture);
        }
    }

    private static bool Expired(NatureCell cell) => cell.Growth <= 0 && cell.LifeS <= 0 && cell.BurningS <= 0 && cell.Ash <= 0;

    private const int StepsPerWeatherCycle = (int)(NatureLaws.WeatherCycleS / NatureLaws.StepS);

    /// <summary>
    /// Time away (the founder, 13 October): the resting cells that are active again replay the time they missed, in grid
    /// order, at most budget cell steps in all; the rest wait for the next step. untilS is the clock at the end of the last
    /// step they missed. Returns whether any cell changed.
    /// </summary>
    public bool CatchUp(double untilS, Func<NatureCell, bool> active, int budget)
    {
        var waiting = Cells.Values.Where(c => c.RestS > 0 && (c.BurningS > 0 || active(c))).OrderBy(c => c.X).ThenBy(c => c.Z).ToList();
        var changed = false;
        foreach (var cell in waiting)
        {
            if (budget <= 0) break;
            changed = true;
            if (Replay(cell, untilS, ref budget)) Cells.Remove(cell.Key);
        }
        return changed;
    }

    /// <summary>A cell about to be changed by a cast or the spread catches up on all its missed time first; false when that left nothing there.</summary>
    private bool CatchUpCell(string key)
    {
        if (!Cells.TryGetValue(key, out var cell)) return false;
        if (cell.RestS <= 0) return true;
        var unbounded = int.MaxValue;
        if (!Replay(cell, WeatherS, ref unbounded)) return true;
        Cells.Remove(key);
        return false;
    }

    /// <summary>
    /// Replays a resting cell's missed time, ending at untilS, in steps of StepS with the weather of each moment: exactly
    /// what the ordinary step would have done to it (a resting cell does not burn, and only its own state changes). Once a
    /// whole weather cycle changes nothing, the remaining whole cycles would not either and are skipped, so even a long
    /// absence costs a few cycles at most. Returns true when the cell faded away (its flowers and ash gone).
    /// </summary>
    private static bool Replay(NatureCell cell, double untilS, ref int budget)
    {
        while (cell.RestS > 0 && budget > 0)
        {
            var pending = (int)Math.Ceiling(cell.RestS / NatureLaws.StepS - 1e-6);
            var cycle = pending > 2 * StepsPerWeatherCycle && budget >= StepsPerWeatherCycle;
            var before = cycle ? cell.Copy() : null;
            for (var i = cycle ? StepsPerWeatherCycle : Math.Min(pending, budget); i > 0 && cell.RestS > 0; i--)
            {
                var seconds = (float)Math.Min(NatureLaws.StepS, cell.RestS);
                Tend(cell, seconds, WeatherAt(untilS - cell.RestS + seconds));
                cell.RestS = Math.Max(0, cell.RestS - seconds);
                budget--;
                if (!Expired(cell)) continue;
                cell.RestS = 0;
                return true;
            }
            if (before != null && before.Moisture == cell.Moisture && before.Growth == cell.Growth && before.LifeS == cell.LifeS && before.Fuel == cell.Fuel && before.Ash == cell.Ash)
            {
                var whole = (int)(cell.RestS / NatureLaws.WeatherCycleS) - 1;
                if (whole > 0) cell.RestS -= whole * NatureLaws.WeatherCycleS;
            }
        }
        return false;
    }

    /// <summary>Puts out the burning cells within radius of a point, horizontally (where the player recovers).</summary>
    public bool PutOutNear(Vector3 at, float radius)
    {
        var any = false;
        foreach (var cell in Cells.Values.Where(c => c.BurningS > 0 && new Vector2(c.Position.X - at.X, c.Position.Z - at.Z).Length() <= radius))
        {
            cell.BurningS = 0;
            any = true;
        }
        return any;
    }

    /// <summary>The fire's half of a step: burning cells eat fuel and lay ash, and catch their neighbours through the gates.</summary>
    private bool Burn(float seconds, NatureWeather weather, NatureCell[] burning, Func<int, int, NatureCell?> sample, Func<NatureCell, bool> fireproof)
    {
        if (Fire == null && burning.Length == 0) return false;
        var step = Fire == null ? null : StepAt(Fire.Revision);
        var intensity = Fire?.Intensity ?? 0.6f;
        var next = new Dictionary<string, (int X, int Z)>(StringComparer.Ordinal);
        foreach (var cell in burning)
        {
            var surface = sample(cell.X, cell.Z);
            var unsupported = surface is not { Fuel: > 0 } || Math.Abs(surface.Y - cell.Y) > NatureLaws.BurnHeightToleranceM;
            if (Fire == null || Fire.RemainingS <= 0 || fireproof(cell) || unsupported || cell.Moisture >= NatureLaws.IgnitionMaxMoisture)
            {
                cell.BurningS = 0;
                continue;
            }
            RememberCell(step, Cells, cell.Key);
            cell.Fuel = Math.Max(0, cell.Fuel - seconds * NatureLaws.FuelBurnPerIntensityS * intensity);
            cell.Growth = 0;
            cell.LifeS = 0;
            cell.Ash = Math.Min(1, cell.Ash + seconds * NatureLaws.AshPerIntensityS * intensity);
            cell.BurningS = Math.Max(0, cell.BurningS - seconds * (1 + weather.Rain01 * NatureLaws.RainQuench));
            if (cell.Fuel <= NatureLaws.BurntOutFuel) cell.BurningS = 0;
            if (cell.BurningS <= 0 || weather.Rain01 >= NatureLaws.RainStopsSpread01) continue;
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                if (burning.Length + next.Count >= MaxBurning || Cells.Count + next.Count >= MaxCells) break;
                var x = cell.X + dx;
                var z = cell.Z + dz;
                var key = Key(x, z);
                if (next.ContainsKey(key) || Cells.TryGetValue(key, out var existing) && existing.BurningS > 0) continue;
                if (existing is { RestS: > 0 } && !CatchUpCell(key)) existing = null;
                var ground = sample(x, z);
                if (ground == null || ground.Fuel <= 0 || ground.Moisture >= NatureLaws.SpreadMaxMoisture || fireproof(ground)) continue;
                var away = new Vector2(ground.Position.X - Fire.Center.X, ground.Position.Z - Fire.Center.Z).Length();
                if (away > Fire.RadiusM || Math.Abs(ground.Y - cell.Y) > NatureLaws.SpreadStepM) continue;
                var fuel = existing ?? ground;
                if (fuel.Fuel < NatureLaws.SpreadMinFuel || fuel.Moisture >= NatureLaws.SpreadMaxMoisture) continue;
                // A reproducible rotating gate keeps one step from burning the whole circle at once.
                var downwind = new Vector3(dx, 0, dz).Dot(weather.WindMps) > 0;
                var gate = Math.Abs((long)x * 31 + (long)z * 17 + (long)(WeatherS * 2)) % (downwind ? NatureLaws.SpreadGateDownwind : NatureLaws.SpreadGateOther);
                if (gate != 0) continue;
                next[key] = (x, z);
            }
        }
        foreach (var (key, (x, z)) in next)
        {
            var cell = Cells.GetValueOrDefault(key) ?? sample(x, z);
            if (cell == null) continue;
            RememberCell(step, Cells, key);
            cell.BurningS = Math.Min(NatureLaws.SpreadBurnMaxS, Fire!.RemainingS);
            Cells[key] = cell;
        }
        if (Fire != null)
        {
            Fire.RemainingS = Math.Max(0, Fire.RemainingS - seconds);
            if (Fire.RemainingS <= 0 || !Cells.Values.Any(c => c.BurningS > 0))
            {
                foreach (var cell in Cells.Values) cell.BurningS = 0;
                Fire = null;
            }
        }
        return true;
    }
}
