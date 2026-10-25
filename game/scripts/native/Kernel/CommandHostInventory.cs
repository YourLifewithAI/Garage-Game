using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Room;
using GarageGame.Native.Saves;
using GarageGame.Native.Sandbox;

namespace GarageGame.Native.Kernel;

/// <summary>
/// The pockets and the chest (P-Inventory; docs/game/MECHANICS.md decisions 98 to 103, 108, 111 and 113; contracts: inventory.store,
/// inventory.take, inventory.use and inventory.drop, all transient receipts with data and no price):
/// <list type="bullet">
/// <item><b>The pockets</b> are the team's, one set per mode (the team file's pockets; a fixture without a team keeps them for the
/// session), carried everywhere: stacks of the rules pack's kinds up to each kind's size, and unique things, each a room object of one
/// island that stays in that island's room state as removed (the nature block's gathered mark, as a gathered log does) and comes out
/// there alone, with its own id. The pack's caps bound the entries and the mass.</item>
/// <item><b>The chest</b> is the island save's, one per island (decision 102), in its team block's camp block (room state's
/// x_play_camp.chest), reachable at any lit campfire or the starting one (PlayerAtCamp). It never spills; the Creative copy into Challenge
/// starts empty (Ui/Menu/MenuSaves.cs Strip).</item>
/// <item><b>Who.</b> The pockets and the hand are the player's: inventory.use and inventory.drop are player-only ops (refused in Execute),
/// and a companion naming the pockets is refused permission_denied before anything is looked up. The chest alone has a companion path:
/// while the player's team setting "Let the Gubble use the chest" (gubble_chest, GubbleChestAllowed) is on, the companion may store what
/// its own avatar holds and take a thing into its own hands, at a camp, never the player's hand or pockets; with it off every companion
/// store or take is permission_denied at $.op, before anything is looked up. Nothing here moves an avatar.</item>
/// <item><b>Saved by the host itself</b>, with no room revision (a transient receipt, as the player's settings and the vitals'
/// changes are): a thing leaving or coming back to the island and the chest go in one write of the island save (its nature and team
/// block together, the weather checkpoint's path); the pockets in the team file. Where two files change, what gains comes first and what
/// gives second, and each read reconciles a thing with the island's room state (a thing the island holds is in no inventory), so a
/// write lost between them can leave a thing in the world twice listed, never lost: the listing that the world disagrees with goes.</item>
/// <item><b>Loss</b> (decision 101): in Challenge and Live a knockout or the deep-water return empties the pockets' stacks, unique things
/// stay, with a journal line in the game's words; Creative loses nothing, and the chest never spills.</item>
/// </list>
/// </summary>
public partial class CommandHost
{
    public const string InventoryNotHere = "This island has no pockets or chest.";
    public const string PocketsArePlayers = "The pockets are the player's alone.";
    public const string GubbleChestOff = "The player has not let the Gubble use the chest.";
    public const string ChestAtCamp = "The chest is at the camp: store and take things by a lit campfire.";
    public const string HoldSomething = "Hold something to put it away: pick it up first.";
    public const string NotWhatIsHeld = "That is not what is being held.";
    public const string OnlyStacksFromHand = "Only food and seeds go in your pockets from your hand: store a thing like this in the chest, at a camp.";
    public const string NotInPockets = "That is not in your pockets.";
    public const string NotInChest = "That is not in the chest.";
    public const string StackToHand = "Food and seeds come out of the chest into your pockets.";
    public const string OtherIsland = "That belongs to another island: it comes out there.";
    public const string AThingIsOne = "A thing is one: name no count for it.";
    public const string InventoryUnsaved = "That could not be saved, so nothing moved. Try again in a moment.";
    public const string PocketsWaitForTeam = "The team file could not be read, so the pockets wait for it.";
    public const string NotOnYourFeet = "Wait until you are back on your feet.";

    private InventoryContents _chest = InventoryContents.Empty;
    private readonly Dictionary<string, InventoryContents> _pocketsUnfiled = new(StringComparer.Ordinal);
    private bool _gubbleChestUnfiled;

