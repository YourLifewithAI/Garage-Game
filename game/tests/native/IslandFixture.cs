using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using GarageGame.Native.Room;
using FileAccess = Godot.FileAccess;

namespace GarageGame.Tests;

/// <summary>
/// Writes synthetic islands for the loading tests (packet S6), from the contract's island example: its meshes and terrain GLBs
/// copied as they are, its tables replaced by synthetic ones written here as the contract lays them out (README "Plant copies":
/// header, tile index, rows sorted by 8 m tile then x then z), and its manifest rewritten with each file's hash and size.
/// Deterministic: a fixed seed. Written under user://tests/rooms, never into the repository.
/// </summary>
public static class IslandFixture
{
    public static string ExampleDirectory => Path.GetFullPath(ProjectSettings.GlobalizePath("res://") + "../contracts/examples/rooms/island_example");

    /// <summary>A copy's row before it is sorted into its table. Caps are (x, z, rim half x, rim half z, rim y, top y, top half x, top half z).</summary>
    public sealed record Row(float X, float Y, float Z, float Yaw, Vector3 Scale, Color Tint, float Top, uint Flags, uint Group, uint Seed,
        float[]? Trunk = null, float[][]? Caps = null);

    public sealed record SetSpec(string Name, string Prototype, string Kind, string Species, string Collides, string FuelClass, bool Seeds,
        string Mesh, int Triangles, (string Mesh, int Triangles)[] Lods, int CapSlots, IReadOnlyList<Row> Rows);

    /// <summary>A broadleaf like the example's: a trunk to 0.53 m, a pole to 0.935 m, one cap from 0.8 to 0.94 m over the trunk.</summary>
    public static Row Broadleaf(float x, float z, float y, uint group, uint seed) => new(x, y, z, 15f, Vector3.One, new Color(0.9f, 1f, 0.85f), y + 0.95f, 0, group, seed,
        new[] { x, z, 0.055f, 0.035f, y + 0.53f, y + 0.935f }, new[] { new[] { x, z, 0.15f, 0.15f, y + 0.8f, y + 0.94f, 0.06f, 0.06f } });

    /// <summary>The table bytes for rows: sorted, behind their tile index. Returns the bytes, the tile count and the rows in table order.</summary>
    public static (byte[] Bytes, int Tiles, Row[] Ordered) Table(PlantKind kind, int capSlots, IEnumerable<Row> rows)
    {
        var ordered = rows.OrderBy(r => PlantCopies.TileOf(r.X, r.Z).Tx).ThenBy(r => PlantCopies.TileOf(r.X, r.Z).Tz).ThenBy(r => r.X).ThenBy(r => r.Z).ToArray();
        var tiles = new List<(int Tx, int Tz, int First, int Count)>();
        for (var i = 0; i < ordered.Length; i++)
        {
            var tile = PlantCopies.TileOf(ordered[i].X, ordered[i].Z);
            if (tiles.Count > 0 && tiles[^1].Tx == tile.Tx && tiles[^1].Tz == tile.Tz) tiles[^1] = tiles[^1] with { Count = tiles[^1].Count + 1 };
            else tiles.Add((tile.Tx, tile.Tz, i, 1));
        }
        var tree = kind == PlantKind.Tree;
        var rowBytes = 56 + (tree ? 28 + 32 * capSlots : 0);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("EFPLANTS"u8.ToArray());
        foreach (var word in new uint[] { 1, (uint)kind, (uint)rowBytes, (uint)ordered.Length, (uint)tiles.Count, tree ? (uint)capSlots : 0, 8, 0 }) writer.Write(word);
        foreach (var (tx, tz, first, count) in tiles) { writer.Write(tx); writer.Write(tz); writer.Write((uint)first); writer.Write((uint)count); }
        foreach (var row in ordered)
        {
            foreach (var value in new[] { row.X, row.Y, row.Z, row.Yaw, row.Scale.X, row.Scale.Y, row.Scale.Z, row.Tint.R, row.Tint.G, row.Tint.B, row.Top }) writer.Write(value);
            writer.Write(row.Flags);
            writer.Write(row.Group);
            writer.Write(row.Seed);
            if (!tree) continue;
            foreach (var value in row.Trunk!) writer.Write(value);
            writer.Write((uint)row.Caps!.Length);
            for (var k = 0; k < capSlots; k++)
                foreach (var value in k < row.Caps.Length ? row.Caps[k] : new float[8]) writer.Write(value);
        }
        writer.Flush();
        return (stream.ToArray(), tiles.Count, ordered);
    }

