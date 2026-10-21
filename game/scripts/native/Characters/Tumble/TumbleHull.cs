using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;
using GarageGame.Native.Room;

namespace GarageGame.Native.Characters;

/// <summary>
/// The tumbling body (the plan's fallback, shipped: docs/runs/RUN-2-CHARACTERS.md, "The risk"): one Jolt rigid body shaped like
/// the character's convex hull (every part at rest but an aura, in the root bone's frame), made in the world's physics space for
/// the length of a tumble and freed after it. It is on no collision layer and masked to the world (RoomBuilder.BodyMask), so it
/// lands on the real terrain and never touches an avatar capsule (layers 2 and 4) or another tumble. It falls under the world's
/// gravity (world.set_physics), not the space's, and a tether at the root holds it near where the capsule stands. Made through
/// PhysicsServer3D: no nodes, nothing for the scene to save.
/// <para>
/// Jolt at this scale needs three corrections, each measured in the suite: (1) it leaves any penetration under its 2 cm slop
/// alone, so a body that rolls into the ground within one step stays there: each tick, rays from the centre to the hull's lowest
/// points find how far its surface is inside the world and the body is put back out; (2) its hull of the given points lies up to
/// 3.4 mm inside them, so where its surface really is along each of those rays is measured once per character and the
/// correction aims at that, never fighting Jolt's own resting contact; (3) the drawn parts stand out of Jolt's surface by those
/// millimetres, so the same rays lift the drawn character (only) by what would still show below the ground.
/// </para>
/// </summary>
public sealed class TumbleHull : IDisposable
{
    /// <summary>The farthest the root may drift from where the capsule stands, along the ground (m): the tether's rope.</summary>
    public const float TetherReachM = 0.018f;
    /// <summary>Past the rope's length by this much (m), a fast turn got the root past it within a step: it is set back at once.</summary>
    public const float TetherGiveM = 0.003f;
    /// <summary>The tether's spring pulls only past this distance (m), so a body at rest near its anchor is left alone.</summary>
    public const float TetherSlackM = 0.01f;
    public const float Friction = 0.8f;
    /// <summary>Plush, a little lighter than water (kg/m³); only the mass's ratio to the forces matters.</summary>
    public const float Density = 500f;
    /// <summary>
    /// Damping (1/s): light while the body is thrown and lands (the first <see cref="FreeS"/>), then rising to its resting values by
    /// <see cref="SettleByS"/>, the rolling resistance Jolt does not model, so an egg-shaped body stops instead of rocking on.
    /// </summary>
    public const float LinearDamp = 1.0f, AngularDamp = 2.0f, RestingLinearDamp = 4.0f, RestingAngularDamp = 6.0f;
    public const float FreeS = 0.25f, SettleByS = 0.5f;
    /// <summary>
    /// The settle: from <see cref="SettleByS"/> the body's motion is eased out (a smooth step to nothing) and at
    /// <see cref="RestByS"/> it is at rest where it lies, so a flop always lies still within 1 s, whether it found a stable side,
    /// balanced on a round back or was still rolling, without any sudden stop.
    /// </summary>
    public const float RestByS = 0.95f;
    /// <summary>A penetration of Jolt's surface deeper than this is put right each tick (m).</summary>
    public const float CorrectAboveM = 0.0005f;
    /// <summary>The lowest points (along gravity) whose rays are cast each tick, and how far above the lowest one they may lie (m).</summary>
    public const int RayPoints = 32;
    public const float RayBandM = 0.04f;
    /// <summary>
    /// The hull's points: the parts' points farthest along evenly spread directions, as many directions as keep within
    /// <see cref="MaxHullPoints"/> (Jolt keeps at most 256), then the parts' points still outside that hull, each pushed out by
    /// <see cref="HullGrowM"/>; a <see cref="HullMarginM"/> convex radius (Jolt rounds corners by the margin).
    /// </summary>
    public const int HullDirections = 1200, MaxHullPoints = 240;
    public const float HullGrowM = 0.001f, HullMarginM = 0.0005f;
    /// <summary>Where the once-per-character measurement of Jolt's surface happens (far from any room), and its private layer.</summary>
    private static readonly Vector3 MeasureAt = new(0, -5000, 0);
    private const uint MeasureLayer = 1u << 30;

