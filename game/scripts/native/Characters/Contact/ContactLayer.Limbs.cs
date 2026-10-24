using Godot;
using GarageGame.Native.Characters.Gaits;

namespace GarageGame.Native.Characters.Contact;

/// <summary>The limbs (water, climbing, carrying, the stumble's touch-down), the leg IK's switch and the springs' wind.</summary>
public sealed partial class ContactLayer
{
    private void Limbs(PoseBuffer pose, in BodyState state, Transform3D toSkeleton, float dt)
    {
        var stumble = StumbleWeight;
        var velocity = toSkeleton.Basis * state.Velocity;
        if (!ContactMath.Finite(velocity)) velocity = Vector3.Zero;
        var flat = new Vector3(state.Velocity.X, 0, state.Velocity.Z).Length();
        var speed = velocity.Length();

        if (_swim > 1e-3f || _carry > 1e-3f || _climb > 1e-3f || stumble > 0)
        {
            var reacher = StumbleArm();
            for (var a = 0; a < _arms.Length; a++)
            {
                var arm = _arms[a];
                var rotation = pose.GetRotation(arm.Bone);
                var phase = arm.Side > 0 ? 0f : 0.5f;
                if (_swim > 1e-3f)
                {
                    // A paddle: the hand circles from in front (+Y lying in the water) down under the body (-Z) and back.
                    var theta = Mathf.DegToRad(90) + Mathf.DegToRad(55) * Mathf.Sin(Mathf.Tau * (_strokePhase + phase));
                    var paddle = ContactMath.Direction(new Vector3(arm.Side * 0.3f, Mathf.Cos(theta), -Mathf.Sin(theta)), arm.Rest);
                    rotation = ContactMath.Blend(rotation, ContactMath.FromTo(arm.Rest, paddle), _swim);
                }
                if (_carry > 1e-3f)
                {
                    // Up to the load over the head, as far as the arm reaches.
                    var joint = pose.ModelTransform(arm.Bone).Origin;
                    var top = new Vector3(arm.Side * Mathf.Min(Mathf.Abs(joint.X) * 0.3f, 0.008f), _rig.Profile.HeightM + 0.005f, joint.Z * 0.5f);
                    var up = ContactMath.Direction(SafeInverse(pose.ModelTransform(arm.Parent).Basis) * (top - joint), Vector3.Up);
                    rotation = ContactMath.Blend(rotation, ContactMath.FromTo(arm.Rest, up), _carry);
                }
                if (_climb > 1e-3f)
                {
                    // Hand over hand: each arm reaches up the face in turn as the body climbs.
                    var elevation = Mathf.DegToRad(72) + Mathf.DegToRad(25) * Mathf.Sin(Mathf.Tau * (_climbPhase + phase));
                    var reach = ContactMath.Direction(new Vector3(arm.Side * Mathf.Cos(elevation) * 0.8f, Mathf.Sin(elevation), -0.35f), Vector3.Up);
                    rotation = ContactMath.Blend(rotation, ContactMath.FromTo(arm.Rest, reach), _climb);
                }
                if (stumble > 0 && a == reacher)
                {
                    // A hand goes down to the ground in front, to the side it falls toward.
                    var down = ContactMath.Direction(new Vector3(arm.Side * 0.35f, -1, -0.6f), Vector3.Down);
                    var local = ContactMath.Direction(SafeInverse(pose.ModelTransform(arm.Parent).Basis) * down, Vector3.Down);
                    rotation = ContactMath.Blend(rotation, ContactMath.FromTo(arm.Rest, local), stumble);
                }
                pose.SetRotation(arm.Bone, rotation);
            }
        }
        if (_carry > 1e-3f)
            foreach (var hand in _hands) pose.AddRotation(hand, ContactMath.AxisAngle(Vector3.Right, CurlRad * _carry));

        if (_wade > 1e-3f || _swim > 1e-3f || _climb > 1e-3f)
        {
            foreach (var leg in _legs)
            {
                var footOnly = leg.Hand == -2;
                var share = footOnly ? 0.6f : 1f;
                var rotation = pose.GetRotation(leg.Bone);
                var opposite = leg.Side > 0 ? 0f : Mathf.Pi;
                if (_wade > 1e-3f)
                {
                    // Wading: the legs trail behind the travel through the water's drag and paddle as they go.
                    var parent = SafeInverse(pose.ModelTransform(leg.Parent).Basis);
                    var travel = parent * new Vector3(velocity.X, 0, velocity.Z);
                    travel.Y = 0;
                    var trail = ContactMath.Direction(-travel, Vector3.Back);
                    var angle = _wade * share * (TrailRad * Mathf.Clamp(flat / BodyPace.Walk(state), 0, 1.5f) +
                        PaddleRad * Mathf.Sin(Mathf.Tau * _wadePhase + opposite));
                    rotation = ContactMath.AxisAngle(new Vector3(-trail.Z, 0, trail.X), angle) * rotation;
                }
                if (_swim > 1e-3f)
                    rotation = ContactMath.AxisAngle(Vector3.Right, _swim * share * KickRad * Mathf.Sin(2 * Mathf.Tau * _strokePhase + opposite)) * rotation;
                if (_climb > 1e-3f)
                {
                    // Feet to the face: the legs (or the soles) press forward against it, stepping in turn.
                    var press = footOnly ? 0.4f : 0.3f + 0.12f * Mathf.Sin(Mathf.Tau * _climbPhase + opposite + Mathf.Pi);
                    rotation = ContactMath.AxisAngle(Vector3.Right, _climb * press) * rotation;
                }
                pose.SetRotation(leg.Bone, ContactMath.Unit(rotation));
            }
        }

        if (stumble > 0 && !_legless) pose.AddRotation(_rig.BodyBone, ContactMath.AxisAngle(Vector3.Right, -StumblePitchRad * stumble));
        if (_head >= 0 && _dive > 1e-3f) pose.AddRotation(_head, ContactMath.AxisAngle(Vector3.Right, -TuckRad * _dive));
        if (_dive > 1e-3f && speed > 0.02f)
        {
            // Diving: the hat and the hair stream back from the way the body goes.
            var strength = _dive * Mathf.Clamp(speed / 0.1f, 0, 1);
            foreach (var part in _streams)
            {
                var parent = SafeInverse(pose.ModelTransform(part.Parent).Basis);
                var along = ContactMath.Direction(parent * velocity, Vector3.Zero);
                if (along == Vector3.Zero) continue;
                var streamed = ContactMath.Direction(part.Rest - along * 1.2f, part.Rest);
                var turn = ContactMath.Limit(ContactMath.FromTo(part.Rest, streamed), Mathf.DegToRad(70));
                pose.AddRotation(part.Bone, ContactMath.Part(turn, strength));
            }
        }
    }

