using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Saves;

namespace GarageGame.Native.Ui;

/// <summary>
/// The machine's appearance profile read and written from the menu: the plain figure's colour (<c>player_color</c>, an index into the
/// five-colour palette) lives there as it did before the Customize panel moved out of the HUD, because the team file holds the costume
/// and not a colour. Every other key of the profile is kept exactly as it was, and a profile that does not read is never overwritten.
/// </summary>
public static class MenuProfile
{
    public const string PlayerColourKey = "player_color";
    public static readonly Color[] Palette = MachineSettings.AppearancePalette.Select(hex => new Color(hex)).ToArray();

    public static int PlayerColour()
    {
        var config = new ConfigFile();
        if (config.Load(RoomHud.ProfilePath) != Error.Ok) return 0;
        var value = config.GetValue(MachineSettings.Section, PlayerColourKey, 0);
        return value.VariantType == Variant.Type.Int ? Mathf.PosMod(value.AsInt32(), Palette.Length) : 0;
    }

    /// <summary>One value into the profile as it is on disk; false when the profile does not load (then it lasts this session only).</summary>
    public static bool Set(string key, Variant value)
    {
        var config = new ConfigFile();
        var loaded = config.Load(RoomHud.ProfilePath);
        if (loaded != Error.Ok && loaded != Error.FileNotFound) return false;
        if (loaded == Error.Ok && (config.GetValue(MachineSettings.Section, "version", 0).VariantType != Variant.Type.Int || config.GetValue(MachineSettings.Section, "version", 0).AsInt32() != 1)) return false;
        config.SetValue(MachineSettings.Section, "version", 1);
        config.SetValue(MachineSettings.Section, key, value);
        var path = RoomHud.ProfilePath;
        var temporary = path + ".tmp";
        var prepared = DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(path.GetBaseDir())) == Error.Ok && config.Save(temporary) == Error.Ok;
        return prepared && DirAccess.RenameAbsolute(ProjectSettings.GlobalizePath(temporary), ProjectSettings.GlobalizePath(path)) == Error.Ok;
    }
}

/// <summary>
/// The player's character: a row of the plain figure and the drawn characters the library supplies, each a button with a thumbnail and
/// a name, and the plain figure's colour button. This is the Customize panel's "Your two avatars" half, lifted out of the in-room HUD
/// into the opening menu's "New team" panel (the characters round, K6; docs/game/MECHANICS.md section 3); the Gubble's half
/// (its name and colour) is the panel's own lines beside it. Choosing one reports its id ("" for the plain figure); nothing is
/// written here but the plain figure's colour. The colour button colours the plain figure only: it hides while a drawn character is
/// chosen, which keeps the colours the family drew.
/// </summary>
public partial class AvatarPicker : VBoxContainer
{
    /// <summary>What shows in place of the colour button while a drawn character is chosen.</summary>
    public const string DrawnColorsNote = "Drawn characters keep the colors they were drawn with.";
    private const int ButtonWidth = 100;
    private const int ButtonHeight = 100;
    private const int Columns = 5;

    /// <summary>The characters the library supplies (ids, names, thumbnails); read when the picker is built and again with Refresh.</summary>
    public Func<IReadOnlyList<CharacterChoice>?>? Choices { get; set; }
    /// <summary>The id chosen now: "" for the plain figure.</summary>
    public string ChosenId { get; private set; } = CharacterChoice.PrototypeId;
    /// <summary>The plain figure's colour, an index into the palette (the profile's).</summary>
    public int PlayerColour { get; private set; }
    /// <summary>Told when the player chooses a different character (never for the same one, never for a refused id).</summary>
    public event Action<string>? ChoiceChanged;

    private IReadOnlyList<CharacterChoice> _row = Array.Empty<CharacterChoice>();
    private readonly List<Button> _buttons = new();
    private readonly ButtonGroup _group = new();
    private HFlowContainer _flow = null!;
    private Label _chosenLabel = null!;
    private Button _colourButton = null!;
    private Label _colourNote = null!;
    private readonly Dictionary<int, ImageTexture> _prototypeSwatches = new();
    private readonly Dictionary<string, ImageTexture> _tileSwatches = new(StringComparer.Ordinal);

    public IReadOnlyList<CharacterChoice> Row => _row;
    public IReadOnlyList<Button> Buttons => _buttons;
    public Button ColourButton => _colourButton;
    public Label ColourNote => _colourNote;
    public Label ChosenLabel => _chosenLabel;
    /// <summary>The plain figure's colour as the menu draws it.</summary>
    public Color PlayerColor => MenuProfile.Palette[PlayerColour];

