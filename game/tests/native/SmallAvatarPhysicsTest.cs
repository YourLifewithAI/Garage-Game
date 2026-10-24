using Godot;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Navigation;
using GarageGame.Native.Room;

namespace GarageGame.Tests;

/// <summary>
/// Actual physics-world checks for the 10 cm player and the companion body. No rooms or saves are changed.
/// Pass "-- --jitter-spike" to also measure the jitter scenarios with the whole world scaled ×10.
/// </summary>
public partial class SmallAvatarPhysicsTest : Node3D
{
    private int _checks;
    private int _failures;
    private SmallPlayerController _player = null!;
    private CompanionAvatar _companion = null!;
    private RoomNavigation _navigation = null!;
    private static readonly GDScript Physics = GD.Load<GDScript>(SmallPlayerController.WorldPhysicsScript);

    public override async void _Ready()
    {
        try
        {
            BuildFloorAndObstacles();
            _player = new SmallPlayerController { Name = "SmallPlayer", ReadKeyboard = false, Position = new Vector3(-2, 0.003f, 2) };
            AddChild(_player);
            _companion = new CompanionAvatar { Name = "Companion", Position = new Vector3(2, 0.01f, 3.5f) };
            Check(_companion.ConfigureIdentity("test_companion"), "identity configurable before scene entry");
            AddChild(_companion);
            _companion.BindPlayer(_player);
            // The companion's walkable map over the test floor, baked from its static collision as in the room.
            _navigation = RoomNavigation.Create(this, this, new Aabb(new Vector3(-5, 0, -6), new Vector3(10, 0.6f, 12)),
                WorldScaleProfile.Companion, _companion.StepHeightM * 0.75f);
            _companion.BindNavigation(_navigation);
            await Frames(30);

            Check(Mathf.Abs(_player.BodyHeightM - 0.10f) < 0.00001f, "player collision height is exactly 0.10 m");
            Check(Mathf.Abs(_player.BodyRadiusM - 0.02f) < 0.00001f, "player collision radius is 0.02 m");
            Check(Mathf.Abs(_player.EyeCamera.Position.Y - 0.087f) < 0.00001f, "eye is 0.087 m above feet");
            Check(Mathf.Abs(_player.ReachM - 0.15f) < 0.00001f, "reach is 0.15 m");
            Check(_player.EyeCamera.Near <= 0.006f && _player.FloorSnapLength <= 0.02f && _player.SafeMargin <= 0.001f,
                "camera near plane, floor snap and safe margin suit a 2 cm capsule");
            Check(_player.IsOnFloor() && Mathf.Abs(_player.GlobalPosition.Y) < 0.004f, "small capsule rests on actual floor");
            Check(_player.WorldPhysicsId == "room_tuned" && Mathf.IsEqualApprox(_player.GravityMps2, 3.5f), "body starts with the tuned room gravity profile");
            Check(!_companion.ConfigureIdentity("replacement"), "identity cannot reset after scene entry");
            // Founder decision, 6 October: the companion shrinks to match the player, but keeps its own profile object.
            var companionProfile = WorldScaleProfile.Companion;
            Check(Mathf.Abs(_companion.BodyHeightM - 0.10f) < 0.00001f && Mathf.Abs(_companion.BodyRadiusM - 0.02f) < 0.00001f &&
                Mathf.Abs(_companion.EyeCamera.Position.Y - 0.087f) < 0.00001f && Mathf.Abs(_companion.ReachM - 0.15f) < 0.00001f,
                "the companion is a 10 cm body like the player's: radius 2 cm, eye 8.7 cm, reach 15 cm");
            Check(companionProfile.IsValid && !ReferenceEquals(companionProfile, WorldScaleProfile.SmallPlayer),
                "the companion keeps a profile object of its own, separate from the player's");
            Check(Mathf.IsEqualApprox(_companion.StepHeightM, _player.StepHeightM) && Mathf.IsEqualApprox(_companion.FloorSnapM, _player.FloorSnapM) &&
                Mathf.IsEqualApprox(_companion.JumpApexM, _player.JumpApexM) && Mathf.IsEqualApprox(_companion.MaxJumpApexM, _player.MaxJumpApexM) && Mathf.IsEqualApprox(_companion.FloorSnapLength, _player.FloorSnapLength),
                "the companion steps, snaps and jumps like the player");
            Check(_companion.RunSpeedMps >= 1.2f * _player.RunSpeedMps * 1.2f, $"the companion's run outpaces the player's, Quick feet at full rank included, so follow can catch up ({_companion.RunSpeedMps:0.00} against {_player.RunSpeedMps:0.00} m/s, {_player.RunSpeedMps * 1.2f:0.000} with the knack)");
            Check(Mathf.IsEqualApprox(_companion.RunSpeedMps, 1.4f) && Mathf.IsEqualApprox(_companion.RunSpeedMps / _companion.GroundAccelerationMps2, 0.16f, 0.002f) && Mathf.IsEqualApprox(_player.RunSpeedMps / _player.GroundAccelerationMps2, 0.16f, 0.002f),
                $"the Gubble runs at about 1.4 m/s and, like the player, reaches a full run in 0.16 s ({_companion.RunSpeedMps:0.00} m/s over {_companion.GroundAccelerationMps2:0.00} m/s², the player's {_player.RunSpeedMps / _player.GroundAccelerationMps2:0.000} s)");
            var visualFits = true;
            foreach (var child in _companion.GetNode<Node3D>("OriginalPrototypeBody").GetChildren())
            {
                if (child is not MeshInstance3D mesh) continue;
                var bounds = mesh.Transform * mesh.Mesh.GetAabb();
                visualFits &= bounds.Position.Y >= -0.001f && bounds.End.Y <= _companion.BodyHeightM + 0.001f &&
                    Mathf.Max(Mathf.Abs(bounds.Position.X), Mathf.Abs(bounds.End.X)) <= _companion.BodyRadiusM + 0.001f &&
                    Mathf.Max(Mathf.Abs(bounds.Position.Z), Mathf.Abs(bounds.End.Z)) <= _companion.BodyRadiusM + 0.001f;
            }
            Check(visualFits, "companion body and hat fit the declared collision height and radius");
            Check(PlayerVisualFits(), "player body meshes fit the 0.10 m capsule");
            _companion.SetAppearance(new Color("d087bd"));
            _companion.SetDisplayName("Juniper");
            Check(_companion.CompanionId == "test_companion" && _companion.CompanionName == "Juniper", "appearance/name do not replace companion identity");

            await TestMovementAndJump();
            await TestLandingImpacts();
            await TestBodyHooks();
            await TestKeyboardActions();
            await TestObstacles();
            await TestDioramaCamera();
            await TestCompanion();
            await TestNavigation();
            await TestGroundContact();
            await TestClimbing();
            await TestSwimming();
            await TestClimbableTree();
            await TestGubbleFloats();
            await TestGubbleBesideAClimber();
            await TestGubbleOffATallLedge();
            await TestGubbleLetsAHeldHeightGo();
            await TestGubbleStuckOverARoof();
            await TestGubbleRisesATallFace();
            await TestGubbleCatchesUp();
            await TestGubbleWalksAgainOnALongTrip();
            await TestExertion();
            await TestClimbAcrossASeam();
            await TestSeaEdge();
            var metrics = await RunJitterSuite(1.0f, new Vector3(0, 0, 30));
            CheckJitter(metrics);
            if (OS.GetCmdlineUserArgs().Contains("--jitter-spike"))
            {
                var scaled = await RunJitterSuite(10.0f, new Vector3(0, 0, 200));
                GD.Print(JitterReport(metrics, scaled));
            }
            GD.Print($"NATIVE_SMALL_AVATAR: {_checks - _failures}/{_checks} checks passed; 10 cm body, room gravity profiles, steps, climbing, swimming, jitter and companion steering; not final art, feel or AI integration");
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (System.Exception exception)
        {
            GD.PushError("Native small-avatar test exception: " + exception);
            GetTree().Quit(1);
        }
    }

    /// <summary>Actual 1.5 m landings report the final descent speed, including slow air, late gliders and deep water.</summary>
    private async Task TestLandingImpacts()
    {
        var fixture = new Node3D { Name = "LandingImpactFixture", Position = new Vector3(20, 0, 20) };
        AddChild(fixture);
        var floor = new StaticBody3D { Position = new Vector3(0, -0.05f, 0), CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(4, 0.1f, 4) } });
        fixture.AddChild(floor);
        var body = new SmallPlayerController { Name = "LandingBody", ReadKeyboard = false, Position = new Vector3(0, 1.5f, 0) };
        fixture.AddChild(body);
        var speed = 0f;
        var damage = 0f;
        var observations = 0;
        var deepWater = false;
        void ObserveLanding(float landingSpeedMps, bool deep)
        {
            speed = Mathf.Max(speed, landingSpeedMps);
            damage = Mathf.Max(damage, PlayerVitals.FallDamage(landingSpeedMps, deep));
            observations++;
            deepWater |= deep;
        }
        body.FallObserved = ObserveLanding;
        void StartFall()
        {
            body.GlobalPosition = fixture.GlobalPosition + new Vector3(0, 1.5f, 0);
            body.Velocity = Vector3.Zero;
            body.ResetVitalsObservation();
            speed = 0;
            damage = 0;
            observations = 0;
            deepWater = false;
        }
        try
        {
            await Frames(100);
            Check(observations > 0 && body.IsOnFloor() && Mathf.Abs(speed - Mathf.Sqrt(2 * 3.5f * 1.5f)) < 0.08f && Mathf.Abs(damage - 96) < 4,
                $"a real default-gravity 1.5 m landing reports impact before the floor stops it (speed {speed:0.000} m/s, damage {damage:0.0})");
            var damageAtRest = damage;
            body.FallObserved = (landingSpeedMps, deep) => damage += PlayerVitals.FallDamage(landingSpeedMps, deep);
            await Frames(30);
            Check(damage == damageAtRest, "standing after an impact does not repeat its damage");
            body.FallObserved = ObserveLanding;

            Check(body.SetWorldPhysics(Preset("room_floaty", body.WorldPhysicsRevision + 1)), "the landing fixture accepts low gravity");
            StartFall();
            await Frames(240);
            Check(observations > 0 && body.IsOnFloor() && speed > 0 && speed <= 0.601f && damage == 0,
                $"the same 1.5 m fall in floaty air lands safely (speed {speed:0.000} m/s, damage {damage:0.0})");

            Check(body.SetWorldPhysics(Preset("room_tuned", body.WorldPhysicsRevision + 1)), "the landing fixture restores default gravity");
            StartFall();
            for (var i = 0; i < 100 && body.GlobalPosition.Y > fixture.GlobalPosition.Y + 0.5f; i++) await Frames(1);
            Check(-body.Velocity.Y > PlayerVitals.SafeLandingSpeedMps, "a glider opened late first has an unsafe descent to slow");
            body.SetCreationEffects(Vector3.Zero, 0.5f, new Callable(this, MethodName.AllowAll));
            await Frames(120);
            Check(observations > 0 && body.IsOnFloor() && speed > 0 && speed <= 0.501f && damage == 0,
                $"a late glider makes the same 1.5 m fall safe by reducing the landing speed (speed {speed:0.000} m/s, damage {damage:0.0})");
            body.ClearCreationMotion();

            var sheet = new SurfaceTool();
            sheet.Begin(Mesh.PrimitiveType.Triangles);
            foreach (var corner in new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, -1), new Vector2(1, 1), new Vector2(-1, 1) })
                sheet.AddVertex(new Vector3(corner.X, 0.2f, corner.Y));
            fixture.AddChild(RoomWater.CreateCollider(new[] { ((Shape3D)sheet.Commit().CreateTrimeshShape(), Transform3D.Identity) }));
            StartFall();
            await Frames(100);
            Check(observations > 0 && deepWater && body.IsSwimming && damage == 0,
                $"deep water catches a real high-speed fall without injury (observed speed {speed:0.000} m/s)");
        }
        finally
        {
            fixture.QueueFree();
            await Frames(2);
        }
    }

    /// <summary>
    /// The body's keys are input actions (game/project.godot): the held keys walk, run and jump through the map as W, A, S, D, Shift
    /// and Space did, the presses (R, G, B) act as before, and a remapped action takes over from its old key. A second body reads
    /// the keyboard here (the player of these tests is steered by code); the keys go in through Input as the engine would send them.
    /// </summary>
    private async Task TestKeyboardActions()
    {
        var saved = PlayerControls.MapActions().ToDictionary(action => action, action => InputMap.ActionGetEvents(action));
        var body = new SmallPlayerController { Name = "KeyboardBody", Position = new Vector3(3.5f, 0.003f, -3f) };
        AddChild(body);
        var physicsAsked = new List<string>();
        body.WorldPhysicsRequest = preset => { physicsAsked.Add(preset); return true; };
        var down = new HashSet<Key>();
        void Feed(Key key, bool pressed)
        {
            if (pressed) down.Add(key); else down.Remove(key);
            Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = pressed });
        }
        void Bind(string action, Key key)
        {
            InputMap.ActionEraseEvents(action);
            InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = key });
            PlayerControls.MapChanged();
        }
        // Hold keys for a while, let go, and report how far the body went.
        async Task<Vector3> Hold(int frames, params Key[] keys)
        {
            var start = body.GlobalPosition;
            foreach (var key in keys) Feed(key, true);
            await Frames(frames);
            foreach (var key in keys) Feed(key, false);
            await Frames(25);
            return body.GlobalPosition - start;
        }
        async Task<float> Leap(Key key)
        {
            var top = body.GlobalPosition.Y;
            Feed(key, true);
            for (var i = 0; i < 25; i++) { await Frames(1); top = Mathf.Max(top, body.GlobalPosition.Y); }
            Feed(key, false);
            await Frames(40);
            return top - body.GlobalPosition.Y;
        }
        try
        {
            await Frames(30);
            Check(body.IsOnFloor() && body.ReadKeyboard, "a body that reads the keyboard stands on the test floor");
            var (forward, back, left, right) = (await Hold(30, Key.W), await Hold(30, Key.S), await Hold(30, Key.A), await Hold(30, Key.D));
            Check(forward.Z < -0.1f && Mathf.Abs(forward.X) < 0.01f && back.Z > 0.1f && Mathf.Abs(back.X) < 0.01f &&
                left.X < -0.1f && Mathf.Abs(left.Z) < 0.01f && right.X > 0.1f && Mathf.Abs(right.Z) < 0.01f,
                $"W, S, A and D walk forward, back, left and right through the move actions (W {forward.Z:0.000}, S {back.Z:0.000}, A {left.X:0.000}, D {right.X:0.000} m)");
            var walked = (await Hold(30, Key.W)).Length();
            var ran = (await Hold(30, Key.W, Key.Shift)).Length();
            Check(ran > walked * 1.8f, $"Shift runs: {ran:0.000} m in half a second against {walked:0.000} m");
            var apex = await Leap(Key.Space);
            Check(apex > 0.03f && body.IsOnFloor(), $"Space jumps ({apex * 100:0.0} cm) and the body lands");

            // A move action on another key: the new key walks, the old one does not.
            Bind("move_forward", Key.Y);
            var newKey = await Hold(30, Key.Y);
            var oldKey = await Hold(30, Key.W);
            Check(newKey.Z < -0.1f && oldKey.Length() < 0.01f, $"with move_forward on Y, Y walks ({newKey.Z:0.000} m) and W does nothing ({oldKey.Length():0.000} m)");
            Bind("jump", Key.X);
            var jumpNew = await Leap(Key.X);
            var jumpOld = await Leap(Key.Space);
            Check(jumpNew > 0.03f && jumpOld < 0.005f, $"with jump on X, X jumps ({jumpNew * 100:0.0} cm) and Space does not ({jumpOld * 100:0.0} cm)");
            Bind("move_sprint", Key.Z);
            var walkedAgain = (await Hold(30, Key.Y, Key.Shift)).Length();
            var ranAgain = (await Hold(30, Key.Y, Key.Z)).Length();
            Check(ranAgain > walkedAgain * 1.8f, $"with sprint on Z, Z runs ({ranAgain:0.000} m) and Shift does not ({walkedAgain:0.000} m)");
            foreach (var (action, events) in saved)
            {
                InputMap.ActionEraseEvents(action);
                foreach (var input in events) InputMap.ActionAddEvent(action, input);
            }
            PlayerControls.MapChanged();

            // The presses: R recovers, G asks for the next world physics, B starts the way home; each on its own key once remapped.
            var recoveries = body.Recoveries;
            Feed(Key.R, true); Feed(Key.R, false);
            await Frames(2);
            Check(body.Recoveries == recoveries + 1, "R recovers");
            Bind("body_recover", Key.K);
            Feed(Key.R, true); Feed(Key.R, false); await Frames(2);
            var withoutIt = body.Recoveries;
            Feed(Key.K, true); Feed(Key.K, false); await Frames(2);
            Check(withoutIt == recoveries + 1 && body.Recoveries == recoveries + 2, "with recover on K, R does nothing and K recovers");
            Feed(Key.G, true); Feed(Key.G, false);
            await Frames(2);
            Check(physicsAsked.Count == 1, "G asks for the next world physics");
            Bind("physics_next", Key.N);
            Feed(Key.G, true); Feed(Key.G, false); await Frames(2);
            var gOnly = physicsAsked.Count;
            Feed(Key.N, true); Feed(Key.N, false); await Frames(2);
            Check(gOnly == 1 && physicsAsked.Count == 2, "with it on N, G asks for nothing and N asks");
            var echo = new InputEventKey { PhysicalKeycode = Key.N, Pressed = true, Echo = true };
            body._UnhandledInput(echo);
            Check(physicsAsked.Count == 2, "a held key repeating (echo) is not a press");
            Feed(Key.B, true); Feed(Key.B, false);
            await Frames(2);
            Check(body.GoingHome, "B starts the way home");
            for (var i = 0; i < 400 && body.GoingHome; i++) await Frames(1);
            Check(!body.GoingHome, "and the way home ends");
        }
        finally
        {
            foreach (var key in down.ToArray()) Feed(key, false);
            foreach (var (action, events) in saved)
            {
                InputMap.ActionEraseEvents(action);
                foreach (var input in events) InputMap.ActionAddEvent(action, input);
            }
            PlayerControls.MapChanged();
            body.QueueFree();
            await Frames(2);
        }
    }

    private async Task TestMovementAndJump()
    {
        _player.SetControlInput(Vector2.Right);
        var start = _player.GlobalPosition;
        await Frames(60);
        _player.SetControlInput(Vector2.Zero);
        var walked = _player.GlobalPosition.X - start.X;
        Report(walked > 0.27f && walked < 0.34f, $"walking covers about 0.31 m in one second (walked={walked:0.000})");
        await Frames(15);
        start = _player.GlobalPosition;
        _player.SetControlInput(Vector2.Left, sprint: true);
        await Frames(60);
        _player.SetControlInput(Vector2.Zero);
        var ran = start.X - _player.GlobalPosition.X;
        Check(_player.RunSpeedMps >= 2.8f * _player.WalkSpeedMps, $"the run is about three times the walk ({_player.RunSpeedMps / _player.WalkSpeedMps:0.00}x)");
        Report(ran > 0.84f && ran < 0.97f, $"running covers about 0.88 m in one second from a standstill (ran={ran:0.000})");
        // Crisp: full run speed within 0.2 s, and a stop from a run within 0.2 s.
        await Frames(20);
        _player.SetControlInput(Vector2.Right, sprint: true);
        var rampFrames = 0;
        for (var i = 0; i < 60 && _player.Velocity.Length() < _player.RunSpeedMps * 0.99f; i++) { await Frames(1); rampFrames++; }
        await Frames(10);
        _player.SetControlInput(Vector2.Zero);
        var stopFrames = 0;
        for (var i = 0; i < 60 && new Vector2(_player.Velocity.X, _player.Velocity.Z).Length() > 0.001f; i++) { await Frames(1); stopFrames++; }
        Report(rampFrames >= 6 && rampFrames <= 12 && stopFrames >= 6 && stopFrames <= 12, $"the run reaches speed and stops within 0.2 s (ramp={rampFrames} frames, stop={stopFrames} frames)");
        await Frames(20);

        var (apex, airtime) = await MeasureJump();
        Check(apex > 0.058f && apex < 0.072f && Mathf.Abs(_player.JumpApexNowM - _player.JumpApexM) < 0.0005f, $"tuned jump rises about 6.5 cm, clearing a 4 cm book (apex={apex:0.0000})");
        GD.Print($"SMALL_AVATAR_MEASURED jump room_tuned: gravity {_player.GravityMps2:0.0} m/s2, apex {apex * 100:0.0} cm, airtime {airtime / 60.0:0.00} s ({airtime} ticks; predicted {_player.JumpApexNowM * 100:0.0} cm, {_player.JumpAirtimeS:0.00} s)");
        Report(airtime >= 19 && airtime <= 27, $"tuned jump lasts about 0.38 s (airborne frames={airtime})");
        Check(_player.IsOnFloor(), "jump returns to stable floor");

        var revision = _player.WorldPhysicsRevision;
        Check(_player.SetWorldPhysics(Preset("room_real", revision + 1)) && Mathf.IsEqualApprox(_player.GravityMps2, 9.8f), "real gravity profile accepted with a newer revision");
        (apex, airtime) = await MeasureJump();
        Check(apex > 0.058f && apex < 0.072f, $"real gravity, heavier than the default, keeps the 6.5 cm jump (apex={apex:0.0000})");
        GD.Print($"SMALL_AVATAR_MEASURED jump room_real: gravity {_player.GravityMps2:0.0} m/s2, apex {apex * 100:0.0} cm, airtime {airtime / 60.0:0.00} s ({airtime} ticks; predicted {_player.JumpApexNowM * 100:0.0} cm, {_player.JumpAirtimeS:0.00} s)");
        Report(airtime >= 11 && airtime <= 17, $"real gravity makes the same jump last about 0.23 s (airborne frames={airtime})");
        Check(!_player.SetWorldPhysics(Preset("room_floaty", revision + 1)), "a stale physics revision is refused");
        var invalid = Preset("room_tuned", revision + 5);
        invalid["gravity_mps2"] = 0.3;
        Check(!_player.SetWorldPhysics(invalid), "gravity below the room bound is refused");
        invalid = Preset("room_tuned", revision + 5);
        invalid["gravity_mps2"] = double.NaN;
        Check(!_player.SetWorldPhysics(invalid), "non-finite gravity is refused");
        invalid = Preset("room_breeze_test", revision + 5);
        invalid["wind_x_mps"] = 3.0;
        Check(!_player.SetWorldPhysics(invalid), "unbounded wind is refused");
        invalid = Preset("room_floaty", revision + 5);
        invalid["terminal_fall_mps"] = 0.1;
        Check(!_player.SetWorldPhysics(invalid), "a fall limit below the room bound is refused");
        invalid = Preset("room_floaty", revision + 5);
        invalid["air_control"] = 9.0;
        Check(!_player.SetWorldPhysics(invalid), "unbounded air control is refused");
        Check(_player.CycleWorldPhysics() == "room_floaty", "the next preset after real is floaty (the G key sends it through the command host in the room)");
        Check(Mathf.IsEqualApprox(_player.GravityMps2, 0.6f) && Mathf.IsEqualApprox(_player.EffectiveTerminalFallMps, 0.6f) && Mathf.IsEqualApprox(_player.AirControl, 2.0f),
            "floaty is 0.6 m/s2 with a 0.6 m/s fall limit and doubled air control");
        await Frames(2);
        Check(_companion.WorldPhysicsId == "room_floaty" && Mathf.IsEqualApprox(_companion.GravityMps2, 0.6f), "the companion lives under the same world gravity as the player");
        (apex, airtime) = await MeasureJump();
        // Founder, 8 October: "I do want to be able to jump higher in the low gravity mode". Lighter gravity than the default
        // lets the body leap higher, as on the moon; floaty's would be 38 cm, so the body's 30 cm cap holds it.
        Check(apex > 0.27f && apex <= _player.MaxJumpApexM + 0.005f && Mathf.Abs(_player.JumpApexNowM - _player.MaxJumpApexM) < 0.001f,
            $"floaty gravity leaps to the body's 30 cm cap, three body heights (apex={apex:0.0000})");
        GD.Print($"SMALL_AVATAR_MEASURED jump room_floaty: gravity {_player.GravityMps2:0.0} m/s2, apex {apex * 100:0.0} cm, airtime {airtime / 60.0:0.00} s ({airtime} ticks; predicted {_player.JumpApexNowM * 100:0.0} cm, {_player.JumpAirtimeS:0.00} s)");
        Report(airtime >= 105 && airtime <= 130, $"the floaty leap lasts about 2 s (airborne frames={airtime})");
        // A fall from 40 cm drifts down at the floaty fall limit instead of accelerating.
        _player.GlobalPosition += Vector3.Up * 0.40f;
        var fastest = 0.0f;
        var fallFrames = 0;
        await Frames(1);
        for (var i = 0; i < 240 && !_player.IsOnFloor(); i++) { await Frames(1); fallFrames++; fastest = Mathf.Max(fastest, -_player.Velocity.Y); }
        Report(fastest <= 0.601f && fastest > 0.55f && fallFrames > 60, $"a floaty fall is capped at 0.6 m/s (fastest={fastest:0.000} m/s over {fallFrames} frames)");
        await Frames(10);
        Check(_player.CycleWorldPhysics() == "room_tuned", "the presets cycle real, floaty and tuned");
        Check(Mathf.IsEqualApprox(_player.GravityMps2, 3.5f) && Mathf.IsEqualApprox(_player.EffectiveTerminalFallMps, 6.0f) && Mathf.IsEqualApprox(_player.AirControl, 1.0f),
            "cycling returns to the tuned gravity, fall limit and air control");
        // Between the presets the rule is the moon's: the take-off speed stays the default's, so half the gravity leaps
        // about twice as high (13 cm, under the cap).
        var half = Preset("room_tuned", _player.WorldPhysicsRevision + 1);
        half["id"] = "half_gravity_test";
        half["gravity_mps2"] = 1.75;
        Check(_player.SetWorldPhysics(half), "a half-gravity profile is accepted");
        (apex, airtime) = await MeasureJump();
        Check(apex > 0.12f && apex < 0.14f, $"half the default gravity leaps about twice as high (apex={apex:0.0000})");
        GD.Print($"SMALL_AVATAR_MEASURED jump half gravity: gravity {_player.GravityMps2:0.00} m/s2, apex {apex * 100:0.0} cm, airtime {airtime / 60.0:0.00} s ({airtime} ticks)");
        var halfSpeed = _player.JumpSpeedMps;
        Check(_player.SetWorldPhysics(Preset("room_tuned", _player.WorldPhysicsRevision + 1)) && Mathf.IsEqualApprox(_player.JumpSpeedMps, halfSpeed),
            "half the gravity, the same push off the ground as in the default");
        await Frames(10);

        // A jump pressed just before landing still happens; one pressed after walking off an edge too.
        _player.GlobalPosition += Vector3.Up * 0.05f;
        await Frames(4);
        var jumpsBefore = _player.JumpsStarted;
        var buffered = false;
        for (var i = 0; i < 30 && !buffered; i++)
        {
            if (!_player.IsOnFloor() && _player.GlobalPosition.Y < 0.012f) { _player.SetControlInput(Vector2.Zero, jump: true); buffered = true; }
            await Frames(1);
        }
        await Frames(8);
        Check(buffered && _player.JumpsStarted == jumpsBefore + 1, "a jump pressed just before landing is buffered, not dropped");
        await Frames(40);

        _player.SetInputEnabled(false);
        start = _player.GlobalPosition;
        _player.SetControlInput(Vector2.Right, true);
        await Frames(25);
        Check(_player.GlobalPosition.DistanceTo(start) < 0.002f, "disabled input blocks movement and jump immediately");
        _player.SetInputEnabled(true);
        _player.SetControlInput(new Vector2(float.NaN, 1));
        await Frames(3);
        Check(_player.GlobalPosition.IsFinite(), "nonfinite control input is rejected");
        _player.SetControlInput(Vector2.Zero);
    }

    private async Task<(float Apex, int Airtime)> MeasureJump()
    {
        await Frames(5);
        var baseY = _player.GlobalPosition.Y;
        _player.SetControlInput(Vector2.Zero, true);
        var highest = baseY;
        var airborne = 0;
        var left = false;
        for (var i = 0; i < 180; i++)
        {
            await Frames(1);
            highest = Mathf.Max(highest, _player.GlobalPosition.Y);
            if (!_player.IsOnFloor()) { airborne++; left = true; }
            else if (left) break;
        }
        await Frames(5);
        return (highest - baseY, airborne);
    }

    private async Task TestObstacles()
    {
        // 1.5 cm curb: a step, no jump needed.
        Check(_player.TryTeleportTo(new Vector3(-0.5f, 0.003f, 0)), "curb route has supported spawn");
        _player.Rotation = Vector3.Zero;
        var steps = _player.StepsClimbed;
        var grabs = _player.Grabs;
        _player.SetControlInput(Vector2.Right, sprint: true);
        var highest = 0.0f;
        for (var i = 0; i < 120; i++) { await Frames(1); highest = Mathf.Max(highest, _player.GlobalPosition.Y); }
        _player.SetControlInput(Vector2.Zero);
        Check(_player.GlobalPosition.X > 0.45f, $"crosses a 1.5 cm curb without a jump (position={_player.GlobalPosition}, floor={_player.IsOnFloor()})");
        Check(highest > 0.012f && highest < 0.03f && _player.StepsClimbed > steps, $"curb traversal uses bounded step lift (height={highest}, steps={_player.StepsClimbed - steps})");
        Check(_player.Grabs == grabs && !_player.IsClimbing, "walking into a step it can step up never grabs it as a climb");
        await Frames(15);

        // The step and the jump, as before climbing: with climbing off, the 3 cm barrier and the 4 cm book block a walker
        // (a sustained push now climbs them; TestClimbing covers that).
        _player.CanClimb = false;
        // 3 cm barrier: above the 2 cm step, so it blocks unless jumped.
        Check(_player.TryTeleportTo(new Vector3(-0.5f, 0.003f, -0.6f)), "barrier route has supported spawn");
        _player.SetControlInput(Vector2.Right);
        await Frames(90);
        _player.SetControlInput(Vector2.Zero);
        Check(_player.GlobalPosition.X < -0.11f && _player.GlobalPosition.Y < 0.008f, "a 3 cm barrier cannot masquerade as a step");

        // The 4 cm book: blocked when walking, climbed with a jump.
        Check(_player.TryTeleportTo(new Vector3(-0.5f, 0.003f, -1.4f)), "book route has supported spawn");
        _player.SetControlInput(Vector2.Right);
        await Frames(90);
        Check(_player.GlobalPosition.X < -0.12f && _player.GlobalPosition.Y < 0.008f, $"walking into the 4 cm book stops at its side (position={_player.GlobalPosition})");
        _player.SetControlInput(Vector2.Right, jump: true);
        await Frames(2);
        _player.SetControlInput(Vector2.Right);
        await Frames(25);
        _player.SetControlInput(Vector2.Zero);
        await Frames(20);
        Check(_player.IsOnFloor() && Mathf.Abs(_player.GlobalPosition.Y - 0.04f) < 0.006f && _player.GlobalPosition.X > -0.11f,
            $"a running jump lands on top of the 4 cm book (position={_player.GlobalPosition})");
        // Coyote time: walk off the book's far side and jump just after leaving it.
        _player.SetControlInput(Vector2.Right);
        var coyote = false;
        var jumps = _player.JumpsStarted;
        for (var i = 0; i < 120 && !coyote; i++)
        {
            await Frames(1);
            if (!_player.IsOnFloor() && _player.GlobalPosition.Y > 0.03f) { _player.SetControlInput(Vector2.Right, jump: true); coyote = true; }
        }
        await Frames(3);
        _player.SetControlInput(Vector2.Zero);
        Check(coyote && _player.JumpsStarted == jumps + 1, "a jump just after walking off an edge still happens (coyote time)");
        await Frames(40);
        _player.CanClimb = true;

        // The 6 mm rug: walked onto and off without a jump or a stumble.
        Check(_player.TryTeleportTo(new Vector3(-0.9f, 0.003f, -2.2f)), "rug route has supported spawn");
        _player.SetControlInput(Vector2.Right);
        var airborne = 0;
        var onRug = false;
        for (var i = 0; i < 360; i++)
        {
            await Frames(1);
            if (!_player.IsOnFloor()) airborne++;
            onRug |= _player.GlobalPosition.Y > 0.004f && _player.IsOnFloor();
        }
        _player.SetControlInput(Vector2.Zero);
        Check(_player.GlobalPosition.X > 0.6f && onRug && airborne <= 6, $"walks over a 6 mm rug and off again (x={_player.GlobalPosition.X:0.00}, airborne frames={airborne})");

        // Clearances: a 12 cm underpass admits the body, an 8 cm one does not.
        Check(_player.TryTeleportTo(new Vector3(-1, 0.003f, 1.2f)), "underpass route has supported spawn");
        _player.SetControlInput(Vector2.Right, sprint: true);
        await Frames(220);
        _player.SetControlInput(Vector2.Zero);
        Check(_player.GlobalPosition.X > 0.65f && _player.GlobalPosition.Y < 0.008f, $"passes beneath a 12 cm clearance deck (position={_player.GlobalPosition})");
        Check(_player.TryTeleportTo(new Vector3(0, 0.165f, 1.2f)), "can explicitly recover onto bridge deck");
        Check(Mathf.Abs(_player.GlobalPosition.Y - 0.16f) < 0.006f, "deck uses structural collider, not floor height");
        Check(_player.TryTeleportTo(new Vector3(0, 0.003f, 1.2f)), "can explicitly recover below the same deck");
        Check(_player.GlobalPosition.Y < 0.008f, "underpass recovery does not snap to deck overhead");
        var start = _player.GlobalPosition;
        Check(!_player.TryTeleportTo(new Vector3(0, 0.003f, -2.8f)), "8 cm head clearance rejects a 10 cm body");
        Check(_player.GlobalPosition == start, "rejected recovery does not move the player");
        Check(_player.TryTeleportTo(new Vector3(-2, 0.003f, 3.2f)), "recovery checkpoint available");
        _player.SetSpawnPoint(_player.GlobalPosition);
        _player.GlobalPosition = new Vector3(100, -20, 100);
        Check(_player.Recover() && _player.GlobalPosition.DistanceTo(new Vector3(-2, 0.003f, 3.2f)) < 0.01f,
            "recovery restores a checked supported point");
        await Frames(10);

        Check(_player.TryTeleportTo(new Vector3(12, 0.10f, 0)), "recovery admits the full capsule on a 30 degree slope");
        await Frames(40);
        Check(_player.IsOnFloor() && Mathf.Abs(_player.GlobalPosition.X - 12) < 0.02f,
            "slope recovery settles without penetration or slide");
        var slopeCheckpoint = _player.GlobalPosition;
        _player.GlobalPosition = new Vector3(100, -20, 100);
        Check(_player.Recover() && _player.GlobalPosition.DistanceTo(slopeCheckpoint) < 0.01f,
            "recovery retains the last safe slope instead of falling back to the flat spawn");

        // A creation guard keeps creation-driven recovery out of refused space.
        Check(_player.TryTeleportTo(new Vector3(-2, 0.003f, 3.2f)), "returns from slope fixture to companion route");
        await Frames(10);
        _player.SetCreationEffects(new Vector3(0.4f, 0, 0), 0, new Callable(this, MethodName.GuardKeepsXBelowMinusOne));
        await Frames(90);
        Check(_player.GlobalPosition.X <= -1.0f && _player.GlobalPosition.X > -1.98f, $"creation push moves the body but never past its guard (x={_player.GlobalPosition.X:0.000})");
        Check(_player.CreationVelocity.Length() <= _player.MaxCreationSpeedMps + 0.001f, "creation speed stays within the body's cap");
        _player.SetCreationEffects(Vector3.Zero, 0, new Callable());
        Check(_player.CreationVelocity == Vector3.Zero && !_player.HasCreationGuard, "an invalid guard removes every creation contribution at once");
        start = _player.GlobalPosition;
        await Frames(20);
        Check(Mathf.Abs(_player.GlobalPosition.X - start.X) < 0.003f, "no creation-induced drift after the effect is removed");
        // A wall just inside the refused side (Codex's reviews): the tick that crosses the guard ends against it, and the slide
        // had already cut the push away, so nothing was taken back. Idle, pushed by a creation alone, it must stay out.
        Box(new Vector3(-0.922f, 0.05f, 5.0f), new Vector3(0.1f, 0.1f, 0.3f));
        Check(_player.TryTeleportTo(new Vector3(-1.3f, 0.003f, 5.0f)), "the player before the wall beyond its guard");
        await Frames(10);
        var crossedTo = float.NegativeInfinity;
        for (var i = 0; i < 150; i++)
        {
            _player.SetCreationEffects(new Vector3(4.0f, 0, 0), 0, new Callable(this, MethodName.GuardKeepsXBelowMinusOne));
            await Frames(1);
            crossedTo = Mathf.Max(crossedTo, _player.GlobalPosition.X);
        }
        _player.SetCreationEffects(Vector3.Zero, 0, new Callable());
        Report(crossedTo <= -1.0f + 0.0005f, $"a creation push never carries the body past its guard, even into a wall just beyond it (furthest x {crossedTo:0.0000}; the guard at -1.0000, the wall stops the body at about -0.995)");
        Check(_player.TryTeleportTo(new Vector3(-2, 0.003f, 3.2f)), "back to the companion route");
        await Frames(10);
    }

    public bool GuardKeepsXBelowMinusOne(Vector3 position) => position.X <= -1.0f;

    /// <summary>The sea test's room bounds (3 m round the island at (0, 0, -80)), as the creation authority refuses past them.</summary>
    public bool InsideTheOldBounds(Vector3 position) => Mathf.Abs(position.X) <= 3.0f && Mathf.Abs(position.Z + 80) <= 3.0f;

    private async Task TestCompanion()
    {
        Check(_companion.TryTeleportTo(new Vector3(0.5f, 0.01f, 3.2f)), "companion gets its own clear spawn");
        _player.Rotation = Vector3.Zero;
        _companion.Follow();
        await Frames(240);
        var gap = PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition);
        Report(gap >= CompanionAvatar.FollowNearM - 0.01f && gap <= CompanionAvatar.FollowFarM + 0.01f && !_companion.FollowMoving,
            $"follow comes to rest inside the comfortable band (gap={gap:0.000} m)");
        var (along, lateral) = Offset(-_player.GlobalBasis.Z);
        Check(Mathf.Abs(lateral) > Mathf.Abs(along), $"follow rests beside the player, not behind (along={along:0.000}, lateral={lateral:0.000})");

        // Founder playtest: "it always moves directly behind me". Turning on the spot must not re-target follow.
        var rest = _companion.GlobalPosition;
        for (var i = 0; i < 90; i++) { _player.Rotation = new Vector3(0, _player.Rotation.Y + Mathf.Pi / 60, 0); await Frames(1); }
        await Frames(30);
        Check(PlanarDistance(_companion.GlobalPosition, rest) < 0.01f, $"turning on the spot does not move the companion (moved={PlanarDistance(_companion.GlobalPosition, rest):0.0000} m)");

        // Walking: it keeps beside the line of travel and never trails directly behind.
        _player.Rotation = Vector3.Zero;
        var side = Mathf.Sign(Offset(Vector3.Forward).Lateral);
        var behind = 0;
        _player.SetControlInput(new Vector2(0, 1));
        for (var i = 0; i < 120; i++)
        {
            await Frames(1);
            (along, lateral) = Offset(Vector3.Forward);
            if (along < -0.08f && Mathf.Abs(lateral) < 0.06f) behind++;
        }
        (along, lateral) = Offset(Vector3.Forward);
        Report(behind == 0 && lateral * side > 0.10f && along > -0.08f, $"follow walks beside the player, not behind (behind frames={behind}, along={along:0.000}, lateral={lateral:0.000})");
        // Turning round and walking back: it keeps its side instead of swinging through behind the player.
        var crossed = 0;
        _player.Rotation = new Vector3(0, Mathf.Pi, 0);
        for (var i = 0; i < 120; i++)
        {
            await Frames(1);
            if (Offset(Vector3.Forward).Lateral * side < 0.04f) crossed++;
        }
        _player.SetControlInput(Vector2.Zero);
        (along, lateral) = Offset(Vector3.Back);
        Report(crossed == 0 && along > -0.10f, $"after the player turns back, follow stays on its side and alongside (crossing frames={crossed}, along={along:0.000})");
        // Settling: it comes to rest smoothly, without oscillating back and forth.
        var reversals = 0;
        var previous = Vector3.Zero;
        for (var i = 0; i < 180; i++)
        {
            await Frames(1);
            var velocity = new Vector3(_companion.Velocity.X, 0, _companion.Velocity.Z);
            if (velocity.Length() > 0.02f && previous.Length() > 0.02f && velocity.Dot(previous) < 0) reversals++;
            if (velocity.Length() > 0.02f) previous = velocity;
        }
        gap = PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition);
        Report(reversals == 0 && !_companion.FollowMoving && new Vector2(_companion.Velocity.X, _companion.Velocity.Z).Length() < 0.01f &&
            gap >= CompanionAvatar.FollowNearM - 0.01f && gap <= CompanionAvatar.FollowFarM + 0.01f,
            $"follow settles in the band without oscillating (reversals={reversals}, gap={gap:0.000})");
        _player.Rotation = Vector3.Zero;
        _companion.Come();
        await Frames(80);
        var cameGap = PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition);
        Report(_companion.CurrentIntent == "stay" && cameGap < CompanionAvatar.ComeArrivalM + 0.04f,
            $"come arrives near the player then stays (gap={cameGap:0.000} m)");
        _companion.Follow();
        await Frames(5);
        _companion.Stop();
        var start = _companion.GlobalPosition;
        await Frames(35);
        Check(PlanarDistance(_companion.GlobalPosition, start) < 0.005f && _companion.CurrentIntent == "stop",
            "stop removes ongoing navigation immediately");
        Check(_companion.TryTeleportTo(_player.GlobalPosition + Vector3.Right * (CompanionAvatar.YieldM - 0.02f)), "stop fixture places companion within yielding distance");
        _companion.Stop();
        start = _companion.GlobalPosition;
        await Frames(35);
        Check(PlanarDistance(_companion.GlobalPosition, start) < 0.005f && _companion.CurrentIntent == "stop",
            "explicit stop cancels proximity yielding beside the player");
        var point = _companion.GlobalPosition + Vector3.Right;
        _companion.PointAt(point);
        await Frames(30);
        Check(_companion.IsPointing && (-_companion.GlobalBasis.Z).Dot(Vector3.Right) > 0.9f,
            "point produces oriented visible cue");
        _companion.LookAtPoint(_companion.GlobalPosition + Vector3.Back);
        await Frames(30);
        Check(!_companion.IsPointing && (-_companion.GlobalBasis.Z).Dot(Vector3.Back) > 0.9f,
            "look turns without retaining stale point cue");
        _companion.Stop();

        Check(_companion.TryTeleportTo(_player.GlobalPosition + Vector3.Right * 0.4f), "companion can be placed ahead of player");
        _companion.Stay();
        start = _player.GlobalPosition;
        _player.SetControlInput(Vector2.Right);
        await Frames(65);
        _player.SetControlInput(Vector2.Zero);
        Check(_player.GlobalPosition.X - start.X > 0.28f, "companion cannot trap or block direct player movement");
        Check(_companion.GlobalPosition.IsFinite(), "yielding companion retains finite physical state");

        // Local steering must report an obstructed goal, never teleport through a wall.
        Box(new Vector3(2.0f, 0.20f, 4.75f), new Vector3(0.65f, 0.4f, 0.08f));
        Box(new Vector3(2.0f, 0.20f, 4.10f), new Vector3(0.65f, 0.4f, 0.08f));
        Box(new Vector3(1.675f, 0.20f, 4.425f), new Vector3(0.08f, 0.4f, 0.65f));
        Box(new Vector3(2.325f, 0.20f, 4.425f), new Vector3(0.08f, 0.4f, 0.65f));
        // The Gubble floats over an open pen's walls; a lid makes the pen one it really cannot leave.
        Box(new Vector3(2.0f, 0.41f, 4.425f), new Vector3(0.73f, 0.02f, 0.73f));
        await Frames(2);
        Check(_companion.TryTeleportTo(new Vector3(2, 0.01f, 4.425f)), "blocked-route fixture admits companion safely");
        _companion.Follow();
        var blocked = false;
        for (var i = 0; i < 100; i++) { await Frames(1); blocked |= _companion.GoalBlocked; }
        Check(blocked && _companion.GlobalPosition.X > 1.70f, "obstructed follow reports blocked without crossing wall");
        _companion.Stop();
        Check(_companion.CompanionId == "test_companion", "all commands preserve identity");

        Check(_player.TryTeleportTo(new Vector3(2, 0.003f, -4.5f)), "low-passage destination admits player outside the roof");
        Check(_companion.TryTeleportTo(new Vector3(-1, 0.01f, -4.5f)), "an 11 cm passage admits the 10 cm companion body");
        await Frames(5);
        _companion.Come();
        blocked = false;
        for (var i = 0; i < 260; i++) { await Frames(1); blocked |= _companion.GoalBlocked; }
        Check(_companion.GlobalPosition.X > 1.6f && _companion.CurrentIntent == "stay" && !blocked,
            "companion crosses a flat low passage without requiring step headroom");

        Check(_player.TryTeleportTo(new Vector3(-4, 0.003f, 3.2f)) &&
            _companion.TryTeleportTo(new Vector3(-4.8f, 0.01f, 3.42f)), "moving follow fixture admits both bodies");
        _player.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0);
        _companion.Follow();
        _player.SetControlInput(new Vector2(0, 1), sprint: true);
        var maximumGap = 0.0f;
        for (var i = 0; i < 380; i++)
        {
            await Frames(1);
            maximumGap = Mathf.Max(maximumGap, PlanarDistance(_player.Position, _companion.Position));
        }
        var runningGap = PlanarDistance(_player.Position, _companion.Position);
        _player.SetControlInput(Vector2.Zero);
        // It starts 0.83 m behind: the gap must never grow past that, and it ends in its band beside the player.
        Report(maximumGap < 0.9f && runningGap < CompanionAvatar.FollowFarM,
            $"companion catches up during sustained player running instead of falling farther behind (max gap={maximumGap:0.00}, final gap={runningGap:0.00})");
        _companion.Stop();

        // Quick feet at full rank run at 1.152 m/s (a fifth over the plain run): the Gubble, at 1.4, still closes the gap at 0.25 m/s
        // (against 0.05 m/s when its run was 1.2), so the 0.83 m it starts behind is gone within the five seconds.
        Check(_player.TryTeleportTo(new Vector3(-4, 0.003f, 3.2f)) &&
            _companion.TryTeleportTo(new Vector3(-4.8f, 0.01f, 3.42f)), "the Quick feet follow fixture admits both bodies");
        await Frames(5);
        _player.Knacks = new BodyKnacks { WalkRunFactor = 1.2f };
        _player.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0);
        _companion.Follow();
        _player.SetControlInput(new Vector2(0, 1), sprint: true);
        var quickMaximum = 0.0f;
        var quickSpeed = 0.0f;
        for (var i = 0; i < 300; i++)
        {
            await Frames(1);
            quickMaximum = Mathf.Max(quickMaximum, PlanarDistance(_player.Position, _companion.Position));
            quickSpeed = Mathf.Max(quickSpeed, new Vector2(_player.Velocity.X, _player.Velocity.Z).Length());
        }
        var quickGap = PlanarDistance(_player.Position, _companion.Position);
        _player.SetControlInput(Vector2.Zero);
        _player.Knacks = BodyKnacks.None;
        Report(quickSpeed > 1.14f && quickMaximum < 1.0f && quickGap < CompanionAvatar.FollowFarM,
            $"companion keeps up with Quick feet at full rank ({quickSpeed:0.000} m/s): the gap never passed {quickMaximum:0.00} m and ends at {quickGap:0.00} m, inside its band");
        _companion.Stop();
    }

    /// <summary>
    /// Founder playtest: "If I walk behind the big box and summon my comp, it gets stuck on the other side of the
    /// box. It can't work its way around." The companion now routes round furniture on a navigation mesh baked from
    /// the static collision; an unreachable goal is reported as blocked and never crossed.
    /// </summary>
    private async Task TestNavigation()
    {
        Check(_navigation.IsReady && _navigation.PolygonCount > 0,
            $"a navigation mesh is baked from the static collision ({_navigation.PolygonCount} polygons, last bake {_navigation.LastBakeMs:0} ms)");
        // The test room's big box, 35 x 30 x 35 cm, on open floor.
        var boxCentre = new Vector3(-3.5f, 0.15f, -1.5f);
        const float half = 0.175f;
        var revision = _navigation.Revision;
        Box(boxCentre, new Vector3(0.35f, 0.30f, 0.35f));
        var waited = 0;
        for (; waited < 120 && _navigation.Revision == revision; waited++) await Frames(1);
        Report(_navigation.Revision > revision, $"adding the box re-bakes the navigation mesh by itself (after {waited} frames, {_navigation.LastBakeMs:0} ms)");
        var playerSpot = new Vector3(-3.5f, 0.003f, -2.15f);
        var behind = new Vector3(-3.5f, 0.01f, -0.95f);
        var route = _navigation.FindRoute(behind, playerSpot, 0.05f);
        Report(route.Reaches && !route.Direct && route.LengthM > 1.25f, $"the route from behind the box goes round it (length={route.LengthM:0.00} m, points={route.Points.Length})");
        Check(_player.TryTeleportTo(playerSpot) && _companion.TryTeleportTo(behind), "player in front of the box, companion behind it");
        _player.Rotation = Vector3.Zero;

        // The founder's report was that local steering alone stayed stuck behind the box. Without a walkable map the Gubble
        // now floats straight there instead, over the 30 cm box, never through it.
        _companion.BindNavigation(null);
        _companion.Come();
        var reported = false;
        var overBox = 0.0f;
        var intoBox = false;
        for (var i = 0; i < 360 && _companion.CurrentIntent == "come"; i++)
        {
            await Frames(1);
            reported |= _companion.GoalBlocked;
            // Over the box by more than its radius (its rounded foot may round the edge), the body must be above the top.
            if (ClearanceFromSquare(_companion.GlobalPosition, boxCentre, half - _companion.BodyRadiusM) == 0)
            {
                overBox = Mathf.Max(overBox, _companion.GlobalPosition.Y);
                intoBox |= _companion.GlobalPosition.Y < 0.30f - 0.002f;
            }
        }
        var gap = PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition);
        Report(_companion.CurrentIntent == "stay" && gap <= CompanionAvatar.ComeArrivalM + 0.02f && overBox > 0.30f && !intoBox,
            $"without navigation come floats over the box instead of staying stuck behind it as the founder saw (gap={gap:0.00} m, highest over the box {overBox:0.000} m, into it {intoBox})");

        // With navigation it walks round the box and arrives, never entering the box.
        _companion.Stop();
        Check(_companion.TryTeleportTo(behind), "companion back behind the box");
        _companion.BindNavigation(_navigation);
        _companion.Come();
        var routed = false;
        var blockedFrames = 0;
        var frames = 0;
        var entered = false;
        for (; frames < 360 && _companion.CurrentIntent == "come"; frames++)
        {
            await Frames(1);
            routed |= _companion.FollowingRoute;
            if (_companion.GoalBlocked) blockedFrames++;
            entered |= ClearanceFromSquare(_companion.GlobalPosition, boxCentre, half) < _companion.BodyRadiusM - 0.005f;
        }
        gap = PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition);
        Report(_companion.CurrentIntent == "stay" && gap <= CompanionAvatar.ComeArrivalM + 0.02f && routed && blockedFrames == 0 && !entered,
            $"with navigation come walks round the box and arrives (frames={frames}, gap={gap:0.00} m, blocked frames={blockedFrames})");

        // Follow from behind the box: the box in the way counts as outside the band, so it comes round.
        Check(_companion.TryTeleportTo(behind), "companion behind the box again");
        _companion.Follow();
        routed = false;
        entered = false;
        for (var i = 0; i < 300; i++)
        {
            await Frames(1);
            routed |= _companion.FollowingRoute;
            entered |= ClearanceFromSquare(_companion.GlobalPosition, boxCentre, half) < _companion.BodyRadiusM - 0.005f;
        }
        gap = PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition);
        var way = _navigation.FindRoute(_companion.GlobalPosition, _player.GlobalPosition, 0.05f);
        var clear = way.Reaches && way.LengthM <= gap + 0.05f;
        Report(routed && !entered && !_companion.FollowMoving && clear &&
            gap >= CompanionAvatar.FollowNearM - 0.01f && gap <= CompanionAvatar.FollowFarM + 0.01f,
            $"follow from behind the box comes round and rests in view of the player (gap={gap:0.00} m, walk={way.LengthM:0.00} m)");

        // A goal it cannot reach (a closed pen) is reported, and the companion stays inside rather than crossing a wall.
        var pen = new Vector3(-3.5f, 0, 1.0f);
        var penWalls = new (Vector3 Centre, Vector3 Size)[]
        {
            (pen + new Vector3(0, 0.20f, 0.325f), new Vector3(0.65f, 0.4f, 0.08f)),
            (pen + new Vector3(0, 0.20f, -0.325f), new Vector3(0.65f, 0.4f, 0.08f)),
            (pen + new Vector3(-0.325f, 0.20f, 0), new Vector3(0.08f, 0.4f, 0.65f)),
            (pen + new Vector3(0.325f, 0.20f, 0), new Vector3(0.08f, 0.4f, 0.65f)),
        };
        foreach (var (centre, size) in penWalls) Box(centre, size);
        revision = _navigation.Revision;
        for (var i = 0; i < 120 && _navigation.Revision == revision; i++) await Frames(1);
        Check(_companion.TryTeleportTo(pen + new Vector3(0, 0.01f, 0)), "companion inside the walled pen");
        Check(!_navigation.FindRoute(_companion.GlobalPosition, _player.GlobalPosition, CompanionAvatar.ComeArrivalM + 0.05f).Reaches, "the navigation finds no way out of the walled pen");
        // The Gubble floats: with no walk out, it rises over the 40 cm walls (never through them) and comes to the player.
        var floatStarts = _companion.FloatStarts;
        var rises = _companion.RiseTicks;
        _companion.Come();
        var nearest = float.PositiveInfinity;
        var highest = 0.0f;
        var penFrames = 0;
        for (; penFrames < 900 && _companion.CurrentIntent == "come"; penFrames++)
        {
            await Frames(1);
            highest = Mathf.Max(highest, _companion.GlobalPosition.Y);
            // Through a wall is the body's capsule reaching into one, deeper than the body's safe margin (the physics' contact
            // tolerance: sliding over an edge, a body may graze it by a fraction of a millimetre). The capsule's foot is rounded,
            // so rounding a wall's top edge the feet can be under the top while the capsule stays clear of the wall. Measured as
            // a flat-footed cylinder (until 13 October), that read as through whenever the rise's last tick under the top ended
            // within 2 mm of it, which hangs on the phase of the hover's bob, so on how long the tests before this one take: 3 of
            // 8 timings across one bob failed so, the capsule 6.8 mm clear of the wall.
            foreach (var (centre, size) in penWalls)
                nearest = Mathf.Min(nearest, CapsuleClearance(_companion.GlobalPosition, _companion.BodyRadiusM, _companion.BodyHeightM, new Aabb(centre - size / 2, size)));
        }
        gap = PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition);
        Report(_companion.FloatStarts > floatStarts && _companion.RiseTicks > rises && highest > 0.40f && nearest >= -_companion.SafeMarginM &&
            _companion.CurrentIntent == "stay" && gap <= CompanionAvatar.ComeArrivalM + 0.02f,
            $"with no walk out of a walled pen, the Gubble floats up over the 40 cm wall, never through it, and arrives (highest {highest:0.000} m, the capsule's nearest to a wall {nearest * 1000:0.0} mm, {penFrames / 60.0f:0.0} s, gap {gap:0.00} m)");
        // A lid makes the pen one it really cannot leave: that is reported, and it stays inside.
        Box(pen + new Vector3(0, 0.41f, 0), new Vector3(0.73f, 0.02f, 0.73f));
        await Frames(2);
        Check(_companion.TryTeleportTo(pen + new Vector3(0, 0.01f, 0)), "companion inside the closed pen");
        _companion.Come();
        reported = false;
        var escaped = false;
        for (var i = 0; i < 180; i++)
        {
            await Frames(1);
            reported |= _companion.GoalBlocked;
            escaped |= !Overlaps(_companion.GlobalPosition, pen, 0.285f);
        }
        Report(reported && !escaped && _companion.CurrentIntent == "come",
            $"an unreachable come (a closed pen) reports blocked and never crosses the pen walls or its lid (blocked={reported}, escaped={escaped})");
        _companion.Stop();

        // Lane P review: across a thin wall the player can be inside the come distance yet unreachable. That is
        // not arrived. A pen of 1 cm walls with the player inside it and the companion 12.5 cm away outside. (The Gubble
        // floats: it does not arrive through the wall, it rises over it and comes down beside the player.)
        var thin = new Vector3(3.0f, 0, -3.0f);
        Box(thin + new Vector3(0, 0.15f, 0.25f), new Vector3(0.51f, 0.3f, 0.01f));
        Box(thin + new Vector3(0, 0.15f, -0.25f), new Vector3(0.51f, 0.3f, 0.01f));
        Box(thin + new Vector3(-0.25f, 0.15f, 0), new Vector3(0.01f, 0.3f, 0.51f));
        Box(thin + new Vector3(0.25f, 0.15f, 0), new Vector3(0.01f, 0.3f, 0.51f));
        revision = _navigation.Revision;
        for (var i = 0; i < 120 && _navigation.Revision == revision; i++) await Frames(1);
        Check(_player.TryTeleportTo(thin + new Vector3(0.175f, 0.003f, 0)) && _companion.TryTeleportTo(thin + new Vector3(0.30f, 0.01f, 0)),
            "the player inside a thin-walled pen, the companion just outside it");
        await Frames(5);
        var across = PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition);
        Check(!_navigation.FindRoute(_companion.GlobalPosition, _player.GlobalPosition, CompanionAvatar.ComeArrivalM + 0.05f).Reaches,
            "the route ending beside the thin wall does not count as reaching the player behind it");
        _companion.Come();
        await Frames(5);
        Report(across <= CompanionAvatar.ComeArrivalM && _companion.CurrentIntent == "come" && _companion.FloatingThere,
            $"come across a thin wall does not arrive through it: the walk cannot reach, so the Gubble floats (distance={across:0.000} m, intent={_companion.CurrentIntent})");
        var wallX = thin.X + 0.25f;
        var crossedLow = false;
        var thinFrames = 0;
        for (; thinFrames < 600 && _companion.CurrentIntent == "come"; thinFrames++)
        {
            await Frames(1);
            // Through the wall: from one side to the other below its 30 cm top.
            crossedLow |= _companion.GlobalPosition.X < wallX && _companion.GlobalPosition.Y < 0.30f - 0.005f && _companion.GlobalPosition.X > wallX - 0.03f && thinFrames < 30;
        }
        Report(_companion.CurrentIntent == "stay" && _companion.GlobalPosition.X < wallX - 0.02f && !crossedLow,
            $"it floats over the 30 cm wall and arrives on the player's side (at {_companion.GlobalPosition.X - wallX:0.000} m from the wall after {thinFrames / 60.0f:0.0} s)");
        _companion.Stop();

        // Lane P review: the re-bake fingerprint hashed shape instance ids, so a shape resized in place went unnoticed.
        var grown = new BoxShape3D { Size = new Vector3(0.2f, 0.2f, 0.2f) };
        var post = new StaticBody3D { Position = new Vector3(3.0f, 0.1f, 3.5f), CollisionLayer = 1, CollisionMask = 0 };
        post.AddChild(new CollisionShape3D { Shape = grown });
        AddChild(post);
        revision = _navigation.Revision;
        for (var i = 0; i < 120 && _navigation.Revision == revision; i++) await Frames(1);
        await Frames(2); // the map holds a bake from the next physics frame
        float Across() => _navigation.FindRoute(new Vector3(3.0f, 0.01f, 2.8f), new Vector3(3.0f, 0.01f, 4.2f), 0.05f).LengthM;
        var before = Across();
        revision = _navigation.Revision;
        grown.Size = new Vector3(0.8f, 0.2f, 0.8f);
        var waitedForResize = 0;
        for (; waitedForResize < 120 && _navigation.Revision == revision; waitedForResize++) await Frames(1);
        await Frames(2);
        var after = Across();
        // A route past the post goes round it: 1.4 m straight, longer round the grown post.
        Report(_navigation.Revision > revision && after > before + 0.2f,
            $"resizing a collision shape in place re-bakes the navigation mesh (after {waitedForResize} frames; the route past it grew from {before:0.00} to {after:0.00} m)");
        _player.TryTeleportTo(new Vector3(-2, 0.003f, 2));
    }

    /// <summary>
    /// Look captures showed both avatars hovering over the rug. Measured cause: the visual pill started 3 % of the
    /// body height above the feet (3 mm; 7.2 mm on the old 0.24 m companion), and Jolt rests the capsule 0 to 1.5 mm
    /// above its support. For both bodies on the floor, the 6 mm rug and the 4 cm book top, after a teleport and after
    /// a short drop: the collider's lowest point is the feet, and the visual's lowest point is within 1 mm of the
    /// surface the ray finds under the body, never sunk into it.
    /// </summary>
    private async Task TestGroundContact()
    {
        _companion.Stop();
        _player.SetControlInput(Vector2.Zero);
        var spots = new (string Name, Vector3 Player, Vector3 Companion)[]
        {
            ("floor", new Vector3(-3.0f, 0, 4.0f), new Vector3(-2.8f, 0, 4.0f)),
            ("6 mm rug", new Vector3(-0.2f, 0.006f, -2.2f), new Vector3(0.2f, 0.006f, -2.2f)),
            ("4 cm book top", new Vector3(-0.05f, 0.04f, -1.4f), new Vector3(0.05f, 0.04f, -1.4f)),
        };
        foreach (var (name, playerSpot, companionSpot) in spots)
            foreach (var drop in new[] { false, true })
            {
                if (drop)
                {
                    // As the room spawns them: 8 mm up, then a fall onto the surface.
                    _player.GlobalPosition = playerSpot + Vector3.Up * 0.008f;
                    _companion.GlobalPosition = companionSpot + Vector3.Up * 0.008f;
                    _player.Velocity = Vector3.Zero;
                    _companion.Velocity = Vector3.Zero;
                }
                else Check(_player.TryTeleportTo(playerSpot) && _companion.TryTeleportTo(companionSpot), $"both bodies stand on the {name}");
                await Frames(45);
                foreach (var body in new SmallPlayerController[] { _player, _companion })
                {
                    var (shapeBottom, meshBottom, contact) = GroundContact(body);
                    var how = drop ? "after an 8 mm drop" : "after a teleport";
                    var label = string.Create(CultureInfo.InvariantCulture,
                        $"{body.Name} on the {name} {how}: capsule {(shapeBottom - contact) * 1000:0.00} mm above the surface, visual {(meshBottom - contact) * 1000:0.00} mm (seat {body.SeatGapM * 1000:0.00} mm)");
                    // The Gubble floats (founder, 8 October): it hovers its hover height over the surface, give or take its bob.
                    if (body.Floats)
                        Report(!body.IsOnFloor() && body.HoverSupportY is { } top && Mathf.Abs(top - contact) < 0.001f &&
                            Mathf.Abs(shapeBottom - contact - body.HoverHeightM) <= SmallPlayerController.HoverBobM + 0.0015f && Mathf.Abs(meshBottom - shapeBottom) < 0.0005f,
                            label + " (hovering)");
                    else
                        Report(body.IsOnFloor() && Mathf.Abs(shapeBottom - body.GlobalPosition.Y) < 0.00001f &&
                            shapeBottom - contact >= -0.0002f && shapeBottom - contact <= SmallPlayerController.MaxSeatGapM &&
                            meshBottom - contact < 0.001f && meshBottom - contact > -0.0002f, label);
                }
            }
        // In the air the visual stays on the body: no seat is applied off the ground.
        _player.GlobalPosition += Vector3.Up * 0.03f;
        await Frames(2);
        Check(!_player.IsOnFloor() && _player.SeatGapM == 0 && _player.GetNode<Node3D>("OriginalPrototypeBody").Position == Vector3.Zero,
            "in the air the visual body is not pulled down");
        await Frames(30);
        Check(_player.TryTeleportTo(new Vector3(-2, 0.003f, 3.2f)), "back to open floor after the contact fixture");
        await Frames(10);
    }

    /// <summary>The collision capsule's lowest point, the visual meshes' lowest point (world AABB) and the surface a ray finds under the body.</summary>
    private (float ShapeBottom, float MeshBottom, float Contact) GroundContact(SmallPlayerController body)
    {
        var shapeNode = body.GetNode<CollisionShape3D>("SmallBodyCollision");
        var capsule = (CapsuleShape3D)shapeNode.Shape;
        var shapeBottom = shapeNode.GlobalPosition.Y - capsule.Height * 0.5f;
        var meshBottom = float.MaxValue;
        foreach (var node in body.GetNode<Node3D>("OriginalPrototypeBody").FindChildren("*", "MeshInstance3D", true, false))
        {
            var mesh = (MeshInstance3D)node;
            meshBottom = Mathf.Min(meshBottom, (mesh.GlobalTransform * mesh.GetAabb()).Position.Y);
        }
        var ray = PhysicsRayQueryParameters3D.Create(body.GlobalPosition + Vector3.Up * 0.05f, body.GlobalPosition + Vector3.Down * 0.1f, 1);
        ray.Exclude = new Godot.Collections.Array<Rid> { body.GetRid() };
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(ray);
        return (shapeBottom, meshBottom, hit.Count > 0 ? hit["position"].AsVector3().Y : float.NaN);
    }

    private static bool Overlaps(Vector3 position, Vector3 centre, float half) =>
        Mathf.Abs(position.X - centre.X) < half && Mathf.Abs(position.Z - centre.Z) < half;

    /// <summary>
    /// How far a standing body's capsule (feet at the bottom, as the bodies' are) keeps from an axis-aligned box: the distance
    /// from its axis, a radius over the feet to a radius under the top, to the box, less the radius. Negative is how deep it
    /// reaches in. Exact, rounded foot and all.
    /// </summary>
    private static float CapsuleClearance(Vector3 feet, float radius, float height, Aabb box)
    {
        var planar = new Vector2(Mathf.Max(Mathf.Max(box.Position.X - feet.X, feet.X - box.End.X), 0),
            Mathf.Max(Mathf.Max(box.Position.Z - feet.Z, feet.Z - box.End.Z), 0)).Length();
        var vertical = Mathf.Max(Mathf.Max(box.Position.Y - (feet.Y + height - radius), feet.Y + radius - box.End.Y), 0);
        return new Vector2(planar, vertical).Length() - radius;
    }

    /// <summary>Planar distance from a point to a square footprint (zero inside it).</summary>
    private static float ClearanceFromSquare(Vector3 point, Vector3 centre, float half) =>
        new Vector2(Mathf.Max(Mathf.Abs(point.X - centre.X) - half, 0), Mathf.Max(Mathf.Abs(point.Z - centre.Z) - half, 0)).Length();

    /// <summary>The companion's planar offset from the player, along a direction of travel and to its right.</summary>
    private (float Along, float Lateral) Offset(Vector3 travel)
    {
        var offset = _companion.GlobalPosition - _player.GlobalPosition;
        offset.Y = 0;
        return (offset.Dot(travel), offset.Dot(travel.Cross(Vector3.Up)));
    }

    /// <summary>F3: a high-angle camera that orbits and zooms around the player, stays centred and keeps out of geometry.</summary>
    private async Task TestDioramaCamera()
    {
        Check(_player.TryTeleportTo(new Vector3(-2, 0.003f, 3.2f)), "diorama fixture admits the player");
        _player.Rotation = new Vector3(0, 0.4f, 0);
        var hud = new RoomHud { Player = _player, Companion = _companion, RoomTitle = "TEST" };
        AddChild(hud);
        await Frames(2);
        hud.SetViewMode(2);
        await Frames(3);
        var camera = hud.DioramaCamera;
        Check(camera.Current && Mathf.IsEqualApprox(hud.DioramaYaw, 0.4f) && _player.MovementFrameYaw == hud.DioramaYaw,
            "F3 makes the diorama camera current, starting behind the player, and movement follows the view");
        bool Centred() => (_player.GlobalPosition + Vector3.Up * (_player.BodyHeightM * 0.5f) - camera.GlobalPosition).Normalized().Dot(-camera.GlobalBasis.Z) > 0.999f;
        float LooksDown() => Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(camera.GlobalBasis.Z.Y, -1, 1)));
        var reach = camera.GlobalPosition.DistanceTo(_player.GlobalPosition + Vector3.Up * (_player.BodyHeightM * 0.5f));
        Check(Centred() && Mathf.Abs(LooksDown() - RoomHud.DioramaDefaultPitchDeg) < 1.0f && Mathf.Abs(reach - RoomHud.DioramaDefaultDistanceM) < 0.03f,
            $"the diorama camera looks down on the player from a high angle (pitch={LooksDown():0.0} deg, distance={reach:0.000} m)");
        var before = new Vector2(camera.GlobalPosition.X - _player.GlobalPosition.X, camera.GlobalPosition.Z - _player.GlobalPosition.Z).Angle();
        hud.OrbitDiorama(new Vector2(400, 0));
        await Frames(3);
        var after = new Vector2(camera.GlobalPosition.X - _player.GlobalPosition.X, camera.GlobalPosition.Z - _player.GlobalPosition.Z).Angle();
        Check(Mathf.Abs(Mathf.AngleDifference(before, after)) > 0.9f && Centred() && Mathf.IsEqualApprox(_player.Rotation.Y, 0.4f),
            "moving the mouse sideways orbits the camera round the player without turning the body");
        hud.OrbitDiorama(new Vector2(0, 100000));
        await Frames(3);
        var steepest = LooksDown();
        hud.OrbitDiorama(new Vector2(0, -100000));
        await Frames(3);
        Check(Mathf.Abs(steepest - RoomHud.DioramaMaxPitchDeg) < 1.0f && Mathf.Abs(LooksDown() - RoomHud.DioramaMinPitchDeg) < 1.0f && Centred(),
            $"orbit pitch stays between {RoomHud.DioramaMinPitchDeg} and {RoomHud.DioramaMaxPitchDeg} degrees (got {steepest:0.0} and {LooksDown():0.0})");
        hud.ZoomDiorama(50);
        await Frames(3);
        var near = camera.GlobalPosition.DistanceTo(_player.GlobalPosition + Vector3.Up * (_player.BodyHeightM * 0.5f));
        hud.ZoomDiorama(-50);
        await Frames(3);
        var far = camera.GlobalPosition.DistanceTo(_player.GlobalPosition + Vector3.Up * (_player.BodyHeightM * 0.5f));
        Check(Mathf.Abs(near - RoomHud.DioramaMinDistanceM) < 0.02f && Mathf.Abs(far - RoomHud.DioramaMaxDistanceM) < 0.03f,
            $"the wheel zooms between {RoomHud.DioramaMinDistanceM} and {RoomHud.DioramaMaxDistanceM} m (got {near:0.000} and {far:0.000})");
        // Movement follows the view: forward walks away from the camera and the body turns to face its motion.
        hud.ZoomDiorama(Mathf.Log(RoomHud.DioramaMaxDistanceM / RoomHud.DioramaDefaultDistanceM) / Mathf.Log(1 / 0.88f));
        hud.OrbitDiorama(new Vector2(0, (RoomHud.DioramaDefaultPitchDeg - RoomHud.DioramaMinPitchDeg) / Mathf.RadToDeg(RoomHud.MouseRadiansPerPixel)));
        var viewForward = new Vector3(-camera.GlobalBasis.Z.X, 0, -camera.GlobalBasis.Z.Z).Normalized();
        var start = _player.GlobalPosition;
        _player.SetControlInput(new Vector2(0, 1));
        await Frames(45);
        _player.SetControlInput(Vector2.Zero);
        var moved = _player.GlobalPosition - start;
        moved.Y = 0;
        Check(moved.Length() > 0.15f && moved.Normalized().Dot(viewForward) > 0.98f && (-_player.GlobalBasis.Z).Dot(viewForward) > 0.97f,
            $"in F3 forward moves away from the camera and the body turns to face its motion (moved={moved.Length():0.000} m)");
        // Under the 12 cm deck the camera is held below the deck instead of passing through it.
        Check(_player.TryTeleportTo(new Vector3(0, 0.003f, 1.2f)), "the player can stand under the deck");
        await Frames(60);
        Check(camera.GlobalPosition.Y < 0.12f && Centred(), $"the camera stays under a low ceiling instead of clipping through it (camera y={camera.GlobalPosition.Y:0.000})");
        hud.SetViewMode(1);
        Check(_player.MovementFrameYaw == null && !camera.Current, "leaving F3 returns the body to its own heading and mouse look");
        hud.QueueFree();
        _player.GetNodeOrNull("FollowCameraArm")?.QueueFree();
        camera.GetParent().GetParent().QueueFree();
        _player.Rotation = Vector3.Zero;
        Check(_player.TryTeleportTo(new Vector3(-2, 0.003f, 3.2f)), "back to the companion route after the camera fixture");
        await Frames(10);
    }


    // ---- climbing and swimming (founder's playtest round, 8 October) ----

    /// <summary>A static body on the world layer, as the room's colliders are.</summary>
    private StaticBody3D Solid(Vector3 position, Shape3D shape)
    {
        var body = new StaticBody3D { Position = position, CollisionLayer = 1, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = shape });
        AddChild(body);
        return body;
    }

    /// <summary>A prism of a convex side profile (x, y pairs) swept from z - halfZ to z + halfZ.</summary>
    private static ConvexPolygonShape3D Prism(float halfZ, params Vector2[] profile) =>
        new() { Points = profile.SelectMany(p => new[] { new Vector3(p.X, p.Y, -halfZ), new Vector3(p.X, p.Y, halfZ) }).ToArray() };

    private float VisualFacingDot(Vector3 direction) => (-_player.GetNode<Node3D>("OriginalPrototypeBody").GlobalBasis.Z).Normalized().Dot(direction);

    /// <summary>Hold forward until the body has climbed and pulled itself over (or the frames run out); report what happened.</summary>
    private async Task<(int GrabFrame, int FirstBlocked, float Speed, bool FacedWall, bool Upright)> ClimbUntilOver(Vector3 wallDirection, int maxFrames)
    {
        var grabs = _player.Grabs;
        var pulls = _player.PullOvers;
        int frame = 0, grabFrame = -1, firstBlocked = -1;
        float? startY = null;
        int startFrame = 0, lastFrame = 0;
        var lastY = 0.0f;
        var faced = true;
        var upright = true;
        _player.SetControlInput(new Vector2(0, 1));
        for (; frame < maxFrames; frame++)
        {
            await Frames(1);
            if (firstBlocked < 0 && _player.IsBlocked && !_player.IsClimbing) firstBlocked = frame;
            if (grabFrame < 0 && _player.Grabs > grabs) grabFrame = frame;
            upright &= _player.GlobalBasis.Y.Dot(Vector3.Up) > 0.9999f && _player.EyeCamera.GlobalBasis.Y.Dot(Vector3.Up) > 0.999f;
            if (_player.IsClimbing && !_player.IsPullingOver && frame - grabFrame > 15)
            {
                faced &= VisualFacingDot(wallDirection) > 0.9f;
                if (startY == null) { startY = _player.GlobalPosition.Y; startFrame = frame; }
                lastY = _player.GlobalPosition.Y;
                lastFrame = frame;
            }
            if (_player.PullOvers > pulls && !_player.IsClimbing && _player.IsOnFloor()) break;
        }
        _player.SetControlInput(Vector2.Zero);
        var speed = startY is { } y0 && lastFrame > startFrame ? (lastY - y0) / ((lastFrame - startFrame) / 60.0f) : 0;
        return (grabFrame, firstBlocked, speed, faced, upright);
    }

    private async Task TestClimbing()
    {
        var a = new Vector3(0, 0, -40);
        Solid(a + new Vector3(0, -0.05f, 0), new BoxShape3D { Size = new Vector3(6, 0.1f, 6) });
        // A vertical face 25 cm tall (a cottage wall, a cliff), its west face at x = -1.15.
        Solid(a + new Vector3(-1.0f, 0.125f, 0), new BoxShape3D { Size = new Vector3(0.3f, 0.25f, 0.6f) });
        // A 70 degree face 25 cm tall with a plateau on top, its foot at x = -1.15.
        var run70 = 0.25f / Mathf.Tan(Mathf.DegToRad(70));
        Solid(a + new Vector3(0, 0, 1.2f), Prism(0.3f, new Vector2(-1.15f, 0), new Vector2(-0.85f, 0), new Vector2(-0.85f, 0.25f), new Vector2(-1.15f + run70, 0.25f)));
        // A walkable 40 degree ramp with a top, its foot at x = -1.15.
        var rise40 = 0.3f * Mathf.Tan(Mathf.DegToRad(40));
        Solid(a + new Vector3(0, 0, -1.2f), Prism(0.3f, new Vector2(-1.15f, 0), new Vector2(-0.55f, 0), new Vector2(-0.55f, rise40), new Vector2(-0.85f, rise40)));
        await Frames(3);
        var facingEast = new Vector3(0, -Mathf.Pi * 0.5f, 0);

        // The vertical face: walk in, grab after a deliberate push, climb slowly facing it, pull over the top.
        Check(_player.TryTeleportTo(a + new Vector3(-1.5f, 0.003f, 0)), "the climbing fixture admits the player");
        _player.Rotation = facingEast;
        var (grab, blocked, speed, faced, upright) = await ClimbUntilOver(Vector3.Right, 600);
        var top = _player.GlobalPosition - a;
        Report(grab > 0 && blocked >= 0 && grab - blocked >= Mathf.FloorToInt(_player.ClimbGrabDelayS * 60) - 1,
            $"walking into a vertical face grabs it only after a deliberate push ({(grab - blocked) / 60.0f:0.00} s against it)");
        Report(speed > 0.10f && speed < 0.13f, $"the body climbs a vertical face slowly, against a walk of {_player.WalkSpeedMps:0.00} m/s (climb {speed:0.000} m/s)");
        Check(faced && upright, "while climbing the visible body faces the face; the capsule and the eye stay upright");
        Report(_player.IsOnFloor() && !_player.IsClimbing && Mathf.Abs(top.Y - 0.25f) < 0.006f && top.X > -1.15f + 0.01f,
            $"at the top the body pulls itself over and stands on the ledge (feet at {Text(top)})");

        // Letting go: jump kicks the body off the face, and it lands below.
        Check(_player.TryTeleportTo(a + new Vector3(-1.5f, 0.003f, 0)), "back to the foot of the vertical face");
        _player.Rotation = facingEast;
        _player.SetControlInput(new Vector2(0, 1));
        for (var i = 0; i < 300 && !(_player.IsClimbing && _player.GlobalPosition.Y - a.Y > 0.08f); i++) await Frames(1);
        var held = _player.GlobalPosition;
        var releases = _player.ClimbReleases;
        _player.SetControlInput(Vector2.Zero, jump: true);
        await Frames(2);
        _player.SetControlInput(Vector2.Zero);
        var awayFrames = 0;
        for (; awayFrames < 120 && !_player.IsOnFloor(); awayFrames++) await Frames(1);
        Report(_player.ClimbReleases == releases + 1 && !_player.IsClimbing && _player.IsOnFloor() && _player.GlobalPosition.X < held.X - 0.01f && _player.GlobalPosition.Y - a.Y < 0.01f,
            $"jump lets go with a kick away from the face and the body lands below (from {held.Y - a.Y:0.000} m, {_player.GlobalPosition.X - held.X:0.000} m back, {awayFrames} frames)");

        // Climbing down to the foot of the face steps off into walking.
        Check(_player.TryTeleportTo(a + new Vector3(-1.5f, 0.003f, 0)), "back to the foot again");
        _player.Rotation = facingEast;
        _player.SetControlInput(new Vector2(0, 1));
        for (var i = 0; i < 300 && !(_player.IsClimbing && _player.GlobalPosition.Y - a.Y > 0.06f); i++) await Frames(1);
        _player.SetControlInput(new Vector2(0, -1));
        var down = 0;
        for (; down < 120 && _player.IsClimbing; down++) await Frames(1);
        await Frames(10);
        _player.SetControlInput(Vector2.Zero);
        Check(!_player.IsClimbing && _player.IsOnFloor() && _player.GlobalPosition.Y - a.Y < 0.006f, $"climbing down to the ground steps off into walking ({down} frames)");

        // A 70 degree face: climbed along its slope and topped out onto the plateau.
        Check(_player.TryTeleportTo(a + new Vector3(-1.5f, 0.003f, 1.2f)), "the 70 degree face's foot admits the player");
        _player.Rotation = facingEast;
        (grab, _, speed, faced, _) = await ClimbUntilOver(Vector3.Right, 600);
        top = _player.GlobalPosition - a;
        Report(grab > 0 && faced && _player.IsOnFloor() && Mathf.Abs(top.Y - 0.25f) < 0.006f,
            $"a 70 degree face is climbed (rising {speed:0.000} m/s) and topped out onto its plateau (feet at {Text(top)})");

        // No grab from a walkable slope: pushing into the 40 degree ramp's foot, and walking up it (from past its crease,
        // as the jitter suite does: the crease from flat ground to 40 degrees holds the body, as it did before climbing).
        Check(_player.TryTeleportTo(a + new Vector3(-1.5f, 0.003f, -1.2f)), "the walkable ramp's foot admits the player");
        _player.Rotation = facingEast;
        var grabs = _player.Grabs;
        _player.SetControlInput(new Vector2(0, 1));
        await Frames(150);
        var pushedFoot = _player.Grabs == grabs && !_player.IsClimbing;
        Check(_player.TryTeleportTo(a + new Vector3(-1.10f, 0.06f, -1.2f)), "the player stands on the walkable ramp");
        _player.Rotation = facingEast;
        _player.SetControlInput(new Vector2(0, 1));
        await Frames(90);
        _player.SetControlInput(Vector2.Zero);
        await Frames(10);
        Report(pushedFoot && _player.Grabs == grabs && _player.IsOnFloor() && _player.GlobalPosition.Y - a.Y > rise40 - 0.01f,
            $"a walkable 40 degree slope is never grabbed: pushed into at its foot, and walked up to its top (feet at {Text(_player.GlobalPosition - a)})");

        // Brushing along a face at a shallow angle never grabs it.
        Check(_player.TryTeleportTo(a + new Vector3(-1.175f, 0.003f, -0.28f)), "the player stands beside the vertical face");
        var along = new Vector3(Mathf.Sin(Mathf.DegToRad(30)), 0, Mathf.Cos(Mathf.DegToRad(30)));
        _player.Rotation = new Vector3(0, Mathf.Atan2(-along.X, -along.Z), 0);
        grabs = _player.Grabs;
        _player.SetControlInput(new Vector2(0, 1));
        await Frames(100);
        _player.SetControlInput(Vector2.Zero);
        Check(_player.Grabs == grabs && !_player.IsClimbing, "walking along a face, 30 degrees into it, brushes past without grabbing");

        // Carrying: climbing takes both hands.
        Check(_player.TryTeleportTo(a + new Vector3(-1.5f, 0.003f, 0)), "back to the vertical face to carry");
        _player.Rotation = facingEast;
        _player.HandsFull = () => true;
        grabs = _player.Grabs;
        _player.SetControlInput(new Vector2(0, 1));
        await Frames(150);
        _player.SetControlInput(Vector2.Zero);
        _player.HandsFull = null;
        Check(_player.Grabs == grabs && !_player.IsClimbing && _player.GlobalPosition.Y - a.Y < 0.006f, "a body carrying something does not grab a face");

        // Low gravity: a leap into a face grabs it in mid-air.
        Check(_player.SetWorldPhysics(Preset("room_floaty", _player.WorldPhysicsRevision + 1)), "floaty gravity for the leap");
        Check(_player.TryTeleportTo(a + new Vector3(-1.27f, 0.003f, 0)), "the player stands a few centimetres from the face");
        _player.Rotation = facingEast;
        await Frames(5);
        grabs = _player.Grabs;
        _player.SetControlInput(new Vector2(0, 1), jump: true);
        var grabbedAt = float.NaN;
        for (var i = 0; i < 120 && float.IsNaN(grabbedAt); i++)
        {
            await Frames(1);
            if (_player.Grabs > grabs) grabbedAt = _player.GlobalPosition.Y - a.Y;
        }
        _player.SetControlInput(Vector2.Zero, jump: true);
        await Frames(2);
        _player.SetControlInput(Vector2.Zero);
        for (var i = 0; i < 300 && !_player.IsOnFloor(); i++) await Frames(1);
        Report(grabbedAt > 0.02f, $"in floaty gravity a leap into a face grabs it in mid-air (at {grabbedAt:0.000} m)");
        Check(_player.SetWorldPhysics(Preset("room_tuned", _player.WorldPhysicsRevision + 1)), "back to tuned gravity");
        await Frames(10);

        // A face that starts above a step, at head height (a crown lobe hanging over a perch, a ledge's underside): the
        // body pushing into it is grabbed and climbed, never left stuck (Lane P fix round: the old single ray at 2.4 cm missed it).
        Solid(a + new Vector3(-1.0f, 0.175f, 2.4f), new BoxShape3D { Size = new Vector3(0.3f, 0.25f, 0.6f) });
        await Frames(2);
        Check(_player.TryTeleportTo(a + new Vector3(-1.5f, 0.003f, 2.4f)), "the player stands before a face that starts 5 cm up");
        _player.Rotation = facingEast;
        (grab, _, _, _, _) = await ClimbUntilOver(Vector3.Right, 600);
        top = _player.GlobalPosition - a;
        Report(grab > 0 && _player.IsOnFloor() && Mathf.Abs(top.Y - 0.30f) < 0.006f,
            $"a face starting at head height is grabbed and climbed to its top, never a trap (feet at {Text(top)})");

        // The bounds' top holds a climber: no pull-over onto a ledge where the head would leave the room.
        Check(_player.SetPlayableBounds(new Aabb(a + new Vector3(-3, -1, -3), new Vector3(6, 1.0f + 0.25f + _player.BodyHeightM - 0.02f, 6))), "bounds whose top is 2 cm under a head standing on the face's top");
        Check(_player.TryTeleportTo(a + new Vector3(-1.5f, 0.003f, 0)), "back to the vertical face under the low bounds");
        _player.Rotation = facingEast;
        var pullsUnderTop = _player.PullOvers;
        var highestHead = float.MinValue;
        _player.SetControlInput(new Vector2(0, 1));
        for (var i = 0; i < 360; i++)
        {
            await Frames(1);
            highestHead = Mathf.Max(highestHead, _player.GlobalPosition.Y + _player.BodyHeightM - a.Y);
        }
        _player.SetControlInput(Vector2.Zero, jump: true);
        await Frames(2);
        _player.SetControlInput(Vector2.Zero);
        await Frames(60);
        Report(_player.PullOvers == pullsUnderTop && highestHead <= 0.25f + _player.BodyHeightM - 0.02f + 0.001f,
            $"the bounds' top holds a climber: no pull-over that would lift the head out of the room (highest head {highestHead:0.000} m, bound {0.25f + _player.BodyHeightM - 0.02f:0.000} m)");
        Check(_player.SetPlayableBounds(new Aabb(new Vector3(-1000, -100, -1000), new Vector3(2000, 200, 2000))), "the bounds open again");

        // Creation forces act on a climber: a lift carries it up along the face, a push away from the face lets it go.
        Check(_player.TryTeleportTo(a + new Vector3(-1.5f, 0.003f, 0)), "back to the vertical face for creation forces");
        _player.Rotation = facingEast;
        _player.SetControlInput(new Vector2(0, 1));
        for (var i = 0; i < 300 && !(_player.IsClimbing && _player.GlobalPosition.Y - a.Y > 0.05f); i++) await Frames(1);
        _player.SetControlInput(Vector2.Zero);
        await Frames(5);
        var hung = _player.GlobalPosition;
        _player.SetCreationEffects(new Vector3(0, 0.15f, 0), 0, new Callable(this, MethodName.AllowAll));
        await Frames(60);
        var lifted = _player.GlobalPosition.Y - hung.Y;
        var stillClimbing = _player.IsClimbing;
        _player.SetCreationEffects(new Vector3(-3.0f, 0, 0), 0, new Callable(this, MethodName.AllowAll));
        var pushedOff = false;
        for (var i = 0; i < 60 && !pushedOff; i++) { await Frames(1); pushedOff = !_player.IsClimbing; }
        await Frames(10);
        _player.SetCreationEffects(Vector3.Zero, 0, new Callable());
        for (var i = 0; i < 300 && !_player.IsOnFloor(); i++) await Frames(1);
        Report(lifted > 0.03f && stillClimbing && pushedOff && _player.GlobalPosition.X < hung.X - 0.01f,
            $"a creation lift carries a climber up the face ({lifted * 100:0.0} cm in 1 s, still climbing {stillClimbing}) and a push away from the face lets it go (let go {pushedOff}, {(hung.X - _player.GlobalPosition.X) * 100:0.0} cm away)");
    }

    public bool AllowAll(Vector3 position) => true;

    /// <summary>
    /// A pool, 20 cm deep with its water 2 cm below the rim: a shelving 20 degree shore on the west, a vertical bank on the
    /// east. Its surface is the room's kind of water collider (RoomWater, layer 4).
    /// </summary>
    private async Task TestSwimming()
    {
        var b = new Vector3(0, 0, -50);
        const float surface = -0.02f;
        const float bed = -0.2f;
        Solid(b + new Vector3(-1.0f, -0.125f, 0), new BoxShape3D { Size = new Vector3(2.0f, 0.25f, 3.0f) });
        Solid(b + new Vector3(2.2f, -0.125f, 0), new BoxShape3D { Size = new Vector3(2.0f, 0.25f, 3.0f) });
        Solid(b + new Vector3(0.6f, -0.125f, -1.0f), new BoxShape3D { Size = new Vector3(1.2f, 0.25f, 1.0f) });
        Solid(b + new Vector3(0.6f, -0.125f, 1.0f), new BoxShape3D { Size = new Vector3(1.2f, 0.25f, 1.0f) });
        Solid(b + new Vector3(0.6f, -0.225f, 0), new BoxShape3D { Size = new Vector3(1.2f, 0.05f, 1.0f) });
        Solid(b, Prism(0.5f, new Vector2(0, 0), new Vector2(0, bed), new Vector2(0.55f, bed)));
        var sheet = new SurfaceTool();
        sheet.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var corner in new[] { new Vector2(0, -0.5f), new Vector2(1.2f, -0.5f), new Vector2(1.2f, 0.5f), new Vector2(0, -0.5f), new Vector2(1.2f, 0.5f), new Vector2(0, 0.5f) })
            sheet.AddVertex(new Vector3(corner.X, surface, corner.Y));
        var water = RoomWater.CreateCollider(new[] { ((Shape3D)sheet.Commit().CreateTrimeshShape(), Transform3D.Identity) });
        water.Position = b;
        AddChild(water);
        await Frames(3);

        // RoomWater's questions, and everything else looking straight through the water.
        var space = GetWorld3D().DirectSpaceState;
        var middle = b + new Vector3(0.8f, 0, 0);
        var above = RoomWater.SurfaceAbove(space, middle + Vector3.Up * (bed + 0.01f));
        var below = RoomWater.SurfaceBelow(space, middle + Vector3.Up * 0.3f);
        var depth = RoomWater.DepthAt(space, middle + Vector3.Up * surface);
        Report(above is { } up && Mathf.Abs(up - surface) < 0.001f && below is { } dn && Mathf.Abs(dn - surface) < 0.001f && Mathf.Abs(depth - (surface - bed)) < 0.002f,
            $"RoomWater finds the surface from below and above and the depth to the bed (above {above:0.000}, below {below:0.000}, depth {depth:0.000} m)");
        // A pond's rim: the exported sheet ends a few millimetres under or over the bank (Lane C's check of the garage). Standing
        // there is dry ground, not water with no bed.
        foreach (var (rimX, rimY) in new[] { (-0.5f, -0.003f), (-0.2f, 0.003f) })
        {
            var rimSheet = new SurfaceTool();
            rimSheet.Begin(Mesh.PrimitiveType.Triangles);
            foreach (var corner in new[] { new Vector2(-0.1f, -1.2f), new Vector2(0.1f, -1.2f), new Vector2(0.1f, -1.0f), new Vector2(-0.1f, -1.2f), new Vector2(0.1f, -1.0f), new Vector2(-0.1f, -1.0f) })
                rimSheet.AddVertex(new Vector3(rimX + corner.X, rimY, corner.Y));
            var rim = RoomWater.CreateCollider(new[] { ((Shape3D)rimSheet.Commit().CreateTrimeshShape(), Transform3D.Identity) });
            rim.Position = b;
            AddChild(rim);
        }
        await Frames(2);
        var underRim = RoomWater.At(space, b + new Vector3(-0.5f, 0.001f, -1.1f), 0.4f, 0.02f);
        var overRim = RoomWater.At(space, b + new Vector3(-0.2f, 0.001f, -1.1f), 0.4f, 0.02f);
        var stillPool = RoomWater.At(space, middle + Vector3.Up * (bed + 0.001f), 0.4f, 0.02f);
        Check(!underRim.Wet && !overRim.Wet && stillPool.Wet && Mathf.Abs(stillPool.DepthM - (surface - bed)) < 0.002f,
            "at a pond's rim, where the sheet ends 3 mm under or over the bank, the ground is dry; the pool is still water to its bed");
        var anyRay = space.IntersectRay(PhysicsRayQueryParameters3D.Create(middle + Vector3.Up * 0.3f, middle + Vector3.Down * 0.5f));
        var worldRay = space.IntersectRay(PhysicsRayQueryParameters3D.Create(middle + Vector3.Up * 0.3f, middle + Vector3.Down * 0.5f, 1));
        Check(anyRay.Count > 0 && Mathf.Abs(anyRay["position"].AsVector3().Y - bed) < 0.001f && worldRay.Count > 0 && Mathf.Abs(worldRay["position"].AsVector3().Y - bed) < 0.001f,
            "a default ray (every layer, no areas) and a world-layer ray pass through the water to the bed: sight, reach, drops and the sun never meet it");
        var poolNavigation = RoomNavigation.Create(this, this, new Aabb(b + new Vector3(-0.4f, -0.3f, -0.45f), new Vector3(2.0f, 0.5f, 0.9f)), WorldScaleProfile.Companion, 0.01f);
        for (var i = 0; i < 30 && !poolNavigation.IsReady; i++) await Frames(1);
        var onBed = poolNavigation.ClosestPoint(middle + Vector3.Up * (bed + 0.01f));
        var onWater = poolNavigation.ClosestPoint(middle + Vector3.Up * surface);
        Report(poolNavigation.IsReady && Mathf.Abs(onBed.Y - (b.Y + bed)) < 0.03f && onWater.DistanceTo(middle + Vector3.Up * surface) > 0.1f,
            $"the navigation bake ignores the water: the pool's walkable floor is its bed (y {onBed.Y - b.Y:0.000} m), and nothing walkable lies on the surface (nearest {onWater.DistanceTo(middle + Vector3.Up * surface):0.000} m away)");
        poolNavigation.QueueFree();

        // Wade in down the shelving shore: slower as the water deepens, then swimming at the surface where it is over the head.
        Check(_player.TryTeleportTo(b + new Vector3(-0.35f, 0.003f, 0)), "the swimming fixture admits the player");
        _player.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0);
        var starts = _player.SwimStarts;
        var dry = 0.0f;
        var wading = 0.0f;
        var wadeDepth = 0.0f;
        var swimStartX = float.NaN;
        var previous = _player.GlobalPosition;
        _player.SetControlInput(new Vector2(0, 1));
        for (var i = 0; i < 240 && _player.GlobalPosition.X - b.X < 0.6f; i++)
        {
            await Frames(1);
            var here = _player.GlobalPosition;
            var planar = new Vector2(here.X - previous.X, here.Z - previous.Z).Length() * 60;
            previous = here;
            var under = _player.Water.Under(here);
            if (here.X - b.X < -0.08f && here.X - b.X > -0.25f) dry = planar;
            if (!_player.IsSwimming && under > 0.035f && under < 0.05f && _player.IsOnFloor()) { wading = planar; wadeDepth = under; }
            if (float.IsNaN(swimStartX) && _player.SwimStarts > starts) swimStartX = here.X - b.X;
        }
        var swimSpeedFrom = _player.GlobalPosition;
        await Frames(30);
        var swim = new Vector2(_player.GlobalPosition.X - swimSpeedFrom.X, _player.GlobalPosition.Z - swimSpeedFrom.Z).Length() * 2;
        var feet = _player.GlobalPosition.Y - b.Y;
        var tilt = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(_player.GetNode<Node3D>("OriginalPrototypeBody").GlobalBasis.Y.Normalized().Dot(Vector3.Up), -1, 1)));
        Report(wading > 0 && wading < dry * 0.9f && wading > dry * 0.6f, $"wading slows the body gradually ({wading:0.000} m/s in {wadeDepth * 100:0.0} cm of water, {dry:0.000} m/s dry)");
        Report(_player.IsSwimming && !float.IsNaN(swimStartX) && swimStartX > 0.2f && swimStartX < 0.4f,
            $"water over the head floats the body: it swims from x={swimStartX:0.00} m, where the water is {(-(bed / 0.55f) * swimStartX) - (-surface):0.000} m deep");
        Report(swim > _player.WalkSpeedMps * 0.5f && swim < _player.WalkSpeedMps * 0.7f, $"swimming is slower than walking ({swim:0.000} m/s, {swim / _player.WalkSpeedMps:0.00} of the walk)");
        Report(Mathf.Abs(feet - (surface - _player.SwimFloatDepthM)) < 0.008f && feet + 0.087f > surface + 0.015f,
            $"the swimmer stays at the surface with the eye above the water (feet {feet:0.000} m, eye {feet + 0.087f - surface:0.000} m above the surface)");
        Report(tilt > 60f && tilt < 85f && _player.GlobalBasis.Y.Dot(Vector3.Up) > 0.9999f && _player.EyeCamera.GlobalBasis.Y.Dot(Vector3.Up) > 0.999f,
            $"the visible body tips to lie along the water ({tilt:0} degrees); the capsule and the eye stay upright");

        // Swim on into the steep east bank: grab it and climb out.
        var grabs = _player.Grabs;
        var pulls = _player.PullOvers;
        _player.SetControlInput(new Vector2(0, 1));
        for (var i = 0; i < 900 && !(_player.PullOvers > pulls && _player.IsOnFloor() && !_player.IsClimbing); i++) await Frames(1);
        _player.SetControlInput(Vector2.Zero);
        var outside = _player.GlobalPosition - b;
        Report(_player.Grabs > grabs && _player.IsOnFloor() && !_player.IsSwimming && Mathf.Abs(outside.Y) < 0.006f && outside.X > 1.2f,
            $"swimming into a steep bank grabs it and climbs out onto the land (feet at {Text(outside)})");

        // Back into deep water, then out up the shelving shore on foot.
        _player.GlobalPosition = b + new Vector3(0.8f, surface - _player.SwimFloatDepthM, 0);
        _player.Velocity = Vector3.Zero;
        _player.Rotation = new Vector3(0, Mathf.Pi * 0.5f, 0);
        await Frames(10);
        grabs = _player.Grabs;
        var swimming = _player.IsSwimming;
        _player.SetControlInput(new Vector2(0, 1));
        for (var i = 0; i < 900 && _player.GlobalPosition.X - b.X > -0.15f; i++) await Frames(1);
        _player.SetControlInput(Vector2.Zero);
        await Frames(10);
        Report(swimming && _player.Grabs == grabs && !_player.IsSwimming && _player.IsOnFloor() && Mathf.Abs(_player.GlobalPosition.Y - b.Y) < 0.006f,
            $"a swimmer walks out up a shelving shore (feet at {Text(_player.GlobalPosition - b)})");

        // Climbing down a bank into deep water hands the body to swimming before the eye goes under: a wall 22 cm above the
        // water on the east bank, its face running on down to the bed.
        Solid(b + new Vector3(1.35f, 0.1f, -0.35f), new BoxShape3D { Size = new Vector3(0.3f, 0.2f, 0.2f) });
        await Frames(2);
        _player.GlobalPosition = b + new Vector3(1.2f - _player.BodyRadiusM - 0.0015f, 0.06f, -0.35f);
        _player.Velocity = Vector3.Zero;
        _player.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0);
        grabs = _player.Grabs;
        _player.SetControlInput(new Vector2(0, 1));
        for (var i = 0; i < 30 && _player.Grabs == grabs; i++) await Frames(1);
        var grabbedBank = _player.Grabs > grabs;
        _player.SetControlInput(new Vector2(0, -1));
        var eyeUnder = 0;
        var handedOver = false;
        for (var i = 0; i < 240 && !handedOver; i++)
        {
            await Frames(1);
            if (_player.GlobalPosition.Y + _player.EyeCamera.Position.Y < b.Y + surface - 0.001f) eyeUnder++;
            handedOver = _player.IsSwimming && !_player.IsClimbing;
        }
        _player.SetControlInput(Vector2.Zero);
        await Frames(60);
        Report(grabbedBank && handedOver && eyeUnder == 0 && _player.IsSwimming,
            $"climbing down a bank into deep water hands the body to swimming, the eye never under (grabbed {grabbedBank}, handed over {handedOver}, ticks with the eye under: {eyeUnder})");

        // Creation forces act on a swimmer: a push carries it across the water.
        var drifting = _player.GlobalPosition;
        _player.SetCreationEffects(new Vector3(-0.6f, 0, 0), 0, new Callable(this, MethodName.AllowAll));
        await Frames(60);
        _player.SetCreationEffects(Vector3.Zero, 0, new Callable());
        var drift = drifting.X - _player.GlobalPosition.X;
        Report(drift > 0.05f && _player.IsSwimming, $"a creation push carries a swimmer across the water ({drift * 100:0.0} cm in 1 s)");

        // Dry ground under a raised basin of water stays dry: walking under it never swims.
        Solid(b + new Vector3(-1.0f, 0.27f, 1.0f), new BoxShape3D { Size = new Vector3(0.4f, 0.02f, 0.4f) });
        var basinSheet = new SurfaceTool();
        basinSheet.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var corner in new[] { new Vector2(-1.2f, 0.8f), new Vector2(-0.8f, 0.8f), new Vector2(-0.8f, 1.2f), new Vector2(-1.2f, 0.8f), new Vector2(-0.8f, 1.2f), new Vector2(-1.2f, 1.2f) })
            basinSheet.AddVertex(new Vector3(corner.X, 0.38f, corner.Y));
        var basin = RoomWater.CreateCollider(new[] { ((Shape3D)basinSheet.Commit().CreateTrimeshShape(), Transform3D.Identity) });
        basin.Position = b;
        AddChild(basin);
        await Frames(2);
        Check(_player.TryTeleportTo(b + new Vector3(-1.5f, 0.003f, 1.0f)), "the player stands beside a raised basin");
        _player.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0);
        var swamDry = 0;
        _player.SetControlInput(new Vector2(0, 1));
        for (var i = 0; i < 180; i++)
        {
            await Frames(1);
            if (_player.IsSwimming) swamDry++;
        }
        _player.SetControlInput(Vector2.Zero);
        Report(swamDry == 0 && _player.IsOnFloor() && _player.GlobalPosition.X - b.X > -0.7f,
            $"walking under a raised basin of water never swims (swimming ticks {swamDry}, ends at {Text(_player.GlobalPosition - b)})");

        // A high, fast fall into deep water: the water catches the body before the eye goes under, every tick.
        // Six drop heights 1.5 cm apart, so one of them meets the surface just after a tick and moves 9 cm through it in the next.
        Check(_player.SetWorldPhysics(Preset("room_real", _player.WorldPhysicsRevision + 1)), "real gravity for the high fall");
        var underTicks = 0;
        var impact = 0.0f;
        var allSwimming = true;
        for (var drop = 0; drop < 6; drop++)
        {
            _player.GlobalPosition = b + new Vector3(0.8f, 1.5f + drop * 0.015f, 0);
            _player.Velocity = Vector3.Zero;
            for (var i = 0; i < 100; i++)
            {
                await Frames(1);
                impact = Mathf.Max(impact, -_player.Velocity.Y);
                if (_player.GlobalPosition.Y + _player.EyeCamera.Position.Y < b.Y + surface - 0.001f) underTicks++;
            }
            allSwimming &= _player.IsSwimming;
        }
        Report(underTicks == 0 && allSwimming, $"six 1.5 m falls into deep water at {impact:0.0} m/s never put the eye under, at any tick (ticks under: {underTicks})");

        // A fall into deep water lands in it, in all three gravities: no fall recovery, and the body floats back up.
        foreach (var preset in new[] { "room_tuned", "room_real", "room_floaty" })
        {
            Check(_player.SetWorldPhysics(Preset(preset, _player.WorldPhysicsRevision + 1)), $"{preset} gravity for the fall");
            _player.GlobalPosition = b + new Vector3(0.8f, 0.35f, 0);
            _player.Velocity = Vector3.Zero;
            var recoveries = _player.Recoveries;
            var lowest = float.MaxValue;
            var fastest = 0.0f;
            for (var i = 0; i < 300; i++)
            {
                await Frames(1);
                lowest = Mathf.Min(lowest, _player.GlobalPosition.Y - b.Y);
                fastest = Mathf.Max(fastest, -_player.Velocity.Y);
            }
            var at = _player.GlobalPosition - b;
            Report(_player.IsSwimming && lowest > bed + 0.005f && Mathf.Abs(at.X - 0.8f) < 0.01f && Mathf.Abs(at.Y - (surface - _player.SwimFloatDepthM)) < 0.008f && _player.Recoveries == recoveries,
                $"{preset}: a 35 cm fall into deep water is broken by it ({fastest:0.00} m/s, deepest feet {lowest:0.000} m over a bed at {bed:0.00}) and floats back to the surface");
            if (preset != "room_tuned") continue;
            // Jump at the surface: a leap out with the land jump's take-off, and back into the water.
            var jumps = _player.JumpsStarted;
            var highest = float.MinValue;
            _player.SetControlInput(Vector2.Zero, jump: true);
            for (var i = 0; i < 120; i++)
            {
                await Frames(1);
                highest = Mathf.Max(highest, _player.GlobalPosition.Y - b.Y);
            }
            Report(_player.JumpsStarted == jumps + 1 && highest > surface - _player.SwimFloatDepthM + 0.05f && _player.IsSwimming,
                $"jump at the surface leaps out of the water (feet up to {highest:0.000} m) and the water takes the body back");
        }
        Check(_player.SetWorldPhysics(Preset("room_tuned", _player.WorldPhysicsRevision + 1)), "back to tuned gravity after the falls");
        Check(_player.TryTeleportTo(new Vector3(-2, 0.003f, 3.2f)) && !_player.IsSwimming, "a teleport out of the water ends the swim");
        await Frames(10);
    }

    private static string Text(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"({v.X:0.000}, {v.Y:0.000}, {v.Z:0.000})");

    /// <summary>
    /// A tree as Codex brief 19 exports it, through RoomData and RoomBuilder: a drawn bark trunk, a hidden climbing pole
    /// ("drawn": false) continuing it up through the leaves to just below the crown's top, and a hidden one-sided cap on the
    /// crown. Written as a copy of the test room with the tree's meshes added as pinned GLB files. The builder must leave
    /// the hidden parts out of the scene and keep their collision one-sided; a climber goes up the trunk and the pole,
    /// passes up through the cap from inside, and stands on top.
    /// </summary>
    private async Task TestClimbableTree()
    {
        const string roomPath = "user://tests/lane_p_tree/test_room";
        var directory = ProjectSettings.GlobalizePath(roomPath);
        if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
        System.IO.Directory.CreateDirectory(directory + "/shell");
        var source = ProjectSettings.GlobalizePath("res://rooms/test_room");
        foreach (var file in System.IO.Directory.GetFiles(source + "/objects", "*.json", System.IO.SearchOption.AllDirectories))
        {
            var target = directory + "/objects/" + System.IO.Path.GetRelativePath(source + "/objects", file).Replace('\\', '/');
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            System.IO.File.Copy(file, target);
        }
        // The tree stands on the test room's floor at (1.2, 0, 1.0): trunk 15 cm, pole to 30 cm, crown top 32 cm.
        var foot = new Vector3(1.5f, 0, 1.0f);
        var trunk = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.02f, BottomRadius = 0.02f, Height = 0.15f, RadialSegments = 12, Rings = 1 }, Position = foot + Vector3.Up * 0.075f };
        var pole = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.012f, BottomRadius = 0.012f, Height = 0.15f, RadialSegments = 8, Rings = 1 }, Position = foot + Vector3.Up * 0.225f };
        var cap = new SurfaceTool();
        cap.Begin(Mesh.PrimitiveType.Triangles);
        const int segments = 12;
        var apex = foot + Vector3.Up * 0.32f;
        for (var i = 0; i < segments; i++)
        {
            Vector3 Rim(int k) => foot + new Vector3(Mathf.Cos(k * Mathf.Tau / segments) * 0.12f, 0.27f, Mathf.Sin(k * Mathf.Tau / segments) * 0.12f);
            var (p, q) = (Rim(i), Rim(i + 1));
            var outward = (q - apex).Cross(p - apex).Normalized();
            if (outward.Y < 0) outward = -outward;
            // Godot's front faces wind clockwise seen from the front (RoomBuilder.Triangle): outward and upward only.
            if ((p - apex).Cross(q - apex).Dot(outward) > 0) (p, q) = (q, p);
            foreach (var vertex in new[] { apex, p, q }) { cap.SetNormal(outward); cap.AddVertex(vertex); }
        }
        var crown = new MeshInstance3D { Mesh = cap.Commit() };
        var puddle = new SurfaceTool();
        puddle.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var corner in new[] { new Vector2(-1.6f, -1.2f), new Vector2(-1.2f, -1.2f), new Vector2(-1.2f, -0.8f), new Vector2(-1.6f, -1.2f), new Vector2(-1.2f, -0.8f), new Vector2(-1.6f, -0.8f) })
            puddle.AddVertex(new Vector3(corner.X, 0.01f, corner.Y));
        var water = new MeshInstance3D { Mesh = puddle.Commit() };
        var added = new List<(string Id, string File, MeshInstance3D Mesh, bool Drawn, bool Collides, string Role, string Material)>
        {
            ("shell:test_tree_bark", "shell/test_tree_bark.glb", trunk, true, true, "ground", "bark"),
            ("shell:tree_climb_bark", "shell/tree_climb_bark.glb", pole, false, true, "ground", "bark"),
            ("shell:tree_climb_foliage", "shell/tree_climb_foliage.glb", crown, false, true, "ground", "foliage"),
            ("shell:test_puddle", "shell/test_puddle.glb", water, true, false, "backdrop", "water"),
        };
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(source + "/room.json"))!.AsObject();
        var parts = manifest["shell"]!["parts"]!.AsArray();
        var files = manifest["files"]!.AsArray();
        foreach (var (id, file, mesh, drawn, collides, role, material) in added)
        {
            var holder = new Node3D { Name = "Export" };
            AddChild(holder);
            holder.AddChild(mesh);
            var document = new GltfDocument();
            var state = new GltfState();
            document.AppendFromScene(holder, state);
            var bytes = document.GenerateBuffer(state);
            holder.QueueFree();
            System.IO.File.WriteAllBytes(directory + "/" + file, bytes);
            var part = new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = id, ["role"] = role, ["geometry"] = new System.Text.Json.Nodes.JsonObject { ["kind"] = "mesh", ["mesh"] = file },
                ["collides"] = collides, ["material_role"] = material, ["base_color"] = "#6f5a44",
            };
            if (!drawn) part["drawn"] = false;
            parts.Add(part);
            files.Add(new System.Text.Json.Nodes.JsonObject { ["path"] = file, ["sha256"] = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), ["bytes"] = bytes.Length });
        }
        System.IO.File.WriteAllText(directory + "/room.json", manifest.ToJsonString(), new UTF8Encoding(false));
        RoomData room;
        try { room = RoomData.Load(roomPath); }
        catch (System.Exception error)
        {
            Check(false, "the test room with a climbable tree loads: " + error.Message);
            return;
        }
        Check(room.Shell.Single(p => p.Id == "shell:tree_climb_bark").Drawn == false && room.Shell.Single(p => p.Id == "shell:test_tree_bark").Drawn,
            "RoomData reads \"drawn\": false, and a part without it is drawn");
        var built = RoomBuilder.Build(room);
        built.Position = new Vector3(0, 0, -70);
        AddChild(built);
        await Frames(3);
        Node3D Part(string id) => built.GetNode<Node3D>("Shell/" + RoomBuilder.NodeName(id));
        bool Visible(Node3D node) => node.FindChildren("*", "MeshInstance3D", true, false).Count > 0;
        IEnumerable<ConcavePolygonShape3D> Concave(Node3D node) => node.GetChildren().OfType<CollisionShape3D>().Select(c => c.Shape).OfType<ConcavePolygonShape3D>();
        Check(!Visible(Part("shell:tree_climb_bark")) && !Visible(Part("shell:tree_climb_foliage")) && Visible(Part("shell:test_tree_bark")) && Visible(Part("shell:test_puddle")),
            "the builder never shows a part that is not drawn, and shows the rest");
        Check(Concave(Part("shell:tree_climb_bark")).Any() && Concave(Part("shell:tree_climb_foliage")).Any() &&
              new[] { "shell:test_tree_bark", "shell:tree_climb_bark", "shell:tree_climb_foliage" }.SelectMany(id => Concave(Part(id))).All(s => !s.BackfaceCollision),
            "collision-only parts keep their collision, and mesh collision is built one-sided (backface_collision off)");
        // Layer 5, "hidden": the parts never drawn collide on it alone, so sight, reach, placement, drops, the camera arms and
        // the navigation bake (all world layer only) pass through them, while bodies (RoomBuilder.BodyMask) still climb and stand on them.
        Check(Part("shell:tree_climb_bark") is StaticBody3D { CollisionLayer: RoomBuilder.HiddenLayer } && Part("shell:tree_climb_foliage") is StaticBody3D { CollisionLayer: RoomBuilder.HiddenLayer } &&
            Part("shell:test_tree_bark") is StaticBody3D { CollisionLayer: RoomBuilder.WorldLayer } &&
            (_player.CollisionMask & RoomBuilder.HiddenLayer) != 0 && (_companion.CollisionMask & RoomBuilder.HiddenLayer) != 0,
            "parts that are never drawn collide on the hidden layer; the drawn trunk on the world layer; both bodies collide with both");
        var capTop = built.Position + foot + new Vector3(0.06f, 0.5f, 0);
        var sightRay = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(capTop, capTop + Vector3.Down * 0.6f, RoomBuilder.WorldLayer));
        var bodyRay = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(capTop, capTop + Vector3.Down * 0.6f, RoomBuilder.BodyMask));
        Check(sightRay.Count > 0 && sightRay["position"].AsVector3().Y < built.Position.Y + 0.01f && bodyRay.Count > 0 && bodyRay["position"].AsVector3().Y > built.Position.Y + 0.25f,
            "a world-layer ray (sight, placement, drops) passes through the hidden crown cap to the floor; a body's ray stops on the cap");
        var puddleArea = Part("shell:test_puddle").GetNodeOrNull<Area3D>(RoomWater.ColliderName);
        Check(puddleArea != null && puddleArea.CollisionLayer == RoomWater.Layer && !puddleArea.Monitoring && Part("shell:test_puddle") is not CollisionObject3D,
            "a water part gets a query-only collider on the water layer, and no body");
        var puddleDepth = RoomWater.DepthAt(GetWorld3D().DirectSpaceState, built.Position + new Vector3(-1.4f, 0.02f, -1.0f));
        Report(Mathf.Abs(puddleDepth - 0.01f) < 0.001f, $"RoomWater measures the built room's water (a 1 cm puddle: {puddleDepth * 100:0.00} cm)");
        // Refused: a part neither drawn nor colliding.
        var refused = manifest.DeepClone().AsObject();
        refused["shell"]!["parts"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = "shell:nothing", ["role"] = "backdrop", ["geometry"] = new System.Text.Json.Nodes.JsonObject { ["kind"] = "mesh", ["mesh"] = "shell/tree_climb_bark.glb" },
            ["collides"] = false, ["drawn"] = false, ["material_role"] = "bark",
        });
        System.IO.File.WriteAllText(directory + "/room.json", refused.ToJsonString(), new UTF8Encoding(false));
        var refusedMessage = "";
        try { RoomData.Load(roomPath); }
        catch (RoomLoadException error) { refusedMessage = error.Message; }
        Check(refusedMessage.Contains("neither drawn nor collides"), "a part neither drawn nor colliding is refused: " + refusedMessage);

        // The climb: up the trunk, up the hidden pole through the leaves, up through the one-sided cap, standing on top.
        var tree = built.Position + foot;
        Check(_player.TryTeleportTo(tree + new Vector3(-0.3f, 0.003f, 0)), "the player stands west of the tree");
        _player.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0);
        var grabs = _player.Grabs;
        var pulls = _player.PullOvers;
        var highestClimbing = 0.0f;
        var frames = 0;
        _player.SetControlInput(new Vector2(0, 1));
        for (; frames < 900 && !(_player.PullOvers > pulls && _player.IsOnFloor() && !_player.IsClimbing); frames++)
        {
            await Frames(1);
            if (_player.IsClimbing) highestClimbing = Mathf.Max(highestClimbing, _player.GlobalPosition.Y - tree.Y);
        }
        _player.SetControlInput(Vector2.Zero);
        await Frames(30);
        var onTop = _player.GlobalPosition - tree;
        var capHeight = 0.32f - 0.05f * new Vector2(onTop.X, onTop.Z).Length() / 0.12f;
        Report(_player.Grabs > grabs && _player.PullOvers > pulls && _player.IsOnFloor() && onTop.Y > 0.29f && Mathf.Abs(onTop.Y - capHeight) < 0.01f,
            $"a climber goes up the trunk and the hidden pole, up through the one-sided cap from inside, and stands on top (feet at {Text(onTop)}, the cap there at {capHeight:0.000} m; climbed to {highestClimbing:0.000} m in {frames / 60.0f:0.0} s)");
        // From above, the cap holds: a body dropped on it from 10 cm lands on it instead of passing through.
        _player.GlobalPosition = tree + new Vector3(0.04f, 0.42f, 0);
        _player.Velocity = Vector3.Zero;
        await Frames(90);
        var landed = _player.GlobalPosition - tree;
        Report(_player.IsOnFloor() && landed.Y > 0.29f, $"a body falling onto the crown lands on the cap (feet at {Text(landed)})");
        Check(_player.TryTeleportTo(new Vector3(-2, 0.003f, 3.2f)), "back to the companion route after the tree");
        built.QueueFree();
        await Frames(5);
        System.IO.Directory.Delete(ProjectSettings.GlobalizePath("user://tests/lane_p_tree"), true);
    }

    /// <summary>
    /// The Gubble floats (founder, 8 October: "a ghost bubble, set apart from the world"; it never climbs, swims or gets
    /// stuck). A bank, a 20 cm deep pool and a 30 cm cliff, off the walkable map: sent to the player on the cliff top, it
    /// hovers over the water (never in it), rises up the cliff's face (never through it) and arrives level with the player,
    /// then bobs gently in place. Off the walkable map, so the walk cannot reach and it floats.
    /// </summary>
    private async Task TestGubbleFloats()
    {
        var b = new Vector3(0, 0, -60);
        const float surface = -0.02f;
        Solid(b + new Vector3(-0.5f, -0.125f, 0), new BoxShape3D { Size = new Vector3(1.0f, 0.25f, 1.0f) });       // the bank, top at 0
        Solid(b + new Vector3(0.4f, -0.225f, 0), new BoxShape3D { Size = new Vector3(0.8f, 0.05f, 1.0f) });         // the pool's bed at -0.2
        Solid(b + new Vector3(1.2f, 0.025f, 0), new BoxShape3D { Size = new Vector3(0.8f, 0.55f, 1.0f) });         // the cliff, top at 0.3
        var sheet = new SurfaceTool();
        sheet.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var corner in new[] { new Vector2(0, -0.5f), new Vector2(0.8f, -0.5f), new Vector2(0.8f, 0.5f), new Vector2(0, -0.5f), new Vector2(0.8f, 0.5f), new Vector2(0, 0.5f) })
            sheet.AddVertex(new Vector3(corner.X, surface, corner.Y));
        var water = RoomWater.CreateCollider(new[] { ((Shape3D)sheet.Commit().CreateTrimeshShape(), Transform3D.Identity) });
        water.Position = b;
        AddChild(water);
        await Frames(3);
        Check(_companion.Floats && !_companion.CanClimb && !_companion.CanSwim && !_player.Floats, "the Gubble floats; the player walks, climbs and swims");
        Check(_player.TryTeleportTo(b + new Vector3(1.1f, 0.31f, 0)) && _companion.TryTeleportTo(b + new Vector3(-0.3f, 0.01f, 0)),
            "the player on the cliff top, the Gubble on the far bank of the pool");
        await Frames(20);
        var rises = _companion.RiseTicks;
        var starts = _companion.FloatStarts;
        _companion.Come();
        var lowestOverWater = float.PositiveInfinity;
        var sawWater = false;
        var intoCliff = false;
        var wetOrClimbing = false;
        var frames = 0;
        for (; frames < 900 && _companion.CurrentIntent == "come"; frames++)
        {
            await Frames(1);
            var at = _companion.GlobalPosition - b;
            if (at.X > 0.03f && at.X < 0.77f) lowestOverWater = Mathf.Min(lowestOverWater, at.Y);
            sawWater |= _companion.HoversOverWater;
            intoCliff |= at.X > 0.8f + _companion.BodyRadiusM && at.Y < 0.30f - 0.002f;
            wetOrClimbing |= _companion.IsSwimming || _companion.IsClimbing;
        }
        var gap = PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition);
        var level = _companion.GlobalPosition.Y - _player.GlobalPosition.Y;
        Report(_companion.CurrentIntent == "stay" && _companion.FloatStarts > starts && sawWater && lowestOverWater > surface + 0.01f && !wetOrClimbing,
            $"the Gubble floats over the pool, never in it (lowest feet over the water {lowestOverWater:0.000} m, the surface at {surface:0.000} m; it never swam or climbed)");
        // (It rises toward the player's height as it goes, so it may clear the face before meeting it; the pens test the rise up a face.)
        Report(!intoCliff && gap <= CompanionAvatar.ComeArrivalM + 0.02f && Mathf.Abs(level) <= CompanionAvatar.LevelGapM,
            $"it rises up the 30 cm cliff, never through it, and arrives level with the player on top (rose over a face for {_companion.RiseTicks - rises} ticks; gap {gap:0.00} m, {level * 100:0.0} cm above the player's feet, {frames / 60.0f:0.0} s)");
        // At rest it hovers over the cliff top with a gentle bob, neither falling nor drifting off.
        var low = float.PositiveInfinity;
        var high = float.NegativeInfinity;
        var rest = _companion.GlobalPosition;
        for (var i = 0; i < 180; i++)
        {
            await Frames(1);
            low = Mathf.Min(low, _companion.GlobalPosition.Y);
            high = Mathf.Max(high, _companion.GlobalPosition.Y);
        }
        var support = _companion.HoverSupportY ?? float.NaN;
        Report(high - low > SmallPlayerController.HoverBobM && high - low < 3 * SmallPlayerController.HoverBobM && PlanarDistance(_companion.GlobalPosition, rest) < 0.01f &&
            low - support > _companion.HoverHeightM - 2 * SmallPlayerController.HoverBobM,
            $"at rest it hovers {(low + high) * 0.5f - support:0.000} m over the cliff top and bobs {(high - low) * 1000:0.0} mm, in place");
        // Back down: sent to a place on the bank, it floats down off the cliff and back over the water.
        _companion.GoTo(new Aabb(b + new Vector3(-0.3f, 0, 0), Vector3.Zero), 0.08f);
        frames = 0;
        for (; frames < 900 && _companion.CurrentIntent == "go_to"; frames++) await Frames(1);
        var home = _companion.GlobalPosition - b;
        Report(_companion.CurrentIntent == "stay" && home.X < -0.2f && home.Y > 0 && home.Y < 0.05f,
            $"sent back to the bank, it floats down off the cliff and back over the pool (at {Text(home)} after {frames / 60.0f:0.0} s)");
        Check(_player.TryTeleportTo(new Vector3(-2, 0.003f, 3.2f)) && _companion.TryTeleportTo(new Vector3(0.5f, 0.01f, 3.2f)), "both bodies back on the open floor");
        await Frames(10);
    }

    /// <summary>
    /// The founder's video (9 October, 4 s on a conifer in the island garage, F2): while the player climbed, the HUD flipped
    /// between "follow" and "follow · floating there" every few frames and the Gubble shook beside the player. A 50 cm cliff
    /// with its own walkable map: the player climbs it with the Gubble on follow. Floating off to the side is fine; starting
    /// to float again and again, flipping its mode and shaking up and down are not. Then it settles level with the player on top.
    /// </summary>
    private async Task TestGubbleBesideAClimber()
    {
        var b = new Vector3(0, 0, -100);
        // The ground west of the cliff, and the cliff as a ridge, its west face at x = 0.1. The ground stops at the cliff's
        // foot: under a box standing on a floor, the walkable map keeps the floor (Recast fills no solids), and the place
        // beside the climber's foot snapped in there.
        Solid(b + new Vector3(-0.65f, -0.05f, 0), new BoxShape3D { Size = new Vector3(1.5f, 0.1f, 3) });
        Solid(b + new Vector3(0.3f, 0.2f, 0), new BoxShape3D { Size = new Vector3(0.4f, 0.6f, 3) });
        var navigation = RoomNavigation.Create(this, this, new Aabb(b + new Vector3(-1.4f, -0.1f, -1.4f), new Vector3(2.8f, 0.8f, 2.8f)),
            WorldScaleProfile.Companion, _companion.StepHeightM * 0.75f);
        _companion.BindNavigation(navigation);
        for (var i = 0; i < 240 && !navigation.IsReady; i++) await Frames(1);
        Check(navigation.IsReady && _player.TryTeleportTo(b + new Vector3(-0.1f, 0.003f, 0)) && _companion.TryTeleportTo(b + new Vector3(-0.15f, 0.01f, 0.16f)),
            "the climber's cliff has its walkable map; the player at its foot, the Gubble beside");
        _player.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0);
        _companion.Follow();
        await Frames(60);
        var starts = _companion.FloatStarts;
        var modeFlips = 0;
        var restFlips = 0;
        var shakes = 0;
        var climbTicks = 0;
        var wasFloating = _companion.FloatingThere;
        var wasMoving = _companion.FollowMoving;
        var lastY = _companion.GlobalPosition.Y;
        var lastStep = 0.0f;
        var widest = 0.0f;
        var pulls = _player.PullOvers;
        _player.SetControlInput(new Vector2(0, 1));
        for (var i = 0; i < 900 && !(_player.PullOvers > pulls && _player.IsOnFloor() && !_player.IsClimbing); i++)
        {
            await Frames(1);
            // From the push into the face (where the founder's HUD flickered too) to standing on top.
            if (_player.IsClimbing) climbTicks++;
            if (_companion.FloatingThere != wasFloating) modeFlips++;
            if (_companion.FollowMoving != wasMoving) restFlips++;
            wasFloating = _companion.FloatingThere;
            wasMoving = _companion.FollowMoving;
            // Shaking: the body's height turning from rising to sinking (or back) faster than the bob ever moves it.
            var step = _companion.GlobalPosition.Y - lastY;
            if (Mathf.Abs(step) > 0.0005f && Mathf.Abs(lastStep) > 0.0005f && step * lastStep < 0) shakes++;
            if (Mathf.Abs(step) > 0.0005f) lastStep = step;
            lastY = _companion.GlobalPosition.Y;
            widest = Mathf.Max(widest, Mathf.Abs(_companion.GlobalPosition.Y - _player.GlobalPosition.Y));
        }
        _player.SetControlInput(Vector2.Zero);
        var seconds = climbTicks / 60.0f;
        var floatStarts = _companion.FloatStarts - starts;
        Report(climbTicks > 120 && floatStarts <= 1 && modeFlips <= 2 && shakes <= 1,
            $"beside a climber the Gubble floats up with them, steadily: walking in and {seconds:0.0} s of climbing, it started to float {floatStarts} times ({floatStarts / Mathf.Max(seconds, 0.01f):0.0} a second of climbing), " +
            $"its mode flipped {modeFlips} times, follow stopped and started {restFlips} times, its height turned back {shakes} times; at most {widest * 100:0.0} cm off the climber's height");
        // On top, at rest: level with the player, its mode settled, holding its height without sinking.
        await Frames(120);
        var settledFlips = 0;
        wasFloating = _companion.FloatingThere;
        var low = float.PositiveInfinity;
        var high = float.NegativeInfinity;
        for (var i = 0; i < 180; i++)
        {
            await Frames(1);
            if (_companion.FloatingThere != wasFloating) settledFlips++;
            wasFloating = _companion.FloatingThere;
            low = Mathf.Min(low, _companion.GlobalPosition.Y);
            high = Mathf.Max(high, _companion.GlobalPosition.Y);
        }
        var level = _companion.GlobalPosition.Y - _player.GlobalPosition.Y;
        Report(_player.IsOnFloor() && _player.GlobalPosition.Y - b.Y > 0.45f && Mathf.Abs(level) <= CompanionAvatar.LevelGapM && settledFlips == 0 && high - low < 3 * SmallPlayerController.HoverBobM,
            $"with the climber on top, the Gubble settles level with them ({level * 100:0.0} cm off) and stays put (mode flips {settledFlips}, height within {(high - low) * 1000:0.0} mm)");
        _companion.BindNavigation(_navigation);
        navigation.QueueFree();
        _companion.Stop();
        Check(_player.TryTeleportTo(new Vector3(-2, 0.003f, 3.2f)) && _companion.TryTeleportTo(new Vector3(0.5f, 0.01f, 3.2f)), "both bodies back on the open floor");
        await Frames(10);
    }

    /// <summary>
    /// The founder on the magnified island (13 October): the Gubble got lost reaching the player and ended up stuck high in the
    /// sky, saying it could not get there. Its mesas stand 10 to 25 m over the land; floating off one, the Gubble found no ground
    /// within the 3 m it looked down and held its height, over the player and never level with them. A 10 m mesa in a room's
    /// bounds, the Gubble on top and the player on the land 3 m off its foot: come floats off the edge, sinks to the player and
    /// arrives level, never blocked for long. Then the same without bounds (nothing under it within reach): it settles to the
    /// player's height instead of holding.
    /// </summary>
    private async Task TestGubbleOffATallLedge()
    {
        var b = new Vector3(60, 0, -120);
        Solid(b + new Vector3(0, -0.05f, 0), new BoxShape3D { Size = new Vector3(10, 0.1f, 10) });          // the land, its top at 0
        Solid(b + new Vector3(-2, 5, 0), new BoxShape3D { Size = new Vector3(2, 10, 2) });                // the mesa, its top at 10 m
        var bounds = new Aabb(b + new Vector3(-5, -0.1f, -5), new Vector3(10, 12, 10));
        _companion.BindNavigation(null);
        foreach (var withBounds in new[] { true, false })
        {
            if (withBounds) Check(_player.SetPlayableBounds(bounds) && _companion.SetPlayableBounds(bounds), "the mesa's land has the room's bounds");
            else { _player.ClearPlayableBounds(); _companion.ClearPlayableBounds(); }
            Check(_player.TryTeleportTo(b + new Vector3(2, 0.003f, 0)) && _companion.TryTeleportTo(b + new Vector3(-2.3f, 10.01f, 0)),
                "the Gubble on the 10 m mesa, the player on the land 3 m off its foot");
            await Frames(20);
            _companion.Come();
            var serial = _companion.IntentSerial;
            var highest = float.NegativeInfinity;
            var blocked = 0;
            var longestBlocked = 0;
            var frames = 0;
            for (; frames < 3600 && _companion.CurrentIntent == "come"; frames++)
            {
                await Frames(1);
                highest = Mathf.Max(highest, _companion.GlobalPosition.Y - b.Y);
                blocked = _companion.GoalBlocked ? blocked + 1 : 0;
                longestBlocked = Mathf.Max(longestBlocked, blocked);
            }
            var gap = PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition);
            var level = _companion.GlobalPosition.Y - _player.GlobalPosition.Y;
            // Sinking 10 m at today's 0.25 m/s took 40 s and more; far over its height it sinks faster (FarGapMps).
            Report(_companion.ComeArrivedSerial == serial && gap <= CompanionAvatar.ComeArrivalM + 0.02f && Mathf.Abs(level) <= CompanionAvatar.LevelGapM &&
                   longestBlocked < 60 && highest < 10.3f && frames < 35 * 60,
                $"{(withBounds ? "in a room's bounds" : "with no bounds, over nothing within reach")}, come floats the Gubble off a 10 m mesa and down to the player, arriving level within 35 s " +
                $"(after {frames / 60.0f:0.0} s: {gap:0.00} m away, {level * 100:0.0} cm above their feet; highest {highest:0.00} m; blocked at most {longestBlocked} ticks running)");
            if (!withBounds) continue;
            // Left staying high over the land (as when the host gives up on a goal and stops it): it sinks to the land below.
            _companion.Stay();
            _companion.GlobalPosition = b + new Vector3(1, 9, 1);
            _companion.Velocity = Vector3.Zero;
            _companion.ResetPhysicsInterpolation();
            await Frames(120);
            var dropped = 9 - (_companion.GlobalPosition.Y - b.Y);
            Report(dropped > 1.0f && _companion.HoverSupportY is { } ground && Mathf.Abs(ground - b.Y) < 0.01f,
                $"left staying 9 m over the land in a room's bounds, the Gubble finds the land and sinks toward it ({dropped:0.00} m in 2 s), never hanging in the sky");
        }
        _companion.BindNavigation(_navigation);
        _companion.Stop();
        Check(_player.TryTeleportTo(new Vector3(-2, 0.003f, 3.2f)) && _companion.TryTeleportTo(new Vector3(0.5f, 0.01f, 3.2f)), "both bodies back on the open floor");
        await Frames(10);
    }

    /// <summary>
    /// A come that arrives by floating holds its height beside the player (on a pillar top, it does not sink to the ground below).
    /// While the player stays, it holds; once the player has gone, the hold goes and the Gubble, staying, settles over the ground
    /// (it used to stay up there for good: one of the ways it ended high in the sky on the magnified island).
    /// </summary>
    private async Task TestGubbleLetsAHeldHeightGo()
    {
        var b = new Vector3(60, 0, -140);
        Solid(b + new Vector3(0, -0.05f, 0), new BoxShape3D { Size = new Vector3(3, 0.1f, 3) });           // the ground, its top at 0
        Solid(b + new Vector3(0, 0.2f, 0), new BoxShape3D { Size = new Vector3(0.06f, 0.4f, 0.06f) });    // a pillar, its top at 0.4 m
        _companion.BindNavigation(null);
        Check(_player.TryTeleportTo(b + new Vector3(0, 0.403f, 0)) && _companion.TryTeleportTo(b + new Vector3(0.5f, 0.01f, 0)),
            "the player on a pillar top 40 cm up, the Gubble on the ground");
        await Frames(20);
        _companion.Come();
        var serial = _companion.IntentSerial;
        for (var i = 0; i < 600 && _companion.CurrentIntent == "come"; i++) await Frames(1);
        await Frames(60);
        var heldAt = _companion.GlobalPosition.Y - b.Y;
        Report(_companion.ComeArrivedSerial == serial && _companion.HoldingHeight && heldAt > 0.3f,
            $"come floats the Gubble up beside the player on the pillar, and it holds its height there ({heldAt:0.000} m over the ground)");
        var releases = _companion.HoldReleases;
        Check(_player.TryTeleportTo(b + new Vector3(-0.8f, 0.003f, 0.6f)), "the player gets down and walks off");
        var lowest = float.PositiveInfinity;
        for (var i = 0; i < 300; i++) { await Frames(1); lowest = Mathf.Min(lowest, _companion.GlobalPosition.Y - b.Y); }
        var support = (_companion.HoverSupportY ?? float.NaN) - b.Y;
        Report(_companion.CurrentIntent == "stay" && !_companion.HoldingHeight && _companion.HoldReleases == releases + 1 && lowest < 0.05f && Mathf.Abs(support) < 0.005f,
            $"with the player gone, the staying Gubble lets the height go and settles over the ground ({lowest:0.000} m over it after 5 s, over ground at {support:0.000} m)");
        _companion.BindNavigation(_navigation);
        _companion.Stop();
        Check(_player.TryTeleportTo(new Vector3(-2, 0.003f, 3.2f)) && _companion.TryTeleportTo(new Vector3(0.5f, 0.01f, 3.2f)), "both bodies back on the open floor");
        await Frames(10);
    }

    /// <summary>
    /// The watchdog for stuck up high. The player stands under a roof (a 1 m slab 30 cm up, open all round), the Gubble on the
    /// roof: no walk from the roof reaches them, so it floats straight for them and comes to rest over them on the roof, the roof
    /// between. It used to report blocked until the host gave up ("can't get there"); now it makes for open air beside the
    /// roof, comes down there and walks in under it to the player.
    /// </summary>
    private async Task TestGubbleStuckOverARoof()
    {
        var b = new Vector3(60, 0, -160);
        Solid(b + new Vector3(0, -0.05f, 0), new BoxShape3D { Size = new Vector3(3, 0.1f, 3) });           // the ground, its top at 0
        Solid(b + new Vector3(0, 0.32f, 0), new BoxShape3D { Size = new Vector3(1, 0.04f, 1) });          // the roof, 30 to 34 cm up
        var navigation = RoomNavigation.Create(this, this, new Aabb(b + new Vector3(-1.5f, -0.1f, -1.5f), new Vector3(3, 0.6f, 3)),
            WorldScaleProfile.Companion, _companion.StepHeightM * 0.75f);
        _companion.BindNavigation(navigation);
        for (var i = 0; i < 240 && !navigation.IsReady; i++) await Frames(1);
        Check(navigation.IsReady && _player.TryTeleportTo(b + new Vector3(0, 0.003f, 0)) && _companion.TryTeleportTo(b + new Vector3(0.3f, 0.35f, 0)),
            "the roof has its walkable map; the player under the roof, the Gubble on it");
        await Frames(20);
        var replans = _companion.HighReplans;
        _companion.Come();
        var serial = _companion.IntentSerial;
        var blocked = 0;
        var longestBlocked = 0;
        var landed = false;
        var frames = 0;
        for (; frames < 1200 && _companion.CurrentIntent == "come"; frames++)
        {
            await Frames(1);
            blocked = _companion.GoalBlocked ? blocked + 1 : 0;
            longestBlocked = Mathf.Max(longestBlocked, blocked);
            landed |= _companion.Landing != null;
        }
        var gap = PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition);
        var level = _companion.GlobalPosition.Y - _player.GlobalPosition.Y;
        Report(_companion.ComeArrivedSerial == serial && gap <= CompanionAvatar.ComeArrivalM + 0.02f && Mathf.Abs(level) <= CompanionAvatar.LevelGapM &&
               _companion.HighReplans > replans && landed && longestBlocked < 5 * 60,
            $"stuck on the roof over the player, the Gubble comes down in open air beside it and walks in under it to them " +
            $"(after {frames / 60.0f:0.0} s: {gap:0.00} m away, {level * 100:0.0} cm off their height; watchdog {_companion.HighReplans - replans} times; blocked at most {longestBlocked} ticks running)");
        _companion.BindNavigation(_navigation);
        navigation.QueueFree();
        _companion.Stop();
        Check(_player.TryTeleportTo(new Vector3(-2, 0.003f, 3.2f)) && _companion.TryTeleportTo(new Vector3(0.5f, 0.01f, 3.2f)), "both bodies back on the open floor");
        await Frames(10);
    }

    // ---- play distances (packet S8 of the 10x world, 13 October) ----

    /// <summary>
    /// Rising up a tall face (S8): the 10x island's mesas stand 10 to 25 m, and at 0.3 m/s a 20 m face took over a minute. A 20 m
    /// wall across the land between the Gubble and the player, with no walk round it (no walkable map): it floats over, rising at
    /// up to 1.5 m/s and easing at the top, and comes down the far side the same way. A 40 cm wall (today's islands) still rises
    /// at 0.3 m/s.
    /// </summary>
    private async Task TestGubbleRisesATallFace()
    {
        Report(Mathf.IsEqualApprox(CompanionAvatar.RiseMpsFor(0.3f, 0.4f, 0.3f), 0.3f) && Mathf.IsEqualApprox(CompanionAvatar.RiseMpsFor(2.9f, 3.0f, 0.3f), 0.3f) &&
               Mathf.IsEqualApprox(CompanionAvatar.RiseMpsFor(18f, 20f, 0.3f), 1.5f) && Mathf.IsEqualApprox(CompanionAvatar.RiseMpsFor(0.5f, 20f, 0.3f), 0.5f) &&
               Mathf.IsEqualApprox(CompanionAvatar.RiseMpsFor(0.1f, 20f, 0.3f), 0.3f) && Mathf.IsEqualApprox(CompanionAvatar.RiseMpsFor(5f, 6f, 0.3f), 0.6f),
            "the rise up a face: 0.3 m/s for a face within 3 m (today's islands), up to 1.5 m/s for a tall one, easing to 0.3 m/s at its top");
        _companion.BindNavigation(null);
        foreach (var (name, b, wallM) in new[] { ("a 20 m wall", new Vector3(60, 0, -200), 20f), ("a 40 cm wall", new Vector3(60, 0, -220), 0.4f) })
        {
            var tall = wallM > CompanionAvatar.TallFaceM;
            var half = tall ? 6f : 1.5f;
            Solid(b + new Vector3(0, -0.05f, 0), new BoxShape3D { Size = new Vector3(2 * half, 0.1f, 2 * half) });   // the land, its top at 0
            Solid(b + new Vector3(0, wallM * 0.5f, 0), new BoxShape3D { Size = new Vector3(tall ? 1f : 0.1f, wallM, 2 * half) });   // the wall across it
            var bounds = new Aabb(b + new Vector3(-half, -0.1f, -half), new Vector3(2 * half, wallM + 2, 2 * half));
            Check(_player.SetPlayableBounds(bounds) && _companion.SetPlayableBounds(bounds), $"{name}'s land has the room's bounds");
            var off = tall ? 2.5f : 0.5f;
            Check(_player.TryTeleportTo(b + new Vector3(off, 0.003f, 0)) && _companion.TryTeleportTo(b + new Vector3(-off, 0.01f, 0)), $"the Gubble and the player either side of {name}");
            await Frames(20);
            _companion.Come();
            var serial = _companion.IntentSerial;
            // The rise it was given while rising over the wall (the measured speed also holds the slide over the lip).
            float fastest = 0, highest = float.NegativeInfinity;
            var frames = 0;
            for (; frames < 120 * 60 && _companion.CurrentIntent == "come"; frames++)
            {
                await Frames(1);
                if (_companion.RisingOver) fastest = Mathf.Max(fastest, _companion.FloatRiseMps);
                highest = Mathf.Max(highest, _companion.GlobalPosition.Y - b.Y);
            }
            var gap = PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition);
            var level = _companion.GlobalPosition.Y - _player.GlobalPosition.Y;
            var arrived = _companion.ComeArrivedSerial == serial && gap <= CompanionAvatar.ComeArrivalM + 0.02f && Mathf.Abs(level) <= CompanionAvatar.LevelGapM;
            if (tall)
                Report(arrived && frames < 45 * 60 && fastest >= CompanionAvatar.TallFaceRiseMps - 0.01f && highest < wallM + 0.6f,
                    $"over {name} with no way round, the Gubble rises fast and eases at the top (fastest {fastest:0.00} m/s, highest {highest:0.00} m), and comes down to the player within 45 s ({frames / 60.0f:0.0} s; with today's 0.3 m/s rise and 0.25 m/s sink it took 54 s)");
            else
                Report(arrived && Mathf.IsEqualApprox(fastest, _companion.FloatRiseMps) && Mathf.IsEqualApprox(fastest, 0.30f),
                    $"over {name} the rise is today's 0.3 m/s (fastest {fastest:0.000} m/s; arrived after {frames / 60.0f:0.0} s)");
        }
        _player.ClearPlayableBounds();
        _companion.ClearPlayableBounds();
        _companion.BindNavigation(_navigation);
        _companion.Stop();
        Check(_player.TryTeleportTo(new Vector3(-2, 0.003f, 3.2f)) && _companion.TryTeleportTo(new Vector3(0.5f, 0.01f, 3.2f)), "both bodies back on the open floor");
        await Frames(10);
    }

    /// <summary>
    /// The Gubble catching up (S8, the founder's decision 4): following or coming, more than CatchUpM away and out of the player's
    /// view, it reappears beside them, out of view too. On an 80 m floor, the view the player's own eye: 30 m off in plain view it
    /// travels; with the player turned away it catches up, 0.5 to 4 m off, level, out of view; behind a wall in front of them it
    /// catches up too; within CatchUpM it never does, nor on a fetch's way back (Come(catchUp: false)).
    /// </summary>
    private async Task TestGubbleCatchesUp()
    {
        var b = new Vector3(-300, 0, 0);
        Solid(b + new Vector3(0, -0.05f, 0), new BoxShape3D { Size = new Vector3(80, 0.1f, 80) });
        _player.ClearPlayableBounds();
        _companion.ClearPlayableBounds();
        _companion.BindNavigation(null);
        var previous = GetViewport().GetCamera3D();
        _player.EyeCamera.MakeCurrent();
        async Task<int> Watching(int frames)
        {
            var start = _companion.CatchUps;
            for (var i = 0; i < frames && _companion.CatchUps == start; i++) await Frames(1);
            return _companion.CatchUps - start;
        }
        async Task Place(float playerYaw, Vector3 gubble)
        {
            _companion.Stop();
            Check(_player.TryTeleportTo(b + new Vector3(0, 0.003f, 0)) && _companion.TryTeleportTo(b + gubble + Vector3.Up * 0.01f), $"the player at the floor's middle, the Gubble at {gubble}");
            _player.Rotation = new Vector3(0, playerYaw, 0);
            await Frames(5);
        }
        await Place(0, new Vector3(0, 0, -30));
        var seen = _companion.BodyInPlayerView(_companion.GlobalPosition);
        _companion.Follow();
        Report(seen && await Watching(90) == 0, "30 m off in plain view, following, the Gubble travels and never pops");

        await Place(Mathf.Pi, new Vector3(0, 0, -30));
        var from = _companion.GlobalPosition;
        _companion.Follow();
        var caught = await Watching(60);
        var spot = _companion.GlobalPosition;
        var gap = PlanarDistance(spot, _player.GlobalPosition);
        Report(caught == 1 && gap >= 0.45f && gap <= 4.1f && Mathf.Abs(spot.Y - _player.GlobalPosition.Y) <= CompanionAvatar.LevelGapM && !_companion.BodyInPlayerView(spot) &&
               _companion.LastCatchUp is { } last && last.From.DistanceTo(from) < 1.0f && last.To.DistanceTo(spot) < 0.05f && _companion.CurrentIntent == "follow",
            $"30 m behind the player, out of view, following: it catches up beside them ({gap:0.00} m off, out of view) and follows on");

        await Place(0, new Vector3(0, 0, -30));
        var wall = Solid(b + new Vector3(0, 1, -15), new BoxShape3D { Size = new Vector3(8, 2, 0.2f) });
        await Frames(2);
        seen = _companion.BodyInPlayerView(_companion.GlobalPosition);
        _companion.Come();
        Report(!seen && await Watching(60) == 1 && _companion.CurrentIntent is "come" or "stay", "30 m off behind a wall in front of the player, coming: it catches up");
        wall.QueueFree();

        await Place(0, new Vector3(0, 0, 30));
        _companion.Come(catchUp: false);
        Report(await Watching(90) == 0, "30 m behind the player on a fetch's way back (Come(catchUp: false)) it never catches up");
        await Place(0, new Vector3(0, 0, 20));
        _companion.Follow();
        Report(await Watching(90) == 0, $"20 m behind, within {CompanionAvatar.CatchUpM:0} m, it never catches up");

        previous?.MakeCurrent();
        _companion.BindNavigation(_navigation);
        _companion.Stop();
        Check(_player.TryTeleportTo(new Vector3(-2, 0.003f, 3.2f)) && _companion.TryTeleportTo(new Vector3(0.5f, 0.01f, 3.2f)), "both bodies back on the open floor");
        _player.Rotation = Vector3.Zero;
        await Frames(10);
    }

    /// <summary>
    /// A long trip walks where it can (S8: a 72 m come on the 10x island floated 73 s of its 80 once it started to float). The
    /// Gubble starts 60 cm up in the air (no walk starts there: it floats), 10 m from the player across the walkable floor: once
    /// low over the ground it asks again, and walks the rest. A short trip (within LongRouteM, today's islands) floats on as before.
    /// </summary>
    private async Task TestGubbleWalksAgainOnALongTrip()
    {
        foreach (var (name, gubble, player, longTrip) in new[]
        {
            ("10 m", new Vector3(-4.5f, 0.6f, -5.3f), new Vector3(-4.5f, 0.003f, 5.0f), true),
            ("2 m", new Vector3(-4.5f, 0.6f, 2.5f), new Vector3(-4.5f, 0.003f, 4.5f), false),
        })
        {
            _companion.Stop();
            Check(_player.TryTeleportTo(player), "the player on the floor");
            _companion.GlobalPosition = gubble;
            _companion.Velocity = Vector3.Zero;
            _companion.ResetPhysicsInterpolation();
            await Frames(2);
            var starts = _companion.FloatStarts;
            var ends = _companion.FloatEnds;
            _companion.Come();
            var serial = _companion.IntentSerial;
            var frames = 0;
            for (; frames < 40 * 60 && _companion.CurrentIntent == "come"; frames++) await Frames(1);
            var arrived = _companion.ComeArrivedSerial == serial;
            if (longTrip)
                Report(arrived && _companion.FloatStarts > starts && _companion.FloatEnds > ends && _companion.FloatReason.StartsWith("start_off_level", System.StringComparison.Ordinal),
                    $"a come of {name} that starts up in the air floats ({_companion.FloatReason}), then walks once low over the floor, and arrives ({frames / 60.0f:0.0} s; back to walking {_companion.FloatEnds - ends} times)");
            else
                Report(arrived && _companion.FloatStarts > starts && _companion.FloatEnds == ends,
                    $"a come of {name} (today's islands) that starts up in the air floats all the way, as before ({frames / 60.0f:0.0} s)");
        }
        _companion.Stop();
        Check(_player.TryTeleportTo(new Vector3(-2, 0.003f, 3.2f)) && _companion.TryTeleportTo(new Vector3(0.5f, 0.01f, 3.2f)), "both bodies back on the open floor");
        await Frames(10);
    }

    /// <summary>
    /// The open sea (the founder, 9 October: "the kids want to be able to swim forever"; B goes home from anywhere). A 1 m
    /// island in a 60 cm deep sea, its reef 1.2 m out, the playable water to 2 m, the room's bounds at 3 m and the water meshes
    /// to 4 m. A swimmer swims on past all of them, nothing carries them, the water holds them up past the meshes and the
    /// Gubble follows; B takes them to the jetty (else the beach, else the spawn), standing and facing inland, the Gubble too;
    /// the far net takes them home hours out. Then the body's motion far out, where 32-bit positions get coarse.
    /// </summary>
    private async Task TestSeaEdge()
    {
        var b = new Vector3(0, 0, -80);
        const float surface = -0.02f;
        Solid(b + new Vector3(0, -0.3f, 0), new BoxShape3D { Size = new Vector3(1.0f, 0.6f, 1.0f) });        // the island, its top at 0
        Solid(b + new Vector3(0, -0.65f, 0), new BoxShape3D { Size = new Vector3(8.0f, 0.1f, 8.0f) });       // the sea floor at -0.6
        Solid(b + new Vector3(0, -1.05f, 6.5f), new BoxShape3D { Size = new Vector3(1.0f, 0.1f, 1.0f) });    // a deep ledge past the meshes, its top at -1.0
        Solid(b + new Vector3(3.0f, -0.25f, 2.0f), new BoxShape3D { Size = new Vector3(0.6f, 0.06f, 0.6f) });  // an offshore shelf, its top at -0.22, 32 cm over the floor
        var sheet = new SurfaceTool();
        sheet.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var corner in new[] { new Vector2(-4, -4), new Vector2(4, -4), new Vector2(4, 4), new Vector2(-4, -4), new Vector2(4, 4), new Vector2(-4, 4) })
            sheet.AddVertex(new Vector3(corner.X, surface, corner.Y));
        var water = RoomWater.CreateCollider(new[] { ((Shape3D)sheet.Commit().CreateTrimeshShape(), Transform3D.Identity) });
        water.Position = b;
        AddChild(water);
        Vector2[] Square(float half) => new[] { new Vector2(b.X - half, b.Z - half), new Vector2(b.X + half, b.Z - half), new Vector2(b.X + half, b.Z + half), new Vector2(b.X - half, b.Z + half) };
        var beach = new RoomSea.Beach("test_beach", b + new Vector3(0, 0.001f, 0.3f), 0, b + new Vector3(0, surface, 0.7f));
        // The jetty runs out east from the island's edge; its landward end on the island, facing inland is facing west.
        var jetty = new RoomSea.Jetty(b + new Vector3(0.4f, 0.0f, -0.2f), b + new Vector3(0.9f, 0.0f, -0.2f), -90, 0.16f, 0.0f);
        var sea = new RoomSea(surface, Square(0.5f), Square(1.2f), Square(2.0f), new[] { beach }, jetty);
        await Frames(3);
        Check(_player.TryTeleportTo(b + new Vector3(-0.2f, 0.01f, 0.2f)) && _companion.TryTeleportTo(b + new Vector3(0.2f, 0.01f, -0.3f)), "both bodies on the island");
        _player.SetSea(sea);
        _companion.SetSea(sea);
        Check(_player.SetPlayableBounds(new Aabb(b + new Vector3(-3, -1, -3), new Vector3(6, 3, 6))) && _companion.SetPlayableBounds(new Aabb(b + new Vector3(-3, -1, -3), new Vector3(6, 3, 6))),
            "the island's bounds lie out past the playable water");
        await Frames(3);
        var openBed = sea.OpenSeaBedAt(new Vector2(b.X + 4.5f, b.Z));
        Check(sea.OpenWater(GetWorld3D().DirectSpaceState, b + new Vector3(4.5f, surface - 0.06f, 0), 0.4f, 0.02f) is { Wet: true } open && Mathf.Abs(open.SurfaceY - surface) < 1e-5f && Mathf.Abs(open.BedY - openBed) < 1e-4f &&
              !sea.OpenWater(GetWorld3D().DirectSpaceState, b + new Vector3(0.2f, surface - 0.06f, 0), 0.4f, 0.02f).Wet &&
              sea.OpenWater(GetWorld3D().DirectSpaceState, b + new Vector3(3.0f, surface - 0.06f, 0), 0.4f, 0.02f) is { Wet: true } meshed && Mathf.Abs(meshed.BedY - (-0.6f)) < 0.001f,
            $"past the island the sea answers where the meshes stop: its surface, over the ground there or the open-sea bed ({openBed:0.000} m at 4.5 m); inside the coast it is dry");
        var ledge = sea.OpenWater(GetWorld3D().DirectSpaceState, b + new Vector3(0, surface - 0.06f, 6.5f), 0.4f, 0.02f);
        Report(ledge.Wet && Mathf.Abs(ledge.BedY - (-1.0f)) < 0.001f,
            $"real ground deeper than the open-sea bed is the bed there (Codex Sol's review): the ledge at -1.000 m reads {ledge.BedY:0.000} m (the open-sea bed there {sea.OpenSeaBedAt(new Vector2(b.X, b.Z + 6.5f)):0.000} m)");

        // The Gubble, sent out over the open sea past the bounds: it hovers over the water, never in it.
        Check(_companion.TryTeleportTo(b + new Vector3(0.2f, 0.01f, -0.3f)), "the Gubble on the island");
        _companion.GoTo(new Aabb(b + new Vector3(3.4f, 0, -0.4f), Vector3.Zero), 0.08f);
        var lowest = float.PositiveInfinity;
        for (var i = 0; i < 600 && _companion.CurrentIntent == "go_to"; i++)
        {
            await Frames(1);
            if (_companion.GlobalPosition.X - b.X > 0.6f) lowest = Mathf.Min(lowest, _companion.GlobalPosition.Y - b.Y);
        }
        Report(_companion.CurrentIntent == "stay" && _companion.GlobalPosition.X - b.X > 3.2f && _companion.HoversOverWater && lowest > surface + 0.01f,
            $"the Gubble floats out over the sea past the bounds, hovering over it (lowest feet {lowest:0.000} m)");

        // Swim on out at the fast swim, past the reef, the playable water, the bounds and the water meshes.
        var stops = _player.BoundsStops;
        var trips = _player.HomeTrips;
        _player.GlobalPosition = b + new Vector3(0.6f, surface - _player.SwimFloatDepthM, 0);
        _player.Velocity = Vector3.Zero;
        _player.ResetPhysicsInterpolation();
        _player.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0);
        _companion.Follow();
        var furthest = 0.0f;
        var eyeUnder = 0;
        var dry = 0;
        var frames = 0;
        for (; frames < 1500 && _player.GlobalPosition.X - b.X < 5.5f; frames++)
        {
            _player.SetControlInput(new Vector2(0, 1), sprint: true);
            await Frames(1);
            furthest = Mathf.Max(furthest, _player.GlobalPosition.X - b.X);
            if (_player.Water.Wet && _player.GlobalPosition.Y + _player.EyeCamera.Position.Y < _player.Water.SurfaceY - 0.001f) eyeUnder++;
            if (!_player.IsSwimming) dry++;
        }
        _player.SetControlInput(Vector2.Zero);
        // The swim's own glide (3 cm from the fast swim) first, then three seconds of floating still.
        await Frames(60);
        var outAt = _player.GlobalPosition;
        await Frames(180);
        var drifted = PlanarDistance(_player.GlobalPosition, outAt);
        var gubbleGap = PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition);
        Report(_player.HomeTrips == trips && _player.BoundsStops == stops && furthest > 5.4f && _player.IsSwimming && dry == 0 && eyeUnder == 0 &&
            Mathf.Abs(_player.GlobalPosition.Y - (surface - _player.SwimFloatDepthM)) < 0.01f,
            $"the sea has no edge: a swimmer swims on past the reef, the playable water, the bounds and the water meshes, out to {furthest:0.00} m in {frames / 60.0f:0.0} s " +
            $"(taken home {_player.HomeTrips - trips} times, bound stops {_player.BoundsStops - stops}, ticks not swimming {dry}, eye under {eyeUnder}; floating at {Text(_player.GlobalPosition - b)}, the water {_player.Water.DepthM:0.00} m deep)");
        Report(drifted < 0.01f, $"out there nothing carries a swimmer who stops ({drifted * 100:0.0} cm in 3 s)");
        Report(gubbleGap < 0.4f && _companion.HoversOverWater && _companion.GlobalPosition.Y > surface + 0.005f,
            $"the Gubble follows out over the open sea, hovering over it ({gubbleGap:0.00} m from the player, {(_companion.GlobalPosition.Y - b.Y - surface) * 100:0.0} cm over the water)");

        // Diving, out in the open sea past the meshes (the founder's son: "dive into the water and swim down under the surface").
        _companion.Follow();
        var bedHere = sea.OpenSeaBedAt(new Vector2(_player.GlobalPosition.X, _player.GlobalPosition.Z));
        var gubbleLowest = float.PositiveInfinity;
        var diveStarts = _player.DiveStarts;
        _player.SetDiveInput(true, false);
        for (var i = 0; i < 180; i++) { await Frames(1); gubbleLowest = Mathf.Min(gubbleLowest, _companion.GlobalPosition.Y - b.Y); }
        var dived = surface - (_player.GlobalPosition.Y - b.Y);
        var eyeUnderWater = _player.EyeUnderWater;
        var gubbleNear = PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition);
        Report(_player.IsDiving && _player.IsSwimming && eyeUnderWater && dived > 0.25f && _player.DiveStarts == diveStarts + 1 && gubbleLowest > surface + 0.005f && gubbleNear < 0.4f,
            $"Ctrl dives: {dived * 100:0} cm under in 3 s, the eye under water, still swimming; the Gubble above the water all the while (lowest {(gubbleLowest - surface) * 100:0.0} cm over it, {gubbleNear:0.00} m away)");
        var deepest = float.PositiveInfinity;
        for (var i = 0; i < 600; i++) { await Frames(1); deepest = Mathf.Min(deepest, _player.GlobalPosition.Y - b.Y); gubbleLowest = Mathf.Min(gubbleLowest, _companion.GlobalPosition.Y - b.Y); }
        Report(deepest >= bedHere - 0.001f && deepest < bedHere + 0.02f && _player.IsSwimming && gubbleLowest > surface + 0.005f,
            $"held down, a diver reaches the open-sea bed, which has no collider, and never goes through it (deepest feet {deepest:0.000} m, the bed {bedHere:0.000} m)");
        // Looking up and swimming on: W takes the diver the way they look.
        _player.SetDiveInput(false, false);
        _player.EyeCamera.Rotation = new Vector3(0.6f, 0, 0);
        var lookStart = _player.GlobalPosition;
        for (var i = 0; i < 120; i++) { _player.SetControlInput(new Vector2(0, 1)); await Frames(1); }
        _player.SetControlInput(Vector2.Zero);
        var swam = _player.GlobalPosition - lookStart;
        Report(swam.Y > 0.08f && swam.X > 0.12f && _player.IsDiving,
            $"looking up with W held, the diver swims up and on, the way they look ({Text(swam)} in 2 s)");
        _player.EyeCamera.Rotation = Vector3.Zero;
        // Let go: a slow drift up; then Space: up to the surface, floating there again.
        await Frames(30);
        var driftFrom = _player.GlobalPosition.Y;
        await Frames(120);
        var drift = _player.GlobalPosition.Y - driftFrom;
        _player.SetDiveInput(false, true);
        for (var i = 0; i < 600 && _player.IsDiving; i++) await Frames(1);
        _player.SetDiveInput(false, false);
        await Frames(60);
        Report(drift > 0.04f && drift < 0.08f && !_player.IsDiving && _player.IsSwimming && Mathf.Abs(_player.GlobalPosition.Y - (surface - _player.SwimFloatDepthM)) < 0.01f && !_player.EyeUnderWater,
            $"let go, a diver drifts slowly up ({drift * 100:0.0} cm in 2 s); Space brings them up to float at the surface again");

        // Come to a deep diver (Codex Sol's review): the Gubble, over the water, could never be level with them and never arrived.
        _player.GlobalPosition = b + new Vector3(2.4f, surface - _player.SwimFloatDepthM, -2.0f);
        _player.ResetPhysicsInterpolation();
        _player.SetDiveInput(true, false);
        await Frames(150);
        _player.SetDiveInput(false, false);
        _companion.GlobalPosition = b + new Vector3(3.0f, surface + 0.03f, -2.4f);
        _companion.ResetPhysicsInterpolation();
        var cameSerial = _companion.ComeArrivedSerial;
        _companion.Come();
        for (var i = 0; i < 360 && _companion.CurrentIntent == "come"; i++) { _player.SetDiveInput(false, false); await Frames(1); }
        var diverDepth = surface - (_player.GlobalPosition.Y - b.Y);
        Report(_companion.CurrentIntent == "stay" && _companion.ComeArrivedSerial != cameSerial && _companion.GlobalPosition.Y > b.Y + surface,
            $"come to a diver {diverDepth * 100:0} cm down arrives over them at the surface ({PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition):0.00} m away, intent {_companion.CurrentIntent})");
        _player.SetDiveInput(false, true);
        for (var i = 0; i < 600 && _player.IsDiving; i++) await Frames(1);
        _player.SetDiveInput(false, false);

        // Under a shelf (Codex's reviews): the shelf's top is a roof over the diver, not the bed; diving on under it and out
        // the far side, the body swims all the way, and comes up past it.
        _player.GlobalPosition = b + new Vector3(2.45f, surface - _player.SwimFloatDepthM, 2.0f);
        _player.ResetPhysicsInterpolation();
        _player.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0);
        _player.EyeCamera.Rotation = Vector3.Zero;
        await Frames(10);
        var notSwimming = 0;
        var underShelf = 0;
        _player.SetDiveInput(true, false);
        for (var i = 0; i < 200; i++) { await Frames(1); if (!_player.IsSwimming) notSwimming++; }
        for (var i = 0; i < 360; i++)
        {
            _player.SetDiveInput(false, false);
            _player.SetControlInput(new Vector2(0, 1));
            await Frames(1);
            if (!_player.IsSwimming) notSwimming++;
            var x = _player.GlobalPosition.X - b.X;
            if (x > 2.72f && x < 3.28f) underShelf++;
        }
        _player.SetControlInput(Vector2.Zero);
        _player.SetDiveInput(false, true);
        for (var i = 0; i < 600 && _player.IsDiving; i++) { await Frames(1); if (!_player.IsSwimming) notSwimming++; }
        _player.SetDiveInput(false, false);
        await Frames(30);
        Report(notSwimming == 0 && underShelf > 30 && _player.IsSwimming && !_player.EyeUnderWater && _player.GlobalPosition.X - b.X > 3.3f,
            $"a diver swims on under an offshore shelf and out past it, never out of the water ({underShelf} ticks under it, {notSwimming} not swimming), and comes up beyond it (at {Text(_player.GlobalPosition - b)})");

        // Off a deep ledge (Codex Sol's review): from the -1 m ledge past the meshes, sideways off it, where the open-sea bed
        // (no collider) stands higher: still in the sea, held up by that bed, never through it.
        _player.GlobalPosition = b + new Vector3(0.2f, surface - _player.SwimFloatDepthM, 6.5f);
        _player.ResetPhysicsInterpolation();
        _player.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0);
        _player.SetDiveInput(true, false);
        for (var i = 0; i < 600; i++) await Frames(1);
        var onLedge = _player.GlobalPosition.Y - b.Y;
        var offNotSwimming = 0;
        var lowestOff = float.PositiveInfinity;
        for (var i = 0; i < 240; i++)
        {
            _player.SetControlInput(new Vector2(0, 1));
            await Frames(1);
            if (!_player.IsSwimming) offNotSwimming++;
            if (_player.GlobalPosition.X - b.X > 0.55f) lowestOff = Mathf.Min(lowestOff, _player.GlobalPosition.Y - b.Y - sea.OpenSeaBedAt(new Vector2(_player.GlobalPosition.X, _player.GlobalPosition.Z)));
        }
        _player.SetControlInput(Vector2.Zero);
        _player.SetDiveInput(false, true);
        for (var i = 0; i < 900 && _player.IsDiving; i++) await Frames(1);
        _player.SetDiveInput(false, false);
        Report(onLedge < -0.98f && offNotSwimming == 0 && lowestOff > -0.005f && _player.IsSwimming,
            $"off the -1 m ledge sideways a diver stays in the sea, on the open-sea bed and never through it (on the ledge at {onLedge:0.000} m; off it, lowest {lowestOff * 1000:0.0} mm from that bed; ticks not swimming {offNotSwimming})");

        // A worn glider (Codex Sol's review): the invention runtime sets its effect every tick while the body is inside the
        // room's bounds, with a guard that refuses anywhere outside them (creation_authority.gd _point_authorized), and clears it
        // outside. The guard took back every whole move across the old bounds, swimming included: a wall again.
        _player.GlobalPosition = b + new Vector3(2.6f, surface - _player.SwimFloatDepthM, 0);
        _player.Velocity = Vector3.Zero;
        _player.ResetPhysicsInterpolation();
        _player.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0);
        await Frames(10);
        var gliderGuard = new Callable(this, MethodName.InsideTheOldBounds);
        var guardOn = 0;
        for (var i = 0; i < 240 && _player.GlobalPosition.X - b.X < 3.6f; i++)
        {
            var inside = InsideTheOldBounds(_player.GlobalPosition);
            if (inside) { _player.SetCreationEffects(Vector3.Zero, 0.5f, gliderGuard); guardOn++; }
            else _player.SetCreationEffects(Vector3.Zero, 0, new Callable());
            _player.SetControlInput(new Vector2(0, 1), sprint: true);
            await Frames(1);
        }
        _player.SetCreationEffects(Vector3.Zero, 0, new Callable());
        _player.SetControlInput(Vector2.Zero);
        Report(_player.GlobalPosition.X - b.X >= 3.6f && guardOn > 0 && _player.IsSwimming,
            $"a glider worn, its guard on inside the room's old bounds ({guardOn} ticks), a swimmer still crosses them out to sea (at {Text(_player.GlobalPosition - b)})");

        // B, out there: the fade, then standing at the jetty's landward end facing inland, and the Gubble beside.
        Check(_player.RequestHome() && !_player.RequestHome(), "B starts the way home once (a second press on the way is refused)");
        var darkest = 0.0f;
        for (var i = 0; i < 90; i++) { await Frames(1); darkest = Mathf.Max(darkest, _player.HomeFade); }
        var atJetty = _player.GlobalPosition - jetty.RootM;
        var inlandYaw = Mathf.Atan2(1, 0);   // facing -X: west, from the jetty's end toward its root
        Report(_player.HomeTrips == trips + 1 && _player.LastHome == "jetty" && _player.IsOnFloor() && !_player.IsSwimming && !_player.GoingHome &&
            new Vector2(atJetty.X, atJetty.Z).Length() < 0.21f && Mathf.Abs(Mathf.AngleDifference(_player.Rotation.Y, inlandYaw)) < 0.01f && darkest > 0.95f &&
            PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition) < 0.3f,
            $"B takes the swimmer home: standing at the jetty's landward end ({Text(atJetty)} from it) facing inland, after the fade ({darkest:0.00}); the Gubble beside ({PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition):0.00} m)");

        // A booth round home (Codex Sol's review): thick walls over the three near rings and a thick roof just over the
        // player's head, with only the player's own spot clear. The Gubble on Stay out at sea still comes home, farther round or at its spawn.
        var booth = new List<StaticBody3D>();
        foreach (var (centre, size) in new[]
        {
            (new Vector3(0.22f, 0.15f, 0), new Vector3(0.36f, 0.3f, 0.8f)), (new Vector3(-0.22f, 0.15f, 0), new Vector3(0.36f, 0.3f, 0.8f)),
            (new Vector3(0, 0.15f, 0.22f), new Vector3(0.08f, 0.3f, 0.36f)), (new Vector3(0, 0.15f, -0.22f), new Vector3(0.08f, 0.3f, 0.36f)),
            (new Vector3(0, 0.36f, 0), new Vector3(0.8f, 0.5f, 0.8f)),
        })
            booth.Add(Solid(jetty.RootM + centre, new BoxShape3D { Size = size }));
        await Frames(2);
        _companion.Stay();
        _companion.GlobalPosition = b + new Vector3(4.6f, surface + 0.03f, 1.0f);
        _companion.ResetPhysicsInterpolation();
        _player.GlobalPosition = b + new Vector3(4.6f, surface - _player.SwimFloatDepthM, 0.6f);
        _player.ResetPhysicsInterpolation();
        await Frames(20);
        _player.RequestHome();
        await Frames(120);
        var boothGap = PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition);
        Report(_player.LastHome == "jetty" && PlanarDistance(_player.GlobalPosition, jetty.RootM) < 0.05f && boothGap < 1.2f && !_companion.HoversOverWater,
            $"in a booth at home with only the player's spot clear, the Gubble on Stay far out still comes home ({boothGap:0.00} m from the player, at {Text(_companion.GlobalPosition - b)})");
        foreach (var wall in booth) wall.QueueFree();
        await Frames(2);

        // The Gubble left on Stay far out at sea, with the four spots beside home it used to try all taken (Codex Sol's review):
        // it still comes home, beside the player or over their head, its old height forgotten.
        var pillars = new List<StaticBody3D>();
        var landing = jetty.RootM;
        foreach (var spot in new[] { new Vector3(0, 0, -0.16f), new Vector3(0, 0, 0.16f), new Vector3(-0.16f, 0, 0), new Vector3(-0.113f, 0, -0.113f) })
            pillars.Add(Solid(landing + spot + Vector3.Up * 0.15f, new BoxShape3D { Size = new Vector3(0.07f, 0.3f, 0.07f) }));
        await Frames(2);
        _companion.Stay();
        _companion.GlobalPosition = b + new Vector3(4.6f, surface + 0.03f, 1.0f);
        _companion.ResetPhysicsInterpolation();
        _player.GlobalPosition = b + new Vector3(4.6f, surface - _player.SwimFloatDepthM, 0.6f);
        _player.ResetPhysicsInterpolation();
        await Frames(20);
        _player.RequestHome();
        await Frames(90);
        var strandedGap = PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition);
        Report(_player.LastHome == "jetty" && strandedGap < 0.5f && _companion.GlobalPosition.Y > b.Y - 0.01f,
            $"with the spots beside home taken, the Gubble on Stay far out still comes home ({strandedGap:0.00} m from the player, at {Text(_companion.GlobalPosition - b)})");
        foreach (var pillar in pillars) pillar.QueueFree();
        await Frames(2);

        // A jetty whose root stands over the seabed half a metre down (Codex Astra's review): no home under water; B goes to the beach.
        var sunk = new RoomSea.Jetty(b + new Vector3(1.0f, -0.15f, 0.6f), b + new Vector3(1.5f, -0.15f, 0.6f), -90, 0.16f, -0.15f);
        _player.SetSea(new RoomSea(surface, Square(0.5f), Square(1.2f), Square(2.0f), new[] { beach }, sunk));
        _player.GlobalPosition = b + new Vector3(2.5f, surface - _player.SwimFloatDepthM, 0);
        _player.ResetPhysicsInterpolation();
        await Frames(5);
        _player.RequestHome();
        await Frames(80);
        Report(_player.LastHome == "test_beach" && _player.IsOnFloor() && !_player.IsSwimming && _player.GlobalPosition.Y > b.Y - 0.01f,
            $"a jetty over the seabed is no home: B lands on the beach instead, dry (home {_player.LastHome}, feet at {Text(_player.GlobalPosition - b)})");

        // Without a jetty, the nearest beach; without a sea, the spawn.
        _player.SetSea(new RoomSea(surface, Square(0.5f), Square(1.2f), Square(2.0f), new[] { beach }));
        _player.GlobalPosition = b + new Vector3(2.5f, surface - _player.SwimFloatDepthM, 0);
        _player.ResetPhysicsInterpolation();
        await Frames(5);
        _player.RequestHome();
        await Frames(80);
        var atBeach = _player.GlobalPosition - beach.WashAshoreM;
        var beachHome = _player.LastHome == "test_beach" && new Vector2(atBeach.X, atBeach.Z).Length() < 0.15f && _player.IsOnFloor();
        _player.SetSea(null);
        _player.ClearPlayableBounds();
        _player.RequestHome();
        await Frames(80);
        Report(beachHome && _player.LastHome == "spawn" && _player.IsOnFloor() && PlanarDistance(_player.GlobalPosition, new Vector3(-2, 0, 3.2f)) < 0.15f,
            $"with no jetty B goes to the nearest beach; with no sea, to the spawn (at {Text(_player.GlobalPosition)})");
        _player.SetSea(sea);
        _player.SetPlayableBounds(new Aabb(b + new Vector3(-3, -1, -3), new Vector3(6, 3, 6)));

        // B while climbing and while carrying is the host's and the hands' matter; here, B in mid-leap: it lands at home.
        Check(_player.TryTeleportTo(b + new Vector3(-0.2f, 0.01f, 0.2f)), "the player back on the island");
        await Frames(5);
        _player.SetControlInput(Vector2.Zero, jump: true);
        await Frames(4);
        var leaping = !_player.IsOnFloor() && _player.Velocity.Y > 0;
        _player.RequestHome();
        await Frames(80);
        Report(leaping && _player.LastHome == "jetty" && _player.IsOnFloor() && _player.Velocity.Length() < 0.01f,
            "B in mid-leap: the leap flies on through the fade, and the body stands at the jetty, still");

        // The sea wraps round to home (the founder, 9 October, late night): out past the wrap, a swimmer comes in from the other side.
        await TestWrap(sea, surface, b);

        // The far net, hours out: a swimmer who reaches it is taken home as by B. (The wrap, a minute out, is set aside for it:
        // it stays the last safety net, for a body the wrap does not take.)
        var wrapAt = _player.WrapM;
        _player.WrapM = float.PositiveInfinity;
        trips = _player.HomeTrips;
        _player.GlobalPosition = new Vector3(sea.MiddleM.X + _player.FarNetM - 0.1f, surface - _player.SwimFloatDepthM, sea.MiddleM.Y);
        _player.ResetPhysicsInterpolation();
        _player.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0);
        for (var i = 0; i < 180 && _player.HomeTrips == trips; i++) { _player.SetControlInput(new Vector2(0, 1), sprint: true); await Frames(1); }
        _player.SetControlInput(Vector2.Zero);
        await Frames(80);
        Report(_player.HomeTrips == trips + 1 && _player.LastHome == "jetty" && _player.IsOnFloor(),
            $"the far net, {_player.FarNetM / 1000:0.#} km out ({_player.FarNetM / 0.32f / 3600:0.0} h of fast swimming, {_player.FarNetM / 0.19f / 3600:0.0} h at the plain swim), takes a swimmer home");

        // The Gubble sent off on its own past the far net (Codex Astra's review): held at the net, reporting blocked.
        _companion.FarNetM = 6.0f;
        _companion.TryTeleportTo(b + new Vector3(0.2f, 0.01f, -0.3f));
        _companion.GoTo(new Aabb(b + new Vector3(9.0f, 0, 0), Vector3.Zero), 0.08f);
        var farthest = 0.0f;
        var gubbleBlocked = false;
        for (var i = 0; i < 1200; i++)
        {
            await Frames(1);
            farthest = Mathf.Max(farthest, PlanarDistance(_companion.GlobalPosition, b));
            gubbleBlocked |= _companion.GoalBlocked;
        }
        _companion.FarNetM = SmallPlayerController.DefaultFarNetM;
        Report(farthest <= 6.01f && farthest > 5.9f && gubbleBlocked && _companion.HoversOverWater,
            $"the Gubble sent past the far net on its own is held at it, hovering, and says blocked (farthest {farthest:0.00} m, the net set at 6 m for the test)");
        _companion.Stop();

        await MeasureFarOut(sea, surface);
        _player.WrapM = wrapAt;

        // The extension is untrusted room data: read only when every loop is bounded, every number finite and each beach in the bounds.
        var room = new Aabb(new Vector3(-5, -1, -5), new Vector3(10, 4, 10));
        string Extension(string loop, string beachAt, string level = "-0.02", string jetty = "null") =>
            "{\"level_m\":" + level + ",\"swim_depth_m\":0.08,\"coast\":{\"outline_m\":[[-1,-1],[1,-1],[1,1]]},\"reef\":{\"outline_m\":" + loop +
            ",\"crest_y_m\":-0.04,\"band_half_width_m\":0.08,\"passes\":[]},\"play_area\":{\"outline_m\":[[-3,-3],[3,-3],[3,3],[-3,3]]}," +
            "\"beaches\":[{\"id\":\"beach_00\",\"wash_ashore_m\":" + beachAt + ",\"yaw_deg\":0,\"water_m\":[0,-0.02,1.5]}],\"jetty\":" + jetty + "}";
        string Jetty(string root, string end, string deck = "0.03") => "{\"root_m\":" + root + ",\"end_m\":" + end + ",\"yaw_deg\":0,\"width_m\":0.16,\"deck_top_m\":" + deck + "}";
        string? Refusal(string json)
        {
            try { using var doc = System.Text.Json.JsonDocument.Parse(json); RoomSea.Parse(doc.RootElement, room); return null; }
            catch (RoomLoadException error) { return error.Message; }
        }
        var square = "[[-2,-2],[2,-2],[2,2],[-2,2]]";
        var huge = "[" + string.Join(",", Enumerable.Repeat("[0,0]", RoomSea.MaxOutlinePoints + 1)) + "]";
        Check(Refusal(Extension(square, "[0,0.01,0.5]")) == null && Refusal(Extension(huge, "[0,0.01,0.5]")) is { } tooLong && tooLong.Contains("4096") &&
            Refusal(Extension("[[0,0],[1,1]]", "[0,0.01,0.5]")) != null && Refusal(Extension(square, "[9,0.01,0.5]")) is { } outside && outside.Contains("outside the room's bounds") &&
            Refusal(Extension(square, "[0,0.01,0.5]", "1e400")) != null && Refusal(Extension(square, "[0,\"x\",0.5]")) != null,
            "x_landscape_sea is read only when bounded and finite: a loop over 4,096 points, a loop of two, a beach outside the bounds, a number out of range and a non-number are refused");
        var square2 = "[[-2,-2],[2,-2],[2,2],[-2,2]]";
        var farJetty = Refusal(Extension(square2, "[0,0.01,0.5]", jetty: Jetty("[900,0.03,900]", "[900,0.03,899]")));
        var sunkJetty = Refusal(Extension(square2, "[0,0.01,0.5]", jetty: Jetty("[1.5,-0.48,0]", "[2,0.03,0]")));
        Report(Refusal(Extension(square2, "[0,0.01,0.5]", jetty: Jetty("[1,0.03,0]", "[1.5,0.03,0]"))) == null && farJetty != null && sunkJetty != null,
            $"a jetty is read only inside the room's bounds and at its deck's height (Codex Sol's and Astra's reviews): one 1 km out is refused ({farJetty ?? "accepted"}), one whose root sits on the seabed is refused ({sunkJetty ?? "accepted"})");

        _player.SetSea(null);
        _companion.SetSea(null);
        _player.ClearPlayableBounds();
        _companion.ClearPlayableBounds();
        _companion.Stop();
        Check(_player.TryTeleportTo(new Vector3(-2, 0.003f, 3.2f)) && _companion.TryTeleportTo(new Vector3(0.5f, 0.01f, 3.2f)), "both bodies back on the open floor");
        await Frames(10);
    }

    /// <summary>
    /// The sea wraps round to home: the wrap lies past the look's seam; a swimmer going out past it is put opposite through the
    /// island's centre at the same distance and height, with the same heading and speed, so it swims back in, and never wraps
    /// straight back; the Gubble following comes too, beside them; a Gubble waiting on the island stays, and a come still finds
    /// the player across the seam; B takes a wrapped swimmer home.
    /// </summary>
    private async Task TestWrap(RoomSea sea, float surface, Vector3 b)
    {
        var bounds = new Aabb(b + new Vector3(-3, -1, -3), new Vector3(6, 3, 6));
        var seam = GarageGame.Native.Look.OpenSea.SeamFor(sea, bounds);
        Check(Mathf.IsEqualApprox(_player.WrapM, seam + SmallPlayerController.WrapPastSeamM) && _player.WrapM > seam,
            $"the wrap lies {SmallPlayerController.WrapPastSeamM} m past the look's seam ({seam:0.00} m from the island's centre): {_player.WrapM:0.00} m");
        var centre = sea.OpenSeaCentreM;
        var east = new Vector3(1, 0, 0);
        Vector3 Out(float metres) => new Vector3(centre.X, 0, centre.Y) + east * metres + Vector3.Up * (surface - _player.SwimFloatDepthM);
        _player.GlobalPosition = Out(_player.WrapM - 0.25f);
        _player.Velocity = Vector3.Zero;
        _player.ResetPhysicsInterpolation();
        _player.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0);
        _companion.GlobalPosition = _player.GlobalPosition + new Vector3(-0.05f, 0.08f, 0.15f);
        _companion.ResetPhysicsInterpolation();
        _companion.Follow();
        await Frames(2);
        var wraps = _player.Wraps;
        var carried = _companion.WrappedWithPlayer;
        var heading = _player.Rotation.Y;
        var before = Vector3.Zero;
        var offset = Vector3.Zero;
        for (var i = 0; i < 600 && _player.Wraps == wraps; i++)
        {
            before = _player.GlobalPosition;
            offset = _companion.GlobalPosition - _player.GlobalPosition;
            _player.SetControlInput(new Vector2(0, 1), sprint: true);
            await Frames(1);
        }
        var after = _player.GlobalPosition;
        var gubbleOffset = _companion.GlobalPosition - after;
        var flatAfter = new Vector2(after.X, after.Z) - centre;
        Report(_player.Wraps == wraps + 1 && flatAfter.X < 0 && Mathf.Abs(flatAfter.Length() - _player.WrapM) < 0.05f && Mathf.Abs(after.Y - before.Y) < 0.01f &&
               Mathf.IsEqualApprox(heading, _player.Rotation.Y) && _player.Velocity.X > 0.1f && _player.IsSwimming,
            $"out past the wrap the swimmer comes in from the opposite side, {flatAfter.Length():0.00} m out, at the same height, heading the same way, still swimming (now toward the island)");
        Report(_companion.WrappedWithPlayer == carried + 1 && gubbleOffset.DistanceTo(offset) < 0.08f && _companion.CurrentIntent == "follow",
            $"the Gubble following comes too, keeping its place beside the swimmer ({gubbleOffset.DistanceTo(offset) * 100:0.0} cm from it)");
        for (var i = 0; i < 120; i++) { _player.SetControlInput(new Vector2(0, 1), sprint: true); await Frames(1); }
        _player.SetControlInput(Vector2.Zero);
        var inward = new Vector2(_player.GlobalPosition.X, _player.GlobalPosition.Z).DistanceTo(centre);
        Report(_player.Wraps == wraps + 1 && inward < _player.WrapM - 0.3f, $"and it swims on in, never wrapping straight back ({inward:0.00} m out after 2 s)");

        // Turned round and swum out the other way: it wraps again, back to the first side.
        _player.Rotation = new Vector3(0, Mathf.Pi * 0.5f, 0);
        for (var i = 0; i < 900 && _player.Wraps == wraps + 1; i++) { _player.SetControlInput(new Vector2(0, 1), sprint: true); await Frames(1); }
        _player.SetControlInput(Vector2.Zero);
        Report(_player.Wraps == wraps + 2 && _player.GlobalPosition.X > centre.X, "turned round and swum out the other way, it wraps back again: the sea has no edge either way");

        // Out past the wrap already, floating still or swimming in: never wrapped. Only going out wraps.
        wraps = _player.Wraps;
        _player.GlobalPosition = Out(_player.WrapM + 0.5f);
        _player.Velocity = Vector3.Zero;
        _player.ResetPhysicsInterpolation();
        await Frames(30);
        var stillThere = _player.Wraps == wraps;
        _player.Rotation = new Vector3(0, Mathf.Pi * 0.5f, 0);
        for (var i = 0; i < 60; i++) { _player.SetControlInput(new Vector2(0, 1), sprint: true); await Frames(1); }
        _player.SetControlInput(Vector2.Zero);
        Report(stillThere && _player.Wraps == wraps && _player.GlobalPosition.X > centre.X,
            "a swimmer out past the wrap, floating still or swimming in, is never wrapped: only going out wraps (no flicker back and forth)");

        // A Gubble waiting on the island stays there; a come still finds the player across the seam.
        Check(_companion.TryTeleportTo(b + new Vector3(0.2f, 0.01f, -0.3f)), "the Gubble back on the island, waiting");
        _companion.Stay();
        _player.GlobalPosition = Out(_player.WrapM - 0.25f);
        _player.ResetPhysicsInterpolation();
        _player.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0);
        carried = _companion.WrappedWithPlayer;
        var waitingAt = _companion.GlobalPosition;
        wraps = _player.Wraps;
        for (var i = 0; i < 600 && _player.Wraps == wraps; i++) { _player.SetControlInput(new Vector2(0, 1), sprint: true); await Frames(1); }
        _player.SetControlInput(Vector2.Zero);
        await Frames(30);
        Report(_player.Wraps == wraps + 1 && _companion.WrappedWithPlayer == carried && _companion.GlobalPosition.DistanceTo(waitingAt) < 0.05f,
            "a Gubble waiting on the island, far from the swimmer, stays where it waits");
        _companion.Come();
        var arrived = false;
        for (var i = 0; i < 3000 && !arrived; i++)
        {
            await Frames(1);
            arrived = _companion.ComeArrivedSerial == _companion.IntentSerial || (_companion.CurrentIntent == "stay" && PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition) < 0.3f);
        }
        Report(arrived && PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition) < 0.3f,
            $"a come across the seam: the Gubble floats over the island to the swimmer on the far side and arrives ({PlanarDistance(_companion.GlobalPosition, _player.GlobalPosition):0.00} m away)");

        // B from out past the seam still goes home.
        var trips = _player.HomeTrips;
        _player.RequestHome();
        await Frames(80);
        Report(_player.HomeTrips == trips + 1 && _player.LastHome == "jetty" && _player.IsOnFloor(), "B from out past the wrap takes the swimmer home to the jetty");
        _companion.Stop();
    }

    /// <summary>
    /// 32-bit positions far out (the open sea, 9 October): the swimmer and the Gubble at 1, 2, 5 and 10 km from the island,
    /// swimming fast. Per tick: how uneven the body's steps are (their spread against the mean step, which smooth motion keeps
    /// near zero), and how much the Gubble's place beside the player shakes (the second difference of their offset, which the
    /// camera shows as the Gubble trembling). The float spacing there is printed beside them. The far net (FarNetM) is set
    /// from these: where both still read as smooth.
    /// </summary>
    private async Task MeasureFarOut(RoomSea sea, float surface)
    {
        var net = _player.FarNetM;
        _player.FarNetM = 20000;
        var lines = new List<string>();
        var smoothAtNet = false;
        foreach (var km in new[] { 0.1f, 1f, 2f, 5f, 10f })
        {
            var at = new Vector3(sea.MiddleM.X + km * 1000, surface - _player.SwimFloatDepthM, sea.MiddleM.Y);
            _player.GlobalPosition = at;
            _player.Velocity = Vector3.Zero;
            _player.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0);
            _player.ResetPhysicsInterpolation();
            _companion.GlobalPosition = at + new Vector3(0, 0.1f, 0.16f);
            _companion.ResetPhysicsInterpolation();
            _companion.Follow();
            for (var i = 0; i < 90; i++) { _player.SetControlInput(new Vector2(0, 1), sprint: true); await Frames(1); }
            var steps = new List<double>();
            var offsets = new List<Vector3>();
            var last = _player.GlobalPosition;
            for (var i = 0; i < 120; i++)
            {
                _player.SetControlInput(new Vector2(0, 1), sprint: true);
                await Frames(1);
                steps.Add((double)_player.GlobalPosition.X - last.X);
                offsets.Add(_companion.GlobalPosition - _player.GlobalPosition);
                last = _player.GlobalPosition;
            }
            _player.SetControlInput(Vector2.Zero);
            var mean = steps.Average();
            var spread = (steps.Max() - steps.Min()) / mean;
            var shake = 0.0f;
            for (var i = 1; i + 1 < offsets.Count; i++) shake = Mathf.Max(shake, (offsets[i + 1] - 2 * offsets[i] + offsets[i - 1]).Length());
            var spacing = Mathf.Pow(2, Mathf.Floor(Mathf.Log(km * 1000) / Mathf.Log(2)) - 23);
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{km:0.#} km: float spacing {spacing * 1000:0.000} mm; step {mean * 1000:0.000} mm a tick, spread {spread * 100:0.0} %; the Gubble's offset shakes {shake * 1000:0.000} mm a tick; swimming {_player.IsSwimming}, the Gubble over water {_companion.HoversOverWater}"));
            if (Mathf.IsEqualApprox(km * 1000, net)) smoothAtNet = spread < 0.15 && shake < 0.0005f && _player.IsSwimming && _companion.HoversOverWater;
        }
        _player.FarNetM = net;
        foreach (var line in lines) GD.Print("SMALL_AVATAR_FAR_OUT " + line);
        Report(smoothAtNet, $"at the far net ({net / 1000:0.#} km) a swimmer's steps and the Gubble beside them are still smooth (see SMALL_AVATAR_FAR_OUT)");
        _companion.Stop();
        Check(_player.TryTeleportTo(new Vector3(-2, 0.003f, 3.2f)) && _companion.TryTeleportTo(new Vector3(0.5f, 0.01f, 3.2f)), "both bodies back from the open sea");
    }

    // ---- Jitter: the spike's scenarios, measured on a separate probe body ----

    /// <summary>VerticalSpread and Drift are in body-scale metres; Roughness is the largest and RMS second
    /// difference of height between ticks (body-scale metres), which is zero for smooth motion on a plane.</summary>
    private sealed record JitterMetric(string Name, float FloorRatio, int Transitions, float VerticalSpread, float Drift,
        float RoughnessMax = 0, float RoughnessRms = 0, string Note = "");

    /// <summary>Probe body with every length, speed and acceleration multiplied by Scale (the ×10 import-scale alternative).</summary>
    private partial class ScaledProbe : SmallPlayerController
    {
        public float WorldScale = 1.0f;
        protected override WorldScaleProfile Profile => new(0.10 * WorldScale, 0.02 * WorldScale, 0.087 * WorldScale, 0.15 * WorldScale);

        public override void _Ready()
        {
            ReadKeyboard = false;
            // The jitter scenarios measure walking: pushing into the book's side must stay a push, not a climb.
            CanClimb = false;
            WalkSpeedMps *= WorldScale; RunSpeedMps *= WorldScale;
            GroundAccelerationMps2 *= WorldScale; AirAccelerationMps2 *= WorldScale;
            JumpApexM *= WorldScale; MaxJumpApexM *= WorldScale; StepHeightM *= WorldScale; FloorSnapM *= WorldScale; SafeMarginM *= WorldScale;
            TerminalFallMps *= WorldScale; MaxCreationSpeedMps *= WorldScale;
            base._Ready();
            ScaleWorldPhysicsForProbe(WorldScale);
        }
    }

    private async Task<List<JitterMetric>> RunJitterSuite(float s, Vector3 origin)
    {
        var area = new Node3D { Name = $"JitterArea_x{s:0}", Position = origin };
        AddChild(area);
        void Solid(Vector3 position, Vector3 size, float angle = 0)
        {
            var body = new StaticBody3D { Position = position * s, Rotation = new Vector3(0, 0, angle), CollisionLayer = 1, CollisionMask = 0 };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size * s } });
            area.AddChild(body);
        }
        Solid(new Vector3(0, -0.05f, 0), new Vector3(8, 0.1f, 8));
        Solid(new Vector3(0, 0.02f, -1.0f), new Vector3(0.22f, 0.04f, 0.15f));          // the book
        Solid(new Vector3(0, 0.003f, 1.0f), new Vector3(1.6f, 0.006f, 1.1f));           // the rug
        var angles = new[] { 10f, 20f, 30f, 40f };
        for (var i = 0; i < angles.Length; i++)
            Solid(new Vector3(2.0f, 0, -2.0f + i * 0.5f), new Vector3(1.2f, 0.05f, 0.3f), Mathf.DegToRad(angles[i]));
        var probe = new ScaledProbe { WorldScale = s, Name = "Probe", Position = new Vector3(-2, 0.003f, 2) * s };
        area.AddChild(probe);
        await Frames(10);
        var metrics = new List<JitterMetric>();
        Vector3 At(float x, float y, float z) => area.GlobalPosition + new Vector3(x, y, z) * s;

        async Task<JitterMetric> Stand(string name, Vector3 feet, int frames = 120)
        {
            if (!probe.TryTeleportTo(feet)) return new JitterMetric(name, 0, 0, 0, 0, Note: "no supported spawn");
            probe.SetControlInput(Vector2.Zero);
            await Frames(30);
            return await Sample(name, frames, null);
        }

        async Task<JitterMetric> Sample(string name, int frames, System.Action? each)
        {
            var start = probe.GlobalPosition;
            float low = float.MaxValue, high = float.MinValue;
            int floor = 0, transitions = 0;
            var was = probe.IsOnFloor();
            var heights = new List<float>();
            for (var i = 0; i < frames; i++)
            {
                each?.Invoke();
                await Frames(1);
                var on = probe.IsOnFloor();
                if (on) floor++;
                if (on != was) transitions++;
                was = on;
                low = Mathf.Min(low, probe.GlobalPosition.Y);
                high = Mathf.Max(high, probe.GlobalPosition.Y);
                heights.Add(probe.GlobalPosition.Y);
            }
            float roughMax = 0, roughSum = 0;
            for (var i = 1; i + 1 < heights.Count; i++)
            {
                var second = Mathf.Abs(heights[i + 1] - 2 * heights[i] + heights[i - 1]);
                roughMax = Mathf.Max(roughMax, second);
                roughSum += second * second;
            }
            var rms = heights.Count > 2 ? Mathf.Sqrt(roughSum / (heights.Count - 2)) : 0;
            var drift = new Vector2(probe.GlobalPosition.X - start.X, probe.GlobalPosition.Z - start.Z).Length();
            return new JitterMetric(name, floor / (float)frames, transitions, (high - low) / s, drift / s, roughMax / s, rms / s);
        }

        metrics.Add(await Stand("stand_floor", At(-1, 0.003f, 0)));
        metrics.Add(await Stand("stand_rug", At(0, 0.009f, 1.0f)));
        metrics.Add(await Stand("stand_book_top", At(0, 0.043f, -1.0f)));
        metrics.Add(await Stand("stand_book_edge_half_over", At(0.105f, 0.043f, -1.0f)));
        for (var i = 0; i < angles.Length; i++)
        {
            var surface = 0.025f / Mathf.Cos(Mathf.DegToRad(angles[i]));
            metrics.Add(await Stand($"stand_slope_{angles[i]:0}", At(2.0f, surface + 0.01f, -2.0f + i * 0.5f)));
        }
        // Walk across the rug (on and off its 6 mm edges).
        probe.TryTeleportTo(At(-1.2f, 0.003f, 1.0f));
        probe.Rotation = Vector3.Zero;
        await Frames(10);
        metrics.Add(await Sample("walk_across_rug", (int)(2.6f / probe.WalkSpeedMps * s * 60), () => probe.SetControlInput(Vector2.Right)));
        probe.SetControlInput(Vector2.Zero);
        // Push into the book's side for two seconds: horizontal vibration against a wall.
        probe.TryTeleportTo(At(-0.4f, 0.003f, -1.0f));
        await Frames(10);
        await Sample("push_into_book_settle", 60, () => probe.SetControlInput(Vector2.Right));
        var pushStart = probe.GlobalPosition;
        var push = await Sample("push_into_book_side", 120, () => probe.SetControlInput(Vector2.Right));
        metrics.Add(push with { Drift = new Vector2(probe.GlobalPosition.X - pushStart.X, probe.GlobalPosition.Z - pushStart.Z).Length() / s });
        probe.SetControlInput(Vector2.Zero);
        // Each ramp: walk from the floor onto it (crosses the crease), then up and down within the ramp.
        for (var i = 0; i < angles.Length; i++)
        {
            var z = -2.0f + i * 0.5f;
            var tan = Mathf.Tan(Mathf.DegToRad(angles[i]));
            var lift = 0.025f / Mathf.Cos(Mathf.DegToRad(angles[i]));
            probe.TryTeleportTo(At(1.45f, 0.003f, z));
            probe.Rotation = Vector3.Zero;
            await Frames(10);
            var onto = await Sample($"walk_onto_slope_{angles[i]:0}", (int)(0.55f / probe.WalkSpeedMps * s * 60), () => probe.SetControlInput(Vector2.Right));
            metrics.Add(onto with { Note = "includes the floor-to-ramp crease" });
            probe.SetControlInput(Vector2.Zero);
            // Start past the crease for every angle (the steepest ramp leaves the floor at x = 1.96).
            probe.TryTeleportTo(At(1.98f, (1.98f - 2.0f) * tan + lift + 0.005f, z));
            probe.Rotation = Vector3.Zero;
            await Frames(15);
            var up = await Sample($"walk_up_slope_{angles[i]:0}", (int)(0.35f / probe.WalkSpeedMps * s * 60), () => probe.SetControlInput(Vector2.Right));
            var climbed = (probe.GlobalPosition.Y - area.GlobalPosition.Y) / s;
            metrics.Add(up with { Note = $"top at {climbed * 100:0.0} cm" });
            probe.SetControlInput(Vector2.Zero);
            await Frames(10);
            metrics.Add(await Sample($"walk_down_slope_{angles[i]:0}", (int)(0.28f / probe.WalkSpeedMps * s * 60), () => probe.SetControlInput(Vector2.Left)));
            probe.SetControlInput(Vector2.Zero);
        }
        area.QueueFree();
        await Frames(2);
        return metrics;
    }

    private void CheckJitter(List<JitterMetric> metrics)
    {
        var byName = metrics.ToDictionary(m => m.Name);
        foreach (var name in new[] { "stand_floor", "stand_rug", "stand_book_top", "stand_slope_10", "stand_slope_20", "stand_slope_30", "stand_slope_40" })
        {
            var m = byName[name];
            Check(m.FloorRatio >= 0.999f && m.Transitions == 0 && m.VerticalSpread < 0.0002f && m.Drift < 0.0005f,
                $"no jitter standing still: {Describe(m)}");
        }
        var edge = byName["stand_book_edge_half_over"];
        Check(edge.Transitions <= 2 && (edge.Drift < 0.0005f || edge.FloorRatio > 0.8f), $"half over the book edge either holds or slides off once: {Describe(edge)}");
        var rug = byName["walk_across_rug"];
        Check(rug.FloorRatio >= 0.97f && rug.Transitions <= 6 && rug.RoughnessMax < 0.008f, $"walking over the rug edges stays grounded: {Describe(rug)}");
        Check(ProjectSettings.GetSetting("physics/3d/physics_engine").AsString() == "Jolt Physics", "the project runs Jolt Physics, the engine these thresholds were measured on");
        var push = byName["push_into_book_side"];
        Check(push.Drift < 0.0005f && push.VerticalSpread < 0.0005f, $"pushing into a wall does not vibrate: {Describe(push)}");
        foreach (var angle in new[] { "10", "20", "30", "40" })
        {
            var up = byName["walk_up_slope_" + angle];
            var down = byName["walk_down_slope_" + angle];
            // Jolt walks slopes smoothly; Godot Physics stuttered here (RMS 0.57 to 0.81 mm per tick squared on 20 and 30 degrees).
            Check(up.FloorRatio >= 0.95f && up.RoughnessRms < 0.0005f, $"walking up a {angle} degree slope stays grounded and smooth: {Describe(up)}");
            Check(down.FloorRatio >= 0.95f && down.RoughnessRms < 0.0005f, $"walking down a {angle} degree slope stays grounded and smooth: {Describe(down)}");
        }
    }

    private static string Describe(JitterMetric m) => string.Create(CultureInfo.InvariantCulture,
        $"{m.Name} floor={m.FloorRatio:0.000} transitions={m.Transitions} y_spread={m.VerticalSpread * 1000:0.000} mm drift={m.Drift * 1000:0.000} mm roughness max={m.RoughnessMax * 1000:0.000} rms={m.RoughnessRms * 1000:0.0000} mm {m.Note}");

    private static string JitterReport(List<JitterMetric> one, List<JitterMetric> ten)
    {
        var text = new StringBuilder("JITTER_SPIKE engine=" + ProjectSettings.GetSetting("physics/3d/physics_engine") +
            " ticks=" + Engine.PhysicsTicksPerSecond + " (lengths divided by the scale, in body-scale millimetres)\n");
        text.Append("scenario | x1 floor | x1 trans | x1 rough max mm | x1 rough rms mm | x1 y_spread mm | x1 drift mm | x10 floor | x10 trans | x10 rough max mm | x10 rough rms mm | x10 y_spread mm | x10 drift mm\n");
        for (var i = 0; i < one.Count; i++)
        {
            var a = one[i];
            var b = ten[i];
            text.Append(string.Create(CultureInfo.InvariantCulture,
                $"{a.Name} | {a.FloorRatio:0.000} | {a.Transitions} | {a.RoughnessMax * 1000:0.0000} | {a.RoughnessRms * 1000:0.0000} | {a.VerticalSpread * 1000:0.000} | {a.Drift * 1000:0.000} | " +
                $"{b.FloorRatio:0.000} | {b.Transitions} | {b.RoughnessMax * 1000:0.0000} | {b.RoughnessRms * 1000:0.0000} | {b.VerticalSpread * 1000:0.000} | {b.Drift * 1000:0.000} | {a.Note} / {b.Note}\n"));
        }
        return text.ToString();
    }

    // ---- helpers ----

    private static Godot.Collections.Dictionary Preset(string id, int revision) => Physics.Call("preset", id, revision).AsGodotDictionary();

    private bool PlayerVisualFits()
    {
        foreach (var child in _player.GetNode<Node3D>("OriginalPrototypeBody").GetChildren())
        {
            if (child is not MeshInstance3D mesh) continue;
            var bounds = mesh.Transform * mesh.Mesh.GetAabb();
            if (bounds.Position.Y < -0.001f || bounds.End.Y > _player.BodyHeightM + 0.001f ||
                Mathf.Max(Mathf.Abs(bounds.Position.X), Mathf.Abs(bounds.End.X)) > _player.BodyRadiusM + 0.001f ||
                Mathf.Max(Mathf.Abs(bounds.Position.Z), Mathf.Abs(bounds.End.Z)) > _player.BodyRadiusM + 0.001f) return false;
        }
        return true;
    }

    /// <summary>
    /// The characters round's hooks (Wave 0, docs/runs/RUN-2-CHARACTERS.md): the tick's BodyState, a drawn visual in the
    /// prototype's place, the tumble's control hold and the character id. None of them moves the capsule.
    /// </summary>
    private async Task TestBodyHooks()
    {
        var fixture = new Node3D { Name = "BodyHooksFixture", Position = new Vector3(-20, 0, 20) };
        AddChild(fixture);
        var floor = new StaticBody3D { Position = new Vector3(0, -0.05f, 0), CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(4, 0.1f, 4) } });
        fixture.AddChild(floor);
        var visual = new RecordingVisual();
        var body = new SmallPlayerController { Name = "HooksBody", ReadKeyboard = false, Position = new Vector3(0, 0.6f, 0) };
        body.SetBodyVisual(visual);
        fixture.AddChild(body);
        await Frames(2);
        var prototype = body.GetNode<Node3D>("OriginalPrototypeBody");
        var prototypeParts = prototype.GetChildren().OfType<Node3D>().Where(n => n != visual.Root).ToList();
        Check(visual.Root.GetParent() == prototype && prototypeParts.Count > 0 && prototypeParts.All(n => !n.Visible),
            "a visual set before the body is ready takes the prototype's place at _Ready; the prototype's parts stay, hidden");
        Check(visual.Mesh.Layers == GarageGame.Native.Look.EyeView.PlayerBodyLayer, "the drawn body is on the body's own layer, so its own eye leaves it out");
        var landing = default(BodyState);
        for (var i = 0; i < 180 && !landing.Landed; i++)
        {
            await Frames(1);
            if (visual.Last.Landed) landing = visual.Last;
        }
        var freeFall = Mathf.Sqrt(2 * body.GravityMps2 * 0.6f);
        Report(landing.Landed && Mathf.Abs(landing.LandingSpeedMps - freeFall) < 0.25f && !landing.LandedInDeepWater && landing.Grounded && landing.AirTimeS == 0,
            $"the tick's state reports a 0.6 m landing at {landing.LandingSpeedMps:0.00} m/s (free fall {freeFall:0.00})");
        await Frames(10);
        Check(visual.Ticks > 0 && visual.Last.Tick == body.LastBodyState.Tick && !body.LastBodyState.Landed && body.LastBodyState.LandingSpeedMps == 0,
            "the visual is ticked with the body's state every physics tick, and a landing is reported once");
        var startDistance = body.LastBodyState.DistanceM;
        var start = body.GlobalPosition;
        body.SetControlInput(new Vector2(0, 1));
        await Frames(60);
        body.SetControlInput(Vector2.Zero);
        await Frames(20);
        var walked = PlanarDistance(body.GlobalPosition, start);
        var counted = body.LastBodyState.DistanceM - startDistance;
        Report(walked > 0.1f && Mathf.Abs(counted - walked) < 0.01f, $"the distance a gait phases by follows the ground walked ({counted:0.000} m counted, {walked:0.000} m walked)");
        var beforeJump = body.LastBodyState.DistanceM;
        body.TryTeleportTo(fixture.GlobalPosition + new Vector3(1, 0.003f, 1));
        await Frames(5);
        Check(body.LastBodyState.DistanceM - beforeJump < 0.01f, "a teleport is not travel");

        var held = body.GlobalPosition;
        body.HoldControl(0.5f);
        body.SetControlInput(new Vector2(0, 1), jump: true);
        await Frames(15);
        Check(body.ControlHeld && PlanarDistance(body.GlobalPosition, held) < 0.002f && body.IsOnFloor() && body.LastBodyState.ControlHeld &&
            body.LastBodyState.HeldInput == new Vector2(0, 1), "held control ignores movement and jumps, and keeps what was asked for the get-up");
        await Frames(25);
        var resumed = body.GlobalPosition;
        await Frames(10);
        Check(!body.ControlHeld && PlanarDistance(body.GlobalPosition, resumed) > 0.01f && body.HeldInput == Vector2.Zero, "control returns when the hold runs out");
        body.HoldControl(3);
        await Frames(2);
        body.ReleaseControl();
        var freed = body.GlobalPosition;
        await Frames(10);
        Check(!body.ControlHeld && PlanarDistance(body.GlobalPosition, freed) > 0.01f, "ReleaseControl gives control back at once");
        body.SetControlInput(Vector2.Zero);
        body.HoldControl(float.NaN);
        body.HoldControl(-1);
        Check(!body.ControlHeld, "a hold that is not a positive number does nothing");
        body.HoldControl(60);
        Check(Mathf.IsEqualApprox(body.ControlHoldS, SmallPlayerController.MaxControlHoldS), "a hold never exceeds MaxControlHoldS");
        body.ReleaseControl();
        body.KnockoutRecoveryDelayS = float.NaN;
        var nanDelay = body.KnockoutRecoveryDelayS;
        body.KnockoutRecoveryDelayS = 99;
        Check(nanDelay == 0 && body.KnockoutRecoveryDelayS == SmallPlayerController.MaxControlHoldS, "the knockout delay is finite and bounded");
        body.KnockoutRecoveryDelayS = 0;

        var told = new List<string>();
        body.CharacterChanged = id => told.Add(id);
        Check(body.SetCharacterId("stickbear") && body.CharacterId == "stickbear" && body.SetCharacterId("stickbear") &&
            !body.SetCharacterId("Stickbear") && !body.SetCharacterId("../x") && !body.SetCharacterId("9lives") && !body.SetCharacterId(new string('a', 65)) &&
            body.CharacterId == "stickbear" && body.SetCharacterId("") && body.CharacterId == "" && told.SequenceEqual(new[] { "stickbear", "" }),
            "the character id is a lowercase token or empty, and each change is told once");

        body.SetBodyVisual(null);
        Check(visual.Root.GetParent() == null && prototypeParts.All(n => n.Visible), "taking the visual off brings the prototype back");
        var ticks = visual.Ticks;
        await Frames(3);
        Check(visual.Ticks == ticks, "a visual taken off is no longer ticked");
        visual.Root.QueueFree();
        fixture.QueueFree();
        await Frames(2);
    }

    /// <summary>A stand-in drawn body: one small sphere, and the states it was given.</summary>
    private sealed class RecordingVisual : IBodyVisual
    {
        public Node3D Root { get; } = new() { Name = "RecordingVisual" };
        public MeshInstance3D Mesh { get; } = new() { Name = "RecordingMesh", Mesh = new SphereMesh { Radius = 0.02f, Height = 0.04f } };
        public BodyState Last { get; private set; }
        public int Ticks { get; private set; }
        public RecordingVisual() => Root.AddChild(Mesh);
        public void BodyTicked(in BodyState state, float delta)
        {
            Last = state;
            Ticks++;
        }
    }

    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    /// <summary>A check whose label carries a measured value worth keeping in the run's evidence.</summary>
    private void Report(bool condition, string label)
    {
        GD.Print("SMALL_AVATAR_MEASURED " + label);
        Check(condition, label);
    }

    private void Check(bool condition, string label)
    {
        _checks++;
        if (condition) return;
        _failures++;
        GD.PushError("Small avatar: " + label);
    }

    private static float PlanarDistance(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    private void BuildFloorAndObstacles()
    {
        Box(new Vector3(0, -0.05f, 0), new Vector3(10, 0.1f, 12));
        Box(new Vector3(0, 0.0075f, 0), new Vector3(0.7f, 0.015f, 0.4f));            // 1.5 cm curb
        Box(new Vector3(0, 0.015f, -0.6f), new Vector3(0.22f, 0.03f, 0.3f));          // 3 cm barrier
        Box(new Vector3(0, 0.02f, -1.4f), new Vector3(0.22f, 0.04f, 0.3f));           // 4 cm book
        Box(new Vector3(0, 0.003f, -2.2f), new Vector3(1.0f, 0.006f, 0.5f));          // 6 mm rug
        Box(new Vector3(0, 0.14f, 1.2f), new Vector3(0.8f, 0.04f, 0.7f));             // deck, 12 cm clearance
        Box(new Vector3(0, 0.09f, -2.8f), new Vector3(0.8f, 0.02f, 0.5f));            // roof, 8 cm clearance
        Box(new Vector3(12, 0, 0), new Vector3(8, 0.1f, 8), Mathf.DegToRad(30));
        Box(new Vector3(0, 0.12f, -4.5f), new Vector3(3, 0.02f, 0.7f));               // companion's 11 cm passage: no room for a 2 cm step
    }

    private void Box(Vector3 position, Vector3 size, float slope = 0)
    {
        var body = new StaticBody3D { Position = position, Rotation = new Vector3(0, 0, slope), CollisionLayer = 1, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size } });
        AddChild(body);
    }
}
