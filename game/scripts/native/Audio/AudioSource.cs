using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Native.Audio;

/// <summary>A lit campfire the director may play the fire's sound at.</summary>
public readonly record struct LitFire(string Id, Vector3 Position);

/// <summary>Where the player's feet are in relation to water: not at all, wading (shallow), or swimming.</summary>
public enum FeetWater { Dry, Wading, Swimming }

/// <summary>
/// Everything the director reads about the world in one frame, as plain values. The room's source fills it from the game's public state;
/// a test fills it by hand, so the director's rules are checked without a room.
/// </summary>
public sealed class AudioSnapshot
{
    /// <summary>0 to 1: how hard it rains now (NatureWeather.Rain01).</summary>
    public float Rain01;
    /// <summary>Wind speed in m/s (the length of NatureWeather.WindMps; calm is about 0.12, rainy about 0.22).</summary>
    public float WindMps;
    /// <summary>0 at night, 1 in full day (LookMoment.Daylight).</summary>
    public float Daylight = 1f;
    /// <summary>winter, spring, summer or autumn.</summary>
    public string Season = "summer";
    /// <summary>True out of doors on open land; false inside a room (wind, rain and the sea are then muffled).</summary>
    public bool Outdoors = true;
    /// <summary>0 far from any shore, 1 at the water's edge.</summary>
    public float SeaNear01;
    public Vector3 PlayerPosition;
    /// <summary>Horizontal speed of the player, m/s.</summary>
    public float PlayerSpeedMps;
    public bool OnFloor = true;
    public FeetWater Water = FeetWater.Dry;
    /// <summary>What the player stands on now: one of Sounds.Grounds. Read only when a footstep is due.</summary>
    public Func<string> GroundNow = () => "grass";
    public IReadOnlyList<LitFire> Fires = Array.Empty<LitFire>();
    /// <summary>True while the game is paused or a menu covers it: the ambience fades out.</summary>
    public bool Paused;
}

public interface IAudioSource
{
    AudioSnapshot Read();
}

/// <summary>The real source: reads the room's bodies, look, weather and campfires, and changes none of them.</summary>
public sealed class RoomAudioSource : IAudioSource
{
    /// <summary>Within this distance of the coast (m) the sea starts to be heard; at the edge it is at full strength.</summary>
    public const float SeaHearingM = 14f;

    private readonly RoomWorld _world;
    private readonly CommandHost? _host;
    private readonly AudioSnapshot _snapshot = new();
    private double _seaClock;
    private float _sea;

    public RoomAudioSource(RoomWorld world, CommandHost? host) { _world = world; _host = host; }

    public AudioSnapshot Read()
    {
        var s = _snapshot;
        var player = _world.Player;
        var look = _world.Look;
        if (_host != null)
        {
            var weather = _host.NatureWeather;
            s.Rain01 = weather.Rain01;
            s.WindMps = weather.WindMps.Length();
            s.Fires = _host.CampSites.Where(c => _host.IsCampfireLit(c.Id)).Select(c => new LitFire(c.Id, c.PositionM)).ToArray();
        }
        if (look?.Moment is { } moment) { s.Daylight = moment.Daylight; s.Season = moment.Season; }
        s.Outdoors = look?.OpenLand ?? false;
        if (player != null && GodotObject.IsInstanceValid(player))
        {
            s.PlayerPosition = player.GlobalPosition;
            s.PlayerSpeedMps = new Vector2(player.Velocity.X, player.Velocity.Z).Length();
            s.OnFloor = player.IsOnFloor();
            var water = player.Water;
            s.Water = !water.Wet || water.Under(player.GlobalPosition) <= 0 ? FeetWater.Dry
                : water.DepthM >= player.SwimDepthM ? FeetWater.Swimming : FeetWater.Wading;
            s.GroundNow = () => GroundUnder(player);
            // The shore is measured four times a second, not every frame: the coast outline can have thousands of points.
            if (look?.OpenSea?.Sea is { } sea && sea.Coast.Length > 1 && (_seaClock -= player.GetPhysicsProcessDeltaTime()) <= 0)
            {
                _seaClock = 0.25;
                _sea = SeaNear(sea.Coast, new Vector2(s.PlayerPosition.X, s.PlayerPosition.Z));
            }
            else if (look?.OpenSea == null) _sea = 0;
            s.SeaNear01 = _sea;
        }
        s.Paused = _world.GetTree()?.Paused ?? false;
        return s;
    }

    /// <summary>1 at the coast line falling to 0 at SeaHearingM away from it (inside or outside).</summary>
    public static float SeaNear(Vector2[] coast, Vector2 point)
    {
        var best = float.MaxValue;
        for (var i = 0; i < coast.Length; i++)
        {
            var a = coast[i]; var b = coast[(i + 1) % coast.Length];
            var ab = b - a;
            var t = ab.LengthSquared() > 0 ? Mathf.Clamp((point - a).Dot(ab) / ab.LengthSquared(), 0f, 1f) : 0f;
            best = Mathf.Min(best, point.DistanceTo(a + ab * t));
        }
        return Mathf.Clamp(1f - best / SeaHearingM, 0f, 1f);
    }

    /// <summary>
    /// The ground under the feet from what the ray hits: a collider (or a parent) named or tagged sand, stone, wood or dirt; otherwise
    /// grass on open land and wood indoors. Names are read as hints only, never as anything to act on.
    /// </summary>
    private string GroundUnder(SmallPlayerController player)
    {
        var from = player.GlobalPosition + Vector3.Up * 0.03f;
        var query = PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * 0.2f);
        query.Exclude = new Godot.Collections.Array<Rid> { player.GetRid() };
        var hit = player.GetWorld3D().DirectSpaceState.IntersectRay(query);
        Node? node = hit.Count > 0 ? hit["collider"].AsGodotObject() as Node : null;
        for (var depth = 0; node != null && depth < 4; depth++, node = node.GetParent())
        {
            if (node.HasMeta("ground") && node.GetMeta("ground").AsString() is { } tag && Sounds.Grounds.Contains(tag)) return tag;
            var found = GroundFromName(node.Name);
            if (found != null) return found;
        }
        return _world.Look?.OpenLand == true ? "grass" : "wood";
    }

    public static string? GroundFromName(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("sand") || n.Contains("beach") || n.Contains("shore")) return "sand";
        if (n.Contains("stone") || n.Contains("rock") || n.Contains("tile") || n.Contains("concrete") || n.Contains("path")) return "stone";
        if (n.Contains("wood") || n.Contains("floor") || n.Contains("deck") || n.Contains("jetty") || n.Contains("plank") || n.Contains("table")) return "wood";
        if (n.Contains("dirt") || n.Contains("soil") || n.Contains("mud") || n.Contains("earth")) return "dirt";
        if (n.Contains("grass") || n.Contains("meadow") || n.Contains("moss")) return "grass";
        return null;
    }
}
