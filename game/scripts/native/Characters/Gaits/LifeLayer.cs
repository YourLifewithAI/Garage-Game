using System;
using System.Collections.Generic;
using Godot;

namespace GarageGame.Native.Characters.Gaits;

/// <summary>
/// Layer 3 of the pose stack, K3's part of it (the look targets are K4's): what keeps a character alive when it is doing
/// nothing. Breathing (about ±1% on the body over 3.5 s, quicker for a while after a run); blinks every 2 to 6 s on the
/// eye bones (lids when there are no eyes), with now and then a double blink, timed from a seed per body so the same
/// frames blink the same way in every run; and the effect parts' own motion by their hints: twinkle (a scale pulse),
/// orbit and drift (paths added to the sparkle's position, wandering farther and trailing behind when the body moves
/// fast: springs only tilt a sparkle), flicker (the flame) and pulse (the aura). Climbing, swimming and floating bodies
/// keep breathing and blinking. Allocation-free per frame.
/// </summary>
public sealed class LifeLayer : IPoseLayer<BodyState>
{
    public const float BreathPeriodS = 3.5f, BreathAmount = 0.01f;
    public const float BlinkMinS = 2, BlinkMaxS = 6, BlinkS = 0.12f;
    /// <summary>How flat a closed eye is (scale in Y; never 0, which the pose refuses).</summary>
    public const float ClosedEye = 0.1f;

    private readonly struct Effect
    {
        public readonly int Bone;
        public readonly EffectMotion Motion;
        /// <summary>The effect's own offset, in cycles.</summary>
        public readonly float Phase;
        public readonly float Rate;
        public readonly float Reach;

        public Effect(int bone, EffectMotion motion, float phase, float rate, float reach)
        {
            Bone = bone;
            Motion = motion;
            Phase = phase;
            Rate = rate;
            Reach = reach;
        }
    }

    private readonly GaitRig _rig;
    private readonly int _body;
    private readonly int[] _blinkers;
    private readonly Effect[] _effects;
    private readonly uint _seed;
    private GaitRandom _random;
    private bool _started;
    private double _lastTime;
    private double _breath;
    private double _nextBlink;
    private double _blinkStart = double.NegativeInfinity;
    private double _secondBlink = double.NaN;
    private CriticalSpring _exertion, _trailX, _trailZ, _speed;

    public LifeLayer(CharacterBody body, uint seed) : this(new GaitRig(body), seed) { }

    public LifeLayer(GaitRig rig, uint seed)
    {
        _rig = rig ?? throw new ArgumentNullException(nameof(rig));
        _seed = seed == 0 ? 0x9E3779B9u : seed;
        _random = new GaitRandom(_seed);
        _body = rig.Free(rig.BodyBone) ? rig.BodyBone : -1;
        var eyes = rig.All(PartRole.Eye);
        _blinkers = eyes.Length > 0 ? eyes : rig.All(PartRole.Lid);
        var effects = new List<Effect>();
        var bones = rig.Body.Bones;
        for (var i = 1; i < bones.Count; i++)
        {
            var bone = bones[i];
            if (bone.Effect == EffectMotion.None || !rig.Free(i)) continue;
            var size = bone.HasMesh ? bone.MeshBounds.Size : new Vector3(0.004f, 0.004f, 0.004f);
            var reach = Mathf.Clamp(0.6f * Mathf.Max(size.X, Mathf.Max(size.Y, size.Z)), 0.002f, 0.004f);
            effects.Add(new Effect(i, bone.Effect, GaitMath.Hash01(_seed, i), 0.75f + 0.5f * GaitMath.Hash01(_seed, i + 977), reach));
        }
        _effects = effects.ToArray();
        _nextBlink = NextInterval();
    }

    public PoseLayer Layer => PoseLayer.Life;
    public uint Seed => _seed;
    /// <summary>Blinks begun so far, and when the last one began (clock seconds).</summary>
    public int Blinks { get; private set; }
    public double LastBlinkS { get; private set; } = double.NaN;
    /// <summary>When the next blink begins (clock seconds).</summary>
    public double NextBlinkS => _nextBlink;
    /// <summary>The bones that close in a blink.</summary>
    public IReadOnlyList<int> Blinkers => _blinkers;
    /// <summary>The breath's phase in cycles (its fraction), and how much the body breathes now (a scale above or below 1).</summary>
    public double BreathCycles => _breath;
    public float BreathScale { get; private set; } = 1;

    public void Apply(PoseBuffer pose, in BodyState state, in PoseClock clock)
    {
        var time = double.IsFinite(clock.TimeS) ? clock.TimeS : _lastTime;
        var dt = float.IsFinite(clock.DeltaS) && clock.DeltaS > 0 ? Mathf.Clamp(clock.DeltaS, 1f / 480, 0.1f) : 1f / 60;
        if (!_started || time < _lastTime)
        {
            _started = true;
            _nextBlink = time + NextInterval();
            _blinkStart = double.NegativeInfinity;
            _secondBlink = double.NaN;
        }
        _lastTime = time;
        var speed = float.IsFinite(state.SpeedMps) ? Mathf.Clamp(state.SpeedMps, 0, 5) : 0;
        _speed.Step(speed, 6, dt);
        var run = BodyPace.Blend(state, _speed.Value);
        // Exertion builds over a few seconds of running and fades as slowly: the breath quickens with it.
        _exertion.Step(run, run > _exertion.Value ? 0.8f : 0.35f, dt);

        _breath += dt * (1 + 0.7f * Mathf.Clamp(_exertion.Value, 0, 1)) / BreathPeriodS;
        if (_breath > 1 << 20) _breath -= 1 << 20;
        BreathScale = 1 + BreathAmount * GaitMath.Wave(_breath);
        if (_body >= 0) pose.MultiplyScale(_body, new Vector3(BreathScale, BreathScale, BreathScale));

        Blink(pose, time);
        Effects(pose, state, time, dt);
    }

