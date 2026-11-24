using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Look.Intro;

namespace GarageGame.Native.Ui;

/// <summary>An opening in the land's rim record (RUN-4.md section 4, provisional): <c>notch</c> or <c>bay</c>, where it stands (x, z) and how wide.</summary>
public sealed record RimOpening(string Kind, Vector2 CentreM, float WidthM);

/// <summary>The parts a picture is made of: every stroke names one. A closed list; the counts are read from it.</summary>
public static class LandPart
{
    /// <summary>Sand, to the paper's edge.</summary>
    public const string Paper = "paper";
    /// <summary>The plain: one triangle of a floor.</summary>
    public const string Floor = "floor";
    /// <summary>One wall's band of cliff.</summary>
    public const string Band = "band";
    /// <summary>The ink line along a cliff's crest.</summary>
    public const string Crest = "crest";
    /// <summary>One hachure: a wedge, broad at the cliff's crest and run down to a point toward the plain.</summary>
    public const string Hatch = "hatch";
    /// <summary>A door: the cliff opened onto the plain.</summary>
    public const string Bay = "bay";
    public const string BaySide = "bay_side";
    /// <summary>A window: a cut in the cliff's crest.</summary>
    public const string Notch = "notch";
    public const string NotchEdge = "notch_edge";
    /// <summary>Rock joining a cliff-side thing to its cliff (beyond what was asked; LandExtras.Buttress).</summary>
    public const string Buttress = "buttress";
    public const string Shadow = "shadow";
    /// <summary>A thing's shaded side, away from the page's light.</summary>
    public const string Flank = "flank";
    /// <summary>The top of a thing standing in the open.</summary>
    public const string Landform = "landform";
    /// <summary>The top of a thing that is part of the cliff.</summary>
    public const string CliffSide = "cliff_side";
    /// <summary>The top of a thing resting on another thing.</summary>
    public const string OnTop = "on_top";
    /// <summary>A thing's flat cap, set in from its edge (beyond what was asked; LandExtras.Caps).</summary>
    public const string Cap = "cap";
    public const string Outline = "outline";
    /// <summary>A thing's lit edge, toward the page's light.</summary>
    public const string Lit = "lit";
    /// <summary>The house drawn for a room without shape data.</summary>
    public const string StandIn = "stand_in";
    /// <summary>Never drawn. Named so that the count of it is a count, not a promise.</summary>
    public const string Sea = "sea";

    /// <summary>Whether a part is ground (paper, plain, cliff, openings), which must never be a water colour; a thing keeps its scan colour, blue or not.</summary>
    public static bool IsGround(string part) =>
        part is Paper or Floor or Band or Crest or Hatch or Bay or BaySide or Notch or NotchEdge or Buttress or Shadow;
}

/// <summary>One mark of the picture, in pixels: a filled shape (Width 0) or an open line (its width in pixels), and which part of the land it is.</summary>
public sealed record LandStroke(string Part, Vector2[] Points, Color Colour, float Width = 0f);

/// <summary>
/// What a picture draws, counted from its strokes as game/tests/native/Menu/MenuTest.Boot.cs counts a thumbnail (docs/runs/RUN-4-MENU.md,
/// MENU-5): cliff bands, landforms, cliff-side things, things on tops, bays, notches. Sea is every stroke of sea and every stroke of
/// ground in a water colour: none, for any land.
/// </summary>
public sealed record LandCounts(int Bands, int Landforms, int CliffSide, int Tops, int Bays, int Notches, int Sea)
{
    /// <summary>Every thing drawn, however it stands.</summary>
    public int Things => Landforms + CliffSide + Tops;

    public static LandCounts Of(IEnumerable<LandStroke> strokes)
    {
        int bands = 0, landforms = 0, cliffSide = 0, tops = 0, bays = 0, notches = 0, sea = 0;
        foreach (var stroke in strokes)
        {
            switch (stroke.Part)
            {
                case LandPart.Band: bands++; break;
                case LandPart.Landform: landforms++; break;
                case LandPart.CliffSide: cliffSide++; break;
                case LandPart.OnTop: tops++; break;
                case LandPart.Bay: bays++; break;
                case LandPart.Notch: notches++; break;
            }
            if (stroke.Part == LandPart.Sea || (LandPart.IsGround(stroke.Part) && LandPicture.IsWater(stroke.Colour))) sea++;
        }
        return new LandCounts(bands, landforms, cliffSide, tops, bays, notches, sea);
    }
}

