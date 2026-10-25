using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Room;

namespace GarageGame.Native.Kernel;

/// <summary>
/// Wall ivy (decision 107, at five Grow expertise; decision 76's "ivy grown up a wall that the player can climb, for a limited
/// distance"): Grow aimed at a wall at least 70 degrees steep roots a strip on the standable ground at its foot, within
/// IvyRootBelowM under the aim, IvyLengthM up the face and IvyWidthM wide, grown whole by one cast. The controller already climbs any
/// face too steep to stand on at the bar's price; the strip changes only the price: a climb on it drains the bar at IvyClimbFactor of a
/// bare face's rate (the body asks IvyView, SmallPlayerController.Climb), full again once it burns. Grabbing still needs a bar above
/// empty. One ivy a ledge: no strip whose foot is within IvyApartM of another's, or whose top reaches the same ledge. At most
/// NatureState.MaxIvy. Drawn, never collided, so it moves no one. Saved and undone like the waterpad; it burns as a plant, caught by
/// burning ground at its foot.
/// </summary>
public partial class CommandHost
{
    /// <summary>The wall a Grow cast is aimed at: the point on its face and the face's outward normal.</summary>
    private readonly record struct IvyWall(Vector3 Aim, Vector3 Normal);

    public const string IvyNoRoot = "Ivy roots in standable ground at the foot of a wall: aim at the wall no more than 0.4 m above that ground.";
    public const string IvyHere = "Ivy already grows here: one strip a ledge.";
    public const string IvySameLedge = "Ivy already climbs to this ledge: one strip a ledge.";

    /// <summary>
    /// The wall a cast's point is on: no standable ground just under the point, and a face at least 70 degrees steep (an overhang to 110)
    /// within IvyAimM of it, found by short level rays toward it from eight sides (the nearest hit, then the first side), on the world
    /// layer (what is drawn: never a tree's hidden pole). Only the island's own ground and walls are walls (WallSurface): a plant's
    /// collision (a tree's trunk, climbed as trees are, and grown by Grow), a thing, a build, a bridge or a leaf is not. Null: no wall.
    /// </summary>
    private IvyWall? WallAt(Vector3 point)
    {
        if (!IsInsideTree() || !point.IsFinite()) return null;
        if (StandableBelow(point, 0.005f, GrowKindLaws.IvyGroundM) != null) return null;
        IvyWall? best = null;
        var nearest = float.PositiveInfinity;
        var space = GetViewport().World3D.DirectSpaceState;
        for (var k = 0; k < 8; k++)
        {
            var side = new Vector3(Mathf.Cos(k * Mathf.Pi / 4), 0, Mathf.Sin(k * Mathf.Pi / 4));
            using var query = PhysicsRayQueryParameters3D.Create(point + side * 0.06f, point - side * 0.02f, RoomBuilder.WorldLayer, KernelNames.NoRids);
            query.HitBackFaces = false;
            using var hit = space.IntersectRay(query);
            if (hit.Count == 0) continue;
            var at = hit[KernelNames.HitPosition].AsVector3();
            var normal = hit[KernelNames.HitNormal].AsVector3().Normalized();
            var distance = at.DistanceTo(point);
            if (Math.Abs(normal.Y) > GrowKindLaws.IvyMaxNormalY || distance > GrowKindLaws.IvyAimM || normal.Dot(side) < 0.3f || distance >= nearest - 1e-5f) continue;
            using (var collider = hit[KernelNames.HitCollider])
                if (!WallSurface(collider.AsGodotObject() as Node)) continue;
            nearest = distance;
            best = new IvyWall(at, normal);
        }
        return best;
    }

    /// <summary>
    /// Whether a collider is the island's own ground or wall (a shell part, the land, a cliff): not a plant copy's, a thing's (obj:), a
    /// build's (the runtime's assemblies), a bridge's or a waterpad's, each looked for up its node's parents.
    /// </summary>
    private bool WallSurface(Node? collider)
    {
        for (var node = collider; node != null; node = node.GetParent())
        {
            if (node.HasMeta(PlantCopiesView.PlantRowsMeta) || node.HasMeta(PlantCopiesView.PlantSetMeta)) return false;
            if (node.HasMeta(KernelNames.EntityIdMeta) && MetaText(node, KernelNames.EntityIdMeta) is var id && !id.StartsWith("shell:", StringComparison.Ordinal)) return false;
            if (Runtime != null && node.GetParent() == Runtime) return false;
            if (node is Node3D { Name: var name } && (name == BridgeView.ViewName || name == WaterpadView.ViewName)) return false;
        }
        return true;
    }

    /// <summary>The face's direction up along it (the world's up, laid on the face): a strip grows this way from its foot.</summary>
    internal static Vector3 IvyUp(Vector3 normal) => (Vector3.Up - normal * normal.Y).Normalized();

