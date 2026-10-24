using System;
using Godot;

namespace GarageGame.Native.Characters;

/// <summary>Where a tumble is.</summary>
public enum TumblePhase
{
    /// <summary>No tumble: the layer leaves the pose alone.</summary>
    Idle = 0,
    /// <summary>The body flops: physics moves it, the gait, contact and life layers wait.</summary>
    Flop = 1,
    /// <summary>The bones blend from where they settled back to the standing pose.</summary>
    GetUp = 2,
}

/// <summary>
/// The tumble on a big fall (K7; docs/runs/RUN-2-CHARACTERS.md, "The tumble (K7)"), the pose stack's last layer. At a landing it
/// asks <see cref="TumbleTiers"/> what the fall would cost in Challenge, in every mode; from 25 health it holds the body's
/// control and the character flops: one rigid body shaped like the character's hull (<see cref="TumbleHull"/>; the plan's
/// fallback, shipped because jointed physical bones sank and jittered at this scale: see the suite's spike) takes the landing's
/// velocity and a small seeded spin, lands on the real terrain under the world's gravity and is held by a soft tether to where
/// the capsule stands; the root bone follows it, and the spring chains flop the limbs. Then the bones blend back to the standing
/// pose over 0.4 s and control returns. Moving starts a tumble's get-up early, by at most 0.5 s. A knockout (100) tumbles for
/// 2 s with dazed stars round the head. The capsule never moves for any of it; deep water and a floating body never tumble.
/// <see cref="Tumble"/> is the one entry point later hazards use (an owl's swoop, a firework too close).
/// </summary>
public sealed class CharacterTumble : IPoseLayer<BodyState>
{
    private readonly CharacterVisual _visual;
    private readonly SmallPlayerController _controller;
    private readonly CharacterBody _body;
    private readonly PoseBuffer _standing;
    private readonly PoseBuffer _rest;
    private readonly PoseBuffer _settled;
    private readonly PoseBuffer _blend;
    private readonly TumbleHull _hull;
    private readonly TumbleSprings _springs;
    private readonly Vector3[] _settledSoles;
    private readonly int _headBone;
    private readonly Vector3 _headTop;
    private TumbleStars? _stars;

    private double _startS;
    private double _getUpS;
    private double _endS;
    private double _earliestGetUpS;
    private double _holdEndS;
    private bool _earlyAllowed;
    private Vector3 _anchor;
    private bool _pending;
    private Vector3 _pendingImpulse;
    private float _pendingSeconds;
    private bool _pendingDazed;
    private uint _seedCounter;

    internal CharacterTumble(CharacterVisual visual, SmallPlayerController controller)
    {
        _visual = visual;
        _controller = controller;
        _body = visual.Body;
        var count = _body.BoneCount;
        var parents = new int[count];
        var rests = new Vector3[count];
        var rigid = new bool[count];
        for (var i = 0; i < count; i++)
        {
            parents[i] = _body.Bones[i].Parent;
            rests[i] = _body.Bones[i].RestLocal;
            rigid[i] = _body.Bones[i].IsRigid;
        }
        _standing = new PoseBuffer(parents, rests, rigid);
        _rest = new PoseBuffer(parents, rests, rigid);
        _settled = new PoseBuffer(parents, rests, rigid);
        _blend = new PoseBuffer(parents, rests, rigid);
        _hull = TumbleHull.For(_body);
        _springs = new TumbleSprings(_body);
        _settledSoles = new Vector3[_body.Legs.Count];
        _headBone = _body.HeadBone;
        var head = _body.Bones[_headBone];
        _headTop = head.HasMesh ? head.MeshBounds.GetCenter() + Vector3.Up * (head.MeshBounds.Size.Y / 2) : Vector3.Up * 0.02f;
    }

    public PoseLayer Layer => PoseLayer.Tumble;

