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
/// The sandbox verbs (Run 2, P3; contracts/README.md "The sandbox verbs"): pick up (entity.grab, transient), carry
/// (holding: the thing rides over its holder's head), drop and put down (entity.release), place with snapping and stack
/// (entity.place, or a release with placement.on), and push (entity.push, at most 1 m). The room's physics decides where
/// a thing comes to rest (Sandbox/SandboxPhysics.cs) before the authority saves the pose with the receipt; the scene is
/// then derived from that state through the authority's pose seam. Both principals use them through their own avatar;
/// the player may also direct the companion's, and a companion never acts through the player's (actor_denied). Holding
/// is never saved: a carried thing stays saved where it was last put down.
/// </summary>
public partial class CommandHost
{
    /// <summary>Where saves live: user://saves/rooms/&lt;room&gt;/&lt;manifest prefix&gt;/. A test seam: room tests point it elsewhere.</summary>
    public static string SaveRoot { get; set; } = DefaultSaveRoot;
    public const string DefaultSaveRoot = "user://saves/rooms";

    /// <summary>What an avatar holds this session.</summary>
    private sealed class Held
    {
        public required string Target { get; init; }
        public required ObjectInstance Item { get; init; }
        public Quaternion RotationAtGrab { get; init; }
        public float YawAtGrab { get; init; }
        public uint Layer { get; init; }
    }

    /// <summary>The outcome of a hand key for the HUD: whether it worked and what to tell the player.</summary>
    public sealed record HandsOutcome(bool Ok, string Message);

    private readonly Dictionary<string, Held> _held = new(StringComparer.Ordinal);
    private Dictionary<string, Node3D>? _objectNodes;

    /// <summary>The entity id an avatar is holding, or null.</summary>
    public string? HeldBy(string avatar) => _held.TryGetValue(avatar, out var held) ? held.Target : null;

    /// <summary>The display name (untrusted world text, made safe) of what an avatar holds, or "".</summary>
    public string HeldName(string avatar) =>
        _held.TryGetValue(avatar, out var held) ? KernelJson.DisplayText(held.Item.DisplayName ?? held.Item.Asset.DisplayName, 80) : "";

    // ---- the verbs ----

    private JsonObject Grab(JsonElement args, string principal, string actionId, string fingerprint, bool preview)
    {
        var (actor, body) = SandboxActor(args, principal);
        RequireChange(principal);
        var target = Str(args, "target")!;
        // The player's own grab of a Fractal (the companion's is refused before, NamesAFractal): picked up, never carried.
        if (target.StartsWith("fractal:", StringComparison.Ordinal) && Fractals.Any(f => f.Id == target))
            throw new Refusal("invalid_args", "A Fractal is picked up, not carried: use item.pickup.", "$.args.target");
        var item = CheckGrab(actor, body, target);
        if (preview) return Previewed("entity.grab", principal, actionId, target);
        // Grabbing what this avatar already holds changes nothing and answers as a grab.
        if (item != null) TakeHold(actor, body, item, PoseOf(item, ObjectPoses()));
        return Transient("entity.grab", principal, actionId, fingerprint, new JsonArray(target), null);
    }

    /// <summary>
    /// entity.grab's checks for an actor and a thing (the command's, and a fetch's pick-up on arrival): the thing to take
    /// hold of, or null when the actor already holds it. Refusals name the contract's codes and fields.
    /// </summary>
    private ObjectInstance? CheckGrab(string actor, SmallPlayerController body, string target)
    {
        var entities = Entities();
        var entity = Find(entities, target) ?? throw new Refusal("target_not_found", "That is not in this room.", "$.args.target");
        var item = CheckHoldable(entity, actor);
        if (item == null) return null;
        CheckReach(body, item, BoundsOf(entity));
        CheckNothingOnIt(entities, entity);
        return item;
    }

    /// <summary>
    /// What may be picked up at all, judged on a summary (live, or as the requester remembers it): a movable object, not
    /// protected, not held by another, an actor with empty hands, within its carry limit. Null when the actor holds it.
    /// </summary>
    private ObjectInstance? CheckHoldable(JsonObject entity, string actor)
    {
        var item = MovableObject(entity, "$.args.target", "That cannot be picked up.");
        if (entity["protected"]!.GetValue<bool>()) throw new Refusal("target_protected", "That is protected. Only the player can unlock it.", "$.args.target");
        // An avatar always knows what it holds itself; anyone else's hold is judged as the summary says (seen or remembered).
        if (HeldBy(actor) == item.Id) return null;
        var holder = entity["held_by"]?.GetValue<string>();
        if (holder != null) throw new Refusal("target_busy", "Someone else is holding that.", "$.args.target");
        if (_held.ContainsKey(actor)) throw new Refusal("target_busy", "This avatar is already holding something. Put it down first.", "$.args.actor");
        var limit = SandboxRules.CarryLimitKg(actor);
        if (item.Asset.MassKg > limit) throw TooHeavy("to pick up", item.Asset.MassKg, limit);
        return item;
    }

