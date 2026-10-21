using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;
using GarageGame.Native.Room;

namespace GarageGame.Native.Ui;

/// <summary>
/// The journal's map: only the team's saved discoveries (the host's discovered block), never the room manifest's objects or the
/// camera. Discovered ground is inked, darker on the player's own level; discovered things are outlined where they were last
/// seen; the two avatars are marked where the host has them. The map lies as the room does from above, -Z at the top; its
/// compass points to the room's own north (the manifest's site.neg_z_bearing_deg), and a room that does not say which way it
/// faces gets no compass rather than a guess.
/// </summary>
public partial class JournalMap : Control
{
    /// <summary>
    /// Discovered ground this close to the player's height is inked darker as their own level. Body-sized (S8 of the 10x world kept
    /// it): a level is a surface the 10 cm body could stand on beside them (CompanionAvatar.LevelGapM), whatever the room's size.
    /// </summary>
    public const float OwnLevelM = CompanionAvatar.LevelGapM;
    private readonly DiscoveredMapData _data = new();
    private Vector2 _player;
    private Vector2 _companion;
    private float _height;
    private RoomData? _siteOf;
    private RoomSite? _site;
    public int KnownObjectCount => _data.Objects.Count;
    public int DiscoveredSpanCount => _data.Land.Count;
    /// <summary>Whether the map shows a compass: only for a room whose manifest declares its site.</summary>
    public bool ShowsCompass => _site is { Declared: true };
    /// <summary>Where north lies on the page, as a unit vector (+Y down); zero without a compass.</summary>
    public Vector2 North => _site is { Declared: true } site ? NorthOnPage(site.NegZBearingDeg) : Vector2.Zero;

    /// <summary>
    /// North on a map drawn with the room's -Z at the top and +X to the right, for a room whose -Z axis faces the compass bearing
    /// negZBearingDeg (0: -Z is north). Bearings turn clockwise, so north is the top turned back (anticlockwise) by the bearing:
    /// -Z facing east (90) puts north on the left.
    /// </summary>
    public static Vector2 NorthOnPage(float negZBearingDeg)
    {
        var bearing = Mathf.DegToRad(negZBearingDeg);
        return new Vector2(-Mathf.Sin(bearing), -Mathf.Cos(bearing));
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Resized += QueueRedraw;
    }

    /// <summary>Read the team's discovered map and the avatars' places from the host.</summary>
    public void UpdateFrom(CommandHost host)
    {
        _data.Read(host.ExportDiscovered());
        var avatars = host.Entities().Where(e => e["kind"]!.GetValue<string>() == "avatar").ToDictionary(e => e["id"]!.GetValue<string>(), e => Read(e["position_m"]!));
        var player = avatars.TryGetValue(CommandHost.PlayerAvatar, out var at) ? at : Vector3.Zero;
        var companion = avatars.TryGetValue(CommandHost.CompanionAvatarId, out var beside) ? beside : player;
        _player = new Vector2(player.X, player.Z);
        _companion = new Vector2(companion.X, companion.Z);
        _height = player.Y;
        // The site is read once per room: from its manifest, as the room loaded it.
        if (_siteOf != host.Room)
        {
            _siteOf = host.Room;
            _site = RoomSite.For(host.Room, out _);
        }
        QueueRedraw();
    }

    private static Vector3 Read(JsonNode node) => DiscoveredMapData.Point(node);

    public override void _Draw()
    {
        var box = new Rect2(_player - Vector2.One * 0.2f, Vector2.One * 0.4f).Expand(_companion);
        foreach (var land in _data.Land) box = box.Merge(land.Area);
        foreach (var thing in _data.Objects) box = box.Merge(thing.Area);
        box = box.Grow(0.15f);
        var scale = MathF.Min((Size.X - 30) / box.Size.X, (Size.Y - 40) / box.Size.Y);
        var margin = (Size - box.Size * scale) * 0.5f;
        Vector2 At(Vector2 p) => margin + (p - box.Position) * scale;
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.55f, 0.40f, 0.18f, 0.10f), false, 1);
        foreach (var land in _data.Land)
            DrawRect(new Rect2(At(land.Area.Position), land.Area.Size * scale), new Color(0.35f, 0.42f, 0.26f, MathF.Abs(land.Height - _height) < OwnLevelM ? 0.45f : 0.16f));
        foreach (var thing in _data.Objects)
            DrawRect(new Rect2(At(thing.Area.Position), thing.Area.Size * scale), new Color(0.4f, 0.28f, 0.15f, 0.65f), false, 1.2f);
        var ink = new Color("694533");
        DrawCircle(At(_player), 5, ink);
        var g = At(_companion);
        DrawPolyline(new[] { g + Vector2.Up * 6, g + Vector2.Right * 6, g + Vector2.Down * 6, g + Vector2.Left * 6, g + Vector2.Up * 6 }, new Color("376e6b"), 2, true);
        if (!ShowsCompass) return;
        // The compass in the top-right corner: a needle pointing to the room's north, an N at its tip.
        var centre = new Vector2(Size.X - 36, 38);
        var north = North;
        DrawLine(centre - north * 12, centre + north * 12, ink, 1.2f, true);
        DrawCircle(centre + north * 12, 2, ink);
        var letter = centre + north * 21 + new Vector2(-5, 6);
        DrawString(GetThemeDefaultFont(), letter, "N", fontSize: 15, modulate: ink);
    }
}
