using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using GarageGame.Native.Ui;

namespace GarageGame.Native;

public partial class RoomHud
{
    public const double FireConfirmSeconds = 1.25;
    private Label _modeName = null!;
    private Label _modeHelp = null!;
    private Label _weatherHelp = null!;
    private Label _natureMaterials = null!;
    private OptionButton _repairChoice = null!;
    private Label _repairCost = null!;
    private Button _undoNature = null!;
    private CheckButton? _gubbleFire;
    private Label _firePreviewWords = null!;
    private Label _fireLocked = null!;
    private Button _firePreview = null!;
    /// <summary>The Magic panel's words while Fire is locked (decision 72); the host's refusal says the same if it is asked anyway.</summary>
    public const string FireLockedWords = "Locked: the Gubble learns Fire when you first rest by the starting campfire (Z beside it).";
    /// <summary>Whether the Magic panel shows Fire locked now (its preview button off, the locked line shown).</summary>
    public bool FireShownLocked => _fireLocked is { Visible: true } && _firePreview is { Disabled: true };
    private Button _fireConfirm = null!;
    private ProgressBar _fireHoldBar = null!;
    private readonly List<string> _repairIds = new();
    private string? _firePreviewId;
    private Vector3 _firePoint;
    private float _fireRadius;
    private bool _fireHolding;
    private double _fireHoldAge;
    public bool FirePreviewReady => _firePreviewId != null;
    public double FireConfirmationProgress => Math.Clamp(_fireHoldAge / FireConfirmSeconds, 0, 1);

