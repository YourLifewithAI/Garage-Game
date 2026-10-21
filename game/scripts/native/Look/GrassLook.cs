using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Room;

namespace GarageGame.Native.Look;

/// <summary>
/// Procedural grass (a prototype for the founder, 15 October; branch run2/look-grass-proto). The island's grass copies (the plant set
/// whose prototype is grass: about 234,000 tufts on the 10x garage) were the kit's tuft: eight solid spikes, lit face by face. Here
/// each tuft is built in code, and the meadow grows from a canopy, after Outerra's procedural grass (outerra.blogspot.com, May 2012):
/// <list type="bullet">
/// <item>The canopy: three broad fractal fields over the land say how tall the grass stands, where it thins to bare earth and where it
/// has gone dry. They are baked once per island into a small texture (a cell every 25 cm or so, as Outerra's canopy is), each field
/// turned into its rank so a share of the meadow is bare or dry exactly as Tuning says; a blade reads it at its own root, so the
/// grass's height changes smoothly across the meadow and thins blade by blade toward a bare patch's edge.</item>
/// <item>The meadow floor draws the same canopy (painterly_land.gdshader, the meadow): lighter where the grass stands, warmer where it
/// is dry, bare earth where it is gone. So the floor between the blades and the meadow far away, where no blade is drawn, read as the
/// grass's own top, and a bare patch is bare near and far.</item>
/// <item>Folded blades: each blade of a tuft is a V, two short blades from one root, which doubles how dense short grass looks.</item>
/// <item>The lighter bands keep fewer blades but wider ones, so the grass still covers the ground with distance.</item>
/// <item>One tuft in Tuning.PartsEvery also carries more than turf (after Malmquist's grass, 80.lv 2021): a clump of tall grass with
/// seed heads, a rosette weed, a few flowers and a couple of small stones, drawn as their own sparse copies (PartsMeshes; the copies
/// view fills them from every PartsEvery-th row). Which it shows is chosen per tuft, by its own numbers and the canopy's fourth field
/// (tall grass where it rises, flowers where it is low, as Malmquist subtracts his masks; rosettes in short grass; stones more often
/// near a bare patch); the rest fold into a point and draw nothing. Stones are their own surface (grass_stones.gdshader).</item>
/// </list>
/// grass_blades.gdshader lights a blade as the ground it grows from, colours it from root to tip in the land's own greens, bends it in
/// the wind and takes the season (LandSeason's globals). --look-grass=kit after "--" (or ENFRACTAL_LOOK_GRASS=kit) brings back the kit's
/// tuft and leaves the meadow floor as it was, for a before and after in one build. Every number is in Tuning.
/// </summary>
public static class GrassLook
{
    public static class Tuning
    {
        /// <summary>Folded blades (a V each, two blades from one root) in a tuft, by the band's mesh: the finest first (the copies' near band, to 8 m), the lightest last (to 40 m).</summary>
        public static readonly int[] Pairs = { 96, 40, 16, 7 };
        /// <summary>Rows along each half of a V, by the band's mesh: 2 rows and a tip make 3 triangles (a curved blade); 1 row and a tip, one.</summary>
        public static readonly int[] Rows = { 2, 1, 1, 1 };
        /// <summary>How much wider the blades of each band are than the near band's: fewer blades, each wider, so the grass still covers the ground.</summary>
        public static readonly float[] WidthScale = { 1f, 1.5f, 2.1f, 2.6f };
        /// <summary>How far from the tuft's centre a blade grows (m): neighbouring tufts overlap into a carpet.</summary>
        public const float RootRadiusM = 0.1f;
        /// <summary>
        /// A blade's height (m), before the canopy: knee-high on the 10 cm body, a meadow like Breath of the Wild's (the founder,
        /// 15 October). The copies scale it by about 1.1 and the canopy by ShortestShare to TallestShare.
        /// </summary>
        public const float MinHeightM = 0.009f, MaxHeightM = 0.016f;
        /// <summary>A blade's width at its root (m), in the near band.</summary>
        public const float MinWidthM = 0.0022f, MaxWidthM = 0.0038f;
        /// <summary>How far a blade's tip leans, as a share of its height; and how much the lean points away from the tuft's centre.</summary>
        public const float MinLean = 0.2f, MaxLean = 0.7f, Outward = 0.6f;
        /// <summary>How far the two halves of a folded blade turn apart from its lean (radians).</summary>
        public const float FoldAngle = 0.5f;
        /// <summary>
        /// How far every part of a tuft rises from the copy's origin (m, in the tuft's own frame). The island's exporter seats each copy
        /// a little under the meadow's finest ground (measured on the 10x island, 4,000 tufts: 4.1 to 4.5 mm under it on level ground,
        /// up to 9 mm on slopes), which buried a third to two-thirds of every 1 to 2 cm blade; risen by this, the roots meet the floor.
        /// </summary>
        public const float RootLiftM = 0.0042f;

