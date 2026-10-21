using System;
using System.Collections.Generic;
using Godot;

namespace GarageGame.Native.Characters.Gaits;

/// <summary>
/// The bones a gait moves, found once from the rig's roles and sides (never a character's name), and the footprint the
/// body leans and rolls about. A bone a spring chain owns is marked so the gaits leave it to its spring; a rigid bone is
/// never chosen (rig.json may lock a part a gait would move). Index 0 of each pair is the character's right (+X), 1 its left.
/// </summary>
public sealed class GaitRig
{
    public const int Right = 0, Left = 1;

    public CharacterBody Body { get; }
    public MotionProfile Profile => Body.Profile;
    public int BodyBone { get; }
    /// <summary>The head, or -1 when the body is the head (or the head is rigid).</summary>
    public int HeadBone { get; }
    public int[] Leg { get; } = { -1, -1 };
    /// <summary>The foot (or shoe) under each leg; for a legless body, the foot or shoe on each side.</summary>
    public int[] Foot { get; } = { -1, -1 };
    public int[] Arm { get; } = { -1, -1 };
    /// <summary>The hand or paw at the end of each arm.</summary>
    public int[] Hand { get; } = { -1, -1 };
    /// <summary>Whether a spring chain owns the bone (the gaits leave it to the spring).</summary>
    public bool[] Chained { get; }
    /// <summary>The body part's joint at rest, in the character's space: a float leans about it.</summary>
    public Vector3 BodyCentre { get; }
    /// <summary>The ground footprint at rest: the extremes of every mesh within 3 mm of the ground (metres, character space).</summary>
    public float FootprintRight { get; }
    public float FootprintLeft { get; }
    public float FootprintFront { get; }
    public float FootprintBack { get; }
    /// <summary>The leg's length from hip joint to sole, for the stride's planted foot (0 without legs).</summary>
    public float LegLengthM { get; }
    /// <summary>Each foot's sole below its joint (positive metres), for keeping it flat.</summary>
    public float[] SoleDepth { get; } = { 0, 0 };

    public GaitRig(CharacterBody body)
    {
        Body = body ?? throw new ArgumentNullException(nameof(body));
        var bones = body.Bones;
        Chained = new bool[bones.Count];
        foreach (var chain in body.Secondary.Chains)
            foreach (var bone in chain.Bones) Chained[bone] = true;

        BodyBone = Free(body.BodyBone) ? body.BodyBone : -1;
        HeadBone = body.HeadBone != body.BodyBone && Free(body.HeadBone) ? body.HeadBone : -1;
        BodyCentre = bones[body.BodyBone].RestModel;

        Pair(PartRole.Leg, Leg);
        for (var s = 0; s < 2; s++)
        {
            if (Leg[s] < 0) continue;
            foreach (var child in bones[Leg[s]].Children)
                if (PartRoles.IsFoot(bones[child].Role) && Free(child)) { Foot[s] = child; break; }
        }
        if (Leg[Right] < 0 && Leg[Left] < 0)
        {
            Pair(PartRole.Foot, Foot);
            if (Foot[Right] < 0 && Foot[Left] < 0) Pair(PartRole.Shoe, Foot);
        }
        Pair(PartRole.Arm, Arm);
        for (var s = 0; s < 2; s++)
        {
            if (Arm[s] < 0) continue;
            foreach (var child in bones[Arm[s]].Children)
                if (PartRoles.IsHand(bones[child].Role) && Free(child)) { Hand[s] = child; break; }
        }

        float legs = 0;
        var legCount = 0;
        for (var s = 0; s < 2; s++)
        {
            if (Foot[s] >= 0 && bones[Foot[s]].HasMesh) SoleDepth[s] = Mathf.Max(0, -bones[Foot[s]].MeshBounds.Position.Y);
            if (Leg[s] < 0) continue;
            var sole = Foot[s] >= 0 ? bones[Foot[s]].RestModel.Y - SoleDepth[s] : 0;
            legs += bones[Leg[s]].RestModel.Y - sole;
            legCount++;
        }
        LegLengthM = legCount > 0 ? legs / legCount : 0;

        // The footprint: what touches the ground at rest. Leaning or rolling about its edge never pushes a part through the floor.
        float right = float.MinValue, left = float.MaxValue, front = float.MaxValue, back = float.MinValue;
        var lowest = float.MaxValue;
        foreach (var bone in bones)
            if (bone.HasMesh && bone.Kind == PartKind.Solid) lowest = Mathf.Min(lowest, bone.RestModel.Y + bone.MeshBounds.Position.Y);
        foreach (var bone in bones)
        {
            if (!bone.HasMesh || bone.Kind != PartKind.Solid || bone.RestModel.Y + bone.MeshBounds.Position.Y > lowest + 0.003f) continue;
            var low = bone.RestModel + bone.MeshBounds.Position;
            var high = bone.RestModel + bone.MeshBounds.End;
            right = Mathf.Max(right, high.X);
            left = Mathf.Min(left, low.X);
            front = Mathf.Min(front, low.Z);
            back = Mathf.Max(back, high.Z);
        }
        if (right < left) { right = 0.01f; left = -0.01f; front = -0.01f; back = 0.01f; }
        FootprintRight = right;
        FootprintLeft = left;
        FootprintFront = front;
        FootprintBack = back;
    }

    /// <summary>A bone a gait may write: it exists and is not rigid.</summary>
    public bool Free(int bone) => bone >= 0 && bone < Body.BoneCount && !Body.Bones[bone].IsRigid;

    /// <summary>A bone a gait may swing itself: free, and no spring chain owns it.</summary>
    public bool Swingable(int bone) => Free(bone) && !Chained[bone];

    /// <summary>The first free bone of a role on each side; a middle one fills an empty side (right first).</summary>
    private void Pair(PartRole role, int[] into)
    {
        var bones = Body.Bones;
        for (var i = 1; i < bones.Count; i++)
        {
            if (bones[i].Role != role || !Free(i)) continue;
            var side = bones[i].Side;
            if (side == BodySide.Right && into[Right] < 0) into[Right] = i;
            else if (side == BodySide.Left && into[Left] < 0) into[Left] = i;
        }
        for (var i = 1; i < bones.Count; i++)
        {
            if (bones[i].Role != role || !Free(i) || bones[i].Side != BodySide.Center) continue;
            if (into[Right] < 0) into[Right] = i;
            else if (into[Left] < 0 && into[Right] != i) into[Left] = i;
        }
    }

    /// <summary>The free bones of a role, in part order (built once, at install).</summary>
    public int[] All(PartRole role)
    {
        var found = new List<int>();
        for (var i = 1; i < Body.BoneCount; i++)
            if (Body.Bones[i].Role == role && Free(i)) found.Add(i);
        return found.ToArray();
    }
}
