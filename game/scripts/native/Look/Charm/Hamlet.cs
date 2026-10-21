using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GarageGame.Native.Room;

namespace GarageGame.Native.Look.Charm;

/// <summary>The hamlet's things the charm hangs on or keeps clear of: the generator's kit (pipeline/landscape/harness/kit.py) and its own yard things.</summary>
public enum HamletKind { Cottage, Tower, Fence, Lantern, DryingLine, Woodpile, Crate, Footbridge }

/// <summary>
/// One of the hamlet's things as the room places it: its kind, where its bottom centre stands, how it is turned and its size
/// (the asset's dimensions times the instance's scale). The kit's shapes are known as fractions of the kit's own size, so a
/// point of the thing is <see cref="At"/>(fraction): (0, 0, 0) its bottom centre, x across, y up, z toward its back; a cottage's
/// door and windows face its local -Z.
/// </summary>
public sealed record HamletThing(string Id, HamletKind Kind, Vector3 PositionM, Basis Turn, Vector3 SizeM)
{
    /// <summary>A point of the thing from a fraction of its size (component-wise), in the world.</summary>
    public Vector3 At(Vector3 fraction) => PositionM + Turn * (fraction * SizeM);
    /// <summary>The way its door faces (its local -Z), level.</summary>
    public Vector3 DoorSide => Flat(Turn * Vector3.Forward);
    /// <summary>Its local +X, level.</summary>
    public Vector3 Across => Flat(Turn * Vector3.Right);
    public Vector2 Ground => new(PositionM.X, PositionM.Z);

    private static Vector3 Flat(Vector3 v)
    {
        v.Y = 0f;
        return v.LengthSquared() < 1e-12f ? Vector3.Forward : v.Normalized();
    }
}

/// <summary>Something of the room's a charm piece must never be inside: every object's box (a stone, a log, a cottage), turned as the object is.</summary>
public readonly record struct ObjectBox(string Id, Vector3 PositionM, Basis Turn, Vector3 SizeM)
{
    /// <summary>Whether a point is inside the box, shrunk by insetM on every side (so a point on its face is not inside).</summary>
    public bool Holds(Vector3 point, float insetM = 0f)
    {
        var local = Turn.Inverse() * (point - PositionM);
        return Mathf.Abs(local.X) < SizeM.X * 0.5f - insetM && Mathf.Abs(local.Z) < SizeM.Z * 0.5f - insetM
            && local.Y > insetM && local.Y < SizeM.Y - insetM;
    }
}

/// <summary>
/// The hamlet found in a room's objects (Run 2, L11: docs/look/CHARM.md). The exporter (pipeline/landscape/export/exporter.py)
/// writes each kit thing with an asset whose <c>category</c> is the generator's prototype token (cottage, tower, fence, lantern,
/// crate, drying_line; a woodpile's logs keep "woodpile"), a display name made from it ("Cottage 2", "Drying line 15", "Log")
/// and an id that never carries it (obj:scatter_0001, obj:loose_s_0003_0000). So the category is read first and the display
/// name only when the category is not a token (an authored object's own kind). Only the room's data: deterministic.
/// </summary>
public sealed class HamletSurvey
{
    public IReadOnlyList<HamletThing> Things { get; }
    /// <summary>Every object's box (the hamlet's and every other), the charm's pieces stay out of them.</summary>
    public IReadOnlyList<ObjectBox> Boxes { get; }

    private HamletSurvey(IReadOnlyList<HamletThing> things, IReadOnlyList<ObjectBox> boxes)
    {
        Things = things;
        Boxes = boxes;
    }

    public IEnumerable<HamletThing> OfKind(HamletKind kind) => Things.Where(t => t.Kind == kind);
    public int Count(HamletKind kind) => Things.Count(t => t.Kind == kind);

    public static HamletSurvey Of(RoomData room)
    {
        var things = new List<HamletThing>();
        var boxes = new List<ObjectBox>();
        foreach (var o in room.Objects)
        {
            var size = o.Asset.DimensionsM * o.Scale;
            var turn = new Basis(o.Rotation.Normalized());
            boxes.Add(new ObjectBox(o.Id, o.PositionM, turn, size));
            if (KindOf(o.Asset.Category, o.DisplayName ?? o.Asset.DisplayName) is { } kind)
                things.Add(new HamletThing(o.Id, kind, o.PositionM, turn, size));
        }
        // The id order: the same room gives the same hamlet in the same order, whatever the manifest's order.
        things.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return new HamletSurvey(things, boxes);
    }

