using Godot;
using System;
using System.Collections.Generic;
using GarageGame.Native;
using GarageGame.Native.Room;
using GarageGame.Native.Characters;

namespace GarageGame.Tests.Characters;

/// <summary>The three grounds of the tumble suite: flat, a 30° slope and 2 cm steps.</summary>
public enum TumbleGround { Flat, Slope, Step }

/// <summary>
/// Ground fixtures for the tumble suite (K7), each a lane of static world bodies with an exact description of its surface, so a
/// test can say how far any point lies below it. The slope rises along +X at 30°; the step lane has 2 cm strips every
/// <see cref="StepPitchM"/>, each rising edge at x = k · pitch.
/// </summary>
public sealed class TumbleTerrain
{
    public const float SlopeDeg = 30f;
    public const float StepHeightM = 0.02f;
    public const float StepPitchM = 0.30f;
    public const float StepWidthM = 0.15f;

    public TumbleGround Kind { get; }
    public Vector3 Origin { get; }
    public Node3D Root { get; }
    private readonly Vector3 _slopeNormal;

    private TumbleTerrain(TumbleGround kind, Vector3 origin, Node3D root)
    {
        Kind = kind;
        Origin = origin;
        Root = root;
        var angle = Mathf.DegToRad(SlopeDeg);
        _slopeNormal = new Vector3(-Mathf.Sin(angle), Mathf.Cos(angle), 0);
    }

    /// <summary>Builds a ground lane of <paramref name="sizeM"/> square around <paramref name="origin"/> (the surface passes through it).</summary>
    public static TumbleTerrain Build(Node parent, TumbleGround kind, Vector3 origin, float sizeM)
    {
        var root = new Node3D { Name = $"Ground{kind}", Position = origin };
        parent.AddChild(root);
        var terrain = new TumbleTerrain(kind, origin, root);
        switch (kind)
        {
            case TumbleGround.Flat:
                AddBox(root, new Vector3(sizeM, 0.2f, sizeM), new Transform3D(Basis.Identity, new Vector3(0, -0.1f, 0)));
                break;
            case TumbleGround.Slope:
            {
                var basis = new Basis(Vector3.Back, Mathf.DegToRad(SlopeDeg));
                // A long box tilted about +Z: its top face is the plane through the origin rising along +X.
                AddBox(root, new Vector3(sizeM * 1.3f, 0.2f, sizeM), new Transform3D(basis, basis * new Vector3(0, -0.1f, 0)));
                break;
            }
            case TumbleGround.Step:
                AddBox(root, new Vector3(sizeM + 1, 0.2f, sizeM), new Transform3D(Basis.Identity, new Vector3(0, -0.1f, 0)));
                for (var k = -(int)(sizeM / StepPitchM) / 2 - 1; k <= (int)(sizeM / StepPitchM) / 2 + 1; k++)
                    AddBox(root, new Vector3(StepWidthM, StepHeightM, sizeM),
                        new Transform3D(Basis.Identity, new Vector3(k * StepPitchM + StepWidthM / 2, StepHeightM / 2, 0)));
                break;
        }
        return terrain;
    }

    private static void AddBox(Node3D root, Vector3 size, Transform3D transform)
    {
        var body = new StaticBody3D { CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0, Transform = transform };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        root.AddChild(body);
    }

    /// <summary>The surface height at a world (x, z) (the step lane: the top of whichever surface is under it).</summary>
    public float SurfaceY(float x, float z)
    {
        var local = x - Origin.X;
        return Kind switch
        {
            TumbleGround.Slope => Origin.Y + local * Mathf.Tan(Mathf.DegToRad(SlopeDeg)),
            TumbleGround.Step => Origin.Y + (OnStrip(local) ? StepHeightM : 0),
            _ => Origin.Y,
        };
    }

    /// <summary>The surface's normal under a world point.</summary>
    public Vector3 Normal => Kind == TumbleGround.Slope ? _slopeNormal : Vector3.Up;

    private static bool OnStrip(float local)
    {
        var within = local - Mathf.Floor(local / StepPitchM) * StepPitchM;
        return within >= 0 && within <= StepWidthM;
    }

