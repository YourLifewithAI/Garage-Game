using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Characters;
using GarageGame.Native.Characters.Contact;
using GarageGame.Native.Room;

namespace GarageGame.Tests.Characters;

/// <summary>
/// K4's suite (docs/runs/RUN-2-CHARACTERS.md, "Contact: how a character meets the land"): real SmallPlayerControllers wearing
/// each of the five shipped characters (CharacterVisual.Create, SetBodyVisual, CharacterContact.Install), each on its own copy
/// of a course with real physics: a flat floor, a 30° slope, a 2 cm step, a pond to wade and swim in, a climbable wall, a
/// carried load, wind, and a wall, a gap and a post to measure intrusion against. Without the gaits (K3) the pose under the
/// contact layer is the rest pose. Headless, --fixed-fps 60. Run: res://tests/native_character_contact.tscn.
/// Prints CHARACTERS_CONTACT_MEASURED lines, one CHARACTERS_INTRUSION line per character (the later round's baseline), then
/// NATIVE_CHARACTER_CONTACT: n/n checks passed.
/// </summary>
public partial class CharacterContactTest : Node3D
{
    private static readonly string[] Shipped = { "cloudpuff", "gubble", "potato_man", "stickbear", "tomato_man" };
    private const float CourseSpacingM = 5f;
    /// <summary>The acceptance's tolerances: soles within 5 mm of the ground, nothing more than 5 mm below it.</summary>
    private const float SoleToleranceM = 0.005f, BelowToleranceM = 0.005f;
    private static readonly float Slope = Mathf.DegToRad(30);
    private static readonly GDScript Physics = GD.Load<GDScript>(SmallPlayerController.WorldPhysicsScript);

    private int _checks;
    private int _failures;
    private CharacterLibrary _library = null!;
    private readonly List<Subject> _subjects = new();

