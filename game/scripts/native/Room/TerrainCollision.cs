using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace GarageGame.Native.Room;

/// <summary>
/// The tiled land's collision where the bodies are (the 10x world, packet S12b). Each colliding tiled part's 16 m tiles collide
/// through their level-0 ground (TiledPart.GroundFaces), one static body and one one-sided shape per tile made on the physics
/// server (no node and no Shape3D resource, so the faces are held once, by the server). The hits name the part's own node, which
/// keeps its id, roles and metadata, as a CollisionShape3D under it did.
///
/// Every tile is built when the room enters the scene, so whatever the boot asks of the land (a save's creations checked on
/// their surfaces, the ponds surveyed, the island's cameras) finds it all. Once the bodies have stood in the room for BootHoldS,
/// a tile is kept only where something may touch it:
/// <list type="bullet">
/// <item>round every moving body (every CharacterBody3D and RigidBody3D in the world, and any node in AnchorGroup): within NearM,
/// and along where it is going (its velocity for LeadS, LeadRadiusM either side), built before it gets there;</item>
/// <item>under it now: anything within SyncM of a body, or of where it will be in two ticks, is built in the same physics tick,
/// before any body moves (this node's physics step runs first), so a body that was put somewhere (caught up, sent home, wrapped
/// round the sea) never meets missing ground;</item>
/// <item>round the places a body may be put without walking there (the spawns, the jetty's root, every beach's landing, each
/// body's last safe footing) and round a fire burning on the land: within PinM, always;</item>
/// <item>wherever something asked for it (EnsureAround), for as long as it asked: the kernel asks before it checks a creation's
/// or a thing's footing far from the bodies (FarActionM round the spot).</item>
/// </list>
/// Where a tile's walkable ground is not built its far ground is (the land's next level, RoomBuilder.FarGroundLayer, built at
/// boot for every drawn tile and kept): sight and aim rays ask for it, so far hills still hide and stop them; no body collides
/// with it and nothing that supports or measures ground asks for it. Where the walkable ground is built, the far ground is not
/// in the world, so a ray meets the fine one.
/// A tile no longer needed goes after ReleaseAfterS, and only once it is more than NearM + ReleaseSlackM from every body. A room
/// with no moving body keeps every tile (tests and tools that build a room without avatars see the whole land, as before).
/// Navigation never reads this: it bakes the ground from the room's data (IslandNavigation).
/// </summary>
public partial class TerrainCollision : Node3D
{
    public const string ViewName = "TerrainCollision";
    /// <summary>A node in this group is kept on built ground like a body (something physics moves that is neither a CharacterBody3D nor a RigidBody3D).</summary>
    public const string AnchorGroup = "terrain_anchor";
    public const float NearM = 16f, LeadS = 1.5f, MaxLeadM = 40f, LeadRadiusM = 8f, SyncM = 4f, PinM = 6f, ReleaseSlackM = 8f;
    public const double ReleaseAfterS = 3.0, BootHoldS = 2.0;
    /// <summary>How far round a far action's spot (a creation placed, a thing put down) the ground is built before it is checked.</summary>
    public const float FarActionM = 4f;
    /// <summary>The physics tick's time for tiles built ahead of the bodies (at least one a tick while any is wanted).</summary>
    public const double BuildBudgetMs = 1.5;

    private sealed class Tile
    {
        public required TiledPart Part;
        public required int Index;
        public required Node3D Owner;
        public required uint Layer;
        public required Rect2 Square;
        public Rid Body;
        public Rid Shape;
        public int Triangles;
        /// <summary>The far ground (RoomBuilder.FarGroundLayer): the tile's coarse level, in the world only while Body is not.</summary>
        public Rid FarBody;
        public Rid FarShape;
        public int FarTriangles;
        public double UnneededS;
        public double HeldUntilS = double.NegativeInfinity;
        public bool Wanted;
        public float Distance;
        public bool Built => Body.IsValid;
    }

