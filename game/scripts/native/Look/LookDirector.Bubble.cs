using Godot;
using System;
using System.Collections.Generic;

namespace GarageGame.Native.Look;

/// <summary>What the place made of a Bubble (Kernel/Bubble, decision 56), as the look draws it: a carry round the player, a bubble that waits for them to walk in, a boat on water.</summary>
public enum BubbleForm { Carry, WalkIn, Boat }

/// <summary>
/// What the look draws of one bubble beyond where it is and how big (the kernel's ShowBubble and MoveBubble say that): what it is,
/// whether it holds the player, the spot the Gubble leads it to (Led; LedLift when that spot is higher than it can drift) and
/// whether it is rising on a lift. The HUD hands it over from the kernel's BubbleView (CommandHost.ActiveBubble); selected is the
/// HUD's own choice and goes by SelectBubble.
/// </summary>
public readonly record struct BubbleDrawState(BubbleForm Form, bool Holding, bool Led, Vector3 LedTo, bool LedLift, bool Lifting)
{
    public static readonly BubbleDrawState Plain = new(BubbleForm.Carry, false, false, Vector3.Zero, false, false);
}

/// <summary>
/// The numbers of the Bubble's drawing (provisional: the founder judges them by playing). The film is the Gubble's own soap bubble
/// (bubble.gdshader, MaterialLibrary.ForBubble) in the Gubble's aura, so the Bubble reads as the Gubble's magic; each state moves
/// a few of its numbers rather than drawing something else.
/// </summary>
public static class BubbleDrawTuning
{
    /// <summary>The film's tint, a faint blue-white wash (the character bubble's own default).</summary>
    public static readonly Color FilmTint = new("ddebf2");
    /// <summary>The film's numbers: the bubble waiting at a spot (an invitation), holding the player (calmer), a ghost (fainter) and rising on a lift (rushing and brighter).</summary>
    public const float WaitingTintAlpha = 0.07f, WaitingRimAlpha = 0.58f;
    public const float HoldingTintAlpha = 0.09f, HoldingRimAlpha = 0.5f;
    public const float GhostTintAlpha = 0.03f, GhostRimAlpha = 0.28f, GhostGlint = 0.25f;
    public const float LiftRimAlpha = 0.8f, LiftIridescence = 0.8f, LiftFilmBands = 2.2f, LiftShimmerSpeed = 0.9f;
    public const float LedShimmerSpeed = 0.55f;
    /// <summary>A selected bubble's rim is this much brighter than its state's, its film takes this much more tint (in FocusLook's rim colour), and a ring just outside it (the focus line) is HaloMargin wider than it is.</summary>
    public const float SelectedRimBoost = 1.2f, SelectedTintMore = 0.05f, HaloMargin = 0.07f;
    /// <summary>The waiting bubble breathes: its rim swells by this share and its body bobs this far, over a period.</summary>
    public const float BreathShare = 0.18f, BreathPeriodS = 2.4f, BobM = 0.0025f, BobPeriodS = 2.8f;
    /// <summary>A lift stretches the bubble up by this share (eased in over LiftEaseS).</summary>
    public const float LiftStretch = 0.08f, LiftEaseS = 0.35f;
    /// <summary>The string from the Gubble: a thin line of this radius, alpha, and how far its sag hangs (none: a straight line).</summary>
    public const float StringRadiusM = 0.0012f, StringAlpha = 0.55f;
    /// <summary>The mark at the spot a bubble is led to: a ring (inner and outer radius) lying on the ground, and a lift's column.</summary>
    public const float MarkInnerM = 0.022f, MarkOuterM = 0.028f, MarkLiftM = 0.004f, MarkAlpha = 0.7f, ColumnHeightM = 0.09f, ColumnRadiusM = 0.0025f, ColumnAlpha = 0.28f;
    /// <summary>The mark breathes: its scale swells by this share over a period.</summary>
    public const float MarkBreathShare = 0.12f, MarkBreathPeriodS = 1.6f;
    /// <summary>A boat's deck: the thin disc at the water, its thickness.</summary>
    public const float DeckThicknessM = 0.003f, DeckAlpha = 0.22f;
    /// <summary>How often the film reads how lit its place is, as the Gubble's own bubble does (four times a second).</summary>
    public const double LightIntervalS = 0.25;
    /// <summary>The frames a new bubble's parts are shown too small to see (see LookDirector.WarmParts).</summary>
    public const int WarmFrames = 3;
}

