using System;
using System.Collections.Generic;
using Godot;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;

namespace GarageGame.Native.Characters.Contact;

/// <summary>What a landing meant, from <see cref="PlayerVitals.FallDamage"/> (what it would cost in Challenge, in every mode).</summary>
public enum LandingTier
{
    None = 0,
    /// <summary>No damage: a squash, deeper the faster.</summary>
    Squash,
    /// <summary>1 to 24 health: the knees or the body buckle and a hand (or the body) touches down for 0.4 s; control is kept.</summary>
    Stumble,
    /// <summary>From <see cref="ContactLayer.TumbleFromHealth"/>: the tumble (K7) takes over; this layer only starts the squash.</summary>
    Tumble,
    /// <summary>Caught by deep water: never a stumble, a soft squash.</summary>
    Water,
}

/// <summary>A landing as the contact layer read it: the hook for the look's dust (or splash) cue and for sounds.</summary>
public readonly record struct ContactLanding(Vector3 Position, float SpeedMps, float Damage, LandingTier Tier);

/// <summary>
/// Layer 2 of the pose stack (K4, docs/runs/RUN-2-CHARACTERS.md, "Contact: how a character meets the land"): each foot finds
/// the ground under it (one ray per foot on the body's collision mask); legs plant their soles with the rig's leg IK, a body
/// without legs pitches and rolls its root to the floor (at most 25°); the pelvis drops so every foot can plant; at a step the
/// leading foot lifts first and the body follows a frame later; landings squash by speed and stumble where they would hurt
/// (the tiers from <see cref="PlayerVitals.FallDamage"/>); wading legs trail and paddle, swimmers stroke and kick, divers tuck
/// and stream; climbers lie along the face and reach up hand over hand; carriers raise closed hands to the load over the head;
/// the wind reaches the springs. Everything comes from the parts' roles and sides and the motion profile, never a name.
/// Nothing here moves the capsule: the layer reads the body's state and casts rays; it writes only the pose, the IK targets
/// and the springs' external force.
/// </summary>
public sealed partial class ContactLayer : IPoseLayer<BodyState>
{
    // ---- The named numbers. The landing tiers' numbers live in PlayerVitals (FallDamage); this is the one this layer adds.

    /// <summary>From this much Challenge damage (health) a landing is the tumble's (K7); below it and above 0, a stumble. The tumble's own threshold, read from the vitals.</summary>
    public const float TumbleFromHealth = TumbleTiers.TumbleHealth;
    /// <summary>A foot that finds no ground within this distance of where its pose puts it keeps its pose.</summary>
    public const float FootSearchM = 0.03f;
    /// <summary>A body without legs pitches and rolls its root to the floor normal by at most this much.</summary>
    public static readonly float MaxRootTiltRad = Mathf.DegToRad(25);
    /// <summary>A body with legs rolls this share of a slope across it (never pitching), at most <see cref="MaxLegRollRad"/>.</summary>
    public const float LegRollShare = 0.5f;
    public static readonly float MaxLegRollRad = Mathf.DegToRad(15);
    /// <summary>A climber leans along a face that is not vertical by at most this much.</summary>
    public static readonly float MaxClimbLeanRad = Mathf.DegToRad(35);
    /// <summary>The ground a foot plants on must face up at least this much (a wall's edge is not ground).</summary>
    public const float MinGroundNormalY = 0.5f;
    /// <summary>The tallest step the capsule climbs (SmallPlayerController.StepHeightM) and the least rise that counts as one.</summary>
    public const float MaxStepM = 0.022f, MinStepM = 0.008f;
    /// <summary>How far past the capsule's front the step probe looks, and how far the leading foot reaches onto the tread.</summary>
    public const float StepLookM = 0.015f, StepReachM = 0.006f;
    /// <summary>How far the pelvis may drop to plant a foot: legs bend for it (a share of the hip height); a body without legs settles only its seat.</summary>
    public const float LegDropShare = 0.3f, LeglessMaxDropM = 0.006f;
    /// <summary>The most a foot without a leg lifts to meet the ground.</summary>
    public const float MaxFootLiftM = 0.025f;
    /// <summary>Feet are planted while the gait holds the lowest sole within this height of its ground (a hop's flight lets them dangle).</summary>
    public const float PlantLiftM = 0.006f;
    /// <summary>A stumble lasts this long (control is kept).</summary>
    public const float StumbleS = 0.4f;
    /// <summary>The squash on a hard landing never passes this (springs under a scaled parent misbehave past it).</summary>
    public const float MaxSquash = 0.15f;
    /// <summary>Landings slower than this are footfalls, not landings.</summary>
    public const float MinLandingMps = 0.2f;
    /// <summary>
    /// How hard the wind (per m/s of wind) and a diver's own speed (per m/s of travel) push the springs, as an acceleration (m/s²).
    /// The engine's external force is a velocity added every step, like the stiffness the rig tunes to the frame (RIG-API.md,
    /// section 4), so it is set to this acceleration times Δt: the lean is the same at any frame rate, and longer, softer parts
    /// lean further (a 0.4 m/s breeze leans Potato Man's hat and hair about 20°, a sparkle a few degrees; the 1 m/s limit about
    /// 40°). The family tunes these two numbers.
    /// </summary>
    public const float WindResponse = 2.0f, StreamResponse = 3.0f;

