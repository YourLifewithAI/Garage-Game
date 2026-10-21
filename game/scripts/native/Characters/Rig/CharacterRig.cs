using System;
using System.Collections.Generic;
using Godot;

namespace GarageGame.Native.Characters;

/// <summary>
/// Builds a <see cref="CharacterBody"/> from a validated character: the skeleton from character.json's part tree and
/// pivots, the roles and sides from the part names, the chains, look-ats and leg IK from the roles and hints, and the
/// motion profile from the geometry, each adjusted by rig.json. No code here knows any character's name.
/// </summary>
public static class CharacterRig
{
    /// <summary>Reads <paramref name="folder"/> (its last path segment is the id) and builds it; null when the folder is refused.</summary>
    public static CharacterBody? Build(string folder) => TryBuild(folder, out var body, out _) ? body : null;

    public static bool TryBuild(string folder, out CharacterBody? body, out string? reason)
    {
        folder = folder.Replace('\\', '/').TrimEnd('/');
        var entry = CharacterLibrary.LoadFolder(folder, folder[(folder.LastIndexOf('/') + 1)..]);
        body = entry.Definition != null ? Build(entry.Definition) : null;
        reason = entry.RefusalReason;
        return body != null;
    }

    /// <summary>A new body. Every call makes new nodes; the meshes are shared with the definition.</summary>
    public static CharacterBody Build(CharacterDefinition definition)
    {
        var sidecar = definition.Sidecar;
        var notes = new List<string>();
        var bones = MakeBones(definition);

        // The modifiers (springs, look-ats, leg IK) step on the physics tick: the body's ticks run the pose stack, which tunes the
        // springs' stiffness and drag to the tick's delta (and contact the wind's force). The engine's default steps them on the
        // render frame, which made them stiffer, more damped and windier on a faster display. Physics interpolation is off.
        var skeleton = new Skeleton3D { Name = "Skeleton", ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Physics };
        foreach (var bone in bones)
        {
            var index = skeleton.AddBone(bone.Name);
            if (index != bone.Index) throw new InvalidOperationException($"bone {bone.Name} landed at {index}, not {bone.Index}");
            if (bone.Parent >= 0) skeleton.SetBoneParent(index, bone.Parent);
            skeleton.SetBoneRest(index, new Transform3D(Basis.Identity, bone.RestLocal));
        }
        skeleton.ResetBonePoses();

        var attachments = new BoneAttachment3D[bones.Length];
        var meshes = new MeshInstance3D?[bones.Length];
        var low = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var high = -low;
        foreach (var bone in bones)
        {
            var attachment = new BoneAttachment3D { Name = bone.Name, BoneName = bone.Name, Transform = new Transform3D(Basis.Identity, bone.RestModel) };
            skeleton.AddChild(attachment);
            attachments[bone.Index] = attachment;
            if (bone.Part < 0) continue;
            var part = definition.Parts[bone.Part];
            if (part.Mesh == null) continue;
            var mesh = new MeshInstance3D { Name = "Mesh", Mesh = part.Mesh };
            attachment.AddChild(mesh);
            meshes[bone.Index] = mesh;
            low = low.Min(bone.RestModel + bone.MeshBounds.Position);
            high = high.Max(bone.RestModel + bone.MeshBounds.End);
        }

        var bodyBone = First(bones, PartRole.Body, rigidAllowed: true);
        if (bodyBone < 0) bodyBone = bones.Length > 1 ? 1 : 0;
        var headBone = First(bones, PartRole.Head, rigidAllowed: false);
        var headIsBody = headBone < 0;
        if (headIsBody) headBone = bodyBone;

        var profile = MotionProfile.Measure(definition.Motion, bones, sidecar.Profile);

        var characterBody = new CharacterBody { Name = "CharacterBody" };
        characterBody.AddChild(skeleton);

        var (legSolver, legs) = BuildLegs(characterBody, skeleton, bones, notes);
        var look = BuildLook(characterBody, skeleton, bones, headBone, headIsBody, notes);
        var secondary = SecondaryMotion.Build(skeleton, bones, profile, sidecar, bodyBone, notes);

        var parents = new int[bones.Length];
        var rests = new Vector3[bones.Length];
        var rigid = new bool[bones.Length];
        foreach (var bone in bones)
        {
            parents[bone.Index] = bone.Parent;
            rests[bone.Index] = bone.RestLocal;
            rigid[bone.Index] = bone.IsRigid;
        }
        var pose = new PoseBuffer(parents, rests, rigid);
        characterBody.Initialize(definition, skeleton, bones, attachments, meshes, bodyBone, headBone, profile, pose, secondary, look,
            legSolver, legs, notes.ToArray(), low.X <= high.X ? new Aabb(low, high - low) : new Aabb());
        return characterBody;
    }

