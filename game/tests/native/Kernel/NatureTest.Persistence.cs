using Godot;
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native.Kernel;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Nature as room state: the saved block packs every field compactly and reads back to the same bytes, and the strict
/// check refuses each kind of forgery; in a room, nature reloads with its save, a leftover sidecar file from an earlier
/// build is ignored, a forged block refuses the whole save (kept as it was), the simulation saves on a throttle without
/// moving the room revision, runs in fixed steps with a bounded catch-up, and an undo never fails for a full ledger.
/// </summary>
public partial class NatureTest
{
    /// <summary>A state with every kind of field: cells (one burning), a scorched creation, a burned object, a fire, an undo step and an undo.</summary>
    private static NatureState FullNature()
    {
        var state = new NatureState { WeatherS = 123.25, ChangedAt = 5, TrailEnabled = false, TrailPrincipal = "companion", TrailRulesSha256 = new string('a', 64), UndoneAt = 4, UndoneTo = 2 };
        Flowers(state, new Vector3(0.025f, 0, 0.025f), 0.15f, 30, null);
        state.Objects["creation:00000001"] = new NatureObject { Id = "creation:00000001", Creation = true, Burn = 0.5f, SourceHash = new string('b', 64), RepairCost = new() { ["wood"] = 2 } };
        state.Objects["obj:book"] = new NatureObject { Id = "obj:book", Burn = 1, PositionM = new Vector3(0.45f, 0, 0.1f), Rotation = Quaternion.Identity };
        var step = state.BeginUndo("Fire", 5, Companion, "challenge");
        state.Ignite(new Vector3(0.025f, 0, 0.025f), 0.5f, 9.5f, 0.6f, 5, new() { ["creation:00000001"] = 2 }, Soil, step, Companion, Player);
        state.Cells.Values.First(c => c.BurningS <= 0).RestS = 12.5;
        step.Cells["40:40"] = null;
        step.Objects["obj:rug"] = null;
        step.Objects["creation:00000001"] = new NatureObject { Id = "creation:00000001", Creation = true, Burn = 0.25f, SourceHash = new string('b', 64), RepairCost = new() { ["wood"] = 2 } };
        return state;
    }

