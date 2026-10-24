using Godot;
using System;
using System.Collections.Generic;

namespace GarageGame.Native.Look.Intro;

/// <summary>Where the intro's camera is at a moment: its transform, its field of view (degrees) and its near plane (metres).</summary>
public readonly record struct IntroPose(Transform3D Transform, float FovDeg, float NearM);

/// <summary>
/// The intro's one continuous camera move, in the room's own proportions so any room and any scale gets the same shot: it starts
/// inside the room on the side away from the player's spawn, at two fifths of the room's height, looking across the boxes toward the
/// spawn's side; drifts in and up as the ceiling dissolves; rises through where the ceiling was to an overview behind and above the
/// island while the land rises and the walls slump into the coast; swings round toward the spawn as the plants grow; comes down behind
/// the player in two steps; and glides into the play camera's own pose, which it reaches exactly at the end (its transform, field of
/// view and near plane), so the hand-over has no cut.
///
/// Key poses are joined by a cubic Hermite spline in time (each key's velocity from its neighbours, Catmull-Rom style), so the camera
/// never stops between them. The last key's velocity points at the play camera at the glide's own pace, so the glide neither
/// overshoots nor stops. Every key from the overview on stays clear of the island's ground (a probe of the real ground under it),
/// and the way down is checked along its length and lifted where a hill stands in it.
/// </summary>
public sealed class IntroCameraPath
{
    public const float StartFovDeg = 62f;
    /// <summary>The way down: this far behind the player (a share of the room's longer side, within these bounds), 0.6 of it up.</summary>
    public const float ApproachShare = 0.08f, ApproachMinM = 0.6f, ApproachMaxM = 8f;
    /// <summary>The glide into the play camera starts here and ends with the intro.</summary>
    public const double GlideFromS = 13.6;
    private readonly List<(double T, Vector3 Position, Vector3 Target)> _keys = new();
    private readonly Vector3 _glideVelocity;
    /// <summary>Points checked along each step of the way down.</summary>
    private const int Samples = 32;

    private void Raise(int key, float metres) => _keys[key] = (_keys[key].T, _keys[key].Position + Vector3.Up * metres, _keys[key].Target);

    /// <summary>The keys (time, position, where the camera looks), for tests and the capture's notes.</summary>
    public IReadOnlyList<(double T, Vector3 Position, Vector3 Target)> Keys => _keys;
    /// <summary>How far behind the player the way down passes (metres).</summary>
    public float ApproachM { get; }

