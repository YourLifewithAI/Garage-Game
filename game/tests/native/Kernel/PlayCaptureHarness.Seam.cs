using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Kernel;

/// <summary>
/// Mark 1 of the first recorded play (docs/runs/playtests/2026-10-16-landscape_garage_nominal.md): Stickbear gone into a wall on a rock
/// face right after scree, in the shoulder view. The scenario walks the founder's way on the garage island (--room=landscape_garage_nominal,
/// a scratch profile's copy; wear Stickbear through the profile's player_character): from the worn path at (0.4, 0.9, z) west onto the
/// scree and up the terrain_rock face near (-2.5, 1.8, -30.1), W held, F2, the Gubble on follow, and measures every tick what decides
/// whether the body is seen: how far the shoulder arm got (the spring's hit length), the camera's distance from the body, whether the
/// capsule's middle is in the camera's view and not behind a face, how deep the capsule and the camera sit under the land's surface, and
/// each drawn part against the face the climber holds (SEAM_FACE: its vertices' distance to that plane) and as the camera sees it. Windowed
/// it also saves stills (seam_*.jpg); headless it measures only (pass --probe). It found mark 1: the drawn body pressed into a sloped face
/// (ContactLayer.Press), and the arm beside the body with the face on its right (RoomHud.UpdateShoulderArm). P1 of the first recorded
/// play's fix round.
///
/// Arguments: --seam-z=-30.1[,-30.15,...] the start's z; --seam-yaw=90[,...] the heading walked in on (degrees; 90 is west, the
/// founder's); --seam-turn=0[,90,...] the view turned this many degrees once on the face (--seam-turn-after=60 climbing ticks);
/// --seam-pitch=0 the eye's pitch (radians); --seam-frames=1500. One walk for each z, heading and turn.
/// </summary>
public partial class PlayCaptureHarness
{
    private sealed class SeamWalk
    {
        public int Frames, ClimbFrames, HiddenFrames, NearClampFrames, InsideFrames, CameraInsideFrames, DrawnUnseenFrames, DrawnUnderFrames, MostPartsUnder;
        public float NearestCameraM = float.MaxValue, ShortestArmM = float.MaxValue, DeepestBodyM = float.NegativeInfinity, DeepestCameraM = float.NegativeInfinity;
        public Vector3 HighestAt;
        public readonly HashSet<string> Parts = new();
        public string Worst = "";
    }

