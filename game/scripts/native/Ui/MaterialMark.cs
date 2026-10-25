using System;
using System.Collections.Generic;
using Godot;

namespace GarageGame.Native.Ui;

/// <summary>
/// A material's mark in the repair icon (MECHANICS.md decision 131): a log, a stone, a coin of copper or a leaf, drawn as shapes so no glyph
/// has to be in the font. The kinds a repair spends are the ones with a material (the pack's inventory kinds); a kind of no material the
/// game knows gets a plain dot. It draws once, when it is made or its material changes, and takes no mouse.
/// </summary>
public partial class MaterialMark : Control
{
    /// <summary>The mark is this many pixels square.</summary>
    public const float SizePx = 16;

    public static readonly Color Bark = new("7a4f2c"), Heartwood = new("d2ad72"), Ring = new("8a6238");
    public static readonly Color StoneBody = new("8d9796"), StoneShade = new("66706f"), StoneLight = new("b7c0bd");
    public static readonly Color Copper = new("c9773a"), CopperRim = new("8f4f22"), Leaf = new("62a14b"), LeafVein = new("3e7032"), Plain = new("c8d3cc");

    // The shapes, made once: a lumpy stone, a leaf with a point at each end.
    private static readonly Vector2[] StoneShape =
    {
        new(2.5f, 11.5f), new(1.5f, 7.5f), new(4.5f, 3.5f), new(9f, 2.5f), new(13f, 5f), new(14.5f, 9f), new(12.5f, 12.5f), new(7f, 13.5f),
    };
    private static readonly Vector2[] LeafShape = BuildLeaf();

    private string _material = "";

    /// <summary>"wood", "stone", "copper" or "leaf" (the inventory kind's material); anything else is a plain dot.</summary>
    public string KindMaterial
    {
        get => _material;
        set
        {
            value ??= "";
            if (value == _material) return;
            _material = value;
            QueueRedraw();
        }
    }

    /// <summary>How many times the mark was drawn (the suite checks that it drew, and that it does not draw again while it waits).</summary>
    public int Draws { get; private set; }

    public MaterialMark()
    {
        Name = "Mark";
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(SizePx, SizePx);
        // In a row beside words the mark keeps its own size, centred on the line (a row would stretch it to the words' height).
        SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
    }

    private static Vector2[] BuildLeaf()
    {
        // A lens from the stalk (2, 14) to the tip (14, 2): two arcs bowed away from the line between them.
        var points = new List<Vector2>();
        var from = new Vector2(2, 14);
        var to = new Vector2(14, 2);
        var across = (to - from).Orthogonal().Normalized() * 3.6f;
        const int steps = 6;
        for (var i = 0; i <= steps; i++)
        {
            var t = i / (float)steps;
            points.Add(from.Lerp(to, t) + across * Mathf.Sin(t * Mathf.Pi));
        }
        for (var i = steps - 1; i >= 1; i--)
        {
            var t = i / (float)steps;
            points.Add(from.Lerp(to, t) - across * Mathf.Sin(t * Mathf.Pi));
        }
        return points.ToArray();
    }

    public override void _Draw()
    {
        Draws++;
        switch (_material)
        {
            case "wood":
                // A log seen from the side: its bark, a paler end with rings.
                DrawRect(new Rect2(1.5f, 4.5f, 10.5f, 9), Bark);
                DrawRect(new Rect2(1.5f, 4.5f, 10.5f, 2f), new Color(Heartwood, 0.25f));
                DrawCircle(new Vector2(12, 9), 4.5f, Bark);
                DrawCircle(new Vector2(12, 9), 3.5f, Heartwood);
                DrawArc(new Vector2(12, 9), 2f, 0, Mathf.Tau, 12, Ring, 1f, true);
                DrawCircle(new Vector2(12, 9), 0.7f, Ring);
                break;
            case "stone":
                DrawColoredPolygon(StoneShape, StoneBody);
                DrawPolyline(new[] { StoneShape[0], StoneShape[1], StoneShape[2], StoneShape[3] }, StoneLight, 1.2f, true);
                DrawPolyline(new[] { StoneShape[4], StoneShape[5], StoneShape[6], StoneShape[7] }, StoneShade, 1.4f, true);
                break;
            case "copper":
                DrawCircle(new Vector2(8, 8), 6.5f, CopperRim);
                DrawCircle(new Vector2(8, 8), 5.2f, Copper);
                DrawArc(new Vector2(8, 8), 3f, 0, Mathf.Tau, 14, CopperRim, 1f, true);
                break;
            case "leaf":
                DrawColoredPolygon(LeafShape, Leaf);
                DrawLine(new Vector2(2.6f, 13.4f), new Vector2(12.6f, 3.4f), LeafVein, 1.1f, true);
                break;
            default:
                DrawCircle(new Vector2(8, 8), 3.5f, Plain);
                break;
        }
    }
}
