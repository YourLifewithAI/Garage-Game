using Godot;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GarageGame.Native.Room;

namespace GarageGame.Native.Kernel;

/// <summary>
/// Nature as the authority's save keeps it: the host-owned "nature" block beside the team block, written with the
/// durable nature commands and the simulation's throttled checkpoints, and checked whole when a save loads, because a
/// save is untrusted input (the authority's nature_check calls Problem). Room state carries the same block as
/// x_play_nature.
/// - Cells are sparse: only cells that exist (where nature has acted) are saved, never a grid of the island. Each is one
///   fixed 32-byte record in a base64 string, sorted by (x, z), with its surface and material named through one string
///   table: about 43 characters a cell, so even MaxCells stays near 700 KB of the save's 4 MiB.
/// - Unit values are 16-bit fixed point (moisture, growth, fuel, ash; light to 2; life to 600 s in centiseconds; burning
///   to 60 s in milliseconds); the height is a float32. A value read back packs to the same bits, so a block survives any
///   number of save and load round trips byte for byte.
/// - The undo steps keep their remembered cells the same way, a record flagged absent for a cell that did not exist, and
///   the mode each was recorded in (only Creative's can be undone; a step saved before 13 October has none and cannot).
/// - The fire names who lit it and at whose direction (a fire saved before 13 October has neither: it counts as the player's).
/// - The plants Grow grew or fire burned ("plants", absent while none is): by id, each its factor (above 1, at most PlantLaws.CapFactor)
///   and or its burn (above 0, at most 1; a plant burnt down has no factor), at most MaxGrownPlants grown and MaxBurntPlants burnt or
///   burning, each a copy of this room's plant sets (never a stone). Undo steps remember them the same way, null for none.
/// - The waterpads Grow grew on water ("waterpads", absent while none is; decision 75): in the order grown, each its id (waterpad:n),
///   its leaf's spot and, while it burns, its burn (above 0, below 1: at 1 it is gone), at most NatureState.MaxWaterpads. An undo step
///   that grew or burnt one remembers them all as they were before it (an empty list for none).
/// - Cells resting far from both avatars keep the time they have not been simulated yet ("rest": 16-byte records of x, z
///   and the seconds as a float64, sorted by (x, z)), so a reload catches them up as the running game would have.
/// - Grow's unlocks (decisions 104 to 107), each absent while there is none and remembered by an undo step that changed it (an empty
///   list for none), in the order grown: "sprouts" (each the copy it copies, its root, when it was sown on the island clock in whole
///   seconds of UTC and, while it burns, its burn; at most NatureState.MaxSprouts), "bridges" (each its id, bridge:n, its lips at the
///   deck's height, the void's span along it and its burn; at most NatureState.MaxBridges) and "ivy" (each its id, ivy:n, its base on
///   the face, the face's normal, the ledge it reaches if any and its burn; at most NatureState.MaxIvy). A replanted plant keeps when
///   it was sown ("sown_s", no factor: a seedling's size is its phase on the island clock, never saved).
/// </summary>
public static class NatureBlock
{
    public const int Format = 1;
    public const int CellBytes = 32;
    public const int RestBytes = 16;
    public const int MaxNames = 4096;
    public const int MaxNameLength = 80;
    /// <summary>Saved weather clocks stay below this (about 300 years of play).</summary>
    public const double MaxWeatherS = 1e10;
    /// <summary>The engine's outer limits on any fire (element.ignite in IslandRules.Primitives).</summary>
    public const float MaxFireRadiusM = 1.5f;
    public const float MaxFireS = 60f;

    private const byte FertileFlag = 1;
    private const byte ExposedFlag = 2;
    private const byte AbsentFlag = 4;
    private static readonly Regex Hex64 = new(@"\A[0-9a-f]{64}\z", RegexOptions.Compiled);
    private static readonly Regex ObjectId = new(@"\Aobj:[A-Za-z0-9_-]{1,64}\z", RegexOptions.Compiled);
    private static readonly Regex CreationId = new(@"\Acreation:[A-Za-z0-9_-]{1,64}\z", RegexOptions.Compiled);
    private static readonly Regex MaterialToken = new(@"\A[a-z][a-z0-9_-]{0,39}\z", RegexOptions.Compiled);

    // ---- packing ----

    /// <summary>The block for a state, deterministic: the same state always packs to the same canonical JSON.</summary>
    public static JsonObject Pack(NatureState state)
    {
        var names = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        int Name(string name)
        {
            if (index.TryGetValue(name, out var at)) return at;
            index[name] = names.Count;
            names.Add(name);
            return names.Count - 1;
        }
        var cells = PackCells(state.Cells.Values.Select(c => (c.X, c.Z, (NatureCell?)c)), Name);
        var undo = new JsonArray();
        foreach (var step in state.Undo)
        {
            var remembered = step.Cells.Select(p => { var (x, z) = ParseKey(p.Key); return (x, z, p.Value); });
            undo.Add(new JsonObject
            {
                ["principal"] = step.Principal, ["revision"] = step.Revision, ["base"] = step.Base, ["label"] = step.Label,
                ["cells"] = PackCells(remembered, Name),
                ["objects"] = Objects(step.Objects.Where(p => p.Value != null).Select(p => p.Value!)),
                ["created"] = new JsonArray(step.Objects.Where(p => p.Value == null).Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).Select(k => (JsonNode?)k).ToArray()),
                ["wallet"] = Wallet(step.Wallet),
                ["trail"] = Trail(step.TrailEnabled, step.TrailPrincipal, step.TrailRulesSha256),
                ["mode"] = step.Mode,
            });
            if (step.Plants.Count > 0) undo[^1]!["plants"] = Plants(step.Plants);
            if (step.Waterpads != null) undo[^1]!["waterpads"] = Waterpads(step.Waterpads);
            if (step.Sprouts != null) undo[^1]!["sprouts"] = Sprouts(step.Sprouts);
            if (step.Bridges != null) undo[^1]!["bridges"] = Bridges(step.Bridges);
            if (step.Ivy != null) undo[^1]!["ivy"] = Ivy(step.Ivy);
        }
        var block = new JsonObject
        {
            ["format"] = Format, ["cell_m"] = KernelJson.Num(NatureState.CellM), ["weather_s"] = state.WeatherS, ["changed_at"] = state.ChangedAt,
            ["trail"] = Trail(state.TrailEnabled, state.TrailPrincipal, state.TrailRulesSha256),
            ["names"] = new JsonArray(names.Select(n => (JsonNode?)n).ToArray()),
            ["cells"] = cells, ["objects"] = Objects(state.Objects.Values), ["wallet"] = Wallet(state.Wallet), ["undo"] = undo,
        };
        if (state.Fire is { } fire)
        {
            block["fire"] = new JsonObject
            {
                ["principal"] = fire.Principal, ["directed_by"] = fire.DirectedBy,
                ["center_m"] = KernelJson.Vector(fire.Center), ["radius_m"] = KernelJson.Num(fire.RadiusM), ["remaining_s"] = KernelJson.Num(fire.RemainingS),
                ["intensity"] = KernelJson.Num(fire.Intensity), ["revision"] = fire.Revision,
                ["authorized"] = new JsonObject(fire.AuthorizedCreations.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
            };
        }
        if (state.Plants.Count > 0) block["plants"] = Plants(state.Plants.ToDictionary(p => p.Key, p => (NaturePlant?)p.Value, StringComparer.Ordinal));
        if (state.Waterpads.Count > 0) block["waterpads"] = Waterpads(state.Waterpads);
        if (state.Sprouts.Count > 0) block["sprouts"] = Sprouts(state.Sprouts);
        if (state.Bridges.Count > 0) block["bridges"] = Bridges(state.Bridges);
        if (state.Ivy.Count > 0) block["ivy"] = Ivy(state.Ivy);
        if (state.UndoneAt >= 0) block["undone"] = new JsonObject { ["at"] = state.UndoneAt, ["state_of"] = state.UndoneTo };
        // The Fractals picked up on this island (item.pickup), sorted; absent while none is (every earlier block reads as none).
        if (state.FractalsPicked.Count > 0)
            block["fractals"] = new JsonArray(state.FractalsPicked.OrderBy(id => id, StringComparer.Ordinal).Select(id => (JsonNode?)id).ToArray());
        var resting = state.Cells.Values.Where(c => c.RestS > 0).OrderBy(c => c.X).ThenBy(c => c.Z).ToList();
        if (resting.Count > 0)
        {
            var rest = new byte[resting.Count * RestBytes];
            for (var i = 0; i < resting.Count; i++)
            {
                var record = rest.AsSpan(i * RestBytes, RestBytes);
                BinaryPrimitives.WriteInt32LittleEndian(record, resting[i].X);
                BinaryPrimitives.WriteInt32LittleEndian(record[4..], resting[i].Z);
                BinaryPrimitives.WriteDoubleLittleEndian(record[8..], resting[i].RestS);
            }
            block["rest"] = Convert.ToBase64String(rest);
        }
        return block;
    }

