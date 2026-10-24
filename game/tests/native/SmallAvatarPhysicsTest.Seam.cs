using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Characters;
using GarageGame.Native.Characters.Contact;
using GarageGame.Native.Room;

namespace GarageGame.Tests;

/// <summary>
/// Mark 1 of the first recorded play (16 October, docs/runs/playtests/2026-10-16-landscape_garage_nominal.md): "the Stickbear character
/// disappears into a wall", climbing a 52 degree terrain_rock face right after terrain_scree in the shoulder view. Reproduced on a copy
/// of the garage (PlayCaptureHarness.Seam.cs), it was two things, neither the capsule: the drawn character lay along a sloped face and
/// was pressed to it as if it stood upright, so Stickbear was drawn up to 3 cm inside the rock while the capsule held 4 cm in front
/// (the founder's still); and with the view turned so the face was on the body's right, the shoulder camera's arm, hung from that
/// shoulder, met the face within centimetres and the body left the frame. Here two climbable faces meet at an angle as scree meets rock,
/// one-sided as the land's shells are, and a climber in Stickbear crosses the seam: the capsule and every drawn part stay in front of
/// both faces, the shoulder camera keeps the body in its frame and stays outside it, the view straight and turned. Then single slopes
/// from 46 to 70 degrees: every drawn part in front.
/// </summary>
public partial class SmallAvatarPhysicsTest
{
    /// <summary>One face of a landform: a quad of two triangles in one plane, one-sided (back faces do not collide), its own static body.</summary>
    private sealed record LandFace(string Name, Vector3[] Corners, Vector3 Normal, StaticBody3D Body)
    {
        /// <summary>Signed distance from the face's plane (in front positive), or null where the point is not over the face.</summary>
        public float? Distance(Vector3 point)
        {
            var d = (point - Corners[0]).Dot(Normal);
            var onPlane = point - Normal * d;
            for (var i = 0; i < Corners.Length; i++)
            {
                var a = Corners[i];
                var b = Corners[(i + 1) % Corners.Length];
                if ((b - a).Cross(onPlane - a).Dot(Normal) < -1e-6f) return null;
            }
            return d;
        }
    }

