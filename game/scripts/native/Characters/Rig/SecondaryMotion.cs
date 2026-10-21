using System;
using System.Collections.Generic;
using Godot;

namespace GarageGame.Native.Characters;

/// <summary>Why a chain exists (the inference rule that made it).</summary>
public enum ChainKind { Custom, Hat, Hair, Ear, Tail, DanglingLeg, DanglingFoot, Arm, Horn, Flame, Wand, Sparkle }

/// <summary>One spring chain: a setting of one of the body's <see cref="SpringBoneSimulator3D"/>s.</summary>
public sealed class SpringChain
{
    /// <summary>The simulator that runs this chain: <see cref="SecondaryMotion.LegSimulator"/> for dangling legs and feet, else <see cref="SecondaryMotion.Simulator"/>.</summary>
    public required SpringBoneSimulator3D Simulator { get; init; }
    /// <summary>The chain's setting index in <see cref="Simulator"/>.</summary>
    public required int Setting { get; init; }
    public required ChainKind Kind { get; init; }
    public required int RootBone { get; init; }
    public required int EndBone { get; init; }
    /// <summary>The chain's bones from root to end.</summary>
    public required int[] Bones { get; init; }
    /// <summary>The end bone's virtual tail, in the end bone's space.</summary>
    public required SkeletonModifier3D.BoneDirection TailDirection { get; init; }
    public required float TailLengthM { get; init; }
    /// <summary>Joint to joint plus the tail: the pendulum's length.</summary>
    public required float LengthM { get; init; }
    /// <summary>The root joint's own segment (to the next joint, or the tail): the stiffness is set so this joint swings at <see cref="FrequencyHz"/>.</summary>
    public required float RootSegmentM { get; init; }
    public required float PendulumHz { get; init; }
    /// <summary>The tuned frequency: the pendulum's, times the profile's and rig.json's springiness.</summary>
    public required float FrequencyHz { get; init; }
    public required float DampingRatio { get; init; }
    public required float RadiusM { get; init; }
    public required float GravityMps { get; init; }
    public required bool Collides { get; init; }
}

/// <summary>
/// The body's secondary motion: every spring chain (at most 6 of at most 6 bones) on two
/// <see cref="SpringBoneSimulator3D"/>s, the dangling legs and feet on <see cref="LegSimulator"/> (so contact can mute
/// them while the leg IK plants the feet) and the rest on <see cref="Simulator"/>, each colliding with a capsule inside
/// the body. The engine's spring is a Verlet step whose stiffness acts
/// like gravity: stiffness = (2πf)²·L·Δt makes a joint whose segment is L long swing at f (measured: 1.88 Hz against
/// 1.91 Hz designed), so <see cref="MatchTimestep"/> re-tunes stiffness and drag whenever the frame delta changes,
/// keeping every frequency frame-rate independent. Each chain's f is the pendulum of its whole length.
/// </summary>
public sealed class SecondaryMotion
{
    public const int MaxChains = 6;
    public const int MaxBonesPerChain = 6;
    /// <summary>The default damping ratio of a chain, before the profile's and rig.json's spring_damping.</summary>
    public const float BaseDampingRatio = 0.12f;
    public const float MinFrequencyHz = 0.4f, MaxFrequencyHz = 8f;

    private readonly List<SpringChain> _chains = new();
    private float _tunedDelta;
    private bool _legSpringsWanted = true;
    private bool _held;

    public static readonly SecondaryMotion None = new();

    /// <summary>Hair, hats, horns, ears, tails, arms, props and effects; null when the character has none of them.</summary>
    public SpringBoneSimulator3D? Simulator { get; private set; }
    /// <summary>Dangling legs and feet (under a hop or a float); null when there are none.</summary>
    public SpringBoneSimulator3D? LegSimulator { get; private set; }
    /// <summary>The capsule inside the body that chains collide with (one per simulator; this is the first); null when the body has no mesh.</summary>
    public SpringBoneCollisionCapsule3D? BodyCollider { get; private set; }
    /// <summary>Every chain, in priority order (hats, hair, ears and tails, dangling legs, arms, horns, flames, props, sparkles).</summary>
    public IReadOnlyList<SpringChain> Chains => _chains;

