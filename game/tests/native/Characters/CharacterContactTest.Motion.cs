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

/// <summary>The intrusion baseline, the layers' allocations, and the capsule's motion with and without the drawn body.</summary>
public partial class CharacterContactTest
{
    /// <summary>
    /// Scripted walks along a wall, through a gap and around a post: how many millimetre-seconds each part's mesh bounds spend
    /// inside the world. A measurement only, printed as the baseline for the round that answers it.
    /// </summary>
    private async Task TestIntrusion()
    {
        // The meter itself, on a box of known overlap: 2 cm cubes reaching 5 mm into the wall walk's wall.
        {
            var s = _subjects[0];
            var meter = new IntrusionMeter(s.Rig, RoomBuilder.BodyMask, s.Course.Floors);
            var face = s.Course.Origin + new Vector3(1.5f, 0.1f, 2.5f);
            var space = GetWorld3D().DirectSpaceState;
            var cube = new Aabb(new Vector3(-0.01f, -0.01f, -0.01f), new Vector3(0.02f, 0.02f, 0.02f));
            // Swept in from 5 cm in front of the wall, as a part is from the body's axis.
            var from = face + new Vector3(-0.05f, 0, 0);
            var inside = meter.Depth(space, new Transform3D(Basis.Identity, face + new Vector3(-0.005f, 0, 0)), cube, from);
            var turned = meter.Depth(space, new Transform3D(new Basis(Vector3.Up, 0.3f), face + new Vector3(-0.012f, 0, 0)), cube, from);
            var clear = meter.Depth(space, new Transform3D(Basis.Identity, face + new Vector3(-0.011f, 0, 0)), cube, from);
            var sunk = meter.Depth(space, new Transform3D(Basis.Identity, face + new Vector3(0.015f, 0, 0)),
                new Aabb(new Vector3(-0.004f, -0.004f, -0.004f), new Vector3(0.008f, 0.008f, 0.008f)), from);
            var pairsOnly = meter.Depth(space, new Transform3D(Basis.Identity, face + new Vector3(-0.005f, 0, 0)), cube);
            // The sweep is as fine as the engine's shape cast (about half a millimetre here).
            Report(Mathf.Abs(inside - 0.005f) < 0.0008f && turned > 0 && turned < 0.001f && clear == 0 && Mathf.Abs(sunk - 0.019f) < 0.0015f &&
                   Mathf.Abs(pairsOnly - 0.005f) < 0.0005f,
                $"the intrusion meter reads a 2 cm box 5 mm into a wall as {Mm(inside)} ({Mm(pairsOnly)} from the contact alone), a turned one as {Mm(turned)}, one 1 mm short of it as {Mm(clear)}, an 8 mm box sunk whole 1.9 cm past the face of a 10 cm wall as {Mm(sunk)}");
        }

        var meters = _subjects.ToDictionary(s => s.Id, s => new IntrusionMeter(s.Rig, RoomBuilder.BodyMask, s.Course.Floors.Append(s.Body.GetRid())));
        var perWalk = _subjects.ToDictionary(s => s.Id, _ => new List<(string Walk, double MmS)>());
        async Task Walk(string name, Func<Subject, Vector3> start, Func<Subject, float> yaw, int frames, Action<Subject, int>? steer = null)
        {
            foreach (var s in _subjects) Place(s.Body, start(s), yaw(s));
            await Frames(20);
            var before = meters.ToDictionary(m => m.Key, m => m.Value.TotalMmS);
            foreach (var s in _subjects) s.Body.SetControlInput(new Vector2(0, 1));
            for (var f = 0; f < frames; f++)
            {
                foreach (var s in _subjects) steer?.Invoke(s, f);
                await Frames(1);
                var space = GetWorld3D().DirectSpaceState;
                foreach (var s in _subjects) meters[s.Id].Sample(space, 1f / 60);
            }
            foreach (var s in _subjects)
            {
                s.Body.SetControlInput(Vector2.Zero);
                perWalk[s.Id].Add((name, meters[s.Id].TotalMmS - before[s.Id]));
            }
        }
        // Along a wall, the capsule 1.5 mm from it; through a 7 cm gap; around a 3 cm post at 4.5 cm.
        await Walk("wall", s => s.Course.WallWalkStart, _ => 0, 240);
        await Walk("gap", s => s.Course.GapStart, _ => 0, 220);
        await Walk("post", s => s.Course.Post + new Vector3(0.045f, 0, 0.45f), _ => 0, 300, (s, f) =>
        {
            // Straight to the post, half way round it, then away.
            var post = s.Course.Post;
            var at = s.Body.GlobalPosition;
            var radial = new Vector3(at.X - post.X, 0, at.Z - post.Z);
            if (at.Z > post.Z && radial.X > 0 && s.Body.Rotation.Y == 0) return;
            if (radial.X > -0.03f || at.Z < post.Z)
            {
                var r = radial.Normalized();
                var tangent = new Vector3(r.Z, 0, -r.X) - r * ((radial.Length() - 0.045f) * 20);
                s.Body.Rotation = new Vector3(0, Facing(tangent), 0);
            }
            else s.Body.Rotation = new Vector3(0, Facing(Vector3.Back), 0);
        });
        foreach (var s in _subjects)
        {
            var meter = meters[s.Id];
            var walks = string.Join(", ", perWalk[s.Id].Select(w => FormattableString.Invariant($"{w.Walk} {w.MmS:0.0}")));
            GD.Print(FormattableString.Invariant($"CHARACTERS_INTRUSION {s.Id}: {meter.TotalMmS:0.0} mm·s ({walks}) over {meter.SampledS:0.0} s; worst depth {meter.WorstDepthM * 1000:0.0} mm ({meter.WorstPart}); ") +
                meter.Describe(4)[(meter.Describe(4).IndexOf("; ", StringComparison.Ordinal) + 2)..]);
            Check(meter.Samples > 700 && double.IsFinite(meter.TotalMmS), $"{s.Id}: the intrusion walks were measured");
        }
        var widest = _subjects.OrderByDescending(s => s.Rig.Profile.WidthM).First();
        var narrowest = _subjects.OrderBy(s => s.Rig.Profile.WidthM).First();
        Check(meters[widest.Id].TotalMmS > meters[narrowest.Id].TotalMmS,
            $"the widest character ({widest.Id}) intrudes more than the narrowest ({narrowest.Id}): the measure sees width");
    }

