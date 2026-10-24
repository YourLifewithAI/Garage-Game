using Godot;
using System;
using System.Linq;
using GarageGame.Native.Kernel;
using GarageGame.Native.Saves;

namespace GarageGame.Native.Ui;

/// <summary>
/// "New team" (docs/game/MECHANICS.md section 3, the Customization panel lifted out of the in-room HUD): the person's name and avatar
/// (AvatarPicker), the Gubble's name and colour, and a knack or "surprise me". Making the team writes its team file (TeamMaker) and
/// chooses it; nothing else is written. Making an avatar from a photo of a drawing is the converter's job and joins the menu later.
/// </summary>
public partial class MainMenu
{
    private Control _overlay = null!;
    private LineEdit _nameEdit = null!, _gubbleNameEdit = null!;
    private AvatarPicker _picker = null!;
    private Button _gubbleColourButton = null!, _create = null!, _cancel = null!;
    private ColorRect _gubbleSwatch = null!;
    private OptionButton _knackChoice = null!;
    private Label _knackDescription = null!, _formNotice = null!;
    private int _gubbleColour = MachineSettings.DefaultCompanionColour;
    private bool _newTeamOpen;
    private Label _formTitle = null!;
    /// <summary>The team being edited in the panel (null: a new team is being made).</summary>
    private string? _editing;

    public bool NewTeamOpen => _newTeamOpen;
    /// <summary>The id of the team the panel is editing; null while it makes a new one.</summary>
    public string? EditingTeamId => _editing;
    public Label FormTitle => _formTitle;
    public PanelContainer NewTeamPanel { get; private set; } = null!;
    public LineEdit NameEdit => _nameEdit;
    public LineEdit GubbleNameEdit => _gubbleNameEdit;
    public AvatarPicker Picker => _picker;
    public Button GubbleColourButton => _gubbleColourButton;
    public OptionButton KnackChoice => _knackChoice;
    public Label KnackDescription => _knackDescription;
    public Button CreateButton => _create;
    public Button CancelButton => _cancel;
    public Label FormNotice => _formNotice;
    /// <summary>The Gubble's colour in the form, an index into the five-colour palette.</summary>
    public int GubbleColourIndex => _gubbleColour;