/// <summary>
/// The Bubble's look (Kernel/Bubble/CommandHostBubble.cs; decisions 55 to 60): one player-sized bubble, drawn from what the kernel
/// says (ShowBubble, MoveBubble, HideBubble: where, how big, a boat or not, a ghost or not) and what the HUD adds from the kernel's
/// view of it (SetBubbleState: a carry, a bubble to walk into or a boat; holding the player; led, and to where; rising on a lift).
/// - The film is the Gubble's own soap bubble in its aura: a Fresnel rim that shimmers, a thin rainbow, a drawn glint (bubble.gdshader).
///   A carry holding the player is calmer; the bubble waiting at a spot breathes and bobs, an invitation to walk in; a boat is a dome
///   over a thin disc at the water; a ghost preview is a fainter film; a lift stretches it up and rushes its colours.
/// - Led, a thin string runs from the Gubble to it (a balloon on a string) and a ring marks the spot it is led to, with a column
///   above it when that spot is a lift.
/// - Selected (the smart ask's first right-click), it wears what a thing that can be carried wears (FocusLook): a warm light line
///   over a darker one just outside its silhouette, breathing slowly, and a warm wash in its film.
/// A handful at most (BubbleLaws.MaxBubbles and MaxGhosts), each a few meshes, moved by setting positions: no allocation while a
/// bubble drifts. Visual only: it never decides anything the kernel does.
/// Everything a bubble may wear (the string, the ring on the ground, a lift's column, the focus ring) is made when it is cast and shown
/// for a few frames too small to see, so the materials, the ring's picture and the renderer's first draws of each cost their hitch
/// then, with the cast, and not in the frame of the first right-click that chooses it or leads it (the first capture: 60 to 90 ms each).
/// The HUD asks WarmBubble once the room is up: a bubble of nothing is drawn in front of the eye for those frames, before anyone casts
/// one, so the first cast's own frames (the first capture: 108 and 52 ms) are no longer the renderer's first look at the film.
/// (The selection is no FocusHighlight overlay: its outline pass fills a see-through shell's whole disc with the line's colour, so a
/// bubble drawn with it looked half full of liquid. The same two-tone warm line is drawn as a ring round the bubble's silhouette
/// instead, in FocusLook's own colours, and the film takes a warm wash.)
/// </summary>
public partial class LookDirector
{
    private sealed class BubbleDraw
    {
        public string Id = "";
        public Node3D Root = null!;
        public MeshInstance3D Shell = null!;
        public MeshInstance3D? Deck, String, Mark, Column, Halo;
        public ShaderMaterial Film = null!;
        public StandardMaterial3D? StringSkin, MarkSkin, ColumnSkin, HaloSkin;
        public float Radius, Phase, Age, Lift, BaseRim = 0.5f;
        public bool Boat, Ghost;
        /// <summary>Frames left of the first, too-small-to-see showing of its parts (the renderer's first draw of each material).</summary>
        public int Warm;
        public BubbleDrawState State = BubbleDrawState.Plain;
        public bool Selected;
        public double SinceLight = double.MaxValue;
        public float Light = 1f;
    }

    private readonly Dictionary<string, BubbleDraw> _bubbleDraws = new();
    /// <summary>The id of the bubble of nothing WarmBubble draws (no kernel bubble has this id).</summary>
    public const string WarmBubbleId = "warmup:film";
    private bool _bubbleWarmPending;
    private ImageTexture? _bubbleRingTexture;
    private string? _selectedBubble;
    private static readonly StringName FilmRimAlpha = "rim_alpha", FilmTintAlpha = "tint_alpha", FilmIridescence = "iridescence", FilmBands = "film_bands",
        FilmShimmerSpeed = "shimmer_speed", FilmGlint = "glint", FilmAura = "aura_color", FilmTint = "tint_color";

