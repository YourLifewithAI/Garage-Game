using Godot;
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// P-Inventory-2's logs and stones (docs/game/MECHANICS.md decision 116 and the founder's refinement of 25 October): a loose log or stone
/// is gathered straight into its stack (not carried), repairs spend the stacks (the pockets first, then the chest), and an older save's
/// wallet becomes the chest's stacks on load. The logs and the stone lie on the floor in the far corner (InventoryTest.InventoryRoom).
/// </summary>
public partial class InventoryTest
{
    /// <summary>Beside a log on the floor in the far corner (the second's spot reaches the stone too; the others reach nothing else).</summary>
    private static Vector3 ByLog(int n) => new(1.6f, 0, n switch { 1 => -1.35f, 2 => -0.9f, _ => -0.4f });

    private JsonObject Gather(string principal, string target, bool preview = false) =>
        Send(Command(NextId("gather"), "item.gather", new JsonObject { ["target"] = target }, preview), principal);

    /// <summary>
    /// v5's kinds; a log within reach goes into the pockets' log stack at once, a held one too; a stone into its own; the refusals (the
    /// companion first, a thing that is no log or stone, out of reach, the hands full, a full stack or full pockets leaving it where it is,
    /// a pack without the inventory); F on a log gathers it; player.status names the material.
    /// </summary>
    private async Task TestLogsAndStones()
    {
        await NewWorld("logs");
        var rules = _host.InventoryRules!;
        Check(rules.Kind("log") is { Material: "wood", Stack: 99, Name: "Log", Plural: "Logs" } && rules.Kind("stone") is { Material: "stone", Stack: 99 } &&
              rules.KindOfMaterial("wood")?.Id == "log" && rules.KindOfMaterial("stone")?.Id == "stone" && rules.KindOfMaterial("copper") == null,
            "v5's logs and stones are stacks of 99, a log wood and a stone stone (decision 116); nothing is made of copper yet");
        await Stand(_player, ByLog(2), "beside the second log and the stone");
        var revision = _host.Revision;
        Check(Refused(Gather(Companion, "obj:log_2"), "permission_denied", "Only the player can do that, directly in the game.", "$.op") && _host.Revision == revision,
            "the companion never gathers: refused at $.op before anything is looked up");
        Check(Refused(Gather(Player, "obj:cup"), "request_invalid", CommandHost.GatherOnlyLogsAndStones, "$.args.target"), "the cup is no log or stone");
        Check(Code(Gather(Player, "obj:log_1")) == "out_of_bounds" && EntityIds().Contains("obj:log_1"), "the first log is out of reach from here: it stays where it is");
        var preview = Gather(Player, "obj:log_2", preview: true);
        Check(Ok(preview) && _host.Revision == revision && Units(_host.PocketEntries, "log") == 0, "a preview gathers nothing");
        var gather = Command("gather-log-2", "item.gather", new JsonObject { ["target"] = "obj:log_2" });
        var gathered = Send(gather, Player);
        Check(Ok(gathered) && gathered["transient"]?.GetValue<bool>() == false && _host.Revision == revision + 1 && gathered["data"] == null &&
              Canonical(gathered["affected"]) == Canonical(new JsonArray("obj:log_2")),
            "a log within reach is gathered as before: one durable room revision, the log in affected, no data: " + Canonical(gathered));
        Check(Units(_host.PocketEntries, "log") == 1 && _host.HeldBy(PlayerAvatar) == null && !EntityIds().Contains("obj:log_2") && _host.Nature.Objects["obj:log_2"] is { Gathered: true },
            "not a carry: it joins the pockets' log stack at once and has left the island");
        var fact = Send(Query("journal.read", new JsonObject { ["kind"] = "gathered" }), Player)["data"]?["entries"]?.AsArray().LastOrDefault();
        Check(fact?["line"]?.GetValue<string>() == "You gathered \"Log\": 1 log in your pockets." && fact["quantity"]?["count"]?.GetValue<int>() == 1,
            "the journal says so in the pockets' words: " + Canonical(fact));
        Check(Send(gather, Player)["replayed"]?.GetValue<bool>() == true && Units(_host.PocketEntries, "log") == 1, "a retry replays and counts nothing twice");
        Check(Ok(Gather(Player, "obj:stone_1")) && Units(_host.PocketEntries, "stone") == 1 && _host.PocketEntries.Count == 2, "the stone joins a stack of its own");
        await Stand(_player, ByLog(3), "beside the third log");
        Check(Ok(Send(Command(NextId("grab"), "entity.grab", new JsonObject { ["target"] = "obj:log_3" }), Player)) && _host.HeldBy(PlayerAvatar) == "obj:log_3",
            "a log can still be held when an entity.grab names it (the Gubble's hands, a direct command)");
        await Stand(_player, ByLog(1), "beside the first log, the third in hand");
        _player.Rotation = new Vector3(0, -Mathf.Pi / 2, 0);
        Check(Refused(Gather(Player, "obj:log_1"), "target_busy", "This avatar is already holding something. Put it down first.", "$.args.target"),
            "with the hands full another log waits");
        Check(Ok(Gather(Player, "obj:log_3")) && Units(_host.PocketEntries, "log") == 2 && _host.HeldBy(PlayerAvatar) == null, "the held log is gathered from the hand");
        var hands = _host.PlayerHands();
        Check(hands.Ok && hands.Message == "Log is in your pockets: 3 logs." && Units(_host.PocketEntries, "log") == 3 && _host.HeldBy(PlayerAvatar) == null && !EntityIds().Contains("obj:log_1"),
            "F facing a log gathers it into its stack, never into the hand: " + hands.Message);
        var status = Status()["pockets"]?["entries"]?.AsArray();
        Check(status?.Any(e => e?["item"]?.GetValue<string>() == "log" && e["material"]?.GetValue<string>() == "wood" && e["count"]?.GetValue<int>() == 3) == true &&
              status.Any(e => e?["item"]?.GetValue<string>() == "stone" && e["material"]?.GetValue<string>() == "stone") && Status()["wallet"] == null,
            "player.status lists the stacks with their materials, and no wallet: " + Canonical(status));

        await NewWorld("logs-full-stack", editPack: inventory => inventory["kinds"]!.AsArray().First(k => k!["id"]!.GetValue<string>() == "log")!["stack"] = 1);
        await Stand(_player, ByLog(2), "beside the second log");
        Check(Ok(Gather(Player, "obj:log_2")), "a stack of one log fills");
        await Stand(_player, ByLog(3), "beside the third log");
        var full = Gather(Player, "obj:log_3");
        Check(Refused(full, "occupied", "A stack holds at most 1 Log.", "$.args") && EntityIds().Contains("obj:log_3") && _host.HeldBy(PlayerAvatar) == null &&
              Units(_host.PocketEntries, "log") == 1, "a full stack refuses in the pockets' words, and the log stays where it is: " + Message(full));
        await NewWorld("logs-full-pockets", editPack: inventory => inventory["pockets"]!["entries"] = 1);
        await Stand(_player, ByLog(2), "beside the second log and the stone");
        Check(Ok(Gather(Player, "obj:log_2")), "the one slot takes a log");
        var slots = Gather(Player, "obj:stone_1");
        Check(Refused(slots, "occupied", "Your pockets are full: one thing.", "$.args") && EntityIds().Contains("obj:stone_1"), "full pockets refuse the stone, which stays where it is");
        _host.LoadRules(RoomWorld.DefaultRulesId, 4);
        Check(Refused(Gather(Player, "obj:stone_1"), "unsupported_capability", CommandHost.InventoryNotHere, "$.args.target"), "a pack without the inventory has no pockets to gather into");
    }