    public CharacterVisual Visual => _visual;
    public SmallPlayerController Controller => _controller;
    public TumblePhase Phase { get; private set; }
    public bool IsTumbling => Phase != TumblePhase.Idle;
    /// <summary>The current tumble is a knockout's (or a hazard's as long): stars circle the head.</summary>
    public bool Dazed { get; private set; }
    /// <summary>The current (or last) tumble's length as planned, before any early get-up (s).</summary>
    public float Seconds { get; private set; }
    /// <summary>Seconds into the current tumble (0 when idle).</summary>
    public float ElapsedS => IsTumbling ? (float)(_visual.TimeS - _startS) : 0;
    /// <summary>The tier of the last landing the tumble saw, and what it would have cost in Challenge.</summary>
    public TumbleTier LastTier { get; private set; }
    public float LastLandingCost { get; private set; }
    /// <summary>The downward speed of the last landing the tumble saw (m/s; a deep-water landing's too, which never tumbles).</summary>
    public float LastLandingSpeedMps { get; private set; }
    /// <summary>Tumbles begun, and how many of them got up early because the player moved.</summary>
    public int Tumbles { get; private set; }
    public int EarlyGetUps { get; private set; }
    /// <summary>How long the last tumble lasted, start to the end of its get-up (s).</summary>
    public float LastLengthS { get; private set; }
    /// <summary>The tumbling body (null transform when idle), for tests and the look's capture.</summary>
    public TumbleHull Hull => _hull;
    /// <summary>The spring chains through the tumble: held from the flop to the end of the get-up, then let go.</summary>
    public TumbleSprings Springs => _springs;
    public TumbleStars? Stars => _stars;
    /// <summary>The next tumble's spin seed (tests set it for seeded tumbles); null: from the tick and a count.</summary>
    public uint? NextSeed { get; set; }

    /// <summary>The tumble installed on <paramref name="visual"/>'s stack, or null.</summary>
    public static CharacterTumble? Find(CharacterVisual? visual)
    {
        if (visual == null) return null;
        for (var i = 0; i < visual.Stack.Count; i++)
            if (visual.Stack[i] is CharacterTumble tumble) return tumble;
        return null;
    }

    /// <summary>
    /// Knocks the character over for <paramref name="seconds"/> (the get-up included), set moving by <paramref name="impulse"/>
    /// (a velocity change in m/s, world space; at most <see cref="TumbleLaunch.MaxImpulseMps"/>): the one entry point for later
    /// hazards (TRAITS.md's owl swoop, a firework too close). Holds control at once. A tumble of a knockout's length or longer
    /// is dazed. Refused (false) for a floating body, while swimming or climbing, or for a length that is not finite or positive;
    /// during a tumble it pushes the tumbling body again and lengthens the tumble if it asks for longer.
    /// </summary>
    public bool Tumble(Vector3 impulse, float seconds)
    {
        if (!GodotObject.IsInstanceValid(_controller) || !float.IsFinite(seconds) || seconds <= 0 || !impulse.IsFinite()) return false;
        var state = _visual.LastState;
        if (_controller.Floats || state.Floating || state.Swimming || state.Climbing) return false;
        seconds = Mathf.Clamp(seconds, TumbleTiers.GetUpSeconds + 0.1f, SmallPlayerController.MaxControlHoldS);
        _controller.HoldControl(seconds);
        _pendingSeconds = _pending ? Mathf.Max(_pendingSeconds, seconds) : seconds;
        _pendingImpulse = _pending ? _pendingImpulse + impulse : impulse;
        _pendingDazed = (_pending && _pendingDazed) || seconds >= TumbleTiers.KnockoutSeconds;
        _pending = true;
        return true;
    }

