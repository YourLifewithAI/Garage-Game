using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GarageGame.Native.Look;

namespace GarageGame.Native.Room;

/// <summary>
/// The room's plant copies in the scene (the 10x world, packets S6 and S12b): per set and 8 m tile, one MultiMesh drawing the set's
/// mesh for the tile's distance band (the set's meshes by detail, DrawDistances), filled only while the tile is within the set's
/// range of a focus (the camera and the avatars), and the copies' collision built from their rows (PlantCollision) as static
/// physics bodies on the server, one per tile and layer, with no node per copy. A tile whose band changes is filled again with
/// the other mesh; one out of range is emptied, so a 350,000-copy island holds only the copies near enough to be seen (S12b: every
/// instance costs its 64 bytes on the GPU and about three times that in the renderer's CPU copies). Tiles are built nearest a
/// focus first, a few each frame within DrawDistances.BuildBudgetMs, so the island arrives without a hitch; the collision within
/// DrawDistances.NearCollisionM of the spawns is built when the view enters the scene, before the bodies are placed.
///
/// Nothing here is authoritative: the copies are the manifest's, and a copy is the entity plant:&lt;set&gt;-&lt;row&gt;, never a
/// node. What Grow and fire did to a copy is the island's nature (the command host's): SetPlantFactors hands the view each changed
/// copy's factor of its natural size (0 for one burnt down), and the tiles holding them are filled and collided again. A hit on a copy's collision names its copy through EntityAt (the holder node the body reports and the shape's index).
/// Layers: a tree's trunk and a solid stone are on the world layer (they block sight, reach and the camera, and navigation
/// avoids them); a tree's pole and caps are on the hidden layer, which bodies climb and stand on and nothing else sees.
/// </summary>
public partial class PlantCopiesView : Node3D
{
    public const string ViewName = "Copies";
    /// <summary>Meta on a collision holder: the set's name, and per shape index the row it belongs to.</summary>
    public const string PlantSetMeta = "plant_set", PlantRowsMeta = "plant_rows";

    private RoomData _room = null!;
    private SetView[] _sets = Array.Empty<SetView>();
    private TileDraw[] _draws = Array.Empty<TileDraw>();
    private readonly List<Work> _pending = new();
    private readonly List<Rid> _bodies = new();
    private readonly List<Rid> _shapes = new();
    private readonly Dictionary<(int Set, int Tile), Node3D> _tiles = new();
    private readonly List<Vector3> _foci = new();
    private readonly List<Vector3> _fociChosen = new();
    private readonly List<TileDraw> _changes = new();
    private readonly List<Node3D> _bodies3D = new();
    private Vector3 _sortedAround = new(float.NaN, 0, float.NaN);
    private int _framesSinceSort, _framesSinceChoice = int.MaxValue / 2;
    private bool _reported;
    /// <summary>One buffer for every tile's instances, grown to the largest tile: a tile filled again allocates nothing.</summary>
    private float[] _scratch = Array.Empty<float>();
    /// <summary>Per set (by index), the rows not at their natural size: grown (above 1) or burnt down (0). Absent: natural.</summary>
    private Dictionary<int, float>[] _factors = Array.Empty<Dictionary<int, float>>();
    private readonly Dictionary<PlantSet, int> _setIndex = new();
    private readonly Dictionary<(int Set, int Tile), TileDraw> _drawAt = new();
    /// <summary>Each tile's collision as built: its bodies and their holder nodes, so a tile whose copies changed is built again alone.</summary>
    private readonly Dictionary<(int Set, int Tile), (List<Rid> Bodies, List<Node3D> Holders)> _collided = new();
    /// <summary>The tile whose collision is being built now (BuildCollision): its holders and bodies are recorded as they are made.</summary>
    private (List<Rid> Bodies, List<Node3D> Holders)? _building;

    /// <summary>Tiles drawn now (their MultiMesh filled), of VisualTiles in all; tiles whose collision is built so far, of CollisionTiles.</summary>
    public int VisualTilesBuilt { get; private set; }
    public int CollisionTilesBuilt { get; private set; }
    public int VisualTiles { get; private set; }
    public int CollisionTiles { get; private set; }
    /// <summary>Times a tile was filled (entering reach or changing band) and emptied (leaving reach), since the view was made.</summary>
    public int VisualFills { get; private set; }
    public int VisualEmpties { get; private set; }
    /// <summary>Every tile built so far, drawn or collided, counting refills: a measurement's "tiles built in this frame".</summary>
    public int TilesBuiltTotal => VisualFills + CollisionTilesBuilt;
    /// <summary>Copies held in MultiMeshes now: each one a 64-byte instance on the GPU and in the renderer's CPU copies.</summary>
    public long InstancesHeld { get; private set; }
    /// <summary>Tiles whose collision was built on entering the scene, near the spawns.</summary>
    public int NearCollisionTiles { get; private set; }
    /// <summary>Every tile's collision is built and every tile in reach is drawn in its band (false again while the foci move on).</summary>
    public bool Complete => IsInsideTree() && _pending.Count == 0 && _changes.Count == 0 && _framesSinceChoice < int.MaxValue / 2;
    /// <summary>Frames that built something, the main thread's time they spent, the longest, and the most tiles one frame built.</summary>
    public int BuildFrames { get; private set; }
    public double BuildMs { get; private set; }
    public double LongestFrameMs { get; private set; }
    public int MostTilesInAFrame { get; private set; }
    public double EnterMs { get; private set; }
    /// <summary>The slowest single tile and what it was (set, tile, drawn or collided), for measurements.</summary>
    public double LongestTileMs { get; private set; }
    public string LongestTile { get; private set; } = "";
    /// <summary>
    /// Where the copies are drawn round besides the current camera, for tests and tools: null takes the avatars (every
    /// CharacterBody3D in the world), so the Gubble's eyes see the copies round it wherever it walks.
    /// </summary>
    public Func<IEnumerable<Vector3>>? ExtraFoci { get; set; }

