using Godot;

namespace GarageGame.Native.Kernel;

/// <summary>
/// A line-of-sight ray with no garbage (S12): one RayCast3D under the host, moved to each ray and updated at once, in place of a
/// PhysicsRayQueryParameters3D and the Dictionary every DirectSpaceState.IntersectRay answers with. The sight sweep casts some
/// thousand rays a sweep on the 10x island; each of those pairs was a Godot value left to the .NET finalizer. The query is the same
/// one (the same space, mask, exclusion, faces and areas), so the answers are the same.
/// </summary>
internal sealed class SightRay
{
    private readonly RayCast3D _ray;
    private Rid _excluded;

    /// <summary>Rays cast by every sight ray since the game started (the frame probe's count).</summary>
    public static long Casts { get; private set; }

    public SightRay(Node parent, uint mask)
    {
        _ray = new RayCast3D
        {
            Name = "SightRay", Enabled = false, TopLevel = true, ExcludeParent = false, CollisionMask = mask,
            // As PhysicsRayQueryParameters3D.Create: bodies only, back faces hit, nothing from inside.
            CollideWithBodies = true, CollideWithAreas = false, HitBackFaces = true, HitFromInside = false,
        };
        parent.AddChild(_ray);
    }

    /// <summary>Whether the ray can be cast now (its node is in the tree, so it has a world).</summary>
    public bool Ready => GodotObject.IsInstanceValid(_ray) && _ray.IsInsideTree();

    /// <summary>The first world surface from one point to another, leaving out one body (an invalid RID leaves out nothing); false when nothing is hit.</summary>
    public bool Cast(Vector3 from, Vector3 to, Rid exclude, out Vector3 point)
    {
        point = default;
        // A ray of no length hits nothing (RayCast3D would cast 1 cm up instead).
        if (from == to) return false;
        Casts++;
        if (exclude != _excluded)
        {
            if (_excluded.IsValid) _ray.RemoveExceptionRid(_excluded);
            if (exclude.IsValid) _ray.AddExceptionRid(exclude);
            _excluded = exclude;
        }
        _ray.GlobalTransform = new Transform3D(Basis.Identity, from);
        _ray.TargetPosition = to - from;
        _ray.ForceRaycastUpdate();
        if (!_ray.IsColliding()) return false;
        point = _ray.GetCollisionPoint();
        return true;
    }
}