    /// <summary>
    /// An island at user://tests/rooms/&lt;roomId&gt;: the example's files, sets as given (a set naming an example mesh reuses it),
    /// bounds as given (null keeps the example's). Returns the room's directory.
    /// </summary>
    public static string Write(string roomId, IReadOnlyList<SetSpec> sets, int groups, Vector3? boundsMin = null, Vector3? boundsMax = null) =>
        Write(roomId, sets, groups, boundsMin, boundsMax, customize: null);

    /// <summary>
    /// A wide island for the land's collision streaming (S12b): the example's grass part replaced by tiles over tx0..tx1, tz0..tz1
    /// (16 m each) of gently rolling ground (Height), three levels each (2, 4 and 8 m grids), no sand; a sea round it at -0.05 m
    /// with a beach at each end and a jetty whose root stands on the land at the east end; spawns at (0, -8) and (0.5, -8).
    /// </summary>
    public static string WriteWideLand(string roomId, int tx0, int tx1, int tz0, int tz1, IReadOnlyList<SetSpec> sets)
    {
        var (x0, x1, z0, z1) = (tx0 * 16f, (tx1 + 1) * 16f, tz0 * 16f, (tz1 + 1) * 16f);
        return Write(roomId, sets, 2, new Vector3(x0 - 24, -1, z0 - 24), new Vector3(x1 + 24, 8, z1 + 24), (manifest, files) =>
        {
            files["shell/terrain_grass.glb"] = TerrainGlb(tx0, tx1, tz0, tz1);
            files.Remove("shell/terrain_sand.glb");
            var parts = manifest["shell"]!["parts"]!.AsArray();
            foreach (var part in parts.Where(p => p!["id"]!.GetValue<string>() == "shell:terrain_sand").ToArray()) parts.Remove(part);
            var tiled = manifest["terrain_tiles"]!["parts"]!.AsArray();
            foreach (var part in tiled.Where(p => p!["part"]!.GetValue<string>() == "shell:terrain_sand").ToArray()) tiled.Remove(part);
            tiled[0]!["tiles"] = (tx1 - tx0 + 1) * (tz1 - tz0 + 1);
            foreach (var spawn in manifest["spawns"]!.AsArray())
            {
                var at = spawn!["position_m"]!.AsArray();
                at[1] = Height(at[0]!.GetValue<double>(), at[2]!.GetValue<double>());
            }
            static JsonArray Loop(float a0, float a1, float b0, float b1) =>
                new(new JsonArray(a0, b0), new JsonArray(a1, b0), new JsonArray(a1, b1), new JsonArray(a0, b1));
            var root = new Vector2(x1 - 2, 0);
            var deck = (float)Height(root.X, root.Y);
            manifest["extensions"] = new JsonObject
            {
                ["x_landscape_sea"] = new JsonObject
                {
                    ["level_m"] = -0.05, ["swim_depth_m"] = 0.12, ["grid_margin_m"] = 1.7,
                    ["coast"] = new JsonObject { ["outline_m"] = Loop(x0, x1, z0, z1) },
                    ["reef"] = new JsonObject { ["outline_m"] = Loop(x0 - 4, x1 + 4, z0 - 4, z1 + 4), ["crest_y_m"] = -0.03, ["band_half_width_m"] = 0.5, ["passes"] = new JsonArray() },
                    ["play_area"] = new JsonObject { ["outline_m"] = Loop(x0 - 10, x1 + 10, z0 - 10, z1 + 10) },
                    ["beaches"] = new JsonArray(
                        new JsonObject { ["id"] = "beach_west", ["wash_ashore_m"] = new JsonArray(x0 + 1, Height(x0 + 1, 8), 8), ["water_m"] = new JsonArray(x0 - 1, -0.05, 8), ["yaw_deg"] = 90 },
                        new JsonObject { ["id"] = "beach_east", ["wash_ashore_m"] = new JsonArray(x1 - 1, Height(x1 - 1, z1 - 8), z1 - 8), ["water_m"] = new JsonArray(x1 + 1, -0.05, z1 - 8), ["yaw_deg"] = -90 }),
                    ["jetty"] = new JsonObject
                    {
                        ["root_m"] = new JsonArray(root.X, deck, root.Y), ["end_m"] = new JsonArray(x1 + 3, deck, root.Y), ["yaw_deg"] = -90, ["width_m"] = 0.16, ["deck_top_m"] = deck,
                    },
                },
            };
        });
    }

