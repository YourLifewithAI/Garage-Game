using Godot;
using GarageGame.Native.Characters.Gaits;
using GarageGame.Native.Kernel;

namespace GarageGame.Native.Characters.Contact;

/// <summary>The ground: landings, feet on the floor and on slopes, steps, the climb's lean and press, the root.</summary>
public sealed partial class ContactLayer
{
    /// <summary>Landings (their tier from fall damage), steps the capsule climbed, and a climb's end (the pull-over's hop).</summary>
    private void Events(in BodyState state, float dt)
    {
        if (state.Landed)
        {
            var speed = float.IsFinite(state.LandingSpeedMps) ? Mathf.Max(0, state.LandingSpeedMps) : 0;
            var deep = state.LandedInDeepWater;
            // What this landing would cost this body at Standard (its Cat feet safe drop included): the tumble's own measure.
            var knacks = _controller.Knacks;
            var damage = TumbleTiers.Cost(speed, deep, knacks);
            var tier = deep ? LandingTier.Water : damage <= TumbleTiers.StumbleHealth ? LandingTier.Squash : damage < TumbleFromHealth ? LandingTier.Stumble : LandingTier.Tumble;
            var depth = Mathf.Clamp(speed / knacks.SafeLandingSpeedMps, 0, 1);
            var amplitude = tier switch
            {
                LandingTier.Squash => _rig.Profile.Squash * depth,
                LandingTier.Water => 0.5f * _rig.Profile.Squash * depth,
                _ => _rig.Profile.Squash,
            };
            if (speed >= MinLandingMps || tier != LandingTier.Squash)
            {
                _landings++;
                LastTier = tier;
                LastLandingSpeedMps = speed;
                LastLandingDamage = damage;
                StartSquash(amplitude, tier == LandingTier.Stumble ? Mathf.Max(_rig.Profile.SettleS, StumbleS) : _rig.Profile.SettleS);
                if (tier == LandingTier.Stumble)
                {
                    _stumbleT = 0;
                    // The hand that goes down: on the side the body is moving toward, else turn and turn about.
                    var sideways = state.Velocity.Dot(new Basis(Vector3.Up, state.Yaw) * Vector3.Right);
                    _stumbleSide = Mathf.Abs(sideways) > 0.05f ? Mathf.Sign(sideways) : (_landings & 1) == 0 ? 1 : -1;
                }
                Landed?.Invoke(new ContactLanding(state.Position, speed, damage, tier));
            }
        }
        // The capsule climbed a step this tick: the body stays where it was for a frame and follows.
        var steps = _controller.StepsClimbed;
        _riseLag *= Mathf.Exp(-dt / RiseLagTau);
        if (steps != _stepsClimbed)
        {
            var rise = state.Position.Y - _lastY;
            // (The capsule is often not on its floor on the tick it steps: it settles onto the tread a tick or two later.)
            if (rise > 0.002f && rise < 0.05f) _riseLag += rise;
            _stepsClimbed = steps;
        }
        if (_riseLag < 1e-5f) _riseLag = 0;
        // Over the top of a climb onto a ledge: the pull-over ends in a little hop's landing.
        if (_wasClimbing && !state.Climbing && state.Grounded) StartSquash(0.6f * _rig.Profile.Squash, _rig.Profile.SettleS);
        _squashT += dt;
        _stumbleT += dt;
        Squash = Mathf.Min(MaxSquash, _squashAmp * SquashEnvelope(_squashT / Mathf.Max(_squashDur, 0.05f)));
        StumbleWeight = StumbleEnvelope(_stumbleT);
    }

    private void StartSquash(float amplitude, float duration)
    {
        _squashAmp = Mathf.Clamp(amplitude, 0, MaxSquash);
        _squashDur = Mathf.Max(duration, 0.12f);
        _squashT = 0;
    }

