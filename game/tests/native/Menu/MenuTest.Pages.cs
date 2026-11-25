using Godot;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native.Saves;
using GarageGame.Native.Ui;

namespace GarageGame.Tests;

/// <summary>
/// What the integration of Run 4's menu lane added between the two pages and the room (the lane's change requests, wave 1):
/// <list type="bullet">
/// <item>New game asks the team's lease also where there is no game to put away: after the wait a team still up is a game
/// still open, and New game waits until it is closed, as the page says;</item>
/// <item>a land's card on the Choose page shows the name the title page shows (the room's name without the word "Landscape"),
/// and the lines under it say the same name;</item>
/// <item>the Choose page has a "Title page" button in plain sight, also where the Choose page came first, and what was chosen
/// stands on the way back;</item>
/// <item>"Back to the menu" names the room that was played by its folder, so the menu opens on that very room: a room launched
/// from a folder of its own, under a listed island's id, comes back with its Continue and is not read as that island rebuilt.</item>
/// </list>
/// </summary>
public partial class MenuTest
{
    // ---- New game with nothing to put away still asks the team's lease ----

    private void TestStartStillOpenNoGame()
    {
        var layout = new TeamLayout(Folder("start-open-no-game"));
        TeamMaker.Create(layout, _rules, "Jets", "", "", "#65b9b0", "", Now);
        var plain = IslandCatalog.Scan(new[] { IslandsFolder(), "res://rooms" }).First(i => i.RoomId == "plain_isle");
        // The team's Challenge game is the one that is open in another window; the player asks for a first Creative game.
        WriteGame(layout, "jets", "challenge", plain, "standard");
        var uptime = TimeSpan.FromSeconds(100);
        var model = NewModel(layout);
        model.Uptime = () => uptime;
        model.SelectTeam("jets");
        model.SelectIsland("plain_isle");
        model.ChooseMode("creative");
        var folder = layout.TeamFolder("jets");
        var before = Hashes(folder);
        var noGame = model.Offer.Case == StartCase.NoGame && !model.Offer.Asks && model.WhyNotReady == null;
        using (var other = PlayLease.TryPlay(layout, "jets")!)
        {
            var leaving = model.NewGame();
            var leavingWords = model.Notice;
            uptime += PlayLease.LeavingWait;
            var hold = model.Hold();
            var refused = model.NewGame();
            Check(noGame && leaving == null && leavingWords == MenuModel.StillClosingWords && hold == TeamHold.StillOpen && refused == null && !model.Asking && model.Notice == PlayLease.StillOpen &&
                  Same(before, Hashes(folder)) && NothingPutAway(layout),
                $"no game in this mode and the team still up after the wait: New game is refused (\"{model.Notice}\"), as the page says it waits, and nothing starts beside the open game ({(refused == null ? "no launch" : "a launch")})");
        }
        var started = model.NewGame();
        Check(model.Hold() == TeamHold.Free && started is { TeamId: "jets", Mode: "creative" } && model.Notice.Length == 0 && Same(before, Hashes(folder)) && NothingPutAway(layout),
            "once that game is closed, New game starts the first game at once: nothing asked, nothing put away");
    }

    // ---- the Choose page: the land's name on its card, and the way back to the title page ----

