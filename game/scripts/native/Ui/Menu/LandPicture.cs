using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Look.Intro;

namespace GarageGame.Native.Ui;

/// <summary>An opening in the land's rim record (RUN-4.md section 4): <c>notch</c> or <c>bay</c>, where it stands (x, z) and how wide.</summary>
public sealed record RimOpening(string Kind, Vector2 CentreM, float WidthM);

/// <summary>What the land's picture holds, counted the way the menu test counts it (docs/runs/RUN-4-MENU.md, MENU-5).</summary>
public sealed record LandCounts(int Bands, int Landforms, int CliffSide, int Tops, int Bays, int Notches, int Sea)
{
    /// <summary>Every thing drawn, however it stands.</summary>
    public int Things => Landforms + CliffSide + Tops;
}

/// <summary>A wall seen from above: a cliff band, the strip of the floor within one band of the wall line.</summary>
public sealed record LandBand(Vector2 A, Vector2 B, Vector2 Inward, float HeightM, string HostId);

/// <summary>One thing, a landform: its true footprint, its height, its colour and how it stands (<c>land</c>, <c>cliff</c> or <c>top</c>).</summary>
public sealed record LandForm(Vector2[] Corners, Vector2 Centre, float BottomM, float HeightM, Color Colour, string Stands, int Level, Vector2? Foot);

/// <summary>A door or window in a band: a bay (a gap in the cliff, open to the plain) or a notch (a cut high in the cliff).</summary>
public sealed record LandGap(string Kind, Vector2 A, Vector2 B, Vector2 Inward);

/// <summary>
/// The room drawn as land for the menu (docs/runs/RUN-4-MENU.md, "The map"): the walls a band of cliff in hachures, each thing a
/// landform in its scan colour at its true footprint, one by a wall part of the cliff, one resting on another standing on top of it,
/// a door a bay, a window a notch. Sand runs to the paper's edge; no sea is ever drawn, so a land made with the rim (no sea) and the
/// sea islands draw the same way. The plan is built once from the shape data (metres, +Z down the page, north up) and drawn at any
/// size; the shape data is untrusted and already bounded by IntroSource.
/// </summary>
public sealed class LandPicture
{
    /// <summary>At most this many things are drawn, the biggest footprints first.</summary>
    public const int MaxThings = 400;
    /// <summary>A cliff band is this share of the land's short side; a thing within one band of a wall is cliff-side.</summary>
    public const float BandShare = 0.04f;

    public static readonly Color Sand = new("e6d9b4");
    public static readonly Color Plain = new("d4c28f");
    public static readonly Color Rock = new("8a7355");
    public static readonly Color RockDark = new("4d3f30");
    public static readonly Color Ink = new("3a2e24");

