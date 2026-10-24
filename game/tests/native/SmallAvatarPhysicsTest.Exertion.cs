using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Room;
using GarageGame.Native.Seams;

namespace GarageGame.Tests;

/// <summary>
/// The body's side of the stamina ledger (docs/runs/RUN-2-MECHANICS.md, packet M5; Seams/StaminaLedger.cs): what it reports each
/// tick, in what order, and what it does with the gate: the grip fails on a climb, deep water fades back to the shore it went in
/// from, the run is locked until the first bar refills. The ledger is the fixture's own arithmetic (contracts/examples/ledger/stamina_ledger.json), proved against
/// the fixture's ten cases first, so the body meets exactly the contract the kernel meets.
/// </summary>
public partial class SmallAvatarPhysicsTest
{
    /// <summary>A ledger that records what the body reports and holds whatever gate the test sets.</summary>
    private class ScriptedLedger : IStaminaLedger
    {
        public StaminaGate GateValue = StaminaGate.Free;
        public BodyExertion Last;
        public int Reports;
        public int Breaches;
        public double SprintTotal, DeepTotal, DiveTotal;
        public Action<BodyExertion>? OnReport;

        public virtual void Report(BodyExertion exertion)
        {
            Last = exertion;
            Reports++;
            SprintTotal += exertion.SprintS;
            DeepTotal += exertion.DeepWaterS;
            DiveTotal += exertion.DiveS;
            // At most one of the three, none below 0, none past a tick (0.05 s is the body's longest).
            var kinds = (exertion.SprintS > 0 ? 1 : 0) + (exertion.DeepWaterS > 0 ? 1 : 0) + (exertion.DiveS > 0 ? 1 : 0);
            if (kinds > 1 || exertion.SprintS < 0 || exertion.DeepWaterS < 0 || exertion.DiveS < 0 || Mathf.Max(exertion.SprintS, Mathf.Max(exertion.DeepWaterS, exertion.DiveS)) > 0.0501f) Breaches++;
            OnReport?.Invoke(exertion);
        }

        public StaminaGate Gate => GateValue;
    }

    /// <summary>The ledger's rule as the README and the fixture state it (the kernel's side is Kernel/CommandHostModes.cs).</summary>
    private sealed class ModelLedger : ScriptedLedger
    {
        /// <summary>FirstBar: the top row (0: the whole bar, a bar with one row). NoRunFraction: of it, refilled before the run comes back.</summary>
        public double Stamina, Max, FirstBar, Running = 0.02, Refill = 0.1, NoRunFraction = 1;
        public double? DeepRate = 0.02, DiveRate = 0.04;
        public bool NoRun;
        private BodyExertion _pending;

        public override void Report(BodyExertion exertion)
        {
            base.Report(exertion);
            _pending = exertion;
            Refresh();
        }

        public void Refresh() => GateValue = new StaminaGate(Stamina <= 1e-6, NoRun);

        /// <summary>The kernel's VitalsTick: drain what was reported (never below empty), refill on standable ground for the seconds not run.</summary>
        public void Tick(float seconds, bool safelyGrounded)
        {
            var e = _pending;
            _pending = BodyExertion.None;
            double Within(float s) => Math.Min(Math.Max(0, s), seconds);
            var drain = Within(e.SprintS) * Running + Within(e.DeepWaterS) * (DeepRate ?? 0) + Within(e.DiveS) * (DiveRate ?? 0);
            if (drain > 0)
            {
                Stamina = Math.Max(0, Stamina - drain);
                if (Stamina <= 0) NoRun = true;
            }
            var rest = Math.Max(0, seconds - Within(e.SprintS));
            if (safelyGrounded && Stamina < Max) Stamina = Math.Min(Max, Stamina + Refill * rest);
            if (NoRun && Stamina >= (FirstBar > 0 ? Math.Min(Max, FirstBar) : Max) * NoRunFraction - 1e-6 && Stamina > 0) NoRun = false;
            Refresh();
        }
    }

    private async Task TestExertion()
    {
        TestLedgerModelHoldsTheFixture();
        await TestReportsAndOrder();
        await TestEmptyBarStopsTheRun();
        await TestEmptyBarDropsAClimber();
        await TestStillWaterCostsNothing();
        await TestEmptyBarInDeepWater();
        await TestShoreReturnGoesWhereItWentIn();
        await TestShoreReturnToTheSeasBeach();
    }

    private static string LedgerFixturePath => ProjectSettings.GlobalizePath("res://") + "../contracts/examples/ledger/stamina_ledger.json";