    /// <summary>
    /// A fetch's checks when it is set (contracts/README.md, Fetch): the pick-up's, on the thing as the requester sees or
    /// remembers it, and a room the principal may change. Reach and what rests on it wait for arrival. True when the
    /// actor already holds it, so the fetch starts on its way back.
    /// </summary>
    private bool CheckFetch(string principal, string actor, JsonObject known)
    {
        RequireChange(principal);
        return CheckHoldable(known, actor) == null;
    }

    private JsonObject Release(JsonElement args, string principal, string actionId, Godot.Collections.Dictionary meta, bool preview)
    {
        var (actor, body) = SandboxActor(args, principal);
        RequireChange(principal);
        if (!_held.TryGetValue(actor, out var held)) throw new Refusal("invalid_args", "This avatar is not holding anything.", "$.args");
        var (position, rotation) = Carried(actor, held);
        var thing = ThingOf(held.Item, position, rotation);
        var hasPlacement = args.TryGetProperty("placement", out var placement);
        var rest = Settled(hasPlacement ? Placed(thing, placement, Entities(), carried: true) : Physics().Drop(thing, body), hasPlacement ? "$.args.placement" : "$.args");
        if (!SandboxRules.WithinReach(body, rest.Bounds)) throw OutOfReach(hasPlacement ? "$.args.placement" : "$.args");
        if (preview) return Previewed("entity.release", principal, actionId, held.Item.Id);
        return MoveHeld(actor, held, rest, principal, actionId, meta);
    }

    private JsonObject PlaceObject(JsonElement args, string principal, string actionId, Godot.Collections.Dictionary meta, bool preview)
    {
        RequireChange(principal);
        var target = Str(args, "target")!;
        var entities = Entities();
        var entity = Find(entities, target) ?? throw new Refusal("target_not_found", "That is not in this room.", "$.args.target");
        var item = MovableObject(entity, "$.args.target", "That cannot be moved.");
        if (entity["protected"]!.GetValue<bool>()) throw new Refusal("target_protected", "That is protected. Only the player can unlock it.", "$.args.target");
        var holder = entity["held_by"]?.GetValue<string>();
        var own = principal == PlayerPrincipal ? PlayerAvatar : CompanionAvatarId;
        if (holder != null && holder != own) throw new Refusal("target_busy", "Someone else is holding that.", "$.args.target");
        if (holder == null) CheckNothingOnIt(entities, entity);
        var (position, rotation) = PoseOf(item, ObjectPoses());
        var rest = Settled(Placed(ThingOf(item, position, rotation), args.GetProperty("placement"), entities), "$.args.placement");
        if (preview) return Previewed("entity.place", principal, actionId, target);
        // Placing what this principal's avatar carries puts it there and ends the carry.
        return holder != null ? MoveHeld(holder, _held[holder], rest, principal, actionId, meta) : Move(target, rest, principal, actionId, meta);
    }

    private JsonObject PushObject(JsonElement args, string principal, string actionId, Godot.Collections.Dictionary meta, bool preview)
    {
        var (actor, body) = SandboxActor(args, principal);
        RequireChange(principal);
        var target = Str(args, "target")!;
        var entities = Entities();
        var entity = Find(entities, target) ?? throw new Refusal("target_not_found", "That is not in this room.", "$.args.target");
        var item = MovableObject(entity, "$.args.target", "That cannot be pushed.");
        if (entity["protected"]!.GetValue<bool>()) throw new Refusal("target_protected", "That is protected. Only the player can unlock it.", "$.args.target");
        if (entity["held_by"] != null) throw new Refusal("target_busy", "Someone is holding that.", "$.args.target");
        var limit = SandboxRules.PushLimitKg(actor);
        if (item.Asset.MassKg > limit) throw TooHeavy("to push", item.Asset.MassKg, limit);
        var bounds = BoundsOf(entity);
        CheckReach(body, item, bounds);
        CheckNothingOnIt(entities, entity);
        var centre = bounds.GetCenter();
        var direction = args.TryGetProperty("toward_m", out var toward) ? KernelJson.ReadVector(toward) - centre : centre - body.GlobalPosition;
        direction.Y = 0;
        if (direction.LengthSquared() < 1e-6f)
            throw new Refusal("invalid_args", "That push has no direction: name a point away from the thing's centre.", args.TryGetProperty("toward_m", out _) ? "$.args.toward_m" : "$.args");
        var distance = Mathf.Min((float)CanonicalJson.ReadNumber(args.GetProperty("distance_m")), SandboxRules.MaxPushM);
        var (position, rotation) = PoseOf(item, ObjectPoses());
        var rest = Settled(Physics().Push(ThingOf(item, position, rotation), direction, distance), "$.args.target");
        if (preview) return Previewed("entity.push", principal, actionId, target);
        return Move(target, rest, principal, actionId, meta);
    }