        /// <summary>
        /// One tuft in this many carries the other parts, drawn as their own sparse copies with their own shader (grass_parts.gdshader),
        /// so the turf's blades do none of the parts' work: every tuft carrying them, most folding them away, had cost the meadow at the
        /// 10 cm eye (about 27,000 tufts at full detail) 4.4 ms of GPU over its 11 ms of turf.
        /// </summary>
        public const int PartsEvery = 3;
        /// <summary>How far the parts' copies are drawn (m to a tile's middle): past it every part has shrunk away (PartsShrinkEndM) even at a tile's near edge.</summary>
        public const float PartsDrawM = 27f;
        /// <summary>A parts tuft's parts, by the band's mesh (finest first): tall blades and seed stalks in its tall clump, rosette leaves, flowers, stones.</summary>
        public static readonly int[] TallBlades = { 9, 7, 6, 6 }, SeedStalks = { 3, 3, 2, 2 }, RosetteLeaves = { 6, 5, 0, 0 }, Flowers = { 4, 4, 3, 3 }, Stones = { 2, 2, 1, 1 };
        /// <summary>How far from a parts tuft's centre its flowers and stones stand (m): wider than a tuft, as its neighbours carry none.</summary>
        public const float PartsRadiusM = 0.15f;
        /// <summary>Tall grass: its blades' height (m: to the 10 cm body's waist and chest), its seed stalks' height, how far apart the clump grows (m).</summary>
        public const float TallMinM = 0.04f, TallMaxM = 0.07f, SeedMinM = 0.06f, SeedMaxM = 0.09f, ClumpRadiusM = 0.035f;
        /// <summary>
        /// The canopy's fourth field (m across), which sets the odds of a parts tuft's parts rather than drawing their edges, so whole
        /// stretches are richer or plainer. The island has about 110 tufts a square metre, so about 36 parts tufts: tall grass grows in up
        /// to TallTufts of them, the most where the field's rank is TallFull and fewest below TallFrom; each of a parts tuft's flowers
        /// blooms in up to FlowerTufts of them, the most where the rank is below FlowersThin and fewest above FlowersGone (more in spring,
        /// few in autumn, none in winter), so flowers and tall grass mostly keep apart. Everywhere keeps PartsBase of the most, so the
        /// metre of meadow before the 10 cm eye (all of the picture below its middle) always has some of both.
        /// </summary>
        public const float PartsM = 2.6f, TallFrom = 0.3f, TallFull = 0.85f, TallTufts = 0.45f, FlowersThin = 0.35f, FlowersGone = 0.85f, FlowerTufts = 0.4f, PartsBase = 0.25f;
        /// <summary>Rosette weeds: in up to this share of the parts tufts, where the grass is short; their leaves' length (m).</summary>
        public const float RosetteShare = 0.12f, RosetteMinM = 0.017f, RosetteMaxM = 0.027f;
        /// <summary>
        /// Flowers: their stalks' height (above the turf), their heads' radius (m: a head 1.2 to 1.5 cm across, so it reads from the 10 cm
        /// eye) and how far a head turns from the sky toward its stalk's lean (radians).
        /// </summary>
        public const float FlowerMinM = 0.03f, FlowerMaxM = 0.052f, FlowerHeadM = 0.0068f, FlowerTiltMin = 0.6f, FlowerTiltMax = 1.15f;
        /// <summary>Stones: their radius (m), the share of a parts tuft's stones that show, and how many times more often near a bare patch.</summary>
        public const float StoneMinM = 0.014f, StoneMaxM = 0.042f, StoneShare = 0.05f, StoneBareBoost = 5f;
        /// <summary>How far tall grass and flowers are drawn (they shrink away from the first to the second, m), and stones.</summary>
        public const float PartsShrinkStartM = 9f, PartsShrinkEndM = 20f, StoneShrinkStartM = 10f, StoneShrinkEndM = 18f;

        /// <summary>The canopy's height field (m across a swell), and the grass's height from its shortest to its tallest, as a share of the blade's own.</summary>
        public const float CanopyM = 3.2f, ShortestShare = 0.65f, TallestShare = 1.3f;
        /// <summary>The bare-earth field (m across a patch), and the share of the meadow that is bare.</summary>
        public const float BareM = 2.6f, BareShare = 0.07f;
        /// <summary>The dry field (m across a patch), the share of the meadow that is dry, and how far the driest patches go toward the dry colour.</summary>
        public const float DryM = 4.5f, DryShare = 0.3f, Driest = 0.55f;
        /// <summary>How soft a bare or dry patch's edge is, in the field's rank (0 to 1).</summary>
        public const float EdgeSoftness = 0.05f;
        /// <summary>The canopy map's cell (m): about Outerra's 30 cm on the 10x island; at most CanopyCells along a side.</summary>
        public const float CanopyCellM = 0.25f;
        public const int CanopyCells = 1024;
        /// <summary>
        /// How far the meadow floor goes toward the grass's own layers where the grass stands: near, the thatch under the blades (a
        /// deep green in the hollows, lighter strokes of fallen blades, straw), so the gaps between blades read as deeper grass rather
        /// than a flat green; far, past the grass's distance, the canopy's top, as the grass is seen from above.
        /// </summary>
        public const float FloorCanopy = 0.85f;
        /// <summary>The thatch under the blades: its deep green (in the hollows) and its straw (dead grass), linear albedo.</summary>
        public static readonly Color Under = new(0.07f, 0.145f, 0.04f);
        public static readonly Color Thatch = new(0.34f, 0.29f, 0.13f);
        /// <summary>How far the low herbs' leaves (daisies, buttercups, ferns: the land shader's grass habit) blend into the canopy's top, so they sit in the meadow rather than dot it.</summary>
        public const float HerbsToCanopy = 0.5f;