    private sealed class Shape
    {
        public required ConvexPolygonShape3D Hull;
        public required Vector3[] Points;
        public required Vector3 CentreOfMass;
        public required float Mass;
        public required float Reach;
        /// <summary>Along the ray from the centre to each point, how far in from it Jolt's surface lies (m); null until measured.</summary>
        public float[]? Inset;
    }

    private static readonly ConditionalWeakTable<CharacterDefinition, Shape> Shapes = new();

    private readonly Shape _shape;
    private readonly int[] _candidates = new int[RayPoints];
    /// <summary>
    /// The ray every tick casts, a top-level child of the body cast by hand: through a node a ray allocates nothing, where the
    /// space's query answers each ray with a new dictionary (review minor 9). The query below is for a body out of the tree.
    /// </summary>
    private readonly RayCast3D? _caster;
    /// <summary>One ray query, reused, for when there is no ray node in the tree.</summary>
    private readonly PhysicsRayQueryParameters3D _ray = new() { CollisionMask = RoomBuilder.BodyMask, CollideWithAreas = false, CollideWithBodies = true };
    /// <summary>The physics space's own gravity (the project setting), read once.</summary>
    private static float _spaceGravity = float.NaN;
    private readonly float[] _candidateY = new float[RayPoints];
    private Rid _body;
    /// <summary>
    /// The body's direct state, kept while it lives: its transform is read through it each tick, because a Transform3D read as a
    /// Variant (PhysicsServer3D.BodyGetState) costs a managed allocation every time.
    /// </summary>
    private PhysicsDirectBodyState3D? _direct;
    private World3D? _world;
    /// <summary>Last tick's push out of the world, a velocity (taken back this tick once the step has used it).</summary>
    private Vector3 _push;
    private float _ageS;
    private float _damping = -1;

    private TumbleHull(Shape shape, Node owner)
    {
        _shape = shape;
        _caster = new RayCast3D
        {
            Name = "TumbleRay", TopLevel = true, Enabled = false, ExcludeParent = false, CollisionMask = RoomBuilder.BodyMask,
            CollideWithAreas = false, CollideWithBodies = true, HitFromInside = false, HitBackFaces = true, TargetPosition = Vector3.Down * 0.01f,
        };
        owner.AddChild(_caster);
    }

    /// <summary>The hull for a body's character (built once per character, from its rest pose), casting its rays from a node of the body's.</summary>
    public static TumbleHull For(CharacterBody body) => new(Shapes.GetValue(body.Definition, _ => Build(body)), body);

    public int Points => _shape.Points.Length;
    public float Mass => _shape.Mass;
    public Vector3 CentreOfMass => _shape.CentreOfMass;
    /// <summary>The farthest the hull reaches from its centre of mass (m): its fastest point moves at |v| + |ω| · reach.</summary>
    public float ReachM => _shape.Reach;
    public bool Active => _body.IsValid;
    /// <summary>The body lies still where the settle left it: no longer simulated, its transform kept until the get-up.</summary>
    public bool Resting { get; private set; }
    /// <summary>The convex hull Jolt simulates (shared by every body of the character).</summary>
    public ConvexPolygonShape3D HullShape => _shape.Hull;
    /// <summary>The deepest Jolt's surface lies inside the given points (m), once measured (-1 before).</summary>
    public float MaxInsetM
    {
        get
        {
            if (_shape.Inset == null) return -1;
            var most = 0f;
            foreach (var inset in _shape.Inset) most = Mathf.Max(most, inset);
            return most;
        }
    }
    /// <summary>The body's transform (the root bone's frame, in world space) after this tick's corrections.</summary>
    public Transform3D Transform { get; private set; }
    /// <summary>What the drawn character is lifted by this tick, world space (m): the parts' last millimetres out of the ground.</summary>
    public Vector3 DrawnLift { get; private set; }
    public Vector3 LinearVelocity { get; private set; }
    public Vector3 AngularVelocity { get; private set; }
    /// <summary>How far the body has been put back out of the world this tumble (m, summed).</summary>
    public float CorrectedM { get; private set; }
    /// <summary>The fastest any point of the body moved as of the last step (m/s).</summary>
    public float FastestPointMps => LinearVelocity.Length() + AngularVelocity.Length() * _shape.Reach;

