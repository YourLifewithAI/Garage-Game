using System;
using Godot;
using GarageGame.Native;
using GarageGame.Native.Characters;
using GarageGame.Native.Characters.Contact;
using GarageGame.Native.Characters.Gaits;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;

namespace GarageGame.Tests.Look;

/// <summary>
/// One character sheet tile's script (CharacterSheetMotion): it feeds the body's pose stack the <see cref="BodyState"/> its capsule
/// would report, once per physics tick (<see cref="CharacterVisual.BodyTicked"/>, the game's own entry), and says when the motion
/// has reached a moment worth a tile. Idle rests two seconds and waits for open eyes; walk, run and wading move at the shared speeds
/// (0.32 and 0.96 m/s, a wade slowed as the controller slows it) and, after the springs settle, watch one cycle and take the tile
/// at its most telling point (the widest stride, the full roll of a waddle, the top of a hop, or in water a hop's lowest point,
/// its feet in the water); a floating body is taken leaning
/// into its start instead. A hop lands at just under the speed that would hurt and is taken at the squash's deepest; the slope
/// stands on the stage's real 30° ground with its normal; climbing goes up the stage's wall at the climb speed, taken with a hand
/// at the top of its reach; the tumble lands at a knockout's speed on the room's floor and is taken 0.4 s into the flop; the
/// close look waits for the face to glance at the camera (its point of interest); the floating body's ninth tile casts at the
/// stage's spot through the companion (CompanionAvatar.CastGesture, with the look's spark) and is taken just past the cast's peak.
/// A floating body never stands, lands, climbs, swims or tumbles: those tiles show it floating. This node lives on the body, so it
/// goes with it.
/// </summary>
public partial class CharacterSheetActor : Node
{
    public const string NodeName = "CharacterSheetActor";
    /// <summary>Seconds for the springs and smoothed weights to settle before a moving tile starts watching its cycle.</summary>
    public const double WarmS = 1.2;
    /// <summary>Seconds a standing tile rests before it is taken (at the first moment its eyes are open).</summary>
    public const double RestS = 2.0, SettleS = 1.6;
    /// <summary>A cycle's tile is taken within this share of the cycle's range of its measure from the top.</summary>
    public const float NearTop = 0.03f;
    /// <summary>When the floating body casts, and how far through the cast gesture its tile is taken (the peak is at 0.25).</summary>
    public const double CastAtS = 1.2;
    public const float CastTakenAt = 0.3f;
    /// <summary>The tumble's tile: this long after the landing.</summary>
    public const double TumbleTakenAfterS = 0.4;

    private CharacterVisual _visual = null!;
    private SmallPlayerController _capsule = null!;
    private bool _floats;
    private GaitLayer? _gait;
    private LifeLayer? _life;
    private ContactLayer? _contact;
    private LookTargetLayer? _look;
    private CharacterTumble? _tumble;

    private ulong _tick;
    private float _distance;
    private float _airTime;
    private float _max = float.NegativeInfinity, _min = float.PositiveInfinity;
    private double _watchUntil = -1;
    private double _eventS = -1;
    private float _peak, _previous;

    /// <summary>Where the face glances now and then (the body's point of interest): the camera for the close look, else none.</summary>
    public Vector3? Interest { get; private set; }

    public void Initialize(CharacterVisual visual, SmallPlayerController capsule)
    {
        Name = NodeName;
        _visual = visual;
        _capsule = capsule;
        _floats = capsule.Floats;
        _gait = CharacterGaits.FindGait(visual.Stack);
        _life = CharacterGaits.FindLife(visual.Stack);
        _contact = CharacterContact.FindContact(visual);
        _look = CharacterContact.FindLook(visual);
        _tumble = CharacterTumble.Find(visual);
    }

    public CharacterSheetPoseStatus Step(CharacterSheetPoseContext c)
    {
        if (_visual == null || !IsInstanceValid(_visual.Body) || !_visual.Body.IsInsideTree() || !IsInstanceValid(_capsule)) return CharacterSheetPoseStatus.Rest;
        var status = Tile(c);
        if (status == CharacterSheetPoseStatus.Ready) Judge(c);
        return status;
    }