    /// <summary>Plants by id, sorted: each its factor and or burn (null: an undo step's "had no record").</summary>
    private static JsonObject Plants(Dictionary<string, NaturePlant?> plants)
    {
        var map = new JsonObject();
        foreach (var (id, plant) in plants.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (plant == null) { map[id] = null; continue; }
            var saved = new JsonObject();
            if (!plant.Burnt && plant.Factor > 1) saved["factor"] = KernelJson.Num(plant.Factor);
            if (plant.Burn > 0) saved["burn"] = KernelJson.Num(Math.Min(1, plant.Burn));
            if (plant.SownS is { } sown && !plant.Burnt) saved["sown_s"] = sown;
            map[id] = saved;
        }
        return map;
    }

    private static Dictionary<string, NaturePlant?> ReadPlants(JsonObject map) => map.ToDictionary(p => p.Key, p => p.Value is JsonObject saved
        ? new NaturePlant
        {
            Id = p.Key, Factor = saved["factor"] is { } factor ? Float(factor) : saved.ContainsKey("sown_s") ? GrowKindLaws.SeedlingStart : 1,
            Burn = saved["burn"] is { } burn ? Float(burn) : 0, SownS = saved["sown_s"] is { } sown ? Long(sown) : null,
        }
        : null, StringComparer.Ordinal);

    /// <summary>Sprouts in the order grown: the copy each copies, its root, when it was sown and, while it burns, its burn.</summary>
    private static JsonArray Sprouts(IEnumerable<NatureSprout> sprouts) => new(sprouts.Select(sprout =>
    {
        var saved = new JsonObject { ["copy"] = sprout.Template, ["position_m"] = KernelJson.Vector(sprout.PositionM), ["sown_s"] = sprout.SownS };
        if (sprout.Burn > 0) saved["burn"] = KernelJson.Num(Math.Min(1, sprout.Burn));
        return (JsonNode?)saved;
    }).ToArray());

    private static List<NatureSprout> ReadSprouts(JsonArray sprouts) => sprouts.Select(node => node!.AsObject()).Select(saved => new NatureSprout
    {
        Template = saved["copy"]!.GetValue<string>(), PositionM = Vector(saved["position_m"]!), SownS = Long(saved["sown_s"]),
        Burn = saved["burn"] is { } burn ? Float(burn) : 0,
    }).ToList();

    /// <summary>Bridges in the order grown: each its id, its lips, the void's span along it and, while it burns, its burn.</summary>
    private static JsonArray Bridges(IEnumerable<NatureBridge> bridges) => new(bridges.Select(bridge =>
    {
        var saved = new JsonObject
        {
            ["id"] = bridge.Id, ["from_m"] = KernelJson.Vector(bridge.FromM), ["to_m"] = KernelJson.Vector(bridge.ToM),
            ["span_m"] = new JsonArray(KernelJson.Num(bridge.SpanFromM), KernelJson.Num(bridge.SpanToM)),
        };
        if (bridge.Burn > 0) saved["burn"] = KernelJson.Num(Math.Min(1, bridge.Burn));
        return (JsonNode?)saved;
    }).ToArray());

    private static List<NatureBridge> ReadBridges(JsonArray bridges) => bridges.Select(node => node!.AsObject()).Select(saved => new NatureBridge
    {
        Id = saved["id"]!.GetValue<string>(), FromM = Vector(saved["from_m"]!), ToM = Vector(saved["to_m"]!),
        SpanFromM = Float(saved["span_m"]![0]), SpanToM = Float(saved["span_m"]![1]), Burn = saved["burn"] is { } burn ? Float(burn) : 0,
    }).ToList();

    /// <summary>Ivy in the order grown: each its id, its base on the face, the face's normal, the ledge it reaches (if any) and, while it burns, its burn.</summary>
    private static JsonArray Ivy(IEnumerable<NatureIvy> strips) => new(strips.Select(ivy =>
    {
        var saved = new JsonObject { ["id"] = ivy.Id, ["base_m"] = KernelJson.Vector(ivy.BaseM), ["normal"] = KernelJson.Vector(ivy.NormalM) };
        if (ivy.LedgeM is { } ledge) saved["ledge_m"] = KernelJson.Vector(ledge);
        if (ivy.Burn > 0) saved["burn"] = KernelJson.Num(Math.Min(1, ivy.Burn));
        return (JsonNode?)saved;
    }).ToArray());

    private static List<NatureIvy> ReadIvy(JsonArray strips) => strips.Select(node => node!.AsObject()).Select(saved => new NatureIvy
    {
        Id = saved["id"]!.GetValue<string>(), BaseM = Vector(saved["base_m"]!), NormalM = Vector(saved["normal"]!),
        LedgeM = saved["ledge_m"] is { } ledge ? Vector(ledge) : null, Burn = saved["burn"] is { } burn ? Float(burn) : 0,
    }).ToList();