    /// <summary>The contact and look layers allocate nothing per frame (rays and all).</summary>
    private async Task TestAllocation()
    {
        foreach (var s in _subjects)
        {
            Place(s.Body, s.Course.SlopeCentre, s.Floats ? 0 : Facing(Vector3.Right));
            s.Interest = s.Course.SlopeCentre + new Vector3(0.3f, 0.1f, 0);
        }
        await Frames(30);
        foreach (var s in _subjects)
        {
            var state = s.Body.LastBodyState;
            var stack = s.Visual.Stack;
            var t = s.Visual.TimeS;
            for (var frame = 0; frame < 3; frame++) stack.Evaluate(state, new PoseClock(t + frame / 60.0, 1f / 60, 1_000_000 + frame));
            var before = GC.GetAllocatedBytesForCurrentThread();
            var casts = 0;
            for (var frame = 3; frame < 303; frame++)
            {
                stack.Evaluate(state, new PoseClock(t + frame / 60.0, 1f / 60, 1_000_000 + frame));
                casts += s.Contact.Casts;
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (var frame = 303; frame < 603; frame++) stack.Evaluate(state, new PoseClock(t + frame / 60.0, 1f / 60, 1_000_000 + frame));
            var micros = clock.Elapsed.TotalMilliseconds * 1000 / 300;
            Report(allocated == 0 && (s.Floats || casts >= 300 * s.Contact.FootCount),
                FormattableString.Invariant($"{s.Id}: 300 frames of the contact and look layers, {casts} rays included, allocate {allocated} bytes; {micros:0} microseconds a frame (Debug build, headless)"));
        }
        foreach (var s in _subjects) s.Interest = null;
        await Frames(5);
    }

    /// <summary>
    /// The tumble (K7) suspends Gait, Contact and Life: the guard lets go of the leg IK and the look-ats so stale targets never
    /// pin a flopping body, and they take hold again, easing in, when the layers resume.
    /// </summary>
    private async Task TestSuspended()
    {
        foreach (var s in _subjects) Place(s.Body, s.Course.Flat, 0);
        await Frames(30);
        var held = new[] { PoseLayer.Gait, PoseLayer.Contact, PoseLayer.Life };
        foreach (var s in _subjects)
            foreach (var layer in held) s.Visual.Stack.Suspend(layer, true);
        await Frames(10);
        var released = _subjects.ToDictionary(s => s.Id, s => (Ik: s.Rig.LegSolver == null || !s.Rig.LegSolver.Active,
            Look: (s.Rig.Look.Head == null || !s.Rig.Look.Head.Modifier.Active) && s.Rig.Look.Eyes.All(e => !e.Modifier.Active)));
        foreach (var s in _subjects)
            foreach (var layer in held) s.Visual.Stack.Suspend(layer, false);
        await Frames(40);
        foreach (var s in _subjects)
        {
            var back = (s.Rig.LegSolver == null || (s.Rig.LegSolver.Active && s.Contact.IkInfluence > 0.99f)) && s.Look.Active > 0.99f &&
                (s.Rig.Look.Head == null || s.Rig.Look.Head.Modifier.Active);
            Check(released[s.Id].Ik && released[s.Id].Look && back,
                $"{s.Id}: suspended for a tumble, the leg IK and look-ats let go; resumed, they take hold again");
        }
    }

    /// <summary>
    /// The same scripted inputs, once on a bare capsule and once on a capsule wearing the character with contact installed:
    /// the capsule's path is identical, tick for tick (the drawn body never pushes it).
    /// </summary>
    private async Task TestCapsuleUnchanged()
    {
        // Park the suite's own bodies out of the way (they never collide with other bodies, but keep the courses clear).
        foreach (var s in _subjects) Place(s.Body, s.Course.Post + new Vector3(0.4f, 0, -0.4f), 0);
        await Frames(5);
        var bare = await RunScript(dressed: false);
        var dressed = await RunScript(dressed: true);
        foreach (var s in _subjects)
        {
            var a = bare[s.Id];
            var b = dressed[s.Id];
            var worst = 0f;
            for (var i = 0; i < Math.Min(a.Count, b.Count); i++) worst = Mathf.Max(worst, a[i].DistanceTo(b[i]));
            var travelled = 0f;
            for (var i = 1; i < a.Count; i++) travelled += a[i].DistanceTo(a[i - 1]) < 0.25f ? a[i].DistanceTo(a[i - 1]) : 0;
            Report(a.Count == b.Count && a.Count > 500 && worst == 0 && travelled > 1,
                FormattableString.Invariant($"{s.Id}: the capsule's path with the drawn body and contact is the bare capsule's, tick for tick ({a.Count} ticks, {travelled:0.00} m walked, stepped, jumped, climbed, swum; worst difference {worst:E1} m)"));
        }
    }

    private async Task<Dictionary<string, List<Vector3>>> RunScript(bool dressed)
    {
        var bodies = new Dictionary<string, SmallPlayerController>();
        var visuals = new List<CharacterVisual>();
        foreach (var s in _subjects)
        {
            var body = NewBody(s, (dressed ? "Dressed_" : "Bare_") + s.Id, s.Course.StepStart);
            if (dressed)
            {
                var visual = CharacterVisual.Create(s.Definition);
                body.SetBodyVisual(visual);
                CharacterContact.Install(visual, body, () => body.GlobalPosition + new Vector3(0.3f, 0.05f, 0));
                visuals.Add(visual);
            }
            bodies[s.Id] = body;
        }
        var paths = _subjects.ToDictionary(s => s.Id, _ => new List<Vector3>());
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        for (var f = 0; f < 600; f++)
        {
            foreach (var s in _subjects)
            {
                var body = bodies[s.Id];
                var course = s.Course;
                switch (f)
                {
                    case 0: body.TryTeleportTo(course.StepStart + Vector3.Up * 0.003f); body.Rotation = Vector3.Zero; body.SetControlInput(new Vector2(0, 1)); break;
                    case 50: body.SetControlInput(new Vector2(0, 1), jump: true); break;
                    case 70: body.SetControlInput(new Vector2(0.7f, 0.7f), sprint: true); break;
                    case 110: body.SetControlInput(Vector2.Zero); break;
                    case 130: body.TryTeleportTo(course.SlopeCentre + Vector3.Up * 0.003f); body.Rotation = new Vector3(0, Facing(Vector3.Right), 0); body.SetControlInput(new Vector2(0, 1)); break;
                    case 190: body.SetControlInput(new Vector2(0, -1)); break;
                    case 250:
                        body.SetControlInput(Vector2.Zero);
                        body.TryTeleportTo(course.Flat + Vector3.Up * 0.003f);
                        body.GlobalPosition = course.Flat + Vector3.Up * 0.4f;
                        body.Velocity = Vector3.Zero;
                        break;
                    case 320: body.TryTeleportTo(course.ClimbStart + Vector3.Up * 0.003f); body.Rotation = Vector3.Zero; body.SetControlInput(new Vector2(0, 1)); break;
                    case 450: body.TryTeleportTo(course.Shelf + Vector3.Up * 0.003f); body.Rotation = new Vector3(0, Mathf.Pi, 0); body.SetControlInput(new Vector2(0, 1)); break;
                    case 540: body.SetDiveInput(true, false); break;
                }
            }
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            foreach (var s in _subjects) paths[s.Id].Add(bodies[s.Id].GlobalPosition);
        }
        foreach (var body in bodies.Values) body.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        foreach (var visual in visuals)
            if (GodotObject.IsInstanceValid(visual.Root)) visual.Root.QueueFree();
        return paths;
    }
}