    private Tile[] _tiles = Array.Empty<Tile>();
    private RoomData _room = null!;
    private readonly List<(Vector3 At, float RadiusM)> _pins = new();
    private readonly List<(Vector3 From, Vector3 To, Vector3 Next)> _bodies = new();
    private readonly List<Tile> _queue = new();
    private double _timeS, _bodiesForS;
    private Node? _host;
    private double _hostCheckedS = double.NegativeInfinity;

    /// <summary>Tiles in all, built now, and the most ever built at once; tiles built and released since the room entered the scene.</summary>
    public int TileCount => _tiles.Length;
    public int TilesBuilt { get; private set; }
    public int MostTilesBuilt { get; private set; }
    public int Builds { get; private set; }
    public int Releases { get; private set; }
    /// <summary>Tiles built inside the physics tick that needed them (a body about to stand on missing ground), and the longest such tick.</summary>
    public int SyncBuilds { get; private set; }
    public double LongestSyncMs { get; private set; }
    /// <summary>Ground triangles the built tiles hold now.</summary>
    public long TrianglesBuilt { get; private set; }

    private static readonly List<WeakReference<TerrainCollision>> Live = new();

    /// <summary>The collision of a room's colliding tiled parts (owners: each part's node, keyed by part id), or null when it has none.</summary>
    public static TerrainCollision? Create(RoomData room, IReadOnlyDictionary<string, (Node3D Node, uint Layer)> owners)
    {
        if (room.Terrain is not { } terrain) return null;
        var tiles = new List<Tile>();
        foreach (var part in terrain.Parts)
        {
            if (!owners.TryGetValue(part.PartId, out var owner)) continue;
            for (var i = 0; i < part.Tiles.Count; i++)
            {
                var (tx, tz, _) = part.Tiles[i];
                tiles.Add(new Tile
                {
                    Part = part, Index = i, Owner = owner.Node, Layer = owner.Layer,
                    Square = new Rect2(tx * TerrainTiles.TileM, tz * TerrainTiles.TileM, TerrainTiles.TileM, TerrainTiles.TileM),
                });
            }
        }
        if (tiles.Count == 0) return null;
        var view = new TerrainCollision { Name = ViewName, _room = room, _tiles = tiles.ToArray(), ProcessPhysicsPriority = int.MinValue };
        // The boot builds every tile: the ground read on worker threads and handed to the physics server now (its faces are
        // then the server's alone), the bodies made as the room enters the scene.
        var faces = new Vector3[tiles.Count][];
        System.Threading.Tasks.Parallel.For(0, tiles.Count, i => faces[i] = tiles[i].Part.GroundFaces(tiles[i].Index));
        for (var i = 0; i < tiles.Count; i++)
        {
            view.Shape(tiles[i], faces[i]);
            faces[i] = null!;
        }
        // The far ground of every drawn tile, kept for the room's life: what sight and aim meet where the ground is not built.
        var far = tiles.Where(t => t.Part.HasCoarse && t.Layer == RoomBuilder.WorldLayer).ToArray();
        var coarse = new Vector3[far.Length][];
        System.Threading.Tasks.Parallel.For(0, far.Length, i => coarse[i] = far[i].Part.CoarseFaces(far[i].Index));
        for (var i = 0; i < far.Length; i++)
        {
            var tile = far[i];
            tile.FarShape = PhysicsServer3D.ConcavePolygonShapeCreate();
            PhysicsServer3D.ShapeSetData(tile.FarShape, new Godot.Collections.Dictionary { ["faces"] = coarse[i], ["backface_collision"] = false });
            tile.FarTriangles = coarse[i].Length / 3;
            tile.FarBody = PhysicsServer3D.BodyCreate();
            PhysicsServer3D.BodySetMode(tile.FarBody, PhysicsServer3D.BodyMode.Static);
            PhysicsServer3D.BodySetCollisionLayer(tile.FarBody, RoomBuilder.FarGroundLayer);
            PhysicsServer3D.BodySetCollisionMask(tile.FarBody, 0);
            PhysicsServer3D.BodyAddShape(tile.FarBody, tile.FarShape, Transform3D.Identity);
            PhysicsServer3D.BodyAttachObjectInstanceId(tile.FarBody, tile.Owner.GetInstanceId());
            view.FarTriangles += tile.FarTriangles;
            coarse[i] = null!;
        }
        return view;
    }

