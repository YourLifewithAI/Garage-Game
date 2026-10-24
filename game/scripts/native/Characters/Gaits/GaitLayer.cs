using System;
using Godot;

namespace GarageGame.Native.Characters.Gaits;

/// <summary>
/// Layer 1 of the pose stack (docs/runs/RUN-2-CHARACTERS.md, "The gaits"): the base pose from what the capsule did. The
/// gait is chosen by the rig's motion profile (float, hop, waddle, stride); its phase is advanced by the distance the
/// body travelled (<see cref="BodyState.DistanceM"/> over <see cref="MotionProfile.StrideAt"/>), never by time, and only
/// on the ground, so standing still never steps and a slow body never moonwalks. The root leans into acceleration and
/// banks into turns through a critically damped spring on the body's velocity and another on the lean (starts, stops
/// and turns get their anticipation and follow-through from them), about the footprint's edge (a float: its middle),
/// and squashes and stretches with volume kept, at most 15% (springs misbehave under larger scales). While the body
/// climbs or swims the gait yields to the contact layer's poses. It also tells a later sound packet about footfalls,
/// landings and splashes. Allocation-free per frame; a pure function of the states and clocks it was fed.
/// Held by a bubble (<see cref="BodyState.InBubble"/>, a carry or a lift, decision 43) the body floats whatever its own gait is: the
/// layer stops stepping and falling and poses the float gait instead, blended in and out by a spring, so the drawn body hangs and
/// bobs inside the bubble that holds it and leans softly into the bubble's drift.
/// </summary>
public sealed class GaitLayer : IPoseLayer<BodyState>
{
    /// <summary>The largest squash or stretch the gaits put on the root (the plan's 15%).</summary>
    public const float MaxSquash = 0.15f;
    /// <summary>The largest lean or bank (radians, about 15°).</summary>
    public const float MaxLeanRad = 0.26f;
    /// <summary>Seconds off the ground before the body counts as in the air (a step down is not a jump).</summary>
    public const float AirDelayS = 0.1f;
    /// <summary>More than this in one tick is a jump (a wrap, a trip home), not travel; the controller drops it too.</summary>
    public const float MaxTickTravelM = 0.25f;

    private readonly GaitRig _rig;
    private readonly IGait _gait;
    /// <summary>The float a body takes inside a bubble: null when its own gait is the float already.</summary>
    private readonly FloatGait? _bubbleGait;
    private bool _started;
    private double _lastDistance;
    private bool _wasSwimming;
    private double _cycles;
    private CriticalSpring _speed, _move, _run, _air, _active, _vx, _vz, _pitch, _roll, _bubble;

    public GaitLayer(CharacterBody body) : this(new GaitRig(body)) { }

    /// <summary>The gait the rig's motion profile names, or <paramref name="gait"/> when one is given (any gait runs on any drawing).</summary>
    public GaitLayer(GaitRig rig, IGait? gait = null)
    {
        _rig = rig ?? throw new ArgumentNullException(nameof(rig));
        _gait = gait ?? rig.Profile.Gait switch
        {
            MotionKind.Float => new FloatGait(rig),
            MotionKind.Hop => new HopGait(rig),
            MotionKind.Stride when rig.Leg[GaitRig.Right] >= 0 || rig.Leg[GaitRig.Left] >= 0 => new StrideGait(rig),
            _ => new WaddleGait(rig),
        };
        if (_gait.Kind != MotionKind.Float) _bubbleGait = new FloatGait(rig);
        _active.Reset(1);
    }

    public PoseLayer Layer => PoseLayer.Gait;
    public GaitRig Rig => _rig;
    public IGait Gait => _gait;
    /// <summary>Stride cycles travelled (the phase is its fraction).</summary>
    public double Cycles => _cycles;
    /// <summary>This tick's frame, as the gait saw it.</summary>
    public GaitFrame Frame { get; private set; }
    /// <summary>How far the body floats inside a bubble now: 0 on its own gait to 1 held (a spring, so it blends in and out).</summary>
    public float BubbleWeight => Mathf.Clamp(_bubble.Value, 0, 1);
    /// <summary>The root's lean now: forward and toward the right, radians.</summary>
    public float LeanPitchRad => _pitch.Value;
    public float LeanRollRad => _roll.Value;
    /// <summary>Counts of the events so far (tests; the sound packet listens to the events).</summary>
    public int Footfalls { get; private set; }
    public int Landings { get; private set; }
    public int Splashes { get; private set; }

    /// <summary>A foot came down in the gait's rhythm (for a hop, both: FootBone -1).</summary>
    public event GaitEventHandler? Footfall;
    /// <summary>The body landed on a floor from the air, with the landing speed fall damage reads.</summary>
    public event GaitEventHandler? Landing;
    /// <summary>The body landed in deep water or began to swim.</summary>
    public event GaitEventHandler? Splash;