    /// <summary>The landing's squash over its settle time (0 to 1 of the amplitude): down fast, back up through a small stretch.</summary>
    public static float SquashEnvelope(float u)
    {
        if (u <= 0 || u >= 1) return 0;
        if (u < 0.18f) return Mathf.Sin(Mathf.Pi * 0.5f * u / 0.18f);
        var v = (u - 0.18f) / 0.82f;
        return Mathf.Cos(Mathf.Pi * 1.5f * v) * (1 - v) * (1 - v);
    }

    /// <summary>The stumble's buckle and touch-down over <see cref="StumbleS"/>: down in 0.1 s, held, up by 0.4 s.</summary>
    public static float StumbleEnvelope(float t)
    {
        if (t <= 0 || t >= StumbleS) return 0;
        if (t < 0.1f) return ContactMath.SmoothStep(t / 0.1f);
        if (t < 0.24f) return 1;
        return 1 - ContactMath.SmoothStep((t - 0.24f) / (StumbleS - 0.24f));
    }

    /// <summary>The smoothed weights of wading, swimming, diving, climbing and carrying, and the limbs' phases.</summary>
    private void Modes(in BodyState state, float dt)
    {
        var rate = ContactMath.Rate(dt, ModeTau);
        var wadeTarget = 0f;
        if (!state.Swimming && !state.Climbing && !state.Floating && float.IsFinite(state.WaterSurfaceY))
        {
            var full = _rig.Profile.HasLegs ? Mathf.Max(_rig.Profile.HipHeightM, 0.01f) : 0.3f * Mathf.Max(_rig.Profile.HeightM, 0.05f);
            wadeTarget = Mathf.Clamp((state.WaterSurfaceY - state.Position.Y) / full, 0, 1);
        }
        _wade += (wadeTarget - _wade) * rate;
        _swim += ((state.Swimming ? 1 : 0) - _swim) * rate;
        _dive += ((state.Diving ? 1 : 0) - _dive) * rate;
        _climb += ((state.Climbing ? 1 : 0) - _climb) * rate;
        _carry += ((state.Carrying ? 1 : 0) - _carry) * rate;
        var moved = (float)System.Math.Abs(state.DistanceM - _lastDistance);
        if (!(moved < 0.25f)) moved = 0;
        var speed = float.IsFinite(state.SpeedMps) ? Mathf.Max(state.SpeedMps, 0) : 0;
        var stroking = Mathf.Clamp(speed / 0.1f, 0, 1);
        _strokePhase = Wrap(_strokePhase + dt * StrokeIdleHz * (1 - stroking) + (state.Swimming ? moved / StrokeLengthM : 0));
        if (state.Climbing) _climbPhase = Wrap(_climbPhase + moved / ClimbCycleM);
        var stride = Mathf.Max(_rig.Profile.StrideAt(Mathf.Max(speed, BodyPace.Walk(state))), 0.02f);
        _wadePhase = Wrap(_wadePhase + 2 * moved / stride + dt * 0.4f * (1 - stroking));
    }

    private static float Wrap(float phase) => phase - Mathf.Floor(phase);