    // ---- the verbs' checks ----

    /// <summary>The avatar a verb acts through: args.actor, else the principal's own. A companion may not name the player's.</summary>
    private (string Id, SmallPlayerController Body) SandboxActor(JsonElement args, string principal)
    {
        var actor = Str(args, "actor") ?? (principal == PlayerPrincipal ? PlayerAvatar : CompanionAvatarId);
        if (actor == PlayerAvatar && principal != PlayerPrincipal)
            throw new Refusal("actor_denied", "A companion acts only through its own avatar, never the player's.", "$.args.actor");
        var body = BodyOf(actor);
        if (body == null || !body.IsInsideTree()) throw new Refusal("target_not_found", "There is no such actor in this room.", "$.args.actor");
        return (actor, body);
    }

    private SmallPlayerController? BodyOf(string avatar)
    {
        SmallPlayerController? body = avatar == PlayerAvatar ? Player : avatar == CompanionAvatarId ? Companion : null;
        return body != null && IsInstanceValid(body) ? body : null;
    }

    /// <summary>The room object behind a summary, if it is one play can move; creations move by revision, the shell and avatars not at all.</summary>
    private ObjectInstance MovableObject(JsonObject entity, string path, string refusal)
    {
        var id = entity["id"]!.GetValue<string>();
        var item = entity["kind"]!.GetValue<string>() == "object" && entity["movable"]!.GetValue<bool>() ? Room.Objects.FirstOrDefault(o => o.Id == id) : null;
        return item ?? throw new Refusal("permission_denied", refusal, path);
    }

    private static Refusal TooHeavy(string what, float massKg, float limitKg) =>
        new("target_too_heavy", $"That is too heavy {what}: {Kilograms(massKg)} kg, and this avatar can manage {Kilograms(limitKg)} kg.", "$.args.target",
            actual: JsonValue.Create(Kilograms(massKg)), allowed: JsonValue.Create(Kilograms(limitKg)));

    private static double Kilograms(float value) => Math.Round(value, 3);

    private static Refusal OutOfReach(string path) => new("out_of_bounds", "That is out of reach. Move closer.", path, retryable: true);

    /// <summary>
    /// A verb changes the room, so it needs what every change needs before anything happens (a hold included): a save that
    /// loaded, and a role that builds. A visitor's hands are empty; so are everyone's while the save failed to load.
    /// </summary>
    private void RequireChange(string principal)
    {
        if (!Authority.Call(KernelNames.IsReady).AsBool())
            throw new Refusal("not_ready", "The room's saved state is not loaded, so nothing in it can be moved.", retryable: true);
        if (!Authority.Call(KernelNames.MayChange, principal).AsBool())
            throw new Refusal("permission_denied", "A visitor cannot move things.");
    }

    /// <summary>Within reach and with a clear way to it: a wall or partition between hand and thing puts it out of reach, however near.</summary>
    private void CheckReach(SmallPlayerController body, ObjectInstance item, Aabb bounds)
    {
        if (!SandboxRules.WithinReach(body, bounds)) throw OutOfReach("$.args.target");
        if (!Reachable(body, item, bounds))
            throw new Refusal("out_of_bounds", "That is out of reach: something is in the way.", "$.args.target", retryable: true);
    }

    private bool Reachable(SmallPlayerController body, ObjectInstance item, Aabb bounds) =>
        Physics().ReachClear(body, bounds, ObjectNode(item.Id) is CollisionObject3D collider ? collider.GetRid() : new Rid());

    /// <summary>Whether a rotation is the object's manifest rotation turned about the vertical: things stay upright as captured.</summary>
    private static bool IsTurnOf(Quaternion rotation, Quaternion manifest)
    {
        var turn = (rotation.Normalized() * manifest.Normalized().Inverse()).Normalized();
        return Mathf.Abs(turn.X) <= 1e-3f && Mathf.Abs(turn.Z) <= 1e-3f;
    }

