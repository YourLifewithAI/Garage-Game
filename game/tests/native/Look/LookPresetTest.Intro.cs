using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GarageGame.Native;
using GarageGame.Native.Kernel;
using GarageGame.Native.Look;
using GarageGame.Native.Look.Intro;
using GarageGame.Native.Room;
using FileAccess = Godot.FileAccess;

namespace GarageGame.Tests.Look;

/// <summary>
/// The room-to-island intro (Look/Intro), headless: the source room read fail closed, the start field from synthetic source boxes,
/// the timeline's phases and order, the camera's one move (inside the room, over the island, clear of the ground, exactly the play
/// camera at the end), the rule for when it plays (never headless or for a boot check, the player's setting), the land shader's
/// guard and the project's globals at rest, and on the real garage island a whole world: what it holds while it plays (the HUD, the
/// player's input, the bodies, the play camera), every input swallowed, any key, click or gamepad button skipping it, and after a
/// skip at any moment, before its data is read or at its very end, the world exactly as a load without it.
/// </summary>
public partial class LookPresetTest
{
    private async Task CheckIntro(StylePreset preset)
    {
        CheckIntroSource();
        CheckIntroField();
        CheckIntroTimeline();
        CheckIntroCamera();
        CheckIntroRule();
        CheckIntroShaderAtRest();
        await CheckIntroInWorld(preset);
    }

    // ---------- the source room and the start field ----------

    private const string SyntheticBoxes =
        """
        {"id":"a","kind":"box","kind_confidence":1,"position_m":[3,0,3],"size_m":[2,1,2],"rotation":[0,0,0,1],"yaw_deg":0,"colours":[{"hex":"#ff0000","share":1}],"support":{"kind":"floor"}},
        {"id":"b","kind":"shelf","kind_confidence":1,"position_m":[7,0,5],"size_m":[2,2,1],"rotation":[0,0.70710678,0,0.70710678],"colours":[{"hex":"#00ff00","share":0.7},{"hex":"#ffffff","share":0.3}],"support":{"kind":"floor"}},
        {"id":"c","kind":"jar","kind_confidence":1,"position_m":[3,1,3],"size_m":[0.5,0.5,0.5],"yaw_deg":0,"colours":[{"hex":"#0000ff","share":1}],"support":{"kind":"object","target_id":"a","height_m":1}}
        """;

    /// <summary>A 10 x 8 m room, 4 m high, with a door, and the boxes given (the exporter's x_landscape_source shape).</summary>
    private static byte[] SyntheticManifest(string boxes, string extraBox = "")
    {
        var json = """
            {"schema":"garage_game.room","extensions":{"x_landscape_source":{"room_id":"synthetic","room_sha256":"0","inventory_sha256":"0",
            "bounds":{"min_m":[0,0,0],"max_m":[10,4,8]},
            "shell":{"parts":[
            {"id":"shell:ceiling","role":"ceiling","geometry":{"kind":"polygon","points_m":[[0,4,0],[10,4,0],[10,4,8],[0,4,8]],"thickness_m":0.1}},
            {"id":"shell:floor","role":"floor","geometry":{"kind":"polygon","points_m":[[0,0,0],[0,0,8],[10,0,8],[10,0,0]],"thickness_m":0.1}},
            {"id":"shell:wall_a","role":"wall","geometry":{"kind":"polygon","points_m":[[0,0,0],[10,0,0],[10,4,0],[0,4,0]],"thickness_m":0.1},"height_m":4},
            {"id":"shell:wall_b","role":"wall","geometry":{"kind":"polygon","points_m":[[10,0,0],[10,0,8],[10,4,8],[10,4,0]],"thickness_m":0.1},"height_m":4},
            {"id":"shell:wall_c","role":"wall","geometry":{"kind":"polygon","points_m":[[10,0,8],[0,0,8],[0,4,8],[10,4,8]],"thickness_m":0.1},"height_m":4},
            {"id":"shell:wall_d","role":"wall","geometry":{"kind":"polygon","points_m":[[0,0,8],[0,0,0],[0,4,0],[0,4,8]],"thickness_m":0.1},"height_m":4}],
            "openings":[{"id":"door","kind":"door","host_part_id":"shell:wall_a","center_m":[5,1,0],"size_m":[1,2]}]},
            "objects":[BOXES]}}}
            """;
        return Encoding.UTF8.GetBytes(json.Replace("BOXES", boxes + extraBox));
    }