    public void Apply(PoseBuffer pose, in BodyState state, in PoseClock clock)
    {
        var now = clock.TimeS;
        if (state.Landed && !state.Floating && !_controller.Floats)
        {
            // What a host holds the body for beyond this layer's own hold (Challenge's recovery at the spawn waiting for the knockout
            // tumble of a fall that took the last health), read before this layer holds anything.
            var ownHold = Math.Max(Math.Max(0, _holdEndS - now), _pending ? _pendingSeconds : 0);
            var hostHold = _controller.ControlHoldS > ownHold + 0.05f ? _controller.ControlHoldS : 0;
            LastLandingSpeedMps = state.LandingSpeedMps;
            LastLandingCost = TumbleTiers.Cost(state.LandingSpeedMps, state.LandedInDeepWater, _controller.Knacks);
            LastTier = TumbleTiers.Classify(LastLandingCost);
            // A landing below the tumble tier that knocks the player out (a stumble's cost from their last health) is the knockout's
            // tumble too: the host's recovery waits for one, and the player must not stand frozen meanwhile.
            var knockedOut = LastTier == TumbleTier.Stumble && hostHold > TumbleTiers.GetUpSeconds + 0.1f;
            if (LastTier >= TumbleTier.Tumble || knockedOut)
            {
                var seconds = knockedOut ? Mathf.Min(hostHold, SmallPlayerController.MaxControlHoldS) : TumbleTiers.Seconds(LastTier);
                var dazed = knockedOut || LastTier == TumbleTier.Knockout;
                _controller.HoldControl(seconds);
                // A host that holds the body longer (Challenge's knockout recovery for a player already hurt) gets the knockout's tumble.
                if (!dazed && _controller.ControlHoldS > seconds + 0.05f)
                {
                    seconds = Mathf.Min(_controller.ControlHoldS, SmallPlayerController.MaxControlHoldS);
                    dazed = true;
                }
                var landing = new Vector3(state.Velocity.X, -state.LandingSpeedMps, state.Velocity.Z);
                Begin(pose, state, now, TumbleLaunch.FromLanding(landing, Seed(state), dazed), seconds, dazed, earlyAllowed: !dazed);
            }
        }
        if (_pending)
        {
            _pending = false;
            var dazed = _pendingDazed;
            _pendingDazed = false;
            Begin(pose, state, now, TumbleLaunch.FromImpulse(_pendingImpulse, Seed(state), dazed), _pendingSeconds, dazed, earlyAllowed: !dazed);
        }
        switch (Phase)
        {
            case TumblePhase.Flop:
                Flop(pose, state, clock);
                break;
            case TumblePhase.GetUp:
                GetUp(pose, now);
                break;
        }
        PlaceStars(pose, now);
    }

    private uint Seed(in BodyState state)
    {
        if (NextSeed is { } seed)
        {
            NextSeed = null;
            return seed;
        }
        return (uint)state.Tick * 2654435761u ^ ++_seedCounter * 40503u;
    }

    private void Begin(PoseBuffer pose, in BodyState state, double now, TumbleKick kick, float seconds, bool dazed, bool earlyAllowed)
    {
        if (Phase == TumblePhase.Flop)
        {
            // A second blow while flopping: push the body again (wake it if it already lay still), and tumble longer if asked.
            if (_hull.Resting && _body.IsInsideTree()) _hull.Begin(_body.GetWorld3D(), _hull.Transform, kick, state.GravityMps2);
            else _hull.Kick(kick);
            ExtendTo(now, seconds, dazed);
            return;
        }
        if (Phase == TumblePhase.GetUp)
        {
            // Knocked over again while getting up: flop again from where the get-up has got to.
            _standing.CopyFrom(_settled);
        }
        else _standing.CopyFrom(pose);
        Tumbles++;
        Phase = TumblePhase.Flop;
        Dazed = dazed;
        Seconds = seconds;
        _startS = now;
        _earlyAllowed = earlyAllowed;
        SetTimes(now, seconds);
        _anchor = state.Position;
        var stack = _visual.Stack;
        stack.Suspend(PoseLayer.Gait, true);
        stack.Suspend(PoseLayer.Contact, true);
        stack.Suspend(PoseLayer.Life, true);
        if (_body.IsInsideTree())
        {
            _hull.Begin(_body.GetWorld3D(), SkeletonGlobal() * RootFrame(_standing), kick, state.GravityMps2);
            _springs.Hold();
        }
    }

    private void SetTimes(double now, float seconds)
    {
        _endS = now + seconds;
        _getUpS = _endS - TumbleTiers.GetUpSeconds;
        _earliestGetUpS = Math.Max(now + 0.1, _getUpS - TumbleTiers.EarlyGetUpSeconds);
        _holdEndS = Math.Max(_holdEndS, now + seconds);
    }

    private void ExtendTo(double now, float seconds, bool dazed)
    {
        if (now + seconds > _endS)
        {
            Seconds = (float)(now + seconds - _startS);
            var start = _startS;
            SetTimes(now, seconds);
            _earliestGetUpS = Math.Max(_earliestGetUpS, start + 0.1);
        }
        if (dazed)
        {
            Dazed = true;
            _earlyAllowed = false;
        }
    }

