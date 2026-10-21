using Godot;

namespace GarageGame.Native.Characters;

/// <summary>
/// The dazed stars of a knockout tumble (docs/runs/RUN-2-CHARACTERS.md, "The tumble"): a few little glowing stars circling above
/// the head for the tumble's length. No words: the stars are the whole message. Effect geometry of the tumble's own (one shared
/// flat five-pointed star, billboarded, emissive so the bloom catches it), on the body's render layers so the player's own eye
/// never sees them. Hidden, they cost nothing.
/// </summary>
public sealed partial class TumbleStars : Node3D
{
    public const int Count = 5;
    /// <summary>Turns per second round the head.</summary>
    public const float OrbitHz = 0.8f;
    /// <summary>A star's size, tip to tip (m).</summary>
    public const float StarSizeM = 0.007f;
    /// <summary>The stars grow in and out over this long at the tumble's ends.</summary>
    public const float FadeS = 0.2f;
    public static readonly Color Glow = new(1.0f, 0.85f, 0.32f);

    private static ArrayMesh? _mesh;
    private static StandardMaterial3D? _material;
    private readonly MeshInstance3D[] _stars = new MeshInstance3D[Count];

    /// <summary>The orbit's radius and height above the head's joint, in the body's frame (from the head's mesh).</summary>
    public float RadiusM { get; private set; }

    public TumbleStars() => Name = "TumbleStars";

    /// <summary>Builds the stars for <paramref name="body"/>, hidden, on the render layers of its body part.</summary>
    public static TumbleStars Create(CharacterBody body)
    {
        var stars = new TumbleStars { Visible = false };
        var head = body.Bones[body.HeadBone];
        var size = head.HasMesh ? head.MeshBounds.Size : new Vector3(0.03f, 0.03f, 0.03f);
        stars.RadiusM = Mathf.Clamp(0.45f * Mathf.Max(size.X, size.Z), 0.012f, 0.03f);
        var layers = body.PartMesh(body.BodyBone)?.Layers ?? 1u;
        for (var i = 0; i < Count; i++)
        {
            stars._stars[i] = new MeshInstance3D
            {
                Name = $"Star{i}", Mesh = StarMesh(), MaterialOverride = StarMaterial(), Layers = layers,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            stars.AddChild(stars._stars[i]);
        }
        return stars;
    }

    /// <summary>
    /// Places the stars round <paramref name="centre"/> (the top of the head, in the parent's frame) at <paramref name="timeS"/> into
    /// the tumble, grown by <paramref name="grow"/> (0 to 1). Allocation-free.
    /// </summary>
    public void Place(Vector3 centre, float timeS, float grow)
    {
        Visible = grow > 0;
        if (!Visible) return;
        Position = centre;
        var scale = Mathf.Clamp(grow, 0, 1);
        for (var i = 0; i < Count; i++)
        {
            var angle = Mathf.Tau * (OrbitHz * timeS + (float)i / Count);
            var bob = 0.002f * Mathf.Sin(Mathf.Tau * (1.7f * timeS + 0.37f * i));
            var star = _stars[i];
            star.Position = new Vector3(Mathf.Cos(angle) * RadiusM, bob, Mathf.Sin(angle) * RadiusM * 0.55f);
            star.Scale = Vector3.One * Mathf.Max(scale, 0.001f);
        }
    }

    /// <summary>The visible stars' positions in world space (tests).</summary>
    public Vector3 StarPosition(int index) => _stars[index].GlobalPosition;

    private static ArrayMesh StarMesh()
    {
        if (_mesh != null) return _mesh;
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        tool.SetNormal(Vector3.Back);
        var outer = StarSizeM / 2;
        var inner = outer * 0.45f;
        for (var p = 0; p < 10; p++)
        {
            var a0 = Mathf.Pi / 2 + Mathf.Tau * p / 10;
            var a1 = Mathf.Pi / 2 + Mathf.Tau * (p + 1) / 10;
            var r0 = p % 2 == 0 ? outer : inner;
            var r1 = p % 2 == 0 ? inner : outer;
            tool.AddVertex(Vector3.Zero);
            tool.AddVertex(new Vector3(Mathf.Cos(a1) * r1, Mathf.Sin(a1) * r1, 0));
            tool.AddVertex(new Vector3(Mathf.Cos(a0) * r0, Mathf.Sin(a0) * r0, 0));
        }
        _mesh = tool.Commit();
        return _mesh;
    }

    private static StandardMaterial3D StarMaterial() => _material ??= new StandardMaterial3D
    {
        AlbedoColor = Glow,
        EmissionEnabled = true,
        Emission = Glow,
        EmissionEnergyMultiplier = 2.5f,
        BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        Roughness = 1,
    };
}
