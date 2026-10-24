using Godot;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GarageGame.Native.Room;

/// <summary>What a plant set's copies are (contracts/README.md, "Plant copies"); the values are the table header's kind codes.</summary>
public enum PlantKind { Plant = 0, Tree = 1, Stone = 2 }

/// <summary>One crown cap of a tree row: a collar from its rim ellipse (at RimM) up to a flat perch ellipse (at TopM), room metres.</summary>
public readonly record struct TreeCap(float X, float Z, float RimHalfXM, float RimHalfZM, float RimM, float TopM, float TopHalfXM, float TopHalfZM);

/// <summary>
/// A tree row's climbing collision as pipeline/landscape/export/trees.py fits it to the ground: a trunk from the copy's base (BaseY)
/// to TrunkTopM, tapering from TrunkRadiusM to PoleRadiusM; a hidden pole of PoleRadiusM from there to PoleTopM; and its caps.
/// </summary>
public sealed record TreeShape(float BaseY, float AxisX, float AxisZ, float TrunkRadiusM, float PoleRadiusM, float TrunkTopM, float PoleTopM, TreeCap[] Caps);

/// <summary>
/// One plant set of the room manifest's plant_copies: one mesh (with lighter ones for distance) drawn many times, one table of
/// fixed-width little-endian rows sorted by 8 m tile behind a tile index. The table's verified bytes are kept and read on demand,
/// so 350,000 rows cost their 20 MB once. Row n is the entity plant:&lt;Name&gt;-n.
/// </summary>
public sealed class PlantSet
{
    public const int RowBytesBase = 56, TreeTailBytes = 28, CapBytes = 32;

    public string Name { get; init; } = "";
    public string Prototype { get; init; } = "";
    public PlantKind Kind { get; init; }
    /// <summary>What grows there (untrusted, a token): grass, daisies, broadleaf, rock...</summary>
    public string Species { get; init; } = "";
    /// <summary>none, tree_climb or solid.</summary>
    public string Collides { get; init; } = "";
    public string FuelClass { get; init; } = "";
    public bool Seeds { get; init; }
    /// <summary>The meshes by detail, finest first (the set's mesh, then its lods): full paths, keys of RoomData.VerifiedMeshes.</summary>
    public IReadOnlyList<string> MeshPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<int> TriangleCounts { get; init; } = Array.Empty<int>();
    /// <summary>The table's path inside the room, as the manifest names it.</summary>
    public string TablePath { get; init; } = "";
    public int CapSlots { get; init; }
    public int Count { get; init; }
    public int RowBytes { get; init; }
    /// <summary>The full-detail mesh's bounds in its own frame (pivot at the bottom centre), from its POSITION accessors.</summary>
    public Aabb MeshBounds { get; init; }
    public float MeshHeightM => MeshBounds.End.Y;
    /// <summary>A solid set's full-detail mesh points, each once: the convex hull of them under a row's transform is that copy's collision.</summary>
    public IReadOnlyList<Vector3> SolidPoints { get; init; } = Array.Empty<Vector3>();
    public bool IsTree => Kind == PlantKind.Tree;

    internal byte[] Data { get; init; } = Array.Empty<byte>();
    internal int RowsAt { get; init; }
    internal int[] TileX { get; init; } = Array.Empty<int>();
    internal int[] TileZ { get; init; } = Array.Empty<int>();
    internal int[] TileFirst { get; init; } = Array.Empty<int>();
    internal int[] TileRows { get; init; } = Array.Empty<int>();

    /// <summary>Entries in the tile index: the 8 m tiles holding at least one copy, sorted by tx, then tz.</summary>
    public int TileCount => TileX.Length;

    public (int Tx, int Tz, int First, int Count) Tile(int index) => (TileX[index], TileZ[index], TileFirst[index], TileRows[index]);