    /// <summary>
    /// Turns the dangling legs' and feet's springs on or off (off while the feet are planted by IK or contact). While the springs
    /// are <see cref="Held"/> this only records the wish: the legs take it when they are let go.
    /// </summary>
    public void SetLegSpringsActive(bool active)
    {
        _legSpringsWanted = active;
        ApplyActive();
    }

    /// <summary>Whether the leg springs run when the springs are not held (off while the leg IK plants the feet).</summary>
    public bool LegSpringsWanted => _legSpringsWanted;

    /// <summary>
    /// Every spring held still, riding its parent as posed (the tumble, from the flop to the end of its get-up): nothing turns a
    /// simulator on meanwhile, not even the leg IK letting go. Let go, each simulator is as active as the rig wants it then.
    /// </summary>
    public bool Held
    {
        get => _held;
        set
        {
            _held = value;
            ApplyActive();
        }
    }

    private void ApplyActive()
    {
        if (Simulator != null && GodotObject.IsInstanceValid(Simulator) && Simulator.Active == _held) Simulator.Active = !_held;
        var legs = _legSpringsWanted && !_held;
        if (LegSimulator != null && GodotObject.IsInstanceValid(LegSimulator) && LegSimulator.Active != legs) LegSimulator.Active = legs;
    }

    /// <summary>The Δt the stiffness and drag are tuned for (1/60 s until the first <see cref="MatchTimestep"/>).</summary>
    public float TunedDeltaS => _tunedDelta;

    /// <summary>Wind or any steady push on every chain, as a velocity (m/s): the engine's external force.</summary>
    public void SetWind(Vector3 velocity)
    {
        if (!float.IsFinite(velocity.X + velocity.Y + velocity.Z)) return;
        if (Simulator != null) Simulator.ExternalForce = velocity;
        if (LegSimulator != null) LegSimulator.ExternalForce = velocity;
    }

    /// <summary>Re-tunes every chain for <paramref name="deltaS"/> when it differs from the tuned Δt by more than 5%. Allocation-free.</summary>
    public void MatchTimestep(float deltaS)
    {
        if (_chains.Count == 0 || !float.IsFinite(deltaS) || deltaS <= 0) return;
        deltaS = Mathf.Clamp(deltaS, 1f / 480, 1f / 10);
        if (Mathf.Abs(deltaS - _tunedDelta) <= 0.05f * _tunedDelta) return;
        Tune(deltaS);
    }

    /// <summary>The engine stiffness for a chain at Δt: (2πf)²·L·Δt.</summary>
    public static float Stiffness(float frequencyHz, float lengthM, float deltaS)
    {
        var omega = Mathf.Tau * frequencyHz;
        return omega * omega * lengthM * deltaS;
    }

    /// <summary>The engine drag (velocity kept per step is 1 - drag) for a damping ratio at Δt.</summary>
    public static float Drag(float frequencyHz, float dampingRatio, float deltaS) =>
        Mathf.Clamp(1 - Mathf.Exp(-2 * dampingRatio * Mathf.Tau * frequencyHz * deltaS), 0, 0.95f);

    private void Tune(float deltaS)
    {
        _tunedDelta = deltaS;
        foreach (var chain in _chains)
        {
            chain.Simulator.SetStiffness(chain.Setting, Stiffness(chain.FrequencyHz, chain.RootSegmentM, deltaS));
            chain.Simulator.SetDrag(chain.Setting, Drag(chain.FrequencyHz, chain.DampingRatio, deltaS));
        }
    }

    private sealed record Candidate(int Bone, ChainKind Kind, int Priority, ChainOverride? Override);

