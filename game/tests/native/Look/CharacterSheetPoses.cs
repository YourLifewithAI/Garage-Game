using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GarageGame.Native;
using GarageGame.Native.Look;

namespace GarageGame.Tests.Look;

/// <summary>
/// Where a character sheet tile stands its character: the feet, the ground's normal, the water's surface, the wall it climbs and
/// the spot a cast aims at, if any (world space).
/// </summary>
public sealed record CharacterSheetStage(Vector3 FeetM, Vector3 GroundNormal, float? WaterLevelM, Plane? Wall, Vector3? CastSpotM = null);

/// <summary>What a pose says after a tick: it declined (the tile shows the rest pose), it is still on its way, or the tile is ready to take.</summary>
public enum CharacterSheetPoseStatus
{
    Rest = 0,
    Posing = 1,
    Ready = 2,
}

/// <summary>
/// What a pose sees: the tile it poses (CharacterSheetPoses.Tiles), the node standing in for the capsule (its origin is the feet,
/// facing -Z), the character's visual root under it, the character's folder and motion kind ("float", "hop", "waddle", "stride"),
/// the stage, the room it stands in and the sheet's camera. The harness calls the pose once per physics tick (the game's tick, at
/// which a body's pose stack runs), with <see cref="Tick"/> counting from 0, until the pose says the tile is ready.
/// </summary>
public sealed class CharacterSheetPoseContext
{
    public CharacterSheetPoseContext(string tileId, Node3D holder, Node3D visual, string folder, string motionKind, CharacterSheetStage stage,
        RoomWorld world, Camera3D camera, float deltaS)
    {
        TileId = tileId;
        Holder = holder;
        Visual = visual;
        Folder = folder;
        MotionKind = motionKind;
        Stage = stage;
        World = world;
        Camera = camera;
        DeltaS = deltaS;
    }

    public string TileId { get; }
    public Node3D Holder { get; }
    public Node3D Visual { get; }
    public string Folder { get; }
    public string MotionKind { get; }
    public CharacterSheetStage Stage { get; }
    public RoomWorld World { get; }
    public Camera3D Camera { get; }
    /// <summary>The physics step, seconds.</summary>
    public float DeltaS { get; }
    /// <summary>Physics ticks since the tile began, 0 on the first.</summary>
    public int Tick { get; internal set; }
    /// <summary>Seconds into the tile at the end of this tick.</summary>
    public double Seconds => (Tick + 1) * (double)DeltaS;
    /// <summary>The pose's word on the moment it chose (added to the tile's label), or empty.</summary>
    public string Note { get; set; } = "";
    /// <summary>
    /// The pose's own verdict, set when it says Ready: whether the body did what the tile's label names (the feet found the slope,
    /// the tumble is flopping, the arm swung for the cast); null for no verdict. A false verdict is said in the label.
    /// </summary>
    public bool? Shows { get; set; }
    /// <summary>Why <see cref="Shows"/> is false (empty otherwise).</summary>
    public string Missing { get; set; } = "";
}

/// <summary>What <see cref="CharacterSheetPoses.BuildBody"/> is asked for: a character's folder, the tile it will stand in, and the room.</summary>
public sealed record CharacterSheetBuild(string Folder, string TileId, RoomWorld World);

/// <summary>A body built for a tile: its visual root and what its look did (the harness applies no look of its own to it).</summary>
public sealed record CharacterSheetBody(Node3D Root, CharacterLookResult Look);

/// <summary>
/// The character sheet's pose hook (tools/look/character_sheet.ps1). Any public static method named RegisterCharacterSheetPoses()
/// taking nothing, on any type in the game's assembly, is called once before the sheets render, and calls Register for the tiles it
/// poses and may set BuildBody. A tile with no pose (or whose pose declines) shows the rest pose and says so in its label.
/// </summary>
public static class CharacterSheetPoses
{
    public delegate CharacterSheetPoseStatus Pose(CharacterSheetPoseContext context);

    /// <summary>The nine tiles: id and label. The last is floating and casting for a floating character and a close look (the face) for the others.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> Tiles = new[]
    {
        ("idle", "idle"), ("walk", "walk"), ("run", "run"), ("hop_landing", "a hop landing"), ("slope_30", "a 30° slope"),
        ("wading", "wading"), ("climbing", "climbing"), ("tumble", "a tumble, mid-flop"), ("ninth", "floating and casting / a close look"),
    };

    /// <summary>The glow a pose may start for a cast at the stage's spot; the harness stops it when the tile is taken.</summary>
    public const string CastGlowId = "character_sheet_cast";

    /// <summary>The longest a tile is ticked before it is taken as it stands.</summary>
    public const double MaxTileSeconds = 8;

    private static readonly Dictionary<string, Pose> Registry = new(StringComparer.Ordinal);
    private static bool _discovered;

    /// <summary>
    /// Builds a character's body for one tile (a fresh one per tile, so no tile inherits another's motion), or null to load the
    /// bare GLB, to which the harness applies CharacterLook itself.
    /// </summary>
    public static Func<CharacterSheetBuild, CharacterSheetBody?>? BuildBody { get; set; }

    public static void Register(string tileId, Pose pose) => Registry[tileId] = pose;

    public static bool Has(string tileId) => Registry.ContainsKey(tileId);

    /// <summary>Pose the character for one tick of its tile; Rest when no pose is registered or the pose declined.</summary>
    public static CharacterSheetPoseStatus TryPose(CharacterSheetPoseContext context) =>
        Registry.TryGetValue(context.TileId, out var pose) ? pose(context) : CharacterSheetPoseStatus.Rest;

    /// <summary>Call every RegisterCharacterSheetPoses() in the assembly, once. Returns the types that registered.</summary>
    public static IReadOnlyList<string> Discover()
    {
        if (_discovered) return Array.Empty<string>();
        _discovered = true;
        var found = new List<string>();
        foreach (var type in typeof(CharacterSheetPoses).Assembly.GetTypes())
        {
            var method = type.GetMethod("RegisterCharacterSheetPoses", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
            if (method == null) continue;
            method.Invoke(null, null);
            found.Add(type.FullName ?? type.Name);
        }
        return found.OrderBy(n => n, StringComparer.Ordinal).ToList();
    }
}
