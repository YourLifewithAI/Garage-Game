using Godot;

namespace GarageGame.Native.Characters.Contact;

/// <summary>
/// Small, allocation-free helpers for the contact and look layers. Godot's own quaternion helpers check normalisation in
/// debug builds and throw on drift, so these build and blend rotations themselves and always return unit quaternions.
/// </summary>
internal static class ContactMath
{
    /// <summary>The shortest rotation taking unit <paramref name="from"/> to unit <paramref name="to"/>.</summary>
    public static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        var d = Mathf.Clamp(from.Dot(to), -1, 1);
        if (d > 0.9999995f) return Quaternion.Identity;
        if (d < -0.9999995f)
        {
            var axis = Mathf.Abs(from.X) < 0.9f ? from.Cross(Vector3.Right) : from.Cross(Vector3.Up);
            axis = axis.Normalized();
            return new Quaternion(axis.X, axis.Y, axis.Z, 0);
        }
        var c = from.Cross(to);
        var s = Mathf.Sqrt((1 + d) * 2);
        return Unit(new Quaternion(c.X / s, c.Y / s, c.Z / s, s * 0.5f));
    }

    /// <summary>A turn of <paramref name="angle"/> radians about <paramref name="axis"/> (any length; none when it is zero).</summary>
    public static Quaternion AxisAngle(Vector3 axis, float angle)
    {
        var length = axis.Length();
        if (length < 1e-8f || angle == 0 || !float.IsFinite(angle)) return Quaternion.Identity;
        axis /= length;
        var half = angle * 0.5f;
        var s = Mathf.Sin(half);
        return new Quaternion(axis.X * s, axis.Y * s, axis.Z * s, Mathf.Cos(half));
    }

    /// <summary>The rotation's angle, 0 to π.</summary>
    public static float Angle(Quaternion q) => 2 * Mathf.Acos(Mathf.Clamp(Mathf.Abs(q.W), 0, 1));

    /// <summary>The rotation, turned no farther than <paramref name="maxAngle"/>.</summary>
    public static Quaternion Limit(Quaternion q, float maxAngle)
    {
        if (q.W < 0) q = -q;
        var angle = 2 * Mathf.Acos(Mathf.Clamp(q.W, 0, 1));
        return angle <= maxAngle ? q : AxisAngle(new Vector3(q.X, q.Y, q.Z), maxAngle);
    }

    /// <summary>The fraction <paramref name="t"/> of the rotation (0 none, 1 all of it), about the same axis.</summary>
    public static Quaternion Part(Quaternion q, float t)
    {
        if (q.W < 0) q = -q;
        var angle = 2 * Mathf.Acos(Mathf.Clamp(q.W, 0, 1));
        return angle < 1e-6f ? Quaternion.Identity : AxisAngle(new Vector3(q.X, q.Y, q.Z), angle * Mathf.Clamp(t, 0, 1));
    }

    /// <summary>Normalised blend from <paramref name="a"/> to <paramref name="b"/> (the shorter way round).</summary>
    public static Quaternion Blend(Quaternion a, Quaternion b, float t)
    {
        t = Mathf.Clamp(t, 0, 1);
        if (t <= 0) return a;
        if (a.Dot(b) < 0) b = -b;
        return Unit(new Quaternion(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t));
    }

    public static Quaternion Unit(Quaternion q)
    {
        var length = Mathf.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        return length > 1e-8f && float.IsFinite(length) ? new Quaternion(q.X / length, q.Y / length, q.Z / length, q.W / length) : Quaternion.Identity;
    }

    /// <summary>Exponential approach of <paramref name="current"/> to <paramref name="target"/> with time constant <paramref name="tau"/>.</summary>
    public static float Approach(float current, float target, float dt, float tau) =>
        tau <= 0 ? target : current + (target - current) * (1 - Mathf.Exp(-dt / tau));

    /// <summary>The weight of one step of an exponential approach (time constant <paramref name="tau"/>).</summary>
    public static float Rate(float dt, float tau) => tau <= 0 ? 1 : 1 - Mathf.Exp(-dt / tau);

    /// <summary>Unit direction, or <paramref name="fallback"/> when the vector is (near) zero or not finite.</summary>
    public static Vector3 Direction(Vector3 v, Vector3 fallback)
    {
        var length = v.Length();
        return length > 1e-6f && float.IsFinite(length) ? v / length : fallback;
    }

    public static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    public static float SmoothStep(float x)
    {
        x = Mathf.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    /// <summary>The direction from a joint to the bulk of its own mesh (its bounds' centre), or <paramref name="fallback"/>.</summary>
    public static Vector3 Reach(RigBone bone, Vector3 fallback) =>
        bone.HasMesh ? Direction(bone.MeshBounds.GetCenter(), fallback) : fallback;
}