    /// <summary>A creation for the repair's cost: three wooden parts and one of stone (its quote is 3 wood and 1 stone).</summary>
    private string BuildShed(double x, double z)
    {
        JsonObject Part(string id, double dx, double dz, string material) => new()
        {
            ["id"] = id, ["shape"] = "box", ["position_m"] = new JsonArray(dx, 0.06, dz), ["rotation_deg"] = new JsonArray(0, 0, 0), ["size_m"] = new JsonArray(0.12, 0.12, 0.12), ["material"] = material,
        };
        var source = new JsonObject
        {
            ["schema"] = "garage_game.creation", ["version"] = 1, ["name"] = "Shed", ["seed"] = 1, ["mount"] = "ground",
            ["parts"] = new JsonArray(Part("a", 0, 0, "wood"), Part("b", 0.12, 0, "wood"), Part("c", 0, 0.12, "wood"), Part("d", 0.12, 0.12, "stone")),
            ["nodes"] = new JsonArray(new JsonObject { ["id"] = "use", ["op"] = "interact", ["part_id"] = "a", ["params"] = new JsonObject() }),
            ["edges"] = new JsonArray(),
        };
        var made = Send(Command(NextId("build"), "creation.place", new JsonObject { ["source"] = source, ["placement"] = new JsonObject { ["position_m"] = new JsonArray(x, 0, z) } }), Player);
        Check(Ok(made), "(the shed is built: " + Canonical(made["error"]) + ")");
        return made["created"]?[0]?.GetValue<string>() ?? "";
    }

    private JsonObject Repair(string principal, string target, bool preview = false) =>
        Send(Command(NextId("repair"), "nature.repair", new JsonObject { ["target"] = target }, preview), principal);