    private static Shape Build(CharacterBody body)
    {
        var points = new List<Vector3>();
        var low = Vector3.Inf;
        var high = -Vector3.Inf;
        var root = body.Bones[CharacterBody.RootBone].RestModel;
        for (var i = 0; i < body.BoneCount; i++)
        {
            var bone = body.Bones[i];
            var mesh = body.PartMesh(i);
            // Every part a tumble could put on the ground, sparkles floating at the feet included; not an aura, which wraps the
            // whole body as a glow rather than a thing.
            if (!bone.HasMesh || bone.Role == PartRole.Aura || mesh?.Mesh == null) continue;
            if (mesh.Mesh.CreateConvexShape(true, false) is not ConvexPolygonShape3D part) continue;
            var place = body.Skeleton.GetBoneGlobalRest(i) * mesh.Transform;
            foreach (var point in part.Points)
            {
                var local = place * point - root;
                points.Add(local);
                low = low.Min(local);
                high = high.Max(local);
            }
        }
        if (points.Count < 4)
        {
            low = new Vector3(-0.02f, 0, -0.02f);
            high = new Vector3(0.02f, 0.1f, 0.02f);
            points.Clear();
            for (var c = 0; c < 8; c++)
                points.Add(new Vector3((c & 1) != 0 ? high.X : low.X, (c & 2) != 0 ? high.Y : low.Y, (c & 4) != 0 ? high.Z : low.Z));
        }
        var size = high - low;
        // An egg in its box: about half the box's volume, at plush density.
        var mass = Mathf.Max(0.5f * size.X * size.Y * size.Z * Density, 0.002f);
        var centre = (low + high) / 2;
        var support = Contain(points, centre, Support(points, centre));
        var hull = new ConvexPolygonShape3D { Points = support, Margin = HullMarginM };
        var reach = 0f;
        foreach (var point in support) reach = Mathf.Max(reach, point.DistanceTo(centre));
        return new Shape { Hull = hull, Points = support, CentreOfMass = centre, Mass = mass, Reach = reach };
    }

    /// <summary>The points farthest along evenly spread directions (a Fibonacci sphere): as many directions as keep room under the cap.</summary>
    private static List<int> Support(List<Vector3> points, Vector3 centre)
    {
        for (var directions = HullDirections; ; directions = directions * 7 / 10)
        {
            var chosen = new HashSet<int>();
            var golden = Mathf.Pi * (3 - Mathf.Sqrt(5));
            for (var i = 0; i < directions; i++)
            {
                var y = 1 - 2 * (i + 0.5f) / directions;
                var ring = Mathf.Sqrt(1 - y * y);
                var direction = new Vector3(Mathf.Cos(golden * i) * ring, y, Mathf.Sin(golden * i) * ring);
                var best = 0;
                var farthest = float.NegativeInfinity;
                for (var p = 0; p < points.Count; p++)
                {
                    var along = (points[p] - centre).Dot(direction);
                    if (along > farthest) { farthest = along; best = p; }
                }
                chosen.Add(best);
            }
            if (chosen.Count <= MaxHullPoints * 3 / 4 || directions < 50) return new List<int>(chosen);
        }
    }