    /// <summary>
    /// A planar quad (corners counter-clockwise seen from its front) as a one-sided ConcavePolygonShape3D on the world layer, as the
    /// land's tiles are; checked from the front with back faces off, so it is the front a climber meets.
    /// </summary>
    private async Task<LandFace> OneSidedFace(string name, params Vector3[] corners)
    {
        var normal = (corners[1] - corners[0]).Cross(corners[2] - corners[0]).Normalized();
        LandFace Build(bool reversed)
        {
            var (a, b, c, d) = (corners[0], corners[1], corners[2], corners[3]);
            var faces = reversed ? new[] { a, b, c, a, c, d } : new[] { a, c, b, a, d, c };
            var body = new StaticBody3D { Name = name, CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
            body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = faces, BackfaceCollision = false } });
            AddChild(body);
            return new LandFace(name, corners, normal, body);
        }
        bool Front(LandFace face)
        {
            var middle = face.Corners.Aggregate(Vector3.Zero, (s, v) => s + v) / face.Corners.Length;
            using var query = PhysicsRayQueryParameters3D.Create(middle + normal * 0.1f, middle - normal * 0.1f, RoomBuilder.WorldLayer);
            query.HitBackFaces = false;
            using var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
            return hit.Count > 0;
        }
        var built = Build(false);
        await Frames(2);
        if (Front(built)) return built;
        built.Body.QueueFree();
        built = Build(true);
        await Frames(2);
        Check(Front(built), $"the {name} face is one-sided toward the climber");
        return built;
    }

    /// <summary>A body wearing Stickbear, dressed as the room dresses the player, with the HUD's shoulder camera on it (F2).</summary>
    private (SmallPlayerController Body, ContactLayer Contact, RoomHud Hud, Camera3D Camera, List<(MeshInstance3D Mesh, Vector3[] Vertices)> Parts) DressInStickbear(Vector3 feet, float yaw)
    {
        var stickbear = _stickbear ??= CharacterLibrary.Load(CharacterLibrary.ShippedRoot).Characters.First(c => c.Id == "stickbear");
        var body = new SmallPlayerController { Name = "SeamClimber", ReadKeyboard = false, Position = feet, Rotation = new Vector3(0, yaw, 0) };
        var visual = CharacterVisual.Create(stickbear);
        CharacterMotion.Install(visual, body);
        body.SetBodyVisual(visual);
        AddChild(body);
        var hud = new RoomHud { Player = body, Companion = _companion, RoomTitle = "SEAM" };
        AddChild(hud);
        var camera = body.GetNode<SpringArm3D>("FollowCameraArm").GetNode<Camera3D>("FollowCamera");
        var parts = visual.Root.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>()
            .Where(m => m.Mesh != null).Select(m => (m, m.Mesh.GetFaces().Distinct().ToArray())).ToList();
        return (body, CharacterContact.FindContact(visual)!, hud, camera, parts);
    }

    private CharacterDefinition? _stickbear;

    /// <summary>Take a seam climber's HUD away with the diorama rig it hung on the test's root.</summary>
    private static void FreeSeamHud(RoomHud hud)
    {
        hud.DioramaCamera.GetParent()?.GetParent()?.QueueFree();
        hud.QueueFree();
    }

    private sealed class SeamWorst
    {
        public float Capsule = float.MaxValue, Drawn = float.MaxValue, CameraToAxis = float.MaxValue, CameraFace = float.MaxValue;
        public int Ticks, OutOfFrame;
        public string DrawnPart = "";
        public void Merge(SeamWorst other)
        {
            Capsule = Mathf.Min(Capsule, other.Capsule);
            if (other.Drawn < Drawn) (Drawn, DrawnPart) = (other.Drawn, other.DrawnPart);
            CameraToAxis = Mathf.Min(CameraToAxis, other.CameraToAxis);
            CameraFace = Mathf.Min(CameraFace, other.CameraFace);
            Ticks += other.Ticks;
            OutOfFrame += other.OutOfFrame;
        }
    }

    /// <summary>One tick of the climb measured against the faces: the capsule, every drawn vertex, the shoulder camera.</summary>
    private static void MeasureSeamTick(SeamWorst worst, SmallPlayerController body, Camera3D camera, IReadOnlyList<(MeshInstance3D Mesh, Vector3[] Vertices)> parts, IReadOnlyList<LandFace> faces, bool drawn)
    {
        worst.Ticks++;
        var r = body.BodyRadiusM;
        var low = body.GlobalPosition + Vector3.Up * r;
        var high = body.GlobalPosition + Vector3.Up * (body.BodyHeightM - r);
        foreach (var face in faces)
        {
            foreach (var centre in new[] { low, high })
                if (face.Distance(centre) is { } d) worst.Capsule = Mathf.Min(worst.Capsule, d - r);
            if (face.Distance(camera.GlobalPosition) is { } c) worst.CameraFace = Mathf.Min(worst.CameraFace, c);
        }
        if (drawn)
            foreach (var (mesh, vertices) in parts)
            {
                if (!mesh.IsVisibleInTree()) continue;
                var t = mesh.GlobalTransform;
                foreach (var vertex in vertices)
                {
                    var world = t * vertex;
                    foreach (var face in faces)
                        if (face.Distance(world) is { } d && d < worst.Drawn) (worst.Drawn, worst.DrawnPart) = (d, mesh.GetParent()?.Name ?? mesh.Name);
                }
            }
        // The camera: outside the body (its distance to the capsule's axis), and the body's middle in its frame.
        var axis = high - low;
        var along = Mathf.Clamp((camera.GlobalPosition - low).Dot(axis) / axis.LengthSquared(), 0, 1);
        worst.CameraToAxis = Mathf.Min(worst.CameraToAxis, camera.GlobalPosition.DistanceTo(low + axis * along));
        var middle = body.GlobalPosition + Vector3.Up * (body.BodyHeightM * 0.5f);
        if (!camera.IsPositionInFrustum(middle) || camera.IsPositionBehind(middle)) worst.OutOfFrame++;
    }

    private async Task TestClimbAcrossASeam()
    {
        var o = new Vector3(800, 0, 1600);
        var floor = Solid(o + new Vector3(0, -0.05f, 0), new BoxShape3D { Size = new Vector3(2, 0.1f, 2) });
        // Scree at 52 degrees from the foot (x 0) up to 0.25 m, rising toward -X; rock at 66 degrees from there to 0.7 m: two shell
        // parts that meet at an angle along the seam, as terrain_scree met terrain_rock under the founder's climb.
        const float halfWidth = 0.4f;
        var screeRun = 0.25f / Mathf.Tan(Mathf.DegToRad(52));
        var rockRun = 0.45f / Mathf.Tan(Mathf.DegToRad(66));
        var scree = await OneSidedFace("SeamScree", o + new Vector3(0, 0, halfWidth), o + new Vector3(0, 0, -halfWidth),
            o + new Vector3(-screeRun, 0.25f, -halfWidth), o + new Vector3(-screeRun, 0.25f, halfWidth));
        var rock = await OneSidedFace("SeamRock", o + new Vector3(-screeRun, 0.25f, halfWidth), o + new Vector3(-screeRun, 0.25f, -halfWidth),
            o + new Vector3(-screeRun - rockRun, 0.7f, -halfWidth), o + new Vector3(-screeRun - rockRun, 0.7f, halfWidth));
        var faces = new[] { scree, rock };
        Check(scree.Normal.X > 0 && rock.Normal.X > 0 && Mathf.Abs(Mathf.RadToDeg(scree.Normal.AngleTo(rock.Normal)) - 14) < 0.5f,
            $"two climbable faces meet at the seam at {Mathf.RadToDeg(scree.Normal.AngleTo(rock.Normal)):0.0} degrees (52 and 66 degree slopes)");

        // Straight at the face, the view behind the body: up across the seam.
        var (body, contact, hud, camera, parts) = DressInStickbear(o + new Vector3(0.15f, 0.003f, 0), Mathf.Pi * 0.5f);
        await Frames(10);
        hud.SetViewMode(1);
        body.SetView(Mathf.Pi * 0.5f, 0.3f);
        var straight = new SeamWorst();
        var turned = new SeamWorst();
        var crossed = false;
        body.SetControlInput(new Vector2(0, 1));
        for (var i = 0; i < 900 && body.GlobalPosition.Y - o.Y < 0.5f; i++)
        {
            await Frames(1);
            if (!body.IsClimbing || contact.ClimbWeight < 0.9f) continue;
            MeasureSeamTick(straight, body, camera, parts, faces, drawn: true);
            crossed |= body.GlobalPosition.Y - o.Y > 0.3f;
        }
        body.SetControlInput(Vector2.Zero);
        Report(crossed && straight.Ticks > 60 && straight.Capsule > -0.0015f,
            $"a climber crosses from scree to rock ({straight.Ticks} ticks measured, up to {body.GlobalPosition.Y - o.Y:0.00} m): the capsule never inside either face (closest {straight.Capsule * 1000:0.0} mm)");
        Report(straight.Drawn > -0.0005f,
            $"every drawn part of Stickbear stays in front of both faces (nearest vertex {straight.Drawn * 1000:0.0} mm, {straight.DrawnPart})");
        Report(straight.OutOfFrame == 0 && straight.CameraToAxis >= body.BodyRadiusM && straight.CameraFace > 0,
            $"the shoulder camera keeps the body in its frame ({straight.OutOfFrame} ticks out) and stays outside it ({straight.CameraToAxis * 100:0.0} cm from the axis) and in front of the faces");

        // The view turned so the face is on the body's right (a player looking round on the face): the arm, hung from the right shoulder, meets the face.
        Check(body.TryTeleportTo(o + new Vector3(0.15f, 0.003f, 0)), "the climber back at the foot of the scree");
        body.SetView(Mathf.Pi * 0.5f, 0.3f);
        await Frames(10);
        body.SetControlInput(new Vector2(0, 1));
        var turnedYet = false;
        var settle = 0;
        for (var i = 0; i < 900 && body.GlobalPosition.Y - o.Y < 0.5f; i++)
        {
            await Frames(1);
            if (!body.IsClimbing || contact.ClimbWeight < 0.9f) continue;
            if (!turnedYet && body.GlobalPosition.Y - o.Y > 0.06f)
            {
                body.SetView(Mathf.Pi * 0.5f + Mathf.DegToRad(120), 0.3f);
                turnedYet = true;
            }
            if (!turnedYet || ++settle < 3) continue;
            MeasureSeamTick(turned, body, camera, parts, faces, drawn: false);
        }
        body.SetControlInput(Vector2.Zero);
        var right = body.GlobalBasis.X;
        Report(turnedYet && turned.Ticks > 60 && right.Dot(-scree.Normal) > 0.5f && turned.OutOfFrame == 0 && turned.CameraToAxis >= body.BodyRadiusM && turned.CameraFace > 0,
            $"the view turned 120 degrees so the face is on the body's right ({turned.Ticks} ticks): the camera keeps the body in its frame ({turned.OutOfFrame} ticks out), " +
            $"never closer than a body radius to it ({turned.CameraToAxis * 100:0.0} cm from the axis) and in front of the faces ({turned.CameraFace * 1000:0.0} mm)");
        FreeSeamHud(hud);
        body.QueueFree();
        await FreeAll(new Node3D[] { scree.Body, rock.Body, floor });

        // Single slopes from 46 to 70 degrees, each climbed a while: every drawn part in front.
        var slopes = new List<string>();
        var slopeWorst = new SeamWorst();
        foreach (var degrees in new[] { 46f, 58f, 70f })
        {
            var s = o + new Vector3(0, 0, 3);
            var run = 0.6f / Mathf.Tan(Mathf.DegToRad(degrees));
            var ground = Solid(s + new Vector3(0, -0.05f, 0), new BoxShape3D { Size = new Vector3(2, 0.1f, 2) });
            var slope = await OneSidedFace($"Slope{degrees:0}", s + new Vector3(0, 0, halfWidth), s + new Vector3(0, 0, -halfWidth),
                s + new Vector3(-run, 0.6f, -halfWidth), s + new Vector3(-run, 0.6f, halfWidth));
            var climber = DressInStickbear(s + new Vector3(0.15f, 0.003f, 0), Mathf.Pi * 0.5f);
            await Frames(10);
            climber.Hud.SetViewMode(1);
            var worst = new SeamWorst();
            climber.Body.SetControlInput(new Vector2(0, 1));
            for (var i = 0; i < 600 && climber.Body.GlobalPosition.Y - s.Y < 0.35f; i++)
            {
                await Frames(1);
                if (!climber.Body.IsClimbing || climber.Contact.ClimbWeight < 0.9f) continue;
                MeasureSeamTick(worst, climber.Body, climber.Camera, climber.Parts, new[] { slope }, drawn: true);
            }
            slopes.Add(FormattableString.Invariant($"{degrees:0}: {worst.Ticks} ticks, nearest {worst.Drawn * 1000:0.0} mm ({worst.DrawnPart})"));
            slopeWorst.Merge(worst);
            Check(worst.Ticks > 30, FormattableString.Invariant($"the {degrees:0} degree slope is climbed ({worst.Ticks} ticks)"));
            FreeSeamHud(climber.Hud);
            climber.Body.QueueFree();
            await FreeAll(new Node3D[] { slope.Body, ground });
        }
        Report(slopeWorst.Drawn > -0.0005f && slopeWorst.Capsule > -0.0015f, "on slopes from 46 to 70 degrees every drawn part stays in front of the face: " + string.Join("; ", slopes));
    }
}
