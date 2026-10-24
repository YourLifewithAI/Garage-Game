using System;
using Godot;

namespace GarageGame.Native.Characters.Contact;

/// <summary>
/// The look targets (K4's part of layer 3, Life): turns the rig's look-ats on and moves their target. A walking body looks
/// where it is going, a little down at its path; a climber looks up the face; now and then it glances at a point of interest
/// (the other avatar), and it watches that point while it falls and for a while after (a tumbling player). Writes no bones: the
/// head and eye look-ats (LookAtModifier3D) turn toward the target within the rig's limits, and the target is kept inside
/// those limits too, so a face never strains against them. A floating body (the Gubble) glances more often and for longer.
/// </summary>
public sealed class LookTargetLayer : IPoseLayer<BodyState>
{
    /// <summary>How far ahead the face looks when nothing calls it, and the nearest and farthest it focuses on a point.</summary>
    public const float LookAheadM = 0.6f, NearestM = 0.15f, FarthestM = 2.0f;
    /// <summary>A point of interest farther than this is not glanced at.</summary>
    public const float InterestRangeM = 6.0f;
    /// <summary>A point of interest falling faster than this is watched (a fall, then the tumble) for <see cref="WatchAfterFallS"/>.</summary>
    public const float WatchFallMps = 1.0f, WatchAfterFallS = 2.5f;
    /// <summary>The target stays within this share of each look-at's limits.</summary>
    public const float LimitShare = 0.9f;
    /// <summary>A head that is the whole body turns gently (its look-at turns everything under it).</summary>
    public const float BodyHeadInfluence = 0.5f;

    private readonly CharacterBody _rig;
    private readonly ContactLayer? _contact;
    private readonly Func<Vector3?>? _interest;
    private readonly Vector3 _focus;
    private readonly float _yawLimit, _pitchLimit;
    private uint _seed;
    private double _nextGlance, _glanceUntil;
    private float _watchS;
    private Vector3 _direction;
    private Vector3 _lastInterest;
    private bool _hasInterest;
    private float _active;
    private bool _on;
    private long _lastFrame = -1;
    private float _headInfluence = -1, _eyeInfluence = -1;

    public LookTargetLayer(CharacterBody rig, ContactLayer? contact, Func<Vector3?>? pointOfInterest)
    {
        _rig = rig ?? throw new ArgumentNullException(nameof(rig));
        _contact = contact;
        _interest = pointOfInterest;
        var look = rig.Look;
        // The target starts 1 m straight ahead of the eyes (K1): that point less the metre is where the face looks from.
        _focus = look.Target.Position - new Vector3(0, 0, -1);
        if (look.Head != null)
        {
            _yawLimit = look.Head.YawLimitRad;
            _pitchLimit = look.Head.PitchLimitRad;
        }
        else
        {
            _yawLimit = LookRig.EyeYawLimit;
            _pitchLimit = LookRig.EyePitchLimit;
        }
        // A seed from the body's own proportions (never its name): two characters glance at different moments.
        _seed = 2166136261u ^ (uint)rig.BoneCount * 16777619u ^ (uint)(rig.Profile.VolumeM3 * 1e9f);
        if (_seed == 0) _seed = 1;
        _nextGlance = 1.5 + 2.0 * Next();
    }

    public PoseLayer Layer => PoseLayer.Life;

    /// <summary>Where the face is told to look now (world, unit).</summary>
    public Vector3 Direction => _direction;
    public bool Glancing { get; private set; }
    public bool Watching => _watchS > 0;
    public bool HasPointOfInterest => _interest != null;
    /// <summary>The share of the look-ats' influence now (they ease in over 0.4 s).</summary>
    public float Active => _active;

    /// <summary>Keep the face on the point of interest for this long (the integrator, or the tumble, can ask).</summary>
    public void WatchFor(float seconds)
    {
        if (float.IsFinite(seconds) && seconds > 0) _watchS = Mathf.Max(_watchS, Mathf.Min(seconds, 10));
    }

    /// <summary>Turns the look-ats off (the tumble suspends this layer); they ease back in when it runs again.</summary>
    public void Release()
    {
        if (_on) _rig.Look.SetActive(false);
        _on = false;
        _active = 0;
        _headInfluence = _eyeInfluence = -1;
        _lastFrame = -1;
    }