    /// <summary>
    /// A thing with something resting on it stays put (the things on top would be left floating): take them off first.
    /// One with an avatar standing on it waits for the avatar to step off.
    /// </summary>
    private void CheckNothingOnIt(List<JsonObject> entities, JsonObject entity)
    {
        var id = entity["id"]!.GetValue<string>();
        var box = BoundsOf(entity);
        foreach (var other in entities)
        {
            var kind = other["kind"]!.GetValue<string>();
            if (other["id"]!.GetValue<string>() == id || kind == "shell" || other["held_by"] != null) continue;
            var on = BoundsOf(other);
            if (Mathf.Min(box.End.X, on.End.X) - Mathf.Max(box.Position.X, on.Position.X) <= 0.001f ||
                Mathf.Min(box.End.Z, on.End.Z) - Mathf.Max(box.Position.Z, on.Position.Z) <= 0.001f) continue;
            if (kind == "avatar")
            {
                if (on.Position.Y >= box.End.Y - 0.002f && on.Position.Y <= box.End.Y + 0.006f)
                    throw new Refusal("occupied", "Someone is standing on it.", "$.args.target", retryable: true);
                continue;
            }
            if (Mathf.Abs(on.Position.Y - box.End.Y) <= 0.003f)
                throw new Refusal("target_busy", "Something is resting on it. Take that off first.", "$.args.target");
        }
    }

    /// <summary>A placement for a thing: on a support (snapped onto its walkable top), or at a position where it settles.</summary>
    private (Rest? At, Blocked? Blocked) Placed(Thing thing, JsonElement placement, List<JsonObject> entities, bool carried = false)
    {
        var position = KernelJson.ReadVector(placement.GetProperty("position_m"));
        if (!Room.Bounds.Grow(0.001f).HasPoint(position)) throw new Refusal("out_of_bounds", "That position is outside the room.", "$.args.placement.position_m");
        // Far from the bodies the land's walkable ground is built first (S12b, TerrainCollision), so the thing settles on it.
        TerrainCollision.EnsureAround(this, position, TerrainCollision.FarActionM);
        var rotation = thing.Rotation;
        if (placement.TryGetProperty("rotation", out var turn))
        {
            var q = turn.EnumerateArray().Select(v => (float)CanonicalJson.ReadNumber(v)).ToArray();
            rotation = new Quaternion(q[0], q[1], q[2], q[3]).Normalized();
            // Things stand as captured: only a turn about the vertical axis.
            var manifest = Room.Objects.FirstOrDefault(o => o.Id == thing.Id)?.Rotation ?? Quaternion.Identity;
            if (!IsTurnOf(rotation, manifest))
                throw new Refusal("invalid_args", "Things stand upright; use a rotation about the vertical axis only.", "$.args.placement.rotation");
        }
        var placed = thing with { Rotation = rotation };
        if (!placement.TryGetProperty("on", out var on)) return Physics().PlaceAt(placed, position, rotation, carried);
        var supportId = on.GetString()!;
        var support = Find(entities, supportId) ?? throw new Refusal("target_not_found", "That is not in this room.", "$.args.placement.on");
        if (supportId == thing.Id) throw new Refusal("invalid_args", "A thing cannot be put on itself.", "$.args.placement.on");
        if (!support["affordances"]!.AsArray().Any(a => a!.GetValue<string>() == "walkable_top"))
            throw new Refusal("invalid_args", "Things can only be placed on surfaces with a walkable top.", "$.args.placement.on");
        if (support["held_by"] != null) throw new Refusal("target_busy", "Someone is holding that.", "$.args.placement.on");
        return Physics().PlaceOn(placed, position, rotation, supportId, BoundsOf(support), carried);
    }

    private static Rest Settled((Rest? At, Blocked? Blocked) outcome, string path) =>
        outcome.Blocked is { } blocked ? throw new Refusal(blocked.Code, blocked.Message, path, blocked.Retryable) : outcome.At!.Value;

    private SandboxPhysics Physics() => new(GetViewport().FindWorld3D().DirectSpaceState, Room.Bounds);

    private static JsonObject? Find(List<JsonObject> entities, string id) => entities.FirstOrDefault(e => e["id"]!.GetValue<string>() == id);

    // ---- committing a move ----

