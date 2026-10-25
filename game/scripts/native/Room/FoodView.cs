using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace GarageGame.Native.Room;

/// <summary>
/// The food on the island's plants in the scene (P-Food; D1's look in docs/runs/RUN-2-PLAYTEST-ROUND-2.md): an apple a red sphere
/// AppleM across with a stalk (a windfall's stands up from it, a canopy apple's reaches up to its crown), berries a cluster of six
/// BeadM dark blue beads. Unshaded and casting no shadow, as the Fractal's star is drawn on the same budget: at most MaxDrawn units,
/// those nearest the focus within DrawM, in three MultiMeshes (apples, stalks, berries; three draws), filled again only when the
/// kernel says the food changed, the focus moved MovedM, or AskEveryS passed (food grows back by the island's clock). A picked spot
/// is simply not among what the kernel hands over, so it shows nothing until it grows back. Nothing here is authoritative, collides
/// or is seen by sight or the map: the command host's FoodSpotsNear is the source (Source), and the plant copies' view, whose child
/// this is, gives the focus (the camera, else an avatar, else the player's spawn). Since P-Inventory-2 the units dropped loose are drawn
/// with them (decision 118): an apple or berries as on a plant, a seed a small brown bead, a log a short brown rod lying down, a stone a
/// grey pebble, each pool hidden (no draw) while it holds none.
/// </summary>
public partial class FoodView : Node3D
{
    public const string ViewName = "Food";
    /// <summary>The most units drawn at once, the nearest the focus (D1: "at most 64 drawn").</summary>
    public const int MaxDrawn = 64;
    /// <summary>Food is drawn this far from the focus: a 1 cm apple is a few pixels there, read from a band away.</summary>
    public const float DrawM = 6f;
    /// <summary>The view asks again when its focus moved this far, and every AskEveryS whatever happens.</summary>
    public const float MovedM = 0.25f;
    public const double AskEveryS = 1.0;
    /// <summary>The apple's size and its shortest stalk; the bead's and the cluster's (the kernel's FoodLaws reads these).</summary>
    public const float AppleM = 0.01f, StalkM = 0.004f, BeadM = 0.003f, ClusterM = 0.01f;
    public const float StalkRadiusM = 0.0006f;
    public static readonly Color AppleColor = new("c8302a");
    public static readonly Color StalkColor = new("5a3b1d");
    public static readonly Color BerryColor = new("1e2a68");
    /// <summary>The loose units' looks (P-Inventory-2): a seed, a log, a stone (sizes in metres; the kernel's reach boxes are FoodLaws.LooseM).</summary>
    public const float SeedM = 0.004f, LogLengthM = 0.012f, LogRadiusM = 0.0025f, StoneM = 0.008f;
    public static readonly Color SeedColor = new("6b4a2b");
    public static readonly Color LogColor = new("7a5532");
    public static readonly Color StoneColor = new("8a8a86");

    public enum Look { Apple, Berries, Seed, Log, Stone }

    /// <summary>One unit of food as drawn: its bottom centre in room metres, its look, and (an apple) its stalk's length up to its crown.</summary>
    public readonly record struct Spot(Vector3 PositionM, Look Look, float HangM);

    /// <summary>The food round a point within a radius (the command host's FoodSpotsNear); null: nothing is drawn.</summary>
    public Func<Vector3, float, IReadOnlyList<Spot>>? Source { get; set; }
    /// <summary>Where the food is drawn round (the plant copies' view sets it); null: the current camera.</summary>
    public Func<Vector3>? Focus { get; set; }