        /// <summary>The blade's colour from its root (in the tuft's shade) to its tip in the light (linear albedo): the land's own greens, light and soft.</summary>
        public static readonly Color Root = new(0.2f, 0.38f, 0.1f);
        public static readonly Color Middle = new(0.31f, 0.54f, 0.14f);
        public static readonly Color Tip = new(0.62f, 0.76f, 0.32f);
        /// <summary>The canopy's top seen from above (the floor where grass stands), dry grass (a warm pale yellow-green, never brown) and bare earth (the worn path's warm light earth).</summary>
        public static readonly Color Top = new(0.3f, 0.52f, 0.13f);
        public static readonly Color Dry = new(0.56f, 0.58f, 0.22f);
        public static readonly Color Bare = new(0.42f, 0.33f, 0.21f);
        /// <summary>How dark a blade's root stays in the tuft's own shade (ambient occlusion: 1 none).</summary>
        public const float RootShade = 0.72f;
        /// <summary>
        /// Light and colour that vary clump by clump (after Malmquist's grass): each tuft a little lighter or darker (by up to this
        /// share), short grass warmer and sunnier, tall grass lusher and bluer (multipliers by the canopy's height).
        /// </summary>
        public const float TuftShade = 0.26f;
        public static readonly Vector3 SunTint = new(1.07f, 1.02f, 0.86f), LushTint = new(0.9f, 0.97f, 1.1f);
        /// <summary>Colours of the tuft's other parts (linear albedo): tall grass's pale tips, seed heads, a rosette's leaf from its heart to its tip.</summary>
        public static readonly Color TallTip = new(0.55f, 0.6f, 0.3f), Seed = new(0.55f, 0.47f, 0.3f), RosetteDark = new(0.14f, 0.28f, 0.12f), RosetteLight = new(0.3f, 0.5f, 0.2f);
        /// <summary>Flowers' petals (yellow, white and pink, in patches across the meadow, as Tiny Glade's) and their hearts (linear albedo).</summary>
        public static readonly Color Flower = new(0.95f, 0.72f, 0.08f), FlowerWhite = new(0.9f, 0.88f, 0.8f), FlowerPink = new(0.85f, 0.5f, 0.62f), FlowerHeart = new(0.92f, 0.55f, 0.06f);
        /// <summary>Stones in the grass (linear albedo): their light and dark faces (the harness palette's stone), and their foot in the thatch.</summary>
        public static readonly Color StoneLight = new(0.56f, 0.57f, 0.53f), StoneDark = new(0.3f, 0.31f, 0.3f), StoneFoot = new(0.12f, 0.2f, 0.07f);
        /// <summary>Far blades fade toward the canopy's top, from FadeStartM to FadeEndM.</summary>
        public const float FadeStartM = 1.5f, FadeEndM = 8f, FadeAmount = 0.75f;
        /// <summary>The grass's distance, as Outerra's: blades shrink away from ShrinkStartM to ShrinkEndM, and beyond it the meadow floor alone carries the canopy.</summary>
        public const float ShrinkStartM = 4f, ShrinkEndM = 12f;
        /// <summary>
        /// The land's pastel softening (the preset's paint block gives the meadow floor the same: 0.82 of its chroma, 0.94 of its value
        /// and a 0.03 lift, a quarter of the way), so the blades read as the same paint as the floor they stand on.
        /// </summary>
        public const float PastelChroma = 0.82f, PastelScale = 0.94f, PastelLift = 0.03f, PastelAmount = 0.3f;
        /// <summary>Where along the blade the tip's colour starts.</summary>
        public const float TipStart = 0.5f;
        /// <summary>Small patches of yellow-green and blue-green within the canopy (m across), and how far they push the colour.</summary>
        public const float PatchM = 1.4f, PatchAmount = 0.1f;
        /// <summary>How much a blade is lit as the ground it grows from (1) rather than as its own flat face (0).</summary>
        public const float GroundNormal = 0.65f;
        /// <summary>Light through the blades from behind, as a share of their colour.</summary>
        public const float Backlight = 0.5f;
        /// <summary>The wind: how far a blade's tip sways, as a share of its height; how fast; over how long a gust (m).</summary>
        public const float WindShare = 0.22f, WindSpeed = 1.3f, GustM = 1.6f;
    }

    public const string ShaderPath = "res://shaders/grass_blades.gdshader";
    public const string PartsShaderPath = "res://shaders/grass_parts.gdshader";
    public const string StoneShaderPath = "res://shaders/grass_stones.gdshader";

    /// <summary>The canopy's global shader parameters, read by the blades and by the meadow floor (painterly_land.gdshader), in declaration order.</summary>
    public static readonly IReadOnlyList<(string Name, RenderingServer.GlobalShaderParameterType Type)> Globals = new[]
    {
        ("grass_canopy", RenderingServer.GlobalShaderParameterType.Vec4),
        ("grass_shape", RenderingServer.GlobalShaderParameterType.Vec4),
        ("grass_floor", RenderingServer.GlobalShaderParameterType.Vec4),
        ("grass_top", RenderingServer.GlobalShaderParameterType.Vec3),
        ("grass_dry", RenderingServer.GlobalShaderParameterType.Vec3),
        ("grass_bare", RenderingServer.GlobalShaderParameterType.Vec3),
        ("grass_canopy_area", RenderingServer.GlobalShaderParameterType.Vec4),
        ("grass_canopy_map", RenderingServer.GlobalShaderParameterType.Sampler2D),
        ("grass_under", RenderingServer.GlobalShaderParameterType.Vec3),
        ("grass_thatch", RenderingServer.GlobalShaderParameterType.Vec3),
    };

    private static ShaderMaterial? _material, _partsMaterial, _stoneMaterial;
    private static readonly Dictionary<(int Level, bool Parts), Mesh> Meshes = new();
    private static readonly StringName[] Names = Globals.Select(g => new StringName(g.Name)).ToArray();
    private static bool _registered;
    private static ImageTexture? _canopyMap, _emptyMap;
    private static Vector4 _canopyArea;