    /// <summary>Record where a thing came to rest: the authority saves the pose with the durable receipt, then hands the poses to the scene.</summary>
    private JsonObject Move(string target, Rest rest, string principal, string actionId, Godot.Collections.Dictionary meta)
    {
        var request = new Godot.Collections.Dictionary
        {
            ["op"] = "move", ["action_id"] = actionId, ["expected_revision"] = Revision, ["expected_permission_revision"] = PermissionRevision,
            ["target"] = target, ["position_m"] = Exact(rest.Position),
            ["rotation"] = new Godot.Collections.Array { Exact(rest.Rotation.X), Exact(rest.Rotation.Y), Exact(rest.Rotation.Z), Exact(rest.Rotation.W) },
            ["bounds"] = new Godot.Collections.Dictionary { ["min_m"] = Exact(rest.Bounds.Position), ["max_m"] = Exact(rest.Bounds.End) },
        };
        return Submit(request, principal, actionId, meta);
    }

    /// <summary>Put down what an avatar holds: the hold ends first, so the scene takes the saved pose; a refused save gives it back.</summary>
    private JsonObject MoveHeld(string actor, Held held, Rest rest, string principal, string actionId, Godot.Collections.Dictionary meta)
    {
        LetGo(actor);
        try { return Move(held.Item.Id, rest, principal, actionId, meta); }
        catch (Refusal)
        {
            if (BodyOf(actor) is { } body) TakeHold(actor, body, held.Item, (held.RotationAtGrab, held.YawAtGrab));
            throw;
        }
    }

    private static Godot.Collections.Array Exact(Vector3 value) => new() { Exact(value.X), Exact(value.Y), Exact(value.Z) };

    /// <summary>A float32 as the decimal it was written as (0.3f is 0.3, not 0.30000001192), so saves stay tidy.</summary>
    private static double Exact(float value) => double.Parse(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);

    // ---- holding ----

    private void TakeHold(string actor, SmallPlayerController body, ObjectInstance item, (Vector3 Position, Quaternion Rotation) pose) =>
        TakeHold(actor, body, item, (pose.Rotation, body.GlobalRotation.Y));

    private void TakeHold(string actor, SmallPlayerController body, ObjectInstance item, (Quaternion Rotation, float Yaw) grip)
    {
        var node = ObjectNode(item.Id);
        uint layer = RoomBuilder.WorldLayer;
        // A carried thing is no obstacle: not to its holder, the camera, the other avatar or the navigation map.
        if (node is CollisionObject3D collider)
        {
            layer = collider.CollisionLayer == 0 ? RoomBuilder.WorldLayer : collider.CollisionLayer;
            collider.CollisionLayer = 0;
        }
        var held = new Held { Target = item.Id, Item = item, RotationAtGrab = grip.Rotation, YawAtGrab = grip.Yaw, Layer = layer };
        _held[actor] = held;
        if (node != null) Carrying.Place(node, CarriedTransform(actor, held));
    }

    private void LetGo(string actor)
    {
        if (!_held.Remove(actor, out var held)) return;
        if (ObjectNode(held.Item.Id) is CollisionObject3D collider) collider.CollisionLayer = held.Layer;
    }

    /// <summary>Where a carried thing rides: centred over its holder's head, turning with it.</summary>
    private (Vector3 Position, Quaternion Rotation) Carried(string actor, Held held)
    {
        var body = BodyOf(actor);
        if (body == null) return PoseOf(held.Item, ObjectPoses(), carried: false);
        var turn = new Quaternion(Vector3.Up, body.GlobalRotation.Y - held.YawAtGrab);
        return (body.GlobalPosition + Vector3.Up * (body.BodyHeightM + SandboxRules.CarryGapM), (turn * held.RotationAtGrab).Normalized());
    }

    private Transform3D CarriedTransform(string actor, Held held)
    {
        var (position, rotation) = Carried(actor, held);
        return new Transform3D(new Basis(rotation).Scaled(Vector3.One * held.Item.Scale), position);
    }

    /// <summary>The Carrying node's feed: each carried thing's node and pose. A holder that left the room drops nothing: its thing goes back to where it was saved.</summary>
    private IEnumerable<(Node3D Node, Transform3D Pose)> CarriedNow()
    {
        foreach (var actor in _held.Keys.ToList())
        {
            var held = _held[actor];
            // A holder that left the room, or whose principal may no longer change it (demoted to visitor, a save that
            // stopped being available), lets go: the thing goes back to where it was last put down.
            if (BodyOf(actor) is not { } body || !body.IsInsideTree() || !Authority.Call(KernelNames.MayChange, actor == PlayerAvatar ? PlayerPrincipal : CompanionPrincipal).AsBool())
            {
                LetGo(actor);
                ApplyObjectPoses(ObjectPoses());
                continue;
            }
            if (ObjectNode(held.Item.Id) is { } node) yield return (node, CarriedTransform(actor, held));
        }
    }