    public MultiMeshInstance3D Apples { get; private set; } = null!;
    public MultiMeshInstance3D Stalks { get; private set; } = null!;
    public MultiMeshInstance3D Berries { get; private set; } = null!;
    public MultiMeshInstance3D Seeds { get; private set; } = null!;
    public MultiMeshInstance3D Logs { get; private set; } = null!;
    public MultiMeshInstance3D Stones { get; private set; } = null!;
    /// <summary>What is drawn now, nearest the focus first.</summary>
    public IReadOnlyList<Spot> Drawn => _drawn;
    /// <summary>How many units the source offered at the last ask, the asks so far, the fills that changed the buffers, and their time.</summary>
    public int Offered { get; private set; }
    public int Asks { get; private set; }
    public int Fills { get; private set; }
    public double LastAskMs { get; private set; }
    public double LongestAskMs { get; private set; }
    /// <summary>Triangles one apple (its body and stalk) and one cluster of berries take.</summary>
    public int AppleTriangles { get; private set; }
    public int BerryTriangles { get; private set; }

    private readonly List<Spot> _drawn = new();
    private bool _dirty = true;
    private Vector3 _askedAt = new(float.NaN, 0, float.NaN);
    private double _sinceAsked;

    public static FoodView Create()
    {
        var view = new FoodView { Name = ViewName };
        var apple = new SphereMesh { Radius = AppleM * 0.5f, Height = AppleM, RadialSegments = 8, Rings = 4 };
        var stalk = new CylinderMesh { TopRadius = StalkRadiusM * 0.8f, BottomRadius = StalkRadiusM, Height = 1, RadialSegments = 4, Rings = 0, CapTop = false, CapBottom = false };
        var berries = Cluster();
        view.Apples = view.Pool("Apples", apple, AppleColor);
        view.Stalks = view.Pool("Stalks", stalk, StalkColor);
        view.Berries = view.Pool("Berries", berries, BerryColor);
        view.Seeds = view.Pool("Seeds", new SphereMesh { Radius = SeedM * 0.5f, Height = SeedM * 0.7f, RadialSegments = 6, Rings = 3 }, SeedColor);
        view.Logs = view.Pool("Logs", new CylinderMesh { TopRadius = LogRadiusM, BottomRadius = LogRadiusM, Height = LogLengthM, RadialSegments = 6, Rings = 0 }, LogColor);
        view.Stones = view.Pool("Stones", new SphereMesh { Radius = StoneM * 0.5f, Height = StoneM * 0.7f, RadialSegments = 6, Rings = 3 }, StoneColor);
        view.AppleTriangles = Triangles(apple) + Triangles(stalk);
        view.BerryTriangles = Triangles(berries);
        return view;
    }

    /// <summary>Something the food depends on changed (a pick, a cast, a burn, a load): the view asks again on its next frame.</summary>
    public void Changed() => _dirty = true;

    public override void _Process(double delta)
    {
        _sinceAsked += delta;
        if (Source == null) return;
        var focus = Focus?.Invoke() ?? GetViewport()?.GetCamera3D()?.GlobalPosition;
        if (focus is not { } at || !at.IsFinite()) return;
        if (!_dirty && _sinceAsked < AskEveryS && at.DistanceSquaredTo(_askedAt) < MovedM * MovedM) return;
        Refill(at);
    }

    /// <summary>Asks the source for the food round focus now and draws the nearest MaxDrawn (the buffers change only when what is drawn does).</summary>
    public void Refill(Vector3 focus)
    {
        var clock = Stopwatch.StartNew();
        _dirty = false;
        _sinceAsked = 0;
        _askedAt = focus;
        var offered = Source?.Invoke(focus, DrawM) ?? Array.Empty<Spot>();
        Offered = offered.Count;
        var nearest = offered.Where(s => s.PositionM.DistanceSquaredTo(focus) <= DrawM * DrawM).OrderBy(s => s.PositionM.DistanceSquaredTo(focus)).Take(MaxDrawn).ToList();
        if (!nearest.SequenceEqual(_drawn))
        {
            _drawn.Clear();
            _drawn.AddRange(nearest);
            Fill();
            Fills++;
        }
        Asks++;
        LastAskMs = clock.Elapsed.TotalMilliseconds;
        LongestAskMs = Math.Max(LongestAskMs, LastAskMs);
    }