    private async Task TestChoosePageNamesAndWayBack()
    {
        var layout = SceneLayout("pages");
        TeamMaker.Create(layout, _rules, "Jets", "", "Wisp", "#65b9b0", "", Now);
        var rooms = Folder("pages-rooms");
        Write(Path.Combine(rooms, "z", "room.json"), Manifest("landscape_zebra", "Landscape zebra", tutorial: false, shape: false));
        IslandCatalog.RootsOverride = new[] { ProjectSettings.LocalizePath(rooms), IslandsFolder(), "res://rooms" };

        // The card shows the land's name, and so do the lines under it.
        var menu = await OpenMenu("--room=landscape_zebra");
        var card = (menu.IslandButtons["landscape_zebra"].FindChild("IslandName", true, false) as Label)?.Text;
        var askMode = menu.SavesLine.Text;
        await Click(menu.ModeButtons["creative"]);
        var zebra = menu.Model.Island!;
        var hint = menu.HintLabel.Text;
        WriteGame(layout, "jets", "creative", zebra);
        new LastGame("jets", "landscape_zebra", "creative", null, Now).Write(layout);
        menu.Model.Reload();
        menu.Refresh();
        await Frames(2);
        Check(card == "Zebra" && zebra.DisplayName == "Landscape zebra" && zebra.LandName == "Zebra" && askMode == "Zebra: choose Creative or Challenge." && hint == "Jets on Zebra, Creative" &&
              Shown(menu.ContinueButton) && menu.ContinueLine.Text == "Jets on Zebra, Creative" && menu.Model.ContinueWords() == "Jets on Zebra, Creative",
            $"a land's card on the Choose page shows its name as the title page does (\"{card}\" for the manifest's \"{zebra.DisplayName}\"), and the lines under it say the same: \"{askMode}\", \"{hint}\", \"{menu.ContinueLine.Text}\"");

        // The button is in the header, on the screen, and only where there is a title page to go back to.
        var button = menu.TitlePageButton;
        var inHeader = Shown(button) && button.Text == MenuModel.TitlePageWords && button.Text == "Title page" && button.GetParent() == menu.QuitButton.GetParent() && button.GetIndex() < menu.QuitButton.GetIndex() &&
                       GetViewport().GetVisibleRect().Encloses(button.GetGlobalRect()) && FootFits(menu) && !button.GetGlobalRect().Intersects(menu.QuitButton.GetGlobalRect());
        var back = menu.BackToTitle;
        menu.BackToTitle = null;
        var hidden = !Shown(button);
        menu.BackToTitle = back;
        Check(inHeader && hidden && Shown(button), $"the Choose page has a \"{button.Text}\" button in its header, beside Quit and on the screen; a page with no title page to go back to shows none");
        await CloseGame();

        // From the title page: Choose, another land chosen, then the button: the title page, with that choice standing.
        var title = await OpenTitleBoot("--room=test_room", "--mode=creative");
        var shared = title.Model;
        await Click(title.ChooseButton);
        var choose = _boot!.Menu;
        if (choose == null) { Check(false, "\"Choose\" opens the Choose page"); return; }
        await Click(choose.IslandButtons["plain_isle"]);
        await Click(choose.TitlePageButton);
        var again = _boot.Title;
        Check(again != null && _boot.Menu == null && _boot.World == null && ReferenceEquals(again.Model, shared) && shared.RoomId == "plain_isle" && shared.Mode == "creative" && again.LandName.Text == "Plain Isle",
            $"a click on \"Title page\" brings the title page back with what was chosen on the Choose page, not the launch's: \"{again?.LandName.Text}\"");
        await CloseGame();

        // Where the Choose page came first (--choose) Esc has no title page to go back to; the button has, and after it Esc has too.
        var line = new[] { MenuBoot.ChooseArgument, "--room=test_room", "--mode=creative" };
        MainMenu.LaunchArguments = () => line;
        _boot = new MenuBoot { AutoOpen = false, OnNewTitle = Hermetic };
        AddChild(_boot);
        _boot.OpenFirst(line);
        await Frames(3);
        var first = _boot.Menu;
        if (first == null) { Check(false, "--choose opens the Choose page first"); return; }
        await PressKey(Key.Escape);
        var stayed = _boot.Menu == first && _boot.Title == null && Shown(first.TitlePageButton);
        await Click(first.IslandButtons["plain_isle"]);
        await Click(first.TitlePageButton);
        var came = _boot.Title;
        var cameBack = came != null && _boot.Menu == null && came.Model.RoomId == "plain_isle" && came.LandName.Text == "Plain Isle";
        if (came != null) await Click(came.ChooseButton);
        var chooseAgain = _boot.Menu != null && _boot.Menu.IslandButtons["plain_isle"].ButtonPressed;
        await PressKey(Key.Escape);
        Check(stayed && cameBack && chooseAgain && _boot.Title != null && _boot.Menu == null && _boot.Model!.RoomId == "plain_isle",
            $"under --choose Esc stays on the Choose page (no title page has been up: {stayed}); \"Title page\" opens it with the choice standing ({cameBack}), and from then on Esc on the Choose page comes back too");
        await CloseGame();
        MainMenu.LaunchArguments = null;
    }

    // ---- "Back to the menu" comes back to the room that was played ----

