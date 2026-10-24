using Godot;
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Companion;
using GarageGame.Native.Kernel;
using GarageGame.Native.Saves;
using GarageGame.Native.Ui;

namespace GarageGame.Tests;

/// <summary>
/// The walk from the menu to the room, headless: the menu opens over a user folder with no team, a team is made in the New team panel
/// (its name, the plain-figure-or-drawn avatar, the Gubble's name and colour, a knack), the island, the mode and the difficulty are
/// chosen by pressing the cards, and Play boots the fixture room (the shipped test room) through the game's own boot with that team,
/// island, mode and difficulty; then Continue boots the last game again. And the copy of a Creative island into Challenge, pressed
/// from the menu, then booted.
/// </summary>
public partial class MenuTest
{
    private MenuBoot? _boot;
    private string _scene = "";

    private TeamLayout SceneLayout(string name)
    {
        _scene = $"{Root}/{name}";
        Folder(name);
        TeamBoot.UserRootOverride = _scene;
        IslandCatalog.RootsOverride = new[] { IslandsFolder(), "res://rooms" };
        RoomHud.ProfilePath = $"{_scene}/single_player/room/avatar_profile_v1.cfg";
        CommandHost.DefaultWishSaveRoot = $"{_scene}/wishes";
        return new TeamLayout(ProjectSettings.GlobalizePath(_scene));
    }

    /// <summary>The menu as the main scene opens it (MenuBoot.Open), over the scene's user folder.</summary>
    private async Task<MainMenu> OpenMenu(params string[] arguments)
    {
        _boot = new MenuBoot { AutoOpen = false };
        AddChild(_boot);
        var menu = _boot.Open(arguments);
        await Frames(3);
        return menu;
    }

    private async Task<(RoomWorld World, CommandHost Host)?> WaitForRoom()
    {
        var world = _boot!.World;
        if (world == null) { Check(false, "the menu started a room"); return null; }
        for (var booting = System.Diagnostics.Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < 60 && !world.WorldReady && world.LoadError.Length == 0;) await Frames(1);
        Check(world.WorldReady, "the room boots from the menu: " + world.LoadError);
        return world.WorldReady ? (world, CommandHost.Of(world)!) : null;
    }

    private async Task CloseGame()
    {
        _boot?.QueueFree();
        _boot = null;
        await Frames(4);
    }

    /// <summary>A click as the GUI receives a real one (a card with its contents on top reads it itself).</summary>
    private async Task Click(Control control)
    {
        var point = control.GetGlobalRect().GetCenter();
        GetViewport().PushInput(new InputEventMouseMotion { Position = point, GlobalPosition = point }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, ButtonMask = MouseButtonMask.Left, Pressed = true }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await Frames(2);
    }

    private static Button FindButton(Node under, string text) => under.FindChildren("*", "Button", true, false).OfType<Button>().First(b => b.Text == text);
    private static void Press(BaseButton button) => button.EmitSignal(BaseButton.SignalName.Pressed);