    /// <summary>Infers the chains from the roles and hints, applies rig.json, and builds the simulator under <paramref name="skeleton"/>.</summary>
    internal static SecondaryMotion Build(Skeleton3D skeleton, IReadOnlyList<RigBone> bones, MotionProfile profile, RigSidecar sidecar,
        int colliderBone, List<string> notes)
    {
        var candidates = new List<Candidate>();
        foreach (var bone in bones)
        {
            if (bone.Part < 0 || bone.IsRigid) continue;
            sidecar.Chains.TryGetValue(bone.Name, out var chainOverride);
            var inferred = Infer(bone, bones, profile.Gait);
            if (chainOverride?.Enabled == false)
            {
                if (inferred != null) notes.Add($"rig.json removes the {inferred.Value.Kind} chain at {bone.Name}");
                continue;
            }
            if (chainOverride?.Enabled == true)
                candidates.Add(new Candidate(bone.Index, inferred?.Kind ?? ChainKind.Custom, 0, chainOverride));
            else if (inferred != null)
                candidates.Add(new Candidate(bone.Index, inferred.Value.Kind, inferred.Value.Priority, chainOverride));
        }
        candidates.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : a.Bone.CompareTo(b.Bone));

        var used = new bool[bones.Count];
        var chosen = new List<(Candidate Candidate, int[] Path)>();
        foreach (var candidate in candidates)
        {
            if (used[candidate.Bone]) continue;
            if (chosen.Count >= MaxChains)
            {
                notes.Add($"no spring for {bones[candidate.Bone].Name} ({candidate.Kind}): the {MaxChains}-chain budget is spent");
                continue;
            }
            var path = Path(candidate, bones, used);
            foreach (var bone in path) used[bone] = true;
            chosen.Add((candidate, path));
        }
        var result = new SecondaryMotion();
        if (chosen.Count == 0) return result;

