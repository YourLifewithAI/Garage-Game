using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Godot;

namespace GarageGame.Native.Characters.Contact;

/// <summary>
/// The intrusion metric (docs/runs/RUN-2-CHARACTERS.md, "Later: wide poses meet the world", item 1): a measurement only. Each
/// sample asks the physics space how deep each part's mesh bounds (a box riding the part's final bone) reach into world
/// geometry, and adds depth × time, in millimetre-seconds, to the part's total. The depth is how far the box has travelled into
/// the world along the line out from the body's own axis (the capsule's, which is always clear): the box is swept from the axis
/// to where it is, and what lies past its first contact is inside. That is what a later tuck must undo, and it stays right for a
/// part sunk whole in a thick wall, where the engine's contact pairs push it out through the far face. A box that already
/// overlaps the world at the axis (a body wider than the capsule) falls back to the contact pairs' depth. Only the shared
/// capsule collides, so wide parts pass into walls, gaps and posts; this baseline is what the later round's tucks, braces and
/// turns must lower. It changes nothing about where a body can go. Allocates per sample (a test and tuning tool).
/// </summary>
public sealed class IntrusionMeter
{
    /// <summary>The thinnest a part's box is made (flat painted parts have almost no thickness).</summary>
    public const float MinBoxM = 0.0005f;

    private readonly CharacterBody _rig;
    private readonly int[] _parts;
    private readonly double[] _mmS;
    private readonly BoxShape3D _box = new();
    private readonly PhysicsShapeQueryParameters3D _query;

    /// <param name="exclude">Colliders not to count (the body's own capsule, the floor it walks on).</param>
    public IntrusionMeter(CharacterBody rig, uint mask, IEnumerable<Rid> exclude)
    {
        _rig = rig ?? throw new ArgumentNullException(nameof(rig));
        _parts = rig.Bones.Where(b => b.HasMesh).Select(b => b.Index).ToArray();
        _mmS = new double[_parts.Length];
        _query = new PhysicsShapeQueryParameters3D
        {
            Shape = _box, CollisionMask = mask, CollideWithAreas = false, CollideWithBodies = true, Margin = 0,
            Exclude = new Godot.Collections.Array<Rid>(exclude),
        };
    }

    /// <summary>Millimetre-seconds inside the world so far, over every part.</summary>
    public double TotalMmS => _mmS.Sum();
    public double SampledS { get; private set; }
    public int Samples { get; private set; }
    /// <summary>The deepest any part reached on any sample, metres, and which part and where (world).</summary>
    public float WorstDepthM { get; private set; }
    public string WorstPart { get; private set; } = "";
    public Vector3 WorstAt { get; private set; }

    /// <summary>Each part with its millimetre-seconds, the most first.</summary>
    public IEnumerable<(string Part, double MmS)> Parts =>
        _parts.Select((bone, i) => (_rig.Bones[bone].Name, _mmS[i])).OrderByDescending(p => p.Item2).ThenBy(p => p.Name, StringComparer.Ordinal);

    public void Reset()
    {
        Array.Clear(_mmS);
        SampledS = 0;
        Samples = 0;
        WorstDepthM = 0;
    }

    /// <summary>One sample over <paramref name="dt"/> seconds, from the bones' final transforms (as of the last skeleton update).</summary>
    public void Sample(PhysicsDirectSpaceState3D space, float dt)
    {
        var body = _rig.GlobalTransform;
        for (var i = 0; i < _parts.Length; i++)
        {
            var bone = _rig.Bones[_parts[i]];
            var transform = body * _rig.FinalBoneTransform(bone.Index);
            var centre = transform * bone.MeshBounds.GetCenter();
            var depth = Depth(space, transform, bone.MeshBounds, new Vector3(body.Origin.X, centre.Y, body.Origin.Z));
            if (depth <= 0) continue;
            _mmS[i] += depth * 1000.0 * dt;
            if (depth > WorstDepthM)
            {
                WorstDepthM = depth;
                WorstPart = bone.Name;
                WorstAt = body * _rig.FinalBoneTransform(bone.Index) * bone.MeshBounds.GetCenter();
            }
        }
        SampledS += dt;
        Samples++;
    }

    /// <summary>
    /// How deep a box (the bounds under <paramref name="transform"/>) reaches into the world: how far it travels past its first
    /// contact when swept from <paramref name="from"/> (clear of the world) to where it is; without a clear start, the deepest
    /// contact pair's separation.
    /// </summary>
    public float Depth(PhysicsDirectSpaceState3D space, Transform3D transform, Aabb bounds, Vector3? from = null)
    {
        var scale = transform.Basis.Scale;
        var size = new Vector3(Mathf.Max(bounds.Size.X * Mathf.Abs(scale.X), MinBoxM), Mathf.Max(bounds.Size.Y * Mathf.Abs(scale.Y), MinBoxM),
            Mathf.Max(bounds.Size.Z * Mathf.Abs(scale.Z), MinBoxM));
        if (!float.IsFinite(size.X + size.Y + size.Z)) return 0;
        _box.Size = size;
        var basis = transform.Basis.Orthonormalized();
        var centre = transform * bounds.GetCenter();
        _query.Motion = Vector3.Zero;
        _query.Transform = new Transform3D(basis, centre);
        var pairs = space.CollideShape(_query, 16);
        if (pairs.Count < 2) return 0;
        var deepest = 0f;
        for (var p = 0; p + 1 < pairs.Count; p += 2) deepest = Mathf.Max(deepest, pairs[p].DistanceTo(pairs[p + 1]));
        if (from is not { } start || !float.IsFinite(start.X + start.Y + start.Z)) return deepest;
        var path = centre - start;
        if (path.Length() < 1e-4f) return deepest;
        _query.Transform = new Transform3D(basis, start);
        if (space.IntersectShape(_query, 1).Count > 0)
        {
            _query.Transform = new Transform3D(basis, centre);
            return deepest;
        }
        _query.Motion = path;
        var cast = space.CastMotion(_query);
        _query.Motion = Vector3.Zero;
        _query.Transform = new Transform3D(basis, centre);
        return cast.Length == 2 && cast[1] < 1 ? (1 - cast[1]) * path.Length() : 0;
    }

    /// <summary>One line for the run's evidence: the total, the time measured and the parts that intrude most.</summary>
    public string Describe(int top = 4)
    {
        var c = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.Append(c, $"total {TotalMmS:0.0} mm·s over {SampledS:0.00} s (worst depth {WorstDepthM * 1000:0.0} mm)");
        var worst = Parts.Where(p => p.MmS > 0.05).Take(top).ToList();
        text.Append(worst.Count == 0 ? "; no part inside" : "; most: " + string.Join(", ", worst.Select(p => string.Create(c, $"{p.Part} {p.MmS:0.0}"))));
        return text.ToString();
    }
}
