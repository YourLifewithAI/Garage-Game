using System;
using GarageGame.Native.Room;

namespace GarageGame.Native.Look;

/// <summary>One distance band of a plant set's copies: which of its meshes (0 full detail, then its lods) is drawn from BeginM to EndM (0: no end), and whether it casts shadows.</summary>
public readonly record struct CopyBand(int Mesh, float BeginM, float EndM, bool CastsShadow);

/// <summary>
/// How far the 10x world is drawn and in which detail (packet S6; the numbers are the look's to tune, S9): each plant set's copies
/// out to a range set by its kind and size, its lighter meshes further out, shadows only from near copies; the terrain one level
/// per 16 m tile by distance. Every switch is a visibility range measured from the camera to the middle of a tile's bounds, with
/// a hysteresis margin, so neighbouring bands swap at one distance whichever way the camera moves (never both, never neither).
/// The defaults follow the run's recommendation to the founder (docs/runs/RUN-2-TEN-TIMES.md): small plants to 40-60 m, trees
/// to about 150 m. The data names no ranges; these numbers are the game's.
/// </summary>
public static class DrawDistances
{
    /// <summary>Plants no taller than SmallPlantHeightM (a 3 cm tuft is under a pixel there) are drawn to SmallPlantM, taller ones to PlantM.</summary>
    public const float SmallPlantM = 40f, PlantM = 60f, SmallPlantHeightM = 0.08f;
    public const float StoneM = 80f;
    public const float TreeM = 150f;
    /// <summary>Copies cast shadows only nearer than this: past it a 10 cm plant's shadow is a few pixels, and 350,000 casters cost the shadow passes.</summary>
    public const float CopyShadowM = 20f;
    /// <summary>
    /// Trees cast shadows as far as the sun's shadows reach on the biggest island (ViewDistances.MaxShadowM, S9): a tree is ten to
    /// twenty body heights, so its shadow on a meadow 50 m away is plain to see; there are only about 5,000 of them.
    /// </summary>
    public const float TreeShadowM = ViewDistances.MaxShadowM;

    /// <summary>
    /// Small plants and stones (no taller than SmallPlantHeightM: the 10x garage's grass, flowers, ferns and heather, 2 to 4 cm) are
    /// drawn in full detail, casting shadows, only this near (S9): at 8 m a 3 cm tuft is three pixels tall and its shadow one or two,
    /// so a lighter mesh with no shadow looks the same and each copy is held in two distance bands instead of four.
    /// </summary>
    public const float SmallDetailM = 8f;
    /// <summary>How far a set with three or more meshes keeps its full detail, as a fraction of its range (trees: TreeDetailFraction).</summary>
    public const float DetailFraction = 0.2f, TreeDetailFraction = 0.12f;

    /// <summary>How far a kind of copy casts shadows: trees to TreeShadowM, small plants and stones to SmallDetailM, the rest to CopyShadowM.</summary>
    public static float CopyShadowRangeM(PlantKind kind, float typicalHeightM = float.PositiveInfinity) =>
        kind == PlantKind.Tree ? TreeShadowM : typicalHeightM <= SmallPlantHeightM ? SmallDetailM : CopyShadowM;
    /// <summary>Hysteresis at every copy band's edge.</summary>
    public const float CopyMarginM = 1f;
    /// <summary>
    /// A set's tile is filled once the nearest focus (the camera or an avatar) comes within its range and this much more, so it is
    /// drawn before it can show, and emptied again only past its range and CopyReleaseM (S12b: only copies in reach are held).
    /// </summary>
    public const float CopyBuildLeadM = 4f, CopyReleaseM = 10f;
    /// <summary>Where each lighter mesh takes over, as fractions of the set's range, for a set with 1 to 4 meshes.</summary>
    private static readonly float[][] DetailEnds =
    {
        new[] { 1f },
        new[] { 0.25f, 1f },
        new[] { 0.15f, 0.4f, 1f },
        new[] { 0.12f, 0.25f, 0.5f, 1f },
    };