    /// <summary>The kit kind an object is, from its asset's category (the prototype token) or else its display name; null when it is none.</summary>
    public static HamletKind? KindOf(string category, string? displayName)
    {
        if (Token(category) is { } fromCategory) return fromCategory;
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        // "Drying line 15" -> drying_line: the exporter's display name is the token with spaces for underscores, capitalised and numbered.
        var words = displayName.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[^1].All(char.IsDigit)) words.RemoveAt(words.Count - 1);
        return Token(string.Join('_', words));
    }

    private static HamletKind? Token(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        "cottage" => HamletKind.Cottage,
        "tower" => HamletKind.Tower,
        "fence" => HamletKind.Fence,
        "lantern" => HamletKind.Lantern,
        "drying_line" => HamletKind.DryingLine,
        "woodpile" => HamletKind.Woodpile,
        "crate" => HamletKind.Crate,
        "footbridge" => HamletKind.Footbridge,
        _ => null,
    };
}

/// <summary>A chimney the smoke rises from: the middle of its top, how wide it is, and its own phase (from the room and the cottage).</summary>
public sealed record Chimney(string CottageId, Vector3 TopM, float WidthM, float Phase);

/// <summary>A window's face, lit warm when the lamps are on: its middle (just off the wall), its frame (x across, y up, z out of the wall) and its size.</summary>
public sealed record Pane(string CottageId, Vector3 CentreM, Basis Frame, Vector2 SizeM, float Phase);

/// <summary>A lantern's glow: the middle of its cloth, the halo's width, its own flicker phase.</summary>
public sealed record LanternGlow(string LanternId, Vector3 CentreM, float SizeM, float Phase);

/// <summary>What a string between two of the hamlet's things carries.</summary>
public enum StringLoad { Bunting, Laundry }

/// <summary>
/// A string hung between two anchors (an eave's corner, a tower's eave, a tree's trunk) with a sag: a parabola close enough to a
/// catenary at a sag of a tenth of the span. S runs from 0 at From to 1 at To.
/// </summary>
public sealed record Span(string FromId, string ToId, Vector3 FromM, Vector3 ToM, float SagM, StringLoad Load, float Phase)
{
    public float LengthM => FromM.DistanceTo(ToM);
    public Vector3 At(float s) => FromM.Lerp(ToM, s) + Vector3.Down * (4f * SagM * s * (1f - s));
    public Vector3 Tangent(float s) => (ToM - FromM + Vector3.Down * (4f * SagM * (1f - 2f * s))).Normalized();
}

/// <summary>A piece of cloth on a string, a bunting flag or a garment: where its top middle hangs, its frame (x along the string, y up, z its face), its width and drop, colour, where on the span it hangs and its phase.</summary>
public sealed record Cloth(int SpanIndex, float S, Vector3 TopM, Basis Frame, float WidthM, float DropM, Color Colour, float Phase);

/// <summary>A fairy light's bulb: where it hangs, its twinkle's phase and its tint.</summary>
public sealed record Bulb(Vector3 AtM, float Phase, Color Tint);

/// <summary>
/// Everything the charm draws on a hamlet, worked out once from the room's data and the ground (Run 2, L11): pure and
/// deterministic from the room's id, the charm's seed and the survey. The rules where a piece may go:
/// <list type="bullet">
/// <item>it hangs from the hamlet's own things (chimneys, windows, lanterns, eaves, fence tops), so it is never placed on the ground;</item>
/// <item>a string never passes within SpawnClearM of a spawn, never hangs over the sea (ground below its level) and keeps HeadroomM
/// between its lowest flag or garment and the ground under it, a path, a pond's bed or a meadow alike, so a body walks under it;</item>
/// <item>no piece is inside another object's box (only the thing it hangs from), and a string crossing a box is left out.</item>
/// </list>
/// Whatever is left out is said in Notes.
/// </summary>
public sealed class CharmPlan
{
    public List<Chimney> Chimneys { get; } = new();
    public List<Pane> Windows { get; } = new();
    public List<LanternGlow> Lanterns { get; } = new();
    public List<Span> Strings { get; } = new();
    public List<Cloth> Flags { get; } = new();
    public List<Cloth> Garments { get; } = new();
    public List<Bulb> Bulbs { get; } = new();
    public List<string> Notes { get; } = new();