    private float NextInterval() => BlinkMinS + (BlinkMaxS - BlinkMinS) * _random.Next();

    private void Blink(PoseBuffer pose, double time)
    {
        if (time >= _nextBlink)
        {
            _blinkStart = _nextBlink;
            Blinks++;
            LastBlinkS = _blinkStart;
            // Now and then a double blink, a beat after the first.
            _secondBlink = _random.Next() < 0.15f ? _blinkStart + 0.22 : double.NaN;
            _nextBlink = _blinkStart + NextInterval();
            if (_nextBlink <= time) _nextBlink = time + NextInterval();
        }
        else if (!double.IsNaN(_secondBlink) && time >= _secondBlink)
        {
            _blinkStart = _secondBlink;
            _secondBlink = double.NaN;
        }
        if (_blinkers.Length == 0) return;
        var t = (float)(time - _blinkStart);
        if (!(t >= 0 && t < BlinkS)) return;
        // Shut quickly, hold a moment, open a little slower.
        float open;
        if (t < 0.04f) open = 1 - GaitMath.Smoothstep(0, 0.04f, t);
        else if (t < 0.06f) open = 0;
        else open = GaitMath.Smoothstep(0.06f, BlinkS, t);
        var y = Mathf.Lerp(ClosedEye, 1, open);
        foreach (var bone in _blinkers) pose.MultiplyScale(bone, new Vector3(1, y, 1));
    }

    private void Effects(PoseBuffer pose, in BodyState state, double time, float dt)
    {
        if (_effects.Length == 0) return;
        // The body's velocity in its own frame, followed slowly: sparkles trail behind it and catch up after a stop.
        var velocity = state.Velocity;
        var yaw = float.IsFinite(state.Yaw) ? state.Yaw : 0;
        float localX = 0, localZ = 0;
        if (float.IsFinite(velocity.X) && float.IsFinite(velocity.Z))
        {
            var cos = MathF.Cos(yaw);
            var sin = MathF.Sin(yaw);
            localX = Mathf.Clamp(cos * velocity.X - sin * velocity.Z, -3, 3);
            localZ = Mathf.Clamp(sin * velocity.X + cos * velocity.Z, -3, 3);
        }
        _trailX.Step(localX, 5, dt);
        _trailZ.Step(localZ, 5, dt);
        var drift = _rig.Profile.Multipliers.Drift;
        var fast = BodyPace.OfRun(state, _speed.Value);
        var trail = new Vector3(-_trailX.Value, 0, -_trailZ.Value) * (0.015f * drift);
        trail = trail.LimitLength(0.02f * drift);
        foreach (var effect in _effects)
        {
            // Every wave is taken in cycles (double, then the fraction), so long play keeps its precision and no wave jumps.
            var rate = effect.Rate;
            var phase = effect.Phase;
            switch (effect.Motion)
            {
                case EffectMotion.Twinkle:
                {
                    var spark = MathF.Pow(0.5f + 0.5f * GaitMath.Wave(time * 1.2 * rate + phase), 4);
                    var s = 0.82f + 0.4f * spark;
                    pose.MultiplyScale(effect.Bone, new Vector3(s, s, s));
                    break;
                }
                case EffectMotion.Orbit:
                {
                    var reach = effect.Reach * drift * (1 + 1.2f * fast);
                    var turn = time * 0.35 * rate + phase;
                    var offset = reach * new Vector3(GaitMath.Wave(turn + 0.25), 0.35f * GaitMath.Wave(2 * turn), GaitMath.Wave(turn));
                    pose.AddPosition(effect.Bone, offset + trail);
                    break;
                }
                case EffectMotion.Drift:
                {
                    var reach = effect.Reach * drift * (1 + 1.5f * fast);
                    var offset = reach * new Vector3(GaitMath.Wave(time * 0.31 * rate + phase), 0.6f * GaitMath.Wave(time * 0.42 * rate + phase + 0.21),
                        0.5f * GaitMath.Wave(time * 0.24 * rate + phase + 0.33));
                    pose.AddPosition(effect.Bone, offset + 1.3f * trail);
                    break;
                }
                case EffectMotion.Flicker:
                {
                    var n = 0.55f * GaitMath.Wave(time * 7.3 + phase) + 0.3f * GaitMath.Wave(time * 11.9 + 0.2) + 0.15f * GaitMath.Wave(time * 17.1 + 0.07);
                    var y = 1 + 0.14f * n * (1 + 0.5f * fast);
                    var side = 1 - 0.07f * n;
                    pose.MultiplyScale(effect.Bone, new Vector3(side, y, side));
                    break;
                }
                case EffectMotion.Pulse:
                {
                    var s = 1 + 0.05f * GaitMath.Wave(time * rate / 2.4 + phase);
                    pose.MultiplyScale(effect.Bone, new Vector3(s, s, s));
                    break;
                }
            }
        }
    }
}