    private void CheckIntroSource()
    {
        var source = IntroSource.FromManifest(SyntheticManifest(SyntheticBoxes))!;
        Check(source.Floors.Count == 1 && source.Walls.Count == 4 && source.Ceilings.Count == 1 && source.Openings.Count == 1 && source.Boxes.Count == 3,
            $"the source room reads its floor, four walls, ceiling, door and three boxes ({source.Floors.Count}, {source.Walls.Count}, {source.Ceilings.Count}, {source.Openings.Count}, {source.Boxes.Count})");
        var b = source.Boxes.First(x => x.Id == "b");
        Check(Mathf.IsEqualApprox(b.YawRad, Mathf.Pi / 2f, 1e-4f) && Mathf.IsEqualApprox(source.FloorY, 0f) && Mathf.IsEqualApprox(source.CeilingY, 4f),
            $"a box's heading comes from its rotation (90 degrees: {Mathf.RadToDeg(b.YawRad):0.##}); the floor at 0 and the ceiling at 4 m");
        Check(b.Covers(7f, 5.8f) && !b.Covers(7.9f, 5f), "a turned box covers its turned footprint, not its unturned one");
        Check(source.Boxes.First(x => x.Id == "a").Colour.IsEqualApprox(new Color("ff0000")) && b.Colour.IsEqualApprox(new Color("00ff00")),
            "each box takes its main colour (the largest share)");
        Check(IntroSource.FromManifest(Encoding.UTF8.GetBytes("""{"schema":"garage_game.room","extensions":{}}""")) == null
            && IntroSource.FromManifest(Encoding.UTF8.GetBytes("""{"schema":"garage_game.room"}""")) == null, "a room without a source has no intro (null, not an error)");
        // Fail closed: the manifest is untrusted data.
        void Refused(string boxes, string fragment, string label)
        {
            try { IntroSource.FromManifest(SyntheticManifest(boxes)); Check(false, label + " (no error)"); }
            catch (IntroSourceException error) { Check(error.Message.Contains(fragment, StringComparison.Ordinal), $"{label} ({error.Message})"); }
        }
        Refused(SyntheticBoxes.Replace("[3,0,3]", "[3e9,0,3]"), "finite within", "a coordinate past the room's limit is refused");
        Refused(SyntheticBoxes.Replace("#ff0000", "red"), "#rrggbb", "a colour that is not #rrggbb is refused");
        Refused(SyntheticBoxes.Replace("\"yaw_deg\":0,\"colours\":[{\"hex\":\"#0000ff\"", "\"colours\":[{\"hex\":\"#0000ff\""), "neither a rotation nor a yaw", "a box with no heading is refused");
        Refused(SyntheticBoxes.Replace("[0,0,0,1]", "[0,0,0,0.5]"), "unit quaternion", "a rotation that is not a unit quaternion is refused");
        Refused(SyntheticBoxes.Replace("[2,1,2]", "[2,0,2]"), "not positive", "a box with no height is refused");
        var many = string.Concat(Enumerable.Range(0, IntroSource.MaxBoxes - 2).Select(i => $$""",{"id":"m{{i}}","position_m":[1,0,1],"size_m":[0.1,0.1,0.1],"yaw_deg":0}"""));
        Refused(SyntheticBoxes + many, "more than", $"more than {IntroSource.MaxBoxes} boxes are refused");
        Check(IntroSource.FromManifest(SyntheticManifest(SyntheticBoxes, many[..many.LastIndexOf(",{", StringComparison.Ordinal)]))?.Boxes.Count == IntroSource.MaxBoxes,
            $"and exactly {IntroSource.MaxBoxes} are read");
    }

