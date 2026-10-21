using Godot;
using System;
using System.Collections.Generic;
using GarageGame.Native.Room;

namespace GarageGame.Native.Sandbox;

/// <summary>A thing a verb moves: its body (excluded from its own queries), size and pose, the pivot at its bottom centre.</summary>
public readonly record struct Thing(string Id, Rid Body, Vector3 Size, Vector3 Position, Quaternion Rotation);

/// <summary>Where a verb leaves a thing: its pose, the bounds it then occupies and the entity it rests on ("" when none is known).</summary>
public readonly record struct Rest(Vector3 Position, Quaternion Rotation, Aabb Bounds, string Support);

/// <summary>Why the room's physics will not take a verb: a contract error code and a plain message for the player or the companion.</summary>
public sealed record Blocked(string Code, string Message, bool Retryable = false);

/// <summary>
/// Where the sandbox verbs leave things, worked out with Jolt's shape queries against the room's real colliders at the
/// moment the command arrives, so a result is decided before its receipt is written and replays exactly.
/// - The query shape is the thing's box (its asset dimensions at its scale and rotation, pivot at the bottom centre),
///   half a millimetre narrower on each side so things set flush against each other are not taken for overlapping.
/// - Dropping and placing start the thing just above where it goes and sweep it straight down onto the first support;
///   the rest height is then snapped to that support's surface by rays, so stacks sit exactly on their supports.
/// - A push slides the thing a centimetre at a time a millimetre above its support, against the world and both avatars:
///   it stops early at an obstacle, climbs a ledge up to a quarter of its own height (at most 1 cm, so a book rides over
///   a rug's edge), settles after every step and falls when it leaves its support. A push that leaves it hanging with
///   its centre over nothing carries on until it tips off, at most half its diagonal further.
/// - A carried thing is put down along a path from over its holder's head (up at the holder if the spot is higher, then
///   across, then down onto the spot), probed along the thing's centre line against the world: a wall between holder and
///   spot stops it, however thin. (A thing carried overhead may clip a wall beside its holder; that alone does not.)
/// - Things rest only on the room's shell and objects. A creation is an obstacle but never a support (a thing on one
///   would float once the creation went), so a verb that would leave a thing on a creation is refused.
/// Things stay upright: there is no tipping over, rolling or sliding once at rest.
/// </summary>
public sealed class SandboxPhysics
{
    public const uint WorldMask = RoomBuilder.WorldLayer;
    /// <summary>The avatars' collision layer (SmallPlayerController).</summary>
    public const uint AvatarMask = 2;
    public const float SideClearanceM = 0.0005f;
    public const float LiftM = 0.001f;
    public const float SlideStepM = 0.01f;
    public const float MaxStepUpM = 0.01f;
    /// <summary>After a slide step, a thing with nothing this close under it falls.</summary>
    public const float FallAfterM = 0.02f;
    /// <summary>A placement's height is a hint: the thing starts this far above it and settles.</summary>
    public const float ProbeM = 0.05f;
    public const float SnapWindowM = 0.003f;
    /// <summary>A push that moves a thing less than this along its support has moved nothing.</summary>
    public const float MinPushM = 0.001f;
    public const float PoseGridM = 0.0001f;

    private readonly PhysicsDirectSpaceState3D _space;
    private readonly Aabb _room;

    public SandboxPhysics(PhysicsDirectSpaceState3D space, Aabb room)
    {
        _space = space;
        _room = room;
    }

    /// <summary>The axis-aligned bounds of a thing at a pose.</summary>
    public static Aabb Bounds(Vector3 pivot, Quaternion rotation, Vector3 size)
    {
        var basis = new Basis(rotation);
        Aabb? box = null;
        foreach (var x in new[] { -0.5f, 0.5f })
        foreach (var y in new[] { 0f, 1f })
        foreach (var z in new[] { -0.5f, 0.5f })
        {
            var corner = pivot + basis * new Vector3(size.X * x, size.Y * y, size.Z * z);
            box = box == null ? new Aabb(corner, Vector3.Zero) : box.Value.Expand(corner);
        }
        return box!.Value;
    }

