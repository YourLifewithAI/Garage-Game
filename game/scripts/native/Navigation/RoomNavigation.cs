using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GarageGame.Native.Room;

namespace GarageGame.Native.Navigation;

/// <summary>
/// Where the companion can walk: a navigation mesh baked at runtime from the room's static collision
/// (StaticBody3D colliders on the world layer under <see cref="SourceRoot"/>), eroded by the companion's
/// radius plus a little clearance, on a navigation map of its own so its cell size never conflicts with
/// anything else. The mesh is derived data, rebuilt whenever that geometry is added, removed or moved;
/// nothing here is authoritative. A route that cannot reach its goal says so, so the companion can report
/// blocked honestly instead of teleporting.
/// <para>
/// Packet S7 of the move to the 10x world (13 October): every room is baked in square tiles (TileM), one region each, from
/// triangle arrays read off the collision shapes; the bakes run on worker threads, a re-bake only bakes the tiles round what
/// moved, and the companion's routes are searched off the main thread too (RequestRoute). Islands (NavigationIslands) say at
/// once that a goal is out of reach, without a search, and give the closest walkable point without Godot's search of every
/// polygon.
/// </para>
/// <para>
/// Why the experiment's tiles did not join, found here: Godot bakes the border inside the bake box, so a box not grown by it
/// stops each tile 4 cm (the agent radius) short of its edge; a tile cut at the room's bounds put its cells off the other
/// tiles' lattice (1 cm short of its seam); a seam on the map's cell lattice split one corner into two cells by float noise
/// (the experiment's half-cell shift cured only this); and joining by margin is quadratic in free edges. So the boxes are
/// grown, every tile lies on one lattice half a cell off the map's, margin joins are off, and NavigationSeams matches the
/// few corners Recast still leaves unmatched.
/// </para>
/// <para>
/// The 10x world (S5, S6): trees and stones are plant copies whose collision is built from the room's rows, not scene nodes, and
/// the land is tiled terrain. Given the room (Room), each tile also bakes the trunks and solid stones round it as obstructions and
/// the tiled ground, both from the room's data (IslandNavigation), on the bake's workers, first bake and re-bakes alike.
/// The trees are those the island's nature leaves (RoomNavigation.Plants.cs): a grown tree's trunk at its grown size, a burnt-down
/// tree's none, the tiles round a tree that changed baked again when the command host says nature moved.
/// </para>
/// </summary>
public partial class RoomNavigation : Node
{
    /// <summary>
    /// 2 cm cells: the 10 cm companion's 2 cm radius plus clearance is exactly two cells, so the agent radius needs no
    /// rounding. 1 cm layers resolve the 6 mm rug and the 2 cm climb.
    /// </summary>
    public const float CellSizeM = 0.02f;
    public const float CellHeightM = 0.01f;
    /// <summary>Added to the body radius so routes keep the body a body-width (2 cm) off walls and corners.</summary>
    public const float ClearanceM = 0.02f;
    /// <summary>How often the source geometry is checked for changes, and how long it must hold still before a re-bake.</summary>
    public const double PollIntervalS = 0.25;
    public const double SettleS = 0.2;
    /// <summary>
    /// A route reaches its goal only when it ends this close to the goal's own nearest walkable point (2.5 cells): a
    /// body hugging a wall stands up to 2 cm inside the eroded margin, while across even a 1 cm wall the two points
    /// are at least 7 cm apart.
    /// </summary>
    public const float SameIslandM = 0.05f;

    /// <summary>
    /// The side of a square tile (400 cells). Every room is tiles, with no size threshold: today's islands (about 7 m) are
    /// one tile or two, the garage magnified ten times (67 x 77 m) about 90. One Recast bake of the magnified island held more
    /// vertices than one polygon mesh may index with a 2 cm climb (S1: 0 polygons); a tile holds a few thousand polygons, a
    /// re-bake after a carry bakes the one to four tiles round it, and each bakes on a worker thread.
    /// </summary>
    public const float TileM = 8f;
    /// <summary>
    /// The tile's border: the agent radius and two more cells of the neighbours' geometry, baked with the tile so erosion
    /// and ledges at its edge come out as they would in one bake. Godot bakes the border inside the bake box (FilterBakingAabb)
    /// and does not grow the box by it, so the box is the tile grown by the border on every side; a tile then ends exactly
    /// on its edge (S7's finding: the experiment's tiles stopped 4 cm short of each edge and left an 8 cm gap). Four cells,
    /// a power of two times the cell, so Godot's whole-cell check on the border never warns.
    /// </summary>
    public const int BorderCells = 4;
    /// <summary>
    /// The first bake of a small map: Create waits for it (at most this long), so today's islands and test rooms (tens of
    /// milliseconds) are ready the moment they are built, exactly as before. A larger map's (the magnified island, a second and
    /// a half) goes on in the background, and the companion floats until it is ready.
    /// </summary>
    public const double FirstBakeWaitMs = 10_000;
    /// <summary>
    /// A map of this many tiles or fewer (today's rooms are one to four) is small: it keeps the timing it had when it baked in
    /// the tick, so a run plays out the same every time. Its first bake is waited for (FirstBakeWaitMs), a re-bake is handed
    /// over exactly SmallReBakeTicks ticks after it starts, routes on it are waited for (IsSmall), and the map joins its tiles
    /// at the end of each physics frame. A larger map (the magnified island, about 90 tiles) is never waited for, takes a
    /// finished bake as soon as it is done, and is joined on worker threads.
    /// </summary>
    public const int SmallBakeTiles = 4;
    /// <summary>
    /// A small map's re-bake goes to the map this many physics ticks after it starts: 167 ms at 60 Hz, longer than such a bake
    /// takes (the test room 20 to 40 ms, the garage island 70 to 90 ms, headless), so a game never waits for it; a headless run,
    /// faster than real time, waits at that tick. Either way it lands on the same tick every run (a check in the small-avatar
    /// suite passes or fails on a millimetre of where the Gubble is).
    /// </summary>
    public const int SmallReBakeTicks = 10;

    /// <summary>
    /// The map keys polygon corners to cells this fraction of its own (1 mm across, 0.5 mm up) to join edges. At Godot's
    /// default, a whole cell, distinct corners of Recast's detail triangles a centimetre apart shared a key, and the map warned
    /// of edges it could not join (the garage island baked with its sea floor). Corners meant to meet are the same to the bit:
    /// Recast repeats a tile's shared corners exactly, and NavigationSeams makes a seam's the same on both sides. (Godot packs a
    /// key in 21 or 22 bits an axis: a kilometre either way at this scale.)
    /// </summary>
    public const float MergeCellScale = 0.05f;

    /// <summary>True for a small map (see SmallBakeTiles): callers wait for its route searches, which take well under a millisecond.</summary>
    public bool IsSmall => TileCount <= SmallBakeTiles;

    /// <summary>
    /// Wait for a task on this thread for at most this long, spinning: a blocking wait with a short timeout rounds up to the
    /// system timer (15.6 ms on Windows), which held physics ticks 26 to 36 ms on the magnified island.
    /// </summary>
    public static bool WaitFor(Task task, double milliseconds)
    {
        if (task.IsCompleted) return true;
        if (milliseconds >= 50) return task.Wait(TimeSpan.FromMilliseconds(milliseconds));
        var deadline = Stopwatch.GetTimestamp() + (long)(milliseconds * Stopwatch.Frequency / 1000);
        var spins = 0;
        while (!task.IsCompleted)
        {
            if (Stopwatch.GetTimestamp() >= deadline) return false;
            if (++spins < 64) Thread.SpinWait(32);
            else Thread.Yield();
        }
        return true;
    }