    /// <summary>The island's rules pack's inventory block, or null: no pockets and no chest.</summary>
    public InventoryRules? InventoryRules => Rules?.Inventory;
    public bool HasInventory => InventoryRules != null;

    /// <summary>"Let the Gubble use the chest" (decision 100): the team file's switch, else this session's; off by default.</summary>
    public bool GubbleChestAllowed => Team?.GubbleChest ?? _gubbleChestUnfiled;

    /// <summary>
    /// The player's own control for "Let the Gubble use the chest" (the HUD's setting; no command reaches it): the host follows it at
    /// once and the team file keeps it. False when it could not be written (it lasts this session) or there is no team file.
    /// </summary>
    public bool SetGubbleChest(bool allowed)
    {
        if (Team is not { } team)
        {
            _gubbleChestUnfiled = allowed;
            return false;
        }
        var next = team.WithGubbleChest(allowed);
        if (ReferenceEquals(next, team)) return true;
        Team = next;
        try
        {
            _teamStore?.Save(next);
            return _teamStore != null;
        }
        catch (TeamFileException) { return false; }
    }

    private string IslandPrefix => Room.ManifestSha256[..16];

    // ---- what the pockets and the chest hold, reconciled with the island's room state ----

    private InventoryContents PocketsAsKept => Team is { } team ? team.PocketsOf(Mode) : _pocketsUnfiled.GetValueOrDefault(Mode) ?? InventoryContents.Empty;

    /// <summary>
    /// This mode's pockets as they are: a thing of this island is kept only while the island holds it away (its nature record says
    /// gathered) and the chest does not list it too; a thing of another island is kept as it was (it comes out there).
    /// </summary>
    public InventoryContents PocketContents
    {
        get
        {
            var chest = ChestContents;
            return Reconciling ? PocketsAsKept.Keeping(t => !OfThisIsland(t) || (HeldAway(t.Id) && chest.Thing(t.Id) == null)) : PocketsAsKept;
        }
    }

    /// <summary>The island's chest as it is: a thing is kept only while the island holds it away.</summary>
    public InventoryContents ChestContents => Reconciling ? _chest.Keeping(t => HeldAway(t.Id)) : _chest;

    /// <summary>Whether the island's nature is loaded, so a thing's listing can be judged against it (else everything is kept as read).</summary>
    private bool Reconciling => !_natureUnavailable && Room != null;

    private bool OfThisIsland(InventoryThing thing) => thing.RoomId == Room.RoomId && thing.ManifestPrefix == IslandPrefix;

    /// <summary>A room object the island holds away for the inventory: gone with the gathered mark (as a gathered log, room state's removed).</summary>
    private bool HeldAway(string id) => Nature.Objects.TryGetValue(id, out var item) && item.Gathered && item.BurnedDown && Room.Objects.Any(o => o.Id == id);

    private InventoryThing ThingFor(ObjectInstance item) =>
        new(item.Id, Room.RoomId, IslandPrefix, NameOf(item), Math.Max(0.000001, item.Asset.MassKg));

    private static string NameOf(ObjectInstance item)
    {
        var name = KernelJson.DisplayText(item.DisplayName ?? item.Asset.DisplayName, InventoryContents.MaxNameLength);
        return InventoryContents.IsName(name) ? name : "Thing";
    }

    /// <summary>A chest thing's name and mass, from the room (LoadCampBlock).</summary>
    private (string Name, double MassKg) DescribeThing(string id) =>
        Room?.Objects.FirstOrDefault(o => o.Id == id) is { } item ? (NameOf(item), Math.Max(0.000001, item.Asset.MassKg)) : ("Thing", 0.000001);

    // ---- the commands ----

