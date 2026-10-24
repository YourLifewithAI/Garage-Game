using System;
using System.Collections.Generic;
using Godot;

using GarageGame.Native.Characters;

namespace GarageGame.Tests.Characters;

/// <summary>A main part's physics shape: a capsule or a sphere sized from its mesh bounds, placed relative to its bone.</summary>
public sealed record TumblePartShape(Shape3D Shape, Transform3D ShapeTransform, Transform3D BodyOffset, Transform3D JointOffset, float Volume);

/// <summary>
/// The spike's physical bones (the plan's first design, not shipped: see CharacterTumbleTest.Spike): the main parts a tumble would move
/// (docs/runs/RUN-2-CHARACTERS.md, "How it moves"): the body, the head, the arms, the legs and the
/// big props, at most <see cref="MaxBones"/> per character, chosen by role (never by name), parents first. A side with no arm
/// uses its hand or paw, a side with no leg its foot or shoe. Each gets a capsule or a sphere sized from its part's mesh bounds.
/// </summary>
public static class TumbleSpikeBones
{
    public const int MaxBones = 10;
    /// <summary>Plush, a little lighter than water (kg/m³); only the ratios matter.</summary>
    public const float Density = TumbleHull.Density;
    /// <summary>A prop this big (the farthest corner of its mesh from its joint) is one of the main parts.</summary>
    public const float BigPropReachM = 0.015f;

    /// <summary>The main parts' bones, parents before children, at most <see cref="MaxBones"/>.</summary>
    public static int[] Select(CharacterBody body)
    {
        var chosen = new List<int>(MaxBones);
        void Take(int bone)
        {
            if (bone <= 0 || chosen.Count >= MaxBones || chosen.Contains(bone) || body.Bones[bone].IsRigid) return;
            chosen.Add(bone);
        }
        Take(body.BodyBone);
        Take(body.HeadBone);
        foreach (var side in new[] { BodySide.Right, BodySide.Left })
        {
            var arm = body.FindBone(PartRole.Arm, side);
            if (arm < 0) arm = body.FindBone(PartRole.Hand, side);
            if (arm < 0) arm = body.FindBone(PartRole.Paw, side);
            Take(arm);
        }
        foreach (var side in new[] { BodySide.Right, BodySide.Left })
        {
            var leg = body.FindBone(PartRole.Leg, side);
            if (leg < 0) leg = body.FindBone(PartRole.Foot, side);
            if (leg < 0) leg = body.FindBone(PartRole.Shoe, side);
            Take(leg);
        }
        foreach (var role in new[] { PartRole.Wand, PartRole.Torch })
            for (var nth = 0; ; nth++)
            {
                var prop = body.FindBone(role, null, nth);
                if (prop < 0) break;
                if (body.Bones[prop].ReachM >= BigPropReachM) Take(prop);
            }
        chosen.Sort();
        return chosen.ToArray();
    }

    /// <summary>
    /// The part's shape: a capsule along its mesh's longest side (radius half the mean of the other two), or a sphere when the
    /// part is round enough; the body sits at the mesh's centre with the bone's axes, and the joint at the bone's joint with
    /// its twist axis (X) from the joint toward the part.
    /// </summary>
    public static TumblePartShape ShapeFor(RigBone bone)
    {
        if (!bone.HasMesh)
        {
            var dot = new SphereShape3D { Radius = 0.004f };
            return new TumblePartShape(dot, Transform3D.Identity, Transform3D.Identity, Transform3D.Identity, Sphere(0.004f));
        }
        var bounds = bone.MeshBounds;
        var size = bounds.Size;
        var centre = bounds.GetCenter();
        var axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
        var longest = size[axis];
        var across = 0.5f * (size.X + size.Y + size.Z - longest);
        var radius = Mathf.Max(0.5f * across, 0.002f);
        Shape3D shape;
        Transform3D placed;
        float volume;
        if (longest <= 1.3f * 2 * radius)
        {
            var round = Mathf.Max((size.X + size.Y + size.Z) / 6, 0.002f);
            shape = new SphereShape3D { Radius = round };
            placed = Transform3D.Identity;
            volume = Sphere(round);
        }
        else
        {
            shape = new CapsuleShape3D { Radius = radius, Height = longest };
            // CapsuleShape3D lies along its Y: turn it onto the longest side.
            var basis = axis switch
            {
                0 => new Basis(Vector3.Back, -Mathf.Pi / 2),
                2 => new Basis(Vector3.Right, Mathf.Pi / 2),
                _ => Basis.Identity,
            };
            placed = new Transform3D(basis, Vector3.Zero);
            volume = Mathf.Pi * radius * radius * (longest - 2 * radius) + Sphere(radius);
        }
        var bodyOffset = new Transform3D(Basis.Identity, centre);
        var twist = centre.LengthSquared() > 1e-8f ? centre.Normalized() : Vector3.Down;
        var jointOffset = new Transform3D(Frame(twist), -centre);
        return new TumblePartShape(shape, placed, bodyOffset, jointOffset, volume);
    }

    private static float Sphere(float radius) => 4f / 3f * Mathf.Pi * radius * radius * radius;

    /// <summary>An orthonormal frame whose X is <paramref name="x"/>.</summary>
    private static Basis Frame(Vector3 x)
    {
        var helper = Mathf.Abs(x.Y) < 0.9f ? Vector3.Up : Vector3.Right;
        var z = x.Cross(helper).Normalized();
        var y = z.Cross(x).Normalized();
        return new Basis(x, y, z);
    }
}