    private CharacterSheetPoseStatus Tile(CharacterSheetPoseContext c)
    {
        return c.TileId switch
        {
            "idle" => Idle(c, _floats ? "floating, breathing" : "breathing, eyes open"),
            "walk" => Travel(c, MotionProfile.WalkSpeedMps, false, null),
            "run" => Travel(c, MotionProfile.RunSpeedMps, true, null),
            "hop_landing" => _floats ? Idle(c, "") : HopLanding(c),
            "slope_30" => _floats ? Idle(c, "floating over it") : Slope(c),
            "wading" => _floats ? Idle(c, "") : Travel(c, WadeSpeed(c), false, c.Stage.WaterLevelM),
            "climbing" => _floats ? Idle(c, "") : Climb(c),
            "tumble" => _floats ? Idle(c, "") : Tumble(c),
            "ninth" => _floats ? Cast(c) : CloseLook(c),
            _ => CharacterSheetPoseStatus.Rest,
        };
    }

    // ---------- the capsule's report ----------

    /// <summary>One tick of the body: the state its capsule would report (still, at the stage's feet), handed to the pose stack.</summary>
    private void Run(CharacterSheetPoseContext c, Vector3 velocity, bool grounded, bool sprint = false, bool landed = false, float landingSpeed = 0,
        bool climbing = false, Vector3 climbNormal = default, float? water = null)
    {
        var dt = c.DeltaS;
        grounded &= !_floats;
        climbing &= !_floats;
        landed &= !_floats;
        var speed = climbing ? velocity.Length() : new Vector2(velocity.X, velocity.Z).Length();
        _distance += speed * dt;
        _airTime = grounded || climbing || _floats ? 0 : _airTime + dt;
        var feet = c.Holder.GlobalPosition;
        var state = new BodyState
        {
            Tick = ++_tick,
            Position = feet,
            Yaw = c.Holder.GlobalRotation.Y,
            Velocity = velocity,
            SpeedMps = speed,
            DistanceM = _distance,
            Grounded = grounded,
            FloorNormal = grounded ? c.Stage.GroundNormal : Vector3.Up,
            AirTimeS = _airTime,
            Landed = landed,
            LandingSpeedMps = landed ? landingSpeed : 0,
            LandedInDeepWater = false,
            Climbing = climbing,
            ClimbNormal = climbing ? climbNormal : Vector3.Zero,
            Swimming = false,
            SwimTilt = 0,
            Diving = false,
            Floating = _floats,
            Carrying = false,
            Sprinting = sprint,
            Blocked = false,
            WindMps = Vector2.Zero,
            ControlHeld = _capsule.ControlHoldS > 0,
            HeldInput = Vector2.Zero,
            WaterSurfaceY = water ?? float.NaN,
            WaterDepthM = water is { } surface ? Mathf.Max(0, surface - feet.Y) : 0,
            BodyHeightM = _capsule.BodyHeightM,
            GravityMps2 = _capsule.GravityMps2,
        };
        _visual.BodyTicked(state, dt);
    }

    // ---------- the tiles ----------

    private CharacterSheetPoseStatus Idle(CharacterSheetPoseContext c, string note)
    {
        Run(c, Vector3.Zero, grounded: true);
        var t = c.Seconds;
        if (t >= RestS && (EyesOpen() || t >= RestS + 4))
        {
            c.Note = note;
            return CharacterSheetPoseStatus.Ready;
        }
        return CharacterSheetPoseStatus.Posing;
    }