    /// <summary>
    /// What repairs spend (decision 116): logs and stones, the pockets first, then the chest; too few is refused in the kernel's words and
    /// nothing is spent; the Gubble's repair waits for the player's yes, and the yes spends; Creative's is free; nature.describe counts them.
    /// </summary>
    private async Task TestRepairFromTheStacks()
    {
        await NewWorld("repair");
        _host.ConfigureNature();
        var shed = BuildShed(-1.75, 1.15);
        _host.ScorchForTests(shed);
        Check(shed.Length > 0 && _host.Nature.Objects[shed] is { Creation: true } scorched && scorched.RepairCost.GetValueOrDefault("wood") == 3 && scorched.RepairCost.GetValueOrDefault("stone") == 1,
            "a scorched shed of three wooden parts and one of stone: its quote is 3 wood and 1 stone");
        var needs = "This repair needs 3 logs and 1 stone, more than your pockets and the chest hold.";
        Check(Refused(Repair(Player, shed), "invalid_args", needs, "$.args.target"), "with nothing gathered the repair is refused in the kernel's words: " + needs);
        await Stand(_player, ByLog(2), "beside the second log and the stone");
        Check(Ok(Gather(Player, "obj:log_2")) && Ok(Gather(Player, "obj:stone_1")), "a log and the stone into the pockets");
        await Stand(_player, ByLog(1), "beside the first log");
        Check(Ok(Gather(Player, "obj:log_1")), "a second log");
        await Stand(_player, AtCamp, "at the camp");
        Check(Ok(Store(Player, "chest", from: "pockets", item: "log", count: 1)) && Units(_host.ChestEntries, "log") == 1 && Units(_host.PocketEntries, "log") == 1,
            "one log goes in the chest: one in the pockets, one in the chest, and the stone in the pockets");
        var described = Send(Query("nature.describe", new JsonObject()), Player)["data"]?["wallet"];
        Check(Canonical(described) == Canonical(new JsonObject { ["stone"] = 1, ["wood"] = 2 }), "nature.describe counts the materials of the stacks, pockets and chest together: " + Canonical(described));
        var revision = _host.Revision;
        Check(Refused(Repair(Player, shed), "invalid_args", needs, "$.args.target") && Units(_host.PocketEntries, "log") == 1 && Units(_host.ChestEntries, "log") == 1 && _host.Revision == revision,
            "two logs are still too few: refused, and nothing is spent");
        await Stand(_player, ByLog(3), "beside the third log");
        Check(Ok(Gather(Player, "obj:log_3")) && Units(_host.PocketEntries, "log") == 2, "a third log: two in the pockets and one in the chest");
        var preview = Repair(Player, shed, preview: true);
        Check(Ok(preview) && Units(_host.PocketEntries, "log") == 2 && _host.Nature.Objects.ContainsKey(shed), "a preview spends nothing");
        revision = _host.Revision;
        var repaired = Repair(Player, shed);
        Check(Ok(repaired) && repaired["transient"]?.GetValue<bool>() == false && _host.Revision == revision + 1 &&
              Canonical(repaired["data"]) == Canonical(new JsonObject { ["cost"] = new JsonObject { ["stone"] = 1, ["wood"] = 3 } }) && !_host.Nature.Objects.ContainsKey(shed),
            "the repair goes through: one durable revision, its cost in its data: " + Canonical(repaired));
        Check(Units(_host.PocketEntries, "log") == 0 && Units(_host.PocketEntries, "stone") == 0 && Units(_host.ChestEntries, "log") == 0 && _host.PocketEntries.Count == 0 && _host.ChestEntries.Count == 0,
            "it spent the pockets' two logs and their stone first, then the chest's log");
        await NewWorld("repair", keepFiles: true);
        Check(_host.PocketEntries.Count == 0 && _host.ChestEntries.Count == 0, "after a reload the pockets and the chest are as the repair left them");

        await NewWorld("repair-gubble");
        _host.ConfigureNature();
        var hut = BuildShed(-1.75, 1.15);
        _host.ScorchForTests(hut);
        await Stand(_player, ByLog(2), "beside the second log and the stone");
        Check(Ok(Gather(Player, "obj:log_2")) && Ok(Gather(Player, "obj:stone_1")), "a log and the stone");
        await Stand(_player, ByLog(1), "beside the first log");
        Check(Ok(Gather(Player, "obj:log_1")), "a second log");
        await Stand(_player, ByLog(3), "beside the third log");
        Check(Ok(Gather(Player, "obj:log_3")), "a third log");
        // The Gubble sees the hut first (a companion names what its team has perceived).
        Check(_companion.TryTeleportTo(new Vector3(-1.65f, 0.01f, 0.8f)), "(the Gubble floats near the hut)");
        _companion.Rotation = new Vector3(0, Mathf.Pi, 0);
        await Frames(4);
        var asked = Repair(Companion, hut);
        var reason = asked["approval_needed"]?["reason"]?.GetValue<string>();
        Check(Code(asked) == "approval_required" && reason == $"The Gubble asks to repair {hut}, which you made, with 3 logs and 1 stone from your pockets and the chest." &&
              Units(_host.PocketEntries, "log") == 3, "the Gubble's repair waits for the player's yes, saying what it would spend: " + reason + Canonical(asked["error"]));
        var yes = asked["approval_needed"]?["request_id"]?.GetValue<string>() is { } id ? _host.Approve(id) : new JsonObject();
        Check(Ok(yes) && yes["approved_by"]?.GetValue<string>() == Player && Units(_host.PocketEntries, "log") == 0 && !_host.Nature.Objects.ContainsKey(hut),
            "and the player's yes spends the pockets' logs and stone: " + Canonical(yes));

        await NewWorld("repair-creative", mode: "creative");
        _host.ConfigureNature();
        var cabin = BuildShed(-1.75, 1.15);
        _host.ScorchForTests(cabin);
        var free = Repair(Player, cabin);
        Check(Ok(free) && Canonical(free["data"]) == Canonical(new JsonObject { ["cost"] = new JsonObject() }), "Creative repairs for free, with nothing gathered: " + Canonical(free));
    }

