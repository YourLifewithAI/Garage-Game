using Godot;
using GarageGame.Native.Look;

namespace GarageGame.Native.Characters;

/// <summary>
/// The family's Gubble drawn on the companion (the characters round, K5): what <see cref="GubbleCharacter.Dress"/> made, kept as a
/// node inside the drawn body (named <see cref="GubbleCharacter.NodeName"/>), so it lives and goes with it. It holds the rig's
/// visual, the gesture layer, and the two glow anchors the look reads from the companion's metas (GlowLook.HaloAnchorMeta: the aura
/// bone, the Gubble's middle; GlowLook.LightAnchorMeta: the torch flame, which carries the glow's light). A few times a second it
/// tells the bubble how lit its place is (CharacterLook.SetLightLevel from LookDirector.LightLevelAt at the Gubble's middle): the
/// film is unshaded, so this is how it darkens at night.
/// </summary>
public partial class DrawnGubble : Node
{
    /// <summary>How often the bubble's light level is read: four times a second, as the dusk moment reads the light.</summary>
    public const double LightIntervalS = 0.25;

    public CompanionAvatar Companion { get; private set; } = null!;
    public CharacterVisual Visual { get; private set; } = null!;
    public CharacterBody Body => Visual.Body;
    public GubbleGestureLayer Gestures { get; private set; } = null!;
    /// <summary>Where the glow's halo sits: on the aura bone (the body bone when there is none), at its mesh's middle.</summary>
    public Node3D HaloAnchor { get; private set; } = null!;
    /// <summary>Where the glow's light sits: on the torch flame (a flame part when no torch holds one); null for neither.</summary>
    public Node3D? LightAnchor { get; private set; }
    /// <summary>The bone the halo rides, and the light's (-1 for none).</summary>
    public int HaloBone { get; private set; }
    public int LightBone { get; private set; } = -1;
    /// <summary>The look the light level is read from (null: none, and the bubble stays fully lit).</summary>
    public LookDirector? Look { get; set; }
    /// <summary>The level last given to the bubble (1 until the first reading), and how many readings there have been.</summary>
    public float LightLevel { get; private set; } = 1f;
    public int LightReadings { get; private set; }

    private double _sinceReading;

    internal void Initialize(CompanionAvatar companion, CharacterVisual visual, GubbleGestureLayer gestures, LookDirector? look)
    {
        Companion = companion;
        Visual = visual;
        Gestures = gestures;
        Look = look;
        var body = visual.Body;
        HaloBone = body.FindBone(PartRole.Aura);
        if (HaloBone < 0) HaloBone = body.BodyBone;
        HaloAnchor = Anchor(body, HaloBone, "GlowHaloAnchor");
        LightBone = HeldFlame(body);
        if (LightBone >= 0) LightAnchor = Anchor(body, LightBone, "GlowLightAnchor");
    }

    /// <summary>The flame a torch holds (a flame part under a torch), else the first flame part; -1 for none.</summary>
    public static int HeldFlame(CharacterBody body)
    {
        var first = -1;
        for (var i = 1; i < body.BoneCount; i++)
        {
            if (body.Bones[i].Role != PartRole.Flame) continue;
            if (first < 0) first = i;
            for (var up = body.Bones[i].Parent; up > 0; up = body.Bones[up].Parent)
                if (body.Bones[up].Role == PartRole.Torch) return i;
        }
        return first;
    }

    /// <summary>A node on a bone's attachment at its mesh's middle (its joint when it has no mesh): it rides the bone's final pose.</summary>
    private static Node3D Anchor(CharacterBody body, int bone, string name)
    {
        var rig = body.Bones[bone];
        var anchor = new Node3D { Name = name, Position = rig.HasMesh ? rig.MeshBounds.GetCenter() : Vector3.Zero };
        body.Attachment(bone).AddChild(anchor);
        return anchor;
    }

    public override void _Ready() => ReadLight();

    public override void _Process(double delta)
    {
        _sinceReading += delta;
        if (_sinceReading < LightIntervalS) return;
        _sinceReading = 0;
        ReadLight();
    }

    /// <summary>
    /// Reads the light at the Gubble's middle and gives it to the bubble now; returns the level, or null without a look to ask (the
    /// bubble keeps what it had).
    /// </summary>
    public float? ReadLight()
    {
        if (Look == null || !IsInstanceValid(Look) || !Look.IsInsideTree() || HaloAnchor == null || !HaloAnchor.IsInsideTree()) return null;
        var level = Look.LightLevelAt(HaloAnchor.GlobalPosition);
        LightLevel = float.IsFinite(level) ? Mathf.Clamp(level, 0f, 1f) : 1f;
        CharacterLook.SetLightLevel(Body, LightLevel);
        LightReadings++;
        return LightLevel;
    }
}
