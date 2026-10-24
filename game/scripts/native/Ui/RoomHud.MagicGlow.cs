using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GarageGame.Native;

public partial class RoomHud
{
    private OptionButton _glowKind = null!;
    private readonly List<string> _glowKindIds = new();
    private OptionButton _glowTouch = null!;
    private HSlider _chimeVolume = null!;
    private CheckButton _chimeMute = null!;
    private bool _audioDirty;
    private Label _glowAllowance = null!;
    private VBoxContainer _constellationEdit = null!;
    private OptionButton _activeGlowChoice = null!;
    private OptionButton _linkGlowChoice = null!;
    private Button _moveGlowButton = null!;
    private Button _linkGlowButton = null!;
    private readonly List<string> _activeGlowIds = new();
    private readonly List<string> _linkGlowIds = new();
    private static readonly string[] TouchIds = { "plain", "air", "ember", "frost", "chime" };

    private void BuildGlowPage(VBoxContainer page)
    {
        MagicHeading(page, "MAKE A LIGHT");
        _glowAllowance = WrapMagicLabel(""); page.AddChild(_glowAllowance);
        _glowKind = new OptionButton { Name = "GlowKindChoice" }; page.AddChild(_glowKind);
        AddButton(page, "Cast Glow", () => CastPanelGlow(_glowKind.Selected >= 0 && _glowKind.Selected < _glowKindIds.Count ? _glowKindIds[_glowKind.Selected] : "auto"), 32);
        var experiments = new HBoxContainer(); page.AddChild(experiments);
        AddButton(experiments, "Try a light above", () => CastPanelGlow("skylight"), 32);
        page.AddChild(WrapMagicLabel("Different places can change what a light becomes. Your journal remembers what you discover."));
        MagicHeading(page, "A PERSONAL TOUCH");
        _glowTouch = new OptionButton { Name = "GlowTouchChoice" };
        foreach (var title in new[] { "Plain", "Air", "Ember", "Frost", "Chime" }) _glowTouch.AddItem(title);
        _glowTouch.ItemSelected += index => { if (Host is { } host) MagicResult(host.PlayerGlowTouch(TouchIds[(int)index]), "Your next light will use this touch."); };
        page.AddChild(_glowTouch);
        page.AddChild(WrapMagicLabel("Touches change a light's appearance or sound. Ember is a warm-looking light; Fire is the separate island ability."));
        MagicHeading(page, "CHIME SOUND");
        _chimeVolume = new HSlider { Name = "ChimeVolume", MinValue = 0, MaxValue = 1, Step = .05, Value = .35 };
        _chimeVolume.ValueChanged += _ => _audioDirty = true;
        page.AddChild(_chimeVolume);
        _chimeMute = new CheckButton { Name = "ChimeMute", Text = "Mute chimes" };
        _chimeMute.Toggled += _ => _audioDirty = true;
        page.AddChild(_chimeMute);
        AddButton(page, "Save sound settings", () =>
        {
            if (Host is not { } host) return;
            var result = host.PlayerGlowAudio((float)_chimeVolume.Value, _chimeMute.ButtonPressed);
            if (result["ok"]?.GetValue<bool>() == true) _audioDirty = false;
            MagicResult(result, "Your chime sound settings are saved.");
        }, 32);
        _constellationEdit = new VBoxContainer { Name = "ConstellationControls", Visible = false };
        page.AddChild(_constellationEdit);
        MagicHeading(_constellationEdit, "ARRANGE YOUR CONSTELLATION");
        _activeGlowChoice = new OptionButton { Name = "ConstellationLight" };
        _activeGlowChoice.ItemSelected += _ => RefreshGlowLinks();
        _constellationEdit.AddChild(_activeGlowChoice);
        _moveGlowButton = new Button { Text = "Move this light", Name = "MoveGlowButton" };
        _moveGlowButton.Pressed += () => { if (SelectedGlowId() is { } id) PickMagicSpot(id); };
        _constellationEdit.AddChild(_moveGlowButton);
        _linkGlowChoice = new OptionButton { Name = "ConstellationLink" };
        _linkGlowChoice.ItemSelected += _ => RefreshGlowLinkButton();
        _constellationEdit.AddChild(_linkGlowChoice);
        _linkGlowButton = new Button { Name = "GlowLinkButton", Text = "Link these lights" };
        _linkGlowButton.Pressed += ToggleGlowLink;
        _constellationEdit.AddChild(_linkGlowButton);
        AddButton(_constellationEdit, "Remove this light's links", () =>
        {
            if (Host is { } host && SelectedGlowId() is { } id) MagicResult(host.PlayerAdjustGlow(id, connections: Array.Empty<string>()), "This light's links are cleared.");
        }, 32);
        _constellationEdit.AddChild(WrapMagicLabel("Choose a light to move it, or join it to another nearby light. The Gubble must be able to reach and see the new place."));
    }

    private void CastPanelGlow(string variant)
    {
        if (Host is not { } host) return;
        Vector3? point = _magicAim.Hit && !_magicAim.AtGubble ? _magicAim.Point : null;
        if (variant == "lantern") point = null;
        if (variant == "skylight") point ??= Companion.GlobalPosition;
        MagicResult(host.PlayerEffect("glow", point, variant), "The light is glowing.");
    }

