using Godot;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace GarageGame.Native.Look.Intro;

/// <summary>
/// Where the land starts in the intro: a grid of cells over the island, each with the room's height there (the floor, or the top of
/// the highest box standing on it), when it starts to rise, how much of it is coast, and the room's colour there. The land's shader
/// draws each vertex between that height and its own (painterly_land.gdshader, intro_land), so the island's own ground shows the room
/// first, the boxes rising and softening into land, with no second island and no generator change: the field is computed in the game
/// from the source boxes and floor (IntroSource).
///
/// <list type="bullet">
/// <item><b>Start height:</b> the floor's height, or a box's top where its footprint covers the cell (the highest when boxes stack:
/// the french press on the table). Outside the floor it is the floor's height too: that land is the coast, hidden behind the walls.</item>
/// <item><b>Delay</b> (0 to 1, the share of the land's phase before the cell starts to move): the boxes first, then the ground round
/// them by its distance from the nearest box, out to DelayReach of the room's size, loosened by a little noise.</item>
/// <item><b>Coast</b> (0 to 1): 1 outside the floor, falling to 0 CoastBand of the room's size inside it. The coast moves with the
/// walls' slump rather than with the boxes.</item>
/// <item><b>Colour</b> (linear): the floor's, or the box's.</item>
/// </list>
/// Pure C# (any thread) but for the two images the GPU samples, which are made on the main thread.
/// </summary>
public sealed class IntroField
{
    /// <summary>The field's cells: the island's longer side in this many, each between MinCellM and MaxCellM.</summary>
    public const int CellsAcross = 320;
    public const float MinCellM = 0.01f, MaxCellM = 0.5f;
    /// <summary>The ground round the boxes waits by its distance from them, out to this share of the room's longer side.</summary>
    public const float DelayReach = 0.3f;
    /// <summary>How much of a cell's delay is noise (the rest is its distance from the boxes).</summary>
    public const float DelayJitter = 0.12f;
    /// <summary>The coast's band inside the floor, as a share of the room's longer side.</summary>
    public const float CoastBand = 0.06f;

    /// <summary>The field's square on the ground: its first cell's corner, the cell size, and the cells along x and z.</summary>
    public Vector2 Origin { get; private init; }
    public float CellM { get; private init; }
    public int Width { get; private init; }
    public int Depth { get; private init; }
    public float FloorY { get; private init; }
    public float[] Start { get; private init; } = Array.Empty<float>();
    public float[] Delay { get; private init; } = Array.Empty<float>();
    public float[] Coast { get; private init; } = Array.Empty<float>();
    /// <summary>Each cell's colour, linear.</summary>
    public Color[] Colour { get; private init; } = Array.Empty<Color>();
    /// <summary>The highest start height (the tallest box's top, or the floor).</summary>
    public float TopY { get; private init; }

    /// <summary>The shader's intro_area: x0, z0, 1 / width, 1 / depth in metres.</summary>
    public Vector4 Area => new(Origin.X, Origin.Y, 1f / (Width * CellM), 1f / (Depth * CellM));

