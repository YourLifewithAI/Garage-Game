using Godot;
using System;
using System.Collections.Generic;
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
    /// <summary>True while the game is paused or a menu covers it: the world's sounds fade out.</summary>
    public bool Paused;
}

public interface IAudioSource
{
    AudioSnapshot Read();
}

/// <summary>The real source: reads the room's bodies, look, weather and campfires, and changes none of them. One snapshot, reused every frame.</summary>
public sealed class RoomAudioSource : IAudioSource, IDisposable
{
    /// <summary>Within this distance of the coast (m) the sea starts to be heard; at the edge it is at full strength.</summary>
    public const float SeaHearingM = 14f;
    /// <summary>A footstep looks this far above and below the feet for the ground (m).</summary>
    public const float GroundRayUpM = 0.03f, GroundRayDownM = 0.2f;

    private readonly RoomWorld _world;
    private readonly CommandHost? _host;
    private readonly AudioSnapshot _snapshot = new();
    private readonly List<LitFire> _fires = new();
    private PhysicsRayQueryParameters3D? _ray;
    private double _seaClock;
    private float _sea;

    public RoomAudioSource(RoomWorld world, CommandHost? host)
    {
        _world = world;
        _host = host;
        _snapshot.Fires = _fires;
        _snapshot.GroundNow = GroundUnder;
    }

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
            _fires.Clear();
            var sites = _host.CampSites;
            for (var i = 0; i < sites.Count; i++)
                if (_host.IsCampfireLit(sites[i].Id)) _fires.Add(new LitFire(sites[i].Id, sites[i].PositionM));
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
            // The shore is measured four times a second, not every frame: the coast outline can have thousands of points.
            if (look?.OpenSea?.Sea is { } sea && sea.Coast.Length > 1)
            {
                if ((_seaClock -= player.GetPhysicsProcessDeltaTime()) <= 0)
                {
                    _seaClock = 0.25;
                    _sea = SeaNear(sea.Coast, new Vector2(s.PlayerPosition.X, s.PlayerPosition.Z));
                }
            }
            else _sea = 0;
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

    /// <summary>The ground under the player's feet: a short ray down, the hit read by <see cref="GroundOfNode"/>.</summary>
    private string GroundUnder()
    {
        var player = _world.Player;
        if (player == null || !GodotObject.IsInstanceValid(player)) return Fallback();
        var from = player.GlobalPosition + Vector3.Up * GroundRayUpM;
        if (_ray == null)
        {
            _ray = PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * GroundRayDownM);
            _ray.Exclude = new Godot.Collections.Array<Rid> { player.GetRid() };
        }
        _ray.From = from;
        _ray.To = from + Vector3.Down * (GroundRayUpM + GroundRayDownM);
        using var hit = player.GetWorld3D().DirectSpaceState.IntersectRay(_ray);
        if (hit.Count == 0) return Fallback();
        using var collider = hit["collider"];
        return GroundOfNode(collider.AsGodotObject() as Node, _world.Look?.OpenLand == true);
    }

    private string Fallback() => _world.Look?.OpenLand == true ? "grass" : "wood";

    public void Dispose()
    {
        _ray?.Dispose();
        _ray = null;
    }

    /// <summary>
    /// What a node, or the nearest parent that says, sounds like underfoot. Reads what the builders write: the <c>material_role</c> meta
    /// on a room part, a plant copy's holder (PlantCopiesView) and a collision body (wood and floors are wood; stone, rock, plaster and
    /// metal are stone; sand; soil and dirt; grass, moss, leaves and foliage are grass). A <c>ground</c> meta overrides it, as a
    /// hand-made level may. Nothing found: grass outdoors, wood indoors.
    /// </summary>
    public static string GroundOfNode(Node? node, bool outdoors)
    {
        for (var depth = 0; node != null && depth < 6; depth++, node = node.GetParent())
        {
            if (node.HasMeta("ground"))
            {
                using var tag = node.GetMeta("ground");
                var text = tag.AsString();
                if (Array.IndexOf(Sounds.Grounds, text) >= 0) return text;
            }
            if (node.HasMeta("material_role"))
            {
                using var role = node.GetMeta("material_role");
                if (GroundFromRole(role.AsString(), outdoors) is { } fromRole) return fromRole;
            }
        }
        return outdoors ? "grass" : "wood";
    }

    public static string? GroundFromRole(string role, bool outdoors) => role switch
    {
        "wood" or "wood_painted" or "floor" or "fabric" or "paper" or "bark" => "wood",
        "stone" or "rock" or "plaster" or "metal" or "metal_painted" or "metallic" or "glass" or "concrete" or "tile" or "landmark" => "stone",
        "sand" or "beach" => "sand",
        "soil" or "dirt" or "mud" or "earth" => "dirt",
        "grass" or "grass_tufts" or "moss" or "leaf" or "foliage" => "grass",
        "ground" => outdoors ? "grass" : "dirt",
        _ => null,
    };
}