    /// <summary>How many bubbles and ghosts the look draws now.</summary>
    public int BubbleDrawCount => _bubbleDraws.Count;

    /// <summary>The bubble the smart ask has selected, or null.</summary>
    public string? SelectedBubble => _selectedBubble;

    /// <summary>A drawn bubble's root, placed by the kernel (null for an id not drawn): for tests and captures.</summary>
    public Node3D? BubbleRoot(string id) => id != null && _bubbleDraws.TryGetValue(id, out var draw) ? draw.Root : null;

    /// <summary>A drawn bubble's film mesh (the sphere or a boat's dome).</summary>
    public MeshInstance3D? BubbleShell(string id) => id != null && _bubbleDraws.TryGetValue(id, out var draw) ? draw.Shell : null;

    /// <summary>What the look draws of a bubble now (the HUD's last word), or null for an id not drawn.</summary>
    public BubbleDrawState? BubbleStateOf(string id) => id != null && _bubbleDraws.TryGetValue(id, out var draw) ? draw.State : null;

    /// <summary>Test and capture views of the parts a bubble wears: its string, the mark at the spot it is led to, that mark's column, a boat's deck.</summary>
    public MeshInstance3D? BubbleString(string id) => id != null && _bubbleDraws.TryGetValue(id, out var draw) ? draw.String : null;
    public MeshInstance3D? BubbleMark(string id) => id != null && _bubbleDraws.TryGetValue(id, out var draw) ? draw.Mark : null;
    public MeshInstance3D? BubbleColumn(string id) => id != null && _bubbleDraws.TryGetValue(id, out var draw) ? draw.Column : null;
    public MeshInstance3D? BubbleDeck(string id) => id != null && _bubbleDraws.TryGetValue(id, out var draw) ? draw.Deck : null;
    /// <summary>The ring a selected bubble wears (the focus line, as FocusLook draws it for a thing to carry), or null while it was never selected.</summary>
    public MeshInstance3D? BubbleHalo(string id) => id != null && _bubbleDraws.TryGetValue(id, out var draw) ? draw.Halo : null;
    /// <summary>A drawn bubble's film material (for tests and captures).</summary>
    public ShaderMaterial? BubbleFilm(string id) => id != null && _bubbleDraws.TryGetValue(id, out var draw) ? draw.Film : null;
    /// <summary>How far a drawn bubble is stretched by a lift now (0 to 1).</summary>
    public float BubbleLiftShown(string id) => id != null && _bubbleDraws.TryGetValue(id, out var draw) ? draw.Lift : 0f;

    /// <summary>
    /// Have the renderer draw every part of a bubble once now, out of sight (a bubble too small to see in front of the eye, for a few
    /// frames), so the materials and pipelines are made before the first cast and not in its frames. Done once the eye exists; asking
    /// again while one is pending does nothing.
    /// </summary>
    public void WarmBubble() => _bubbleWarmPending = true;

    /// <summary>Draw a bubble (or its ghost) at a place. An id already drawn is moved instead.</summary>
    public void ShowBubble(string id, Vector3 center, float radiusM, bool boat, bool ghost)
    {
        if (string.IsNullOrEmpty(id) || !center.IsFinite() || !float.IsFinite(radiusM) || radiusM <= 0) return;
        var began = BubbleCost.Begin();
        MakeBubble(id, center, radiusM, boat, ghost);
        BubbleCost.End(began, BubbleCost.Piece.Show);
    }

