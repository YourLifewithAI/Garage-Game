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

    private RoomWorld _iw = null!;
    private CommandHost _ih = null!;
    private RoomHud _ihud = null!;

    /// <summary>The test room with a broadleaf tree and a shrub, and on the rug two windfall apples, a seed pod, a cup and a brick of 0.29 kg (the apples and the pod are units of their kinds once the pack says so).</summary>
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
        foreach (var (id, asset, x, z) in new[] { ("obj:apple_1", "windfall_apple", 0.3, 0.35), ("obj:apple_2", "windfall_apple", 0.3, 0.45), ("obj:pod", "seed_pod", 0.3, 0.65), ("obj:cup", "cup_proxy", -0.2, 0.75), ("obj:brick", "brick_proxy", 0.0, 0.95) })
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
            await InvCost();
            await InvCampPage();
            await InvSettingsSwitch();
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

    // ---- food, the Tab panel ----

    private async Task InvFoodAndPanel()
    {
        var host = _ih;
        var hud = _ihud;
        var prompt = hud.InteractPrompt;
        var view = hud.GetViewport().GetVisibleRect().Size;
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

        // Tab opens the panel on the left, under the meters: the brick, with Eat dimmed and Drop live; the body waits.
        Check(PlayerControls.Label(Act.PocketsPanel) == "Tab" && InputMap.HasAction(Act.PocketsPanel) && PlayerControls.Specs.Any(spec => spec.Action == Act.PocketsPanel),
            "the pockets panel's key is Tab, an input action in the map and in the list of controls");
        await InvKey(Key.Tab);
        var panel = hud.PocketsPanel;
        var rect = panel.GetGlobalRect();
        Check(hud.PocketsPanelOpen && panel.Visible && !_iw.Player.InputEnabled, "Tab opens the pockets panel and the body waits");
        Check(rect.Position == new Vector2(18, 150) && rect.Size.X >= 320 && rect.End.X <= view.X && rect.End.Y <= view.Y && !rect.Intersects(hud.MeterPanel.GetGlobalRect()) && rect.End.X < view.X * 0.5f,
            $"on the left where the Magic panel opens, under the meters and clear of them: {rect.Position.X:0},{rect.Position.Y:0} {rect.Size.X:0}x{rect.Size.Y:0} of {view.X:0}x{view.Y:0}");
        Check(hud.PocketsCapsText == "1 of 1 thing" && hud.PocketRows.Count() == 1, "it lists the caps and one entry: " + hud.PocketsCapsText);
        var row = hud.PocketRows.Single();
        var entry = (Label)row.GetNode("Entry");
        var eat = (Button)row.FindChild("Eat_obj_brick", true, false)!;
        var drop = (Button)row.FindChild("Drop_obj_brick", true, false)!;
        var rowRect = row.GetGlobalRect();
        Check(entry.Text == "Brick · 0.29 kg" && eat.Disabled && !drop.Disabled && rowRect.Position.X >= rect.Position.X && drop.GetGlobalRect().End.X <= rect.End.X &&
              eat.GetGlobalRect().End.X <= drop.GetGlobalRect().Position.X && entry.GetGlobalRect().Size.X >= 150,
            $"the entry reads \"{entry.Text}\", a thing is not eaten, and its Eat and Drop sit inside the panel after the words (the words {entry.GetGlobalRect().Size.X:0} px wide)");
        Check(row.GetChildren().OfType<Button>().All(b => b.FocusMode == Control.FocusModeEnum.None) && panel.FindChild("PocketsClose", true, false) is Button { FocusMode: Control.FocusModeEnum.None },
            "nothing on it takes the keyboard (Space jumps, Tab opens and closes; neither presses a button)");
        // Esc closes it first (decision 97), then the next opens the menu.
        await InvKey(Key.Escape);
        Check(!hud.PocketsPanelOpen && _iw.Player.InputEnabled && !hud.PauseMenuOpen, "the first Esc closes the panel and gives the body back; it does not open the menu");
        await InvKey(Key.Escape);
        Check(hud.PauseMenuOpen, "and the next opens the menu");
        hud.ClosePauseMenu();
        await InvKey(Key.Tab);
        await InvKey(Key.Tab);
        Check(!hud.PocketsPanelOpen && _iw.Player.InputEnabled, "Tab opens it and Tab again closes it");
        // Tab is also the engine's focus-next key: a footer button the player clicked holds the focus, and Tab, sent as the engine would
        // send it, still opens the panel (the HUD lets the focus go first) instead of moving the focus.
        await InvKey(Key.H);
        var follow = hud.FindChild("CompanionActions", true, false)!.GetChildren().OfType<Button>().First(b => b.Text == "Follow");
        follow.GrabFocus();
        Check(hud.GetViewport().GuiGetFocusOwner() == follow, "(a button of the help holds the keyboard focus)");
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.Tab, Keycode = Key.Tab, Pressed = true });
        await Frames(3);
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.Tab, Keycode = Key.Tab, Pressed = false });
        await Frames(2);
        Check(hud.PocketsPanelOpen && hud.GetViewport().GuiGetFocusOwner() == null, "Tab sent to the engine opens the panel even with a clicked button holding the focus");
        // (The engine hands the key to every HUD in the tree: the suite's first room's gets it too, and is put back.)
        _hud.SetPocketsPanelOpen(false);
        await InvKey(Key.Tab);
        await InvKey(Key.H);
        // Another panel takes the screen: it closes the pockets, and the pockets close the others.
        await InvKey(Key.Tab);
        await InvKey(Key.M);
        Check(hud.MagicPanelOpen && !hud.PocketsPanelOpen, "M opens the Magic panel and the pockets go");
        await InvKey(Key.Tab);
        Check(hud.PocketsPanelOpen && !hud.MagicPanelOpen, "Tab from the Magic panel opens the pockets and the Magic panel goes");
        await InvKey(Key.C);
        Check(hud.SettingsOpen && !hud.PocketsPanelOpen, "C opens the settings and the pockets go");
        await InvKey(Key.Tab);
        Check(hud.PocketsPanelOpen && !hud.SettingsOpen, "Tab from the settings opens the pockets");
        await InvKey(Key.Tab);
        Check(!hud.PocketsPanelOpen && _iw.Player.InputEnabled, "(closed again)");

        // A thing from the pockets set down before the player, through inventory.drop.
        await InvKey(Key.Tab);
        var revision = host.Revision;
        drop = (Button)hud.PocketRows.Single().FindChild("Drop_obj_brick", true, false)!;
        drop.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(4);
        Check(host.PocketEntries.Count == 0 && host.Revision >= revision + 2 && hud.NoticeText == "You set Brick down." && hud.PocketsFoot == "You set Brick down." &&
              !hud.PocketRows.Any() && hud.PocketsCapsText == "0 of 1 thing" && hud.PocketsPanel.FindChildren("*", "Label", true, false).OfType<Label>().Any(l => l.Text == RoomHud.PocketsEmptyWords),
            $"Drop sets the brick down before the player (the pose and the drop are two room changes): \"{hud.PocketsFoot}\", the caps \"{hud.PocketsCapsText}\", the empty panel says how to fill it");
        await InvKey(Key.Tab);
        // The shipped ten slots again.
        host.UseRules(InventoryHudPack());

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

        // Eat: in Creative nothing heals, but the unit is used up (decision 125) and its sound plays.
        await InvKey(Key.Tab);
        Check(hud.PocketRows.Count() == 2 && hud.PocketsCapsText == "2 of 10 things", $"the panel lists the apple and the berries: {hud.PocketsCapsText}");
        var appleRow = hud.PocketRows.First(r => r.Name == "Pocket_apple");
        Check(((Label)appleRow.GetNode("Entry")).Text == "Apple ×1 · 0.02 kg · heals half a heart each", "an apple's line says what it heals: " + ((Label)appleRow.GetNode("Entry")).Text);
        var sounds = 0;
        host.FoodSound += _ => sounds++;
        ((Button)appleRow.FindChild("Eat_apple", true, false)!).EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(2);
        Check(hud.NoticeText == "Crunch! You ate an apple." && InvUnits("apple") == 0 && InvUnits("seed") == 1 && sounds == 1,
            $"in Creative eating heals nothing but uses the apple up, leaving its seed (decisions 118 and 125): \"{hud.NoticeText}\"");
        // The notice stands to the right of the open panel.
        var card = hud.NoticeCard.GetGlobalRect();
        Check(hud.NoticeShowing && card.Position.X >= panel.GetGlobalRect().End.X + 15, $"the notice card stands clear of the open panel ({card.Position.X:0} against {panel.GetGlobalRect().End.X:0})");
        ((Button)hud.PocketRows.First(r => r.Name == "Pocket_berries").FindChild("Drop_berries", true, false)!).EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(2);
        Check(hud.NoticeText == "You set Berries down." && InvUnits("berries") == 0 && host.LooseUnits.Count == 1 && host.LooseUnits[0].Kind == "berries",
            "a unit of food set down lies loose before the player (decision 118): " + hud.NoticeText);
        await InvKey(Key.Tab);
        await Frames(2);
        Check(hud.Interaction.Kind == RoomHud.InteractKind.PickFood && hud.Interaction.Words == "E: pick up berries", $"E faces the loose berries: \"{hud.Interaction.Words}\"");
        await InvKey(Key.E);
        Check(InvUnits("berries") == 1 && host.LooseUnits.Count == 0 && hud.NoticeText == "You picked up berries. In your pockets: 1 berries.", "E picks them up again: " + hud.NoticeText);
        await InvKey(Key.Tab);
        // (Berries give a seed one time in three by the eating's id, the HUD's own: the kernel's food suite eats them. A harvest's hook, which
        // farming will call, puts a second seed in the stack for the camp page's Store all.)
        Check(host.FoodHarvested("plant:broadleaf-0") == 1 && InvUnits("seed") == 2 && hud.PocketRows.Count() == 2, "(a harvest's seed joins the stack: berries and two seeds)");
        await InvKey(Key.Tab);

        // Challenge has pockets of its own (empty): another windfall, picked there. Full hearts keep the unit; hurt, it heals at once.
        FreshBodies(host);
        Check(host.PlayerMode("challenge")["ok"]?.GetValue<bool>() == true && host.Mode == "challenge" && host.PocketEntries.Count == 0, "(Challenge: full hearts, pockets of its own, empty)");
        Check(windfalls.Length > 1, "(the tree has a second windfall)");
        var (apple2, apple2Stand) = InvApproach("plant:broadleaf-0", windfalls[1]);
        await InvFace(apple2Stand, apple2);
        await InvKey(Key.E);
        Check(InvUnits("apple") == 1 && hud.NoticeText == "You picked up an apple. In your pockets: 1 apple.", "(a windfall picked in Challenge: " + hud.NoticeText + ")");
        await InvKey(Key.Tab);
        appleRow = hud.PocketRows.First(r => r.Name == "Pocket_apple");
        ((Button)appleRow.FindChild("Eat_apple", true, false)!).EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(2);
        Check(hud.NoticeText == "Your hearts are full: the Apple stays in your pockets." && InvUnits("apple") == 1 && hud.PocketsFoot == hud.NoticeText, "at full hearts the host's words, and the unit stays: " + hud.NoticeText);
        var quarters = host.BodyRules.Quarters(host.Vitals);
        host.ApplyPlayerDamage(20, PlayerDamageSource.Fire);
        var hurt = host.BodyRules.Quarters(host.Vitals);
        appleRow = hud.PocketRows.First(r => r.Name == "Pocket_apple");
        ((Button)appleRow.FindChild("Eat_apple", true, false)!).EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(2);
        Check(hurt < quarters - 2 && host.BodyRules.Quarters(host.Vitals) == hurt + 2 && InvUnits("apple") == 0 && hud.NoticeText == "Crunch! You ate an apple: half a heart healed." &&
              hud.PocketRows.Select(r => r.Name.ToString()).SequenceEqual(new[] { "Pocket_seed" }) && InvUnits("seed") == 1,
            $"hurt, the apple heals half a heart at once and is used up, leaving its seed: \"{hud.NoticeText}\" ({hurt} to {host.BodyRules.Quarters(host.Vitals)} quarters)");
        await InvKey(Key.Tab);
        FreshBodies(host);
        host.PlayerMode("creative");
    }

    /// <summary>
    /// What the inventory pass adds to a frame (E's prompt now asks the host about the chest, the pockets and food; the panel reads the pockets), against the
    /// HUD packet's budget of 0.5 ms: walking about the tree and the shrub with notices coming, then standing by the food with the panel open.
    /// </summary>
    private async Task InvCost()
    {
        var hud = _ihud;
        var spots = new[] { InvApproach("plant:shrub-0", 0).Stand, InvApproach("plant:broadleaf-0", 0).Stand, InvAway, InvAtCamp, InvApproach("plant:shrub-0", 0).Stand + new Vector3(0.1f, 0, 0) };
        await InvStand(InvAtCamp);
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
        GD.Print("PLAY_HUD_COST: the inventory pass's prompt, notice and pockets walking about the tree and the shrub, ms a frame: " +
                 string.Join(", ", Enum.GetValues<PromptCost.Piece>().Select(p => $"{p} {PromptCost.PieceMeanMs(p):0.0000}")) + $"; mean {walking.MeanMs:0.0000}, 99% under {walking.Item4:0.000}, worst {walking.WorstMs:0.000} over {walking.Frames} frames");
        await InvStand(InvApproach("plant:shrub-0", 0).Stand);
        hud._UnhandledInput(Press(Key.Tab));
        await Frames(4);
        PromptCost.Reset();
        for (var i = 0; i < 300; i++) await Frames(1);
        var open = (PromptCost.MeanMs, PromptCost.WorstMs, PromptCost.Frames, PromptCost.SamplesMs.OrderBy(m => m).ElementAt((int)(PromptCost.SamplesMs.Count * 0.99)));
        PromptCost.Measuring = false;
        GD.Print("PLAY_HUD_COST: the pockets panel open, ms a frame: " + string.Join(", ", Enum.GetValues<PromptCost.Piece>().Select(p => $"{p} {PromptCost.PieceMeanMs(p):0.0000}")) +
                 $"; mean {open.MeanMs:0.0000}, 99% under {open.Item4:0.000}, worst {open.WorstMs:0.000} over {open.Frames} frames, {PromptCost.AllocatedBytes / Math.Max(1, open.Frames)} bytes a frame");
        Check(walking.Frames >= 550 && walking.MeanMs < 0.5 && walking.Item4 < 0.5 && open.Frames >= 280 && open.MeanMs < 0.5 && open.Item4 < 0.5,
            $"the pass's additions cost {walking.MeanMs:0.000} ms a frame walking about the food and {open.MeanMs:0.000} ms with the panel open (99 in 100 under {walking.Item4:0.000} and {open.Item4:0.000} ms), against the HUD's budget of 0.5 ms");
        hud._UnhandledInput(Press(Key.Tab));
        await Frames(2);
    }

    // ---- the camp's Inventory page ----

    private async Task InvCampPage()
    {
        var host = _ih;
        var hud = _ihud;
        var screen = hud.CampScreen;
        await InvStand(InvAtCamp);
        Check(host.ChestEntries.Count == 3 && host.PocketEntries.Count == 2 && host.HeldBy(CommandHost.PlayerAvatar) == null,
            "(the chest holds the cup, an apple and the doorstop; Creative's pockets berries and two seeds, the apple eaten; the hands are empty)");
        await InvKey(Key.E);
        Check(hud.Sitting && hud.CampOpen, "E sits the player at the fire and opens the camp");
        hud.CampTabs.CurrentTab = 4;
        await Frames(4);
        var page = hud.CampPageText;
        Check(page.Contains("The chest\n3 of 20 things", StringComparison.Ordinal) && page.Contains("Cup · 0.05 kg", StringComparison.Ordinal) &&
              page.Contains("Apple ×1 · 0.02 kg · heals half a heart each", StringComparison.Ordinal) && page.Contains("Doorstop · 0.3 kg", StringComparison.Ordinal) &&
              // The pockets in slot order (P-Inventory-2: the hotbar's): the berries were picked first.
              page.Contains("Your pockets\n2 of 10 things\nBerries ×1 · 0.005 kg · heals a quarter of a heart each", StringComparison.Ordinal) && page.Contains("Seeds ×2 · 0.002 kg", StringComparison.Ordinal),
            "the Inventory page lists the chest's entries with their masses and caps, and the pockets beside it: " + page.Replace("\n", " | "));
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
        Check(lines.Contains("Your pockets\n3 of 10 things\nBerries ×1", StringComparison.Ordinal) && lines.IndexOf("Seeds ×2", lines.IndexOf("Your pockets", StringComparison.Ordinal), StringComparison.Ordinal) is var seeds &&
              seeds > 0 && lines.IndexOf("Apple ×1", seeds, StringComparison.Ordinal) > seeds,
            "and the pockets' lines follow, in slot order (the apple in the first free slot after the seeds): " + hud.CampPageText.Replace("\n", " | "));
        // Store from the pockets: all of a stack, or one.
        var storeAll = screen.FindChild("inv|store|seed|chest|all", true, false) as Button;
        Check(storeAll != null && screen.FindChild("inv|store|berries|chest|all", true, false) == null, "a stack of several has Store all, a single unit does not");
        storeAll!.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(3);
        Check(InvUnits("seed") == 0 && host.ChestEntries.Any(e => e.Item == "seed" && e.Count == 2) && hud.CampFoot == "Stored 2 Seeds in the chest.", "Store all puts both seeds in the chest: " + hud.CampFoot);
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