    // ---- poses and the scene ----

    /// <summary>The authority's object poses (objects play has moved).</summary>
    private Godot.Collections.Dictionary ObjectPoses() => Authority.Call(KernelNames.Snapshot, KernelNames.PlayerArgument).AsGodotDictionary() is { } snapshot && snapshot.ContainsKey("object_poses")
        ? snapshot["object_poses"].AsGodotDictionary() : new Godot.Collections.Dictionary();

    /// <summary>Where an object is now: carried, where play last put it, or where the room manifest puts it.</summary>
    private (Vector3 Position, Quaternion Rotation) PoseOf(ObjectInstance item, Godot.Collections.Dictionary poses, bool carried = true)
    {
        if (carried)
            foreach (var (actor, held) in _held)
                if (held.Target == item.Id && BodyOf(actor) != null) return Carried(actor, held);
        if (!poses.TryGetValue(item.Id, out var value)) return (item.PositionM, item.Rotation);
        var pose = value.AsGodotDictionary();
        var position = pose["position_m"].AsGodotArray();
        var rotation = pose["rotation"].AsGodotArray();
        return (new Vector3((float)position[0].AsDouble(), (float)position[1].AsDouble(), (float)position[2].AsDouble()),
            new Quaternion((float)rotation[0].AsDouble(), (float)rotation[1].AsDouble(), (float)rotation[2].AsDouble(), (float)rotation[3].AsDouble()).Normalized());
    }

    /// <summary>PoseOf, from saved poses already read into C# (ReadEntitySnapshot).</summary>
    private (Vector3 Position, Quaternion Rotation) PoseOf(ObjectInstance item, Dictionary<string, (Vector3 Position, Quaternion Rotation)> poses)
    {
        foreach (var (actor, held) in _held)
            if (held.Target == item.Id && BodyOf(actor) != null) return Carried(actor, held);
        return poses.TryGetValue(item.Id, out var pose) ? pose : (item.PositionM, item.Rotation);
    }

    private Thing ThingOf(ObjectInstance item, Vector3 position, Quaternion rotation) =>
        new(item.Id, ObjectNode(item.Id) is CollisionObject3D body ? body.GetRid() : new Rid(), item.Asset.DimensionsM * item.Scale, position, rotation);

    /// <summary>
    /// The authority's pose seam (creation_authority.gd pose_sink): the room's object nodes take the saved poses, or the
    /// manifest's where play has moved nothing. A carried thing is left to its holder.
    /// </summary>
    public void ApplyObjectPoses(Godot.Collections.Dictionary poses)
    {
        foreach (var item in Room.Objects)
        {
            if (_held.Values.Any(h => h.Target == item.Id) || ObjectNode(item.Id) is not { } node) continue;
            var (position, rotation) = PoseOf(item, poses, carried: false);
            Carrying.Place(node, new Transform3D(new Basis(rotation).Scaled(Vector3.One * item.Scale), position));
        }
    }

    /// <summary>
    /// The authority's pose check (creation_authority.gd pose_check), for a save being loaded, once its poses are in the scene:
    /// a pose is believed only if it is the object's manifest rotation turned about the vertical, its bounds are its asset's
    /// at that pose, and it rests on the shell or an object without overlapping anything. Otherwise the save is refused,
    /// saying which object, and stays on disk untouched (as a save with an unplaceable creation does).
    /// </summary>
    public Godot.Collections.Dictionary CheckObjectPoses(Godot.Collections.Dictionary poses)
    {
        foreach (var (key, value) in poses)
        {
            var id = key.AsString();
            var item = Room.Objects.FirstOrDefault(o => o.Id == id);
            if (item == null) return PoseRefused(id, "is not in this room");
            var pose = value.AsGodotDictionary();
            var (position, rotation) = PoseOf(item, new Godot.Collections.Dictionary { [id] = pose }, carried: false);
            if (!IsTurnOf(rotation, item.Rotation)) return PoseRefused(id, "is turned off its upright");
            var bounds = SandboxPhysics.Bounds(position, rotation, item.Asset.DimensionsM * item.Scale);
            var saved = pose["bounds"].AsGodotDictionary();
            if (bounds.Position.DistanceTo(ArrayVector(saved["min_m"].AsGodotArray())) > 0.001f || bounds.End.DistanceTo(ArrayVector(saved["max_m"].AsGodotArray())) > 0.001f)
                return PoseRefused(id, "does not match the object's size");
            if (Physics().PoseProblem(ThingOf(item, position, rotation)) is { } problem) return PoseRefused(id, problem);
        }
        return new Godot.Collections.Dictionary { ["ok"] = true };
    }

