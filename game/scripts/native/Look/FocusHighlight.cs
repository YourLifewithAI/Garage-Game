using Godot;
using System.Collections.Generic;
using System.Linq;

namespace GarageGame.Native.Look;

/// <summary>
/// How the focus is drawn: a warm rim of light on the thing and a soft two-tone line just outside it (a light line over a wider,
/// darker one, so it reads on pale sand as on grass, rock and wood). Faint enough to keep the scene painterly; it breathes slowly.
/// The numbers live here until a preset version carries an x_look_focus block (v1 is locked; the founder judges this first).
/// </summary>
public sealed record FocusLook(
    Color RimColor, float RimStrength, float RimPower, float Lift,
    Color LineColor, float LineWidthPx, Color UnderColor, float UnderWidthPx,
    float Breath, float BreathPeriodS)
{
    // A rim only at grazing edges and faint (a box's faces keep their own colour), a light warm line over a wider umber one.
    public static readonly FocusLook Default = new(
        new Color("ffe9b8"), 0.18f, 5f, 0f,
        new Color(1f, 0.9f, 0.62f, 0.9f), 2.2f, new Color(0.18f, 0.11f, 0.05f, 0.6f), 4.4f,
        0.15f, 2.6f);
}

/// <summary>
/// The focus (the founder, 9 October: "the thing the avatar faces and can use gets an indicator"): one thing at a time, the one
/// the next key press would act on, wears FocusLook as an overlay on each of its meshes. Things that can't be used stay plain.
/// The look director owns one; the play lane says what is in focus (LookDirector.SetFocusHighlight). Visual only.
/// </summary>
public sealed class FocusHighlight
{
    public const string RimShaderPath = "res://shaders/focus_rim.gdshader";
    public const string LineShaderPath = "res://shaders/focus_outline.gdshader";

    /// <summary>What is in focus now, or null.</summary>
    public Node3D? Target { get; private set; }
    /// <summary>The meshes wearing the focus now.</summary>
    public IReadOnlyList<MeshInstance3D> Lit => _lit.Select(l => l.Mesh).ToArray();
    /// <summary>The overlay every lit mesh wears: the darker, wider line, which chains the light line, which chains the rim.</summary>
    public ShaderMaterial Overlay => Under;
    public ShaderMaterial Under { get; }
    public ShaderMaterial Line { get; }
    public ShaderMaterial Rim { get; }

    private readonly List<(MeshInstance3D Mesh, Material? Before)> _lit = new();

    public FocusHighlight(FocusLook? look = null)
    {
        var l = look ?? FocusLook.Default;
        // The darker, wider line first (the overlay itself: Godot draws an overlay's first chained pass for certain), the light
        // line over it, then the rim on the thing itself.
        Rim = new ShaderMaterial { Shader = GD.Load<Shader>(RimShaderPath), ResourceName = "focus rim", RenderPriority = 0 };
        Rim.SetShaderParameter("rim_color", l.RimColor);
        Rim.SetShaderParameter("rim_strength", l.RimStrength);
        Rim.SetShaderParameter("rim_power", l.RimPower);
        Rim.SetShaderParameter("lift", l.Lift);
        Rim.SetShaderParameter("breath", l.Breath);
        Rim.SetShaderParameter("breath_period_s", l.BreathPeriodS);
        Line = MakeLine(l.LineColor, l.LineWidthPx, l, -1, "focus line");
        Line.NextPass = Rim;
        Under = MakeLine(l.UnderColor, l.UnderWidthPx, l, -2, "focus under-line");
        Under.NextPass = Line;
    }

    private static ShaderMaterial MakeLine(Color colour, float widthPx, FocusLook look, int priority, string name)
    {
        var material = new ShaderMaterial { Shader = GD.Load<Shader>(LineShaderPath), ResourceName = name, RenderPriority = priority };
        material.SetShaderParameter("line_color", colour);
        material.SetShaderParameter("width_px", widthPx);
        material.SetShaderParameter("breath", look.Breath);
        material.SetShaderParameter("breath_period_s", look.BreathPeriodS);
        return material;
    }

    /// <summary>Put the focus on one thing (every visible mesh under it, itself included), or on nothing (null). The last focus is cleared first.</summary>
    public void Set(Node3D? target)
    {
        if (target == Target && target != null && GodotObject.IsInstanceValid(target)) return;
        Clear();
        if (target == null || !GodotObject.IsInstanceValid(target)) return;
        Target = target;
        var meshes = target.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().Prepend(target as MeshInstance3D).OfType<MeshInstance3D>();
        foreach (var mesh in meshes.Where(m => m.Mesh != null))
        {
            _lit.Add((mesh, mesh.MaterialOverlay));
            mesh.MaterialOverlay = Overlay;
            mesh.SetInstanceShaderParameter("hull_centre", mesh.Mesh.GetAabb().GetCenter());
        }
    }

    /// <summary>Take the focus off whatever wears it, giving each mesh back the overlay it had.</summary>
    public void Clear()
    {
        foreach (var (mesh, before) in _lit)
            if (GodotObject.IsInstanceValid(mesh) && mesh.MaterialOverlay == Overlay) mesh.MaterialOverlay = before;
        _lit.Clear();
        Target = null;
    }

    /// <summary>A target that left the tree or was freed loses the focus.</summary>
    public void Prune()
    {
        if (Target != null && (!GodotObject.IsInstanceValid(Target) || !Target.IsInsideTree())) Clear();
    }
}
