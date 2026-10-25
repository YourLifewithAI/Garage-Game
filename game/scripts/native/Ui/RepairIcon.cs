using System;
using System.Collections.Generic;
using Godot;

namespace GarageGame.Native.Ui;

/// <summary>
/// The icon over a thing the Gubble repaired (MECHANICS.md decision 131: it repairs without asking when the repair spends, and the spend is
/// made visible): a small card of what was spent, each kind's mark and the kernel's words for it ("2 logs, 1 stone"), standing over the
/// thing, rising a little and fading, gone after about three seconds as a notice goes. The HUD (RoomHud.Repair.cs) places it every frame;
/// it takes no mouse and holds no state but its age. The words are the pack's display text, shown as plain text and never read.
/// </summary>
public partial class RepairIcon : PanelContainer
{
    /// <summary>It stands fully this long, then fades over FadeS: gone after about three seconds (a notice's is four).</summary>
    public const double HoldS = 2.4, FadeS = 0.6, LifeS = HoldS + FadeS;
    /// <summary>How far it rises over its life, in pixels of the HUD's canvas.</summary>
    public const float RisePx = 22;
    public const int FontSize = 16;

    private readonly List<MaterialMark> _marks = new();
    private readonly List<Label> _labels = new();

    /// <summary>The thing repaired (its id) and the point above it in the world.</summary>
    public string Thing { get; private set; } = "";
    public Vector3 At { get; private set; }
    /// <summary>The words as one line: "2 logs, 1 stone".</summary>
    public string Words { get; private set; } = "";
    /// <summary>Seconds since it came.</summary>
    public double Age { get; private set; }
    /// <summary>The marks and the words of each kind spent, in order.</summary>
    public IReadOnlyList<MaterialMark> Marks => _marks;
    public IReadOnlyList<Label> Labels => _labels;

    /// <summary>Its opacity at this age: 1 while it holds, then falling to 0 over FadeS.</summary>
    public static float AlphaAt(double age) => age <= HoldS ? 1f : (float)Math.Max(0, (LifeS - age) / FadeS);

    /// <summary>How far up it has risen at this age: it rises over the whole of its life, quickly at first.</summary>
    public static float RiseAt(double age)
    {
        var t = (float)Math.Clamp(age / LifeS, 0, 1);
        return RisePx * (1 - (1 - t) * (1 - t));
    }

    /// <summary>Whether its time is up.</summary>
    public bool Done => Age >= LifeS;

    public RepairIcon()
    {
        Name = "RepairIcon";
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        AddThemeStyleboxOverride("panel", MenuStyle.Box(new Color(0.075f, 0.13f, 0.13f, 0.9f), MenuStyle.Warm, 1, 8, 6));
    }

    /// <summary>
    /// The icon for this thing: one mark and one label for each kind spent, the label's words followed by a comma but the last. A kind's material
    /// picks its mark (null or unknown: a plain dot). Sized to fit its words.
    /// </summary>
    public void Build(string thing, Vector3 at, IReadOnlyList<(string? Material, string Words)> spent)
    {
        Thing = thing;
        At = at;
        Age = 0;
        foreach (var child in GetChildren()) { RemoveChild(child); child.Free(); }
        _marks.Clear();
        _labels.Clear();
        var row = new HBoxContainer { Name = "Spent", MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 4);
        AddChild(row);
        var all = new List<string>();
        for (var i = 0; i < spent.Count; i++)
        {
            var mark = new MaterialMark { KindMaterial = spent[i].Material ?? "" };
            var label = new Label { Name = "Words" + i, Text = spent[i].Words + (i < spent.Count - 1 ? "," : ""), MouseFilter = MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center };
            label.AddThemeFontSizeOverride("font_size", FontSize);
            label.AddThemeColorOverride("font_color", MenuStyle.Ink);
            row.AddChild(mark);
            row.AddChild(label);
            _marks.Add(mark);
            _labels.Add(label);
            all.Add(spent[i].Words);
        }
        Words = string.Join(", ", all);
        Modulate = Colors.White;
        ResetSize();
    }

    /// <summary>The icon is older by this much (the HUD's frame time); its opacity follows.</summary>
    public void Advance(double delta)
    {
        Age += delta;
        var alpha = AlphaAt(Age);
        if (!Mathf.IsEqualApprox(Modulate.A, alpha)) Modulate = new Color(1, 1, 1, alpha);
    }
}