    /// <summary>Waterpads in the order grown: each its id, its leaf's spot and, while it burns, its burn.</summary>
    private static JsonArray Waterpads(IEnumerable<NatureWaterpad> pads) => new(pads.Select(pad =>
    {
        var saved = new JsonObject { ["id"] = pad.Id, ["position_m"] = KernelJson.Vector(pad.PositionM) };
        if (pad.Burn > 0) saved["burn"] = KernelJson.Num(Math.Min(1, pad.Burn));
        return (JsonNode?)saved;
    }).ToArray());

    private static List<NatureWaterpad> ReadWaterpads(JsonArray pads) => pads.Select(node => node!.AsObject()).Select(saved => new NatureWaterpad
    {
        Id = saved["id"]!.GetValue<string>(), PositionM = new Vector3(Float(saved["position_m"]![0]), Float(saved["position_m"]![1]), Float(saved["position_m"]![2])),
        Burn = saved["burn"] is { } burn ? Float(burn) : 0,
    }).ToList();

    private static JsonObject Trail(bool enabled, string principal, string rules) => new() { ["enabled"] = enabled, ["by"] = principal, ["rules_sha256"] = rules };

    private static JsonObject Wallet(Dictionary<string, int> wallet) =>
        new(wallet.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value)));

    private static JsonObject Objects(IEnumerable<NatureObject> items)
    {
        var map = new JsonObject();
        foreach (var item in items.OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            var saved = new JsonObject { ["burn"] = KernelJson.Num(item.Burn) };
            if (item.Creation)
            {
                saved["source_sha256"] = item.SourceHash;
                saved["repair_cost"] = Wallet(item.RepairCost);
            }
            else if (item.PositionM is { } at && item.Rotation is { } turn)
                saved["pose"] = new JsonObject { ["position_m"] = KernelJson.Vector(at), ["rotation"] = new JsonArray(KernelJson.Num(turn.X), KernelJson.Num(turn.Y), KernelJson.Num(turn.Z), KernelJson.Num(turn.W)) };
            // Gathered into the wallet (item.gather), not burnt: gone all the same.
            if (!item.Creation && item.Gathered) saved["gathered"] = true;
            map[item.Id] = saved;
        }
        return map;
    }

    private static string PackCells(IEnumerable<(int X, int Z, NatureCell? Cell)> cells, Func<string, int> name)
    {
        var sorted = cells.OrderBy(c => c.X).ThenBy(c => c.Z).ToList();
        var bytes = new byte[sorted.Count * CellBytes];
        for (var i = 0; i < sorted.Count; i++)
        {
            var record = bytes.AsSpan(i * CellBytes, CellBytes);
            var (x, z, cell) = sorted[i];
            BinaryPrimitives.WriteInt32LittleEndian(record, x);
            BinaryPrimitives.WriteInt32LittleEndian(record[4..], z);
            if (cell == null)
            {
                record[16] = AbsentFlag;
                continue;
            }
            BinaryPrimitives.WriteSingleLittleEndian(record[8..], cell.Y);
            BinaryPrimitives.WriteUInt16LittleEndian(record[12..], (ushort)name(cell.Surface));
            BinaryPrimitives.WriteUInt16LittleEndian(record[14..], (ushort)name(cell.Material));
            record[16] = (byte)((cell.Fertile ? FertileFlag : 0) | (cell.Exposed ? ExposedFlag : 0));
            var values = new[]
            {
                Fixed(cell.Moisture, 1), Fixed(cell.Growth, 1), Fixed(cell.Fuel, 1), Fixed(cell.Ash, 1),
                Fixed(cell.Light, 2), Fixed(cell.LifeS, 600), Fixed(cell.BurningS, 60),
            };
            for (var v = 0; v < values.Length; v++) BinaryPrimitives.WriteUInt16LittleEndian(record[(18 + v * 2)..], values[v]);
        }
        return Convert.ToBase64String(bytes);
    }

    /// <summary>The scale of each fixed-point field: 1, light 2, life 600 s and burning 60 s, each over 0..60000 or 0..65535.</summary>
    private static ushort Fixed(float value, float range) => (ushort)Math.Round(Math.Clamp((double)value, 0, range) * Steps(range) / range);

    private static float Unfixed(ushort bits, float range) => (float)(bits * (double)range / Steps(range));

    /// <summary>Unit values use all 65,535 steps; seconds use 60,000 (centiseconds to 600 s, milliseconds to 60 s).</summary>
    private static double Steps(float range) => range is 600 or 60 ? 60000 : 65535;

    private static (int X, int Z) ParseKey(string key)
    {
        var colon = key.IndexOf(':');
        return (int.Parse(key[..colon], System.Globalization.CultureInfo.InvariantCulture), int.Parse(key[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>A block as strict JSON reads it (whole numbers as integers), however it arrived (a Godot dictionary reads them as floats).</summary>
    public static JsonObject? Normalized(JsonNode? node) => node is JsonObject ? JsonNode.Parse(CanonicalJson.Text(node)) as JsonObject : null;

    // ---- unpacking (only a block Problem passed) ----

    public static NatureState Unpack(JsonNode given)
    {
        var block = Normalized(given)!;
        var names = block["names"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        var state = new NatureState
        {
            WeatherS = block["weather_s"]!.GetValue<double>(), ChangedAt = Int(block["changed_at"]),
            TrailEnabled = block["trail"]!["enabled"]!.GetValue<bool>(), TrailPrincipal = block["trail"]!["by"]!.GetValue<string>(),
            TrailRulesSha256 = block["trail"]!["rules_sha256"]!.GetValue<string>(),
            Cells = new(StringComparer.Ordinal), Objects = ReadObjects(block["objects"]!.AsObject()), Wallet = ReadWallet(block["wallet"]!.AsObject()),
        };
        foreach (var (key, cell) in ReadCells(block["cells"]!.GetValue<string>(), names)) state.Cells[key] = cell!;
        if (block["fire"] is JsonObject fire)
        {
            state.Fire = new NatureFire
            {
                Principal = fire["principal"]?.GetValue<string>() ?? CommandHost.PlayerPrincipal,
                DirectedBy = fire["directed_by"]?.GetValue<string>() ?? fire["principal"]?.GetValue<string>() ?? CommandHost.PlayerPrincipal,
                Center = Vector(fire["center_m"]!), RadiusM = Float(fire["radius_m"]), RemainingS = Float(fire["remaining_s"]), Intensity = Float(fire["intensity"]),
                Revision = Int(fire["revision"]),
                AuthorizedCreations = fire["authorized"]!.AsObject().ToDictionary(p => p.Key, p => Int(p.Value), StringComparer.Ordinal),
            };
        }
        foreach (var node in block["undo"]!.AsArray())
        {
            var saved = node!.AsObject();
            var step = new NatureUndoStep
            {
                Principal = saved["principal"]!.GetValue<string>(), Revision = Int(saved["revision"]), Base = Int(saved["base"]), Label = saved["label"]!.GetValue<string>(),
                Cells = ReadCells(saved["cells"]!.GetValue<string>(), names).ToDictionary(p => p.Key, p => p.Cell, StringComparer.Ordinal),
                Wallet = ReadWallet(saved["wallet"]!.AsObject()),
                TrailEnabled = saved["trail"]!["enabled"]!.GetValue<bool>(), TrailPrincipal = saved["trail"]!["by"]!.GetValue<string>(),
                TrailRulesSha256 = saved["trail"]!["rules_sha256"]!.GetValue<string>(),
                Mode = saved["mode"]?.GetValue<string>() ?? "",
            };
            foreach (var (id, item) in ReadObjects(saved["objects"]!.AsObject())) step.Objects[id] = item;
            foreach (var id in saved["created"]!.AsArray()) step.Objects[id!.GetValue<string>()] = null;
            if (saved["plants"] is JsonObject remembered) step.Plants = ReadPlants(remembered);
            if (saved["waterpads"] is JsonArray rememberedPads) step.Waterpads = ReadWaterpads(rememberedPads);
            if (saved["sprouts"] is JsonArray rememberedSprouts) step.Sprouts = ReadSprouts(rememberedSprouts);
            if (saved["bridges"] is JsonArray rememberedBridges) step.Bridges = ReadBridges(rememberedBridges);
            if (saved["ivy"] is JsonArray rememberedIvy) step.Ivy = ReadIvy(rememberedIvy);
            state.Undo.Add(step);
        }
        if (block["undone"] is JsonObject undone)
        {
            state.UndoneAt = Int(undone["at"]);
            state.UndoneTo = Int(undone["state_of"]);
        }
        if (block["fractals"] is JsonArray picked)
            foreach (var id in picked) state.FractalsPicked.Add(id!.GetValue<string>());
        if (block["plants"] is JsonObject plants)
            foreach (var (id, plant) in ReadPlants(plants)) state.Plants[id] = plant!;
        if (block["waterpads"] is JsonArray pads) state.Waterpads = ReadWaterpads(pads);
        if (block["sprouts"] is JsonArray sprouts) state.Sprouts = ReadSprouts(sprouts);
        if (block["bridges"] is JsonArray bridges) state.Bridges = ReadBridges(bridges);
        if (block["ivy"] is JsonArray strips) state.Ivy = ReadIvy(strips);
        if (block["rest"]?.GetValue<string>() is { } rest)
        {
            var bytes = Convert.FromBase64String(rest);
            for (var i = 0; i < bytes.Length / RestBytes; i++)
            {
                var record = bytes.AsSpan(i * RestBytes, RestBytes);
                state.Cells[NatureState.Key(BinaryPrimitives.ReadInt32LittleEndian(record), BinaryPrimitives.ReadInt32LittleEndian(record[4..]))].RestS =
                    BinaryPrimitives.ReadDoubleLittleEndian(record[8..]);
            }
        }
        return state;
    }

    private static IEnumerable<(string Key, NatureCell? Cell)> ReadCells(string packed, string[] names)
    {
        var bytes = Convert.FromBase64String(packed);
        for (var i = 0; i < bytes.Length / CellBytes; i++)
        {
            var record = bytes.AsSpan(i * CellBytes, CellBytes);
            var x = BinaryPrimitives.ReadInt32LittleEndian(record);
            var z = BinaryPrimitives.ReadInt32LittleEndian(record[4..]);
            if ((record[16] & AbsentFlag) != 0)
            {
                yield return (NatureState.Key(x, z), null);
                continue;
            }
            yield return (NatureState.Key(x, z), new NatureCell
            {
                X = x, Z = z, Y = BinaryPrimitives.ReadSingleLittleEndian(record[8..]),
                Surface = names[BinaryPrimitives.ReadUInt16LittleEndian(record[12..])], Material = names[BinaryPrimitives.ReadUInt16LittleEndian(record[14..])],
                Fertile = (record[16] & FertileFlag) != 0, Exposed = (record[16] & ExposedFlag) != 0,
                Moisture = Unfixed(BinaryPrimitives.ReadUInt16LittleEndian(record[18..]), 1), Growth = Unfixed(BinaryPrimitives.ReadUInt16LittleEndian(record[20..]), 1),
                Fuel = Unfixed(BinaryPrimitives.ReadUInt16LittleEndian(record[22..]), 1), Ash = Unfixed(BinaryPrimitives.ReadUInt16LittleEndian(record[24..]), 1),
                Light = Unfixed(BinaryPrimitives.ReadUInt16LittleEndian(record[26..]), 2), LifeS = Unfixed(BinaryPrimitives.ReadUInt16LittleEndian(record[28..]), 600),
                BurningS = Unfixed(BinaryPrimitives.ReadUInt16LittleEndian(record[30..]), 60),
            });
        }
    }

    private static Dictionary<string, NatureObject> ReadObjects(JsonObject map)
    {
        var objects = new Dictionary<string, NatureObject>(StringComparer.Ordinal);
        foreach (var (id, node) in map)
        {
            var saved = node!.AsObject();
            var item = new NatureObject { Id = id, Creation = id.StartsWith("creation:", StringComparison.Ordinal), Burn = Float(saved["burn"]) };
            if (item.Creation)
            {
                item.SourceHash = saved["source_sha256"]!.GetValue<string>();
                item.RepairCost = ReadWallet(saved["repair_cost"]!.AsObject());
            }
            else if (saved["pose"] is JsonObject pose)
            {
                item.PositionM = Vector(pose["position_m"]!);
                var turn = pose["rotation"]!.AsArray().Select(v => Float(v)).ToArray();
                item.Rotation = new Quaternion(turn[0], turn[1], turn[2], turn[3]);
            }
            item.Gathered = !item.Creation && saved["gathered"]?.GetValueKind() == JsonValueKind.True;
            objects[id] = item;
        }
        return objects;
    }

    private static Dictionary<string, int> ReadWallet(JsonObject map) => map.ToDictionary(p => p.Key, p => Int(p.Value), StringComparer.Ordinal);

    private static int Int(JsonNode? node) => (int)node!.GetValue<double>();

    private static long Long(JsonNode? node) => (long)node!.GetValue<double>();

    private static float Float(JsonNode? node) => (float)node!.GetValue<double>();

    private static Vector3 Vector(JsonNode node) => new(Float(node[0]), Float(node[1]), Float(node[2]));

    // ---- the strict check ----

    /// <summary>
    /// What is wrong with a saved nature block, or null when nothing is. Every field, its type and range, the bounds, and
    /// that each cell record is canonical (sorted, unique, its spare bits clear). storeRevision bounds every revision the
    /// block names. With a room, cells must lie in its bounds and objects must be its own; without one (an earlier
    /// manifest's save, before a migration filters it) only the shape is checked. Never throws.
    /// </summary>
    public static string? Problem(JsonNode? node, int storeRevision, RoomData? room)
    {
        try { return Unchecked(node, storeRevision, room); }
        catch (Exception error)
        {
            return "an unreadable block (" + error.GetType().Name + ")";
        }
    }

    private static string? Unchecked(JsonNode? node, int storeRevision, RoomData? room)
    {
        if (Normalized(node) is not { } block) return "not an object";
        var required = new[] { "format", "cell_m", "weather_s", "changed_at", "trail", "names", "cells", "objects", "wallet", "undo" };
        if (block.Any(p => !required.Contains(p.Key) && p.Key is not ("fire" or "undone" or "rest" or "fractals" or "plants" or "waterpads" or "sprouts" or "bridges" or "ivy")) ||
            required.Any(k => !block.ContainsKey(k))) return "its fields";
        if (block["fractals"] is { } fractals && FractalsProblem(fractals)) return "its Fractals picked up";
        if (block["plants"] is { } plants && PlantsProblem(plants, room, absentAllowed: false) is { } plantsWhy) return "its plants: " + plantsWhy;
        if (block["waterpads"] is { } pads && WaterpadsProblem(pads, room, emptyAllowed: false) is { } padsWhy) return "its waterpads: " + padsWhy;
        if (UnlocksProblem(block, room, emptyAllowed: false) is { } unlocksWhy) return "its " + unlocksWhy;
        if (!Whole(block["format"], Format) || block["format"]!.GetValue<double>() != Format) return "its format";
        if (!Number(block["cell_m"], 1) || (float)block["cell_m"]!.GetValue<double>() != NatureState.CellM) return "its cell size";
        if (!Number(block["weather_s"], MaxWeatherS) || block["weather_s"]!.GetValue<double>() < 0) return "its weather clock";
        if (!Whole(block["changed_at"], storeRevision)) return "its last change";
        if (TrailProblem(block["trail"])) return "its flower trail";
        if (block["names"] is not JsonArray names || names.Count > MaxNames) return "its names";
        var table = new List<string>();
        foreach (var name in names)
        {
            if (name?.GetValueKind() != JsonValueKind.String || name.GetValue<string>() is not { Length: > 0 and <= MaxNameLength } text || KernelText.HasHidden(text)) return "a name";
            table.Add(text);
        }
        if (table.Distinct(StringComparer.Ordinal).Count() != table.Count) return "a name listed twice";
        var cells = CellsProblem(block["cells"], table, room, absentAllowed: false, out var count, out var burning);
        if (cells != null) return "its cells: " + cells;
        if (block["rest"] is { } rest && RestProblem(rest, block["cells"]!.GetValue<string>()) is { } restWhy) return "its resting cells: " + restWhy;
        if (count > NatureState.MaxCells || burning > NatureState.MaxBurning) return "more cells than the engine allows";
        if (block["objects"] is not JsonObject objects || objects.Count > NatureState.MaxObjects) return "its objects";
        foreach (var (id, item) in objects)
            if (ObjectProblem(id, item, room) is { } why) return "an object: " + why;
        if (WalletProblem(block["wallet"])) return "its wallet";
        if (block["fire"] is { } fire && FireProblem(fire, storeRevision, room) is { } fireWhy) return "its fire: " + fireWhy;
        if (block["fire"] == null && burning > 0) return "burning cells with no fire";
        if (block["undo"] is not JsonArray undo || undo.Count > NatureState.MaxUndoSteps) return "its undo steps";
        var previous = -1;
        var remembered = 0;
        foreach (var stepNode in undo)
        {
            // A step saved before 13 October has no mode (and cannot be undone); a newer one says which mode recorded it.
            if (stepNode is not JsonObject step || step.Count != 9 + new[] { "mode", "plants", "waterpads", "sprouts", "bridges", "ivy" }.Count(step.ContainsKey) ||
                new[] { "principal", "revision", "base", "label", "cells", "objects", "created", "wallet", "trail" }.Any(k => !step.ContainsKey(k)))
                return "an undo step's fields";
            if (step["mode"] is { } mode && (mode.GetValueKind() != JsonValueKind.String || !PlayerModesProfile.ValidMode(mode.GetValue<string>()))) return "an undo step's mode";
            if (step["principal"]?.GetValueKind() != JsonValueKind.String || step["principal"]!.GetValue<string>() is not (CommandHost.PlayerPrincipal or CommandHost.CompanionPrincipal))
                return "an undo step's principal";
            if (!Whole(step["revision"], storeRevision) || !Whole(step["base"], storeRevision)) return "an undo step's revisions";
            var revision = (int)step["revision"]!.GetValue<double>();
            if (revision <= previous || revision < 1 || step["base"]!.GetValue<double>() >= revision) return "the order of the undo steps";
            previous = revision;
            if (step["label"]?.GetValueKind() != JsonValueKind.String || step["label"]!.GetValue<string>() is not { Length: > 0 and <= 40 } label || KernelText.HasHidden(label)) return "an undo step's label";
            if (CellsProblem(step["cells"], table, room: null, absentAllowed: true, out var stepCells, out _) is { } stepWhy) return "an undo step's cells: " + stepWhy;
            remembered += stepCells;
            if (step["objects"] is not JsonObject stepObjects || stepObjects.Count > NatureState.MaxObjects) return "an undo step's objects";
            foreach (var (id, item) in stepObjects)
                if (ObjectProblem(id, item, room) is { } why) return "an undo step's object: " + why;
            if (step["created"] is not JsonArray created || created.Count > NatureState.MaxObjects ||
                created.Any(c => c?.GetValueKind() != JsonValueKind.String || !(ObjectId.IsMatch(c.GetValue<string>()) || CreationId.IsMatch(c.GetValue<string>())) || stepObjects.ContainsKey(c.GetValue<string>())) ||
                created.Select(c => c!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() != created.Count) return "an undo step's created objects";
            if (WalletProblem(step["wallet"]) || TrailProblem(step["trail"])) return "an undo step's wallet or trail";
            if (step["plants"] is { } stepPlants && PlantsProblem(stepPlants, room, absentAllowed: true) is { } stepPlantsWhy) return "an undo step's plants: " + stepPlantsWhy;
            if (step["waterpads"] is { } stepPads && WaterpadsProblem(stepPads, room, emptyAllowed: true) is { } stepPadsWhy) return "an undo step's waterpads: " + stepPadsWhy;
            if (UnlocksProblem(step, room, emptyAllowed: true) is { } stepUnlocksWhy) return "an undo step's " + stepUnlocksWhy;
        }
        if (remembered > NatureState.MaxUndoCells + NatureState.MaxCells) return "undo steps that remember too many cells";
        if (block["undone"] is { } undone && (undone is not JsonObject u || u.Count != 2 || !Whole(u["at"], storeRevision) || !Whole(u["state_of"], storeRevision) ||
            u["state_of"]!.GetValue<double>() >= u["at"]!.GetValue<double>())) return "its last undo";
        return null;
    }

    private static string? CellsProblem(JsonNode? node, List<string> names, RoomData? room, bool absentAllowed, out int count, out int burning)
    {
        count = 0;
        burning = 0;
        if (node?.GetValueKind() != JsonValueKind.String) return "not a string";
        var bytes = Convert.FromBase64String(node.GetValue<string>());
        if (bytes.Length % CellBytes != 0 || bytes.Length / CellBytes > NatureState.MaxCells) return "not whole records";
        if (Convert.ToBase64String(bytes) != node.GetValue<string>()) return "not canonical base64";
        count = bytes.Length / CellBytes;
        long previous = long.MinValue;
        for (var i = 0; i < count; i++)
        {
            var record = bytes.AsSpan(i * CellBytes, CellBytes);
            var x = BinaryPrimitives.ReadInt32LittleEndian(record);
            var z = BinaryPrimitives.ReadInt32LittleEndian(record[4..]);
            if (Math.Abs((long)x) > 20000 || Math.Abs((long)z) > 20000) return "a cell beyond 1,000 m";
            var order = (long)x * 100000 + z;
            if (order <= previous) return "cells out of order or listed twice";
            previous = order;
            var flags = record[16];
            if (record[17] != 0 || (flags & ~(FertileFlag | ExposedFlag | AbsentFlag)) != 0) return "spare bits set";
            if ((flags & AbsentFlag) != 0)
            {
                if (!absentAllowed) return "an absent cell";
                if (flags != AbsentFlag || record[8..16].IndexOfAnyExcept((byte)0) >= 0 || record[18..].IndexOfAnyExcept((byte)0) >= 0) return "an absent cell with values";
                continue;
            }
            var y = BinaryPrimitives.ReadSingleLittleEndian(record[8..]);
            if (!float.IsFinite(y) || Math.Abs(y) > 1000) return "a height";
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[12..]) >= names.Count || BinaryPrimitives.ReadUInt16LittleEndian(record[14..]) >= names.Count) return "a name index";
            var material = names[BinaryPrimitives.ReadUInt16LittleEndian(record[14..])];
            if (!MaterialToken.IsMatch(material)) return "a material";
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[28..]) > 60000 || BinaryPrimitives.ReadUInt16LittleEndian(record[30..]) > 60000) return "a life or burn time";
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[30..]) > 0) burning++;
            if (room != null)
            {
                var at = new Vector3((x + 0.5f) * NatureState.CellM, y, (z + 0.5f) * NatureState.CellM);
                if (!room.Bounds.Grow(NatureState.CellM).HasPoint(at)) return "a cell outside the room";
            }
        }
        return null;
    }

    private static string? ObjectProblem(string id, JsonNode? node, RoomData? room)
    {
        if (node is not JsonObject item || !Number(item["burn"], 1) || item["burn"]!.GetValue<double>() is < 0 or > 1) return "its burn";
        if (CreationId.IsMatch(id))
        {
            if (item.Count != 3 || item["source_sha256"]?.GetValueKind() != JsonValueKind.String || !Hex64.IsMatch(item["source_sha256"]!.GetValue<string>())) return "a creation's design hash";
            if (item["repair_cost"] is not JsonObject cost || cost.Count > NatureState.MaxWalletKinds ||
                cost.Any(p => !MaterialToken.IsMatch(p.Key) || !Whole(p.Value, NatureState.MaxWalletUnits) || p.Value!.GetValue<double>() < 1)) return "a creation's repair quote";
            return null;
        }
        if (!ObjectId.IsMatch(id) || (room != null && room.Objects.All(o => o.Id != id))) return "an object not in this room";
        var burnedDown = item["burn"]!.GetValue<double>() >= 1;
        // A gathered object (item.gather) is gone as a burnt one is, and says so: "gathered" is true, and only on one gone.
        var gathered = item.ContainsKey("gathered");
        if (gathered && (!burnedDown || item["gathered"]?.GetValueKind() != JsonValueKind.True)) return "a gathered object's mark";
        if (item.Count != (burnedDown ? 2 : 1) + (gathered ? 1 : 0)) return "a stock object's fields";
        if (!burnedDown) return null;
        if (item["pose"] is not JsonObject pose || pose.Count != 2 || !Vec3(pose["position_m"]) || pose["rotation"] is not JsonArray q || q.Count != 4 || q.Any(v => !Number(v, 1)) ||
            Math.Abs(Math.Sqrt(q.Sum(v => v!.GetValue<double>() * v.GetValue<double>())) - 1) > 1e-3) return "a burned object's pose";
        if (room != null && !room.Bounds.Grow(0.01f).HasPoint(Vector(pose["position_m"]!))) return "a burned object outside the room";
        return null;
    }

    /// <summary>The resting cells' record: whole, canonical, sorted and unique, each an existing cell, each time finite and positive.</summary>
    private static string? RestProblem(JsonNode node, string packedCells)
    {
        if (node.GetValueKind() != JsonValueKind.String) return "not a string";
        var bytes = Convert.FromBase64String(node.GetValue<string>());
        if (bytes.Length == 0 || bytes.Length % RestBytes != 0 || bytes.Length / RestBytes > NatureState.MaxCells) return "not whole records";
        if (Convert.ToBase64String(bytes) != node.GetValue<string>()) return "not canonical base64";
        var cells = Convert.FromBase64String(packedCells);
        var keys = new HashSet<long>();
        for (var i = 0; i < cells.Length / CellBytes; i++)
            keys.Add((long)BinaryPrimitives.ReadInt32LittleEndian(cells.AsSpan(i * CellBytes)) * 100000 + BinaryPrimitives.ReadInt32LittleEndian(cells.AsSpan(i * CellBytes + 4)));
        long previous = long.MinValue;
        for (var i = 0; i < bytes.Length / RestBytes; i++)
        {
            var record = bytes.AsSpan(i * RestBytes, RestBytes);
            var order = (long)BinaryPrimitives.ReadInt32LittleEndian(record) * 100000 + BinaryPrimitives.ReadInt32LittleEndian(record[4..]);
            if (order <= previous) return "out of order or listed twice";
            previous = order;
            if (!keys.Contains(order)) return "a cell that is not there";
            var seconds = BinaryPrimitives.ReadDoubleLittleEndian(record[8..]);
            if (!double.IsFinite(seconds) || seconds <= 0 || seconds > MaxWeatherS) return "a time";
        }
        return null;
    }

    private static string? FireProblem(JsonNode node, int storeRevision, RoomData? room)
    {
        // A fire saved before 13 October has six fields; a newer one names who lit it and at whose direction.
        if (node is not JsonObject fire || fire.Count != (fire.ContainsKey("principal") ? 8 : 6) || !Vec3(fire["center_m"])) return "its centre";
        if (fire.ContainsKey("principal") && (fire["principal"]?.GetValueKind() != JsonValueKind.String || fire["principal"]!.GetValue<string>() is not (CommandHost.PlayerPrincipal or CommandHost.CompanionPrincipal) ||
            fire["directed_by"]?.GetValueKind() != JsonValueKind.String || fire["directed_by"]!.GetValue<string>() is not (CommandHost.PlayerPrincipal or CommandHost.CompanionPrincipal)))
            return "who lit it";
        if (!Number(fire["radius_m"], MaxFireRadiusM) || fire["radius_m"]!.GetValue<double>() <= 0) return "its radius";
        if (!Number(fire["remaining_s"], MaxFireS) || fire["remaining_s"]!.GetValue<double>() < 0) return "its time";
        if (!Number(fire["intensity"], 2) || fire["intensity"]!.GetValue<double>() < 0.05) return "its intensity";
        if (!Whole(fire["revision"], storeRevision) || fire["revision"]!.GetValue<double>() < 1) return "its revision";
        if (fire["authorized"] is not JsonObject authorized || authorized.Count > 64 || authorized.Any(p => !CreationId.IsMatch(p.Key) || !Whole(p.Value) || p.Value!.GetValue<double>() < 1))
            return "the creations it may scorch";
        if (room != null && !room.Bounds.Grow(0.02f).HasPoint(Vector(fire["center_m"]!))) return "a fire outside the room";
        return null;
    }

    private static bool TrailProblem(JsonNode? node) =>
        node is not JsonObject trail || trail.Count != 3 || trail["enabled"]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False) ||
        trail["by"]?.GetValueKind() != JsonValueKind.String || trail["by"]!.GetValue<string>() is not ("player" or "companion") ||
        trail["rules_sha256"]?.GetValueKind() != JsonValueKind.String || trail["rules_sha256"]!.GetValue<string>() is var rules && rules.Length != 0 && !Hex64.IsMatch(rules);

    private static readonly Regex PlantIdPattern = new(@"\Aplant:[a-z][a-z0-9_]{0,31}-(0|[1-9][0-9]{0,7})\z", RegexOptions.Compiled);

    /// <summary>
    /// The plants: 1 or more ids of one spelling, each a plant or tree of this room's copies (with a room), each holding only a factor
    /// (above 1, at most PlantLaws.CapFactor) and or a burn (above 0, at most 1; none at 1 holds a factor), at most MaxGrownPlants
    /// grown and MaxBurntPlants burnt or burning. An undo step's may name a plant with null (it had no record).
    /// </summary>
    private static string? PlantsProblem(JsonNode node, RoomData? room, bool absentAllowed)
    {
        if (node is not JsonObject plants || plants.Count is < 1 or > NatureState.MaxGrownPlants + NatureState.MaxBurntPlants) return "not 1 to the most the island keeps";
        int grown = 0, burning = 0, seedlings = 0;
        foreach (var (id, value) in plants)
        {
            if (!PlantIdPattern.IsMatch(id)) return "an id";
            if (room != null && (room.Copies is not { } copies || !copies.TryResolve(id, out var set, out _) || set.Kind == PlantKind.Stone)) return "a plant not in this room";
            if (value == null)
            {
                if (!absentAllowed) return "a plant with no record";
                continue;
            }
            if (value is not JsonObject plant || plant.Count is < 1 or > 2 || plant.Any(p => p.Key is not ("factor" or "burn" or "sown_s"))) return "a plant's fields";
            if (plant["factor"] is { } factor && (!Number(factor, PlantLaws.CapFactor) || factor.GetValue<double>() <= 1)) return "a plant's size";
            if (plant["burn"] is { } burn && (!Number(burn, 1) || burn.GetValue<double>() <= 0)) return "a plant's burn";
            if (plant["burn"] is { } whole && whole.GetValue<double>() >= 1 && plant.ContainsKey("factor")) return "a burnt plant's size";
            // A replanted seedling (decision 105): when it was sown, never with a size (its phase is the clock's) nor burnt down.
            if (plant["sown_s"] is { } sown && (!Whole(sown, GrowKindLaws.MaxSownS) || plant.ContainsKey("factor") || plant["burn"] is { } gone && gone.GetValue<double>() >= 1))
                return "a seedling's sowing";
            if (plant.ContainsKey("burn")) burning++;
            else if (plant.ContainsKey("sown_s")) seedlings++;
            else grown++;
        }
        return grown > NatureState.MaxGrownPlants || burning > NatureState.MaxBurntPlants || seedlings > NatureState.MaxBurntPlants ? "more plants than the island keeps" : null;
    }

    private static readonly Regex BridgeIdPattern = new(@"\Abridge:[1-9][0-9]{0,2}\z", RegexOptions.Compiled);
    private static readonly Regex IvyIdPattern = new(@"\Aivy:[1-9][0-9]{0,2}\z", RegexOptions.Compiled);

    /// <summary>
    /// Grow's unlocks in a block or an undo step (decisions 104 to 107): each list 1 to its most (an undo step's may be empty: it
    /// remembers the island had none), each entry its fields only, its places in this room's bounds (with a room), and only while it
    /// burns a burn above 0 and below 1. A sprout copies a grass or flower copy of this room; a sowing is a whole number of seconds; a
    /// bridge is level, 0.4 to 1.2 m long, its span inside it; an ivy strip's normal is a unit vector at least 70 degrees steep.
    /// </summary>
    private static string? UnlocksProblem(JsonObject holder, RoomData? room, bool emptyAllowed)
    {
        bool Inside(JsonNode? node, float margin) => Vec3(node) && (room == null || room.Bounds.Grow(margin).HasPoint(Vector(node!)));
        bool Burn(JsonObject entry) => entry["burn"] is not { } burn || Number(burn, 1) && burn.GetValue<double>() > 0 && burn.GetValue<double>() < 1;
        bool Count(JsonNode? node, int most) => node is JsonArray list && list.Count <= most && (list.Count > 0 || emptyAllowed);
        if (holder["sprouts"] is { } sprouts)
        {
            if (!Count(sprouts, NatureState.MaxSprouts)) return "sprouts: not 1 to the most the island keeps";
            foreach (var value in sprouts.AsArray())
            {
                if (value is not JsonObject sprout || sprout.Any(p => p.Key is not ("copy" or "position_m" or "sown_s" or "burn")) ||
                    !sprout.ContainsKey("copy") || !sprout.ContainsKey("position_m") || !sprout.ContainsKey("sown_s")) return "sprouts: a sprout's fields";
                if (sprout["copy"]?.GetValueKind() != JsonValueKind.String || !PlantIdPattern.IsMatch(sprout["copy"]!.GetValue<string>()) ||
                    (room != null && (room.Copies is not { } copies || !copies.TryResolve(sprout["copy"]!.GetValue<string>(), out var set, out _) || !CommandHost.SproutSet(set))))
                    return "sprouts: a copy that is not this room's grass or flowers";
                if (!Inside(sprout["position_m"], 0.05f)) return "sprouts: a sprout outside the island";
                if (!Whole(sprout["sown_s"], GrowKindLaws.MaxSownS)) return "sprouts: a sprout's sowing";
                if (!Burn(sprout)) return "sprouts: a sprout's burn";
            }
        }
        if (holder["bridges"] is { } bridges)
        {
            if (!Count(bridges, NatureState.MaxBridges)) return "bridges: not 1 to the most the island keeps";
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in bridges.AsArray())
            {
                if (value is not JsonObject bridge || bridge.Any(p => p.Key is not ("id" or "from_m" or "to_m" or "span_m" or "burn")) ||
                    new[] { "id", "from_m", "to_m", "span_m" }.Any(k => !bridge.ContainsKey(k))) return "bridges: a bridge's fields";
                if (bridge["id"]?.GetValueKind() != JsonValueKind.String || !BridgeIdPattern.IsMatch(bridge["id"]!.GetValue<string>()) || !ids.Add(bridge["id"]!.GetValue<string>()))
                    return "bridges: a bridge's id";
                if (!Inside(bridge["from_m"], 0.05f) || !Inside(bridge["to_m"], 0.05f)) return "bridges: a bridge outside the island";
                var from = Vector(bridge["from_m"]!);
                var to = Vector(bridge["to_m"]!);
                var length = from.DistanceTo(to);
                if (Math.Abs(from.Y - to.Y) > 1e-3f || length < GrowKindLaws.BridgeMinM - 1e-3f || length > GrowKindLaws.BridgeMaxM + 1e-3f) return "bridges: a bridge's lips";
                if (bridge["span_m"] is not JsonArray span || span.Count != 2 || !span.All(v => Number(v, 10)) || span[0]!.GetValue<double>() < 0 ||
                    span[1]!.GetValue<double>() <= span[0]!.GetValue<double>() || span[1]!.GetValue<double>() > length + 1e-3) return "bridges: a bridge's span";
                if (!Burn(bridge)) return "bridges: a bridge's burn";
            }
        }
        if (holder["ivy"] is { } strips)
        {
            if (!Count(strips, NatureState.MaxIvy)) return "ivy: not 1 to the most the island keeps";
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in strips.AsArray())
            {
                if (value is not JsonObject ivy || ivy.Any(p => p.Key is not ("id" or "base_m" or "normal" or "ledge_m" or "burn")) ||
                    new[] { "id", "base_m", "normal" }.Any(k => !ivy.ContainsKey(k))) return "ivy: a strip's fields";
                if (ivy["id"]?.GetValueKind() != JsonValueKind.String || !IvyIdPattern.IsMatch(ivy["id"]!.GetValue<string>()) || !ids.Add(ivy["id"]!.GetValue<string>()))
                    return "ivy: a strip's id";
                if (!Inside(ivy["base_m"], 0.05f) || (ivy.ContainsKey("ledge_m") && !Inside(ivy["ledge_m"], 0.05f))) return "ivy: a strip outside the island";
                if (!Vec3(ivy["normal"]) || Vector(ivy["normal"]!) is var normal && (Math.Abs(normal.Length() - 1) > 1e-3f || Math.Abs(normal.Y) > GrowKindLaws.IvyMaxNormalY + 1e-3f))
                    return "ivy: a strip's wall";
                if (!Burn(ivy)) return "ivy: a strip's burn";
            }
        }
        return null;
    }

    private static readonly Regex WaterpadIdPattern = new(@"\Awaterpad:[1-9][0-9]{0,2}\z", RegexOptions.Compiled);

    /// <summary>
    /// The waterpads (decision 75): 1 to NatureState.MaxWaterpads (an undo step's may be none: it remembers the island had none), each
    /// once by id, its spot in this room's bounds (with a room), and only while it burns a burn above 0 and below 1 (at 1 it is gone).
    /// </summary>
    private static string? WaterpadsProblem(JsonNode node, RoomData? room, bool emptyAllowed)
    {
        if (node is not JsonArray pads || pads.Count > NatureState.MaxWaterpads || (pads.Count == 0 && !emptyAllowed)) return "not 1 to the most the island keeps";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in pads)
        {
            if (value is not JsonObject pad || pad.Any(p => p.Key is not ("id" or "position_m" or "burn")) || !pad.ContainsKey("id") || !pad.ContainsKey("position_m")) return "a waterpad's fields";
            if (pad["id"]?.GetValueKind() != JsonValueKind.String || !WaterpadIdPattern.IsMatch(pad["id"]!.GetValue<string>()) || !ids.Add(pad["id"]!.GetValue<string>())) return "a waterpad's id";
            if (!Vec3(pad["position_m"])) return "a waterpad's spot";
            var at = new Vector3((float)pad["position_m"]![0]!.GetValue<double>(), (float)pad["position_m"]![1]!.GetValue<double>(), (float)pad["position_m"]![2]!.GetValue<double>());
            if (room != null && !room.Bounds.Grow(0.05f).HasPoint(at)) return "a waterpad outside the island";
            if (pad["burn"] is { } burn && (!Number(burn, 1) || burn.GetValue<double>() <= 0 || burn.GetValue<double>() >= 1)) return "a waterpad's burn";
        }
        return null;
    }

    private static readonly Regex FractalIdPattern = new(@"\Afractal:[a-z0-9]{1,32}\z", RegexOptions.Compiled);

    /// <summary>The Fractals picked up: 1 to 1000 ids of one spelling, sorted and each once.</summary>
    private static bool FractalsProblem(JsonNode node)
    {
        if (node is not JsonArray ids || ids.Count is < 1 or > CommandHost.MaxFractals) return true;
        string? previous = null;
        foreach (var id in ids)
        {
            if (id?.GetValueKind() != JsonValueKind.String || id.GetValue<string>() is not { } text || !FractalIdPattern.IsMatch(text)) return true;
            if (previous != null && string.CompareOrdinal(previous, text) >= 0) return true;
            previous = text;
        }
        return false;
    }

    private static bool WalletProblem(JsonNode? node) =>
        node is not JsonObject wallet || wallet.Count > NatureState.MaxWalletKinds || wallet.Any(p => !MaterialToken.IsMatch(p.Key) || !Whole(p.Value, NatureState.MaxWalletUnits));

    private static bool Number(JsonNode? node, double limit) =>
        node?.GetValueKind() == JsonValueKind.Number && node.GetValue<double>() is var d && double.IsFinite(d) && Math.Abs(d) <= limit;

    private static bool Whole(JsonNode? node, double max = int.MaxValue) => Number(node, double.MaxValue) && node!.GetValue<double>() is var d && d >= 0 && d <= max && d == Math.Floor(d);

    private static bool Vec3(JsonNode? node) => node is JsonArray a && a.Count == 3 && a.All(v => Number(v, 1000));
}