    /// <summary>The stand-in ledger gives the fixture's answers in all ten cases: the body then meets the contract through it.</summary>
    private void TestLedgerModelHoldsTheFixture()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(LedgerFixturePath));
        var root = fixture.RootElement;
        var stamina = root.GetProperty("stamina");
        var tolerance = root.GetProperty("tolerance_m").GetDouble();
        var cases = root.GetProperty("cases").EnumerateArray().ToList();
        var held = 0;
        foreach (var item in cases)
        {
            var omit = item.TryGetProperty("omit", out var o) ? o.EnumerateArray().Select(x => x.GetString()!).ToHashSet() : new HashSet<string>();
            double? Rate(string name) => omit.Contains(name) ? null : stamina.GetProperty(name).GetDouble();
            var model = new ModelLedger
            {
                Max = item.GetProperty("max_stamina_m").GetDouble(), FirstBar = item.GetProperty("first_bar_m").GetDouble(),
                Stamina = item.GetProperty("stamina_m").GetDouble(), NoRun = item.GetProperty("no_run").GetBoolean(),
                Running = stamina.GetProperty("running_m_per_s").GetDouble(), Refill = stamina.GetProperty("refill_m_per_s").GetDouble(),
                NoRunFraction = stamina.GetProperty("empty_no_run_until_fraction").GetDouble(),
                DeepRate = Rate("deep_water_m_per_s"), DiveRate = Rate("dive_m_per_s"),
            };
            var ok = true;
            foreach (var step in item.GetProperty("steps").EnumerateArray())
            {
                model.Report(new BodyExertion((float)step.GetProperty("sprint_s").GetDouble(), (float)step.GetProperty("deep_water_s").GetDouble(), (float)step.GetProperty("dive_s").GetDouble()));
                model.Tick((float)step.GetProperty("seconds").GetDouble(), step.GetProperty("safely_grounded").GetBoolean());
                var expect = step.GetProperty("expect");
                var gate = model.Gate;
                ok &= Math.Abs(model.Stamina - expect.GetProperty("stamina_m").GetDouble()) <= tolerance + 1e-6 && gate.Empty == expect.GetProperty("empty").GetBoolean() &&
                      gate.NoRun == expect.GetProperty("no_run").GetBoolean();
            }
            if (ok) held++;
            else Check(false, "the stand-in ledger gives the fixture's answers: " + item.GetProperty("name").GetString());
        }
        Check(held == cases.Count && cases.Count == 10, $"the stand-in ledger gives the fixture's answers in all {cases.Count} cases, so the body meets the contract through it");
    }

    private const float Tick = 1f / 60;

    /// <summary>A fixture floor with a body standing on it, far from the other suites.</summary>
    private (Node3D Fixture, SmallPlayerController Body) ExertionFixture(Vector3 origin, float length = 6, string name = "ExertionFixture")
    {
        var fixture = new Node3D { Name = name, Position = origin };
        AddChild(fixture);
        var floor = new StaticBody3D { Position = new Vector3(0, -0.05f, 2 - length * 0.5f + 1), CollisionLayer = 1, CollisionMask = 0 };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(6, 0.1f, length) } });
        fixture.AddChild(floor);
        var body = new SmallPlayerController { Name = name + "Body", ReadKeyboard = false, Position = new Vector3(0, 0.003f, 2) };
        fixture.AddChild(body);
        return (fixture, body);
    }

    /// <summary>The body reports what it did once a tick, before the tick, and only what is true.</summary>
    private async Task TestReportsAndOrder()
    {
        var (fixture, body) = ExertionFixture(new Vector3(800, 0, 800));
        var wall = new StaticBody3D { Position = new Vector3(0, 0.2f, -1.5f), CollisionLayer = 1, CollisionMask = 0 };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.0f, 0.4f, 0.1f) } });
        fixture.AddChild(wall);
        await Frames(5);

        var ledger = new ScriptedLedger();
        var log = new List<string>();
        ledger.OnReport = _ => log.Add("R");
        body.VitalsTick = (_, _) => log.Add("T");
        body.StaminaLedger = ledger;
        await Frames(30);
        var inOrder = log.Count >= 50 && log.Count % 2 == 0;
        for (var i = 0; i + 1 < log.Count; i += 2) inOrder &= log[i] == "R" && log[i + 1] == "T";
        Check(inOrder && ledger.Reports >= 25 && ledger.Reports == log.Count / 2, $"the body reports once a physics tick, before VitalsTick: {log.Count / 2} ticks, each report then each tick");
        Check(ledger.Last.SprintS == 0 && ledger.Last.DeepWaterS == 0 && ledger.Last.DiveS == 0, "standing still costs nothing beyond climbing");

        // Sprint held, no step taken: no running to pay for.
        body.SetControlInput(Vector2.Zero, sprint: true);
        await Frames(30);
        Check(ledger.SprintTotal == 0, "holding the run key while standing still reports no running");

        // Running: a second of sprint seconds, whole ticks of them.
        body.SetControlInput(new Vector2(0, 1), sprint: true);
        await Frames(10);
        ledger.SprintTotal = 0;
        var before = ledger.Reports;
        var sprintTicks = 0;
        ledger.OnReport = e => { if (e.SprintS > 0) sprintTicks++; };
        await Frames(60);
        var ticks = ledger.Reports - before;
        Report(ticks == 60 && sprintTicks >= 58 && Mathf.Abs((float)ledger.SprintTotal - sprintTicks * Tick) < 0.002f && ledger.Last.SprintS <= 0.0501f,
            $"running reports its seconds, the whole tick each ({ledger.SprintTotal:0.000} s over {ticks} ticks, {sprintTicks} of them running)");
        Check(ledger.Breaches == 0 && ledger.DeepTotal == 0 && ledger.DiveTotal == 0, "never two kinds at once, never more than the tick, never a negative");

        // Walking is free.
        body.SetControlInput(new Vector2(0, 1), sprint: false);
        await Frames(20);
        ledger.SprintTotal = 0;
        await Frames(40);
        Check(ledger.SprintTotal == 0, "walking reports no running");

        // Pushing into a wall at a run goes nowhere, so it is not running (a climbable face would be grabbed: the body cannot climb here).
        body.CanClimb = false;
        body.SetControlInput(new Vector2(0, 1), sprint: true);
        for (var i = 0; i < 400 && !body.IsBlocked; i++) await Frames(1);
        await Frames(15);
        ledger.SprintTotal = 0;
        await Frames(30);
        Check(body.IsBlocked && ledger.SprintTotal == 0, $"running into a wall reports no running ({ledger.SprintTotal:0.000} s while blocked)");
        body.CanClimb = true;

        // A tumble holds control: nothing is exerted.
        body.TryTeleportTo(fixture.GlobalPosition + new Vector3(0, 0.003f, 2));
        await Frames(3);
        body.HoldControl(0.5f);
        body.SetControlInput(new Vector2(0, 1), sprint: true);
        ledger.SprintTotal = 0;
        await Frames(20);
        Check(body.ControlHeld && ledger.SprintTotal == 0, "a body whose control is held (a tumble) exerts nothing");
        body.ReleaseControl();
        body.SetControlInput(Vector2.Zero);

        // Taking the ledger off frees the body again; the gate it held is gone with it.
        ledger.GateValue = new StaminaGate(true, true);
        await Frames(3);
        Check(body.Gate.Empty && body.Gate.NoRun, "the body holds the gate the ledger gave after its tick");
        body.StaminaLedger = null;
        Check(body.Gate == StaminaGate.Free, "with no ledger bound, nothing is gated");
        var reports = ledger.Reports;
        await Frames(5);
        Check(ledger.Reports == reports, "an unbound ledger hears nothing more");

        // A floating body (the Gubble) neither exerts nor is gated.
        var companionLedger = new ScriptedLedger { GateValue = new StaminaGate(true, true) };
        _companion.StaminaLedger = companionLedger;
        _companion.VitalsTick = (_, _) => { };
        await Frames(20);
        Check(companionLedger.Reports == 0 && _companion.Floats && _companion.WalkSpeedNowMps == _companion.WalkSpeedMps,
            "a floating body reports nothing and is never slowed");
        _companion.StaminaLedger = null;
        _companion.VitalsTick = null;

        body.VitalsTick = null;
        fixture.QueueFree();
        await Frames(2);
    }

    /// <summary>
    /// The empty bar takes the run away, never the walk, until the first bar is full again (the founder, 15 October); the ledger
    /// meets the body through its own reports.
    /// </summary>
    private async Task TestEmptyBarStopsTheRun()
    {
        var (fixture, body) = ExertionFixture(new Vector3(800, 0, 900), 90, "EmptyBarFixture");
        await Frames(5);
        // The starting bar, a full one: 0.4 m, run down at 0.02 m a second.
        var ledger = new ModelLedger { Max = 0.4, Stamina = 0.4 };
        ledger.Refresh();
        body.VitalsTick = (dt, grounded) => ledger.Tick(dt, grounded);
        body.StaminaLedger = ledger;
        await Frames(3);

        // Paces measured on the floor: the walk, and Shift held.
        async Task<float> PaceFor(bool sprint, int frames = 40)
        {
            body.TryTeleportTo(fixture.GlobalPosition + new Vector3(0, 0.003f, 2));
            await Frames(2);
            body.SetControlInput(new Vector2(0, 1), sprint: sprint);
            await Frames(frames);
            var from = body.GlobalPosition;
            await Frames(20);
            var pace = PlanarDistance(body.GlobalPosition, from) * 3;
            body.SetControlInput(Vector2.Zero);
            await Frames(10);
            return pace;
        }
        var walk = await PaceFor(false);
        Report(Mathf.Abs(walk - 0.32f) < 0.01f && Mathf.IsEqualApprox(body.WalkSpeedNowMps, 0.32f), $"a full bar walks at the base pace ({walk:0.000} m/s)");

        // A full starting bar lasts 20 s of running (the fixture's first case, here by the body's own reports).
        ledger.Stamina = 0.4;
        ledger.NoRun = false;
        ledger.Refresh();
        body.TryTeleportTo(fixture.GlobalPosition + new Vector3(0, 0.003f, 2));
        await Frames(2);
        body.SetControlInput(new Vector2(0, 1), sprint: true);
        var frames = 0;
        for (; frames < 3600 && !ledger.Gate.Empty; frames++) await Frames(1);
        var seconds = frames / 60f;
        Report(seconds > 19.5f && seconds < 21.0f && ledger.NoRun && ledger.Stamina <= 1e-6,
            $"a full starting bar lasts about 20 s of running, by what the body reports ({seconds:0.0} s, {ledger.SprintTotal:0.0} s of it running)");

        // Empty: Shift held no longer runs. The body walks at its whole pace, reports no running, and the walk refills the bar.
        await Frames(15);
        var sprintBefore = ledger.SprintTotal;
        var heldFrom = body.GlobalPosition;
        await Frames(30);
        var heldPace = PlanarDistance(body.GlobalPosition, heldFrom) * 2;
        var state = body.LastBodyState;
        Report(Mathf.Abs(heldPace - 0.32f) < 0.02f && body.Gate.NoRun && ledger.SprintTotal == sprintBefore && ledger.Stamina > 0 &&
               Mathf.IsEqualApprox(state.WalkSpeedMps, 0.32f),
            $"an empty bar takes the run away, never the walk: Shift held walks at {heldPace:0.000} m/s, reports no running and refills ({ledger.Stamina:0.000} m)");

        // The run comes back when the first bar is full again: 0.4 m at 0.10 m a second, about 4 s after the running stopped.
        var walked = 45;
        for (; walked < 600 && ledger.NoRun; walked++) await Frames(1);
        Report(walked >= 225 && walked <= 255 && !ledger.NoRun && ledger.Stamina >= 0.4 - 1e-6,
            $"the run comes back when the first bar is full again ({walked / 60f:0.00} s after the running stopped)");
        var runAgain = await PaceFor(true);
        Report(Mathf.Abs(runAgain - 0.96f) < 0.03f && !ledger.NoRun, $"then Shift runs at the whole pace again ({runAgain:0.000} m/s)");

        // The lock takes the run, never the knack: Quick feet at full rank keeps its walk and its run's pace.
        body.SetControlInput(Vector2.Zero);
        body.Knacks = new BodyKnacks { WalkRunFactor = 1.2f };
        ledger.Stamina = 0;
        ledger.NoRun = true;
        ledger.Refresh();
        await Frames(2);
        Check(Mathf.IsEqualApprox(body.WalkSpeedNowMps, 0.32f * 1.2f) && Mathf.IsEqualApprox(body.RunSpeedNowMps, 0.96f * 1.2f) && body.Gate.NoRun,
            $"Quick feet with an empty bar: walk {body.WalkSpeedNowMps:0.000}, run {body.RunSpeedNowMps:0.000} m/s, the run held back by the gate");
        body.Knacks = BodyKnacks.None;

        body.VitalsTick = null;
        body.StaminaLedger = null;
        fixture.QueueFree();
        await Frames(2);
    }

    /// <summary>On a climb the grip fails when the bar is empty: the body falls from where it is, and does not grab again until the bar is back.</summary>
    private async Task TestEmptyBarDropsAClimber()
    {
        var (fixture, body) = ExertionFixture(new Vector3(800, 0, 1000), 6, "GripFixture");
        // A vertical face 60 cm tall, its west face 0.3 m east of the start.
        var face = new StaticBody3D { Position = new Vector3(0.45f, 0.3f, 2), CollisionLayer = 1, CollisionMask = 0 };
        face.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.3f, 0.6f, 0.6f) } });
        fixture.AddChild(face);
        body.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0);
        await Frames(5);
        var ledger = new ScriptedLedger();
        body.StaminaLedger = ledger;

        // A bar with something in it: grab, climb.
        body.SetControlInput(new Vector2(0, 1));
        for (var i = 0; i < 400 && !(body.IsClimbing && body.GlobalPosition.Y - fixture.GlobalPosition.Y > 0.1f); i++) await Frames(1);
        var held = body.GlobalPosition;
        var grabs = body.Grabs;
        Check(body.IsClimbing && body.GripFailures == 0, $"with the bar not empty the body grabs and climbs (at {held.Y - fixture.GlobalPosition.Y:0.000} m)");

        // The bar runs empty on the face: the very next tick the grip fails and the body falls from where it is.
        ledger.GateValue = new StaminaGate(true, true);
        var failures = body.GripFailures;
        await Frames(3);
        Check(!body.IsClimbing && body.GripFailures == failures + 1 && body.ClimbReleases > 0,
            $"an empty bar on a climb lets go ({body.GripFailures} grip failure, {body.ClimbReleases} releases)");
        var letGo = body.GlobalPosition;
        await Frames(10);
        var falling = body.GlobalPosition.Y < letGo.Y - 0.003f && PlanarDistance(body.GlobalPosition, held) < 0.03f;
        var landed = false;
        for (var i = 0; i < 200 && !landed; i++)
        {
            await Frames(1);
            landed = body.IsOnFloor();
        }
        Report(falling && landed && body.GlobalPosition.Y - fixture.GlobalPosition.Y < 0.006f && PlanarDistance(body.GlobalPosition, held) < 0.06f,
            $"the body falls from where it is (let go at {letGo.Y - fixture.GlobalPosition.Y:0.000} m, {PlanarDistance(body.GlobalPosition, held):0.000} m from the face, landed at {body.GlobalPosition.Y - fixture.GlobalPosition.Y:0.000} m)");

        // Still empty: pushing into the face grabs nothing.
        await Frames(30);
        grabs = body.Grabs;
        body.SetControlInput(new Vector2(0, 1));
        await Frames(120);
        Check(body.Grabs == grabs && !body.IsClimbing, "with the bar empty a push into the face does not grab it");

        // The bar is back: the same push climbs again.
        ledger.GateValue = StaminaGate.Free;
        for (var i = 0; i < 300 && !body.IsClimbing; i++) await Frames(1);
        Check(body.IsClimbing && body.Grabs == grabs + 1, "with the bar back, the same push grabs again");
        body.SetControlInput(Vector2.Zero);
        body.StaminaLedger = null;
        fixture.QueueFree();
        await Frames(2);
    }

    // ---- Out of stamina in deep water: the scene fades and the body is back on the shore where it went in (decision 33) ----

    private const float PondSurface = -0.02f;
    private const float PondBed = -0.2f;

    /// <summary>
    /// TestSwimming's pond, 18 cm deep at its deepest with its water 2 cm under the rim: a shelving 20 degree shore on the west and,
    /// width metres east of it, a vertical bank, or with eastRamp a second shelving shore. Returns its parts (to free) and the west
    /// shore's ramp and land.
    /// </summary>
    private (List<Node3D> Parts, StaticBody3D WestRamp, StaticBody3D WestLand) BuildPond(Vector3 b, float width, bool eastRamp)
    {
        var westLand = Solid(b + new Vector3(-1.0f, -0.125f, 0), new BoxShape3D { Size = new Vector3(2.0f, 0.25f, 3.0f) });
        var westRamp = Solid(b, Prism(0.5f, new Vector2(0, 0), new Vector2(0, PondBed), new Vector2(0.55f, PondBed)));
        var parts = new List<Node3D>
        {
            westLand, westRamp,
            Solid(b + new Vector3(width + 1.0f, -0.125f, 0), new BoxShape3D { Size = new Vector3(2.0f, 0.25f, 3.0f) }),
            Solid(b + new Vector3(width * 0.5f, -0.125f, -1.0f), new BoxShape3D { Size = new Vector3(width, 0.25f, 1.0f) }),
            Solid(b + new Vector3(width * 0.5f, -0.125f, 1.0f), new BoxShape3D { Size = new Vector3(width, 0.25f, 1.0f) }),
            Solid(b + new Vector3(width * 0.5f, -0.225f, 0), new BoxShape3D { Size = new Vector3(width, 0.05f, 1.0f) }),
        };
        if (eastRamp) parts.Add(Solid(b + new Vector3(width, 0, 0), Prism(0.5f, new Vector2(0, 0), new Vector2(0, PondBed), new Vector2(-0.55f, PondBed))));
        var sheet = new SurfaceTool();
        sheet.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var corner in new[] { new Vector2(0, -0.5f), new Vector2(width, -0.5f), new Vector2(width, 0.5f), new Vector2(0, -0.5f), new Vector2(width, 0.5f), new Vector2(0, 0.5f) })
            sheet.AddVertex(new Vector3(corner.X, PondSurface, corner.Y));
        var water = RoomWater.CreateCollider(new[] { ((Shape3D)sheet.Commit().CreateTrimeshShape(), Transform3D.Identity) });
        water.Position = b;
        AddChild(water);
        parts.Add(water);
        return (parts, westRamp, westLand);
    }

    private async Task FreeAll(IEnumerable<Node3D> parts)
    {
        foreach (var part in parts)
            if (GodotObject.IsInstanceValid(part)) part.QueueFree();
        await Frames(2);
    }

    /// <summary>
    /// Walk east from where the body stands until it swims at least toX metres into the pond; the last place it stood on dry ground
    /// on the way (the body's own rule: its feet a few millimetres above the water).
    /// </summary>
    private async Task<Vector3> WalkIntoThePond(SmallPlayerController body, Vector3 b, float toX)
    {
        var lastDry = body.GlobalPosition;
        body.SetControlInput(new Vector2(0, 1));
        for (var i = 0; i < 1500 && !(body.IsSwimming && body.GlobalPosition.X - b.X >= toX); i++)
        {
            await Frames(1);
            if (body.IsOnFloor() && !body.IsSwimming && (!body.Water.Wet || body.Water.Under(body.GlobalPosition) <= -SmallPlayerController.ShoreEntryClearM)) lastDry = body.GlobalPosition;
        }
        body.SetControlInput(Vector2.Zero);
        return lastDry;
    }

    /// <summary>One shore return, frame by frame: when the fade began, how dark and how long, how far the swimmer moved on the way, and where it was put.</summary>
    private sealed class ShoreTrip
    {
        public int FadeBeganAfter = -1;
        public int FadeFrames;
        public float Darkest;
        public float Drift;
        public float Deepest;
        public Vector3? Arrival;
        public double DeepSecondsAfter, SprintSecondsAfter, DiveSecondsAfter;
        /// <summary>The longest wall-clock time between two frames of the trip: the arrival frame holds the search for a shore.</summary>
        public double WorstFrameMs;
        public Vector3 At => Arrival ?? new Vector3(float.NaN, float.NaN, float.NaN);
    }

    /// <summary>
    /// Run the bar empty under a swimmer and watch the fade until it has ended. onFade runs once, on the first frame of the fade (the
    /// body has seen the gate by then, as it does in play, where the bar runs empty in the body's own tick).
    /// </summary>
    private async Task<ShoreTrip> WatchShoreReturn(SmallPlayerController body, ScriptedLedger ledger, Action? onFade = null, int maxFrames = 300)
    {
        var trip = new ShoreTrip();
        Vector3? movedFrom = null;
        void Arrived(SmallPlayerController who) => trip.Arrival = who.GlobalPosition;
        body.WentHome += Arrived;
        var (deep, sprint, dive) = (ledger.DeepTotal, ledger.SprintTotal, ledger.DiveTotal);
        ledger.GateValue = new StaminaGate(true, true);
        var last = Time.GetTicksUsec();
        for (var frame = 0; frame < maxFrames; frame++)
        {
            await Frames(1);
            var now = Time.GetTicksUsec();
            trip.WorstFrameMs = Math.Max(trip.WorstFrameMs, (now - last) / 1000.0);
            last = now;
            if (body.GoingHome)
            {
                if (trip.FadeBeganAfter < 0)
                {
                    trip.FadeBeganAfter = frame;
                    onFade?.Invoke();
                }
                trip.FadeFrames++;
                trip.Darkest = Mathf.Max(trip.Darkest, body.HomeFade);
                movedFrom ??= body.GlobalPosition;
                // How far the swimmer moves with the fade on, up to the moment it is put down: a drift would show here.
                if (trip.Arrival == null) trip.Drift = Mathf.Max(trip.Drift, PlanarDistance(body.GlobalPosition, movedFrom.Value));
            }
            if (trip.Arrival == null) trip.Deepest = Mathf.Max(trip.Deepest, body.Water.SurfaceY - body.GlobalPosition.Y);
            if (trip.FadeBeganAfter >= 0 && !body.GoingHome) break;
        }
        body.WentHome -= Arrived;
        (trip.DeepSecondsAfter, trip.SprintSecondsAfter, trip.DiveSecondsAfter) = (ledger.DeepTotal - deep, ledger.SprintTotal - sprint, ledger.DiveTotal - dive);
        return trip;
    }

    private static bool StandsDry(SmallPlayerController body) => body.IsOnFloor() && !body.IsSwimming && (!body.Water.Wet || body.Water.Under(body.GlobalPosition) <= 0);

    /// <summary>
    /// Still costs nothing in the water (decision 64 of 16 October, the first recorded play: "If I stop while swimming or climbing I
    /// continue to lose stamina"): a body floating still in deep water for ten seconds reports no deep-water seconds and keeps its bar;
    /// the same body swimming reports the tick's seconds as before, and the bar pays the pack's 0.02 m a second. Under water the same
    /// two ways: a diver let go, drifting up, reports none; Ctrl held, the tick's seconds at 0.04 m a second. The ledger is the
    /// fixture's own arithmetic (ModelLedger), so the bar is the one the kernel would keep.
    /// </summary>
    private async Task TestStillWaterCostsNothing()
    {
        var b = new Vector3(800, 0, 1400);
        var (parts, _, _) = BuildPond(b, 3.0f, eastRamp: false);
        var east = new Vector3(0, -Mathf.Pi * 0.5f, 0);
        var floatY = PondSurface - 0.06f;
        var body = new SmallPlayerController { Name = "StillWaterBody", ReadKeyboard = false, Position = b + new Vector3(1.0f, floatY, 0), Rotation = east };
        AddChild(body);
        await Frames(40);
        var ledger = new ModelLedger { Max = 0.4, Stamina = 0.4 };
        ledger.Refresh();
        body.VitalsTick = (dt, grounded) => ledger.Tick(dt, grounded);
        body.StaminaLedger = ledger;
        await Frames(5);
        Check(body.IsSwimming && !body.IsDiving && Mathf.Abs(body.GlobalPosition.Y - floatY) < 0.01f, "a swimmer afloat in the deep middle of a 3 m pond, no key held");

        // Ten seconds floating still: no deep-water seconds, the bar kept.
        var reports = ledger.Reports;
        ledger.DeepTotal = ledger.DiveTotal = ledger.SprintTotal = 0;
        var bar = ledger.Stamina;
        var from = body.GlobalPosition;
        await Frames(600);
        Report(ledger.Reports == reports + 600 && ledger.DeepTotal == 0 && ledger.DiveTotal == 0 && ledger.SprintTotal == 0 && ledger.Stamina == bar && body.IsSwimming &&
               PlanarDistance(body.GlobalPosition, from) < 0.01f,
            $"floating still in deep water for ten seconds reports no seconds ({ledger.DeepTotal:0.000} s over {ledger.Reports - reports} ticks) and keeps its bar ({ledger.Stamina:0.000} m of {bar:0.000})");

        // The run key alone moves nothing: still afloat, still free.
        body.SetControlInput(Vector2.Zero, sprint: true);
        await Frames(60);
        body.SetControlInput(Vector2.Zero);
        Check(ledger.DeepTotal == 0 && ledger.Stamina == bar, "holding the run key alone while afloat reports nothing");

        // Swimming: every tick a key moves the body is a deep-water second, as before, and the bar pays the pack's rate.
        body.SetControlInput(new Vector2(0, 1));
        await Frames(10);
        ledger.DeepTotal = 0;
        bar = ledger.Stamina;
        var stroked = 0;
        ledger.OnReport = e => { if (e.DeepWaterS > 0) stroked++; };
        from = body.GlobalPosition;
        await Frames(60);
        ledger.OnReport = null;
        body.SetControlInput(Vector2.Zero);
        Report(body.IsSwimming && stroked == 60 && Mathf.Abs((float)ledger.DeepTotal - 1.0f) < 0.002f && Mathf.Abs((float)(bar - ledger.Stamina) - 0.02f) < 0.0005f &&
               PlanarDistance(body.GlobalPosition, from) > 0.1f && ledger.Breaches == 0,
            $"swimming reports the tick's seconds as before ({ledger.DeepTotal:0.000} s over a second, {stroked} of 60 ticks) and the bar pays 0.02 m a second ({bar - ledger.Stamina:0.0000} m)");

        // Stopping stops the cost at once: the glide that is left is the water's, not a stroke.
        await Frames(2);
        ledger.DeepTotal = 0;
        await Frames(120);
        Check(ledger.DeepTotal == 0, $"letting go of the key stops the cost at once, while the swimmer still glides ({ledger.DeepTotal:0.000} s)");

        // A leap out of the water is a key's motion too: its tick is paid.
        await Frames(60);
        ledger.DeepTotal = 0;
        var jumps = body.JumpsStarted;
        for (var i = 0; i < 60 && body.JumpsStarted == jumps; i++)
        {
            body.SetControlInput(Vector2.Zero, jump: true);
            await Frames(1);
        }
        Check(body.JumpsStarted == jumps + 1 && ledger.DeepTotal > 0 && ledger.DeepTotal < 3 * Tick + 1e-4,
            $"a leap from the water pays its tick as a stroke ({ledger.DeepTotal:0.000} s)");
        for (var i = 0; i < 180 && !(body.IsSwimming && Mathf.Abs(body.GlobalPosition.Y - floatY) < 0.01f && body.Velocity.Length() < 0.01f); i++) await Frames(1);

        // Under water: Ctrl held is a stroke under the surface, its seconds as diving at the pack's 0.04 m a second ...
        body.SetDiveInput(true, false);
        for (var i = 0; i < 200 && !body.IsDiving; i++) await Frames(1);
        await Frames(5);
        ledger.DeepTotal = ledger.DiveTotal = 0;
        bar = ledger.Stamina;
        await Frames(30);
        var diveSeconds = ledger.DiveTotal;
        var diveCost = bar - ledger.Stamina;
        Report(body.IsDiving && Mathf.Abs((float)diveSeconds - 0.5f) < 0.002f && ledger.DeepTotal == 0 && Mathf.Abs((float)diveCost - 0.02f) < 0.0005f,
            $"diving with Ctrl held reports the tick's seconds under water ({diveSeconds:0.000} s over half a second, {diveCost:0.0000} m of bar)");
        // ... and a diver let go drifts up for nothing, as long as it stays under.
        await Frames(60);
        body.SetDiveInput(false, false);
        await Frames(1);
        ledger.DeepTotal = ledger.DiveTotal = 0;
        bar = ledger.Stamina;
        var underTicks = 0;
        ledger.OnReport = _ => { if (body.IsDiving) underTicks++; };
        await Frames(90);
        ledger.OnReport = null;
        Report(underTicks >= 60 && ledger.DiveTotal == 0 && ledger.DeepTotal == 0 && ledger.Stamina == bar,
            $"a diver let go, drifting up under the water, reports nothing ({underTicks} ticks under, {ledger.DiveTotal:0.000} s) and keeps its bar");
        // Under water with a movement key: a stroke again.
        body.SetDiveInput(true, false);
        for (var i = 0; i < 60 && !body.IsDiving; i++) await Frames(1);
        await Frames(40);
        body.SetDiveInput(false, false);
        body.SetControlInput(new Vector2(0, 1));
        await Frames(2);
        ledger.DiveTotal = 0;
        var swumUnder = 0;
        ledger.OnReport = e => { if (body.IsDiving && e.DiveS > 0) swumUnder++; };
        await Frames(20);
        ledger.OnReport = null;
        body.SetControlInput(Vector2.Zero);
        Check(swumUnder >= 15 && ledger.Breaches == 0, $"under water a movement key is a stroke too ({swumUnder} of 20 ticks paid as diving)");

        body.VitalsTick = null;
        body.StaminaLedger = null;
        body.QueueFree();
        await FreeAll(parts);
    }

    /// <summary>
    /// In deep water an empty bar fades the scene and puts the body back on the shore where it went in: standing, dry, still, unhurt, the
    /// Gubble beside it, and no drift on the way. A diver is not held down. A body with no entry (put into the water) goes to the
    /// nearest shore a swimmer could stand on, never up a bank.
    /// </summary>
    private async Task TestEmptyBarInDeepWater()
    {
        var b = new Vector3(800, 0, 1100);
        var (parts, _, _) = BuildPond(b, 1.2f, eastRamp: false);
        var east = new Vector3(0, -Mathf.Pi * 0.5f, 0);
        var body = new SmallPlayerController { Name = "ShoreBody", ReadKeyboard = false, Position = b + new Vector3(-1.2f, 0.003f, 0), Rotation = east };
        AddChild(body);
        await Frames(5);

        var ledger = new ScriptedLedger();
        body.StaminaLedger = ledger;
        var landing = 0f;
        body.FallObserved = (speed, _) => landing = Mathf.Max(landing, speed);
        var floatY = PondSurface - body.SwimFloatDepthM;
        void Place(Vector3 at)
        {
            body.GlobalPosition = at;
            body.Velocity = Vector3.Zero;
            body.SetControlInput(Vector2.Zero);
        }

        // Diving reports its own seconds, never deep water's (a stroke's cost, and floating still, are TestStillWaterCostsNothing's).
        Place(b + new Vector3(0.9f, floatY, 0));
        await Frames(40);
        Check(body.IsSwimming && !body.IsDiving, "the swimmer floats at the surface of the deep end");
        body.SetDiveInput(true, false);
        for (var i = 0; i < 200 && !body.IsDiving; i++) await Frames(1);
        await Frames(20);
        ledger.DeepTotal = ledger.DiveTotal = 0;
        await Frames(30);
        Report(body.IsDiving && Mathf.Abs((float)ledger.DiveTotal - 0.5f) < 0.03f && ledger.DeepTotal == 0 && ledger.Breaches == 0,
            $"diving reports its seconds as under water, not as deep water ({ledger.DiveTotal:0.000} s over half a second)");
        body.SetDiveInput(false, false);

        // The Gubble follows the swimmer in, floating over the water.
        var gubble = new CompanionAvatar { Name = "ShoreGubble", Position = b + new Vector3(-1.0f, 0.01f, 0.3f) };
        gubble.ConfigureIdentity("shore_gubble");
        AddChild(gubble);
        await Frames(5);
        gubble.BindPlayer(body);

        // Back on the west shore, walking in: what it remembers is the last dry ground on the way, not where it began.
        Check(body.TryTeleportTo(b + new Vector3(-1.2f, 0.003f, 0)) && body.ShoreEntry == null, "put down on the west shore, the body has not yet walked in from anywhere");
        body.Rotation = east;
        await Frames(5);
        Check(body.ShoreEntry is { } stood && PlanarDistance(stood, b + new Vector3(-1.2f, 0, 0)) < 0.01f, "standing there, it remembers where it stands");
        gubble.Follow();
        var lastDry = await WalkIntoThePond(body, b, 0.9f);
        var entry = body.ShoreEntry;
        var edgeX = lastDry.X - b.X;
        Report(entry is { } e && PlanarDistance(e, lastDry) < 0.02f && edgeX > 0.0f && edgeX < 0.1f,
            $"what it remembers is the last dry ground on the way in, at the water's edge ({edgeX:0.000} m into the pond), not the wading shallows or where it began ({(entry is { } r ? r.X - b.X : float.NaN):0.000} m)");
        await Frames(40);
        Check(body.IsSwimming && !body.IsDiving && body.GlobalPosition.X - b.X >= 0.88f, "the swimmer floats at the surface of the deep end, most of a metre from where it went in");

        // The bar runs empty, the push forward and the jump key still held: the scene fades, the controls are held, nothing drifts.
        landing = 0;
        var grabs = body.Grabs;
        var jumps = body.JumpsStarted;
        var trips = body.HomeTrips;
        var trip = await WatchShoreReturn(body, ledger, onFade: () => body.SetControlInput(new Vector2(0, 1), jump: true));
        body.SetControlInput(Vector2.Zero);
        var arrival = trip.At;
        var dryNow = StandsDry(body);
        var inlandFacing = (-body.GlobalBasis.Z).Dot(Vector3.Left);
        Report(trip.FadeBeganAfter >= 0 && trip.FadeBeganAfter <= 3 && trip.Darkest > 0.95f && Mathf.Abs(trip.FadeFrames - 72) <= 3,
            $"an empty bar in deep water fades the scene at once: dark {trip.Darkest:0.00}, begun after {trip.FadeBeganAfter} frames, {trip.FadeFrames / 60f:0.00} s in all (0.6 s out, 0.6 s in)");
        Report(trip.Drift < 0.03f && body.Grabs == grabs && body.JumpsStarted == jumps && trip.DeepSecondsAfter < 3 * Tick + 1e-4 && trip.SprintSecondsAfter == 0 && trip.DiveSecondsAfter == 0,
            $"the controls are held and nothing drifts ({trip.Drift * 100:0.0} cm moved with the fade on, pushing forward and jumping; {trip.DeepSecondsAfter:0.000} s more of deep water reported)");
        Report(PlanarDistance(arrival, lastDry) < 0.03f && body.ShoreReturns == 1 && body.LastShoreReturn == "entry" && body.LastHome == "shore" && body.HomeTrips == trips,
            $"the body is put down where it went in ({PlanarDistance(arrival, lastDry) * 100:0.0} cm from the last dry ground, {(arrival.X - b.X):0.000} m into the pond; return: {body.LastShoreReturn}, trips home {body.HomeTrips - trips})");
        await Frames(30);
        Report(dryNow && StandsDry(body) && body.Velocity.Length() < 0.05f && body.LandingCost(landing, false) == 0 && inlandFacing > 0.9f && body.ShoreReturns == 1,
            $"standing, dry, still and unhurt, facing away from the water (landing {landing * 1000:0.0} mm/s costs {body.LandingCost(landing, false):0} health; facing {inlandFacing:0.00}; returns {body.ShoreReturns})");
        Report(body.Gate.Empty && body.Gate.NoRun && trip.Arrival != null && PlanarDistance(body.GlobalPosition, arrival) < 0.03f,
            "the bar's own rules are the ledger's: on the shore the gate still reads empty and no run, and the body stays put without a second trip");
        Report(gubble.WentHomeWithPlayer == 1 && PlanarDistance(gubble.GlobalPosition, body.GlobalPosition) < 0.6f,
            $"the Gubble came too, to {PlanarDistance(gubble.GlobalPosition, body.GlobalPosition):0.00} m from the player ({gubble.WentHomeWithPlayer} time)");
        gubble.Stop();

        // Under water, the dive key held: a diver is not held down, and is taken back to where it went in just the same.
        ledger.GateValue = StaminaGate.Free;
        landing = 0;
        Place(b + new Vector3(0.9f, floatY, 0));
        body.SetDiveInput(true, false);
        for (var i = 0; i < 300 && !body.IsDiving; i++) await Frames(1);
        await Frames(20);
        var diving = body.IsDiving;
        var depth = body.Water.SurfaceY - body.GlobalPosition.Y;
        trips = body.HomeTrips;
        trip = await WatchShoreReturn(body, ledger);
        body.SetDiveInput(false, false);
        arrival = trip.At;
        await Frames(30);
        Report(diving && trip.Deepest < depth + 0.01f && trip.FadeBeganAfter <= 3 && PlanarDistance(arrival, lastDry) < 0.03f && body.ShoreReturns == 2 && body.LastShoreReturn == "entry" && StandsDry(body) && !body.IsDiving && body.HomeTrips == trips,
            $"an empty bar under water, the dive key held, does not hold the diver down ({depth:0.000} m deep to {trip.Deepest:0.000} m at most) and takes it back to the same shore ({PlanarDistance(arrival, lastDry) * 100:0.0} cm), standing dry");

        // Put into the water (a teleport), it has no entry: the nearest shore a swimmer could stand on, dry ground inland of it.
        ledger.GateValue = StaminaGate.Free;
        Check(body.TryTeleportTo(b + new Vector3(0.9f, 0, 0)), "the body is put down in the deep end");
        await Frames(40);
        Check(body.IsSwimming && body.ShoreEntry == null, "swimming, it has no shore to go back to");
        trips = body.HomeTrips;
        trip = await WatchShoreReturn(body, ledger);
        arrival = trip.At;
        await Frames(30);
        Report(body.LastShoreReturn == "nearest" && body.ShoreReturns == 3 && arrival.X - b.X > -0.05f && arrival.X - b.X < 0.12f && StandsDry(body) && body.HomeTrips == trips,
            $"with no entry it is put on the nearest shore, the shelving one, on dry ground ({arrival.X - b.X:0.000} m into the pond; return: {body.LastShoreReturn})");

        // Beside the vertical bank, still west: a bank is no shore, so never up it.
        ledger.GateValue = StaminaGate.Free;
        Check(body.TryTeleportTo(b + new Vector3(1.12f, 0, 0.1f)), "the body is put down beside the steep east bank");
        await Frames(40);
        grabs = body.Grabs;
        Check(body.IsSwimming && body.ShoreEntry == null, "swimming beside the bank, with no shore to go back to");
        trip = await WatchShoreReturn(body, ledger);
        arrival = trip.At;
        await Frames(30);
        Report(body.LastShoreReturn == "nearest" && arrival.X - b.X < 0.12f && body.Grabs == grabs && StandsDry(body),
            $"beside the vertical bank the swimmer is taken to the shelving shore, not up the bank ({arrival.X - b.X:0.000} m into the pond, grabs {body.Grabs - grabs})");

        // The bar back, the same body swims where it likes.
        ledger.GateValue = StaminaGate.Free;
        Place(b + new Vector3(0.9f, floatY, 0));
        await Frames(30);
        var anchored = body.GlobalPosition;
        var returns = body.ShoreReturns;
        body.SetControlInput(new Vector2(0, 1));
        await Frames(60);
        body.SetControlInput(Vector2.Zero);
        Check(PlanarDistance(body.GlobalPosition, anchored) > 0.1f && body.ShoreReturns == returns, "with the bar back the swimmer swims where it likes again, and no one takes it home");

        gubble.QueueFree();
        body.StaminaLedger = null;
        body.FallObserved = null;
        body.QueueFree();
        await FreeAll(parts);
    }

    /// <summary>
    /// A pond with a shelving shore at each end: it went in from the west and runs dry 10 cm off the east shore, and it is taken back to
    /// the west, where it went in, not to the nearer one. When that ground is gone it is taken to the nearer one.
    /// </summary>
    private async Task TestShoreReturnGoesWhereItWentIn()
    {
        var b = new Vector3(800, 0, 1200);
        var (parts, westRamp, westLand) = BuildPond(b, 1.6f, eastRamp: true);
        var body = new SmallPlayerController { Name = "TwoShoreBody", ReadKeyboard = false, Position = b + new Vector3(-1.2f, 0.003f, 0), Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0) };
        AddChild(body);
        await Frames(5);
        var ledger = new ScriptedLedger();
        body.StaminaLedger = ledger;

        var lastDry = await WalkIntoThePond(body, b, 1.25f);
        await Frames(30);
        var swimmer = body.GlobalPosition;
        var entry = body.ShoreEntry;
        Check(body.IsSwimming && entry is { } e && PlanarDistance(e, lastDry) < 0.02f && swimmer.X - b.X > 1.2f,
            $"it went in from the west and swims 35 cm from the east shore ({swimmer.X - b.X:0.00} m into a 1.6 m pond)");
        var trip = await WatchShoreReturn(body, ledger);
        var arrival = trip.At;
        await Frames(30);
        Report(body.LastShoreReturn == "entry" && PlanarDistance(arrival, lastDry) < 0.03f && arrival.X - b.X < 0.12f && StandsDry(body),
            $"close to the east shore, an empty swimmer is taken back to the west one where it went in ({arrival.X - b.X:0.000} m), not to the nearer one (1.2 m off against 0.1 m)");

        // Walk in again, and the ground it went in from is taken away: the nearer shore, then, on its dry ground.
        ledger.GateValue = StaminaGate.Free;
        await Frames(5);
        body.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0);
        lastDry = await WalkIntoThePond(body, b, 1.25f);
        await Frames(30);
        Check(body.IsSwimming && body.ShoreEntry is { } again && PlanarDistance(again, lastDry) < 0.02f, "it walked in from the west again");
        await FreeAll(new Node3D[] { westRamp, westLand });
        trip = await WatchShoreReturn(body, ledger);
        arrival = trip.At;
        await Frames(30);
        Report(body.LastShoreReturn == "nearest" && arrival.X - b.X > 1.45f && StandsDry(body),
            $"when the ground it went in from is gone it is taken to the nearest shore, on dry ground ({arrival.X - b.X:0.000} m into the pond; return: {body.LastShoreReturn})");

        body.StaminaLedger = null;
        body.QueueFree();
        await FreeAll(parts);
    }

    /// <summary>With no shore within 12 m and no entry, the sea's nearest beach.</summary>
    private async Task TestShoreReturnToTheSeasBeach()
    {
        var c = new Vector3(800, 0, 1300);
        const float surface = -0.02f;
        var island = Solid(c + new Vector3(0, -0.05f, 0), new BoxShape3D { Size = new Vector3(1.2f, 0.1f, 1.2f) });
        Vector2[] Square(float half) => new[] { new Vector2(c.X - half, c.Z - half), new Vector2(c.X + half, c.Z - half), new Vector2(c.X + half, c.Z + half), new Vector2(c.X - half, c.Z + half) };
        var beach = new RoomSea.Beach("far_beach", c + new Vector3(0, 0.001f, 0.3f), 0, c + new Vector3(0, surface, 0.7f));
        var sea = new RoomSea(surface, Square(0.5f), Square(1.2f), Square(2.0f), new[] { beach });
        var body = new SmallPlayerController { Name = "OpenSeaBody", ReadKeyboard = false, Position = c + new Vector3(14.0f, surface - 0.06f, 0) };
        AddChild(body);
        body.SetSea(sea);
        await Frames(40);
        var ledger = new ScriptedLedger();
        body.StaminaLedger = ledger;
        Check(body.IsSwimming && body.ShoreEntry == null && sea.Beaches.Count == 1, "a swimmer out in the open sea, 14 m from the island, never having stood anywhere");
        var trip = await WatchShoreReturn(body, ledger);
        var arrival = trip.At;
        await Frames(30);
        Report(body.LastShoreReturn == "beach" && body.LastHome == "shore" && PlanarDistance(arrival, beach.WashAshoreM) < 0.15f && StandsDry(body) && body.HomeTrips == 0,
            $"with no entry and no shore within 12 m it is put on the sea's nearest beach, standing dry ({PlanarDistance(arrival, beach.WashAshoreM):0.000} m from it; return: {body.LastShoreReturn}); the frame that held the whole search (1152 places) took {trip.WorstFrameMs:0.0} ms");
        Check(trip.WorstFrameMs < 250, $"the search for a shore, once and in the dark, is no hitch to speak of ({trip.WorstFrameMs:0.0} ms the longest frame)");

        body.StaminaLedger = null;
        body.QueueFree();
        await FreeAll(new Node3D[] { island });
    }
}