    private CharacterSheetPoseStatus Travel(CharacterSheetPoseContext c, float speed, bool sprint, float? water)
    {
        var t = c.Seconds;
        var wet = water is { } surface ? $", water {Mathf.Max(0, surface - c.Holder.GlobalPosition.Y) * 100:0} cm deep" : "";
        if (_floats)
        {
            // A float leans into a start (its lean follows acceleration): it sets off from rest, gathering speed as the companion
            // does in the air, and is taken at the height of the lean.
            const double start = 0.8;
            var v = t <= start ? 0f : Mathf.Min(speed, (float)(t - start) * CompanionAvatar.CompanionAirAccelerationMps2);
            Run(c, new Vector3(0, 0, -v), grounded: false, sprint);
            if (t <= start + 0.1) return CharacterSheetPoseStatus.Posing;
            var lean = Mathf.Abs(_gait?.LeanPitchRad ?? 0);
            if (lean + 1e-5f < _peak || t > start + 3)
            {
                c.Note = $"setting off for {speed:0.00} m/s, leaning {Mathf.RadToDeg(_peak):0}° into it";
                return CharacterSheetPoseStatus.Ready;
            }
            _peak = Mathf.Max(_peak, lean);
            return CharacterSheetPoseStatus.Posing;
        }
        Run(c, new Vector3(0, 0, -speed), grounded: true, sprint, water: water);
        var stride = _gait?.Frame.StrideM ?? 0;
        var cycle = stride > 0 && speed > 0 ? stride / speed : 1f;
        var inWater = water != null;
        return Cycle(c, Measure(inWater), cycle, $"{speed:0.00} m/s, {Moment(inWater)}{wet}");
    }

    private CharacterSheetPoseStatus HopLanding(CharacterSheetPoseContext c)
    {
        // A hop up and down onto the floor, landing just under the speed that would start to hurt: the deepest plain squash.
        const double jumpAt = 0.5;
        const float upMps = 0.6f;
        var landing = 0.97f * PlayerVitals.SafeLandingSpeedMps;
        var t = c.Seconds;
        if (_eventS < 0)
        {
            if (t <= jumpAt)
            {
                Run(c, Vector3.Zero, grounded: true);
                return CharacterSheetPoseStatus.Posing;
            }
            var vy = upMps - _capsule.GravityMps2 * (float)(t - jumpAt);
            if (vy > -landing)
            {
                Run(c, new Vector3(0, vy, 0), grounded: false);
                return CharacterSheetPoseStatus.Posing;
            }
            _eventS = t;
            Run(c, Vector3.Zero, grounded: true, landed: true, landingSpeed: landing);
            _previous = _contact?.Squash ?? 0;
            return CharacterSheetPoseStatus.Posing;
        }
        Run(c, Vector3.Zero, grounded: true);
        var squash = _contact?.Squash ?? 0;
        if (squash + 1e-6f < _previous || t - _eventS > 0.4)
        {
            c.Note = $"landed at {landing:0.00} m/s, squashed {Mathf.Max(squash, _previous) * 100:0}%";
            return CharacterSheetPoseStatus.Ready;
        }
        _previous = squash;
        return CharacterSheetPoseStatus.Posing;
    }

    private CharacterSheetPoseStatus Slope(CharacterSheetPoseContext c)
    {
        Run(c, Vector3.Zero, grounded: true);
        var t = c.Seconds;
        if (t < SettleS || (!EyesOpen() && t < SettleS + 4)) return CharacterSheetPoseStatus.Posing;
        var feet = _contact?.FootCount ?? 0;
        var found = 0;
        for (var i = 0; i < feet; i++)
            if (_contact!.FootFoundGround(i)) found++;
        var tilt = TiltDeg();
        c.Note = _contact == null ? "" : _contact.Legless
            ? $"{found} of {feet} feet on it, the body tilted {tilt:0}°"
            : $"{found} of {feet} feet planted by the leg IK, hips down {_contact.DropM * 1000:0} mm, tilted {tilt:0}°";
        return CharacterSheetPoseStatus.Ready;
    }

    private CharacterSheetPoseStatus Climb(CharacterSheetPoseContext c)
    {
        var speed = _capsule.ClimbSpeedMps;
        var normal = c.Stage.Wall?.Normal ?? Vector3.Back;
        Run(c, new Vector3(0, speed, 0), grounded: false, climbing: true, climbNormal: normal);
        return Cycle(c, HighestHand(), 0.9f, $"{speed:0.00} m/s up the wall, a hand at the top of its reach");
    }

