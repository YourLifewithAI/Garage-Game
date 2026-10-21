using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Characters;
using GarageGame.Native.Kernel;
using GarageGame.Native.Room;
using GarageGame.Native.Seams;

namespace GarageGame.Tests;

/// <summary>
/// The knacks and the body (docs/runs/RUN-2-MECHANICS.md, packet M5; docs/game/MECHANICS.md section 7, decisions 9 and 22). Every
/// knack of the shipped pack (storybook_wild v5), every rank, the stacked pairs the generator must size its shortcuts against, and
/// every costume a player may wear (the four drawn characters and the prototype figure), diffed on the four numbers a gate is
/// sized by: reach, hop distance, dive length and climbable height. Then the guard this round exists to keep (decision 4 of the
/// traits draft, reopened by the knacks): no knack, no rank and no costume moves the collision capsule, the size, the eye height or
/// the reach, and none moves a number it does not name. The suite fails on the first knack that does.
/// Headless, --fixed-fps 60. Run: res://tests/native_body_knacks.tscn.
/// </summary>
public partial class BodyKnackTest : Node3D
{
    private const float Dt = 1f / 60;

    /// <summary>The numbers a body is made of, for one setting of knacks on one costume.</summary>
    private readonly record struct Numbers(float Walk, float Run, float Apex, float ApexNow, float Airtime, float HopDistance, float Swim, float DiveSpeed, float DiveLength,
        float ClimbM, float SafeDropM, float HealthPerQuarter, float StaminaBarM, float Reach, float Height, float Radius, float Eye, float ShapeHeight, float ShapeRadius, float Step, string Profile);

    private int _checks;
    private int _failures;
    private int _serial;
    private CharacterLibrary _library = null!;
    private IslandRules _pack = null!;
    private IslandRules _watered = null!;
    private KnacksRules _table = null!;

    /// <summary>A costume: the prototype figure, or a drawn character the player may wear (never the Gubble, which floats).</summary>
    private readonly record struct Costume(string Name, CharacterDefinition? Definition);

    public override async void _Ready()
    {
        try
        {
            // "-- --boot-room --knack=quick_feet:3 ...": the real game's boot only, headless (the runners run this second).
            if (OS.GetCmdlineUserArgs().Contains("--boot-room"))
            {
                await TestBootReachesTheBody();
                GD.Print($"NATIVE_BODY_KNACKS_BOOT: {_checks - _failures}/{_checks} checks passed; a real headless boot of the room with --knack= arguments: the host holds the ranks, the pack's table resolves them, the body wears them and walks at their pace; not the feel");
                GetTree().Quit(_failures == 0 ? 0 : 1);
                return;
            }
            _library = CharacterLibrary.Load(CharacterLibrary.ShippedRoot);
            var path = ProjectSettings.GlobalizePath(IslandRules.PathFor("storybook_wild", 5));
            _pack = IslandRules.Parse(File.ReadAllBytes(path));
            _table = _pack.Knacks ?? throw new InvalidOperationException("storybook_wild v5 has no knacks block");
            // v5 sets no deep-water or dive drain (the founder's to set), so a dive's length needs rates: the ledger fixture's own.
            var node = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
            node["traits"]!["stamina"]!["deep_water_m_per_s"] = 0.02;
            node["traits"]!["stamina"]!["dive_m_per_s"] = 0.04;
            _watered = IslandRules.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()));