    /// <summary>
    /// Ivy at the wall (decision 107): rooted on the standable ground at the foot of the face within IvyRootBelowM under the aim, the
    /// strip's base on the face at that ground's height, the ledge its top reaches found behind the face; refused when it has no root, the
    /// island keeps its most, or a strip there already climbs (one a ledge). grown_plants 1.
    /// </summary>
    private JsonObject CastIvy(JsonElement root, IslandAbility ability, Cast cast, IvyWall wall, string principal, string action, string fingerprint, bool preview, string? approvedBy)
    {
        if (Nature.Ivy.Count >= NatureState.MaxIvy)
            throw new Refusal("invalid_args", $"The island already keeps {NatureState.MaxIvy} ivy strips, the most it can.", "$.args.center_m");
        var normal = wall.Normal;
        var outward = new Vector3(normal.X, 0, normal.Z).Normalized();
        float? foot = null;
        foreach (var away in new[] { 0.03f, 0.08f, 0.16f })
        {
            foot = StandableBelow(wall.Aim + outward * away, 0.005f, GrowKindLaws.IvyRootBelowM + 0.005f);
            if (foot != null) break;
        }
        if (foot is not { } ground) throw new Refusal("invalid_args", IvyNoRoot, "$.args.center_m");
        var up = IvyUp(normal);
        var baseM = wall.Aim + up * ((ground - wall.Aim.Y) / Math.Max(up.Y, 0.2f));
        var top = baseM + up * GrowKindLaws.IvyLengthM;
        Vector3? ledge = null;
        foreach (var behind in new[] { 0.03f, 0.08f })
        {
            var probe = new Vector3(top.X, top.Y + GrowKindLaws.IvyLedgeReachM, top.Z) - outward * behind;
            if (StandableBelow(probe, 0.002f, probe.Y - (baseM.Y + GrowKindLaws.IvyLedgeRiseM)) is { } y) { ledge = new Vector3(probe.X, y, probe.Z); break; }
        }
        if (Nature.Ivy.Any(i => i.BaseM.DistanceTo(baseM) < GrowKindLaws.IvyApartM)) throw new Refusal("invalid_args", IvyHere, "$.args.center_m");
        if (ledge is { } reached && Nature.Ivy.Any(i => i.LedgeM is { } theirs && Math.Abs(theirs.Y - reached.Y) <= GrowKindLaws.IvySameLedgeRiseM &&
                CampLaws.Flat(theirs, reached) <= GrowKindLaws.IvySameLedgeM))
            throw new Refusal("invalid_args", IvySameLedge, "$.args.center_m");
        return CommitGrowKind(root, ability, cast, GrowKindWords.Ivy, 1, "The Gubble asks to grow ivy up this wall.", null, (candidate, step) =>
        {
            candidate.RememberIvy(step);
            candidate.Ivy.Add(new NatureIvy { Id = candidate.NextIvyId(), BaseM = baseM, NormalM = normal, LedgeM = ledge });
        }, _ => Array.Empty<string>(), principal, action, fingerprint, preview, approvedBy);
    }

    // ---- the scene and fire ----

    /// <summary>The room's ivy follows nature's (IvyView under the room node, made with the first strip), with the share of the bar a climb on it pays.</summary>
    private bool ApplyIvy()
    {
        var room = GetParent()?.GetNodeOrNull<Node3D>(RoomNodePath);
        if (room == null || !IsInstanceValid(room)) return false;
        var view = room.GetNodeOrNull<IvyView>(IvyView.ViewName);
        if (view == null)
        {
            if (Nature.Ivy.Count == 0) return false;
            view = IvyView.Create();
            room.AddChild(view);
        }
        return view.SetIvy(Nature.Ivy.Select(i => new IvyView.Strip(i.Id, i.BaseM, i.NormalM, i.Burn)).ToList(), IvyClimbFactorNow);
    }

    /// <summary>A strip's bounds for catching fire: the strip on its face and the ground at its foot before it.</summary>
    private static Aabb IvyBounds(NatureIvy ivy)
    {
        var outward = new Vector3(ivy.NormalM.X, 0, ivy.NormalM.Z).Normalized();
        var across = IvyUp(ivy.NormalM).Cross(ivy.NormalM).Normalized() * (GrowKindLaws.IvyWidthM * 0.5f);
        var top = ivy.BaseM + IvyUp(ivy.NormalM) * GrowKindLaws.IvyLengthM;
        var corners = new[] { ivy.BaseM + across, ivy.BaseM - across, top + across, top - across, ivy.BaseM + outward * 0.06f + across, ivy.BaseM + outward * 0.06f - across };
        var low = corners.Aggregate((a, b) => a.Min(b)) - new Vector3(0, 0.03f, 0);
        var high = corners.Aggregate((a, b) => a.Max(b));
        return new Aabb(low, high - low);
    }

    /// <summary>Ivy near burning ground burns as plants do, slowed by rain; a strip at 1 is gone, and the bar's full price with it.</summary>
    private bool BurnIvy(float rate, Dictionary<(int, int), NatureCell> burning, NatureUndoStep? step)
    {
        var changed = false;
        foreach (var ivy in Nature.Ivy.ToList())
        {
            if (!Catches(IvyBounds(ivy).Grow(PlantLaws.CatchM), burning)) continue;
            Nature.RememberIvy(step);
            ivy.Burn = Math.Min(1, ivy.Burn + rate);
            if (ivy.Burn >= 1) Nature.Ivy.Remove(ivy);
            changed = true;
        }
        return changed;
    }
}