    private sealed class SetView
    {
        public required PlantSet Set;
        public required Node3D Node;
        public required Mesh[] Meshes;
        public required CopyBand[] Bands;
        /// <summary>Prototype (run2/look-grass-proto): the grass's parts tufts by band (GrassLook.PartsMeshes), drawn for one row in GrassLook.Tuning.PartsEvery; null for every other set.</summary>
        public Mesh[]? PartsMeshes;
    }

    /// <summary>One set's tile as drawn: the middle of its copies' bounds (what its distance is measured to), its band now (-1: empty) and the one it should have.</summary>
    private sealed class TileDraw
    {
        public required int Set;
        public required int Tile;
        public required Vector3 Centre;
        public int Band = -1;
        public int Wanted = -1;
        public float Distance = float.PositiveInfinity;
        public MultiMeshInstance3D? Instance;
        public MultiMesh? Multimesh;
        /// <summary>Prototype (run2/look-grass-proto): the grass tile's parts tufts, a child of Instance (shown and hidden with it).</summary>
        public MultiMeshInstance3D? Parts;
        public MultiMesh? PartsMultimesh;
    }

    private readonly record struct Work(int Set, int Tile, bool Collision, Vector2 Centre);

    /// <summary>The view of a room's copies, its meshes loaded and painted; it builds once it is in the scene.</summary>
    public static PlantCopiesView Create(RoomData room)
    {
        var copies = room.Copies ?? throw new ArgumentException("the room has no plant copies", nameof(room));
        var view = new PlantCopiesView { Name = ViewName, _room = room };
        view.SetMeta("plant_copies", copies.TotalCount);
        view._sets = copies.Sets.Select(set =>
        {
            var node = new Node3D { Name = set.Name };
            node.SetMeta(PlantSetMeta, set.Name);
            node.SetMeta("species", set.Species);
            node.SetMeta("plant_kind", set.Kind.ToString().ToLowerInvariant());
            view.AddChild(node);
            // Prototype (run2/look-grass-proto): the grass's tufts built in code (GrassLook), the kit's back with --look-grass=kit.
            if (GrassLook.Draws(set)) GrassLook.BuildCanopy(room);
            var meshes = GrassLook.Draws(set) ? GrassLook.TuftMeshes(set.MeshPaths.Count) : set.MeshPaths.Select(path => CopyMesh(room, path, set.Name)).ToArray();
            return new SetView
            {
                Set = set, Node = node, Meshes = meshes, Bands = DrawDistances.CopyBands(set.Kind, TypicalHeightM(set), meshes.Length),
                PartsMeshes = GrassLook.Draws(set) ? GrassLook.PartsMeshes(set.MeshPaths.Count) : null,
            };
        }).ToArray();
        var draws = new List<TileDraw>();
        for (var s = 0; s < view._sets.Length; s++)
        {
            var set = view._sets[s].Set;
            for (var t = 0; t < set.TileCount; t++)
            {
                var (tx, tz, _, _) = set.Tile(t);
                var centre = new Vector2((tx + 0.5f) * PlantCopies.TileM, (tz + 0.5f) * PlantCopies.TileM);
                draws.Add(new TileDraw { Set = s, Tile = t, Centre = new Vector3(centre.X, 0, centre.Y) });
                view.VisualTiles++;
                if (set.Collides == "none") continue;
                view._pending.Add(new Work(s, t, true, centre));
                view.CollisionTiles++;
            }
        }
        // The middle of each tile's drawn bounds, as the renderer measures a visibility range to it (rows read on worker threads).
        var bounds = view._sets.Select(v => v.Set.MeshBounds).ToArray();
        System.Threading.Tasks.Parallel.ForEach(draws, draw =>
        {
            var set = view._sets[draw.Set].Set;
            var (_, _, first, count) = set.Tile(draw.Tile);
            draw.Centre = TileBounds(set, first, count, bounds[draw.Set]).GetCenter();
        });
        view._draws = draws.ToArray();
        foreach (var draw in view._draws) view._drawAt[(draw.Set, draw.Tile)] = draw;
        view._factors = view._sets.Select(_ => new Dictionary<int, float>()).ToArray();
        for (var s = 0; s < view._sets.Length; s++) view._setIndex[view._sets[s].Set] = s;
        return view;
    }