    private void BuildNewTeam()
    {
        _overlay = new Control { Name = "NewTeamOverlay", Visible = false };
        AddChild(_overlay);
        _overlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var dim = new ColorRect { Name = "Dim", Color = new Color(0, 0, 0, 0.62f), MouseFilter = MouseFilterEnum.Stop };
        _overlay.AddChild(dim);
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        _overlay.AddChild(centre);
        centre.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var panel = new PanelContainer { Name = "NewTeamPanel" };
        NewTeamPanel = panel;
        panel.AddThemeStyleboxOverride("panel", MenuStyle.Box(MenuStyle.Panel, MenuStyle.Muted, 1, 14, 18));
        centre.AddChild(panel);
        var form = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0) };
        form.AddThemeConstantOverride("separation", 6);
        panel.AddChild(form);

        _formTitle = MenuStyle.Text("NEW TEAM", 20, MenuStyle.Warm, "FormTitle");
        form.AddChild(_formTitle);
        _nameEdit = new LineEdit { Name = "TeamName", MaxLength = TeamFile.MaxNameLength, PlaceholderText = "Your name", CustomMinimumSize = new Vector2(0, 40) };
        _nameEdit.TextSubmitted += _ => Submit();
        form.AddChild(_nameEdit);
        _picker = new AvatarPicker { Choices = () => _characters };
        form.AddChild(_picker);
        _gubbleNameEdit = new LineEdit { Name = "GubbleName", Text = CompanionAvatar.DefaultName, MaxLength = TeamFile.MaxNameLength, PlaceholderText = "Your companion's name (the Gubble)", CustomMinimumSize = new Vector2(0, 40) };
        form.AddChild(_gubbleNameEdit);
        var colour = new HBoxContainer();
        colour.AddThemeConstantOverride("separation", 10);
        form.AddChild(colour);
        _gubbleColourButton = new Button { Name = "GubbleColour", Text = "Change the Gubble's color", FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 38) };
        _gubbleColourButton.Pressed += CycleGubbleColour;
        colour.AddChild(_gubbleColourButton);
        _gubbleSwatch = new ColorRect { Name = "GubbleSwatch", CustomMinimumSize = new Vector2(38, 38), Color = MenuProfile.Palette[_gubbleColour] };
        colour.AddChild(_gubbleSwatch);
        form.AddChild(Wrapped("A knack is one small gift that never changes your size or reach. Swap it any day in this menu.", 14, MenuStyle.Muted, 520));
        _knackChoice = new OptionButton { Name = "KnackChoice", FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 38), FitToLongestItem = false, ClipText = true };
        form.AddChild(_knackChoice);
        _knackDescription = Wrapped("", 14, MenuStyle.Muted, 520, "KnackDescription");
        form.AddChild(_knackDescription);
        _knackChoice.ItemSelected += _ => DescribeKnackChoice();
        _formNotice = Wrapped("", 15, MenuStyle.Refused, 520, "FormNotice");
        form.AddChild(_formNotice);
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 10);
        form.AddChild(buttons);
        _cancel = new Button { Name = "CancelNewTeam", Text = "Cancel", FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(120, 44) };
        _cancel.Pressed += CloseNewTeam;
        buttons.AddChild(_cancel);
        _create = new Button { Name = "CreateTeam", Text = "Make this team", FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(200, 44) };
        MenuStyle.Big(_create);
        _create.AddThemeFontSizeOverride("font_size", 18);
        _create.Pressed += Submit;
        buttons.AddChild(_create);
    }

    /// <summary>The panel opens, empty: a name to type, the plain figure, the Gubble as it is called, "surprise me".</summary>
    public void OpenNewTeam()
    {
        _editing = null;
        _formTitle.Text = "NEW TEAM";
        _create.Text = "Make this team";
        _nameEdit.Text = "";
        _gubbleNameEdit.Text = CompanionAvatar.DefaultName;
        _gubbleColour = MachineSettings.DefaultCompanionColour;
        _gubbleSwatch.Color = MenuProfile.Palette[_gubbleColour];
        ShowForm();
        _picker.Choose(CharacterChoice.PrototypeId);
        FillKnackChoice(editing: false);
    }

    /// <summary>
    /// "Redraw yourself any day; keep everything": the same panel, filled with the chosen team's name, costume and Gubble, to change them.
    /// The team's id, Trust and every earned rank stay as they are; a new knack here is a free swap.
    /// </summary>
    public void OpenEditTeam()
    {
        if (Model.Team?.File is not { } team) return;
        _editing = team.TeamId;
        _formTitle.Text = "EDIT YOUR TEAM";
        _create.Text = "Save changes";
        _nameEdit.Text = team.DisplayName;
        _gubbleNameEdit.Text = team.GubbleName;
        var colour = Array.FindIndex(MachineSettings.AppearancePalette, hex => string.Equals(hex, team.GubbleColour, StringComparison.OrdinalIgnoreCase));
        _gubbleColour = colour >= 0 ? colour : MachineSettings.DefaultCompanionColour;
        _gubbleSwatch.Color = new Color(team.GubbleColour);
        ShowForm();
        _picker.Choose(team.Costume == TeamFile.PrototypeCostume ? CharacterChoice.PrototypeId : team.Costume);
        FillKnackChoice(editing: true);
    }

    private void ShowForm()
    {
        _newTeamOpen = true;
        _overlay.Visible = true;
        _formNotice.Text = "";
        _picker.Refresh();
        _nameEdit.GrabFocus();
    }

    public void CloseNewTeam()
    {
        _newTeamOpen = false;
        _overlay.Visible = false;
        _editing = null;
        if (_newTeam != null && IsInstanceValid(_newTeam)) _newTeam.GrabFocus();
    }

    private void FillKnackChoice(bool editing)
    {
        _knackChoice.Clear();
        _knackChoice.AddItem(editing ? "Keep my knack" : "Surprise me");
        foreach (var knack in TeamMaker.Offered(Rules)) _knackChoice.AddItem(KnackLine(knack));
        _knackChoice.Select(0);
        DescribeKnackChoice();
    }

    private void DescribeKnackChoice() =>
        _knackDescription.Text = _knackChoice.Selected > 0 ? "" : _editing != null ? "Your knack stays as it is." : "A knack picked for you by chance.";

    private void CycleGubbleColour()
    {
        _gubbleColour = (_gubbleColour + 1) % MenuProfile.Palette.Length;
        _gubbleSwatch.Color = MenuProfile.Palette[_gubbleColour];
    }

    /// <summary>The panel's button: "Make this team" or, editing a team, "Save changes".</summary>
    public void Submit()
    {
        if (_editing != null) Save();
        else Create();
    }

    /// <summary>"Save changes": the team file rewritten with the new name, costume and Gubble (and knack when one is chosen); the team stays chosen.</summary>
    public void Save()
    {
        if (_editing is not { } id) return;
        var offered = TeamMaker.Offered(Rules);
        string? knack = _knackChoice.Selected <= 0 || _knackChoice.Selected > offered.Count ? null : offered[_knackChoice.Selected - 1].Knack;
        try
        {
            TeamMaker.Update(Model.Layout, Rules, id, _nameEdit.Text, _picker.ChosenId.Length == 0 ? TeamFile.PrototypeCostume : _picker.ChosenId, _gubbleNameEdit.Text,
                MachineSettings.AppearancePalette[_gubbleColour], knack);
            Model.Reload();
            Model.SelectTeam(id);
            CloseNewTeam();
            Refresh();
        }
        catch (TeamFileException error) { _formNotice.Text = error.Message; }
    }

    /// <summary>"Make this team": the team file written, the team chosen. A refusal (no name, a file that would not save) is said in the panel and nothing is made.</summary>
    public void Create()
    {
        var offered = TeamMaker.Offered(Rules);
        string? knack = _knackChoice.Selected <= 0 || _knackChoice.Selected > offered.Count ? null : offered[_knackChoice.Selected - 1].Knack;
        try
        {
            var team = TeamMaker.Create(Model.Layout, Rules, _nameEdit.Text, _picker.ChosenId.Length == 0 ? TeamFile.PrototypeCostume : _picker.ChosenId, _gubbleNameEdit.Text,
                MachineSettings.AppearancePalette[_gubbleColour], knack, Model.Clock());
            Model.Reload();
            Model.SelectTeam(team.TeamId);
            CreatedTeamId = team.TeamId;
            CloseNewTeam();
            Refresh();
        }
        catch (TeamFileException error) { _formNotice.Text = error.Message; }
    }

    /// <summary>The team the New team panel made last (empty before any).</summary>
    public string CreatedTeamId { get; private set; } = "";
}
