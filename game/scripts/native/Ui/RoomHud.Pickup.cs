using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using GarageGame.Native.Kernel;
using GarageGame.Native.Ui;

namespace GarageGame.Native;

/// <summary>
/// Collecting Fractals by touching them (the founder: he walked over Fractals and could not collect them; nothing sent item.pickup
/// but a test). The player collects a Fractal by walking into it: once its box, grown by FractalTouchM, meets the body (feet to head,
/// its radius), the HUD sends the player's item.pickup through the host, the one command path, in every mode. The host still checks
/// what it always checks (on the island, within reach with a clear way, the vitals ready), counts it and writes the pace line; the
/// look's sparks follow the host's NatureChanged (Look/Fractals/FractalLook.cs). Facing one within reach and pressing the hands key
/// picks it up too (Hands). A refusal stays quiet on screen unless it is a real reason the player can act on (the vitals or their
/// save not ready: not_ready); each refused Fractal is tried again after PickupRetryS, never every frame.
/// </summary>
public partial class RoomHud
{
    /// <summary>The touch: a Fractal's box grown by this meets the body's (provisional, a fifth of the 10 cm body's height).</summary>
    public const float FractalTouchM = 0.02f;
    /// <summary>How often the touch is looked for (seconds).</summary>
    public const double PickupCheckS = 0.05;
    /// <summary>A Fractal the host refused is tried again after this (seconds).</summary>
    public const double PickupRetryS = 1.0;

    private double _pickupAge = double.MaxValue;
    private double _pickupClock;
    private readonly Dictionary<string, double> _pickupRefused = new(StringComparer.Ordinal);
    private string _pickupNotice = "";

    /// <summary>The notice line's words now (tests).</summary>
    public string NoticeText => _noticeText;

    /// <summary>How many Fractals the touch has collected this session (tests).</summary>
    public int FractalsTouched { get; private set; }

    /// <summary>Whether a Fractal's box, grown by FractalTouchM, meets the body standing at feet with this radius and height.</summary>
    public static bool Touches(IslandFractal fractal, Vector3 feet, float radius, float height)
    {
        var body = new Aabb(feet - new Vector3(radius, 0, radius), new Vector3(radius * 2, height, radius * 2));
        return CommandHost.FractalBox(fractal).Grow(FractalTouchM).Intersects(body);
    }

    /// <summary>Each frame, at PickupCheckS: every Fractal the body touches is picked up as the player's own command.</summary>
    private void UpdatePickup(double delta)
    {
        _pickupClock += delta;
        _pickupAge += delta;
        if (_pickupAge < PickupCheckS) return;
        _pickupAge = 0;
        if (Host is not { } host || Player == null || !IsInstanceValid(Player) || !Player.IsInsideTree() || host.AllFractals.Count == 0) return;
        var feet = Player.GlobalPosition;
        var radius = Player.BodyRadiusM;
        var height = Player.BodyHeightM;
        List<IslandFractal>? touched = null;
        foreach (var fractal in host.Fractals)
            if (Touches(fractal, feet, radius, height)) (touched ??= new List<IslandFractal>()).Add(fractal);
        if (touched == null) return;
        foreach (var fractal in touched)
        {
            if (_pickupRefused.TryGetValue(fractal.Id, out var until) && _pickupClock < until) continue;
            if (PickUp(host, fractal.Id) == true) FractalsTouched++;
        }
    }

    /// <summary>
    /// The player's item.pickup of one Fractal: true when it was picked up, false when refused (quiet unless not_ready, then its words
    /// once until they change), null for no host.
    /// </summary>
    private bool? PickUp(CommandHost host, string fractal)
    {
        var result = host.PlayerPickUp(fractal);
        if (result["ok"]?.GetValue<bool>() == true)
        {
            _pickupRefused.Remove(fractal);
            return true;
        }
        _pickupRefused[fractal] = _pickupClock + PickupRetryS;
        if (_pickupRefused.Count > 64)
            foreach (var stale in _pickupRefused.Where(p => p.Value <= _pickupClock).Select(p => p.Key).ToList()) _pickupRefused.Remove(stale);
        if (result["error"]?["code"]?.GetValue<string>() == "not_ready" && result["error"]?["message"]?.GetValue<string>() is { Length: > 0 } words && words != _pickupNotice)
            _noticeText = _pickupNotice = words;
        return false;
    }

    /// <summary>
    /// The hands key on a Fractal: while the hands hold nothing, the nearest Fractal within reach is picked up (the host checks the clear
    /// way); with none in reach, the hands key does what it always did. Returns the words for the notice, or null when it did nothing.
    /// </summary>
    private string? HandsOnFractal()
    {
        if (Host is not { } host || host.HeldBy(CommandHost.PlayerAvatar) != null || Player == null || !IsInstanceValid(Player)) return null;
        var reach = host.FractalsInReach();
        if (reach.Count == 0) return null;
        var feet = Player.GlobalPosition;
        var nearest = host.Fractals.Where(f => reach.Contains(f.Id)).OrderBy(f => f.PositionM.DistanceSquaredTo(feet)).First();
        var result = host.PlayerPickUp(nearest.Id);
        if (result["ok"]?.GetValue<bool>() == true) return $"You picked up a {MeterWords.One(host, "fractals")}.";
        return result["error"]?["message"]?.GetValue<string>() ?? "That did not work.";
    }
}