    private void MakeBubble(string id, Vector3 center, float radiusM, bool boat, bool ghost)
    {
        if (_bubbleDraws.TryGetValue(id, out var drawn))
        {
            drawn.Root.Position = center;
            return;
        }
        var aura = AuraOf(CompanionBody()) ?? BubbleLook.Default.DefaultAura;
        var draw = new BubbleDraw { Id = id, Radius = radiusM, Boat = boat, Ghost = ghost, Phase = Mathf.Abs(Seed(id)) * 6.2831853f };
        draw.State = draw.State with { Form = boat ? BubbleForm.Boat : BubbleForm.Carry };
        draw.Film = MaterialLibrary.ForBubble(BubbleDrawTuning.FilmTint, aura, BubbleHem.None);
        draw.Film.SetShaderParameter(FilmGlint, ghost ? BubbleDrawTuning.GhostGlint : BubbleLook.Default.Glint);
        draw.Root = new Node3D { Name = ghost ? "BubbleGhost" : boat ? "Boat" : "Bubble", Position = center };
        Mesh mesh = boat
            ? new SphereMesh { Radius = radiusM, Height = radiusM, IsHemisphere = true, RadialSegments = 24, Rings = 8 }
            : new SphereMesh { Radius = radiusM, Height = radiusM * 2, RadialSegments = 24, Rings = 12 };
        draw.Shell = new MeshInstance3D
        {
            Name = "Film", Mesh = mesh, MaterialOverride = draw.Film, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
        };
        draw.Root.AddChild(draw.Shell);
        if (boat)
        {
            draw.Deck = new MeshInstance3D
            {
                Name = "Deck", Mesh = new CylinderMesh { TopRadius = radiusM, BottomRadius = radiusM, Height = BubbleDrawTuning.DeckThicknessM, RadialSegments = 24, Rings = 1 },
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    AlbedoColor = new Color(aura.R * 0.5f + 0.5f, aura.G * 0.5f + 0.5f, aura.B * 0.5f + 0.5f, BubbleDrawTuning.DeckAlpha), ResourceName = "bubble deck",
                },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
                Position = Vector3.Down * (BubbleDrawTuning.DeckThicknessM * 0.5f),
            };
            draw.Root.AddChild(draw.Deck);
        }
        _bubbleDraws[id] = draw;
        StyleBubble(draw);
        AddChild(draw.Root);
        // A ghost is a preview of a cast on the player: it wears no string, ring or mark. A bubble wears them all, hidden.
        if (ghost) return;
        MakeBubbleParts(draw);
        draw.Warm = BubbleDrawTuning.WarmFrames;
    }

    /// <summary>A drifting bubble's place, every tick it moves.</summary>
    public void MoveBubble(string id, Vector3 center)
    {
        if (id != null && center.IsFinite() && _bubbleDraws.TryGetValue(id, out var drawn)) drawn.Root.Position = center;
    }

    /// <summary>A bubble popped or a ghost went: its drawing goes. An unknown id is a no-op.</summary>
    public void HideBubble(string id)
    {
        if (id == null || !_bubbleDraws.Remove(id, out var drawn)) return;
        if (id == _selectedBubble) _selectedBubble = null;
        if (IsInstanceValid(drawn.Root)) drawn.Root.QueueFree();
        FreeBubbleParts(drawn);
    }

    /// <summary>
    /// What the HUD adds from the kernel's view of the bubble (CommandHost.ActiveBubble): what it is, whether it holds the player, the
    /// spot it is led to, whether that is a lift, and whether it is rising. An unknown id is a no-op. Only a change redraws.
    /// </summary>
    public void SetBubbleState(string id, BubbleDrawState state)
    {
        if (id == null || !_bubbleDraws.TryGetValue(id, out var draw) || draw.Ghost || draw.State == state) return;
        // A boat stays a boat: the kernel drew it as a dome.
        if (draw.Boat) state = state with { Form = BubbleForm.Boat };
        draw.State = state;
        StyleBubble(draw);
    }

    /// <summary>
    /// The smart ask selected this bubble (or let it go: null, or an id not drawn): it wears the focus line, as a thing that can be
    /// carried does (a ring just outside it in FocusLook's colours, and a warm wash in its film). One at a time.
    /// </summary>
    public void SelectBubble(string? id)
    {
        if (id != null && !_bubbleDraws.ContainsKey(id)) id = null;
        if (id == _selectedBubble) return;
        if (_selectedBubble != null && _bubbleDraws.TryGetValue(_selectedBubble, out var before))
        {
            before.Selected = false;
            StyleBubble(before);
        }
        _selectedBubble = id;
        if (id == null) return;
        var draw = _bubbleDraws[id];
        draw.Selected = true;
        StyleBubble(draw);
    }

    /// <summary>Every bubble's rim takes the Gubble's aura now (the HUD calls this through RecolorGlows when its colour changes).</summary>
    private void RecolorBubbles()
    {
        var aura = AuraOf(CompanionBody()) ?? BubbleLook.Default.DefaultAura;
        foreach (var draw in _bubbleDraws.Values) draw.Film.SetShaderParameter(FilmAura, aura);
    }

    private static void FreeBubbleParts(BubbleDraw draw)
    {
        foreach (var part in new[] { draw.String, draw.Mark, draw.Column })
            if (part != null && IsInstanceValid(part)) part.QueueFree();
    }

    /// <summary>The film's numbers for a bubble's state: what holds, waits, lifts, is led or is chosen.</summary>
    private void StyleBubble(BubbleDraw draw)
    {
        var state = draw.State;
        float tint, rim, iridescence = BubbleLook.Default.Iridescence, bands = BubbleLook.Default.FilmBands, shimmer = BubbleLook.Default.ShimmerSpeed;
        if (draw.Ghost) (tint, rim) = (BubbleDrawTuning.GhostTintAlpha, BubbleDrawTuning.GhostRimAlpha);
        else if (state.Holding) (tint, rim) = (BubbleDrawTuning.HoldingTintAlpha, BubbleDrawTuning.HoldingRimAlpha);
        else (tint, rim) = (BubbleDrawTuning.WaitingTintAlpha, BubbleDrawTuning.WaitingRimAlpha);
        if (state.Led) shimmer = BubbleDrawTuning.LedShimmerSpeed;
        if (state.Lifting)
        {
            rim = Mathf.Max(rim, BubbleDrawTuning.LiftRimAlpha);
            (iridescence, bands, shimmer) = (BubbleDrawTuning.LiftIridescence, BubbleDrawTuning.LiftFilmBands, BubbleDrawTuning.LiftShimmerSpeed);
        }
        if (draw.Selected)
        {
            rim = Mathf.Min(1f, rim * BubbleDrawTuning.SelectedRimBoost);
            tint += BubbleDrawTuning.SelectedTintMore;
        }
        draw.BaseRim = rim;
        draw.Film.SetShaderParameter(FilmTint, draw.Selected ? FocusLook.Default.RimColor : BubbleDrawTuning.FilmTint);
        draw.Film.SetShaderParameter(FilmTintAlpha, tint);
        draw.Film.SetShaderParameter(FilmRimAlpha, rim);
        draw.Film.SetShaderParameter(FilmIridescence, iridescence);
        draw.Film.SetShaderParameter(FilmBands, bands);
        draw.Film.SetShaderParameter(FilmShimmerSpeed, shimmer);
        EnsureBubbleParts(draw);
    }

    /// <summary>The string, the ring at the spot, a lift's column and the focus ring (not a ghost's): made once, when the bubble is cast, shown as the state asks.</summary>
    private void EnsureBubbleParts(BubbleDraw draw)
    {
        if (draw.Warm > 0) return;
        ApplyPartVisibility(draw);
    }

    private void ApplyPartVisibility(BubbleDraw draw)
    {
        var state = draw.State;
        if (draw.String != null) draw.String.Visible = state.Led;
        if (draw.Mark != null) draw.Mark.Visible = state.Led;
        if (draw.Column != null) draw.Column.Visible = state.Led && state.LedLift;
        if (draw.Halo != null) draw.Halo.Visible = draw.Selected;
    }

    /// <summary>Makes every part a bubble may wear, hidden (a ghost wears none).</summary>
    private void MakeBubbleParts(BubbleDraw draw)
    {
        EnsureHalo(draw);
        var color = AuraOf(CompanionBody()) ?? BubbleLook.Default.DefaultAura;
        if (draw.String == null)
        {
            draw.StringSkin = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ResourceName = "bubble string",
                AlbedoColor = new Color(color.R * 0.4f + 0.6f, color.G * 0.4f + 0.6f, color.B * 0.4f + 0.6f, BubbleDrawTuning.StringAlpha),
            };
            draw.String = new MeshInstance3D
            {
                Name = "BubbleString", Mesh = new CylinderMesh { TopRadius = BubbleDrawTuning.StringRadiusM, BottomRadius = BubbleDrawTuning.StringRadiusM, Height = 1f, RadialSegments = 5, Rings = 0, CapTop = false, CapBottom = false },
                MaterialOverride = draw.StringSkin, TopLevel = true, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
                PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off,
            };
            AddChild(draw.String);
        }
        if (draw.Mark == null)
        {
            draw.MarkSkin = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled, ResourceName = "bubble mark",
                AlbedoColor = new Color(color.R, color.G, color.B, BubbleDrawTuning.MarkAlpha),
            };
            draw.Mark = new MeshInstance3D
            {
                Name = "BubbleMark", Mesh = new TorusMesh { InnerRadius = BubbleDrawTuning.MarkInnerM, OuterRadius = BubbleDrawTuning.MarkOuterM, Rings = 24, RingSegments = 6 },
                MaterialOverride = draw.MarkSkin, TopLevel = true, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
                PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off,
            };
            AddChild(draw.Mark);
        }
        if (draw.Column == null)
        {
            draw.ColumnSkin = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled, ResourceName = "bubble lift column",
                AlbedoColor = new Color(color.R, color.G, color.B, BubbleDrawTuning.ColumnAlpha),
            };
            draw.Column = new MeshInstance3D
            {
                Name = "BubbleLiftColumn", Mesh = new CylinderMesh { TopRadius = BubbleDrawTuning.ColumnRadiusM, BottomRadius = BubbleDrawTuning.ColumnRadiusM * 2.2f, Height = BubbleDrawTuning.ColumnHeightM, RadialSegments = 8, Rings = 1, CapTop = false, CapBottom = false },
                MaterialOverride = draw.ColumnSkin, TopLevel = true, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
                PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off,
            };
            AddChild(draw.Column);
        }
        ApplyPartVisibility(draw);
    }

    /// <summary>
    /// The selection's ring: a quad that always faces the eye, a little wider than the bubble, with the focus line drawn on it (a
    /// light line over a darker one, FocusLook's colours), made when the bubble is first selected and shown only while it is.
    /// </summary>
    private void EnsureHalo(BubbleDraw draw)
    {
        if (!draw.Ghost && draw.Halo == null)
        {
            var size = draw.Radius * 2f * (1f + BubbleDrawTuning.HaloMargin);
            draw.HaloSkin = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled, BillboardKeepScale = true, AlbedoTexture = BubbleRingTexture(), DisableReceiveShadows = true,
                ResourceName = "bubble focus ring",
            };
            draw.Halo = new MeshInstance3D
            {
                Name = "FocusRing", Mesh = new QuadMesh { Size = new Vector2(size, size) }, MaterialOverride = draw.HaloSkin,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            };
            draw.Root.AddChild(draw.Halo);
        }
        if (draw.Halo != null) draw.Halo.Visible = draw.Selected;
    }

    /// <summary>The ring's picture: FocusLook's under line (wide, dark) and its line (narrow, light) as two bands at the quad's edge, on nothing.</summary>
    private ImageTexture BubbleRingTexture()
    {
        if (_bubbleRingTexture != null) return _bubbleRingTexture;
        // Written into one byte array and handed over whole: a SetPixel call for each of 65,536 pixels stalled the frame that first chose a
        // bubble for 60 ms (the capture's slowest frame), where this is a millisecond or two.
        const int size = 160;
        var look = FocusLook.Default;
        var bytes = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var d = new Vector2((x + 0.5f) / size * 2f - 1f, (y + 0.5f) / size * 2f - 1f).Length();
                // The dark line from 0.915 to 1.0 of the edge, the light one 0.935 to 0.985 over it, both soft at their sides.
                var under = Mathf.SmoothStep(0.915f, 0.935f, d) * (1f - Mathf.SmoothStep(0.985f, 1f, d));
                var line = Mathf.SmoothStep(0.935f, 0.948f, d) * (1f - Mathf.SmoothStep(0.972f, 0.985f, d));
                var a = line * look.LineColor.A;
                var u = under * look.UnderColor.A * (1f - a);
                var total = a + u;
                var mix = total > 1e-4f ? u / total : 0f;
                var at = (y * size + x) * 4;
                bytes[at] = (byte)Mathf.RoundToInt(Mathf.Lerp(look.LineColor.R, look.UnderColor.R, mix) * 255f);
                bytes[at + 1] = (byte)Mathf.RoundToInt(Mathf.Lerp(look.LineColor.G, look.UnderColor.G, mix) * 255f);
                bytes[at + 2] = (byte)Mathf.RoundToInt(Mathf.Lerp(look.LineColor.B, look.UnderColor.B, mix) * 255f);
                bytes[at + 3] = (byte)Mathf.RoundToInt(Mathf.Clamp(total, 0f, 1f) * 255f);
            }
        _bubbleRingTexture = ImageTexture.CreateFromImage(Image.CreateFromData(size, size, false, Image.Format.Rgba8, bytes));
        return _bubbleRingTexture;
    }

    /// <summary>
    /// A bubble's first frames: every part shown too small to see, at the bubble, so the renderer draws each material once now (and
    /// compiles what it must) instead of in the frame of the right-click that first wants it. Then the state decides what shows.
    /// </summary>
    private void WarmParts(BubbleDraw draw)
    {
        var at = draw.Root.GetGlobalTransformInterpolated().Origin;
        var tiny = new Transform3D(Basis.Identity.Scaled(Vector3.One * 1e-4f), at);
        foreach (var part in new[] { draw.String, draw.Mark, draw.Column })
            if (part != null)
            {
                part.Visible = true;
                part.GlobalTransform = tiny;
            }
        if (draw.Halo != null)
        {
            draw.Halo.Visible = true;
            draw.Halo.Scale = Vector3.One * 1e-4f;
        }
        if (--draw.Warm > 0) return;
        if (draw.Halo != null) draw.Halo.Scale = Vector3.One;
        ApplyPartVisibility(draw);
    }

    /// <summary>A basis whose Y axis runs along a direction (unit) and is length long: a thin cylinder (height 1) laid between two points.</summary>
    private static Basis AlongY(Vector3 direction, float length)
    {
        var y = direction;
        var helper = Mathf.Abs(y.Dot(Vector3.Right)) < 0.9f ? Vector3.Right : Vector3.Forward;
        var x = helper.Cross(y).Normalized();
        return new Basis(x, y * length, x.Cross(y).Normalized());
    }

    /// <summary>Every frame: a waiting bubble breathes and bobs, a lift stretches, the string runs from the Gubble, the mark breathes at the spot, the film reads the light.</summary>
    private void UpdateBubbles(double delta)
    {
        if (_bubbleWarmPending && IsInsideTree() && GetViewport().GetCamera3D() is { } eye && eye.IsInsideTree())
        {
            _bubbleWarmPending = false;
            ShowBubble(WarmBubbleId, ToLocal(eye.GlobalPosition - eye.GlobalBasis.Z * 0.6f), 0.08f, boat: false, ghost: false);
            if (_bubbleDraws.TryGetValue(WarmBubbleId, out var warming)) warming.Root.Scale = Vector3.One * 1e-4f;
        }
        if (_bubbleDraws.Count == 0) return;
        var began = BubbleCost.Begin();
        DrawBubbles(delta);
        BubbleCost.End(began, BubbleCost.Piece.Draw);
        // The bubble of nothing has been drawn; it goes (never from inside the loop that draws it).
        if (_bubbleDraws.TryGetValue(WarmBubbleId, out var warm) && warm.Warm == 0) HideBubble(WarmBubbleId);
    }

    private void DrawBubbles(double delta)
    {
        var dt = (float)Math.Min(delta, 0.1);
        var gubble = GubbleMiddle();
        foreach (var draw in _bubbleDraws.Values)
        {
            if (!IsInstanceValid(draw.Root)) continue;
            draw.Age += dt;
            var waiting = !draw.Ghost && !draw.State.Holding && draw.State.Form == BubbleForm.WalkIn;
            // Waiting at a spot it breathes (its rim) and bobs a little, an invitation; held or a boat it is steady.
            var breath = waiting ? 1f + BubbleDrawTuning.BreathShare * Mathf.Sin(draw.Age * Mathf.Tau / BubbleDrawTuning.BreathPeriodS + draw.Phase) : 1f;
            draw.Film.SetShaderParameter(FilmRimAlpha, Mathf.Min(1f, draw.BaseRim * breath));
            var bob = waiting ? BubbleDrawTuning.BobM * Mathf.Sin(draw.Age * Mathf.Tau / BubbleDrawTuning.BobPeriodS + draw.Phase) : 0f;
            draw.Lift = Mathf.MoveToward(draw.Lift, draw.State.Lifting ? 1f : 0f, dt / BubbleDrawTuning.LiftEaseS);
            var stretch = 1f + BubbleDrawTuning.LiftStretch * draw.Lift * draw.Lift * (3f - 2f * draw.Lift);
            draw.Shell.Position = new Vector3(0, bob, 0);
            draw.Shell.Scale = new Vector3(1f, stretch, 1f);
            if (draw.HaloSkin != null && draw.Selected)
            {
                // The focus line breathes slowly (FocusLook), and follows a lift's stretch.
                var focus = FocusLook.Default;
                var glow = 1f - focus.Breath * (0.5f + 0.5f * Mathf.Sin(draw.Age * Mathf.Tau / focus.BreathPeriodS));
                draw.HaloSkin.AlbedoColor = new Color(1f, 1f, 1f, glow);
                draw.Halo!.Position = new Vector3(0, bob, 0);
                draw.Halo.Scale = Vector3.One * (1f + 0.5f * BubbleDrawTuning.LiftStretch * draw.Lift);
            }
            // The film is unshaded: it darkens with the place, read four times a second as the Gubble's own bubble does.
            draw.SinceLight += delta;
            if (draw.SinceLight >= BubbleDrawTuning.LightIntervalS && IsInsideTree())
            {
                draw.SinceLight = 0;
                var level = LightLevelAt(draw.Root.GlobalPosition);
                draw.Light = float.IsFinite(level) ? Mathf.Clamp(level, 0f, 1f) : 1f;
                MaterialLibrary.SetBubbleLight(draw.Film, draw.Light);
            }
            if (draw.Warm > 0)
            {
                WarmParts(draw);
                continue;
            }
            if (!draw.State.Led) continue;
            var centre = draw.Root.GetGlobalTransformInterpolated().Origin;
            if (draw.String != null && draw.String.Visible)
            {
                // From the Gubble's middle to the bubble's skin on the side that faces it: a balloon on a string.
                var from = gubble ?? centre + Vector3.Up * (draw.Radius * 1.6f);
                var reach = centre - from;
                var length = reach.Length();
                if (length > draw.Radius + 0.004f)
                {
                    var along = reach / length;
                    var skin = centre - along * (draw.Radius * stretch * 0.97f);
                    var span = skin - from;
                    draw.String.GlobalTransform = new Transform3D(AlongY(span.Normalized(), span.Length()), from + span * 0.5f);
                    draw.String.Visible = true;
                }
                else draw.String.Visible = false;
            }
            if (draw.Mark != null && draw.Mark.Visible)
            {
                var swell = 1f + BubbleDrawTuning.MarkBreathShare * Mathf.Sin(draw.Age * Mathf.Tau / BubbleDrawTuning.MarkBreathPeriodS);
                draw.Mark.GlobalTransform = new Transform3D(Basis.Identity.Scaled(new Vector3(swell, 1f, swell)), draw.State.LedTo + Vector3.Up * BubbleDrawTuning.MarkLiftM);
            }
            if (draw.Column != null && draw.Column.Visible)
                draw.Column.GlobalTransform = new Transform3D(Basis.Identity, draw.State.LedTo + Vector3.Up * (BubbleDrawTuning.ColumnHeightM * 0.5f + BubbleDrawTuning.MarkLiftM));
        }
    }
}