    private void TestBlock()
    {
        var state = FullNature();
        var block = NatureBlock.Pack(state);
        var text = Canonical(block);
        Check(NatureBlock.Problem(block, 5, _room) == null, "a packed block passes the strict check: " + NatureBlock.Problem(block, 5, _room));
        Check(Canonical(NatureBlock.Pack(NatureBlock.Unpack(block))) == text, "and reads back to the same bytes (values are fixed point, heights float32)");
        Check(Convert.FromBase64String(block["cells"]!.GetValue<string>()).Length == state.Cells.Count * NatureBlock.CellBytes && block["names"]!.AsArray().Count == 2,
            "cells are 32-byte records, their surfaces and materials named once");
        var copy = NatureBlock.Unpack(block);
        Check(copy.Fire!.Revision == 5 && copy.Undo.Single().Cells["40:40"] == null && copy.Undo.Single().Objects["obj:rug"] == null && copy.Objects["obj:book"].BurnedDown &&
            copy.UndoneAt == 4 && copy.Effective(4) == 2, "the fire, the undo step (an absent cell, a created object) and the last undo come back");
        Check(copy.Fire.Principal == Companion && copy.Fire.DirectedBy == Player && copy.Undo.Single().Mode == "challenge" && !copy.Undo.Single().Undoable &&
            copy.Cells.Values.Count(c => c.RestS == 12.5) == 1, "so do who lit the fire and at whose direction, the mode a step was recorded in, and a resting cell's missed time");
        var legacy = Edited(block, b =>
        {
            b["fire"]!.AsObject().Remove("principal");
            b["fire"]!.AsObject().Remove("directed_by");
            b["undo"]![0]!.AsObject().Remove("mode");
        });
        var old = NatureBlock.Problem(legacy, 5, _room) == null ? NatureBlock.Unpack(legacy) : null;
        Check(old != null && old.Fire!.Principal == Player && old.Undo.Single().Mode == "" && !old.Undo.Single().Undoable,
            "a block saved before 13 October still loads: its fire counts as the player's (no one is blamed without proof) and its steps cannot be undone");
        byte[] Cells() => Convert.FromBase64String(block["cells"]!.GetValue<string>());
        JsonObject WithCells(byte[] bytes) { var forged = (JsonObject)block.DeepClone(); forged["cells"] = Convert.ToBase64String(bytes); return forged; }
        var swapped = Cells();
        swapped.AsSpan(0, NatureBlock.CellBytes).ToArray().CopyTo(swapped, NatureBlock.CellBytes);
        var spare = Cells();
        spare[17] = 1;
        var nameIndex = Cells();
        nameIndex[12] = 9;
        var life = Cells();
        life[28] = 0xff;
        life[29] = 0xff;
        var absent = Cells();
        absent[16] = 4;
        var outside = Cells();
        BitConverter.GetBytes(30000).CopyTo(outside, 0);
        var forgeries = new (string Label, Func<JsonNode?> Block)[]
        {
            ("a field it does not have", () => Edited(block, b => b["owner"] = "companion:local")),
            ("another format", () => Edited(block, b => b["format"] = 2)),
            ("another cell size", () => Edited(block, b => b["cell_m"] = 0.1)),
            ("a negative weather clock", () => Edited(block, b => b["weather_s"] = -1)),
            ("a change after the store revision", () => Edited(block, b => b["changed_at"] = 6)),
            ("a trail chosen by a guest", () => Edited(block, b => b["trail"]!["by"] = "guest")),
            ("a hidden character in a name", () => Edited(block, b => b["names"]![0] = "shell:so​il")),
            ("a name listed twice", () => Edited(block, b => b["names"]![1] = b["names"]![0]!.GetValue<string>())),
            ("cells that are not base64", () => Edited(block, b => b["cells"] = "!!!")),
            ("cells in base64 with spaces", () => Edited(block, b => b["cells"] = " " + b["cells"]!.GetValue<string>())),
            ("a cell listed twice", () => WithCells(swapped)),
            ("a spare bit set", () => WithCells(spare)),
            ("a name index past the table", () => WithCells(nameIndex)),
            ("a life past 600 s", () => WithCells(life)),
            ("an absent cell among the cells", () => WithCells(absent)),
            ("a cell outside the room", () => WithCells(outside)),
            ("more cells than the engine allows", () => WithCells(new byte[(NatureState.MaxCells + 1) * NatureBlock.CellBytes])),
            ("burning cells with no fire", () => Edited(block, b => b.Remove("fire"))),
            ("an object not in this room", () => Edited(block, b => b["objects"]!["obj:nothing"] = new JsonObject { ["burn"] = 0.5 })),
            ("a creation without a design hash", () => Edited(block, b => b["objects"]!["creation:00000001"]!["source_sha256"] = "zz")),
            ("a burned object without its pose", () => Edited(block, b => b["objects"]!["obj:book"]!.AsObject().Remove("pose"))),
            ("a fire wider than any island allows", () => Edited(block, b => b["fire"]!["radius_m"] = 2)),
            ("a fire with no revision", () => Edited(block, b => b["fire"]!["revision"] = 0)),
            ("an undo step by an unknown principal", () => Edited(block, b => b["undo"]![0]!["principal"] = "companion:else")),
            ("an undo step that starts after it ends", () => Edited(block, b => b["undo"]![0]!["base"] = 5)),
            ("an undo step recorded in no mode there is", () => Edited(block, b => b["undo"]![0]!["mode"] = "sandbox")),
            ("a fire lit by a stranger", () => Edited(block, b => b["fire"]!["principal"] = "guest:local")),
            ("a fire that names who lit it but not at whose direction", () => Edited(block, b => b["fire"]!.AsObject().Remove("directed_by"))),
            ("a resting cell that is not there", () => Edited(block, b => b["rest"] = Rest(9999, 9999, 1))),
            ("a resting time that is not a number", () => Edited(block, b => b["rest"] = Rest(state.Cells.Values.First().X, state.Cells.Values.First().Z, double.NaN))),
            ("resting cells that are not base64", () => Edited(block, b => b["rest"] = "!!!")),
            ("an undo standing for a later state", () => Edited(block, b => b["undone"]!["state_of"] = 4)),
            ("an array", () => new JsonArray()),
        };
        foreach (var (label, forge) in forgeries)
            Check(NatureBlock.Problem(forge(), 5, _room) != null, "the strict check refuses a block with " + label);
    }