    /// <param name="room">The source room's bounds (its floor to its ceiling).</param>
    /// <param name="spawn">The player's spawn.</param>
    /// <param name="end">The play camera's pose when the intro starts (its back on the ground is where the way down comes from).</param>
    /// <param name="landTopY">The highest the island's land reaches: the overview stays well above it.</param>
    /// <param name="startHeight">The room's height at a point (IntroField.StartAt): the first shots stay above the boxes.</param>
    /// <param name="groundHeight">The island's ground at a point (null: none known): the later shots stay above it.</param>
    public IntroCameraPath(Aabb room, Vector3 spawn, Transform3D end, float landTopY, Func<float, float, float> startHeight, Func<float, float, float>? groundHeight = null)
    {
        var floor = room.Position.Y;
        var height = Math.Max(room.Size.Y, 0.1f);
        var centre = new Vector3(room.GetCenter().X, floor, room.GetCenter().Z);
        var longest = Math.Max(room.Size.X, room.Size.Z);
        // Toward the spawn on the ground (the start looks that way); a spawn at the middle looks along -Z.
        var toward = new Vector3(spawn.X - centre.X, 0, spawn.Z - centre.Z);
        toward = toward.Length() < 0.05f * longest ? Vector3.Forward : toward.Normalized();
        var side = new Vector3(-toward.Z, 0, toward.X);
        var half = 0.5f * (Math.Abs(toward.X) * room.Size.X + Math.Abs(toward.Z) * room.Size.Z);
        var above = Math.Max(landTopY - floor, 0f);
        Vector3 At(float back, float across, float up) => centre - toward * (back * half) + side * (across * longest) + Vector3.Up * up;
        Vector3 Inside(Vector3 point, float clearance) => new(point.X, Math.Max(point.Y, startHeight(point.X, point.Z) + clearance), point.Z);
        float Ground(Vector3 point) => groundHeight?.Invoke(point.X, point.Z) ?? floor;
        Vector3 Over(Vector3 point, float clearance) => new(point.X, Math.Max(point.Y, Ground(point) + clearance), point.Z);
        var (_, roomEnd) = IntroTimeline.Window("room");
        var (_, skyEnd) = IntroTimeline.Window("sky");
        var (_, landEnd) = IntroTimeline.Window("land");
        var (plantStart, _) = IntroTimeline.Window("plants");
        ApproachM = Math.Clamp(ApproachShare * longest, ApproachMinM, ApproachMaxM);
        var back = new Vector3(end.Basis.Z.X, 0, end.Basis.Z.Z);
        back = back.Length() < 1e-4f ? -toward : back.Normalized();
        var a = ApproachM;
        // Inside the room: two fifths of the way up, looking across the boxes toward the spawn's side, then drifting in and up.
        _keys.Add((0.0, Inside(At(0.82f, 0.10f, 0.42f * height), 0.1f * height), centre + toward * (0.35f * half) + Vector3.Up * (0.12f * height)));
        _keys.Add((roomEnd, Inside(At(0.68f, 0.07f, 0.56f * height), 0.1f * height), centre + toward * (0.30f * half) + Vector3.Up * (0.08f * height)));
        // Up through where the ceiling was.
        _keys.Add((skyEnd + 0.4, Over(At(0.62f, 0.02f, Math.Max(1.3f * height, above + 0.4f * height)), 0.2f * height), centre + toward * (0.10f * half)));
        // The overview: behind and above the whole island.
        _keys.Add((landEnd, Over(At(1.25f, -0.18f, Math.Max(1.85f * height, above + 0.9f * height)), 0.3f * height), centre));
        // Swinging round toward the spawn while the plants grow.
        _keys.Add((plantStart + 1.0, Over(At(0.55f, -0.42f, Math.Max(1.35f * height, above + 0.6f * height)), 0.3f * height), centre.Lerp(spawn, 0.55f)));
        // Down behind the player in two steps, each clear of the ground under it.
        _keys.Add((0.5 * (plantStart + 1.0 + GlideFromS), Over(spawn + back * (3f * a) + Vector3.Up * (1.6f * a), 0.5f * a), spawn));
        _keys.Add((GlideFromS, Over(spawn + back * a + Vector3.Up * (0.6f * a), 0.3f * a), spawn));
        // The way down along its length: a hill under the line between two keys lifts the later key by just what the line needs
        // there (a lift of d / f at a point f along the line lifts that point by d); the glide's line to the play camera lifts the last
        // key by d / (1 - f). Near the play camera only a hand's breadth is needed: its own arm keeps it off the ground there.
        if (groundHeight != null)
            for (var round = 0; round < 8; round++)
            {
                var moved = false;
                var n = _keys.Count;
                var lift = 0f;
                for (var s = 1; s < Samples; s++)
                {
                    var f = s / (float)Samples;
                    var point = _keys[n - 1].Position.Lerp(end.Origin, f);
                    var need = Ground(point) + 0.25f * a * (1f - f);
                    if (point.Y < need) lift = Math.Max(lift, (need - point.Y) / (1f - f));
                }
                if (lift > 1e-3f) { Raise(n - 1, lift); moved = true; }
                for (var k = n - 3; k < n - 1; k++)
                {
                    lift = 0f;
                    for (var s = 1; s <= Samples; s++)
                    {
                        var f = s / (float)Samples;
                        var point = _keys[k].Position.Lerp(_keys[k + 1].Position, f);
                        var need = Ground(point) + 0.25f * a;
                        if (point.Y < need) lift = Math.Max(lift, (need - point.Y) / f);
                    }
                    if (lift > 1e-3f) { Raise(k + 1, lift); moved = true; }
                }
                if (!moved) break;
            }
        // The glide's pace: straight at the play camera, arriving with no speed (see Pose).
        _glideVelocity = (end.Origin - _keys[^1].Position) / (float)(IntroTimeline.DurationS - GlideFromS);
    }