    public override void _Ready()
    {
        var clock = Stopwatch.StartNew();
        // The bodies are placed at their spawns on the next physics frame (RoomWorld): what they could touch there exists first.
        var spawns = _room.Spawns.Select(s => new Vector2(s.PositionM.X, s.PositionM.Z)).ToArray();
        var near = _pending.Where(w => w.Collision && spawns.Any(s => TileDistance(w, s) <= DrawDistances.NearCollisionM)).ToHashSet();
        foreach (var work in near) Build(work);
        _pending.RemoveAll(near.Contains);
        NearCollisionTiles = near.Count;
        EnterMs = clock.Elapsed.TotalMilliseconds;
    }

    public override void _Process(double delta)
    {
        Foci(_foci);
        if (_foci.Count == 0) return;
        // The tiles' bands are chosen again when a focus has moved half a metre, and twice a second whatever happens.
        if (++_framesSinceChoice >= 30 || Moved(_foci, _fociChosen, 0.5f)) Choose();
        if (_pending.Count == 0 && _changes.Count == 0) { Report(); return; }
        var focus = _foci[0];
        if (_pending.Count > 0 && (++_framesSinceSort >= 30 || !(focus.DistanceSquaredTo(_sortedAround) < 4f)))
        {
            // Farthest first in the list, so the nearest is taken from its end.
            var at = new Vector2(focus.X, focus.Z);
            _pending.Sort((a, b) => TileDistance(b, at).CompareTo(TileDistance(a, at)));
            _sortedAround = focus;
            _framesSinceSort = 0;
        }
        var clock = Stopwatch.StartNew();
        var built = 0;
        // Drawing first, nearest first (the list is nearest last); then collision, nearest the camera first.
        while (_changes.Count > 0 && (built == 0 || clock.Elapsed.TotalMilliseconds < DrawDistances.BuildBudgetMs))
        {
            var draw = _changes[^1];
            _changes.RemoveAt(_changes.Count - 1);
            if (draw.Wanted == draw.Band) continue;
            Timed(draw.Set, draw.Tile, false, () => Fill(draw, draw.Wanted));
            built++;
        }
        while (_pending.Count > 0 && (built == 0 || clock.Elapsed.TotalMilliseconds < DrawDistances.BuildBudgetMs))
        {
            var work = _pending[^1];
            _pending.RemoveAt(_pending.Count - 1);
            Build(work);
            built++;
        }
        var spent = clock.Elapsed.TotalMilliseconds;
        BuildFrames++;
        BuildMs += spent;
        LongestFrameMs = Math.Max(LongestFrameMs, spent);
        MostTilesInAFrame = Math.Max(MostTilesInAFrame, built);
        Report();
    }

    private void Report()
    {
        if (_reported || !Complete) return;
        _reported = true;
        GD.Print(System.FormattableString.Invariant(
            $"ROOM_COPIES {_room.Copies!.TotalCount} copies of {_sets.Length} sets: {VisualTilesBuilt} of {VisualTiles} tiles drawn (in reach of the camera and the avatars, {InstancesHeld} copies held), {CollisionTiles} collided ({NearCollisionTiles} near the spawns on entering, {EnterMs:0} ms), over {BuildFrames} frames, {BuildMs:0} ms in all, longest frame {LongestFrameMs:0.0} ms; slowest tile: {LongestTile}"));
    }

    /// <summary>
    /// Every tile's band from its distance to the nearest focus: within its set's range (and DrawDistances.CopyBuildLeadM beyond,
    /// so it is filled before it shows) the band holding that distance, kept across a band's edge by DrawDistances.CopyMarginM so
    /// a focus standing on an edge never refills it every frame; out of reach (CopyReleaseM past the range) empty. Emptying is
    /// cheap and done at once; filling waits its turn, nearest first.
    /// </summary>
    private void Choose()
    {
        _framesSinceChoice = 0;
        _fociChosen.Clear();
        _fociChosen.AddRange(_foci);
        _changes.Clear();
        foreach (var draw in _draws)
        {
            var distance = float.PositiveInfinity;
            foreach (var focus in _foci) distance = Math.Min(distance, focus.DistanceTo(draw.Centre));
            draw.Distance = distance;
            draw.Wanted = WantedBand(_sets[draw.Set].Bands, draw.Band, distance);
            if (draw.Wanted == draw.Band) continue;
            if (draw.Wanted < 0) Fill(draw, -1);
            else _changes.Add(draw);
        }
        _changes.Sort((a, b) => b.Distance.CompareTo(a.Distance));
    }