    /// <summary>One resting cell's record, as the block keeps it: x, z and its missed seconds.</summary>
    private static string Rest(int x, int z, double seconds)
    {
        var record = new byte[NatureBlock.RestBytes];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(record, x);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), z);
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(record.AsSpan(8), seconds);
        return Convert.ToBase64String(record);
    }

    private static JsonObject Edited(JsonObject block, Action<JsonObject> edit)
    {
        var copy = (JsonObject)block.DeepClone();
        edit(copy);
        return copy;
    }

    private async Task TestPersistence()
    {
        // Saved with the room: whatever the simulation did since the last command is saved on exit and comes back.
        await NewWorld(SavePath("reload"));
        Check(Ok(Send(Command(NextId("reload-bloom"), "nature.cast", Cast("grow", NearGubble, 0.2, 1)), Player)), "a Grow commits");
        Steps(3);
        var expected = Canonical(NatureBlock.Pack(_host.Nature));
        await NewWorld(SavePath("reload"));
        Check(Canonical(NatureBlock.Pack(_host.Nature)) == expected && _host.Nature.Plants.Count > 0, "nature reloads with the room's save, as the simulation left it on exit");

        // A sidecar nature.json from an earlier test build is never read.
        var sidecar = ProjectSettings.GlobalizePath($"{TestRoot}/sidecar/nature.json");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(sidecar)!);
        System.IO.File.WriteAllText(sidecar, "{\"schema\":\"enfractal.nature_state\",\"cells\":\"broken");
        await NewWorld(SavePath("sidecar"));
        Check(_host.NatureNotice.Length == 0 && Ok(Send(Command(NextId("sidecar-bloom"), "nature.cast", Cast("grow", NearGubble, 0.2, 1)), Player)),
            "a leftover sidecar file breaks nothing");

        // A forged nature block refuses the whole save, which stays as it was.
        await FreeWorld();
        var path = ProjectSettings.GlobalizePath(SavePath("sidecar"));
        var save = JsonNode.Parse(System.IO.File.ReadAllText(path))!.AsObject();
        save["nature"]!["cells"] = "!!!";
        var forged = CanonicalJson.Bytes(save);
        System.IO.File.WriteAllBytes(path, forged);
        await NewWorld(SavePath("sidecar"));
        var refused = Send(Command(NextId("forged-bloom"), "nature.cast", Cast("grow", NearGubble, 0.2, 1)), Player);
        Check(!_host.Authority.Call("is_ready").AsBool() && Code(refused) == "not_ready" && System.IO.File.ReadAllBytes(path).SequenceEqual(forged),
            "a save with a forged nature block is not loaded, refuses nature commands and is kept unchanged");

        await TestThrottle();
        await TestFullLedgerUndo();
    }

    /// <summary>Review major 3 and 4: the simulation's saves never move the revision, come at most every 5 s or when a fire ends, and steps are fixed.</summary>
    private async Task TestThrottle()
    {
        await NewWorld(SavePath("throttle"));
        var writes = 0;
        _host.Authority.Set("persistence_sink", Callable.From((Godot.Collections.Dictionary _) =>
        {
            writes++;
            return new Godot.Collections.Dictionary { ["ok"] = true };
        }));
        Check(Ok(Send(Command(NextId("throttle-grow"), "nature.cast", Cast("grow", NearGubble, 0.2, 1)), Player)) && writes == 1, "a Grow is one write");
        Steps(40);
        Check(writes == 1, "with nothing alive (grown plants stay as they are), twenty seconds of weather write nothing");
        var lit = Fire(Player, new Vector3(0.9f, 0.01f, 1.2f), 0.2, 1, out _);
        var before = writes;
        Steps(3);
        Check(Ok(lit) && _host.Nature.Fire == null && writes == before + 1, "a fire is one write, and its end is saved at once");
        var revision = _host.Revision;
        // The ash it left dries and wets: wait for its first throttled save, then time the next ones from it.
        var synced = writes;
        for (var i = 0; i < 12 && writes == synced; i++) Steps(1);
        before = writes;
        Steps(9);
        var early = writes;
        Steps(1);
        var due = writes;
        Steps(10);
        Check(early == before && due == before + 1 && writes == before + 2, $"the ash it left, drying and wetting, is saved every 5 s, no sooner ({before}, {early}, {due}, {writes})");
        Check(_host.Revision == revision && Ok(Send(Command(NextId("throttle-lock"), "protect.lock", new JsonObject { ["targets"] = new JsonArray("obj:box") }, expectedRevision: revision), Player)),
            "the simulation's saves never move the room revision: a command naming the revision before them commits");
        var clock = _host.Nature.WeatherS;
        _host.AdvanceNature(0.3);
        var none = _host.Nature.WeatherS;
        _host.AdvanceNature(0.3);
        var one = _host.Nature.WeatherS;
        _host.AdvanceNature(10);
        Check(none == clock && one == clock + 0.5 && _host.Nature.WeatherS == clock + 1.5, "whole fixed steps, the remainder kept, and a stall catches up at most two steps");
        await FreeWorld();
    }

    /// <summary>Review blocker 2: undo never fails for a full ledger, for either principal; the oldest receipts are compacted, and still replay.</summary>
    private async Task TestFullLedgerUndo()
    {
        await NewWorld(SavePath("ledger"));
        var envelope = KernelJson.ToJson(_host.Authority.Call("export_envelope"))!.AsObject();
        await FreeWorld();
        var receipts = envelope["receipts"]!.AsObject();
        // One short of the companion's share (the ledger's last 256 places are the player's).
        for (var i = 0; i < 2048 - 256 - 1; i++)
        {
            var action = $"seed-{i:0000}";
            receipts[Player + "|" + action] = new JsonObject
            {
                ["principal"] = Player, ["action_id"] = action, ["fingerprint"] = CanonicalJson.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(action)),
                ["receipt"] = new JsonObject { ["ok"] = true, ["instance_id"] = "", ["revision"] = 0, ["permission_revision"] = 0, ["replayed"] = false, ["affected"] = new JsonArray(), ["created"] = new JsonArray() },
                ["meta"] = new JsonObject { ["op"] = "room.checkpoint", ["at_utc"] = "", ["approved_by"] = "" },
            };
        }
        var path = ProjectSettings.GlobalizePath(SavePath("ledger"));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, CanonicalJson.Bytes(envelope));
        await NewWorld(SavePath("ledger"));
        var bloom = Send(Command(NextId("full-bloom"), "nature.cast", Cast("grow", NearGubble, 0.2, 1)), Companion);
        var another = Send(Command(NextId("full-again"), "nature.cast", Cast("grow", NearGubble, 0.2, 1)), Companion);
        Check(Ok(bloom) && Code(another) == "receipt_limit", "the Gubble's Grow fills its share of the ledger; its next change is refused for capacity");
        var undo = Send(Command(NextId("full-undo"), "room.undo", new JsonObject { ["to_revision"] = _host.Revision - 1 }, expectedRevision: _host.Revision), Companion);
        var oldest = Send(Query("receipt.lookup", new JsonObject { ["action_id"] = "seed-0000" }), Player);
        Check(Ok(undo) && _host.Nature.Plants.Count == 0 && oldest["data"]?["compacted"]?.GetValue<bool>() == true,
            "yet its undo commits: the oldest receipt was compacted to make room: " + Canonical(undo["error"]));
        await FreeWorld();
    }
}