    /// <summary>Whether the copies view should draw this set with procedural blades: the grass, unless pinned back to the kit's tuft.</summary>
    public static bool Draws(PlantSet set) => set.Kind == PlantKind.Plant && set.Prototype == "grass" && On;

    /// <summary>Whether the procedural grass (and the canopy on the meadow floor) is on: unless pinned to the kit's tuft.</summary>
    public static bool On => Mode() != "kit";

    /// <summary>The pin: --look-grass=kit|blades after "--", or ENFRACTAL_LOOK_GRASS.</summary>
    public static string Mode()
    {
        foreach (var argument in OS.GetCmdlineUserArgs())
            if (argument.StartsWith("--look-grass=", StringComparison.Ordinal)) return argument["--look-grass=".Length..];
        var value = OS.GetEnvironment("ENFRACTAL_LOOK_GRASS");
        return string.IsNullOrWhiteSpace(value) ? "blades" : value.Trim();
    }

    /// <summary>
    /// The canopy into the globals the blades and the meadow floor read (registered at run time on the first call, as LandSeason's are;
    /// all zero leaves the meadow floor as it was). LandSeason.Apply calls this once per moment.
    /// </summary>
    public static void ApplyGlobals()
    {
        Register();
        // On only once an island's canopy is baked (a room without grass, or the kit's tufts, leaves the meadow floor as it was).
        RenderingServer.GlobalShaderParameterSet(Names[0], new Vector4(Tuning.BareShare, Tuning.DryShare, Tuning.EdgeSoftness, On && _canopyMap != null ? 1f : 0f));
        RenderingServer.GlobalShaderParameterSet(Names[1], new Vector4(Tuning.ShortestShare, Tuning.TallestShare, Tuning.Driest, 0f));
        RenderingServer.GlobalShaderParameterSet(Names[2], new Vector4(Tuning.FloorCanopy, Tuning.HerbsToCanopy, Tuning.ShrinkStartM, Tuning.ShrinkEndM));
        RenderingServer.GlobalShaderParameterSet(Names[3], new Vector3(Tuning.Top.R, Tuning.Top.G, Tuning.Top.B));
        RenderingServer.GlobalShaderParameterSet(Names[4], new Vector3(Tuning.Dry.R, Tuning.Dry.G, Tuning.Dry.B));
        RenderingServer.GlobalShaderParameterSet(Names[5], new Vector3(Tuning.Bare.R, Tuning.Bare.G, Tuning.Bare.B));
        RenderingServer.GlobalShaderParameterSet(Names[6], _canopyArea);
        RenderingServer.GlobalShaderParameterSet(Names[7], (_canopyMap ?? EmptyMap()).GetRid());
        RenderingServer.GlobalShaderParameterSet(Names[8], new Vector3(Tuning.Under.R, Tuning.Under.G, Tuning.Under.B));
        RenderingServer.GlobalShaderParameterSet(Names[9], new Vector3(Tuning.Thatch.R, Tuning.Thatch.G, Tuning.Thatch.B));
    }

    private static void Register()
    {
        if (_registered) return;
        _registered = true;
        if (Globals.All(g => ProjectSettings.HasSetting("shader_globals/" + g.Name))) return;
        for (var i = 0; i < Globals.Count; i++)
        {
            Variant initial = Globals[i].Type switch
            {
                RenderingServer.GlobalShaderParameterType.Vec4 => Vector4.Zero,
                RenderingServer.GlobalShaderParameterType.Sampler2D => EmptyMap().GetRid(),
                _ => Vector3.Zero,
            };
            RenderingServer.GlobalShaderParameterAdd(Names[i], Globals[i].Type, initial);
        }
    }

    private static ImageTexture EmptyMap()
    {
        if (_emptyMap != null) return _emptyMap;
        var image = Image.CreateEmpty(1, 1, false, Image.Format.Rgb8);
        image.Fill(new Color(0.5f, 1f, 0f));
        return _emptyMap = ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// Bakes an island's canopy (once, as its copies view is made): over the room's bounds, a cell every CanopyCellM (at most
    /// CanopyCells a side), three fractal fields (Godot's FastNoiseLite, fixed seeds, so the same island always grows the same meadow),
    /// each turned into its rank across the island so it is even from 0 to 1: red how tall the grass stands, green how surely it stands
    /// (bare below BareShare), blue how dry it is (dry above 1 - DryShare), alpha the tuft's parts (tall grass high, flowers in a band).
    /// The blades, the stones and the meadow floor read it at a point's x and z.
    /// </summary>
    public static void BuildCanopy(RoomData room)
    {
        if (!On) return;
        var bounds = room.Bounds;
        var cell = Math.Max(Tuning.CanopyCellM, Math.Max(bounds.Size.X, bounds.Size.Z) / Tuning.CanopyCells);
        var width = Math.Max(2, (int)Math.Ceiling(bounds.Size.X / cell) + 1);
        var depth = Math.Max(2, (int)Math.Ceiling(bounds.Size.Z / cell) + 1);
        FastNoiseLite Field(int seed, float scaleM) => new()
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Seed = seed, Frequency = 1f / scaleM,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm, FractalOctaves = 3, FractalGain = 0.5f, FractalLacunarity = 2.1f,
        };
        var fields = new[] { Field(31, Tuning.CanopyM), Field(57, Tuning.BareM), Field(83, Tuning.DryM), Field(109, Tuning.PartsM) };
        var values = new float[4][];
        for (var f = 0; f < 4; f++)
        {
            var raw = new float[width * depth];
            for (var z = 0; z < depth; z++)
                for (var x = 0; x < width; x++)
                    raw[z * width + x] = fields[f].GetNoise2D(bounds.Position.X + x * cell, bounds.Position.Z + z * cell);
            // Each field as its rank across the island, so its shares are exact.
            var order = Enumerable.Range(0, raw.Length).OrderBy(i => raw[i]).ToArray();
            var ranked = new float[order.Length];
            for (var r = 0; r < order.Length; r++) ranked[order[r]] = (r + 0.5f) / order.Length;
            values[f] = ranked;
        }
        var image = Image.CreateEmpty(width, depth, false, Image.Format.Rgba8);
        for (var z = 0; z < depth; z++)
            for (var x = 0; x < width; x++)
            {
                var i = z * width + x;
                image.SetPixel(x, z, new Color(values[0][i], values[1][i], values[2][i], values[3][i]));
            }
        _canopyMap = ImageTexture.CreateFromImage(image);
        // x0, z0 and one over the map's width and depth (m): a point's uv is (xz - (x0, z0)) times them.
        _canopyArea = new Vector4(bounds.Position.X - 0.5f * cell, bounds.Position.Z - 0.5f * cell, 1f / (width * cell), 1f / (depth * cell));
        ApplyGlobals();
    }

