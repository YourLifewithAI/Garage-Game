using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Saves;
using GarageGame.Native.Ui;
using Godot;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The player's character (the characters round, K6), now in the opening menu's New team panel (AvatarPicker, lifted out of the
/// Customize panel): a row of the plain figure and the characters the library supplies, never the Gubble; choosing by mouse or
/// keyboard reports the choice; the player's colour button is for the plain figure only, and the machine's profile keeps its colour
/// beside every other key. In the room the body wears the team file's costume and the Gubble takes its name and colour
/// (RoomHud.UseTeam); an older profile's character still restores in a fixture with no team, and a bad saved value is the plain figure.
/// </summary>
public partial class PlayHudTest
{
    /// <summary>The row the supplied list must give, in order: the plain figure, then the family's four.</summary>
    private static readonly string[] CharacterRowNames = { CharacterChoice.PrototypeName, "Potato Man", "Tomato Man", "Cloudpuff", "Stickbear" };

    /// <summary>What the library might supply, with what the row must leave out: the Gubble, a second plain figure, a non-token and a repeat.</summary>
    private static IReadOnlyList<CharacterChoice> SuppliedCharacters(Texture2D? potato) => new[]
    {
        new CharacterChoice(CharacterChoice.GubbleId, "the Gubble"),
        new CharacterChoice("potato_man", "Potato Man", potato),
        new CharacterChoice("tomato_man", "Tomato Man"),
        new CharacterChoice("", "Nobody"),
        new CharacterChoice("Cloud Puff!", "Not a token"),
        new CharacterChoice("cloudpuff", "Cloudpuff"),
        new CharacterChoice("stickbear", "  Stickbear\t"),
        new CharacterChoice("potato_man", "Potato Man again"),
    };

    private async Task TestCharacterChoice()
    {
        var player = _world.Player;
        var companion = _world.Companion;
        var profileFile = ProjectSettings.GlobalizePath(HudProfile);
        var profileBefore = System.IO.File.Exists(profileFile) ? System.IO.File.ReadAllBytes(profileFile) : null;
        var previousTold = player.CharacterChanged;
        var told = new List<string>();
        player.CharacterChanged = id => { told.Add(id); previousTold?.Invoke(id); };
        // The room supplies the shipped characters (RoomWorld); this test supplies its own, so it starts from none.
        var roomChoices = _hud.CharacterChoices;
        _hud.CharacterChoices = null;
        AvatarPicker? picker = null;
        // Above the HUD's own layer, so a click on the picker reaches it and not the HUD under it.
        var pickerLayer = new CanvasLayer { Layer = 50 };
        AddChild(pickerLayer);
        try
        {
            // The picker is the Customize panel's character half, lifted into the menu's New team panel: the plain figure alone, chosen, with its colour button.
            picker = new AvatarPicker { Choices = null, Position = new Vector2(20, 20) };
            pickerLayer.AddChild(picker);
            await Frames(2);
            Check(picker.Row.Select(c => c.DisplayName).SequenceEqual(new[] { CharacterChoice.PrototypeName }) &&
                  picker.Buttons.Count == 1 && picker.Buttons[0].ButtonPressed && picker.ColourButton.IsVisibleInTree() && !picker.ColourNote.Visible && picker.ChosenId == "",
                "with no characters supplied the picker shows only the plain figure, chosen, and \"Change player color\" is there");

            var root = ProjectSettings.GlobalizePath("res://").TrimEnd('/').GetBaseDir();
            var potato = CharacterChoice.LoadThumbnail(root + "/assets/characters/potato_man");
            Check(potato != null && Math.Max(potato.GetWidth(), potato.GetHeight()) == CharacterChoice.ThumbnailPx &&
                  CharacterChoice.LoadThumbnail(root + "/assets/characters/no_such_character") == null,
                $"a character folder's {CharacterChoice.ThumbnailFile} loads as a {CharacterChoice.ThumbnailPx} px thumbnail; a folder without one gives none");
            // Review: a thumbnail is bounded before it is decoded, by the file's length and by the size its PNG header claims.
            var limits = ProjectSettings.GlobalizePath("user://thumbnail_limits");
            try
            {
                string Folder(string name, byte[] png)
                {
                    var folder = System.IO.Path.Combine(limits, name);
                    System.IO.Directory.CreateDirectory(folder);
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, CharacterChoice.ThumbnailFile), png);
                    return folder;
                }
                byte[] Png(int width, int height)
                {
                    var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
                    image.Fill(new Color(0.5f, 0.6f, 0.7f));
                    return image.SavePngToBuffer();
                }
                var wide = Png(CharacterChoice.MaxThumbnailSidePx + 1, 8);
                var widest = Png(CharacterChoice.MaxThumbnailSidePx, 8);
                var padded = Png(16, 16).Concat(new byte[CharacterChoice.MaxThumbnailBytes]).ToArray();
                var forged = Png(16, 16);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(forged.AsSpan(16), 100_000);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(forged.AsSpan(20), 100_000);
                var fits = CharacterChoice.LoadThumbnail(Folder("widest", widest));
                Check(CharacterChoice.PngSize(wide, out var w, out var h) && w == CharacterChoice.MaxThumbnailSidePx + 1 && h == 8 && !CharacterChoice.PngSize(padded.AsSpan(1), out _, out _) &&
                      CharacterChoice.LoadThumbnail(Folder("wide", wide)) == null && CharacterChoice.LoadThumbnail(Folder("padded", padded)) == null &&
                      CharacterChoice.LoadThumbnail(Folder("forged", forged)) == null && fits != null && fits.GetWidth() == CharacterChoice.ThumbnailPx,
                    $"a thumbnail over {CharacterChoice.MaxThumbnailSidePx} px a side or {CharacterChoice.MaxThumbnailBytes} bytes, or whose header claims 100,000 px, is never decoded (a swatch instead); {CharacterChoice.MaxThumbnailSidePx} px still loads");
            }
            finally
            {
                if (System.IO.Directory.Exists(limits)) System.IO.Directory.Delete(limits, true);
            }
            picker.Choices = () => SuppliedCharacters(potato);
            picker.Refresh();
            await Frames(2);
            var buttons = picker.Buttons;
            var (colourButton, note, chosenLabel) = (picker.ColourButton, picker.ColourNote, picker.ChosenLabel);
            var picked = new List<string>();
            picker.ChoiceChanged += id => picked.Add(id);
            Check(picker.Row.Select(c => c.DisplayName).SequenceEqual(CharacterRowNames) && buttons.Select(b => b.Text).SequenceEqual(CharacterRowNames) &&
                  picker.Row.Select(c => c.Id).SequenceEqual(new[] { "", "potato_man", "tomato_man", "cloudpuff", "stickbear" }),
                "refreshed, the row lists the plain figure, Potato Man, Tomato Man, Cloudpuff and Stickbear in order: never the Gubble, a second plain figure, a non-token or a repeat");
            Check(buttons.All(b => b.FocusMode == Control.FocusModeEnum.All && b.ToggleMode && b.Icon != null && b.IsVisibleInTree()) && buttons[1].Icon == potato &&
                  buttons[2].Icon != buttons[3].Icon,
                "each is a focusable button with a picture: Potato Man's own thumbnail, a swatch of its own for each of the others");
            picker.FocusChosen();
            Check(buttons[0].ButtonPressed && buttons.Skip(1).All(b => !b.ButtonPressed) && chosenLabel.Text == "Your character: Plain figure" &&
                  GetViewport().GuiGetFocusOwner() == buttons[0],
                "the chosen one, the plain figure, is pressed and named, and holds the keyboard focus");
            Check(buttons.All(b => Mathf.IsEqualApprox(b.GlobalPosition.Y, buttons[0].GlobalPosition.Y)), "with five characters the picker holds the row on one line");

            // The mouse: a click on Potato Man, then another.
            await ClickThroughViewport(buttons[1]);
            Check(picker.ChosenId == "potato_man" && picked.SequenceEqual(new[] { "potato_man" }) && buttons[1].ButtonPressed && !buttons[0].ButtonPressed &&
                  chosenLabel.Text == "Your character: Potato Man",
                $"a click on Potato Man chooses him: the choice told once, his button pressed and named ({string.Join(",", picked)})");
            await ClickThroughViewport(buttons[1]);
            Check(picked.Count == 1 && buttons[1].ButtonPressed && picker.ChosenId == "potato_man", "a second click on him changes nothing and tells nothing; he stays pressed");
            Check(!colourButton.Visible && note.IsVisibleInTree() && note.Text == AvatarPicker.DrawnColorsNote,
                "with a drawn character chosen the player's colour button hides and a short note says why");

            // The keyboard: the arrows move along the row and Enter chooses.
            buttons[1].GrabFocus();
            await TapKey(Key.Right);
            var focused = GetViewport().GuiGetFocusOwner();
            await TapKey(Key.Enter);
            Check(focused == buttons[2] && picker.ChosenId == "tomato_man" && picked.SequenceEqual(new[] { "potato_man", "tomato_man" }) && buttons[2].ButtonPressed &&
                  !buttons[1].ButtonPressed,
                $"the keyboard works too: from Potato Man the right arrow moves to Tomato Man and Enter chooses him ({focused?.Name})");
            await TapKey(Key.Left);
            await TapKey(Key.Left);
            focused = GetViewport().GuiGetFocusOwner();
            await TapKey(Key.Enter);
            Check(focused == buttons[0] && picker.ChosenId == "" && picked.SequenceEqual(new[] { "potato_man", "tomato_man", "" }) && buttons[0].ButtonPressed &&
                  colourButton.IsVisibleInTree() && !note.Visible,
                $"left twice to the plain figure and Enter choose the plain figure again, and the player's colour button comes back ({focused?.Name})");
            await TapKey(Key.Right);
            var right = GetViewport().GuiGetFocusOwner();
            await TapKey(Key.Left);
            await TapKey(Key.Left);
            var wrapped = GetViewport().GuiGetFocusOwner();
            Check(right == buttons[1] && wrapped == buttons[buttons.Count - 1] && picker.ChosenId == "" && picked.Count == 3,
                $"with the colour button showing under the row the arrows still move along it (right to Potato Man) and wrap at its ends (left from the plain figure to Stickbear), choosing nothing ({right?.Name}, {wrapped?.Name})");

            // The player's colour button colours the plain figure, and its swatch with it; the machine's profile keeps the colour beside every other key.
            var config = new ConfigFile();
            config.Load(HudProfile);
            config.SetValue("profile", "a_later_setting", "kept");
            config.SetValue("later_section", "a_value", 3);
            config.Save(HudProfile);
            var kept = ProfileSnapshot();
            kept.Remove("profile/player_color");
            var colourBefore = picker.PlayerColour;
            var swatchBefore = buttons[0].Icon;
            colourButton.EmitSignal(BaseButton.SignalName.Pressed);
            Check(picker.PlayerColour == (colourBefore + 1) % MenuProfile.Palette.Length && buttons[0].Icon != swatchBefore && ProfileValue("player_color").AsInt32() == picker.PlayerColour,
                "\"Change player color\" cycles the plain figure's colour, its swatch follows, and the machine's profile keeps it");
            var saved = ProfileSnapshot();
            var lost = kept.Where(pair => !saved.TryGetValue(pair.Key, out var value) || value != pair.Value).Select(pair => $"{pair.Key}: {pair.Value} -> {saved.GetValueOrDefault(pair.Key, "(gone)")}");
            Check(!lost.Any(), $"and every other key of the profile stays exactly as it was ({string.Join(", ", kept.Keys)}){string.Concat(lost.Select(l => "; changed " + l))}");
            for (var i = 0; i < 4; i++) colourButton.EmitSignal(BaseButton.SignalName.Pressed);
            Check(picker.PlayerColour == colourBefore && buttons[0].Icon == swatchBefore, "(the colour put back)");

            // A choice is refused unless the row shows it.
            Check(picker.Choose("stickbear") && !picker.Choose(CharacterChoice.GubbleId) && !picker.Choose("dragon") && picker.ChosenId == "stickbear",
                "Stickbear is chosen; the Gubble and a character the row does not show are refused");
            picker.QueueFree();
            picker = null;

            // The room puts on the team's costume and the Gubble's name and colour over what the machine's profile gave the HUD.
            _hud.CharacterChoices = () => SuppliedCharacters(null);
            told.Clear();
            var team = TeamFile.Create("hud_team", new DateTime(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc), "Mira", "stickbear", "Pip", "#a18cc3");
            _hud.UseTeam(team);
            Check(player.CharacterId == "stickbear" && told.SequenceEqual(new[] { "stickbear" }) && companion.CompanionName == "Pip" &&
                  companion.AuraColor is { } teamAura && teamAura.IsEqualApprox(new Color("a18cc3")) && companion.AppearanceColor.IsEqualApprox(new Color("a18cc3")),
                $"the team file's costume is worn (Stickbear, told once) and the Gubble takes its name and colour ({companion.CompanionName}, {companion.AuraColor?.ToHtml(false)})");
            _hud.UseTeam(team with { Costume = TeamFile.PrototypeCostume });
            Check(player.CharacterId == "", "the prototype costume is the plain figure");
            _hud.UseTeam(team with { Costume = "dragon" });
            Check(player.CharacterId == "", "a costume the library does not offer is the plain figure");
            _hud.UseTeam(team with { Costume = CharacterChoice.GubbleId });
            Check(player.CharacterId == "", "the family's Gubble is never a player's costume");
            _hud.UseTeam(team with { GubbleColour = "#123456" });
            Check(companion.AppearanceColor.IsEqualApprox(new Color("a18cc3")), "a colour that is not one of the five leaves the Gubble's as it was");
            companion.SetDisplayName(CompanionAvatar.DefaultName);

            // A new session (a fixture with no team file) restores an older profile's character through SetCharacterId.
            SetProfileCharacter("cloudpuff");
            var fresh = await FreshCharacterHud(() => SuppliedCharacters(null));
            Check(fresh.Id == "cloudpuff" && fresh.Told.SequenceEqual(new[] { "cloudpuff" }), $"a new session restores Cloudpuff from an older profile: the body wears her, told once ({fresh.Id})");

            // A bad saved value is the plain figure.
            foreach (var (value, why) in new (Variant? Value, string Why)[]
                     { ("Cloud Puff!", "not a token"), ("dragon", "a character the library does not have"), (CharacterChoice.GubbleId, "the Gubble"), (7, "not a string"), (null, "missing") })
            {
                SetProfileCharacter(value);
                fresh = await FreshCharacterHud(() => SuppliedCharacters(null));
                Check(fresh.Id == "" && fresh.Told.Length == 0, $"a saved character that is {why} loads as the plain figure ({fresh.Id})");
            }

            // Nothing supplied, or a list that is null: the plain figure alone, and nothing breaks.
            SetProfileCharacter("stickbear");
            var none = await FreshCharacterHud(null);
            var nothing = await FreshCharacterHud(() => null);
            Check(none.Id == "" && nothing.Id == "", "with no characters supplied (or a null list) a saved Stickbear is the plain figure, and nothing breaks");
        }
        finally
        {
            picker?.QueueFree();
            pickerLayer.QueueFree();
            _hud.CharacterChoices = roomChoices;
            player.SetCharacterId("");
            player.CharacterChanged = previousTold;
            if (profileBefore != null) System.IO.File.WriteAllBytes(profileFile, profileBefore);
        }
    }

    /// <summary>A key pressed and let go through the viewport, as the GUI receives a real one (the panel's focus moves and presses with it).</summary>
    private async Task TapKey(Key key)
    {
        GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true }, true);
        GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false }, true);
        await Frames(1);
    }

    /// <summary>Every key of the suite's profile and its value, as text ("section/key").</summary>
    private static Dictionary<string, string> ProfileSnapshot()
    {
        var config = new ConfigFile();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (config.Load(HudProfile) != Error.Ok) return values;
        foreach (var section in config.GetSections())
            foreach (var key in config.GetSectionKeys(section))
                values[section + "/" + key] = GD.VarToStr(config.GetValue(section, key));
        return values;
    }

    /// <summary>The profile's saved character set to a value, or taken out (null); every other key left as it is.</summary>
    private static void SetProfileCharacter(Variant? value)
    {
        var config = new ConfigFile();
        config.Load(HudProfile);
        if (value is { } saved) config.SetValue("profile", RoomHud.CharacterProfileKey, saved);
        else if (config.HasSectionKey("profile", RoomHud.CharacterProfileKey)) config.EraseSectionKey("profile", RoomHud.CharacterProfileKey);
        config.Save(HudProfile);
    }

    /// <summary>
    /// A new session's HUD on the suite's profile, with these characters supplied as the composition root would (before the HUD
    /// enters the tree): what the body wears, and what CharacterChanged was told.
    /// </summary>
    private async Task<(string Id, string[] Told)> FreshCharacterHud(Func<IReadOnlyList<CharacterChoice>?>? choices)
    {
        var world = new Node3D();
        AddChild(world);
        var player = new SmallPlayerController { ReadKeyboard = false };
        var told = new List<string>();
        player.CharacterChanged = told.Add;
        world.AddChild(player);
        var companion = new CompanionAvatar();
        world.AddChild(companion);
        var hud = new RoomHud { Player = player, Companion = companion, RoomTitle = "CHARACTER", CharacterChoices = choices };
        world.AddChild(hud);
        await Frames(2);
        var result = (player.CharacterId, told.ToArray());
        world.QueueFree();
        await Frames(1);
        return result;
    }
}