    /// <summary>A walkable route: corner points from the start's projection onto the mesh to the end, and the bake (Revision) it was planned on.</summary>
    public readonly record struct Route(Vector3[] Points, bool Reaches, bool Direct, float LengthM, int Revision = 0)
    {
        public static readonly Route None = new(Array.Empty<Vector3>(), false, false, 0);
        public bool IsEmpty => Points.Length == 0;
    }

    public Node3D SourceRoot { get; private set; } = null!;
    /// <summary>
    /// The room the source root was built from (RoomBuilder.Build: room metres are the source root's own space), when the bake
    /// takes part of its sources from the room's data (IslandNavigation; the 10x world): the copies' collision is built from their
    /// rows as physics-server bodies (PlantCopiesView), never scene nodes, so the scene parse cannot see a trunk or a stone. Each
    /// tile then bakes the trunks and solid stones round it as obstructions, at every bake, and the ground of the tiled terrain
    /// parts, read from the room once and kept; those parts' scene bodies are not parsed (Parses). Null: the scene alone.
    /// </summary>
    public RoomData? Room { get; private set; }
    /// <summary>The room's ground triangles the tiles bake (read once, at the first bake; 0 before it or without a room).</summary>
    public int RoomGroundTriangles { get; private set; }
    /// <summary>Obstructions the last bake took from the room's data, counted once per tile they reach into.</summary>
    public int LastRoomObstructions { get; private set; }
    /// <summary>The baking box in the source root's space: the room bounds, a little deeper below and above.</summary>
    public Aabb BakeBounds { get; private set; }
    /// <summary>The horizontal cell of this mesh (CellSizeM unless a measurement asks for another).</summary>
    public float CellM { get; private set; } = CellSizeM;
    public float AgentRadiusM { get; private set; }
    public float AgentHeightM { get; private set; }
    public float AgentMaxClimbM { get; private set; }
    public Rid Map { get; private set; }
    public int PolygonCount { get; private set; }
    /// <summary>The last bake from its start to the moment it went to the map (it runs on worker threads).</summary>
    public double LastBakeMs { get; private set; }
    /// <summary>Of that, the time the main thread spent on it: reading the scene, waiting and handing the tiles to the map.</summary>
    public double LastBakeMainThreadMs { get; private set; }
    /// <summary>The longest any one physics tick of this node took (reading the scene, waiting for or applying a bake).</summary>
    public double WorstTickMs { get; set; }
    public int TileCount => _tiles.Length;
    public int TileColumns { get; private set; }
    public int TileRows { get; private set; }
    /// <summary>Tiles baked by the last bake (all of them the first time; only those round what moved afterwards).</summary>
    public int LastTilesBaked { get; private set; }
    /// <summary>True while a bake runs on the worker threads or is being handed to the map.</summary>
    public bool Baking => _generation != null || _handOver != null;

    /// <summary>
    /// Completed bakes in the map: a route planned on an older revision should be planned again. A bake counts only once the
    /// map holds it, so a route planned on this revision was planned on this bake's tiles, never on the ones before.
    /// <para>
    /// A map of more than SmallBakeTiles tiles builds itself on worker threads (Godot's asynchronous iterations: joining the
    /// magnified island's 80,000 polygons took 27 ms of the main thread at each re-bake, and taking in six re-baked tiles
    /// another 8 ms), so when it holds a bake is not a frame count. Each bake moves a marker, one triangle 200 m under the
    /// room whose height is the bake's number, once every tile it changed has taken its new polygons (RegionGetIterationId):
    /// the bake is in the map once the map's way to where its marker is ends on that marker.
    /// </para>
    /// </summary>
    public int Revision
    {
        get
        {
            RefreshLive();
            return _liveRevision;
        }
    }

    /// <summary>Whether the map holds the bake handed over last yet (checked once a physics frame, once the map has changed since).</summary>
    private void RefreshLive()
    {
        if (_appliedRevision == _liveRevision || !Map.IsValid) return;
        var frame = Engine.GetPhysicsFrames();
        if (frame == _liveCheckedFrame && !_liveCheckNow) return;
        _liveCheckedFrame = frame;
        _liveCheckNow = false;
        if (_markerWaits.Count > 0)
        {
            // The tiles take their new polygons on worker threads: the marker moves once all of them have.
            if (_markerWaits.Any(w => w.Region.IsValid && NavigationServer3D.RegionGetIterationId(w.Region) == w.Iteration)) return;
            _markerWaits.Clear();
            MoveMarker();
            return;
        }
        if (NavigationServer3D.MapGetIterationId(Map) == _appliedAtIteration) return;
        // The marker has a navigation layer of its own, so the map looks at its one polygon only (its closest point to anything
        // else looks through every polygon of the map: 2.7 ms on the magnified island).
        var marker = SourceRoot.GlobalTransform * MarkerAt(_appliedRevision);
        var path = NavigationServer3D.MapGetPath(Map, marker, marker, false, MarkerLayer);
        if (path.Length > 0 && path[^1].DistanceTo(marker) < 0.5f * MarkerStepM)
        {
            _liveRevision = _appliedRevision;
            _liveIslands = _islands;
            LastLiveFrame = Engine.GetPhysicsFrames();
        }
    }

    /// <summary>The navigation layer of the marker alone (the tiles are on layer 1, which every route uses).</summary>
    private const uint MarkerLayer = 1u << 31;
    /// <summary>The physics frame the last bake was handed to the map in, and the one it was found in the map (for measurements).</summary>
    public ulong LastAppliedFrame { get; private set; }
    public ulong LastLiveFrame { get; private set; }

    /// <summary>The marker of a bake (see Revision): under the middle of the bake bounds, MarkerDepthM down, a step lower for each bake.</summary>
    private Vector3 MarkerAt(int revision) =>
        new(BakeBounds.GetCenter().X, BakeBounds.Position.Y - MarkerDepthM - (revision % 1000) * MarkerStepM, BakeBounds.GetCenter().Z);

    private const float MarkerDepthM = 200f;
    private const float MarkerStepM = 0.1f;

    /// <summary>Tiles handed over whose regions have not taken their new polygons yet, with the iteration they had.</summary>
    private readonly List<(Rid Region, long Iteration)> _markerWaits = new();

    /// <summary>The bake's marker goes to the map (in the frame its tiles' regions all hold their new polygons).</summary>
    private void MoveMarker()
    {
        NavigationServer3D.RegionSetTransform(_marker, SourceRoot.GlobalTransform);
        NavigationServer3D.RegionSetNavigationMesh(_marker, MarkerMesh(_appliedRevision));
        _appliedAtIteration = NavigationServer3D.MapGetIterationId(Map);
    }

    private NavigationMesh MarkerMesh(int revision)
    {
        var at = MarkerAt(revision);
        var mesh = new NavigationMesh { CellSize = CellM, CellHeight = CellHeightM };
        mesh.SetVertices(new[] { at, at + new Vector3(0, 0, 0.04f), at + new Vector3(0.04f, 0, 0) });
        mesh.AddPolygon(new[] { 0, 1, 2 });
        return mesh;
    }

