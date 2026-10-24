using Godot;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GarageGame.Native.Room;

/// <summary>
/// The 10x world's two manifest blocks (contract round S2; packet S6): plant_copies and terrain_tiles, read and checked at load,
/// fail closed, with the rules contracts/validate.py applies (check_plant_copies, read_plant_table, check_terrain_tiles), because
/// a room in the player's folder never passes through the Python validator. The tables are read and checked on worker threads,
/// one set each; Load is safe to call off the main thread as a whole (LoadAsync).
/// </summary>
public sealed partial class RoomData
{
    /// <summary>The plants, trees and stones drawn as copies (plant_copies), or null: a room without them has none.</summary>
    public PlantCopies? Copies { get; private init; }
    /// <summary>The land cut into tiles with distance detail (terrain_tiles), or null: every mesh part is drawn whole.</summary>
    public TerrainTiles? Terrain { get; private init; }

    /// <summary>Load on a worker thread: reading, hashing and checking a 10x island's tables never holds up a frame.</summary>
    public static Task<RoomData> LoadAsync(string directory) => Task.Run(() => Load(directory));

    private const string PlantMagic = "EFPLANTS";
    private const double PlantToleranceM = 1e-3, PivotToleranceM = 2e-3;
    private static readonly Regex SetName = new(@"^[a-z][a-z0-9_]{0,31}\z", RegexOptions.Compiled);
    private static readonly Regex TiledPartId = new(@"^shell:[A-Za-z0-9_-]{1,64}\z", RegexOptions.Compiled);
    private static readonly string[] Collisions = { "none", "tree_climb", "solid" };
    private static readonly string[] FuelClasses = { "none", "grass", "herb", "shrub", "broadleaf", "conifer" };

    private sealed record SetRecord(
        int Index, string Name, string Prototype, PlantKind Kind, string Species, string Collides, string FuelClass, bool Seeds,
        (string Path, int Triangles)[] Meshes, string Table, int CapSlots, int Tiles, int Count);

    private sealed record TableCheck(int[] TileX, int[] TileZ, int[] First, int[] Rows, bool[] GroupsUsed, int RowsAt, int RowBytes);

    private static PlantCopies ParsePlantCopies(JsonElement block, string directory, Dictionary<string, (string Sha, long Bytes)> listed,
        Dictionary<string, byte[]> meshes, double[] low, double[] high)
    {
        Expect(block.ValueKind == JsonValueKind.Object, "plant_copies must be an object");
        Expect(Int(block, "tile_m") == PlantCopies.TileM, "$.plant_copies.tile_m: 8 was expected");
        var groups = Int(block, "groups");
        Expect(groups is >= 0 and <= PlantCopies.MaxGroups, $"plant_copies groups must be 0 to {PlantCopies.MaxGroups}");
        var elements = Arr(block, "sets").ToArray();
        Expect(elements.Length is >= 1 and <= PlantCopies.MaxSets, $"plant_copies holds 1 to {PlantCopies.MaxSets} sets");
        var records = elements.Select(ParseSetRecord).ToArray();
        foreach (var name in records.GroupBy(r => r.Name).Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(n => n, StringComparer.Ordinal))
            throw new RoomLoadException($"plant set {name} is listed twice");
        foreach (var table in records.GroupBy(r => r.Table).Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(n => n, StringComparer.Ordinal))
            throw new RoomLoadException($"plant table {table} belongs to more than one set");
        var footprint = (high[0] - low[0]) * (high[2] - low[2]);
        var total = records.Sum(r => (long)r.Count);
        Expect(total <= PlantCopies.DensityPerM2 * footprint,
            string.Create(CultureInfo.InvariantCulture, $"{total} plant copies on {footprint:0} m2 of bounds; at most {PlantCopies.DensityPerM2} per square metre"));

        // The meshes, one set after another (they are small): pinned, self-contained, as light as the manifest says, pivoted.
        var bounds = new Aabb[records.Length];
        var solidPoints = new Vector3[records.Length][];
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            var where = $"plant set {record.Name}";
            for (var m = 1; m < record.Meshes.Length; m++)
                Expect(record.Meshes[m].Triangles < record.Meshes[m - 1].Triangles, $"{where}: {record.Meshes[m].Path} is not lighter than the mesh before it");
            for (var m = 0; m < record.Meshes.Length; m++)
            {
                var (path, triangles) = record.Meshes[m];
                var key = directory + "/" + path;
                if (!meshes.TryGetValue(key, out var bytes)) bytes = Keep(meshes, key, path, listed, ReadMesh(directory, listed, path, "room.json"));
                var glb = GlbReader.Read(bytes, path);
                var held = glb.Triangles();
                Expect(held == triangles, $"{where}: {path} holds {(held?.ToString(CultureInfo.InvariantCulture) ?? "None")} triangles; the manifest says {triangles}");
                Expect(glb.Extent() is not null, $"{where}: {path} has no mesh with POSITION bounds");
                var (lowM, highM) = glb.Extent()!.Value;
                Expect(Math.Abs(lowM.Y) <= PivotToleranceM && Math.Abs(lowM.X + highM.X) <= PivotToleranceM && Math.Abs(lowM.Z + highM.Z) <= PivotToleranceM,
                    $"{where}: {path} is not pivoted at its bottom centre");
                if (m == 0 && record.Collides == "solid")
                    solidPoints[i] = Enumerable.Range(0, glb.Meshes.Count).SelectMany(glb.Positions).Distinct().ToArray();
                if (m == 0)
                    bounds[i] = new Aabb(new Vector3((float)lowM.X, (float)lowM.Y, (float)lowM.Z),
                        new Vector3((float)(highM.X - lowM.X), (float)(highM.Y - lowM.Y), (float)(highM.Z - lowM.Z)));
            }
        }

