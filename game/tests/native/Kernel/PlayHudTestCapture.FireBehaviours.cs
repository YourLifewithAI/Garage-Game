using Godot;
using System;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The Fire behaviours packet's captures, in the real window on a scratch profile: "fractal" walks the body into the island's nearest
/// Fractal (before, the sparks, after); "fire" shows the wheel's Fire wedge locked before the first rest, then, Fire learned, a Will-o'-wisp
/// set over the nearest water by key 6 and the ask's hold, by day and by night.
/// </summary>
public partial class PlayHudTestCapture
{
    private async Task FractalCapture()
    {
        var host = Host;
        await Stage();
        var player = _world.Player;
        var spawn = player.GlobalPosition;
        var fractal = host.Fractals.OrderBy(f => f.PositionM.DistanceSquaredTo(spawn)).FirstOrDefault();
        if (fractal == null)
        {
            // None of the founder's installed islands places Fractals yet: one is set on the floor 40 cm ahead (the host's test seam),
            // and the look is bound again so it draws it, as an island whose manifest placed it would.
            // To the side, clear of the starting campfire in front of the spawn.
            var ahead = spawn + player.GlobalBasis * new Vector3(0.5f, 0, 0);
            using var down = PhysicsRayQueryParameters3D.Create(ahead + Vector3.Up * 0.5f, ahead + Vector3.Down * 0.5f, GarageGame.Native.Room.RoomBuilder.WorldLayer);
            using var floor = player.GetWorld3D().DirectSpaceState.IntersectRay(down);
            if (floor.Count > 0) ahead = floor["position"].AsVector3();
            host.FractalsForTests = new[] { new IslandFractal("fractal:capture01", ahead, null, null) };
            _world.GetNodeOrNull<GarageGame.Native.Look.Fractals.FractalLook>("FractalLook")?.Configure(host, _world.Look);
            fractal = host.Fractals.First();
            GD.Print("PLAY_HUD_CAPTURE: this island places no Fractals; one is set 50 cm to the side for the capture");
        }
        // Stand 30 cm short of it, facing it, the Gubble out of the way.
        var toward = new Vector3(fractal.PositionM.X - spawn.X, 0, fractal.PositionM.Z - spawn.Z).Normalized();
        if (!toward.IsFinite() || toward.LengthSquared() < 0.5f) toward = Vector3.Forward;
        var start = fractal.PositionM - toward * 0.3f + Vector3.Up * 0.02f;
        player.TryTeleportTo(start);
        player.Rotation = new Vector3(0, Mathf.Atan2(-toward.X, -toward.Z), 0);
        player.ResetPhysicsInterpolation();
        _world.Companion.TryTeleportTo(start - toward * 0.25f + new Vector3(toward.Z, 0, -toward.X) * 0.15f);
        await Frames(60);
        var found = host.Vitals.Fractals.Found;
        GD.Print($"PLAY_HUD_CAPTURE fractal {fractal.Id} at {fractal.PositionM}, the player {player.GlobalPosition.DistanceTo(fractal.PositionM):0.00} m short");
        await Save("fractal_before");
        player.SetControlInput(new Vector2(0, 1));
        var frames = 0;
        for (; frames < 240 && host.Fractals.Any(f => f.Id == fractal.Id); frames++) await NextFrame();
        await Frames(4);
        await Save("fractal_sparks");
        player.SetControlInput(Vector2.Zero);
        await Frames(60);
        await Save("fractal_after");
        GD.Print($"PLAY_HUD_CAPTURE fractal collected={!host.Fractals.Any(f => f.Id == fractal.Id)} after {frames} frames; found {found} -> {host.Vitals.Fractals.Found}; notice \"{_hud.NoticeText}\"");
    }