    /// <summary>The far ground's triangles in all (every drawn tile's coarse level), and how many tiles show it now (their ground not built).</summary>
    public long FarTriangles { get; private set; }
    public int FarTilesShown { get; private set; }

    /// <summary>Whether a physics body is one of the far ground's (a body must never rest on one).</summary>
    public bool IsFarGround(Rid body) => body.IsValid && _tiles.Any(t => t.FarBody == body);

    /// <summary>The far ground taken out of the world and freed (measurements: what it holds).</summary>
    internal void FreeFarGround()
    {
        foreach (var tile in _tiles)
        {
            if (tile.FarBody.IsValid) PhysicsServer3D.FreeRid(tile.FarBody);
            if (tile.FarShape.IsValid) PhysicsServer3D.FreeRid(tile.FarShape);
            tile.FarBody = tile.FarShape = default;
        }
        FarTilesShown = 0;
        FarTriangles = 0;
    }

    /// <summary>A tile's far ground is in the world exactly while its walkable ground is not, so a ray meets the fine one where it is built.</summary>
    private void PlaceFar(Tile tile)
    {
        if (!tile.FarBody.IsValid) return;
        var show = !tile.Built && IsInsideTree();
        var shown = PhysicsServer3D.BodyGetSpace(tile.FarBody).IsValid;
        if (show == shown) return;
        if (show)
        {
            PhysicsServer3D.BodySetState(tile.FarBody, PhysicsServer3D.BodyState.Transform, tile.Owner.IsInsideTree() ? tile.Owner.GlobalTransform : Transform3D.Identity);
            PhysicsServer3D.BodySetSpace(tile.FarBody, GetWorld3D().Space);
            FarTilesShown++;
        }
        else
        {
            PhysicsServer3D.BodySetSpace(tile.FarBody, default);
            FarTilesShown--;
        }
    }

    public override void _EnterTree()
    {
        lock (Live) Live.Add(new WeakReference<TerrainCollision>(this));
        var space = GetWorld3D().Space;
        for (var i = 0; i < _tiles.Length; i++)
        {
            var tile = _tiles[i];
            if (tile.Built)
            {
                PhysicsServer3D.BodySetState(tile.Body, PhysicsServer3D.BodyState.Transform, tile.Owner.GlobalTransform);
                PhysicsServer3D.BodySetSpace(tile.Body, space);
            }
            else if (tile.Shape.IsValid) Build(tile);
            PlaceFar(tile);
        }
    }

    public override void _ExitTree()
    {
        lock (Live) Live.RemoveAll(w => !w.TryGetTarget(out var view) || view == this);
        foreach (var tile in _tiles)
        {
            if (tile.Built) PhysicsServer3D.BodySetSpace(tile.Body, default);
            if (tile.FarBody.IsValid && PhysicsServer3D.BodyGetSpace(tile.FarBody).IsValid)
            {
                PhysicsServer3D.BodySetSpace(tile.FarBody, default);
                FarTilesShown--;
            }
        }
    }