    private JsonObject InventoryStore(JsonElement args, string principal, string actionId, string fingerprint, bool preview)
    {
        var to = Str(args, "to")!;
        var from = Str(args, "from") ?? "hand";
        if (principal != PlayerPrincipal) CompanionChestOnly(to == "pockets" ? "$.args.to" : from == "pockets" ? "$.args.from" : null);
        var rules = RequireInventory(principal);
        Settle();
        if (from == "pockets") return StoreFromPockets(args, rules, principal, actionId, fingerprint, preview);
        var actor = principal == PlayerPrincipal ? PlayerAvatar : CompanionAvatarId;
        if (!_held.TryGetValue(actor, out var held)) throw new Refusal("request_invalid", HoldSomething, "$.args");
        if (Str(args, "item") is { } named && named != held.Target && rules.KindOfAsset(held.Item.Asset.AssetId)?.Id != named)
            throw new Refusal("request_invalid", NotWhatIsHeld, "$.args.item");
        if (args.TryGetProperty("count", out var count) && count.GetInt32() != 1) throw new Refusal("invalid_args", AThingIsOne, "$.args.count");
        var kind = rules.KindOfAsset(held.Item.Asset.AssetId);
        if (to == "pockets")
        {
            if (kind == null) throw new Refusal("request_invalid", OnlyStacksFromHand, "$.args.to");
            var pockets = PocketContents;
            var next = pockets.WithUnits(kind.Id, pockets.Units(kind.Id) + 1);
            CheckFits(next, rules, rules.Pockets, pockets: true, kind);
            if (preview) return Previewed("inventory.store", principal, actionId, held.Target);
            // The pockets gain the unit first, then the windfall leaves the island (a lost second write leaves a unit, never loses one).
            SavePockets(next);
            try { LeaveIsland(actor, held, null); }
            catch (Refusal)
            {
                SavePockets(pockets, keepIfUnsaved: true);
                throw;
            }
            return Transient("inventory.store", principal, actionId, fingerprint, new JsonArray(held.Target), Moved(kind.Id, 1, "pockets"));
        }
        if (!AtCampFor(actor)) throw new Refusal("not_ready", ChestAtCamp, "$.args.to", retryable: true);
        var chest = ChestContents;
        var filled = kind != null ? chest.WithUnits(kind.Id, chest.Units(kind.Id) + 1) : chest.WithThing(ThingFor(held.Item));
        CheckFits(filled, rules, rules.Chest, pockets: false, kind);
        if (preview) return Previewed("inventory.store", principal, actionId, held.Target);
        // The chest and the island in one write: the thing is away (gathered) and listed together, or neither.
        LeaveIsland(actor, held, filled);
        return Transient("inventory.store", principal, actionId, fingerprint, new JsonArray(held.Target), Moved(kind?.Id ?? held.Target, 1, "chest"));
    }

    private JsonObject StoreFromPockets(JsonElement args, InventoryRules rules, string principal, string actionId, string fingerprint, bool preview)
    {
        var item = Str(args, "item")!;
        var pockets = PocketContents;
        var thing = item.StartsWith("obj:", StringComparison.Ordinal) ? pockets.Thing(item) ?? throw new Refusal("target_not_found", NotInPockets, "$.args.item") : null;
        var have = thing == null ? pockets.Units(item) : 1;
        if (have == 0) throw new Refusal("target_not_found", NotInPockets, "$.args.item");
        if (thing != null && !OfThisIsland(thing)) throw new Refusal("request_invalid", OtherIsland, "$.args.item");
        if (!PlayerAtCamp()) throw new Refusal("not_ready", ChestAtCamp, "$.args.to", retryable: true);
        var count = Units(args, have, thing != null);
        var chest = ChestContents;
        var kind = rules.Kind(item);
        var filled = thing != null ? chest.WithThing(thing) : chest.WithUnits(item, chest.Units(item) + count);
        CheckFits(filled, rules, rules.Chest, pockets: false, kind);
        if (preview) return Previewed("inventory.store", principal, actionId, thing != null ? new[] { item } : Array.Empty<string>());
        // The chest gains first, then the pockets give (a thing listed twice is settled by the world; never lost).
        SaveChest(filled);
        try { SavePockets(thing != null ? pockets.WithoutThing(item) : pockets.WithUnits(item, have - count)); }
        catch (Refusal)
        {
            SaveChest(chest, quiet: true);
            throw;
        }
        return Transient("inventory.store", principal, actionId, fingerprint, thing != null ? new JsonArray(item) : new JsonArray(), Moved(item, count, "chest"));
    }