    public Aabb Bounds { get; init; }
    public IReadOnlyList<Vector2[]> Floors { get; init; } = Array.Empty<Vector2[]>();
    public IReadOnlyList<LandBand> Bands { get; init; } = Array.Empty<LandBand>();
    public IReadOnlyList<LandForm> Forms { get; init; } = Array.Empty<LandForm>();
    public IReadOnlyList<LandGap> Gaps { get; init; } = Array.Empty<LandGap>();
    /// <summary>The band's width in metres: 4% of the land's short side.</summary>
    public float BandM { get; init; }
    /// <summary>True when the openings came from a rim record, not from the room's doors and windows.</summary>
    public bool FromRim { get; init; }
    /// <summary>Counts of what the plan holds (what the test reads to know the picture is the room's).</summary>
    public LandCounts Counts { get; init; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>The plan of a room's shape data. A rim record's openings, when given, stand in for the room's doors and windows.</summary>
    public static LandPicture Build(IntroSource shape, IReadOnlyList<RimOpening>? rim = null)
    {
        var bounds = shape.Bounds;
        var band = BandShare * Math.Max(Math.Min(bounds.Size.X, bounds.Size.Z), 0.001f);
        var floors = shape.Floors.Select(f => f.Points.Select(Flat).ToArray()).ToList();
        var inside = floors.Count > 0 ? floors.SelectMany(f => f).Aggregate(Vector2.Zero, (s, p) => s + p) / floors.Sum(f => f.Length)
                                       : new Vector2(bounds.GetCenter().X, bounds.GetCenter().Z);

        var bands = new List<LandBand>();
        var hosts = new Dictionary<string, LandBand>(StringComparer.Ordinal);
        foreach (var wall in shape.Walls)
        {
            var points = wall.Points.Select(Flat).ToArray();
            // A wall stands upright: seen from above it is the line between its two furthest points.
            var (a, b, far) = (points[0], points[0], 0f);
            for (var i = 0; i < points.Length; i++)
                for (var j = i + 1; j < points.Length; j++)
                    if (points[i].DistanceSquaredTo(points[j]) > far) (a, b, far) = (points[i], points[j], points[i].DistanceSquaredTo(points[j]));
            if (far < band * band * 0.01f) continue;
            var along = (b - a).Normalized();
            var normal = new Vector2(-along.Y, along.X);
            if (normal.Dot(inside - (a + b) * 0.5f) < 0) normal = -normal;
            var height = wall.Points.Max(p => p.Y) - wall.Points.Min(p => p.Y);
            var made = new LandBand(a, b, normal, height, wall.Id);
            bands.Add(made);
            hosts[wall.Id] = made;
        }

        var forms = Forms_(shape, bands, band);

        var gaps = new List<LandGap>();
        if (rim != null)
        {
            foreach (var opening in rim.Take(16))
                if (Gap(opening.Kind, opening.CentreM, opening.WidthM, null, bands) is { } gap) gaps.Add(gap);
        }
        else
        {
            foreach (var opening in shape.Openings)
            {
                var kind = opening.Kind is "window" or "vent" ? "notch" : "bay";
                hosts.TryGetValue(opening.HostId, out var host);
                if (Gap(kind, Flat(opening.CentreM), opening.SizeM.X, host, bands) is { } gap) gaps.Add(gap);
            }
        }

        return new LandPicture
        {
            Bounds = bounds, Floors = floors, Bands = bands, Forms = forms, Gaps = gaps, BandM = band, FromRim = rim != null,
            Counts = new LandCounts(bands.Count, forms.Count(f => f.Stands == "land"), forms.Count(f => f.Stands == "cliff"), forms.Count(f => f.Stands == "top"),
                gaps.Count(g => g.Kind == "bay"), gaps.Count(g => g.Kind == "notch"), 0),
        };
    }

    private static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

    private static List<LandForm> Forms_(IntroSource shape, List<LandBand> bands, float band)
    {
        var floorY = shape.FloorY;
        var boxes = shape.Boxes.OrderByDescending(b => b.SizeM.X * b.SizeM.Z).Take(MaxThings).ToList();
        var forms = new List<LandForm>(boxes.Count);
        // The box it rests on: the highest other box whose top is at this one's bottom and whose footprint holds its centre.
        var hosts = new IntroBox?[boxes.Count];
        for (var i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            if (box.BottomM.Y <= floorY + 0.03f) continue;
            for (var j = 0; j < boxes.Count; j++)
            {
                if (j == i) continue;
                var under = boxes[j];
                if (Math.Abs(under.TopM - box.BottomM.Y) > Math.Max(0.03f, 0.05f * under.SizeM.Y) || !under.Covers(box.BottomM.X, box.BottomM.Z)) continue;
                if (hosts[i] == null || under.TopM > hosts[i]!.TopM) hosts[i] = under;
            }
        }
        // A thing's level is how many things it stands on (a stack of three is levels 0, 1, 2); a ring of hosts cannot loop.
        int Level(int index, int depth) => hosts[index] == null || depth > 8 ? 0 : 1 + Level(boxes.IndexOf(hosts[index]!), depth + 1);
        for (var i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            var half = new Vector2(box.SizeM.X, box.SizeM.Z) * 0.5f;
            var (sin, cos) = (MathF.Sin(box.YawRad), MathF.Cos(box.YawRad));
            // The box's own axes turned by its heading, as IntroBox.Covers reads them.
            Vector2 Corner(float along, float across) => new(box.BottomM.X + along * cos + across * sin, box.BottomM.Z - along * sin + across * cos);
            var corners = new[] { Corner(-half.X, -half.Y), Corner(half.X, -half.Y), Corner(half.X, half.Y), Corner(-half.X, half.Y) };
            var centre = new Vector2(box.BottomM.X, box.BottomM.Z);
            var level = Level(i, 0);
            var stands = "land";
            Vector2? foot = null;
            if (level > 0) stands = "top";
            else
            {
                LandBand? nearest = null;
                var nearestGap = float.MaxValue;
                foreach (var candidate in bands)
                    foreach (var point in corners.Append(centre))
                    {
                        var gap = point.DistanceTo(Closest(candidate, point));
                        if (gap < nearestGap) (nearest, nearestGap) = (candidate, gap);
                    }
                if (nearest != null && nearestGap <= band) { stands = "cliff"; foot = Closest(nearest, centre); }
            }
            forms.Add(new LandForm(corners, centre, box.BottomM.Y - floorY, box.SizeM.Y, box.Colour, stands, level, foot));
        }
        return forms;
    }

    private static Vector2 Closest(LandBand band, Vector2 point)
    {
        var line = band.B - band.A;
        var length = line.LengthSquared();
        var t = length <= 0 ? 0 : Math.Clamp((point - band.A).Dot(line) / length, 0f, 1f);
        return band.A + line * t;
    }

    private static LandGap? Gap(string kind, Vector2 centre, float width, LandBand? host, List<LandBand> bands)
    {
        if (bands.Count == 0 || width <= 0) return null;
        var band = host ?? bands.OrderBy(b => centre.DistanceTo(Closest(b, centre))).First();
        var middle = Closest(band, centre);
        var along = (band.B - band.A).Normalized();
        var half = Math.Min(width, band.A.DistanceTo(band.B)) * 0.5f;
        return new LandGap(kind, middle - along * half, middle + along * half, band.Inward);
    }

    /// <summary>Draw the plan into a canvas item's current draw call, filling the rectangle (north up, the land centred, margin in pixels).</summary>
    public void Draw(CanvasItem canvas, Vector2 size, float margin = 8f)
    {
        canvas.DrawRect(new Rect2(Vector2.Zero, size), Sand);
        var span = new Vector2(Math.Max(Bounds.Size.X, 0.001f), Math.Max(Bounds.Size.Z, 0.001f));
        var scale = Math.Min((size.X - 2 * margin) / span.X, (size.Y - 2 * margin) / span.Y);
        var origin = (size - span * scale) * 0.5f;
        Vector2 Map(Vector2 p) => origin + (p - new Vector2(Bounds.Position.X, Bounds.Position.Z)) * scale;
        var bandPx = Math.Max(BandM * scale, 2.5f);

        foreach (var floor in Floors)
        {
            var points = floor.Select(Map).ToArray();
            var triangles = Geometry2D.TriangulatePolygon(points);
            for (var i = 0; i + 2 < triangles.Length; i += 3)
                canvas.DrawColoredPolygon(new[] { points[triangles[i]], points[triangles[i + 1]], points[triangles[i + 2]] }, Plain);
        }
        // The cliff: a band of rock along each wall, hatched across, taller walls closer.
        foreach (var band in Bands)
        {
            var (a, b) = (Map(band.A), Map(band.B));
            var inward = band.Inward;
            canvas.DrawColoredPolygon(new[] { a - inward * bandPx * 0.4f, b - inward * bandPx * 0.4f, b + inward * bandPx, a + inward * bandPx }, Rock);
            canvas.DrawLine(a - inward * bandPx * 0.4f, b - inward * bandPx * 0.4f, Ink, 1.5f, true);
            var length = a.DistanceTo(b);
            var step = Math.Max(bandPx * (band.HeightM > 2f ? 0.6f : 0.9f), 2f);
            var along = (b - a) / Math.Max(length, 0.001f);
            for (var s = step * 0.5f; s < length && s < step * 400; s += step)
                canvas.DrawLine(a + along * s, a + along * s + inward * bandPx * 0.9f, RockDark, 1f, true);
        }
        // Doors are bays (the cliff opens onto the plain), windows notches (a cut high in the cliff, a spark of sky).
        foreach (var gap in Gaps)
        {
            var (a, b) = (Map(gap.A), Map(gap.B));
            if (gap.Kind == "bay")
            {
                canvas.DrawColoredPolygon(new[] { a - gap.Inward * bandPx * 0.5f, b - gap.Inward * bandPx * 0.5f, b + gap.Inward * bandPx * 1.1f, a + gap.Inward * bandPx * 1.1f }, Plain);
                canvas.DrawLine(a - gap.Inward * bandPx * 0.5f, a + gap.Inward * bandPx * 1.1f, Ink, 1f, true);
                canvas.DrawLine(b - gap.Inward * bandPx * 0.5f, b + gap.Inward * bandPx * 1.1f, Ink, 1f, true);
            }
            else
            {
                var middle = (a + b) * 0.5f;
                canvas.DrawColoredPolygon(new[] { a - gap.Inward * bandPx * 0.4f, b - gap.Inward * bandPx * 0.4f, middle + gap.Inward * bandPx * 0.7f }, Sand);
                canvas.DrawPolyline(new[] { a - gap.Inward * bandPx * 0.4f, middle + gap.Inward * bandPx * 0.7f, b - gap.Inward * bandPx * 0.4f }, Ink, 1f, true);
            }
        }
        // Landforms low to high, so a top lies over its host. Light falls from the upper left: shadows lean lower right.
        var light = new Vector2(1, 1).Normalized();
        foreach (var form in Forms.OrderBy(f => f.Level).ThenBy(f => f.BottomM + f.HeightM))
        {
            var corners = form.Corners.Select(Map).ToArray();
            var lean = light * Math.Min(form.HeightM * scale * 0.18f, bandPx * 1.5f);
            if (form.Foot is { } foot)
                canvas.DrawLine(Map(form.Centre), Map(foot), Rock, Math.Max(Math.Min(form.Corners[0].DistanceTo(form.Corners[1]), form.Corners[1].DistanceTo(form.Corners[2])) * scale, 1.5f), true);
            canvas.DrawColoredPolygon(corners.Select(c => c + lean).ToArray(), new Color(Ink, 0.28f));
            var lift = form.Level > 0 ? -light * bandPx * 0.35f * form.Level : Vector2.Zero;
            var top = corners.Select(c => c + lift).ToArray();
            canvas.DrawColoredPolygon(top, form.Colour);
            canvas.DrawPolyline(new[] { top[0], top[1], top[2], top[3], top[0] }, form.Colour.Darkened(0.45f), 1f, true);
            // A cliff-faced landform shows its flank: a darker edge on the sides away from the light.
            canvas.DrawLine(top[1], top[2], form.Colour.Darkened(0.35f), 2f, true);
            canvas.DrawLine(top[2], top[3], form.Colour.Darkened(0.35f), 2f, true);
        }
    }
}
