using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GarageGame.Native.Look.Intro;
using GarageGame.Native.Ui;

namespace GarageGame.Tests;

/// <summary>
/// MENU-5, the land's picture: the room drawn as land, counted as the menu test counts a thumbnail (cliff bands, landforms, cliff-side
/// things, things on tops, bays, notches, no sea). Nothing here reads a count the plan wrote down: every count is of strokes, the
/// card's of the strokes it sent to its canvas, and the strokes are painted into pixels by this test's own painter (no GPU) and looked
/// at where the land must show: sand at the paper's edge, rock on a wall, the plain through a door, a cut at a window, each thing in
/// its colour, the shaded side away from the page's light.
/// Pictures for a person's eye: run with <c>-- --land-pictures=&lt;folder&gt;</c> (and <c>--land-room=&lt;a stand-in's room.json&gt;</c>).
/// </summary>
public partial class MenuTest
{
    /// <summary>
    /// A 10 by 8 m room, walls 3 m. Ways through: a door, a pet door and a door at the wall's very end in the south wall, a garage
    /// door in the east wall, an archway in the north wall, a portal in the west wall. Holes: a window in the north wall, a vent in the
    /// west wall. Not gaps in a cliff: a skylight, an opening of an unknown kind, a door in a wall the room does not have.
    /// Things: a table, a box on the table, a cup on the box; a cabinet against the north wall; a shelf 31 cm from the east wall (a
    /// band is 32 cm) and a stool 33 cm from the west wall; a crate in the open, turned 45 degrees.
    /// </summary>
    private const string LandRoom = """
        {"bounds":{"min_m":[-5,0,-4],"max_m":[5,3,4]},
         "shell":{"parts":[
           {"id":"shell:floor","role":"floor","geometry":{"kind":"polygon","points_m":[[-5,0,-4],[5,0,-4],[5,0,4],[-5,0,4]],"thickness_m":0.1}},
           {"id":"shell:ceiling","role":"ceiling","geometry":{"kind":"polygon","points_m":[[-5,3,-4],[5,3,-4],[5,3,4],[-5,3,4]],"thickness_m":0.1}},
           {"id":"shell:wall_n","role":"wall","geometry":{"kind":"polygon","points_m":[[-5,0,-4],[5,0,-4],[5,3,-4],[-5,3,-4]],"thickness_m":0.1}},
           {"id":"shell:wall_s","role":"wall","geometry":{"kind":"polygon","points_m":[[-5,0,4],[5,0,4],[5,3,4],[-5,3,4]],"thickness_m":0.1}},
           {"id":"shell:wall_e","role":"wall","geometry":{"kind":"polygon","points_m":[[5,0,-4],[5,0,4],[5,3,4],[5,3,-4]],"thickness_m":0.1}},
           {"id":"shell:wall_w","role":"wall","geometry":{"kind":"polygon","points_m":[[-5,0,-4],[-5,0,4],[-5,3,4],[-5,3,-4]],"thickness_m":0.1}}],
          "openings":[
           {"id":"door","kind":"door","host_part_id":"shell:wall_s","center_m":[2,1,4],"size_m":[1,2]},
           {"id":"pet","kind":"pet_door","host_part_id":"shell:wall_s","center_m":[-3,0.2,4],"size_m":[0.4,0.4]},
           {"id":"end","kind":"door","host_part_id":"shell:wall_s","center_m":[4.9,1,4],"size_m":[1,2]},
           {"id":"garage","kind":"garage_door","host_part_id":"shell:wall_e","center_m":[5,1.1,0],"size_m":[3,2.2]},
           {"id":"arch","kind":"archway","host_part_id":"shell:wall_n","center_m":[4,1,-4],"size_m":[0.8,2]},
           {"id":"way","kind":"portal","host_part_id":"shell:wall_w","center_m":[-5,1,2],"size_m":[0.9,2]},
           {"id":"win","kind":"window","host_part_id":"shell:wall_n","center_m":[-2,1.6,-4],"size_m":[1.2,1]},
           {"id":"vent","kind":"vent","host_part_id":"shell:wall_w","center_m":[-5,2.5,-2],"size_m":[0.5,0.3]},
           {"id":"sky","kind":"window","host_part_id":"shell:ceiling","center_m":[0.5,3,0.2],"size_m":[1,1]},
           {"id":"odd","kind":"hatchway","host_part_id":"shell:wall_n","center_m":[0,1,-4],"size_m":[1,1]},
           {"id":"lost","kind":"door","host_part_id":"shell:wall_gone","center_m":[0,1,4],"size_m":[1,2]}]},
         "objects":[{"id":"table","position_m":[0,0,0],"size_m":[2,1,1.2],"yaw_deg":0,"colours":[{"hex":"#aa5533"}]},
                    {"id":"box","position_m":[0.2,1,0],"size_m":[0.4,0.4,0.4],"yaw_deg":20,"colours":[{"hex":"#3366aa"}]},
                    {"id":"cup","position_m":[0.2,1.4,0],"size_m":[0.1,0.1,0.1],"yaw_deg":0,"colours":[{"hex":"#ddddcc"}]},
                    {"id":"cabinet","position_m":[2,0,-3.7],"size_m":[1,2,0.5],"yaw_deg":0,"colours":[{"hex":"#665544"}]},
                    {"id":"shelf","position_m":[4.49,0,3],"size_m":[0.4,1.2,0.8],"yaw_deg":0,"colours":[{"hex":"#447755"}]},
                    {"id":"stool","position_m":[-4.47,0,0.5],"size_m":[0.4,0.5,0.4],"yaw_deg":0,"colours":[{"hex":"#996633"}]},
                    {"id":"crate","position_m":[-2,0,2],"size_m":[0.6,0.6,0.6],"yaw_deg":45}]}
        """;

    private static readonly LandCounts LandRoomCounts = new(Bands: 4, Landforms: 3, CliffSide: 2, Tops: 2, Bays: 6, Notches: 2, Sea: 0);
    private static readonly Vector2 LandPage = new(520, 400), LandCard = new(246, 64);