    /// <summary>The band a tile should be drawn in at a distance, given the band it has now (-1: none): see Choose.</summary>
    public static int WantedBand(CopyBand[] bands, int current, float distance)
    {
        var last = bands.Length - 1;
        var reach = bands[last].EndM + (current < 0 ? DrawDistances.CopyBuildLeadM : DrawDistances.CopyReleaseM);
        if (!(distance <= reach)) return -1;
        if (current >= 0 && current <= last)
        {
            var low = current == 0 ? float.NegativeInfinity : bands[current].BeginM - DrawDistances.CopyMarginM;
            var high = current == last ? float.PositiveInfinity : bands[current].EndM + DrawDistances.CopyMarginM;
            if (distance >= low && distance < high) return current;
        }
        for (var band = 0; band < last; band++)
            if (distance < bands[band].EndM) return band;
        return last;
    }

    /// <summary>The current camera, then the avatars (or ExtraFoci): every place the copies must be drawn round.</summary>
    private void Foci(List<Vector3> foci)
    {
        foci.Clear();
        if (GetViewport()?.GetCamera3D() is { } camera && camera.IsInsideTree()) foci.Add(camera.GlobalPosition);
        if (ExtraFoci != null) foci.AddRange(ExtraFoci());
        else foreach (var body in TerrainCollision.Bodies(this, _bodies3D)) foci.Add(body.GlobalPosition);
        if (foci.Count == 0) foci.Add(_room.SpawnFor("player").PositionM);
    }

    private static bool Moved(List<Vector3> now, List<Vector3> then, float metres)
    {
        if (now.Count != then.Count) return true;
        for (var i = 0; i < now.Count; i++)
            if (!(now[i].DistanceSquaredTo(then[i]) < metres * metres)) return true;
        return false;
    }

    /// <summary>The copies' bodies leave the physics space with the view and come back with it; they and their shapes are freed with the view.</summary>
    public override void _Notification(int what)
    {
        switch ((long)what)
        {
            case NotificationEnterTree:
                foreach (var body in _bodies)
                {
                    PhysicsServer3D.BodySetState(body, PhysicsServer3D.BodyState.Transform, GlobalTransform);
                    PhysicsServer3D.BodySetSpace(body, GetWorld3D().Space);
                }
                break;
            case NotificationExitTree:
                foreach (var body in _bodies) PhysicsServer3D.BodySetSpace(body, default);
                break;
            case NotificationPredelete:
                foreach (var body in _bodies) PhysicsServer3D.FreeRid(body);
                foreach (var shape in _shapes) PhysicsServer3D.FreeRid(shape);
                _bodies.Clear();
                _shapes.Clear();
                // The MultiMeshes go with their nodes; their wrappers are let go now, not when the collector gets to them.
                foreach (var draw in _draws)
                {
                    draw.Multimesh?.Dispose();
                    draw.PartsMultimesh?.Dispose();
                }
                break;
        }
    }

    /// <summary>Every copy's collision taken out of the world and freed (measurements: what it holds).</summary>
    internal void FreeCollision()
    {
        foreach (var body in _bodies) PhysicsServer3D.FreeRid(body);
        foreach (var shape in _shapes) PhysicsServer3D.FreeRid(shape);
        _bodies.Clear();
        _shapes.Clear();
        _shared.Clear();
    }

    /// <summary>
    /// The copy a collision hit belongs to, as its entity id (plant:&lt;set&gt;-&lt;row&gt;), or "" when the collider is not a copy's.
    /// Pass a ray or shape query's "collider" and "shape" (or a KinematicCollision3D's collider and shape index).
    /// </summary>
    public static string EntityAt(GodotObject? collider, int shape)
    {
        if (collider is not Node node || !node.HasMeta(PlantRowsMeta) || !node.HasMeta(PlantSetMeta)) return "";
        var rows = node.GetMeta(PlantRowsMeta).AsInt32Array();
        return shape >= 0 && shape < rows.Length ? PlantCopies.IdOf(node.GetMeta(PlantSetMeta).AsString(), rows[shape]) : "";
    }

    /// <summary>The tile node holding a set's tile (its bands and collision holders), once built.</summary>
    public Node3D? TileNode(string set, int tx, int tz)
    {
        var index = Array.FindIndex(_sets, s => s.Set.Name == set);
        if (index < 0) return null;
        var tile = _sets[index].Set.FindTile(tx, tz);
        return tile >= 0 && _tiles.TryGetValue((index, tile), out var node) ? node : null;
    }

    private static float TileDistance(Work work, Vector2 at) => work.Centre.DistanceTo(at);

    private void Build(Work work) => Timed(work.Set, work.Tile, work.Collision, () => BuildCollision(work.Set, work.Tile));