    /// <summary>Half the thing's horizontal extent along a horizontal direction.</summary>
    public static float HalfAlong(Quaternion rotation, Vector3 size, Vector3 direction)
    {
        var basis = new Basis(rotation);
        return Mathf.Abs(direction.Dot(basis.X)) * size.X * 0.5f + Mathf.Abs(direction.Dot(basis.Z)) * size.Z * 0.5f;
    }

    /// <summary>
    /// Whether an avatar's hand gets to a box: for some point of it (its centre, corners and face centres, pulled inside),
    /// nothing in the world but the box's own body lies on the path from the avatar's axis, straight up to the point's height
    /// when it is above the head, then across to the point. Reach distance is SandboxRules.WithinReach; this is the way there.
    /// </summary>
    public bool ReachClear(SmallPlayerController body, Aabb box, Rid target)
    {
        var feet = body.GlobalPosition;
        var head = feet.Y + body.BodyHeightM;
        var exclude = new Godot.Collections.Array<Rid> { target };
        foreach (var point in Samples(box))
        {
            var corner = new Vector3(feet.X, point.Y, feet.Z);
            if (point.Y > head && RayBlocked(new Vector3(feet.X, head, feet.Z), corner, exclude)) continue;
            if (!RayBlocked(corner, point, exclude)) return true;
        }
        return false;
    }

    /// <summary>
    /// What is wrong with a saved pose in the room as it is, or null: it overlaps something (a millimetre in from every
    /// face, so resting and flush neighbours do not count), or rests on nothing of the room's (no surface of the shell or an
    /// object within a few millimetres under it). The command host checks every saved pose this way when a save loads.
    /// </summary>
    public string? PoseProblem(Thing thing)
    {
        var size = new Vector3(Mathf.Max(0.001f, thing.Size.X - 0.002f), Mathf.Max(0.001f, thing.Size.Y - 0.002f), Mathf.Max(0.001f, thing.Size.Z - 0.002f));
        var basis = new Basis(thing.Rotation);
        var inside = new PhysicsShapeQueryParameters3D
        {
            Shape = new BoxShape3D { Size = size, Margin = 0.001f }, Transform = new Transform3D(basis, thing.Position + basis * new Vector3(0, thing.Size.Y * 0.5f, 0)),
            CollisionMask = WorldMask, CollideWithBodies = true, CollideWithAreas = false, Exclude = new Godot.Collections.Array<Rid> { thing.Body },
        };
        if (_space.IntersectShape(inside, 1).Count > 0) return "is inside something";
        if (Snap(thing, thing.Position, thing.Rotation) is not { } support || Mathf.Abs(support.Height - thing.Position.Y) > 0.002f || !RoomSupport(support.Support))
            return "is not resting on anything";
        return null;
    }

    /// <summary>Release with no placement: set down in front of the actor, then settle.</summary>
    public (Rest? At, Blocked? Blocked) Drop(Thing thing, SmallPlayerController actor)
    {
        var facing = -actor.GlobalBasis.Z;
        facing.Y = 0;
        facing = facing.LengthSquared() > 1e-6f ? facing.Normalized() : Vector3.Forward;
        var feet = actor.GlobalPosition;
        var noRoom = new Blocked("occupied", "There is no room to put it down here. Turn round or step back.", true);
        // In front. Only when an avatar stands there (the player a fetch came back to) does it go beside the actor, or
        // behind it: a wall or a thing in front still refuses, so a drop never reaches round what the actor faces.
        var (front, frontBlocked, avatar) = DropToward(thing, actor, feet, facing);
        if (front != null || !avatar) return (front, front != null ? null : frontBlocked ?? noRoom);
        foreach (var turn in new[] { Mathf.Pi * 0.5f, -Mathf.Pi * 0.5f, Mathf.Pi })
        {
            var (rest, _, _) = DropToward(thing, actor, feet, facing.Rotated(Vector3.Up, turn));
            if (rest != null) return (rest, null);
        }
        return (null, frontBlocked ?? noRoom);
    }

