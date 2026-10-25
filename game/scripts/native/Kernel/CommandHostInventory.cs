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
/// inventory.take, inventory.use and inventory.drop, receipts with data and no price: a store, a take and a drop are durable room changes,
/// as item.gather is, and a use is transient):
/// <list type="bullet">
/// <item><b>The pockets</b> are the team's, one set per mode (the team file's pockets; a fixture without a team keeps them for the
/// session), carried everywhere: stacks of the rules pack's kinds up to each kind's size, and unique things, each a room object of one
/// island that stays in that island's room state as removed (the nature block's gathered mark, as a gathered log does) and comes out
/// there alone, with its own id. The pack's caps are counts (decision 120): the slots, and each kind's stack.</item>
/// <item><b>The chest</b> is the island save's, one per island (decision 102), in its team block's camp block (room state's
/// x_play_camp.chest), reachable at any lit campfire or the starting one (PlayerAtCamp). It never spills; the Creative copy into Challenge
/// starts empty (Ui/Menu/MenuSaves.cs Strip).</item>
/// <item><b>Who.</b> The pockets and the hand are the player's: inventory.use and inventory.drop are player-only ops (refused in Execute),
/// and a companion naming the pockets is refused permission_denied before anything is looked up. The chest alone has a companion path:
/// while the player's team setting "Let the Gubble use the chest" (gubble_chest, GubbleChestAllowed) is on, the companion may store what
/// its own avatar holds and take a thing into its own hands, at a camp, never the player's hand or pockets; with it off every companion
/// store or take is permission_denied at $.op, before anything is looked up. Nothing here moves an avatar.</item>
/// <item><b>Saved as a room revision</b> (decided 25 October: they change the island's room state, the chest and a thing held away):
/// a store, a take or a drop is one durable receipt with its data, as item.gather's (CommitInventory): the island's nature (a thing away
/// or back), its team block (the chest) and the receipt in one write of the island save, the revision moved; a drop's put-down pose is
/// one more (an entity.place receipt under the host's own action id, first). The pockets are the team file's, written beside it; a use
/// changes only the pockets and the body, so its receipt is transient. Where two files change, what gains comes first and what gives
/// second, and each read reconciles a thing with the island's room state (a thing the island holds is in no inventory), so a write lost
/// between them can leave a thing in the world twice listed, never lost: the listing that the world disagrees with goes.</item>
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
    public const string NotInPockets = "That is not in your pockets.";
    public const string NotInChest = "That is not in the chest.";
    public const string StackToHand = "Food and seeds come out of the chest into your pockets.";
    public const string OtherIsland = "That belongs to another island: it comes out there.";
    public const string AThingIsOne = "A thing is one: name no count for it.";
    public const string InventoryUnsaved = "That could not be saved, so nothing moved. Try again in a moment.";
    public const string PocketsWaitForTeam = "The team file could not be read, so the pockets wait for it.";
    public const string NotOnYourFeet = "Wait until you are back on your feet.";

    /// <summary>The inventory's ops whose receipts are durable, kept with their data (room state's receipts; the authority's NATURE_OPS).</summary>
    internal static readonly HashSet<string> InventoryDurableOps = new(StringComparer.Ordinal) { "inventory.store", "inventory.take", "inventory.drop" };

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
            // A slot reserved for a thing taken into the hand lasts while the hand still holds it (P-Inventory-2: the hotbar's hand).
            var kept = PocketsAsKept.KeepingReservations(id => HeldBy(PlayerAvatar) == id);
            return Reconciling ? kept.Keeping(t => !OfThisIsland(t) || (HeldAway(t.Id) && chest.Thing(t.Id) == null)) : kept;
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
        new(item.Id, Room.RoomId, IslandPrefix, NameOf(item), MassOf(item));

    /// <summary>
    /// A thing's mass as the inventory counts it: the asset's, to the milligram (the team file's precision), so a 0.3 kg
    /// doorstop is 0.3 kg and fits pockets that hold 0.3 kg (the asset's float would make it 0.30000001).
    /// </summary>
    private static double MassOf(ObjectInstance item) => Math.Max(0.000001, Math.Round((double)item.Asset.MassKg, 6));

    private static string NameOf(ObjectInstance item)
    {
        var name = KernelJson.DisplayText(item.DisplayName ?? item.Asset.DisplayName, InventoryContents.MaxNameLength);
        return InventoryContents.IsName(name) ? name : "Thing";
    }

    /// <summary>A chest thing's name and mass, from the room (LoadCampBlock).</summary>
    private (string Name, double MassKg) DescribeThing(string id) =>
        Room?.Objects.FirstOrDefault(o => o.Id == id) is { } item ? (NameOf(item), MassOf(item)) : ("Thing", 0.000001);

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
        if (Str(args, "item") is { } named && named != held.Target && KindOfObject(rules, held.Item)?.Id != named)
            throw new Refusal("request_invalid", NotWhatIsHeld, "$.args.item");
        if (args.TryGetProperty("count", out var count) && count.GetInt32() != 1) throw new Refusal("invalid_args", AThingIsOne, "$.args.count");
        // A unit of a kind (a windfall by its asset, a log or stone by its material, decision 116) joins its stack; anything else is a thing.
        var kind = KindOfObject(rules, held.Item);
        if (to == "pockets")
        {
            // Anywhere: a unit of a kind joins its stack; a unique thing takes an entry of its own (decision 99), under the pockets' caps.
            var pockets = PocketContents;
            var next = kind != null ? pockets.WithUnits(kind.Id, pockets.Units(kind.Id) + 1) : pockets.WithThing(ThingFor(held.Item));
            CheckFits(next, rules, rules.Pockets, pockets: true, kind);
            if (preview) return Previewed("inventory.store", principal, actionId, held.Target);
            // The pockets gain it first, then it leaves the island, a room revision (a lost second write leaves a unit, never loses one;
            // a thing listed while the island still holds it is let go when read).
            SavePockets(next);
            try { return LeaveIsland(actor, held, _chest, principal, actionId, fingerprint, Moved(kind?.Id ?? held.Target, 1, "pockets")); }
            catch (Refusal)
            {
                SavePockets(pockets, keepIfUnsaved: true);
                throw;
            }
        }
        if (!AtCampFor(actor)) throw new Refusal("not_ready", ChestAtCamp, "$.args.to", retryable: true);
        var chest = ChestContents;
        var filled = kind != null ? chest.WithUnits(kind.Id, chest.Units(kind.Id) + 1) : chest.WithThing(ThingFor(held.Item));
        CheckFits(filled, rules, rules.Chest, pockets: false, kind);
        if (preview) return Previewed("inventory.store", principal, actionId, held.Target);
        // The chest and the island in one write: the thing is away (gathered) and listed together, or neither.
        return LeaveIsland(actor, held, filled, principal, actionId, fingerprint, Moved(kind?.Id ?? held.Target, 1, "chest"));
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
        RequirePocketsFiled();
        // The chest gains first, a room revision, then the pockets give: a pockets write lost now stands for the session (the next one
        // keeps it), and a thing listed twice is settled by the world; never lost, and a committed receipt is never taken back.
        var result = CommitInventory(Nature.Copy(), filled, "inventory.store", principal, actionId, fingerprint, thing != null ? new[] { item } : Array.Empty<string>(),
            Moved(item, count, "chest"));
        SavePockets(thing != null ? pockets.WithoutThing(item) : pockets.WithUnits(item, have - count), keepIfUnsaved: true);
        return result;
    }

    private JsonObject InventoryTake(JsonElement args, string principal, string actionId, string fingerprint, bool preview)
    {
        var to = Str(args, "to")!;
        var from = Str(args, "from") ?? "chest";
        if (principal != PlayerPrincipal) CompanionChestOnly(to == "pockets" ? "$.args.to" : from == "pockets" ? "$.args.from" : null);
        var rules = RequireInventory(principal);
        Settle();
        // P-Inventory-2: a unique thing from the pockets into the hand (the hotbar's hand, CommandHostInventoryHand).
        if (from == "pockets") return TakeFromPockets(args, rules, principal, actionId, fingerprint, preview);
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
            // The pockets gain first, then the chest gives, a room revision; a refused revision puts the pockets back.
            SavePockets(next);
            try { return CommitInventory(Nature.Copy(), chest.WithUnits(item, have - count), "inventory.take", principal, actionId, fingerprint, Array.Empty<string>(), Moved(item, count, "pockets")); }
            catch (Refusal)
            {
                SavePockets(pockets, keepIfUnsaved: true);
                throw;
            }
        }
        var room = Room.Objects.First(o => o.Id == item);
        if (to == "pockets")
        {
            var next = pockets.WithThing(thing);
            CheckFits(next, rules, rules.Pockets, pockets: true, null);
            if (preview) return Previewed("inventory.take", principal, actionId, item);
            SavePockets(next);
            try { return CommitInventory(Nature.Copy(), chest.WithoutThing(item), "inventory.take", principal, actionId, fingerprint, new[] { item }, Moved(item, 1, "pockets")); }
            catch (Refusal)
            {
                SavePockets(pockets, keepIfUnsaved: true);
                throw;
            }
        }
        if (BodyOf(actor) is not { } body || !body.IsInsideTree()) throw new Refusal("not_ready", "That avatar is not in the room.", "$.args.to", retryable: true);
        if (_held.ContainsKey(actor)) throw new Refusal("target_busy", "This avatar is already holding something. Put it down first.", "$.args.to");
        var limit = SandboxRules.CarryLimitKg(actor);
        if (room.Asset.MassKg > limit)
            throw new Refusal("target_too_heavy", $"That is too heavy to carry: {Kilograms(room.Asset.MassKg)} kg, and this avatar can manage {Kilograms(limit)} kg.", "$.args.item",
                actual: JsonValue.Create(Kilograms(room.Asset.MassKg)), allowed: JsonValue.Create(Kilograms(limit)));
        if (preview) return Previewed("inventory.take", principal, actionId, item);
        // Back on the island and out of the chest in one write, a room revision, then in the hand (holding is never saved).
        var candidate = Nature.Copy();
        candidate.Objects.Remove(item);
        var result = CommitInventory(candidate, chest.WithoutThing(item), "inventory.take", principal, actionId, fingerprint, new[] { item }, Moved(item, 1, "hand"));
        TakeHold(actor, body, room, PoseOf(room, ObjectPoses()));
        return result;
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
        // P-Inventory-2: no item named is the hand's selected slot (the hotbar's armed unit).
        var item = Str(args, "item") ?? SelectedItem(forUse: true);
        var pockets = PocketContents;
        var have = pockets.Units(item);
        if (have == 0) throw new Refusal("target_not_found", NotInPockets, "$.args.item");
        var kind = rules.Kind(item);
        if (kind?.HealQuarters is not { } heal) throw new Refusal("request_invalid", $"{kind?.Plural ?? "That"} {(kind == null ? "has" : "have")} no use yet.", "$.args.item");
        if (Mode == "creative")
        {
            if (preview) return Previewed("inventory.use", principal, actionId);
            // P-Food: eating makes its sound in Creative too (decision 108; CommandHostFood.Eaten).
            return Transient("inventory.use", principal, actionId, fingerprint, new JsonArray(), Eaten(item, Moved(item, 0, null, healed: 0)));
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
        return Transient("inventory.use", principal, actionId, fingerprint, new JsonArray(), Eaten(item, Moved(item, 1, null, healed: quarters)));
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
    /// while the thing is still away: a room revision), then it is back on the island with its id (the drop's own durable receipt, the
    /// next revision), then the pockets give it up. A stack's unit has no body in the world yet (P-Food gives one): unsupported_capability.
    /// </summary>
    private JsonObject InventoryDrop(JsonElement args, string principal, string actionId, string fingerprint, bool preview)
    {
        if (principal != PlayerPrincipal) throw new Refusal("permission_denied", "Only the player can do that, directly in the game.", "$.op");
        var rules = RequireInventory(principal);
        Settle();
        // P-Inventory-2: no item named is the hand's selected slot; the thing it took into the hand is set down from there.
        var item = Str(args, "item") ?? SelectedItem(forUse: false);
        var pockets = PocketContents;
        if (_held.TryGetValue(PlayerAvatar, out var inHand) && inHand.Target == item && pockets.ReservedFor(item) != null)
            return DropFromHand(inHand, principal, actionId, fingerprint, preview);
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
        var result = CommitInventory(candidate, _chest, "inventory.drop", principal, actionId, fingerprint, new[] { item }, Moved(item, 1, "ground"));
        // The thing is back: a pockets write lost now leaves it listed, and the next read lets that listing go.
        SavePockets(pockets.WithoutThing(item), keepIfUnsaved: true);
        return result;
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

    /// <summary>
    /// The caps a change must keep (decision 120: counts, not weights): a stack's own size, then the slots (occupied past either, in the
    /// kernel's words: "A stack holds at most 99 Apples.", "Your pockets are full: ten things.", "The chest is full: twenty things.").
    /// </summary>
    private static void CheckFits(InventoryContents next, InventoryRules rules, InventoryCaps caps, bool pockets, InventoryKind? kind)
    {
        if (kind != null && next.Units(kind.Id) > kind.Stack)
            throw new Refusal("occupied", StackFullWords(kind), "$.args", allowed: JsonValue.Create(kind.Stack));
        if (next.Entries > caps.Entries)
            throw new Refusal("occupied", pockets ? PocketsFullWords(caps) : ChestFullWords(caps), "$.args", allowed: JsonValue.Create(caps.Entries));
    }

    public static string StackFullWords(InventoryKind kind) => $"A stack holds at most {kind.Stack} {kind.Words(kind.Stack)}.";
    public static string PocketsFullWords(InventoryCaps caps) => $"Your pockets are full: {InventoryWords.Things(caps.Entries)}.";
    public static string ChestFullWords(InventoryCaps caps) => $"The chest is full: {InventoryWords.Things(caps.Entries)}.";

    private static JsonObject Moved(string item, int count, string? to, int? healed = null)
    {
        var data = new JsonObject { ["item"] = item, ["count"] = count };
        if (to != null) data["to"] = to;
        if (healed != null) data["healed_quarters"] = healed.Value;
        return data;
    }

    // ---- the writes ----

    /// <summary>
    /// A held thing leaves the island, a room revision: into the chest (with the chest it fills, in the same write) or into the pockets (the
    /// pockets already gained it; chest is the chest as it is). Gone as a gathered thing is, at the pose it was last put down; the hands are
    /// empty first, and a refused write gives it back.
    /// </summary>
    private JsonObject LeaveIsland(string actor, Held held, InventoryContents chest, string principal, string actionId, string fingerprint, JsonObject data)
    {
        var (position, rotation) = PoseOf(held.Item, ObjectPoses(), carried: false);
        var candidate = Nature.Copy();
        candidate.Objects[held.Target] = new NatureObject { Id = held.Target, Burn = 1, Gathered = true, PositionM = position, Rotation = rotation };
        LetGo(actor);
        try { return CommitInventory(candidate, chest, "inventory.store", principal, actionId, fingerprint, new[] { held.Target }, data); }
        catch (Refusal)
        {
            if (BodyOf(actor) is { } body) TakeHold(actor, body, held.Item, (held.RotationAtGrab, held.YawAtGrab));
            throw;
        }
    }

    /// <summary>
    /// One durable room revision for an inventory change, as item.gather's (CommandHostGather, through the authority's nature path): the
    /// island's nature (a thing away or back), its team block with this chest, and the receipt with its data, in one atomic write of the
    /// island save that moves the revision. The answer is the durable receipt, as every replay and room state's receipts will be. A refused
    /// write changes nothing: the chest is put back and the refusal is the authority's.
    /// </summary>
    private JsonObject CommitInventory(NatureState candidate, InventoryContents chest, string op, string principal, string actionId, string fingerprint,
        IEnumerable<string> affected, JsonObject data, string? approvedBy = null)
    {
        if (_natureUnavailable) throw new Refusal("not_ready", NatureNotice, retryable: true);
        candidate.ChangedAt = Revision + 1;
        candidate.TrimUndo();
        var before = _chest;
        _chest = chest;
        var meta = new Godot.Collections.Dictionary { ["fingerprint"] = fingerprint, ["op"] = op, ["at_utc"] = Now() };
        // A Gubble's change the player said yes to (a repair spending the pockets) carries that yes, as CommitNature's does.
        if (approvedBy != null && principal != PlayerPrincipal) meta["approved_by"] = approvedBy;
        var request = new Godot.Collections.Dictionary
        {
            ["op"] = "nature", ["action_id"] = actionId, ["expected_revision"] = Revision, ["expected_permission_revision"] = PermissionRevision,
            ["nature"] = KernelJson.ToVariant(NatureBlock.Pack(candidate)), ["team"] = KernelJson.ToVariant(ExportTeam(_notes)),
            ["affected"] = new Godot.Collections.Array(affected.Select(id => (Variant)id)), ["data"] = KernelJson.ToVariant(data), ["undo"] = false,
        };
        var outcome = Authority.Call(KernelNames.Submit, principal, request, meta).AsGodotDictionary();
        if (!outcome["ok"].AsBool())
        {
            _chest = before;
            throw IslandHeld ? new Refusal("not_ready", TeamNotice) : Translate(outcome);
        }
        PublishNature(candidate);
        _natureDirty = false;
        _natureClockMoved = false;
        _natureSaveClock = 0;
        _teamDirty = false;
        _saveClock = 0;
        return Durable(Authority.Call(KernelNames.ReceiptFor, principal, actionId).AsGodotDictionary(), replayed: false);
    }

    // ---- the retired wallet (P-Inventory-2, decision 116): what repairs spend is the stacks, the pockets first, then the chest ----

    /// <summary>
    /// What a paid repair takes (decision 116): its cost, by the creation compiler's materials, in units of the pack's kinds whose material
    /// each is, from the pockets first, then the chest (whose chest is the island's, wherever the repair is). Refused invalid_args, in the
    /// kernel's words, when the pockets and the chest hold too few, or no kind is made of a material (copper and leaf have none yet).
    /// </summary>
    private RepairBill RepairSpend(IReadOnlyDictionary<string, int> cost)
    {
        Settle();
        var rules = InventoryRules;
        var pockets = PocketContents;
        var chest = ChestContents;
        var need = new List<string>();
        var short_ = false;
        // In the pack's order of its kinds (a log before a stone), then any material no kind is made of.
        int Order(string material) => rules?.KindOfMaterial(material) is { } kind ? rules.Kinds.ToList().IndexOf(kind) : int.MaxValue;
        foreach (var (material, units) in cost.OrderBy(c => Order(c.Key)).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            if (units <= 0) continue;
            var kind = rules?.KindOfMaterial(material);
            need.Add(kind == null ? $"{units} {material}" : $"{units} {KernelJson.DisplayText(kind.Words(units), InventoryContents.MaxNameLength).ToLowerInvariant()}");
            if (kind == null) { short_ = true; continue; }
            var fromPockets = Math.Min(units, pockets.Units(kind.Id));
            var fromChest = Math.Min(units - fromPockets, chest.Units(kind.Id));
            if (fromPockets + fromChest < units) short_ = true;
            pockets = pockets.WithUnits(kind.Id, pockets.Units(kind.Id) - fromPockets);
            chest = chest.WithUnits(kind.Id, chest.Units(kind.Id) - fromChest);
        }
        var words = need.Count switch { 0 => "nothing", 1 => need[0], _ => string.Join(", ", need.Take(need.Count - 1)) + " and " + need[^1] };
        if (short_) throw new Refusal("invalid_args", $"This repair needs {words}, more than your pockets and the chest hold.", "$.args.target");
        return new RepairBill(pockets, chest, words, !pockets.Same(PocketContents));
    }

    /// <summary>A repair's bill: the pockets and the chest after it, its cost in words ("2 logs and 1 stone"), and whether the pockets give.</summary>
    private sealed record RepairBill(InventoryContents Pockets, InventoryContents Chest, string Words, bool FromPockets);

    /// <summary>
    /// A paid repair, one durable room revision (the island's nature, its team block with the chest after it, and the receipt), then the
    /// pockets give (a pockets write lost now leaves the units unspent, never lost twice; a committed receipt is never taken back).
    /// </summary>
    private JsonObject CommitRepair(NatureState candidate, RepairBill bill, string principal, string actionId, string fingerprint, string? approvedBy, string id, JsonObject data)
    {
        if (bill.FromPockets) RequirePocketsFiled();
        var result = CommitInventory(candidate, bill.Chest, "nature.repair", principal, actionId, fingerprint, new[] { id }, data, approvedBy);
        if (bill.FromPockets) SavePockets(bill.Pockets, keepIfUnsaved: true);
        return result;
    }

    /// <summary>The Gubble's ask for a repair (it waits for the player's yes): the player's own creation, and what it would spend of the player's.</summary>
    private static string RepairAsk(string id, bool players, RepairBill? bill) =>
        bill == null ? $"The Gubble asks to repair {id}, which you made." :
        players ? $"The Gubble asks to repair {id}, which you made, with {bill.Words} from your pockets and the chest." :
        $"The Gubble asks to repair {id}, with {bill.Words} from your pockets and the chest.";

    /// <summary>
    /// The retired wallet's counts become the chest's stacks (decision 116: "an older save's wallet counts become chest stacks on load"),
    /// once the island's pack has an inventory block that names a kind for the material (a log for wood, a stone for stone): added to the
    /// chest's stack of the kind, up to its size and the chest's slots. Whatever cannot be moved (copper and leaf, which no kind is made of
    /// yet, or past a cap) stays in the retired wallet as it was, read and written back unchanged, never spent. The chest and the nature go
    /// in one write of the island save (the authority's set_nature with the team block), so a save can never hold both the stacks and the
    /// wallet they came from; a write that fails changes nothing, and the next load tries again. Called when nature loads and when rules
    /// arrive (UseRules): the pack may come after the save.
    /// </summary>
    private void MigrateWallet()
    {
        if (Nature.Wallet.Count == 0 || InventoryRules is not { } rules || Room == null || _natureUnavailable) return;
        if (Authority == null || !Authority.Call(KernelNames.IsReady).AsBool() || (_migrationOffer != null && !Godot.FileAccess.FileExists(SavePath))) return;
        var wallet = Nature.Wallet;
        var chest = ChestContents;
        var left = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (material, units) in wallet.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (units <= 0) continue;
            var kind = rules.KindOfMaterial(material);
            var have = kind == null ? 0 : chest.Units(kind.Id);
            var moved = kind == null || (have == 0 && chest.Entries >= rules.Chest.Entries) ? 0 : Math.Min(units, Math.Max(0, kind.Stack - have));
            if (moved > 0) chest = chest.WithUnits(kind!.Id, have + moved);
            if (units - moved > 0) left[material] = units - moved;
        }
        var before = _chest;
        _chest = chest;
        Nature.Wallet = left;
        using var team = KernelJson.ToVariant(ExportTeam(_notes));
        using var nature = KernelJson.ToVariant(NatureBlock.Pack(Nature));
        if (!SavedOk(Authority.Call(KernelNames.SetNature, nature, team)))
        {
            _chest = before;
            Nature.Wallet = wallet;
            return;
        }
        _teamDirty = false;
        _saveClock = 0;
    }

    /// <summary>Before a change whose pockets give after the room revision: the team file must be there to keep them (not a file that would not read).</summary>
    private void RequirePocketsFiled()
    {
        if (_teamStore != null && Team == null) throw new Refusal("not_ready", PocketsWaitForTeam, retryable: true);
    }

    /// <summary>
    /// A durable inventory receipt's data as room state keeps it (CommandHostRoomState's receipts): this host's fields only, each of its
    /// type and range ($defs/data/inventory: item, count, to; healed_quarters is a use's, which is transient). True when it is not.
    /// </summary>
    internal static bool InventoryDataProblem(JsonNode? node)
    {
        if (node is not JsonObject data || data.Count != 3 || data.Any(p => p.Key is not ("item" or "count" or "to"))) return true;
        if (data["item"] is not JsonValue item || item.GetValueKind() != JsonValueKind.String || item.GetValue<string>() is not { Length: > 0 } named ||
            !(named.StartsWith("obj:", StringComparison.Ordinal) ? InventoryContents.IsThingId(named) : InventoryContents.IsKindId(named))) return true;
        if (data["count"] is not JsonValue count || count.GetValueKind() != JsonValueKind.Number || !count.TryGetValue<double>(out var units) || units != Math.Floor(units) ||
            units < 0 || units > InventoryContents.MaxUnits) return true;
        return data["to"] is not JsonValue to || to.GetValueKind() != JsonValueKind.String || to.GetValue<string>() is not ("chest" or "pockets" or "hand" or "ground");
    }

    /// <summary>
    /// This mode's pockets into the team file (a fixture without a team: this session). keepIfUnsaved: the change stands for the session
    /// even when the file cannot be written (a loss, a step back); otherwise nothing changes and the command is refused.
    /// </summary>
    private void SavePockets(InventoryContents next, bool keepIfUnsaved = false, HandSelection? hand = null)
    {
        if (Team is { } team)
        {
            TeamFile updated;
            // The hand's selection rides in the same write when it changes with the pockets (a gathered log's slot, P-Inventory-2).
            try { updated = hand == null ? team.WithPockets(Mode, next) : team.WithPockets(Mode, next).WithHand(Mode, hand); }
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
        if (hand != null) _handUnfiled[Mode] = hand;
    }

    /// <summary>
    /// inventory.drop of the unique thing the hand took from its slot (P-Inventory-2): set down before the player as a put-down settles it,
    /// its pose saved first (an entity.place receipt under the host's own action id, as a drop from the pockets), then the drop's own
    /// durable receipt; the slot is free again.
    /// </summary>
    private JsonObject DropFromHand(Held held, string principal, string actionId, string fingerprint, bool preview)
    {
        if (Player == null || !IsInstanceValid(Player) || !Player.IsInsideTree()) throw new Refusal("not_ready", "The player is not in the room.", retryable: true);
        var (position, rotation) = Carried(PlayerAvatar, held);
        var rest = Settled(Physics().Drop(ThingOf(held.Item, position, rotation), Player), "$.args.item");
        if (!SandboxRules.WithinReach(Player, rest.Bounds)) throw OutOfReach("$.args.item");
        if (preview) return Previewed("inventory.drop", principal, actionId, held.Target);
        var (placed, placedFingerprint) = InnerAction("inventory.drop", fingerprint);
        var meta = new Godot.Collections.Dictionary { ["fingerprint"] = placedFingerprint, ["op"] = "entity.place", ["at_utc"] = Now() };
        MoveHeld(PlayerAvatar, held, rest, PlayerPrincipal, placed, meta);
        var result = CommitInventory(Nature.Copy(), _chest, "inventory.drop", principal, actionId, fingerprint, new[] { held.Target }, Moved(held.Target, 1, "ground"));
        SavePockets(PocketContents.Unreserving(held.Target), keepIfUnsaved: true);
        return result;
    }

    // ---- the args (contracts: $defs/args/inventory.*) ----

    private static void CheckInventoryArgs(string op, JsonElement args)
    {
        if (op == "inventory.select") { CheckSelectArgs(args); return; }
        string[] allowed = op switch
        {
            "inventory.store" => new[] { "to", "from", "item", "count" },
            "inventory.take" => new[] { "item", "to", "count", "from" },
            _ => new[] { "item" },
        };
        foreach (var property in args.EnumerateObject())
            if (!allowed.Contains(property.Name))
                throw new Refusal("field_unknown", "This argument is not part of the operation.", "$.args." + KernelJson.DisplayText(property.Name, 64));
        // P-Inventory-2: a use or a drop with no item acts on the hand's selected slot.
        var required = op switch { "inventory.store" => new[] { "to" }, "inventory.take" => new[] { "item", "to" }, _ => Array.Empty<string>() };
        foreach (var name in required)
            if (!args.TryGetProperty(name, out _)) throw new Refusal("request_invalid", $"This operation needs '{name}'.", "$.args." + name);
        var to = Str(args, "to");
        if (op == "inventory.store" && args.TryGetProperty("to", out _) && to is not ("chest" or "pockets"))
            throw new Refusal("request_invalid", "to is chest or pockets.", "$.args.to");
        if (op == "inventory.take" && to is not ("hand" or "pockets")) throw new Refusal("request_invalid", "to is hand or pockets.", "$.args.to");
        if (op == "inventory.store" && args.TryGetProperty("from", out _) && Str(args, "from") is not ("hand" or "pockets")) throw new Refusal("request_invalid", "from is hand or pockets.", "$.args.from");
        if (op == "inventory.take" && args.TryGetProperty("from", out _) && Str(args, "from") is not ("chest" or "pockets")) throw new Refusal("request_invalid", "from is chest or pockets.", "$.args.from");
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
