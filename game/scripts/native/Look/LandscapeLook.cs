using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GarageGame.Native.Look;

/// <summary>The marks painterly_land.gdshader knows; the values match its K_ constants.</summary>
public enum LandKind
{
    None = 0, Meadow = 1, Soil = 2, Path = 3, Gravel = 4, Scree = 5, Rock = 6, Cliff = 7, Moss = 8, Snow = 9, StillWater = 10,
    FlowingWater = 11, Foliage = 12, Bark = 13, Timber = 14, Wood = 15, Masonry = 16, Roof = 17, Cloth = 18, Metal = 19, Stone = 20,
}

/// <summary>
/// How one harness role is painted: its marks (kind, two mark sizes in metres, stroke stretch and axis, how strongly tone
/// varies), how it takes light (wrap, warm terminator, sheen, a soft sky rim for water) and its surface (roughness, metallic,
/// highlight, normal strength, softening toward a pastel, calm, flow speed for water).
/// </summary>
public sealed record LandRole(
    LandKind Kind, float ScaleM, float Scale2M, float Stretch, Vector3 Axis, float Variation,
    float Wrap, float Warmth, float Sheen, float Specular, float Roughness, float Metallic,
    float Softening, float NormalStrength, float Calm = 0f, float RampGain = 1f, float FlowSpeed = 0f, float SkyRim = 0f, Vector3? Glaze = null);

/// <summary>
/// The landscape in the game (Run 2, Lane L): how the shared harness's twenty material roles are painted when a
/// landscape package's GLBs arrive in a room. The exporter writes one primitive per harness role (the glTF material is
/// named after the role and carries extras role, role_base_color, blend_roles and colors_baked), bakes each vertex's tint
/// and every soft role blend (grass to soil, rock to snow) into COLOR_0, and divides that by the material's colour
/// envelope. Godot's importer keeps the material name and the extras (as the "extras" metadata), the vertex colour and the
/// envelope as the material's albedo, so the room's own look pass, which paints a role over the material's albedo alone,
/// used to show the envelope: a pale cream for every blended ground role. The land keeps the vertex colour inside the
/// painterly treatment and gives each harness role its own marks.
///
/// A captured room's materials never take this path: only a material whose extras say colors_baked is a landscape's.
///
/// The numbers live here, not in the preset, because v1 is locked and v2 carries the founder's pending observe decision; a
/// later preset version moves them into an x_look_land block (the look bible says so).
/// </summary>
public static class LandscapeLook
{
    public const string ShaderPath = "res://shaders/painterly_land.gdshader";
    /// <summary>Still and flowing water wear this shader instead: a glaze you can see into (Run 2, the founder's playtest round).</summary>
    public const string WaterShaderPath = "res://shaders/painterly_water.gdshader";
    /// <summary>
    /// How strongly the brush's finer marks come in close up (painterly_land's close_detail): the shoulder camera's ground and the
    /// cliff beside the diorama camera read as brushwork rather than a smooth wash (the founder's F2 playtest, Run 2).
    /// </summary>
    public const float CloseDetail = 2.8f;
    /// <summary>Meta on a mesh whose surfaces wear landscape materials.</summary>
    public const string LandscapePaintedMeta = "look_landscape_painted";

    private static LandRole R(LandKind kind, float scale, float scale2, float stretch, Vector3 axis, float variation, float wrap, float warmth, float sheen,
        float specular, float roughness, float softening, float normal, float metallic = 0f, float flow = 0f, float rim = 0f, Vector3? glaze = null) =>
        new(kind, scale, scale2, stretch, axis, variation, wrap, warmth, sheen, specular, roughness, metallic, softening, normal, 0f, 1f, flow, rim, glaze);

    private static readonly Vector3 Wind = new Vector3(0.7071f, 0f, 0.7071f);