    /// <summary>A drop to one side of the actor: where it comes to rest, or why not, and whether an avatar was what stood in the way.</summary>
    private (Rest? At, Blocked? Blocked, bool Avatar) DropToward(Thing thing, SmallPlayerController actor, Vector3 feet, Vector3 direction)
    {
        var at = feet + direction * (actor.BodyRadiusM + HalfAlong(thing.Rotation, thing.Size, direction) + 0.01f);
        var avatar = false;
        // From where it is carried (over the head); with something low overhead, from just above the feet.
        foreach (var height in new[] { actor.BodyHeightM + SandboxRules.CarryGapM, actor.StepHeightM })
        {
            var start = new Vector3(at.X, feet.Y + height, at.Z);
            if (start.Y + thing.Size.Y > _room.End.Y || !Free(thing, start, thing.Rotation, WorldMask) || !PathClear(thing, thing.Position, start, thing.Rotation)) continue;
            if (!Free(thing, start, thing.Rotation, AvatarMask))
            {
                avatar = true;
                continue;
            }
            var (rest, blocked) = Finish(thing, start, thing.Rotation, null);
            return (rest, blocked, avatar || ReferenceEquals(blocked, AvatarInTheWay));
        }
        return (null, null, avatar);
    }

    /// <summary>
    /// A placement without a support: the thing starts a little above position_m and settles on whatever is below. A
    /// carried thing (carried: its pose is over its holder's head) must get there along the put-down path.
    /// </summary>
    public (Rest? At, Blocked? Blocked) PlaceAt(Thing thing, Vector3 position, Quaternion rotation, bool carried = false)
    {
        var start = new Vector3(position.X, Mathf.Min(position.Y + ProbeM, _room.End.Y - thing.Size.Y - 0.001f), position.Z);
        if (!Free(thing, start, rotation, WorldMask | AvatarMask))
            return (null, new Blocked("occupied", "There is no room for it there.", true));
        if (carried && !PathClear(thing, thing.Position, start, rotation)) return (null, InTheWay);
        return Finish(thing, start, rotation, null);
    }

    /// <summary>
    /// A placement on a support: snapped onto its walkable top at position_m's horizontal place, kept inside the top where
    /// it fits (centred on an axis where it does not), and refused when anything else is in the way. position_m's height
    /// picks a lower surface of the support when it has one there (a shelf); otherwise the thing goes on top.
    /// </summary>
    public (Rest? At, Blocked? Blocked) PlaceOn(Thing thing, Vector3 position, Quaternion rotation, string support, Aabb supportBounds, bool carried = false)
    {
        var footprint = Bounds(Vector3.Zero, rotation, thing.Size);
        var x = Inside(position.X, supportBounds.Position.X - footprint.Position.X + 0.001f, supportBounds.End.X - footprint.End.X - 0.001f, supportBounds.GetCenter().X);
        var z = Inside(position.Z, supportBounds.Position.Z - footprint.Position.Z + 0.001f, supportBounds.End.Z - footprint.End.Z - 0.001f, supportBounds.GetCenter().Z);
        var heights = new List<float> { Mathf.Clamp(position.Y + ProbeM, supportBounds.Position.Y, supportBounds.End.Y) + 0.002f };
        if (supportBounds.End.Y + 0.002f - heights[0] > 0.0005f) heights.Add(supportBounds.End.Y + 0.002f);
        Blocked? blocked = null;
        foreach (var height in heights)
        {
            var start = new Vector3(x, height, z);
            if (start.Y + thing.Size.Y > _room.End.Y)
            {
                blocked ??= new Blocked("out_of_bounds", "It would not fit under the ceiling there.");
                continue;
            }
            if (!Free(thing, start, rotation, WorldMask | AvatarMask)) continue;
            if (carried && !PathClear(thing, thing.Position, start, rotation))
            {
                blocked ??= InTheWay;
                continue;
            }
            var (rest, refused) = Finish(thing, start, rotation, support);
            if (rest != null) return (rest, null);
            blocked ??= refused;
        }
        return (null, blocked ?? new Blocked("occupied", "There is no room for it there.", true));
    }

