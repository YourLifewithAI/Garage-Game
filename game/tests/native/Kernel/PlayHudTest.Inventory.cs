using Godot;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;
using GarageGame.Native.Saves;
using GarageGame.Native.Ui;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The HUD pass for the inventory and food (docs/game/MECHANICS.md decisions 86, 87, 90, 97 to 103, 108, 111 and 113; docs/game/INVENTORY.md), in
/// the real room with the real HUD and the real host, the shipped pack's pockets, chest and food (its windfall apple and seed pod made units
/// of their kinds, every food source bearing): E's order (the chest at a camp, the pockets elsewhere, a log to the wallet, a thing set
/// down when neither fits, food picked into the pockets, its full-pockets words beside it), the Tab panel (opens, lists, Eat and Drop,
/// Esc first), the camp's Inventory page (Take, Store, the Gubble's switch in words only) and the fifth switch in the settings.
/// </summary>
public partial class PlayHudTest
{
    private const string InvHudRoot = HudSaves + "/inventory";
    private static readonly Vector3 InvTreeAt = new(1.4f, 0, -0.9f);
    private static readonly Vector3 InvShrubAt = new(-1.4f, 0, 0.9f);
    private static readonly Vector3 InvAtCamp = new(0.1f, 0, 0.62f);
    private static readonly Vector3 InvByApple1 = new(0.24f, 0, 0.35f);
    private static readonly Vector3 InvByApple2 = new(0.24f, 0, 0.45f);
    private static readonly Vector3 InvByPod = new(0.24f, 0, 0.65f);
    private static readonly Vector3 InvByCup = new(-0.13f, 0, 0.75f);
    private static readonly Vector3 InvByDoorstop = new(-0.3f, 0, 0.62f);
    private static readonly Vector3 InvByBrick = new(0.0f, 0, 0.86f);
    private static readonly Vector3 InvAway = new(0.9f, 0, -0.6f);
    /// <summary>A spot with nothing within the hands' reach (the brick is dropped at InvAway by then): F has nothing to pick up there.</summary>
    private static readonly Vector3 InvClear = new(0.3f, 0, -0.9f);

    private RoomWorld _iw = null!;
    private CommandHost _ih = null!;
    private RoomHud _ihud = null!;