    /// <summary>
    /// Adds the parts' points still outside the support points' hull (farthest first, within the cap), then pushes every point out
    /// from the centre by <see cref="HullGrowM"/> plus whatever is still outside, so the hull holds every part.
    /// </summary>
    private static Vector3[] Contain(List<Vector3> points, Vector3 centre, List<int> chosen)
    {
        var outside = Outside(points, centre, chosen, out var distances);
        if (outside.Count > 0)
        {
            outside.Sort((a, b) => distances[b].CompareTo(distances[a]));
            for (var i = 0; i < outside.Count && chosen.Count < MaxHullPoints; i++) chosen.Add(outside[i]);
            Outside(points, centre, chosen, out distances);
        }
        var still = 0f;
        foreach (var distance in distances) still = Mathf.Max(still, distance);
        var grow = HullGrowM + still;
        var result = new Vector3[chosen.Count];
        for (var i = 0; i < chosen.Count; i++)
        {
            var point = points[chosen[i]];
            var radial = point - centre;
            result[i] = point + (radial.LengthSquared() > 1e-12f ? radial.Normalized() : Vector3.Up) * grow;
        }
        return result;
    }

    /// <summary>The points more than 0.3 mm outside the hull of <paramref name="chosen"/>, and every point's distance outside it.</summary>
    private static List<int> Outside(List<Vector3> points, Vector3 centre, List<int> chosen, out float[] distances)
    {
        var vertices = new Vector3[chosen.Count];
        for (var i = 0; i < chosen.Count; i++) vertices[i] = points[chosen[i]];
        var faces = new ConvexPolygonShape3D { Points = vertices }.GetDebugMesh().GetFaces();
        var planes = new List<Plane>(faces.Length / 3);
        for (var f = 0; f + 2 < faces.Length; f += 3)
        {
            var plane = new Plane(faces[f], faces[f + 1], faces[f + 2]);
            if (!plane.Normal.IsNormalized()) continue;
            planes.Add(plane.DistanceTo(centre) > 0 ? -plane : plane);
        }
        distances = new float[points.Count];
        var outside = new List<int>();
        for (var p = 0; p < points.Count; p++)
        {
            var farthest = float.NegativeInfinity;
            foreach (var plane in planes) farthest = Mathf.Max(farthest, plane.DistanceTo(points[p]));
            distances[p] = Mathf.Max(0, farthest);
            if (farthest > 0.0003f) outside.Add(p);
        }
        return outside;
    }

    /// <summary>
    /// Where Jolt's surface really lies along the ray from the centre to each point, measured once per character with a static copy
    /// of the hull far below the world on a layer of its own, and freed at once.
    /// </summary>
    private void MeasureInset(World3D world)
    {
        if (_shape.Inset != null) return;
        var inset = new float[_shape.Points.Length];
        var probe = PhysicsServer3D.BodyCreate();
        try
        {
            PhysicsServer3D.BodySetMode(probe, PhysicsServer3D.BodyMode.Static);
            PhysicsServer3D.BodyAddShape(probe, _shape.Hull.GetRid(), Transform3D.Identity);
            PhysicsServer3D.BodySetCollisionLayer(probe, MeasureLayer);
            PhysicsServer3D.BodySetCollisionMask(probe, 0);
            PhysicsServer3D.BodySetState(probe, PhysicsServer3D.BodyState.Transform, new Transform3D(Basis.Identity, MeasureAt));
            PhysicsServer3D.BodySetSpace(probe, world.Space);
            var space = world.DirectSpaceState;
            var c = _shape.CentreOfMass;
            var hits = 0;
            for (var i = 0; i < inset.Length; i++)
            {
                var radial = _shape.Points[i] - c;
                var length = radial.Length();
                if (length < 1e-5f) continue;
                var outward = radial / length;
                using var query = PhysicsRayQueryParameters3D.Create(MeasureAt + _shape.Points[i] + outward * 0.02f, MeasureAt + c, MeasureLayer);
                using var hit = space.IntersectRay(query);
                if (hit.Count == 0) continue;
                hits++;
                inset[i] = Mathf.Clamp(length - (hit["position"].AsVector3() - MeasureAt - c).Length(), 0, 0.01f);
            }
            MeasuredRays = hits;
        }
        finally
        {
            PhysicsServer3D.FreeRid(probe);
        }
        _shape.Inset = inset;
    }

    /// <summary>How many of the once-per-character measuring rays met Jolt's surface (the suite checks they all did).</summary>
    public int MeasuredRays { get; private set; } = -1;