    /// <summary>The tuft (turf alone) for each of a set's meshes (finest first), as many as the set has, so its bands stay as they were.</summary>
    public static Mesh[] TuftMeshes(int count) => BandMeshes(count, parts: false);

    /// <summary>The parts tuft (its tall grass, rosette, flowers and stones, no turf) for each of a set's meshes, drawn for one row in Tuning.PartsEvery.</summary>
    public static Mesh[] PartsMeshes(int count) => BandMeshes(count, parts: true);

    private static Mesh[] BandMeshes(int count, bool parts)
    {
        var meshes = new Mesh[count];
        for (var i = 0; i < count; i++)
        {
            var level = Math.Min(i == count - 1 ? Tuning.Pairs.Length - 1 : i, Tuning.Pairs.Length - 1);
            if (!Meshes.TryGetValue((level, parts), out var mesh)) Meshes[(level, parts)] = mesh = Tuft(level, parts);
            meshes[i] = mesh;
        }
        return meshes;
    }

    /// <summary>The kinds of a tuft's parts (CUSTOM1.x): turf, tall blade, seed stalk and head, rosette leaf, flower stalk and head, stone.</summary>
    public enum Part { Turf = 0, Tall = 1, Seed = 2, Rosette = 3, Flower = 4, Stone = 6 }

    /// <summary>
    /// One tuft for a band (level 0 the finest): its turf as folded blades, or (parts) two surfaces, its other parts (a clump of tall
    /// blades and seed stalks with their heads; a rosette's leaves; flowers on stalks) and its stones. Every vertex carries UV (across,
    /// how far up: 0 root, 1 tip), UV2 (its part's number, its height), CUSTOM0 (its part's root x and z in the tuft, its height or
    /// size, its number) and CUSTOM1 (its kind, 1 on a head), so the shaders can read the canopy at the root, choose the part per tuft
    /// and grow or fold it about its root. A fixed seed: every tuft is the same and every run too.
    /// </summary>
    public static Mesh Tuft(int level, bool parts = false)
    {
        var mesh = new ArrayMesh();
        var random = new Random(1501);
        float Next(float low, float high) => low + (float)random.NextDouble() * (high - low);
        Vector3 Spot(float radius) { var r = radius * Mathf.Sqrt(Next(0f, 1f)); var a = Next(0f, Mathf.Tau); return new Vector3(r * Mathf.Cos(a), 0f, r * Mathf.Sin(a)); }

        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        tool.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);
        if (parts) tool.SetCustomFormat(1, SurfaceTool.CustomFormat.RgbaFloat);
        var index = 0;
        void Vertex(Vector3 at, Vector2 uv, Vector3 normal, Vector3 root, float size, float number, Part kind, bool head)
        {
            tool.SetUV(uv);
            tool.SetUV2(new Vector2(number, size));
            tool.SetCustom(0, new Color(root.X, root.Z, size, number));
            if (parts) tool.SetCustom(1, new Color((float)kind, head ? 1f : 0f, 0f, 0f));
            tool.SetNormal(normal);
            tool.SetColor(Colors.White);
            tool.AddVertex(at);
        }
        // A tapered strip from its root to a single tip vertex, curving as it leans (its lean grows with the square of the height).
        void Strip(Vector3 root, Vector3 lean, float height, float width, float reach, int rows, float number, Part kind, float sideTurn)
        {
            var side = lean.Cross(Vector3.Up).Normalized().Rotated(Vector3.Up, sideTurn);
            Vector3 At(float t) => root + Vector3.Up * height * t * (1f - 0.12f * t * t) + lean * reach * t * t;
            for (var row = 0; row < rows; row++)
            {
                var t = (float)row / rows;
                var centre = At(t);
                var half = 0.5f * width * Mathf.Pow(1f - t, 0.8f);
                var normal = side.Cross((At(t + 0.05f) - centre).Normalized()).Normalized();
                Vertex(centre - side * half, new Vector2(0f, t), normal, root, height, number, kind, false);
                Vertex(centre + side * half, new Vector2(1f, t), normal, root, height, number, kind, false);
            }
            Vertex(At(1f), new Vector2(0.5f, 1f), side.Cross((At(1f) - At(0.95f)).Normalized()).Normalized(), root, height, number, kind, false);
            for (var row = 0; row < rows - 1; row++)
            {
                var a0 = index + row * 2;
                tool.AddIndex(a0); tool.AddIndex(a0 + 2); tool.AddIndex(a0 + 1);
                tool.AddIndex(a0 + 1); tool.AddIndex(a0 + 2); tool.AddIndex(a0 + 3);
            }
            var last = index + (rows - 1) * 2;
            tool.AddIndex(last); tool.AddIndex(index + rows * 2); tool.AddIndex(last + 1);
            index += rows * 2 + 1;
        }
        // A flat diamond (a leaf or a seed head's face): from its base along a direction, widest at its middle.
        void Diamond(Vector3 basePoint, Vector3 along, Vector3 across, float length, float width, Vector3 root, float size, float number, Part kind, bool head, float lift)
        {
            var normal = across.Cross(along).Normalized();
            if (normal.Y < 0f) normal = -normal;
            Vertex(basePoint, new Vector2(0.5f, head ? 1f : 0f), normal, root, size, number, kind, head);
            Vertex(basePoint + along * length * 0.45f - across * width * 0.5f + Vector3.Up * lift * 0.5f, new Vector2(0f, head ? 1f : 0.45f), normal, root, size, number, kind, head);
            Vertex(basePoint + along * length * 0.45f + across * width * 0.5f + Vector3.Up * lift * 0.5f, new Vector2(1f, head ? 1f : 0.45f), normal, root, size, number, kind, head);
            Vertex(basePoint + along * length + Vector3.Up * lift, new Vector2(0.5f, 1f), normal, root, size, number, kind, head);
            tool.AddIndex(index); tool.AddIndex(index + 2); tool.AddIndex(index + 1);
            tool.AddIndex(index + 1); tool.AddIndex(index + 2); tool.AddIndex(index + 3);
            index += 4;
        }