    /// <summary>A push: slid along its support up to distance, with collisions; see the class summary.</summary>
    public (Rest? At, Blocked? Blocked) Push(Thing thing, Vector3 direction, float distance)
    {
        var dir = new Vector3(direction.X, 0, direction.Z);
        if (dir.LengthSquared() < 1e-8f) return (null, new Blocked("invalid_args", "That push has no direction."));
        dir = dir.Normalized();
        var pivot = thing.Position;
        var rotation = thing.Rotation;
        var stepUp = Mathf.Min(MaxStepUpM, thing.Size.Y * 0.25f);
        var tipLimit = new Vector2(thing.Size.X, thing.Size.Z).Length() * 0.5f;
        var travelled = 0f;
        var tipped = 0f;
        for (var guard = 0; guard < 400; guard++)
        {
            float want;
            var tipping = false;
            if (travelled < distance - 1e-5f) want = Mathf.Min(SlideStepM, distance - travelled);
            else if (tipped < tipLimit - 1e-5f && !CentreSupported(thing, pivot, rotation))
            {
                want = Mathf.Min(SlideStepM, tipLimit - tipped);
                tipping = true;
            }
            else break;
            var moved = Slide(thing, ref pivot, rotation, dir, want, stepUp);
            if (tipping) tipped += moved; else travelled += moved;
            var fell = !SettleAfterSlide(thing, ref pivot, rotation, stepUp);
            if (fell || moved < want - 1e-5f) break;
        }
        // Less than a millimetre along the floor is no push (a thing against a wall only settles a hair).
        if (new Vector2(pivot.X - thing.Position.X, pivot.Z - thing.Position.Z).Length() < MinPushM && pivot.Y >= thing.Position.Y - FallAfterM)
            return (null, new Blocked("occupied", "Something is in the way.", true));
        return Finish(thing, pivot, rotation, null, settled: true);
    }

    private static readonly Blocked InTheWay = new("occupied", "Something is in the way: it cannot be put there from here.", true);
    private static readonly Blocked AvatarInTheWay = new("occupied", "An avatar is in the way.", true);

    /// <summary>Whether a supporting entity is part of the room a thing may rest on: the shell or another object, never a creation.</summary>
    public static bool RoomSupport(string support) => support.StartsWith("shell:", StringComparison.Ordinal) || support.StartsWith("obj:", StringComparison.Ordinal);

    // ---- queries ----

    private PhysicsShapeQueryParameters3D Query(Thing thing, Vector3 pivot, Quaternion rotation, uint mask, Vector3 motion = default)
    {
        var size = new Vector3(Mathf.Max(0.001f, thing.Size.X - 2 * SideClearanceM), Mathf.Max(0.001f, thing.Size.Y), Mathf.Max(0.001f, thing.Size.Z - 2 * SideClearanceM));
        var basis = new Basis(rotation);
        return new PhysicsShapeQueryParameters3D
        {
            Shape = new BoxShape3D { Size = size, Margin = 0.001f },
            Transform = new Transform3D(basis, pivot + basis * new Vector3(0, thing.Size.Y * 0.5f, 0)),
            Motion = motion, CollisionMask = mask, CollideWithBodies = true, CollideWithAreas = false,
            Exclude = new Godot.Collections.Array<Rid> { thing.Body },
        };
    }

    public bool Free(Thing thing, Vector3 pivot, Quaternion rotation, uint mask) => _space.IntersectShape(Query(thing, pivot, rotation, mask), 1).Count == 0;

    /// <summary>
    /// The put-down path of a carried thing, along its centre line: up at the holder if the spot is higher, across, then down
    /// to the start. The start itself is checked with the whole shape by the caller.
    /// </summary>
    private bool PathClear(Thing thing, Vector3 from, Vector3 to, Quaternion rotation)
    {
        var lift = new Basis(rotation) * new Vector3(0, thing.Size.Y * 0.5f, 0);
        var start = from + lift;
        var end = to + lift;
        var up = Mathf.Max(start.Y, end.Y);
        var over = new Vector3(start.X, up, start.Z);
        var above = new Vector3(end.X, up, end.Z);
        var exclude = new Godot.Collections.Array<Rid> { thing.Body };
        return !RayBlocked(start, over, exclude) && !RayBlocked(over, above, exclude) && !RayBlocked(above, end, exclude);
    }

