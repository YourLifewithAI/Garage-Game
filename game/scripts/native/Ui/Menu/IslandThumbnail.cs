using Godot;
using System;
using System.Linq;
using GarageGame.Native.Look.Intro;

namespace GarageGame.Native.Ui;

/// <summary>
/// An island's picture on its card, drawn from its shape data (the manifest's <c>x_landscape_source</c>, the room the island grew
/// from: its floor, walls and posed boxes as coloured polygons, seen from above; no photograph is in anything the game reads,
/// docs/game/MECHANICS.md section 3). A room without shape data draws a plain stand-in (a roof over a door). North (-Z) is up.
/// The picture is drawn once into the control's own commands; the biggest boxes only (the 10x island lists thousands).
/// </summary>
public partial class IslandThumbnail : Control
{
    /// <summary>At most this many boxes are drawn, the biggest footprints first.</summary>
    public const int MaxBoxes = 400;
    public static readonly Color Ground = new("2a4a47");
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

    public void SetShape(IntroSource? shape)
    {
        _shape = shape;
        QueueRedraw();
    }

    public override void _Draw()
    {
        FloorTriangles = WallStrokes = BoxesDrawn = 0;
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), Ground);
        if (_shape == null)
        {
            DrawStandIn(size);
            return;
        }
        var bounds = _shape.Bounds;
        var margin = 8f;
        var span = new Vector2(Math.Max(bounds.Size.X, 0.001f), Math.Max(bounds.Size.Z, 0.001f));
        var scale = Math.Min((size.X - 2 * margin) / span.X, (size.Y - 2 * margin) / span.Y);
        var origin = (size - span * scale) * 0.5f;
        Vector2 Map(Vector3 p) => origin + new Vector2(p.X - bounds.Position.X, p.Z - bounds.Position.Z) * scale;

        foreach (var floor in _shape.Floors)
        {
            var points = floor.Points.Select(Map).ToArray();
            var triangles = Geometry2D.TriangulatePolygon(points);
            for (var i = 0; i + 2 < triangles.Length; i += 3)
            {
                DrawColoredPolygon(new[] { points[triangles[i]], points[triangles[i + 1]], points[triangles[i + 2]] }, IntroSource.DefaultFloorColour);
                FloorTriangles++;
            }
        }
        foreach (var wall in _shape.Walls)
        {
            var points = wall.Points.Select(Map).ToArray();
            // A wall stands upright: seen from above it is the line between its two furthest points.
            var (a, b, far) = (points[0], points[0], 0f);
            for (var i = 0; i < points.Length; i++)
                for (var j = i + 1; j < points.Length; j++)
                    if (points[i].DistanceSquaredTo(points[j]) > far) (a, b, far) = (points[i], points[j], points[i].DistanceSquaredTo(points[j]));
            if (far > 0.25f) { DrawLine(a, b, WallInk, 2f, true); WallStrokes++; }
        }
        foreach (var box in _shape.Boxes.OrderByDescending(b => b.SizeM.X * b.SizeM.Z).Take(MaxBoxes).Reverse())
        {
            var half = new Vector2(box.SizeM.X, box.SizeM.Z) * 0.5f;
            var (sin, cos) = (MathF.Sin(box.YawRad), MathF.Cos(box.YawRad));
            // The box's own axes turned by its heading, as IntroBox.Covers reads them.
            Vector2 Corner(float along, float across) => Map(new Vector3(box.BottomM.X + along * cos + across * sin, 0, box.BottomM.Z - along * sin + across * cos));
            var corners = new[] { Corner(-half.X, -half.Y), Corner(half.X, -half.Y), Corner(half.X, half.Y), Corner(-half.X, half.Y) };
            DrawColoredPolygon(corners, box.Colour);
            DrawPolyline(new[] { corners[0], corners[1], corners[2], corners[3], corners[0] }, box.Colour.Darkened(0.4f), 1f, true);
            BoxesDrawn++;
        }
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