    /// <summary>
    /// Makes the body at <paramref name="start"/> (the root bone's frame in world space) in <paramref name="world"/>'s space, moving
    /// with <paramref name="kick"/> (velocity at the centre of mass, and spin), falling at <paramref name="gravityMps2"/>.
    /// </summary>
    public void Begin(World3D world, Transform3D start, TumbleKick kick, float gravityMps2)
    {
        End();
        MeasureInset(world);
        _world = world;
        _ageS = 0;
        _damping = -1;
        _push = Vector3.Zero;
        CorrectedM = 0;
        Resting = false;
        _body = PhysicsServer3D.BodyCreate();
        PhysicsServer3D.BodySetMode(_body, PhysicsServer3D.BodyMode.Rigid);
        PhysicsServer3D.BodyAddShape(_body, _shape.Hull.GetRid(), Transform3D.Identity);
        PhysicsServer3D.BodySetCollisionLayer(_body, 0);
        PhysicsServer3D.BodySetCollisionMask(_body, RoomBuilder.BodyMask);
        PhysicsServer3D.BodySetParam(_body, PhysicsServer3D.BodyParameter.Mass, _shape.Mass);
        PhysicsServer3D.BodySetParam(_body, PhysicsServer3D.BodyParameter.CenterOfMass, _shape.CentreOfMass);
        PhysicsServer3D.BodySetParam(_body, PhysicsServer3D.BodyParameter.Friction, Friction);
        PhysicsServer3D.BodySetParam(_body, PhysicsServer3D.BodyParameter.Bounce, 0f);
        PhysicsServer3D.BodySetParam(_body, PhysicsServer3D.BodyParameter.LinearDampMode, (int)PhysicsServer3D.BodyDampMode.Replace);
        PhysicsServer3D.BodySetParam(_body, PhysicsServer3D.BodyParameter.AngularDampMode, (int)PhysicsServer3D.BodyDampMode.Replace);
        SetDamping(0);
        PhysicsServer3D.BodySetParam(_body, PhysicsServer3D.BodyParameter.GravityScale, GravityScale(gravityMps2));
        PhysicsServer3D.BodySetEnableContinuousCollisionDetection(_body, true);
        // A body standing flat-footed on a slope, or across a step's edge, starts partly inside the ground: out with it first.
        start = Corrected(start, out _, out var drawn);
        PhysicsServer3D.BodySetState(_body, PhysicsServer3D.BodyState.Transform, start);
        PhysicsServer3D.BodySetSpace(_body, world.Space);
        _direct = PhysicsServer3D.BodyGetDirectState(_body);
        // The kick is the motion of the centre of mass; the body's spin carries the rest.
        PhysicsServer3D.BodySetState(_body, PhysicsServer3D.BodyState.LinearVelocity, kick.Linear);
        PhysicsServer3D.BodySetState(_body, PhysicsServer3D.BodyState.AngularVelocity, kick.Angular);
        Transform = start;
        DrawnLift = drawn;
        LinearVelocity = kick.Linear;
        AngularVelocity = kick.Angular;
    }

    /// <summary>Adds a push (m/s at the centre of mass) and a spin to the tumbling body: a second hazard while it tumbles.</summary>
    public void Kick(TumbleKick kick)
    {
        if (!_body.IsValid) return;
        PhysicsServer3D.BodyApplyCentralImpulse(_body, kick.Linear * _shape.Mass);
        PhysicsServer3D.BodySetState(_body, PhysicsServer3D.BodyState.AngularVelocity, AngularVelocity + kick.Angular);
    }

