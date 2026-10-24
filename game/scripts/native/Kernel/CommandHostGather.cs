using Godot;
using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Room;

namespace GarageGame.Native.Kernel;

/// <summary>
/// Gathering (decision 26 of docs/game/MECHANICS.md; contracts: item.gather): a loose log or stone the player is holding
/// goes into the team's wallet. Player-only (the companion is refused before anything is looked up, PlayerOnlyOps). The
/// held thing must be a room object that is a loose log or stone by what it is made of (GatherMaterial): wood for bark or
/// wood, stone for stone; anything else, or nothing held, is request_invalid. It leaves the island as one durable room
/// revision through the host's nature block: the object is gone as a burnt one is (hidden, no collision, a removed entity in
/// room state), marked gathered, and the wallet gains GatherUnits of its material. The wallet's counts show on
/// player.status; the journal keeps a 'gathered' fact. The key that sends it is the HUD's (M2).
/// </summary>
public partial class CommandHost
{
    /// <summary>What one loose log or stone adds to the wallet. Provisional: the design names no number (the founder's to set).</summary>
    public const int GatherUnits = 1;

    /// <summary>The wallet's material a room object would be gathered as, or null when it is no loose log or stone.</summary>
    public static string? GatherMaterial(ObjectInstance item)
    {
        var asset = item.Asset;
        if (!asset.Movable || asset.Affordances.Contains("container") || asset.Materials.Length == 0) return null;
        if (asset.Materials.All(m => m.Role is "bark" or "wood")) return "wood";
        if (asset.Materials.All(m => m.Role == "stone")) return "stone";
        return null;
    }

    /// <summary>The HUD's gather key: item.gather of what the player is holding (M2 binds the key).</summary>
    public JsonObject PlayerGather(bool preview = false) =>
        PlayerCommand("item.gather", new JsonObject { ["target"] = HeldBy(PlayerAvatar) ?? "obj:nothing" }, preview);

    /// <summary>The team's wallet as player.status counts it: the creation compiler's materials, sorted.</summary>
    private JsonObject WalletCounts() => new(new[] { "copper", "leaf", "stone", "wood" }
        .Select(material => System.Collections.Generic.KeyValuePair.Create(material, (JsonNode?)JsonValue.Create(Math.Clamp(Nature.Wallet.GetValueOrDefault(material), 0, NatureState.MaxWalletUnits)))));

    private JsonObject GatherHeld(JsonElement args, string principal, string actionId, string fingerprint, bool preview)
    {
        if (principal != PlayerPrincipal) throw new Refusal("permission_denied", "Only the player can do that, directly in the game.", "$.op");
        RequireChange(principal);
        var target = Str(args, "target")!;
        if (!_held.TryGetValue(PlayerAvatar, out var held) || held.Target != target)
            throw new Refusal("request_invalid", "Hold a loose log or stone to gather it: pick it up first.", "$.args.target");
        var material = GatherMaterial(held.Item)
            ?? throw new Refusal("request_invalid", "Only a loose log or stone can be gathered.", "$.args.target");
        if (preview) return Previewed("item.gather", principal, actionId, target);
        var (position, rotation) = PoseOf(held.Item, ObjectPoses(), carried: false);
        var candidate = Nature.Copy();
        candidate.Objects[target] = new NatureObject { Id = target, Burn = 1, Gathered = true, PositionM = position, Rotation = rotation };
        candidate.Wallet[material] = Math.Min(NatureState.MaxWalletUnits, candidate.Wallet.GetValueOrDefault(material) + GatherUnits);
        // The hands are empty first, so the scene hides what left; a refused save gives it back (as MoveHeld does).
        LetGo(PlayerAvatar);
        JsonObject result;
        try { result = CommitNature(candidate, "item.gather", principal, actionId, fingerprint, null, new[] { target }, new JsonObject(), undo: false); }
        catch (Refusal)
        {
            if (BodyOf(PlayerAvatar) is { } body) TakeHold(PlayerAvatar, body, held.Item, (held.RotationAtGrab, held.YawAtGrab));
            throw;
        }
        var name = held.Item.DisplayName ?? held.Item.Asset.DisplayName;
        var count = Nature.Wallet.GetValueOrDefault(material);
        var fact = Fact("gathered", $"You gathered {Quoted(name)}: {count} {material} in the wallet.", PlayerPrincipal, PlayerPrincipal, target, name, position);
        fact["quantity"] = new JsonObject { ["count"] = Math.Clamp(count, 0, 1_000_000) };
        AddHistory(fact);
        SaveTeam();
        return result;
    }
}