/// <summary>
/// What the picture draws beyond what was asked (docs/runs/RUN-4.md: "marked beyond what was asked, with an off switch where it
/// draws"). Each is on until switched off, for every picture (<c>LandExtras.Current = LandExtras.Current with { Buttress = false }</c>)
/// or for one (the set passed to LandPicture.Strokes). The counts are the same with any of them off.
/// </summary>
/// <param name="Buttress">Rock joins a cliff-side thing to its cliff: its footprint swept to the wall line.</param>
/// <param name="HatchByHeight">A cliff well under the land's tallest is hatched wider apart (hachures stand closer where the ground is steeper).</param>
/// <param name="BrokenHatch">Hachures run long and short by turns, as a pen draws them.</param>
/// <param name="Caps">A thing big enough is a mesa: a flat cap in its scan colour, set in from an edge a shade darker (the worldbook's hard cap bed).</param>
public sealed record LandExtras(bool Buttress = true, bool HatchByHeight = true, bool BrokenHatch = true, bool Caps = true)
{
    public static LandExtras Current { get; set; } = new();
    public static LandExtras None => new(false, false, false, false);
}

/// <summary>
/// A wall seen from above: a cliff band, the strip of the floor within one band of the wall line. Where an end meets another wall at
/// a corner it is that corner, and JoinA or JoinB is the way the other wall's land lies, so the two cliffs are mitred into one.
/// </summary>
public sealed record LandBand(Vector2 A, Vector2 B, Vector2 Inward, float HeightM, string HostId, Vector2? JoinA = null, Vector2? JoinB = null);

/// <summary>
/// One thing, a landform: its true footprint, its height, its colour and how it stands (<c>land</c>, <c>cliff</c> or <c>top</c>).
/// A cliff-side thing knows the nearest point of its cliff (Foot) and the ground between it and the wall line (Root).
/// </summary>
public sealed record LandForm(string Id, Vector2[] Corners, Vector2 Centre, float BottomM, float HeightM, Color Colour, string Stands, int Level, Vector2? Foot, Vector2[]? Root);

/// <summary>A door or window in a band: a bay (a gap in the cliff, open to the plain) or a notch (a cut in the cliff's crest).</summary>
public sealed record LandGap(string Kind, Vector2 A, Vector2 B, Vector2 Inward, string HostId);

/// <summary>
/// The room drawn as land for the menu (docs/runs/RUN-4-MENU.md, "The map"): the walls a band of cliff in hachures, each thing a
/// landform in its scan colour at its true footprint, one by a wall part of the cliff, one resting on another standing on top of it,
/// a door a bay, a window a notch, all shaded from the page's upper left. Sand runs to the paper's edge; no sea is ever drawn, so a
/// land made with the rim (no sea) and the sea islands draw the same way.
///
/// Three steps, each a pure function of the last: the plan (Build: metres, +Z down the page, north up), the strokes (Strokes: the
/// whole picture at a size, as a list of filled shapes and lines), and the replay of those strokes onto a canvas (Draw). A test reads
/// the strokes; what a card counts is what was replayed. The shape data is untrusted and already bounded by IntroSource; a rim record
/// is checked here and a bad one is refused whole.
/// </summary>
public sealed class LandPicture
{
    /// <summary>At most this many things are drawn, the biggest footprints first.</summary>
    public const int MaxThings = 400;
    /// <summary>A cliff band is this share of the land's short side; a thing within one band of a wall is cliff-side.</summary>
    public const float BandShare = 0.04f;
    /// <summary>A rim record holds at most this many openings (RUN-4.md section 4); one with more is refused.</summary>
    public const int MaxRimOpenings = 16;
    /// <summary>A stack is counted this deep and no deeper.</summary>
    public const int MaxLevel = 8;
    /// <summary>A band is never drawn thinner than this, and is hatched only from HatchFromPx up (thinner, hachures smear).</summary>
    public const float MinBandPx = 2.5f, HatchFromPx = 5f;
    /// <summary>A shape smaller than this many square pixels is not drawn (the canvas refuses a shape with no area).</summary>
    public const float MinAreaPx = 0.01f;
    /// <summary>The sand left round the land, in bands (a shadow reaches at most 1.06 bands past a wall).</summary>
    public const float ApronBands = 1.1f;
    /// <summary>How far outside the wall line a cliff's crest stands, in bands.</summary>
    public const float CrestOut = 0.4f;
    /// <summary>A corner's mitre runs at most this many times the band's own reach.</summary>
    public const float MitreLimit = 3f;

    public static readonly Color Sand = new("e6d9b4");
    public static readonly Color Plain = new("d4c28f");
    public static readonly Color Rock = new("8a7355");
    public static readonly Color RockDark = new("4d3f30");
    public static readonly Color Ink = new("3a2e24");
    /// <summary>The way light travels on the page: from the upper left, always (lit from below, hills read as hollows).</summary>
    public static readonly Vector2 Light = new Vector2(1, 1).Normalized();

