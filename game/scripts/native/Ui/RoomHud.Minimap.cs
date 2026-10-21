using Godot;
using GarageGame.Native.Ui;

namespace GarageGame.Native;

/// <summary>
/// The minimap in the top-right corner, from the first minute: the journal's map, drawn from what either avatar has seen
/// (Ui/Minimap.cs). The corner is the one the focus tag already keeps clear (Sandbox.SandboxControls.FocusWords). The map reads
/// the host's discovered map and nothing else; it changes nothing.
/// </summary>
public partial class RoomHud
{
    private Minimap _minimap = null!;
    /// <summary>The map in the corner.</summary>
    public Minimap Map => _minimap;
    /// <summary>The corner's gap to the screen's edge, as the status panel's at the top left.</summary>
    public const float MinimapMarginPx = 18f;

    private void BuildMinimap()
    {
        _minimap = new Minimap { Name = "Minimap" };
        AddChild(_minimap);
        // Under every panel, the wheel and the book (the wheel's title reaches into this corner): just above the pips' layer.
        MoveChild(_minimap, 1);
        _minimap.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
        _minimap.OffsetLeft = -(Minimap.PanelPx + MinimapMarginPx);
        _minimap.OffsetRight = -MinimapMarginPx;
        _minimap.OffsetTop = MinimapMarginPx;
        _minimap.OffsetBottom = MinimapMarginPx + Minimap.PanelPx;
    }

    /// <summary>Each frame: where the avatars are on the map; the host's map is read again on the map's own slow cadence.</summary>
    private void UpdateMinimap(double delta)
    {
        if (Host is not { } host || !IsInstanceValid(host) || IntroPlaying || Player == null || Companion == null) return;
        var heading = Player.GlobalRotation.Y;
        _minimap.Tick(host, Player.GlobalPosition, heading, Companion.GlobalPosition, Companion.AuraColor ?? Companion.AppearanceColor, delta);
    }
}