    /// <summary>The arm that reaches down in a stumble: one on the falling side, else any.</summary>
    private int StumbleArm()
    {
        var any = -1;
        for (var a = 0; a < _arms.Length; a++)
        {
            if (_arms[a].Side == _stumbleSide) return a;
            if (any < 0) any = a;
        }
        return any;
    }

    /// <summary>The leg IK on while the feet are planted (faded in and out), its targets; the wind (and a diver's stream) on the springs.</summary>
    private void Secondary(in BodyState state, bool grounded, float dt)
    {
        if (_rig.LegSolver != null)
        {
            var lowest = float.MaxValue;
            for (var i = 0; i < _feet.Length; i++)
                if (_feet[i].Leg >= 0) lowest = Mathf.Min(lowest, _posed[i].Y);
            var plant = grounded && lowest < PlantLiftM ? 1f : 0f;
            _plant = ContactMath.Approach(_plant, plant, dt, PlantTau);
            if (_plant < 1e-3f && plant == 0) _plant = 0;
            var influence = _plant * (1 - WadeIkRelease * _wade);
            if (influence > 0.002f)
            {
                if (!_ikOn)
                {
                    _rig.UseLegIk(true);
                    _ikOn = true;
                }
                if (Mathf.Abs(influence - _ikInfluence) > 1e-4f)
                {
                    _rig.LegSolver.Influence = influence;
                    _ikInfluence = influence;
                }
                for (var i = 0; i < _feet.Length; i++)
                    if (_feet[i].Leg >= 0 && ContactMath.Finite(_target[i])) _rig.Legs[_feet[i].Leg].Target.GlobalPosition = _target[i];
            }
            else if (_ikOn)
            {
                _rig.UseLegIk(false);
                _ikOn = false;
                _ikInfluence = -1;
            }
        }
        var wind = WindForce(state.WindMps, dt);
        if (_dive > 1e-3f) wind -= state.Velocity * (StreamResponse * _dive * dt);
        if (!ContactMath.Finite(wind)) wind = Vector3.Zero;
        if ((wind - _windSet).LengthSquared() > 1e-10f)
        {
            _rig.Secondary.SetWind(wind);
            _windSet = wind;
        }
    }
}