    private JsonObject InventoryTake(JsonElement args, string principal, string actionId, string fingerprint, bool preview)
    {
        var to = Str(args, "to")!;
        if (principal != PlayerPrincipal) CompanionChestOnly(to == "pockets" ? "$.args.to" : null);
        var rules = RequireInventory(principal);
        Settle();
        var item = Str(args, "item")!;
        var actor = principal == PlayerPrincipal ? PlayerAvatar : CompanionAvatarId;
        var chest = ChestContents;
        var thing = item.StartsWith("obj:", StringComparison.Ordinal) ? chest.Thing(item) ?? throw new Refusal("target_not_found", NotInChest, "$.args.item") : null;
        var have = thing == null ? chest.Units(item) : 1;
        if (have == 0) throw new Refusal("target_not_found", NotInChest, "$.args.item");
        if (!AtCampFor(actor)) throw new Refusal("not_ready", ChestAtCamp, "$.args.item", retryable: true);
        var count = Units(args, have, thing != null);
        var pockets = PocketContents;
        if (thing == null)
        {
            if (to == "hand") throw new Refusal("request_invalid", StackToHand, "$.args.to");
            var next = pockets.WithUnits(item, pockets.Units(item) + count);
            CheckFits(next, rules, rules.Pockets, pockets: true, rules.Kind(item));
            if (preview) return Previewed("inventory.take", principal, actionId);
            SavePockets(next);
            try { SaveChest(chest.WithUnits(item, have - count)); }
            catch (Refusal)
            {
                SavePockets(pockets, keepIfUnsaved: true);
                throw;
            }
            return Transient("inventory.take", principal, actionId, fingerprint, new JsonArray(), Moved(item, count, "pockets"));
        }
        var room = Room.Objects.First(o => o.Id == item);
        if (to == "pockets")
        {
            var next = pockets.WithThing(thing);
            CheckFits(next, rules, rules.Pockets, pockets: true, null);
            if (preview) return Previewed("inventory.take", principal, actionId, item);
            SavePockets(next);
            try { SaveChest(chest.WithoutThing(item)); }
            catch (Refusal)
            {
                SavePockets(pockets, keepIfUnsaved: true);
                throw;
            }
            return Transient("inventory.take", principal, actionId, fingerprint, new JsonArray(item), Moved(item, 1, "pockets"));
        }
        if (BodyOf(actor) is not { } body || !body.IsInsideTree()) throw new Refusal("not_ready", "That avatar is not in the room.", "$.args.to", retryable: true);
        if (_held.ContainsKey(actor)) throw new Refusal("target_busy", "This avatar is already holding something. Put it down first.", "$.args.to");
        var limit = SandboxRules.CarryLimitKg(actor);
        if (room.Asset.MassKg > limit)
            throw new Refusal("target_too_heavy", $"That is too heavy to carry: {Kilograms(room.Asset.MassKg)} kg, and this avatar can manage {Kilograms(limit)} kg.", "$.args.item",
                actual: JsonValue.Create(Kilograms(room.Asset.MassKg)), allowed: JsonValue.Create(Kilograms(limit)));
        if (preview) return Previewed("inventory.take", principal, actionId, item);
        // Back on the island and out of the chest in one write, then in the hand (holding is never saved).
        var candidate = Nature.Copy();
        candidate.Objects.Remove(item);
        var before = _chest;
        _chest = chest.WithoutThing(item);
        try { SaveInventoryNature(candidate); }
        catch (Refusal)
        {
            _chest = before;
            throw;
        }
        TakeHold(actor, body, room, PoseOf(room, ObjectPoses()));
        return Transient("inventory.take", principal, actionId, fingerprint, new JsonArray(item), Moved(item, 1, "hand"));
    }