    /// <summary>True from the first bake that produced a walkable mesh, once the map has taken it.</summary>
    public bool IsReady => Revision > 0 && PolygonCount > 0 && Map.IsValid && NavigationServer3D.MapGetIterationId(Map) > 0;

    private int _appliedRevision;
    private int _liveRevision;
    private long _appliedAtIteration;
    private ulong _liveCheckedFrame = ulong.MaxValue;
    private bool _liveCheckNow;
    private Rid _marker;
    private int _signature;
    /// <summary>Change counts of shapes whose data is too large to hash each poll (concave meshes, height maps), from their changed signal.</summary>
    private readonly Dictionary<ulong, int> _shapeVersions = new();
    private bool _changed;
    private bool _dirtyAll;
    private readonly List<Aabb> _dirtyBoxes = new();
    private double _sincePoll;
    private double _sinceChange;
    private Tile[] _tiles = Array.Empty<Tile>();
    private float _tileX0, _tileZ0, _tileStep;
    private Generation? _generation;
    private int _generationNumber;
    /// <summary>What each body was at the last bake: its fingerprint and bounds (for the tiles to bake again when it changes).</summary>
    private Dictionary<ulong, BodyRecord> _records = new();
    /// <summary>
    /// Each body's triangles as last read, by fingerprint: the land's faces are read off its shapes once (about 54 MB on the
    /// magnified island), not again at every re-bake, where reading them made a garbage collection that held a tick 28 ms.
    /// Only the bake's worker touches it (one bake at a time).
    /// </summary>
    private readonly Dictionary<ulong, (int Signature, float[] Triangles)> _triangles = new();
    private readonly NavigationIslands.Scratch _islandScratch = new();
    /// <summary>For each body's triangles, which of them each tile holds (IndexByTile), kept with them. The bake's worker's alone.</summary>
    private readonly Dictionary<ulong, (float[] Triangles, List<int>?[] ByTile)> _tileTriangles = new();
    /// <summary>The islands of the bake handed over last (the map may not hold it yet).</summary>
    private NavigationIslands? _islands;
    /// <summary>The islands of the bake the map holds: for "out of reach" without a search, and the closest walkable point.</summary>
    private volatile NavigationIslands? _liveIslands;
    /// <summary>Route searches on worker threads hold this for reading; leaving the tree takes it for writing, so the map is never freed under one.</summary>
    private readonly ReaderWriterLockSlim _serverLock = new();
    private volatile bool _closed;

    /// <summary>The integrator's one-line wiring: bake the built room for the companion and give it the map.</summary>
    public static RoomNavigation Attach(RoomWorld world)
    {
        // An island bakes its land only: the sea floor out to the reef doubled the bake (garage: 86-114 ms against 50-57 ms),
        // and nobody walks there (swimmers swim, the Gubble floats).
        var bounds = world.Room.Sea?.LandBounds(world.Room.Bounds) ?? world.Room.Bounds;
        // The trees as the island's nature has them (grown ones larger, burnt ones gone): read from the command host, which the
        // room world attached before this (RoomNavigation.Plants.cs).
        var navigation = Create(world, world.Built, bounds, WorldScaleProfile.Companion, world.Companion.StepHeightM, room: world.Room, nature: Kernel.CommandHost.Of(world));
        world.Companion.BindNavigation(navigation);
        return navigation;
    }

    /// <param name="maxClimbM">
    /// The body's step (2 cm). S1 measured the magnified island: on 2 cm cells a 1 cm climb drops every slope over 26.6
    /// degrees, which split the land so only 31% of it was connected to the Gubble's spawn; a 2 cm climb keeps slopes to 45
    /// degrees, the body's own steepest walkable floor.
    /// </param>
    /// <param name="room">The room sourceRoot was built from, for the sources its data gives (see Room); null bakes the scene alone.</param>
    /// <param name="nature">The command host whose nature the trees follow (grown ones larger, burnt ones gone); null leaves every tree at its natural size, as the room's data has it.</param>
    public static RoomNavigation Create(Node parent, Node3D sourceRoot, Aabb bounds, WorldScaleProfile agent, float maxClimbM, float cellM = CellSizeM, RoomData? room = null,
        Kernel.CommandHost? nature = null)
    {
        var navigation = new RoomNavigation
        {
            Name = "RoomNavigation", SourceRoot = sourceRoot, CellM = cellM, Room = room, _nature = nature,
            BakeBounds = new Aabb(bounds.Position - new Vector3(0, 0.1f, 0), bounds.Size + new Vector3(0, 0.2f, 0)),
            // Whole cells, as the baker rounds them anyway (and warns when it has to). For the 10 cm companion:
            // 2 + 2 cm clearance is 4 cm, the height 10 cm, and its 2 cm step a 2 cm climb.
            AgentRadiusM = Cells((float)agent.RadiusMeters + ClearanceM, cellM, up: true),
            AgentHeightM = Cells((float)agent.HeightMeters, CellHeightM, up: true),
            AgentMaxClimbM = Mathf.Max(CellHeightM, Cells(maxClimbM, CellHeightM, up: false))
        };
        parent.AddChild(navigation);
        return navigation;
    }

    public override void _Ready()
    {
        LayOutTiles();
        Map = NavigationServer3D.MapCreate();
        NavigationServer3D.MapSetCellSize(Map, CellM);
        NavigationServer3D.MapSetCellHeight(Map, CellHeightM);
        NavigationServer3D.MapSetUp(Map, Vector3.Up);
        // Tiles join by the map's exact edge keys (their seams are made to match: NavigationSeams); the margin search compares
        // every free edge with every other, which took 18.6 s on the magnified island.
        NavigationServer3D.MapSetUseEdgeConnections(Map, false);
        NavigationServer3D.MapSetMergeRasterizerCellScale(Map, MergeCellScale);
        // A small map (today's rooms) updates at the end of each physics frame, so a bake is in every route from the next frame,
        // as before; a larger one is joined on a worker thread (see Revision).
        NavigationServer3D.MapSetUseAsyncIterations(Map, TileCount > SmallBakeTiles);
        NavigationServer3D.MapSetActive(Map, true);
        _marker = NavigationServer3D.RegionCreate();
        NavigationServer3D.RegionSetUseAsyncIterations(_marker, false);
        NavigationServer3D.RegionSetNavigationLayers(_marker, MarkerLayer);
        NavigationServer3D.RegionSetMap(_marker, Map);
        GetTree().NodeAdded += SceneChanged;
        GetTree().NodeRemoved += SceneChanged;
        if (_nature != null) _nature.NatureChanged += OnNatureChanged;
        _dirtyAll = true;
        StartGeneration();
        // Today's rooms bake within this, and are ready the moment they are built, as they always were.
        if (_generation != null && IsSmall && WaitFor(_generation.Task, FirstBakeWaitMs)) ApplyGeneration();
    }

    public override void _ExitTree()
    {
        _closed = true;
        GetTree().NodeAdded -= SceneChanged;
        GetTree().NodeRemoved -= SceneChanged;
        if (_nature != null && IsInstanceValid(_nature)) _nature.NatureChanged -= OnNatureChanged;
        // A bake in flight stops after the tile it is on (it checks _closed); let it, so no worker calls the server at shutdown.
        _generation?.Task.Wait(TimeSpan.FromSeconds(10));
        _generation = null;
        _handOver = null;
        _serverLock.EnterWriteLock();
        try
        {
            foreach (var tile in _tiles)
                if (tile.Region.IsValid)
                {
                    NavigationServer3D.FreeRid(tile.Region);
                    tile.Region = default;
                }
            if (_marker.IsValid) NavigationServer3D.FreeRid(_marker);
            _marker = default;
            if (Map.IsValid) NavigationServer3D.FreeRid(Map);
            Map = default;
        }
        finally { _serverLock.ExitWriteLock(); }
    }