    /// <summary>The twenty harness roles (pipeline/landscape/harness/common.py PALETTE) and how each is painted. Sizes are metres in the 10 cm avatar's world.</summary>
    public static readonly IReadOnlyDictionary<string, LandRole> Roles = new Dictionary<string, LandRole>
    {
        // Ground: broad washes carry the far hills, tufts, clods and pebbles appear as the camera comes close.
        ["meadow"] = R(LandKind.Meadow, 0.025f, 0.10f, 3.0f, Wind, 0.55f, 0.35f, 0.25f, 0.12f, 0.10f, 0.92f, 0.50f, 0.40f, glaze: new Vector3(0.92f, 0.98f, 1.06f)),
        ["soil"] = R(LandKind.Soil, 0.030f, 0.10f, 2.0f, Vector3.Right, 0.55f, 0.30f, 0.25f, 0.00f, 0.06f, 0.96f, 0.30f, 0.40f),
        ["worn_path"] = R(LandKind.Path, 0.020f, 0.08f, 3.0f, Wind, 0.40f, 0.30f, 0.25f, 0.00f, 0.06f, 0.96f, 0.35f, 0.40f),
        ["gravel"] = R(LandKind.Gravel, 0.030f, 0.016f, 2.0f, Vector3.Right, 0.45f, 0.25f, 0.20f, 0.00f, 0.20f, 0.90f, 0.25f, 0.60f),
        ["scree"] = R(LandKind.Scree, 0.040f, 0.050f, 2.0f, Vector3.Right, 0.45f, 0.22f, 0.20f, 0.00f, 0.18f, 0.92f, 0.25f, 0.70f),
        ["rock"] = R(LandKind.Rock, 0.050f, 0.160f, 2.0f, Vector3.Right, 0.50f, 0.22f, 0.25f, 0.00f, 0.18f, 0.93f, 0.25f, 0.50f),
        ["cliff"] = R(LandKind.Cliff, 0.060f, 0.140f, 2.0f, Vector3.Up, 0.55f, 0.20f, 0.25f, 0.00f, 0.15f, 0.93f, 0.25f, 0.60f),
        ["moss"] = R(LandKind.Moss, 0.030f, 0.040f, 1.5f, Vector3.Right, 0.60f, 0.45f, 0.25f, 0.25f, 0.04f, 0.97f, 0.25f, 0.50f),
        ["snow"] = R(LandKind.Snow, 0.050f, 0.050f, 1.5f, Vector3.Right, 0.35f, 0.55f, 0.12f, 0.15f, 0.22f, 0.70f, 0.15f, 0.30f),
        // Water: ripples that drift and a soft sky rim, painted as a glaze you can see into (WaterLook); no refraction.
        ["still_water"] = R(LandKind.StillWater, 0.120f, 0.100f, 1.5f, Wind, 0.40f, 0.12f, 0.05f, 0.00f, 0.85f, 0.20f, 0.10f, 1.00f, 0f, 0.45f, 0.75f),
        ["flowing_water"] = R(LandKind.FlowingWater, 0.050f, 0.100f, 5.0f, Wind, 0.50f, 0.12f, 0.05f, 0.00f, 0.80f, 0.20f, 0.10f, 1.00f, 0f, 0.70f, 0.60f),
        // Vegetation: rounded dabs of leaf paint, bark with furrows.
        ["foliage"] = R(LandKind.Foliage, 0.030f, 0.030f, 2.0f, Vector3.Up, 0.70f, 0.55f, 0.30f, 0.30f, 0.04f, 0.90f, 0.45f, 0.60f, glaze: new Vector3(0.92f, 0.99f, 1.06f)),
        ["bark"] = R(LandKind.Bark, 0.010f, 0.030f, 6.0f, Vector3.Up, 0.65f, 0.25f, 0.30f, 0.00f, 0.06f, 0.95f, 0.25f, 0.80f),
        // Buildings: upright planks, coursed masonry, shingles.
        ["timber"] = R(LandKind.Timber, 0.030f, 0.025f, 6.0f, Vector3.Up, 0.60f, 0.25f, 0.30f, 0.00f, 0.15f, 0.85f, 0.30f, 0.40f),
        ["stone_masonry"] = R(LandKind.Masonry, 0.030f, 0.035f, 2.0f, Vector3.Right, 0.60f, 0.20f, 0.25f, 0.00f, 0.12f, 0.92f, 0.25f, 0.40f),
        ["roof"] = R(LandKind.Roof, 0.030f, 0.025f, 2.0f, Vector3.Right, 0.60f, 0.22f, 0.25f, 0.00f, 0.20f, 0.80f, 0.25f, 0.50f),
        // Populated props.
        ["wood"] = R(LandKind.Wood, 0.020f, 0.030f, 6.0f, Vector3.Right, 0.60f, 0.20f, 0.30f, 0.00f, 0.30f, 0.80f, 0.35f, 0.40f),
        ["stone"] = R(LandKind.Stone, 0.040f, 0.080f, 2.0f, Vector3.Right, 0.50f, 0.20f, 0.25f, 0.00f, 0.20f, 0.92f, 0.25f, 0.50f),
        ["cloth"] = R(LandKind.Cloth, 0.020f, 0.004f, 2.0f, Vector3.Right, 0.50f, 0.45f, 0.30f, 0.35f, 0.05f, 0.95f, 0.40f, 0.20f),
        ["metal"] = R(LandKind.Metal, 0.020f, 0.030f, 8.0f, Vector3.Right, 0.35f, 0.10f, 0.10f, 0.00f, 0.60f, 0.48f, 0.25f, 0.20f, metallic: 0.35f),
    };