    /// <summary>
    /// One tick: reads where the last step left the body, holds its root to <paramref name="anchor"/> (the capsule's root, world
    /// space) along the ground, puts it back out of the world, and works out what the drawn character still needs lifting.
    /// </summary>
    public void Tick(Vector3 anchor, float gravityMps2, float deltaS)
    {
        if (!_body.IsValid) return;
        if (_direct == null || !GodotObject.IsInstanceValid(_direct)) _direct = PhysicsServer3D.BodyGetDirectState(_body);
        if (_direct == null) { End(); return; }
        var transform = _direct.Transform;
        var linear = _direct.LinearVelocity;
        var angular = _direct.AngularVelocity;
        if (!transform.Origin.IsFinite() || !linear.IsFinite() || !angular.IsFinite()) { End(); return; }
        LinearVelocity = linear;
        AngularVelocity = angular;
        PhysicsServer3D.BodySetParam(_body, PhysicsServer3D.BodyParameter.GravityScale, GravityScale(gravityMps2));
        _ageS += deltaS;
        SetDamping(_ageS);
        // Last tick's push out of the world has done its work in the step: take back what of it is left.
        if (_push != Vector3.Zero)
        {
            var normal = _push.Normalized();
            linear -= normal * Mathf.Clamp(linear.Dot(normal), 0, _push.Length());
            _push = Vector3.Zero;
        }
        // The tether, on the centre of mass (a central impulse turns nothing, so it can never wind the body up): a spring while
        // the body is thrown and lands, past a little slack; once it is down only the rope, so nothing keeps nudging a round body
        // that would roll to and fro about its anchor.
        var offset = transform.Origin - anchor;
        offset.Y = 0;
        var along = linear with { Y = 0 };
        var distance = offset.Length();
        var change = Vector3.Zero;
        if (distance > TetherSlackM && _ageS < SettleByS)
            change = (-TumbleLaunch.TetherStiffness * offset * ((distance - TetherSlackM) / distance) - TumbleLaunch.TetherDamping * along) * deltaS;
        if (distance > TetherReachM)
        {
            // The rope: the root stops moving away, whatever pushes or turns it (pulling it back in would only set a lying body
            // rocking against the rope); past it by more than the give (a fast turn within one step) it is set back at once.
            var outward = offset / distance;
            var rootVelocity = linear + angular.Cross(transform.Origin - transform * _shape.CentreOfMass) + change;
            var away = rootVelocity.Dot(outward);
            if (away > 0) change -= outward * away;
            if (distance > TetherReachM + TetherGiveM)
            {
                transform = transform.Translated(-outward * (distance - TetherReachM));
                _direct.Transform = transform;
            }
        }
        // Out of the world: Jolt leaves anything under its slop, so the body is sent back out within the next step, by velocity
        // (moving it would cost Jolt its contacts for a step, and a resting body would fall through and be lifted again forever).
        Depths(transform, out var surface, out var drawn);
        if (surface.LengthSquared() > CorrectAboveM * CorrectAboveM)
        {
            var normal = surface.Normalized();
            var into = (linear + change).Dot(normal);
            if (into < 0) change -= normal * into;
            _push = surface / Mathf.Max(deltaS, 1e-4f);
            change += _push;
            CorrectedM += surface.Length();
        }
        Transform = transform;
        DrawnLift = drawn;
        var velocity = linear + change;
        // The settle: ease the motion out, then lie still.
        if (_ageS >= RestByS)
        {
            Rest();
            return;
        }
        if (_ageS > SettleByS)
        {
            var t = Mathf.Clamp((_ageS - SettleByS) / (RestByS - SettleByS), 0, 1);
            var keep = 1 - t * t * (3 - 2 * t);
            var spin = angular * keep;
            velocity = (velocity - _push) * keep + _push;
            PhysicsServer3D.BodySetState(_body, PhysicsServer3D.BodyState.AngularVelocity, spin);
            AngularVelocity = spin;
        }
        if (velocity != LinearVelocity) PhysicsServer3D.BodySetState(_body, PhysicsServer3D.BodyState.LinearVelocity, velocity);
        LinearVelocity = velocity;
    }

    /// <summary>
    /// While the body lies still, the capsule may still be sliding to a stop: the rope keeps the resting body's root within reach of
    /// <paramref name="anchor"/> (world space) along the ground.
    /// </summary>
    public void Follow(Vector3 anchor)
    {
        if (!Resting) return;
        var offset = Transform.Origin - anchor;
        offset.Y = 0;
        var distance = offset.Length();
        if (distance > TetherReachM) Transform = Transform.Translated(-offset / distance * (distance - TetherReachM));
    }