    /// <summary>The tile index entry of tile (tx, tz), or -1 when no copy of this set stands there.</summary>
    public int FindTile(int tx, int tz)
    {
        int low = 0, high = TileX.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) >> 1;
            var order = TileX[middle] != tx ? TileX[middle].CompareTo(tx) : TileZ[middle].CompareTo(tz);
            if (order == 0) return middle;
            if (order < 0) low = middle + 1;
            else high = middle - 1;
        }
        return -1;
    }

    /// <summary>The tile index entry holding a row (rows are stored tile by tile).</summary>
    public int TileOfRow(int row)
    {
        int low = 0, high = TileFirst.Length - 1;
        while (low < high)
        {
            var middle = (low + high + 1) >> 1;
            if (TileFirst[middle] <= row) low = middle;
            else high = middle - 1;
        }
        return low;
    }

    private int At(int row, int field) => RowsAt + row * RowBytes + field * 4;
    private float F(int row, int field) => BinaryPrimitives.ReadSingleLittleEndian(Data.AsSpan(At(row, field)));
    private uint U(int row, int field) => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(At(row, field)));

    /// <summary>The copy's pivot (bottom centre) in room metres.</summary>
    public Vector3 Position(int row) => new(F(row, 0), F(row, 1), F(row, 2));
    /// <summary>About +Y, Godot's rotation.y: 90 turns +X toward -Z.</summary>
    public float YawDeg(int row) => F(row, 3);
    public Vector3 Scale(int row) => new(F(row, 4), F(row, 5), F(row, 6));
    public Color Tint(int row) => new(F(row, 7), F(row, 8), F(row, 9));
    public float TopM(int row) => F(row, 10);
    public uint Flags(int row) => U(row, 11);
    /// <summary>Bit 0: its base is under water, so fire does not reach it there.</summary>
    public bool Submerged(int row) => (Flags(row) & 1) != 0;
    /// <summary>Its grove or patch (1 to the room's groups), or 0.</summary>
    public int Group(int row) => (int)U(row, 12);
    public uint VariantSeed(int row) => U(row, 13);

    /// <summary>The copy's basis: yaw about +Y, times its scale. The copy is position + basis × mesh.</summary>
    public Basis Basis(int row) => new Basis(Vector3.Up, Mathf.DegToRad(YawDeg(row))) * Godot.Basis.FromScale(Scale(row));

    public Transform3D Transform(int row) => new(Basis(row), Position(row));

    /// <summary>The tree row's climbing collision (trees only).</summary>
    public TreeShape Tree(int row)
    {
        if (!IsTree) throw new InvalidOperationException($"plant set {Name} holds no trees");
        var count = (int)U(row, 20);
        var caps = new TreeCap[Math.Clamp(count, 0, CapSlots)];
        for (var k = 0; k < caps.Length; k++)
        {
            var c = 21 + 8 * k;
            caps[k] = new TreeCap(F(row, c), F(row, c + 1), F(row, c + 2), F(row, c + 3), F(row, c + 4), F(row, c + 5), F(row, c + 6), F(row, c + 7));
        }
        return new TreeShape(F(row, 1), F(row, 14), F(row, 15), F(row, 16), F(row, 17), F(row, 18), F(row, 19), caps);
    }
}

/// <summary>
/// The room manifest's plant_copies (the 10x world, contract round S2): plants, trees and stones drawn as copies of one mesh per
/// set. Read and checked by RoomData.Load (fail closed); drawn and collided by PlantCopiesView. A copy is the entity
/// plant:&lt;set&gt;-&lt;row&gt;, one spelling each; no copy is a scene node or a room entity.
/// </summary>
public sealed class PlantCopies
{
    public const int TileM = 8;
    public const int MaxSets = 64, MaxRows = 16_777_216, MaxTiles = 65_536, MaxCapSlots = 8, MaxGroups = 1_048_576;
    /// <summary>Copies per square metre of the bounds' footprint, all sets together.</summary>
    public const int DensityPerM2 = 256;
    private static readonly Regex Id = new(@"^plant:([a-z][a-z0-9_]{0,31})-(0|[1-9][0-9]{0,7})\z", RegexOptions.Compiled);

    /// <summary>Groves and patches, numbered 1 to Groups across every set.</summary>
    public int Groups { get; init; }
    public IReadOnlyList<PlantSet> Sets { get; init; } = Array.Empty<PlantSet>();
    private readonly Dictionary<string, PlantSet> _byName = new(StringComparer.Ordinal);

    internal PlantCopies(int groups, IReadOnlyList<PlantSet> sets)
    {
        Groups = groups;
        Sets = sets;
        foreach (var set in sets) _byName[set.Name] = set;
    }

    public int TotalCount
    {
        get
        {
            var total = 0;
            foreach (var set in Sets) total += set.Count;
            return total;
        }
    }

    public PlantSet? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>The copy's entity id: plant:&lt;set&gt;-&lt;row&gt;, the row in decimal without leading zeros.</summary>
    public static string IdOf(string set, int row) => "plant:" + set + "-" + row.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The set and row an entity id names, when it is a copy of this room: exactly one spelling (no leading zeros, no sign), a
    /// set the room has and a row its table holds. Anything else, including a well-formed id past the table's end, is not found.
    /// </summary>
    public bool TryResolve(string entityId, out PlantSet set, out int row)
    {
        set = null!;
        row = -1;
        var match = Id.Match(entityId ?? "");
        if (!match.Success || !_byName.TryGetValue(match.Groups[1].Value, out var found)) return false;
        if (!int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number >= found.Count) return false;
        set = found;
        row = number;
        return true;
    }

    /// <summary>
    /// Every copy whose pivot (its root) lies within radius of (x, z) horizontally, set by set: only the tiles round the circle are
    /// read (a copy's row sits in the tile of its pivot), so a cast or a fire reads a few tiles, never the island.
    /// </summary>
    public IEnumerable<(PlantSet Set, int Row)> RowsNear(float x, float z, float radius)
    {
        var (tx0, tz0) = TileOf(x - radius, z - radius);
        var (tx1, tz1) = TileOf(x + radius, z + radius);
        foreach (var set in Sets)
            for (var tx = tx0; tx <= tx1; tx++)
            for (var tz = tz0; tz <= tz1; tz++)
            {
                var tile = set.FindTile(tx, tz);
                if (tile < 0) continue;
                var (_, _, first, count) = set.Tile(tile);
                for (var row = first; row < first + count; row++)
                {
                    var at = set.Position(row);
                    float dx = at.X - x, dz = at.Z - z;
                    if (dx * dx + dz * dz <= radius * radius) yield return (set, row);
                }
            }
    }

    /// <summary>The 8 m tile of a point, from its float32 coordinates (exact: 8 is a power of two).</summary>
    public static (int Tx, int Tz) TileOf(float x, float z) => ((int)Math.Floor(x / (double)TileM), (int)Math.Floor(z / (double)TileM));
}
