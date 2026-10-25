using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Look;
using GarageGame.Native.Room;
using GarageGame.Native.Sandbox;

namespace GarageGame.Native.Kernel;

/// <summary>
/// P6, the world as data (Run 2): room state in the contract's form (contracts/room-state.schema.json), written from the
/// authority's state and read back into a room built from nothing; and the migration of a save between room manifests
/// (docs/engine/phase3/command-host.md), one step the player confirms, never automatic.
/// - Export: objects play changed (their pose, revision and lock; one that burned down is removed, at the pose it burned
///   at), creations (source, its hash, pose, owner, lock), the two avatars' identities, the durable receipts as contract
///   results, the journal and the discovered map. What the contract has no field for (compacted receipts, checkpoints,
///   the companion's role, the next creation number, the open tasks' goals, the island's nature) travels in x_play_
///   extensions, so a rebuild loses nothing.
/// - Import: into a host whose room has no play yet and the same manifest. The state becomes the authority's save envelope
///   and goes through the same load as a save (poses checked against the room, creations recompiled and re-placed, the
///   team and nature blocks checked), then is saved; the scene is derived from it as from any load.
/// </summary>
public partial class CommandHost
{
    public const string RoomStateBuild = "garage-game-native";

    /// <summary>
    /// The island's clock record (room state's extensions.x_clock, M6): kept as read from an imported room state and written back
    /// on export; the look's WorldTime reads it in place of the machine's clock (LookDirector.SetWorldClockFrom). Null: the machine's
    /// clock, today's behaviour. The island save keeps it too, in its team block's "clock" (CommandHostCamps).
    /// </summary>
    public JsonObject? ClockRecord { get; set; }