    /// <summary>Lies still where it is: the body leaves the physics space, its transform and drawn lift kept for the get-up.</summary>
    private void Rest()
    {
        if (_body.IsValid) PhysicsServer3D.FreeRid(_body);
        _body = default;
        _direct = null;
        _push = Vector3.Zero;
        LinearVelocity = Vector3.Zero;
        AngularVelocity = Vector3.Zero;
        Resting = true;
    }

    /// <summary>
    /// Rays from the centre to the hull's lowest points (along the ground's normal): how far Jolt's surface (the measured inset short
    /// of each point) lies inside the world (<paramref name="surface"/>, along the normal of the surface the ray met), and how far the
    /// points themselves, the drawn parts' outline, do (<paramref name="drawn"/>). Zero vectors when clear.
    /// </summary>
    private void Depths(Transform3D transform, out Vector3 surface, out Vector3 drawn)
    {
        surface = Vector3.Zero;
        drawn = Vector3.Zero;
        if (_world != null) Depths(_world, transform, out surface, out drawn);
    }

    /// <summary>
    /// How far the drawn parts' outline would lie inside <paramref name="world"/> with the root bone's frame at
    /// <paramref name="transform"/> (world space), along the normal of what it meets: the get-up keeps its blend out of the ground
    /// with it. A zero vector when clear.
    /// </summary>
    public Vector3 DrawnDepth(World3D world, Transform3D transform)
    {
        Depths(world, transform, out _, out var drawn);
        return drawn;
    }

    /// <summary>
    /// <see cref="DrawnDepth(World3D, Transform3D)"/> in the world of the body the hull belongs to, through its ray node (no world
    /// to fetch: the get-up asks twice a tick). A zero vector when clear, or while the body is out of the tree.
    /// </summary>
    public Vector3 DrawnDepth(Transform3D transform)
    {
        if (!CasterReady) return Vector3.Zero;
        Depths(null, transform, out _, out var drawn);
        return drawn;
    }

    private void Depths(World3D? world, Transform3D transform, out Vector3 surface, out Vector3 drawn)
    {
        surface = Vector3.Zero;
        drawn = Vector3.Zero;
        var space = CasterReady ? null : world?.DirectSpaceState;
        if (space == null && !CasterReady) return;
        var centre = transform * _shape.CentreOfMass;
        var ground = Vector3.Up;
        if (Cast(space, centre, centre + Vector3.Down * (_shape.Reach + RayBandM), out _, out var below) && below.IsNormalized()) ground = below;
        var count = Lowest(transform, ground);
        var deepestSurface = 0f;
        var deepestPoint = 0f;
        for (var k = 0; k < count; k++)
        {
            var index = _candidates[k];
            var point = transform * _shape.Points[index];
            var ray = point - centre;
            var length = ray.Length();
            if (length < 1e-5f) continue;
            if (!Cast(space, centre, point, out var at, out var normal) || !normal.IsNormalized()) continue;
            var cos = Mathf.Max(-(ray / length).Dot(normal), 0.2f);
            // Along the normal: how far the point, and Jolt's surface short of it, lie behind the surface the ray met.
            var pointDepth = (at - point).Dot(normal);
            var surfaceDepth = pointDepth - (_shape.Inset?[index] ?? 0) * cos;
            if (surfaceDepth > deepestSurface) { deepestSurface = surfaceDepth; surface = normal * surfaceDepth; }
            if (pointDepth > deepestPoint) { deepestPoint = pointDepth; drawn = normal * pointDepth; }
        }
    }

    private bool CasterReady => _caster != null && GodotObject.IsInstanceValid(_caster) && _caster.IsInsideTree();

