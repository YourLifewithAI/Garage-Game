using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Room;
using GarageGame.Native.Sandbox;

namespace GarageGame.Native.Kernel;

/// <summary>
/// Gathering (decisions 26 and 116 of docs/game/MECHANICS.md; contracts: item.gather; P-Inventory-2): a loose log or stone goes into
/// the pockets as a unit of its stack, the pack's kind whose material it is (a log wood, a stone stone, by what the object is made of:
/// GatherMaterial). Picking a log or stone up is not a carry (the founder's refinement, 25 October): the player names one within reach
/// (or the one it holds), it joins its stack at once and that slot becomes the hand's selection (inventory.select's state). Player-only
/// (the companion is refused before anything is looked up, PlayerOnlyOps). Refused in the pockets' own words when the stack or the
/// slots are full (occupied), and nothing moves: the thing stays where it is. It leaves the island as one durable room revision
/// through the host's nature block: the object is gone as a burnt one is (hidden, no collision, a removed entity in room state),
/// marked gathered; the pockets are written first (the team file), so a lost second write leaves a unit, never loses one. The
/// receipt keeps its shape: durable, no data, the object in affected; the journal keeps a 'gathered' fact. The materials wallet it
/// once filled is retired: what repairs spend is the stacks (CommandHostInventory.RepairSpend).
/// </summary>
public partial class CommandHost
{
    /// <summary>What one loose log or stone adds to its stack. Provisional: the design names no number (the founder's to set).</summary>
    public const int GatherUnits = 1;

    public const string GatherOnlyLogsAndStones = "Only a loose log or stone can be gathered.";

    /// <summary>The material a room object would be gathered as, or null when it is no loose log or stone.</summary>
    public static string? GatherMaterial(ObjectInstance item)
    {
        var asset = item.Asset;
        if (!asset.Movable || asset.Affordances.Contains("container") || asset.Materials.Length == 0) return null;
        if (asset.Materials.All(m => m.Role is "bark" or "wood")) return "wood";
        if (asset.Materials.All(m => m.Role == "stone")) return "stone";
        return null;
    }

    /// <summary>The kind a held or loose room object counts as in the pockets and the chest: a kind naming its asset, else its material's (a log, a stone), else none (a unique thing).</summary>
    private static InventoryKind? KindOfObject(InventoryRules rules, ObjectInstance item) => rules.KindOfAsset(item.Asset.AssetId) ?? rules.KindOfMaterial(GatherMaterial(item));

    /// <summary>The HUD's gather key: item.gather of what the player is holding (M2 binds the key).</summary>
    public JsonObject PlayerGather(bool preview = false) =>
        PlayerCommand("item.gather", new JsonObject { ["target"] = HeldBy(PlayerAvatar) ?? "obj:nothing" }, preview);

    /// <summary>A loose log or stone within reach into the pockets, as the player's item.gather (F and E on one: PlayerHands).</summary>
    public JsonObject PlayerGatherFrom(string target, bool preview = false) => PlayerCommand("item.gather", new JsonObject { ["target"] = target }, preview);