    private void Flop(PoseBuffer pose, in BodyState state, in PoseClock clock)
    {
        var now = clock.TimeS;
        // The capsule went somewhere the flop cannot follow (home, a recovery, a wrap), or the water or a wall caught it: get up.
        var jumped = state.Position.DistanceSquaredTo(_anchor) > 0.25f * 0.25f;
        _anchor = state.Position;
        var anchor = SkeletonGlobal() * _body.Bones[CharacterBody.RootBone].RestLocal;
        if (_hull.Active) _hull.Tick(anchor, state.GravityMps2, clock.DeltaS);
        else _hull.Follow(anchor);
        // Every part flops at rest, the pose the hull is made from (TumbleHull: every part at rest): the standing pose the gait and
        // contact gave the landing (a stride, a swing, legs bent to plant) would carry a limb outside the hull and into the ground
        // (Stickbear's arms and legs sank up to 8 mm once the suite dressed its bodies as the room does).
        pose.CopyFrom(_rest);
        if ((_hull.Active || _hull.Resting) && !jumped)
        {
            // Where physics put the body, and the drawn parts' last millimetres out of the ground (TumbleHull.DrawnLift).
            var root = SkeletonGlobal().AffineInverse() * _hull.Transform.Translated(_hull.DrawnLift);
            pose.SetPosition(CharacterBody.RootBone, root.Origin);
            pose.SetRotation(CharacterBody.RootBone, root.Basis.GetRotationQuaternion());
            pose.SetScale(CharacterBody.RootBone, Vector3.One);
        }
        if (_earlyAllowed && state.HeldInput.LengthSquared() > 0.01f && now < _getUpS)
        {
            var early = Math.Max(now, _earliestGetUpS);
            if (early < _getUpS)
            {
                _getUpS = early;
                _endS = early + TumbleTiers.GetUpSeconds;
                EarlyGetUps++;
                _earlyAllowed = false;
            }
        }
        if (jumped || state.Swimming || state.Climbing || state.Floating || !_hull.Active && !_hull.Resting && _body.IsInsideTree())
        {
            _getUpS = now;
            _endS = now + TumbleTiers.GetUpSeconds;
        }
        if (now + 1e-6 >= _getUpS) StartGetUp(pose, now);
    }

    private void StartGetUp(PoseBuffer pose, double now)
    {
        _settled.CopyFrom(pose);
        var skeleton = SkeletonGlobal();
        for (var i = 0; i < _settledSoles.Length; i++)
            _settledSoles[i] = skeleton * (_settled.ModelTransform(_body.Legs[i].FootBone) * _body.Legs[i].SoleOffset);
        _hull.End();
        _getUpS = now;
        _endS = now + TumbleTiers.GetUpSeconds;
        Phase = TumblePhase.GetUp;
        var stack = _visual.Stack;
        stack.Suspend(PoseLayer.Gait, false);
        stack.Suspend(PoseLayer.Contact, false);
        stack.Suspend(PoseLayer.Life, false);
    }