    /// <summary>
    /// Where each terrain level ends (0: drawn to the camera's far plane), for 1 to 4 levels, measured to a 16 m tile's middle.
    /// Level 0 reaches past the tiles round the camera (a tile's middle is at most 11.3 m from a body standing on it, its
    /// neighbours' at most 34 m), so the ground under every nearby foot is drawn as it collides.
    /// </summary>
    private static readonly float[][] TerrainEnds =
    {
        new[] { 0f },
        new[] { 48f, 0f },
        new[] { 48f, 112f, 0f },
        new[] { 40f, 80f, 160f, 0f },
    };
    public const float TerrainMarginM = 4f;

    /// <summary>The main thread's time for building copies each frame (PlantCopiesView), so a 350,000-copy island arrives over a few seconds without a hitch.</summary>
    public const double BuildBudgetMs = 2.5;
    /// <summary>Copies' collision within this distance of a spawn is built before the bodies are placed; the rest follows nearest first.</summary>
    public const float NearCollisionM = 16f;

    /// <summary>How far a set's copies are drawn: by kind, and for plants by their typical height (the mesh's height times a typical y scale).</summary>
    public static float CopyRangeM(PlantKind kind, float typicalHeightM) => kind switch
    {
        PlantKind.Tree => TreeM,
        PlantKind.Stone => StoneM,
        _ => typicalHeightM <= SmallPlantHeightM ? SmallPlantM : PlantM,
    };

    /// <summary>
    /// A set's bands. A tile's copies are held once, in the one MultiMesh of the band its distance chooses (PlantCopiesView, S12b;
    /// S9 held them once per band), and a band change fills the tile again, so the bands are as few as the look allows (S9):
    /// <list type="bullet">
    /// <item>small plants and stones with two or more meshes: two bands, full detail with shadows to SmallDetailM, the lightest mesh
    /// beyond;</item>
    /// <item>any other set with three or more meshes: three bands, full detail to a fraction of the range, its first lighter mesh to
    /// its shadow range, the lightest beyond, the shadow's edge on a band's edge so no band is split for it;</item>
    /// <item>one or two meshes: each mesh from where the one before ends, the band that crosses the shadow range split there.</item>
    /// </list>
    /// </summary>
    public static CopyBand[] CopyBands(PlantKind kind, float typicalHeightM, int meshes)
    {
        var range = CopyRangeM(kind, typicalHeightM);
        var shadow = CopyShadowRangeM(kind, typicalHeightM);
        var small = kind != PlantKind.Tree && typicalHeightM <= SmallPlantHeightM;
        if (small && meshes >= 2)
            return new[] { new CopyBand(0, 0f, SmallDetailM, true), new CopyBand(meshes - 1, SmallDetailM, range, false) };
        if (meshes >= 3)
        {
            var near = range * (kind == PlantKind.Tree ? TreeDetailFraction : DetailFraction);
            if (shadow > near && shadow < range)
                return new[] { new CopyBand(0, 0f, near, true), new CopyBand(1, near, shadow, true), new CopyBand(meshes - 1, shadow, range, false) };
            return new[] { new CopyBand(0, 0f, near, near <= shadow), new CopyBand(meshes - 1, near, range, range <= shadow) };
        }
        var ends = DetailEnds[Math.Clamp(meshes, 1, DetailEnds.Length) - 1];
        var bands = new System.Collections.Generic.List<CopyBand>();
        var begin = 0f;
        for (var mesh = 0; mesh < ends.Length; mesh++)
        {
            var end = ends[mesh] * range;
            if (begin < shadow && end > shadow)
            {
                bands.Add(new CopyBand(mesh, begin, shadow, true));
                bands.Add(new CopyBand(mesh, shadow, end, false));
            }
            else bands.Add(new CopyBand(mesh, begin, end, end <= shadow));
            begin = end;
        }
        return bands.ToArray();
    }

    /// <summary>The distances a terrain level is drawn between (an end of 0: to the far plane).</summary>
    public static (float BeginM, float EndM) TerrainBand(int level, int levels)
    {
        var ends = TerrainEnds[Math.Clamp(levels, 1, TerrainEnds.Length) - 1];
        level = Math.Clamp(level, 0, ends.Length - 1);
        return (level == 0 ? 0f : ends[level - 1], ends[level]);
    }
}