        // The tables: read, hashed and checked row by row, each set on its own worker. A broken table refuses the room with the
        // first problem in manifest order, whichever worker found it first.
        var checks = new TableCheck?[records.Length];
        var tables = new byte[records.Length][];
        var failures = new Exception?[records.Length];
        Parallel.For(0, records.Length, i =>
        {
            try
            {
                var record = records[i];
                tables[i] = ReadListed(directory, listed, record.Table, "room.json");
                checks[i] = CheckTable(tables[i], record, bounds[i].End.Y, low, high, groups);
            }
            catch (Exception error) { failures[i] = error; }
        });
        foreach (var failure in failures)
            if (failure != null) throw failure as RoomLoadException ?? new RoomLoadException(failure.Message);
        for (var group = 1; group <= groups; group++)
            Expect(checks.Any(c => c!.GroupsUsed[group]), $"plant group {group} has no copy; groups are numbered 1 to {groups} with none unused");

        var sets = records.Select((record, i) => new PlantSet
        {
            Name = record.Name, Prototype = record.Prototype, Kind = record.Kind, Species = record.Species, Collides = record.Collides,
            FuelClass = record.FuelClass, Seeds = record.Seeds, MeshPaths = record.Meshes.Select(m => directory + "/" + m.Path).ToArray(),
            TriangleCounts = record.Meshes.Select(m => m.Triangles).ToArray(), TablePath = record.Table, CapSlots = record.CapSlots,
            Count = record.Count, RowBytes = checks[i]!.RowBytes, MeshBounds = bounds[i], SolidPoints = solidPoints[i] ?? Array.Empty<Vector3>(),
            Data = tables[i], RowsAt = checks[i]!.RowsAt,
            TileX = checks[i]!.TileX, TileZ = checks[i]!.TileZ, TileFirst = checks[i]!.First, TileRows = checks[i]!.Rows,
        }).ToArray();
        return new PlantCopies(groups, sets);
    }

    /// <summary>One set's record against the schema's rules (the loader runs no JSON schema): names, enums, limits and what each kind may be.</summary>
    private static SetRecord ParseSetRecord(JsonElement set, int index)
    {
        Expect(set.ValueKind == JsonValueKind.Object, $"$.plant_copies.sets[{index}] must be an object");
        var at = $"$.plant_copies.sets[{index}]";
        var name = Str(set, "set");
        Expect(SetName.IsMatch(name), $"{at}.set: '{name}' does not match ^[a-z][a-z0-9_]{{0,31}}$");
        var where = $"plant set {name}";
        var prototype = Str(set, "prototype");
        var species = Str(set, "species");
        Expect(Token.IsMatch(prototype) && Token.IsMatch(species), $"{where}: prototype and species must be lowercase tokens");
        var kind = Str(set, "kind") switch
        {
            "plant" => PlantKind.Plant,
            "tree" => PlantKind.Tree,
            "stone" => PlantKind.Stone,
            var other => throw new RoomLoadException($"{at}.kind: '{other}' is not one of ['plant', 'tree', 'stone']"),
        };
        var collides = Str(set, "collides");
        Expect(Collisions.Contains(collides), $"{at}.collides: '{collides}' is not one of ['none', 'tree_climb', 'solid']");
        var fuel = Str(set, "fuel_class");
        Expect(FuelClasses.Contains(fuel), $"{at}.fuel_class: '{fuel}' is not one of the fuel classes");
        Expect(set.TryGetProperty("seeds", out var seedsValue) && seedsValue.ValueKind is JsonValueKind.True or JsonValueKind.False, $"{at}.seeds must be true or false");
        var seeds = seedsValue.GetBoolean();
        var meshList = new List<(string, int)> { (GlbPath(Str(set, "mesh"), $"{at}.mesh"), Triangles(set, $"{at}.triangle_count")) };
        if (set.TryGetProperty("lods", out var lods))
        {
            Expect(lods.ValueKind == JsonValueKind.Array && lods.GetArrayLength() <= 3, $"{at}.lods holds at most 3 lighter meshes");
            var k = 0;
            foreach (var lod in lods.EnumerateArray())
            {
                Expect(lod.ValueKind == JsonValueKind.Object, $"{at}.lods[{k}] must be an object");
                meshList.Add((GlbPath(Str(lod, "mesh"), $"{at}.lods[{k}].mesh"), Triangles(lod, $"{at}.lods[{k}].triangle_count")));
                k++;
            }
        }
        var table = SafePath(Str(set, "table"));
        Expect(table.EndsWith(".bin", StringComparison.Ordinal), $"{at}.table: '{table}' does not match \\.bin$");
        var hasSlots = set.TryGetProperty("cap_slots", out _);
        var slots = hasSlots ? Int(set, "cap_slots") : 0;
        var tiles = Int(set, "tiles");
        var count = Int(set, "count");
        Expect(tiles is >= 1 and <= PlantCopies.MaxTiles, $"{at}.tiles: {tiles} is outside 1 to {PlantCopies.MaxTiles}");
        Expect(count >= 1, $"{at}.count: {count} is less than the minimum of 1");
        Expect(count <= PlantCopies.MaxRows, $"{at}.count: {count} is more than the maximum of {PlantCopies.MaxRows}");
        if (kind == PlantKind.Tree)
        {
            Expect(hasSlots, $"{at}: 'cap_slots' is a required property");
            Expect(slots is >= 1 and <= PlantCopies.MaxCapSlots, $"{at}.cap_slots: {slots} is outside 1 to {PlantCopies.MaxCapSlots}");
            Expect(collides == "tree_climb", $"{at}.collides: 'tree_climb' was expected");
            Expect(fuel is "broadleaf" or "conifer", $"{at}.fuel_class: '{fuel}' is not one of ['broadleaf', 'conifer']");
        }
        else
        {
            Expect(!hasSlots, $"{at}: only trees have cap_slots (must not match {{\"required\": [\"cap_slots\"]}})");
            Expect(collides != "tree_climb", $"{at}.collides: only trees climb (must not match {{\"const\": \"tree_climb\"}})");
        }
        if (kind == PlantKind.Plant)
        {
            Expect(collides == "none", $"{at}.collides: 'none' was expected");
            Expect(fuel is "grass" or "herb" or "shrub", $"{at}.fuel_class: '{fuel}' is not one of ['grass', 'herb', 'shrub']");
        }
        if (kind == PlantKind.Stone)
        {
            Expect(fuel == "none", $"{at}.fuel_class: 'none' was expected");
            Expect(!seeds, $"{at}.seeds: False was expected");
        }
        return new SetRecord(index, name, prototype, kind, species, collides, fuel, seeds, meshList.ToArray(), table, slots, tiles, count);
    }

    private static string GlbPath(string path, string at)
    {
        SafePath(path);
        Expect(path.EndsWith(".glb", StringComparison.Ordinal), $"{at}: '{path}' must be a self-contained .glb");
        return path;
    }

    private static int Triangles(JsonElement element, string at)
    {
        var count = Int(element, "triangle_count");
        Expect(count is >= 1 and <= 1_000_000, $"{at}: {count} is outside 1 to 1000000");
        return count;
    }

    /// <summary>
    /// A set's table (contracts/README.md "Plant copies"; validate.py read_plant_table and _check_plant_rows): its header against
    /// the set, its exact size, the tile index (sorted, each entry starting where the one before ended, covering every row), then
    /// every row: finite, in its index entry's tile, sorted by x then z, inside the bounds, its yaw, scale, tint, top, flags and
    /// group, and a tree's climbing collision. Stops at the first problem. Pure: runs on a worker thread.
    /// </summary>
    private static TableCheck CheckTable(byte[] data, SetRecord record, double height, double[] low, double[] high, int groups)
    {
        var where = $"plant set {record.Name}";
        var table = $"{where}: table {record.Table}";
        Expect(data.Length >= 40, $"{table} is {data.Length} bytes, shorter than the 40-byte header");
        uint Word(int index) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8 + 4 * index));
        var magic = System.Text.Encoding.ASCII.GetString(data, 0, 8);
        Expect(magic == PlantMagic, $"{table} header magic is {PythonBytes(data.AsSpan(0, 8))}; expected b'{PlantMagic}'");
        var tree = record.Kind == PlantKind.Tree;
        var rowBytes = PlantSet.RowBytesBase + (tree ? PlantSet.TreeTailBytes + PlantSet.CapBytes * record.CapSlots : 0);
        var expected = new (string Name, uint Got, long Want)[]
        {
            ("version", Word(0), 1), ("kind", Word(1), (int)record.Kind), ("row_bytes", Word(2), rowBytes),
            ("cap_slots", Word(5), tree ? record.CapSlots : 0), ("tile_m", Word(6), PlantCopies.TileM), ("reserved", Word(7), 0),
        };
        foreach (var (name, got, want) in expected)
            Expect(got == want, $"{table} header {name} is {got}; expected {want}");
        long rowCount = Word(3), tileCount = Word(4);
        var size = 40 + 16 * tileCount + rowBytes * rowCount;
        Expect(data.LongLength == size, $"{table} is {data.LongLength} bytes; its header's {tileCount} tiles and {rowCount} rows of {rowBytes} bytes make {size}");
        Expect(rowCount == record.Count && tileCount == record.Tiles,
            $"{where}: table holds {rowCount} rows in {tileCount} tiles; the manifest says {record.Count} in {record.Tiles}");

        var tiles = (int)tileCount;
        var tileX = new int[tiles];
        var tileZ = new int[tiles];
        var firsts = new int[tiles];
        var counts = new int[tiles];
        long expectedFirst = 0;
        for (var index = 0; index < tiles; index++)
        {
            var at = 40 + 16 * index;
            int tx = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)), tz = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 4));
            long first = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 8)), count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 12));
            if (index > 0 && (tx < tileX[index - 1] || (tx == tileX[index - 1] && tz <= tileZ[index - 1])))
                throw new RoomLoadException($"{where}: tile index entry {index} ({tx}, {tz}) is not after ({tileX[index - 1]}, {tileZ[index - 1]}); tiles are sorted by tx, then tz");
            if (count < 1 || first != expectedFirst)
                throw new RoomLoadException($"{where}: tile index entry {index} ({tx}, {tz}) starts at row {first} with {count}; expected row {expectedFirst} and at least one");
            tileX[index] = tx;
            tileZ[index] = tz;
            firsts[index] = (int)first;
            counts[index] = (int)count;
            expectedFirst = first + count;
        }
        Expect(expectedFirst == rowCount, $"{where}: the tile index covers {expectedFirst} rows; the table has {rowCount}");

        var used = new bool[groups + 1];
        var rowsAt = 40 + 16 * tiles;
        // Tiles are checked side by side (a 10x island's grass table holds 250,000 rows); the first problem in table order wins.
        var problems = new string?[tiles];
        Parallel.For(0, tiles, index => problems[index] = CheckRows(data, record, rowsAt, rowBytes, tileX[index], tileZ[index], firsts[index], counts[index], height, low, high, groups, used));
        foreach (var problem in problems)
            if (problem != null) throw new RoomLoadException(problem);
        return new TableCheck(tileX, tileZ, firsts, counts, used, rowsAt, rowBytes);
    }

    /// <summary>One tile's rows, in order; the first problem as its message, or null. Marks the groups its rows use (true only, so tiles may share the array).</summary>
    private static string? CheckRows(byte[] data, SetRecord record, int rowsAt, int rowBytes, int tx, int tz, int first, int count, double height,
        double[] low, double[] high, int groups, bool[] used)
    {
        var tree = record.Kind == PlantKind.Tree;
        var floats = 11 + (tree ? 6 + 8 * record.CapSlots : 0);
        double previousX = double.NaN, previousZ = double.NaN;
        for (var row = first; row < first + count; row++)
        {
            var o = rowsAt + row * rowBytes;
            float F(int field) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(o + 4 * FieldOffset(field)));
            string At() => $"plant set {record.Name}: row {row} (plant:{record.Name}-{row})";
            for (var f = 0; f < floats; f++)
                if (!float.IsFinite(F(f))) return $"{At()} has a non-finite number";
            double x = F(0), y = F(1), z = F(2), yaw = F(3), sx = F(4), sy = F(5), sz = F(6), top = F(10);
            var (cx, cz) = ((long)Math.Floor(x / PlantCopies.TileM), (long)Math.Floor(z / PlantCopies.TileM));
            if (cx != tx || cz != tz)
                return $"{At()} at ({Py(x)}, {Py(z)}) lies in tile ({cx}, {cz}), not in its index entry ({tx}, {tz})";
            if (row > first && (x < previousX || (x == previousX && z < previousZ)))
                return $"{At()} is out of order: rows in a tile are sorted by x, then z";
            if (!(Inside(x, 0) && Inside(y, 1) && Inside(z, 2))) return $"{At()} is outside the room bounds";
            if (yaw is < -180 or > 180) return $"{At()} has yaw {Py(yaw)}; it is between -180 and 180 degrees";
            if (!(sx is > 0 and <= 100 && sy is > 0 and <= 100 && sz is > 0 and <= 100)) return $"{At()} has a scale outside (0, 100]";
            for (var c = 7; c <= 9; c++)
                if (F(c) is < 0 or > 1) return $"{At()} has a tint outside [0, 1]";
            if (Math.Abs(top - (y + height * sy)) > PlantToleranceM)
                return string.Create(CultureInfo.InvariantCulture, $"{At()} has top_m {Py(top)}; its y plus its mesh's height times its y scale is {y + height * sy:0.0000}");
            if (top > high[1] + PlantToleranceM) return $"{At()} reaches above the room bounds";
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o + 44));
            if ((flags & ~1u) != 0) return $"{At()} sets flag bits 0x{flags:x}; only 0x1 are defined";
            var group = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o + 48));
            if (group > groups) return $"{At()} is in group {group}; the room has {groups}";
            if (tree && TreeProblem(data, o + 56, record.CapSlots, y, low, high) is { } problem) return $"{At()} {problem}";
            used[group] = true;
            previousX = x;
            previousZ = z;
        }
        return null;

        bool Inside(double value, int axis) => low[axis] - PlantToleranceM <= value && value <= high[axis] + PlantToleranceM;
    }

    /// <summary>The byte offset (in 4-byte fields) of the n-th float of a row: the eleven common floats, then a tree's six tail floats and its caps.</summary>
    private static int FieldOffset(int floatIndex) => floatIndex < 11 ? floatIndex : floatIndex < 17 ? floatIndex + 3 : floatIndex + 4;

    /// <summary>
    /// A tree row's climbing collision (validate.py _tree_row_problem): cap_count within the slots, positive radii, the base below
    /// the trunk's top below the pole's top inside the bounds, the trunk and every cap inside the bounds, unused slots all zero,
    /// each cap's top no wider than its rim and its rim between the base and its top, and the pole no higher than the highest cap.
    /// </summary>
    private static string? TreeProblem(byte[] data, int tail, int slots, double y, double[] low, double[] high)
    {
        float T(int field) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(tail + 4 * field));
        double tx = T(0), tz = T(1), trunkRadius = T(2), poleRadius = T(3), trunkTop = T(4), poleTop = T(5);
        var capCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(tail + 24));
        var tol = PlantToleranceM;
        if (capCount < 1 || capCount > slots) return $"has cap_count {capCount}; it is 1 to the set's {slots} cap slots";
        if (trunkRadius <= 0 || poleRadius <= 0) return "has a trunk or pole radius that is not positive";
        if (!(y < trunkTop && trunkTop < poleTop && poleTop <= high[1] + tol)) return "needs its base below trunk_top_m, below pole_top_m, inside the bounds";
        if (!(low[0] - tol <= tx && tx <= high[0] + tol && low[2] - tol <= tz && tz <= high[2] + tol)) return "has its trunk outside the room bounds";
        for (var k = (int)capCount; k < slots; k++)
            for (var f = 0; f < 8; f++)
                if (T(7 + 8 * k + f) != 0) return "has an unused cap slot that is not all zero";
        var highest = double.NegativeInfinity;
        for (var k = 0; k < capCount; k++)
        {
            double cx = T(7 + 8 * k), cz = T(8 + 8 * k), rimX = T(9 + 8 * k), rimZ = T(10 + 8 * k), rim = T(11 + 8 * k), top = T(12 + 8 * k), topX = T(13 + 8 * k), topZ = T(14 + 8 * k);
            if (!(0 < topX && topX <= rimX && 0 < topZ && topZ <= rimZ)) return $"has cap {k} with a top wider than its rim or a size that is not positive";
            if (!(y < rim && rim < top && top <= high[1] + tol)) return $"has cap {k} whose rim is not between the copy's base and the cap's top, inside the bounds";
            if (!(low[0] - tol <= cx && cx <= high[0] + tol && low[2] - tol <= cz && cz <= high[2] + tol)) return $"has cap {k} outside the room bounds";
            highest = Math.Max(highest, top);
        }
        return poleTop > highest ? "has its pole above its highest cap: a climber comes up through a cap" : null;
    }

    private static TerrainTiles ParseTerrainTiles(JsonElement block, string directory, IReadOnlyList<ShellPart> shell, Dictionary<string, byte[]> meshes)
    {
        Expect(block.ValueKind == JsonValueKind.Object, "terrain_tiles must be an object");
        Expect(Int(block, "tile_m") == TerrainTiles.TileM, "$.terrain_tiles.tile_m: 16 was expected");
        var levels = Arr(block, "levels").Select((level, i) =>
        {
            Expect(level.ValueKind == JsonValueKind.Object && level.TryGetProperty("grid_m", out var grid) && grid.ValueKind == JsonValueKind.Number,
                $"$.terrain_tiles.levels[{i}] needs grid_m");
            var value = level.GetProperty("grid_m").GetDouble();
            Expect(value > 0 && value <= TerrainTiles.TileM, $"$.terrain_tiles.levels[{i}].grid_m must be above 0 and at most 16");
            return value;
        }).ToArray();
        Expect(levels.Length is >= 1 and <= TerrainTiles.MaxLevels, $"terrain_tiles holds 1 to {TerrainTiles.MaxLevels} levels");
        for (var i = 1; i < levels.Length; i++)
            Expect(levels[i] > levels[i - 1], "terrain levels must be finest first, each coarser than the one before");
        var entries = Arr(block, "parts").Select((entry, i) =>
        {
            Expect(entry.ValueKind == JsonValueKind.Object, $"$.terrain_tiles.parts[{i}] must be an object");
            var id = Str(entry, "part");
            Expect(TiledPartId.IsMatch(id), $"$.terrain_tiles.parts[{i}].part: '{id}' is not a shell part id");
            var tiles = Int(entry, "tiles");
            Expect(tiles is >= 1 and <= TerrainTiles.MaxTilesPerPart, $"$.terrain_tiles.parts[{i}].tiles: {tiles} is outside 1 to {TerrainTiles.MaxTilesPerPart}");
            return (Id: id, Tiles: tiles);
        }).ToArray();
        Expect(entries.Length is >= 1 and <= TerrainTiles.MaxParts, $"terrain_tiles lists 1 to {TerrainTiles.MaxParts} parts");
        foreach (var id in entries.GroupBy(e => e.Id).Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(n => n, StringComparer.Ordinal))
            throw new RoomLoadException($"terrain part {id} is listed twice");
        var parts = new List<TiledPart>();
        foreach (var (id, count) in entries)
        {
            var where = $"terrain part {id}";
            var part = shell.FirstOrDefault(p => p.Id == id) ?? throw new RoomLoadException($"{where} is not a shell part");
            Expect(part.MeshPath != null, $"{where} is not a mesh part");
            var path = directory + "/" + part.MeshPath;
            var glb = GlbReader.Read(meshes[path], part.MeshPath!);
            var tiles = TerrainTiles.CheckNodes(glb, levels.Length, count, where);
            // The game reads a colliding part's ground straight from the file (TiledPart.GroundFaces): refuse now what it could not read then.
            if (part.Collides || RoomWater.IsWater(part))
                foreach (var (tx, tz, byLevel) in tiles)
                    if (glb.Unreadable(byLevel[0]) is { } why) throw new RoomLoadException($"{where}: tile ({tx}, {tz}) level 0 cannot be read for collision: {why}");
            // The ground is kept as positions and corners (TiledPart.Ground), so the file's bytes can go once the land is drawn.
            (float[], ushort[]?, int[]?)[]? ground = null;
            if (part.Collides || RoomWater.IsWater(part))
            {
                var read = ground = new (float[], ushort[]?, int[]?)[tiles.Count];
                System.Threading.Tasks.Parallel.For(0, tiles.Count, i => read[i] = glb.Ground(tiles[i].MeshByLevel[0]));
            }
            // Sight and aim see the far land through its next level (S12b): kept for a drawn, colliding part.
            (float[], ushort[]?, int[]?)[]? coarse = null;
            if (part.Collides && part.Drawn)
            {
                var level = Math.Min(1, levels.Length - 1);
                var read = coarse = new (float[], ushort[]?, int[]?)[tiles.Count];
                // A level the reader cannot take (compressed positions: the contract only promises level 0) falls back to level 0.
                System.Threading.Tasks.Parallel.For(0, tiles.Count, i => read[i] = glb.Ground(tiles[i].MeshByLevel[glb.Unreadable(tiles[i].MeshByLevel[level]) == null ? level : 0]));
            }
            parts.Add(new TiledPart { PartId = id, MeshPath = path, Ground = ground, Coarse = coarse, Tiles = tiles });
        }
        return new TerrainTiles { LevelGridM = levels.Select(l => (float)l).ToArray(), Parts = parts };
    }

    /// <summary>A number as Python prints it (contracts/validate.py's messages): the shortest round trip, with ".0" on a whole number.</summary>
    private static string Py(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') || text.Contains("Infinity") || text.Contains("NaN") ? text : text + ".0";
    }

    private static string PythonBytes(ReadOnlySpan<byte> bytes)
    {
        var text = new System.Text.StringBuilder("b'");
        foreach (var b in bytes) text.Append(b is >= 0x20 and < 0x7F && b != (byte)'\'' && b != (byte)'\\' ? ((char)b).ToString() : $"\\x{b:x2}");
        return text.Append('\'').ToString();
    }

    /// <summary>The bounds exactly as the manifest writes them (doubles), for the contract's checks, which compare in doubles.</summary>
    private static double[] Doubles(JsonElement element)
    {
        var values = element.EnumerateArray().Select(v => v.GetDouble()).ToArray();
        Expect(values.Length == 3 && values.All(double.IsFinite), "expected three finite numbers");
        return values;
    }
}