    private void CheckIntroField()
    {
        var source = IntroSource.FromManifest(SyntheticManifest(SyntheticBoxes))!;
        var area = new Rect2(-2, -2, 14, 12);
        var field = IntroField.Build(source, area);
        Check(field.Width >= 300 && field.Width <= IntroField.CellsAcross && field.Width * field.CellM >= area.Size.X && field.Depth * field.CellM >= area.Size.Y,
            $"the field covers the island in {field.Width} x {field.Depth} cells of {field.CellM:0.###} m");
        Check(Mathf.IsEqualApprox(field.StartAt(2.3f, 2.3f), 1f, 1e-3f), $"a box's footprint starts at its top (1 m: {field.StartAt(2.3f, 2.3f):0.###})");
        Check(Mathf.IsEqualApprox(field.StartAt(3f, 3f), 1.5f, 1e-3f), $"where boxes stack, the highest top (the jar on the box, 1.5 m: {field.StartAt(3f, 3f):0.###})");
        Check(Mathf.IsEqualApprox(field.StartAt(5f, 5f), 0f) && Mathf.IsEqualApprox(field.StartAt(-1f, -1f), 0f), "the floor, and the coast outside it, start at the floor's height");
        Check(Mathf.IsEqualApprox(field.StartAt(7f, 5.8f), 2f, 1e-3f) && Mathf.IsEqualApprox(field.StartAt(7.9f, 5f), 0f),
            $"a turned box rises over its turned footprint only ({field.StartAt(7f, 5.8f):0.###}, {field.StartAt(7.9f, 5f):0.###})");
        Check(Mathf.IsEqualApprox(field.TopY, 2f, 1e-4f), $"the field's top is the tallest box's ({field.TopY:0.###} m)");
        Check(field.ColourAt(2.3f, 2.3f).IsEqualApprox(new Color("ff0000").SrgbToLinear()) && field.ColourAt(3f, 3f).IsEqualApprox(new Color("0000ff").SrgbToLinear())
            && field.ColourAt(5f, 5f).IsEqualApprox(IntroSource.DefaultFloorColour.SrgbToLinear()), "each cell has the room's colour there, linear: a box's (the top one's) or the floor's");
        Check(field.DelayAt(3f, 3f) <= IntroField.DelayJitter + 1e-3f && field.DelayAt(7f, 5f) <= IntroField.DelayJitter + 1e-3f && field.DelayAt(9.6f, 0.4f) > 0.6f,
            $"the boxes rise first and the ground far from them last (delays {field.DelayAt(3f, 3f):0.##}, {field.DelayAt(7f, 5f):0.##}, {field.DelayAt(9.6f, 0.4f):0.##})");
        Check(Mathf.IsEqualApprox(field.CoastAt(-1f, -1f), 1f) && Mathf.IsEqualApprox(field.CoastAt(5f, 4f), 0f) && field.CoastAt(0.1f, 4f) > 0.5f,
            $"outside the floor is coast, its middle is not, and its edge mostly is ({field.CoastAt(-1f, -1f):0.##}, {field.CoastAt(5f, 4f):0.##}, {field.CoastAt(0.1f, 4f):0.##})");
        var area4 = field.Area;
        Check(Mathf.IsEqualApprox(area4.X, -2f) && Mathf.IsEqualApprox(area4.Y, -2f) && Mathf.IsEqualApprox(area4.Z * field.Width * field.CellM, 1f) && Mathf.IsEqualApprox(area4.W * field.Depth * field.CellM, 1f),
            "the shader's intro_area maps the field's square to the texture");
        var image = field.StartImage();
        var colours = field.ColourImage();
        var (i, j) = field.CellOf(3f, 3f);
        var texel = image.GetPixel(i, j);
        Check(image.GetWidth() == field.Width && image.GetHeight() == field.Depth && image.GetFormat() == Image.Format.Rgbah && colours.GetFormat() == Image.Format.Rgbah
            && Mathf.IsEqualApprox(texel.R, 1.5f, 2e-3f) && Mathf.IsEqualApprox(texel.G, field.Delay[j * field.Width + i], 2e-3f) && Mathf.IsEqualApprox(texel.B, field.Coast[j * field.Width + i], 2e-3f)
            && colours.GetPixel(i, j).IsEqualApprox(field.Colour[j * field.Width + i]),
            $"the GPU's textures hold the field in half floats: start height, delay and coast; the colours ({texel})");
        var again = IntroField.Build(source, area);
        Check(again.Start.SequenceEqual(field.Start) && again.Delay.SequenceEqual(field.Delay) && again.Coast.SequenceEqual(field.Coast), "the field is the same every time it is built");
        var empty = IntroField.Build(IntroSource.FromManifest(SyntheticManifest(""))!, area);
        Check(empty.Start.All(h => h == 0f) && Mathf.IsEqualApprox(empty.TopY, 0f), "a room with no boxes starts flat");
    }

    // ---------- the story and the camera ----------