    /// <summary>How far a world point lies inside the ground (0 above it).</summary>
    public float Depth(Vector3 point)
    {
        var p = point - Origin;
        switch (Kind)
        {
            case TumbleGround.Slope:
                return Mathf.Max(0, -p.Dot(_slopeNormal));
            case TumbleGround.Step:
            {
                var depth = Mathf.Max(0, -p.Y);
                var k = Mathf.Floor(p.X / StepPitchM);
                var x0 = k * StepPitchM;
                var x1 = x0 + StepWidthM;
                if (p.X >= x0 && p.X <= x1 && p.Y <= StepHeightM)
                    depth = Mathf.Max(depth, Mathf.Min(StepHeightM - p.Y, Mathf.Min(p.X - x0, x1 - p.X)));
                return depth;
            }
            default:
                return Mathf.Max(0, -p.Y);
        }
    }
}

/// <summary>
/// Reads a character body in the tree as the tumble's acceptance measures it: every part's mesh (its convex hull's points) where
/// the skeleton last put it, and each bone's joint, in world space.
/// </summary>
public sealed class TumbleProbe
{
    private readonly CharacterBody _body;
    private readonly Vector3[][] _points;
    private readonly Transform3D[] _meshLocal;
    private readonly Vector3[] _lastJoint;
    private bool _hasLast;

    private static readonly Dictionary<Mesh, Vector3[]> HullCache = new();

    public TumbleProbe(CharacterBody body)
    {
        _body = body;
        _points = new Vector3[body.BoneCount][];
        _meshLocal = new Transform3D[body.BoneCount];
        _lastJoint = new Vector3[body.BoneCount];
        for (var i = 0; i < body.BoneCount; i++)
        {
            var mesh = body.PartMesh(i);
            _points[i] = mesh?.Mesh == null ? Array.Empty<Vector3>() : Hull(mesh.Mesh);
            _meshLocal[i] = mesh?.Transform ?? Transform3D.Identity;
        }
    }

    private static Vector3[] Hull(Mesh mesh)
    {
        if (HullCache.TryGetValue(mesh, out var points)) return points;
        points = mesh.CreateConvexShape(true, false) is ConvexPolygonShape3D shape ? shape.Points : Array.Empty<Vector3>();
        HullCache[mesh] = points;
        return points;
    }

    /// <summary>The deepest any mesh point lies inside the ground (m); only the bones <paramref name="only"/> marks, when given.</summary>
    public float Penetration(TumbleTerrain terrain, bool[]? only = null)
    {
        var deepest = 0f;
        for (var i = 0; i < _body.BoneCount; i++)
        {
            if (_points[i].Length == 0 || (only != null && !only[i])) continue;
            var transform = _body.Attachment(i).GlobalTransform * _meshLocal[i];
            foreach (var point in _points[i]) deepest = Mathf.Max(deepest, terrain.Depth(transform * point));
        }
        return deepest;
    }

    /// <summary>Whether every final bone transform is finite.</summary>
    public bool Finite()
    {
        for (var i = 0; i < _body.BoneCount; i++)
        {
            var t = _body.Attachment(i).GlobalTransform;
            if (!t.Origin.IsFinite() || !t.Basis.X.IsFinite() || !t.Basis.Y.IsFinite() || !t.Basis.Z.IsFinite()) return false;
        }
        return true;
    }

    /// <summary>The fastest bone joint since the last call (m/s, by finite difference over <paramref name="dt"/>); 0 on the first call.</summary>
    public float FastestJoint(float dt)
    {
        var fastest = 0f;
        for (var i = 0; i < _body.BoneCount; i++)
        {
            var joint = _body.Attachment(i).GlobalPosition;
            if (_hasLast) fastest = Mathf.Max(fastest, joint.DistanceTo(_lastJoint[i]) / dt);
            _lastJoint[i] = joint;
        }
        _hasLast = true;
        return fastest;
    }

    public void ResetSpeed() => _hasLast = false;
}

/// <summary>A small deterministic generator for seeded tumbles (xorshift; the same seed gives the same tumbles everywhere).</summary>
public struct TumbleRandom
{
    private uint _state;
    public TumbleRandom(uint seed) => _state = seed == 0 ? 0x9E3779B9u : seed;
    public uint Next()
    {
        _state ^= _state << 13;
        _state ^= _state >> 17;
        _state ^= _state << 5;
        return _state;
    }
    public float Range(float low, float high) => low + (high - low) * (Next() / (float)uint.MaxValue);
}