    private static IntroSource LandSourceOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return IntroSource.Parse(document.RootElement);
    }

    /// <summary>Numbers as JSON writes them, whatever the machine's language.</summary>
    private static string LandList(params float[] values) => "[" + string.Join(",", values.Select(v => v.ToString(CultureInfo.InvariantCulture))) + "]";

    private static string LandWall(string id, float x0, float z0, float x1, float z1, float height = 3) =>
        "{\"id\":\"shell:" + id + "\",\"role\":\"wall\",\"geometry\":{\"kind\":\"polygon\",\"points_m\":[" +
        string.Join(",", LandList(x0, 0, z0), LandList(x1, 0, z1), LandList(x1, height, z1), LandList(x0, height, z0)) + "],\"thickness_m\":0.1}}";

    private static string LandFloor(params float[] xz) =>
        "{\"id\":\"shell:floor\",\"role\":\"floor\",\"geometry\":{\"kind\":\"polygon\",\"points_m\":[" +
        string.Join(",", Enumerable.Range(0, xz.Length / 2).Select(i => LandList(xz[2 * i], 0, xz[2 * i + 1]))) + "],\"thickness_m\":0.1}}";

    private static string LandBox(string id, float x, float y, float z, float sx, float sy, float sz, float yaw, string hex = "#aa5533") =>
        "{\"id\":\"" + id + "\",\"position_m\":" + LandList(x, y, z) + ",\"size_m\":" + LandList(sx, sy, sz) + ",\"yaw_deg\":" + yaw.ToString(CultureInfo.InvariantCulture) + ",\"colours\":[{\"hex\":\"" + hex + "\"}]}";

    private static string LandShape(string bounds, IEnumerable<string> parts, IEnumerable<string> objects, string openings = "") =>
        "{\"bounds\":" + bounds + ",\"shell\":{\"parts\":[" + string.Join(",", parts) + "],\"openings\":[" + openings + "]},\"objects\":[" + string.Join(",", objects) + "]}";

    private const string LandBounds = "{\"min_m\":[-5,0,-4],\"max_m\":[5,3,4]}";
    private static string[] LandFourWalls(float south = 3) => new[]
    {
        LandFloor(-5, -4, 5, -4, 5, 4, -5, 4), LandWall("wall_n", -5, -4, 5, -4), LandWall("wall_s", -5, 4, 5, 4, south), LandWall("wall_e", 5, -4, 5, 4), LandWall("wall_w", -5, -4, -5, 4),
    };

    // ---- this test's own painter and its measures (none of it is the picture's code) ----

    private static bool LandInside(IReadOnlyList<Vector2> polygon, Vector2 p)
    {
        var crossings = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var (a, b) = (polygon[i], polygon[(i + 1) % polygon.Count]);
            if ((a.Y <= p.Y) == (b.Y <= p.Y)) continue;
            if (a.X + (p.Y - a.Y) / (b.Y - a.Y) * (b.X - a.X) > p.X) crossings++;
        }
        return crossings % 2 == 1;
    }

    private static float LandNear(Vector2 a, Vector2 b, Vector2 p)
    {
        var line = b - a;
        var t = line.LengthSquared() <= 0 ? 0 : Math.Clamp((p - a).Dot(line) / line.LengthSquared(), 0f, 1f);
        return p.DistanceTo(a + line * t);
    }

    /// <summary>The strokes painted in order into pixels: a filled shape covers the pixel centres it holds, a line those within half its width (at least half a pixel); a see-through colour is laid over what is there.</summary>
    private static Color[,] LandPaint(IReadOnlyList<LandStroke> strokes, Vector2 size)
    {
        int width = (int)size.X, height = (int)size.Y;
        var pixels = new Color[width, height];
        foreach (var stroke in strokes)
        {
            var points = stroke.Points;
            var reach = stroke.Width > 0 ? Math.Max(stroke.Width, 1f) * 0.5f : 0f;
            int x0 = Math.Max((int)MathF.Floor(points.Min(p => p.X) - reach), 0), x1 = Math.Min((int)MathF.Ceiling(points.Max(p => p.X) + reach), width - 1);
            int y0 = Math.Max((int)MathF.Floor(points.Min(p => p.Y) - reach), 0), y1 = Math.Min((int)MathF.Ceiling(points.Max(p => p.Y) + reach), height - 1);
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    var hit = stroke.Width <= 0 && LandInside(points, p);
                    for (var i = 0; stroke.Width > 0 && !hit && i + 1 < points.Length; i++) hit = LandNear(points[i], points[i + 1], p) <= reach;
                    if (!hit) continue;
                    var (under, over) = (pixels[x, y], stroke.Colour);
                    pixels[x, y] = new Color(Mathf.Lerp(under.R, over.R, over.A), Mathf.Lerp(under.G, over.G, over.A), Mathf.Lerp(under.B, over.B, over.A), 1f);
                }
        }
        return pixels;
    }

    private static Color LandAt(Color[,] pixels, Vector2 at) =>
        pixels[Math.Clamp((int)MathF.Floor(at.X), 0, pixels.GetLength(0) - 1), Math.Clamp((int)MathF.Floor(at.Y), 0, pixels.GetLength(1) - 1)];

    /// <summary>Whether a canvas takes every stroke: numbers that are numbers, on the paper (a pixel over at most), filled shapes with area, lines with two points.</summary>
    private static bool LandSound(IReadOnlyList<LandStroke> strokes, Vector2 size) =>
        strokes.All(s => s.Points.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y) && p.X >= -1 && p.Y >= -1 && p.X <= size.X + 1 && p.Y <= size.Y + 1)) &&
        strokes.Where(s => s.Width <= 0).All(s => s.Points.Length >= 3 && MathF.Abs(LandPicture.Area(s.Points)) >= LandPicture.MinAreaPx) && strokes.Where(s => s.Width > 0).All(s => s.Points.Length >= 2);

    private static bool LandSame(Color a, Color b) => MathF.Abs(a.R - b.R) < 0.02f && MathF.Abs(a.G - b.G) < 0.02f && MathF.Abs(a.B - b.B) < 0.02f;
    private static bool LandRocky(Color c) => LandSame(c, LandPicture.Rock) || LandSame(c, LandPicture.RockDark);
    private static Vector2 LandMiddle(IReadOnlyList<Vector2> points) => points.Aggregate(Vector2.Zero, (s, p) => s + p) / points.Count;
    private static bool LandSpans(LandGap gap, float x0, float z0, float x1, float z1)
    {
        var (a, b) = (new Vector2(x0, z0), new Vector2(x1, z1));
        return (gap.A.DistanceTo(a) < 1e-3f && gap.B.DistanceTo(b) < 1e-3f) || (gap.A.DistanceTo(b) < 1e-3f && gap.B.DistanceTo(a) < 1e-3f);
    }

    /// <summary>Each thing's strokes, in drawing order: a thing starts at its join to the cliff or its shadow and ends before the next thing's.</summary>
    private static List<List<LandStroke>> LandThings(IReadOnlyList<LandStroke> strokes)
    {
        var things = new List<List<LandStroke>>();
        var open = false;
        foreach (var stroke in strokes)
        {
            if (stroke.Part == LandPart.Buttress || (stroke.Part == LandPart.Shadow && !open))
            {
                things.Add(new List<LandStroke>());
                open = stroke.Part == LandPart.Buttress;
            }
            else if (stroke.Part == LandPart.Shadow) open = false;
            if (things.Count > 0 && stroke.Part is LandPart.Buttress or LandPart.Shadow or LandPart.Flank or LandPart.Landform or LandPart.CliffSide or LandPart.OnTop or LandPart.Cap or LandPart.Outline or LandPart.Lit)
                things[^1].Add(stroke);
        }
        return things;
    }

    private string? _landPictures;

    private void LandSave(Color[,] pixels, string name)
    {
        if (_landPictures == null) return;
        Directory.CreateDirectory(_landPictures);
        var image = Image.CreateEmpty(pixels.GetLength(0), pixels.GetLength(1), false, Image.Format.Rgba8);
        for (var y = 0; y < pixels.GetLength(1); y++)
            for (var x = 0; x < pixels.GetLength(0); x++)
                image.SetPixel(x, y, pixels[x, y]);
        image.SavePng(Path.Combine(_landPictures, name + ".png"));
    }

    // ---- the checks ----

    private async Task TestLandPicture()
    {
        _landPictures = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--land-pictures=", StringComparison.Ordinal))?["--land-pictures=".Length..];
        var source = LandSourceOf(LandRoom);
        TestLandPlan(source);
        TestLandStrokes(source);
        TestLandShading();
        TestLandOddRooms();
        TestLandRim(source);
        TestLandExtras(source);
        await TestLandCard(source);
        TestLandNames();
        if (_landPictures != null && OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--land-room=", StringComparison.Ordinal))?["--land-room=".Length..] is { Length: > 0 } room && File.Exists(room))
        {
            // A stand-in's manifest, for a person's eye only: no check reads it.
            var shape = IntroSource.FromManifest(File.ReadAllBytes(room));
            if (shape != null)
                foreach (var (size, name) in new[] { (LandPage, "room_page"), (LandCard, "room_card"), (new Vector2(1040, 800), "room_large") })
                {
                    var land = LandPicture.Build(shape);
                    LandSave(LandPaint(land.Strokes(size), size), name);
                    GD.Print($"LAND_PICTURE {name} {size}: {land.Count(size)}; {land.Forms.Count} things, {land.Gaps.Count} openings, band {land.BandM:0.###} m");
                }
        }
    }

    private void TestLandPlan(IntroSource source)
    {
        var land = LandPicture.Build(source);
        string Stands(string id) => land.Forms.FirstOrDefault(f => f.Id == id) is { } form ? $"{form.Stands}@{form.Level}" : "missing";
        Check(land.Forms.Count == 7 && Stands("table") == "land@0" && Stands("crate") == "land@0" && Stands("box") == "top@1" && Stands("cup") == "top@2" && Stands("cabinet") == "cliff@0",
            $"each thing stands where it is: the table and the crate in the open, the box on the table, the cup on the box, the cabinet against the wall part of the cliff ({string.Join(", ", land.Forms.Select(f => f.Id + " " + Stands(f.Id)))})");
        Check(MathF.Abs(land.BandM - 0.32f) < 1e-4f && Stands("shelf") == "cliff@0" && Stands("stool") == "land@0",
            "a cliff band is 4% of the land's short side (32 cm here), and one band is the line: a shelf 31 cm from its wall is cliff, a stool 33 cm from its wall is not");
        var cabinet = land.Forms.First(f => f.Id == "cabinet");
        Check(cabinet.Foot is { } foot && foot.DistanceTo(new Vector2(2, -4)) < 1e-4f && cabinet.Root is { Length: >= 4 } root && root.All(p => p.Y is >= -4.0001f and <= -3.4499f && p.X is >= 1.4999f and <= 2.5001f),
            "a cliff-side thing knows its cliff: the cabinet's foot is on the north wall line, and the ground between is its footprint swept to the wall");
        var floor = land.Floors[0];
        Check(land.Bands.Count == 4 && land.Bands.All(b => LandInside(floor, (b.A + b.B) * 0.5f + b.Inward * land.BandM * 0.5f)) && land.Bands.All(b => MathF.Abs(b.HeightM - 3) < 1e-4f),
            "four walls are four cliff bands, each leaning in onto the floor, each as tall as its wall");

        LandGap? In(string host, string kind, float x0, float z0, float x1, float z1) => land.Gaps.FirstOrDefault(g => g.HostId == host && g.Kind == kind && LandSpans(g, x0, z0, x1, z1));
        var ways = new[] { In("shell:wall_s", "bay", 1.5f, 4, 2.5f, 4), In("shell:wall_s", "bay", -3.2f, 4, -2.8f, 4), In("shell:wall_e", "bay", 5, -1.5f, 5, 1.5f), In("shell:wall_n", "bay", 3.6f, -4, 4.4f, -4), In("shell:wall_w", "bay", -5, 1.55f, -5, 2.45f) };
        Check(ways.All(g => g != null) && land.Gaps.Count(g => g.Kind == "bay") == 6,
            "a way through is a bay, in its own wall, where it is and as wide as it is: the door, the pet door, the garage door, the archway and the portal");
        var holes = new[] { In("shell:wall_n", "notch", -2.6f, -4, -1.4f, -4), In("shell:wall_w", "notch", -5, -2.25f, -5, -1.75f) };
        Check(holes.All(g => g != null) && land.Gaps.Count(g => g.Kind == "notch") == 2, "a hole in a wall is a notch, where it is and as wide as it is: the window and the vent");
        Check(In("shell:wall_s", "bay", 4, 4, 5, 4) != null, "a door at the very end of its wall is slid along until all of it is in the wall");
        Check(land.Gaps.Count == 8 && LandPicture.GapKind("hatchway") == null && land.Gaps.All(g => g.HostId.StartsWith("shell:wall_", StringComparison.Ordinal) && g.Inward == land.Bands.First(b => b.HostId == g.HostId).Inward),
            "a skylight, an opening of a kind the room contract does not name, and a door in a wall this room does not have are no gap in any cliff (eight gaps of eleven openings)");
    }

    private void TestLandStrokes(IntroSource source)
    {
        var land = LandPicture.Build(source);
        var strokes = land.Strokes(LandPage);
        var counts = LandCounts.Of(strokes);
        Check(counts == LandRoomCounts && land.Count(LandCard) == LandRoomCounts,
            $"the room is drawn as land, counted from the strokes at a page's size and at a card's: four cliff bands, three landforms in the open, two cliff-side things, two things on tops, six bays, two notches, no sea ({counts})");
        var known = new HashSet<string> { LandPart.Paper, LandPart.Floor, LandPart.Band, LandPart.Crest, LandPart.Hatch, LandPart.Bay, LandPart.BaySide, LandPart.Notch, LandPart.NotchEdge, LandPart.Buttress,
            LandPart.Shadow, LandPart.Flank, LandPart.Landform, LandPart.CliffSide, LandPart.OnTop, LandPart.Cap, LandPart.Outline, LandPart.Lit };
        var paper = strokes[0];
        Check(paper.Part == LandPart.Paper && paper.Colour == LandPicture.Sand && paper.Width == 0 && MathF.Abs(MathF.Abs(LandPicture.Area(paper.Points)) - LandPage.X * LandPage.Y) < 1 &&
              strokes.Count(s => s.Part == LandPart.Paper) == 1 && strokes.All(s => known.Contains(s.Part)),
            "the first stroke is sand over the whole paper, and every other stroke is a named part of the land");
        Check(LandSound(strokes, LandPage) && LandSound(land.Strokes(LandCard), LandCard) && LandSound(land.Strokes(new Vector2(1040, 800)), new Vector2(1040, 800)),
            "every stroke lies on the paper, every filled shape has area and every line has two points (a canvas refuses less), at a page's size, a card's and twice a page's");
        Check(strokes.Count(s => s.Part == LandPart.Floor) == 2 && strokes.Count(s => s.Part == LandPart.Crest) == 4 && strokes.Count(s => s.Part == LandPart.Hatch) > 40 &&
              land.Strokes(LandCard).All(s => s.Part != LandPart.Hatch),
            $"the plain is the floor's two triangles; each cliff has its crest; a page's cliffs are hatched ({strokes.Count(s => s.Part == LandPart.Hatch)} hachures), a card's too thin to hatch are plain rock");
        Check(counts.Sea == 0 && strokes.Where(s => LandPart.IsGround(s.Part)).All(s => !LandPicture.IsWater(s.Colour)) && LandPicture.IsWater(new Color("1f6fb5")) && LandPicture.IsWater(new Color("3a8fa0")) &&
              new[] { LandPicture.Sand, LandPicture.Plain, LandPicture.Rock, LandPicture.RockDark, LandPicture.Ink }.All(c => !LandPicture.IsWater(c)) &&
              LandCounts.Of(strokes.Append(new LandStroke(LandPart.Floor, paper.Points, new Color("1f6fb5")))).Sea == 1 && LandCounts.Of(strokes.Append(new LandStroke(LandPart.Sea, paper.Points, LandPicture.Sand))).Sea == 1,
            "no sea: no stroke of ground is a water colour, and the count of sea is a count (one stroke of blue ground, or one stroke called sea, and it reads one)");

        // The strokes painted, and looked at where the land must show.
        var pixels = LandPaint(strokes, LandPage);
        LandSave(pixels, "land_room_page");
        LandSave(LandPaint(land.Strokes(LandCard), LandCard), "land_room_card");
        Vector2 Page(float x, float z) => land.ToPage(new Vector2(x, z), LandPage);
        var bandPx = land.BandM * land.Frame(LandPage).Scale;
        var edges = new[] { new Vector2(1, 1), new Vector2(LandPage.X - 2, 1), new Vector2(1, LandPage.Y - 2), LandPage - new Vector2(2, 2), new Vector2(LandPage.X / 2, 1), new Vector2(LandPage.X / 2, LandPage.Y - 2), new Vector2(1, LandPage.Y / 2), new Vector2(LandPage.X - 2, LandPage.Y / 2) };
        Check(edges.All(p => LandSame(LandAt(pixels, p), LandPicture.Sand)) && LandSame(LandAt(pixels, Page(-3.5f, -1)), LandPicture.Plain),
            "painted: sand runs to the paper's edge on every side, and the open floor is the plain");
        var blue = Page(0.2f, 0);
        var water = 0;
        var strays = 0;
        for (var y = 0; y < (int)LandPage.Y; y++)
            for (var x = 0; x < (int)LandPage.X; x++)
                if (LandPicture.IsWater(pixels[x, y]))
                {
                    water++;
                    if (new Vector2(x + 0.5f, y + 0.5f).DistanceTo(blue) > 0.5f * land.Frame(LandPage).Scale) strays++;
                }
        Check(water > 50 && strays == 0, $"painted: the only water colour in the whole picture is the blue box itself, a thing in its own colour ({water} pixels, {strays} elsewhere)");
        // On each wall, a little outside the wall line (rock, or a notch's cut) and half a band inside it (rock, or a bay's plain).
        Color Outside(LandBand wall, Vector2 at) => LandAt(pixels, land.ToPage(at, LandPage) - wall.Inward * bandPx * 0.2f);
        Color Within(LandBand wall, Vector2 at) => LandAt(pixels, land.ToPage(at, LandPage) + wall.Inward * bandPx * 0.5f);
        LandBand Wall(string id) => land.Bands.First(b => b.HostId == "shell:wall_" + id);
        Check(LandRocky(Within(Wall("n"), new Vector2(0, -4))) && LandRocky(Within(Wall("s"), new Vector2(0, 4))) && LandRocky(Within(Wall("e"), new Vector2(5, -3))) && LandRocky(Within(Wall("w"), new Vector2(-5, 0))) &&
              LandRocky(Outside(Wall("n"), new Vector2(0, -4))) && LandRocky(Outside(Wall("w"), new Vector2(-5, 0))),
            "painted: each wall is a band of rock, where it has no opening (also where the skylight, the unknown opening and the lost door would have been)");
        Check(LandSame(Within(Wall("s"), new Vector2(2, 4)), LandPicture.Plain) && LandSame(Within(Wall("s"), new Vector2(-3, 4)), LandPicture.Plain) && LandSame(Within(Wall("e"), new Vector2(5, 0)), LandPicture.Plain) &&
              LandSame(Within(Wall("n"), new Vector2(4, -4)), LandPicture.Plain) && LandSame(Within(Wall("w"), new Vector2(-5, 2)), LandPicture.Plain) && LandSame(Outside(Wall("s"), new Vector2(2, 4)), LandPicture.Plain),
            "painted: at each way through the cliff is open and the plain runs out through it (door, pet door, garage door, archway, portal)");
        Check(LandSame(Outside(Wall("n"), new Vector2(-2, -4)), LandPicture.Sand) && LandSame(Outside(Wall("w"), new Vector2(-5, -2)), LandPicture.Sand) &&
              LandRocky(LandAt(pixels, Page(-2, -4) + Wall("n").Inward * bandPx * 0.9f)) && LandRocky(LandAt(pixels, Page(-5, -2) + Wall("w").Inward * bandPx * 0.9f)),
            "painted: at the window and the vent the crest is cut to the sand, and the cliff's foot below the cut is still rock (a notch, not a bay)");
        Check(LandSame(LandAt(pixels, Page(-0.7f, 0.3f)), new Color("aa5533")) && LandSame(LandAt(pixels, Page(0.097f, 0.038f)), new Color("3366aa")) && LandSame(LandAt(pixels, Page(0.2f, 0)), new Color("ddddcc")) &&
              LandSame(LandAt(pixels, Page(2, -3.7f)), new Color("665544")) && LandSame(LandAt(pixels, Page(4.49f, 3)), new Color("447755")) && LandSame(LandAt(pixels, Page(-4.47f, 0.5f)), new Color("996633")) &&
              LandSame(LandAt(pixels, Page(-2, 2)), IntroSource.DefaultBoxColour),
            "painted: each thing is in its scan colour at its own place, a thing on a top over its host (the cup over the box over the table)");
    }

    /// <summary>The same 2 by 1.2 m block six times over, turned 0, 90, 180, 270, 45 and 200 degrees: its shaded sides are always the page's lower right.</summary>
    private void TestLandShading()
    {
        var turns = new[] { 0f, 90f, 180f, 270f, 45f, 200f };
        var source = LandSourceOf(LandShape("{\"min_m\":[-6,0,-4],\"max_m\":[6,3,4]}", new[] { LandFloor(-6, -4, 6, -4, 6, 4, -6, 4) },
            turns.Select((turn, i) => LandBox("turn" + i, -4 + 4 * (i % 3), 0, -2 + 4 * (i / 3), 2, 1, 1.2f, turn))));
        var land = LandPicture.Build(source);
        var strokes = land.Strokes(LandPage);
        var things = LandThings(strokes);
        var wrong = new List<string>();
        for (var i = 0; i < turns.Length && things.Count == turns.Length; i++)
        {
            var form = land.Forms.First(f => f.Id == "turn" + i);
            var thing = things.First(t => LandMiddle(t.First(s => s.Part == LandPart.Landform).Points).DistanceTo(land.ToPage(form.Centre, LandPage)) < 0.5f);
            var centre = land.ToPage(form.Centre, LandPage);
            var flanks = thing.Where(s => s.Part == LandPart.Flank).ToList();
            var lit = thing.Where(s => s.Part == LandPart.Lit).ToList();
            var sides = turns[i] == 45f ? 1 : 2;
            if (flanks.Count != sides || lit.Count != sides) wrong.Add($"{turns[i]}: {flanks.Count} shaded, {lit.Count} lit");
            // A shaded side starts on an edge of the top that faces away from the light, and falls further that way.
            if (flanks.Any(f => (LandMiddle(f.Points.Take(2).ToArray()) - centre).Dot(LandPicture.Light) <= 0 || (LandMiddle(f.Points) - LandMiddle(f.Points.Take(2).ToArray())).Dot(LandPicture.Light) <= 0)) wrong.Add($"{turns[i]}: a shaded side toward the light");
            if (lit.Any(l => (LandMiddle(l.Points) - centre).Dot(LandPicture.Light) >= 0)) wrong.Add($"{turns[i]}: a lit edge away from the light");
            if (thing.First(s => s.Part == LandPart.Shadow) is var shadow && (LandMiddle(shadow.Points) - centre).Dot(LandPicture.Light) <= 0) wrong.Add($"{turns[i]}: a shadow toward the light");
        }
        Check(things.Count == turns.Length && wrong.Count == 0 && MathF.Abs(LandPicture.Light.X - LandPicture.Light.Y) < 1e-6f && LandPicture.Light.X > 0,
            "light falls from the page's upper left on every thing, however it is turned (0, 90, 180, 270, 45, 200 degrees): its shaded sides and its shadow lie to the lower right, its lit edges to the upper left" +
            (wrong.Count > 0 ? ": " + string.Join("; ", wrong) : ""));

        var pixels = LandPaint(strokes, LandPage);
        LandSave(pixels, "land_turned_page");
        var painted = new List<string>();
        foreach (var i in new[] { 0, 1, 2, 3 })
        {
            var form = land.Forms.First(f => f.Id == "turn" + i);
            var corners = form.Corners.Select(c => land.ToPage(c, LandPage)).ToArray();
            var (west, east, north, south) = (corners.Min(c => c.X), corners.Max(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.Y));
            var centre = land.ToPage(form.Centre, LandPage);
            var shade = form.Colour.Darkened(0.35f);
            if (!LandSame(LandAt(pixels, new Vector2(east + 1.5f, centre.Y)), shade) || !LandSame(LandAt(pixels, new Vector2(centre.X, south + 1.5f)), shade)) painted.Add($"{turns[i]}: east or south not shaded");
            if (!LandSame(LandAt(pixels, new Vector2(west - 1.5f, centre.Y)), LandPicture.Plain) || !LandSame(LandAt(pixels, new Vector2(centre.X, north - 1.5f)), LandPicture.Plain)) painted.Add($"{turns[i]}: west or north not open plain");
        }
        Check(painted.Count == 0, "painted: a block turned 0, 90, 180 or 270 degrees shows its darker side just past its east and south edges, and open plain just past its west and north edges" +
            (painted.Count > 0 ? ": " + string.Join("; ", painted) : ""));
    }

    private void TestLandOddRooms()
    {
        // A room that is not convex: a 10 by 10 m floor with a 4 by 7 m piece cut from its north side.
        var cut = new[] { 0f, 0, 3, 0, 3, 7, 7, 7, 7, 0, 10, 0, 10, 10, 0, 10 };
        var walls = Enumerable.Range(0, 8).Select(i => LandWall("w" + (i + 1), cut[2 * i], cut[2 * i + 1], cut[(2 * i + 2) % 16], cut[(2 * i + 3) % 16]));
        var room = LandPicture.Build(LandSourceOf(LandShape("{\"min_m\":[0,0,0],\"max_m\":[10,3,10]}", walls.Prepend(LandFloor(cut)), Array.Empty<string>(),
            "{\"id\":\"d\",\"kind\":\"door\",\"host_part_id\":\"shell:w3\",\"center_m\":[5,1,7],\"size_m\":[1,2]}")));
        var floor = room.Floors[0];
        var leaning = room.Bands.Where(b => !LandInside(floor, (b.A + b.B) * 0.5f + b.Inward * room.BandM * 0.5f) || LandInside(floor, (b.A + b.B) * 0.5f - b.Inward * room.BandM * 0.5f)).Select(b => b.HostId).ToList();
        var pixels = LandPaint(room.Strokes(LandPage), LandPage);
        LandSave(pixels, "land_cut_room_page");
        var tiles = room.Strokes(LandPage).Where(s => s.Part == LandPart.Floor).ToList();
        Check(room.Bands.Count == 8 && leaning.Count == 0 && room.Gaps.Count == 1 && room.Gaps[0].Inward == new Vector2(0, 1) &&
              tiles.Count == 6 && MathF.Abs(tiles.Sum(t => MathF.Abs(LandPicture.Area(t.Points))) - 72 * room.Frame(LandPage).Scale * room.Frame(LandPage).Scale) < 2 &&
              LandSame(LandAt(pixels, room.ToPage(new Vector2(5, 3), LandPage)), LandPicture.Sand) && LandSame(LandAt(pixels, room.ToPage(new Vector2(5, 8.5f), LandPage)), LandPicture.Plain) &&
              LandRocky(LandAt(pixels, room.ToPage(new Vector2(3, 3.5f), LandPage) + new Vector2(-1, 0) * room.BandM * room.Frame(LandPage).Scale * 0.5f)),
            "in a room that is not convex every cliff still leans onto the floor (the three walls of the cut-out too), the plain is the floor's own shape, and the cut-out is sand" +
            (leaning.Count > 0 ? ": wrong " + string.Join(",", leaning) : ""));

        // Walls joined at their corners: the four of the test room, the eight of the cut room (inside corners too), and a room that
        // comes to a sharp point (20 degrees), where a mitre would run far out and the cliffs are cut square instead.
        var square = LandPicture.Build(LandSourceOf(LandRoom));
        var sharp = LandPicture.Build(LandSourceOf(LandShape("{\"min_m\":[0,0,0],\"max_m\":[10,3,3.6]}",
            new[] { LandFloor(0, 0, 10, 0, 10, 3.6f), LandWall("a", 0, 0, 10, 0), LandWall("b", 10, 0, 10, 3.6f), LandWall("c", 10, 3.6f, 0, 0) }, Array.Empty<string>())));
        float Furthest(LandPicture land)
        {
            // How far, in bands, any corner of a cliff's band lies from the nearer end of its own wall.
            var scale = land.Frame(LandPage).Scale;
            var bandPx = Math.Max(land.BandM * scale, LandPicture.MinBandPx);
            var bands = land.Strokes(LandPage).Where(s => s.Part == LandPart.Band).ToList();
            return bands.Select((s, i) => s.Points.Max(p => Math.Min(p.DistanceTo(land.ToPage(land.Bands[i].A, LandPage)), p.DistanceTo(land.ToPage(land.Bands[i].B, LandPage))))).Max() / bandPx;
        }
        var cornerPx = square.ToPage(new Vector2(-5, -4), LandPage);
        var cornerBand = square.BandM * square.Frame(LandPage).Scale;
        var north = square.Strokes(LandPage).First(s => s.Part == LandPart.Band && LandInside(s.Points, square.ToPage(new Vector2(0, -3.9f), LandPage)));
        Check(square.Bands.All(b => b.JoinA != null && b.JoinB != null) && room.Bands.All(b => b.JoinA != null && b.JoinB != null) &&
              north.Points.Any(p => p.DistanceTo(cornerPx + new Vector2(-1, -1) * cornerBand * LandPicture.CrestOut) < 0.01f) && north.Points.Any(p => p.DistanceTo(cornerPx + new Vector2(1, 1) * cornerBand) < 0.01f) &&
              MathF.Abs(Furthest(square) - MathF.Sqrt(2)) < 0.01f && MathF.Abs(Furthest(room) - MathF.Sqrt(2)) < 0.01f && Furthest(sharp) <= LandPicture.MitreLimit + 0.01f && LandSound(sharp.Strokes(LandPage), LandPage),
            $"walls that meet are one cliff: each band is mitred to its neighbour's at the corner, outside and inside (a square corner's mitre runs 1.41 bands; in a room with a 20 degree point no band runs past {Furthest(sharp):0.##})");

        // Shapes cut into triangles without the engine: an L either way round, an outline that ends on its first point again, points in a line.
        var el = new[] { new Vector2(0, 0), new Vector2(4, 0), new Vector2(4, 2), new Vector2(2, 2), new Vector2(2, 4), new Vector2(0, 4) };
        var closed = LandPicture.Triangles(new[] { new Vector2(0, 0), new Vector2(2, 0), new Vector2(2, 2), new Vector2(0, 2), new Vector2(0, 0) });
        static float Covered(List<Vector2[]> triangles) => triangles.Sum(t => MathF.Abs(LandPicture.Area(t)));
        Check(LandPicture.Triangles(el).Count == 4 && MathF.Abs(Covered(LandPicture.Triangles(el)) - 12) < 1e-3f && MathF.Abs(Covered(LandPicture.Triangles(el.Reverse().ToArray())) - 12) < 1e-3f &&
              MathF.Abs(Covered(closed) - 4) < 1e-3f && Covered(LandPicture.Triangles(new[] { new Vector2(0, 0), new Vector2(1, 1), new Vector2(2, 2), new Vector2(3, 3) })) < 1e-6f,
            "a floor is cut into triangles that cover it exactly, whichever way round its outline runs and even if it ends on its first point again; points in a line cover nothing");

        // Nothing at all: no floor, no wall, no thing.
        var empty = LandPicture.Build(LandSourceOf(LandShape(LandBounds, Array.Empty<string>(), Array.Empty<string>())));
        var only = empty.Strokes(LandPage);
        Check(empty.Bands.Count == 0 && empty.Forms.Count == 0 && empty.Gaps.Count == 0 && only.Count == 1 && only[0].Part == LandPart.Paper && LandCounts.Of(only) == new LandCounts(0, 0, 0, 0, 0, 0, 0) &&
              empty.Strokes(Vector2.Zero).Count == 0 && empty.Strokes(new Vector2(float.NaN, 100)).Count == 0,
            "a shape with nothing in it is bare sand and counts nothing; a picture of no size, or of a size that is not a number, draws nothing");
        // A thing far outside its room's bounds (the shape data is untrusted) is not drawn off the paper; a picture a few pixels across is still sound.
        var astray = LandPicture.Build(LandSourceOf(LandShape(LandBounds, LandFourWalls(), new[] { LandBox("in", 0, 0, 0, 1, 1, 1, 0), LandBox("out", 400, 0, 300, 1, 1, 1, 0) })));
        var tiny = LandPicture.Build(LandSourceOf(LandRoom)).Strokes(new Vector2(6, 6));
        Check(astray.Forms.Count == 2 && astray.Count(LandPage).Things == 1 && LandSound(astray.Strokes(LandPage), LandPage) && tiny.Count > 0 && LandSound(tiny, new Vector2(6, 6)),
            "a thing far outside its room is left off the paper, and a picture six pixels across holds only shapes a canvas takes");

        // Two thin sheets one over the other, off the floor with nothing under them: neither is the other's host twice over.
        var sheets = LandPicture.Build(LandSourceOf(LandShape(LandBounds, LandFourWalls(), new[] { LandBox("a", 0, 0.05f, 0, 0.3f, 0.01f, 0.2f, 0), LandBox("b", 0, 0.06f, 0, 0.29f, 0.01f, 0.2f, 0) })));
        // A stack of twelve: counted eight deep and no deeper.
        var stack = LandPicture.Build(LandSourceOf(LandShape(LandBounds, LandFourWalls(), Enumerable.Range(0, 12).Select(i => LandBox("s" + i, 0, i * 0.2f, 0, 1f - i * 0.05f, 0.2f, 1f - i * 0.05f, 0)))));
        Check(sheets.Forms.First(f => f.Id == "a").Level == 0 && sheets.Forms.First(f => f.Id == "b").Level == 1 && sheets.Count(LandPage) is { Landforms: 1, Tops: 1 } &&
              stack.Forms.Select(f => f.Level).OrderBy(l => l).SequenceEqual(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 8, 8, 8 }) && stack.Count(LandPage) is { Landforms: 1, Tops: 11 },
            $"a thing rests only on a thing below it, so two thin sheets are one on the other (levels {sheets.Forms.First(f => f.Id == "a").Level} and {sheets.Forms.First(f => f.Id == "b").Level}), and a stack of twelve is counted eight deep");

        // More things than a picture draws: the 400 with the biggest footprints, and no others.
        var many = Enumerable.Range(0, 450).Select(i => LandBox("m" + i, -4.5f + 0.3f * (i % 30), 0, -3.5f + 0.45f * (i / 30), 0.05f + 0.0004f * ((i * 7) % 450), 0.2f, 0.2f, 0)).ToList();
        var crowd = LandPicture.Build(LandSourceOf(LandShape(LandBounds, LandFourWalls(), many)));
        var kept = Enumerable.Range(0, 450).OrderByDescending(i => (i * 7) % 450).Take(LandPicture.MaxThings).Select(i => "m" + i).ToHashSet();
        Check(LandPicture.MaxThings == 400 && IslandThumbnail.MaxBoxes == 400 && crowd.Forms.Count == 400 && crowd.Forms.All(f => kept.Contains(f.Id)) && crowd.Count(new Vector2(1040, 800)).Things == 400,
            "of 450 things the picture draws the 400 with the biggest footprints");
    }

    private void TestLandRim(IntroSource source)
    {
        // A land made with the rim: its own openings stand in for the room's, and it still draws no sea.
        var own = LandPicture.Build(source);
        var rim = LandPicture.Build(source, new List<RimOpening> { new("notch", new Vector2(-2, -4), 1.2f), new("bay", new Vector2(2, 4), 1f), new("bay", new Vector2(-5, 0), 1f) });
        var counts = rim.Count(LandPage);
        Check(rim.FromRim && !rim.RimRefused && counts == LandRoomCounts with { Bays = 2, Notches = 1 } && rim.Gaps.Any(g => g.Kind == "notch" && g.HostId == "shell:wall_n" && LandSpans(g, -2.6f, -4, -1.4f, -4)) &&
              rim.Gaps.Any(g => g.Kind == "bay" && g.HostId == "shell:wall_s" && LandSpans(g, 1.5f, 4, 2.5f, 4)) && rim.Gaps.Any(g => g.Kind == "bay" && g.HostId == "shell:wall_w" && LandSpans(g, -5, -0.5f, -5, 0.5f)),
            $"a rim record's notches and bays are the land's, each in the wall it is nearest, and no sea is drawn ({counts})");
        var none = LandPicture.Build(source, new List<RimOpening>());
        Check(none.FromRim && none.Gaps.Count == 0 && none.Count(LandPage) == LandRoomCounts with { Bays = 0, Notches = 0 }, "a rim record with no openings is an unbroken cliff");

        var bad = new (string Why, List<RimOpening> Rim)[]
        {
            ("a kind that is neither notch nor bay", new() { new("sea", new Vector2(0, -4), 2) }),
            ("a kind in other letters", new() { new("Bay", new Vector2(0, -4), 2) }),
            ("a width that is not a number", new() { new("notch", new Vector2(-2, -4), 1.2f), new("bay", new Vector2(0, 4), float.NaN) }),
            ("a width of nothing", new() { new("bay", new Vector2(0, 4), 0) }),
            ("a width wider than the land", new() { new("bay", new Vector2(0, 4), 10.5f) }),
            ("a centre without end", new() { new("notch", new Vector2(float.PositiveInfinity, 0), 1) }),
            ("a centre that is not a number", new() { new("notch", new Vector2(0, float.NaN), 1) }),
            ("a centre outside the land", new() { new("bay", new Vector2(50, 0), 1) }),
            ("seventeen openings", Enumerable.Range(0, 17).Select(i => new RimOpening("notch", new Vector2(-4 + 0.5f * i, -4), 0.2f)).ToList()),
        };
        var kept = new List<string>();
        foreach (var (why, record) in bad)
        {
            var land = LandPicture.Build(source, record);
            var strokes = land.Strokes(LandPage);
            if (land.FromRim || !land.RimRefused || LandCounts.Of(strokes) != LandRoomCounts || land.Gaps.Count != own.Gaps.Count || strokes.Any(s => s.Points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y)))) kept.Add(why);
        }
        Check(kept.Count == 0 && LandPicture.Build(source, Enumerable.Range(0, 16).Select(i => new RimOpening("notch", new Vector2(-4 + 0.5f * i, -4), 0.2f)).ToList()).FromRim,
            "a bad rim record fails closed: it is refused whole, nothing of it is drawn, and the room's own doors and windows are" + (kept.Count > 0 ? "; kept: " + string.Join("; ", kept) : " (nine bad records; sixteen openings are taken)"));
    }

    private void TestLandExtras(IntroSource source)
    {
        // Beyond what was asked, each with its switch; the counts never change.
        var land = LandPicture.Build(source);
        var all = land.Strokes(LandPage, extras: new LandExtras());
        var plain = land.Strokes(LandPage, extras: LandExtras.None);
        var cabinet = land.Forms.First(f => f.Id == "cabinet");
        var joins = all.Where(s => s.Part == LandPart.Buttress).ToList();
        Check(joins.Count == 2 && joins.All(s => s.Colour == LandPicture.Rock) && joins.Any(s => LandInside(s.Points, land.ToPage(new Vector2(2, -3.97f), LandPage)) && LandInside(s.Points, land.ToPage(cabinet.Centre, LandPage))) &&
              plain.All(s => s.Part != LandPart.Buttress) && LandCounts.Of(plain) == LandRoomCounts && LandCounts.Of(all) == LandRoomCounts,
            "beyond what was asked, with its switch: rock joins each cliff-side thing to its wall line; switched off there is none, and the counts are the same");
        // A hachure is a wedge: its base on the crest (two points), its point toward the plain.
        static Vector2 Run(LandStroke hatch) => hatch.Points[2] - (hatch.Points[0] + hatch.Points[1]) * 0.5f;
        static float Reach(LandStroke hatch) => Run(hatch).Length();
        var lengths = all.Where(s => s.Part == LandPart.Hatch).Select(h => MathF.Round(Reach(h), 1)).Distinct().Count();
        var even = plain.Where(s => s.Part == LandPart.Hatch).Select(h => MathF.Round(Reach(h), 1)).Distinct().Count();
        // A low south wall (1.2 m against 3 m): hatched wider apart than the tall north wall of the same length.
        var low = LandPicture.Build(LandSourceOf(LandShape(LandBounds, LandFourWalls(south: 1.2f), Array.Empty<string>())));
        (int North, int South) Hachures(LandExtras extras)
        {
            // The north cliff's hachures run down the page onto the plain, the south cliff's up it.
            var hatches = low.Strokes(LandPage, extras: extras).Where(s => s.Part == LandPart.Hatch).ToList();
            return (hatches.Count(h => Run(h).Normalized().Y > 0.9f), hatches.Count(h => Run(h).Normalized().Y < -0.9f));
        }
        var (byHeight, flat) = (Hachures(new LandExtras()), Hachures(LandExtras.None));
        var wedges = all.Where(s => s.Part == LandPart.Hatch).ToList();
        Check(wedges.Count > 40 && wedges.All(h => h.Width == 0 && h.Points.Length == 3 && h.Colour == LandPicture.RockDark && Reach(h) > 2 * h.Points[0].DistanceTo(h.Points[1])) &&
              land.Bands.All(b => wedges.Count(h => Run(h).Normalized().Dot(b.Inward) > 0.9f) > 10),
            $"a cliff's hachures are wedges of darker rock, broad at the crest and run down to a point toward the plain, on every wall ({wedges.Count} at a page's size)");
        var tops = all.Where(s => s.Part is LandPart.Landform or LandPart.CliffSide or LandPart.OnTop).ToList();
        var caps = all.Where(s => s.Part == LandPart.Cap).ToList();
        Check(caps.Count == 6 && caps.All(c => land.Forms.Any(f => f.Colour == c.Colour && LandMiddle(c.Points).DistanceTo(land.ToPage(f.Centre, LandPage)) < 0.5f)) &&
              tops.Count(t => land.Forms.Any(f => f.Colour == t.Colour)) == 1 && plain.All(s => s.Part != LandPart.Cap) &&
              plain.Where(s => s.Part is LandPart.Landform or LandPart.CliffSide or LandPart.OnTop).All(t => land.Forms.Any(f => f.Colour == t.Colour)),
            $"beyond what was asked, with its switch: a thing big enough is a mesa, a flat cap in its scan colour set in from a darker edge ({caps.Count} of 7; the cup is too small); switched off, each thing is one flat colour");
        Check(lengths == 2 && even == 1 && byHeight.North > 20 && byHeight.South < byHeight.North * 0.7f && flat.North == flat.South && flat.North == byHeight.North,
            $"beyond what was asked, with their switches: hachures run long and short by turns ({lengths} lengths; switched off, {even}), and a low cliff is hatched wider apart than a tall one ({byHeight.South} against {byHeight.North}; switched off, {flat.South} and {flat.North})");
        var before = LandExtras.Current;
        LandExtras.Current = LandExtras.None;
        var switched = land.Strokes(LandPage);
        LandExtras.Current = before;
        Check(switched.All(s => s.Part != LandPart.Buttress) && land.Strokes(LandPage).Any(s => s.Part == LandPart.Buttress) && before == new LandExtras(),
            "the one switch for every picture (LandExtras.Current) turns them off and on again; all are on to begin with");
    }

    private async Task TestLandCard(IntroSource source)
    {
        // The card draws by sending strokes to its canvas; what it counts is what it sent.
        var thumbnail = new IslandThumbnail { CustomMinimumSize = LandCard, Size = LandCard };
        AddChild(thumbnail);
        thumbnail.SetShape(source);
        await Frames(3);
        var expected = thumbnail.Land?.Strokes(thumbnail.Size) ?? Array.Empty<LandStroke>();
        Check(thumbnail.Land != null && thumbnail.Size == LandCard && thumbnail.DrawnStrokes.Count == expected.Count && expected.Count > 40 &&
              thumbnail.DrawnStrokes.Select(s => s.Part).SequenceEqual(expected.Select(s => s.Part)) && thumbnail.Drawn == LandRoomCounts,
            $"the card's picture is drawn: every stroke of the land went to its canvas, in order ({thumbnail.DrawnStrokes.Count} strokes: {thumbnail.Drawn})");
        Check(thumbnail.FloorTriangles == 2 && thumbnail.WallStrokes == 4 && thumbnail.BoxesDrawn == 7,
            $"the card counts what it drew ({thumbnail.FloorTriangles} floor triangles, {thumbnail.WallStrokes} cliff bands, {thumbnail.BoxesDrawn} things)");
        thumbnail.SetShape(source, new List<RimOpening> { new("notch", new Vector2(-2, -4), 1.2f), new("bay", new Vector2(2, 4), 1f) });
        await Frames(2);
        Check(thumbnail.Land is { FromRim: true } && thumbnail.Drawn == LandRoomCounts with { Bays = 1, Notches = 1 }, $"a land with a rim record is drawn with the rim's openings and no sea ({thumbnail.Drawn})");
        thumbnail.SetShape(null);
        await Frames(2);
        var house = LandPicture.StandIn(LandCard);
        var outline = house.FirstOrDefault(s => s.Part == LandPart.StandIn && s.Width > 0);
        Check(!thumbnail.HasShape && thumbnail.Land == null && thumbnail.BoxesDrawn == 0 && thumbnail.WallStrokes == 0 && thumbnail.DrawnStrokes.Count == house.Count && house.Count == 5 &&
              thumbnail.DrawnStrokes[0].Part == LandPart.Paper && thumbnail.DrawnStrokes.Skip(1).All(s => s.Part == LandPart.StandIn) && thumbnail.Drawn.Sea == 0 &&
              outline != null && LandPicture.Sand.Luminance - outline.Colour.Luminance > 0.4f && house.All(s => s.Points.All(p => p.X >= 0 && p.Y >= 0 && p.X <= LandCard.X && p.Y <= LandCard.Y)),
            "a room without shape data draws the stand-in, a house on the same sand, its outline dark enough to see, all of it on the card");
        LandSave(LandPaint(house, LandCard), "land_stand_in_card");
        thumbnail.SetShape(LandSourceOf(LandShape(LandBounds, Array.Empty<string>(), Array.Empty<string>())));
        await Frames(2);
        Check(thumbnail.HasShape && thumbnail.DrawnStrokes.Count == 1 && thumbnail.DrawnStrokes[0].Part == LandPart.Paper && thumbnail.BoxesDrawn == 0, "a shape with nothing in it still draws: bare sand");
        thumbnail.QueueFree();
        await Frames(1);
    }

    private void TestLandNames()
    {
        // Names: cleaned as display text, then freed of "Landscape".
        Check(IslandNames.Land("Landscape: My   Garage\u0007", "x") == "My Garage" && IslandNames.Land("Landscape", "landscape_garage_nominal") == "Garage nominal" &&
              IslandNames.Land("", "test_room") == "Test room" && IslandNames.Land(new string('a', 90), "x").Length == IslandEntry.MaxNameLength &&
              IslandNames.Land("Landscape garage nominal", "landscape_garage_nominal") == "Garage nominal" && IslandNames.Land("Landscapes of Mars", "x") == "Landscapes of Mars",
            "a land's name is cleaned for display: no control characters, no \"Landscape\", the room's id spoken plainly when it has none, never over forty characters");
        Check(IslandNames.Land("iPad room", "x") == "iPad room" && IslandNames.Land("eBay shelf Landscape", "x") == "eBay shelf" && IslandNames.Land("the den", "x") == "the den" &&
              IslandNames.Land("Garage - Landscape", "x") == "Garage" && IslandNames.Land("Landscape: the garage (Landscape)", "x") == "The garage",
            "a name the family typed keeps its own letters; only a name that led with \"Landscape\" has its new first letter raised");
        Check(IslandNames.Land("Landscape of the very long garage name here ok", "x") == "Of the very long garage name here ok" && IslandNames.Land("Landscape Garage", "x") == "Garage" &&
              IslandNames.Land("Landscape\tden", "x") == "Den" && IslandNames.Land("Landscape " + new string('b', 60), "x") == "B" + new string('b', 39),
            "\"Landscape\" is taken off before the cut to forty, so a long name keeps what fits, and whatever space follows the word (a tab, a no-break space)");
        Check(IslandNames.Land("Landscape", "landscape") == "This land" && IslandNames.Land("Refused.", "my_room") == "My room" && IslandNames.Land("\u0007", "x") == "X" && IslandNames.Land("  ", "") == "This land",
            "a name with nothing left is the room's id spoken plainly, or \"This land\"");
        var root = Folder("rooms_land");
        Write(Path.Combine(root, "a", "room.json"), Manifest("landscape_zebra", "Landscape zebra", tutorial: false, shape: false));
        Write(Path.Combine(root, "b", "room.json"), Manifest("marsh", "Marsh", tutorial: false, shape: false));
        Write(Path.Combine(root, "c", "room.json"), Manifest("landscape_attic", "landscape attic", tutorial: false, shape: false));
        var listed = IslandCatalog.Scan(new[] { ProjectSettings.LocalizePath(root) });
        Check(listed.Select(i => i.LandName).SequenceEqual(new[] { "Attic", "Marsh", "Zebra" }) && listed.Select(i => i.DisplayName).SequenceEqual(new[] { "landscape attic", "Marsh", "Landscape zebra" }),
            "the lands are listed by the name shown for them, not by the word taken off: " + string.Join(", ", listed.Select(i => i.LandName)));
    }
}