    private void Fill()
    {
        int apples = 0, berries = 0, seeds = 0, logs = 0, stones = 0;
        foreach (var spot in _drawn)
        {
            if (spot.Look == Look.Seed) Seeds.Multimesh.SetInstanceTransform(seeds++, new Transform3D(Basis.Identity, spot.PositionM + Vector3.Up * (SeedM * 0.35f)));
            else if (spot.Look == Look.Stone) Stones.Multimesh.SetInstanceTransform(stones++, new Transform3D(Basis.Identity, spot.PositionM + Vector3.Up * (StoneM * 0.35f)));
            // A log lies on its side: the cylinder turned a quarter about Z.
            else if (spot.Look == Look.Log) Logs.Multimesh.SetInstanceTransform(logs++, new Transform3D(new Basis(Vector3.Back, Mathf.Pi / 2), spot.PositionM + Vector3.Up * LogRadiusM));
            else if (spot.Look == Look.Apple)
            {
                Apples.Multimesh.SetInstanceTransform(apples, new Transform3D(Basis.Identity, spot.PositionM + Vector3.Up * (AppleM * 0.5f)));
                // The stalk rises from the apple's top to its crown (a windfall's stands StalkM up from it), the unit cylinder stretched to its length.
                var length = Math.Max(StalkM, spot.HangM);
                Stalks.Multimesh.SetInstanceTransform(apples, new Transform3D(Basis.FromScale(new Vector3(1, length, 1)), spot.PositionM + Vector3.Up * (AppleM * 0.9f + length * 0.5f)));
                apples++;
            }
            else Berries.Multimesh.SetInstanceTransform(berries++, new Transform3D(Basis.Identity, spot.PositionM));
        }
        Apples.Multimesh.VisibleInstanceCount = apples;
        Stalks.Multimesh.VisibleInstanceCount = apples;
        Berries.Multimesh.VisibleInstanceCount = berries;
        Apples.Visible = Stalks.Visible = apples > 0;
        Berries.Visible = berries > 0;
        Seeds.Multimesh.VisibleInstanceCount = seeds;
        Logs.Multimesh.VisibleInstanceCount = logs;
        Stones.Multimesh.VisibleInstanceCount = stones;
        Seeds.Visible = seeds > 0;
        Logs.Visible = logs > 0;
        Stones.Visible = stones > 0;
    }

    /// <summary>One pool: a MultiMesh of MaxDrawn instances (as many shown as are drawn), unshaded in its colour, casting no shadow.</summary>
    private MultiMeshInstance3D Pool(string name, Mesh mesh, Color color)
    {
        var material = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = color };
        var multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = MaxDrawn, VisibleInstanceCount = 0 };
        var pool = new MultiMeshInstance3D
        {
            Name = name, Multimesh = multimesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            Visible = false,
        };
        AddChild(pool);
        return pool;
    }

    /// <summary>Six beads in a little heap within a ClusterM box from its bottom centre: three round the bottom, two over them, one on top.</summary>
    private static ArrayMesh Cluster()
    {
        var bead = new SphereMesh { Radius = BeadM * 0.5f, Height = BeadM, RadialSegments = 6, Rings = 3 };
        var r = BeadM * 0.5f;
        var places = new List<Vector3>();
        for (var i = 0; i < 3; i++) places.Add(new Vector3(MathF.Cos(i * Mathf.Tau / 3) * 0.0022f, r, MathF.Sin(i * Mathf.Tau / 3) * 0.0022f));
        for (var i = 0; i < 2; i++) places.Add(new Vector3(MathF.Cos(Mathf.Pi / 3 + i * Mathf.Pi) * 0.0012f, r + 0.0026f, MathF.Sin(Mathf.Pi / 3 + i * Mathf.Pi) * 0.0012f));
        places.Add(new Vector3(0, r + 0.0052f, 0));
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var at in places) tool.AppendFrom(bead, 0, new Transform3D(Basis.Identity, at));
        return tool.Commit();
    }

    private static int Triangles(Mesh mesh)
    {
        var arrays = mesh.SurfaceGetArrays(0);
        var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        return (indices.Length > 0 ? indices.Length : arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length) / 3;
    }
}