    private void BuildNaturePage(VBoxContainer page)
    {
        MagicHeading(page, "YOUR ISLAND");
        // The mode is not changed in play (the mechanics round, decision 21: switching modes means switching saves, chosen at the
        // menu): the page says which this save is, and that is all.
        _modeName = WrapMagicLabel(""); _modeName.Name = "ModeName"; page.AddChild(_modeName);
        _modeHelp = WrapMagicLabel(""); _modeHelp.Name = "ModeHelp"; page.AddChild(_modeHelp);
        _weatherHelp = WrapMagicLabel(""); _weatherHelp.Name = "WeatherReadout"; page.AddChild(_weatherHelp);
        MagicHeading(page, "GROW");
        AddButton(page, "Grow the plants at the selected spot", CastGrow, 32);
        page.AddChild(WrapMagicLabel("Grass, flowers, ferns, shrubs and trees grow a little each time, up to twice their size, and stay grown. A grown tree can be climbed."));
        MagicHeading(page, "FIRE");
        page.AddChild(WrapMagicLabel("Fire can spread inside the previewed area. Creations become scorched and repairable; other things can burn away."));
        // "Let the Gubble use fire" is one of the player's settings (RoomHud.Settings.cs): the switch moved there from this page.
        page.AddChild(WrapMagicLabel("Whether the Gubble may light fires is one of your settings."));
        // Decision 72: Fire is locked until the first rest by the starting campfire; the section says so and its button waits.
        _fireLocked = WrapMagicLabel(FireLockedWords); _fireLocked.Name = "FireLocked"; page.AddChild(_fireLocked);
        _firePreview = new Button { Name = "PreviewFireButton", Text = "Preview Fire at this spot", CustomMinimumSize = new Vector2(0, 32), FocusMode = Control.FocusModeEnum.All };
        _firePreview.Pressed += PreviewFire;
        page.AddChild(_firePreview);
        _firePreviewWords = WrapMagicLabel("Choose a spot and preview it before lighting a fire.");
        _firePreviewWords.Name = "FirePreviewWords"; page.AddChild(_firePreviewWords);
        _fireConfirm = new Button { Name = "ConfirmFireButton", Text = "Hold to light this fire", Disabled = true };
        _fireConfirm.ButtonDown += BeginFireConfirmation;
        _fireConfirm.ButtonUp += CancelFireHold;
        _fireConfirm.MouseExited += CancelFireHold;
        page.AddChild(_fireConfirm);
        _fireHoldBar = new ProgressBar { Name = "FireHoldProgress", MinValue = 0, MaxValue = 1, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 8) };
        page.AddChild(_fireHoldBar);
        MagicHeading(page, "REPAIR & UNDO");
        _natureMaterials = WrapMagicLabel(""); page.AddChild(_natureMaterials);
        _repairChoice = new OptionButton { Name = "RepairChoice" };
        _repairChoice.ItemSelected += _ => RefreshRepairCost();
        page.AddChild(_repairChoice);
        _repairCost = WrapMagicLabel(""); page.AddChild(_repairCost);
        AddButton(page, "Repair this creation", () =>
        {
            if (Host is not { } host || _repairChoice.Selected < 0 || _repairChoice.Selected >= _repairIds.Count)
            { _magicMessageText = "There are no scorched creations to repair."; return; }
            MagicResult(host.PlayerNatureRepair(_repairIds[_repairChoice.Selected]), "The creation is repaired.");
        }, 32);
        _undoNature = new Button { Name = "NatureUndoButton", Text = "Undo the last nature change" };
        _undoNature.Pressed += () => { if (Host is { } host) { CancelFirePreview(); MagicResult(host.PlayerNatureUndo(), "The last nature change was undone."); } };
        page.AddChild(_undoNature);
    }

    private void CastGrow()
    {
        if (!_magicAim.Hit || _magicAim.AtGubble) { _magicMessageText = "Choose a spot among the plants for Grow."; RefreshMagicPanel(); return; }
        if (Host is { } host) { CancelFirePreview(); MagicResult(host.PlayerNatureCast("grow", _magicAim.Point), "The plants are growing."); }
    }

    private void PreviewFire()
    {
        CancelFirePreview();
        if (!_magicAim.Hit || _magicAim.AtGubble) { _magicMessageText = "Choose a spot on the ground for Fire."; RefreshMagicPanel(); return; }
        if (Host is not { } host) return;
        var result = host.PlayerNaturePreview("fire", _magicAim.Point);
        if (result["ok"]?.GetValue<bool>() != true) { MagicResult(result, ""); return; }
        _firePreviewId = result["data"]?["preview_id"]?.GetValue<string>();
        _firePoint = _magicAim.Point;
        _fireRadius = result["data"]?["radius_m"]?.GetValue<float>() ?? 1.5f;
        var cells = result["data"]?["cell_count"]?.GetValue<int>() ?? 0;
        var affected = result["data"]?["affected_ids"]?.AsArray().Count ?? 0;
        var approval = result["data"]?["requires_approval"]?.GetValue<bool>() == true;
        // The place decides (decision 77): a firepit, Snuff or a Will-o'-wisp says what it will do, as the wheel's preview does.
        var kind = result["data"]?["kind"]?.GetValue<string>() ?? Kernel.FireKindWords.Fire;
        var plain = kind == Kernel.FireKindWords.Fire;
        // The one ask about builds is the host's card after the hold (Creative only); the preview only says it will come.
        _firePreviewWords.Text = !plain ? WheelFireWords(kind, cells, affected, _fireRadius, approval).Split(" Hold ")[0] + " Release the button to cancel before the hold finishes." :
            $"Fire stays within the outlined {_fireRadius:0.##} m radius. {affected} {(affected == 1 ? "thing may" : "things may")} be affected. " +
            (cells == 0 ? "There is no suitable ground here." : "Release the button to cancel before the hold finishes.") +
            (approval ? " Your builds are in reach: you will be asked once whether they may burn." : "");
        _fireConfirm.Disabled = _firePreviewId == null || (plain && cells == 0);
        NatureVisual?.ShowFirePreview(_firePoint, _fireRadius);
        _magicMessageText = "Review the outlined area, then hold the button if you want this fire.";
        RefreshMagicPanel();
    }

    private global::GarageGame.Native.Look.NatureLook? NatureVisual => GetParent()?.GetNodeOrNull<global::GarageGame.Native.Look.NatureLook>(NatureLookPath);
    private static readonly NodePath NatureLookPath = "NatureLook";

    internal void BeginFireConfirmation()
    {
        if (!MagicPanelOpen || _firePreviewId == null || _fireConfirm.Disabled) return;
        _fireHolding = true; _fireHoldAge = 0;
    }

    private void CancelFireHold()
    {
        _fireHolding = false; _fireHoldAge = 0;
        if (_fireHoldBar != null) _fireHoldBar.Value = 0;
    }

    private void CancelFirePreview()
    {
        CancelFireHold(); _firePreviewId = null;
        if (_fireConfirm != null) _fireConfirm.Disabled = true;
        if (_firePreviewWords != null) _firePreviewWords.Text = "Choose a spot and preview it before lighting a fire.";
        NatureVisual?.HideFirePreview();
    }

    internal void TickFireConfirmation(double delta)
    {
        if (!_fireHolding || !MagicPanelOpen || _firePreviewId == null) return;
        if (!_fireConfirm.IsVisibleInTree()) { CancelFireHold(); return; }
        _fireHoldAge += Math.Clamp(delta, 0, .1);
        _fireHoldBar.Value = FireConfirmationProgress;
        if (_fireHoldAge < FireConfirmSeconds) return;
        var token = _firePreviewId; var point = _firePoint; var radius = _fireRadius;
        CancelFirePreview();
        if (Host is { } host) MagicResult(host.PlayerNatureCast("fire", point, radius, 30, token), "The fire is lit. Rain and wind will shape it.");
    }

    private void RefreshNaturePage()
    {
        if (Host is not { } host) return;
        // A held island (decision 54) is paused: the page says so, not what the host happens to play (Creative) and what that allows.
        var held = host.IslandHeld;
        var stamina = MeterWords.Name(host, "stamina");
        if (held)
        {
            _modeName.Text = IslandPaused.Name;
            _modeHelp.Text = IslandPaused.Heading + ". " + IslandPaused.Words(host);
        }
        else
        {
            _modeName.Text = ModeWords(host.Mode) + (host.Mode == "challenge" ? " · " + char.ToUpperInvariant(host.Difficulty[0]) + host.Difficulty[1..] : "");
            _modeHelp.Text = host.Mode == "creative" ? $"Explore freely. Climbing uses no {stamina.ToLowerInvariant()}, and falls and fire do not hurt you." :
                $"Health {host.Vitals.Health:0}/{host.Vitals.MaxHealth:0} · {stamina} {StaminaPercent(host.Vitals.Stamina, host.Vitals.MaxStamina):0}%\nClimbing capacity {host.Vitals.MaxStamina:0.00} m. Climbing uses {stamina.ToLowerInvariant()}. Falls and fire can hurt. Completed climbs can extend your capacity.";
            if (host.VitalsNotice.Length > 0) _modeHelp.Text += "\n" + host.VitalsNotice;
        }
        _weatherHelp.Text = $"{WeatherWords(host.NatureWeather.Pattern)} · Wind {host.NatureWeather.WindMps.Length():0.00} m/s\nThe weather changes naturally across the island.";
        // The wallet's counts, in every mode (a gathered log or stone is in it): repairs spend it outside Creative.
        _natureMaterials.Text = (host.Mode == "creative" && !held ? "Repairs are free in Creative mode.\n" : "") + "In your wallet: " + MaterialWords(host.Nature.Wallet);
        var previous = _repairChoice.Selected >= 0 && _repairChoice.Selected < _repairIds.Count ? _repairIds[_repairChoice.Selected] : null;
        var ids = host.Nature.Objects.Values.Where(item => item.Creation && item.Burn > 0).Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (!_repairIds.SequenceEqual(ids) || _repairChoice.ItemCount == 0)
        {
            _repairIds.Clear(); _repairIds.AddRange(ids); _repairChoice.Clear();
            for (var i = 0; i < ids.Length; i++) _repairChoice.AddItem($"Scorched creation {i + 1}");
            if (ids.Length == 0) _repairChoice.AddItem("No scorched creations");
            _repairChoice.Select(Math.Max(0, previous == null ? 0 : _repairIds.IndexOf(previous)));
        }
        _repairChoice.Disabled = ids.Length == 0;
        // Undo is Creative's alone (the founder, 13 October): in Challenge and Live the control is not there.
        _undoNature.Visible = host.Mode == "creative";
        _undoNature.Disabled = !host.NatureCanUndo;
        _gubbleFire?.SetPressedNoSignal(host.GubbleFireAllowed);
        _fireLocked.Visible = !host.FireLearned;
        _firePreview.Disabled = !host.FireLearned;
        RefreshRepairCost();
    }

    /// <summary>The player's choice "Let the Gubble use fire": the host follows it at once, and the profile keeps it (this session only if it cannot be written).</summary>
    public void SetGubbleFire(bool allowed)
    {
        Host?.SetGubbleFire(allowed);
        if (_gubbleFire != null && _gubbleFire.ButtonPressed != allowed) _gubbleFire.SetPressedNoSignal(allowed);
        var saved = SaveProfileValue(Kernel.CommandHost.FireProfileKey, allowed);
        _magicMessageText = (allowed ? "The Gubble may use fire." : "The Gubble will not light fires.") + (saved ? "" : " (Not saved: it lasts this session.)");
    }

    private void RefreshRepairCost()
    {
        _repairCost.Text = "";
        if (Host is not { } host || _repairChoice.Selected < 0 || _repairChoice.Selected >= _repairIds.Count || !host.Nature.Objects.TryGetValue(_repairIds[_repairChoice.Selected], out var item)) return;
        _repairCost.Text = host.IslandHeld ? "" : host.Mode == "creative" ? "This repair uses no materials in Creative mode." : "Repair quote (one unit per original part): " + MaterialWords(item.RepairCost);
    }

    private static string MaterialWords(IReadOnlyDictionary<string, int> materials) => string.Join(" · ", materials.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item =>
        (item.Key switch { "wood" => "Wood", "stone" => "Stone", "copper" => "Copper", "leaf" => "Leaves", "metal" => "Metal", "glass" => "Glass", "cloth" => "Cloth", _ => "Material" }) + " " + item.Value));
}