    private async Task SeamF2()
    {
        var player = _world.Player;
        var companion = _world.Companion;
        var headless = DisplayServer.GetName() == "headless";
        var lanes = Arg("seam-z", "-29.8").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(z => float.Parse(z, CultureInfo.InvariantCulture)).ToArray();
        var pitch = float.Parse(Arg("seam-pitch", "0"), CultureInfo.InvariantCulture);
        var maxFrames = int.Parse(Arg("seam-frames", "1500"), CultureInfo.InvariantCulture);
        // A player looking round while on the face: the view turned this many degrees (positive to the left) once the climb is under way.
        var turns = Arg("seam-turn", "0").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray();
        var turnAfter = int.Parse(Arg("seam-turn-after", "60"), CultureInfo.InvariantCulture);
        // The heading walked in on (degrees; 90 walks west, as the founder did).
        var yaws = Arg("seam-yaw", "90").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray();
        var arm = player.GetNode<SpringArm3D>("FollowCameraArm");
        var camera = arm.GetNode<Camera3D>("FollowCamera");
        var space = player.GetWorld3D().DirectSpaceState;
        var exclude = new Godot.Collections.Array<Rid> { player.GetRid(), companion.GetRid() };

        // The land's own surface over a point, read from above (front faces only): a heightfield's depth under it.
        float GroundAbove(Vector3 at, out string what)
        {
            using var query = PhysicsRayQueryParameters3D.Create(new Vector3(at.X, at.Y + 3, at.Z), new Vector3(at.X, at.Y - 3, at.Z), RoomBuilder.WorldLayer, exclude);
            query.HitBackFaces = false;
            using var hit = space.IntersectRay(query);
            what = hit.Count > 0 && hit["collider"].AsGodotObject() is Node node ? node.Name.ToString() : "";
            return hit.Count > 0 ? hit["position"].AsVector3().Y : float.NegativeInfinity;
        }
        // Whether a drawn face stands between the camera and a point (front faces only: the land is drawn one-sided).
        bool Occluded(Vector3 from, Vector3 to, out string what)
        {
            using var query = PhysicsRayQueryParameters3D.Create(from, to, RoomBuilder.WorldLayer, exclude);
            query.HitBackFaces = false;
            using var hit = space.IntersectRay(query);
            what = hit.Count > 0 && hit["collider"].AsGodotObject() is Node node ? node.Name.ToString() : "";
            return hit.Count > 0;
        }
        static string V(Vector3 v) => FormattableString.Invariant($"({v.X:0.000}, {v.Y:0.000}, {v.Z:0.000})");
        // Each drawn part against the face the climber holds (the plane under a ray from the capsule's middle along -ClimbNormal): its
        // vertex nearest that plane.
        string FaceDump()
        {
            var middle = player.GlobalPosition + Vector3.Up * (player.BodyHeightM * 0.5f);
            using var query = PhysicsRayQueryParameters3D.Create(middle, middle - player.ClimbNormal * 0.2f, RoomBuilder.WorldLayer, exclude);
            query.HitBackFaces = false;
            using var hit = space.IntersectRay(query);
            if (hit.Count == 0) return "no face under the hands";
            var at = hit["position"].AsVector3();
            var n = hit["normal"].AsVector3();
            var lines = new List<(float D, string Name)>();
            foreach (var mesh in player.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
            {
                if (!mesh.IsVisibleInTree() || mesh.Mesh == null) continue;
                var nearest = float.MaxValue;
                var global = mesh.GlobalTransform;
                foreach (var vertex in mesh.Mesh.GetFaces()) nearest = Mathf.Min(nearest, (global * vertex - at).Dot(n));
                lines.Add((nearest, mesh.GetParent()?.Name + "/" + mesh.Name));
            }
            var behind = lines.Count(l => l.D < -0.002f);
            var worst = lines.OrderBy(l => l.D).Take(3).Select(l => FormattableString.Invariant($"{l.Name} {l.D * 1000:0.0}mm"));
            var visual = player.FindChild("OriginalPrototypeBody", false, false) as Node3D;
            return FormattableString.Invariant($"face at {V(at)} n {V(n)}, capsule middle {(middle - at).Dot(n) * 1000:0.0} mm in front; drawn parts more than 2 mm behind it {behind}/{lines.Count}; worst {string.Join(", ", worst)}; visual root {(visual == null ? "?" : V(visual.Position) + " yaw " + Mathf.RadToDeg(visual.Rotation.Y).ToString("0", CultureInfo.InvariantCulture))}");
        }
        // The drawn body (the prototype figure or a drawn character): each visible part's middle, whether it sits under the land's
        // surface, and whether the camera sees it (in the view, no drawn face in front).
        (int Parts, int Under, int Seen) Drawn()
        {
            int parts = 0, under = 0, seen = 0;
            foreach (var mesh in player.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
            {
                if (!mesh.IsVisibleInTree() || mesh.Mesh == null) continue;
                var centre = mesh.GlobalTransform * mesh.GetAabb().GetCenter();
                parts++;
                if (GroundAbove(centre, out _) > centre.Y + 0.001f) under++;
                if (camera.IsPositionInFrustum(centre) && !Occluded(camera.GlobalPosition, centre, out _)) seen++;
            }
            return (parts, under, seen);
        }

        foreach (var z in lanes)
        foreach (var yaw in yaws)
        foreach (var turn in turns)
        {
            var walk = new SeamWalk();
            var turned = false;
            await Stage(new Vector3(0.4f, 0.95f, z), yaw, new Vector3(0.1f, 0, 0.15f));
            player.SetView(Mathf.DegToRad(yaw), pitch);
            companion.Follow();
            for (var i = 0; i < 30; i++) await NextFrame();
            GD.Print($"SEAM_START z={z.ToString(CultureInfo.InvariantCulture)} yaw={yaw.ToString(CultureInfo.InvariantCulture)} turn={turn.ToString(CultureInfo.InvariantCulture)} at {V(player.GlobalPosition)} pitch {pitch.ToString(CultureInfo.InvariantCulture)} on floor {player.IsOnFloor()}");
            player.SetControlInput(new Vector2(0, 1));
            var stills = 0;
            var lastClimbY = float.NaN;
            var stuck = 0;
            for (var frame = 0; frame < maxFrames; frame++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                walk.Frames++;
                var feet = player.GlobalPosition;
                var middle = feet + Vector3.Up * (player.BodyHeightM * 0.5f);
                var cam = camera.GlobalPosition;
                var toBody = middle.DistanceTo(cam);
                var hitLength = arm.GetHitLength();
                var inView = camera.IsPositionInFrustum(middle) && !camera.IsPositionBehind(middle);
                var hidden = Occluded(cam, middle, out var occluder);
                var groundAtBody = GroundAbove(middle, out var groundWhat);
                var bodyDepth = groundAtBody - (feet.Y + player.BodyRadiusM);
                var cameraDepth = GroundAbove(cam, out _) - cam.Y;
                for (var c = 0; c < player.GetSlideCollisionCount(); c++)
                    if (player.GetSlideCollision(c).GetCollider() is Node touched) walk.Parts.Add(touched.Name);
                var climbing = player.IsClimbing;
                if (climbing && !turned && walk.ClimbFrames >= turnAfter && player.GlobalPosition.X < -2.4f)
                {
                    player.SetView(player.GlobalRotation.Y + Mathf.DegToRad(turn), pitch);
                    turned = true;
                }
                var shoulderUnder = GroundAbove(arm.GlobalPosition, out _) - arm.GlobalPosition.Y;
                var drawn = Drawn();
                if (climbing)
                {
                    walk.ClimbFrames++;
                    if (!inView || hidden) walk.HiddenFrames++;
                    if (toBody < 0.06f) walk.NearClampFrames++;
                    if (bodyDepth > 0.005f) walk.InsideFrames++;
                    if (cameraDepth > 0) walk.CameraInsideFrames++;
                    if (drawn.Seen == 0) walk.DrawnUnseenFrames++;
                    if (drawn.Under > 0) walk.DrawnUnderFrames++;
                    walk.MostPartsUnder = Math.Max(walk.MostPartsUnder, drawn.Under);
                    walk.NearestCameraM = Mathf.Min(walk.NearestCameraM, toBody);
                    walk.ShortestArmM = Mathf.Min(walk.ShortestArmM, hitLength);
                    if (bodyDepth > walk.DeepestBodyM) walk.DeepestBodyM = bodyDepth;
                    walk.DeepestCameraM = Mathf.Max(walk.DeepestCameraM, cameraDepth);
                    if (feet.Y > walk.HighestAt.Y || walk.HighestAt == Vector3.Zero) walk.HighestAt = feet;
                    if ((!inView || hidden) && walk.Worst.Length == 0)
                        walk.Worst = FormattableString.Invariant($"first hidden at frame {frame}: body {V(feet)} normal {V(player.ClimbNormal)}, camera {V(cam)} {toBody:0.000} m off (arm {hitLength:0.000} of {arm.SpringLength:0.000}), in view {inView}, behind a face {hidden} ({occluder}), body {bodyDepth * 1000:0.0} mm under {groundWhat}, camera {cameraDepth * 1000:0.0} mm under the land");
                    stuck = Mathf.Abs(feet.Y - lastClimbY) < 0.0002f ? stuck + 1 : 0;
                    lastClimbY = feet.Y;
                }
                if (frame % 15 == 0 || (climbing && frame % 5 == 0))
                    GD.Print(FormattableString.Invariant($"SEAM f={frame} {(climbing ? "climb" : player.IsOnFloor() ? "floor" : "air")} feet={V(feet)} n={V(player.ClimbNormal)} yaw={Mathf.RadToDeg(player.GlobalRotation.Y):0.0} arm={hitLength:0.000}/{arm.SpringLength:0.000} cam={V(cam)} d={toBody:0.000} view={inView} hidden={hidden}:{occluder} body_under={bodyDepth * 1000:0.0}mm({groundWhat}) cam_under={cameraDepth * 1000:0.0}mm shoulder_under={shoulderUnder * 1000:0.0}mm drawn={drawn.Seen}/{drawn.Parts} seen, {drawn.Under} under gubble={companion.GlobalPosition.DistanceTo(feet):0.00}"));
                if (climbing && walk.ClimbFrames % 30 == 1) GD.Print($"SEAM_FACE f={frame} " + FaceDump());
                if (!headless && climbing && stills < 12 && walk.ClimbFrames % 30 == 1)
                {
                    var image = await Grab();
                    var name = FormattableString.Invariant($"{_tag}_seam_z{z:0.000}_y{yaw:0}_t{turn:0}_{stills:00}_f{frame}.jpg");
                    image.SaveJpg(System.IO.Path.Combine(_out, name), 0.9f);
                    GD.Print($"SEAM_STILL {name} hidden={!inView || hidden} d={toBody:0.000} drawn {drawn.Seen}/{drawn.Parts} seen, {drawn.Under} under");
                    stills++;
                }
                if (turned && !climbing && player.IsOnFloor()) break;
                if (climbing && stuck > 180) break;
                if (walk.ClimbFrames > 900) break;
            }
            player.SetControlInput(Vector2.Zero);
            GD.Print(FormattableString.Invariant($"SEAM_RESULT z={z} yaw={yaw} turn={turn} frames={walk.Frames} climbing={walk.ClimbFrames} hidden={walk.HiddenFrames} camera_within_6cm={walk.NearClampFrames} body_inside={walk.InsideFrames} camera_inside={walk.CameraInsideFrames} drawn_unseen={walk.DrawnUnseenFrames} drawn_under={walk.DrawnUnderFrames} most_parts_under={walk.MostPartsUnder} nearest_camera={walk.NearestCameraM:0.000} shortest_arm={walk.ShortestArmM:0.000} deepest_body={walk.DeepestBodyM * 1000:0.0}mm deepest_camera={walk.DeepestCameraM * 1000:0.0}mm highest={V(walk.HighestAt)} parts={string.Join("|", walk.Parts.OrderBy(p => p, StringComparer.Ordinal))}"));
            if (walk.Worst.Length > 0) GD.Print("SEAM_WORST " + walk.Worst);
            _records.Add(new Dictionary<string, object>
            {
                ["scenario"] = "seam_f2", ["subject"] = FormattableString.Invariant($"z {z} turn {turn}"), ["climb_frames"] = walk.ClimbFrames, ["hidden_frames"] = walk.HiddenFrames,
                ["body_inside_frames"] = walk.InsideFrames, ["camera_inside_frames"] = walk.CameraInsideFrames,
                ["nearest_camera_m"] = walk.ClimbFrames > 0 ? Math.Round(walk.NearestCameraM, 4) : -1, ["shortest_arm_m"] = walk.ClimbFrames > 0 ? Math.Round(walk.ShortestArmM, 4) : -1,
                ["deepest_body_m"] = walk.ClimbFrames > 0 ? Math.Round(walk.DeepestBodyM, 4) : -1, ["worst"] = walk.Worst,
            });
        }
    }
}