    /// <summary>The springs' external force for a wind (m/s, X and Z) on a frame of <paramref name="deltaS"/> seconds.</summary>
    public static Vector3 WindForce(Vector2 windMps, float deltaS) => new Vector3(windMps.X, 0, windMps.Y) * (WindResponse * deltaS);

    private const float TiltTau = 0.07f, FootTau = 0.03f, DropTau = 0.05f, RiseLagTau = 0.03f, ModeTau = 0.12f, PlantTau = 0.06f, PressTau = 0.08f;
    private const float StumblePitchRad = 0.22f, StumbleBuckle = 0.35f, LurchRad = 0.2f;
    private const float StrokeLengthM = 0.16f, StrokeIdleHz = 0.7f, ClimbCycleM = 0.08f;
    private const float TrailRad = 0.35f, PaddleRad = 0.3f, KickRad = 0.4f, TuckRad = 0.45f, CurlRad = 0.7f;
    private const float WadeIkRelease = 0.6f;

    private struct Foot
    {
        public int Bone, Parent, Leg;
        /// <summary>The sole in the foot bone's space: the leg IK's virtual end, or the bottom of the foot's mesh.</summary>
        public Vector3 Sole;
        public float Side;
    }

    private struct Limb
    {
        public int Bone, Parent, Hand;
        /// <summary>The limb's reach at rest (to its hand, or its mesh), in its parent's frame (rests carry no rotation).</summary>
        public Vector3 Rest;
        public float Side;
    }

    private readonly CharacterBody _rig;
    private readonly SmallPlayerController _controller;
    private readonly Foot[] _feet;
    private readonly Limb[] _arms;
    private readonly Limb[] _legs;
    private readonly Limb[] _streams;
    private readonly int[] _hands;
    private readonly int _head;
    private readonly bool _legless;
    private readonly bool _hasHands;
    private readonly float _maxDrop;
    private readonly float _toeZ;
    private readonly float _capsuleRadius;
    private readonly RayCast3D[] _rays;
    private readonly int _probeRay, _wallRay;

    // Per foot, this frame.
    private readonly Vector3[] _posed;
    private readonly Vector3[] _tilted;
    private readonly bool[] _hit;
    private readonly Vector3[] _ground;
    private readonly float[] _offset;
    private readonly Vector3[] _target;
    private readonly float[] _lift;

    // What the layer keeps between frames.
    private long _lastFrame = -1;
    private float _lastY;
    private double _lastDistance;
    private int _stepsClimbed;
    private bool _wasClimbing;
    private Quaternion _tilt = Quaternion.Identity;
    private Vector3 _tiltUp = Vector3.Up;
    private float _tiltWeight;
    private float _drop, _riseLag, _stepAhead, _press;
    private Vector3 _pressDir = Vector3.Back;
    private float _wade, _swim, _dive, _climb, _carry, _plant;
    private float _strokePhase, _climbPhase, _wadePhase;
    private float _squashAmp, _squashT = 1e6f, _squashDur = 1;
    private float _stumbleT = 1e6f;
    private float _stumbleSide = 1;
    private int _landings;
    private bool _ikOn;
    private float _ikInfluence = -1;
    private Vector3 _windSet;
    private Vector3 _stepDir = Vector3.Forward;
    private Transform3D _skeleton = Transform3D.Identity;