    /// <summary>
    /// inventory.use (player-only): one unit of a stack in the pockets. A kind whose use heals heals at once, its heal_quarters (decisions 92
    /// and 111: no unharmed wait); at full hearts the unit stays (not_ready); in Creative nothing changes but the receipt (decisions 101 and
    /// 108). The unit goes first, then the heal is saved; a heal that cannot be saved puts the unit back.
    /// </summary>
    private JsonObject InventoryUse(JsonElement args, string principal, string actionId, string fingerprint, bool preview)
    {
        if (principal != PlayerPrincipal) throw new Refusal("permission_denied", "Only the player can do that, directly in the game.", "$.op");
        var rules = RequireInventory(principal);
        Settle();
        var item = Str(args, "item")!;
        var pockets = PocketContents;
        var have = pockets.Units(item);
        if (have == 0) throw new Refusal("target_not_found", NotInPockets, "$.args.item");
        var kind = rules.Kind(item);
        if (kind?.HealQuarters is not { } heal) throw new Refusal("request_invalid", $"{kind?.Plural ?? "That"} {(kind == null ? "has" : "have")} no use yet.", "$.args.item");
        if (Mode == "creative")
        {
            if (preview) return Previewed("inventory.use", principal, actionId);
            return Transient("inventory.use", principal, actionId, fingerprint, new JsonArray(), Moved(item, 0, null, healed: 0));
        }
        if (_playerVitalsUnavailable) throw new Refusal("not_ready", VitalsNotice, retryable: true);
        if (RecoveryPending || Vitals.Health <= 0) throw new Refusal("not_ready", NotOnYourFeet, "$.args.item", retryable: true);
        var vitals = Vitals;
        var (healed, quarters) = HealedBy(vitals, heal);
        if (quarters <= 0) throw new Refusal("not_ready", $"Your hearts are full: the {kind.Name} stays in your pockets.", "$.args.item", retryable: true);
        if (preview) return Previewed("inventory.use", principal, actionId);
        SavePockets(pockets.WithUnits(item, have - 1));
        if (TrySaveVitals(_playerModes.WithCurrent(healed)) is not { } saved)
        {
            SavePockets(pockets, keepIfUnsaved: true);
            throw new Refusal("not_ready", InventoryUnsaved, retryable: true);
        }
        _playerModes = saved;
        _vitalsDirty = false;
        return Transient("inventory.use", principal, actionId, fingerprint, new JsonArray(), Moved(item, 1, null, healed: quarters));
    }