    private void Timed(int set, int tile, bool collision, Action build)
    {
        var clock = Stopwatch.StartNew();
        build();
        var spent = clock.Elapsed.TotalMilliseconds;
        if (_slowest.Count == 6 && spent <= _slowest[^1].Ms) return;
        var (tx, tz, _, count) = _sets[set].Set.Tile(tile);
        _slowest.Add((spent, $"{_sets[set].Set.Name} ({tx}, {tz}) {count} {(collision ? "collided" : "drawn")} {spent:0.0} ms"));
        _slowest.Sort((a, b) => b.Ms.CompareTo(a.Ms));
        if (_slowest.Count > 6) _slowest.RemoveAt(_slowest.Count - 1);
        LongestTileMs = _slowest[0].Ms;
        LongestTile = string.Join("; ", _slowest.Select(t => t.What));
    }

    private readonly List<(double Ms, string What)> _slowest = new();

    private Node3D TileNodeFor(int set, int tile)
    {
        if (_tiles.TryGetValue((set, tile), out var node)) return node;
        var (tx, tz, _, _) = _sets[set].Set.Tile(tile);
        node = new Node3D { Name = $"Tile_{tx}_{tz}" };
        node.SetMeta("tile", new Vector2I(tx, tz));
        _sets[set].Node.AddChild(node);
        _tiles[(set, tile)] = node;
        return node;
    }

    /// <summary>The name of a tile's MultiMeshInstance3D under its tile node.</summary>
    public const string DrawnName = "Drawn";