    private void CheckIntroTimeline()
    {
        var phases = IntroTimeline.Phases.Select(p => p.Name).ToArray();
        Check(phases.SequenceEqual(new[] { "room", "sky", "land", "coast", "water", "plants", "arrive" }) && IntroTimeline.Phases.All(p => p.Start < p.End)
            && IntroTimeline.Phases.Zip(IntroTimeline.Phases.Skip(1)).All(pair => pair.First.Start < pair.Second.Start && pair.Second.Start <= pair.First.End),
            "the story's phases in order, each overlapping the next: room, sky, land, coast, water, plants, arrive");
        Check(Math.Abs(IntroTimeline.DurationS - 15.0) < 1e-9 && IntroTimeline.Phases[^1].End == IntroTimeline.DurationS, "about 15 seconds, ending with the arrival");
        var expected = new[] { (0.5, "room"), (3.0, "sky"), (5.0, "land"), (7.0, "coast"), (9.5, "water"), (11.0, "plants"), (14.0, "arrive"), (15.0, "done") };
        Check(expected.All(e => IntroTimeline.PhaseAt(e.Item1) == e.Item2), "the phase at each moment: " + string.Join(", ", expected.Select(e => $"{e.Item1} s {IntroTimeline.PhaseAt(e.Item1)}")));
        var start = IntroTimeline.At(0);
        Check(start.Cover == 1f && start.Land == 0f && start.Coast == 0f && start.Ceiling == 0f && start.Slump == 0f && start.Sea == 0f && start.Grow == 0f && !start.BodiesShown && !start.LifeShown && !start.Done,
            "at the start: black over a whole room, nothing risen, no sea, nothing grown, no bodies");
        var end = IntroTimeline.At(IntroTimeline.DurationS);
        Check(end.Cover == 0f && end.Land == 1f && end.Coast == 1f && end.Ceiling == 1f && end.Slump == 1f && end.WallFray == 1f && end.Sea == 1f && end.Grow == 1f && end.Prompt == 0f
            && end.BodiesShown && end.LifeShown && end.Done, "at the end: everything is the island's (the end state is a normal load's)");
        var previous = start;
        var monotonic = true;
        for (var t = 0.0; t <= IntroTimeline.DurationS + 1e-9; t += 1.0 / 60.0)
        {
            var now = IntroTimeline.At(t);
            monotonic &= now.Land >= previous.Land && now.Coast >= previous.Coast && now.Ceiling >= previous.Ceiling && now.Slump >= previous.Slump
                && now.WallFray >= previous.WallFray && now.Sea >= previous.Sea && now.Grow >= previous.Grow && now.Cover <= previous.Cover;
            previous = now;
        }
        Check(monotonic, "every part only moves toward the island, frame after frame");
        double When(Func<IntroMoment, bool> reached)
        {
            for (var t = 0.0; t <= IntroTimeline.DurationS; t += 0.01) if (reached(IntroTimeline.At(t))) return t;
            return double.PositiveInfinity;
        }
        var ceilingGone = When(m => m.Ceiling >= 1f);
        var landStarts = When(m => m.Land > 0f);
        var landDone = When(m => m.Land >= 1f);
        var wallsStart = When(m => m.Slump > 0f);
        var seaStarts = When(m => m.Sea > 0f);
        var seaFull = When(m => m.Sea >= 1f);
        var wallsGone = When(m => m.WallFray >= 1f);
        var plantsStart = When(m => m.Grow > 0f);
        var bodies = When(m => m.BodiesShown);
        Check(landStarts < ceilingGone && ceilingGone < landDone && landStarts < wallsStart && wallsStart < seaStarts && seaStarts < wallsGone && wallsGone < seaFull
            && plantsStart < seaFull && plantsStart > wallsGone && bodies > plantsStart && bodies < IntroTimeline.DurationS,
            $"the order: the ceiling dissolves as the boxes start to rise ({landStarts:0.0}-{ceilingGone:0.0} s), the walls slump while the sea rises ({wallsStart:0.0}, {seaStarts:0.0} s), the walls are gone before the water is full ({wallsGone:0.0}, {seaFull:0.0} s), then the plants ({plantsStart:0.0} s), then the bodies ({bodies:0.0} s)");
    }

    private void CheckIntroCamera()
    {
        var room = new Aabb(new Vector3(-30, 0, -35), new Vector3(60, 27, 70));
        var spawn = new Vector3(0, 0.9f, -30);
        var end = new Transform3D(Basis.FromEuler(new Vector3(-0.18f, 0, 0)), spawn + new Vector3(0.022f, 0.08f, 0.32f));
        float Start(float x, float z) => x is > -5 and < 5 && z is > 20 and < 30 ? 8f : 0f;
        // A ridge across the way down, behind the player (the play camera's back is +Z here).
        float Ground(float x, float z) => z is > -28 and < -12 ? 6f : 0f;
        var path = new IntroCameraPath(room, spawn, end, 21f, Start, Ground);
        var first = path.Pose(0, end, 68f).Transform.Origin;
        Check(room.HasPoint(first) && first.Y > Start(first.X, first.Z) + 0.1f * room.Size.Y && first.Y < room.End.Y,
            $"the camera starts inside the room, between the boxes and the ceiling ({first})");
        var overview = path.Pose(IntroTimeline.Window("land").End, end, 68f).Transform.Origin;
        Check(overview.Y > 21f + 0.5f * room.Size.Y, $"the overview is well above the island's highest land ({overview.Y:0.#} m)");
        var last = path.Pose(IntroTimeline.DurationS, end, 68f);
        Check(last.Transform.IsEqualApprox(end) && Mathf.IsEqualApprox(last.FovDeg, 68f) && Mathf.IsEqualApprox(last.NearM, 0.005f),
            "it ends exactly as the play camera: its transform, field of view and near plane");
        var previous = first;
        var biggest = 0f;
        var clear = true;
        var gliding = float.PositiveInfinity;
        var glides = true;
        for (var t = 1.0 / 60.0; t <= IntroTimeline.DurationS; t += 1.0 / 60.0)
        {
            var pose = path.Pose(t, end, 68f);
            var at = pose.Transform.Origin;
            biggest = Math.Max(biggest, at.DistanceTo(previous));
            previous = at;
            if (t > IntroTimeline.Window("land").End && t < IntroTimeline.DurationS - 0.05) clear &= at.Y >= Ground(at.X, at.Z) + 0.05f;
            if (t >= IntroCameraPath.GlideFromS)
            {
                var distance = at.DistanceTo(end.Origin);
                glides &= distance <= gliding + 1e-4f;
                gliding = distance;
            }
            if (!pose.Transform.Origin.IsFinite() || pose.NearM < 0.005f - 1e-6f) clear = false;
        }
        Check(biggest < 0.04f * 70f, $"one continuous move: never more than {biggest:0.##} m in a frame at 60 fps");
        Check(clear, "from the overview on the camera stays over the ground, the ridge across the way down too");
        Check(glides, "the glide into the play camera only ever gets nearer: no overshoot");
        var plain = new IntroCameraPath(room, spawn, end, 21f, Start);
        Check(plain.Keys.Count == path.Keys.Count && path.Keys[^1].Position.Y > plain.Keys[^1].Position.Y, "the ridge lifts the way down");
    }