    private async Task TestMenuToRoom()
    {
        var layout = SceneLayout("walk");
        var menu = await OpenMenu();
        var model = menu.Model;
        Check(model.Teams.Count == 0 && menu.TeamButtons.Count == 0 && menu.NewTeamButton.IsVisibleInTree() && menu.ContinueButton.Disabled && menu.ContinueButton.Text.StartsWith("Continue", StringComparison.Ordinal) &&
              menu.PlayButton.Disabled && menu.HintLabel.Text == "Choose your character first.",
            "the menu opens on a machine with no team: only \"New team\", no Continue, no Play, and it says what to choose first");
        Check(menu.IslandButtons.Keys.SequenceEqual(new[] { "tutorial_isle", "plain_isle", "test_room" }) && menu.IslandButtons["tutorial_isle"].FindChild("TutorialTag", true, false) is Label { Text: "tutorial" },
            "one card per island, the tutorial first and marked");
        var thumbnail = menu.Thumbnails["tutorial_isle"];
        Check(thumbnail.HasShape && thumbnail.FloorTriangles == 2 && thumbnail.WallStrokes == 1 && thumbnail.BoxesDrawn == 2 && !menu.Thumbnails["test_room"].HasShape,
            $"an island's thumbnail is drawn from its shape data (a floor of {thumbnail.FloorTriangles} triangles, {thumbnail.WallStrokes} wall, {thumbnail.BoxesDrawn} boxes); a room without any draws a stand-in");
        Check(menu.ModeButtons.Keys.SequenceEqual(new[] { "creative", "challenge", "live" }) && menu.ModeButtons["live"].Text.Length == 0 && menu.ModeButtons["live"].FindChildren("*", "Label", true, false).OfType<Label>().Any(l => l.Text == "Live (for now just a solo preview)"),
            "the mode row has Creative, Challenge and \"Live (for now just a solo preview)\"");
        var screen = GetViewport().GetVisibleRect();
        var column = menu.GetNode<VBoxContainer>("Margin/Column");
        var rows = menu.GetNode<VBoxContainer>("Margin/Column/Scroll/Rows");
        var scroll = menu.GetNode<ScrollContainer>("Margin/Column/Scroll");
        Check(column.GetCombinedMinimumSize().Y + 40 <= screen.Size.Y && column.GetCombinedMinimumSize().X + 64 <= screen.Size.X && rows.GetCombinedMinimumSize().Y <= scroll.Size.Y + 1 &&
              rows.GetCombinedMinimumSize().X <= scroll.Size.X + 1,
            $"the three rows and the foot fit the screen without scrolling the page ({column.GetCombinedMinimumSize()} in {screen.Size}; the rows {rows.GetCombinedMinimumSize()} in {scroll.Size})");

        // New team: refused without a name, then made.
        Press(menu.NewTeamButton);
        await Frames(2);
        var panel = menu.NewTeamPanel;
        Check(menu.NewTeamOpen && panel.IsVisibleInTree() && screen.Encloses(panel.GetGlobalRect()) && menu.NameEdit.HasFocus(), $"\"New team\" opens its panel on the screen, the name ready to be typed ({panel.GetGlobalRect()})");
        Press(menu.CreateButton);
        Check(menu.NewTeamOpen && menu.FormNotice.Text == "Give the team a name first." && model.Teams.Count == 0 && !Directory.Exists(layout.TeamsRoot),
            "without a name nothing is made and the panel says so: " + menu.FormNotice.Text);
        var offered = TeamMaker.Offered(_rules);
        Check(menu.KnackChoice.ItemCount == offered.Count + 1 && menu.KnackChoice.GetItemText(0) == "Surprise me" && menu.KnackChoice.Selected == 0,
            "the knack is \"Surprise me\" or one of the island's " + offered.Count);
        menu.NameEdit.Text = "Mira Quinn";
        var costume = menu.Picker.Row.Any(c => c.Id == "stickbear") ? "stickbear" : "";
        menu.Picker.Choose(costume);
        menu.GubbleNameEdit.Text = "Pip";
        Press(menu.GubbleColourButton);
        Press(menu.GubbleColourButton);
        menu.KnackChoice.Select(2);
        Press(menu.CreateButton);
        await Frames(2);
        var made = new TeamStore(layout.TeamFile("mira-quinn"), "mira-quinn").Load();
        Check(!menu.NewTeamOpen && menu.CreatedTeamId == "mira-quinn" && made != null && made.DisplayName == "Mira Quinn" && made.Costume == (costume.Length > 0 ? costume : "prototype") &&
              made.GubbleName == "Pip" && made.GubbleColour == "#a18cc3" && made.Knacks.Single() == new ChosenKnack(1, offered[1].Knack, 1),
            $"the New team panel writes the team file (the name, the costume {costume}, the Gubble Pip in #a18cc3, the knack {offered[1].Knack}) and closes");
        var teamCard = menu.TeamButtons["mira-quinn"];
        string CardText(string name) => (teamCard.FindChild(name, true, false) as Label)?.Text ?? "";
        Check(model.TeamId == "mira-quinn" && teamCard.ButtonPressed && CardText("TeamName") == "Mira Quinn" && CardText("TeamStats").Contains('5') && CardText("TeamStats").Contains("0.4 m") && CardText("TeamStats").Contains('8') &&
              CardText("TeamIslands") == "0 islands visited" && CardText("TeamGubbleLine").StartsWith("Pip · 0 ", StringComparison.Ordinal) && CardText("TeamKnackTrust").Contains(TeamMaker.KnackName(_rules, offered[1].Knack), StringComparison.Ordinal) &&
              CardText("TeamKnackTrust").Contains("earning your trust", StringComparison.Ordinal),
            $"the new team is chosen and its card shows the person, the numbers it plays with, the islands visited, the Gubble with its expertise, the knack and Trust in words: {CardText("TeamStats")} | {CardText("TeamIslands")} | {CardText("TeamGubbleLine")} | {CardText("TeamKnackTrust")}");
        Check(menu.KnackSwap.GetParent<Control>().Visible && menu.KnackSwap.ItemCount == offered.Count + 1, "and its knack can be swapped for free from the first row");

        // Where to, and how.
        await Click(menu.IslandButtons["test_room"]);
        Check(model.RoomId == "test_room" && menu.IslandButtons["test_room"].ButtonPressed, "a real click on an island's card chooses it (the card's own labels let the mouse through)");
        await Click(menu.ModeButtons["live"]);
        Check(model.Mode == null && menu.NoticeLabel.Visible && menu.NoticeLabel.Text.StartsWith(CommandHost.LiveNotYet, StringComparison.Ordinal) && menu.PlayButton.Disabled && !menu.ModeButtons["live"].ButtonPressed,
            $"Live is refused on pressing it, with the words, and Play stays shut: \"{menu.NoticeLabel.Text}\"");
        await Click(menu.ModeButtons["challenge"]);
        var gentle = menu.DifficultyButtons["gentle"];
        Check(model.Mode == "challenge" && model.Difficulty == "gentle" && menu.DifficultyRow.Visible && gentle.ButtonPressed && gentle.Text.Contains("suggested", StringComparison.Ordinal) && !menu.PlayButton.Disabled &&
              menu.DifficultyButtons.Values.All(b => b.Visible) && !menu.CopyRow.Visible,
            "Challenge shows Gentle, Standard and Brave with Gentle suggested for a first Challenge; there is no Creative island to copy, so no copy row");
        await Click(menu.DifficultyButtons["brave"]);
        Check(model.Difficulty == "brave" && menu.DifficultyButtons["brave"].ButtonPressed && !gentle.ButtonPressed && menu.HintLabel.Text.Contains("Brave", StringComparison.Ordinal),
            "Brave chosen: " + menu.HintLabel.Text);
        Press(menu.PlayButton);
        var room = await WaitForRoom();
        if (room is not { } running) return;
        var (world, host) = running;
        Check(host.ModeFixed && host.Mode == "challenge" && host.Difficulty == "brave" && host.SaveTeamIdentity == new SaveTeam("mira-quinn", "challenge", "brave") && world.Room.RoomId == "test_room",
            $"Play boots the room with the team, island, mode and difficulty the menu chose: {host.SaveTeamIdentity}, {world.Room.RoomId}");
        static string Os(string godot) => Path.GetFullPath(ProjectSettings.GlobalizePath(godot));
        Check(Os(CommandHost.SaveRoot) == layout.IslandsRoot("mira-quinn", "challenge") && Os(CommandHost.DefaultPlayerVitalsPath) == layout.VitalsFile("mira-quinn", "challenge") && (!GodotObject.IsInstanceValid(menu) || menu.GetParent() == null),
            "the host's stores are in the team's folder, under the mode the menu chose, and the menu has gone");
        Check(host.Team != null && host.Team.Serialize().SequenceEqual(made!.Serialize()) && world.Player.CharacterId == (costume.Length > 0 ? costume : "") && world.Companion.CompanionName == "Pip" && world.Companion.AuraColor is { } aura && aura.IsEqualApprox(new Color("a18cc3")),
            $"the room wears the team's costume ({world.Player.CharacterId}) and the Gubble its name and colour ({world.Companion.CompanionName}, {world.Companion.AuraColor?.ToHtml(false)})");
        Check(host.Knacks.Count == 1 && host.Knacks.ContainsKey(offered[1].Knack), $"the knack chosen in the panel reaches the body ({string.Join(",", host.Knacks.Keys)})");
        var last = LastGame.Read(layout);
        Check(last is { TeamId: "mira-quinn", RoomId: "test_room", Mode: "challenge", Difficulty: "brave" }, "the game is remembered for Continue: " + last);
        // "Gubble: connected" or "waiting for your AI" in the room's first seconds.
        var note = world.GetNodeOrNull<GubbleLinkNote>("GubbleLinkNote");
        await Frames(3);
        Check(note != null && (note.Words == GubbleLinkNote.Waiting || note.Words == GubbleLinkNote.NoLink) && note.Words == note.Status,
            $"the room's first seconds say what the Gubble's link is ({note?.Words})");
        if (note == null) return;
        note._Process(GubbleLinkNote.FirstSeconds + 1);
        Check(note.Words == "" && note.Status.Length > 0, "and the line goes away after a few seconds");
        Check(world.GetNode<RoomHud>("RoomHud").Fullscreen == false && world.GetNode<RoomHud>("RoomHud").SettingsPanel != null, "(the HUD is the game's, with the settings panel)");
        await CloseGame();

        // Continue: the last team, island and mode, in one press.
        var again = await OpenMenu();
        Check(again.Model.ContinueWords() == "Mira Quinn on Test room, Challenge (Brave)" && !again.ContinueButton.Disabled && again.ContinueButton.Text == "Continue: Mira Quinn on Test room, Challenge (Brave)" &&
              again.TeamButtons.Count == 1,
            $"the menu opens with Continue: \"{again.ContinueButton.Text}\"");
        Press(again.ContinueButton);
        var second = await WaitForRoom();
        if (second is { } playing)
            Check(playing.Host.SaveTeamIdentity == new SaveTeam("mira-quinn", "challenge", "brave") && playing.Host.Difficulty == "brave" && playing.World.Room.RoomId == "test_room",
                $"Continue boots the last team, island and mode: {playing.Host.SaveTeamIdentity}");
        await CloseGame();

        // The command line chooses first: --menu with a team, a room and a mode.
        var named = await OpenMenu("--team=mira-quinn", "--room=test_room", "--mode=creative");
        Check(named.Model.TeamId == "mira-quinn" && named.Model.RoomId == "test_room" && named.Model.Mode == "creative" && named.Model.Notice.Length == 0 && !named.PlayButton.Disabled,
            "a launch with --menu keeps what the command line asked: the team, the room, the mode");

        // "Redraw yourself any day; keep everything": the panel again, filled with the team, to change its name, costume and Gubble.
        Press(named.EditTeamButton);
        await Frames(2);
        Check(named.NewTeamOpen && named.EditingTeamId == "mira-quinn" && named.FormTitle.Text == "EDIT YOUR TEAM" && named.CreateButton.Text == "Save changes" && named.NameEdit.Text == "Mira Quinn" &&
              named.GubbleNameEdit.Text == "Pip" && named.Picker.ChosenId == costume && named.GubbleColourIndex == 3 && named.KnackChoice.GetItemText(0) == "Keep my knack",
            "\"Edit this team\" opens the panel filled with the team's name, costume and Gubble, and its knack kept unless another is chosen");
        var before = new TeamStore(layout.TeamFile("mira-quinn"), "mira-quinn").Load()!;
        named.NameEdit.Text = "Mira Q";
        named.Picker.Choose("");
        named.GubbleNameEdit.Text = "Pib";
        Press(named.GubbleColourButton);
        Press(named.CreateButton);
        await Frames(2);
        var after = new TeamStore(layout.TeamFile("mira-quinn"), "mira-quinn").Load()!;
        Check(!named.NewTeamOpen && after.DisplayName == "Mira Q" && after.Costume == "prototype" && after.GubbleName == "Pib" && after.GubbleColour == "#75965c" &&
              after.TeamId == before.TeamId && after.CreatedUtc == before.CreatedUtc && after.Trust == before.Trust && after.Knacks.SequenceEqual(before.Knacks) && named.Model.TeamId == "mira-quinn" &&
              (named.TeamButtons["mira-quinn"].FindChild("TeamName", true, false) as Label)?.Text == "Mira Q",
            "Save changes rewrites the name, the costume and the Gubble's name and colour, and keeps the id, the age, Trust and the knack; the card follows");
        Press(named.EditTeamButton);
        named.NameEdit.Text = "  ";
        Press(named.CreateButton);
        Check(named.NewTeamOpen && named.FormNotice.Text == "Give the team a name first." && new TeamStore(layout.TeamFile("mira-quinn"), "mira-quinn").Load()!.DisplayName == "Mira Q",
            "an empty name is refused in the panel and the team is as it was");
        Press(named.CancelButton);
        Check(!named.NewTeamOpen && named.EditingTeamId == null, "Cancel puts the panel away");
        await CloseGame();
    }