    public ContactLayer(CharacterBody rig, SmallPlayerController controller)
    {
        _rig = rig ?? throw new ArgumentNullException(nameof(rig));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        var bones = rig.Bones;
        var profile = rig.Profile;
        _capsuleRadius = controller.BodyRadiusM > 0 ? controller.BodyRadiusM : 0.02f;

        var feet = new List<Foot>();
        var ikFeet = new HashSet<int>();
        for (var i = 0; i < rig.Legs.Count; i++)
        {
            var leg = rig.Legs[i];
            ikFeet.Add(leg.FootBone);
            feet.Add(new Foot { Bone = leg.FootBone, Parent = bones[leg.FootBone].Parent, Leg = i, Sole = leg.SoleOffset, Side = SideOf(bones[leg.FootBone]) });
        }
        foreach (var bone in bones)
        {
            if (feet.Count >= 4) break;
            if (bone.Part < 0 || bone.IsRigid || !PartRoles.IsFoot(bone.Role) || !bone.HasMesh || ikFeet.Contains(bone.Index)) continue;
            var bounds = bone.MeshBounds;
            feet.Add(new Foot { Bone = bone.Index, Parent = bone.Parent, Leg = -1, Sole = new Vector3(bounds.GetCenter().X, bounds.Position.Y, bounds.GetCenter().Z), Side = SideOf(bone) });
        }
        _feet = feet.ToArray();
        _legless = rig.Legs.Count == 0;
        _maxDrop = _legless ? LeglessMaxDropM : LegDropShare * Mathf.Max(profile.HipHeightM, 0.01f);

        var toe = 0f;
        foreach (var foot in _feet) toe = Mathf.Min(toe, bones[foot.Bone].RestModel.Z + bones[foot.Bone].MeshBounds.Position.Z);
        if (_feet.Length == 0 && bones[rig.BodyBone].HasMesh) toe = bones[rig.BodyBone].RestModel.Z + bones[rig.BodyBone].MeshBounds.Position.Z;
        _toeZ = toe;

        var arms = new List<Limb>();
        var legs = new List<Limb>();
        var streams = new List<Limb>();
        var hands = new List<int>();
        foreach (var bone in bones)
        {
            if (bone.Part < 0 || bone.IsRigid) continue;
            if (bone.Role == PartRole.Arm)
            {
                var hand = -1;
                foreach (var child in bone.Children)
                    if (PartRoles.IsHand(bones[child].Role) && !bones[child].IsRigid) { hand = child; break; }
                var reach = hand >= 0 ? bones[hand].RestModel - bone.RestModel : ContactMath.Reach(bone, Vector3.Down) * Mathf.Max(bone.ReachM, 0.005f);
                arms.Add(new Limb { Bone = bone.Index, Parent = bone.Parent, Hand = hand, Rest = ContactMath.Direction(reach, Vector3.Down), Side = SideOf(bone) });
            }
            else if (bone.Role == PartRole.Leg)
            {
                legs.Add(new Limb { Bone = bone.Index, Parent = bone.Parent, Hand = -1, Rest = ContactMath.Reach(bone, Vector3.Down), Side = SideOf(bone) });
            }
            else if (PartRoles.IsFoot(bone.Role) && bones[bone.Parent].Role != PartRole.Leg)
            {
                // A foot without a leg kicks, trails and presses as the leg would.
                legs.Add(new Limb { Bone = bone.Index, Parent = bone.Parent, Hand = -2, Rest = ContactMath.Reach(bone, Vector3.Down), Side = SideOf(bone) });
            }
            else if ((bone.Role == PartRole.Hat && bones[bone.Parent].Role != PartRole.Hat) || (bone.Role == PartRole.Hair && bones[bone.Parent].Role != PartRole.Hair))
            {
                streams.Add(new Limb { Bone = bone.Index, Parent = bone.Parent, Hand = -1, Rest = ContactMath.Reach(bone, Vector3.Up), Side = SideOf(bone) });
            }
            if (PartRoles.IsHand(bone.Role)) hands.Add(bone.Index);
        }
        _arms = arms.ToArray();
        _legs = legs.ToArray();
        _streams = streams.ToArray();
        _hands = hands.ToArray();
        _hasHands = _hands.Length > 0;
        _head = rig.HeadBone != rig.BodyBone ? rig.HeadBone : -1;

        _posed = new Vector3[_feet.Length];
        _tilted = new Vector3[_feet.Length];
        _hit = new bool[_feet.Length];
        _ground = new Vector3[_feet.Length];
        _offset = new float[_feet.Length];
        _target = new Vector3[_feet.Length];
        _lift = new float[_feet.Length];

        // One ray per foot, the step probe and the wall ray: top-level nodes cast by hand (allocation-free), never on their own.
        _probeRay = _feet.Length;
        _wallRay = _feet.Length + 1;
        _rays = new RayCast3D[_feet.Length + 2];
        var holder = new Node3D { Name = "ContactRays" };
        rig.AddChild(holder);
        for (var i = 0; i < _rays.Length; i++)
        {
            var ray = new RayCast3D
            {
                Name = i < _feet.Length ? "Foot" + i : i == _probeRay ? "StepProbe" : "Wall",
                TopLevel = true, Enabled = false, CollisionMask = RoomBuilder.BodyMask, CollideWithAreas = false, CollideWithBodies = true,
                HitFromInside = false, TargetPosition = Vector3.Down * 0.01f,
            };
            ray.AddException(controller);
            holder.AddChild(ray);
            _rays[i] = ray;
        }
    }