    public Aabb Bounds { get; init; }
    public IReadOnlyList<Vector2[]> Floors { get; init; } = Array.Empty<Vector2[]>();
    public IReadOnlyList<LandBand> Bands { get; init; } = Array.Empty<LandBand>();
    public IReadOnlyList<LandForm> Forms { get; init; } = Array.Empty<LandForm>();
    public IReadOnlyList<LandGap> Gaps { get; init; } = Array.Empty<LandGap>();
    /// <summary>The band's width in metres: 4% of the land's short side.</summary>
    public float BandM { get; init; }
    /// <summary>True when the openings came from a rim record, not from the room's doors and windows.</summary>
    public bool FromRim { get; init; }
    /// <summary>True when a rim record was given and refused (a token outside notch and bay, a number not finite or out of the land, too many openings): the room's own doors and windows are drawn.</summary>
    public bool RimRefused { get; init; }

    /// <summary>Whether a colour reads as water: a blue or blue-green with some strength. None of the land's own colours does.</summary>
    public static bool IsWater(Color colour) => colour.A > 0.05f && colour.S >= 0.25f && colour.V >= 0.15f && colour.H >= 0.42f && colour.H <= 0.75f;

    /// <summary>How a room's opening is drawn, by its kind (contracts/room-manifest.schema.json): a way through is a bay, a hole in the wall a notch; any other word is not drawn.</summary>
    public static string? GapKind(string openingKind) => openingKind switch
    {
        "door" or "garage_door" or "archway" or "pet_door" or "portal" => LandPart.Bay,
        "window" or "vent" => LandPart.Notch,
        _ => null,
    };

    /// <summary>The plan of a room's shape data. A rim record's openings, when given and sound, stand in for the room's doors and windows.</summary>
    public static LandPicture Build(IntroSource shape, IReadOnlyList<RimOpening>? rim = null)
    {
        var bounds = shape.Bounds;
        var band = BandShare * Math.Max(Math.Min(bounds.Size.X, bounds.Size.Z), 0.001f);
        var floors = shape.Floors.Select(f => f.Points.Select(Flat).ToArray()).ToList();
        var middle = floors.Count > 0 ? floors.SelectMany(f => f).Aggregate(Vector2.Zero, (s, p) => s + p) / floors.Sum(f => f.Length)
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
            var height = wall.Points.Max(p => p.Y) - wall.Points.Min(p => p.Y);
            bands.Add(new LandBand(a, b, Inward(a, b, new Vector2(-along.Y, along.X), floors, band, middle), height, wall.Id));
        }
        bands = Joined(bands, band);
        foreach (var made in bands) hosts.TryAdd(made.HostId, made);

        var forms = FormsOf(shape, bands, band);

        var gaps = new List<LandGap>();
        var fromRim = rim != null && RimIsSound(rim, bounds, band);
        if (fromRim)
        {
            // A rim opening names no wall: it is in the wall its centre is nearest.
            foreach (var opening in rim!)
                if (bands.Count > 0 && Gap(opening.Kind, opening.CentreM, opening.WidthM, bands.MinBy(b => opening.CentreM.DistanceSquaredTo(Closest(b, opening.CentreM)))!) is { } gap)
                    gaps.Add(gap);
        }
        else
        {
            // An opening is in the wall that holds it. One in the ceiling or the floor (a skylight), or in no wall of this room, is no gap in a cliff.
            foreach (var opening in shape.Openings)
                if (GapKind(opening.Kind) is { } kind && hosts.TryGetValue(opening.HostId, out var host) && Gap(kind, Flat(opening.CentreM), opening.SizeM.X, host) is { } gap)
                    gaps.Add(gap);
        }