    /// <summary>
    /// An island that cannot be loaded sends the player back to the menu with a word about it, never to a blank window. Run by hand
    /// (-- --broken-room): the room's own refusal is an engine error on stderr, which the runners treat as a failure.
    /// </summary>
    private async Task TestBrokenIsland()
    {
        var layout = new TeamLayout(ProjectSettings.GlobalizePath(_scene));
        var broken = await OpenMenu();
        broken.Model.SelectTeam("mira-quinn");
        broken.Model.SelectIsland("plain_isle");
        broken.Model.ChooseMode("creative");
        Press(broken.PlayButton);
        MainMenu? back = null;
        for (var waiting = System.Diagnostics.Stopwatch.StartNew(); waiting.Elapsed.TotalSeconds < 30 && (back = _boot!.Menu) == null;) await Frames(2);
        await Frames(3);
        Check(back != null && _boot!.World == null && back.NoticeLabel.Visible && back.NoticeLabel.Text.StartsWith("That island could not be opened (", StringComparison.Ordinal) && back.NoticeLabel.Text.EndsWith("It was left as it was.", StringComparison.Ordinal) &&
              LastGame.Read(layout) is { RoomId: "test_room" },
            "an island that cannot be loaded brings the menu back with a word about it, and Continue still opens the last game that was played: " + back?.NoticeLabel.Text);
        await CloseGame();
    }