    /// <summary>
    /// The root's tilt: a body without legs pitches and rolls to the floor (at most 25°); a climber lies along the face (at most
    /// 35°). The up it tilts to is kept in the world, so turning never has to tilt the body over again.
    /// </summary>
    private void Tilt(in BodyState state, Transform3D toSkeleton, bool grounded, float dt)
    {
        var active = false;
        var target = Vector3.Up;
        if (state.Climbing)
        {
            var normal = ContactMath.Direction(state.ClimbNormal, Vector3.Back);
            target = ContactMath.Direction(Vector3.Up - normal * normal.Dot(Vector3.Up), Vector3.Up);
            active = true;
        }
        else if (grounded && state.FloorNormal.Y > 0.1f)
        {
            target = ContactMath.Direction(state.FloorNormal, Vector3.Up);
            if (!_legless)
            {
                // Legs plant the feet and keep the body upright, but short legs cannot straddle a steep slope across them
                // alone: the body rolls a share of a cross-slope (never pitches), and the legs do the rest.
                var forward = ContactMath.Direction(new Vector3(_skeleton.Basis.Z.X, 0, _skeleton.Basis.Z.Z), Vector3.Back);
                target = ContactMath.Direction(Vector3.Up.Lerp(ContactMath.Direction(target - forward * target.Dot(forward), Vector3.Up), LegRollShare), Vector3.Up);
            }
            active = true;
        }
        var rate = ContactMath.Rate(dt, TiltTau);
        if (active) _tiltUp = _tiltWeight < 0.01f ? target : ContactMath.Direction(_tiltUp.Lerp(target, rate), target);
        _tiltWeight += ((active ? 1 : 0) - _tiltWeight) * rate;
        if (_tiltWeight < 1e-3f && !active) _tiltWeight = 0;
        var limit = Mathf.Lerp(_legless ? MaxRootTiltRad : MaxLegRollRad, MaxClimbLeanRad, _climb);
        var full = ContactMath.Limit(ContactMath.FromTo(Vector3.Up, ContactMath.Direction(toSkeleton.Basis * _tiltUp, Vector3.Up)), limit);
        _tilt = _tiltWeight > 0 ? ContactMath.Part(full, _tiltWeight) : Quaternion.Identity;
    }

    /// <summary>One ray per foot from 3 cm above to 3 cm below where the gait's ground lies under it, plus the step probe.</summary>
    private void FindGround(PoseBuffer pose, in BodyState state, Transform3D skeleton, float dt)
    {
        var rootPosition = pose.GetPosition(CharacterBody.RootBone);
        var rootPlane = new Vector3(rootPosition.X, 0, rootPosition.Z);
        var lowest = float.MaxValue;
        for (var i = 0; i < _feet.Length; i++)
        {
            // Where the gait's sole and the ground it assumed under it lie once the root is tilted (about its joint).
            var plane = rootPlane + _tilt * (new Vector3(_posed[i].X, 0, _posed[i].Z) - rootPlane);
            _tilted[i] = rootPosition + _tilt * (_posed[i] - rootPosition);
            var reference = skeleton * plane;
            var hit = Cast(i, reference + Vector3.Up * FootSearchM, reference + Vector3.Down * FootSearchM, out var point, out var normal) &&
                normal.Y >= MinGroundNormalY;
            if (hit)
            {
                var offset = point.Y - reference.Y + (_feet[i].Leg >= 0 ? Footprint(pose, i, skeleton, normal) : 0);
                _offset[i] = _hit[i] ? ContactMath.Approach(_offset[i], offset, dt, FootTau) : offset;
                _ground[i] = point;
                lowest = Mathf.Min(lowest, _offset[i]);
            }
            else _offset[i] = ContactMath.Approach(_offset[i], 0, dt, FootTau);
            _hit[i] = hit;
        }
        var dropTarget = lowest < float.MaxValue ? Mathf.Clamp(-lowest, 0, _maxDrop) : 0;
        _drop = ContactMath.Approach(_drop, dropTarget, dt, DropTau);
        StepProbe(state, skeleton, dt);
    }

    /// <summary>
    /// A leg's foot stays level under the IK, so on a slope its footprint straddles the ground: the sole is set so the foot's
    /// uphill edge sinks no more than its downhill edge floats (half the slope's rise across the foot each way).
    /// </summary>
    private float Footprint(PoseBuffer pose, int i, Transform3D skeleton, Vector3 normal)
    {
        var foot = _rig.Bones[_feet[i].Bone];
        if (!foot.HasMesh || normal.Y < 0.3f) return 0;
        var transform = pose.ModelTransform(foot.Index);
        var bounds = foot.MeshBounds;
        float low = float.MaxValue, high = float.MinValue;
        for (var c = 0; c < 4; c++)
        {
            var corner = transform * (bounds.Position + new Vector3((c & 1) != 0 ? bounds.Size.X : 0, 0, (c & 2) != 0 ? bounds.Size.Z : 0));
            var across = skeleton.Basis * (corner - _posed[i]);
            var rise = -(normal.X * across.X + normal.Z * across.Z) / normal.Y;
            low = Mathf.Min(low, rise);
            high = Mathf.Max(high, rise);
        }
        return 0.5f * (low + high);
    }