    /// <summary>One ray from <paramref name="from"/> to <paramref name="to"/> (world space): the node's when it is in the tree, else the space's query.</summary>
    private bool Cast(PhysicsDirectSpaceState3D? space, Vector3 from, Vector3 to, out Vector3 position, out Vector3 normal)
    {
        position = normal = Vector3.Zero;
        if (CasterReady)
        {
            _caster!.GlobalTransform = new Transform3D(Basis.Identity, from);
            _caster.TargetPosition = to - from;
            _caster.ForceRaycastUpdate();
            if (!_caster.IsColliding()) return false;
            position = _caster.GetCollisionPoint();
            normal = _caster.GetCollisionNormal();
            return true;
        }
        if (space == null) return false;
        _ray.From = from;
        _ray.To = to;
        using var hit = space.IntersectRay(_ray);
        if (hit.Count == 0) return false;
        position = hit["position"].AsVector3();
        normal = hit["normal"].AsVector3();
        return true;
    }

    /// <summary>The body moved straight out of the world (its start): Jolt's surface out, and what the drawing still needs.</summary>
    private Transform3D Corrected(Transform3D transform, out Vector3 push, out Vector3 drawn)
    {
        Depths(transform, out push, out drawn);
        if (push.LengthSquared() <= CorrectAboveM * CorrectAboveM)
        {
            push = Vector3.Zero;
            return transform;
        }
        if (drawn != Vector3.Zero)
        {
            var along = drawn.Normalized();
            drawn = along * Mathf.Max(0, drawn.Length() - push.Dot(along));
        }
        return transform.Translated(push);
    }

    /// <summary>
    /// The hull's points lowest along the ground's normal under <paramref name="transform"/> (so the uphill side on a slope too): at
    /// most <see cref="RayPoints"/>, within <see cref="RayBandM"/> of the lowest.
    /// </summary>
    private int Lowest(Transform3D transform, Vector3 up)
    {
        var points = _shape.Points;
        var lowest = float.MaxValue;
        foreach (var point in points) lowest = Mathf.Min(lowest, (transform * point).Dot(up));
        var count = 0;
        for (var i = 0; i < points.Length; i++)
        {
            var y = (transform * points[i]).Dot(up);
            if (y > lowest + RayBandM) continue;
            if (count < RayPoints)
            {
                _candidates[count] = i;
                _candidateY[count++] = y;
                continue;
            }
            // Full: replace the highest kept when this one is lower.
            var worst = 0;
            for (var k = 1; k < count; k++)
                if (_candidateY[k] > _candidateY[worst]) worst = k;
            if (y < _candidateY[worst])
            {
                _candidates[worst] = i;
                _candidateY[worst] = y;
            }
        }
        return count;
    }

    private void SetDamping(float ageS)
    {
        var t = Mathf.Clamp((ageS - FreeS) / (SettleByS - FreeS), 0, 1);
        if (Mathf.IsEqualApprox(t, _damping)) return;
        _damping = t;
        PhysicsServer3D.BodySetParam(_body, PhysicsServer3D.BodyParameter.LinearDamp, Mathf.Lerp(LinearDamp, RestingLinearDamp, t));
        PhysicsServer3D.BodySetParam(_body, PhysicsServer3D.BodyParameter.AngularDamp, Mathf.Lerp(AngularDamp, RestingAngularDamp, t));
    }

    /// <summary>Frees the body (the get-up, or the tumble's end).</summary>
    public void End()
    {
        if (_body.IsValid) PhysicsServer3D.FreeRid(_body);
        _body = default;
        _direct = null;
        _world = null;
        Resting = false;
        DrawnLift = Vector3.Zero;
    }

    public void Dispose() => End();

    /// <summary>
    /// The world's gravity over the space's: the space keeps the project's default (9.8 m/s², nothing changes it), while the room
    /// falls at world.set_physics' gravity, which the capsule applies itself and the body state reports.
    /// </summary>
    private static float GravityScale(float gravityMps2)
    {
        if (float.IsNaN(_spaceGravity))
        {
            var space = (float)ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8).AsDouble();
            _spaceGravity = float.IsFinite(space) && space > 0 ? space : 9.8f;
        }
        return (float.IsFinite(gravityMps2) && gravityMps2 > 0 ? gravityMps2 : 3.5f) / _spaceGravity;
    }
}