    public override void _Ready()
    {
        Name = "AvatarPicker";
        PlayerColour = MenuProfile.PlayerColour();
        _chosenLabel = new Label { Name = "CharacterChosen" };
        AddChild(_chosenLabel);
        _flow = new HFlowContainer { Name = "CharacterRow", CustomMinimumSize = new Vector2(Columns * ButtonWidth + (Columns - 1) * 6, 0) };
        _flow.AddThemeConstantOverride("h_separation", 6);
        _flow.AddThemeConstantOverride("v_separation", 6);
        AddChild(_flow);
        _colourButton = new Button { Name = "PlayerColor", Text = "Change player color", CustomMinimumSize = new Vector2(0, 38), FocusMode = FocusModeEnum.All };
        _colourButton.Pressed += CycleColour;
        AddChild(_colourButton);
        _colourNote = new Label { Name = "PlayerColorNote", Text = DrawnColorsNote, Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        AddChild(_colourNote);
        Refresh();
    }

    /// <summary>The row made again from what the library supplies now (a character converted since shows); the choice stays if it is still there, else the plain figure.</summary>
    public void Refresh()
    {
        var fresh = CharacterChoice.RowOf(Choices?.Invoke());
        if (_buttons.Count == 0 || !fresh.SequenceEqual(_row))
        {
            _row = fresh;
            if (_row.All(c => c.Id != ChosenId)) ChosenId = CharacterChoice.PrototypeId;
            Fill();
        }
        Sync();
    }

    private void Fill()
    {
        foreach (var button in _buttons)
        {
            button.ButtonGroup = null;
            _flow.RemoveChild(button);
            button.QueueFree();
        }
        _buttons.Clear();
        var chosen = new StyleBoxFlat
        {
            BgColor = new Color(0.22f, 0.36f, 0.33f), BorderColor = new Color("f0e6cf"),
            BorderWidthLeft = 3, BorderWidthTop = 3, BorderWidthRight = 3, BorderWidthBottom = 3,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 4, ContentMarginBottom = 4,
        };
        foreach (var choice in _row)
        {
            var id = choice.Id;
            var button = new Button
            {
                Name = id.Length > 0 ? "Character_" + id : "PlainFigure", Text = choice.DisplayName, TooltipText = choice.DisplayName,
                ToggleMode = true, ButtonGroup = _group, FocusMode = FocusModeEnum.All,
                CustomMinimumSize = new Vector2(ButtonWidth, ButtonHeight),
                Icon = choice.Thumbnail ?? SwatchFor(id), ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center, VerticalIconAlignment = VerticalAlignment.Top,
                ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            };
            button.AddThemeFontSizeOverride("font_size", 15);
            button.AddThemeStyleboxOverride("pressed", chosen);
            button.AddThemeStyleboxOverride("hover_pressed", chosen);
            button.Pressed += () => Choose(id);
            _flow.AddChild(button);
            _buttons.Add(button);
        }
        // The arrows move along the row and wrap at its ends; left to the engine, the colour button under the row's full width is the
        // nearer control to the right of a character.
        for (var i = 0; i < _buttons.Count; i++)
        {
            _buttons[i].FocusNeighborLeft = _buttons[i].GetPathTo(_buttons[(i + _buttons.Count - 1) % _buttons.Count]);
            _buttons[i].FocusNeighborRight = _buttons[i].GetPathTo(_buttons[(i + 1) % _buttons.Count]);
        }
    }

    /// <summary>Choose a character by its id ("" for the plain figure). An id the row does not show is refused; the same one changes nothing.</summary>
    public bool Choose(string id)
    {
        var shown = id != null && _row.Any(c => c.Id == id);
        var changed = shown && id != ChosenId;
        if (shown) ChosenId = id!;
        Sync();
        if (changed) ChoiceChanged?.Invoke(ChosenId);
        return shown;
    }

    /// <summary>The chosen button takes the keyboard focus (the picker has just opened).</summary>
    public void FocusChosen()
    {
        var index = _row.ToList().FindIndex(c => c.Id == ChosenId);
        if (index >= 0 && index < _buttons.Count) _buttons[index].GrabFocus();
    }

    private void Sync()
    {
        for (var i = 0; i < _buttons.Count && i < _row.Count; i++) _buttons[i].SetPressedNoSignal(_row[i].Id == ChosenId);
        _chosenLabel.Text = "Your character: " + (_row.FirstOrDefault(c => c.Id == ChosenId)?.DisplayName ?? CharacterChoice.PrototypeName);
        var drawn = ChosenId.Length > 0;
        _colourButton.Visible = !drawn;
        _colourNote.Visible = drawn;
    }

    /// <summary>"Change player color": the plain figure's next colour of the five, kept in the machine's profile. A drawn character keeps its own colours.</summary>
    private void CycleColour()
    {
        if (ChosenId.Length > 0) return;
        PlayerColour = (PlayerColour + 1) % MenuProfile.Palette.Length;
        MenuProfile.Set(MenuProfile.PlayerColourKey, PlayerColour);
        if (_buttons.Count > 0) _buttons[0].Icon = SwatchFor(CharacterChoice.PrototypeId);
    }

    /// <summary>The swatch of a choice without a thumbnail: the plain figure in its colour now, or a drawn character's tile.</summary>
    private ImageTexture SwatchFor(string id)
    {
        if (id.Length == 0)
        {
            if (!_prototypeSwatches.TryGetValue(PlayerColour, out var figure))
                _prototypeSwatches[PlayerColour] = figure = CharacterChoice.Swatch(id, MenuProfile.Palette[PlayerColour]);
            return figure;
        }
        if (!_tileSwatches.TryGetValue(id, out var tile)) _tileSwatches[id] = tile = CharacterChoice.Swatch(id, default);
        return tile;
    }
}
