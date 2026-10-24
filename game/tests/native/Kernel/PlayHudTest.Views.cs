using Godot;
using System;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// The two views (the founder, 13 October): first person (F1) and third person over the shoulder (F2, the default). The diorama
/// (F3) and isometric (F4) views are shelved, their keys, and [ and ] with them, unbound; ViewMode 2 and 3 stay in code for the look
/// harness only. F2 zooms with the wheel from today's arm (exactly the shoulder view) out to ShoulderFarM; past OrbitFromM a drag
/// orbits the camera round the player instead of turning the body, and movement follows the camera's heading; the focus stays the
/// player's body. Everything is in the real room with the real look and HUD, headless like the rest of the suite.
/// </summary>
public partial class PlayHudTest
{
    private static InputEventMouseButton WheelEvent(MouseButton which) => new() { ButtonIndex = which, Pressed = true };

    private Camera3D ViewCamera => _hud.GetViewport().GetCamera3D()!;

    /// <summary>The heading a camera looks along, in the HUD's terms (radians about +Y; 0 looks along -Z).</summary>
    private static float HeadingOf(Camera3D camera)
    {
        var forward = -camera.GlobalBasis.Z;
        return Mathf.Atan2(-forward.X, -forward.Z);
    }

    /// <summary>How far down a camera looks, in degrees.</summary>
    private static float LooksDownDeg(Camera3D camera) => Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(camera.GlobalBasis.Z.Y, -1f, 1f)));

    private Vector3 PlayerMiddle() => _world.Player.GlobalPosition + Vector3.Up * (_world.Player.BodyHeightM * 0.5f);

    /// <summary>The player on a free spot of the room, facing a heading with the eye tilted, standing by (the Gubble waits, the keyboard is the test's).</summary>
    private async Task StandForViews(Vector3 at, float yaw, float eyePitch)
    {
        var player = _world.Player;
        CommandHost.Of(_world)!.PlayerGoal("stay");
        player.ReadKeyboard = false;
        player.SetControlInput(Vector2.Zero);
        Check(player.TryTeleportTo(at), $"the player can stand at {at} for the view tests");
        player.SetView(yaw, eyePitch);
        _hud.SetViewMode(1);
        _hud.ZoomShoulder(1000);
        await Frames(10);
    }

    /// <summary>A free spot clear of the room's furniture, with 0.9 m or more to every wall and the ceiling above 2 m.</summary>
    private static readonly Vector3 FreeSpot = new(0.2f, 0.003f, -0.5f);

    /// <summary>The wheel zooms by this many clicks: positive in, negative out (the HUD's own step).</summary>
    private static float ClicksTo(float from, float to) => Mathf.Log(to / from) / Mathf.Log(RoomHud.ShoulderZoomStep);

    // ---- F3, F4, [ and ] do nothing in play ----

    private async Task TestShelvedViewKeys()
    {
        var player = _world.Player;
        await StandForViews(FreeSpot, 0.4f, 0f);
        Check(_hud.ViewMode == 1 && _hud.ShoulderDistanceM == _hud.ShoulderNearM && Mathf.IsEqualApprox(_hud.ShoulderNearM, player.BodyHeightM * 3.2f) && !_hud.Orbiting,
            $"the default view is F2 at today's arm, {player.BodyHeightM * 3.2f * 100:0} cm for the {player.BodyHeightM * 100:0} cm body");

        var shelved = new[] { Key.F3, Key.F4, Key.Bracketleft, Key.Bracketright };
        foreach (var mode in new[] { 1, 0 })
        {
            _hud.SetViewMode(mode);
            await Frames(2);
            var (camera, diorama, iso) = (ViewCamera, _hud.DioramaYaw, _hud.IsoYaw);
            foreach (var key in shelved)
            {
                _hud._UnhandledInput(Press(key));
                player._UnhandledInput(Press(key));
                _hud._UnhandledInput(Press(key, shift: true));
                Check(_hud.ViewMode == mode && !_hud.Orbiting && player.MovementFrameYaw == null && ViewCamera == camera && camera.Current &&
                      Mathf.IsEqualApprox(diorama, _hud.DioramaYaw) && Mathf.IsEqualApprox(iso, _hud.IsoYaw),
                    $"{key} does nothing in {(mode == 1 ? "F2" : "F1")}: the view, its camera, the movement frame and the shelved views' headings are as they were");
            }
        }
        _hud.SetViewMode(1);

        // The keys are free: nothing in the map is on them, and the actions that were are gone from it.
        var held = PlayerControls.MapActions().Where(action => InputMap.ActionGetEvents(action).Any(e =>
            e is InputEventKey { PhysicalKeycode: Key.F3 or Key.F4 or Key.Bracketleft or Key.Bracketright } or InputEventKey { Keycode: Key.F3 or Key.F4 or Key.Bracketleft or Key.Bracketright })).ToArray();
        Check(held.Length == 0, "no action is on F3, F4, [ or ]: " + string.Join(", ", held));
        Check(new[] { "view_diorama", "view_iso", "iso_turn_left", "iso_turn_right" }.All(action => !InputMap.HasAction(action)), "the four actions are gone from the input map");

        // Nothing on screen names them either: the key help, the buttons and every label of the HUD.
        var words = string.Join("\n", HelpLines()) + "\n" + string.Join("\n", _hud.FindChildren("*", "Label", true, false).OfType<Label>().Select(l => l.Text)) +
                    "\n" + string.Join("\n", _hud.FindChildren("*", "Button", true, false).OfType<Button>().Select(b => b.Text + " " + b.TooltipText));
        var named = new[] { "F3", "F4", "diorama", "isometric", "[/]" }.Where(word => words.Contains(word, StringComparison.OrdinalIgnoreCase)).ToArray();
        Check(named.Length == 0, "the help and the HUD say nothing of the diorama, the isometric view or their keys: " + string.Join(", ", named));

        // The look harness still sets them directly (hud_view 2 and 3): code only.
        _hud.SetViewMode(2);
        var diorama2 = _hud.DioramaCamera.Current && player.MovementFrameYaw == _hud.DioramaYaw && !_hud.Orbiting;
        _hud.SetViewMode(3);
        var iso3 = _hud.DioramaCamera.Current && player.MovementFrameYaw == _hud.IsoYaw && Mathf.IsEqualApprox(_hud.DioramaCamera.Fov, RoomHud.IsoFovDeg);
        _hud.SetViewMode(1);
        Check(diorama2 && iso3 && ViewCamera != _hud.DioramaCamera && player.MovementFrameYaw == null, "ViewMode 2 and 3 still work when set directly, as the look harness does, and F2 takes the body back");
    }

    // ---- the wheel's range and its clamping ----

    private async Task TestShoulderRange()
    {
        var player = _world.Player;
        await StandForViews(FreeSpot, 0.4f, 0f);
        var near = _hud.ShoulderNearM;
        Check(RoomHud.ShoulderFarM == 8f && near < RoomHud.OrbitFromM && RoomHud.OrbitFromM < RoomHud.ShoulderFarM &&
              Mathf.IsEqualApprox(RoomHud.OrbitFromM, near * 2.5f) && RoomHud.ShoulderFarM / player.BodyHeightM >= 79.9f,
            $"the range is today's arm ({near:0.00} m) to {RoomHud.ShoulderFarM:0} m ({RoomHud.ShoulderFarM / player.BodyHeightM:0} body heights), orbiting from {RoomHud.OrbitFromM:0.0} m ({RoomHud.OrbitFromM / player.BodyHeightM:0} body heights)");

        _hud._UnhandledInput(WheelEvent(MouseButton.WheelUp));
        Check(_hud.ShoulderDistanceM == near, "at the near end the wheel in zooms nothing: the arm is today's, exactly");
        _hud._UnhandledInput(WheelEvent(MouseButton.WheelDown));
        Check(Mathf.IsEqualApprox(_hud.ShoulderDistanceM, near / RoomHud.ShoulderZoomStep), $"one click out lengthens the arm by {1 / RoomHud.ShoulderZoomStep - 1:P0} ({_hud.ShoulderDistanceM:0.000} m)");
        _hud._UnhandledInput(WheelEvent(MouseButton.WheelUp));
        Check(Mathf.IsEqualApprox(_hud.ShoulderDistanceM, near), "and one click in puts it back");

        var clicks = 0;
        var orbitedAt = 0;
        for (; _hud.ShoulderDistanceM < RoomHud.ShoulderFarM && clicks < 100; clicks++)
        {
            _hud._UnhandledInput(WheelEvent(MouseButton.WheelDown));
            if (_hud.Orbiting && orbitedAt == 0) orbitedAt = clicks + 1;
        }
        Check(clicks is >= 15 and <= 25 && _hud.ShoulderDistanceM == RoomHud.ShoulderFarM && _hud.Orbiting, $"{clicks} clicks out reach the far end, {_hud.ShoulderDistanceM:0.0} m exactly, and it is clamped there");
        Check(orbitedAt is >= 4 and <= 8, $"the orbit begins at click {orbitedAt}, past {RoomHud.OrbitFromM:0.0} m");
        _hud._UnhandledInput(WheelEvent(MouseButton.WheelDown));
        _hud.ZoomShoulder(-1000);
        Check(_hud.ShoulderDistanceM == RoomHud.ShoulderFarM, "more clicks out change nothing");
        _hud.ZoomShoulder(1000);
        Check(_hud.ShoulderDistanceM == near && !_hud.Orbiting && player.MovementFrameYaw == null, "and the same the other way: back at today's arm exactly, the body's own frame");
        _hud.ZoomShoulder(float.NaN);
        _hud.ZoomShoulder(float.PositiveInfinity);
        Check(_hud.ShoulderDistanceM == near, "a zoom that is not a number is ignored");

        // The wheel is F2's: not in F1, and not with a panel open.
        _hud.SetViewMode(0);
        _hud._UnhandledInput(WheelEvent(MouseButton.WheelDown));
        Check(_hud.ShoulderDistanceM == near, "the wheel zooms nothing in F1");
        _hud.SetViewMode(1);
        _hud._UnhandledInput(Press(Key.C));
        _hud._UnhandledInput(WheelEvent(MouseButton.WheelDown));
        var shut = _hud.ShoulderDistanceM == near;
        _hud._UnhandledInput(Press(Key.C));
        Check(shut && !_hud.SettingsOpen, "nor with the appearance panel open");

        // Walls and the ground hold the camera in, as the shoulder arm always was held: out at the far end in this small room, the lens
        // stands inside it with nothing between it and the player.
        foreach (var (pitch, label) in new[] { (0f, "level"), (-0.9f, "looking down"), (0.6f, "looking up") })
        {
            player.SetView(0.4f, pitch);
            _hud.ZoomShoulder(-1000);
            await Frames(6);
            var camera = ViewCamera;
            var middle = PlayerMiddle();
            var reach = camera.GlobalPosition.DistanceTo(middle);
            var cast = PhysicsRayQueryParameters3D.Create(middle, camera.GlobalPosition, 1, new Godot.Collections.Array<Rid> { player.GetRid() });
            var clear = _hud.GetViewport().World3D.DirectSpaceState.IntersectRay(cast).Count == 0;
            Check(reach < RoomHud.ShoulderFarM - 1f && reach > 0.02f && clear && _world.Room.Bounds.Grow(0.05f).HasPoint(camera.GlobalPosition),
                $"the arm {label} is held by the room's walls and floor: the lens is {reach:0.00} m from the player of the {RoomHud.ShoulderFarM:0} m asked, inside the room, nothing between");
            _hud.ZoomShoulder(1000);
        }
        player.SetView(0.4f, 0f);
    }

    // ---- the orbit past the threshold, the body turn below it ----

    private async Task TestShoulderOrbit()
    {
        var player = _world.Player;
        await StandForViews(FreeSpot, 0.4f, -0.3f);
        _hud.DragHeldForTests = () => true;
        try
        {
            // Below the threshold: a drag turns the body and tilts the eye, as ever; nothing orbits.
            _hud.ZoomShoulder(-3);
            await Frames(3);
            Check(!_hud.Orbiting && _hud.ShoulderDistanceM < RoomHud.OrbitFromM && ViewCamera.Name == "FollowCamera" && player.MovementFrameYaw == null,
                $"{_hud.ShoulderDistanceM:0.00} m out (3 clicks) is still the shoulder camera");
            var (bodyYaw, orbitYaw, pitch) = (player.GlobalRotation.Y, _hud.OrbitYaw, player.EyeCamera.Rotation.X);
            _hud._UnhandledInput(LeftButton(true));
            _hud._UnhandledInput(Motion(new Vector2(40, -20)));
            _hud._UnhandledInput(LeftButton(false));
            Check(Mathf.Abs(Mathf.AngleDifference(bodyYaw, player.GlobalRotation.Y) + 40 * SmallPlayerController.LookRadiansPerPixel) < 1e-4f &&
                  Mathf.Abs(player.EyeCamera.Rotation.X - (pitch + 20 * SmallPlayerController.LookRadiansPerPixel)) < 1e-4f && Mathf.IsEqualApprox(orbitYaw, _hud.OrbitYaw) && player.MovementFrameYaw == null,
                "below the threshold the drag turns the body and tilts the eye, 0.0025 rad a pixel, and orbits nothing");

            // Past it: the camera is the orbit's, starting behind the player at the pitch of the shoulder camera it took over from.
            bodyYaw = player.GlobalRotation.Y;
            var armPitchDown = -Mathf.RadToDeg(Mathf.Clamp(player.EyeCamera.Rotation.X - 0.18f, -1.1f, 0.8f));
            _hud.ZoomShoulder(-3);
            await Frames(4);
            var camera = ViewCamera;
            Check(_hud.Orbiting && camera == _hud.DioramaCamera && camera.Current && Mathf.IsEqualApprox(camera.Fov, RoomHud.ShoulderFovDeg) &&
                  Mathf.Abs(Mathf.AngleDifference(_hud.OrbitYaw, bodyYaw)) < 1e-3f && Mathf.Abs(_hud.OrbitPitchDeg - armPitchDown) < 0.01f,
                $"past {RoomHud.OrbitFromM:0.0} m ({_hud.ShoulderDistanceM:0.00} m) the orbit camera takes over, with the shoulder lens, behind the player at the shoulder camera's pitch ({_hud.OrbitPitchDeg:0.0} deg)");
            Check(player.MovementFrameYaw is { } frame && Mathf.IsEqualApprox(frame, _hud.OrbitYaw), "and movement follows its heading");

            // The orbit: a sideways drag swings the camera round the player, the body stays, the frame follows.
            var wanted = 1.0f;
            _hud.ZoomShoulder(ClicksTo(_hud.ShoulderDistanceM, wanted));
            await Frames(4);
            var before = HeadingOf(camera);
            var yawBefore = _hud.OrbitYaw;
            bodyYaw = player.GlobalRotation.Y;
            _hud._UnhandledInput(LeftButton(true));
            _hud._UnhandledInput(Motion(new Vector2(60, 0)));
            var dragging = _hud.Drag.Dragging;
            _hud._UnhandledInput(LeftButton(false));
            await Frames(4);
            Check(dragging && Mathf.Abs(Mathf.AngleDifference(yawBefore, _hud.OrbitYaw) + 60 * RoomHud.MouseRadiansPerPixel) < 1e-4f && Mathf.IsEqualApprox(bodyYaw, player.GlobalRotation.Y) &&
                  Mathf.Abs(Mathf.AngleDifference(before, HeadingOf(camera)) + 60 * RoomHud.MouseRadiansPerPixel) < 0.01f && player.MovementFrameYaw == _hud.OrbitYaw,
                "past the threshold the drag orbits the camera round the player, 0.0025 rad a pixel; the body does not turn and the frame follows");
            var middle = PlayerMiddle();
            var reach = camera.GlobalPosition.DistanceTo(middle);
            Check((middle - camera.GlobalPosition).Normalized().Dot(-camera.GlobalBasis.Z) > 0.999f && Mathf.Abs(reach - wanted) < 0.03f && Mathf.Abs(LooksDownDeg(camera) - _hud.OrbitPitchDeg) < 1f,
                $"the orbit camera is centred on the player's middle, {reach:0.000} m of the {wanted:0.0} m away, looking down {LooksDownDeg(camera):0.0} of {_hud.OrbitPitchDeg:0.0} deg");

            // The tilt: up and down, within the shoulder camera's own limits.
            _hud.OrbitShoulder(new Vector2(0, 100000));
            await Frames(3);
            var steepest = _hud.OrbitPitchDeg;
            var steepestSeen = LooksDownDeg(camera);
            _hud.OrbitShoulder(new Vector2(0, -100000));
            await Frames(6);
            var flattest = _hud.OrbitPitchDeg;
            Check(Mathf.IsEqualApprox(steepest, RoomHud.OrbitMaxPitchDeg) && Mathf.IsEqualApprox(flattest, RoomHud.OrbitMinPitchDeg) && Mathf.Abs(steepestSeen - steepest) < 1f &&
                  RoomHud.OrbitMaxPitchDeg > 62f && RoomHud.OrbitMaxPitchDeg < 64f && RoomHud.OrbitMinPitchDeg > 0f,
                $"the tilt stays between {RoomHud.OrbitMinPitchDeg:0.0} and {RoomHud.OrbitMaxPitchDeg:0.0} degrees down, the top the shoulder camera's own limit (got {flattest:0.0} and {steepest:0.0})");
            var flatSeen = LooksDownDeg(camera);
            Check(Mathf.Abs(flatSeen - flattest) < 1f && camera.GlobalPosition.Y > 0.02f && camera.GlobalPosition.DistanceTo(PlayerMiddle()) > 0.3f && _world.Room.Bounds.Grow(0.05f).HasPoint(camera.GlobalPosition),
                $"at the flattest the camera is still clear of the floor and of the body ({camera.GlobalPosition.Y * 100:0.0} cm up, {camera.GlobalPosition.DistanceTo(PlayerMiddle()):0.00} m away, looking down {flatSeen:0.0} deg)");

            // Nothing orbits unless zoomed out: the call is refused at the shoulder.
            _hud.ZoomShoulder(1000);
            var yaw = _hud.OrbitYaw;
            _hud.OrbitShoulder(new Vector2(80, 80));
            Check(Mathf.IsEqualApprox(yaw, _hud.OrbitYaw) && !_hud.Orbiting, "at the shoulder an orbit call does nothing");
        }
        finally
        {
            _hud.DragHeldForTests = null;
            _hud.ZoomShoulder(1000);
            _hud.SetViewMode(1);
        }
    }

    /// <summary>The camera's pose before and after the arm crosses the threshold, one small step apart: no jump in either direction.</summary>
    private async Task TestShoulderHandover()
    {
        var player = _world.Player;
        await StandForViews(FreeSpot, 0.4f, -0.3f);
        var threshold = RoomHud.OrbitFromM;
        _hud.ZoomShoulder(ClicksTo(_hud.ShoulderDistanceM, threshold - 0.002f));
        await Frames(6);
        var cameraIn = ViewCamera;
        var shoulderPose = cameraIn.GlobalTransform;
        Check(!_hud.Orbiting && cameraIn.Name == "FollowCamera", $"just inside the threshold ({_hud.ShoulderDistanceM:0.000} m) the shoulder camera is current");
        _hud.ZoomShoulder(ClicksTo(_hud.ShoulderDistanceM, threshold + 0.002f));
        await Frames(6);
        var orbitPose = ViewCamera.GlobalTransform;
        Check(_hud.Orbiting && ViewCamera == _hud.DioramaCamera, $"just outside ({_hud.ShoulderDistanceM:0.000} m) the orbit camera is");
        var jump = shoulderPose.Origin.DistanceTo(orbitPose.Origin);
        var turn = Mathf.RadToDeg(shoulderPose.Basis.GetRotationQuaternion().AngleTo(orbitPose.Basis.GetRotationQuaternion()));
        Check(jump < 0.004f + 1e-3f && turn < 0.5f, $"the swap out is not a jump: the lens moved {jump * 1000:0.0} mm (the 4 mm of arm between the two steps) and turned {turn:0.00} degrees");

        _hud.ZoomShoulder(ClicksTo(_hud.ShoulderDistanceM, threshold - 0.002f));
        await Frames(6);
        var backPose = ViewCamera.GlobalTransform;
        Check(!_hud.Orbiting && ViewCamera.Name == "FollowCamera" && backPose.Origin.DistanceTo(shoulderPose.Origin) < 1e-3f &&
              Mathf.RadToDeg(backPose.Basis.GetRotationQuaternion().AngleTo(shoulderPose.Basis.GetRotationQuaternion())) < 0.1f,
            "and back in: the shoulder camera is where it was");

        // Orbited round to the player's side, then back in: the body takes the camera's heading and the view does not swing.
        _hud.DragHeldForTests = () => true;
        try
        {
            _hud.ZoomShoulder(ClicksTo(_hud.ShoulderDistanceM, 1.2f));
            await Frames(4);
            _hud._UnhandledInput(LeftButton(true));
            _hud._UnhandledInput(Motion(new Vector2(-300, 40)));
            _hud._UnhandledInput(LeftButton(false));
            await Frames(8);
            var camera = ViewCamera;
            var (heading, pitch, orbitYaw) = (HeadingOf(camera), LooksDownDeg(camera), _hud.OrbitYaw);
            Check(Mathf.Abs(Mathf.AngleDifference(player.GlobalRotation.Y, orbitYaw)) > 0.5f, $"the camera has been orbited well away from the body's heading ({Mathf.RadToDeg(Mathf.AngleDifference(player.GlobalRotation.Y, orbitYaw)):0} deg)");
            var first = camera.GlobalTransform;
            _hud.ZoomShoulder(ClicksTo(_hud.ShoulderDistanceM, threshold - 0.002f));
            await Frames(8);
            var shoulder = ViewCamera;
            Check(!_hud.Orbiting && shoulder.Name == "FollowCamera" && player.MovementFrameYaw == null &&
                  Mathf.Abs(Mathf.AngleDifference(player.GlobalRotation.Y, orbitYaw)) < 1e-3f && Mathf.Abs(Mathf.AngleDifference(HeadingOf(shoulder), heading)) < 0.005f &&
                  Mathf.Abs(LooksDownDeg(shoulder) - pitch) < 0.5f,
                $"zoomed back in, the body has turned to the camera's heading and the shoulder camera looks the same way, down {LooksDownDeg(shoulder):0.0} of {pitch:0.0} deg");
            Check(first.Origin.DistanceTo(shoulder.GlobalPosition) < 1.2f + 0.01f, "the camera came in along its line of sight");
        }
        finally
        {
            _hud.DragHeldForTests = null;
            _hud.ZoomShoulder(1000);
        }
    }

    // ---- the movement frame ----

    private async Task TestShoulderMovementFrame()
    {
        await TestShoulderHandover();
        var player = _world.Player;
        await StandForViews(FreeSpot, 0.4f, -0.3f);
        _hud.DragHeldForTests = () => true;
        try
        {
            // Zoomed out and turned: forward is away from the camera, right is its right, and the body turns to face where it goes.
            _hud.ZoomShoulder(ClicksTo(_hud.ShoulderDistanceM, 1.0f));
            await Frames(3);
            _hud._UnhandledInput(LeftButton(true));
            _hud._UnhandledInput(Motion(new Vector2(-300, 0)));
            _hud._UnhandledInput(LeftButton(false));
            await Frames(6);
            var camera = ViewCamera;
            var forward = new Vector3(-camera.GlobalBasis.Z.X, 0, -camera.GlobalBasis.Z.Z).Normalized();
            var right = new Vector3(camera.GlobalBasis.X.X, 0, camera.GlobalBasis.X.Z).Normalized();
            var bodyForward = -player.GlobalBasis.Z;
            Check(player.MovementFrameYaw is { } frame && Mathf.IsEqualApprox(frame, _hud.OrbitYaw) && Mathf.Abs(forward.Dot(bodyForward)) < 0.9f,
                "orbited well away from where the body faces, movement follows the camera's heading");
            var start = player.GlobalPosition;
            player.SetControlInput(new Vector2(0, 1));
            await Frames(40);
            player.SetControlInput(Vector2.Zero);
            var moved = player.GlobalPosition - start;
            moved.Y = 0;
            Check(moved.Length() > 0.1f && moved.Normalized().Dot(forward) > 0.98f && (-player.GlobalBasis.Z).Dot(forward) > 0.95f,
                $"forward walks away from the camera and the body turns to face its motion (moved {moved.Length() * 100:0.0} cm, {moved.Normalized().Dot(forward):0.000} along the view)");
            await Frames(10);
            start = player.GlobalPosition;
            player.SetControlInput(new Vector2(1, 0));
            await Frames(40);
            player.SetControlInput(Vector2.Zero);
            moved = player.GlobalPosition - start;
            moved.Y = 0;
            Check(moved.Length() > 0.1f && moved.Normalized().Dot(right) > 0.98f, $"right walks to the camera's right ({moved.Normalized().Dot(right):0.000} along it)");

            // Back to the shoulder: the body's own frame again, and forward is where the body faces, which is where the camera looked.
            _hud.ZoomShoulder(1000);
            await Frames(6);
            camera = ViewCamera;
            Check(!_hud.Orbiting && player.MovementFrameYaw == null && camera.Name == "FollowCamera", "back at the shoulder movement is the body's own again");
            forward = new Vector3(-camera.GlobalBasis.Z.X, 0, -camera.GlobalBasis.Z.Z).Normalized();
            start = player.GlobalPosition;
            player.SetControlInput(new Vector2(0, 1));
            await Frames(40);
            player.SetControlInput(Vector2.Zero);
            moved = player.GlobalPosition - start;
            moved.Y = 0;
            Check(moved.Length() > 0.1f && moved.Normalized().Dot(forward) > 0.98f && moved.Normalized().Dot(-player.GlobalBasis.Z) > 0.98f,
                $"forward walks the way the body faces, which is the way the shoulder camera looks ({moved.Normalized().Dot(forward):0.000})");

            // Between the shoulder and the threshold the frame is the body's: a drag turns the body and forward follows it.
            _hud.ZoomShoulder(-4);
            await Frames(3);
            Check(!_hud.Orbiting && _hud.ShoulderDistanceM > _hud.ShoulderNearM * 1.4f && player.MovementFrameYaw == null, $"{_hud.ShoulderDistanceM:0.00} m out the frame is still the body's");
        }
        finally
        {
            player.SetControlInput(Vector2.Zero);
            _hud.DragHeldForTests = null;
            _hud.ZoomShoulder(1000);
        }
    }

    // ---- the near end is unchanged ----

    private async Task TestShoulderNearEndUnchanged()
    {
        var player = _world.Player;
        var (h, r) = (player.BodyHeightM, player.BodyRadiusM);
        var armNode = player.GetNode<SpringArm3D>("FollowCameraArm");
        var follow = armNode.GetNode<Camera3D>("FollowCamera");
        foreach (var (yaw, eyePitch) in new[] { (0.4f, 0f), (-1.2f, -0.5f), (2.5f, 0.15f), (0.9f, 1.3f), (0f, -1.3f) })
        {
            await StandForViews(FreeSpot, yaw, eyePitch);
            await Frames(4);
            // Today's shoulder camera, restated: the arm off the body's right shoulder (2.2 radii out, 0.8 body heights up), swung by the
            // eye's pitch less 0.18 rad (within -1.1 and 0.8), 3.2 body heights long, a 68 degree lens.
            var armPitch = Mathf.Clamp(eyePitch - 0.18f, -1.1f, 0.8f);
            var expectedAt = player.GlobalTransform * new Vector3(r * 2.2f, h * 0.8f, 0);
            var expectedBasis = player.GlobalBasis * new Basis(Vector3.Right, armPitch);
            // The arm is as long as the room lets it be (looking well up, today's arm meets the floor): the camera hangs at its end.
            var length = follow.Position.Z;
            Check(armNode.Position == new Vector3(r * 2.2f, h * 0.8f, 0) && Mathf.IsEqualApprox(armNode.Rotation.X, armPitch) && armNode.Rotation.Y == 0 && armNode.Rotation.Z == 0 &&
                  Mathf.IsEqualApprox(armNode.SpringLength, h * 3.2f) && Mathf.IsEqualApprox(follow.Fov, 68f) && Mathf.IsEqualApprox(follow.Near, 0.005f),
                $"at the near end the arm is exactly today's (yaw {yaw:0.0}, eye pitch {eyePitch:0.0}): the shoulder offset, the pitch {armPitch:0.000}, {armNode.SpringLength * 100:0} cm long, the 68 degree lens");
            var free = Mathf.IsEqualApprox(length, h * 3.2f, 1e-3f);
            Check((free || eyePitch > 0.3f) && ViewCamera == follow && follow.Current && player.MovementFrameYaw == null && !_hud.Orbiting,
                $"the shoulder camera is the current camera, nothing holds the arm but the floor when looking well up ({length * 100:0.0} cm of {h * 320:0}), and the body's own movement frame is in force");
            Check(follow.GlobalPosition.DistanceTo(expectedAt + expectedBasis.Z * length) < 1e-4f && (follow.GlobalBasis.Z - expectedBasis.Z).Length() < 1e-4f && (follow.GlobalBasis.Y - expectedBasis.Y).Length() < 1e-4f,
                $"and the camera stands where it always did: {follow.GlobalPosition.DistanceTo(expectedAt + expectedBasis.Z * length) * 1000:0.000} mm from the old geometry");
            var pose = follow.GlobalTransform;
            _hud.ZoomShoulder(-4);
            await Frames(4);
            _hud.ZoomShoulder(1000);
            await Frames(4);
            Check(follow.GlobalTransform.Origin.DistanceTo(pose.Origin) < 1e-5f && (follow.GlobalBasis.Z - pose.Basis.Z).Length() < 1e-5f && armNode.Position == new Vector3(r * 2.2f, h * 0.8f, 0),
                "zoomed out a little and back, the camera and the arm are exactly as they were");
        }
        player.SetView(0.4f, 0f);
    }

    // ---- the focus, the observe switch, and the Gubble's eyes ----

    private async Task TestShoulderFocusAndSwitches()
    {
        var player = _world.Player;
        var look = _world.Look;
        await StandForViews(FreeSpot, 0.4f, -0.3f);
        _hud.ZoomShoulder(ClicksTo(_hud.ShoulderDistanceM, 3.0f));
        await Frames(6);
        var camera = ViewCamera;
        Check(_hud.Orbiting && camera == _hud.DioramaCamera, $"zoomed out to {_hud.ShoulderDistanceM:0.0} m the orbit camera is current");

        // The depth-of-field focus is the player's body, wherever the camera is; its blur is the shoulder view's.
        var bodyFocus = player.GlobalPosition + Vector3.Up * (look.Preset.Tuning.Dof.BodyFocusHeightFraction * player.BodyHeightM);
        Check(look.FocusPointFor(camera).DistanceTo(bodyFocus) < 1e-4f && look.ViewFor(camera) == LookView.Shoulder,
            $"the focus is the player's body from the orbit ({look.FocusPointFor(camera).DistanceTo(bodyFocus) * 1000:0.000} mm off it) and the view blurs as the shoulder view");
        _hud.OrbitShoulder(new Vector2(150, 40));
        await Frames(4);
        Check(look.FocusPointFor(camera).DistanceTo(bodyFocus) < 1e-4f, "and still after the camera is orbited round");

        // The observe switch stays: O in the zoomed-out view, focus on the drawn body's middle.
        var observeBefore = look.Observe;
        _hud._UnhandledInput(Press(Key.O));
        var observeOn = look.Observe;
        var middle = player.GetGlobalTransformInterpolated().Origin + Vector3.Up * (player.BodyHeightM * 0.5f);
        var observeFocus = look.FocusPointFor(camera);
        _hud._UnhandledInput(Press(Key.O));
        Check(!observeBefore && observeOn && !look.Observe && observeFocus.DistanceTo(middle) < 1e-4f, "O switches the observe view on and off in the orbit, focus on the body's middle");

        // Through the Gubble's eyes nothing changes: the picture is the Gubble's, the wheel and the drag do not touch the orbit, and
        // closing it leaves F2 as it was.
        var (distance, yaw, pitch) = (_hud.ShoulderDistanceM, _hud.OrbitYaw, _hud.OrbitPitchDeg);
        _hud._UnhandledInput(Press(Key.I));
        Check(_hud.GubbleViewOpen, "I opens the view through the Gubble's eyes from the orbit");
        _hud.DragHeldForTests = () => true;
        try
        {
            _hud._UnhandledInput(WheelEvent(MouseButton.WheelDown));
            _hud._UnhandledInput(WheelEvent(MouseButton.WheelUp));
            _hud._UnhandledInput(LeftButton(true));
            _hud._UnhandledInput(Motion(new Vector2(50, 10)));
            _hud._UnhandledInput(LeftButton(false));
            Check(_hud.ShoulderDistanceM == distance && _hud.OrbitYaw == yaw && _hud.OrbitPitchDeg == pitch && _hud.Orbiting,
                "through the eyes the wheel does not zoom and a drag does not orbit (it turns the Gubble's eye)");
        }
        finally { _hud.DragHeldForTests = null; }
        _hud._UnhandledInput(Press(Key.I));
        await Frames(4);
        Check(!_hud.GubbleViewOpen && _hud.Orbiting && ViewCamera == camera && camera.Current && _hud.ShoulderDistanceM == distance && _hud.OrbitYaw == yaw && player.MovementFrameYaw == yaw,
            "closed again, the orbit camera, its heading and the movement frame are as they were");
        _hud.ZoomShoulder(1000);
        _hud.SetViewMode(1);
        await Frames(2);
    }
}