    public PoseLayer Layer => PoseLayer.Contact;

    // ---- What the layer did on its last frame (tests, the look's cues, the tumble).

    public CharacterBody Rig => _rig;
    public LandingTier LastTier { get; private set; }
    public float LastLandingSpeedMps { get; private set; }
    public float LastLandingDamage { get; private set; }
    /// <summary>Landings read so far.</summary>
    public int Landings => _landings;
    /// <summary>The root's squash now (0 none; 0.1 is 10% shorter).</summary>
    public float Squash { get; private set; }
    public bool Stumbling => _stumbleT < StumbleS;
    /// <summary>0 to 1 over the stumble (its buckle and touch-down), 0 otherwise.</summary>
    public float StumbleWeight { get; private set; }
    /// <summary>The arm reaching down in a stumble (its side: +1 the character's right, -1 its left).</summary>
    public float StumbleSide => _stumbleSide;
    /// <summary>The root's tilt to the floor (a body without legs) or along a climbed face, in the skeleton's space.</summary>
    public Quaternion RootTilt => _tilt;
    public float DropM => _drop;
    public float RiseLagM => _riseLag;
    public float StepAheadM => _stepAhead;
    public int LeadFoot { get; private set; } = -1;
    public bool IkOn => _ikOn;
    public float IkInfluence => _ikOn ? Mathf.Max(_ikInfluence, 0) : 0;
    public float Wading => _wade;
    public float SwimWeight => _swim;
    public float DiveWeight => _dive;
    public float ClimbWeight => _climb;
    public float CarryWeight => _carry;
    public float PressM => _press;
    /// <summary>Rays cast on the last frame.</summary>
    public int Casts { get; private set; }
    public int FootCount => _feet.Length;
    public int FootBone(int i) => _feet[i].Bone;
    /// <summary>The foot's sole in its bone's space.</summary>
    public Vector3 FootSole(int i) => _feet[i].Sole;
    public bool FootIsLeg(int i) => _feet[i].Leg >= 0;
    public bool FootFoundGround(int i) => _hit[i];
    public Vector3 FootGround(int i) => _ground[i];
    public float FootOffsetM(int i) => _offset[i];
    /// <summary>Where the pose under this layer (the gait's) put the sole on the last frame, in the world.</summary>
    public Vector3 FootPosed(int i) => _skeleton * _posed[i];
    /// <summary>The skeleton's world transform on the last frame.</summary>
    public Transform3D SkeletonTransform => _skeleton;
    /// <summary>Where the leg IK puts this sole (world), when the foot is on a leg.</summary>
    public Vector3 FootTarget(int i) => _target[i];
    /// <summary>How far a foot without a leg was lifted to meet the ground.</summary>
    public float FootLiftM(int i) => _lift[i];
    public bool HasHands => _hasHands;
    public bool Legless => _legless;

