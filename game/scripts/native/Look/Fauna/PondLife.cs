using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GarageGame.Native.Look.Fauna;

/// <summary>
/// The living scenery of a room's water (Run 2, Lane L): fish that swim the ponds and dart away from the avatars, and the veil
/// that clouds the water when a camera dips under it. Add it beside the built room once its colliders are registered, then hand
/// it the avatars:
/// <code>
/// var ponds = PondLife.Create(Room.RoomId, Built);
/// AddChild(ponds);
/// ponds.SetAvatars(Player);
/// </code>
/// On its first physics frame it surveys the water (PondSurvey: the water shell parts, beds by a downward ray to the world
/// layer) and places the fish (Shoal: deterministic from the room's id and its water). Every fish is one instance of one
/// MultiMesh, a single draw. Nothing here is an entity, saved, collides, or is seen by the kernel, sight or map.
/// </summary>
public partial class PondLife : Node3D
{
    public const string ShaderPath = "res://shaders/painterly_fish.gdshader";
    public const string VeilShaderPath = "res://shaders/underwater_veil.gdshader";
    private const int VeilPonds = 8;

    private string _roomId = "";
    private Node3D? _built;
    private readonly List<Node3D> _avatars = new();
    private readonly List<Vector3> _threats = new();
    private float[] _buffer = Array.Empty<float>();

    public IReadOnlyList<Pond> Ponds { get; private set; } = Array.Empty<Pond>();
    public Shoal? Shoal { get; private set; }
    public MultiMeshInstance3D? School { get; private set; }
    public MeshInstance3D? Veil { get; private set; }
    public bool Surveyed { get; private set; }
    /// <summary>Whether the fish swim on their own each frame (tests turn it off and step the shoal themselves).</summary>
    public bool Running { get; set; } = true;
    public string Summary { get; private set; } = "";

    public static PondLife Create(string roomId, Node3D built) => new() { Name = "PondLife", _roomId = roomId, _built = built };

    /// <summary>The bodies the fish dart away from: the player. Never the Gubble (the companion): the founder (8 October) wants it set
    /// apart from the world, "like a ghost only the player can see", so no animal reacts to it. Pass them again whenever they change.</summary>
    public void SetAvatars(params Node3D?[] avatars)
    {
        _avatars.Clear();
        _avatars.AddRange(avatars.OfType<Node3D>());
    }

    /// <summary>
    /// How much of a physics frame the survey may take on the main thread (milliseconds): the bed rays, a few hundred a frame. The first
    /// recorded play lost 0.6 to 0.8 s to one frame that did them all as the room opened; spread over a few seconds nothing is felt, and
    /// the fish are not drawn until the intro has built the water anyway.
    /// </summary>
    public double SurveyBudgetMs { get; set; } = 3.0;
    /// <summary>Whether the plain arithmetic of the survey runs on a worker thread (the game) or in the same call (the look suite, which checks the survey after a frame or two).</summary>
    public bool SurveyInBackground { get; set; } = true;
    /// <summary>The survey done in the first physics frame, in one piece, as it was before the first recorded play (the look suite's way).</summary>
    public PondLife AtOnce()
    {
        SurveyBudgetMs = double.PositiveInfinity;
        SurveyInBackground = false;
        return this;
    }

    private PondSurvey.Surveyor? _surveyor;
    private System.Diagnostics.Stopwatch? _surveyClock;
    private double _surveyMainMs;

    public override void _PhysicsProcess(double delta)
    {
        if (Surveyed || _built == null || !IsInstanceValid(_built)) return;
        _surveyClock ??= System.Diagnostics.Stopwatch.StartNew();
        _surveyor ??= new PondSurvey.Surveyor(_built, PondSurvey.RayProbe(GetWorld3D().DirectSpaceState), SurveyInBackground);
        var step = System.Diagnostics.Stopwatch.StartNew();
        var done = _surveyor.Step(SurveyBudgetMs);
        _surveyMainMs += step.Elapsed.TotalMilliseconds;
        if (!done) return;
        Finish(_surveyor.Ponds, _surveyClock.Elapsed.TotalMilliseconds, _surveyMainMs, $", {_surveyor.Rays} bed rays over {_surveyor.Steps} frames");
        SetPhysicsProcess(false);
    }