    private void ClearGround(float dt)
    {
        for (var i = 0; i < _feet.Length; i++)
        {
            _hit[i] = false;
            _offset[i] = 0;
        }
        _drop = ContactMath.Approach(_drop, 0, dt, DropTau);
        _stepAhead = ContactMath.Approach(_stepAhead, 0, dt, 0.04f);
        LeadFoot = -1;
    }

    /// <summary>
    /// A step just ahead of the moving capsule: the leading foot (the one farthest along the travel) lifts onto it before the
    /// capsule steps up.
    /// </summary>
    private void StepProbe(in BodyState state, Transform3D skeleton, float dt)
    {
        var travel = new Vector3(state.Velocity.X, 0, state.Velocity.Z);
        var speed = travel.Length();
        var target = 0f;
        LeadFoot = -1;
        if (speed > 0.04f && _feet.Length > 0 && float.IsFinite(speed))
        {
            _stepDir = travel / speed;
            var origin = skeleton.Origin;
            var probe = origin + _stepDir * (_capsuleRadius + StepLookM);
            // A slope is not a step: what the floor's own plane rises over the probe's distance is taken off.
            var floor = state.FloorNormal.Y > 0.5f ? state.FloorNormal : Vector3.Up;
            var slope = -(floor.X * (probe.X - origin.X) + floor.Z * (probe.Z - origin.Z)) / floor.Y;
            if (Cast(_probeRay, probe + Vector3.Up * (slope + MaxStepM + 0.006f), probe + Vector3.Up * (slope - 0.004f), out var point, out var normal) &&
                normal.Y >= Mathf.Cos(Mathf.Pi / 4))
            {
                var rise = point.Y - origin.Y - slope;
                if (rise >= MinStepM && rise <= MaxStepM) target = rise;
            }
            var best = float.MinValue;
            var parity = (int)((long)System.Math.Floor(state.DistanceM / Mathf.Max(0.5f * _rig.Profile.WalkStrideM, 0.01f)) & 1);
            for (var i = 0; i < _feet.Length; i++)
            {
                var along = (skeleton * _posed[i] - origin).Dot(_stepDir);
                if (LeadFoot < 0 || along > best + 0.003f)
                {
                    best = along;
                    LeadFoot = i;
                }
                // Feet side by side: turn and turn about.
                else if (along >= best - 0.003f && (parity == 0 ? _feet[i].Side > 0 : _feet[i].Side < 0))
                {
                    best = Mathf.Max(best, along);
                    LeadFoot = i;
                }
            }
        }
        _stepAhead = target > _stepAhead ? ContactMath.Approach(_stepAhead, target, dt, 0.02f) : ContactMath.Approach(_stepAhead, target, dt, 0.04f);
        if (_stepAhead < 1e-4f) _stepAhead = 0;
    }