    /// <summary>The test room with a broadleaf tree and a shrub, and on the rug two windfall apples, a seed pod, a cup, a brick of 0.29 kg and two pebbles (the second for the repair's stone in Challenge; the apples and the pod are units of their kinds once the pack says so; the pebble, of stone, is a stone).</summary>
    private static string InventoryHudRoom()
    {
        IslandFixture.Row Bush(Vector3 at, float top, uint seed) => new(at.X, 0, at.Z, 0, new Vector3(1, top / 0.13f, 1), new Color(0.8f, 0.9f, 0.7f), top, 0, 0, seed);
        var shrub = new IslandFixture.SetSpec("shrub", "shrub", "plant", "shrub", "none", "shrub", true, "plants/grass.glb", 12, new[] { ("plants/grass_far.glb", 2) }, 0, new[] { Bush(InvShrubAt, 0.12f, 51) });
        var shown = IslandFixture.WithPlants(RoomWorld.DefaultRoom, InvHudRoot + "/room/test_room", new[] { IslandFixture.Trees(new[] { IslandFixture.Broadleaf(InvTreeAt.X, InvTreeAt.Z, 0, 0, 41) }), shrub });
        var target = ProjectSettings.GlobalizePath(shown);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "room.json")))!.AsObject();
        var files = manifest["files"]!.AsArray();
        var objects = manifest["objects"]!.AsArray();
        var assets = new[]
        {
            ("windfall_apple", "Apple", "fallen apple", 0.02, new[] { 0.025, 0.025, 0.025 }, "foliage"),
            ("seed_pod", "Seed pod", "seed pod", 0.001, new[] { 0.012, 0.012, 0.012 }, "foliage"),
            ("cup_proxy", "Cup", "cup", 0.05, new[] { 0.03, 0.04, 0.03 }, "ceramic"),
            ("brick_proxy", "Brick", "brick", 0.29, new[] { 0.06, 0.03, 0.04 }, "ceramic"),
            // A stone (its material's role is stone: a unit of the stone kind, decision 116), for the hotbar's set down.
            ("pebble_proxy", "Pebble", "pebble", 0.05, new[] { 0.03, 0.02, 0.03 }, "stone"),
        };
        foreach (var (asset, name, category, mass, size, role) in assets)
        {
            var record = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "objects/doorstop_proxy/asset.json")))!.AsObject();
            record["asset_id"] = asset;
            record["display_name"] = name;
            record["category"] = category;
            record["category_group"] = "decor";
            record["dimensions_m"] = new JsonArray(size.Select(v => (JsonNode?)v).ToArray());
            record["physics"] = new JsonObject { ["movable"] = true, ["mass_kg"] = mass };
            record["materials"] = new JsonArray(new JsonObject { ["slot"] = "body", ["role"] = role, ["base_color"] = "#b84a32" });
            record["affordances"] = new JsonArray();
            var bytes = Encoding.UTF8.GetBytes(record.ToJsonString());
            var relative = $"objects/{asset}/asset.json";
            Directory.CreateDirectory(Path.Combine(target, "objects", asset));
            File.WriteAllBytes(Path.Combine(target, relative), bytes);
            files.Add(new JsonObject { ["path"] = relative, ["sha256"] = CanonicalJson.Sha256Hex(bytes), ["bytes"] = bytes.Length });
        }
        foreach (var (id, asset, x, z) in new[] { ("obj:apple_1", "windfall_apple", 0.3, 0.35), ("obj:apple_2", "windfall_apple", 0.3, 0.45), ("obj:pod", "seed_pod", 0.3, 0.65), ("obj:cup", "cup_proxy", -0.2, 0.75), ("obj:brick", "brick_proxy", 0.0, 0.95), ("obj:pebble", "pebble_proxy", 0.7, 0.4), ("obj:pebble_2", "pebble_proxy", -0.75, 0.45) })
            objects.Add(new JsonObject
            {
                ["id"] = id, ["asset"] = $"objects/{asset}/asset.json",
                ["transform"] = new JsonObject { ["position_m"] = new JsonArray(x, 0.006, z), ["rotation"] = new JsonArray(0.0, 0.0, 0.0, 1.0) },
                ["support"] = new JsonObject { ["kind"] = "object", ["target_id"] = "obj:rug" },
            });
        File.WriteAllText(Path.Combine(target, "room.json"), manifest.ToJsonString(), new UTF8Encoding(false));
        return shown;
    }

    /// <summary>The shipped pack with its apple and seed made units of the room's two assets, and every food source bearing; pockets, when named, that many slots.</summary>
    private static IslandRules InventoryHudPack(int? pockets = null)
    {
        var pack = JsonNode.Parse(Godot.FileAccess.GetFileAsString($"res://rules/{RoomWorld.DefaultRulesId}/v{ShippedRules.Version}.json"))!.AsObject();
        if (pockets is { } slots) pack["inventory"]!["pockets"]!["entries"] = slots;
        var kinds = pack["inventory"]!["kinds"]!.AsArray();
        kinds.First(k => k!["id"]!.GetValue<string>() == "apple")!["assets"] = new JsonArray("windfall_apple");
        kinds.First(k => k!["id"]!.GetValue<string>() == "seed")!["assets"] = new JsonArray("seed_pod");
        foreach (var source in pack["food"]!["sources"]!.AsArray()) source!["one_in"] = 1;
        // Three windfalls under the tree (the pack's two to four, by the plant's id): two for Creative's bar (a count to fall by one), one for Challenge.
        pack["food"]!["sources"]!.AsArray().First(s => s!["kind"]!.GetValue<string>() == "apple")!["windfalls"] = new JsonArray(3, 3);
        return IslandRules.Parse(Encoding.UTF8.GetBytes(pack.ToJsonString()));
    }

    private async Task TestInventoryHud()
    {
        var previousSaves = CommandHost.SaveRoot;
        CommandHost.SaveRoot = InvHudRoot + "/saves";
        _iw = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
        try
        {
            _iw.RoomDirectory = InventoryHudRoom();
            AddChild(_iw);
            for (var booting = System.Diagnostics.Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < 60 && !_iw.WorldReady && _iw.LoadError.Length == 0;) await Frames(1);
            Check(_iw.WorldReady, "(the room with a tree, a shrub and small things boots: " + _iw.LoadError + ")");
            if (!_iw.WorldReady) return;
            await Frames(5);
            _ih = CommandHost.Of(_iw)!;
            _ihud = _iw.GetNode<RoomHud>("RoomHud");
            _iw.Player.ReadKeyboard = false;
            _ih.UseRules(InventoryHudPack());
            FreshBodies(_ih);
            if (_ih.Mode != "creative") _ih.PlayerMode("creative");
            await Frames(5);
            Check(_ih.HasInventory && _ih.PocketCaps is { Entries: 10 } && _ih.ChestCaps is { Entries: 20 } && _ih.PocketEntries.Count == 0 && _ih.ChestEntries.Count == 0,
                "(the pack's pockets and chest are there, empty)");
            await InvEOrder();
            await InvFoodAndPanel();
            await InvBarWheelAndClick();
            await InvCost();
            await InvCampPage();
            await InvSettingsSwitch();
            // The Gubble's repair and the card that shows its spend (PlayHudTest.Repair.cs): last, in Challenge, then back to Creative.
            await InvRepairIcon();
        }
        finally
        {
            if (IsInstanceValid(_iw)) _iw.Free();
            await Frames(2);
            CommandHost.SaveRoot = previousSaves;
        }
    }

    // ---- the helpers ----

    private async Task InvStand(Vector3 at)
    {
        _iw.Player.SetControlInput(Vector2.Zero);
        Check(_iw.Player.TryTeleportTo(at), $"(the player stands at {at.X:0.00}, {at.Z:0.00})");
        _iw.Player.ResetPhysicsInterpolation();
        await Frames(6);
    }

    private bool InvGrab(string id) =>
        _ih.HandleObject(CanonicalJson.Text(new JsonObject
        {
            ["schema"] = "garage_game.command", ["version"] = 1, ["action_id"] = "inv-grab-" + id.Replace(':', '-') + "-" + Guid.NewGuid().ToString("N")[..6], ["room_id"] = _ih.Room.RoomId,
            ["op"] = "entity.grab", ["args"] = new JsonObject { ["target"] = id, ["actor"] = CommandHost.PlayerAvatar },
        }), CommandHost.PlayerPrincipal)["ok"]?.GetValue<bool>() == true && _ih.HeldBy(CommandHost.PlayerAvatar) == id;

    /// <summary>Stand beside a thing, take it in hand, and stand where the test wants it carried to.</summary>
    private async Task<bool> InvCarry(string id, Vector3 beside, Vector3 to)
    {
        await InvStand(beside);
        var held = InvGrab(id);
        await InvStand(to);
        await Frames(40);
        return held && _ih.HeldBy(CommandHost.PlayerAvatar) == id;
    }

    private async Task InvKey(Key key, int frames = 2)
    {
        _ihud._UnhandledInput(Press(key));
        await Frames(frames);
    }

    private int InvUnits(string kind) => _ih.PocketEntries.FirstOrDefault(e => e.Item == kind)?.Count ?? 0;

    /// <summary>The spot a unit of a plant's food lies at (its bottom centre) and where to stand facing it from, 6 cm short on the way from the stem.</summary>
    private (Vector3 Food, Vector3 Stand) InvApproach(string plant, int spot)
    {
        var food = _ih.FoodPlants.First(p => p.Id == plant);
        var at = food.At(spot, 1);
        var away = new Vector3(at.X - food.Root.X, 0, at.Z - food.Root.Z);
        away = away.LengthSquared() < 1e-8f ? Vector3.Back : away.Normalized();
        return (at, new Vector3(at.X, 0, at.Z) + away * 0.06f);
    }

    private async Task InvFace(Vector3 at, Vector3 toward)
    {
        await InvStand(at);
        var way = toward - at;
        _iw.Player.Rotation = new Vector3(0, Mathf.Atan2(-way.X, -way.Z), 0);
        await Frames(40);
    }

    // ---- E's order (decision 113) ----

    private async Task InvEOrder()
    {
        var host = _ih;
        var hud = _ihud;
        var player = CommandHost.PlayerAvatar;
        var prompt = hud.InteractPrompt;
        _iw.Player.ReadKeyboard = false;
        hud.SetViewMode(1);
        // At the camp the chest comes first, for a unique thing and for a unit of food.
        Check(await InvCarry("obj:cup", InvByCup, InvAtCamp) && host.PlayerAtCamp(), "the player carries the cup to the camp");
        Check(hud.Interaction.Kind == RoomHud.InteractKind.Store && hud.Interaction.Words == "E: store Cup in the chest" && prompt.Visible && prompt.Text == "E: store Cup in the chest",
            "holding the cup at the camp, E's prompt is the host's words: \"" + hud.Interaction.Words + "\"");
        var revision = host.Revision;
        await InvKey(Key.E);
        Check(host.HeldBy(player) == null && host.ChestEntries.Any(e => e.Item == "obj:cup" && e.Kind == "thing") && host.PocketEntries.Count == 0 && host.Revision == revision + 1 &&
              hud.NoticeText == "Cup is in the chest now.",
            "E stores it in the chest, not the pockets, as one durable change: " + hud.NoticeText);
        Check(await InvCarry("obj:apple_1", InvByApple1, InvAtCamp) && hud.Interaction.Kind == RoomHud.InteractKind.Store && hud.Interaction.Words == "E: store Apple in the chest",
            "a windfall apple in hand at the camp is stored the same way: " + hud.Interaction.Words);
        await InvKey(Key.E);
        Check(host.ChestEntries.Any(e => e.Item == "apple" && e.Count == 1) && host.PocketEntries.Count == 0 && hud.NoticeText == "Apple is in the chest now.", "and it joins the chest's apple stack: " + hud.NoticeText);
        Check(await InvCarry("obj:doorstop", InvByDoorstop, InvAtCamp) && hud.Interaction.Kind == RoomHud.InteractKind.Store, "the doorstop (0.3 kg) is stored too");
        await InvKey(Key.E);
        Check(host.ChestEntries.Count == 3 && host.PocketEntries.Count == 0, "(the chest holds the cup, the apple and the doorstop)");

        // Away from a camp the pockets take a thing that fits: here pockets of one slot (decision 120: caps are counts), so the brick fills them.
        host.UseRules(InventoryHudPack(pockets: 1));
        Check(await InvCarry("obj:brick", InvByBrick, InvAway) && !host.PlayerAtCamp(), "the player carries the brick (0.29 kg) away from the camp");
        Check(hud.Interaction.Kind == RoomHud.InteractKind.Pocket && hud.Interaction.Words == "E: put Brick in your pockets" && prompt.Visible,
            "away from a camp E's prompt puts it in the pockets: \"" + hud.Interaction.Words + "\"");
        await InvKey(Key.E);
        Check(host.HeldBy(player) == null && host.PocketEntries.Count == 1 && host.PocketEntries[0].Item == "obj:brick" && hud.NoticeText == "Brick is in your pockets now.",
            "E puts it in the pockets: " + hud.NoticeText);
        // The pockets' one slot is full: the next thing does not fit, the host says why, and E sets it down as it always did.
        Check(await InvCarry("obj:apple_2", InvByApple2, InvAway) && host.PlayerCanStore() is { Ok: false } too && too.Words == "Your pockets are full: one thing.",
            "a windfall apple does not fit the full pockets: the host's words");
        Check(hud.Interaction.Kind == RoomHud.InteractKind.SetDown && hud.Interaction.Words == "E: set Apple down", "so E's prompt falls through to setting it down, as it always did: \"" + hud.Interaction.Words + "\"");
        await InvKey(Key.E);
        Check(host.HeldBy(player) == null && host.PocketEntries.Count == 1 && host.ChestEntries.Count == 3, "and E sets it down in front of the player: " + hud.NoticeText);
    }

    // ---- food, and the hotbar's pockets (the Tab panel's place: docs/game/HOTBAR.md) ----

    private async Task InvFoodAndPanel()
    {
        var host = _ih;
        var hud = _ihud;
        var prompt = hud.InteractPrompt;
        // Facing a windfall with the pockets full: the pockets' words stand beside it and E picks nothing.
        var windfalls = host.FoodPlants.First(p => p.Id == "plant:broadleaf-0").Spots.Select((s, i) => (s, i)).Where(p => p.s.Place == FoodPlace.Windfall).Select(p => p.i).ToArray();
        Check(windfalls.Length > 0, "(the tree has windfalls)");
        var (apple, appleStand) = InvApproach("plant:broadleaf-0", windfalls[0]);
        await InvFace(appleStand, apple);
        var full = host.PlayerCanPick();
        Check(full is { Ok: false, Source: CommandHost.FromTheGround } && full.Words == "Your pockets are full: one thing." &&
              hud.Interaction.Kind == RoomHud.InteractKind.FoodFull && hud.Interaction.Words == full.Words && prompt.Visible && prompt.Text == full.Words,
            $"facing an apple with the pockets full, the pockets' own words stand beside it: \"{hud.Interaction.Words}\"");
        var left = host.FoodLeft("plant:broadleaf-0").Count;
        await InvKey(Key.E);
        Check(host.PocketEntries.Count == 1 && host.FoodLeft("plant:broadleaf-0").Count == left && hud.NoticeText == full.Words, "E says the same as a notice and picks nothing: " + hud.NoticeText);

        // The bar's pockets (PlayHudTest.Hotbar.cs): Tab, a key within the frame, the brick in the hand and back, Esc, F; then the shipped ten slots.
        await InvBarThing();

        // Now the berries: "E: pick berries", one unit into the pockets, in the host's words.
        var (berries, berryStand) = InvApproach("plant:shrub-0", 0);
        await InvFace(berryStand, berries);
        var pick = host.PlayerCanPick();
        Check(pick is { Ok: true, Words: "Pick berries", Kind: "berries" } && hud.Interaction.Kind == RoomHud.InteractKind.PickFood && hud.Interaction.Words == "E: pick berries" && prompt.Visible && prompt.Text == "E: pick berries",
            $"facing berries with room in the pockets the prompt is the host's words: \"{hud.Interaction.Words}\"");
        var camera = hud.GetViewport().GetCamera3D();
        var onScreen = camera.UnprojectPosition(pick.At!.Value);
        var promptRect = new Rect2(prompt.Position, prompt.GetCombinedMinimumSize());
        var gap = promptRect.Grow(2).HasPoint(onScreen) ? 0 : new[] { promptRect.Position, promptRect.End, new Vector2(promptRect.Position.X, promptRect.End.Y), new Vector2(promptRect.End.X, promptRect.Position.Y) }.Min(c => c.DistanceTo(onScreen));
        Check(camera.IsPositionBehind(pick.At.Value) || gap < 40, $"and it stands beside the berries on the screen ({gap:0} px from them)");
        var shrubLeft = host.FoodLeft("plant:shrub-0").Count;
        await InvKey(Key.E);
        Check(InvUnits("berries") == 1 && host.FoodLeft("plant:shrub-0").Count == shrubLeft - 1 && hud.NoticeText == "You picked berries. In your pockets: 1 berries.", "E picks one unit into the pockets: " + hud.NoticeText);
        // The tree's windfall: the brief's own example. It lies loose at the trunk (P-Inventory-2, decision 118): E picks it up off the ground.
        await InvFace(appleStand, apple);
        Check(hud.Interaction.Kind == RoomHud.InteractKind.PickFood && hud.Interaction.Words == "E: pick up an apple", $"at the tree's foot: \"{hud.Interaction.Words}\"");
        await InvKey(Key.E);
        Check(InvUnits("apple") == 1 && hud.NoticeText == "You picked up an apple. In your pockets: 1 apple.", "E picks the apple up: " + hud.NoticeText);
        // A second windfall, for the bar's count to fall by one.
        Check(windfalls.Length == 3, "(the test pack's tree has three windfalls)");
        var (third, thirdStand) = InvApproach("plant:broadleaf-0", windfalls[2]);
        await InvFace(thirdStand, third);
        await InvKey(Key.E);
        Check(InvUnits("apple") == 2, "(a second apple: " + hud.NoticeText + ")");

        // The armed units, used from the bar (PlayHudTest.Hotbar.cs): a click and E, food eaten, a seed refused, a stone set down and picked up.
        await InvBarUnits();
        // F drops a unit of food (decision 130; the bar's E sets down logs and stones only): it lies loose before the player (decision 118), and E picks it
        // up again. F's other meanings are checked there too (PlayHudTest.Hotbar.cs).
        await InvBarDropWithF();
        await Frames(40);
        Check(hud.Interaction.Kind == RoomHud.InteractKind.PickFood && hud.Interaction.Words == "E: pick up berries", $"E faces the loose berries: \"{hud.Interaction.Words}\"");
        await InvKey(Key.E);
        Check(InvUnits("berries") == 1 && !host.LooseUnits.Any(u => u.Kind == "berries") && hud.NoticeText == "You picked up berries. In your pockets: 1 berries.", "E picks them up again: " + hud.NoticeText);
        // (Berries give a seed one time in three by the eating's id: the kernel's food suite counts those. A harvest's hook, which farming will
        // call, puts one more seed in the stack.)
        var seeds = InvUnits("seed");
        Check(host.FoodHarvested("plant:broadleaf-0") == 1 && InvUnits("seed") == seeds + 1, "(a harvest's seed joins the stack)");

        // Challenge has pockets of its own (empty), and a hand of its own (never chosen: the magic layer down). Full hearts keep the unit; hurt, it heals at once.
        FreshBodies(host);
        Check(host.PlayerMode("challenge")["ok"]?.GetValue<bool>() == true && host.Mode == "challenge" && host.PocketEntries.Count == 0, "(Challenge: full hearts, pockets of its own, empty)");
        Check(windfalls.Length > 1, "(the tree has a second windfall)");
        var (apple2, apple2Stand) = InvApproach("plant:broadleaf-0", windfalls[1]);
        await InvFace(apple2Stand, apple2);
        await InvKey(Key.E);
        Check(InvUnits("apple") == 1 && hud.NoticeText == "You picked up an apple. In your pockets: 1 apple.", "(a windfall picked in Challenge: " + hud.NoticeText + ")");
        await Frames(8);
        Check(host.HandNow.IsNone && hud.BarLayerShown == HandSelection.AbilitiesLayer, "Challenge's hand is its own, never chosen: the magic layer is down");
        await InvKey(Key.Tab);
        var appleSlot = host.PocketEntries.First(e => e.Item == "apple").Slot;
        await InvKey(InvBarKey(appleSlot));
        hud.PointerForTests = InvOffTheBar;
        hud._UnhandledInput(LeftButton(true));
        hud._UnhandledInput(LeftButton(false));
        await Frames(2);
        Check(hud.NoticeText == "Your hearts are full: the Apple stays in your pockets." && InvUnits("apple") == 1, "a click on the armed apple at full hearts: the host's words, and the unit stays: " + hud.NoticeText);
        var quarters = host.BodyRules.Quarters(host.Vitals);
        host.ApplyPlayerDamage(20, PlayerDamageSource.Fire);
        var hurt = host.BodyRules.Quarters(host.Vitals);
        hud._UnhandledInput(LeftButton(true));
        hud._UnhandledInput(LeftButton(false));
        await Frames(2);
        Check(hurt < quarters - 2 && host.BodyRules.Quarters(host.Vitals) == hurt + 2 && InvUnits("apple") == 0 && hud.NoticeText == "Crunch! You ate an apple: half a heart healed." &&
              host.PocketEntries.Select(e => e.Item).SequenceEqual(new[] { "seed" }) && InvUnits("seed") == 1,
            $"hurt, the click eats the apple and it heals half a heart at once, leaving its seed: \"{hud.NoticeText}\" ({hurt} to {host.BodyRules.Quarters(host.Vitals)} quarters)");
        hud.PointerForTests = null;
        FreshBodies(host);
        host.PlayerMode("creative");
        await Frames(8);
    }

    /// <summary>
    /// What the inventory pass and the hotbar add to a frame (E's prompt asks the host about the chest, the pockets, food and the hand; the bar
    /// reads the hand and the pockets at its pace and follows the pointer), against the HUD packet's budget of 0.5 ms: walking about the tree and
    /// the shrub with notices coming and a unit armed on the bar, then the pointer moving over the bar, its caption following and the wheel
    /// stepping the selection now and then.
    /// </summary>
    private async Task InvCost()
    {
        var hud = _ihud;
        var bar = hud.Hotbar;
        var spots = new[] { InvApproach("plant:shrub-0", 0).Stand, InvApproach("plant:broadleaf-0", 0).Stand, InvAway, InvAtCamp, InvApproach("plant:shrub-0", 0).Stand + new Vector3(0.1f, 0, 0) };
        await InvStand(InvAtCamp);
        if (hud.BarLayerShown != HandSelection.PocketsLayer) await InvKey(Key.Tab);
        await InvKey(InvBarKey(_ih.PocketEntries.First().Slot));
        PromptCost.Reset();
        PromptCost.Measuring = true;
        for (var i = 0; i < 600; i++)
        {
            if (i % 30 == 0)
            {
                _iw.Player.TryTeleportTo(spots[i / 30 % spots.Length]);
                _iw.Player.ResetPhysicsInterpolation();
                hud._UnhandledInput(Press(Key.P, shift: true));
            }
            await Frames(1);
        }
        var walking = (PromptCost.MeanMs, PromptCost.WorstMs, PromptCost.Frames, PromptCost.SamplesMs.OrderBy(m => m).ElementAt((int)(PromptCost.SamplesMs.Count * 0.99)));
        var walkingBar = PromptCost.PieceMeanMs(PromptCost.Piece.Hotbar);
        GD.Print("PLAY_HUD_COST: the inventory pass's prompt, notice and bar walking about the tree and the shrub with a unit armed, ms a frame: " +
                 string.Join(", ", Enum.GetValues<PromptCost.Piece>().Select(p => $"{p} {PromptCost.PieceMeanMs(p):0.0000}")) + $"; mean {walking.MeanMs:0.0000}, 99% under {walking.Item4:0.000}, worst {walking.WorstMs:0.000} over {walking.Frames} frames");
        await InvStand(InvApproach("plant:shrub-0", 0).Stand);
        var writes = bar.Writes;
        PromptCost.Reset();
        for (var i = 0; i < 300; i++)
        {
            if (i % 25 == 0) hud.PointerForTests = bar.Placed.Row[i / 25 % Hotbar.Slots].GetCenter();
            if (i % 75 == 40) hud._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.WheelDown, Pressed = true });
            await Frames(1);
        }
        var over = (PromptCost.MeanMs, PromptCost.WorstMs, PromptCost.Frames, PromptCost.SamplesMs.OrderBy(m => m).ElementAt((int)(PromptCost.SamplesMs.Count * 0.99)));
        var overBar = PromptCost.PieceMeanMs(PromptCost.Piece.Hotbar);
        PromptCost.Measuring = false;
        hud.PointerForTests = null;
        GD.Print("PLAY_HUD_COST: the pointer over the bar, its caption following, the wheel stepping it, ms a frame: " + string.Join(", ", Enum.GetValues<PromptCost.Piece>().Select(p => $"{p} {PromptCost.PieceMeanMs(p):0.0000}")) +
                 $"; mean {over.MeanMs:0.0000}, 99% under {over.Item4:0.000}, worst {over.WorstMs:0.000} over {over.Frames} frames, {PromptCost.AllocatedBytes / Math.Max(1, over.Frames)} bytes a frame; the bar written {bar.Writes - writes} times");
        Check(walking.Frames >= 550 && walking.MeanMs < 0.5 && walking.Item4 < 0.5 && over.Frames >= 280 && over.MeanMs < 0.5 && over.Item4 < 0.5,
            $"the pass's additions cost {walking.MeanMs:0.000} ms a frame walking about the food and {over.MeanMs:0.000} ms with the pointer over the bar (99 in 100 under {walking.Item4:0.000} and {over.Item4:0.000} ms), against the HUD's budget of 0.5 ms");
        Check(walkingBar < 0.1 && overBar < 0.1 && bar.Writes - writes <= 8,
            $"the bar's own share is {walkingBar:0.0000} ms a frame walking and {overBar:0.0000} ms under the pointer, and it is written again only when it changes ({bar.Writes - writes} times in 300 frames with four wheel steps)");
    }

    // ---- the camp's Inventory page ----

    private async Task InvCampPage()
    {
        var host = _ih;
        var hud = _ihud;
        var screen = hud.CampScreen;
        await InvStand(InvAtCamp);
        var seeds = InvUnits("seed");
        Check(host.ChestEntries.Count == 3 && host.PocketEntries.Select(e => e.Item).Order(StringComparer.Ordinal).SequenceEqual(new[] { "berries", "obj:brick", "seed", "stone" }) && seeds >= 2 &&
              host.HeldBy(CommandHost.PlayerAvatar) == null,
            $"(the chest holds the cup, an apple and the doorstop; Creative's pockets berries, the brick, {seeds} seeds and a stone, the apples eaten; the hands are empty)");
        // The bar's selection: the berries' slot chosen (a unit armed). At the camp E still sits you down first.
        if (hud.BarLayerShown != HandSelection.PocketsLayer) await InvKey(Key.Tab);
        var berrySlot = host.PocketEntries.First(e => e.Item == "berries").Slot;
        var seedSlot = host.PocketEntries.First(e => e.Item == "seed").Slot;
        await InvKey(InvBarKey(berrySlot));
        await InvKey(Key.E);
        Check(hud.Sitting && hud.CampOpen, "E sits the player at the fire and opens the camp");
        hud.CampTabs.CurrentTab = 4;
        await Frames(4);
        var page = hud.CampPageText;
        Check(page.Contains("The chest\n3 of 20 things", StringComparison.Ordinal) && page.Contains("Cup · 0.05 kg", StringComparison.Ordinal) &&
              page.Contains("Apple ×1 · 0.02 kg · heals half a heart each", StringComparison.Ordinal) && page.Contains("Doorstop · 0.3 kg", StringComparison.Ordinal) &&
              // The pockets in slot order (P-Inventory-2: the hotbar's), each with its slot on the bar, and the bar's selection (the hotbar packet).
              page.Contains($"Your pockets\n4 of 10 things\nOn the bar: slot {berrySlot}, Berries ×1, one ready to use.\n", StringComparison.Ordinal) &&
              page.Contains($"{berrySlot} · Berries ×1 · 0.005 kg · heals a quarter of a heart each · chosen", StringComparison.Ordinal) &&
              page.Contains($"{seedSlot} · Seeds ×{seeds} · ", StringComparison.Ordinal) && !page.Contains($"{seedSlot} · Seeds ×{seeds} · {InventoryPages.Kg(seeds * 0.001)} kg · chosen", StringComparison.Ordinal),
            "the Inventory page lists the chest's entries with their masses and caps, and the pockets beside it, by slot, with the bar's choice: " + page.Replace("\n", " | "));
        Check(page.Contains("The Gubble may not use the chest. You can let it in the settings (C).", StringComparison.Ordinal) && !screen.FindChildren("*", "CheckButton", true, false).Any(),
            "the Gubble's chest switch is read-only words here, off by default, with the setting in the settings and no switch on the page");
        var campRect = screen.GetGlobalRect();
        var buttons = screen.FindChildren("inv|*", "Button", true, false).OfType<Button>().ToArray();
        Check(buttons.Length >= 6 && buttons.All(b => b.Visible && b.GetGlobalRect().Position.X >= campRect.Position.X && b.GetGlobalRect().End.X <= campRect.End.X && b.GetGlobalRect().Size.Y >= 32),
            $"the page's {buttons.Length} buttons all lie inside the camp's panel, each at least 32 px high (the panel {campRect.Position.X:0} to {campRect.End.X:0}, the widest button ends at {buttons.Max(b => b.GetGlobalRect().End.X):0})");
        // Take: a thing into the hand; a second thing is refused in the host's words; a stack's unit into the pockets.
        var takeCup = screen.FindChild("inv|take|obj_cup|hand|", true, false) as Button;
        var takeDoorstop = screen.FindChild("inv|take|obj_doorstop|hand|", true, false) as Button;
        Check(takeCup is { Disabled: false } && takeDoorstop is { Disabled: false } && screen.FindChild("inv|take|obj_cup|pockets|", true, false) is Button { Disabled: false },
            "each chest thing has Take into the hand and into the pockets, live at the camp");
        takeCup!.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(3);
        Check(host.HeldBy(CommandHost.PlayerAvatar) == "obj:cup" && hud.CampFoot == "Cup is in your hand." && hud.NoticeText == hud.CampFoot && host.ChestEntries.All(e => e.Item != "obj:cup"),
            "Take puts the cup in the hand, through inventory.take: " + hud.CampFoot);
        screen.FindChild("inv|take|obj_doorstop|hand|", true, false)!.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(3);
        Check(host.ChestEntries.Any(e => e.Item == "obj:doorstop") && hud.CampFoot == "This avatar is already holding something. Put it down first.",
            "with the hands full the host's refusal is the answer: " + hud.CampFoot);
        // Decision 120: no mass cap; the doorstop (0.3 kg) comes into the pockets, and goes back to the chest.
        hud.CampButton(InventoryPages.Target(InventoryPages.Take, "obj:doorstop", "pockets"));
        Check(host.PocketEntries.Any(e => e.Item == "obj:doorstop") && host.ChestEntries.All(e => e.Item != "obj:doorstop"), "the pockets take the doorstop, whatever it weighs: " + hud.CampFoot);
        hud.CampButton(InventoryPages.Target(InventoryPages.Store, "obj:doorstop", "chest"));
        Check(host.ChestEntries.Any(e => e.Item == "obj:doorstop") && host.PocketEntries.All(e => e.Item != "obj:doorstop"), "and Store puts it back in the chest: " + hud.CampFoot);
        screen.FindChild("inv|take|apple|pockets|1", true, false)!.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(3);
        Check(InvUnits("apple") == 1 && host.ChestEntries.All(e => e.Item != "apple") && hud.CampFoot == "Took 1 Apple into your pockets.", "Take puts the chest's apple into the pockets: " + hud.CampFoot);
        var lines = hud.CampPageText;
        var bySlot = host.PocketEntries.OrderBy(e => e.Slot).Select(e => lines.IndexOf($"\n{e.Slot} · {e.Name}", lines.IndexOf("Your pockets", StringComparison.Ordinal), StringComparison.Ordinal)).ToArray();
        Check(lines.Contains("Your pockets\n5 of 10 things", StringComparison.Ordinal) && bySlot.Length == 5 && bySlot.All(i => i > 0) && bySlot.SequenceEqual(bySlot.Order()) &&
              host.PocketEntries.First(e => e.Item == "apple").Slot == Enumerable.Range(1, 10).First(n => host.PocketEntries.All(e => e.Item == "apple" || e.Slot != n)),
            "and the pockets' lines follow, in slot order, the apple in the first free slot: " + hud.CampPageText.Replace("\n", " | "));
        // Store from the pockets: all of a stack, or one.
        var storeAll = screen.FindChild("inv|store|seed|chest|all", true, false) as Button;
        Check(storeAll != null && screen.FindChild("inv|store|berries|chest|all", true, false) == null, "a stack of several has Store all, a single unit does not");
        storeAll!.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(3);
        Check(InvUnits("seed") == 0 && host.ChestEntries.Any(e => e.Item == "seed" && e.Count == seeds) && hud.CampFoot == $"Stored {seeds} Seeds in the chest.", "Store all puts every seed in the chest: " + hud.CampFoot);
        screen.FindChild("inv|store|berries|chest|1", true, false)!.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(3);
        Check(InvUnits("berries") == 0 && host.ChestEntries.Any(e => e.Item == "berries") && hud.CampFoot == "Stored 1 Berries in the chest.", "and Store puts the berries away: " + hud.CampFoot);
        // The Gubble's switch changes in words once the setting does; the page never offers the switch itself.
        host.SetGubbleChest(true);
        await Frames(40);
        Check(hud.CampPageText.Contains("The Gubble may use the chest. You can change that in the settings (C).", StringComparison.Ordinal), "the page's words follow the setting");
        host.SetGubbleChest(false);
        await InvKey(Key.E);
        Check(!hud.Sitting, "(standing up again)");
    }

    // ---- the settings' fifth switch ----

    private async Task InvSettingsSwitch()
    {
        var host = _ih;
        var hud = _ihud;
        await InvKey(Key.C);
        var panel = hud.SettingsPanel;
        var switches = panel.FindChildren("*", "CheckButton", true, false).OfType<CheckButton>().ToArray();
        Check(switches.Length == 5 && switches[4].Text == "Let the Gubble use the chest" && switches[4].Name == "GubbleChestSetting" && hud.ChestToggle == switches[4],
            "the settings hold a fifth switch, \"Let the Gubble use the chest\": " + string.Join(" | ", switches.Select(b => b.Text)));
        var toggle = switches[4];
        Check(!toggle.ButtonPressed && !host.GubbleChestAllowed && !toggle.Disabled, "off by default (decision 100)");
        toggle.EmitSignal(BaseButton.SignalName.Toggled, true);
        Check(host.GubbleChestAllowed && toggle.ButtonPressed && hud.NoticeText == "The Gubble may use the chest. (Not saved: it lasts this session.)",
            "switching it on is obeyed by the host at once; with no team file it lasts the session and says so: " + hud.NoticeText);
        // With a team file the host writes it.
        var teamPath = ProjectSettings.GlobalizePath(InvHudRoot + "/teams/carrier/team.json");
        Directory.CreateDirectory(Path.GetDirectoryName(teamPath)!);
        var store = new TeamStore(teamPath, "carrier");
        if (store.Load() == null) store.Save(TeamFile.Create("carrier", DateTime.UtcNow, "Player", TeamFile.PrototypeCostume, "Gubble", "#65b9b0"));
        host.UseTeam(store);
        toggle.EmitSignal(BaseButton.SignalName.Toggled, false);
        Check(!host.GubbleChestAllowed && !TeamFile.Parse(File.ReadAllBytes(teamPath)).GubbleChest && hud.NoticeText == "The Gubble will not use the chest.", "off again, written through the host to the team file: " + hud.NoticeText);
        toggle.EmitSignal(BaseButton.SignalName.Toggled, true);
        var listed = JsonNode.Parse(host.Handle(new JsonObject
        {
            ["schema"] = "garage_game.query", ["version"] = 1, ["query_id"] = "inv-chest-switch", ["room_id"] = host.Room.RoomId, ["op"] = "capabilities.list", ["args"] = new JsonObject(),
        }.ToJsonString(), CommandHost.CompanionPrincipal))!.AsObject();
        Check(host.GubbleChestAllowed && TeamFile.Parse(File.ReadAllBytes(teamPath)).GubbleChest && listed["data"]?["inventory"]?["gubble_chest"]?.GetValue<bool>() == true &&
              hud.NoticeText == "The Gubble may use the chest.", "on, the team file keeps it and capabilities.list tells the companion: " + hud.NoticeText);
        // The panel shows the host's value when it opens.
        await InvKey(Key.C);
        host.SetGubbleChest(false);
        await InvKey(Key.C);
        Check(!hud.ChestToggle!.ButtonPressed, "the switch follows the host when the panel opens");
        await InvKey(Key.C);
    }
}