        // The capsule the chains collide with: inside the body's mesh, so a limb drawn against the body is not pushed off it.
        var body = bones[colliderBone];
        Vector3 capsuleCentre = Vector3.Zero;
        float capsuleRadius = 0, capsuleHalf = 0, capsuleHeight = 0;
        if (body.HasMesh)
        {
            var size = body.MeshBounds.Size;
            capsuleRadius = 0.8f * 0.5f * Mathf.Min(size.X, size.Z);
            capsuleHeight = Mathf.Max(0.9f * size.Y, 2 * capsuleRadius + 0.001f);
            capsuleCentre = body.MeshBounds.GetCenter();
            capsuleHalf = 0.5f * capsuleHeight - capsuleRadius;
        }
        SpringBoneCollisionCapsule3D? Capsule() => body.HasMesh
            ? new SpringBoneCollisionCapsule3D { Name = "BodyCollider", BoneName = body.Name, PositionOffset = capsuleCentre, Radius = capsuleRadius, Height = capsuleHeight }
            : null;
        static bool IsLeg(ChainKind kind) => kind is ChainKind.DanglingLeg or ChainKind.DanglingFoot;
        var legCount = 0;
        foreach (var (candidate, _) in chosen) if (IsLeg(candidate.Kind)) legCount++;
        if (legCount > 0) result.LegSimulator = new SpringBoneSimulator3D { Name = "LegSprings", SettingCount = legCount };
        if (legCount < chosen.Count) result.Simulator = new SpringBoneSimulator3D { Name = "Springs", SettingCount = chosen.Count - legCount };
        int legSetting = 0, otherSetting = 0;
        for (var i = 0; i < chosen.Count; i++)
        {
            var (candidate, path) = chosen[i];
            var simulator = IsLeg(candidate.Kind) ? result.LegSimulator! : result.Simulator!;
            var setting = IsLeg(candidate.Kind) ? legSetting++ : otherSetting++;
            var end = bones[path[^1]];
            var (direction, tail) = Tail(end);
            var length = tail;
            for (var j = 1; j < path.Length; j++) length += bones[path[j]].RestLocal.Length();
            var rootSegment = path.Length > 1 ? Mathf.Max(bones[path[1]].RestLocal.Length(), 0.002f) : tail;
            var chainOverride = candidate.Override;
            var pendulum = MotionProfile.PendulumHz(length);
            var frequency = Mathf.Clamp(pendulum * profile.Multipliers.Springiness * (chainOverride?.Springiness ?? 1), MinFrequencyHz, MaxFrequencyHz);
            var damping = BaseDampingRatio * profile.Multipliers.SpringDamping * (chainOverride?.Damping ?? 1);
            var root = bones[path[0]];
            var radius = chainOverride?.RadiusM ?? Mathf.Clamp(0.3f * MinExtent(root), 0.0008f, 0.004f);
            var collides = body.HasMesh && (chainOverride?.Collide ?? Clear(path, end, direction, tail, bones, body, capsuleCentre, capsuleRadius, capsuleHalf, radius));
            var chain = new SpringChain
            {
                Simulator = simulator, Setting = setting, Kind = candidate.Kind, RootBone = path[0], EndBone = path[^1], Bones = path,
                TailDirection = direction, TailLengthM = tail, LengthM = length, RootSegmentM = rootSegment, PendulumHz = pendulum,
                FrequencyHz = frequency, DampingRatio = damping, RadiusM = radius, GravityMps = chainOverride?.GravityMps ?? 0f, Collides = collides,
            };
            result._chains.Add(chain);
            simulator.SetRootBoneName(setting, root.Name);
            simulator.SetEndBoneName(setting, end.Name);
            simulator.SetExtendEndBone(setting, true);
            simulator.SetEndBoneDirection(setting, direction);
            simulator.SetEndBoneLength(setting, tail);
            simulator.SetRadius(setting, radius);
            simulator.SetGravity(setting, chain.GravityMps);
            simulator.SetGravityDirection(setting, Vector3.Down);
            simulator.SetEnableAllChildCollisions(setting, collides);
            profile.AddHanging(new HangingPart(i, chain.RootBone, chain.EndBone, length, pendulum, frequency));
        }
        // Leg springs first: the modifiers run in child order, and the legs hang from the body the others may lean.
        foreach (var simulator in new[] { result.LegSimulator, result.Simulator })
        {
            if (simulator == null) continue;
            var capsule = Capsule();
            if (capsule != null)
            {
                simulator.AddChild(capsule);
                result.BodyCollider ??= capsule;
            }
            skeleton.AddChild(simulator);
        }
        result.Tune(1f / 60);
        return result;
    }

    private static (ChainKind Kind, int Priority)? Infer(RigBone bone, IReadOnlyList<RigBone> bones, MotionKind gait)
    {
        var parent = bones[bone.Parent];
        var hangs = gait is MotionKind.Hop or MotionKind.Float;
        return bone.Role switch
        {
            PartRole.Hat when parent.Role != PartRole.Hat => (ChainKind.Hat, 1),
            PartRole.Hair when parent.Role != PartRole.Hair => (ChainKind.Hair, 2),
            PartRole.Ear when parent.Role != PartRole.Ear => (ChainKind.Ear, 3),
            PartRole.Tail when parent.Role != PartRole.Tail => (ChainKind.Tail, 3),
            PartRole.Leg when hangs => (ChainKind.DanglingLeg, 4),
            PartRole.Foot or PartRole.Shoe when hangs && parent.Role != PartRole.Leg => (ChainKind.DanglingFoot, 4),
            // The hop and stride gaits swing the arms; under a waddle or a float nothing does, so they hang on springs.
            PartRole.Arm when gait is MotionKind.Waddle or MotionKind.Float => (ChainKind.Arm, 5),
            PartRole.Horn when parent.Role != PartRole.Horn => (ChainKind.Horn, 6),
            PartRole.Flame => (ChainKind.Flame, 7),
            PartRole.Wand => (ChainKind.Wand, 8),
            PartRole.Sparkle when bone.Effect is EffectMotion.Drift or EffectMotion.Orbit => (ChainKind.Sparkle, 9),
            _ => null,
        };
    }

    /// <summary>The chain's bones: down from the root while there is exactly one free child that belongs to the same chain.</summary>
    private static int[] Path(Candidate candidate, IReadOnlyList<RigBone> bones, bool[] used)
    {
        var limit = Math.Min(candidate.Override?.MaxBones ?? MaxBonesPerChain, MaxBonesPerChain);
        var path = new List<int> { candidate.Bone };
        var current = bones[candidate.Bone];
        while (path.Count < limit)
        {
            var next = -1;
            var count = 0;
            foreach (var child in current.Children)
            {
                var bone = bones[child];
                if (bone.IsRigid || used[child] || !Continues(candidate.Kind, bones[candidate.Bone].Role, bone.Role)) continue;
                next = child;
                count++;
            }
            if (count != 1) break;
            path.Add(next);
            current = bones[next];
            if (candidate.Kind is ChainKind.DanglingLeg or ChainKind.Arm) break;
        }
        return path.ToArray();
    }

    private static bool Continues(ChainKind kind, PartRole rootRole, PartRole child) => kind switch
    {
        ChainKind.DanglingLeg => PartRoles.IsFoot(child),
        ChainKind.Arm => PartRoles.IsHand(child),
        ChainKind.Hat or ChainKind.Hair or ChainKind.Ear or ChainKind.Tail or ChainKind.Horn or ChainKind.Custom => child == rootRole,
        _ => false,
    };

    /// <summary>The end bone's tail: along the axis on which its mesh reaches farthest from the joint.</summary>
    private static (SkeletonModifier3D.BoneDirection Direction, float Length) Tail(RigBone end)
    {
        if (!end.HasMesh) return (SkeletonModifier3D.BoneDirection.PlusY, 0.005f);
        var low = end.MeshBounds.Position;
        var high = end.MeshBounds.End;
        Span<float> reach = stackalloc float[] { high.X, -low.X, high.Y, -low.Y, high.Z, -low.Z };
        var best = 0;
        for (var i = 1; i < 6; i++)
            if (reach[i] > reach[best] + 1e-6f) best = i;
        var direction = best switch
        {
            0 => SkeletonModifier3D.BoneDirection.PlusX, 1 => SkeletonModifier3D.BoneDirection.MinusX,
            2 => SkeletonModifier3D.BoneDirection.PlusY, 3 => SkeletonModifier3D.BoneDirection.MinusY,
            4 => SkeletonModifier3D.BoneDirection.PlusZ, _ => SkeletonModifier3D.BoneDirection.MinusZ,
        };
        return (direction, Mathf.Max(reach[best], 0.002f));
    }

    private static Vector3 Axis(SkeletonModifier3D.BoneDirection direction) => direction switch
    {
        SkeletonModifier3D.BoneDirection.PlusX => Vector3.Right, SkeletonModifier3D.BoneDirection.MinusX => Vector3.Left,
        SkeletonModifier3D.BoneDirection.PlusY => Vector3.Up, SkeletonModifier3D.BoneDirection.MinusY => Vector3.Down,
        SkeletonModifier3D.BoneDirection.PlusZ => Vector3.Back, _ => Vector3.Forward,
    };

    private static float MinExtent(RigBone bone)
    {
        if (!bone.HasMesh) return 0.004f;
        var size = bone.MeshBounds.Size;
        return Mathf.Min(size.X, Mathf.Min(size.Y, size.Z));
    }

    /// <summary>True when every moving joint of the chain sits clear of the body's capsule at rest (else it would be pushed off its drawn pose).</summary>
    private static bool Clear(int[] path, RigBone end, SkeletonModifier3D.BoneDirection direction, float tail, IReadOnlyList<RigBone> bones,
        RigBone body, Vector3 centre, float radius, float half, float jointRadius)
    {
        var a = body.RestModel + centre + new Vector3(0, -half, 0);
        var b = body.RestModel + centre + new Vector3(0, half, 0);
        bool Inside(Vector3 point)
        {
            var t = Mathf.Clamp((point.Y - a.Y) / Mathf.Max(b.Y - a.Y, 1e-6f), 0, 1);
            return point.DistanceTo(a.Lerp(b, t)) < radius + jointRadius;
        }
        for (var j = 1; j < path.Length; j++)
            if (Inside(bones[path[j]].RestModel)) return false;
        return !Inside(end.RestModel + Axis(direction) * tail);
    }
}