    /// <summary>Build the field over an area of the ground (the island's bounds), from the source room.</summary>
    public static IntroField Build(IntroSource source, Rect2 area)
    {
        var longest = Math.Max(area.Size.X, area.Size.Y);
        var cell = Math.Clamp(longest / CellsAcross, MinCellM, MaxCellM);
        var width = Math.Max(2, (int)MathF.Ceiling(area.Size.X / cell));
        var depth = Math.Max(2, (int)MathF.Ceiling(area.Size.Y / cell));
        var floorY = source.FloorY;
        var count = width * depth;
        var start = new float[count];
        var colour = new Color[count];
        var floorColour = IntroSource.DefaultFloorColour.SrgbToLinear();
        Array.Fill(start, floorY);
        Array.Fill(colour, floorColour);
        // The boxes: every cell a footprint covers takes its top, the highest box winning (its colour too).
        var boxed = new bool[count];
        foreach (var box in source.Boxes)
        {
            var (x0, z0, x1, z1) = box.FootprintBounds();
            int i0 = Math.Max(0, (int)MathF.Floor((x0 - area.Position.X) / cell)), i1 = Math.Min(width - 1, (int)MathF.Floor((x1 - area.Position.X) / cell));
            int j0 = Math.Max(0, (int)MathF.Floor((z0 - area.Position.Y) / cell)), j1 = Math.Min(depth - 1, (int)MathF.Floor((z1 - area.Position.Y) / cell));
            var boxColour = box.Colour.SrgbToLinear();
            for (var j = j0; j <= j1; j++)
                for (var i = i0; i <= i1; i++)
                {
                    float x = area.Position.X + (i + 0.5f) * cell, z = area.Position.Y + (j + 0.5f) * cell;
                    if (!box.Covers(x, z)) continue;
                    var index = j * width + i;
                    boxed[index] = true;
                    if (box.TopM <= start[index]) continue;
                    start[index] = box.TopM;
                    colour[index] = boxColour;
                }
        }
        // Inside the floor or not (any floor polygon), on the ground.
        var floors = source.Floors.Select(f => f.Points.Select(p => new Vector2(p.X, p.Z)).ToArray()).ToArray();
        var inside = new bool[count];
        for (var j = 0; j < depth; j++)
            for (var i = 0; i < width; i++)
            {
                var point = new Vector2(area.Position.X + (i + 0.5f) * cell, area.Position.Y + (j + 0.5f) * cell);
                inside[j * width + i] = floors.Length == 0 || floors.Any(f => InPolygon(point, f));
            }
        var roomSize = Math.Max(source.Bounds.Size.X, source.Bounds.Size.Z);
        var fromBoxes = Distances(boxed, width, depth, cell);
        var outside = new bool[count];
        for (var k = 0; k < count; k++) outside[k] = !inside[k];
        var fromOutside = Distances(outside, width, depth, cell);
        var delay = new float[count];
        var coast = new float[count];
        var reach = Math.Max(DelayReach * roomSize, cell);
        var band = Math.Max(CoastBand * roomSize, 2f * cell);
        for (var j = 0; j < depth; j++)
            for (var i = 0; i < width; i++)
            {
                var k = j * width + i;
                var near = source.Boxes.Count == 0 ? 0.5f : MathF.Pow(Math.Clamp(fromBoxes[k] / reach, 0f, 1f), 0.8f);
                delay[k] = Math.Clamp(near * (1f - DelayJitter) + Hash(i, j) * DelayJitter, 0f, 1f);
                coast[k] = inside[k] ? 1f - SmoothStep(0f, band, fromOutside[k]) : 1f;
            }
        return new IntroField
        {
            Origin = area.Position, CellM = cell, Width = width, Depth = depth, FloorY = floorY,
            Start = start, Delay = delay, Coast = coast, Colour = colour, TopY = start.Max(),
        };
    }

    /// <summary>The start height at a point, sampled as the GPU samples the field (bilinear between cell centres, clamped at the edge).</summary>
    public float StartAt(float x, float z) => Sample(Start, x, z);

    public float CoastAt(float x, float z) => Sample(Coast, x, z);

    public float DelayAt(float x, float z) => Sample(Delay, x, z);

    public Color ColourAt(float x, float z)
    {
        var (i, j) = CellOf(x, z);
        return Colour[j * Width + i];
    }

    public (int I, int J) CellOf(float x, float z) =>
        (Math.Clamp((int)MathF.Floor((x - Origin.X) / CellM), 0, Width - 1), Math.Clamp((int)MathF.Floor((z - Origin.Y) / CellM), 0, Depth - 1));