    private bool RayBlocked(Vector3 from, Vector3 to, Godot.Collections.Array<Rid> exclude) =>
        from.DistanceSquaredTo(to) > 1e-10f && _space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to, WorldMask, exclude)).Count > 0;

    /// <summary>A box's centre, corners and face centres, each pulled 1 cm (or a quarter of the box) inside: the points perception samples too.</summary>
    private static IEnumerable<Vector3> Samples(Aabb box)
    {
        var centre = box.GetCenter();
        var size = box.Size;
        var reach = new Vector3(Mathf.Max(0, size.X * 0.5f - Mathf.Min(0.01f, size.X * 0.25f)),
            Mathf.Max(0, size.Y * 0.5f - Mathf.Min(0.01f, size.Y * 0.25f)), Mathf.Max(0, size.Z * 0.5f - Mathf.Min(0.01f, size.Z * 0.25f)));
        yield return centre;
        foreach (var x in new[] { -1f, 1f })
        foreach (var y in new[] { -1f, 1f })
        foreach (var z in new[] { -1f, 1f })
            yield return centre + reach * new Vector3(x, y, z);
        foreach (var axis in new[] { Vector3.Right, Vector3.Up, Vector3.Back })
        {
            yield return centre + reach * axis;
            yield return centre - reach * axis;
        }
    }

    private float Cast(Thing thing, Vector3 pivot, Quaternion rotation, Vector3 motion, uint mask)
    {
        if (motion.LengthSquared() < 1e-12f) return 1f;
        var fractions = _space.CastMotion(Query(thing, pivot, rotation, mask, motion));
        return fractions.Length > 0 ? Mathf.Clamp(fractions[0], 0f, 1f) : 0f;
    }

    /// <summary>One slide step a millimetre above the support, climbing a ledge when blocked. Returns the distance covered.</summary>
    private float Slide(Thing thing, ref Vector3 pivot, Quaternion rotation, Vector3 direction, float want, float stepUp)
    {
        var start = pivot + Vector3.Up * LiftM;
        var mask = WorldMask | AvatarMask;
        // A cast that starts against an obstacle may report it clear; a step that would end inside something is no step.
        var moved = want * Cast(thing, start, rotation, direction * want, mask);
        if (moved > 0 && !Free(thing, start + direction * moved, rotation, mask)) moved = 0;
        if (moved < want - 1e-5f && stepUp > 0)
        {
            var raised = start + Vector3.Up * stepUp * Cast(thing, start, rotation, Vector3.Up * stepUp, mask);
            var over = want * Cast(thing, raised, rotation, direction * want, mask);
            if (over > moved + 0.0005f && Free(thing, raised + direction * over, rotation, mask))
            {
                pivot = raised + direction * over;
                return over;
            }
        }
        pivot = start + direction * moved;
        return moved;
    }

    /// <summary>Lower the thing onto what is under it after a slide step. False when it went over an edge and fell.</summary>
    private bool SettleAfterSlide(Thing thing, ref Vector3 pivot, Quaternion rotation, float stepUp)
    {
        var near = LiftM + stepUp + FallAfterM;
        var fraction = Cast(thing, pivot, rotation, Vector3.Down * near, WorldMask);
        if (fraction < 1f)
        {
            pivot.Y -= near * fraction;
            return true;
        }
        var far = pivot.Y - _room.Position.Y + 0.5f;
        pivot.Y -= far * Cast(thing, pivot, rotation, Vector3.Down * far, WorldMask);
        return false;
    }

    private bool CentreSupported(Thing thing, Vector3 pivot, Quaternion rotation)
    {
        var ray = PhysicsRayQueryParameters3D.Create(pivot + Vector3.Up * 0.002f, pivot + Vector3.Down * 0.01f, WorldMask, new Godot.Collections.Array<Rid> { thing.Body });
        return _space.IntersectRay(ray).Count > 0;
    }

    /// <summary>
    /// Settle from start (unless already settled), snap the height to the support, then check the room and the avatars.
    /// With mustRestOn, a thing that comes to rest on anything else is refused.
    /// </summary>
    private (Rest? At, Blocked? Blocked) Finish(Thing thing, Vector3 start, Quaternion rotation, string? mustRestOn, bool settled = false)
    {
        var pivot = start;
        if (!settled)
        {
            var drop = pivot.Y - _room.Position.Y + 0.5f;
            var fraction = Cast(thing, pivot, rotation, Vector3.Down * drop, WorldMask);
            if (fraction >= 1f) return (null, new Blocked("out_of_bounds", "There is nothing there to put it on."));
            pivot.Y -= drop * fraction;
        }
        var support = "";
        if (Snap(thing, pivot, rotation) is { } snapped)
        {
            pivot.Y = snapped.Height;
            support = snapped.Support;
        }
        else support = Contact(thing, pivot, rotation);
        if (mustRestOn != null && support != mustRestOn) return (null, new Blocked("occupied", "There is no room for it there.", true));
        if (!RoomSupport(support))
            return (null, new Blocked("occupied", support == CreationSupport
                ? "It would rest on a creation; for now things are set only on the room's surfaces and objects."
                : "It would not rest on the room's surfaces or objects there."));
        pivot = new Vector3(Mathf.Snapped(pivot.X, PoseGridM), Mathf.Snapped(pivot.Y, PoseGridM), Mathf.Snapped(pivot.Z, PoseGridM));
        var rounded = new Quaternion(Mathf.Snapped(rotation.X, 1e-6f), Mathf.Snapped(rotation.Y, 1e-6f), Mathf.Snapped(rotation.Z, 1e-6f), Mathf.Snapped(rotation.W, 1e-6f)).Normalized();
        var bounds = Bounds(pivot, rounded, thing.Size);
        if (!_room.Grow(0.001f).Encloses(bounds)) return (null, new Blocked("out_of_bounds", "It would not fit inside the room there."));
        if (!Free(thing, pivot + Vector3.Up * 0.0005f, rounded, AvatarMask)) return (null, AvatarInTheWay);
        return (new Rest(pivot, rounded, bounds, support), null);
    }

    /// <summary>The highest upward-facing surface under the footprint within a few millimetres of its bottom, and whose it is.</summary>
    private (float Height, string Support)? Snap(Thing thing, Vector3 pivot, Quaternion rotation)
    {
        var basis = new Basis(rotation);
        var hx = Mathf.Max(0, thing.Size.X * 0.5f - 0.001f);
        var hz = Mathf.Max(0, thing.Size.Z * 0.5f - 0.001f);
        (float Height, string Support)? best = null;
        foreach (var (sx, sz) in new[] { (0f, 0f), (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
        {
            var offset = basis * new Vector3(sx * hx, 0, sz * hz);
            var at = new Vector3(pivot.X + offset.X, pivot.Y, pivot.Z + offset.Z);
            var ray = PhysicsRayQueryParameters3D.Create(at + Vector3.Up * SnapWindowM, at + Vector3.Down * SnapWindowM, WorldMask, new Godot.Collections.Array<Rid> { thing.Body });
            var hit = _space.IntersectRay(ray);
            if (hit.Count == 0 || hit["normal"].AsVector3().Y < 0.7f) continue;
            var height = hit["position"].AsVector3().Y;
            if (best == null || height > best.Value.Height) best = (height, SupportOf(hit["collider"].AsGodotObject()));
        }
        return best;
    }

    /// <summary>What the thing touches just below its bottom, when no ray found its support (resting on an edge).</summary>
    private string Contact(Thing thing, Vector3 pivot, Quaternion rotation)
    {
        var info = _space.GetRestInfo(Query(thing, pivot + Vector3.Down * 0.0015f, rotation, WorldMask));
        return info.Count > 0 ? SupportOf(GodotObject.InstanceFromId(info["collider_id"].AsUInt64())) : "";
    }

    /// <summary>What a support is: an entity id, CreationSupport for a creation's parts (they carry an artifact hash, not an id), else "".</summary>
    public const string CreationSupport = "creation";

    private static string SupportOf(GodotObject? collider)
    {
        var entity = EntityOf(collider);
        if (entity.Length > 0) return entity;
        for (var node = collider as Node; node != null; node = node.GetParent())
            if (node.HasMeta("artifact_hash")) return CreationSupport;
        return "";
    }

    /// <summary>The entity id a collider belongs to: the nearest node at or above it carrying entity_id metadata.</summary>
    public static string EntityOf(GodotObject? collider)
    {
        for (var node = collider as Node; node != null; node = node.GetParent())
            if (node.HasMeta("entity_id")) return node.GetMeta("entity_id").AsString();
        return "";
    }

    private static float Inside(float value, float low, float high, float centre) => low <= high ? Mathf.Clamp(value, low, high) : centre;
}
