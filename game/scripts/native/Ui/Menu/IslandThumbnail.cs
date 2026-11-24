using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Look.Intro;

namespace GarageGame.Native.Ui;

/// <summary>
/// The land's picture on its card (the room drawn as land, see LandPicture), drawn from its shape data (the manifest's <c>x_landscape_source</c>, the room the island grew
/// from: its floor, walls and posed boxes as coloured polygons, seen from above; no photograph is in anything the game reads,
/// docs/game/MECHANICS.md section 3). A room without shape data draws a plain stand-in (a roof over a door). North (-Z) is up.
/// The picture is drawn once into the control's own commands; the biggest boxes only (the 10x island lists thousands).
/// </summary>
public partial class IslandThumbnail : Control
{
    /// <summary>At most this many boxes are drawn, the biggest footprints first.</summary>
    public const int MaxBoxes = 400;
    public static readonly Color Ground = new("e6d9b4");
    public static readonly Color WallInk = new("f0e6cf");

    private IntroSource? _shape;
    /// <summary>What was drawn: floor triangles, wall strokes and boxes (the test reads these to know the picture is the island's).</summary>
    public int FloorTriangles { get; private set; }
    public int WallStrokes { get; private set; }
    public int BoxesDrawn { get; private set; }
    public bool HasShape => _shape != null;

    public IslandThumbnail()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(220, 130);
    }

    /// <summary>The land as the picture draws it (null for a room without shape data); its Counts are what the test reads.</summary>
    public LandPicture? Land => _land;
    private LandPicture? _land;

    /// <param name="rim">A land made with the rim record gives its notches and bays here (it has no sea; none is drawn either way).</param>
    public void SetShape(IntroSource? shape, IReadOnlyList<RimOpening>? rim = null)
    {
        _shape = shape;
        _land = shape == null ? null : LandPicture.Build(shape, rim);
        QueueRedraw();
    }

    public override void _Draw()
    {
        FloorTriangles = WallStrokes = BoxesDrawn = 0;
        var size = Size;
        if (_land == null)
        {
            DrawRect(new Rect2(Vector2.Zero, size), LandPicture.Sand);
            DrawStandIn(size);
            return;
        }
        _land.Draw(this, size);
        foreach (var floor in _land.Floors)
            FloorTriangles += Math.Max(floor.Length - 2, 0);
        WallStrokes = _land.Counts.Bands;
        BoxesDrawn = _land.Counts.Things;
    }

    /// <summary>The picture of a room that has no shape data: a house in outline.</summary>
    private void DrawStandIn(Vector2 size)
    {
        var centre = size * 0.5f;
        var half = new Vector2(Math.Min(size.X, size.Y * 1.6f) * 0.22f, size.Y * 0.2f);
        var body = new Rect2(centre - half + new Vector2(0, size.Y * 0.08f), half * 2);
        DrawRect(body, new Color("3b6a63"));
        DrawRect(body, WallInk, false, 2f);
        var roof = new[] { new Vector2(body.Position.X - 8, body.Position.Y), new Vector2(centre.X, body.Position.Y - size.Y * 0.22f), new Vector2(body.End.X + 8, body.Position.Y) };
        DrawColoredPolygon(roof, new Color("c98562"));
        DrawRect(new Rect2(centre.X - 9, body.End.Y - 26, 18, 26), new Color("1b2f2c"));
    }
}
