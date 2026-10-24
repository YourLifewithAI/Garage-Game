using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace GarageGame.Native.Room;

/// <summary>
/// One tiled shell part of the room manifest's terrain_tiles: its GLB holds one node per 16 m tile and level of detail, named
/// tile_&lt;tx&gt;_&lt;tz&gt;_lod&lt;level&gt;, each a root with a mesh and no transform, in room metres inside its tile's square.
/// </summary>
public sealed class TiledPart
{
    public string PartId { get; init; } = "";
    /// <summary>The GLB's full path, a key of RoomData.VerifiedMeshes.</summary>
    public string MeshPath { get; init; } = "";
    /// <summary>The part's tiles, sorted by tx then tz; each tile's mesh index in the GLB by level, finest first.</summary>
    public IReadOnlyList<(int Tx, int Tz, int[] MeshByLevel)> Tiles { get; init; } = Array.Empty<(int, int, int[])>();
    /// <summary>
    /// Each tile's level-0 ground as read from the file at load (a colliding or water part only): its float positions and its
    /// triangles' corners in Godot's winding, kept so the file's bytes need not be (S12b: about half their size on the 10x island).
    /// </summary>
    internal (float[] Positions, ushort[]? Small, int[]? Large)[]? Ground { get; init; }

    /// <summary>
    /// The ground of one tile: its level-0 triangles as a face list in Godot's winding, from the file's float positions. Level 0
    /// is the walkable ground: a colliding part collides through it alone, and navigation bakes it. Pure C#: any thread.
    /// </summary>
    public Vector3[] GroundFaces(int tileIndex) =>
        Faces((Ground ?? throw new InvalidOperationException($"{PartId} neither collides nor holds water: its ground was not kept"))[tileIndex]);

    /// <summary>
    /// Each tile's coarse ground (level 1, about 5 cm from level 0 on the 10x garage; level 0 when the land has one level) for a
    /// drawn colliding part: the far ground sight and aim see where the walkable ground is not built (TerrainCollision). Bodies
    /// never touch it. Null for a part that is not drawn or does not collide.
    /// </summary>
    internal (float[] Positions, ushort[]? Small, int[]? Large)[]? Coarse { get; init; }

    /// <summary>The coarse ground of one tile as a face list in Godot's winding (see Coarse). Pure C#: any thread.</summary>
    public Vector3[] CoarseFaces(int tileIndex) =>
        Faces((Coarse ?? throw new InvalidOperationException($"{PartId} has no far ground: it is not drawn or does not collide"))[tileIndex]);

    public bool HasCoarse => Coarse != null;

    private static Vector3[] Faces((float[] Positions, ushort[]? Small, int[]? Large) ground)
    {
        var (positions, small, large) = ground;
        var count = small?.Length ?? large!.Length;
        var faces = new Vector3[count];
        for (var i = 0; i < count; i++)
        {
            var corner = small != null ? small[i] : large![i];
            faces[i] = new Vector3(positions[corner * 3], positions[corner * 3 + 1], positions[corner * 3 + 2]);
        }
        return faces;
    }

    /// <summary>Triangles in a tile's level-0 ground (0 when its ground was not kept).</summary>
    public int GroundTriangles(int tileIndex) => Ground is { } ground ? (ground[tileIndex].Small?.Length ?? ground[tileIndex].Large!.Length) / 3 : 0;