    public void Apply(PoseBuffer pose, in BodyState state, in PoseClock clock)
    {
        var dt = clock.DeltaS;
        dt = float.IsFinite(dt) && dt > 0 ? Mathf.Clamp(dt, 1f / 480, 0.1f) : 1f / 60;
        var profile = _rig.Profile;
        var settle = Mathf.Max(profile.SettleS, 0.05f);
        var distance = double.IsFinite(state.DistanceM) ? state.DistanceM : _lastDistance;
        var velocity = state.Velocity;
        if (!float.IsFinite(velocity.X) || !float.IsFinite(velocity.Y) || !float.IsFinite(velocity.Z)) velocity = Vector3.Zero;
        var flat = new Vector2(velocity.X, velocity.Z).LimitLength(4);
        if (!_started)
        {
            _started = true;
            _lastDistance = distance;
            _vx.Reset(flat.X);
            _vz.Reset(flat.Y);
            _wasSwimming = state.Swimming;
        }

        var moved = (float)(distance - _lastDistance);
        if (!(moved > 0) || moved > MaxTickTravelM) moved = 0;
        _lastDistance = distance;
        var raw = float.IsFinite(state.SpeedMps) ? Mathf.Clamp(state.SpeedMps, 0, 5) : 0;
        _speed.Step(raw, 25, dt);
        var speed = Mathf.Max(_speed.Value, 0);
        var stride = Mathf.Max(profile.StrideAt(speed), 1e-3f);

        var yields = state.Climbing || state.Swimming;
        // Held by a bubble the body is carried, not walking or falling: no steps, no airborne squash; the float gait is posed instead.
        var held = state.InBubble && !yields;
        var airborne = !yields && !held && !state.Floating && !state.Grounded && state.AirTimeS > AirDelayS && _gait.Kind != MotionKind.Float;
        var before = _cycles;
        if (!yields && !airborne && !held && moved > 0) _cycles += moved / stride;

        var omega = 5 / settle;
        _bubble.Step(held ? 1 : 0, 12, dt);
        _move.Step(yields || held ? 0 : GaitMath.Smoothstep(0.02f, 0.1f, speed), omega, dt);
        // The walk-to-run blend is against this body's own walk and run (BodyPace): a Quick feet body walks at its full pace.
        _run.Step(held ? 0 : BodyPace.Blend(state, speed), omega, dt);
        _air.Step(airborne ? 1 : 0, 18, dt);
        _active.Step(yields ? 0 : 1, 10, dt);

        // The lean: the body's velocity followed by a critically damped spring; the lag between them leans the body
        // into a start, back at a stop and into a turn, then a second spring smooths the lean itself.
        _vx.Step(flat.X, 4 / settle, dt);
        _vz.Step(flat.Y, 4 / settle, dt);
        var lag = flat - new Vector2(_vx.Value, _vz.Value);
        var yaw = float.IsFinite(state.Yaw) ? state.Yaw : 0;
        var forward = new Vector2(-MathF.Sin(yaw), -MathF.Cos(yaw));
        var right = new Vector2(MathF.Cos(yaw), -MathF.Sin(yaw));
        var gain = 0.55f * profile.Multipliers.Lean * profile.Bounce;
        var reach = Mathf.Min(0.21f * profile.Multipliers.Lean, MaxLeanRad);
        var active = _active.Value * (1 - 0.7f * _air.Value);
        var pitchTarget = active * (Mathf.Clamp(gain * lag.Dot(forward), -reach, reach) + _run.Value * _move.Value * _gait.RunLeanRad);
        var rollTarget = active * Mathf.Clamp(gain * lag.Dot(right), -reach, reach);
        // A float's lean is soft and wide: slower to come and to go.
        var leanOmega = (_gait.LeansAboutCentre || held ? 3 : 6) / settle;
        _pitch.Step(pitchTarget, leanOmega, dt);
        _roll.Step(rollTarget, leanOmega, dt);

        Emit(state, before, speed);

        // Inside a bubble its own gait fades out as the float fades in (the weights add to the gait's own Active).
        var inside = _bubbleGait == null ? 0f : BubbleWeight;
        var frame = new GaitFrame
        {
            Cycles = _cycles, SpeedMps = speed, StrideM = stride, WalkMps = BodyPace.Walk(state), RunMps = BodyPace.Run(state),
            Move = Mathf.Clamp(_move.Value, 0, 1), Run = Mathf.Clamp(_run.Value, 0, 1),
            Air = Mathf.Clamp(_air.Value, 0, 1), Active = Mathf.Clamp(_active.Value, 0, 1) * (1 - inside),
            VerticalMps = float.IsFinite(velocity.Y) ? velocity.Y : 0, TimeS = clock.TimeS, DeltaS = dt,
        };
        Frame = frame;
        // A layer with nothing to say leaves the pose alone: a body that climbs or swims, settled.
        if (frame.Active < 1e-3f && inside < 1e-3f && MathF.Abs(_pitch.Value) < 1e-5f && MathF.Abs(_roll.Value) < 1e-5f) return;

        var root = new RootMotion { SquashY = 1 };
        if (inside < 0.999f) _gait.Pose(pose, frame, ref root);
        if (inside > 1e-3f)
        {
            // The float is posed from still: no travel, no air, as much of it as the bubble holds the body (and not while it climbs or swims).
            var floating = frame;
            floating.Active = Mathf.Clamp(_active.Value, 0, 1) * inside;
            floating.Move = 0;
            floating.Air = 0;
            _bubbleGait!.Pose(pose, floating, ref root);
        }
        ApplyRoot(pose, root, inside > 0.5f ? _bubbleGait! : _gait);
    }