    public override void _Notification(int what)
    {
        if ((long)what != NotificationPredelete) return;
        foreach (var tile in _tiles)
        {
            Release(tile, counted: false);
            if (tile.FarBody.IsValid) PhysicsServer3D.FreeRid(tile.FarBody);
            if (tile.FarShape.IsValid) PhysicsServer3D.FreeRid(tile.FarShape);
            tile.FarBody = tile.FarShape = default;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        _timeS += delta;
        Gather(delta);
        if (_bodies.Count == 0) return;
        _bodiesForS += delta;
        var started = Stopwatch.GetTimestamp();
        _queue.Clear();
        foreach (var tile in _tiles)
        {
            tile.Distance = float.PositiveInfinity;
            var sync = false;
            var wanted = tile.HeldUntilS >= _timeS;
            foreach (var (from, to, next) in _bodies)
            {
                var near = Distance(tile.Square, from);
                tile.Distance = Math.Min(tile.Distance, near);
                sync |= near <= SyncM || Distance(tile.Square, next) <= SyncM;
                wanted |= near <= NearM || SegmentDistance(tile.Square, from, to) <= LeadRadiusM;
            }
            foreach (var (at, radius) in _pins) wanted |= Distance(tile.Square, at) <= radius;
            wanted |= sync;
            tile.Wanted = wanted;
            if (sync && !tile.Built)
            {
                var clock = Stopwatch.GetTimestamp();
                Build(tile);
                SyncBuilds++;
                LongestSyncMs = Math.Max(LongestSyncMs, Stopwatch.GetElapsedTime(clock).TotalMilliseconds);
            }
            else if (wanted && !tile.Built) _queue.Add(tile);
            if (wanted) tile.UnneededS = 0;
            else if (tile.Built)
            {
                tile.UnneededS += delta;
                if (_bodiesForS >= BootHoldS && tile.UnneededS >= ReleaseAfterS && tile.Distance > NearM + ReleaseSlackM) Release(tile, counted: true);
            }
        }
        if (_queue.Count == 0) return;
        _queue.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var built = 0;
        foreach (var tile in _queue)
        {
            if (built > 0 && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= BuildBudgetMs) break;
            Build(tile);
            built++;
        }
    }

    /// <summary>
    /// Build the tiles within radiusM of a point now and keep them for holdS (a tool or a test about to ask the ground somewhere no
    /// body stands, or a system that will put something there). Returns the tiles built by this call.
    /// </summary>
    public static int EnsureAround(Node inWorld, Vector3 point, float radiusM = PinM, double holdS = 10.0)
    {
        var built = 0;
        foreach (var view in In(inWorld))
            foreach (var tile in view._tiles)
            {
                if (Distance(tile.Square, point) > radiusM) continue;
                tile.HeldUntilS = Math.Max(tile.HeldUntilS, view._timeS + holdS);
                tile.UnneededS = 0;
                if (tile.Built) continue;
                view.Build(tile);
                built++;
            }
        return built;
    }

    /// <summary>Build every tile now and keep them all for holdS (a tool that asks the whole land, such as the look suite deriving its cameras).</summary>
    public static int EnsureAll(Node inWorld, double holdS = 10.0)
    {
        var built = 0;
        foreach (var view in In(inWorld))
            foreach (var tile in view._tiles)
            {
                tile.HeldUntilS = Math.Max(tile.HeldUntilS, view._timeS + holdS);
                tile.UnneededS = 0;
                if (tile.Built) continue;
                view.Build(tile);
                built++;
            }
        return built;
    }

    /// <summary>Whether the land's tile holding a point (of any colliding tiled part there) collides now; true where there is no tiled land.</summary>
    public static bool CollidesAt(Node inWorld, Vector3 point)
    {
        foreach (var view in In(inWorld))
            foreach (var tile in view._tiles)
                if (tile.Square.HasPoint(new Vector2(point.X, point.Z)) && !tile.Built) return false;
        return true;
    }

    /// <summary>The TerrainCollision views in the scene that share a node's world.</summary>
    public static List<TerrainCollision> In(Node inWorld)
    {
        var found = new List<TerrainCollision>();
        var space = inWorld is Node3D spatial && spatial.IsInsideTree() ? spatial.GetWorld3D().Space : default;
        lock (Live)
            foreach (var weak in Live)
                if (weak.TryGetTarget(out var view) && GodotObject.IsInstanceValid(view) && view.IsInsideTree() && (!space.IsValid || view.GetWorld3D().Space == space))
                    found.Add(view);
        return found;
    }

    /// <summary>The bodies and the places this tick must keep built: where each body is, where it is heading, where it will be in two ticks.</summary>
    private void Gather(double delta)
    {
        _bodies.Clear();
        Bodies(this, _bodyNodes);
        foreach (var body in _bodyNodes)
        {
            var at = body.GlobalPosition;
            var velocity = body switch
            {
                CharacterBody3D character => character.Velocity,
                RigidBody3D rigid => rigid.LinearVelocity,
                _ => Vector3.Zero,
            };
            if (!at.IsFinite()) continue;
            if (!velocity.IsFinite()) velocity = Vector3.Zero;
            var lead = velocity * LeadS;
            if (lead.Length() > MaxLeadM) lead = lead.Normalized() * MaxLeadM;
            _bodies.Add((at, at + lead, at + velocity * (float)(2 * delta)));
        }
        // The places a body may be put without walking there: the room's own, a fire (looked for once a second: it starts and goes
        // out), and each body's last safe footing (where it recovers to).
        if (_fixedPins == null)
        {
            _fixedPins = new List<(Vector3, float)>();
            foreach (var spawn in _room.Spawns) _fixedPins.Add((spawn.PositionM, PinM));
            if (_room.Sea is { } sea)
            {
                if (sea.JettyData is { } jetty) _fixedPins.Add((jetty.RootM, PinM));
                foreach (var beach in sea.Beaches) _fixedPins.Add((beach.WashAshoreM, PinM));
            }
        }
        _pins.Clear();
        _pins.AddRange(_fixedPins);
        if (_timeS - _hostCheckedS >= 1.0)
        {
            _hostCheckedS = _timeS;
            _fire = Fire();
        }
        if (_fire is { } fire) _pins.Add(fire);
        foreach (var body in _bodyNodes)
        {
            if (body is not SmallPlayerController avatar) continue;
            if (avatar.LastSafePosition.IsFinite()) _pins.Add((avatar.LastSafePosition, PinM));
            // The shore a swimmer went in from stays solid however far it swims (an empty bar fades it back there).
            if (avatar.ShoreEntry is { } entry && entry.IsFinite()) _pins.Add((entry, PinM));
        }
    }

    private (Vector3, float)? _fire;
    private List<(Vector3, float)>? _fixedPins;
    private readonly List<Node3D> _bodyNodes = new();
    /// <summary>A fire burning on the land (the kernel's nature): its disk, so it never runs out of ground while nobody watches.</summary>
    private (Vector3, float)? Fire()
    {
        if (_host == null || !IsInstanceValid(_host))
            _host = GetParent()?.GetParent()?.GetChildren().FirstOrDefault(c => c is Kernel.CommandHost);
        return _host is Kernel.CommandHost { Nature.Fire: { } fire } ? (fire.Center, fire.RadiusM + PinM) : null;
    }

    /// <summary>
    /// The moving bodies sharing a node's world: every CharacterBody3D and RigidBody3D in the scene, and the nodes in AnchorGroup.
    /// The scene is scanned once; the tree's added and removed nodes keep the list after that.
    /// </summary>
    public static List<Node3D> Bodies(Node inWorld) => Bodies(inWorld, new List<Node3D>());

    /// <summary>Bodies(inWorld) into a list the caller keeps (cleared first), so a caller every frame allocates nothing.</summary>
    public static List<Node3D> Bodies(Node inWorld, List<Node3D> result)
    {
        result.Clear();
        if (!inWorld.IsInsideTree()) return result;
        var tree = inWorld.GetTree();
        var tracker = Trackers.GetValue(tree, t => new BodyTracker(t));
        var space = inWorld is Node3D spatial ? spatial.GetWorld3D().Space : default;
        foreach (var body in tracker.Bodies)
            if (GodotObject.IsInstanceValid(body) && body.IsInsideTree() && (!space.IsValid || body.GetWorld3D().Space == space)) result.Add(body);
        if (!tree.HasGroup(AnchorGroup)) return result;
        foreach (var node in tree.GetNodesInGroup(AnchorGroup))
            if (node is Node3D anchor && anchor.IsInsideTree() && !result.Contains(anchor) && (!space.IsValid || anchor.GetWorld3D().Space == space)) result.Add(anchor);
        return result;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SceneTree, BodyTracker> Trackers = new();

    private sealed class BodyTracker
    {
        public readonly List<Node3D> Bodies = new();

        public BodyTracker(SceneTree tree)
        {
            foreach (var node in tree.Root.FindChildren("*", "PhysicsBody3D", true, false))
                if (Moves(node)) Bodies.Add((Node3D)node);
            tree.NodeAdded += node => { if (Moves(node)) Bodies.Add((Node3D)node); };
            tree.NodeRemoved += node => { if (Moves(node)) Bodies.Remove((Node3D)node); };
        }

        private static bool Moves(Node node) => node is CharacterBody3D or RigidBody3D;
    }

    /// <summary>A tile's one-sided ground shape on the physics server, from its faces (read from the room's data when not given).</summary>
    private void Shape(Tile tile, Vector3[]? faces = null)
    {
        if (tile.Shape.IsValid) return;
        faces ??= tile.Part.GroundFaces(tile.Index);
        tile.Shape = PhysicsServer3D.ConcavePolygonShapeCreate();
        PhysicsServer3D.ShapeSetData(tile.Shape, new Godot.Collections.Dictionary { ["faces"] = faces, ["backface_collision"] = false });
        tile.Triangles = faces.Length / 3;
    }

    private void Build(Tile tile)
    {
        if (tile.Built) return;
        Shape(tile);
        tile.Body = PhysicsServer3D.BodyCreate();
        PhysicsServer3D.BodySetMode(tile.Body, PhysicsServer3D.BodyMode.Static);
        PhysicsServer3D.BodySetCollisionLayer(tile.Body, tile.Layer);
        PhysicsServer3D.BodySetCollisionMask(tile.Body, 0);
        PhysicsServer3D.BodyAddShape(tile.Body, tile.Shape, Transform3D.Identity);
        PhysicsServer3D.BodyAttachObjectInstanceId(tile.Body, tile.Owner.GetInstanceId());
        if (IsInsideTree())
        {
            PhysicsServer3D.BodySetState(tile.Body, PhysicsServer3D.BodyState.Transform, tile.Owner.IsInsideTree() ? tile.Owner.GlobalTransform : Transform3D.Identity);
            PhysicsServer3D.BodySetSpace(tile.Body, GetWorld3D().Space);
        }
        tile.UnneededS = 0;
        TilesBuilt++;
        MostTilesBuilt = Math.Max(MostTilesBuilt, TilesBuilt);
        Builds++;
        TrianglesBuilt += tile.Triangles;
        PlaceFar(tile);
    }

    private void Release(Tile tile, bool counted)
    {
        if (tile.Built)
        {
            PhysicsServer3D.BodySetSpace(tile.Body, default);
            PhysicsServer3D.FreeRid(tile.Body);
            tile.Body = default;
            TilesBuilt--;
            TrianglesBuilt -= tile.Triangles;
            if (counted) Releases++;
        }
        if (tile.Shape.IsValid) PhysicsServer3D.FreeRid(tile.Shape);
        tile.Shape = default;
        PlaceFar(tile);
    }

    /// <summary>How far a point is from a tile's square, seen from above (0 inside it).</summary>
    public static float Distance(Rect2 square, Vector3 point)
    {
        var dx = Math.Max(Math.Max(square.Position.X - point.X, 0f), point.X - square.End.X);
        var dz = Math.Max(Math.Max(square.Position.Y - point.Z, 0f), point.Z - square.End.Y);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>How far a segment comes to a tile's square, seen from above (sampled every half lead radius, which is close enough).</summary>
    private static float SegmentDistance(Rect2 square, Vector3 from, Vector3 to)
    {
        var length = new Vector2(to.X - from.X, to.Z - from.Z).Length();
        var steps = Math.Max(1, (int)MathF.Ceiling(length / (LeadRadiusM * 0.5f)));
        var least = float.PositiveInfinity;
        for (var s = 0; s <= steps; s++) least = Math.Min(least, Distance(square, from.Lerp(to, s / (float)steps)));
        return least;
    }
}