    /// <summary>
    /// A climber's body front meets the face: one ray from the middle of the body to the wall. The middle is taken where the lean along
    /// the face puts it (Tilt, turned about the root's joint by ApplyRoot), and on a sloped face, where the body lies along it and its
    /// limbs reach into it, no drawn part is left behind the face: the press stops short, or backs off, where the part nearest the face
    /// (as last drawn) would be FaceClearM in front of it. On a cliff, with no lean, the body's front meets the face as before. (Mark 1
    /// of the first recorded play, 16 October: the press measured from the upright middle and the lean added up, and on a 52 degree
    /// rock face Stickbear was drawn 3 cm inside it while the capsule held 4 cm in front.)
    /// </summary>
    private void Press(in BodyState state, Transform3D skeleton, Transform3D toSkeleton, float dt)
    {
        var target = 0f;
        if (state.Climbing && _rig.Bones[_rig.BodyBone].HasMesh)
        {
            var normal = ContactMath.Direction(state.ClimbNormal, Vector3.Back);
            var body = _rig.Bones[_rig.BodyBone];
            var joint = _rig.Bones[CharacterBody.RootBone].RestModel;
            var upright = new Vector3(0, body.RestModel.Y + body.MeshBounds.GetCenter().Y, 0);
            var middle = skeleton * (joint + _tilt * (upright - joint));
            if (Cast(_wallRay, middle, middle - normal * 0.08f, out var point, out var face))
            {
                // The body's front at rest, measured from the capsule's axis (forward is -Z).
                var front = -(body.RestModel.Z + body.MeshBounds.Position.Z);
                var gap = (middle - point).Length();
                target = Mathf.Clamp(gap - front - 0.001f, -0.02f, 0.02f);
                // How far the body lies along a sloped face: none on a cliff, all of it from a 70 degree slope down.
                var sloped = Mathf.Clamp(ContactMath.Angle(_tilt) / SlopedLeanRad, 0, 1);
                if (sloped > 0 && ContactMath.Finite(face))
                {
                    // Every drawn part in front: the press that leaves the nearest one FaceClearM off the face (the last drawn pose
                    // already carries the last press, so the change is the nearest part's distance less the clearance). Where two faces
                    // meet at a seam the hands reach the one above: the face under the leaned top counts too.
                    var nearest = NearestToFace(point, face);
                    var top = skeleton * (joint + _tilt * (new Vector3(0, _rig.RestBounds.End.Y * 0.85f, 0) - joint));
                    if (Cast(_wallRay, top, top - normal * 0.1f, out var upper, out var upperFace) && upperFace.Dot(face) < 0.9999f)
                        nearest = Mathf.Min(nearest, NearestToFace(upper, upperFace));
                    var clear = Mathf.Max(_press + nearest - FaceClearM, -MaxPressOutM);
                    if (clear < target) target = Mathf.Lerp(target, clear, sloped);
                }
            }
            _pressDir = ContactMath.Direction(toSkeleton.Basis * -normal, Vector3.Forward);
        }
        _press = ContactMath.Approach(_press, target, dt, PressTau);
        if (Mathf.Abs(_press) < 1e-5f) _press = 0;
    }

    /// <summary>A lean along the face of this much (a 70 degree slope's) or more keeps every drawn part in front of it.</summary>
    private static readonly float SlopedLeanRad = Mathf.DegToRad(20);
    /// <summary>The nearest drawn part stops this far in front of a sloped face.</summary>
    private const float FaceClearM = 0.002f;
    /// <summary>The most the press backs a climber off a sloped face.</summary>
    private const float MaxPressOutM = 0.04f;

    /// <summary>
    /// The signed distance to the face (a point on it and its outward normal, world) of the drawn body's corner nearest it: every meshed
    /// bone's bounds at its last drawn transform, carried with the body to where it is now. Negative: something is drawn behind the face.
    /// </summary>
    private float NearestToFace(Vector3 point, Vector3 face)
    {
        var toWorld = _rig.GlobalTransform;
        var nearest = float.MaxValue;
        var bones = _rig.Bones;
        for (var i = 0; i < bones.Count; i++)
        {
            var bone = bones[i];
            if (!bone.HasMesh) continue;
            var t = toWorld * _rig.FinalBoneTransform(i);
            var b = bone.MeshBounds;
            for (var c = 0; c < 8; c++)
            {
                var corner = b.Position + new Vector3((c & 1) != 0 ? b.Size.X : 0, (c & 2) != 0 ? b.Size.Y : 0, (c & 4) != 0 ? b.Size.Z : 0);
                nearest = Mathf.Min(nearest, (t * corner - point).Dot(face));
            }
        }
        return nearest == float.MaxValue ? 0 : nearest;
    }