    public override async void _Ready()
    {
        try
        {
            _library = CharacterLibrary.Load(CharacterLibrary.ShippedRoot);
            Check(Shipped.All(id => _library.TryGet(id, out _)), "the five shipped characters load");
            for (var i = 0; i < Shipped.Length; i++)
            {
                _library.TryGet(Shipped[i], out var definition);
                var course = BuildCourse(new Vector3(i * CourseSpacingM, 0, 0));
                var subject = new Subject { Id = Shipped[i], Definition = definition, Course = course };
                Dress(subject, subject.Course.Flat);
                _subjects.Add(subject);
            }
            await Frames(10);
            TestInstall();
            await TestFlat();
            await TestSlope();
            await TestSlopeWalk();
            await TestStep();
            await TestLedge();
            await TestLandings();
            await TestLandingsWithCatFeet();
            await TestWater();
            await TestClimb();
            await TestCarry();
            await TestWind();
            await TestLook();
            await TestIntrusion();
            await TestAllocation();
            await TestSuspended();
            await TestCapsuleUnchanged();
            foreach (var s in _subjects)
            {
                Check(s.NonFiniteFrames == 0 && s.Rig.Pose.NonFiniteWrites == 0,
                    $"{s.Id}: every bone stayed finite through every scenario ({s.NonFiniteFrames} frames with a non-finite bone, {s.Rig.Pose.NonFiniteWrites} refused writes)");
                Check(s.MaxCasts <= 6, $"{s.Id}: at most 6 rays a frame ({s.MaxCasts})");
            }
            GD.Print($"NATIVE_CHARACTER_CONTACT: {_checks - _failures}/{_checks} checks passed; contact on real physics: feet and slopes, the step, landing tiers, water, climbing, carrying, wind, look targets, the capsule unchanged, intrusion measured; not the gaits, the look or the feel");
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (Exception exception)
        {
            GD.PushError("Native character contact test exception: " + exception);
            GetTree().Quit(1);
        }
    }

    // ---- The subjects: a capsule wearing each character, on its own course.

    private sealed class Subject
    {
        public required string Id { get; init; }
        public required CharacterDefinition Definition { get; init; }
        public required Course Course { get; init; }
        public SmallPlayerController Body = null!;
        public CharacterVisual Visual = null!;
        public ContactLayer Contact = null!;
        public LookTargetLayer Look = null!;
        /// <summary>The point the look layer is given (null: none).</summary>
        public Vector3? Interest;
        public int NonFiniteFrames;
        public int MaxCasts;
        public CharacterBody Rig => Visual.Body;
        /// <summary>The motion hint says it floats (the Gubble): it never stands, climbs or swims.</summary>
        public bool Floats => Definition.Motion == MotionKind.Float;
        public bool Legged => Rig.Legs.Count > 0;
    }

    /// <summary>A capsule with the character on and contact installed, standing at <paramref name="feet"/>.</summary>
    private void Dress(Subject s, Vector3 feet)
    {
        s.Body = NewBody(s, "Contact_" + s.Id, feet);
        s.Visual = CharacterVisual.Create(s.Definition);
        s.Body.SetBodyVisual(s.Visual);
        CharacterContact.Install(s.Visual, s.Body, () => s.Interest);
        s.Contact = CharacterContact.FindContact(s.Visual)!;
        s.Look = CharacterContact.FindLook(s.Visual)!;
    }

    private SmallPlayerController NewBody(Subject s, string name, Vector3 feet)
    {
        var body = new SmallPlayerController
        {
            Name = name, ReadKeyboard = false, Floats = s.Floats, CanClimb = !s.Floats, CanSwim = !s.Floats,
            Position = feet + Vector3.Up * (s.Floats ? 0.02f : 0.003f),
        };
        AddChild(body);
        return body;
    }

    private void TestInstall()
    {
        foreach (var s in _subjects)
        {
            var layers = Enumerable.Range(0, s.Visual.Stack.Count).Select(i => s.Visual.Stack[i].Layer).ToList();
            var count = s.Visual.Stack.Count;
            CharacterContact.Install(s.Visual, s.Body);
            Check(s.Contact != null && s.Look != null && layers.Contains(PoseLayer.Contact) && layers.Contains(PoseLayer.Life) && s.Visual.Stack.Count == count,
                $"{s.Id}: the installer adds the contact and look layers once ({string.Join(", ", layers)})");
            Check(s.Visual.Root.GetParent() != null && s.Visual.Root.GetParent().GetParent() == s.Body, $"{s.Id}: the drawn body hangs under the capsule's visual root");
        }
        Check(_subjects.Single(s => s.Id == "stickbear").Contact.FootCount == 2 && _subjects.Single(s => s.Id == "stickbear").Contact.FootIsLeg(0) &&
              _subjects.Single(s => s.Id == "potato_man").Contact.FootCount == 2 && !_subjects.Single(s => s.Id == "potato_man").Contact.FootIsLeg(0) &&
              _subjects.Single(s => s.Id == "gubble").Contact.FootCount == 0,
            "feet come from the roles: legs with IK (Stickbear, Cloudpuff), shoes and feet without (Potato Man, Tomato Man), none on the Gubble");
    }

    // ---- The course: one copy per character, side by side (bodies never collide with one another).

    private sealed class Course
    {
        public required Vector3 Origin { get; init; }
        /// <summary>The slab the walks are on (left out of the intrusion count).</summary>
        public readonly List<Rid> Floors = new();
        public Vector3 Flat => Origin;
        public Vector3 StepStart => Origin + new Vector3(1.2f, 0, 0.6f);
        public float StepRiserZ => Origin.Z + 0.2f;
        public Vector3 ClimbStart => Origin + new Vector3(-1.2f, 0, 0.2f);
        public float WallFaceZ => Origin.Z - 0.2f;
        public Vector3 SlopeCentre => Origin + new Vector3(0, 0.2f, -2.75f);
        public float SlopeY(Vector3 p) => Origin.Y + 0.2f + (p.X - Origin.X) * Mathf.Tan(Mathf.DegToRad(30));
        public Vector3 SlopeNormal => new(-Mathf.Sin(Mathf.DegToRad(30)), Mathf.Cos(Mathf.DegToRad(30)), 0);
        public Vector3 Shelf => Origin + new Vector3(0, -0.05f, 5.4f);
        public Vector3 DeepWater => Origin + new Vector3(0, -0.01f, 6.2f);
        public float WaterY => Origin.Y - 0.01f;
        public Vector3 LookStart => Origin + new Vector3(0, 0, 1.6f);
        public Vector3 WallWalkStart => Origin + new Vector3(1.5f - 0.0215f, 0, 3.2f);
        public Vector3 GapStart => Origin + new Vector3(0, 0, 3.2f);
        public Vector3 Post => Origin + new Vector3(-1.6f, 0, 2.5f);
    }

    private Course BuildCourse(Vector3 origin)
    {
        var course = new Course { Origin = origin };
        var holder = new Node3D { Name = "Course" + (int)(origin.X / CourseSpacingM), Position = origin };
        AddChild(holder);
        StaticBody3D Box(Vector3 centre, Vector3 size, float roll = 0)
        {
            var body = new StaticBody3D { Position = centre, Rotation = new Vector3(0, 0, roll), CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            holder.AddChild(body);
            return body;
        }
        course.Floors.Add(Box(new Vector3(0, -0.05f, 1.5f), new Vector3(4, 0.1f, 5)).GetRid());   // top y=0, x -2..2, z -1..4
        Box(new Vector3(1.2f, 0.01f, 0), new Vector3(0.6f, 0.02f, 0.4f));                          // the 2 cm step, riser at z=0.2
        Box(new Vector3(-1.2f, 0.125f, -0.3f), new Vector3(0.8f, 0.25f, 0.2f));                    // the climbing wall, face at z=-0.2
        Box(new Vector3(-0.535f, 0.125f, 2.5f), new Vector3(1.0f, 0.25f, 0.2f));                   // the gap, 7 cm wide at x=0
        Box(new Vector3(0.535f, 0.125f, 2.5f), new Vector3(1.0f, 0.25f, 0.2f));
        Box(new Vector3(1.55f, 0.125f, 2.5f), new Vector3(0.1f, 0.25f, 1.6f));                     // the wall to walk along, face at x=1.5
        Box(new Vector3(-1.6f, 0.125f, 2.5f), new Vector3(0.03f, 0.25f, 0.03f));                   // the post
        // The 30° slope: its surface passes through (0, 0.2, -2.75), rising toward +X.
        var normal = new Vector3(-Mathf.Sin(Slope), Mathf.Cos(Slope), 0);
        Box(new Vector3(0, 0.2f, -2.75f) - normal * 0.05f, new Vector3(1.6f, 0.1f, 1.2f), Slope);
        // The pond: a 4 cm shelf to wade, then 24 cm deep; the water 1 cm under the rim.
        Box(new Vector3(-1.05f, -0.15f, 5.8f), new Vector3(0.9f, 0.3f, 2.4f));
        Box(new Vector3(1.05f, -0.15f, 5.8f), new Vector3(0.9f, 0.3f, 2.4f));
        Box(new Vector3(0, -0.15f, 4.8f), new Vector3(1.2f, 0.3f, 0.4f));
        Box(new Vector3(0, -0.15f, 6.8f), new Vector3(1.2f, 0.3f, 0.4f));
        Box(new Vector3(0, -0.15f, 5.3f), new Vector3(1.2f, 0.2f, 0.6f));
        Box(new Vector3(0, -0.3f, 6.1f), new Vector3(1.2f, 0.1f, 1.0f));
        var sheet = new SurfaceTool();
        sheet.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var corner in new[] { new Vector2(-0.6f, 5.0f), new Vector2(0.6f, 5.0f), new Vector2(0.6f, 6.6f), new Vector2(-0.6f, 5.0f), new Vector2(0.6f, 6.6f), new Vector2(-0.6f, 6.6f) })
            sheet.AddVertex(new Vector3(corner.X, -0.01f, corner.Y));
        var water = RoomWater.CreateCollider(new[] { ((Shape3D)sheet.Commit().CreateTrimeshShape(), Transform3D.Identity) });
        holder.AddChild(water);
        return course;
    }

    // ---- Helpers.

    private void Check(bool condition, string label)
    {
        _checks++;
        if (condition) return;
        _failures++;
        GD.Print("FAIL " + label);
    }

    /// <summary>A check whose label carries a measured value worth keeping in the run's evidence.</summary>
    private void Report(bool condition, string label)
    {
        GD.Print("CHARACTERS_CONTACT_MEASURED " + label);
        Check(condition, label);
    }

    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            foreach (var s in _subjects) Scan(s);
        }
    }

    /// <summary>Every frame: the bones are finite, and the rays stay within the budget.</summary>
    private static void Scan(Subject s)
    {
        if (!GodotObject.IsInstanceValid(s.Rig)) return;
        s.MaxCasts = Math.Max(s.MaxCasts, s.Contact.Casts);
        for (var b = 0; b < s.Rig.BoneCount; b++)
        {
            var t = s.Rig.FinalBoneTransform(b);
            if (!(float.IsFinite(t.Origin.X) && float.IsFinite(t.Origin.Y) && float.IsFinite(t.Origin.Z) && float.IsFinite(t.Basis.Determinant())))
            {
                s.NonFiniteFrames++;
                return;
            }
        }
    }

    private static void Place(SmallPlayerController body, Vector3 feet, float yaw)
    {
        body.SetControlInput(Vector2.Zero);
        body.SetDiveInput(false, false);
        body.TryTeleportTo(feet + Vector3.Up * 0.003f);
        body.Rotation = new Vector3(0, yaw, 0);
        body.Velocity = Vector3.Zero;
    }

    /// <summary>Yaw that faces the body along <paramref name="direction"/> (it faces -Z at 0).</summary>
    private static float Facing(Vector3 direction) => Mathf.Atan2(-direction.X, -direction.Z);

    private IEnumerable<Subject> Walkers => _subjects.Where(s => !s.Floats);

    /// <summary>A bone's final transform in the world.</summary>
    private static Transform3D World(Subject s, int bone) => s.Rig.GlobalTransform * s.Rig.FinalBoneTransform(bone);

    /// <summary>A leg's sole in the world, after the IK.</summary>
    private static Vector3 Sole(Subject s, int leg) => World(s, s.Rig.Legs[leg].FootBone) * s.Rig.Legs[leg].SoleOffset;

    private readonly Dictionary<(string, int), Vector3[]> _vertices = new();

    /// <summary>
    /// How far the lowest mesh point sits below the ground (positive is below), from each part's bounds, checked against its
    /// real vertices where a corner of the bounds dips under the ground.
    /// </summary>
    private float Below(Subject s, Func<Vector3, float> groundY, Func<RigBone, bool>? which = null)
    {
        var worst = float.MinValue;
        foreach (var bone in s.Rig.Bones)
        {
            if (!bone.HasMesh || (which != null && !which(bone))) continue;
            var t = World(s, bone.Index);
            var bounds = bone.MeshBounds;
            var lowest = float.MaxValue;
            for (var c = 0; c < 8; c++)
            {
                var p = t * (bounds.Position + new Vector3((c & 1) != 0 ? bounds.Size.X : 0, (c & 2) != 0 ? bounds.Size.Y : 0, (c & 4) != 0 ? bounds.Size.Z : 0));
                lowest = Mathf.Min(lowest, p.Y - groundY(p));
            }
            if (lowest >= -0.001f)
            {
                worst = Mathf.Max(worst, -lowest);
                continue;
            }
            foreach (var v in Vertices(s, bone.Index))
            {
                var p = t * v;
                worst = Mathf.Max(worst, groundY(p) - p.Y);
            }
        }
        return worst;
    }

    private Vector3[] Vertices(Subject s, int bone)
    {
        if (_vertices.TryGetValue((s.Id, bone), out var cached)) return cached;
        var mesh = s.Rig.PartMesh(bone)?.Mesh;
        var list = new List<Vector3>();
        if (mesh != null)
            for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                list.AddRange(mesh.SurfaceGetArrays(surface)[(int)Mesh.ArrayType.Vertex].AsVector3Array());
        var vertices = list.ToArray();
        _vertices[(s.Id, bone)] = vertices;
        return vertices;
    }

    /// <summary>The root's up axis in the world and its angle from vertical (degrees).</summary>
    private static (Vector3 Up, float Degrees) RootUp(Subject s)
    {
        var up = World(s, CharacterBody.RootBone).Basis.Y.Normalized();
        return (up, Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(up.Dot(Vector3.Up), -1, 1))));
    }

    private static float TurnDegrees(Quaternion q) => Mathf.RadToDeg(2 * Mathf.Acos(Mathf.Clamp(Mathf.Abs(q.W), 0, 1)));

    private static Godot.Collections.Dictionary Preset(string id, int revision) => Physics.Call("preset", id, revision).AsGodotDictionary();

    private static string Mm(float metres) => FormattableString.Invariant($"{metres * 1000:0.0} mm");
}