        if (!parts)
        {
            // Turf: folded blades, a V each.
            var pairs = Tuning.Pairs[level];
            var rows = Tuning.Rows[level];
            for (var b = 0; b < pairs; b++)
            {
                // Spread the roots evenly over the tuft (a sunflower spiral), each nudged a little.
                var r = Tuning.RootRadiusM * Mathf.Sqrt((b + 0.5f) / pairs) * Next(0.8f, 1.05f);
                var a = b * 2.39996f + Next(-0.3f, 0.3f);
                var root = new Vector3(r * Mathf.Cos(a), 0f, r * Mathf.Sin(a));
                var outward = r > 1e-4f ? new Vector3(root.X, 0f, root.Z).Normalized() : Vector3.Right;
                var free = Vector3.Right.Rotated(Vector3.Up, Next(0f, Mathf.Tau));
                var lean = (outward * Tuning.Outward + free * (1f - Tuning.Outward)).Normalized();
                var height = Next(Tuning.MinHeightM, Tuning.MaxHeightM);
                var width = Next(Tuning.MinWidthM, Tuning.MaxWidthM) * Tuning.WidthScale[level];
                var number = Next(0f, 1f);
                foreach (var turn in new[] { -Tuning.FoldAngle, Tuning.FoldAngle })
                {
                    var halfHeight = height * Next(0.8f, 1f);
                    Strip(root, lean.Rotated(Vector3.Up, turn + Next(-0.2f, 0.2f)).Normalized(), halfHeight, width, halfHeight * Next(Tuning.MinLean, Tuning.MaxLean), rows, number, Part.Turf, Next(-0.4f, 0.4f));
                }
            }
        }