    /// <summary>
    /// The wide island's ground height: a few centimetres of roll over a 0.3 m plateau, always above its sea, and one hill 2 m high
    /// round (HillX, 0), far from the spawn, for sight across the land.
    /// </summary>
    public static double Height(double x, double z) =>
        0.3 + 0.05 * Math.Sin(0.37 * x) + 0.03 * Math.Cos(0.53 * z) + 2.0 * Math.Exp(-((x - HillX) * (x - HillX) + z * z) / (2 * 6.0 * 6.0));

    public const double HillX = 120;

    /// <summary>A tiled terrain GLB as the contract has it: one root node per tile and level (tile_tx_tz_lodN), a grid of Height's points.</summary>
    private static byte[] TerrainGlb(int tx0, int tx1, int tz0, int tz1)
    {
        var bin = new MemoryStream();
        var writer = new BinaryWriter(bin);
        var nodes = new JsonArray();
        var meshes = new JsonArray();
        var views = new JsonArray();
        var accessors = new JsonArray();
        void Align() { while (bin.Length % 4 != 0) writer.Write((byte)0); }
        for (var tx = tx0; tx <= tx1; tx++)
        for (var tz = tz0; tz <= tz1; tz++)
            for (var level = 0; level < 3; level++)
            {
                var cells = 8 >> level;
                var step = 16f / cells;
                var low = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
                var high = new[] { float.MinValue, float.MinValue, float.MinValue };
                Align();
                var start = bin.Length;
                for (var j = 0; j <= cells; j++)
                for (var i = 0; i <= cells; i++)
                {
                    var p = new[] { tx * 16f + i * step, (float)Height(tx * 16f + i * step, tz * 16f + j * step), tz * 16f + j * step };
                    for (var k = 0; k < 3; k++) { writer.Write(p[k]); low[k] = Math.Min(low[k], p[k]); high[k] = Math.Max(high[k], p[k]); }
                }
                views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = start, ["byteLength"] = bin.Length - start, ["target"] = 34962 });
                accessors.Add(new JsonObject { ["bufferView"] = views.Count - 1, ["componentType"] = 5126, ["count"] = (cells + 1) * (cells + 1), ["type"] = "VEC3",
                    ["min"] = new JsonArray(low[0], low[1], low[2]), ["max"] = new JsonArray(high[0], high[1], high[2]) });
                start = bin.Length;
                // Each cell two triangles, counter-clockwise seen from above (glTF's front faces), so the ground faces up.
                for (var j = 0; j < cells; j++)
                for (var i = 0; i < cells; i++)
                {
                    ushort a = (ushort)(j * (cells + 1) + i), b = (ushort)(a + 1), d = (ushort)(a + cells + 1), c = (ushort)(d + 1);
                    foreach (var index in new[] { a, c, b, a, d, c }) writer.Write(index);
                }
                views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = start, ["byteLength"] = bin.Length - start, ["target"] = 34963 });
                accessors.Add(new JsonObject { ["bufferView"] = views.Count - 1, ["componentType"] = 5123, ["count"] = cells * cells * 6, ["type"] = "SCALAR" });
                meshes.Add(new JsonObject { ["primitives"] = new JsonArray(new JsonObject { ["attributes"] = new JsonObject { ["POSITION"] = accessors.Count - 2 }, ["indices"] = accessors.Count - 1, ["material"] = 0 }) });
                nodes.Add(new JsonObject { ["mesh"] = meshes.Count - 1, ["name"] = $"tile_{tx}_{tz}_lod{level}" });
            }
        Align();
        writer.Flush();
        var json = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "Garage Game tests" }, ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(Enumerable.Range(0, nodes.Count).Select(i => (JsonNode)i).ToArray()) }),
            ["nodes"] = nodes, ["meshes"] = meshes,
            ["materials"] = new JsonArray(new JsonObject { ["name"] = "grass", ["pbrMetallicRoughness"] = new JsonObject { ["baseColorFactor"] = new JsonArray(0.45, 0.62, 0.3, 1.0), ["metallicFactor"] = 0.0, ["roughnessFactor"] = 0.9 } }),
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Length }), ["bufferViews"] = views, ["accessors"] = accessors,
        };
        var text = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
        var padded = (text.Length + 3) / 4 * 4;
        using var glb = new MemoryStream();
        using var output = new BinaryWriter(glb);
        output.Write(0x46546C67u);
        output.Write(2u);
        output.Write((uint)(12 + 8 + padded + 8 + bin.Length));
        output.Write((uint)padded);
        output.Write(0x4E4F534Au);
        output.Write(text);
        for (var i = text.Length; i < padded; i++) output.Write((byte)0x20);
        output.Write((uint)bin.Length);
        output.Write(0x004E4942u);
        output.Write(bin.ToArray());
        output.Flush();
        return glb.ToArray();
    }

    private static string Write(string roomId, IReadOnlyList<SetSpec> sets, int groups, Vector3? boundsMin, Vector3? boundsMax, Action<JsonObject, Dictionary<string, byte[]>>? customize)
    {
        var target = "user://tests/rooms/" + roomId;
        var absolute = ProjectSettings.GlobalizePath(target);
        if (System.IO.Directory.Exists(absolute)) System.IO.Directory.Delete(absolute, recursive: true);
        DirAccess.MakeDirRecursiveAbsolute(absolute + "/plants");
        DirAccess.MakeDirRecursiveAbsolute(absolute + "/shell");
        var manifest = JsonNode.Parse(File.ReadAllText(ExampleDirectory + "/room.json"))!.AsObject();
        var files = new Dictionary<string, byte[]>();
        foreach (var entry in manifest["files"]!.AsArray())
        {
            var path = entry!["path"]!.GetValue<string>();
            if (path.EndsWith(".glb", StringComparison.Ordinal)) files[path] = File.ReadAllBytes(ExampleDirectory + "/" + path);
        }
        var records = new JsonArray();
        foreach (var spec in sets)
        {
            var kind = spec.Kind switch { "tree" => PlantKind.Tree, "stone" => PlantKind.Stone, _ => PlantKind.Plant };
            var (bytes, tiles, _) = Table(kind, spec.CapSlots, spec.Rows);
            var table = $"plants/{spec.Name}.bin";
            files[table] = bytes;
            var record = new JsonObject
            {
                ["set"] = spec.Name, ["prototype"] = spec.Prototype, ["kind"] = spec.Kind, ["species"] = spec.Species, ["collides"] = spec.Collides,
                ["fuel_class"] = spec.FuelClass, ["seeds"] = spec.Seeds, ["mesh"] = spec.Mesh, ["triangle_count"] = spec.Triangles,
            };
            if (spec.Lods.Length > 0) record["lods"] = new JsonArray(spec.Lods.Select(l => (JsonNode)new JsonObject { ["mesh"] = l.Mesh, ["triangle_count"] = l.Triangles }).ToArray());
            record["table"] = table;
            if (kind == PlantKind.Tree) record["cap_slots"] = spec.CapSlots;
            record["tiles"] = tiles;
            record["count"] = spec.Rows.Count;
            records.Add(record);
        }
        manifest["room_id"] = roomId;
        manifest["plant_copies"]!["groups"] = groups;
        manifest["plant_copies"]!["sets"] = records;
        if (boundsMin is { } low && boundsMax is { } high)
            manifest["bounds"] = new JsonObject { ["min_m"] = new JsonArray(low.X, low.Y, low.Z), ["max_m"] = new JsonArray(high.X, high.Y, high.Z) };
        customize?.Invoke(manifest, files);
        var listed = new JsonArray();
        foreach (var (path, bytes) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            Save(target + "/" + path, bytes);
            listed.Add(new JsonObject { ["path"] = path, ["sha256"] = RoomData.Sha256Hex(bytes), ["bytes"] = bytes.Length });
        }
        manifest["files"] = listed;
        Save(target + "/room.json", System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        return target;
    }

    /// <summary>
    /// A copy of a room (its manifest and every file it lists, its room id kept, at target, a user:// folder named as the room) with
    /// plant copies added: the island example's plant meshes, the sets as given, and the manifest's files listed again with their hashes
    /// and sizes. For Grow's tests (decision 52), which need the test room's objects and plants beside them.
    /// </summary>
    public static string WithPlants(string sourceDirectory, string target, IReadOnlyList<SetSpec> sets)
    {
        var source = ProjectSettings.GlobalizePath(sourceDirectory);
        var absolute = ProjectSettings.GlobalizePath(target);
        if (System.IO.Directory.Exists(absolute)) System.IO.Directory.Delete(absolute, recursive: true);
        var manifest = JsonNode.Parse(File.ReadAllText(source + "/room.json"))!.AsObject();
        var files = new Dictionary<string, byte[]>();
        foreach (var entry in manifest["files"]!.AsArray())
        {
            var path = entry!["path"]!.GetValue<string>();
            files[path] = File.ReadAllBytes(source + "/" + path);
        }
        var records = new JsonArray();
        foreach (var spec in sets)
        {
            var kind = spec.Kind switch { "tree" => PlantKind.Tree, "stone" => PlantKind.Stone, _ => PlantKind.Plant };
            var (bytes, tiles, _) = Table(kind, spec.CapSlots, spec.Rows);
            var table = $"plants/{spec.Name}.bin";
            files[table] = bytes;
            foreach (var mesh in new[] { spec.Mesh }.Concat(spec.Lods.Select(l => l.Mesh))) files[mesh] = File.ReadAllBytes(ExampleDirectory + "/" + mesh);
            var record = new JsonObject
            {
                ["set"] = spec.Name, ["prototype"] = spec.Prototype, ["kind"] = spec.Kind, ["species"] = spec.Species, ["collides"] = spec.Collides,
                ["fuel_class"] = spec.FuelClass, ["seeds"] = spec.Seeds, ["mesh"] = spec.Mesh, ["triangle_count"] = spec.Triangles,
            };
            if (spec.Lods.Length > 0) record["lods"] = new JsonArray(spec.Lods.Select(l => (JsonNode)new JsonObject { ["mesh"] = l.Mesh, ["triangle_count"] = l.Triangles }).ToArray());
            record["table"] = table;
            if (kind == PlantKind.Tree) record["cap_slots"] = spec.CapSlots;
            record["tiles"] = tiles;
            record["count"] = spec.Rows.Count;
            records.Add(record);
        }
        manifest["plant_copies"] = new JsonObject { ["tile_m"] = PlantCopies.TileM, ["groups"] = sets.SelectMany(s => s.Rows).Select(r => (int)r.Group).DefaultIfEmpty(0).Max(), ["sets"] = records };
        foreach (var directory in files.Keys.Select(p => Path.GetDirectoryName(p)!.Replace('\\', '/')).Distinct())
            DirAccess.MakeDirRecursiveAbsolute(absolute + "/" + directory);
        var listed = new JsonArray();
        foreach (var (path, bytes) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            Save(target + "/" + path, bytes);
            listed.Add(new JsonObject { ["path"] = path, ["sha256"] = RoomData.Sha256Hex(bytes), ["bytes"] = bytes.Length });
        }
        manifest["files"] = listed;
        Save(target + "/room.json", System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        return target;
    }

    /// <summary>The example's grass set with these rows: a 12-triangle tuft and its 2-triangle card for distance.</summary>
    public static SetSpec Grass(string name, IReadOnlyList<Row> rows) =>
        new(name, "grass_tuft", "plant", "grass", "none", "grass", true, "plants/grass.glb", 12, new[] { ("plants/grass_far.glb", 2) }, 0, rows);

    public static SetSpec Trees(IReadOnlyList<Row> rows) =>
        new("broadleaf", "broadleaf", "tree", "broadleaf", "tree_climb", "broadleaf", true, "plants/broadleaf.glb", 12, new[] { ("plants/broadleaf_far.glb", 2) }, 2, rows);

    public static SetSpec Stones(IReadOnlyList<Row> rows) =>
        new("stones", "rock", "stone", "rock", "solid", "none", false, "plants/stones.glb", 12, Array.Empty<(string, int)>(), 0, rows);

    /// <summary>Grass rows spread evenly at random over a rectangle, group 2 every tenth (the example's patch), the rest in none.</summary>
    public static List<Row> GrassRows(int count, Rect2 area, int seed, float height = 0.13f)
    {
        var random = new Random(seed);
        var rows = new List<Row>(count);
        for (var i = 0; i < count; i++)
        {
            var x = area.Position.X + (float)random.NextDouble() * area.Size.X;
            var z = area.Position.Y + (float)random.NextDouble() * area.Size.Y;
            var scale = new Vector3(0.8f + 0.4f * (float)random.NextDouble(), 0.7f + 0.6f * (float)random.NextDouble(), 0.8f + 0.4f * (float)random.NextDouble());
            var tint = new Color(0.75f + 0.25f * (float)random.NextDouble(), 0.85f + 0.15f * (float)random.NextDouble(), 0.6f + 0.3f * (float)random.NextDouble());
            rows.Add(new Row(x, 0, z, -180f + 360f * (float)random.NextDouble(), scale, tint, height * scale.Y, 0, i % 10 == 0 ? 2u : 0u, (uint)random.Next()));
        }
        return rows;
    }

    private static void Save(string path, byte[] bytes)
    {
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write) ?? throw new IOException($"cannot write {path}: {FileAccess.GetOpenError()}");
        file.StoreBuffer(bytes);
    }
}
