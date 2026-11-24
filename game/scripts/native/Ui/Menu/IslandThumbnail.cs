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
    /// <summary>At most this many boxes are drawn, the biggest footprints first (LandPicture's own limit).</summary>
    public const int MaxBoxes = LandPicture.MaxThings;

    private IntroSource? _shape;
    private LandPicture? _land;

    /// <summary>What the last draw sent to the canvas, stroke by stroke as each was drawn (the test reads these to know the picture is the land's).</summary>
    public IReadOnlyList<LandStroke> DrawnStrokes { get; private set; } = Array.Empty<LandStroke>();
    /// <summary>What was drawn, counted: cliff bands, landforms, cliff-side things, things on tops, bays, notches, sea.</summary>
    public LandCounts Drawn => LandCounts.Of(DrawnStrokes);
    /// <summary>What was drawn: floor triangles, wall strokes (cliff bands) and boxes (things).</summary>
    public int FloorTriangles => DrawnStrokes.Count(s => s.Part == LandPart.Floor);
    public int WallStrokes => Drawn.Bands;
    public int BoxesDrawn => Drawn.Things;
    public bool HasShape => _shape != null;

    public IslandThumbnail()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(220, 130);
        // Shape data is untrusted: whatever it holds, nothing is drawn outside the card.
        ClipContents = true;
    }

    /// <summary>The land as the picture draws it (null for a room without shape data).</summary>
    public LandPicture? Land => _land;

    /// <param name="rim">A land made with the rim record gives its notches and bays here (it has no sea; none is drawn either way).</param>
    public void SetShape(IntroSource? shape, IReadOnlyList<RimOpening>? rim = null)
    {
        _shape = shape;
        _land = shape == null ? null : LandPicture.Build(shape, rim);
        QueueRedraw();
    }

    public override void _Draw()
    {
        DrawnStrokes = Array.Empty<LandStroke>();
        DrawnStrokes = LandPicture.Replay(this, _land != null ? _land.Strokes(Size) : LandPicture.StandIn(Size));
    }
}