    private float Sample(float[] values, float x, float z)
    {
        var u = Math.Clamp((x - Origin.X) / CellM - 0.5f, 0f, Width - 1f);
        var v = Math.Clamp((z - Origin.Y) / CellM - 0.5f, 0f, Depth - 1f);
        int i0 = (int)MathF.Floor(u), j0 = (int)MathF.Floor(v);
        int i1 = Math.Min(i0 + 1, Width - 1), j1 = Math.Min(j0 + 1, Depth - 1);
        float fu = u - i0, fv = v - j0;
        var a = values[j0 * Width + i0] + (values[j0 * Width + i1] - values[j0 * Width + i0]) * fu;
        var b = values[j1 * Width + i0] + (values[j1 * Width + i1] - values[j1 * Width + i0]) * fu;
        return a + (b - a) * fv;
    }

    /// <summary>The field the shader samples (intro_start): r start height, g delay, b coast, as half floats. Main thread.</summary>
    public Image StartImage()
    {
        var bytes = new byte[Width * Depth * 8];
        for (var k = 0; k < Width * Depth; k++)
        {
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(k * 8), (Half)Start[k]);
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(k * 8 + 2), (Half)Delay[k]);
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(k * 8 + 4), (Half)Coast[k]);
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(k * 8 + 6), (Half)1f);
        }
        return Image.CreateFromData(Width, Depth, false, Image.Format.Rgbah, bytes);
    }

    /// <summary>The room's colours the shader samples (intro_colour), linear, as half floats. Main thread.</summary>
    public Image ColourImage()
    {
        var bytes = new byte[Width * Depth * 8];
        for (var k = 0; k < Width * Depth; k++)
        {
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(k * 8), (Half)Colour[k].R);
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(k * 8 + 2), (Half)Colour[k].G);
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(k * 8 + 4), (Half)Colour[k].B);
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(k * 8 + 6), (Half)1f);
        }
        return Image.CreateFromData(Width, Depth, false, Image.Format.Rgbah, bytes);
    }

    /// <summary>Each cell's distance in metres to the nearest marked cell (a two-pass chamfer; far when none is marked).</summary>
    public static float[] Distances(bool[] marked, int width, int depth, float cell)
    {
        const float Far = 1e9f;
        var diagonal = cell * MathF.Sqrt(2f);
        var d = new float[marked.Length];
        for (var k = 0; k < d.Length; k++) d[k] = marked[k] ? 0f : Far;
        for (var j = 0; j < depth; j++)
            for (var i = 0; i < width; i++)
            {
                var k = j * width + i;
                if (d[k] == 0f) continue;
                var best = d[k];
                if (i > 0) best = MathF.Min(best, d[k - 1] + cell);
                if (j > 0)
                {
                    best = MathF.Min(best, d[k - width] + cell);
                    if (i > 0) best = MathF.Min(best, d[k - width - 1] + diagonal);
                    if (i < width - 1) best = MathF.Min(best, d[k - width + 1] + diagonal);
                }
                d[k] = best;
            }
        for (var j = depth - 1; j >= 0; j--)
            for (var i = width - 1; i >= 0; i--)
            {
                var k = j * width + i;
                if (d[k] == 0f) continue;
                var best = d[k];
                if (i < width - 1) best = MathF.Min(best, d[k + 1] + cell);
                if (j < depth - 1)
                {
                    best = MathF.Min(best, d[k + width] + cell);
                    if (i < width - 1) best = MathF.Min(best, d[k + width + 1] + diagonal);
                    if (i > 0) best = MathF.Min(best, d[k + width - 1] + diagonal);
                }
                d[k] = best;
            }
        return d;
    }

    /// <summary>Even-odd point in polygon on the ground.</summary>
    public static bool InPolygon(Vector2 point, IReadOnlyList<Vector2> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    private static float SmoothStep(float from, float to, float x)
    {
        var t = Math.Clamp((x - from) / Math.Max(to - from, 1e-6f), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>A stable value in [0, 1) for a cell.</summary>
    private static float Hash(int i, int j)
    {
        unchecked
        {
            var h = (uint)(i * 73856093) ^ (uint)(j * 19349663);
            h ^= h >> 13;
            h *= 0x5bd1e995;
            h ^= h >> 15;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }
}