    /// <summary>Re-bake every tile at the next poll even if no change was seen (for geometry changes a poll cannot detect).</summary>
    public void MarkDirty()
    {
        _dirtyAll = true;
        _changed = true;
        _sinceChange = SettleS;
    }

    /// <summary>Re-bake the tiles round a box (in the source root's space) at the next poll.</summary>
    public void MarkDirty(Aabb box)
    {
        _dirtyBoxes.Add(box);
        _changed = true;
        _sinceChange = SettleS;
    }

    public override void _PhysicsProcess(double delta)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            RefreshLive();
            if (_handOver != null) ApplyGeneration();
            else if (_generation != null)
            {
                // A small map's re-bake lands on its set tick, waited for there if need be (SmallReBakeTicks); a large map's when done.
                if (IsSmall && Engine.GetPhysicsFrames() - _generation.StartedFrame >= SmallReBakeTicks) _generation.Task.Wait();
                if (_generation.Task.IsCompleted && (!IsSmall || Engine.GetPhysicsFrames() - _generation.StartedFrame >= SmallReBakeTicks)) ApplyGeneration();
                else _generation.MainMs += Elapsed(started);
            }
            if (!IsInstanceValid(SourceRoot) || !SourceRoot.IsInsideTree()) return;
            _sincePoll += delta;
            _sinceChange += delta;
            if (_sincePoll < PollIntervalS) return;
            _sincePoll = 0;
            // The trees as nature has them: a difference marks the tiles round the trees that changed, and bakes them at once
            // (nothing is still moving: a cast or a burnt-down tree is a step, not a drag).
            if (DetectPlants())
            {
                _changed = true;
                _sinceChange = Math.Max(_sinceChange, SettleS);
            }
            var signature = Signature();
            if (signature != _signature)
            {
                // Still moving (a carried or dragged object): wait until it holds still.
                _signature = signature;
                _changed = true;
                _sinceChange = 0;
                return;
            }
            if (_changed && _sinceChange >= SettleS && _generation == null && _handOver == null) StartGeneration();
        }
        finally
        {
            WorstTickMs = Math.Max(WorstTickMs, Elapsed(started));
        }
    }

    /// <summary>Bake now from the source root's current static collision, every tile, and wait for it (measurements and tests).</summary>
    public void Bake()
    {
        if (!Map.IsValid || !IsInstanceValid(SourceRoot) || !SourceRoot.IsInsideTree()) return;
        if (_handOver != null) ApplyGeneration(all: true);
        if (_generation != null)
        {
            _generation.Task.Wait();
            ApplyGeneration(all: true);
        }
        _dirtyAll = true;
        StartGeneration();
        _generation?.Task.Wait();
        if (_generation != null) ApplyGeneration(all: true);
    }

    // ---- tiles ----

    private sealed class Tile
    {
        public int Index;
        /// <summary>The tile's own square (ending at the first cell edge past the bake bounds at the outside), in the source root's space.</summary>
        public Rect2 Rect;
        /// <summary>The bake box: the square grown by the border, the bake bounds' height.</summary>
        public Aabb BakeBox;
        public Rid Region;
        /// <summary>What Recast baked for the tile.</summary>
        public NavigationIslands.TileGeometry? Raw;
        /// <summary>The same with its seams matched to its neighbours' (NavigationSeams): what the map holds.</summary>
        public NavigationIslands.TileGeometry? Geometry;
        public NavigationMesh? Mesh;
    }

    /// <summary>
    /// Square tiles over the bake bounds. Their seams lie half a cell off the map's cell lattice: the map joins two tiles'
    /// edges when their corners fall in the same cell, and a seam exactly on a cell boundary put float noise either side of
    /// it (-0.65999997 against -0.6600001, measured), so the same corner fell in two cells and the seam did not join. Half a
    /// cell off, the noise stays inside one cell.
    /// </summary>
    private void LayOutTiles()
    {
        var low = BakeBounds.Position;
        var high = BakeBounds.End;
        _tileStep = Mathf.Round(TileM / CellM) * CellM;
        _tileX0 = Mathf.Floor(low.X / CellM) * CellM - 0.5f * CellM;
        _tileZ0 = Mathf.Floor(low.Z / CellM) * CellM - 0.5f * CellM;
        TileColumns = Math.Max(1, Mathf.CeilToInt((high.X - _tileX0) / _tileStep - 1e-4f));
        TileRows = Math.Max(1, Mathf.CeilToInt((high.Z - _tileZ0) / _tileStep - 1e-4f));
        var border = BorderCells * CellM;
        // The outer edges stop at the first cell edge past the bounds (not at the bounds): every tile's box then lies on the
        // same cell lattice, so Recast's cells, and the corners it puts on a seam, are the same on both sides of it (a first
        // tile cut at the bounds ended 1 cm short of its seam, measured).
        var xEnd = _tileX0 + Mathf.Ceil((high.X - _tileX0) / CellM - 1e-3f) * CellM;
        var zEnd = _tileZ0 + Mathf.Ceil((high.Z - _tileZ0) / CellM - 1e-3f) * CellM;
        _tiles = new Tile[TileColumns * TileRows];
        for (var j = 0; j < TileRows; j++)
            for (var i = 0; i < TileColumns; i++)
            {
                var x0 = _tileX0 + i * _tileStep;
                var x1 = i == TileColumns - 1 ? xEnd : _tileX0 + (i + 1) * _tileStep;
                var z0 = _tileZ0 + j * _tileStep;
                var z1 = j == TileRows - 1 ? zEnd : _tileZ0 + (j + 1) * _tileStep;
                var rect = new Rect2(x0, z0, x1 - x0, z1 - z0);
                _tiles[j * TileColumns + i] = new Tile
                {
                    Index = j * TileColumns + i, Rect = rect,
                    BakeBox = new Aabb(new Vector3(x0 - border, low.Y, z0 - border), new Vector3(rect.Size.X + 2 * border, BakeBounds.Size.Y, rect.Size.Y + 2 * border)),
                };
            }
    }

    private NavigationMesh TileSettings(Tile tile) => new()
    {
        CellSize = CellM, CellHeight = CellHeightM,
        // The default 6 cells, except on finer cells, where Godot clamps the sample distance to 0.1 m (and warns).
        DetailSampleDistance = Mathf.Max(6.0f, Mathf.Ceil(0.1f / CellM) + 1),
        AgentRadius = AgentRadiusM, AgentHeight = AgentHeightM, AgentMaxClimb = AgentMaxClimbM, AgentMaxSlope = 45.0f,
        // Low ceilings and ledges are not walkable; a low lip within the climb is.
        FilterWalkableLowHeightSpans = true, FilterLedgeSpans = true, FilterLowHangingObstacles = true,
        FilterBakingAabb = tile.BakeBox,
        BorderSize = BorderCells * CellM,
        // At 1.0 or below the contour keeps its corners on the tile's edge (Godot's note on BorderSize).
        EdgeMaxError = 1.0f,
    };

    // ---- bakes ----

    private sealed class Generation
    {
        public int Number;
        public Task<GenerationResult> Task = null!;
        public long Started;
        public ulong StartedFrame;
        public double MainMs;
        public double SnapshotMs;
        public int Collections;
    }

    private sealed class GenerationResult
    {
        public double ReadMs, BakeMs, SeamsMs, IslandsMs;
        public Dictionary<ulong, BodyRecord> Records = new();
        /// <summary>Tiles baked (their new raw geometry, null for nothing walkable) and tiles whose seams changed with them.</summary>
        public List<(Tile Tile, NavigationIslands.TileGeometry? Raw, NavigationIslands.TileGeometry? Geometry, NavigationMesh? Mesh)> Changed = new();
        public int Baked;
        public NavigationIslands? Islands;
        /// <summary>The room's ground triangles kept for the tiles, and the obstructions from its data put into the tiles baked (see Room).</summary>
        public int RoomGroundTriangles, RoomObstructions;
    }

    /// <summary>
    /// The room's ground under the tiles to bake (IslandNavigation.GroundTiles): each 16 m tile's level-0 faces, nine floats a
    /// triangle in Godot's face order (as a trimesh's faces come), with which of them each tile holds. Read on the bake's worker for
    /// that bake and let go (S12b: kept, the 10x island's was about 45 MB of C#'s heap; the room's data holds the ground compactly,
    /// and a re-bake reads only the few ground tiles under the tiles it bakes).
    /// </summary>
    private static List<(float[] Triangles, List<int>?[] ByTile)> ReadRoomGround(RoomData room, Tile[] tiles, NavigationSeams.Layout layout, List<Tile> chosen)
    {
        var pieces = IslandNavigation.GroundTiles(room).Where(piece =>
        {
            var (tx, tz, _) = piece.Part.Tiles[piece.Tile];
            float x0 = tx * TerrainTiles.TileM, z0 = tz * TerrainTiles.TileM, x1 = x0 + TerrainTiles.TileM, z1 = z0 + TerrainTiles.TileM;
            return chosen.Any(t => t.BakeBox.Position.X <= x1 && t.BakeBox.End.X >= x0 && t.BakeBox.Position.Z <= z1 && t.BakeBox.End.Z >= z0);
        }).ToList();
        var ground = new (float[] Triangles, List<int>?[] ByTile)[pieces.Count];
        System.Threading.Tasks.Parallel.For(0, pieces.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, System.Environment.ProcessorCount - 2) }, i =>
        {
            var faces = pieces[i].Part.GroundFaces(pieces[i].Tile);
            var triangles = new float[faces.Length / 3 * 9];
            for (var k = 0; k < triangles.Length / 3; k++)
            {
                triangles[k * 3] = faces[k].X;
                triangles[k * 3 + 1] = faces[k].Y;
                triangles[k * 3 + 2] = faces[k].Z;
            }
            ground[i] = (triangles, IndexByTile(triangles, tiles, layout));
        });
        return ground.Where(g => g.Triangles.Length > 0).ToList();
    }

    /// <summary>Read the scene (main thread: references and transforms, never the big arrays) and start a bake on the workers.</summary>
    private void StartGeneration()
    {
        if (!Map.IsValid || !IsInstanceValid(SourceRoot) || !SourceRoot.IsInsideTree() || _generation != null || _handOver != null) return;
        var started = Stopwatch.GetTimestamp();
        var toRoot = SourceRoot.GlobalTransform.AffineInverse();
        var bodies = Snapshot(toRoot, out var signature);
        // The trees' sizes this bake takes (the poll may not have looked since nature last changed: a bake asked for now sees it).
        DetectPlants();
        var plants = _plantsSeen;
        var all = _dirtyAll;
        var boxes = _dirtyBoxes.ToList();
        var records = _records;
        _signature = signature;
        _changed = false;
        _dirtyAll = false;
        _dirtyBoxes.Clear();
        var number = ++_generationNumber;
        var tiles = _tiles;
        var islandsLayout = new NavigationSeams.Layout(_tileX0, _tileZ0, _tileStep, TileColumns, TileRows, CellM);
        var generation = new Generation { Number = number, Started = started, StartedFrame = Engine.GetPhysicsFrames(), Collections = GC.CollectionCount(2) };
        generation.Task = System.Threading.Tasks.Task.Factory.StartNew(() => RunGeneration(generation, all, boxes, bodies, records, tiles, islandsLayout, plants),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _generation = generation;
        _generation.SnapshotMs = _generation.MainMs = Elapsed(started);
    }

    /// <summary>On a worker: which bodies changed, which tiles they touch, and those tiles baked (in parallel), then the islands.</summary>
    private GenerationResult RunGeneration(Generation generation, bool all, List<Aabb> boxes, List<BodySource> bodies, Dictionary<ulong, BodyRecord> previous,
        Tile[] tiles, NavigationSeams.Layout layout, Dictionary<string, PlantSize> plants)
    {
        var number = generation.Number;
        var clock = Stopwatch.StartNew();
        var result = new GenerationResult();
        var dirty = new List<Aabb>(boxes);
        var seen = new HashSet<ulong>();
        foreach (var body in bodies)
            if (_triangles.TryGetValue(body.Id, out var cached) && cached.Signature == body.Signature) body.Reuse(cached.Triangles);
        // The bodies that changed are read now, side by side (the land's trimeshes, once, are most of the reading).
        var changed = bodies.Where(b => all || !previous.TryGetValue(b.Id, out var old) || old.Signature != b.Signature).ToList();
        System.Threading.Tasks.Parallel.ForEach(changed, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, System.Environment.ProcessorCount - 2) }, b => b.Triangles());
        foreach (var body in bodies)
        {
            seen.Add(body.Id);
            var known = previous.TryGetValue(body.Id, out var old);
            if (!all && known && old!.Signature == body.Signature)
            {
                result.Records[body.Id] = old;
                continue;
            }
            var bounds = body.Bounds();
            _triangles[body.Id] = (body.Signature, body.Triangles());
            if (known && old!.HasBounds) dirty.Add(old.Bounds);
            if (bounds != null) dirty.Add(bounds.Value);
            result.Records[body.Id] = new BodyRecord(body.Signature, bounds ?? default, bounds != null);
        }
        foreach (var (id, old) in previous)
            if (!seen.Contains(id) && old.HasBounds) dirty.Add(old.Bounds);
        foreach (var id in _triangles.Keys.Where(id => !seen.Contains(id)).ToList()) _triangles.Remove(id);
        foreach (var id in _tileTriangles.Keys.Where(id => !seen.Contains(id)).ToList()) _tileTriangles.Remove(id);
        var chosen = tiles.Where(t => all || dirty.Any(d => Overlaps(t.BakeBox, d))).ToList();
        // Which bodies' triangles go into each tile to bake (by the index of each body's triangles per tile, worked out once
        // for its triangles: a re-bake then copies a tile's share of the land, not the whole island's).
        var shares = chosen.ToDictionary(t => t.Index, _ => new List<(float[] Triangles, List<int> Ids)>());
        var blocks = chosen.ToDictionary(t => t.Index, _ => new List<Obstruction>());
        foreach (var body in bodies)
        {
            var record = result.Records[body.Id];
            if (!record.HasBounds) continue;
            var touched = chosen.Where(t => Overlaps(t.BakeBox, record.Bounds)).ToArray();
            if (touched.Length == 0) continue;
            var triangles = body.Triangles();
            if (!_triangles.TryGetValue(body.Id, out var cached) || cached.Triangles != triangles) _triangles[body.Id] = cached = (body.Signature, triangles);
            if (!_tileTriangles.TryGetValue(body.Id, out var index) || index.Triangles != triangles)
                _tileTriangles[body.Id] = index = (triangles, IndexByTile(triangles, tiles, layout));
            foreach (var tile in touched)
                if (index.ByTile[tile.Index] is { } ids) shares[tile.Index].Add((triangles, ids));
            foreach (var block in body.Obstructions)
                foreach (var tile in touched)
                    if (Overlaps(tile.BakeBox, block.Bounds)) blocks[tile.Index].Add(block);
        }
        // The room's own ground under the tiles to bake (see ReadRoomGround); each takes its share, as it takes a body's.
        var room = Room;
        if (room != null)
        {
            var ground = ReadRoomGround(room, tiles, layout, chosen);
            foreach (var (triangles, byTile) in ground)
                foreach (var tile in chosen)
                    if (byTile[tile.Index] is { } ids) shares[tile.Index].Add((triangles, ids));
            result.RoomGroundTriangles = ground.Sum(g => g.Triangles.Length / 9);
        }
        var obstructed = 0;
        // The trees nature resized: their natural trunks are left out of every tile and their trunks as they are now put in, made once here.
        var standing = PlantObstacles.Resized(plants.Values, _standing);
        result.ReadMs = clock.Elapsed.TotalMilliseconds;
        var baked = new NavigationIslands.TileGeometry?[chosen.Count];
        var next = -1;
        void Worker()
        {
            while (true)
            {
                var k = Interlocked.Increment(ref next);
                if (k >= chosen.Count || _closed) return;
                var tile = chosen[k];
                var count = shares[tile.Index].Sum(s => s.Ids.Count);
                if (count == 0) continue;
                // Pooled arrays (a tile's share of the land is megabytes; fresh ones every re-bake fed the garbage collector).
                var floats = System.Buffers.ArrayPool<float>.Shared.Rent(count * 9);
                var indices = System.Buffers.ArrayPool<int>.Shared.Rent(count * 3);
                try
                {
                    var n = 0;
                    foreach (var (triangles, ids) in shares[tile.Index])
                        foreach (var id in ids)
                        {
                            Array.Copy(triangles, id * 9, floats, n * 9, 9);
                            n++;
                        }
                    // Godot's face order to Recast's: each face's second and third corners swapped (as add_faces does).
                    for (var i = 0; i < count; i++) (indices[i * 3], indices[i * 3 + 1], indices[i * 3 + 2]) = (i * 3, i * 3 + 2, i * 3 + 1);
                    var source = new NavigationMeshSourceGeometryData3D();
                    source.AppendArrays(floats.AsSpan(0, count * 9), indices.AsSpan(0, count * 3));
                    foreach (var block in blocks[tile.Index]) source.AddProjectedObstruction(block.Corners, block.Bottom, block.Height, false);
                    // The trunks and solid stones that reach into the tile's bake box, from the room's rows (see Room), the trees as
                    // nature has them (RoomNavigation.Plants.cs).
                    if (room != null)
                        Interlocked.Add(ref obstructed, PlantObstacles.AddTo(room, source,
                            new Rect2(tile.BakeBox.Position.X, tile.BakeBox.Position.Z, tile.BakeBox.Size.X, tile.BakeBox.Size.Z), plants, standing));
                    var mesh = TileSettings(tile);
                    NavigationServer3D.BakeFromSourceGeometryData(mesh, source);
                    if (mesh.GetPolygonCount() > 0) baked[k] = NavigationIslands.TileGeometry.From(tile.Index, mesh);
                }
                finally
                {
                    System.Buffers.ArrayPool<float>.Shared.Return(floats);
                    System.Buffers.ArrayPool<int>.Shared.Return(indices);
                }
            }
        }
        var threads = Math.Clamp(System.Environment.ProcessorCount - 2, 1, Math.Max(1, chosen.Count));
        var workers = Enumerable.Range(0, threads).Select(_ => System.Threading.Tasks.Task.Factory.StartNew(Worker, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        System.Threading.Tasks.Task.WaitAll(workers);
        result.BakeMs = clock.Elapsed.TotalMilliseconds - result.ReadMs;
        result.Baked = chosen.Count;
        result.RoomObstructions = obstructed;
        if (_closed) return result;
        // Seams: the baked tiles and their neighbours, matched corner for corner (NavigationSeams), as the map will hold them.
        var raw = tiles.Select(t => t.Raw).ToArray();
        for (var k = 0; k < chosen.Count; k++) raw[chosen[k].Index] = baked[k];
        var affected = new HashSet<int>();
        foreach (var tile in chosen)
        {
            var (column, row) = (tile.Index % layout.Columns, tile.Index / layout.Columns);
            for (var r = Math.Max(0, row - 1); r <= Math.Min(layout.Rows - 1, row + 1); r++)
                for (var c = Math.Max(0, column - 1); c <= Math.Min(layout.Columns - 1, column + 1); c++)
                    affected.Add(r * layout.Columns + c);
        }
        var conformed = NavigationSeams.Conform(raw, affected.Where(t => raw[t] != null), layout);
        var geometry = tiles.Select(t => t.Geometry).ToArray();
        var chosenSet = chosen.Select(t => t.Index).ToHashSet();
        foreach (var t in affected)
        {
            var tileGeometry = conformed.GetValueOrDefault(t);
            if (tileGeometry == null && !chosenSet.Contains(t)) continue;
            geometry[t] = tileGeometry;
            result.Changed.Add((tiles[t], raw[t], tileGeometry, tileGeometry?.ToMesh(CellM, CellHeightM)));
        }
        result.SeamsMs = clock.Elapsed.TotalMilliseconds - result.ReadMs - result.BakeMs;
        result.Islands = NavigationIslands.Build(number, geometry, layout with { Cell = layout.Cell * MergeCellScale }, CellHeightM * MergeCellScale, _islandScratch);
        result.IslandsMs = clock.Elapsed.TotalMilliseconds - result.ReadMs - result.BakeMs - result.SeamsMs;
        return result;
    }

    /// <summary>For each tile, the triangles (by number) that touch its bake box, from the lattice and then each box.</summary>
    private static List<int>?[] IndexByTile(float[] triangles, Tile[] tiles, NavigationSeams.Layout layout)
    {
        var byTile = new List<int>?[tiles.Length];
        var reach = BorderCells * layout.Cell + 0.001f;
        for (var v = 0; v + 8 < triangles.Length; v += 9)
        {
            var minX = Math.Min(triangles[v], Math.Min(triangles[v + 3], triangles[v + 6]));
            var maxX = Math.Max(triangles[v], Math.Max(triangles[v + 3], triangles[v + 6]));
            var minZ = Math.Min(triangles[v + 2], Math.Min(triangles[v + 5], triangles[v + 8]));
            var maxZ = Math.Max(triangles[v + 2], Math.Max(triangles[v + 5], triangles[v + 8]));
            var c0 = Math.Clamp((int)MathF.Floor((minX - reach - layout.X0) / layout.Step), 0, layout.Columns - 1);
            var c1 = Math.Clamp((int)MathF.Floor((maxX + reach - layout.X0) / layout.Step), 0, layout.Columns - 1);
            var r0 = Math.Clamp((int)MathF.Floor((minZ - reach - layout.Z0) / layout.Step), 0, layout.Rows - 1);
            var r1 = Math.Clamp((int)MathF.Floor((maxZ + reach - layout.Z0) / layout.Step), 0, layout.Rows - 1);
            for (var r = r0; r <= r1; r++)
                for (var c = c0; c <= c1; c++)
                {
                    var box = tiles[r * layout.Columns + c].BakeBox;
                    if (maxX < box.Position.X || minX > box.End.X || maxZ < box.Position.Z || minZ > box.End.Z) continue;
                    (byTile[r * layout.Columns + c] ??= new List<int>()).Add(v / 9);
                }
        }
        return byTile;
    }

    /// <summary>
    /// A large map takes at most this many polygons from a finished bake each physics tick (at least one tile): handing a
    /// region its mesh copies it in the navigation server's own step on the main thread, so no tick copies the whole magnified
    /// island (80,000 polygons) at once; it is in the map about 30 ticks after its bake.
    /// </summary>
    public const int HandOverPolygonsPerTick = 3000;

    /// <summary>The bake being handed to the map, a few tiles a tick (null when none), and how far it has got.</summary>
    private (Generation Generation, GenerationResult Result, int Next, double Ms)? _handOver;

    /// <summary>
    /// Hand a finished bake's tiles to the map and count it (main thread): a small map's all at once (as before), a large
    /// map's HandOverPolygonsPerTick a tick, unless all is asked for.
    /// </summary>
    private void ApplyGeneration(bool all = false)
    {
        var started = Stopwatch.GetTimestamp();
        if (_handOver == null)
        {
            var generation = _generation!;
            _generation = null;
            GenerationResult taken;
            try { taken = generation.Task.Result; }
            catch (Exception error)
            {
                GD.PushError("Room navigation: a bake failed: " + error.GetBaseException().Message);
                return;
            }
            if (!Map.IsValid) return;
            // What went to the map before is live by now or not: settle that before this bake's hand-over is counted.
            RefreshLive();
            _handOver = (generation, taken, 0, 0);
        }
        var (owner, result, next, ms) = _handOver.Value;
        var transform = SourceRoot.GlobalTransform;
        var budget = all || IsSmall ? int.MaxValue : HandOverPolygonsPerTick;
        var handed = 0;
        while (next < result.Changed.Count && (handed == 0 || handed < budget))
        {
            var (tile, raw, geometry, mesh) = result.Changed[next++];
            tile.Raw = raw;
            if (mesh == null || mesh.GetPolygonCount() == 0)
            {
                if (tile.Region.IsValid) NavigationServer3D.FreeRid(tile.Region);
                tile.Region = default;
                tile.Mesh = null;
                tile.Geometry = null;
                continue;
            }
            if (!tile.Region.IsValid)
            {
                tile.Region = NavigationServer3D.RegionCreate();
                // A large map's tiles take their polygons on worker threads too (see Revision).
                NavigationServer3D.RegionSetUseAsyncIterations(tile.Region, TileCount > SmallBakeTiles);
                NavigationServer3D.RegionSetMap(tile.Region, Map);
            }
            NavigationServer3D.RegionSetTransform(tile.Region, transform);
            NavigationServer3D.RegionSetNavigationMesh(tile.Region, mesh);
            _markerWaits.Add((tile.Region, NavigationServer3D.RegionGetIterationId(tile.Region)));
            tile.Mesh = mesh;
            tile.Geometry = geometry;
            handed += mesh.GetPolygonCount();
        }
        ms += Elapsed(started);
        if (next < result.Changed.Count)
        {
            _handOver = (owner, result, next, ms);
            return;
        }
        _handOver = null;
        _records = result.Records;
        PolygonCount = _tiles.Sum(t => t.Mesh?.GetPolygonCount() ?? 0);
        _appliedRevision++;
        // A small map takes its tiles in the frame they are handed over, so the marker goes with them; a large map's tiles take
        // theirs on worker threads, and the marker follows once they all have (see Revision).
        if (IsSmall) _markerWaits.Clear();
        if (_markerWaits.Count == 0) MoveMarker();
        _liveCheckNow = true;
        result.Islands?.SetRevision(_appliedRevision);
        if (result.Islands != null) result.Islands.ToTiles = transform.AffineInverse();
        _islands = result.Islands;
        LastAppliedFrame = Engine.GetPhysicsFrames();
        LastTilesBaked = result.Baked;
        LastBakeMs = Elapsed(owner.Started);
        LastBakeMainThreadMs = owner.MainMs + ms;
        RoomGroundTriangles = result.RoomGroundTriangles;
        LastRoomObstructions = result.RoomObstructions;
        var fromRoom = Room == null ? "" : $"from the room's data {result.RoomGroundTriangles} ground triangles and {result.RoomObstructions} obstructions in the tiles baked ({PlantsFollowed} copies at other than natural size, {PlantsBurnt} burnt down); ";
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"ROOM_NAVIGATION bake {_appliedRevision}: {PolygonCount} polygons in {LastBakeMs:0.0} ms ({LastTilesBaked} of {TileCount} tiles of {_tileStep:0.##} m baked on worker threads, {result.Changed.Count} handed over: reading {result.ReadMs:0} ms, baking {result.BakeMs:0} ms, seams {result.SeamsMs:0} ms, islands {result.IslandsMs:0} ms; " +
            $"{fromRoom}the main thread {LastBakeMainThreadMs:0.0} ms in all, reading the scene {owner.SnapshotMs:0.0} ms, handing over {ms:0.0} ms; {GC.CollectionCount(2) - owner.Collections} full collections; " +
            $"agent radius {AgentRadiusM:0.000} m, height {AgentHeightM:0.00} m, climb {AgentMaxClimbM:0.000} m; {result.Islands?.Count ?? 0} islands)"));
    }

    private static bool Overlaps(Aabb box, Aabb other) =>
        other.Position.X <= box.End.X && other.End.X >= box.Position.X && other.Position.Z <= box.End.Z && other.End.Z >= box.Position.Z &&
        other.Position.Y <= box.End.Y && other.End.Y >= box.Position.Y;

    private static double Elapsed(long since) => (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;

    // ---- routes ----

    /// <summary>
    /// The walkable route from one point to another, searched now on the calling thread. Reaches is false when the goal is
    /// not on the start's walkable island: the route must end at the goal's own nearest walkable point, and within the planar
    /// tolerance of the goal. (Within the tolerance alone was not enough: across a thin wall the route's end can be a few
    /// centimetres from a goal it can never reach. Lane P review.)
    /// </summary>
    /// <para>
    /// The search is not capped by polygon count (SearchMaxPolygons). maxLengthM, when above zero, stops it at that distance
    /// from the start: a caller that would not walk a longer way round anyway (the Gubble floats instead) need not pay for
    /// searching the rest of the island when the goal is out of reach. A goal on another island is out of reach at once
    /// (NavigationIslands), with no search at all: the route is then the start's point on the mesh.
    /// </para>
    public Route FindRoute(Vector3 from, Vector3 to, float toleranceM, float maxLengthM = 0)
    {
        if (!IsReady || !from.IsFinite() || !to.IsFinite()) return Route.None;
        return Search(from, to, toleranceM, maxLengthM, Revision, _query, _result);
    }

    /// <summary>
    /// The same route, searched on a worker thread: the companion's way of planning (on the magnified island one search could
    /// take 20 ms). The request says which bake it was planned on; TryTake refuses a route planned on an older bake.
    /// </summary>
    public RouteRequest RequestRoute(Vector3 from, Vector3 to, float toleranceM, float maxLengthM = 0)
    {
        if (!IsReady || !from.IsFinite() || !to.IsFinite()) return RouteRequest.Done(this, from, to, Route.None);
        var revision = Revision;
        var query = new NavigationPathQueryParameters3D
        {
            PathfindingAlgorithm = _query.PathfindingAlgorithm, PathPostprocessing = _query.PathPostprocessing, NavigationLayers = _query.NavigationLayers,
            SimplifyPath = false, PathSearchMaxPolygons = SearchMaxPolygons, MetadataFlags = _query.MetadataFlags,
        };
        var result = new NavigationPathQueryResult3D();
        var request = new RouteRequest(this, from, to, revision);
        request.Task = System.Threading.Tasks.Task.Run(() =>
        {
            if (!_serverLock.TryEnterReadLock(1000)) return Route.None;
            try { return _closed ? Route.None : Search(from, to, toleranceM, maxLengthM, revision, query, result); }
            finally { _serverLock.ExitReadLock(); }
        });
        return request;
    }

    /// <summary>A route search in flight on a worker thread, and the bake it was planned on.</summary>
    public sealed class RouteRequest
    {
        private readonly RoomNavigation _navigation;
        internal Task<Route>? Task;
        private readonly Route _done;
        public Vector3 From { get; }
        public Vector3 Goal { get; }
        /// <summary>The bake (RoomNavigation.Revision) the route is planned on.</summary>
        public int Revision { get; }

        internal RouteRequest(RoomNavigation navigation, Vector3 from, Vector3 goal, int revision)
        {
            _navigation = navigation;
            From = from;
            Goal = goal;
            Revision = revision;
            _done = Route.None;
        }

        private RouteRequest(RoomNavigation navigation, Vector3 from, Vector3 goal, Route done) : this(navigation, from, goal, 0) => _done = done;

        internal static RouteRequest Done(RoomNavigation navigation, Vector3 from, Vector3 goal, Route route) => new(navigation, from, goal, route);

        public bool IsDone => Task == null || Task.IsCompleted;
        /// <summary>A newer bake went to the map after this route was planned: it must be planned again, not trusted.</summary>
        public bool Stale => Revision != _navigation.Revision;

        /// <summary>Wait up to this long for the search (main thread: only where the caller has nothing to steer by meanwhile).</summary>
        public bool Wait(double milliseconds) => IsDone || WaitFor(Task!, milliseconds);

        /// <summary>The route, once found and only if it was planned on the bake the map holds now.</summary>
        public bool TryTake(out Route route)
        {
            route = Route.None;
            if (!IsDone || Stale) return false;
            route = Task?.Result ?? _done;
            return true;
        }
    }

    /// <summary>True when a route was planned on the bake the map holds now.</summary>
    public bool IsCurrent(in Route route) => route.Revision == Revision;

    private Route Search(Vector3 from, Vector3 to, float toleranceM, float maxLengthM, int revision, NavigationPathQueryParameters3D query, NavigationPathQueryResult3D result)
    {
        var islands = _liveIslands is { } live && live.Revision == revision ? live : null;
        var island = Closest(islands, to);
        // Another island: out of reach, known without searching everywhere the start can walk (S1 on the magnified island: 10
        // to 36 ms a search).
        if (islands != null)
        {
            var start = Closest(islands, from);
            var a = islands.IslandAt(start);
            var b = islands.IslandAt(island);
            if (a >= 0 && b >= 0 && a != b) return new Route(new[] { start }, false, true, 0, revision);
        }
        query.Map = Map;
        query.StartPosition = from;
        query.TargetPosition = to;
        query.PathSearchMaxDistance = maxLengthM > 0 && float.IsFinite(maxLengthM) ? maxLengthM : 0;
        NavigationServer3D.QueryPath(query, result);
        var points = result.Path;
        if (points.Length == 0) return Route.None;
        var end = points[^1];
        var reaches = Planar(end - to).Length() <= toleranceM && end.DistanceTo(island) <= SameIslandM;
        var length = Planar(points[0] - from).Length();
        for (var i = 1; i < points.Length; i++) length += Planar(points[i] - points[i - 1]).Length();
        return new Route(points, reaches, points.Length <= 2, length, revision);
    }

    /// <summary>
    /// Godot's own path query (MapGetPath) gives up after NavigationPathQueryParameters3D's default 4,096 polygons and returns
    /// a path to the polygon nearest the goal found so far. Today's islands bake to about 1,200 polygons; the garage island
    /// magnified ten times (13 October) bakes to 55,110, and a capped route stopped short of half the land it could walk to
    /// (on a 1.5 m survey from the Gubble's spawn: 189 places reached capped, 357 uncapped), so the Gubble floated straight
    /// at goals it could have walked to. Here the search is unlimited (0): it never covers more than the start's own walkable
    /// part of the map, and a goal on another part is refused before any search (NavigationIslands).
    /// The rest is MapGetPath's: the funnel (optimize), layer 1, no simplification; no metadata, which nothing reads.
    /// </summary>
    public const int SearchMaxPolygons = 0;

    private readonly NavigationPathQueryParameters3D _query = new()
    {
        PathfindingAlgorithm = NavigationPathQueryParameters3D.PathfindingAlgorithmEnum.Astar,
        PathPostprocessing = NavigationPathQueryParameters3D.PathPostProcessing.Corridorfunnel,
        NavigationLayers = 1, SimplifyPath = false, PathSearchMaxPolygons = SearchMaxPolygons,
        MetadataFlags = NavigationPathQueryParameters3D.PathMetadataFlags.None
    };
    private readonly NavigationPathQueryResult3D _result = new();

    /// <summary>
    /// The nearest walkable point to a position (the position itself when the mesh is not ready). Looked up in the tiles of
    /// the bake the map holds (NavigationIslands.ClosestPoint); a point farther than ClosestLimitM from all of them asks the
    /// map itself, which also holds any region a caller added to it.
    /// </summary>
    public Vector3 ClosestPoint(Vector3 point)
    {
        if (!IsReady || !point.IsFinite()) return point;
        var islands = _liveIslands is { } live && live.Revision == Revision ? live : null;
        return Closest(islands, point);
    }

    private const float ClosestLimitM = 2f;

    private Vector3 Closest(NavigationIslands? islands, Vector3 point) =>
        islands?.ClosestPoint(point, ClosestLimitM) ?? NavigationServer3D.MapGetClosestPoint(Map, point);

    /// <summary>The walkable island a point's nearest walkable point lies on (-1 when unknown), for measurements and tests.</summary>
    public int IslandAt(Vector3 point) => IsReady && _liveIslands is { } islands && islands.Revision == Revision ? islands.IslandAt(ClosestPoint(point)) : -1;

    /// <summary>How many separate walkable islands the bake the map holds has.</summary>
    public int IslandCount => _liveIslands?.Count ?? 0;

    private static Vector3 Planar(Vector3 value) => new(value.X, 0, value.Z);

    private static float Cells(float metres, float cell, bool up) =>
        (up ? Mathf.Ceil(metres / cell - 0.001f) : Mathf.Floor(metres / cell + 0.001f)) * cell;
}