        if (parts)
        {
            // A clump of tall grass and its seed stalks, each stalk carrying a head of two crossed diamonds.
            var clump = Spot(Tuning.RootRadiusM * 0.6f);
            var tallRows = level == 0 ? 2 : 1;
            for (var b = 0; b < Tuning.TallBlades[level]; b++)
            {
                var root = clump + Spot(Tuning.ClumpRadiusM);
                var lean = (root - clump + Spot(0.01f)).Normalized();
                if (!lean.IsFinite() || lean.LengthSquared() < 0.5f) lean = Vector3.Right.Rotated(Vector3.Up, Next(0f, Mathf.Tau));
                var height = Next(Tuning.TallMinM, Tuning.TallMaxM);
                Strip(root, lean, height, Next(0.0025f, 0.004f) * Tuning.WidthScale[level], height * Next(0.15f, 0.45f), tallRows, Next(0f, 1f), Part.Tall, Next(-0.4f, 0.4f));
            }
            for (var b = 0; b < Tuning.SeedStalks[level]; b++)
            {
                var root = clump + Spot(Tuning.ClumpRadiusM * 0.5f);
                var lean = Vector3.Right.Rotated(Vector3.Up, Next(0f, Mathf.Tau));
                var height = Next(Tuning.SeedMinM, Tuning.SeedMaxM);
                var reach = height * Next(0.1f, 0.3f);
                var number = Next(0f, 1f);
                Strip(root, lean, height, 0.0011f * Tuning.WidthScale[level], reach, tallRows, number, Part.Seed, 0f);
                var tip = root + Vector3.Up * height * 0.88f + lean * reach;
                var headLength = Next(0.008f, 0.013f);
                var up = (Vector3.Up + lean * 0.35f).Normalized();
                Diamond(tip, up, lean.Cross(Vector3.Up).Normalized(), headLength, 0.0026f, root, height, number, Part.Seed, true, 0f);
                Diamond(tip, up, lean, headLength, 0.0026f, root, height, number, Part.Seed, true, 0f);
            }

            // A rosette weed: broad leaves lying almost flat from its heart.
            if (Tuning.RosetteLeaves[level] > 0)
            {
                // Near the tuft's centre, where the tuft sits on the ground, so a slope never buries its flat leaves.
                var heart = Spot(Tuning.RootRadiusM * 0.25f);
                var length = Next(Tuning.RosetteMinM, Tuning.RosetteMaxM);
                var leaves = Tuning.RosetteLeaves[level];
                var start = Next(0f, Mathf.Tau);
                for (var l = 0; l < leaves; l++)
                {
                    var along = Vector3.Right.Rotated(Vector3.Up, start + l * Mathf.Tau / leaves + Next(-0.25f, 0.25f));
                    Diamond(heart + Vector3.Up * 0.002f, along, along.Cross(Vector3.Up).Normalized(), length * Next(0.75f, 1.05f), length * 0.45f, heart, length, Next(0f, 1f), Part.Rosette, false, length * Next(0.25f, 0.45f));
                }
            }

            // Flowers: a stalk and a head (a star of petals), the head turned toward its lean by FlowerTilt: a head facing the sky is a
            // sliver among the blades from the 10 cm eye, one turned to the side shows its face.
            for (var f = 0; f < Tuning.Flowers[level]; f++)
            {
                var root = Spot(Tuning.PartsRadiusM);
                var lean = Vector3.Right.Rotated(Vector3.Up, Next(0f, Mathf.Tau));
                var height = Next(Tuning.FlowerMinM, Tuning.FlowerMaxM);
                var reach = height * Next(0.05f, 0.2f);
                var number = Next(0f, 1f);
                Strip(root, lean, height, 0.0012f * Tuning.WidthScale[level], reach, 1, number, Part.Flower, 0f);
                var centre = root + Vector3.Up * height * 0.88f + lean * reach;
                var tiltAxis = lean.Cross(Vector3.Up).Normalized();
                var tilt = -Next(Tuning.FlowerTiltMin, Tuning.FlowerTiltMax);
                Vector3 Face(Vector3 v) => v.Rotated(tiltAxis, tilt);
                // A star of petals, cupped a little: its rim runs out to a petal's tip and back in to the notch between two.
                var petals = level == 0 ? 6 : 5;
                var radius = Tuning.FlowerHeadM * Next(0.85f, 1.15f);
                var turn = Next(0f, Mathf.Tau);
                var first = index;
                var rimCount = petals * 2;
                // A head's UV.x runs from its heart (0) to its petals' tips (1).
                Vertex(centre + Face(Vector3.Up * 0.0008f), new Vector2(0f, 1f), Face(Vector3.Up), root, height, number, Part.Flower, true);
                for (var k = 0; k < rimCount; k++)
                {
                    var tipOut = k % 2 == 0;
                    var reachOut = tipOut ? 1f : 0.42f;
                    var rim = Vector3.Right.Rotated(Vector3.Up, turn + k * Mathf.Tau / rimCount) * radius * reachOut;
                    Vertex(centre + Face(rim + Vector3.Up * radius * (tipOut ? 0.4f : 0.15f)), new Vector2(reachOut, 1f), Face((Vector3.Up * 2f - rim.Normalized()).Normalized()), root, height, number, Part.Flower, true);
                }
                for (var k = 0; k < rimCount; k++) { tool.AddIndex(first); tool.AddIndex(first + 1 + (k + 1) % rimCount); tool.AddIndex(first + 1 + k); }
                index += rimCount + 1;
            }
        }
        tool.SetMaterial(parts ? PartsMaterial() : Material());
        tool.Commit(mesh);