    /// <summary>
    /// The harness role of an imported material, or false when it is not a landscape's: only a material whose glTF extras name a
    /// role and say colors_baked (the exporter's own statement that the vertex colours hold the tints and blends) takes the land's
    /// treatment, so a captured room's materials, even ones named like a role, never change.
    /// </summary>
    public static bool TryRole(Material? material, out string role)
    {
        role = "";
        if (material == null || !material.HasMeta("extras")) return false;
        var extras = material.GetMeta("extras");
        if (extras.VariantType != Variant.Type.Dictionary) return false;
        var dictionary = extras.AsGodotDictionary();
        if (!dictionary.TryGetValue("colors_baked", out var baked) || baked.VariantType != Variant.Type.Bool || !baked.AsBool()) return false;
        if (!dictionary.TryGetValue("role", out var named) || named.VariantType != Variant.Type.String) return false;
        role = named.AsString();
        return Roles.ContainsKey(role);
    }

    /// <summary>
    /// The flat colour VoxelGI should voxelize for a landscape surface: the material's linear factor times the mean of the
    /// baked vertex colours (the voxelizer ignores vertex colour, so the bounce would otherwise take the envelope's cream),
    /// as an sRGB colour for a StandardMaterial3D. A surface without vertex colours bakes its factor.
    /// </summary>
    public static Color BakeColor(Mesh mesh, int surface, Color albedoSrgb)
    {
        var factor = albedoSrgb.SrgbToLinear();
        var arrays = mesh.SurfaceGetArrays(surface);
        var colours = arrays.Count > (int)Mesh.ArrayType.Color && arrays[(int)Mesh.ArrayType.Color].VariantType == Variant.Type.PackedColorArray
            ? arrays[(int)Mesh.ArrayType.Color].AsColorArray() : Array.Empty<Color>();
        if (colours.Length == 0) return albedoSrgb;
        var step = Math.Max(1, colours.Length / 4096);
        double r = 0, g = 0, b = 0;
        var count = 0;
        for (var i = 0; i < colours.Length; i += step)
        {
            r += colours[i].R; g += colours[i].G; b += colours[i].B;
            count++;
        }
        return new Color((float)(factor.R * r / count), (float)(factor.G * g / count), (float)(factor.B * b / count)).LinearToSrgb();
    }

    /// <summary>Whether a harness role is water, which takes the water shader (see-through, two-sided, no shadow of its own).</summary>
    public static bool IsWater(string role) => Roles.TryGetValue(role, out var look) && look.Kind is LandKind.StillWater or LandKind.FlowingWater;

    /// <summary>Whether a room is open land: its shell has no wall and no ceiling (a landscape's ground and backdrop are all there is).</summary>
    public static bool IsOpenLand(Room.RoomData room) =>
        room.Shell.Count > 0 && !room.Shell.Any(part => part.Role is "wall" or "ceiling");
}