    /// <summary>The camera at a time, given the play camera's pose (its transform and field of view) it hands over to at the end.</summary>
    public IntroPose Pose(double seconds, Transform3D end, float endFovDeg)
    {
        var t = Math.Clamp(seconds, 0.0, IntroTimeline.DurationS);
        Vector3 position, target;
        if (t < GlideFromS)
        {
            var k = 0;
            while (k < _keys.Count - 2 && t >= _keys[k + 1].T) k++;
            position = Hermite(k, t, position: true);
            target = Hermite(k, t, position: false);
            return new IntroPose(new Transform3D(LookFrom(position, target), position), StartFovDeg, Near(position, end.Origin));
        }
        // The glide: from the last key at the spline's pace into the play camera with none left. Its start velocity is the line to
        // the play camera, so the curve runs along that line and never overshoots it (the Hermite weights add to -u^3 + u^2 + u).
        var span = IntroTimeline.DurationS - GlideFromS;
        var u = (float)((t - GlideFromS) / span);
        if (u >= 1f) return new IntroPose(end, endFovDeg, 0.005f);
        var last = _keys[^1];
        position = HermiteSegment(last.Position, end.Origin, (end.Origin - last.Position), Vector3.Zero, u);
        var eased = u * u * (3f - 2f * u);
        var basis = LookFrom(position, last.Target).Slerp(end.Basis.Orthonormalized(), eased);
        return new IntroPose(new Transform3D(basis, position), Mathf.Lerp(StartFovDeg, endFovDeg, eased), Near(position, end.Origin));
    }

    /// <summary>A near plane that suits the distance to the player: 5 mm at the play camera, no more than 20 cm far out.</summary>
    private static float Near(Vector3 position, Vector3 end) => Math.Clamp(0.004f * position.DistanceTo(end) + 0.005f, 0.005f, 0.2f);

    private static Basis LookFrom(Vector3 position, Vector3 target)
    {
        var forward = target - position;
        if (forward.LengthSquared() < 1e-8f) forward = Vector3.Forward;
        forward = forward.Normalized();
        var up = Math.Abs(forward.Dot(Vector3.Up)) > 0.999f ? Vector3.Back : Vector3.Up;
        return Basis.LookingAt(forward, up);
    }

    private Vector3 Part(int k, bool position) => position ? _keys[k].Position : _keys[k].Target;

    private Vector3 Velocity(int k, bool position)
    {
        // The last key's position moves at the glide's pace (straight at the play camera); a target stops there.
        if (k == _keys.Count - 1) return position ? _glideVelocity : Vector3.Zero;
        var before = Math.Max(k - 1, 0);
        return (Part(k + 1, position) - Part(before, position)) / (float)(_keys[k + 1].T - _keys[before].T);
    }

    private Vector3 Hermite(int k, double t, bool position)
    {
        var (t0, t1) = (_keys[k].T, _keys[k + 1].T);
        var span = (float)(t1 - t0);
        var u = (float)Math.Clamp((t - t0) / (t1 - t0), 0.0, 1.0);
        // The first key starts at rest (the shot begins still), every other key keeps the spline's speed.
        var v0 = k == 0 ? Vector3.Zero : Velocity(k, position) * span;
        var v1 = Velocity(k + 1, position) * span;
        return HermiteSegment(Part(k, position), Part(k + 1, position), v0, v1, u);
    }

    private static Vector3 HermiteSegment(Vector3 p0, Vector3 p1, Vector3 v0, Vector3 v1, float u)
    {
        var u2 = u * u;
        var u3 = u2 * u;
        return p0 * (2f * u3 - 3f * u2 + 1f) + v0 * (u3 - 2f * u2 + u) + p1 * (-2f * u3 + 3f * u2) + v1 * (u3 - u2);
    }
}