        if (parts)
        {
            // Stones: faceted (an icosahedron near, an octahedron far, their corners pulled about), squashed, half sunk, each its own
        // surface's part (grass_stones.gdshader).
            var stones = new SurfaceTool();
            stones.Begin(Mesh.PrimitiveType.Triangles);
            stones.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);
            var (corners, faces) = level == 0 ? Icosahedron() : Octahedron();
            for (var n = 0; n < Tuning.Stones[level]; n++)
            {
                var centre = Spot(Tuning.PartsRadiusM * 0.8f);
                var radius = Next(Tuning.StoneMinM, Tuning.StoneMaxM);
                var squash = Next(0.55f, 0.85f);
                var yaw = Next(0f, Mathf.Tau);
                var number = Next(0f, 1f);
                var jitter = corners.Select(_ => Next(0.78f, 1.12f)).ToArray();
                Vector3 Corner(int c)
                {
                    var v = corners[c] * jitter[c];
                    v = new Vector3(v.X * radius, v.Y * radius * squash, v.Z * radius * Next(0.85f, 1f)).Rotated(Vector3.Up, yaw);
                    return centre + v + Vector3.Down * radius * squash * 0.15f;
                }
                var placed = Enumerable.Range(0, corners.Length).Select(Corner).ToArray();
                var top = placed.Max(v => v.Y);
                var bottom = placed.Min(v => v.Y);
                foreach (var face in faces)
                {
                    var (p0, p1, p2) = (placed[face.A], placed[face.B], placed[face.C]);
                    var normal = (p1 - p0).Cross(p2 - p0).Normalized();
                    if (normal.Dot((p0 + p1 + p2) / 3f - centre) < 0f) { (p1, p2) = (p2, p1); normal = -normal; }
                    var tone = Next(0f, 1f);
                    foreach (var v in new[] { p0, p2, p1 })
                    {
                        stones.SetUV(new Vector2(tone, (v.Y - bottom) / Math.Max(top - bottom, 1e-5f)));
                        stones.SetCustom(0, new Color(centre.X, centre.Z, radius, number));
                        stones.SetNormal(normal);
                        stones.AddVertex(v);
                    }
                }
            }
            stones.SetMaterial(StoneMaterial());
            stones.Commit(mesh);
        }
        return mesh;
    }

    private static (Vector3[] Corners, (int A, int B, int C)[] Faces) Icosahedron()
    {
        var t = (1f + Mathf.Sqrt(5f)) / 2f;
        var corners = new[]
        {
            new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
            new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
            new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
        }.Select(v => v.Normalized()).ToArray();
        var faces = new[]
        {
            (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11), (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
            (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9), (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1),
        };
        return (corners, faces);
    }

    private static (Vector3[] Corners, (int A, int B, int C)[] Faces) Octahedron()
    {
        var corners = new[] { Vector3.Right, Vector3.Left, Vector3.Up, Vector3.Down, Vector3.Back, Vector3.Forward };
        var faces = new[] { (0, 2, 4), (4, 2, 1), (1, 2, 5), (5, 2, 0), (0, 4, 3), (4, 1, 3), (1, 5, 3), (5, 0, 3) };
        return (corners, faces);
    }

    /// <summary>The stones' material, one for every tuft.</summary>
    public static ShaderMaterial StoneMaterial()
    {
        if (_stoneMaterial != null) return _stoneMaterial;
        var material = new ShaderMaterial { Shader = GD.Load<Shader>(StoneShaderPath), ResourceName = "stones in the grass" };
        material.SetShaderParameter("stone_light", Tuning.StoneLight);
        material.SetShaderParameter("stone_dark", Tuning.StoneDark);
        material.SetShaderParameter("stone_foot", Tuning.StoneFoot);
        material.SetShaderParameter("stone_parts", new Vector4(Tuning.StoneShare, Tuning.StoneBareBoost, Tuning.StoneShrinkStartM, Tuning.StoneShrinkEndM));
        material.SetShaderParameter("root_lift", Tuning.RootLiftM);
        material.SetShaderParameter("pastel", new Vector4(Tuning.PastelChroma, Tuning.PastelScale, Tuning.PastelLift, Tuning.PastelAmount));
        return _stoneMaterial = material;
    }

    /// <summary>The turf's material, one for every tuft.</summary>
    public static ShaderMaterial Material() => _material ??= Blades(ShaderPath, "procedural grass", parts: false);

    /// <summary>The parts tufts' material (tall grass, rosettes, flowers), one for every parts tuft.</summary>
    public static ShaderMaterial PartsMaterial() => _partsMaterial ??= Blades(PartsShaderPath, "procedural grass parts", parts: true);

    private static ShaderMaterial Blades(string shader, string name, bool parts)
    {
        var material = new ShaderMaterial { Shader = GD.Load<Shader>(shader), ResourceName = name };
        material.SetShaderParameter("root_color", Tuning.Root);
        material.SetShaderParameter("middle_color", Tuning.Middle);
        material.SetShaderParameter("tip_color", Tuning.Tip);
        material.SetShaderParameter("tip_start", Tuning.TipStart);
        material.SetShaderParameter("root_shade", Tuning.RootShade);
        material.SetShaderParameter("root_lift", Tuning.RootLiftM);
        material.SetShaderParameter("tuft_shade", Tuning.TuftShade);
        material.SetShaderParameter("sun_tint", Tuning.SunTint);
        material.SetShaderParameter("lush_tint", Tuning.LushTint);
        if (parts)
        {
            material.SetShaderParameter("tall_tip_color", Tuning.TallTip);
            material.SetShaderParameter("seed_color", Tuning.Seed);
            material.SetShaderParameter("rosette_dark", Tuning.RosetteDark);
            material.SetShaderParameter("rosette_light", Tuning.RosetteLight);
            material.SetShaderParameter("flower_color", Tuning.Flower);
            material.SetShaderParameter("flower_white", Tuning.FlowerWhite);
            material.SetShaderParameter("flower_pink", Tuning.FlowerPink);
            material.SetShaderParameter("flower_heart", Tuning.FlowerHeart);
            material.SetShaderParameter("tall_parts", new Vector4(Tuning.TallFrom, Tuning.TallFull, Tuning.TallTufts, Tuning.PartsBase));
            material.SetShaderParameter("flower_parts", new Vector4(Tuning.FlowersThin, Tuning.FlowersGone, Tuning.FlowerTufts, Tuning.PartsBase));
            material.SetShaderParameter("rosette_share", Tuning.RosetteShare);
            material.SetShaderParameter("parts_reach", new Vector2(Tuning.PartsShrinkStartM, Tuning.PartsShrinkEndM));
        }
        material.SetShaderParameter("fade_start_m", Tuning.FadeStartM);
        material.SetShaderParameter("fade_end_m", Tuning.FadeEndM);
        material.SetShaderParameter("fade_amount", Tuning.FadeAmount);
        material.SetShaderParameter("shrink_start_m", Tuning.ShrinkStartM);
        material.SetShaderParameter("shrink_end_m", Tuning.ShrinkEndM);
        material.SetShaderParameter("pastel", new Vector4(Tuning.PastelChroma, Tuning.PastelScale, Tuning.PastelLift, Tuning.PastelAmount));
        material.SetShaderParameter("patch_m", Tuning.PatchM);
        material.SetShaderParameter("patch_amount", Tuning.PatchAmount);
        material.SetShaderParameter("ground_normal", Tuning.GroundNormal);
        material.SetShaderParameter("backlight", Tuning.Backlight);
        material.SetShaderParameter("wind_axis", new Vector3(0.7071f, 0f, 0.7071f));
        material.SetShaderParameter("wind_share", Tuning.WindShare);
        material.SetShaderParameter("wind_speed", Tuning.WindSpeed);
        material.SetShaderParameter("gust_m", Tuning.GustM);
        return material;
    }
}