    public void Apply(PoseBuffer pose, in BodyState state, in PoseClock clock)
    {
        var look = _rig.Look;
        if (look.Count == 0 || state.Tick == 0 || !_rig.IsInsideTree()) return;
        var dt = Mathf.Clamp(clock.DeltaS, 0, 0.1f);
        var resumed = _lastFrame < 0 || clock.Frame != _lastFrame + 1;
        _lastFrame = clock.Frame;
        var skeleton = _rig.Skeleton.GlobalTransform;
        if (!ContactMath.Finite(skeleton.Origin)) return;
        var basis = skeleton.Basis.Orthonormalized();
        var eye = skeleton * _focus;
        var forward = ContactMath.Direction(basis * Vector3.Forward, Vector3.Forward);
        var flatForward = ContactMath.Direction(new Vector3(forward.X, 0, forward.Z), forward);

        // Where it is going.
        var travel = new Vector3(state.Velocity.X, 0, state.Velocity.Z);
        var desired = travel.Length() > 0.06f && float.IsFinite(travel.Length())
            ? ContactMath.Direction(travel.Normalized() + Vector3.Down * 0.12f, flatForward)
            : ContactMath.Direction(flatForward + Vector3.Down * 0.08f, flatForward);
        if (state.Climbing) desired = ContactMath.Direction(Vector3.Up * 0.8f - ContactMath.Direction(state.ClimbNormal, Vector3.Back) * 0.6f, desired);
        if (_contact != null && _contact.Stumbling) desired = ContactMath.Direction(flatForward + Vector3.Down * 0.8f, desired);

        // The point of interest: glanced at now and then; watched while it falls and for a while after.
        var distance = LookAheadM;
        Glancing = false;
        _watchS = Mathf.Max(0, _watchS - dt);
        if (_interest != null)
        {
            var point = _interest();
            if (point is { } p && ContactMath.Finite(p))
            {
                if (_hasInterest && !resumed && dt > 0 && Mathf.Abs(p.Y - _lastInterest.Y) / dt > WatchFallMps) _watchS = WatchAfterFallS;
                _lastInterest = p;
                _hasInterest = true;
                var time = clock.TimeS;
                if (time >= _nextGlance)
                {
                    var floating = state.Floating;
                    _glanceUntil = time + (floating ? 1.5 + 1.5 * Next() : 0.8 + 0.8 * Next());
                    _nextGlance = _glanceUntil + (floating ? 1.0 + 2.0 * Next() : 3.0 + 4.0 * Next());
                }
                var toPoint = p - eye;
                var range = toPoint.Length();
                if ((time < _glanceUntil || _watchS > 0) && range > 0.05f && range < InterestRangeM)
                {
                    desired = toPoint / range;
                    distance = Mathf.Clamp(range, NearestM, FarthestM);
                    Glancing = true;
                }
            }
            else _hasInterest = false;
        }

        // Inside the look-ats' limits, relative to the body.
        var local = ContactMath.Direction(basis.Transposed() * desired, Vector3.Forward);
        var yaw = Mathf.Atan2(local.X, -local.Z);
        var pitch = Mathf.Asin(Mathf.Clamp(local.Y, -1, 1));
        var yawLimit = LimitShare * _yawLimit;
        var pitchLimit = LimitShare * _pitchLimit * (look.HeadIsBody ? 0.3f : 1);
        yaw = Mathf.Clamp(yaw, -yawLimit, yawLimit);
        pitch = Mathf.Clamp(pitch, -pitchLimit, pitchLimit);
        var clamped = new Vector3(Mathf.Sin(yaw) * Mathf.Cos(pitch), Mathf.Sin(pitch), -Mathf.Cos(yaw) * Mathf.Cos(pitch));
        desired = ContactMath.Direction(basis * clamped, forward);

        if (resumed || _direction == Vector3.Zero) _direction = desired;
        else _direction = ContactMath.Direction(_direction.Lerp(desired, ContactMath.Rate(dt, Glancing ? 0.08f : 0.12f)), desired);
        look.Target.GlobalPosition = eye + _direction * distance;

        // On, easing in; a swimmer's head (lying in the water) turns less.
        if (!_on)
        {
            look.SetActive(true, 0);
            _on = true;
            _headInfluence = _eyeInfluence = 0;
        }
        _active = Mathf.MoveToward(_active, 1, dt / 0.4f);
        var head = _active * (look.HeadIsBody ? BodyHeadInfluence : 1) * (state.Swimming ? 0.7f : 1);
        var eyes = _active;
        if (look.Head != null && Mathf.Abs(head - _headInfluence) > 1e-4f)
        {
            look.Head.Modifier.Influence = head;
            _headInfluence = head;
        }
        if (Mathf.Abs(eyes - _eyeInfluence) > 1e-4f)
        {
            for (var i = 0; i < look.Eyes.Count; i++) look.Eyes[i].Modifier.Influence = eyes;
            _eyeInfluence = eyes;
        }
    }

    /// <summary>A deterministic number from 0 to 1 (xorshift): the glances' rhythm, the same in every run.</summary>
    private double Next()
    {
        _seed ^= _seed << 13;
        _seed ^= _seed >> 17;
        _seed ^= _seed << 5;
        return (_seed & 0xFFFFFF) / (double)0x1000000;
    }
}