    /// <summary>
    /// What a heal of so many quarters makes of a body, at once: the hearts in quarters (or a pack without them: so many health-points'
    /// quarters, HealthPerQuarter each), never past the most; and the quarters it really healed.
    /// </summary>
    private (PlayerVitals Healed, int Quarters) HealedBy(PlayerVitals vitals, int quarters)
    {
        if (_vitalsRules.InQuarters)
        {
            var now = _vitalsRules.Quarters(vitals);
            var after = Math.Min(_vitalsRules.MaxQuarters(vitals), now + quarters);
            return (vitals.With(health: (float)(after * _vitalsRules.HealthPerQuarter)), after - now);
        }
        var health = (float)Math.Min(vitals.MaxHealth, vitals.Health + quarters * _vitalsRules.HealthPerQuarter);
        return (vitals.With(health: health), (int)Math.Round((health - vitals.Health) / _vitalsRules.HealthPerQuarter, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// inventory.drop (player-only): a unique thing from the pockets set on the ground before the player, on its own island, as a put-down
    /// with no placement settles it. Its pose is saved first as a put-down's is (an entity.place receipt under the host's own action id,
    /// while the thing is still away), then it is back on the island with its id, then the pockets give it up. A stack's unit has no body
    /// in the world yet (P-Food gives one): unsupported_capability.
    /// </summary>
    private JsonObject InventoryDrop(JsonElement args, string principal, string actionId, string fingerprint, bool preview)
    {
        if (principal != PlayerPrincipal) throw new Refusal("permission_denied", "Only the player can do that, directly in the game.", "$.op");
        var rules = RequireInventory(principal);
        Settle();
        var item = Str(args, "item")!;
        var pockets = PocketContents;
        if (!item.StartsWith("obj:", StringComparison.Ordinal))
        {
            if (pockets.Units(item) == 0) throw new Refusal("target_not_found", NotInPockets, "$.args.item");
            var plural = rules.Kind(item)?.Plural ?? "Those";
            throw new Refusal("unsupported_capability", $"{plural} cannot be set down yet: use them, or keep them in the chest.", "$.args.item");
        }
        var thing = pockets.Thing(item) ?? throw new Refusal("target_not_found", NotInPockets, "$.args.item");
        if (!OfThisIsland(thing)) throw new Refusal("request_invalid", OtherIsland, "$.args.item");
        if (Player == null || !IsInstanceValid(Player) || !Player.IsInsideTree()) throw new Refusal("not_ready", "The player is not in the room.", retryable: true);
        var room = Room.Objects.First(o => o.Id == item);
        var rotation = PoseOf(room, ObjectPoses(), carried: false).Rotation;
        var above = Player.GlobalPosition + Vector3.Up * (Player.BodyHeightM + SandboxRules.CarryGapM);
        var rest = Settled(Physics().Drop(ThingOf(room, above, rotation), Player), "$.args.item");
        if (!SandboxRules.WithinReach(Player, rest.Bounds)) throw OutOfReach("$.args.item");
        if (preview) return Previewed("inventory.drop", principal, actionId, item);
        var placed = $"inv-{_session}-{++_counter}";
        var meta = new Godot.Collections.Dictionary
        {
            ["fingerprint"] = CanonicalJson.Sha256Hex(System.Text.Encoding.UTF8.GetBytes("inventory.drop|" + fingerprint + "|" + placed)), ["op"] = "entity.place", ["at_utc"] = Now(),
        };
        Move(item, rest, PlayerPrincipal, placed, meta);
        var candidate = Nature.Copy();
        candidate.Objects.Remove(item);
        SaveInventoryNature(candidate);
        // The thing is back: a pockets write lost now leaves it listed, and the next read lets that listing go.
        SavePockets(pockets.WithoutThing(item), keepIfUnsaved: true);
        return Transient("inventory.drop", principal, actionId, fingerprint, new JsonArray(item), Moved(item, 1, "ground"));
    }

    // ---- the checks ----

    /// <summary>
    /// The companion's one path, decided before anything is looked up (decision 100): the pockets (and the player's hand) are never its,
    /// and the chest only while the player's gubble_chest is on.
    /// </summary>
    private void CompanionChestOnly(string? pocketsPath)
    {
        if (pocketsPath != null) throw new Refusal("permission_denied", PocketsArePlayers, pocketsPath);
        if (!GubbleChestAllowed) throw new Refusal("permission_denied", GubbleChestOff, "$.op");
    }

    /// <summary>What every inventory command needs: a pack with the block, an island not held, a save that loaded and a role that may change it.</summary>
    private InventoryRules RequireInventory(string principal)
    {
        var rules = InventoryRules ?? throw new Refusal("unsupported_capability", InventoryNotHere, "$.op");
        if (IslandHeld) throw new Refusal("not_ready", TeamNotice);
        RequireChange(principal);
        return rules;
    }

    /// <summary>The reconciled chest is the one every command builds on (and writes back).</summary>
    private void Settle() => _chest = ChestContents;

    private bool AtCampFor(string actor) => actor == PlayerAvatar ? PlayerAtCamp()
        : Companion != null && IsInstanceValid(Companion) && Room != null && AtCamp(Companion.GlobalPosition);

    /// <summary>How many units a command moves: its count, or everything; never more than there are; a thing is one.</summary>
    private static int Units(JsonElement args, int have, bool thing)
    {
        if (!args.TryGetProperty("count", out var count)) return have;
        var units = count.GetInt32();
        if (thing && units != 1) throw new Refusal("invalid_args", AThingIsOne, "$.args.count");
        if (units > have)
            throw new Refusal("invalid_args", $"There {(have == 1 ? "is" : "are")} only {have} of them.", "$.args.count", actual: JsonValue.Create(units), allowed: JsonValue.Create(have));
        return units;
    }

    /// <summary>The caps a change must keep: a stack's own size, the entries and the mass (occupied past any).</summary>
    private static void CheckFits(InventoryContents next, InventoryRules rules, InventoryCaps caps, bool pockets, InventoryKind? kind)
    {
        if (kind != null && next.Units(kind.Id) > kind.Stack)
            throw new Refusal("occupied", $"A stack holds at most {kind.Stack} {kind.Words(kind.Stack)}.", "$.args", allowed: JsonValue.Create(kind.Stack));
        if (next.Entries > caps.Entries)
            throw new Refusal("occupied", pockets ? $"Your pockets are full: they hold {caps.Entries} things." : $"The chest is full: it holds {caps.Entries} things.", "$.args",
                allowed: JsonValue.Create(caps.Entries));
        if (next.MassKg(rules) > caps.MassKg + 1e-9)
            throw new Refusal("occupied", pockets ? $"That would be too heavy for your pockets: they hold {Kilograms((float)caps.MassKg)} kg." :
                $"That would be too heavy for the chest: it holds {Kilograms((float)caps.MassKg)} kg.", "$.args", allowed: JsonValue.Create(Math.Round(caps.MassKg, 3)));
    }

    private static JsonObject Moved(string item, int count, string? to, int? healed = null)
    {
        var data = new JsonObject { ["item"] = item, ["count"] = count };
        if (to != null) data["to"] = to;
        if (healed != null) data["healed_quarters"] = healed.Value;
        return data;
    }

    // ---- the writes ----

    /// <summary>
    /// A held thing leaves the island: into the chest (with the chest it fills, in the same write) or into the pockets as a unit (the pockets
    /// already gained it). Gone as a gathered thing is, at the pose it was last put down; the hands are empty first, and a refused write
    /// gives it back.
    /// </summary>
    private void LeaveIsland(string actor, Held held, InventoryContents? chest)
    {
        var (position, rotation) = PoseOf(held.Item, ObjectPoses(), carried: false);
        var candidate = Nature.Copy();
        candidate.Objects[held.Target] = new NatureObject { Id = held.Target, Burn = 1, Gathered = true, PositionM = position, Rotation = rotation };
        LetGo(actor);
        var before = _chest;
        if (chest != null) _chest = chest;
        try { SaveInventoryNature(candidate); }
        catch (Refusal)
        {
            _chest = before;
            if (BodyOf(actor) is { } body) TakeHold(actor, body, held.Item, (held.RotationAtGrab, held.YawAtGrab));
            throw;
        }
    }

    /// <summary>
    /// The island's nature and its team block (the chest with them) in one write of the island save, with no receipt and no room revision:
    /// the weather checkpoint's path (CommandHostNature.SaveNature). Then the island is what was written.
    /// </summary>
    private void SaveInventoryNature(NatureState candidate)
    {
        if (_natureUnavailable) throw new Refusal("not_ready", NatureNotice, retryable: true);
        candidate.TrimUndo();
        using var team = KernelJson.ToVariant(ExportTeam(_notes));
        using var nature = KernelJson.ToVariant(NatureBlock.Pack(candidate));
        if (!SavedOk(Authority.Call(KernelNames.SetNature, nature, team))) throw new Refusal("not_ready", IslandHeld ? TeamNotice : InventoryUnsaved, retryable: !IslandHeld);
        PublishNature(candidate);
        _natureDirty = false;
        _natureClockMoved = false;
        _teamDirty = false;
        _saveClock = 0;
        _natureSaveClock = 0;
    }

    /// <summary>The chest alone (stacks, or a thing already away): the island save's team block. quiet: a step back that must not throw.</summary>
    private void SaveChest(InventoryContents next, bool quiet = false)
    {
        var before = _chest;
        _chest = next;
        SaveTeam();
        if (!_teamDirty || quiet) return;
        _chest = before;
        throw new Refusal("not_ready", IslandHeld ? TeamNotice : InventoryUnsaved, retryable: !IslandHeld);
    }

    /// <summary>
    /// This mode's pockets into the team file (a fixture without a team: this session). keepIfUnsaved: the change stands for the session
    /// even when the file cannot be written (a loss, a step back); otherwise nothing changes and the command is refused.
    /// </summary>
    private void SavePockets(InventoryContents next, bool keepIfUnsaved = false)
    {
        if (Team is { } team)
        {
            TeamFile updated;
            try { updated = team.WithPockets(Mode, next); }
            catch (TeamFileException) { if (keepIfUnsaved) return; throw new Refusal("not_ready", InventoryUnsaved, retryable: true); }
            if (ReferenceEquals(updated, team)) return;
            try { _teamStore?.Save(updated); }
            catch (TeamFileException)
            {
                if (!keepIfUnsaved) throw new Refusal("not_ready", InventoryUnsaved, retryable: true);
                if (TeamNotice.Length == 0) TeamNotice = "The pockets could not be saved to the team file; they count for this session.";
            }
            Team = updated;
            return;
        }
        if (_teamStore != null && !keepIfUnsaved) throw new Refusal("not_ready", PocketsWaitForTeam, retryable: true);
        _pocketsUnfiled[Mode] = next;
    }

    // ---- the args (contracts: $defs/args/inventory.*) ----

    private static void CheckInventoryArgs(string op, JsonElement args)
    {
        string[] allowed = op switch
        {
            "inventory.store" => new[] { "to", "from", "item", "count" },
            "inventory.take" => new[] { "item", "to", "count" },
            _ => new[] { "item" },
        };
        foreach (var property in args.EnumerateObject())
            if (!allowed.Contains(property.Name))
                throw new Refusal("field_unknown", "This argument is not part of the operation.", "$.args." + KernelJson.DisplayText(property.Name, 64));
        var required = op switch { "inventory.store" => new[] { "to" }, "inventory.take" => new[] { "item", "to" }, _ => new[] { "item" } };
        foreach (var name in required)
            if (!args.TryGetProperty(name, out _)) throw new Refusal("request_invalid", $"This operation needs '{name}'.", "$.args." + name);
        var to = Str(args, "to");
        if (op == "inventory.store" && args.TryGetProperty("to", out _) && to is not ("chest" or "pockets"))
            throw new Refusal("request_invalid", "to is chest or pockets.", "$.args.to");
        if (op == "inventory.take" && to is not ("hand" or "pockets")) throw new Refusal("request_invalid", "to is hand or pockets.", "$.args.to");
        if (args.TryGetProperty("from", out _) && Str(args, "from") is not ("hand" or "pockets")) throw new Refusal("request_invalid", "from is hand or pockets.", "$.args.from");
        if (args.TryGetProperty("item", out var item))
        {
            var text = item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
            var fits = op == "inventory.use" ? Token.IsMatch(text) : Token.IsMatch(text) || ObjectIdPattern.IsMatch(text);
            if (!fits) throw new Refusal("request_invalid", op == "inventory.use" ? "item names a kind in your pockets." : "item names a kind or a thing by its id.", "$.args.item");
        }
        if (args.TryGetProperty("count", out var count) && (count.ValueKind != JsonValueKind.Number || !IsIntegerLiteral(count) || count.GetDouble() is < 1 or > InventoryContents.MaxUnits))
            throw new Refusal("request_invalid", "count is a whole number from 1 to 1000.", "$.args.count");
        if (op == "inventory.store" && Str(args, "from") == "pockets")
        {
            if (!args.TryGetProperty("item", out _)) throw new Refusal("request_invalid", "This operation needs 'item'.", "$.args.item");
            if (to != "chest") throw new Refusal("request_invalid", "From the pockets, things go to the chest.", "$.args.to");
        }
    }
}