    /// <summary>Room state now, in the contract's form. Null while the save is not loaded.</summary>
    public JsonObject? ExportRoomState()
    {
        if (!Authority.Call(KernelNames.IsReady).AsBool()) return null;
        var envelope = Normalized(KernelJson.ToJson(Authority.Call(KernelNames.ExportEnvelope)))!;
        var entities = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        var locks = envelope["locks"]!.AsObject();
        var revisions = envelope["entity_revisions"]!.AsObject();
        var poses = envelope["object_poses"]?.AsObject() ?? new JsonObject();
        var receipts = envelope["receipts"]!.AsObject();
        JsonObject Protection(string id) => locks[id] is JsonObject held
            ? new JsonObject { ["locked"] = true, ["locked_by"] = held["locked_by"]!.GetValue<string>(), ["locked_revision"] = held["locked_revision"]!.GetValue<int>() }
            : new JsonObject { ["locked"] = false };
        var burned = Nature.Objects.Values.Where(o => o.BurnedDown).ToDictionary(o => o.Id, StringComparer.Ordinal);
        foreach (var id in revisions.Select(p => p.Key).Concat(locks.Select(p => p.Key)).Concat(poses.Select(p => p.Key)).Concat(burned.Keys)
            .Where(i => i.StartsWith("obj:", StringComparison.Ordinal)).Distinct())
        {
            var item = Room.Objects.First(o => o.Id == id);
            var entity = new JsonObject
            {
                ["id"] = id, ["kind"] = "object", ["revision"] = Math.Max(1, revisions[id]?.GetValue<int>() ?? 1),
                ["provenance"] = new JsonObject { ["kind"] = item.Asset.ProvenanceKind, ["created_by"] = "system:room", ["created_revision"] = 0 },
                ["protection"] = Protection(id),
            };
            if (poses[id] is JsonObject pose) entity["transform"] = new JsonObject { ["position_m"] = pose["position_m"]!.DeepClone(), ["rotation"] = pose["rotation"]!.DeepClone() };
            // Burned down: gone from the room, kept as removed at the pose it burned at (the manifest is never edited).
            if (burned.TryGetValue(id, out var ash))
            {
                entity["removed"] = true;
                var turn = ash.Rotation ?? item.Rotation;
                entity["transform"] ??= new JsonObject
                {
                    ["position_m"] = KernelJson.Vector(ash.PositionM ?? item.PositionM),
                    ["rotation"] = new JsonArray(KernelJson.Num(turn.X), KernelJson.Num(turn.Y), KernelJson.Num(turn.Z), KernelJson.Num(turn.W)),
                };
            }
            entities[id] = entity;
        }
        foreach (var node in envelope["instances"]!.AsArray())
        {
            var instance = node!.AsObject();
            var id = instance["id"]!.GetValue<string>();
            var owner = instance["owner_id"]!.GetValue<string>();
            var half = CanonicalYaw(instance["yaw_deg"]!.GetValue<double>()) * Math.PI / 360.0;
            var created = receipts.Select(r => r.Value!["receipt"]!).FirstOrDefault(r => r["created"]!.AsArray().Any(c => c!.GetValue<string>() == id))?["revision"]?.GetValue<int>() ?? 0;
            entities[id] = new JsonObject
            {
                ["id"] = id, ["kind"] = "creation", ["revision"] = instance["revision"]!.GetValue<int>(),
                ["transform"] = new JsonObject { ["position_m"] = instance["position_m"]!.DeepClone(), ["rotation"] = new JsonArray(0, Math.Sin(half), 0, Math.Cos(half)) },
                ["creation"] = new JsonObject { ["source"] = instance["source"]!.DeepClone(), ["source_sha256"] = CanonicalJson.Sha256Hex(CanonicalJson.Bytes(instance["source"]!)) },
                ["provenance"] = new JsonObject { ["kind"] = owner == CompanionPrincipal ? "ai_created" : "player_created", ["created_by"] = owner, ["created_revision"] = created },
                ["protection"] = Protection(id),
            };
        }
        foreach (var (id, body, role, name) in new (string, SmallPlayerController?, string, string)[] { (PlayerAvatar, Player, "player", "Player"), (CompanionAvatarId, Companion, "companion", Companion?.CompanionName ?? "Companion") })
        {
            if (body == null || !IsInstanceValid(body)) continue;
            entities[id] = new JsonObject
            {
                ["id"] = id, ["kind"] = "avatar", ["revision"] = 1,
                ["avatar"] = new JsonObject { ["role"] = role, ["display_name"] = KernelJson.DisplayText(name, 40), ["appearance"] = Appearance(body) },
                ["provenance"] = new JsonObject { ["kind"] = "hand_authored", ["created_by"] = "system:room", ["created_revision"] = 0 },
                ["protection"] = new JsonObject { ["locked"] = false },
            };
        }
        var results = new JsonObject();
        foreach (var (key, record) in receipts.OrderBy(p => p.Key, StringComparer.Ordinal))
            results[key] = new JsonObject
            {
                ["fingerprint"] = record!["fingerprint"]!.GetValue<string>(),
                ["result"] = Durable(Authority.Call(KernelNames.ReceiptFor, record["principal"]!.GetValue<string>(), record["action_id"]!.GetValue<string>()).AsGodotDictionary(), replayed: false),
            };
        var style = Style ?? StylePreset.Resolve(RoomWorld.DefaultStyleId, RoomWorld.DefaultStyleVersion);
        var extensions = new JsonObject
        {
            ["x_play_companion_role"] = envelope["roles"]![CompanionPrincipal]!.GetValue<string>(),
            ["x_play_next_creation"] = envelope["next_id"]!.GetValue<int>(),
            ["x_play_compacted"] = envelope["compacted"]?.DeepClone() ?? new JsonObject(),
            ["x_play_checkpoints"] = envelope["checkpoints"]?.DeepClone() ?? new JsonArray(),
            ["x_play_task_goals"] = new JsonObject(_taskGoals.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(p.Value)))),
        };
        // The island's nature as it is now (the simulation runs ahead of its last save), where this room has any.
        if (NatureConfigured || envelope["nature"] is JsonObject { Count: > 0 }) extensions["x_play_nature"] = NatureBlock.Pack(Nature);
        // The island's clock (M6), where it has one.
        if (ClockRecord != null) extensions["x_clock"] = ClockRecord.DeepClone();
        // Fire's camp: the start spots, the lit campfires and the camp the player wakes at (CommandHostCamps), where there are any.
        if (CampBlock() is { } camp) extensions["x_play_camp"] = camp;
        // Whose island save this is and how it plays (M3: the save's team block).
        if (XTeam() is { } whose) extensions["x_team"] = whose;
        return new JsonObject
        {
            ["schema"] = SchemaIds.RoomState, ["version"] = 1, ["room_id"] = Room.RoomId,
            ["room_pin"] = new JsonObject { ["manifest_sha256"] = Room.ManifestSha256 },
            ["style_pin"] = new JsonObject { ["preset_id"] = style.PresetId, ["preset_version"] = style.PresetVersion, ["preset_sha256"] = style.Sha256 },
            ["rules"] = new JsonObject { ["command_version"] = 1, ["compiler_version"] = 1 },
            ["store_revision"] = Revision,
            ["session"] = new JsonObject
            {
                ["session_id"] = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(), ["written_utc"] = Now(), ["build"] = RoomStateBuild,
            },
            ["entities"] = new JsonObject(entities.Select(p => KeyValuePair.Create(p.Key, p.Value))),
            ["receipts"] = results,
            ["journal"] = ExportJournal(),
            ["discovered"] = ExportDiscovered(),
            ["extensions"] = extensions,
        };
    }

    /// <summary>The outcome of a rebuild or a migration: whether it happened, and what the player is told.</summary>
    public sealed record RoomStateOutcome(bool Ok, string Message, IReadOnlyList<string> Kept, IReadOnlyList<string> LeftBehind);

    private static RoomStateOutcome Refused(string message) => new(false, message, Array.Empty<string>(), Array.Empty<string>());

    /// <summary>The largest earlier save a migration reads: the authority's own save limit.</summary>
    public const int MaxSaveBytes = 4 * 1024 * 1024;
    private static readonly System.Text.RegularExpressions.Regex Hex64 = new(@"\A[0-9a-f]{64}\z", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex Hex16 = new(@"\A[0-9a-f]{16}\z", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex ColorHex = new(@"\A#[0-9a-fA-F]{6}\z", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// An avatar's appearance as room state writes it: its colour, and the drawn character it wears when it wears one
    /// (room-state.schema.json's optional appearance.body, a token; the characters round).
    /// </summary>
    private static JsonObject Appearance(SmallPlayerController body) => WithBody(new JsonObject { ["color"] = "#" + body.AppearanceColor.ToHtml(false) }, body);

    /// <summary>Adds the body's drawn character, when it has one, as "body".</summary>
    private static JsonObject WithBody(JsonObject identity, SmallPlayerController body)
    {
        if (body.CharacterId.Length > 0) identity["body"] = body.CharacterId;
        return identity;
    }

    /// <summary>A colour, and at most an optional body token beside it.</summary>
    private static bool AppearanceIsValid(JsonObject look) =>
        look.Count == (look.ContainsKey("body") ? 2 : 1) && StrOf(look["color"]) is { } color && ColorHex.IsMatch(color) && BodyIsValid(look);

    /// <summary>
    /// No "body" key, or a token string under it (room-state.schema.json's optional appearance.body, common.schema.json's token):
    /// a key present with null, or any other value, is refused.
    /// </summary>
    private static bool BodyIsValid(JsonObject holder) =>
        !holder.TryGetPropertyValue("body", out var body) || (StrOf(body) is { } token && SmallPlayerController.IsCharacterToken(token));
    private static readonly System.Text.RegularExpressions.Regex SessionHex = new(@"\A[a-f0-9]{16,64}\z", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex CheckpointId = new(@"\Acp[0-9]{4,9}\z", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex HostCreationId = new(@"\Acreation:[0-9]{8}\z", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex MetaOp = new(@"\A[a-z]+\.[a-z_]+\z", System.Text.RegularExpressions.RegexOptions.Compiled);
    /// <summary>The ops whose receipts are durable (contracts/README.md "Receipts"): the nature changes and their undo among them.</summary>
    private static readonly HashSet<string> DurableOps = new()
    {
        "creation.place", "creation.revise", "entity.remove", "protect.lock", "protect.unlock", "room.checkpoint", "entity.release", "entity.place", "entity.push", "journal.note",
        "nature.cast", "nature.trail", "nature.repair", "room.undo", "item.pickup", "item.gather",
        // P-Inventory: the chest and a thing held away are the island's room state (CommandHostInventory.CommitInventory).
        "inventory.store", "inventory.take", "inventory.drop",
    };
    /// <summary>The durable ops whose receipts keep the data their first answer had (the authority's NATURE_OPS).</summary>
    private static readonly HashSet<string> NatureOps = new() { "nature.cast", "nature.trail", "nature.repair", "room.undo" };
    /// <summary>The authority keeps a pickup's receipt by the nature path, with empty data: the contract's result carries none.</summary>
    private static bool KeptWithData(string op) => NatureOps.Contains(op) || op is "item.pickup" or "item.gather" || InventoryDurableOps.Contains(op);

    /// <summary>One canonical angle: a yaw in (-180, 180], so a half-turn either way is the same bytes everywhere.</summary>
    private static double CanonicalYaw(double yaw)
    {
        while (yaw > 180.0) yaw -= 360.0;
        while (yaw <= -180.0) yaw += 360.0;
        return yaw;
    }

    private static string? StrOf(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
    private static bool Bool(JsonNode? node) => node?.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
    private static bool Quat(JsonNode? node) => node is JsonArray q && q.Count == 4 && q.All(v => Number(v, 1)) &&
        Math.Abs(Math.Sqrt(q.Sum(v => v!.GetValue<double>() * v.GetValue<double>())) - 1) <= 1e-3;
    private static bool EntityIds(JsonNode? node, int max = 64) => node is JsonArray list && list.Count <= max && list.All(x => StrOf(x) is { } id && EntityId.IsMatch(id));

    /// <summary>
    /// Whether this room is genuinely fresh: nothing saved for it, no command of any kind (durable or transient), no goal,
    /// nothing held, no request waiting, an empty journal, nature as it began; and, unless sight is allowed, nothing seen
    /// yet and no flower planted by the trail either (review major 4).
    /// </summary>
    private bool Untouched(bool sightAllowed) =>
        Authority.Call(KernelNames.IsReady).AsBool() && !Godot.FileAccess.FileExists(SavePath) && Revision == 0 && Authority.Call(KernelNames.ReceiptCount).AsInt32() == 0 &&
        _transient.Count == 0 && _jobs.Count == 0 && _runningGoals.Count == 0 && _held.Count == 0 && _approvals.Count == 0 &&
        _openTasks.Count == 0 && _history.Count == 0 && _notes.Count == 0 && Nature.Untouched(trailAllowed: sightAllowed, StartWallet) &&
        (sightAllowed || (TeamMemory().Entries.Count == 0 && _levels.Values.All(l => l.Count == 0)));

    // ---- rebuild from room state (P6) ----

    /// <summary>
    /// Rebuild this room from room state, into a genuinely fresh room with the same manifest and style. Room state is
    /// untrusted input, like a save: it is checked whole before anything is translated (types, ranges, required fields,
    /// receipts that are durable and succeeded, payload ids that match their keys, identities that can be restored), then
    /// the authority checks the translation again as a save, saves it, and only then believes it; the scene, the team's
    /// journal and map and the avatars' identities follow. Anything wrong refuses the whole state and changes nothing.
    /// Never throws.
    /// </summary>
    public RoomStateOutcome ImportRoomState(JsonObject given)
    {
        try
        {
            if (!Untouched(sightAllowed: false)) return Refused("Only a room with no play yet is rebuilt from room state.");
            if (Normalized(given) is not { } state) return Refused("That is not room state.");
            if (RoomStateProblem(state) is { } problem) return Refused("That room state cannot be rebuilt: " + problem + ".");
            ClockRecord = (state["extensions"]?["x_clock"] as JsonObject)?.DeepClone() as JsonObject;
            var envelope = EnvelopeOf(state);
            return Adopt(envelope, envelope["instances"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()).ToList(), Array.Empty<string>(),
                "The room was rebuilt from its state.", closeOpenTasks: false);
        }
        catch (Exception error)
        {
            return Refused("That room state cannot be rebuilt (" + error.GetType().Name + ").");
        }
    }

    /// <summary>What is wrong with room state for this room (null when nothing is): the contract's shape and what this host can restore.</summary>
    private string? RoomStateProblem(JsonObject state)
    {
        var top = new HashSet<string> { "schema", "version", "room_id", "room_pin", "style_pin", "rules", "store_revision", "session", "entities", "receipts", "checkpoints", "journal", "discovered", "extensions" };
        if (state.Any(p => !top.Contains(p.Key))) return "a field room state does not have";
        if (!SchemaIds.IsSchema(StrOf(state["schema"]), "room_state") || !Whole(state["version"]) || state["version"]!.GetValue<double>() != 1) return "not room state, version 1";
        if (StrOf(state["room_id"]) != Room.RoomId) return "another room";
        if (state["room_pin"] is not JsonObject pin || pin.Count != 1 || StrOf(pin["manifest_sha256"]) != Room.ManifestSha256)
            return "another version of this room (it migrates instead)";
        var style = Style ?? StylePreset.Resolve(RoomWorld.DefaultStyleId, RoomWorld.DefaultStyleVersion);
        if (state["style_pin"] is not JsonObject stylePin || stylePin.Count != 3 || StrOf(stylePin["preset_id"]) != style.PresetId ||
            !Whole(stylePin["preset_version"]) || stylePin["preset_version"]!.GetValue<double>() != style.PresetVersion || StrOf(stylePin["preset_sha256"]) != style.Sha256)
            return "a style this room does not use (a style cannot be restored from room state)";
        if (state["rules"] is not JsonObject rules || rules.Count != 2 || !Whole(rules["command_version"]) || rules["command_version"]!.GetValue<double>() != 1 ||
            !Whole(rules["compiler_version"]) || rules["compiler_version"]!.GetValue<double>() != 1) return "other rules";
        if (!Whole(state["store_revision"], int.MaxValue)) return "the store revision";
        var store = (int)state["store_revision"]!.GetValue<double>();
        if (state["session"] is not JsonObject session || session.Count != 3 || StrOf(session["session_id"]) is not { } sid || !SessionHex.IsMatch(sid) ||
            !RealTime(session["written_utc"]) || !Text(session["build"], 80)) return "the session";
        if (state["checkpoints"] is JsonArray { Count: > 0 }) return "checkpoints with files (this host keeps them in x_play_checkpoints)";
        if (state["checkpoints"] is { } cps && cps is not JsonArray) return "the checkpoints";
        if (state["entities"] is not JsonObject entities || entities.Count > 2048) return "the entities";
        // Nature first: an object that burned down is removed in entities, and only nature can say it did.
        var nature = state["extensions"]?["x_play_nature"];
        if (nature != null && NatureBlock.Problem(nature, store, Room) is { } natureProblem) return "the island's nature: " + natureProblem;
        var burned = nature is JsonObject block ? block["objects"]!.AsObject().Where(p => p.Key.StartsWith("obj:", StringComparison.Ordinal) && p.Value!["burn"]!.GetValue<double>() >= 1)
            .Select(p => p.Key).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, node) in entities)
            if (EntityStateProblem(key, node, store, burned) is { } why) return $"entity {KernelJson.DisplayText(key, 70)}: {why}";
        if (burned.Any(id => entities[id]?["removed"]?.GetValueKind() != JsonValueKind.True)) return "an object nature burned down that entities still list";
        if (state["receipts"] is not JsonObject receipts || receipts.Count > 2048) return "the receipts";
        foreach (var (key, node) in receipts)
            if (ReceiptProblem(key, node, store) is { } why) return $"receipt {KernelJson.DisplayText(key, 100)}: {why}";
        var team = new JsonObject();
        if (state["journal"] is { } journal) team["journal"] = journal.DeepClone();
        if (state["discovered"] is { } discovered) team["discovered"] = discovered.DeepClone();
        if (TeamProblem(team, store) is { } teamProblem) return "the team's journal or map: " + teamProblem;
        if (state["extensions"] is { } extensions && ExtensionsProblem(extensions, store) is { } extensionProblem) return "an extension: " + extensionProblem;
        return null;
    }

    private string? EntityStateProblem(string key, JsonNode? node, int store, HashSet<string> burned)
    {
        if (!EntityId.IsMatch(key) || node is not JsonObject entity) return "its id";
        var allowed = new HashSet<string> { "id", "kind", "revision", "removed", "transform", "parts", "creation", "avatar", "protection", "provenance" };
        if (entity.Any(p => !allowed.Contains(p.Key))) return "a field it does not have";
        if (StrOf(entity["id"]) != key) return "a payload that names another id";
        var kind = StrOf(entity["kind"]);
        var expected = key.StartsWith("obj:", StringComparison.Ordinal) ? "object" : key.StartsWith("creation:", StringComparison.Ordinal) ? "creation" : key.StartsWith("avatar:", StringComparison.Ordinal) ? "avatar" : null;
        if (kind == null || kind != expected) return "a kind its id does not allow";
        if (!Whole(entity["revision"], Math.Max(store, 1)) || entity["revision"]!.GetValue<double>() < 1) return "its revision";
        // A removal this host restores is an object that burned down, as the island's nature records it.
        if (entity["removed"] is { } removed && (!Bool(removed) || (removed.GetValue<bool>() && (kind != "object" || !burned.Contains(key))))) return "a removal this host cannot restore";
        if (entity["parts"] != null) return "parts this host cannot restore";
        if (entity["provenance"] is not JsonObject provenance || provenance.Count != 3 || !ProvenanceKinds.Contains(StrOf(provenance["kind"]) ?? "") ||
            StrOf(provenance["created_by"]) is not { } by || !System.Text.RegularExpressions.Regex.IsMatch(by, @"\A(player|companion|guest|guest_companion|system):[a-z0-9_-]{1,32}\z") ||
            !Whole(provenance["created_revision"], store)) return "its provenance";
        if (entity["protection"] is not JsonObject protection || !Bool(protection["locked"]) || protection.Any(p => p.Key is not ("locked" or "locked_by" or "locked_revision" or "support_ids")))
            return "its protection";
        if (protection["support_ids"] != null) return "support ids this host cannot restore";
        var locked = protection["locked"]!.GetValue<bool>();
        if (locked && (!Principals.Contains(StrOf(protection["locked_by"]) ?? "") || !Whole(protection["locked_revision"], store) || protection["locked_revision"]!.GetValue<double>() < 1))
            return "its lock";
        if (!locked && (protection["locked_by"] != null || protection["locked_revision"] != null)) return "its lock";
        if (entity["transform"] is { } transform && (transform is not JsonObject t || t.Any(p => p.Key is not ("position_m" or "rotation" or "scale")) ||
            !Vec3(t["position_m"]) || !Quat(t["rotation"]) || (t["scale"] != null && (!Number(t["scale"], 100) || t["scale"]!.GetValue<double>() != 1)))) return "its transform";
        switch (kind)
        {
            case "object":
                if (Room.Objects.All(o => o.Id != key)) return "an object not in this room";
                if (entity["creation"] != null || entity["avatar"] != null) return "fields an object does not have";
                break;
            case "creation":
                if (!HostCreationId.IsMatch(key)) return "an id the host did not assign";
                if (entity["avatar"] != null || entity["transform"] is not JsonObject placed) return "its placement";
                var q = placed["rotation"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
                if (Math.Abs(q[0]) > 1e-4 || Math.Abs(q[2]) > 1e-4) return "a creation that does not stand upright";
                if (!Principals.Contains(StrOf(entity["provenance"]!["created_by"]) ?? "")) return "an owner that is not the player or the companion";
                if (entity["creation"] is not JsonObject creation || creation.Any(p => p.Key is not ("source" or "source_sha256")) || creation["source"] is not JsonObject source ||
                    !SchemaIds.IsSchema(StrOf(source["schema"]), "creation") || StrOf(creation["source_sha256"]) is not { } sha || !Hex64.IsMatch(sha)) return "its source";
                if (CanonicalJson.Sha256Hex(CanonicalJson.Bytes(source)) != sha) return "a source that does not match its hash";
                break;
            case "avatar":
                if (key is not (PlayerAvatar or CompanionAvatarId)) return "an avatar this room does not have";
                if (entity["transform"] != null || entity["creation"] != null || locked) return "fields an avatar does not have";
                if (entity["avatar"] is not JsonObject avatar || avatar.Count != 3 || StrOf(avatar["role"]) != (key == PlayerAvatar ? "player" : "companion") ||
                    !Text(avatar["display_name"], 40) || avatar["appearance"] is not JsonObject look || !AppearanceIsValid(look))
                    return "its identity";
                if (key == PlayerAvatar && StrOf(avatar["display_name"]) != "Player") return "a player's name this host cannot restore";
                break;
        }
        return null;
    }

    /// <summary>A receipt as room state keeps it: a durable result that succeeded, under its own principal and action id, in this room.</summary>
    private string? ReceiptProblem(string key, JsonNode? node, int store)
    {
        if (node is not JsonObject receipt || receipt.Count != 2 || StrOf(receipt["fingerprint"]) is not { } fingerprint || !Hex64.IsMatch(fingerprint) || receipt["result"] is not JsonObject result)
            return "its shape";
        var allowed = new HashSet<string> { "schema", "version", "ok", "op", "principal", "room_id", "revision", "replayed", "preview", "transient", "at_utc", "action_id", "affected", "created", "approved_by", "data" };
        if (result.Any(p => !allowed.Contains(p.Key))) return "a result field a durable receipt does not have";
        if (!SchemaIds.IsSchema(StrOf(result["schema"]), "result") || !Whole(result["version"]) || result["version"]!.GetValue<double>() != 1) return "not a result";
        if (result["ok"]?.GetValueKind() != JsonValueKind.True) return "a result that failed";
        if (result["transient"]?.GetValueKind() != JsonValueKind.False) return "a transient result";
        if (result["replayed"]?.GetValueKind() != JsonValueKind.False || result["preview"]?.GetValueKind() != JsonValueKind.False) return "a replay or a preview";
        var op = StrOf(result["op"]);
        if (op == null || !DurableOps.Contains(op)) return "an op without a durable receipt";
        var principal = StrOf(result["principal"]);
        var action = StrOf(result["action_id"]);
        if (principal == null || !Principals.Contains(principal) || action == null || !ActionId.IsMatch(action) || key != principal + "|" + action) return "a key that is not its principal and action id";
        if (StrOf(result["room_id"]) != Room.RoomId) return "another room";
        if (!Whole(result["revision"], store)) return "its revision";
        if (result["at_utc"] != null && !RealTime(result["at_utc"])) return "its time";
        if (result["approved_by"] is { } approved && (StrOf(approved) != PlayerPrincipal || principal == PlayerPrincipal)) return "an approval";
        if ((result["affected"] != null && !EntityIds(result["affected"])) || (result["created"] != null && !EntityIds(result["created"]))) return "what it touched";
        if (op == "room.checkpoint" ? result["data"] is not JsonObject { Count: 1 } c || !Whole(c["checkpoint_revision"], store)
            : op == "journal.note" ? result["data"] is not JsonObject { Count: 1 } n || StrOf(n["entry_id"]) is not { } entry || !EntryIdPattern.IsMatch(entry)
            : NatureOps.Contains(op) ? NatureDataProblem(op, result["data"])
            : InventoryDurableOps.Contains(op) ? InventoryDataProblem(result["data"])
            : result["data"] != null) return "its data";
        if (op == "item.pickup" && (result["affected"] is not JsonArray { Count: 1 } picked || StrOf(picked[0]) is not { } fractal || !(FractalId.IsMatch(fractal) || FoodSourceId.IsMatch(fractal))))
            return "a pickup that names no Fractal or plant";
        if (op == "item.gather" && (result["affected"] is not JsonArray { Count: 1 } gathered || StrOf(gathered[0]) is not { } thing || !ObjectIdPattern.IsMatch(thing)))
            return "a gather that names no object";
        return null;
    }

    /// <summary>A nature change's receipt data, as this host writes it: each op's own fields, each of its type and range.</summary>
    private static bool NatureDataProblem(string op, JsonNode? node)
    {
        if (node is not JsonObject data) return true;
        var fields = op switch
        {
            "nature.cast" => new[] { "center_m", "radius_m", "duration_s", "cell_count", "requires_approval", "started_cells", "max_burning_cells", "grown_plants", "kind", "end_m" },
            "nature.trail" => new[] { "enabled", "requires_approval" },
            // Decision 131: a paid repair's receipt says what it spent, by kind (validated as its cost is, below).
            "nature.repair" => new[] { "cost", "spent" },
            _ => new[] { "undone", "undone_steps", "requires_approval" },
        };
        if (data.Any(p => !fields.Contains(p.Key))) return true;
        foreach (var (name, value) in data)
        {
            var fine = name switch
            {
                "center_m" or "end_m" => Vec3(value),
                // Fire's kinds (decisions 77 to 80): a durable receipt names a Fireline; the others' receipts are transient or plain. Grow's
                // unlocks (decisions 104 to 107): a durable receipt names sprouts, a replanting, a bridge or ivy (CommandHostGrowKinds).
                "kind" => value?.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is "fire" or "firepit" or "fireline" or "snuff" or "will_o_wisp" or
                    "sprouts" or "replant" or "bridge" or "ivy",
                "radius_m" => Number(value, NatureBlock.MaxFireRadiusM) && value!.GetValue<double>() > 0,
                "duration_s" => Number(value, 600) && value!.GetValue<double>() > 0,
                "cell_count" or "started_cells" or "max_burning_cells" => Whole(value, NatureState.MaxCells),
                "undone_steps" => Whole(value, NatureState.MaxUndoSteps),
                "grown_plants" => Whole(value, NatureState.MaxGrownPlants),
                "requires_approval" or "enabled" => Bool(value),
                "undone" => Text(value, 200),
                _ => value is JsonObject cost && cost.Count <= NatureState.MaxWalletKinds &&
                    cost.All(p => Token.IsMatch(p.Key) && Whole(p.Value, NatureState.MaxWalletUnits) && p.Value!.GetValue<double>() >= 1),
            };
            if (!fine) return true;
        }
        return false;
    }

    private string? ExtensionsProblem(JsonNode extensions, int store)
    {
        if (extensions is not JsonObject x || x.Count > 32) return "the block";
        if (x["x_play_companion_role"] is { } role && StrOf(role) is not ("editor" or "visitor")) return "the companion's role";
        if (x["x_play_next_creation"] is { } next && (!Whole(next, 99999999) || next.GetValue<double>() < 1)) return "the next creation number";
        if (x["x_play_compacted"] is { } compacted)
        {
            if (compacted is not JsonObject map || map.Count > 4096) return "the compacted receipts";
            foreach (var (key, value) in map)
            {
                var parts = key.Split('|');
                if (parts.Length != 2 || !Principals.Contains(parts[0]) || !ActionId.IsMatch(parts[1]) || value is not JsonArray pair || pair.Count != 2 ||
                    !Whole(pair[0], store) || StrOf(pair[1]) is not { } prefix || !Hex16.IsMatch(prefix)) return "a compacted receipt";
            }
        }
        if (x["x_play_checkpoints"] is { } checkpoints && (checkpoints is not JsonArray list || list.Count > 16 || list.Any(c => c is not JsonObject cp || cp.Count != 3 ||
            StrOf(cp["id"]) is not { } id || !CheckpointId.IsMatch(id) || !Whole(cp["revision"], store) || StrOf(cp["label"]) is not { } label ||
            KernelText.CodePoints(label).Length > 80 || KernelText.HasHidden(label)))) return "a checkpoint";
        if (x["x_play_task_goals"] is { } goals && (goals is not JsonObject g || g.Any(p => !EntryIdPattern.IsMatch(p.Key) || !TaskGoals.Contains(StrOf(p.Value) ?? "")))) return "an open task's goal";
        if (x["x_team"] is { } whose && XTeamProblem(whose) is { } who) return who;
        if (x["x_play_camp"] is { } camp && CampBlockProblem(camp) is { } campfires) return campfires;
        // x_play_nature is checked whole before the entities (RoomStateProblem).
        return null;
    }

    /// <summary>The authority's save envelope (version 5) for room state already checked by RoomStateProblem.</summary>
    private JsonObject EnvelopeOf(JsonObject state)
    {
        var extensions = state["extensions"] as JsonObject ?? new JsonObject();
        var instances = new JsonArray();
        var locks = new JsonObject();
        var revisions = new JsonObject();
        var poses = new JsonObject();
        var avatars = new JsonObject();
        foreach (var (id, node) in state["entities"]!.AsObject())
        {
            var entity = node!.AsObject();
            var kind = entity["kind"]!.GetValue<string>();
            if (entity["protection"]!["locked"]!.GetValue<bool>())
                locks[id] = new JsonObject { ["locked_by"] = entity["protection"]!["locked_by"]!.GetValue<string>(), ["locked_revision"] = (int)entity["protection"]!["locked_revision"]!.GetValue<double>() };
            if (kind == "object")
            {
                var item = Room.Objects.First(o => o.Id == id);
                revisions[id] = (int)entity["revision"]!.GetValue<double>();
                if (entity["transform"] is not JsonObject transform) continue;
                var position = ReadNodeVector(transform["position_m"]!);
                var r = transform["rotation"]!.AsArray().Select(v => (float)v!.GetValue<double>()).ToArray();
                var bounds = SandboxPhysics.Bounds(position, new Quaternion(r[0], r[1], r[2], r[3]).Normalized(), item.Asset.DimensionsM * item.Scale);
                poses[id] = new JsonObject
                {
                    ["position_m"] = transform["position_m"]!.DeepClone(), ["rotation"] = transform["rotation"]!.DeepClone(),
                    ["bounds"] = new JsonObject { ["min_m"] = ExactNode(bounds.Position), ["max_m"] = ExactNode(bounds.End) },
                };
            }
            else if (kind == "creation")
            {
                var q = entity["transform"]!["rotation"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
                instances.Add(new JsonObject
                {
                    ["id"] = id, ["owner_id"] = entity["provenance"]!["created_by"]!.GetValue<string>(), ["revision"] = (int)entity["revision"]!.GetValue<double>(),
                    ["source"] = entity["creation"]!["source"]!.DeepClone(), ["position_m"] = entity["transform"]!["position_m"]!.DeepClone(),
                    ["yaw_deg"] = CanonicalYaw(Math.Round(2.0 * Math.Atan2(q[1], q[3]) * 180.0 / Math.PI, 9)),
                });
            }
            else
            {
                avatars[id] = new JsonObject { ["display_name"] = entity["avatar"]!["display_name"]!.GetValue<string>(), ["color"] = entity["avatar"]!["appearance"]!["color"]!.GetValue<string>().ToLowerInvariant() };
                // The drawn character the avatar wore (room-state.schema.json's optional appearance.body), when it wore one.
                if (StrOf(entity["avatar"]!["appearance"]!["body"]) is { } wore) avatars[id]!["body"] = wore;
            }
        }
        var receipts = new JsonObject();
        foreach (var (key, node) in state["receipts"]!.AsObject())
        {
            var result = node!["result"]!.AsObject();
            var op = result["op"]!.GetValue<string>();
            string Id(string list) => result[list]?.AsArray().Select(x => x!.GetValue<string>()).FirstOrDefault(x => x.StartsWith("creation:", StringComparison.Ordinal)) ?? "";
            var receipt = new JsonObject
            {
                ["ok"] = true, ["instance_id"] = op == "creation.place" ? Id("created") : Id("affected"), ["revision"] = (int)result["revision"]!.GetValue<double>(),
                ["permission_revision"] = 0, ["replayed"] = false,
                ["affected"] = result["affected"]?.DeepClone() ?? new JsonArray(), ["created"] = result["created"]?.DeepClone() ?? new JsonArray(),
            };
            if (op == "journal.note") receipt["entry_id"] = result["data"]!["entry_id"]!.GetValue<string>();
            if (NatureOps.Contains(op) || InventoryDurableOps.Contains(op)) receipt["data"] = result["data"]!.DeepClone();
            if (op == "item.pickup")
            {
                // As the host committed it (CommandHostFractals): the Fractal in the receipt's data, nothing the authority checks as an entity.
                // P-Food: a pick of food names its plant ("food", CommandHostFood).
                var picked = result["affected"]![0]!.GetValue<string>();
                receipt["data"] = new JsonObject { [FoodSourceId.IsMatch(picked) ? "food" : "fractal"] = picked };
                receipt["affected"] = new JsonArray();
            }
            // A gather's receipt names its object in affected, with empty data (CommandHostGather).
            if (op == "item.gather") receipt["data"] = new JsonObject();
            receipts[key] = new JsonObject
            {
                ["principal"] = result["principal"]!.GetValue<string>(), ["action_id"] = result["action_id"]!.GetValue<string>(), ["fingerprint"] = node["fingerprint"]!.GetValue<string>(),
                ["receipt"] = receipt,
                ["meta"] = new JsonObject { ["op"] = op, ["at_utc"] = StrOf(result["at_utc"]) ?? "", ["approved_by"] = StrOf(result["approved_by"]) ?? "" },
            };
        }
        var next = extensions["x_play_next_creation"] is { } given ? (int)given.GetValue<double>() :
            instances.Select(i => int.Parse(i!["id"]!.GetValue<string>()["creation:".Length..], System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max() + 1;
        var team = new JsonObject
        {
            ["journal"] = state["journal"]?.DeepClone() ?? new JsonObject { ["open_tasks"] = new JsonArray(), ["history"] = new JsonArray(), ["notes"] = new JsonArray() },
            ["discovered"] = state["discovered"]?.DeepClone() ?? new JsonObject { ["cell_m"] = DiscoverCellM, ["levels"] = new JsonArray(), ["entities"] = new JsonObject() },
            ["task_goals"] = extensions["x_play_task_goals"]?.DeepClone() ?? new JsonObject(),
            ["avatars"] = avatars,
        };
        // Whose save it is (x_team) goes into the save's team block (M3).
        CopyXTeam(extensions, team);
        // Fire's camp and the island's clock go there too (CommandHostCamps): the island save keeps them.
        if (extensions["x_play_camp"] is JsonObject camp) team["camp"] = camp.DeepClone();
        if (extensions["x_clock"] is JsonObject clock) team["clock"] = clock.DeepClone();
        return new JsonObject
        {
            ["schema"] = SchemaIds.CreationWorld, ["version"] = 6, ["compiler_version"] = 1, ["style_version"] = "painterly_v1",
            ["room_pin"] = new JsonObject { ["room_id"] = Room.RoomId, ["manifest_sha256"] = Room.ManifestSha256 },
            ["revision"] = (int)state["store_revision"]!.GetValue<double>(), ["permission_revision"] = 0, ["next_id"] = next,
            ["roles"] = new JsonObject { [PlayerPrincipal] = "owner", [CompanionPrincipal] = StrOf(extensions["x_play_companion_role"]) ?? "editor" },
            ["consent"] = new JsonObject { [PlayerPrincipal] = false, [CompanionPrincipal] = false },
            ["instances"] = instances, ["receipts"] = receipts,
            ["compacted"] = extensions["x_play_compacted"]?.DeepClone() ?? new JsonObject(),
            ["checkpoints"] = extensions["x_play_checkpoints"]?.DeepClone() ?? new JsonArray(),
            ["locks"] = locks, ["entity_revisions"] = revisions, ["object_poses"] = poses, ["team"] = team,
            ["nature"] = extensions["x_play_nature"]?.DeepClone() ?? new JsonObject(),
        };
    }

    private static JsonArray ExactNode(Vector3 value) => new(Exact(value.X), Exact(value.Y), Exact(value.Z));

    /// <summary>
    /// Adopt a whole state (review major 2): the authority checks it as a save, writes it, and only then believes it; if
    /// the write fails nothing changes (the scene's poses go back too). Then the derived parts follow from what was saved:
    /// the team's journal and map, the avatars' identities, the creations and the objects in the scene.
    /// </summary>
    private RoomStateOutcome Adopt(JsonObject envelope, IReadOnlyList<string> kept, IReadOnlyList<string> leftBehind, string message, bool closeOpenTasks)
    {
        var adopted = Authority.Call(KernelNames.AdoptEnvelope, KernelJson.ToVariant(envelope)).AsGodotDictionary();
        if (!adopted["ok"].AsBool())
        {
            ApplyObjectPoses(ObjectPoses());
            return Refused(adopted.ContainsKey("message") ? adopted["message"].AsString() : "The state did not load.");
        }
        _migrationOffer = null;
        LoadTeam(envelope["team"] as JsonObject, closeOpenTasks);
        LoadNature();
        Runtime.Call(KernelNames.RefreshNow);
        ApplyObjectPoses(ObjectPoses());
        SaveNotice = "";
        return new(true, message, kept, leftBehind);
    }

    // ---- saves of a re-exported room: migration (docs/engine/phase3/command-host.md) ----

    /// <summary>The earlier save this room offers to bring over, set when the room opens with no save of its own; held until the player answers.</summary>
    private string? _migrationOffer;

    /// <summary>The newest save of this room under another manifest (null when there is none).</summary>
    private string? NewestOtherSave()
    {
        var file = SavePath.GetFile();
        var manifestDirectory = SavePath.GetBaseDir();
        var roomDirectory = manifestDirectory.GetBaseDir();
        using var directory = DirAccess.Open(roomDirectory);
        if (directory == null) return null;
        return directory.GetDirectories()
            .Where(name => name != manifestDirectory.GetFile() && ManifestPrefix.IsMatch(name) && Godot.FileAccess.FileExists($"{roomDirectory}/{name}/{file}"))
            .Select(name => $"{roomDirectory}/{name}/{file}")
            .OrderByDescending(path => Godot.FileAccess.GetModifiedTime(path)).ThenBy(path => path, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>
    /// The earlier save this room offers, while the player has not answered (review major 6): the team's sightings are not
    /// saved over the offer meanwhile. It is withdrawn when the player declines, migrates, or commits play in the new room.
    /// </summary>
    public string? MigrationCandidate() =>
        _migrationOffer != null && !Godot.FileAccess.FileExists(SavePath) && Authority.Call(KernelNames.IsReady).AsBool() && Revision == 0 && Authority.Call(KernelNames.ReceiptCount).AsInt32() == 0
            ? _migrationOffer : null;

    /// <summary>The player declines the offer: the earlier save stays as it is, and this room saves its own from now on.</summary>
    public void DeclineMigration()
    {
        _migrationOffer = null;
        if (_teamDirty) SaveTeam();
    }

    /// <summary>
    /// The player's one confirmed step: bring creations over from a save of this room under another manifest. The earlier
    /// save is untrusted input, like any save (review major 1): its size is bounded before it is read, and its whole
    /// document is checked (types, required fields, receipts that succeeded under their own key, the compiler) before
    /// anything is translated or compacted; any problem refuses all of it. Each creation must stand where it stood, at the
    /// height it stood, with its whole compiled volume clear of the new room's shell and objects and of the creations
    /// already kept (review major 3); the rest are listed and stay in the old file, which is never modified. Locks on what
    /// still exists carry over; moved objects take the new manifest's places; the journal comes over and the sightings
    /// made in this room are kept; the ledger starts compacted at the migrated revision. Never throws.
    /// </summary>
    public RoomStateOutcome MigrateFrom(string oldSavePath)
    {
        try
        {
            if (MigrationCandidate() is not { } candidate || candidate != oldSavePath) return Refused("There is no earlier save of this room to bring over.");
            if (!Untouched(sightAllowed: true)) return Refused("This room has play of its own now; the earlier save stays as it is.");
            using (var file = Godot.FileAccess.Open(oldSavePath, Godot.FileAccess.ModeFlags.Read))
            {
                if (file == null || file.GetLength() > (ulong)MaxSaveBytes) return Refused("The earlier save is unreadable or larger than a save can be; it is kept as it was.");
            }
            JsonObject old;
            try
            {
                using var document = CanonicalJson.Parse(Godot.FileAccess.GetFileAsString(oldSavePath));
                old = JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
            }
            catch (Exception) { return Refused("The earlier save is not strict JSON; it is kept as it was."); }
            if (OldSaveProblem(old) is { } problem) return Refused("The earlier save cannot be brought over (" + problem + "); it is kept as it was.");
            return Migrate(old);
        }
        catch (Exception error)
        {
            return Refused("The earlier save cannot be brought over (" + error.GetType().Name + "); it is kept as it was.");
        }
    }

    /// <summary>What is wrong with an earlier save's document (null when nothing is), before anything of it is used.</summary>
    private string? OldSaveProblem(JsonObject old)
    {
        // Version 7 (M3) is version 6 with whose save it is in the team block (checked by TeamProblem below).
        if (!Whole(old["version"]) || old["version"]!.GetValue<double>() is not (2 or 3 or 4 or 5 or 6 or 7)) return "its version";
        var version = (int)old["version"]!.GetValue<double>();
        var expected = new List<string> { "schema", "version", "compiler_version", "style_version", "room_pin", "revision", "permission_revision", "next_id", "roles", "consent", "instances", "receipts", "locks", "entity_revisions" };
        if (version >= 3) expected.AddRange(new[] { "compacted", "checkpoints" });
        if (version >= 4) expected.Add("object_poses");
        if (version >= 5) expected.Add("team");
        if (version >= 6) expected.Add("nature");
        if (old.Count != expected.Count || expected.Any(k => !old.ContainsKey(k))) return "its fields";
        if (!SchemaIds.IsSchema(StrOf(old["schema"]), "creation-world") || !Whole(old["compiler_version"]) || old["compiler_version"]!.GetValue<double>() != 1 || StrOf(old["style_version"]) != "painterly_v1")
            return "another schema, compiler or style";
        if (old["room_pin"] is not JsonObject pin || pin.Count != 2 || StrOf(pin["room_id"]) != Room.RoomId || StrOf(pin["manifest_sha256"]) is not { } sha || !Hex64.IsMatch(sha))
            return "another room";
        if (!Whole(old["revision"], int.MaxValue) || !Whole(old["permission_revision"], int.MaxValue) || !Whole(old["next_id"], 99999999) || old["next_id"]!.GetValue<double>() < 1)
            return "its revisions";
        var revision = (int)old["revision"]!.GetValue<double>();
        var nextId = (int)old["next_id"]!.GetValue<double>();
        if (old["roles"] is not JsonObject roles || roles.Count != 2 || StrOf(roles[PlayerPrincipal]) != "owner" || StrOf(roles[CompanionPrincipal]) is not ("editor" or "visitor")) return "its roles";
        if (old["consent"] is not JsonObject consent || consent.Count != 2 || !Bool(consent[PlayerPrincipal]) || !Bool(consent[CompanionPrincipal])) return "its consent";
        if (old["instances"] is not JsonArray instances || instances.Count > 16) return "its creations";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in instances)
        {
            if (node is not JsonObject instance || instance.Count != 6 || StrOf(instance["id"]) is not { } id || !HostCreationId.IsMatch(id) || !ids.Add(id) ||
                int.Parse(id["creation:".Length..], System.Globalization.CultureInfo.InvariantCulture) is var n && (n < 1 || n >= nextId)) return "a creation's id";
            if (!Principals.Contains(StrOf(instance["owner_id"]) ?? "") || !Whole(instance["revision"], revision) || instance["revision"]!.GetValue<double>() < 1) return "a creation's owner or revision";
            if (instance["source"] is not JsonObject source || !SchemaIds.IsSchema(StrOf(source["schema"]), "creation") || !Vec3(instance["position_m"]) || !Number(instance["yaw_deg"], 180)) return "a creation's source or place";
        }
        if (old["receipts"] is not JsonObject receipts || receipts.Count > 2048) return "its receipts";
        foreach (var (key, node) in receipts)
        {
            if (node is not JsonObject entry || entry.Count != 5 || StrOf(entry["principal"]) is not { } principal || !Principals.Contains(principal) ||
                StrOf(entry["action_id"]) is not { } action || !ActionId.IsMatch(action) || key != principal + "|" + action || StrOf(entry["fingerprint"]) is not { } fingerprint || !Hex64.IsMatch(fingerprint))
                return "a receipt's key";
            if (entry["meta"] is not JsonObject meta || meta.Count != 3 || StrOf(meta["op"]) is not { } op || !MetaOp.IsMatch(op) || StrOf(meta["at_utc"]) is not { } at ||
                (at.Length > 0 && !RealTime(meta["at_utc"])) || StrOf(meta["approved_by"]) is not { } approved || (approved.Length > 0 && (approved != PlayerPrincipal || principal == PlayerPrincipal)))
                return "a receipt's record";
            var keys = op == "journal.note" || KeptWithData(op) ? 8 : 7;
            if (entry["receipt"] is not JsonObject receipt || receipt.Count != keys || receipt["ok"]?.GetValueKind() != JsonValueKind.True || receipt["replayed"]?.GetValueKind() != JsonValueKind.False ||
                StrOf(receipt["instance_id"]) is not { } instanceId || (instanceId.Length > 0 && !instanceId.StartsWith("creation:", StringComparison.Ordinal)) ||
                !Whole(receipt["revision"], revision) || !Whole(receipt["permission_revision"], int.MaxValue) || !EntityIds(receipt["affected"]) || !EntityIds(receipt["created"]) ||
                (op == "journal.note" && (StrOf(receipt["entry_id"]) is not { } entryId || !EntryIdPattern.IsMatch(entryId))) ||
                (KeptWithData(op) && receipt["data"] is not JsonObject { Count: <= 32 }))
                return "a receipt that did not succeed as recorded";
        }
        if (version >= 3)
        {
            if (old["compacted"] is not JsonObject compacted || compacted.Count > 4096) return "its compacted receipts";
            foreach (var (key, value) in compacted)
            {
                var parts = key.Split('|');
                if (receipts.ContainsKey(key) || parts.Length != 2 || !Principals.Contains(parts[0]) || !ActionId.IsMatch(parts[1]) || value is not JsonArray pair || pair.Count != 2 ||
                    !Whole(pair[0], revision) || StrOf(pair[1]) is not { } prefix || !Hex16.IsMatch(prefix)) return "a compacted receipt";
            }
            if (old["checkpoints"] is not JsonArray checkpoints || checkpoints.Count > 16 || checkpoints.Any(c => c is not JsonObject cp || cp.Count != 3 ||
                StrOf(cp["id"]) is not { } cid || !CheckpointId.IsMatch(cid) || !Whole(cp["revision"], revision) || StrOf(cp["label"]) is not { } label ||
                KernelText.CodePoints(label).Length > 80 || KernelText.HasHidden(label))) return "a checkpoint";
        }
        if (old["locks"] is not JsonObject locks || locks.Count > 256 || locks.Any(p => !System.Text.RegularExpressions.Regex.IsMatch(p.Key, @"\A(obj|creation):[A-Za-z0-9_-]{1,64}\z") ||
            p.Value is not JsonObject l || l.Count != 2 || !Principals.Contains(StrOf(l["locked_by"]) ?? "") || !Whole(l["locked_revision"], revision) || l["locked_revision"]!.GetValue<double>() < 1))
            return "a lock";
        if (old["entity_revisions"] is not JsonObject revisions || revisions.Count > 4096 || revisions.Any(p => !System.Text.RegularExpressions.Regex.IsMatch(p.Key, @"\A(shell|obj):[A-Za-z0-9_-]{1,64}\z") ||
            !Whole(p.Value, revision) || p.Value!.GetValue<double>() < 1)) return "an object's revision";
        if (version >= 4 && (old["object_poses"] is not JsonObject poses || poses.Count > 4096 || poses.Any(p => !p.Key.StartsWith("obj:", StringComparison.Ordinal) || p.Value is not JsonObject pose ||
            pose.Count != 3 || !Vec3(pose["position_m"]) || !Quat(pose["rotation"]) || pose["bounds"] is not JsonObject b || b.Count != 2 || !Vec3(b["min_m"]) || !Vec3(b["max_m"]))))
            return "an object's pose";
        if (version >= 5 && (old["team"] is not JsonObject team || (team.Count > 0 && TeamProblem(team, revision) is not null))) return "its journal or map";
        // The island's nature, as far as an earlier manifest's save can be checked: its shape (Migrate keeps what still fits).
        if (version >= 6 && (old["nature"] is not JsonObject nature || (nature.Count > 0 && NatureBlock.Problem(nature, revision, room: null) is not null))) return "its nature";
        return null;
    }

    /// <summary>The migration itself, from an earlier save already checked whole.</summary>
    private RoomStateOutcome Migrate(JsonObject old)
    {
        var revision = (int)old["revision"]!.GetValue<double>();
        var kept = new List<string>();
        var leftBehind = new List<string>();
        var instances = new JsonArray();
        var keptBoxes = new List<Aabb>();
        foreach (var node in old["instances"]!.AsArray())
        {
            var instance = node!.AsObject();
            var id = instance["id"]!.GetValue<string>();
            if (FitsTheNewRoom(instance, keptBoxes) is not { } box)
            {
                leftBehind.Add(id);
                continue;
            }
            keptBoxes.Add(box);
            instances.Add(instance.DeepClone());
            kept.Add(id);
        }
        var locks = new JsonObject();
        foreach (var (id, node) in old["locks"]!.AsObject())
            if (kept.Contains(id) || Room.Objects.Any(o => o.Id == id)) locks[id] = node!.DeepClone();
        var revisions = new JsonObject();
        foreach (var (id, node) in old["entity_revisions"]!.AsObject())
            if (Room.Objects.Any(o => o.Id == id) || Room.Shell.Any(p => p.Id == id)) revisions[id] = node!.DeepClone();
        foreach (var (id, _) in locks) if (id.StartsWith("obj:", StringComparison.Ordinal) && !revisions.ContainsKey(id)) revisions[id] = locks[id]!["locked_revision"]!.DeepClone();
        // The ledger starts compacted: every receipt (each checked to have succeeded under its own key) replays and refuses conflicting reuse.
        var compacted = old["compacted"]?.DeepClone().AsObject() ?? new JsonObject();
        foreach (var (key, node) in old["receipts"]!.AsObject())
            compacted[key] = new JsonArray((int)node!["receipt"]!["revision"]!.GetValue<double>(), node["fingerprint"]!.GetValue<string>()[..16]);
        var checkpoints = old["checkpoints"]?.DeepClone().AsArray() ?? new JsonArray();
        var number = checkpoints.Count == 0 ? 1 : int.Parse(checkpoints[^1]!["id"]!.GetValue<string>()[2..], System.Globalization.CultureInfo.InvariantCulture) + 1;
        checkpoints.Add(new JsonObject { ["id"] = $"cp{number:0000}", ["revision"] = revision, ["label"] = "Brought over from an earlier version of this room" });
        while (checkpoints.Count > 16) checkpoints.RemoveAt(0);
        var oldTeam = old["team"] as JsonObject;
        var current = ExportTeam(_notes);
        // Whose save it is (M3) is this one's: the team and mode the island save is filed under.
        var team = WithSaveTeam(new JsonObject
        {
            ["journal"] = oldTeam?["journal"]?.DeepClone() ?? new JsonObject { ["open_tasks"] = new JsonArray(), ["history"] = new JsonArray(), ["notes"] = new JsonArray() },
            // What the avatars have discovered of this room while the offer stood belongs to this room: it is kept.
            ["discovered"] = current["discovered"]!.DeepClone(),
            ["task_goals"] = oldTeam?["task_goals"]?.DeepClone() ?? new JsonObject(),
            ["avatars"] = current["avatars"]!.DeepClone(),
        });
        var envelope = new JsonObject
        {
            ["schema"] = SchemaIds.CreationWorld, ["version"] = 6, ["compiler_version"] = 1, ["style_version"] = "painterly_v1",
            ["room_pin"] = new JsonObject { ["room_id"] = Room.RoomId, ["manifest_sha256"] = Room.ManifestSha256 },
            ["revision"] = revision, ["permission_revision"] = old["permission_revision"]!.DeepClone(), ["next_id"] = old["next_id"]!.DeepClone(),
            ["roles"] = old["roles"]!.DeepClone(), ["consent"] = new JsonObject { [PlayerPrincipal] = false, [CompanionPrincipal] = false },
            ["instances"] = instances, ["receipts"] = new JsonObject(), ["compacted"] = compacted, ["checkpoints"] = checkpoints,
            ["locks"] = locks, ["entity_revisions"] = revisions, ["object_poses"] = new JsonObject(), ["team"] = team,
            ["nature"] = MigratedNature(old["nature"] as JsonObject, kept, revision),
        };
        var message = leftBehind.Count == 0 ? $"Brought over {kept.Count} creations from the earlier version of this room."
            : $"Brought over {kept.Count} creations; {leftBehind.Count} no longer fit this version of the room and stay in the earlier save: {string.Join(", ", leftBehind)}.";
        // Tasks the earlier session left open closed with it.
        return Adopt(envelope, kept, leftBehind, message, closeOpenTasks: true);
    }

    /// <summary>
    /// The island's nature brought over from an earlier save: what fire did to the creations that came over and to objects
    /// this manifest still has, the wallet, the trail's setting and the weather clock; cells only where this room's ground
    /// is still there at their height (sampled now); the flowers the trail planted here while the offer stood are kept. A
    /// fire and the undo steps stay behind with the earlier session: the ledger starts compacted, and nothing to undo.
    /// </summary>
    private JsonObject MigratedNature(JsonObject? old, IReadOnlyCollection<string> kept, int revision)
    {
        var here = Nature.Untouched(trailAllowed: true, StartWallet) ? Nature : new NatureState();
        if (old == null || old.Count == 0)
            return here.Cells.Count == 0 ? new JsonObject() : NatureBlock.Pack(new NatureState { WeatherS = here.WeatherS, Cells = here.Cells.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal) });
        var earlier = NatureBlock.Unpack(old);
        var next = new NatureState
        {
            WeatherS = earlier.WeatherS, ChangedAt = Math.Min(earlier.ChangedAt, revision), TrailEnabled = earlier.TrailEnabled, TrailPrincipal = earlier.TrailPrincipal,
            TrailRulesSha256 = earlier.TrailRulesSha256, Wallet = earlier.Wallet,
        };
        foreach (var (id, item) in earlier.Objects)
            if (item.Creation ? kept.Contains(id) : Room.Objects.Any(o => o.Id == id)) next.Objects[id] = item;
        foreach (var (key, cell) in earlier.Cells)
        {
            if (SampleNatureCell(cell.X, cell.Z, cell.Y) is not { } ground || Math.Abs(ground.Y - cell.Y) > NatureLaws.BurnHeightToleranceM) continue;
            cell.BurningS = 0;
            next.Cells[key] = cell;
        }
        foreach (var (key, cell) in here.Cells) next.Cells[key] = cell.Copy();
        while (next.Cells.Count > NatureState.MaxCells) next.Cells.Remove(next.Cells.Keys.First());
        return NatureBlock.Pack(next);
    }

    /// <summary>
    /// Whether an earlier creation still fits in this room, where and as it stood (null when it does not): the authority's
    /// own placement checks at its height, then its whole compiled volume, from just above its base (its support contact)
    /// to its top, clear of the new room's shell and objects, of the creations already kept, and under the ceiling.
    /// </summary>
    private Aabb? FitsTheNewRoom(JsonObject instance, List<Aabb> kept)
    {
        var position = instance["position_m"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
        var yaw = instance["yaw_deg"]!.GetValue<double>();
        var check = Authority.Call(KernelNames.Preflight, instance["owner_id"]!.GetValue<string>(), KernelJson.ToVariant(instance["source"]), position[0], position[2], yaw, "", position[1], "", "").AsGodotDictionary();
        if (!check["ok"].AsBool() || Math.Abs(check["position_m"].AsGodotArray()[1].AsDouble() - position[1]) > 1e-5) return null;
        var bounds = check["artifact"].AsGodotDictionary()["bounds"].AsGodotDictionary();
        var low = ArrayVector(bounds["min"].AsGodotArray());
        var local = new Aabb(low, ArrayVector(bounds["max"].AsGodotArray()) - low);
        var origin = new Vector3((float)position[0], (float)position[1], (float)position[2]);
        var box = new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad((float)yaw)), origin) * local;
        if (box.End.Y > Room.Bounds.End.Y + 0.001f) return null;
        // Its support may touch its base; anything else inside its volume is in the way. A 2 mm skin allows contact at the sides.
        const float Skin = 0.002f;
        var start = Mathf.Max(box.Position.Y, origin.Y) + 0.003f;
        var inner = new Aabb(new Vector3(box.Position.X + Skin, start, box.Position.Z + Skin), new Vector3(box.Size.X - 2 * Skin, box.End.Y - Skin - start, box.Size.Z - 2 * Skin));
        if (inner.Size.X <= 0 || inner.Size.Y <= 0 || inner.Size.Z <= 0) return box;
        if (kept.Any(other => other.Intersects(inner))) return null;
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = new BoxShape3D { Size = inner.Size }, Transform = new Transform3D(Basis.Identity, inner.GetCenter()), CollisionMask = RoomBuilder.WorldLayer,
            CollideWithAreas = false, CollideWithBodies = true,
        };
        return GetViewport().FindWorld3D().DirectSpaceState.IntersectShape(query, 1).Count > 0 ? null : box;
    }
}