    private CharacterSheetPoseStatus Tumble(CharacterSheetPoseContext c)
    {
        // A fall that ends at a knockout's landing speed (the tumble's own tiers, from fall damage), travelling a little forward,
        // onto the room's floor: the tumble's real physics takes it from there.
        const double fallAt = 0.3;
        const float carryMps = 0.3f;
        var knockout = KnockoutSpeed();
        var t = c.Seconds;
        if (_eventS < 0)
        {
            if (t <= fallAt)
            {
                Run(c, Vector3.Zero, grounded: true);
                return CharacterSheetPoseStatus.Posing;
            }
            var vy = -_capsule.GravityMps2 * (float)(t - fallAt);
            if (vy > -knockout)
            {
                Run(c, new Vector3(0, vy, -carryMps), grounded: false);
                return CharacterSheetPoseStatus.Posing;
            }
            _eventS = t;
            Run(c, new Vector3(0, 0, -carryMps), grounded: true, landed: true, landingSpeed: knockout);
            return CharacterSheetPoseStatus.Posing;
        }
        Run(c, Vector3.Zero, grounded: true);
        if (t - _eventS + 1e-6 < TumbleTakenAfterS) return CharacterSheetPoseStatus.Posing;
        var what = _tumble == null ? "no tumble on this body" : _tumble.IsTumbling
            ? $"{_tumble.ElapsedS:0.00} s into a {(_tumble.Dazed ? "dazed " : "")}{_tumble.Phase.ToString().ToLowerInvariant()}"
            : "the tumble did not start";
        c.Note = $"landed at {knockout:0.0} m/s, {what}";
        return CharacterSheetPoseStatus.Ready;
    }

    private CharacterSheetPoseStatus CloseLook(CharacterSheetPoseContext c)
    {
        // The camera is the face's point of interest: it glances at it now and then, as at the other avatar in the game.
        Interest = c.Camera.GlobalPosition;
        Run(c, Vector3.Zero, grounded: true);
        var t = c.Seconds;
        if (_look?.Glancing == true)
        {
            if (_eventS < 0) _eventS = t;
        }
        else _eventS = -1;
        if (t >= SettleS && _eventS >= 0 && t - _eventS >= 0.35 && EyesOpen())
        {
            c.Note = "glancing at the camera";
            return CharacterSheetPoseStatus.Ready;
        }
        if (t >= 6.5 && (EyesOpen() || t >= 7.5))
        {
            c.Note = "looking ahead";
            return CharacterSheetPoseStatus.Ready;
        }
        return CharacterSheetPoseStatus.Posing;
    }

    private CharacterSheetPoseStatus Cast(CharacterSheetPoseContext c)
    {
        Run(c, Vector3.Zero, grounded: false);
        var t = c.Seconds;
        var companion = c.World.Companion;
        if (_eventS < 0)
        {
            if (t < CastAtS) return CharacterSheetPoseStatus.Posing;
            // As the HUD casts: the look's glow at the spot (its spark leaves the drawn body now), then the gesture toward it. The
            // companion faces as the drawing does, so the lean it works out is the drawing's.
            var spot = c.Stage.CastSpotM ?? c.Holder.GlobalTransform * new Vector3(-0.16f, 0.08f, -0.06f);
            companion.GlobalRotation = new Vector3(0, c.Holder.GlobalRotation.Y, 0);
            // The Glow ability's defaults (rules storybook_wild: area 0.6 m, intensity 0.6).
            c.World.Look.StartGlow(CharacterSheetPoses.CastGlowId, null, spot - Vector3.Up * GlowLook.WispLiftM, 0.6f, 0.6f);
            companion.CastGesture(spot);
            _eventS = t;
            return CharacterSheetPoseStatus.Posing;
        }
        if (companion.Playing == CompanionAvatar.Gesture.Cast && companion.GestureProgress >= CastTakenAt)
        {
            c.Note = $"casting to its left, {companion.GestureProgress:0.00} through, the arm swung {Mathf.RadToDeg(CastSwing()):0}°";
            return CharacterSheetPoseStatus.Ready;
        }
        if (t - _eventS > 1.0)
        {
            c.Note = "the cast gesture did not play";
            return CharacterSheetPoseStatus.Ready;
        }
        return CharacterSheetPoseStatus.Posing;
    }

    // ---------- the verdict ----------