/// <summary>
/// Light on open land. The room look's rule, "the sun lands only through the window", is an indoor rule: on a landscape the
/// sky lights everything (the founder, 8 October: a real sky lights the land, and the window only tells where the sun rises).
/// The sun is at full strength with no opening needed, the environment's ambient light, which a closed VoxelGI interior
/// makes nearly irrelevant (scale 0.15), becomes the sky's fill in the shade, and no VoxelGI is baked (it bakes a closed
/// interior; the land is open to the sky on every side).
/// </summary>
public static class OpenLandLight
{
    /// <summary>How much of the hour's ambient light (its colour and energy from the time keys) fills the shade on open land.</summary>
    public const float AmbientScale = 0.85f;
    /// <summary>
    /// The sun's strength on open land, as a fraction of the room look's (whose sun is bright enough for one patch of floor in a
    /// dark room: energy 3.3 at a summer morning). With the whole ground in the sun that blew every lit surface out to a lime
    /// or a cream; at this fraction sunlit grass reads about its own colour, a little bright, and the shade a cool 40% of it.
    /// </summary>
    public const float SunStrength = 0.38f;
    /// <summary>How far the sun's shadows reach on the smallest open land, metres (the 1x island, in two splits); a bigger island's reach grows with it to 60 m in four splits (ViewDistances.OpenLandShadows, S9).</summary>
    public const float ShadowDistanceM = ViewDistances.MinShadowM;
    /// <summary>The fraction of that distance the first (sharpest) shadow split covers in two splits: the 10 cm eye's world.</summary>
    public const float ShadowSplit1 = ViewDistances.TwoSplit1;
}


/// <summary>
/// How water is painted (Run 2, the founder's playtest round: water you can see into). A pond is a glaze over its bed: the eye's
/// path through the water sets how much it hides (clear shallows, an opaque deep middle), and the depth straight down to the bed
/// sets its colour in a few soft washes, the generator's water colour lifted toward clear green in the shallows and darker and
/// cooler in the deep middle. A pale wet line marks the shore; from below the surface is a pale rippled ceiling. Numbers are
/// metres in the 10 cm avatar's world: a 5 cm pond reads clear at its rim and dusky in its middle, a 20 cm one dark and cool.
/// </summary>
public sealed record WaterLook(
    float ClarityM, float SurfaceAlpha, float MaxAlpha, float DeepM, float WashSteps, float WashWobble,
    Vector3 ShallowTint, Vector3 DeepTint, float ShoreM, float ShoreStrength, Color SkyColor, Vector3 UndersideTint, float UndersideAlpha)
{
    /// <summary>Still water: a pond or a lake, green-clear at its rim and deepening to a cool dusk.</summary>
    public static readonly WaterLook Still = new(0.06f, 0.10f, 0.93f, 0.12f, 4f, 0.45f,
        new Vector3(1.25f, 1.32f, 1.15f), new Vector3(0.34f, 0.46f, 0.62f), 0.005f, 0.55f, new Color(0.78f, 0.88f, 0.96f), new Vector3(1.5f, 1.6f, 1.55f), 0.42f);
    /// <summary>Flowing water: a brook runs shallow and clear over its stones.</summary>
    public static readonly WaterLook Flowing = Still with { ClarityM = 0.08f, SurfaceAlpha = 0.12f, ShoreStrength = 0.6f };

    public static WaterLook For(LandKind kind) => kind == LandKind.FlowingWater ? Flowing : Still;
}

/// <summary>
/// How the island's sea is painted over the pond water it shares a material with (Run 2, the sea's look). Depths are metres in the
/// 10 cm avatar's world: the lagoon (22 cm) reads a light turquoise, the open sea (66 cm and deeper) a deep blue-green, the reef's
/// crest (2 cm under) breaks white. Foam fills water shallower than FoamDepthM in bands WaveSpacingM of depth apart that drift
/// shoreward at WaveSpeed bands a second. The distant islands sit under a haze that reaches HazeMax of the horizon's colour
/// between HazeStartM and HazeEndM, keeping their silhouettes.
/// </summary>
public sealed record SeaLook(
    float DeepM, float ClarityM, Vector3 DeepTint, Color FoamColor, float FoamDepthM, float FoamStrength, float WaveSpacingM, float WaveSpeed,
    float HazeStartM, float HazeEndM, float HazeMax)
{
    public static readonly SeaLook Default = new(0.55f, 0.1f, new Vector3(0.22f, 0.36f, 0.60f), new Color(0.95f, 0.97f, 0.95f), 0.07f, 0.9f, 0.012f, 0.25f,
        3f, 45f, 0.55f);
}