    // ---- the copy, pressed from the menu ----

    private string Creation(string name) => new JsonObject
    {
        ["schema"] = "garage_game.command", ["version"] = 1, ["action_id"] = "menu-" + Guid.NewGuid().ToString("N")[..12], ["room_id"] = "test_room", ["op"] = "creation.place",
        ["args"] = new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["schema"] = "garage_game.creation", ["version"] = 1, ["name"] = name, ["seed"] = 1, ["mount"] = "ground",
                ["parts"] = new JsonArray(new JsonObject
                {
                    ["id"] = "base", ["shape"] = "box", ["position_m"] = new JsonArray(0, 0.06, 0), ["rotation_deg"] = new JsonArray(0, 0, 0), ["size_m"] = new JsonArray(0.12, 0.12, 0.12), ["material"] = "wood",
                }),
                ["nodes"] = new JsonArray(new JsonObject { ["id"] = "use", ["op"] = "interact", ["part_id"] = "base", ["params"] = new JsonObject() }),
                ["edges"] = new JsonArray(),
            },
            ["placement"] = new JsonObject { ["position_m"] = new JsonArray(1.5, 0, -1.0) },
        },
    }.ToJsonString();

    private async Task TestCopyThroughTheMenu()
    {
        var layout = SceneLayout("copy-scene");
        TeamMaker.Create(layout, _rules, "Theo", "", "Wisp", "#65b9b0", "", Now);
        // A Creative game on the fixture island: a build, and a nature change (which the Creative island can undo).
        var menu = await OpenMenu();
        menu.Model.SelectTeam("theo");
        menu.Model.SelectIsland("test_room");
        menu.Model.ChooseMode("creative");
        menu.Refresh();
        Check(!menu.CopyRow.Visible && menu.SavesLine.Text.StartsWith("No Creative island here yet", StringComparison.Ordinal), "a Creative game on a new island: \"" + menu.SavesLine.Text + "\"");
        Press(menu.PlayButton);
        var first = await WaitForRoom();
        if (first is not { } creative) return;
        var placed = JsonNode.Parse(creative.Host.Handle(Creation("Menu totem"), CommandHost.PlayerPrincipal))!.AsObject();
        // (The indoor fixture has no plants to grow, and the flower trail went with Bloom: a small Fire on its wooden floor beside the
        // Gubble, away from the build, is a durable nature change with an undo step of its own.)
        var fireAt = creative.Host.Companion!.GlobalPosition + new Godot.Vector3(0.15f, 0, 0);
        // Decision 72: the team knows Fire, as after the first rest (the one helper every suite that lights fires uses).
        creative.Host.LearnFireForTests();
        var preview = creative.Host.PlayerNaturePreview("fire", fireAt, 0.2f, 3);
        var bloomed = preview["ok"]?.GetValue<bool>() == true
            ? creative.Host.PlayerNatureCast("fire", fireAt, 0.2f, 3, preview["data"]?["preview_id"]?.GetValue<string>())
            : preview;
        Check(placed["ok"]?.GetValue<bool>() == true && bloomed["ok"]?.GetValue<bool>() == true && creative.Host.NatureCanUndo,
            $"(a build and a nature change made in Creative: the island can undo) {placed["error"]?["message"]} | {bloomed["error"]?["message"]} | {creative.Host.Mode}");
        if (placed["created"] == null) return;
        // The small fire burns out before the game closes (a fire still burning would go on burning in the copy).
        for (var waited = 0; waited < 60 * 20 && creative.Host.Nature.Fire != null; waited++) await Frames(1);
        Check(creative.Host.Nature.Fire == null, "(the small fire has burnt out)");
        var prefix = creative.World.Room.ManifestSha256[..16];
        var creativeSave = layout.IslandSave("theo", "creative", "test_room", prefix);
        var challengeSave = layout.IslandSave("theo", "challenge", "test_room", prefix);
        var built = placed["created"]![0]!.GetValue<string>();
        await CloseGame();
        var original = File.ReadAllBytes(creativeSave);
        var originalNature = JsonNode.Parse(original)!["nature"]!.AsObject();
        Check(JsonNode.Parse(original)!["instances"]!.AsArray().Count == 1 && originalNature["undo"]!.AsArray().Count > 0, "the Creative save holds the build and the undo steps");

        // The menu now offers the copy.
        var again = await OpenMenu();
        again.Model.SelectTeam("theo");
        Press(again.IslandButtons["test_room"]);
        Check(again.IslandButtons["test_room"].FindChild("IslandSaves", true, false) is Label { Text: var saves } && saves.StartsWith("Creative ", StringComparison.Ordinal),
            "the island's card lists this team's saves by mode");
        Press(again.ModeButtons["challenge"]);
        await Frames(1);
        Check(again.CopyRow.Visible && again.StartFreshButton.ButtonPressed && !again.CopyButton.ButtonPressed && again.CopyButton.Text == "Copy my Creative island into Challenge",
            "on an island with a Creative save, Challenge offers \"Start fresh\" and \"Copy my Creative island into Challenge\"");
        Press(again.DifficultyButtons["standard"]);
        Press(again.CopyButton);
        Check(again.CopyButton.ButtonPressed && again.Model.CopyCreative && again.CopyNote.Text.Contains("count for no Challenge first", StringComparison.Ordinal) && again.HintLabel.Text.Contains("with your Creative island", StringComparison.Ordinal),
            "the copy chosen says what it carries and what it does not: " + again.CopyNote.Text);
        Press(again.PlayButton);
        var second = await WaitForRoom();
        if (second is not { } challenge) return;
        var copy = JsonNode.Parse(File.ReadAllBytes(challengeSave))!.AsObject();
        Check(challenge.Host.Mode == "challenge" && challenge.Host.Difficulty == "standard" && !challenge.Host.IslandHeld && challenge.Host.SaveTeamIdentity == new SaveTeam("theo", "challenge", "standard") &&
              copy["instances"]!.AsArray().Count == 1,
            $"the copy opens as a Challenge island at the difficulty chosen, with the build in it ({challenge.Host.SaveTeamIdentity}, held {challenge.Host.IslandHeld})");
        var listed = JsonNode.Parse(challenge.Host.Handle(new JsonObject
        {
            ["schema"] = "garage_game.query", ["version"] = 1, ["query_id"] = "menu-entities", ["room_id"] = "test_room", ["op"] = "entities.list",
            ["args"] = new JsonObject { ["filter"] = new JsonObject { ["kind"] = "creation" } },
        }.ToJsonString(), CommandHost.PlayerPrincipal))!.AsObject();
        Check(listed["data"]?["items"]?.AsArray().Any(i => i?["id"]?.GetValue<string>() == built) == true && !challenge.Host.NatureCanUndo && challenge.Host.Nature.Undo.Count == 0,
            "the host loads it: the build stands on the island, and there is no undo in Challenge");
        Check(File.ReadAllBytes(creativeSave).SequenceEqual(original), "the Creative island is as it was");
        Check(CarriedBuilds.CarriedOn(layout, "theo", "test_room").SequenceEqual(new[] { built }), "and the build is written down as carried over: " + string.Join(",", CarriedBuilds.CarriedOn(layout, "theo", "test_room")));
        await CloseGame();

        var last = await OpenMenu();
        last.Model.SelectTeam("theo");
        last.Model.SelectIsland("test_room");
        last.Model.ChooseMode("challenge");
        last.Refresh();
        Check(!last.CopyRow.Visible && last.Model.FixedDifficulty == "standard" && last.DifficultyButtons.Values.Count(b => b.Visible) == 1 && last.DifficultyButtons["standard"].Visible && last.SavesLine.Text.StartsWith("Your Challenge island here", StringComparison.Ordinal),
            "afterwards the island has its Challenge save, which shows the difficulty it keeps and offers no second copy: " + last.SavesLine.Text);
        await CloseGame();
    }
}