    private void RefreshGlowPage()
    {
        if (Host is not { } host) return;
        _glowAllowance.Text = $"{host.ActiveGlowPoints.Count} lights glowing · room for {host.EffectiveEffectLimit("glow")} together";
        var variants = new List<string> { "auto" };
        foreach (var id in new[] { "lantern", "torch", "bioluminesce", "skylight" }) if (host.GlowProgress.HasDiscovery(id)) variants.Add(id);
        if (!_glowKindIds.SequenceEqual(variants))
        {
            var selected = _glowKind.Selected >= 0 && _glowKind.Selected < _glowKindIds.Count ? _glowKindIds[_glowKind.Selected] : "auto";
            _glowKindIds.Clear(); _glowKindIds.AddRange(variants); _glowKind.Clear();
            foreach (var id in variants) _glowKind.AddItem(GlowKindWords(id));
            _glowKind.Select(Math.Max(0, variants.IndexOf(selected)));
        }
        _glowTouch.Select(Math.Max(0, Array.IndexOf(TouchIds, host.GlowProgress.SelectedTouch)));
        if (!_audioDirty)
        {
            _chimeVolume.SetValueNoSignal(host.GlowProgress.SoundVolume);
            _chimeMute.SetPressedNoSignal(host.GlowProgress.SoundMuted);
        }
        _constellationEdit.Visible = host.GlowProgress.ActiveLightAllowance >= 5 && host.GlowProgress.HasDiscovery("constellation");
        if (!_constellationEdit.Visible) return;
        var active = host.ActiveGlowPoints.Where(light => !light.Self && light.Variant is "plain" or "torch").ToArray();
        var ids = active.Select(light => light.Id).ToArray();
        if (!_activeGlowIds.SequenceEqual(ids) || _activeGlowChoice.ItemCount == 0)
        {
            var selected = SelectedGlowId();
            _activeGlowChoice.Clear(); _activeGlowIds.Clear(); _activeGlowIds.AddRange(ids);
            for (var i = 0; i < active.Length; i++) _activeGlowChoice.AddItem($"Light {i + 1} · {GlowKindWords(active[i].Variant)}");
            if (active.Length == 0) _activeGlowChoice.AddItem("No movable lights yet");
            _activeGlowChoice.Select(Math.Max(0, selected == null ? 0 : _activeGlowIds.IndexOf(selected)));
        }
        _activeGlowChoice.Disabled = _moveGlowButton.Disabled = ids.Length == 0;
        RefreshGlowLinks();
    }

    private string? SelectedGlowId() => _activeGlowChoice.Selected >= 0 && _activeGlowChoice.Selected < _activeGlowIds.Count ? _activeGlowIds[_activeGlowChoice.Selected] : null;

    private void RefreshGlowLinks()
    {
        var selected = SelectedGlowId();
        var others = _activeGlowIds.Where(id => id != selected).ToArray();
        if (!_linkGlowIds.SequenceEqual(others) || _linkGlowChoice.ItemCount == 0)
        {
            var previous = _linkGlowChoice.Selected >= 0 && _linkGlowChoice.Selected < _linkGlowIds.Count ? _linkGlowIds[_linkGlowChoice.Selected] : null;
            _linkGlowChoice.Clear(); _linkGlowIds.Clear(); _linkGlowIds.AddRange(others);
            foreach (var id in others) _linkGlowChoice.AddItem($"Light {_activeGlowIds.IndexOf(id) + 1}");
            if (others.Length == 0) _linkGlowChoice.AddItem("Make another nearby light");
            _linkGlowChoice.Select(Math.Max(0, previous == null ? 0 : _linkGlowIds.IndexOf(previous)));
        }
        _linkGlowChoice.Disabled = others.Length == 0;
        RefreshGlowLinkButton();
    }

    private void RefreshGlowLinkButton()
    {
        var selected = SelectedGlowId();
        var other = _linkGlowChoice.Selected >= 0 && _linkGlowChoice.Selected < _linkGlowIds.Count ? _linkGlowIds[_linkGlowChoice.Selected] : null;
        _linkGlowButton.Disabled = selected == null || other == null;
        _linkGlowButton.Text = selected != null && other != null && Host?.GlowConnections(selected).Contains(other) == true ? "Remove this link" : "Link these lights";
    }

    private void ToggleGlowLink()
    {
        if (Host is not { } host || SelectedGlowId() is not { } selected || _linkGlowChoice.Selected < 0 || _linkGlowChoice.Selected >= _linkGlowIds.Count) return;
        var other = _linkGlowIds[_linkGlowChoice.Selected];
        var links = host.GlowConnections(selected).ToList();
        if (!links.Remove(other)) links.Add(other);
        MagicResult(host.PlayerAdjustGlow(selected, connections: links), "The light's connections are updated.");
    }

    private static string GlowKindWords(string id) => id switch
    {
        "auto" => "Let this place shape the light", "lantern" => "Lantern", "torch" => "Torch",
        "bioluminesce" => "Bioluminesce", "skylight" => "Skylight", "glowing_bubbles" => "Glowing bubbles", _ => "Glow",
    };
}