    /// <summary>The level of a tile node named by the contract (tile_&lt;tx&gt;_&lt;tz&gt;_lod&lt;level&gt;), or null.</summary>
    public static (int Tx, int Tz, int Level)? ParseNodeName(string name)
    {
        var match = TerrainTiles.NodeName.Match(name);
        if (!match.Success) return null;
        return (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// The room manifest's terrain_tiles (the 10x world, contract round S2): the land cut into 16 m tiles with levels of distance
/// detail, finest first. The game draws one level per tile, chosen by distance, and collides through level 0 only. Read and
/// checked by RoomData.Load, fail closed, exactly as contracts/validate.py checks it.
/// </summary>
public sealed class TerrainTiles
{
    public const int TileM = 16;
    public const int MaxLevels = 4, MaxParts = 128, MaxTilesPerPart = 16_384;
    public const double BoundsToleranceM = 1e-3;
    internal static readonly Regex NodeName = new(@"^tile_(0|-?[1-9][0-9]{0,4})_(0|-?[1-9][0-9]{0,4})_lod([0-9])\z", RegexOptions.Compiled);

    /// <summary>Each level's nominal grid spacing in metres, finest first.</summary>
    public IReadOnlyList<float> LevelGridM { get; init; } = Array.Empty<float>();
    public IReadOnlyList<TiledPart> Parts { get; init; } = Array.Empty<TiledPart>();
    public int LevelCount => LevelGridM.Count;

    public TiledPart? Part(string partId) => Parts.FirstOrDefault(p => p.PartId == partId);

    /// <summary>The 16 m tile of a point.</summary>
    public static (int Tx, int Tz) TileOf(float x, float z) => ((int)Math.Floor(x / (double)TileM), (int)Math.Floor(z / (double)TileM));

    /// <summary>
    /// The contract's rules for one tiled part's GLB (contracts/validate.py _check_tile_nodes): every node a root of its scene,
    /// named for its tile and level, a mesh with no transform and no children, inside its tile's square, every tile at every
    /// level, as many tiles as the manifest says. Returns the tiles in order, or throws RoomLoadException naming the rule.
    /// </summary>
    internal static List<(int Tx, int Tz, int[] MeshByLevel)> CheckNodes(GlbReader glb, int levels, int tiles, string where)
    {
        var nodes = glb.Nodes;
        if (nodes.Count == 0 || !glb.Roots.OrderBy(r => r).SequenceEqual(Enumerable.Range(0, nodes.Count)))
            throw new RoomLoadException($"{where}: every node of a tiled GLB is a root of its scene");
        var found = new SortedDictionary<(int, int), int[]>();
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            var parsed = node.Name is { } name ? TiledPart.ParseNodeName(name) : null;
            if (parsed is not { } tile) throw new RoomLoadException($"{where}: node {index} ('{node.Name}') is not named tile_<tx>_<tz>_lod<level>");
            if (tile.Level >= levels) throw new RoomLoadException($"{where}: node {node.Name} names level {tile.Level}; the room has {levels}");
            if (node.HasChildren || node.HasTransform || node.Mesh < 0) throw new RoomLoadException($"{where}: node {node.Name} must hold a mesh with no transform and no children");
            if (!found.TryGetValue((tile.Tx, tile.Tz), out var byLevel)) found[(tile.Tx, tile.Tz)] = byLevel = Enumerable.Repeat(-1, levels).ToArray();
            if (byLevel[tile.Level] >= 0) throw new RoomLoadException($"{where}: node {node.Name} appears twice");
            byLevel[tile.Level] = node.Mesh;
            if (glb.MeshExtent(node.Mesh) is not { } extent) throw new RoomLoadException($"{where}: node {node.Name} has no mesh with POSITION bounds");
            double x = tile.Tx * (double)TileM, z = tile.Tz * (double)TileM;
            if (extent.Low.X < x - BoundsToleranceM || extent.High.X > x + TileM + BoundsToleranceM ||
                extent.Low.Z < z - BoundsToleranceM || extent.High.Z > z + TileM + BoundsToleranceM)
                throw new RoomLoadException($"{where}: node {node.Name} reaches outside its tile's square");
        }
        foreach (var ((tx, tz), byLevel) in found)
            for (var level = 0; level < levels; level++)
                if (byLevel[level] < 0) throw new RoomLoadException($"{where}: tile ({tx}, {tz}) has no level {level}; every tile has every level");
        if (found.Count != tiles) throw new RoomLoadException($"{where}: its GLB holds {found.Count} tiles; the manifest says {tiles}");
        return found.Select(pair => (pair.Key.Item1, pair.Key.Item2, pair.Value)).ToList();
    }
}