    /// <summary>Whether the body did what the tile's label names, read from the layers themselves at the moment taken.</summary>
    private void Judge(CharacterSheetPoseContext c)
    {
        var moving = (_gait?.Frame.Move ?? 0) > 0.9f;
        string missing;
        if (_floats)
            missing = c.TileId switch
            {
                "walk" or "run" => _peak > 0.01f ? "" : "no lean into the start",
                "ninth" => CastSwing() > 0.05f && c.World.Companion.Playing == CompanionAvatar.Gesture.Cast ? "" : "no cast under way",
                _ => EyesOpen() ? "" : "mid-blink",
            };
        else
            switch (c.TileId)
            {
                case "idle":
                case "ninth":
                    missing = EyesOpen() ? "" : "mid-blink";
                    break;
                case "walk":
                case "run":
                    missing = moving ? "" : "not moving";
                    break;
                case "wading":
                    missing = !moving ? "not moving" : (_contact?.Wading ?? 0) < 0.3f ? "the legs are not wading"
                        : c.Stage.WaterLevelM is { } surface && LowestFootY() > surface
                            ? $"the feet are out of the water (the lowest {(LowestFootY() - surface) * 1000:0} mm above it)" : "";
                    break;
                case "hop_landing":
                    missing = _contact == null || _contact.LastTier != LandingTier.Squash || Mathf.Max(_contact.Squash, _previous) < 0.01f ? "no landing squash" : "";
                    break;
                case "slope_30":
                {
                    var feet = _contact?.FootCount ?? 0;
                    var found = 0;
                    for (var i = 0; i < feet; i++)
                        if (_contact!.FootFoundGround(i)) found++;
                    missing = _contact == null ? "no contact layer" : feet > 0 && found < feet ? $"{feet - found} feet found no ground"
                        : !_contact.Legless && !_contact.IkOn ? "the leg IK is off"
                        : _contact.Legless && TiltDeg() < 10 ? "the body did not tilt to the slope" : "";
                    break;
                }
                case "climbing":
                    missing = _contact == null || _contact.ClimbWeight < 0.9f ? "not climbing" : Mathf.Abs(_contact.PressM) < 1e-4f ? "the wall was not found" : "";
                    break;
                case "tumble":
                    missing = _tumble?.IsTumbling == true && _tumble.Phase == TumblePhase.Flop ? "" : "not flopping";
                    break;
                default:
                    missing = "";
                    break;
            }
        c.Shows = missing.Length == 0;
        c.Missing = missing;
    }

    private float CastSwing()
    {
        for (var i = 0; i < _visual.Stack.Count; i++)
            if (_visual.Stack[i] is GubbleGestureLayer gestures) return gestures.LastArmSwingRad;
        return 0;
    }

    private float TiltDeg() => _contact == null ? 0 : Mathf.RadToDeg(2 * Mathf.Acos(Mathf.Clamp(Mathf.Abs(_contact.RootTilt.Normalized().W), 0, 1)));

    // ---------- choosing the moment ----------

    /// <summary>
    /// After the warm-up, watch one cycle of <paramref name="measure"/> (its range), then take the first tick near its top: the
    /// cycle repeats, so that is the same moment one cycle on.
    /// </summary>
    private CharacterSheetPoseStatus Cycle(CharacterSheetPoseContext c, float measure, float cycleS, string note)
    {
        var t = c.Seconds;
        if (t < WarmS) return CharacterSheetPoseStatus.Posing;
        if (_watchUntil < 0) _watchUntil = t + Math.Clamp(1.1 * cycleS, 0.5, 2.0);
        if (t <= _watchUntil)
        {
            _max = Mathf.Max(_max, measure);
            _min = Mathf.Min(_min, measure);
            return CharacterSheetPoseStatus.Posing;
        }
        var range = _max - _min;
        if (range < 1e-6f || measure >= _max - NearTop * range || t > _watchUntil + 2.5)
        {
            c.Note = note;
            return CharacterSheetPoseStatus.Ready;
        }
        return CharacterSheetPoseStatus.Posing;
    }

