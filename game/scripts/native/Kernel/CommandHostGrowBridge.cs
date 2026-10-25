using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarageGame.Native.Room;

namespace GarageGame.Native.Kernel;

/// <summary>
/// A bridge of plants (decision 106, at three Grow expertise; horizontal only, decision 76): the player holds 2 and drags from lip to lip,
/// as 6 drags a Fireline, and the drag's ends (center_m, end_m) are the lips: standable ground each, level within BridgeLevelM (the deck
/// grows at the higher), at least BridgeMinM and never more than BridgeMaxM apart (decision 106: 1.2 m, fixed: gaps are made to fit
/// it). Between them the deck must cross one true void: no ground within BridgeVoidDropM under it, or swimmable water, with nothing in
/// the way over it; both lips and the middle within the Gubble's reach and the team's sight. The deck is BridgeWidthM wide, drawn from
/// lip to lip and collided only over the void (its span along the deck), one-sided facing up, so it never pushes an avatar, and it is
/// not grown while one is where its collision would be. At most NatureState.MaxBridges. Saved and undone like the waterpad; it burns as
/// a plant, caught by burning ground at a lip, and whoever is on it falls as usual when it is gone. BridgeView draws and collides it.
/// </summary>
public partial class CommandHost
{
    /// <summary>A bridge as checked: its lips at the deck's height and the span of the void along it (metres from From).</summary>
    private readonly record struct BridgePlan(Vector3 From, Vector3 To, float SpanFrom, float SpanTo);

    public const string BridgeNoLip = "A bridge of plants grows from lip to lip: start and end the drag on solid ground at the gap's edges.";
    public const string BridgeNotLevel = "The two lips are not level: a bridge of plants grows level, its lips within 2 cm of each other.";
    public const string BridgeNoGap = "There is no gap to cross there: a bridge of plants grows only over a drop or deep water.";
    public const string BridgeTwoGaps = "There is ground in the middle of that gap: a bridge of plants crosses one gap, from lip to lip.";
    public const string BridgeInTheWay = "Something is in the way of the bridge.";
    public const string BridgeHere = "A bridge of plants already crosses here.";
    public const string BridgeAvatar = "Someone is in the gap where the bridge would grow: wait until it is clear.";

    /// <summary>
    /// A bridge's drag, checked in the kernel's order (CheckCast checked the near lip's reach and sight): the far lip inside the island,
    /// the far lip and the middle within reach and sight, the island's most, the length, the lips' ground and their level, no other
    /// bridge there, one true void between them, nothing in the way and no avatar where its collision would be.
    /// </summary>
    private BridgePlan CheckBridge(IslandAbility ability, JsonElement args, Cast cast, string principal)
    {
        var end = KernelJson.ReadVector(args.GetProperty("end_m"));
        if (!Room.Bounds.Grow(0.02f).HasPoint(end)) throw new Refusal("out_of_bounds", "Choose a bridge inside this island.", "$.args.end_m");
        var middle = (cast.Center + end) * 0.5f;
        foreach (var point in new[] { end, middle })
        {
            if (Companion == null || Companion.GlobalPosition.DistanceTo(point) > ability.ReachM)
                throw new Refusal("out_of_bounds", "Bring the Gubble closer: both lips of the bridge must be within its reach.", "$.args.end_m", retryable: true);
            if (!TeamSeesSpot(point, principal)) throw new Refusal("target_not_found", "The team must see the whole gap the bridge will cross.", "$.args.end_m");
        }
        if (Nature.Bridges.Count >= NatureState.MaxBridges)
            throw new Refusal("invalid_args", $"The island already keeps {NatureState.MaxBridges} bridges of plants, the most it can.", "$.args.end_m");
        var length = CampLaws.Flat(cast.Center, end);
        if (length > GrowKindLaws.BridgeMaxM + 1e-4f)
            throw new Refusal("invalid_args", $"A bridge of plants is at most {GrowKindLaws.BridgeMaxM:0.0#} m long.", "$.args.end_m", allowed: JsonValue.Create(GrowKindLaws.BridgeMaxM));
        if (length < GrowKindLaws.BridgeMinM)
            throw new Refusal("invalid_args", $"A bridge of plants is at least {GrowKindLaws.BridgeMinM:0.0#} m long: a narrower gap can be hopped.", "$.args.end_m");
        var near = LipAt(cast.Center) ?? throw new Refusal("invalid_args", BridgeNoLip, "$.args.center_m");
        var far = LipAt(end) ?? throw new Refusal("invalid_args", BridgeNoLip, "$.args.end_m");
        if (Math.Abs(near - far) > GrowKindLaws.BridgeLevelM + 1e-4f) throw new Refusal("invalid_args", BridgeNotLevel, "$.args.end_m");
        var deck = Math.Max(near, far);
        var from = new Vector3(cast.Center.X, deck, cast.Center.Z);
        var to = new Vector3(end.X, deck, end.Z);
        if (Nature.Bridges.Any(b => DecksMeet(b, from, to))) throw new Refusal("invalid_args", BridgeHere, "$.args.end_m");
        var (spanFrom, spanTo) = VoidSpan(from, to);
        if (DeckBlocked(from, to)) throw new Refusal("invalid_args", BridgeInTheWay, "$.args.end_m");
        if (AvatarOverSpan(from, to, spanFrom, spanTo)) throw new Refusal("target_busy", BridgeAvatar, "$.args.end_m", retryable: true);
        return new BridgePlan(from, to, spanFrom, spanTo);
    }