    private static Godot.Collections.Dictionary PoseRefused(string id, string problem) => new()
    {
        ["ok"] = false, ["message"] = $"The saved place of {id} {problem}; the save was not loaded and is kept as it was.",
    };

    /// <summary>The built room's object nodes by entity id (the room the host's parent built, named "Room").</summary>
    private Node3D? ObjectNode(string id)
    {
        if (_objectNodes == null || _objectNodes.Values.Any(n => !IsInstanceValid(n)))
        {
            _objectNodes = new Dictionary<string, Node3D>(StringComparer.Ordinal);
            if (GetParent()?.GetNodeOrNull(BuiltPath) is { } built) IndexObjects(built);
        }
        return _objectNodes.GetValueOrDefault(id);
    }

    private void IndexObjects(Node node)
    {
        if (node is Node3D spatial && node.HasMeta("entity_id") && node.GetMeta("entity_id").AsString() is { } id && id.StartsWith("obj:", StringComparison.Ordinal))
            _objectNodes![id] = spatial;
        foreach (var child in node.GetChildren()) IndexObjects(child);
    }

    // ---- the player's hand keys (RoomHud: F and V) ----

    /// <summary>The focus: the thing the next hand key would act on (empty Id for none), its bounds, and the tag's words.</summary>
    public readonly record struct HandsFocus(string Id, Aabb Box, string Tag);

    /// <summary>
    /// What F would act on now, and what the keys would do there (RUN-2-OPEN-SEA.md, "Things to touch"): carrying, the thing
    /// with a walkable top F would set it on (none: F sets it down in front, and nothing is lit); else the thing F would
    /// pick up, tagged with the keys that work on it, or the honest reason. The same choices PlayerHands and PlayerPush make.
    /// </summary>
    public HandsFocus PlayerFocus()
    {
        if (Player == null || !IsInstanceValid(Player)) return default;
        // Only what is within reach can be the focus (both choices below keep only what PlayerReaches): the rest is never built.
        var entities = ObjectEntitiesNear(Player);
        if (_held.TryGetValue(PlayerAvatar, out var held))
        {
            var support = SandboxControls.SupportAhead(Player, entities, held.Target, PlayerReaches);
            return support == null ? default : new HandsFocus(support["id"]!.GetValue<string>(), BoundsOf(support),
                string.Format(SandboxControls.FocusWords.SetOn, HeldName(PlayerAvatar), support["display_name"]!.GetValue<string>()));
        }
        var target = SandboxControls.ThingAhead(Player, entities, SandboxRules.CarryLimitKg(PlayerAvatar), MassOf, PlayerReaches);
        if (target == null) return default;
        var id = target["id"]!.GetValue<string>();
        var mass = MassOf(id);
        var tag = mass <= SandboxRules.CarryLimitKg(PlayerAvatar) ? SandboxControls.FocusWords.PickUpOrPush
            : mass <= SandboxRules.PushLimitKg(PlayerAvatar) ? SandboxControls.FocusWords.PushOnly : SandboxControls.FocusWords.TooHeavy;
        return new HandsFocus(id, BoundsOf(target), tag);
    }

    /// <summary>The built node of a room object (for the focus highlight), or null.</summary>
    public Node3D? ObjectNodeOf(string id) => ObjectNode(id);