        return new LandPicture { Bounds = bounds, Floors = floors, Bands = bands, Forms = forms, Gaps = gaps, BandM = band, FromRim = fromRim, RimRefused = rim != null && !fromRim };
    }

    private static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

    /// <summary>
    /// Which side of a wall is land: the side the floor lies on, asked of the floor itself (three places along the wall, near and
    /// further in), so a room that is not convex is right too. With no floor to ask, or floor on both sides, the side toward the room's middle.
    /// </summary>
    private static Vector2 Inward(Vector2 a, Vector2 b, Vector2 normal, List<Vector2[]> floors, float band, Vector2 middle)
    {
        var votes = 0;
        foreach (var along in new[] { 0.25f, 0.5f, 0.75f })
            foreach (var reach in new[] { 0.5f, 2f })
            {
                var at = a.Lerp(b, along);
                if (floors.Any(f => Holds(f, at + normal * band * reach))) votes++;
                if (floors.Any(f => Holds(f, at - normal * band * reach))) votes--;
            }
        if (votes != 0) return votes > 0 ? normal : -normal;
        return normal.Dot(middle - (a + b) * 0.5f) < 0 ? -normal : normal;
    }

    /// <summary>
    /// Walls that meet are joined: an end within one band of another wall's end is moved to where the two wall lines cross (a scan's
    /// walls overlap or fall short by their thickness), and remembers which way that wall's land lies. Walls nearly in one line, or
    /// whose lines cross far from the end, are left as they are.
    /// </summary>
    private static List<LandBand> Joined(List<LandBand> bands, float band)
    {
        var joined = new List<LandBand>(bands.Count);
        for (var i = 0; i < bands.Count; i++)
        {
            var wall = bands[i];
            (Vector2 Point, Vector2? Join) End(Vector2 end)
            {
                LandBand? partner = null;
                var nearest = band;
                for (var j = 0; j < bands.Count; j++)
                {
                    var gap = Math.Min(end.DistanceTo(bands[j].A), end.DistanceTo(bands[j].B));
                    if (j != i && gap <= nearest) (partner, nearest) = (bands[j], gap);
                }
                if (partner == null) return (end, null);
                var (mine, theirs) = (wall.B - wall.A, partner.B - partner.A);
                var cross = mine.Cross(theirs);
                if (MathF.Abs(cross) < 0.2f * mine.Length() * theirs.Length()) return (end, null);
                var corner = wall.A + mine * ((partner.A - wall.A).Cross(theirs) / cross);
                return corner.DistanceTo(end) <= band * 2 ? (corner, partner.Inward) : (end, null);
            }
            var (a, joinA) = End(wall.A);
            var (b, joinB) = End(wall.B);
            joined.Add(a.DistanceTo(b) > band * 0.1f ? wall with { A = a, B = b, JoinA = joinA, JoinB = joinB } : wall);
        }
        return joined;
    }

    /// <summary>
    /// The point a distance from a wall's end on one side of it (its normal), and the same distance from the wall it meets there, on
    /// that wall's same side: the mitre of the two. An end that meets no wall is cut square, and so is one whose mitre would run
    /// more than three times the distance (two walls folded back on each other in a sharp point).
    /// </summary>
    private static Vector2 Mitre(Vector2 end, Vector2 normal, Vector2? join, float distance)
    {
        if (join is not { } other) return end + normal * distance;
        var cross = normal.Cross(other);
        if (MathF.Abs(cross) < 0.2f) return end + normal * distance;
        var reach = new Vector2(other.Y - normal.Y, normal.X - other.X) * (distance / cross);
        return reach.Length() <= MitreLimit * distance ? end + reach : end + normal * distance;
    }

    /// <summary>A rim record is taken whole or not at all: closed tokens, finite numbers inside the land, at most sixteen openings.</summary>
    private static bool RimIsSound(IReadOnlyList<RimOpening> rim, Aabb bounds, float band)
    {
        if (rim.Count > MaxRimOpenings) return false;
        var longSide = Math.Max(bounds.Size.X, bounds.Size.Z);
        foreach (var opening in rim)
        {
            if (opening == null || opening.Kind is not (LandPart.Notch or LandPart.Bay)) return false;
            if (!float.IsFinite(opening.WidthM) || opening.WidthM <= 0 || opening.WidthM > longSide) return false;
            var (x, z) = (opening.CentreM.X, opening.CentreM.Y);
            if (!float.IsFinite(x) || !float.IsFinite(z)) return false;
            if (x < bounds.Position.X - band || x > bounds.End.X + band || z < bounds.Position.Z - band || z > bounds.End.Z + band) return false;
        }
        return true;
    }

    private static List<LandForm> FormsOf(IntroSource shape, List<LandBand> bands, float band)
    {
        var floorY = shape.FloorY;
        // How near counts as touching: 3 cm in a room, and in proportion in a room made ten times over.
        var touch = Math.Max(0.03f, band * 0.1f);
        var boxes = shape.Boxes.OrderByDescending(b => b.SizeM.X * b.SizeM.Z).Take(MaxThings).ToList();
        // The box a thing rests on: the highest other box whose top is at this one's bottom and whose footprint holds its centre.
        // A host's own bottom is lower than its guest's, so no two things can each rest on the other.
        var hosts = new int[boxes.Count];
        for (var i = 0; i < boxes.Count; i++)
        {
            hosts[i] = -1;
            var box = boxes[i];
            if (box.BottomM.Y <= floorY + touch) continue;
            for (var j = 0; j < boxes.Count; j++)
            {
                var under = boxes[j];
                if (j == i || under.BottomM.Y >= box.BottomM.Y) continue;
                if (Math.Abs(under.TopM - box.BottomM.Y) > Math.Max(touch, 0.05f * under.SizeM.Y) || !under.Covers(box.BottomM.X, box.BottomM.Z)) continue;
                if (hosts[i] < 0 || under.TopM > boxes[hosts[i]].TopM) hosts[i] = j;
            }
        }
        // A thing's level is how many things it stands on (a stack of three is levels 0, 1, 2), lowest first so each host is known.
        var levels = new int[boxes.Count];
        foreach (var i in Enumerable.Range(0, boxes.Count).OrderBy(i => boxes[i].BottomM.Y))
            levels[i] = hosts[i] < 0 ? 0 : Math.Min(levels[hosts[i]] + 1, MaxLevel);

        var forms = new List<LandForm>(boxes.Count);
        for (var i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            var half = new Vector2(box.SizeM.X, box.SizeM.Z) * 0.5f;
            var (sin, cos) = (MathF.Sin(box.YawRad), MathF.Cos(box.YawRad));
            // The box's own axes turned by its heading, as IntroBox.Covers reads them.
            Vector2 Corner(float along, float across) => new(box.BottomM.X + along * cos + across * sin, box.BottomM.Z - along * sin + across * cos);
            var corners = new[] { Corner(-half.X, -half.Y), Corner(half.X, -half.Y), Corner(half.X, half.Y), Corner(-half.X, half.Y) };
            var centre = new Vector2(box.BottomM.X, box.BottomM.Z);
            var stands = levels[i] > 0 ? "top" : "land";
            Vector2? foot = null;
            Vector2[]? root = null;
            if (levels[i] == 0)
            {
                LandBand? nearest = null;
                var nearestGap = float.MaxValue;
                foreach (var candidate in bands)
                    foreach (var point in corners.Append(centre))
                    {
                        var gap = point.DistanceTo(Closest(candidate, point));
                        if (gap < nearestGap) (nearest, nearestGap) = (candidate, gap);
                    }
                if (nearest is { } cliff && nearestGap <= band)
                {
                    stands = "cliff";
                    foot = Closest(cliff, centre);
                    root = Hull(corners.Concat(corners.Select(c => Closest(cliff, c))).ToArray());
                }
            }
            forms.Add(new LandForm(box.Id, corners, centre, box.BottomM.Y - floorY, box.SizeM.Y, box.Colour, stands, levels[i], foot, root));
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

    /// <summary>A gap in a band: as wide as the opening (never wider than the wall), slid along the wall until all of it is in the wall.</summary>
    private static LandGap? Gap(string kind, Vector2 centre, float width, LandBand band)
    {
        var length = band.A.DistanceTo(band.B);
        if (!(width > 0) || !(length > 0)) return null;
        var along = (band.B - band.A) / length;
        var half = Math.Min(width, length) * 0.5f;
        var middle = band.A + along * Math.Clamp((centre - band.A).Dot(along), half, length - half);
        return new LandGap(kind, middle - along * half, middle + along * half, band.Inward, band.HostId);
    }

    // ---- the picture at a size ----

    /// <summary>
    /// Where the plan sits in a picture of a size: the page point of the land's north-west corner and the pixels a metre. The land is
    /// centred with an apron of sand round it, wide enough for the cliff's crest, a bay's mouth and the shadow of a thing at a wall,
    /// so that all of a sound room stays on the paper, inside the margin.
    /// </summary>
    public (Vector2 Origin, float Scale) Frame(Vector2 size, float margin = 8f)
    {
        margin = Math.Clamp(margin, 0f, Math.Max(Math.Min(size.X, size.Y) * 0.25f, 0f));
        var apron = BandM * ApronBands;
        var span = new Vector2(Math.Max(Bounds.Size.X, 0.001f), Math.Max(Bounds.Size.Z, 0.001f)) + Vector2.One * apron * 2;
        var scale = Math.Max(Math.Min((size.X - 2 * margin) / span.X, (size.Y - 2 * margin) / span.Y), 0f);
        return ((size - span * scale) * 0.5f + Vector2.One * apron * scale, scale);
    }

    /// <summary>A point of the plan (x, z in metres) on a picture of a size: north up, the land centred.</summary>
    public Vector2 ToPage(Vector2 metres, Vector2 size, float margin = 8f)
    {
        var (origin, scale) = Frame(size, margin);
        return origin + (metres - new Vector2(Bounds.Position.X, Bounds.Position.Z)) * scale;
    }

    /// <summary>What the picture draws at a size, counted from its strokes.</summary>
    public LandCounts Count(Vector2 size, float margin = 8f) => LandCounts.Of(Strokes(size, margin));

    /// <summary>
    /// The whole picture at a size, in drawing order: sand, the plain, the cliffs and their hachures, the openings, then each thing low
    /// to high (its join to the cliff, its shadow, its shaded sides, its top and cap, its outline, its lit edges). Nothing else is drawn.
    /// </summary>
    public IReadOnlyList<LandStroke> Strokes(Vector2 size, float margin = 8f, LandExtras? extras = null)
    {
        extras ??= LandExtras.Current;
        var strokes = new List<LandStroke>();
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X <= 0 || size.Y <= 0) return strokes;
        // A stroke wholly off the paper is left out (shape data is untrusted: a thing may lie far outside its room's bounds).
        bool OnPaper(Vector2[] points) => points.Min(p => p.X) < size.X && points.Max(p => p.X) > 0 && points.Min(p => p.Y) < size.Y && points.Max(p => p.Y) > 0;
        void Fill(string part, Color colour, params Vector2[] points)
        {
            if (points.Length >= 3 && points.All(Finite) && MathF.Abs(Area(points)) >= MinAreaPx && OnPaper(points)) strokes.Add(new LandStroke(part, points, colour));
        }
        void Line(string part, Color colour, float width, params Vector2[] points)
        {
            if (points.Length < 2 || !points.All(Finite) || !OnPaper(points)) return;
            var length = 0f;
            for (var i = 0; i + 1 < points.Length; i++) length += points[i].DistanceTo(points[i + 1]);
            if (length >= 0.1f) strokes.Add(new LandStroke(part, points, colour, width));
        }

        Fill(LandPart.Paper, Sand, Vector2.Zero, new Vector2(size.X, 0), size, new Vector2(0, size.Y));
        var (origin, scale) = Frame(size, margin);
        var corner = new Vector2(Bounds.Position.X, Bounds.Position.Z);
        Vector2 Map(Vector2 p) => origin + (p - corner) * scale;
        var bandPx = Math.Max(BandM * scale, MinBandPx);

        foreach (var floor in Floors)
            foreach (var triangle in Triangles(floor.Select(Map).ToArray()))
                Fill(LandPart.Floor, Plain, triangle);

        // The cliff: a band of rock along each wall, from its crest a little outside the wall line to its foot one band inside,
        // mitred where two walls meet. Hachures are wedges, broad at the crest and run down to a point toward the plain; the corner
        // itself is left to the two cliffs' rock.
        var tallest = Bands.Count > 0 ? Bands.Max(b => b.HeightM) : 0f;
        foreach (var band in Bands)
        {
            var (a, b) = (Map(band.A), Map(band.B));
            var length = a.DistanceTo(b);
            if (length < 0.01f) continue;
            var along = (b - a) / length;
            Vector2? Outward(Vector2? join) => join is { } other ? -other : null;
            var (crestA, crestB) = (Mitre(a, -band.Inward, Outward(band.JoinA), bandPx * CrestOut), Mitre(b, -band.Inward, Outward(band.JoinB), bandPx * CrestOut));
            Fill(LandPart.Band, Rock, crestA, crestB, Mitre(b, band.Inward, band.JoinB, bandPx), Mitre(a, band.Inward, band.JoinA, bandPx));
            Line(LandPart.Crest, Ink, 1.5f, crestA, crestB);
            if (bandPx < HatchFromPx) continue;
            var low = extras.HatchByHeight && band.HeightM < tallest * 0.75f;
            var step = Math.Max(bandPx * (low ? 1.1f : 0.6f), 3f);
            var (from, to) = (band.JoinA != null ? bandPx : 0f, length - (band.JoinB != null ? bandPx : 0f));
            var crest = -band.Inward * bandPx * CrestOut;
            var count = 0;
            for (var s = from + step * 0.5f; s < to && count < 400; s += step, count++)
            {
                var at = a + along * s;
                Fill(LandPart.Hatch, RockDark, at + crest - along * step * 0.3f, at + crest + along * step * 0.3f, at + band.Inward * bandPx * (extras.BrokenHatch && count % 2 == 1 ? 0.5f : 0.9f));
            }
        }
        // A way through is a bay (the cliff opens and the plain runs out through it), a hole in the wall a notch (a cut in the crest).
        foreach (var gap in Gaps)
        {
            var (a, b) = (Map(gap.A), Map(gap.B));
            var (outer, inner) = (-gap.Inward * bandPx * (CrestOut + 0.1f), gap.Inward * bandPx * 1.1f);
            if (gap.Kind == LandPart.Bay)
            {
                Fill(LandPart.Bay, Plain, a + outer, b + outer, b + inner, a + inner);
                Line(LandPart.BaySide, Ink, 1f, a + outer, a + inner);
                Line(LandPart.BaySide, Ink, 1f, b + outer, b + inner);
            }
            else if (gap.Kind == LandPart.Notch)
            {
                var point = (a + b) * 0.5f + gap.Inward * bandPx * 0.7f;
                Fill(LandPart.Notch, Sand, a + outer, b + outer, point);
                Line(LandPart.NotchEdge, Ink, 1f, a + outer, point, b + outer);
            }
        }
        // Landforms low to high, so a thing on a top lies over its host and throws its shadow on it. Each keeps its true footprint.
        // A side is shaded when it faces away from the page's light, whichever way the thing is turned.
        foreach (var form in Forms.OrderBy(f => f.Level).ThenBy(f => f.BottomM + f.HeightM))
        {
            var top = form.Corners.Select(Map).ToArray();
            var centre = Map(form.Centre);
            var rise = Math.Min(form.HeightM * scale * 0.18f, bandPx * 1.5f);
            if (extras.Buttress && form.Root is { } root) Fill(LandPart.Buttress, Rock, root.Select(Map).ToArray());
            Fill(LandPart.Shadow, new Color(Ink, 0.28f), top.Select(c => c + Light * rise).ToArray());
            var facing = new float[top.Length];
            for (var i = 0; i < top.Length; i++)
            {
                var (p, q) = (top[i], top[(i + 1) % top.Length]);
                var normal = new Vector2(q.Y - p.Y, p.X - q.X).Normalized();
                if (normal.Dot((p + q) * 0.5f - centre) < 0) normal = -normal;
                facing[i] = normal.Dot(Light);
                if (facing[i] > 0.05f) Fill(LandPart.Flank, form.Colour.Darkened(0.35f), p, q, q + Light * rise * 0.5f, p + Light * rise * 0.5f);
            }
            // A thing big enough is drawn as a mesa: its flat cap in its scan colour, set in from an edge a shade darker.
            var (wide, deep) = (top[0].DistanceTo(top[1]), top[1].DistanceTo(top[2]));
            var inset = Math.Min(rise * 0.5f, Math.Min(wide, deep) * 0.22f);
            var capped = extras.Caps && inset >= 1f;
            Fill(form.Stands switch { "cliff" => LandPart.CliffSide, "top" => LandPart.OnTop, _ => LandPart.Landform }, capped ? form.Colour.Darkened(0.14f) : form.Colour, top);
            if (capped)
            {
                var (across, down) = ((top[1] - top[0]) / wide * inset, (top[2] - top[1]) / deep * inset);
                Fill(LandPart.Cap, form.Colour, top[0] + across + down, top[1] - across + down, top[2] - across - down, top[3] + across - down);
            }
            Line(LandPart.Outline, form.Colour.Darkened(0.45f), 1f, top[0], top[1], top[2], top[3], top[0]);
            for (var i = 0; i < top.Length; i++)
                if (facing[i] < -0.05f) Line(LandPart.Lit, form.Colour.Lightened(0.35f), 1f, top[i], top[(i + 1) % top.Length]);
        }
        return strokes;
    }

    /// <summary>The picture of a room that has no shape data: a house in outline, on the same sand.</summary>
    public static IReadOnlyList<LandStroke> StandIn(Vector2 size)
    {
        var strokes = new List<LandStroke>();
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X < 1 || size.Y < 1) return strokes;
        static Vector2[] Box(Rect2 r) => new[] { r.Position, new Vector2(r.End.X, r.Position.Y), r.End, new Vector2(r.Position.X, r.End.Y) };
        strokes.Add(new LandStroke(LandPart.Paper, Box(new Rect2(Vector2.Zero, size)), Sand));
        var centre = size * 0.5f;
        var half = new Vector2(Math.Min(size.X, size.Y * 1.6f) * 0.22f, size.Y * 0.2f);
        var body = new Rect2(centre - half + new Vector2(0, size.Y * 0.08f), half * 2);
        var door = new Vector2(Math.Min(9f, half.X * 0.4f), Math.Min(26f, half.Y * 1.3f));
        strokes.Add(new LandStroke(LandPart.StandIn, Box(body), new Color("3b6a63")));
        strokes.Add(new LandStroke(LandPart.StandIn, Box(body).Append(body.Position).ToArray(), Ink, 2f));
        strokes.Add(new LandStroke(LandPart.StandIn, new[] { new Vector2(body.Position.X - 8, body.Position.Y), new Vector2(centre.X, body.Position.Y - size.Y * 0.22f), new Vector2(body.End.X + 8, body.Position.Y) }, new Color("c98562")));
        strokes.Add(new LandStroke(LandPart.StandIn, Box(new Rect2(centre.X - door.X, body.End.Y - door.Y, door.X * 2, door.Y)), new Color("1b2f2c")));
        return strokes;
    }

    /// <summary>Draw the picture into a canvas item's current draw call. Returns what it drew.</summary>
    public IReadOnlyList<LandStroke> Draw(CanvasItem canvas, Vector2 size, float margin = 8f) => Replay(canvas, Strokes(size, margin));

    /// <summary>Send strokes to a canvas, in order. Returns the strokes it sent, one by one as each was drawn: what a card counts.</summary>
    public static IReadOnlyList<LandStroke> Replay(CanvasItem canvas, IEnumerable<LandStroke> strokes)
    {
        var drawn = new List<LandStroke>();
        foreach (var stroke in strokes)
        {
            if (stroke.Width <= 0) canvas.DrawColoredPolygon(stroke.Points, stroke.Colour);
            else canvas.DrawPolyline(stroke.Points, stroke.Colour, stroke.Width, true);
            drawn.Add(stroke);
        }
        return drawn;
    }

    // ---- plane geometry, kept here so that the plan and the strokes need no engine ----

    private static bool Finite(Vector2 p) => float.IsFinite(p.X) && float.IsFinite(p.Y);

    /// <summary>Twice-signed area by the shoelace rule, halved: positive when the points turn one way, negative the other.</summary>
    public static float Area(IReadOnlyList<Vector2> points)
    {
        var sum = 0f;
        for (var i = 0; i < points.Count; i++) sum += points[i].Cross(points[(i + 1) % points.Count]);
        return sum * 0.5f;
    }

    /// <summary>Whether a polygon holds a point (even-odd rule, so any simple polygon).</summary>
    public static bool Holds(IReadOnlyList<Vector2> polygon, Vector2 p)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>The convex hull of points (monotone chain), turning one way.</summary>
    public static Vector2[] Hull(IReadOnlyList<Vector2> points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (sorted.Count < 3) return sorted.ToArray();
        var hull = new List<Vector2>();
        for (var pass = 0; pass < 2; pass++)
        {
            var start = hull.Count;
            foreach (var p in sorted)
            {
                while (hull.Count >= start + 2 && (hull[^1] - hull[^2]).Cross(p - hull[^1]) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
            sorted.Reverse();
        }
        return hull.ToArray();
    }

    /// <summary>A simple polygon cut into triangles (ears clipped one by one). A shape that will not cut (all its points in a line) gives its hull's, which may be none.</summary>
    public static List<Vector2[]> Triangles(Vector2[] polygon)
    {
        var result = new List<Vector2[]>();
        if (polygon.Length < 3 || !polygon.All(Finite)) return result;
        var order = Enumerable.Range(0, polygon.Length).ToList();
        if (Area(polygon) < 0) order.Reverse();
        var clipped = true;
        while (order.Count > 3 && clipped)
        {
            clipped = false;
            for (var i = 0; i < order.Count && !clipped; i++)
            {
                var (ia, ib, ic) = (order[(i + order.Count - 1) % order.Count], order[i], order[(i + 1) % order.Count]);
                var (a, b, c) = (polygon[ia], polygon[ib], polygon[ic]);
                var turn = (b - a).Cross(c - b);
                if (turn <= 1e-9f) continue;
                // An ear holds no other point, inside it or on its edge (a point on the cut would be cut across); a point that
                // repeats one of its corners is no obstacle.
                var slack = -1e-5f * Math.Max(turn, 1f);
                var ear = true;
                foreach (var other in order)
                {
                    if (other == ia || other == ib || other == ic) continue;
                    var p = polygon[other];
                    if (p == a || p == b || p == c) continue;
                    if ((b - a).Cross(p - a) >= slack && (c - b).Cross(p - b) >= slack && (a - c).Cross(p - c) >= slack) { ear = false; break; }
                }
                if (!ear) continue;
                result.Add(new[] { a, b, c });
                order.RemoveAt(i);
                clipped = true;
            }
        }
        if (order.Count == 3)
        {
            result.Add(order.Select(i => polygon[i]).ToArray());
            return result;
        }
        result.Clear();
        var hull = Hull(polygon);
        for (var i = 1; i + 1 < hull.Length; i++) result.Add(new[] { hull[0], hull[i], hull[i + 1] });
        return result;
    }
}