    /// <summary>
    /// One tile of one set drawn in a band, or emptied (band -1): its rows' transforms and tints in its one MultiMesh (12 floats of
    /// transform, basis rows with the origin, then 4 of colour), drawing the band's mesh with the band's shadow, to the set's range.
    /// The instance colour is the row's tint: Godot multiplies it into the mesh's vertex colour, as the exporter's merged meshes
    /// baked it. A band change empties the MultiMesh before its mesh changes (with instances, setting the mesh reads the GPU's
    /// buffer back to measure it), then fills it again; an empty one holds no buffer anywhere.
    /// </summary>
    private void Fill(TileDraw draw, int band)
    {
        if (band < 0)
        {
            if (draw.Band < 0) return;
            draw.Multimesh!.InstanceCount = 0;
            if (draw.PartsMultimesh != null) draw.PartsMultimesh.InstanceCount = 0;
            draw.Instance!.Visible = false;
            InstancesHeld -= _sets[draw.Set].Set.Tile(draw.Tile).Count;
            draw.Band = -1;
            VisualTilesBuilt--;
            VisualEmpties++;
            return;
        }
        var view = _sets[draw.Set];
        var set = view.Set;
        var (_, _, first, count) = set.Tile(draw.Tile);
        var drawn = view.Bands[band];
        if (draw.Instance == null)
        {
            draw.Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true };
            draw.Instance = new MultiMeshInstance3D { Name = DrawnName, Multimesh = draw.Multimesh, GIMode = GeometryInstance3D.GIModeEnum.Disabled };
            // Which way the set's leaves go through the year (the land's seasons, LandSeason): a conifer's stay green, grass goes to straw.
            draw.Instance.SetInstanceShaderParameter(LandSeason.HabitParameter, (float)LandSeason.HabitFor(set.FuelClass));
            TileNodeFor(draw.Set, draw.Tile).AddChild(draw.Instance);
        }
        var multimesh = draw.Multimesh!;
        if (draw.Band >= 0)
        {
            InstancesHeld -= count;
            VisualTilesBuilt--;
        }
        multimesh.InstanceCount = 0;
        multimesh.Mesh = view.Meshes[drawn.Mesh];
        multimesh.InstanceCount = count;
        if (_scratch.Length < count * 16) _scratch = new float[Math.Max(count * 16, _scratch.Length * 3 / 2)];
        FillInstances(set, first, count, _scratch, factors: _factors[draw.Set]);
        RenderingServer.MultimeshSetBuffer(multimesh.GetRid(), new ReadOnlySpan<float>(_scratch, 0, count * 16));
        var instance = draw.Instance!;
        // Drawn to the set's range as its last band was, whichever band it holds: the band is this view's choice, so another
        // camera (the Gubble's eyes) sees what is held rather than a hole.
        instance.VisibilityRangeEnd = view.Bands[^1].EndM;
        instance.VisibilityRangeEndMargin = DrawDistances.CopyMarginM;
        // Prototype (run2/look-grass-proto): procedural blades cast no shadow of their own (4 mm blades only alias in the sun's map).
        instance.CastShadow = drawn.CastsShadow && !GrassLook.Draws(set) ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
        instance.SetMeta("band", band);
        if (view.PartsMeshes != null) FillParts(draw, view, drawn.Mesh, first, count);
        instance.Visible = true;
        draw.Band = band;
        InstancesHeld += count;
        VisualTilesBuilt++;
        VisualFills++;
    }

    /// <summary>
    /// Prototype (run2/look-grass-proto): a grass tile's parts tufts (its tall grass, rosettes, flowers and stones), one row in
    /// GrassLook.Tuning.PartsEvery, in their own MultiMesh under the tile's, drawing the band's parts mesh to GrassLook.Tuning.PartsDrawM
    /// (past it every part has shrunk away). Not counted in InstancesHeld: the copies are the rows, these only dress some of them.
    /// </summary>
    private void FillParts(TileDraw draw, SetView view, int mesh, int first, int count)
    {
        var every = GrassLook.Tuning.PartsEvery;
        var parts = (count + every - 1) / every;
        if (draw.Parts == null)
        {
            draw.PartsMultimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true };
            draw.Parts = new MultiMeshInstance3D
            {
                Name = "Parts", Multimesh = draw.PartsMultimesh, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                VisibilityRangeEnd = GrassLook.Tuning.PartsDrawM, VisibilityRangeEndMargin = DrawDistances.CopyMarginM,
            };
            draw.Parts.SetInstanceShaderParameter(LandSeason.HabitParameter, (float)LandSeason.HabitFor(view.Set.FuelClass));
            draw.Instance!.AddChild(draw.Parts);
        }
        var multimesh = draw.PartsMultimesh!;
        multimesh.InstanceCount = 0;
        multimesh.Mesh = view.PartsMeshes![mesh];
        multimesh.InstanceCount = parts;
        FillInstances(view.Set, first, parts, _scratch, every, _factors[draw.Set]);
        RenderingServer.MultimeshSetBuffer(multimesh.GetRid(), new ReadOnlySpan<float>(_scratch, 0, parts * 16));
    }

    /// <summary>The mesh a set's band draws (its meshes by detail, DrawDistances.CopyBands), for tests.</summary>
    public Mesh MeshOfBand(string set, int band)
    {
        var view = _sets.First(s => s.Set.Name == set);
        return view.Meshes[view.Bands[band].Mesh];
    }

    /// <summary>The band a set's tile is drawn in now (-1: empty, out of reach), for tests.</summary>
    public int BandOf(string set, int tx, int tz)
    {
        var index = Array.FindIndex(_sets, s => s.Set.Name == set);
        var tile = index < 0 ? -1 : _sets[index].Set.FindTile(tx, tz);
        return tile < 0 ? -1 : _draws.First(d => d.Set == index && d.Tile == tile).Band;
    }

    /// <summary>
    /// Rows first to first + count as a MultiMesh buffer with colours: per instance 16 floats, the transform's basis rows each
    /// followed by its origin's coordinate (Godot's layout, RenderingServer.MultimeshSetBuffer), then the tint and an alpha of 1.
    /// </summary>
    public static float[] InstanceBuffer(PlantSet set, int first, int count)
    {
        var buffer = new float[count * 16];
        FillInstances(set, first, count, buffer);
        return buffer;
    }

    private static void FillInstances(PlantSet set, int first, int count, Span<float> buffer, int stride = 1, Dictionary<int, float>? factors = null)
    {
        var data = set.Data;
        var sized = factors is { Count: > 0 };
        for (var i = 0; i < count; i++)
        {
            var index = first + i * stride;
            var row = data.AsSpan(set.RowsAt + index * set.RowBytes, PlantSet.RowBytesBase);
            var (sin, cos) = MathF.SinCos(Field(row, 3) * (MathF.PI / 180f));
            // A grown copy is scaled about its pivot (its root, the bottom centre); one burnt down is scaled to nothing, drawing nothing.
            var factor = sized && factors!.TryGetValue(index, out var f) ? f : 1f;
            float sx = Field(row, 4) * factor, sy = Field(row, 5) * factor, sz = Field(row, 6) * factor;
            // The basis is yaw about +Y times the scale: its columns sx (cos, 0, -sin), sy (0, 1, 0) and sz (sin, 0, cos).
            var o = i * 16;
            buffer[o] = sx * cos; buffer[o + 1] = 0f; buffer[o + 2] = sz * sin; buffer[o + 3] = Field(row, 0);
            buffer[o + 4] = 0f; buffer[o + 5] = sy; buffer[o + 6] = 0f; buffer[o + 7] = Field(row, 1);
            buffer[o + 8] = -sx * sin; buffer[o + 9] = 0f; buffer[o + 10] = sz * cos; buffer[o + 11] = Field(row, 2);
            buffer[o + 12] = Field(row, 7); buffer[o + 13] = Field(row, 8); buffer[o + 14] = Field(row, 9); buffer[o + 15] = 1f;
        }
    }

    /// <summary>The bounds of a tile's copies as the renderer gives a MultiMesh's: every row's mesh bounds placed by its transform.</summary>
    private static Aabb TileBounds(PlantSet set, int first, int count, Aabb mesh)
    {
        var low = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var high = -low;
        for (var row = first; row < first + count; row++)
        {
            var placed = set.Transform(row) * mesh;
            low = low.Min(placed.Position);
            high = high.Max(placed.End);
        }
        return count == 0 ? new Aabb() : new Aabb(low, high - low);
    }

    private static float Field(ReadOnlySpan<byte> row, int field) => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(row[(4 * field)..]);

    /// <summary>
    /// One tile's collision: trees' trunks (world) and poles and caps (hidden), or solid stones' hulls (world). Every shape is a
    /// shared unit shape (PlantCollision), placed per row by a scaling transform; each body is the tile's, one per layer.
    /// </summary>
    private void BuildCollision(int setIndex, int tile)
    {
        var set = _sets[setIndex].Set;
        var (_, _, first, count) = set.Tile(tile);
        var node = TileNodeFor(setIndex, tile);
        var factors = _factors[setIndex];
        var built = (new List<Rid>(), new List<Node3D>());
        _collided[(setIndex, tile)] = built;
        _building = built;
        if (set.Kind == PlantKind.Tree)
        {
            var trunks = new List<(Rid, Transform3D, int)>(count);
            var climb = new List<(Rid, Transform3D, int)>(count * 3);
            for (var row = first; row < first + count; row++)
            {
                // A grown tree's trunk, pole and perches grow with it (the same unit shapes, placed grown); one burnt down has none.
                var factor = factors.TryGetValue(row, out var f) ? f : 1f;
                if (factor <= 0) continue;
                var root = set.Position(row);
                var tree = PlantCollision.Grown(set.Tree(row), root.X, root.Z, factor);
                var taper = PlantCollision.TaperSteps(tree);
                trunks.Add((Shared($"trunk {taper}", () => Convex(PlantCollision.UnitTrunk(taper))), PlantCollision.TrunkPlace(tree), row));
                climb.Add((Shared("pole", () => Convex(PlantCollision.UnitPole())), PlantCollision.PolePlace(tree), row));
                foreach (var cap in tree.Caps)
                {
                    var steps = PlantCollision.CapSteps(cap);
                    climb.Add((Shared($"cap {steps.X} {steps.Z}", () => Concave(PlantCollision.UnitCapFaces(steps))), PlantCollision.CapPlace(cap), row));
                }
            }
            Body(Holder(node, "Trunks", set.Name, "bark", true), RoomBuilder.WorldLayer, trunks);
            Body(Holder(node, "Climb", set.Name, "foliage", false), RoomBuilder.HiddenLayer, climb);
        }
        else if (set.Collides == "solid" && set.SolidPoints.Count > 0)
        {
            var hull = Shared("solid " + set.Name, () => Convex(set.SolidPoints.ToArray()));
            var solids = new List<(Rid, Transform3D, int)>(count);
            for (var row = first; row < first + count; row++)
            {
                var factor = factors.TryGetValue(row, out var f) ? f : 1f;
                if (factor <= 0) continue;
                var place = set.Transform(row);
                solids.Add((hull, new Transform3D(place.Basis * Basis.FromScale(Vector3.One * factor), place.Origin), row));
            }
            Body(Holder(node, "Solids", set.Name, set.Kind == PlantKind.Stone ? "rock" : "foliage", true), RoomBuilder.WorldLayer, solids);
        }
        _building = null;
        CollisionTilesBuilt++;
    }

    private readonly Dictionary<string, Rid> _shared = new(StringComparer.Ordinal);

    private Rid Shared(string key, Func<Rid> create)
    {
        if (!_shared.TryGetValue(key, out var shape)) _shared[key] = shape = create();
        return shape;
    }

    /// <summary>The node a body reports as its collider: what it is (the set, a material role for nature's surface rule, drawn or collision only) and its shapes' rows.</summary>
    private Node3D Holder(Node3D tile, string name, string set, string materialRole, bool drawn)
    {
        var holder = new Node3D { Name = name };
        _building?.Holders.Add(holder);
        holder.SetMeta(PlantSetMeta, set);
        holder.SetMeta("material_role", materialRole);
        holder.SetMeta("drawn", drawn);
        tile.AddChild(holder);
        return holder;
    }

    private void Body(Node3D holder, uint layer, List<(Rid Shape, Transform3D Place, int Row)> shapes)
    {
        holder.SetMeta(PlantRowsMeta, shapes.Select(s => s.Row).ToArray());
        var body = PhysicsServer3D.BodyCreate();
        PhysicsServer3D.BodySetMode(body, PhysicsServer3D.BodyMode.Static);
        PhysicsServer3D.BodySetCollisionLayer(body, layer);
        PhysicsServer3D.BodySetCollisionMask(body, 0);
        foreach (var (shape, place, _) in shapes) PhysicsServer3D.BodyAddShape(body, shape, place);
        PhysicsServer3D.BodySetState(body, PhysicsServer3D.BodyState.Transform, GlobalTransform);
        PhysicsServer3D.BodyAttachObjectInstanceId(body, holder.GetInstanceId());
        PhysicsServer3D.BodySetSpace(body, GetWorld3D().Space);
        _bodies.Add(body);
        _building?.Bodies.Add(body);
    }

    /// <summary>
    /// What Grow and fire did to the copies (the island's nature): each changed copy's factor of its natural size, 1 natural, above 1
    /// grown, 0 burnt down (drawn as nothing, with no collision). Only the tiles holding a changed copy are touched: a drawn one is
    /// filled again in its band, a collided one has its bodies built again; a tile not drawn or collided yet takes the factors when
    /// it is. Ids that are not this room's copies are ignored. Returns the tiles touched.
    /// </summary>
    public int SetPlantFactors(IEnumerable<(string Id, float Factor)> changes)
    {
        var copies = _room.Copies!;
        var touched = new HashSet<(int Set, int Tile)>();
        foreach (var (id, factor) in changes)
        {
            if (!copies.TryResolve(id, out var set, out var row) || !_setIndex.TryGetValue(set, out var index)) continue;
            if (factor == 1) _factors[index].Remove(row);
            else _factors[index][row] = Math.Max(0, factor);
            touched.Add((index, set.TileOfRow(row)));
        }
        foreach (var key in touched)
        {
            if (_drawAt.TryGetValue(key, out var draw) && draw.Band >= 0) Fill(draw, draw.Band);
            if (_collided.Remove(key, out var built))
            {
                foreach (var body in built.Bodies)
                {
                    _bodies.Remove(body);
                    PhysicsServer3D.FreeRid(body);
                }
                foreach (var holder in built.Holders) holder.Free();
                CollisionTilesBuilt--;
                BuildCollision(key.Set, key.Tile);
            }
        }
        return touched.Count;
    }

    /// <summary>A copy's factor of its natural size as the view has it (1 natural, 0 burnt down), for tests.</summary>
    public float FactorOf(string id) =>
        _room.Copies!.TryResolve(id, out var set, out var row) && _setIndex.TryGetValue(set, out var index) && _factors[index].TryGetValue(row, out var factor) ? factor : 1f;

    private Rid Convex(Vector3[] points)
    {
        var shape = PhysicsServer3D.ConvexPolygonShapeCreate();
        PhysicsServer3D.ShapeSetData(shape, points);
        PhysicsServer3D.ShapeSetMargin(shape, PlantCollision.ConvexMarginM);
        _shapes.Add(shape);
        return shape;
    }

    private Rid Concave(Vector3[] faces)
    {
        var shape = PhysicsServer3D.ConcavePolygonShapeCreate();
        PhysicsServer3D.ShapeSetData(shape, new Godot.Collections.Dictionary { ["faces"] = faces, ["backface_collision"] = false });
        _shapes.Add(shape);
        return shape;
    }

    /// <summary>A set's typical height: its mesh's height times the mean y scale of up to 1,024 rows spread over its table.</summary>
    private static float TypicalHeightM(PlantSet set)
    {
        var step = Math.Max(1, set.Count / 1024);
        double sum = 0;
        var n = 0;
        for (var row = 0; row < set.Count; row += step, n++) sum += set.Scale(row).Y;
        return set.MeshHeightM * (float)(sum / Math.Max(1, n));
    }

    /// <summary>
    /// One of a set's meshes as a single mesh for a MultiMesh: the GLB's meshes merged (their node transforms applied), each
    /// surface keeping its material, painted as the look paints the land: a landscape material (its glTF extras name a harness
    /// role and say its colours are baked) becomes that role's painterly land material, which keeps the vertex colour the
    /// copies' tints multiply; any other becomes the painterly material of its own name's role.
    /// </summary>
    private static Mesh CopyMesh(RoomData room, string path, string set)
    {
        var scene = RoomBuilder.LoadGlb(room, path, "plant set " + set);
        var parts = new List<(MeshInstance3D Mesh, Transform3D At)>();
        Collect(scene, Transform3D.Identity, parts, root: true);
        if (parts.Count == 0) throw new RoomLoadException($"plant set {set}: {path} has no mesh");
        var merged = new ArrayMesh();
        foreach (var (instance, at) in parts)
            for (var surface = 0; surface < instance.Mesh.GetSurfaceCount(); surface++)
            {
                var tool = new SurfaceTool();
                tool.AppendFrom(instance.Mesh, surface, at);
                var material = instance.GetActiveMaterial(surface);
                var source = material as BaseMaterial3D;
                if (LandscapeLook.TryRole(material, out var role) && source != null)
                    tool.SetMaterial(MaterialLibrary.ForLandscape(role, source.AlbedoColor, LandscapeLook.BakeColor(instance.Mesh, surface, source.AlbedoColor)));
                else if (source != null)
                    tool.SetMaterial(MaterialLibrary.ForCaptured(source.ResourceName.Length > 0 ? source.ResourceName : "foliage", source.AlbedoColor, source.AlbedoTexture));
                else if (material != null) tool.SetMaterial(material);
                tool.Commit(merged);
            }
        scene.Free();
        return merged;
    }

    private static void Collect(Node node, Transform3D parent, List<(MeshInstance3D, Transform3D)> parts, bool root)
    {
        var at = node is Node3D spatial && !root ? parent * spatial.Transform : parent;
        if (node is MeshInstance3D { Mesh: not null } mesh) parts.Add((mesh, at));
        foreach (var child in node.GetChildren()) Collect(child, at, parts, root: false);
    }
}