    private static RigBone[] MakeBones(CharacterDefinition definition)
    {
        var parts = definition.Parts;
        var sidecar = definition.Sidecar;
        var bones = new RigBone[parts.Count + 1];
        bones[0] = new RigBone
        {
            Index = 0, Name = "character_root", Parent = -1, Part = -1, Role = PartRole.Decor, Side = BodySide.Center, Kind = PartKind.Solid,
            Effect = EffectMotion.None, IsRigid = false, RestLocal = definition.RootPivotM, RestModel = definition.RootPivotM,
            MeshBounds = new Aabb(), HasMesh = false, Depth = 0,
        };
        var children = new List<int>[bones.Length];
        for (var i = 0; i < children.Length; i++) children[i] = new List<int>();
        foreach (var part in parts)
        {
            var index = part.Index + 1;
            var parent = part.Parent + 1;
            var role = sidecar.Roles.TryGetValue(part.Name, out var renamed) ? renamed : PartRoles.Infer(part.Name, part.Kind, part.Effect);
            bones[index] = new RigBone
            {
                Index = index, Name = part.Name, Parent = parent, Part = part.Index, Role = role,
                Side = PartRoles.SideFromName(part.Name) ?? bones[parent].Side,
                Kind = part.Kind, Effect = part.Effect, Bubble = part.Bubble,
                IsRigid = role == PartRole.Decor || sidecar.IsRigid(part.Name),
                RestLocal = part.LocalPivotM, RestModel = part.PivotM, MeshBounds = part.Mesh != null ? part.MeshBounds : new Aabb(),
                HasMesh = part.Mesh != null, Depth = bones[parent].Depth + 1,
            };
            children[parent].Add(index);
        }
        for (var i = 0; i < bones.Length; i++) bones[i].Children = children[i].ToArray();
        return bones;
    }

    private static int First(RigBone[] bones, PartRole role, bool rigidAllowed)
    {
        for (var i = 1; i < bones.Length; i++)
            if (bones[i].Role == role && (rigidAllowed || !bones[i].IsRigid)) return i;
        return -1;
    }