    /// <summary>Composes the gait's root motion with the lean: squash about the ground, roll and pitch about the pivot, yaw, then the shift.</summary>
    private void ApplyRoot(PoseBuffer pose, in RootMotion root, IGait pivotGait)
    {
        var pitch = Mathf.Clamp(root.Pitch + _pitch.Value, -MaxLeanRad, MaxLeanRad);
        var roll = Mathf.Clamp(root.Roll + _roll.Value, -MaxLeanRad, MaxLeanRad);
        var squash = float.IsFinite(root.SquashY) ? Mathf.Clamp(root.SquashY, 1 - MaxSquash, 1 + MaxSquash) : 1;
        Vector3 pivot;
        if (pivotGait.LeansAboutCentre) pivot = _rig.BodyCentre;
        else pivot = new Vector3(roll > 0 ? _rig.FootprintRight : roll < 0 ? _rig.FootprintLeft : 0, 0, pitch > 0 ? _rig.FootprintFront : pitch < 0 ? _rig.FootprintBack : 0);
        var tilt = GaitMath.Roll(roll) * GaitMath.Pitch(pitch);
        var turn = GaitMath.Yaw(root.Yaw);
        var rotation = turn * tilt;
        var offset = turn * (pivot - tilt * pivot) + root.Offset;
        if (pivotGait.LeansAboutCentre)
        {
            // A body leaning about its middle rises as far as its footprint's lowest corner would dip: it never sinks.
            var dip = 0f;
            for (var corner = 0; corner < 4; corner++)
            {
                var point = new Vector3((corner & 1) != 0 ? _rig.FootprintRight : _rig.FootprintLeft, 0, (corner & 2) != 0 ? _rig.FootprintBack : _rig.FootprintFront);
                dip = Mathf.Min(dip, (pivot + tilt * (point - pivot)).Y);
            }
            offset.Y -= dip;
        }
        const int rootBone = CharacterBody.RootBone;
        if (squash != 1) pose.MultiplyScale(rootBone, GaitMath.SquashScale(squash));
        if (rotation != Quaternion.Identity) pose.AddRotation(rootBone, rotation);
        if (offset != Vector3.Zero) pose.AddPosition(rootBone, offset);
    }

    private void Emit(in BodyState state, double before, float speed)
    {
        var wet = state.WaterDepthM > 0;
        var steps = _gait.StepsPerCycle;
        if (steps > 0 && _move.Value > 0.3f)
        {
            var from = (long)Math.Floor(before * steps);
            var to = (long)Math.Floor(_cycles * steps);
            // At most one footfall a tick (a slow frame does not machine-gun the sound).
            if (to > from)
            {
                var foot = _gait.FootForStep((int)(to % steps));
                var side = foot >= 0 ? _rig.Body.Bones[foot].Side : BodySide.Center;
                Footfalls++;
                Footfall?.Invoke(new GaitEvent(GaitEventKind.Footfall, foot, side, speed, state.Position, wet, state.Tick));
            }
        }
        var splashed = false;
        if (state.Landed)
        {
            var landing = new GaitEvent(state.LandedInDeepWater ? GaitEventKind.Splash : GaitEventKind.Landing, -1, BodySide.Center,
                float.IsFinite(state.LandingSpeedMps) ? state.LandingSpeedMps : 0, state.Position, wet || state.LandedInDeepWater, state.Tick);
            if (state.LandedInDeepWater)
            {
                splashed = true;
                Splashes++;
                Splash?.Invoke(landing);
            }
            else
            {
                Landings++;
                Landing?.Invoke(landing);
            }
        }
        if (state.Swimming && !_wasSwimming && !splashed)
        {
            Splashes++;
            Splash?.Invoke(new GaitEvent(GaitEventKind.Splash, -1, BodySide.Center, MathF.Max(speed, MathF.Abs(state.Velocity.Y)), state.Position, true, state.Tick));
        }
        _wasSwimming = state.Swimming;
    }
}