    /// <summary>
    /// The materials the team could spend on a repair now (nature.describe's "wallet", kept for its readers): the units of each kind with
    /// a material, in the pockets and the chest together, by material, sorted.
    /// </summary>
    private JsonObject MaterialCounts()
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        if (InventoryRules is { } rules)
            foreach (var kind in rules.Kinds.Where(k => k.Material != null))
                if (PocketContents.Units(kind.Id) + ChestContents.Units(kind.Id) is var units && units > 0) counts[kind.Material!] = Math.Min(units, NatureState.MaxWalletUnits);
        return new JsonObject(counts.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value)));
    }

    private JsonObject GatherHeld(JsonElement args, string principal, string actionId, string fingerprint, bool preview)
    {
        if (principal != PlayerPrincipal) throw new Refusal("permission_denied", "Only the player can do that, directly in the game.", "$.op");
        RequireChange(principal);
        var target = Str(args, "target")!;
        var held = _held.TryGetValue(PlayerAvatar, out var hand) && hand.Target == target ? hand : null;
        ObjectInstance item;
        if (held != null) item = held.Item;
        else
        {
            var entities = Entities();
            var entity = Find(entities, target) ?? throw new Refusal("target_not_found", "That is not in this room.", "$.args.target");
            item = Room.Objects.FirstOrDefault(o => o.Id == target) ?? throw new Refusal("request_invalid", GatherOnlyLogsAndStones, "$.args.target");
            if (GatherMaterial(item) == null) throw new Refusal("request_invalid", GatherOnlyLogsAndStones, "$.args.target");
            if (entity["protected"]!.GetValue<bool>()) throw new Refusal("target_protected", "That is protected. Only the player can unlock it.", "$.args.target");
            if (entity["held_by"] != null) throw new Refusal("target_busy", "Someone else is holding that.", "$.args.target");
            if (_held.ContainsKey(PlayerAvatar)) throw new Refusal("target_busy", "This avatar is already holding something. Put it down first.", "$.args.target");
            var limit = SandboxRules.CarryLimitKg(PlayerAvatar);
            if (item.Asset.MassKg > limit) throw TooHeavy("to pick up", item.Asset.MassKg, limit);
            if (Player == null || !IsInstanceValid(Player) || !Player.IsInsideTree()) throw new Refusal("not_ready", "The player is not in the room.", retryable: true);
            CheckReach(Player, item, BoundsOf(entity));
            CheckNothingOnIt(entities, entity);
        }
        var material = GatherMaterial(item) ?? throw new Refusal("request_invalid", GatherOnlyLogsAndStones, "$.args.target");
        var rules = InventoryRules ?? throw new Refusal("unsupported_capability", InventoryNotHere, "$.args.target");
        var kind = KindOfObject(rules, item) ?? throw new Refusal("unsupported_capability", $"This island's pockets hold no {(material == "wood" ? "logs" : "stones")}.", "$.args.target");
        if (IslandHeld) throw new Refusal("not_ready", TeamNotice);
        var pockets = PocketContents;
        var next = pockets.WithUnits(kind.Id, pockets.Units(kind.Id) + GatherUnits);
        CheckFits(next, rules, rules.Pockets, pockets: true, kind);
        if (_natureUnavailable) throw new Refusal("not_ready", NatureNotice, retryable: true);
        if (preview) return Previewed("item.gather", principal, actionId, target);
        var (position, rotation) = PoseOf(item, ObjectPoses(), carried: false);
        var candidate = Nature.Copy();
        candidate.Objects[target] = new NatureObject { Id = target, Burn = 1, Gathered = true, PositionM = position, Rotation = rotation };
        // The pockets gain first, then the island gives (one room revision); the hands are empty first, so the scene hides what left.
        // A refused revision puts the pockets back and gives a held one back to the hand (as MoveHeld does). The stack's slot becomes
        // the hand's selection in the same write (the founder's refinement: the player holds the stack's unit for immediate use).
        var selection = HandNow;
        var slot = next.SlotOf(kind.Id);
        SavePockets(next, hand: selection.Choosing(HandSelection.PocketsLayer, slot is <= HandSelection.MaxSlot ? slot : selection.Pockets));
        if (held != null) LetGo(PlayerAvatar);
        JsonObject result;
        try { result = CommitNature(candidate, "item.gather", principal, actionId, fingerprint, null, new[] { target }, new JsonObject(), undo: false); }
        catch (Refusal)
        {
            SavePockets(pockets, keepIfUnsaved: true, hand: selection);
            if (held != null && BodyOf(PlayerAvatar) is { } body) TakeHold(PlayerAvatar, body, held.Item, (held.RotationAtGrab, held.YawAtGrab));
            throw;
        }
        var name = item.DisplayName ?? item.Asset.DisplayName;
        var count = PocketContents.Units(kind.Id);
        var words = KernelJson.DisplayText(kind.Words(count), InventoryContents.MaxNameLength).ToLowerInvariant();
        var fact = Fact("gathered", $"You gathered {Quoted(name)}: {count} {words} in your pockets.", PlayerPrincipal, PlayerPrincipal, target, name, position);
        fact["quantity"] = new JsonObject { ["count"] = Math.Clamp(count, 0, 1_000_000) };
        AddHistory(fact);
        SaveTeam();
        return result;
    }
}