    /// <summary>A tree that can hold one end of a string: where its trunk stands, how thick it is there and how high its trunk goes.</summary>
    public readonly record struct Tree(Vector3 BaseM, float TrunkRadiusM, float TrunkTopM);

    /// <summary>
    /// The plan for a room. groundAt gives the ground's height under a point (null: unknown there, so the anchors' own footing is
    /// used); seaLevelM the sea's level (null: no sea).
    /// </summary>
    public static CharmPlan Make(string roomId, HamletSurvey survey, IReadOnlyList<SpawnPoint> spawns, IReadOnlyList<Tree> trees,
        Func<Vector2, float?> groundAt, float? seaLevelM, IReadOnlyList<Color> accents, int seed)
    {
        var plan = new CharmPlan();
        var cottages = survey.OfKind(HamletKind.Cottage).ToList();
        foreach (var cottage in cottages)
        {
            plan.Chimneys.Add(new Chimney(cottage.Id, cottage.At(CharmLook.Tuning.ChimneyTop), CharmLook.Tuning.ChimneyWidth * cottage.SizeM.X, Hash(roomId, seed, cottage.Id, 1)));
            foreach (var side in new[] { -1f, 1f })
            {
                var fraction = CharmLook.Tuning.WindowCentre * new Vector3(side, 1f, 1f);
                var outward = cottage.DoorSide;
                var centre = cottage.At(fraction) + outward * CharmLook.Tuning.PaneOffsetM;
                var frame = new Basis(cottage.Across, Vector3.Up, outward);
                var size = new Vector2(CharmLook.Tuning.WindowSize.X * cottage.SizeM.X, CharmLook.Tuning.WindowSize.Y * cottage.SizeM.Y) * CharmLook.Tuning.PaneGrow;
                plan.Windows.Add(new Pane(cottage.Id, centre, frame, size, Hash(roomId, seed, cottage.Id, side < 0f ? 2 : 3)));
            }
        }
        foreach (var lantern in survey.OfKind(HamletKind.Lantern))
            plan.Lanterns.Add(new LanternGlow(lantern.Id, lantern.At(CharmLook.Tuning.LanternCloth), CharmLook.Tuning.LanternHalo * lantern.SizeM.Y, Hash(roomId, seed, lantern.Id, 4)));

        plan.HangStrings(roomId, survey, cottages, spawns, trees, groundAt, seaLevelM, accents, seed);

        foreach (var fence in survey.OfKind(HamletKind.Fence))
            plan.FenceLights(roomId, fence, survey, seed);
        if (survey.Count(HamletKind.DryingLine) > 0)
            plan.Notes.Add($"{survey.Count(HamletKind.DryingLine)} drying line(s): their washing is the generator's own (life.py hangs four garments in the mesh), which no look can sway; the charm's laundry hangs on its own line between cottages");
        return plan;
    }

    // ---------- the strings between cottages (and towers, and a near tree) ----------

    private sealed record Anchor(string Id, Vector3[] Points, Vector3 Foot, bool IsTree = false);