    /// <summary>The bridge grown (one room revision; grown_plants 1): the receipt's center_m and end_m are its lips as it grew.</summary>
    private JsonObject CastBridge(JsonElement root, IslandAbility ability, Cast cast, BridgePlan plan, string principal, string action, string fingerprint, bool preview, string? approvedBy) =>
        CommitGrowKind(root, ability, cast, GrowKindWords.Bridge, 1, "The Gubble asks to grow a bridge of plants across this gap.", data =>
        {
            data["center_m"] = KernelJson.Vector(plan.From);
            data["end_m"] = KernelJson.Vector(plan.To);
        }, (candidate, step) =>
        {
            candidate.RememberBridges(step);
            candidate.Bridges.Add(new NatureBridge { Id = candidate.NextBridgeId(), FromM = plan.From, ToM = plan.To, SpanFromM = plan.SpanFrom, SpanToM = plan.SpanTo });
        }, _ => Array.Empty<string>(), principal, action, fingerprint, preview, approvedBy);

    // ---- the gap ----

    /// <summary>What a body could stand on under a point, within depth below it (from just above it): the height, or null.</summary>
    private float? StandableBelow(Vector3 point, float above, float depth)
    {
        if (!IsInsideTree()) return null;
        using var query = PhysicsRayQueryParameters3D.Create(point + Vector3.Up * above, point + Vector3.Down * depth, RoomBuilder.BodyMask, KernelNames.NoRids);
        query.HitBackFaces = false;
        using var hit = GetViewport().World3D.DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0 || hit[KernelNames.HitNormal].AsVector3().Y < FloorCos) return null;
        return hit[KernelNames.HitPosition].AsVector3().Y;
    }

    /// <summary>The cosine of the steepest ground a body stands on (the player's FloorMaxAngle; 45 degrees without one).</summary>
    private float FloorCos => Mathf.Cos(Player != null && IsInstanceValid(Player) ? Player.FloorMaxAngle : Mathf.DegToRad(45f));

    /// <summary>A lip: standable ground within BridgeLipM over or under a drag's end, its height.</summary>
    private float? LipAt(Vector3 end) => StandableBelow(end, GrowKindLaws.BridgeLipM, GrowKindLaws.BridgeLipM);

    /// <summary>Whether the deck over a point is over a true void: no ground within BridgeVoidDropM below it, or swimmable water there.</summary>
    private bool VoidUnder(Vector3 deck)
    {
        if (!IsInsideTree()) return false;
        using var query = PhysicsRayQueryParameters3D.Create(deck + Vector3.Up * 0.005f, deck + Vector3.Down * GrowKindLaws.BridgeVoidDropM, RoomBuilder.BodyMask, KernelNames.NoRids);
        using var hit = GetViewport().World3D.DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0) return true;
        var ground = hit[KernelNames.HitPosition].AsVector3();
        var water = WaterColumn(ground + Vector3.Up * 0.001f, deck.Y - ground.Y, 0.01f);
        return water.Wet && water.DepthM >= GrowKindLaws.BridgeSwimDepthM;
    }

    /// <summary>
    /// The one void the deck crosses, as metres along it from From: ground at both lips and one run of void between, its edges found to
    /// BridgeEdgeM; refused when there is none, or ground in its middle.
    /// </summary>
    private (float From, float To) VoidSpan(Vector3 from, Vector3 to)
    {
        var length = from.DistanceTo(to);
        var along = (to - from) / Math.Max(length, 1e-6f);
        var steps = Math.Max(2, (int)Math.Ceiling(length / GrowKindLaws.BridgeSampleM));
        var voids = new bool[steps + 1];
        for (var k = 0; k <= steps; k++) voids[k] = k > 0 && k < steps && VoidUnder(from + along * (length * k / steps));
        var first = Array.IndexOf(voids, true);
        if (first < 0) throw new Refusal("invalid_args", BridgeNoGap, "$.args.end_m");
        var last = Array.LastIndexOf(voids, true);
        for (var k = first; k <= last; k++)
            if (!voids[k]) throw new Refusal("invalid_args", BridgeTwoGaps, "$.args.end_m");
        float Edge(float ground, float hole)
        {
            while (Math.Abs(hole - ground) > GrowKindLaws.BridgeEdgeM)
            {
                var mid = (ground + hole) * 0.5f;
                if (VoidUnder(from + along * mid)) hole = mid;
                else ground = mid;
            }
            return hole;
        }
        return (Edge(length * (first - 1) / steps, length * first / steps), Edge(length * (last + 1) / steps, length * last / steps));
    }

    private BoxShape3D? _deckShape;

    /// <summary>Whether anything a body would bump into lies over the deck, from a step over it to BridgeClearM over it, lip to lip.</summary>
    private bool DeckBlocked(Vector3 from, Vector3 to)
    {
        if (!IsInsideTree()) return false;
        var length = from.DistanceTo(to);
        var tall = GrowKindLaws.BridgeClearM - GrowKindLaws.BridgeLevelM;
        _deckShape ??= new BoxShape3D();
        _deckShape.Size = new Vector3(GrowKindLaws.BridgeWidthM, tall, length);
        var forward = (to - from).Normalized();
        var basis = Basis.LookingAt(forward, Vector3.Up);
        var centre = (from + to) * 0.5f + Vector3.Up * (GrowKindLaws.BridgeLevelM + tall * 0.5f + 0.001f);
        using var query = new PhysicsShapeQueryParameters3D { Shape = _deckShape, Transform = new Transform3D(basis, centre), CollisionMask = RoomBuilder.BodyMask };
        return GetViewport().World3D.DirectSpaceState.IntersectShape(query, 1).Count > 0;
    }

    /// <summary>Whether a new deck would come within a deck's width of a bridge already there (and near its height).</summary>
    private static bool DecksMeet(NatureBridge bridge, Vector3 from, Vector3 to)
    {
        if (Math.Abs(bridge.FromM.Y - from.Y) > GrowKindLaws.BridgeClearM) return false;
        for (var k = 0; k <= 16; k++)
        {
            var point = from.Lerp(to, k / 16f);
            if (FlatToSegment(point, bridge.FromM, bridge.ToM) < GrowKindLaws.BridgeWidthM) return true;
            var theirs = bridge.FromM.Lerp(bridge.ToM, k / 16f);
            if (FlatToSegment(theirs, from, to) < GrowKindLaws.BridgeWidthM) return true;
        }
        return false;
    }

    /// <summary>A point's distance on the ground plane to a segment.</summary>
    private static float FlatToSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        var p = new Vector2(point.X, point.Z);
        var start = new Vector2(a.X, a.Z);
        var along = new Vector2(b.X, b.Z) - start;
        var t = Math.Clamp((p - start).Dot(along) / Math.Max(along.LengthSquared(), 1e-8f), 0f, 1f);
        return (start + along * t).DistanceTo(p);
    }

    /// <summary>Whether an avatar's body is where the deck's collision would be (over the void, at the deck's height): no growth then.</summary>
    private bool AvatarOverSpan(Vector3 from, Vector3 to, float spanFrom, float spanTo)
    {
        var along = (to - from).Normalized();
        foreach (var body in new SmallPlayerController?[] { Player, Companion })
        {
            if (body == null || !IsInstanceValid(body)) continue;
            var feet = body.GlobalPosition;
            var radius = body.BodyRadiusM + 0.005f;
            var t = (feet - from).Dot(along);
            var across = FlatToSegment(feet, from + along * spanFrom, from + along * spanTo);
            if (t >= spanFrom - radius && t <= spanTo + radius && across <= GrowKindLaws.BridgeWidthM * 0.5f + radius &&
                feet.Y <= from.Y + 0.005f && feet.Y + body.BodyHeightM >= from.Y - 0.005f) return true;
        }
        return false;
    }

    // ---- the scene and fire ----

    /// <summary>The room's bridges follow nature's (BridgeView under the room node, made with the first one). Returns whether what it shows changed.</summary>
    private bool ApplyBridges()
    {
        var room = GetParent()?.GetNodeOrNull<Node3D>(RoomNodePath);
        if (room == null || !IsInstanceValid(room)) return false;
        var view = room.GetNodeOrNull<BridgeView>(BridgeView.ViewName);
        if (view == null)
        {
            if (Nature.Bridges.Count == 0) return false;
            view = BridgeView.Create();
            room.AddChild(view);
        }
        return view.SetBridges(Nature.Bridges.Select(b => new BridgeView.Shown(b.Id, b.FromM, b.ToM, b.SpanFromM, b.SpanToM, b.Burn)).ToList());
    }

    /// <summary>A deck's bounds for catching fire: lip to lip, its width, from a little under the deck to a burning cell's height over it.</summary>
    private static Aabb BridgeBounds(NatureBridge bridge)
    {
        var half = GrowKindLaws.BridgeWidthM * 0.5f;
        var low = bridge.FromM.Min(bridge.ToM) - new Vector3(half, 0.03f, half);
        var high = bridge.FromM.Max(bridge.ToM) + new Vector3(half, 0.03f, half);
        return new Aabb(low, high - low);
    }

    /// <summary>Bridges near burning ground (at a lip, or on the deck) burn as plants do, slowed by rain; one at 1 is gone, its collision with it.</summary>
    private bool BurnBridges(float rate, Dictionary<(int, int), NatureCell> burning, NatureUndoStep? step)
    {
        var changed = false;
        foreach (var bridge in Nature.Bridges.ToList())
        {
            if (!Catches(BridgeBounds(bridge).Grow(PlantLaws.CatchM), burning)) continue;
            Nature.RememberBridges(step);
            bridge.Burn = Math.Min(1, bridge.Burn + rate);
            if (bridge.Burn >= 1) Nature.Bridges.Remove(bridge);
            changed = true;
        }
        return changed;
    }
}