    /// <summary>
    /// An older save's wallet (written before P-Inventory-2) becomes the chest's stacks on load (decision 116): logs for wood, stones for
    /// stone, in one write with the nature, which keeps only what no kind is made of (copper); a second load moves nothing twice. Room
    /// state's x_play_nature.wallet is read the same way, and no new island writes one.
    /// </summary>
    private async Task TestWalletMigrates()
    {
        await NewWorld("wallet");
        Check(await PickUp("obj:cup", ByCup) && Ok(Store(Player, "chest")), "the island gets a save: the cup in the chest");
        var fresh = JsonNode.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(SavePath("wallet"))))!.AsObject();
        Check(fresh["nature"] is JsonObject block && !block.ContainsKey("wallet") && _host.ExportRoomState()?["extensions"]?["x_play_nature"]?["wallet"] == null,
            "a new island's save writes no wallet, nor does its room state");
        await FreeWorld();
        // A save from before: the same island with the wallet it had then.
        var path = ProjectSettings.GlobalizePath(SavePath("wallet"));
        var save = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        save["nature"]!["wallet"] = new JsonObject { ["copper"] = 2, ["leaf"] = 0, ["stone"] = 3, ["wood"] = 5 };
        File.WriteAllText(path, save.ToJsonString());
        await NewWorld("wallet", keepFiles: true);
        Check(Units(_host.ChestEntries, "log") == 5 && Units(_host.ChestEntries, "stone") == 3 && _host.ChestEntries.Any(e => e.Item == "obj:cup") &&
              Canonical(new JsonObject(_host.Nature.Wallet.Select(p => System.Collections.Generic.KeyValuePair.Create(p.Key, (JsonNode?)p.Value)))) == Canonical(new JsonObject { ["copper"] = 2 }),
            "on load the wallet's wood and stone are the chest's logs and stones, beside the cup; copper, which nothing is made of yet, stays as it was");
        var written = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Check(Canonical(written["nature"]?["wallet"]) == Canonical(new JsonObject { ["copper"] = 2 }) &&
              Canonical(written["team"]?["camp"]?["chest"]?["stacks"]) == Canonical(new JsonObject { ["log"] = 5, ["stone"] = 3 }),
            "and the save holds both in one write: the chest's stacks, and only the copper left in the wallet: " + Canonical(written["nature"]?["wallet"]) + " " + Canonical(written["team"]?["camp"]?["chest"]));
        await NewWorld("wallet", keepFiles: true);
        Check(Units(_host.ChestEntries, "log") == 5 && Units(_host.ChestEntries, "stone") == 3, "a second load moves nothing twice");
        var state = _host.ExportRoomState()!;
        state["extensions"]!["x_play_nature"]!["wallet"] = new JsonObject { ["copper"] = 2, ["wood"] = 4 };
        await NewWorld("wallet-import");
        var imported = _host.ImportRoomState(state);
        Check(imported.Ok && Units(_host.ChestEntries, "log") == 9 && Units(_host.ChestEntries, "stone") == 3,
            "room state's x_play_nature.wallet is read for its migration too: its four wood join the chest's five logs: " + imported.Message);
    }
}