    /// <summary>
    /// The root: the tilt to the floor or the face (about the root's joint, on the ground), a stumble's lurch, the pelvis drop and
    /// the step's lag (world down), the climber's press to the face, and the squash (volume kept, about the feet).
    /// </summary>
    private void ApplyRoot(PoseBuffer pose, Vector3 upS)
    {
        const int root = CharacterBody.RootBone;
        var stumble = StumbleWeight;
        var shift = Vector3.Zero;
        if (stumble > 0 && _legless)
        {
            // No knees to buckle: the body lurches forward over its toes.
            var position = pose.GetPosition(root);
            var pivot = new Vector3(position.X, position.Y, position.Z + _toeZ);
            var lurch = ContactMath.AxisAngle(Vector3.Right, -LurchRad * stumble);
            pose.AddRotation(root, lurch);
            shift += pivot + lurch * (position - pivot) - position;
        }
        if (ContactMath.Angle(_tilt) > 1e-5f) pose.AddRotation(root, _tilt);
        var buckle = !_legless ? StumbleBuckle * _rig.Profile.HipHeightM * stumble : 0;
        shift -= upS * (_drop + _riseLag + buckle);
        shift += _pressDir * _press;
        if (shift.LengthSquared() > 1e-14f) pose.AddPosition(root, shift);
        var squash = Squash;
        if (stumble > 0) squash = Mathf.Max(squash, Mathf.Min(MaxSquash, 1.2f * _rig.Profile.Squash) * stumble);
        // A load overhead weighs a little; a body with no hands carries it on its head.
        squash = Mathf.Max(squash, _carry * (_hasHands ? 0.015f : 0.04f));
        Squash = squash;
        if (squash > 1e-5f)
        {
            // The gait may already squash or stretch the root: together they stay within MaxSquash of rest (springs misbehave
            // under larger scales; K3's note at the merge, 13 October).
            var current = pose.GetScale(root).Y;
            var y = current > 1e-4f ? Mathf.Max(current * (1 - squash), 1 - MaxSquash) / current : 1 - squash;
            if (y < 1)
            {
                var xz = 1 / Mathf.Sqrt(y);
                pose.MultiplyScale(root, new Vector3(xz, y, xz));
            }
        }
    }

    /// <summary>Feet onto the ground: the leg IK's targets (world), or a footed body's feet lifted to meet it.</summary>
    private void PlaceFeet(PoseBuffer pose, in BodyState state, Transform3D skeleton, Transform3D toSkeleton, Vector3 upS, bool grounded)
    {
        for (var i = 0; i < _feet.Length; i++)
        {
            var foot = _feet[i];
            var lead = grounded && i == LeadFoot && _stepAhead > 0;
            var offset = _hit[i] ? _offset[i] : 0;
            if (lead) offset = Mathf.Max(offset, _stepAhead);
            var reach = lead ? _stepDir * (StepReachM * Mathf.Clamp(_stepAhead / MaxStepM, 0, 1)) : Vector3.Zero;
            if (foot.Leg >= 0)
            {
                // Planted: the gait's sole moved by the ground's offset, so a swing foot keeps its lift over the real ground.
                // No ground within reach (or in the air): the sole stays where the pose puts it.
                _target[i] = grounded && (_hit[i] || lead)
                    ? skeleton * _tilted[i] + Vector3.Up * offset + reach
                    : skeleton * (pose.ModelTransform(foot.Bone) * foot.Sole);
                _lift[i] = 0;
                continue;
            }
            var lift = grounded && (_hit[i] || lead) ? Mathf.Clamp(offset + _drop + _riseLag, 0, MaxFootLiftM) : 0;
            _lift[i] = lift;
            if (lift <= 1e-5f && !lead) continue;
            var parent = SafeInverse(pose.ModelTransform(foot.Parent).Basis);
            var move = upS * lift + toSkeleton.Basis * reach;
            pose.AddPosition(foot.Bone, parent * move);
        }
    }
}