    private void GetUp(PoseBuffer pose, double now)
    {
        var t = Mathf.Clamp((float)((now - _getUpS) / TumbleTiers.GetUpSeconds), 0, 1);
        if (t >= 1)
        {
            Finish(now);
            return;
        }
        // The layers below have posed the standing body this frame; blend from where the body settled toward it.
        var weight = t * t * (3 - 2 * t);
        var standing = RootFrame(pose);
        _blend.CopyFrom(_settled);
        _blend.BlendToward(pose, weight);
        pose.CopyFrom(_blend);
        // Contact plants the standing feet again with the leg IK (it ran before this layer and set its targets): the targets move
        // from where the soles lay to where contact puts them as the body rises, so a body still lying never reaches its legs
        // through the ground for where its standing feet will be, and the planted feet hold the dropped pelvis up meanwhile.
        if (_body.LegSolver is { Active: true } && _body.IsInsideTree())
            for (var i = 0; i < _settledSoles.Length; i++)
            {
                var target = _body.Legs[i].Target;
                var planted = target.GlobalPosition;
                if (!planted.IsFinite() || !_settledSoles[i].IsFinite()) continue;
                // Up a step the foot rises early and down one it drops late, so it never cuts through the step's edge.
                var from = _settledSoles[i];
                var rise = planted.Y > from.Y ? 1 - (1 - weight) * (1 - weight) : weight * weight;
                target.GlobalPosition = new Vector3(Mathf.Lerp(from.X, planted.X, weight), Mathf.Lerp(from.Y, planted.Y, rise), Mathf.Lerp(from.Z, planted.Z, weight));
            }
        // The blend turns the body up about its feet, through poses neither end has: the tumble's own rays keep the drawn parts no
        // deeper in the ground than the standing pose it blends to (whose feet are the contact layer's business), so it rises out
        // of the ground and hands over without a jump.
        if (_body.IsInsideTree())
        {
            var skeleton = SkeletonGlobal();
            var depth = _hull.DrawnDepth(skeleton * RootFrame(pose));
            if (depth != Vector3.Zero)
            {
                var along = depth.Normalized();
                var allowed = Mathf.Max(0, _hull.DrawnDepth(skeleton * standing).Dot(along));
                var lift = depth.Length() - allowed;
                if (lift > 0)
                    pose.SetPosition(CharacterBody.RootBone, pose.GetPosition(CharacterBody.RootBone) + skeleton.Basis.Inverse() * (along * lift));
            }
        }
        // Wherever that put it (a step's face pushes sideways), the root stays on the tether's rope.
        var rest = _body.Bones[CharacterBody.RootBone].RestLocal;
        var root = pose.GetPosition(CharacterBody.RootBone);
        var offset = new Vector2(root.X - rest.X, root.Z - rest.Z);
        if (offset.Length() > TumbleHull.TetherReachM)
        {
            offset = offset.Normalized() * TumbleHull.TetherReachM;
            pose.SetPosition(CharacterBody.RootBone, new Vector3(rest.X + offset.X, root.Y, rest.Z + offset.Y));
        }
    }

    private void Finish(double now)
    {
        Phase = TumblePhase.Idle;
        _springs.Release();
        LastLengthS = (float)(now - _startS);
        // Give control back if what is left of the hold is the tumble's own; a longer hold (a host's knockout recovery) stays.
        if (_controller.ControlHoldS <= Math.Max(0, _holdEndS - now) + 1.0 / 30) _controller.ReleaseControl();
        _holdEndS = 0;
        Dazed = false;
    }

    private void PlaceStars(PoseBuffer pose, double now)
    {
        if (!Dazed || Phase == TumblePhase.Idle)
        {
            if (_stars != null) _stars.Visible = false;
            return;
        }
        if (_stars == null)
        {
            _stars = TumbleStars.Create(_body);
            _body.AddChild(_stars);
        }
        var head = _body.Skeleton.Transform * pose.ModelTransform(_headBone);
        var top = head * _headTop;
        var elapsed = (float)(now - _startS);
        var left = (float)(_endS - now);
        var grow = Mathf.Min(1, Mathf.Min(elapsed, left) / TumbleStars.FadeS);
        _stars.Place(top + Vector3.Up * 0.008f, elapsed, grow);
    }

    /// <summary>The skeleton's world transform (where the capsule draws the character).</summary>
    private Transform3D SkeletonGlobal() => _body.Skeleton.IsInsideTree() ? _body.Skeleton.GlobalTransform : Transform3D.Identity;

    /// <summary>The root bone's frame under <paramref name="pose"/>, without its scale (the hull does not squash).</summary>
    private static Transform3D RootFrame(PoseBuffer pose) =>
        new(new Basis(pose.GetRotation(CharacterBody.RootBone)), pose.GetPosition(CharacterBody.RootBone));

    /// <summary>Ends any tumble at once and frees its body (the visual left the tree, or was replaced).</summary>
    internal void Abort()
    {
        _hull.End();
        _springs.Release();
        _pending = false;
        if (Phase != TumblePhase.Idle)
        {
            var stack = _visual.Stack;
            stack.Suspend(PoseLayer.Gait, false);
            stack.Suspend(PoseLayer.Contact, false);
            stack.Suspend(PoseLayer.Life, false);
        }
        Phase = TumblePhase.Idle;
        Dazed = false;
        if (_stars != null) _stars.Visible = false;
    }
}