    /// <summary>
    /// What a gait's cycle shows best, measured on this tick's pose: the legs' spread, the waddle's roll, the hop's height (in
    /// water, its lowest point instead: a wade is the feet in the water, which the top of a hop lifts out of it).
    /// </summary>
    private float Measure(bool inWater = false)
    {
        if (_gait == null) return 0;
        var pose = _visual.Body.Pose;
        const int root = CharacterBody.RootBone;
        switch (_gait.Gait.Kind)
        {
            case MotionKind.Stride:
            {
                var right = _gait.Rig.Leg[GaitRig.Right];
                var left = _gait.Rig.Leg[GaitRig.Left];
                if (right < 0 || left < 0) return 0;
                return Mathf.Abs(LegSwing(pose, right) - LegSwing(pose, left));
            }
            case MotionKind.Waddle:
                return Mathf.Abs((pose.GetRotation(root) * Vector3.Up).X);
            case MotionKind.Hop:
                return (inWater ? -1 : 1) * (pose.GetPosition(root).Y - pose.RestPosition(root).Y);
            default:
                return 0;
        }
    }

    private string Moment(bool inWater = false) => _gait?.Gait.Kind switch
    {
        MotionKind.Stride => "the widest stride",
        MotionKind.Waddle => "rolled onto one foot",
        MotionKind.Hop => inWater ? "down between hops" : "the top of a hop",
        _ => "mid-glide",
    };

    /// <summary>The lowest sole (the bottom middle of a foot's or shoe's mesh) in the world under this tick's pose; +infinity for a body without feet.</summary>
    private float LowestFootY()
    {
        var body = _visual.Body;
        var pose = body.Pose;
        var skeleton = body.Skeleton.GlobalTransform;
        var lowest = float.PositiveInfinity;
        for (var i = 1; i < body.BoneCount; i++)
        {
            var bone = body.Bones[i];
            if (!PartRoles.IsFoot(bone.Role)) continue;
            var sole = bone.HasMesh ? new Vector3(bone.MeshBounds.GetCenter().X, bone.MeshBounds.Position.Y, bone.MeshBounds.GetCenter().Z) : Vector3.Zero;
            lowest = Mathf.Min(lowest, (skeleton * (pose.ModelTransform(i) * sole)).Y);
        }
        return lowest;
    }

    /// <summary>A leg's swing forward about its hip (radians; positive forward), from its pose.</summary>
    private static float LegSwing(PoseBuffer pose, int leg)
    {
        var down = pose.GetRotation(leg) * Vector3.Down;
        return Mathf.Atan2(-down.Z, -down.Y);
    }

    /// <summary>The highest hand (or paw) in the character's space under this tick's pose; 0 for a body without hands.</summary>
    private float HighestHand()
    {
        var body = _visual.Body;
        var pose = body.Pose;
        var highest = float.NegativeInfinity;
        for (var i = 1; i < body.BoneCount; i++)
        {
            var bone = body.Bones[i];
            if (bone.IsRigid || !PartRoles.IsHand(bone.Role)) continue;
            highest = Mathf.Max(highest, pose.ModelTransform(i).Origin.Y);
        }
        return float.IsFinite(highest) ? highest : 0;
    }

    /// <summary>Eyes open: no blink under way or about to begin (a double blink comes 0.22 s after the first).</summary>
    private bool EyesOpen()
    {
        if (_life == null || _life.Blinkers.Count == 0) return true;
        var now = _visual.TimeS;
        var last = _life.LastBlinkS;
        return (double.IsNaN(last) || now - last > 0.4) && _life.NextBlinkS - now > 0.05;
    }

    /// <summary>The slowest landing that is a knockout by the tumble's own tiers (fall damage), and a little more.</summary>
    private static float KnockoutSpeed()
    {
        var speed = 0f;
        while (speed < 20 && TumbleTiers.Classify(speed, false) != TumbleTier.Knockout) speed += 0.05f;
        return speed + 0.1f;
    }

    /// <summary>The walk speed slowed as the controller slows a wade: toward WadeSlowestFactor as the water rises to SwimDepthM.</summary>
    private float WadeSpeed(CharacterSheetPoseContext c)
    {
        var depth = c.Stage.WaterLevelM is { } surface ? Mathf.Max(0, surface - c.Holder.GlobalPosition.Y) : 0;
        return MotionProfile.WalkSpeedMps * Mathf.Lerp(1f, _capsule.WadeSlowestFactor, Mathf.Clamp(depth / Mathf.Max(_capsule.SwimDepthM, 1e-3f), 0, 1));
    }
}