            TestPackReading();
            TestBootArguments();
            TestTheTypeHasNoRoomForAGeometryKnack();
            var costumes = Costumes();
            await TestEveryKnackEveryCostume(costumes);
            await TestHopsAreWhatTheyAdd(costumes);
            await TestSwimsAreWhatTheyAdd();
            await TestClimbsAreWhatTheyAdd();
            await TestLandingsAreWhatTheyAdd();
            await TestGubbleHasNone();
            GD.Print($"NATIVE_BODY_KNACKS: {_checks - _failures}/{_checks} checks passed; every knack, rank and costume diffed on reach, hop distance, dive length and climbable height; the capsule, size, eye height and reach never move; the hop, the swim, the climb and the landing measured on real physics; not the feel");
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
        catch (Exception exception)
        {
            GD.PushError("Native body knack test exception: " + exception);
            GetTree().Quit(1);
        }
    }

    private void Check(bool condition, string label)
    {
        _checks++;
        if (condition) return;
        _failures++;
        GD.Print("FAIL " + label);
    }

    private void Report(bool condition, string label)
    {
        GD.Print("BODY_KNACKS_MEASURED " + label);
        Check(condition, label);
    }

    private Task Frames(int count)
    {
        var source = new TaskCompletionSource();
        var remaining = count;
        void Step()
        {
            if (--remaining > 0) return;
            GetTree().PhysicsFrame -= Step;
            source.SetResult();
        }
        GetTree().PhysicsFrame += Step;
        return source.Task;
    }

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    // ---- the settings ----

    /// <summary>A setting of knacks: a name for the table, and the ranks a team holds.</summary>
    private sealed record Setting(string Name, string? Knack, int Rank, IReadOnlyDictionary<string, int> Ranks);

    /// <summary>Nothing, then every knack of the pack at every one of its ranks, then the stacks a generator sizes a shortcut against.</summary>
    private IReadOnlyList<Setting> Settings()
    {
        var settings = new List<Setting> { new("none", null, 0, new Dictionary<string, int>()) };
        foreach (var knack in _table.Table)
            for (var rank = 1; rank <= knack.Ranks.Count; rank++)
                settings.Add(new Setting($"{knack.Knack}:{rank}", knack.Knack, rank, new Dictionary<string, int> { [knack.Knack] = rank }));
        // Three slots stack: the strongest pairs and a trio. The margin of a shortcut is sized against these, not one knack.
        void Stack(params (string Knack, int Rank)[] picks) =>
            settings.Add(new Setting(string.Join("+", picks.Select(p => $"{p.Knack}:{p.Rank}")), null, 0, picks.ToDictionary(p => p.Knack, p => p.Rank)));
        Stack(("quick_feet", 3), ("high_hop", 3));
        Stack(("big_bar", 3), ("steady_climber", 3));
        Stack(("strong_swimmer", 3), ("long_breath", 3));
        Stack(("quick_feet", 3), ("high_hop", 3), ("cat_feet", 3));
        Stack(("big_bar", 3), ("steady_climber", 3), ("long_breath", 3));
        return settings;
    }

    private IReadOnlyList<Costume> Costumes()
    {
        var costumes = new List<Costume> { new("prototype", null) };
        foreach (var definition in _library.Characters)
            if (definition.Motion != MotionKind.Float) costumes.Add(new Costume(definition.Id, definition));
        return costumes;
    }

    // ---- the pack, read ----

    private void TestPackReading()
    {
        Check(_table.Table.Count == 11 && _table.Table.Count(k => k.Available) == 8, $"the pack has the design's eleven knacks, eight of them with a verb ({_table.Table.Count} knacks)");
        Check(BodyKnacks.None.IsNone && BodyKnacks.FromPack(null, null).IsNone && BodyKnacks.FromPack(_table, new Dictionary<string, int>()).IsNone &&
              BodyKnacks.None.SafeDropM == PlayerVitals.SafeFallHeight && BodyKnacks.None.WalkRunFactor == 1 && BodyKnacks.None.HopFactor == 1 && BodyKnacks.None.SwimFactor == 1,
            "no knack is today's body: every factor 1, the safe drop the vitals' 0.30 m");
        BodyKnacks Of(params (string, int)[] picks) => BodyKnacks.FromPack(_table, picks.ToDictionary(p => p.Item1, p => p.Item2));
        Check(Math.Abs(Of(("quick_feet", 1)).WalkRunFactor - 1.10f) < 1e-6 && Math.Abs(Of(("quick_feet", 2)).WalkRunFactor - 1.15f) < 1e-6 && Math.Abs(Of(("quick_feet", 3)).WalkRunFactor - 1.20f) < 1e-6,
            "Quick feet is 10, 15 and 20 percent on walk and run");
        Check(Math.Abs(Of(("high_hop", 3)).HopFactor - 1.20f) < 1e-6 && Math.Abs(Of(("strong_swimmer", 3)).SwimFactor - 1.20f) < 1e-6, "High hop and Strong swimmer are 20 percent at full rank");
        Check(Of(("cat_feet", 1)).SafeDropM == 0.45f && Of(("cat_feet", 2)).SafeDropM == 0.60f && Of(("cat_feet", 3)).SafeDropM == 0.75f,
            "Cat feet is 0.45, 0.60 and 0.75 m, in metres, from 0.30");
        Check(Of(("quick_feet", 9)).WalkRunFactor == Of(("quick_feet", 3)).WalkRunFactor, "a rank past the pack's last is its last");
        Check(Of(("quick_feet", 0)).IsNone && Of(("quick_feet", -2)).IsNone && Of(("nonesuch", 3)).IsNone, "rank 0, a negative rank and a knack the pack lacks count for nothing");
        Check(Of(("handy", 3), ("green_thumb", 3), ("strong_arm", 3)).IsNone, "a knack without a verb yet (Handy, Green thumb, Strong arm) moves nothing on the body");
        Check(Of(("big_bar", 3), ("steady_climber", 3), ("long_breath", 3), ("big_heart", 2)).IsNone, "the knacks that move the vitals (Big bar, Steady climber, Long breath, Big heart) are the kernel's: the body's numbers do not move");
        var both = Of(("quick_feet", 3), ("high_hop", 3), ("strong_swimmer", 2), ("cat_feet", 2));
        Check(Math.Abs(both.WalkRunFactor - 1.2f) < 1e-6 && Math.Abs(both.HopFactor - 1.2f) < 1e-6 && Math.Abs(both.SwimFactor - 1.15f) < 1e-6 && both.SafeDropM == 0.6f && !both.IsNone,
            "knacks that move different numbers all apply");
        // The same row read twice (a pack with two knacks on one number) multiplies, as the kernel's rules do; the highest safe drop wins.
        var twin = new KnacksRules(_table.Slots, _table.Table.Append(new KnackRule("quick_feet_too", "walk_run_speed", "factor", new[] { 1.1, 1.15, 1.2 }, true))
            .Append(new KnackRule("cat_feet_too", "safe_drop", "m", new[] { 0.40, 0.5 }, true)).ToList());
        var stacked = BodyKnacks.FromPack(twin, new Dictionary<string, int> { ["quick_feet"] = 3, ["quick_feet_too"] = 3, ["cat_feet"] = 1, ["cat_feet_too"] = 2 });
        Check(Math.Abs(stacked.WalkRunFactor - 1.44f) < 1e-5 && stacked.SafeDropM == 0.5f, "two knacks on one number multiply; the highest safe drop wins");
        var broken = new KnacksRules(_table.Slots, new List<KnackRule> { new("quick_feet", "walk_run_speed", "factor", new[] { double.NaN }, true), new("cat_feet", "safe_drop", "m", new[] { 0.1 }, true) });
        var refused = BodyKnacks.FromPack(broken, new Dictionary<string, int> { ["quick_feet"] = 1, ["cat_feet"] = 1 });
        Check(refused.IsNone, "a rank that is not a number, and a safe drop below the vitals' own, give the body nothing");
        // The soft landing the kernel and the tumble share.
        var cat = Of(("cat_feet", 3));
        Check(Math.Abs(cat.SafeLandingSpeedMps - MathF.Sqrt(2 * 3.5f * 0.75f)) < 1e-4f && Math.Abs(BodyKnacks.None.SafeLandingSpeedMps - PlayerVitals.SafeLandingSpeedMps) < 1e-6f,
            $"the safe landing speed is a free fall through the safe drop under the vitals' gravity ({cat.SafeLandingSpeedMps:0.000} m/s through 0.75 m)");
        Check(BodyKnacks.None.FallDamage(3.24f, false) == PlayerVitals.FallDamage(3.24f, false) && cat.FallDamage(cat.SafeLandingSpeedMps - 0.01f, false) == 0 &&
              Math.Abs(cat.FallDamage(cat.SafeLandingSpeedMps + 1.0f, false) - PlayerVitals.FallDamagePerMps) < 1e-3f && cat.FallDamage(9, true) == 0 && cat.FallDamage(float.NaN, false) == 0 && cat.FallDamage(-1, false) == 0,
            "fall damage with a safe drop is the vitals' own curve, started later: nothing below it, the same slope above it, nothing into deep water");
    }

    private void TestBootArguments()
    {
        Dictionary<string, int> Parse(params string[] arguments) => BodyKnacks.FromArguments(arguments);
        Check(Parse().Count == 0 && Parse("--mode=challenge", "--team=a").Count == 0 && BodyKnacks.FromArguments(null).Count == 0, "no knack argument is no knack: today's body");
        var one = Parse("--knack=quick_feet:3");
        Check(one.Count == 1 && one["quick_feet"] == 3, "--knack=quick_feet:3 is Quick feet at rank 3");
        Check(Parse("--knack=cat_feet")["cat_feet"] == 1, "a rank left off is rank 1");
        var listed = Parse("--knacks=quick_feet:2, high_hop:3", "--knack=cat_feet:1");
        Check(listed.Count == 3 && listed["quick_feet"] == 2 && listed["high_hop"] == 3 && listed["cat_feet"] == 1, "knacks are listed, or repeated, up to the three slots");
        Check(Parse("--knacks=a:1,b:1,c:1,d:1").Count == 3, "no more than three");
        Check(Parse("--knack=quick_feet:0").Count == 0 && Parse("--knack=quick_feet:4").Count == 0 && Parse("--knack=quick_feet:x").Count == 0 && Parse("--knack=quick_feet:-1").Count == 0 &&
              Parse("--knack=Quick_Feet:1").Count == 0 && Parse("--knack=../x:1").Count == 0 && Parse("--knack=:2").Count == 0 && Parse("--knack=").Count == 0 && Parse("--knack").Count == 0,
            "a malformed argument gives no knack, never a guess");
        var host = BodyKnacks.FromPack(_table, Parse("--knack=quick_feet:3", "--knack=cat_feet:2"));
        Check(Math.Abs(host.WalkRunFactor - 1.2f) < 1e-6 && host.SafeDropM == 0.6f, "boot ranks resolve against the island's pack");
    }

    /// <summary>
    /// The structural guard: what a knack can reach is what BodyKnacks names. If a field for the capsule, the size, the eye or the
    /// reach is ever added, this fails first and asks for the decision (decision 4 of the traits draft; MECHANICS section 7).
    /// </summary>
    private void TestTheTypeHasNoRoomForAGeometryKnack()
    {
        var names = typeof(BodyKnacks).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(p => p.Name).Where(n => n != "EqualityContract").OrderBy(n => n, StringComparer.Ordinal).ToList();
        var expected = new[] { "HopFactor", "IsNone", "SafeDropM", "SafeLandingCreditMps", "SafeLandingSpeedMps", "SwimFactor", "WalkRunFactor" }.OrderBy(n => n, StringComparer.Ordinal).ToList();
        Check(names.SequenceEqual(expected), "a knack on the body can move the walk and run, the hop, the swim and the safe drop, and nothing else: " + string.Join(", ", names));
    }

    // ---- the body, measured ----

    private StaticBody3D Floor(Vector3 origin, float width, float length)
    {
        var floor = new StaticBody3D { Name = $"Floor{_serial++}", Position = origin + new Vector3(0, -0.05f, -length * 0.5f + 2), CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(width, 0.1f, length) } });
        AddChild(floor);
        return floor;
    }

    /// <summary>A body in a costume, dressed as the room dresses the player (the gaits, contact and the tumble, then SetBodyVisual).</summary>
    private SmallPlayerController Wear(Costume costume, Vector3 position)
    {
        var body = new SmallPlayerController { Name = $"Wearer{_serial++}_{costume.Name}", ReadKeyboard = false, Position = position };
        if (costume.Definition != null)
        {
            var visual = CharacterVisual.Create(costume.Definition);
            CharacterMotion.Install(visual, body);
            body.SetBodyVisual(visual);
        }
        AddChild(body);
        return body;
    }

    private static string ProfileOf(SmallPlayerController body) =>
        body.BodyVisual is CharacterVisual visual ? visual.Body.Profile.Describe(visual.Body.Bones) : "prototype";

    /// <summary>Every number a body is made of and a gate is sized by, for the knacks it wears and the pack that reads them.</summary>
    private Numbers Measure(SmallPlayerController body, IReadOnlyDictionary<string, int> ranks)
    {
        body.Knacks = BodyKnacks.FromPack(_table, ranks);
        var rules = VitalsRules.For(_watered, "challenge", "standard", ranks);
        var fresh = rules.Fresh();
        var bar = rules.MaxStaminaOf(fresh);
        var diveDrain = rules.Drain(new BodyExertion(0, 0, 1));
        var diveSeconds = diveDrain > 0 ? bar / diveDrain : 0;
        var shape = body.GetNode<CollisionShape3D>("SmallBodyCollision").Shape as CapsuleShape3D ?? throw new InvalidOperationException("the body has no capsule");
        return new Numbers(body.WalkSpeedNowMps, body.RunSpeedNowMps, body.HopApexM, body.JumpApexNowM, body.JumpAirtimeS, body.RunSpeedNowMps * body.JumpAirtimeS, body.SwimSpeedNowMps, body.DiveSpeedNowMps,
            body.DiveSpeedNowMps * diveSeconds, bar * (float)(rules.ClimbMPerBarM * rules.ClimbFactor), body.Knacks.SafeDropM, (float)rules.HealthPerQuarter, bar, body.ReachM, body.BodyHeightM, body.BodyRadiusM,
            body.EyeCamera.Position.Y, shape.Height, shape.Radius, body.StepHeightM, ProfileOf(body));
    }

    private static bool Same(float a, float b) => Math.Abs(a - b) <= 1e-5f * Math.Max(1, Math.Abs(a));

    /// <summary>
    /// The diff: for each setting, on each costume, the body's numbers against the same costume's with no knack. The geometry (capsule,
    /// size, eye, reach, step and the drawn character's motion profile) must be identical to the bit; every other number may differ only
    /// where the knack names it, by exactly the rank's factor; and the same setting on every costume gives the same numbers.
    /// </summary>
    private async Task TestEveryKnackEveryCostume(IReadOnlyList<Costume> costumes)
    {
        var settings = Settings();
        Floor(new Vector3(0, 0, 0), 4, 4);
        var bodies = new List<(Costume Costume, SmallPlayerController Body)>();
        for (var i = 0; i < costumes.Count; i++) bodies.Add((costumes[i], Wear(costumes[i], new Vector3(0.4f * i - 0.8f, 0.003f, 0))));
        await Frames(10);
        var baseline = bodies.ToDictionary(b => b.Costume.Name, b => Measure(b.Body, new Dictionary<string, int>()));

        // The base body, to the numbers the design gives it.
        var none = baseline["prototype"];
        Check(Same(none.Walk, 0.32f) && Same(none.Run, 0.96f) && Same(none.Apex, 0.065f) && Same(none.Reach, 0.15f) && Same(none.Height, 0.10f) && Same(none.Radius, 0.02f) && Same(none.Eye, 0.087f) &&
              Same(none.SafeDropM, 0.30f) && Same(none.StaminaBarM, 0.4f) && Same(none.ClimbM, 0.6f) && Same(none.HealthPerQuarter, 5f),
            Inv($"with no knack the body is the design's base: walk {none.Walk} and run {none.Run} m/s, hop {none.Apex * 100:0.0} cm, reach {none.Reach} m, a 0.4 m bar climbs {none.ClimbM} m (1.5 m a metre of bar, decision 65), 0.30 m safe drop, a quarter of {none.HealthPerQuarter} health"));
        Report(none.HopDistance > 0.33f && none.HopDistance < 0.40f, Inv($"the base running hop is about 0.36 m ({none.HopDistance:0.000} m)"));
        Check(baseline.Values.All(n => n.Reach == none.Reach && n.Height == none.Height && n.Radius == none.Radius && n.Eye == none.Eye && n.ShapeHeight == none.ShapeHeight && n.ShapeRadius == none.ShapeRadius &&
                                       n.Step == none.Step && Same(n.HopDistance, none.HopDistance) && Same(n.DiveLength, none.DiveLength) && Same(n.ClimbM, none.ClimbM)),
            "with no knack every costume has the same capsule, size, eye, reach, hop distance, dive length and climbable height: " + string.Join(", ", baseline.Keys));
        Check(none.ShapeHeight == 0.10f && none.ShapeRadius == 0.02f, "the capsule is 10 cm tall and 2 cm round, under every costume");

        // Which numbers each knack may move, and by how much (the rank's factor, in the direction the knack names).
        var moved = 0;
        var table = new List<string>();
        foreach (var setting in settings.Where(s => s.Name != "none"))
        {
            Numbers? first = null;
            var geometry = true;
            var costumeNames = new List<string>();
            foreach (var (costume, body) in bodies)
            {
                var n = Measure(body, setting.Ranks);
                var b = baseline[costume.Name];
                // The geometry, to the bit: capsule, size, eye, reach, the step, and the drawn character's own profile.
                var geometryHolds = n.Reach == b.Reach && n.Height == b.Height && n.Radius == b.Radius && n.Eye == b.Eye && n.ShapeHeight == b.ShapeHeight && n.ShapeRadius == b.ShapeRadius &&
                                    n.Step == b.Step && n.Profile == b.Profile;
                geometry &= geometryHolds;
                if (!geometryHolds) Check(false, $"{setting.Name} on {costume.Name} moves the capsule, the size, the eye height, the reach or the character's motion profile");
                // The same setting on every costume gives the same numbers.
                if (first is { } f)
                    Check(Same(n.Walk, f.Walk) && Same(n.Run, f.Run) && Same(n.Apex, f.Apex) && Same(n.HopDistance, f.HopDistance) && Same(n.Swim, f.Swim) && Same(n.DiveLength, f.DiveLength) &&
                          Same(n.ClimbM, f.ClimbM) && Same(n.SafeDropM, f.SafeDropM) && Same(n.HealthPerQuarter, f.HealthPerQuarter),
                        $"{setting.Name} on {costume.Name} gives the numbers it gives on {costumes[0].Name}");
                first ??= n;
                costumeNames.Add(costume.Name);
            }
            Check(geometry, $"{setting.Name} leaves the capsule, size, eye height, reach and motion profile as they were on all {bodies.Count} costumes");
            var m = first!.Value;
            var b0 = baseline["prototype"];
            var movedNames = new List<string>();
            if (!Same(m.Walk, b0.Walk) || !Same(m.Run, b0.Run)) movedNames.Add("speed");
            if (!Same(m.Apex, b0.Apex)) movedNames.Add("hop apex");
            if (!Same(m.Swim, b0.Swim)) movedNames.Add("swim");
            if (!Same(m.DiveLength, b0.DiveLength)) movedNames.Add("dive length");
            if (!Same(m.ClimbM, b0.ClimbM)) movedNames.Add("climbable height");
            if (!Same(m.SafeDropM, b0.SafeDropM)) movedNames.Add("safe drop");
            if (!Same(m.HealthPerQuarter, b0.HealthPerQuarter)) movedNames.Add("health per quarter");
            if (!Same(m.HopDistance, b0.HopDistance)) movedNames.Add("hop distance");
            table.Add(Inv($"KNACK_TABLE {setting.Name,-34} reach {m.Reach:0.000} m | hop {m.HopDistance:0.000} m (apex {m.Apex * 100:0.00} cm) | dive {m.DiveLength:0.000} m | climb {m.ClimbM:0.000} m | walk {m.Walk:0.000} run {m.Run:0.000} swim {m.Swim:0.000} | safe drop {m.SafeDropM:0.00} m | quarter {m.HealthPerQuarter:0.0}"));
            if (setting.Knack is { } id)
            {
                moved++;
                var knack = _table.Knack(id)!;
                var factor = (float)knack.Ranks[setting.Rank - 1];
                string[] allowed = id switch
                {
                    "quick_feet" => new[] { "speed", "hop distance" },
                    "high_hop" => new[] { "hop apex", "hop distance" },
                    "big_bar" => new[] { "dive length", "climbable height" },
                    "steady_climber" => new[] { "climbable height" },
                    "strong_swimmer" => new[] { "swim", "dive length" },
                    "long_breath" => new[] { "dive length" },
                    "big_heart" => new[] { "health per quarter" },
                    "cat_feet" => new[] { "safe drop" },
                    _ => Array.Empty<string>(),
                };
                Check(movedNames.All(allowed.Contains), $"{setting.Name} moves {(movedNames.Count == 0 ? "nothing" : string.Join(", ", movedNames))}, and may move only {(allowed.Length == 0 ? "nothing" : string.Join(", ", allowed))}");
                var ratio = id switch
                {
                    "quick_feet" => movedNames.Contains("speed") && Same(m.Walk, b0.Walk * factor) && Same(m.Run, b0.Run * factor) && Same(m.HopDistance, b0.HopDistance * factor),
                    "high_hop" => movedNames.Contains("hop apex") && Same(m.Apex, b0.Apex * factor) && Math.Abs(m.ApexNow / b0.ApexNow - factor) < 0.02f && Math.Abs(m.HopDistance / b0.HopDistance - MathF.Sqrt(factor)) < 0.02f,
                    "big_bar" => Same(m.StaminaBarM, b0.StaminaBarM * factor) && Same(m.ClimbM, b0.ClimbM * factor) && Same(m.DiveLength, b0.DiveLength * factor),
                    "steady_climber" => Same(m.ClimbM, b0.ClimbM * factor) && Same(m.StaminaBarM, b0.StaminaBarM),
                    "strong_swimmer" => Same(m.Swim, b0.Swim * factor) && Same(m.DiveSpeed, b0.DiveSpeed * factor) && Same(m.DiveLength, b0.DiveLength * factor),
                    "long_breath" => Same(m.DiveLength, b0.DiveLength / factor) && Same(m.DiveSpeed, b0.DiveSpeed),
                    "big_heart" => Same(m.HealthPerQuarter, b0.HealthPerQuarter * factor),
                    "cat_feet" => Same(m.SafeDropM, (float)knack.Ranks[setting.Rank - 1]),
                    _ => !knack.Available && movedNames.Count == 0,
                };
                Check(ratio, Inv($"{setting.Name} moves its one number by its rank ({factor}) and the numbers that follow from it"));
            }
            else
            {
                // A stack moves the numbers its knacks name, by the product of their ranks, and nothing else.
                Check(m.Reach == b0.Reach && m.Eye == b0.Eye && m.Height == b0.Height && m.Radius == b0.Radius, $"the stack {setting.Name} leaves the geometry alone");
                if (setting.Ranks.ContainsKey("quick_feet") && setting.Ranks.ContainsKey("high_hop"))
                    Check(Math.Abs(m.HopDistance / b0.HopDistance - 1.2 * Math.Sqrt(1.2)) < 0.02, Inv($"Quick feet and High hop at full rank stack on the hop: {m.HopDistance / b0.HopDistance:0.000} times the base (1.2 x sqrt 1.2)"));
                if (setting.Ranks.ContainsKey("big_bar") && setting.Ranks.ContainsKey("steady_climber"))
                    Check(Same(m.ClimbM, b0.ClimbM * 1.2f * 1.2f), Inv($"Big bar and Steady climber at full rank stack on the climb: {m.ClimbM:0.000} m (0.4 x 1.5 x 1.2 x 1.2)"));
                if (setting.Ranks.ContainsKey("strong_swimmer") && setting.Ranks.ContainsKey("long_breath"))
                    Check(Same(m.DiveLength, b0.DiveLength * 1.2f / 0.8f), Inv($"Strong swimmer and Long breath at full rank stack on the dive: {m.DiveLength:0.000} m ({m.DiveLength / b0.DiveLength:0.00} times the base)"));
            }
        }
        foreach (var line in table) GD.Print(line);
        Check(moved == _table.Table.Sum(k => k.Ranks.Count), $"every rank of every knack was diffed on all {bodies.Count} costumes ({moved} ranks, {settings.Count - 1 - moved} stacks)");
        var largest = settings.Where(s => s.Name != "none").Select(s => Measure(bodies[0].Body, s.Ranks)).Max(n => n.Reach);
        Check(largest == none.Reach, "no setting, alone or stacked, reaches farther than 15 cm");
        foreach (var (_, body) in bodies) body.Knacks = BodyKnacks.None;
        foreach (var (_, body) in bodies) body.QueueFree();
        await Frames(2);
    }

    // ---- real physics: the hop ----

    /// <summary>Runs up to a steady pace, jumps, and returns how far it went through the air and how long it was up (ticks).</summary>
    private async Task<(float Distance, int Ticks, float Pace)> RunAndHop(SmallPlayerController body)
    {
        body.SetControlInput(new Vector2(0, 1), sprint: true);
        await Frames(75);
        var pace = new Vector2(body.Velocity.X, body.Velocity.Z).Length();
        body.SetControlInput(new Vector2(0, 1), jump: true, sprint: true);
        Vector3? takeoff = null;
        var ticks = 0;
        var landed = Vector3.Zero;
        for (var i = 0; i < 120; i++)
        {
            await Frames(1);
            body.SetControlInput(new Vector2(0, 1), sprint: true);
            if (!body.IsOnFloor())
            {
                takeoff ??= body.GlobalPosition;
                ticks++;
            }
            else if (takeoff != null)
            {
                landed = body.GlobalPosition;
                break;
            }
        }
        body.SetControlInput(Vector2.Zero);
        await Frames(25);
        return takeoff is { } from ? (new Vector2(landed.X - from.X, landed.Z - from.Z).Length(), ticks, pace) : (0, 0, pace);
    }

    private async Task TestHopsAreWhatTheyAdd(IReadOnlyList<Costume> costumes)
    {
        var origin = new Vector3(0, 0, -40);
        Floor(origin, 4, 40);
        var rows = new List<string>();
        var distances = new Dictionary<string, float>();
        var airborne = new Dictionary<string, int>();
        foreach (var (name, ranks) in new (string, Dictionary<string, int>)[]
                 {
                     ("none", new()), ("quick_feet:3", new() { ["quick_feet"] = 3 }), ("high_hop:3", new() { ["high_hop"] = 3 }),
                     ("quick_feet:3+high_hop:3", new() { ["quick_feet"] = 3, ["high_hop"] = 3 }),
                 })
        {
            foreach (var costume in costumes)
            {
                var body = Wear(costume, origin + new Vector3(0, 0.003f, 0));
                await Frames(8);
                body.Knacks = BodyKnacks.FromPack(_table, ranks);
                var (distance, ticks, pace) = await RunAndHop(body);
                var predicted = body.RunSpeedNowMps * body.JumpAirtimeS;
                distances[$"{name}/{costume.Name}"] = distance;
                airborne[$"{name}/{costume.Name}"] = ticks;
                if (costume.Name == "prototype")
                    rows.Add(Inv($"{name}: ran at {pace:0.000} m/s (the body's run is {body.RunSpeedNowMps:0.000}), hopped {distance:0.000} m in {ticks} ticks (predicted {predicted:0.000} m, {body.JumpAirtimeS * 60:0.0} ticks)"));
                Check(Math.Abs(pace - body.RunSpeedNowMps) < 0.03f, Inv($"{name}/{costume.Name}: the run reaches the body's own run speed ({pace:0.000} against {body.RunSpeedNowMps:0.000} m/s)"));
                Check(Math.Abs(distance - predicted) < 0.06f * predicted + 0.01f, Inv($"{name}/{costume.Name}: a running hop goes as far as the run speed times the air time ({distance:0.000} against {predicted:0.000} m)"));
                body.Knacks = BodyKnacks.None;
                body.QueueFree();
                await Frames(2);
            }
        }
        foreach (var row in rows) GD.Print("BODY_KNACKS_MEASURED hop " + row);
        // Costumes change nothing about the hop: the same ticks in the air, the same distance to a tick's run.
        foreach (var name in new[] { "none", "quick_feet:3", "high_hop:3", "quick_feet:3+high_hop:3" })
        {
            var proto = distances[$"{name}/prototype"];
            var protoTicks = airborne[$"{name}/prototype"];
            var same = costumes.All(c => Math.Abs(distances[$"{name}/{c.Name}"] - proto) < 0.025f && Math.Abs(airborne[$"{name}/{c.Name}"] - protoTicks) <= 1);
            Check(same, $"{name}: every costume hops the same ({string.Join(", ", costumes.Select(c => Inv($"{c.Name} {distances[$"{name}/{c.Name}"]:0.000} m")))})");
        }
        var baseHop = distances["none/prototype"];
        Report(distances["quick_feet:3/prototype"] > baseHop * 1.12f && distances["quick_feet:3/prototype"] < baseHop * 1.28f,
            Inv($"Quick feet at full rank lengthens the running hop from {baseHop:0.000} m to {distances["quick_feet:3/prototype"]:0.000} m (about +20 percent)"));
        Report(distances["high_hop:3/prototype"] > baseHop * 1.04f && distances["high_hop:3/prototype"] < baseHop * 1.16f,
            Inv($"High hop at full rank lengthens it to {distances["high_hop:3/prototype"]:0.000} m (airtime grows with the root of the apex)"));
        Check(distances["quick_feet:3+high_hop:3/prototype"] > distances["quick_feet:3/prototype"] && distances["quick_feet:3+high_hop:3/prototype"] > distances["high_hop:3/prototype"],
            Inv($"the two stack ({distances["quick_feet:3+high_hop:3/prototype"]:0.000} m)"));

        // The apex itself, on the body, in the room's gravity and in a lighter one (the cap rises with the knack).
        var origin2 = new Vector3(0, 0, 40);
        Floor(origin2, 4, 8);
        var jumper = Wear(costumes[0], origin2 + new Vector3(0, 0.003f, 0));
        await Frames(8);
        async Task<float> Apex()
        {
            await Frames(5);
            var y0 = jumper.GlobalPosition.Y;
            var top = y0;
            jumper.SetControlInput(Vector2.Zero, jump: true);
            var left = false;
            for (var i = 0; i < 200; i++)
            {
                await Frames(1);
                top = Mathf.Max(top, jumper.GlobalPosition.Y);
                if (!jumper.IsOnFloor()) left = true;
                else if (left) break;
            }
            await Frames(8);
            return top - y0;
        }
        var apexBase = await Apex();
        jumper.Knacks = BodyKnacks.FromPack(_table, new Dictionary<string, int> { ["high_hop"] = 3 });
        var apexHigh = await Apex();
        Report(Math.Abs(apexBase - 0.065f) < 0.006f && Math.Abs(apexHigh / apexBase - 1.2f) < 0.06f && Math.Abs(apexHigh - 0.078f) < 0.007f,
            Inv($"the hop's apex is 6.5 cm and, with High hop at full rank, 7.8 cm ({apexBase * 100:0.00} cm to {apexHigh * 100:0.00} cm)"));
        Check(jumper.SetWorldPhysics(GD.Load<GDScript>(SmallPlayerController.WorldPhysicsScript).Call("preset", "room_floaty", jumper.WorldPhysicsRevision + 1).AsGodotDictionary()), "the floaty gravity is accepted");
        var floatyHigh = await Apex();
        jumper.Knacks = BodyKnacks.None;
        var floatyBase = await Apex();
        Report(Math.Abs(floatyBase - jumper.MaxJumpApexM) < 0.02f && Math.Abs(floatyHigh / floatyBase - 1.2f) < 0.06f,
            Inv($"in floaty gravity the cap on the leap rises with the knack too: {floatyBase * 100:0.0} cm to {floatyHigh * 100:0.0} cm"));
        jumper.QueueFree();
        await Frames(2);
    }

    // ---- real physics: the swim and the dive ----

    private async Task TestSwimsAreWhatTheyAdd()
    {
        var b = new Vector3(100, 0, 0);
        const float surface = -0.02f;
        void Solid(Vector3 position, Vector3 size)
        {
            var body = new StaticBody3D { Position = b + position, CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            AddChild(body);
        }
        // A pond 8 m long (x from 0 to 8), 3 m wide and 18 cm deep, banked all round.
        Solid(new Vector3(4, -0.225f, 0), new Vector3(8, 0.05f, 3));
        Solid(new Vector3(-2, -0.125f, 0), new Vector3(4, 0.25f, 5));
        Solid(new Vector3(10, -0.125f, 0), new Vector3(4, 0.25f, 5));
        Solid(new Vector3(4, -0.125f, -2), new Vector3(8, 0.25f, 1));
        Solid(new Vector3(4, -0.125f, 2), new Vector3(8, 0.25f, 1));
        var sheet = new SurfaceTool();
        sheet.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var corner in new[] { new Vector2(0, -1.5f), new Vector2(8, -1.5f), new Vector2(8, 1.5f), new Vector2(0, -1.5f), new Vector2(8, 1.5f), new Vector2(0, 1.5f) })
            sheet.AddVertex(new Vector3(corner.X, surface, corner.Y));
        var water = RoomWater.CreateCollider(new[] { ((Shape3D)sheet.Commit().CreateTrimeshShape(), Transform3D.Identity) });
        water.Position = b;
        AddChild(water);
        var floatY = surface - 0.06f;
        await Frames(3);

        async Task<(float Surface, float Run, float Dive)> Speeds(IReadOnlyDictionary<string, int> ranks)
        {
            var body = Wear(new Costume("prototype", null), b + new Vector3(0.2f, floatY, 0));
            body.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0);
            await Frames(20);
            body.Knacks = BodyKnacks.FromPack(_table, ranks);
            body.GlobalPosition = b + new Vector3(0.2f, floatY, 0);
            body.Velocity = Vector3.Zero;
            await Frames(30);
            async Task<float> Pace(bool sprint, bool dive)
            {
                body.GlobalPosition = new Vector3(b.X + 0.2f, dive ? body.GlobalPosition.Y : floatY, b.Z);
                body.Velocity = Vector3.Zero;
                body.SetDiveInput(dive, false);
                body.SetControlInput(new Vector2(0, 1), sprint: sprint);
                await Frames(dive ? 50 : 45);
                var from = body.GlobalPosition;
                await Frames(30);
                var pace = new Vector2(body.GlobalPosition.X - from.X, body.GlobalPosition.Z - from.Z).Length() * 2;
                body.SetControlInput(Vector2.Zero);
                body.SetDiveInput(false, false);
                return pace;
            }
            var surfacePace = await Pace(false, false);
            var surfaceSwimming = body.IsSwimming;
            var runPace = await Pace(true, false);
            var divePace = await Pace(false, true);
            Check(surfaceSwimming, "the swimmer swims in the pond");
            body.Knacks = BodyKnacks.None;
            body.QueueFree();
            await Frames(3);
            return (surfacePace, runPace, divePace);
        }
        var baseline = await Speeds(new Dictionary<string, int>());
        var strong = await Speeds(new Dictionary<string, int> { ["strong_swimmer"] = 3 });
        var quick = await Speeds(new Dictionary<string, int> { ["quick_feet"] = 3 });
        Report(Math.Abs(baseline.Surface - 0.192f) < 0.012f && Math.Abs(baseline.Run - 0.32f) < 0.02f, Inv($"the base swim is 0.19 m/s, and 0.32 with the run key ({baseline.Surface:0.000}, {baseline.Run:0.000} m/s)"));
        Report(Math.Abs(strong.Surface / baseline.Surface - 1.2f) < 0.05f && Math.Abs(strong.Run / baseline.Run - 1.2f) < 0.05f,
            Inv($"Strong swimmer at full rank swims 20 percent faster at the surface ({strong.Surface:0.000} m/s, with the run key {strong.Run:0.000})"));
        Report(baseline.Dive > 0.1f && Math.Abs(strong.Dive / baseline.Dive - 1.2f) < 0.06f, Inv($"and under it ({baseline.Dive:0.000} to {strong.Dive:0.000} m/s)"));
        Check(Math.Abs(quick.Surface - baseline.Surface) < 0.012f && Math.Abs(quick.Dive - baseline.Dive) < 0.02f, Inv($"Quick feet swims no faster ({quick.Surface:0.000} m/s): the water has its own knack"));
    }

    // ---- real physics: the climb ----

    private async Task TestClimbsAreWhatTheyAdd()
    {
        var origin = new Vector3(200, 0, 0);
        Floor(origin, 4, 4);
        // A vertical face 1.5 m tall, 0.3 m east of where the climber stands.
        var wall = new StaticBody3D { Position = origin + new Vector3(0.45f, 0.75f, 0), CollisionLayer = RoomBuilder.WorldLayer, CollisionMask = 0 };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.3f, 1.5f, 0.6f) } });
        AddChild(wall);
        await Frames(3);
        var results = new Dictionary<string, float>();
        foreach (var (name, ranks) in new (string, Dictionary<string, int>)[]
                 {
                     ("none", new()), ("big_bar:3", new() { ["big_bar"] = 3 }), ("steady_climber:3", new() { ["steady_climber"] = 3 }),
                     ("big_bar:3+steady_climber:3", new() { ["big_bar"] = 3, ["steady_climber"] = 3 }), ("quick_feet:3", new() { ["quick_feet"] = 3 }), ("high_hop:3", new() { ["high_hop"] = 3 }),
                 })
        {
            // The climber pays the kernel's own price for every centimetre (VitalsRules.SpendClimb), from a full starting bar.
            var rules = VitalsRules.For(_watered, "challenge", "standard", ranks);
            var vitals = rules.Fresh();
            var body = Wear(new Costume("prototype", null), origin + new Vector3(0, 0.003f, 0));
            body.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0);
            body.Knacks = BodyKnacks.FromPack(_table, ranks);
            body.ClimbBudget = (seconds, rise) =>
            {
                if (seconds == 0 && rise == 0) return vitals.Stamina > 0;
                vitals = rules.SpendClimb(vitals, seconds, rise, out var enough);
                return enough;
            };
            await Frames(8);
            body.SetControlInput(new Vector2(0, 1));
            var top = 0f;
            for (var i = 0; i < 1500; i++)
            {
                await Frames(1);
                if (body.IsClimbing) top = Mathf.Max(top, body.GlobalPosition.Y - origin.Y);
                if (body.GripFailures > 0 || (top > 0.1f && !body.IsClimbing && body.IsOnFloor())) break;
            }
            body.SetControlInput(Vector2.Zero);
            // The kernel's gate says when the bar is empty; the climb ended because it could not be paid for.
            var expected = rules.MaxStaminaOf(rules.Fresh()) * (float)(rules.ClimbMPerBarM * rules.ClimbFactor);
            results[name] = top;
            Report(Math.Abs(top - expected) < 0.025f && rules.ClimbMPerBarM == 1.5,
                Inv($"{name}: a full starting bar climbs {top:0.000} m of a vertical face before the grip fails (the vitals say {expected:0.000} m: bar {rules.MaxStaminaOf(rules.Fresh()):0.000} m x {rules.ClimbMPerBarM:0.00} a metre of bar x {rules.ClimbFactor:0.00})"));
            body.ClimbBudget = null;
            body.Knacks = BodyKnacks.None;
            body.QueueFree();
            await Frames(3);
        }
        Check(results["big_bar:3"] > results["none"] + 0.05f && results["steady_climber:3"] > results["none"] + 0.05f && results["big_bar:3+steady_climber:3"] > results["big_bar:3"] + 0.05f,
            "Big bar and Steady climber each climb higher on the same bar, and together higher still");
        Check(Math.Abs(results["quick_feet:3"] - results["none"]) < 0.02f && Math.Abs(results["high_hop:3"] - results["none"]) < 0.02f,
            "Quick feet and High hop climb no higher: climbing is paid for in the bar, not in speed");
    }

    // ---- real physics: the landing ----

    private async Task TestLandingsAreWhatTheyAdd()
    {
        var origin = new Vector3(300, 0, 0);
        Floor(origin, 6, 6);
        await Frames(3);
        // A fall from each height, with each safe drop: the landing speed the body reports, and what the vitals charge for it.
        var heights = new[] { 0.25f, 0.40f, 0.55f, 0.70f, 1.00f, 1.60f };
        var costs = new Dictionary<string, List<float>>();
        foreach (var (name, ranks) in new (string, Dictionary<string, int>)[] { ("none", new()), ("cat_feet:1", new() { ["cat_feet"] = 1 }), ("cat_feet:3", new() { ["cat_feet"] = 3 }) })
        {
            costs[name] = new List<float>();
            for (var i = 0; i < heights.Length; i++)
            {
                var body = Wear(new Costume("prototype", null), origin + new Vector3(0.4f * i - 1, heights[i], 0));
                body.Knacks = BodyKnacks.FromPack(_table, ranks);
                var landing = 0f;
                body.FallObserved = (speed, deep) => landing = Mathf.Max(landing, speed);
                for (var f = 0; f < 240 && landing == 0; f++) await Frames(1);
                var cost = body.LandingCost(landing, false);
                costs[name].Add(cost);
                // The landing is the same fall whatever the knack; only the cost differs, and exactly as the safe drop says.
                var free = MathF.Sqrt(2 * body.GravityMps2 * heights[i]);
                Check(Math.Abs(landing - free) < 0.2f, Inv($"{name}: a {heights[i]:0.00} m fall lands at {landing:0.00} m/s (free fall {free:0.00})"));
                var safe = body.Knacks.SafeDropM;
                Check((heights[i] <= safe - 0.04f) == (cost == 0) || Math.Abs(heights[i] - safe) < 0.05f, Inv($"{name}: a {heights[i]:0.00} m fall costs {cost:0.0} health with a {safe:0.00} m safe drop"));
                body.FallObserved = null;
                body.Knacks = BodyKnacks.None;
                body.QueueFree();
            }
            await Frames(3);
        }
        Check(costs["none"][1] > 0 && costs["cat_feet:1"][1] == 0 && costs["cat_feet:3"][3] == 0 && costs["none"][3] > 0 && costs["cat_feet:3"][5] > 0 && costs["cat_feet:3"][5] < costs["none"][5],
            "Cat feet makes the same fall cost less, none at all within the safe drop, and a big fall still hurts");
        Check(costs["none"].Zip(costs["cat_feet:1"], (a, c) => a >= c).All(x => x) && costs["cat_feet:1"].Zip(costs["cat_feet:3"], (a, c) => a >= c).All(x => x), "each rank costs no more than the one below it, at every height");
        GD.Print("BODY_KNACKS_MEASURED landing cost by fall height " + string.Join(" | ", costs.Select(kv => $"{kv.Key}: " + string.Join(", ", heights.Zip(kv.Value, (h, c) => Inv($"{h:0.00} m {c:0.0}"))))));

        // The tumble's three thresholds as heights: where the stumble starts, the flop and the knockout flop, for each safe drop.
        foreach (var drop in new[] { 0.30f, 0.45f, 0.60f, 0.75f })
        {
            var knacks = new BodyKnacks { SafeDropM = drop };
            float HeightFor(float health)
            {
                var speed = knacks.SafeLandingSpeedMps + health / PlayerVitals.FallDamagePerMps;
                return speed * speed / (2 * 3.5f);
            }
            var stumble = HeightFor(0.01f);
            var flop = HeightFor(TumbleTiers.TumbleHealth);
            var knock = HeightFor(TumbleTiers.KnockoutHealth);
            var tiers = new[] { TumbleTiers.Classify(MathF.Sqrt(2 * 3.5f * (stumble - 0.01f)), false, knacks), TumbleTiers.Classify(MathF.Sqrt(2 * 3.5f * (stumble + 0.01f)), false, knacks),
                TumbleTiers.Classify(MathF.Sqrt(2 * 3.5f * (flop + 0.01f)), false, knacks), TumbleTiers.Classify(MathF.Sqrt(2 * 3.5f * (knock + 0.01f)), false, knacks) };
            GD.Print(Inv($"BODY_KNACKS_MEASURED tumble heights with a {drop:0.00} m safe drop: stumble from {stumble:0.00} m, flop from {flop:0.00} m, knockout flop from {knock:0.00} m"));
            Check(tiers[0] == TumbleTier.None && tiers[1] == TumbleTier.Stumble && tiers[2] == TumbleTier.Tumble && tiers[3] == TumbleTier.Knockout,
                Inv($"the tiers fall at {stumble:0.00}, {flop:0.00} and {knock:0.00} m with a {drop:0.00} m safe drop: {string.Join(", ", tiers)}"));
        }
        var plain = new BodyKnacks();
        Check(Math.Abs(plain.SafeLandingSpeedMps - PlayerVitals.SafeLandingSpeedMps) < 1e-6f && TumbleTiers.TumbleHealth == 25 && TumbleTiers.KnockoutHealth == 100 && TumbleTiers.StumbleHealth == 0,
            "the three thresholds are the vitals' health points: past the safe drop, 25 and 100");
    }

    // ---- the real boot ----

    /// <summary>
    /// The game as it boots (res://scenes/room.tscn, headless): RoomWorld hands the command line's --knack= arguments to the host
    /// (CommandHost.UseKnacks), the host resolves them against the island's pack and hands the result to the body. The run must name a
    /// knack, so it cannot pass by having nothing to check.
    /// </summary>
    private async Task TestBootReachesTheBody()
    {
        var args = OS.GetCmdlineUserArgs();
        var ranks = BodyKnacks.FromArguments(args);
        Check(ranks.Count > 0, "the boot run names a knack (--knack=quick_feet:3): " + string.Join(" ", args));
        // This run's own saves, whoever launches it; the runner also points the user folder into the worktree's cache.
        var previousVitals = CommandHost.DefaultPlayerVitalsPath;
        var previousGlow = CommandHost.DefaultGlowProgressPath;
        var previousWishes = CommandHost.DefaultWishSaveRoot;
        var previousSaves = CommandHost.SaveRoot;
        CommandHost.SaveRoot = "user://tests/body_knacks/saves";
        CommandHost.DefaultPlayerVitalsPath = "user://tests/body_knacks/profile/player_vitals_v1.json";
        CommandHost.DefaultGlowProgressPath = "user://tests/body_knacks/profile/glow_progress_v1.json";
        CommandHost.DefaultWishSaveRoot = "user://tests/body_knacks/saves/wishes";
        try
        {
            var world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
            AddChild(world);
            for (var booting = System.Diagnostics.Stopwatch.StartNew(); booting.Elapsed.TotalSeconds < 60 && !world.WorldReady && world.LoadError.Length == 0;) await Frames(1);
            Check(world.WorldReady, "the room boots: " + world.LoadError);
            if (world.WorldReady)
            {
                var host = CommandHost.Of(world) ?? throw new InvalidOperationException("the room has no command host");
                var body = world.Player;
                var table = host.Rules?.Knacks;
                Check(table != null && table.Table.Count == 11, "the default pack carries the knacks table the boot's ranks are read against");
                Check(host.Knacks.Count == ranks.Count && ranks.All(kv => host.Knacks.TryGetValue(kv.Key, out var held) && held == kv.Value),
                    "the host holds the ranks the command line named: " + string.Join(", ", host.Knacks.Select(kv => $"{kv.Key}:{kv.Value}")));
                var expected = BodyKnacks.FromPack(table, ranks);
                Check(!expected.IsNone && body.Knacks == expected, $"the body wears them: walk x{body.Knacks.WalkRunFactor:0.00}, hop x{body.Knacks.HopFactor:0.00}, swim x{body.Knacks.SwimFactor:0.00}, safe drop {body.Knacks.SafeDropM:0.00} m");
                if (ranks.TryGetValue("quick_feet", out var quick) && table?.Knack("quick_feet") is { } row)
                {
                    var factor = (float)row.Ranks[Math.Min(quick, row.Ranks.Count) - 1];
                    await Frames(10);
                    Report(Mathf.IsEqualApprox(body.WalkSpeedNowMps, 0.32f * factor) && Mathf.IsEqualApprox(body.RunSpeedNowMps, 0.96f * factor) &&
                           Mathf.IsEqualApprox(body.LastBodyState.WalkSpeedMps, body.WalkSpeedNowMps) && Mathf.IsEqualApprox(body.LastBodyState.RunSpeedMps, body.RunSpeedNowMps),
                        Inv($"the booted body walks at {body.WalkSpeedNowMps:0.000} m/s and runs at {body.RunSpeedNowMps:0.000} (the base 0.320 and 0.960 times {factor}), and its state tells the gaits so"));
                }
                Check(world.Companion.Knacks.IsNone && world.Companion.WalkSpeedNowMps == world.Companion.WalkSpeedMps, "the Gubble wears none");
                // The same path at run time: the team's ranks change, the body follows; none again is today's body.
                host.UseKnacks(new Dictionary<string, int>());
                Check(body.Knacks.IsNone && body.WalkSpeedNowMps == body.WalkSpeedMps, "with no ranks the body is today's");
                host.UseKnacks(ranks);
                Check(body.Knacks == expected, "and with the ranks again, it wears them again");
                body.Knacks = BodyKnacks.None;
            }
            world.QueueFree();
            await Frames(3);
        }
        finally
        {
            CommandHost.SaveRoot = previousSaves;
            CommandHost.DefaultPlayerVitalsPath = previousVitals;
            CommandHost.DefaultGlowProgressPath = previousGlow;
            CommandHost.DefaultWishSaveRoot = previousWishes;
        }
    }

    // ---- the companion has no knacks ----

    private async Task TestGubbleHasNone()
    {
        var origin = new Vector3(400, 0, 0);
        Floor(origin, 4, 4);
        var gubble = new CompanionAvatar { Name = "KnackCompanion", Position = origin + new Vector3(0, 0.1f, 0) };
        gubble.ConfigureIdentity("knack_companion");
        AddChild(gubble);
        await Frames(5);
        var before = (gubble.WalkSpeedNowMps, gubble.RunSpeedNowMps, gubble.HopApexM, gubble.SwimSpeedNowMps);
        gubble.Knacks = BodyKnacks.FromPack(_table, new Dictionary<string, int> { ["quick_feet"] = 3, ["high_hop"] = 3, ["strong_swimmer"] = 3, ["cat_feet"] = 3 });
        Check(gubble.Floats && before == (gubble.WalkSpeedNowMps, gubble.RunSpeedNowMps, gubble.HopApexM, gubble.SwimSpeedNowMps) && gubble.WalkSpeedNowMps == gubble.WalkSpeedMps && gubble.RunSpeedNowMps == gubble.RunSpeedMps &&
              gubble.HopApexM == gubble.JumpApexM, "a floating body (the Gubble) takes no knack: its walk, run, hop and swim are its own");
        Check(gubble.ReachM == 0.15f && gubble.BodyHeightM == 0.10f && gubble.BodyRadiusM == 0.02f, "and its capsule and reach are the player's");
        // The margin the follow has to close a gap on a player with the strongest Quick feet: the Gubble's run against the player's best.
        var best = BodyKnacks.FromPack(_table, new Dictionary<string, int> { ["quick_feet"] = 3 });
        var player = 0.96f * best.WalkRunFactor;
        Report(Mathf.IsEqualApprox(CompanionAvatar.CompanionRunMps, 1.4f) && CompanionAvatar.CompanionRunMps / player > 1.2f,
            Inv($"the Gubble's run ({CompanionAvatar.CompanionRunMps:0.000} m/s) outpaces the player's with Quick feet at full rank ({player:0.000} m/s) by {(CompanionAvatar.CompanionRunMps / player - 1) * 100:0.0} percent, {CompanionAvatar.CompanionRunMps - player:0.000} m/s: follow closes a gap in a second or two"));
        gubble.QueueFree();
        await Frames(2);
    }
}