    private async Task TestBackToTheRoomPlayed()
    {
        var engine = new[] { "--path", "C:/dev/game" };
        var named = MenuBoot.RelaunchArguments(engine, new[] { "--build=abc1234", "--menu", "--room=somewhere/else", "--mode=creative", "--fullscreen" }, null, "C:/rooms/far/test_room");
        Check(named.SequenceEqual(new[] { "--path", "C:/dev/game", "--", "--build=abc1234", "--fullscreen", "--room=C:/rooms/far/test_room", "--menu" }) &&
              MenuBoot.RelaunchArguments(engine, new[] { "--room=x" }, null, " ").SequenceEqual(new[] { "--path", "C:/dev/game", "--", "--menu" }) &&
              MenuBoot.RelaunchArguments(engine, new[] { "--room=x" }).SequenceEqual(new[] { "--path", "C:/dev/game", "--", "--menu" }),
            "\"Back to the menu\" names the room that was played by its folder, once, in place of the room the launch named; with no room up it names none: " + string.Join(" ", named));

        var layout = SceneLayout("back-room");
        TeamMaker.Create(layout, _rules, "Nest", "", "Wisp", "#65b9b0", "", Now);
        var own = RoomOfItsOwn("back-room-far", "Far room");
        var listed = IslandCatalog.Scan().First(i => i.RoomId == "test_room");
        // A room shortcut's line: the menu, a room's folder (a room of its own under a listed island's id), a mode, no team.
        var line = new[] { "--no-playtest-record", "--menu", "--room=" + own, "--mode=creative" };
        var nobodyUp = MenuBoot.RoomUp(GetTree()) == null;
        var menu = await OpenMenu(line);
        await Click(menu.NewGameButton);
        var first = await WaitForRoom();
        if (first is not { } game) return;
        var up = MenuBoot.RoomUp(GetTree());
        var back = MenuBoot.RelaunchArguments(engine, line, null, up);
        Check(nobodyUp && up == own && game.World.RoomDirectory == own && game.World.Room.ManifestSha256[..16] != listed.ManifestPrefix && back.Count(a => a.StartsWith("--room=", StringComparison.Ordinal)) == 1 &&
              back.Contains("--room=" + own) && back[^1] == MenuBoot.MenuArgument && !back.Any(a => a.StartsWith("--mode=", StringComparison.Ordinal) || a.StartsWith("--team=", StringComparison.Ordinal)),
            $"with a room up, the relaunch names that room's own folder ({up}), and no team or mode: " + string.Join(" ", back.SkipWhile(a => a != "--").Skip(1)));
        // The line the game itself runs: BackToMenu starts a process with exactly BackToMenuArguments, from this process's own
        // command line and the room that is up, so the played room cannot drop out of it unseen.
        var run = MenuBoot.BackToMenuArguments(GetTree());
        Check(run.Count(a => a.StartsWith("--room=", StringComparison.Ordinal)) == 1 && run.Contains("--room=" + own) && run[^1] == MenuBoot.MenuArgument && run.Contains("--"),
            "\"Back to the menu\" as the game runs it (BackToMenu's own command line) names the room that is up: " + string.Join(" ", run.SkipWhile(a => a != "--").Skip(1)));
        await CloseGame();

        // The menu as that relaunch opens it: the title page, on the room that was played, with its Continue.
        var user = back.SkipWhile(a => a != "--").Skip(1).ToArray();
        var title = await OpenTitleBoot(user);
        Check(MenuBoot.ShowsMenu(user, "Windows") && _boot!.World == null && title.Model.Island is { } island && island.Directory == own && title.Model.TeamId == "nest" && title.Model.Mode == "creative" &&
              title.Model.Offer.Case == StartCase.SaveHere && Shown(title.ContinueButton) && title.ContinueLine.Text == "Nest on Far room, Creative" && title.LandName.Text == "Far room" &&
              title.HintLabel.Text != MenuModel.RebuiltWords && NothingPutAway(layout),
            $"back at the menu the title page opens on the room that was played, with its Continue, and opens nothing by itself ({title.Model.Offer.Case}): \"{title.ContinueLine.Text}\"");
        await CloseGame();

        // The control, the relaunch as it was before (no room named): the listed island of that id, read as rebuilt, no Continue.
        var bare = await OpenTitleBoot(MenuBoot.RelaunchArguments(engine, line).SkipWhile(a => a != "--").Skip(1).ToArray());
        Check(bare.Model.Island is { } other && other.Directory != own && other.ManifestPrefix == listed.ManifestPrefix && bare.Model.Offer.Case == StartCase.Rebuilt && !Shown(bare.ContinueButton),
            $"(without the folder the menu lands on the listed island of that id, which reads as rebuilt and has no Continue: {bare.Model.Offer.Case}; the folder is what tells the two apart)");
        await CloseGame();
        MainMenu.LaunchArguments = null;
    }
}