    /// <summary>Raised on each landing (the look's dust or splash cue, a sound): where, how fast, what it would cost, the tier.</summary>
    public event Action<ContactLanding>? Landed;

    /// <summary>
    /// Let go of everything the layer holds outside the pose (the leg IK, whose leg springs the rig then wants running): the
    /// tumble suspends this layer, and its guard calls this so stale foot targets never fight the flop. The tumble holds every
    /// spring still meanwhile (SecondaryMotion.Held), so letting go never frees the legs mid-flop. The layer starts fresh when it
    /// runs again.
    /// </summary>
    public void Release()
    {
        if (_ikOn) _rig.UseLegIk(false);
        _ikOn = false;
        _ikInfluence = -1;
        _plant = 0;
        _lastFrame = -1;
    }

    public void Apply(PoseBuffer pose, in BodyState state, in PoseClock clock)
    {
        Casts = 0;
        if (state.Tick == 0 || !_rig.IsInsideTree()) return;
        var dt = Mathf.Clamp(clock.DeltaS, 0, 0.1f);
        if (!(dt > 0)) return;
        var resumed = _lastFrame < 0 || clock.Frame != _lastFrame + 1;
        _lastFrame = clock.Frame;
        if (resumed) Restart(state);
        var skeleton = _rig.Skeleton.GlobalTransform;
        if (!ContactMath.Finite(skeleton.Origin) || !ContactMath.Finite(state.Position)) return;
        _skeleton = skeleton;
        var toSkeleton = skeleton.AffineInverse();
        var upS = ContactMath.Direction(toSkeleton.Basis * Vector3.Up, Vector3.Up);

        Events(state, dt);
        Modes(state, dt);
        for (var i = 0; i < _feet.Length; i++) _posed[i] = pose.ModelTransform(_feet[i].Bone) * _feet[i].Sole;

        var grounded = state.Grounded && !state.Climbing && !state.Swimming && !state.Floating;
        Tilt(state, toSkeleton, grounded, dt);

        if (grounded) FindGround(pose, state, skeleton, dt);
        else ClearGround(dt);
        Press(state, skeleton, toSkeleton, dt);

        ApplyRoot(pose, upS);
        PlaceFeet(pose, state, skeleton, toSkeleton, upS, grounded);
        Limbs(pose, state, toSkeleton, dt);
        Secondary(state, grounded, dt);

        _lastY = state.Position.Y;
        _lastDistance = state.DistanceM;
        _wasClimbing = state.Climbing;
    }

    /// <summary>A fresh start (the first frame, or after a suspension): no carried-over squash, lag or tilt.</summary>
    private void Restart(in BodyState state)
    {
        _tilt = Quaternion.Identity;
        _tiltUp = Vector3.Up;
        _tiltWeight = 0;
        _drop = _riseLag = _stepAhead = _press = 0;
        _squashT = _stumbleT = 1e6f;
        _lastY = state.Position.Y;
        _lastDistance = state.DistanceM;
        _stepsClimbed = _controller.StepsClimbed;
        _wasClimbing = state.Climbing;
        Array.Clear(_offset);
        Array.Clear(_hit);
        Array.Clear(_lift);
    }

    private static float SideOf(RigBone bone) => bone.Side switch
    {
        BodySide.Right => 1,
        BodySide.Left => -1,
        _ => bone.RestModel.X >= 0 ? 1 : -1,
    };

    /// <summary>Casts one of the layer's rays (world space); false when it finds nothing.</summary>
    private bool Cast(int index, Vector3 from, Vector3 to, out Vector3 point, out Vector3 normal)
    {
        var ray = _rays[index];
        ray.GlobalTransform = new Transform3D(Basis.Identity, from);
        ray.TargetPosition = to - from;
        ray.ForceRaycastUpdate();
        Casts++;
        if (!ray.IsColliding())
        {
            point = normal = Vector3.Zero;
            return false;
        }
        point = ray.GetCollisionPoint();
        normal = ray.GetCollisionNormal();
        return ContactMath.Finite(point) && ContactMath.Finite(normal);
    }

    private static Basis SafeInverse(Basis basis)
    {
        var det = basis.Determinant();
        return Mathf.Abs(det) > 1e-12f && float.IsFinite(det) ? basis.Inverse() : Basis.Identity;
    }
}