    private async Task FireCapture()
    {
        var host = Host;
        await Stage();
        host.ConfigureGlowProgress(new MemoryGlow(GlowProgress.Fresh));
        _hud.LightForTests = _ => 0.9f;
        _hud._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true });
        await Frames(40);
        await Save("fire_wheel_locked");
        _hud._UnhandledInput(new InputEventKey { PhysicalKeycode = Key.Escape, Pressed = true });
        _hud._UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false });
        _hud.LightForTests = null;
        await Frames(10);
        host.LearnFireForTests();
        // The nearest open water to the spawn, and dry ground beside it to stand on.
        if (FindShore(_world.Player.GlobalPosition) is not { } shore) { GD.Print("PLAY_HUD_CAPTURE: no water found near the spawn"); return; }
        var (water, dry) = shore;
        _world.Player.TryTeleportTo(dry + Vector3.Up * 0.02f);
        var toward = new Vector3(water.X - dry.X, 0, water.Z - dry.Z).Normalized();
        _world.Player.Rotation = new Vector3(0, Mathf.Atan2(-toward.X, -toward.Z), 0);
        _world.Player.ResetPhysicsInterpolation();
        _world.Companion.TryTeleportTo(dry + new Vector3(toward.Z, 0, -toward.X) * 0.15f + Vector3.Up * 0.02f);
        _world.Companion.Stay();
        await Frames(60);
        _hud.AimRayForTests = (water + Vector3.Up * 0.3f, water + Vector3.Down * 0.3f);
        _hud._UnhandledInput(new InputEventKey { PhysicalKeycode = Key.Key6, Pressed = true });
        _hud._UnhandledInput(new InputEventKey { PhysicalKeycode = Key.Key6, Pressed = false });
        // Out of the Gubble's reach or the team's sight it comes closer first, then previews (the slots' rule).
        for (var i = 0; i < 600 && !_hud.WheelFireArmed && (_hud.CastWaiting || i < 10); i++) await NextFrame();
        GD.Print($"PLAY_HUD_CAPTURE fire preview armed={_hud.WheelFireArmed} kind={_hud.WheelFireKind}: {_hud.NoticeText}");
        await Save("fire_wisp_preview");
        _hud.BeginWheelFireHold();
        for (var i = 0; i < 200 && _hud.WheelFireArmed; i++) await NextFrame();
        _hud.AimRayForTests = null;
        await Frames(90);
        GD.Print($"PLAY_HUD_CAPTURE wisps {host.Wisps.Count}; notice \"{_hud.NoticeText}\"");
        await Save("fire_wisp_day");
        _world.Look.SetClock(22f, DateTime.ParseExact(Arg("date", "2026-10-06"), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).DayOfYear);
        await Frames(120);
        await Save("fire_wisp_night");
    }

    /// <summary>A water point at least 8 cm deep within 12 m of the spawn and a dry floor point within 1.2 m of it, from the shore, or null.</summary>
    private (Vector3 Water, Vector3 Dry)? FindShore(Vector3 from)
    {
        var space = _world.Player.GetWorld3D().DirectSpaceState;
        var sea = _world.Room.Sea;
        for (var ring = 0.5f; ring <= 12f; ring += 0.25f)
        for (var step = 0; step < 48; step++)
        {
            var angle = step * Mathf.Tau / 48;
            var probe = from + new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle)) * ring;
            if (sea == null) continue;
            var water = new Vector3(probe.X, sea.LevelM, probe.Z);
            // Deep enough for a wisp (FireKindLaws.WispMinDepthM, with room to spare): the shoreline's film of water is not.
            if (sea.OpenWater(space, water, 0.01f, 0.01f, _world.Player.GetRid()) is not { Wet: true } open || open.DepthM < 0.08f) continue;
            // Back toward the spawn: the first dry floor within 0.6 m.
            for (var back = 0.15f; back <= 1.2f; back += 0.05f)
            {
                var land = water + new Vector3(from.X - water.X, 0, from.Z - water.Z).Normalized() * back;
                using var query = PhysicsRayQueryParameters3D.Create(land + Vector3.Up * 2f, land + Vector3.Down * 2f, GarageGame.Native.Room.RoomBuilder.WorldLayer);
                using var hit = space.IntersectRay(query);
                if (hit.Count == 0) continue;
                var floor = hit["position"].AsVector3();
                if (floor.Y > sea.LevelM + 0.01f && !sea.OpenWater(space, floor + Vector3.Up * 0.01f, 0.01f, 0.01f, _world.Player.GetRid()).Wet) return (water, floor);
            }
        }
        return null;
    }

    private sealed class MemoryGlow(GlowProgress state) : IGlowProgressStore
    {
        public GlowProgress Load() => state;
        public void Save(GlowProgress next) => state = next;
    }
}