    /// <summary>F: pick up what the player faces within reach, or put down what it holds: on the thing it faces if that has a walkable top, else in front of it.</summary>
    public HandsOutcome PlayerHands()
    {
        if (Player == null || !IsInstanceValid(Player)) return new(false, "The player's body is not in the room.");
        var entities = Entities();
        if (_held.TryGetValue(PlayerAvatar, out var held))
        {
            var name = HeldName(PlayerAvatar);
            var support = SandboxControls.SupportAhead(Player, entities, held.Target, PlayerReaches);
            var args = new JsonObject();
            if (support != null)
            {
                var footprint = SandboxPhysics.Bounds(Vector3.Zero, Carried(PlayerAvatar, held).Rotation, held.Item.Asset.DimensionsM * held.Item.Scale);
                args["placement"] = new JsonObject
                {
                    ["position_m"] = KernelJson.Vector(SandboxControls.SpotOn(Player, BoundsOf(support), footprint)), ["on"] = support["id"]!.GetValue<string>(),
                };
            }
            var released = PlayerSandbox("entity.release", args);
            return Outcome(released, support != null ? $"Set {name} on {support["display_name"]!.GetValue<string>()}." : $"Set {name} down.");
        }
        var target = SandboxControls.ThingAhead(Player, entities, SandboxRules.CarryLimitKg(PlayerAvatar), MassOf, PlayerReaches);
        // P-Inventory-2 (decision 118): a loose unit faced (a windfall, an apple or a log set down) is picked up into its stack, before a
        // thing behind it.
        if (InventoryRules != null && LooseInReach().FirstOrDefault() is { Kind.Length: > 0 } unit &&
            (target == null || unit.Gap <= Player.GlobalPosition.DistanceSquaredTo(BoundsOf(target).GetCenter())))
        {
            var unitKind = InventoryRules.Kind(unit.Kind);
            var taken = PlayerTakeFromGround(unit.Kind);
            var units = PocketContents.Units(unit.Kind);
            var unitWords = unitKind == null ? unit.Kind : KernelJson.DisplayText(unitKind.Words(units), 40).ToLowerInvariant();
            return Outcome(taken, $"Picked up. In your pockets: {units} {unitWords}.");
        }
        if (target == null) return new(false, "Nothing to pick up within reach. Walk up to something and face it.");
        // P-Inventory-2 (decision 116, the founder's refinement): a log or stone is not carried: F (and E) gathers it into its stack.
        if (InventoryRules is { } rules && Room.Objects.FirstOrDefault(o => o.Id == target["id"]!.GetValue<string>()) is { } loose &&
            GatherMaterial(loose) != null && KindOfObject(rules, loose) is { } kind)
        {
            var name = KernelJson.DisplayText(loose.DisplayName ?? loose.Asset.DisplayName, 40);
            var gathered = PlayerGatherFrom(loose.Id);
            var count = PocketContents.Units(kind.Id);
            return Outcome(gathered, $"{name} is in your pockets: {count} {KernelJson.DisplayText(kind.Words(count), 40).ToLowerInvariant()}.");
        }
        var grabbed = PlayerSandbox("entity.grab", new JsonObject { ["target"] = target["id"]!.GetValue<string>() });
        return Outcome(grabbed, $"Holding {HeldName(PlayerAvatar)}. {PlayerControls.Label(Act.Hands)} sets it down in front of you, or on top of what you face.");
    }

    /// <summary>V: push what the player faces within reach, HandPushM along the player's facing.</summary>
    public HandsOutcome PlayerPush()
    {
        if (Player == null || !IsInstanceValid(Player)) return new(false, "The player's body is not in the room.");
        var target = SandboxControls.ThingAhead(Player, Entities(), SandboxRules.PushLimitKg(PlayerAvatar), MassOf, PlayerReaches);
        if (target == null) return new(false, "Nothing to push within reach. Walk up to something and face it.");
        var centre = BoundsOf(target).GetCenter();
        var pushed = PlayerSandbox("entity.push", new JsonObject
        {
            ["target"] = target["id"]!.GetValue<string>(), ["distance_m"] = SandboxRules.HandPushM,
            ["toward_m"] = KernelJson.Vector(centre + SandboxControls.Facing(Player)),
        });
        return Outcome(pushed, $"Pushed {target["display_name"]!.GetValue<string>()}.");
    }

    private float MassOf(string id) => Room.Objects.FirstOrDefault(o => o.Id == id)?.Asset.MassKg ?? float.MaxValue;

    /// <summary>What the hand keys may aim at: within the player's reach, with a clear way to it.</summary>
    private bool PlayerReaches(JsonObject entity)
    {
        var bounds = BoundsOf(entity);
        if (Player == null || !SandboxRules.WithinReach(Player, bounds)) return false;
        var item = Room.Objects.FirstOrDefault(o => o.Id == entity["id"]!.GetValue<string>());
        // A support for a put-down is reached at its top; objects are checked with their own body left out.
        return item == null || Reachable(Player, item, bounds);
    }

    /// <summary>
    /// A hand key's command, as the player. Each put-down and push keeps a durable receipt; if the player's share of the
    /// ledger is full, the keys compact it with a room.checkpoint (it changes nothing in the room) and try once more.
    /// </summary>
    private JsonObject PlayerSandbox(string op, JsonObject args)
    {
        var result = PlayerCommand(op, (JsonObject)args.DeepClone());
        if (result["error"]?["code"]?.GetValue<string>() != "receipt_limit") return result;
        PlayerCommand("room.checkpoint", new JsonObject { ["label"] = "hand keys" });
        return PlayerCommand(op, args);
    }

    private static HandsOutcome Outcome(JsonObject result, string success) =>
        HandleObjectOk(result) ? new(true, success) : new(false, result["error"]?["message"]?.GetValue<string>() ?? "That did not work.");
}