    private static LookRig BuildLook(CharacterBody body, Skeleton3D skeleton, RigBone[] bones, int headBone, bool headIsBody, List<string> notes)
    {
        var eyes = new List<int>();
        int right = -1, left = -1;
        for (var i = 1; i < bones.Length; i++)
        {
            if (bones[i].Role != PartRole.Eye || bones[i].IsRigid) continue;
            eyes.Add(i);
            if (bones[i].Side == BodySide.Right && right < 0) right = i;
            if (bones[i].Side == BodySide.Left && left < 0) left = i;
        }
        var chosen = new List<int>();
        if (right >= 0) chosen.Add(right);
        if (left >= 0) chosen.Add(left);
        foreach (var eye in eyes)
            if (chosen.Count < 2 && !chosen.Contains(eye)) chosen.Add(eye);
        chosen.Sort();
        foreach (var eye in eyes)
            if (!chosen.Contains(eye)) notes.Add($"no look-at for {bones[eye].Name}: the {LookRig.MaxLookAts}-look-at budget is spent");

        var focus = Vector3.Zero;
        if (chosen.Count > 0)
        {
            foreach (var eye in chosen) focus += bones[eye].RestModel;
            focus /= chosen.Count;
        }
        else
        {
            focus = bones[headBone].RestModel;
        }
        var target = new Node3D { Name = "LookTarget", Position = focus + new Vector3(0, 0, -1) };
        body.AddChild(target);
        var look = new LookRig { Target = target, HeadIsBody = headIsBody };
        const string path = "../../LookTarget";
        if (headBone > 0 && !bones[headBone].IsRigid)
        {
            look.Head = headIsBody
                ? LookRig.Make("LookHead", bones[headBone], LookRig.BodyYawLimit, LookRig.BodyPitchLimit, path)
                : LookRig.Make("LookHead", bones[headBone], LookRig.HeadYawLimit, LookRig.HeadPitchLimit, path);
            skeleton.AddChild(look.Head.Modifier);
        }
        foreach (var eye in chosen)
        {
            var lookAt = LookRig.Make("LookEye_" + bones[eye].Name, bones[eye], LookRig.EyeYawLimit, LookRig.EyePitchLimit, path);
            skeleton.AddChild(lookAt.Modifier);
            look.AddEye(lookAt);
        }
        return look;
    }

    private static (TwoBoneIK3D? Solver, LegIk[] Legs) BuildLegs(CharacterBody body, Skeleton3D skeleton, RigBone[] bones, List<string> notes)
    {
        var pairs = new List<(int Leg, int Foot)>();
        for (var i = 1; i < bones.Length; i++)
        {
            if (bones[i].Role != PartRole.Leg || bones[i].IsRigid) continue;
            var foot = -1;
            foreach (var child in bones[i].Children)
                if (PartRoles.IsFoot(bones[child].Role) && !bones[child].IsRigid) { foot = child; break; }
            if (foot < 0) continue;
            if (pairs.Count >= 2) { notes.Add($"no IK for {bones[i].Name}: the 2-chain IK budget is spent"); continue; }
            pairs.Add((i, foot));
        }
        if (pairs.Count == 0) return (null, Array.Empty<LegIk>());
        var targets = new Node3D { Name = "IkTargets" };
        body.AddChild(targets);
        var solver = new TwoBoneIK3D { Name = "LegIK", Active = false };
        solver.SettingCount = pairs.Count;
        var legs = new LegIk[pairs.Count];
        for (var i = 0; i < pairs.Count; i++)
        {
            var hip = bones[pairs[i].Leg];
            var foot = bones[pairs[i].Foot];
            var sole = new Vector3(0, -Mathf.Max(foot.HasMesh ? -foot.MeshBounds.Position.Y : 0, 0.002f), 0);
            var target = new Node3D { Name = hip.Name + "_target", Position = foot.RestModel + sole };
            var pole = new Node3D { Name = hip.Name + "_pole", Position = 0.5f * (hip.RestModel + foot.RestModel) + new Vector3(0, 0, -0.05f) };
            targets.AddChild(target);
            targets.AddChild(pole);
            solver.SetRootBoneName(i, hip.Name);
            solver.SetMiddleBoneName(i, foot.Name);
            solver.SetUseVirtualEnd(i, true);
            solver.SetExtendEndBone(i, true);
            solver.SetEndBoneDirection(i, SkeletonModifier3D.BoneDirection.MinusY);
            solver.SetEndBoneLength(i, -sole.Y);
            solver.SetTargetNode(i, new NodePath("../../IkTargets/" + target.Name));
            solver.SetPoleNode(i, new NodePath("../../IkTargets/" + pole.Name));
            legs[i] = new LegIk
            {
                Solver = solver, Setting = i, HipBone = hip.Index, FootBone = foot.Index, Side = hip.Side, Target = target, Pole = pole,
                SoleOffset = sole, RestSole = foot.RestModel + sole,
            };
        }
        skeleton.AddChild(solver);
        return (solver, legs);
    }
}