    private void HangStrings(string roomId, HamletSurvey survey, List<HamletThing> cottages, IReadOnlyList<SpawnPoint> spawns, IReadOnlyList<Tree> trees,
        Func<Vector2, float?> groundAt, float? seaLevelM, IReadOnlyList<Color> accents, int seed)
    {
        var nodes = new List<Anchor>();
        foreach (var c in cottages)
            nodes.Add(new Anchor(c.Id, CharmLook.Tuning.EaveCorners.Select(c.At).ToArray(), c.PositionM));
        foreach (var t in survey.OfKind(HamletKind.Tower))
        {
            // A tower's round eave: eight points round it, so a string leaves from the side facing its partner.
            var eave = Enumerable.Range(0, 8).Select(k =>
            {
                var a = Mathf.Tau * k / 8f;
                return t.At(new Vector3(0.5f * Mathf.Cos(a), CharmLook.Tuning.TowerEave, 0.5f * Mathf.Sin(a)));
            }).ToArray();
            nodes.Add(new Anchor(t.Id, eave, t.PositionM));
        }
        if (nodes.Count == 0) return;

        // Every pair within reach, nearest corners first; the minimum spanning forest of the hamlet (each pair at most once, no loops).
        var candidates = new List<(float Length, int A, int B, Vector3 From, Vector3 To)>();
        for (var a = 0; a < nodes.Count; a++)
            for (var b = a + 1; b < nodes.Count; b++)
            {
                var (from, to) = Nearest(nodes[a].Points, nodes[b].Points);
                var length = from.DistanceTo(to);
                if (length >= CharmLook.Tuning.SpanMinM && length <= CharmLook.Tuning.SpanMaxM) candidates.Add((length, a, b, from, to));
            }
        candidates.Sort((x, y) => x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(nodes[x.A].Id + nodes[x.B].Id, nodes[y.A].Id + nodes[y.B].Id));
        var group = Enumerable.Range(0, nodes.Count).ToArray();
        int Root(int i) { while (group[i] != i) i = group[i] = group[group[i]]; return i; }
        var chosen = new List<(Anchor A, Anchor B, Vector3 From, Vector3 To)>();
        var strung = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (length, a, b, from, to) in candidates)
        {
            if (Root(a) == Root(b)) continue;
            if (Refuse(nodes[a], nodes[b], from, to, spawns, survey, groundAt, seaLevelM, CharmLook.Tuning.ClothDropMaxM) is { } why)
            {
                Notes.Add($"no string between {nodes[a].Id} and {nodes[b].Id}: {why}");
                continue;
            }
            group[Root(a)] = Root(b);
            chosen.Add((nodes[a], nodes[b], from, to));
            strung.Add(nodes[a].Id);
            strung.Add(nodes[b].Id);
        }
        // A cottage no other building reaches strings to its nearest tree within reach, at the trunk's side facing it.
        foreach (var node in nodes.Where(n => !strung.Contains(n.Id)))
        {
            var best = (Length: float.MaxValue, From: Vector3.Zero, To: Vector3.Zero, Tree: default(Tree));
            foreach (var tree in trees)
            {
                var flat = new Vector2(tree.BaseM.X - node.Foot.X, tree.BaseM.Z - node.Foot.Z);
                if (flat.Length() > CharmLook.Tuning.SpanMaxM + 0.5f) continue;
                foreach (var p in node.Points)
                {
                    var height = Mathf.Min(p.Y, tree.BaseM.Y + tree.TrunkTopM * CharmLook.Tuning.TreeAnchorHeight);
                    var toward = new Vector3(p.X - tree.BaseM.X, 0f, p.Z - tree.BaseM.Z);
                    if (toward.LengthSquared() < 1e-8f) continue;
                    var at = new Vector3(tree.BaseM.X, height, tree.BaseM.Z) + toward.Normalized() * tree.TrunkRadiusM;
                    var length = p.DistanceTo(at);
                    if (length < best.Length && length >= CharmLook.Tuning.SpanMinM && length <= CharmLook.Tuning.SpanMaxM) best = (length, p, at, tree);
                }
            }
            if (best.Length == float.MaxValue) continue;
            var treeAnchor = new Anchor("tree", new[] { best.To }, best.Tree.BaseM, IsTree: true);
            if (Refuse(node, treeAnchor, best.From, best.To, spawns, survey, groundAt, seaLevelM, CharmLook.Tuning.ClothDropMaxM) is { } why)
            {
                Notes.Add($"no string from {node.Id} to its tree: {why}");
                continue;
            }
            chosen.Add((node, treeAnchor, best.From, best.To));
        }