    private void CheckIntroRule()
    {
        var none = Array.Empty<string>();
        Check(RoomIntro.WhyNot(true, none, true, true, false) == "" && RoomIntro.WhyNot(false, new[] { "--quit-after-ready=240" }, true, true, false) == "",
            "never headless or for a boot check (quietly: ROOM_WORLD_READY and its quit line are what those runs read)");
        Check(RoomIntro.WhyNot(false, none, false, true, false)?.Contains("Play the intro", StringComparison.Ordinal) == true, "never with the player's setting off");
        Check(RoomIntro.WhyNot(false, none, true, false, false) != null && RoomIntro.WhyNot(false, none, true, true, true) != null, "never on a room that is not a landscape, nor twice at once");
        Check(RoomIntro.WhyNot(false, none, true, true, false) == null, "otherwise on every load");
        Check(RoomIntro.IsSkip(new InputEventKey { Pressed = true, PhysicalKeycode = Key.W }) && !RoomIntro.IsSkip(new InputEventKey { Pressed = true, Echo = true, PhysicalKeycode = Key.W })
            && !RoomIntro.IsSkip(new InputEventKey { Pressed = false, PhysicalKeycode = Key.W }) && RoomIntro.IsSkip(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Left })
            && RoomIntro.IsSkip(new InputEventJoypadButton { Pressed = true, ButtonIndex = JoyButton.A }) && !RoomIntro.IsSkip(new InputEventMouseMotion { Relative = new Vector2(5, 0) })
            && !RoomIntro.IsSkip(new InputEventJoypadMotion { Axis = JoyAxis.LeftX, AxisValue = 1f }),
            "any key, a click or a gamepad button skips; a held key's repeat, a release, the mouse moving or a stick does not");
        const string profile = "user://tests/look_intro/setting/avatar_profile_v1.cfg";
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(profile.GetBaseDir()));
        DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(profile));
        var missing = RoomHud.LoadPlayIntro(profile);
        void Write(string text) { using var file = FileAccess.Open(profile, FileAccess.ModeFlags.Write); file.StoreString(text); }
        Write("[profile]\nversion=1\nplay_intro=false\n");
        var off = RoomHud.LoadPlayIntro(profile);
        Write("[profile]\nversion=1\nplay_intro=true\n");
        var on = RoomHud.LoadPlayIntro(profile);
        Write("not a config file {");
        var broken = RoomHud.LoadPlayIntro(profile);
        DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(profile));
        Check(missing && !off && on && broken, "\"Play the intro\" is on unless the profile says it is off (a missing or unreadable profile plays it)");
    }

    /// <summary>At rest the land draws exactly as without the intro: the project's globals rest where the shader's guard skips it all.</summary>
    private void CheckIntroShaderAtRest()
    {
        var code = GD.Load<Shader>(LandscapeLook.ShaderPath).Code.Replace("\r\n", "\n");
        Check(new[] { "global uniform vec4 intro_rise;", "global uniform vec4 intro_area;", "global uniform vec4 intro_grow;", "global uniform sampler2D intro_start;", "global uniform sampler2D intro_colour;", "instance uniform float intro_part = 0.0;" }
            .All(code.Contains), "the land shader reads the intro's five globals and its one instance mark");
        Check(code.Contains("intro_room = vec4(0.0);\n\tif (intro_rise.w > 0.5) {", StringComparison.Ordinal) && code.Contains("color = mix(color, intro_room.rgb, intro_room.a);", StringComparison.Ordinal),
            "its guard: at rest (intro_rise.w 0) no vertex moves and the colour mixes in nothing");
        Variant Global(string name) => ProjectSettings.GetSetting("shader_globals/" + name);
        var rise = Global("intro_rise");
        Check(rise.VariantType == Variant.Type.Dictionary && rise.AsGodotDictionary()["type"].AsString() == "vec4" && rise.AsGodotDictionary()["value"].AsVector4() == RoomIntro.RiseAtRest && RoomIntro.RiseAtRest.W == 0f,
            $"game/project.godot declares intro_rise at rest (w 0: off), as RoomIntro rests it: {rise}");
        Check(new[] { ("intro_area", "vec4"), ("intro_grow", "vec4"), ("intro_start", "sampler2D"), ("intro_colour", "sampler2D") }
            .All(g => Global(g.Item1).VariantType == Variant.Type.Dictionary && Global(g.Item1).AsGodotDictionary()["type"].AsString() == g.Item2), "and the other four globals");
        var shell = GD.Load<Shader>(IntroShell.ShaderPath);
        Check(shell != null && shell.Code.Contains("discard", StringComparison.Ordinal), "the intro's shell shader loads");
    }

    // ---------- a whole world ----------

    private const string IntroSaves = "user://tests/look_intro/saves";
    private const string IntroProfile = "user://tests/look_intro/profile/avatar_profile_v1.cfg";

    private async Task CheckIntroInWorld(StylePreset preset)
    {
        var directory = FindLandscapeFixture();
        if (directory == null) { GD.Print("LOOK_INFO: no garage landscape (-- --landscape=DIR); the intro's world checks were skipped"); return; }
        var (saveRoot, glow, vitals, wishes, profile) = (CommandHost.SaveRoot, CommandHost.DefaultGlowProgressPath, CommandHost.DefaultPlayerVitalsPath, CommandHost.DefaultWishSaveRoot, RoomHud.ProfilePath);
        RemoveTree(ProjectSettings.GlobalizePath("user://tests/look_intro"));
        CommandHost.SaveRoot = IntroSaves;
        CommandHost.DefaultGlowProgressPath = IntroSaves + "/glow.json";
        CommandHost.DefaultPlayerVitalsPath = IntroSaves + "/vitals.json";
        CommandHost.DefaultWishSaveRoot = IntroSaves + "/wishes";
        RoomHud.ProfilePath = IntroProfile;
        RoomWorld? world = null;
        try
        {
            world = GD.Load<PackedScene>("res://scenes/room.tscn").Instantiate<RoomWorld>();
            world.RoomDirectory = directory;
            AddChild(world);
            for (var boot = System.Diagnostics.Stopwatch.StartNew(); boot.Elapsed.TotalSeconds < 90 && !world.WorldReady && world.LoadError.Length == 0;) await Frames(1);
            Check(world.WorldReady, "the garage island boots: " + world.LoadError);
            if (!world.WorldReady) return;
            Check(RoomIntro.Of(world) == null && RoomIntro.WhyNot(world, IntroProfile) == "", "headless, the load plays no intro and says nothing (ROOM_WORLD_READY is not later for it)");
            await Frames(30);
            var hud = world.GetNode<RoomHud>("RoomHud");
            var host = CommandHost.Of(world)!;
            var normal = IntroSnapshot(world);
            var revision = host.Revision;
            var playerAt = world.Player.GlobalPosition;
            var playCamera = GetViewport().GetCamera3D();

            // Playing: what it holds.
            var intro = await StartIntro(world);
            Check(intro.Playing && intro.LandMarked > 0 && intro.StillMarked > 0 && intro.WaterMoved > 0 && intro.Shell!.Walls.Count == 4 && intro.Shell.Ceilings.Count == 1 && intro.Source!.Boxes.Count >= 10,
                $"it reads the garage's source and marks its island: {intro.LandMarked} land meshes, {intro.StillMarked} still, {intro.WaterMoved} water meshes, {intro.Shell?.Walls.Count} walls, {intro.Source?.Boxes.Count} boxes");
            Check(hud.IntroPlaying && !hud.Visible && hud.SkipPrompt?.Text == RoomHud.SkipPromptText && !world.Player.InputEnabled && !world.Player.Visible && !world.Companion.Visible
                && intro.Camera!.Current && RoomIntro.GlobalsPlaying && world.Look.View == LookView.Shoulder,
                "while it plays: the HUD hidden but its skip prompt, the player's input off, the bodies out of sight, its own camera current, the globals held");
            var land = world.Built.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().First(m => m.HasMeta(RoomBuilder.TerrainLevelMeta));
            var copiesView = world.Built.GetNodeOrNull<Node3D>(PlantCopiesView.ViewName);
            // (A headless renderer keeps no instance parameters to read back: the marks are counted above and seen in the GPU captures.)
            Check(land.ExtraCullMargin > 0f && copiesView is { Visible: false }, "the land's cull margins are grown for the room's heights, and the copies wait for their phase");
            foreach (var (seconds, phase) in new[] { (0.5, "room"), (3.0, "sky"), (6.5, "coast"), (11.0, "plants"), (14.0, "arrive") })
            {
                intro.Seek(seconds);
                await Frames(2);
                Check(intro.Moment.Phase == phase && !intro.Finished, $"at {seconds} s it is in its {phase} phase ({intro.Moment.Phase})");
            }
            Check(copiesView is { Visible: true } && world.Player.Visible && world.Companion.Visible, "by the arrival the copies grow and the bodies are back in sight");
            // Every input is the intro's: the mouse moving neither skips nor turns the player.
            var facing = world.Player.GlobalRotation;
            GetViewport().PushInput(new InputEventMouseMotion { Relative = new Vector2(120, 40) });
            await Frames(2);
            Check(!intro.Finished && world.Player.GlobalRotation.IsEqualApprox(facing), "the mouse moving neither skips it nor turns the player");
            // A key skips it, and the HUD never sees that key (H would unfold the key help).
            var help = KeyHelpOpen(hud);
            GetViewport().PushInput(new InputEventKey { PhysicalKeycode = Key.H, Pressed = true });
            await Frames(3);
            GetViewport().PushInput(new InputEventKey { PhysicalKeycode = Key.H, Pressed = false });
            await Frames(2);
            Check(intro.Finished && intro.EndReason == "skipped" && KeyHelpOpen(hud) == help, "a key skips it, and the HUD never sees that key");
            CompareIntroSnapshot(normal, IntroSnapshot(world), "after a key at the arrival, the world is exactly as a load without the intro");
            Check(GetViewport().GetCamera3D() == playCamera && world.Player.InputEnabled && hud.Visible && !hud.IntroPlaying && hud.SkipPrompt == null && !RoomIntro.GlobalsPlaying,
                "the play camera, the player's input, the HUD and the globals are back");
            GetViewport().PushInput(new InputEventKey { PhysicalKeycode = Key.H, Pressed = true });
            await Frames(2);
            Check(KeyHelpOpen(hud) != help, "and the same key without the intro reaches the HUD (the control)");
            GetViewport().PushInput(new InputEventKey { PhysicalKeycode = Key.H, Pressed = true });
            await Frames(2);

            // Skipped at other moments, by other inputs, and on the big island's coarse land.
            var skips = new (double Seconds, InputEvent Input, string How)[]
            {
                (0.0, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true }, "a click in the room"),
                (4.0, new InputEventJoypadButton { ButtonIndex = JoyButton.A, Pressed = true }, "a gamepad button as the land rises"),
                (8.5, new InputEventKey { PhysicalKeycode = Key.Escape, Pressed = true }, "Esc as the walls slump"),
                (10.5, new InputEventKey { PhysicalKeycode = Key.W, Pressed = true }, "W as the water fills"),
                (12.4, new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true }, "a right click as the plants grow"),
            };
            for (var k = 0; k < skips.Length; k++)
            {
                RoomIntro.ForceCoarseLand = k % 2 == 0;
                var again = await StartIntro(world);
                var coarse = again.CoarseHeld;
                again.Seek(skips[k].Seconds);
                await Frames(2);
                GetViewport().PushInput(skips[k].Input);
                await Frames(3);
                if (skips[k].Input is InputEventKey key) GetViewport().PushInput(new InputEventKey { PhysicalKeycode = key.PhysicalKeycode, Pressed = false });
                if (skips[k].Input is InputEventMouseButton button) GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = button.ButtonIndex, Pressed = false });
                if (skips[k].Input is InputEventJoypadButton pad) GetViewport().PushInput(new InputEventJoypadButton { ButtonIndex = pad.ButtonIndex, Pressed = false });
                await Frames(2);
                Check(again.Finished && again.EndReason == "skipped" && (k % 2 == 1 || coarse > 0), $"{skips[k].How} skips it ({again.EndReason}; {coarse} land meshes held coarse)");
                CompareIntroSnapshot(normal, IntroSnapshot(world), $"after {skips[k].How}, the world is exactly as a load without the intro");
            }
            RoomIntro.ForceCoarseLand = null;
            // Skipped before its data is even read: the black cover goes and nothing was marked.
            var early = RoomIntro.Start(world);
            GetViewport().PushInput(new InputEventKey { PhysicalKeycode = Key.Space, Pressed = true });
            await Frames(3);
            GetViewport().PushInput(new InputEventKey { PhysicalKeycode = Key.Space, Pressed = false });
            await Frames(2);
            Check(early.Finished && !early.Playing && early.EndReason == "skipped", "a key while it still reads its data skips it too");
            CompareIntroSnapshot(normal, IntroSnapshot(world), "and the world is exactly as a load without the intro");
            // Played to its end.
            var whole = await StartIntro(world);
            whole.Seek(IntroTimeline.DurationS - 0.2);
            whole.Paused = false;
            for (var f = 0; f < 60 && !whole.Finished; f++) await Frames(1);
            await Frames(2);
            Check(whole.Finished && whole.EndReason == "played" && GetViewport().GetCamera3D() == playCamera, "played to its end it hands over to the play camera itself");
            CompareIntroSnapshot(normal, IntroSnapshot(world), "and at its end the world is exactly as a load without the intro");
            Check(host.Revision == revision && world.Player.GlobalPosition.DistanceTo(playerAt) < 0.01f,
                $"the world itself never changed: the host's revision {host.Revision} (was {revision}), the player {world.Player.GlobalPosition.DistanceTo(playerAt) * 1000f:0.#} mm from where it stood");

            // The player's setting, in the appearance panel.
            Check(hud.IntroToggle is { ButtonPressed: true } toggle && toggle.GetParent() != null, "\"Play the intro\" is in the appearance panel, on by default");
            hud.SetPlayIntro(false);
            var savedOff = RoomHud.LoadPlayIntro(IntroProfile);
            var refused = RoomIntro.WhyNot(false, Array.Empty<string>(), RoomHud.LoadPlayIntro(IntroProfile), true, false);
            hud.SetPlayIntro(true);
            Check(!savedOff && refused != null && RoomHud.LoadPlayIntro(IntroProfile) && hud.IntroToggle!.ButtonPressed, "turning it off is kept in the profile and stops the next load's intro; on again plays it");
        }
        finally
        {
            RoomIntro.ForceCoarseLand = null;
            if (world != null && IsInstanceValid(world)) world.QueueFree();
            await Frames(3);
            CommandHost.SaveRoot = saveRoot;
            CommandHost.DefaultGlowProgressPath = glow;
            CommandHost.DefaultPlayerVitalsPath = vitals;
            CommandHost.DefaultWishSaveRoot = wishes;
            RoomHud.ProfilePath = profile;
            RemoveTree(ProjectSettings.GlobalizePath("user://tests/look_intro"));
            MaterialLibrary.Configure(preset);
        }
    }

    private async Task<RoomIntro> StartIntro(RoomWorld world)
    {
        var intro = RoomIntro.Start(world);
        intro.Paused = true;
        for (var f = 0; f < 600 && !intro.Playing && !intro.Finished; f++) await Frames(1);
        return intro;
    }

    private static bool KeyHelpOpen(RoomHud hud) => hud.GetNode<PanelContainer>("HelpFooter").GetChild<VBoxContainer>(0).GetNode<VBoxContainer>("KeyHelp").Visible;

    /// <summary>
    /// Everything the intro could leave changed, as text: every drawn mesh of the room (but the copies' streaming tiles) with its
    /// visibility, transform, cull margin, intro mark, visibility ranges, shadows and materials; the bodies', fish's and copies'
    /// visibility; the current camera; the HUD; the player's input; the look's view and focus; the globals; any intro left behind.
    /// </summary>
    private static string IntroSnapshot(RoomWorld world)
    {
        static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
        static string T(Transform3D t) => string.Join(",", new[] { t.Basis.X, t.Basis.Y, t.Basis.Z, t.Origin }.SelectMany(v => new[] { v.X, v.Y, v.Z }).Select(F));
        var lines = new List<string>();
        var copies = world.Built.GetNodeOrNull<Node3D>(PlantCopiesView.ViewName);
        foreach (var node in world.Built.FindChildren("*", "GeometryInstance3D", true, false).OfType<GeometryInstance3D>())
        {
            if (copies != null && copies.IsAncestorOf(node)) continue;
            var mark = node.GetInstanceShaderParameter("intro_part");
            var line = new StringBuilder($"{world.GetPathTo(node)} v={node.Visible} t={T(node.Transform)} m={F(node.ExtraCullMargin)} part={(mark.VariantType == Variant.Type.Nil ? 0f : mark.AsSingle())} " +
                $"vr={F(node.VisibilityRangeBegin)},{F(node.VisibilityRangeBeginMargin)},{F(node.VisibilityRangeEnd)},{F(node.VisibilityRangeEndMargin)} shadow={node.CastShadow} mo={node.MaterialOverride?.GetInstanceId()}");
            if (node is MeshInstance3D { Mesh: { } mesh } instance)
                for (var s = 0; s < mesh.GetSurfaceCount(); s++) line.Append($" s{s}={instance.GetSurfaceOverrideMaterial(s)?.GetInstanceId()}");
            lines.Add(line.ToString());
        }
        lines.Add($"copies visible={copies?.Visible}");
        foreach (var node in new Node3D?[] { world.Player, world.Companion, world.Look?.OpenSea?.Life }.Concat(world.GetChildren().OfType<GarageGame.Native.Look.Fauna.PondLife>().Cast<Node3D?>()))
            if (node != null) lines.Add($"{node.Name} visible={node.Visible}");
        var hud = world.GetNode<RoomHud>("RoomHud");
        lines.Add($"camera={world.GetViewport().GetCamera3D()?.Name} hud visible={hud.Visible} intro={hud.IntroPlaying} children={hud.GetChildCount()} view={hud.ViewMode} input={world.Player.InputEnabled}");
        lines.Add($"look view={world.Look?.View} focus={world.Look?.FocusOverride} globals={RoomIntro.GlobalsPlaying} intro={RoomIntro.Of(world) != null} node={world.GetNodeOrNull(RoomIntro.NodeName) != null}");
        return string.Join("\n", lines);
    }

    private void CompareIntroSnapshot(string expected, string actual, string label)
    {
        if (expected == actual) { Check(true, label); return; }
        var a = expected.Split('\n');
        var b = actual.Split('\n');
        var diff = a.Except(b).Take(3).Select(l => "was " + l).Concat(b.Except(a).Take(3).Select(l => "now " + l));
        Check(false, $"{label}: {a.Length} lines before, {b.Length} after; " + string.Join(" | ", diff));
    }

    private static void RemoveTree(string path)
    {
        if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);
    }
}