    /// <summary>Survey the water and place the fish now, all at once, probing beds with bedBelow (the look suite's way; the game spreads it over frames).</summary>
    public void SurveyNow(Func<Vector3, float?> bedBelow)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ponds = PondSurvey.Survey(_built!, bedBelow);
        Finish(ponds, clock.Elapsed.TotalMilliseconds, clock.Elapsed.TotalMilliseconds, "");
    }

    private void Finish(IReadOnlyList<Pond> ponds, double wallMs, double mainMs, string how)
    {
        Surveyed = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Ponds = ponds;
        Shoal = new Shoal(_roomId, Ponds);
        BuildSchool();
        BuildVeil();
        mainMs += clock.Elapsed.TotalMilliseconds;
        var lived = Ponds.Where(p => Shoal.Schools.Any(s => s.Pond == p)).ToArray();
        Summary = $"{Ponds.Count} bodies of water, {Shoal.Fish.Count} fish in {Shoal.Schools.Count} schools, surveyed and placed in {wallMs + clock.Elapsed.TotalMilliseconds:0} ms ({mainMs:0} ms of them on the main thread{how})"
            + string.Concat(lived.Select(p => $"; pond {p.Index} ({p.Kind}, {p.WetAreaM2:0.00} m2, {p.MaxDepthM * 100f:0.0} cm deep): {Shoal.Schools.Where(s => s.Pond == p).Sum(s => s.Members.Length)} fish"));
        GD.Print("POND_LIFE: " + Summary);
    }

    private void BuildSchool()
    {
        if (Shoal == null || Shoal.Fish.Count == 0) return;
        var material = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderPath), ResourceName = "painterly fish" };
        var multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = FishMesh.Get(),
        };
        multimesh.InstanceCount = Shoal.Fish.Count;
        _buffer = new float[Shoal.Fish.Count * 16];
        Shoal.WriteBuffer(_buffer);
        multimesh.Buffer = _buffer;
        // The fish never leave their water: the ponds' boxes, from bed to surface, bound them for culling.
        var lived = Shoal.Schools.Select(s => s.Pond).Distinct().ToArray();
        var box = lived.Select(PondBox).Aggregate((a, b) => a.Merge(b));
        multimesh.CustomAabb = box;
        School = new MultiMeshInstance3D { Name = "Fish", Multimesh = multimesh, MaterialOverride = material, CustomAabb = box };
        School.GIMode = GeometryInstance3D.GIModeEnum.Disabled;
        AddChild(School);
    }

    private static Aabb PondBox(Pond pond)
    {
        var low = Enumerable.Range(0, pond.Bed.Length).Where(i => !float.IsNaN(pond.Bed[i])).Select(i => pond.Bed[i]).DefaultIfEmpty(pond.Level - 0.1f).Min();
        var high = Enumerable.Range(0, pond.Surface.Length).Where(i => !float.IsNaN(pond.Surface[i])).Select(i => pond.Surface[i]).DefaultIfEmpty(pond.Level).Max();
        return new Aabb(new Vector3(pond.Min.X, low - 0.01f, pond.Min.Y), new Vector3(pond.Nx * pond.Cell, high - low + 0.02f, pond.Nz * pond.Cell));
    }

    /// <summary>
    /// The still ponds a camera can dip into (deepest first, at most eight): their boxes from above and their levels. The island's
    /// sea, which the survey also finds as one great pond, is left to the open sea's own veil (OpenSea), so the two never stack.
    /// </summary>
    public IReadOnlyList<(Vector4 Box, float Level)> VeilPondsFor() =>
        Ponds.Where(p => p.Kind == WaterKind.Still && p.MaxDepthM > 0.01f && !IsSea(p)).OrderByDescending(p => p.MaxDepthM).Take(VeilPonds)
            .Select(p => (new Vector4(p.Min.X, p.Min.Y, p.Min.X + p.Nx * p.Cell, p.Min.Y + p.Nz * p.Cell), p.Level)).ToList();

    /// <summary>The level of the room's sea, if it has one (the room this life lives in, else what a test sets).</summary>
    public float? SeaLevel { get; set; }

    private bool IsSea(Pond pond) => (SeaLevel ?? (GetParent() as RoomWorld)?.Room?.Sea?.LevelM) is { } level && Mathf.Abs(pond.Level - level) < 0.003f;

    /// <summary>Whether a camera at this point is under a pond's water (the veil shader's own test, for checks and tools).</summary>
    public bool IsUnderwater(Vector3 eye) =>
        VeilPondsFor().Any(p => eye.X >= p.Box.X && eye.X <= p.Box.Z && eye.Z >= p.Box.Y && eye.Z <= p.Box.W && eye.Y < p.Level);

    private void BuildVeil()
    {
        var ponds = VeilPondsFor();
        if (ponds.Count == 0) return;
        var material = new ShaderMaterial { Shader = GD.Load<Shader>(VeilShaderPath), ResourceName = "underwater veil", RenderPriority = 100 };
        var boxes = Enumerable.Range(0, VeilPonds).Select(i => i < ponds.Count ? ponds[i].Box : Vector4.Zero).ToArray();
        var levels = Enumerable.Range(0, VeilPonds).Select(i => i < ponds.Count ? ponds[i].Level : -1e9f).ToArray();
        material.SetShaderParameter("pond_count", ponds.Count);
        material.SetShaderParameter("pond_box", boxes);
        material.SetShaderParameter("pond_level", levels);
        Veil = new MeshInstance3D
        {
            Name = "UnderwaterVeil",
            Mesh = new QuadMesh { Size = new Vector2(2f, 2f) },
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            // A full-screen quad placed by its shader: it must never be culled by where its node stands.
            ExtraCullMargin = 16384f,
        };
        AddChild(Veil);
    }

    public override void _Process(double delta)
    {
        if (Shoal == null || !Running) return;
        Advance((float)delta);
    }

    /// <summary>Move every fish by dt seconds, away from the avatars that come close, and show them.</summary>
    public void Advance(float dt)
    {
        if (Shoal == null) return;
        _threats.Clear();
        foreach (var avatar in _avatars)
            if (IsInstanceValid(avatar) && avatar.IsInsideTree()) _threats.Add(avatar.GlobalPosition);
        Shoal.Step(dt, _threats);
        if (School?.Multimesh is { } multimesh && _buffer.Length > 0)
        {
            Shoal.WriteBuffer(_buffer);
            RenderingServer.MultimeshSetBuffer(multimesh.GetRid(), _buffer);
        }
        if (Veil?.MaterialOverride is ShaderMaterial veil && GetWorld3D()?.Environment is { } environment)
        {
            var ambient = environment.AmbientLightColor * environment.AmbientLightEnergy;
            veil.SetShaderParameter("light_level", Math.Clamp(ambient.Luminance * 1.2f + 0.08f, 0.04f, 1.5f));
        }
    }
}