        // Loads: bunting first, then laundry, then alternating, in an order from the room and the seed (the same island, the same strings).
        var order = chosen.Select((c, i) => (Key: Hash(roomId, seed, c.A.Id + "|" + c.B.Id + "|" + c.From, 5), Index: i)).OrderBy(k => k.Key).ToList();
        var loads = new StringLoad[chosen.Count];
        for (var k = 0; k < order.Count; k++) loads[order[k].Index] = k % 2 == 0 ? StringLoad.Bunting : StringLoad.Laundry;
        for (var i = 0; i < chosen.Count; i++)
        {
            var (a, b, from, to) = chosen[i];
            var span = new Span(a.Id, b.IsTree ? "tree" : b.Id, from, to, CharmLook.Tuning.SagFraction * from.DistanceTo(to), loads[i], Hash(roomId, seed, a.Id + b.Id, 6));
            Strings.Add(span);
            if (span.Load == StringLoad.Bunting) HangBunting(roomId, Strings.Count - 1, span, accents, seed);
            else HangLaundry(roomId, Strings.Count - 1, span, seed);
        }
    }

    /// <summary>The nearest pair of points, one from each list (the first pair on ties: the lists' own order).</summary>
    private static (Vector3, Vector3) Nearest(Vector3[] a, Vector3[] b)
    {
        var best = (a[0], b[0]);
        var bestD = float.MaxValue;
        foreach (var p in a)
            foreach (var q in b)
            {
                var d = p.DistanceSquaredTo(q);
                if (d < bestD - 1e-9f) { bestD = d; best = (p, q); }
            }
        return best;
    }

    /// <summary>Why a string between two anchors may not hang (null: it may): the spawn, the sea, the headroom, another object's box.</summary>
    private static string? Refuse(Anchor a, Anchor b, Vector3 from, Vector3 to, IReadOnlyList<SpawnPoint> spawns, HamletSurvey survey,
        Func<Vector2, float?> groundAt, float? seaLevelM, float drop)
    {
        var span = new Span(a.Id, b.Id, from, to, CharmLook.Tuning.SagFraction * from.DistanceTo(to), StringLoad.Bunting, 0f);
        var flatFrom = new Vector2(from.X, from.Z);
        var flatTo = new Vector2(to.X, to.Z);
        foreach (var spawn in spawns)
        {
            var p = new Vector2(spawn.PositionM.X, spawn.PositionM.Z);
            if (Geometry2D.GetClosestPointToSegment(p, flatFrom, flatTo).DistanceTo(p) < CharmLook.Tuning.SpawnClearM)
                return $"it would pass within {CharmLook.Tuning.SpawnClearM} m of the spawn {spawn.Id}";
        }
        const int samples = 16;
        for (var k = 0; k <= samples; k++)
        {
            var s = k / (float)samples;
            var at = span.At(s);
            var footing = Mathf.Lerp(a.Foot.Y, b.Foot.Y, s);
            var ground = groundAt(new Vector2(at.X, at.Z)) ?? footing;
            if (seaLevelM is { } sea && ground < sea) return "it would hang over the sea";
            if (at.Y - drop - ground < CharmLook.Tuning.HeadroomM)
                return $"its washing would hang {(at.Y - drop - ground) * 100f:0.#} cm over the ground at {at.X:0.##}, {at.Z:0.##} (it keeps {CharmLook.Tuning.HeadroomM * 100f:0} cm)";
            // Only the anchors' own boxes may hold the string's ends; along the way it crosses no thing's box, nor the flags under it.
            foreach (var box in survey.Boxes)
            {
                if (box.Id == a.Id || box.Id == b.Id) continue;
                if (box.Holds(at) || box.Holds(at + Vector3.Down * drop)) return $"it would cross {box.Id}";
            }
            if (k is > 1 and < samples - 1)
                foreach (var box in survey.Boxes.Where(x => x.Id == a.Id || x.Id == b.Id))
                    if (box.Holds(at, CharmLook.Tuning.AnchorInsetM) || box.Holds(at + Vector3.Down * drop, CharmLook.Tuning.AnchorInsetM))
                        return $"it would pass through {box.Id}, the thing it hangs from";
        }
        return null;
    }

    private void HangBunting(string roomId, int index, Span span, IReadOnlyList<Color> accents, int seed)
    {
        var palette = accents.Count > 0 ? accents : CharmLook.Tuning.Accents;
        var count = Math.Max(2, (int)Math.Floor((span.LengthM - CharmLook.Tuning.FlagWidthM) / CharmLook.Tuning.FlagSpacingM) + 1);
        var used = (count - 1) * CharmLook.Tuning.FlagSpacingM;
        var start = 0.5f - used * 0.5f / span.LengthM;
        var offset = (int)(Hash(roomId, seed, span.FromId + span.ToId, 7) * palette.Count);
        for (var i = 0; i < count; i++)
        {
            var s = start + i * CharmLook.Tuning.FlagSpacingM / span.LengthM;
            Flags.Add(ClothAt(index, span, s, CharmLook.Tuning.FlagWidthM, CharmLook.Tuning.FlagDropM, palette[(offset + i) % palette.Count].SrgbToLinear(),
                Hash(roomId, seed, span.FromId + span.ToId, 100 + i)));
            // Fairy lights on the bunting's string, one between each two flags: lit at night.
            if (i < count - 1)
            {
                var between = s + 0.5f * CharmLook.Tuning.FlagSpacingM / span.LengthM;
                Bulbs.Add(new Bulb(span.At(between) + Vector3.Down * CharmLook.Tuning.BulbHangM, Hash(roomId, seed, span.FromId + span.ToId, 300 + i), BulbTint(roomId, seed, span.FromId + i)));
            }
        }
    }

    private void HangLaundry(string roomId, int index, Span span, int seed)
    {
        var margin = CharmLook.Tuning.LaundryEndsM;
        var x = margin;
        var i = 0;
        while (true)
        {
            var h = Hash(roomId, seed, span.FromId + span.ToId, 500 + i);
            var (width, drop) = CharmLook.Tuning.Garments[(int)(h * CharmLook.Tuning.Garments.Length) % CharmLook.Tuning.Garments.Length];
            if (x + width > span.LengthM - margin) break;
            var s = (x + width * 0.5f) / span.LengthM;
            var colour = CharmLook.Tuning.LaundryColours[(int)(Hash(roomId, seed, span.FromId + span.ToId, 600 + i) * CharmLook.Tuning.LaundryColours.Length) % CharmLook.Tuning.LaundryColours.Length];
            Garments.Add(ClothAt(index, span, s, width, drop, colour.SrgbToLinear(), Hash(roomId, seed, span.FromId + span.ToId, 700 + i)));
            x += width + CharmLook.Tuning.LaundryGapM;
            i++;
        }
    }

    private static Cloth ClothAt(int index, Span span, float s, float width, float drop, Color colour, float phase)
    {
        var along = span.Tangent(s);
        var up = (Vector3.Up - along * along.Dot(Vector3.Up)).Normalized();
        return new Cloth(index, s, span.At(s), new Basis(along, up, along.Cross(up)), width, drop, colour, phase);
    }

    /// <summary>Fairy lights along a fence's top: from post top to post top, a little sagging between them.</summary>
    private void FenceLights(string roomId, HamletThing fence, HamletSurvey survey, int seed)
    {
        var posts = CharmLook.Tuning.FencePosts.Select(x => fence.At(new Vector3(x, 1f, 0f)) + Vector3.Up * CharmLook.Tuning.BulbHangM).ToArray();
        var n = 0;
        for (var p = 0; p + 1 < posts.Length; p++)
        {
            var bay = new Span(fence.Id, fence.Id, posts[p], posts[p + 1], CharmLook.Tuning.FenceSagM, StringLoad.Bunting, 0f);
            var count = Math.Max(1, (int)Math.Round(bay.LengthM / CharmLook.Tuning.FenceBulbSpacingM));
            for (var k = 0; k < count; k++)
            {
                var at = bay.At((k + 0.5f) / count);
                if (survey.Boxes.Any(b => b.Id != fence.Id && b.Holds(at))) continue;
                Bulbs.Add(new Bulb(at, Hash(roomId, seed, fence.Id, 800 + n), BulbTint(roomId, seed, fence.Id + n)));
                n++;
            }
        }
    }

    private static Color BulbTint(string roomId, int seed, string key)
    {
        var h = Hash(roomId, seed, key, 900);
        return (h < CharmLook.Tuning.BulbColouredShare ? CharmLook.Tuning.BulbColours[(int)(h / CharmLook.Tuning.BulbColouredShare * CharmLook.Tuning.BulbColours.Length) % CharmLook.Tuning.BulbColours.Length]
            : CharmLook.Tuning.BulbWarm).SrgbToLinear();
    }

    /// <summary>A number in [0, 1) from the room's id, the seed, a key and a salt (FNV-1a and a mix): the charm's only source of variety.</summary>
    public static float Hash(string roomId, int seed, string key, int salt)
    {
        var hash = 2166136261u ^ (uint)seed * 16777619u ^ (uint)salt * 2246822519u;
        foreach (var c in roomId) hash = (hash ^ c) * 16777619u;
        hash = (hash ^ '/') * 16777619u;
        foreach (var c in key) hash = (hash ^ c) * 16777619u;
        hash ^= hash >> 15;
        hash *= 0x2c1b3c6du;
        hash ^= hash >> 12;
        hash *= 0x297a2d39u;
        hash ^= hash >> 15;
        return (hash & 0xFFFFFF) / (float)0x1000000;
    }
}
